using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Backs the per-mod "Override Roots" dialog: which fields of an NPC record the dependent-override
/// search may START from for this mod.
///
/// <para>Roots, not record types. A ticked field is followed to unlimited depth through whatever
/// record types it reaches; an unticked one is simply not a starting point. The default set is
/// appearance-only because rooting at everything walks out through AI packages into cells, placed
/// references and quests and copies their ancestry in as private duplicates — but it is offered as
/// a per-mod choice rather than a fixed rule, because no allowlist of appearance fields can be
/// proven complete and a mod that genuinely needs another root should be able to say so.</para>
/// </summary>
public class VM_OverrideRootSelector : ReactiveObject, IDisposable
{
    public event Action RequestClose = delegate { };

    public ObservableCollection<VM_SelectableRootField> Fields { get; }
    public bool HasChanged { get; private set; }

    /// <summary>True while the selection equals the catalog defaults, so the caller can persist
    /// null ("follow the default") instead of freezing today's default into the mod.</summary>
    public bool IsAtDefaults => GetSelection().SetEquals(NpcRootFieldCatalog.Defaults);

    [Reactive] public string SummaryText { get; private set; } = string.Empty;

    public ReactiveCommand<Unit, Unit> OKCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckAllCommand { get; }
    public ReactiveCommand<Unit, Unit> UncheckAllCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetToDefaultsCommand { get; }

    private readonly HashSet<NpcRootField> _initialSelection;
    private readonly CompositeDisposable _disposables = new();

    public VM_OverrideRootSelector(IReadOnlySet<NpcRootField> currentSelection, string modDisplayName)
    {
        _initialSelection = new HashSet<NpcRootField>(currentSelection);
        ModDisplayName = modDisplayName;

        // Catalog order, which is xEdit's field order — the dialog reads like the record does.
        Fields = new ObservableCollection<VM_SelectableRootField>(
            NpcRootFieldCatalog.All.Select(entry =>
                new VM_SelectableRootField(entry, _initialSelection.Contains(entry.Field))));

        foreach (var row in Fields)
        {
            row.WhenAnyValue(x => x.IsSelected)
                .Subscribe(_ => UpdateSummary())
                .DisposeWith(_disposables);
        }

        OKCommand = ReactiveCommand.Create(ExecuteOk).DisposeWith(_disposables);
        CancelCommand = ReactiveCommand.Create(ExecuteCancel).DisposeWith(_disposables);
        CheckAllCommand = ReactiveCommand.Create(() => SetAll(_ => true)).DisposeWith(_disposables);
        UncheckAllCommand = ReactiveCommand.Create(() => SetAll(_ => false)).DisposeWith(_disposables);
        ResetToDefaultsCommand = ReactiveCommand
            .Create(() => SetAll(f => NpcRootFieldCatalog.Defaults.Contains(f)))
            .DisposeWith(_disposables);

        UpdateSummary();
    }

    public string ModDisplayName { get; }

    public HashSet<NpcRootField> GetSelection() =>
        Fields.Where(f => f.IsSelected).Select(f => f.Field).ToHashSet();

    private void SetAll(Func<NpcRootField, bool> predicate)
    {
        foreach (var row in Fields) row.IsSelected = predicate(row.Field);
    }

    private void UpdateSummary()
    {
        int selected = Fields.Count(f => f.IsSelected);
        SummaryText = selected == 0
            ? "Nothing selected — no dependent overrides will be discovered for this mod."
            : $"{selected} of {Fields.Count} fields selected" +
              (IsAtDefaults ? " (the default appearance set)." : ".");
    }

    private void ExecuteOk()
    {
        HasChanged = !_initialSelection.SetEquals(GetSelection());
        RequestClose?.Invoke();
    }

    private void ExecuteCancel()
    {
        HasChanged = false;
        RequestClose?.Invoke();
    }

    public void Dispose() => _disposables.Dispose();
}

/// <summary>One checkbox row: an <see cref="NpcRootFieldCatalog"/> entry plus its ticked state.</summary>
public class VM_SelectableRootField : ReactiveObject
{
    public VM_SelectableRootField(NpcRootFieldCatalog.Entry entry, bool isSelected)
    {
        Field = entry.Field;
        DisplayName = entry.DisplayName;
        IsAppearanceDefault = entry.OnByDefault;
        IsSelected = isSelected;
        ToolTip = entry.OnByDefault
            ? "Part of the default appearance set. Turning it off means this mod's edits to records " +
              "reachable only through this field are not carried into the output."
            : "Off by default. Tick it only if this mod's appearance depends on records reachable " +
              "through this field — rooting the search here can pull in unrelated records and copy " +
              "them into the output.";
    }

    public NpcRootField Field { get; }
    public string DisplayName { get; }
    public string ToolTip { get; }

    /// <summary>Lets the view mark the appearance set, so a non-default tick is visible at a glance.</summary>
    public bool IsAppearanceDefault { get; }

    [Reactive] public bool IsSelected { get; set; }
}
