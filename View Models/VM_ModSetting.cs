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
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views; // Assuming Models namespace

namespace NPC_Plugin_Chooser_2.View_Models
{
    /// <summary>
    /// Enum to specify the type of path being modified for merge checks.
    /// </summary>
    public enum PathType
    {
        MugshotFolder,
        ModFolder
    }

    public class VM_ModSetting : ReactiveObject
    {
        // --- Properties ---
        [Reactive] public string DisplayName { get; set; } = string.Empty;
        [Reactive] public string MugShotFolderPath { get; set; } = string.Empty; // Path to the mugshot folder for this mod
        [Reactive] public ObservableCollection<ModKey> CorrespondingModKeys { get; set; } = new();
        [Reactive] public ObservableCollection<string> CorrespondingFolderPaths { get; set; } = new();
        public List<string> NpcNames { get; set; } = new();
        public List<string> NpcEditorIDs { get; set; } = new();
        public List<FormKey> NpcFormKeys { get; set; } = new();
        public Dictionary<FormKey, string> NpcFormKeysToDisplayName { get; set; } = new();

        // Maps NPC FormKey to the SINGLE ModKey within this setting that provides its record.
        public Dictionary<FormKey, ModKey> NpcSourcePluginMap { get; private set; } = new();
        // Stores FormKeys of NPCs found in multiple plugins within this setting (Error State)
        public HashSet<FormKey> AmbiguousNpcFormKeys { get; private set; } = new();
        private readonly SkyrimRelease _skyrimRelease;

        // Flag indicating if this VM was created dynamically only from a Mugshot folder
        // and wasn't loaded from the persisted ModSettings.
        public bool IsMugshotOnlyEntry { get; set; } = false;

        // Helper properties derived from other reactive properties for UI Styling/Logic
        [ObservableAsProperty] public bool HasMugshotPathAssigned { get; } // True if MugShotFolderPath is not null/whitespace
        [ObservableAsProperty] public bool HasModPathsAssigned { get; } // True if CorrespondingFolderPaths has items

        // Calculated property for displaying the ModKey suffix in the UI
        private readonly ObservableAsPropertyHelper<string> _modKeyDisplaySuffix;
        public string ModKeyDisplaySuffix => _modKeyDisplaySuffix.Value;

        // Flag indicating if the MugShotFolderPath contains validly structured image files.
        // Used to enable/disable the clickable DisplayName link.
        [Reactive] public bool HasValidMugshots { get; set; }

        // --- Commands ---
        public ReactiveCommand<Unit, Unit> AddFolderPathCommand { get; }
        public ReactiveCommand<string, Unit> BrowseFolderPathCommand { get; }
        public ReactiveCommand<string, Unit> RemoveFolderPathCommand { get; }
        public ReactiveCommand<Unit, Unit> BrowseMugshotFolderCommand { get; }

        // --- Private Fields ---
        private readonly VM_Mods _parentVm; // Reference to the parent VM (VM_Mods)

        // --- Constructors ---

        /// <summary>
        /// Constructor used when loading from an existing Models.ModSetting.
        /// </summary>
        public VM_ModSetting(Models.ModSetting model, VM_Mods parentVm)
            : this(model.DisplayName, parentVm, isMugshotOnly: false) // Chain constructor, explicitly false for IsMugshotOnlyEntry
        {
            // Properties specific to loading existing model
            CorrespondingModKeys = new ObservableCollection<ModKey>(model.ModKeys ?? new List<ModKey>());
            CorrespondingFolderPaths = new ObservableCollection<string>(model.CorrespondingFolderPaths ?? new List<string>()); // Handle potential null
            MugShotFolderPath = model.MugShotFolderPath; // Load persisted mugshot folder path
            // IsMugshotOnlyEntry is set to false via chaining
        }

        /// <summary>
        /// Constructor used when creating dynamically from a Mugshot folder during initial population.
        /// </summary>
        public VM_ModSetting(string displayName, string mugshotPath, VM_Mods parentVm)
            : this(displayName, parentVm, isMugshotOnly: true) // Chain constructor, explicitly true for IsMugshotOnlyEntry
        {
             MugShotFolderPath = mugshotPath;
             // IsMugshotOnlyEntry is set to true via chaining
        }

