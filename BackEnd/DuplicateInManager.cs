using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class DuplicateInManager
{
    private Dictionary<ModKey, Dictionary<FormKey, FormKey>> _contextMappings = new();

    private readonly EnvironmentStateProvider _environmentStateProvider;

    public DuplicateInManager(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
    }

    public void Reinitialize()
    {
        _contextMappings.Clear();
    }

    /// <summary>
    /// Tries to deep copy a FormLink into another FormLink, copying in records and remapping recursivley
    /// </summary>
    /// <param name="targetFormLink">The FormLink to be modified).</param>
    /// <param name="formLinkToCopy">The FormLink to copy.</param>
    /// <param name="modToDuplicateInto">The mod that will contain the modified FromLink data.</param>
    /// /// <param name="modKeysToDuplicateFrom">The mods whose records are eligible to be deep copied in.</param>
    /// /// <param name="rootContextModKey">The mod which is the source override of "formLinkToCopy".</param>
    /// <returns>No return; modification in-place.</returns>
    public void DuplicateInFormLink<TMod>(
        IFormLink<IMajorRecordGetter> targetFormLink,
        IFormLinkGetter<IMajorRecordGetter> formLinkToCopy,
        TMod modToDuplicateInto,
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        ModKey rootContextModKey,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        if (formLinkToCopy.IsNull)
        {
            targetFormLink.SetToNull();
            return;
        }

        var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts(formLinkToCopy);
        var matchedContext = contexts.FirstOrDefault(ctx => modKeysToDuplicateFrom.Contains(ctx.ModKey));

        if (matchedContext == null)
        {
            targetFormLink.SetTo(formLinkToCopy.FormKey);
            return;
        }
        
        DuplicateFromOnlyReferencedGetters(modToDuplicateInto, matchedContext.Record, modKeysToDuplicateFrom, 
            rootContextModKey, false, typesToInspect);

        if (_contextMappings.ContainsKey(rootContextModKey) &&
            _contextMappings[rootContextModKey].ContainsKey(formLinkToCopy.FormKey))
        {
            var deepCopiedFormKey = _contextMappings[rootContextModKey][formLinkToCopy.FormKey];
            targetFormLink.SetTo(deepCopiedFormKey);
        }
        else
        {
            targetFormLink.SetTo(formLinkToCopy.FormKey);
        }
    }

    public void DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IEnumerable<IMajorRecordGetter> recordsToDuplicate,
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        ModKey rootContextModKey,
        bool onlySubRecords,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        Dictionary<FormKey, FormKey> mapping = new();
        if (_contextMappings.TryGetValue(rootContextModKey, out var storedMapping))
        {
            mapping = storedMapping;
        }
        else
        {
            mapping = new Dictionary<FormKey, FormKey>();
            _contextMappings.Add(rootContextModKey, mapping);
        }
        
        modToDuplicateInto.DuplicateFromOnlyReferencedGetters(
            recordsToDuplicate,
            _environmentStateProvider.LinkCache,
            modKeysToDuplicateFrom,
            onlySubRecords,
            ref mapping,
            typesToInspect);
    }

    // convenience overload for a single ModKey
    public void DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IEnumerable<IMajorRecordGetter> recordsToDuplicate,
        ModKey modKeyToDuplicateFrom,
        bool onlySubRecords,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        DuplicateFromOnlyReferencedGetters(
            modToDuplicateInto,
            recordsToDuplicate,
            new[] { modKeyToDuplicateFrom },
            modKeyToDuplicateFrom,
            onlySubRecords,
            typesToInspect);
    }
    
    // convenience overload for a single Record
    public void DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IMajorRecordGetter recordToDuplicate,
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        ModKey rootContextModKey,
        bool onlySubRecords,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        DuplicateFromOnlyReferencedGetters(
            modToDuplicateInto,
            new[] { recordToDuplicate },
            modKeysToDuplicateFrom,
            rootContextModKey,
            onlySubRecords,
            typesToInspect);
    }
    
    // convenience overload for a single Record and ModKey
    public void DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IMajorRecordGetter recordToDuplicate,
        ModKey modKeyToDuplicateFrom,
        bool onlySubRecords,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        DuplicateFromOnlyReferencedGetters(
            modToDuplicateInto,
            new[] { recordToDuplicate },
            new[] { modKeyToDuplicateFrom },
            modKeyToDuplicateFrom,
            onlySubRecords,
            typesToInspect);
    }
}