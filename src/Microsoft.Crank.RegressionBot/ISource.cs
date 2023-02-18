using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Crank.RegressionBot.Models;

namespace Microsoft.Crank.RegressionBot
{
    public interface ISource
    {
        public IEnumerable<Rule> Match(string descriptor);
        public bool Include(string descriptor);
        public Task<List<BenchmarksResult>> GetData(BotOptions options);

        public SourceSection Regressions { get; set; }
        public int StdevCount { get; set; }
        public int DaysToSkip { get; set; }
        public string Name { get; set; }
        public int DaysToLoad { get; set; }

    }
}
