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
    private HashSet<(string sourcePath, string destinationPath, string requestingModName)> _fileCopyQueue = new();
    
    private HashSet<string> _pathsToIgnore = new();

    private Dictionary<string, HashSet<string>>
        _warningsToSuppress = new(); // Key: Plugin Name (lowercase), Value: Set of paths

    private HashSet<string> _warningsToSuppress_Global = new();

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

    public async Task ScheduleCopyAssetLinkFiles(List<IAssetLinkGetter> assetLinks, ModSetting appearanceModSetting,
        string outputBasePath)
    {
        var assetRelPaths =
            assetLinks
                .Select(x => x.GivenPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(RegularizeOrNull)
                .Where(rel => rel is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> autoPredictedRelPaths = new();
        AddCorrespondingNumericalNifPaths(assetRelPaths, autoPredictedRelPaths);

        Dictionary<string, string> loosePaths = new(); // rel path : full path
        Dictionary<string, string> bsaFiles = new(); // rel path : bsa file path

        foreach (var relPath in assetRelPaths.Distinct())
        {
            bool found = false;
            foreach (var dirPath in appearanceModSetting.CorrespondingFolderPaths)
            {
                var candidatePath = Path.Combine(dirPath, relPath);
                if (FileExists(candidatePath, appearanceModSetting.DisplayName, dirPath))
                {
                    loosePaths.Add(relPath, candidatePath);
                    found = true;
                    break;
                }

                foreach (var plugin in appearanceModSetting.CorrespondingModKeys)
                {
                    var readers = _bsaHandler.OpenBsaArchiveReaders(dirPath, plugin);
                    if (_bsaHandler.FileExists(relPath, plugin, out string? bsaPath) && bsaPath != null)
                    {
                        bsaFiles.Add(relPath, bsaPath);
                        found = true;
                        break;
                    }
                }

                if (found) break;
            }

            if (found) continue;
        }
        
        foreach (var entry in loosePaths)
        {
            var outputPath = Path.Combine(outputBasePath, entry.Key);
            QueueFileForCopy(entry.Value, outputPath, appearanceModSetting.DisplayName);
        }
  
        foreach (var entry in bsaFiles)
        {
            var outputPath = Path.Combine(outputBasePath, entry.Key);
            _bsaHandler.QueueFileForExtraction(entry.Value, entry.Key, outputPath, appearanceModSetting.DisplayName);
        }
    }

    /// <summary>
    /// Main asset copying orchestrator. Calls helpers for identification and copying.
    /// Handles scenarios where only FaceGen assets should be copied.
    /// </summary>
    /// <param name="appearanceNpcRecord">The NPC record being added/modified in the output patch.</param>
    /// <param name="appearanceModSetting">The ModSetting chosen for the selected NPC.</param>
    public async Task SchduleCopyNpcAssets(
        INpcGetter appearanceNpcRecord,
        ModSetting appearanceModSetting,
        string outputBasePath)
    {
        _runVM.Value.AppendLog(
            $"    Copying assets for {appearanceNpcRecord.EditorID ?? appearanceNpcRecord.FormKey.ToString()} from sources related to '{appearanceModSetting.DisplayName}'..."); // Verbose only

        var assetSourceDirs = appearanceModSetting.CorrespondingFolderPaths ?? new List<string>();
        if (!assetSourceDirs.Any())
        {
            _runVM.Value.AppendLog(
                $"      WARNING: Mod Setting '{appearanceModSetting.DisplayName}' has no Corresponding Folder Paths. Cannot copy assets."); // Verbose only (Warning)
            return;
        }

        _runVM.Value.AppendLog($"      Asset source directories: {string.Join(", ", assetSourceDirs)}"); // Verbose only

        // --- Identify Required Assets ---
        HashSet<string> meshToCopyRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> textureToCopyRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> handledRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseModKey = appearanceNpcRecord.FormKey.ModKey;

        // 1. FaceGen Assets (Always check if requested, templating handled by caller passing null npcForExtraAssetLookup)
        // Construct FaceGen paths using the provided key
        var (faceMeshRelativePath, faceTexRelativePath) =
            Auxilliary.GetFaceGenSubPathStrings(appearanceNpcRecord.FormKey); // Use the keyForFaceGenPath here!
        meshToCopyRelativePaths.Add(faceMeshRelativePath);
        textureToCopyRelativePaths.Add(faceTexRelativePath);
        _runVM.Value.AppendLog(
            $"      Identified FaceGen paths (using key {baseModKey.FileName}): {faceMeshRelativePath}, {faceTexRelativePath}"); // Verbose only

        // 2. Non-FaceGen Assets (Only if CopyExtraAssets is true)
        HashSet<string> autoPredictedExtraNifRelPaths = new();
        if (_copyExtraAssets)
        {
            _runVM.Value.AppendLog(
                $"      Identifying extra assets referenced by plugin record {appearanceNpcRecord.FormKey}..."); // Verbose only
            GetAssetPathsReferencedByPlugin(appearanceNpcRecord, appearanceModSetting.CorrespondingModKeys, meshToCopyRelativePaths, textureToCopyRelativePaths);
            AddCorrespondingNumericalNifPaths(meshToCopyRelativePaths, autoPredictedExtraNifRelPaths);
        }
        else if (!_copyExtraAssets)
        {
            _runVM.Value.AppendLog(
                $"      Skipping extra asset identification (CopyExtraAssets disabled)."); // Verbose only
        }
        
        // find assets in loose files
        var loosTexResultStatus = ScheduleCopyAssetFiles(assetSourceDirs, meshToCopyRelativePaths, appearanceModSetting.DisplayName, "Meshes",
            baseModKey.FileName.String, Array.Empty<string>(), outputBasePath);

        var looseMeshResultStatus = ScheduleCopyAssetFiles(assetSourceDirs, textureToCopyRelativePaths, appearanceModSetting.DisplayName, "Textures",
            baseModKey.FileName.String, autoPredictedExtraNifRelPaths, outputBasePath);

        handledRelativePaths.UnionWith(loosTexResultStatus.Where(x => x.Value == true).Select(x => x.Key));
        handledRelativePaths.UnionWith(looseMeshResultStatus.Where(x => x.Value == true).Select(x => x.Key));
        
        // find assets in BSAs
        meshToCopyRelativePaths = meshToCopyRelativePaths.Except(handledRelativePaths).ToHashSet();
        textureToCopyRelativePaths = textureToCopyRelativePaths.Except(handledRelativePaths).ToHashSet();
        autoPredictedExtraNifRelPaths = autoPredictedExtraNifRelPaths.Except(handledRelativePaths).ToHashSet();
        
        var bsaTexResultStatus = _bsaHandler.ScheduleExtractAssetFiles(textureToCopyRelativePaths, appearanceModSetting, "Textures", Array.Empty<string>(), outputBasePath);
        var bsaMeshResultStatus = _bsaHandler.ScheduleExtractAssetFiles(meshToCopyRelativePaths, appearanceModSetting, "Meshes", autoPredictedExtraNifRelPaths, outputBasePath);

        handledRelativePaths.UnionWith(bsaTexResultStatus.Where(x => x.Value == true).Select(x => x.Key));
        handledRelativePaths.UnionWith(bsaMeshResultStatus.Where(x => x.Value == true).Select(x => x.Key));
        
        meshToCopyRelativePaths = meshToCopyRelativePaths.Except(handledRelativePaths).ToHashSet();
        textureToCopyRelativePaths = textureToCopyRelativePaths.Except(handledRelativePaths).ToHashSet();

        if (meshToCopyRelativePaths.Any() || textureToCopyRelativePaths.Any())
        {
            AppendLog("Could not find the following asset paths to copy (this may or may not cause issues depending on how the Appearance Mod is set up");
            var files = String.Join(Environment.NewLine, meshToCopyRelativePaths.And(textureToCopyRelativePaths));
            AppendLog(files);
        }
    }

    public async Task CopyQueuedFiles(string outputRootFolder,  List<ModSetting> allModSettings, bool includeBsa = true, bool performNifTextureDetection = true)
    {
        Dictionary<string, HashSet<string>> extractedNifPaths = new(); // needed for followup texture search
        foreach (var pathInfo in _fileCopyQueue)
        {
            try
            {
                FileInfo fileInfo = Auxilliary.CreateDirectoryIfNeeded(pathInfo.destinationPath, Auxilliary.PathType.File);
                File.Copy(pathInfo.sourcePath, pathInfo.destinationPath, true);
                if (pathInfo.sourcePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                {
                    if (!extractedNifPaths.TryGetValue(pathInfo.requestingModName, out var modEntry))
                    {
                        modEntry = new HashSet<string>();
                        extractedNifPaths.Add(pathInfo.requestingModName, modEntry);
                    }
                    
                    modEntry.Add(pathInfo.destinationPath);
                }
            }
            catch (Exception ex)
            {
                AppendLog("Failed to copy file: " + pathInfo.sourcePath + " to " + pathInfo.destinationPath, true, true);
                AppendLog(ExceptionLogger.GetExceptionStack(ex));
            }
        }
        
        _fileCopyQueue.Clear();

        if (includeBsa)
        {
            extractedNifPaths = _bsaHandler.ExtractQueuedFiles(extractedNifPaths);
        }

        if (performNifTextureDetection)
        {
            foreach (var entry in extractedNifPaths)
            {
                var texturePathsFromNifs = new HashSet<string?>();
                var modName = entry.Key;
                var nifPathsForMod = entry.Value;
                var correspondingModEntry = allModSettings.FirstOrDefault(x => x.DisplayName == modName);
                if (correspondingModEntry == null)
                {
                    continue;
                }

                foreach (var nifPath in nifPathsForMod)
                {
                    var extraTexturePaths = NifHandler.GetExtraTexturesFromNif(nifPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(RegularizeOrNull)
                        .Where(rel => rel is not null)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);;
                    
                    texturePathsFromNifs.UnionWith(extraTexturePaths);
                }

                HashSet<string?> foundTexturePaths = new();

                // search loose files first
                foreach (var relativeTexPath in texturePathsFromNifs)
                {
                    if (relativeTexPath == null) continue;
                    foreach (var candidateDir in correspondingModEntry.CorrespondingFolderPaths)
                    {
                        var candidateSourcePath = Path.Combine(candidateDir, relativeTexPath);
                        if (FileExists(relativeTexPath, modName))
                        {
                            var destinationPath = Path.Combine(outputRootFolder, relativeTexPath);
                            foundTexturePaths.Add(relativeTexPath);
                            QueueFileForCopy(candidateSourcePath, destinationPath, modName);
                        }
                    }
                }
                texturePathsFromNifs.RemoveWhere(x => foundTexturePaths.Contains(x));
                
                // for any remaining search in BSA
                foundTexturePaths.Clear();
                foreach (var relativeTexPath in texturePathsFromNifs)
                {
                    if (relativeTexPath == null) continue;
                    if (_bsaHandler.FileExists(relativeTexPath, correspondingModEntry.CorrespondingModKeys,
                            out var foundModKey, out var foundBsaPath)
                        && foundBsaPath != null && foundModKey != null)
                    {
                        var destinationPath = Path.Combine(outputRootFolder, relativeTexPath);
                        foundTexturePaths.Add(relativeTexPath);
                        _bsaHandler.QueueFileForExtraction(foundBsaPath, relativeTexPath, destinationPath, modName);
                    }
                }
                texturePathsFromNifs.RemoveWhere(x => foundTexturePaths.Contains(x));

                if (texturePathsFromNifs.Any())
                {
                    AppendLog($"The following texture files were specified within the .nif files within {modName} but could not be found. This may or may not cause issues depending on how the mod is structured.");
                    AppendLog(string.Join(Environment.NewLine, texturePathsFromNifs));
                }
            }
            
            // now that extra textures have been collected, repeat the copy and extraction tasks
            await CopyQueuedFiles(outputRootFolder, allModSettings, includeBsa, false);
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
    /// Schedules copy operation for loose asset files, checking all source directories.
    /// </summary>
    private Dictionary<string, bool> ScheduleCopyAssetFiles(List<string> sourceDataDirPaths,
        HashSet<string> assetRelativePathList,
        string modName,
        string assetType /*"Meshes" or "Textures"*/, string sourcePluginName,
        IEnumerable<string> autoPredictedExtraPaths, string outputBaseDirPath)
    {
        Dictionary<string, bool> result = new();

        string outputBase = Path.Combine(outputBaseDirPath, assetType);

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
                if (FileExists(potentialPath, modName, sourcePathBase))
                {
                    foundSourcePath = potentialPath;
                    break; // Found it
                }
            }

            // Check Game Data Folder (if configured and not FaceGen)
            bool isFaceGen = relativePath.Contains("facegendata", StringComparison.OrdinalIgnoreCase);
            if (foundSourcePath == null && _getMissingExtraAssetsFromAvailableWinners && !isFaceGen)
            {
                string gameDataTrialPath = Path.Combine(_environmentStateProvider.DataFolderPath.ToString(), assetType,
                    relativePath);
                if (FileExists(gameDataTrialPath, GameDataKey, _environmentStateProvider.DataFolderPath))
                {
                    foundSourcePath = gameDataTrialPath;
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
                // Queue the file copy
                string destPath = Path.Combine(outputBase, relativePath);
                QueueFileForCopy(foundSourcePath, destPath, modName);
                result[relativePath] = true;
            }
        }

        return result;
    }

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

    public void QueueFileForCopy(string sourcePath, string destinationPath, string requestingModName)
    {
        _fileCopyQueue.Add((sourcePath, destinationPath, requestingModName));
    }
}

public class SuppressedWarnings
{
    public string Plugin { get; set; } = "";
    public HashSet<string> Paths { get; set; } = new HashSet<string>();
}