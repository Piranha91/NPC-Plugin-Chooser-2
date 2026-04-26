using CharacterViewer.Rendering;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;

/// <summary>
/// Adapts NPC2's <see cref="NpcMeshResolver"/> to the rendering tier's
/// <see cref="INpcMeshDataSource"/>. <see cref="NpcIdentity.CacheKey"/> carries
/// a string-encoded <see cref="FormKey"/>; the adapter parses it back, looks up
/// the current LinkCache from the host environment, and delegates resolution.
/// </summary>
public sealed class NpcChooserNpcMeshDataSourceAdapter : INpcMeshDataSource
{
    private readonly NpcMeshResolver _resolver;
    private readonly EnvironmentStateProvider _env;

    public NpcChooserNpcMeshDataSourceAdapter(NpcMeshResolver resolver, EnvironmentStateProvider env)
    {
        _resolver = resolver;
        _env = env;
    }

    public ResolvedNpcMeshPaths? Resolve(NpcIdentity identity)
    {
        if (!FormKey.TryFactory(identity.CacheKey, out var formKey)) return null;
        var linkCache = _env.LinkCache;
        if (linkCache == null) return null;
        return _resolver.Resolve(formKey, linkCache);
    }

    public object? CurrentInvalidationToken => _env.LinkCache;
}
