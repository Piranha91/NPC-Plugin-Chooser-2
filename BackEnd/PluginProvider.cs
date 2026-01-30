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
            public ModKey ModKey { get; }
            public string SourcePath { get; }
            public int ReferenceCount { get; set; }

            public CachedPlugin(ISkyrimModGetter plugin, ModKey modKey, string sourcePath)
            {
                Plugin = plugin;
                ModKey = modKey;
                SourcePath = sourcePath;
                ReferenceCount = 1; // Start with one reference upon creation
            }
        }

        // --- State ---
        // Cache key is now the normalized (case-insensitive) full file path
        private readonly ConcurrentDictionary<string, CachedPlugin> _pluginCache = new();
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
        /// Normalizes a file path to be used as a cache key (case-insensitive).
        /// </summary>
        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).ToUpperInvariant();
        }

        /// <summary>
        /// Resolves a ModKey to its full file path by searching the provided folders and the data folder.
        /// Returns null if the file is not found.
        /// </summary>
        private string? ResolveModKeyToPath(ModKey modKey, HashSet<string>? fallBackModFolderPaths)
        {
            if (fallBackModFolderPaths != null)
            {
                foreach (var modFolderPath in fallBackModFolderPaths)
                {
                    var candidatePath = Path.Combine(modFolderPath, modKey.ToString());
                    if (File.Exists(candidatePath))
                    {
                        return candidatePath;
                    }
                }
            }

            if (_environmentStateProvider.DataFolderPath.Exists)
            {
                var candidatePath = Path.Combine(_environmentStateProvider.DataFolderPath, modKey.ToString());
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets a set of plugins for the given keys.
        /// It increments the reference count for each returned plugin.
        /// Returns both the plugins and their resolved paths for use with UnloadPlugins.
        /// </summary>
        public HashSet<ISkyrimModGetter> LoadPlugins(IEnumerable<ModKey> keys, HashSet<string> modFolderNames, out HashSet<string> loadedPaths, bool asReadOnly = true)
        {
            var plugins = new HashSet<ISkyrimModGetter>();
            loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (keys == null) return plugins;

            lock (_cacheLock)
            {
                foreach (var key in keys.Distinct())
                {
                    // First, resolve the ModKey to a full path
                    var resolvedPath = ResolveModKeyToPath(key, modFolderNames);
                    if (resolvedPath == null) continue;

                    var cacheKey = NormalizePath(resolvedPath);

                    if (_pluginCache.TryGetValue(cacheKey, out var entry))
                    {
                        // Plugin exists at this path, increment its reference count
                        entry.ReferenceCount++;
                        plugins.Add(entry.Plugin);
                        loadedPaths.Add(resolvedPath);
                    }
                    else
                    {
                        // Plugin not in cache, load it from disk
                        if (TryLoadPluginFromPath(resolvedPath, key, out var plugin, asReadOnly) && plugin != null)
                        {
                            var newEntry = new CachedPlugin(plugin, key, resolvedPath);
                            _pluginCache[cacheKey] = newEntry; // Adds with ReferenceCount = 1
                            plugins.Add(plugin);
                            loadedPaths.Add(resolvedPath);
                        }
                    }
                }
            }
            return plugins;
        }

        /// <summary>
        /// Gets a set of plugins for the given keys.
        /// It increments the reference count for each returned plugin.
        /// </summary>
        public HashSet<ISkyrimModGetter> LoadPlugins(IEnumerable<ModKey> keys, HashSet<string> modFolderNames, bool asReadOnly = true)
        {
            return LoadPlugins(keys, modFolderNames, out _, asReadOnly);
        }

        /// <summary>
        /// Decrements the reference count for plugins at the specified paths.
        /// If a plugin's reference count reaches zero, it is disposed and removed from the cache.
        /// </summary>
        public void UnloadPlugins(IEnumerable<string> pluginPaths)
        {
            if (pluginPaths == null) return;

            lock (_cacheLock)
            {
                foreach (var path in pluginPaths.Distinct())
                {
                    var cacheKey = NormalizePath(path);
                    
                    if (_pluginCache.TryGetValue(cacheKey, out var entry))
                    {
                        entry.ReferenceCount--;
                        if (entry.ReferenceCount <= 0)
                        {
                            // No more references, safe to dispose
                            _pluginCache.TryRemove(cacheKey, out _);
                            (entry.Plugin as IDisposable)?.Dispose();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Decrements the reference count for plugins with the given ModKeys.
        /// Requires the same modFolderNames used during loading to resolve paths correctly.
        /// If a plugin's reference count reaches zero, it is disposed and removed from the cache.
        /// </summary>
        public void UnloadPlugins(IEnumerable<ModKey> keys, HashSet<string> modFolderNames)
        {
            if (keys == null) return;

            var pathsToUnload = new List<string>();
            foreach (var key in keys.Distinct())
            {
                var resolvedPath = ResolveModKeyToPath(key, modFolderNames);
                if (resolvedPath != null)
                {
                    pathsToUnload.Add(resolvedPath);
                }
            }

            UnloadPlugins(pathsToUnload);
        }

        /// <summary>
        /// Loads a plugin from a specific path without reference counting.
        /// </summary>
        private bool TryLoadPluginFromPath(string path, ModKey modKey, out ISkyrimModGetter? plugin, bool asReadOnly = true)
        {
            plugin = null;
            
            try
            {
                plugin = asReadOnly
                    ? SkyrimMod.CreateFromBinaryOverlay(path, _environmentStateProvider.SkyrimVersion)
                    : SkyrimMod.CreateFromBinary(path, _environmentStateProvider.SkyrimVersion);

                return plugin != null;
            }
            catch (Exception e)
            {
                throw new Exception($"Failed to load plugin from: {path}", e);
            }
        }

        /// <summary>
        /// Tries to get a plugin, checking the cache first, then loading from disk if not found.
        /// Note: This does NOT increment the reference count. Use LoadPlugins for managed access.
        /// </summary>
        public bool TryGetPlugin(ModKey modKey, HashSet<string>? fallBackModFolderPaths, out ISkyrimModGetter? plugin, out string? pluginSourcePath, bool asReadOnly = true)
        {
            plugin = null;
            pluginSourcePath = null;

            lock (_cacheLock)
            {
                // Resolve path first
                var resolvedPath = ResolveModKeyToPath(modKey, fallBackModFolderPaths);
                if (resolvedPath == null) return false;

                var cacheKey = NormalizePath(resolvedPath);

                if (_pluginCache.TryGetValue(cacheKey, out var entry))
                {
                    plugin = entry.Plugin;
                    pluginSourcePath = entry.SourcePath;
                    return true;
                }
                else
                {
                    if (TryLoadPluginFromPath(resolvedPath, modKey, out plugin, asReadOnly))
                    {
                        pluginSourcePath = resolvedPath;
                        return true;
                    }
                    return false;
                }
            }
        }

        public bool TryGetPlugin(ModKey modKey, HashSet<string>? fallBackModFolderPaths, out ISkyrimModGetter? plugin, bool asReadOnly = true)
        {
            return TryGetPlugin(modKey, fallBackModFolderPaths, out plugin, out _, asReadOnly);
        }

        public HashSet<ModKey> GetMasterPlugins(ModKey modKey, IEnumerable<string>? fallBackModFolderPaths)
        {
            var masterPlugins = new HashSet<ModKey>();
            if (TryGetPlugin(modKey, fallBackModFolderPaths?.ToHashSet(), out var plugin, out _) && plugin != null)
            {
                masterPlugins.UnionWith(plugin.ModHeader.MasterReferences.Select(x => x.Master));
            }
            return masterPlugins;
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
                    return "Cached Plugins: " + Environment.NewLine + 
                           string.Join(Environment.NewLine, _pluginCache.Select(x => 
                               $"\t{x.Value.ModKey} @ {x.Value.SourcePath} (Ref Count: {x.Value.ReferenceCount})"));
                }
            }
        }
    }
}