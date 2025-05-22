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
        /// Preserves the order of availableModKeys. Appends selected keys not present in available keys.
        /// </summary>
        /// <param name="availableModKeys">All mods that should be displayed in the list, in the desired order.</param>
        /// <param name="initiallySelectedModKeys">Mods that should be checked initially.</param>
        public void LoadFromModel(IEnumerable<ModKey> availableModKeys, IEnumerable<ModKey> initiallySelectedModKeys)
        {
            SelectableMods.Clear();

            // Handle null inputs gracefully
            var available = availableModKeys?.ToList() ?? new List<ModKey>();
            var initialSelected = initiallySelectedModKeys?.ToList() ?? new List<ModKey>();

            // Use a HashSet for efficient lookup and removal of selected keys that are processed
            var selectedSet = new HashSet<ModKey>(initialSelected);

            // --- Process available mods, preserving order (Requirement 1) ---
            foreach (var modKey in available) // Iterate directly over the input list/enumerable
            {
                bool isSelected = selectedSet.Contains(modKey);
                // Create VM, explicitly marking it as NOT missing
                var vm = new VM_SelectableMod(modKey, isSelected, isMissing: false);
                SelectableMods.Add(vm);

                // If this available mod was in the initial selection, remove it from the set
                // so we know which selected mods were *not* found in the available list later.
                if (isSelected)
                {
                    selectedSet.Remove(modKey);
                }
            }

            // --- Process initially selected mods that were NOT in the available list (Requirement 2) ---
            // Any keys remaining in selectedSet are the ones that were selected but not available
            foreach (var missingKey in selectedSet)
            {
                // Create a VM for the missing key. It's selected by definition, and marked as missing.
                var vm = new VM_SelectableMod(missingKey, isSelected: true, isMissing: true);
                SelectableMods.Add(vm); // Add to the *end* of the list
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