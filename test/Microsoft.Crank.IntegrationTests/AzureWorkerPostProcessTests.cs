// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Crank.AzureDevOpsWorker;
using Xunit;

namespace Microsoft.Crank.IntegrationTests
{
    public class AzureWorkerPostProcessTests
    {
        [Fact]
        public void PayloadDeserializationSupportsOptionalPostProcess()
        {
            var payload = JobPayload.Deserialize(Encoding.UTF8.GetBytes(
                """
                {
                  "name": "crank",
                  "args": ["--json", "crank-results.json"],
                  "postProcess": {
                    "name": "Result export",
                    "args": ["upload", "--crank-json", "crank-results.json"],
                    "executable": "message-controlled.exe"
                  }
                }
                """));

            Assert.Equal("Result export", payload.PostProcess.Name);
            Assert.True(payload.PostProcess.Enabled);
            Assert.Equal(new[] { "upload", "--crank-json", "crank-results.json" }, payload.PostProcess.Args);
            Assert.Null(typeof(PostProcessPayload).GetProperty("Executable"));

            var disabledPayload = JobPayload.Deserialize(Encoding.UTF8.GetBytes(
                """
                {
                  "name": "crank",
                  "args": [],
                  "postProcess": {
                    "name": "Disabled export",
                    "enabled": false,
                    "args": []
                  }
                }
                """));

            Assert.False(disabledPayload.PostProcess.Enabled);

            var compatiblePayload = JobPayload.Deserialize(Encoding.UTF8.GetBytes(
                """{"name":"crank","args":["--json","crank-results.json"]}"""));

            Assert.Null(compatiblePayload.PostProcess);
        }

        [Fact]
        public void PayloadParseFailureDoesNotExposeArguments()
        {
            const string secret = "super-secret-post-process-token";
            var malformedPayload = Encoding.UTF8.GetBytes(
                $$"""{"postProcess":{"args":["{{secret}}"]}""");

            var exception = Assert.Throws<JobPayloadParseException>(() => JobPayload.Deserialize(malformedPayload));

            Assert.Null(exception.InnerException);
            Assert.DoesNotContain(secret, exception.ToString());
            Assert.DoesNotContain(
                Convert.ToHexString(Encoding.UTF8.GetBytes(secret)),
                exception.ToString());
        }

        [Fact]
        public void InvalidTimeoutParseFailureAndProgramLogAreSanitized()
        {
            const string secret = "PASSWORD=super-secret-invalid-timeout";
            var payload = Encoding.UTF8.GetBytes(
                $$"""{"name":"crank","timeout":"{{secret}}","args":[]}""");

            var exception = Assert.Throws<JobPayloadParseException>(() => JobPayload.Deserialize(payload));
            var programLog = Program.FormatExceptionForLog(exception);

            Assert.Null(exception.InnerException);
            Assert.Equal(
                $"JobPayloadParseException: Job payload parsing failed ({payload.Length} bytes, FormatException).",
                programLog);
            Assert.DoesNotContain(secret, exception.ToString());
            Assert.DoesNotContain(secret, programLog);
        }

        [Fact]
        public void WorkerConfigurationUsesCliThenEnvironmentFallback()
        {
            var environment = new Dictionary<string, string>
            {
                [WorkerConfiguration.PostProcessExecutableEnvironmentVariable] = "environment-exporter",
                [WorkerConfiguration.PostProcessTimeoutEnvironmentVariable] = "00:00:45",
                [WorkerConfiguration.MaxAutoLockRenewalDurationEnvironmentVariable] = "02:00:00"
            };

            var environmentConfiguration = WorkerConfiguration.Create(
                null,
                null,
                null,
                name => environment.GetValueOrDefault(name));

            Assert.Equal("environment-exporter", environmentConfiguration.PostProcessExecutablePath);
            Assert.Equal(TimeSpan.FromSeconds(45), environmentConfiguration.PostProcessTimeout);
            Assert.Equal(TimeSpan.FromHours(2), environmentConfiguration.MaxAutoLockRenewalDuration);

            var cliConfiguration = WorkerConfiguration.Create(
                "cli-exporter",
                "00:00:15",
                "03:00:00",
                name => environment.GetValueOrDefault(name));

            Assert.Equal("cli-exporter", cliConfiguration.PostProcessExecutablePath);
            Assert.Equal(TimeSpan.FromSeconds(15), cliConfiguration.PostProcessTimeout);
            Assert.Equal(TimeSpan.FromHours(3), cliConfiguration.MaxAutoLockRenewalDuration);

            var defaultConfiguration = WorkerConfiguration.Create(null, null, null, _ => null);
            Assert.Null(defaultConfiguration.PostProcessExecutablePath);
            Assert.Equal(WorkerConfiguration.DefaultPostProcessTimeout, defaultConfiguration.PostProcessTimeout);
            Assert.Equal(
                WorkerConfiguration.DefaultMaxAutoLockRenewalDuration,
                defaultConfiguration.MaxAutoLockRenewalDuration);
        }

