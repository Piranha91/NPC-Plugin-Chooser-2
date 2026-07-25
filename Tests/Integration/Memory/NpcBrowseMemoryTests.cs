using System.IO;
using System.Reactive.Disposables;
using System.Reflection;
using FluentAssertions;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Memory;

/// <summary>
/// CI-safe memory-leak regression for the NPC-browse flow — the flow whose per-item VMs leaked before
/// commit 2312cb6 ("Fix mugshot tile/ModSetting leaks while browsing NPCs"). The dominant leak was that a
/// <c>VM_NpcsMenuMugshot</c> tile stayed rooted for the life of the app by its subscription to the
/// singleton <c>NpcConsistencyProvider</c>; the fix disposes each NPC's tiles when the user browses to the
/// next NPC (see <c>VM_NpcSelectionBar</c>'s <c>CurrentNpcAppearanceMods</c> rebuild). This test guards that
/// contract directly and deterministically: it drives the real <see cref="VM_NpcSelectionBar"/> via
/// <see cref="FrontendVmHarness"/> and asserts that a browsed-away NPC's tiles have had their subscription
/// composite disposed.
///
/// <para><b>Why the disposal contract rather than a GC/WeakReference check:</b> a tile's constructor kicks a
/// fire-and-forget <c>LoadInitialImageAsync</c> task with no cancellation token, and for NPCs with no
/// resolvable image that task doesn't quiesce under the headless stub renderer — so it keeps every tile
/// reachable regardless of disposal, making a "was it garbage collected" assertion a 100% false positive
/// here. Disposal of the subscription composite is exactly what severs the singleton root the leak fix
/// targeted, and it is deterministic. Absolute byte-growth measurement (which needs the real renderer so
/// loads actually complete) lives in <see cref="MugshotAcquisitionMemoryDiagnostic"/>.</para>
///
/// <para>Needs a live Skyrim SE install to populate NPCs; graceful-skips (as a passing no-op) when none is
/// present. Curated mugshots are picked up from <c>S:\Skyrim Mugshots</c> when present; the flow runs
/// against whatever the environment provides otherwise, and skips if too few NPCs yield tiles.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class NpcBrowseMemoryTests
{
    private const string CuratedMugshotsFolder = @"S:\Skyrim Mugshots";

    // The tile's private CompositeDisposable that holds all its subscriptions (incl. the one to the
    // singleton NpcConsistencyProvider). Its IsDisposed flag is the observable proof that browsing away
    // severed the tile's roots. Reflection into a private is the project's sanctioned test seam (see Reflect).
    private static readonly FieldInfo DisposablesField = typeof(VM_NpcsMenuMugshot)
        .GetField("Disposables", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static bool IsTileDisposed(VM_NpcsMenuMugshot t) =>
        ((CompositeDisposable)DisposablesField.GetValue(t)!).IsDisposed;

    private readonly WpfStaFixture _sta;
    private readonly ITestOutputHelper _out;

    public NpcBrowseMemoryTests(WpfStaFixture sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    private Settings BuildSettings()
    {
        var s = new Settings { SkyrimRelease = SkyrimRelease.SkyrimSE };
        if (Directory.Exists(CuratedMugshotsFolder))
            s.MugshotsFolder = CuratedMugshotsFolder;
        return s;
    }

    [Fact]
    public async Task Harness_ConstructsAndPopulatesNpcs()
    {
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip))
        {
            _out.WriteLine("SKIP: " + skip);
            return;
        }

        await _sta.RunOnStaAsync(async () =>
        {
            using var _ = new StaticStateGuard(immediateSchedulers: false);
            FrontendVmHarness.InstallStaMainThreadScheduler();
            using var harness = new FrontendVmHarness(env!.Provider, BuildSettings());

            await harness.DriveStartupPopulationAsync();

            _out.WriteLine($"AllNpcs populated: {harness.NpcSelectionBar.AllNpcs.Count}");
            harness.NpcSelectionBar.AllNpcs.Should().NotBeEmpty(
                "a valid Skyrim environment should yield at least the vanilla NPCs via the Base Game entry");
        });
    }

    [Fact]
    public async Task BrowsingAway_DisposesPreviousNpcTiles()
    {
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip))
        {
            _out.WriteLine("SKIP: " + skip);
            return;
        }

        await _sta.RunOnStaAsync(async () =>
        {
            using var _ = new StaticStateGuard(immediateSchedulers: false);
            FrontendVmHarness.InstallStaMainThreadScheduler();
            using var harness = new FrontendVmHarness(env!.Provider, BuildSettings());
            await harness.DriveStartupPopulationAsync();
            var bar = harness.NpcSelectionBar;

            const int browseAwayTarget = 3;
            const int maxScan = 250;
            // A tile set worth asserting on. Taking the *first* NPC that yields any tiles picks a
            // vanilla NPC with only the "Base Game" source — a single tile, which cannot distinguish
            // "disposes the whole set" from "disposes the first one". Keep scanning for a genuinely
            // multi-source NPC and stop early once one is good enough, so the run stays quick.
            const int goodEnoughTiles = 3;

            // Phase 1: find the NPC with the most appearance sources. Only the count is recorded —
            // browsing on is what disposes the tiles, so the instances themselves can't be held here.
            VM_NpcsMenuSelection? richest = null;
            int richestCount = 0;
            int scanned = 0;
            foreach (var npc in bar.AllNpcs)
            {
                if (scanned++ >= maxScan) break;
                var current = await harness.SelectAndWaitAsync(npc);
                int count = current?.Count ?? 0;
                if (count > richestCount)
                {
                    richest = npc;
                    richestCount = count;
                }
                if (richestCount >= goodEnoughTiles) break;
            }

            if (richest == null || richestCount == 0)
            {
                _out.WriteLine($"SKIP: no NPC yielded tiles ({scanned} scanned). " +
                               $"Configure {CuratedMugshotsFolder} or appearance mods.");
                return;
            }

            // Phase 2: go back to that NPC for a fresh tile set, then browse away from it.
            var tiles = (await harness.SelectAndWaitAsync(richest))?.ToArray();
            if (tiles == null || tiles.Length == 0)
            {
                _out.WriteLine("SKIP: re-selecting the richest NPC yielded no tiles.");
                return;
            }

            // While these tiles belong to the displayed NPC they must be live (not disposed).
            tiles.Should().OnlyContain(t => !IsTileDisposed(t),
                "the currently-displayed NPC's tiles must not be disposed");

            int browsedAway = 0;
            foreach (var npc in bar.AllNpcs)
            {
                if (ReferenceEquals(npc, richest)) continue;
                await harness.SelectAndWaitAsync(npc);
                if (++browsedAway >= browseAwayTarget) break;
            }

            browsedAway.Should().BeGreaterThan(0, "the bar should hold more than one NPC to browse between");

            var stillLive = tiles.Count(t => !IsTileDisposed(t));
            _out.WriteLine($"Richest of {scanned} scanned NPCs ('{richest.DisplayName}') had {tiles.Length} tiles; " +
                           $"after browsing {browsedAway} NPCs away, {tiles.Length - stillLive}/{tiles.Length} disposed.");

            if (tiles.Length == 1)
            {
                _out.WriteLine("NOTE: only a single-tile NPC was reachable — this run cannot distinguish " +
                               "'disposes the whole set' from 'disposes the first tile'.");
            }

            tiles.Should().OnlyContain(t => IsTileDisposed(t),
                "browsing away from an NPC must dispose its VM_NpcsMenuMugshot tiles — each tile's subscription " +
                "to the singleton NpcConsistencyProvider is what rooted it for the app's lifetime, so a tile " +
                "left undisposed is the leak fixed in commit 2312cb6 regressing");
        });
    }
}
