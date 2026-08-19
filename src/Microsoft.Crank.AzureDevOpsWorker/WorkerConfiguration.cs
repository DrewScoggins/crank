// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;

namespace Microsoft.Crank.AzureDevOpsWorker
{
    internal sealed class WorkerConfiguration
    {
        internal const string PostProcessExecutableEnvironmentVariable = "CRANK_AZDO_POST_PROCESS_EXECUTABLE";
        internal const string PostProcessTimeoutEnvironmentVariable = "CRANK_AZDO_POST_PROCESS_TIMEOUT";
        internal static readonly TimeSpan DefaultPostProcessTimeout = TimeSpan.FromMinutes(10);

        private WorkerConfiguration(string postProcessExecutablePath, TimeSpan postProcessTimeout)
        {
            PostProcessExecutablePath = postProcessExecutablePath;
            PostProcessTimeout = postProcessTimeout;
        }

        public string PostProcessExecutablePath { get; }

        public TimeSpan PostProcessTimeout { get; }

        internal static WorkerConfiguration Create(
            string postProcessExecutablePath,
            string postProcessTimeout,
            Func<string, string> environmentVariableReader = null)
        {
            environmentVariableReader ??= Environment.GetEnvironmentVariable;

            var executablePath = FirstNonEmpty(
                postProcessExecutablePath,
                environmentVariableReader(PostProcessExecutableEnvironmentVariable));

            var timeoutValue = FirstNonEmpty(
                postProcessTimeout,
                environmentVariableReader(PostProcessTimeoutEnvironmentVariable));

            var timeout = DefaultPostProcessTimeout;

            if (!String.IsNullOrEmpty(timeoutValue) &&
                (!TimeSpan.TryParse(timeoutValue, CultureInfo.InvariantCulture, out timeout) || timeout <= TimeSpan.Zero))
            {
                throw new ArgumentException(
                    $"The post-process timeout must be a positive TimeSpan. Received '{timeoutValue}'.",
                    nameof(postProcessTimeout));
            }

            return new WorkerConfiguration(executablePath, timeout);
        }

        private static string FirstNonEmpty(string first, string second)
        {
            if (!String.IsNullOrWhiteSpace(first))
            {
                return first.Trim();
            }

            return String.IsNullOrWhiteSpace(second) ? null : second.Trim();
        }
    }
}
