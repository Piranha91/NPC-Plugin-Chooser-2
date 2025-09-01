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
        // Path examples:
        // Meshes/actors/character/facegendata/facegeom/Skyrim.esm/00012345.nif
        // Textures/actors/character/facegendata/facetint/Skyrim.esm/00012345.dds
        
        // The plugin name is the name of the parent directory.
        string? pluginName = new FileInfo(filePath.Replace('/', Path.DirectorySeparatorChar)).Directory?.Name;
        if (string.IsNullOrWhiteSpace(pluginName)) return;

        string fileId = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(fileId)) return;

        // Update flags based on file extension
        if (filePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) meshesExist = true;
        else if (filePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) texturesExist = true;

        // Add the file ID to the dictionary
        if (!filesDict.TryGetValue(pluginName, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            filesDict[pluginName] = set;
        }
        set.Add(fileId);
    }
}