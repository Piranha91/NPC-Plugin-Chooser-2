namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// What the user chose for ONE NPC whose wig, under <see cref="WigHandlingMode.ForwardToOutfit"/>,
/// would be added to an outfit that already holds headgear on the same biped slot (Legate Rikke's
/// wig on slot 31 against the Imperial officer helmet on slots 31 and 42). The game shows only one
/// of two items on a slot, so one of the two goes missing; the selection stays valid and the user
/// decides which (user decision of 2026-11-26). Stored per TARGET NPC in
/// <see cref="Settings.WigHeadgearConflictChoices"/>; an NPC with no entry is treated as
/// <see cref="Forward"/> and reported in the end-of-run warnings.
/// <para>Serialized by name (StringEnumConverter), so members may be added but not renamed.</para>
/// </summary>
public enum WigHeadgearChoice
{
    /// <summary>Forward the wig into the outfit anyway, as the wig mode asks. The run log still
    /// lists the NPC under the wig-and-headgear warning.</summary>
    Forward,

    /// <summary>Take the wig off the skin as ForwardToOutfit does, but do not add it to the
    /// outfit: the NPC keeps the headgear and has no wig.</summary>
    Omit,

    /// <summary>Deliver this NPC's wig as head parts instead (see
    /// <see cref="WigHandlingMode.ConvertToHeadParts"/>), so wig and headgear no longer compete
    /// for a slot.</summary>
    ConvertToHeadParts,
}
