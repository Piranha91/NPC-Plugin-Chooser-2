using System.IO;
using Noggog;

namespace NPC_Plugin_Chooser_2.BackEnd;

using System.Collections.Concurrent;

public static class PluginArchiveIndex
{
    private sealed class DirectoryIndex
    {
        public readonly IReadOnlyList<string> EspBasesByLenDesc;
        public readonly IReadOnlyDictionary<string, string[]> BsaByOwnerBase;

        public DirectoryIndex(IReadOnlyList<string> espBasesByLenDesc,
                              IReadOnlyDictionary<string, string[]> bsaByOwnerBase)
        {
            EspBasesByLenDesc = espBasesByLenDesc;
            BsaByOwnerBase = bsaByOwnerBase;
        }
    }

    private static readonly ConcurrentDictionary<string, Lazy<DirectoryIndex>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static HashSet<string> GetOwnedBsaFiles(Mutagen.Bethesda.Plugins.ModKey modKey, string directory)
    {
        if (modKey.IsNull || directory.IsNullOrWhitespace() || !Directory.Exists(directory))
        {
            return new HashSet<string>();
        }
        
        var index = _cache.GetOrAdd(directory, dir => new Lazy<DirectoryIndex>(() => BuildIndex(dir))).Value;

        string currentPluginBase = Path.GetFileNameWithoutExtension(modKey.FileName.ToString());

        if (index.BsaByOwnerBase.TryGetValue(currentPluginBase, out var array))
        {
            // Return as HashSet for fast lookups, OrdinalIgnoreCase for Windows FS
            return new HashSet<string>(array, StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public static void Invalidate(string directory) => _cache.TryRemove(directory, out _);

    private static DirectoryIndex BuildIndex(string directory)
    {
        // Add a guard clause to protect against empty or null paths
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return new DirectoryIndex(new List<string>(), new Dictionary<string, string[]>());
        }
        
        var allPluginBases = Auxilliary.ValidPluginExtensions
            .SelectMany(ext => Directory.EnumerateFiles(directory, $"*{ext}", SearchOption.TopDirectoryOnly))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n!.Length)
            .ToList();

        var ownerToBsas = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var bsaPath in Directory.EnumerateFiles(directory, "*.bsa", SearchOption.TopDirectoryOnly))
        {
            string bsaBase = Path.GetFileNameWithoutExtension(bsaPath);

            string? ownerPluginBase = allPluginBases.FirstOrDefault(
                pluginBase => bsaBase.StartsWith(pluginBase, StringComparison.OrdinalIgnoreCase));

            if (ownerPluginBase is null)
                continue;

            bool isExactMatch = bsaBase.Length == ownerPluginBase.Length;
            bool isSubArchiveMatch = bsaBase.Length > ownerPluginBase.Length
                                     && bsaBase[ownerPluginBase.Length] == ' ';

            if (!(isExactMatch || isSubArchiveMatch))
                continue;

            if (!ownerToBsas.TryGetValue(ownerPluginBase, out var list))
            {
                list = new List<string>();
                ownerToBsas[ownerPluginBase] = list;
            }
            list.Add(bsaPath);
        }

        // Convert lists to arrays for minimal memory & fast iteration
        var finalDict = ownerToBsas.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        return new DirectoryIndex(allPluginBases, finalDict);
    }
}
