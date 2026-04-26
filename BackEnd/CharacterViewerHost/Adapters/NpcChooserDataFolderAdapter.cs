using CharacterViewer.Rendering;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;

/// <summary>
/// Adapts NPC2's <see cref="EnvironmentStateProvider"/> to the slimmer
/// <see cref="IDataFolderProvider"/> the viewer asset resolver needs. The
/// invalidation token is the active LinkCache instance — Mutagen replaces it
/// when the load order is rebuilt, which is exactly when downstream caches
/// (loose-file resolutions, BSA-extraction map) need to drop and re-resolve.
/// </summary>
public sealed class NpcChooserDataFolderAdapter : IDataFolderProvider
{
    private readonly EnvironmentStateProvider _env;

    public NpcChooserDataFolderAdapter(EnvironmentStateProvider env) => _env = env;

    // EnvironmentStateProvider.DataFolderPath is a Noggog DirectoryPath; cast
    // through ToString() to expose its string representation to the rendering tier.
    public string DataFolderPath => _env.DataFolderPath.ToString();

    public object? CurrentLoadOrderToken => _env.LinkCache;
}
