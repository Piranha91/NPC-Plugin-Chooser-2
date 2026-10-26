using System.IO;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Benchmark;

/// <summary>
/// Builds NPC2's real <see cref="EnvironmentStateProvider"/> the way it resolves when the app is launched
/// <em>through</em> a Mod Organizer 2 instance, without the MO2 virtual file system: the Data folder is the
/// instance's configured <c>gamePath</c> (read from <c>ModOrganizer.ini</c>, never auto-detected), and the
/// profile's active plugins are resolved the way the VFS would — <c>overwrite</c> first, then enabled mods
/// in <c>modlist.txt</c> priority order (top line = highest priority), then the real Data folder — and
/// injected through <see cref="EnvironmentStateProvider.UpdateEnvironmentForTest"/> (the same seam
/// <c>GoldenEnvironmentBuilder</c> uses). The production trimming of the app's own output plugin
/// (<c>TrimDependentPlugins</c>) is applied to the result.
///
/// <para>This exists so the discovery benchmark provably targets one specific installation (by default
/// <c>S:\Skyrim NPC Selection</c>, profile <c>NPC Test</c>) instead of whatever <c>plugins.txt</c> happens
/// to sit in <c>%LOCALAPPDATA%</c> — on the reference machine that file is empty, so auto-detection would
/// silently produce a vanilla-only load order.</para>
/// </summary>
internal sealed class Mo2ProfileEnvironment
{
    public required EnvironmentStateProvider Provider { get; init; }
    public required string InstanceRoot { get; init; }
    public required string ProfileName { get; init; }
    public required string GameDataFolder { get; init; }
    public required IReadOnlyList<string> EnabledModFolders { get; init; }
    /// <summary>Active profile plugins, in load order, with where each was resolved from.</summary>
    public required IReadOnlyList<(ModKey Key, string ResolvedPath)> ResolvedPlugins { get; init; }
    /// <summary>Active profile plugins no folder supplied (the VFS would not have shown them either).</summary>
    public required IReadOnlyList<ModKey> UnresolvedPlugins { get; init; }

    public static bool TryBuild(string instanceRoot, string profileName, string outputPluginName,
        out Mo2ProfileEnvironment? environment, out string failure)
    {
        environment = null;
        failure = string.Empty;

        var iniPath = Path.Combine(instanceRoot, "ModOrganizer.ini");
        var profileDir = Path.Combine(instanceRoot, "profiles", profileName);
        var modsDir = Path.Combine(instanceRoot, "mods");
        var overwriteDir = Path.Combine(instanceRoot, "overwrite");
        foreach (var required in new[] { iniPath, Path.Combine(profileDir, "modlist.txt"),
                     Path.Combine(profileDir, "plugins.txt"), Path.Combine(profileDir, "loadorder.txt") })
        {
            if (!File.Exists(required))
            {
                failure = $"MO2 instance file missing: {required}";
                return false;
            }
        }

        var gamePath = ReadGamePath(iniPath);
        if (gamePath == null)
        {
            failure = $"No gamePath in {iniPath}";
            return false;
        }
        var dataFolder = Path.Combine(gamePath, "Data");
        if (!Directory.Exists(dataFolder))
        {
            failure = $"MO2 gamePath Data folder does not exist: {dataFolder}";
            return false;
        }

        // modlist.txt: '+' enabled, '-' disabled, '*' unmanaged; first listed = highest priority.
        var enabledMods = File.ReadAllLines(Path.Combine(profileDir, "modlist.txt"))
            .Where(l => l.StartsWith('+'))
            .Select(l => Path.Combine(modsDir, l.Substring(1).Trim()))
            .Where(Directory.Exists)
            .ToList();
        var searchOrder = new List<string>();
        if (Directory.Exists(overwriteDir)) searchOrder.Add(overwriteDir);
        searchOrder.AddRange(enabledMods);
        searchOrder.Add(dataFolder);

        var active = new HashSet<ModKey>();
        foreach (var raw in File.ReadAllLines(Path.Combine(profileDir, "plugins.txt")))
        {
            var line = raw.Trim();
            if (!line.StartsWith('*')) continue;
            if (ModKey.TryFromFileName(line.Substring(1).Trim()) is { } key) active.Add(key);
        }

        var implicitKeys = Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToHashSet();
        var order = new List<ModKey>();
        var seen = new HashSet<ModKey>();
        foreach (var raw in File.ReadAllLines(Path.Combine(profileDir, "loadorder.txt")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (ModKey.TryFromFileName(line) is not { } key || !seen.Add(key)) continue;
            // Implicit masters (base game) are always active; CC plugins follow the profile like any other.
            if (implicitKeys.Contains(key) || active.Contains(key)) order.Add(key);
        }

        var resolved = new List<(ModKey, string)>();
        var unresolved = new List<ModKey>();
        foreach (var key in order)
        {
            var path = searchOrder.Select(dir => Path.Combine(dir, key.FileName)).FirstOrDefault(File.Exists);
            if (path == null) unresolved.Add(key);
            else resolved.Add((key, path));
        }

        var outputKey = ModKey.FromFileName(Path.GetFileNameWithoutExtension(outputPluginName) + ".esp");
        IEnumerable<IModListingGetter<ISkyrimModGetter>> Transform(
            IEnumerable<IModListingGetter<ISkyrimModGetter>> _)
        {
            var listings = resolved
                .Select(r => (IModListingGetter<ISkyrimModGetter>)new ModListing<ISkyrimModGetter>(r.Item1,
                    SkyrimMod.CreateFromBinaryOverlay(r.Item2, SkyrimRelease.SkyrimSE), enabled: true))
                .ToList();
            // Same post-filter as the production UpdateEnvironment path.
            return listings.OnlyEnabledAndExisting().TrimDependentPlugins(outputKey).ToList();
        }

        try
        {
            var provider = new EnvironmentStateProvider(null);
            provider.SetEnvironmentTarget(SkyrimRelease.SkyrimSE, dataFolder, outputPluginName);
            provider.UpdateEnvironmentForTest(Transform);

            if (provider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid || provider.LoadOrder == null)
            {
                failure = "MO2-profile environment did not resolve: " + provider.EnvironmentBuilderError;
                return false;
            }
            if (!string.Equals(Path.GetFullPath(provider.DataFolderPath.Path).TrimEnd('\\'),
                    Path.GetFullPath(dataFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                failure = $"Environment resolved Data folder '{provider.DataFolderPath}' instead of the MO2 " +
                          $"instance's '{dataFolder}'";
                return false;
            }

            environment = new Mo2ProfileEnvironment
            {
                Provider = provider,
                InstanceRoot = instanceRoot,
                ProfileName = profileName,
                GameDataFolder = dataFolder,
                EnabledModFolders = enabledMods,
                ResolvedPlugins = resolved,
                UnresolvedPlugins = unresolved,
            };
            return true;
        }
        catch (Exception ex)
        {
            failure = "Exception building the MO2-profile environment: " + ex;
            return false;
        }
    }

    /// <summary>Parses <c>gamePath=@ByteArray(C:\\Games\\...)</c> from ModOrganizer.ini's [General] section.</summary>
    private static string? ReadGamePath(string iniPath)
    {
        foreach (var line in File.ReadLines(iniPath))
        {
            var m = Regex.Match(line, @"^gamePath\s*=\s*(?:@ByteArray\((?<v>.*)\)|(?<v>.*))\s*$");
            if (!m.Success) continue;
            return m.Groups["v"].Value.Replace(@"\\", @"\").Trim();
        }
        return null;
    }
}
