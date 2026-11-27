using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="NpcSelectionIndicatorRules"/>: which of the NPC list's indicator states a row is
/// in, given the facts the list gathers. The three original states (red, purple, green) must
/// come out exactly as before; the two hatched states refine green only, and the wig conflict
/// outranks the outfit note. Plain logic: no WPF state, no game install.
/// </summary>
public class NpcSelectionIndicatorRulesTests
{
    private static readonly IReadOnlyList<WigHeadgearConflict> RikkeConflict = new[]
    {
        new WigHeadgearConflict(
            new WigHeadgearConflictItem(MutagenFixtures.Fk("000801:HighPoly.esp"), "HighPoly_WigAA_HairFemaleNord03", null, new[] { 31 }),
            new WigHeadgearConflictItem(MutagenFixtures.Fk("013EDC:Skyrim.esm"), "ArmorImperialHelmetOfficer",
                "Imperial Officer's Helmet", new[] { 31, 42 }),
            MutagenFixtures.Fk("000D3E:Skyrim.esm"), "ImperialOfficerOutfit", new[] { 31 }),
    };

    // ------------------------------------------------------------------------------------------
    // The state
    // ------------------------------------------------------------------------------------------

    [Theory]
    // no selection: no indicator, whatever else is true
    [InlineData(false, true, true, TemplatedOutfitNote.None, false, NpcSelectionIndicatorState.None)]
    [InlineData(false, false, false, TemplatedOutfitNote.NeverWorn, true, NpcSelectionIndicatorState.None)]
    // the three original states
    [InlineData(true, false, true, TemplatedOutfitNote.None, false, NpcSelectionIndicatorState.NotInLoadOrder)]
    [InlineData(true, false, false, TemplatedOutfitNote.None, false, NpcSelectionIndicatorState.NotInLoadOrder)]
    [InlineData(true, true, false, TemplatedOutfitNote.None, false, NpcSelectionIndicatorState.SelectedModHasNoData)]
    [InlineData(true, true, true, TemplatedOutfitNote.None, false, NpcSelectionIndicatorState.Valid)]
    // the notes refine green
    [InlineData(true, true, true, TemplatedOutfitNote.ReplacesTemplateClothing, false, NpcSelectionIndicatorState.ValidWithOutfitNote)]
    [InlineData(true, true, true, TemplatedOutfitNote.NeverWorn, false, NpcSelectionIndicatorState.ValidWithOutfitNote)]
    [InlineData(true, true, true, TemplatedOutfitNote.None, true, NpcSelectionIndicatorState.ValidWithWigConflict)]
    // both apply: red hatches win
    [InlineData(true, true, true, TemplatedOutfitNote.NeverWorn, true, NpcSelectionIndicatorState.ValidWithWigConflict)]
    [InlineData(true, true, true, TemplatedOutfitNote.ReplacesTemplateClothing, true, NpcSelectionIndicatorState.ValidWithWigConflict)]
    // ...and never replace red or purple
    [InlineData(true, false, true, TemplatedOutfitNote.NeverWorn, true, NpcSelectionIndicatorState.NotInLoadOrder)]
    [InlineData(true, true, false, TemplatedOutfitNote.NeverWorn, true, NpcSelectionIndicatorState.SelectedModHasNoData)]
    public void Resolve_GivesTheState(bool hasSelection, bool inLoadOrder, bool modHasData,
        TemplatedOutfitNote outfitNote, bool wigConflictIsOpen, NpcSelectionIndicatorState expected)
    {
        NpcSelectionIndicatorRules.Resolve(hasSelection, inLoadOrder, modHasData, outfitNote, wigConflictIsOpen)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(NpcSelectionIndicatorState.None, false)]
    [InlineData(NpcSelectionIndicatorState.NotInLoadOrder, false)]
    [InlineData(NpcSelectionIndicatorState.SelectedModHasNoData, false)]
    [InlineData(NpcSelectionIndicatorState.Valid, false)]
    [InlineData(NpcSelectionIndicatorState.ValidWithOutfitNote, true)]
    [InlineData(NpcSelectionIndicatorState.ValidWithWigConflict, true)]
    public void OnlyTheTwoNoteStates_AreHatched(NpcSelectionIndicatorState state, bool hatched)
    {
        NpcSelectionIndicatorRules.IsHatched(state).Should().Be(hatched);
    }

