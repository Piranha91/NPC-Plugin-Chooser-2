using System;
using System.IO;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// How the renderer's <see cref="SkyRender.GameAssetResolver"/> serves BSA assets, chosen at
/// startup. <c>extract</c> writes each archive file to <c>SkyRenderCache</c>; <c>memory</c> reads
/// it through <see cref="Adapters.NpcChooserBsaProviderAdapter.TryReadBytes"/> and writes nothing.
///
/// <para>The user's choice is <see cref="InternalMugshotSettings.ServeArchiveAssetsFromMemory"/> /
/// <see cref="InternalMugshotSettings.ArchiveMemoryBudgetMB"/> (Settings > renderer options). For
/// A/B runs, an environment variable (<c>NPC2_BSA_ASSET_MODE</c> = memory|extract,
/// <c>NPC2_BSA_MEMORY_BUDGET_MB</c>) or, for MO2 launches where an environment is awkward to set,
/// a <c>BsaAssetMode.txt</c> next to the exe holding <c>mode=memory</c> / <c>budgetMB=64</c> lines
/// overrides it; an environment variable wins over the file, as with <c>DdsMaxFinalMip.txt</c>.
/// Read once: the resolver fixes its mode at construction.</para>
/// </summary>
public static class BsaAssetMode
{
    public const string ModeVariable = "NPC2_BSA_ASSET_MODE";
    public const string BudgetVariable = "NPC2_BSA_MEMORY_BUDGET_MB";
    public const string TriggerFileName = "BsaAssetMode.txt";

    /// <summary>Where extraction mode writes archive files (SkyRender's default cache folder;
    /// NPC2 passes no other).</summary>
    public static string ExtractionCacheFolder => Path.Combine(AppContext.BaseDirectory, "SkyRenderCache");

    /// <summary>The extraction folder of the pre-SkyRender CharacterViewer renderer.</summary>
    public static string LegacyExtractionCacheFolder => Path.Combine(AppContext.BaseDirectory, "CharacterViewerCache");

    /// <param name="BudgetBytes">Null keeps SkyRender's default budget.</param>
    public sealed record Options(bool ExtractToDisk, long? BudgetBytes, string Source);

    public static Options Read(InternalMugshotSettings settings)
    {
        string? mode = settings.ServeArchiveAssetsFromMemory ? "memory" : "extract";
        string? budget = settings.ArchiveMemoryBudgetMB.ToString();
        string source = "settings";
        try
        {
            string triggerPath = Path.Combine(AppContext.BaseDirectory, TriggerFileName);
            if (File.Exists(triggerPath))
            {
                foreach (var rawLine in File.ReadAllLines(triggerPath))
                {
                    var parts = rawLine.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2) continue;
                    if (parts[0].Equals("mode", StringComparison.OrdinalIgnoreCase)) mode = parts[1];
                    else if (parts[0].Equals("budgetMB", StringComparison.OrdinalIgnoreCase)) budget = parts[1];
                }
                source = TriggerFileName;
            }
        }
        catch (Exception ex)
        {
            source = $"settings ({TriggerFileName} unreadable: {ex.Message})";
        }

        if (Environment.GetEnvironmentVariable(ModeVariable) is { Length: > 0 } envMode)
        {
            mode = envMode;
            source = ModeVariable;
        }
        if (Environment.GetEnvironmentVariable(BudgetVariable) is { Length: > 0 } envBudget)
        {
            budget = envBudget;
        }

        bool extract = !string.Equals(mode?.Trim(), "memory", StringComparison.OrdinalIgnoreCase);
        long? budgetBytes = long.TryParse(budget, out long mb) && mb > 0 ? mb * 1024 * 1024 : null;
        return new Options(extract, budgetBytes, source);
    }
}
