using CharacterViewer.Rendering;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;

/// <summary>
/// Adapts NPC2's <see cref="NpcMeshResolver"/> to the rendering tier's
/// <see cref="INpcMeshDataSource"/>. <see cref="NpcIdentity.CacheKey"/> carries
/// a string-encoded <see cref="FormKey"/>; the adapter parses it back, looks up
/// the current LinkCache from the host environment, and delegates resolution.
///
/// <para><b>Caching invariant — do not route selection-dependent loads through
/// this adapter.</b> The rendering tier memoizes <see cref="ResolvedNpcMeshPaths"/>
/// per <see cref="NpcIdentity"/> and invalidates only when
/// <see cref="CurrentInvalidationToken"/> (the LinkCache) is replaced.
/// <see cref="Resolve"/> here depends on the user's CURRENT appearance
/// selection, which can change WITHOUT an environment rebuild — so the
/// FormKey-only identity + LinkCache token cannot see a selection switch, and
/// a memoized entry would serve the previous selection's record resolution
/// (head parts / worn armor / TXST). Callers that need the active selection
/// (or an explicit mod) must resolve paths themselves and use the viewer's
/// path-accepting <c>LoadAsync</c>, as both <c>VM_InternalMugshotPreview</c>
/// entry points do; this adapter remains to satisfy the rendering tier's
/// constructor dependency.</para>
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
        // Honor the user's appearance-mod selection for this NPC: records come
        // from the selected mod's plugins and assets are pulled from its data
        // folders, with the conflict-winning override + vanilla data folder as
        // fallback when the mod doesn't override a given record / file.
        return _resolver.ResolveForActiveSelection(formKey);
    }

    public object? CurrentInvalidationToken => _env.LinkCache;
}
