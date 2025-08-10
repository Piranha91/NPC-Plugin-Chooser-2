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
        public string? SelectedModName { get; }
        public FormKey SourceNpcFormKey { get; } // Can be .Null

        public NpcSelectionChangedEventArgs(FormKey npcFormKey, string? selectedModName, FormKey sourceNpcFormKey)
        {
            NpcFormKey = npcFormKey;
            SelectedModName = selectedModName;
            SourceNpcFormKey = sourceNpcFormKey;
        }
    }

    // Manages the state of which mod is selected for each NPC
    public class NpcConsistencyProvider
    {
        private readonly Settings _settingsModel;
        private readonly Dictionary<FormKey, (string ModName, FormKey NpcFormKey)> _selectedMods; // Internal cache
        private readonly Lazy<VM_Settings> _lazyVmSettings;

        // Observable to notify when a selection changes
        private readonly Subject<NpcSelectionChangedEventArgs> _npcSelectionChanged = new();
        public IObservable<NpcSelectionChangedEventArgs> NpcSelectionChanged => _npcSelectionChanged;

        public NpcConsistencyProvider(Settings settings, Lazy<VM_Settings> lazyVmSettings)
        {
            _settingsModel = settings;
            _lazyVmSettings = lazyVmSettings;
            _selectedMods = new Dictionary<FormKey, (string, FormKey)>(_settingsModel.SelectedAppearanceMods ?? new());
        }

        public void SetSelectedMod(FormKey npcFormKey, string selectedMod, FormKey sourceNpcFormKey)
        {
            if (string.IsNullOrEmpty(selectedMod)) return;

            bool changed = false;
            var newSelection = (ModName: selectedMod, NpcFormKey: sourceNpcFormKey);
            if (!_selectedMods.TryGetValue(npcFormKey, out var currentSelection) ||
                !currentSelection.Equals(newSelection))
            {
                _selectedMods[npcFormKey] = newSelection;
                _settingsModel.SelectedAppearanceMods[npcFormKey] = newSelection;
                changed = true;
            }

            if (changed)
            {
                _npcSelectionChanged.OnNext(new NpcSelectionChangedEventArgs(npcFormKey, selectedMod,
                    sourceNpcFormKey));
                _lazyVmSettings.Value?.RequestThrottledSave();
            }
        }

        public void ClearSelectedMod(FormKey npcFormKey)
        {
            bool changed = false;
            if (_selectedMods.ContainsKey(npcFormKey))
            {
                _selectedMods.Remove(npcFormKey);
                _settingsModel.SelectedAppearanceMods.Remove(npcFormKey);
                changed = true;
            }

            if (changed)
            {
                _npcSelectionChanged.OnNext(new NpcSelectionChangedEventArgs(npcFormKey, null, FormKey.Null));
                _lazyVmSettings.Value?.RequestThrottledSave();
            }
        }

        public void ClearAllSelections()
        {
            var keysToClear = new List<FormKey>(_selectedMods.Keys);

            _selectedMods.Clear();
            _settingsModel.SelectedAppearanceMods.Clear();

            foreach (var npcFormKey in keysToClear)
            {
                _npcSelectionChanged.OnNext(new NpcSelectionChangedEventArgs(npcFormKey, null, FormKey.Null));
            }

            _lazyVmSettings.Value?.RequestThrottledSave();
        }

        public (string? ModName, FormKey SourceNpcFormKey) GetSelectedMod(FormKey npcFormKey)
        {
            if (_selectedMods.TryGetValue(npcFormKey, out var selectedMod))
            {
                return (selectedMod.ModName, selectedMod.NpcFormKey);
            }

            return (null, FormKey.Null);
        }

        public bool IsModSelected(FormKey npcFormKey, string modToCheck, FormKey sourceNpcFormKey)
        {
            var (selectedModName, selectedSourceKey) = GetSelectedMod(npcFormKey);
            return selectedModName == modToCheck && selectedSourceKey.Equals(sourceNpcFormKey);
        }
    }
}