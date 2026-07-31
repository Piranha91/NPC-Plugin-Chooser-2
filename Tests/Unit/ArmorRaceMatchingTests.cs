using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The ArmorRace (RNAM) indirection every ARMA race filter in the app runs on
/// (Auxilliary.GetArmorRaceKey + ArmaNamesRace).
///
/// The regression these pin: the filters compared the NPC's own race FormKey
/// against ARMA.Race/AdditionalRaces. The engine doesn't — it matches armatures
/// against the actor race's ArmorRace, which is how custom-race followers wear
/// vanilla-targeted armatures. Comparing the raw key dropped every armature such
/// a mod ships, so the NPC rendered as a disembodied FaceGen head (the head comes
/// from the FaceGeom NIF and never goes through armature resolution).
///
/// Specimen encoded below: Chaconne Vilja SSE. AAEMNordViljaRace has
/// ArmorRace=NordRace; ChaconneTorsoAP/HandsAP/FeetAP all name NordRace only.
/// </summary>
public class ArmorRaceMatchingTests
{
    private static readonly ModKey TestKey = ModKey.FromNameAndExtension("ArmorRaceTest.esp");

    private sealed class Fixture
    {
        public SkyrimMod Mod { get; } = new(TestKey, SkyrimRelease.SkyrimSE);
        public Race CustomRace { get; }
        public Race NordRace { get; }

        public Fixture()
        {
            NordRace = Mod.Races.AddNew();
            NordRace.EditorID = "NordRace";
            CustomRace = Mod.Races.AddNew();
            CustomRace.EditorID = "AAEMNordViljaRace";
        }

        public ArmorAddon Arma(FormKey? race, params FormKey[] additional)
        {
            var arma = Mod.ArmorAddons.AddNew();
            if (race.HasValue) arma.Race.SetTo(race.Value);
            foreach (var extra in additional) arma.AdditionalRaces.Add(extra.ToLink<IRaceGetter>());
            return arma;
        }
    }

    [Fact]
    public void GetArmorRaceKey_ReturnsRnam_WhenItPointsElsewhere()
    {
        var f = new Fixture();
        f.CustomRace.ArmorRace.SetTo(f.NordRace.FormKey);

        Auxilliary.GetArmorRaceKey(f.CustomRace).Should().Be(f.NordRace.FormKey);
    }

    [Fact]
    public void GetArmorRaceKey_IsNull_WhenRnamPointsAtTheRaceItself()
    {
        // Vanilla races self-reference; treating that as a second key to match
        // would be noise, and callers use null to mean "no indirection".
        var f = new Fixture();
        f.NordRace.ArmorRace.SetTo(f.NordRace.FormKey);

        Auxilliary.GetArmorRaceKey(f.NordRace).Should().BeNull();
    }

    [Fact]
    public void GetArmorRaceKey_IsNull_WhenRnamUnsetOrRaceUnresolvable()
    {
        var f = new Fixture();

        Auxilliary.GetArmorRaceKey(f.CustomRace).Should().BeNull();
        Auxilliary.GetArmorRaceKey(null).Should().BeNull();
    }

    [Fact]
    public void ArmaNamesRace_MatchesViaArmorRace_TheViljaCase()
    {
        var f = new Fixture();
        var torso = f.Arma(f.NordRace.FormKey);
        var armorRace = f.NordRace.FormKey;

        // Pre-fix comparison (raw race key only) is what dropped the body.
        Auxilliary.ArmaNamesRace(torso, f.CustomRace.FormKey, null).Should().BeFalse();

        Auxilliary.ArmaNamesRace(torso, f.CustomRace.FormKey, armorRace).Should().BeTrue();
    }

    [Fact]
    public void ArmaNamesRace_StillMatchesTheNpcsOwnRace()
    {
        // The indirection is additive: an ARMA naming the actor's actual race
        // must keep matching whether or not ArmorRace points elsewhere.
        var f = new Fixture();
        var arma = f.Arma(f.CustomRace.FormKey);

        Auxilliary.ArmaNamesRace(arma, f.CustomRace.FormKey, null).Should().BeTrue();
        Auxilliary.ArmaNamesRace(arma, f.CustomRace.FormKey, f.NordRace.FormKey).Should().BeTrue();
    }

    [Fact]
    public void ArmaNamesRace_MatchesArmorRace_InAdditionalRaces()
    {
        var f = new Fixture();
        var other = f.Mod.Races.AddNew();
        var arma = f.Arma(other.FormKey, f.NordRace.FormKey);

        Auxilliary.ArmaNamesRace(arma, f.CustomRace.FormKey, null).Should().BeFalse();
        Auxilliary.ArmaNamesRace(arma, f.CustomRace.FormKey, f.NordRace.FormKey).Should().BeTrue();
    }

    [Fact]
    public void ArmaNamesRace_RejectsAnArmatureForAnUnrelatedRace()
    {
        // The filter must still filter — an ArmorRace match is not a free pass
        // for every armature on the record.
        var f = new Fixture();
        var beast = f.Mod.Races.AddNew();
        var arma = f.Arma(beast.FormKey);

        Auxilliary.ArmaNamesRace(arma, f.CustomRace.FormKey, f.NordRace.FormKey).Should().BeFalse();
    }

    [Fact]
    public void ArmaNamesRace_IgnoresNullKeysRatherThanMatchingThem()
    {
        // An ARMA with no Race and a caller with no race keys must not be
        // reported as a match by two nulls comparing equal.
        var f = new Fixture();
        var arma = f.Arma(race: null);

        Auxilliary.ArmaNamesRace(arma, null, null).Should().BeFalse();
        Auxilliary.ArmaNamesRace(arma, f.CustomRace.FormKey, f.NordRace.FormKey).Should().BeFalse();
    }
}
