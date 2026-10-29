using System.IO;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Detects an N.P.C.2 install that lives inside the game's Data folder or the mod manager's Mods
/// folder. N.P.C.2 keeps Settings.json (and its logs and caches) next to the exe. Installed as an MO2
/// mod, the exe runs from the virtual Data folder, so Settings.json IS Data\Settings.json — the same
/// path other tools installed as mods use (Pandora). Whichever mod wins the conflict has its file
/// overwritten by the other tool's saves, and N.P.C.2's own settings can be replaced the same way.
/// </summary>
public static class InstallLocationCheck
{
    /// <summary>
    /// Returns a warning for the user if <paramref name="exeDirectory"/> is inside
    /// <paramref name="dataFolder"/> or <paramref name="modsFolder"/>; otherwise null.
    /// Blank or unparseable folders are ignored.
    /// </summary>
    public static string? GetWarning(string exeDirectory, string? dataFolder, string? modsFolder)
    {
        string? where =
            IsInside(exeDirectory, dataFolder) ? $"the game's Data folder ({dataFolder})" :
            IsInside(exeDirectory, modsFolder) ? $"your Mods folder ({modsFolder})" :
            null;
        if (where == null) return null;

        return $"N.P.C.2 is running from inside {where}:\n{exeDirectory}\n\n" +
               "N.P.C.2 saves Settings.json and its logs next to its .exe. Installed as a mod, those files land in " +
               "the game's (virtual) Data folder, where other tools installed the same way use the same file " +
               "names — Pandora's Settings.json, for example. The tools then overwrite each other's settings, and " +
               "N.P.C.2's selections can be lost.\n\n" +
               "Move the N.P.C.2 folder somewhere outside the game and the Mods folder (for example C:\\Modding\\NPC2), " +
               "copy your Settings.json into the new folder, and point your mod manager's executable entry at the new " +
               "location.";
    }

    internal static bool IsInside(string path, string? folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            string child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
