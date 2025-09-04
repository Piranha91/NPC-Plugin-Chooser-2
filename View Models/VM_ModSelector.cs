using Mutagen.Bethesda.Plugins;
using ReactiveUI;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System;
using System.Reactive.Disposables;
using ReactiveUI.Fody.Helpers;
using System.Reactive.Linq;

namespace NPC_Plugin_Chooser_2.View_Models;

public class VM_ModSelector : ReactiveObject, IDisposable
{
    private List<VM_SelectableMod> _allMods = new();
    private List<ModKey> _masterSortOrder = new();
    private readonly CompositeDisposable _disposables = new();

    [Reactive] public string FilterText { get; set; } = string.Empty;
    public ObservableCollection<VM_SelectableMod> SelectableMods { get; } = new();

    public VM_ModSelector()
    {
        this.WhenAnyValue(x => x.FilterText)
            .Throttle(TimeSpan.FromMilliseconds(200), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter()).DisposeWith(_disposables);
    }

    private void ApplyFilter()
    {
        SelectableMods.Clear();

        var filtered = string.IsNullOrWhiteSpace(FilterText)
            ? _allMods
            : _allMods.Where(vm => vm.DisplayText.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

        var orderMap = _masterSortOrder
            .Select((modKey, index) => new { modKey, index })
            .ToDictionary(item => item.modKey, item => item.index);

        var sortedMods = filtered
            .OrderBy(vm => orderMap.TryGetValue(vm.ModKey, out var index) ? index : int.MaxValue)
            .ThenBy(vm => vm.ModKey.ToString());

        foreach (var vm in sortedMods)
        {
            SelectableMods.Add(vm);
        }
    }

    /// <summary>
    /// Loads the selector with available mods, marking those currently selected.
    /// Sorts the final list based on orderByModKeys, with any others sorted alphabetically at the end.
    /// </summary>
    /// <param name="availableModKeys">All mods that should be displayed in the list.</param>
    /// <param name="initiallySelectedModKeys">Mods that should be checked initially.</param>
    /// <param name="orderByModKeys">A list defining the desired sort order.</param>
    public void LoadFromModel(IEnumerable<ModKey> availableModKeys, IEnumerable<ModKey> initiallySelectedModKeys,
        List<ModKey> orderByModKeys)
    {
        _allMods.Clear();

        // Handle null inputs gracefully
        var available = availableModKeys?.ToList() ?? new List<ModKey>();
        var initialSelected = initiallySelectedModKeys?.ToList() ?? new List<ModKey>();
        _masterSortOrder = orderByModKeys ?? new List<ModKey>();

        var selectedSet = new HashSet<ModKey>(initialSelected);
        var allModsList = new List<VM_SelectableMod>();

        // --- Step 1: Create VM for all available mods ---
        foreach (var modKey in available)
        {
            bool isSelected = selectedSet.Contains(modKey);
            var vm = new VM_SelectableMod(modKey, isSelected, isMissing: false);
            allModsList.Add(vm);

            // Remove from set to later identify missing mods
            if (isSelected)
            {
                selectedSet.Remove(modKey);
            }
        }

        // --- Step 2: Create VM for selected mods that were not in the available list ---
        foreach (var missingKey in selectedSet)
        {
            var vm = new VM_SelectableMod(missingKey, isSelected: true, isMissing: true);
            allModsList.Add(vm);
        }

        _allMods = allModsList;

        // --- Step 3: Populate the collection with the initial filter/sort ---
        ApplyFilter();
    }

    /// <summary>
    /// Returns a list of CorrespondingModKeys corresponding to the currently selected items.
    /// </summary>
    /// <returns>A List containing the CorrespondingModKeys of selected items.</returns>
    public List<ModKey> SaveToModel()
    {
        return _allMods
            .Where(vm => vm.IsSelected)
            .Select(vm => vm.ModKey)
            .ToList();
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}