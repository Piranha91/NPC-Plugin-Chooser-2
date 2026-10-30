using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd.SetupValidation;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models.SetupWizard;

/// <summary>
/// One wizard page. Findings gate Next: any Error blocks it, any Warning needs the "I understand"
/// tick. Each page's view is a DataTemplate keyed on its type in SetupWizardWindow.xaml.
/// </summary>
public abstract class WizardStep : ReactiveObject
{
    private int _validationVersion;
    private string _acknowledgedWarnings = string.Empty;

    protected WizardStep(VM_SetupWizard wizard)
    {
        Wizard = wizard;
        this.WhenAnyValue(x => x.IsValidating, x => x.HasErrors, x => x.HasWarnings, x => x.WarningsAcknowledged,
                (validating, errors, warnings, acknowledged) => !validating && !errors && (!warnings || acknowledged))
            .ToPropertyEx(this, x => x.CanProceed);
    }

    public VM_SetupWizard Wizard { get; }
    public abstract string Title { get; }
    public abstract string Explanation { get; }
    public virtual bool IsApplicable => true;

    public ObservableCollection<SetupFinding> Findings { get; } = new();
    [Reactive] public bool IsValidating { get; private set; }
    [Reactive] public bool HasErrors { get; private set; }
    [Reactive] public bool HasWarnings { get; private set; }
    [Reactive] public bool WarningsAcknowledged { get; set; }
    [ObservableAsProperty] public bool CanProceed { get; }

    public async Task RevalidateAsync()
    {
        int version = ++_validationVersion;
        IsValidating = true;
        List<SetupFinding> findings;
        try
        {
            findings = await ValidateAsync();
        }
        catch (Exception ex)
        {
            findings = new() { SetupFinding.Error($"This page could not be checked: {ex.Message}") };
        }
        if (version != _validationVersion) return; // a newer edit superseded this run

        Findings.Clear();
        foreach (var finding in findings.OrderByDescending(f => f.Severity)) Findings.Add(finding);
        HasErrors = findings.Any(f => f.Severity == SetupSeverity.Error);

        // An acknowledgement covers the warnings the user actually read, not whatever replaces them.
        string warnings = string.Join("\n", findings.Where(f => f.Severity == SetupSeverity.Warning).Select(f => f.Message));
        if (warnings != _acknowledgedWarnings) WarningsAcknowledged = false;
        _acknowledgedWarnings = warnings;
        HasWarnings = warnings.Length > 0;
        IsValidating = false;
    }

    protected abstract Task<List<SetupFinding>> ValidateAsync();

    protected static Task<List<SetupFinding>> Done(List<SetupFinding> findings) => Task.FromResult(findings);
}

public sealed class ModManagerStep : WizardStep
{
    public ModManagerStep(VM_SetupWizard wizard) : base(wizard) { }
    public override string Title => "Mod Manager";
    public override string Explanation =>
        "Which mod manager do you launch N.P.C.2 from? This tailors the advice on the following pages; it is not saved.";

    protected override Task<List<SetupFinding>> ValidateAsync() => Done(Wizard.ModManager switch
    {
        SetupModManager.MO2 => new() { SetupFinding.Info("Add NPC Plugin Chooser 2.exe to MO2's executables list and always launch it from there.") },
        SetupModManager.Vortex => new() { SetupFinding.Info("Add NPC Plugin Chooser 2.exe as a Vortex tool and launch it from the Dashboard. N.P.C.2 has been tested mostly with MO2.") },
        _ => new() { SetupFinding.Warning("N.P.C.2 reads your load order the way MO2 and Vortex present it. Without one, make sure your plugins.txt lists the plugins you play with.") },
    });
}

public sealed class InstallLocationStep : WizardStep
{
    private readonly bool _runningUnderMo2 = SetupValidators.IsRunningUnderMo2();

    public InstallLocationStep(VM_SetupWizard wizard) : base(wizard) { }
    public override string Title => "Install Location";
    public override string Explanation =>
        $"N.P.C.2 is running from:\n{AppContext.BaseDirectory}\n\nIt saves Settings.json, caches and logs next to its exe, " +
        "so it needs its own folder outside the game, the Mods folder and protected locations.";

    protected override Task<List<SetupFinding>> ValidateAsync() => Done(SetupValidators.InstallLocation(
        AppContext.BaseDirectory, Wizard.EffectiveDataFolder, Wizard.ModsFolder, Wizard.ModManager, _runningUnderMo2));
}

public sealed class GameVersionStep : WizardStep
{
    private Dictionary<SkyrimRelease, string?>? _detected;

