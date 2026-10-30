using System.Diagnostics;
using System.IO;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd.SetupValidation;

/// <summary>
/// The setup wizard's per-page checks. Pure apart from reading (and, for writability, probing) the
/// file system, so each check is unit-testable on a temp folder without a game install. The
/// checks target setup mistakes users actually reported on the Nexus page — see
/// docs/SetupWizardPlan-2026-10.md.
/// </summary>
public static class SetupValidators
{
    private static readonly string[] PluginExtensions = { ".esp", ".esm", ".esl" };
    private static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };

    // ---------------------------------------------------------------- install location

    public static List<SetupFinding> InstallLocation(string exeDirectory, string? dataFolder, string? modsFolder,
        SetupModManager manager, bool runningUnderMo2)
    {
        var findings = new List<SetupFinding>();

        string? insideWarning = InstallLocationCheck.GetWarning(exeDirectory, dataFolder, modsFolder);
        if (insideWarning != null) findings.Add(SetupFinding.Warning(insideWarning));

        if (IsUnderSpecialFolder(exeDirectory, Environment.SpecialFolder.ProgramFiles) ||
            IsUnderSpecialFolder(exeDirectory, Environment.SpecialFolder.ProgramFilesX86))
        {
            findings.Add(SetupFinding.Warning(
                "N.P.C.2 is installed under Program Files, where Windows blocks programs from saving files next " +
                "to themselves. Move the N.P.C.2 folder somewhere like C:\\Modding\\NPC2."));
        }

        if (IsUnderOneDrive(exeDirectory))
        {
            findings.Add(SetupFinding.Warning(
                "N.P.C.2 is inside a OneDrive folder. OneDrive can lock or offload files while N.P.C.2 is using " +
                "them, which can corrupt Settings.json. A local folder such as C:\\Modding\\NPC2 is safer."));
        }

        if (!CanWriteTo(exeDirectory))
        {
            findings.Add(SetupFinding.Error(
                $"N.P.C.2 cannot write to its own folder ({exeDirectory}), so it cannot save your settings. " +
                "Move it to a folder you own, or add an antivirus exclusion for it. Running as administrator " +
                "is not recommended."));
        }

        if (manager == SetupModManager.MO2 && !runningUnderMo2)
        {
            findings.Add(SetupFinding.Warning(
                "N.P.C.2 does not appear to have been launched through Mod Organizer 2. Without MO2, N.P.C.2 sees " +
                "only the base game's plugins and patches against the wrong records. Close N.P.C.2, add it to " +
                "MO2's executables list, and launch it from there."));
        }

        if (findings.Count == 0) findings.Add(SetupFinding.Info("The install location looks good."));
        return findings;
    }

    /// <summary>
    /// MO2 launches its executables with its virtual file system DLL (usvfs) injected, which is the
    /// only reliable in-process sign of an MO2 launch. A heuristic: false never blocks anything.
    /// </summary>
    public static bool IsRunningUnderMo2()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.Modules.Cast<ProcessModule>()
                .Any(m => m.ModuleName.StartsWith("usvfs", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- Skyrim version

    /// <summary>The executable that identifies a release's game folder.</summary>
    public static string ExpectedExecutable(SkyrimRelease release) => release.ToString() switch
    {
        "SkyrimVR" => "SkyrimVR.exe",
        "SkyrimLE" or "EnderalLE" => "TESV.exe",
        _ => "SkyrimSE.exe",
    };

    public static List<SetupFinding> GameVersion(SkyrimRelease release, string? gameFolder)
    {
        var findings = new List<SetupFinding>();
        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
        {
            findings.Add(SetupFinding.Info(
                "No install of this version was found in its default location. That is normal for Stock Game, " +
                "Root Builder and modlist installs. You will point N.P.C.2 at the game's Data folder on the next page."));
            return findings;
        }

        string expected = ExpectedExecutable(release);
        bool hasExpected = File.Exists(Path.Combine(gameFolder, expected));
        bool hasVr = File.Exists(Path.Combine(gameFolder, "SkyrimVR.exe"));
        bool hasSe = File.Exists(Path.Combine(gameFolder, "SkyrimSE.exe"));

        if (hasVr && expected != "SkyrimVR.exe")
        {
            findings.Add(SetupFinding.Error($"{gameFolder} is a Skyrim VR install, but {release} is selected. Choose SkyrimVR."));
        }
        else if (hasSe && expected == "SkyrimVR.exe")
        {
            findings.Add(SetupFinding.Error($"{gameFolder} is a Skyrim SE/AE install, but SkyrimVR is selected."));
        }
        else if (!hasExpected)
        {
            findings.Add(SetupFinding.Warning($"{expected} was not found in {gameFolder}. Check that the selected version matches your game."));
        }
        else
        {
            findings.Add(SetupFinding.Info($"Found {expected} in {gameFolder}."));
        }
        return findings;
    }

    // ---------------------------------------------------------------- game data path

    /// <summary>
    /// Data folders of portable game copies (Stock Game, Game Root, ...) near the N.P.C.2 exe. Modlists
    /// keep one inside the MO2 instance and usually install tools next to it, so this walks up a few
    /// levels from the exe and looks one folder down for a Data folder holding Skyrim.esm.
    /// </summary>
    public static List<string> FindPortableGameDataFolders(string startDirectory, int levelsUp = 4)
    {
        var found = new List<string>();
        try
        {
            var dir = new DirectoryInfo(startDirectory);
            for (int i = 0; i <= levelsUp && dir != null; i++, dir = dir.Parent)
            {
                IEnumerable<DirectoryInfo> children;
                try { children = dir.EnumerateDirectories().ToList(); }
                catch { continue; }

                foreach (var child in children)
                {
                    string data = Path.Combine(child.FullName, "Data");
                    if (File.Exists(Path.Combine(data, "Skyrim.esm")) &&
                        !found.Contains(data, StringComparer.OrdinalIgnoreCase))
                    {
                        found.Add(data);
                    }
                }
            }
        }
        catch
        {
            // Probing only: an unreadable parent just ends the search.
        }
        return found;
    }

    /// <summary>Checks the path itself; the trial environment build is <see cref="EnvironmentProbe"/>.</summary>
    public static List<SetupFinding> DataFolderPath(string? dataFolder)
    {
        var findings = new List<SetupFinding>();
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            findings.Add(SetupFinding.Info("Left blank: N.P.C.2 will use the Data folder of the standard install for the selected version."));
            return findings;
        }
        if (!Directory.Exists(dataFolder))
        {
            findings.Add(SetupFinding.Error($"The folder {dataFolder} does not exist."));
            return findings;
        }
        if (File.Exists(Path.Combine(dataFolder, "Data", "Skyrim.esm")))
        {
            findings.Add(SetupFinding.Error(
                $"This is the game folder, not its Data folder. Select {Path.Combine(dataFolder, "Data")} instead."));
            return findings;
        }
        if (!File.Exists(Path.Combine(dataFolder, "Skyrim.esm")))
        {
            findings.Add(SetupFinding.Error("Skyrim.esm is not in this folder, so it is not a Skyrim Data folder."));
        }
        return findings;
    }

    // ---------------------------------------------------------------- mods folder

    public static List<SetupFinding> ModsFolder(string? modsFolder, string? dataFolder, SetupModManager manager)
    {
        var findings = new List<SetupFinding>();
        if (string.IsNullOrWhiteSpace(modsFolder))
        {
            findings.Add(SetupFinding.Error("Select the folder your appearance mods are installed in."));
            return findings;
        }
        if (!Directory.Exists(modsFolder))
        {
            findings.Add(SetupFinding.Error($"The folder {modsFolder} does not exist."));
            return findings;
        }

        if (!string.IsNullOrWhiteSpace(dataFolder) &&
            (PathsEqual(modsFolder, dataFolder) || InstallLocationCheck.IsInside(modsFolder, dataFolder)))
        {
            findings.Add(SetupFinding.Error(
                "This is the game's Data folder. Select your mod manager's mods folder instead " +
                "(MO2: the 'mods' folder of your instance; Vortex: the staging folder)."));
            return findings;
        }
        if (File.Exists(Path.Combine(modsFolder, "Data", "Skyrim.esm")) || File.Exists(Path.Combine(modsFolder, "Skyrim.esm")))
        {
            findings.Add(SetupFinding.Error("This is a game folder, not a mods folder."));
            return findings;
        }

        if (LooksLikeSingleMod(modsFolder))
        {
            findings.Add(SetupFinding.Error(
                "This looks like a single mod (it has plugins or a meshes/textures folder directly inside). " +
                $"Select the folder that contains it instead: {Path.GetDirectoryName(modsFolder.TrimEnd('\\', '/'))}"));
            return findings;
        }

        List<string> subfolders;
        try { subfolders = Directory.EnumerateDirectories(modsFolder).ToList(); }
        catch (Exception ex)
        {
            findings.Add(SetupFinding.Error($"The folder could not be read: {ex.Message}"));
            return findings;
        }
        if (subfolders.Count == 0)
        {
            findings.Add(SetupFinding.Error("This folder has no subfolders, so it holds no mods."));
            return findings;
        }

        int modLike = subfolders.Count(LooksLikeSingleMod);
        if (modLike == 0)
        {
            findings.Add(SetupFinding.Warning(
                $"None of the {subfolders.Count} subfolders contain plugins or meshes/textures folders. Is this the right folder?"));
        }
        else
        {
            findings.Add(SetupFinding.Info($"Found {subfolders.Count} mod folders; {modLike} contain plugins or meshes/textures."));
        }

        bool isMo2 = IsMo2ModsFolder(modsFolder);
        bool isVortex = IsVortexStagingFolder(modsFolder);
        if (isMo2) findings.Add(SetupFinding.Info("Recognized as a Mod Organizer 2 mods folder."));
        if (isVortex) findings.Add(SetupFinding.Info("Recognized as a Vortex staging folder."));
        if (manager == SetupModManager.Vortex && isMo2)
        {
            findings.Add(SetupFinding.Info("You chose Vortex, but this is an MO2 mods folder. That's fine if your appearance mods live in a separate MO2 instance."));
        }
        if (manager == SetupModManager.MO2 && isVortex)
        {
            findings.Add(SetupFinding.Info("You chose MO2, but this is a Vortex staging folder. That's fine if your appearance mods live in a separate Vortex setup."));
        }
        return findings;
    }

    /// <summary>An MO2 instance keeps ModOrganizer.ini (portable) or profiles\ next to its mods folder.</summary>
    public static bool IsMo2ModsFolder(string modsFolder)
    {
        string? parent = Path.GetDirectoryName(modsFolder.TrimEnd('\\', '/'));
        return parent != null &&
               (File.Exists(Path.Combine(parent, "ModOrganizer.ini")) || Directory.Exists(Path.Combine(parent, "profiles")));
    }

    /// <summary>Vortex marks its staging folder with a <c>__vortex_staging_folder</c> file.</summary>
    public static bool IsVortexStagingFolder(string modsFolder) =>
        File.Exists(Path.Combine(modsFolder, "__vortex_staging_folder"));

    private static bool LooksLikeSingleMod(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Any(f => PluginExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) ||
                   Directory.Exists(Path.Combine(folder, "meshes")) ||
                   Directory.Exists(Path.Combine(folder, "textures"));
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- MO2 modlist filter

    /// <summary>The modlist.txt of each profile of the MO2 instance that owns <paramref name="modsFolder"/>.</summary>
    public static List<string> FindMo2ProfileModlists(string? modsFolder)
    {
        if (string.IsNullOrWhiteSpace(modsFolder)) return new();
        try
        {
            string? instance = Path.GetDirectoryName(modsFolder.TrimEnd('\\', '/'));
            string profiles = instance == null ? string.Empty : Path.Combine(instance, "profiles");
            if (!Directory.Exists(profiles)) return new();
            return Directory.EnumerateDirectories(profiles)
                .Select(p => Path.Combine(p, "modlist.txt"))
                .Where(File.Exists)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    public static List<SetupFinding> ModlistFilter(string? modlistPath, string? modsFolder)
    {
        var findings = new List<SetupFinding>();
        if (string.IsNullOrWhiteSpace(modlistPath) || !File.Exists(modlistPath))
        {
            findings.Add(SetupFinding.Error("Select a modlist.txt, or turn the filter off."));
            return findings;
        }

        HashSet<string> enabled;
        try { enabled = View_Models.VM_Mods.ParseEnabledModNames(File.ReadLines(modlistPath)); }
        catch (Exception ex)
        {
            findings.Add(SetupFinding.Error($"modlist.txt could not be read: {ex.Message}"));
            return findings;
        }

        if (string.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder))
        {
            findings.Add(SetupFinding.Info($"{enabled.Count} mods are enabled in this profile."));
            return findings;
        }

        var folderNames = Directory.EnumerateDirectories(modsFolder).Select(Path.GetFileName).ToList();
        int matched = folderNames.Count(n => n != null && enabled.Contains(n));
        if (enabled.Count > 0 && matched == 0)
        {
            findings.Add(SetupFinding.Error(
                "None of the mods enabled in this modlist.txt are in your mods folder. It probably belongs to a different MO2 instance."));
        }
        else
        {
            findings.Add(SetupFinding.Info($"{matched} of the {folderNames.Count} folders in your mods folder are enabled in this profile and will be scanned."));
        }
        return findings;
    }

    // ---------------------------------------------------------------- curated mugshots

    public static List<SetupFinding> MugshotsFolder(string? mugshotsFolder, string? modsFolder)
    {
        var findings = new List<SetupFinding>();
        if (string.IsNullOrWhiteSpace(mugshotsFolder))
        {
            findings.Add(SetupFinding.Error("Select the folder you extracted your mugshot downloads into."));
            return findings;
        }
        if (!Directory.Exists(mugshotsFolder))
        {
            findings.Add(SetupFinding.Error($"The folder {mugshotsFolder} does not exist."));
            return findings;
        }

        List<string> topFolders;
        List<string> archives;
        try
        {
            topFolders = Directory.EnumerateDirectories(mugshotsFolder).ToList();
            archives = Directory.EnumerateFiles(mugshotsFolder, "*", SearchOption.AllDirectories)
                .Where(f => ArchiveExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex)
        {
            findings.Add(SetupFinding.Error($"The folder could not be read: {ex.Message}"));
            return findings;
        }

        if (archives.Count > 0)
        {
            findings.Add(SetupFinding.Warning(
                $"{archives.Count} archive(s) have not been extracted (for example {Path.GetFileName(archives[0])}). " +
                "N.P.C.2 cannot read mugshots inside .zip/.7z/.rar files — extract them here first."));
        }

        // Expected layout: <Mugshots>\<ModName>\<Plugin.esp>\<FormID>.png
        if (topFolders.Any(IsPluginNamed))
        {
            findings.Add(SetupFinding.Error(
                "This folder holds plugin-named folders (e.g. Something.esp), so it is a single mod's mugshot folder. " +
                $"Select the folder above it: {Path.GetDirectoryName(mugshotsFolder.TrimEnd('\\', '/'))}"));
            return findings;
        }
        if (topFolders.Count == 0)
        {
            if (archives.Count == 0) findings.Add(SetupFinding.Error("This folder is empty."));
            return findings;
        }

        var good = new List<string>();
        var nested = new List<string>();
        var empty = new List<string>();
        foreach (var folder in topFolders)
        {
            var children = SafeDirectories(folder);
            if (children.Any(IsPluginNamed)) good.Add(folder);
            else if (children.Any(c => SafeDirectories(c).Any(IsPluginNamed))) nested.Add(folder);
            else empty.Add(folder);
        }

        if (nested.Count > 0)
        {
            findings.Add(SetupFinding.Warning(
                $"{nested.Count} folder(s) have an extra folder level (for example {Path.GetFileName(nested[0])}). " +
                "Each mod's folder must directly contain the plugin-named folders (Mugshots\\ModName\\Plugin.esp\\...). " +
                "Move the inner folder up one level."));
        }
        if (empty.Count > 0)
        {
            findings.Add(SetupFinding.Info(
                $"{empty.Count} folder(s) contain no plugin-named subfolders and will be ignored (for example {Path.GetFileName(empty[0])})."));
        }

        var vanilla = good.Concat(nested).Select(Path.GetFileName)
            .FirstOrDefault(n => n != null && n.StartsWith("Vanilla", StringComparison.OrdinalIgnoreCase));
        if (vanilla != null)
        {
            findings.Add(SetupFinding.Info(
                $"If '{vanilla}' holds base-game mugshots, rename it to 'Base Game' so it matches N.P.C.2's Base Game entry " +
                "(or drag it onto that entry in the Mods tab later)."));
        }

        if (good.Count > 0 && !string.IsNullOrWhiteSpace(modsFolder) && Directory.Exists(modsFolder))
        {
            var modNames = new HashSet<string>(SafeDirectories(modsFolder).Select(Path.GetFileName).OfType<string>(),
                StringComparer.OrdinalIgnoreCase);
            var unmatched = good.Select(Path.GetFileName).OfType<string>()
                .Where(n => !modNames.Contains(n) && !string.Equals(n, "Base Game", StringComparison.OrdinalIgnoreCase))
                .ToList();
            findings.Add(SetupFinding.Info($"{good.Count - unmatched.Count} of {good.Count} mugshot folders match a mod folder by name."));
            if (unmatched.Count > 0)
            {
                findings.Add(SetupFinding.Info(
                    $"Not matched by name: {string.Join(", ", unmatched.Take(5))}{(unmatched.Count > 5 ? ", ..." : "")}. " +
                    "N.P.C.2 still lists these; link them to the right mod by dragging them in the Mods tab."));
            }
        }
        else if (good.Count > 0)
        {
            findings.Add(SetupFinding.Info($"Found {good.Count} mod mugshot folders."));
        }
        else if (nested.Count == 0)
        {
            findings.Add(SetupFinding.Warning("No folders in the expected layout were found (Mugshots\\ModName\\Plugin.esp\\FormID.png)."));
        }
        return findings;
    }

    private static bool IsPluginNamed(string folder) =>
        PluginExtensions.Contains(Path.GetExtension(folder), StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- generated-mugshot folders

    public static List<SetupFinding> WritableFolder(string? folder, string what)
    {
        var findings = new List<SetupFinding>();
        if (string.IsNullOrWhiteSpace(folder))
        {
            findings.Add(SetupFinding.Error($"Select a {what}."));
            return findings;
        }
        if (!CanWriteTo(NearestExistingAncestor(folder) ?? folder))
        {
            findings.Add(SetupFinding.Error($"N.P.C.2 cannot write to {folder}. Pick a folder you own."));
        }
        if (folder.Any(c => c > 127))
        {
            findings.Add(SetupFinding.Warning("This path contains non-English characters, which have broken mugshot rendering for some users."));
        }
        return findings;
    }

    // ---------------------------------------------------------------- output

    /// <summary>Same rule as the patcher: a bare name is created inside the mods folder.</summary>
    public static string? ResolveOutputDirectory(string? outputDirectory, string? modsFolder)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) return null;
        if (Path.IsPathRooted(outputDirectory)) return outputDirectory;
        return string.IsNullOrWhiteSpace(modsFolder) ? null : Path.Combine(modsFolder, outputDirectory);
    }

    public static List<SetupFinding> OutputDirectory(string? outputDirectory, string? modsFolder, string? dataFolder,
        SetupModManager manager)
    {
        var findings = new List<SetupFinding>();
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            findings.Add(SetupFinding.Error("Enter a folder name or a full path for the output."));
            return findings;
        }

        string withoutDrive = Path.IsPathRooted(outputDirectory)
            ? outputDirectory.Substring(Path.GetPathRoot(outputDirectory)?.Length ?? 0)
            : outputDirectory;
        var invalid = Path.GetInvalidFileNameChars().Except(new[] { '\\', '/' }).ToHashSet();
        if (withoutDrive.Any(invalid.Contains))
        {
            findings.Add(SetupFinding.Error("The path contains characters Windows does not allow in folder names."));
            return findings;
        }
        if (outputDirectory.IndexOfAny(new[] { '{', '}' }) >= 0)
        {
            findings.Add(SetupFinding.Error("Curly braces { } in the output path have stopped output from being written. Remove them."));
            return findings;
        }

        string? resolved = ResolveOutputDirectory(outputDirectory, modsFolder);
        if (resolved == null)
        {
            findings.Add(SetupFinding.Error("A bare folder name is created inside your mods folder, but no mods folder is set. Enter a full path instead."));
            return findings;
        }

        if (!string.IsNullOrWhiteSpace(dataFolder) &&
            (PathsEqual(resolved, dataFolder) || InstallLocationCheck.IsInside(resolved, dataFolder)))
        {
            findings.Add(SetupFinding.Error("Output must not go into the game's Data folder. Write it to a mod folder your mod manager manages."));
            return findings;
        }
        if (!string.IsNullOrWhiteSpace(modsFolder) && PathsEqual(resolved, modsFolder))
        {
            findings.Add(SetupFinding.Error("This is the mods folder itself. Give the output its own folder inside it (e.g. 'NPC Output')."));
            return findings;
        }
        if (!string.IsNullOrWhiteSpace(modsFolder) && InstallLocationCheck.IsInside(resolved, modsFolder))
        {
            string relative = Path.GetRelativePath(modsFolder, resolved);
            if (relative.Contains(Path.DirectorySeparatorChar) || relative.Contains(Path.AltDirectorySeparatorChar))
            {
                findings.Add(SetupFinding.Warning("This folder is inside another mod's folder. The output is normally its own mod."));
            }
        }

        if (!CanWriteTo(NearestExistingAncestor(resolved) ?? resolved))
        {
            findings.Add(SetupFinding.Error($"N.P.C.2 cannot write to {resolved}."));
            return findings;
        }

        findings.Add(SetupFinding.Info($"Output will be written to: {resolved}"));
        if (manager == SetupModManager.Vortex)
        {
            if (!string.IsNullOrWhiteSpace(modsFolder) && !InstallLocationCheck.IsInside(resolved, modsFolder))
            {
                findings.Add(SetupFinding.Warning("Vortex only deploys mods from its staging folder. Put the output inside your staging folder."));
            }
            findings.Add(SetupFinding.Info("After each run, deploy in Vortex so the game sees the new output."));
        }
        else if (manager == SetupModManager.MO2)
        {
            findings.Add(SetupFinding.Info("After each run, enable the output in MO2 and let it win all file conflicts."));
        }
        return findings;
    }

    public static List<SetupFinding> OutputPluginName(string? pluginName, IReadOnlyCollection<string>? loadOrderFileNames)
    {
        var findings = new List<SetupFinding>();
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            findings.Add(SetupFinding.Error("Enter a plugin name."));
            return findings;
        }
        if (pluginName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            findings.Add(SetupFinding.Error("The name contains characters Windows does not allow in file names."));
            return findings;
        }

        string extension = Path.GetExtension(pluginName);
        string stem = PluginExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(pluginName)
            : pluginName;
        if (string.IsNullOrWhiteSpace(stem))
        {
            findings.Add(SetupFinding.Error("Enter a plugin name."));
            return findings;
        }

        string fileName = stem + ".esp";
        findings.Add(SetupFinding.Info($"The output plugin will be {fileName}."));
        if (loadOrderFileNames != null && loadOrderFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(SetupFinding.Warning(
                $"{fileName} is already in your load order. That's fine if it is an earlier N.P.C.2 output; " +
                "otherwise pick a different name so another mod's plugin isn't overwritten."));
        }
        return findings;
    }

    // ---------------------------------------------------------------- helpers

    internal static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool CanWriteTo(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            string probe = Path.Combine(directory, $".npc2-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? NearestExistingAncestor(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            while (dir != null && !dir.Exists) dir = dir.Parent;
            return dir?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> SafeDirectories(string folder)
    {
        try { return Directory.EnumerateDirectories(folder).ToList(); }
        catch { return new(); }
    }

    private static bool IsUnderSpecialFolder(string path, Environment.SpecialFolder folder)
    {
        string root = Environment.GetFolderPath(folder);
        return !string.IsNullOrEmpty(root) && InstallLocationCheck.IsInside(path, root);
    }

    private static bool IsUnderOneDrive(string path)
    {
        string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        return (!string.IsNullOrEmpty(oneDrive) && InstallLocationCheck.IsInside(path, oneDrive)) ||
               path.Contains(@"\OneDrive\", StringComparison.OrdinalIgnoreCase);
    }
}
