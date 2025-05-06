// View Models/VM_Mods.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq; // Added for Throttle, ObserveOn
using System.Reactive.Subjects; // Added for Subject
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks; // Added for Task
using System.Windows; // For MessageBox
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat; // For Locator


namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_Mods : ReactiveObject
    {
        private readonly Settings _settings;
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly VM_NpcSelectionBar _npcSelectionBar; // To access AllNpcs and navigate
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly Lazy<VM_MainWindow> _lazyMainWindowVm; // *** NEW: To switch tabs ***

        // --- Filtering Properties (Left Panel) ---
        [Reactive] public string NameFilterText { get; set; } = string.Empty;
        [Reactive] public string PluginFilterText { get; set; } = string.Empty;
        [Reactive] public ModNpcSearchType SelectedNpcSearchType { get; set; } = ModNpcSearchType.Name;
        [Reactive] public string NpcSearchText { get; set; } = string.Empty;
        public Array AvailableNpcSearchTypes => Enum.GetValues(typeof(ModNpcSearchType));
        [Reactive] public bool IsLoadingNpcData { get; private set; }

        // --- Data Lists (Left Panel) ---
        private List<VM_ModSetting> _allModSettingsInternal = new();
        public IReadOnlyList<VM_ModSetting> AllModSettings => _allModSettingsInternal; // Public access
        public ObservableCollection<VM_ModSetting> ModSettingsList { get; } = new();

        // --- Right Panel Properties ---
        [Reactive] public VM_ModSetting? SelectedModForMugshots { get; private set; }
        public ObservableCollection<VM_ModNpcMugshot> CurrentModNpcMugshots { get; } = new();
        [Reactive] public bool IsLoadingMugshots { get; private set; }
        [Reactive] public bool HasUsedMugshotZoom { get; set; } = false; // Track zoom state for right panel

        // Subject for triggering right panel image refresh
        private readonly Subject<Unit> _refreshMugshotSizesSubject = new Subject<Unit>();
        public IObservable<Unit> RefreshMugshotSizesObservable => _refreshMugshotSizesSubject.AsObservable();

        // --- Commands ---
        public ReactiveCommand<VM_ModSetting, Unit> ShowMugshotsCommand { get; }

        // Expose for binding in VM_ModSetting commands
        public string ModsFolderSetting => _settings.ModsFolder;
        public string MugshotsFolderSetting => _settings.MugshotsFolder; // Needed for BrowseMugshotFolder
        public SkyrimRelease SkyrimRelease => _settings.SkyrimRelease;

        // *** Updated Constructor Signature ***
        public VM_Mods(Settings settings, EnvironmentStateProvider environmentStateProvider, VM_NpcSelectionBar npcSelectionBar, NpcConsistencyProvider consistencyProvider, Lazy<VM_MainWindow> lazyMainWindowVm)
        {
            _settings = settings;
            _environmentStateProvider = environmentStateProvider;
            _npcSelectionBar = npcSelectionBar;
            _consistencyProvider = consistencyProvider;
            _lazyMainWindowVm = lazyMainWindowVm;

            if (!_npcSelectionBar.AllNpcs.Any())
            {
                 System.Diagnostics.Debug.WriteLine("Warning: VM_Mods initialized before VM_NpcSelectionBar potentially finished. NPC data might be incomplete for linking.");
            }

            // Initialize commands
            ShowMugshotsCommand = ReactiveCommand.CreateFromTask<VM_ModSetting>(ShowMugshotsAsync);
            ShowMugshotsCommand.ThrownExceptions.Subscribe(ex =>
            {
                MessageBox.Show($"Error loading mugshots: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                IsLoadingMugshots = false;
            });


            PopulateModSettings(); // Populates and sorts _allModSettingsInternal

            // --- Setup Filter Reaction ---
            this.WhenAnyValue(x => x.NameFilterText, x => x.PluginFilterText, x => x.NpcSearchText, x => x.SelectedNpcSearchType) // Added NpcSearchType
                .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ApplyFilters());

            // --- Clear right panel when filters change? (Optional) ---
            // this.WhenAnyValue(...) above could also clear SelectedModForMugshots/CurrentModNpcMugshots
            // This prevents showing potentially incorrect mugshots if the selected mod is filtered out.
            this.WhenAnyValue(x => x.NameFilterText, x => x.PluginFilterText, x => x.NpcSearchText, x => x.SelectedNpcSearchType)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => {
                    // If the currently selected mod for mugshots is no longer in the filtered list, clear the right panel
                    if (SelectedModForMugshots != null && !ModSettingsList.Contains(SelectedModForMugshots))
                    {
                        SelectedModForMugshots = null;
                        CurrentModNpcMugshots.Clear();
                    }
                });


            ApplyFilters(); // Apply initial filter
        }

        private async Task ShowMugshotsAsync(VM_ModSetting selectedModSetting)
        {
            if (selectedModSetting == null || !selectedModSetting.HasValidMugshots || string.IsNullOrWhiteSpace(selectedModSetting.MugShotFolderPath))
            {
                SelectedModForMugshots = null;
                CurrentModNpcMugshots.Clear();
                return;
            }

            IsLoadingMugshots = true;
            SelectedModForMugshots = selectedModSetting;
            CurrentModNpcMugshots.Clear();
            HasUsedMugshotZoom = false; // Reset zoom state

            var mugshotVMs = new List<VM_ModNpcMugshot>();

            try
            {
                await Task.Run(() => // Run scanning and VM creation in background
                {
                    var imageFiles = Directory.EnumerateFiles(selectedModSetting.MugShotFolderPath, "*.*", SearchOption.AllDirectories)
                                            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^[0-9A-F]{8}\.(png|jpg|jpeg|bmp)$", RegexOptions.IgnoreCase))
                                            .ToList();

                    foreach (var imagePath in imageFiles)
                    {
                        string fileName = Path.GetFileName(imagePath);
                        string hexPart = Path.GetFileNameWithoutExtension(fileName);
                        DirectoryInfo? pluginDir = new FileInfo(imagePath).Directory;

                        if (pluginDir != null && Regex.IsMatch(pluginDir.Name, @"^.+\.(esm|esp|esl)$", RegexOptions.IgnoreCase))
                        {
                            string pluginName = pluginDir.Name;
                            string formIdHex = hexPart.Substring(Math.Max(0, hexPart.Length - 6)); // Get last 6 chars, safely
                            string formKeyString = $"{formIdHex}:{pluginName}";

                            try
                            {
                                FormKey npcFormKey = FormKey.Factory(formKeyString);
                                string npcDisplayName = "Not Loaded (" + formKeyString + ")";
                                if (selectedModSetting.NpcFormKeysToDisplayName.TryGetValue(npcFormKey, out var name))
                                {
                                    npcDisplayName = name;
                                }
                                else if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey,
                                             out var npcGetter))
                                {
                                    if (npcGetter.Name != null && npcGetter.Name.String != null)
                                    {
                                        npcDisplayName = npcGetter.Name.String;
                                    }
                                    else if (npcGetter.EditorID != null)
                                    {
                                        npcDisplayName = npcGetter.EditorID;
                                    }
                                }

                                mugshotVMs.Add(new VM_ModNpcMugshot(imagePath, npcFormKey, npcDisplayName, this));
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error creating FormKey or VM for mugshot {imagePath}: {ex.Message}");
                                // Optionally add a placeholder VM or skip
                            }
                        }
                    }
                });

                // Update collection on UI thread
                CurrentModNpcMugshots.Clear(); // Ensure it's clear before adding
                foreach (var vm in mugshotVMs.OrderBy(vm => vm.NpcDisplayName)) // Sort by NPC name
                {
                    CurrentModNpcMugshots.Add(vm);
                }
            }
            catch (Exception ex)
            {
                 // Catch errors during Task.Run or directory enumeration
                 MessageBox.Show($"Failed to load mugshots from {selectedModSetting.MugShotFolderPath}:\n{ex.Message}", "Mugshot Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                 CurrentModNpcMugshots.Clear(); // Clear potentially partial list
            }
            finally
            {
                IsLoadingMugshots = false;
                 // Trigger initial image sizing after VMs are added
                 if (CurrentModNpcMugshots.Any())
                 {
                      _refreshMugshotSizesSubject.OnNext(Unit.Default);
                 }
            }
        }

        // *** NEW: Method to handle navigation triggered by VM_ModNpcMugshot ***
        public void NavigateToNpc(FormKey npcFormKey)
        {
             // 1. Switch Tab
             _lazyMainWindowVm.Value.IsNpcsTabSelected = true;

             // 2. Find and Select NPC in NpcSelectionBar
             RxApp.MainThreadScheduler.Schedule(() => // Ensure runs on UI thread after tab switch might complete
             {
                 var npcToSelect = _npcSelectionBar.AllNpcs.FirstOrDefault(npc => npc.NpcFormKey == npcFormKey);
                 if (npcToSelect != null)
                 {
                     // Check if filters need adjustment or clearing
                     // This is complex: maybe just clear filters? Or try applying a FormKey filter?
                     // Simplest: Clear filters to ensure the NPC is visible.
                     _npcSelectionBar.SearchText1 = "";
                     _npcSelectionBar.SearchText2 = "";
                     _npcSelectionBar.SearchText3 = "";

                     // Ensure the NPC is in the filtered list after clearing filters (should be)
                     if (!_npcSelectionBar.FilteredNpcs.Contains(npcToSelect))
                     {
                         _npcSelectionBar.ApplyFilter(false); // Re-apply (now empty) filter
                     }

                     // Now select the NPC
                     _npcSelectionBar.SelectedNpc = npcToSelect;

                     // Attempt to scroll into view (Requires reference or messaging)
                     // This is often better handled with an Attached Property or Behavior on the ListBox in the View.
                     // For now, just selecting it is the primary goal.
                     _npcSelectionBar.RequestScrollIntoView(npcToSelect); // Assuming VM_NpcSelectionBar has such a method/mechanism
                 }
                 else
                 {
                     MessageBox.Show($"Could not find NPC with FormKey {npcFormKey} in the main NPC list.", "NPC Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                 }
             });
        }

        // *** NEW: Method called by VM_ModSetting to recalculate mugshot validity ***
        public void RecalculateMugshotValidity(VM_ModSetting modSetting)
        {
             modSetting.HasValidMugshots = CheckMugshotValidity(modSetting.MugShotFolderPath);
             // If this was the selected mod, refresh the right panel
             if (SelectedModForMugshots == modSetting)
             {
                 ShowMugshotsCommand.Execute(modSetting).Subscribe(); // Re-run the command
             }
        }

        private bool CheckMugshotValidity(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return false;
            }
            try
            {
                // Check if it contains at least one valid image file directly or in a plugin subfolder
                return Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                                .Any(f => Regex.IsMatch(Path.GetFileName(f), @"^[0-9A-F]{8}\.(png|jpg|jpeg|bmp)$", RegexOptions.IgnoreCase) &&
                                            new FileInfo(f).Directory?.Name?.Contains('.') == true); // Basic check for plugin-like folder name
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking mugshot validity for {path}: {ex.Message}");
                return false; // Treat errors as invalid
            }
        }

        public void PopulateModSettings()
        {
            _allModSettingsInternal.Clear();
            var warnings = new List<string>();
            var loadedDisplayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var linkedModKeys = new HashSet<ModKey>(); // Tracks keys linked via settings or folders
            var tempList = new List<VM_ModSetting>();
            IsLoadingNpcData = true; // Still indicates background NPC list loading later

            // --- Ensure Environment is Ready ---
            if (!_environmentStateProvider.EnvironmentIsValid || _environmentStateProvider.LoadOrder == null)
            {
                warnings.Add("Environment is not valid. Cannot accurately link plugins.");
                // Proceed cautiously, might load from settings but linking will be incomplete.
            }
            var enabledKeys = _environmentStateProvider.EnvironmentIsValid
                            ? _environmentStateProvider.LoadOrder.Keys.ToHashSet()
                            : new HashSet<ModKey>(); // Empty set if environment is invalid


            // Phase 1: Load existing data from Settings
            foreach (var setting in _settings.ModSettings)
            {
                 if (string.IsNullOrWhiteSpace(setting.DisplayName)) continue;
                 var vm = new VM_ModSetting(setting, this); // Constructor now handles model.ModKeys
                 // Attempt to link mugshot path based on display name
                 if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder))
                 {
                     string potentialMugshotPath = Path.Combine(_settings.MugshotsFolder, vm.DisplayName);
                     if (Directory.Exists(potentialMugshotPath)) { vm.MugShotFolderPath = potentialMugshotPath; }
                 }
                 tempList.Add(vm);
                 loadedDisplayNames.Add(vm.DisplayName);
                 // Add all linked keys from the existing setting to tracking
                 if (vm.CorrespondingModKeys.Any())
                 {
                     foreach(var key in vm.CorrespondingModKeys) { linkedModKeys.Add(key); }
                 }
            }

            // Phase 2a: Create potential VM_ModSettings from Mugshot folders (if not already loaded)
             var vmsFromMugshots = new List<VM_ModSetting>();
             if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder))
             {
                 try
                 {
                    var mugShotDirs = Directory.EnumerateDirectories(_settings.MugshotsFolder);
                    foreach (var dirPath in mugShotDirs)
                    {
                        string folderName = Path.GetFileName(dirPath);
                        // Only create if a VM with this DisplayName wasn't loaded from settings
                        if (!loadedDisplayNames.Contains(folderName))
                        {
                            // Create VM with mugshot path, but defer adding to tempList
                            var vm = new VM_ModSetting(folderName, dirPath, this); // Uses constructor for mugshot-only initially
                            vmsFromMugshots.Add(vm);
                        }
                    }
                 }
                 catch (Exception ex) { warnings.Add($"Error scanning Mugshots folder '{_settings.MugshotsFolder}': {ex.Message}"); }
             }

            // Phase 2b: Scan Mod Folders, Link Paths and ENABLED Plugins
             if (!string.IsNullOrWhiteSpace(_settings.ModsFolder) && Directory.Exists(_settings.ModsFolder))
             {
                 try
                 {
                     foreach (var modFolderPath in Directory.EnumerateDirectories(_settings.ModsFolder))
                     {
                         string modFolderName = Path.GetFileName(modFolderPath);
                         var foundEnabledKeysInFolder = new List<ModKey>();

                         // Scan for enabled plugins within this folder
                         try
                         {
                             foreach (var filePath in Directory.EnumerateFiles(modFolderPath, "*.es*", SearchOption.TopDirectoryOnly)) // Only top level
                             {
                                string fileNameWithExt = Path.GetFileName(filePath);
                                if (fileNameWithExt.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                                    fileNameWithExt.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                                    fileNameWithExt.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                                {
                                     try
                                     {
                                         ModKey parsedKey = ModKey.FromFileName(fileNameWithExt);
                                         if (enabledKeys.Contains(parsedKey)) // Check if the found plugin is enabled
                                         {
                                             foundEnabledKeysInFolder.Add(parsedKey);
                                         }
                                     }
                                     catch (Exception parseEx) { warnings.Add($"Could not parse plugin filename '{fileNameWithExt}' in folder '{modFolderName}': {parseEx.Message}"); }
                                 }
                             }
                         }
                         catch (Exception fileScanEx) { warnings.Add($"Error scanning files in Mod folder '{modFolderName}': {fileScanEx.Message}"); }


                         // Try to link to existing VM (from settings or mugshots) or create new VM
                         var existingVm = tempList.FirstOrDefault(vm => vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));
                         var mugshotVm = vmsFromMugshots.FirstOrDefault(vm => vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));

                         if (existingVm != null) // Priority 1: Link to VM loaded from settings
                         {
                             if (!existingVm.CorrespondingFolderPaths.Contains(modFolderPath, StringComparer.OrdinalIgnoreCase))
                                existingVm.CorrespondingFolderPaths.Add(modFolderPath);
                             foreach (var key in foundEnabledKeysInFolder)
                             {
                                 if (!existingVm.CorrespondingModKeys.Contains(key)) existingVm.CorrespondingModKeys.Add(key);
                                 linkedModKeys.Add(key); // Track linked key
                             }
                         }
                         else if (mugshotVm != null) // Priority 2: Link to VM created from mugshots
                         {
                             // Add folder path and keys to the mugshot VM
                             mugshotVm.CorrespondingFolderPaths.Add(modFolderPath);
                             foreach (var key in foundEnabledKeysInFolder)
                             {
                                 if (!mugshotVm.CorrespondingModKeys.Contains(key)) mugshotVm.CorrespondingModKeys.Add(key);
                                 linkedModKeys.Add(key); // Track linked key
                             }
                             // Now add this completed VM (which is no longer mugshot-only) to the main list
                             tempList.Add(mugshotVm);
                             vmsFromMugshots.Remove(mugshotVm); // Remove from temp mugshot list
                             loadedDisplayNames.Add(mugshotVm.DisplayName); // Mark display name as processed
                         }
                         else if (foundEnabledKeysInFolder.Any()) // Priority 3: Create new VM if plugins were found and no existing match
                         {
                             var newVm = new VM_ModSetting(modFolderName, this) // Base constructor
                             {
                                 CorrespondingFolderPaths = new ObservableCollection<string> { modFolderPath },
                                 CorrespondingModKeys = new ObservableCollection<ModKey>(foundEnabledKeysInFolder)
                             };
                             tempList.Add(newVm);
                             loadedDisplayNames.Add(newVm.DisplayName); // Mark display name as processed
                             foreach(var key in foundEnabledKeysInFolder) { linkedModKeys.Add(key); } // Track linked keys
                         }
                         // Else: Folder exists but contains no enabled plugins and doesn't match existing VMs - do nothing.
                     }
                 }
                 catch (Exception ex) { warnings.Add($"Error scanning Mods folder '{_settings.ModsFolder}' for linking: {ex.Message}"); }
             }

            // Phase 2c: Add remaining mugshot-only VMs (those that couldn't be linked to a mod folder)
            foreach(var mugshotVm in vmsFromMugshots)
            {
                 // These VMs definitely don't have paths or keys linked in Phase 2b
                 mugshotVm.IsMugshotOnlyEntry = true;
                 if (!tempList.Any(existing => existing.DisplayName.Equals(mugshotVm.DisplayName, StringComparison.OrdinalIgnoreCase)))
                 {
                      tempList.Add(mugshotVm);
                 }
            }

            // *** Phase 3 (Orphaned Key Handling) is now removed/integrated into Phase 2b ***
            // We no longer rely on VM_NpcSelectionBar here.

            // Set IsMugshotOnlyEntry flag correctly for all VMs *before* refreshing NPC lists
            foreach (var vm in tempList)
            {
                 vm.IsMugshotOnlyEntry = !(vm.CorrespondingFolderPaths.Any() || vm.CorrespondingModKeys.Any());
            }

            // Asynchronously refresh NPC lists for each created/updated VM
            var refreshTasks = tempList.Select(vm => Task.Run(() => vm.RefreshNpcLists())).ToList();
            Task.WhenAll(refreshTasks).ContinueWith(t =>
            {
                // This continuation runs on a background thread by default
                if (t.IsFaulted)
                {
                    // Flatten and log aggregate exceptions if needed
                    var flattenedExceptions = t.Exception?.Flatten().InnerExceptions;
                    if (flattenedExceptions != null)
                    {
                         foreach(var ex in flattenedExceptions)
                         {
                              Debug.WriteLine($"Error during async NPC list refresh: {ex.Message}");
                         }
                    }
                }

                // *** Dispatch UI updates back to the main thread ***
                RxApp.MainThreadScheduler.Schedule(() =>
                {
                    IsLoadingNpcData = false; // *** Clear loading flag on UI thread ***
                    ApplyFilters(); // *** Re-apply filters now that data is loaded ***
                    // Show warnings (if any accumulated) on UI thread
                    if (warnings.Any())
                    {
                        var warningMessage = new StringBuilder("Warnings encountered during Mod Settings population:\n\n");
                        foreach (var warning in warnings) { warningMessage.AppendLine($"- {warning}"); }
                        MessageBox.Show(warningMessage.ToString(), "Mod Settings Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                });

            }, TaskScheduler.Default); // Specify scheduler for the continuation itself if needed, default is fine here

            // Populate _allModSettingsInternal immediately (NPC lists will populate in the background)
            // Final Step 1: Populate and Sort the source list
            _allModSettingsInternal = tempList.OrderBy(vm => vm.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            // Final Step 2: Check Mugshot Validity for UI (Can be done before NPC lists finish)
            foreach (var vm in _allModSettingsInternal)
            {
                vm.HasValidMugshots = CheckMugshotValidity(vm.MugShotFolderPath);
            }

            // Apply initial filters immediately, they will be re-applied when NPC data finishes loading
            ApplyFilters();
        }

        // Filtering Logic (Left Panel)
         public void ApplyFilters()
         {
             IEnumerable<VM_ModSetting> filtered = _allModSettingsInternal;

             if (!string.IsNullOrWhiteSpace(NameFilterText))
             {
                 filtered = filtered.Where(vm => vm.DisplayName.Contains(NameFilterText, StringComparison.OrdinalIgnoreCase));
             }

             if (!string.IsNullOrWhiteSpace(PluginFilterText))
             {
                 filtered = filtered.Where(vm => vm.CorrespondingModKeys.Any(key =>
                     key.FileName.String.Contains(PluginFilterText, StringComparison.OrdinalIgnoreCase) ||
                     key.ToString().Contains(PluginFilterText, StringComparison.OrdinalIgnoreCase)
                 ));
             }

             // *** Apply NPC Filter ***
             if (!IsLoadingNpcData && !string.IsNullOrWhiteSpace(NpcSearchText))
             {
                 string searchTextLower = NpcSearchText.Trim().ToLowerInvariant(); // Use invariant culture lowercase

                 switch (SelectedNpcSearchType)
                 {
                     case ModNpcSearchType.Name:
                         filtered = filtered.Where(vm => vm.NpcNames.Any(name => name.Contains(searchTextLower, StringComparison.OrdinalIgnoreCase)) ||
                                                         vm.NpcFormKeysToDisplayName.Values.Any(dName => dName.Contains(searchTextLower, StringComparison.OrdinalIgnoreCase))); // Also check dictionary values
                         break;
                     case ModNpcSearchType.EditorID:
                         filtered = filtered.Where(vm => vm.NpcEditorIDs.Any(eid => eid.Contains(searchTextLower, StringComparison.OrdinalIgnoreCase)));
                         break;
                     case ModNpcSearchType.FormKey:
                         // Compare string representations of FormKeys
                         filtered = filtered.Where(vm => vm.NpcFormKeys.Any(fk => fk.ToString().Contains(searchTextLower, StringComparison.OrdinalIgnoreCase)));
                         break;
                 }
             }
             // *** End NPC Filter ***

             var previouslySelectedMod = SelectedModForMugshots; // Preserve selection if possible

             ModSettingsList.Clear();
             var filteredList = filtered.ToList(); // Materialize the list
             foreach (var vm in filteredList)
             {
                 ModSettingsList.Add(vm);
             }

             // Check if the previously selected item for mugshots is still in the filtered list
             if (previouslySelectedMod != null && !filteredList.Contains(previouslySelectedMod))
             {
                 // It was filtered out, clear the right panel
                 SelectedModForMugshots = null;
                 CurrentModNpcMugshots.Clear();
             }


             System.Diagnostics.Debug.WriteLine($"ApplyFilters: Displaying {ModSettingsList.Count} of {_allModSettingsInternal.Count} items.");
         }

         public bool TryGetWinningNpc(FormKey fk, out INpcGetter? npcGetter)
         {
             var matchingNpc = _environmentStateProvider.LinkCache.TryResolve<INpcGetter>(fk, out npcGetter);
             return matchingNpc;
         }


         // Save Logic
         public void SaveModSettingsToModel()
         {
             _settings.ModSettings.Clear();
             foreach (var vm in _allModSettingsInternal) // Save from the full list
             {
                 // Only save if it has meaningful data (Key, Folder Paths, or Mugshot Path)
                 if (!string.IsNullOrWhiteSpace(vm.DisplayName) &&
                     (vm.CorrespondingModKeys.Any() || vm.CorrespondingFolderPaths.Any())) // Check if any keys exist
                 {
                     // Create a new ModSetting model instance
                     var model = new Models.ModSetting
                     {
                         DisplayName = vm.DisplayName,
                         ModKeys = vm.CorrespondingModKeys.ToList(),
                         // Important: Create new lists/collections when saving to the model
                         // to avoid potential issues with shared references if the VM is reused.
                         CorrespondingFolderPaths = vm.CorrespondingFolderPaths.ToList()
                         // Note: MugShotFolderPath is NOT saved in ModSetting model, it's derived/managed by VM
                     };
                     _settings.ModSettings.Add(model);
                 }
             }

             System.Diagnostics.Debug.WriteLine($"DEBUG: SaveModSettingsToModel preparing to save {_settings.ModSettings.Count} items.");

             // Saving the main settings file is handled by VM_Settings on App Exit
             // string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
             // JSONhandler<Settings>.SaveJSONFile(_settings, settingsPath, out bool success, out string exception);
             // if (!success) { MessageBox.Show($"Error saving settings: {exception}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error); }
             // else { System.Diagnostics.Debug.WriteLine("DEBUG: Settings successfully updated in memory by SaveModSettingsToModel."); }
         }
         
        /// <summary>
        /// Tries to find an existing VM_ModSetting matching the plugin key.
        /// It searches based on the CorrespondingModKey property of the VM_ModSettings.
        /// </summary>
        /// <param name="appearancePluginKey">The ModKey of the appearance plugin to search for.</param>
        /// <param name="foundVm">Output: The found VM_ModSetting instance if a match exists; otherwise, null.</param>
        /// <param name="modDisplayName">Output: The display name of the found VM_ModSetting if found; otherwise, defaults to the input plugin's filename.</param>
        /// <returns>True if a matching VM_ModSetting was found based on the CorrespondingModKey, false otherwise.</returns>
        public bool TryGetModSettingForPlugin(ModKey appearancePluginKey, out VM_ModSetting? foundVm, out string modDisplayName)
        {
            // Initialize output parameters
            foundVm = null;
            // Default the display name to the filename from the input key.
            // This will be used if the VM is not found or if the key is invalid.
            // Use ?? to handle potential null FileName (though unlikely for valid ModKey)
            modDisplayName = appearancePluginKey.FileName;

            // Check if the input ModKey is valid (not null or default)
            if (appearancePluginKey.IsNull)
            {
                Debug.WriteLine($"TryGetModSettingForPlugin: Received an invalid (IsNull) ModKey.");
                // Keep foundVm as null and modDisplayName as the default set above.
                return false; // Cannot find a match for an invalid key.
            }

            // Search the internal list of all loaded/created mod settings.
            // Find the first VM where *any* of its CorrespondingModKeys matches the input key.
            foundVm = _allModSettingsInternal.FirstOrDefault(vm => vm.CorrespondingModKeys.Any(key => key.Equals(appearancePluginKey)));
            // Check if a VM was found
            if (foundVm != null)
            {
                // A matching VM was found. Update the output display name to the actual display name of the found VM.
                modDisplayName = foundVm.DisplayName;
                // Log success for debugging if needed.
                // Debug.WriteLine($"TryGetModSettingForPlugin: Found existing VM '{modDisplayName}' for key '{appearancePluginKey}'.");
                return true; // Indicate success.
            }
            else
            {
                // No VM with a matching CorrespondingModKey was found in the list.
                // Log failure for debugging if needed.
                // Debug.WriteLine($"TryGetModSettingForPlugin: No VM found for key '{appearancePluginKey}'.");
                // Keep foundVm as null and modDisplayName as the default filename set earlier.
                return false; // Indicate failure.
            }
        }
        
        /// <summary>
        /// Called after a Mod Folder Path or Mugshot Folder Path is potentially changed on a VM_ModSetting.
        /// Checks if this change links it to another complementary VM (one with only mugshots, one with only mods)
        /// and performs an automatic merge if conditions are met.
        /// </summary>
        /// <param name="modifiedVm">The VM that the user directly modified.</param>
        /// <param name="addedOrSetPath">The specific path that was added or set.</param>
        /// <param name="pathType">Indicates whether a ModFolder or MugshotFolder was changed.</param>
        /// <param name="hadMugshotPathBefore">Did modifiedVm have a mugshot path BEFORE this change?</param>
        /// <param name="hadModPathsBefore">Did modifiedVm have mod paths BEFORE this change?</param>
        public void CheckForAndPerformMerge(VM_ModSetting modifiedVm, string addedOrSetPath, PathType pathType, bool hadMugshotPathBefore, bool hadModPathsBefore)
        {
            if (modifiedVm == null || string.IsNullOrEmpty(addedOrSetPath)) return;

            VM_ModSetting? sourceVm = null; // The potential VM to merge *from*

            // Find a potential source VM that contains the path added/set to the modified VM
            foreach (var vm in _allModSettingsInternal)
            {
                if (vm == modifiedVm) continue; // Don't compare to self

                bool pathMatches = false;
                if (pathType == PathType.ModFolder && vm.CorrespondingFolderPaths.Contains(addedOrSetPath, StringComparer.OrdinalIgnoreCase))
                {
                    pathMatches = true;
                }
                else if (pathType == PathType.MugshotFolder && addedOrSetPath.Equals(vm.MugShotFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    pathMatches = true;
                }

                if (pathMatches)
                {
                    sourceVm = vm;
                    break; // Found a potential source containing the path
                }
            }

            if (sourceVm == null) return; // No other VM contains this specific path

            // Now check if the merge conditions based on initial states are met
            bool mergeConditionsMet = false;
            VM_ModSetting winner = modifiedVm; // Assume the modified VM is the winner initially
            VM_ModSetting loser = sourceVm;

            if (pathType == PathType.ModFolder)
            {
                // User added a Mod Folder path to 'modifiedVm'
                // Conditions:
                // 1. 'modifiedVm' previously ONLY had mugshots (hadMugshotPathBefore=true, hadModPathsBefore=false)
                // 2. 'sourceVm' previously ONLY had this specific mod path and NO mugshots
                bool sourceHadOnlyThisModPath = sourceVm.CorrespondingFolderPaths.Count == 1 &&
                                               sourceVm.CorrespondingFolderPaths.Contains(addedOrSetPath, StringComparer.OrdinalIgnoreCase);
                bool sourceHadNoMugshots = string.IsNullOrWhiteSpace(sourceVm.MugShotFolderPath); // Check current state is okay here

                if (hadMugshotPathBefore && !hadModPathsBefore && sourceHadOnlyThisModPath && sourceHadNoMugshots)
                {
                    mergeConditionsMet = true;
                    // Winner = modifiedVm, Loser = sourceVm (Correctly initialized)
                }
            }
            else // pathType == PathType.MugshotFolder
            {
                // User set the Mugshot Folder path on 'modifiedVm'
                // Conditions:
                // 1. 'modifiedVm' previously ONLY had mod paths (hadMugshotPathBefore=false, hadModPathsBefore=true)
                // 2. 'sourceVm' previously ONLY had this specific mugshot path and NO mod paths
                bool sourceHadOnlyThisMugshot = addedOrSetPath.Equals(sourceVm.MugShotFolderPath, StringComparison.OrdinalIgnoreCase) &&
                                               !sourceVm.HasModPathsAssigned; // Check current state is okay
                bool sourceHadNoModPaths = !sourceVm.HasModPathsAssigned; // Redundant check, but clear

                if (!hadMugshotPathBefore && hadModPathsBefore && sourceHadOnlyThisMugshot)
                {
                    mergeConditionsMet = true;
                    // Winner = modifiedVm, Loser = sourceVm (Correctly initialized)
                }
            }


            // Perform the merge if conditions are met
            if (mergeConditionsMet)
            {
                Debug.WriteLine($"Merge Condition Met: Merging '{loser.DisplayName}' into '{winner.DisplayName}'");

                // --- Perform Merge Actions ---
                // 1. Transfer necessary data (loser -> winner)
                // Mugshot Path (only if winner doesn't have one)
                if (!winner.HasMugshotPathAssigned && loser.HasMugshotPathAssigned)
                {
                    winner.MugShotFolderPath = loser.MugShotFolderPath;
                }
                // Mod Folder Paths (add paths from loser not already in winner)
                foreach (var path in loser.CorrespondingFolderPaths)
                {
                    if (!winner.CorrespondingFolderPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        winner.CorrespondingFolderPaths.Add(path);
                    }
                }
                // Merge Corresponding Mod Keys (add keys from loser not already in winner)
                foreach (var key in loser.CorrespondingModKeys)
                {
                    if (!winner.CorrespondingModKeys.Contains(key))
                    {
                        winner.CorrespondingModKeys.Add(key);
                    }
                }
                // IsMugshotOnlyEntry should remain based on the WINNER's original status
                 // Although, if a merge happens, it's unlikely the winner was mugshot-only. Let's set it to false.
                 winner.IsMugshotOnlyEntry = false;


                // 2. Update NPC Selections (_model.SelectedAppearanceMods via _consistencyProvider)
                 string loserName = loser.DisplayName;
                 string winnerName = winner.DisplayName;
                 var npcsToUpdate = _settings.SelectedAppearanceMods
                     .Where(kvp => kvp.Value.Equals(loserName, StringComparison.OrdinalIgnoreCase))
                     .Select(kvp => kvp.Key) // Get FormKeys of NPCs assigned to the loser
                     .ToList(); // Materialize the list before modifying the dictionary

                 if (npcsToUpdate.Any())
                 {
                      Debug.WriteLine($"Updating {npcsToUpdate.Count} NPC selections from '{loserName}' to '{winnerName}'.");
                      foreach (var npcKey in npcsToUpdate)
                      {
                           // Use consistency provider to update both cache and _settings model
                           _consistencyProvider.SetSelectedMod(npcKey, winnerName);
                      }
                 }


                // 3. Remove Loser VM
                bool removed = _allModSettingsInternal.Remove(loser);
                Debug.WriteLine($"Removed loser VM '{loserName}': {removed}");


                // 4. Refresh UI
                ApplyFilters();


                // 5. Inform User
                 MessageBox.Show(
                     $"Automatically merged mod settings:\n\n'{loserName}' was merged into '{winnerName}'.\n\nNPC assignments using '{loserName}' have been updated.",
                     "Mod Settings Merged",
                     MessageBoxButton.OK,
                     MessageBoxImage.Information);
            }
        }
    }
}