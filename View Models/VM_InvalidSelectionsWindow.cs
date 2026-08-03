using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Text;
using System.Windows;
using NPC_Plugin_Chooser_2.BackEnd;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Backs the pre-run "Invalid Selections Found" dialog: the rejections the screening pass
/// produced, as a three-level tree of issue -> mod -> NPC, plus text/spreadsheet export of the
/// same content. The window itself owns the go/no-go answer (DialogResult); this type is purely
/// the presentation of what would be skipped.
/// </summary>
public sealed class VM_InvalidSelectionsWindow : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly IReadOnlyList<Validator.InvalidSelection> _entries;

    public ObservableCollection<VM_InvalidSelectionIssue> Issues { get; } = new();

    public string SummaryText { get; }

    public ReactiveCommand<Unit, Unit> CopyTextCommand { get; }
    public ReactiveCommand<Unit, Unit> CopySheetCommand { get; }

    private static readonly string[] SheetHeaders = { "Issue", "Mod", "NPC", "FormKey", "Missing Record" };

    public VM_InvalidSelectionsWindow(IReadOnlyList<Validator.InvalidSelection> entries)
    {
        _entries = entries ?? Array.Empty<Validator.InvalidSelection>();

        // Same grouping as Validator.FormatInvalidSelectionsReport, and deliberately the same
        // order: GroupBy keeps first-appearance order, so the tree, the copied text and the run
        // log all read in screening order.
        foreach (var reasonGroup in _entries.GroupBy(e => e.Reason, StringComparer.Ordinal))
        {
            var mods = reasonGroup
                .GroupBy(e => e.ModName, StringComparer.OrdinalIgnoreCase)
                .Select(modGroup => new VM_InvalidSelectionMod(
                    modGroup.Key,
                    modGroup.Select(e => new VM_InvalidSelectionNpc(e))))
                .ToList();

            Issues.Add(new VM_InvalidSelectionIssue(reasonGroup.Key, mods));
        }

        int npcCount = _entries.Count;
        int modCount = _entries.Select(e => e.ModName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        SummaryText =
            $"Found {npcCount} invalid NPC selection{(npcCount == 1 ? string.Empty : "s")} " +
            $"across {modCount} mod{(modCount == 1 ? string.Empty : "s")} and " +
            $"{Issues.Count} issue{(Issues.Count == 1 ? string.Empty : "s")}. " +
            "These NPCs will be skipped.";

        CopyTextCommand = ReactiveCommand.Create(CopyText).DisposeWith(_disposables);
        CopySheetCommand = ReactiveCommand.Create(CopySheet).DisposeWith(_disposables);
    }

    /// <summary>The indented report — the same text the old message box showed, so anything a
    /// user has previously pasted into a bug report still looks familiar.</summary>
    private void CopyText() => SetClipboard(Validator.FormatInvalidSelectionsReport(_entries));

    /// <summary>One flat tab-separated row per rejected NPC, headers included: pastes straight
    /// into Excel as five columns, where the tree's grouping is a sort/pivot away. The last column
    /// is empty for rejections that carry no per-NPC detail.</summary>
    private void CopySheet()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("\t", SheetHeaders));
        foreach (var e in _entries)
        {
            sb.AppendLine(string.Join("\t",
                new[] { e.Reason, e.ModName, e.NpcDescription, e.NpcFormKey, e.Detail }.Select(CleanTsv)));
        }
        SetClipboard(sb.ToString());
    }

    private static void SetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* clipboard can be transiently locked by another process; ignore */ }
    }

    private static string CleanTsv(string? s) =>
        (s ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    public void Dispose() => _disposables.Dispose();
}

/// <summary>Top tree level: one rejection reason, shared by every NPC beneath it.</summary>
public sealed class VM_InvalidSelectionIssue : ReactiveObject
{
    public VM_InvalidSelectionIssue(string reason, IEnumerable<VM_InvalidSelectionMod> mods)
    {
        Reason = reason;
        Mods = new ObservableCollection<VM_InvalidSelectionMod>(mods);
        SelectionCount = Mods.Sum(m => m.Npcs.Count);
    }

    public string Reason { get; }
    public ObservableCollection<VM_InvalidSelectionMod> Mods { get; }
    public int SelectionCount { get; }

    public string DisplayText =>
        $"{Reason} ({SelectionCount} selection{(SelectionCount == 1 ? string.Empty : "s")})";

    /// <summary>Expanded by default: the issues are the headline, and there are only ever a
    /// handful of them.</summary>
    [Reactive] public bool IsExpanded { get; set; } = true;
}

/// <summary>Middle tree level: the mod the rejected appearance was chosen from.</summary>
public sealed class VM_InvalidSelectionMod : ReactiveObject
{
    public VM_InvalidSelectionMod(string modName, IEnumerable<VM_InvalidSelectionNpc> npcs)
    {
        ModName = modName;
        Npcs = new ObservableCollection<VM_InvalidSelectionNpc>(npcs);
    }

    public string ModName { get; }
    public ObservableCollection<VM_InvalidSelectionNpc> Npcs { get; }

    public string DisplayText =>
        $"{ModName} ({Npcs.Count} NPC{(Npcs.Count == 1 ? string.Empty : "s")})";

    /// <summary>Collapsed by default: one shared reason can reject hundreds of NPCs, and
    /// expanding all of them on open buries the issue list itself.</summary>
    [Reactive] public bool IsExpanded { get; set; }
}

/// <summary>Leaf: one rejected NPC, named as it is elsewhere in the app with its FormKey
/// appended, plus whatever is specific to this NPC under the shared reason above it.</summary>
public sealed class VM_InvalidSelectionNpc : ReactiveObject
{
    public VM_InvalidSelectionNpc(Validator.InvalidSelection entry)
    {
        // With the detail, because the reason heading has already been printed once for the whole
        // group: the row's job is what differs between the NPCs under it. For the written-link
        // rejections that is the record the appearance points at and the user's install lacks —
        // the reason names the plugin, only this names the record.
        DisplayText = entry.NpcLabelWithDetail;
        NpcFormKey = entry.NpcFormKey;
        Detail = entry.Detail;
    }

    public string DisplayText { get; }
    public string NpcFormKey { get; }
    public string Detail { get; }

    /// <summary>Hover text: the FormKey alone once the row carries no detail, otherwise both, since
    /// the row then holds two FormKeys and the tooltip has to say which one is the NPC.</summary>
    public string ToolTipText =>
        string.IsNullOrWhiteSpace(Detail) ? NpcFormKey : $"NPC {NpcFormKey}\nReferences {Detail}";

    /// <summary>Leaves never have children, so this only exists to satisfy the shared
    /// TreeViewItem style's two-way IsExpanded binding.</summary>
    [Reactive] public bool IsExpanded { get; set; }
}
