using System.IO;
using System.Text.RegularExpressions;
using GameFinder.RegistryUtils;
using GameFinder.StoreHandlers.Steam;
using GameFinder.StoreHandlers.Steam.Models.ValueTypes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Installs;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace NPC_Plugin_Chooser_2.BackEnd.SetupValidation;

/// <summary>
/// A trial build of the game environment for values the user has not applied yet. Built the way
/// <see cref="EnvironmentStateProvider.TryBuildUntrimmedEnvironment"/> builds its throwaway
/// environment, then disposed: nothing in the running app's environment changes. The result is
/// what the wizard's Game Data page shows — the same facts as the Settings page's Environment
/// Status, plus the follow-up checks users needed (which plugin broke the build, CC plugins
/// present but inactive).
/// </summary>
public sealed class EnvironmentProbe
{
    private static readonly ModKey SkyrimEsm = ModKey.FromFileName("Skyrim.esm");

    public bool IsValid { get; private init; }
    public string? DataFolderPath { get; private init; }
    public string? LoadOrderFilePath { get; private init; }
    public string? CreationClubListingsPath { get; private init; }
    public int PluginCount { get; private init; }
    public int ActivePluginCount { get; private init; }
    public IReadOnlyList<string> ActivePluginFileNames { get; private init; } = Array.Empty<string>();
    public List<SetupFinding> Findings { get; } = new();

    /// <summary>Default install location of <paramref name="release"/>, or null when Mutagen finds none.</summary>
    public static string? FindDefaultGameFolder(SkyrimRelease release)
    {
        try
        {
            var gameRelease = release.ToGameRelease();
            string? mutagenFolder = GameLocations.TryGetGameFolder(gameRelease, out var folder) ? folder.Path : null;
            if (gameRelease is not (GameRelease.EnderalLE or GameRelease.EnderalSE or GameRelease.EnderalSEGog))
            {
                return mutagenFolder;
            }

            // Mutagen falls back to Skyrim's own Steam app (489830 / 72850) for Enderal, which reports every
            // Skyrim install as Enderal. Enderal's master is the only marker that tells the two apart, so a
            // folder only counts with it — and the standalone Steam release (976620) is looked up as well.
            return new[] { mutagenFolder, gameRelease == GameRelease.EnderalSE ? FindSteamGameFolder(EnderalSESteamAppId) : null }
                .FirstOrDefault(f => f != null && File.Exists(Path.Combine(f, "Data", EnderalMasterFileName)));
        }
        catch
        {
            return null;
        }
    }

    private const string EnderalMasterFileName = "Enderal - Forgotten Stories.esm";
    private const uint EnderalSESteamAppId = 976620;

    private static string? FindSteamGameFolder(uint appId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var steam = new SteamHandler(NexusMods.Paths.FileSystem.Shared, WindowsRegistry.Shared, null);
        return steam.FindOneGameById(AppId.From(appId), out _)?.Path.GetFullPath();
    }

    public static EnvironmentProbe Run(SkyrimRelease release, string? dataFolder)
    {
        try
        {
            var builder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(release.ToGameRelease());
            if (!dataFolder.IsNullOrWhitespace() && Directory.Exists(dataFolder))
            {
                builder = builder.WithTargetDataFolder(dataFolder);
            }
            builder = EnvironmentStateProvider.WithCreationClubListingsPath(builder,
                EnvironmentStateProvider.FindCreationClubListingsNextTo(dataFolder));

            using var env = builder
                .TransformModListings(x => x.OnlyEnabledAndExisting())
                .Build();

            return FromEnvironment(release, env);
        }
        catch (Exception ex)
        {
            var failed = new EnvironmentProbe { IsValid = false, DataFolderPath = dataFolder };
            failed.Findings.Add(SetupFinding.Error(DescribeBuildFailure(ex)));
            if (dataFolder.IsNullOrWhitespace())
            {
                failed.Findings.Add(SetupFinding.Info(
                    "The Data folder was left blank, so the standard install was used. If you play from a Stock Game or " +
                    "Root Builder copy, select that copy's Data folder instead."));
            }
            return failed;
        }
    }

