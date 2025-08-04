using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Views;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class EasyNpcTranslator
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly NpcConsistencyProvider _consistencyProvider;
    private readonly Lazy<VM_NpcSelectionBar> _lazyNpcSelectionBar;
    private readonly Lazy<VM_Mods> _lazyModListVM;
    private readonly Settings _settings;
    private readonly Lazy<VM_Settings> _lazySettingsVM;

    public EasyNpcTranslator(EnvironmentStateProvider environmentStateProvider,
        NpcConsistencyProvider consistencyProvider,
        Lazy<VM_NpcSelectionBar> lazyNpcSelectionBar, Lazy<VM_Mods> lazyModListVM,
        Settings settings,
        Lazy<VM_Settings> lazySettingsVM)
    {
        _environmentStateProvider = environmentStateProvider;
        _consistencyProvider = consistencyProvider;
        _lazyNpcSelectionBar = lazyNpcSelectionBar;
        _lazyModListVM = lazyModListVM;
        _settings = settings;
        _lazySettingsVM = lazySettingsVM;
    }

    public void ImportEasyNpc()
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = "EasyNPC Profile (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Select EasyNPC Profile Text File"
        };

        if (openFileDialog.ShowDialog() != true) return; // User cancelled

        string filePath = openFileDialog.FileName;
        // Store *successfully matched* potential changes
        var potentialChanges =
            new List<(FormKey NpcKey, ModKey DefaultKey, ModKey AppearanceKey, string NpcName, string
                TargetModDisplayName)>();
        var ambiguousChanges = new Dictionary<ModKey, HashSet<string>>();
        var errors = new List<string>();
        var missingAppearancePlugins = new Dictionary<ModKey, int>();
        var missingNpcPlugins = new Dictionary<FormKey, (ModKey plugin, List<string> modSettingNames)>();
        ; // Track plugins without matching VM_ModSetting
        int lineNum = 0;

        // --- Pass 1: Parse file and identify missing plugins ---
        try
        {
            foreach (string line in File.ReadLines(filePath))
            {
                lineNum++;
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//")) continue;

                var equalSplit = line.Split(new[] { '=' }, 2);
                if (equalSplit.Length != 2)
                {
                    errors.Add($"Line {lineNum}: Invalid format (missing '=').");
                    continue;
                }

                string formStringPart = equalSplit[0].Trim();
                string pluginInfoPart = equalSplit[1].Trim();

                FormKey npcKey = default;
                ModKey defaultKey = default;
                ModKey appearanceKey = default;

                // Parse FormKey
                var formSplit = formStringPart.Split('#');
                if (formSplit.Length == 2 && !string.IsNullOrWhiteSpace(formSplit[0]) &&
                    !string.IsNullOrWhiteSpace(formSplit[1]))
                {
                    try
                    {
                        npcKey = FormKey.Factory($"{formSplit[1]}:{formSplit[0]}");
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Line {lineNum}: Cannot parse FormKey '{formStringPart}'. {ex.Message}");
                        continue;
                    }
                }
                else
                {
                    errors.Add($"Line {lineNum}: Invalid FormString '{formStringPart}'.");
                    continue;
                }

                // Parse Plugins
                var pipeSplit = pluginInfoPart.Split('|');
                if (pipeSplit.Length < 2 || string.IsNullOrWhiteSpace(pipeSplit[0]) ||
                    string.IsNullOrWhiteSpace(pipeSplit[1]))
                {
                    errors.Add($"Line {lineNum}: Invalid Plugin Info '{pluginInfoPart}'.");
                    continue;
                }

                string defaultPluginName = pipeSplit[0].Trim();
                string appearancePluginName = pipeSplit[1].Trim();

                try
                {
                    defaultKey = ModKey.FromFileName(defaultPluginName);
                }
                catch (Exception ex)
                {
                    errors.Add($"Line {lineNum}: Cannot parse Default Plugin '{defaultPluginName}'. {ex.Message}");
                    continue;
                }

                try
                {
                    appearanceKey = ModKey.FromFileName(appearancePluginName);
                }
                catch (Exception ex)
                {
                    errors.Add(
                        $"Line {lineNum}: Cannot parse Appearance Plugin '{appearancePluginName}'. {ex.Message}");
                    continue;
                }

                // Check if a VM_ModSetting exists for the appearance plugin
                // TryGetModSettingForPlugin now finds a VM where *any* key matches.
                // We still need the DisplayName for the consistency provider.
                bool modsExist = false;
                List<string> preFilteredModSettings = new();
                if (_lazyModListVM.Value.TryGetModSettingForPlugin(appearanceKey, out var foundModSettingVms) && foundModSettingVms != null)
                {
                    modsExist = true;
                    preFilteredModSettings = foundModSettingVms.Select(x => x.DisplayName).ToList();
                    foundModSettingVms = foundModSettingVms.Where(x => x.AvailablePluginsForNpcs.ContainsKey(npcKey)).ToList();
                }
                else
                {
                    foundModSettingVms = new();
                }
                
                
                if (!foundModSettingVms.Any())
                {
                    // Not found - track it and skip adding to potentialChanges for now

                    if (!modsExist)
                    {
                        Debug.WriteLine(
                            $"ImportEasyNpc: Appearance Plugin '{appearanceKey}' not found in Mods Menu for NPC {npcKey}.");
                        missingAppearancePlugins.TryAdd(appearanceKey, 0);
                        missingAppearancePlugins[appearanceKey]++;
                    }
                    else
                    {
                        Debug.WriteLine(
                            $"ImportEasyNpc: Appearance Plugin '{appearanceKey}' found in Mods Menu for NPC {npcKey}. but it doesn't contain this NPC.");
                        missingNpcPlugins.TryAdd(npcKey, (appearanceKey, preFilteredModSettings));
                    }
                }
                else
                {
                    // Found - add to potential changes
                    string npcName =
                        _lazyNpcSelectionBar.Value.AllNpcs.FirstOrDefault(n => n.NpcFormKey == npcKey)?.DisplayName ??
                        npcKey.ToString();
                    potentialChanges.Add((npcKey, defaultKey, appearanceKey, npcName,
                        foundModSettingVms.First().DisplayName));

                    if (foundModSettingVms.Count > 1)
                    {
                        ambiguousChanges.TryAdd(appearanceKey, new());
                        foreach (var vm in foundModSettingVms)
                        {
                            ambiguousChanges[appearanceKey].Add(vm.DisplayName);
                        }
                    }
                }
            } // End foreach line
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"Error reading file '{filePath}':\n{ex.Message}", "File Read Error");
            return;
        }

        // --- Handle Parsing Errors (Optional: Show before Missing Plugin check) ---
        if (errors.Any())
        {
            var errorMsg =
                new StringBuilder(
                    $"Encountered {errors.Count} errors while parsing '{Path.GetFileName(filePath)}':\n\n");
            errorMsg.AppendLine(string.Join("\n", errors));
            errorMsg.AppendLine("\nThese lines were skipped. Continue processing?");
            if (!ScrollableMessageBox.Confirm(errorMsg.ToString(), "Parsing Errors"))
            {
                return; // Cancel based on parsing errors
            }
        }

        // --- Handle Missing Appearance Plugins ---
        if (missingAppearancePlugins.Any() || missingNpcPlugins.Any())
        {
            var missingMsg = new StringBuilder();
            if (missingAppearancePlugins.Any())
            {
                missingMsg.AppendLine("The following Appearance Plugins are assigned in your EasyNPC profile, ");
                missingMsg.AppendLine(
                    "but there are no Mods in your Mods Menu that list them as Corresponding Plugins:");
                missingMsg.AppendLine();
                foreach (var missingEntry in missingAppearancePlugins)
                {
                    missingMsg.AppendLine($"- {missingEntry.Key.FileName}: {missingEntry.Value} NPCs");
                }
                
                missingMsg.AppendLine(" ");
            }
            if (missingNpcPlugins.Any())
            {
                missingMsg.AppendLine("The following NPCs are assigned in your EasyNPC profile, ");
                missingMsg.AppendLine(
                    "but they don't exist (or don't have FaceGen) in the assigned mod(s):");
                missingMsg.AppendLine();
                foreach (var missingEntry in missingNpcPlugins)
                {
                    missingMsg.AppendLine($"- {missingEntry.Key.ToString()}: {missingEntry.Value.plugin.FileName} from {string.Join(" or ", missingEntry.Value.modSettingNames)}");
                }
            }
            

            missingMsg.AppendLine(
                $"\nWould you like to import the remaining {potentialChanges.Count} NPCs for which a mod could be found?");

            if (!ScrollableMessageBox.Confirm(missingMsg.ToString(), "Missing Appearance Plugin Mappings",
                    MessageBoxImage.Warning)) // User chose Cancel
            {
                MessageBox.Show("Import cancelled.", "Import Cancelled", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // If Yes, we simply proceed with the already filtered 'potentialChanges' list.
            Debug.WriteLine("User chose to continue, skipping NPCs with missing appearance plugins.");
        }

        // --- Handle Ambiguous changes

        if (ambiguousChanges.Any())
        {
            var ambiguousMsg =
                new StringBuilder(
                    "The following Appearance Plugins from EasyNPC match multiple AppearanceMods in your mod list:");
            foreach (var entry in ambiguousChanges)
            {
                ambiguousMsg.AppendLine($"{entry.Key.FileName}: " + string.Join(", ", entry.Value));
            }

            ambiguousMsg.AppendLine(
                $"\nWould you like to import the corresponding NPCs using the first matched Appearance Mod for each plugin?");

            if (!ScrollableMessageBox.Confirm(ambiguousMsg.ToString(), "Ambiguous Appearance Plugin Mappings",
                    MessageBoxImage.Warning))
            {
                var toRemove = ambiguousChanges.Keys.ToHashSet();
                potentialChanges.RemoveWhere(x => toRemove.Contains(x.AppearanceKey));
            }

            // If Yes, we simply proceed with the already filtered 'potentialChanges' list.
            Debug.WriteLine("User chose to include ambiguous NPCs");
        }

        // Check if there are any changes left to process
        if (!potentialChanges.Any())
        {
            ScrollableMessageBox.Show(
                "No valid changes found to apply after processing the file (possibly due to skipping or parsing errors).",
                "Import Empty");
            return;
        }

        // --- Prepare Confirmation (using the filtered potentialChanges) ---
        var changesToConfirm = new List<string>(); // List of strings for the message box
        // No need for finalApplyList, potentialChanges already has targetModDisplayName

        foreach (var change in potentialChanges)
        {
            // Get current selection display name
            string? currentSelectionDisplayName = _consistencyProvider.GetSelectedMod(change.NpcKey);

            // Add to confirmation list ONLY if overwriting an EXISTING selection
            if (!string.IsNullOrEmpty(currentSelectionDisplayName) &&
                currentSelectionDisplayName != change.TargetModDisplayName)
            {
                const int maxLen = 40;
                string oldDisplay = currentSelectionDisplayName;
                if (oldDisplay.Length > maxLen) oldDisplay = oldDisplay.Substring(0, maxLen - 3) + "...";
                string newDisplay = change.TargetModDisplayName;
                if (newDisplay.Length > maxLen) newDisplay = newDisplay.Substring(0, maxLen - 3) + "...";

                changesToConfirm.Add($"{change.NpcName}: [{oldDisplay}] -> [{newDisplay}]");
            }
            // We will apply all items in potentialChanges list later
        }

        // --- Show Confirmation Dialog ---
        string confirmationMessage = string.Empty;
        int totalToProcess = potentialChanges.Count; // Based on successfully matched items

        if (changesToConfirm.Any()) // Specific *overwrites* will occur
        {
            confirmationMessage =
                $"The following {changesToConfirm.Count} existing NPC appearance assignments will be changed:\n\n" +
                string.Join("\n", changesToConfirm);

            if (!ScrollableMessageBox.Confirm(confirmationMessage, "Confirm Import"))
            {
                return;
            }
        }

        // --- Apply Changes ---
        foreach (var applyItem in potentialChanges) // Iterate the filtered list
        {
            // Update EasyNPC Default Plugin in the model
            _settings.EasyNpcDefaultPlugins[applyItem.NpcKey] = applyItem.DefaultKey;

            // Update Selected Appearance Mod using the consistency provider
            _consistencyProvider.SetSelectedMod(applyItem.NpcKey, applyItem.TargetModDisplayName);
        }

        // Refresh NPC list filter in case selection state changed
        _lazyNpcSelectionBar.Value.ApplyFilter(false);
    }

    /// <summary>
    /// Exports the current NPC appearance assignments and default plugin information
    /// to a text file compatible with EasyNPC's profile format.
    /// </summary>
    public void ExportEasyNpc()
    {
        // --- Step 1: Compile NPCs that need to be exported ---

        // Get the FormKeys of all NPCs that currently have an appearance mod selected.
        // This is the primary list of NPCs we need to process for export.
        var assignedAppearanceNpcFormKeys = _settings.SelectedAppearanceMods.Keys.ToList();

        // Check if there are any assignments to export.
        if (!assignedAppearanceNpcFormKeys.Any())
        {
            ScrollableMessageBox.Show("No NPC appearance assignments have been made yet. Nothing to export.",
                "Export Empty");
            return;
        }

        // --- Step 2: Prepare Helper Data and Output Storage ---

        // Retrieve the set of CorrespondingModKeys that the user has configured to exclude
        // when determining the "default" plugin (usually the conflict winner).
        // Store this locally for efficient lookup within the loop.
        var excludedDefaultPlugins =
            new HashSet<ModKey>(_lazySettingsVM.Value.ExclusionSelectorViewModel.SaveToModel()); // Ensure it's a HashSet for O(1) lookups

        // List to hold the formatted strings for each successfully processed NPC.
        List<string> outputStrs = new();
        // Lists to collect errors encountered during processing.
        List<string> formKeyErrors = new List<string>();
        List<string> appearanceModErrors = new List<string>();
        List<string> defaultPluginErrors = new List<string>();
        Dictionary<string, string> facegenOnlyWarnings = new Dictionary<string, string>();


        // --- Step 3: Process each assigned NPC ---

        // Iterate through each NPC FormKey that has an appearance assignment.
        foreach (var npcFormKey in assignedAppearanceNpcFormKeys)
        {
            // --- 3a: Convert FormKey to EasyNPC Form String format ---
            string formString = string.Empty;
            try
            {
                // The EasyNPC format is "PluginFileName.esm#IDHexPart".
                // FormKey.ToString() gives "IDHexPart:PluginFileName.esm". We need to reverse this.
                // ModKey.ToString() usually gives "PluginFileName.esm".
                // IDString() gives the FormID hex part (e.g., "001F3F").
                formString = $"{npcFormKey.ModKey.FileName}#{npcFormKey.IDString()}"; // Use FileName for consistency
            }
            catch (Exception e)
            {
                // If conversion fails (e.g., null ModKey or IDString issues, though unlikely for valid FormKey),
                // record the error and skip this NPC.
                formKeyErrors.Add($"Failed to convert FormKey '{npcFormKey}' to string format: {e.Message}");
                continue; // Skip to the next NPC FormKey
            }

            // --- 3b: Get the assigned Appearance Plugin ModKey ---
            ModKey? appearancePlugin = null; // Use nullable ModKey
            // Retrieve the display name of the selected appearance mod for this NPC.
            // We assume if the key exists in SelectedAppearanceMods, the value (name) is valid.
            var appearanceModName = _settings.SelectedAppearanceMods[npcFormKey];

            // Find the VM_ModSetting in the Mods View list that corresponds to this display name.
            var appearanceMod =
                _lazyModListVM.Value.AllModSettings.FirstOrDefault(mod => mod.DisplayName == appearanceModName);
            if (appearanceMod == null)
            {
                // If no VM_ModSetting is found (e.g., inconsistency after import/manual changes), record error.
                appearanceModErrors.Add(
                    $"NPC {formString}: Could not find Mod Setting entry for assigned appearance '{appearanceModName}'.");
                continue; // Skip this NPC
            }

            // Get the CorrespondingModKey from the found VM_ModSetting. This is the plugin we need for the output.
            if (appearanceMod.AvailablePluginsForNpcs.TryGetValue(npcFormKey, out var availablePlugins))
            {
                if (availablePlugins.Count == 1)
                {
                    appearancePlugin = availablePlugins.First();
                }
                else if (availablePlugins.Count == 0)
                {
                    appearanceModErrors.Add(
                        $"NPC {formString}: Mod Setting '{appearanceModName}' does not have a source plugin for this NPC.");
                    continue; // Skip this NPC
                }
                else if (appearanceMod.NpcPluginDisambiguation.TryGetValue(npcFormKey, out var specificKey))
                {
                    appearancePlugin = specificKey;
                }
                else
                {
                    appearanceModErrors.Add(
                        $"NPC {formString}: Source plugin is ambiguous within Mod Setting '{appearanceModName}'. Cannot export.");
                    continue; // Skip this NPC
                }
            }
            else if (appearanceMod.IsFaceGenOnlyEntry)
            {
                appearancePlugin = npcFormKey.ModKey;
                facegenOnlyWarnings.Add(formString, $"NPC {formString} from {appearanceModName}");
            }
            else
            {
                appearanceModErrors.Add(
                    $"NPC {formString}: Mod Setting '{appearanceModName}' does not have a source plugin for this NPC.");
                continue; // Skip this NPC
            }

            // --- 3c: Determine the Default Plugin ModKey ---
            ModKey defaultPlugin = default; // Use default ModKey struct (represents null/invalid state)
            // First, check if a default plugin has been explicitly set for this NPC (e.g., via import).
            if (_settings.EasyNpcDefaultPlugins.TryGetValue(npcFormKey, out var presetDefaultPlugin))
            {
                defaultPlugin = presetDefaultPlugin;
            }
            else // If no preset default, determine it from the load order context.
            {
                // Resolve all plugins that provide a record for this NPC, ordered by load order priority (winners first).
                // This requires the LinkCache from the EnvironmentStateProvider.
                if (_environmentStateProvider.LinkCache == null)
                {
                    defaultPluginErrors.Add(
                        $"NPC {formString}: Cannot determine default plugin because Link Cache is not available.");
                    continue; // Cannot proceed without LinkCache
                }

                var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcFormKey);
                if (!contexts.Any())
                {
                    // Should be unlikely if the NPC exists, but handle defensively.
                    defaultPluginErrors.Add(
                        $"NPC {formString}: Cannot determine default plugin because no context found in Link Cache.");
                    continue; // Skip this NPC
                }

                // Iterate through the overriding plugins (highest priority first).
                foreach (var context in contexts)
                {
                    // Check if the plugin providing this override is in the exclusion list.
                    if (!excludedDefaultPlugins.Contains(context.ModKey))
                    {
                        // This is the first non-excluded plugin, consider it the default.
                        defaultPlugin = context.ModKey;
                        break; // Stop searching once the default is found.
                    }
                }
                // If the loop completes without finding a non-excluded plugin, defaultPlugin remains default(ModKey).
            }

            // Validate the determined default plugin.
            if (defaultPlugin.IsNull) // Use IsNull check for default(ModKey)
            {
                // This could happen if all overrides were excluded or if resolution failed.
                defaultPluginErrors.Add($"NPC {formString}: Could not determine a non-excluded Default Plugin.");
                continue; // Skip this NPC
            }

            // --- 3d: Assemble the output string ---
            // Format: PluginName#IDHex=DefaultPluginFileName|AppearancePluginFileName|
            // Use FileName property for cleaner output, matching EasyNPC expectation.
            outputStrs.Add($"{formString}={defaultPlugin.FileName}|{appearancePlugin.Value.FileName}|");
        } // End foreach npcFormKey


        // --- Step 4: Report Errors and Confirm Save ---

        // Consolidate all errors found during processing.
        var allErrors = formKeyErrors.Concat(appearanceModErrors).Concat(defaultPluginErrors).ToList();

        // If any errors occurred, display them and ask the user whether to proceed with saving the valid entries.
        if (allErrors.Any())
        {
            var errorMsg = new StringBuilder($"Encountered {allErrors.Count} errors during export processing:\n\n");
            errorMsg.AppendLine(string.Join("\n", allErrors)); 
            errorMsg.AppendLine("\nDo you want to save the successfully processed entries?");

            if (!ScrollableMessageBox.Confirm(errorMsg.ToString(), "Export Errors"))
            {
                MessageBox.Show("Export cancelled due to errors.", "Export Cancelled", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return; // Cancel the export.
            }
            // If Yes, proceed to save the outputStrs list which contains only successful entries.
        }

        if (facegenOnlyWarnings.Any())
        {
            var warningMsg = new StringBuilder($"The following NPCs are assigned to FaceGen-only mods, which EasyNPC can't ");
            warningMsg.AppendLine(
                "differentiate from the originating plugin. Press Yes to include them, or No to remove them from the output:\n\n");
            
            warningMsg.AppendLine(string.Join("\n", facegenOnlyWarnings.Values)); 
            
            if (!ScrollableMessageBox.Confirm(warningMsg.ToString(), "Export Warnings"))
            {
                foreach (var faceGenString in facegenOnlyWarnings.Keys)
                {
                    outputStrs.RemoveWhere(x => x.Contains(faceGenString));
                }
            } 
        }

        // Check if there's anything to save after potential errors/skips
        if (!outputStrs.Any())
        {
            ScrollableMessageBox.Show("No valid NPC assignments could be exported.", "Export Empty");
            return;
        }

        // --- Step 5: Get Output File Path ---

        // Prompt user to select an output file path using a Save File Dialog.
        var saveFileDialog = new SaveFileDialog
        {
            Filter = "EasyNPC Profile (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Save EasyNPC Profile As...",
            FileName = "EasyNPC_Profile_Export.txt" // Suggest a default filename
        };

        if (saveFileDialog.ShowDialog() != true)
        {
            ScrollableMessageBox.Show("Export cancelled by user.", "Export Cancelled");
            return; // User cancelled the save dialog.
        }

        string outputFilePath = saveFileDialog.FileName;


        // --- Step 6: Write Output File ---

        try
        {
            // Save outputStrs (separated by Environment.NewLine()) to the selected output file.
            // Use UTF-8 encoding without BOM, which is common for config files.
            File.WriteAllLines(outputFilePath, outputStrs, new UTF8Encoding(false));

            ScrollableMessageBox.Show(
                $"Successfully exported assignments for {outputStrs.Count} NPCs to:\n{outputFilePath}",
                "Export Complete");
        }
        catch (Exception ex)
        {
            // Handle potential file writing errors (permissions, disk full, etc.).
            ScrollableMessageBox.ShowError($"Failed to save the export file:\n{ex.Message}", "File Save Error");
        }
    }

    /// <summary>
    /// Updates an existing EasyNPC profile file with the current application's
    /// NPC appearance selections. Can optionally add NPCs missing from the file.
    /// </summary>
    /// <param name="addMissingNPCs">If true, NPCs selected in the application but not found in the profile file will be added.</param>
    public async Task UpdateEasyNpcProfile(bool addMissingNPCs)
    {
        // --- Step 1: Get File to Update ---
        var openFileDialog = new OpenFileDialog
        {
            Filter = "EasyNPC Profile (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Select EasyNPC Profile File to Update",
            CheckFileExists = true // Ensure the file exists
        };

        if (openFileDialog.ShowDialog() != true)
        {
            Debug.WriteLine("UpdateEasyNpcProfile: File selection cancelled.");
            return; // User cancelled
        }

        string filePath = openFileDialog.FileName;

        // --- Step 2: Read Existing Profile and Prepare Data ---
        List<string> originalLines;
        var lineLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // Maps FormString -> Line Index
        var errors = new List<string>();
        int lineNum = 0;

        try
        {
            originalLines = File.ReadAllLines(filePath).ToList(); // Read all lines into memory

            // Build the lookup dictionary from valid lines
            foreach (string line in originalLines)
            {
                lineNum++;
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//"))
                    continue; // Skip comments/empty

                var equalSplit = line.Split(new[] { '=' }, 2);
                if (equalSplit.Length != 2) continue; // Skip invalid format lines during lookup build
                string formStringPart = equalSplit[0].Trim();

                // Basic validation of FormString format (Plugin#ID)
                var formSplit = formStringPart.Split('#');
                if (formSplit.Length == 2 && !string.IsNullOrWhiteSpace(formSplit[0]) &&
                    !string.IsNullOrWhiteSpace(formSplit[1]))
                {
                    if (!lineLookup.TryAdd(formStringPart, lineNum - 1)) // Store 0-based index
                    {
                        errors.Add(
                            $"Duplicate FormString '{formStringPart}' found at line {lineNum}. Only the first occurrence will be updated.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"Error reading existing profile file '{filePath}':\n{ex.Message}",
                "File Read Error");
            return;
        }

        // --- Step 3: Get App Selections and Prepare Updates ---
        var appNpcSelections = _settings.SelectedAppearanceMods.ToList(); // Get current selections as pairs
        if (!appNpcSelections.Any())
        {
            ScrollableMessageBox.Show(
                "No NPC appearance assignments are currently selected in the application. Nothing to update.",
                "Update Empty");
            return;
        }

        var updatedLines = new List<string>(originalLines); // Create a mutable copy
        var processedFormStrings =
            new HashSet<string>(StringComparer
                .OrdinalIgnoreCase); // Track which FormStrings from the file were updated/found
        var addedLines = new List<string>(); // Track lines added for missing NPCs
        var skippedMissingNpcs = new List<string>(); // Track NPCs skipped because addMissingNPCs was false
        var lookupErrors = new List<string>(); // Track errors finding plugins/defaults

        // Retrieve excluded plugins once
        var excludedDefaultPlugins = new HashSet<ModKey>(_lazySettingsVM.Value.ExclusionSelectorViewModel.SaveToModel());

        // --- Step 4: Iterate Through App Selections and Update/Prepare Additions ---
        foreach (var kvp in appNpcSelections)
        {
            var npcFormKey = kvp.Key;
            var selectedAppearanceModName = kvp.Value; // This is the VM_ModSetting.DisplayName

            // --- 4a: Convert FormKey to EasyNPC Form String ---
            string formString;
            try
            {
                formString = $"{npcFormKey.ModKey.FileName}#{npcFormKey.IDString()}";
            }
            catch (Exception e)
            {
                lookupErrors.Add($"Skipping NPC {npcFormKey}: Cannot format FormKey - {e.Message}");
                continue;
            }

            // --- 4b: Get Appearance Plugin ModKey from selected VM_ModSetting name ---
            var appearanceMod =
                _lazyModListVM.Value.AllModSettings.FirstOrDefault(m => m.DisplayName == selectedAppearanceModName);
            if (appearanceMod == null)
            {
                lookupErrors.Add(
                    $"Skipping NPC {formString}: Cannot find Mod Setting entry named '{selectedAppearanceModName}'.");
                continue;
            }
            
            var appearanceModName = appearanceMod.DisplayName;

            // *** Use the NpcSourcePluginMap to find the specific key for this NPC within this ModSetting ***
            ModKey appearancePluginKey;
            if (appearanceMod.AvailablePluginsForNpcs.TryGetValue(npcFormKey, out var availablePlugins))
            {
                if (appearanceMod.IsFaceGenOnlyEntry)
                {
                    appearancePluginKey = npcFormKey.ModKey;
                }
                else if (availablePlugins.Count == 1)
                {
                    appearancePluginKey = availablePlugins.First();
                }
                else if (availablePlugins.Count == 0)
                {
                    lookupErrors.Add(
                        $"NPC {formString}: Mod Setting '{appearanceModName}' does not have a source plugin for this NPC.");
                    continue; // Skip this NPC
                }
                else if (appearanceMod.NpcPluginDisambiguation.TryGetValue(npcFormKey, out var specificKey))
                {
                    appearancePluginKey = specificKey;
                }
                else
                {
                    lookupErrors.Add(
                        $"NPC {formString}: Source plugin is ambiguous within Mod Setting '{appearanceModName}'. Cannot export.");
                    continue; // Skip this NPC
                }
            }
            else
            {
                lookupErrors.Add(
                    $"NPC {formString}: Mod Setting '{appearanceModName}' does not have a source plugin for this NPC.");
                continue; // Skip this NPC
            }

            // --- 4c: Determine Default Plugin ---
            ModKey defaultPluginKey = default;
            if (!_settings.EasyNpcDefaultPlugins.TryGetValue(npcFormKey, out defaultPluginKey))
            {
                // Not explicitly set, find winning context
                if (_environmentStateProvider.LinkCache != null)
                {
                    var contexts =
                        _environmentStateProvider.LinkCache
                            .ResolveAllContexts<INpc, INpcGetter>(npcFormKey); // Highest first
                    defaultPluginKey = contexts.FirstOrDefault(ctx => !excludedDefaultPlugins.Contains(ctx.ModKey))
                        .ModKey; // Get first non-excluded
                }
            }

            // Validate default key after attempting to find it
            if (defaultPluginKey.IsNull)
            {
                lookupErrors.Add($"Skipping NPC {formString}: Could not determine a valid Default Plugin.");
                continue;
            }

            // --- 4d: Construct the new line's content ---
            string newLineContent = $"{defaultPluginKey.FileName}|{appearancePluginKey.FileName}|";
            string fullNewLine = $"{formString}={newLineContent}";

            // --- 4e: Find existing line or handle missing ---
            if (lineLookup.TryGetValue(formString, out int lineIndex))
            {
                // NPC exists in the file, update the line in our copy
                if (updatedLines[lineIndex] != fullNewLine) // Only mark as processed if changed
                {
                    updatedLines[lineIndex] = fullNewLine;
                    processedFormStrings.Add(formString); // Mark as updated/found
                }
                else
                {
                    processedFormStrings.Add(formString); // Mark as found even if not changed
                }
            }
            else
            {
                // NPC not found in the original file
                if (addMissingNPCs)
                {
                    addedLines.Add(fullNewLine); // Add to a separate list for appending later
                    processedFormStrings.Add(formString); // Mark as processed (added)
                }
                else
                {
                    skippedMissingNpcs.Add(formString); // Track skipped NPC
                }
            }
        } // End foreach app selection

        // --- Step 5: Report Errors and Skipped NPCs ---
        errors.AddRange(lookupErrors); // Combine parsing and lookup errors
        if (errors.Any() || skippedMissingNpcs.Any())
        {
            var reportMsg = new StringBuilder();
            if (errors.Any())
            {
                reportMsg.AppendLine($"Encountered {errors.Count} errors during processing (these NPCs were skipped):");
                reportMsg.AppendLine(string.Join("\n", errors.Select(e => $"- {e}")));
                reportMsg.AppendLine();
            }

            if (skippedMissingNpcs.Any())
            {
                reportMsg.AppendLine(
                    $"Skipped {skippedMissingNpcs.Count} NPCs selected in the app because they were not found in the profile file (Add Missing NPCs was disabled):");
                reportMsg.AppendLine(string.Join("\n", skippedMissingNpcs.Select(s => $"- {s}")));
                reportMsg.AppendLine();
            }

            reportMsg.AppendLine("Do you want to save the updates for the successfully processed NPCs?");

            if (!ScrollableMessageBox.Confirm(reportMsg.ToString(), "Update Issues"))
            {
                ScrollableMessageBox.Show("Update cancelled.", "Update Cancelled");
                return;
            }
        }

        // Add the newly generated lines (if any) to the end of the updated list
        if (addedLines.Any())
        {
            updatedLines.AddRange(addedLines);
        }

        int updatedCount = processedFormStrings.Count - addedLines.Count; // Number of existing lines modified
        int addedCount = addedLines.Count;

        if (updatedCount == 0 && addedCount == 0)
        {
            ScrollableMessageBox.Show("No changes were made to the profile file (assignments might already match).",
                "No Changes");
            return;
        }

        // --- Step 6: Confirm Save Location ---
        var saveFileDialog = new SaveFileDialog
        {
            Filter = "EasyNPC Profile (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Save Updated EasyNPC Profile As...",
            FileName = Path.GetFileName(filePath), // Suggest original name
            InitialDirectory = Path.GetDirectoryName(filePath) // Start in original directory
        };

        if (saveFileDialog.ShowDialog() != true)
        {
            ScrollableMessageBox.Show("Update cancelled by user.", "Update Cancelled");
            return;
        }

        string outputFilePath = saveFileDialog.FileName;

        // --- Step 7: Write Updated File ---
        try
        {
            // Write the potentially modified list (preserving comments/order, appending new)
            File.WriteAllLines(outputFilePath, updatedLines, new UTF8Encoding(false)); // Use UTF-8 without BOM

            string successMessage = $"Successfully updated profile file:\n{outputFilePath}\n\n";
            successMessage += $"Existing NPCs Updated: {updatedCount}\n";
            successMessage += $"Missing NPCs Added: {addedCount}";

            ScrollableMessageBox.Show(successMessage, "Update Complete");
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"Failed to save the updated profile file:\n{ex.Message}",
                "File Save Error");
        }
    }
}