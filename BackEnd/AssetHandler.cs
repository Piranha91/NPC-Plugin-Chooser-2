using System.Collections.Concurrent;
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

    private bool _copyExtraAssets = true;
    private bool _copyExtraTexturesInNifs = true;
    private bool _getMissingExtraAssetsFromAvailableWinners = true;
    private bool _suppressAllMissingFileWarnings = false;
    private bool _suppressKnownMissingFileWarnings = true;
    
    private Dictionary<string, Dictionary<string, HashSet<string>>> _modContentPaths = new();
    public const string GameDataKey = "GameData";
    
    private HashSet<string> _pathsToIgnore = new();

    private Dictionary<string, HashSet<string>>
        _warningsToSuppress = new(); // Key: Plugin Name (lowercase), Value: Set of paths

    private HashSet<string> _warningsToSuppress_Global = new();
    
    // This dictionary tracks all requested asset operations to ensure each asset is processed only once.
    // The key is the asset's relative path, and the value is the task that handles its processing.
    private readonly ConcurrentDictionary<string, Task> _processedAssetTasks = new(StringComparer.OrdinalIgnoreCase);

    public AssetHandler(EnvironmentStateProvider environmentStateProvider, BsaHandler bsaHandler, RecordHandler recordHandler,
        Lazy<VM_Run> runVM)
    {
        _environmentStateProvider = environmentStateProvider;
        _bsaHandler = bsaHandler;
        _recordHandler = recordHandler;
        _runVM = runVM;
    }

    public void Initialize()
    {
        LoadAuxiliaryFiles();
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
    private Task RequestAssetCopyAsync(string relativePath, ModSetting modSetting, string outputBasePath)
    {
        return _processedAssetTasks.GetOrAdd(relativePath, (relPath) => Task.Run(async () =>
        {
            var (sourceType, sourcePath, bsaPath) = FindAssetSource(relPath, modSetting);

            string destPath = Path.Combine(outputBasePath, relPath);

            switch (sourceType)
            {
                case AssetSourceType.LooseFile:
                    if (await PerformLooseCopyAsync(sourcePath, destPath))
                    {
                        await PostProcessNifTextures(destPath, modSetting, outputBasePath);
                    }
                    break;

                case AssetSourceType.BsaFile:
                    if (await _bsaHandler.ExtractFileAsync(bsaPath, relPath, destPath))
                    {
                        await PostProcessNifTextures(destPath, modSetting, outputBasePath);
                    }
                    break;
                
                default:
                    // Optionally log missing assets here. Note that SchduleCopyNpcAssets already does this.
                    break;
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
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to copy file from {sourcePath} to {destPath}: {ExceptionLogger.GetExceptionStack(ex)}", true, true);
            return false;
        }
    }

    /// <summary>
    /// After a file is copied/extracted, this checks if it is a NIF file. If so, it parses it
    /// for texture paths and recursively requests those assets.
    /// </summary>
    private async Task PostProcessNifTextures(string filePath, ModSetting modSetting, string outputBasePath)
    {
        if (!filePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var texturesInNif = NifHandler.GetExtraTexturesFromNif(filePath);
            var regularizedPaths = new HashSet<string>();
            foreach (var t in texturesInNif)
            {
                if (Auxilliary.TryRegularizePath(t, out var regularizedPath))
                {
                    regularizedPaths.Add(regularizedPath);
                }
            }

            if (!regularizedPaths.Any()) return;

            AppendLog($"      NIF Analysis: Found {regularizedPaths.Count} additional textures in {Path.GetFileName(filePath)}.", false, true);

            var textureTasks = new List<Task>();
            foreach (var texRelPath in regularizedPaths)
            {
                // Recursively request the textures found inside the NIF.
                textureTasks.Add(RequestAssetCopyAsync(texRelPath, modSetting, outputBasePath));
            }
            await Task.WhenAll(textureTasks);
        }
        catch (Exception ex)
        {
            AppendLog($"NIF TEXTURE ERROR: Failed to parse '{filePath}' for additional textures: {ExceptionLogger.GetExceptionStack(ex)}", true, true);
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
        INpcGetter appearanceNpcRecord,
        ModSetting appearanceModSetting,
        string outputBasePath)
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
            Auxilliary.GetFaceGenSubPathStrings(appearanceNpcRecord.FormKey); 
        meshToCopyRelativePaths.Add(faceMeshRelativePath);
        textureToCopyRelativePaths.Add(faceTexRelativePath);
        
        // 2. Non-FaceGen Assets (Only if CopyExtraAssets is true)
        if (_copyExtraAssets)
        {
            GetAssetPathsReferencedByPlugin(appearanceNpcRecord, appearanceModSetting.CorrespondingModKeys, meshToCopyRelativePaths, textureToCopyRelativePaths);
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
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var relPath in assetRelPaths)
        {
            if (relPath != null)
            {
                RequestAssetCopyAsync(relPath, appearanceModSetting, outputBasePath);
            }
        }
    }

    // --- Asset Identification Helpers (No changes needed here) ---
    private void GetAssetPathsReferencedByPlugin(INpcGetter npc, IEnumerable<ModKey> correspondingModKeys, HashSet<string> meshPaths,
        HashSet<string> texturePaths)
    {
        /* Implementation remains the same */
        if (npc.HeadParts != null)
            foreach (var hpLink in npc.HeadParts)
                GetHeadPartAssetPaths(hpLink, correspondingModKeys, texturePaths, meshPaths);
        if (!npc.WornArmor.IsNull && _recordHandler.TryGetRecordFromMods(npc.WornArmor, correspondingModKeys,
                RecordLookupFallBack.Winner, out var wornArmorGetterGeneric) && wornArmorGetterGeneric != null)
        {
            var wornArmorGetter = wornArmorGetterGeneric as IArmorGetter;
            if (wornArmorGetter != null && wornArmorGetter.Armature != null)
            {
                foreach (var aaLink in wornArmorGetter.Armature)
                    GetARMAAssetPaths(aaLink, correspondingModKeys, texturePaths, meshPaths);
            }
        }
    }

    private void GetHeadPartAssetPaths(IFormLinkGetter<IHeadPartGetter> hpLink, IEnumerable<ModKey> correspondingModKeys, HashSet<string> texturePaths,
        HashSet<string> meshPaths)
    {
        /* Implementation remains the same */
        if (hpLink.IsNull || !_recordHandler.TryGetRecordFromMods(hpLink, correspondingModKeys, RecordLookupFallBack.Winner, out var hpGetterGeneric)) return;
        var hpGetter = hpGetterGeneric as IHeadPartGetter;
        if (hpGetter is null) return;
        
        if (hpGetter.Model?.File != null) meshPaths.Add(hpGetter.Model.File);
        if (hpGetter.Parts != null)
            foreach (var part in hpGetter.Parts)
                if (part?.FileName != null)
                    meshPaths.Add(part.FileName);
        if (!hpGetter.TextureSet.IsNull) GetTextureSetPaths(hpGetter.TextureSet, correspondingModKeys, texturePaths);
        if (hpGetter.ExtraParts != null)
            foreach (var extraPartLink in hpGetter.ExtraParts)
                GetHeadPartAssetPaths(extraPartLink, correspondingModKeys, texturePaths, meshPaths);
    }

    private void GetARMAAssetPaths(IFormLinkGetter<IArmorAddonGetter> aaLink, IEnumerable<ModKey> correspondingModKeys, HashSet<string> texturePaths,
        HashSet<string> meshPaths)
    {
        /* Implementation remains the same */
        if (aaLink.IsNull || !_recordHandler.TryGetRecordFromMods(aaLink, correspondingModKeys, RecordLookupFallBack.Winner, out var aaGetterGeneric)) return;
        var aaGetter = aaGetterGeneric as IArmorAddonGetter;
        if (aaGetter is null) return;
        
        if (aaGetter.WorldModel?.Male?.File != null) meshPaths.Add(aaGetter.WorldModel.Male.File);
        if (aaGetter.WorldModel?.Female?.File != null) meshPaths.Add(aaGetter.WorldModel.Female.File);
        if (!aaGetter.SkinTexture?.Male.IsNull ?? false)
            GetTextureSetPaths(aaGetter.SkinTexture.Male, correspondingModKeys, texturePaths);
        if (!aaGetter.SkinTexture?.Female.IsNull ?? false)
            GetTextureSetPaths(aaGetter.SkinTexture.Female, correspondingModKeys, texturePaths);
    }

    private void GetTextureSetPaths(IFormLinkGetter<ITextureSetGetter> txstLink, IEnumerable<ModKey> correspondingModKeys,  HashSet<string> texturePaths)
    {
        /* Implementation remains the same */
        if (txstLink.IsNull ||
            !_recordHandler.TryGetRecordFromMods(txstLink, correspondingModKeys, RecordLookupFallBack.Winner, out var txstGetterGeneric)) return;
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