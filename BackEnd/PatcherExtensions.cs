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
        bool handleInjectedRecords,
        HashSet<string> fallBackModFolderNames,
        RecordLookupFallBack fallBackMode,
        ref Dictionary<FormKey, FormKey> mapping,
        ref HashSet<IFormLinkGetter> traversedFormLinks,
        ref List<string> exceptionStrings,
        params Type[] typesToInspect)
        where TModGetter : class, IModGetter
        where TMod : class, TModGetter, IMod, ISkyrimMod
    {
        if (modKeysToDuplicateFrom.Contains(modToDuplicateInto.ModKey))
        {
            throw new ArgumentException("Cannot pass the target mod's Key as the one to extract and self contain");
        }

        HashSet<IFormLinkGetter> identifiedLinks = new();
        var implicits = Implicits.Get(modToDuplicateInto.GameRelease);

        // Use an explicit stack to prevent recursive overflow
        var linksToProcess = new Stack<IFormLinkGetter>();

        // 1. Seed the stack with the initial records to traverse
        foreach (var rec in recordsToDuplicate)
        {
            if (onlySubRecords)
            {
                foreach (var containedLink in rec.EnumerateFormLinks())
                {
                    linksToProcess.Push(containedLink);
                }
            }
            else
            {
                linksToProcess.Push(rec.ToLink());
            }
        }

        // 2. Process the stack iteratively
        while (linksToProcess.Count > 0)
        {
            var link = linksToProcess.Pop();

            if (link.FormKey.IsNull || !traversedFormLinks.Add(link))
            {
                // Skip null links or links we've already processed
                continue;
            }

            if (implicits.Listings.Contains(link.FormKey.ModKey))
            {
                continue;
            }

            if ((modKeysToDuplicateFrom.Contains(link.FormKey.ModKey) || handleInjectedRecords) &&
                recordHandler.TryGetRecordFromMods(link, modKeysToDuplicateFrom, fallBackModFolderNames, fallBackMode, out var linkRec) && 
                linkRec != null)
            {
                identifiedLinks.Add(link);
                // 3. Add newly discovered links to the stack instead of making a recursive call
                foreach (var containedLink in linkRec.EnumerateFormLinks())
                {
                    if (modKeysToDuplicateFrom.Contains(containedLink.FormKey.ModKey) || handleInjectedRecords)
                    {
                        linksToProcess.Push(containedLink);
                    }
                }
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

            if (!recordHandler.TryGetRecordFromMods(identifiedLink, modKeysToDuplicateFrom, fallBackModFolderNames,
                    RecordLookupFallBack.None, out var identifiedRec)
                || identifiedRec == null)
            {
                throw new KeyNotFoundException($"Could not locate record to make self contained: {identifiedLink}");
            }

            var newEdid = (identifiedRec.EditorID ?? "NoEditorID");
            if (Auxilliary.TryDuplicateGenericRecordAsNew(identifiedRec, modToDuplicateInto, out dynamic? dup,
                    out string exceptionString) &&
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

    public static IEnumerable<IModListingGetter<ISkyrimModGetter>> TrimDependentPlugins(
        this IEnumerable<IModListingGetter<ISkyrimModGetter>> loadOrder)
    {
        List<ModKey> mastersToRemove = loadOrder.Where(x => x.Mod?.ModHeader.Description != null && 
                                                            x.Mod.ModHeader.Description.Equals(Patcher.PluginDescriptionSignature))
            .Select(x => x.ModKey).ToList();

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