        [Theory]
        [InlineData("not-a-timespan")]
        [InlineData("00:00:00")]
        [InlineData("-00:00:01")]
        public void WorkerConfigurationRejectsInvalidTimeout(string timeout)
        {
            Assert.Throws<ArgumentException>(() => WorkerConfiguration.Create(null, timeout, null, _ => null));
        }

        [Theory]
        [InlineData("not-a-timespan")]
        [InlineData("00:00:00")]
        [InlineData("-00:00:01")]
        public void WorkerConfigurationRejectsInvalidLockRenewalDuration(string duration)
        {
            Assert.Throws<ArgumentException>(() => WorkerConfiguration.Create(null, null, duration, _ => null));
        }

        [Fact]
        public void LockRenewalDurationAccountsForRetriesAndPostProcessTimeout()
        {
            var summedTimeoutConfiguration = WorkerConfiguration.Create(
                null,
                "00:05:00",
                "01:15:00",
                _ => null);
            var boundaryConfiguration = WorkerConfiguration.Create(
                null,
                "00:05:00",
                "01:20:00",
                _ => null);
            var belowBoundaryConfiguration = WorkerConfiguration.Create(
                null,
                "00:05:00",
                "01:19:59.9999999",
                _ => null);
            var payload = new JobPayload
            {
                Timeout = TimeSpan.FromMinutes(20),
                Retries = 2,
                PostProcess = new PostProcessPayload()
            };

            Assert.Equal(TimeSpan.FromMinutes(5), WorkerConfiguration.MessageLockRenewalSafetyMargin);
            Assert.False(summedTimeoutConfiguration.HasSufficientLockRenewalDuration(payload, out var requiredDuration));
            Assert.Equal(TimeSpan.FromMinutes(80), requiredDuration);
            Assert.True(boundaryConfiguration.HasSufficientLockRenewalDuration(payload, out requiredDuration));
            Assert.Equal(TimeSpan.FromMinutes(80), requiredDuration);
            Assert.False(belowBoundaryConfiguration.HasSufficientLockRenewalDuration(payload, out requiredDuration));
            Assert.Equal(TimeSpan.FromMinutes(80), requiredDuration);
            Assert.Equal(
                TimeSpan.FromMinutes(80),
                Program.CreateProcessorOptions(boundaryConfiguration).MaxAutoLockRenewalDuration);
        }

        [Fact]
        public void LockRenewalDurationPreservesJobsWithoutEnabledPostProcess()
        {
            var configuration = WorkerConfiguration.Create(
                null,
                "00:10:00",
                "00:35:00",
                _ => null);
            var payload = new JobPayload
            {
                Timeout = TimeSpan.FromMinutes(10),
                Retries = 2
            };

            Assert.True(configuration.HasSufficientLockRenewalDuration(payload, out var requiredDuration));
            Assert.Equal(TimeSpan.FromMinutes(35), requiredDuration);

            payload.PostProcess = new PostProcessPayload { Enabled = false };
            Assert.True(configuration.HasSufficientLockRenewalDuration(payload, out requiredDuration));
            Assert.Equal(TimeSpan.FromMinutes(35), requiredDuration);

            payload.PostProcess.Enabled = true;
            Assert.False(configuration.HasSufficientLockRenewalDuration(payload, out requiredDuration));
            Assert.Equal(TimeSpan.FromMinutes(65), requiredDuration);

            payload.PostProcess = null;
            payload.Retries = -1;
            Assert.True(configuration.HasSufficientLockRenewalDuration(payload, out requiredDuration));
            Assert.Equal(TimeSpan.FromMinutes(15), requiredDuration);
        }

        [Fact]
        public void LockRenewalDurationOverflowFailsSafely()
        {
            var configuration = WorkerConfiguration.Create(null, null, null, _ => null);
            var payload = new JobPayload
            {
                Timeout = TimeSpan.FromTicks(
                    TimeSpan.MaxValue.Ticks -
                    WorkerConfiguration.MessageLockRenewalSafetyMargin.Ticks +
                    1),
                Retries = 0
            };

            Assert.False(configuration.HasSufficientLockRenewalDuration(payload, out var requiredDuration));
            Assert.Equal(TimeSpan.MaxValue, requiredDuration);
        }

