using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>One NPC under a group of the wig-and-headgear window.</summary>
/// <param name="NpcFormKey">The NPC being patched.</param>
/// <param name="Display">That NPC as the run log names it.</param>
/// <param name="ModName">The appearance mod selected for it.</param>
public sealed record WigHeadgearConflictNpcRow(FormKey NpcFormKey, string Display, string ModName);

/// <summary>
/// The NPCs that share one (outfit, headgear, wig) conflict, and the sentence that states it.
/// Every name and slot number comes from the records the back end read.
/// </summary>
public sealed record WigHeadgearConflictGroupRow(
    WigHeadgearConflict Conflict, IReadOnlyList<WigHeadgearConflictNpcRow> Npcs)
{
    /// <summary>"Wig 'W' (slot 31) would be added to outfit 'O', which holds 'H' (slots 31 and 42);
    /// both use slot 31".</summary>
    public string Description => WigHeadgearConflictGrouping.Describe(Conflict);

    /// <summary>"1 NPC" / "12 NPCs".</summary>
    public string CountText => WigHeadgearConflictGrouping.Plural(Npcs.Count, "NPC");
}

/// <summary>
/// The cases of the pre-run wig-and-headgear scan, arranged for display: grouped by
/// (outfit, headgear, wig), largest group first. Pure; no WPF.
///
/// <para>The back end returns one case per NPC, and a case can hold more than one conflict (a
/// wig against a helmet AND a circlet, or against each helmet of a leveled list). Such an NPC is
/// listed under every group it belongs to and counted once in the totals.</para>
/// </summary>
public sealed class WigHeadgearConflictGrouping
{
    private WigHeadgearConflictGrouping(IReadOnlyList<WigHeadgearConflictGroupRow> groups,
        IReadOnlyList<FormKey> npcFormKeys)
    {
        Groups = groups;
        NpcFormKeys = npcFormKeys;

        var rows = new List<object>(groups.Sum(g => g.Npcs.Count + 1));
        foreach (var group in groups)
        {
            rows.Add(group);
            rows.AddRange(group.Npcs);
        }
        Rows = rows;
    }

    /// <summary>Largest group first; groups of one size keep the order the scan found them in.</summary>
    public IReadOnlyList<WigHeadgearConflictGroupRow> Groups { get; }

    /// <summary>Every NPC of the scan, once, in the order given. The keys a choice is stored under.</summary>
    public IReadOnlyList<FormKey> NpcFormKeys { get; }

    /// <summary>The list as shown: each group's row followed by its NPC rows
    /// (<see cref="WigHeadgearConflictGroupRow"/> and <see cref="WigHeadgearConflictNpcRow"/>).
    /// Flat so the list control can virtualise it.</summary>
    public IReadOnlyList<object> Rows { get; }

    public int NpcCount => NpcFormKeys.Count;
    public int GroupCount => Groups.Count;

    /// <summary>"3 NPCs in 2 groups".</summary>
    public string TotalsText => $"{Plural(NpcCount, "NPC")} in {Plural(GroupCount, "group")}";

    public static WigHeadgearConflictGrouping From(IEnumerable<WigHeadgearConflictCase>? cases)
    {
        var order = new List<(FormKey Outfit, FormKey Headgear, FormKey Wig)>();
        var members = new Dictionary<(FormKey, FormKey, FormKey), (WigHeadgearConflict Conflict, List<WigHeadgearConflictNpcRow> Npcs)>();
        var npcKeys = new List<FormKey>();
        var seenNpcs = new HashSet<FormKey>();

        foreach (var conflictCase in cases ?? Enumerable.Empty<WigHeadgearConflictCase>())
        {
            if (conflictCase?.Conflicts == null || conflictCase.Conflicts.Count == 0) continue;

            if (seenNpcs.Add(conflictCase.TargetNpcFormKey)) npcKeys.Add(conflictCase.TargetNpcFormKey);

            var row = new WigHeadgearConflictNpcRow(conflictCase.TargetNpcFormKey,
                conflictCase.TargetNpcDisplay, conflictCase.AppearanceModName);

            foreach (var conflict in conflictCase.Conflicts)
            {
                var key = (conflict.OutfitFormKey, conflict.Headgear.FormKey, conflict.Wig.FormKey);
                if (!members.TryGetValue(key, out var group))
                {
                    group = (conflict, new List<WigHeadgearConflictNpcRow>());
                    members[key] = group;
                    order.Add(key);
                }

                // The same NPC reported twice for one triple is still one row.
                if (!group.Npcs.Any(n => n.NpcFormKey == row.NpcFormKey)) group.Npcs.Add(row);
            }
        }

        // OrderByDescending is stable: equal sizes stay in discovery order.
        var groups = order
            .Select(key => members[key])
            .OrderByDescending(g => g.Npcs.Count)
            .Select(g => new WigHeadgearConflictGroupRow(g.Conflict, g.Npcs))
            .ToList();

        return new WigHeadgearConflictGrouping(groups, npcKeys);
    }

    /// <summary>The conflict as a sentence about what the run WOULD do (the back end's own
    /// <see cref="WigHeadgearConflict.Describe"/> is worded for the log, after the fact).</summary>
    public static string Describe(WigHeadgearConflict conflict) =>
        $"Wig '{conflict.Wig.Label}' ({WigHeadgearConflictDetector.FormatSlots(conflict.Wig.Slots)}) would be " +
        $"added to outfit '{conflict.OutfitLabel}', which holds '{conflict.Headgear.Label}' " +
        $"({WigHeadgearConflictDetector.FormatSlots(conflict.Headgear.Slots)}); both use " +
        $"{WigHeadgearConflictDetector.FormatSlots(conflict.SharedSlots)}";

    internal static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
