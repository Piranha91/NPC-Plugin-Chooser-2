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
        public string SelectedMod { get; }

        public NpcSelectionChangedEventArgs(FormKey npcFormKey,string selectedMod)
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

        public string GetSelectedMod(FormKey npcFormKey, ModKey? defaultModKey = null)
        {
            if (_selectedMods.TryGetValue(npcFormKey, out var selectedModKey))
            {
                return selectedModKey.ToString();
            }
            // If no selection is stored and a default (e.g., the winning override) is provided, use it.
             if (defaultModKey.HasValue)
             {
                 return defaultModKey.Value.ToString();
             }
             // This case should ideally not happen if initialization is correct,
             // but return something sensible. Maybe the NPC's original modkey?
             return npcFormKey.ModKey.ToString(); // Fallback to the NPC's definition mod
        }

        public bool IsModSelected(FormKey npcFormKey, string modToCheck)
        {
            return GetSelectedMod(npcFormKey) == modToCheck;
        }

        // Add SaveSettings method if needed
        // public void SaveSettings() { /* Implement saving _settings */ }
    }
}