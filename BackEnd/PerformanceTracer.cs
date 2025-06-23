using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace NPC_Plugin_Chooser_2.BackEnd
{
    /// <summary>
    /// A static helper class for performance profiling.
    /// Usage: `using var p = PerformanceTracer.Trace("MyClass.MyMethod");`
    /// Call `PerformanceTracer.PrintReport()` to output summary statistics.
    /// Call `PerformanceTracer.Reset()` to clear all statistics.
    /// </summary>
    public static class PerformanceTracer
    {
        private static readonly ConcurrentDictionary<string, ProfileData> _stats = new ConcurrentDictionary<string, ProfileData>();

        private class ProfileData
        {
            public long CallCount;
            public double TotalMilliseconds;
            public double MaxMilliseconds;
            public readonly object LockObj = new object();

            public ProfileData()
            {
                CallCount = 0;
                TotalMilliseconds = 0;
                MaxMilliseconds = 0;
            }
        }

        public static IDisposable Trace(string functionName)
        {
            return new Tracer(functionName);
        }

        public static void Reset()
        {
            _stats.Clear();
            Debug.WriteLine("--- PerformanceTracer Statistics Reset ---");
        }

        public static string GetReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("====================================== PERFORMANCE REPORT ======================================");
            sb.AppendLine("Function                               |      Calls |     Total (ms) |     Avg (ms) |     Max (ms)");
            sb.AppendLine("---------------------------------------+------------+----------------+--------------+--------------");

            if (_stats.IsEmpty)
            {
                sb.AppendLine("No performance data was recorded.");
            }
            else
            {
                // Order by the most time-consuming functions first
                var orderedStats = _stats.OrderByDescending(kvp => kvp.Value.TotalMilliseconds);

                foreach (var stat in orderedStats)
                {
                    var data = stat.Value;
                    if (data.CallCount > 0)
                    {
                        double avg = data.TotalMilliseconds / data.CallCount;
                        // PadRight for function name, PadLeft for numbers
                        sb.AppendLine($"{stat.Key.PadRight(38).Substring(0, 38)} | {data.CallCount,10} | {data.TotalMilliseconds,14:N2} | {avg,12:N4} | {data.MaxMilliseconds,12:N2}");
                    }
                }
            }
            sb.AppendLine("==============================================================================================");
            sb.AppendLine();
            return sb.ToString();
        }

        public static void PrintReport()
        {
            Debug.WriteLine(GetReport());
        }

        private class Tracer : IDisposable
        {
            private readonly string _functionName;
            private readonly Stopwatch _stopwatch;

            public Tracer(string functionName)
            {
                _functionName = functionName;
                _stopwatch = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _stopwatch.Stop();
                double elapsed = _stopwatch.Elapsed.TotalMilliseconds;

                var data = _stats.GetOrAdd(_functionName, new ProfileData());

                lock (data.LockObj)
                {
                    data.CallCount++;
                    data.TotalMilliseconds += elapsed;
                    if (elapsed > data.MaxMilliseconds)
                    {
                        data.MaxMilliseconds = elapsed;
                    }
                }

                // Report individual call duration
                Debug.WriteLine($"[PERF] {_functionName} took {elapsed:F4} ms.");
            }
        }
    }
}