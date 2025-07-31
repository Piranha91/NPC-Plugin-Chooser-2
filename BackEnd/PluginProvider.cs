using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using static NPC_Plugin_Chooser_2.BackEnd.RecordHandler;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class PluginProvider
{
    private Dictionary<ModKey, ISkyrimMod> _pluginCache = new();

    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Settings _settings;

    public PluginProvider(EnvironmentStateProvider environmentStateProvider, Settings settings)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
    }

    public void LoadPlugins(IEnumerable<ModKey> keys, HashSet<string> modFolderNames)
    {
        foreach (var key in keys)
        {
            if (!_pluginCache.ContainsKey(key))
            {
                TryGetPlugin(key, modFolderNames, out _);
            }
        }
    }

    public void UnloadPlugins(IEnumerable<ModKey> keys)
    {
        foreach (var key in keys)
        {
            _pluginCache.Remove(key);
        }
    }

    public bool TryGetPlugin(ModKey modKey, HashSet<string>? fallBackModFolderPaths, out ISkyrimMod? plugin)
    {
        if (_pluginCache.TryGetValue(modKey, out plugin))
        {
            return true;
        }

        string? foundPath = null;
 
        if (fallBackModFolderPaths != null)
        {
            foreach (var modFolderPath in fallBackModFolderPaths)
            {
                var candidatePath = Path.Combine(modFolderPath, modKey.ToString());
                if (File.Exists(candidatePath))
                {
                    foundPath = candidatePath;
                    break;
                }
            }
        }
        
        if (foundPath == null && _environmentStateProvider.DataFolderPath.Exists)
        {
            var candidatePath = Path.Combine(_environmentStateProvider.DataFolderPath, modKey.ToString());
            if (File.Exists(candidatePath))
            {
                foundPath = candidatePath;
            }
        }

        if (foundPath != null)
        {
            try
            {
                var imported = SkyrimMod.CreateFromBinary(foundPath, SkyrimRelease.SkyrimSE);
                plugin = imported;
                if (plugin != null)
                {
                    _pluginCache.Add(modKey, plugin);
                    return true;
                }
            }
            catch (Exception e)
            {
                throw new Exception("Failed to load plugin from: " + foundPath, e);
            }
        }

        plugin = null;
        return false;
    }

    public HashSet<ModKey> GetMasterPlugins(ModKey modKey, IEnumerable<string>? fallBackModFolderPaths)
    {
        HashSet<ModKey> masterPlugins = new();

        if (TryGetPlugin(modKey, fallBackModFolderPaths?.ToHashSet(), out var plugin) && plugin != null)
        {
            masterPlugins.UnionWith(plugin.ModHeader.MasterReferences.Select(x => x.Master).ToHashSet());
        }
        
        return masterPlugins;
    }
}