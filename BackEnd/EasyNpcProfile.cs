using System.IO;
using System.Globalization;
using Mutagen.Bethesda.Plugins;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NPC_Plugin_Chooser_2.BackEnd;

internal sealed record EasyNpcProfileEntry(FormKey NpcKey, ModKey DefaultPlugin, ModKey FacePlugin,
    string FaceMod = "")
{
    public string Serialize() =>
        $"{NpcKey.ModKey.FileName}#{NpcKey.IDString()}={DefaultPlugin.FileName}|{FacePlugin.FileName}|{FaceMod}";
}

/// <summary>A problem tied to a file line (<see cref="Line"/>) or to an NPC (<see cref="Npc"/>), kept
/// structured so the transfer dialog can show it as a table row.</summary>
internal sealed record EasyNpcIssue(string Message, int? Line = null, FormKey? Npc = null)
{
    public override string ToString() =>
        (Line is { } line ? $"Line {line}: " : Npc is { } npc ? $"NPC {npc}: " : "") + Message;
}

internal enum EasyNpcLineChangeKind { Updated, Added, Unchanged }

/// <summary>What <see cref="EasyNpcProfile.UpdateLines"/> did with one NPC's replacement entry.</summary>
internal sealed record EasyNpcLineChange(EasyNpcLineChangeKind Kind, EasyNpcProfileEntry? Before,
    EasyNpcProfileEntry After);

/// <summary>The text profile and JSON-lines autosave formats shared by EasyNPC 0.9.6 and NEXT 1.4.</summary>
internal static class EasyNpcProfile
{
    internal static bool IsCommentOrBlank(string line) =>
        string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith("//");

