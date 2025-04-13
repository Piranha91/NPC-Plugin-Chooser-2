// VM_MainWindow.cs
using NPC_Plugin_Chooser_2.Views; // Not strictly needed if using ViewModelViewHost
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Reactive.Linq;

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_MainWindow : ReactiveObject
    {
        // Injected ViewModels for the different tabs
        public VM_NpcSelectionBar NpcsViewModel { get; }
        public VM_Mods ModsViewModel { get; } // *** NEW ***
        public VM_Settings SettingsViewModel { get; }
        public VM_Run RunViewModel { get; }

        // The currently displayed ViewModel in the content area
        [Reactive] public ReactiveObject? CurrentViewModel { get; private set; }

        // Properties to control which tab is selected (bound to RadioButtons)
        [Reactive] public bool IsNpcsTabSelected { get; set; } = true; // Default tab
        [Reactive] public bool IsModsTabSelected { get; set; } // *** NEW ***
        [Reactive] public bool IsSettingsTabSelected { get; set; }
        [Reactive] public bool IsRunTabSelected { get; set; }

        // *** Update Constructor Signature ***
        public VM_MainWindow(VM_NpcSelectionBar npcsViewModel, VM_Mods modsViewModel, VM_Settings settingsViewModel, VM_Run runViewModel)
        {
            NpcsViewModel = npcsViewModel;
            ModsViewModel = modsViewModel; // *** NEW ***
            SettingsViewModel = settingsViewModel;
            RunViewModel = runViewModel;

            // Set the initial view
            CurrentViewModel = NpcsViewModel;

            // Change CurrentViewModel based on which tab is selected
            this.WhenAnyValue(x => x.IsNpcsTabSelected)
                .Where(isSelected => isSelected)
                .Subscribe(_ => CurrentViewModel = NpcsViewModel);

            // *** NEW: Handle Mods Tab Selection ***
            this.WhenAnyValue(x => x.IsModsTabSelected)
                .Where(isSelected => isSelected)
                .Subscribe(_ => CurrentViewModel = ModsViewModel);

            this.WhenAnyValue(x => x.IsSettingsTabSelected)
                .Where(isSelected => isSelected)
                .Subscribe(_ => CurrentViewModel = SettingsViewModel);

            this.WhenAnyValue(x => x.IsRunTabSelected)
                .Where(isSelected => isSelected)
                .Subscribe(_ => CurrentViewModel = RunViewModel);
        }
    }
}