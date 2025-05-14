// View Models/VM_Mods.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
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
using NPC_Plugin_Chooser_2.Views;
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
        private readonly CompositeDisposable _disposables = new();

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
        // This property will be set to true by the View (ModsView.xaml.cs) when the user
        // directly interacts with zoom (Ctrl+Scroll, +/- buttons).
        [Reactive] public bool ModsViewHasUserManuallyZoomed { get; set; } = false;

        // Subject for triggering right panel image refresh
        private readonly Subject<Unit> _refreshMugshotSizesSubject = new Subject<Unit>();
        public IObservable<Unit> RefreshMugshotSizesObservable => _refreshMugshotSizesSubject.AsObservable();
        
        // --- NEW: Zoom Control Properties & Commands for ModsView ---
        [Reactive] public double ModsViewZoomLevel { get; set; }
        [Reactive] public bool ModsViewIsZoomLocked { get; set; }
        public ReactiveCommand<Unit, Unit> ZoomInModsCommand { get; }
        public ReactiveCommand<Unit, Unit> ZoomOutModsCommand { get; }

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

            // ... (previous constructor logic like ShowMugshotsCommand, Filter setup)
            ShowMugshotsCommand = ReactiveCommand.CreateFromTask<VM_ModSetting>(ShowMugshotsAsync);
            ShowMugshotsCommand.ThrownExceptions.Subscribe(ex =>
            {
                ScrollableMessageBox.ShowError($"Error loading mugshots: {ex.Message}");
                IsLoadingMugshots = false;
            }).DisposeWith(_disposables);


            // --- NEW: Initialize Zoom Settings from _settings ---
            ModsViewZoomLevel = _settings.ModsViewZoomLevel;
            ModsViewIsZoomLocked = _settings.ModsViewIsZoomLocked;

            // --- NEW: Zoom Commands ---
            ZoomInModsCommand = ReactiveCommand.Create(() =>
            {
                ModsViewZoomLevel = Math.Min(500, ModsViewZoomLevel + 2.5); // Max 500%
                ModsViewHasUserManuallyZoomed = true; // User interaction
            });
            ZoomOutModsCommand = ReactiveCommand.Create(() =>
            {
                ModsViewZoomLevel = Math.Max(10, ModsViewZoomLevel - 2.5); // Min 10%
                ModsViewHasUserManuallyZoomed = true; // User interaction
            });
            ZoomInModsCommand.ThrownExceptions.Subscribe(ex => Debug.WriteLine($"Error ZoomInModsCommand: {ex.Message}")).DisposeWith(_disposables);
            ZoomOutModsCommand.ThrownExceptions.Subscribe(ex => Debug.WriteLine($"Error ZoomOutModsCommand: {ex.Message}")).DisposeWith(_disposables);


            PopulateModSettings(); // Populates and sorts _allModSettingsInternal

            // --- Setup Filter Reaction ---
            this.WhenAnyValue(x => x.NameFilterText, x => x.PluginFilterText, x => x.NpcSearchText, x => x.SelectedNpcSearchType) // Added NpcSearchType
                .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ApplyFilters())
                .DisposeWith(_disposables);
            
            this.WhenAnyValue(x => x.NameFilterText, x => x.PluginFilterText, x => x.NpcSearchText, x => x.SelectedNpcSearchType)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => {
                    if (SelectedModForMugshots != null && !ModSettingsList.Contains(SelectedModForMugshots))
                    {
                        SelectedModForMugshots = null;
                        CurrentModNpcMugshots.Clear();
                    }
                })
                .DisposeWith(_disposables);

            // --- NEW: Persist Zoom Settings and Trigger Refresh ---
            this.WhenAnyValue(x => x.ModsViewZoomLevel)
                .Skip(1) // Skip initial value from settings
                .Throttle(TimeSpan.FromMilliseconds(50))
                .Subscribe(zoom =>
                {
                    var clampedZoom = Math.Max(10, Math.Min(500, zoom));
                    if (clampedZoom != zoom)
                    {
                        ModsViewZoomLevel = clampedZoom; // Will re-trigger if changed
                        return;
                    }
                    _settings.ModsViewZoomLevel = clampedZoom;
                    if (ModsViewIsZoomLocked || ModsViewHasUserManuallyZoomed)
                    {
                        _refreshMugshotSizesSubject.OnNext(Unit.Default);
                    }
                })
                .DisposeWith(_disposables);

            this.WhenAnyValue(x => x.ModsViewIsZoomLocked)
                .Skip(1) // Skip initial value
                .Subscribe(isLocked =>
                {
                    _settings.ModsViewIsZoomLocked = isLocked;
                    ModsViewHasUserManuallyZoomed = false; // Reset manual flag
                    _refreshMugshotSizesSubject.OnNext(Unit.Default); // Always refresh packer
                })
                .DisposeWith(_disposables);

            // MODIFIED: When SelectedModForMugshots changes, reset manual zoom state if not locked.
            this.WhenAnyValue(x => x.SelectedModForMugshots)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(selectedMod => {
                    if (!ModsViewIsZoomLocked)
                    {
                        ModsViewHasUserManuallyZoomed = false;
                    }
                    // The ShowMugshotsAsync method, called when SelectedModForMugshots changes (usually via command),
                    // is already responsible for triggering _refreshMugshotSizesSubject.
                })
                .DisposeWith(_disposables);

            ApplyFilters(); // Apply initial filter
        }
        
        /// <summary>
        /// Adds a new VM_ModSetting (typically created by Unlink operation) to the internal list
        /// and refreshes dependent UI.
        /// </summary>
        public void AddAndRefreshModSetting(VM_ModSetting newVm)
        {
            if (newVm == null || _allModSettingsInternal.Any(vm => vm.DisplayName.Equals(newVm.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                Debug.WriteLine($"VM_Mods: Not adding VM '{newVm?.DisplayName}' either because it's null or a VM with that DisplayName already exists.");
                // Optionally, if it exists, consider merging properties, but for unlink, it should be a new entry.
                return;
            }

            _allModSettingsInternal.Add(newVm);
            // Re-sort the internal list by DisplayName
            _allModSettingsInternal = _allModSettingsInternal.OrderBy(vm => vm.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            
            // Recalculate its mugshot validity (it might be a new mugshot-only entry)
            RecalculateMugshotValidity(newVm);

            // Refresh the filtered list in the UI
            ApplyFilters();

            // Asynchronously refresh its NPC lists if it might have mod data (though unlink usually makes it mugshot-only)
            // For a new mugshot-only entry, RefreshNpcLists won't find much, but it's harmless.
            Task.Run(() => newVm.RefreshNpcLists()); 
        }

        /// <summary>
        /// Requests the VM_NpcSelectionBar to refresh its current NPC's appearance sources.
        /// </summary>
        public void RequestNpcSelectionBarRefresh()
        {
            // This relies on VM_NpcSelectionBar being accessible, e.g., if injected or via a message bus.
            // Assuming _npcSelectionBar is the injected instance.
            _npcSelectionBar?.RefreshAppearanceSources();
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

            // MODIFIED: Reset manual zoom flag if zoom is not locked
            if (!ModsViewIsZoomLocked)
            {
                ModsViewHasUserManuallyZoomed = false; 
            }

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
                            string formIdHex = hexPart.Substring(Math.Max(0, hexPart.Length - 6)); 
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
                            }
                        }
                    }
                });

                CurrentModNpcMugshots.Clear(); 
                foreach (var vm in mugshotVMs.OrderBy(vm => vm.NpcDisplayName)) 
                {
                    CurrentModNpcMugshots.Add(vm);
                }
            }
            catch (Exception ex)
            {
                 ScrollableMessageBox.ShowWarning($"Failed to load mugshots from {selectedModSetting.MugShotFolderPath}:\n{ex.Message}", "Mugshot Load Error");
                 CurrentModNpcMugshots.Clear(); 
            }
            finally
            {
                IsLoadingMugshots = false;
                 if (CurrentModNpcMugshots.Any())
                 {
                      _refreshMugshotSizesSubject.OnNext(Unit.Default); // This will trigger packing in the view
                 }
            }
        }

        // --- NEW or MODIFIED IF NEEDED: Dispose method for cleaning up subscriptions ---
        public void Dispose() // If VM_Mods needs to be disposable
        {
            _disposables.Dispose();
            // Any other cleanup
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
                     ScrollableMessageBox.ShowWarning($"Could not find NPC with FormKey {npcFormKey} in the main NPC list.", "NPC Not Found");
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
            var tempList = new List<VM_ModSetting>();
            IsLoadingNpcData = true;

            var claimedMugshotPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Tracks mugshot paths already assigned

            // --- Ensure Environment is Ready ---
            if (!_environmentStateProvider.EnvironmentIsValid || _environmentStateProvider.LoadOrder == null)
            {
                warnings.Add("Environment is not valid. Cannot accurately link plugins.");
            }
            var enabledKeys = _environmentStateProvider.EnvironmentIsValid
                            ? _environmentStateProvider.LoadOrder.Keys.ToHashSet()
                            : new HashSet<ModKey>();


            // Phase 1: Load existing data from _settings.ModSettings
            foreach (var settingModel in _settings.ModSettings)
            {
                if (string.IsNullOrWhiteSpace(settingModel.DisplayName)) continue;

                var vm = new VM_ModSetting(settingModel, this); // Constructor now loads MugShotFolderPath from model

                // If MugShotFolderPath was loaded from settings and is valid, claim it
                if (!string.IsNullOrWhiteSpace(vm.MugShotFolderPath) && Directory.Exists(vm.MugShotFolderPath))
                {
                    claimedMugshotPaths.Add(vm.MugShotFolderPath);
                }
                // Else if MugShotFolderPath was NOT in settings (empty/null), try to derive it from DisplayName
                else if (string.IsNullOrWhiteSpace(vm.MugShotFolderPath))
                {
                    if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder))
                    {
                        string potentialPathByName = Path.Combine(_settings.MugshotsFolder, vm.DisplayName);
                        if (Directory.Exists(potentialPathByName) && !claimedMugshotPaths.Contains(potentialPathByName))
                        {
                            vm.MugShotFolderPath = potentialPathByName;
                            claimedMugshotPaths.Add(potentialPathByName);
                        }
                    }
                }
                // If after these steps, vm.MugShotFolderPath is still empty, it means it either had no persisted path,
                // and no mugshot folder matching its DisplayName was found (or was already claimed).

                tempList.Add(vm);
                loadedDisplayNames.Add(vm.DisplayName);
            }

            // Phase 2a: Discover unassigned Mugshot folders and create VMs for them if necessary
            var vmsFromMugshotsOnly = new List<VM_ModSetting>();
            if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder))
            {
                try
                {
                    var mugShotDirs = Directory.EnumerateDirectories(_settings.MugshotsFolder);
                    foreach (var dirPath in mugShotDirs)
                    {
                        if (!claimedMugshotPaths.Contains(dirPath)) // Only process if not already claimed by a setting from JSON
                        {
                            string folderName = Path.GetFileName(dirPath);
                            // Check if a VM with this DisplayName already exists from settings (Phase 1)
                            // If it does, that VM didn't claim this path (e.g., its MugShotFolderPath was different or empty)
                            // So, this mugshot folder is truly unassigned or belongs to a new entity.
                            if (!loadedDisplayNames.Contains(folderName))
                            {
                                var vm = new VM_ModSetting(folderName, dirPath, this); // Mugshot-only constructor
                                vmsFromMugshotsOnly.Add(vm);
                                // No need to add to claimedMugshotPaths here, as it's for a new temp VM
                            }
                            // If loadedDisplayNames.Contains(folderName), it means a VM_ModSetting with this name exists
                            // but its MugShotFolderPath is either empty or points elsewhere.
                            // In this case, this dirPath is an "orphaned" mugshot folder that doesn't match by name
                            // to an existing VM that *could* take it. We let it be for now; it won't get a VM.
                            // It could be assigned later via drag-drop.
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

                        // Scan for enabled plugins within this folder (same as before)
                        try
                        {
                            foreach (var filePath in Directory.EnumerateFiles(modFolderPath, "*.es*", SearchOption.TopDirectoryOnly))
                            {
                                string fileNameWithExt = Path.GetFileName(filePath);
                                if (fileNameWithExt.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                                    fileNameWithExt.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                                    fileNameWithExt.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        ModKey parsedKey = ModKey.FromFileName(fileNameWithExt);
                                        if (enabledKeys.Contains(parsedKey))
                                        {
                                            foundEnabledKeysInFolder.Add(parsedKey);
                                        }
                                    }
                                    catch (Exception parseEx) { warnings.Add($"Could not parse plugin filename '{fileNameWithExt}' in folder '{modFolderName}': {parseEx.Message}"); }
                                }
                            }
                        }
                        catch (Exception fileScanEx) { warnings.Add($"Error scanning files in Mod folder '{modFolderName}': {fileScanEx.Message}"); }

                        // Try to link to existing VM (from settings) or upgrade a mugshot-only VM, or create new
                        var existingVmFromSettings = tempList.FirstOrDefault(vm => vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));
                        var mugshotOnlyVmToUpgrade = vmsFromMugshotsOnly.FirstOrDefault(vm => vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));

                        if (existingVmFromSettings != null)
                        {
                            if (!existingVmFromSettings.CorrespondingFolderPaths.Contains(modFolderPath, StringComparer.OrdinalIgnoreCase))
                                existingVmFromSettings.CorrespondingFolderPaths.Add(modFolderPath);
                            foreach (var key in foundEnabledKeysInFolder)
                            {
                                if (!existingVmFromSettings.CorrespondingModKeys.Contains(key)) existingVmFromSettings.CorrespondingModKeys.Add(key);
                            }
                        }
                        else if (mugshotOnlyVmToUpgrade != null)
                        {
                            mugshotOnlyVmToUpgrade.CorrespondingFolderPaths.Add(modFolderPath);
                            foreach (var key in foundEnabledKeysInFolder)
                            {
                                if (!mugshotOnlyVmToUpgrade.CorrespondingModKeys.Contains(key)) mugshotOnlyVmToUpgrade.CorrespondingModKeys.Add(key);
                            }
                            tempList.Add(mugshotOnlyVmToUpgrade); // Move to main list
                            vmsFromMugshotsOnly.Remove(mugshotOnlyVmToUpgrade);
                            loadedDisplayNames.Add(mugshotOnlyVmToUpgrade.DisplayName); // Mark as fully processed
                        }
                        else if (foundEnabledKeysInFolder.Any()) // Create new VM if plugins were found and no existing match
                        {
                            var newVm = new VM_ModSetting(modFolderName, this)
                            {
                                CorrespondingFolderPaths = new ObservableCollection<string> { modFolderPath },
                                CorrespondingModKeys = new ObservableCollection<ModKey>(foundEnabledKeysInFolder)
                            };
                            // Attempt to link mugshot path by name if available and unclaimed
                            string potentialMugshotPathForNewVm = Path.Combine(_settings.MugshotsFolder, newVm.DisplayName);
                            if (Directory.Exists(potentialMugshotPathForNewVm) && !claimedMugshotPaths.Contains(potentialMugshotPathForNewVm))
                            {
                                newVm.MugShotFolderPath = potentialMugshotPathForNewVm;
                                claimedMugshotPaths.Add(potentialMugshotPathForNewVm);
                            }
                            tempList.Add(newVm);
                            loadedDisplayNames.Add(newVm.DisplayName);
                        }
                    }
                }
                catch (Exception ex) { warnings.Add($"Error scanning Mods folder '{_settings.ModsFolder}' for linking: {ex.Message}"); }
            }

            // Phase 2c: Add remaining mugshot-only VMs that couldn't be linked to a mod folder
            foreach (var mugshotVm in vmsFromMugshotsOnly)
            {
                // These are truly mugshot-only: no settings entry, no matching mod folder by name.
                if (!tempList.Any(existing => existing.DisplayName.Equals(mugshotVm.DisplayName, StringComparison.OrdinalIgnoreCase)))
                {
                    tempList.Add(mugshotVm); // MugShotFolderPath is already set from its constructor
                }
            }

            // Set IsMugshotOnlyEntry flag correctly and Refresh NPC Lists
            foreach (var vm in tempList)
            {
                vm.IsMugshotOnlyEntry = string.IsNullOrWhiteSpace(vm.MugShotFolderPath) && !vm.CorrespondingFolderPaths.Any() && !vm.CorrespondingModKeys.Any()
                                     || (!vm.CorrespondingFolderPaths.Any() && !vm.CorrespondingModKeys.Any() && !string.IsNullOrWhiteSpace(vm.MugShotFolderPath)); // Simplified: it's mugshot-only if it ONLY has a mugshot path OR is completely empty. More accurately: has mugshots but no mod data.
                // A better definition for IsMugshotOnlyEntry: Does it have a mugshot path, but no mod keys/folders?
                // This flag is mostly for UI warnings if it won't be saved.
                // If it has a MugShotFolderPath set (either from JSON or derived), and no ModKeys/FolderPaths, it might be considered mugshot-only.
                // For saving: an entry is saved if it has a DisplayName AND (ModKeys OR FolderPaths OR a non-empty MugShotFolderPath).
                // Let's refine `IsMugshotOnlyEntry` based on whether it would be saved.
                // It's primarily for entries auto-created from mugshot folders that don't get linked to mod data.
                 bool willBeSaved = !string.IsNullOrWhiteSpace(vm.DisplayName) &&
                                   (vm.CorrespondingModKeys.Any() || 
                                    vm.CorrespondingFolderPaths.Any() ||
                                    !string.IsNullOrWhiteSpace(vm.MugShotFolderPath)); // If it has a mugshot path, it should be saved.

                 // IsMugshotOnlyEntry is true if it was *created* as such and hasn't gained mod data.
                 // The original `vm.IsMugshotOnlyEntry` (from constructor) is more indicative of its origin.
                 // Let's ensure it's false if it gains mod data.
                 if (vm.IsMugshotOnlyEntry && (vm.CorrespondingFolderPaths.Any() || vm.CorrespondingModKeys.Any()))
                 {
                     vm.IsMugshotOnlyEntry = false;
                 }
            }

            // Asynchronously refresh NPC lists (same as before)
            var refreshTasks = tempList.Select(vm => Task.Run(() => vm.RefreshNpcLists())).ToList();
            Task.WhenAll(refreshTasks).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var flattenedExceptions = t.Exception?.Flatten().InnerExceptions;
                    if (flattenedExceptions != null)
                    {
                        foreach (var ex in flattenedExceptions) { Debug.WriteLine($"Error during async NPC list refresh: {ex.Message}"); }
                    }
                }
                RxApp.MainThreadScheduler.Schedule(() =>
                {
                    IsLoadingNpcData = false;
                    ApplyFilters();
                    if (warnings.Any())
                    {
                        var warningMessage = new StringBuilder("Warnings encountered during Mod Settings population:\n\n");
                        foreach (var warning in warnings) { warningMessage.AppendLine($"- {warning}"); }
                        ScrollableMessageBox.ShowWarning(warningMessage.ToString(), "Mod Settings Warning");
                    }
                });
            }, TaskScheduler.Default);

            _allModSettingsInternal = tempList.OrderBy(vm => vm.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var vm in _allModSettingsInternal)
            {
                vm.HasValidMugshots = CheckMugshotValidity(vm.MugShotFolderPath);
            }

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
                         CorrespondingFolderPaths = vm.CorrespondingFolderPaths.ToList(),
                         MugShotFolderPath = vm.MugShotFolderPath, // Save the mugshot folder path
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
         /// Removes the specified VM_ModSetting from the internal list and refreshes the filtered view.
         /// </summary>
         /// <param name="modSettingToRemove">The VM_ModSetting to remove.</param>
         /// <returns>True if the item was successfully found and removed; otherwise, false.</returns>
         public bool RemoveModSetting(VM_ModSetting modSettingToRemove)
         {
             if (modSettingToRemove == null) return false;

             bool removed = _allModSettingsInternal.Remove(modSettingToRemove);
             if (removed)
             {
                 Debug.WriteLine($"VM_Mods: Removed ModSetting '{modSettingToRemove.DisplayName}' from internal list.");
                 // If the removed mod was selected for mugshots, clear the selection
                 if (SelectedModForMugshots == modSettingToRemove)
                 {
                     SelectedModForMugshots = null;
                     CurrentModNpcMugshots.Clear();
                 }
                 ApplyFilters(); // Refresh the ModSettingsList (left panel)
             }
             else
             {
                 Debug.WriteLine($"VM_Mods: ModSetting '{modSettingToRemove.DisplayName}' not found in internal list for removal.");
             }
             return removed;
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
                ScrollableMessageBox.Show(
                    $"Automatically merged mod settings:\n\n'{loserName}' was merged into '{winnerName}'.\n\nNPC assignments using '{loserName}' have been updated.",
                    "Mod Settings Merged"
                    );
            }
        }
    }
}