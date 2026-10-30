using System.Linq;
using Autofac;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.Memory;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using SkyRender.Character;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// The outfit walk against vanilla Skyrim.esm, for the NPCs of the in-game outfit tests of
/// 2026-10-29. <c>GuardSonsOutfit</c> is a Use All list of two <c>ArmorStormcloakSet</c>s (each
/// with a plain helmet list: hide, scaled, full) and four <c>ArmorStormcloakSetHelmetFullFace</c>s
/// (full helmet). Every spawn carries the full helmet; in game it was worn 12 times in 18, the
/// hide and scaled helmets 3 each. The preview used to draw the hide helmet AND the full helmet,
/// and drew no boots on a female soldier (<c>StormCloakBootsAA</c> has only a male model).
/// Skips without a Skyrim SE install.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class OutfitResolutionVanillaTests
{
    private static readonly FormKey FemaleSonsGuard = FormKey.Factory("0AA922:Skyrim.esm");  // EncGuardSonsF01FemaleNord
    private static readonly FormKey SonsCommander = FormKey.Factory("0EA32C:Skyrim.esm");    // CWFort04SoldierCommanderSons

    private readonly WpfStaFixture _sta;
    private readonly ITestOutputHelper _out;

    public OutfitResolutionVanillaTests(WpfStaFixture sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    [Fact]
    public async Task GuardSonsOutfit_DrawsBootsOnAFemaleSoldier_AndOnlyTheFullHelmet()
    {
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip))
        {
            _out.WriteLine("SKIP: " + skip);
            return;
        }

        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard(immediateSchedulers: false);
            FrontendVmHarness.InstallStaMainThreadScheduler();
            using var harness = new FrontendVmHarness(env!.Provider, new Settings { SkyrimRelease = SkyrimRelease.SkyrimSE });
            var resolver = harness.Container.Resolve<NpcMeshResolver>();

            foreach (var npc in new[] { FemaleSonsGuard, SonsCommander })
            {
                var overrides = resolver.ResolveAttireMeshOverrides(
                    npc, null, includeDefaultOutfit: true, includeHeadgear: true);
                foreach (var o in overrides) _out.WriteLine($"{npc}: {o.Kind} slots={o.BipedSlots} {o.MeshPath}");

                var paths = overrides.Select(o => o.MeshPath).ToList();
                paths.Should().Contain(p => p.EndsWith(@"StormCloaks\BootsM_1.nif", StringComparison.OrdinalIgnoreCase),
                    "every spawn wears ArmorStormcloakBoots, female ones the male model");
                paths.Should().NotContain(p => p.Contains(@"Armor\Hide\", StringComparison.OrdinalIgnoreCase),
                    "the always-carried full helmet takes slot 31 from the rolled hide/scaled helmet");
                overrides.Where(o => o.Kind == MeshOverrideKind.Headgear).Should().ContainSingle()
                    .Which.MeshPath.Should().Contain(@"StormCloaks\Helm");
                // ArmorAddon slots may overlap (cuirass and boots both list calves, 38): that is
                // the partition-hiding case, not a slot conflict — conflicts are on ARMO slots.
            }
            return Task.CompletedTask;
        });
    }
}
