using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class AssetHandler
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly BsaHandler _bsaHandler;
    private readonly Lazy<VM_Run> _runVM;

    private bool _copyExtraAssets = true;
    private bool _copyExtraTexturesInNifs = true;
    private bool _getMissingExtraAssetsFromAvailableWinners = true;
    private bool _suppressAllMissingFileWarnings = false;
    private bool _suppressKnownMissingFileWarnings = true;

    private HashSet<string> _pathsToIgnore = new();

    private Dictionary<string, HashSet<string>>
        _warningsToSuppress = new(); // Key: Plugin Name (lowercase), Value: Set of paths

    private HashSet<string> _warningsToSuppress_Global = new();

    public AssetHandler(EnvironmentStateProvider environmentStateProvider, BsaHandler bsaHandler,
        Lazy<VM_Run> runVM)
    {
        _environmentStateProvider = environmentStateProvider;
        _bsaHandler = bsaHandler;
        _runVM = runVM;
    }

    public void Initialize()
    {
        LoadAuxiliaryFiles();
    }

    public async Task CopyAssetLinkFiles(List<IAssetLinkGetter> assetLinks, ModSetting appearanceModSetting,
        string outputBasePath)
    {
        var assetRelPaths = assetLinks
            .Select(x => x.GivenPath)
            .Distinct()
            .Select(x => Auxilliary.AddTopFolderByExtension(x))
            .ToHashSet();

        List<string> autoPredictedRelPaths = new();
        AddCorrespondingNumericalNifPaths(assetRelPaths, autoPredictedRelPaths);

        Dictionary<string, string> loosePaths = new();
        Dictionary<string, IArchiveFile> bsaFiles = new();

        foreach (var relPath in assetRelPaths.Distinct())
        {
            bool found = false;
            foreach (var dirPath in appearanceModSetting.CorrespondingFolderPaths)
            {
                var candidatePath = Path.Combine(dirPath, relPath);
                if (File.Exists(candidatePath))
                {
                    loosePaths.Add(relPath, candidatePath);
                    found = true;
                    break;
                }

                foreach (var plugin in appearanceModSetting.CorrespondingModKeys)
                {
                    var readers = _bsaHandler.OpenBsaArchiveReaders(dirPath, plugin);
                    if (_bsaHandler.TryGetFileFromReaders(relPath, readers, out var file) && file != null)
                    {
                        bsaFiles.Add(relPath, file);
                        found = true;
                        break;
                    }
                }

                if (found) break;
            }

            if (found) continue;
        }


        // copy the files
        foreach (var entry in loosePaths)
        {
            var outputPath = Path.Combine(outputBasePath, entry.Key);
            var sourcePath = entry.Value;

            try
            {
                Auxilliary.CreateDirectoryIfNeeded(outputPath, Auxilliary.PathType.File);
                File.Copy(sourcePath, outputPath, true);
            }
            catch (Exception ex)
            {
                // pass
            }
        }

        // Extract the archives
        foreach (var entry in bsaFiles)
        {
            var outputPath = Path.Combine(outputBasePath, entry.Key);
            Auxilliary.CreateDirectoryIfNeeded(outputPath, Auxilliary.PathType.File);
            _bsaHandler.ExtractFileFromBsa(entry.Value, outputPath);
        }
    }

    /// <summary>
    /// Main asset copying orchestrator. Calls helpers for identification and copying.
    /// Handles scenarios where only FaceGen assets should be copied.
    /// </summary>
    /// <param name="appearanceNpcRecord">The NPC record being added/modified in the output patch.</param>
    /// <param name="appearanceModSetting">The ModSetting chosen for the selected NPC.</param>
    public async Task CopyNpcAssets(
        INpcGetter appearanceNpcRecord,
        ModSetting appearanceModSetting,
        ModKey appearancePluginKey,
        string outputBasePath,
        string modsFolderPath)
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

        // 2. Extra Assets (Only if CopyExtraAssets is true)
        List<string> autoPredictedExtraAssetRelPaths = new();
        if (_copyExtraAssets)
        {
            _runVM.Value.AppendLog(
                $"      Identifying extra assets referenced by plugin record {appearanceNpcRecord.FormKey}..."); // Verbose only
            GetAssetsReferencedByPlugin(appearanceNpcRecord, meshToCopyRelativePaths, textureToCopyRelativePaths);
            AddCorrespondingNumericalNifPaths(meshToCopyRelativePaths, autoPredictedExtraAssetRelPaths);
        }
        else if (!_copyExtraAssets)
        {
            _runVM.Value.AppendLog(
                $"      Skipping extra asset identification (CopyExtraAssets disabled)."); // Verbose only
        }

        // --- Handle BSAs ---
        HashSet<string> extractedMeshFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> extractedTextureFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _runVM.Value.AppendLog(
            $"      Checking BSAs associated with plugin key {baseModKey.FileName} in source directories..."); // Verbose only
        await Task.Run(() =>
            UnpackAssetsFromBSA(meshToCopyRelativePaths, textureToCopyRelativePaths,
                extractedMeshFiles, extractedTextureFiles,
                appearancePluginKey, assetSourceDirs, outputBasePath));
        _runVM.Value.AppendLog(
            $"      Extracted {extractedMeshFiles.Count} meshes and {extractedTextureFiles.Count} textures from BSAs."); // Verbose only

        handledRelativePaths.UnionWith(extractedMeshFiles);
        handledRelativePaths.UnionWith(extractedTextureFiles);

        // --- Handle NIF Scanning ---
        // Scan NIFs if CopyExtraAssets OR copyOnlyFaceGenAssets (because FaceGen NIF itself needs scanning) AND FindExtraTexturesInNifs is true
        if (_copyExtraAssets && _copyExtraTexturesInNifs)
        {
            _runVM.Value.AppendLog($"      Scanning NIF files for additional texture references..."); // Verbose only
            HashSet<string> texturesFromNifsRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> alreadyDetectedTextures =
                new HashSet<string>(textureToCopyRelativePaths, StringComparer.OrdinalIgnoreCase);
            alreadyDetectedTextures.UnionWith(extractedTextureFiles);

            // Scan loose NIFs (check all source dirs)
            GetExtraTexturesFromNifSet(meshToCopyRelativePaths, assetSourceDirs, texturesFromNifsRelativePaths,
                alreadyDetectedTextures);
            // Scan extracted NIFs (check output dir)
            GetExtraTexturesFromNifSet(extractedMeshFiles, new List<string> { outputBasePath },
                texturesFromNifsRelativePaths, alreadyDetectedTextures);

            _runVM.Value.AppendLog(
                $"        Found {texturesFromNifsRelativePaths.Count} additional textures in NIFs."); // Verbose only

            // Try extracting newly found textures from BSAs
            if (texturesFromNifsRelativePaths.Any())
            {
                HashSet<string> newlyExtractedNifTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                UnpackAssetsFromBSA(new HashSet<string>(), texturesFromNifsRelativePaths, new HashSet<string>(),
                    newlyExtractedNifTextures, appearancePluginKey, assetSourceDirs, outputBasePath);
                _runVM.Value.AppendLog(
                    $"        Extracted {newlyExtractedNifTextures.Count} of these additional textures from BSAs."); // Verbose only
                texturesFromNifsRelativePaths.ExceptWith(newlyExtractedNifTextures);
                handledRelativePaths.UnionWith(newlyExtractedNifTextures);
            }

            textureToCopyRelativePaths.UnionWith(texturesFromNifsRelativePaths);
        }
        else
        {
            _runVM.Value.AppendLog($"      Skipping NIF scanning for textures."); // Verbose only
        }


        // --- Copy Loose Files ---
        _runVM.Value.AppendLog($"      Copying {meshToCopyRelativePaths.Count} loose mesh files..."); // Verbose only
        var texResultStatus = CopyAssetFiles(assetSourceDirs, meshToCopyRelativePaths, "Meshes",
            baseModKey.FileName.String, autoPredictedExtraAssetRelPaths, outputBasePath);

        _runVM.Value.AppendLog(
            $"      Copying {textureToCopyRelativePaths.Count} loose texture files..."); // Verbose only
        var meshResultStatus = CopyAssetFiles(assetSourceDirs, textureToCopyRelativePaths, "Textures",
            baseModKey.FileName.String, autoPredictedExtraAssetRelPaths, outputBasePath);

        handledRelativePaths.UnionWith(texResultStatus.Where(x => x.Value == true).Select(x => x.Key));
        handledRelativePaths.UnionWith(meshResultStatus.Where(x => x.Value == true).Select(x => x.Key));

        // Make sure facegen has been copied. If not, try to source it from the original record.
        bool faceGenTexCopied = handledRelativePaths.Contains(faceTexRelativePath);
        bool faceGenMeshCopied = handledRelativePaths.Contains(faceMeshRelativePath);

        if (!faceGenTexCopied)
        {
            faceGenTexCopied = CopySourceFaceGen(faceTexRelativePath, outputBasePath, "Textures", assetSourceDirs,
                appearanceNpcRecord.FormKey.ModKey, modsFolderPath, _runVM.Value.AppendLog);
        }

        if (!faceGenMeshCopied)
        {
            faceGenMeshCopied = CopySourceFaceGen(faceMeshRelativePath, outputBasePath, "Meshes", assetSourceDirs,
                appearanceNpcRecord.FormKey.ModKey, modsFolderPath, _runVM.Value.AppendLog);
        }

        if (!faceGenTexCopied)
        {
            _runVM.Value.AppendLog($"ERROR: Failed to find any FaceGen texture: {faceTexRelativePath}", true);
        }

        if (!faceGenMeshCopied)
        {
            _runVM.Value.AppendLog($"ERROR: Failed to find any FaceGen mesh: {faceMeshRelativePath}", true);
        }

        _runVM.Value.AppendLog(
            $"    Finished asset copying for {appearanceNpcRecord.EditorID ?? appearanceNpcRecord.FormKey.ToString()}."); // Verbose only
    }


    // --- Asset Identification Helpers (No changes needed here) ---
    private void GetAssetsReferencedByPlugin(INpcGetter npc, HashSet<string> meshPaths,
        HashSet<string> texturePaths)
    {
        /* Implementation remains the same */
        if (npc.HeadParts != null)
            foreach (var hpLink in npc.HeadParts)
                GetHeadPartAssetPaths(hpLink, texturePaths, meshPaths);
        if (!npc.WornArmor.IsNull &&
            npc.WornArmor.TryResolve(_environmentStateProvider.LinkCache, out var wornArmorGetter) &&
            wornArmorGetter.Armature != null)
            foreach (var aaLink in wornArmorGetter.Armature)
                GetARMAAssetPaths(aaLink, texturePaths, meshPaths);
    }

    private void GetHeadPartAssetPaths(IFormLinkGetter<IHeadPartGetter> hpLink, HashSet<string> texturePaths,
        HashSet<string> meshPaths)
    {
        /* Implementation remains the same */
        if (hpLink.IsNull || !hpLink.TryResolve(_environmentStateProvider.LinkCache, out var hpGetter)) return;
        if (hpGetter.Model?.File != null) meshPaths.Add(hpGetter.Model.File);
        if (hpGetter.Parts != null)
            foreach (var part in hpGetter.Parts)
                if (part?.FileName != null)
                    meshPaths.Add(part.FileName);
        if (!hpGetter.TextureSet.IsNull) GetTextureSetPaths(hpGetter.TextureSet, texturePaths);
        if (hpGetter.ExtraParts != null)
            foreach (var extraPartLink in hpGetter.ExtraParts)
                GetHeadPartAssetPaths(extraPartLink, texturePaths, meshPaths);
    }

    private void GetARMAAssetPaths(IFormLinkGetter<IArmorAddonGetter> aaLink, HashSet<string> texturePaths,
        HashSet<string> meshPaths)
    {
        /* Implementation remains the same */
        if (aaLink.IsNull || !aaLink.TryResolve(_environmentStateProvider.LinkCache, out var aaGetter)) return;
        if (aaGetter.WorldModel?.Male?.File != null) meshPaths.Add(aaGetter.WorldModel.Male.File);
        if (aaGetter.WorldModel?.Female?.File != null) meshPaths.Add(aaGetter.WorldModel.Female.File);
        if (!aaGetter.SkinTexture?.Male.IsNull ?? false)
            GetTextureSetPaths(aaGetter.SkinTexture.Male, texturePaths);
        if (!aaGetter.SkinTexture?.Female.IsNull ?? false)
            GetTextureSetPaths(aaGetter.SkinTexture.Female, texturePaths);
    }

    private void GetTextureSetPaths(IFormLinkGetter<ITextureSetGetter> txstLink, HashSet<string> texturePaths)
    {
        /* Implementation remains the same */
        if (txstLink.IsNull ||
            !txstLink.TryResolve(_environmentStateProvider.LinkCache, out var txstGetter)) return;
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
    /// Attempts to copy facegen from a mod or plugin's source
    /// </summary>
    ///
    private bool CopySourceFaceGen(string relativePath, string outputDirPath,
        string assetType /*"Meshes" or "Textures"*/, List<string> assetSourceDirs, ModKey baseNpcPlugin,
        string modsFolderPath, Action<string, bool, bool>? log = null)
    {
        string dataRelativePath = Path.Combine(assetType, relativePath);
        string destPath = Path.Combine(outputDirPath, dataRelativePath);

        // try to find the missing FaceGen in a BSA corresponding to the base NPC record
        var directoriesToQueryForBsa = assetSourceDirs.And(_environmentStateProvider.DataFolderPath.Path);

        foreach (var dir in directoriesToQueryForBsa)
        {
            if (_bsaHandler.DirectoryHasCorrespondingBsaFile(dir, baseNpcPlugin))
            {
                var readers = _bsaHandler.OpenBsaArchiveReaders(dir, baseNpcPlugin);

                if (_bsaHandler.TryGetFileFromReaders(dataRelativePath, readers, out IArchiveFile? archiveFile) &&
                    archiveFile != null &&
                    _bsaHandler.ExtractFileFromBsa(archiveFile, destPath))
                {
                    return true;
                }
            }
        }

        // try to find the missing FaceGen in the mods folder where the base NPC plugin lives
        var candidateDirectories = GetContainingSubdirectories(modsFolderPath, baseNpcPlugin.FileName);
        foreach (var dir in candidateDirectories)
        {
            var candidatePath = System.IO.Path.Combine(dir, dataRelativePath);
            if (File.Exists(candidatePath))
            {
                List<string> sourceDirAsList = new() { dir };
                HashSet<string> relativePathAsSet = new() { relativePath };
                var status = CopyAssetFiles(sourceDirAsList, relativePathAsSet, assetType,
                    baseNpcPlugin.FileName, new HashSet<string>(), outputDirPath);
                if (status[relativePath] == true)
                {
                    return true;
                }
            }
        }

        return false;
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
    /// Extracts assets from BSAs found in any of the assetSourceDirs, prioritizing later directories.
    /// </summary>
    private void UnpackAssetsFromBSA(
        HashSet<string> MeshesToExtract, HashSet<string> TexturesToExtract,
        HashSet<string> extractedMeshes, HashSet<string> extractedTextures,
        ModKey currentPluginKey, List<string> assetSourceDirs, string targetAssetPath)
    {
        if (!assetSourceDirs.Any()) return;

        var foundMeshSources =
            new Dictionary<string, (IArchiveFile file, string dest)>(StringComparer.OrdinalIgnoreCase);
        var foundTextureSources =
            new Dictionary<string, (IArchiveFile file, string dest)>(StringComparer.OrdinalIgnoreCase);

        // Iterate source directories *backwards*
        for (int i = assetSourceDirs.Count - 1; i >= 0; i--)
        {
            string sourceDir = assetSourceDirs[i];
            var readers =
                _bsaHandler.OpenBsaArchiveReaders(sourceDir, currentPluginKey);
            if (!readers.Any()) continue;

            // Check remaining meshes
            foreach (string subPath in MeshesToExtract.ToList())
            {
                if (foundMeshSources.ContainsKey(subPath)) continue;

                string bsaMeshPath = Path.Combine("meshes", subPath).Replace('/', '\\');
                // *** Use HaveFile here ***
                if (_bsaHandler.HaveFile(bsaMeshPath, readers, out var file) && file != null)
                {
                    foundMeshSources[subPath] = (file, Path.Combine(targetAssetPath, "meshes", subPath));
                }
            }

            // Check remaining textures
            foreach (string subPath in TexturesToExtract.ToList())
            {
                if (foundTextureSources.ContainsKey(subPath)) continue;

                string bsaTexPath = Path.Combine("textures", subPath).Replace('/', '\\');
                // *** Use HaveFile here ***
                if (_bsaHandler.HaveFile(bsaTexPath, readers, out var file) && file != null)
                {
                    foundTextureSources[subPath] = (file, Path.Combine(targetAssetPath, "textures", subPath));
                }
            }
        }

        // Extract winning sources
        foreach (var kvp in foundMeshSources)
        {
            string subPath = kvp.Key;
            if (_bsaHandler.ExtractFileFromBsa(kvp.Value.file,
                    kvp.Value.dest)) // Assumes ExtractFileFromBSA returns bool
            {
                extractedMeshes.Add(subPath);
                MeshesToExtract.Remove(subPath);
            }
            else
            {
                _runVM.Value.AppendLog($"ERROR: Failed to extract winning BSA mesh: {subPath}", true);
            }
        }

        foreach (var kvp in foundTextureSources)
        {
            string subPath = kvp.Key;
            if (_bsaHandler.ExtractFileFromBsa(kvp.Value.file,
                    kvp.Value.dest)) // Assumes ExtractFileFromBSA returns bool
            {
                extractedTextures.Add(subPath);
                TexturesToExtract.Remove(subPath);
            }
            else
            {
                _runVM.Value.AppendLog($"ERROR: Failed to extract winning BSA texture: {subPath}", true);
            }
        }
    }


    /// <summary>
    /// Scans NIFs found in the source directories for textures.
    /// **Revised for Clarification 2.**
    /// </summary>
    private void GetExtraTexturesFromNifSet(HashSet<string> nifSubPaths, List<string> sourceBaseDirs,
        HashSet<string> outputTextures, HashSet<string> ignoredTextures)
    {
        int foundCount = 0;
        foreach (var nifPathRelative in nifSubPaths)
        {
            if (!nifPathRelative.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) continue;

            string? foundNifFullPath = null;
            // Iterate source dirs to find the NIF file
            foreach (var baseDir in sourceBaseDirs)
            {
                string potentialPath = Path.Combine(baseDir, "meshes", nifPathRelative);
                if (File.Exists(potentialPath))
                {
                    foundNifFullPath = potentialPath;
                    break; // Found it
                }
            }

            if (foundNifFullPath != null)
            {
                try
                {
                    var nifTextures = NifHandler.GetExtraTexturesFromNif(foundNifFullPath);
                    foreach (var texPathRelative in nifTextures)
                    {
                        if (!ignoredTextures.Contains(texPathRelative) &&
                            !IsIgnored(texPathRelative, _pathsToIgnore))
                        {
                            if (outputTextures.Add(texPathRelative)) foundCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _runVM.Value.AppendLog(
                        $"        WARNING: Failed to scan NIF '{foundNifFullPath}': {ExceptionLogger.GetExceptionStack(ex)}");
                } // Verbose only (Warning)
            }
        }
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
    public static void AddCorrespondingNumericalNifPaths(HashSet<string> relativeNifPaths, List<string> addedRelPaths)
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
                relativeNifPaths.Add(replacedPath);
                addedRelPaths.Add(replacedPath);
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