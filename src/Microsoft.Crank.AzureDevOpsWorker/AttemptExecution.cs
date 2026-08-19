// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading.Tasks;

namespace Microsoft.Crank.AzureDevOpsWorker
{
    internal sealed class AttemptResult
    {
        public AttemptResult(bool succeeded, bool canceled = false)
        {
            Succeeded = succeeded;
            Canceled = canceled;
        }

        public bool Succeeded { get; }

        public bool Canceled { get; }
    }

    internal static class AttemptExecution
    {
        public static async Task<AttemptResult> ApplyPostProcessAsync(
            AttemptResult crankResult,
            PostProcessPayload postProcess,
            Func<Task<PostProcessResult>> runPostProcessAsync)
        {
            if (!crankResult.Succeeded || crankResult.Canceled || postProcess == null)
            {
                return crankResult;
            }

            var postProcessResult = await runPostProcessAsync();
            return new AttemptResult(postProcessResult.Succeeded, postProcessResult.Canceled);
        }

        public static async Task<AttemptResult> RunWithCleanupAsync(
            string workingDirectory,
            Func<Task<AttemptResult>> runAttemptAsync,
            Action<string> cleanup)
        {
            try
            {
                return await runAttemptAsync();
            }
            finally
            {
                cleanup(workingDirectory);
            }
        }

        public static async Task<AttemptResult> RunWithRetriesAsync(
            int retryCount,
            Func<int, Task<AttemptResult>> runAttemptAsync,
            Action<int> onRetry)
        {
            AttemptResult result = null;
            retryCount = Math.Max(0, retryCount);

            for (var attempt = 0; attempt <= retryCount; attempt++)
            {
                if (attempt > 0)
                {
                    onRetry?.Invoke(attempt);
                }

                result = await runAttemptAsync(attempt);

                if (result.Succeeded || result.Canceled)
                {
                    break;
                }
            }

            return result ?? new AttemptResult(succeeded: false);
        }
    }
}
