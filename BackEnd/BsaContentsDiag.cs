using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Opt-in append-mode diagnostic for tracing writes to / skips on
/// <c>BsaHandler._bsaContents</c>. Intended to identify which call path
/// poisons the per-ModKey BSA index with an empty entry (which then
/// blocks all later populates via the ContainsKey guard at the top of
/// <see cref="BsaHandler.PopulateBsaContentPathsAsync"/>).
///
/// <para>Disabled by default. To re-enable: drop a file named
/// <c>LogBsaDiag.txt</c> (contents irrelevant) next to the exe and
/// restart. Mirrors <c>StartupLogger</c>'s <c>LogStartup.txt</c>
/// trigger pattern so re-enabling for a one-off repro doesn't require
/// rebuilding. When disabled, every call site in the codebase is a
/// single early-return on a static bool — the stack walk and string
/// formatting are skipped entirely.</para>
///
/// <para>Writes to <c>&lt;exe-dir&gt;\BsaContentsDiag.log</c>. Append mode
/// so a session that crashes or hangs still leaves a usable trail.
/// Each line includes timestamp, thread ID, the most-immediate caller
/// outside <c>BsaContentsDiag</c>/<c>BsaHandler</c>, and the event.</para>
/// </summary>
public static class BsaContentsDiag
{
    private const string LogFileName = "BsaContentsDiag.log";
    private const string TriggerFileName = "LogBsaDiag.txt";
    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static bool _openAttempted;

    /// <summary>True when the file trigger was found at startup. Hot-path
    /// callers (e.g. per-BSA-archive open) check this to skip building
    /// expensive trace strings when nobody's listening.</summary>
    public static bool IsEnabled { get; private set; }

    /// <summary>Call once during app startup, before any BSA-handler code
    /// runs. Looks for <c>LogBsaDiag.txt</c> next to the exe; presence
    /// flips <see cref="IsEnabled"/> on. Safe to call multiple times.</summary>
    public static void InitializeFromFileTrigger()
    {
        try
        {
            string triggerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TriggerFileName);
            if (File.Exists(triggerPath))
            {
                IsEnabled = true;
            }
        }
        catch
        {
            // Swallow — if the probe itself throws, leave IsEnabled false.
        }
    }

    private static StreamWriter? GetWriter()
    {
        if (_writer != null) return _writer;
        lock (_lock)
        {
            if (_writer != null) return _writer;
            if (_openAttempted) return null;
            _openAttempted = true;
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogFileName);
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream) { AutoFlush = true };
                _writer.WriteLine();
                _writer.WriteLine($"=== NPC2 BSA contents diagnostic — session start {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
            }
            catch
            {
                _writer = null;
            }
            return _writer;
        }
    }

    public static void Log(string message)
    {
        if (!IsEnabled) return;
        var w = GetWriter();
        if (w == null) return;
        // Walk up the stack to find the first frame outside this class
        // and BsaHandler — gives us the actual caller (e.g.
        // EnsureAllArchivesOpened, FindNpcNifPath) without us having to
        // pass it through every call site.
        string caller = ResolveCaller();
        int tid = Thread.CurrentThread.ManagedThreadId;
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [T{tid,3}] [{caller}] {message}";
        lock (_lock)
        {
            try { w.WriteLine(line); } catch { /* swallow */ }
        }
    }

    private static string ResolveCaller()
    {
        try
        {
            var st = new StackTrace(2, false);
            for (int i = 0; i < st.FrameCount; i++)
            {
                var frame = st.GetFrame(i);
                var m = frame?.GetMethod();
                if (m == null) continue;
                var declaring = m.DeclaringType?.FullName ?? "";
                if (declaring.Contains("BsaContentsDiag")) continue;
                if (declaring.Contains("BsaHandler") &&
                    m.Name != "PopulateBsaContentPathsAsync" &&
                    m.Name != "AddMissingModToCache" &&
                    m.Name != "EnsureAllArchivesOpened") continue;
                var typeShort = m.DeclaringType?.Name ?? "?";
                return $"{typeShort}.{m.Name}";
            }
        }
        catch { /* swallow */ }
        return "?";
    }
}
