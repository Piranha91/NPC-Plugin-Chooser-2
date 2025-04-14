// View Models/VM_Mods.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq; // Added for Throttle, ObserveOn
using System.Text;
using System.Windows; // For MessageBox
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_Mods : ReactiveObject
    {
        private readonly Settings _settings;
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly VM_NpcSelectionBar _npcSelectionBar; // To access AllNpcs

        // --- Filtering Properties ---
        [Reactive] public string NameFilterText { get; set; } = string.Empty;
        [Reactive] public string PluginFilterText { get; set; } = string.Empty;

        // --- Data Lists ---
        // Private list holding ALL mod settings after population and sorting
        private List<VM_ModSetting> _allModSettings = new();
        // Public list bound to the UI, displaying the filtered results
        // *** REMOVED [Reactive] attribute ***
        public ObservableCollection<VM_ModSetting> ModSettingsList { get; } = new();

        // Expose for binding in VM_ModSetting commands
        public string ModsFolderSetting => _settings.ModsFolder;

        public VM_Mods(Settings settings, EnvironmentStateProvider environmentStateProvider, VM_NpcSelectionBar npcSelectionBar)
        {
            _settings = settings;
            _environmentStateProvider = environmentStateProvider;
            _npcSelectionBar = npcSelectionBar;

            if (!_npcSelectionBar.AllNpcs.Any())
            {
                 System.Diagnostics.Debug.WriteLine("Warning: VM_Mods initialized before VM_NpcSelectionBar potentially finished. NPC data might be incomplete for linking.");
            }

            PopulateModSettings(); // Populates and sorts _allModSettings

            // --- Setup Filter Reaction ---
            this.WhenAnyValue(x => x.NameFilterText, x => x.PluginFilterText)
                .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ApplyFilters());

            ApplyFilters(); // Apply initial filter

            // Saving logic (e.g., on exit) should call SaveModSettingsToModel()
             // Application.Current.Exit += (s, e) => SaveModSettingsToModel(); // Example hook
        }

        public void PopulateModSettings()
        {
            _allModSettings.Clear();
            var warnings = new List<string>();
            var loadedDisplayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var linkedModKeys = new HashSet<ModKey>();
            var tempList = new List<VM_ModSetting>();

            // Phase 1: Load existing data from Settings
            foreach (var setting in _settings.ModSettings)
            {
                 if (string.IsNullOrWhiteSpace(setting.DisplayName)) continue;
                 var vm = new VM_ModSetting(setting, this);
                 if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder))
                 {
                     string potentialMugshotPath = Path.Combine(_settings.MugshotsFolder, vm.DisplayName);
                     if (Directory.Exists(potentialMugshotPath)) { vm.MugShotFolderPath = potentialMugshotPath; }
                 }
                 tempList.Add(vm);
                 loadedDisplayNames.Add(vm.DisplayName);
                 if (vm.CorrespondingModKey.HasValue) { linkedModKeys.Add(vm.CorrespondingModKey.Value); }
            }

            // Phase 2a:  Create new VM_ModSettings from Mugshot folders
             var vmsFromMugshots = new List<VM_ModSetting>();
             if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder))
             {
                 try
                 {
                     foreach (var dirPath in Directory.EnumerateDirectories(_settings.MugshotsFolder))
                     {
                         string folderName = Path.GetFileName(dirPath);
                         if (!loadedDisplayNames.Contains(folderName))
                         {
                             var vm = new VM_ModSetting(folderName, this) { MugShotFolderPath = dirPath };
                             vmsFromMugshots.Add(vm);
                         }
                     }
                 }
                 catch (Exception ex) { warnings.Add($"Error scanning Mugshots folder '{_settings.MugshotsFolder}': {ex.Message}"); }
             }

            // Phase 2b: Try to link CorrespondingFolderPaths to the new VM_ModSettings created in Phase 2a
             var vmsLinkedToModFolder = new List<VM_ModSetting>();
             if (!string.IsNullOrWhiteSpace(_settings.ModsFolder) && Directory.Exists(_settings.ModsFolder))
             {
                 try
                 {
                     foreach (var modFolderPath in Directory.EnumerateDirectories(_settings.ModsFolder))
                     {
                         string modFolderName = Path.GetFileName(modFolderPath);
                         var matchingVm = vmsFromMugshots.FirstOrDefault(vm => vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));
                         if (matchingVm != null && !matchingVm.CorrespondingFolderPaths.Any())
                         {
                             matchingVm.CorrespondingFolderPaths.Add(modFolderPath);
                             vmsLinkedToModFolder.Add(matchingVm);
                         }
                     }
                 }
                 catch (Exception ex) { warnings.Add($"Error scanning Mods folder '{_settings.ModsFolder}' for linking: {ex.Message}"); }
             }

            // Phase 2c: Try to link CorrespondingModKeys to the new VM_ModSettings altered in Phase 2b
             var appearanceModKeys = new HashSet<ModKey>();
             if (_npcSelectionBar.AllNpcs != null)
             {
                 try
                 {
                      appearanceModKeys = _npcSelectionBar.AllNpcs
                                                     .Where(npc => npc != null && npc.AppearanceMods != null)
                                                     .SelectMany(npc => npc.AppearanceMods)
                                                     .Distinct()
                                                     .ToHashSet();
                 }
                 catch (Exception ex) { warnings.Add($"Error compiling AppearanceModKeys: {ex.Message}"); }
             }
             else { warnings.Add("Could not compile AppearanceModKeys: NPC list not available."); }

             foreach (var vm in vmsLinkedToModFolder)
             {
                 if (vm.CorrespondingModKey.HasValue) continue;
                 string folderPath = vm.CorrespondingFolderPaths.First();
                 if (Directory.Exists(folderPath))
                 {
                     try
                     {
                         foreach (var filePath in Directory.EnumerateFiles(folderPath))
                         {
                             string fileName = Path.GetFileNameWithoutExtension(filePath);
                             string fileNameWithExt = Path.GetFileName(filePath);
                             foreach (var modKey in appearanceModKeys)
                             {
                                 if (fileName.Equals(modKey.ToString(), StringComparison.OrdinalIgnoreCase) ||
                                     fileNameWithExt.Equals(modKey.FileName, StringComparison.OrdinalIgnoreCase))
                                 {
                                     vm.CorrespondingModKey = modKey;
                                     linkedModKeys.Add(modKey);
                                     goto NextVmInPhase2c;
                                 }
                             }
                         }
                     }
                     catch (Exception ex) { warnings.Add($"Error scanning files in '{folderPath}' for ModKey linking: {ex.Message}"); }
                 }
             NextVmInPhase2c:;
             }

            // Add Phase 2 VMs to temp list: 
            foreach(var vm in vmsFromMugshots)
            {
                if (vm.CorrespondingFolderPaths.Any() || vm.CorrespondingModKey.HasValue)
                {
                    if (!tempList.Any(existing => existing.DisplayName.Equals(vm.DisplayName, StringComparison.OrdinalIgnoreCase)))
                    {
                         tempList.Add(vm);
                    }
                }
            }

            // Phase 3: Create new VM_ModSettings from "orphaned" ModKeys that lack corresponding MugShots.
             var orphanedModKeys = appearanceModKeys.Except(linkedModKeys).ToList();
             var modKeyToFileLocations = new Dictionary<ModKey, List<string>>();
             if (orphanedModKeys.Any() && !string.IsNullOrWhiteSpace(_settings.ModsFolder) && Directory.Exists(_settings.ModsFolder))
             {
                 try
                 {
                     foreach (var modFolderPath in Directory.EnumerateDirectories(_settings.ModsFolder))
                     {
                          if (!Directory.Exists(modFolderPath)) continue;
                          try
                          {
                              foreach (var filePath in Directory.EnumerateFiles(modFolderPath))
                              {
                                  string fileName = Path.GetFileNameWithoutExtension(filePath);
                                  string fileNameWithExt = Path.GetFileName(filePath);
                                  foreach (var orphanedKey in orphanedModKeys)
                                  {
                                      if (fileName.Equals(orphanedKey.ToString(), StringComparison.OrdinalIgnoreCase) ||
                                          fileNameWithExt.Equals(orphanedKey.FileName, StringComparison.OrdinalIgnoreCase))
                                      {
                                          if (!modKeyToFileLocations.TryGetValue(orphanedKey, out var locations))
                                          {
                                              locations = new List<string>();
                                              modKeyToFileLocations[orphanedKey] = locations;
                                          }
                                          if (!locations.Contains(modFolderPath, StringComparer.OrdinalIgnoreCase)) { locations.Add(modFolderPath); }
                                      }
                                  }
                              }
                          }
                          catch (Exception ex) { warnings.Add($"Error scanning files in '{modFolderPath}' for orphaned ModKeys: {ex.Message}"); }
                     }
                 }
                 catch (Exception ex) { warnings.Add($"Error scanning Mods folder '{_settings.ModsFolder}' for orphaned ModKeys: {ex.Message}"); }
             }

            // Add Phase 3 VMs to temp list
            foreach (var kvp in modKeyToFileLocations)
            {
                var modKey = kvp.Key;
                var folderPaths = kvp.Value;
                if (tempList.Any(vm => vm.CorrespondingModKey == modKey)) continue;

                // create new display name
                var folderNames = folderPaths.Select(folderPath => Path.GetFileName(folderPath));
                var displayName = String.Join(" | ", folderNames);
                
                var vm = new VM_ModSetting(displayName, this)
                {
                    CorrespondingModKey = modKey,
                    CorrespondingFolderPaths = new ObservableCollection<string>(folderPaths)
                };
                 if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder))
                 {
                     if (folderPaths.Any())
                     {
                         string firstFolderName = Path.GetFileName(folderPaths.First());
                         string potentialMugshotPath = Path.Combine(_settings.MugshotsFolder, firstFolderName);
                         if (Directory.Exists(potentialMugshotPath))
                         {
                              vm.DisplayName = firstFolderName;
                              vm.MugShotFolderPath = potentialMugshotPath;
                         }
                     }
                 }
                tempList.Add(vm);
                if (folderPaths.Count > 1)
                {
                    warnings.Add($"ModKey '{modKey}' found in multiple folders ({string.Join(", ", folderPaths.Select(p => Path.GetFileName(p)))}). Review '{vm.DisplayName}' entry.");
                }
            }
            
            // Final Step 1: Populate and Sort the source list
            _allModSettings = tempList.OrderBy(vm => vm.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            // Final Step 2: Show warnings
            if (warnings.Any())
            {
                var warningMessage = new StringBuilder("Warnings encountered during Mod Settings population:\n\n");
                foreach (var warning in warnings) { warningMessage.AppendLine($"- {warning}"); }
                MessageBox.Show(warningMessage.ToString(), "Mod Settings Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Filtering Logic
         private void ApplyFilters()
         {
             IEnumerable<VM_ModSetting> filtered = _allModSettings;

             if (!string.IsNullOrWhiteSpace(NameFilterText))
             {
                 filtered = filtered.Where(vm => vm.DisplayName.Contains(NameFilterText, StringComparison.OrdinalIgnoreCase));
             }

             if (!string.IsNullOrWhiteSpace(PluginFilterText))
             {
                 filtered = filtered.Where(vm => vm.CorrespondingModKey.HasValue &&
                                                vm.CorrespondingModKey.Value.ToString().Contains(PluginFilterText, StringComparison.OrdinalIgnoreCase));
             }

             ModSettingsList.Clear();
             foreach (var vm in filtered)
             {
                 ModSettingsList.Add(vm);
             }
             System.Diagnostics.Debug.WriteLine($"ApplyFilters: Displaying {ModSettingsList.Count} of {_allModSettings.Count} items.");
         }


         // Save Logic
         public void SaveModSettingsToModel()
         {
             _settings.ModSettings.Clear();
             foreach (var vm in _allModSettings) // Save from the full list
             {
                 if (!string.IsNullOrWhiteSpace(vm.DisplayName) &&
                     (vm.CorrespondingModKey.HasValue || vm.CorrespondingFolderPaths.Any() || !string.IsNullOrWhiteSpace(vm.MugShotFolderPath)))
                 {
                     _settings.ModSettings.Add(new Models.ModSetting
                     {
                         DisplayName = vm.DisplayName,
                         ModKey = vm.CorrespondingModKey,
                         CorrespondingFolderPaths = vm.CorrespondingFolderPaths.ToList()
                     });
                 }
             }

             System.Diagnostics.Debug.WriteLine($"DEBUG: SaveModSettingsToModel preparing to save {_settings.ModSettings.Count} items.");

             string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
             JSONhandler<Settings>.SaveJSONFile(_settings, settingsPath, out bool success, out string exception);
              if (!success) { MessageBox.Show($"Error saving settings: {exception}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error); }
              else { System.Diagnostics.Debug.WriteLine("DEBUG: Settings successfully saved by SaveModSettingsToModel."); }
         }
    }
}