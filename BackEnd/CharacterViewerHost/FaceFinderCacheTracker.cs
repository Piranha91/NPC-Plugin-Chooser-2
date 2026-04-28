using System;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Parallel to <see cref="GeneratedMugshotTracker"/> but for the
/// <see cref="Settings.CachedFaceFinderPaths"/> bucket — the cache that
/// drives "Delete Cached FaceFinder Images" Fast mode. The two caches are
/// deliberately disjoint: FaceFinder downloads must NOT be deleted by
/// "Delete All Auto-Generated Mugshots" and vice versa, so each pipeline
/// records into its own bucket.
///
/// Like the generated tracker, every Add / Remove fires
/// <see cref="VM_Settings.RequestThrottledSave"/> so the cache persists
/// within ~1.5 s of the file write — independent of whether the app
/// exits cleanly. <see cref="TrackIfFileExists"/> closes the partial-write
/// gap when a download is cancelled or errors mid-write.
/// </summary>
public sealed class FaceFinderCacheTracker
{
    private readonly Settings _settings;
    private readonly Lazy<VM_Settings> _lazyVmSettings;

    public FaceFinderCacheTracker(Settings settings, Lazy<VM_Settings> lazyVmSettings)
    {
        _settings = settings;
        _lazyVmSettings = lazyVmSettings;
    }

    public void Track(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_settings.CachedFaceFinderPaths.Add(path))
        {
            RequestSave();
        }
    }

    public void TrackIfFileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (System.IO.File.Exists(path))
        {
            Track(path);
        }
    }

    public void Untrack(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_settings.CachedFaceFinderPaths.Remove(path))
        {
            RequestSave();
        }
    }

    private void RequestSave() => _lazyVmSettings.Value?.RequestThrottledSave();
}
