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
        WigHandlingMode.None => "None",
        _ => mode.ToString(),
    };

    public static string ToDisplayString(AntlerHandlingMode mode) => mode switch
    {
        AntlerHandlingMode.ForwardToSkin => "Forward To Skin",
        AntlerHandlingMode.ForwardToOutfit => "Forward To Outfit",
        AntlerHandlingMode.Remove => "Remove",
        AntlerHandlingMode.None => "None",
        _ => mode.ToString(),
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
}
