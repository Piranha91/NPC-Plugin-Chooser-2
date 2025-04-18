// View Models/VM_ModSetting.cs
using System;
using System.Collections.ObjectModel;
using System.Diagnostics; // For Debug.WriteLine
using System.IO;
using System.Reactive;
using System.Reactive.Linq; // Needed for Select and ObservableAsPropertyHelper
using System.Windows.Forms; // For FolderBrowserDialog
using Mutagen.Bethesda.Plugins;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Linq;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.BackEnd;
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

        // *** NEW: Flag for dynamically created entries ***
        public bool IsMugshotOnlyEntry { get; set; } = false;

        // *** NEW: Helper properties for UI Styling ***
        [ObservableAsProperty] public bool HasMugshotPathAssigned { get; } // Reactive property backed by MugShotFolderPath
        [ObservableAsProperty] public bool HasModPathsAssigned { get; } // Reactive property backed by CorrespondingFolderPaths

        // *** Calculated property for display ***
        private readonly ObservableAsPropertyHelper<string> _modKeyDisplaySuffix;
        public string ModKeyDisplaySuffix => _modKeyDisplaySuffix.Value;

        [Reactive] public bool HasValidMugshots { get; set; } // Flag for clickable display name (checks content)

        // Commands
        public ReactiveCommand<Unit, Unit> AddFolderPathCommand { get; }
        public ReactiveCommand<string, Unit> BrowseFolderPathCommand { get; }
        public ReactiveCommand<string, Unit> RemoveFolderPathCommand { get; }
        public ReactiveCommand<Unit, Unit> BrowseMugshotFolderCommand { get; }

        // Reference to the parent VM to notify about changes if needed (e.g., for saving)
        private readonly VM_Mods _parentVm;

        // Constructor used when loading from existing Models.ModSetting
        public VM_ModSetting(Models.ModSetting model, VM_Mods parentVm) : this(model.DisplayName, parentVm, false) // Chain constructor, explicitly false
        {
            // Properties specific to loading existing model
            CorrespondingModKey = model.ModKey;
            CorrespondingFolderPaths = new ObservableCollection<string>(model.CorrespondingFolderPaths);
            // IsMugshotOnlyEntry is set to false via chaining
        }

        // Constructor used when creating dynamically from a Mugshot folder
        public VM_ModSetting(string displayName, string mugshotPath, VM_Mods parentVm) : this(displayName, parentVm, true) // Chain constructor, explicitly true
        {
            MugShotFolderPath = mugshotPath;
            // IsMugshotOnlyEntry is set to true via chaining
        }

        // Base constructor (used directly or chained) - ADD isMugshotOnly PARAMETER
        public VM_ModSetting(string displayName, VM_Mods parentVm, bool isMugshotOnly = false)
        {
            _parentVm = parentVm;
            DisplayName = displayName;
            IsMugshotOnlyEntry = isMugshotOnly; // Set the flag based on how it was created

            // --- Setup for ModKeyDisplaySuffix ---
            _modKeyDisplaySuffix = this.WhenAnyValue(x => x.DisplayName, x => x.CorrespondingModKey)
                .Select(tuple => {
                    var name = tuple.Item1;
                    var key = tuple.Item2;

                    if (!key.HasValue)
                    {
                        return string.Empty; // No key, empty suffix
                    }
                    else if (!string.IsNullOrEmpty(name) && name.Equals(key.Value.FileName, StringComparison.OrdinalIgnoreCase)) // Compare against FileName
                    {
                        return "(Base Mod)"; // Name matches key FileName
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

            // --- Setup Reactive Helper Properties for UI ---
            // Reactively determine if a mugshot path is assigned
            this.WhenAnyValue(x => x.MugShotFolderPath)
                .Select(path => !string.IsNullOrWhiteSpace(path))
                .ToPropertyEx(this, x => x.HasMugshotPathAssigned);

            // Reactively determine if any mod paths are assigned
            // Need to handle collection changes AND initial state
             Observable.Merge(
                     this.WhenAnyValue(x => x.CorrespondingFolderPaths.Count).Select(count => count > 0), // React to count changes
                     Observable.Return(this.CorrespondingFolderPaths?.Any() ?? false) // Initial check
                 )
                 .DistinctUntilChanged() // Only update if the value actually changes
                 .ToPropertyEx(this, x => x.HasModPathsAssigned);


            // --- Update UI state based on property changes ---
             // Recalculate HasValidMugshots (content check) when path changes
             this.WhenAnyValue(x => x.MugShotFolderPath)
                 .Throttle(TimeSpan.FromMilliseconds(100)) // Add slight delay in case path changes rapidly
                 .ObserveOn(RxApp.MainThreadScheduler)
                 .Subscribe(_ => {
                     _parentVm?.RecalculateMugshotValidity(this); // Notify parent VM
                 });
        }

        private void AddFolderPath()
        {
            using (var dialog = new FolderBrowserDialog()) // Consider using CommonOpenFileDialog for modern look
            {
                dialog.Description = $"Select a corresponding folder for {DisplayName}";
                string initialPath = _parentVm.ModsFolderSetting; // Start in main Mods folder if set
                if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
                {
                    // Fallback: Use first existing path in the list, or My Documents
                    initialPath = CorrespondingFolderPaths.FirstOrDefault(p => Directory.Exists(p)) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                }
                 dialog.SelectedPath = initialPath; // Set starting path

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    if (!CorrespondingFolderPaths.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
                    {
                        CorrespondingFolderPaths.Add(dialog.SelectedPath);
                        // Re-evaluate ModKey linking? Might be complex. Parent could handle this if needed.
                        // _parentVm?.AttemptRelinkModKey(this); // Example hook
                    }
                }
            }
        }

        private void BrowseFolderPath(string existingPath)
        {
            using (var dialog = new FolderBrowserDialog()) // Consider using CommonOpenFileDialog
            {
                dialog.Description = $"Change corresponding folder for {DisplayName}";
                dialog.SelectedPath = Directory.Exists(existingPath) ? existingPath : _parentVm.ModsFolderSetting; // Start in current or main mods
                 if (string.IsNullOrWhiteSpace(dialog.SelectedPath) || !Directory.Exists(dialog.SelectedPath)) {
                     dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); // Fallback
                 }

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    int index = CorrespondingFolderPaths.IndexOf(existingPath);
                    if (index >= 0 && !CorrespondingFolderPaths.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
                    {
                        CorrespondingFolderPaths[index] = dialog.SelectedPath;
                        // _parentVm?.AttemptRelinkModKey(this); // Re-evaluate link if path changes
                    }
                    else if (index >= 0 && dialog.SelectedPath.Equals(existingPath, StringComparison.OrdinalIgnoreCase)) { /* Path didn't actually change */ }
                    else if (index < 0)
                    {
                         System.Windows.MessageBox.Show($"Cannot change path. The original path '{existingPath}' was not found in the list.", "Browse Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    }
                     else // Path already exists
                    {
                         System.Windows.MessageBox.Show($"Cannot change path. The new path '{dialog.SelectedPath}' already exists in the list.", "Browse Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    }
                }
            }
        }

        private void RemoveFolderPath(string pathToRemove)
        {
            if (CorrespondingFolderPaths.Contains(pathToRemove))
            {
                CorrespondingFolderPaths.Remove(pathToRemove);
                // If no paths remain, potentially clear the ModKey?
                 if (!CorrespondingFolderPaths.Any() && CorrespondingModKey.HasValue)
                 {
                     // Maybe add logic here or in parent VM to clear/re-evaluate ModKey
                     // CorrespondingModKey = null; // Directly? Or let parent handle?
                     // _parentVm?.EvaluateModKeyAfterPathRemoval(this);
                 }
            }
        }

        private void BrowseMugshotFolder()
        {
            using (var dialog = new FolderBrowserDialog()) // Consider CommonOpenFileDialog
            {
                dialog.Description = $"Select the Mugshot Folder for {DisplayName}";
                // Prefer current path, then parent's setting, then fallback
                string initialPath = MugShotFolderPath;
                if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
                {
                     initialPath = _parentVm.MugshotsFolderSetting;
                }
                if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
                {
                     initialPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                }
                dialog.SelectedPath = initialPath;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    if (Directory.Exists(dialog.SelectedPath))
                    {
                        MugShotFolderPath = dialog.SelectedPath; // This will trigger reactive update via WhenAnyValue
                    }
                    else
                    {
                        System.Windows.MessageBox.Show($"The selected folder does not exist: '{dialog.SelectedPath}'", "Browse Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    }
                }
            }
        }

        public void RefreshNpcLists()
        {
            // Reset lists before potentially repopulating
            NpcNames.Clear();
            NpcEditorIDs.Clear();
            NpcFormKeys.Clear();
            NpcFormKeysToDisplayName.Clear();

            // Use the reactive helper property HasModPathsAssigned
            if (CorrespondingModKey.HasValue && HasModPathsAssigned)
            {
                foreach (var dirPath in CorrespondingFolderPaths)
                {
                     // Check if the *plugin file* exists within the corresponding folder path
                     // We assume the plugin name matches the ModKey FileName
                     if (!CorrespondingModKey.HasValue) continue; // Should not happen due to outer check, but safety first

                     string pluginFileName = CorrespondingModKey.Value.FileName;
                     string potentialPluginPath = Path.Combine(dirPath, pluginFileName);

                     string actualPluginPath = null;

                     if (File.Exists(potentialPluginPath))
                     {
                         actualPluginPath = potentialPluginPath;
                     }
                     // Add more sophisticated checking if needed, e.g., searching subdirs or alternative naming conventions

                     if (actualPluginPath != null && File.Exists(actualPluginPath))
                     {
                         try
                         {
                             // Use FromBinaryOverlay to avoid locking the file if possible
                             using var mod = SkyrimMod.CreateFromBinaryOverlay(actualPluginPath, _parentVm.SkyrimRelease);
                             {
                                 foreach (var npcGetter in mod.Npcs) // Iterate through INpcGetter
                                 {
                                     GetNpcDisplayName(npcGetter);
                                 }
                             }
                         }
                         catch (Exception e)
                         {
                             // Log or display error specific to this plugin file?
                             Debug.WriteLine($"Error loading NPC data from {actualPluginPath} for '{DisplayName}': {e.Message}");
                             continue; // Skip to next folder path on error
                         }
                     }
                     else
                     {
                         // Debug.WriteLine($"Plugin file '{pluginFileName}' not found in path '{dirPath}' for '{DisplayName}'.");
                     }
                 }
             }
            else if (!MugShotFolderPath.IsNullOrWhitespace())
            {
                var pluginNameDirs = Directory.EnumerateDirectories(MugShotFolderPath, "*", SearchOption.AllDirectories);
                foreach (var pluginNameDir in pluginNameDirs)
                {
                    var pluginName = pluginNameDir.Split(Path.DirectorySeparatorChar).Last();
                    var files = Directory.EnumerateFiles(pluginNameDir, "*.*", SearchOption.AllDirectories);
                    var formKeyStrs = files.Select(x => 
                        Path.GetFileNameWithoutExtension(x).Substring(2) + ":" + pluginName
                        ).ToArray();

                    foreach (var formKeyStr in formKeyStrs)
                    {
                        var formKey = FormKey.TryFactory(formKeyStr.ToUpper());
                        if (formKey != null && _parentVm.TryGetWinningNpc(formKey.Value, out var npcGetter) && npcGetter != null)
                        {
                            GetNpcDisplayName(npcGetter);
                        }
                    }
                }
            }
             // After processing all paths, raise property changed for filter purposes if needed?
             // Filtering relies on these lists, so maybe trigger a filter update in VM_Mods after all RefreshNpcLists tasks complete.
        }

        public void GetNpcDisplayName(INpcGetter npcGetter)
        {
            // Make sure FormKey doesn't already exist (can happen if NPC record is in multiple linked plugins/paths)
            if (!NpcFormKeys.Contains(npcGetter.FormKey))
            {
                // Resolve the link for full NPC data if needed, but FormKey, Name, EditorID are usually available directly
                // For now, use the getter directly
                var npc = npcGetter; // Use the getter interface directly for basic info

                string displayName = string.Empty;
                NpcFormKeys.Add(npc.FormKey);

                if (npc.Name is not null && !string.IsNullOrEmpty(npc.Name.String))
                {
                    NpcNames.Add(npc.Name.String);
                    displayName = npc.Name.String;
                }

                if (!string.IsNullOrEmpty(npc.EditorID))
                {
                    NpcEditorIDs.Add(npc.EditorID);
                    if (string.IsNullOrEmpty(displayName))
                    {
                        displayName = npc.EditorID;
                    }
                }

                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = npc.FormKey.ToString(); // Fallback to FormKey
                }
                NpcFormKeysToDisplayName.Add(npc.FormKey, displayName);
            }
        }
    }
}