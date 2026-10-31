using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SkyRender;
using SkyRender.Character;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;

/// <summary>
/// Adapts NPC2's <see cref="BsaHandler"/> to <see cref="IBsaArchiveProvider"/>.
/// The renderer's asset resolver doesn't know which mod a missing texture / mesh
/// belongs to, so this adapter performs broadcast lookups across every loaded
/// archive. <see cref="EnsureAllArchivesOpened"/> walks <see cref="Settings.ModSettings"/>
/// once and pre-warms the BSA reader cache so subsequent extractions are
/// O(1) reader-cache hits.
///
/// <para>Mod-folder loose-file priority is handled by the renderer itself
/// since CharacterViewer.Rendering 1.1.0 — hosts pass
/// <c>OffscreenRenderRequest.AdditionalDataFolders</c> /
/// <c>VM_CharacterViewer.AdditionalDataFolders</c>, and the renderer's
/// <c>GameAssetResolver</c> consults those before vanilla. This adapter
/// stays focused on real BSA lookups.</para>
/// </summary>
public sealed class NpcChooserBsaProviderAdapter : IBsaArchiveProvider, IBsaArchiveByteReader
{
    private readonly BsaHandler _bsa;
    private readonly Settings _settings;
    private readonly EnvironmentStateProvider _env;
    private readonly object _ensureLock = new();
    private volatile bool _allOpened;
    private volatile bool _loadOrderWidened;

    public NpcChooserBsaProviderAdapter(BsaHandler bsa, Settings settings, EnvironmentStateProvider env)
    {
        _bsa = bsa;
        _settings = settings;
        _env = env;
    }

    public void EnsureAllArchivesOpened()
    {
        if (_allOpened) return;
        lock (_ensureLock)
        {
            if (_allOpened) return;

            var sw = Stopwatch.StartNew();
            int tid = Environment.CurrentManagedThreadId;
            int total = _settings.ModSettings.Count;
            string baseGameSummary;
            try
            {
                var bg = _settings.ModSettings.FirstOrDefault(m => m.DisplayName == "Base Game");
                if (bg == null)
                {
                    baseGameSummary = "(no Base Game entry in _settings.ModSettings)";
                }
                else
                {
                    baseGameSummary = $"BaseGame keys=[{string.Join(",", bg.CorrespondingModKeys.Select(k => k.FileName.String))}] folders=[{string.Join("|", bg.CorrespondingFolderPaths)}]";
                }
            }
            catch (Exception ex) { baseGameSummary = $"(summary failed: {ex.Message})"; }
            Trace($"ENTER tid={tid} mods={total} envStatus={_env.Status} envDataFolderPath=[{_env.DataFolderPath}] {baseGameSummary}");

            // Empty-model safety net. The startup pre-warm at App.xaml.cs is
            // fired right after VM_Settings.InitializeAsync and ordinarily
            // sees a populated Settings.ModSettings (either deserialized from
            // Settings.json on a normal launch, or just synced from VM_Mods
            // by the fix-A call inserted there for fresh installs). If we
            // ever do get here with zero mods anyway — a future caller fires
            // EnsureAllArchivesOpened too early, env-invalid early-returns
            // strand the model empty, etc. — DO NOT latch _allOpened=true.
            // Latching would lock out every later call (each gated on
            // _allOpened) from doing the real indexing once Settings.ModSettings
            // gets populated, producing the silent "no BSAs indexed all
            // session, mugshots empty" failure that this whole investigation
            // was chasing. Bailing without latching lets the next call retry.
            if (total == 0)
            {
                Trace($"EXIT tid={tid} mods=0 — bailing without latching _allOpened so a later call can retry once Settings.ModSettings is populated");
                return;
            }

            var release = _env.SkyrimVersion.ToGameRelease();
            int i = 0;
            // Snapshot: this runs on a background pre-warm task while the UI
            // thread may still be mutating Settings.ModSettings (startup sync,
            // user edits). Iterating the live list intermittently died with
            // "Collection was modified", which skipped the latch and pushed
            // this whole walk into the first mugshot render instead.
            foreach (var ms in _settings.ModSettings.ToList())
            {
                i++;
                long modStart = sw.ElapsedMilliseconds;
                _bsa.AddMissingModToCache(ms, release).GetAwaiter().GetResult();
                _bsa.OpenBsaReadersFor(ms, release);
                long modElapsed = sw.ElapsedMilliseconds - modStart;
                if (modElapsed > 50)
                {
                    Trace($"  slow-mod tid={tid} [{i}/{total}] '{ms.DisplayName}' elapsed={modElapsed}ms");
                }
            }
            _allOpened = true;
            Trace($"EXIT tid={tid} mods={total} totalElapsed={sw.ElapsedMilliseconds}ms");

            // Dump the full BSA-path inventory so subsequent lookup traces can be
            // correlated against it: the user can scroll back to this block to see
            // which archives a TryLocateInBsa call scans.
            //
            // This is the STARTUP baseline, not the final set. Three paths widen the
            // index later in the session, each logging its own additions:
            // RefreshArchivesForMod (a folder added to a mod mid-session),
            // BsaHandler.EnsureDataFolderArchivesIndexed (record-scoped widening for
            // an outfit whose donor plugin has no ModSetting), and the same method
            // fed the whole enabled load order by NpcMeshResolver's unreachable-attire
            // fallback (archives owned by a dummy-loader plugin no record names). A
            // miss logged below must be read against this block PLUS any such lines
            // that precede it.
            var bsaPaths = _bsa.GetIndexedBsaPaths();
            Trace($"Indexed BSA inventory ({bsaPaths.Count} archive(s)):");
            foreach (var bsaPath in bsaPaths)
            {
                Trace($"  {bsaPath}");
            }
        }
    }