        [Fact]
        public void ProcessStartInfoPreservesArgumentsAndTrustedExecutable()
        {
            var arguments = new[]
            {
                "upload",
                "--label",
                "value with spaces",
                "\"quoted value\"",
                "semi;colon",
                String.Empty,
                null
            };

            var startInfo = PostProcessRunner.CreateStartInfo(
                "trusted-exporter",
                arguments,
                "trusted-working-directory");

            Assert.Equal("trusted-exporter", startInfo.FileName);
            Assert.Equal("trusted-working-directory", startInfo.WorkingDirectory);
            Assert.Equal(
                arguments.Select(argument => argument ?? String.Empty).ToArray(),
                startInfo.ArgumentList.ToArray());
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.False(startInfo.UseShellExecute);
        }

        [Fact]
        public async Task NoHookAndCrankFailureDoNotRunPostProcess()
        {
            var invocations = 0;

            var noHookResult = await AttemptExecution.ApplyPostProcessAsync(
                new AttemptResult(succeeded: true),
                postProcess: null,
                () =>
                {
                    invocations++;
                    return Task.FromResult(new PostProcessResult(PostProcessStatus.Succeeded));
                });

            var failedCrankResult = await AttemptExecution.ApplyPostProcessAsync(
                new AttemptResult(succeeded: false),
                new PostProcessPayload(),
                () =>
                {
                    invocations++;
                    return Task.FromResult(new PostProcessResult(PostProcessStatus.Succeeded));
                });

            Assert.True(noHookResult.Succeeded);
            Assert.False(failedCrankResult.Succeeded);
            Assert.Equal(0, invocations);
        }

        [Fact]
        public async Task DisabledPostProcessSkipsExecutionAndCleansUpSuccessfully()
        {
            var workingDirectory = CreateTestDirectory();
            var attempts = 0;
            var processStarts = 0;
            var postProcessNetworkCalls = 0;
            var logs = new List<string>();
            var lifecycle = new List<string>();

            var result = await AttemptExecution.RunWithRetriesAsync(
                retryCount: 2,
                _ =>
                {
                    attempts++;

                    return AttemptExecution.RunWithCleanupAsync(
                        workingDirectory,
                        () => AttemptExecution.ApplyPostProcessAsync(
                            new AttemptResult(succeeded: true),
                            new PostProcessPayload
                            {
                                Name = "Optional export",
                                Enabled = false,
                                Args = new[] { "--token", "unused-secret" }
                            },
                            () =>
                            {
                                processStarts++;
                                postProcessNetworkCalls++;
                                return Task.FromResult(new PostProcessResult(PostProcessStatus.Succeeded));
                            },
                            log =>
                            {
                                logs.Add(log);
                                lifecycle.Add("disabled-log");
                                return Task.CompletedTask;
                            }),
                        directory =>
                        {
                            lifecycle.Add("cleanup");
                            Directory.Delete(directory, recursive: true);
                        });
                },
                onRetry: null);

            Assert.True(result.Succeeded);
            Assert.Equal(1, attempts);
            Assert.Equal(0, processStarts);
            Assert.Equal(0, postProcessNetworkCalls);
            Assert.Equal(new[] { "Post-process 'Optional export' is disabled." }, logs);
            Assert.Equal(new[] { "disabled-log", "cleanup" }, lifecycle);
            Assert.False(Directory.Exists(workingDirectory));
        }

        [Fact]
        public async Task MissingExecutableFailsExplicitlyWithoutLoggingArguments()
        {
            var logs = new List<string>();
            var workingDirectory = CreateTestDirectory();
            var payload = new PostProcessPayload
            {
                Name = "Export\r\n##vso[injected]",
                Args = new[] { "--token", "super-secret" }
            };

            try
            {
                var result = await new PostProcessRunner(TimeSpan.FromMilliseconds(20)).RunAsync(
                    executablePath: null,
                    payload,
                    workingDirectory,
                    TimeSpan.FromSeconds(1),
                    messages =>
                    {
                        logs.AddRange(messages);
                        return Task.CompletedTask;
                    },
                    _ => Task.FromResult(false),
                    CancellationToken.None);

                var combinedLogs = String.Concat(logs);

                Assert.Equal(PostProcessStatus.Failed, result.Status);
                Assert.Contains("no post-process executable is configured", combinedLogs);
                Assert.DoesNotContain("super-secret", combinedLogs);
                Assert.DoesNotContain("##vso", combinedLogs);
                Assert.DoesNotContain("\r", combinedLogs);
                Assert.DoesNotContain("\n", combinedLogs);
            }
            finally
            {
                TryDelete(workingDirectory);
            }
        }

