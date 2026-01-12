using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Newtonsoft.Json;
using Noggog; // For IsNullOrWhitespace
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using NPC_Plugin_Chooser_2.Themes;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

public enum MugshotSearchMode
{
    Fast,
    Comprehensive
}

public class VM_Settings : ReactiveObject, IDisposable, IActivatableViewModel
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Auxilliary _aux;
    private readonly Settings _model; // Renamed from settings to _model for clarity
    private readonly Lazy<VM_NpcSelectionBar> _lazyNpcSelectionBar;
    private readonly Lazy<VM_Mods> _lazyModListVM;
    private readonly NpcConsistencyProvider _consistencyProvider;
    private readonly Lazy<VM_MainWindow> _lazyMainWindowVm;
    private readonly EasyNpcTranslator _easyNpcTranslator;
    private readonly PortraitCreator _portraitCreator;
    private readonly EventLogger _eventLogger;

    public ViewModelActivator Activator { get; } = new ViewModelActivator();

    public int NumPluginsInEnvironment => _environmentStateProvider.NumPlugins;
    public int NumActivePluginsInEnvironment => _environmentStateProvider.NumActivePlugins;

    // --- Existing & Modified Properties ---
    [Reactive] public string ModsFolder { get; set; }
    [Reactive] public string MugshotsFolder { get; set; }
    [Reactive] public SkyrimRelease SkyrimRelease { get; set; }
    
    // --- NEW: Mugshot Fallback Properties ---
    [Reactive] public bool UseFaceFinderFallback { get; set; }
    [Reactive] public bool CacheFaceFinderImages { get; set; } 
    [Reactive] public bool LogFaceFinderRequests { get; set; }
    [Reactive] public bool UsePortraitCreatorFallback { get; set; }
    [Reactive] public int MaxParallelPortraitRenders { get; set; }
    [Reactive] public SolidColorBrush MugshotBackgroundColor { get; set; }
    [Reactive] public string DefaultLightingJsonString { get; set; }
    [ObservableAsProperty] public bool IsLightingJsonValid { get; }

    // --- NEW: Portrait Creator Camera Properties ---
    [Reactive] public bool AutoUpdateOldMugshots { get; set; }
    [Reactive] public bool AutoUpdateStaleMugshots { get; set; }
    [Reactive] public PortraitCameraMode SelectedCameraMode { get; set; }
    public IEnumerable<PortraitCameraMode> CameraModes { get; } = Enum.GetValues(typeof(PortraitCameraMode)).Cast<PortraitCameraMode>();
    [Reactive] public float VerticalFOV { get; set; }
    [Reactive] public float CamX { get; set; }
    [Reactive] public float CamY { get; set; }
    [Reactive] public float CamZ { get; set; }
    [Reactive] public float CamPitch { get; set; }
    [Reactive] public float CamYaw { get; set; }
    [Reactive] public float CamRoll { get; set; }
    [Reactive] public float HeadTopOffset { get; set; }
    [Reactive] public float HeadBottomOffset { get; set; }
    [Reactive] public int ImageXRes { get; set; }
    [Reactive] public int ImageYRes { get; set; }
    [Reactive] public bool EnableNormalMapHack { get; set; }
    [Reactive] public bool UseModdedFallbackTextures { get; set; }
    [Reactive] public string SkyrimGamePath { get; set; }
    [Reactive] public MugshotSearchMode SelectedMugshotSearchModePC { get; set; }
    [Reactive] public MugshotSearchMode SelectedMugshotSearchModeFF { get; set; }
    public IEnumerable<MugshotSearchMode> MugshotSearchModes { get; } = Enum.GetValues(typeof(MugshotSearchMode)).Cast<MugshotSearchMode>();


    // TargetPluginName now maps to the conceptual name in the model
    [Reactive] public string TargetPluginName { get; set; }

    // --- New Output Settings Properties ---
    [Reactive] public string OutputDirectory { get; set; }
    [Reactive] public bool AppendTimestampToOutputDirectory { get; set; }
    [Reactive] public bool UseSkyPatcherMode { get; set; }
    [Reactive] public bool AutoEslIfy { get; set; } = true;
    [Reactive] public bool SplitOutput { get; set; }
    [Reactive] public bool SplitOutputByGender { get; set; }
    [Reactive] public bool SplitOutputByRace { get; set; }
    [Reactive] public int? SplitOutputMaxNpcs { get; set; }

    [Reactive] public PatchingMode SelectedPatchingMode { get; set; }
    public IEnumerable<PatchingMode> PatchingModes { get; } = Enum.GetValues(typeof(PatchingMode)).Cast<PatchingMode>();
    [Reactive] public RecordOverrideHandlingMode SelectedRecordOverrideHandlingMode { get; set; }

    public IEnumerable<RecordOverrideHandlingMode> RecordOverrideHandlingModes { get; } =
        Enum.GetValues(typeof(RecordOverrideHandlingMode)).Cast<RecordOverrideHandlingMode>();

    public ReactiveCommand<Unit, Unit> ShowOverrideHandlingModeHelpCommand { get; }

    // --- New EasyNPC Transfer Properties ---
    // Assuming ModKeyMultiPicker binds directly to an ObservableCollection-like source
    // We will wrap the HashSet from the model. Consider a more robust solution if direct binding causes issues.
    [Reactive] public VM_ModSelector ExclusionSelectorViewModel { get; private set; }
    [ObservableAsProperty] public IEnumerable<ModKey> AvailablePluginsForExclusion { get; }

    // --- Bat File Properties
    [Reactive] public string BatFilePreCommands { get; set; }
    [Reactive] public string BatFilePostCommands { get; set; }

    // --- Read-only properties reflecting environment state ---
    [ObservableAsProperty] public EnvironmentStateProvider.EnvironmentStatus EnvironmentStatus { get; }
    [ObservableAsProperty] public string EnvironmentErrorText { get; }

    // --- Properties for modifying EasyNPC Import/Export ---
    [Reactive] public bool AddMissingNpcsOnUpdate { get; set; } = true; // Default to true

    // --- Properties for Auto-Selection of NPC Appearances
    [Reactive] public VM_ModSelector ImportFromLoadOrderExclusionSelectorViewModel { get; private set; }

    // --- NEW: Properties for Non-Appearance Mod Filtering ---
    [Reactive] public string NonAppearanceModFilterText { get; set; } = string.Empty;
    public ObservableCollection<KeyValuePair<string, string>> FilteredNonAppearanceMods { get; } = new();

    // MODIFIED: Type changed to handle dictionary from model
    public ObservableCollection<KeyValuePair<string, string>> CachedNonAppearanceMods { get; private set; }

    // --- New: Properties for display settings
    [Reactive] public bool NormalizeImageDimensions { get; set; }
    [Reactive] public int MaxMugshotsToFit { get; set; }
    [Reactive] public bool IsDarkMode { get; set; }

    [Reactive] public bool SuppressPopupWarnings { get; set; }

    // --- NEW: Localization Settings ---
    [Reactive] public bool IsLocalizationEnabled { get; set; }
    [Reactive] public Language? SelectedLocalizationLanguage { get; set; }

    public IEnumerable<Language> AvailableLanguages { get; } = Enum.GetValues(typeof(Language)).Cast<Language>();
    [Reactive] public bool FixGarbledText { get; set; } = false;

    // --- NPC Display ---
    [Reactive] public bool ShowNpcNameInList { get; set; } = true;
    [Reactive] public bool ShowNpcEditorIdInList { get; set; }
    [Reactive] public bool ShowNpcFormKeyInList { get; set; }
    [Reactive] public bool ShowNpcFormIdInList { get; set; }
    [Reactive] public string NpcListSeparator { get; set; } = " | ";
    
    [Reactive] public bool LogActivity { get; set; }

    // For throttled saving
    private readonly Subject<Unit> _saveRequestSubject = new Subject<Unit>();
    private readonly CompositeDisposable _disposables = new CompositeDisposable(); // To manage subscriptions
    private readonly TimeSpan _saveThrottleTime = TimeSpan.FromMilliseconds(1500);

    // --- Commands ---
    public ReactiveCommand<Unit, Unit> SelectGameFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectModsFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectMugshotsFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectOutputDirectoryCommand { get; } // New
    public ReactiveCommand<Unit, Unit> ShowPatchingModeHelpCommand { get; } // New
    public ReactiveCommand<Unit, Unit> ImportEasyNpcCommand { get; } // New
    public ReactiveCommand<Unit, Unit> ExportEasyNpcCommand { get; } // New
    public ReactiveCommand<bool, Unit> UpdateEasyNpcProfileCommand { get; } // Takes bool parameter
    public ReactiveCommand<string, Unit> RemoveCachedModCommand { get; }
    public ReactiveCommand<string, Unit> RescanCachedModCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenModLinkerCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCachedFaceFinderImagesCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectBackgroundColorCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteAutoGeneratedMugshotsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowFullEnvironmentErrorCommand { get; }

    public VM_Settings(
        EnvironmentStateProvider environmentStateProvider,
        Auxilliary aux,
        Settings settingsModel,
        Lazy<VM_NpcSelectionBar> lazyNpcSelectionBar,
        Lazy<VM_Mods> lazyModListVm,
        NpcConsistencyProvider consistencyProvider,
        Lazy<VM_MainWindow> lazyMainWindowVm,
        EasyNpcTranslator easyNpcTranslator,
        PortraitCreator portraitCreator,
        EventLogger eventLogger)
    {
        _model = settingsModel;
        
        _environmentStateProvider = environmentStateProvider;
        _environmentStateProvider.OnEnvironmentUpdated
            .ObserveOn(RxApp.MainThreadScheduler) // Ensure UI updates happen on the UI thread
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(NumPluginsInEnvironment));
                this.RaisePropertyChanged(nameof(NumActivePluginsInEnvironment));
            })
            .DisposeWith(_disposables);

        // At the time ths constructor is called, the model has already been read in via App.xaml.cs
        // This should be refactored later, but for now treat the _model values as the ground truth
        SkyrimRelease = _model?.SkyrimRelease ?? SkyrimRelease.SkyrimSE;
        SkyrimGamePath = _model?.SkyrimGamePath ?? string.Empty;
        TargetPluginName =
            _model?.OutputPluginName ??
            EnvironmentStateProvider.DefaultPluginName; // Model's OutputPluginName is the source of truth
        if (TargetPluginName.IsNullOrEmpty())
        {
            TargetPluginName = EnvironmentStateProvider.DefaultPluginName;
        }

        // Now that the environment parameters have been set, initialize the environment
        _environmentStateProvider.SetEnvironmentTarget(SkyrimRelease, SkyrimGamePath, TargetPluginName);
        _environmentStateProvider.UpdateEnvironment();
        // Finished environment initialization

        _aux = aux;
        _lazyNpcSelectionBar = lazyNpcSelectionBar;
        _lazyModListVM = lazyModListVm;
        _consistencyProvider = consistencyProvider;
        _lazyMainWindowVm = lazyMainWindowVm;
        _easyNpcTranslator = easyNpcTranslator;
        _portraitCreator = portraitCreator;
        _eventLogger = eventLogger;

        // Initialize other VM properties from the model
        ModsFolder = _model.ModsFolder;
        MugshotsFolder = _model.MugshotsFolder;
        
        // --- NEW: Mugshot Fallback Initialization ---
        
        UseFaceFinderFallback = _model.UseFaceFinderFallback;
        CacheFaceFinderImages = _model.CacheFaceFinderImages;
        LogFaceFinderRequests = _model.LogFaceFinderRequests;
        SelectedMugshotSearchModeFF = MugshotSearchMode.Fast;
        UsePortraitCreatorFallback = _model.UsePortraitCreatorFallback;
        MaxParallelPortraitRenders = _model.MaxParallelPortraitRenders;

        // --- NEW: Portrait Creator Initialization ---
        AutoUpdateOldMugshots = _model.AutoUpdateOldMugshots;
        AutoUpdateStaleMugshots = _model.AutoUpdateStaleMugshots;
        MugshotBackgroundColor = new SolidColorBrush(_model.MugshotBackgroundColor);
        if (MugshotBackgroundColor.CanFreeze) MugshotBackgroundColor.Freeze(); // Good practice
        DefaultLightingJsonString = _model.DefaultLightingJsonString;
        EnableNormalMapHack = _model.EnableNormalMapHack;
        UseModdedFallbackTextures = _model.UseModdedFallbackTextures;
        SelectedCameraMode = _model.SelectedCameraMode;
        VerticalFOV = _model.VerticalFOV;
        CamX = _model.CamX;
        CamY = _model.CamY;
        CamZ = _model.CamZ;
        CamPitch = _model.CamPitch;
        CamYaw = _model.CamYaw;
        CamRoll = _model.CamRoll;
        HeadTopOffset = _model.HeadTopOffset;
        HeadBottomOffset = _model.HeadBottomOffset;
        ImageXRes = _model.ImageXRes;
        ImageYRes = _model.ImageYRes;
        SelectedMugshotSearchModePC = MugshotSearchMode.Fast;
        
        OutputDirectory = _model.OutputDirectory;
        UseSkyPatcherMode = _model.UseSkyPatcherMode;
        AutoEslIfy = _model.AutoEslIfy;
        SplitOutput = _model.SplitOutput;
        SplitOutputByGender = _model.SplitOutputByGender;
        SplitOutputByRace = _model.SplitOutputByRace;
        SplitOutputMaxNpcs = _model.SplitOutputMaxNpcs;
        AppendTimestampToOutputDirectory = _model.AppendTimestampToOutputDirectory;
        SelectedPatchingMode = _model.PatchingMode;
        SelectedRecordOverrideHandlingMode = _model.DefaultRecordOverrideHandlingMode;
        AddMissingNpcsOnUpdate = _model.AddMissingNpcsOnUpdate;
        BatFilePreCommands = _model.BatFilePreCommands;
        BatFilePostCommands = _model.BatFilePostCommands;
        NormalizeImageDimensions = _model.NormalizeImageDimensions;
        MaxMugshotsToFit = _model.MaxMugshotsToFit;
        SuppressPopupWarnings = _model.SuppressPopupWarnings;
        IsLocalizationEnabled = _model.LocalizationLanguage.HasValue;
        SelectedLocalizationLanguage = _model.LocalizationLanguage;
        FixGarbledText = _model.FixGarbledText;
        IsDarkMode = _model.IsDarkMode;
        ShowNpcNameInList = _model.ShowNpcNameInList;
        ShowNpcEditorIdInList = _model.ShowNpcEditorIdInList;
        ShowNpcFormKeyInList = _model.ShowNpcFormKeyInList;
        ShowNpcFormIdInList = _model.ShowNpcFormIdInList;
        NpcListSeparator = _model.NpcListSeparator;
        
        LogActivity = _model.LogActivity;

        ExclusionSelectorViewModel = new VM_ModSelector(); // Initialize early
        ImportFromLoadOrderExclusionSelectorViewModel = new VM_ModSelector();

        // Initialize the collection from the model, ordered by folder name for consistent display.
        CachedNonAppearanceMods = new ObservableCollection<KeyValuePair<string, string>>(
            _model.CachedNonAppearanceMods.OrderBy(kvp => Path.GetFileName(kvp.Key) ?? kvp.Key,
                StringComparer.OrdinalIgnoreCase));

        // Commands (as before)
        SelectGameFolderCommand = ReactiveCommand.CreateFromTask(SelectGameFolderAsync).DisposeWith(_disposables);
        SelectModsFolderCommand = ReactiveCommand.CreateFromTask(SelectModsFolderAsync).DisposeWith(_disposables);
        SelectMugshotsFolderCommand =
            ReactiveCommand.CreateFromTask(SelectMugshotsFolderAsync).DisposeWith(_disposables);
        SelectOutputDirectoryCommand =
            ReactiveCommand.CreateFromTask(SelectOutputDirectoryAsync).DisposeWith(_disposables);
        ShowPatchingModeHelpCommand = ReactiveCommand.Create(ShowPatchingModeHelp).DisposeWith(_disposables);
        ShowOverrideHandlingModeHelpCommand =
            ReactiveCommand.Create(ShowOverrideHandlingModeHelp).DisposeWith(_disposables);
        ImportEasyNpcCommand = ReactiveCommand.Create(_easyNpcTranslator.ImportEasyNpc).DisposeWith(_disposables);
        ExportEasyNpcCommand = ReactiveCommand.Create(_easyNpcTranslator.ExportEasyNpc).DisposeWith(_disposables);
        UpdateEasyNpcProfileCommand = ReactiveCommand.CreateFromTask<bool>(_easyNpcTranslator.UpdateEasyNpcProfile)
            .DisposeWith(_disposables);
        RemoveCachedModCommand = ReactiveCommand.Create<string>(path =>
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            // Remove from the underlying model dictionary
            _model.CachedNonAppearanceMods.Remove(path);

            // Find and remove the KeyValuePair from the view model collections
            var itemToRemove = CachedNonAppearanceMods.FirstOrDefault(kvp => kvp.Key == path);
            if (!itemToRemove.Equals(default(KeyValuePair<string, string>)))
            {
                CachedNonAppearanceMods.Remove(itemToRemove);
                FilteredNonAppearanceMods.Remove(itemToRemove);
            }
        }).DisposeWith(_disposables);
        RescanCachedModCommand =
            ReactiveCommand.CreateFromTask<string>(RescanCachedModAsync).DisposeWith(_disposables);
        RescanCachedModCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Failed to re-scan mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        OpenModLinkerCommand = ReactiveCommand.Create(OpenModLinkerWindow);
        DeleteCachedFaceFinderImagesCommand = ReactiveCommand.CreateFromTask(DeleteCachedFaceFinderImagesAsync).DisposeWith(_disposables);
        DeleteCachedFaceFinderImagesCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error deleting cached FaceFinder images: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        SelectBackgroundColorCommand = ReactiveCommand.Create(SelectBackgroundColor).DisposeWith(_disposables);
        DeleteAutoGeneratedMugshotsCommand = ReactiveCommand.CreateFromTask(DeleteAutoGeneratedMugshotsAsync).DisposeWith(_disposables);
        DeleteAutoGeneratedMugshotsCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error deleting auto-generated mugshots: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        
        ShowFullEnvironmentErrorCommand = ReactiveCommand.Create(() => 
            ScrollableMessageBox.ShowError(EnvironmentErrorText, "Environment Error")).DisposeWith(_disposables);

        // Property subscriptions (as before, ensure .Skip(1) where appropriate if values are set above)
        // These update the model or _environmentStateProvider's direct properties
        this.WhenAnyValue(x => x.ModsFolder).Skip(1).Subscribe(s => _model.ModsFolder = s)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.MugshotsFolder).Skip(1).Subscribe(s => _model.MugshotsFolder = s)
            .DisposeWith(_disposables);
        // --- NEW: Subscriptions for Mugshot Settings ---
        this.WhenAnyValue(x => x.UseFaceFinderFallback).Skip(1)
            .Subscribe(b => _model.UseFaceFinderFallback = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.CacheFaceFinderImages).Skip(1) 
            .Subscribe(b => _model.CacheFaceFinderImages = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.LogFaceFinderRequests).Skip(1)
            .Subscribe(b => _model.LogFaceFinderRequests = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SelectedMugshotSearchModeFF)
            .Skip(1)
            .Subscribe(mode => _model.SelectedMugshotSearchModeFF = mode)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.UsePortraitCreatorFallback).Skip(1)
            .Subscribe(b => _model.UsePortraitCreatorFallback = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.MaxParallelPortraitRenders).Skip(1)
            .Subscribe(value => _model.MaxParallelPortraitRenders = value).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.AutoUpdateOldMugshots).Skip(1)
            .Subscribe(b => _model.AutoUpdateOldMugshots = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.AutoUpdateStaleMugshots).Skip(1)
            .Subscribe(b => _model.AutoUpdateStaleMugshots = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SelectedCameraMode).Skip(1)
            .Subscribe(mode => _model.SelectedCameraMode = mode).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.VerticalFOV).Skip(1).Subscribe(f => _model.VerticalFOV = f).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.CamX).Skip(1).Subscribe(f => _model.CamX = f).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.CamY).Skip(1).Subscribe(f => _model.CamY = f).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.CamZ).Skip(1).Subscribe(f => _model.CamZ = f).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.CamPitch).Skip(1).Subscribe(f => _model.CamPitch = f).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.CamYaw).Skip(1).Subscribe(f => _model.CamYaw = f).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.CamRoll).Skip(1).Subscribe(f => _model.CamRoll = f).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.HeadTopOffset).Skip(1).Subscribe(f => _model.HeadTopOffset = f).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.HeadBottomOffset).Skip(1).Subscribe(f => _model.HeadBottomOffset = f).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.ImageXRes).Skip(1).Subscribe(i => _model.ImageXRes = i).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.ImageYRes).Skip(1).Subscribe(i => _model.ImageYRes = i).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.EnableNormalMapHack).Skip(1)
            .Subscribe(b => _model.EnableNormalMapHack = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.UseModdedFallbackTextures).Skip(1)
            .Subscribe(b => _model.UseModdedFallbackTextures = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.OutputDirectory).Skip(1).Subscribe(s => _model.OutputDirectory = s)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SelectedMugshotSearchModePC)
            .Skip(1)
            .Subscribe(mode => _model.SelectedMugshotSearchModePC = mode)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.AppendTimestampToOutputDirectory).Skip(1)
            .Subscribe(b => _model.AppendTimestampToOutputDirectory = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SelectedPatchingMode).Skip(1).Subscribe(pm => _model.PatchingMode = pm)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.AddMissingNpcsOnUpdate).Skip(1).Subscribe(b => _model.AddMissingNpcsOnUpdate = b)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.BatFilePreCommands).Skip(1).Subscribe(s => _model.BatFilePreCommands = s)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.BatFilePostCommands).Skip(1).Subscribe(s => _model.BatFilePostCommands = s)
            .DisposeWith(_disposables);
        // Subscribe to property changes to update the model
        this.WhenAnyValue(x => x.ShowNpcNameInList).Skip(1).Subscribe(b => _model.ShowNpcNameInList = b)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.ShowNpcEditorIdInList).Skip(1).Subscribe(b => _model.ShowNpcEditorIdInList = b)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.ShowNpcFormKeyInList).Skip(1).Subscribe(b => _model.ShowNpcFormKeyInList = b)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.ShowNpcFormIdInList).Skip(1).Subscribe(b => _model.ShowNpcFormIdInList = b)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.NpcListSeparator).Skip(1).Subscribe(s => _model.NpcListSeparator = s)
            .DisposeWith(_disposables);

        // Combine all display setting changes into a single observable to trigger updates
        var npcDisplaySettingsChanged = Observable.Merge(
            this.WhenAnyValue(x => x.ShowNpcNameInList).Select(_ => Unit.Default),
            this.WhenAnyValue(x => x.ShowNpcEditorIdInList).Select(_ => Unit.Default),
            this.WhenAnyValue(x => x.ShowNpcFormKeyInList).Select(_ => Unit.Default),
            this.WhenAnyValue(x => x.ShowNpcFormIdInList).Select(_ => Unit.Default),
            this.WhenAnyValue(x => x.NpcListSeparator).Select(_ => Unit.Default)
        );

        npcDisplaySettingsChanged
            .Skip(5) // Skip the initial values set during construction
            .Throttle(TimeSpan.FromMilliseconds(200), RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                // If the NPC view has been created, tell it to refresh its display names
                if (_lazyNpcSelectionBar.IsValueCreated)
                {
                    _lazyNpcSelectionBar.Value.RefreshAllNpcDisplayNames();
                }
            })
            .DisposeWith(_disposables);

        // Also, update the language change subscription to use the new refresh mechanism
        this.WhenAnyValue(x => x.SelectedLocalizationLanguage)
            .Skip(1) // Skip initial value
            .Subscribe(lang =>
            {
                _model.LocalizationLanguage = lang;
                if (_lazyNpcSelectionBar.IsValueCreated)
                {
                    // This now calls the central refresh method
                    _lazyNpcSelectionBar.Value.RefreshAllNpcDisplayNames();
                }
            })
            .DisposeWith(_disposables);
        
        this.WhenAnyValue(x => x.FixGarbledText)
            .Skip(1)
            .Subscribe(x => _model.FixGarbledText = x)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.SuppressPopupWarnings)
            .Skip(1)
            .Subscribe(b => _model.SuppressPopupWarnings = b)
            .DisposeWith(_disposables);
        
        this.WhenAnyValue(x => x.LogActivity)
            .Skip(1)
            .Subscribe(b => _model.LogActivity = b)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.IsLocalizationEnabled)
            .Skip(1) // Skip initial value set from model
            .Subscribe(enabled =>
            {
                if (!enabled)
                {
                    // If localization is disabled, clear the selected language
                    SelectedLocalizationLanguage = null;
                }
                else if (SelectedLocalizationLanguage == null)
                {
                    // If it's enabled and no language is selected, default to English
                    SelectedLocalizationLanguage = Language.English;
                }
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.IsDarkMode)
            .Skip(1) // Skip initial value set from model
            .Subscribe(isDark =>
            {
                _model.IsDarkMode = isDark;
                ThemeManager.ApplyTheme(isDark);
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.SelectedRecordOverrideHandlingMode)
            .Skip(1) // Skip the initial value loaded from the model
            .Subscribe(mode =>
            {
                // Only show the warning if the user selects a mode other than the default 'Ignore'
                if (mode != RecordOverrideHandlingMode.Ignore && !SuppressPopupWarnings)
                {
                    const string message =
                        "WARNING: Setting the override handling mode to anything other than 'Ignore' is generally not recommended.\n\n" +
                        "It can significantly increase patching time and is only necessary in very specific, rare scenarios.\n\n" +
                        "You're almost certainly better off changing this setting only for specific mods in the Mods Menu.\n\n" +
                        "Are you sure you want to change this setting?";

                    if (!ScrollableMessageBox.Confirm(message, "Confirm Override Handling Mode",
                            MessageBoxImage.Warning))
                    {
                        // If the user clicks "No", use Observable.Timer to schedule the reversion.
                        // This runs on the UI thread after a 1ms delay, breaking the call-stack and ensuring the ComboBox updates.
                        Observable.Timer(TimeSpan.FromMilliseconds(1), RxApp.MainThreadScheduler)
                            .Subscribe(_ =>
                            {
                                SelectedRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore;
                            });
                    }
                }

                // Persist the final value to the model. This will be the original value if the
                // user confirmed, or it will be re-triggered with 'Ignore' if they cancelled.
                _model.DefaultRecordOverrideHandlingMode = mode;
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.UseSkyPatcherMode)
            .Skip(1) // Skip initial value set on load
            .Subscribe(useSkyPatcher =>
            {
                if (useSkyPatcher) // If the checkbox was just checked
                {
                    const string message =
                        "SkyPatcher is a powerful tool for overwriting conflicts. Be aware that it might conflict with other runtime editing tools such as RSV and SynthEBD. Are you sure you want to use SkyPatcher Mode?";
                    if (!SuppressPopupWarnings && !ScrollableMessageBox.Confirm(message, "Confirm SkyPatcher Mode"))
                    {
                        // If user clicks "No", revert the checkbox state
                        UseSkyPatcherMode = false;
                    }
                }

                // Persist the final state (whether confirmed true or set to false) to the model
                _model.UseSkyPatcherMode = UseSkyPatcherMode;
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.AutoEslIfy)
            .Skip(1)
            .Subscribe(b => _model.AutoEslIfy = b)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.SplitOutput).Skip(1).Subscribe(b => _model.SplitOutput = b)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SplitOutputByGender).Skip(1).Subscribe(b => _model.SplitOutputByGender = b)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SplitOutputByRace).Skip(1).Subscribe(b => _model.SplitOutputByRace = b)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SplitOutputMaxNpcs).Skip(1).Subscribe(i => _model.SplitOutputMaxNpcs = i)
            .DisposeWith(_disposables);


        // Consolidate all environment update logic into a single, throttled subscription.
        var gameEnvironmentChanged = Observable.Merge(
            this.WhenAnyValue(x => x.SkyrimRelease).Select(_ => Unit.Default),
            this.WhenAnyValue(x => x.SkyrimGamePath).Select(_ => Unit.Default),
            this.WhenAnyValue(x => x.TargetPluginName).Select(_ => Unit.Default));

        gameEnvironmentChanged
            .Skip(3) // Skip the 3 initial values set in the constructor
            .Throttle(TimeSpan.FromMilliseconds(250),
                RxApp.MainThreadScheduler) // Throttle to prevent rapid-fire updates
            .Subscribe(_ =>
            {
                // 1. Update the model to persist the new settings
                _model.SkyrimRelease = SkyrimRelease;
                _model.SkyrimGamePath = SkyrimGamePath;
                _model.OutputPluginName = TargetPluginName;

                // 2. Set the target and update the environment
                _environmentStateProvider.SetEnvironmentTarget(SkyrimRelease, SkyrimGamePath, TargetPluginName);
                _environmentStateProvider.UpdateEnvironment();
            })
            .DisposeWith(_disposables);

        // OAPHs for environment state
        _environmentStateProvider.WhenAnyValue(x => x.Status)
            .ToPropertyEx(this, x => x.EnvironmentStatus)
            .DisposeWith(_disposables);

        _environmentStateProvider.WhenAnyValue(x => x.EnvironmentBuilderError)
            .Select(err => string.IsNullOrWhiteSpace(err) ? string.Empty : $"Environment Error: {err}")
            .ToPropertyEx(this, x => x.EnvironmentErrorText)
            .DisposeWith(_disposables);

        // Populate AvailablePluginsForExclusion
        this.WhenAnyValue(x => x.EnvironmentStatus)
            .Select(_ =>
                _environmentStateProvider.Status == EnvironmentStateProvider.EnvironmentStatus.Valid &&
                _environmentStateProvider.LoadOrder != null
                    ? _environmentStateProvider.LoadOrder.Keys
                        .OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase).ToList()
                    : Enumerable.Empty<ModKey>())
            .ObserveOn(RxApp.MainThreadScheduler)
            .ToPropertyEx(this, x => x.AvailablePluginsForExclusion)
            .DisposeWith(_disposables);


        Observable.Merge(
                this.WhenAnyValue(x => x.NonAppearanceModFilterText).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.CachedNonAppearanceMods.Count)
                    .Select(_ => Unit.Default) // Use Count property
            )
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => { ApplyNonAppearanceFilter(); })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.AvailablePluginsForExclusion)
            .Where(list => list != null)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(availablePlugins =>
            {
                var currentUISelctions = ExclusionSelectorViewModel.SaveToModel(); // Save current UI state
                ExclusionSelectorViewModel.LoadFromModel(availablePlugins, currentUISelctions,
                    _environmentStateProvider?.LoadOrder?.ListedOrder?.Select(x => x.ModKey).ToList() ??
                    new List<ModKey>()); // Reload with new available, preserving selections

                // ADDED: Do the same for the new selector
                var currentLoadOrderExclusionSelections =
                    ImportFromLoadOrderExclusionSelectorViewModel.SaveToModel();
                ImportFromLoadOrderExclusionSelectorViewModel.LoadFromModel(availablePlugins,
                    currentLoadOrderExclusionSelections,
                    _environmentStateProvider?.LoadOrder?.ListedOrder?.Select(x => x.ModKey).ToList() ??
                    new List<ModKey>());
            }).DisposeWith(_disposables);

        this.WhenAnyValue(x => x.NormalizeImageDimensions)
            .Skip(1) // Skip the initial value set from the model
            .Subscribe(b => _model.NormalizeImageDimensions = b)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.MaxMugshotsToFit)
            .Skip(1)
            .Subscribe(val => _model.MaxMugshotsToFit = val)
            .DisposeWith(_disposables);

        _saveRequestSubject
            .Throttle(_saveThrottleTime, RxApp.TaskpoolScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => SaveSettings())
            .DisposeWith(_disposables);

        this.WhenActivated(d =>
        {
            // Initial load of exclusions (if EnvironmentStatus might already be true from construction)
            if (EnvironmentStatus == EnvironmentStateProvider.EnvironmentStatus.Valid &&
                AvailablePluginsForExclusion != null && AvailablePluginsForExclusion.Any())
            {
                ExclusionSelectorViewModel.LoadFromModel(AvailablePluginsForExclusion,
                    _model.EasyNpcDefaultPluginExclusions,
                    _environmentStateProvider.LoadOrder?.ListedOrder?.Select(x => x.ModKey).ToList() ??
                    new List<ModKey>());
                ImportFromLoadOrderExclusionSelectorViewModel.LoadFromModel(AvailablePluginsForExclusion,
                    _model.ImportFromLoadOrderExclusions,
                    _environmentStateProvider.LoadOrder?.ListedOrder?.Select(x => x.ModKey).ToList() ??
                    new List<ModKey>());
            }

            // Subscribe to NPC Selection changes for saving LastSelectedNpc
            _lazyNpcSelectionBar.Value
                .WhenAnyValue(x => x.SelectedNpc)
                .Skip(1) // Skip initial
                .Subscribe(npc =>
                {
                    _model.LastSelectedNpcFormKey = npc?.NpcFormKey ?? FormKey.Null;
                    RequestThrottledSave();
                })
                .DisposeWith(d);

            _disposables.Add(d);
        });
        
        this.WhenAnyValue(x => x.MugshotBackgroundColor).Skip(1)
            .Subscribe(brush => _model.MugshotBackgroundColor = brush.Color)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.DefaultLightingJsonString).Skip(1)
            .Subscribe(json => _model.DefaultLightingJsonString = json).DisposeWith(_disposables);

        // --- NEW: JSON validation logic ---
        this.WhenAnyValue(x => x.DefaultLightingJsonString)
            .Select(IsValidJson)
            .ToPropertyEx(this, x => x.IsLightingJsonValid)
            .DisposeWith(_disposables);
    }

    // New method for heavy initialization, called from App.xaml.cs
    public async Task InitializeAsync(VM_SplashScreen? splashReporter)
    {
        const int totalSteps = 3; // Define the total number of steps

        if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
        {
            Thread.Sleep(1000);
        }

        if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
        {
            return;
        }

        try
        {
            // --- STEP 1 ---
            splashReporter?.UpdateStep($"Step 1 of {totalSteps}: Populating mod list...");

            using (ContextualPerformanceTracer.Trace("VM_Settings.PopulateModSettings"))
            {
                // **NEW ORDER: Populate mods first**
                splashReporter?.UpdateProgress(70, "Populating mod list...");
                await _lazyModListVM.Value
                    .PopulateModSettingsAsync(splashReporter); // Base 70%, span 10% (e.g. 70-80)
            }

            // --- STEP 2 ---
            splashReporter?.UpdateStep($"Step 2 of {totalSteps}: Initializing NPC selection bar...");

            using (ContextualPerformanceTracer.Trace("VM_Settings.InitializeNpcSelectionBar"))
            {
                splashReporter?.UpdateProgress(80, "Initializing NPC selection bar...");
                await _lazyNpcSelectionBar.Value.InitializeAsync(splashReporter);
            }

            // --- STEP 3 ---
            splashReporter?.UpdateStep($"Step 3 of {totalSteps}: Applying default settings...");

            if (!_model.HasBeenLaunched && _environmentStateProvider.LoadOrder != null)
            {
                var defaultExclusions = new HashSet<ModKey>();
                var loadOrderSet = new HashSet<ModKey>(_environmentStateProvider.LoadOrder.Keys);

                // 1. Add official master files (DLC, etc.) from the current game release if they are in the load order
                defaultExclusions.UnionWith(_environmentStateProvider.BaseGamePlugins);

                // 2. Add Creation Club plugins by name
                defaultExclusions.UnionWith(_environmentStateProvider.CreationClubPlugins);

                // 3. Add the Unofficial Patch if it exists in the load order
                var ussepKey = ModKey.FromFileName("unofficial skyrim special edition patch.esp");
                if (loadOrderSet.Contains(ussepKey))
                {
                    defaultExclusions.Add(ussepKey);
                }

                _model.ImportFromLoadOrderExclusions = defaultExclusions;
                _model.HasBeenLaunched = true; // Mark defaults as set
            }
            // END: Added Logic

            this.RaisePropertyChanged(nameof(EnvironmentStatus));
            this.RaisePropertyChanged(nameof(EnvironmentErrorText));
            this.RaisePropertyChanged(nameof(AvailablePluginsForExclusion));

            if (EnvironmentStatus == EnvironmentStateProvider.EnvironmentStatus.Valid &&
                AvailablePluginsForExclusion != null)
            {
                ExclusionSelectorViewModel.LoadFromModel(AvailablePluginsForExclusion,
                    _model.EasyNpcDefaultPluginExclusions,
                    _environmentStateProvider.LoadOrder.ListedOrder.Select(x => x.ModKey).ToList());
                ImportFromLoadOrderExclusionSelectorViewModel.LoadFromModel(AvailablePluginsForExclusion,
                    _model.ImportFromLoadOrderExclusions,
                    _environmentStateProvider.LoadOrder.ListedOrder.Select(x => x.ModKey).ToList());
            }

            RefreshNonAppearanceMods();

            // Add the report generation at the very end
            ContextualPerformanceTracer.GenerateDetailedReport("Initial Validation and Load");
        }
        catch (Exception e)
        {
            splashReporter?.ShowMessagesOnClose("An error occured during initialization: " + Environment.NewLine +
                                                Environment.NewLine + ExceptionLogger.GetExceptionStack(e));
        }
    }

    public static Settings LoadSettings()
    {
        string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
        Settings? loadedSettings = null;
        if (File.Exists(settingsPath))
        {
            loadedSettings = JSONhandler<Settings>.LoadJSONFile(settingsPath, out bool success, out string exception);
            if (!success || loadedSettings == null) // Add the null check here
            {
                if (success && loadedSettings == null) // Optional: more specific error message
                {
                    exception = "Settings file was empty or invalid.";
                }

                ScrollableMessageBox.ShowWarning(
                    $"Error loading settings from {settingsPath}:\n{exception}\n\nDefault settings will be used.",
                    "Settings Load Error");
                loadedSettings = new Settings(); // Use defaults on error
            }
        }
        else
        {
            loadedSettings = new Settings(); // Use defaults if file doesn't exist
        }

        // Ensure defaults for new/potentially missing fields after loading old file
        loadedSettings.OutputPluginName ??= "NPC.esp";
        loadedSettings.OutputDirectory ??= default;
        // AppendTimestampToOutputDirectory default is false (bool default)
        // PatchingMode default is Default (enum default)
        loadedSettings.EasyNpcDefaultPluginExclusions ??= new() { ModKey.FromFileName("Synthesis.esp") };
        loadedSettings.ImportFromLoadOrderExclusions ??= new();
        loadedSettings.BatFilePreCommands ??= string.Empty;
        loadedSettings.BatFilePostCommands ??= string.Empty;

        return loadedSettings;
    }

    public void RequestThrottledSave()
    {
        _saveRequestSubject.OnNext(Unit.Default);
    }

    public void SaveSettings()
    {
        if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
        {
            return;
        }

        _model.EasyNpcDefaultPluginExclusions = new HashSet<ModKey>(ExclusionSelectorViewModel.SaveToModel());
        _model.ImportFromLoadOrderExclusions =
            new HashSet<ModKey>(ImportFromLoadOrderExclusionSelectorViewModel.SaveToModel());
        _lazyModListVM.Value.SaveModSettingsToModel();

        string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
        _model.ProgramVersion = App.ProgramVersion;
        JSONhandler<Settings>.SaveJSONFile(_model, settingsPath, out bool success, out string exception);
        if (!success)
        {
            // Maybe show a non-modal warning or log? Saving happens on exit.
            System.Diagnostics.Debug.WriteLine($"ERROR saving settings: {exception}");
        }
    }

    // --- Folder Selection Methods ---
    private async Task SelectGameFolderAsync() // Renamed for consistency, though it wasn't async before
    {
        var dialog = new CommonOpenFileDialog
        {
            IsFolderPicker = true,
            Title = "Select Skyrim Game Folder",
            InitialDirectory = GetSafeInitialDirectory(SkyrimGamePath)
        };

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            SkyrimGamePath = dialog.FileName;
            // SkyrimGamePath change will trigger InitializeAsyncInternal via its WhenAnyValue subscription
        }
    }

    private async Task SelectModsFolderAsync()
    {
        var dialog = new CommonOpenFileDialog
        {
            IsFolderPicker = true,
            Title = "Select Mods Folder (e.g., MO2 Mods Path)",
            InitialDirectory = GetSafeInitialDirectory(ModsFolder)
        };

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;

        ModsFolder = dialog.FileName;

        VM_SplashScreen? splashScreen = null;
        try
        {
            // clear existing ModSettings to avoid cross-contamination
            _model.ModSettings.Clear();
            _model.CachedNonAppearanceMods.Clear();
            //

            _lazyMainWindowVm.Value.IsLoadingFolders = true;
            var footerMsg = "First time analyzing mods folder. Subsequent runs will be faster.";
            splashScreen = VM_SplashScreen.InitializeAndShow(App.ProgramVersion, footerMessage: footerMsg);

            // *** FIX: Removed the incorrect Task.Run wrapper. ***
            // The called methods are already async and handle their own threading internally.
            if (_lazyModListVM.IsValueCreated)
            {
                await _lazyModListVM.Value.PopulateModSettingsAsync(splashScreen);
            }

            if (_lazyNpcSelectionBar.IsValueCreated)
            {
                await _lazyNpcSelectionBar.Value.InitializeAsync(splashScreen);
            }

            if (_lazyModListVM.IsValueCreated)
            {
                _lazyModListVM.Value.ApplyFilters();
            }

            RefreshNonAppearanceMods();
        }
        finally
        {
            _lazyMainWindowVm.Value.IsLoadingFolders = false;
            // Ensure the splash screen is closed and the main window is re-enabled
            if (splashScreen != null)
            {
                await splashScreen.CloseSplashScreenAsync();
            }
        }
    }

    private async Task SelectMugshotsFolderAsync()
    {
        var dialog = new CommonOpenFileDialog
        {
            IsFolderPicker = true,
            Title = "Select Mugshots Folder",
            InitialDirectory = GetSafeInitialDirectory(MugshotsFolder)
        };

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;

        MugshotsFolder = dialog.FileName;

        VM_SplashScreen? splashScreen = null;
        try
        {
            _lazyMainWindowVm.Value.IsLoadingFolders = true;
            splashScreen = VM_SplashScreen.InitializeAndShow(App.ProgramVersion, isModal: false);

            // *** FIX: Removed the incorrect Task.Run wrapper. ***
            if (_lazyNpcSelectionBar.IsValueCreated)
            {
                await _lazyNpcSelectionBar.Value.InitializeAsync(splashScreen);
            }

            if (_lazyModListVM.IsValueCreated)
            {
                await _lazyModListVM.Value.PopulateModSettingsAsync(splashScreen);
            }

            if (_lazyModListVM.IsValueCreated)
            {
                _lazyModListVM.Value.ApplyFilters();
            }
        }
        finally
        {
            _lazyMainWindowVm.Value.IsLoadingFolders = false;
            // Ensure the splash screen is closed and the main window is re-enabled
            if (splashScreen != null)
            {
                await splashScreen.CloseSplashScreenAsync();
            }
        }
    }

    private async Task SelectOutputDirectoryAsync() // Renamed for consistency
    {
        var dialog = new CommonOpenFileDialog
        {
            IsFolderPicker = true,
            Title = "Select Output Directory",
            InitialDirectory = GetSafeInitialDirectory(OutputDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
        };

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            OutputDirectory = dialog.FileName;
        }
    }

    // Helper to get a valid initial directory for folder pickers
    private string GetSafeInitialDirectory(string preferredPath, string fallbackPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && Directory.Exists(preferredPath))
        {
            return preferredPath;
        }

        if (!string.IsNullOrWhiteSpace(fallbackPath) && Directory.Exists(fallbackPath))
        {
            return fallbackPath;
        }

        // Final fallback if neither preferred nor specific fallback exists
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
    
    private void OpenModLinkerWindow()
    {
        var linkerViewModel = new VM_ModFaceFinderLinker(_model, new FaceFinderClient(_model, _eventLogger), _lazyModListVM.Value);
        var linkerView = new ModFaceFinderLinkerWindow
        {
            DataContext = linkerViewModel
        };
        linkerView.ShowDialog();
    }

    private async Task DeleteCachedFaceFinderImagesAsync()
    {
        if (!_lazyModListVM.IsValueCreated)
        {
            ScrollableMessageBox.ShowWarning("Mod list has not been initialized yet.", "Cannot Delete Mugshots");
            return;
        }

        var modSettings = _lazyModListVM.Value.AllModSettings.ToList();
        if (!modSettings.Any())
        {
            ScrollableMessageBox.Show("No mod settings found to process.", "No Mugshots");
            return;
        }

        // Create progress window
        var progressVM = new VM_ProgressWindow
        {
            Title = "Scanning Mugshots",
            StatusMessage = SelectedMugshotSearchModeFF == MugshotSearchMode.Fast 
                ? "Checking cached FaceFinder images..." 
                : "Counting cached FaceFinder images...",
            IsIndeterminate = SelectedMugshotSearchModeFF == MugshotSearchMode.Fast
        };

        var progressWindow = new ProgressWindow
        {
            ViewModel = progressVM
        };

        // Try to set the owner, but don't fail if we can't
        try
        {
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow != null && mainWindow != progressWindow)
            {
                progressWindow.Owner = mainWindow;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not set progress window owner: {ex.Message}");
        }

        progressWindow.Show();

        try
        {
            // Declare cachedFiles outside the if/else to ensure it's always initialized
            List<string> cachedFiles = new List<string>();
            int cachedCount = 0;

            if (SelectedMugshotSearchModeFF == MugshotSearchMode.Fast)
            {
                // FAST MODE: Use cached list
                await Task.Run(() =>
                {
                    // Filter cached paths to only existing files
                    cachedFiles = _model.CachedFaceFinderPaths
                        .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        .ToList();

                    // Remove non-existent files from cache
                    var nonExistentFiles = _model.CachedFaceFinderPaths
                        .Where(path => !File.Exists(path))
                        .ToList();

                    if (nonExistentFiles.Any())
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (var file in nonExistentFiles)
                            {
                                _model.CachedFaceFinderPaths.Remove(file);
                            }
                            Debug.WriteLine($"Removed {nonExistentFiles.Count} non-existent FaceFinder files from cache.");
                        });
                    }

                    cachedCount = cachedFiles.Count;
                });
            }
            else
            {
                // COMPREHENSIVE MODE: Scan all folders
                var allMugshotFolders = new List<(VM_ModSetting modSetting, string folder)>();

                // Collect all folders
                foreach (var modSetting in modSettings)
                {
                    if (progressVM.IsCancellationRequested)
                    {
                        progressWindow.Close();
                        return;
                    }

                    foreach (var mugshotFolder in modSetting.MugShotFolderPaths)
                    {
                        if (string.IsNullOrWhiteSpace(mugshotFolder) || !Directory.Exists(mugshotFolder))
                            continue;

                        allMugshotFolders.Add((modSetting, mugshotFolder));
                    }
                }

                if (!allMugshotFolders.Any())
                {
                    progressWindow.Close();
                    ScrollableMessageBox.Show("No mugshot folders found to process.", "No Mugshots");
                    return;
                }

                // Scan for FaceFinder cached files
                progressVM.IsIndeterminate = false;
                progressVM.ProgressMaximum = allMugshotFolders.Count;
                progressVM.StatusMessage = "Scanning for cached FaceFinder images...";

                int foldersScanned = 0;

                await Task.Run(() =>
                {
                    foreach (var (modSetting, mugshotFolder) in allMugshotFolders)
                    {
                        if (progressVM.IsCancellationRequested)
                            return;

                        try
                        {
                            var imageFiles = Directory.EnumerateFiles(mugshotFolder, "*.*", SearchOption.AllDirectories)
                                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                           f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                           f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

                            foreach (var imageFile in imageFiles)
                            {
                                if (progressVM.IsCancellationRequested)
                                    return;

                                // Check if this is a FaceFinder cached image
                                var metadataPath = imageFile + FaceFinderClient.MetadataFileExtension;
                                if (File.Exists(metadataPath))
                                {
                                    try
                                    {
                                        var metadataJson = File.ReadAllText(metadataPath);
                                        var metadata = JsonConvert.DeserializeObject<FaceFinderClient.FaceFinderMetadata>(metadataJson);
                                        
                                        if (metadata?.Source == "FaceFinder")
                                        {
                                            cachedFiles.Add(imageFile);
                                            cachedCount++;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"Error reading metadata for {imageFile}: {ex.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error scanning folder '{mugshotFolder}': {ex.Message}");
                        }

                        foldersScanned++;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            progressVM.UpdateProgress(foldersScanned,
                                $"Scanning folders... Found {cachedCount} cached FaceFinder images");
                        });
                    }
                });
            }

            if (progressVM.IsCancellationRequested)
            {
                progressWindow.Close();
                return;
            }

            if (cachedCount == 0 || !cachedFiles.Any())
            {
                progressWindow.Close();
                ScrollableMessageBox.Show("No cached FaceFinder images found to delete.", "Nothing to Delete");
                return;
            }

            // Close progress window temporarily for confirmation dialog
            progressWindow.Close();

            // Group files by their containing folder for display
            var filesByFolder = cachedFiles
                .GroupBy(file => Path.GetDirectoryName(file) ?? string.Empty)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.OrderBy(f => Path.GetFileName(f)).ToList());

            // Build the detailed message
            var messageBuilder = new StringBuilder();
            messageBuilder.AppendLine($"Found {cachedFiles.Count} cached FaceFinder image(s) in {filesByFolder.Count} folder(s).");
            messageBuilder.AppendLine();
            messageBuilder.AppendLine("This will permanently delete all cached FaceFinder images and their metadata.");
            messageBuilder.AppendLine("Empty folders will also be removed.");
            messageBuilder.AppendLine();
            messageBuilder.AppendLine("Files to be deleted:");
            messageBuilder.AppendLine("═══════════════════════════════════════════════════════════");
            messageBuilder.AppendLine();

            foreach (var folderGroup in filesByFolder)
            {
                messageBuilder.AppendLine($"📁 {folderGroup.Key}");
                foreach (var file in folderGroup.Value)
                {
                    messageBuilder.AppendLine($"   • {Path.GetFileName(file)}");
                }
                messageBuilder.AppendLine(); // Empty line between folders
            }

            messageBuilder.AppendLine("═══════════════════════════════════════════════════════════");
            messageBuilder.AppendLine();
            messageBuilder.AppendLine("Are you sure you want to continue?");

            // Confirm deletion with scrollable list
            if (!ScrollableMessageBox.Confirm(messageBuilder.ToString(), "Confirm Deletion"))
            {
                return;
            }

            // Reopen progress window for deletion
            progressVM = new VM_ProgressWindow
            {
                Title = "Deleting Images",
                StatusMessage = "Deleting cached FaceFinder images...",
                ProgressMaximum = cachedFiles.Count,
                IsIndeterminate = false
            };

            progressWindow = new ProgressWindow
            {
                ViewModel = progressVM
            };

            // Try to set the owner, but don't fail if we can't
            try
            {
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow != null && mainWindow != progressWindow)
                {
                    progressWindow.Owner = mainWindow;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not set progress window owner: {ex.Message}");
            }

            progressWindow.Show();

            // Perform deletion
            int deletedCount = 0;
            var emptyFoldersToRemove = new Dictionary<VM_ModSetting, List<string>>();

            await Task.Run(() =>
            {
                foreach (var imageFile in cachedFiles)
                {
                    if (progressVM.IsCancellationRequested)
                        return;

                    try
                    {
                        // Find which mod setting this file belongs to
                        var mugshotFolder = modSettings
                            .SelectMany(ms => ms.MugShotFolderPaths.Select(folder => (ms, folder)))
                            .FirstOrDefault(x => imageFile.StartsWith(x.folder, StringComparison.OrdinalIgnoreCase))
                            .ms;

                        // Delete the image file
                        File.Delete(imageFile);
                        deletedCount++;

                        // Delete the metadata file
                        var metadataPath = imageFile + FaceFinderClient.MetadataFileExtension;
                        if (File.Exists(metadataPath))
                        {
                            File.Delete(metadataPath);
                        }

                        // Remove from cache
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _model.CachedFaceFinderPaths.Remove(imageFile);
                        });

                        // Clean up empty parent directories
                        if (mugshotFolder != null)
                        {
                            var correspondingFolder = mugshotFolder.MugShotFolderPaths
                                .FirstOrDefault(f => imageFile.StartsWith(f, StringComparison.OrdinalIgnoreCase));

                            if (correspondingFolder != null)
                            {
                                var parentDir = Path.GetDirectoryName(imageFile);
                                while (parentDir != null &&
                                       !parentDir.Equals(correspondingFolder, StringComparison.OrdinalIgnoreCase) &&
                                       Directory.Exists(parentDir) &&
                                       !Directory.EnumerateFileSystemEntries(parentDir).Any())
                                {
                                    try
                                    {
                                        Directory.Delete(parentDir);
                                        Debug.WriteLine($"Deleted empty parent folder: {parentDir}");
                                        parentDir = Path.GetDirectoryName(parentDir);
                                    }
                                    catch (Exception deleteEx)
                                    {
                                        Debug.WriteLine($"Failed to delete empty parent folder '{parentDir}': {deleteEx.Message}");
                                        break;
                                    }
                                }

                                // Check if the root mugshot folder is now empty
                                if (Directory.Exists(correspondingFolder) &&
                                    !Directory.EnumerateFileSystemEntries(correspondingFolder).Any())
                                {
                                    try
                                    {
                                        Directory.Delete(correspondingFolder, true);
                                        Debug.WriteLine($"Deleted empty mugshot folder: {correspondingFolder}");

                                        if (!emptyFoldersToRemove.ContainsKey(mugshotFolder))
                                        {
                                            emptyFoldersToRemove[mugshotFolder] = new List<string>();
                                        }
                                        emptyFoldersToRemove[mugshotFolder].Add(correspondingFolder);
                                    }
                                    catch (Exception deleteDirEx)
                                    {
                                        Debug.WriteLine($"Failed to delete empty folder '{correspondingFolder}': {deleteDirEx.Message}");
                                    }
                                }
                            }
                        }

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            progressVM.UpdateProgress(deletedCount,
                                $"Deleted {deletedCount} of {cachedFiles.Count} images...");
                        });
                    }
                    catch (Exception deleteEx)
                    {
                        Debug.WriteLine($"Failed to delete file '{imageFile}': {deleteEx.Message}");
                        // Remove from cache even if delete failed (file doesn't exist)
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _model.CachedFaceFinderPaths.Remove(imageFile);
                        });
                    }
                }
            });

            progressWindow.Close();

            if (progressVM.IsCancellationRequested)
            {
                ScrollableMessageBox.Show($"Operation cancelled. Deleted {deletedCount} of {cachedFiles.Count} cached FaceFinder images.",
                    "Deletion Cancelled");
            }
            else
            {
                // Update mod settings on the UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var kvp in emptyFoldersToRemove)
                    {
                        var modSetting = kvp.Key;
                        var foldersToRemove = kvp.Value;

                        foreach (var folder in foldersToRemove)
                        {
                            if (modSetting.MugShotFolderPaths.Contains(folder))
                            {
                                modSetting.MugShotFolderPaths.Remove(folder);
                                Debug.WriteLine($"Removed folder '{folder}' from mod setting '{modSetting.DisplayName}'");
                            }
                        }

                        // Recalculate mugshot validity for the mod
                        _lazyModListVM.Value?.RecalculateMugshotValidity(modSetting);
                    }

                    // Refresh the NPC view if it's loaded
                    if (_lazyNpcSelectionBar.IsValueCreated)
                    {
                        _lazyNpcSelectionBar.Value.RefreshCurrentNpcAppearanceSources();
                    }
                });

                ScrollableMessageBox.Show($"Successfully deleted {deletedCount} cached FaceFinder image(s).",
                    "Deletion Complete");
            }
        }
        catch (Exception ex)
        {
            progressWindow?.Close();
            ScrollableMessageBox.ShowError($"An error occurred: {ExceptionLogger.GetExceptionStack(ex)}");
        }
    }

    // --- Placeholder Command Handlers ---
    private void ShowPatchingModeHelp() // New
    {
        string helpText = @"Patching Mode Help:

Create:
NPC Plugins are imported directly from their selected Appearance Mods.
When the output plugin is generated, place it as high as it will go in your load order, and then use Synthesis FaceFixer (or manually perform conflict resolution) to forward the faces to your final load order.

Create And Patch:
NPC plugins are imported from their conflict-winning override in your load order, and their appearance is modified to match their selected Appearance Mod.
When the ouptut plugin is generated, put it at the end of your load order.";

        ScrollableMessageBox.Show(helpText, "Patching Mode Information");
    }

    private void ShowOverrideHandlingModeHelp()
    {
        string helpText = @"Override Handling Mode:

This setting determines how the default behavior for how the patcher handles mods that override/modify non-appearance records from the base game or other mods.

IMPORANT: Handling overrides makes patching take longer, and is only needed in very rare cases. You almost certainly should leave the default behavior as Ignore.

Options:

- Ignore:
  - The overriden records will be left as-is. If there are conflicts, it'll be up to you to patch them yourself.
  - The output plugin will be mastered to the mods providing the records being overridden.

- Include:
  - The overriden records will be copied into the output plugin.
  - In Create And Patch mode, only the changed aspects of the record will be applied.
  - The output plugin will still be mastered to the mods providing the records being overridden.

- Include As New:
  - The overridden records will be added to the output plugin as new records.
  - This mode may be appropriate if you have two Appearance Mods overriding the same record in different ways, which would make them incompatible with each other otherwise.";
        ScrollableMessageBox.Show(helpText, "Override Handling Mode Information");
    }

