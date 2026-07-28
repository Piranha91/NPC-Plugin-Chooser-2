using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Per-NPC resolution scope. When non-null, <see cref="NpcMeshResolver"/>
/// reads records from <see cref="PreferredModKeys"/> first (falling back to
/// the load-order winner via <see cref="RecordHandler"/>) and rebases asset
/// paths to absolute when the file is present under any
/// <see cref="PreferredFolderPaths"/> entry. Mirrors the behavior of the
/// legacy <c>PortraitCreator.FindNpcNifPath</c> + <c>AssetHandler.FindAssetSource</c>
/// pair for the in-process renderer.
/// </summary>
public sealed class NpcResolutionContext
{
    /// <summary>Plugins owned by the user-selected mod, in the order recorded by
    /// <see cref="Models.ModSetting.CorrespondingModKeys"/> (last = winner).</summary>
    public IReadOnlyList<ModKey> PreferredModKeys { get; init; } = System.Array.Empty<ModKey>();

    /// <summary>Mod data folders, in <see cref="Models.ModSetting.CorrespondingFolderPaths"/>
    /// order (last = override winner). Loose-file lookups iterate this in
    /// reverse so the override beats the base.</summary>
    public IReadOnlyList<string> PreferredFolderPaths { get; init; } = System.Array.Empty<string>();

    /// <summary>Folder name set passed to <see cref="RecordHandler"/> as the
    /// fall-back disk-discovery scope when the plugin isn't already cached.</summary>
    public HashSet<string> FallBackFolderNames { get; init; } = new();

    /// <summary>
    /// Plugins of the mod that ORIGINALLY added this NPC, consulted below the selected mod and
    /// above vanilla. A mod may legitimately ship a face tint without the mesh (or a record
    /// without either) and let the origin supply the rest — the FaceGen ladder sources exactly
    /// that case — so without the origin in scope the renderer draws a headless body for a whole
    /// class of otherwise healthy NPCs.
    /// </summary>
    public IReadOnlyList<ModKey> OriginModKeys { get; init; } = System.Array.Empty<ModKey>();

    /// <summary>Data folders of the origin mod, paired with <see cref="OriginModKeys"/>. Kept
    /// separate from <see cref="PreferredFolderPaths"/> so archive lookups stay folder-scoped to
    /// the mod that actually owns them.</summary>
    public IReadOnlyList<string> OriginFolderPaths { get; init; } = System.Array.Empty<string>();

    /// <summary>Set when <see cref="Models.ModSetting.NpcPluginDisambiguation"/>
    /// pins this NPC to a specific plugin within <see cref="PreferredModKeys"/>.
    /// Resolution tries this key first.</summary>
    public ModKey? DisambiguationModKey { get; init; }
}
