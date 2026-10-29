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
    /// the <see cref="Models.OutputBackend"/> (and <see cref="Models.RecastMode"/> under Recast).
    /// "Validate Output" grades the deployed files against the CURRENT settings (effective
    /// wig/antler modes included), so validating an output produced under a different mode floods
    /// the report with false mismatches — these let it say so up front instead. Null in tokens
    /// written by older versions, which readers must treat as "unknown", never as a mismatch.
    /// </summary>
    public string? PatchingMode { get; set; }
    public string? OutputBackend { get; set; }
    public string? RecastMode { get; set; }

    /// <summary>
    /// Written by versions before <see cref="OutputBackend"/> existed; still read so their tokens
    /// keep reporting the mode they ran under. New tokens leave it null. Use
    /// <see cref="GetEffectiveOutputBackend"/> rather than reading either field directly.
    /// </summary>
    public bool? UseSkyPatcherMode { get; set; }

    /// <summary>The backend this run used, or null when the token predates both fields. A method
    /// rather than a property so it is not serialized into the token.</summary>
    public string? GetEffectiveOutputBackend() => OutputBackend ?? UseSkyPatcherMode switch
    {
        true => nameof(Models.OutputBackend.SkyPatcher),
        false => nameof(Models.OutputBackend.ESP),
        null => null,
    };

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

    /// <summary>
    /// Assets the output REFERENCES but deliberately did not copy, resolved from the user's live
    /// data folder at patch time (out-of-scope references, or a mod with "Copy Assets" unchecked).
    /// The run keeps them working two ways — archive-packed assets pin their loader plugin as an
    /// output MASTER, loose ones are recorded here — and "Validate Output" re-verifies both
    /// against the CURRENT setup. Null in tokens written by older versions, which readers must
    /// treat as "unknown", never as "no dependencies".
    /// </summary>
    public AssetDependencyLedger? AssetDependencies { get; set; }
}

/// <summary>
/// The runtime-dependency half of <see cref="NpcToken"/>: what the user's mod configuration must
/// keep supplying for the generated output to render correctly. See
/// <c>AssetHandler.ResolveRuntimeDependenciesAsync</c> for how it is populated.
/// </summary>
public class AssetDependencyLedger
{
    /// <summary>
    /// Plugins added as EXTRA masters of the output plugin because one of their data-folder
    /// archives supplies referenced assets that were not copied. The game refuses to load the
    /// output without its masters, which is exactly the guarantee: the archive stays loaded.
    /// </summary>
    public List<ArchiveDependency> ArchiveMasters { get; set; } = new();

    /// <summary>
    /// Regularized data-relative paths of referenced-but-not-copied assets satisfied by LOOSE
    /// files in the data folder at patch time. Loose files cannot be protected by mastering
    /// (they belong to no plugin), so "Validate Output" checks each still exists and warns
    /// when the supplying mod was removed or disabled.
    /// </summary>
    public List<string> LooseFiles { get; set; } = new();
}

/// <summary>One extra-master entry of <see cref="AssetDependencyLedger.ArchiveMasters"/>.</summary>
public class ArchiveDependency
{
    /// <summary>The plugin the output was mastered to.</summary>
    public ModKey Plugin { get; set; }

    /// <summary>Archive file names (not full paths — the data folder moves between machines)
    /// that supplied the assets.</summary>
    public List<string> Archives { get; set; } = new();

    /// <summary>Regularized data-relative paths of the assets this plugin's archives supply.</summary>
    public List<string> Assets { get; set; } = new();
}