// --- NEW: Method to launch the color picker dialog ---
    private void SelectBackgroundColor()
    {
        // Note: This requires adding a reference to System.Windows.Forms.dll in your project
        var dialog = new System.Windows.Forms.ColorDialog();
        
        // Set initial color from the view model
        var initialColor = MugshotBackgroundColor.Color;
        dialog.Color = System.Drawing.Color.FromArgb(initialColor.A, initialColor.R, initialColor.G, initialColor.B);

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var newColor = dialog.Color;
            MugshotBackgroundColor = new SolidColorBrush(Color.FromArgb(newColor.A, newColor.R, newColor.G, newColor.B));
        }
    }

    // --- NEW: Helper to validate JSON string ---
    private bool IsValidJson(string? str)
    {
        if (string.IsNullOrWhiteSpace(str)) return false;
        try
        {
            JsonConvert.DeserializeObject(str);
            return true;
        }
        catch (JsonReaderException)
        {
            return false;
        }
    }
    
    private async Task DeleteAutoGeneratedMugshotsAsync()
    {
        if (!_lazyModListVM.IsValueCreated)
        {
            ScrollableMessageBox.ShowWarning("Mod list has not been initialized yet.", "Cannot Delete Mugshots");
            return;
        }

        var modSettings = _lazyModListVM.Value.AllModSettings.ToList();
        if (!modSettings.Any())
        {
            ScrollableMessageBox.Show("No mod settings found to process.", "No Mugshots");
            return;
        }

        // Create progress window
        var progressVM = new VM_ProgressWindow
        {
            Title = "Scanning Mugshots",
            StatusMessage = SelectedMugshotSearchModePC == MugshotSearchMode.Fast 
                ? "Checking cached generated mugshots..." 
                : "Counting auto-generated mugshots...",
            IsIndeterminate = SelectedMugshotSearchModePC == MugshotSearchMode.Fast
        };

        var progressWindow = new ProgressWindow
        {
            ViewModel = progressVM
        };

        // Try to set the owner, but don't fail if we can't
        try
        {
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow != null && mainWindow != progressWindow)
            {
                progressWindow.Owner = mainWindow;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not set progress window owner: {ex.Message}");
        }

        progressWindow.Show();

        try
        {
            // Declare autoGenFiles outside the if/else to ensure it's always initialized
            List<string> autoGenFiles = new List<string>();
            int autoGenCount = 0;

            if (SelectedMugshotSearchModePC == MugshotSearchMode.Fast)
            {
                // FAST MODE: Use cached list
                await Task.Run(() =>
                {
                    // Filter cached paths to only existing files
                    autoGenFiles = _model.GeneratedMugshotPaths
                        .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        .ToList();

                    // Remove non-existent files from cache
                    var nonExistentFiles = _model.GeneratedMugshotPaths
                        .Where(path => !File.Exists(path))
                        .ToList();

                    if (nonExistentFiles.Any())
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (var file in nonExistentFiles)
                            {
                                _model.GeneratedMugshotPaths.Remove(file);
                            }
                            Debug.WriteLine($"Removed {nonExistentFiles.Count} non-existent files from cache.");
                        });
                    }

                    autoGenCount = autoGenFiles.Count;
                });
            }
            else
            {
                // COMPREHENSIVE MODE: Scan all folders
                var allMugshotFolders = new List<(VM_ModSetting modSetting, string folder)>();

                // Collect all folders
                foreach (var modSetting in modSettings)
                {
                    if (progressVM.IsCancellationRequested)
                    {
                        progressWindow.Close();
                        return;
                    }

                    foreach (var mugshotFolder in modSetting.MugShotFolderPaths)
                    {
                        if (string.IsNullOrWhiteSpace(mugshotFolder) || !Directory.Exists(mugshotFolder))
                            continue;

                        allMugshotFolders.Add((modSetting, mugshotFolder));
                    }
                }

                if (!allMugshotFolders.Any())
                {
                    progressWindow.Close();
                    ScrollableMessageBox.Show("No mugshot folders found to process.", "No Mugshots");
                    return;
                }

                // Scan for auto-generated files
                progressVM.IsIndeterminate = false;
                progressVM.ProgressMaximum = allMugshotFolders.Count;
                progressVM.StatusMessage = "Scanning for auto-generated mugshots...";

                int foldersScanned = 0;

                await Task.Run(() =>
                {
                    foreach (var (modSetting, mugshotFolder) in allMugshotFolders)
                    {
                        if (progressVM.IsCancellationRequested)
                            return;

                        try
                        {
                            var pngFiles = Directory.EnumerateFiles(mugshotFolder, "*.png", SearchOption.AllDirectories);
                            foreach (var pngFile in pngFiles)
                            {
                                if (progressVM.IsCancellationRequested)
                                    return;

                                if (_portraitCreator.IsAutoGenerated(pngFile))
                                {
                                    autoGenFiles.Add(pngFile);
                                    autoGenCount++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error scanning folder '{mugshotFolder}': {ex.Message}");
                        }

                        foldersScanned++;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            progressVM.UpdateProgress(foldersScanned,
                                $"Scanning folders... Found {autoGenCount} auto-generated mugshots");
                        });
                    }
                });
            }

            if (progressVM.IsCancellationRequested)
            {
                progressWindow.Close();
                return;
            }

            if (autoGenCount == 0 || !autoGenFiles.Any())
            {
                progressWindow.Close();
                ScrollableMessageBox.Show("No auto-generated mugshots found to delete.", "Nothing to Delete");
                return;
            }

            // Close progress window temporarily for confirmation dialog
            progressWindow.Close();

            // Group files by their containing folder for display
            var filesByFolder = autoGenFiles
                .GroupBy(file => Path.GetDirectoryName(file) ?? string.Empty)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.OrderBy(f => Path.GetFileName(f)).ToList());

            // Build the detailed message
            var messageBuilder = new StringBuilder();
            messageBuilder.AppendLine($"Found {autoGenFiles.Count} auto-generated mugshot(s) in {filesByFolder.Count} folder(s).");
            messageBuilder.AppendLine();
            messageBuilder.AppendLine("This will permanently delete all auto-generated portrait images.");
            messageBuilder.AppendLine("Empty folders will also be removed.");
            messageBuilder.AppendLine();
            messageBuilder.AppendLine("Files to be deleted:");
            messageBuilder.AppendLine("═══════════════════════════════════════════════════════════");
            messageBuilder.AppendLine();

            foreach (var folderGroup in filesByFolder)
            {
                messageBuilder.AppendLine($"📁 {folderGroup.Key}");
                foreach (var file in folderGroup.Value)
                {
                    messageBuilder.AppendLine($"   • {Path.GetFileName(file)}");
                }
                messageBuilder.AppendLine(); // Empty line between folders
            }

            messageBuilder.AppendLine("═══════════════════════════════════════════════════════════");
            messageBuilder.AppendLine();
            messageBuilder.AppendLine("Are you sure you want to continue?");

            // Confirm deletion with scrollable list
            if (!ScrollableMessageBox.Confirm(messageBuilder.ToString(), "Confirm Deletion"))
            {
                return;
            }

            // Reopen progress window for deletion
            progressVM = new VM_ProgressWindow
            {
                Title = "Deleting Mugshots",
                StatusMessage = "Deleting auto-generated mugshots...",
                ProgressMaximum = autoGenFiles.Count,
                IsIndeterminate = false
            };

            progressWindow = new ProgressWindow
            {
                ViewModel = progressVM
            };

            // Try to set the owner, but don't fail if we can't
            try
            {
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow != null && mainWindow != progressWindow)
                {
                    progressWindow.Owner = mainWindow;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not set progress window owner: {ex.Message}");
            }

            progressWindow.Show();

            // Perform deletion
            int deletedCount = 0;
            var emptyFoldersToRemove = new Dictionary<VM_ModSetting, List<string>>();

            await Task.Run(() =>
            {
                foreach (var pngFile in autoGenFiles)
                {
                    if (progressVM.IsCancellationRequested)
                        return;

                    try
                    {
                        // Find which mod setting this file belongs to
                        var mugshotFolder = modSettings
                            .SelectMany(ms => ms.MugShotFolderPaths.Select(folder => (ms, folder)))
                            .FirstOrDefault(x => pngFile.StartsWith(x.folder, StringComparison.OrdinalIgnoreCase))
                            .ms;

                        File.Delete(pngFile);
                        deletedCount++;

                        // Remove from cache
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _model.GeneratedMugshotPaths.Remove(pngFile);
                        });

                        // Clean up empty parent directories
                        if (mugshotFolder != null)
                        {
                            var correspondingFolder = mugshotFolder.MugShotFolderPaths
                                .FirstOrDefault(f => pngFile.StartsWith(f, StringComparison.OrdinalIgnoreCase));

                            if (correspondingFolder != null)
                            {
                                var parentDir = Path.GetDirectoryName(pngFile);
                                while (parentDir != null &&
                                       !parentDir.Equals(correspondingFolder, StringComparison.OrdinalIgnoreCase) &&
                                       Directory.Exists(parentDir) &&
                                       !Directory.EnumerateFileSystemEntries(parentDir).Any())
                                {
                                    try
                                    {
                                        Directory.Delete(parentDir);
                                        Debug.WriteLine($"Deleted empty parent folder: {parentDir}");
                                        parentDir = Path.GetDirectoryName(parentDir);
                                    }
                                    catch (Exception deleteEx)
                                    {
                                        Debug.WriteLine($"Failed to delete empty parent folder '{parentDir}': {deleteEx.Message}");
                                        break;
                                    }
                                }

                                // Check if the root mugshot folder is now empty
                                if (Directory.Exists(correspondingFolder) &&
                                    !Directory.EnumerateFileSystemEntries(correspondingFolder).Any())
                                {
                                    try
                                    {
                                        Directory.Delete(correspondingFolder, true);
                                        Debug.WriteLine($"Deleted empty mugshot folder: {correspondingFolder}");

                                        if (!emptyFoldersToRemove.ContainsKey(mugshotFolder))
                                        {
                                            emptyFoldersToRemove[mugshotFolder] = new List<string>();
                                        }
                                        emptyFoldersToRemove[mugshotFolder].Add(correspondingFolder);
                                    }
                                    catch (Exception deleteDirEx)
                                    {
                                        Debug.WriteLine($"Failed to delete empty folder '{correspondingFolder}': {deleteDirEx.Message}");
                                    }
                                }
                            }
                        }

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            progressVM.UpdateProgress(deletedCount,
                                $"Deleted {deletedCount} of {autoGenFiles.Count} mugshots...");
                        });
                    }
                    catch (Exception deleteEx)
                    {
                        Debug.WriteLine($"Failed to delete file '{pngFile}': {deleteEx.Message}");
                        // Remove from cache even if delete failed (file doesn't exist)
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _model.GeneratedMugshotPaths.Remove(pngFile);
                        });
                    }
                }
            });

            progressWindow.Close();

            if (progressVM.IsCancellationRequested)
            {
                ScrollableMessageBox.Show($"Operation cancelled. Deleted {deletedCount} of {autoGenFiles.Count} auto-generated mugshots.",
                    "Deletion Cancelled");
            }
            else
            {
                // Update mod settings on the UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var kvp in emptyFoldersToRemove)
                    {
                        var modSetting = kvp.Key;
                        var foldersToRemove = kvp.Value;

                        foreach (var folder in foldersToRemove)
                        {
                            if (modSetting.MugShotFolderPaths.Contains(folder))
                            {
                                modSetting.MugShotFolderPaths.Remove(folder);
                                Debug.WriteLine($"Removed folder '{folder}' from mod setting '{modSetting.DisplayName}'");
                            }
                        }

                        // Recalculate mugshot validity for the mod
                        _lazyModListVM.Value?.RecalculateMugshotValidity(modSetting);
                    }

                    // Refresh the NPC view if it's loaded
                    if (_lazyNpcSelectionBar.IsValueCreated)
                    {
                        _lazyNpcSelectionBar.Value.RefreshCurrentNpcAppearanceSources();
                    }
                });

                ScrollableMessageBox.Show($"Successfully deleted {deletedCount} auto-generated mugshot(s).",
                    "Deletion Complete");
            }
        }
        catch (Exception ex)
        {
            progressWindow?.Close();
            ScrollableMessageBox.ShowError($"An error occurred: {ExceptionLogger.GetExceptionStack(ex)}");
        }
    }
    
    private void RefreshNonAppearanceMods()
    {
        CachedNonAppearanceMods = new ObservableCollection<KeyValuePair<string, string>>(
            _model.CachedNonAppearanceMods.OrderBy(kvp => Path.GetFileName(kvp.Key) ?? kvp.Key,
                StringComparer.OrdinalIgnoreCase));
        ApplyNonAppearanceFilter();
    }

    /// <summary>
    /// Clears and repopulates the filtered list based on the current filter text.
    /// </summary>
    private void ApplyNonAppearanceFilter()
    {
        if (CachedNonAppearanceMods == null) return;

        FilteredNonAppearanceMods.Clear();

        var filter = NonAppearanceModFilterText;
        var sourceList = CachedNonAppearanceMods;

        IEnumerable<KeyValuePair<string, string>> itemsToDisplay;

        if (string.IsNullOrWhiteSpace(filter))
        {
            itemsToDisplay = sourceList;
        }
        else
        {
            // Filter is applied to the Key (the path)
            itemsToDisplay = sourceList.Where(kvp =>
                (Path.GetFileName(kvp.Key) ?? kvp.Key).Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in itemsToDisplay)
        {
            FilteredNonAppearanceMods.Add(item);
        }
    }

    private async Task RescanCachedModAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ScrollableMessageBox.ShowWarning($"The path '{path}' no longer exists and cannot be scanned.",
                "Path Not Found");
            return;
        }

        var modsVM = _lazyModListVM.Value;

        // Deconstruct the result to get the reason
        var (wasReimported, failureReason) = await modsVM.RescanSingleModFolderAsync(path);

        if (wasReimported)
        {
            _model.CachedNonAppearanceMods.Remove(path);

            var itemToRemove = CachedNonAppearanceMods.FirstOrDefault(kvp => kvp.Key == path);
            if (!itemToRemove.Equals(default(KeyValuePair<string, string>)))
            {
                CachedNonAppearanceMods.Remove(itemToRemove);

                // FIX: Explicitly remove from the filtered list bound to the UI
                FilteredNonAppearanceMods.Remove(itemToRemove);
            }

            ScrollableMessageBox.Show(
                $"Successfully re-imported '{Path.GetFileName(path)}' as an appearance mod. You can now find it in the Mods menu.",
                "Re-Scan Successful");
        }
        else
        {
            // Now includes the specific failure reason (e.g., "No FaceGen files found")
            ScrollableMessageBox.Show(
                $"The mod at '{Path.GetFileName(path)}' is still not recognized as an appearance mod.\nReason: {failureReason}",
                "Re-Scan Failed");
        }
    }

    public string GetStatusReport()
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine("Mods Folder: " + ModsFolder);
        sb.AppendLine("Mugshots Folder: " + MugshotsFolder);
        sb.AppendLine("Patching Mode: " + SelectedPatchingMode);
        sb.AppendLine("Output Directory: " + OutputDirectory);
        sb.AppendLine("Output Name: " + TargetPluginName);
        sb.AppendLine("Default Override Handling: " + SelectedRecordOverrideHandlingMode);
        sb.AppendLine("SkyPatcher Mode: " + UseSkyPatcherMode);
        sb.AppendLine("AutoEslIfy: " + AutoEslIfy);
        return sb.ToString();
    }

    public void Dispose()
    {
        _disposables.Dispose(); // Dispose all subscriptions
    }
}