        [Fact]
        public async Task SuccessfulPostProcessUsesAttemptDirectoryAndRunsBeforeCleanup()
        {
            var workingDirectory = CreateTestDirectory();
            var rawResultsPath = Path.Combine(workingDirectory, "crank-results.json");
            File.WriteAllText(rawResultsPath, "{}");

            var command = CreateSuccessCommand();
            var logs = new List<string>();
            var lifecycle = new List<string>();

            var result = await AttemptExecution.RunWithCleanupAsync(
                workingDirectory,
                async () =>
                {
                    lifecycle.Add("post-process");

                    var postProcessResult = await new PostProcessRunner(TimeSpan.FromMilliseconds(20)).RunAsync(
                        command.Executable,
                        new PostProcessPayload
                        {
                            Name = "Result export",
                            Args = command.Arguments
                        },
                        workingDirectory,
                        TimeSpan.FromSeconds(10),
                        messages =>
                        {
                            logs.AddRange(messages);
                            return Task.CompletedTask;
                        },
                        _ => Task.FromResult(false),
                        CancellationToken.None);

                    Assert.True(File.Exists(rawResultsPath));
                    Assert.True(File.Exists(Path.Combine(workingDirectory, "post-working-directory.txt")));

                    lifecycle.Add("post-process-complete");
                    return new AttemptResult(postProcessResult.Succeeded, postProcessResult.Canceled);
                },
                directory =>
                {
                    lifecycle.Add("cleanup");
                    Assert.True(File.Exists(rawResultsPath));
                    Directory.Delete(directory, recursive: true);
                });

            Assert.True(result.Succeeded);
            Assert.Equal(new[] { "post-process", "post-process-complete", "cleanup" }, lifecycle);
            Assert.Contains("post stdout", logs);
            Assert.Contains("post stderr", logs);

            var reportedWorkingDirectory = logs.Single(log => log.StartsWith("working-directory=", StringComparison.Ordinal))
                .Substring("working-directory=".Length);

            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(reportedWorkingDirectory)));
            Assert.False(Directory.Exists(workingDirectory));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PostProcessFailureParticipatesInRetryState(bool timedOut)
        {
            var attempts = new List<int>();
            var retries = new List<int>();
            var firstAttemptStatus = timedOut ? PostProcessStatus.TimedOut : PostProcessStatus.Failed;

            var result = await AttemptExecution.RunWithRetriesAsync(
                retryCount: 2,
                async attempt =>
                {
                    attempts.Add(attempt);

                    return await AttemptExecution.ApplyPostProcessAsync(
                        new AttemptResult(succeeded: true),
                        new PostProcessPayload(),
                        () => Task.FromResult(new PostProcessResult(
                            attempt == 0 ? firstAttemptStatus : PostProcessStatus.Succeeded)));
                },
                retry => retries.Add(retry));

            Assert.True(result.Succeeded);
            Assert.Equal(new[] { 0, 1 }, attempts);
            Assert.Equal(new[] { 1 }, retries);
        }

        [Fact]
        public async Task CanceledPostProcessDoesNotRetry()
        {
            var attempts = 0;

            var result = await AttemptExecution.RunWithRetriesAsync(
                retryCount: 2,
                _ =>
                {
                    attempts++;
                    return Task.FromResult(new AttemptResult(succeeded: false, canceled: true));
                },
                onRetry: null);

            Assert.True(result.Canceled);
            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task FailedAttemptStillRunsCleanup()
        {
            var workingDirectory = CreateTestDirectory();
            var cleanupCalled = false;

            var result = await AttemptExecution.RunWithCleanupAsync(
                workingDirectory,
                () => Task.FromResult(new AttemptResult(succeeded: false)),
                directory =>
                {
                    cleanupCalled = true;
                    Directory.Delete(directory, recursive: true);
                });

            Assert.False(result.Succeeded);
            Assert.True(cleanupCalled);
            Assert.False(Directory.Exists(workingDirectory));
        }

        [Fact]
        public async Task PostProcessTimeoutStopsTheProcess()
        {
            var workingDirectory = CreateTestDirectory();
            var command = CreateSleepCommand();
            var logs = new List<string>();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var result = await new PostProcessRunner(TimeSpan.FromMilliseconds(20)).RunAsync(
                    command.Executable,
                    new PostProcessPayload { Name = "Timeout test", Args = command.Arguments },
                    workingDirectory,
                    TimeSpan.FromMilliseconds(100),
                    messages =>
                    {
                        logs.AddRange(messages);
                        return Task.CompletedTask;
                    },
                    _ => Task.FromResult(false),
                    CancellationToken.None);

                Assert.Equal(PostProcessStatus.TimedOut, result.Status);
                Assert.Contains(logs, log => log.Contains("timed out", StringComparison.Ordinal));
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
            }
            finally
            {
                TryDelete(workingDirectory);
            }
        }

