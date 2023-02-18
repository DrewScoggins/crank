// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Crank.Models;
using Microsoft.Crank.RegressionBot.Models;
using Microsoft.Data.SqlClient;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using Newtonsoft.Json;

namespace Microsoft.Crank.RegressionBot
{
    public class KustoSource : ISource
    {
        // The name of the source
        public string Name { get; set;}

        // The name of the SQL table to load
        public string Table { get; set; }

        // The list of rules to apply
        public List<Rule> Rules { get; set; } = new List<Rule>();

        public SourceSection Regressions { get; set; }

        public int DaysToLoad { get; set; } = 7;

        // Numbers of values to use to build the stdev
        public int StdevCount { get; set; } = 6;

        // Numbers of days to skip from the analysis
        public int DaysToSkip { get; set; } = 0;

        /// <summary>
        /// Returns the list of <see cref="Rule" /> that match a descriptor
        /// </summary>
        public IEnumerable<Rule> Match(string descriptor)
        {
            foreach (var rule in Rules)
            {
                if (!string.IsNullOrEmpty(rule.Include))
                {
                    rule.IncludeRegex ??= new Regex(rule.Include);

                    if (!rule.IncludeRegex.IsMatch(descriptor))
                    {
                        continue;
                    }
                }

                yield return rule;
            }
        }

        /// <summary>
        /// Returns whether the descriptor should be include or not
        /// </summary>
        public bool Include(string descriptor)
        {
            // The last matched rule prevails
            // If there are no matching rule, don't include the descriptor

            var include = false;
            
            foreach (var rule in Rules)
            {
                if (!string.IsNullOrEmpty(rule.Include))
                {
                    rule.IncludeRegex ??= new Regex(rule.Include);

                    if (rule.IncludeRegex.IsMatch(descriptor))
                    {
                        include = true;
                    }
                }

                if (!string.IsNullOrEmpty(rule.Exclude))
                {
                    rule.ExcludeRegex ??= new Regex(rule.Exclude);

                    if (rule.ExcludeRegex.IsMatch(descriptor))
                    {
                        include = false;
                    }
                }
            }

            return include;
        }
        public async Task<List<BenchmarksResult>> GetData(BotOptions options)
        {
            if (Regressions == null)
            {
                return new List<BenchmarksResult>();
            }

            if (!Table.All(char.IsLetterOrDigit))
            {
                Console.Write("Invalid table name should only contain alphanumeric characters.");
                return new List<BenchmarksResult>();
            }

            var loadStartDateTimeUtc = DateTime.UtcNow.AddDays(0 - DaysToLoad);
            var detectionMaxDateTimeUtc = DateTime.UtcNow.AddDays(0 - DaysToSkip);

            var allResults = new List<BenchmarksResult>();

            // Load latest records

            Console.Write("Loading records... ");

            var db = "https://dotnetperf.westus.kusto.windows.net";
            var kcsb = new KustoConnectionStringBuilder(db, "PerformanceData").WithAadUserPromptAuthentication();
            var queryProvider = KustoClientFactory.CreateCslQueryProvider(kcsb);
            var clientRequestProperties = new ClientRequestProperties() { ClientRequestId = Guid.NewGuid().ToString() };
            var query = @$"Measurements
| where BuildTimeStamp > ago({DaysToLoad}d)
| where BuildBranch == ""refs/heads/main""
| where BuildArchitecture == ""x64""
| where TestCounterName == ""Duration of single invocation""
| where RunQueue == ""Windows.10.Amd64.19H1.Tiger.Perf""
| where RunConfigurationsRunKind == ""micro""
| where RunConfigurations[""CompilationMode""] == ""Tiered""
| where RunConfigurationsPgoType == """"
| project BuildName, TestCounterResultAverage, TestName, BuildTimeStamp
| order by BuildTimeStamp asc";

            var reader = queryProvider.ExecuteQuery(query, clientRequestProperties);
            Dictionary<string, List<double>> resultsData = new Dictionary<string, List<double>>();
            int count = 0;
            while (reader.Read())
            {
                var obj = new
                {
                    results = new[]
                    {
                        new
                        {
                            result = reader.GetDouble(1)
                        }
                    }
                };
                var testName = reader.GetString(2);
                var result = new BenchmarksResult
                {
                    Id = count,
                    Excluded = false, // Handle DBNull values
                    DateTimeUtc = (DateTime)reader["BuildTimeStamp"],
                    Session = Convert.ToString(reader["BuildName"]),
                    Scenario = Convert.ToString(reader["TestName"]),
                    Description = Convert.ToString(reader["TestName"]),
                    Document = JsonConvert.SerializeObject(obj),
                };
                allResults.Add(result);
                count++;
            }

            return allResults;
        }
    }
}
