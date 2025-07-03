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
        
        // --- Configuration and State for Sampling ---
        private static int _sampleLimit;
        private static string _targetContextForSampling = string.Empty;
        private static int _samplesTaken;
        private static volatile bool _isProfilingActive;

        public static bool SampleLimitReached => !_isProfilingActive && _samplesTaken > 0;
        
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

        public static void ResetAndStartSampling(string targetContext, int sampleLimit)
        {
            _stats.Clear();
            _currentContext.Value = null;
            _targetContextForSampling = targetContext;
            _sampleLimit = sampleLimit;
            _samplesTaken = 0;
            _isProfilingActive = true; 
            Debug.WriteLine($"--- ContextualPerformanceTracer Started: Sampling {sampleLimit} of '{targetContext}' ---");
        }

        public static IDisposable BeginContext(ModKey sourceModKey)
        {
            if (!_isProfilingActive)
            {
                return new NoOpDisposable();
            }

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

            if (context == _targetContextForSampling)
            {
                int currentSampleCount = Interlocked.Increment(ref _samplesTaken);
                if (currentSampleCount >= _sampleLimit)
                {
                    _isProfilingActive = false;
                    Debug.WriteLine($"--- Profiling sample limit of {_sampleLimit} reached. Disabling tracer. ---");
                }
            }
            
            return new ContextScope();
        }

        public static IDisposable Trace(string functionName)
        {
            if (!_isProfilingActive)
            {
                return new NoOpDisposable();
            }

            string contextToUse = _currentContext.Value ?? "No Context";
            return new Tracer(contextToUse, functionName);
        }

        public static string GetReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n=========================== CONTEXTUAL PERFORMANCE REPORT (Sampled) ===========================");

            if (_stats.IsEmpty)
            {
                sb.AppendLine("No performance data was recorded.");
                return sb.ToString();
            }

            var orderedContexts = _stats.OrderBy(kvp => kvp.Key);

            foreach (var (context, functionStats) in orderedContexts)
            {
                // To make the report cleaner, let's try to get the number of NPCs (MainLoopIteration calls)
                long npcCount = 0;
                if (functionStats.TryGetValue("Patcher.MainLoopIteration", out var mainLoopData))
                {
                    npcCount = mainLoopData.CallCount;
                }

                sb.AppendLine($"\n--- CONTEXT: {context} (NPCs Sampled: {npcCount}) ---");
                sb.AppendLine("Function                               |      Calls |     Total (ms) |     Avg (ms) |     Max (ms)");
                sb.AppendLine("---------------------------------------+------------+----------------+--------------+--------------");

                var orderedFunctions = functionStats.OrderByDescending(kvp => kvp.Value.TotalMilliseconds);
                foreach (var (functionName, data) in orderedFunctions)
                {
                    if (data.CallCount <= 0) continue;
                    double avg = data.TotalMilliseconds / data.CallCount;
                    string name = functionName.PadRight(38);
                    if (name.Length > 38) name = name.Substring(0, 38);
                    sb.AppendLine($"{name} | {data.CallCount,10} | {data.TotalMilliseconds,14:N2} | {avg,12:N4} | {data.MaxMilliseconds,12:N2}");
                }

            }
            sb.AppendLine("=====================================================================================");
            return sb.ToString();
        }

        // ============================ THIS IS THE FIX ============================
        private class Tracer : IDisposable
        {
            private readonly string _context;
            private readonly string _functionName;
            private readonly Stopwatch _stopwatch;

            // The constructor now correctly accepts the two arguments.
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
        // =======================================================================

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