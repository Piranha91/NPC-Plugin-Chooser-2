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

    public async Task RunPatchingLogic(string SelectedNpcGroup, bool showFinalMessage, CancellationToken ct)
    {
        ResetLog();
        UpdateProgress(0, 1, "Initializing...");
        AppendLog("Starting patch generation...");

        var processedNpcsTokenData = new Dictionary<FormKey, (string ModName, ModKey AppearancePlugin)>();

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

        if (!_modSettingsMap.Any())
        {
            BuildModSettingsMap();
        }

        var screeningCache = _validator.GetScreeningCache();
        var selectionsToProcess = screeningCache.Where(kv => kv.Value.SelectionIsValid).ToList();

        try
        {
            _assetHandler.Initialize();
            _recordDeltaPatcher.Reinitialize();

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

            AppendLog($"Using output asset directory: {_currentRunOutputAssetPath}");
            try
            {
                Directory.CreateDirectory(_currentRunOutputAssetPath);
                AppendLog("Ensured output asset directory exists.");
            }
            catch (Exception ex)
            {
                AppendLog(
                    $"ERROR: Could not create output asset directory... Aborting. Error: {ExceptionLogger.GetExceptionStack(ex)}",
                    true);
                ResetProgress();
                return;
            }

            _environmentStateProvider.OutputMod =
                new SkyrimMod(ModKey.FromName(_environmentStateProvider.OutputPluginName, ModType.Plugin),
                    _environmentStateProvider.SkyrimVersion);
            AppendLog($"Initialized output mod: {_environmentStateProvider.OutputPluginName}");

            if (_clearOutputDirectoryOnRun)
            {
                AppendLog("Clearing output asset directory...");
                try
                {
                    ClearDirectory(_currentRunOutputAssetPath);
                    AppendLog("Output asset directory cleared.");
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

            AppendLog("\nProcessing Valid NPC Appearance Selections...");

            if (!selectionsToProcess.Any())
            {
                AppendLog("No valid NPC selections found or remaining after screening.");
            }
            else
            {
                var groupedSelections = selectionsToProcess
                    .GroupBy(kv => kv.Value.AppearanceModSetting?.DisplayName ?? "[FaceGen/No ModSetting]")
                    .OrderBy(g => g.Key);

                int totalToProcess = selectionsToProcess.Count;
                int overallProgressCounter = 0;
                int processedCount = 0;
                int skippedCount = 0;

                foreach (var npcGroup in groupedSelections)
                {
                    ct.ThrowIfCancellationRequested();

                    ContextualPerformanceTracer.Reset();

                    AppendLog($"\n--- Loading resources for batch: {npcGroup.Key} ---", false, true);

                    List<ModKey> modKeysForBatch = new();
                    HashSet<string> currentModFolderPaths = new();
                    
                    await Task.Run(() =>
                    {
                        ModSetting? currentModSetting = null;

                        if (_modSettingsMap.TryGetValue(npcGroup.Key, out currentModSetting) &&
                            currentModSetting != null)
                        {
                            modKeysForBatch.AddRange(currentModSetting.CorrespondingModKeys);
                            currentModFolderPaths = currentModSetting.CorrespondingFolderPaths.ToHashSet();
                            _pluginProvider.LoadPlugins(modKeysForBatch, currentModFolderPaths);
                            _recordHandler.PrimeLinkCachesFor(modKeysForBatch, currentModFolderPaths);
                            _recordHandler.ResetMapping();
                            _bsaHandler.OpenBsaReadersFor(currentModSetting, _settings.SkyrimRelease.ToGameRelease());
                        }
                        else
                        {
                            AppendLog(
                                $"Note: Batch '{npcGroup.Key}' has no associated mod setting. Processing with standard resources.",
                                false, true);
                        }
                    });

                    _recordDeltaPatcher.Reinitialize();

                    var npcsInGroup = npcGroup.ToList();
                    for (int i = 0; i < npcsInGroup.Count; i++)
                    {
                        overallProgressCounter++;
                        var kvp = npcsInGroup[i];
                        var npcFormKey = kvp.Key;
                        var result = kvp.Value;
                        var winningNpcOverride = result.WinningNpcOverride;
                        var appearanceModSetting = result.AppearanceModSetting;

                        string selectedModDisplayName = appearanceModSetting?.DisplayName ?? "N/A";
                        string npcIdentifier =
                            $"{winningNpcOverride.Name?.String ?? winningNpcOverride.EditorID ?? npcFormKey.ToString()} ({npcFormKey})";

                        bool shouldUpdateUI = (overallProgressCounter % 10 == 0) ||
                                              (overallProgressCounter == totalToProcess) ||
                                              (overallProgressCounter == 1);

                        if (ShouldSkipNpc(winningNpcOverride, SelectedNpcGroup))
                        {
                            AppendLog($"  Skipping {npcIdentifier} (Group Filter)...");
                            skippedCount++;
                            if (shouldUpdateUI)
                            {
                                UpdateProgress(overallProgressCounter, totalToProcess,
                                    $"Skipped: {npcIdentifier}");
                                await Task.Yield();
                            }

                            continue;
                        }

                        if (shouldUpdateUI)
                        {
                            UpdateProgress(overallProgressCounter, totalToProcess,
                                $"Processing: {winningNpcOverride.EditorID ?? npcIdentifier}");
                            await Task.Yield();
                        }

                        await Task.Run(async () =>
                        {
                            using var _context =
                                ContextualPerformanceTracer.BeginContext(winningNpcOverride.FormKey.ModKey);
                            using var _ = ContextualPerformanceTracer.Trace("Patcher.MainLoopIteration");

                            AppendLog($"- Processing: {npcIdentifier} -> Selected Mod: '{selectedModDisplayName}'");

                            INpcGetter? appearanceNpcRecord = null;
                            ModKey? appearanceModKey = null;
                            bool correspondingRecordFound = false;

                            if (appearanceModSetting.NpcPluginDisambiguation.TryGetValue(npcFormKey,
                                    out var disambiguationKey) &&
                                _recordHandler.TryGetRecordGetterFromMod(npcFormKey.ToLink<INpcGetter>(),
                                    disambiguationKey,
                                    currentModFolderPaths,
                                    RecordHandler.RecordLookupFallBack.None, out var disambiguatedRecord) &&
                                disambiguatedRecord != null)
                            {
                                appearanceNpcRecord = disambiguatedRecord as INpcGetter;
                                appearanceModKey = disambiguationKey;
                                correspondingRecordFound = true;
                                AppendLog(
                                    $"    Source: Found specific plugin record override in {disambiguationKey.FileName} (disambiguated).");
                            }
                            else
                            {
                                if (appearanceModSetting.CorrespondingModKeys.Any())
                                {
                                    foreach (var candidateKey in appearanceModSetting.CorrespondingModKeys)
                                    {
                                        if (_recordHandler.TryGetRecordGetterFromMod(npcFormKey.ToLink<INpcGetter>(),
                                                candidateKey, currentModFolderPaths, RecordHandler.RecordLookupFallBack.None,
                                                out var record) &&
                                            record != null)
                                        {
                                            appearanceNpcRecord = record as INpcGetter;
                                            appearanceModKey = candidateKey;
                                            correspondingRecordFound = true;
                                            AppendLog(
                                                $"    Source: Found plugin record override in {candidateKey.FileName}.");
                                            break;
                                        }
                                    }
                                }
                            }

                            if (!correspondingRecordFound)
                            {
                                AppendLog(
                                    $"    Source: No specific plugin record override found in '{selectedModDisplayName}'. Using winning override from load order as the base for assets.");
                                appearanceNpcRecord = winningNpcOverride;
                                appearanceModKey = winningNpcOverride.FormKey.ModKey;
                            }

                            Npc? patchNpc = null;
                            var mergeInDependencyRecords = appearanceModSetting?.MergeInDependencyRecords ?? false;
                            var recordOverrideHandlingMode = appearanceModSetting?.ModRecordOverrideHandlingMode ??
                                                             _settings.DefaultRecordOverrideHandlingMode;
                            List<IAssetLinkGetter> assetLinks = new();

                            if (appearanceNpcRecord != null)
                            {
                                AppendLog("    Source: Plugin Record Override");

                                switch (_settings.PatchingMode)
                                {
                                    case PatchingMode.EasyNPC_Like:
                                        AppendLog(
                                            $"      Mode: EasyNPC-Like. Patching winning override ({winningNpcOverride.FormKey.ModKey.FileName}) with appearance from {appearanceModKey?.FileName ?? "N/A"}.");
                                        patchNpc =
                                            _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(
                                                winningNpcOverride);
                                        var mergedInAppearanceRecords = CopyAppearanceData(appearanceNpcRecord,
                                            patchNpc,
                                            appearanceModSetting, appearanceModKey.Value, 
                                            currentModFolderPaths, npcIdentifier,
                                            mergeInDependencyRecords);
                                        _aux.CollectShallowAssetLinks(mergedInAppearanceRecords, assetLinks);
                                        if (mergeInDependencyRecords)
                                        {
                                            List<string> mergeInExceptions = new();
                                            var mergedInRecords = _recordHandler.DuplicateFromOnlyReferencedGetters(
                                                _environmentStateProvider.OutputMod, patchNpc,
                                                appearanceModSetting.CorrespondingModKeys, appearanceModKey.Value, true, currentModFolderPaths,
                                                ref mergeInExceptions);
                                            if (mergeInExceptions.Any())
                                            {
                                                AppendLog("Exceptions occurred during dependency merge-in of " +
                                                          Auxilliary.GetNpcLogString(patchNpc) + Environment.NewLine +
                                                          string.Join(Environment.NewLine, mergeInExceptions));
                                            }

                                            _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                        }

                                        switch (recordOverrideHandlingMode)
                                        {
                                            case RecordOverrideHandlingMode.Ignore:
                                                break;
                                            case RecordOverrideHandlingMode.Include:
                                                var dependencyContexts =
                                                    _recordHandler.DeepGetOverriddenDependencyRecords(patchNpc,
                                                        appearanceModSetting.CorrespondingModKeys, currentModFolderPaths);
                                                List<MajorRecord> deltaPatchedRecords = new();
                                                foreach (var ctx in dependencyContexts)
                                                {
                                                    bool wasDeltaPatched = false;
                                                    if (_recordHandler.TryGetRecordFromMod(ctx.Record.FormKey,
                                                            ctx.Record.Type,
                                                            ctx.Record.FormKey.ModKey,
                                                            currentModFolderPaths,
                                                            RecordHandler.RecordLookupFallBack.None,
                                                            out var baseRecord) && baseRecord != null)
                                                    {
                                                        if (!_recordHandler.TryGetRecordFromMod(ctx.Record.FormKey,
                                                                ctx.Record.Type, ctx.ModKey,
                                                                currentModFolderPaths,
                                                                RecordHandler.RecordLookupFallBack.None,
                                                                out var overrideRecord) && baseRecord != null)
                                                        {
                                                            continue;
                                                        }

                                                        List<RecordDeltaPatcher.PropertyDiff> recordDifs =
                                                            _recordDeltaPatcher.GetPropertyDiffs(overrideRecord,
                                                                baseRecord);
                                                        IMajorRecordGetter? winningGetter = null;
                                                        if (recordDifs is not null && recordDifs.Any() &&
                                                            _environmentStateProvider.LinkCache.TryResolve(
                                                                ctx.Record.FormKey,
                                                                ctx.Record.Type, out winningGetter) &&
                                                            winningGetter != null)
                                                        {
                                                            if (Auxilliary.TryGetOrAddGenericRecordAsOverride(
                                                                    winningGetter,
                                                                    _environmentStateProvider.OutputMod,
                                                                    out var winningRecord,
                                                                    out string exceptionString) &&
                                                                winningRecord != null)
                                                            {
                                                                _recordDeltaPatcher.ApplyPropertyDiffs(winningRecord,
                                                                    recordDifs);
                                                                deltaPatchedRecords.Add(winningRecord);
                                                            }
                                                            else
                                                            {
                                                                AppendLog(
                                                                    Auxilliary.GetNpcLogString(patchNpc) +
                                                                    ": Could not merge in winning override for " +
                                                                    Auxilliary.GetLogString(winningGetter) + ": " +
                                                                    exceptionString, true, true);
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
                                                    var importSourceModKeys = appearanceModSetting.CorrespondingModKeys
                                                        .Distinct().Where(k => k != patchNpc.FormKey.ModKey)
                                                        .ToHashSet();
                                                    var additionalMergedRecords =
                                                        _recordHandler.DuplicateFromOnlyReferencedGetters(
                                                            _environmentStateProvider.OutputMod, deltaPatchedRecords,
                                                            importSourceModKeys, appearanceModKey.Value, true, currentModFolderPaths,
                                                            ref mergeInExceptions);
                                                    if (mergeInExceptions.Any())
                                                    {
                                                        AppendLog("Exceptions occurred during dependency merge-in of " +
                                                                  Auxilliary.GetNpcLogString(patchNpc) +
                                                                  Environment.NewLine +
                                                                  string.Join(Environment.NewLine, mergeInExceptions));
                                                    }

                                                    _aux.CollectShallowAssetLinks(additionalMergedRecords, assetLinks);
                                                }

                                                _aux.CollectShallowAssetLinks(dependencyContexts, assetLinks);
                                                break;
                                            case RecordOverrideHandlingMode.IncludeAsNew:
                                                List<string> overrideExceptionStrings = new();
                                                var mergedInRecords = _recordHandler.DuplicateInOverrideRecords(
                                                    appearanceNpcRecord, patchNpc,
                                                    appearanceModSetting.CorrespondingModKeys,
                                                    appearanceModKey.Value, patchNpc.FormKey.ModKey,
                                                    currentModFolderPaths,
                                                    ref overrideExceptionStrings);
                                                if (overrideExceptionStrings.Any())
                                                {
                                                    AppendLog(
                                                        string.Join(Environment.NewLine, overrideExceptionStrings),
                                                        true,
                                                        true);
                                                }

                                                _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                                break;
                                        }

                                        break;
                                    default:
                                        AppendLog(
                                            $"      Mode: Default. Forwarding record from source plugin ({appearanceModKey?.FileName ?? "N/A"}).");
                                        patchNpc =
                                            _environmentStateProvider.OutputMod.Npcs
                                                .GetOrAddAsOverride(appearanceNpcRecord);
                                        if (mergeInDependencyRecords)
                                        {
                                            List<string> mergeInExceptions = new();
                                            var mergedInRecords = _recordHandler.DuplicateFromOnlyReferencedGetters(
                                                _environmentStateProvider.OutputMod, patchNpc,
                                                appearanceModSetting.CorrespondingModKeys, appearanceModKey.Value, true,
                                                currentModFolderPaths,
                                                ref mergeInExceptions);
                                            if (mergeInExceptions.Any())
                                            {
                                                AppendLog("Exceptions occurred during dependency merge-in of " +
                                                          Auxilliary.GetNpcLogString(patchNpc) + Environment.NewLine +
                                                          string.Join(Environment.NewLine, mergeInExceptions));
                                            }

                                            _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                        }

                                        switch (recordOverrideHandlingMode)
                                        {
                                            case RecordOverrideHandlingMode.Ignore:
                                                break;
                                            case RecordOverrideHandlingMode.Include:
                                                var dependencyContexts =
                                                    _recordHandler.DeepGetOverriddenDependencyRecords(patchNpc,
                                                        appearanceModSetting.CorrespondingModKeys, currentModFolderPaths);
                                                foreach (var ctx in dependencyContexts)
                                                {
                                                    ctx.GetOrAddAsOverride(_environmentStateProvider.OutputMod);
                                                }

                                                _aux.CollectShallowAssetLinks(dependencyContexts, assetLinks);
                                                break;
                                            case RecordOverrideHandlingMode.IncludeAsNew:
                                                List<string> overrideExceptionStrings = new();
                                                var mergedInRecords = _recordHandler.DuplicateInOverrideRecords(
                                                    appearanceNpcRecord, patchNpc,
                                                    appearanceModSetting.CorrespondingModKeys,
                                                    appearanceModKey.Value, patchNpc.FormKey.ModKey,
                                                    currentModFolderPaths,
                                                    ref overrideExceptionStrings);
                                                if (overrideExceptionStrings.Any())
                                                {
                                                    AppendLog(
                                                        string.Join(Environment.NewLine, overrideExceptionStrings),
                                                        true,
                                                        true);
                                                }

                                                _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                                break;
                                        }

                                        break;
                                }
                            }
                            else
                            {
                                AppendLog(
                                    $"ERROR: UNEXPECTED: Selection for {npcIdentifier} was marked valid but has no plugin record. Skipping.",
                                    true);
                            }

                            if (patchNpc != null && appearanceModSetting != null)
                            {
                                await _assetHandler.ScheduleCopyNpcAssets(appearanceNpcRecord, appearanceModSetting,
                                    _currentRunOutputAssetPath);
                                await _assetHandler.ScheduleCopyAssetLinkFiles(assetLinks, appearanceModSetting,
                                    _currentRunOutputAssetPath);
                            }
                            else
                            {
                                AppendLog(
                                    $"ERROR: Could not proceed with asset copying due to missing patch record or mod setting for {npcIdentifier}.",
                                    true);
                            }

                            if (appearanceModKey.HasValue)
                            {
                                processedNpcsTokenData[npcFormKey] = (selectedModDisplayName, appearanceModKey.Value);
                            }

                        });

                        processedCount++;
                    }

                    var perfReport = ContextualPerformanceTracer.GenerateReportForGroup(npcGroup.Key, false);
                    AppendLog(perfReport, false, true);

                    if (modKeysForBatch.Any())
                    {
                        AppendLog($"--- Unloading resources for batch: {npcGroup.Key} ---", false, true);
                        _pluginProvider.UnloadPlugins(modKeysForBatch);
                        _recordHandler.ClearLinkCachesFor(modKeysForBatch);
                    }
                }

                UpdateProgress(totalToProcess, totalToProcess, "Copying Files...");

                if (processedCount > 0)
                {
                    AppendLog($"\nProcessed {processedCount} NPC(s).", false, true);
                    if (skippedCount > 0) AppendLog($"{skippedCount} NPC(s) were skipped.", false, true);

                    AppendLog("Waiting for all background asset copying and extraction to finish...", false, true);

                    await _assetHandler.MonitorAndWaitForAllTasks(logMessage =>
                        AppendLog("  " + logMessage, false, true));

                    AppendLog("All file operations finished.", false, true);

                    string outputPluginPath = Path.Combine(_currentRunOutputAssetPath,
                        _environmentStateProvider.OutputPluginFileName);
                    AppendLog($"Attempting to save output mod to: {outputPluginPath}", true);
                    try
                    {
                        _environmentStateProvider.OutputMod.WriteToBinary(outputPluginPath);
                        AppendLog($"Output mod saved successfully.", false, true);

                        AppendLog("Writing NPC token file...", false, true);
                        var tokenFilePath = Path.Combine(_currentRunOutputAssetPath, "NPC_Token.json");
                        var tokenData = new NpcToken
                        {
                            CreationDate = DateTime.Now.ToString("o"),
                            ProcessedNpcs = processedNpcsTokenData
                        };

                        JSONhandler<NpcToken>.SaveJSONFile(tokenData, tokenFilePath, out bool tokenSaved,
                            out var exceptionStr);
                        if (!tokenSaved)
                        {
                            AppendLog($"NPC_Token.json not saved:" + Environment.NewLine + exceptionStr, true, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog(
                            $"FATAL SAVE ERROR: Could not write output plugin or token file: {ExceptionLogger.GetExceptionStack(ex)}",
                            true);
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
            }
        }
        finally
        {
            _bsaHandler.UnloadAllBsaReaders();
            UpdateProgress(selectionsToProcess.Count, selectionsToProcess.Count, "Finished.");
        }

        if (showFinalMessage)
        {
            AppendLog("\nPatch generation process completed.", false, true);
        }

        UpdateProgress(selectionsToProcess.Count, selectionsToProcess.Count, "Finished.");
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

        private List<MajorRecord> CopyAppearanceData(INpcGetter sourceNpc, Npc targetNpc, ModSetting appearanceModSetting, ModKey sourceNpcContextModKey, HashSet<string> currentModFolderPaths, string npcIdentifier, bool mergeInDependencyRecords)
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
                    var skinRecords = _recordHandler.DuplicateInOrAddFormLink(targetNpc.WornArmor, sourceNpc.WornArmor, _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, currentModFolderPaths, ref skinExceptions);
                    if (skinExceptions.Any())
                    {
                        AppendLog("Exceptions during skin assignment: " + Environment.NewLine + string.Join(Environment.NewLine, skinExceptions), true, true);
                    }
                    mergedInRecords.AddRange(skinRecords);
                    
                    List<string> headExceptions = new();
                    var headRecords =_recordHandler.DuplicateInOrAddFormLink(targetNpc.HeadTexture, sourceNpc.HeadTexture, _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, currentModFolderPaths,  ref headExceptions);
                    if (headExceptions.Any())
                    {
                        AppendLog("Exceptions during head texture assignment: " + Environment.NewLine + string.Join(Environment.NewLine, skinExceptions), true, true);
                    }
                    mergedInRecords.AddRange(headRecords);
                    
                    List<string> colorExceptions = new();
                    var hairColorRecords = _recordHandler.DuplicateInOrAddFormLink(targetNpc.HairColor, sourceNpc.HairColor, _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, currentModFolderPaths,  ref colorExceptions);
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
                        var headPartRecords =_recordHandler.DuplicateInOrAddFormLink(targetHp, hp, _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, currentModFolderPaths,  ref headPartExceptions);
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

/// <summary>
/// A data class to structure the contents of the NPC_Token.json file.
/// </summary>
public class NpcToken
{
    public string CreationDate { get; set; } = string.Empty;
    public Dictionary<FormKey, (string ModName, ModKey AppearancePlugin)> ProcessedNpcs { get; set; } = new();
}