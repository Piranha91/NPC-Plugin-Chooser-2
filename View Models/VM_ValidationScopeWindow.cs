using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Mutagen.Bethesda.Plugins;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>One NPC (with an appearance selection) shown in the validation scope picker.</summary>
public sealed class VM_ValidationScopeItem : ReactiveObject
{
    public FormKey FormKey { get; }
    public string DisplayName { get; }
    public string FormKeyText { get; }
    public string SelectedMod { get; }

    [Reactive] public bool IsChecked { get; set; }

    public VM_ValidationScopeItem(FormKey formKey, string displayName, string selectedMod)
    {
        FormKey = formKey;
        DisplayName = displayName;
        FormKeyText = formKey.ToString();
        SelectedMod = selectedMod;
    }
}

/// <summary>
/// Backs the "which NPCs to validate" dialog: either all NPCs with selections, or an
/// explicit, searchable subset the user checks.
/// </summary>
public sealed class VM_ValidationScopeWindow : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly List<VM_ValidationScopeItem> _allItems;

    [Reactive] public bool ScopeAllSelected { get; set; } = true;
    [Reactive] public bool ScopeSubset { get; set; }
    [Reactive] public string SearchText { get; set; } = string.Empty;

    public ObservableCollection<VM_ValidationScopeItem> FilteredItems { get; } = new();
    public string SummaryText { get; }

    public ReactiveCommand<Unit, Unit> CheckAllVisibleCommand { get; }
    public ReactiveCommand<Unit, Unit> UncheckAllVisibleCommand { get; }

    public VM_ValidationScopeWindow(IEnumerable<VM_ValidationScopeItem> items)
    {
        _allItems = items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        SummaryText = $"{_allItems.Count} NPC(s) have appearance selections.";

        ApplyFilter();

        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter())
            .DisposeWith(_disposables);

        // Keep the two radio-bound bools mutually exclusive even on programmatic change.
        this.WhenAnyValue(x => x.ScopeSubset)
            .Subscribe(sub => { if (sub) ScopeAllSelected = false; })
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.ScopeAllSelected)
            .Subscribe(all => { if (all) ScopeSubset = false; })
            .DisposeWith(_disposables);

        CheckAllVisibleCommand = ReactiveCommand.Create(() =>
        {
            foreach (var item in FilteredItems) item.IsChecked = true;
            if (FilteredItems.Count > 0) ScopeSubset = true; // bulk-checking implies subset scope
        }).DisposeWith(_disposables);

        UncheckAllVisibleCommand = ReactiveCommand.Create(() =>
        {
            foreach (var item in FilteredItems) item.IsChecked = false;
        }).DisposeWith(_disposables);
    }

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        IEnumerable<VM_ValidationScopeItem> query = _allItems;
        var s = SearchText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(i =>
                i.DisplayName.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                i.FormKeyText.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                i.SelectedMod.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var item in query) FilteredItems.Add(item);
    }

    /// <summary>The FormKeys the user chose to validate (all, or the checked subset).</summary>
    public IReadOnlyList<FormKey> GetChosenFormKeys()
    {
        if (ScopeAllSelected) return _allItems.Select(i => i.FormKey).ToList();
        return _allItems.Where(i => i.IsChecked).Select(i => i.FormKey).ToList();
    }

    public void Dispose() => _disposables.Dispose();
}
