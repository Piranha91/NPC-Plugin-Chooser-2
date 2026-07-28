using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Deletes the per-mod analysis logs written next to the .exe, so that a rescan cannot leave
/// entries behind for mods that no longer exist, were renamed, or no longer reject anything.
///
/// <para>Two folders are covered, both keyed by the path-safe form of a mod's display name:
/// "Rejected NPCs" (written by <c>VM_ModSetting.RefreshNpcLists</c>) and "LoadingErrors"
/// (exception dumps from plugin scanning / injected-record checks, which <em>append</em>, so
/// without this they accumulate indefinitely).</para>
///
/// <para>Staleness matters more than it used to: the rejection logs are no longer just text
/// files for the curious, they back the Settings &gt; Mod Import Settings &gt; Rejected NPCs
/// tree, where an orphaned file shows up as a mod the user no longer has.</para>
///
/// <para>Every operation is best-effort. A log the user happens to have open in an editor, or a
/// folder held by Explorer, must not take down the scan that called this — failures are traced
/// and skipped.</para>
/// </summary>
public static class AnalysisLogCleaner
{
    public const string RejectedNpcsFolderName = RejectedNpcLogParser.LogFolderName;
    public const string LoadingErrorsFolderName = "LoadingErrors";

    /// <summary>Suffix on the second log <c>CheckForInjectedRecords</c> writes for a mod.</summary>
    private const string InjectionCheckSuffix = "_InjectionCheck";

    private static readonly string[] LogFolderNames = { RejectedNpcsFolderName, LoadingErrorsFolderName };

    /// <summary>
    /// Removes every log in both folders. For the triggers that rebuild the whole mod list from
    /// scratch — a mods-folder change, or Refresh All — where per-mod deletion is not enough
    /// because a mod that was renamed or dropped never gets visited again to clean up after itself.
    /// </summary>
    public static void ClearAll(string? baseDirectory = null)
    {
        var root = ResolveBaseDirectory(baseDirectory);
        foreach (var folderName in LogFolderNames)
        {
            ClearFolder(Path.Combine(root, folderName));
        }
    }

    /// <summary>
    /// Removes just one mod's logs, for a single-mod Refresh. Called before the refresh runs
    /// rather than after, so that the paths which drop the mod entirely (own patch output, or
    /// no appearance data left) clean up too — those return early and never reach the code that
    /// would rewrite the logs.
    /// </summary>
    public static void ClearForMod(string modDisplayName, string? baseDirectory = null)
    {
        foreach (var path in GetLogPathsForMod(modDisplayName, baseDirectory))
        {
            TryDeleteFile(path);
        }
    }

    /// <summary>
    /// Rewrites one mod's rejection log, or deletes it when the mod rejected nothing this run.
    ///
    /// <para>The delete half is the whole point. Writing only when there is something to say leaves
    /// the previous run's file in place, so a mod that STOPS rejecting anything — its race became
    /// valid once a missing master was installed, its template chain no longer dead-ends in a
    /// Leveled NPC — goes on reporting rejections that no longer happen. Neither
    /// <see cref="ClearAll"/> nor <see cref="ClearForMod"/> catches that case, because the mod is
    /// still present and was re-analysed normally.</para>
    /// </summary>
    public static void WriteOrClearRejectionLog(string modDisplayName,
        IReadOnlyCollection<string> rejectionMessages, string? baseDirectory = null)
    {
        var safeName = Auxilliary.MakeStringPathSafe(modDisplayName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return; // No usable file name — the same condition under which no file was ever created.
        }

        var directory = Path.Combine(ResolveBaseDirectory(baseDirectory), RejectedNpcsFolderName);
        var path = Path.Combine(directory, safeName + ".txt");

        try
        {
            if (rejectionMessages.Count > 0)
            {
                Directory.CreateDirectory(directory); // No-op when it already exists.
                File.WriteAllLines(path, rejectionMessages);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnalysisLogCleaner] Could not update the rejection log for '{modDisplayName}': {ExceptionLogger.GetExceptionStack(ex)}");
        }
    }

    /// <summary>
    /// Every log path belonging to one mod. Empty when the display name has no path-safe form,
    /// which is the same condition under which the writers decline to create a file at all.
    /// </summary>
    public static IReadOnlyList<string> GetLogPathsForMod(string modDisplayName, string? baseDirectory = null)
    {
        var safeName = Auxilliary.MakeStringPathSafe(modDisplayName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return Array.Empty<string>();
        }

        var root = ResolveBaseDirectory(baseDirectory);
        var rejectedNpcs = Path.Combine(root, RejectedNpcsFolderName);
        var loadingErrors = Path.Combine(root, LoadingErrorsFolderName);

        return new[]
        {
            Path.Combine(rejectedNpcs, safeName + ".txt"),
            Path.Combine(loadingErrors, safeName + ".txt"),
            Path.Combine(loadingErrors, safeName + InjectionCheckSuffix + ".txt"),
        };
    }

    private static string ResolveBaseDirectory(string? baseDirectory) =>
        string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory!;

    /// <summary>
    /// Deletes the folder's logs but not the folder. Leaving it in place keeps an Explorer window
    /// the user opened from the Settings panel valid, and only *.txt is touched so anything else
    /// parked in there is left alone.
    /// </summary>
    private static void ClearFolder(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.txt", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnalysisLogCleaner] Could not enumerate '{directory}': {ExceptionLogger.GetExceptionStack(ex)}");
            return;
        }

        foreach (var file in files)
        {
            TryDeleteFile(file);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path); // No-op when absent; no need to probe first.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AnalysisLogCleaner] Could not delete '{path}': {ExceptionLogger.GetExceptionStack(ex)}");
        }
    }
}
