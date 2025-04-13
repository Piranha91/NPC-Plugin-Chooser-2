// VM_Settings.cs (Updated)
using System.Windows;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Win32;
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
        private Settings _model;
        private readonly VM_NpcSelectionBar _npcSelectionBar; // To trigger re-initialization

        // Reactive properties bound to the UI
        [Reactive] public string ModsFolder { get; set; }
        [Reactive] public string MugshotsFolder { get; set; }
        [Reactive] public SkyrimRelease SkyrimRelease { get; set; }
        [Reactive] public string SkyrimGamePath { get; set; }
        [Reactive] public string OutputModName { get; set; }
        [Reactive] public bool ShowNpcDescriptions { get; set; }

        // Read-only properties reflecting environment state
        [ObservableAsProperty] public bool EnvironmentIsValid { get; }
        [ObservableAsProperty] public string EnvironmentErrorText { get; }


        // Commands for folder browsers
        public ReactiveCommand<Unit, Unit> SelectGameFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectModsFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectMugshotsFolderCommand { get; }


        public VM_Settings(EnvironmentStateProvider environmentStateProvider, Settings settings, VM_NpcSelectionBar npcSelectionBar)
        {
            _environmentStateProvider = environmentStateProvider;
            _npcSelectionBar = npcSelectionBar; // Inject NPC bar VM
            _model = settings;
            
            Application.Current.Exit += (_, __) => { SaveSettings(); };

            // Initialize VM properties from the model
            ModsFolder = _model.ModsFolder;
            MugshotsFolder = _model.MugshotsFolder;
            SkyrimRelease = _model.SkyrimRelease;
            SkyrimGamePath = _model.SkyrimGamePath;
            OutputModName = _model.OutputModName;
            ShowNpcDescriptions = _model.ShowNpcDescriptions;

            // Update model when VM properties change
            this.WhenAnyValue(x => x.ModsFolder).Subscribe(s => _model.ModsFolder = s);
            this.WhenAnyValue(x => x.MugshotsFolder).Subscribe(s => _model.MugshotsFolder = s);
            this.WhenAnyValue(x => x.ShowNpcDescriptions).Subscribe(b => _model.ShowNpcDescriptions = b);

            // Properties that trigger environment update
            this.WhenAnyValue(x => x.SkyrimGamePath).Subscribe(s =>
            {
                _model.SkyrimGamePath = s;
                _environmentStateProvider.DataFolderPath = s; // Update provider directly
                UpdateEnvironmentAndNotify();
            });
            this.WhenAnyValue(x => x.SkyrimRelease).Subscribe(r =>
            {
                _model.SkyrimRelease = r;
                _environmentStateProvider.SkyrimVersion = r; // Update provider directly
                UpdateEnvironmentAndNotify();
            });
            this.WhenAnyValue(x => x.OutputModName)
                .Where(x => !x.IsNullOrWhitespace()) // Prevent updating with invalid name
                .Subscribe(s =>
            {
                _model.OutputModName = s;
                 _environmentStateProvider.OutputModName = s; // Update provider directly
                 UpdateEnvironmentAndNotify();
            });

            // Expose environment state reactively
            this.WhenAnyValue(x => x._environmentStateProvider.EnvironmentIsValid)
                .ToPropertyEx(this, x => x.EnvironmentIsValid);

            this.WhenAnyValue(x => x._environmentStateProvider.EnvironmentBuilderError)
                 .Select(err => string.IsNullOrWhiteSpace(err) ? string.Empty : $"Environment Error: {err}") // Provide user-friendly text
                .ToPropertyEx(this, x => x.EnvironmentErrorText);


            // Folder Browser Commands
            SelectGameFolderCommand = ReactiveCommand.Create(SelectGameFolder);
            SelectModsFolderCommand = ReactiveCommand.Create(SelectModsFolder);
            SelectMugshotsFolderCommand = ReactiveCommand.Create(SelectMugshotsFolder);

             // Initial environment check
             UpdateEnvironmentAndNotify();
        }

        public static Settings LoadSettings()
        {
            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
            if (File.Exists(settingsPath))
            {
                var trialSettings =
                    JSONhandler<Settings>.LoadJSONFile(settingsPath, out bool success, out string exception);

                if (success)
                {
                    return trialSettings;
                }
                else
                {
                    // log the exception
                }

            }
            return new Settings();
        }
        
        private void SaveSettings()
        {
            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
            JSONhandler<Settings>.SaveJSONFile(_model, settingsPath, out bool success, out string exception);
        }

         private void UpdateEnvironmentAndNotify()
         {
            _environmentStateProvider.UpdateEnvironment();
            // Force ReactiveUI to re-evaluate properties bound to the provider's state
            this.RaisePropertyChanged(nameof(EnvironmentIsValid));
            this.RaisePropertyChanged(nameof(EnvironmentErrorText));

             // If the environment became valid/invalid, or critical paths changed,
             // re-initialize the NPC list.
             _npcSelectionBar.Initialize(); // Call Initialize on the NPC bar VM
         }


        private void SelectGameFolder()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select Skyrim Game Folder (e.g., Steam/.../Skyrim Special Edition)";
                dialog.SelectedPath = SkyrimGamePath; // Start from current setting
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    SkyrimGamePath = dialog.SelectedPath; // Triggers environment update via WhenAnyValue
                }
            }
        }

        private void SelectModsFolder()
        {
             // Implement if ModsFolder property is used
             using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select Mods Folder (e.g., MO2/mods)";
                dialog.SelectedPath = ModsFolder;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    ModsFolder = dialog.SelectedPath;
                }
            }
        }

        private void SelectMugshotsFolder()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select Mugshots Folder";
                dialog.SelectedPath = MugshotsFolder;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    MugshotsFolder = dialog.SelectedPath;
                    // Optional: Trigger NPC view update if image paths depend on this?
                     _npcSelectionBar.Initialize(); // Re-init might be needed to regenerate image paths
                }
            }
        }
    }
}