using nifly;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class NifHandler
{
    public static HashSet<string> GetExtraTexturesFromNif(string nifPath)
    {
        HashSet<string> ExtraTextures = new HashSet<string>();
        using (NifFile nif = new NifFile())
        {
            nif.Load(nifPath);
            ExtraTextures = new niflycpp.TextureFinder(nif.GetHeader()).UniqueTextures.ToHashSet<string>();
            return RemoveTopFolderFromPath(ExtraTextures, "textures");
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