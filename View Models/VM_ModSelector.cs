// In View_Models/VM_ModSelector.cs
using Mutagen.Bethesda.Plugins;
using ReactiveUI;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_ModSelector : ReactiveObject
    {
        public ObservableCollection<VM_SelectableMod> SelectableMods { get; } = new();

        /// <summary>
        /// Loads the selector with available mods, marking those currently selected.
        /// Sorts the final list based on orderByModKeys, with any others sorted alphabetically at the end.
        /// </summary>
        /// <param name="availableModKeys">All mods that should be displayed in the list.</param>
        /// <param name="initiallySelectedModKeys">Mods that should be checked initially.</param>
        /// <param name="orderByModKeys">A list defining the desired sort order.</param>
        public void LoadFromModel(IEnumerable<ModKey> availableModKeys, IEnumerable<ModKey> initiallySelectedModKeys, List<ModKey> orderByModKeys)
        {
            SelectableMods.Clear();

            // Handle null inputs gracefully
            var available = availableModKeys?.ToList() ?? new List<ModKey>();
            var initialSelected = initiallySelectedModKeys?.ToList() ?? new List<ModKey>();
            var order = orderByModKeys ?? new List<ModKey>();

            var selectedSet = new HashSet<ModKey>(initialSelected);
            var allMods = new List<VM_SelectableMod>();

            // --- Step 1: Create VM for all available mods ---
            foreach (var modKey in available)
            {
                bool isSelected = selectedSet.Contains(modKey);
                var vm = new VM_SelectableMod(modKey, isSelected, isMissing: false);
                allMods.Add(vm);

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
                allMods.Add(vm);
            }

            // --- Step 3: Sort the combined list ---
            // Create a dictionary for efficient order lookup.
            var orderMap = order
                .Select((modKey, index) => new { modKey, index })
                .ToDictionary(item => item.modKey, item => item.index);

            // Sort the list. Get the index from the map if it exists; otherwise, use a large number
            // to push it to the end. Unordered items are then sorted alphabetically.
            var sortedMods = allMods
                .OrderBy(vm => orderMap.TryGetValue(vm.ModKey, out var index) ? index : int.MaxValue)
                .ThenBy(vm => vm.ModKey.ToString());

            // --- Step 4: Populate the final ObservableCollection ---
            foreach (var vm in sortedMods)
            {
                SelectableMods.Add(vm);
            }
        }

        /// <summary>
        /// Returns a list of CorrespondingModKeys corresponding to the currently selected items.
        /// </summary>
        /// <returns>A List containing the CorrespondingModKeys of selected items.</returns>
        public List<ModKey> SaveToModel()
        {
            return SelectableMods
                .Where(vm => vm.IsSelected)
                .Select(vm => vm.ModKey)
                .ToList();
        }
    }
}