// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;

namespace Microsoft.Crank.AzureDevOpsWorker
{
    public sealed class PostProcessPayload
    {
        private const int MaximumDisplayNameLength = 128;

        public string Name { get; set; }

        public string[] Args { get; set; } = Array.Empty<string>();

        internal string GetSafeDisplayName()
        {
            var sanitizedName = new string((Name ?? String.Empty)
                .Take(MaximumDisplayNameLength)
                .Select(character =>
                    Char.IsLetterOrDigit(character) ||
                    character == ' ' ||
                    character == '-' ||
                    character == '_' ||
                    character == '.' ||
                    character == '(' ||
                    character == ')'
                        ? character
                        : '_')
                .ToArray())
                .Trim();

            return String.IsNullOrEmpty(sanitizedName) ? "post-process" : sanitizedName;
        }
    }
}