    public GameVersionStep(VM_SetupWizard wizard) : base(wizard) { }
    public override string Title => "Skyrim Version";
    public override string Explanation =>
        "Choose the edition you are modding. SkyrimSE covers both SE and AE; GOG and VR have their own entries.";

    [Reactive] public string DetectedInstallsText { get; private set; } = "Looking for installed games...";

    protected override async Task<List<SetupFinding>> ValidateAsync()
    {
        _detected ??= await Task.Run(() =>
            Wizard.SkyrimReleases.ToDictionary(r => r, EnvironmentProbe.FindDefaultGameFolder));
        var found = _detected.Where(kv => kv.Value != null).ToList();
        DetectedInstallsText = found.Count == 0
            ? "No installs were found in their default locations (normal for Stock Game, Root Builder and modlists)."
            : "Installs found:\n" + string.Join("\n", found.Select(kv => $"  {kv.Key}: {kv.Value}"));

        // A custom Data path already chosen says more about the real install than the default location.
        string? gameFolder = !string.IsNullOrWhiteSpace(Wizard.SkyrimGamePath)
            ? Path.GetDirectoryName(Wizard.SkyrimGamePath.TrimEnd('\\', '/'))
            : _detected.GetValueOrDefault(Wizard.SkyrimRelease);
        return SetupValidators.GameVersion(Wizard.SkyrimRelease, gameFolder);
    }
}

public sealed class GameDataStep : WizardStep
{
    private (SkyrimRelease, string)? _probedFor;

    public GameDataStep(VM_SetupWizard wizard) : base(wizard)
    {
        PortableGameDataFolders = SetupValidators.FindPortableGameDataFolders(AppContext.BaseDirectory);
        UseFolderCommand = ReactiveCommand.Create<string>(folder => Wizard.SkyrimGamePath = folder);
    }

    public ReactiveCommand<string, Unit> UseFolderCommand { get; }

    public override string Title => "Game Data Folder";
    public override string Explanation =>
        "Leave blank for a standard Steam or GOG install. If you play from a Stock Game or Root Builder copy (most " +
        "modlists do), select that copy's Data folder. N.P.C.2 checks the result by loading your load order.";

    public IReadOnlyList<string> PortableGameDataFolders { get; }
    public bool HasPortableGameDataFolders => PortableGameDataFolders.Count > 0;

    protected override async Task<List<SetupFinding>> ValidateAsync()
    {
        var findings = SetupValidators.DataFolderPath(Wizard.SkyrimGamePath);
        if (findings.Any(f => f.Severity == SetupSeverity.Error))
        {
            Wizard.LastProbe = null;
            _probedFor = null;
            return findings;
        }

        var key = (Wizard.SkyrimRelease, Wizard.SkyrimGamePath ?? string.Empty);
        if (_probedFor != key || Wizard.LastProbe == null)
        {
            var release = Wizard.SkyrimRelease;
            var path = Wizard.SkyrimGamePath;
            Wizard.LastProbe = await Task.Run(() => EnvironmentProbe.Run(release, path));
            _probedFor = key;
        }
        findings.AddRange(Wizard.LastProbe.Findings);

        if (string.IsNullOrWhiteSpace(Wizard.SkyrimGamePath) && PortableGameDataFolders.Count > 0)
        {
            findings.Add(SetupFinding.Warning(
                "A portable game copy was found near N.P.C.2 (listed above). If you play from it, select its Data folder; " +
                "otherwise N.P.C.2 will patch against the standard install."));
        }
        return findings;
    }
}

public sealed class ModsFolderStep : WizardStep
{
    public ModsFolderStep(VM_SetupWizard wizard) : base(wizard) { }
    public override string Title => "Mods Folder";
    public override string Explanation =>
        "Select the folder your appearance mods are installed in — MO2: the instance's 'mods' folder; Vortex: the staging " +
        "folder. It does not have to belong to the mod manager you launch N.P.C.2 from. The first scan of a new folder can take a while.";

    protected override Task<List<SetupFinding>> ValidateAsync() =>
        Done(SetupValidators.ModsFolder(Wizard.ModsFolder, Wizard.EffectiveDataFolder, Wizard.ModManager));
}

public sealed class ModlistFilterStep : WizardStep
{
    public ModlistFilterStep(VM_SetupWizard wizard) : base(wizard)
    {
        UseModlistCommand = ReactiveCommand.Create<string>(path =>
        {
            Wizard.MO2ModlistPath = path;
            Wizard.FilterByActiveModsMO2 = true;
        });
    }

