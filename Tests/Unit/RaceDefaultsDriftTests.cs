using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The pure half of the race-drift compatibility trigger:
/// <see cref="Patcher.RaceDefaultHeadParts"/> extracts the RACE's chargen default head part
/// FormKeys for one sex — the set whose disagreement between the selected mod's context and
/// the live load order sends an otherwise-unprobed mod-authored mesh to the compatibility
/// probe (<see cref="NpcWarningKind.RaceDefaultsDrift"/>). Built in memory; the I/O half
/// (mod-scoped race resolution, the mesh parse) is exercised by the patcher integration
/// suites.
/// </summary>
public class RaceDefaultsDriftTests
{
    private static HeadPartReference Ref(FormKey headPart)
    {
        var hpRef = new HeadPartReference();
        hpRef.Head.SetTo(headPart);
        return hpRef;
    }

    [Fact]
    public void RaceDefaultHeadParts_NoHeadData_IsEmpty()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var race = MutagenFixtures.NewRace(mod, "BareRace");

        Patcher.RaceDefaultHeadParts(race, female: false).Should().BeEmpty();
        Patcher.RaceDefaultHeadParts(race, female: true).Should().BeEmpty();
    }

    [Fact]
    public void RaceDefaultHeadParts_PicksTheRequestedSex()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var maleHead = MutagenFixtures.Fk("000801:Test.esp");
        var femaleHead = MutagenFixtures.Fk("000802:Test.esp");

        var maleData = new HeadData();
        maleData.HeadParts.Add(Ref(maleHead));
        var femaleData = new HeadData();
        femaleData.HeadParts.Add(Ref(femaleHead));

        var race = MutagenFixtures.NewRace(mod, "SexedRace");
        race.HeadData = new GenderedItem<HeadData?>(maleData, femaleData);

        Patcher.RaceDefaultHeadParts(race, female: false).Should().BeEquivalentTo(new[] { maleHead });
        Patcher.RaceDefaultHeadParts(race, female: true).Should().BeEquivalentTo(new[] { femaleHead });
    }

    [Fact]
    public void RaceDefaultHeadParts_SkipsNullLinks_AndCollapsesDuplicates()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var head = MutagenFixtures.Fk("000801:Test.esp");

        var data = new HeadData();
        data.HeadParts.Add(Ref(head));
        data.HeadParts.Add(Ref(head));               // duplicate entry (vanilla races have them)
        data.HeadParts.Add(new HeadPartReference()); // null Head link

        var race = MutagenFixtures.NewRace(mod, "DupRace");
        race.HeadData = new GenderedItem<HeadData?>(data, null);

        var parts = Patcher.RaceDefaultHeadParts(race, female: false);

        parts.Should().BeEquivalentTo(new[] { head });
    }

    // ---- Include vs IncludeAsNew advice: which mods disagree about a race's version ----------

    private static HashSet<FormKey> Parts(params string[] formKeys) =>
        new(formKeys.Select(s => FormKey.Factory(s)));

    [Fact]
    public void ModsWithDifferentRaceVersion_SingleMod_IsEmpty_SoIncludeIsSafe()
    {
        var usage = new Dictionary<string, HashSet<FormKey>>
        {
            ["RS-Style Overhaul"] = Parts("000011:RaceMod.esm", "000004:RaceMod.esm"),
        };

        Patcher.ModsWithDifferentRaceVersion(usage, "RS-Style Overhaul").Should().BeEmpty();
    }

    [Fact]
    public void ModsWithDifferentRaceVersion_AgreeingMods_AreEmpty_SoIncludeIsStillSafe()
    {
        // Two mods of the same family (an overhaul plus its patch) authored against the SAME
        // race version must not push the advice to IncludeAsNew — the branch keys on version
        // content, not mod count.
        var shared = Parts("000011:RaceMod.esm", "000004:RaceMod.esm");
        var usage = new Dictionary<string, HashSet<FormKey>>
        {
            ["RS-Style Overhaul"] = shared,
            ["RS-Style Patch"] = new(shared),
        };

        Patcher.ModsWithDifferentRaceVersion(usage, "RS-Style Overhaul").Should().BeEmpty();
    }

    [Fact]
    public void ModsWithDifferentRaceVersion_MixedVersions_NamesTheDisagreeingMods()
    {
        // The measured scenario: some children on a race-editing overhaul, others on mods
        // authored against the unedited race. A shared Include override would break the latter,
        // so the drifting mod must be steered to IncludeAsNew.
        var edited = Parts("000011:RaceMod.esm", "000004:RaceMod.esm");
        var vanilla = Parts("051401:Skyrim.esm", "0511CA:Skyrim.esm");
        var usage = new Dictionary<string, HashSet<FormKey>>
        {
            ["RS-Style Overhaul"] = edited,
            ["Vanilla-Authored B"] = vanilla,
            ["Vanilla-Authored A"] = new(vanilla),
        };

        Patcher.ModsWithDifferentRaceVersion(usage, "RS-Style Overhaul")
            .Should().Equal("Vanilla-Authored A", "Vanilla-Authored B"); // alphabetical
    }

    [Fact]
    public void ModsWithDifferentRaceVersion_ComparesAsSets_NotLists()
    {
        var usage = new Dictionary<string, HashSet<FormKey>>
        {
            ["Mod A"] = Parts("000001:X.esp", "000002:X.esp"),
            ["Mod B"] = Parts("000002:X.esp", "000001:X.esp"),
        };

        Patcher.ModsWithDifferentRaceVersion(usage, "Mod A").Should().BeEmpty();
    }

    [Fact]
    public void RaceDefaultHeadParts_DriftComparison_IsOrderInsensitive()
    {
        // The trigger compares the two contexts with SetEquals: same members in a different
        // list order is NOT drift (races reordered by a patch must not fire probes), while a
        // swapped member is.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var a = MutagenFixtures.Fk("000801:Test.esp");
        var b = MutagenFixtures.Fk("000802:Test.esp");
        var c = MutagenFixtures.Fk("000803:Test.esp");

        var forward = new HeadData();
        forward.HeadParts.Add(Ref(a));
        forward.HeadParts.Add(Ref(b));
        var reversed = new HeadData();
        reversed.HeadParts.Add(Ref(b));
        reversed.HeadParts.Add(Ref(a));
        var swapped = new HeadData();
        swapped.HeadParts.Add(Ref(a));
        swapped.HeadParts.Add(Ref(c));

        var race1 = MutagenFixtures.NewRace(mod, "R1");
        race1.HeadData = new GenderedItem<HeadData?>(forward, null);
        var race2 = MutagenFixtures.NewRace(mod, "R2");
        race2.HeadData = new GenderedItem<HeadData?>(reversed, null);
        var race3 = MutagenFixtures.NewRace(mod, "R3");
        race3.HeadData = new GenderedItem<HeadData?>(swapped, null);

        var s1 = Patcher.RaceDefaultHeadParts(race1, female: false);
        var s2 = Patcher.RaceDefaultHeadParts(race2, female: false);
        var s3 = Patcher.RaceDefaultHeadParts(race3, female: false);

        s1.SetEquals(s2).Should().BeTrue();
        s1.SetEquals(s3).Should().BeFalse();
    }
}
