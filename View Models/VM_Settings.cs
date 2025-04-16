// VM_Settings.cs (Updated)

using System.Collections.ObjectModel;
using System.Windows;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Noggog; // For IsNullOrWhitespace
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_Settings : ReactiveObject
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _model; // Renamed from settings to _model for clarity
        private readonly VM_NpcSelectionBar _npcSelectionBar;
        private readonly VM_Mods _modListVM;

        // --- Existing & Modified Properties ---
        [Reactive] public string ModsFolder { get; set; }
        [Reactive] public string MugshotsFolder { get; set; }
        [Reactive] public SkyrimRelease SkyrimRelease { get; set; }
        [Reactive] public string SkyrimGamePath { get; set; }
        // OutputModName now maps to the conceptual name in the model
        [Reactive] public string OutputModName { get; set; }

        // --- New Output Settings Properties ---
        [Reactive] public string OutputDirectory { get; set; }
        [Reactive] public bool AppendTimestampToOutputDirectory { get; set; }
        [Reactive] public string OutputPluginName { get; set; } // New property for the actual plugin filename
        [Reactive] public PatchingMode SelectedPatchingMode { get; set; }
        public IEnumerable<PatchingMode> PatchingModes { get; } = Enum.GetValues(typeof(PatchingMode)).Cast<PatchingMode>();

        // --- New EasyNPC Transfer Properties ---
        // Assuming ModKeyMultiPicker binds directly to an ObservableCollection-like source
        // We will wrap the HashSet from the model. Consider a more robust solution if direct binding causes issues.
        [Reactive] public VM_ModSelector ExclusionSelectorViewModel { get; private set; }
        [ObservableAsProperty] public IEnumerable<ModKey> AvailablePluginsForExclusion { get; }

        // --- Read-only properties reflecting environment state ---
        [ObservableAsProperty] public bool EnvironmentIsValid { get; }
        [ObservableAsProperty] public string EnvironmentErrorText { get; }

        // --- Commands ---
        public ReactiveCommand<Unit, Unit> SelectGameFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectModsFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectMugshotsFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectOutputDirectoryCommand { get; } // New
        public ReactiveCommand<Unit, Unit> ShowPatchingModeHelpCommand { get; } // New
        public ReactiveCommand<Unit, Unit> ImportEasyNpcCommand { get; } // New
        public ReactiveCommand<Unit, Unit> ExportEasyNpcCommand { get; } // New

        public VM_Settings(EnvironmentStateProvider environmentStateProvider, Settings settings, VM_NpcSelectionBar npcSelectionBar, VM_Mods modListVM)
        {
            _environmentStateProvider = environmentStateProvider;
            _npcSelectionBar = npcSelectionBar;
            _modListVM = modListVM;
            _model = settings; // Use the injected model instance

            Application.Current.Exit += (_, __) => { SaveSettings(); };

            // Initialize VM properties from the model
            ModsFolder = _model.ModsFolder;
            MugshotsFolder = _model.MugshotsFolder;
            SkyrimRelease = _model.SkyrimRelease;
            SkyrimGamePath = _model.SkyrimGamePath;
            OutputPluginName = _model.OutputPluginName; 
            OutputDirectory = _model.OutputDirectory;
            AppendTimestampToOutputDirectory = _model.AppendTimestampToOutputDirectory;
            SelectedPatchingMode = _model.PatchingMode;
            ExclusionSelectorViewModel = new VM_ModSelector();
            ExclusionSelectorViewModel.LoadFromModel(_environmentStateProvider.LoadOrder.Keys, _model.EasyNpcDefaultPluginExclusions);

            // Update model when VM properties change
            this.WhenAnyValue(x => x.ModsFolder).Subscribe(s => _model.ModsFolder = s);
            this.WhenAnyValue(x => x.MugshotsFolder).Subscribe(s => _model.MugshotsFolder = s);
            this.WhenAnyValue(x => x.OutputDirectory).Subscribe(s => _model.OutputDirectory = s);
            this.WhenAnyValue(x => x.AppendTimestampToOutputDirectory).Subscribe(b => _model.AppendTimestampToOutputDirectory = b);
            this.WhenAnyValue(x => x.SelectedPatchingMode).Subscribe(pm => _model.PatchingMode = pm);

            // Properties that trigger environment update
            this.WhenAnyValue(x => x.SkyrimGamePath).Subscribe(s =>
            {
                _model.SkyrimGamePath = s;
                _environmentStateProvider.DataFolderPath = s;
                UpdateEnvironmentAndNotify();
            });
            this.WhenAnyValue(x => x.SkyrimRelease).Subscribe(r =>
            {
                _model.SkyrimRelease = r;
                _environmentStateProvider.SkyrimVersion = r;
                UpdateEnvironmentAndNotify();
            });
            this.WhenAnyValue(x => x.OutputPluginName) // Trigger on actual plugin name change
                .Where(x => !x.IsNullOrWhitespace())
                .Subscribe(s =>
                {
                    _model.OutputPluginName = s;
                    // Update the provider's understanding of the output mod's name
                    _environmentStateProvider.OutputPluginName = s;
                    UpdateEnvironmentAndNotify();
                });

            // Expose environment state reactively
            this.WhenAnyValue(x => x._environmentStateProvider.EnvironmentIsValid)
                .ToPropertyEx(this, x => x.EnvironmentIsValid);

            this.WhenAnyValue(x => x._environmentStateProvider.EnvironmentBuilderError)
                 .Select(err => string.IsNullOrWhiteSpace(err) ? string.Empty : $"Environment Error: {err}")
                .ToPropertyEx(this, x => x.EnvironmentErrorText);

            // Populate AvailablePluginsForExclusion (existing code...)
            this.WhenAnyValue(x => x.EnvironmentIsValid)
                .Select(_ => _environmentStateProvider.EnvironmentIsValid
                    ? _environmentStateProvider.LoadOrder.Keys.ToList()
                    : Enumerable.Empty<ModKey>())
                .ObserveOn(RxApp.MainThreadScheduler)
                .ToPropertyEx(this, x => x.AvailablePluginsForExclusion);

            // --- Update ModSelectorViewModel when available plugins change --- NEW
            this.WhenAnyValue(x => x.AvailablePluginsForExclusion)
                .Where(list => list != null)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(availablePlugins =>
                {
                    // --- Get the currently selected items from the UI's ViewModel ---
                    var currentUISelctions = ExclusionSelectorViewModel.SaveToModel();

                    // --- Reload using the current UI selections, not the model's ---
                    ExclusionSelectorViewModel.LoadFromModel(availablePlugins, currentUISelctions);
                });


            // Folder Browser Commands
            SelectGameFolderCommand = ReactiveCommand.Create(SelectGameFolder);
            SelectModsFolderCommand = ReactiveCommand.Create(SelectModsFolder);
            SelectMugshotsFolderCommand = ReactiveCommand.Create(SelectMugshotsFolder);
            SelectOutputDirectoryCommand = ReactiveCommand.Create(SelectOutputDirectory); // New
            ShowPatchingModeHelpCommand = ReactiveCommand.Create(ShowPatchingModeHelp); // New
            ImportEasyNpcCommand = ReactiveCommand.Create(ImportEasyNpc); // New
            ExportEasyNpcCommand = ReactiveCommand.Create(ExportEasyNpc); // New

            // Initial environment check
            UpdateEnvironmentAndNotify();
        }

        public static Settings LoadSettings()
        {
            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
            Settings loadedSettings = null;
            if (File.Exists(settingsPath))
            {
                 loadedSettings = JSONhandler<Settings>.LoadJSONFile(settingsPath, out bool success, out string exception);
                 if (!success)
                 {
                     MessageBox.Show($"Error loading settings from {settingsPath}:\n{exception}\n\nDefault settings will be used.", "Settings Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                     loadedSettings = new Settings(); // Use defaults on error
                 }
            }
            else
            {
                 loadedSettings = new Settings(); // Use defaults if file doesn't exist
            }

            // Ensure defaults for new/potentially missing fields after loading old file
            loadedSettings.OutputPluginName ??= "NPC.esp";
            loadedSettings.OutputDirectory ??= default;
            // AppendTimestampToOutputDirectory default is false (bool default)
            // PatchingMode default is Default (enum default)
            loadedSettings.EasyNpcDefaultPluginExclusions ??= new() { ModKey.FromFileName("Synthesis.esp") };


            return loadedSettings;
        }

        private void SaveSettings()
        {
            _model.EasyNpcDefaultPluginExclusions = new HashSet<ModKey>(ExclusionSelectorViewModel.SaveToModel());

            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
            JSONhandler<Settings>.SaveJSONFile(_model, settingsPath, out bool success, out string exception);
             if (!success)
             {
                  // Maybe show a non-modal warning or log? Saving happens on exit.
                  System.Diagnostics.Debug.WriteLine($"ERROR saving settings: {exception}");
             }
        }

         private void UpdateEnvironmentAndNotify()
         {
            // Crucially, ensure the provider uses the *correct* name for the output file
            _environmentStateProvider.OutputPluginName = _model.OutputPluginName;
            _environmentStateProvider.UpdateEnvironment();

            this.RaisePropertyChanged(nameof(EnvironmentIsValid));
            this.RaisePropertyChanged(nameof(EnvironmentErrorText));

            _npcSelectionBar.Initialize();
            _modListVM.PopulateModSettings();
            _modListVM.ApplyFilters(); // Re-apply filters after mods repopulate
         }

        // --- Folder Selection Methods ---
        private void SelectGameFolder()
        {
            // Use WindowsAPICodePack for a modern dialog
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select Skyrim Game Folder",
                InitialDirectory = GetSafeInitialDirectory(SkyrimGamePath)
            };

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                SkyrimGamePath = dialog.FileName;
            }
        }

        private void SelectModsFolder()
        {
             var dialog = new CommonOpenFileDialog
             {
                 IsFolderPicker = true,
                 Title = "Select Mods Folder (e.g., MO2 Mods Path)",
                 InitialDirectory = GetSafeInitialDirectory(ModsFolder)
             };

             if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
             {
                 ModsFolder = dialog.FileName;
                 _modListVM.PopulateModSettings(); // Refresh mods list view
                 _modListVM.ApplyFilters();
             }
        }

        private void SelectMugshotsFolder()
        {
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select Mugshots Folder",
                InitialDirectory = GetSafeInitialDirectory(MugshotsFolder)
            };

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                MugshotsFolder = dialog.FileName;
                _npcSelectionBar.Initialize();
                _modListVM.PopulateModSettings();
                _modListVM.ApplyFilters();
            }
        }

        private void SelectOutputDirectory() // New
        {
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select Output Directory",
                 // Start in current output directory, or My Documents if not set/invalid
                InitialDirectory = GetSafeInitialDirectory(OutputDirectory, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
            };

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                OutputDirectory = dialog.FileName;
            }
        }

        // Helper to get a valid initial directory for folder pickers
        private string GetSafeInitialDirectory(string preferredPath, string fallbackPath = null)
        {
            if (!string.IsNullOrWhiteSpace(preferredPath) && Directory.Exists(preferredPath))
            {
                return preferredPath;
            }
            if (!string.IsNullOrWhiteSpace(fallbackPath) && Directory.Exists(fallbackPath))
            {
                return fallbackPath;
            }
            // Final fallback if neither preferred nor specific fallback exists
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }


        // --- Placeholder Command Handlers ---
        private void ShowPatchingModeHelp() // New
        {
            string helpText = @"Patching Mode Help:

Default:
NPC Plugins are imported directly from their selected Appearance Mods.
When the output plugin is generated, place it as high as it will go in your load order, and then use Synthesis FaceFixer (or manually perform conflict resolution) to forward the faces to your final load order.

EasyNPC-Like:
NPC plugins are imported from their conflict-winning override in your load order, and their appearance is modified to match their selected Appearance Mod.
When the ouptut plugin is generated, put it at the end of your load order.";

            MessageBox.Show(helpText, "Patching Mode Information", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ImportEasyNpc() // New Placeholder
        {
            MessageBox.Show("Import from EasyNPC - Not yet implemented.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            // Future implementation here
        }

        private void ExportEasyNpc() // New Placeholder
        {
             MessageBox.Show("Export to EasyNPC - Not yet implemented.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            // Future implementation here
        }
    }
}