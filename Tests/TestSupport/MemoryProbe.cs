using System.Diagnostics;
using System.IO;
using System.Runtime;

namespace NPC_Plugin_Chooser_2.Tests.TestSupport;

/// <summary>
/// Small, dependency-free helpers for the memory tier: deterministic GC settling, byte snapshots, and an
/// append-only per-iteration byte report (the local diagnostic output). None of these need a game install.
///
/// <para>Note: a WeakReference "was the tile collected" assertion is deliberately <em>not</em> offered.
/// A tile's constructor kicks a fire-and-forget image-load task with no cancellation token, which keeps the
/// tile reachable until it completes — and under the headless stub renderer that never happens for imageless
/// NPCs, so reachability is a 100% false positive here. The browse leak-fix contract is asserted
/// deterministically instead (see <c>NpcBrowseMemoryTests</c> — subscription-composite disposal).</para>
/// </summary>
public static class MemoryProbe
{
    /// <summary>
    /// Forces a full, blocking, compacting collection and drains the finalizer queue so that
    /// unreachable-but-not-yet-collected objects are actually gone before a reachability assertion or a
    /// byte snapshot. Runs the collect/finalize cycle twice: the first pass runs finalizers (which can
    /// resurrect nothing here but can release referenced objects), the second reclaims what they freed.
    /// </summary>
    public static void ForceFullGc()
    {
        for (int i = 0; i < 2; i++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>Managed heap bytes after a full GC — the stable "how much are we holding" number.</summary>
    public static long GcBytesAfterCollect()
    {
        ForceFullGc();
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    /// <summary>Process working set (physical RAM) in bytes — what the user actually sees in Task Manager.</summary>
    public static long WorkingSetBytes()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64;
    }

    public static double ToMiB(long bytes) => bytes / (1024.0 * 1024.0);

    /// <summary>
    /// Append-only report of memory over an iteration loop, written to the test output directory. Used by
    /// the opt-in diagnostics whose flows (GPU render, network FaceFinder) can't run in CI: they log
    /// bytes-per-iteration for a human to compare against a user's long-session growth, rather than assert
    /// a brittle absolute threshold.
    /// </summary>
    public sealed class MemoryReport
    {
        private readonly string _path;
        private readonly List<(int Iter, long Gc, long Ws)> _rows = new();

        public MemoryReport(string label)
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "MemoryReports");
            Directory.CreateDirectory(dir);
            // A stable name per label keeps the latest run easy to find and overwrites the prior one.
            _path = System.IO.Path.Combine(dir, SanitizeFileName(label) + ".log");
            File.WriteAllText(_path, $"# Memory report: {label}\n# iteration\tgcMiB\tworkingSetMiB\n");
        }

        public string Path => _path;

        public void Record(int iteration)
        {
            long gc = GcBytesAfterCollect();
            long ws = WorkingSetBytes();
            _rows.Add((iteration, gc, ws));
            File.AppendAllText(_path, $"{iteration}\t{ToMiB(gc):F1}\t{ToMiB(ws):F1}\n");
        }

        /// <summary>Managed-heap growth (MiB) from the first recorded row to the last — the trend that matters.</summary>
        public double GcGrowthMiB()
        {
            if (_rows.Count < 2) return 0;
            return ToMiB(_rows[^1].Gc - _rows[0].Gc);
        }

        public double WorkingSetGrowthMiB()
        {
            if (_rows.Count < 2) return 0;
            return ToMiB(_rows[^1].Ws - _rows[0].Ws);
        }

        private static string SanitizeFileName(string s)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }
    }
}
