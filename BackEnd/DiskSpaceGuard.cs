using System;
using System.IO;
using System.Threading;
using System.Windows;
using NPC_Plugin_Chooser_2.Views;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Stops mugshot renders from silently producing partial images when a drive
/// runs out of space, and tells the user why.
///
/// <para>SkyRender reads BSA content by extracting each file into
/// <c>&lt;exe-dir&gt;\SkyRenderCache</c> through <see cref="BsaHandler"/>. When that
/// drive is full every extraction fails, the renderer drops the asset and still
/// returns a PNG: vanilla tiles came out as a bare loose-file body with no
/// FaceGen head and no outfit, modded tiles lost eyes and hair. Nothing on the
/// tile said why. Two checks close that gap:</para>
/// <list type="bullet">
/// <item><see cref="CheckRenderPreflight"/> refuses to start a render when the
/// cache or output drive is below <see cref="MinFreeBytes"/>.</item>
/// <item><see cref="ReportDiskFull"/> counts disk-full write failures (from BSA
/// extraction or a PNG write). A render that saw the count move discards its
/// output instead of saving a partial image.</item>
/// </list>
/// <para>Both raise one warning dialog per low-space episode; the episode ends
/// when a later preflight finds enough room again.</para>
/// </summary>
public static class DiskSpaceGuard
{
    /// <summary>Below this much free space a render is not started. A floor,
    /// not a reservation: an extraction or PNG write that still runs out of
    /// space is caught on its own and the render discarded.</summary>
    public const long MinFreeBytes = 25L * 1024 * 1024;

    private static long _diskFullEvents;
    private static int _warningActive;

    /// <summary>Monotonic count of disk-full write failures this session.
    /// Snapshot before a render and compare after it.</summary>
    public static long DiskFullEventCount => Interlocked.Read(ref _diskFullEvents);

    /// <summary>The renderer's BSA extraction folder (SkyRender's default).</summary>
    public static string RenderCacheFolder => Path.Combine(AppContext.BaseDirectory, "SkyRenderCache");

    /// <summary>True when <paramref name="ex"/> (or an inner exception) is a
    /// Win32 out-of-space error.</summary>
    public static bool IsDiskFullError(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is not IOException) continue;
            int win32Code = e.HResult & 0xFFFF;
            if (win32Code == 112 /* ERROR_DISK_FULL */ || win32Code == 39 /* ERROR_HANDLE_DISK_FULL */)
                return true;
        }
        return false;
    }

    /// <summary>Records a disk-full write failure at <paramref name="path"/> and
    /// warns the user (once per episode).</summary>
    public static void ReportDiskFull(string path)
    {
        Interlocked.Increment(ref _diskFullEvents);
        RaiseWarning(SafeRoot(path), freeBytes: null);
    }

    /// <summary>Returns false (and warns once per episode) when the render cache
    /// drive or the drive holding <paramref name="outputPath"/> has less than
    /// <see cref="MinFreeBytes"/> free. Unknown free space (network path,
    /// probe failure) never blocks a render.</summary>
    public static bool CheckRenderPreflight(string outputPath)
    {
        foreach (var path in new[] { RenderCacheFolder, outputPath })
        {
            long? free = TryGetFreeBytes(path);
            if (free.HasValue && free.Value < MinFreeBytes)
            {
                RaiseWarning(SafeRoot(path), free);
                return false;
            }
        }
        // Space recovered (or was never short): the next shortage warns again.
        Interlocked.Exchange(ref _warningActive, 0);
        return true;
    }

    public static long? TryGetFreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Builds the dialog text. Public for tests.</summary>
    public static string BuildWarningMessage(string driveRoot, long? freeBytes, bool legacyCacheExists)
    {
        string freeText = freeBytes.HasValue ? $" ({FormatBytes(freeBytes.Value)} free)" : "";
        var msg =
            $"Drive {driveRoot} is out of space{freeText}.\n\n" +
            "Mugshot rendering is paused: the renderer extracts files from BSA archives to\n" +
            $"{RenderCacheFolder}\n" +
            "and saves mugshots to disk. Without room for those files, renders come out with " +
            "missing heads, eyes, hair or outfits, so NPC2 will not save them. Affected tiles " +
            "keep their placeholder.\n\n" +
            $"Free at least {FormatBytes(MinFreeBytes)} on {driveRoot}, then regenerate the affected mugshots.";
        if (legacyCacheExists)
        {
            msg += "\n\nThe old renderer cache folder\n" +
                   $"{LegacyCacheFolder}\n" +
                   "is no longer used and can be deleted to reclaim space.";
        }
        return msg;
    }

    private static string LegacyCacheFolder => Path.Combine(AppContext.BaseDirectory, "CharacterViewerCache");

    private static void RaiseWarning(string driveRoot, long? freeBytes)
    {
        if (Interlocked.Exchange(ref _warningActive, 1) == 1) return;

        bool legacyCacheExists = false;
        try { legacyCacheExists = Directory.Exists(LegacyCacheFolder); } catch { /* best-effort hint */ }
        string message = BuildWarningMessage(driveRoot, freeBytes, legacyCacheExists);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return; // tests / headless harness
        dispatcher.BeginInvoke(() => ScrollableMessageBox.ShowWarning(message, "Out of Disk Space"));
    }

    private static string SafeRoot(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? path; }
        catch { return path; }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / 1024.0:0.#} KB";
    }
}
