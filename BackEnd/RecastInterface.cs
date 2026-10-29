using System.Globalization;
using System.IO;
using System.Text;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Emits the Recast patch TOML for <see cref="OutputBackend.Recast"/>: one <c>[[npcs]]</c> block per
/// target, pointing <c>face</c> at the surrogate NPC this run wrote into the output plugin.
///
/// <para>A Recast <c>face</c> entry carries everything SkyPatcher mode spells out directive by
/// directive (head parts, hair colour, height, weight, tint, skin and the female flag), so a block
/// needs only <c>target</c> and <c>face</c>. Race, outfits and keywords are not Recast's to deliver;
/// in <see cref="RecastMode.Hybrid"/> they still go through <see cref="SkyPatcherInterface"/>.</para>
///
/// <para>Written by hand rather than through a TOML library: the shape is fixed and tiny, and the
/// only values are FormID selectors and the manifest name.</para>
/// </summary>
public class RecastInterface : OptionalUIModule
{
    /// <summary>The manifest <c>name</c>; Recast logs it beside every conflict it resolves.</summary>
    public const string ManifestName = "NPC Plugin Chooser 2";

    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Settings _settings;
    private readonly Dictionary<FormKey, FaceEntry> _entries = new();

    /// <param name="Comment">Provenance note, written as a whole <c>#</c> line above the block.</param>
    private sealed record FaceEntry(FormKey Target, FormKey Face, string? Comment);

