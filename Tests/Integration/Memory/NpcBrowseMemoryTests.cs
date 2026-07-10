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

            // Browse to the first NPC that yields tiles; capture that tile set.
            VM_NpcsMenuMugshot[]? firstTiles = null;
            int browsedAway = 0;
            const int browseAwayTarget = 3;
            const int maxScan = 250;
            int scanned = 0;
            foreach (var npc in bar.AllNpcs)
            {
                if (scanned++ >= maxScan) break;
                var current = await harness.SelectAndWaitAsync(npc);
                if ((current?.Count ?? 0) == 0) continue;

                if (firstTiles == null)
                {
                    firstTiles = current!.ToArray();
                    // While these tiles belong to the displayed NPC they must be live (not disposed).
                    firstTiles.Should().OnlyContain(t => !IsTileDisposed(t),
                        "the currently-displayed NPC's tiles must not be disposed");
                }
                else
                {
                    browsedAway++;
                    if (browsedAway >= browseAwayTarget) break;
                }
                current = null;
            }

            if (firstTiles == null || browsedAway < browseAwayTarget)
            {
                _out.WriteLine($"SKIP: not enough tiled NPCs to browse ({scanned} scanned). " +
                               $"Configure {CuratedMugshotsFolder} or appearance mods.");
                return;
            }

            var stillLive = firstTiles.Count(t => !IsTileDisposed(t));
            _out.WriteLine($"First NPC had {firstTiles.Length} tiles; after browsing {browsedAway} NPCs away, " +
                           $"{firstTiles.Length - stillLive}/{firstTiles.Length} disposed.");

            firstTiles.Should().OnlyContain(t => IsTileDisposed(t),
                "browsing away from an NPC must dispose its VM_NpcsMenuMugshot tiles — each tile's subscription " +
                "to the singleton NpcConsistencyProvider is what rooted it for the app's lifetime, so a tile " +
                "left undisposed is the leak fixed in commit 2312cb6 regressing");
        });
    }
}
