using System;
using System.Collections.Generic;
using System.Linq;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Re-inserts locked mod folders into a folder list that a Refresh has just rebuilt, preserving
/// each locked folder's position <em>relative to the folders around it</em> rather than a raw index.
///
/// <para>A Refresh (<c>VM_Mods.CleanupCorrespondingFolders</c>) resets a mod's
/// <c>CorrespondingFolderPaths</c> to its primary folder and lets the master-chain detector re-add
/// whatever it can find. Folders with no plugin — the "silent" resource dependencies locking exists
/// for — are never re-added, and the detector inserts everything it does find at index 0, so raw
/// indices from before the rebuild are meaningless afterwards.</para>
///
/// <para>Anchoring solves that: for each locked folder, walk backwards through the pre-refresh order
/// for the nearest entry that still survives and re-insert immediately after it. Locked folders are
/// processed in their original order and count as survivors once placed, so a run of consecutive
/// locked folders chains off itself and stays intact. A locked folder whose every predecessor is gone
/// (or that led the list to begin with) goes to the front.</para>
/// </summary>
public static class LockedFolderOrdering
{
    /// <summary>
    /// Returns <paramref name="rebuilt"/> with every path in <paramref name="lockedPaths"/> present
    /// exactly once, positioned per the anchoring rule above. Locked paths already in
    /// <paramref name="rebuilt"/> (the detector happened to re-find them) are repositioned rather than
    /// left where the detector put them, so the result does not depend on whether a given refresh run
    /// rediscovered them. Locked paths absent from <paramref name="originalOrder"/> are appended at the
    /// end — there is no anchor to reason about, and dropping them would defeat the lock.
    /// </summary>
    /// <param name="rebuilt">Folder list as the refresh left it.</param>
    /// <param name="originalOrder">Folder list as it stood immediately before the refresh.</param>
    /// <param name="lockedPaths">Paths the user has pinned.</param>
    public static List<string> Reconcile(
        IEnumerable<string> rebuilt,
        IEnumerable<string> originalOrder,
        IEnumerable<string> lockedPaths)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var original = originalOrder?.ToList() ?? new List<string>();
        var locked = (lockedPaths ?? Enumerable.Empty<string>()).ToList();
        var lockedSet = new HashSet<string>(locked, comparer);

        // Locked folders are placed by this method, so start from a list with none of them in it.
        var result = (rebuilt ?? Enumerable.Empty<string>())
            .Where(p => !lockedSet.Contains(p))
            .ToList();

        if (lockedSet.Count == 0)
        {
            return result;
        }

        // Walk the pre-refresh order so locked folders are placed in the sequence the user saw them,
        // which is what makes consecutive locked folders chain off one another correctly.
        for (int i = 0; i < original.Count; i++)
        {
            var lockedPath = original[i];
            if (!lockedSet.Contains(lockedPath)) continue;
            if (result.Contains(lockedPath, comparer)) continue; // duplicate entry in originalOrder

            int insertAt = 0; // no surviving predecessor -> lead the list
            for (int back = i - 1; back >= 0; back--)
            {
                int anchorIndex = result.FindIndex(p => comparer.Equals(p, original[back]));
                if (anchorIndex >= 0)
                {
                    insertAt = anchorIndex + 1;
                    break;
                }
            }

            result.Insert(insertAt, lockedPath);
        }

        // Locked but never seen in the pre-refresh list: no anchor exists, so append rather than drop.
        // Iterated in caller order (not HashSet order) so the outcome is deterministic.
        foreach (var orphan in locked)
        {
            if (!result.Contains(orphan, comparer)) result.Add(orphan);
        }

        return result;
    }
}
