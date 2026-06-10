using Microsoft.Build.Tasks;
using System.Collections.Concurrent;
using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class RecordHandler
{
    // Outer Key: Appearance Mod Name
    // Inner Key: FormKey from source plugin
    // Value: FormKey of merged-in record in output plugin
    private Dictionary<FormKey, FormKey> _currentDuplicateInMappings = new();
    private HashSet<IFormLinkGetter> _currenTraversedFormLinks = new();
    
    // For converting plugins into linkcaches and avoiding having to resolve all contexts to get mod-specific records
    private ConcurrentDictionary<ModKey, ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>> _modLinkCaches = new();

    // Plugin instance backing each entry in _modLinkCaches. Held so the
    // deferred-warmup path can prime the SAME instance the link cache wraps
    // (PluginProvider.TryGetPlugin doesn't cache standalone, so a fresh
    // TryGetPlugin call would return a different — still cold — instance).
    private ConcurrentDictionary<ModKey, ISkyrimModGetter> _modLinkCachePlugins = new();

    // Resolved source path each cache was built from. PluginProvider keys its
    // own cache by file path, but _modLinkCaches keys by ModKey alone — so
    // two NPC2 mods that both ship a plugin with the same filename but
    // different contents (e.g. "Bijin Redux" vs "Bijin Redux - SkyPatched",
    // both shipping Bijin Redux.esp) would collide here: whichever mod's
    // render touched the plugin first would populate the cache, and every
    // later render for the OTHER mod would hit CACHED-OK against the wrong
    // physical file and silently fail to resolve records only present in
    // that other file. Tracking the path lets TryAddPluginToCaches detect
    // the collision and evict before the GetOrAdd factory runs.
    private ConcurrentDictionary<ModKey, string> _modLinkCacheSourcePaths = new();

    // Per-ModKey memo of the cold-ESL warmup outcome. Presence = warmup
    // attempted; value = true on successful Npcs.Count, false on throw.
    // Used by TryWarmPlugin to skip repeat work after either outcome.
    // Cleared by ClearLinkCachesFor so a re-loaded plugin instance is
    // re-evaluated.
    private ConcurrentDictionary<ModKey, bool> _warmedPlugins = new();

    private readonly EnvironmentStateProvider _environmentStateProvider;
    private PluginProvider _pluginProvider;
    private readonly Settings _settings;

    public RecordHandler(EnvironmentStateProvider environmentStateProvider, PluginProvider pluginProvider, Settings settings)
    {
        _environmentStateProvider = environmentStateProvider;
        _pluginProvider = pluginProvider;
        _settings = settings;
    }

    public void ResetMapping()
    {
        _currentDuplicateInMappings.Clear();
        _currenTraversedFormLinks.Clear();
    }

    /// <summary>
    /// Seeds an identity remap (formKey -> formKey) into the active duplicate-in
    /// mapping so the merge-in walker treats this record as "already handled" and
    /// will NOT duplicate it into a new record. Used to stop the NPC being patched
    /// from being pulled into the output as a brand-new NPC: its own winning
    /// override often lives in an appearance plugin that is in the duplicate-from
    /// set, so a self-reference (or the input NPC's own override) would otherwise
    /// be deep-copied and re-FormKey'd. Any link to this FormKey now resolves to
    /// the existing output override instead.
    /// </summary>
    public void ProtectRecordFromDuplication(FormKey formKey)
    {
        if (formKey.IsNull) return;
        if (!_currentDuplicateInMappings.ContainsKey(formKey))
        {
            _currentDuplicateInMappings[formKey] = formKey;
        }
    }

    public void PrimeLinkCachesFor(IEnumerable<ModKey> modKeys, HashSet<string> fallBackModFolderNames)
    {
        foreach (var modKey in modKeys)
        {
            if (!_pluginProvider.TryGetPlugin(modKey, fallBackModFolderNames, out var plugin, out var sourcePath) ||
                plugin == null || sourcePath == null)
            {
                continue;
            }

            EvictIfSourcePathChanged(modKey, sourcePath);

            if (_modLinkCaches.ContainsKey(modKey)) continue;

            _modLinkCachePlugins.TryAdd(modKey, plugin);
            _modLinkCacheSourcePaths.TryAdd(modKey, NormalizePath(sourcePath));
            _modLinkCaches.TryAdd(modKey, new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(plugin, new LinkCachePreferences()));
        }
    }

    public void ClearLinkCachesFor(IEnumerable<ModKey> modKeys)
    {
        foreach (var modKey in modKeys)
        {
            _modLinkCaches.TryRemove(modKey, out _);
            _modLinkCachePlugins.TryRemove(modKey, out _);
            _modLinkCacheSourcePaths.TryRemove(modKey, out _);
            _warmedPlugins.TryRemove(modKey, out _);
        }
    }

    /// <summary>Normalizes a filesystem path for case-insensitive equality
    /// comparison between paths that may have been produced by different
    /// code paths (PluginProvider's out param, our own Path.Combine of the
    /// data folder + modKey filename, etc.).</summary>
    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).ToUpperInvariant(); }
        catch { return path.ToUpperInvariant(); }
    }

    /// <summary>If an existing cache entry for <paramref name="modKey"/> was
    /// built from a path different from <paramref name="desiredSourcePath"/>,
    /// evict it so the next <c>GetOrAdd</c> factory call rebuilds the cache
    /// against the right physical file. Two NPC2 mods can share a plugin
    /// filename with different contents, and <c>_modLinkCaches</c> is keyed
    /// by ModKey only — without this check the first mod's render would
    /// poison every later render that targets the same ModKey from a
    /// different folder.</summary>
    private void EvictIfSourcePathChanged(ModKey modKey, string desiredSourcePath)
    {
        if (!_modLinkCacheSourcePaths.TryGetValue(modKey, out var existing)) return;
        string desired = NormalizePath(desiredSourcePath);
        if (string.Equals(existing, desired, StringComparison.OrdinalIgnoreCase)) return;

        if (RenderLogCapture.IsCapturing)
        {
            TraceLookup($"  evicting cache for {modKey.FileName}: cached path '{existing}' " +
                        $"differs from desired '{desired}' (likely two NPC2 mods sharing this plugin filename).");
        }
        ClearLinkCachesFor(new[] { modKey });
    }

    /// <summary>Diagnostic trace that lands in the active mugshot/preview
    /// RenderLogCapture flow file when one is bound, and is a no-op otherwise.
    /// Used to surface where mod-scoped record resolution decides a record
    /// "doesn't exist" — analysis-time vs render-time disagreements that the
    /// boolean return values alone can't pinpoint.</summary>
    private static void TraceLookup(string message)
    {
        if (!RenderLogCapture.IsCapturing) return;
        RenderLogCapture.Write("[RecordHandler] " + message);
    }

    /// <summary>Workaround for a Mutagen 0.53-alpha overlay-reader bug where
    /// the first cold access to a freshly-loaded ESL plugin's NPC group
    /// throws <see cref="ArgumentOutOfRangeException"/>, and any
    /// <see cref="ImmutableModLinkCache{TMod,TModGetter}.TryResolve"/> that
    /// runs before the plugin is primed leaves the link cache in a state
    /// from which it can't recover — even after a subsequent successful
    /// <c>Npcs.Count</c> on the same plugin and rebuilding the link cache.
    /// Symptom: SkyPatcher template-replacer mugshots fail to resolve the
    /// donor NPC even though analysis-time scanning found it just fine.
    ///
    /// <para>Calling <c>plugin.Npcs.Count</c> before the link cache is wrapped
    /// around the plugin sidesteps the bug entirely. Gated on the
    /// LightMaster (Small) flag because the bug is ESL-specific in practice
    /// and unconditional priming here would walk the NPC GRUP of every
    /// loaded full-master appearance plugin, which contended on disk I/O
    /// and froze the UI for ~20s when many tiles render simultaneously.
    /// ESLs cap at 4096 records and are typically much smaller, so priming
    /// every ESL the resolver touches is bounded and fast.</para>
    ///
    /// <para>The deferred warmup in <see cref="TryGetRecordGetterFromMod"/>
    /// remains as a safety net for any non-ESL plugin that ever surfaces
    /// the same failure mode. Throws are swallowed: a corrupt plugin
    /// returns a link cache that resolves nothing — same observable
    /// behavior as today's cold-state failure — and the trace surfaces
    /// the exception when capture is active.</para>
    /// </summary>
    private static void PrimeIfEsl(ISkyrimModGetter plugin)
    {
        try
        {
            if (!plugin.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small)) return;
            _ = plugin.Npcs.Count;
        }
        catch (Exception ex)
        {
            if (RenderLogCapture.IsCapturing)
            {
                TraceLookup($"  PrimeIfEsl threw on '{plugin.ModKey.FileName}' " +
                            $"({ex.GetType().Name}: {ex.Message}); link cache will likely " +
                            $"resolve nothing for this plugin.");
            }
        }
    }

    /// <summary>On-demand workaround for a Mutagen 0.53-alpha overlay-reader
    /// bug where the first cold access to a freshly loaded ESL plugin's NPC
    /// group throws <see cref="ArgumentOutOfRangeException"/> from
    /// <c>Npcs.Count</c> / <c>Npcs.GetEnumerator()</c>, and
    /// <see cref="ImmutableModLinkCache{TMod,TModGetter}.TryResolve"/>
    /// swallows that throw internally and returns <c>false</c> — making the
    /// plugin silently look "empty" of records that are actually present.
    /// Symptom: SkyPatcher template-replacer mugshots fail with "Could not
    /// resolve NPC" even though analysis-time scanning found the same
    /// FormKey just fine.
    ///
    /// <para>Calling <c>Npcs.Count</c> primes the plugin's lazy parser state;
    /// after that, link-cache resolution on the same instance behaves
    /// correctly. We deliberately do NOT prime proactively at link-cache
    /// construction time — when many tiles render simultaneously, eagerly
    /// walking the NPC GRUP of every loaded plugin contended on disk I/O
    /// and froze the UI for ~20s. Instead, this is invoked lazily from
    /// <see cref="TryGetRecordGetterFromMod"/> only when a "missing"
    /// verdict is suspicious (the record's natural ModKey matches the
    /// queried plugin, so the plugin should own it as a new entry rather
    /// than as a master override).</para>
    ///
    /// <para>Result is memoized per-ModKey in <see cref="_warmedPlugins"/>
    /// so each plugin pays the warmup cost at most once per session, even
    /// across many legitimately-missing queries. Returns <c>true</c> if
    /// the plugin was successfully primed (or already had been); returns
    /// <c>false</c> if the warmup threw (the plugin is likely structurally
    /// invalid; the caller should not bother retrying resolution).</para>
    ///
    /// <para>Why analysis-time wasn't affected: <c>RefreshNpcLists</c>
    /// already iterates <c>plugin.Npcs</c> eagerly inside its own
    /// <c>foreach</c>, priming the lazy state before anything else touches
    /// it. The mugshot resolver path bypassed that priming and exposed
    /// the bug.</para>
    /// </summary>
    private bool TryWarmPlugin(ModKey modKey)
    {
        bool capturing = RenderLogCapture.IsCapturing;

        if (_warmedPlugins.TryGetValue(modKey, out var prev))
        {
            if (capturing) TraceLookup($"  TryWarmPlugin {modKey.FileName}: memoized prev={prev}");
            return prev;
        }

        // First-choice source: the plugin instance our factory stashed when
        // this ModKey's link cache was created. Same instance the link cache
        // wraps, so priming it primes the cache's view too.
        ISkyrimModGetter? plugin;
        bool fromStash = _modLinkCachePlugins.TryGetValue(modKey, out plugin) && plugin != null;

        if (!fromStash)
        {
            // Fallback: pull the plugin out of the link cache itself. Covers
            // the case where _modLinkCaches was populated by some code path
            // we didn't update to mirror into _modLinkCachePlugins. For an
            // ImmutableModLinkCache built from a single plugin, PriorityOrder
            // returns that plugin in slot 0.
            if (_modLinkCaches.TryGetValue(modKey, out var existingCache) && existingCache != null)
            {
                plugin = existingCache.PriorityOrder.FirstOrDefault() as ISkyrimModGetter;
                if (capturing)
                {
                    TraceLookup($"  TryWarmPlugin {modKey.FileName}: plugin missing from " +
                                $"_modLinkCachePlugins; pulled from linkCache.PriorityOrder " +
                                $"(plugin={(plugin?.ModKey.FileName.String ?? "(null)")}).");
                }
                if (plugin != null)
                {
                    _modLinkCachePlugins.TryAdd(modKey, plugin);
                }
            }
        }
        if (plugin == null)
        {
            if (capturing) TraceLookup($"  TryWarmPlugin {modKey.FileName}: no plugin reference available; aborting warmup");
            return false;
        }

        if (capturing)
        {
            TraceLookup($"  TryWarmPlugin {modKey.FileName}: priming Npcs (plugin source={(fromStash ? "stash" : "PriorityOrder fallback")})");
        }

        bool success;
        try
        {
            int count = plugin.Npcs.Count;
            success = true;
            if (capturing) TraceLookup($"  TryWarmPlugin {modKey.FileName}: Npcs.Count={count} (warmup ok)");
        }
        catch (Exception ex)
        {
            if (capturing)
            {
                TraceLookup($"  TryWarmPlugin {modKey.FileName} threw: {ex.GetType().Name}: {ex.Message}");
            }
            success = false;
        }

        if (success)
        {
            // Replace the existing link cache with a fresh one wrapping the
            // now-primed plugin. The original cache may have already walked
            // the plugin in its cold state during an earlier TryResolve and
            // built a partial / corrupt index — priming alone doesn't undo
            // that, so we hand the caller a clean cache to retry against.
            var freshCache = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(plugin, new LinkCachePreferences());
            _modLinkCaches[modKey] = freshCache;
            if (capturing) TraceLookup($"  TryWarmPlugin {modKey.FileName}: rebuilt link cache around primed plugin");
        }

        _warmedPlugins.TryAdd(modKey, success);
        return success;
    }

    private bool TryAddPluginToCaches(ModKey modKey, HashSet<string> fallBackModFolderNames)
    {
        bool capturing = RenderLogCapture.IsCapturing;

        // Resolve the desired source path UP FRONT. PluginProvider checks
        // fallBackModFolderNames first, then the data folder. We use the
        // resolved path to (a) detect a stale cache entry built from a
        // different physical file under the same ModKey and evict it, and
        // (b) decide inside the factory whether to reuse Mutagen's already-
        // parsed LoadOrder instance (only when the desired path IS the data
        // folder path — otherwise the LO instance is the WRONG file).
        bool resolvedDesired = _pluginProvider.TryGetPlugin(
            modKey, fallBackModFolderNames, out var providerPlugin, out var resolvedSourcePath);
        if (resolvedDesired && resolvedSourcePath != null)
        {
            EvictIfSourcePathChanged(modKey, resolvedSourcePath);
        }

        bool wasCached = capturing && _modLinkCaches.ContainsKey(modKey);

        // Use GetOrAdd for an atomic "get or create" operation.
        // The value factory (the second argument) is only executed if the key is not already present.
        bool factoryRan = false;
        bool loBranch = false;
        bool pluginProviderBranch = false;
        bool pluginProviderFailed = false;
        ISkyrimModGetter? loadedPlugin = null;
        var linkCache = _modLinkCaches.GetOrAdd(modKey, key =>
        {
            factoryRan = true;
            if (!resolvedDesired || providerPlugin == null || resolvedSourcePath == null)
            {
                pluginProviderFailed = true;
                return null;
            }

            // Reuse Mutagen's already-parsed LoadOrder instance ONLY when the
            // desired path is the data folder path. If fallBackModFolderNames
            // resolved to a mod folder, we MUST use PluginProvider's instance
            // — the LO instance is parsed from the data folder file and
            // would have different content than the mod-folder file the
            // caller is asking about.
            string dataFolderCandidate = NormalizePath(
                Path.Combine(_environmentStateProvider.DataFolderPath, key.ToString()));
            bool desiredIsDataFolder = string.Equals(
                NormalizePath(resolvedSourcePath), dataFolderCandidate, StringComparison.OrdinalIgnoreCase);

            if (desiredIsDataFolder)
            {
                var modListing = _environmentStateProvider.LoadOrder?.TryGetValue(key);
                if (modListing != null && modListing.Mod != null)
                {
                    loBranch = true;
                    loadedPlugin = modListing.Mod;
                    _modLinkCachePlugins.TryAdd(key, modListing.Mod);
                    _modLinkCacheSourcePaths.TryAdd(key, NormalizePath(resolvedSourcePath));
                    PrimeIfEsl(modListing.Mod);
                    return new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(modListing.Mod, new LinkCachePreferences());
                }
            }

            pluginProviderBranch = true;
            loadedPlugin = providerPlugin;
            _modLinkCachePlugins.TryAdd(key, providerPlugin);
            _modLinkCacheSourcePaths.TryAdd(key, NormalizePath(resolvedSourcePath));
            PrimeIfEsl(providerPlugin);
            return new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(providerPlugin, new LinkCachePreferences());
        });

        if (capturing)
        {
            string folders = fallBackModFolderNames == null
                ? "(null)"
                : "[" + string.Join(", ", fallBackModFolderNames) + "]";
            string outcome;
            if (wasCached)
            {
                outcome = linkCache != null ? "CACHED-OK" : "CACHED-NULL (poisoned)";
            }
            else if (factoryRan)
            {
                if (loBranch) outcome = "LOAD-FROM-LO";
                else if (pluginProviderBranch) outcome = "LOAD-FROM-PROVIDER";
                else if (pluginProviderFailed) outcome = "LOAD-FAILED (provider could not resolve path)";
                else outcome = "LOAD-FACTORY-RAN-UNKNOWN";
            }
            else
            {
                outcome = linkCache != null ? "CACHED-OK (raced)" : "CACHED-NULL (raced, poisoned)";
            }
            string pathLabel = resolvedSourcePath != null ? resolvedSourcePath : "(unresolved)";
            string cachedPathLabel = _modLinkCacheSourcePaths.TryGetValue(modKey, out var cachedPath)
                ? cachedPath
                : "(none)";
            TraceLookup($"TryAddPluginToCaches mk={modKey.FileName} fallback={folders} → {outcome} " +
                        $"desiredPath={pathLabel} cachedPath={cachedPathLabel}");

            // Probe the SAME plugin instance the link cache wraps so we know
            // whether enumeration throws on the very instance the resolver
            // queries — independent of any fresh re-load via TryGetPlugin.
            // Reports masters list, NPC count + sample, and a full exception
            // chain (type/message/stack head) when enumeration throws.
            if (loadedPlugin != null)
            {
                try
                {
                    var masters = loadedPlugin.ModHeader.MasterReferences
                        .Select(m => m.Master.FileName.String).ToList();
                    TraceLookup($"  loadedPlugin masters=[{string.Join(", ", masters)}], flags={loadedPlugin.ModHeader.Flags}");
                }
                catch (Exception ex)
                {
                    TraceLookup($"  loadedPlugin masters=THREW({ex.GetType().Name}: {ex.Message})");
                }

                try
                {
                    int npcCount = loadedPlugin.Npcs.Count;
                    var sample = loadedPlugin.Npcs.Take(8).Select(n => n.FormKey.ToString()).ToList();
                    TraceLookup($"  loadedPlugin Npcs.Count={npcCount}, firstKeys=[{string.Join(", ", sample)}]");
                }
                catch (Exception ex)
                {
                    var exChain = new System.Text.StringBuilder();
                    var cur = ex;
                    int depth = 0;
                    while (cur != null && depth < 4)
                    {
                        exChain.Append("    [").Append(depth).Append("] ").Append(cur.GetType().FullName)
                               .Append(": ").Append(cur.Message).Append('\n');
                        if (!string.IsNullOrEmpty(cur.StackTrace))
                        {
                            var lines = cur.StackTrace.Split('\n');
                            foreach (var line in lines.Take(6))
                            {
                                exChain.Append("        ").Append(line.Trim()).Append('\n');
                            }
                        }
                        cur = cur.InnerException;
                        depth++;
                    }
                    TraceLookup($"  loadedPlugin Npcs enumeration threw:\n{exChain}");
                }
            }
        }

        // The method succeeds if the linkCache is not null (either it existed before or was successfully created).
        return linkCache != null;
    }

    #region Merge In New Records
    
    /// <summary>
    /// Tries to deep copy a FormLink into another FormLink, copying in records and remapping recursivley
    /// If the FormLink target is not contained in modKeysToDuplicateFrom, simply adds the FormLink
    /// </summary>
    /// <param name="targetFormLink">The FormLink to be modified).</param>
    /// <param name="formLinkToCopy">The FormLink to copy.</param>
    /// <param name="modToDuplicateInto">The mod that will contain the modified FromLink data.</param>
    /// /// <param name="modKeysToDuplicateFrom">The mods whose records are eligible to be deep copied in.</param>
    /// /// <param name="rootContextModKey">The mod which is the source override of "formLinkToCopy".</param>
    /// <returns>No return; modification in-place.</returns>
    public List<MajorRecord> DuplicateInOrAddFormLink<TMod>(
        IFormLink<IMajorRecordGetter> targetFormLink,
        IFormLinkGetter<IMajorRecordGetter> formLinkToCopy,
        TMod modToDuplicateInto,
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        ModKey rootContextModKey, 
        bool handleInjectedRecords,
        HashSet<string> fallBackModFolderNames,
        ref List<string> exceptionStrings,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        List<MajorRecord> mergedInRecords = new();
        if (formLinkToCopy.IsNull)
        {
            targetFormLink.SetToNull();
            return mergedInRecords;
        }
        
        if (_currentDuplicateInMappings.TryGetValue(targetFormLink.FormKey, out var remappedFormKey))
        {
            targetFormLink.SetTo(remappedFormKey);
            return mergedInRecords;
        }
        
        if (!modKeysToDuplicateFrom.Contains(formLinkToCopy.FormKey.ModKey) && !handleInjectedRecords)
        {
            if (NpcDiagnosticLogger.IsActive)
                NpcDiagnosticLogger.Log($"  Merge skip: {formLinkToCopy.FormKey} not provided by appearance mod(s) [{string.Join(", ", modKeysToDuplicateFrom)}]; left FormLink unchanged.");
            targetFormLink.SetTo(formLinkToCopy);
            return mergedInRecords;
        }

        if (!TryGetRecordFromMods(formLinkToCopy, modKeysToDuplicateFrom, fallBackModFolderNames, RecordLookupFallBack.None, out var record) || record == null)
        {
            if (NpcDiagnosticLogger.IsActive)
                NpcDiagnosticLogger.Log($"  Merge abort: could not resolve {formLinkToCopy.FormKey} in appearance mod(s); left FormLink unchanged.");
            targetFormLink.SetTo(formLinkToCopy);
            return mergedInRecords;
        }
        
        mergedInRecords = DuplicateFromOnlyReferencedGetters(modToDuplicateInto, record, modKeysToDuplicateFrom, 
            rootContextModKey, false, handleInjectedRecords, fallBackModFolderNames, ref exceptionStrings, typesToInspect);

        if (_currentDuplicateInMappings.ContainsKey(formLinkToCopy.FormKey))
        {
            var deepCopiedFormKey = _currentDuplicateInMappings[formLinkToCopy.FormKey];
            targetFormLink.SetTo(deepCopiedFormKey);
        }
        else
        {
            targetFormLink.SetTo(formLinkToCopy.FormKey);
        }
        
        return mergedInRecords;
    }

    private bool ExplicitRecordCheck(IFormLinkGetter<IMajorRecordGetter> formLinkToCopy,IEnumerable<ModKey> modKeysToDuplicateFrom, HashSet<string> fallBackModFolderNames, out IMajorRecordGetter? recordGetter)
    {
        recordGetter = null;
        // extra check
        foreach (var modKey in modKeysToDuplicateFrom)
        {
            if (TryGetRecordGetterFromMod(formLinkToCopy, modKey, fallBackModFolderNames, RecordLookupFallBack.None,
                    out recordGetter))
            {
                return true;
            }
        }

        return false;
    }

    public List<MajorRecord> DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IEnumerable<IMajorRecordGetter> recordsToDuplicate,
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        ModKey rootContextModKey,
        bool onlySubRecords, 
        bool handleInjectedRecords,
        HashSet<string> fallBackModFolderNames,
        ref List<string> exceptionStrings,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        using var _ = ContextualPerformanceTracer.Trace("RecordHandler.DuplicateFromOnlyReferencedGetters");

        int exceptionCountBefore = exceptionStrings?.Count ?? 0;
        // Snapshot the remap table so we can report each newly-duplicated record's
        // ORIGINAL FormKey (orig -> new), which pinpoints what got pulled in (and
        // from where) when an undesired record — e.g. an NPC via its Template — is merged.
        Dictionary<FormKey, FormKey>? mappingBefore =
            NpcDiagnosticLogger.IsActive ? new Dictionary<FormKey, FormKey>(_currentDuplicateInMappings) : null;

        var result = modToDuplicateInto.DuplicateFromOnlyReferencedGetters<TMod, ISkyrimModGetter>(
            recordsToDuplicate,
            this,
            modKeysToDuplicateFrom,
            onlySubRecords,
            handleInjectedRecords,
            fallBackModFolderNames,
            RecordLookupFallBack.None, // Don't fall back to winning override or origin - if the chain of new records breaks, don't search through overrides
            // Override searching is the job of RecordHandler.DeepGetOverriddenDependencyRecords()
            ref _currentDuplicateInMappings,
            ref _currenTraversedFormLinks,
            ref exceptionStrings,
            typesToInspect);

        // Per-NPC merge-in detail: the concrete set of referenced records pulled
        // into the output plugin for the NPC currently being logged.
        if (NpcDiagnosticLogger.IsActive)
        {
            // Reverse-map (new FormKey -> original FormKey) for entries added by this call.
            var newToOrig = new Dictionary<FormKey, FormKey>();
            if (mappingBefore != null)
            {
                foreach (var kv in _currentDuplicateInMappings)
                {
                    if (!mappingBefore.ContainsKey(kv.Key))
                    {
                        newToOrig[kv.Value] = kv.Key;
                    }
                }
            }

            NpcDiagnosticLogger.Log($"  Merge-in (DuplicateFromOnlyReferencedGetters): copied {result.Count} referenced record(s) from [{string.Join(", ", modKeysToDuplicateFrom)}] (onlySubRecords={onlySubRecords}, handleInjected={handleInjectedRecords}).");
            foreach (var r in result)
            {
                string origin = newToOrig.TryGetValue(r.FormKey, out var orig) ? $" (was {orig})" : string.Empty;
                NpcDiagnosticLogger.Log($"      + [{r.GetType().Name}] {r.EditorID ?? "(no EditorID)"} {r.FormKey}{origin}");
            }
            if (exceptionStrings != null)
            {
                foreach (var ex in exceptionStrings.Skip(exceptionCountBefore))
                {
                    NpcDiagnosticLogger.Log($"      ! merge note: {ex}");
                }
            }
        }

        return result;
    }

    // convenience overload for a single ModKey
    public List<MajorRecord> DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IEnumerable<IMajorRecordGetter> recordsToDuplicate,
        ModKey modKeyToDuplicateFrom,
        bool onlySubRecords, 
        bool handleInjectedRecords,
        HashSet<string> fallBackModFolderNames,
        ref List<string> exceptionStrings,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        return DuplicateFromOnlyReferencedGetters(
            modToDuplicateInto,
            recordsToDuplicate,
            new[] { modKeyToDuplicateFrom },
            modKeyToDuplicateFrom,
            onlySubRecords,
            handleInjectedRecords,
            fallBackModFolderNames,
            ref exceptionStrings,
            typesToInspect);
    }
    
    // convenience overload for a single Record
    public List<MajorRecord> DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IMajorRecordGetter recordToDuplicate,
        IEnumerable<ModKey> modKeysToDuplicateFrom,
        ModKey rootContextModKey,
        bool onlySubRecords,
        bool handleInjectedRecords,
        HashSet<string> fallBackModFolderNames,
        ref List<string> exceptionStrings,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        return DuplicateFromOnlyReferencedGetters(
            modToDuplicateInto,
            new[] { recordToDuplicate },
            modKeysToDuplicateFrom,
            rootContextModKey,
            onlySubRecords,
            handleInjectedRecords,
            fallBackModFolderNames,
            ref exceptionStrings,
            typesToInspect);
    }
    
    // convenience overload for a single Record and ModKey
    public List<MajorRecord> DuplicateFromOnlyReferencedGetters<TMod>(
        TMod modToDuplicateInto,
        IMajorRecordGetter recordToDuplicate,
        ModKey modKeyToDuplicateFrom,
        bool onlySubRecords,
        bool handleInjectedRecords,
        HashSet<string> fallBackModFolderNames,
        ref List<string> exceptionStrings,
        params Type[] typesToInspect)
        where TMod : class, IMod, ISkyrimMod, IModGetter
    {
        return DuplicateFromOnlyReferencedGetters(
            modToDuplicateInto,
            new[] { recordToDuplicate },
            new[] { modKeyToDuplicateFrom },
            modKeyToDuplicateFrom,
            onlySubRecords,
            handleInjectedRecords,
            fallBackModFolderNames,
            ref exceptionStrings,
            typesToInspect);
    }
    #endregion

    #region Collect Overrides of Existing Records
    public HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>
        DeepGetOverriddenDependencyRecords(IMajorRecordGetter majorRecordGetter, List<ModKey> relevantContextKeys, HashSet<FormKey> searchedFormKeys, HashSet<string> fallBackModFolderNames, int maxNestedIntervalDepth, CancellationToken ct)
    {
        return DeepGetOverriddenDependencyRecords(majorRecordGetter.EnumerateFormLinks(), relevantContextKeys,
            searchedFormKeys, fallBackModFolderNames, maxNestedIntervalDepth, ct);
    }

    /// <summary>
    /// Override-discovery variant that traverses an explicit set of FormLinks instead of every
    /// link on a record. SkyPatcher mode uses this to restrict discovery to the NPC's
    /// appearance-descended links (skin, head texture, race, hair color, head parts, outfit) so
    /// non-appearance overrides (packages, factions, items, AI data) are never pulled into the
    /// output plugin as masters.
    /// </summary>
    public HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>
        DeepGetOverriddenDependencyRecords(IEnumerable<IFormLinkGetter> containedFormLinks, List<ModKey> relevantContextKeys, HashSet<FormKey> searchedFormKeys, HashSet<string> fallBackModFolderNames, int maxNestedIntervalDepth, CancellationToken ct)
    {
        using var _ = ContextualPerformanceTracer.Trace("RecordHandler.DeepGetOverriddenDependencyRecords");
        foreach (var modKey in relevantContextKeys)
        {
            TryAddPluginToCaches(modKey, fallBackModFolderNames);
        }
        HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> dependencyContexts = new();
        foreach (var link in containedFormLinks)
        {
            ct.ThrowIfCancellationRequested();
            CollectOverriddenDependencyRecords(link, relevantContextKeys, dependencyContexts, maxNestedIntervalDepth, 0, searchedFormKeys, ct);
        }

        if (NpcDiagnosticLogger.IsActive)
        {
            NpcDiagnosticLogger.Log($"  Override discovery (DeepGetOverriddenDependencyRecords): found {dependencyContexts.Count} overridden dependency record(s) across [{string.Join(", ", relevantContextKeys)}].");
            foreach (var ctx in dependencyContexts)
            {
                NpcDiagnosticLogger.Log($"      * [{ctx.Record.GetType().Name}] {ctx.Record.EditorID ?? "(no EditorID)"} {ctx.Record.FormKey} (override in {ctx.ModKey})");
            }
        }

        return dependencyContexts.ToHashSet();;
    }
    
    /// <summary>
    /// Gets ALL override records from the specified plugins, regardless of NPC traversal.
    /// This is a simpler but less targeted approach compared to DeepGetOverriddenDependencyRecords.
    /// </summary>
    /// <param name="relevantContextKeys">The ModKeys of plugins to search for overrides.</param>
    /// <param name="searchedFormKeys">FormKeys that have already been processed (will be updated).</param>
    /// <param name="fallBackModFolderNames">Fallback folder paths for plugin loading.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A HashSet of all override record contexts found in the specified plugins.</returns>
    public HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>
        GetAllOverriddenDependencyRecords(List<ModKey> relevantContextKeys, HashSet<FormKey> searchedFormKeys, 
            HashSet<string> fallBackModFolderNames, CancellationToken ct)
    {
        using var _ = ContextualPerformanceTracer.Trace("RecordHandler.GetAllOverriddenDependencyRecords");
        
        foreach (var modKey in relevantContextKeys)
        {
            TryAddPluginToCaches(modKey, fallBackModFolderNames);
        }
        
        HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> dependencyContexts = new();
        
        foreach (var modKey in relevantContextKeys)
        {
            ct.ThrowIfCancellationRequested();
            
            if (!_modLinkCaches.TryGetValue(modKey, out var linkCache) || linkCache == null)
            {
                continue;
            }
            
            // Get the plugin to iterate through all its records
            if (!_pluginProvider.TryGetPlugin(modKey, fallBackModFolderNames, out var plugin) || plugin == null)
            {
                continue;
            }
            
            // Iterate through all major records in the plugin
            foreach (var record in plugin.EnumerateMajorRecords())
            {
                ct.ThrowIfCancellationRequested();
                
                // Skip if already processed
                if (searchedFormKeys.Contains(record.FormKey))
                {
                    continue;
                }

                if (record is INpcGetter)
                {
                    continue; // patcher has explicit logic to manually handle NPCs
                }
                
                // Check if this is an override (FormKey's ModKey is NOT one of the appearance mod's plugins)
                if (!relevantContextKeys.Contains(record.FormKey.ModKey))
                {
                    // This is an override record (originates from outside the mod's plugins)
                    searchedFormKeys.Add(record.FormKey);
    
                    try
                    {
                        var context = linkCache.ResolveContext(record.FormKey, record.Registration.GetterType);
                        if (context != null)
                        {
                            dependencyContexts.Add(context);
                        }
                    }
                    catch
                    {
                        // Skip records that can't be resolved to a context
                    }
                }
            }
        }
    
    return dependencyContexts;
}
    
    /// <summary>
    /// Duplicates ALL override records from the specified plugins as new records.
    /// This is a simpler but less targeted approach compared to DuplicateInOverrideRecords.
    /// </summary>
    public HashSet<IMajorRecord>
        DuplicateAllOverrideRecordsAsNew(IMajorRecord rootRecord, List<ModKey> relevantContextKeys, 
            ModKey rootContextKey, ModKey npcSourceModKey, bool handleInjectedRecords,
            HashSet<string> fallBackModFolderNames, ref List<string> exceptionStrings, 
            HashSet<FormKey> searchedFormKeys, CancellationToken ct)
    {
        using var _ = ContextualPerformanceTracer.Trace("RecordHandler.DuplicateAllOverrideRecordsAsNew");
        HashSet<IMajorRecord> mergedInRecords = new();
        
        foreach (var modKey in relevantContextKeys)
        {
            TryAddPluginToCaches(modKey, fallBackModFolderNames);
        }

        Dictionary<FormKey, FormKey> remappedOverrideMap = new();
        
        foreach (var modKey in relevantContextKeys)
        {
            ct.ThrowIfCancellationRequested();
            
            if (!_modLinkCaches.TryGetValue(modKey, out var linkCache) || linkCache == null)
            {
                continue;
            }
            
            if (!_pluginProvider.TryGetPlugin(modKey, fallBackModFolderNames, out var plugin) || plugin == null)
            {
                continue;
            }
            
            foreach (var record in plugin.EnumerateMajorRecords())
            {
                ct.ThrowIfCancellationRequested();
                
                // Skip if already processed
                if (searchedFormKeys.Contains(record.FormKey))
                {
                    continue;
                }
                
                // Check if this is an override (FormKey's ModKey is NOT one of the appearance mod's plugins)
                if (!relevantContextKeys.Contains(record.FormKey.ModKey))
                {
                    searchedFormKeys.Add(record.FormKey);
    
                    // Skip if already mapped
                    if (_currentDuplicateInMappings.ContainsKey(record.FormKey))
                    {
                        remappedOverrideMap.TryAdd(record.FormKey, _currentDuplicateInMappings[record.FormKey]);
                        continue;
                    }
                    
                    try
                    {
                        var context = linkCache.ResolveContext(record.FormKey, record.Registration.GetterType);
                        if (context != null)
                        {
                            var duplicate = context.DuplicateIntoAsNewRecord(_environmentStateProvider.OutputMod);
                            duplicate.EditorID = (duplicate.EditorID ?? "NoEditorID") + "_" + modKey.FileName;
                            _currentDuplicateInMappings.Add(record.FormKey, duplicate.FormKey);
                            remappedOverrideMap.Add(record.FormKey, duplicate.FormKey);
                            mergedInRecords.Add(duplicate);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptionStrings.Add($"Failed to duplicate {record.FormKey}: {ex.Message}");
                    }
                }
            }
        }
        
        // Remap links in all merged records and root record
        foreach (var newRecord in mergedInRecords.And(rootRecord).ToArray())
        {
            newRecord.RemapLinks(remappedOverrideMap);
        }
        
        // Now merge in any new records that the overrides may reference
        var importSourceModKeys = relevantContextKeys
            .Distinct()
            .Where(k => k != npcSourceModKey)
            .ToHashSet();
        var newMergedSubRecords = DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod, 
            mergedInRecords, importSourceModKeys, rootContextKey, true, handleInjectedRecords, 
            fallBackModFolderNames, ref exceptionStrings);
        
        mergedInRecords.UnionWith(newMergedSubRecords);
        
        return mergedInRecords;
    }
    
    private void CollectOverriddenDependencyRecords(IFormLinkGetter formLinkGetter, List<ModKey> relevantContextKeys,
        HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> collectedRecords, int maxNestedIntervalDepth, int currentDepth, HashSet<FormKey> searchedFormKeys, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (formLinkGetter.IsNull)
        {
            return;
        }

        currentDepth++;
        if (currentDepth > maxNestedIntervalDepth) {return;}
        
        if (searchedFormKeys == null)
        {
            searchedFormKeys = new HashSet<FormKey>();
        }
        
        searchedFormKeys.Add(formLinkGetter.FormKey);

        IMajorRecordGetter? modRecord = null;
        
        // try to get the record version in the given mod plugin if possible
        foreach (var modKey in relevantContextKeys)
        {
            if (_modLinkCaches.ContainsKey(modKey) && _modLinkCaches[modKey].TryResolve(formLinkGetter, out modRecord) && modRecord != null)
            {
                var context = _modLinkCaches[modKey].ResolveContext(formLinkGetter);
                currentDepth = 0; // reset the interval search
                if (!relevantContextKeys.Contains(formLinkGetter.FormKey.ModKey)) // this is an override rather than a new record
                {
                    collectedRecords.Add(context);
                }
                break;
            }
        }
        
        // otherwise, traverse the parent record
        if (modRecord is null)
        {
            var parentmod = formLinkGetter.FormKey.ModKey;
            if (!_modLinkCaches.ContainsKey(parentmod))
            {
                var parentListing = _environmentStateProvider.LoadOrder.TryGetValue(parentmod);
                if (parentListing != null && parentListing.Mod != null)
                {
                    _modLinkCachePlugins[parentListing.ModKey] = parentListing.Mod;
                    _modLinkCacheSourcePaths[parentListing.ModKey] = NormalizePath(
                        Path.Combine(_environmentStateProvider.DataFolderPath, parentmod.ToString()));
                    _modLinkCaches[parentListing.ModKey] = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(parentListing.Mod, new LinkCachePreferences());
                }
            }

            if (_modLinkCaches.ContainsKey(parentmod))
            {
                _modLinkCaches[parentmod].TryResolve(formLinkGetter, out modRecord);
            }
        }

        if (modRecord != null)
        {
            var sublinks = modRecord.EnumerateFormLinks();
            foreach (var subLink in sublinks.Where(x => !searchedFormKeys.Contains(x.FormKey)).ToArray())
            {
                CollectOverriddenDependencyRecords(subLink, relevantContextKeys, collectedRecords, maxNestedIntervalDepth, currentDepth, searchedFormKeys, ct);
            }
        }
    }

    #endregion

    #region Merge In Overrides of Existing Records

    public HashSet<IMajorRecord> // return is For Caller's Information only; duplication and remapping happens internally
        DuplicateInOverrideRecords(IMajorRecordGetter majorRecordGetter, IMajorRecord rootRecord, List<ModKey> relevantContextKeys, ModKey rootContextKey, ModKey npcSourceModKey, bool handleInjectedRecords, int maxNestedIntervalDepth, HashSet<string> fallBackModFolderNames, ref List<string> exceptionStrings, HashSet<FormKey> searchedFormKeys, CancellationToken ct)
    {
        using var _ = ContextualPerformanceTracer.Trace("RecordHandler.DuplicateInOverrideRecords");
        HashSet<IMajorRecord> mergedInRecords = new();
        var containedFormLinks = majorRecordGetter.EnumerateFormLinks().ToArray();
        foreach (var modKey in relevantContextKeys)
        {
            TryAddPluginToCaches(modKey, fallBackModFolderNames);
        }

        Dictionary<FormKey, FormKey> remappedOverrideMap = new();
        foreach (var link in containedFormLinks)
        {
            ct.ThrowIfCancellationRequested();
            TraverseAndDuplicateInOverrideRecords(link, relevantContextKeys, _environmentStateProvider.OutputMod, remappedOverrideMap, mergedInRecords, maxNestedIntervalDepth, 0, ref exceptionStrings, searchedFormKeys, ct);
        }
        
        foreach (var newRecord in mergedInRecords.And(rootRecord).ToArray())
        {
            newRecord.RemapLinks(remappedOverrideMap);
        }
        
        // Now go through all merged-in override records and also merge in any new records they may be pointing to
        var importSourceModKeys = relevantContextKeys
            .Distinct()
            .Where(k => k != npcSourceModKey) // don't copy from the mod that defines the NPC, since that is a base mod
            .ToHashSet();
        var newMergedSubRecords = DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod, mergedInRecords, importSourceModKeys, rootContextKey, true, handleInjectedRecords, fallBackModFolderNames, ref exceptionStrings);
        
        mergedInRecords.UnionWith(newMergedSubRecords);
        
        return mergedInRecords;
    }

    private bool TraverseAndDuplicateInOverrideRecords(IFormLinkGetter formLinkGetter, List<ModKey> relevantContextKeys,
        ISkyrimMod outputMod,
        Dictionary<FormKey, FormKey> remappedSubLinks, HashSet<IMajorRecord> mergedInRecords,
        int maxNestedIntervalDepth, int currentDepth, ref List<string> exceptionStrings, HashSet<FormKey> searchedFormKeys, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (formLinkGetter.IsNull)
        {
            return false;
        }

        currentDepth++;
        if (currentDepth > maxNestedIntervalDepth) {return false;}
        
        searchedFormKeys.Add(formLinkGetter.FormKey);

        bool parentRecordShouldBeMergedIn = false;
        bool currentRecordHasBeenMergedIn = false;
        IMajorRecordGetter? traversedModRecord = null;
        IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>? modContext = null;
        
        // try to get the record version in the given mod plugin if possible
        foreach (var modKey in relevantContextKeys)
        {
            if (_modLinkCaches.ContainsKey(modKey) && 
                _modLinkCaches[modKey].TryResolve(formLinkGetter, out traversedModRecord) && 
                traversedModRecord != null)
            {
                modContext = _modLinkCaches[modKey].ResolveContext(formLinkGetter);
                currentDepth = 0; // reset the interval search
                if (!relevantContextKeys.Contains(formLinkGetter.FormKey.ModKey)) // this is an override rather than a new record
                {
                    if (_currentDuplicateInMappings.ContainsKey(formLinkGetter.FormKey))
                    {
                        // This record has already been merged in from a previous function call on a previously processed NPC
                        // add it to remappedSubLinks so that the caller knows to remap it in the current NPC
                        // no need to add it to mergedInRecords because its AssetLinks have already been processed during the previous iteration
                        remappedSubLinks.TryAdd(formLinkGetter.FormKey, _currentDuplicateInMappings[formLinkGetter.FormKey]);
                        return true;
                    }
                    
                    var duplicate = modContext.DuplicateIntoAsNewRecord(outputMod);
                    duplicate.EditorID = (duplicate.EditorID ?? "NoEditorID") + "_" + modKey.FileName;
                    traversedModRecord = duplicate;
                    _currentDuplicateInMappings.Add(formLinkGetter.FormKey, duplicate.FormKey);
                    remappedSubLinks.Add(formLinkGetter.FormKey, duplicate.FormKey);
                    mergedInRecords.Add(duplicate);
                    parentRecordShouldBeMergedIn = true;
                    currentRecordHasBeenMergedIn = true;
                    if (NpcDiagnosticLogger.IsActive)
                        NpcDiagnosticLogger.Log($"  Override merged: {formLinkGetter.FormKey} ({modKey}) -> {duplicate.FormKey} [{duplicate.GetType().Name}] EditorID='{duplicate.EditorID}'.");
                }
                break;
            }
        }
        
        // otherwise, traverse the parent record
        if (traversedModRecord is null)
        {
            var parentmod = formLinkGetter.FormKey.ModKey;
            if (!_modLinkCaches.ContainsKey(parentmod))
            {
                var parentListing = _environmentStateProvider.LoadOrder.TryGetValue(parentmod);
                if (parentListing != null && parentListing.Mod != null)
                {
                    _modLinkCachePlugins[parentListing.ModKey] = parentListing.Mod;
                    _modLinkCacheSourcePaths[parentListing.ModKey] = NormalizePath(
                        Path.Combine(_environmentStateProvider.DataFolderPath, parentmod.ToString()));
                    _modLinkCaches[parentListing.ModKey] = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(parentListing.Mod, new LinkCachePreferences());
                }
            }

            if (_modLinkCaches.ContainsKey(parentmod))
            {
                _modLinkCaches[parentmod].TryResolve(formLinkGetter, out traversedModRecord);
            }
        }
        
        if (traversedModRecord != null)
        {
            var sublinks = traversedModRecord.EnumerateFormLinks().Distinct();
            foreach (var subLink in sublinks.Where(x => !searchedFormKeys.Contains(x.FormKey)).ToArray())
            {
                // don't repeat records that have already been processed
                bool hasCachedSubLink = false;
                if (_currentDuplicateInMappings.ContainsKey(subLink.FormKey))
                {
                    hasCachedSubLink = true;
                    remappedSubLinks.TryAdd(subLink.FormKey, _currentDuplicateInMappings[subLink.FormKey]);
                    parentRecordShouldBeMergedIn = true;
                    break;
                }

                if (hasCachedSubLink)
                {
                    continue;
                }
                
                bool subRecordsAreOverrides = TraverseAndDuplicateInOverrideRecords(subLink, relevantContextKeys, outputMod, remappedSubLinks, mergedInRecords, maxNestedIntervalDepth, currentDepth, ref exceptionStrings, searchedFormKeys, ct);
                if (subRecordsAreOverrides)
                {
                    parentRecordShouldBeMergedIn = true; // merge in this record (even if it's not itself contained in the source mod) because this record's subrecords have been merged in.
                }
            }
            
            if (parentRecordShouldBeMergedIn && 
                !currentRecordHasBeenMergedIn && 
                !remappedSubLinks.ContainsKey(formLinkGetter.FormKey))
            {
                if (Auxilliary.TryDuplicateGenericRecordAsNew(traversedModRecord, outputMod, out var duplicate, out string exceptionString))
                {
                    duplicate.EditorID = (duplicate.EditorID ?? "NoEditorID") + "_" + formLinkGetter.FormKey.ModKey;
                    remappedSubLinks.Add(formLinkGetter.FormKey, duplicate.FormKey);
                    mergedInRecords.Add(duplicate);
                }
                else
                {
                    exceptionStrings.Add(Auxilliary.GetLogString(traversedModRecord, _settings.LocalizationLanguage) + ": " + exceptionString);
                    if (NpcDiagnosticLogger.IsActive)
                        NpcDiagnosticLogger.Log($"  Override merge failed for {formLinkGetter.FormKey}: {exceptionString}");
                }
            }
        }

        return parentRecordShouldBeMergedIn;
    }
    #endregion
    
    #region Misc Functions

    public bool TryGetRecordFromMod(FormKey formKey, Type type, ModKey modKey, HashSet<string> fallBackModFolderNames,  RecordLookupFallBack fallbackMode,
        out dynamic? record)
    {
        using var _ = ContextualPerformanceTracer.Trace("RecordHandler.TryGetRecordFromMod");
        record = null;
        if (_pluginProvider.TryGetPlugin(modKey, fallBackModFolderNames, out var plugin) && plugin != null)
        {
            var group = plugin.TryGetTopLevelGroup(type);
            if (group != null && group.ContainsKey(formKey))
            {
                record = group[formKey];
                return true;
            }
        }
        return false;
    }
    
    public bool TryGetRecordGetterFromMod(IFormLinkGetter formLink, ModKey modKey, HashSet<string> fallBackModFolderNames, RecordLookupFallBack fallbackMode, out IMajorRecordGetter? record)
    {
        bool capturing = RenderLogCapture.IsCapturing;
        if (TryAddPluginToCaches(modKey, fallBackModFolderNames))
        {
            bool resolved = _modLinkCaches[modKey].TryResolve(formLink, out var modRecord) && modRecord is not null;

            // Cold-ESL recovery: if the lookup missed but the FormKey's
            // natural origin IS this plugin (so the plugin should own this
            // record as a new entry, not as an override of a master), the
            // miss may be the Mutagen lazy-parse bug rather than a true
            // absence. Prime the plugin's NPC group via TryWarmPlugin and
            // retry once. Memoized per-ModKey, so this incurs at most one
            // NPC-GRUP walk per buggy plugin per session — and never runs
            // for queries whose ModKey doesn't match the queried plugin
            // (those misses are expected and shouldn't trigger work).
            if (!resolved && formLink.FormKey.ModKey == modKey)
            {
                if (capturing) TraceLookup($"  triggering deferred warmup for {modKey.FileName} (suspicious miss)");
                bool warmed = TryWarmPlugin(modKey);
                if (warmed)
                {
                    bool retryResolved = _modLinkCaches[modKey].TryResolve(formLink, out modRecord) && modRecord is not null;
                    if (capturing) TraceLookup($"  post-warmup retry on {modKey.FileName}: TryResolve={retryResolved}");
                    if (retryResolved && !resolved)
                    {
                        TraceLookup($"  recovered fk={formLink.FormKey} via post-warmup retry on {modKey.FileName}");
                    }
                    resolved = retryResolved;
                }
                else if (capturing)
                {
                    TraceLookup($"  warmup failed for {modKey.FileName}; not retrying TryResolve");
                }
            }
            else if (!resolved && capturing)
            {
                TraceLookup($"  not triggering warmup: formLink.FormKey.ModKey={formLink.FormKey.ModKey.FileName}, modKey={modKey.FileName}, equal={formLink.FormKey.ModKey == modKey}");
            }

            if (capturing)
            {
                int? recordCount = null;
                string? probeError = null;
                List<string>? sampleNpcKeys = null;
                try
                {
                    // Probe NPC count to confirm Mutagen actually parsed the plugin's
                    // contents — a loaded-but-empty link cache would resolve nothing
                    // and look identical to a true "not present" miss otherwise.
                    // Sample a few FormKeys so we can detect ESL key-storage drift
                    // (the lookup FormKey vs. how Mutagen actually keyed the record).
                    if (_pluginProvider.TryGetPlugin(modKey, fallBackModFolderNames, out var probe) && probe != null)
                    {
                        recordCount = probe.Npcs.Count;
                        sampleNpcKeys = probe.Npcs.Take(8).Select(n => n.FormKey.ToString()).ToList();
                    }
                }
                catch (Exception ex)
                {
                    probeError = ex.GetType().Name + ": " + ex.Message;
                }
                string countLabel;
                if (recordCount.HasValue) countLabel = recordCount.Value.ToString();
                else if (probeError != null) countLabel = "THREW(" + probeError + ")";
                else countLabel = "?";
                TraceLookup($"  TryGetRecordGetterFromMod mk={modKey.FileName} fk={formLink.FormKey} → TryResolve={resolved}, npcsInPlugin={countLabel}");
                if (sampleNpcKeys != null && sampleNpcKeys.Count > 0)
                {
                    TraceLookup($"    NPC keys actually in plugin (first {sampleNpcKeys.Count}): [{string.Join(", ", sampleNpcKeys)}]");
                }
            }
            if (resolved)
            {
                record = modRecord;
                return true;
            }
        }
        else if (capturing)
        {
            TraceLookup($"  TryGetRecordGetterFromMod mk={modKey.FileName} fk={formLink.FormKey} → plugin not loadable");
        }

        // fallbacks
        IMajorRecordGetter? fallbackRecord = null;
        switch (fallbackMode)
        {
            case RecordLookupFallBack.Origin:
                if (TryAddPluginToCaches(formLink.FormKey.ModKey, fallBackModFolderNames) && _modLinkCaches[formLink.FormKey.ModKey].TryResolve(formLink, out fallbackRecord) && fallbackRecord is not null)
                {
                    if (capturing) TraceLookup($"  TryGetRecordGetterFromMod[Origin] mk={formLink.FormKey.ModKey.FileName} fk={formLink.FormKey} → resolved");
                    record = fallbackRecord;
                    return true;
                }
                break;

            case RecordLookupFallBack.Winner:
                if (_environmentStateProvider.LinkCache.TryResolve(formLink, out fallbackRecord) && fallbackRecord is not null)
                {
                    if (capturing) TraceLookup($"  TryGetRecordGetterFromMod[Winner] fk={formLink.FormKey} → resolved via global LinkCache");
                    record = fallbackRecord;
                    return true;
                }
                if (capturing) TraceLookup($"  TryGetRecordGetterFromMod[Winner] fk={formLink.FormKey} → global LinkCache miss");
                break;

            case RecordLookupFallBack.None:
                default:
                    break;
        }

        record = null;
        return false;
    }

    public bool TryGetRecordFromMods(IFormLinkGetter formLink, IEnumerable<ModKey> modKeys, HashSet<string> fallBackModFolderNames, RecordLookupFallBack fallbackMode,
        out IMajorRecordGetter? record, bool reverseOrder = true)
    {
        record = null;
        if (modKeys == null || formLink.IsNull)
        {
            return false;
        }

        var toSearch = modKeys.Reverse().ToArray();
        if (!reverseOrder)
        {
            toSearch = modKeys.ToArray();
        }

        bool capturing = RenderLogCapture.IsCapturing;
        if (capturing)
        {
            TraceLookup($"TryGetRecordFromMods fk={formLink.FormKey} fallback={fallbackMode} reverseOrder={reverseOrder} keys=[{string.Join(", ", toSearch.Select(k => k.FileName.String))}]");
        }

        foreach (var mk in toSearch)
        {
            if (TryGetRecordGetterFromMod(formLink, mk, fallBackModFolderNames, RecordLookupFallBack.None, out record) && record != null)
            {
                if (capturing) TraceLookup($"  → MATCHED via {mk.FileName}");
                return true;
            }
        }

        // fallbacks
        switch (fallbackMode)
        {
            case RecordLookupFallBack.Origin:
                if (TryGetRecordGetterFromMod(formLink, formLink.FormKey.ModKey, fallBackModFolderNames, RecordLookupFallBack.None,  out record) && record != null)
                {
                    if (capturing) TraceLookup($"  → MATCHED via Origin fallback {formLink.FormKey.ModKey.FileName}");
                    return true;
                }
                if (capturing) TraceLookup("  → Origin fallback miss");
                break;

            case RecordLookupFallBack.Winner:
                if (_environmentStateProvider.LinkCache.TryResolve(formLink, out record) && record is not null)
                {
                    if (capturing) TraceLookup("  → MATCHED via Winner fallback (global LinkCache)");
                    return true;
                }
                if (capturing) TraceLookup("  → Winner fallback miss (global LinkCache)");
                break;

            case RecordLookupFallBack.None:
            default:
                break;
        }

        if (capturing) TraceLookup($"TryGetRecordFromMods fk={formLink.FormKey} → MISS");
        return false;
    }

    public enum RecordLookupFallBack
    {
        None,
        Origin,
        Winner
    }
    
    public string GetStatusReport()
    {
        if (!_modLinkCaches.Any())
        {
            return "No plugins link caches currently created.";
        }
        else
        {
            return "Link caches for plugins: " + Environment.NewLine + string.Join(Environment.NewLine, _modLinkCaches.Select(x => "\t" + x.Key.ToString()));
        }
    }
    #endregion
}