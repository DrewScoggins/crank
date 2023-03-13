using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Crank.RegressionBot
{
    public enum DifferenceType
    {
        Regression,
        Improvement,
        Unchanged
    }
    public abstract class Difference
    {
        public abstract (DifferenceType change, int changePointIndex) DetectDifference(double[] values);
    }
}
