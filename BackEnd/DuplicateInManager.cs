using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class DuplicateInManager
{
    private Dictionary<FormKey, FormKey> _mapping = new();
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
    
    // convenience overload for a single Record
    public void DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IMajorRecordGetter recordToDuplicate,
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        DuplicateFromOnlyReferencedGetters(
            modToDuplicateInto,
            new[] { recordToDuplicate },
            modKeysToDuplicateFrom,
            typesToInspect);
    }
    
    // convenience overload for a single Record and ModKey
    public void DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IMajorRecordGetter recordToDuplicate,
        ModKey modKeyToDuplicateFrom,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        DuplicateFromOnlyReferencedGetters(
            modToDuplicateInto,
            new[] { recordToDuplicate },
            new[] { modKeyToDuplicateFrom },
            typesToInspect);
    }
}