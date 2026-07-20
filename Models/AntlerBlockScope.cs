namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// How broadly a manually-designated antler head part (by EditorID) is treated
/// as an antler across the load order. Controls only ELIGIBILITY — actual
/// removal still requires the mod's Antler Handling Mode to be Remove. Global
/// setting (Settings.ManualAntlerBlockScope); the same scope applies to every
/// designation.
/// </summary>
public enum AntlerBlockScope
{
    /// <summary>Block the EditorID on any NPC in any mod (the default —
    /// "regardless of source mod").</summary>
    AllNpcs,

    /// <summary>Block the EditorID only within the mod(s) it was designated in.</summary>
    SameMod,

    /// <summary>Block the EditorID only on the specific NPC(s) it was designated
    /// on (regardless of which mod provides that NPC's appearance).</summary>
    SpecificNpc
}
