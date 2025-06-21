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

    public bool TryGetPlugin(ModKey modKey, string? fallBackModPath, out ISkyrimMod? plugin)
    {
        if (_pluginCache.TryGetValue(modKey, out plugin))
        {
            return true;
        }

        if (_environmentStateProvider.DataFolderPath.Exists)
        {
            var fullPath = Path.Combine(_environmentStateProvider.DataFolderPath, modKey.ToString());
            if (File.Exists(fullPath))
            {
                var imported = SkyrimMod.CreateFromBinary(fullPath, SkyrimRelease.SkyrimSE);
                plugin = imported;
                if (plugin != null)
                {
                    _pluginCache.Add(modKey, plugin);
                    return true;
                }
            } 
        }
        
        
        if (fallBackModPath != null)
        {
            var fullPath = Path.Combine(_settings.ModsFolder, fallBackModPath, modKey.ToString());
            if (File.Exists(fullPath))
            {
                var imported = SkyrimMod.CreateFromBinary(fullPath, SkyrimRelease.SkyrimSE);
                plugin = imported;
                if (plugin != null)
                {
                    _pluginCache.Add(modKey, plugin);
                    return true;
                }
            }
        }

        return false;
    }
}