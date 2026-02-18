using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Views;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Handles migrating user settings from older versions of the application to the current version.
/// </summary>
public class UpdateHandler 
{
    private readonly Settings _settings;

    public UpdateHandler(Settings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Checks the settings version against the current program version and applies any necessary updates.
    /// Runs as soon as the settings are read in
    /// </summary>
    public void InitialCheckForUpdatesAndPatch()
    {
        // If the settings version is empty (e.g., a new user), there's nothing to migrate.
        if (string.IsNullOrWhiteSpace(_settings.ProgramVersion))
        {
            Debug.WriteLine("New user or fresh settings, skipping update check.");
            return;
        }

        // Use the custom ProgramVersion class for comparison.
        // The string from settings is implicitly converted to a ProgramVersion object.
        ProgramVersion settingsVersion = _settings.ProgramVersion;
        ProgramVersion currentVersion = App.ProgramVersion;

        Debug.WriteLine($"Updating settings from version {settingsVersion} to {currentVersion}");

        // --- Update Logic ---
        // Updates should be cumulative. If a user jumps from 2.0.7 to 2.1.0,
        // all intermediate patches (for < 2.0.8, < 2.0.9, etc.) should be applied in order.
        // The `if` statements are not `else if` for this reason.

        if (settingsVersion < "2.0.4")
        {
            UpdateTo2_0_4_Initial();
        }

        if (!_settings.HasUpdatedTo2_0_7)
        {
            UpdateTo2_0_7_Initial();
        }

        Debug.WriteLine("Settings update process complete.");
    }

    // <summary>
    /// Checks the settings version against the current program version and applies any necessary updates.
    /// Runs after UI initializes
    /// </summary>
    public async Task FinalCheckForUpdatesAndPatch(VM_NpcSelectionBar npcsVm, VM_Mods modsVm, PluginProvider pluginProvider,
        Auxilliary aux, VM_SplashScreen? splashReporter)
    {
        // If the settings version is empty (e.g., a new user), there's nothing to migrate.
        if (string.IsNullOrWhiteSpace(_settings.ProgramVersion))
        {
            Debug.WriteLine("New user or fresh settings, skipping update check.");
            return;
        }

        // Use the custom ProgramVersion class for comparison.
        // The string from settings is implicitly converted to a ProgramVersion object.
        ProgramVersion settingsVersion = _settings.ProgramVersion;
        ProgramVersion currentVersion = App.ProgramVersion;

        // If the settings version is the same or newer, no action is needed.
        if (settingsVersion >= currentVersion)
        {
            //return;
        }

        splashReporter?.UpdateStep($"Updating settings from version {settingsVersion} to {currentVersion}");
        Debug.WriteLine($"Updating settings from version {settingsVersion} to {currentVersion}");

        // --- Update Logic ---
        // Updates should be cumulative. If a user jumps from 2.0.7 to 2.1.0,
        // all intermediate patches (for < 2.0.8, < 2.0.9, etc.) should be applied in order.
        // The `if` statements are not `else if` for this reason.

        if (settingsVersion < "2.0.4")
        {
            await UpdateTo2_0_4_Final(modsVm, splashReporter);
        }

        if (settingsVersion < "2.0.5")
        {
            await UpdateTo2_0_5_Final(modsVm, splashReporter);
        }

        if (settingsVersion < "2.0.9" && !_settings.HasUpdatedTo2_0_7_templates)
        {
            await UpdateTo2_0_7_Final(modsVm, npcsVm, splashReporter);
        }

        if (settingsVersion < "2.1.1")
        {
            await UpdateTo2_1_1_Final(modsVm, splashReporter);
        }
        
        if (settingsVersion < "2.1.3")
        {
            await UpdateTo2_1_3_Final(modsVm, npcsVm, pluginProvider, aux, splashReporter);
        }

        Debug.WriteLine("Settings update process complete.");
    }

    private void UpdateTo2_0_4_Initial()
    {
        var message =
            """
            In previous versions, the "Include Outfits" option was erroneously defaulted to "Enabled". 
            Changing outfits on an existing save can be problematic because it causes NPCs with 
            modified outfits to unequip their clothes. 

            If you would like to disable this option, there is now a batch option in the Mods Menu 
            to enable/disable outfits for all mods.
            """;
        ScrollableMessageBox.Show(message, "Updating to 2.0.4");
    }

    private void UpdateTo2_0_7_Initial()
    {
        bool shouldReset = true;

        if (_settings.UsePortraitCreatorFallback)
        {
            var message =
                """
                The Portrait Creator has received significant updates in the 2.0.7 release. 
                It is strongly recommended to reset Portrait Creator settings to default. 

                Would you like to do so?
                """;

            shouldReset = ScrollableMessageBox.Confirm(message, "Portrait Creator Settings Update");
        }

        if (shouldReset)
        {
            // Reset all Portrait Creator settings to their new defaults
            _settings.MugshotBackgroundColor = Color.FromRgb(58, 61, 64);
            _settings.VerticalFOV = 25;
            _settings.HeadTopOffset = 0.0f;
            _settings.HeadBottomOffset = -0.05f;
            _settings.CamPitch = 2.0f;
            _settings.CamYaw = 90.0f;
            _settings.CamRoll = 0.0f;
            _settings.CamX = 0.0f;
            _settings.CamY = 0.0f;
            _settings.CamZ = 0.0f;
            _settings.SelectedCameraMode = PortraitCameraMode.Portrait;

            _settings.DefaultLightingJsonString = @"
{
    ""lights"": [
        {
            ""color"": [
                1.0,
                0.8799999952316284,
                0.699999988079071
            ],
            ""intensity"": 0.6499999761581421,
            ""type"": ""ambient""
        },
        {
            ""color"": [
                1.0,
                0.8500000238418579,
                0.6499999761581421
            ],
            ""direction"": [
                -0.0798034518957138,
                -0.99638432264328,
                -0.029152285307645798
            ],
            ""intensity"": 1.600000023841858,
            ""type"": ""directional""
        },
        {
            ""color"": [
                1.0,
                0.8700000047683716,
                0.6800000071525574
            ],
            ""direction"": [
                0.12252168357372284,
                -0.6893905401229858,
                0.7139532566070557
            ],
            ""intensity"": 0.800000011920929,
            ""type"": ""directional""
        }
    ]
}";
            _settings.EnableNormalMapHack = true;

            Debug.WriteLine("Portrait Creator settings reset to 1.0.7 defaults.");
        }

        // Always mark as updated, even if user declined the reset
        _settings.HasUpdatedTo2_0_7 = true;
    }

    private async Task UpdateTo2_0_4_Final(VM_Mods modsVm, VM_SplashScreen? splashReporter)
    {
        var modsToScan = modsVm.AllModSettings.Where(modVm =>
            modVm.DisplayName != VM_Mods.BaseGameModSettingName &&
            modVm.DisplayName != VM_Mods.CreationClubModsettingName &&
            !modVm.IsFaceGenOnlyEntry).ToList();

        splashReporter?.UpdateStep($"Updating to 2.0.4: Scanning mods for injected records...", modsToScan.Count);

        var modsWithInjectedRecords = new ConcurrentBag<VM_ModSetting>();

        // 1. Perform the expensive, IO-bound work on background threads
        await Task.Run(() =>
        {
            Parallel.ForEach(modsToScan, modVm =>
            {
                // .Result is acceptable here as we are already inside a background thread via Task.Run
                if (modVm.CheckForInjectedRecords(splashReporter == null ? null : splashReporter.ShowMessagesOnClose,
                        _settings.LocalizationLanguage).Result)
                {
                    modsWithInjectedRecords.Add(modVm);
                }

                splashReporter?.IncrementProgress(string.Empty);
            });
        });

        // 2. Perform the UI update on the UI thread after all parallel work is complete
        foreach (var modVm in modsWithInjectedRecords)
        {
            modVm.HasAlteredHandleInjectedRecordsLogic = true;
        }
    }

    private async Task UpdateTo2_0_5_Final(VM_Mods modsVm, VM_SplashScreen? splashReporter)
    {
        // Call the public refresh coordinator, passing the existing splash screen reporter
        await modsVm.RefreshAllModSettingsAsync(splashReporter);
    }

    private async Task UpdateTo2_0_7_Final(VM_Mods modsVm, VM_NpcSelectionBar npcSelectionBar,
        VM_SplashScreen? splashReporter)
    {
        string messageStr =
            "Previous versions of NPC Plugin Chooser allowed you to select appearances for NPCs with invalid templates using the Select All From Mod batch action. This could result in bugged appearances in-game for those NPCs. Would you like to scan and automatically de-select these NPCs?";
        if (!ScrollableMessageBox.Confirm(messageStr, "2.0.7 Update"))
        {
            _settings.HasUpdatedTo2_0_7_templates = true;
            return;
        }

        splashReporter?.UpdateStep("Validating existing NPC selections...");

        var invalidSelections = new List<(FormKey npcKey, string modName, string reason)>();

        // Check all existing selections
        foreach (var selection in _settings.SelectedAppearanceMods.ToList())
        {
            var npcFormKey = selection.Key;
            var (modName, sourceNpcFormKey) = selection.Value;

            // Find the corresponding mod setting
            var modSetting = modsVm.AllModSettings.FirstOrDefault(m =>
                m.DisplayName.Equals(modName, StringComparison.OrdinalIgnoreCase));

            if (modSetting == null)
            {
                // Mod no longer exists - this is a different issue, skip for now
                continue;
            }

            // Validate the selection
            var (isValid, failureReason) = npcSelectionBar.ValidateSelection(npcFormKey, modSetting);

            if (!isValid)
            {
                invalidSelections.Add((npcFormKey, modName, failureReason));
            }
        }

        if (invalidSelections.Any())
        {
            var message = new StringBuilder();
            message.AppendLine($"Found {invalidSelections.Count} invalid NPC selection(s) from previous versions.");
            message.AppendLine();
            message.AppendLine(
                "These selections have template chain issues that will likely cause incorrect appearances in-game.");
            message.AppendLine();
            message.AppendLine("Would you like to deselect these NPCs? (Recommended)");
            message.AppendLine();
            message.AppendLine("Details:");
            message.AppendLine();

            foreach (var (npcKey, modName, reason) in invalidSelections)
            {
                message.AppendLine($"• {reason}");
            }

            if (ScrollableMessageBox.Confirm(message.ToString(), "Invalid NPC Selections Found",
                    MessageBoxImage.Warning))
            {
                // User confirmed - deselect all problematic NPCs
                foreach (var (npcKey, modName, _) in invalidSelections)
                {
                    _settings.SelectedAppearanceMods.Remove(npcKey);
                    Debug.WriteLine($"Deselected invalid selection: {npcKey} -> {modName}");
                }

                ScrollableMessageBox.Show($"Deselected {invalidSelections.Count} invalid NPC selection(s).",
                    "Selections Cleared");
            }
            else
            {
                ScrollableMessageBox.ShowWarning(
                    "Invalid selections were kept. These NPCs may have incorrect appearances in-game until you manually correct them.",
                    "Selections Kept");
            }
        }
        else
        {
            Debug.WriteLine("No invalid NPC selections found during update check.");
        }

        _settings.HasUpdatedTo2_0_7_templates = true;
    }

    private async Task UpdateTo2_1_1_Final(VM_Mods modsVm, VM_SplashScreen? splashReporter)
    {
        splashReporter?.UpdateStep("Caching SkyPatcher Templates...");

        // We need empty maps because GetSkyPatcherImportsAsync requires them for resolving editor IDs,
        // though for this specific update we are mostly interested in FormKey matches which don't need the map.
        // If your mods rely heavily on EditorID mapping for SkyPatcher, we might miss some here without full maps,
        // but building full maps is expensive. 
        // Ideally, we reuse the maps if available, but passing empty ones is safe to prevent crashes.
        var emptyMap = new Dictionary<string, HashSet<FormKey>>();

        var allMods = modsVm.AllModSettings.ToList();
        int count = 0;

        await Task.Run(async () =>
        {
            foreach (var mod in allMods)
            {
                try
                {
                    // Re-parse the INIs for this mod
                    var imports = await mod.GetSkyPatcherImportsAsync(emptyMap, emptyMap);

                    foreach (var import in imports)
                    {
                        // Cache the SOURCE NPC (the template)
                        lock (_settings.CachedSkyPatcherTemplates)
                        {
                            _settings.CachedSkyPatcherTemplates.Add(import.SourceNpc);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error scanning SkyPatcher INIs for {mod.DisplayName}: {ex.Message}");
                }

                count++;
                splashReporter?.UpdateProgress((double)count / allMods.Count * 100, $"Scanning {mod.DisplayName}...");
            }
        });

        Debug.WriteLine(
            $"SkyPatcher Template Scan Complete. Cached {_settings.CachedSkyPatcherTemplates.Count} templates.");
    }
    
    /// <summary>
    /// Prunes NPCs from all mod entries that no longer pass the updated race/template
    /// validation (e.g. templated dragons, spiders, etc. that were previously allowed
    /// through because any templated NPC bypassed the ActorTypeNPC check).
    /// 
    /// When the same NPC appears in multiple plugins within a single VM_ModSetting,
    /// priority is determined by CorrespondingModKeys order (last = highest priority).
    /// If the highest-priority version passes the check, the NPC is kept even if a
    /// lower-priority version would fail.
    /// </summary>
    private async Task UpdateTo2_1_3_Final(VM_Mods modsVm, VM_NpcSelectionBar npcsVm,
        PluginProvider pluginProvider, Auxilliary aux, VM_SplashScreen? splashReporter)
    {
        splashReporter?.UpdateStep("Updating to 2.1.3: Pruning invalid templated NPCs...");

        var modsToCheck = modsVm.AllModSettings.ToList();

        if (!modsToCheck.Any())
        {
            Debug.WriteLine("2.1.3 Update: No mod entries found.");
            return;
        }

        // --- Phase 1: Scan all mods and compile the full removal manifest on a background thread ---
        // Key: VM_ModSetting  Value: list of (FormKey, logString) pairs flagged for removal
        var removalManifest = new Dictionary<VM_ModSetting, List<(FormKey NpcFormKey, string LogString)>>();
        // Track which plugins were loaded per mod so we can unload them afterwards
        var loadedPathsByMod = new Dictionary<VM_ModSetting, HashSet<string>>();
        // Keep the loaded plugins alive for Phase 3 (NpcNames/NpcEditorIDs rebuild)
        var pluginsByMod = new Dictionary<VM_ModSetting, HashSet<ISkyrimModGetter>>();

        await Task.Run(() =>
        {
            int modIndex = 0;
            foreach (var vm in modsToCheck)
            {
                modIndex++;
                splashReporter?.UpdateProgress((double)modIndex / modsToCheck.Count * 50,
                    $"Scanning {vm.DisplayName}...");

                var modFolderPaths = vm.CorrespondingFolderPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var plugins = pluginProvider.LoadPlugins(vm.CorrespondingModKeys, modFolderPaths, out var loadedPaths);
                loadedPathsByMod[vm] = loadedPaths;
                pluginsByMod[vm] = plugins;

                // Build a lookup of NPC records respecting CorrespondingModKeys priority.
                // Iterate plugins in CorrespondingModKeys order so that later (higher-priority)
                // entries overwrite earlier ones.
                var npcLookup = new Dictionary<FormKey, INpcGetter>();
                foreach (var modKey in vm.CorrespondingModKeys)
                {
                    var plugin = plugins.FirstOrDefault(p => p.ModKey == modKey);
                    if (plugin == null) continue;

                    foreach (var npc in plugin.Npcs)
                    {
                        npcLookup[npc.FormKey] = npc; // last-wins: higher-priority ModKey overwrites
                    }
                }

                var flaggedForRemoval = new List<(FormKey, string)>();

                foreach (var npcFormKey in vm.NpcFormKeys)
                {
                    // If the NPC isn't in the loaded plugins, it may have come from another source
                    // (e.g. FaceGen-only or mugshot-only). Leave it alone.
                    if (!npcLookup.TryGetValue(npcFormKey, out var npcGetter))
                    {
                        continue;
                    }

                    if (!aux.IsValidAppearanceRace(npcGetter.Race.FormKey, npcGetter,
                            _settings.LocalizationLanguage, out string rejectionMessage, out var resolvedRace))
                    {
                        var raceLogStr = resolvedRace != null
                            ? Auxilliary.GetLogString(resolvedRace, _settings.LocalizationLanguage, fullString: false)
                            : npcGetter.Race.FormKey.ToString();
                        var logStr = Auxilliary.GetLogString(npcGetter, _settings.LocalizationLanguage, fullString: true)
                                     + $" [Race: {raceLogStr}]";
                        flaggedForRemoval.Add((npcFormKey, logStr));
                        Debug.WriteLine(
                            $"2.1.3 Update: Flagging {logStr} from {vm.DisplayName} because {rejectionMessage}");
                    }
                }

                if (flaggedForRemoval.Any())
                {
                    removalManifest[vm] = flaggedForRemoval;
                }
            }
        });

        // If nothing to remove, clean up and return early
        if (!removalManifest.Any())
        {
            Debug.WriteLine("2.1.3 Update: No invalid templated NPCs found across any mods.");
            foreach (var kvp in loadedPathsByMod)
            {
                pluginProvider.UnloadPlugins(kvp.Value);
            }
            return;
        }

        // --- Phase 2: User notification and optional backup (UI thread) ---
        int totalFlagged = removalManifest.Values.Sum(list => list.Count);

        // Build a combined display message
        var displayMessage = new StringBuilder();
        displayMessage.AppendLine(
            "2.1.3 has updated its NPC loader to exclude non-humanoid template NPCs which previously " +
            "had been erroneously included in the NPC list. The following NPCs are slated for removal:");
        displayMessage.AppendLine();

        foreach (var (vm, flagged) in removalManifest)
        {
            displayMessage.AppendLine($"[{vm.DisplayName}] ({flagged.Count} NPC(s)):");
            foreach (var (_, logStr) in flagged)
            {
                displayMessage.AppendLine($"  • {logStr}");
            }
            displayMessage.AppendLine();
        }

        // Check if any flagged NPCs have user assignments
        var allFlaggedFormKeys = removalManifest.Values
            .SelectMany(list => list.Select(entry => entry.NpcFormKey))
            .ToHashSet();

        var flaggedWithAssignments = allFlaggedFormKeys
            .Where(fk => _settings.SelectedAppearanceMods.ContainsKey(fk))
            .ToList();

        if (flaggedWithAssignments.Any())
        {
            var backupMessage = new StringBuilder();
            backupMessage.AppendLine(
                "2.1.3 has updated its NPC loader to exclude non-humanoid template NPCs which previously " +
                "had been erroneously included in the NPC list. You have made a selection for the following " +
                "NPCs which are slated for removal. Would you like to make a backup of your selections now " +
                "so that if any of the removals are erroneous, you can restore them by re-importing your list?");
            backupMessage.AppendLine();

            foreach (var fk in flaggedWithAssignments)
            {
                var (modName, _) = _settings.SelectedAppearanceMods[fk];
                // Find the log string from the manifest
                var logStr = removalManifest.Values
                    .SelectMany(list => list)
                    .FirstOrDefault(entry => entry.NpcFormKey == fk).LogString ?? fk.ToString();
                backupMessage.AppendLine($"  • {logStr}  →  [{modName}]");
            }

            if (ScrollableMessageBox.Confirm(backupMessage.ToString(), "Backup Selections Before 2.1.3 Update",
                    MessageBoxImage.Warning))
            {
                // Execute the same export that the Export button uses
                try
                {
                    await npcsVm.ExportChoicesCommand.Execute().FirstAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"2.1.3 Update: Export failed or was cancelled: {ex.Message}");
                    // If the export failed, we still proceed with the removal
                }
            }
        }

        // --- Phase 3: Perform the removal and rebuild NpcNames/NpcEditorIDs on a background thread ---
        splashReporter?.UpdateStep("Updating to 2.1.3: Removing invalid NPCs...");

        await Task.Run(() =>
        {
            int totalRemoved = 0;

            foreach (var (vm, flagged) in removalManifest)
            {
                var formKeysToRemove = flagged.Select(entry => entry.NpcFormKey).ToHashSet();

                // Remove from all mod-level collections
                foreach (var formKey in formKeysToRemove)
                {
                    vm.NpcFormKeys.Remove(formKey);
                    vm.NpcFormKeysToDisplayName.Remove(formKey);
                    vm.AvailablePluginsForNpcs.Remove(formKey);
                    vm.NpcFormKeysToNotifications.Remove(formKey);
                    vm.AmbiguousNpcFormKeys.Remove(formKey);

                    // Clear any user selection for the pruned NPC
                    _settings.SelectedAppearanceMods.Remove(formKey);
                }

                // Rebuild NpcNames and NpcEditorIDs from the remaining NPCs
                var remainingNpcNames = new HashSet<string>();
                var remainingNpcEditorIDs = new HashSet<string>();
                var npcFormKeysFoundInPlugins = new HashSet<FormKey>();

                if (pluginsByMod.TryGetValue(vm, out var plugins))
                {
                    foreach (var plugin in plugins)
                    {
                        foreach (var npc in plugin.Npcs)
                        {
                            // Only include NPCs that are still in the mod's NPC list
                            if (!vm.NpcFormKeys.Contains(npc.FormKey)) continue;
                            npcFormKeysFoundInPlugins.Add(npc.FormKey);

                            if (Auxilliary.TryGetName(npc, _settings.LocalizationLanguage,
                                    _settings.FixGarbledText, out string name))
                            {
                                remainingNpcNames.Add(name);
                            }

                            if (!string.IsNullOrEmpty(npc.EditorID))
                            {
                                remainingNpcEditorIDs.Add(npc.EditorID);
                            }
                        }
                    }
                }
                
                // For remaining NPCs not found in plugins (mugshot-only, FaceGen-only),
                // preserve their display names so search still works
                foreach (var npcFormKey in vm.NpcFormKeys)
                {
                    if (npcFormKeysFoundInPlugins.Contains(npcFormKey)) continue;
                    if (vm.NpcFormKeysToDisplayName.TryGetValue(npcFormKey, out var displayName)
                        && !string.IsNullOrEmpty(displayName))
                    {
                        remainingNpcNames.Add(displayName);
                    }
                }

                vm.NpcNames = remainingNpcNames;
                vm.NpcEditorIDs = remainingNpcEditorIDs;
                vm.RefreshNpcCount();

                totalRemoved += formKeysToRemove.Count;
                Debug.WriteLine(
                    $"2.1.3 Update: Removed {formKeysToRemove.Count} invalid NPC(s) from {vm.DisplayName}");
            }

            Debug.WriteLine($"2.1.3 Update: Pruning complete. Removed {totalRemoved} invalid NPC(s) total.");
        });
        
        // --- Phase 4: Synchronize the NPC selection bar (UI thread) ---
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            npcsVm.PruneRemovedNpcs(allFlaggedFormKeys);
        });

        // --- Cleanup: Unload all plugins that were loaded during the scan ---
        foreach (var kvp in loadedPathsByMod)
        {
            pluginProvider.UnloadPlugins(kvp.Value);
        }
    }
}