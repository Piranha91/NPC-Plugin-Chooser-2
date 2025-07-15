// ContextualPerformanceTracer.cs
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading; // Required for Interlocked
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd
{
    public static class ContextualPerformanceTracer
    {
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ProfileData>> _stats = new();
        private static readonly AsyncLocal<string?> _currentContext = new();
        
        // Sampling-related fields have been removed.
        
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

        /// <summary>
        /// Resets all performance statistics. Call this before processing a new batch or group.
        /// </summary>
        public static void Reset()
        {
            _stats.Clear();
            _currentContext.Value = null;
            Debug.WriteLine($"--- ContextualPerformanceTracer Reset ---");
        }

        // The old ResetAndStartSampling method is no longer needed.

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
            
            // Sampling logic has been removed.
            
            return new ContextScope();
        }

        public static IDisposable Trace(string functionName)
        {
            // The tracer is now always active.
            string contextToUse = _currentContext.Value ?? "No Context";
            return new Tracer(contextToUse, functionName);
        }

        /// <summary>
        /// Generates a detailed performance report for a completed group, writing it to the debug console
        /// and returning it as a string for other logging purposes.
        /// </summary>
        /// <param name="groupName">The name of the NPC group that was just processed.</param>
        /// <returns>A formatted string containing the performance report.</returns>
        public static string GenerateReportForGroup(string groupName)
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

            // Find total NPCs and time from the main loop tracer across all contexts
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

            // Create a detailed breakdown table for all traced functions
            var allFunctions = _stats
                .SelectMany(contextKvp => contextKvp.Value.Select(funcKvp => new { FunctionName = funcKvp.Key, Data = funcKvp.Value }))
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
            sb.AppendLine("Function                               |      Calls |     Total (ms) |     Avg (ms) |     Max (ms)");
            sb.AppendLine("---------------------------------------+------------+----------------+--------------+--------------");

            foreach (var func in allFunctions)
            {
                if (func.TotalCalls <= 0) continue;
                double avg = func.TotalMilliseconds / func.TotalCalls;
                string name = func.FunctionName.PadRight(38);
                if (name.Length > 38) name = name.Substring(0, 38);
                sb.AppendLine($"{name} | {func.TotalCalls,10} | {func.TotalMilliseconds,14:N2} | {avg,12:N4} | {func.MaxMilliseconds,12:N2}");
            }

            sb.AppendLine("=====================================================================================");
            
            // Mirror the final report to the Debug console
            Debug.WriteLine(sb.ToString());
            
            return sb.ToString();
        }
        
        /// <summary>
        /// Generates a performance report specifically for the validation/screening phase.
        /// </summary>
        /// <returns>A formatted string containing the performance report.</returns>
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