        /// <summary>
        /// Base constructor (used directly or chained).
        /// </summary>
        /// <param name="displayName">The initial display name.</param>
        /// <param name="parentVm">Reference to the parent VM_Mods.</param>
        /// <param name="isMugshotOnly">Flag indicating if this VM represents an entry initially created only from a mugshot folder.</param>
        public VM_ModSetting(string displayName, VM_Mods parentVm, bool isMugshotOnly = false)
        {
            _parentVm = parentVm;
            DisplayName = displayName;
            IsMugshotOnlyEntry = isMugshotOnly; // Set the flag based on how it was created
            _skyrimRelease = parentVm.SkyrimRelease; // Get SkyrimRelease from parent

            // --- Setup for ModKeyDisplaySuffix ---
            _modKeyDisplaySuffix = this.WhenAnyValue(x => x.DisplayName, x => x.CorrespondingModKeys.Count) // Trigger on count change
                .Select(_ => {
                    var name = DisplayName;
                    var keys = CorrespondingModKeys;
                    if (keys == null || !keys.Any()) return string.Empty;

                    var keyStrings = keys.Select(k => k.ToString()).ToList();

                    // Try to find a key matching the display name
                    var matchingKey = keys.FirstOrDefault(k => !string.IsNullOrEmpty(name) && name.Equals(k.FileName, StringComparison.OrdinalIgnoreCase));
                    if (matchingKey != default(ModKey))
                    {
                        return "(Base Mod)"; // Indicate if a key matches the folder/display name
                    }
                    else if (keys.Count == 1)
                    {
                        return $"({keys.First()})"; // Display single key if no match
                    }
                    else
                    {
                        return $"({keys.Count} Plugins)"; // Indicate multiple plugins
                    }
                })
                .ToProperty(this, x => x.ModKeyDisplaySuffix, scheduler: RxApp.MainThreadScheduler);

            // --- Setup Reactive Helper Properties for UI ---
            this.WhenAnyValue(x => x.MugShotFolderPath)
                .Select(path => !string.IsNullOrWhiteSpace(path))
                .ToPropertyEx(this, x => x.HasMugshotPathAssigned);

             Observable.Merge(
                     this.WhenAnyValue(x => x.CorrespondingFolderPaths.Count).Select(count => count > 0),
                     Observable.Return(this.CorrespondingFolderPaths?.Any() ?? false) // Initial check
                 )
                 .DistinctUntilChanged()
                 .ToPropertyEx(this, x => x.HasModPathsAssigned);

            // --- Command Initializations ---
            AddFolderPathCommand = ReactiveCommand.Create(AddFolderPath);
            BrowseFolderPathCommand = ReactiveCommand.Create<string>(BrowseFolderPath);
            RemoveFolderPathCommand = ReactiveCommand.Create<string>(RemoveFolderPath);
            BrowseMugshotFolderCommand = ReactiveCommand.Create(BrowseMugshotFolder);

            // --- Subscribe to Property Changes for Dependent Logic ---
             this.WhenAnyValue(x => x.MugShotFolderPath)
                 .Throttle(TimeSpan.FromMilliseconds(100))
                 .ObserveOn(RxApp.MainThreadScheduler)
                 .Subscribe(_ => {
                     // Notify parent to recheck if the folder contains valid images
                     _parentVm?.RecalculateMugshotValidity(this);
                 });

            // Optionally, trigger RefreshNpcLists when ModKey or Paths change?
            // Could be intensive. Let's assume it's done during initial load for now.
            // this.WhenAnyValue(x => x.CorrespondingModKeys.Count, x => x.CorrespondingFolderPaths.Count) // Check counts
            //    .Throttle(TimeSpan.FromSeconds(1)) // Avoid rapid calls
            //    .ObserveOn(TaskPoolScheduler.Default) // Run on background thread
            //    .Subscribe(_ => RefreshNpcLists());
        }

        // --- Command Implementations ---

