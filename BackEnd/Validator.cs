﻿using System.IO;
using System.Text;
using System.Windows;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class Validator : OptionalUIModule
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Settings _settings;
    private readonly BsaHandler _bsaHandler;
    private readonly PluginProvider _pluginProvider;
    private readonly RecordHandler _recordHandler;
    private readonly AssetHandler _assetHandler;
    
    private Action<string, bool, bool>? _appendLog;
    private Action<int, int, string>? _updateProgress;
    private Action? _resetProgress;

    private Dictionary<FormKey, ScreeningResult> _screeningCache = new();
    
    public Validator(EnvironmentStateProvider environmentStateProvider, Settings settings, BsaHandler bsaHandler, PluginProvider pluginProvider, RecordHandler recordHandler, AssetHandler assetHandler)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
        _bsaHandler = bsaHandler;
        _pluginProvider = pluginProvider;
        _recordHandler = recordHandler;
        _assetHandler = assetHandler;
    }

    public Dictionary<FormKey, ScreeningResult> GetScreeningCache()
    {
        return _screeningCache;
    }
    
    public async Task<bool>
        ScreenSelectionsAsync(Dictionary<string, ModSetting> modSettingsMap, CancellationToken ct)
    {
        // Logging the message, if you have an appendLog delegate
        AppendLog("\nStarting pre-run screening of NPC selections...", false, false); // Verbose only
            _screeningCache = new Dictionary<FormKey, ScreeningResult>();
            var invalidSelections = new List<string>(); // List of strings for user message
            var selections = _settings.SelectedAppearanceMods;

            if (selections == null || !selections.Any())
            {
                AppendLog("No selections to screen."); // Verbose only
                return false;
            }

            // Convert dictionary to a list to allow access by index
            var selectionsList = selections.ToList(); 
            int totalToScreen = selectionsList.Count;

            // Use a for loop to get a reliable index 'i'
            for (int i = 0; i < totalToScreen; i++) 
            {
                ct.ThrowIfCancellationRequested();

                var kvp = selectionsList[i]; // Get the current item
                var npcFormKey = kvp.Key;
                var selectedModDisplayName = kvp.Value;
                string npcIdentifier = npcFormKey.ToString();

                // ======================= THROTTLING LOGIC =======================
                // Determine IF we should update the UI in this iteration.
                bool shouldUpdateUI = (i % 10 == 0) || (i == totalToScreen - 1);
                // ================================================================

                // 1. Resolve winning override (needed for context and potentially EasyNPC mode base)
                if (!_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey, out var winningNpcOverride))
                {
                    AppendLog(
                        $"  SCREENING WARNING: Could not resolve base/winning NPC {npcFormKey}. Skipping screening for this NPC."); // Verbose only (Warning)
                    // Don't add to cache or invalid list if the base NPC itself is unresolvable
                    await Task.Delay(1, ct); // Throttle slightly
                    continue;
                }

                npcIdentifier =
                    $"{winningNpcOverride.Name?.String ?? winningNpcOverride.EditorID ?? npcFormKey.ToString()} ({npcFormKey})"; // Better identifier
                
                // ======================= APPLY THROTTLING =======================
                if (shouldUpdateUI)
                {
                    // Use the new, more informative hybrid message
                    UpdateProgress(i + 1, totalToScreen, $"({i + 1}/{totalToScreen}) Screening: {npcIdentifier}");
                }
                // ================================================================


                // 2. Find the ModSetting for the selection
                if (!modSettingsMap.TryGetValue(selectedModDisplayName, out var appearanceModSetting))
                {
                    AppendLog(
                        $"  SCREENING ERROR: Cannot find Mod Setting '{selectedModDisplayName}' for NPC {npcIdentifier}. Invalid selection.",
                        true);
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Mod Setting not found)");
                    // Add a placeholder invalid result to cache? Or just rely on the invalidSelections list? Let's rely on the list for now.
                    await Task.Delay(1, ct);
                    continue;
                }

                // 3. Check for Plugin Record
                bool correspondingRecordFound = false;
                INpcGetter appearanceModRecord;
                ModKey? appearanceModKey;

                List<string> sourcePluginNames = new List<string>(); // For logging ambiguity

                var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcFormKey)
                    .ToList();
                
                var baseNpcRecord = contexts.FirstOrDefault(x => x.ModKey.Equals(npcFormKey.ModKey))?.Record;
                
                if (baseNpcRecord == null)
                {
                    AppendLog(
                        $"  SCREENING ERROR: Cannot find the base record for NPC {npcIdentifier} in the current load order. Invalid selection.",
                        true);
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Base record not found in current load order)");
                    // Add a placeholder invalid result to cache? Or just rely on the invalidSelections list? Let's rely on the list for now.
                    await Task.Delay(1, ct);
                    continue;
                }
                
                appearanceModRecord = baseNpcRecord; // placeholder to initialize
                appearanceModKey = npcFormKey.ModKey; // placeholder to initialize
                
                var availableModKeysForThisNpcInSelectedAppearanceMod = new List<ModKey>();
                foreach (var mk in appearanceModSetting.CorrespondingModKeys)
                {
                    if (_pluginProvider.TryGetPlugin(mk, appearanceModSetting.DisplayName, out _))
                    {
                        availableModKeysForThisNpcInSelectedAppearanceMod.Add(mk);
                    }
                }

                if (!availableModKeysForThisNpcInSelectedAppearanceMod.Any() && appearanceModSetting.CorrespondingModKeys.Any()) // If there are no appearanceModSetting.CorrespondingModKeys, the baseNpcRecord is used
                {
                    AppendLog(
                        $"  SCREENING ERROR: Cannot find any plugins in Mod Setting '{selectedModDisplayName}' for NPC {npcIdentifier}. Invalid selection.",
                        true);
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Mod Setting doesn't contain any plugin for this NPC)");
                    // Add a placeholder invalid result to cache? Or just rely on the invalidSelections list? Let's rely on the list for now.
                    await Task.Delay(1, ct);
                    continue;
                }
                
                if (appearanceModSetting.NpcPluginDisambiguation.TryGetValue(npcFormKey, out ModKey disambiguation))
                {
                    if (availableModKeysForThisNpcInSelectedAppearanceMod.Contains(disambiguation) && _recordHandler.TryGetRecordGetterFromMod(npcFormKey.ToLink<INpcGetter>(), disambiguation, RecordHandler.RecordLookupFallBack.None, out var record) && record != null)
                    {
                        appearanceModRecord = record as INpcGetter;
                        appearanceModKey = disambiguation;
                        correspondingRecordFound = true;
                        AppendLog(
                            $"    Screening: Found assigned plugin record override in {appearanceModRecord.FormKey.ModKey.FileName}. Using this as source."); // Verbose only
                    }
                    else
                    {
                        AppendLog(
                            $"    Screening: Source plugin is set to {disambiguation.FileName} but this plugin is not in the load order or doesn't contain this NPC. Falling back to first available plugin"); // Verbose only
                    }
                }

                if (!correspondingRecordFound)
                {
                    if (!appearanceModSetting.CorrespondingModKeys.Any())
                    {
                        AppendLog(
                            "    Screening: Selected appearance mod doesn't have any associated ModKeys. Using the base record as the appearance source."); // Verbose only
                        appearanceModRecord = baseNpcRecord;
                        appearanceModKey = baseNpcRecord.FormKey.ModKey;
                        correspondingRecordFound = true;
                    }
                    else if (availableModKeysForThisNpcInSelectedAppearanceMod.Any())
                    {
                        foreach (var candidate in availableModKeysForThisNpcInSelectedAppearanceMod)
                        {
                            if (availableModKeysForThisNpcInSelectedAppearanceMod.Contains(candidate) &&
                                _recordHandler.TryGetRecordGetterFromMod(npcFormKey.ToLink<INpcGetter>(), candidate, RecordHandler.RecordLookupFallBack.None, out var record) && record != null)
                            {
                                appearanceModRecord = record as INpcGetter;
                                appearanceModKey = candidate;
                                AppendLog(
                                    $"    Screening: Selected plugin record override in {appearanceModRecord.FormKey.ModKey.FileName}. Using this as source."); // Verbose only
                                correspondingRecordFound = true;
                                break;
                            }
                        }
                    }
                }
                
                if (!correspondingRecordFound)
                {
                    AppendLog(
                        $"  SCREENING ERROR: Cannot find any plugins in Mod Setting '{selectedModDisplayName}' for NPC {npcIdentifier}. Invalid selection.",
                        true);
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Mod Setting doesn't contain any plugin for this NPC)");
                    // Add a placeholder invalid result to cache? Or just rely on the invalidSelections list? Let's rely on the list for now.
                    await Task.Delay(1, ct);
                    continue;
                }

                // 4. Check for FaceGen Assets
                var assetSourceDirs = appearanceModSetting.CorrespondingFolderPaths ?? new List<string>();
                bool hasFaceGen = false;
                if (assetSourceDirs.Any())
                {
                    // Use the existing FaceGenExists but pass the *effective* plugin key (could be null if only assets defined)
                    // Pass the original NPC FormKey for path generation
                    // Use the *specific* key found above, or the original NPC's key if no override was found
                    ModKey keyForFaceGenCheck = npcFormKey.ModKey;
                    hasFaceGen = FaceGenExists(npcFormKey, keyForFaceGenCheck, appearanceModSetting);
                    AppendLog(
                        $"    Screening: FaceGen assets found in source directories: {hasFaceGen}."); // Verbose only
                }
                else
                {
                    AppendLog(
                        $"    Screening: Mod Setting '{selectedModDisplayName}' has no asset source directories defined."); // Verbose only
                }


                // 5. Determine Validity and Cache Result
                _screeningCache[npcFormKey] = new ScreeningResult(
                    correspondingRecordFound,
                    hasFaceGen,
                    appearanceModRecord,
                    appearanceModKey,
                    winningNpcOverride, // Cache the winning override
                    appearanceModSetting
                );

                if (!correspondingRecordFound)
                {
                    string reason = "No plugin override found";

                    if (!hasFaceGen)
                    {
                        if (!string.IsNullOrEmpty(reason)) reason += " and ";
                        reason += "No FaceGen assets found";
                    }

                    AppendLog(
                        $"ERROR: SCREENING INVALID: NPC {npcIdentifier} -> Selected Mod '{selectedModDisplayName}'. Reason: {reason}.",
                        true);
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' ({reason})");
                }

                await Task.Delay(1, ct); // Throttle loop slightly
            } // End foreach selection

            UpdateProgress(totalToScreen, totalToScreen, "Screening Complete.");
            AppendLog(
                $"Screening finished. Found {invalidSelections.Count} invalid selections."); // Verbose only (summarizes errors previously logged)
            
            ct.ThrowIfCancellationRequested(); // Check again before showing a dialog
            if (invalidSelections.Any())
            {
                var message =
                    new StringBuilder(
                        $"Found {invalidSelections.Count} NPC selection(s) where the chosen appearance mod provides neither a plugin record override nor FaceGen assets:\n\n");
                message.AppendLine(string.Join("\n", invalidSelections.Take(15))); // Show first 15
                if (invalidSelections.Count > 15) message.AppendLine("[...]");
                message.AppendLine("\nThese selections will be skipped. Continue with the valid selections?");

                // Show confirmation dialog - Needs to run on UI thread if called from background
                bool continuePatching = false;
                // Use Dispatcher.Invoke to run synchronously on the UI thread and get the result back
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[VM_Run.RunPatchingLogic] Showing MessageBox on Thread: {System.Threading.Thread.CurrentThread.ManagedThreadId}");
                    continuePatching = ScrollableMessageBox.Confirm(message.ToString(), "Invalid Selections Found");
                    System.Diagnostics.Debug.WriteLine(
                        $"[VM_Run.RunPatchingLogic] continuePatching: {continuePatching}");
                });

                // Execution of RunPatchingLogic will pause here until the MessageBox is closed
                // because Dispatcher.Invoke is synchronous.

                if (!continuePatching)
                {
                    AppendLog("Patching cancelled by user due to invalid selections."); // Verbose only
                    ResetProgress();
                    return false; // Exit RunPatchingLogic
                }

                AppendLog("Proceeding with valid selections, skipping invalid ones..."); // Verbose only
            }
            // --- *** End Screening *** ---

            return true;
    }
    
    /// <summary>
    /// Checks if FaceGen files exist loose or in BSAs within the provided source directories.
    /// **Revised for Clarification 2.**
    /// </summary>
    private bool FaceGenExists(FormKey npcFormKey, ModKey appearancePluginKey, ModSetting modSetting)
    {
        var (faceMeshPath, faceTexPath) = Auxilliary.GetFaceGenSubPathStrings(npcFormKey);
        bool nifFound = false;
        bool ddsFound = false;

        // Check Loose Files (Iterate all source dirs)
        foreach (var sourceDir in modSetting.CorrespondingFolderPaths)
        {
            if (!nifFound && _assetHandler.FileExists(Path.Combine(sourceDir, "meshes", faceMeshPath), modSetting.DisplayName, sourceDir)) nifFound = true;
            if (!ddsFound && _assetHandler.FileExists(Path.Combine(sourceDir, "textures", faceTexPath), modSetting.DisplayName, sourceDir)) ddsFound = true;
            if (nifFound && ddsFound) break;
        }

        // Check BSAs 
        if (!nifFound || !ddsFound)
        {
            string bsaMeshPath = Path.Combine("meshes", faceMeshPath);
            string bsaTexPath = Path.Combine("textures", faceTexPath);

            if (!nifFound && _bsaHandler.FileExists(bsaMeshPath, appearancePluginKey, out _))
            {
                nifFound = true;
            }

            if (!ddsFound && _bsaHandler.FileExists(bsaTexPath, appearancePluginKey, out _))
            {
                ddsFound = true;
            }
        }
        /*
        if (!nifFound || !ddsFound)
        {
            // Iterate source directories *backwards*
            for (int i = modSetting.CorrespondingFolderPaths.Count - 1; i >= 0; i--)
            {
                var sourceDir = modSetting.CorrespondingFolderPaths[i];
                HashSet<IArchiveReader> readers = _bsaHandler.OpenBsaArchiveReaders(sourceDir, appearancePluginKey);
                if (readers.Any())
                {
                    string bsaMeshPath = Path.Combine("meshes", faceMeshPath).Replace('/', '\\');
                    string bsaTexPath = Path.Combine("textures", faceTexPath).Replace('/', '\\');

                    // Check *this specific set* of readers using HaveFile
                    // *** Use HaveFile here (out var _ discards the file) ***
                    if (!nifFound && _bsaHandler.HaveFile(bsaMeshPath, readers, out _)) nifFound = true;
                    if (!ddsFound && _bsaHandler.HaveFile(bsaTexPath, readers, out _)) ddsFound = true;

                    if (nifFound && ddsFound) break; // Found both
                }
            }
        }*/

        return nifFound || ddsFound;
    }
}