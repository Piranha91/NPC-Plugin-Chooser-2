using System.Diagnostics;
using System.Threading;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Opt-in counters and phase timers for the mod-discovery pipeline (FaceGen path caching, the
/// mod-folder scan and analysis), used by the discovery benchmark under <c>Tests/Integration/Benchmark</c>.
///
/// <para><b>Disabled by default and never enabled by the app itself.</b> When <see cref="Enabled"/> is
/// false every call is a single static-field read, so the instrumentation can stay in the production
/// code paths it measures. Counters are <em>logical</em> work counts (directories enumerated, paths
/// yielded, archives opened) — they are not physical disk I/O and must not be reported as bytes read.
/// Hot loops count locally and publish once per folder/archive, so enabling the metrics does not put an
/// interlocked operation on every file.</para>
///
/// <para>Phase timers accumulate wall-clock time of a region. Regions are only placed around whole
/// stages (never around per-item work running in parallel), so a phase total is elapsed time, not a
/// sum of overlapping worker times.</para>
/// </summary>
internal static class DiscoveryMetrics
{
    internal enum Counter
    {
        /// <summary>Mod folders whose loose files were enumerated (after de-duplication).</summary>
        LooseFoldersScanned,
        /// <summary>Folder references skipped because the same physical folder was already scanned.</summary>
        LooseDuplicateFolderReferences,
        /// <summary>Enumeration roots actually opened for loose files (Textures/Meshes trees, or FaceGen trees).</summary>
        LooseRootsEnumerated,
        /// <summary>File paths yielded by loose enumeration (before any FaceGen filtering).</summary>
        LooseCandidatePaths,
        /// <summary>Loose paths kept in the discovery cache handed to consumers.</summary>
        LoosePathsRetained,
        /// <summary>Discovery candidates (mod folders + synthetic entries) considered for archives.</summary>
        PreliminaryCandidates,
        /// <summary>Temporary VM_ModSetting instances built only to drive discovery.</summary>
        TemporaryViewModelsConstructed,
        /// <summary>Distinct archives associated with a candidate's plugins.</summary>
        ArchivesConsidered,
        /// <summary>Archives examined with the cheap folder-metadata probe.</summary>
        ArchivesProbed,
        /// <summary>Probed archives whose folder metadata proves they hold no FaceGen.</summary>
        ArchivesProbeAbsent,
        /// <summary>Probed archives whose folder metadata shows FaceGen folders.</summary>
        ArchivesProbePresent,
        /// <summary>Probes that could not decide (unsupported/unreadable metadata) and fell back.</summary>
        ArchivesProbeInconclusive,
        /// <summary>Archives whose complete file list was enumerated (legacy path or fallback).</summary>
        ArchivesFullyIndexed,
        /// <summary>Archive folders opened for their file list (FaceGen folders on the optimized path).</summary>
        ArchiveFoldersRead,
        /// <summary>Archive file entries materialized (path strings built) during FaceGen discovery.</summary>
        ArchiveFileEntriesEnumerated,
        /// <summary>Archive FaceGen paths kept in the discovery cache.</summary>
        ArchivePathsRetained,
        /// <summary>Archive FaceGen listings served from the in-process listing cache.</summary>
        ArchiveListingCacheHits,
        /// <summary>Plugin directory listings performed to find a folder's plugins.</summary>
        PluginDirectoryListings,
        /// <summary>Plugin files opened (overlay created) by PluginProvider.</summary>
        PluginLoads,
    }

    internal enum Phase
    {
        /// <summary>The whole CacheFaceGenPathsOnLoadAsync call.</summary>
        FaceGenCacheTotal,
        LooseFaceGenDiscovery,
        /// <summary>Building the per-entry folder/plugin lists that drive archive discovery.</summary>
        PreliminaryCandidates,
        /// <summary>Resolving which archives each entry's plugins own.</summary>
        ArchiveAssociation,
        /// <summary>Reading archive contents for FaceGen paths.</summary>
        ArchiveFaceGenIndexing,
        ModFolderScan,
        Analysis,
    }

    /// <summary>
    /// Per-item steps inside the parallel new-folder scan. Their totals are SUMMED WORKER TIME across
    /// concurrently running folder tasks (so they can exceed the scan's wall time); they show where the
    /// scan's work goes, not how long the user waits.
    /// </summary>
    internal enum WorkStep
    {
        ScanTemporaryViewModel,
        ScanFaceGenPrecheck,
        ScanLoadPlugins,
        ScanFindMissingMasters,
        ScanAppearancePluginCheck,
        ScanMergeInSuitability,
        ScanInjectedRecords,
        ScanUnloadPlugins,
    }

    internal static volatile bool Enabled;

    private static readonly long[] Counts = new long[Enum.GetValues<Counter>().Length];
    private static readonly long[] PhaseTicks = new long[Enum.GetValues<Phase>().Length];
    private static readonly long[] PhaseEntries = new long[Enum.GetValues<Phase>().Length];
    private static readonly long[] WorkTicks = new long[Enum.GetValues<WorkStep>().Length];
    private static readonly long[] WorkEntries = new long[Enum.GetValues<WorkStep>().Length];

    internal static void Add(Counter counter, long amount = 1)
    {
        if (!Enabled || amount == 0) return;
        Interlocked.Add(ref Counts[(int)counter], amount);
    }

    internal static PhaseScope Time(Phase phase) =>
        Enabled ? new PhaseScope(phase, Stopwatch.GetTimestamp()) : default;

    internal static WorkScope Work(WorkStep step) =>
        Enabled ? new WorkScope(step, Stopwatch.GetTimestamp()) : default;

    internal static void Reset()
    {
        Array.Clear(Counts);
        Array.Clear(PhaseTicks);
        Array.Clear(PhaseEntries);
        Array.Clear(WorkTicks);
        Array.Clear(WorkEntries);
    }

    /// <summary>Summed worker milliseconds and entry count per scan step.</summary>
    internal static Dictionary<string, (double Milliseconds, long Entries)> SnapshotWork() =>
        Enum.GetValues<WorkStep>().ToDictionary(s => s.ToString(),
            s => (Interlocked.Read(ref WorkTicks[(int)s]) * 1000.0 / Stopwatch.Frequency,
                Interlocked.Read(ref WorkEntries[(int)s])));

    internal readonly struct WorkScope : IDisposable
    {
        private readonly WorkStep _step;
        private readonly long _start;

        internal WorkScope(WorkStep step, long start)
        {
            _step = step;
            _start = start;
        }

        public void Dispose()
        {
            if (_start == 0) return;
            Interlocked.Add(ref WorkTicks[(int)_step], Stopwatch.GetTimestamp() - _start);
            Interlocked.Increment(ref WorkEntries[(int)_step]);
        }
    }

    internal static Dictionary<string, long> SnapshotCounters() =>
        Enum.GetValues<Counter>().ToDictionary(c => c.ToString(), c => Interlocked.Read(ref Counts[(int)c]));

    /// <summary>Accumulated milliseconds and entry count per phase.</summary>
    internal static Dictionary<string, (double Milliseconds, long Entries)> SnapshotPhases() =>
        Enum.GetValues<Phase>().ToDictionary(p => p.ToString(),
            p => (Interlocked.Read(ref PhaseTicks[(int)p]) * 1000.0 / Stopwatch.Frequency,
                Interlocked.Read(ref PhaseEntries[(int)p])));

    internal readonly struct PhaseScope : IDisposable
    {
        private readonly Phase _phase;
        private readonly long _start;

        internal PhaseScope(Phase phase, long start)
        {
            _phase = phase;
            _start = start;
        }

        public void Dispose()
        {
            if (_start == 0) return;
            Interlocked.Add(ref PhaseTicks[(int)_phase], Stopwatch.GetTimestamp() - _start);
            Interlocked.Increment(ref PhaseEntries[(int)_phase]);
        }
    }
}
