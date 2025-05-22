using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class DuplicateInManager
{
    private Dictionary<FormKey, FormKey> _mapping;
    private readonly EnvironmentStateProvider _environmentStateProvider;

    public DuplicateInManager(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
    }

    public void Reinitialize()
    {
        _mapping.Clear();
    }

    public void DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IEnumerable<IMajorRecordGetter> recordsToDuplicate,
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        modToDuplicateInto.DuplicateFromOnlyReferencedGetters(
            recordsToDuplicate,
            _environmentStateProvider.LinkCache,
            modKeysToDuplicateFrom,
            ref _mapping,
            typesToInspect);
    }

    // convenience overload for a single ModKey
    public void DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IEnumerable<IMajorRecordGetter> recordsToDuplicate,
        ModKey modKeyToDuplicateFrom,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        DuplicateFromOnlyReferencedGetters(
            modToDuplicateInto,
            recordsToDuplicate,
            new[] { modKeyToDuplicateFrom },
            typesToInspect);
    }
}