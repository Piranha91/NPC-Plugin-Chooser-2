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

    public bool TryLocateInBsa(string subpath, out string? containingBsaPath)
    {
        containingBsaPath = null;
        var keys = _bsa.GetIndexedModKeys();
        if (keys.Count == 0)
        {
            Trace($"TryLocateInBsa: NO INDEXED BSA KEYS — file=[{subpath}]");
            return false;
        }
        bool hit = _bsa.FileExists(subpath, keys, out _, out containingBsaPath);
        if (hit)
        {
            // Log the exact BSA file path the lookup resolved to. Useful for
            // confirming the right archive is being consulted (e.g. mod's
            // BSA vs. vanilla BSA when both contain the same relative path).
            Trace($"TryLocateInBsa: HIT — file=[{subpath}] in [{containingBsaPath}]");
        }
        else
        {
            // Renderer fell through here after vanilla loose + mod-folder
            // (AdditionalDataFolders) checks both failed — definitive
            // "file not in any indexed BSA." The full BSA-path inventory was
            // dumped once at the end of EnsureAllArchivesOpened; correlate
            // this miss against that block to verify the expected archive is
            // actually indexed.
            Trace($"TryLocateInBsa: MISS — file=[{subpath}] (scanned {_bsa.GetIndexedBsaPaths().Count} indexed BSA file(s) across {keys.Count} mod key(s); see EnsureAllArchivesOpened inventory)");
        }
        return hit;
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
