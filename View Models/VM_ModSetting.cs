// View Models/VM_ModSetting.cs
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics; // For Debug.WriteLine
using System.IO;
using System.Reactive;
using System.Reactive.Linq; // Needed for Select and ObservableAsPropertyHelper
using System.Windows.Forms; // For FolderBrowserDialog
using Mutagen.Bethesda.Plugins;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Linq;
using System.Windows.Media;
using DynamicData;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using LinkCacheConstructionMixIn = Mutagen.Bethesda.LinkCacheConstructionMixIn; // Assuming Models namespace

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
    
    /// <summary>
    /// Enum to specify the type of issue that a given modded NPC has
    /// </summary>
    public enum NpcIssueType
    {
        Template
    }

    [DebuggerDisplay("{DisplayName}")]
    public class VM_ModSetting : ReactiveObject
    {
        // --- Factory Delegates ---
        public delegate VM_ModSetting FromModelFactory(ModSetting model, VM_Mods parentVm);
        public delegate VM_ModSetting FromMugshotPathFactory(string displayName, string mugshotPath, VM_Mods parentVm);
        public delegate VM_ModSetting FromModFolderFactory(string modFolderPath, IEnumerable<ModKey>? plugins, VM_Mods parentVm);
        
        // --- Properties ---
        [Reactive] public string DisplayName { get; set; } = string.Empty;
        [Reactive] public string MugShotFolderPath { get; set; } = string.Empty; // Path to the mugshot folder for this mod
        [Reactive] public ObservableCollection<ModKey> CorrespondingModKeys { get; private set; } = new();
        [Reactive] public ObservableCollection<string> CorrespondingFolderPaths { get; private set; } = new();
        [Reactive] public bool MergeInDependencyRecords { get; set; } = true;
        [Reactive] public bool MergeInDependencyRecordsVisible { get; set; } = true;
       

        [Reactive] public string MergeInToolTip { get; set; } = ModSetting.DefaultMergeInTooltip;
        [Reactive] public SolidColorBrush? MergeInLabelColor { get; set; } = null; // null initialization is intentional
        public bool HasAlteredMergeLogic { get; set; } = false;

        [Reactive] public RecordOverrideHandlingMode? OverrideRecordOverrideHandlingMode { get; set; }
        public IEnumerable<KeyValuePair<RecordOverrideHandlingMode?,string>> RecordOverrideHandlingModes { get; }
            = new[]
                {
                    new KeyValuePair<RecordOverrideHandlingMode?,string>(null, "Default")
                }
                .Concat(Enum.GetValues(typeof(RecordOverrideHandlingMode))
                    .Cast<RecordOverrideHandlingMode>()
                    .Select(e => 
                        new KeyValuePair<RecordOverrideHandlingMode?,string>(e, e.ToString())
                    ));

        public HashSet<string> NpcNames { get; set; } = new();
        public HashSet<string> NpcEditorIDs { get; set; } = new();
        public HashSet<FormKey> NpcFormKeys { get; set; } = new();
        public Dictionary<FormKey, string> NpcFormKeysToDisplayName { get; set; } = new();
        public Dictionary<FormKey, List<ModKey>> AvailablePluginsForNpcs { get; set; } = new(); // tracks which plugins contain which Npc entry

        public Dictionary<FormKey, (NpcIssueType IssueType, string IssueMessage, FormKey? ReferencedFormKey)> 
            NpcFormKeysToNotifications
                = new();
        // tracks any notifications the user should be alerted to for the given Npc

        // New Property: Maps NPC FormKey to the ModKey from which it should inherit data,
        // specifically for NPCs appearing in multiple plugins within this ModSetting.
        // This is loaded from and saved to Models.ModSetting.
        public Dictionary<FormKey, ModKey> NpcPluginDisambiguation { get; set; }
        
        // Stores FormKeys of NPCs found in multiple plugins within this setting (Error State)
        public HashSet<FormKey> AmbiguousNpcFormKeys { get; private set; } = new();
        
        public ModStateSnapshot? LastKnownState { get; set; } // hang on to DTO for serialization
        
        private readonly SkyrimRelease _skyrimRelease;
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Auxilliary _aux;
        private readonly BsaHandler _bsaHandler;
        private readonly PluginProvider _pluginProvider;

        // Flag indicating if this VM was created dynamically only from a Mugshot folder
        // and wasn't loaded from the persisted ModSettings.
        public bool IsMugshotOnlyEntry { get; set; } = false;
        
        // Flag indicating if this VM was created from a facegen-only Mod folder (in which case only NPCs with facegen
        // rather than all NPCs in the corresponding plugins should be displayed
        public bool IsFaceGenOnlyEntry { get; set; } = false;
        public HashSet<FormKey> FaceGenOnlyNpcFormKeys { get; set; } = new(); // NPCs contained in the given FaceGen-only mod
        
        // Flag indicating if this VM was created automatically from base game or creation club (in which case its data
        // folder path should remain unset.
        public bool IsAutoGenerated { get; set;  } = false;

        // Helper properties derived from other reactive properties for UI Styling/Logic
        [ObservableAsProperty] public bool HasMugshotPathAssigned { get; } // True if MugShotFolderPath is not null/whitespace
        [ObservableAsProperty] public bool HasModPathsAssigned { get; } // True if CorrespondingFolderPaths has items

        // Calculated property for displaying the ModKey suffix in the UI
        private readonly ObservableAsPropertyHelper<string> _modKeyDisplaySuffix;
        public string ModKeyDisplaySuffix => _modKeyDisplaySuffix.Value;
        
        // Calculated property for displaying whether or not the contained plugins have an override
        private HashSet<ModKey> _pluginsWithOverrideRecords = new();
        public bool HasPluginWithOverrideRecords => _pluginsWithOverrideRecords.Any();

        // HasValidMugshots now indicates if *actual* mugshots are present.
        // If false, but MugShotFolderPath is assigned (or even if not),
        // the mod is still "clickable" to show placeholders.
        [Reactive] public bool HasValidMugshots { get; set; }
        
        private readonly ObservableAsPropertyHelper<bool> _canUnlinkMugshots;
        public bool CanUnlinkMugshots => _canUnlinkMugshots.Value;
        // Reactive property to control Delete button visibility ***
        [ObservableAsProperty] public bool CanDelete { get; }
        
        // Property control if additional expensive checks are performed the first time a mod is discovered
        public bool IsNewlyCreated { get; set; } = false;

        // --- Commands ---
        public ReactiveCommand<Unit, Unit> AddFolderPathCommand { get; }
        public ReactiveCommand<string, Unit> BrowseFolderPathCommand { get; }
        public ReactiveCommand<string, Unit> RemoveFolderPathCommand { get; }
        public ReactiveCommand<Unit, Unit> BrowseMugshotFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> UnlinkMugshotDataCommand { get; }
        
        // Command for deleting the mod setting ***
        public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

        // --- Private Fields ---
        private readonly VM_Mods _parentVm; // Reference to the parent VM (VM_Mods)

        // --- Constructors ---

        /// <summary>
        /// Constructor used when loading from an existing Models.ModSetting.
        /// Called by FromModelFactory.
        /// </summary>
        public VM_ModSetting(Models.ModSetting model, VM_Mods parentVm, Auxilliary aux, BsaHandler bsaHandler, PluginProvider pluginProvider)
            : this(model.DisplayName, parentVm, aux, bsaHandler, pluginProvider, isMugshotOnly: false, 
                correspondingFolderPaths: model.CorrespondingFolderPaths,
                correspondingModKeys: model.CorrespondingModKeys)
        {
            // Properties specific to loading existing model
            IsAutoGenerated = model.IsAutoGenerated; // make sure this loads first
            MugShotFolderPath = model.MugShotFolderPath; // Load persisted mugshot folder path
            NpcPluginDisambiguation = new Dictionary<FormKey, ModKey>(model.NpcPluginDisambiguation ?? new Dictionary<FormKey, ModKey>());
            MergeInDependencyRecords = model.MergeInDependencyRecords;
            MergeInToolTip = model.MergeInToolTip;
            MergeInLabelColor = model.MergeInLabelColor;
            OverrideRecordOverrideHandlingMode = model.ModRecordOverrideHandlingMode;
            // AvailablePluginsForNpcs should be re-calculated on load.
            // IsMugshotOnlyEntry is set to false via chaining
            IsFaceGenOnlyEntry = model.IsFaceGenOnlyEntry;
            FaceGenOnlyNpcFormKeys = new(FaceGenOnlyNpcFormKeys);

            MergeInDependencyRecordsVisible = DisplayName != VM_Mods.BaseGameModSettingName &&
                                              DisplayName != VM_Mods.CreationClubModsettingName;

            _pluginsWithOverrideRecords = model.PluginsWithOverrideRecords;
            HasAlteredMergeLogic = model.HasAlteredMergeLogic;
            LastKnownState = model.LastKnownState;
            
            NpcFormKeys = new HashSet<FormKey>(model.NpcFormKeys);
            NpcFormKeysToDisplayName = new Dictionary<FormKey, string>(model.NpcFormKeysToDisplayName);
            AvailablePluginsForNpcs = new Dictionary<FormKey, List<ModKey>>(model.AvailablePluginsForNpcs);
            AmbiguousNpcFormKeys = new HashSet<FormKey>(model.AmbiguousNpcFormKeys);
        }

        /// <summary>
        /// Constructor used when creating dynamically from a Mugshot folder.
        /// Called by FromMugshotPathFactory.
        /// </summary>
        public VM_ModSetting(string displayName, string mugshotPath, VM_Mods parentVm, Auxilliary aux, BsaHandler bsaHandler, PluginProvider pluginProvider)
            : this(displayName, parentVm, aux, bsaHandler, pluginProvider, isMugshotOnly: true)
        {
            MugShotFolderPath = mugshotPath;
        }
        
        /// <summary>
        /// Constructor used when creating from a new mod folder entry.
        /// Called by FromModFolderFactory.
        /// </summary>
        public VM_ModSetting(string modFolderPath, IEnumerable<ModKey>? plugins, VM_Mods parentVm, Auxilliary aux, BsaHandler bsaHandler, PluginProvider pluginProvider)
            : this(Path.GetFileName(modFolderPath) ?? "Invalid Path", 
                parentVm, aux, bsaHandler, pluginProvider, 
                isMugshotOnly: false, 
                correspondingFolderPaths: new List<string>() {modFolderPath},
                correspondingModKeys: plugins ?? aux.GetModKeysInDirectory(modFolderPath, new List<string>(), false))
        {

        }

        /// <summary>
        /// Base constructor (private to enforce factory usage for specific scenarios if desired, or internal).
        /// Making it public for simplicity with Autofac delegate factories if they directly target this,
        /// but current setup chains to it.
        /// </summary>
        private VM_ModSetting(string displayName, VM_Mods parentVm, Auxilliary aux, BsaHandler bsaHandler, PluginProvider pluginProvider, bool isMugshotOnly,
            IEnumerable<string>? correspondingFolderPaths = null, IEnumerable<ModKey>? correspondingModKeys = null)
        {
            _parentVm = parentVm;
            DisplayName = displayName;
            IsMugshotOnlyEntry = isMugshotOnly; // Set the flag based on how it was created
            _skyrimRelease = parentVm.SkyrimRelease; // Get SkyrimRelease from parent
            _environmentStateProvider = parentVm.EnvironmentStateProvider; // Get EnvironmentStateProvider from parent
            _aux = aux;
            _bsaHandler = bsaHandler;
            _pluginProvider = pluginProvider;

            // Initialize NpcPluginDisambiguation if not loaded from model (chained constructors handle this)
            if (NpcPluginDisambiguation == null) // Should only be null if this base constructor is called directly without chaining from model constructor
            {
                NpcPluginDisambiguation = new Dictionary<FormKey, ModKey>();
            }
            
            // Set paths and trigger key update BEFORE subscriptions are created
            if (correspondingFolderPaths != null)
            {
                CorrespondingFolderPaths = new ObservableCollection<string>(correspondingFolderPaths);
            }
            
            if (correspondingModKeys == null)
            {
                UpdateCorrespondingModKeys();
            }
            else
            {
                CorrespondingModKeys = new(correspondingModKeys);
            }

            // --- Setup for ModKeyDisplaySuffix ---
            _modKeyDisplaySuffix = this.WhenAnyValue(x => x.DisplayName, x => x.CorrespondingModKeys.Count) // Trigger on count change
                .Select(_ => {
                    var name = DisplayName;
                    var keys = CorrespondingModKeys;
                    if (keys == null || !keys.Any()) return string.Empty;

                    var keyStrings = keys.Select(k => k.ToString()).ToList();

                    // Try to find a key matching the display name
                    var matchingKey = keys.FirstOrDefault(k => !string.IsNullOrEmpty(name) && name.Equals(k.FileName, StringComparison.OrdinalIgnoreCase));
                    if (keys.Count == 1)
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
             
             // Keep corresponding CorrespondingModKeys up to date when folders are added or removed
             this.WhenAnyValue(x => x.CorrespondingFolderPaths.Count).Select(_ => Unit.Default)
                 .Skip(1) // Skip the initial value set in the constructor
                 .Throttle(TimeSpan.FromMilliseconds(100))
                 .ObserveOn(RxApp.MainThreadScheduler) 
                 .Subscribe(_ =>
                 {
                     UpdateCorrespondingModKeys();
                 });
     
             // When MugShotFolderPath changes OR CorrespondingModKeys.Count changes,
             // re-evaluate HasValidMugshots.
             Observable.Merge(
                     this.WhenAnyValue(x => x.MugShotFolderPath).Select(_ => Unit.Default),
                     this.WhenAnyValue(x => x.CorrespondingModKeys.Count).Select(_ => Unit.Default) // Use Count property
                 )
                 .Skip(1) // Skip the initial value set in the constructor
                 .Throttle(TimeSpan.FromMilliseconds(100))
                 .ObserveOn(RxApp.MainThreadScheduler) 
                 .Subscribe(_ => {
                     _parentVm?.RecalculateMugshotValidity(this);
                 });
             
             // --- Setup for CanUnlinkMugshots ---
             _canUnlinkMugshots = this.WhenAnyValue(
                x => x.MugShotFolderPath,
                x => x.CorrespondingFolderPaths.Count, // React to count changes
                (mugshotPath, folderCount) => // folderCount is needed to trigger re-evaluation when collection changes
                {
                    // Condition 1: Must have an assigned mugshot path
                    if (string.IsNullOrWhiteSpace(mugshotPath))
                    {
                        return false;
                    }

                    // Condition 2: Must have at least one corresponding mod data folder path
                    // If no data folders, it's already "mugshot-only" (or should be), so unlinking isn't applicable.
                    if (!this.CorrespondingFolderPaths.Any()) // Use this.CorrespondingFolderPaths for direct access
                    {
                        return false;
                    }

                    // Condition 3: The mugshot folder's name must NOT match the name of ANY of the data folders.
                    try // Add try-catch for Path.GetFileName in case of invalid paths
                    {
                        string mugshotFolderName = Path.GetFileName(mugshotPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        
                        // Check if ANY data folder name matches the mugshot folder name
                        bool matchesAnyDataFolder = this.CorrespondingFolderPaths.Any(dataPath =>
                        {
                            if (string.IsNullOrWhiteSpace(dataPath)) return false;
                            string dataFolderName = Path.GetFileName(dataPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                            return mugshotFolderName.Equals(dataFolderName, StringComparison.OrdinalIgnoreCase);
                        });

                        return !matchesAnyDataFolder; // Can unlink if it does NOT match any
                    }
                    catch (ArgumentException ex) // Path.GetFileName can throw if path contains invalid chars
                    {
                        Debug.WriteLine($"Error in CanUnlinkMugshots logic while getting folder names: {ex.Message}");
                        return false; // Safety: if paths are invalid, don't allow unlink
                    }
                })
                .ToProperty(this, x => x.CanUnlinkMugshots, scheduler: RxApp.MainThreadScheduler);

             // --- Setup for CanDelete ---
             // Condition: MugShotFolderPath is empty AND NO CorrespondingFolderPaths are assigned OR exist on disk.
             this.WhenAnyValue(
                     x => x.MugShotFolderPath,
                     // React to count changes in CorrespondingFolderPaths (add/remove)
                     // This doesn't react to changes *within* the strings or filesystem status,
                     // but provides reasonable reactivity for UI state changes (add/remove paths, change mugshot path).
                     x => x.CorrespondingFolderPaths.Count,
                     (mugshotPath, _) =>
                     {
                         bool mugshotIsEmpty = string.IsNullOrWhiteSpace(mugshotPath);

                         // Check if ANY path in the CorrespondingFolderPaths collection is assigned AND exists.
                         // If any such path exists, the item cannot be deleted.
                         bool anyModPathExists = CorrespondingFolderPaths.Any(path =>
                             !string.IsNullOrWhiteSpace(path) && System.IO.Directory.Exists(path)
                         );

                         // Can delete if mugshot path is empty AND no corresponding mod path exists on disk.
                         return mugshotIsEmpty && !anyModPathExists;
                     }
                 )
                 .DistinctUntilChanged()
                 .ToPropertyEx(this, x => x.CanDelete);
             
            // --- Command Initializations ---
            AddFolderPathCommand = ReactiveCommand.Create(AddFolderPath);
            BrowseFolderPathCommand = ReactiveCommand.Create<string>(BrowseFolderPath);
            RemoveFolderPathCommand = ReactiveCommand.Create<string>(RemoveFolderPath);
            BrowseMugshotFolderCommand = ReactiveCommand.Create(BrowseMugshotFolder);
            UnlinkMugshotDataCommand = ReactiveCommand.Create(UnlinkMugshotData, this.WhenAnyValue(x => x.CanUnlinkMugshots));
            UnlinkMugshotDataCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error unlinking mugshot data: {ex.Message}"));
            DeleteCommand = ReactiveCommand.Create(Delete, this.WhenAnyValue(x => x.CanDelete));
            DeleteCommand.ThrownExceptions.Subscribe(ex => Debug.WriteLine($"Error executing DeleteCommand: {ex.Message}"));

            this.WhenAnyValue(x => x.CorrespondingFolderPaths.Count)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(CanUnlinkMugshots))); // Manually trigger update if needed, though WhenAnyValue should handle it

            // Optionally, trigger RefreshNpcLists when ModKey or Paths change?
            // Could be intensive. Let's assume it's done during initial load for now.
            // this.WhenAnyValue(x => x.CorrespondingModKeys.Count, x => x.CorrespondingFolderPaths.Count) // Check counts
            //    .Throttle(TimeSpan.FromSeconds(1)) // Avoid rapid calls
            //    .ObserveOn(TaskPoolScheduler.Default) // Run on background thread
            //    .Subscribe(_ => RefreshNpcLists());
        }

        public ModSetting SaveToModel()
        {
            var model = new Models.ModSetting
            {
                DisplayName = DisplayName,
                CorrespondingModKeys = CorrespondingModKeys.ToList(),
                // Important: Create new lists/collections when saving to the model
                // to avoid potential issues with shared references if the VM is reused.
                CorrespondingFolderPaths = CorrespondingFolderPaths.ToList(),
                MugShotFolderPath = MugShotFolderPath, // Save the mugshot folder path
                NpcPluginDisambiguation = new Dictionary<FormKey, ModKey>(NpcPluginDisambiguation),
                AvailablePluginsForNpcs = new Dictionary<FormKey, List<ModKey>>(AvailablePluginsForNpcs),
                NpcFormKeys = this.NpcFormKeys,
                NpcFormKeysToDisplayName = this.NpcFormKeysToDisplayName,
                AmbiguousNpcFormKeys = this.AmbiguousNpcFormKeys,
                IsFaceGenOnlyEntry = IsFaceGenOnlyEntry,
                MergeInDependencyRecords = MergeInDependencyRecords,
                MergeInToolTip = MergeInToolTip,
                MergeInLabelColor = MergeInLabelColor,
                ModRecordOverrideHandlingMode = OverrideRecordOverrideHandlingMode,
                IsAutoGenerated = IsAutoGenerated,
                PluginsWithOverrideRecords = _pluginsWithOverrideRecords,
                HasAlteredMergeLogic = HasAlteredMergeLogic,
                LastKnownState = LastKnownState
            };
            return model;
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

                        FindPluginsWithOverrides(_parentVm.GetPluginProvider());
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
                        
                        FindPluginsWithOverrides(_parentVm.GetPluginProvider());
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
            
            FindPluginsWithOverrides(_parentVm.GetPluginProvider());
        }

        public void UpdateCorrespondingModKeys(IEnumerable<ModKey>? explicitModKeys = null)
        {
            // For auto-generated mods, keys are set explicitly and should not be
            // recalculated based on folder paths. If this method is called
            // without explicit keys (e.g., from the folder path subscription),
            // we should not modify the existing keys.
            if (IsAutoGenerated && explicitModKeys == null)
            {
                return;
            }
            
            List<ModKey> correspondingModKeys = new();
            if (IsAutoGenerated)
            {
                if (explicitModKeys != null)
                {
                    correspondingModKeys.AddRange(explicitModKeys);
                }
            }

            else
            {
                foreach (var path in CorrespondingFolderPaths)
                {
                    correspondingModKeys.AddRange(_aux.GetModKeysInDirectory(path, new(), false));
                }
            }

            CorrespondingModKeys.Clear();
            CorrespondingModKeys.AddRange(correspondingModKeys);
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
        public void RefreshNpcLists(HashSet<string> allFaceGenLooseFiles,
            Dictionary<string, HashSet<string>> allFaceGenBsaFiles,
            HashSet<ISkyrimModGetter> plugins)
        {
            NpcNames.Clear();
            NpcEditorIDs.Clear();
            NpcFormKeys.Clear();
            NpcFormKeysToDisplayName.Clear();
            AvailablePluginsForNpcs.Clear();
            // AmbiguousNpcFormKeys is cleared and repopulated below

            List<string> rejectionMessages = new();

            if (CorrespondingModKeys.Any() && (HasModPathsAssigned || IsAutoGenerated) && !IsFaceGenOnlyEntry)
            {
                // load plugins for downstream parsing
                Dictionary<ModKey, Dictionary<FormKey, IRaceGetter>> raceGetterCache = new();

                using (ContextualPerformanceTracer.Trace("RefreshNpcLists.MainLoop"))
                {
                    try
                    {
                        using (ContextualPerformanceTracer.Trace("RefreshNpcLists.CachePluginRaces"))
                        {
                            // If the plugin is not in the load order, cache its race to pass to downstream evaluation function so it doesn't waste time searching the main link cache for a FormKey that was never there
                            var loadOrderPlugins = _environmentStateProvider.LoadOrderModKeys.ToHashSet(); // source is IEnumerable; makes sure to convert.
                            foreach (var plugin in plugins)
                            {
                                foreach (var race in plugin.Races)
                                {
                                    if (!loadOrderPlugins.Contains(race.FormKey.ModKey)) 
                                    {
                                        raceGetterCache.TryAdd(plugin.ModKey, new Dictionary<FormKey, IRaceGetter>());
                                        raceGetterCache[plugin.ModKey].TryAdd(race.FormKey, race);
                                    }
                                }
                            }
                        }

                        foreach (var plugin in plugins)
                        {
                            foreach (var npcGetter in plugin.Npcs)
                            {
                                using (ContextualPerformanceTracer.Trace("RefreshNpcLists.RaceChecks"))
                                {
                                    IRaceGetter? sourcePluginRace = null;
                                    // first check and see if the race has been cached for the current plugin
                                    if (raceGetterCache.TryGetValue(plugin.ModKey, out var samePluginEntry) &&
                                        samePluginEntry.TryGetValue(npcGetter.Race.FormKey, out var cachedRace))
                                    {
                                        sourcePluginRace = cachedRace;
                                    }
                                    else
                                    {
                                        foreach (var entry in raceGetterCache.Values)
                                        {
                                            if (entry.TryGetValue(npcGetter.Race.FormKey, out var cachedRace2))
                                            {
                                                sourcePluginRace = cachedRace2;
                                            }
                                        }   
                                    }
                                    
                                    if (!_aux.IsValidAppearanceRace(npcGetter.Race.FormKey, npcGetter,
                                            out string rejectionMessage,
                                            sourcePluginRace:
                                            sourcePluginRace)) // if sourcePluginRace is null, falls back to searching the link cache
                                    {
                                        string message =
                                            $"Discarded {Auxilliary.GetNpcLogString(npcGetter)} from {DisplayName} because {rejectionMessage}.";
                                        //Debug.WriteLine(message);
                                        rejectionMessages.Add(message);
                                        continue;
                                    }
                                }

                                FormKey currentNpcKey = npcGetter.FormKey;

                                using (ContextualPerformanceTracer.Trace("RefreshNpcLists.FaceGenCheck"))
                                {
                                    // This is the cache of BSA files relevant to the *current mod setting*
                                    allFaceGenBsaFiles.TryGetValue(this.DisplayName, out var currentBsaCache);

                                    if (!FaceGenExists(currentNpcKey, allFaceGenLooseFiles,
                                            currentBsaCache ?? new HashSet<string>()) &&
                                        !npcGetter.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag
                                            .Traits))
                                    {
                                        string message =
                                            $"Discarded {Auxilliary.GetNpcLogString(npcGetter)} from {DisplayName} because it has no FaceGen and does not use a template.";
                                        //Debug.WriteLine(message);
                                        rejectionMessages.Add(message);
                                        continue;
                                    }
                                }

                                if (!AvailablePluginsForNpcs.TryGetValue(currentNpcKey, out var sourceList))
                                {
                                    sourceList = new List<ModKey>();
                                    AvailablePluginsForNpcs[currentNpcKey] = sourceList;
                                }

                                if (!sourceList.Contains(plugin.ModKey))
                                {
                                    sourceList.Add(plugin.ModKey);
                                }

                                using (ContextualPerformanceTracer.Trace("RefreshNpcLists.TemplateCheck"))
                                {
                                    if (npcGetter.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag
                                            .Traits))
                                    {
                                        string templateStr = npcGetter.Template?.FormKey.ToString() ??
                                                             "NULL TEMPLATE";

                                        NpcFormKeysToNotifications[currentNpcKey] = (
                                            IssueType: NpcIssueType.Template,
                                            IssueMessage: $"Despite having FaceGen files, this NPC from {plugin.ModKey.FileName} has the Traits flag so it inherits appearance from {templateStr}. If the selected Appearance Mod for this NPC doesn't match that of its Template, visual glitches can occur in-game.",
                                            FormKey: npcGetter.Template?.FormKey);
                                    }
                                }

                                using (ContextualPerformanceTracer.Trace("RefreshNpcLists.Finalization"))
                                {
                                    if (!NpcFormKeys.Contains(currentNpcKey))
                                    {
                                        NpcFormKeys.Add(currentNpcKey);
                                        var npc = npcGetter;
                                        string displayName = string.Empty;
                                        if (npc.Name is not null && !string.IsNullOrEmpty(npc.Name.String))
                                        {
                                            NpcNames.Add(npc.Name.String);
                                            displayName = npc.Name.String;
                                        }

                                        if (!string.IsNullOrEmpty(npc.EditorID))
                                        {
                                            NpcEditorIDs.Add(npc.EditorID);
                                            if (string.IsNullOrEmpty(displayName)) displayName = npc.EditorID;
                                        }

                                        if (string.IsNullOrEmpty(displayName)) displayName = npc.FormKey.ToString();
                                        NpcFormKeysToDisplayName.Add(currentNpcKey, displayName);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        string message =
                            $"Error loading NPC data for ModSetting '{DisplayName}': \n{ExceptionLogger.GetExceptionStack(ex)}";
                        Debug.WriteLine(message);
                        rejectionMessages.Add(message);
                    }
                }
            }

            else if (IsFaceGenOnlyEntry)
            {
                using (ContextualPerformanceTracer.Trace("RefreshNpcLists.FaceGenOnly"))
                {
                    foreach (var currentNpcKey in FaceGenOnlyNpcFormKeys)
                    {
                        NpcFormKeys.Add(currentNpcKey);
                        var sourcePlugin = currentNpcKey.ModKey;
                        var contexts =
                            _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(currentNpcKey);
                        var sourceContext = contexts.LastOrDefault();
                        if (sourceContext is not null)
                        {
                            var npc = sourceContext.Record;
                            string displayName = string.Empty;
                            if (npc.Name is not null && !string.IsNullOrEmpty(npc.Name.String))
                            {
                                NpcNames.Add(npc.Name.String);
                                displayName = npc.Name.String;
                            }

                            if (!string.IsNullOrEmpty(npc.EditorID))
                            {
                                NpcEditorIDs.Add(npc.EditorID);
                                if (string.IsNullOrEmpty(displayName)) displayName = npc.EditorID;
                            }

                            if (string.IsNullOrEmpty(displayName)) displayName = npc.FormKey.ToString();
                            NpcFormKeysToDisplayName.Add(currentNpcKey, displayName);
                        }
                        else
                        {
                            NpcFormKeysToDisplayName.Add(currentNpcKey, currentNpcKey.ToString());
                        }
                    }
                }
            }

            // --- Post-Processing: Populate NpcPluginDisambiguation, identify AmbiguousNpcFormKeys ---
            using (ContextualPerformanceTracer.Trace("RefreshNpcLists.PostProcessing"))
            {
                AmbiguousNpcFormKeys.Clear(); // Clear before repopulating

                foreach (var kvp in AvailablePluginsForNpcs)
                {
                    FormKey npcKey = kvp.Key;
                    List<ModKey> sources = kvp.Value;

                    if (sources.Count == 1)
                    {
                        NpcPluginDisambiguation.Remove(npcKey); // Not ambiguous, no disambiguation needed
                    }
                    else if (sources.Count > 1)
                    {
                        AmbiguousNpcFormKeys.Add(npcKey); // Mark as having multiple origins within this ModSetting

                        ModKey resolvedSource;
                        if (NpcPluginDisambiguation.TryGetValue(npcKey, out var preferredKey) &&
                            sources.Contains(preferredKey))
                        {
                            resolvedSource = preferredKey;
                        }
                        else
                        {
                            // No valid disambiguation or preferred key is no longer a source. Determine default.
                            var loadOrder = _environmentStateProvider?.LoadOrder;
                            if (loadOrder == null || loadOrder.ListedOrder == null)
                            {
                                Debug.WriteLine(
                                    $"CRITICAL ERROR for ModSetting '{DisplayName}': Load order not available from EnvironmentStateProvider. Cannot resolve default source for NPC {npcKey}. This NPC will be skipped.");
                                continue;
                            }

                            var loadOrderList = loadOrder.ListedOrder.Select(x => x.ModKey).ToList();
                            ModKey? winner = null;

                            // 1) Check if all sources are in the load order. If so, use load order winner.
                            if (sources.All(s => !s.IsNull && loadOrderList.Contains(s)))
                            {
                                winner = sources
                                    .OrderBy(s => loadOrderList.IndexOf(s))
                                    .FirstOrDefault();
                            }

                            // 2) If not, find a winner based on deepest valid master dependency.
                            var candidates = new List<(ModKey plugin, int masterCount)>();
                            if (winner == null || winner.Value.IsNull)
                            {
                                foreach (var source in sources)
                                {
                                    if (source.IsNull) continue;

                                    var masters = _pluginProvider.GetMasterPlugins(source, CorrespondingFolderPaths);
                                    bool allMastersValid = masters.All(m =>
                                        loadOrderList.Contains(m) || CorrespondingModKeys.Contains(m));

                                    if (allMastersValid)
                                    {
                                        candidates.Add((source, masters.Count));
                                    }
                                }

                                if (candidates.Any())
                                {
                                    var maxDepth = candidates.Max(c => c.masterCount);
                                    var topCandidates = candidates.Where(c => c.masterCount == maxDepth).ToList();
                                    if (topCandidates.Count == 1)
                                    {
                                        winner = topCandidates.First().plugin;
                                    }
                                }
                            }

                            // 3) Refined Fallback Logic
                            if (winner == null || winner.Value.IsNull)
                            {
                                // 3a) If there were valid candidates (all masters available) but they tied for depth,
                                // pick alphabetically from that list of valid candidates only.
                                if (candidates.Any())
                                {
                                    winner = candidates
                                        .Select(c => c.plugin)
                                        .OrderBy(s => s.FileName.String, StringComparer.OrdinalIgnoreCase)
                                        .FirstOrDefault();
                                }
                                // 3b) If there were NO valid candidates at all (all had missing masters),
                                // then and only then pick alphabetically from all original sources.
                                else
                                {
                                    winner = sources
                                        .Where(s => !s.IsNull)
                                        .OrderBy(s => s.FileName.String, StringComparer.OrdinalIgnoreCase)
                                        .FirstOrDefault();
                                }
                            }

                            // Assign the winning source
                            if (winner != null && !winner.Value.IsNull)
                            {
                                resolvedSource = winner.Value;
                                NpcPluginDisambiguation[npcKey] = resolvedSource; // Persist the default choice
                            }
                            else
                            {
                                var message =
                                    $"ERROR for ModSetting '{DisplayName}': NPC {npcKey} found in multiple associated plugins: {string.Join(", ", sources.Select(k => k.FileName))}, but no valid default source could be determined. This NPC will be skipped for this Mod Setting.";
                                Debug.WriteLine(message);
                                rejectionMessages.Add(message);
                                continue;
                            }
                        }
                    }
                }
            }

            // Cleanup NpcPluginDisambiguation: remove entries for NPCs no longer found or no longer ambiguous (i.e. now only in 1 plugin)
            var keysInDisambiguation =
                NpcPluginDisambiguation.Keys.ToList(); // ToList() for safe removal while iterating
            foreach (var npcKeyInDisambiguation in keysInDisambiguation)
            {
                if (!AvailablePluginsForNpcs.TryGetValue(npcKeyInDisambiguation, out var currentSources) ||
                    currentSources.Count <= 1)
                {
                    NpcPluginDisambiguation.Remove(npcKeyInDisambiguation);
                }
            }
            
            if (rejectionMessages.Any())
            {
                try
                {
                    string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Rejected NPCs");
                    Auxilliary.CreateDirectoryIfNeeded(logDirectory, Auxilliary.PathType.Directory);
                    string safeDisplayName = Auxilliary.MakeStringPathSafe(this.DisplayName);
                    if (!string.IsNullOrWhiteSpace(safeDisplayName))
                    {
                        string logFilePath = Path.Combine(logDirectory, $"{safeDisplayName}.txt");
                        File.WriteAllLines(logFilePath, rejectionMessages);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Could not write rejection log file for {this.DisplayName}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Checks if FaceGen for the given FormKey exists using pre-cached sets of file paths.
        /// </summary>
        public bool FaceGenExists(FormKey formKey, HashSet<string> looseFileCache, HashSet<string> bsaFileCache)
        {
            var faceGenRelPaths = Auxilliary.GetFaceGenSubPathStrings(formKey);

            // Normalize paths for consistent lookups. Use forward slashes and lowercase.
            string faceGenMeshRelPath = Path.Combine("Meshes", faceGenRelPaths.MeshPath).Replace('\\', '/').ToLowerInvariant();
            string faceGenTexRelPath = Path.Combine("Textures", faceGenRelPaths.TexturePath).Replace('\\', '/').ToLowerInvariant();

            // The check is now a near-instantaneous HashSet lookup.
            if (looseFileCache.Contains(faceGenMeshRelPath) || looseFileCache.Contains(faceGenTexRelPath))
            {
                return true;
            }

            if (bsaFileCache.Contains(faceGenMeshRelPath) || bsaFileCache.Contains(faceGenTexRelPath))
            {
                return true;
            }

            return false;
        }
        
        /// <summary>
        /// Sets the source plugin for a single NPC that has multiple potential source plugins within this ModSetting.
        /// This method directly updates NpcPluginDisambiguation and NpcSourcePluginMap.
        /// Returns true if the source was successfully updated internally.
        /// </summary>
        public bool SetSingleNpcSourcePlugin(FormKey npcKey, ModKey newSourcePlugin)
        {
            // This first check is important: if the NPC is no longer considered ambiguous 
            // (e.g., due to a background refresh or other changes), don't proceed.
            if (!AmbiguousNpcFormKeys.Contains(npcKey))
            {
                Debug.WriteLine($"NPC {npcKey} ({NpcFormKeysToDisplayName.GetValueOrDefault(npcKey, "N/A")}) in ModSetting '{DisplayName}' is no longer ambiguous or choice is not needed. Ignoring SetSingleNpcSourcePlugin.");
                return false; 
            }
            
            // Check if the chosen plugin is actually one of the CorrespondingModKeys associated with this ModSetting.
            if (!CorrespondingModKeys.Contains(newSourcePlugin))
            {
                Debug.WriteLine($"Error: Plugin {newSourcePlugin.FileName} is not a valid source choice within ModSetting '{DisplayName}' because it's not in CorrespondingModKeys.");
                ScrollableMessageBox.ShowError($"Cannot set {newSourcePlugin.FileName} as source for {NpcFormKeysToDisplayName.GetValueOrDefault(npcKey, npcKey.ToString())} because {newSourcePlugin.FileName} is not one of the plugins associated with the '{DisplayName}' mod entry.", "Invalid Source Plugin");
                return false;
            }

            // Further check: Does the newSourcePlugin *actually* contain this NPC according to our last full scan?
            // This requires having the result of the last `npcFoundInPlugins` scan or re-querying.
            // For performance, we might skip this very deep check here and rely on the initial population
            // of AvailableSourcePlugins in VM_ModsMenuMugshot to be correct.
            // If an invalid choice were somehow presented and selected, NpcSourcePluginMap would be briefly inconsistent
            // until the next full RefreshNpcLists(). This is a trade-off.

            bool disambiguationChanged = false;
            if (!NpcPluginDisambiguation.TryGetValue(npcKey, out var currentDisambiguation) || currentDisambiguation != newSourcePlugin)
            {
                NpcPluginDisambiguation[npcKey] = newSourcePlugin;
                disambiguationChanged = true; // The user's preference has been recorded/changed.
                Debug.WriteLine($"NpcPluginDisambiguation updated: NPC {npcKey} in ModSetting '{DisplayName}' now prefers {newSourcePlugin.FileName}.");
            }
            
            // If either the user's preference changed or the actual map entry changed, return true.
            // Typically, if disambiguationChanged is true, mapChanged will also be true.
            if (disambiguationChanged)
            {
                // We are intentionally NOT calling RefreshNpcLists() here to avoid lag.
                // The NpcSourcePluginMap is updated directly for this NPC.
                // Other aspects that RefreshNpcLists() handles (like re-evaluating AmbiguousNpcFormKeys
                // if this change made the NPC no longer ambiguous) will not be updated until the next full refresh.
                // This is acceptable for this specific UI interaction.
                return true;
            }
            else
            {
                Debug.WriteLine($"No change made for NPC {npcKey} in ModSetting '{DisplayName}'; new source {newSourcePlugin.FileName} was already the effective one.");
                return false; 
            }
        }

        /// <summary>
        /// Sets a given plugin as the source for all NPCs in this ModSetting that are found within that plugin
        /// AND are listed in AmbiguousNpcFormKeys.
        /// Updates NpcPluginDisambiguation and NpcSourcePluginMap directly.
        /// </summary>
        /// <returns>A list of FormKeys for NPCs whose source was actually changed.</returns>
        public List<FormKey> SetSourcePluginForAllApplicableNpcs(ModKey newGlobalSourcePlugin, bool showMessages = true)
        {
            var changedNpcKeys = new List<FormKey>();

            if (!CorrespondingModKeys.Contains(newGlobalSourcePlugin))
            {
                Debug.WriteLine($"Error: Plugin {newGlobalSourcePlugin.FileName} is not part of this ModSetting '{DisplayName}'. Cannot set as global source.");
                if (showMessages)
                {
                    ScrollableMessageBox.ShowError($"Plugin {newGlobalSourcePlugin.FileName} is not associated with the mod entry '{DisplayName}'.", "Invalid Global Source Plugin");
                }
                return changedNpcKeys;
            }

            string? pluginFilePath = null;
            foreach (var dirPath in CorrespondingFolderPaths)
            {
                string potentialPath = Path.Combine(dirPath, newGlobalSourcePlugin.FileName);
                if (File.Exists(potentialPath))
                {
                    pluginFilePath = potentialPath;
                    break;
                }
            }

            if (pluginFilePath == null)
            {
                Debug.WriteLine($"Error: Could not find plugin file {newGlobalSourcePlugin.FileName} for ModSetting '{DisplayName}'.");
                if (showMessages)
                {
                    ScrollableMessageBox.ShowError($"Could not locate the file for plugin {newGlobalSourcePlugin.FileName} within the specified mod folders for '{DisplayName}'.", "Plugin File Not Found");
                }
                return changedNpcKeys;
            }

            HashSet<FormKey> npcsActuallyInSelectedPlugin = new HashSet<FormKey>();
            try
            {
                using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginFilePath, _skyrimRelease);
                foreach (var npcGetter in mod.Npcs)
                {
                    npcsActuallyInSelectedPlugin.Add(npcGetter.FormKey);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Error reading NPCs from {newGlobalSourcePlugin.FileName} for ModSetting '{DisplayName}': {e.Message}");
                if (showMessages)
                {
                    ScrollableMessageBox.ShowError($"Error reading NPC data from plugin {newGlobalSourcePlugin.FileName}:\n{e.Message}", "Plugin Read Error");
                }
                return changedNpcKeys;
            }
            
            // Iterate a copy of AmbiguousNpcFormKeys if modifications to it are possible indirectly,
            // though direct updates to NpcSourcePluginMap shouldn't affect AmbiguousNpcFormKeys here.
            foreach (FormKey ambiguousNpcKey in AmbiguousNpcFormKeys.ToList()) 
            {
                // If this multi-origin NPC *can* be sourced from the 'newGlobalSourcePlugin'
                if (npcsActuallyInSelectedPlugin.Contains(ambiguousNpcKey))
                {
                    bool disambiguationChanged = false;
                    if (!NpcPluginDisambiguation.TryGetValue(ambiguousNpcKey, out var currentDisambiguation) || currentDisambiguation != newGlobalSourcePlugin)
                    {
                        NpcPluginDisambiguation[ambiguousNpcKey] = newGlobalSourcePlugin;
                        disambiguationChanged = true;
                    }

                    if (disambiguationChanged)
                    {
                        changedNpcKeys.Add(ambiguousNpcKey);
                        Debug.WriteLine($"ModSetting '{DisplayName}': Globally set source for NPC {ambiguousNpcKey} to {newGlobalSourcePlugin.FileName} (direct map update).");
                    }
                }
            }

            if (showMessages)
            {
                if (changedNpcKeys.Any())
                {
                    ScrollableMessageBox.Show($"Set {newGlobalSourcePlugin.FileName} as the source for {changedNpcKeys.Count} applicable NPC(s) in '{DisplayName}'.", "Global Source Updated");
                }
                else
                {
                    ScrollableMessageBox.Show($"No NPC source plugin assignments were changed for '{DisplayName}'. This may be because all relevant NPCs already used {newGlobalSourcePlugin.FileName} as their source, or no ambiguous NPCs are present in that plugin.", "No Changes Made");
                }
            }
            return changedNpcKeys;
        }

        /// <summary>
        /// Sets the source plugin for all applicable ambiguous NPCs and notifies the parent
        /// VM to refresh the UI. This is called by the context menu command.
        /// </summary>
        public void SetAndNotifySourcePluginForAll(ModKey newGlobalSourcePlugin)
        {
            // Call the existing logic to update the data model, but suppress the pop-up messages.
            var changedKeys = SetSourcePluginForAllApplicableNpcs(newGlobalSourcePlugin, showMessages: false);

            if (changedKeys.Any())
            {
                // Use the existing reference to the parent VM to signal that the mugshot
                // panel needs to be reloaded to reflect the data changes.
                _parentVm.NotifyMultipleNpcSourcesChanged(this);

                // Also notify the NpcSelectionBar to refresh its view, in case this
                // command was initiated from the NpcsView.
                _parentVm.RequestNpcSelectionBarRefresh();
            }
        }
        
        private void UnlinkMugshotData()
        {
            if (!CanUnlinkMugshots) return; // Should be caught by CanExecute, but defensive check

            string originalMugshotPath = this.MugShotFolderPath;
            string mugshotDirName = Path.GetFileName(originalMugshotPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            // Confirm with user
            if (!ScrollableMessageBox.Confirm(
                $"Are you sure you want to unlink the mugshot folder '{mugshotDirName}' from '{this.DisplayName}'?\n\n" +
                $"This will create a new, separate entry for '{mugshotDirName}' (mugshots only), " +
                $"and '{this.DisplayName}' will no longer have these mugshots associated.",
                "Confirm Unlink Mugshot Data"))
            {
                return;
            }

            // 1. Create the new "Mugshot-Only" VM
            // It will be mugshot-only by definition because it has no CorrespondingFolderPaths/CorrespondingModKeys initially
            var newMugshotOnlyVm = new VM_ModSetting(displayName: mugshotDirName, mugshotPath: originalMugshotPath, parentVm: _parentVm, aux: _aux, bsaHandler: _bsaHandler, pluginProvider: _pluginProvider);
            // Ensure IsMugshotOnlyEntry is correctly set based on its initial state
            newMugshotOnlyVm.IsMugshotOnlyEntry = true; 
            // It won't have NPC lists immediately, that will be populated if/when VM_Mods calls RefreshNpcLists on all.
            // Or, if it's purely for mugshots, NPC lists aren't relevant for *its* data.

            // 2. Modify the current VM (this instance) to be "Data-Only" regarding this mugshot path
            this.MugShotFolderPath = string.Empty; // Clear the mugshot path
            // HasValidMugshots will update reactively via RecalculateMugshotValidity call below.
            // IsMugshotOnlyEntry status of 'this' vm might also change if it now has no paths at all.
            // This will be re-evaluated by VM_Mods.PopulateModSettings next time or by logic within VM_Mods
            this.IsMugshotOnlyEntry = !this.CorrespondingFolderPaths.Any() && !this.CorrespondingModKeys.Any();


            // 3. Add the new VM to VM_Mods and refresh
            _parentVm.AddAndRefreshModSetting(newMugshotOnlyVm); // Parent VM handles adding and refreshing its lists

            // 4. Trigger a recalculation of valid mugshots for the current (now data-only) VM
            _parentVm.RecalculateMugshotValidity(this);

            // 5. Notify selection bar to refresh appearance sources for current NPC if necessary
            _parentVm.RequestNpcSelectionBarRefresh();
        }
        
        // Method executed by DeleteCommand ***
        private void Delete()
        {
            // The CanExecute already prevents this if paths/mugshot are assigned,
            // but a final check is good practice.
            if (!CanDelete)
            {
                Debug.WriteLine($"Attempted to delete VM_ModSetting '{DisplayName}' but CanDelete was false.");
                return;
            }

            // Confirm with user
            if (!ScrollableMessageBox.Confirm(
                    $"Are you sure you want to permanently delete the entry for '{DisplayName}'?\n\n" +
                    "This action cannot be undone.",
                    "Confirm Deletion"))
            {
                return;
            }

            // Request the parent VM to remove this instance from its list
            _parentVm.RemoveModSetting(this);

            // Note: The removal from the underlying Settings model list
            // will happen when VM_Mods.SaveModSettingsToModel is called.
        }

        // [VM_ModSetting.cs] - Full Code After Modifications
        // located in NPC_Plugin_Chooser_2.View_Models/VM_ModSetting.cs

        public async Task FindPluginsWithOverrides(PluginProvider pluginProvider)
        {
            var appearanceTypesToSkip = new HashSet<Type>()
            {
                typeof(INpcGetter),
            };
            
            using (ContextualPerformanceTracer.Trace("FindPluginsWithOverrides", this.DisplayName))
            {
                _pluginsWithOverrideRecords.Clear();
                var overridesCache = _parentVm.GetOverrideCache();

                foreach (var modKey in CorrespondingModKeys)
                {
                    using (ContextualPerformanceTracer.Trace("FPO.ModKeyLoop", modKey.FileName))
                    {
                        if (modKey.Equals(_environmentStateProvider.AbsoluteBasePlugin))
                        {
                            continue;
                        }

                        ISkyrimModGetter? plugin;
                        string? pluginSourcePath;
                        bool pluginFound;
                        using (ContextualPerformanceTracer.Trace("FPO.TryGetPlugin", modKey.FileName))
                        {
                            pluginFound = pluginProvider.TryGetPlugin(modKey, new HashSet<string>(CorrespondingFolderPaths), out plugin, out pluginSourcePath) && plugin != null && pluginSourcePath != null;
                        }

                        if (!pluginFound)
                        {
                            continue;
                        }

                        var cacheKey = (pluginSourcePath: pluginSourcePath!, modKey);

                        // Use GetOrAdd to atomically get the result or create it if it doesn't exist.
                        bool hasOverrides = overridesCache.GetOrAdd(cacheKey, key =>
                        {
                            // This "value factory" code only runs if the key is not already in the cache.
                            // The expensive work is now protected from race conditions.
                            bool result = false;

                            using (ContextualPerformanceTracer.Trace("FPO.RecordLoop", key.modKey.FileName))
                            {
                                // Iterates lazily. Stops pulling records from the plugin file as soon as the break is hit.
                                foreach (var record in Auxilliary.LazyEnumerateMajorRecords(plugin, appearanceTypesToSkip))
                                {
                                    if (!CorrespondingModKeys.Contains(record.FormKey.ModKey))
                                    {
                                        result = true;
                                        break; // This now prevents further enumeration and parsing
                                    }
                                }
                            }
                            return result;
                        });

                        if (hasOverrides)
                        {
                            _pluginsWithOverrideRecords.Add(modKey);
                        }
                    }
                }
            }
        }
    }
}