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
            
            ContextualPerformanceTracer.ResetAndStartSampling("Mod-Added", 250);
            bool profilingReportGenerated = false;

            // --- Main Processing Loop ---
            AppendLog("\nProcessing Valid NPC Appearance Selections...");
            
            var selectionsToProcess =
                screeningCache.Where(kv => kv.Value.SelectionIsValid).ToList();

            int totalToProcess = selectionsToProcess.Count;
            int overallProgressCounter = 0;
            int processedCount = 0;
            int skippedCount = 0;
            
            if (!selectionsToProcess.Any())
            {
                AppendLog("No valid NPC selections found or remaining after screening.");
            }
            else
            {
                // ====================== REFACTOR START ======================
                // Group selections by the chosen appearance mod to process them in batches.
                // This allows clearing the record traversal cache between batches, improving performance and reducing memory usage.
                var groupedSelections = selectionsToProcess
                    .GroupBy(kv => kv.Value.AppearanceModSetting?.DisplayName ?? "[FaceGen/No ModSetting]")
                    .OrderBy(g => g.Key);

                // Outer loop iterates through each batch (one per appearance mod)
                foreach (var npcGroup in groupedSelections)
                {
                    ct.ThrowIfCancellationRequested();

                    AppendLog($"\n--- Starting batch for: {npcGroup.Key} ({npcGroup.Count()} NPCs) ---", false, true);

                    // Clear caches to free memory and ensure they are only relevant for the current batch.
                    _recordHandler.Reinitialize();
                    _recordDeltaPatcher.Reinitialize();

                    var npcsInGroup = npcGroup.ToList();
                    // Inner loop processes each NPC within the current batch
                    for (int i = 0; i < npcsInGroup.Count; i++)
                    {
                        overallProgressCounter++;
                        var kvp = npcsInGroup[i];
                        
                        var npcFormKey = kvp.Key;
                        var result = kvp.Value;

                        var winningNpcOverride = result.WinningNpcOverride;
                        var appearanceModSetting = result.AppearanceModSetting;
                        var appearanceNpcRecord = result.AppearanceModRecord;
                        var appearanceModKey = result.AppearanceModKey;
                        string selectedModDisplayName = appearanceModSetting?.DisplayName ?? "N/A";
                        string npcIdentifier = $"{winningNpcOverride.Name?.String ?? winningNpcOverride.EditorID ?? npcFormKey.ToString()} ({npcFormKey})";
                        var mergeInDependencyRecords = appearanceModSetting?.MergeInDependencyRecords ?? false;
                        var recordOverrideHandlingMode = appearanceModSetting?.ModRecordOverrideHandlingMode ?? _settings.DefaultRecordOverrideHandlingMode;
                        List<IAssetLinkGetter> assetLinks = new();
                        
                        using var _context = ContextualPerformanceTracer.BeginContext(winningNpcOverride.FormKey.ModKey);
                        using var _ = ContextualPerformanceTracer.Trace("Patcher.MainLoopIteration");
                        
                        bool shouldUpdateUI = (overallProgressCounter % 10 == 0) || (overallProgressCounter == totalToProcess) || (overallProgressCounter == 1);

                        if (ShouldSkipNpc(winningNpcOverride, SelectedNpcGroup))
                        {
                            AppendLog($"  Skipping {npcIdentifier} (Group Filter)...");
                            skippedCount++;
                            if (shouldUpdateUI)
                            {
                                UpdateProgress(overallProgressCounter, totalToProcess, $"({overallProgressCounter}/{totalToProcess}) Skipped: {npcIdentifier}");
                            }
                            await Task.Delay(1, ct);
                            continue;
                        }

                        if (shouldUpdateUI)
                        {
                            UpdateProgress(overallProgressCounter, totalToProcess, $"({overallProgressCounter}/{totalToProcess}) Processing: {winningNpcOverride.EditorID ?? npcIdentifier}");
                        }
                        AppendLog($"- Processing: {npcIdentifier} -> Selected Mod: '{selectedModDisplayName}'");

                        Npc? patchNpc = null;

                        if (appearanceNpcRecord != null)
                        {
                            AppendLog("    Source: Plugin Record Override");

                            switch (_settings.PatchingMode)
                            {
                                case PatchingMode.EasyNPC_Like:
                                    AppendLog($"      Mode: EasyNPC-Like. Patching winning override ({winningNpcOverride.FormKey.ModKey.FileName}) with appearance from {appearanceModKey?.FileName ?? "N/A"}.");
                                    patchNpc = _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(winningNpcOverride);
                                    
                                    var mergedInAppearanceRecords = CopyAppearanceData(appearanceNpcRecord, patchNpc, appearanceModSetting, appearanceModKey.Value, npcIdentifier, mergeInDependencyRecords);
                                    _aux.CollectShallowAssetLinks(mergedInAppearanceRecords, assetLinks);
                                    
                                    if (mergeInDependencyRecords)
                                    {
                                        List<string> mergeInExceptions = new();
                                        var mergedInRecords = _recordHandler.DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod, patchNpc, appearanceModSetting.CorrespondingModKeys, appearanceModKey.Value, true, RecordHandler.RecordLookupFallBack.Winner, ref mergeInExceptions);
                                        if (mergeInExceptions.Any())
                                        {
                                            AppendLog("Exceptions occurred during dependency merge-in of " + Auxilliary.GetNpcLogString(patchNpc) + Environment.NewLine + string.Join(Environment.NewLine, mergeInExceptions));
                                        }
                                        _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                    }
                                    
                                    switch (recordOverrideHandlingMode)
                                    {
                                        case RecordOverrideHandlingMode.Ignore:
                                            break;
                                        case RecordOverrideHandlingMode.Include:
                                            var dependencyContexts = _recordHandler.DeepGetOverriddenDependencyRecords(patchNpc, appearanceModSetting.CorrespondingModKeys);
                                            List<MajorRecord> deltaPatchedRecords = new();
                                            foreach (var ctx in dependencyContexts)
                                            {
                                                bool wasDeltaPatched = false;
                                                if (_recordHandler.TryGetRecordFromMod(ctx.Record.FormKey, ctx.Record.Type, ctx.Record.FormKey.ModKey, RecordHandler.RecordLookupFallBack.None, out var baseRecord) && baseRecord != null)
                                                {
                                                    if (!_recordHandler.TryGetRecordFromMod(ctx.Record.FormKey, ctx.Record.Type, ctx.ModKey, RecordHandler.RecordLookupFallBack.None, out var overrideRecord) && baseRecord != null)
                                                    {
                                                        continue;
                                                    }
                                                    List<RecordDeltaPatcher.PropertyDiff> recordDifs = _recordDeltaPatcher.GetPropertyDiffs(overrideRecord, baseRecord);
                                                    IMajorRecordGetter? winningGetter = null;
                                                    if (recordDifs is not null && recordDifs.Any() && _environmentStateProvider.LinkCache.TryResolve(ctx.Record.FormKey, ctx.Record.Type, out winningGetter) && winningGetter != null)
                                                    {
                                                        if (Auxilliary.TryGetOrAddGenericRecordAsOverride(winningGetter, _environmentStateProvider.OutputMod, out var winningRecord, out string exceptionString) && winningRecord != null)
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
                                                    ctx.GetOrAddAsOverride(_environmentStateProvider.OutputMod);
                                                }
                                            }
                                            if (mergeInDependencyRecords)
                                            {
                                                List<string> mergeInExceptions = new();
                                                var importSourceModKeys = appearanceModSetting.CorrespondingModKeys.Distinct().Where(k => k != patchNpc.FormKey.ModKey).ToHashSet();
                                                var additionalMergedRecords = _recordHandler.DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod, deltaPatchedRecords, importSourceModKeys, appearanceModKey.Value, true, RecordHandler.RecordLookupFallBack.Winner, ref mergeInExceptions);
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
                                            var mergedInRecords = _recordHandler.DuplicateInOverrideRecords(appearanceNpcRecord, patchNpc, appearanceModSetting.CorrespondingModKeys, appearanceModKey.Value, patchNpc.FormKey.ModKey, ref overrideExceptionStrings);
                                            if (overrideExceptionStrings.Any())
                                            {
                                                AppendLog(string.Join(Environment.NewLine, overrideExceptionStrings), true, true);
                                            }
                                            _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                            break;
                                    }
                                    break;
                                default: // Default PatchingMode
                                    AppendLog($"      Mode: Default. Forwarding record from source plugin ({appearanceModKey?.FileName ?? "N/A"}).");
                                    patchNpc = _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(appearanceNpcRecord);
                                    if (mergeInDependencyRecords)
                                    {
                                        List<string> mergeInExceptions = new();
                                        var mergedInRecords =_recordHandler.DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod, patchNpc, appearanceModSetting.CorrespondingModKeys, appearanceModKey.Value, true, RecordHandler.RecordLookupFallBack.Origin, ref mergeInExceptions);
                                        if (mergeInExceptions.Any())
                                        {
                                            AppendLog("Exceptions occurred during dependency merge-in of " + Auxilliary.GetNpcLogString(patchNpc) + Environment.NewLine + string.Join(Environment.NewLine, mergeInExceptions));
                                        }
                                        _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                    }
                                    switch (recordOverrideHandlingMode)
                                    {
                                        case RecordOverrideHandlingMode.Ignore:
                                            break;
                                        case RecordOverrideHandlingMode.Include:
                                            var dependencyContexts = _recordHandler.DeepGetOverriddenDependencyRecords(patchNpc, appearanceModSetting.CorrespondingModKeys);
                                            foreach (var ctx in dependencyContexts)
                                            {
                                                ctx.GetOrAddAsOverride(_environmentStateProvider.OutputMod);
                                            }
                                            _aux.CollectShallowAssetLinks(dependencyContexts, assetLinks);
                                            break;
                                        case RecordOverrideHandlingMode.IncludeAsNew:
                                            List<string> overrideExceptionStrings = new();
                                            var mergedInRecords = _recordHandler.DuplicateInOverrideRecords(appearanceNpcRecord, patchNpc, appearanceModSetting.CorrespondingModKeys, appearanceModKey.Value, patchNpc.FormKey.ModKey, ref overrideExceptionStrings);
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
                            AppendLog($"ERROR: UNEXPECTED: Selection for {npcIdentifier} was marked valid but has no plugin record. Skipping.", true);
                            skippedCount++;
                            await Task.Delay(1, ct);
                            continue;
                        }

                        if (patchNpc != null && appearanceModSetting != null)
                        {
                            await _assetHandler.SchduleCopyNpcAssets(appearanceNpcRecord, appearanceModSetting, _currentRunOutputAssetPath);
                            await _assetHandler.ScheduleCopyAssetLinkFiles(assetLinks, appearanceModSetting, _currentRunOutputAssetPath);
                        }
                        else
                        {
                            AppendLog($"ERROR: Could not proceed with asset copying due to missing patch record or mod setting for {npcIdentifier}.", true);
                            skippedCount++;
                            await Task.Delay(1, ct);
                            continue;
                        }
                        
                        if (!profilingReportGenerated && ContextualPerformanceTracer.SampleLimitReached)
                        {
                            AppendLog("\n>>>>> Profiling sample limit reached. Generating performance report... <<<<<\n", true, true);
                            AppendLog(ContextualPerformanceTracer.GetReport(), true, true);
                            profilingReportGenerated = true;
                        }

                        processedCount++;
                        await Task.Delay(5, ct);
                    } // End Inner For Loop

                    AppendLog($"--- Finished batch for: {npcGroup.Key} ---", false, true);
                } // End Outer Foreach Loop
                // ======================= REFACTOR END =======================
            }

            UpdateProgress(totalToProcess, totalToProcess, "Copying Files...");

            // --- Final Steps (Save Output Mod) ---
            if (processedCount > 0)
            {
                AppendLog($"\nProcessed {processedCount} NPC(s).", false, true);
                if (skippedCount > 0) AppendLog($"{skippedCount} NPC(s) were skipped.", false, true);
                
                AppendLog("Copying Asset Files to Output Mod: Please Wait.", false, true);
                await _assetHandler.CopyQueuedFiles(_currentRunOutputAssetPath, _settings.ModSettings, includeBsa: true, performNifTextureDetection: true);
                AppendLog("Finished copying Asset Files.", false, true);
                
                string outputPluginPath = Path.Combine(_currentRunOutputAssetPath, _environmentStateProvider.OutputPluginFileName);
                AppendLog($"Attempting to save output mod to: {outputPluginPath}", true);
                try
                {
                    _environmentStateProvider.OutputMod.WriteToBinary(outputPluginPath);
                    AppendLog($"Output mod saved successfully.", false, true);
                }
                catch (Exception ex)
                {
                    AppendLog($"FATAL SAVE ERROR: Could not write output plugin: {ExceptionLogger.GetExceptionStack(ex)}", true);
                    AppendLog($"ERROR: Output mod NOT saved.", true);
                    ResetProgress();
                    return;
                }
            }
            else
            {
                AppendLog("\nNo NPC appearances processed or dependencies duplicated.", false, true);
                if (skippedCount > 0) AppendLog($"{skippedCount} NPC(s) were skipped.", false, true);
                AppendLog("Output mod not saved as no changes were made.", false, true);
            }

            AppendLog("\nPatch generation process completed.", false, true);
            UpdateProgress(totalToProcess, totalToProcess, "Finished.");
        }

        private void ClearDirectory(string path)
        {
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
                }
            }
        }

        private List<MajorRecord> CopyAppearanceData(INpcGetter sourceNpc, Npc targetNpc, ModSetting appearanceModSetting, ModKey sourceNpcContextModKey, string npcIdentifier, bool mergeInDependencyRecords)
        {
            using var _ = ContextualPerformanceTracer.Trace("Patcher.CopyAppearanceData");
            targetNpc.FaceMorph = sourceNpc.FaceMorph?.DeepCopy();
            targetNpc.FaceParts = sourceNpc.FaceParts?.DeepCopy();
            targetNpc.Height = sourceNpc.Height;
            targetNpc.Weight = sourceNpc.Weight;
            targetNpc.TextureLighting = sourceNpc.TextureLighting;
            targetNpc.TintLayers.Clear();
            targetNpc.TintLayers.AddRange(sourceNpc.TintLayers?.Select(t => t.DeepCopy()) ??
                                          Enumerable.Empty<TintLayer>());
            
            List<MajorRecord> mergedInRecords = new();
            var importSourceModKeys = appearanceModSetting.CorrespondingModKeys
                .Distinct()
                .Where(k => k != sourceNpc.FormKey.ModKey)
                .ToHashSet();
            
            if (mergeInDependencyRecords)
            {
                try
                {
                    List<string> skinExceptions = new();
                    var skinRecords = _recordHandler.DuplicateInFormLink(targetNpc.WornArmor, sourceNpc.WornArmor, _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, RecordHandler.RecordLookupFallBack.Origin, ref skinExceptions);
                    if (skinExceptions.Any())
                    {
                        AppendLog("Exceptions during skin assignment: " + Environment.NewLine + string.Join(Environment.NewLine, skinExceptions), true, true);
                    }
                    mergedInRecords.AddRange(skinRecords);
                    
                    List<string> headExceptions = new();
                    var headRecords =_recordHandler.DuplicateInFormLink(targetNpc.HeadTexture, sourceNpc.HeadTexture, _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, RecordHandler.RecordLookupFallBack.Origin, ref headExceptions);
                    if (headExceptions.Any())
                    {
                        AppendLog("Exceptions during head texture assignment: " + Environment.NewLine + string.Join(Environment.NewLine, skinExceptions), true, true);
                    }
                    mergedInRecords.AddRange(headRecords);
                    
                    List<string> colorExceptions = new();
                    var hairColorRecords = _recordHandler.DuplicateInFormLink(targetNpc.HairColor, sourceNpc.HairColor, _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, RecordHandler.RecordLookupFallBack.Origin, ref colorExceptions);
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
                        var headPartRecords =_recordHandler.DuplicateInFormLink(targetHp, hp, _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, RecordHandler.RecordLookupFallBack.Origin, ref headPartExceptions);
                        targetNpc.HeadParts.Add(targetHp);
                        mergedInRecords.AddRange(headPartRecords);
                    }
                    if (headPartExceptions.Any())
                    {
                        AppendLog("Exceptions during head part assignment: " + Environment.NewLine + string.Join(Environment.NewLine, skinExceptions), true, true);
                    }

                    AppendLog($"    Completed dependency processing for {npcIdentifier}.");
                }
                catch (Exception ex)
                {
                    AppendLog($"  ERROR duplicating dependencies for {npcIdentifier}: {ExceptionLogger.GetExceptionStack(ex)}", true);
                }
            }

            AppendLog($"      Copied appearance fields from {sourceNpc.FormKey.ModKey.FileName} to {targetNpc.FormKey} in patch.");

            if (NPCisTemplated(targetNpc) && !NPCisTemplated(sourceNpc))
            {
                AppendLog($"      Removing template flag from {targetNpc.FormKey} in patch.");
                targetNpc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Traits;
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