    private static EnvironmentProbe FromEnvironment(SkyrimRelease release, IGameEnvironment<ISkyrimMod, ISkyrimModGetter> env)
    {
        var listed = env.LoadOrder?.ListedOrder?.ToList() ?? new();
        string dataFolder = env.DataFolderPath;
        string? loadOrderFile = env.LoadOrderFilePath;
        string? cccFile = env.CreationClubListingsFilePath;
        var active = listed.Where(l => l.Enabled).Select(l => l.ModKey).ToList();

        var probe = new EnvironmentProbe
        {
            DataFolderPath = dataFolder,
            LoadOrderFilePath = loadOrderFile,
            CreationClubListingsPath = cccFile,
            PluginCount = listed.Count,
            ActivePluginCount = active.Count,
            ActivePluginFileNames = active.Select(k => k.FileName.String).ToList(),
            IsValid = Directory.Exists(dataFolder) && listed.Count > 0 &&
                      listed.Any(l => l.ModKey == SkyrimEsm) &&
                      !string.IsNullOrEmpty(loadOrderFile) && File.Exists(loadOrderFile),
        };

        if (!Directory.Exists(dataFolder))
        {
            probe.Findings.Add(SetupFinding.Error($"The Data folder {dataFolder} does not exist."));
            return probe;
        }
        if (string.IsNullOrEmpty(loadOrderFile) || !File.Exists(loadOrderFile))
        {
            probe.Findings.Add(SetupFinding.Error(
                $"Your load order file (plugins.txt) was not found{(string.IsNullOrEmpty(loadOrderFile) ? "" : $" at {loadOrderFile}")}. " +
                "Launch the game once through its launcher (needed after a fresh install or a downgrade), then launch N.P.C.2 " +
                "through your mod manager. Copying a plugins.txt into the Data folder does not help: the game and mod managers " +
                "read it from your AppData folder."));
            return probe;
        }
        if (!probe.IsValid)
        {
            probe.Findings.Add(SetupFinding.Error("The load order does not contain Skyrim.esm, so this is not a usable Skyrim environment."));
            return probe;
        }

        probe.Findings.Add(SetupFinding.Info($"Valid. Data folder: {dataFolder}"));
        probe.Findings.Add(SetupFinding.Info($"Load order: {loadOrderFile} — {active.Count} active of {listed.Count} plugins."));

        var baseGame = Implicits.Get(release.ToGameRelease()).BaseMasters.ToHashSet();
        var cc = ReadCreationClubPlugins(cccFile);
        if (EnvironmentStateProvider.IsVanillaOnlyLoadOrder(active, baseGame, cc, null))
        {
            probe.Findings.Add(SetupFinding.Warning(
                "Only base-game and Creation Club plugins are active. This almost always means N.P.C.2 was not launched " +
                "through your mod manager, so it is reading the game launcher's plugin list instead of your profile's. " +
                "Relaunch N.P.C.2 from your mod manager. (A genuinely unmodded load order is fine to continue with.)"));
        }

        if (string.IsNullOrEmpty(cccFile) || !File.Exists(cccFile))
        {
            probe.Findings.Add(SetupFinding.Info("Skyrim.ccc (the Creation Club list) was not found; Creation Club plugins will be treated as ordinary plugins."));
        }
        else
        {
            probe.Findings.Add(SetupFinding.Info($"Creation Club list: {cccFile}"));
            var activeSet = active.ToHashSet();
            var inactive = cc.Where(k => !activeSet.Contains(k) && File.Exists(Path.Combine(dataFolder, k.FileName))).ToList();
            if (inactive.Count > 0)
            {
                probe.Findings.Add(SetupFinding.Warning(
                    $"{inactive.Count} Creation Club plugin(s) are in the Data folder but not active (e.g. {inactive[0].FileName}). " +
                    "Mods that require them will fail. Enable them in your mod manager, or verify game files if the game moved drives."));
            }
        }

        return probe;
    }

