using System.Globalization;
using System.IO;
using Mutagen.Bethesda.Plugins;
using Tomlyn;
using Tomlyn.Model;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// One side of a Recast entry: a FormID selector (<c>"0x&lt;hex&gt;~Plugin.esp"</c> or
/// <c>{ form_id = ... }</c>) or an EditorID selector (any other string, or <c>{ editor_id = ... }</c>).
/// Exactly one of the two is set.
/// </summary>
public readonly record struct RecastSelector(FormKey? FormKey, string? EditorId)
{
    /// <summary>
    /// The key Recast resolves conflicts by. A FormID selector and an EditorID selector naming the
    /// same NPC have DIFFERENT identities, so Recast does not treat them as a conflict at all.
    /// </summary>
    public string Identity => FormKey is { } fk
        ? $"formid:{fk.ID:X6}~{fk.ModKey.FileName.String.ToLowerInvariant()}"
        : $"editorid:{EditorId!.ToLowerInvariant()}";

    public override string ToString() => FormKey is { } fk ? RecastInterface.FormatSelector(fk) : EditorId!;
}

/// <summary>A face replacement: <paramref name="Target"/> takes <paramref name="Source"/>'s face.</summary>
/// <param name="Section">Which syntax declared it: <c>npcs</c>, <c>recasts</c> or <c>replacements</c>.</param>
public sealed record RecastFaceEntry(RecastSelector Target, RecastSelector Source, string Section);

/// <summary>What one Recast patch file declares, as far as NPC2 cares: its manifest and its faces.</summary>
public sealed class RecastPatchFile
{
    public required string Path { get; init; }

    /// <summary>False when Recast itself would skip the whole file (TOML error, bad manifest).</summary>
    public bool IsLoaded { get; init; }

    public string? ManifestName { get; init; }
    public int Priority { get; init; }
    public IReadOnlyList<RecastFaceEntry> Faces { get; init; } = Array.Empty<RecastFaceEntry>();

    /// <summary>Why the file or individual entries were skipped, in Recast's own terms.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Reads Recast patch TOML the way Recast 1.6.3 does (<c>src/Config/TomlLoader.cpp</c>), for the
/// output validator and for importing replacer packs. Only faces are extracted; <c>body</c>,
/// <c>sex</c>, <c>voice</c>, <c>name</c> and <c>overlays</c> are not NPC2's concern yet.
///
/// <para>Recast's rules mirrored here: a TOML syntax error, a missing <c>[manifest]</c> or an
/// <c>api_version</c> other than 1 skips the whole file; a bad entry skips only that entry. The
/// three face syntaxes may coexist in one file.</para>
/// </summary>
public static class RecastPatchReader
{
    public const int SupportedApiVersion = 1;

    /// <summary><c>&lt;root&gt;\SKSE\Plugins\Recast\Patches</c>, where Recast loads every <c>*.toml</c>.</summary>
    public static string GetPatchesDir(string dataOrModRoot) =>
        Path.Combine(dataOrModRoot, "SKSE", "Plugins", "Recast", "Patches");

    /// <summary>
    /// Every patch file under <paramref name="dataOrModRoot"/>, recursively, in Recast's load order
    /// (ordinal path sort). The extension test is case-insensitive, as Recast's is.
    /// </summary>
    public static List<string> EnumeratePatchFiles(string dataOrModRoot)
    {
        var dir = GetPatchesDir(dataOrModRoot);
        if (!Directory.Exists(dir)) return new List<string>();

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => string.Equals(Path.GetExtension(f), ".toml", StringComparison.OrdinalIgnoreCase))
            .ToList();
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    public static RecastPatchFile ReadFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            return new RecastPatchFile { Path = path, IsLoaded = false, Errors = new[] { $"{path}: could not be read: {e.Message}" } };
        }

