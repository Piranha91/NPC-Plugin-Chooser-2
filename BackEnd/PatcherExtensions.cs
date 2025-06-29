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
        ref Dictionary<FormKey, FormKey> mapping,
        ref List<string> exceptionStrings,
        params Type[] typesToInspect)
        where TModGetter : class, IModGetter
        where TMod : class, TModGetter, IMod, ISkyrimMod
    {
        if (modKeysToDuplicateFrom.Contains(modToDuplicateInto.ModKey))
        {
            exceptionStrings.Add("Cannot pass the target mod's Key: " + modToDuplicateInto.ModKey.ToString() + " as the one to extract and self contain");
            return new();
        }
        
        // make sure not to import from the base game
        var implicits = Implicits.Get(modToDuplicateInto.GameRelease);
        var implicitModKeys = implicits.Listings.ToArray();
        var filteredSourceModKeys = modKeysToDuplicateFrom.Where(x => !implicitModKeys.Contains(x)).ToHashSet();

        // Compile list of things to duplicate
        HashSet<IFormLinkGetter> identifiedLinks = new();
        HashSet<FormKey> passedLinks = new();

        void AddAllLinksIterative(IFormLinkGetter root, HashSet<FormKey> seenFormKeys)
        {
            var pending = new Stack<IFormLinkGetter>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var link = pending.Pop();
                if (link.FormKey.IsNull || !seenFormKeys.Add(link.FormKey)) continue;
                if (implicits.RecordFormKeys.Contains(link.FormKey)) continue;

                // Mark for duplication if it lives in a donor plugin
                if (filteredSourceModKeys.Contains(link.FormKey.ModKey))
                    identifiedLinks.Add(link);

                // Try to resolve the record so we can walk its children
                if (!recordHandler.TryGetRecordFromMods(link,
                        filteredSourceModKeys, fallBackMode, out var linkRec) ||
                    linkRec is null) continue;

                foreach (var child in linkRec.EnumerateFormLinks())
                {
                    // Only walk links we might actually need
                    if (filteredSourceModKeys.Contains(child.FormKey.ModKey))
                        pending.Push(child);
                }
            }
        }
        
        foreach (var rec in recordsToDuplicate)
        {
            if (onlySubRecords)
            {
                foreach (var contained in rec.EnumerateFormLinks())
                    AddAllLinksIterative(contained, passedLinks);
            }
            else
            {
                AddAllLinksIterative(rec.ToLink(), passedLinks);
            }
        }

        List<MajorRecord> mergedInRecords = new();
        // Duplicate in the records
        foreach (var identifiedLink in identifiedLinks)
        {
            if (mapping.ContainsKey(identifiedLink.FormKey))
            {
                continue; // this form has already been remapped in a previous call of this function
            }
            
            if (!recordHandler.TryGetRecordFromMods(identifiedLink, filteredSourceModKeys,
                    RecordLookupFallBack.None, out var identifiedRec)
                || identifiedRec == null)
            {
                exceptionStrings.Add($"Could not locate record to make self contained: {identifiedLink}");
                continue;
            }

            var newEdid = (identifiedRec.EditorID ?? "NoEditorID");
            if (Auxilliary.TryDuplicateGenericRecordAsNew(identifiedRec, modToDuplicateInto, out dynamic? dup, out string exceptionString) &&
                dup != null)
            {
                dup.EditorID = newEdid;
                mapping[identifiedLink.FormKey] = dup.FormKey;
                mergedInRecords.Add(dup);
                modToDuplicateInto.Remove(identifiedLink.FormKey, identifiedLink.Type);
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