        private void AddFolderPath()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = $"Select a corresponding folder for {DisplayName}";
                string initialPath = _parentVm.ModsFolderSetting;
                if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath)) { initialPath = CorrespondingFolderPaths.FirstOrDefault(p => Directory.Exists(p)) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
                dialog.SelectedPath = initialPath;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string addedPath = dialog.SelectedPath;
                    if (!CorrespondingFolderPaths.Contains(addedPath, StringComparer.OrdinalIgnoreCase))
                    {
                        // Store state *before* modification for merge check
                        bool hadMugshotBefore = HasMugshotPathAssigned;
                        bool hadModPathsBefore = HasModPathsAssigned;

                        CorrespondingFolderPaths.Add(addedPath); // Modify the collection

                        // *** Notify parent VM AFTER path is added ***
                        _parentVm?.CheckForAndPerformMerge(this, addedPath, PathType.ModFolder, hadMugshotBefore, hadModPathsBefore);
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
                if (string.IsNullOrWhiteSpace(dialog.SelectedPath) || !Directory.Exists(dialog.SelectedPath)) { dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string newPath = dialog.SelectedPath;
                    int index = CorrespondingFolderPaths.IndexOf(existingPath);

                    // Check if path actually changed and isn't already in the list elsewhere
                    if (index >= 0 &&
                        !newPath.Equals(existingPath, StringComparison.OrdinalIgnoreCase) &&
                        !CorrespondingFolderPaths.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                    {
                         // Store state *before* modification for merge check
                        bool hadMugshotBefore = HasMugshotPathAssigned;
                        bool hadModPathsBefore = CorrespondingFolderPaths.Count > 0; // Had paths if count was > 0 before change

                        CorrespondingFolderPaths[index] = newPath; // Modify the collection

                        // *** Notify parent VM AFTER path is changed ***
                        _parentVm?.CheckForAndPerformMerge(this, newPath, PathType.ModFolder, hadMugshotBefore, hadModPathsBefore);
                    }
                    else if (index >= 0 && newPath.Equals(existingPath, StringComparison.OrdinalIgnoreCase)) { /* No change needed */ }
                    else if (index >= 0) // Path didn't change but new path already exists elsewhere
                    {
                        ScrollableMessageBox.ShowWarning($"Cannot change path. The new path '{newPath}' already exists in the list.", "Browse Error");
                    }
                    else // index < 0
                    {
                        ScrollableMessageBox.ShowWarning($"Cannot change path. The original path '{existingPath}' was not found.", "Browse Error");
                    }
                }
            }
        }

        private void RemoveFolderPath(string pathToRemove)
        {
            // Removing a path doesn't trigger the merge check.
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
                string initialPath = MugShotFolderPath;
                if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath)) { initialPath = _parentVm.MugshotsFolderSetting; }
                if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath)) { initialPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
                dialog.SelectedPath = initialPath;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string newPath = dialog.SelectedPath;
                    if (Directory.Exists(newPath) && !newPath.Equals(MugShotFolderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // Store state *before* modification for merge check
                        bool hadMugshotBefore = HasMugshotPathAssigned;
                        bool hadModPathsBefore = HasModPathsAssigned;

                        MugShotFolderPath = newPath; // Modify the property

                        // *** Notify parent VM AFTER path is set ***
                        _parentVm?.CheckForAndPerformMerge(this, newPath, PathType.MugshotFolder, hadMugshotBefore, hadModPathsBefore);
                    }
                    else if (!Directory.Exists(newPath))
                    {
                        ScrollableMessageBox.ShowWarning($"The selected folder does not exist: '{newPath}'", "Browse Error");
                    }
                    // else: Path didn't change or wasn't valid - no action needed
                }
            }
        }

        // --- Other Methods ---

        /// <summary>
        /// Reads associated plugin files (based on CorrespondingModKey and CorrespondingFolderPaths)
        /// and populates the NPC lists (NpcNames, NpcEditorIDs, NpcFormKeys, NpcFormKeysToDisplayName).
        /// Should typically be run asynchronously during initial load or after significant changes.
        /// </summary>
        public void RefreshNpcLists()
        {
            // Reset lists before potentially repopulating
            NpcNames.Clear();
            NpcEditorIDs.Clear();
            NpcFormKeys.Clear();
            NpcFormKeysToDisplayName.Clear();

            NpcSourcePluginMap.Clear();
            AmbiguousNpcFormKeys.Clear();
            var npcFoundInPlugins = new Dictionary<FormKey, List<ModKey>>(); // Temp track sources

            // Check if there are any keys AND paths assigned
            if (CorrespondingModKeys.Any() && HasModPathsAssigned)
            {
                // Iterate through each potential plugin associated with this mod setting
                foreach (var modKey in CorrespondingModKeys)
                {
                    string? foundPluginPath = null;
                    // Iterate through each potential folder path *to find this specific plugin*
                    foreach (var dirPath in CorrespondingFolderPaths)
                    {
                        string pluginFileName = modKey.FileName;
                        string potentialPluginPath = Path.Combine(dirPath, pluginFileName);

                        if (File.Exists(potentialPluginPath))
                        {
                            foundPluginPath = potentialPluginPath;
                            break; // Found the plugin in this folder, no need to check others for this key
                        }
                    }

                    if (foundPluginPath != null) // If a valid path for this plugin was found
                    {
                        try
                        {
                            using var mod = SkyrimMod.CreateFromBinaryOverlay(foundPluginPath, _skyrimRelease);
                            foreach (var npcGetter in mod.Npcs)
                            {
                                FormKey currentNpcKey = npcGetter.FormKey;

                                // --- Track which plugins contain this NPC ---
                                if (!npcFoundInPlugins.TryGetValue(currentNpcKey, out var sourceList))
                                {
                                    sourceList = new List<ModKey>();
                                    npcFoundInPlugins[currentNpcKey] = sourceList;
                                }
                                if (!sourceList.Contains(modKey)) // Avoid adding same key twice if plugin in multiple folders
                                {
                                     sourceList.Add(modKey);
                                }
                                // --- End Tracking ---

                                // --- Populate display info (only once per NPC) ---
                                if (!NpcFormKeys.Contains(currentNpcKey))
                                {
                                    NpcFormKeys.Add(currentNpcKey); // Add to overall list for filtering
                                    var npc = npcGetter;
                                    string displayName = string.Empty;
                                    if (npc.Name is not null && !string.IsNullOrEmpty(npc.Name.String)) { NpcNames.Add(npc.Name.String); displayName = npc.Name.String; }
                                    if (!string.IsNullOrEmpty(npc.EditorID)) { NpcEditorIDs.Add(npc.EditorID); if (string.IsNullOrEmpty(displayName)) displayName = npc.EditorID; }
                                    if (string.IsNullOrEmpty(displayName)) displayName = npc.FormKey.ToString();
                                    NpcFormKeysToDisplayName.Add(currentNpcKey, displayName);
                                }
                                // --- End display info ---
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.WriteLine($"Error loading NPC data from {foundPluginPath} for ModSetting '{DisplayName}': {e.Message}");
                        }
                    }
                } // End foreach modKey
            } // End if keys and paths exist

            // --- Post-Processing: Populate Map and Detect Ambiguity ---
            foreach(var kvp in npcFoundInPlugins)
            {
                FormKey npcKey = kvp.Key;
                List<ModKey> sources = kvp.Value;

                if (sources.Count == 1)
                {
                    // Exactly one source plugin found within this ModSetting - this is valid
                    NpcSourcePluginMap[npcKey] = sources[0];
                }
                else if (sources.Count > 1)
                {
                    // Multiple source plugins found within this ModSetting - this is an error
                    AmbiguousNpcFormKeys.Add(npcKey);
                    Debug.WriteLine($"ERROR for ModSetting '{DisplayName}': NPC {npcKey} found in multiple associated plugins: {string.Join(", ", sources.Select(k=>k.FileName))}. This NPC will be skipped for this Mod Setting.");
                    // Optionally: Add to a persistent warning list for the user?
                }
                // If sources.Count == 0, something went wrong, but it won't be in the map.
            }
        }
    }
}