        return Parse(text, path);
    }

    public static RecastPatchFile Parse(string text, string path)
    {
        var errors = new List<string>();

        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(text)!;
        }
        catch (TomlException e)
        {
            errors.Add($"{path}: TOML parse error: {e.Message}");
            return new RecastPatchFile { Path = path, IsLoaded = false, Errors = errors };
        }

        if (!root.TryGetValue("manifest", out var manifestNode) || manifestNode is not TomlTable manifest)
        {
            errors.Add($"{path}: missing or invalid [manifest] table");
            return new RecastPatchFile { Path = path, IsLoaded = false, Errors = errors };
        }

        long apiVersion = manifest.TryGetValue("api_version", out var apiNode) && apiNode is long a ? a : 0;
        if (apiVersion != SupportedApiVersion)
        {
            errors.Add(apiVersion == 0
                ? $"{path}: [manifest].api_version is required (expected {SupportedApiVersion})"
                : $"{path}: api_version {apiVersion} not supported (expected {SupportedApiVersion})");
            return new RecastPatchFile { Path = path, IsLoaded = false, Errors = errors };
        }

        int priority = 0;
        if (manifest.TryGetValue("priority", out var priorityNode) && priorityNode is long p)
        {
            priority = (int)Math.Clamp(p, int.MinValue, int.MaxValue);
        }

        string? name = manifest.TryGetValue("name", out var nameNode) ? nameNode as string : null;

        var faces = new List<RecastFaceEntry>();
        ReadRecasts(root, path, faces, errors);
        ReadReplacements(root, path, faces, errors);
        ReadNpcs(root, path, faces, errors);

        return new RecastPatchFile
        {
            Path = path,
            IsLoaded = true,
            ManifestName = name,
            Priority = priority,
            Faces = faces,
            Errors = errors,
        };
    }

    /// <summary>
    /// Whether <paramref name="challenger"/> beats <paramref name="incumbent"/> for a target both
    /// name with the same selector identity. Recast ranks by (priority, file NAME, line), highest
    /// wins, comparing the name byte-wise; entries in one file are not compared here.
    /// </summary>
    public static bool Outranks(RecastPatchFile challenger, RecastPatchFile incumbent)
    {
        if (challenger.Priority != incumbent.Priority) return challenger.Priority > incumbent.Priority;
        return string.CompareOrdinal(challenger.FileName, incumbent.FileName) > 0;
    }

    /// <summary>
    /// The NPC FormKeys a selector names. A FormID selector needs no lookup; an EditorID selector
    /// resolves through <paramref name="editorIdMap"/> (the same maps the SkyPatcher import uses)
    /// and yields nothing when the EditorID is unknown.
    /// </summary>
    public static IEnumerable<FormKey> Resolve(RecastSelector selector,
        IReadOnlyDictionary<string, HashSet<FormKey>> editorIdMap)
    {
        if (selector.FormKey is { } fk) return new[] { fk };
        return editorIdMap.TryGetValue(selector.EditorId!, out var keys) ? keys : Enumerable.Empty<FormKey>();
    }

    // ── Sections ──────────────────────────────────────────────────────────────

    private static void ReadNpcs(TomlTable root, string path, List<RecastFaceEntry> faces, List<string> errors)
    {
        if (!root.TryGetValue("npcs", out var node)) return;
        if (node is not TomlTableArray blocks)
        {
            errors.Add($"{path}: [[npcs]] must be an array of tables");
            return;
        }

        foreach (var block in blocks)
        {
            if (!TryParseSelector(Get(block, "target"), "target ([[npcs]])", out var target, out var error))
            {
                errors.Add($"{path}: {error}");
                continue;
            }

            bool hasFace = block.ContainsKey("face");
            bool hasSource = block.ContainsKey("source");
            if (hasFace && hasSource)
            {
                errors.Add($"{path}: [[npcs]] uses both \"face\" and \"source\"; choose one");
                continue;
            }
            if (!hasFace && !hasSource) continue; // a body/sex/voice/name/overlays-only block

            if (!TryParseSelector(Get(block, hasFace ? "face" : "source"), "face ([[npcs]])", out var source, out error))
            {
                errors.Add($"{path}: {error}");
                continue;
            }

            faces.Add(new RecastFaceEntry(target, source, "npcs"));
        }
    }

    private static void ReadRecasts(TomlTable root, string path, List<RecastFaceEntry> faces, List<string> errors)
    {
        if (!root.TryGetValue("recasts", out var node)) return;
        if (node is not TomlTableArray entries)
        {
            errors.Add($"{path}: [[recasts]] must be an array of tables");
            return;
        }

        foreach (var entry in entries)
        {
            if (!TryParseSelector(Get(entry, "target"), "target", out var target, out var error) ||
                !TryParseSelector(Get(entry, "source"), "source", out var source, out error))
            {
                errors.Add($"{path}: {error}");
                continue;
            }

            faces.Add(new RecastFaceEntry(target, source, "recasts"));
        }
    }

    private static void ReadReplacements(TomlTable root, string path, List<RecastFaceEntry> faces, List<string> errors)
    {
        if (!root.TryGetValue("replacements", out var node)) return;
        if (node is not TomlTable map)
        {
            errors.Add($"{path}: [replacements] must be a table (target = source pairs)");
            return;
        }

        foreach (var (key, value) in map)
        {
            if (!TryParseSelectorString(key, "target (replacements key)", out var target, out var error) ||
                !TryParseSelector(value, "source (replacements value)", out var source, out error))
            {
                errors.Add($"{path}: {error}");
                continue;
            }

            faces.Add(new RecastFaceEntry(target, source, "replacements"));
        }
    }

    private static object? Get(TomlTable table, string key) => table.TryGetValue(key, out var value) ? value : null;

    // ── Selectors ─────────────────────────────────────────────────────────────

    /// <summary>A selector node: a string, or an inline table with exactly one of
    /// <c>form_id</c> / <c>editor_id</c>.</summary>
    public static bool TryParseSelector(object? node, string which, out RecastSelector selector, out string? error)
    {
        selector = default;
        switch (node)
        {
            case null:
                error = $"{which} selector is missing";
                return false;
            case string s:
                return TryParseSelectorString(s, which, out selector, out error);
            case TomlTable table:
                bool hasFormId = table.TryGetValue("form_id", out var formIdNode);
                bool hasEditorId = table.TryGetValue("editor_id", out var editorIdNode);
                if (hasFormId == hasEditorId)
                {
                    error = hasFormId
                        ? $"{which} selector has both form_id and editor_id set; choose one"
                        : $"{which} selector has neither form_id nor editor_id set";
                    return false;
                }
                if (hasFormId)
                {
                    if (formIdNode is not string formIdText)
                    {
                        error = $"{which} selector's form_id is not a string";
                        return false;
                    }
                    if (!TryParseFormId(formIdText, out var fk))
                    {
                        error = $"form_id must be of the form \"0x<hex>~<plugin>\", got \"{formIdText}\"";
                        return false;
                    }
                    selector = new RecastSelector(fk, null);
                    error = null;
                    return true;
                }
                if (editorIdNode is not string editorId || editorId.Length == 0)
                {
                    error = $"{which} selector's editor_id is not a non-empty string";
                    return false;
                }
                selector = new RecastSelector(null, editorId);
                error = null;
                return true;
            default:
                error = $"{which} selector must be a string or an inline table";
                return false;
        }
    }

    /// <summary>
    /// A bare-string selector, smart-detected as Recast does: <c>0x&lt;hex&gt;~&lt;plugin&gt;</c> is a
    /// FormID; any other string containing <c>~</c> is an error; everything else is an EditorID.
    /// </summary>
    public static bool TryParseSelectorString(string raw, string which, out RecastSelector selector, out string? error)
    {
        selector = default;
        if (raw.Length == 0)
        {
            error = $"{which} selector is an empty string";
            return false;
        }

        if (LooksLikeFormId(raw))
        {
            if (!TryParseFormId(raw, out var fk))
            {
                error = $"{which} selector \"{raw}\" is not a valid FormID selector";
                return false;
            }
            selector = new RecastSelector(fk, null);
            error = null;
            return true;
        }

        if (raw.Contains('~'))
        {
            error = $"{which} selector \"{raw}\" contains '~' but is not a valid FormID selector";
            return false;
        }

        selector = new RecastSelector(null, raw);
        error = null;
        return true;
    }

    private static bool LooksLikeFormId(string raw)
    {
        if (raw.Length < 4 || raw[0] != '0' || (raw[1] != 'x' && raw[1] != 'X')) return false;
        int tilde = raw.IndexOf('~');
        if (tilde <= 2 || tilde >= raw.Length - 1) return false;
        for (int i = 2; i < tilde; i++)
        {
            if (!Uri.IsHexDigit(raw[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// <c>[0x]&lt;1-8 hex&gt;~&lt;plugin&gt;</c> to a FormKey. Like Recast, a full runtime FormID is cut
    /// down to its local part: 12 bits under an <c>FE</c> light-plugin prefix, else 24 bits.
    /// </summary>
    private static bool TryParseFormId(string raw, out FormKey formKey)
    {
        formKey = FormKey.Null;
        int tilde = raw.IndexOf('~');
        if (tilde <= 0 || tilde == raw.Length - 1) return false;

        var hex = raw.AsSpan(0, tilde);
        if (hex.Length > 2 && hex[0] == '0' && (hex[1] == 'x' || hex[1] == 'X')) hex = hex[2..];
        if (hex.Length == 0 || hex.Length > 8) return false;
        if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint id)) return false;

        id = (id & 0xFF000000u) == 0xFE000000u ? id & 0x00000FFFu : id & 0x00FFFFFFu;

        if (!ModKey.TryFromFileName(raw[(tilde + 1)..], out var modKey)) return false;
        formKey = new FormKey(modKey, id);
        return true;
    }
}
