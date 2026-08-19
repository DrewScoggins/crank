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
        internal const string MaxAutoLockRenewalDurationEnvironmentVariable = "CRANK_AZDO_MAX_LOCK_RENEWAL_DURATION";
        internal static readonly TimeSpan DefaultPostProcessTimeout = TimeSpan.FromMinutes(10);
        internal static readonly TimeSpan DefaultMaxAutoLockRenewalDuration = TimeSpan.FromDays(1);
        internal static readonly TimeSpan MessageLockRenewalSafetyMargin = TimeSpan.FromMinutes(5);

        private WorkerConfiguration(
            string postProcessExecutablePath,
            TimeSpan postProcessTimeout,
            TimeSpan maxAutoLockRenewalDuration)
        {
            PostProcessExecutablePath = postProcessExecutablePath;
            PostProcessTimeout = postProcessTimeout;
            MaxAutoLockRenewalDuration = maxAutoLockRenewalDuration;
        }

        public string PostProcessExecutablePath { get; }

        public TimeSpan PostProcessTimeout { get; }

        public TimeSpan MaxAutoLockRenewalDuration { get; }

        internal static WorkerConfiguration Create(
            string postProcessExecutablePath,
            string postProcessTimeout,
            string maxAutoLockRenewalDuration,
            Func<string, string> environmentVariableReader = null)
        {
            environmentVariableReader ??= Environment.GetEnvironmentVariable;

            var executablePath = FirstNonEmpty(
                postProcessExecutablePath,
                environmentVariableReader(PostProcessExecutableEnvironmentVariable));

            var timeoutValue = FirstNonEmpty(
                postProcessTimeout,
                environmentVariableReader(PostProcessTimeoutEnvironmentVariable));

            var lockRenewalDurationValue = FirstNonEmpty(
                maxAutoLockRenewalDuration,
                environmentVariableReader(MaxAutoLockRenewalDurationEnvironmentVariable));

            var timeout = ParsePositiveTimeSpan(
                timeoutValue,
                DefaultPostProcessTimeout,
                "The post-process timeout",
                nameof(postProcessTimeout));

            var lockRenewalDuration = ParsePositiveTimeSpan(
                lockRenewalDurationValue,
                DefaultMaxAutoLockRenewalDuration,
                "The maximum lock renewal duration",
                nameof(maxAutoLockRenewalDuration));

            return new WorkerConfiguration(executablePath, timeout, lockRenewalDuration);
        }

        internal bool HasSufficientLockRenewalDuration(JobPayload jobPayload, out TimeSpan requiredDuration)
        {
            ArgumentNullException.ThrowIfNull(jobPayload);

            var attempts = (long)Math.Max(0, jobPayload.Retries) + 1;
            var jobTimeoutTicks = Math.Max(0, jobPayload.Timeout.Ticks);
            var postProcessTimeoutTicks = jobPayload.PostProcess?.Enabled == true
                ? PostProcessTimeout.Ticks
                : 0;

            try
            {
                var attemptDurationTicks = checked(jobTimeoutTicks + postProcessTimeoutTicks);
                var attemptsDurationTicks = checked(attemptDurationTicks * attempts);
                var requiredDurationTicks = checked(
                    attemptsDurationTicks + MessageLockRenewalSafetyMargin.Ticks);
                requiredDuration = TimeSpan.FromTicks(requiredDurationTicks);
            }
            catch (OverflowException)
            {
                requiredDuration = TimeSpan.MaxValue;
                return false;
            }

            return MaxAutoLockRenewalDuration >= requiredDuration;
        }

        private static string FirstNonEmpty(string first, string second)
        {
            if (!String.IsNullOrWhiteSpace(first))
            {
                return first.Trim();
            }

            return String.IsNullOrWhiteSpace(second) ? null : second.Trim();
        }

        private static TimeSpan ParsePositiveTimeSpan(
            string value,
            TimeSpan defaultValue,
            string description,
            string parameterName)
        {
            if (String.IsNullOrEmpty(value))
            {
                return defaultValue;
            }

            if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsedValue) ||
                parsedValue <= TimeSpan.Zero)
            {
                throw new ArgumentException(
                    $"{description} must be a positive TimeSpan. Received '{value}'.",
                    parameterName);
            }

            return parsedValue;
        }
    }
}
