using System;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Single entry point for recording (and forgetting) mugshot PNGs produced
/// by NPC2's auto-generation pipelines — Internal renderer, legacy NPC
/// Portrait Creator subprocess, and FaceFinder downloads. Wraps
/// <see cref="Settings.GeneratedMugshotPaths"/> additions and removals
/// with an immediate <see cref="VM_Settings.RequestThrottledSave"/> trigger
/// so the cache persists within ~1.5 s of the file write — independent of
/// whether the app exits cleanly. Without this, an abnormal exit (debugger
/// stop, crash, OS shutdown) between generation and app-exit would discard
/// the in-memory cache additions and leave orphan PNGs on disk that
/// Fast-mode "Delete All Auto-Generated Mugshots" can't see.
///
/// Also tolerates partial-write / cancellation cases: callers can invoke
/// <see cref="TrackIfFileExists"/> from cancellation / error paths so a
/// PNG that hit disk before the cancel propagated still ends up in the
/// cache and gets cleaned up on the next Fast-mode delete.
/// </summary>
public sealed class GeneratedMugshotTracker
{
    private readonly Settings _settings;
    private readonly Lazy<VM_Settings> _lazyVmSettings;

    public GeneratedMugshotTracker(Settings settings, Lazy<VM_Settings> lazyVmSettings)
    {
        _settings = settings;
        _lazyVmSettings = lazyVmSettings;
    }

    /// <summary>Adds <paramref name="path"/> to the cache and schedules a
    /// throttled save. No-op when the path is null/empty or already
    /// present (HashSet semantics).</summary>
    public void Track(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_settings.GeneratedMugshotPaths.Add(path))
        {
            RequestSave();
        }
    }

    /// <summary>Variant for cancellation / error paths: tracks the path
    /// only if it actually landed on disk. Use this in catch blocks of
    /// generators that may have partially written the PNG before the
    /// cancellation token fired or the exception propagated.</summary>
    public void TrackIfFileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (System.IO.File.Exists(path))
        {
            Track(path);
        }
    }

    /// <summary>Removes <paramref name="path"/> from the cache and
    /// schedules a throttled save. No-op when the path is null/empty
    /// or absent.</summary>
    public void Untrack(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_settings.GeneratedMugshotPaths.Remove(path))
        {
            RequestSave();
        }
    }

    private void RequestSave() => _lazyVmSettings.Value?.RequestThrottledSave();
}
