using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

public static class PatcherExtensions
{
    public static void DuplicateFromOnlyReferencedGetters<TMod, TModGetter>(
        this TMod modToDuplicateInto,
        IEnumerable<IMajorRecordGetter> recordsToDuplicate,
        ILinkCache<TMod, TModGetter> linkCache, 
        IEnumerable<ModKey> modKeysToDuplicateFrom,
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
            var containedLinks = rec.EnumerateFormLinks();
            foreach (var containedLink in containedLinks)
            {
                AddAllLinks(containedLink);
            }
        }

        // Duplicate in the records
        foreach (var identifiedRec in identifiedLinks)
        {
            if (!linkCache.TryResolveContext(identifiedRec.FormKey, identifiedRec.Type, out var rec))
            {
                throw new KeyNotFoundException($"Could not locate record to make self contained: {identifiedRec}");
            }

            if (!mapping.ContainsKey(rec.Record.FormKey))
            {
                var newEdid = (rec.Record.EditorID ?? "NoEditorID");
                var dup = rec.DuplicateIntoAsNewRecord(modToDuplicateInto, newEdid);
                dup.EditorID = newEdid;
                mapping[rec.Record.FormKey] = dup.FormKey;
            }
            
            modToDuplicateInto.Remove(identifiedRec.FormKey, identifiedRec.Type);
        }

        // Remap links
        modToDuplicateInto.RemapLinks(mapping);
    }
}