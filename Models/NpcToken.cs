using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Models;


/// <summary>
/// A data class to structure the contents of the NPC_Token.json file.
/// </summary>
public class NpcToken
{
    public string CreationDate { get; set; } = string.Empty;
    public List<ModKey> CreatedPlugins { get; set; } = new();
    public Dictionary<FormKey, NpcAppearanceData> ProcessedNpcs { get; set; } = new();

    /// <summary>
    /// NPCs that had a selection but that this run deliberately did NOT patch, mapped to a
    /// human-readable reason: rejected by pre-run screening, or left alone by the FaceGen ladder
    /// because patching would have produced the dark-face bug.
    ///
    /// <para>Written so "Validate Output" can tell "NPC2 never touched this NPC" apart from "NPC2
    /// patched it and something went wrong", and can quote the reason instead of sending the user
    /// back to a run log they may no longer have. Absent from tokens written by older versions,
    /// which is why every reader must treat an empty map as "unknown", not as "nothing skipped".</para>
    /// </summary>
    public Dictionary<FormKey, string> SkippedNpcs { get; set; } = new();
}