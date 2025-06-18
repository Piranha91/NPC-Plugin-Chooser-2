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
    private Dictionary<ModKey, ISkyrimModGetter> _pluginCache = new();

    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Settings _settings;

    public PluginProvider(EnvironmentStateProvider environmentStateProvider, Settings settings)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
    }

    public bool TryGetPlugin(ModKey modKey, string? fallBackModPath, out ISkyrimModGetter? plugin)
    {
        if (modKey.FileName.String.StartsWith("NPC"))
        {
            var debug = true;
        }
        if (_pluginCache.TryGetValue(modKey, out plugin))
        {
            return true;
        }

        var modListing = _environmentStateProvider.LoadOrder.TryGetValue(modKey);
        if (modListing != null)
        {
            plugin = modListing.Mod;
            if (plugin != null)
            {
                _pluginCache.Add(modKey, plugin);
                return true;
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

    public bool TryGetRecord(FormKey formKey, ModKey modKey, Type? type, out IMajorRecordGetter? record)
    {
        record = null;
        if (TryGetPlugin(modKey, _settings.ModsFolder, out var plugin) && plugin != null)
        {
            var cache = plugin.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
            if (type != null)
            {
                if (cache.TryResolve(formKey, type, out record))
                {
                    return true;
                }
            }
            else
            {
                if (cache.TryResolve(formKey, out record))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool TryGetRecord(FormKey formKey, IEnumerable<ModKey> modKeys, Type? type, RecordLookupFallBack fallbackMode, out IMajorRecordGetter? record, bool reverseOrder = true)
    {
        record = null;
        if (modKeys == null)
        {
            return false;
        }
        
        var toSearch = modKeys.Reverse().ToArray();
        if (!reverseOrder)
        {
            toSearch = modKeys.ToArray();
        }

        foreach (var mk in toSearch)
        {
            if (TryGetRecord(formKey, mk, type, out record) && record != null)
            {
                return true;
            }
        }
        
        // fallbacks
        switch (fallbackMode)
        {
            case RecordLookupFallBack.Origin:
                if (TryGetRecord(formKey, formKey.ModKey, type,  out record) && record != null)
                {
                    return true;
                }
                break;
            
            case RecordLookupFallBack.Winner:
                if (type != null)
                {
                    if (_environmentStateProvider.LinkCache.TryResolve(formKey, type, out record) && record is not null)
                    {
                        return true;
                    }
                }
                else
                {
                    if (_environmentStateProvider.LinkCache.TryResolve(formKey, out record) && record is not null)
                    {
                        return true;
                    }
                }

                break;
            
            case RecordLookupFallBack.None:
            default:
                break;
        }
        
        return false;
    }
}