using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using SkyRender.Character;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The rules NpcMeshResolver uses to turn an outfit into worn pieces, pinned against the
/// in-game tests of 2026-10-29:
/// <list type="bullet">
/// <item>An ArmorAddon with only a male world model (MOD2) draws that model on a female actor
/// ("If MOD3 is not present, MOD2 is both genders"). Female Stormcloak soldiers wore
/// <c>ArmorStormcloakBoots</c>, whose <c>StormCloakBootsAA</c> has only <c>BootsM_1.nif</c>, on
/// every spawn; the preview used to draw no boots for them.</item>
/// <item>A plain leveled list stands in for its random pick with the most-listed entry when all
/// entries share one level.</item>
/// <item>Outfit armors whose own biped slots overlap are never both drawn; an armor every spawn
/// carries beats a rolled one.</item>
/// </list>
/// </summary>
public class OutfitResolutionRulesTests
{
    private static readonly ModKey TestKey = ModKey.FromNameAndExtension("OutfitRulesTest.esp");

    private static ArmorAddon Arma(SkyrimMod mod, Model? male, Model? female)
    {
        var arma = mod.ArmorAddons.AddNew();
        arma.WorldModel = new GenderedItem<Model?>(male, female);
        return arma;
    }

    private static Model ModelWithVariant(string path, string shapeName) => new()
    {
        File = path,
        AlternateTextures = new ExtendedList<AlternateTexture>
        {
            new AlternateTexture { Name = shapeName, Index = 0 },
        },
    };

    // ── Item 1: female actor, ARMA with only a male world model ──

    [Fact]
    public void FemaleActor_DrawsTheMaleModel_WhenTheArmaHasNoFemaleModel()
    {
        // StormCloakBootsAA: MOD2 = Armor\StormCloaks\BootsM_1.nif, no MOD3.
        var mod = new SkyrimMod(TestKey, SkyrimRelease.SkyrimSE);
        var boots = Arma(mod, new Model { File = @"Armor\StormCloaks\BootsM_1.nif" }, female: null);

        NpcMeshResolver.GetWorldModelPath(boots, NpcGender.Female)
            .Should().Be(@"meshes\Armor\StormCloaks\BootsM_1.nif");
        NpcMeshResolver.GetWorldModelPath(boots, NpcGender.Male)
            .Should().Be(@"meshes\Armor\StormCloaks\BootsM_1.nif");
    }

    [Fact]
    public void FemaleActor_FallsBackToMale_WhenTheFemaleModelHasNoFile()
    {
        var mod = new SkyrimMod(TestKey, SkyrimRelease.SkyrimSE);
        var arma = Arma(mod, new Model { File = @"Armor\M\Boots.nif" }, new Model());

        NpcMeshResolver.GetWorldModelPath(arma, NpcGender.Female).Should().Be(@"meshes\Armor\M\Boots.nif");
    }

    [Fact]
    public void TheMaleAlternateTextures_FollowTheFallbackModel()
    {
        // MODS belongs to MOD2: a female actor drawing the male model takes the male variants.
        var mod = new SkyrimMod(TestKey, SkyrimRelease.SkyrimSE);
        var male = ModelWithVariant(@"Armor\M\Boots.nif", "BootsM");
        var arma = Arma(mod, male, female: null);

        var selected = NpcMeshResolver.SelectWorldModel(arma, NpcGender.Female);

        selected.Should().BeSameAs(male);
        selected!.AlternateTextures!.Select(a => a.Name).Should().Equal("BootsM");
    }

    [Fact]
    public void BothModelsPresent_EachSexDrawsItsOwn()
    {
        var mod = new SkyrimMod(TestKey, SkyrimRelease.SkyrimSE);
        var male = ModelWithVariant(@"Armor\M\Cuirass.nif", "CuirassM");
        var female = ModelWithVariant(@"Armor\F\Cuirass.nif", "CuirassF");
        var arma = Arma(mod, male, female);

        NpcMeshResolver.SelectWorldModel(arma, NpcGender.Female).Should().BeSameAs(female);
        NpcMeshResolver.SelectWorldModel(arma, NpcGender.Male).Should().BeSameAs(male);
    }

    [Fact]
    public void MaleActor_NeverFallsBackToTheFemaleModel()
    {
        var mod = new SkyrimMod(TestKey, SkyrimRelease.SkyrimSE);
        var arma = Arma(mod, male: null, new Model { File = @"Armor\F\Dress.nif" });

        NpcMeshResolver.GetWorldModelPath(arma, NpcGender.Male).Should().BeNull();
        NpcMeshResolver.GetWorldModelPath(arma, NpcGender.Female).Should().Be(@"meshes\Armor\F\Dress.nif");
    }

