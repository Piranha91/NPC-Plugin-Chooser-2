using System.Linq;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Compares the appearance of two NPC records (a fresh patch output vs the committed reference) at the
/// semantic level the patcher is supposed to preserve. FormKey-valued links (head parts, worn armor, head
/// texture, hair color) are compared by their RESOLVED EditorID rather than FormKey, because records that
/// the patcher merges in (IncludeAsNew / dependency merge-in) are duplicated to freshly-allocated FormKeys
/// that legitimately differ run-to-run while keeping a stable EditorID. Floats are compared with a small
/// tolerance (the reference plugin round-trips through ESL compaction, introducing float32 epsilon).
/// </summary>
internal static class NpcAppearanceComparer
{
    private const double FloatTolerance = 1e-3;

    /// <summary>Builds a FormKey -> EditorID map for every record defined in a plugin (for link resolution).</summary>
    public static Dictionary<FormKey, string?> BuildEditorIdMap(ISkyrimModGetter mod)
    {
        var map = new Dictionary<FormKey, string?>();
        foreach (var rec in mod.EnumerateMajorRecords())
            map[rec.FormKey] = rec.EditorID;
        return map;
    }

    /// <summary>Returns the list of appearance mismatches between the two NPCs; empty when equivalent.</summary>
    public static List<string> Compare(INpcGetter reference, Dictionary<FormKey, string?> refEids,
        INpcGetter fresh, Dictionary<FormKey, string?> freshEids)
    {
        var diffs = new List<string>();

        // Head parts: unordered set of resolved keys.
        var refHp = reference.HeadParts.Select(h => ResolveKey(h.FormKey, refEids)).OrderBy(x => x).ToList();
        var freshHp = fresh.HeadParts.Select(h => ResolveKey(h.FormKey, freshEids)).OrderBy(x => x).ToList();
        if (!refHp.SequenceEqual(freshHp))
            diffs.Add($"HeadParts: ref=[{string.Join(",", refHp)}] fresh=[{string.Join(",", freshHp)}]");

        CompareLink("WornArmor", reference.WornArmor.FormKey, refEids, fresh.WornArmor.FormKey, freshEids, diffs);
        CompareLink("HeadTexture", reference.HeadTexture.FormKey, refEids, fresh.HeadTexture.FormKey, freshEids, diffs);
        CompareLink("HairColor", reference.HairColor.FormKey, refEids, fresh.HairColor.FormKey, freshEids, diffs);
        // Race is resolved by EditorID too: a custom race the mod provides (e.g. RS Children's child race)
        // is duplicated into the output under IncludeAsNew, so its FormKey legitimately differs run-to-run.
        CompareLink("Race", reference.Race.FormKey, refEids, fresh.Race.FormKey, freshEids, diffs);

        if (!FloatEq(reference.Height, fresh.Height)) diffs.Add($"Height: ref={reference.Height} fresh={fresh.Height}");
        if (!FloatEq(reference.Weight, fresh.Weight)) diffs.Add($"Weight: ref={reference.Weight} fresh={fresh.Weight}");

        bool refFemale = reference.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        bool freshFemale = fresh.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        if (refFemale != freshFemale) diffs.Add($"Female flag: ref={refFemale} fresh={freshFemale}");

        CompareNumericMembers("FaceMorph", reference.FaceMorph, fresh.FaceMorph, diffs);
        CompareNumericMembers("FaceParts", reference.FaceParts, fresh.FaceParts, diffs);
        CompareTintLayers(reference, fresh, diffs);

        return diffs;
    }

    private static void CompareLink(string name, FormKey refFk, Dictionary<FormKey, string?> refEids,
        FormKey freshFk, Dictionary<FormKey, string?> freshEids, List<string> diffs)
    {
        var a = ResolveKey(refFk, refEids);
        var b = ResolveKey(freshFk, freshEids);
        if (a != b) diffs.Add($"{name}: ref={a} fresh={b}");
    }

    /// <summary>A record duplicated into the output plugin resolves to "eid:&lt;EditorID&gt;"; an external
    /// (master) reference - identical FormKey in both runs - resolves to "ext:&lt;FormKey&gt;".</summary>
    private static string ResolveKey(FormKey fk, Dictionary<FormKey, string?> eids)
    {
        if (fk.IsNull) return "null";
        if (eids.TryGetValue(fk, out var eid) && !string.IsNullOrEmpty(eid)) return "eid:" + eid;
        return "ext:" + fk;
    }

    private static void CompareNumericMembers(string label, object? refObj, object? freshObj, List<string> diffs)
    {
        if (refObj == null && freshObj == null) return;
        if (refObj == null || freshObj == null)
        {
            diffs.Add($"{label}: ref-null={refObj == null} fresh-null={freshObj == null}");
            return;
        }
        foreach (var prop in refObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            object? rv, fv;
            try { rv = prop.GetValue(refObj); fv = prop.GetValue(freshObj); } catch { continue; }
            if (rv is float or double && fv is float or double)
            {
                if (!FloatEq(Convert.ToDouble(rv), Convert.ToDouble(fv)))
                    diffs.Add($"{label}.{prop.Name}: ref={rv} fresh={fv}");
            }
        }
    }

    private static void CompareTintLayers(INpcGetter reference, INpcGetter fresh, List<string> diffs)
    {
        string Sig(ITintLayerGetter t) =>
            $"{t.Index}:{t.Color}:{t.InterpolationValue?.ToString("0.##")}";
        var refLayers = reference.TintLayers.Select(Sig).OrderBy(x => x).ToList();
        var freshLayers = fresh.TintLayers.Select(Sig).OrderBy(x => x).ToList();
        if (!refLayers.SequenceEqual(freshLayers))
            diffs.Add($"TintLayers: ref({refLayers.Count})=[{string.Join(";", refLayers)}] fresh({freshLayers.Count})=[{string.Join(";", freshLayers)}]");
    }

    private static bool FloatEq(double a, double b) => Math.Abs(a - b) <= FloatTolerance;
}
