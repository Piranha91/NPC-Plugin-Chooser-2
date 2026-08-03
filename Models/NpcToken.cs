using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Models;


/// <summary>
/// A data class to structure the contents of the NPC_Token.json file.
/// </summary>
public class NpcToken
{
    public string CreationDate { get; set; } = string.Empty;
    public List<ModKey> CreatedPlugins { get; set; } = new();

    /// <summary>
    /// The output mode that produced this run: the <see cref="Models.PatchingMode"/> name plus
    /// whether SkyPatcher output was on. "Validate Output" grades the deployed files against the
    /// CURRENT settings (effective wig/antler modes included), so validating an output produced
    /// under a different mode floods the report with false mismatches — these let it say so up
    /// front instead. Null in tokens written by older versions, which readers must treat as
    /// "unknown", never as a mismatch.
    /// </summary>
    public string? PatchingMode { get; set; }
    public bool? UseSkyPatcherMode { get; set; }

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

    /// <summary>
    /// Relative paths (regularized, e.g. <c>meshes\actors\character\facegendata\facegeom\...</c>)
    /// of FaceGen meshes this run rewrote AFTER copying them out of the appearance mod: baked hair
    /// or antler shapes stripped, a wig scene baked in, or shapes renamed to follow a duplicated
    /// head part.
    ///
    /// <para>Such a file is deliberately no longer byte-identical to the mod's own copy, which is
    /// otherwise how "Validate Output" proves nothing in the load order overwrote this app's
    /// output. Without this list an intentional edit reads as a lost conflict.</para>
    /// </summary>
    public HashSet<string> EditedFaceGen { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}