        [Fact]
        public async Task AzureTaskCancellationStopsThePostProcess()
        {
            var workingDirectory = CreateTestDirectory();
            var command = CreateSleepCommand();
            var cancellationChecks = 0;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var result = await new PostProcessRunner(TimeSpan.FromMilliseconds(20)).RunAsync(
                    command.Executable,
                    new PostProcessPayload { Name = "Cancellation test", Args = command.Arguments },
                    workingDirectory,
                    TimeSpan.FromSeconds(30),
                    _ => Task.CompletedTask,
                    _ => Task.FromResult(++cancellationChecks >= 2),
                    CancellationToken.None);

                Assert.Equal(PostProcessStatus.Canceled, result.Status);
                Assert.True(cancellationChecks >= 2);
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
            }
            finally
            {
                TryDelete(workingDirectory);
            }
        }

        [Fact]
        public async Task StartupFailureIsSanitized()
        {
            var workingDirectory = CreateTestDirectory();
            var logs = new List<string>();

            try
            {
                var result = await new PostProcessRunner(TimeSpan.FromMilliseconds(20)).RunAsync(
                    Path.Combine(workingDirectory, "missing-exporter"),
                    new PostProcessPayload
                    {
                        Name = "Result export",
                        Args = new[] { "--token", "super-secret" }
                    },
                    workingDirectory,
                    TimeSpan.FromSeconds(1),
                    messages =>
                    {
                        logs.AddRange(messages);
                        return Task.CompletedTask;
                    },
                    _ => Task.FromResult(false),
                    CancellationToken.None);

                var combinedLogs = String.Join(Environment.NewLine, logs);

                Assert.Equal(PostProcessStatus.Failed, result.Status);
                Assert.Contains("failed to start", combinedLogs);
                Assert.DoesNotContain("super-secret", combinedLogs);
                Assert.DoesNotContain("--token", combinedLogs);
            }
            finally
            {
                TryDelete(workingDirectory);
            }
        }

        private static (string Executable, string[] Arguments) CreateSuccessCommand()
        {
            if (OperatingSystem.IsWindows())
            {
                return (
                    GetWindowsPowerShellPath(),
                    new[]
                    {
                        "-NoLogo",
                        "-NoProfile",
                        "-NonInteractive",
                        "-Command",
                        "[IO.File]::WriteAllText('post-working-directory.txt', [Environment]::CurrentDirectory); " +
                        "[Console]::Out.WriteLine('post stdout'); " +
                        "[Console]::Error.WriteLine('post stderr'); " +
                        "[Console]::Out.WriteLine('working-directory=' + [Environment]::CurrentDirectory)"
                    });
            }

            return (
                "/bin/sh",
                new[]
                {
                    "-c",
                    "pwd > post-working-directory.txt; " +
                    "printf 'post stdout\\n'; " +
                    "printf 'post stderr\\n' >&2; " +
                    "printf 'working-directory=%s\\n' \"$PWD\""
                });
        }

        private static (string Executable, string[] Arguments) CreateSleepCommand()
        {
            if (OperatingSystem.IsWindows())
            {
                return (
                    GetWindowsPowerShellPath(),
                    new[]
                    {
                        "-NoLogo",
                        "-NoProfile",
                        "-NonInteractive",
                        "-Command",
                        "Start-Sleep -Seconds 30"
                    });
            }

            return ("/bin/sh", new[] { "-c", "sleep 30" });
        }

        private static string GetWindowsPowerShellPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
        }

        private static string CreateTestDirectory()
        {
            var directory = Path.Combine(
                AppContext.BaseDirectory,
                "azure-worker-post-process-tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