    private static HashSet<ModKey> ReadCreationClubPlugins(string? cccFile)
    {
        var set = new HashSet<ModKey>();
        if (string.IsNullOrEmpty(cccFile) || !File.Exists(cccFile)) return set;
        try
        {
            foreach (var line in File.ReadLines(cccFile))
            {
                if (ModKey.TryFromFileName(line.Trim(), out var key)) set.Add(key);
            }
        }
        catch
        {
            // Unreadable list: report nothing rather than fail the probe.
        }
        return set;
    }

    // Plugin names may contain spaces, but so does the sentence around them, so a spaced name is only
    // trusted when quoted or bracketed; otherwise the name is the space-free token before the extension.
    private static readonly Regex QuotedPluginNamePattern =
        new(@"['""\[(]([^'""\[\]()\r\n]+?\.(?:esp|esm|esl))['""\])]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BarePluginNamePattern =
        new(@"[^\s'""\[\](),:;]+\.(?:esp|esm|esl)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Users got a raw stack trace when one plugin broke the build — the most common "Invalid
    /// environment" report on 2.2.5 (a ModGroupsMalformedException from a corrupt CC file on a
    /// clean install, or an LE plugin hand-ported to SE). Name the plugin, from Mutagen's typed
    /// exception context first (the same fields ExceptionLogger reports) and the message text
    /// second, and say what usually fixes that kind of failure.
    /// </summary>
    internal static string DescribeBuildFailure(Exception ex)
    {
        var chain = Flatten(ex).ToList();
        string text = ExceptionLogger.GetExceptionStack(ex);
        string firstLine = (ex.GetBaseException().Message ?? ex.Message).Split('\n')[0].Trim();

        var plugin = chain.OfType<ModGroupsMalformedException>()
                         .Select(m => m.ModPath?.ModKey.FileName.ToString())
                         .Concat(chain.OfType<RecordException>().Select(r => r.ModKey?.FileName.ToString()))
                         .FirstOrDefault(n => !string.IsNullOrEmpty(n))
                     ?? QuotedPluginNamePattern.Matches(text).Select(m => m.Groups[1].Value.Trim())
                         .Concat(BarePluginNamePattern.Matches(text).Select(m => m.Value))
                         .FirstOrDefault(n => !n.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase));

        if (text.Contains("itself as a master", StringComparison.OrdinalIgnoreCase))
        {
            return $"{plugin ?? "A plugin"} lists itself as a master. This usually means a patch was renamed to the same file " +
                   "name as the plugin it patches. Delete or rename that patch.\n\nError: " + firstLine;
        }

        string fix = chain.OfType<ModGroupsMalformedException>().Any()
            ? "The file is not valid Skyrim SE data: usually a corrupt or incomplete file, or an LE (Oldrim) plugin that was " +
              "never converted for SE. If it is a base-game or Creation Club plugin, verify your game files in Steam/GOG; " +
              "otherwise disable or reinstall the mod it comes from."
            : "It may be corrupt or unreadable: verify game files if it is a base-game or Creation Club plugin, otherwise " +
              "disable or reinstall the mod it comes from.";

        return plugin != null
            ? $"The environment could not be built because of {plugin}. {fix}\n\nError: {firstLine}"
            : $"The environment could not be built: {firstLine}";
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        yield return ex;
        IEnumerable<Exception> children = ex is AggregateException agg
            ? agg.InnerExceptions
            : ex.InnerException is { } inner ? new[] { inner } : Array.Empty<Exception>();
        foreach (var child in children)
        foreach (var e in Flatten(child))
            yield return e;
    }
}
