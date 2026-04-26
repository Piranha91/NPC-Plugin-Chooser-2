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

    /// <summary>Set when <see cref="Models.ModSetting.NpcPluginDisambiguation"/>
    /// pins this NPC to a specific plugin within <see cref="PreferredModKeys"/>.
    /// Resolution tries this key first.</summary>
    public ModKey? DisambiguationModKey { get; init; }
}
