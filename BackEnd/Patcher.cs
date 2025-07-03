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
    private readonly RecordHandler _recordHandler;
    private readonly Auxilliary _aux;
    private readonly RecordDeltaPatcher _recordDeltaPatcher;
    private readonly PluginProvider _pluginProvider;
    private readonly BsaHandler _bsaHandler;
    
    private Dictionary<string, ModSetting> _modSettingsMap;
    private string _currentRunOutputAssetPath = string.Empty;

    private bool _clearOutputDirectoryOnRun = true;

    public const string ALL_NPCS_GROUP = VM_Run.ALL_NPCS_GROUP;
    
    public Patcher(EnvironmentStateProvider environmentStateProvider, Settings settings, Validator validator, AssetHandler assetHandler, RecordHandler recordHandler, Auxilliary aux, RecordDeltaPatcher recordDeltaPatcher, PluginProvider pluginProvider, BsaHandler bsaHandler)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
        _validator = validator;
        _assetHandler = assetHandler;
        _recordHandler = recordHandler;
        _aux = aux;
        _recordDeltaPatcher = recordDeltaPatcher;
        _pluginProvider = pluginProvider;
        _bsaHandler = bsaHandler;
    }

    public async Task PreInitializationLogicAsync() 
    {
        AppendLog("Pre-Indexing loose file paths...", false, true);
        await _assetHandler.PopulateExistingFilePathsAsync(_settings.ModSettings); 
        AppendLog("Finished Pre-Indexing loose file paths.", false, true);

        AppendLog("Pre-Indexing BSA file paths...", false, true);
        await _bsaHandler.PopulateBsaContentPathsAsync(_settings.ModSettings, _environmentStateProvider.SkyrimVersion.ToGameRelease());
        AppendLog("Finished Pre-Indexing BSA file paths.", false, true);
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
    
    public async Task PreloadAppearanceModPluginsAsync(Dictionary<string, ModSetting> modSettingsMap)
    {
        AppendLog("Pre-loading all required appearance mod plugins...", false, true);

        // 1. Get all unique ModSetting objects that are actually being used.
        var usedModSettingNames = _settings.SelectedAppearanceMods.Values.Distinct().ToHashSet();
    
        // 2. From those settings, get all unique ModKeys that need to be loaded.
        var requiredKeys = usedModSettingNames
            .Select(name => modSettingsMap.TryGetValue(name, out var setting) ? setting : null)
            .Where(setting => setting != null)
            .SelectMany(setting => setting!.CorrespondingModKeys)
            .Distinct()
            .ToList();

        if (!requiredKeys.Any())
        {
            AppendLog("No appearance mod plugins to pre-load.", false, true);
            return;
        }

        int loadedCount = 0;
        int failedCount = 0;

        // 3. Offload the blocking I/O to a background thread.
        await Task.Run(() =>
        {
            foreach (var key in requiredKeys)
            {
                // The act of calling TryGetPlugin will load and cache the plugin if it's not already.
                // We pass the settings' ModsFolder as a fallback path.
                if (_pluginProvider.TryGetPlugin(key, _settings.ModsFolder, out _))
                {
                    loadedCount++;
                }
                else
                {
                    failedCount++;
                    // This logging will now happen on the background thread, which is fine.
                    // The UI will update when it gets a chance.
                    System.Diagnostics.Debug.WriteLine($"[Pre-loader] Failed to load plugin: {key.FileName}");
                }
            }
        });

        AppendLog($"Finished pre-loading. {loadedCount} plugins cached. {failedCount} could not be found.", false, true);
    }
    
    public async Task RunPatchingLogic(string SelectedNpcGroup, CancellationToken ct)
        {
            ResetLog();
            ResetProgress();
            UpdateProgress(0, 1, "Initializing...");
            AppendLog("Starting patch generation..."); // Verbose only
            
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
            
            // ====================== TRACER SETUP ======================
            // Configure the tracer to stop after sampling 50 "Mod-Added" NPCs.
            ContextualPerformanceTracer.ResetAndStartSampling("Mod-Added", 250);
            bool profilingReportGenerated = false;
            // ==========================================================

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
                    ct.ThrowIfCancellationRequested();
                    
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
                    
                    // Set the context for this iteration. This will also count the sample if it's "Mod-Added".
                    using var _context = ContextualPerformanceTracer.BeginContext(winningNpcOverride.FormKey.ModKey);

                    // This will automatically do nothing after the sample limit is reached.
                    using var _ = ContextualPerformanceTracer.Trace("Patcher.MainLoopIteration");
                    
                    // ======================= THROTTLING LOGIC =======================
                    // Determine IF we should update the UI in this iteration.
                    // Update every 50 items, on the first item (i=0), or on the very last item.
                    bool shouldUpdateUI = (i % 10 == 0) || (i == totalToProcess - 1);
                    // ================================================================

                    // Apply Group Filter (still needed)
                    if (ShouldSkipNpc(winningNpcOverride, SelectedNpcGroup))
                    {
                        AppendLog($"  Skipping {npcIdentifier} (Group Filter)..."); // Verbose only
                        skippedCount++;
                        // ======================= APPLY THROTTLING =======================
                        if (shouldUpdateUI)
                        {
                            UpdateProgress(i + 1, totalToProcess, $"({i + 1}/{totalToProcess}) Skipped: {npcIdentifier}");
                        }
                        // ================================================================
                        await Task.Delay(1, ct);
                        continue;
                    }

                    // ======================= APPLY THROTTLING =======================
                    if (shouldUpdateUI)
                    {
                        UpdateProgress(i + 1, totalToProcess, $"({i + 1}/{totalToProcess}) Processing: {winningNpcOverride.EditorID ?? npcIdentifier}");
                    }
                    // ================================================================
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
                                
                                var mergedInAppearanceRecords = CopyAppearanceData(appearanceNpcRecord, patchNpc, appearanceModSetting, appearanceModKey.Value, npcIdentifier, mergeInDependencyRecords);
                                _aux.CollectShallowAssetLinks(mergedInAppearanceRecords, assetLinks);
                                
                                // deep copy in all referenced records originating from the source plugins
                                if (mergeInDependencyRecords)
                                {
                                    List<string> mergeInExceptions = new();
                                     var mergedInRecords = _recordHandler.DuplicateFromOnlyReferencedGetters(
                                        _environmentStateProvider.OutputMod, patchNpc,
                                        appearanceModSetting.CorrespondingModKeys,
                                        appearanceModKey.Value, true, RecordHandler.RecordLookupFallBack.Winner, ref mergeInExceptions);

                                     if (mergeInExceptions.Any())
                                     {
                                         AppendLog("Exceptions occurred during dependency merge-in of " + Auxilliary.GetNpcLogString(patchNpc) + Environment.NewLine + string.Join(Environment.NewLine, mergeInExceptions));
                                     }
                                     
                                     _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                }
                                
                                // handle overriden records originating outside of the source plugins
                                switch (recordOverrideHandlingMode)
                                {
                                    case RecordOverrideHandlingMode.Ignore:
                                        break;
                                    
                                    case RecordOverrideHandlingMode.Include:
                                        var dependencyContexts = _recordHandler.DeepGetOverriddenDependencyRecords(patchNpc,
                                            appearanceModSetting.CorrespondingModKeys); // To Do: Skip the Race formlink since that's handled separately
                                        List<MajorRecord> deltaPatchedRecords = new();
                                        foreach (var ctx in dependencyContexts)
                                        {
                                            bool wasDeltaPatched = false;
                                            if (_recordHandler.TryGetRecordFromMod(ctx.Record.FormKey, ctx.Record.Type, ctx.Record.FormKey.ModKey, RecordHandler.RecordLookupFallBack.None, out var baseRecord) && baseRecord != null)
                                            {
                                                if (!_recordHandler.TryGetRecordFromMod(ctx.Record.FormKey,
                                                        ctx.Record.Type, ctx.ModKey,
                                                        RecordHandler.RecordLookupFallBack.None, out var overrideRecord) &&
                                                    baseRecord != null)
                                                {
                                                    continue;
                                                }
                                                List<RecordDeltaPatcher.PropertyDiff> recordDifs = _recordDeltaPatcher.GetPropertyDiffs(overrideRecord, baseRecord);
                                                IMajorRecordGetter? winningGetter = null;
                                                if (recordDifs is not null && recordDifs.Any() && _environmentStateProvider.LinkCache.TryResolve(ctx.Record.FormKey, ctx.Record.Type, out winningGetter) && winningGetter != null)
                                                {
                                                    if (Auxilliary.TryGetOrAddGenericRecordAsOverride(winningGetter,
                                                            _environmentStateProvider.OutputMod,
                                                            out var winningRecord, out string exceptionString) && winningRecord != null)
                                                    {
                                                        _recordDeltaPatcher.ApplyPropertyDiffs(winningRecord, recordDifs);
                                                        deltaPatchedRecords.Add(winningRecord);
                                                    }
                                                    else
                                                    {
                                                        AppendLog(Auxilliary.GetNpcLogString(patchNpc) +  ": Could not merge in winning override for " + Auxilliary.GetLogString(winningGetter) + ": " + exceptionString, true, true);
                                                    }
                                                }
                                            }
                                            
                                            if (!wasDeltaPatched)
                                            {
                                                ctx.GetOrAddAsOverride(_environmentStateProvider.OutputMod); // fallback in case parent record isn't in the load order (will cause a missing master, but that's the user's problem)
                                            }
                                        }
                                        if (mergeInDependencyRecords)
                                        {
                                            List<string> mergeInExceptions = new();
                                            var importSourceModKeys = appearanceModSetting.CorrespondingModKeys
                                                .Distinct()
                                                .Where(k => k != patchNpc.FormKey.ModKey) // don't copy from the mod that defines the NPC, since that is a base mod
                                                .ToHashSet();
                                            
                                            var additionalMergedRecords =_recordHandler.DuplicateFromOnlyReferencedGetters(
                                                _environmentStateProvider.OutputMod, deltaPatchedRecords,
                                                importSourceModKeys,
                                                appearanceModKey.Value, true, RecordHandler.RecordLookupFallBack.Winner, ref mergeInExceptions);
                                    
                                            if (mergeInExceptions.Any())
                                            {
                                                AppendLog("Exceptions occurred during dependency merge-in of " + Auxilliary.GetNpcLogString(patchNpc) + Environment.NewLine + string.Join(Environment.NewLine, mergeInExceptions));
                                            }
                                            
                                            _aux.CollectShallowAssetLinks(additionalMergedRecords, assetLinks);
                                        }
                                        _aux.CollectShallowAssetLinks(dependencyContexts, assetLinks);
                                        break;
                                    
                                    case RecordOverrideHandlingMode.IncludeAsNew:
                                        List<string> overrideExceptionStrings = new();
                                        var mergedInRecords =
                                            _recordHandler.DuplicateInOverrideRecords(appearanceNpcRecord, patchNpc,
                                                appearanceModSetting.CorrespondingModKeys, appearanceModKey.Value, patchNpc.FormKey.ModKey, ref overrideExceptionStrings);
                                        if (overrideExceptionStrings.Any())
                                        {
                                            AppendLog(string.Join(Environment.NewLine, overrideExceptionStrings), true, true);
                                        }
                                        _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                        break;
                                }
                                break;
                            
                            case PatchingMode.Default:
                            default:
                                AppendLog(
                                    $"      Mode: Default. Forwarding record from source plugin ({appearanceModKey?.FileName ?? "N/A"})."); // Verbose only
                                patchNpc = _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(appearanceNpcRecord); // copy in the NPC as it appears in the source mod
                
                                // deep copy in all referenced records originating from the source plugins
                                if (mergeInDependencyRecords)
                                {
                                    List<string> mergeInExceptions = new();
                                    var mergedInRecords =_recordHandler.DuplicateFromOnlyReferencedGetters(
                                        _environmentStateProvider.OutputMod, patchNpc,
                                        appearanceModSetting.CorrespondingModKeys,
                                        appearanceModKey.Value, true, RecordHandler.RecordLookupFallBack.Origin, ref mergeInExceptions);
                                    
                                    if (mergeInExceptions.Any())
                                    {
                                        AppendLog("Exceptions occurred during dependency merge-in of " + Auxilliary.GetNpcLogString(patchNpc) + Environment.NewLine + string.Join(Environment.NewLine, mergeInExceptions));
                                    }
                                    _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                }
                                
                                // handle overriden records originating outside of the source plugins
                                switch (recordOverrideHandlingMode)
                                {
                                    case RecordOverrideHandlingMode.Ignore:
                                        break;
                                    
                                    case RecordOverrideHandlingMode.Include:
                                        var dependencyContexts = _recordHandler.DeepGetOverriddenDependencyRecords(patchNpc,
                                            appearanceModSetting.CorrespondingModKeys);
                                        foreach (var ctx in dependencyContexts)
                                        {
                                            ctx.GetOrAddAsOverride(_environmentStateProvider.OutputMod);
                                        }
                                        _aux.CollectShallowAssetLinks(dependencyContexts, assetLinks);
                                        break;
                                    
                                    case RecordOverrideHandlingMode.IncludeAsNew:
                                        List<string> overrideExceptionStrings = new();
                                        var mergedInRecords =
                                            _recordHandler.DuplicateInOverrideRecords(appearanceNpcRecord, patchNpc,
                                                appearanceModSetting.CorrespondingModKeys, appearanceModKey.Value, patchNpc.FormKey.ModKey, ref overrideExceptionStrings);

                                        if (overrideExceptionStrings.Any())
                                        {
                                            AppendLog(string.Join(Environment.NewLine, overrideExceptionStrings), true, true);
                                        }
                                        
                                        _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                        break;
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
                        await Task.Delay(1, ct);
                        continue;
                    }
                    // --- *** End Scenario Logic *** 

                    // --- Copy Assets ---
                    if (patchNpc != null && appearanceModSetting != null) // Ensure we have a patch record and mod settings
                    {
                        await _assetHandler.SchduleCopyNpcAssets(appearanceNpcRecord, appearanceModSetting, _currentRunOutputAssetPath);
                        await _assetHandler.ScheduleCopyAssetLinkFiles(assetLinks, appearanceModSetting,
                            _currentRunOutputAssetPath);
                    }
                    else
                    {
                        AppendLog(
                            $"ERROR: Could not proceed with asset copying due to missing patch record or mod setting for {npcIdentifier}.",
                            true);
                        skippedCount++;
                        await Task.Delay(1, ct);
                        continue;
                    }

                    // Handle race deep-copy if needed
                    //_raceHandler.ProcessNpcRace(patchNpc, appearanceNpcRecord, winningNpcOverride, appearanceModKey.Value, appearanceModSetting);
                    
                    // ====================== CHECK AND PRINT REPORT ======================
                    // Check if the report should be generated and hasn't been already.
                    if (!profilingReportGenerated && ContextualPerformanceTracer.SampleLimitReached)
                    {
                        AppendLog("\n>>>>> Profiling sample limit reached. Generating performance report... <<<<<\n", true, true);
                        AppendLog(ContextualPerformanceTracer.GetReport(), true, true);
                        profilingReportGenerated = true; // Set flag to ensure it only prints once.
                    }
                    // ===================================================================

                    processedCount++;
                    await Task.Delay(5, ct);
                } // End For Loop
            } // End else (selectionsToProcess.Any())

            //await _raceHandler.ApplyRaceChanges(_currentRunOutputAssetPath);

            UpdateProgress(processedCount + skippedCount, processedCount + skippedCount, "Copying Files...");

            // --- Final Steps (Save Output Mod) ---
            if (processedCount > 0)
            {
                AppendLog($"\nProcessed {processedCount} NPC(s).", false, true); // Force log
                if (skippedCount > 0) AppendLog($"{skippedCount} NPC(s) were skipped.", false, true); // Force log
                
                AppendLog("Copying Asset Files to Output Mod: Please Wait.", false, true);
                await _assetHandler.CopyQueuedFiles(_currentRunOutputAssetPath, _settings.ModSettings, includeBsa: true,
                    performNifTextureDetection: true);
                AppendLog("Finished copying Asset Files.", false, true);
                
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
        private List<MajorRecord> CopyAppearanceData(INpcGetter sourceNpc, Npc targetNpc, ModSetting appearanceModSetting, ModKey sourceNpcContextModKey, string npcIdentifier, bool mergeInDependencyRecords)
        {
            using var _ = ContextualPerformanceTracer.Trace("Patcher.CopyAppearanceData");
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
            List<MajorRecord> mergedInRecords = new();
            var importSourceModKeys = appearanceModSetting.CorrespondingModKeys
                .Distinct()
                .Where(k => k != sourceNpc.FormKey.ModKey) // don't copy from the mod that defines the NPC, since that is a base mod
                .ToHashSet();
            
            if (mergeInDependencyRecords)
            {
                try
                {
                    List<string> skinExceptions = new();
                    var skinRecords = _recordHandler.DuplicateInFormLink(targetNpc.WornArmor, sourceNpc.WornArmor,
                        _environmentStateProvider.OutputMod, importSourceModKeys,
                        sourceNpcContextModKey, RecordHandler.RecordLookupFallBack.Origin, ref skinExceptions);

                    if (skinExceptions.Any())
                    {
                        AppendLog("Exceptions during skin assignment: " + Environment.NewLine + string.Join(Environment.NewLine, skinExceptions), true, true);
                    }
                    
                    mergedInRecords.AddRange(skinRecords);
                    
                    List<string> headExceptions = new();
                    var headRecords =_recordHandler.DuplicateInFormLink(targetNpc.HeadTexture, sourceNpc.HeadTexture,
                        _environmentStateProvider.OutputMod, importSourceModKeys,
                        sourceNpcContextModKey, RecordHandler.RecordLookupFallBack.Origin, ref headExceptions);
                    
                    if (headExceptions.Any())
                    {
                        AppendLog("Exceptions during head texture assignment: " + Environment.NewLine + string.Join(Environment.NewLine, skinExceptions), true, true);
                    }
                    mergedInRecords.AddRange(headRecords);
                    
                    List<string> colorExceptions = new();
                    var hairColorRecords = _recordHandler.DuplicateInFormLink(targetNpc.HairColor, sourceNpc.HairColor,
                        _environmentStateProvider.OutputMod, importSourceModKeys,
                        sourceNpcContextModKey, RecordHandler.RecordLookupFallBack.Origin, ref colorExceptions);
                    
                    if (colorExceptions.Any())
                    {
                        AppendLog("Exceptions during hair color assignment: " + Environment.NewLine + string.Join(Environment.NewLine, skinExceptions), true, true);
                    }
                    mergedInRecords.AddRange(hairColorRecords);

                    targetNpc.HeadParts.Clear();
                    List<string> headPartExceptions = new();
                    foreach (var hp in sourceNpc.HeadParts.Where(x => !x.IsNull))
                    {
                        var targetHp = new FormLink<IHeadPartGetter>();
                        var headPartRecords =_recordHandler.DuplicateInFormLink(targetHp, hp,
                            _environmentStateProvider.OutputMod, importSourceModKeys,
                            sourceNpcContextModKey, RecordHandler.RecordLookupFallBack.Origin, ref headPartExceptions);
                        targetNpc.HeadParts.Add(targetHp);
                        mergedInRecords.AddRange(headPartRecords);
                    }

                    if (headPartExceptions.Any())
                    {
                        AppendLog("Exceptions during head part assignment: " + Environment.NewLine + string.Join(Environment.NewLine, skinExceptions), true, true);
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
            
            return mergedInRecords;
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