using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>One NPCs-menu tile the prewarmer may warm: the same (source NPC, mod, target NPC)
/// triple the tile passes to <see cref="BatchMugshotGenerator.RunSelectedRendererAsync"/>.</summary>
public readonly record struct PrewarmTile(FormKey SourceNpc, VM_ModSetting Mod, FormKey TargetNpc);

/// <summary>
/// Background warm-ups that hide mugshot preparation time without changing output.
/// <list type="bullet">
/// <item><see cref="Schedule"/>: after the current NPC's tiles finish, warms the renderer's CPU
/// caches for the next NPC's tiles, so navigating there renders from warm caches. One tile at a
/// time, only while no render is queued or running, at most <see cref="MaxTilesPerNpc"/> tiles
/// (keeps the parsed-mesh cache from evicting its own entries), cancelled by the next Schedule.</item>
/// <item><see cref="WarmUpOnce"/>: opens the BSA index in the background after start-up, a cost
/// otherwise paid by every tile of the first NPC rendered.</item>
/// </list>
/// Both are opt-out (<see cref="InternalMugshotSettings.PrewarmNextNpcMugshots"/>,
/// <see cref="InternalMugshotSettings.WarmUpRendererAtStartup"/>) and memory-gated
/// (<see cref="HasHeadroom"/>, checked before each tile).
/// </summary>
public sealed class NpcMugshotPrewarmer
{
    public const int MaxTilesPerNpc = 12;
    private const int IdleSettleMs = 750;
    private const int IdlePollMs = 150;

    private readonly Settings _settings;
    private readonly BatchMugshotGenerator _generator;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private int _warmedUp;

    public NpcMugshotPrewarmer(Settings settings, BatchMugshotGenerator generator)
    {
        _settings = settings;
        _generator = generator;
    }

    /// <summary>Enough free RAM to grow the caches: at least max(4 GB, 25% of physical RAM)
    /// free. False when the totals are unknown. Same inputs as the renderer's cache budget
    /// (<c>GC.GetGCMemoryInfo</c>): total available minus system-wide load.</summary>
    public static bool HasHeadroom(long totalBytes, long loadBytes)
    {
        if (totalBytes <= 0) return false;
        long free = totalBytes - loadBytes;
        return free >= Math.Max(4L << 30, totalBytes / 4);
    }

    private static bool HasHeadroomNow()
    {
        var info = GC.GetGCMemoryInfo();
        return HasHeadroom(info.TotalAvailableMemoryBytes, info.MemoryLoadBytes);
    }

    public void WarmUpOnce()
    {
        if (!_settings.InternalMugshot.WarmUpRendererAtStartup) return;
        if (!_settings.UsePortraitCreatorFallback || _settings.SelectedRenderer != MugshotRenderer.Internal) return;
        if (Interlocked.Exchange(ref _warmedUp, 1) == 1) return;
        if (!HasHeadroomNow()) return;
        _ = Task.Run(() =>
        {
            try { _generator.EnsureArchivesOpened(); }
            catch (Exception ex) { Debug.WriteLine($"NpcMugshotPrewarmer warm-up failed: {ex.Message}"); }
        });
    }

    /// <summary>Replaces any pending prewarm with <paramref name="tiles"/> (pass none to
    /// just cancel). Returns immediately; the work waits for the renderer to go idle.</summary>
    public void Schedule(IReadOnlyList<PrewarmTile> tiles)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
            if (tiles.Count == 0) return;
            if (!_settings.InternalMugshot.PrewarmNextNpcMugshots)
            {
                LogEvent(tiles[0].TargetNpc, null, "disabled", 0);
                return;
            }
            cts = _cts = new CancellationTokenSource();
        }
        var batch = tiles.Take(MaxTilesPerNpc).ToList();
        LogEvent(batch[0].TargetNpc, null, $"scheduled {batch.Count} of {tiles.Count}", 0);
        _ = Task.Run(() => RunAsync(batch, cts.Token));
    }

    private async Task RunAsync(List<PrewarmTile> tiles, CancellationToken token)
    {
        int warmed = 0;
        var current = (PrewarmTile?)null;
        try
        {
            foreach (var tile in tiles)
            {
                current = tile;
                await WaitForIdleAsync(token).ConfigureAwait(false);
                if (!HasHeadroomNow())
                {
                    LogEvent(tile.TargetNpc, tile, "stopped-low-memory", 0);
                    break;
                }
                var sw = Stopwatch.StartNew();
                bool ran = await _generator.PrewarmTileIfNeededAsync(tile.SourceNpc, tile.Mod, tile.TargetNpc, token)
                    .ConfigureAwait(false);
                if (ran) warmed++;
                LogEvent(tile.TargetNpc, tile, ran ? "warmed" : (token.IsCancellationRequested ? "cancelled" : "skipped"),
                    sw.ElapsedMilliseconds);
                current = null;
            }
        }
        catch (OperationCanceledException)
        {
            if (current is { } t) LogEvent(t.TargetNpc, t, "cancelled", 0);
        }
        catch (Exception ex) { Debug.WriteLine($"NpcMugshotPrewarmer failed: {ex.Message}"); }
        Debug.WriteLine($"[NpcPerf] next-NPC prewarm warmed={warmed} of {tiles.Count} cancelled={token.IsCancellationRequested}");
    }

    // Opt-in activity log beside RenderTimings.csv (same LogRenderTimings.txt trigger), so a
    // timing run records whether and what the prewarm did. "skipped" = the tile needed no
    // render (fresh autogen / FaceFinder image) or had nothing to warm.
    private static readonly object _logLock = new();
    private static readonly string _logPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "RenderLogs", "PrewarmEvents.csv");
    private static bool _logStarted;

    private static void LogEvent(FormKey targetNpc, PrewarmTile? tile, string outcome, long ms)
    {
        if (!InternalMugshotGenerator.RenderTimingsEnabled) return;
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            string row = string.Join(",", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", ci),
                Q(targetNpc.ToString()), Q(tile?.SourceNpc.ToString() ?? ""), Q(tile?.Mod.DisplayName ?? ""),
                Q(outcome), ms.ToString(ci));
            lock (_logLock)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_logPath)!);
                if (!_logStarted)
                {
                    _logStarted = true;
                    if (!System.IO.File.Exists(_logPath))
                        System.IO.File.AppendAllText(_logPath, "at,targetNpc,sourceNpc,mod,outcome,ms\n");
                }
                System.IO.File.AppendAllText(_logPath, row + "\n");
            }
        }
        catch (Exception ex) { Debug.WriteLine($"PrewarmEvents log failed: {ex.Message}"); }
    }

    /// <summary>Waits until no render has been queued or running for <see cref="IdleSettleMs"/>,
    /// so the prewarm never competes with the tiles on screen (their generation is kicked a
    /// moment after the tile set binds, hence the settle window rather than a single check).</summary>
    private async Task WaitForIdleAsync(CancellationToken token)
    {
        var idle = Stopwatch.StartNew();
        while (idle.ElapsedMilliseconds < IdleSettleMs)
        {
            await Task.Delay(IdlePollMs, token).ConfigureAwait(false);
            if (_generator.InFlightRenders > 0) idle.Restart();
        }
    }
}
