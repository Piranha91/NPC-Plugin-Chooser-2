using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>What the selection indicator of one row of the NPC list says.</summary>
public enum NpcSelectionIndicatorState
{
    /// <summary>No appearance selected: no indicator.</summary>
    None,

    /// <summary>Red: the NPC is not in the load order.</summary>
    NotInLoadOrder,

    /// <summary>Purple: the selected mod has no data (mugshots only).</summary>
    SelectedModHasNoData,

    /// <summary>Green: a valid selection.</summary>
    Valid,

    /// <summary>Green with orange hatches: valid, and 'Include Outfit' acts on an NPC a template
    /// dresses.</summary>
    ValidWithOutfitNote,

    /// <summary>Green with red hatches: valid, and the wig would be forwarded into an outfit that
    /// holds headgear on the same slot (no stored choice, or the stored choice Forward).</summary>
    ValidWithWigConflict,
}

/// <summary>What 'Include Outfit' does for an NPC its template dresses.</summary>
public enum TemplatedOutfitNote
{
    /// <summary>Nothing to say: no outfit included, or no template dresses the NPC.</summary>
    None,

    /// <summary>The outfit arrives by SkyPatcher directive and replaces the template's clothing.</summary>
    ReplacesTemplateClothing,

    /// <summary>The outfit is written to a record field the engine never reads for this NPC.</summary>
    NeverWorn,
}

/// <summary>
/// The decisions behind the NPC list's selection indicator, free of WPF state so they can be
/// tested as plain logic. The three original states (red, purple, green) are decided exactly as
/// before; the two hatched ones are refinements of green and never replace red or purple.
/// </summary>
public static class NpcSelectionIndicatorRules
{
    public const string OutfitReplacesTemplateNote =
        "Valid selection. 'Include Outfit' is on and this NPC is normally dressed by a template NPC. " +
        "The mod's outfit replaces the clothing the template gives it in game.";

    public const string OutfitNeverWornNote =
        "Valid selection. 'Include Outfit' is on, but this NPC is dressed by a template NPC, so the " +
        "outfit written to its record is never worn in game.";

    /// <summary>Per-NPC override first, else the mod's own setting: the rule the patcher, the
    /// screening pass and the outfit preview each apply.</summary>
    public static bool IncludeOutfitInEffect(bool modIncludesOutfits, OutfitOverride? npcOverride) =>
        npcOverride switch
        {
            OutfitOverride.No => false,
            OutfitOverride.Yes => true,
            _ => modIncludesOutfits,
        };

    /// <summary>
    /// The 'Include Outfit' note for one NPC.
    /// </summary>
    /// <param name="includeOutfitInEffect">See <see cref="IncludeOutfitInEffect"/>.</param>
    /// <param name="dressedByTemplate">The NPC's winning record names a Template and carries the
    /// Inventory template flag (<see cref="Settings.NpcIsDressedByTemplate"/>).</param>
    /// <param name="directiveReplacesTemplatedInventory">
    /// <see cref="Settings.OutfitDirectiveReplacesTemplatedInventory"/> for that record: the
    /// outfit is delivered by SkyPatcher directive.</param>
    public static TemplatedOutfitNote ResolveOutfitNote(bool includeOutfitInEffect, bool dressedByTemplate,
        bool directiveReplacesTemplatedInventory)
    {
        if (!includeOutfitInEffect || !dressedByTemplate) return TemplatedOutfitNote.None;
        return directiveReplacesTemplatedInventory
            ? TemplatedOutfitNote.ReplacesTemplateClothing
            : TemplatedOutfitNote.NeverWorn;
    }

    /// <summary>The wig-and-headgear question is asked only for a mod whose effective wig mode is
    /// 'Forward to Outfit'; under every other mode nothing is resolved at all.</summary>
    public static bool WigConflictQueryApplies(WigHandlingMode effectiveWigMode) =>
        effectiveWigMode == WigHandlingMode.ForwardToOutfit;

    /// <summary>
    /// Whether a conflict still stands for the row: it exists and the user has not answered it
    /// away. A stored Omit or Convert settles it; a stored Forward keeps it (the wig or the
    /// headgear will still be missing in game).
    /// </summary>
    public static bool WigConflictIsOpen(bool conflictExists, WigHeadgearChoice? storedChoice) =>
        conflictExists && storedChoice is null or WigHeadgearChoice.Forward;

    /// <summary>
    /// The state of one row. Red and purple are decided first and exactly as they always were;
    /// the notes only ever refine green, and the wig conflict outranks the outfit note.
    /// </summary>
    public static NpcSelectionIndicatorState Resolve(bool hasSelection, bool isInLoadOrder,
        bool selectedModHasData, TemplatedOutfitNote outfitNote, bool wigConflictIsOpen)
    {
        if (!hasSelection) return NpcSelectionIndicatorState.None;
        if (!isInLoadOrder) return NpcSelectionIndicatorState.NotInLoadOrder;
        if (!selectedModHasData) return NpcSelectionIndicatorState.SelectedModHasNoData;
        if (wigConflictIsOpen) return NpcSelectionIndicatorState.ValidWithWigConflict;
        if (outfitNote != TemplatedOutfitNote.None) return NpcSelectionIndicatorState.ValidWithOutfitNote;
        return NpcSelectionIndicatorState.Valid;
    }

    /// <summary>True for the two "valid, with a note" states, which are drawn hatched.</summary>
    public static bool IsHatched(NpcSelectionIndicatorState state) =>
        state is NpcSelectionIndicatorState.ValidWithOutfitNote or NpcSelectionIndicatorState.ValidWithWigConflict;

