using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd
{
    public class PluginProvider : IDisposable
    {
        // Helper class to hold the plugin and its usage count
        private class CachedPlugin
        {
            public ISkyrimModGetter Plugin { get; }
            public string SourcePath { get; }
            public int ReferenceCount { get; set; }

            public CachedPlugin(ISkyrimModGetter plugin, string sourcePath)
            {
                Plugin = plugin;
                SourcePath = sourcePath;
                ReferenceCount = 1; // Start with one reference upon creation
            }
        }

        // --- State ---
        private readonly ConcurrentDictionary<ModKey, CachedPlugin> _pluginCache = new();
        private readonly object _cacheLock = new(); // Used to synchronize access to ReferenceCount
        private bool _disposed = false;

        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _settings;

        public PluginProvider(EnvironmentStateProvider environmentStateProvider, Settings settings)
        {
            _environmentStateProvider = environmentStateProvider;
            _settings = settings;
        }

        /// <summary>
        /// Gets a set of plugins for the given keys.
        /// It increments the reference count for each returned plugin.
        /// </summary>
        public HashSet<ISkyrimModGetter> LoadPlugins(IEnumerable<ModKey> keys, HashSet<string> modFolderNames, bool asReadyOnly = true)
        {
            var plugins = new HashSet<ISkyrimModGetter>();
            if (keys == null) return plugins;

            lock (_cacheLock)
            {
                foreach (var key in keys.Distinct())
                {
                    if (_pluginCache.TryGetValue(key, out var entry))
                    {
                        // Plugin exists, increment its reference count
                        entry.ReferenceCount++;
                        plugins.Add(entry.Plugin);
                    }
                    else
                    {
                        // Plugin not in cache, load it from disk
                        if (TryGetPluginInternal(key, modFolderNames, out var plugin, out var sourcePath, asReadyOnly) && plugin != null)
                        {
                            var newEntry = new CachedPlugin(plugin, sourcePath);
                            _pluginCache[key] = newEntry; // Adds with ReferenceCount = 1
                            plugins.Add(plugin);
                        }
                    }
                }
            }
            return plugins;
        }

        /// <summary>
        /// Decrements the reference count for each plugin.
        /// If a plugin's reference count reaches zero, it is disposed and removed from the cache.
        /// </summary>
        public void UnloadPlugins(IEnumerable<ModKey> keys)
        {
            if (keys == null) return;

            lock (_cacheLock)
            {
                foreach (var key in keys.Distinct())
                {
                    if (_pluginCache.TryGetValue(key, out var entry))
                    {
                        entry.ReferenceCount--;
                        if (entry.ReferenceCount <= 0)
                        {
                            // No more references, safe to dispose
                            _pluginCache.TryRemove(key, out _);
                            (entry.Plugin as IDisposable)?.Dispose();
                        }
                    }
                }
            }
        }

        // Internal loading logic without reference counting - only called from within a lock.
        private bool TryGetPluginInternal(ModKey modKey, HashSet<string>? fallBackModFolderPaths, out ISkyrimModGetter? plugin, out string pluginSourcePath, bool asReadOnly = true)
        {
            plugin = null;
            pluginSourcePath = null;
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
                    ISkyrimModGetter? imported = asReadOnly
                        ? SkyrimMod.CreateFromBinaryOverlay(foundPath, _environmentStateProvider.SkyrimVersion)
                        : SkyrimMod.CreateFromBinary(foundPath, _environmentStateProvider.SkyrimVersion);

                    plugin = imported;
                    pluginSourcePath = foundPath;
                    return plugin != null;
                }
                catch (Exception e)
                {
                    // It's better to let the exception bubble up here to the analysis task
                    throw new Exception($"Failed to load plugin from: {foundPath}", e);
                }
            }

            return false;
        }

        // Public TryGetPlugin - should be used sparingly, if ever, outside of the main analysis loop
        public bool TryGetPlugin(ModKey modKey, HashSet<string>? fallBackModFolderPaths, out ISkyrimModGetter? plugin, out string? pluginSourcePath, bool asReadyOnly = true)
        {
            lock (_cacheLock)
            {
                 if (_pluginCache.TryGetValue(modKey, out var value))
                 {
                    plugin = value.Plugin;
                    pluginSourcePath = value.SourcePath;
                    return true;
                 }
                 else
                 {
                     return TryGetPluginInternal(modKey, fallBackModFolderPaths, out plugin, out pluginSourcePath, asReadyOnly);
                 }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                lock (_cacheLock)
                {
                    foreach (var kvp in _pluginCache)
                    {
                        (kvp.Value.Plugin as IDisposable)?.Dispose();
                    }
                    _pluginCache.Clear();
                }
            }
            _disposed = true;
        }

        // Other public methods (unchanged from your version, just ensure they exist)
        public bool TryGetPlugin(ModKey modKey, HashSet<string>? fallBackModFolderPaths, out ISkyrimModGetter? plugin, bool asReadyOnly = true)
        {
            return TryGetPlugin(modKey, fallBackModFolderPaths, out plugin, out _, asReadyOnly);
        }
        
        public HashSet<ModKey> GetMasterPlugins(ModKey modKey, IEnumerable<string>? fallBackModFolderPaths)
        {
            var masterPlugins = new HashSet<ModKey>();
            // Note: This temporarily loads a plugin without proper ref counting if not already in cache.
            // Use with caution or refactor if it becomes part of a concurrent loop.
            if (TryGetPlugin(modKey, fallBackModFolderPaths?.ToHashSet(), out var plugin, out _) && plugin != null)
            {
                masterPlugins.UnionWith(plugin.ModHeader.MasterReferences.Select(x => x.Master));
            }
            return masterPlugins;
        }

        public string GetStatusReport()
        {
            lock (_cacheLock)
            {
                if (_pluginCache.IsEmpty)
                {
                    return "No plugins currently cached.";
                }
                else
                {
                    return "Cached Plugins: " + Environment.NewLine + string.Join(Environment.NewLine, _pluginCache.Select(x => $"\t{x.Key} (Ref Count: {x.Value.ReferenceCount})"));
                }
            }
        }
    }
}