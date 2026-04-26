using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            Trace($"ENTER tid={tid} mods={total}");

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

    public bool TryExtractToDisk(string subpath, string destPath)
    {
        if (!TryLocateInBsa(subpath, out var bsa) || bsa == null) return false;
        bool ok = _bsa.ExtractFileAsync(bsa, subpath, destPath).GetAwaiter().GetResult();
        if (!ok)
        {
            Trace($"TryExtractToDisk: FAILED — file=[{subpath}] from bsa=[{bsa}] dest=[{destPath}]");
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
    }
}
