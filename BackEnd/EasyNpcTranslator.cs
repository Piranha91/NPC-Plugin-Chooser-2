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
        var assignedAppearanceNpcFormKeys = _settings.SelectedAppearanceMods.Keys.ToList();
        if (!assignedAppearanceNpcFormKeys.Any())
        {
            ScrollableMessageBox.Show("No NPC appearance assignments have been made yet. Nothing to export.", "Export Empty");
            return;
        }

        // --- Step 2: Prepare Helper Data and Output Storage ---
        var excludedDefaultPlugins = new HashSet<ModKey>(_lazySettingsVM.Value.ExclusionSelectorViewModel.SaveToModel());
        var outputStrs = new List<string>();
        var formKeyErrors = new List<string>();
        var appearanceModErrors = new List<string>();
        var defaultPluginErrors = new List<string>();
        var facegenOnlyWarnings = new Dictionary<string, string>();

        // --- Step 3: Process each assigned NPC ---
        foreach (var npcFormKey in assignedAppearanceNpcFormKeys)
        {
            // --- 3a: Convert FormKey to EasyNPC Form String format ---
            string formString;
            try
            {
                formString = $"{npcFormKey.ModKey.FileName}#{npcFormKey.IDString()}";
            }
            catch (Exception e)
            {
                formKeyErrors.Add($"Failed to convert FormKey '{npcFormKey}' to string format: {e.Message}");
                continue;
            }

            // --- 3b: Get the assigned Appearance Plugin ModKey ---
            var appearanceModName = _settings.SelectedAppearanceMods[npcFormKey];
            if (!TryGetAppearancePlugin(npcFormKey, appearanceModName, out var appearancePlugin, out var faceGenWarning, out var appearanceError))
            {
                appearanceModErrors.Add(appearanceError!);
                continue;
            }

            if (faceGenWarning != null)
            {
                facegenOnlyWarnings.Add(formString, faceGenWarning);
            }

            // --- 3c: Determine the Default Plugin ModKey ---
            if (!TryGetDefaultPlugin(npcFormKey, excludedDefaultPlugins, out var defaultPlugin, out var defaultError))
            {
                defaultPluginErrors.Add(defaultError!);
                continue;
            }

            // --- 3d: Assemble the output string ---
            outputStrs.Add($"{formString}={defaultPlugin.FileName}|{appearancePlugin!.Value.FileName}|");
        } // End foreach npcFormKey

        // --- Step 4: Report Errors and Confirm Save ---
        var allErrors = formKeyErrors.Concat(appearanceModErrors).Concat(defaultPluginErrors).ToList();
        if (allErrors.Any())
        {
            var errorMsg = new StringBuilder($"Encountered {allErrors.Count} errors during export processing:\n\n");
            errorMsg.AppendLine(string.Join("\n", allErrors));
            errorMsg.AppendLine("\nDo you want to save the successfully processed entries?");

            if (!ScrollableMessageBox.Confirm(errorMsg.ToString(), "Export Errors"))
            {
                MessageBox.Show("Export cancelled due to errors.", "Export Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        if (facegenOnlyWarnings.Any())
        {
            var warningMsg = new StringBuilder("The following NPCs are assigned to FaceGen-only mods, which EasyNPC can't ");
            warningMsg.AppendLine("differentiate from the originating plugin. Press Yes to include them, or No to remove them from the output:\n\n");
            warningMsg.AppendLine(string.Join("\n", facegenOnlyWarnings.Values));

            if (!ScrollableMessageBox.Confirm(warningMsg.ToString(), "Export Warnings"))
            {
                outputStrs.RemoveAll(line => facegenOnlyWarnings.ContainsKey(line.Split('=')[0]));
            }
        }

        if (!outputStrs.Any())
        {
            ScrollableMessageBox.Show("No valid NPC assignments could be exported.", "Export Empty");
            return;
        }

        // --- Step 5: Get Output File Path ---
        var saveFileDialog = new SaveFileDialog
        {
            Filter = "EasyNPC Profile (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Save EasyNPC Profile As...",
            FileName = "EasyNPC_Profile_Export.txt"
        };

        if (saveFileDialog.ShowDialog() != true)
        {
            ScrollableMessageBox.Show("Export cancelled by user.", "Export Cancelled");
            return;
        }
        string outputFilePath = saveFileDialog.FileName;

        // --- Step 6: Write Output File ---
        try
        {
            File.WriteAllLines(outputFilePath, outputStrs, new UTF8Encoding(false));
            ScrollableMessageBox.Show($"Successfully exported assignments for {outputStrs.Count} NPCs to:\n{outputFilePath}", "Export Complete");
        }
        catch (Exception ex)
        {
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
            CheckFileExists = true
        };

        if (openFileDialog.ShowDialog() != true)
        {
            Debug.WriteLine("UpdateEasyNpcProfile: File selection cancelled.");
            return;
        }
        string filePath = openFileDialog.FileName;

        // --- Step 2: Read Existing Profile and Prepare Data ---
        List<string> originalLines;
        var lineLookup = new Dictionary<string, (int LineIndex, ModKey DefaultPlugin)>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        int lineNum = 0;

        try
        {
            originalLines = File.ReadAllLines(filePath).ToList();
            foreach (string line in originalLines)
            {
                lineNum++;
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//")) continue;

                var equalSplit = line.Split(new[] { '=' }, 2);
                if (equalSplit.Length != 2) continue;

                string formStringPart = equalSplit[0].Trim();
                var formSplit = formStringPart.Split('#');
                if (formSplit.Length != 2 || string.IsNullOrWhiteSpace(formSplit[0]) || string.IsNullOrWhiteSpace(formSplit[1])) continue;

                var pluginInfoPart = equalSplit[1].Trim();
                var pipeSplit = pluginInfoPart.Split('|');
                if (pipeSplit.Length < 1 || string.IsNullOrWhiteSpace(pipeSplit[0])) continue;

                try
                {
                    var defaultPluginKey = ModKey.FromFileName(pipeSplit[0].Trim());
                    if (!lineLookup.TryAdd(formStringPart, (lineNum - 1, defaultPluginKey)))
                    {
                        errors.Add($"Duplicate FormString '{formStringPart}' found at line {lineNum}. Only the first occurrence will be updated.");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Could not parse default plugin for FormString '{formStringPart}' at line {lineNum}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"Error reading existing profile file '{filePath}':\n{ex.Message}", "File Read Error");
            return;
        }

        // --- Step 3: Get App Selections and Prepare Updates ---
        var appNpcSelections = _settings.SelectedAppearanceMods.ToList();
        if (!appNpcSelections.Any())
        {
            ScrollableMessageBox.Show("No NPC appearance assignments are currently selected in the application. Nothing to update.", "Update Empty");
            return;
        }

        var updatedLines = new List<string>(originalLines);
        var processedFormStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedLines = new List<string>();
        var skippedMissingNpcs = new List<string>();
        var lookupErrors = new List<string>();
        var excludedDefaultPlugins = new HashSet<ModKey>(_lazySettingsVM.Value.ExclusionSelectorViewModel.SaveToModel());

        // --- Step 4: Iterate Through App Selections and Update/Prepare Additions ---
        foreach (var kvp in appNpcSelections)
        {
            var npcFormKey = kvp.Key;
            var selectedAppearanceModName = kvp.Value;

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

            if (!TryGetAppearancePlugin(npcFormKey, selectedAppearanceModName, out var appearancePluginKey, out _, out var appearanceError))
            {
                lookupErrors.Add(appearanceError!);
                continue;
            }

            // --- Find existing line or handle missing ---
            if (lineLookup.TryGetValue(formString, out var existingInfo))
            {
                // NPC exists in the file: preserve its default plugin
                var defaultPluginKey = existingInfo.DefaultPlugin;
                string newLineContent = $"{defaultPluginKey.FileName}|{appearancePluginKey!.Value.FileName}|";
                string fullNewLine = $"{formString}={newLineContent}";

                if (updatedLines[existingInfo.LineIndex] != fullNewLine)
                {
                    updatedLines[existingInfo.LineIndex] = fullNewLine;
                }
                processedFormStrings.Add(formString);
            }
            else
            {
                // NPC not found in file: add it if requested
                if (addMissingNPCs)
                {
                    // Determine a new default plugin since none exists in the file
                    if (!TryGetDefaultPlugin(npcFormKey, excludedDefaultPlugins, out var defaultPluginKey, out var defaultError))
                    {
                        lookupErrors.Add(defaultError!);
                        continue;
                    }

                    string newLineContent = $"{defaultPluginKey.FileName}|{appearancePluginKey!.Value.FileName}|";
                    string fullNewLine = $"{formString}={newLineContent}";
                    addedLines.Add(fullNewLine);
                    processedFormStrings.Add(formString);
                }
                else
                {
                    skippedMissingNpcs.Add(formString);
                }
            }
        } // End foreach app selection

        // --- Step 5: Report Errors and Skipped NPCs ---
        errors.AddRange(lookupErrors);
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
                reportMsg.AppendLine($"Skipped {skippedMissingNpcs.Count} NPCs selected in the app because they were not found in the profile file (Add Missing NPCs was disabled):");
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

        if (addedLines.Any())
        {
            updatedLines.AddRange(addedLines);
        }

        int updatedCount = processedFormStrings.Count - addedLines.Count;
        int addedCount = addedLines.Count;

        if (updatedCount == 0 && addedCount == 0)
        {
            ScrollableMessageBox.Show("No changes were made to the profile file (assignments might already match).", "No Changes");
            return;
        }

        // --- Step 6: Confirm Save Location ---
        var saveFileDialog = new SaveFileDialog
        {
            Filter = "EasyNPC Profile (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Save Updated EasyNPC Profile As...",
            FileName = Path.GetFileName(filePath),
            InitialDirectory = Path.GetDirectoryName(filePath)
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
            File.WriteAllLines(outputFilePath, updatedLines, new UTF8Encoding(false));

            string successMessage = $"Successfully updated profile file:\n{outputFilePath}\n\n";
            successMessage += $"Existing NPCs Updated: {updatedCount}\n";
            successMessage += $"Missing NPCs Added: {addedCount}";

            ScrollableMessageBox.Show(successMessage, "Update Complete");
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"Failed to save the updated profile file:\n{ex.Message}", "File Save Error");
        }
    }

    /// <summary>
    /// Attempts to find the appropriate appearance plugin ModKey for a given NPC and selected ModSetting.
    /// Handles FaceGen-only mods and plugin disambiguation.
    /// </summary>
    /// <param name="npcFormKey">The FormKey of the NPC.</param>
    /// <param name="appearanceModName">The display name of the selected appearance mod.</param>
    /// <param name="appearancePlugin">The resulting appearance plugin ModKey, if found.</param>
    /// <param name="faceGenWarning">A warning message if the mod is FaceGen-only.</param>
    /// <param name="errorMessage">An error message if the plugin cannot be determined.</param>
    /// <returns>True if a plugin was successfully determined, otherwise false.</returns>
    private bool TryGetAppearancePlugin(FormKey npcFormKey, string appearanceModName, out ModKey? appearancePlugin, out string? faceGenWarning, out string? errorMessage)
    {
        appearancePlugin = null;
        errorMessage = null;
        faceGenWarning = null;
        string formString = $"{npcFormKey.ModKey.FileName}#{npcFormKey.IDString()}";

        var appearanceMod = _lazyModListVM.Value.AllModSettings.FirstOrDefault(mod => mod.DisplayName == appearanceModName);
        if (appearanceMod == null)
        {
            errorMessage = $"NPC {formString}: Could not find Mod Setting entry for assigned appearance '{appearanceModName}'.";
            return false;
        }

        if (appearanceMod.IsFaceGenOnlyEntry)
        {
            appearancePlugin = npcFormKey.ModKey;
            faceGenWarning = $"NPC {formString} from {appearanceModName}";
            return true;
        }

        if (appearanceMod.AvailablePluginsForNpcs.TryGetValue(npcFormKey, out var availablePlugins))
        {
            if (availablePlugins.Count == 1)
            {
                appearancePlugin = availablePlugins.First();
                return true;
            }
            if (availablePlugins.Count == 0)
            {
                errorMessage = $"NPC {formString}: Mod Setting '{appearanceModName}' does not have a source plugin for this NPC.";
                return false;
            }
            if (appearanceMod.NpcPluginDisambiguation.TryGetValue(npcFormKey, out var specificKey))
            {
                appearancePlugin = specificKey;
                return true;
            }
            
            errorMessage = $"NPC {formString}: Source plugin is ambiguous within Mod Setting '{appearanceModName}'. Cannot export.";
            return false;
        }
        
        errorMessage = $"NPC {formString}: Mod Setting '{appearanceModName}' does not have a source plugin for this NPC.";
        return false;
    }

    /// <summary>
    /// Attempts to determine the default plugin for an NPC.
    /// It first checks for a user-specified default, then falls back to the winning override from the load order.
    /// </summary>
    /// <param name="npcFormKey">The FormKey of the NPC.</param>
    /// <param name="excludedDefaultPlugins">A collection of ModKeys to exclude when determining the winner.</param>
    /// <param name="defaultPlugin">The resulting default plugin ModKey, if found.</param>
    /// <param name="errorMessage">An error message if a default plugin cannot be determined.</param>
    /// <returns>True if a default plugin was successfully determined, otherwise false.</returns>
    private bool TryGetDefaultPlugin(FormKey npcFormKey, ICollection<ModKey> excludedDefaultPlugins, out ModKey defaultPlugin, out string? errorMessage)
    {
        defaultPlugin = default;
        errorMessage = null;
        string formString = $"{npcFormKey.ModKey.FileName}#{npcFormKey.IDString()}";

        if (_settings.EasyNpcDefaultPlugins.TryGetValue(npcFormKey, out var presetDefaultPlugin))
        {
            defaultPlugin = presetDefaultPlugin;
        }
        else
        {
            if (_environmentStateProvider.LinkCache == null)
            {
                errorMessage = $"NPC {formString}: Cannot determine default plugin because Link Cache is not available.";
                return false;
            }

            var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcFormKey);
            if (!contexts.Any())
            {
                errorMessage = $"NPC {formString}: Cannot determine default plugin because no context found in Link Cache.";
                return false;
            }

            foreach (var context in contexts)
            {
                if (!excludedDefaultPlugins.Contains(context.ModKey))
                {
                    defaultPlugin = context.ModKey;
                    break;
                }
            }
        }

        if (defaultPlugin.IsNull)
        {
            errorMessage = $"NPC {formString}: Could not determine a non-excluded Default Plugin.";
            return false;
        }

        return true;
    }
}