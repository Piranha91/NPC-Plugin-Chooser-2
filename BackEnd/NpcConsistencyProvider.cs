using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects; // For Subject
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models; // For Settings (to load/save)
using NPC_Plugin_Chooser_2.View_Models; // For Settings Save
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
        private readonly Settings _settingsModel; 
        private readonly Dictionary<FormKey, string> _selectedMods; // Internal cache
        private readonly Lazy<VM_Settings> _lazyVmSettings; // Direct reference to VM_Settings

        // Observable to notify when a selection changes
        private readonly Subject<NpcSelectionChangedEventArgs> _npcSelectionChanged = new Subject<NpcSelectionChangedEventArgs>();
        public IObservable<NpcSelectionChangedEventArgs> NpcSelectionChanged => _npcSelectionChanged;


        public NpcConsistencyProvider(Settings settings, Lazy<VM_Settings> lazyVmSettings)
        {
            _settingsModel  = settings;
            _lazyVmSettings = lazyVmSettings;
            // Initialize from persistent settings
            _selectedMods = new Dictionary<FormKey, string>(_settingsModel .SelectedAppearanceMods ?? new());
        }

        public void SetSelectedMod(FormKey npcFormKey, string selectedMod)
        {
            // Prevent setting null/empty string via this method, use ClearSelectedMod instead
            if (string.IsNullOrEmpty(selectedMod)) return;
            
            bool changed = false;
            if (!_selectedMods.TryGetValue(npcFormKey, out var currentModKey) || currentModKey != selectedMod)
            {
                _selectedMods[npcFormKey] = selectedMod;
                _settingsModel .SelectedAppearanceMods[npcFormKey] = selectedMod; // Update persistent settings
                changed = true;
            }

            if (changed)
            {
                // Notify subscribers
                _npcSelectionChanged.OnNext(new NpcSelectionChangedEventArgs(npcFormKey, selectedMod));
                var vmSettings = _lazyVmSettings.Value;
                if (vmSettings != null)
                {
                    vmSettings.RequestThrottledSave();
                }
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
                _settingsModel .SelectedAppearanceMods.Remove(npcFormKey);
                changed = true;
            }

            if (changed)
            {
                // Notify subscribers, passing null for selectedMod to indicate deselection
                _npcSelectionChanged.OnNext(new NpcSelectionChangedEventArgs(npcFormKey, null));
                var vmSettings = _lazyVmSettings.Value;
                if (vmSettings != null)
                {
                    vmSettings.RequestThrottledSave();
                }
            }
        }
        
        /// <summary>
        /// Clears all selected appearance mods.
        /// </summary>
        public void ClearAllSelections()
        {
            // Make a copy of the keys to notify subscribers for each deselection
            var keysToClear = new List<FormKey>(_selectedMods.Keys);

            _selectedMods.Clear();
            _settingsModel.SelectedAppearanceMods.Clear();

            // Notify subscribers for each NPC that was deselected
            foreach (var npcFormKey in keysToClear)
            {
                _npcSelectionChanged.OnNext(new NpcSelectionChangedEventArgs(npcFormKey, null));
            }
    
            var vmSettings = _lazyVmSettings.Value;
            if (vmSettings != null)
            {
                vmSettings.RequestThrottledSave();
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