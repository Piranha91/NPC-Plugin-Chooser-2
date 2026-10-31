using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.WindowsAPICodePack.Dialogs;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.SetupValidation;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models.SetupWizard;

/// <summary>
/// Every value the wizard can set. The wizard edits a draft and nothing reaches the running app
/// until Finish, when <see cref="VM_Settings.ApplySetupWizardAsync"/> applies it in dependency
/// order — so going back and forth never triggers a mods-folder rescan or an environment rebuild,
/// and Cancel leaves everything as it was.
/// </summary>
public sealed record SetupWizardDraft
{
    public SetupModManager ModManager { get; init; } = SetupModManager.MO2;
    public SkyrimRelease SkyrimRelease { get; init; } = SkyrimRelease.SkyrimSE;
    public string SkyrimGamePath { get; init; } = string.Empty;
    public string ModsFolder { get; init; } = string.Empty;
    public bool FilterByActiveModsMO2 { get; init; }
    public string MO2ModlistPath { get; init; } = string.Empty;
    public bool UseCuratedMugshots { get; init; } = true;
    public bool UseFaceFinder { get; init; }
    public bool UseAutoGeneration { get; init; }
    public bool UseLiveTiles { get; init; }
    public string MugshotsFolder { get; init; } = string.Empty;
    public string FaceFinderMugshotsFolder { get; init; } = string.Empty;
    public string AutogenMugshotsFolder { get; init; } = string.Empty;
    public MugshotRenderer Renderer { get; init; } = MugshotRenderer.Internal;
    public string OutputDirectory { get; init; } = "NPC Output";
    public string OutputPluginName { get; init; } = "NPC";
    public PatchingMode PatchingMode { get; init; } = PatchingMode.CreateAndPatch;
    public OutputBackend OutputBackend { get; init; } = OutputBackend.ESP;
    public RecastMode RecastMode { get; init; } = RecastMode.Hybrid;
}

