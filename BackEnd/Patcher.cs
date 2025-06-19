using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class Patcher : OptionalUIModule
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Settings _settings;
    private readonly Validator _validator;
    private readonly AssetHandler _assetHandler;
    private readonly RaceHandler _raceHandler;
    private readonly RecordHandler _recordHandler;
    private readonly Auxilliary _aux;
    private readonly RecordDeltaPatcher _recordDeltaPatcher;
    private readonly PluginProvider _pluginProvider;
    
    private Dictionary<string, ModSetting> _modSettingsMap;
    private string _currentRunOutputAssetPath = string.Empty;

    private bool _clearOutputDirectoryOnRun = true;

    public const string ALL_NPCS_GROUP = VM_Run.ALL_NPCS_GROUP;
    
    public Patcher(EnvironmentStateProvider environmentStateProvider, Settings settings, Validator validator, AssetHandler assetHandler, RaceHandler raceHandler, RecordHandler recordHandler, Auxilliary aux, RecordDeltaPatcher recordDeltaPatcher, PluginProvider pluginProvider)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
        _validator = validator;
        _assetHandler = assetHandler;
        _raceHandler = raceHandler;
        _recordHandler = recordHandler;
        _aux = aux;
        _recordDeltaPatcher = recordDeltaPatcher;
        _pluginProvider = pluginProvider;
    }

    public Dictionary<string, ModSetting> BuildModSettingsMap()
    {
        // --- Build Mod Settings Map ---
        _modSettingsMap = _settings.ModSettings
            .Where(ms => !string.IsNullOrWhiteSpace(ms.DisplayName))
            .GroupBy(ms => ms.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        AppendLog($"Built lookup map for {_modSettingsMap.Count} unique Mod Settings."); // Verbose only
        return _modSettingsMap;
    }
    
    public async Task RunPatchingLogic(string SelectedNpcGroup)
        {
            ResetLog();
            ResetProgress();
            UpdateProgress(0, 1, "Initializing...");
            AppendLog("Starting patch generation..."); // Verbose only
            
            _raceHandler.Reinitialize();
            _recordHandler.Reinitialize();
            _assetHandler.Initialize();
            _recordDeltaPatcher.Reinitialize();

            // --- Pre-Run Checks ---
            if (!_environmentStateProvider.EnvironmentIsValid || _environmentStateProvider.LoadOrder == null)
            {
                AppendLog("ERROR: Environment is not valid. Aborting.", true);
                ResetProgress();
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.OutputDirectory))
            {
                AppendLog("ERROR: Output Directory is not set. Aborting.", true);
                ResetProgress();
                return;
            }

            if (!_modSettingsMap.Any()) // check in the future in case this function gets called outside of VM_Run (e.g. Headless mode)
            {
                BuildModSettingsMap();
            }

            var screeningCache = _validator.GetScreeningCache();

            // --- Prepare Output Paths ---
            // (Logic for determining _currentRunOutputAssetPath remains the same)
            string baseOutputDirectory;
            bool isSpecifiedDirectory = false;
            var testSplit = _settings.OutputDirectory.Split(Path.DirectorySeparatorChar);
            if (testSplit.Length > 1 && Directory.Exists(_settings.OutputDirectory))
            {
                baseOutputDirectory = _settings.OutputDirectory;
                isSpecifiedDirectory = true;
            }
            else if (testSplit.Length == 1)
            {
                baseOutputDirectory = Path.Combine(_settings.ModsFolder, _settings.OutputDirectory);
            }
            else
            {
                AppendLog("ERROR: Could not locate directory " + _settings.OutputDirectory, true);
                ResetProgress();
                return;
            }

            _currentRunOutputAssetPath = baseOutputDirectory;
            if (_settings.AppendTimestampToOutputDirectory && !isSpecifiedDirectory)
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _currentRunOutputAssetPath = Path.Combine(baseOutputDirectory, timestamp);
            }

            AppendLog($"Using output asset directory: {_currentRunOutputAssetPath}"); // Verbose only
            try
            {
                Directory.CreateDirectory(_currentRunOutputAssetPath);
                AppendLog("Ensured output asset directory exists."); /* Verbose only */
            }
            catch (Exception ex)
            {
                AppendLog(
                    $"ERROR: Could not create output asset directory... Aborting. Error: {ExceptionLogger.GetExceptionStack(ex)}",
                    true);
                ResetProgress();
                return;
            }


            // --- Initialize Output Mod ---
            _environmentStateProvider.OutputMod = new SkyrimMod(
                ModKey.FromName(_environmentStateProvider.OutputPluginName, ModType.Plugin),
                _environmentStateProvider.SkyrimVersion);
            AppendLog($"Initialized output mod: {_environmentStateProvider.OutputPluginName}"); // Verbose only

            // --- Clear Output Asset Directory ---
            if (_clearOutputDirectoryOnRun)
            {
                /* (Logic remains the same) */
                AppendLog("Clearing output asset directory..."); // Verbose only
                try
                {
                    ClearDirectory(_currentRunOutputAssetPath);
                    AppendLog("Output asset directory cleared."); /* Verbose only */
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"ERROR: Failed to clear output asset directory: {ExceptionLogger.GetExceptionStack(ex)}. Aborting.",
                        true);
                    ResetProgress();
                    return;
                }
            }

            // --- Main Processing Loop (Using Screening Cache) ---
            AppendLog("\nProcessing Valid NPC Appearance Selections..."); // Verbose only
            int processedCount = 0;
            int skippedCount = 0; // Counts skips *within* this loop (invalid were already accounted for)

            var selectionsToProcess =
                screeningCache.Where(kv => kv.Value.SelectionIsValid).ToList(); // Only process valid ones

            if (!selectionsToProcess.Any())
            {
                AppendLog("No valid NPC selections found or remaining after screening."); // Verbose only
            }
            else
            {
                int totalToProcess = selectionsToProcess.Count;
                for (int i = 0; i < totalToProcess; i++)
                {
                    var kvp = selectionsToProcess[i];
                    var npcFormKey = kvp.Key;
                    var result = kvp.Value; // The ScreeningResult

                    // Resolve necessary components from cache
                    var winningNpcOverride =
                        result.WinningNpcOverride; // Already resolved, guaranteed non-null if in cache
                    var appearanceModSetting = result.AppearanceModSetting; // Already looked up
                    var appearanceNpcRecord = result.AppearanceModRecord; // Cached specific record, might be null
                    var appearanceModKey = result.AppearanceModKey;
                    string selectedModDisplayName =
                        appearanceModSetting?.DisplayName ?? "N/A"; // Get name from cached setting
                    string npcIdentifier =
                        $"{winningNpcOverride.Name?.String ?? winningNpcOverride.EditorID ?? npcFormKey.ToString()} ({npcFormKey})";
                    var mergeInDependencyRecords = appearanceModSetting?.MergeInDependencyRecords ?? false;
                    var recordOverrideHandlingMode = appearanceModSetting?.ModRecordOverrideHandlingMode ??
                                                     _settings.DefaultRecordOverrideHandlingMode;
                    List<IAssetLinkGetter> assetLinks = new();

                    // Apply Group Filter (still needed)
                    if (ShouldSkipNpc(winningNpcOverride, SelectedNpcGroup))
                    {
                        AppendLog($"  Skipping {npcIdentifier} (Group Filter)..."); // Verbose only
                        skippedCount++;
                        UpdateProgress(i + 1, totalToProcess, $"Skipped {npcIdentifier} (Group Filter)");
                        await Task.Delay(1);
                        continue;
                    }

                    UpdateProgress(i + 1, totalToProcess,
                        $"Processing {winningNpcOverride.EditorID ?? npcFormKey.ToString()}");
                    AppendLog(
                        $"- Processing: {npcIdentifier} -> Selected Mod: '{selectedModDisplayName}'"); // Verbose only

                    Npc? patchNpc = null; // The NPC record to be placed in the patch

                    // --- *** Apply Logic Based on Screening Result (Requirement 1) *** ---
                    if (appearanceNpcRecord != null) // Scenario: Plugin override exists
                    {
                        AppendLog("    Source: Plugin Record Override"); // Verbose only

                        switch (_settings.PatchingMode)
                        {
                            case PatchingMode.EasyNPC_Like:
                                AppendLog(
                                    $"      Mode: EasyNPC-Like. Patching winning override ({winningNpcOverride.FormKey.ModKey.FileName}) with appearance from {appearanceModKey?.FileName ?? "N/A"}."); // Verbose only
                                patchNpc =
                                    _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(winningNpcOverride); // copy in the winning override and patch its appearance
                                CopyAppearanceData(appearanceNpcRecord, patchNpc, appearanceModSetting, appearanceModKey.Value, npcIdentifier, mergeInDependencyRecords);
                                
                                switch (recordOverrideHandlingMode)
                                {
                                    case RecordOverrideHandlingMode.Ignore:
                                        break;
                                    
                                    case RecordOverrideHandlingMode.Include:
                                        var dependencyContexts = _recordHandler.DeepGetOverriddenDependencyRecords(patchNpc,
                                            appearanceModSetting.CorrespondingModKeys); // To Do: Skip the Race formlink since that's handled separately
                                        foreach (var ctx in dependencyContexts)
                                        {
                                            bool wasDeltaPatched = false;
                                            if (_recordHandler.TryGetRecordFromMod(ctx.Record.ToLink(), ctx.Record.FormKey.ModKey, RecordHandler.RecordLookupFallBack.None, out var baseRecord) && baseRecord != null)
                                            {
                                                var recordDifs = _recordDeltaPatcher.GetPropertyDiffs(ctx.Record, baseRecord);
                                                var getterType = Auxilliary.GetRecordGetterType(ctx.Record);
                                                if (recordDifs.Any() && _environmentStateProvider.LinkCache.TryResolve(ctx.Record.FormKey, getterType, out var winningGetter) && winningGetter != null)
                                                {
                                                    var winningRecord = Auxilliary.GetOrAddGenericRecordAsOverride(winningGetter, _environmentStateProvider.OutputMod);
                                                    _recordDeltaPatcher.ApplyPropertyDiffs(winningRecord, recordDifs);
                                                }
                                            }
                                            
                                            if (!wasDeltaPatched)
                                            {
                                                ctx.GetOrAddAsOverride(_environmentStateProvider.OutputMod); // fallback in case parent record isn't in the load order (will cause a missing master, but that's the user's problem)
                                            }
                                            
                                            var assets = _aux.ShallowGetAssetLinks(ctx.Record).Where(x => !assetLinks.Contains(x));
                                            assetLinks.AddRange(assets);
                                        }
                                        break;
                                    
                                    case RecordOverrideHandlingMode.IncludeAsNew:
                                        var mergedInRecords =
                                            _recordHandler.DuplicateInOverrideRecords(appearanceNpcRecord, patchNpc,
                                                appearanceModSetting.CorrespondingModKeys);
                                        foreach (var rec in mergedInRecords)
                                        {
                                            var assets = _aux.ShallowGetAssetLinks(rec).Where(x => !assetLinks.Contains(x));
                                            assetLinks.AddRange(assets);
                                        }
                                        
                                        break;
                                }
                                
                                                    
                                // deep copy in all dependencies
                                if (mergeInDependencyRecords)
                                {
                                    _recordHandler.DuplicateFromOnlyReferencedGetters(
                                        _environmentStateProvider.OutputMod, patchNpc,
                                        appearanceModSetting.CorrespondingModKeys,
                                        appearanceModKey.Value, true, RecordHandler.RecordLookupFallBack.Winner);
                                }
                                break;
                            case PatchingMode.Default:
                            default:
                                AppendLog(
                                    $"      Mode: Default. Forwarding record from source plugin ({appearanceModKey?.FileName ?? "N/A"})."); // Verbose only
                                patchNpc = _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(appearanceNpcRecord); // copy in the NPC as it appears in the source mod

                                switch (recordOverrideHandlingMode)
                                {
                                    case RecordOverrideHandlingMode.Ignore:
                                        break;
                                    
                                    case RecordOverrideHandlingMode.Include:
                                        var dependencyRecords = _recordHandler.DeepGetOverriddenDependencyRecords(patchNpc,
                                            appearanceModSetting.CorrespondingModKeys);
                                        foreach (var ctx in dependencyRecords)
                                        {
                                            ctx.GetOrAddAsOverride(_environmentStateProvider.OutputMod);
                                            var assets = _aux.ShallowGetAssetLinks(ctx.Record).Where(x => !assetLinks.Contains(x));
                                            assetLinks.AddRange(assets);
                                        }
                                        break;
                                    
                                    case RecordOverrideHandlingMode.IncludeAsNew:
                                        var mergedInRecords =
                                            _recordHandler.DuplicateInOverrideRecords(appearanceNpcRecord, patchNpc,
                                                appearanceModSetting.CorrespondingModKeys);
                                        foreach (var rec in mergedInRecords)
                                        {
                                            var assets = _aux.ShallowGetAssetLinks(rec).Where(x => !assetLinks.Contains(x));
                                            assetLinks.AddRange(assets);
                                        }
                                        break;
                                }
                                
                                                    
                                // deep copy in all dependencies
                                if (mergeInDependencyRecords)
                                {
                                    _recordHandler.DuplicateFromOnlyReferencedGetters(
                                        _environmentStateProvider.OutputMod, patchNpc,
                                        appearanceModSetting.CorrespondingModKeys,
                                        appearanceModKey.Value, true, RecordHandler.RecordLookupFallBack.Origin);
                                }
                                break;
                        }
                    }
                    else
                    {
                        // This case should have been filtered by the screening result, but handle defensively
                        AppendLog(
                            $"ERROR: UNEXPECTED: Selection for {npcIdentifier} was marked valid but has neither plugin record nor FaceGen. Skipping.",
                            true);
                        skippedCount++;
                        await Task.Delay(1);
                        continue;
                    }
                    // --- *** End Scenario Logic *** 

                    // --- Copy Assets ---
                    if (patchNpc != null && appearanceModSetting != null) // Ensure we have a patch record and mod settings
                    {
                        await _assetHandler.CopyNpcAssets(appearanceNpcRecord, appearanceModSetting, appearanceModKey.Value, _currentRunOutputAssetPath, _settings.ModsFolder);
                        await _assetHandler.CopyAssetLinkFiles(assetLinks, appearanceModSetting,
                            _currentRunOutputAssetPath);
                    }
                    else
                    {
                        AppendLog(
                            $"ERROR: Could not proceed with asset copying due to missing patch record or mod setting for {npcIdentifier}.",
                            true);
                        skippedCount++;
                        await Task.Delay(1);
                        continue;
                    }

                    // Handle race deep-copy if needed
                    //_raceHandler.ProcessNpcRace(patchNpc, appearanceNpcRecord, winningNpcOverride, appearanceModKey.Value, appearanceModSetting);

                    processedCount++;
                    await Task.Delay(5);
                } // End For Loop
            } // End else (selectionsToProcess.Any())

            //await _raceHandler.ApplyRaceChanges(_currentRunOutputAssetPath);

            UpdateProgress(processedCount + skippedCount, processedCount + skippedCount, "Finalizing...");

            // --- Final Steps (Save Output Mod) ---
            if (processedCount > 0)
            {
                AppendLog($"\nProcessed {processedCount} NPC(s).", false, true); // Force log
                if (skippedCount > 0) AppendLog($"{skippedCount} NPC(s) were skipped.", false, true); // Force log
                string outputPluginPath = Path.Combine(_currentRunOutputAssetPath,
                    _environmentStateProvider.OutputPluginFileName);
                AppendLog($"Attempting to save output mod to: {outputPluginPath}", true); // Force log
                try
                {
                    _environmentStateProvider.OutputMod.WriteToBinary(outputPluginPath);
                    AppendLog($"Output mod saved successfully.", false, true); // Force log
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"FATAL SAVE ERROR: Could not write output plugin: {ExceptionLogger.GetExceptionStack(ex)}",
                        true); // isError true, will log
                    AppendLog($"ERROR: Output mod NOT saved.", true); // isError true, will log
                    ResetProgress();
                    return;
                }
            }
            else
            {
                AppendLog("\nNo NPC appearances processed or dependencies duplicated.", false, true); // Force log
                if (skippedCount > 0) AppendLog($"{skippedCount} NPC(s) were skipped.", false, true); // Force log
                AppendLog("Output mod not saved as no changes were made.", false, true); // Force log
            }

            AppendLog("\nPatch generation process completed.", false, true); // Force log
            UpdateProgress(processedCount + skippedCount, processedCount + skippedCount, "Finished.");
        } // End RunPatchingLogic

        // --- Helper Methods (Partially Revised) ---

        private void ClearDirectory(string path)
        {
            // (Implementation remains the same as before)
            DirectoryInfo di = new DirectoryInfo(path);
            if (!di.Exists) return;

            foreach (FileInfo file in di.EnumerateFiles()) file.Delete();
            foreach (DirectoryInfo dir in di.EnumerateDirectories())
            {
                string dirNameLower = dir.Name.ToLowerInvariant();
                if (dirNameLower == "meshes" || dirNameLower == "textures" || dirNameLower == "facegendata" ||
                    dirNameLower == "actors")
                {
                    dir.Delete(true);
                }
                else
                {
                    AppendLog($"  Skipping deletion of non-asset directory: {dir.Name}");
                } // Verbose only
            }
        }


        /// <summary>
        /// Copies appearance-related fields from sourceNpc to targetNpc.
        /// Used only in EasyNPC-Like mode.
        /// </summary>
        private void CopyAppearanceData(INpcGetter sourceNpc, Npc targetNpc, ModSetting appearanceModSetting, ModKey sourceNpcContextModKey, string npcIdentifier, bool mergeInDependencyRecords)
        {
            // Copy non-formlinks
            targetNpc.FaceMorph = sourceNpc.FaceMorph?.DeepCopy();
            targetNpc.FaceParts = sourceNpc.FaceParts?.DeepCopy();
            targetNpc.Height = sourceNpc.Height;
            targetNpc.Weight = sourceNpc.Weight;
            targetNpc.TextureLighting = sourceNpc.TextureLighting;
            targetNpc.TintLayers.Clear();
            targetNpc.TintLayers.AddRange(sourceNpc.TintLayers?.Select(t => t.DeepCopy()) ??
                                          Enumerable.Empty<TintLayer>());
            
            // Merge in formlinks
            if (mergeInDependencyRecords)
            {
                try
                {
                    _recordHandler.DuplicateInFormLink(targetNpc.WornArmor, sourceNpc.WornArmor,
                        _environmentStateProvider.OutputMod, appearanceModSetting.CorrespondingModKeys,
                        sourceNpcContextModKey, RecordHandler.RecordLookupFallBack.Origin);

                    _recordHandler.DuplicateInFormLink(targetNpc.HeadTexture, sourceNpc.HeadTexture,
                        _environmentStateProvider.OutputMod, appearanceModSetting.CorrespondingModKeys,
                        sourceNpcContextModKey, RecordHandler.RecordLookupFallBack.Origin);

                    _recordHandler.DuplicateInFormLink(targetNpc.HairColor, sourceNpc.HairColor,
                        _environmentStateProvider.OutputMod, appearanceModSetting.CorrespondingModKeys,
                        sourceNpcContextModKey, RecordHandler.RecordLookupFallBack.Origin);

                    targetNpc.HeadParts.Clear();
                    foreach (var hp in sourceNpc.HeadParts.Where(x => !x.IsNull))
                    {
                        var targetHp = new FormLink<IHeadPartGetter>();
                        _recordHandler.DuplicateInFormLink(targetHp, hp,
                            _environmentStateProvider.OutputMod, appearanceModSetting.CorrespondingModKeys,
                            sourceNpcContextModKey, RecordHandler.RecordLookupFallBack.Origin);
                        targetNpc.HeadParts.Add(targetHp);
                    }


                    AppendLog(
                        $"    Completed dependency processing for {npcIdentifier}."); // Verbose only
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"  ERROR duplicating dependencies for {npcIdentifier}: {ExceptionLogger.GetExceptionStack(ex)}",
                        true);
                    // Continue processing other plugins
                }
            }

            AppendLog(
                $"      Copied appearance fields from {sourceNpc.FormKey.ModKey.FileName} to {targetNpc.FormKey} in patch."); // Verbose only

            if (NPCisTemplated(targetNpc) && !NPCisTemplated(sourceNpc))
            {
                AppendLog(
                    $"      Removing template flag from {targetNpc.FormKey} in patch."); // Verbose only
                targetNpc.Configuration.TemplateFlags 
                    &= ~NpcConfiguration.TemplateFlag.Traits;
            }
        }

        private bool NPCisTemplated(INpcGetter? npc)
        {
            if (npc == null) return false;
            return !npc.Template.IsNull &&
                   npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits);
        }
    
    private bool ShouldSkipNpc(INpcGetter npc, string selectedGroup)
    {
        // (Implementation remains the same as before)
        if (selectedGroup == ALL_NPCS_GROUP) return false;

        if (_settings.NpcGroupAssignments != null &&
            _settings.NpcGroupAssignments.TryGetValue(npc.FormKey, out var assignedGroups) &&
            assignedGroups != null &&
            assignedGroups.Contains(selectedGroup, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
    
    
}