using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Display metadata for the wig/antler handling dropdowns, shared by the global
/// Settings dropdowns (<see cref="VM_Settings"/>) and the per-mod dropdowns
/// (<see cref="VM_ModSetting"/>) so both show the same friendly names in the
/// same order. Display order is deliberately decoupled from the enum's declared
/// order: persisted integers must never shift (ConvertToHeadParts is appended
/// after None in the enum), but in the UI the active handling modes come first
/// and "None" always sits last.
/// </summary>
public static class HandlingModeDisplay
{
    public static string ToDisplayString(WigHandlingMode mode) => mode switch
    {
        WigHandlingMode.ForwardToSkin => "Forward To Skin",
        WigHandlingMode.ForwardToOutfit => "Forward To Outfit",
        WigHandlingMode.ConvertToHeadParts => "Convert To Headparts",
        WigHandlingMode.None => "Leave As Is",
        _ => mode.ToString(),
    };

    public static string ToDisplayString(AntlerHandlingMode mode) => mode switch
    {
        AntlerHandlingMode.ForwardToSkin => "Forward To Skin",
        AntlerHandlingMode.ForwardToOutfit => "Forward To Outfit",
        AntlerHandlingMode.Remove => "Remove",
        AntlerHandlingMode.None => "Leave As Is",
        _ => mode.ToString(),
    };

    public static string ToDisplayString(WigHairTintMode mode) => mode switch
    {
        WigHairTintMode.Auto => "Auto (placeholder tints only)",
        WigHairTintMode.Always => "Always use NPC's hair color",
        WigHairTintMode.Never => "Keep the wig's own color",
        _ => mode.ToString(),
    };

    public static string ToDisplayString(TemplateHandlingMode mode) => mode switch
    {
        TemplateHandlingMode.InheritFromTemplate => "Use the template's appearance",
        TemplateHandlingMode.GiveEachNpcOwnCopy => "Give each NPC its own copy",
        _ => mode.ToString(),
    };

    public static readonly IReadOnlyList<TemplateHandlingMode> TemplateModesInDisplayOrder = new[]
    {
        TemplateHandlingMode.InheritFromTemplate,
        TemplateHandlingMode.GiveEachNpcOwnCopy,
    };

    public static readonly IReadOnlyList<WigHairTintMode> WigHairTintModesInDisplayOrder = new[]
    {
        WigHairTintMode.Auto,
        WigHairTintMode.Always,
        WigHairTintMode.Never,
    };

    public static readonly IReadOnlyList<WigHandlingMode> WigModesInDisplayOrder = new[]
    {
        WigHandlingMode.ForwardToSkin,
        WigHandlingMode.ForwardToOutfit,
        WigHandlingMode.ConvertToHeadParts,
        WigHandlingMode.None,
    };

    public static readonly IReadOnlyList<AntlerHandlingMode> AntlerModesInDisplayOrder = new[]
    {
        AntlerHandlingMode.ForwardToSkin,
        AntlerHandlingMode.ForwardToOutfit,
        AntlerHandlingMode.Remove,
        AntlerHandlingMode.None,
    };

    /// <summary>Confirmation shown when the user selects Convert To Headparts
    /// (globally or per mod). Declining reverts the dropdown to its previous
    /// value.</summary>
    public const string ConvertToHeadPartsWarning =
        "Convert To Headparts is an EXPERIMENTAL feature.\n\n" +
        "The wig armor is discarded and rebuilt as head part records, with the wig mesh baked " +
        "directly into each affected NPC's FaceGen file.\n\n" +
        "Please verify your output by spawning the affected NPCs in game and confirming they " +
        "don't have the dark face bug.\n\n" +
        "You may report NPCs for which the conversion is unsuccessful, but no promises that " +
        "anything can be done about it. (In the author's testing this works reliably, including " +
        "on HDT-enabled wigs.)\n\n" +
        "Are you sure you want to enable Convert To Headparts?";

    public const string ConvertToHeadPartsWarningTitle = "Confirm Wig Conversion";

    /// <summary>
    /// Warning shown wherever Forward To Outfit and SkyPatcher mode are both in
    /// play: either dropdown/checkbox in Settings, and again before patching.
    ///
    /// SkyPatcher's outfitDefault directive is a bare pointer assignment on the
    /// base TESNPC record — it never drives the engine's equip pass. Diagnosed
    /// 2026-07-30 against a real load order: SkyPatcher applied every directive
    /// (2931/2931 outfits, 3025/3025 visuals) and then touched nothing at
    /// runtime, yet a shifting subset of NPCs spawned naked with the forwarded
    /// outfit sitting in their inventory tagged "(Outfit)". Console
    /// <c>resetinventory</c> dresses the affected NPC, confirming an init-time
    /// equip failure. The minted records are item-for-item identical to the ones
    /// record mode writes successfully, so nothing in the output distinguishes a
    /// working NPC from a broken one and there is nothing here to fix or detect.
    /// Skin- and HeadPart-carried wigs ride on record fields instead and were
    /// verified unaffected in the same session.
    /// </summary>
    public const string SkyPatcherForwardToOutfitWarning =
        "SkyPatcher applies outfits unreliably.\n\n" +
        "It repoints an NPC's default outfit but never equips it. The game intermittently " +
        "leaves the forwarded outfit in the NPC's inventory unworn, so the NPC spawns naked. " +
        "Which NPCs are affected shifts between playthroughs, and nothing in the output tells " +
        "them apart — N.P.C.2 can neither detect nor correct this.\n\n" +
        "In SkyPatcher mode, forward wigs as Skin or Headparts instead. Both are written into " +
        "records and are unaffected.";

    public const string SkyPatcherForwardToOutfitWarningTitle = "SkyPatcher + Forward To Outfit";
}