    public RecastInterface(EnvironmentStateProvider environmentStateProvider, Settings settings)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
    }

    /// <param name="clearAllGeneratedTomls">
    /// Sweep EVERY toml in NPC2's Recast folder, not just the current output plugin's. Same reason
    /// as <see cref="SkyPatcherInterface.Reinitialize"/>: the run's directory clear spares SKSE\, so a
    /// previous run's "&lt;name&gt;_2.toml" would otherwise outlive a run that splits into fewer plugins.
    /// </param>
    public void Reinitialize(string outputRootDir, bool clearAllGeneratedTomls = false)
    {
        _entries.Clear();

        if (clearAllGeneratedTomls)
        {
            ClearAllGeneratedTomls(outputRootDir);
            return;
        }

        if (!ClearToml(_environmentStateProvider.OutputMod.ModKey, outputRootDir, out string exceptionStr))
        {
            AppendLog(exceptionStr);
        }
    }

    /// <summary>
    /// Queues <paramref name="target"/> to take its appearance from <paramref name="surrogate"/>.
    /// A second call for the same target replaces the first: Recast would apply only one of two
    /// same-file blocks anyway, so NPC2 decides which rather than leaving it to Recast.
    /// </summary>
    public void AddFace(FormKey target, FormKey surrogate, string? comment = null)
    {
        if (target.IsNull || surrogate.IsNull)
        {
            return;
        }

        _entries[target] = new FaceEntry(target, surrogate, string.IsNullOrWhiteSpace(comment) ? null : comment);
    }

    public bool HasEntries => _entries.Count > 0;

    /// <summary>
    /// The surrogates every queued block names. A surrogate is reached only through the TOML, never
    /// through a plugin link, so the post-run orphan sweep needs this to keep it.
    /// </summary>
    public IEnumerable<FormKey> EnumerateDirectiveFormKeys() => _entries.Values.Select(e => e.Face);

    /// <summary>
    /// Writes the queued blocks, or deletes this output plugin's TOML when nothing is queued so a
    /// run that no longer selects any NPC leaves no stale faces behind.
    /// </summary>
    /// <param name="formKeyRemap">
    /// Output-plugin FormKeys relocated by an auto-split, as for
    /// <see cref="SkyPatcherInterface.WriteIni"/>. Applied to both selectors; FormKeys outside the
    /// output plugin are absent from the map and pass through.
    /// </param>
    public bool WriteToml(string outputRootFolder, IReadOnlyDictionary<FormKey, FormKey>? formKeyRemap = null)
    {
        var outputPlugin = _environmentStateProvider.OutputMod.ModKey;
        (var outputDir, var outputPath) = GetOutputPath(outputPlugin, outputRootFolder);

        if (!HasEntries)
        {
            if (!ClearToml(outputPlugin, outputRootFolder, out string clearError))
            {
                AppendLog(clearError, true, true);
                return false;
            }
            return true;
        }

        try
        {
            Directory.CreateDirectory(outputDir);
            string text = Render(_entries.Values, _settings.RecastPatchPriority, formKeyRemap);
            File.WriteAllText(outputPath, text, new UTF8Encoding(false));
            AppendLog("Saved Recast patch to " + outputPath, false, true);
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("Failed to write Recast patch to: " + outputPath + Environment.NewLine +
                      ExceptionLogger.GetExceptionStack(ex), true, true);
            return false;
        }
    }

    /// <summary>
    /// The TOML text. Blocks are sorted by (remapped) target so reruns diff cleanly; lines end in
    /// <c>\n</c> whatever the platform, since Recast's parser does not care and goldens do.
    /// </summary>
    private static string Render(IEnumerable<FaceEntry> entries, int priority,
        IReadOnlyDictionary<FormKey, FormKey>? formKeyRemap)
    {
        FormKey Remap(FormKey fk) =>
            formKeyRemap != null && formKeyRemap.TryGetValue(fk, out var mapped) ? mapped : fk;

        var sb = new StringBuilder();
        sb.Append("# Generated by NPC Plugin Chooser 2. Do not edit: it is regenerated every run.\n");
        sb.Append("[manifest]\n");
        sb.Append("name = ").Append(Quote(ManifestName)).Append('\n');
        sb.Append("priority = ").Append(priority.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("api_version = 1\n");

        var ordered = entries
            .Select(e => (Target: Remap(e.Target), Face: Remap(e.Face), e.Comment))
            .OrderBy(e => e.Target.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Target.ID);

        foreach (var entry in ordered)
        {
            sb.Append('\n');
            if (entry.Comment != null)
            {
                sb.Append("# ").Append(ToCommentText(entry.Comment)).Append('\n');
            }
            sb.Append("[[npcs]]\n");
            sb.Append("target = ").Append(Quote(FormatSelector(entry.Target))).Append('\n');
            sb.Append("face = ").Append(Quote(FormatSelector(entry.Face))).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Recast's FormID selector, <c>0x&lt;local id&gt;~&lt;plugin&gt;</c>. <see cref="FormKey.ID"/> is
    /// already the plugin-local ID Recast hands to <c>TESDataHandler::LookupForm</c>, for light and
    /// full plugins alike. FormID selectors work without powerofthree's Tweaks; EditorIDs do not.
    /// </summary>
    public static string FormatSelector(FormKey formKey) =>
        $"0x{formKey.ID.ToString("X8", CultureInfo.InvariantCulture)}~{formKey.ModKey.FileName}";

    /// <summary>A TOML basic string: backslash, quote and control characters escaped.</summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// A TOML comment runs to the end of the line and may not hold control characters other than
    /// tab, so an NPC name with a line break (or worse) becomes one clean line.
    /// </summary>
    private static string ToCommentText(string comment)
    {
        var sb = new StringBuilder(comment.Length);
        foreach (char c in comment)
        {
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    private void ClearAllGeneratedTomls(string outputRootFolder)
    {
        var outputDir = GetOutputDir(outputRootFolder);
        if (!Directory.Exists(outputDir)) return;

        foreach (var file in Directory.EnumerateFiles(outputDir, "*.toml"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e)
            {
                AppendLog("Failed to clear stale Recast patch at " + file + Environment.NewLine +
                          ExceptionLogger.GetExceptionStack(e), true, true);
            }
        }
    }

    private bool ClearToml(ModKey? outputPlugin, string outputRootFolder, out string exceptionStr)
    {
        exceptionStr = string.Empty;
        if (outputPlugin == null || outputPlugin.Value.IsNull)
        {
            return true;
        }

        (_, var outputPath) = GetOutputPath(outputPlugin.Value, outputRootFolder);
        if (!File.Exists(outputPath))
        {
            return true;
        }

        try
        {
            File.Delete(outputPath);
            return true;
        }
        catch (Exception e)
        {
            exceptionStr = "Failed to clear Recast patch at " + outputPath + Environment.NewLine +
                           ExceptionLogger.GetExceptionStack(e);
            return false;
        }
    }

    /// <summary>NPC2's own subfolder of Recast's patch tree; Recast loads <c>Patches\**.toml</c>.</summary>
    public static string GetOutputDir(string outputRootFolder) =>
        Path.Combine(outputRootFolder, "SKSE", "Plugins", "Recast", "Patches", "NPC Plugin Chooser");

    private static (string outputDir, string outputPath) GetOutputPath(ModKey outputPlugin, string outputRootFolder)
    {
        string outputDir = GetOutputDir(outputRootFolder);
        return (outputDir, Path.Combine(outputDir, $"{outputPlugin.Name}.toml"));
    }
}
