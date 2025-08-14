using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Policy;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using static NPC_Plugin_Chooser_2.BackEnd.RecordHandler;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class AssetHandler : OptionalUIModule
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly BsaHandler _bsaHandler;
    private readonly RecordHandler _recordHandler;
    private readonly Lazy<VM_Run> _runVM;
    private readonly SkyPatcherInterface _skyPatcherInterface;

    private bool _copyExtraAssets = true;
    private bool _copyExtraTexturesInNifs = true;
    private bool _getMissingExtraAssetsFromAvailableWinners = true;
    private bool _suppressAllMissingFileWarnings = false;
    private bool _suppressKnownMissingFileWarnings = true;
    
    private Dictionary<string, Dictionary<string, HashSet<string>>> _modContentPaths = new();
    public const string GameDataKey = "GameData";
    
    // This dictionary will store destination paths and their corresponding error messages
    // for copy operations that fail because the file is in use.
    private readonly ConcurrentDictionary<string, string> _potentialCopyFailures = new(StringComparer.OrdinalIgnoreCase);

    
    private HashSet<string> _pathsToIgnore = new();

    private Dictionary<string, HashSet<string>>
        _warningsToSuppress = new(); // Key: Plugin Name (lowercase), Value: Set of paths

    private HashSet<string> _warningsToSuppress_Global = new();
    
    // This dictionary tracks all requested asset operations to ensure each asset is processed only once.
    // The key is the asset's relative path, and the value is the task that handles its processing.
    private readonly ConcurrentDictionary<string, Task> _processedAssetTasks = new(StringComparer.OrdinalIgnoreCase);
    
    // ADD THIS FIELD:
    // This semaphore limits how many NIFs we process at the same time.
    // Initializing with Environment.ProcessorCount is a safe default.
    private readonly SemaphoreSlim _nifProcessingSemaphore = new(Environment.ProcessorCount);

    public AssetHandler(EnvironmentStateProvider environmentStateProvider, BsaHandler bsaHandler, RecordHandler recordHandler,
        SkyPatcherInterface skyPatcherInterface, Lazy<VM_Run> runVM)
    {
        _environmentStateProvider = environmentStateProvider;
        _bsaHandler = bsaHandler;
        _recordHandler = recordHandler;
        _skyPatcherInterface = skyPatcherInterface;
        _runVM = runVM;
    }

    public void Initialize()
    {
        _potentialCopyFailures.Clear();
        LoadAuxiliaryFiles();
    }
    
    /// <summary>
    /// Iterates through any copy failures that were cached due to "file in use" errors.
    /// Logs an error only if the destination file does not exist after all operations are complete.
    /// </summary>
    public void LogTrueCopyFailures()
    {
        AppendLog("Verifying final asset integrity...", false, true);
        int loggedErrors = 0;
        foreach (var failure in _potentialCopyFailures)
        {
            // Check if the file STILL doesn't exist. If so, it's a real failure.
            if (!File.Exists(failure.Key))
            {
                AppendLog(failure.Value, true, true); // Log the cached error message.
                loggedErrors++;
            }
        }

        if (loggedErrors > 0)
        {
            AppendLog($"Verification complete. Found {loggedErrors} true asset copy failures.", true, true);
        }
        else
        {
            AppendLog("Verification complete. All assets copied successfully.", false, true);
        }
    }
    
    /// <summary>
    /// Checks if a given folder path for a specific mod display name has been successfully cached.
    /// This relies on the cache built by PopulateExistingFilePathsAsync, avoiding redundant disk I/O.
    /// </summary>
    /// <param name="modDisplayName">The display name of the mod setting.</param>
    /// <param name="folderPath">The specific folder path to check.</param>
    /// <returns>True if the folder path exists as a key in the cache for the given mod.</returns>
    public bool IsModFolderPathCached(string modDisplayName, string folderPath)
    {
        return _modContentPaths.TryGetValue(modDisplayName, out var modFolders) && modFolders.ContainsKey(folderPath);
    }

    public async Task PopulateExistingFilePathsAsync(IEnumerable<ModSetting> mods)
    {
        _modContentPaths.Clear();

        // Task.Run pushes the synchronous, blocking file I/O work to a background thread,
        // allowing this method to immediately yield via 'await'. This unblocks the caller
        // and lets the UI update with any log messages that were sent before this call.
        await Task.Run(() =>
        {
            foreach (var mod in mods)
            {
                var subDict = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                _modContentPaths.Add(mod.DisplayName, subDict);

                foreach (var dataFolder in mod.CorrespondingFolderPaths)
                {
                    // This is the blocking call that we are moving to a background thread.
                    string[] allFilePaths = Directory.GetFiles(
                        dataFolder,
                        "*",
                        SearchOption.AllDirectories);

                    var fileSet = new HashSet<string>(allFilePaths, StringComparer.OrdinalIgnoreCase);
                    subDict.Add(dataFolder, fileSet);
                }
            }

            // manual scan of game data folder
            if (!_modContentPaths.ContainsKey(GameDataKey))
            {
                var gameDatasubDict = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                string[] allFilePathsDataFolder = Directory.GetFiles(
                    _environmentStateProvider.DataFolderPath,
                    "*",
                    SearchOption.AllDirectories);

                var gameFileSet = new HashSet<string>(allFilePathsDataFolder, StringComparer.OrdinalIgnoreCase);
                gameDatasubDict.Add(_environmentStateProvider.DataFolderPath, gameFileSet);
                _modContentPaths.Add(GameDataKey, gameDatasubDict);
            }
        });
    }

    
    public bool FileExists(string path, string modName, string dataFolder)
    {
        // This check is now O(1) instead of O(N)
        if (_modContentPaths.TryGetValue(modName, out var modFolders) &&
            modFolders.TryGetValue(dataFolder, out var files) &&
            files.Contains(path))
        {
            return true;
        }
        return false;
    }
    
    public bool FileExists(string path, string modName)
    {
        if (_modContentPaths.TryGetValue(modName, out var modFolders))
        {
            foreach (var entry in modFolders)
            {
                // This check is now O(1) instead of O(N)
                if (entry.Value.Contains(path))
                {
                    return true;
                }
            }
        }
        return false;
    }
    
    public bool FileExists(string path)
    {
        foreach (var modEntry in _modContentPaths.Values)
        {
            foreach (var folderEntry in modEntry.Values)
            {
                // This check is now O(1) instead of O(N)
                if (folderEntry.Contains(path))
                {
                    return true;
                }
            }
        }

        return false;
    }
    
    public bool AssetExists(string relativePath, ModSetting modSetting)
    {
        var (sourceType, _, _) = FindAssetSource(relativePath, modSetting);
        return sourceType != AssetSourceType.NotFound;
    }
    
    static string? RegularizeOrNull(string path) =>
        Auxilliary.TryRegularizePath(path, out var rel) ? rel : null;
    
    /// <summary>
    /// Returns a task that will complete when all scheduled asset operations have finished.
    /// </summary>
    public Task WhenAllTasks() => Task.WhenAll(_processedAssetTasks.Values);

    /// <summary>
    /// The main entry point for requesting an asset. It finds the asset's source (loose or BSA)
    /// and schedules a task to copy/extract it, including post-processing for NIF files.
    /// This operation is idempotent; subsequent requests for the same asset will return the existing task.
    /// </summary>
    /// <param name="relativePath">The asset's relative path (e.g., "textures\\actor.dds").</param>
    /// <param name="modSetting">The mod setting providing context for where to find the asset.</param>
    /// <param name="outputBasePath">The root output directory for the patch.</param>
    private Task RequestAssetCopyAsync(string relativePath, ModSetting modSetting, string outputBasePath, string? overrideDestinationRelativePath = null)
    {
        // FIX: Create a composite key to uniquely identify an asset *within the context of its source mod*.
        // This prevents a failed lookup from one mod from blocking a successful lookup from another mod for the same relative path.
        string cacheKey = $"{relativePath}|{modSetting.DisplayName}";

        return _processedAssetTasks.GetOrAdd(cacheKey, _ => Task.Run(async () =>
        {
            await _nifProcessingSemaphore.WaitAsync();
            try
            {
                var (sourceType, sourcePath, bsaPath) = FindAssetSource(relativePath, modSetting);
                string destPath = Path.Combine(outputBasePath, relativePath);
                if (overrideDestinationRelativePath != null)
                {
                    destPath = Path.Combine(outputBasePath, overrideDestinationRelativePath);
                }
                switch (sourceType)
                {
                    case AssetSourceType.LooseFile:
                        // Create two tasks: one for copying, one for analyzing the source file.
                        Task copyTask = PerformLooseCopyAsync(sourcePath, destPath);
                        Task analysisTask = PostProcessNifTextures(sourcePath, modSetting, outputBasePath);
                    
                        // Await both tasks to run them in parallel.
                        await Task.WhenAll(copyTask, analysisTask);
                        break;

                    case AssetSourceType.BsaFile:
                        // For BSAs, we must extract first, then analyze the extracted file.
                        // The original sequential logic is still best here.
                        if (await _bsaHandler.ExtractFileAsync(bsaPath, relativePath, destPath))
                        {
                            await PostProcessNifTextures(destPath, modSetting, outputBasePath);
                        }
                        break;
                
                    default:
                        // If the asset is not found within this modSetting, the task simply completes,
                        // doing nothing. Another call with a different modSetting will have its own task.
                        break;
                }
            }
            finally
            {
                _nifProcessingSemaphore.Release();
            }
        }));
    }
    
    /// <summary>
    /// Performs the physical copy of a loose file.
    /// </summary>
    private async Task<bool> PerformLooseCopyAsync(string sourcePath, string destPath)
    {
        try
        {
            await Task.Run(() =>
            {
                FileInfo fileInfo = Auxilliary.CreateDirectoryIfNeeded(destPath, Auxilliary.PathType.File);
                File.Copy(sourcePath, destPath, true);
            });

            // If this copy succeeded, we can safely remove any error message
            // that a concurrent, failing thread might have added for the same file.
            _potentialCopyFailures.TryRemove(destPath, out _);
            return true;
        }
        catch (IOException ioEx) when (ioEx.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
        {
            // This is the specific concurrency error. Instead of logging, store the message.
            // We'll verify if the file actually failed to copy at the very end.
            string errorMessage = $"Failed to copy file from {sourcePath} to {destPath}: {ExceptionLogger.GetExceptionStack(ioEx)}";
            _potentialCopyFailures.TryAdd(destPath, errorMessage); // It's fine if another thread already added it.
            return false;
        }
        catch (Exception ex)
        {
            // Log all other types of exceptions immediately.
            AppendLog($"Failed to copy file from {sourcePath} to {destPath}: {ExceptionLogger.GetExceptionStack(ex)}", true, true);
            return false;
        }
    }

    /// <summary>
    /// After a file is copied/extracted, this checks if it is a NIF file. If so, it parses it
    /// for texture paths and recursively requests those assets.
    /// </summary>
    private async Task PostProcessNifTextures(string nifPathToAnalyze, ModSetting modSetting, string outputBasePath)
    {
        // The check now correctly uses the passed-in path.
        if (!nifPathToAnalyze.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            // The analysis now happens on the source NIF file.
            var texturesInNif = NifHandler.GetExtraTexturesFromNif(nifPathToAnalyze);
            var regularizedPaths = new HashSet<string>();
            foreach (var t in texturesInNif)
            {
                if (Auxilliary.TryRegularizePath(t, out var regularizedPath))
                {
                    regularizedPaths.Add(regularizedPath);
                }
            }

            if (!regularizedPaths.Any()) return;

            // This debug message remains useful.
            Debug.WriteLine($"      NIF Analysis: Found {regularizedPaths.Count} additional textures in {Path.GetFileName(nifPathToAnalyze)}.");

            var textureTasks = new List<Task>();
            foreach (var texRelPath in regularizedPaths)
            {
                textureTasks.Add(RequestAssetCopyAsync(texRelPath, modSetting, outputBasePath));
            }
            // await Task.WhenAll(textureTasks); DO not await here - causes deadlock due to upstream semaphore
        }
        catch (Exception ex)
        {
            AppendLog($"NIF TEXTURE ERROR: Failed to parse '{nifPathToAnalyze}' for additional textures: {ExceptionLogger.GetExceptionStack(ex)}", true, true);
        }
    }

    /// <summary>
    /// Finds the source of a given asset, checking loose files first, then BSAs.
    /// </summary>
    private (AssetSourceType, string SourcePath, string BsaPath) FindAssetSource(string relativePath, ModSetting modSetting)
    {
        // 1. Check loose files in the mod's folders
        foreach (var dirPath in modSetting.CorrespondingFolderPaths)
        {
            var candidatePath = Path.Combine(dirPath, relativePath);
            if (FileExists(candidatePath, modSetting.DisplayName, dirPath))
            {
                return (AssetSourceType.LooseFile, candidatePath, string.Empty);
            }
        }
        
        // 2. Check BSAs associated with the mod
        foreach (var modKey in modSetting.CorrespondingModKeys)
        {
            if (_bsaHandler.FileExists(relativePath, modKey, out var bsaPath) && bsaPath != null)
            {
                return (AssetSourceType.BsaFile, relativePath, bsaPath);
            }
        }

        return (AssetSourceType.NotFound, string.Empty, string.Empty);
    }
    
    private enum AssetSourceType { NotFound, LooseFile, BsaFile }

    /// <summary>
    /// Orchestrates copying assets for a specific NPC by identifying all required assets
    /// and scheduling them for asynchronous processing.
    /// </summary>
    public async Task ScheduleCopyNpcAssets(
        FormKey targetNpcFormKey, // the NPC to which the appearance is being applied
        INpcGetter appearanceNpcRecord,
        ModSetting appearanceModSetting,
        string outputBasePath,
        string npcIdentifier)
    {
        using var _ = ContextualPerformanceTracer.Trace("AssertHandler.ScheduleCopyNpcAssets");
        _runVM.Value.AppendLog(
            $"    Copying assets for {appearanceNpcRecord.EditorID ?? appearanceNpcRecord.FormKey.ToString()} from sources related to '{appearanceModSetting.DisplayName}'..."); // Verbose only

        var assetSourceDirs = appearanceModSetting.CorrespondingFolderPaths ?? new List<string>();
        if (!assetSourceDirs.Any())
        {
            _runVM.Value.AppendLog(
                $"      WARNING: Mod Setting '{appearanceModSetting.DisplayName}' has no Corresponding Folder Paths. Cannot copy assets."); // Verbose only (Warning)
            return;
        }

        // --- Identify Required Assets ---
        HashSet<string> meshToCopyRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> textureToCopyRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseModKey = appearanceNpcRecord.FormKey.ModKey;

        // 1. FaceGen Assets
        var (faceMeshRelativePath, faceTexRelativePath) =
            Auxilliary.GetFaceGenSubPathStrings(appearanceNpcRecord.FormKey, true);
        
        FormKey surrogateNpcFormKey = FormKey.Null;;

        bool useSkyPatcher =
            _skyPatcherInterface.TryGetSurrogateFormKey(appearanceNpcRecord.FormKey, out surrogateNpcFormKey);
        
        bool isFaceSwap = !targetNpcFormKey.Equals(appearanceNpcRecord.FormKey);
        if (!useSkyPatcher && isFaceSwap)
        {
            surrogateNpcFormKey = targetNpcFormKey;
        }
        
        // START: ADDED WARNING LOGIC FOR MISMATCHED FACEGEN FILES
        var (meshSourceType, _, _) = FindAssetSource(faceMeshRelativePath, appearanceModSetting);
        var (texSourceType, _, _) = FindAssetSource(faceTexRelativePath, appearanceModSetting);

        if (meshSourceType != AssetSourceType.NotFound && texSourceType == AssetSourceType.NotFound)
        {
            _runVM.Value.AppendLog($"      WARNING: For {npcIdentifier}, a FaceGen mesh (.nif) was found in '{appearanceModSetting.DisplayName}' but a texture (.dds) was not. The game may use a default texture.", false, true);
        }
        else if (meshSourceType == AssetSourceType.NotFound && texSourceType != AssetSourceType.NotFound)
        {
            _runVM.Value.AppendLog($"      WARNING: For {npcIdentifier}, a FaceGen texture (.dds) was found in '{appearanceModSetting.DisplayName}' but a mesh (.nif) was not. This may result in the 'brown face' bug.", false, true);
        }
        // END: ADDED WARNING LOGIC

        if (useSkyPatcher || isFaceSwap)
        {
            (var surrogateFaceGenNifPath, var surrogateFaceGenDdsPath) = // These store the paths for the original NPC FormKey - e.g. the one being copied from
                Auxilliary.GetFaceGenSubPathStrings(surrogateNpcFormKey, true);
            RequestAssetCopyAsync(faceMeshRelativePath, appearanceModSetting, outputBasePath, overrideDestinationRelativePath: surrogateFaceGenNifPath);
            RequestAssetCopyAsync(faceTexRelativePath, appearanceModSetting, outputBasePath, overrideDestinationRelativePath: surrogateFaceGenDdsPath);
        }
        else
        {
            meshToCopyRelativePaths.Add(faceMeshRelativePath);
            textureToCopyRelativePaths.Add(faceTexRelativePath); 
        }
        
        // 2. Non-FaceGen Assets (Only if CopyExtraAssets is true)
        if (_copyExtraAssets)
        {
            GetAssetPathsReferencedByPlugin(appearanceNpcRecord, appearanceModSetting.CorrespondingModKeys, appearanceModSetting.CorrespondingFolderPaths.ToHashSet(), meshToCopyRelativePaths, textureToCopyRelativePaths);
            AddCorrespondingNumericalNifPaths(meshToCopyRelativePaths, new HashSet<string>());
        }

        // 3. Schedule all identified assets for processing
        var allAssetPaths = meshToCopyRelativePaths.Concat(textureToCopyRelativePaths).ToHashSet(StringComparer.OrdinalIgnoreCase);

        AppendLog($"      Identified {allAssetPaths.Count} unique assets to process for {appearanceNpcRecord.EditorID ?? appearanceNpcRecord.FormKey.ToString()}.");

        foreach (var relPath in allAssetPaths)
        {
            if (Auxilliary.TryRegularizePath(relPath, out string regularizedPath))
            // This method is fire-and-forget; the task is added to the concurrent dictionary 
            // and runs in the background. It will not re-process assets it has already seen.
            RequestAssetCopyAsync(regularizedPath, appearanceModSetting, outputBasePath);
        }
    }

    /// <summary>
    /// Schedules asynchronous processing for assets identified via direct asset links.
    /// </summary>
    public async Task ScheduleCopyAssetLinkFiles(List<IAssetLinkGetter> assetLinks, ModSetting appearanceModSetting, string outputBasePath)
    {
        using var _ = ContextualPerformanceTracer.Trace("AssetHandler.ScheduleCopyAssetLinkFiles");
        var assetRelPaths =
            assetLinks
                .Select(x => x.GivenPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(RegularizeOrNull)
                .Where(rel => rel is not null)
                .Select(rel => rel!) // null-forgiving operator converts string? → string
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        AddCorrespondingNumericalNifPaths(assetRelPaths, new HashSet<string>()); // safe to pass non-nif files because the function filters for .nif paths anyway
        foreach (var relPath in assetRelPaths)
        {
            if (relPath != null)
            {
                RequestAssetCopyAsync(relPath, appearanceModSetting, outputBasePath);
            }
        }
    }
    
    /// <summary>
    /// Waits for all scheduled asset operations to finish while providing periodic status updates.
    /// </summary>
    /// <param name="progressReporter">An action that will be called with a formatted status string.</param>
    public async Task MonitorAndWaitForAllTasks(Action<string> progressReporter)
    {
        int lastReportedCompleted = 0;
        int lastReportedTotal = _processedAssetTasks.Count;

        progressReporter($"Asset Transfer: Beginning final asset copy. {lastReportedTotal} initial files queued.");

        while (true)
        {
            // Create a task that completes only when all *currently known* tasks are finished.
            var allCurrentTasks = _processedAssetTasks.Values.ToList();
            var completionTask = Task.WhenAll(allCurrentTasks);

            // Create a delay task that acts as our 10-second timer.
            var timerTask = Task.Delay(TimeSpan.FromSeconds(10));

            // Wait for whichever finishes first: the 10-second timer, or all known tasks completing.
            await Task.WhenAny(completionTask, timerTask);

            // Get a snapshot of the current counts.
            int totalTasks = _processedAssetTasks.Count;
            int completedTasks = _processedAssetTasks.Values.Count(t => t.IsCompleted);

            // Check if all tasks that existed at the start of this loop are done, AND no new tasks have been added.
            // This is the condition for being truly finished.
            if (completionTask.IsCompleted && totalTasks == allCurrentTasks.Count)
            {
                break; // Exit the monitoring loop.
            }

            // If we are here, it's because the 10-second timer elapsed, or tasks finished but new ones were added.
            // Time to generate a progress report.
            int remaining = totalTasks - completedTasks;
            int processedSinceLast = completedTasks - lastReportedCompleted;
            int discoveredSinceLast = totalTasks - lastReportedTotal;

            progressReporter($"Asset Transfer: {remaining} remaining files ({completedTasks}/{totalTasks} total, {processedSinceLast} processed, {discoveredSinceLast} discovered since last report)");

            // Update our counters for the next report.
            lastReportedCompleted = completedTasks;
            lastReportedTotal = totalTasks;
        }

        progressReporter($"Asset Transfer: Complete. {_processedAssetTasks.Count} total assets processed.");
    }

    // --- Asset Identification Helpers (No changes needed here) ---
    private void GetAssetPathsReferencedByPlugin(INpcGetter npc, IEnumerable<ModKey> correspondingModKeys, HashSet<string> fallBackModFolderNames, HashSet<string> meshPaths,
        HashSet<string> texturePaths)
    {
        /* Implementation remains the same */
        if (npc.HeadParts != null)
            foreach (var hpLink in npc.HeadParts)
                GetHeadPartAssetPaths(hpLink, correspondingModKeys, fallBackModFolderNames, texturePaths, meshPaths);
        if (!npc.WornArmor.IsNull && _recordHandler.TryGetRecordFromMods(npc.WornArmor, correspondingModKeys, fallBackModFolderNames,
                RecordLookupFallBack.Winner, out var wornArmorGetterGeneric) && wornArmorGetterGeneric != null)
        {
            var wornArmorGetter = wornArmorGetterGeneric as IArmorGetter;
            if (wornArmorGetter != null && wornArmorGetter.Armature != null)
            {
                foreach (var aaLink in wornArmorGetter.Armature)
                    GetARMAAssetPaths(aaLink, correspondingModKeys, fallBackModFolderNames, texturePaths, meshPaths);
            }
        }
    }

    private void GetHeadPartAssetPaths(IFormLinkGetter<IHeadPartGetter> hpLink, IEnumerable<ModKey> correspondingModKeys, HashSet<string> fallBackModFolderNames, HashSet<string> texturePaths,
        HashSet<string> meshPaths)
    {
        /* Implementation remains the same */
        if (hpLink.IsNull || !_recordHandler.TryGetRecordFromMods(hpLink, correspondingModKeys, fallBackModFolderNames, RecordLookupFallBack.Winner, out var hpGetterGeneric)) return;
        var hpGetter = hpGetterGeneric as IHeadPartGetter;
        if (hpGetter is null) return;
        
        if (hpGetter.Model?.File != null) meshPaths.Add(hpGetter.Model.File);
        if (hpGetter.Parts != null)
            foreach (var part in hpGetter.Parts)
                if (part?.FileName != null)
                    meshPaths.Add(part.FileName);
        if (!hpGetter.TextureSet.IsNull) GetTextureSetPaths(hpGetter.TextureSet, correspondingModKeys, fallBackModFolderNames, texturePaths);
        if (hpGetter.ExtraParts != null)
            foreach (var extraPartLink in hpGetter.ExtraParts)
                GetHeadPartAssetPaths(extraPartLink, correspondingModKeys, fallBackModFolderNames, texturePaths, meshPaths);
    }

    private void GetARMAAssetPaths(IFormLinkGetter<IArmorAddonGetter> aaLink, IEnumerable<ModKey> correspondingModKeys, HashSet<string> fallBackModFolderNames, HashSet<string> texturePaths,
        HashSet<string> meshPaths)
    {
        /* Implementation remains the same */
        if (aaLink.IsNull || !_recordHandler.TryGetRecordFromMods(aaLink, correspondingModKeys, fallBackModFolderNames, RecordLookupFallBack.Winner, out var aaGetterGeneric)) return;
        var aaGetter = aaGetterGeneric as IArmorAddonGetter;
        if (aaGetter is null) return;
        
        if (aaGetter.WorldModel?.Male?.File != null) meshPaths.Add(aaGetter.WorldModel.Male.File);
        if (aaGetter.WorldModel?.Female?.File != null) meshPaths.Add(aaGetter.WorldModel.Female.File);
        if (!aaGetter.SkinTexture?.Male.IsNull ?? false)
            GetTextureSetPaths(aaGetter.SkinTexture.Male, correspondingModKeys, fallBackModFolderNames, texturePaths);
        if (!aaGetter.SkinTexture?.Female.IsNull ?? false)
            GetTextureSetPaths(aaGetter.SkinTexture.Female, correspondingModKeys, fallBackModFolderNames, texturePaths);
    }

    private void GetTextureSetPaths(IFormLinkGetter<ITextureSetGetter> txstLink, IEnumerable<ModKey> correspondingModKeys, HashSet<string> fallBackModFolderNames,  HashSet<string> texturePaths)
    {
        /* Implementation remains the same */
        if (txstLink.IsNull ||
            !_recordHandler.TryGetRecordFromMods(txstLink, correspondingModKeys, fallBackModFolderNames, RecordLookupFallBack.Winner, out var txstGetterGeneric)) return;
        var txstGetter = txstGetterGeneric as ITextureSetGetter;
        if (txstGetter is null) return;
        
        if (!string.IsNullOrEmpty(txstGetter.Diffuse?.GivenPath)) texturePaths.Add(txstGetter.Diffuse.GivenPath);
        if (!string.IsNullOrEmpty(txstGetter.NormalOrGloss?.GivenPath))
            texturePaths.Add(txstGetter.NormalOrGloss.GivenPath);
        if (!string.IsNullOrEmpty(txstGetter.EnvironmentMaskOrSubsurfaceTint?.GivenPath))
            texturePaths.Add(txstGetter.EnvironmentMaskOrSubsurfaceTint.GivenPath);
        if (!string.IsNullOrEmpty(txstGetter.GlowOrDetailMap?.GivenPath))
            texturePaths.Add(txstGetter.GlowOrDetailMap.GivenPath);
        if (!string.IsNullOrEmpty(txstGetter.Height?.GivenPath)) texturePaths.Add(txstGetter.Height.GivenPath);
        if (!string.IsNullOrEmpty(txstGetter.Environment?.GivenPath))
            texturePaths.Add(txstGetter.Environment.GivenPath);
        if (!string.IsNullOrEmpty(txstGetter.Multilayer?.GivenPath))
            texturePaths.Add(txstGetter.Multilayer.GivenPath);
        if (!string.IsNullOrEmpty(txstGetter.BacklightMaskOrSpecular?.GivenPath))
            texturePaths.Add(txstGetter.BacklightMaskOrSpecular.GivenPath);
    }

    // --- Asset Copying/Extraction Helpers ---
    
    /// <summary>
    /// Returns the direct sub-directories of <paramref name="rootDir"/> that contain
    /// <paramref name="relativeFilePath"/> (e.g.  "Textures\Foo.png").
    /// Only the first level of sub-directories is inspected—no recursion.
    /// </summary>
    public static string[] GetContainingSubdirectories(
        string rootDir,
        string relativeFilePath)
    {
        if (rootDir is null) throw new ArgumentNullException(nameof(rootDir));
        if (relativeFilePath is null) throw new ArgumentNullException(nameof(relativeFilePath));

        return Directory // 1️⃣  list immediate sub-folders
            .EnumerateDirectories(rootDir, "*", SearchOption.TopDirectoryOnly)
            .Where(subDir => // 2️⃣  keep those that contain the file
                File.Exists(Path.Combine(subDir, relativeFilePath)))
            .ToArray(); // 3️⃣  materialise as string[]
    }

    /// <summary>
    /// Copies loose asset files, checking all source directories.
    /// **Revised for Clarification 2.**
    /// </summary>
    private Dictionary<string, bool> CopyAssetFiles(List<string> sourceDataDirPaths,
        HashSet<string> assetRelativePathList,
        string assetType /*"Meshes" or "Textures"*/, string sourcePluginName,
        IEnumerable<string> autoPredictedExtraPaths, string outputBaseDirPath)
    {
        Dictionary<string, bool> result = new();

        string outputBase = Path.Combine(outputBaseDirPath, assetType);
        Directory.CreateDirectory(outputBase);

        var warningsToSuppressSet = _warningsToSuppress_Global;
        string pluginKeyLower = sourcePluginName.ToLowerInvariant();
        if (_warningsToSuppress.TryGetValue(pluginKeyLower, out var specificWarnings))
        {
            warningsToSuppressSet = specificWarnings;
        }

        warningsToSuppressSet.UnionWith(autoPredictedExtraPaths);

        foreach (string relativePath in assetRelativePathList)
        {
            result[relativePath] = false;
            if (IsIgnored(relativePath, _pathsToIgnore)) continue;

            string? foundSourcePath = null;
            // Check Source Directories
            foreach (var sourcePathBase in sourceDataDirPaths)
            {
                string potentialPath = Path.Combine(sourcePathBase, assetType, relativePath);
                if (File.Exists(potentialPath))
                {
                    foundSourcePath = potentialPath;
                    break; // Found it
                }
            }

            // Check Game Data Folder (if configured and not FaceGen)
            bool isFaceGen = relativePath.Contains("facegendata", StringComparison.OrdinalIgnoreCase);
            if (foundSourcePath == null && _getMissingExtraAssetsFromAvailableWinners && !isFaceGen)
            {
                string gameDataPath = Path.Combine(_environmentStateProvider.DataFolderPath.ToString(), assetType,
                    relativePath);
                if (File.Exists(gameDataPath))
                {
                    foundSourcePath = gameDataPath;
                    _runVM.Value.AppendLog(
                        $"        Found missing asset '{relativePath}' in game data folder."); // Verbose only
                }
            }

            if (foundSourcePath == null)
            {
                // Handle Missing File Warning/Error
                bool suppressWarning = _suppressAllMissingFileWarnings ||
                                       (_suppressKnownMissingFileWarnings &&
                                        warningsToSuppressSet.Contains(relativePath)) ||
                                       GetExtensionOfMissingFile(relativePath) == ".tri";
                if (!suppressWarning)
                {
                    string errorMsg = $"Asset '{relativePath}' not found in any source directories";
                    if (_getMissingExtraAssetsFromAvailableWinners && !isFaceGen) errorMsg += " or game data folder";
                    errorMsg += $" (needed by {sourcePluginName}).";
                    _runVM.Value.AppendLog($"      WARNING: {errorMsg}"); // Verbose only (Warning)
                }
            }
            else
            {
                // Copy the found file
                string destPath = Path.Combine(outputBase, relativePath);
                try
                {
                    FileInfo fileInfo = Auxilliary.CreateDirectoryIfNeeded(destPath, Auxilliary.PathType.File);
                    File.Copy(foundSourcePath, destPath, true);
                    result[relativePath] = true;
                }
                catch (Exception ex)
                {
                    _runVM.Value.AppendLog(
                        $"      ERROR copying '{foundSourcePath}' to '{destPath}': {ExceptionLogger.GetExceptionStack(ex)}",
                        true);
                }
            }
        }

        return result;
    }

    // "The ArmorAddon should always point to the _1.nifs, so if they aren't I suggest you change it."
    // https://forums.nexusmods.com/topic/11698578-_1nif-not-detected/
    // Also from personal testing, it works the other way as well - if the corresponding _0.nif file is missing, a mesh
    // will (or at least can) turn invisible
    public static void AddCorrespondingNumericalNifPaths(HashSet<string> relativeNifPaths, HashSet<string> addedRelPaths)
    {
        // Manually add corresponding numerical nif paths if they don't already exist
        var iterableRelativePaths = relativeNifPaths.ToList();
        for (int i = 0; i < iterableRelativePaths.Count; i++)
        {
            string relPath = iterableRelativePaths[i];
            string replacedPath = string.Empty;
            if (relPath.EndsWith("_0.nif"))
            {
                replacedPath = relPath.Replace("_0.nif", "_1.nif");
                if (!relativeNifPaths.Contains(replacedPath))
                {
                    relativeNifPaths.Add(replacedPath);
                    addedRelPaths.Add(replacedPath);
                }
            }
            else if (relPath.EndsWith("_1.nif"))
            {
                replacedPath = relPath.Replace("_1.nif", "_0.nif");
                if (!relativeNifPaths.Contains(replacedPath))
                {
                    relativeNifPaths.Add(replacedPath);
                    addedRelPaths.Add(replacedPath);
                }
            }
        }
    }

    private bool IsIgnored(string relativePath, HashSet<string> toIgnore)
    {
        // (Implementation remains the same as before)
        return toIgnore.Contains(relativePath);
    }

    private string GetExtensionOfMissingFile(string input)
    {
        // (Implementation remains the same as before)
        if (string.IsNullOrEmpty(input))
        {
            return "";
        }

        return Path.GetExtension(input).ToLowerInvariant();
    }

    private bool LoadAuxiliaryFiles()
    {
        _runVM.Value.AppendLog("Loading auxiliary configuration files..."); // Verbose only
        bool success = true;
        // **Correction 3:** Use Resources path
        string resourcesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");

        // Load Ignored Paths
        _pathsToIgnore.Clear();
        string ignorePathFile = Path.Combine(resourcesPath, "Paths To Ignore.json"); // Corrected path
        if (File.Exists(ignorePathFile))
        {
            try
            {
                var tempList =
                    JSONhandler<List<string>>.LoadJSONFile(ignorePathFile, out bool loadSuccess, out string loadEx);
                if (loadSuccess && tempList != null)
                {
                    _pathsToIgnore = new HashSet<string>(tempList.Select(p => p.Replace(@"\\", @"\")),
                        StringComparer.OrdinalIgnoreCase);
                    _runVM.Value.AppendLog($"Loaded {_pathsToIgnore.Count} paths to ignore."); // Verbose only
                }
                else
                {
                    throw new Exception(loadEx);
                }
            }
            catch (Exception ex)
            {
                // **Correction 4:** Use ExceptionLogger
                _runVM.Value.AppendLog(
                    $"WARNING: Could not load or parse '{ignorePathFile}'. No paths will be ignored. Error: {ExceptionLogger.GetExceptionStack(ex)}"); // Verbose only (Warning)
            }
        }
        else
        {
            _runVM.Value.AppendLog(
                $"INFO: Ignore paths file not found at '{ignorePathFile}'. No paths will be ignored."); // Verbose only
        }


        // Load Suppressed Warnings
        _warningsToSuppress.Clear();
        _warningsToSuppress_Global.Clear();
        string suppressWarningsFile = Path.Combine(resourcesPath, "Warnings To Suppress.json"); // Corrected path
        if (File.Exists(suppressWarningsFile))
        {
            try
            {
                var tempList = JSONhandler<List<SuppressedWarnings>>.LoadJSONFile(suppressWarningsFile,
                    out bool loadSuccess, out string loadEx);
                if (loadSuccess && tempList != null)
                {
                    foreach (var sw in tempList)
                    {
                        var cleanedPaths = new HashSet<string>(sw.Paths.Select(p => p.Replace(@"\\", @"\")),
                            StringComparer.OrdinalIgnoreCase);
                        string pluginKeyLower = sw.Plugin.ToLowerInvariant();

                        if (pluginKeyLower == "global")
                        {
                            _warningsToSuppress_Global = cleanedPaths;
                        }
                        else
                        {
                            _warningsToSuppress[pluginKeyLower] = cleanedPaths;
                        }
                    }

                    _runVM.Value.AppendLog(
                        $"Loaded suppressed warnings for {_warningsToSuppress.Count} specific plugins and global scope."); // Verbose only
                }
                else
                {
                    throw new Exception(loadEx);
                }
            }
            catch (Exception ex)
            {
                // **Correction 4:** Use ExceptionLogger
                _runVM.Value.AppendLog(
                    $"ERROR: Could not load or parse '{suppressWarningsFile}'. Suppressed warnings will not be used. Error: {ExceptionLogger.GetExceptionStack(ex)}",
                    true);
                success = false;
            }
        }
        else
        {
            _runVM.Value.AppendLog(
                $"ERROR: Suppressed warnings file not found at '{suppressWarningsFile}'. Cannot proceed.",
                true);
            success = false;
        }

        return success;
    }
}

public class SuppressedWarnings
{
    public string Plugin { get; set; } = "";
    public HashSet<string> Paths { get; set; } = new HashSet<string>();
}