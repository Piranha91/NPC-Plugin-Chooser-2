// View Models/VM_ModSetting.cs
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq; // Needed for Select and ObservableAsPropertyHelper
using System.Windows.Forms; // For FolderBrowserDialog
using Mutagen.Bethesda.Plugins;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Linq;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_ModSetting : ReactiveObject
    {
        [Reactive] public string DisplayName { get; set; } = string.Empty;
        [Reactive] public string MugShotFolderPath { get; set; } = string.Empty; // Path to the mugshot folder for this mod
        [Reactive] public ModKey? CorrespondingModKey { get; set; }
        [Reactive] public ObservableCollection<string> CorrespondingFolderPaths { get; set; } = new();
        public List<string> NpcNames { get; set; } = new();
        public List<string> NpcEditorIDs { get; set; } = new();
        public List<FormKey> NpcFormKeys { get; set; } = new();
        public Dictionary<FormKey, string> NpcFormKeysToDisplayName { get; set; } = new();

        private readonly SkyrimRelease _skyrimRelease;
        
        // *** NEW: Calculated property for display ***
        private readonly ObservableAsPropertyHelper<string> _modKeyDisplaySuffix;
        public string ModKeyDisplaySuffix => _modKeyDisplaySuffix.Value;

        // Commands
        public ReactiveCommand<Unit, Unit> AddFolderPathCommand { get; }
        public ReactiveCommand<string, Unit> BrowseFolderPathCommand { get; }
        public ReactiveCommand<string, Unit> RemoveFolderPathCommand { get; }
        public ReactiveCommand<Unit, Unit> BrowseMugshotFolderCommand { get; }

        // Reference to the parent VM to notify about changes if needed (e.g., for saving)
        private readonly VM_Mods _parentVm;

        // Constructor used when loading from existing Models.ModSetting
        public VM_ModSetting(Models.ModSetting model, VM_Mods parentVm) : this(model.DisplayName, parentVm) // Chain constructor
        {
            // Properties specific to loading existing model
            CorrespondingModKey = model.ModKey;
            CorrespondingFolderPaths = new ObservableCollection<string>(model.CorrespondingFolderPaths);
        }

        // Base constructor (used directly or chained)
        public VM_ModSetting(string displayName, VM_Mods parentVm)
        {
            _parentVm = parentVm;
            DisplayName = displayName;
            // Other properties (MugShotFolderPath, CorrespondingModKey, CorrespondingFolderPaths) set during population logic or loading constructor

            // --- Setup for ModKeyDisplaySuffix ---
            _modKeyDisplaySuffix = this.WhenAnyValue(x => x.DisplayName, x => x.CorrespondingModKey)
                .Select(tuple => {
                    var name = tuple.Item1;
                    var key = tuple.Item2;

                    if (!key.HasValue)
                    {
                        return string.Empty; // No key, empty suffix
                    }
                    else if (!string.IsNullOrEmpty(name) && name.Equals(key.Value.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        return "(Base Mod)"; // Name matches key
                    }
                    else
                    {
                        return $"({key.Value})"; // Name differs or is empty, show key
                    }
                })
                .ToProperty(this, x => x.ModKeyDisplaySuffix, scheduler: RxApp.MainThreadScheduler); // Ensure updates on UI thread


            // --- Command Initializations ---
            AddFolderPathCommand = ReactiveCommand.Create(AddFolderPath);
            BrowseFolderPathCommand = ReactiveCommand.Create<string>(BrowseFolderPath);
            RemoveFolderPathCommand = ReactiveCommand.Create<string>(RemoveFolderPath);
            BrowseMugshotFolderCommand = ReactiveCommand.Create(BrowseMugshotFolder);
        }

        public void RefreshNpcLists()
        {
            if (CorrespondingModKey.HasValue && CorrespondingFolderPaths.Any())
            {
                foreach (var dirPath in CorrespondingFolderPaths)
                {
                    var trialPath = Path.Combine(dirPath, CorrespondingModKey.Value.ToString());
                    if (File.Exists(trialPath))
                    {
                        try
                        {
                            using var mod = SkyrimMod.CreateFromBinaryOverlay(trialPath, _parentVm.SkyrimRelease);
                            {
                                foreach (var npc in mod.Npcs)
                                {
                                    if (!NpcFormKeys.Contains(npc.FormKey))
                                    {
                                        string displayName = string.Empty;
                                        NpcFormKeys.Add(npc.FormKey);
                                        if (npc.Name is not null && npc.Name.String is not null)
                                        {
                                            NpcNames.Add(npc.Name.String);
                                            displayName = npc.Name.String;
                                        }

                                        if (npc.EditorID is not null)
                                        {
                                            NpcEditorIDs.Add(npc.EditorID);
                                            displayName = npc.EditorID;
                                        }

                                        if (displayName == string.Empty)
                                        {
                                            displayName = npc.FormKey.ToString();
                                        }
                                        NpcFormKeysToDisplayName.Add(npc.FormKey, displayName);
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            continue;
                        }
                    }
                }
            }
        }

        private void AddFolderPath()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = $"Select a corresponding folder for {DisplayName}";
                string initialPath = _parentVm.ModsFolderSetting;
                if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
                {
                    initialPath = CorrespondingFolderPaths.FirstOrDefault(p => Directory.Exists(p));
                }
                 dialog.SelectedPath = initialPath ?? "";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    if (!CorrespondingFolderPaths.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
                    {
                        CorrespondingFolderPaths.Add(dialog.SelectedPath);
                    }
                }
            }
        }

        private void BrowseFolderPath(string existingPath)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = $"Change corresponding folder for {DisplayName}";
                dialog.SelectedPath = Directory.Exists(existingPath) ? existingPath : _parentVm.ModsFolderSetting;
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    int index = CorrespondingFolderPaths.IndexOf(existingPath);
                    if (index >= 0 && !CorrespondingFolderPaths.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
                    {
                        CorrespondingFolderPaths[index] = dialog.SelectedPath;
                    }
                    else if (index >= 0 && dialog.SelectedPath.Equals(existingPath, StringComparison.OrdinalIgnoreCase)) { /* Path didn't actually change */ }
                    else
                    {
                        System.Windows.MessageBox.Show($"Cannot change path. The new path '{dialog.SelectedPath}' might already exist in the list or the original path was not found.", "Browse Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    }
                }
            }
        }

        private void RemoveFolderPath(string pathToRemove)
        {
            if (CorrespondingFolderPaths.Contains(pathToRemove))
            {
                CorrespondingFolderPaths.Remove(pathToRemove);
            }
        }

        private void BrowseMugshotFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = $"Select the Mugshot Folder for {DisplayName}";
                dialog.SelectedPath = Directory.Exists(MugShotFolderPath) ? MugShotFolderPath : _parentVm.ModsFolderSetting;
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    if (Directory.Exists(dialog.SelectedPath))
                    {
                         MugShotFolderPath = dialog.SelectedPath;
                    }
                    else
                    {
                        System.Windows.MessageBox.Show($"The selected folder does not exist: '{dialog.SelectedPath}'", "Browse Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    }
                }
            }
        }
    }
}