public sealed class VM_SetupWizard : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly Func<Task<int>> _testFaceFinder;

    public VM_SetupWizard(SetupWizardDraft initial, string? appliedModsFolder, int existingModCount,
        Func<Task<int>> testFaceFinder)
    {
        _testFaceFinder = testFaceFinder;
        AppliedModsFolder = appliedModsFolder;
        ExistingModCount = existingModCount;

        ModManager = initial.ModManager;
        SkyrimRelease = initial.SkyrimRelease;
        SkyrimGamePath = initial.SkyrimGamePath;
        ModsFolder = initial.ModsFolder;
        FilterByActiveModsMO2 = initial.FilterByActiveModsMO2;
        MO2ModlistPath = initial.MO2ModlistPath;
        UseCuratedMugshots = initial.UseCuratedMugshots;
        UseFaceFinder = initial.UseFaceFinder;
        UseAutoGeneration = initial.UseAutoGeneration;
        UseLiveTiles = initial.UseLiveTiles;
        MugshotsFolder = initial.MugshotsFolder;
        FaceFinderMugshotsFolder = initial.FaceFinderMugshotsFolder;
        AutogenMugshotsFolder = initial.AutogenMugshotsFolder;
        Renderer = initial.Renderer;
        OutputDirectory = initial.OutputDirectory;
        OutputPluginName = initial.OutputPluginName;
        PatchingMode = initial.PatchingMode;
        OutputBackend = initial.OutputBackend;
        RecastMode = initial.RecastMode;

        Steps = new List<WizardStep>
        {
            new ModManagerStep(this),
            new InstallLocationStep(this),
            new GameVersionStep(this),
            new GameDataStep(this),
            new ModsFolderStep(this),
            new ModlistFilterStep(this),
            new MugshotSourcesStep(this),
            new MugshotsFolderStep(this),
            new FaceFinderStep(this),
            new AutoGenerationStep(this),
            new OutputDirectoryStep(this),
            new OutputPluginNameStep(this),
            new PatchingModeStep(this),
            new OutputBackendStep(this),
            new SummaryStep(this),
        };
        CurrentStep = Steps[0];

        var canGoNext = this.WhenAnyValue(x => x.CurrentStep)
            .Select(s => s.WhenAnyValue(x => x.CanProceed))
            .Switch();
        NextCommand = ReactiveCommand.Create(GoNext, canGoNext).DisposeWith(_disposables);
        BackCommand = ReactiveCommand.Create(GoBack, this.WhenAnyValue(x => x.CanGoBack)).DisposeWith(_disposables);
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(false)).DisposeWith(_disposables);
        BrowseFolderCommand = ReactiveCommand.Create<string>(BrowseFolder).DisposeWith(_disposables);
        BrowseModlistCommand = ReactiveCommand.Create(BrowseModlist).DisposeWith(_disposables);
        OpenLinkCommand = ReactiveCommand.Create<string>(OpenLink).DisposeWith(_disposables);
        TestFaceFinderCommand = ReactiveCommand.CreateFromTask(TestFaceFinderAsync).DisposeWith(_disposables);

        // Entering a page validates it; editing a draft value re-validates the page being shown.
        this.WhenAnyValue(x => x.CurrentStep)
            .Subscribe(step =>
            {
                RefreshNavigation();
                _ = step.RevalidateAsync();
            })
            .DisposeWith(_disposables);
        this.Changed
            .Where(e => e.PropertyName is not (nameof(CurrentStep) or nameof(CanGoBack) or nameof(IsLastStep)
                or nameof(StepCounterText) or nameof(NextButtonText) or nameof(FaceFinderTestResult)
                or nameof(LastProbe)))
            .Throttle(TimeSpan.FromMilliseconds(350), RxApp.MainThreadScheduler)
            .Subscribe(_ => CurrentStep.RevalidateAsync())
            .DisposeWith(_disposables);
    }

    public event Action<bool>? CloseRequested;

    /// <summary>The mods folder currently applied in the app (null on a fresh install).</summary>
    public string? AppliedModsFolder { get; }
    public int ExistingModCount { get; }

    // ---------------------------------------------------------------- draft values

    [Reactive] public SetupModManager ModManager { get; set; }
    [Reactive] public SkyrimRelease SkyrimRelease { get; set; }
    [Reactive] public string SkyrimGamePath { get; set; }
    [Reactive] public string ModsFolder { get; set; }
    [Reactive] public bool FilterByActiveModsMO2 { get; set; }
    [Reactive] public string MO2ModlistPath { get; set; }
    [Reactive] public bool UseCuratedMugshots { get; set; }
    [Reactive] public bool UseFaceFinder { get; set; }
    [Reactive] public bool UseAutoGeneration { get; set; }
    [Reactive] public bool UseLiveTiles { get; set; }
    [Reactive] public string MugshotsFolder { get; set; }
    [Reactive] public string FaceFinderMugshotsFolder { get; set; }
    [Reactive] public string AutogenMugshotsFolder { get; set; }
    [Reactive] public MugshotRenderer Renderer { get; set; }
    [Reactive] public string OutputDirectory { get; set; }
    [Reactive] public string OutputPluginName { get; set; }
    [Reactive] public PatchingMode PatchingMode { get; set; }
    [Reactive] public OutputBackend OutputBackend { get; set; }
    [Reactive] public RecastMode RecastMode { get; set; }

    public IReadOnlyList<SetupModManager> ModManagers { get; } = Enum.GetValues<SetupModManager>();
    public IReadOnlyList<SkyrimRelease> SkyrimReleases { get; } = Enum.GetValues<SkyrimRelease>();
    public IReadOnlyList<MugshotRenderer> Renderers { get; } = Enum.GetValues<MugshotRenderer>();
    public IReadOnlyList<PatchingMode> PatchingModes { get; } = Enum.GetValues<PatchingMode>();
    public IReadOnlyList<OutputBackend> OutputBackends { get; } = Enum.GetValues<OutputBackend>();
    public IReadOnlyList<RecastMode> RecastModes { get; } = Enum.GetValues<RecastMode>();

    /// <summary>Last trial environment build (Game Data page); later pages read its Data folder and plugins.</summary>
    [Reactive] public EnvironmentProbe? LastProbe { get; set; }

    /// <summary>The Data folder later checks compare against: the probed one, else the typed path.</summary>
    public string? EffectiveDataFolder =>
        LastProbe?.DataFolderPath ?? (string.IsNullOrWhiteSpace(SkyrimGamePath) ? null : SkyrimGamePath);

    /// <summary>null until the user runs the FaceFinder connection test; then the number of mods it returned (-1: failed).</summary>
    [Reactive] public int? FaceFinderTestResult { get; private set; }

    public SetupWizardDraft ToDraft() => new()
    {
        ModManager = ModManager,
        SkyrimRelease = SkyrimRelease,
        SkyrimGamePath = SkyrimGamePath?.Trim() ?? string.Empty,
        ModsFolder = ModsFolder?.Trim() ?? string.Empty,
        FilterByActiveModsMO2 = FilterByActiveModsMO2 && ModManager != SetupModManager.Other,
        MO2ModlistPath = MO2ModlistPath ?? string.Empty,
        UseCuratedMugshots = UseCuratedMugshots,
        UseFaceFinder = UseFaceFinder,
        UseAutoGeneration = UseAutoGeneration,
        UseLiveTiles = UseLiveTiles,
        MugshotsFolder = MugshotsFolder?.Trim() ?? string.Empty,
        FaceFinderMugshotsFolder = FaceFinderMugshotsFolder?.Trim() ?? string.Empty,
        AutogenMugshotsFolder = AutogenMugshotsFolder?.Trim() ?? string.Empty,
        Renderer = Renderer,
        OutputDirectory = OutputDirectory?.Trim() ?? string.Empty,
        OutputPluginName = Path.GetFileNameWithoutExtension(OutputPluginName?.Trim() ?? string.Empty),
        PatchingMode = PatchingMode,
        OutputBackend = OutputBackend,
        RecastMode = RecastMode,
    };

    // ---------------------------------------------------------------- navigation

    public IReadOnlyList<WizardStep> Steps { get; }
    [Reactive] public WizardStep CurrentStep { get; private set; }
    [Reactive] public bool CanGoBack { get; private set; }
    [Reactive] public bool IsLastStep { get; private set; }
    [Reactive] public string StepCounterText { get; private set; } = string.Empty;
    [Reactive] public string NextButtonText { get; private set; } = "Next >";

    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<string, Unit> BrowseFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseModlistCommand { get; }
    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }
    public ReactiveCommand<Unit, Unit> TestFaceFinderCommand { get; }

    private List<WizardStep> ApplicableSteps => Steps.Where(s => s.IsApplicable).ToList();

    private void GoNext()
    {
        var steps = ApplicableSteps;
        int index = steps.IndexOf(CurrentStep);
        if (index >= steps.Count - 1)
        {
            CloseRequested?.Invoke(true);
            return;
        }
        CurrentStep = steps[index + 1];
    }

    private void GoBack()
    {
        var steps = ApplicableSteps;
        int index = steps.IndexOf(CurrentStep);
        if (index > 0) CurrentStep = steps[index - 1];
    }

    private void RefreshNavigation()
    {
        var steps = ApplicableSteps;
        int index = steps.IndexOf(CurrentStep);
        CanGoBack = index > 0;
        IsLastStep = index == steps.Count - 1;
        StepCounterText = $"Step {index + 1} of {steps.Count}";
        NextButtonText = IsLastStep ? "Finish" : "Next >";
    }

    // ---------------------------------------------------------------- page actions

    /// <summary>Folder picker for the draft property named by <paramref name="propertyName"/>.</summary>
    private void BrowseFolder(string propertyName)
    {
        var property = GetType().GetProperty(propertyName);
        if (property == null) return;
        string? current = property.GetValue(this) as string;

        using var dialog = new CommonOpenFileDialog
        {
            IsFolderPicker = true,
            Title = "Select Folder",
            InitialDirectory = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current) ? current : null,
        };
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            property.SetValue(this, dialog.FileName);
        }
    }

    private void BrowseModlist()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select MO2 modlist.txt",
            Filter = "Text files (modlist.txt)|modlist.txt|All files (*.*)|*.*",
            FileName = "modlist.txt",
        };
        if (!string.IsNullOrWhiteSpace(MO2ModlistPath) && File.Exists(MO2ModlistPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(MO2ModlistPath);
        }
        if (dialog.ShowDialog() == true) MO2ModlistPath = dialog.FileName;
    }

    private static void OpenLink(string url)
    {
        try
        {
            // Shell-executing a URL silently does nothing when launched under MO2; OpenUrl goes through Explorer.
            Auxilliary.OpenUrl(url);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open {url}: {ex.Message}");
        }
    }

    private async Task TestFaceFinderAsync()
    {
        try
        {
            FaceFinderTestResult = await _testFaceFinder();
        }
        catch
        {
            FaceFinderTestResult = -1;
        }
        await CurrentStep.RevalidateAsync();
    }

    public void Dispose() => _disposables.Dispose();
}
