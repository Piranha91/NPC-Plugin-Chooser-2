using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Autofac;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.Benchmark;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Lab;

/// <summary>
/// Measures what the override search costs on a real load order, and what three replacements for
/// its depth setting would do (bench plan item N7). Under Include / Include As New the patcher
/// walks outward from the NPC looking for records the appearance mod overrides
/// (<c>RecordHandler.CollectOverriddenDependencyRecordsCore</c>); a depth setting
/// (<see cref="ModSetting.MaxNestedIntervalDepth"/>) stops it after N records in a row the mod does
/// not carry. This runs that walk at depths 1 to 5 and with no depth, from the default Override
/// Roots and from every root, and records for each run the records visited and collected, the split
/// by record type, the longest chain, the time and the memory.
///
/// <para><b>The product walk is not changed and not instrumented.</b> <see cref="TodayWalk"/> is a
/// copy of it, statement for statement, with counters added; at depths 1 and 2 its result is
/// compared with the product's own <c>RecordHandler.DeepGetOverriddenDependencyRecords</c>
/// and the agreement is written to the result. Each run gets link caches of its own, built the way
/// the product builds them (one <c>ImmutableModLinkCache</c> per plugin), so the memory of a run is
/// the memory of a first NPC.</para>
///
/// <para><b>Guards.</b> Every run stops, and says so, at 2,000,000 records visited, when the
/// process working set passes 12 GB, when the machine's free memory falls under 6 GB, or after
/// 120 seconds. A second line of defence ends the process at a 16 GB working set or 3 GB free.</para>
///
/// <para>Read-only: it builds no output and starts nothing. Opt-in, because it needs a mod manager
/// instance: does nothing unless <c>NPC2_N7_SPEC</c> names a bench arm spec
/// (<c>{ "instance", "profile", "settingsPath" }</c>, the format of <see cref="BenchOutputBuilder"/>).
/// <c>NPC2_N7_OUT</c> and <c>NPC2_N7_CSV</c> are the text and CSV files to APPEND to.
/// <c>NPC2_N7_PARTS</c> picks the parts (<c>measure</c>, <c>sweep</c>; default both),
/// <c>NPC2_N7_SUBJECTS</c> the measured NPCs by a part of their label,
/// <c>NPC2_N7_LIGHT</c> the NPCs to repeat with the short table only, and
/// <c>NPC2_N7_GUARD_SECONDS</c> shortens the time guard for a trial run.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class OverrideSearchCostDiagnostic
{
    private const long GuardVisits = 2_000_000;
    private const long GuardWorkingSetBytes = 12L * 1024 * 1024 * 1024;
    private const long HardStopWorkingSetBytes = 16L * 1024 * 1024 * 1024;
    // Not asked for by the plan: the machine is shared, so a run also stops when the MACHINE runs low.
    private const long GuardFreeMachineBytes = 6L * 1024 * 1024 * 1024;
    private const long HardStopFreeMachineBytes = 3L * 1024 * 1024 * 1024;
    private const double GuardSecondsDefault = 120;
    private const int Unbounded = int.MaxValue / 2;
    private static readonly int[] Depths = { 1, 2, 3, 4, 5, Unbounded };
    private static readonly long[] Budgets = { 1_000, 5_000, 20_000, 100_000, 500_000 };
    private static readonly FormKey FixtureArmature = FormKey.Factory("000800:NPC2Bench_ArmaRace_Base.esp");

    private sealed class Spec
    {
        public string Instance { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
        public string SettingsPath { get; set; } = string.Empty;
    }

    private sealed class GuardHit : Exception
    {
        public GuardHit(string reason) : base(reason) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>Free physical memory of the machine, or -1 when it cannot be read.</summary>
    private static long FreePhysicalBytes()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.AvailPhys : -1;
    }

    /// <summary>Samples the process while a run is under way and raises the time and memory guards.</summary>
    private sealed class Watch : IDisposable
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly double _maxSeconds;
        private readonly string? _notePath;
        private readonly Thread _thread;
        private volatile bool _stop;
        public volatile string? Reason;
        public long StartWorkingSet;
        public long PeakWorkingSet;
        public long PeakPrivate;
        public long PeakManaged;
        public long LowestFreeMachineMemory = long.MaxValue;

        public Watch(double maxSeconds, string? notePath)
        {
            _maxSeconds = maxSeconds;
            _notePath = notePath;
            using (var p = Process.GetCurrentProcess())
            {
                p.Refresh();
                StartWorkingSet = PeakWorkingSet = p.WorkingSet64;
                PeakPrivate = p.PrivateMemorySize64;
            }
            PeakManaged = GC.GetTotalMemory(false);
            _thread = new Thread(Loop) { IsBackground = true, Name = "N7 watch" };
            _thread.Start();
        }

        private void Sample(Process p)
        {
            p.Refresh();
            long ws = p.WorkingSet64;
            if (ws > PeakWorkingSet) PeakWorkingSet = ws;
            long priv = p.PrivateMemorySize64;
            if (priv > PeakPrivate) PeakPrivate = priv;
            long managed = GC.GetTotalMemory(false);
            if (managed > PeakManaged) PeakManaged = managed;

            long free = FreePhysicalBytes();
            if (free >= 0 && free < LowestFreeMachineMemory) LowestFreeMachineMemory = free;

            if (ws >= HardStopWorkingSetBytes || (free >= 0 && free < HardStopFreeMachineBytes))
            {
                try
                {
                    if (_notePath != null)
                        File.AppendAllText(_notePath,
                            $"{Environment.NewLine}HARD STOP {DateTime.Now:HH:mm:ss}: working set {Gb(ws)} GB, free memory on the machine {Gb(free)} GB " +
                            $"(limits: 16 GB working set, 3 GB free) inside one call; the process was ended.{Environment.NewLine}");
                }
                catch
                {
                    // Ending the process matters more than the note.
                }
                Environment.Exit(86);
            }

            if (Reason == null)
            {
                if (ws >= GuardWorkingSetBytes) Reason = $"working set {Gb(ws)} GB";
                else if (free >= 0 && free < GuardFreeMachineBytes) Reason = $"machine free memory {Gb(free)} GB";
                else if (_clock.Elapsed.TotalSeconds >= _maxSeconds) Reason = $"{_maxSeconds:F0} s";
            }
        }

        private void Loop()
        {
            using var p = Process.GetCurrentProcess();
            while (!_stop)
            {
                Sample(p);
                Thread.Sleep(25);
            }
            Sample(p);
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }
    }

    /// <summary>The link caches of one run: the mod's plugins up front, a load-order plugin the
    /// first time a record of it is asked for. The same shape as <c>RecordHandler._modLinkCaches</c>.</summary>
    private sealed class CacheSet
    {
        private readonly ILoadOrderGetter<IModListingGetter<ISkyrimModGetter>> _loadOrder;
        public readonly Dictionary<ModKey, ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>?> Caches = new();

        public CacheSet(ILoadOrderGetter<IModListingGetter<ISkyrimModGetter>> loadOrder,
            IEnumerable<(ModKey Key, ISkyrimModGetter Plugin)> modPlugins)
        {
            _loadOrder = loadOrder;
            foreach (var (key, plugin) in modPlugins)
            {
                Caches[key] = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(plugin, new LinkCachePreferences());
            }
        }

        /// <summary>The cache of the plugin a FormKey belongs to, read from the load order.</summary>
        public ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>? Origin(ModKey key)
        {
            if (Caches.TryGetValue(key, out var cache)) return cache;
            var listing = _loadOrder.TryGetValue(key);
            cache = listing != null && listing.Mod != null
                ? new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(listing.Mod, new LinkCachePreferences())
                : null;
            Caches[key] = cache;
            return cache;
        }
    }

    private readonly record struct PathNode(IMajorRecordGetter Record, bool Carried);

    private sealed record Found(FormKey Key, string Type, string? EditorId, long Ordinal, int ChainLength,
        int DepthNeeded, string Chain);

    private sealed class Cost
    {
        public long Count;
        public long Bytes;
        public long Ticks;
        public long LargestBytes;
    }

    private sealed class RunStats
    {
        public string Status = "ok";
        public bool GuardWasHit;
        public long Calls;
        public long DepthCuts;
        public long TypeCuts;
        public long VisitEvents;
        public int DistinctVisited;
        public long Resolved;
        public long Unresolved;
        public long ModNewRecords;
        public long LeftOut;
        public int LongestChain;
        public string LongestChainText = string.Empty;
        public readonly Dictionary<string, int> DistinctByType = new();
        public readonly Dictionary<string, int> UnresolvedByLinkType = new();
        public readonly Dictionary<FormKey, Found> Collected = new();
        public readonly Dictionary<FormKey, string> BridgeParents = new();
        public readonly Dictionary<(ModKey Plugin, Type LinkType), Cost> ResolveCost = new();
        public long ResolveAllocated;
        public long EnumerateAllocated;
        public double WallMs;
        public long ManagedBefore;
        public long ManagedAfter;
        public long ManagedPeak;
        public long Allocated;
        public long WsStart;
        public long WsPeak;
        public long PrivatePeak;
        public int PluginsTouched;

        public void Guard(Watch watch)
        {
            if (++VisitEvents > GuardVisits) throw new GuardHit($"{GuardVisits:N0} records visited");
            var reason = watch.Reason;
            if (reason != null) throw new GuardHit(reason);
            RuntimeHelpers.EnsureSufficientExecutionStack();
        }

        private void Note(ModKey plugin, Type linkType, long bytes, long ticks)
        {
            if (!ResolveCost.TryGetValue((plugin, linkType), out var cost))
            {
                cost = new Cost();
                ResolveCost[(plugin, linkType)] = cost;
            }
            cost.Count++;
            cost.Bytes += bytes;
            cost.Ticks += ticks;
            if (bytes > cost.LargestBytes) cost.LargestBytes = bytes;
            ResolveAllocated += bytes;
        }

        /// <summary>The product's lookup (<c>cache.TryResolve(link, out record)</c>), with what it
        /// allocated and how long it took booked against the plugin and the link's declared type.</summary>
        public bool Resolve(ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter> cache, ModKey plugin,
            IFormLinkGetter link, out IMajorRecordGetter? record)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long ticks = Stopwatch.GetTimestamp();
            IMajorRecordGetter? found = null;
            bool ok = cache.TryResolve(link, out found);
            Note(plugin, link.Type, GC.GetAllocatedBytesForCurrentThread() - bytes, Stopwatch.GetTimestamp() - ticks);
            record = found;
            return ok;
        }

        /// <summary>A lookup for one record type instead of the link's declared type.</summary>
        public bool ResolveTyped(ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter> cache, ModKey plugin,
            FormKey key, Type type, out IMajorRecordGetter? record)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long ticks = Stopwatch.GetTimestamp();
            ILinkCache byInterface = cache;
            bool ok = byInterface.TryResolve(key, type, out var found);
            Note(plugin, type, GC.GetAllocatedBytesForCurrentThread() - bytes, Stopwatch.GetTimestamp() - ticks);
            record = found;
            return ok;
        }

        public void Collect(IMajorRecordGetter record, List<PathNode> path)
        {
            // Depth the setting would need for this record along the path it was found on: the
            // longest run of records the mod does not carry, plus the carried record that ends it.
            int needed = 1, run = 0;
            foreach (var node in path)
            {
                if (node.Carried)
                {
                    needed = Math.Max(needed, run + 1);
                    run = 0;
                }
                else
                {
                    run++;
                    // Include As New copies every record between the NPC and an override so that
                    // something points at the copy (a "bridge parent").
                    BridgeParents[node.Record.FormKey] = node.Record.Registration.Name;
                }
            }
            needed = Math.Max(needed, run + 1);

            if (Collected.ContainsKey(record.FormKey)) return;
            Collected[record.FormKey] = new Found(record.FormKey, record.Registration.Name, record.EditorID,
                VisitEvents, path.Count + 1, needed, ChainText(path, record));
        }

        public void NoteLongest(List<PathNode> path)
        {
            LongestChain = path.Count;
            if (path.Count <= 16 || (path.Count & (path.Count - 1)) == 0)
            {
                LongestChainText = ChainText(path, null);
            }
        }

        private static string ChainText(List<PathNode> path, IMajorRecordGetter? last)
        {
            var parts = new List<string>();
            for (int i = 0; i < path.Count; i++)
            {
                if (path.Count > 14 && i >= 9 && i < path.Count - 3)
                {
                    if (i == 9) parts.Add($"... {path.Count - 12} more ...");
                    continue;
                }
                parts.Add(Describe(path[i].Record) + (path[i].Carried ? "*" : string.Empty));
            }
            if (last != null) parts.Add(Describe(last) + "*");
            return string.Join(" > ", parts);
        }

        private static string Describe(IMajorRecordGetter record) =>
            $"{record.Registration.Name} {record.EditorID ?? record.FormKey.ToString()}";
    }

    private static void Bump(Dictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out var n);
        counts[key] = n + 1;
    }

    /// <summary>
    /// The walk as it is today: a copy of <c>RecordHandler.CollectOverriddenDependencyRecordsCore</c>
    /// with counters. Kept statement for statement, including what looks odd: the depth counter is
    /// checked BEFORE the record is looked at, a record is marked searched as soon as it is entered
    /// (so one first reached at the depth limit is not opened again from its parent's siblings),
    /// and the list of links to follow is fixed before the first of them is followed (so a link
    /// that an earlier sibling's subtree has since reached is entered a second time).
    /// </summary>
    private sealed class TodayWalk
    {
        private readonly CacheSet _caches;
        private readonly List<ModKey> _modKeys;
        private readonly int _maxDepth;
        private readonly IReadOnlySet<Type>? _appearanceTypes;
        private readonly RunStats _stats;
        private readonly Watch _watch;
        private readonly HashSet<FormKey> _searched = new();
        private readonly List<PathNode> _path = new();
        // Not the product's walk: the same depth rule with the visiting order taken out. A record
        // is opened again whenever it is reached with a smaller count than before, so the result
        // is everything the rule allows, whichever link happens to be followed first.
        private readonly Dictionary<FormKey, int>? _smallestCount;

        public TodayWalk(CacheSet caches, List<ModKey> modKeys, int maxDepth, IReadOnlySet<Type>? appearanceTypes,
            RunStats stats, Watch watch, bool orderIndependent = false)
        {
            _caches = caches;
            _modKeys = modKeys;
            _maxDepth = maxDepth;
            _appearanceTypes = appearanceTypes;
            _stats = stats;
            _watch = watch;
            _smallestCount = orderIndependent ? new Dictionary<FormKey, int>() : null;
        }

        public void Run(IEnumerable<IFormLinkGetter> rootLinks)
        {
            try
            {
                foreach (var link in rootLinks) Visit(link, 0);
            }
            finally
            {
                _stats.DistinctVisited = _searched.Count;
            }
        }

        private void Visit(IFormLinkGetter formLinkGetter, int currentDepth)
        {
            if (formLinkGetter.IsNull) return;
            _stats.Calls++;

            currentDepth++;
            if (currentDepth > _maxDepth)
            {
                _stats.DepthCuts++;
                return;
            }

            if (_smallestCount != null)
            {
                if (_smallestCount.TryGetValue(formLinkGetter.FormKey, out var before) && before <= currentDepth) return;
                _smallestCount[formLinkGetter.FormKey] = currentDepth;
            }

            _stats.Guard(_watch);
            bool first = _searched.Add(formLinkGetter.FormKey);

            IMajorRecordGetter? modRecord = null;
            bool carried = false;

            // try to get the record version in the given mod plugin if possible
            foreach (var modKey in _modKeys)
            {
                if (_caches.Caches.TryGetValue(modKey, out var scopedCache) && scopedCache != null &&
                    _stats.Resolve(scopedCache, modKey, formLinkGetter, out modRecord) && modRecord != null)
                {
                    var context = scopedCache.ResolveContext(formLinkGetter);
                    currentDepth = 0; // reset the interval search
                    carried = true;
                    if (!_modKeys.Contains(formLinkGetter.FormKey.ModKey))
                    {
                        if (context != null &&
                            (_appearanceTypes == null || AppearanceRecordTypeCatalog.Contains(_appearanceTypes, modRecord)))
                        {
                            _stats.Collect(modRecord, _path);
                        }
                        else if (first)
                        {
                            _stats.LeftOut++;
                        }
                    }
                    else if (first)
                    {
                        _stats.ModNewRecords++;
                    }
                    break;
                }
            }

            // otherwise, traverse the parent record
            if (modRecord is null)
            {
                var parentCache = _caches.Origin(formLinkGetter.FormKey.ModKey);
                if (parentCache != null)
                {
                    _stats.Resolve(parentCache, formLinkGetter.FormKey.ModKey, formLinkGetter, out modRecord);
                }
            }

            if (modRecord == null)
            {
                if (first)
                {
                    _stats.Unresolved++;
                    Bump(_stats.UnresolvedByLinkType, formLinkGetter.Type.Name);
                }
                return;
            }

            if (first)
            {
                _stats.Resolved++;
                Bump(_stats.DistinctByType, modRecord.Registration.Name);
            }

            _path.Add(new PathNode(modRecord, carried));
            if (_path.Count > _stats.LongestChain) _stats.NoteLongest(_path);

            // A carried record restarts the count, so it never needs opening a second time.
            if (_smallestCount != null && carried) _smallestCount[formLinkGetter.FormKey] = 0;

            long bytes = GC.GetAllocatedBytesForCurrentThread();
            var subLinks = _smallestCount != null
                ? modRecord.EnumerateFormLinks().ToArray()
                : modRecord.EnumerateFormLinks().Where(x => !_searched.Contains(x.FormKey)).ToArray();
            _stats.EnumerateAllocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
            foreach (var subLink in subLinks)
            {
                Visit(subLink, currentDepth);
            }
            _path.RemoveAt(_path.Count - 1);
        }
    }

    /// <summary>
    /// Candidate 1: no depth. From the ticked root fields the walk follows only the links the
    /// declared appearance table (<see cref="AppearanceRecordTypeCatalog"/>) lists as followed,
    /// opens each record once, and looks a link up only as the record types it may lead to (so no
    /// cache is built for keywords, sounds, quests or placed references). With
    /// <paramref name="useful"/> given it also leaves out every record type from which none of the
    /// mod's overridden record types can be reached.
    ///
    /// <para>With <paramref name="known"/> given (the mod's own override list, read once) it is
    /// the walk "from the mod's list": whether the mod carries a record is answered from that list
    /// with no lookup at all, so EVERY link of an opened record can be tested, whatever its type,
    /// and a carried record is always opened, in the mod's version. A record the mod does not
    /// carry is still opened only through a declared appearance link. An NPC, carried or not, is
    /// followed through the ticked root fields only.</para>
    /// </summary>
    private sealed class TypedWalk
    {
        private static readonly IReadOnlySet<Type> NoTypes = new HashSet<Type>();
        private readonly CacheSet _caches;
        private readonly List<ModKey> _modKeys;
        private readonly IReadOnlySet<NpcRootField> _roots;
        private readonly IReadOnlySet<Type> _npcAllowed;
        private readonly IReadOnlySet<Type>? _useful;
        private readonly IReadOnlySet<FormKey>? _known;
        private readonly RunStats _stats;
        private readonly Watch _watch;
        private readonly HashSet<FormKey> _searched = new();
        private readonly List<PathNode> _path = new();
        private readonly Dictionary<Type, IReadOnlySet<Type>> _allowedByRow = new();
        private readonly Dictionary<(Type LinkType, IReadOnlySet<Type> Allowed), Type[]> _candidates = new();

        public TypedWalk(CacheSet caches, List<ModKey> modKeys, IReadOnlySet<NpcRootField> roots,
            IReadOnlySet<Type>? useful, RunStats stats, Watch watch, IReadOnlySet<FormKey>? known = null)
        {
            _caches = caches;
            _modKeys = modKeys;
            _roots = roots;
            _useful = useful;
            _known = known;
            _stats = stats;
            _watch = watch;
            _npcAllowed = Narrow(AppearanceRecordTypeCatalog.Resolve(roots));
        }

        private IReadOnlySet<Type> Narrow(IEnumerable<Type> types) =>
            _useful == null ? types.ToHashSet() : types.Where(t => _useful.Contains(t)).ToHashSet();

        public void Run(INpcGetter npc)
        {
            try
            {
                foreach (var link in NpcRootFieldCatalog.GetRootLinks(npc, _roots)) Visit(link, _npcAllowed);
            }
            finally
            {
                _stats.DistinctVisited = _searched.Count;
            }
        }

        private Type[] Candidates(Type linkType, IReadOnlySet<Type> allowed)
        {
            if (!_candidates.TryGetValue((linkType, allowed), out var types))
            {
                types = allowed.Where(t => linkType.IsAssignableFrom(t)).ToArray();
                _candidates[(linkType, allowed)] = types;
            }
            return types;
        }

        /// <summary>The record types something is followed out of: an NPC (its ticked root
        /// fields) and every type whose row follows at least one member.</summary>
        private static readonly HashSet<Type> Openable = AppearanceRecordTypeCatalog.All
            .Where(row => row.MembersComeFromNpcRootFieldCatalog || row.Members.Any(m => m.IsFollowed))
            .Select(row => row.GetterType).ToHashSet();

        private readonly Dictionary<(Type LinkType, IReadOnlySet<Type> Allowed), Type[]> _openableCandidates = new();

        /// <summary>From the mod's list, a record the mod does not carry is worth a lookup only
        /// when something can be followed out of it: a texture set, a color or a form list that
        /// is not in the list has nothing to give, so it is never looked up.</summary>
        private Type[] OpenableCandidates(Type linkType, IReadOnlySet<Type> allowed)
        {
            if (!_openableCandidates.TryGetValue((linkType, allowed), out var types))
            {
                types = Candidates(linkType, allowed).Where(t => Openable.Contains(t)).ToArray();
                _openableCandidates[(linkType, allowed)] = types;
            }
            return types;
        }

        private IReadOnlySet<Type> AllowedFrom(AppearanceRecordTypeCatalog.TypeEntry row)
        {
            if (!_allowedByRow.TryGetValue(row.GetterType, out var allowed))
            {
                allowed = Narrow(row.Members.SelectMany(m => m.Follows));
                _allowedByRow[row.GetterType] = allowed;
            }
            return allowed;
        }

        private void Visit(IFormLinkGetter link, IReadOnlySet<Type> allowed)
        {
            if (link.IsNull) return;
            _stats.Calls++;

            // From the mod's list: the mod carries the record (an override in its list, or a
            // record of its own). No lookup is needed to know that.
            bool listed = _known != null &&
                          (_known.Contains(link.FormKey) || _modKeys.Contains(link.FormKey.ModKey));
            var declared = Candidates(link.Type, allowed);
            var types = _known != null && !listed ? OpenableCandidates(link.Type, allowed) : declared;
            if (types.Length == 0 && !listed)
            {
                _stats.TypeCuts++;
                return;
            }

            if (!_searched.Add(link.FormKey)) return;
            _stats.Guard(_watch);

            IMajorRecordGetter? record = null;
            bool carried = false;
            foreach (var modKey in _modKeys)
            {
                // With the list, a record that is not in it is not in the mod's plugins either.
                if (_known != null && !listed) break;
                if (!_caches.Caches.TryGetValue(modKey, out var scopedCache) || scopedCache == null) continue;
                if (listed)
                {
                    // The mod's plugins are small; the link's own declared type will do.
                    _stats.Resolve(scopedCache, modKey, link, out record);
                }
                else
                {
                    foreach (var type in types)
                    {
                        if (_stats.ResolveTyped(scopedCache, modKey, link.FormKey, type, out record) && record != null) break;
                    }
                }
                if (record != null)
                {
                    carried = true;
                    break;
                }
            }

            if (record == null && types.Length > 0)
            {
                var parentCache = _caches.Origin(link.FormKey.ModKey);
                if (parentCache != null)
                {
                    foreach (var type in types)
                    {
                        if (_stats.ResolveTyped(parentCache, link.FormKey.ModKey, link.FormKey, type, out record) && record != null) break;
                    }
                }
            }

            if (record == null)
            {
                _stats.Unresolved++;
                Bump(_stats.UnresolvedByLinkType, link.Type.Name);
                return;
            }

            // An NPC is entered through a declared link only (a template, a leveled list), never
            // merely because the mod carries it: the mod may carry every NPC of the game.
            if (record is INpcGetter && declared.Length == 0)
            {
                _searched.Remove(link.FormKey); // a declared link may still lead here later
                _stats.TypeCuts++;
                return;
            }

            _stats.Resolved++;
            Bump(_stats.DistinctByType, record.Registration.Name);

            if (carried)
            {
                if (!_modKeys.Contains(link.FormKey.ModKey)) _stats.Collect(record, _path);
                else _stats.ModNewRecords++;
            }

            IEnumerable<IFormLinkGetter> subLinks;
            IReadOnlySet<Type> subAllowed;
            if (record is INpcGetter npc)
            {
                // An NPC met on the way (a template) is followed through the ticked root fields.
                subLinks = NpcRootFieldCatalog.GetRootLinks(npc, _roots);
                subAllowed = _npcAllowed;
            }
            else
            {
                // No row, or a row with nothing followed: terminal, nothing is followed out of it.
                // From the mod's list, a CARRIED record is still opened so that the mod's own
                // records it points at are found (a chain of the mod's edits is followed down).
                var row = AppearanceRecordTypeCatalog.Find(record.Registration.GetterType);
                subAllowed = row == null ? NoTypes : AllowedFrom(row);
                if (subAllowed.Count == 0 && !(carried && _known != null)) return;
                subLinks = record.EnumerateFormLinks();
            }

            _path.Add(new PathNode(record, carried));
            if (_path.Count > _stats.LongestChain) _stats.NoteLongest(_path);
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            var followed = subLinks.ToArray();
            _stats.EnumerateAllocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
            foreach (var subLink in followed)
            {
                Visit(subLink, subAllowed);
            }
            _path.RemoveAt(_path.Count - 1);
        }
    }

    private sealed class LevelRow
    {
        public int Level;
        public int NewRecords;
        public int Total;
        public int Overrides;
        public bool Complete;
        public bool FixtureHere;
        public double Seconds;
        public readonly Dictionary<string, int> ByType = new();
    }

    /// <summary>
    /// Nearest first: every link is followed, as in today's walk, but level by level, each record
    /// opened once and no count restarted. Level N holds the records N links from the NPC. It
    /// shows how fast the neighbourhood of an NPC grows, and what a budget on records opened
    /// would cover if the walk took the nearest records first.
    /// </summary>
    private sealed class NearestFirstWalk
    {
        private const int MaxFrontier = 3_000_000;
        public readonly List<LevelRow> Levels = new();
        public string Stopped = string.Empty;

        public void Run(CacheSet caches, List<ModKey> modKeys, IEnumerable<IFormLinkGetter> roots, RunStats stats,
            Watch watch, int maxLevel)
        {
            var seen = new HashSet<FormKey>();
            var frontier = roots.ToList();
            var clock = Stopwatch.StartNew();
            int total = 0;
            for (int level = 1; level <= maxLevel && frontier.Count > 0; level++)
            {
                var row = new LevelRow { Level = level };
                Levels.Add(row);
                var next = new List<IFormLinkGetter>();
                try
                {
                    foreach (var link in frontier)
                    {
                        if (link.IsNull || !seen.Add(link.FormKey)) continue;
                        stats.Calls++;
                        stats.Guard(watch);

                        IMajorRecordGetter? record = null;
                        bool carried = false;
                        foreach (var modKey in modKeys)
                        {
                            if (caches.Caches.TryGetValue(modKey, out var scopedCache) && scopedCache != null &&
                                stats.Resolve(scopedCache, modKey, link, out record) && record != null)
                            {
                                carried = true;
                                break;
                            }
                        }
                        if (record == null)
                        {
                            var parentCache = caches.Origin(link.FormKey.ModKey);
                            if (parentCache != null) stats.Resolve(parentCache, link.FormKey.ModKey, link, out record);
                        }
                        if (record == null)
                        {
                            stats.Unresolved++;
                            continue;
                        }

                        row.NewRecords++;
                        stats.Resolved++;
                        Bump(row.ByType, record.Registration.Name);
                        Bump(stats.DistinctByType, record.Registration.Name);
                        if (carried && !modKeys.Contains(link.FormKey.ModKey))
                        {
                            row.Overrides++;
                            if (link.FormKey == FixtureArmature) row.FixtureHere = true;
                            stats.Collected[link.FormKey] = new Found(link.FormKey, record.Registration.Name,
                                record.EditorID, stats.VisitEvents, level, level, "level " + level);
                        }

                        if (Stopped.Length > 0) continue;
                        foreach (var subLink in record.EnumerateFormLinks())
                        {
                            if (subLink.IsNull || seen.Contains(subLink.FormKey)) continue;
                            next.Add(subLink);
                            if (next.Count >= MaxFrontier)
                            {
                                Stopped = $"more than {MaxFrontier:N0} links waiting for level {level + 1}";
                                break;
                            }
                        }
                    }
                    row.Complete = true;
                }
                finally
                {
                    total += row.NewRecords;
                    row.Total = total;
                    row.Seconds = clock.Elapsed.TotalSeconds;
                    stats.DistinctVisited = seen.Count;
                }

                if (Stopped.Length > 0) break;
                frontier = next;
            }
        }
    }

    /// <summary>One appearance mod of the staged settings, its plugins loaded, its overrides listed.</summary>
    private sealed class ModInfo
    {
        public required ModSetting Setting { get; init; }
        public required List<(ModKey Key, ISkyrimModGetter Plugin)> Plugins { get; init; }
        public required HashSet<string> Folders { get; init; }
        /// <summary>Every record of the mod's plugins whose FormKey belongs to another plugin.</summary>
        public required Dictionary<FormKey, (string Type, Type GetterType, string? EditorId)> Overrides { get; init; }
        public required int NpcOverrides { get; init; }
        public required double ListSeconds { get; init; }
        public required CacheSet Lookup { get; init; }
        public List<ModKey> Keys => Setting.CorrespondingModKeys;

        public IEnumerable<KeyValuePair<FormKey, (string Type, Type GetterType, string? EditorId)>> NonNpcOverrides =>
            Overrides.Where(o => o.Value.GetterType != typeof(INpcGetter));

        private HashSet<FormKey>? _knownList;

        /// <summary>The list the "from the mod's list" walk tests links against: every override,
        /// NPCs included. (The walk itself enters an NPC through a declared link only.)</summary>
        public HashSet<FormKey> KnownList => _knownList ??= Overrides.Keys.ToHashSet();
    }

    private sealed record Subject(string Label, string ModNamePart, string NpcKey, bool FullMatrix, bool FromLoadOrder);

    private static readonly Subject[] DefaultSubjects =
    {
        new("Ulfric", "High Poly NPC Overhaul - Skyrim Special Edition", "01414D:Skyrim.esm", true, false),
        new("Rikke", "High Poly NPC Overhaul - Skyrim Special Edition", "0132A1:Skyrim.esm", false, false),
        new("Cultist (DLC2EncCultist06NordM)", "High Poly NPC Overhaul - Skyrim Special Edition", "034FC5:Dragonborn.esm", false, false),
        new("Briarheart", "High Poly NPC Overhaul - Skyrim Special Edition", "044310:Skyrim.esm", false, false),
        new("Kharjo", "High Poly NPC Overhaul - Skyrim Special Edition", "01B1D2:Skyrim.esm", false, false),
        new("Orgnar", "NPC2Bench - ArmaRace ModA", "013479:Skyrim.esm", false, false),
        new("Hod (not in the mod; walked from the load order's record)", "NPC2Bench - ArmaRace ModA", "01347D:Skyrim.esm", false, true),
        new("Lydia", "UBE Lydia", "0A2C8E:Skyrim.esm", false, false),
    };

    private readonly ITestOutputHelper _output;
    private string? _outPath;
    private string? _csvPath;
    private double _guardSeconds = GuardSecondsDefault;
    private string[] _lightSubjects = Array.Empty<string>();
    private ILoadOrderGetter<IModListingGetter<ISkyrimModGetter>> _loadOrder = null!;
    private Settings _settings = null!;
    private NpcChooserHarness _harness = null!;
    private readonly Dictionary<string, ModInfo> _mods = new(StringComparer.Ordinal);

    public OverrideSearchCostDiagnostic(ITestOutputHelper output) => _output = output;

    // ---- Output ----

    private void Emit(string text = "")
    {
        _output.WriteLine(text);
        if (_outPath != null) File.AppendAllText(_outPath, text + Environment.NewLine);
    }

    private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("F2", CultureInfo.InvariantCulture);
    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F0", CultureInfo.InvariantCulture);
    private static string DepthName(int depth) => depth == Unbounded ? "none" : depth.ToString(CultureInfo.InvariantCulture);

    private static string Counts(IEnumerable<KeyValuePair<string, int>> counts, int top = int.MaxValue) =>
        string.Join("; ", counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal)
            .Take(top).Select(c => $"{c.Key}={c.Value}"));

    private static Dictionary<string, int> ByType(IEnumerable<string> types)
    {
        var counts = new Dictionary<string, int>();
        foreach (var type in types) Bump(counts, type);
        return counts;
    }

    private static string CsvCell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private void Csv(string part, string mod, string npc, string npcKey, string walk, string roots, string depth,
        RunStats? s, string? statusOverride = null)
    {
        if (_csvPath == null) return;
        if (!File.Exists(_csvPath))
        {
            File.AppendAllText(_csvPath,
                "part,mod,npc,npcKey,walk,roots,depth,status,calls,visitEvents,distinctVisited,resolved,unresolved," +
                "cutByDepth,cutByType,overridesCollected,bridgeParents,modNewRecords,longestChain,wallMs,managedBeforeMB," +
                "managedAfterMB,managedPeakMB,allocatedMB,resolveAllocMB,enumerateAllocMB,wsStartMB,wsPeakMB,privatePeakMB," +
                "pluginsTouched,visitedByType,collectedByType,bridgeByType,resolveAllocByLinkTypeMB" + Environment.NewLine);
        }

        var cells = new List<string> { part, CsvCell(mod), CsvCell(npc), npcKey, CsvCell(walk), roots, depth };
        if (s == null)
        {
            cells.Add(CsvCell(statusOverride ?? "not run"));
            File.AppendAllText(_csvPath, string.Join(",", cells) + Environment.NewLine);
            return;
        }

        string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
        cells.Add(CsvCell(statusOverride ?? s.Status));
        cells.AddRange(new[]
        {
            s.Calls.ToString(), s.VisitEvents.ToString(), s.DistinctVisited.ToString(), s.Resolved.ToString(),
            s.Unresolved.ToString(), s.DepthCuts.ToString(), s.TypeCuts.ToString(), s.Collected.Count.ToString(),
            s.BridgeParents.Count.ToString(), s.ModNewRecords.ToString(), s.LongestChain.ToString(), F(s.WallMs),
            Mb(s.ManagedBefore), Mb(s.ManagedAfter), Mb(s.ManagedPeak), Mb(s.Allocated), Mb(s.ResolveAllocated),
            Mb(s.EnumerateAllocated), Mb(s.WsStart), Mb(s.WsPeak), Mb(s.PrivatePeak), s.PluginsTouched.ToString(),
            CsvCell(Counts(s.DistinctByType)),
            CsvCell(Counts(ByType(s.Collected.Values.Select(f => f.Type)))),
            CsvCell(Counts(ByType(s.BridgeParents.Values))),
            CsvCell(string.Join("; ", AllocByLinkType(s).Take(12).Select(a => $"{a.Type}={Mb(a.Bytes)}"))),
        });
        File.AppendAllText(_csvPath, string.Join(",", cells) + Environment.NewLine);
    }

    private static List<(string Type, long Bytes, long Count, double Seconds)> AllocByLinkType(RunStats s) =>
        s.ResolveCost.GroupBy(c => c.Key.LinkType.Name)
            .Select(g => (Type: g.Key, Bytes: g.Sum(c => c.Value.Bytes), Count: g.Sum(c => c.Value.Count),
                Seconds: g.Sum(c => c.Value.Ticks) / (double)Stopwatch.Frequency))
            .OrderByDescending(a => a.Bytes).ToList();

    private const string RunHeader =
        "  walk        roots    depth  status                          calls    visits  distinct  cut-depth  overrides  bridge  longest   wall s   managed MB    alloc MB   working set MB  plugins";

    private static string RunRow(string walk, string roots, string depth, RunStats s) =>
        $"  {walk,-11} {roots,-8} {depth,5}  {s.Status,-30} {s.Calls,7} {s.VisitEvents,9} {s.DistinctVisited,9} " +
        $"{(s.DepthCuts + s.TypeCuts),10} {s.Collected.Count,10} {s.BridgeParents.Count,7} {s.LongestChain,8} " +
        $"{(s.WallMs / 1000).ToString("F2", CultureInfo.InvariantCulture),8}  {Mb(s.ManagedBefore),5}>{Mb(s.ManagedAfter),-6} " +
        $"{Mb(s.Allocated),9}  {Mb(s.WsStart),6}>{Mb(s.WsPeak),-7} {s.PluginsTouched,7}";

    // ---- Runs ----

    private static long ForcedManaged()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        return GC.GetTotalMemory(false);
    }

    /// <summary>One guarded run on a thread of its own (a deep chain needs the stack), with link
    /// caches of its own, the managed heap read before and after with the caches still alive.</summary>
    private RunStats Execute(ModInfo mod, Action<CacheSet, RunStats, Watch> body, double? guardSeconds = null,
        CacheSet? sharedCaches = null)
    {
        var stats = new RunStats();
        stats.ManagedBefore = ForcedManaged();
        var caches = sharedCaches ?? new CacheSet(_loadOrder, mod.Plugins);
        long allocated = GC.GetTotalAllocatedBytes(false);
        Exception? failure = null;
        var clock = Stopwatch.StartNew();
        using (var watch = new Watch(guardSeconds ?? _guardSeconds, _outPath))
        {
            var thread = new Thread(() =>
            {
                try
                {
                    body(caches, stats, watch);
                }
                catch (GuardHit hit)
                {
                    stats.Status = "guard hit: " + hit.Message;
                    stats.GuardWasHit = true;
                }
                catch (InsufficientExecutionStackException)
                {
                    stats.Status = "guard hit: stack";
                    stats.GuardWasHit = true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }, 1 << 30);
            thread.Start();
            thread.Join();
            clock.Stop();
            watch.Dispose();
            stats.WsStart = watch.StartWorkingSet;
            stats.WsPeak = watch.PeakWorkingSet;
            stats.PrivatePeak = watch.PeakPrivate;
            stats.ManagedPeak = watch.PeakManaged;
        }
        stats.WallMs = clock.Elapsed.TotalMilliseconds;
        stats.Allocated = GC.GetTotalAllocatedBytes(false) - allocated;
        stats.ManagedAfter = ForcedManaged();
        stats.PluginsTouched = caches.Caches.Count(c => c.Value != null);
        GC.KeepAlive(caches);
        if (failure != null) stats.Status = "FAILED: " + failure.GetType().Name + ": " + failure.Message;
        return stats;
    }

    private ModInfo LoadMod(ModSetting setting)
    {
        if (_mods.TryGetValue(setting.DisplayName, out var known)) return known;

        var folders = setting.CorrespondingFolderPaths.ToHashSet();
        var clock = Stopwatch.StartNew();
        _harness.PluginProvider.LoadPlugins(setting.CorrespondingModKeys, folders, out _);
        var plugins = new List<(ModKey Key, ISkyrimModGetter Plugin)>();
        foreach (var key in setting.CorrespondingModKeys)
        {
            if (_harness.PluginProvider.TryGetPlugin(key, folders, out var plugin) && plugin != null)
            {
                plugins.Add((key, plugin));
            }
        }

        var overrides = new Dictionary<FormKey, (string Type, Type GetterType, string? EditorId)>();
        int npcOverrides = 0;
        clock.Restart();
        foreach (var (_, plugin) in plugins)
        {
            foreach (var record in plugin.EnumerateMajorRecords())
            {
                if (setting.CorrespondingModKeys.Contains(record.FormKey.ModKey)) continue;
                if (overrides.ContainsKey(record.FormKey)) continue;
                overrides[record.FormKey] = (record.Registration.Name, record.Registration.GetterType, record.EditorID);
                if (record is INpcGetter) npcOverrides++;
            }
        }
        clock.Stop();

        var info = new ModInfo
        {
            Setting = setting,
            Plugins = plugins,
            Folders = folders,
            Overrides = overrides,
            NpcOverrides = npcOverrides,
            ListSeconds = clock.Elapsed.TotalSeconds,
            Lookup = new CacheSet(_loadOrder, plugins),
        };
        _mods[setting.DisplayName] = info;
        return info;
    }

    /// <summary>The mod's own record of an NPC, picked as the patcher picks it: the disambiguated
    /// plugin, else the last of the mod's plugins that holds the NPC and is not resource-only.</summary>
    private static INpcGetter? ModNpcRecord(ModInfo mod, FormKey npcKey, out ModKey source)
    {
        source = ModKey.Null;
        var candidates = new List<ModKey>();
        if (mod.Setting.NpcPluginDisambiguation.TryGetValue(npcKey, out var chosen)) candidates.Add(chosen);
        for (int i = mod.Keys.Count - 1; i >= 0; i--)
        {
            if (mod.Setting.ResourceOnlyModKeys.Contains(mod.Keys[i])) continue;
            candidates.Add(mod.Keys[i]);
        }

        foreach (var key in candidates)
        {
            if (!mod.Lookup.Caches.TryGetValue(key, out var cache) || cache == null) continue;
            ILinkCache byInterface = cache;
            if (byInterface.TryResolve(npcKey, typeof(INpcGetter), out var record) && record is INpcGetter npc)
            {
                source = key;
                return npc;
            }
        }
        return null;
    }

    // ---- Record type graphs ----

    private Dictionary<Type, HashSet<Type>>? _fullGraph;

    /// <summary>Every link any member of a record class can hold, by reflection (no plugin read).</summary>
    private Dictionary<Type, HashSet<Type>> FullGraph()
    {
        if (_fullGraph != null) return _fullGraph;
        var graph = new Dictionary<Type, HashSet<Type>>();
        foreach (var (classType, getterType) in AppearanceRecordTypeCatalog.AllRecordTypes)
        {
            var targets = new HashSet<Type>();
            foreach (var p in classType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                targets.UnionWith(AppearanceRecordTypeCatalog.LinkTargetsOfMember(classType, p.Name));
            }
            graph[getterType] = targets;
        }
        return _fullGraph = graph;
    }

    /// <summary>The declared appearance links, with Npc's links being the ticked root fields.</summary>
    private static Dictionary<Type, HashSet<Type>> DeclaredGraph(IReadOnlySet<NpcRootField> roots)
    {
        var graph = new Dictionary<Type, HashSet<Type>>();
        foreach (var row in AppearanceRecordTypeCatalog.All)
        {
            graph[row.GetterType] = row.MembersComeFromNpcRootFieldCatalog
                ? roots.SelectMany(r => AppearanceRecordTypeCatalog.RootFieldTargets(r)).ToHashSet()
                : row.Members.SelectMany(m => m.Follows).ToHashSet();
        }
        return graph;
    }

    /// <summary>The record types from which one of <paramref name="targets"/> can be reached.</summary>
    private static HashSet<Type> CanReach(Dictionary<Type, HashSet<Type>> graph, IEnumerable<Type> targets)
    {
        var useful = targets.ToHashSet();
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (source, links) in graph)
            {
                if (!useful.Contains(source) && links.Overlaps(useful))
                {
                    useful.Add(source);
                    grew = true;
                }
            }
        }
        return useful;
    }

    private HashSet<Type> UsefulDeclared(ModInfo mod, IReadOnlySet<NpcRootField> roots)
    {
        var set = AppearanceRecordTypeCatalog.Resolve(roots);
        return CanReach(DeclaredGraph(roots), mod.Overrides.Values.Select(o => o.GetterType).Where(t => set.Contains(t)));
    }

    // ---- The test ----

    [Fact]
    public void MeasureOverrideSearch()
    {
        var specPath = Environment.GetEnvironmentVariable("NPC2_N7_SPEC");
        if (string.IsNullOrWhiteSpace(specPath))
        {
            _output.WriteLine("SKIPPED: set NPC2_N7_SPEC=<arm spec json> to measure the override search.");
            return;
        }

        _outPath = Environment.GetEnvironmentVariable("NPC2_N7_OUT");
        if (string.IsNullOrWhiteSpace(_outPath)) _outPath = null;
        _csvPath = Environment.GetEnvironmentVariable("NPC2_N7_CSV");
        if (string.IsNullOrWhiteSpace(_csvPath)) _csvPath = null;
        var parts = (Environment.GetEnvironmentVariable("NPC2_N7_PARTS") ?? "measure,sweep")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var wantedSubjects = (Environment.GetEnvironmentVariable("NPC2_N7_SUBJECTS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _lightSubjects = (Environment.GetEnvironmentVariable("NPC2_N7_LIGHT") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (double.TryParse(Environment.GetEnvironmentVariable("NPC2_N7_GUARD_SECONDS"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds) && seconds > 0 && seconds < GuardSecondsDefault)
        {
            _guardSeconds = seconds;
        }

        var total = Stopwatch.StartNew();
        var spec = JsonSerializer.Deserialize<Spec>(File.ReadAllText(specPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var settings = JSONhandler<Settings>.LoadJSONFile(spec.SettingsPath, out var loaded, out var loadError);
        loaded.Should().BeTrue(loadError);
        _settings = settings!;

        // Nothing is written by the walk, and nothing may land in the instance's own output mod.
        var scratch = Path.Combine(Path.GetTempPath(), "npc2-n7-override-search-diagnostic");
        Directory.CreateDirectory(scratch);
        _settings.OutputDirectory = scratch;

        Mo2ProfileEnvironment.TryBuild(spec.Instance, spec.Profile, _settings.OutputPluginName, out var env, out var failure)
            .Should().BeTrue(failure);
        _loadOrder = env!.Provider.LoadOrder!;

        using var harness = new NpcChooserHarness(env.Provider, _settings);
        _harness = harness;
        harness.PluginProvider.VirtualDataPluginPaths = env.VirtualDataPlugins;

        Emit();
        Emit("==========================================================================================");
        Emit($"N7 override search measurement, written {DateTime.Now:yyyy-MM-dd HH:mm:ss} by OverrideSearchCostDiagnostic (read-only)");
        Emit($"  parts: {string.Join(", ", parts)}; subjects: {(wantedSubjects.Length == 0 ? "all" : string.Join(", ", wantedSubjects))}");
        Emit($"  spec: {specPath}; instance {spec.Instance}; profile {spec.Profile}");
        Emit($"  load order: {env.ResolvedPlugins.Count} plugins resolved, {env.UnresolvedPlugins.Count} not found");
        Emit($"  guards per run: {GuardVisits:N0} records visited, working set {Gb(GuardWorkingSetBytes)} GB, {_guardSeconds:F0} s" +
             (_guardSeconds < GuardSecondsDefault ? " (SHORTENED for a trial run)" : string.Empty) +
             $", machine free memory under {Gb(GuardFreeMachineBytes)} GB; process ended at a {Gb(HardStopWorkingSetBytes)} GB " +
             $"working set or {Gb(HardStopFreeMachineBytes)} GB free; machine free memory now {Gb(FreePhysicalBytes())} GB");
        using (var p = Process.GetCurrentProcess())
        {
            p.Refresh();
            Emit($"  process before any run: working set {Mb(p.WorkingSet64)} MB, managed {Mb(ForcedManaged())} MB; " +
                 $"64-bit {Environment.Is64BitProcess}; server GC {GCSettings.IsServerGC}");
        }
        Emit("  columns: calls = links looked at; visits = records entered (a record entered twice counts twice);");
        Emit("    distinct = different records entered; cut-depth = links not followed because of the depth setting");
        Emit("    (for the typed walk: because the link cannot lead to an appearance record); overrides = records of");
        Emit("    other plugins that the mod carries, found by the walk (what Include patches in); bridge = records the");
        Emit("    mod does not carry that lie between the NPC and an override (what Include As New copies as well);");
        Emit("    longest = most links from the NPC to a record entered; managed MB = managed heap before > after the");
        Emit("    run, both after a full collection, the run's link caches still alive; alloc MB = bytes allocated");
        Emit("    during the run; working set MB = at the start > highest seen (sampled every 25 ms).");

        if (parts.Contains("measure", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var subject in DefaultSubjects)
            {
                if (wantedSubjects.Length > 0 &&
                    !wantedSubjects.Any(w => subject.Label.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                MeasureSubject(subject, env);
            }
        }

        if (parts.Contains("sweep", StringComparer.OrdinalIgnoreCase))
        {
            Sweep();
        }

        total.Stop();
        Emit();
        Emit($"TIMING: this invocation {total.Elapsed.TotalSeconds:F0} s");
    }

    private void EmitModFacts(ModInfo mod)
    {
        var nonNpc = mod.NonNpcOverrides.ToList();
        var defaults = AppearanceRecordTypeCatalog.Resolve(NpcRootFieldCatalog.Defaults);
        var appearance = nonNpc.Where(o => defaults.Contains(o.Value.GetterType)).ToList();
        Emit($"  mod '{mod.Setting.DisplayName}': plugins {string.Join(", ", mod.Plugins.Select(p => p.Key.FileName))}");
        Emit($"    its override list (records whose FormKey belongs to another plugin), read once in {mod.ListSeconds:F2} s: " +
             $"{mod.Overrides.Count} records = {mod.NpcOverrides} NPCs + {nonNpc.Count} others");
        Emit($"    the others by type: {(nonNpc.Count == 0 ? "(none)" : Counts(ByType(nonNpc.Select(o => o.Value.Type))))}");
        Emit($"    of the others, appearance record types (default roots): {appearance.Count}" +
             (appearance.Count > 0 ? " (" + Counts(ByType(appearance.Select(o => o.Value.Type))) + ")" : string.Empty));

        var overrideTypes = nonNpc.Select(o => o.Value.GetterType).ToHashSet();
        if (overrideTypes.Count > 0)
        {
            var full = CanReach(FullGraph(), overrideTypes);
            Emit($"    record types from which one of those types can be reached, by reflection over every link member: " +
                 $"{full.Count} of {AppearanceRecordTypeCatalog.AllRecordTypes.Count}");
        }
        var useful = UsefulDeclared(mod, NpcRootFieldCatalog.Defaults);
        Emit($"    record types from which one of the mod's overridden appearance types can be reached along the DECLARED " +
             $"appearance links (default roots): {useful.Count} of {defaults.Count}" +
             (useful.Count > 0 ? " (" + string.Join(", ", useful.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)) + ")" : string.Empty));
    }

    private void MeasureSubject(Subject subject, Mo2ProfileEnvironment env)
    {
        var setting = _settings.ModSettings.FirstOrDefault(m =>
            m.DisplayName.Contains(subject.ModNamePart, StringComparison.OrdinalIgnoreCase));
        Emit();
        Emit("------------------------------------------------------------------------------------------");
        if (setting == null)
        {
            Emit($"SUBJECT {subject.Label}: no mod entry whose name contains '{subject.ModNamePart}' in the staged settings. Not measured.");
            return;
        }

        var mod = LoadMod(setting);
        var npcKey = FormKey.Factory(subject.NpcKey);
        INpcGetter? record;
        string recordSource;
        if (subject.FromLoadOrder)
        {
            record = env.Provider.LinkCache!.TryResolve<INpcGetter>(npcKey, out var winning) ? winning : null;
            recordSource = "the load order's winning record (the mod has no record of this NPC, so the patcher never " +
                           "starts this walk; shown to see what a walk from this NPC would meet)";
        }
        else
        {
            record = ModNpcRecord(mod, npcKey, out var source);
            recordSource = $"the mod's record in {source.FileName}";
        }

        if (record == null)
        {
            Emit($"SUBJECT {subject.Label} [{npcKey}]: record not found. Not measured.");
            return;
        }

        string npcName = record.EditorID ?? npcKey.ToString();
        Emit($"SUBJECT {subject.Label}: {npcName} [{npcKey}], walked from {recordSource}");
        EmitModFacts(mod);

        var rootSets = new (string Name, IReadOnlySet<NpcRootField> Fields)[]
        {
            ("default", NpcRootFieldCatalog.Defaults),
            ("all", NpcRootFieldCatalog.AllFields),
        };
        foreach (var (name, fields) in rootSets)
        {
            var links = NpcRootFieldCatalog.GetRootLinks(record, fields);
            Emit($"    {name} roots: {fields.Count} fields, {links.Count} links on this NPC");
        }

        bool light = _lightSubjects.Any(w => subject.Label.Contains(w, StringComparison.OrdinalIgnoreCase));
        Emit();
        Emit("  TODAY'S WALK (a copy of RecordHandler.CollectOverriddenDependencyRecordsCore with counters)");
        if (light) Emit("  (short repeat: default roots at depth 1 to 3 only; the full table for this NPC is in an earlier part)");
        Emit(RunHeader);
        var results = new Dictionary<(string Roots, int Depth), RunStats>();
        foreach (var (rootName, fields) in rootSets)
        {
            var rootLinks = NpcRootFieldCatalog.GetRootLinks(record, fields);
            int? guardAt = null;
            foreach (var depth in Depths)
            {
                // A short repeat for an NPC whose full table is in an earlier part.
                if (light && !(rootName == "default" && depth <= 3)) continue;

                bool skip = guardAt != null && !subject.FullMatrix && !(rootName == "default" && depth == Unbounded);
                if (skip)
                {
                    string why = $"not run: the guard was hit at depth {DepthName(guardAt!.Value)}";
                    string walkName = "today";
                    Emit($"  {walkName,-11} {rootName,-8} {DepthName(depth),5}  {why}");
                    Csv("measure", setting.DisplayName, npcName, npcKey.ToString(), "today", rootName, DepthName(depth), null, why);
                    continue;
                }

                var stats = Execute(mod, (caches, s, watch) =>
                    new TodayWalk(caches, mod.Keys, depth, null, s, watch).Run(rootLinks));
                results[(rootName, depth)] = stats;
                if (stats.GuardWasHit) guardAt ??= depth;
                Emit(RunRow("today", rootName, DepthName(depth), stats));
                Csv("measure", setting.DisplayName, npcName, npcKey.ToString(), "today", rootName, DepthName(depth), stats);
            }
        }

        CrossCheckWithProduct(mod, record, results);
        EmitOrderEffect(mod, record, results);
        EmitNearestFirst(mod, record);

        foreach (var (rootName, _) in rootSets)
        {
            var runs = Depths.Where(d => results.ContainsKey((rootName, d)))
                .Select(d => (DepthName(d), results[(rootName, d)])).ToList();
            EmitTypeTable($"  records entered, by record type ({rootName} roots; distinct records; the 14 largest)", runs,
                s => s.DistinctByType, 14);
            EmitTypeTable($"  overrides found, by record type ({rootName} roots)", runs,
                s => ByType(s.Collected.Values.Select(f => f.Type)), 14);
            EmitTypeTable($"  bridge records Include As New would copy as well, by record type ({rootName} roots)", runs,
                s => ByType(s.BridgeParents.Values), 10);
        }

        foreach (var (rootName, _) in rootSets)
        {
            var costly = results.Where(r => r.Key.Roots == rootName).Select(r => (r.Key.Depth, r.Value))
                .OrderByDescending(r => r.Value.ResolveAllocated).FirstOrDefault();
            if (costly.Value == null) continue;
            EmitMemory($"  where the memory goes: {rootName} roots, depth {DepthName(costly.Depth)} ({costly.Value.Status})", costly.Value);
        }

        foreach (var (rootName, _) in rootSets)
        {
            var deepest = results.Where(r => r.Key.Roots == rootName).OrderByDescending(r => r.Value.LongestChain)
                .Select(r => (r.Key.Depth, r.Value)).FirstOrDefault();
            if (deepest.Value == null) continue;
            Emit($"  longest chain, {rootName} roots (depth {DepthName(deepest.Depth)}): {deepest.Value.LongestChain} links" +
                 (deepest.Value.LongestChainText.Length > 0 ? $"; a chain on the way there: {deepest.Value.LongestChainText}" : string.Empty));
        }

        EvaluateCandidates(subject, mod, record, npcName, npcKey, results);
    }

    private void CrossCheckWithProduct(ModInfo mod, INpcGetter record, Dictionary<(string Roots, int Depth), RunStats> results)
    {
        var handler = _harness.Container.Resolve<RecordHandler>();
        var notes = new List<string>();
        foreach (var (rootName, fields) in new (string, IReadOnlySet<NpcRootField>)[]
                 {
                     ("default", NpcRootFieldCatalog.Defaults), ("all", NpcRootFieldCatalog.AllFields),
                 })
        {
            foreach (var depth in new[] { 1, 2 })
            {
                // Not where the copy needed more than 4 GB: the product's walk has no memory guard.
                if (!results.TryGetValue((rootName, depth), out var mine) || mine.GuardWasHit || mine.VisitEvents > 100_000 ||
                    mine.ManagedAfter - mine.ManagedBefore > 4L * 1024 * 1024 * 1024)
                {
                    notes.Add($"{rootName}/{depth}: not compared");
                    continue;
                }

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    var product = handler.DeepGetOverriddenDependencyRecords(
                            NpcRootFieldCatalog.GetRootLinks(record, fields), mod.Keys, new HashSet<FormKey>(),
                            mod.Folders, depth, cts.Token)
                        .Select(c => c.Record.FormKey).ToHashSet();
                    bool same = product.SetEquals(mine.Collected.Keys);
                    notes.Add($"{rootName}/{depth}: {(same ? "same" : "DIFFERENT")} ({product.Count} product, {mine.Collected.Count} copy)");
                }
                catch (OperationCanceledException)
                {
                    notes.Add($"{rootName}/{depth}: product walk cancelled after 60 s");
                }
            }
        }
        Emit($"  check of the copy against the product's own walk (overrides found, roots/depth): {string.Join("; ", notes)}");

        // The product keeps the link cache of every load-order plugin its walk touched for as long
        // as the RecordHandler lives (its own clean-up after a batch covers the mod's plugins
        // only). Dropped here so the next run is not measured on top of them.
        long held = ForcedManaged();
        handler.ClearLinkCachesFor(_harness.Environment.LoadOrderModKeys.Concat(mod.Keys).ToList());
        long freed = held - ForcedManaged();
        Emit($"    the product's walk left {Mb(freed)} MB of link caches behind in its RecordHandler (cleared here by the diagnostic)");
    }

    /// <summary>
    /// Today's walk marks a record searched the moment it enters it, and never enters it again
    /// from another record. One first reached at the depth limit is therefore closed for good,
    /// even where another path reaches it with depth to spare. This runs the same depth rule with
    /// that taken out and lists what today's walk loses to the order it happens to visit in.
    /// </summary>
    private void EmitOrderEffect(ModInfo mod, INpcGetter record, Dictionary<(string Roots, int Depth), RunStats> results)
    {
        var rootLinks = NpcRootFieldCatalog.GetRootLinks(record, NpcRootFieldCatalog.Defaults);
        foreach (var depth in new[] { 2, 3 })
        {
            if (!results.TryGetValue(("default", depth), out var asToday) || asToday.GuardWasHit) continue;
            var orderFree = Execute(mod, (caches, s, watch) =>
                new TodayWalk(caches, mod.Keys, depth, null, s, watch, orderIndependent: true).Run(rootLinks));
            var lost = orderFree.Collected.Values.Where(f => !asToday.Collected.ContainsKey(f.Key))
                .OrderBy(f => f.Type, StringComparer.Ordinal).ToList();
            Emit($"  the same depth rule with the visiting order taken out (default roots, depth {depth}): {orderFree.Status}, " +
                 $"{orderFree.VisitEvents} entered, {orderFree.Collected.Count} overrides; today's walk finds {asToday.Collected.Count}; " +
                 $"lost to the visiting order: {lost.Count}" +
                 (lost.Count > 0 ? " (" + Counts(ByType(lost.Select(f => f.Type))) + ")" : string.Empty));
            foreach (var f in lost.Take(6)) Emit("      " + FoundLine(f));
        }
    }

    private void EmitNearestFirst(ModInfo mod, INpcGetter record)
    {
        var rootLinks = NpcRootFieldCatalog.GetRootLinks(record, NpcRootFieldCatalog.Defaults);
        var walk = new NearestFirstWalk();
        var stats = Execute(mod, (caches, s, watch) => walk.Run(caches, mod.Keys, rootLinks, s, watch, 12),
            guardSeconds: Math.Min(_guardSeconds, 60));
        Emit();
        Emit("  NEAREST FIRST (default roots): every link followed, each record opened once, no count restarted;");
        Emit("  level N = the records N links from the NPC. The 'total' column is the budget a nearest-first walk needs to cover that level.");
        Emit(RunHeader);
        Emit(RunRow("nearest", "default", "none", stats));
        if (walk.Stopped.Length > 0) Emit($"    stopped: {walk.Stopped}");
        Emit("    level  new records     total  overrides first met  seconds so far  largest types");
        foreach (var row in walk.Levels)
        {
            string seconds = row.Seconds.ToString("F1", CultureInfo.InvariantCulture);
            Emit($"    {row.Level,5} {row.NewRecords,12} {row.Total,9} {row.Overrides,20} {seconds,15}  " +
                 Counts(row.ByType, 7) + (row.Complete ? string.Empty : "  (level not finished)") +
                 (row.FixtureHere ? "  <- the fixture's armature is here" : string.Empty));
        }
    }

    private void EmitTypeTable(string title, List<(string Column, RunStats Stats)> runs,
        Func<RunStats, Dictionary<string, int>> pick, int top)
    {
        var tables = runs.Select(r => (r.Column, Counts: pick(r.Stats), r.Stats.GuardWasHit)).ToList();
        var types = tables.SelectMany(t => t.Counts.OrderByDescending(c => c.Value).Take(top).Select(c => c.Key))
            .Distinct().OrderByDescending(type => tables.Max(t => t.Counts.GetValueOrDefault(type)))
            .ThenBy(type => type, StringComparer.Ordinal).ToList();
        Emit();
        Emit(title);
        if (types.Count == 0)
        {
            Emit("    (none)");
            return;
        }
        Emit("    " + "type".PadRight(26) + string.Concat(tables.Select(t => ("depth " + t.Column + (t.GuardWasHit ? "!" : "")).PadLeft(12))));
        foreach (var type in types)
        {
            Emit("    " + type.PadRight(26) + string.Concat(tables.Select(t => t.Counts.GetValueOrDefault(type).ToString().PadLeft(12))));
        }
        Emit("    " + "ALL TYPES".PadRight(26) + string.Concat(tables.Select(t => t.Counts.Values.Sum().ToString().PadLeft(12))));
        if (tables.Any(t => t.GuardWasHit)) Emit("    (! = the run was stopped by a guard; its numbers are what it had reached)");
    }

    private void EmitMemory(string title, RunStats s)
    {
        Emit();
        Emit(title);
        Emit($"    allocated {Mb(s.Allocated)} MB in all: {Mb(s.ResolveAllocated)} MB inside record lookups, " +
             $"{Mb(s.EnumerateAllocated)} MB listing records' links; kept after the run {Mb(s.ManagedAfter - s.ManagedBefore)} MB");
        Emit("    lookups by the link's declared type (all plugins together):");
        foreach (var a in AllocByLinkType(s).Take(10))
        {
            Emit($"      {a.Type,-34} {Mb(a.Bytes),7} MB  {a.Count,8} lookups  {a.Seconds.ToString("F2", CultureInfo.InvariantCulture),7} s");
        }
        Emit("    the costliest (plugin, link type) pairs; 'largest' is the biggest single lookup, i.e. building that cache:");
        foreach (var entry in s.ResolveCost.OrderByDescending(c => c.Value.Bytes).Take(10))
        {
            string pair = entry.Key.Plugin.FileName.String + " / " + entry.Key.LinkType.Name;
            string took = (entry.Value.Ticks / (double)Stopwatch.Frequency).ToString("F2", CultureInfo.InvariantCulture);
            Emit($"      {pair,-58} {Mb(entry.Value.Bytes),7} MB  largest {Mb(entry.Value.LargestBytes),6} MB  " +
                 $"{entry.Value.Count,7} lookups  {took,7} s");
        }
    }

    private static string FoundLine(Found f) =>
        $"{f.Type} {f.EditorId ?? "(no EditorID)"} [{f.Key}], {f.ChainLength} links from the NPC, depth setting needed on this path {f.DepthNeeded}: {f.Chain}";

    private void EvaluateCandidates(Subject subject, ModInfo mod, INpcGetter record, string npcName, FormKey npcKey,
        Dictionary<(string Roots, int Depth), RunStats> results)
    {
        var defaults = NpcRootFieldCatalog.Defaults;
        var allFields = NpcRootFieldCatalog.AllFields;
        results.TryGetValue(("default", 2), out var today2);
        results.TryGetValue(("default", Unbounded), out var unbounded);

        Emit();
        Emit("  CANDIDATES (default roots unless said otherwise)");
        Emit(RunHeader);

        var typed = Execute(mod, (caches, s, watch) => new TypedWalk(caches, mod.Keys, defaults, null, s, watch).Run(record));
        Emit(RunRow("1 typed", "default", "none", typed));
        Csv("measure", mod.Setting.DisplayName, npcName, npcKey.ToString(), "candidate 1: declared appearance links, no depth", "default", "none", typed);

        var useful = UsefulDeclared(mod, defaults);
        var pruned = Execute(mod, (caches, s, watch) => new TypedWalk(caches, mod.Keys, defaults, useful, s, watch).Run(record));
        Emit(RunRow("1 pruned", "default", "none", pruned));
        Csv("measure", mod.Setting.DisplayName, npcName, npcKey.ToString(), "candidate 1: as above, pruned to the mod's override types", "default", "none", pruned);

        var typedAll = Execute(mod, (caches, s, watch) => new TypedWalk(caches, mod.Keys, allFields, null, s, watch).Run(record));
        Emit(RunRow("1 typed", "all", "none", typedAll));
        Csv("measure", mod.Setting.DisplayName, npcName, npcKey.ToString(), "candidate 1: declared appearance links, no depth", "all", "none", typedAll);

        // "From the mod's list": the plain typed walk is kept under the name 'typedOnly' and the
        // comparisons below are made against this one.
        var typedOnly = typed;
        var known = mod.KnownList;
        typed = Execute(mod, (caches, s, watch) => new TypedWalk(caches, mod.Keys, defaults, null, s, watch, known).Run(record));
        Emit(RunRow("1 list", "default", "none", typed));
        Csv("measure", mod.Setting.DisplayName, npcName, npcKey.ToString(), "candidate 1: from the mod's list (declared links + any link to a record the mod carries)", "default", "none", typed);

        var listAll = Execute(mod, (caches, s, watch) => new TypedWalk(caches, mod.Keys, allFields, null, s, watch, known).Run(record));
        Emit(RunRow("1 list", "all", "none", listAll));
        Csv("measure", mod.Setting.DisplayName, npcName, npcKey.ToString(), "candidate 1: from the mod's list (declared links + any link to a record the mod carries)", "all", "none", listAll);

        Emit("    '1 typed' follows the declared appearance links only; '1 pruned' also drops record types that cannot lead to a");
        Emit("    type the mod overrides; '1 list' tests every link of an opened record against the mod's own override list (no");
        Emit("    lookup), opens what the mod carries, and opens other records through declared appearance links only.");
        Emit($"    1 typed entered, by record type: {Counts(typedOnly.DistinctByType)}");
        Emit($"    1 pruned entered, by record type: {Counts(pruned.DistinctByType)}");
        Emit($"    1 list entered, by record type: {Counts(typed.DistinctByType)}");
        Emit($"    1 list, all roots, entered by record type: {Counts(listAll.DistinctByType)}");
        Emit($"    1 typed found: {typedOnly.Collected.Count} overrides ({Counts(ByType(typedOnly.Collected.Values.Select(f => f.Type)))}); " +
             $"pruned finds the same set: {pruned.Collected.Keys.ToHashSet().SetEquals(typedOnly.Collected.Keys)}");
        Emit($"    1 list found: {typed.Collected.Count} overrides ({Counts(ByType(typed.Collected.Values.Select(f => f.Type)))}); " +
             $"bridge records {typed.BridgeParents.Count} ({Counts(ByType(typed.BridgeParents.Values))}); " +
             $"with all roots {listAll.Collected.Count} overrides ({Counts(ByType(listAll.Collected.Values.Select(f => f.Type)))})");
        Emit("    'candidate 1' below is '1 list'.");

        if (today2 == null)
        {
            Emit("    today's walk at depth 2 was not run; no comparison.");
            return;
        }

        var todaySet = today2.Collected;
        Emit();
        Emit($"  COMPARISON with today's walk at the default depth 2, default roots ({todaySet.Count} overrides: " +
             $"{Counts(ByType(todaySet.Values.Select(f => f.Type)))})");

        void ListMissing(string label, IEnumerable<Found> missing)
        {
            var list = missing.OrderBy(f => f.Type, StringComparer.Ordinal).ThenBy(f => f.Key.ToString(), StringComparer.Ordinal).ToList();
            Emit($"    {label}: {list.Count}");
            foreach (var f in list.Take(25)) Emit("      " + FoundLine(f));
            if (list.Count > 25) Emit($"      ... and {list.Count - 25} more ({Counts(ByType(list.Skip(25).Select(f => f.Type)))})");
        }

        // Candidate 1
        ListMissing("candidate 1 misses, of what today's walk finds", todaySet.Values.Where(f => !typed.Collected.ContainsKey(f.Key)));
        ListMissing("candidate 1 finds, that today's walk at depth 2 does not", typed.Collected.Values.Where(f => !todaySet.ContainsKey(f.Key)));

        // Candidate 2
        if (unbounded == null)
        {
            Emit("    candidate 2 (budget): today's walk with no depth was not run.");
        }
        else
        {
            Emit($"    candidate 2 (today's walk, no depth, stopped at a budget of records entered); the walk with no depth: " +
                 $"{unbounded.Status}, {unbounded.VisitEvents} entered, {unbounded.Collected.Count} overrides " +
                 $"({Counts(ByType(unbounded.Collected.Values.Select(f => f.Type)), 8)})");
            foreach (var budget in Budgets)
            {
                if (unbounded.GuardWasHit && budget > unbounded.VisitEvents)
                {
                    Emit($"      budget {budget,9:N0}: not known (the walk was stopped by a guard at {unbounded.VisitEvents:N0})");
                    continue;
                }
                var within = unbounded.Collected.Values.Where(f => f.Ordinal <= budget).Select(f => f.Key).ToHashSet();
                var missed = todaySet.Values.Where(f => !within.Contains(f.Key)).ToList();
                bool hit = unbounded.VisitEvents > budget;
                Emit($"      budget {budget,9:N0}: {(hit ? "budget hit" : "walk ends before the budget")}; {within.Count} overrides found; " +
                     $"misses {missed.Count} of today's {todaySet.Count}" +
                     (missed.Count > 0 ? " (" + Counts(ByType(missed.Select(f => f.Type)), 6) + ")" : string.Empty));
            }
            ListMissing("with no depth and no budget (as far as the walk got), not found of what today's walk finds",
                todaySet.Values.Where(f => !unbounded.Collected.ContainsKey(f.Key)));
            var extra = unbounded.Collected.Values.Where(f => !todaySet.ContainsKey(f.Key)).ToList();
            Emit($"    with no depth (as far as the walk got), found beyond today's: {extra.Count}" +
                 (extra.Count > 0 ? " (" + Counts(ByType(extra.Select(f => f.Type)), 10) + ")" : string.Empty));
            foreach (var beyond in extra.OrderBy(f => f.Ordinal).Take(8)) Emit($"      at record {beyond.Ordinal:N0}: " + FoundLine(beyond));
        }

        // Candidate 3
        var appearanceTypes = AppearanceRecordTypeCatalog.Resolve(defaults);
        var includeAll = mod.NonNpcOverrides.Where(o => appearanceTypes.Contains(o.Value.GetterType)).Select(o => o.Key).ToHashSet();
        var includeAllEvery = mod.NonNpcOverrides.Select(o => o.Key).ToHashSet();
        Emit($"    candidate 3 ('Include All' + 'Appearance records only'): takes {includeAll.Count} of the mod's " +
             $"{includeAllEvery.Count} non-NPC overrides, for the whole mod and whatever the NPC");
        ListMissing("candidate 3 misses, of what today's walk finds", todaySet.Values.Where(f => !includeAll.Contains(f.Key)));
        Emit($"      of those, found by 'Include All' without the appearance filter: " +
             $"{todaySet.Values.Count(f => !includeAll.Contains(f.Key) && includeAllEvery.Contains(f.Key))}");
        Emit($"      taken by candidate 3 and NOT reached from this NPC by candidate 1: {includeAll.Count(k => !typed.Collected.ContainsKey(k))}");

        if (mod.Overrides.ContainsKey(FixtureArmature))
        {
            string At(int depth) => results.TryGetValue(("default", depth), out var r)
                ? (r.Collected.ContainsKey(FixtureArmature) ? "found" : "missed")
                : "not run";
            Emit($"    the fixture's armature {FixtureArmature}: today's walk depth 1 {At(1)}, 2 {At(2)}, 3 {At(3)}, 4 {At(4)}, 5 {At(5)}, " +
                 $"none {At(Unbounded)}; candidate 1 {(typed.Collected.ContainsKey(FixtureArmature) ? "found" : "missed")}; " +
                 $"candidate 3 {(includeAll.Contains(FixtureArmature) ? "found" : "missed")}");
            if (typed.Collected.TryGetValue(FixtureArmature, out var armature)) Emit("      " + FoundLine(armature));
            if (unbounded != null && unbounded.Collected.TryGetValue(FixtureArmature, out var byBudget))
                Emit($"      with no depth it is the {byBudget.Ordinal:N0}th record entered (the smallest budget that finds it)");
        }
    }

    // ---- Sweep: every NPC of every mod of the staged settings ----

    private sealed class Tally
    {
        public Found First = null!;
        public string ExampleNpc = string.Empty;
        public int Npcs;
    }

    private void Sweep()
    {
        Emit();
        Emit("==========================================================================================");
        Emit("SWEEP: every NPC of every appearance mod in the staged settings, as if selected, each walked alone");
        Emit("  today's walk at the mod's own depth and roots, against candidate 1 (from the mod's list: every link of an opened");
        Emit("  record is tested against the mod's override list, a carried record is opened, any other record is opened through");
        Emit("  a declared appearance link only; no depth).");
        Emit("  One set of link caches per mod (shared by its NPCs), so times are warm and no memory is reported.");
        Emit("  In a real run the NPCs of a mod share one 'already searched' list; here each NPC starts with an empty one.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in _settings.ModSettings)
        {
            if (setting.IsAutoGenerated || setting.CorrespondingModKeys.Count == 0) continue;
            string identity = string.Join("|", setting.CorrespondingModKeys.Select(k => k.FileName.String).OrderBy(n => n, StringComparer.Ordinal).ToList());
            if (!seen.Add(identity)) continue;
            SweepMod(setting);
        }
    }

    private void SweepMod(ModSetting setting)
    {
        var mode = setting.ModRecordOverrideHandlingMode ?? _settings.DefaultRecordOverrideHandlingMode;
        int depth = setting.ModRecordOverrideHandlingMode == null
            ? _settings.DefaultMaxNestedIntervalDepth
            : setting.MaxNestedIntervalDepth;
        var roots = NpcRootFieldCatalog.Resolve(setting, _settings);
        var mod = LoadMod(setting);

        Emit();
        Emit($"MOD '{setting.DisplayName}' (override handling {mode}, depth {depth}, {roots.Count} root fields, {setting.NpcFormKeys.Count} NPCs listed)");
        EmitModFacts(mod);

        var todayUnion = new Dictionary<FormKey, Tally>();
        var typedUnion = new Dictionary<FormKey, Tally>();
        var missed = new Dictionary<FormKey, Tally>();
        var gained = new Dictionary<FormKey, Tally>();
        var lostToOrder = new Dictionary<FormKey, Tally>();
        int npcsWithLoss = 0;
        var todayVisits = new List<long>();
        var typedVisits = new List<long>();
        int walked = 0, noRecord = 0, npcsWithMiss = 0, npcsWithGain = 0, guardHits = 0;
        double todaySeconds = 0, typedSeconds = 0;
        string stopped = string.Empty;

        void Add(Dictionary<FormKey, Tally> into, Found f, string npc)
        {
            if (!into.TryGetValue(f.Key, out var tally))
            {
                tally = new Tally { First = f, ExampleNpc = npc };
                into[f.Key] = tally;
            }
            tally.Npcs++;
        }

        var shared = new CacheSet(_loadOrder, mod.Plugins);
        var known = mod.KnownList;
        var typedOnlyUnion = new HashSet<FormKey>();
        var outer = Execute(mod, (caches, unusedStats, watch) =>
        {
            foreach (var npcKey in setting.NpcFormKeys.OrderBy(k => k.ToString(), StringComparer.Ordinal))
            {
                var stopReason = watch.Reason;
                if (stopReason != null)
                {
                    stopped = stopReason;
                    break;
                }

                var record = ModNpcRecord(mod, npcKey, out _);
                if (record == null)
                {
                    noRecord++;
                    continue;
                }
                walked++;
                string npcName = $"{record.EditorID ?? "(no EditorID)"} [{npcKey}]";
                var rootLinks = NpcRootFieldCatalog.GetRootLinks(record, roots);

                var today = new RunStats();
                var typed = new RunStats();
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    new TodayWalk(caches, mod.Keys, depth, null, today, watch).Run(rootLinks);
                }
                catch (GuardHit)
                {
                    guardHits++;
                }
                long t1 = Stopwatch.GetTimestamp();
                try
                {
                    new TypedWalk(caches, mod.Keys, roots, null, typed, watch, known).Run(record);
                }
                catch (GuardHit)
                {
                    guardHits++;
                }
                long t2 = Stopwatch.GetTimestamp();
                try
                {
                    var typedOnly = new RunStats();
                    new TypedWalk(caches, mod.Keys, roots, null, typedOnly, watch).Run(record);
                    typedOnlyUnion.UnionWith(typedOnly.Collected.Keys);
                }
                catch (GuardHit)
                {
                    guardHits++;
                }
                try
                {
                    var orderFree = new RunStats();
                    new TodayWalk(caches, mod.Keys, depth, null, orderFree, watch, orderIndependent: true).Run(rootLinks);
                    bool anyLost = false;
                    foreach (var f in orderFree.Collected.Values)
                    {
                        if (today.Collected.ContainsKey(f.Key)) continue;
                        Add(lostToOrder, f, npcName);
                        anyLost = true;
                    }
                    if (anyLost) npcsWithLoss++;
                }
                catch (GuardHit)
                {
                    guardHits++;
                }
                todaySeconds += (t1 - t0) / (double)Stopwatch.Frequency;
                typedSeconds += (t2 - t1) / (double)Stopwatch.Frequency;
                todayVisits.Add(today.VisitEvents);
                typedVisits.Add(typed.VisitEvents);

                bool anyMiss = false, anyGain = false;
                foreach (var f in today.Collected.Values)
                {
                    Add(todayUnion, f, npcName);
                    if (!typed.Collected.ContainsKey(f.Key))
                    {
                        Add(missed, f, npcName);
                        anyMiss = true;
                    }
                }
                foreach (var f in typed.Collected.Values)
                {
                    Add(typedUnion, f, npcName);
                    if (!today.Collected.ContainsKey(f.Key))
                    {
                        Add(gained, f, npcName);
                        anyGain = true;
                    }
                }
                if (anyMiss) npcsWithMiss++;
                if (anyGain) npcsWithGain++;
            }
        }, guardSeconds: 420, sharedCaches: shared);

        string Spread(List<long> values)
        {
            if (values.Count == 0) return "(none)";
            values.Sort();
            return $"median {values[values.Count / 2]}, 95th {values[(int)Math.Min(values.Count - 1, 0.95 * values.Count)]}, max {values[^1]}";
        }

        Emit($"  NPCs walked {walked}; listed but with no record in the mod's plugins {noRecord}" +
             (stopped.Length > 0 ? $"; STOPPED EARLY by the guard ({stopped})" : string.Empty) +
             (outer.Status != "ok" ? $"; {outer.Status}" : string.Empty) +
             (guardHits > 0 ? $"; single walks stopped by a guard: {guardHits}" : string.Empty));
        Emit($"  today's walk:  records entered per NPC {Spread(todayVisits)}; {todaySeconds:F1} s in all; " +
             $"distinct overrides over all NPCs {todayUnion.Count} ({Counts(ByType(todayUnion.Values.Select(t => t.First.Type)))})");
        Emit($"  candidate 1:   records entered per NPC {Spread(typedVisits)}; {typedSeconds:F1} s in all; " +
             $"distinct overrides over all NPCs {typedUnion.Count} ({Counts(ByType(typedUnion.Values.Select(t => t.First.Type)))})");
        Emit($"    (candidate 1 = from the mod's list. Following the declared appearance links alone finds {typedOnlyUnion.Count} of those " +
             $"{typedUnion.Count}, and {todayUnion.Keys.Count(k => typedOnlyUnion.Contains(k))} of today's {todayUnion.Count}.)");

        var appearanceTypes = AppearanceRecordTypeCatalog.Resolve(roots);
        var includeAll = mod.NonNpcOverrides.Where(o => appearanceTypes.Contains(o.Value.GetterType)).Select(o => o.Key).ToHashSet();
        Emit($"  candidate 3 would take {includeAll.Count} overrides for the whole mod; of today's {todayUnion.Count} it misses " +
             $"{todayUnion.Keys.Count(k => !includeAll.Contains(k))} " +
             $"({Counts(ByType(todayUnion.Where(t => !includeAll.Contains(t.Key)).Select(t => t.Value.First.Type)))}); " +
             $"it takes {includeAll.Count(k => !typedUnion.ContainsKey(k))} that candidate 1 reaches from none of the mod's NPCs");

        void ListTallies(string label, Dictionary<FormKey, Tally> tallies, int npcs)
        {
            Emit($"  {label}: {tallies.Count} distinct records, on {npcs} NPCs" +
                 (tallies.Count > 0 ? $" ({Counts(ByType(tallies.Values.Select(t => t.First.Type)))})" : string.Empty));
            foreach (var tally in tallies.Values.OrderBy(t => t.First.Type, StringComparer.Ordinal).ThenByDescending(t => t.Npcs).Take(30))
            {
                Emit($"    on {tally.Npcs} NPCs, e.g. {tally.ExampleNpc}: {FoundLine(tally.First)}");
            }
            if (tallies.Count > 30) Emit($"    ... and {tallies.Count - 30} more");
        }

        ListTallies($"today's walk LOSES TO ITS OWN VISITING ORDER (found at depth {depth} when a record may be opened again from a shorter path)",
            lostToOrder, npcsWithLoss);
        ListTallies("candidate 1 MISSES, of what today's walk finds for the same NPC", missed, npcsWithMiss);
        ListTallies("candidate 1 FINDS, that today's walk does not for the same NPC", gained, npcsWithGain);
        if (gained.Count > 0)
        {
            Emit($"    depth setting today's walk would need for those, along the path found: " +
                 Counts(ByType(gained.Values.Select(t => "depth " + t.First.DepthNeeded))));
        }

        Csv("sweep", setting.DisplayName, $"{walked} NPCs", string.Empty, "today", $"{roots.Count} fields", depth.ToString(), null,
            $"overrides {todayUnion.Count}; entered per NPC {Spread(todayVisits)}; {todaySeconds:F1} s");
        Csv("sweep", setting.DisplayName, $"{walked} NPCs", string.Empty, "candidate 1", $"{roots.Count} fields", "none", null,
            $"overrides {typedUnion.Count}; entered per NPC {Spread(typedVisits)}; {typedSeconds:F1} s; " +
            $"misses {missed.Count} on {npcsWithMiss} NPCs; finds {gained.Count} more on {npcsWithGain} NPCs");
        GC.KeepAlive(shared);
    }
}
