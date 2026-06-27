using System.IO;
using System.Linq;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Parses and semantically compares two SkyPatcher NPC .ini files. Each line is
/// <c>filterByNPCs=&lt;target&gt;:&lt;dir&gt;=&lt;val&gt;,&lt;dir&gt;=&lt;val&gt;,...</c>. Comparison is keyed by the
/// (stable, vanilla) target NPC. Directive values that point at the freshly-generated output plugin
/// (copyVisualStyle / skin -&gt; NPC.esp|&lt;allocated FormID&gt;) are compared by PRESENCE only, since the
/// FormID allocation legitimately differs run-to-run; all other directives (height, weight, race -&gt;
/// vanilla, removeFlags, ...) are compared by normalized value.
/// </summary>
internal static class SkyPatcherIniComparer
{
    public sealed class Result
    {
        public List<string> Diffs { get; } = new();
        public int TargetsCompared { get; set; }
        public bool IsMatch => Diffs.Count == 0;
        public string Describe() => $"SkyPatcher targets compared: {TargetsCompared}. Diffs={Diffs.Count}." +
            (Diffs.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, Diffs.Take(30).Select(d => "  " + d)));
    }

    public static string DefaultIniRelativePath =>
        Path.Combine("SKSE", "Plugins", "SkyPatcher", "npc", "NPC Plugin Chooser", "NPC.ini");

    /// <summary>
    /// Maps each target NPC -&gt; the output-plugin surrogate NPC it copies its visual style from
    /// (<c>copyVisualStyle=&lt;output&gt;|&lt;FormID&gt;</c>), so the surrogate's appearance can be compared
    /// record-to-record across runs even though the surrogate FormID itself is allocator-dependent.
    /// </summary>
    public static Dictionary<Mutagen.Bethesda.Plugins.FormKey, Mutagen.Bethesda.Plugins.FormKey>
        SurrogateByTarget(string iniPath)
    {
        var map = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, Mutagen.Bethesda.Plugins.FormKey>();
        foreach (var (target, dirs) in Parse(iniPath))
        {
            if (!dirs.TryGetValue("copyVisualStyle", out var surrogate)) continue;
            if (TryFormKey(target, out var targetFk) && TryFormKey(surrogate, out var surrogateFk))
                map[targetFk] = surrogateFk;
        }
        return map;
    }

    private static bool TryFormKey(string pluginBarHex, out Mutagen.Bethesda.Plugins.FormKey formKey)
    {
        formKey = default;
        var bar = pluginBarHex.IndexOf('|');
        if (bar <= 0) return false;
        var plugin = pluginBarHex.Substring(0, bar).Trim();
        var hex = pluginBarHex.Substring(bar + 1).Trim();
        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var id)) return false;
        try { formKey = Mutagen.Bethesda.Plugins.FormKey.Factory($"{id:X6}:{plugin}"); return true; }
        catch { return false; }
    }

    public static Result Compare(string referenceIniPath, string freshIniPath, string outputPluginFileName)
    {
        var reference = Parse(referenceIniPath);
        var fresh = Parse(freshIniPath);
        var result = new Result();

        foreach (var target in reference.Keys.Union(fresh.Keys))
        {
            result.TargetsCompared++;
            bool inRef = reference.TryGetValue(target, out var refDirs);
            bool inFresh = fresh.TryGetValue(target, out var freshDirs);
            if (!inRef) { result.Diffs.Add($"{target}: present in fresh, absent in reference"); continue; }
            if (!inFresh) { result.Diffs.Add($"{target}: present in reference, absent in fresh"); continue; }

            foreach (var key in refDirs!.Keys.Union(freshDirs!.Keys))
            {
                bool hasRef = refDirs.TryGetValue(key, out var rv);
                bool hasFresh = freshDirs.TryGetValue(key, out var fv);
                if (!hasRef || !hasFresh)
                {
                    result.Diffs.Add($"{target}: directive '{key}' present ref={hasRef} fresh={hasFresh}");
                    continue;
                }
                bool refPointsToOutput = PointsToOutput(rv!, outputPluginFileName);
                bool freshPointsToOutput = PointsToOutput(fv!, outputPluginFileName);
                if (refPointsToOutput || freshPointsToOutput)
                {
                    // Pointer into the output plugin: compare presence + that BOTH point to the output, not the FormID.
                    if (refPointsToOutput != freshPointsToOutput)
                        result.Diffs.Add($"{target}.{key}: output-pointer mismatch ref='{rv}' fresh='{fv}'");
                }
                else if (!string.Equals(Normalize(rv!), Normalize(fv!), StringComparison.OrdinalIgnoreCase))
                {
                    result.Diffs.Add($"{target}.{key}: ref='{rv}' fresh='{fv}'");
                }
            }
        }
        return result;
    }

    private static bool PointsToOutput(string value, string outputPluginFileName)
    {
        var bar = value.IndexOf('|');
        if (bar <= 0) return false;
        var plugin = value.Substring(0, bar).Trim();
        return string.Equals(plugin, outputPluginFileName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(plugin, Path.GetFileNameWithoutExtension(outputPluginFileName), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalizes a directive value, including "Plugin|HEX" FormID references (lower-case plugin,
    /// FormID parsed to an int so 1347A == 01347A).</summary>
    private static string Normalize(string value)
    {
        var bar = value.IndexOf('|');
        if (bar > 0 && int.TryParse(value.Substring(bar + 1).Trim(),
                System.Globalization.NumberStyles.HexNumber, null, out var id))
        {
            return value.Substring(0, bar).Trim().ToLowerInvariant() + "|" + id.ToString("X");
        }
        return value.Trim();
    }

    /// <summary>target -> (directive key -> value).</summary>
    private static Dictionary<string, Dictionary<string, string>> Parse(string path)
    {
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return map;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.StartsWith("filterByNPCs=")) continue;

            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var target = Normalize(line.Substring("filterByNPCs=".Length, colon - "filterByNPCs=".Length));
            var directivePart = line.Substring(colon + 1);

            var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in directivePart.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = token.IndexOf('=');
                if (eq <= 0) continue;
                dirs[token.Substring(0, eq).Trim()] = token.Substring(eq + 1).Trim();
            }
            map[target] = dirs;
        }
        return map;
    }
}
