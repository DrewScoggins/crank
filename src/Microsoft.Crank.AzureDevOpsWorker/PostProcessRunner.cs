// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Crank.AzureDevOpsWorker
{
    internal enum PostProcessStatus
    {
        Succeeded,
        Failed,
        TimedOut,
        Canceled
    }

    internal sealed class PostProcessResult
    {
        public PostProcessResult(PostProcessStatus status)
        {
            Status = status;
        }

        public PostProcessStatus Status { get; }

        public bool Succeeded => Status == PostProcessStatus.Succeeded;

        public bool Canceled => Status == PostProcessStatus.Canceled;
    }

    internal sealed class PostProcessRunner
    {
        private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
        private readonly TimeSpan _pollInterval;

        public PostProcessRunner(TimeSpan? pollInterval = null)
        {
            _pollInterval = pollInterval ?? DefaultPollInterval;
        }

        public async Task<PostProcessResult> RunAsync(
            string executablePath,
            PostProcessPayload postProcess,
            string workingDirectory,
            TimeSpan timeout,
            Func<IReadOnlyCollection<string>, Task> writeLogsAsync,
            Func<CancellationToken, Task<bool>> isTaskCanceledAsync,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(postProcess);
            ArgumentNullException.ThrowIfNull(writeLogsAsync);

            var displayName = postProcess.GetSafeDisplayName();

            if (String.IsNullOrWhiteSpace(executablePath))
            {
                await WriteLogAsync(
                    writeLogsAsync,
                    $"Post-process '{displayName}' was requested, but no post-process executable is configured.");

                return new PostProcessResult(PostProcessStatus.Failed);
            }

            if (timeout <= TimeSpan.Zero)
            {
                await WriteLogAsync(
                    writeLogsAsync,
                    $"Post-process '{displayName}' cannot start because its worker timeout is invalid.");

                return new PostProcessResult(PostProcessStatus.Failed);
            }

            if (cancellationToken.IsCancellationRequested ||
                (isTaskCanceledAsync != null && await isTaskCanceledAsync(CancellationToken.None)))
            {
                await WriteLogAsync(writeLogsAsync, $"Post-process '{displayName}' was canceled before it started.");
                return new PostProcessResult(PostProcessStatus.Canceled);
            }

            var pendingLogs = new ConcurrentQueue<string>();

            using var process = new Process
            {
                StartInfo = CreateStartInfo(executablePath, postProcess.Args, workingDirectory)
            };

            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data != null)
                {
                    pendingLogs.Enqueue(eventArgs.Data);
                }
            };

            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data != null)
                {
                    pendingLogs.Enqueue(eventArgs.Data);
                }
            };

            await WriteLogAsync(writeLogsAsync, $"Starting post-process '{displayName}'.");

            try
            {
                if (!process.Start())
                {
                    await WriteLogAsync(writeLogsAsync, $"Post-process '{displayName}' failed to start.");
                    return new PostProcessResult(PostProcessStatus.Failed);
                }
            }
            catch (Exception exception)
            {
                await WriteLogAsync(
                    writeLogsAsync,
                    $"Post-process '{displayName}' failed to start ({exception.GetType().Name}).");

                return new PostProcessResult(PostProcessStatus.Failed);
            }

            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var stopwatch = Stopwatch.StartNew();
                var terminationStatus = default(PostProcessStatus?);

                while (!process.HasExited)
                {
                    await FlushLogsAsync(pendingLogs, writeLogsAsync);

                    if (cancellationToken.IsCancellationRequested ||
                        (isTaskCanceledAsync != null && await isTaskCanceledAsync(CancellationToken.None)))
                    {
                        terminationStatus = PostProcessStatus.Canceled;
                        break;
                    }

                    if (stopwatch.Elapsed >= timeout)
                    {
                        terminationStatus = PostProcessStatus.TimedOut;
                        break;
                    }

                    try
                    {
                        await Task.Delay(_pollInterval, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        terminationStatus = PostProcessStatus.Canceled;
                        break;
                    }
                }

                if (terminationStatus.HasValue)
                {
                    TerminateProcess(process);
                }

                await WaitForExitAsync(process);
                await FlushLogsAsync(pendingLogs, writeLogsAsync);

                if (terminationStatus == PostProcessStatus.Canceled)
                {
                    await WriteLogAsync(writeLogsAsync, $"Post-process '{displayName}' was canceled.");
                    return new PostProcessResult(PostProcessStatus.Canceled);
                }

                if (terminationStatus == PostProcessStatus.TimedOut)
                {
                    await WriteLogAsync(writeLogsAsync, $"Post-process '{displayName}' timed out after {timeout}.");
                    return new PostProcessResult(PostProcessStatus.TimedOut);
                }

                if (process.ExitCode != 0)
                {
                    await WriteLogAsync(
                        writeLogsAsync,
                        $"Post-process '{displayName}' failed with exit code {process.ExitCode}.");

                    return new PostProcessResult(PostProcessStatus.Failed);
                }

                await WriteLogAsync(writeLogsAsync, $"Post-process '{displayName}' completed successfully.");
                return new PostProcessResult(PostProcessStatus.Succeeded);
            }
            catch (Exception exception)
            {
                TerminateProcess(process);
                await WaitForExitAsync(process);

                try
                {
                    await WriteLogAsync(
                        writeLogsAsync,
                        $"Post-process '{displayName}' failed ({exception.GetType().Name}).");
                }
                catch
                {
                }

                return new PostProcessResult(PostProcessStatus.Failed);
            }
        }

        internal static ProcessStartInfo CreateStartInfo(
            string executablePath,
            IEnumerable<string> arguments,
            string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = String.IsNullOrWhiteSpace(workingDirectory)
                    ? Directory.GetCurrentDirectory()
                    : workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            foreach (var argument in arguments ?? Enumerable.Empty<string>())
            {
                startInfo.ArgumentList.Add(argument ?? String.Empty);
            }

            return startInfo;
        }

        private static async Task FlushLogsAsync(
            ConcurrentQueue<string> pendingLogs,
            Func<IReadOnlyCollection<string>, Task> writeLogsAsync)
        {
            var logs = new List<string>();

            while (pendingLogs.TryDequeue(out var log))
            {
                logs.Add(log);
            }

            if (logs.Count > 0)
            {
                await writeLogsAsync(logs);
            }
        }

        private static Task WriteLogAsync(
            Func<IReadOnlyCollection<string>, Task> writeLogsAsync,
            string message)
        {
            return writeLogsAsync(new[] { message });
        }

        private static void TerminateProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }

        private static async Task WaitForExitAsync(Process process)
        {
            try
            {
                await process.WaitForExitAsync();
                process.WaitForExit();
            }
            catch
            {
            }
        }
    }
}
