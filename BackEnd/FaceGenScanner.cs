namespace NPC_Plugin_Chooser_2.BackEnd;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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
    /// <summary>
    /// Asynchronously scans a MO2-style mod folder for face-gen meshes (.nif) and textures (.dds).
    /// This method offloads the synchronous file I/O to a background thread.
    /// </summary>
    /// <param name="modFolderPath">Root path of the mod being inspected.</param>
    /// <returns>
    /// A Task that represents the asynchronous operation. The task result contains the
    /// discovered files and flags indicating what was found.
    /// </returns>
    public static Task<FaceGenScanResult> CollectFaceGenFilesAsync(string modFolderPath)
    {
        // Task.Run is used to offload the synchronous I/O operations to a thread pool thread.
        // This prevents blocking the calling thread, which is crucial for responsive UIs.
        return Task.Run(() =>
        {
            var filesDict = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            bool meshesExist = false;
            bool texturesExist = false;

            // This local function processes a given directory for files matching a pattern.
            // It's defined within the lambda to capture the filesDict.
            void ProcessDirectory(string root, string pattern, ref bool flag)
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

            // Define the canonical paths for facegen meshes and textures.
            string meshRoot = Path.Combine(modFolderPath, "meshes", "actors", "character", "facegendata", "facegeom");
            string texRoot = Path.Combine(modFolderPath, "textures", "actors", "character", "facegendata", "facetint");

            // Scan both directory trees for the respective file types.
            ProcessDirectory(meshRoot, "*.nif", ref meshesExist);
            ProcessDirectory(texRoot, "*.dds", ref texturesExist);

            // Return the results wrapped in a result object.
            return new FaceGenScanResult(filesDict, meshesExist, texturesExist);
        });
    }
}