    public ReactiveCommand<string, Unit> UseModlistCommand { get; }
    public override bool IsApplicable => Wizard.ModManager != SetupModManager.Other;
    public override string Title => "Active Mods Filter";
    public override string Explanation =>
        "Optionally scan only the mods that are enabled in a mod manager profile, using its modlist.txt. Set this up now: " +
        "it is applied during the first scan. Vortex has no modlist.txt; you can write one yourself (one +FolderName line per mod).";

    [Reactive] public IReadOnlyList<string> ProfileModlists { get; private set; } = Array.Empty<string>();
    [Reactive] public bool HasProfileModlists { get; private set; }

    protected override Task<List<SetupFinding>> ValidateAsync()
    {
        ProfileModlists = SetupValidators.FindMo2ProfileModlists(Wizard.ModsFolder);
        HasProfileModlists = ProfileModlists.Count > 0;
        if (!Wizard.FilterByActiveModsMO2)
        {
            return Done(new() { SetupFinding.Info("Every mod in your mods folder will be scanned, whether or not it is enabled.") });
        }
        return Done(SetupValidators.ModlistFilter(Wizard.MO2ModlistPath, Wizard.ModsFolder));
    }
}

public sealed record MugshotLink(string Name, string Url, string Note);

public sealed class MugshotSourcesStep : WizardStep
{
    public MugshotSourcesStep(VM_SetupWizard wizard) : base(wizard) { }
    public override string Title => "Mugshots";
    public override string Explanation =>
        "Mugshots are the face previews you choose between. Pick any combination; where several have an image for the " +
        "same NPC, the order below decides which one is shown (you can reorder later in Settings).";

    /// <summary>The curated packs the README FAQ and the Nexus description recommend.</summary>
    public IReadOnlyList<MugshotLink> CuratedSources { get; } = new[]
    {
        new MugshotLink("Natural Lighting Mugshots", "https://www.nexusmods.com/skyrimspecialedition/mods/97595", "the author's favorite"),
        new MugshotLink("EasyNPC Mugshot Collection", "https://www.nexusmods.com/skyrimspecialedition/mods/99546", ""),
        new MugshotLink("Modpocalypse mugshots", "https://www.nexusmods.com/skyrimspecialedition/mods/103837", ""),
    };

    protected override Task<List<SetupFinding>> ValidateAsync()
    {
        if (!Wizard.UseCuratedMugshots && !Wizard.UseFaceFinder && !Wizard.UseAutoGeneration && !Wizard.UseLiveTiles)
        {
            return Done(new() { SetupFinding.Error("Choose at least one mugshot source, or every NPC will show a placeholder.") });
        }
        var findings = new List<SetupFinding>();
        if (Wizard.UseCuratedMugshots)
        {
            findings.Add(SetupFinding.Info("Download the packs you want and EXTRACT them into one folder; you will select it on the next page."));
        }
        if (Wizard.UseFaceFinder)
        {
            findings.Add(SetupFinding.Info("FaceFinder downloads previews from npcfacefinder.com, so it needs an internet connection."));
        }
        if (Wizard.UseAutoGeneration || Wizard.UseLiveTiles)
        {
            findings.Add(SetupFinding.Info("Auto-generation renders mugshots on your PC from the mods' own files. It needs a working graphics driver; " +
                                           "N.P.C.2 falls back to a slower software renderer if OpenGL is unavailable."));
        }
        return Done(findings);
    }
}

public sealed class MugshotsFolderStep : WizardStep
{
    public MugshotsFolderStep(VM_SetupWizard wizard) : base(wizard) { }
    public override bool IsApplicable => Wizard.UseCuratedMugshots;
    public override string Title => "Mugshots Folder";
    public override string Explanation =>
        "Select the folder you extracted your mugshot packs into. Expected layout:\n" +
        "  <Mugshots folder>\\<Mod name>\\<Plugin.esp>\\<FormID>.png\n" +
        "Each mod's folder should be named like that mod's folder in your mods folder.";

    protected override Task<List<SetupFinding>> ValidateAsync() =>
        Done(SetupValidators.MugshotsFolder(Wizard.MugshotsFolder, Wizard.ModsFolder));
}

public sealed class FaceFinderStep : WizardStep
{
    public FaceFinderStep(VM_SetupWizard wizard) : base(wizard) { }
    public override bool IsApplicable => Wizard.UseFaceFinder;
    public override string Title => "FaceFinder";
    public override string Explanation =>
        "Where downloaded FaceFinder images are cached. Leave blank for the default folder next to N.P.C.2. " +
        "Use Test Connection to check that N.P.C.2 can reach FaceFinder (a VPN can block it).";

