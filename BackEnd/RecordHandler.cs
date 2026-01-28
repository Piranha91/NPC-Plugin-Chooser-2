using Microsoft.Build.Tasks;
using System.Collections.Concurrent;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class RecordHandler
{
    // Outer Key: Appearance Mod Name
    // Inner Key: FormKey from source plugin
    // Value: FormKey of merged-in record in output plugin
    private Dictionary<FormKey, FormKey> _currentDuplicateInMappings = new();
    private HashSet<IFormLinkGetter> _currenTraversedFormLinks = new();
    
    // For converting plugins into linkcaches and avoiding having to resolve all contexts to get mod-specific records
    private ConcurrentDictionary<ModKey, ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>> _modLinkCaches = new();

    private readonly EnvironmentStateProvider _environmentStateProvider;
    private PluginProvider _pluginProvider;
    private readonly Settings _settings;

    public RecordHandler(EnvironmentStateProvider environmentStateProvider, PluginProvider pluginProvider, Settings settings)
    {
        _environmentStateProvider = environmentStateProvider;
        _pluginProvider = pluginProvider;
        _settings = settings;
    }

    public void ResetMapping()
    {
        _currentDuplicateInMappings.Clear();
        _currenTraversedFormLinks.Clear();
    }

    public void PrimeLinkCachesFor(IEnumerable<ModKey> modKeys, HashSet<string> fallBackModFolderNames)
    {
        foreach (var modKey in modKeys)
        {
            if (_modLinkCaches.ContainsKey(modKey)) continue;

            if (_pluginProvider.TryGetPlugin(modKey, fallBackModFolderNames, out var plugin) && plugin != null)
            {
                _modLinkCaches.TryAdd(modKey, new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(plugin, new LinkCachePreferences()));
            }
        }
    }

    public void ClearLinkCachesFor(IEnumerable<ModKey> modKeys)
    {
        foreach (var modKey in modKeys)
        {
            _modLinkCaches.TryRemove(modKey, out _);
        }
    }

    private bool TryAddPluginToCaches(ModKey modKey, HashSet<string> fallBackModFolderNames)
    {
        // Use GetOrAdd for an atomic "get or create" operation.
        // The value factory (the second argument) is only executed if the key is not already present.
        var linkCache = _modLinkCaches.GetOrAdd(modKey, key =>
        {
            // Logic to create the link cache if it doesn't exist.
            var modListing = _environmentStateProvider.LoadOrder?.TryGetValue(key);
            if (modListing != null && modListing.Mod != null)
            {
                return new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(modListing.Mod, new LinkCachePreferences());
            }

            if (_pluginProvider.TryGetPlugin(key, fallBackModFolderNames, out var plugin) && plugin != null)
            {
                return new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(plugin, new LinkCachePreferences());
            }

            return null; // Return null if it can't be created.
        });

        // The method succeeds if the linkCache is not null (either it existed before or was successfully created).
        return linkCache != null;
    }

    #region Merge In New Records
    
    /// <summary>
    /// Tries to deep copy a FormLink into another FormLink, copying in records and remapping recursivley
    /// If the FormLink target is not contained in modKeysToDuplicateFrom, simply adds the FormLink
    /// </summary>
    /// <param name="targetFormLink">The FormLink to be modified).</param>
    /// <param name="formLinkToCopy">The FormLink to copy.</param>
    /// <param name="modToDuplicateInto">The mod that will contain the modified FromLink data.</param>
    /// /// <param name="modKeysToDuplicateFrom">The mods whose records are eligible to be deep copied in.</param>
    /// /// <param name="rootContextModKey">The mod which is the source override of "formLinkToCopy".</param>
    /// <returns>No return; modification in-place.</returns>
    public List<MajorRecord> DuplicateInOrAddFormLink<TMod>(
        IFormLink<IMajorRecordGetter> targetFormLink,
        IFormLinkGetter<IMajorRecordGetter> formLinkToCopy,
        TMod modToDuplicateInto,
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        ModKey rootContextModKey, 
        bool handleInjectedRecords,
        HashSet<string> fallBackModFolderNames,
        ref List<string> exceptionStrings,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        List<MajorRecord> mergedInRecords = new();
        if (formLinkToCopy.IsNull)
        {
            targetFormLink.SetToNull();
            return mergedInRecords;
        }
        
        if (_currentDuplicateInMappings.TryGetValue(targetFormLink.FormKey, out var remappedFormKey))
        {
            targetFormLink.SetTo(remappedFormKey);
            return mergedInRecords;
        }
        
        if (!modKeysToDuplicateFrom.Contains(formLinkToCopy.FormKey.ModKey) && !handleInjectedRecords)
        {
            targetFormLink.SetTo(formLinkToCopy);
            return mergedInRecords;
        }

        if (!TryGetRecordFromMods(formLinkToCopy, modKeysToDuplicateFrom, fallBackModFolderNames, RecordLookupFallBack.None, out var record) || record == null)
        {
            targetFormLink.SetTo(formLinkToCopy);
            return mergedInRecords;
        }
        
        mergedInRecords = DuplicateFromOnlyReferencedGetters(modToDuplicateInto, record, modKeysToDuplicateFrom, 
            rootContextModKey, false, handleInjectedRecords, fallBackModFolderNames, ref exceptionStrings, typesToInspect);

        if (_currentDuplicateInMappings.ContainsKey(formLinkToCopy.FormKey))
        {
            var deepCopiedFormKey = _currentDuplicateInMappings[formLinkToCopy.FormKey];
            targetFormLink.SetTo(deepCopiedFormKey);
        }
        else
        {
            targetFormLink.SetTo(formLinkToCopy.FormKey);
        }
        
        return mergedInRecords;
    }

    private bool ExplicitRecordCheck(IFormLinkGetter<IMajorRecordGetter> formLinkToCopy,IEnumerable<ModKey> modKeysToDuplicateFrom, HashSet<string> fallBackModFolderNames, out IMajorRecordGetter? recordGetter)
    {
        recordGetter = null;
        // extra check
        foreach (var modKey in modKeysToDuplicateFrom)
        {
            if (TryGetRecordGetterFromMod(formLinkToCopy, modKey, fallBackModFolderNames, RecordLookupFallBack.None,
                    out recordGetter))
            {
                return true;
            }
        }

        return false;
    }

    public List<MajorRecord> DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IEnumerable<IMajorRecordGetter> recordsToDuplicate,
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        ModKey rootContextModKey,
        bool onlySubRecords, 
        bool handleInjectedRecords,
        HashSet<string> fallBackModFolderNames,
        ref List<string> exceptionStrings,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        using var _ = ContextualPerformanceTracer.Trace("RecordHandler.DuplicateFromOnlyReferencedGetters");
        
        return modToDuplicateInto.DuplicateFromOnlyReferencedGetters<TMod, ISkyrimModGetter>(
            recordsToDuplicate,
            this,
            modKeysToDuplicateFrom,
            onlySubRecords,
            handleInjectedRecords,
            fallBackModFolderNames,
            RecordLookupFallBack.None, // Don't fall back to winning override or origin - if the chain of new records breaks, don't search through overrides
            // Override searching is the job of RecordHandler.DeepGetOverriddenDependencyRecords()
            ref _currentDuplicateInMappings,
            ref _currenTraversedFormLinks,
            ref exceptionStrings,
            typesToInspect);
    }

    // convenience overload for a single ModKey
    public List<MajorRecord> DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IEnumerable<IMajorRecordGetter> recordsToDuplicate,
        ModKey modKeyToDuplicateFrom,
        bool onlySubRecords, 
        bool handleInjectedRecords,
        HashSet<string> fallBackModFolderNames,
        ref List<string> exceptionStrings,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        return DuplicateFromOnlyReferencedGetters(
            modToDuplicateInto,
            recordsToDuplicate,
            new[] { modKeyToDuplicateFrom },
            modKeyToDuplicateFrom,
            onlySubRecords,
            handleInjectedRecords,
            fallBackModFolderNames,
            ref exceptionStrings,
            typesToInspect);
    }
    
    // convenience overload for a single Record
    public List<MajorRecord> DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IMajorRecordGetter recordToDuplicate,
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        ModKey rootContextModKey,
        bool onlySubRecords,
        bool handleInjectedRecords,
        HashSet<string> fallBackModFolderNames,
        ref List<string> exceptionStrings,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        return DuplicateFromOnlyReferencedGetters(
            modToDuplicateInto,
            new[] { recordToDuplicate },
            modKeysToDuplicateFrom,
            rootContextModKey,
            onlySubRecords,
            handleInjectedRecords,
            fallBackModFolderNames,
            ref exceptionStrings,
            typesToInspect);
    }
    
    // convenience overload for a single Record and ModKey
    public List<MajorRecord> DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IMajorRecordGetter recordToDuplicate,
        ModKey modKeyToDuplicateFrom,
        bool onlySubRecords,
        bool handleInjectedRecords,
        HashSet<string> fallBackModFolderNames,
        ref List<string> exceptionStrings,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        return DuplicateFromOnlyReferencedGetters(
            modToDuplicateInto,
            new[] { recordToDuplicate },
            new[] { modKeyToDuplicateFrom },
            modKeyToDuplicateFrom,
            onlySubRecords,
            handleInjectedRecords,
            fallBackModFolderNames,
            ref exceptionStrings,
            typesToInspect);
    }
    #endregion

    #region Collect Overrides of Existing Records
    public HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>
        DeepGetOverriddenDependencyRecords(IMajorRecordGetter majorRecordGetter, List<ModKey> relevantContextKeys, HashSet<FormKey> searchedFormKeys, HashSet<string> fallBackModFolderNames, int maxNestedIntervalDepth, CancellationToken ct)
    {
        using var _ = ContextualPerformanceTracer.Trace("RecordHandler.DeepGetOverriddenDependencyRecords");
        var containedFormLinks = majorRecordGetter.EnumerateFormLinks().ToArray();
        foreach (var modKey in relevantContextKeys)
        {
            TryAddPluginToCaches(modKey, fallBackModFolderNames);
        }
        HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> dependencyContexts = new();
        foreach (var link in containedFormLinks)
        {
            ct.ThrowIfCancellationRequested();
            CollectOverriddenDependencyRecords(link, relevantContextKeys, dependencyContexts, maxNestedIntervalDepth, 0, searchedFormKeys, ct);
        }
        return dependencyContexts.ToHashSet();;
    }
    
    /// <summary>
    /// Gets ALL override records from the specified plugins, regardless of NPC traversal.
    /// This is a simpler but less targeted approach compared to DeepGetOverriddenDependencyRecords.
    /// </summary>
    /// <param name="relevantContextKeys">The ModKeys of plugins to search for overrides.</param>
    /// <param name="searchedFormKeys">FormKeys that have already been processed (will be updated).</param>
    /// <param name="fallBackModFolderNames">Fallback folder paths for plugin loading.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A HashSet of all override record contexts found in the specified plugins.</returns>
    public HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>
        GetAllOverriddenDependencyRecords(List<ModKey> relevantContextKeys, HashSet<FormKey> searchedFormKeys, 
            HashSet<string> fallBackModFolderNames, CancellationToken ct)
    {
        using var _ = ContextualPerformanceTracer.Trace("RecordHandler.GetAllOverriddenDependencyRecords");
        
        foreach (var modKey in relevantContextKeys)
        {
            TryAddPluginToCaches(modKey, fallBackModFolderNames);
        }
        
        HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> dependencyContexts = new();
        
        foreach (var modKey in relevantContextKeys)
        {
            ct.ThrowIfCancellationRequested();
            
            if (!_modLinkCaches.TryGetValue(modKey, out var linkCache) || linkCache == null)
            {
                continue;
            }
            
            // Get the plugin to iterate through all its records
            if (!_pluginProvider.TryGetPlugin(modKey, fallBackModFolderNames, out var plugin) || plugin == null)
            {
                continue;
            }
            
            // Iterate through all major records in the plugin
            foreach (var record in plugin.EnumerateMajorRecords())
            {
                ct.ThrowIfCancellationRequested();
                
                // Skip if already processed
                if (searchedFormKeys.Contains(record.FormKey))
                {
                    continue;
                }

                if (record is INpcGetter)
                {
                    continue; // patcher has explicit logic to manually handle NPCs
                }
                
                // Check if this is an override (FormKey's ModKey is NOT one of the appearance mod's plugins)
                if (!relevantContextKeys.Contains(record.FormKey.ModKey))
                {
                    // This is an override record (originates from outside the mod's plugins)
                    searchedFormKeys.Add(record.FormKey);
    
                    try
                    {
                        var context = linkCache.ResolveContext(record.FormKey, record.Registration.GetterType);
                        if (context != null)
                        {
                            dependencyContexts.Add(context);
                        }
                    }
                    catch
                    {
                        // Skip records that can't be resolved to a context
                    }
                }
            }
        }
    
    return dependencyContexts;
}
    
    /// <summary>
    /// Duplicates ALL override records from the specified plugins as new records.
    /// This is a simpler but less targeted approach compared to DuplicateInOverrideRecords.
    /// </summary>
    public HashSet<IMajorRecord>
        DuplicateAllOverrideRecordsAsNew(IMajorRecord rootRecord, List<ModKey> relevantContextKeys, 
            ModKey rootContextKey, ModKey npcSourceModKey, bool handleInjectedRecords,
            HashSet<string> fallBackModFolderNames, ref List<string> exceptionStrings, 
            HashSet<FormKey> searchedFormKeys, CancellationToken ct)
    {
        using var _ = ContextualPerformanceTracer.Trace("RecordHandler.DuplicateAllOverrideRecordsAsNew");
        HashSet<IMajorRecord> mergedInRecords = new();
        
        foreach (var modKey in relevantContextKeys)
        {
            TryAddPluginToCaches(modKey, fallBackModFolderNames);
        }

        Dictionary<FormKey, FormKey> remappedOverrideMap = new();
        
        foreach (var modKey in relevantContextKeys)
        {
            ct.ThrowIfCancellationRequested();
            
            if (!_modLinkCaches.TryGetValue(modKey, out var linkCache) || linkCache == null)
            {
                continue;
            }
            
            if (!_pluginProvider.TryGetPlugin(modKey, fallBackModFolderNames, out var plugin) || plugin == null)
            {
                continue;
            }
            
            foreach (var record in plugin.EnumerateMajorRecords())
            {
                ct.ThrowIfCancellationRequested();
                
                // Skip if already processed
                if (searchedFormKeys.Contains(record.FormKey))
                {
                    continue;
                }
                
                // Check if this is an override (FormKey's ModKey is NOT one of the appearance mod's plugins)
                if (!relevantContextKeys.Contains(record.FormKey.ModKey))
                {
                    searchedFormKeys.Add(record.FormKey);
    
                    // Skip if already mapped
                    if (_currentDuplicateInMappings.ContainsKey(record.FormKey))
                    {
                        remappedOverrideMap.TryAdd(record.FormKey, _currentDuplicateInMappings[record.FormKey]);
                        continue;
                    }
                    
                    try
                    {
                        var context = linkCache.ResolveContext(record.FormKey, record.Registration.GetterType);
                        if (context != null)
                        {
                            var duplicate = context.DuplicateIntoAsNewRecord(_environmentStateProvider.OutputMod);
                            duplicate.EditorID = (duplicate.EditorID ?? "NoEditorID") + "_" + modKey.FileName;
                            _currentDuplicateInMappings.Add(record.FormKey, duplicate.FormKey);
                            remappedOverrideMap.Add(record.FormKey, duplicate.FormKey);
                            mergedInRecords.Add(duplicate);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptionStrings.Add($"Failed to duplicate {record.FormKey}: {ex.Message}");
                    }
                }
            }
        }
        
        // Remap links in all merged records and root record
        foreach (var newRecord in mergedInRecords.And(rootRecord).ToArray())
        {
            newRecord.RemapLinks(remappedOverrideMap);
        }
        
        // Now merge in any new records that the overrides may reference
        var importSourceModKeys = relevantContextKeys
            .Distinct()
            .Where(k => k != npcSourceModKey)
            .ToHashSet();
        var newMergedSubRecords = DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod, 
            mergedInRecords, importSourceModKeys, rootContextKey, true, handleInjectedRecords, 
            fallBackModFolderNames, ref exceptionStrings);
        
        mergedInRecords.UnionWith(newMergedSubRecords);
        
        return mergedInRecords;
    }
    
    private void CollectOverriddenDependencyRecords(IFormLinkGetter formLinkGetter, List<ModKey> relevantContextKeys,
        HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> collectedRecords, int maxNestedIntervalDepth, int currentDepth, HashSet<FormKey> searchedFormKeys, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (formLinkGetter.IsNull)
        {
            return;
        }

        currentDepth++;
        if (currentDepth > maxNestedIntervalDepth) {return;}
        
        if (searchedFormKeys == null)
        {
            searchedFormKeys = new HashSet<FormKey>();
        }
        
        searchedFormKeys.Add(formLinkGetter.FormKey);

        IMajorRecordGetter? modRecord = null;
        
        // try to get the record version in the given mod plugin if possible
        foreach (var modKey in relevantContextKeys)
        {
            if (_modLinkCaches.ContainsKey(modKey) && _modLinkCaches[modKey].TryResolve(formLinkGetter, out modRecord) && modRecord != null)
            {
                var context = _modLinkCaches[modKey].ResolveContext(formLinkGetter);
                currentDepth = 0; // reset the interval search
                if (!relevantContextKeys.Contains(formLinkGetter.FormKey.ModKey)) // this is an override rather than a new record
                {
                    collectedRecords.Add(context);
                }
                break;
            }
        }
        
        // otherwise, traverse the parent record
        if (modRecord is null)
        {
            var parentmod = formLinkGetter.FormKey.ModKey;
            if (!_modLinkCaches.ContainsKey(parentmod))
            {
                var parentListing = _environmentStateProvider.LoadOrder.TryGetValue(parentmod);
                if (parentListing != null && parentListing.Mod != null)
                {
                    _modLinkCaches[parentListing.ModKey] = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(parentListing.Mod, new LinkCachePreferences());
                }
            }

            if (_modLinkCaches.ContainsKey(parentmod))
            {
                _modLinkCaches[parentmod].TryResolve(formLinkGetter, out modRecord);
            }
        }
        
        if (modRecord != null)
        {
            var sublinks = modRecord.EnumerateFormLinks();
            foreach (var subLink in sublinks.Where(x => !searchedFormKeys.Contains(x.FormKey)).ToArray())
            {
                CollectOverriddenDependencyRecords(subLink, relevantContextKeys, collectedRecords, maxNestedIntervalDepth, currentDepth, searchedFormKeys, ct);
            }
        }
    }

    #endregion

    #region Merge In Overrides of Existing Records

    public HashSet<IMajorRecord> // return is For Caller's Information only; duplication and remapping happens internally
        DuplicateInOverrideRecords(IMajorRecordGetter majorRecordGetter, IMajorRecord rootRecord, List<ModKey> relevantContextKeys, ModKey rootContextKey, ModKey npcSourceModKey, bool handleInjectedRecords, int maxNestedIntervalDepth, HashSet<string> fallBackModFolderNames, ref List<string> exceptionStrings, HashSet<FormKey> searchedFormKeys, CancellationToken ct)
    {
        using var _ = ContextualPerformanceTracer.Trace("RecordHandler.DuplicateInOverrideRecords");
        HashSet<IMajorRecord> mergedInRecords = new();
        var containedFormLinks = majorRecordGetter.EnumerateFormLinks().ToArray();
        foreach (var modKey in relevantContextKeys)
        {
            TryAddPluginToCaches(modKey, fallBackModFolderNames);
        }

        Dictionary<FormKey, FormKey> remappedOverrideMap = new();
        foreach (var link in containedFormLinks)
        {
            ct.ThrowIfCancellationRequested();
            TraverseAndDuplicateInOverrideRecords(link, relevantContextKeys, _environmentStateProvider.OutputMod, remappedOverrideMap, mergedInRecords, maxNestedIntervalDepth, 0, ref exceptionStrings, searchedFormKeys, ct);
        }
        
        foreach (var newRecord in mergedInRecords.And(rootRecord).ToArray())
        {
            newRecord.RemapLinks(remappedOverrideMap);
        }
        
        // Now go through all merged-in override records and also merge in any new records they may be pointing to
        var importSourceModKeys = relevantContextKeys
            .Distinct()
            .Where(k => k != npcSourceModKey) // don't copy from the mod that defines the NPC, since that is a base mod
            .ToHashSet();
        var newMergedSubRecords = DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod, mergedInRecords, importSourceModKeys, rootContextKey, true, handleInjectedRecords, fallBackModFolderNames, ref exceptionStrings);
        
        mergedInRecords.UnionWith(newMergedSubRecords);
        
        return mergedInRecords;
    }

    private bool TraverseAndDuplicateInOverrideRecords(IFormLinkGetter formLinkGetter, List<ModKey> relevantContextKeys,
        ISkyrimMod outputMod,
        Dictionary<FormKey, FormKey> remappedSubLinks, HashSet<IMajorRecord> mergedInRecords,
        int maxNestedIntervalDepth, int currentDepth, ref List<string> exceptionStrings, HashSet<FormKey> searchedFormKeys, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (formLinkGetter.IsNull)
        {
            return false;
        }

        currentDepth++;
        if (currentDepth > maxNestedIntervalDepth) {return false;}
        
        searchedFormKeys.Add(formLinkGetter.FormKey);

        bool parentRecordShouldBeMergedIn = false;
        bool currentRecordHasBeenMergedIn = false;
        IMajorRecordGetter? traversedModRecord = null;
        IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>? modContext = null;
        
        // try to get the record version in the given mod plugin if possible
        foreach (var modKey in relevantContextKeys)
        {
            if (_modLinkCaches.ContainsKey(modKey) && 
                _modLinkCaches[modKey].TryResolve(formLinkGetter, out traversedModRecord) && 
                traversedModRecord != null)
            {
                modContext = _modLinkCaches[modKey].ResolveContext(formLinkGetter);
                currentDepth = 0; // reset the interval search
                if (!relevantContextKeys.Contains(formLinkGetter.FormKey.ModKey)) // this is an override rather than a new record
                {
                    if (_currentDuplicateInMappings.ContainsKey(formLinkGetter.FormKey))
                    {
                        // This record has already been merged in from a previous function call on a previously processed NPC
                        // add it to remappedSubLinks so that the caller knows to remap it in the current NPC
                        // no need to add it to mergedInRecords because its AssetLinks have already been processed during the previous iteration
                        remappedSubLinks.TryAdd(formLinkGetter.FormKey, _currentDuplicateInMappings[formLinkGetter.FormKey]);
                        return true;
                    }
                    
                    var duplicate = modContext.DuplicateIntoAsNewRecord(outputMod);
                    duplicate.EditorID = (duplicate.EditorID ?? "NoEditorID") + "_" + modKey.FileName;
                    traversedModRecord = duplicate;
                    _currentDuplicateInMappings.Add(formLinkGetter.FormKey, duplicate.FormKey);
                    remappedSubLinks.Add(formLinkGetter.FormKey, duplicate.FormKey);
                    mergedInRecords.Add(duplicate);
                    parentRecordShouldBeMergedIn = true;
                    currentRecordHasBeenMergedIn = true;
                }
                break;
            }
        }
        
        // otherwise, traverse the parent record
        if (traversedModRecord is null)
        {
            var parentmod = formLinkGetter.FormKey.ModKey;
            if (!_modLinkCaches.ContainsKey(parentmod))
            {
                var parentListing = _environmentStateProvider.LoadOrder.TryGetValue(parentmod);
                if (parentListing != null && parentListing.Mod != null)
                {
                    _modLinkCaches[parentListing.ModKey] = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(parentListing.Mod, new LinkCachePreferences());
                }
            }

            if (_modLinkCaches.ContainsKey(parentmod))
            {
                _modLinkCaches[parentmod].TryResolve(formLinkGetter, out traversedModRecord);
            }
        }
        
        if (traversedModRecord != null)
        {
            var sublinks = traversedModRecord.EnumerateFormLinks().Distinct();
            foreach (var subLink in sublinks.Where(x => !searchedFormKeys.Contains(x.FormKey)).ToArray())
            {
                // don't repeat records that have already been processed
                bool hasCachedSubLink = false;
                if (_currentDuplicateInMappings.ContainsKey(subLink.FormKey))
                {
                    hasCachedSubLink = true;
                    remappedSubLinks.TryAdd(subLink.FormKey, _currentDuplicateInMappings[subLink.FormKey]);
                    parentRecordShouldBeMergedIn = true;
                    break;
                }

                if (hasCachedSubLink)
                {
                    continue;
                }
                
                bool subRecordsAreOverrides = TraverseAndDuplicateInOverrideRecords(subLink, relevantContextKeys, outputMod, remappedSubLinks, mergedInRecords, maxNestedIntervalDepth, currentDepth, ref exceptionStrings, searchedFormKeys, ct);
                if (subRecordsAreOverrides)
                {
                    parentRecordShouldBeMergedIn = true; // merge in this record (even if it's not itself contained in the source mod) because this record's subrecords have been merged in.
                }
            }
            
            if (parentRecordShouldBeMergedIn && 
                !currentRecordHasBeenMergedIn && 
                !remappedSubLinks.ContainsKey(formLinkGetter.FormKey))
            {
                if (Auxilliary.TryDuplicateGenericRecordAsNew(traversedModRecord, outputMod, out var duplicate, out string exceptionString))
                {
                    duplicate.EditorID = (duplicate.EditorID ?? "NoEditorID") + "_" + formLinkGetter.FormKey.ModKey;
                    remappedSubLinks.Add(formLinkGetter.FormKey, duplicate.FormKey);
                    mergedInRecords.Add(duplicate);
                }
                else
                {
                    exceptionStrings.Add(Auxilliary.GetLogString(traversedModRecord, _settings.LocalizationLanguage) + ": " + exceptionString);
                }
            }
        }

        return parentRecordShouldBeMergedIn;
    }
    #endregion
    
    #region Misc Functions

    public bool TryGetRecordFromMod(FormKey formKey, Type type, ModKey modKey, HashSet<string> fallBackModFolderNames,  RecordLookupFallBack fallbackMode,
        out dynamic? record)
    {
        using var _ = ContextualPerformanceTracer.Trace("RecordHandler.TryGetRecordFromMod");
        record = null;
        if (_pluginProvider.TryGetPlugin(modKey, fallBackModFolderNames, out var plugin) && plugin != null)
        {
            var group = plugin.TryGetTopLevelGroup(type);
            if (group != null && group.ContainsKey(formKey))
            {
                record = group[formKey];
                return true;
            }
        }
        return false;
    }
    
    public bool TryGetRecordGetterFromMod(IFormLinkGetter formLink, ModKey modKey, HashSet<string> fallBackModFolderNames, RecordLookupFallBack fallbackMode, out IMajorRecordGetter? record)
    {
        if (TryAddPluginToCaches(modKey, fallBackModFolderNames) && _modLinkCaches[modKey].TryResolve(formLink, out var modRecord) && modRecord is not null)
        {
            record = modRecord;
            return true;
        }
        
        // fallbacks
        switch (fallbackMode)
        {
            case RecordLookupFallBack.Origin:
                if (TryAddPluginToCaches(formLink.FormKey.ModKey, fallBackModFolderNames) && _modLinkCaches[formLink.FormKey.ModKey].TryResolve(formLink, out modRecord) && modRecord is not null)
                {
                    record = modRecord;
                    return true;
                }
                break;
            
            case RecordLookupFallBack.Winner:
                if (_environmentStateProvider.LinkCache.TryResolve(formLink, out modRecord) && modRecord is not null)
                {
                    record = modRecord;
                    return true;
                }
                break;
            
            case RecordLookupFallBack.None:
                default:
                    break;
        }

        record = null;
        return false;
    }

    public bool TryGetRecordFromMods(IFormLinkGetter formLink, IEnumerable<ModKey> modKeys, HashSet<string> fallBackModFolderNames, RecordLookupFallBack fallbackMode,
        out IMajorRecordGetter? record, bool reverseOrder = true)
    {
        record = null;
        if (modKeys == null || formLink.IsNull)
        {
            return false;
        }
        
        var toSearch = modKeys.Reverse().ToArray();
        if (!reverseOrder)
        {
            toSearch = modKeys.ToArray();
        }

        foreach (var mk in toSearch)
        {
            if (TryGetRecordGetterFromMod(formLink, mk, fallBackModFolderNames, RecordLookupFallBack.None, out record) && record != null)
            {
                return true;
            }
        }
        
        // fallbacks
        switch (fallbackMode)
        {
            case RecordLookupFallBack.Origin:
                if (TryGetRecordGetterFromMod(formLink, formLink.FormKey.ModKey, fallBackModFolderNames, RecordLookupFallBack.None,  out record) && record != null)
                {
                    return true;
                }
                break;
            
            case RecordLookupFallBack.Winner:
                if (_environmentStateProvider.LinkCache.TryResolve(formLink, out record) && record is not null)
                {
                    return true;
                }
                break;
            
            case RecordLookupFallBack.None:
            default:
                break;
        }
        
        return false;
    }

    public enum RecordLookupFallBack
    {
        None,
        Origin,
        Winner
    }
    
    public string GetStatusReport()
    {
        if (!_modLinkCaches.Any())
        {
            return "No plugins link caches currently created.";
        }
        else
        {
            return "Link caches for plugins: " + Environment.NewLine + string.Join(Environment.NewLine, _modLinkCaches.Select(x => "\t" + x.Key.ToString()));
        }
    }
    #endregion
}