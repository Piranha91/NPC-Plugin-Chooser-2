using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.Memory;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Views;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Benchmark;

/// <summary>
/// Opt-in, local-only benchmark of NPC2's startup mod discovery/population against a real Mod Organizer 2
/// instance (default <c>S:\Skyrim NPC Selection</c>, profile <c>NPC Test</c>), comparing the legacy and the
/// targeted FaceGen discovery over identical inputs. <b>One measured scenario per process</b>: the driver
/// (<c>Tests/Benchmark/run_discovery_benchmark.py</c>) launches a fresh test host for every run, so caches
/// held in statics (<c>PluginArchiveIndex</c>, the archive listing cache, the race cache) start empty unless
/// the scenario seeds them from disk.
///
/// <para>Inert (prints SKIP and passes) unless <c>NPC2_DISCOVERY_BENCH=1</c>. When enabled it FAILS — rather
/// than silently skipping — if the configured instance cannot be resolved, so a green benchmark run always
/// means the real environment was exercised.</para>
///
/// <para>Environment variables: <c>NPC2_BENCH_VARIANT</c> (<c>legacy</c>|<c>optimized</c>),
/// <c>NPC2_BENCH_SCENARIO</c> (<c>cold</c>|<c>warm</c>|<c>refresh-all</c>|<c>refresh-mod</c>),
/// <c>NPC2_BENCH_OUT</c> (result directory), <c>NPC2_BENCH_RUN</c> (run label),
/// <c>NPC2_BENCH_INSTANCE</c>, <c>NPC2_BENCH_PROFILE</c>, <c>NPC2_BENCH_MODS_FOLDER</c> (override the mods
/// folder, for the disposable fixture subset), <c>NPC2_BENCH_REFRESH_MOD</c> (display name the
/// <c>refresh-mod</c> scenario refreshes), <c>NPC2_BENCH_MUTATE</c> (a command file the test applies
/// between population and the refresh), <c>NPC2_BENCH_MANIFEST=1</c> (write the equivalence manifest after
/// the measurement) and <c>NPC2_BENCH_SAVE_SETTINGS</c> (persist the populated settings, to seed the warm
/// scenario). Settings are read from <c>Settings.json</c> next to the test assembly via the app's own
/// <c>VM_Settings.LoadSettings</c> (the driver stages a copy there), and every cache file the app writes
/// lands next to the test assembly, which the driver keeps in an isolated benchmark directory.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class DiscoveryBenchmark
{
    private readonly WpfStaFixture _sta;
    private readonly ITestOutputHelper _out;

    public DiscoveryBenchmark(WpfStaFixture sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    [Fact]
    public async Task RunConfiguredScenario()
    {
        if (Env("NPC2_DISCOVERY_BENCH", "0") != "1")
        {
            _out.WriteLine("SKIP: discovery benchmark not enabled (set NPC2_DISCOVERY_BENCH=1).");
            return;
        }

        var variant = Env("NPC2_BENCH_VARIANT", "optimized");
        var scenario = Env("NPC2_BENCH_SCENARIO", "cold");
        var outDir = Env("NPC2_BENCH_OUT", Path.Combine(AppContext.BaseDirectory, "BenchmarkResults"));
        var runLabel = Env("NPC2_BENCH_RUN", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff"));
        var instance = Env("NPC2_BENCH_INSTANCE", @"S:\Skyrim NPC Selection");
        var profile = Env("NPC2_BENCH_PROFILE", "NPC Test");
        Directory.CreateDirectory(outDir);
        Assert.True(variant is "legacy" or "optimized", $"Unknown NPC2_BENCH_VARIANT '{variant}'");
        Assert.True(scenario is "cold" or "warm" or "refresh-all" or "refresh-mod",
            $"Unknown NPC2_BENCH_SCENARIO '{scenario}'");

        var settings = VM_Settings.LoadSettings();
        var modsFolder = Env("NPC2_BENCH_MODS_FOLDER", settings.ModsFolder);
        settings.ModsFolder = modsFolder;
        Assert.True(Directory.Exists(modsFolder), $"Mods folder '{modsFolder}' does not exist");
        if (Environment.GetEnvironmentVariable("NPC2_BENCH_MODS_FOLDER") == null)
        {
            Assert.True(Path.GetFullPath(modsFolder).StartsWith(Path.GetFullPath(instance), StringComparison.OrdinalIgnoreCase),
                $"Settings ModsFolder '{modsFolder}' is not inside the benchmark instance '{instance}'");
        }
        ApplyBenchmarkToggles(settings);
        // refresh-mod runs against the disposable fixture subset, so it must not inherit entries that point
        // into the real instance; it starts from empty discovery caches like the cold scenario.
        if (scenario is "cold" or "refresh-mod") ClearDiscoveryCaches(settings);

        var envWatch = Stopwatch.StartNew();
        Assert.True(Mo2ProfileEnvironment.TryBuild(instance, profile, settings.OutputPluginName, out var mo2, out var failure),
            failure);
        envWatch.Stop();

        FaceGenDiscovery.UseLegacyImplementation = variant == "legacy";
        var dialogs = new List<string>();
        var result = new JObject
        {
            ["run"] = runLabel,
            ["variant"] = variant,
            ["scenario"] = scenario,
            ["instance"] = instance,
            ["profile"] = profile,
            ["dataFolder"] = mo2!.GameDataFolder,
            ["modsFolder"] = modsFolder,
            ["loadOrderCount"] = mo2.Provider.LoadOrder!.ListedOrder.Count(),
            ["unresolvedProfilePlugins"] = new JArray(mo2.UnresolvedPlugins.Select(k => k.FileName.String)),
            ["modSettingsAtStart"] = settings.ModSettings.Count,
            ["cachedNonAppearanceAtStart"] = settings.CachedNonAppearanceMods.Count,
            ["environmentBuildMs"] = envWatch.Elapsed.TotalMilliseconds,
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["serverGc"] = GCSettings.IsServerGC,
            ["debuggerAttached"] = Debugger.IsAttached,
#if DEBUG
            ["configuration"] = "Debug",
#else
            ["configuration"] = "Release",
#endif
        };

        await _sta.RunOnStaAsync(async () =>
        {
            using var _ = new StaticStateGuard(immediateSchedulers: false);
            FrontendVmHarness.InstallStaMainThreadScheduler();
            using var closer = MessageBoxAutoCloser.Install(dialogs);
            using var harness = new FrontendVmHarness(mo2.Provider, settings);

            if (scenario is "cold" or "warm")
            {
                var m = await MeasureAsync(() => harness.DriveStartupPopulationAsync());
                result["measured"] = m;
            }
            else
            {
                // Refresh scenarios: populate first (not measured), then measure the refresh.
                await harness.DriveStartupPopulationAsync();
                result["initialPopulationNpcs"] = harness.NpcSelectionBar.AllNpcs.Count;
                if (Environment.GetEnvironmentVariable("NPC2_BENCH_MUTATE") is { Length: > 0 } mutateFile)
                {
                    result["mutations"] = FixtureMutations.Apply(mutateFile);
                }

                if (scenario == "refresh-all")
                {
                    var splash = new VM_SplashScreen(App.ProgramVersion);
                    result["measured"] = await MeasureAsync(() => harness.ModsVm.RefreshAllModSettingsAsync(splash));
                }
                else
                {
                    var name = Env("NPC2_BENCH_REFRESH_MOD", "");
                    var target = harness.ModsVm.AllModSettings.FirstOrDefault(v =>
                        v.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    Assert.True(target != null, $"refresh-mod target '{name}' not found after population");
                    // Captured before the refresh: the mutation was applied above, so a refresh that misses it
                    // (e.g. a stale archive listing) shows up as before == after.
                    result["targetBefore"] = DescribeEntry(target!);
                    (bool Success, string Reason) refreshResult = default;
                    result["measured"] = await MeasureAsync(async () =>
                        refreshResult = await harness.ModsVm.RefreshSingleModSettingAsync(target!));
                    result["refreshSucceeded"] = refreshResult.Success;
                    result["refreshFailureReason"] = refreshResult.Reason;
                    result["targetAfter"] = DescribeEntry(target!);
                }
            }

            result["modSettingsInMemory"] = harness.ModsVm.AllModSettings.Count;
            result["modSettingsPersisted"] = settings.ModSettings.Count;
            result["npcCatalogueCount"] = harness.NpcSelectionBar.AllNpcs.Count;
            result["dialogs"] = new JArray(dialogs);

            // Everything below is outside the measured region.
            if (Env("NPC2_BENCH_MANIFEST", "0") == "1")
            {
                var manifestPath = Path.Combine(outDir, $"manifest-{scenario}-{variant}-{runLabel}.json");
                await DiscoveryManifest.WriteAsync(manifestPath, harness, settings, dialogs);
                result["manifest"] = manifestPath;
            }
        });

        if (Environment.GetEnvironmentVariable("NPC2_BENCH_SAVE_SETTINGS") is { Length: > 0 } savePath)
        {
            JSONhandler<Settings>.SaveJSONFile(settings, savePath, out bool ok, out string ex);
            Assert.True(ok, ex);
        }

        var resultPath = Path.Combine(outDir, $"result-{scenario}-{variant}-{runLabel}.json");
        await File.WriteAllTextAsync(resultPath, result.ToString(Formatting.Indented));
        _out.WriteLine(result.ToString(Formatting.Indented));
    }

    /// <summary>
    /// Runs <paramref name="body"/> once and returns process-level costs: wall clock, process CPU time,
    /// managed bytes allocated on every thread (<see cref="GC.GetTotalAllocatedBytes(bool)"/> is
    /// process-wide, so parallel workers are included), GC counts, the process peak working set, plus the
    /// opt-in <see cref="DiscoveryMetrics"/> counters and phase timers. A full blocking GC before the start
    /// keeps garbage from environment construction out of the measured allocation window.
    /// </summary>
    private static async Task<JObject> MeasureAsync(Func<Task> body)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var cpu0 = proc.TotalProcessorTime;
        var ws0 = proc.WorkingSet64;
        var alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var gc0 = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        DiscoveryMetrics.Reset();
        DiscoveryMetrics.Enabled = true;

        var sw = Stopwatch.StartNew();
        await body();
        sw.Stop();

        DiscoveryMetrics.Enabled = false;
        var alloc1 = GC.GetTotalAllocatedBytes(precise: true);
        proc.Refresh();
        var cpu1 = proc.TotalProcessorTime;
        var phases = new JObject();
        foreach (var (name, (ms, entries)) in DiscoveryMetrics.SnapshotPhases())
        {
            phases[name] = new JObject { ["ms"] = ms, ["entries"] = entries };
        }
        var work = new JObject();
        foreach (var (name, (ms, entries)) in DiscoveryMetrics.SnapshotWork())
        {
            work[name] = new JObject { ["summedWorkerMs"] = ms, ["entries"] = entries };
        }

        return new JObject
        {
            ["wallMs"] = sw.Elapsed.TotalMilliseconds,
            ["cpuMs"] = (cpu1 - cpu0).TotalMilliseconds,
            ["allocatedBytes"] = alloc1 - alloc0,
            ["gen0"] = GC.CollectionCount(0) - gc0[0],
            ["gen1"] = GC.CollectionCount(1) - gc0[1],
            ["gen2"] = GC.CollectionCount(2) - gc0[2],
            ["workingSetBeforeBytes"] = ws0,
            ["workingSetAfterBytes"] = proc.WorkingSet64,
            ["peakWorkingSetBytes"] = proc.PeakWorkingSet64,
            ["peakPagedMemoryBytes"] = proc.PeakPagedMemorySize64,
            ["counters"] = JObject.FromObject(DiscoveryMetrics.SnapshotCounters()),
            ["phases"] = phases,
            ["scanWorkSteps"] = work,
        };
    }

    private static JObject DescribeEntry(VM_ModSetting vm) => new()
    {
        ["npcFormKeys"] = vm.NpcFormKeys.Count,
        ["faceGenOnlyNpcFormKeys"] = vm.FaceGenOnlyNpcFormKeys.Count,
        ["faceGenOnlySample"] = new JArray(vm.FaceGenOnlyNpcFormKeys.Select(k => k.ToString()).OrderBy(k => k).Take(5)),
        ["isFaceGenOnlyEntry"] = vm.IsFaceGenOnlyEntry,
    };

    /// <summary>
    /// Holds network acquisition, portrait generation and verbose logging off for both variants alike, so
    /// they cannot obscure discovery cost. These are user-facing toggles in the copied settings; the
    /// benchmark never writes them back to the user's own Settings.json.
    /// </summary>
    private static void ApplyBenchmarkToggles(Settings settings)
    {
        settings.UseFaceFinderFallback = false;
        settings.CacheFaceFinderImages = false;
        settings.UsePortraitCreatorFallback = false;
        settings.AutoUpdateOldMugshots = false;
        settings.AutoUpdateStaleMugshots = false;
        settings.AutoUpdateMugshotsWithMissingAssets = false;
        settings.AutoUpdateMugshotsWithMissingOutfitAssets = false;
        settings.EnableLiveTilesOnSelection = false;
        settings.ShowNpcDescriptions = false;
        settings.EnableFaceGenAnalysis = false;
        settings.LogStartup = false;
        settings.LogPerformance = false;
        settings.LogVerbose = false;
        settings.LogActivity = false;
    }

    /// <summary>
    /// "Empty NPC2 discovery caches": forget every discovered mod entry (and with it every persisted
    /// analysis snapshot), the non-appearance / missing-master folder caches, and the on-disk race and
    /// mod-issue caches next to the (isolated) test assembly. User choices that are not caches — mods
    /// folder, ignored mods, selections — are kept.
    /// </summary>
    private static void ClearDiscoveryCaches(Settings settings)
    {
        settings.ModSettings.Clear();
        settings.CachedNonAppearanceMods.Clear();
        settings.CachedMissingMasterMods.Clear();
        foreach (var file in new[] { Auxilliary.RaceValidityCacheFileName, ModIssuesCache.CacheFileName })
        {
            var path = Path.Combine(AppContext.BaseDirectory, file);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Closes every <see cref="ScrollableMessageBox"/> the population shows (its end-of-population warning
    /// summary would otherwise block the headless STA thread on a modal dialog) and records its text.
    /// </summary>
    private sealed class MessageBoxAutoCloser : IDisposable
    {
        private static List<string>? _sink;
        private static bool _registered;

        public static MessageBoxAutoCloser Install(List<string> sink)
        {
            _sink = sink;
            if (!_registered)
            {
                EventManager.RegisterClassHandler(typeof(ScrollableMessageBox), FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnLoaded));
                _registered = true;
            }
            return new MessageBoxAutoCloser();
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Window window) return;
            var texts = FindTexts(window).Where(t => !string.IsNullOrWhiteSpace(t));
            _sink?.Add(window.Title + ": " + string.Join(" | ", texts));
            window.Dispatcher.BeginInvoke(() => { if (window.IsVisible) window.Close(); });
        }

        private static IEnumerable<string> FindTexts(DependencyObject node)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                if (child is TextBlock tb) yield return tb.Text;
                if (child is TextBox tx) yield return tx.Text;
                foreach (var t in FindTexts(child)) yield return t;
            }
        }

        public void Dispose() => _sink = null;
    }
}