    public static string? OutfitNoteText(TemplatedOutfitNote note) => note switch
    {
        TemplatedOutfitNote.ReplacesTemplateClothing => OutfitReplacesTemplateNote,
        TemplatedOutfitNote.NeverWorn => OutfitNeverWornNote,
        _ => null,
    };

    /// <summary>
    /// The wig-and-headgear note, every name and slot number read from the records. With no
    /// stored choice the next run asks; with a stored Forward it does not, so the last sentence
    /// says where that answer can be taken back instead.
    /// </summary>
    public static string WigConflictNoteText(IReadOnlyList<WigHeadgearConflict> conflicts,
        WigHeadgearChoice? storedChoice)
    {
        string what = string.Join("; ", conflicts.Select(c =>
            $"wig {c.Wig.Label} ({WigHeadgearConflictDetector.FormatSlots(c.Wig.Slots)}) would be added to outfit " +
            $"{c.OutfitLabel}, which holds {c.Headgear.Label} " +
            $"({WigHeadgearConflictDetector.FormatSlots(c.Headgear.Slots)})"));

        string next = storedChoice == WigHeadgearChoice.Forward
            ? "You chose to forward the wig for this NPC. Clear the remembered wig and headgear choices in " +
              "Settings to choose again, or change the mod's wig handling."
            : "Start a run to choose, or change the mod's wig handling.";

        return $"Valid selection with a conflict: {what}. Skyrim shows only one. {next}";
    }

    /// <summary>
    /// The tooltip text of a row's note, or null for a row without one. When both notes apply
    /// the wig conflict leads (it is the state shown) and the outfit note follows.
    /// </summary>
    public static string? BuildNote(NpcSelectionIndicatorState state, TemplatedOutfitNote outfitNote,
        IReadOnlyList<WigHeadgearConflict>? conflicts, WigHeadgearChoice? storedChoice)
    {
        string? outfitText = OutfitNoteText(outfitNote);
        switch (state)
        {
            case NpcSelectionIndicatorState.ValidWithWigConflict:
                string wigText = WigConflictNoteText(conflicts ?? Array.Empty<WigHeadgearConflict>(), storedChoice);
                return outfitText == null ? wigText : wigText + Environment.NewLine + Environment.NewLine + outfitText;
            case NpcSelectionIndicatorState.ValidWithOutfitNote:
                return outfitText;
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Drawing
    // ---------------------------------------------------------------------------------------

    /// <summary>Theme resource keys of the indicator colours. Themes are loaded from disk and a
    /// theme may lack a key; the NPC list then falls back to a fixed colour.</summary>
    public const string ValidBrushKey = "StatusValidForeground";
    public const string NotInLoadOrderBrushKey = "StatusErrorForeground";
    public const string NoDataBrushKey = "SelectionNoDataBrush";

    /// <summary>The theme's orange (its "pending" status colour): the hatch colour of the
    /// 'Include Outfit' note. The wig conflict's hatch colour is
    /// <see cref="NotInLoadOrderBrushKey"/>, the theme's red.</summary>
    public const string OutfitNoteHatchBrushKey = "StatusPendingForeground";

    /// <summary>The orange used when the theme has none, or has one that would not show against
    /// its own green.</summary>
    public static readonly Color FallbackOrange = Color.FromRgb(0xFF, 0x8C, 0x00);

    /// <summary>The least distance, in RGB space, the theme's orange must keep from the theme's
    /// green for stripes of the two to be told apart in a bar a few pixels wide. Measured on the
    /// four shipped themes: Dark 196 and Light 258 pass; GitHub Light (a dark amber on a dark
    /// green, 141) and Tokyo Night (two pale yellowish tones, 73) do not.</summary>
    public const double MinimumHatchContrast = 150;

    /// <summary>
    /// The hatch colour of the 'Include Outfit' note: the theme's orange when it stands out
    /// against the valid green it is striped with, else <see cref="FallbackOrange"/>. Themes are
    /// user-editable files, so "the theme's orange" cannot be trusted to be readable here.
    /// </summary>
    public static Color ResolveOutfitNoteHatchColor(Color validGreen, Color? themeOrange) =>
        themeOrange is { } orange && ColorDistance(orange, validGreen) >= MinimumHatchContrast
            ? orange
            : FallbackOrange;

    public static double ColorDistance(Color a, Color b)
    {
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    /// <summary>Width of the indicator bar of the three solid states, as NpcsView draws it.</summary>
    public const double SolidBarWidth = 3;

    /// <summary>Width of the bar of the two hatched states: twice the solid one, so the diagonal
    /// hatches can be read. NpcsView takes the difference out of the row's padding, so the NPC's
    /// name stays where it was.</summary>
    public const double HatchedBarWidth = 6;

    /// <summary>Size of the hatching, in device-independent pixels: going down the bar, each
    /// colour's stripe is this tall where it crosses, so the pattern repeats every two of
    /// them. The stripes run at 45 degrees.</summary>
    public const double HatchPeriod = 4;

    /// <summary>
    /// The hatched brush of the "valid, with a note" states: diagonal stripes alternating the
    /// base colour (the theme's valid green) and the hatch colour. A repeating two-colour linear
    /// gradient with hard stops, in absolute coordinates so the stripes keep their size whatever
    /// the row's height. Frozen, so one instance serves every row.
    /// </summary>
    public static Brush CreateHatchedBrush(Color baseColor, Color hatchColor)
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            SpreadMethod = GradientSpreadMethod.Repeat,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(HatchPeriod, HatchPeriod),
        };
        brush.GradientStops.Add(new GradientStop(baseColor, 0.0));
        brush.GradientStops.Add(new GradientStop(baseColor, 0.5));
        brush.GradientStops.Add(new GradientStop(hatchColor, 0.5));
        brush.GradientStops.Add(new GradientStop(hatchColor, 1.0));
        brush.Freeze();
        return brush;
    }
}
