using Microsoft.IO;
using nifly;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class NifHandler
{
    public static HashSet<string> GetExtraTexturesFromNif(string nifPath)
    {
        var debug = File.Exists(nifPath);
        HashSet<string> uniqueTextures = new HashSet<string>();
        using (NifFile nif = new NifFile())
        {
            nif.Load(nifPath);
            // Assume 'niFile' is your loaded NIF file object and niFile.Header is the NiHeader.
            NiHeader header = nif.GetHeader();

            var blockCount = header.GetNumBlocks();
            for (uint id = 0; id < blockCount; id++)
            {
                NiObject block = header.GetBlockById(id);
                if (block is BSShaderTextureSet textureSet)
                {
                    // Access the texture paths safely
                    using var texturesArray = textureSet.textures;           // textures is a container (e.g. NiTArray<NiString>)
                    using var textureItems = texturesArray.items();          // items() gives an enumerable collection of NiString
                    foreach (NiString tex in textureItems)
                    {
                        if (tex != null)
                        {
                            string path = tex.get();  // Get the actual string from NiString
                            if (!string.IsNullOrEmpty(path))
                            {
                                uniqueTextures.Add(path);
                            }
                        }
                        // (NiString will be disposed at end of using scope if required)
                    }
                }
            }
            return uniqueTextures;
        }
    }

    // Textures in Nifs are referenced from Textures folder, whereas in plugins they are referenced from WITHIN the Textures folder. This function is made to remove "textures\\" from nif-derived texture paths
    public static HashSet<string> RemoveTopFolderFromPath(HashSet<string> inputs, string topFolderName)
    {
        HashSet<string> output = new HashSet<string>();

        string topFolderSlashed = topFolderName + "\\";
        int removeLength = topFolderSlashed.Length;

        foreach (string s in inputs)
        {
            if (s.ToLower().IndexOf(topFolderSlashed.ToLower()) == 0)
            {
                output.Add(s.Remove(0, removeLength));
            }
        }

        return output;
    }
}