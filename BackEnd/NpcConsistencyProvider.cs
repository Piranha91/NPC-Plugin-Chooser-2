using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects; // For Subject
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models; // For Settings (to load/save)
using ReactiveUI; // For MessageBus or Subjects

namespace NPC_Plugin_Chooser_2.BackEnd
{
    // Arguments for the selection changed event
    public class NpcSelectionChangedEventArgs : EventArgs
    {
        public FormKey NpcFormKey { get; }
        public string? SelectedMod { get; }

        public NpcSelectionChangedEventArgs(FormKey npcFormKey, string? selectedMod)
        {
            NpcFormKey = npcFormKey;
            SelectedMod = selectedMod;
        }
    }

    // Manages the state of which mod is selected for each NPC
    public class NpcConsistencyProvider
    {
        private readonly Settings _settings;
        private readonly Dictionary<FormKey, string> _selectedMods; // Internal cache

        // Observable to notify when a selection changes
        private readonly Subject<NpcSelectionChangedEventArgs> _npcSelectionChanged = new Subject<NpcSelectionChangedEventArgs>();
        public IObservable<NpcSelectionChangedEventArgs> NpcSelectionChanged => _npcSelectionChanged;


        public NpcConsistencyProvider(Settings settings)
        {
            _settings = settings;
            // Initialize from persistent settings
            _selectedMods = new Dictionary<FormKey, string>(_settings.SelectedAppearanceMods ?? new());
        }

        public void SetSelectedMod(FormKey npcFormKey, string selectedMod)
        {
            // Prevent setting null/empty string via this method, use ClearSelectedMod instead
            if (string.IsNullOrEmpty(selectedMod)) return;
            
            bool changed = false;
            if (!_selectedMods.TryGetValue(npcFormKey, out var currentModKey) || currentModKey != selectedMod)
            {
                _selectedMods[npcFormKey] = selectedMod;
                _settings.SelectedAppearanceMods[npcFormKey] = selectedMod; // Update persistent settings
                changed = true;
            }

            if (changed)
            {
                // Notify subscribers
                _npcSelectionChanged.OnNext(new NpcSelectionChangedEventArgs(npcFormKey, selectedMod));
                // Consider adding save logic here or elsewhere periodically/on exit
                 // SaveSettings();
            }
        }
        
        /// <summary>
        /// Clears the selected appearance mod for the specified NPC.
        /// </summary>
        /// <param name="npcFormKey">The FormKey of the NPC whose selection should be cleared.</param>
        public void ClearSelectedMod(FormKey npcFormKey)
        {
            bool changed = false;
            if (_selectedMods.ContainsKey(npcFormKey))
            {
                _selectedMods.Remove(npcFormKey);
                // Also remove from the persistent settings model
                _settings.SelectedAppearanceMods.Remove(npcFormKey);
                changed = true;
            }

            if (changed)
            {
                // Notify subscribers, passing null for selectedMod to indicate deselection
                _npcSelectionChanged.OnNext(new NpcSelectionChangedEventArgs(npcFormKey, null));
            }
        }

        public string? GetSelectedMod(FormKey npcFormKey) // Return nullable string
        {
            if (_selectedMods.TryGetValue(npcFormKey, out var selectedModKey))
            {
                return selectedModKey;
            }
            return null; // Return null if no specific selection exists
        }

        public bool IsModSelected(FormKey npcFormKey, string modToCheck)
        {
            return GetSelectedMod(npcFormKey) == modToCheck;
        }

        // Add SaveSettings method if needed
        // public void SaveSettings() { /* Implement saving _settings */ }
    }
}