    // ── Item 2: plain leveled list stand-in ──

    private static readonly FormKey Light = FormKey.Factory("01D249:Skyrim.esm");   // ArmorImperialLightSet
    private static readonly FormKey Studded = FormKey.Factory("01D24A:Skyrim.esm"); // ArmorImperialStuddedSet

    [Fact]
    public void PlainList_TriesTheMostListedEntryFirst_WhenAllLevelsMatch()
    {
        // OutfitListSoldierImperial: light set once, studded set twice, all level 1 —
        // the game picks studded two times in three.
        var order = NpcMeshResolver.OrderPlainListEntries(new List<(FormKey, short)>
        {
            (Light, 1), (Studded, 1), (Studded, 1),
        });

        order.Should().Equal(1, 0);
    }

    [Fact]
    public void PlainList_KeepsEntryOrder_OnATie()
    {
        // ArmorStormcloakHelmets: hide, scaled, full — one entry each.
        var hide = FormKey.Factory("013913:Skyrim.esm");
        var scaled = FormKey.Factory("01B3A1:Skyrim.esm");
        var full = FormKey.Factory("0A6D79:Skyrim.esm");

        NpcMeshResolver.OrderPlainListEntries(new List<(FormKey, short)> { (hide, 1), (scaled, 1), (full, 1) })
            .Should().Equal(0, 1, 2);
    }

    [Fact]
    public void PlainList_KeepsEntryOrder_WhenLevelsDiffer()
    {
        // Eligibility depends on the actor's level, which the preview does not model.
        NpcMeshResolver.OrderPlainListEntries(new List<(FormKey, short)>
            {
                (Light, 1), (Studded, 10), (Studded, 10),
            })
            .Should().Equal(0, 1, 2);
    }

    // ── Item 2: one armor per biped slot ──

    private const int Head = 1 << 0, Hair = 1 << 1, Body = 1 << 2, Feet = 1 << 7, Circlet = 1 << 12, Ears = 1 << 13;

    [Fact]
    public void SlotConflict_TheAlwaysCarriedArmorWins_TheSonsCommanderCase()
    {
        // GuardSonsOutfit: the hide helmet (slot 31) comes from a plain helmet list inside a
        // set, the full helmet (30, 31, 42, 43) from the full-face sets every spawn gets. Both
        // are carried; in game the full helmet was worn most often (12 of 18).
        var candidates = new List<(int, bool)>
        {
            (Feet, false),                       // boots
            (Body, false),                       // cuirass
            (Hair, true),                        // hide helmet (rolled)
            (Head | Hair | Circlet | Ears, false), // full helmet
        };

        var kept = NpcMeshResolver.SelectOutfitArmorsBySlot(candidates, out var dropped);

        kept.Should().Equal(0, 1, 3);
        dropped.Should().Equal((2, 3));
    }

    [Fact]
    public void SlotConflict_AmongEquals_TheEarlierArmorWins()
    {
        var kept = NpcMeshResolver.SelectOutfitArmorsBySlot(
            new List<(int, bool)> { (Hair, false), (Head | Hair, false), (Body, false) }, out var dropped);

        kept.Should().Equal(0, 2);
        dropped.Should().Equal((1, 0));
    }

    [Fact]
    public void SlotConflict_NeverKeepsTwoArmorsThatShareASlot()
    {
        var candidates = new List<(int, bool)>
        {
            (Hair, true), (Head | Hair, true), (Head, false), (Body | Feet, true), (Feet, false), (0, true),
        };

        var kept = NpcMeshResolver.SelectOutfitArmorsBySlot(candidates, out _);

        kept.Should().BeInAscendingOrder();
        int occupied = 0;
        foreach (int i in kept)
        {
            (candidates[i].Item1 & occupied).Should().Be(0, "armor {0} must not share a slot with a kept one", i);
            occupied |= candidates[i].Item1;
        }
        // Head (always carried) beats both rolled helmets; Feet (always carried) beats the
        // rolled body+feet piece; the rolled hair helmet survives because nothing else wears
        // slot 31 once the Head|Hair helmet is out; a slotless item conflicts with nothing.
        kept.Should().Equal(0, 2, 4, 5);
    }
}
