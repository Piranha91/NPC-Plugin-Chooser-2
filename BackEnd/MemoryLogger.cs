using System;
using System.Collections.Generic;
using System.Diagnostics;
using NPC_Plugin_Chooser_2.BackEnd.Logging;
using System.IO;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Opt-in per-NPC memory sampler for diagnosing the RAM growth users report in long sessions evaluating
/// many NPCs. Disabled unless a <c>LogMemory.txt</c> trigger file sits next to the exe (same convention as
/// <see cref="StartupLogger"/> / <c>BsaContentsDiag</c>); when enabled it appends one table row to
/// <c>MemoryLog.html</c> each time the NPC selection rebuilds its mugshot tiles.
///
/// <para>Each row records the cheap managed-heap size (no forced collection), the process working set, and
/// the GC generation counts. Every <see cref="ForcedGcEvery"/>th sample it additionally forces a full,
/// blocking collection and logs the <em>retained</em> managed bytes — that column is the leak signal: if it
/// keeps climbing as you browse, managed objects are genuinely accumulating (a leak); if only the cheap /
/// working-set columns climb while the forced column stays flat, the growth is reclaimable cache, not a
/// leak. The forced collection adds a brief hitch every N NPCs, which is why it is throttled.</para>
///
/// <para>Logging must never destabilize the app: every method is guarded and swallows its own errors.</para>
/// </summary>
public static class MemoryLogger
{
    private const string TriggerFileName = "LogMemory.txt";
    private const string LogFileName = "MemoryLog.html";

    /// <summary>How often (in samples) to force a full GC and log the retained-after-GC managed bytes.</summary>
    private const int ForcedGcEvery = 20;

    private static HtmlLogWriter? _writer;
    private static readonly object _lock = new();
    private static bool _isEnabled;
    private static int _sampleIndex;

    public static bool IsEnabled => _isEnabled;

    /// <summary>
    /// Enables the sampler if <c>LogMemory.txt</c> exists next to the exe. Call once at startup (alongside
    /// the other file-trigger loggers in <c>App.OnStartup</c>).
    /// </summary>
    public static void InitializeFromFileTrigger()
    {
        try
        {
            string triggerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TriggerFileName);
            if (File.Exists(triggerPath)) Enable();
        }
        catch { /* logging must never crash the app */ }
    }

    private static void Enable()
    {
        lock (_lock)
        {
            if (_isEnabled) return;
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogFileName);
                _writer = new HtmlLogWriter(logPath, "NPC Plugin Chooser 2 — Memory Log",
                    meta: new[]
                    {
                        new KeyValuePair<string, string>("Started", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                        new KeyValuePair<string, string>("Version", App.ProgramVersion.ToString()),
                        new KeyValuePair<string, string>("How to read",
                            "Watch the Retained MB column: steadily rising = managed leak; flat while the " +
                            "other columns rise = reclaimable cache, not a leak. Retained is measured after " +
                            $"a forced full GC every {ForcedGcEvery} samples."),
                    },
                    tableColumns: new[]
                    {
                        "Time", "#", "Managed MB (no GC)", "Retained MB (after full GC)",
                        "Working set MB", "Gen0", "Gen1", "Gen2", "Tiles", "Context",
                    });
                _isEnabled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize MemoryLogger: {ex.Message}");
                _isEnabled = false;
            }
        }
    }

    /// <summary>
    /// Records one memory sample. <paramref name="context"/> identifies what was just selected (e.g. the NPC
    /// name), <paramref name="tileCount"/> the number of mugshot tiles built for it. No-op unless enabled.
    /// </summary>
    public static void LogSample(string context, int tileCount)
    {
        if (!_isEnabled) return;
        lock (_lock)
        {
            try
            {
                int idx = ++_sampleIndex;

                long managed = GC.GetTotalMemory(forceFullCollection: false);
                string retained = "-";
                if (idx == 1 || idx % ForcedGcEvery == 0)
                {
                    // Force a full, blocking collection so this row reports genuinely retained managed bytes.
                    retained = Mb(GC.GetTotalMemory(forceFullCollection: true));
                }

                long workingSet;
                using (var p = Process.GetCurrentProcess()) { p.Refresh(); workingSet = p.WorkingSet64; }

                _writer?.WriteTableRow(HtmlLogSeverity.Info, new[]
                {
                    DateTime.Now.ToString("HH:mm:ss"),
                    idx.ToString(),
                    Mb(managed),
                    retained,
                    Mb(workingSet),
                    GC.CollectionCount(0).ToString(),
                    GC.CollectionCount(1).ToString(),
                    GC.CollectionCount(2).ToString(),
                    tileCount.ToString(),
                    context,
                });
            }
            catch { /* never crash the app for logging */ }
        }
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1");
}
