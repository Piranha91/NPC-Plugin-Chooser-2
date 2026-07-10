using System.IO;
using FluentAssertions;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Memory;

/// <summary>
/// Opt-in, local-only memory <em>measurement</em> for the mugshot-acquisition conditions that can't run in
/// CI — real GPU rendering (auto-generation) and live-network FaceFinder — plus the curated-mugshot browse
/// baseline. Unlike <see cref="NpcBrowseMemoryTests"/> (a deterministic reachability gate), these drive a
/// long browse loop through the real <see cref="VM_NpcSelectionBar"/> under each condition and write
/// managed-heap + working-set bytes per iteration to <c>MemoryReports/*.log</c> next to the test assembly,
/// so a human can compare the growth curve against a user's long-session report. A generous absolute ceiling
/// is asserted only as a backstop — the report is the real product.
///
/// <para><b>Enabling:</b> these are inert (a passing no-op) unless opted in, because the render mode needs a
/// GPU on the WPF UI thread and the FaceFinder mode hits the network. Opt in with the env var
/// <c>NPC2_MEMORY_DIAG=1</c> or by dropping a <c>RunMemoryDiag.txt</c> file next to the test assembly
/// (mirrors the app's file-trigger diagnostics convention). All modes also graceful-skip without a Skyrim SE
/// install. Point curated mugshots at <c>S:\Skyrim Mugshots</c> (auto-detected when present).</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class MugshotAcquisitionMemoryDiagnostic
{
    private const string CuratedMugshotsFolder = @"S:\Skyrim Mugshots";

    // How many NPCs to browse through, and how often to sample memory. Kept modest so an opted-in local run
    // finishes in a few minutes; raise locally to chase a slow leak over a longer session.
    private const int BrowseIterations = 120;
    private const int SampleEvery = 10;

    // Backstop ceiling on managed-heap growth across the loop. Deliberately generous: this is a measurement
    // tool, not a tight regression. A real unbounded leak blows well past this; normal caches don't.
    private const double MaxGcGrowthMiB = 400.0;

    private readonly WpfStaFixture _sta;
    private readonly ITestOutputHelper _out;

    public MugshotAcquisitionMemoryDiagnostic(WpfStaFixture sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    private static bool DiagEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("NPC2_MEMORY_DIAG"), "1", StringComparison.Ordinal)
        || File.Exists(Path.Combine(AppContext.BaseDirectory, "RunMemoryDiag.txt"));

    [Fact]
    public Task CuratedBrowse_MemoryOverManyNpcs() =>
        RunConditionAsync("curated-browse", s =>
        {
            if (Directory.Exists(CuratedMugshotsFolder)) s.MugshotsFolder = CuratedMugshotsFolder;
        }, useRealRenderer: false);

    [Fact]
    public Task RenderAutogen_MemoryOverManyNpcs() =>
        RunConditionAsync("render-autogen", s =>
        {
            if (Directory.Exists(CuratedMugshotsFolder)) s.MugshotsFolder = CuratedMugshotsFolder;
        }, useRealRenderer: true);

    [Fact]
    public Task FaceFinder_MemoryOverManyNpcs() =>
        RunConditionAsync("facefinder", s =>
        {
            s.UseFaceFinderFallback = true;
            s.CacheFaceFinderImages = true;
        }, useRealRenderer: false);

    private async Task RunConditionAsync(string label, Action<Settings> configure, bool useRealRenderer)
    {
        if (!DiagEnabled())
        {
            _out.WriteLine($"SKIP ({label}): memory diagnostic not enabled. " +
                           "Set NPC2_MEMORY_DIAG=1 or drop RunMemoryDiag.txt next to the test assembly.");
            return;
        }
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip))
        {
            _out.WriteLine($"SKIP ({label}): " + skip);
            return;
        }

        var settings = new Settings { SkyrimRelease = SkyrimRelease.SkyrimSE };
        configure(settings);

        await _sta.RunOnStaAsync(async () =>
        {
            using var _ = new StaticStateGuard(immediateSchedulers: false);
            FrontendVmHarness.InstallStaMainThreadScheduler();
            using var harness = new FrontendVmHarness(env!.Provider, settings, useRealRenderer);

            // Resolve the renderer eagerly on the UI thread (GLFW constraint) so its one-time allocation
            // isn't attributed to the first browse iteration's delta.
            //
            // NOTE: the real offscreen renderer initializes GLFW, which OpenTK pins to the process MAIN
            // thread. In the running app that is the WPF UI thread, so it works; under the xUnit test host
            // the main thread belongs to the runner and this harness drives the UI on a dedicated STA
            // thread, so GLFW throws "GLFW can only be called from the main thread!". That means the
            // real-renderer (GPU) memory profile can only be captured from the running app, not from here.
            // Skip gracefully with that guidance rather than failing the run.
            if (useRealRenderer)
            {
                try
                {
                    harness.EnsureRendererResolved();
                }
                catch (Exception ex)
                {
                    _out.WriteLine($"SKIP ({label}): real renderer unavailable under the test host " +
                                   $"({ex.GetBaseException().Message}). The GPU render path pins GLFW to the " +
                                   "process main thread; capture this profile from the running app instead " +
                                   "(browse many NPCs with the Internal renderer and watch memory).");
                    return;
                }
            }

            await harness.DriveStartupPopulationAsync();
            var bar = harness.NpcSelectionBar;

            var report = new MemoryProbe.MemoryReport($"mugshot-memory-{label}");
            report.Record(0); // baseline after population, before browsing

            int iterations = 0, scanned = 0, tiled = 0;
            const int maxScan = 3000;
            foreach (var npc in bar.AllNpcs)
            {
                if (iterations >= BrowseIterations || scanned >= maxScan) break;
                scanned++;

                var current = await harness.SelectAndWaitAsync(npc);
                if ((current?.Count ?? 0) == 0) { current = null; continue; }
                current = null; // don't pin the browsed collection

                tiled++;
                iterations++;
                if (iterations % SampleEvery == 0)
                {
                    report.Record(iterations);
                    _out.WriteLine($"[{label}] iter {iterations}: browsed {npc.DisplayName}");
                }
            }

            report.Record(iterations); // final sample

            _out.WriteLine($"[{label}] browsed {tiled} tiled NPCs of {scanned} scanned; report: {report.Path}");
            _out.WriteLine($"[{label}] managed-heap growth: {report.GcGrowthMiB():F1} MiB, " +
                           $"working-set growth: {report.WorkingSetGrowthMiB():F1} MiB");

            if (iterations < SampleEvery)
            {
                _out.WriteLine($"[{label}] too few tiled NPCs ({iterations}) to judge a trend — measurement only.");
                return;
            }

            report.GcGrowthMiB().Should().BeLessThan(MaxGcGrowthMiB,
                $"[{label}] managed heap grew {report.GcGrowthMiB():F1} MiB over {iterations} NPC selections — " +
                "far past the backstop ceiling suggests an unbounded per-NPC leak (see the report log)");
        });
    }
}