    protected override Task<List<SetupFinding>> ValidateAsync()
    {
        string folder = string.IsNullOrWhiteSpace(Wizard.FaceFinderMugshotsFolder)
            ? Settings.GetDefaultFaceFinderMugshotsFolder()
            : Wizard.FaceFinderMugshotsFolder;
        var findings = SetupValidators.WritableFolder(folder, "FaceFinder cache folder");
        findings.Add(Wizard.FaceFinderTestResult switch
        {
            null => SetupFinding.Info("Connection not tested yet."),
            > 0 => SetupFinding.Info($"Connected: FaceFinder knows {Wizard.FaceFinderTestResult} mods."),
            _ => SetupFinding.Warning("N.P.C.2 could not reach FaceFinder. Check your internet connection, VPN or firewall. You can continue and test again later."),
        });
        return Done(findings);
    }
}

public sealed class AutoGenerationStep : WizardStep
{
    public AutoGenerationStep(VM_SetupWizard wizard) : base(wizard) { }
    public override bool IsApplicable => Wizard.UseAutoGeneration || Wizard.UseLiveTiles;
    public override string Title => "Auto-Generated Mugshots";
    public override string Explanation =>
        "Where rendered mugshots are saved (blank = the default folder next to N.P.C.2), and which renderer draws them. " +
        "Internal is the current renderer; Legacy is the older NPC Portrait Creator, kept for its look and no longer developed.";

    protected override Task<List<SetupFinding>> ValidateAsync()
    {
        string folder = string.IsNullOrWhiteSpace(Wizard.AutogenMugshotsFolder)
            ? Settings.GetDefaultAutogenMugshotsFolder()
            : Wizard.AutogenMugshotsFolder;
        var findings = SetupValidators.WritableFolder(folder, "mugshot folder");
        if (Wizard.ModsFolder?.Any(c => c > 127) == true)
        {
            findings.Add(SetupFinding.Warning("Your mods folder path contains non-English characters, which have broken mugshot rendering for some users."));
        }
        if (Wizard.Renderer == MugshotRenderer.LegacyPortraitCreator &&
            !File.Exists(Path.Combine(AppContext.BaseDirectory, "NPC Portrait Creator", "NPCPortraitCreator.exe")))
        {
            findings.Add(SetupFinding.Error("The Legacy renderer's NPCPortraitCreator.exe is missing from the N.P.C.2 folder. Use Internal, or reinstall N.P.C.2."));
        }
        return Done(findings);
    }
}

public sealed class OutputDirectoryStep : WizardStep
{
    public OutputDirectoryStep(VM_SetupWizard wizard) : base(wizard) { }
    public override string Title => "Output Folder";
    public override string Explanation =>
        "Where N.P.C.2 writes the generated plugin and files. A plain name (e.g. 'NPC Output') becomes a folder inside " +
        "your mods folder; a full path puts it anywhere.";

    protected override Task<List<SetupFinding>> ValidateAsync() => Done(SetupValidators.OutputDirectory(
        Wizard.OutputDirectory, Wizard.ModsFolder, Wizard.EffectiveDataFolder, Wizard.ModManager));
}

public sealed class OutputPluginNameStep : WizardStep
{
    public OutputPluginNameStep(VM_SetupWizard wizard) : base(wizard) { }
    public override string Title => "Output Plugin Name";
    public override string Explanation => "The name of the generated plugin, without extension (e.g. 'NPC' gives NPC.esp).";

    protected override Task<List<SetupFinding>> ValidateAsync() =>
        Done(SetupValidators.OutputPluginName(Wizard.OutputPluginName, Wizard.LastProbe?.ActivePluginFileNames.ToList()));
}

public sealed class PatchingModeStep : WizardStep
{
    public PatchingModeStep(VM_SetupWizard wizard) : base(wizard) { }
    public override string Title => "Patching Mode";
    public override string Explanation =>
        "Create and Patch (recommended) works like EasyNPC: your conflict-winning NPC records get your chosen appearances, " +
        "so the output goes at the END of your load order.\n\n" +
        "Create builds a standalone appearance mod without looking at your load order: place its plugin like any other " +
        "appearance mod and resolve conflicts yourself. Create mode cannot give one NPC another NPC's appearance.";

    protected override Task<List<SetupFinding>> ValidateAsync()
    {
        var findings = new List<SetupFinding>
        {
            SetupFinding.Info("Tip: you can leave your appearance mods DISABLED in your mod manager. N.P.C.2 reads them straight from the " +
                              "mods folder, and keeping them disabled stops their plugins becoming masters of the output."),
        };
        if (Wizard.PatchingMode == PatchingMode.Create)
        {
            findings.Add(SetupFinding.Info("Create mode: after running, sort the output plugin high and let its files win conflicts."));
        }
        return Done(findings);
    }
}

