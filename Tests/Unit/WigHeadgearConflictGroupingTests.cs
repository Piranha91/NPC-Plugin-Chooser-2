using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="WigHeadgearConflictGrouping"/>: how the cases of the pre-run wig-and-headgear scan
/// are arranged for the window. Grouped by (outfit, headgear, wig); the totals count each NPC
/// once; the sentence of a group is built from what the back end read off the records. Pure
/// logic on the back end's records: no WPF, no game install.
/// </summary>
public class WigHeadgearConflictGroupingTests
{
    private static readonly FormKey OfficerOutfit = MutagenFixtures.Fk("000D3E:Skyrim.esm");
    private static readonly FormKey GuardOutfit = MutagenFixtures.Fk("0A1B2C:Skyrim.esm");
    private static readonly FormKey OfficerHelmet = MutagenFixtures.Fk("013EDC:Skyrim.esm");
    private static readonly FormKey GuardHelmet = MutagenFixtures.Fk("021508:Skyrim.esm");
    private static readonly FormKey WigA = MutagenFixtures.Fk("000801:HighPoly.esp");
    private static readonly FormKey WigB = MutagenFixtures.Fk("000802:HighPoly.esp");

    private static WigHeadgearConflict Conflict(FormKey outfit, string outfitEditorId, FormKey headgear,
        string headgearEditorId, string? headgearName, FormKey wig, string wigEditorId) =>
        new(new WigHeadgearConflictItem(wig, wigEditorId, null, new[] { 31 }),
            new WigHeadgearConflictItem(headgear, headgearEditorId, headgearName, new[] { 31, 42 }),
            outfit, outfitEditorId, new[] { 31 });

    private static WigHeadgearConflict OfficerA() => Conflict(OfficerOutfit, "ImperialOfficerOutfit",
        OfficerHelmet, "ArmorImperialHelmetOfficer", "Imperial Officer's Helmet", WigA, "HighPoly_WigAA_HairFemaleNord03");

    private static WigHeadgearConflict GuardA() => Conflict(GuardOutfit, "GuardWhiterunOutfit",
        GuardHelmet, "ArmorGuardHelmetWhiterun", "Whiterun Guard's Helmet", WigA, "HighPoly_WigAA_HairFemaleNord03");

    private static WigHeadgearConflict GuardB() => Conflict(GuardOutfit, "GuardWhiterunOutfit",
        GuardHelmet, "ArmorGuardHelmetWhiterun", "Whiterun Guard's Helmet", WigB, "HighPoly_WigAA_HairMaleNord01");

    private static WigHeadgearConflictCase Case(string npcId, string display, params WigHeadgearConflict[] conflicts) =>
        new(MutagenFixtures.Fk(npcId + ":Skyrim.esm"), display, "High Poly NPC Overhaul", conflicts);

    [Fact]
    public void Cases_AreGroupedByOutfitHeadgearAndWig()
    {
        var grouping = WigHeadgearConflictGrouping.From(new[]
        {
            Case("000001", "Rikke", OfficerA()),
            Case("000002", "Guard One", GuardA()),
            Case("000003", "Guard Two", GuardA()),
            Case("000004", "Guard Three", GuardB()), // same outfit and helmet, another wig: its own group
        });

        grouping.GroupCount.Should().Be(3);
        grouping.Groups.Select(g => (g.Conflict.OutfitFormKey, g.Conflict.Headgear.FormKey, g.Conflict.Wig.FormKey))
            .Should().BeEquivalentTo(new[]
            {
                (OfficerOutfit, OfficerHelmet, WigA),
                (GuardOutfit, GuardHelmet, WigA),
                (GuardOutfit, GuardHelmet, WigB),
            });
        grouping.Groups.Single(g => g.Conflict.OutfitFormKey == GuardOutfit && g.Conflict.Wig.FormKey == WigA)
            .Npcs.Select(n => n.Display).Should().Equal("Guard One", "Guard Two");
    }

    [Fact]
    public void Totals_CountNpcsAndGroups()
    {
        var grouping = WigHeadgearConflictGrouping.From(new[]
        {
            Case("000001", "Rikke", OfficerA()),
            Case("000002", "Guard One", GuardA()),
            Case("000003", "Guard Two", GuardA()),
        });

        grouping.NpcCount.Should().Be(3);
        grouping.GroupCount.Should().Be(2);
        grouping.TotalsText.Should().Be("3 NPCs in 2 groups");
    }

    [Fact]
    public void Totals_UseTheSingularForOne()
    {
        WigHeadgearConflictGrouping.From(new[] { Case("000001", "Rikke", OfficerA()) })
            .TotalsText.Should().Be("1 NPC in 1 group");
    }

    [Fact]
    public void NoCases_GiveAnEmptyGrouping()
    {
        var grouping = WigHeadgearConflictGrouping.From(Array.Empty<WigHeadgearConflictCase>());

        grouping.Groups.Should().BeEmpty();
        grouping.Rows.Should().BeEmpty();
        grouping.NpcFormKeys.Should().BeEmpty();
        grouping.TotalsText.Should().Be("0 NPCs in 0 groups");
        WigHeadgearConflictGrouping.From(null).NpcCount.Should().Be(0);
    }

