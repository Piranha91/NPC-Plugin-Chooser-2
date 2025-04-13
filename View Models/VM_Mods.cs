// View Models/VM_Mods.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
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

        [Reactive] public ObservableCollection<VM_ModSetting> ModSettingsList { get; set; } = new();

        // Expose for binding in VM_ModSetting commands
        public string ModsFolderSetting => _settings.ModsFolder;

        public VM_Mods(Settings settings, EnvironmentStateProvider environmentStateProvider, VM_NpcSelectionBar npcSelectionBar)
        {
            _settings = settings;
            _environmentStateProvider = environmentStateProvider;
            _npcSelectionBar = npcSelectionBar;

            // Ensure NPC list is initialized before we use it
            if (!_npcSelectionBar.AllNpcs.Any())
            {
                 // Consider adding a mechanism to wait or re-trigger population if NPC list isn't ready
                 // For now, initialize it if it seems empty (might be redundant if already done elsewhere)
                 // _npcSelectionBar.Initialize(); // Be careful about re-init loops
            }

            PopulateModSettings();

            // TODO: Add mechanism to save changes from ModSettingsList back to _settings.ModSettings
            // This could be triggered on window close (in App.xaml.cs or VM_MainWindow)
            // or via an explicit "Save" button in the ModsView.
            // For now, changes are only in memory.
             // Example: Application.Current.Exit += (s, e) => SaveModSettingsToModel();
        }

        private void PopulateModSettings()
        {
            ModSettingsList.Clear();
            var warnings = new List<string>();
            var loadedDisplayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var linkedModKeys = new HashSet<ModKey>(); // Track keys linked in Phase 2c

            // --- Phase 1: Load existing data from Settings ---
            foreach (var setting in _settings.ModSettings)
            {
                if (string.IsNullOrWhiteSpace(setting.DisplayName)) continue; // Skip invalid entries

                var vm = new VM_ModSetting(setting, this);
                // Try to find MugShotFolderPath based on DisplayName (matching Phase 2a logic)
                if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder))
                {
                    string potentialMugshotPath = Path.Combine(_settings.MugshotsFolder, vm.DisplayName);
                    if (Directory.Exists(potentialMugshotPath))
                    {
                        vm.MugShotFolderPath = potentialMugshotPath;
                    }
                }
                ModSettingsList.Add(vm);
                loadedDisplayNames.Add(vm.DisplayName);
                if (vm.CorrespondingModKey.HasValue)
                {
                    linkedModKeys.Add(vm.CorrespondingModKey.Value); // Track keys already present in settings
                }
            }

            // --- Phase 2a: Create new VM_ModSettings from Mugshot folders ---
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
                            var vm = new VM_ModSetting(folderName, this)
                            {
                                MugShotFolderPath = dirPath
                            };
                            vmsFromMugshots.Add(vm);
                            // Don't add to ModSettingsList yet, wait for Phase 2b/2c linking
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Error scanning Mugshots folder '{_settings.MugshotsFolder}': {ex.Message}");
                }
            }

            // --- Phase 2b: Try to link CorrespondingFolderPaths to the new VM_ModSettings created in Phase 2a ---
            var vmsLinkedToModFolder = new List<VM_ModSetting>();
            if (!string.IsNullOrWhiteSpace(_settings.ModsFolder) && Directory.Exists(_settings.ModsFolder))
            {
                try
                {
                    foreach (var modFolderPath in Directory.EnumerateDirectories(_settings.ModsFolder))
                    {
                        string modFolderName = Path.GetFileName(modFolderPath);
                        // Find a VM created in Phase 2a that matches this folder name
                        var matchingVm = vmsFromMugshots.FirstOrDefault(vm => vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));
                        if (matchingVm != null && !matchingVm.CorrespondingFolderPaths.Any())
                        {
                            matchingVm.CorrespondingFolderPaths.Add(modFolderPath);
                            vmsLinkedToModFolder.Add(matchingVm); // Track VMs that got a path in this step
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Error scanning Mods folder '{_settings.ModsFolder}' for linking: {ex.Message}");
                }
            }

            // --- Phase 2c: Try to link CorrespondingModKeys to the new VM_ModSettings altered in Phase 2b ---
            // Compile AppearanceModKeys
            var appearanceModKeys = new HashSet<ModKey>();
            if (_npcSelectionBar.AllNpcs != null) // Check if AllNpcs is available
            {
                try
                {
                     appearanceModKeys = _npcSelectionBar.AllNpcs
                                                    .Where(npc => npc != null && npc.AppearanceMods != null) // Add null checks
                                                    .SelectMany(npc => npc.AppearanceMods)
                                                    .Distinct()
                                                    .ToHashSet();
                }
                catch (Exception ex)
                {
                     warnings.Add($"Error compiling AppearanceModKeys: {ex.Message}");
                     // Continue without appearanceModKeys if compilation fails? Or halt? Let's add warning and continue.
                }
            }
            else
            {
                 warnings.Add("Could not compile AppearanceModKeys: NPC list not available.");
            }


            foreach (var vm in vmsLinkedToModFolder) // Only check VMs that received a folder path in Phase 2b
            {
                if (vm.CorrespondingModKey.HasValue) continue; // Already has a key (maybe from Settings originally?)

                string folderPath = vm.CorrespondingFolderPaths.First(); // We added only one path in 2b
                if (Directory.Exists(folderPath))
                {
                    try
                    {
                        foreach (var filePath in Directory.EnumerateFiles(folderPath))
                        {
                            string fileName = Path.GetFileNameWithoutExtension(filePath); // Check file name itself
                            string fileNameWithExt = Path.GetFileName(filePath); // Check full filename if needed

                            foreach (var modKey in appearanceModKeys)
                            {
                                // Compare filename (without extension) against ModKey.ToString()
                                if (fileName.Equals(modKey.ToString(), StringComparison.OrdinalIgnoreCase) ||
                                    fileNameWithExt.Equals(modKey.FileName, StringComparison.OrdinalIgnoreCase)) // Also check full filename match
                                {
                                    vm.CorrespondingModKey = modKey;
                                    linkedModKeys.Add(modKey);
                                    goto NextVmInPhase2c; // Found key for this VM, move to the next VM
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Error scanning files in '{folderPath}' for ModKey linking: {ex.Message}");
                    }
                }
            NextVmInPhase2c:;
            }

            // Add VMs created/linked in Phase 2 to the main list
            foreach(var vm in vmsFromMugshots)
            {
                // Add only if it was linked to a mod folder OR if we want to show mugshot-only entries
                // Based on requirements, seems we only add if linked or if it's an orphan later
                // Let's add all that got linked in 2b/2c
                if (vm.CorrespondingFolderPaths.Any() || vm.CorrespondingModKey.HasValue)
                {
                    if (!ModSettingsList.Any(existing => existing.DisplayName.Equals(vm.DisplayName, StringComparison.OrdinalIgnoreCase)))
                    {
                         ModSettingsList.Add(vm);
                    }
                }
                // Else: it's a mugshot folder with no matching mod folder found - decide if these should be shown.
                // Requirement doesn't explicitly state, let's omit them for now.
            }


            // --- Phase 3: Create new VM_ModSettings from "orphaned" ModKeys ---
            var orphanedModKeys = appearanceModKeys.Except(linkedModKeys).ToList();
            var modKeyToFileLocations = new Dictionary<ModKey, List<string>>();

            if (orphanedModKeys.Any() && !string.IsNullOrWhiteSpace(_settings.ModsFolder) && Directory.Exists(_settings.ModsFolder))
            {
                try
                {
                    // Search only top-level folders within ModsFolder, as implied by Phase 2b
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
                                         // Add the *folder* path, not the file path
                                         if (!locations.Contains(modFolderPath, StringComparer.OrdinalIgnoreCase))
                                         {
                                             locations.Add(modFolderPath);
                                         }
                                     }
                                 }
                             }
                         }
                         catch (Exception ex)
                         {
                             warnings.Add($"Error scanning files in '{modFolderPath}' for orphaned ModKeys: {ex.Message}");
                         }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Error scanning Mods folder '{_settings.ModsFolder}' for orphaned ModKeys: {ex.Message}");
                }
            }

            // Create VMs for orphans
            foreach (var kvp in modKeyToFileLocations)
            {
                var modKey = kvp.Key;
                var folderPaths = kvp.Value;

                // Check if a VM for this ModKey already exists (e.g., from Settings phase 1)
                if (ModSettingsList.Any(vm => vm.CorrespondingModKey == modKey)) continue;

                var vm = new VM_ModSetting(modKey.ToString(), this) // Use ModKey string as default DisplayName
                {
                    CorrespondingModKey = modKey,
                    CorrespondingFolderPaths = new ObservableCollection<string>(folderPaths)
                    // MugShotFolderPath remains empty unless found otherwise
                };

                // Attempt to find MugShotFolderPath based on folder name matching DisplayName (less likely here)
                 if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder))
                 {
                     // Try matching based on the first folder path's name
                     if (folderPaths.Any())
                     {
                         string firstFolderName = Path.GetFileName(folderPaths.First());
                         string potentialMugshotPath = Path.Combine(_settings.MugshotsFolder, firstFolderName);
                         if (Directory.Exists(potentialMugshotPath))
                         {
                              vm.DisplayName = firstFolderName; // Use folder name as display name if mugshot found
                              vm.MugShotFolderPath = potentialMugshotPath;
                         }
                     }
                 }


                ModSettingsList.Add(vm);

                if (folderPaths.Count > 1)
                {
                    warnings.Add($"ModKey '{modKey}' was found in multiple folders ({string.Join(", ", folderPaths.Select(p => Path.GetFileName(p)))}). Please review '{vm.DisplayName}' entry in the Mods tab and remove incorrect paths if necessary.");
                }
            }


            // --- Final Step: Show warnings ---
            if (warnings.Any())
            {
                var warningMessage = new StringBuilder("Warnings encountered during Mod Settings population:\n\n");
                foreach (var warning in warnings)
                {
                    warningMessage.AppendLine($"- {warning}");
                }
                MessageBox.Show(warningMessage.ToString(), "Mod Settings Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

             // TODO: Add sorting if desired ModSettingsList = new ObservableCollection<VM_ModSetting>(ModSettingsList.OrderBy(vm => vm.DisplayName));
        }

         // Method to be called to save VM state back to the model
         public void SaveModSettingsToModel()
         {
             _settings.ModSettings.Clear();
             foreach (var vm in ModSettingsList)
             {
                 // Only save settings that have essential info
                 // Criteria: Has a DisplayName AND (has a ModKey OR has FolderPaths OR has MugshotPath)
                 if (!string.IsNullOrWhiteSpace(vm.DisplayName) &&
                     (vm.CorrespondingModKey.HasValue || vm.CorrespondingFolderPaths.Any() || !string.IsNullOrWhiteSpace(vm.MugShotFolderPath)))
                 {
                     _settings.ModSettings.Add(new Models.ModSetting
                     {
                         DisplayName = vm.DisplayName,
                         ModKey = vm.CorrespondingModKey,
                         CorrespondingFolderPaths = vm.CorrespondingFolderPaths.ToList()
                         // Note: MugShotFolderPath is not saved to the model per requirements
                     });
                 }
             }
             // Trigger the actual save to file (this might be better placed in VM_Settings or App.xaml.cs)
             // VM_Settings.SaveSettings(); // Need static save or access to instance
             System.Diagnostics.Debug.WriteLine("DEBUG: SaveModSettingsToModel called. _settings.ModSettings updated in memory.");

             // Proper saving should be handled by VM_Settings or App's Exit event logic
              string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
              JSONhandler<Settings>.SaveJSONFile(_settings, settingsPath, out bool success, out string exception);
               if (!success)
               {
                    MessageBox.Show($"Error saving settings after Mod Settings update: {exception}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
               }

         }

    }
}