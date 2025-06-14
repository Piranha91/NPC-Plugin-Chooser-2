﻿using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class RecordHandler
{
    // ModKey: plugin whose record is merged-in
    // Key: FormKey from source plugin
    // Value: FormKey of merged-in record in output plugin
    private Dictionary<ModKey, Dictionary<FormKey, FormKey>> _contextMappings = new();
    
    // For converting plugins into linkcaches and avoiding having to resolve all contexts to get mod-specific records
    private Dictionary<ModKey, ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>> _modLinkCaches = new();

    private readonly EnvironmentStateProvider _environmentStateProvider;

    public RecordHandler(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
    }

    public void Reinitialize()
    {
        _contextMappings.Clear();
    }

    #region Merge In New Records
    
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
    
    
    
    #endregion

    #region Collect Overrides of Existing Records
    public HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>
        DeepGetOverriddenDependencyRecords(IMajorRecordGetter majorRecordGetter, List<ModKey> relevantContextKeys)
    {
        var containedFormLinks = majorRecordGetter.EnumerateFormLinks().ToArray();
        foreach (var modKey in relevantContextKeys)
        {
            var modListing = _environmentStateProvider.LoadOrder.TryGetValue(modKey);
            if (modListing != null && modListing.Mod != null)
            {
                _modLinkCaches.TryAdd(modKey, new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(modListing.Mod, new LinkCachePreferences()));
            }
        }
        HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> dependencyContexts = new();
        foreach (var link in containedFormLinks)
        {
            CollectOverriddenDependencyRecords(link, relevantContextKeys, dependencyContexts, 2, 0);
        }
        return dependencyContexts.Distinct().ToHashSet();;
    }
    
    private void CollectOverriddenDependencyRecords(IFormLinkGetter formLinkGetter, List<ModKey> relevantContextKeys,
        HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> collectedRecords, int maxNestedIntervalDepth, int currentDepth, HashSet<FormKey>? searchedFormKeys = null)
    {
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
                CollectOverriddenDependencyRecords(subLink, relevantContextKeys, collectedRecords, maxNestedIntervalDepth, currentDepth, searchedFormKeys);
            }
        }
    }

    #endregion

    #region Merge In Overrides of Existing Records

    public HashSet<IMajorRecord> // return is For Caller's Information only; duplication and remapping happens internally
        DuplicateInOverrideRecords(IMajorRecordGetter majorRecordGetter, List<ModKey> relevantContextKeys)
    {
        HashSet<IMajorRecord> mergedInRecords = new();
        var containedFormLinks = majorRecordGetter.EnumerateFormLinks().ToArray();
        foreach (var modKey in relevantContextKeys)
        {
            var modListing = _environmentStateProvider.LoadOrder.TryGetValue(modKey);
            if (modListing != null && modListing.Mod != null)
            {
                _modLinkCaches.TryAdd(modKey, new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(modListing.Mod, new LinkCachePreferences()));
            }

            if (!_contextMappings.ContainsKey(modKey))
            {
                _contextMappings.Add(modKey, new());
            }
        }

        Dictionary<FormKey, FormKey> remappedSublinks = new();
        foreach (var link in containedFormLinks)
        {
            TraverseAndDuplicateInOverrideRecords(link, relevantContextKeys, _environmentStateProvider.OutputMod, remappedSublinks, mergedInRecords,2, 0);
        }
        
        _environmentStateProvider.OutputMod.RemapLinks(remappedSublinks);
        
        return mergedInRecords;
    }
    
    private bool TraverseAndDuplicateInOverrideRecords(IFormLinkGetter formLinkGetter, List<ModKey> relevantContextKeys, ISkyrimMod outputMod,
        Dictionary<FormKey, FormKey> remappedSubLinks, HashSet<IMajorRecord> mergedInRecords, int maxNestedIntervalDepth, int currentDepth, HashSet<FormKey>? searchedFormKeys = null)
    {
        if (formLinkGetter.IsNull)
        {
            return false;
        }

        currentDepth++;
        if (currentDepth > maxNestedIntervalDepth) {return false;}
        
        if (searchedFormKeys == null)
        {
            searchedFormKeys = new HashSet<FormKey>();
        }
        
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
                    if (_contextMappings[modKey].ContainsKey(formLinkGetter.FormKey))
                    {
                        // This record has already been merged in from a previous function call on a previously processed NPC
                        // add it to remappedSubLinks so that the caller knows to remap it in the current NPC
                        // no need to add it to mergedInRecords because its AssetLinks have already been processed during the previous iteration
                        remappedSubLinks.TryAdd(formLinkGetter.FormKey, _contextMappings[modKey][formLinkGetter.FormKey]);
                        return true;
                    }
                    
                    var duplicate = modContext.DuplicateIntoAsNewRecord(outputMod);
                    duplicate.EditorID = (duplicate.EditorID ?? "NoEditorID") + "_" + modKey.FileName;
                    traversedModRecord = duplicate;
                    _contextMappings[modKey].Add(formLinkGetter.FormKey, duplicate.FormKey);
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
                foreach (var mk in relevantContextKeys)
                {
                    if (_contextMappings[mk].ContainsKey(subLink.FormKey))
                    {
                        hasCachedSubLink = true;
                        remappedSubLinks.TryAdd(subLink.FormKey, _contextMappings[mk][subLink.FormKey]);
                        parentRecordShouldBeMergedIn = true;
                        break;
                    }
                }

                if (hasCachedSubLink)
                {
                    continue;
                }
                
                bool subRecordsAreOverrides = TraverseAndDuplicateInOverrideRecords(subLink, relevantContextKeys, outputMod, remappedSubLinks, mergedInRecords, maxNestedIntervalDepth, currentDepth, searchedFormKeys);
                if (subRecordsAreOverrides)
                {
                    parentRecordShouldBeMergedIn = true; // merge in this record (even if it's not itself contained in the source mod) because this record's subrecords have been merged in.
                }
            }
            
            if (parentRecordShouldBeMergedIn && !currentRecordHasBeenMergedIn && !remappedSubLinks.ContainsKey(formLinkGetter.FormKey))
            {
                var duplicate = Auxilliary.DuplicateGenericRecordAsNew(traversedModRecord, outputMod);
                duplicate.EditorID = (duplicate.EditorID ?? "NoEditorID") + "_" + formLinkGetter.FormKey.ModKey;
                remappedSubLinks.Add(formLinkGetter.FormKey, duplicate.FormKey);
                mergedInRecords.Add(duplicate);
            }
        }

        return parentRecordShouldBeMergedIn;
    }
    #endregion
    
    #region Misc Functions
    
    public bool GetRecordFromMod(IFormLinkGetter formLink, ModKey modKey, out IMajorRecordGetter? record)
    {
        if (_modLinkCaches.ContainsKey(modKey) && _modLinkCaches[modKey].TryResolve(formLink, out var modRecord) && modRecord is not null)
        {
            record = modRecord;
            return true;
        }
        else if (_environmentStateProvider.LoadOrder.Keys.Contains(modKey))
        {
            var listing = _environmentStateProvider.LoadOrder.TryGetValue(modKey);
            if (listing != null && listing.Mod != null)
            {
                _modLinkCaches[listing.ModKey] = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(listing.Mod, new LinkCachePreferences());
                if (_modLinkCaches[listing.ModKey].TryResolve(formLink, out modRecord) && modRecord is not null)
                {
                    record = modRecord;
                    return true;
                }
            }
        }
        record = null;
        return false;
    }
    #endregion
}