using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Best-effort reverse map from a record's FormKeys to the field path(s) that hold them
/// (e.g. "Race", "HeadParts[2]", "VirtualMachineAdapter.Scripts[0].Properties[1].Object").
///
/// <para>Mutagen's <c>EnumerateFormLinks</c> flattens a record to bare links with no field names,
/// and a link's declared type is only as specific as the field that declares it — a Papyrus script
/// property is <c>IFormLinkGetter&lt;ISkyrimMajorRecordGetter&gt;</c>, which names no record type at
/// all. For those the field path is the only thing that says where a reference actually lives, so
/// this walks the record's property graph by reflection to recover it.</para>
///
/// <para><b>Diagnostics and failure paths only.</b> Reflection over a whole record graph is not
/// cheap and this runs per record, so it belongs only where something has already gone wrong. Every
/// access is guarded: a field that cannot be walked goes unnamed rather than masking the real
/// error.</para>
/// </summary>
public static class RecordFieldPathMapper
{
    /// <summary>How deep the walk goes. Six levels is what a Papyrus script property costs —
    /// VirtualMachineAdapter.Scripts[i].Properties[j].Object — which is the deepest thing worth
    /// naming on an NPC.</summary>
    private const int MaxDepth = 6;

    /// <summary>Guard against a pathological list turning a diagnostic into a hang.</summary>
    private const int MaxSequenceItems = 512;

    // Property names that lead back into Mutagen's static/registration plumbing rather than
    // into record data. Walking them yields nothing useful and can be expensive.
    private static readonly HashSet<string> SkippedProperties = new(StringComparer.Ordinal)
    {
        "Registration", "StaticRegistration", "CommonInstance", "CommonSetterInstance",
        "CommonSetterTranslationInstance", "ContainedFormLinks", "ContainedAssetLinks", "Mask",
    };

    /// <summary>
    /// The field path(s) holding each of <paramref name="wanted"/>. A key can appear more than once
    /// on a record, so each maps to a list.
    /// </summary>
    public static Dictionary<FormKey, List<string>> MapFieldNames(IMajorRecordGetter record,
        HashSet<FormKey> wanted)
    {
        var result = new Dictionary<FormKey, List<string>>();
        try
        {
            foreach (var (path, key) in EnumerateNamedFormLinks(record, string.Empty, 0,
                         new HashSet<object>(ReferenceEqualityComparer.Instance)))
            {
                if (!wanted.Contains(key)) continue;
                if (!result.TryGetValue(key, out var names))
                {
                    names = new List<string>();
                    result[key] = names;
                }
                if (!names.Contains(path)) names.Add(path);
            }
        }
        catch
        {
            // Leave whatever was collected; unnamed fields are the caller's to label.
        }
        return result;
    }

    /// <summary>
    /// The single field path holding <paramref name="key"/>, or null when the record does not hold
    /// it or the walk could not reach it. Several paths join with " / " — a record referencing the
    /// same target twice is unusual enough to be worth showing rather than picking one.
    /// </summary>
    public static string? FindFieldPath(IMajorRecordGetter? record, FormKey key)
    {
        if (record == null || key.IsNull) return null;

        var names = MapFieldNames(record, new HashSet<FormKey> { key });
        return names.TryGetValue(key, out var paths) && paths.Count > 0
            ? string.Join(" / ", paths.OrderBy(n => n, StringComparer.Ordinal))
            : null;
    }

    private static IEnumerable<(string Path, FormKey Key)> EnumerateNamedFormLinks(object? obj, string path,
        int depth, HashSet<object> visited)
    {
        if (obj == null || depth > MaxDepth) yield break;

        if (obj is IFormLinkGetter link)
        {
            if (!link.FormKey.IsNull) yield return (path.Length == 0 ? "(root)" : path, link.FormKey);
            yield break;
        }

        if (obj is string || obj.GetType().IsPrimitive || obj is FormKey) yield break;

        // A nested MajorRecord is referenced by link, never embedded; if one shows up, stop
        // rather than walking a second record's whole graph under this record's field path.
        if (depth > 0 && obj is IMajorRecordGetter) yield break;

        if (!visited.Add(obj)) yield break;

        if (obj is System.Collections.IEnumerable sequence)
        {
            int index = 0;
            foreach (var item in sequence)
            {
                foreach (var found in EnumerateNamedFormLinks(item, $"{path}[{index}]", depth + 1, visited))
                {
                    yield return found;
                }
                index++;
                if (index > MaxSequenceItems) break;
            }
            yield break;
        }

        var type = obj.GetType();
        if (type.Namespace == null || !type.Namespace.StartsWith("Mutagen.", StringComparison.Ordinal)) yield break;

        foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public |
                                                    System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead) continue;
            if (property.GetIndexParameters().Length > 0) continue;
            if (SkippedProperties.Contains(property.Name)) continue;

            object? value;
            try
            {
                value = property.GetValue(obj);
            }
            catch
            {
                continue; // a property that throws is not worth failing the diagnostic over
            }

            if (value == null) continue;

            string childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
            foreach (var found in EnumerateNamedFormLinks(value, childPath, depth + 1, visited))
            {
                yield return found;
            }
        }
    }
}