    // ------------------------------------------------------------------------------------------
    // The wig conflict and the stored choice
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, null, true)]                                   // unanswered
    [InlineData(true, WigHeadgearChoice.Forward, true)]              // answered "forward": still missing an item in game
    [InlineData(true, WigHeadgearChoice.Omit, false)]                // settled
    [InlineData(true, WigHeadgearChoice.ConvertToHeadParts, false)]  // settled
    [InlineData(false, null, false)]                                 // no conflict in the records
    [InlineData(false, WigHeadgearChoice.Forward, false)]            // a stale answer shows nothing
    public void WigConflictIsOpen_UntilOmitOrConvertIsStored(bool conflictExists, WigHeadgearChoice? stored, bool expected)
    {
        NpcSelectionIndicatorRules.WigConflictIsOpen(conflictExists, stored).Should().Be(expected);
    }

    [Theory]
    [InlineData(WigHandlingMode.ForwardToOutfit, true)]
    [InlineData(WigHandlingMode.ForwardToSkin, false)]
    [InlineData(WigHandlingMode.ConvertToHeadParts, false)]
    [InlineData(WigHandlingMode.None, false)]
    public void TheWigQuestion_IsAskedOnlyUnderForwardToOutfit(WigHandlingMode mode, bool asked)
    {
        NpcSelectionIndicatorRules.WigConflictQueryApplies(mode).Should().Be(asked);
    }

    // ------------------------------------------------------------------------------------------
    // The outfit note
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, OutfitOverride.UseModSetting, false)]
    [InlineData(true, OutfitOverride.UseModSetting, true)]
    [InlineData(false, OutfitOverride.Yes, true)]   // the NPC's own override beats the mod
    [InlineData(true, OutfitOverride.No, false)]
    public void IncludeOutfitInEffect_IsThePerNpcOverrideElseTheMod(bool modIncludes, OutfitOverride? npcOverride, bool expected)
    {
        NpcSelectionIndicatorRules.IncludeOutfitInEffect(modIncludes, npcOverride).Should().Be(expected);
    }

    [Theory]
    [InlineData(false, true, true, TemplatedOutfitNote.None)]   // no outfit included
    [InlineData(false, true, false, TemplatedOutfitNote.None)]
    [InlineData(true, false, true, TemplatedOutfitNote.None)]   // no template dresses the NPC
    [InlineData(true, false, false, TemplatedOutfitNote.None)]
    [InlineData(true, true, true, TemplatedOutfitNote.ReplacesTemplateClothing)]  // by SkyPatcher directive
    [InlineData(true, true, false, TemplatedOutfitNote.NeverWorn)]                // into the record field
    public void OutfitNote_NeedsAnIncludedOutfitAndATemplateDressedNpc(bool includeOutfit, bool dressedByTemplate,
        bool directiveReplaces, TemplatedOutfitNote expected)
    {
        NpcSelectionIndicatorRules.ResolveOutfitNote(includeOutfit, dressedByTemplate, directiveReplaces)
            .Should().Be(expected);
    }

    // ------------------------------------------------------------------------------------------
    // The tooltip text
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void OutfitNotes_HaveTheTwoAgreedWordings()
    {
        NpcSelectionIndicatorRules.BuildNote(NpcSelectionIndicatorState.ValidWithOutfitNote,
                TemplatedOutfitNote.ReplacesTemplateClothing, null, null)
            .Should().Be("Valid selection. 'Include Outfit' is on and this NPC is normally dressed by a template " +
                         "NPC. The mod's outfit replaces the clothing the template gives it in game.");

        NpcSelectionIndicatorRules.BuildNote(NpcSelectionIndicatorState.ValidWithOutfitNote,
                TemplatedOutfitNote.NeverWorn, null, null)
            .Should().Be("Valid selection. 'Include Outfit' is on, but this NPC is dressed by a template NPC, so " +
                         "the outfit written to its record is never worn in game.");
    }

    [Fact]
    public void WigNote_IsBuiltFromTheRecords()
    {
        NpcSelectionIndicatorRules.BuildNote(NpcSelectionIndicatorState.ValidWithWigConflict,
                TemplatedOutfitNote.None, RikkeConflict, null)
            .Should().Be(
                "Valid selection with a conflict: wig HighPoly_WigAA_HairFemaleNord03 (slot 31) would be added to " +
                "outfit ImperialOfficerOutfit, which holds Imperial Officer's Helmet (ArmorImperialHelmetOfficer) " +
                "(slots 31 and 42). Skyrim shows only one. Start a run to choose, or change the mod's wig handling.");
    }

    [Fact]
    public void WigNote_ForAStoredForward_DoesNotPromiseThatARunWillAsk()
    {
        var note = NpcSelectionIndicatorRules.BuildNote(NpcSelectionIndicatorState.ValidWithWigConflict,
            TemplatedOutfitNote.None, RikkeConflict, WigHeadgearChoice.Forward);

        note.Should().StartWith("Valid selection with a conflict: wig HighPoly_WigAA_HairFemaleNord03 (slot 31)");
        note.Should().Contain("Skyrim shows only one.");
        note.Should().NotContain("Start a run to choose");
        note.Should().EndWith("You chose to forward the wig for this NPC. Clear the remembered wig and headgear " +
                              "choices in Settings to choose again, or change the mod's wig handling.");
    }

    [Fact]
    public void WigNote_ListsEveryConflictOfTheNpc()
    {
        var two = new[]
        {
            RikkeConflict[0],
            new WigHeadgearConflict(
                RikkeConflict[0].Wig,
                new WigHeadgearConflictItem(MutagenFixtures.Fk("0A0B0C:Skyrim.esm"), "ClothesCirclet01", "Gold Circlet", new[] { 42 }),
                RikkeConflict[0].OutfitFormKey, RikkeConflict[0].OutfitEditorID, new[] { 42 }),
        };

        var note = NpcSelectionIndicatorRules.WigConflictNoteText(two, null);

        note.Should().Contain("which holds Imperial Officer's Helmet (ArmorImperialHelmetOfficer) (slots 31 and 42); wig ");
        note.Should().Contain("which holds Gold Circlet (ClothesCirclet01) (slot 42). Skyrim shows only one.");
    }

    [Fact]
    public void WhenBothApply_TheTooltipCarriesBothNotes_WigFirst()
    {
        var note = NpcSelectionIndicatorRules.BuildNote(NpcSelectionIndicatorState.ValidWithWigConflict,
            TemplatedOutfitNote.ReplacesTemplateClothing, RikkeConflict, null);

        note.Should().StartWith("Valid selection with a conflict: wig ");
        note.Should().EndWith(NpcSelectionIndicatorRules.OutfitReplacesTemplateNote);
        note!.IndexOf("Start a run to choose", StringComparison.Ordinal).Should().BeLessThan(
            note.IndexOf("'Include Outfit' is on", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------
    // The orange of the outfit note's hatches
    // ------------------------------------------------------------------------------------------

    [Theory]
    // theme, its green, its orange, and whether the theme's orange is kept
    [InlineData("Dark", "#90EE90", "#FFA500", true)]
    [InlineData("Light", "#008000", "#FFA500", true)]
    [InlineData("GitHub Light", "#1A7F37", "#9A6700", false)]  // a dark amber on a dark green
    [InlineData("Tokyo Night", "#9ECE6A", "#E0AF68", false)]   // two pale yellowish tones
    public void OutfitHatchColour_IsTheThemesOrange_OnlyWhenItShowsAgainstTheThemesGreen(
        string theme, string greenHex, string orangeHex, bool themeOrangeKept)
    {
        var green = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(greenHex);
        var orange = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(orangeHex);

        var hatch = NpcSelectionIndicatorRules.ResolveOutfitNoteHatchColor(green, orange);

        hatch.Should().Be(themeOrangeKept ? orange : NpcSelectionIndicatorRules.FallbackOrange, theme);
        NpcSelectionIndicatorRules.ColorDistance(hatch, green)
            .Should().BeGreaterThanOrEqualTo(NpcSelectionIndicatorRules.MinimumHatchContrast, theme);
    }

    [Fact]
    public void OutfitHatchColour_ForAThemeWithoutAnOrange_IsTheFixedOne()
    {
        NpcSelectionIndicatorRules.ResolveOutfitNoteHatchColor(System.Windows.Media.Colors.Green, null)
            .Should().Be(NpcSelectionIndicatorRules.FallbackOrange);
    }

    [Theory]
    [InlineData(NpcSelectionIndicatorState.None)]
    [InlineData(NpcSelectionIndicatorState.NotInLoadOrder)]
    [InlineData(NpcSelectionIndicatorState.SelectedModHasNoData)]
    [InlineData(NpcSelectionIndicatorState.Valid)]
    public void TheOriginalStates_HaveNoNote(NpcSelectionIndicatorState state)
    {
        // Even when a note's facts are handed in: a red or purple row is not "valid, with a note".
        NpcSelectionIndicatorRules.BuildNote(state, TemplatedOutfitNote.NeverWorn, RikkeConflict, null)
            .Should().BeNull();
    }
}
