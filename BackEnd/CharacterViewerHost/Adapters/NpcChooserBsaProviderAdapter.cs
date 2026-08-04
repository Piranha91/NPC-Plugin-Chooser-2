using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CharacterViewer.Rendering;
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
public sealed class NpcChooserBsaProviderAdapter : IBsaArchiveProvider
{
    private readonly BsaHandler _bsa;
    private readonly Settings _settings;
    private readonly EnvironmentStateProvider _env;
    private readonly object _ensureLock = new();
    private volatile bool _allOpened;

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
            foreach (var ms in _settings.ModSettings)
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

            // Dump the full BSA-path inventory once so subsequent lookup traces
            // can be correlated against it. The set is invariant after
            // EnsureAllArchivesOpened completes (no further BSAs get indexed
            // during a session), so logging once is sufficient — the user can
            // scroll back to this block to see exactly which archives every
            // TryLocateInBsa call scans.
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
    /// <para><see cref="EnsureAllArchivesOpened"/> latches for the whole session
    /// and its comment calls the indexed set invariant — true for the startup
    /// walk, but a mod folder the user ADDS mid-session (Mods tab → Add folder)
    /// brings archives that walk never saw, so a forced re-render would keep
    /// missing their assets until the next launch. This is the targeted escape
    /// hatch: <see cref="BsaHandler.AddMissingModToCache"/> merges per archive
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
    /// Broadcast lookup across every indexed archive, resolved by LOAD ORDER
    /// rather than by whichever archive happened to be indexed first.
    ///
    /// <para>Candidates are ranked in two tiers. <b>Tier 1</b> — the owning
    /// plugin is in the load order: the LATEST-loading one wins, matching the
    /// conventional "later archive wins" rule and staying consistent with how
    /// records and the renderer's own scope chain resolve conflicts elsewhere.
    /// <b>Tier 2</b> — the owning plugin is not in the load order (an NPC2 mod
    /// the user has not enabled): load order cannot rank these, so they only
    /// apply when no tier-1 candidate exists, preserving the historical
    /// first-indexed behavior for callers whose mod isn't active. Dropping
    /// tier 2 outright would regress those.</para>
    ///
    /// <para>Callers that need a specific mod's copy should not be here at all
    /// — the renderer's strict scope chain (<see cref="TryLocateInScopedBsa"/>)
    /// answers that question and runs first.</para>
    /// </summary>
    public bool TryLocateInBsa(string subpath, out string? containingBsaPath)
    {
        containingBsaPath = null;
        var keys = _bsa.GetIndexedModKeys();
        if (keys.Count == 0)
        {
            Trace($"TryLocateInBsa: NO INDEXED BSA KEYS — file=[{subpath}]");
            return false;
        }

        var candidates = _bsa.LocateAllInBsas(subpath);
        if (candidates.Count == 0)
        {
            // Renderer fell through here after vanilla loose + mod-folder
            // (AdditionalDataFolders) checks both failed — definitive
            // "file not in any indexed BSA." The full BSA-path inventory was
            // dumped once at the end of EnsureAllArchivesOpened; correlate
            // this miss against that block to verify the expected archive is
            // actually indexed.
            Trace($"TryLocateInBsa: MISS — file=[{subpath}] (scanned {_bsa.GetIndexedBsaPaths().Count} indexed BSA file(s) across {keys.Count} mod key(s); see EnsureAllArchivesOpened inventory)");
            return false;
        }

        var best = SelectByLoadOrder(candidates, _env.LoadOrderModKeys);
        if (best != null)
        {
            string winner = best.Value.BsaPath;
            containingBsaPath = winner;
            // Log the exact BSA file path the lookup resolved to, plus the
            // field it beat, so a surprising winner can be checked against the
            // load order without re-running.
            Trace($"TryLocateInBsa: HIT (load order #{best.Value.LoadOrderIndex}, {candidates.Count} candidate(s)) — " +
                  $"file=[{subpath}] in [{winner}] modKey=[{best.Value.ModKey.FileName}]" +
                  (candidates.Count > 1
                      ? $"; also in [{string.Join(" | ", candidates.Where(c => c.BsaPath != winner).Select(c => c.BsaPath))}]"
                      : string.Empty));
            return true;
        }

        // Tier 2: nothing in the load order carries it. Fall back to the first
        // indexed match so an inactive-but-indexed mod still resolves the way
        // it always did.
        containingBsaPath = candidates[0].BsaPath;
        Trace($"TryLocateInBsa: HIT (no load-order candidate; first of {candidates.Count} indexed) — " +
              $"file=[{subpath}] in [{containingBsaPath}] modKey=[{candidates[0].ModKey.FileName}]");
        return true;
    }

    /// <summary>
    /// Tier-1 selection for <see cref="TryLocateInBsa"/>: of the archives holding
    /// the file, the one whose owning plugin loads LATEST. Candidates whose plugin
    /// is absent from <paramref name="loadOrder"/> are not ranked here — load order
    /// says nothing about them — and yield null when they are the only ones, which
    /// is the caller's signal to fall back.
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
        }
        error = ok ? null : extractError;
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