    [Fact]
    public void AnNpcWithTwoConflicts_IsListedUnderBothGroups_AndCountedOnce()
    {
        // A guard whose wig clashes with the helmet of two outfits' worth of headgear: the back
        // end returns ONE case holding two conflicts.
        var grouping = WigHeadgearConflictGrouping.From(new[]
        {
            Case("000002", "Guard One", GuardA(), OfficerA()),
            Case("000003", "Guard Two", GuardA()),
        });

        grouping.GroupCount.Should().Be(2);
        grouping.NpcCount.Should().Be(2, "an NPC under two groups is still one NPC");
        grouping.NpcFormKeys.Should().Equal(MutagenFixtures.Fk("000002:Skyrim.esm"), MutagenFixtures.Fk("000003:Skyrim.esm"));
        grouping.Groups.Should().OnlyContain(g => g.Npcs.Any(n => n.Display == "Guard One"));
        grouping.TotalsText.Should().Be("2 NPCs in 2 groups");
    }

    [Fact]
    public void TheSameNpcReportedTwiceForOneTriple_IsOneRow()
    {
        var grouping = WigHeadgearConflictGrouping.From(new[] { Case("000002", "Guard One", GuardA(), GuardA()) });

        grouping.Groups.Should().ContainSingle().Which.Npcs.Should().ContainSingle();
    }

    [Fact]
    public void LargestGroupComesFirst_AndEqualSizesKeepTheScanOrder()
    {
        var grouping = WigHeadgearConflictGrouping.From(new[]
        {
            Case("000001", "Rikke", OfficerA()),      // 1 NPC, found first
            Case("000004", "Guard Three", GuardB()),  // 1 NPC, found second
            Case("000002", "Guard One", GuardA()),    // 2 NPCs
            Case("000003", "Guard Two", GuardA()),
        });

        grouping.Groups.Select(g => g.Npcs.Count).Should().Equal(2, 1, 1);
        grouping.Groups[0].Conflict.Wig.FormKey.Should().Be(WigA);
        grouping.Groups[0].Conflict.OutfitFormKey.Should().Be(GuardOutfit);
        grouping.Groups[1].Conflict.OutfitFormKey.Should().Be(OfficerOutfit, "equal sizes keep the order found");
        grouping.Groups[2].Conflict.Wig.FormKey.Should().Be(WigB);
    }

    [Fact]
    public void Rows_AreEachGroupFollowedByItsNpcs()
    {
        var grouping = WigHeadgearConflictGrouping.From(new[]
        {
            Case("000001", "Rikke", OfficerA()),
            Case("000002", "Guard One", GuardA()),
            Case("000003", "Guard Two", GuardA()),
        });

        grouping.Rows.Should().HaveCount(5);
        grouping.Rows[0].Should().BeOfType<WigHeadgearConflictGroupRow>()
            .Which.Conflict.OutfitFormKey.Should().Be(GuardOutfit);
        grouping.Rows[1].Should().BeOfType<WigHeadgearConflictNpcRow>().Which.Display.Should().Be("Guard One");
        grouping.Rows[2].Should().BeOfType<WigHeadgearConflictNpcRow>().Which.Display.Should().Be("Guard Two");
        grouping.Rows[3].Should().BeOfType<WigHeadgearConflictGroupRow>()
            .Which.Conflict.OutfitFormKey.Should().Be(OfficerOutfit);
        grouping.Rows[4].Should().BeOfType<WigHeadgearConflictNpcRow>().Which.Display.Should().Be("Rikke");
        ((WigHeadgearConflictNpcRow)grouping.Rows[4]).ModName.Should().Be("High Poly NPC Overhaul");
    }

    [Fact]
    public void GroupLine_NamesWigOutfitHeadgearAndSlots_FromTheRecords()
    {
        var group = WigHeadgearConflictGrouping.From(new[] { Case("000001", "Rikke", OfficerA()) }).Groups.Single();

        group.Description.Should().Be(
            "Wig 'HighPoly_WigAA_HairFemaleNord03' (slot 31) would be added to outfit 'ImperialOfficerOutfit', " +
            "which holds 'Imperial Officer's Helmet (ArmorImperialHelmetOfficer)' (slots 31 and 42); both use slot 31");
        group.CountText.Should().Be("1 NPC");
    }

    [Fact]
    public void GroupLine_FallsBackToTheFormKey_NeverToAPlaceholder()
    {
        // Records with neither a name nor an EditorID still identify themselves.
        var bare = new WigHeadgearConflict(
            new WigHeadgearConflictItem(WigA, null, null, new[] { 31 }),
            new WigHeadgearConflictItem(OfficerHelmet, null, null, new[] { 30, 31 }),
            OfficerOutfit, null, new[] { 31 });

        var line = WigHeadgearConflictGrouping.Describe(bare);

        line.Should().Be($"Wig '{WigA}' (slot 31) would be added to outfit '{OfficerOutfit}', " +
                         $"which holds '{OfficerHelmet}' (slots 30 and 31); both use slot 31");
    }
}