    internal static bool TryParseLine(string line, out EasyNpcProfileEntry entry, out string error)
    {
        entry = null!;
        error = string.Empty;
        var sides = line.Split('=', 2);
        var key = sides[0].Trim().Split('#');
        var fields = sides.Length == 2 ? sides[1].Split('|') : [];
        if (key.Length != 2 || fields.Length is < 2 or > 3 ||
            !TryParseNpcKey(key[0].Trim(), key.ElementAtOrDefault(1)?.Trim(), out var npc))
        {
            error = "Expected BasePlugin#FormID=DefaultPlugin|FacePlugin|FaceMod.";
            return false;
        }
        try
        {
            var defaultPlugin = ModKey.FromFileName(fields[0].Trim());
            var facePlugin = ModKey.FromFileName(fields[1].Trim());
            if (defaultPlugin.IsNull || facePlugin.IsNull) throw new FormatException("Plugin names cannot be empty.");
            var faceMod = fields.Length == 3 ? fields[2].Trim() : string.Empty;
            if (!TryGetFaceModName(faceMod, out _)) throw new FormatException("Invalid FaceMod locator.");
            entry = new(npc, defaultPlugin, facePlugin, faceMod);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseNpcKey(string master, string? id, out FormKey npc)
    {
        npc = FormKey.Null;
        // EasyNPC IDs are local, not runtime load-order IDs. Accept zero-padded eight-digit
        // exports too; Mutagen's FormKey parser expects the six-digit local form.
        return id is { Length: > 0 and <= 8 } &&
               uint.TryParse(id, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) &&
               value <= 0xFFFFFF && FormKey.TryFactory($"{value:X6}:{master}", out npc) && !npc.IsNull;
    }

    internal static bool TryGetFaceModName(string locator, out string name)
    {
        name = locator;
        if (!locator.StartsWith("::")) return true;
        var parts = locator[2..].Split("//", StringSplitOptions.None);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1])) return false;
        name = parts[1];
        return true;
    }

    internal static List<EasyNpcProfileEntry> Parse(IEnumerable<string> lines, bool autosave, List<EasyNpcIssue> errors)
    {
        if (autosave) return ParseAutosave(lines, errors);
        var entries = new Dictionary<FormKey, EasyNpcProfileEntry>();
        int lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            if (IsCommentOrBlank(line)) continue;
            if (!TryParseLine(line, out var entry, out var error))
                errors.Add(new(error, lineNumber));
            else if (!entries.TryAdd(entry.NpcKey, entry))
                errors.Add(new("Duplicate NPC; only the first entry is used.", lineNumber, entry.NpcKey));
        }
        return entries.Values.ToList();
    }

    private static List<EasyNpcProfileEntry> ParseAutosave(IEnumerable<string> lines, List<EasyNpcIssue> errors)
    {
        // File order is authoritative, not the timestamp. Null FaceMod explicitly clears an override.
        var states = new Dictionary<FormKey, Dictionary<string, string?>>();
        int lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var json = JObject.Parse(line);
                string? ReadString(string field) => json[field]?.Type == JTokenType.String ? (string?)json[field] : null;
                var master = ReadString("master");
                var id = ReadString("id");
                var field = ReadString("field");
                if (string.IsNullOrWhiteSpace(master) || string.IsNullOrWhiteSpace(id) ||
                    !TryParseNpcKey(master, id, out var npc) ||
                    field is not ("DefaultPlugin" or "FacePlugin" or "FaceMod") ||
                    json["newValue"] == null || json["newValue"]!.Type is not (JTokenType.String or JTokenType.Null))
                    throw new FormatException("Invalid profile event (master, id, field or newValue).");
                if (!states.TryGetValue(npc, out var state)) states[npc] = state = new();
                state[field] = ReadString("newValue");
            }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
            {
                errors.Add(new(ex.Message, lineNumber));
            }
        }
        var entries = new List<EasyNpcProfileEntry>();
        foreach (var (npc, state) in states)
        {
            // Never invent a plugin when only a partial history is available.
            var line = $"{npc.ModKey.FileName}#{npc.IDString()}={state.GetValueOrDefault("DefaultPlugin")}|" +
                       $"{state.GetValueOrDefault("FacePlugin")}|{state.GetValueOrDefault("FaceMod")}";
            if (TryParseLine(line, out var entry, out var error)) entries.Add(entry);
            else errors.Add(new($"Incomplete or invalid autosave selection. {error}", Npc: npc));
        }
        return entries;
    }

    internal static List<string> ReadLines(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    internal static List<string> UpdateLines(IReadOnlyList<string> original,
        IReadOnlyDictionary<FormKey, EasyNpcProfileEntry> replacements, bool addMissing,
        List<EasyNpcIssue> errors, out int updated, out int added, List<EasyNpcLineChange>? changes = null)
    {
        var result = original.ToList();
        var seen = new HashSet<FormKey>();
        // A duplicate would otherwise be imported in parallel by EasyNPC with unpredictable results.
        var duplicates = original.Where(line => !IsCommentOrBlank(line))
            .Select(line => TryParseLine(line, out var entry, out _) ? entry : null)
            .Where(entry => entry != null).GroupBy(entry => entry!.NpcKey)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        updated = added = 0;
        for (int i = 0; i < original.Count; i++)
        {
            if (IsCommentOrBlank(original[i])) continue;
            if (!TryParseLine(original[i], out var entry, out var error))
            {
                errors.Add(new($"Left unchanged: {error}", i + 1));
                continue;
            }
            seen.Add(entry.NpcKey);
            if (!replacements.TryGetValue(entry.NpcKey, out var replacement)) continue;
            if (duplicates.Contains(entry.NpcKey))
            {
                errors.Add(new("Duplicate NPC; left unchanged. Remove duplicates before updating.", i + 1, entry.NpcKey));
                continue;
            }
            // The existing behavior source is intentionally preserved; appearance includes BOTH fields.
            var after = replacement with { DefaultPlugin = entry.DefaultPlugin };
            // Keep EasyNPC's "::id//name" locator when it already names the same FaceGen mod.
            if (after.FaceMod.Length > 0 && TryGetFaceModName(entry.FaceMod, out var existingName) &&
                entry.FaceMod != after.FaceMod &&
                string.Equals(existingName, after.FaceMod, StringComparison.OrdinalIgnoreCase))
                after = after with { FaceMod = entry.FaceMod };
            var newLine = after.Serialize();
            if (newLine == original[i])
            {
                changes?.Add(new(EasyNpcLineChangeKind.Unchanged, entry, after));
                continue;
            }
            result[i] = newLine;
            updated++;
            changes?.Add(new(EasyNpcLineChangeKind.Updated, entry, after));
        }
        foreach (var (key, entry) in replacements)
        {
            if (seen.Contains(key)) continue;
            if (addMissing)
            {
                result.Add(entry.Serialize());
                added++;
                changes?.Add(new(EasyNpcLineChangeKind.Added, null, entry));
            }
            else errors.Add(new("Absent from profile; Add Missing NPCs is disabled.", Npc: key));
        }
        return result;
    }
}
