using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using static NPC_Plugin_Chooser_2.BackEnd.RecordHandler;

namespace NPC_Plugin_Chooser_2.BackEnd;

public static class PatcherExtensions
{
    public static List<MajorRecord> DuplicateFromOnlyReferencedGetters<TMod, TModGetter>(
        this TMod modToDuplicateInto,
        IEnumerable<IMajorRecordGetter> recordsToDuplicate,
        RecordHandler recordHandler,
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        bool onlySubRecords,
        RecordLookupFallBack fallBackMode,
        Dictionary<FormKey, FormKey> mapping, // Changed from ref to value
        ref List<string> exceptionStrings,
        Dictionary<(FormKey, ModKey), HashSet<IFormLinkGetter>> traversalCache,
        params Type[] typesToInspect)
        where TModGetter : class, IModGetter
        where TMod : class, TModGetter, IMod, ISkyrimMod
    {
        if (modKeysToDuplicateFrom.Contains(modToDuplicateInto.ModKey))
        {
            exceptionStrings.Add("Cannot pass the target mod's Key: " + modToDuplicateInto.ModKey.ToString() +
                                 " as the one to extract and self contain");
            return new();
        }

        var implicits = Implicits.Get(modToDuplicateInto.GameRelease);
        // Corrected and more efficient check using implicits.ModKeys
        var filteredSourceModKeys = modKeysToDuplicateFrom.Where(x => !implicits.Listings.Contains(x)).ToHashSet();

        // This will collect all unique links that need to be duplicated.
        var identifiedLinks = new HashSet<IFormLinkGetter>();

        // This is the recursive local function. It now correctly passes 'seenFormKeys' down.
        void AddAllLinksIterative(IFormLinkGetter root, HashSet<FormKey> seenFormKeys)
        {
            // ============================ THE FIX - PART 1 ============================
            // The check for already-seen keys must be the very first thing we do.
            if (root.FormKey.IsNull || !seenFormKeys.Add(root.FormKey))
            {
                return; // Exit if null or if this is a cycle.
            }
            // ========================================================================

            if (!recordHandler.TryGetRecordFromMods(root, filteredSourceModKeys, fallBackMode, out var rootRecord,
                    out var providerModKey) || rootRecord is null || providerModKey is null)
            {
                return;
            }

            var cacheKey = (root.FormKey, providerModKey.Value);
            if (traversalCache.TryGetValue(cacheKey, out var cachedLinks))
            {
                identifiedLinks.UnionWith(cachedLinks);
                return;
            }

            var linksFoundInThisTraversal = new HashSet<IFormLinkGetter>();

            if (!implicits.RecordFormKeys.Contains(rootRecord.FormKey) &&
                filteredSourceModKeys.Contains(rootRecord.FormKey.ModKey))
            {
                linksFoundInThisTraversal.Add(rootRecord.ToLinkGetter());
                identifiedLinks.Add(rootRecord.ToLinkGetter());
            }

            foreach (var child in rootRecord.EnumerateFormLinks())
            {
                // ============================ THE FIX - PART 2 ============================
                // Pass the EXISTING 'seenFormKeys' set down to the recursive call.
                // Do NOT create a 'new HashSet<FormKey>()' here.
                AddAllLinksIterative(child, seenFormKeys);
                // ========================================================================
            }

            traversalCache[cacheKey] = linksFoundInThisTraversal;
        }

        // This part remains correct. It creates a new 'seen' set for each top-level traversal.
        foreach (var rec in recordsToDuplicate)
        {
            if (onlySubRecords)
            {
                foreach (var containedLink in rec.EnumerateFormLinks())
                {
                    AddAllLinksIterative(containedLink, new HashSet<FormKey>());
                }
            }
            else
            {
                AddAllLinksIterative(rec.ToLinkGetter(), new HashSet<FormKey>());
            }
        }

        var mergedInRecords = new List<MajorRecord>();
        // Duplicate in the records
        foreach (var identifiedLink in identifiedLinks)
        {
            if (mapping.ContainsKey(identifiedLink.FormKey))
            {
                continue;
            }

            // We only need to resolve from the specific mod now, no fallback needed.
            if (!recordHandler.TryGetRecordFromMods(identifiedLink, new[] { identifiedLink.FormKey.ModKey },
                    RecordLookupFallBack.None, out var identifiedRec, out _) || identifiedRec == null)
            {
                exceptionStrings.Add($"Could not locate record to make self contained: {identifiedLink}");
                continue;
            }

            var newEdid = (identifiedRec.EditorID ?? "NoEditorID");
            if (Auxilliary.TryDuplicateGenericRecordAsNew(identifiedRec, modToDuplicateInto, out dynamic? dup,
                    out string exceptionString) && dup != null)
            {
                dup.EditorID = newEdid;
                mapping[identifiedLink.FormKey] = dup.FormKey;
                mergedInRecords.Add(dup);
            }
            else
            {
                exceptionStrings.Add(identifiedLink.FormKey.ToString() + ": " + exceptionString);
            }
        }

        // Remap links
        modToDuplicateInto.RemapLinks(mapping);

        return mergedInRecords;
    }

