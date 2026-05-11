using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CharacterViewer.Rendering;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// SHA-keyed lazy cache for FaceGen-NIF stats (vertex / triangle / shape /
/// file-size totals) feeding the per-tile overlay on the NPCs tab. Each entry
/// is stamped with the source NIF's SHA256 at capture time; on the next view
/// the live NIF's hash is compared and an outdated entry triggers a fresh
/// parse. Persistence piggybacks <see cref="Settings.FaceGenAnalysisCache"/>
/// + <see cref="VM_Settings.RequestThrottledSave"/> so a clean exit (or any
/// throttle-window crash within ~1.5 s) preserves the cache across runs.
///
/// <para>Why SHA over disk-path or mtime: BSA-extracted NIFs land at a shared
/// temp slot (<c>tmpExtraction/FaceGen/&lt;plugin&gt;/&lt;id&gt;.nif</c>) that's
/// overwritten by whichever extraction ran last. Both disk path and mtime
/// are unstable across (mod, NPC) pairs that happen to share a plugin
/// filename. The same SHA the autogen staleness checker uses
/// (<see cref="PortraitCreator.NeedsRegeneration"/> via its private
/// <c>CalculateSha256</c>) is the only proxy that's stable for the actual
/// content the user cares about.</para>
/// </summary>
public sealed class FaceGenAnalysisCache
{
    private readonly Settings _settings;
    private readonly Lazy<VM_Settings> _lazyVmSettings;
    private readonly PortraitCreator _portraitCreator;
    private readonly NifMeshBuilder _meshBuilder;

    public FaceGenAnalysisCache(
        Settings settings,
        Lazy<VM_Settings> lazyVmSettings,
        PortraitCreator portraitCreator,
        // CharacterPreviewCache exposes the NifMeshBuilder instance that the
        // rest of the rendering pipeline already shares — reuse it rather
        // than constructing a parallel parser. Matches MeshSurveyRunner's
        // wiring.
        CharacterPreviewCache previewCache)
    {
        _settings = settings;
        _lazyVmSettings = lazyVmSettings;
        _portraitCreator = portraitCreator;
        _meshBuilder = previewCache.MeshBuilder;
    }

    /// <summary>Get-or-compute stats for the (mod, NPC) pair. Returns null
    /// when no FaceGen NIF can be located (uninstalled mod, missing FaceGen
    /// geometry, etc.). Safe to call from a worker thread — does its own
    /// SHA + NIF parse synchronously; the caller is expected to be inside
    /// a <c>Task.Run</c> already.</summary>
    public NifMeshBuilder.FaceGenStats? Get(VM_ModSetting modSetting, FormKey npcFormKey,
        bool measureGeometry, CancellationToken ct = default)
    {
        if (modSetting == null || npcFormKey.IsNull) return null;
        ct.ThrowIfCancellationRequested();

        // Resolve disk path (loose-or-BSA, with the latter extracted to the
        // shared temp slot). Re-running this is what makes the SHA stable —
        // we always re-resolve before consulting the cache so a re-install
        // / re-extraction can't serve stale numbers.
        string nifPath;
        try
        {
            nifPath = _portraitCreator.FindNpcNifPath(npcFormKey, modSetting)
                .GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(nifPath) || !File.Exists(nifPath))
            return null;

        ct.ThrowIfCancellationRequested();

        string sha;
        try { sha = ComputeSha256(nifPath); }
        catch { return null; }

        string key = MakeKey(modSetting, npcFormKey);

        // Cache hit: SHA matches AND we have enough data for the request
        // (a size-only entry is reusable for a size-only request but not
        // for a geometry-requested call — re-parse in the latter case).
        if (_settings.FaceGenAnalysisCache.TryGetValue(key, out var cached)
            && string.Equals(cached.Sha, sha, StringComparison.OrdinalIgnoreCase)
            && (cached.MeasuredGeometry || !measureGeometry))
        {
            return new NifMeshBuilder.FaceGenStats(
                LoadOk: cached.MeasuredGeometry || cached.FileSizeBytes > 0,
                TotalVertices: cached.Vertices,
                TotalTriangles: cached.Triangles,
                ShapeCount: cached.Shapes,
                FileSizeBytes: cached.FileSizeBytes);
        }

        ct.ThrowIfCancellationRequested();
        var stats = _meshBuilder.AnalyzeFaceGen(nifPath, measureGeometry);

        _settings.FaceGenAnalysisCache[key] = new CachedFaceGenStats
        {
            Sha = sha,
            Vertices = stats.TotalVertices,
            Triangles = stats.TotalTriangles,
            Shapes = stats.ShapeCount,
            FileSizeBytes = stats.FileSizeBytes,
            MeasuredGeometry = measureGeometry && stats.LoadOk,
        };
        _lazyVmSettings.Value?.RequestThrottledSave();

        return stats;
    }

    /// <summary>Removes every cached entry. Hooked up to a "Clear FaceGen
    /// Analysis Cache" button in Settings.</summary>
    public void Clear()
    {
        if (_settings.FaceGenAnalysisCache.Count == 0) return;
        _settings.FaceGenAnalysisCache.Clear();
        _lazyVmSettings.Value?.RequestThrottledSave();
    }

    private static string MakeKey(VM_ModSetting modSetting, FormKey npcFormKey)
    {
        // DisplayName is the stable user-visible label — survives plugin
        // renames within the same mod folder set. NpcFormKey.ToString() is
        // "xxxxxxxx:Plugin.esp" — unique inside a load order.
        return modSetting.DisplayName + "|" + npcFormKey.ToString();
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }
}
