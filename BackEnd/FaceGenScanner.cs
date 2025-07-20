using Mutagen.Bethesda;

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
    /// Asynchronously scans a MO2-style mod folder for face-gen meshes (.nif) and textures (.dds), including those in BSAs.
    /// This method offloads the synchronous file I/O to a background thread.
    /// </summary>
    /// <param name="modFolderPath">Root path of the mod being inspected.</param>
    /// <param name="bsaHandler">The BsaHandler instance to scan archives.</param>
    /// <param name="modKeys">The plugin keys associated with the mod being scanned.</param>
    /// <returns>
    /// A Task that represents the asynchronous operation. The task result contains the
    /// discovered files and flags indicating what was found.
    /// </returns>
    public static Task<FaceGenScanResult> CollectFaceGenFilesAsync(string modFolderPath, BsaHandler bsaHandler, IEnumerable<ModKey> modKeys, GameRelease gameRelease)
    {
        // Task.Run is used to offload the synchronous I/O operations to a thread pool thread.
        // This prevents blocking the calling thread, which is crucial for responsive UIs.
        return Task.Run(() =>
        {
            var filesDict = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            bool meshesExist = false;
            bool texturesExist = false;

            // Process loose files
            ProcessDirectory(Path.Combine(modFolderPath, "meshes", "actors", "character", "facegendata", "facegeom"), "*.nif", ref meshesExist, filesDict);
            ProcessDirectory(Path.Combine(modFolderPath, "textures", "actors", "character", "facegendata", "facetint"), "*.dds", ref texturesExist, filesDict);

            // Process files within BSAs
            ProcessBsaContents(bsaHandler, modKeys, modFolderPath, gameRelease, ref meshesExist, ref texturesExist, filesDict);

            // Return the results wrapped in a result object.
            return new FaceGenScanResult(filesDict, meshesExist, texturesExist);
        });
    }

    /// <summary>
    /// Processes a given directory for loose files matching a pattern.
    /// </summary>
    private static void ProcessDirectory(string root, string pattern, ref bool flag, Dictionary<string, HashSet<string>> filesDict)
    {
        // Since this runs on a background thread, synchronous Directory.Exists is acceptable.
        if (!Directory.Exists(root)) return;

        // Enumerate subdirectories for each plugin.
        foreach (string pluginDir in Directory.EnumerateDirectories(root))
        {
            // Find all files matching the pattern in the current plugin directory.
            var matches = Directory.EnumerateFiles(pluginDir, pattern, SearchOption.TopDirectoryOnly);
            if (!matches.Any()) continue;

            // If we found any files, set the corresponding flag to true.
            flag = true;

            // Extract the plugin name from the directory path.
            string pluginName = Path.GetFileName(pluginDir);

            // Get or create the HashSet for the current plugin.
            if (!filesDict.TryGetValue(pluginName, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                filesDict[pluginName] = set;
            }

            // Add the base file name (without extension) to the set.
            foreach (string f in matches)
            {
                set.Add(Path.GetFileNameWithoutExtension(f));
            }
        }
    }

    /// <summary>
    /// Processes BSA contents for facegen files.
    /// </summary>
    private static void ProcessBsaContents(BsaHandler bsaHandler, IEnumerable<ModKey> modKeys, string modFolderPath, GameRelease gameRelease, ref bool meshesExist, ref bool texturesExist, Dictionary<string, HashSet<string>> filesDict)
    {
        var bsaContents = bsaHandler.GetAllFilePathsForMod(modKeys, new HashSet<string>() {modFolderPath}, gameRelease); // You will need to implement this method in BsaHandler

        foreach (var entry in bsaContents)
        {
            var modKey = entry.Key;
            string? pluginName = null;
            string? fileToAdd = null;
            
            string faceGeomPrefix = $"meshes\\actors\\character\\facegendata\\facegeom\\{modKey.FileName}\\";
            string faceTintPrefix = $"textures\\actors\\character\\facegendata\\facetint\\{modKey.FileName}\\";
            
            foreach (var filePath in entry.Value)
            {
                if (filePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) && filePath.StartsWith(faceGeomPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    meshesExist = true;
                    pluginName = modKey.FileName;
                    fileToAdd = Path.GetFileNameWithoutExtension(filePath);
                }
                else if (filePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && filePath.StartsWith(faceTintPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    texturesExist = true;
                    pluginName = modKey.FileName;
                    fileToAdd = Path.GetFileNameWithoutExtension(filePath);
                }

                if (pluginName != null && fileToAdd != null)
                {
                    if (!filesDict.TryGetValue(pluginName, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        filesDict[pluginName] = set;
                    }
                    set.Add(fileToAdd);
                }
            }
        }
        return;
    }
}