public sealed class OutputBackendStep : WizardStep
{
    public OutputBackendStep(VM_SetupWizard wizard) : base(wizard) { }
    public override string Title => "Output Type";
    public override string Explanation =>
        "ESP (recommended): appearances are written as NPC records in the output plugin.\n\n" +
        "SkyPatcher: the plugin carries a copy of each appearance and a SkyPatcher .ini applies it while the game runs. " +
        "Requires SkyPatcher.\n\n" +
        "Recast: like SkyPatcher, but the appearance is applied by Recast. Requires Recast; Hybrid uses SkyPatcher for the race, " +
        "outfits and keywords Recast cannot change.";

    protected override Task<List<SetupFinding>> ValidateAsync()
    {
        var findings = new List<SetupFinding>();
        switch (Wizard.OutputBackend)
        {
            case OutputBackend.SkyPatcher:
                findings.Add(SetupFinding.Warning("SkyPatcher can conflict with other runtime appearance tools such as RSV and SynthEBD."));
                break;
            case OutputBackend.Recast:
                findings.Add(SetupFinding.Warning(
                    "Recast mode is new. Recast never changes race; in Recast Only mode, selections that need a race change, " +
                    "outfits or keywords cannot be delivered."));
                break;
            default:
                findings.Add(SetupFinding.Info("ESP output works with no extra frameworks."));
                break;
        }
        return Done(findings);
    }
}

public sealed class SummaryStep : WizardStep
{
    public SummaryStep(VM_SetupWizard wizard) : base(wizard) { }
    public override string Title => "Summary";
    public override string Explanation =>
        "Finish applies these settings. Everything else keeps its default and can be changed on the Settings page; you can " +
        "run this wizard again from the button at the top of that page.";

    [Reactive] public IReadOnlyList<KeyValuePair<string, string>> Rows { get; private set; } =
        Array.Empty<KeyValuePair<string, string>>();

    protected override Task<List<SetupFinding>> ValidateAsync()
    {
        var d = Wizard.ToDraft();
        var sources = new List<string>();
        if (d.UseCuratedMugshots) sources.Add("Downloaded");
        if (d.UseFaceFinder) sources.Add("FaceFinder");
        if (d.UseAutoGeneration) sources.Add("Auto-generated");
        if (d.UseLiveTiles) sources.Add("Live tiles");

        var rows = new List<KeyValuePair<string, string>>
        {
            new("Skyrim version", d.SkyrimRelease.ToString()),
            new("Game Data folder", string.IsNullOrEmpty(d.SkyrimGamePath) ? $"(auto) {Wizard.LastProbe?.DataFolderPath}" : d.SkyrimGamePath),
            new("Mods folder", d.ModsFolder),
            new("Active mods filter", d.FilterByActiveModsMO2 ? d.MO2ModlistPath : "Off"),
            new("Mugshot sources", string.Join(", ", sources)),
        };
        if (d.UseCuratedMugshots) rows.Add(new("Mugshots folder", d.MugshotsFolder));
        if (d.UseAutoGeneration || d.UseLiveTiles) rows.Add(new("Renderer", d.Renderer.ToString()));
        rows.Add(new("Output folder", SetupValidators.ResolveOutputDirectory(d.OutputDirectory, d.ModsFolder) ?? d.OutputDirectory));
        rows.Add(new("Output plugin", d.OutputPluginName + ".esp"));
        rows.Add(new("Patching mode", d.PatchingMode.ToString()));
        rows.Add(new("Output type", d.OutputBackend == OutputBackend.Recast ? $"Recast ({d.RecastMode})" : d.OutputBackend.ToString()));
        Rows = rows;

        var findings = new List<SetupFinding>();
        bool modsFolderChanges = !string.IsNullOrWhiteSpace(Wizard.AppliedModsFolder) &&
                                 !SetupValidators.PathsEqual(Wizard.AppliedModsFolder, d.ModsFolder);
        if (modsFolderChanges && Wizard.ExistingModCount > 0)
        {
            findings.Add(SetupFinding.Warning(
                $"The mods folder changes. Finish clears the per-mod settings of your current mod list ({Wizard.ExistingModCount} mods) " +
                "and scans the new folder. NPC selections are kept, but any that point at mods missing from the new folder become invalid."));
        }
        if (string.IsNullOrWhiteSpace(Wizard.AppliedModsFolder) || modsFolderChanges)
        {
            findings.Add(SetupFinding.Info("N.P.C.2 will now scan your mods folder. The first scan of a folder takes a while; later launches reuse the results."));
        }
        return Done(findings);
    }
}