    /// <summary>
    /// Re-scans ONE mod's folders and indexes any BSA that wasn't indexed yet.
    /// <para><see cref="EnsureAllArchivesOpened"/> latches for the whole session,
    /// which covers the startup walk — but a mod folder the user ADDS mid-session
    /// (Mods tab → Add folder) brings archives that walk never saw, so a forced
    /// re-render would keep missing their assets until the next launch. This is the
    /// targeted escape hatch: <see cref="BsaHandler.AddMissingModToCache"/> merges per archive
    /// path and filters out already-indexed archives, so a mod with nothing new
    /// costs one directory scan and no archive I/O. Deliberately does NOT clear
    /// <c>_allOpened</c> — a full re-walk is far more expensive and the startup
    /// walk's results stay valid.</para>
    /// <para>The reader open re-bumps refcounts for archives already cached. The
    /// adapter holds its readers for the session and never releases, so the
    /// inflated count changes nothing; it only makes premature disposal even
    /// less possible.</para>
    /// </summary>
    public void RefreshArchivesForMod(ModSetting? modSetting)
    {
        if (modSetting == null) return;
        lock (_ensureLock)
        {
            try
            {
                var release = _env.SkyrimVersion.ToGameRelease();
                var sw = Stopwatch.StartNew();
                _bsa.AddMissingModToCache(modSetting, release).GetAwaiter().GetResult();
                _bsa.OpenBsaReadersFor(modSetting, release);
                Trace($"RefreshArchivesForMod '{modSetting.DisplayName}' folders=[{string.Join("|", modSetting.CorrespondingFolderPaths)}] elapsed={sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                // Never let an index refresh take down the render that asked for
                // it — worst case the render resolves what was already indexed.
                Trace($"RefreshArchivesForMod '{modSetting.DisplayName}' FAILED: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Broadcast lookup across the archives the GAME will actually load: those
    /// owned by an ENABLED load-order plugin, latest-loading plugin winning.
    ///
    /// <para>This is the renderer's out-of-scope tier, and the rule for it is
    /// the data folder's rule: an asset outside the depicted mod's own folders
    /// is legitimate only if the user's live setup delivers it — i.e. its
    /// archive is loaded by an enabled plugin. Candidates owned by a plugin
    /// that is disabled or absent from the load order are never returned (the
    /// game would not load their archives either); a mod's OWN folder archives
    /// are the strict scope chain's job (<see cref="TryLocateInScopedBsa"/>),
    /// which runs first and is unaffected.</para>
    ///
    /// <para>On a miss, the index is widened ONCE per session to the full
    /// enabled load order (<see cref="TryWidenToEnabledLoadOrder"/>) and the
    /// lookup retried — the startup walk only indexes ModSettings' archives,
    /// so the first out-of-scope asset pays the widening cost and every
    /// later lookup sees the complete index.</para>
    /// </summary>
    public bool TryLocateInBsa(string subpath, out string? containingBsaPath)
    {
        long t0 = Stopwatch.GetTimestamp();
        bool hit = false;
        try
        {
            containingBsaPath = null;

            var ranking = GetEnabledRanking();
            if (ranking.KeysAscending.Count == 0)
            {
                Trace($"TryLocateInBsa: NO ENABLED LOAD-ORDER PLUGINS (environment not resolved?) — file=[{subpath}]");
                return false;
            }

            if (TryLocateAmongEnabled(subpath, ranking, out containingBsaPath)) return hit = true;

            // First miss: make sure every enabled plugin's data-folder archives are
            // actually in the index, then retry. Memoized — later misses are final.
            if (TryWidenToEnabledLoadOrder(ranking.KeysAscending) &&
                TryLocateAmongEnabled(subpath, ranking, out containingBsaPath))
            {
                return hit = true;
            }

            // Definitive miss. The full BSA-path inventory was dumped at the end of
            // EnsureAllArchivesOpened, and the widen above logged its additions;
            // correlate this miss against those blocks to verify the expected
            // archive is actually indexed.
            Trace($"TryLocateInBsa: MISS — file=[{subpath}] (scanned {_bsa.GetIndexedBsaPaths().Count} indexed BSA file(s) across {_bsa.GetIndexedModKeys().Count} mod key(s); see EnsureAllArchivesOpened inventory)");
            return false;
        }
        finally
        {
            BroadcastLookupStats.RecordLookup(Stopwatch.GetTimestamp() - t0, hit);
        }
    }

    /// <summary>One ranked pass over the current index: all archives holding
    /// <paramref name="subpath"/>, filtered+ranked by the enabled load order.</summary>
    private bool TryLocateAmongEnabled(string subpath, EnabledRanking ranking, out string? containingBsaPath)
    {
        containingBsaPath = null;
        var candidates = _bsa.LocateAllInBsas(subpath);
        if (candidates.Count == 0) return false;

        var best = SelectByLoadOrderRanked(candidates, ranking.Rank);
        if (best == null)
        {
            // Present in the index, but only in archives no enabled plugin
            // loads (e.g. a disabled mod's folder BSA). The game wouldn't see
            // those bytes, so neither do we.
            Trace($"TryLocateInBsa: {candidates.Count} candidate(s) for file=[{subpath}] but none owned by an enabled load-order plugin — treating as miss " +
                  (BsaContentsDiag.IsEnabled ? $"([{string.Join(" | ", candidates.Select(c => c.BsaPath))}])" : "(enable LogBsaDiag.txt for the candidate list)"));
            return false;
        }

        string winner = best.Value.BsaPath;
        containingBsaPath = winner;
        // Log the exact BSA file path the lookup resolved to, plus the field
        // it beat, so a surprising winner can be checked against the load
        // order without re-running. The candidate join is diag-gated: it is
        // per-lookup string work on the render hot path.
        Trace($"TryLocateInBsa: HIT (load order #{best.Value.LoadOrderIndex}, {candidates.Count} candidate(s)) — " +
              $"file=[{subpath}] in [{winner}] modKey=[{best.Value.ModKey.FileName}]" +
              (candidates.Count > 1 && BsaContentsDiag.IsEnabled
                  ? $"; also in [{string.Join(" | ", candidates.Where(c => c.BsaPath != winner).Select(c => c.BsaPath))}]"
                  : string.Empty));
        return true;
    }

    /// <summary>Immutable enabled-load-order snapshot: keys ascending + a
    /// ModKey→position rank map. Built once per resolved load order instead of
    /// per broadcast lookup — with a ~1000-plugin load order the per-call
    /// enumeration + dictionary build dominated the lookup itself.</summary>
    private sealed record EnabledRanking(
        object LoadOrderIdentity,
        IReadOnlyList<ModKey> KeysAscending,
        IReadOnlyDictionary<ModKey, int> Rank);

    private volatile EnabledRanking? _enabledRanking;

    /// <summary>Returns the cached ranking, rebuilding when the environment
    /// re-resolved (the LoadOrder object's identity changes). Never caches an
    /// EMPTY load order — an early call during startup must not pin
    /// "no plugins" for the session.</summary>
    private EnabledRanking GetEnabledRanking()
    {
        var loadOrder = _env.LoadOrder;
        if (loadOrder == null)
        {
            return new EnabledRanking(this, Array.Empty<ModKey>(),
                new Dictionary<ModKey, int>());
        }

        var cached = _enabledRanking;
        if (cached != null && ReferenceEquals(cached.LoadOrderIdentity, loadOrder)) return cached;

        var keys = loadOrder.ListedOrder.Where(l => l.Enabled).Select(l => l.ModKey).ToList();
        var rank = new Dictionary<ModKey, int>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            // First listing wins if a key somehow repeats.
            rank.TryAdd(keys[i], i);
        }

        var built = new EnabledRanking(loadOrder, keys, rank);
        if (keys.Count > 0) _enabledRanking = built;
        return built;
    }

    /// <summary>
    /// Once-per-session index widening to the full enabled load order, the
    /// broadcast tier's counterpart of NpcMeshResolver's unreachable-attire
    /// sweep (which remains as a pre-warm for outfit renders). Keys the index
    /// has already seen are dropped — their archives are already reachable, and
    /// re-indexing them from the Data folder would open a duplicate reader on
    /// the same physical BSA through its VFS path. Returns true only when it
    /// actually indexed something new this call (the caller's cue to retry its
    /// lookup). Does not latch on an empty load order, so an early call during
    /// an unresolved environment can retry later.
    /// </summary>
    /// <summary>
    /// Startup counterpart of the lazy first-miss widen: indexes the full
    /// enabled load order's data-folder archives so no RENDER ever pays that
    /// cost in-band (on large load orders it is seconds of BSA file-table
    /// reads). Called from App startup's background pre-warm after
    /// <see cref="EnsureAllArchivesOpened"/>; a no-op once latched, and safe
    /// to skip — the lazy path in <see cref="TryLocateInBsa"/> remains the
    /// correctness backstop.
    /// </summary>
    public void PrewarmEnabledLoadOrderArchives()
    {
        var ranking = GetEnabledRanking();
        if (ranking.KeysAscending.Count == 0) return;
        TryWidenToEnabledLoadOrder(ranking.KeysAscending);
    }

    private bool TryWidenToEnabledLoadOrder(IReadOnlyList<ModKey> enabledKeys)
    {
        if (_loadOrderWidened) return false;
        // Harness A/B: legacy mode never widened on a broadcast miss.
        if (RenderResolutionMode.ForceLegacy) return false;
        lock (_ensureLock)
        {
            if (_loadOrderWidened) return false;
            if (enabledKeys.Count == 0) return false;
            try
            {
                var keys = NpcMeshResolver.SelectEnabledLoadOrderPluginsToIndex(
                        enabledKeys.Select(k => (k, true)),
                        _env.BaseGamePlugins,
                        _env.CreationClubPlugins)
                    .Where(k => !_bsa.CacheContainsModKey(k))
                    .ToList();
                Trace($"TryWidenToEnabledLoadOrder: indexing data-folder archives for {keys.Count} enabled plugin(s) not yet in the index");
                if (keys.Count > 0)
                {
                    var sw = Stopwatch.StartNew();
                    _bsa.EnsureDataFolderArchivesIndexed(keys, _env.SkyrimVersion.ToGameRelease());
                    Trace($"TryWidenToEnabledLoadOrder: done in {sw.ElapsedMilliseconds}ms");
                    BroadcastLookupStats.RecordWiden(sw.ElapsedMilliseconds);
                }
                _loadOrderWidened = true;
                return keys.Count > 0;
            }
            catch (Exception ex)
            {
                // Latch anyway: a failing widen would otherwise re-run (and
                // re-fail) on every subsequent broadcast miss this session.
                Trace($"TryWidenToEnabledLoadOrder FAILED: {ex.Message}");
                _loadOrderWidened = true;
                return false;
            }
        }
    }

    /// <summary>
    /// Ranked selection for <see cref="TryLocateInBsa"/>: of the archives holding
    /// the file, the one whose owning plugin loads LATEST. Candidates whose plugin
    /// is absent from <paramref name="loadOrder"/> are not ranked — load order
    /// says nothing about them — and yield null when they are the only ones,
    /// which the broadcast tier treats as a miss (the caller passes ENABLED
    /// plugins only, and an archive no enabled plugin loads is invisible to
    /// the game).
    ///
    /// <para><paramref name="loadOrder"/> is expected in ascending (ListedOrder)
    /// form, earliest plugin first.</para>
    ///
    /// <para>Pure and static so the precedence rule is testable without a game
    /// install, a BSA index, or a resolved environment.</para>
    /// </summary>
    public static (ModKey ModKey, string BsaPath, int LoadOrderIndex)? SelectByLoadOrder(
        IReadOnlyList<(ModKey ModKey, string BsaPath)> candidates,
        IEnumerable<ModKey> loadOrder)
    {
        if (candidates == null || candidates.Count == 0 || loadOrder == null) return null;

        var loadOrderIndex = new Dictionary<ModKey, int>();
        int i = 0;
        foreach (var mk in loadOrder)
        {
            // First listing wins if a key somehow repeats — the position a
            // duplicate would occupy is not meaningful either way.
            if (!loadOrderIndex.ContainsKey(mk)) loadOrderIndex[mk] = i;
            i++;
        }

        return SelectByLoadOrderRanked(candidates, loadOrderIndex);
    }

    /// <summary>Ranked core of <see cref="SelectByLoadOrder"/>, taking a
    /// prebuilt ModKey→position map so hot-path callers (the broadcast tier)
    /// don't rebuild the map per lookup.</summary>
    public static (ModKey ModKey, string BsaPath, int LoadOrderIndex)? SelectByLoadOrderRanked(
        IReadOnlyList<(ModKey ModKey, string BsaPath)> candidates,
        IReadOnlyDictionary<ModKey, int> loadOrderIndex)
    {
        if (candidates == null || candidates.Count == 0 || loadOrderIndex == null) return null;

        (ModKey ModKey, string BsaPath, int LoadOrderIndex)? best = null;
        foreach (var candidate in candidates)
        {
            if (!loadOrderIndex.TryGetValue(candidate.ModKey, out int idx)) continue;
            // Strictly greater: among archives owned by the SAME plugin, the
            // first enumerated stays the winner. Load order cannot separate
            // them, so there is nothing to prefer.
            if (best == null || idx > best.Value.LoadOrderIndex)
            {
                best = (candidate.ModKey, candidate.BsaPath, idx);
            }
        }

        return best;
    }

    public bool TryExtractToDisk(string containingBsaPath, string subpath, string destPath, out string? error)
    {
        // Extract from the EXACT BSA the caller specified — never re-broadcast.
        // The previous broadcast version silently leaked vanilla content into
        // mod-scoped renders when both shipped the same relative path: the
        // renderer's strict scope chain would correctly identify (e.g.) FF's
        // BSA as the source via TryLocateInScopedBsa, but the broadcast extract
        // would then pull the file from whichever BSA the index happened to
        // hit first (vanilla, since it's always indexed early). Keying the
        // resolver's extraction cache per source-BSA didn't help because the
        // cache stored the wrong content.
        if (string.IsNullOrEmpty(containingBsaPath))
        {
            error = "empty containingBsaPath";
            Trace($"TryExtractToDisk: REJECTED — empty containingBsaPath, file=[{subpath}] dest=[{destPath}]");
            return false;
        }
        var (ok, extractError) = _bsa.ExtractFileAsync(containingBsaPath, subpath, destPath).GetAwaiter().GetResult();
        if (!ok)
        {
            Trace($"TryExtractToDisk: FAILED — file=[{subpath}] from bsa=[{containingBsaPath}] dest=[{destPath}] :: {extractError}");
            // The renderer would drop this asset and still return a PNG; the
            // guard makes the render discard it and tells the user why.
            if (extractError != null && extractError.StartsWith(BsaHandler.DiskFullPrefix, StringComparison.Ordinal))
            {
                DiskSpaceGuard.ReportDiskFull(destPath);
            }
        }
        else
        {
            BsaAssetHashDiag.LogFile("extract", containingBsaPath, subpath, destPath);
        }
        error = ok ? null : extractError;
        return ok;
    }

    /// <summary>
    /// In-memory counterpart of <see cref="TryExtractToDisk"/>: reads the file from the EXACT
    /// BSA the caller located, for the same reason (never re-broadcast). Writes nothing, so
    /// there is no disk-full case to report. Called concurrently by the renderer's resolver.
    /// </summary>
    public bool TryReadBytes(string containingBsaPath, string subpath, out byte[]? bytes, out string? error)
    {
        bytes = null;
        if (string.IsNullOrEmpty(containingBsaPath))
        {
            error = "empty containingBsaPath";
            Trace($"TryReadBytes: REJECTED — empty containingBsaPath, file=[{subpath}]");
            return false;
        }
        bool ok = _bsa.TryReadFileBytes(containingBsaPath, subpath, out bytes, out error);
        if (!ok)
        {
            Trace($"TryReadBytes: FAILED — file=[{subpath}] from bsa=[{containingBsaPath}] :: {error}");
        }
        else if (bytes != null)
        {
            BsaAssetHashDiag.LogBytes("memory", containingBsaPath, subpath, bytes);
        }
        return ok;
    }

    public bool TryLocateInScopedBsa(
        string subpath,
        string folderPath,
        IReadOnlyList<string> modKeyFileNames,
        out string? containingBsaPath)
    {
        containingBsaPath = null;
        if (string.IsNullOrEmpty(folderPath) || modKeyFileNames == null || modKeyFileNames.Count == 0)
        {
            return false;
        }

        // Iterate the scope's plugin filenames; for each, ask BsaHandler
        // whether the file exists in any BSA owned by that ModKey AND
        // located at folderPath. First hit wins. This mirrors the user-spec
        // step "Does it have a BSA file associated with any of the
        // CorrespondingModKeys? If so, does it contain the given file?"
        foreach (var keyName in modKeyFileNames)
        {
            if (string.IsNullOrEmpty(keyName)) continue;
            if (!ModKey.TryFromNameAndExtension(keyName, out var modKey)) continue;
            if (_bsa.FileExistsInArchiveAtFolder(subpath, modKey, folderPath, out var bsaPath) &&
                bsaPath != null)
            {
                containingBsaPath = bsaPath;
                Trace($"TryLocateInScopedBsa: HIT — file=[{subpath}] folder=[{folderPath}] modKey=[{keyName}] bsa=[{bsaPath}]");
                return true;
            }
        }

        Trace($"TryLocateInScopedBsa: MISS — file=[{subpath}] folder=[{folderPath}] keys=[{string.Join(",", modKeyFileNames)}]");
        return false;
    }

    private static void Trace(string message)
    {
        Debug.WriteLine($"[BsaAdapter] {message}");
        System.Diagnostics.Trace.WriteLine($"[BsaAdapter] {message}");
        // Mirror into the BSA contents diagnostic so adapter-level events
        // (ENTER/EXIT of EnsureAllArchivesOpened, per-mod elapsed timings,
        // TryLocateInBsa/TryLocateInScopedBsa hits and misses) sit on the
        // same timeline as the _bsaContents Add/Skip lines from BsaHandler.
        BsaContentsDiag.Log($"[BsaAdapter] {message}");
    }
}
