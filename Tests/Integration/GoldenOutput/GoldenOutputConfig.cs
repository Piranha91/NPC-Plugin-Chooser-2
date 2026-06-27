using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Machine-specific configuration for the patcher golden-output tests, deserialized from the gitignored
/// <c>Tests/TestData/EnvironmentMap.local.json</c> (a committed <c>EnvironmentMap.example.json</c> documents
/// the schema). It maps the abstract test fixture - "USSEP", "RS Children Overhaul", the reference outputs -
/// onto concrete on-disk locations for the machine that generated the reference set, so the tests
/// <b>skip gracefully</b> on any machine where the file (or a referenced path) is absent.
/// </summary>
internal sealed class GoldenOutputConfig
{
    /// <summary>Root folder holding the 12 reference output sub-folders ("NPC 01 - ...", etc.). Gitignored (assets).</summary>
    public string ReferenceOutputRoot { get; set; } = string.Empty;

    /// <summary>The MO2 profile load-order file (full order, incl. inactive plugins). Optional; used to order the env.</summary>
    public string? LoadOrderFile { get; set; }

    /// <summary>The MO2 profile plugins file (active plugins marked with '*'). Optional; used to pick the active set.</summary>
    public string? PluginsFile { get; set; }

    /// <summary>
    /// The active, non-vanilla plugins that must be present in the patch-time environment but live in mod-manager
    /// folders rather than the game Data folder (USSEP, AI Overhaul + addons). Listed in load order. The prior
    /// <c>NPC.esp</c> output is deliberately NOT listed here - it must be trimmed out of the environment.
    /// </summary>
    public List<EnvironmentPluginEntry> EnvironmentPlugins { get; set; } = new();

    /// <summary>The appearance source mods the patcher reads from disk (by folder), keyed by ModSetting DisplayName.</summary>
    public Dictionary<string, AppearanceModEntry> AppearanceMods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal sealed class EnvironmentPluginEntry
    {
        public string Plugin { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
    }

    internal sealed class AppearanceModEntry
    {
        public List<string> Folders { get; set; } = new();
        public List<string> Plugins { get; set; } = new();
        public bool IsFaceGenOnly { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Tries to load and validate the local map. Returns false with a human-readable <paramref name="skipReason"/>
    /// when the map file is absent, malformed, or references paths that do not exist on this machine.
    /// </summary>
    public static bool TryLoad(out GoldenOutputConfig? config, out string skipReason)
    {
        config = null;
        skipReason = string.Empty;

        var mapPath = GoldenPaths.LocalMapPath;
        if (mapPath == null)
        {
            skipReason = "Could not locate the Tests/TestData directory.";
            return false;
        }
        if (!File.Exists(mapPath))
        {
            skipReason = $"Local environment map not found at '{mapPath}'. " +
                         "Copy EnvironmentMap.example.json to EnvironmentMap.local.json and fill in your paths to run this test.";
            return false;
        }

        GoldenOutputConfig parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GoldenOutputConfig>(File.ReadAllText(mapPath), JsonOptions)
                     ?? throw new JsonException("Deserialized to null.");
        }
        catch (Exception ex)
        {
            skipReason = $"Failed to parse '{mapPath}': {ex.Message}";
            return false;
        }

        if (!Directory.Exists(parsed.ReferenceOutputRoot))
        {
            skipReason = $"ReferenceOutputRoot '{parsed.ReferenceOutputRoot}' does not exist.";
            return false;
        }

        foreach (var env in parsed.EnvironmentPlugins)
        {
            var path = Path.Combine(env.Folder, env.Plugin);
            if (!File.Exists(path))
            {
                skipReason = $"Environment plugin '{env.Plugin}' not found at '{path}'.";
                return false;
            }
        }

        foreach (var (name, mod) in parsed.AppearanceMods)
        {
            foreach (var folder in mod.Folders)
            {
                if (!Directory.Exists(folder))
                {
                    skipReason = $"Appearance mod '{name}' folder '{folder}' does not exist.";
                    return false;
                }
            }
        }

        config = parsed;
        return true;
    }

    /// <summary>Absolute path to a named reference combo folder (e.g. "NPC 02 - CreateAndPatch - Include").</summary>
    public string ReferenceComboDir(string comboFolderName) => Path.Combine(ReferenceOutputRoot, comboFolderName);
}
