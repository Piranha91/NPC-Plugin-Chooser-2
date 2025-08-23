// VM_MainWindow.cs
using NPC_Plugin_Chooser_2.BackEnd; // Added for EnvironmentStateProvider
using NPC_Plugin_Chooser_2.Models; // Added for Settings
using NPC_Plugin_Chooser_2.Views; // Not strictly needed if using ViewModelViewHost
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Reactive.Linq;
using System.IO; // Added for Directory.Exists

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_MainWindow : ReactiveObject
    {
        // Injected ViewModels for the different tabs
        private readonly VM_NpcSelectionBar _npcsViewModel;
        private readonly VM_Mods _modsViewModel;
        private readonly VM_Summary _summaryViewModel;
        private readonly VM_Settings _settingsViewModel;
        private readonly VM_Run _runViewModel;

        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _settings;

        // The currently displayed ViewModel in the content area
        [Reactive] public ReactiveObject? CurrentViewModel { get; private set; }

        // Properties to control which tab is selected (bound to RadioButtons)
        [Reactive] public bool IsNpcsTabSelected { get; set; } // Default tab
        [Reactive] public bool IsModsTabSelected { get; set; }
        [Reactive] public bool IsSummaryTabSelected { get; set; }
        [Reactive] public bool IsSettingsTabSelected { get; set; }
        [Reactive] public bool IsRunTabSelected { get; set; }
        [Reactive] public bool AreOtherTabsEnabled { get; private set; }
        [Reactive] public bool IsLoadingFolders { get; set; } = false;

        public VM_MainWindow(
            VM_NpcSelectionBar npcsViewModel,
            VM_Mods modsViewModel,
            VM_Summary summaryViewModel,
            VM_Settings settingsViewModel,
            VM_Run runViewModel,
            EnvironmentStateProvider environmentStateProvider,
            Settings settings)
        {
            _npcsViewModel = npcsViewModel;
            _modsViewModel = modsViewModel;
            _summaryViewModel = summaryViewModel;
            _settingsViewModel = settingsViewModel;
            _runViewModel = runViewModel;
            _environmentStateProvider = environmentStateProvider;
            _settings = settings;

            AreOtherTabsEnabled = false; // Default to false, InitializeApplicationState will set it.

            // Reactive handling for conditions changing *after* initial setup
            Observable.CombineLatest(
                    _environmentStateProvider.WhenAnyValue(x => x.Status),
                    _settingsViewModel.WhenAnyValue(x => x.ModsFolder),
                    this.WhenAnyValue(x => x.IsLoadingFolders), // <-- Observe the new property
                    (status, _, isLoading) => // <-- Capture the new 'isLoading' value
                        status == EnvironmentStateProvider.EnvironmentStatus.Valid &&
                        !string.IsNullOrWhiteSpace(_settings.ModsFolder) &&
                        Directory.Exists(_settings.ModsFolder) &&
                        !isLoading // <-- Add this condition
                )
                .Skip(1)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(conditionsMet =>
                {
                    // This logic remains the same, but now respects the loading flag
                    AreOtherTabsEnabled = conditionsMet;
                    if (!conditionsMet && CurrentViewModel != _settingsViewModel)
                    {
                        CurrentViewModel = _settingsViewModel;
                        IsSettingsTabSelected = true;
                        IsNpcsTabSelected = false;
                        IsModsTabSelected = false;
                        IsRunTabSelected = false;
                    }
                });

            // Change CurrentViewModel based on which tab is selected
            this.WhenAnyValue(x => x.IsNpcsTabSelected)
                .Where(isSelected => isSelected && AreOtherTabsEnabled)
                .Subscribe(_ => { if (CurrentViewModel != _npcsViewModel) CurrentViewModel = _npcsViewModel; });

            this.WhenAnyValue(x => x.IsModsTabSelected)
                .Where(isSelected => isSelected && AreOtherTabsEnabled)
                .Subscribe(_ => { if (CurrentViewModel != _modsViewModel) CurrentViewModel = _modsViewModel; });
            
            this.WhenAnyValue(x => x.IsSummaryTabSelected)
                .Where(isSelected => isSelected && AreOtherTabsEnabled)
                .Subscribe(_ => { if (CurrentViewModel != _summaryViewModel) CurrentViewModel = _summaryViewModel; });

            this.WhenAnyValue(x => x.IsSettingsTabSelected)
                .Where(isSelected => isSelected)
                .Subscribe(_ => { if (CurrentViewModel != _settingsViewModel) CurrentViewModel = _settingsViewModel; });

            this.WhenAnyValue(x => x.IsRunTabSelected)
                .Where(isSelected => isSelected && AreOtherTabsEnabled)
                .Subscribe(_ => { if (CurrentViewModel != _runViewModel) CurrentViewModel = _runViewModel; });
        }

        public void InitializeApplicationState(bool isStartup)
        {
            bool gameEnvironmentCanBeCreated = _environmentStateProvider.Status == EnvironmentStateProvider.EnvironmentStatus.Valid;
            bool modsFolderIsValid = !string.IsNullOrWhiteSpace(_settings.ModsFolder) && Directory.Exists(_settings.ModsFolder);
            bool conditionsMet = gameEnvironmentCanBeCreated && modsFolderIsValid;

            AreOtherTabsEnabled = conditionsMet;

            if (conditionsMet)
            {
                if (isStartup)
                {
                    CurrentViewModel = _npcsViewModel;
                    IsNpcsTabSelected = true;
                    IsModsTabSelected = false;
                    IsSettingsTabSelected = false;
                    IsRunTabSelected = false;
                }
                // If not startup and conditions are met:
                // - If current view is Settings, user can now navigate away.
                // - If current view is already another (valid) tab, stay there.
                // No automatic switch out of Settings if not startup.
            }
            else // Conditions NOT met
            {
                // Force to Settings view if not already there, or if startup.
                if (CurrentViewModel != _settingsViewModel || isStartup)
                {
                    CurrentViewModel = _settingsViewModel;
                    IsSettingsTabSelected = true;
                }
                // Ensure other tabs are deselected, regardless of startup state,
                // as conditions are not met.
                IsNpcsTabSelected = false;
                IsModsTabSelected = false;
                IsRunTabSelected = false;
            }
        }
    }
}