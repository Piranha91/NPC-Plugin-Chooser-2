﻿using System.Diagnostics;
using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
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
    private readonly SkyPatcherInterface _skyPatcherInterface;

    private Dictionary<string, ModSetting> _modSettingsMap;
    private string _currentRunOutputAssetPath = string.Empty;

    private bool _clearOutputDirectoryOnRun = true;

    public const string ALL_NPCS_GROUP = VM_Run.ALL_NPCS_GROUP;

    public Patcher(EnvironmentStateProvider environmentStateProvider, Settings settings, Validator validator,
        AssetHandler assetHandler, RecordHandler recordHandler, Auxilliary aux, RecordDeltaPatcher recordDeltaPatcher,
        PluginProvider pluginProvider, BsaHandler bsaHandler, SkyPatcherInterface skyPatcherInterface)
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
        _skyPatcherInterface = skyPatcherInterface;
    }

    public async Task PreInitializationLogicAsync()
    {
        AppendLog("Pre-Indexing loose file paths...", false, true);
        await _assetHandler.PopulateExistingFilePathsAsync(_settings.ModSettings);
        AppendLog("Finished Pre-Indexing loose file paths.", false, true);

        AppendLog("Pre-Indexing BSA file paths...", false, true);
        await _bsaHandler.PopulateBsaContentPathsAsync(_settings.ModSettings,
            _environmentStateProvider.SkyrimVersion.ToGameRelease());
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

        var processedNpcsTokenData = new Dictionary<FormKey, NpcAppearanceData>();

        if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid || _environmentStateProvider.LoadOrder == null)
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
            _recordDeltaPatcher.Reinitialize(true);

            string baseOutputDirectory;
            bool isSpecifiedDirectory = false;
            // Check if the provided path is a fully qualified path (e.g., "C:\My Output").
            // Path.IsPathRooted correctly distinguishes "NPC Output" from "C:\NPC Output".
            if (Path.IsPathRooted(_settings.OutputDirectory))
            {
                // If it's a full path, use it directly, whether it exists or not.
                baseOutputDirectory = _settings.OutputDirectory;
                isSpecifiedDirectory = true;
            }
            else
            {
                // If it's a simple name (relative path), treat it as a subdirectory of the mods folder.
                baseOutputDirectory = Path.Combine(_settings.ModsFolder, _settings.OutputDirectory);
                // isSpecifiedDirectory remains false, which is correct for this case.
            }

            // The baseOutputDirectory is already determined (e.g., "modsDir\NPC Output" or "C:\Mods\NPC Output")
            _currentRunOutputAssetPath = baseOutputDirectory;

            // Now, append a timestamp if the setting is enabled, regardless of whether the path was specified or not.
            if (_settings.AppendTimestampToOutputDirectory)
            {
                // Use the user-requested timestamp format.
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

                // Append the timestamp directly to the path string with a space.
                // This changes "C:\Mods\NPC Output" to "C:\Mods\NPC Output 2025-05-29_13-42-12".
                _currentRunOutputAssetPath = $"{baseOutputDirectory} {timestamp}";
            }

            AppendLog($"Using output asset directory: {_currentRunOutputAssetPath}", false, true);
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

            _skyPatcherInterface.Reinitialize(
                _currentRunOutputAssetPath); // reinitialize whether in SkyPatcher mode or not to avoid stale output 

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

                    using var _ = ContextualPerformanceTracer.BeginContext(npcGroup.Key);

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

                    _recordDeltaPatcher.Reinitialize(false);

                    var npcsInGroup = npcGroup.ToList();
                    for (int i = 0; i < npcsInGroup.Count; i++)
                    {
                        overallProgressCounter++;
                        var kvp = npcsInGroup[i];
                        var npcFormKey = kvp.Key;
                        var result = kvp.Value;
                        var winningNpcOverride = result.WinningNpcOverride;
                        var appearanceModSetting = result.AppearanceModSetting;
                        var appearanceNpcFormKey = kvp.Value.AppearanceNpcFormKey;

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
                            using var _ = ContextualPerformanceTracer.Trace("Patcher.MainLoopIteration");

                            AppendLog($"- Processing: {npcIdentifier} -> Selected Mod: '{selectedModDisplayName}'");

                            INpcGetter? appearanceNpcRecord = null;
                            ModKey? appearanceModKey = null;
                            bool correspondingRecordFound = false;

                            if (appearanceModSetting.NpcPluginDisambiguation.TryGetValue(appearanceNpcFormKey,
                                    out var disambiguationKey) &&
                                _recordHandler.TryGetRecordGetterFromMod(appearanceNpcFormKey.ToLink<INpcGetter>(),
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
                                        if (_recordHandler.TryGetRecordGetterFromMod(appearanceNpcFormKey.ToLink<INpcGetter>(),
                                                candidateKey, currentModFolderPaths,
                                                RecordHandler.RecordLookupFallBack.None,
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

                            bool isFaceGenOnly = false;
                            if (!correspondingRecordFound)
                            {
                                // Try to find the original source record (e.g., from Skyrim.esm)
                                if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey,
                                        out var baseNpcGetter, ResolveTarget.Origin))
                                {
                                    AppendLog(
                                        $"    Source: No specific plugin record override found in '{selectedModDisplayName}'. Using the source plugin as the base for assets.");
                                    appearanceNpcRecord = baseNpcGetter;
                                    appearanceModKey = baseNpcGetter.FormKey.ModKey;
                                    isFaceGenOnly = true;
                                }
                                else
                                {
                                    AppendLog(
                                        $"      ERROR: Could not resolve the original source record for {npcIdentifier}. This NPC may be from a missing master file. SKIPPING.",
                                        isError: true,
                                        forceLog: true);

                                    return;
                                }
                            }

                            Npc? patchNpc = null;
                            var mergeInDependencyRecords = appearanceModSetting?.MergeInDependencyRecords ?? false;
                            var recordOverrideHandlingMode = appearanceModSetting?.ModRecordOverrideHandlingMode ??
                                                             _settings.DefaultRecordOverrideHandlingMode;

                            if (isFaceGenOnly)
                            {
                                mergeInDependencyRecords = false;
                                recordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore;
                            }

                            List<IAssetLinkGetter> assetLinks = new();

                            if (appearanceNpcRecord != null)
                            {

                                var (faceMeshRelativePath, _) =
                                    Auxilliary.GetFaceGenSubPathStrings(appearanceNpcRecord.FormKey, regularized: true);
                                if (!_assetHandler.AssetExists(faceMeshRelativePath, appearanceModSetting))
                                {
                                    // If the mesh is missing, perform the more expensive check to see if the plugin *actually* changed head data.
                                    // Resolve the original base record for this NPC (e.g., from Skyrim.esm).
                                    if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(
                                            npcFormKey,
                                            out var baseNpcGetter, ResolveTarget.Origin))
                                    {
                                        // Compare the head-related properties of the appearance record to the base record.
                                        // Use static Equals() for top-level properties to safely handle any potential nulls.
                                        bool faceMorphsDiffer = !Equals(baseNpcGetter.FaceMorph,
                                            appearanceNpcRecord.FaceMorph);
                                        bool facePartsDiffer = !Equals(baseNpcGetter.FaceParts,
                                            appearanceNpcRecord.FaceParts);

                                        // fast head part equality check
                                        var appearanceHeadParts = appearanceNpcRecord.HeadParts.Select(x => x.FormKey)
                                            .ToHashSet();
                                        var baseHeadParts = baseNpcGetter.HeadParts.Select(x => x.FormKey).ToHashSet();
                                        bool headPartsDiffer = false;
                                        foreach (var hp in appearanceHeadParts)
                                        {
                                            if (!baseHeadParts.Contains(hp))
                                            {
                                                headPartsDiffer = true;
                                                break;
                                            }
                                        }

                                        // If any of the head data properties differ, log the critical warning.
                                        if (faceMorphsDiffer || facePartsDiffer || headPartsDiffer)
                                        {
                                            AppendLog(
                                                $"      CRITICAL WARNING: Mod '{appearanceModSetting.DisplayName}' modifies head data for {npcIdentifier} but does not provide the corresponding FaceGen mesh ({faceMeshRelativePath}). THIS WILL LIKELY CAUSE THE 'BLACK FACE' BUG.",
                                                true, true);
                                        }
                                    }
                                }

                                if (isFaceGenOnly)
                                {
                                    AppendLog("    Source: Original Plugin (FaceGen-only Mod)");
                                }
                                else
                                {
                                    AppendLog("    Source: Plugin Record Override");
                                }

                                switch (_settings.PatchingMode)
                                {
                                    case PatchingMode.CreateAndPatch:
                                        AppendLog(
                                            $"      Mode: Create and Patch. Patching winning override ({winningNpcOverride.FormKey.ModKey.FileName}) with appearance from {appearanceModKey?.FileName ?? "N/A"}.");

                                        if (_settings.UseSkyPatcherMode)
                                        {
                                            patchNpc = _skyPatcherInterface.CreateSkyPatcherNpc(winningNpcOverride);
                                        }
                                        else
                                        {
                                            patchNpc =
                                                _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(
                                                    winningNpcOverride);
                                        }

                                        bool includeOutfit = false;
                                        if (_settings.NpcOutfitOverrides.TryGetValue(npcFormKey,
                                                out var outfitOverrideChoice))
                                        {
                                            switch (outfitOverrideChoice)
                                            {
                                                case OutfitOverride.No: includeOutfit = false; break;
                                                case OutfitOverride.Yes: includeOutfit = true; break;
                                                case OutfitOverride.UseModSetting:
                                                    includeOutfit = appearanceModSetting.IncludeOutfits; break;
                                            }
                                        }
                                        else
                                        {
                                            includeOutfit = appearanceModSetting.IncludeOutfits;
                                        }
                                        

                                        var mergedInAppearanceRecords = CopyAppearanceData(appearanceNpcRecord,
                                            patchNpc,
                                            appearanceModSetting, appearanceModKey.Value,
                                            currentModFolderPaths, npcIdentifier,
                                            mergeInDependencyRecords, includeOutfit);
                                        _aux.CollectShallowAssetLinks(mergedInAppearanceRecords, assetLinks);
                                        if (mergeInDependencyRecords)
                                        {
                                            List<string> mergeInExceptions = new();
                                            var mergedInRecords = _recordHandler.DuplicateFromOnlyReferencedGetters(
                                                _environmentStateProvider.OutputMod, patchNpc,
                                                appearanceModSetting.CorrespondingModKeys, appearanceModKey.Value, true,
                                                appearanceModSetting.HandleInjectedRecords,
                                                currentModFolderPaths,
                                                ref mergeInExceptions);
                                            if (mergeInExceptions.Any())
                                            {
                                                AppendLog("Exceptions occurred during dependency merge-in of " +
                                                          Auxilliary.GetLogString(patchNpc, _settings.LocalizationLanguage) + Environment.NewLine +
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
                                                        appearanceModSetting.CorrespondingModKeys,
                                                        currentModFolderPaths);
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
                                                                baseRecord, overrideRecord, ctx.ModKey);

                                                        if (recordDifs is not null && recordDifs.Any())
                                                        {
                                                            IMajorRecordGetter? winningGetter = null;
                                                            var loquiType = Auxilliary.GetRecordGetterType(ctx.Record);
                                                            
                                                            if (
                                                                (loquiType != null && _environmentStateProvider.LinkCache.TryResolve(
                                                                    ctx.Record.FormKey,
                                                                    ctx.Record.Type, out winningGetter)
                                                                
                                                                ||
                                                                
                                                                _environmentStateProvider.LinkCache.TryResolve( // fallback because the typed lookup fails for IRaceGetter
                                                                    ctx.Record.FormKey, out winningGetter)
                                                                ) &&
                                                                winningGetter != null)
                                                            {
                                                                if (Auxilliary.TryGetOrAddGenericRecordAsOverride(
                                                                        winningGetter,
                                                                        _environmentStateProvider.OutputMod,
                                                                        out var winningRecord,
                                                                        out string exceptionString) &&
                                                                    winningRecord != null)
                                                                {
                                                                    _recordDeltaPatcher.ApplyPropertyDiffs(
                                                                        winningRecord,
                                                                        recordDifs, winningRecord, ctx.ModKey);
                                                                    deltaPatchedRecords.Add(winningRecord);
                                                                }
                                                                else
                                                                {
                                                                    AppendLog(
                                                                        Auxilliary.GetLogString(patchNpc, _settings.LocalizationLanguage) +
                                                                        ": Could not merge in winning override for " +
                                                                        Auxilliary.GetLogString(winningGetter, _settings.LocalizationLanguage) + ": " +
                                                                        exceptionString, true, true);
                                                                }
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
                                                            importSourceModKeys, appearanceModKey.Value, true,
                                                            appearanceModSetting.HandleInjectedRecords,
                                                            currentModFolderPaths,
                                                            ref mergeInExceptions);
                                                    if (mergeInExceptions.Any())
                                                    {
                                                        AppendLog("Exceptions occurred during dependency merge-in of " +
                                                                  Auxilliary.GetLogString(patchNpc, _settings.LocalizationLanguage) +
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
                                                    appearanceModSetting.HandleInjectedRecords,
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
                                            $"      Mode: Create. Forwarding record from source plugin ({appearanceModKey?.FileName ?? "N/A"}).");
                                        if (_settings.UseSkyPatcherMode)
                                        {
                                            patchNpc = _skyPatcherInterface.CreateSkyPatcherNpc(appearanceNpcRecord);
                                        }
                                        else
                                        {
                                            patchNpc =
                                                _environmentStateProvider.OutputMod.Npcs
                                                    .GetOrAddAsOverride(appearanceNpcRecord);
                                        }

                                        if (mergeInDependencyRecords)
                                        {
                                            List<string> mergeInExceptions = new();
                                            var mergedInRecords = _recordHandler.DuplicateFromOnlyReferencedGetters(
                                                _environmentStateProvider.OutputMod, patchNpc,
                                                appearanceModSetting.CorrespondingModKeys, appearanceModKey.Value, true,
                                                appearanceModSetting.HandleInjectedRecords,
                                                currentModFolderPaths,
                                                ref mergeInExceptions);
                                            if (mergeInExceptions.Any())
                                            {
                                                AppendLog("Exceptions occurred during dependency merge-in of " +
                                                          Auxilliary.GetLogString(patchNpc, _settings.LocalizationLanguage) + Environment.NewLine +
                                                          string.Join(Environment.NewLine, mergeInExceptions));
                                            }

                                            _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                        }

                                        if (_settings.UseSkyPatcherMode)
                                        {
                                            // These calls should always be delayed until after the merge-in functionality
                                            // Otherwise the referenced FormKeys will be the stale un-merged-in ones.
                                            // Correspondingly, make sure to call the SkyPatcher functions on patchNpc, not appearanceNpcRecord

                                            if (ShouldChangeGender(winningNpcOverride, patchNpc, out var genderToSet) && genderToSet != null)
                                            {
                                                _skyPatcherInterface.ToggleGender(npcFormKey, genderToSet.Value);
                                            }

                                            if (ShouldChangeRace(winningNpcOverride, patchNpc,
                                                    out var raceToSet) && raceToSet != null)
                                            {
                                                _skyPatcherInterface.ApplyRace(npcFormKey, raceToSet.Value);
                                            }

                                            if (ShouldChangeTraitsStatus(winningNpcOverride, patchNpc, out bool hasTraitsStatus))
                                            {
                                                _skyPatcherInterface.ToggleTemplateTraitsStatus(npcFormKey, hasTraitsStatus);
                                            }
                                            
                                            _skyPatcherInterface.SetOutfit(npcFormKey, patchNpc.DefaultOutfit.FormKey);
                                        }

                                        switch (recordOverrideHandlingMode)
                                        {
                                            case RecordOverrideHandlingMode.Ignore:
                                                break;
                                            case RecordOverrideHandlingMode.Include:
                                                var dependencyContexts =
                                                    _recordHandler.DeepGetOverriddenDependencyRecords(patchNpc,
                                                        appearanceModSetting.CorrespondingModKeys,
                                                        currentModFolderPaths);
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
                                                    appearanceModSetting.HandleInjectedRecords,
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
                                await _assetHandler.ScheduleCopyNpcAssets(npcFormKey, appearanceNpcRecord,
                                    appearanceModSetting, // appearanceNpcRecord here rather than patchNpc is intentional
                                    _currentRunOutputAssetPath, npcIdentifier);
                                await _assetHandler.ScheduleCopyAssetLinkFiles(assetLinks, appearanceModSetting,
                                    _currentRunOutputAssetPath);


                                if (_settings.UseSkyPatcherMode)
                                {
                                    _skyPatcherInterface.ApplyCoreAppearance(npcFormKey, patchNpc);
                                }
                            }
                            else
                            {
                                AppendLog(
                                    $"ERROR: Could not proceed with asset copying due to missing patch record or mod setting for {npcIdentifier}.",
                                    true);
                            }

                            if (appearanceModKey.HasValue)
                            {
                                processedNpcsTokenData[npcFormKey] = new NpcAppearanceData
                                {
                                    ModName = selectedModDisplayName,
                                    AppearancePlugin = appearanceModKey.Value
                                };
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
                    
                    // Verify any cached file access errors to see if they were actual failures.
                    _assetHandler.LogTrueCopyFailures();

                    AppendLog("All file operations finished.", false, true);

                    string outputPluginPath = Path.Combine(_currentRunOutputAssetPath,
                        _environmentStateProvider.OutputPluginFileName);
                    AppendLog($"Attempting to save output mod to: {outputPluginPath}", false);
                    try
                    {
                        _environmentStateProvider.OutputMod.WriteToBinary(outputPluginPath);
                        AppendLog($"Saved plugin: {outputPluginPath}.", false, true);

                        AppendLog("Writing NPC token file...", false, false);
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

                    if (_settings.UseSkyPatcherMode)
                    {
                        _skyPatcherInterface.WriteIni(_currentRunOutputAssetPath);
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
            _recordDeltaPatcher.FinalizeLog();
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

        foreach (FileInfo file in di.EnumerateFiles())
        {
            bool preserveFile = false;
            // Check if the file is a .txt file first, which is a fast operation.
            if (file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Efficiently check if any line in the file contains the command,
                    // without loading the entire file into memory.
                    if (File.ReadLines(file.FullName).Any(line =>
                            line.Contains("player.placeatme", StringComparison.OrdinalIgnoreCase)))
                    {
                        preserveFile = true;
                    }
                }
                catch (Exception ex)
                {
                    // If the file can't be read (e.g., permissions), err on the side of caution and preserve it.
                    AppendLog(
                        $"  Could not read file '{file.Name}' to check for preservation: {ex.Message}. Skipping deletion.",
                        isError: true);
                    preserveFile = true;
                }
            }

            if (preserveFile)
            {
                AppendLog($"  Preserving spawn command file: {file.Name}");
                continue; // Skip to the next file without deleting.
            }
            // --- End of new logic ---

            file.Delete();
        }

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

    private List<MajorRecord> CopyAppearanceData(INpcGetter sourceNpc, Npc targetNpc, ModSetting appearanceModSetting,
        ModKey sourceNpcContextModKey, HashSet<string> currentModFolderPaths, string npcIdentifier,
        bool mergeInDependencyRecords, bool includeOutfit)
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

        bool useSkyPatcher = false;
        FormKey skyPatcherOriginalFormKey = FormKey.Null;
        if (_settings.UseSkyPatcherMode &&
            _skyPatcherInterface.TryGetOriginalFormKey(targetNpc.FormKey, out skyPatcherOriginalFormKey))
        {
            useSkyPatcher = true;
        }

        if (ShouldChangeGender(targetNpc, sourceNpc, out var genderToSet) && genderToSet != null)
        {
            SetGender(targetNpc, genderToSet.Value);

            if (useSkyPatcher)
            {
                _skyPatcherInterface.ToggleGender(skyPatcherOriginalFormKey, genderToSet.Value);
            }
        }

        if (ShouldChangeRace(targetNpc, sourceNpc, out var raceToSet) && raceToSet != null)
        {
            targetNpc.Race.SetTo(raceToSet);
        }

        if (ShouldChangeTraitsStatus(targetNpc, sourceNpc, out bool hasTraitsStatus))
        {
            SetTraitsFlag(targetNpc, hasTraitsStatus);

            if (useSkyPatcher)
            {
                _skyPatcherInterface.ToggleTemplateTraitsStatus(skyPatcherOriginalFormKey, hasTraitsStatus);
            }
        }

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
                var skinRecords = _recordHandler.DuplicateInOrAddFormLink(targetNpc.WornArmor, sourceNpc.WornArmor,
                    _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, appearanceModSetting.HandleInjectedRecords,
                    currentModFolderPaths, ref skinExceptions);
                if (skinExceptions.Any())
                {
                    AppendLog(
                        "Exceptions during skin assignment: " + Environment.NewLine +
                        string.Join(Environment.NewLine, skinExceptions), true, true);
                }

                mergedInRecords.AddRange(skinRecords);

                List<string> headExceptions = new();
                var headRecords = _recordHandler.DuplicateInOrAddFormLink(targetNpc.HeadTexture, sourceNpc.HeadTexture,
                    _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, appearanceModSetting.HandleInjectedRecords,
                    currentModFolderPaths, ref headExceptions);
                if (headExceptions.Any())
                {
                    AppendLog(
                        "Exceptions during head texture assignment: " + Environment.NewLine +
                        string.Join(Environment.NewLine, headExceptions), true, true);
                }

                mergedInRecords.AddRange(headRecords);

                if (!targetNpc.Race.Equals(sourceNpc.Race))
                {
                    List<string> raceExceptions = new();
                    var raceRecords = _recordHandler.DuplicateInOrAddFormLink(targetNpc.Race, sourceNpc.Race,
                        _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, appearanceModSetting.HandleInjectedRecords,
                        currentModFolderPaths, ref raceExceptions);
                    if (raceExceptions.Any())
                    {
                        AppendLog(
                            "Exceptions during race assignment: " + Environment.NewLine +
                            string.Join(Environment.NewLine, raceExceptions), true, true);
                    }

                    if (useSkyPatcher)
                    {
                        _skyPatcherInterface.ApplyRace(skyPatcherOriginalFormKey, targetNpc.Race.FormKey);
                    }

                    mergedInRecords.AddRange(raceRecords);
                }

                List<string> colorExceptions = new();
                var hairColorRecords = _recordHandler.DuplicateInOrAddFormLink(targetNpc.HairColor, sourceNpc.HairColor,
                    _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, appearanceModSetting.HandleInjectedRecords,
                    currentModFolderPaths, ref colorExceptions);
                if (colorExceptions.Any())
                {
                    AppendLog(
                        "Exceptions during hair color assignment: " + Environment.NewLine +
                        string.Join(Environment.NewLine, skinExceptions), true, true);
                }

                mergedInRecords.AddRange(hairColorRecords);

                targetNpc.HeadParts.Clear();
                List<string> headPartExceptions = new();
                foreach (var hp in sourceNpc.HeadParts.Where(x => !x.IsNull))
                {
                    var targetHp = new FormLink<IHeadPartGetter>();
                    var headPartRecords = _recordHandler.DuplicateInOrAddFormLink(targetHp, hp,
                        _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, appearanceModSetting.HandleInjectedRecords,
                        currentModFolderPaths, ref headPartExceptions);
                    targetNpc.HeadParts.Add(targetHp);
                    mergedInRecords.AddRange(headPartRecords);
                }

                if (headPartExceptions.Any())
                {
                    AppendLog(
                        "Exceptions during head part assignment: " + Environment.NewLine +
                        string.Join(Environment.NewLine, headPartExceptions), true, true);
                }

                if (includeOutfit)
                {
                    List<string> outfitExceptions = new();
                    var outfitRecords = _recordHandler.DuplicateInOrAddFormLink(targetNpc.DefaultOutfit, sourceNpc.DefaultOutfit,
                        _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, appearanceModSetting.HandleInjectedRecords,
                        currentModFolderPaths, ref outfitExceptions);
                    if (outfitExceptions.Any())
                    {
                        AppendLog(
                            "Exceptions during outfit assignment: " + Environment.NewLine +
                            string.Join(Environment.NewLine, outfitExceptions), true, true);
                    }
                    mergedInRecords.AddRange(outfitRecords);

                    if (useSkyPatcher)
                    {
                        _skyPatcherInterface.SetOutfit(skyPatcherOriginalFormKey, targetNpc.DefaultOutfit.FormKey);
                    }
                }

                AppendLog($"    Completed dependency processing for {npcIdentifier}.");
            }
            catch (Exception ex)
            {
                AppendLog(
                    $"  ERROR duplicating dependencies for {npcIdentifier}: {ExceptionLogger.GetExceptionStack(ex)}",
                    true);
            }
        }
        else // set the formlinks to the original values
        {
            targetNpc.WornArmor.SetTo(sourceNpc.WornArmor);
            targetNpc.HeadTexture.SetTo(sourceNpc.HeadTexture);
            targetNpc.HairColor.SetTo(sourceNpc.HairColor);
            targetNpc.HeadParts.Clear();
            foreach (var hp in sourceNpc.HeadParts)
            {
                targetNpc.HeadParts.Add(hp);
            }

            if (includeOutfit)
            {
                targetNpc.DefaultOutfit.SetTo(sourceNpc.DefaultOutfit);
            }
        }

        AppendLog(
            $"      Copied appearance fields from {sourceNpc.FormKey.ModKey.FileName} to {targetNpc.FormKey} in patch.");

        if (NPCisTemplated(targetNpc) && !NPCisTemplated(sourceNpc))
        {
            AppendLog($"      Removing template flag from {targetNpc.FormKey} in patch.");
            targetNpc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Traits;
        }

        return mergedInRecords;
    }

    private bool ShouldChangeRace(INpcGetter targetNpc, INpcGetter appearanceNpc, out FormKey? changeTo)
    {
        changeTo = null;
        if (!targetNpc.Race.Equals(appearanceNpc.Race))
        {
            changeTo = appearanceNpc.Race.FormKey;
            return true;
        }
        return false;
    }

    private bool ShouldChangeGender(INpcGetter targetNpc, INpcGetter appearanceNpc, out Gender? changeTo)
    {
        changeTo = null;
        
        var targetGender = Auxilliary.GetGender(targetNpc);
        var appearanceGender = Auxilliary.GetGender(appearanceNpc);
        if (appearanceGender == Gender.Male && targetGender == Gender.Female)
        {
            changeTo = Gender.Male;
            return true;
        }

        if (appearanceGender == Gender.Female && targetGender == Gender.Male)
        {
            changeTo = Gender.Female;
            return true;
        }

        return false;
    }

    private void SetGender(Npc targetNpc, Gender gender)
    {
        if (gender == Gender.Female)
        {
            // Set Female bit
            targetNpc.Configuration.Flags |= NpcConfiguration.Flag.Female;
        }
        else
        {
            // Clear Female bit
            targetNpc.Configuration.Flags &= ~NpcConfiguration.Flag.Female;
        }
    }

    private void SetTraitsFlag(Npc targetNpc, bool hasTraits)
    {
        if (hasTraits)
        {
            // Set Traits bit
            targetNpc.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
        }
        else
        {
            // Clear Traits bit
            targetNpc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Traits;
        }
    }

    private bool ShouldChangeTraitsStatus(INpcGetter targetNpc, INpcGetter appearanceNpc, out bool hasTraits)
    {
        hasTraits = false;

        bool targetHasTraits = Auxilliary.HasTraitsFlag(targetNpc);
        bool apparanceHasTraits = Auxilliary.HasTraitsFlag(appearanceNpc);

        if (apparanceHasTraits && !targetHasTraits)
        {
            hasTraits = true;
            return true;
        }

        if (!apparanceHasTraits && targetHasTraits)
        {
            hasTraits = false;
            return true;
        }
        
        return false;
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