using Mutagen.Bethesda;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mutagen.Bethesda.Plugins;

/// <summary>
/// A container for the results of the asynchronous FaceGen scan.
/// </summary>
public class FaceGenScanResult
{
    /// <summary>
    /// Dictionary where keys are plugin names (e.g., "Skyrim.esm") and values are the set of facegen file names.
    /// </summary>
    public Dictionary<string, HashSet<string>> FaceGenFiles { get; }

    /// <summary>
    /// True if at least one mesh file (*.nif) was found.
    /// </summary>
    public bool MeshesExist { get; }

    /// <summary>
    /// True if at least one texture file (*.dds) was found.
    /// </summary>
    public bool TexturesExist { get; }

    /// <summary>
    /// True if either meshes or textures were found.
    /// </summary>
    public bool AnyFilesFound => MeshesExist || TexturesExist;

    public FaceGenScanResult(Dictionary<string, HashSet<string>> faceGenFiles, bool meshesExist, bool texturesExist)
    {
        FaceGenFiles = faceGenFiles;
        MeshesExist = meshesExist;
        TexturesExist = texturesExist;
    }
}

public static class FaceGenScanner
{
    private const string FaceGeom = "facegeom";
    private const string FaceTint = "facetint";
    
    /// <summary>
    /// Creates a FaceGenScanResult by processing pre-cached collections of file paths.
    /// This is a purely computational method with no file I/O.
    /// </summary>
    /// <param name="vmToProcess">The VM_ModSetting to generate the result for.</param>
    /// <param name="allFaceGenLooseFiles">The global cache of loose FaceGen files.</param>
    /// <param name="allFaceGenBsaFiles">The global cache of BSA-packed FaceGen files.</param>
    /// <returns>A FaceGenScanResult populated from the cached data.</returns>
    public static FaceGenScanResult CreateFaceGenScanResultFromCache(
        VM_ModSetting vmToProcess,
        Dictionary<string, HashSet<string>> allFaceGenLooseFiles,
        Dictionary<string, HashSet<string>> allFaceGenBsaFiles)
    {
        var filesDict = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        bool meshesExist = false;
        bool texturesExist = false;

        // 1. Process loose files from the cache
        foreach (var modPath in vmToProcess.CorrespondingFolderPaths)
        {
            if (allFaceGenLooseFiles.TryGetValue(modPath, out var looseFiles))
            {
                foreach (var filePath in looseFiles)
                {
                    ProcessFilePath(filePath, ref meshesExist, ref texturesExist, filesDict);
                }
            }
        }

        // 2. Process BSA files from the cache
        if (allFaceGenBsaFiles.TryGetValue(vmToProcess.DisplayName, out var bsaFiles))
        {
            foreach (var filePath in bsaFiles)
            {
                ProcessFilePath(filePath, ref meshesExist, ref texturesExist, filesDict);
            }
        }

        return new FaceGenScanResult(filesDict, meshesExist, texturesExist);
    }

    /// <summary>
    /// Helper method to parse a relative FaceGen file path and update the result collections.
    /// </summary>
    private static void ProcessFilePath(string filePath, ref bool meshesExist, ref bool texturesExist, Dictionary<string, HashSet<string>> filesDict)
    {
        // Normalize paths to forward slashes for consistent checking. 
        // VM_Mods caches loose files and BSA paths using '/', but we ensure safety here.
        string normalizedPath = filePath.Replace('\\', '/');

        // STRICT VALIDATION FIX:
        // 1. Identify file type
        bool isMesh = normalizedPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase);
        bool isTexture = normalizedPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);

        // 2. Enforce the required folder structure
        bool isValidMeshPath = isMesh && normalizedPath.Contains("meshes/actors/character/facegendata/facegeom", StringComparison.OrdinalIgnoreCase);
        bool isValidTexturePath = isTexture && normalizedPath.Contains("textures/actors/character/facegendata/facetint", StringComparison.OrdinalIgnoreCase);

        // If it doesn't match the strict criteria, ignore it immediately.
        if (!isValidMeshPath && !isValidTexturePath) 
        {
            return;
        }

        // 3. Extract Plugin Name (Parent Directory)
        // We handle this via string manipulation on the normalized path to avoid FileInfo overhead/OS inconsistencies.
        // Expected structure: .../facegeom/PluginName.esp/00001234.nif
        var segments = normalizedPath.Split('/');
        if (segments.Length < 2) return;

        string pluginName = segments[segments.Length - 2];
        string fileId = Path.GetFileNameWithoutExtension(normalizedPath);

        if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(fileId)) return;

        // 4. Update flags and dictionary
        if (isValidMeshPath) meshesExist = true;
        if (isValidTexturePath) texturesExist = true;

        if (!filesDict.TryGetValue(pluginName, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            filesDict[pluginName] = set;
        }
        set.Add(fileId);
    }
}