    // Original form depending on global link cache
    // Kept for reference
    public static void DuplicateFromOnlyReferencedGetters<TMod, TModGetter>(
        this TMod modToDuplicateInto,
        IEnumerable<IMajorRecordGetter> recordsToDuplicate,
        ILinkCache<TMod, TModGetter> linkCache, 
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        bool onlySubRecords,
        ref Dictionary<FormKey, FormKey> mapping,
        params Type[] typesToInspect)
        where TModGetter : class, IModGetter
        where TMod : class, TModGetter, IMod, ISkyrimMod
    {
        if (modKeysToDuplicateFrom.Contains(modToDuplicateInto.ModKey))
        {
            throw new ArgumentException("Cannot pass the target mod's Key as the one to extract and self contain");
        }

        // Compile list of things to duplicate
        HashSet<IFormLinkGetter> identifiedLinks = new();
        HashSet<FormKey> passedLinks = new();
        var implicits = Implicits.Get(modToDuplicateInto.GameRelease);

        void AddAllLinks(IFormLinkGetter link)
        {
            if (link.FormKey.IsNull) return;
            if (!passedLinks.Add(link.FormKey)) return;
            if (implicits.RecordFormKeys.Contains(link.FormKey)) return;

            if (!linkCache.TryResolve(link.FormKey, link.Type, out var linkRec))
            {
                return;
            }

            if (modKeysToDuplicateFrom.Contains(link.FormKey.ModKey))
            {
                identifiedLinks.Add(link);
            }

            var containedLinks = linkRec.EnumerateFormLinks();
            foreach (var containedLink in containedLinks)
            {
                if (!modKeysToDuplicateFrom.Contains(containedLink.FormKey.ModKey)) continue;
                AddAllLinks(containedLink);
            }
        }
        
        foreach (var rec in recordsToDuplicate)
        {
            if (onlySubRecords)
            {
                var containedLinks = rec.EnumerateFormLinks();
                foreach (var containedLink in containedLinks)
                {
                    AddAllLinks(containedLink);
                }
            }
            else
            {
                AddAllLinks(rec.ToLink());
            }
        }

        // Duplicate in the records
        foreach (var identifiedRec in identifiedLinks)
        {
            var context = linkCache.ResolveAllContexts(identifiedRec.FormKey, identifiedRec.Type)
                .FirstOrDefault(x => modKeysToDuplicateFrom.Contains(x.ModKey));
            
            if (context == null)
            {
                throw new KeyNotFoundException($"Could not locate record to make self contained: {identifiedRec}");
            }

            var newEdid = (context.Record.EditorID ?? "NoEditorID");
            var dup = context.DuplicateIntoAsNewRecord(modToDuplicateInto, newEdid);
            dup.EditorID = newEdid;
            mapping[context.Record.FormKey] = dup.FormKey;
            
            modToDuplicateInto.Remove(identifiedRec.FormKey, identifiedRec.Type);
        }

        // Remap links
        modToDuplicateInto.RemapLinks(mapping);
    }
    
    public static IEnumerable<IModListingGetter<ISkyrimModGetter>> TrimPluginAndDependents(this IEnumerable<IModListingGetter<ISkyrimModGetter>> loadOrder, ModKey modKey)
    {
        List<ModKey> mastersToRemove = new() { modKey };
        
        List<IModListingGetter<ISkyrimModGetter>> trimmedLoadOrder = new();
        foreach (var listing in loadOrder)
        {
            if (listing.ModKey.IsNull) continue;
            if (mastersToRemove.Contains(listing.ModKey)) continue;
            if (listing.Mod.ModHeader.MasterReferences.Select(x => x.Master).Intersect(mastersToRemove).Any())
            {
                mastersToRemove.Add(listing.ModKey);
                continue;
            }
            trimmedLoadOrder.Add(listing);
        }
        
        return trimmedLoadOrder;
    }
}