using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Reproduces the exact patch-time environment that generated the reference outputs: vanilla + DLC +
/// Creation Club auto-detected from the game's Data folder, plus the active non-vanilla plugins
/// (USSEP, AI Overhaul + addons) loaded from their <b>mod-manager folders</b> and injected at the right
/// load-order position via <see cref="EnvironmentStateProvider.UpdateEnvironmentForTest"/>. The prior
/// <c>NPC.esp</c> output is trimmed (never injected), reproducing the mod manager's "the output is just
/// another active plugin" situation without letting its stale records leak into the link cache.
/// </summary>
internal sealed class GoldenEnvironment
{
    public required EnvironmentStateProvider Provider { get; init; }
    /// <summary>The extra plugins (USSEP/AI Overhaul) that were loaded from mod folders and injected.</summary>
    public required IReadOnlyList<ModKey> InjectedExtraKeys { get; init; }
    /// <summary>Desired vanilla/CC keys that were NOT present in the auto-detected load order (diagnostics).</summary>
    public required IReadOnlyList<ModKey> MissingKeys { get; init; }
    public required ModKey OutputKey { get; init; }
}

internal static class GoldenEnvironmentBuilder
{
    private static readonly ModKey[] BaseMasters =
        Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();

    public static bool TryBuild(GoldenOutputConfig config, string outputModFolderPath, string outputPluginName,
        out GoldenEnvironment? environment, out string skipReason)
    {
        environment = null;
        skipReason = string.Empty;

        var outputKey = ModKey.FromFileName(Path.ChangeExtension(outputPluginName, ".esp"));

        // Map each active-extra plugin (USSEP, AI Overhaul...) to the mod folder it must be loaded from.
        var extraByKey = new Dictionary<ModKey, string>();
        var extraOrder = new List<ModKey>();
        foreach (var entry in config.EnvironmentPlugins)
        {
            var key = ModKey.FromFileName(entry.Plugin);
            extraByKey[key] = entry.Folder;
            extraOrder.Add(key);
        }

        // The desired load order: from the MO2 profile files when provided (faithful), else vanilla/CC
        // discovered by Mutagen with the extras appended.
        List<ModKey>? desiredOrder = TryReadProfileOrder(config, outputKey, extraByKey.Keys.ToHashSet());

        var injected = new List<ModKey>();
        var missing = new List<ModKey>();

        IEnumerable<IModListingGetter<ISkyrimModGetter>> Transform(
            IEnumerable<IModListingGetter<ISkyrimModGetter>> input)
        {
            var existing = input.OnlyEnabledAndExisting().ToList();
            var existingByKey = new Dictionary<ModKey, IModListingGetter<ISkyrimModGetter>>();
            foreach (var listing in existing)
            {
                existingByKey[listing.ModKey] = listing;
            }

            // Fallback order when no profile files: vanilla/CC the builder found, then the extras.
            var order = desiredOrder ?? existing.Where(l => IsVanillaOrCc(l.ModKey))
                .Select(l => l.ModKey).Concat(extraOrder).ToList();

            var result = new List<IModListingGetter<ISkyrimModGetter>>();
            foreach (var key in order)
            {
                if (key == outputKey) continue; // trim the app's own prior output
                if (extraByKey.TryGetValue(key, out var folder))
                {
                    var path = Path.Combine(folder, key.FileName);
                    var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                    result.Add(new ModListing<ISkyrimModGetter>(key, mod, enabled: true));
                    injected.Add(key);
                }
                else if (existingByKey.TryGetValue(key, out var listing))
                {
                    result.Add(listing);
                }
                else
                {
                    missing.Add(key);
                }
            }

            return result;
        }

        try
        {
            var provider = new EnvironmentStateProvider(null);
            provider.SetEnvironmentTarget(SkyrimRelease.SkyrimSE, string.Empty, outputPluginName, outputModFolderPath);
            provider.UpdateEnvironmentForTest(Transform);

            if (provider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid
                || provider.LinkCache == null
                || provider.LoadOrder == null)
            {
                skipReason = "Could not resolve a valid Skyrim SE environment for the golden test" +
                             (string.IsNullOrEmpty(provider.EnvironmentBuilderError)
                                 ? "."
                                 : ": " + provider.EnvironmentBuilderError);
                return false;
            }

            // Every active extra must actually have loaded; otherwise the env doesn't match the reference set.
            var loaded = provider.LoadOrder.ListedOrder.Select(l => l.ModKey).ToHashSet();
            var missingExtras = extraOrder.Where(k => !loaded.Contains(k)).ToList();
            if (missingExtras.Count > 0)
            {
                skipReason = "Required environment plugin(s) failed to load into the resolved load order: " +
                             string.Join(", ", missingExtras.Select(k => k.FileName));
                return false;
            }

            environment = new GoldenEnvironment
            {
                Provider = provider,
                InjectedExtraKeys = injected,
                MissingKeys = missing,
                OutputKey = outputKey
            };
            return true;
        }
        catch (Exception ex)
        {
            skipReason = "Exception building the golden environment: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Parses the profile's loadorder.txt (full order) + plugins.txt (active set marked with '*') into the
    /// ordered set the env should contain: vanilla/CC (always active) plus the '*'-active plugins, minus the
    /// output plugin. Returns null when the files are not configured.
    /// </summary>
    private static List<ModKey>? TryReadProfileOrder(GoldenOutputConfig config, ModKey outputKey,
        HashSet<ModKey> extraKeys)
    {
        if (string.IsNullOrWhiteSpace(config.LoadOrderFile) || !File.Exists(config.LoadOrderFile)) return null;

        var active = new HashSet<ModKey>();
        if (!string.IsNullOrWhiteSpace(config.PluginsFile) && File.Exists(config.PluginsFile))
        {
            foreach (var raw in File.ReadAllLines(config.PluginsFile))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || !line.StartsWith('*')) continue;
                var key = TryModKey(line.Substring(1).Trim());
                if (key != null) active.Add(key.Value);
            }
        }

        var order = new List<ModKey>();
        var seen = new HashSet<ModKey>();
        foreach (var raw in File.ReadAllLines(config.LoadOrderFile))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var key = TryModKey(line);
            if (key == null || key.Value == outputKey || !seen.Add(key.Value)) continue;

            bool include = IsVanillaOrCc(key.Value) || active.Contains(key.Value) || extraKeys.Contains(key.Value);
            if (include) order.Add(key.Value);
        }
        return order.Count > 0 ? order : null;
    }

    private static ModKey? TryModKey(string fileName)
    {
        return ModKey.TryFromFileName(fileName);
    }

    private static bool IsVanillaOrCc(ModKey key)
    {
        if (BaseMasters.Contains(key)) return true;
        string fn = key.FileName;
        return fn.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
               || fn.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
    }
}
