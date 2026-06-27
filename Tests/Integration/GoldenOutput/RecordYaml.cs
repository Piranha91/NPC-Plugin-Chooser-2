using System.IO;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Serialization.Streams;
using Mutagen.Bethesda.Serialization.Yaml;
using Mutagen.Bethesda.Skyrim;
using YamlDotNet.RepresentationModel;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Serializes a single Skyrim record to its full element tree (via Mutagen's YAML serialization) and
/// flattens it to a path-&gt;value dictionary for element-by-element comparison. FormKey-valued leaves are
/// normalized to their resolved EditorID, so a record that the patcher merged in under a freshly-allocated
/// FormKey still compares equal to its source (only the FormKey changed, not the identity it points at).
/// The serializing mod's header fields (ModKey/GameRelease/ModHeader) are dropped as noise.
/// </summary>
internal static class RecordYaml
{
    private static readonly Regex FormKeyPattern = new(@"^[0-9A-Fa-f]{6}:.+\.es[pml]$", RegexOptions.Compiled);

    /// <summary>Flattens a record (resolved as a context) into element-path -&gt; normalized-value pairs.</summary>
    public static async Task<Dictionary<string, string>> ToFlatAsync(
        IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter> ctx,
        Func<FormKey, string?> editorIdResolver)
    {
        var temp = new SkyrimMod(ModKey.FromName("oracle", ModType.Plugin), SkyrimRelease.SkyrimSE);
        ctx.GetOrAddAsOverride(temp);

        var path = Path.Combine(Path.GetTempPath(), "recyaml_" + Guid.NewGuid().ToString("N"));
        string text;
        try
        {
            await MutagenYamlConverter.Instance.Serialize(temp, path);
            text = File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); else if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch { /* best effort */ }
        }

        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        var flat = new Dictionary<string, string>();
        if (stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root)
        {
            Flatten(root, "", flat, editorIdResolver, topLevel: true);
        }
        return flat;
    }

    private static void Flatten(YamlNode node, string path, Dictionary<string, string> flat,
        Func<FormKey, string?> resolver, bool topLevel = false)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                flat[path] = Normalize(scalar.Value, resolver);
                break;
            case YamlMappingNode map:
                foreach (var (k, v) in map.Children)
                {
                    var key = ((YamlScalarNode)k).Value ?? "";
                    // Drop the serializing-mod header noise (identical across every record we serialize).
                    if (topLevel && key is "ModKey" or "GameRelease" or "ModHeader") continue;
                    // An unset nullable serializes as explicit "Null" after the patcher's deep-copy round-trip
                    // but is omitted when reading the source directly; both mean "no value", so drop it.
                    if (IsNull(v)) continue;
                    Flatten(v, path.Length == 0 ? key : path + "." + key, flat, resolver);
                }
                break;
            case YamlSequenceNode seq:
                // Sequences are sorted by a canonical key before indexing, so a list a mod merely REORDERED
                // (same content, different order - e.g. a Race's Attacks/Keywords) flattens identically and
                // is correctly treated as unchanged. Genuine content differences still surface at some index.
                var ordered = seq.Children
                    .OrderBy(c => Canonical(c, resolver), StringComparer.Ordinal)
                    .ToList();
                for (int i = 0; i < ordered.Count; i++)
                    Flatten(ordered[i], $"{path}[{i}]", flat, resolver);
                break;
        }
    }

    /// <summary>A deterministic, order-independent string for a node (used only to sort sequence elements).</summary>
    private static string Canonical(YamlNode node, Func<FormKey, string?> resolver)
    {
        switch (node)
        {
            case YamlScalarNode s:
                return Normalize(s.Value, resolver);
            case YamlMappingNode m:
                return "{" + string.Join(",", m.Children
                    .Where(kv => !IsNull(kv.Value))
                    .Select(kv => ((YamlScalarNode)kv.Key).Value + "=" + Canonical(kv.Value, resolver))
                    .OrderBy(x => x, StringComparer.Ordinal)) + "}";
            case YamlSequenceNode seq:
                return "[" + string.Join(",", seq.Children
                    .Select(c => Canonical(c, resolver))
                    .OrderBy(x => x, StringComparer.Ordinal)) + "]";
            default:
                return "";
        }
    }

    private static bool IsNull(YamlNode node) => node is YamlScalarNode { Value: "Null" };

    private static string Normalize(string? value, Func<FormKey, string?> resolver)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        // Fixed-size string fields keep trailing null padding in the source plugin that the patcher's
        // deep-copy round-trip trims (e.g. a Race movement-type code "WALK\0"); same value either way.
        value = value.TrimEnd('\0');
        if (FormKeyPattern.IsMatch(value) && FormKey.TryFactory(value, out var fk))
        {
            var eid = resolver(fk);
            return string.IsNullOrEmpty(eid) ? "fk:" + value : "eid:" + eid;
        }
        return value;
    }
}
