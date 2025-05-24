namespace NPC_Plugin_Chooser_2.BackEnd;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class FaceGenScanner
{
    /// <summary>
    /// Scans a MO2-style mod folder for face-gen meshes (.nif) and textures (.dds).
    /// </summary>
    /// <param name="modFolderPath">Root path of the mod being inspected.</param>
    /// <param name="faceGenFiles">
    ///     OUT: Dictionary whose keys are the immediate sub-folder names under
    ///     facegeom/facetint (e.g. "Skyrim.esm") and whose values are HashSets
    ///     containing the base file names (no extension) found there.
    /// </param>
    /// <param name="meshesExist">OUT: true if at least one *.nif exists.</param>
    /// <param name="texturesExist">OUT: true if at least one *.dds exists.</param>
    /// <returns>
    ///     True when <paramref name="meshesExist"/> || <paramref name="texturesExist"/>.
    /// </returns>
    public static bool CollectFaceGenFiles(
        string modFolderPath,
        out Dictionary<string, HashSet<string>> faceGenFiles,
        out bool meshesExist,
        out bool texturesExist)
    {
        // Build results in a *local* variable first
        var filesDict = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        meshesExist   = false;
        texturesExist = false;

        // Re-usable helper
        void Process(string root, string pattern, ref bool flag)
        {
            if (!Directory.Exists(root)) return;

            foreach (string pluginDir in Directory.EnumerateDirectories(root))
            {
                var matches = Directory.EnumerateFiles(pluginDir, pattern, SearchOption.TopDirectoryOnly);
                if (!matches.Any()) continue;

                flag = true;                                         // mark that we found something

                string pluginName = Path.GetFileName(pluginDir);
                if (!filesDict.TryGetValue(pluginName, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    filesDict[pluginName] = set;
                }

                foreach (string f in matches)
                    set.Add(Path.GetFileNameWithoutExtension(f));
            }
        }

        // Canonical roots
        string meshRoot = Path.Combine(modFolderPath, "meshes", "actors", "character",
                                       "facegendata", "facegeom");
        string texRoot  = Path.Combine(modFolderPath, "textures", "actors", "character",
                                       "facegendata", "facetint");

        // Scan both trees
        Process(meshRoot, "*.nif",  ref meshesExist);
        Process(texRoot,  "*.dds",  ref texturesExist);

        // Expose the dictionary through the out parameter *after* scanning
        faceGenFiles = filesDict;

        return meshesExist || texturesExist;
    }
}
