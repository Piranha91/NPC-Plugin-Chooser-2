using System.IO;
using CharacterViewer.Rendering;
using Autofac;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Memory;

/// <summary>
/// Opt-in, local-only <em>measurement</em> of the three CharacterViewer.Rendering in-RAM decode caches'
/// natural working-set ratio, to validate (or refute) the configured pixel:mesh:cubemap budget split
/// in <c>SystemMemoryBudget</c> (50:25:10 as originally designed; 75:9:1 after the measurement-driven retune). Drives ~50 diverse NPCs through the exact prewarm path the offscreen
/// renderer uses before each mugshot render (<c>GameAssetResolver.PushScopes</c> +
/// <c>CharacterPreviewCache.PrewarmNpc</c>, including attire/headgear mesh overrides) — prewarm performs the
/// full NIF parse + DDS/cubemap decode with NO GL work, so this runs under the test host where the real
/// GLFW renderer cannot (it pins to the process main thread; see <see cref="MugshotAcquisitionMemoryDiagnostic"/>).
///
/// <para>Peaks/inserts/evictions are collected by <see cref="CacheBudgetDiag"/> (enabled programmatically
/// here; in the app it's the <c>LogCacheBudgetDiag.txt</c> trigger file) and written per-NPC plus as a final
/// ratio summary to <c>MemoryReports/cache-ratio-prewarm.log</c> next to the test assembly. Diversity comes
/// from sorting all resolvable NPCs by <see cref="Auxilliary.NpcGroupingKey"/> (sex → race → worn armor →
/// head parts → hair) and stride-sampling across that order, so the picked set spans sexes, races, and
/// outfit complexity rather than 50 adjacent bandits.</para>
///
/// <para><b>Enabling:</b> inert (passing no-op) unless <c>NPC2_CACHE_RATIO_DIAG=1</c> is set or a
/// <c>RunCacheRatioDiag.txt</c> file exists next to the test assembly. Graceful-skips without a Skyrim SE
/// install. This is a measurement tool: the only assertions are backstops that the run actually exercised
/// the caches.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class CacheRatioMeasurementDiagnostic
{
    /// <summary>Default sample size; override with the NPC2_CACHE_RATIO_COUNT env var
    /// for a bigger (or quicker) run. Runtime and pixel-cache footprint scale roughly
    /// linearly with this (sub-linearly once cross-NPC texture sharing kicks in).</summary>
    private const int DefaultTargetNpcCount = 50;

    private static int TargetNpcCount =>
        int.TryParse(Environment.GetEnvironmentVariable("NPC2_CACHE_RATIO_COUNT"), out int n) && n > 0
            ? n : DefaultTargetNpcCount;

    private readonly WpfStaFixture _sta;
    private readonly ITestOutputHelper _out;

    public CacheRatioMeasurementDiagnostic(WpfStaFixture sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    private static bool DiagEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("NPC2_CACHE_RATIO_DIAG"), "1", StringComparison.Ordinal)
        || File.Exists(Path.Combine(AppContext.BaseDirectory, "RunCacheRatioDiag.txt"));

    [Fact]
    public async Task PrewarmWorkingSet_MeasuresCacheRatioOver50Npcs()
    {
        if (!DiagEnabled())
        {
            _out.WriteLine("SKIP: cache-ratio diagnostic not enabled. " +
                           "Set NPC2_CACHE_RATIO_DIAG=1 or drop RunCacheRatioDiag.txt next to the test assembly.");
            return;
        }
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip))
        {
            _out.WriteLine("SKIP: " + skip);
            return;
        }

        // Default cache config = PercentFreeRam at the 85% baseline — the natural-working-set
        // measurement condition the task calls for (budgets should not be the binding constraint).
        var settings = new Settings { SkyrimRelease = SkyrimRelease.SkyrimSE };

        var reportDir = Path.Combine(AppContext.BaseDirectory, "MemoryReports");
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, "cache-ratio-prewarm.log");
        var report = new StreamWriter(reportPath, append: false) { AutoFlush = true };
        await using var _report = report;
        report.WriteLine("# Cache-ratio measurement: prewarm working set over diverse NPCs");
        report.WriteLine($"# mode=PercentFreeRam freeRamPercent=85 (baseline) target={TargetNpcCount} NPCs");

        await _sta.RunOnStaAsync(async () =>
        {
            using var _ = new StaticStateGuard(immediateSchedulers: false);
            FrontendVmHarness.InstallStaMainThreadScheduler();
            using var harness = new FrontendVmHarness(env!.Provider, settings);
            await harness.DriveStartupPopulationAsync();

            var resolver = harness.Container.Resolve<NpcMeshResolver>();
            var assetResolver = harness.Container.Resolve<GameAssetResolver>();
            var cache = harness.Container.Resolve<CharacterPreviewCache>();
            var bsa = harness.Container.Resolve<IBsaArchiveProvider>();
            var linkCache = harness.Environment.LinkCache;
            linkCache.Should().NotBeNull();

            // Work list: every NPC of every populated mod entry, sorted by the same grouping key the
            // "Generate All Mugshots" batch uses. Sorting groups similar NPCs; stride-sampling across
            // the sorted order then maximizes diversity of (sex, race, armor, head parts).
            var work = new List<(ModSetting Mod, FormKey Npc, string Display, Auxilliary.NpcGroupingKey Key)>();
            foreach (var modVm in harness.ModsVm.AllModSettings)
            {
                if (modVm.NpcFormKeysToDisplayName == null || modVm.NpcFormKeysToDisplayName.Count == 0) continue;
                var model = settings.ModSettings.FirstOrDefault(m => m.DisplayName == modVm.DisplayName);
                if (model == null) continue;
                foreach (var (fk, displayName) in modVm.NpcFormKeysToDisplayName)
                {
                    if (!linkCache!.TryResolve<INpcGetter>(fk, out var npcGetter) || npcGetter == null) continue;
                    work.Add((model, fk, displayName, Auxilliary.BuildNpcGroupingKey(npcGetter)));
                }
            }
            _out.WriteLine($"Work list: {work.Count} resolvable NPCs across {harness.ModsVm.AllModSettings.Count} mod entries.");
            report.WriteLine($"# work list: {work.Count} resolvable NPCs");
            if (work.Count == 0)
            {
                _out.WriteLine("SKIP: no resolvable NPCs — nothing to measure.");
                return;
            }
            work.Sort((a, b) => a.Key.CompareTo(b.Key));

            // Open BSA readers once up front (mirrors InternalMugshotGenerator.GenerateAsync) so archive
            // opening isn't interleaved with the measured decode work.
            await Task.Run(() => bsa.EnsureAllArchivesOpened());

            CacheBudgetDiag.Enabled = true;
            CacheBudgetDiag.Reset();

            var cfg = settings.InternalMugshot;
            int done = 0, attempted = 0, noFaceGen = 0, noResolve = 0;
            // Candidate order matters for diversity: the primary pass is TargetNpcCount indices evenly
            // spaced across the FULL sorted range (so the first 50 successes already span both sexes and
            // all races); skip replacements come from a second pass offset by half a stride, again spread
            // across the whole range, rather than from the front.
            double stride = Math.Max(1.0, work.Count / (double)TargetNpcCount);
            var candidateIndices = new List<int>();
            for (double i = 0; i < work.Count && candidateIndices.Count < TargetNpcCount; i += stride)
                candidateIndices.Add((int)i);
            for (double i = stride / 2; i < work.Count && candidateIndices.Count < TargetNpcCount * 2; i += stride)
                candidateIndices.Add((int)i);

            foreach (int idx in candidateIndices)
            {
                if (done >= TargetNpcCount) break;
                var (mod, npc, display, key) = work[idx];
                attempted++;

                // Mirror InternalMugshotGenerator.GenerateAsync minus the GL render: resolve mesh paths,
                // skip templated NPCs (no exported FaceGen), resolve attire/headgear overrides (both ON to
                // exercise outfit meshes/textures — the mesh-cache stressor), then prewarm under the same
                // per-mod resolution scopes the renderer pushes.
                var paths = resolver.Resolve(npc, mod);
                if (paths == null) { noResolve++; continue; }
                if (!resolver.FaceGenExists(npc, mod)) { noFaceGen++; continue; }

                var overrides = resolver.ResolveAttireMeshOverrides(
                    npc, mod, includeDefaultOutfit: true, includeHeadgear: true);

                await Task.Run(() =>
                {
                    using var scopes = assetResolver.PushScopes(
                        resolver.BuildResolutionScopes(mod), null,
                        cfg.VanillaLooseOverridesBsa, cfg.VanillaLooseOverridesModLoose);
                    cache.PrewarmNpc(paths, overrides.Count > 0 ? overrides : null);
                });

                done++;
                string line = $"[{done,2}] {display} ({npc}) sex={(key.IsFemale ? "F" : "M")} race={key.Race} " +
                              $"armor={(string.IsNullOrEmpty(key.WornArmor) ? "-" : key.WornArmor)}";
                _out.WriteLine(line);
                _out.WriteLine("     " + CacheBudgetDiag.SummaryLine());
                report.WriteLine(line);
                report.WriteLine("     " + CacheBudgetDiag.SummaryLine());
            }

            var pixel = CacheBudgetDiag.Pixel;
            var mesh = CacheBudgetDiag.Mesh;
            var cubemap = CacheBudgetDiag.Cubemap;

            static double Mb(long b) => b / (1024.0 * 1024.0);
            long peakSum = pixel.PeakBytes + mesh.PeakBytes + cubemap.PeakBytes;
            long demandSum = pixel.InsertedBytes + mesh.InsertedBytes + cubemap.InsertedBytes;

            string summary =
                $"\n=== SUMMARY ({done} NPCs prewarmed, {attempted} attempted, {noResolve} unresolvable, {noFaceGen} no-FaceGen) ===\n" +
                $"pixel   peak={Mb(pixel.PeakBytes):F1}MB inserted={Mb(pixel.InsertedBytes):F1}MB ({pixel.InsertCount} entries) " +
                $"evicted={Mb(pixel.EvictedBytes):F1}MB budget={Mb(pixel.BudgetBytes):F0}MB clamped={pixel.EverBudgetClamped}\n" +
                $"mesh    peak={Mb(mesh.PeakBytes):F1}MB inserted={Mb(mesh.InsertedBytes):F1}MB ({mesh.InsertCount} entries) " +
                $"evicted={Mb(mesh.EvictedBytes):F1}MB (countCap: {mesh.CountCapEvictionCount} of {mesh.EvictionCount}) " +
                $"budget={Mb(mesh.BudgetBytes):F0}MB clamped={mesh.EverBudgetClamped}\n" +
                $"cubemap peak={Mb(cubemap.PeakBytes):F1}MB inserted={Mb(cubemap.InsertedBytes):F1}MB ({cubemap.InsertCount} entries) " +
                $"evicted={Mb(cubemap.EvictedBytes):F1}MB budget={Mb(cubemap.BudgetBytes):F0}MB clamped={cubemap.EverBudgetClamped}\n" +
                (peakSum > 0
                    ? $"peak ratio     pixel:mesh:cubemap = {100.0 * pixel.PeakBytes / peakSum:F1} : {100.0 * mesh.PeakBytes / peakSum:F1} : {100.0 * cubemap.PeakBytes / peakSum:F1} (% of sum)\n"
                    : "") +
                (demandSum > 0
                    ? $"demand ratio   pixel:mesh:cubemap = {100.0 * pixel.InsertedBytes / demandSum:F1} : {100.0 * mesh.InsertedBytes / demandSum:F1} : {100.0 * cubemap.InsertedBytes / demandSum:F1} (% of cumulative inserts)\n"
                    : "") +
                $"configured ratio pixel:mesh:cubemap = {100.0 * 0.75 / 0.85:F1} : {100.0 * 0.09 / 0.85:F1} : {100.0 * 0.01 / 0.85:F1} (75:9:1 normalized; pre-retune was 50:25:10 = 58.8 : 29.4 : 11.8)";

            _out.WriteLine(summary);
            report.WriteLine(summary);
            _out.WriteLine($"Report: {reportPath}");

            // Backstops only — this is a measurement, not a regression gate.
            done.Should().BeGreaterThan(10, "the measurement needs a meaningful NPC sample to be interpretable");
            pixel.InsertCount.Should().BeGreaterThan(0, "prewarm must have decoded textures");
            mesh.InsertCount.Should().BeGreaterThan(0, "prewarm must have parsed NIFs");
        });
    }
}
