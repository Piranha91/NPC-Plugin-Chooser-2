using System;
using System.Diagnostics;
using CharacterViewer.Rendering;
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
        }
    }

    private static void Trace(string message)
    {
        Debug.WriteLine($"[BsaAdapter] {message}");
        System.Diagnostics.Trace.WriteLine($"[BsaAdapter] {message}");
    }

    public bool TryLocateInBsa(string subpath, out string? containingBsaPath)
    {
        containingBsaPath = null;
        var keys = _bsa.GetIndexedModKeys();
        if (keys.Count == 0) return false;
        return _bsa.FileExists(subpath, keys, out _, out containingBsaPath);
    }

    public bool TryExtractToDisk(string subpath, string destPath)
    {
        if (!TryLocateInBsa(subpath, out var bsa) || bsa == null) return false;
        return _bsa.ExtractFileAsync(bsa, subpath, destPath).GetAwaiter().GetResult();
    }
}
