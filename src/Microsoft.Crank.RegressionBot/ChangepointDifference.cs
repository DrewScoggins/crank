using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Crank.RegressionBot
{
    public class ChangepointDifference : Difference
    {
        public override (DifferenceType change, int changePointIndex) DetectDifference(double[] values)
        {
            /*foreach (var counterId in counterComparers.Keys)
            {
                if (!counterComparers[counterId].IsTopCounter)
                {
                    continue;
                }
                int count = 0;
                double[] resultsData;
                SummarizedResultEntity[] sreData;
                if (BaselineTrendValues.Count != 0)
                {
                    return true;
                }
                else
                {
                    resultsData = new double[TrendValues[(uint)(Compare.Name + counterComparers[counterId].CounterName).GetHashCode()].Count];
                    if (resultsData.Length < 3)
                    {
                        return true;
                    }
                }

                ExtraTrendData = tdc.GetAllTestData(new List<string>() { $"{Compare.Name + counterComparers[counterId].CounterName}" }, 400)[(uint)(Compare.Name + counterComparers[counterId].CounterName).GetHashCode()];
                resultsData = new double[ExtraTrendData.Count];
                sreData = new SummarizedResultEntity[resultsData.Length];
                var keysList = ExtraTrendData.Keys.ToList();
                keysList.Sort();

                foreach (var item in keysList)
                {
                    resultsData[count] = ExtraTrendData[item].ResultAvg;
                    sreData[count] = ExtraTrendData[item];
                    count++;
                }
                for (int i = 0; i < resultsData.Length - 2; i++)
                {
                    double percentDiffOneAhead = Math.Abs((resultsData[i + 1] - resultsData[i]) / resultsData[i]);
                    double percentDiffTwoAhead = Math.Abs((resultsData[i + 2] - resultsData[i]) / resultsData[i]);
                    if (percentDiffOneAhead > .15 && percentDiffTwoAhead < .015)
                    {
                        resultsData[i + 1] = (resultsData[i] + resultsData[i + 2]) / 2;
                        i = i + 2;
                    }
                }
                allTestData = resultsData;*/
            string dataString = "";
            foreach (var datum in values)
            {
                dataString += $"{datum},";
            }
            dataString = dataString.Trim(',');
            string csvFileName = "test";
            foreach (var item in Path.GetInvalidFileNameChars())
            {
                csvFileName = csvFileName.Replace(item, '-');
            }
            csvFileName += ".csv";
            //csvFileName = Path.Combine("temp", csvFileName);
            Console.WriteLine(csvFileName);
            File.WriteAllText(csvFileName, dataString);
            if (!File.Exists("bocpd.py"))
            {
                File.WriteAllText("bocpd.py", peltPy);
            }
            else
            {
                File.Delete("bocpd.py");
                File.WriteAllText("bocpd.py", peltPy);
            }
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "py";
            psi.Arguments = $@"bocpd.py ""{csvFileName}""";
            Process p = new Process();
            psi.RedirectStandardOutput = true;
            p.StartInfo = psi;
            p.Start();
            p.WaitForExit();
            var indexes = p.StandardOutput.ReadToEnd().Trim('[').Trim('\n').Trim('\r').Trim(']').Split(",");
            if (indexes.Length > 1)
            {
                /*Dictionary<DateTime, DateTime> keyListMapper = new Dictionary<DateTime, DateTime>();
                int indexerIndex = 0;
                DateTime currentDateTimeBucket = keysList[0];
                foreach (var date in keysList)
                {
                    if (indexerIndex >= indexes.Length || date <= keysList[Int32.Parse(indexes[indexerIndex]) - 1])
                    {
                        keyListMapper.Add(date, currentDateTimeBucket);
                    }
                    else
                    {
                        Console.Write($"{Int32.Parse(indexes[indexerIndex])} ");
                        indexerIndex++;
                        currentDateTimeBucket = date;
                        keyListMapper.Add(date, currentDateTimeBucket);
                    }
                }
                Console.WriteLine($"{keyListMapper[compare.CommitTime]} {keyListMapper[baseline.CommitTime]}");
                if (keyListMapper[compare.CommitTime] != keyListMapper[baseline.CommitTime])
                {
                    Console.WriteLine($"{baseline.CommitTime} {compare.CommitTime}");
                    ChangepointBaseline = (RunInfoEntity)CloudTableHelper.RunInfoTable.Execute(TableOperation.Retrieve<RunInfoEntity>(sreData[Int32.Parse(indexes[indexes.Length - 2]) - 2].PartitionKey, "1")).Result;
                    ChangepointCompare = (RunInfoEntity)CloudTableHelper.RunInfoTable.Execute(TableOperation.Retrieve<RunInfoEntity>(sreData[Math.Min(Int32.Parse(indexes[indexes.Length - 2]) + 1, sreData.Length - 1)].PartitionKey, "1")).Result;
                    Changepoint = keyListMapper[compare.CommitTime];
                    DetectorDescription += $"IsChangePoint: Marked as a change because one of {indexes.Aggregate<string>((result, item) => result = result.Length > 3 ? (result + ", " + keysList[Int32.Parse(item) - 1]) : (keysList[Int32.Parse(result) - 1]) + ", " + keysList[Int32.Parse(item) - 1])} falls between {baseline.CommitTime} and {compare.CommitTime}.\r\n";
                    return true;
                }*/
                return (DifferenceType.Regression, indexes.Length - 2);
            }
            else
            {
                return (DifferenceType.Unchanged, -1);
            }
            //DetectorDescription += $"IsChangePoint: Marked as not a change because none of {indexes.Aggregate<string>((result, item) => result = result.Length > 3 ? (result + ", " + keysList[Int32.Parse(item) - 1]) : (keysList[Int32.Parse(result) - 1]) + ", " + keysList[Int32.Parse(item) - 1])} falls between {baseline.CommitTime} and {compare.CommitTime}.\r\n";
        }
        private string peltPy = @"import numpy as np
import ruptures as rpt
import sys

data = open(sys.argv[1], ""r"").read()
points = data.split(',')

for i in range(0, len(points)):
    points[i] = float (points[i])

points = np.concatenate([points])

algo = rpt.Pelt(model=""mahalanobis"", jump=1, min_size=3).fit(points)
results = algo.predict(pen=4*np.log(len(points)))
print(results)";
    }
}
