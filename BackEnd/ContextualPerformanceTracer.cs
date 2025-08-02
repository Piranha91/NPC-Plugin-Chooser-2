// [ContextualPerformanceTracer.cs] - Full Code After Modifications
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading; // Required for Interlocked
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd
{
    public static class ContextualPerformanceTracer
    {
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ProfileData>> _stats = new();
        private static readonly AsyncLocal<string?> _currentContext = new();
        
        private static readonly HashSet<string> OfficialPlugins = new(StringComparer.OrdinalIgnoreCase)
        {
            "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"
        };

        private class ProfileData
        {
            public long CallCount;
            public double TotalMilliseconds;
            public double MaxMilliseconds;
            public readonly object LockObj = new();
        }

        public static void Reset()
        {
            _stats.Clear();
            _currentContext.Value = null;
            Debug.WriteLine($"--- ContextualPerformanceTracer Reset ---");
        }

        public static IDisposable BeginContext(ModKey sourceModKey)
        {
            string npcSourceMod = sourceModKey.FileName;
            string context;

            if (OfficialPlugins.Contains(npcSourceMod))
            {
                context = "Base Game";
            }
            else if (npcSourceMod.StartsWith("cc", StringComparison.OrdinalIgnoreCase) && npcSourceMod.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
            {
                context = "Creation Club";
            }
            else
            {
                context = "Mod-Added";
            }

            _currentContext.Value = context;
            
            return new ContextScope();
        }

        public static IDisposable Trace(string functionName, string? detail = null)
        {
            string contextToUse = _currentContext.Value ?? "No Context";
            string fullFunctionName = detail == null ? functionName : $"{functionName}[{detail}]";
            return new Tracer(contextToUse, fullFunctionName);
        }

        public static string GenerateReportForGroup(string groupName, bool showFunctionStats)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"\n================= PERFORMANCE REPORT for Group: [{groupName}] =================");

            if (_stats.IsEmpty)
            {
                sb.AppendLine("No performance data was recorded for this group.");
                sb.AppendLine("=====================================================================================");
                Debug.WriteLine(sb.ToString());
                return sb.ToString();
            }

            long totalNpcsProcessed = 0;
            double totalNpcProcessingTime = 0;

            foreach (var contextStats in _stats.Values)
            {
                if (contextStats.TryGetValue("Patcher.MainLoopIteration", out var mainLoopData))
                {
                    totalNpcsProcessed += mainLoopData.CallCount;
                    totalNpcProcessingTime += mainLoopData.TotalMilliseconds;
                }
            }

            if (totalNpcsProcessed > 0)
            {
                double avgTimePerNpc = totalNpcProcessingTime / totalNpcsProcessed;
                sb.AppendLine($"Total NPCs Processed: {totalNpcsProcessed}");
                sb.AppendLine($"Total Cycle Time:     {totalNpcProcessingTime:N2} ms");
                sb.AppendLine($"Average Time per NPC: {avgTimePerNpc:N4} ms");
            }
            else
            {
                sb.AppendLine("No 'Patcher.MainLoopIteration' calls were traced for this group.");
            }

            if (showFunctionStats)
            {
                var allFunctions = _stats
                    .SelectMany(contextKvp =>
                        contextKvp.Value.Select(funcKvp => new { FunctionName = funcKvp.Key, Data = funcKvp.Value }))
                    .GroupBy(x => x.FunctionName)
                    .Select(g => new
                    {
                        FunctionName = g.Key,
                        TotalCalls = g.Sum(x => x.Data.CallCount),
                        TotalMilliseconds = g.Sum(x => x.Data.TotalMilliseconds),
                        MaxMilliseconds = g.Max(x => x.Data.MaxMilliseconds)
                    })
                    .OrderByDescending(x => x.TotalMilliseconds)
                    .ToList();

                sb.AppendLine("\n--- Sub-function Details ---");
                sb.AppendLine(
                    "Function                               |      Calls |     Total (ms) |     Avg (ms) |     Max (ms)");
                sb.AppendLine(
                    "---------------------------------------+------------+----------------+--------------+--------------");

                foreach (var func in allFunctions)
                {
                    if (func.TotalCalls <= 0) continue;
                    double avg = func.TotalMilliseconds / func.TotalCalls;
                    string name = func.FunctionName.PadRight(38);
                    if (name.Length > 38) name = name.Substring(0, 38);
                    sb.AppendLine(
                        $"{name} | {func.TotalCalls,10} | {func.TotalMilliseconds,14:N2} | {avg,12:N4} | {func.MaxMilliseconds,12:N2}");
                }
            }

            sb.AppendLine("=====================================================================================");
            
            Debug.WriteLine(sb.ToString());
            
            return sb.ToString();
        }
        
        public static string GenerateValidationReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n============== VALIDATION PERFORMANCE REPORT ==============");

            if (_stats.IsEmpty)
            {
                sb.AppendLine("No performance data was recorded during validation.");
            }
            else
            {
                sb.AppendLine("Function                               |      Calls |     Total (ms) |     Avg (ms) |     Max (ms)");
                sb.AppendLine("---------------------------------------+------------+----------------+--------------+--------------");

                var orderedFunctions = _stats
                    .SelectMany(contextKvp => contextKvp.Value.Select(funcKvp => funcKvp))
                    .OrderByDescending(kvp => kvp.Value.TotalMilliseconds);

                foreach (var (functionName, data) in orderedFunctions)
                {
                    if (data.CallCount <= 0) continue;
                    double avg = data.TotalMilliseconds / data.CallCount;
                    string name = functionName.PadRight(38);
                    if (name.Length > 38) name = name.Substring(0, 38);
                    sb.AppendLine($"{name} | {data.CallCount,10} | {data.TotalMilliseconds,14:N2} | {avg,12:N4} | {data.MaxMilliseconds,12:N2}");
                }
            }
            
            sb.AppendLine("==========================================================");
            
            Debug.WriteLine(sb.ToString());
            return sb.ToString();
        }
        
        /// <summary>
        /// Generates a performance report with detailed breakdowns by context.
        /// </summary>
        public static string GenerateDetailedReport(string reportTitle)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"\n================= DETAILED PERFORMANCE REPORT: [{reportTitle}] =================");

            if (_stats.IsEmpty)
            {
                sb.AppendLine("No performance data was recorded.");
                sb.AppendLine("=====================================================================================");
                Debug.WriteLine(sb.ToString());
                return sb.ToString();
            }

            var allFunctions = _stats
                .SelectMany(contextKvp =>
                    contextKvp.Value.Select(funcKvp => new { FunctionNameWithDetail = funcKvp.Key, Data = funcKvp.Value }))
                .ToList();

            var funcDetailRegex = new Regex(@"^(?<func>.+?)(\[(?<detail>.+)\])?$");

            var aggregatedByFunction = allFunctions
                .Select(f =>
                {
                    var match = funcDetailRegex.Match(f.FunctionNameWithDetail);
                    return new
                    {
                        BaseFunction = match.Groups["func"].Value,
                        Detail = match.Groups["detail"].Success ? match.Groups["detail"].Value : "N/A",
                        f.Data
                    };
                })
                .GroupBy(f => f.BaseFunction)
                .Select(g => new
                {
                    BaseFunction = g.Key,
                    TotalMilliseconds = g.Sum(x => x.Data.TotalMilliseconds),
                    TotalCalls = g.Sum(x => x.Data.CallCount),
                    Details = g.GroupBy(detailGroup => detailGroup.Detail)
                               .Select(detail => new
                               {
                                   DetailName = detail.Key,
                                   TotalMilliseconds = detail.Sum(x => x.Data.TotalMilliseconds),
                                   TotalCalls = detail.Sum(x => x.Data.CallCount),
                                   MaxMilliseconds = detail.Max(x => x.Data.MaxMilliseconds)
                               })
                               .OrderByDescending(d => d.TotalMilliseconds)
                               .ToList()
                })
                .OrderByDescending(f => f.TotalMilliseconds)
                .ToList();


            sb.AppendLine("\n--- Performance Summary by Function ---");
            sb.AppendLine("Function                               |      Calls |     Total (ms) |     Avg (ms)");
            sb.AppendLine("---------------------------------------+------------+----------------+--------------");

            foreach (var func in aggregatedByFunction)
            {
                if (func.TotalCalls <= 0) continue;
                double avg = func.TotalMilliseconds / func.TotalCalls;
                string name = func.BaseFunction.PadRight(38);
                if (name.Length > 38) name = name.Substring(0, 38);
                sb.AppendLine($"{name} | {func.TotalCalls,10} | {func.TotalMilliseconds,14:N2} | {avg,12:N4}");
            }

            sb.AppendLine("\n--- Detailed Breakdown ---");
            foreach (var func in aggregatedByFunction)
            {
                if (func.Details.Count == 1 && func.Details.First().DetailName == "N/A")
                {
                    continue; 
                }

                sb.AppendLine($"\n--- Function: {func.BaseFunction} (Total: {func.TotalMilliseconds:N2} ms) ---");
                sb.AppendLine("Detail                                 |      Calls |     Total (ms) |     Avg (ms) |     Max (ms)");
                sb.AppendLine("---------------------------------------+------------+----------------+--------------+--------------");
                
                int detailsToShow = 15;
                foreach (var detail in func.Details.Take(detailsToShow))
                {
                     if (detail.TotalCalls <= 0) continue;
                     double avg = detail.TotalMilliseconds / detail.TotalCalls;
                     string name = detail.DetailName.PadRight(38);
                     if (name.Length > 38) name = name.Substring(0, 38);
                     sb.AppendLine($"{name} | {detail.TotalCalls,10} | {detail.TotalMilliseconds,14:N2} | {avg,12:N4} | {detail.MaxMilliseconds,12:N2}");
                }
                if(func.Details.Count > detailsToShow)
                {
                    sb.AppendLine($"... and {func.Details.Count - detailsToShow} more.");
                }
            }

            sb.AppendLine("=====================================================================================");

            Debug.WriteLine(sb.ToString());
            return sb.ToString();
        }

        private class Tracer : IDisposable
        {
            private readonly string _context;
            private readonly string _functionName;
            private readonly Stopwatch _stopwatch;

            public Tracer(string context, string functionName)
            {
                _context = context;
                _functionName = functionName;
                _stopwatch = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _stopwatch.Stop();
                double elapsed = _stopwatch.Elapsed.TotalMilliseconds;

                var contextStats = _stats.GetOrAdd(_context, _ => new ConcurrentDictionary<string, ProfileData>());
                var data = contextStats.GetOrAdd(_functionName, _ => new ProfileData());

                lock (data.LockObj)
                {
                    data.CallCount++;
                    data.TotalMilliseconds += elapsed;
                    if (elapsed > data.MaxMilliseconds) data.MaxMilliseconds = elapsed;
                }
            }
        }
        
        private class ContextScope : IDisposable
        {
            public void Dispose()
            {
                _currentContext.Value = null;
            }
        }
        
        private class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}