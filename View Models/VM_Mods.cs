using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq; // Added for Throttle, ObserveOn
using System.Reactive.Subjects; // Added for Subject
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks; // Added for Task
using System.Windows;
using System.Windows.Media;
using DynamicData;
using Microsoft.Build.Experimental.BuildCheck;
using Mutagen.Bethesda.Archives; // For MessageBox
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat; // For Locator


namespace NPC_Plugin_Chooser_2.View_Models;

public class VM_Mods : ReactiveObject
{
    private readonly Settings _settings;
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly VM_NpcSelectionBar _npcSelectionBar; // To access AllNpcs and navigate
    private readonly NpcConsistencyProvider _consistencyProvider;
    private readonly Lazy<VM_MainWindow> _lazyMainWindowVm; // *** NEW: To switch tabs ***
    private readonly Lazy<VM_Settings> _lazySettingsVM;
    private readonly Auxilliary _aux;
    private readonly PluginProvider _pluginProvider;
    private readonly BsaHandler _bsaHandler;
    private readonly ImagePacker _imagePacker;
    private readonly ConcurrentDictionary<(string pluginSourcePath, ModKey modKey), bool> _overridesCache = new();
    private CancellationTokenSource? _mugshotLoadingCts;
    private TaskCompletionSource<PackingResult> _packingCompletionSource;
    private IDisposable? _packingCompletedSubscription;

    private readonly CompositeDisposable _disposables = new();

    public const string BaseGameModSettingName = "Base Game";
    public const string CreationClubModsettingName = "Creation Club";

    private readonly ObservableAsPropertyHelper<PatchingMode> _currentPatchingMode;
    public PatchingMode CurrentPatchingMode => _currentPatchingMode.Value;

    // Subject and Observable for scroll requests
    private readonly BehaviorSubject<VM_ModSetting?> _requestScrollToModSubject =
        new BehaviorSubject<VM_ModSetting?>(null);

    public IObservable<VM_ModSetting?> RequestScrollToModObservable => _requestScrollToModSubject.AsObservable();

    // --- Filtering Properties (Left Panel) ---
    [Reactive] public string NameFilterText { get; set; } = string.Empty;
    [Reactive] public string PluginFilterText { get; set; } = string.Empty;
    [Reactive] public ModNpcSearchType SelectedNpcSearchType { get; set; } = ModNpcSearchType.Name;
    [Reactive] public string NpcSearchText { get; set; } = string.Empty;
    public Array AvailableNpcSearchTypes => Enum.GetValues(typeof(ModNpcSearchType));
    [Reactive] public bool IsLoadingNpcData { get; private set; }

    // --- Data Lists (Left Panel) ---
    private List<VM_ModSetting> _allModSettingsInternal = new();
    public IReadOnlyList<VM_ModSetting> AllModSettings => _allModSettingsInternal; // Public access
    public ObservableCollection<VM_ModSetting> ModSettingsList { get; } = new();

    // --- Right Panel Properties ---
    [Reactive] public VM_ModSetting? SelectedModForMugshots { get; private set; }
    public ObservableCollection<VM_ModsMenuMugshot> CurrentModNpcMugshots { get; } = new();

    [Reactive] public bool IsLoadingMugshots { get; private set; }

    // This property will be set to true by the View (ModsView.xaml.cs) when the user
    // directly interacts with zoom (Ctrl+Scroll, +/- buttons).
    [Reactive] public bool ModsViewHasUserManuallyZoomed { get; set; } = false;

    // Subject for triggering right panel image refresh
    private readonly Subject<Unit> _refreshMugshotSizesSubject = new Subject<Unit>();
    public IObservable<Unit> RefreshMugshotSizesObservable => _refreshMugshotSizesSubject.AsObservable();
    
    // Record for Refresh All Mods
    private record ModSettingsBackup(
        List<string> MugShotFolderPaths,
        bool MergeInDependencyRecords,
        bool HasAlteredMergeLogic,
        bool IncludeOutfits,
        bool HandleInjectedRecords,
        bool HasAlteredHandleInjectedRecordsLogic,
        RecordOverrideHandlingMode? OverrideRecordOverrideHandlingMode
    );
    
    // --- Batch Action Controls ---
    [Reactive] public bool ShouldRescanNonAppearanceMods { get; set; } = false;
    public ReactiveCommand<Unit, Unit> RefreshAllModsCommand { get; }
    public ReactiveCommand<Unit, Unit> BatchEnableMergeInCommand { get; }
    public ReactiveCommand<Unit, Unit> BatchForceEnableMergeInCommand { get; }
    public ReactiveCommand<Unit, Unit> BatchDisableMergeInCommand { get; }
    public ReactiveCommand<Unit, Unit> BatchIncludeOutfitsCommand { get; }
    public ReactiveCommand<Unit, Unit> BatchExcludeOutfitsCommand { get; }
    public ReactiveCommand<Unit, Unit> BatchEnableInjectedRecordsCommand { get; }
    public ReactiveCommand<Unit, Unit> BatchDisableInjectedRecordsCommand { get; }
    public ReactiveCommand<Unit, Unit> BatchEnableCopyAssetsCommand { get; }
    public ReactiveCommand<Unit, Unit> BatchDisableCopyAssetsCommand { get; }

    // --- NEW: Zoom Control Properties & Commands for ModsView ---
    [Reactive] public double ModsViewZoomLevel { get; set; }
    [Reactive] public bool ModsViewIsZoomLocked { get; set; }
    public ReactiveCommand<Unit, Unit> ZoomInModsCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutModsCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetZoomModsCommand { get; }
    private const double _minZoomPercentage = 1.0;
    private const double _maxZoomPercentage = 1000.0;
    private const double _zoomStepPercentage = 2.5; // For +/- buttons and scroll wheel

    // --- New: Other Display Controls
    public bool NormalizeImageDimensions => _settings.NormalizeImageDimensions;
    public int MaxMugshotsToFit => _settings.MaxMugshotsToFit;

    // --- NEW: Source Plugin Disambiguation (Right Panel - Above Mugshots, below Mod Name) ---
    [Reactive] public ModKey? SelectedSourcePluginForDisambiguation { get; set; }
    [ObservableAsProperty] public bool ShowSourcePluginControls { get; }
    [ObservableAsProperty] public ObservableCollection<ModKey> AvailableSourcePluginsForSelectedMod { get; }
    public ReactiveCommand<Unit, Unit> SetGlobalSourcePluginCommand { get; }

    // --- Placeholder Image Configuration --- 
    private const string PlaceholderResourceRelativePath = @"Resources\No Mugshot.png";

    public static readonly string FullPlaceholderPath =
        Path.Combine(AppContext.BaseDirectory, PlaceholderResourceRelativePath);

    private static readonly bool PlaceholderExists = File.Exists(FullPlaceholderPath);

    // --- Commands ---
    public ReactiveCommand<VM_ModSetting, Unit> ShowMugshotsCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelMugshotLoadCommand { get; }

    // Expose for binding in VM_ModSetting commands
    public string ModsFolderSetting => _settings.ModsFolder;
    public string MugshotsFolderSetting => _settings.MugshotsFolder; // Needed for BrowseMugshotFolder
    public SkyrimRelease SkyrimRelease => _settings.SkyrimRelease;
    public EnvironmentStateProvider EnvironmentStateProvider => _environmentStateProvider;

    // Concurrency management
    private bool _isPopulatingModSettings = false;
    
    public bool SuppressPopupWarnings => _settings.SuppressPopupWarnings;

    // Factory fields
    private readonly VM_ModSetting.FromModelFactory _modSettingFromModelFactory;
    private readonly VM_ModSetting.FromMugshotPathFactory _modSettingFromMugshotPathFactory;
    private readonly VM_ModSetting.FromModFolderFactory _modSettingFromModFolderFactory;
    private readonly VM_ModsMenuMugshot.Factory _mugshotFactory;
    
    // Helpers
    private static readonly Regex MugshotNameRegex =
        new(@"^(?<hex>[0-9A-F]{8})\.(png|jpg|jpeg|bmp)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // *** Updated Constructor Signature ***
    public VM_Mods(Settings settings, EnvironmentStateProvider environmentStateProvider,
        VM_NpcSelectionBar npcSelectionBar, NpcConsistencyProvider consistencyProvider,
        Lazy<VM_MainWindow> lazyMainWindowVm, Lazy<VM_Settings> lazySettingsVm, Auxilliary aux, 
        PluginProvider pluginProvider, BsaHandler bsaHandler,
        VM_ModSetting.FromModelFactory modSettingFromModelFactory,
        VM_ModSetting.FromMugshotPathFactory modSettingFromMugshotPathFactory,
        VM_ModSetting.FromModFolderFactory modSettingFromModFolderFactory,
        ImagePacker imagePacker, VM_ModsMenuMugshot.Factory mugshotFactory)
    {
        _settings = settings;
        _environmentStateProvider = environmentStateProvider;
        _npcSelectionBar = npcSelectionBar;
        _consistencyProvider = consistencyProvider;
        _lazyMainWindowVm = lazyMainWindowVm;
        _lazySettingsVM = lazySettingsVm;
        _aux = aux;
        _pluginProvider = pluginProvider;
        _bsaHandler = bsaHandler;
        _modSettingFromModelFactory = modSettingFromModelFactory;
        _modSettingFromMugshotPathFactory = modSettingFromMugshotPathFactory;
        _modSettingFromModFolderFactory = modSettingFromModFolderFactory;
        _imagePacker = imagePacker;
        _mugshotFactory = mugshotFactory;
        
        RefreshAllModsCommand = ReactiveCommand.CreateFromTask(() => RefreshAllModSettingsAsync(null)).DisposeWith(_disposables);
        RefreshAllModsCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error refreshing all mods: {ExceptionLogger.GetExceptionStack(ex)}")).DisposeWith(_disposables);

        ShowMugshotsCommand = ReactiveCommand.CreateFromTask<VM_ModSetting>(ShowMugshotsAsync).DisposeWith(_disposables);
        ShowMugshotsCommand.ThrownExceptions.Subscribe(ex =>
        {
            ScrollableMessageBox.ShowError($"Error loading mugshots: {ExceptionLogger.GetExceptionStack(ex)}");
            IsLoadingMugshots = false;
        }).DisposeWith(_disposables);
        
        CancelMugshotLoadCommand = ReactiveCommand.Create(() =>
        {
            _mugshotLoadingCts?.Cancel();
            IsLoadingMugshots = false; // Set UI state immediately for responsiveness
        }).DisposeWith(_disposables);
        CancelMugshotLoadCommand.ThrownExceptions.Subscribe(ex =>
        {
            ScrollableMessageBox.ShowError($"Error cancelling mugshot load: {ExceptionLogger.GetExceptionStack(ex)}");
        }).DisposeWith(_disposables);

        // --- NEW: Initialize Zoom Settings from _settings ---
        ModsViewZoomLevel =
            Math.Max(_minZoomPercentage,
                Math.Min(_maxZoomPercentage, _settings.ModsViewZoomLevel)); // Clamp initial load
        ModsViewIsZoomLocked = _settings.ModsViewIsZoomLocked;
        Debug.WriteLine(
            $"VM_Mods.Constructor: Initial ZoomLevel: {ModsViewZoomLevel:F2}, IsZoomLocked: {ModsViewIsZoomLocked}");

        // --- NEW: Zoom Commands ---
        ZoomInModsCommand = ReactiveCommand.Create(() =>
        {
            Debug.WriteLine("VM_Mods: ZoomInModsCommand executed.");
            ModsViewHasUserManuallyZoomed = true;
            ModsViewZoomLevel = Math.Min(_maxZoomPercentage, ModsViewZoomLevel + _zoomStepPercentage);
        }).DisposeWith(_disposables);
        ZoomOutModsCommand = ReactiveCommand.Create(() =>
        {
            Debug.WriteLine("VM_Mods: ZoomOutModsCommand executed.");
            ModsViewHasUserManuallyZoomed = true;
            ModsViewZoomLevel = Math.Max(_minZoomPercentage, ModsViewZoomLevel - _zoomStepPercentage);
        }).DisposeWith(_disposables);
        ResetZoomModsCommand = ReactiveCommand.Create(() =>
        {
            Debug.WriteLine("VM_Mods: ResetZoomModsCommand executed.");
            ModsViewIsZoomLocked = false;
            ModsViewHasUserManuallyZoomed = false; // This allows packer to take over
            // The key is that the VIEW needs to be told to re-evaluate its layout
            // BEFORE the packer uses the ScrollViewer's dimensions.
            // So, just signaling the subject might not be enough if the view's layout
            // isn't guaranteed to be updated first.
            // This subject will trigger RefreshMugshotImageSizes in the view.
            _refreshMugshotSizesSubject.OnNext(Unit.Default);
        }).DisposeWith(_disposables);
        // ... (exception handlers for commands) ...
        ZoomInModsCommand.ThrownExceptions
            .Subscribe(ex => Debug.WriteLine($"Error ZoomInModsCommand: {ExceptionLogger.GetExceptionStack(ex)}")).DisposeWith(_disposables);
        ZoomOutModsCommand.ThrownExceptions
            .Subscribe(ex => Debug.WriteLine($"Error ZoomOutModsCommand: {ExceptionLogger.GetExceptionStack(ex)}")).DisposeWith(_disposables);
        ResetZoomModsCommand.ThrownExceptions
            .Subscribe(ex => Debug.WriteLine($"Error ResetZoomModsCommand: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        
        Observable.FromEventPattern<ImagePacker.PackingCompletedEventArgs>(
                _imagePacker, nameof(ImagePacker.PackingCompleted))
            .Throttle(TimeSpan.FromMilliseconds(100))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt => {
                // Complete the TaskCompletionSource if we're waiting for it
                if (_packingCompletionSource != null && !_packingCompletionSource.Task.IsCompleted)
                {
                    var result = evt.EventArgs.Result;
                    _packingCompletionSource.SetResult(result);
                }
        
                // Then trigger async generation as before
                this.TriggerAsyncMugshotGeneration();
            })
            .DisposeWith(_disposables);

        // --- Source Plugin Disambiguation Logic ---
        this.WhenAnyValue(x => x.SelectedModForMugshots)
            .Select(mod => mod != null && mod.CorrespondingModKeys.Count > 1 && mod.AmbiguousNpcFormKeys.Any())
            .ToPropertyEx(this, x => x.ShowSourcePluginControls)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.SelectedModForMugshots)
            .Select(mod => mod != null && mod.CorrespondingModKeys.Count > 1
                ? new ObservableCollection<ModKey>(mod.CorrespondingModKeys.OrderBy(mk => mk.FileName.String))
                : new ObservableCollection<ModKey>())
            .ToPropertyEx(this, x => x.AvailableSourcePluginsForSelectedMod)
            .DisposeWith(_disposables);

        // When SelectedModForMugshots changes, reset the SelectedSourcePluginForDisambiguation
        this.WhenAnyValue(x => x.SelectedModForMugshots)
            .Subscribe(_ => SelectedSourcePluginForDisambiguation = null) // Reset dropdown selection
            .DisposeWith(_disposables);

        // Command for setting the global source plugin
        var canSetGlobalSource = this.WhenAnyValue(
            x => x.SelectedModForMugshots,
            x => x.SelectedSourcePluginForDisambiguation,
            (mod, selectedPlugin) => mod != null && selectedPlugin.HasValue && !selectedPlugin.Value.IsNull &&
                                     mod.CorrespondingModKeys.Count > 1);

        SetGlobalSourcePluginCommand = ReactiveCommand.Create(() =>
        {
            if (SelectedModForMugshots != null && SelectedSourcePluginForDisambiguation.HasValue &&
                !SelectedSourcePluginForDisambiguation.Value.IsNull)
            {
                // SetSourcePluginForAllApplicableNpcs now returns a list of changed FormKeys
                List<FormKey> changedKeys =
                    SelectedModForMugshots.SetSourcePluginForAllApplicableNpcs(SelectedSourcePluginForDisambiguation
                        .Value);

                if (changedKeys.Any())
                {
                    // Manually update the CurrentSourcePlugin for displayed mugshots
                    // This ensures their context menu checkmarks are correct without a full panel reload.
                    foreach (var mugshotVM in CurrentModNpcMugshots)
                    {
                        if (changedKeys.Contains(mugshotVM.NpcFormKey))
                        {
                            mugshotVM.CurrentSourcePlugin = SelectedSourcePluginForDisambiguation;
                        }
                    }

                    Debug.WriteLine(
                        $"VM_Mods: Updated CurrentSourcePlugin for {changedKeys.Count} displayed mugshots after global source set.");
                }
            }
        }, canSetGlobalSource).DisposeWith(_disposables); // canSetGlobalSource is the WhenAnyValue observable

        SetGlobalSourcePluginCommand.ThrownExceptions.Subscribe(ex =>
        {
            ScrollableMessageBox.ShowError($"Error setting global source plugin: {ExceptionLogger.GetExceptionStack(ex)}");
        }).DisposeWith(_disposables);
        // --- END: Source Plugin Disambiguation Logic ---

        // --- Setup Filter Reaction ---
        this.WhenAnyValue(x => x.NameFilterText, x => x.PluginFilterText, x => x.NpcSearchText,
                x => x.SelectedNpcSearchType) // Added NpcSearchType
            .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilters())
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.NameFilterText, x => x.PluginFilterText, x => x.NpcSearchText,
                x => x.SelectedNpcSearchType)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (SelectedModForMugshots != null && !ModSettingsList.Contains(SelectedModForMugshots))
                {
                    SelectedModForMugshots = null;
                    CurrentModNpcMugshots.Clear();
                }
            })
            .DisposeWith(_disposables);

        // --- NEW: Persist Zoom Settings and Trigger Refresh ---
        this.WhenAnyValue(x => x.ModsViewZoomLevel)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(100))
            .Subscribe(zoom =>
            {
                bool isFromPackerUpdate = !ModsViewIsZoomLocked && !ModsViewHasUserManuallyZoomed;
                Debug.WriteLine(
                    $"VM_Mods: ModsViewZoomLevel RAW input {zoom:F2}. IsFromPacker: {isFromPackerUpdate}, IsLocked: {ModsViewIsZoomLocked}, ManualZoom: {ModsViewHasUserManuallyZoomed}");

                double previousVmZoomLevel = _settings.ModsViewZoomLevel;
                double newClampedZoom = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, zoom));

                if (Math.Abs(_settings.ModsViewZoomLevel - newClampedZoom) > 0.001)
                {
                    _settings.ModsViewZoomLevel = newClampedZoom;
                    Debug.WriteLine($"VM_Mods: Settings.ModsViewZoomLevel updated to {newClampedZoom:F2}.");
                }

                if (Math.Abs(newClampedZoom - zoom) > 0.001)
                {
                    Debug.WriteLine(
                        $"VM_Mods: ZoomLevel IS being clamped from {zoom:F2} to {newClampedZoom:F2}. Updating property.");
                    ModsViewZoomLevel = newClampedZoom;
                    return;
                }

                if (ModsViewIsZoomLocked || ModsViewHasUserManuallyZoomed)
                {
                    Debug.WriteLine(
                        $"VM_Mods: ZoomLevel processed. IsLocked or ManualZoom. Triggering refresh. Value: {newClampedZoom:F2}");
                    _refreshMugshotSizesSubject.OnNext(Unit.Default);
                }
                else
                {
                    Debug.WriteLine(
                        $"VM_Mods: ZoomLevel processed. Unlocked & not manual. No VM-initiated refresh. Value: {newClampedZoom:F2}");
                }
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.ModsViewIsZoomLocked)
            .Skip(1)
            .Subscribe(isLocked =>
            {
                Debug.WriteLine($"VM_Mods: ModsViewIsZoomLocked changed to {isLocked}.");
                _settings.ModsViewIsZoomLocked = isLocked;
                ModsViewHasUserManuallyZoomed = false;
                Debug.WriteLine("VM_Mods: ModsViewIsZoomLocked changed - Triggering _refreshMugshotSizesSubject.");
                _refreshMugshotSizesSubject.OnNext(Unit.Default);
            })
            .DisposeWith(_disposables);

        // MODIFIED: When SelectedModForMugshots changes, reset manual zoom state if not locked.
        this.WhenAnyValue(x => x.SelectedModForMugshots)
            .Skip(1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(selectedMod =>
            {
                Debug.WriteLine(
                    $"VM_Mods: SelectedModForMugshots changed to {selectedMod?.DisplayName ?? "null"}.");
                if (!ModsViewIsZoomLocked)
                {
                    Debug.WriteLine(
                        "VM_Mods: SelectedModForMugshots changed - Zoom not locked, setting ModsViewHasUserManuallyZoomed = false");
                    ModsViewHasUserManuallyZoomed = false;
                }
                // ShowMugshotsAsync (called when this property changes, typically via command) will trigger _refreshMugshotSizesSubject
            })
            .DisposeWith(_disposables);

        // When SelectedModForMugshots changes (which happens when ShowMugshotsCommand is executed),
        // signal a scroll request for this newly selected mod.
        this.WhenAnyValue(x => x.SelectedModForMugshots)
            .Skip(1) // Skip initial null or first value
            .ObserveOn(RxApp
                .MainThreadScheduler) // Ensure subject is updated on UI thread if needed, though OnNext is thread-safe
            .Subscribe(modToScrollTo =>
            {
                if (modToScrollTo != null)
                {
                    Debug.WriteLine(
                        $"VM_Mods: SelectedModForMugshots changed to {modToScrollTo.DisplayName}. Signaling scroll.");
                    _requestScrollToModSubject.OnNext(modToScrollTo);
                }
                else
                {
                    _requestScrollToModSubject.OnNext(null); // Clear scroll request if selection is cleared
                }

                // Reset manual zoom flag if not locked (existing logic)
                if (!ModsViewIsZoomLocked)
                {
                    ModsViewHasUserManuallyZoomed = false;
                }
            })
            .DisposeWith(_disposables);
        
        this.WhenAnyValue(x => x._lazySettingsVM.Value.SelectedPatchingMode)
            .ToProperty(this, x => x.CurrentPatchingMode, out _currentPatchingMode);
        
        // --- NEW: Initialize Batch Action Commands ---
        BatchIncludeOutfitsCommand = ReactiveCommand.Create(() =>
        {
            const string message = "Modifying NPC outfits on an existing save can lead to NPCs unequipping their outifts entirely. Are you sure you want to enable outfit modification?";

            if (!_settings.SuppressPopupWarnings && !ScrollableMessageBox.Confirm(message, "Confirm Outfit Forwarding"))
            {
                return;
            }
            
            foreach (var modSetting in _allModSettingsInternal)
            {
                modSetting.IsPerformingBatchAction = true;
                modSetting.IncludeOutfits = true;
                modSetting.IsPerformingBatchAction = false;
            }
        }).DisposeWith(_disposables);

        BatchExcludeOutfitsCommand = ReactiveCommand.Create(() =>
        {
            foreach (var modSetting in _allModSettingsInternal)
            {
                modSetting.IsPerformingBatchAction = true;
                modSetting.IncludeOutfits = false;
                modSetting.IsPerformingBatchAction = false;
            }
        }).DisposeWith(_disposables);
        
        BatchEnableInjectedRecordsCommand = ReactiveCommand.Create(() =>
        {
            const string message = "Searching for injected records makes patching take longer, and most appearance mods don't need it. Are you sure you want to enable this for all mods?";

            if (!_settings.SuppressPopupWarnings && !ScrollableMessageBox.Confirm(message, "Confirm Injected Record Search"))
            {
                return;
            }
            
            foreach (var modSetting in _allModSettingsInternal)
            {
                if (modSetting.MergeInDependencyRecords)
                {
                    modSetting.IsPerformingBatchAction = true;
                    modSetting.HandleInjectedRecords = true;
                    modSetting.IsPerformingBatchAction = false;
                }
            }
        }).DisposeWith(_disposables);

        BatchDisableInjectedRecordsCommand = ReactiveCommand.Create(() =>
        {
            foreach (var modSetting in _allModSettingsInternal)
            {
                modSetting.IsPerformingBatchAction = true;
                modSetting.HandleInjectedRecords = false;
                modSetting.IsPerformingBatchAction = false;
            }
        }).DisposeWith(_disposables);
        
        BatchEnableMergeInCommand = ReactiveCommand.Create(() =>
        {
            foreach (var modSetting in _allModSettingsInternal)
            {
                if (!modSetting.HasAlteredMergeLogic)
                {
                    modSetting.MergeInDependencyRecords = true;
                }
            }
        }).DisposeWith(_disposables);

        BatchForceEnableMergeInCommand = ReactiveCommand.Create(() =>
        {
            const string message = "WARNING: Forcing 'Merge Dependencies' ON for all mods is not recommended.\n\n" +
                                   "This feature is intended for mods you plan to disable after patching. Merging in large mods that remain in your load order can cause patcher freezes and is unnecessary.\n\n" +
                                   "Are you sure you want to enable this for all mods, including those automatically flagged as non-appearance mods?";

            if (ScrollableMessageBox.Confirm(message, "Confirm Force Enable Merge-in"))
            {
                foreach (var modSetting in _allModSettingsInternal)
                {
                    modSetting.MergeInDependencyRecords = true;
                }
            }
        }).DisposeWith(_disposables);

        BatchDisableMergeInCommand = ReactiveCommand.Create(() =>
        {
            foreach (var modSetting in _allModSettingsInternal)
            {
                modSetting.MergeInDependencyRecords = false;
            }
        }).DisposeWith(_disposables);
        
        BatchEnableCopyAssetsCommand = ReactiveCommand.Create(() =>
        {
            foreach (var modSetting in _allModSettingsInternal)
            {
                modSetting.IsPerformingBatchAction = true;
                modSetting.CopyAssets = true;
                modSetting.IsPerformingBatchAction = false;
            }
        }).DisposeWith(_disposables);
        
        BatchDisableCopyAssetsCommand = ReactiveCommand.Create(() =>
        {
            const string message =
                "Disabling asset copying for ALL mods means only FaceGen files (.nif/.dds) will be transferred for every NPC.\n\n" +
                "It becomes your responsibility to ensure that all other required assets (meshes, textures for armor, hair, eyes, etc.) are still available, though you can disable or hide the source mod plugins.\n\n" +
                "Are you sure you want to disable asset copying for all mods?";

            if (ScrollableMessageBox.Confirm(message, "Confirm Disable All Asset Copying"))
            {
                foreach (var modSetting in _allModSettingsInternal)
                {
                    modSetting.IsPerformingBatchAction = true;
                    modSetting.CopyAssets = false;
                    modSetting.IsPerformingBatchAction = false;
                }
            }
        }).DisposeWith(_disposables);
        
        BatchIncludeOutfitsCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error including outfits: {ExceptionLogger.GetExceptionStack(ex)}")).DisposeWith(_disposables);
        BatchExcludeOutfitsCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error excluding outfits: {ExceptionLogger.GetExceptionStack(ex)}")).DisposeWith(_disposables);
        BatchEnableInjectedRecordsCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error enabling injected record handling: {ExceptionLogger.GetExceptionStack(ex)}")).DisposeWith(_disposables);
        BatchDisableInjectedRecordsCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error disabling injected record handling: {ExceptionLogger.GetExceptionStack(ex)}")).DisposeWith(_disposables);
        BatchEnableMergeInCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error enabling merge-in: {ExceptionLogger.GetExceptionStack(ex)}")).DisposeWith(_disposables);
        BatchForceEnableMergeInCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error force-enabling merge-in: {ExceptionLogger.GetExceptionStack(ex)}")).DisposeWith(_disposables);
        BatchDisableMergeInCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error disabling merge-in: {ExceptionLogger.GetExceptionStack(ex)}")).DisposeWith(_disposables);
        BatchEnableCopyAssetsCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error enabling asset copying: {ExceptionLogger.GetExceptionStack(ex)}")).DisposeWith(_disposables);
        BatchDisableCopyAssetsCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error disabling asset copying: {ExceptionLogger.GetExceptionStack(ex)}")).DisposeWith(_disposables);
        
        ApplyFilters(); // Apply initial filter
    }
    
    private void TriggerAsyncMugshotGeneration()
    {
        if (CurrentModNpcMugshots == null || !CurrentModNpcMugshots.Any())
        {
            return;
        }

        Debug.WriteLine("Mods Menu packer finished. Triggering background image generation.");
        foreach (var mugshotVM in CurrentModNpcMugshots)
        {
            // If the mugshot is a placeholder (`HasMugshot` is false), start the real image loading.
            if (!mugshotVM.HasMugshot)
            {
                // Fire-and-forget. The VM will update its own image when the task completes.
                _ = mugshotVM.LoadRealImageAsync();
            }
        }
    }

    /// <summary>
    /// Adds a new VM_ModSetting (typically created by Unlink operation) to the internal list
    /// and refreshes dependent UI.
    /// </summary>
    public async Task AddAndRefreshModSettingAsync(VM_ModSetting newVm)
    {
        if (newVm == null || _allModSettingsInternal.Any(vm =>
                vm.DisplayName.Equals(newVm.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            Debug.WriteLine(
                $"VM_Mods: Not adding VM '{newVm?.DisplayName}' either because it's null or a VM with that DisplayName already exists.");
            // Optionally, if it exists, consider merging properties, but for unlink, it should be a new entry.
            return;
        }

        _allModSettingsInternal.Add(newVm);
        // Re-sort the internal list by DisplayName
        SortVMsInPlace();

        // Recalculate its mugshot validity (it might be a new mugshot-only entry)
        RecalculateMugshotValidity(newVm);

        // Refresh the filtered list in the UI
        ApplyFilters();

        // Asynchronously refresh its NPC lists if it might have mod data (though unlink usually makes it mugshot-only)
        // For a new mugshot-only entry, RefreshNpcLists won't find much, but it's harmless.

        var faceGenCache = await CacheFaceGenPathsOnLoadAsync(new[] { newVm }, null);

        var plugins =
            _pluginProvider.LoadPlugins(newVm.CorrespondingModKeys, newVm.CorrespondingFolderPaths.ToHashSet());
        await Task.Run(() => newVm.RefreshNpcLists(faceGenCache.allFaceGenLooseFiles, faceGenCache.allFaceGenBsaFiles, plugins, _settings.LocalizationLanguage));
        _pluginProvider.UnloadPlugins(newVm.CorrespondingModKeys);
    }

    /// <summary>
    /// Sorts an input mod settings list alphabetically (except for base game and CC content)
    /// </summary>
    public List<VM_ModSetting> SortVMs(IEnumerable<VM_ModSetting> inputs)
    {
        var sorted = inputs
            .OrderBy(vm => vm.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        var baseGameSetting = inputs.FirstOrDefault(x => x.DisplayName == BaseGameModSettingName);
        var creationClubSetting =
            inputs.FirstOrDefault(x => x.DisplayName == CreationClubModsettingName);

        if (creationClubSetting != null)
        {
            sorted.Remove(creationClubSetting);
            sorted.Insert(0, creationClubSetting);
        }

        if (baseGameSetting != null)
        {
            sorted.Remove(baseGameSetting);
            sorted.Insert(0, baseGameSetting);
        }

        return sorted;
    }

    /// <summary>
    /// Sorts the main mod settings list alphabetically (except for base game and CC content) in-place
    /// </summary>
    public void SortVMsInPlace()
    {
        var sorted = SortVMs(_allModSettingsInternal);
        _allModSettingsInternal.Clear();
        _allModSettingsInternal.AddRange(sorted);
    }

    /// <summary>
    /// Requests the VM_NpcSelectionBar to refresh its current NPC's appearance sources.
    /// </summary>
    public void RequestNpcSelectionBarRefreshView()
    {
        // This relies on VM_NpcSelectionBar being accessible, e.g., if injected or via a message bus.
        // Assuming _npcSelectionBar is the injected instance.
        _npcSelectionBar?.RefreshCurrentNpcAppearanceSources();
    }
    
    // In VM_Mods.cs

private Task ShowMugshotsAsync(VM_ModSetting selectedModSetting)
{
    _mugshotLoadingCts?.Cancel();
    _mugshotLoadingCts = new CancellationTokenSource();
    var token = _mugshotLoadingCts.Token;

    if (selectedModSetting == null)
    {
        SelectedModForMugshots = null;
        CurrentModNpcMugshots.ForEach(vm => vm.Dispose());
        CurrentModNpcMugshots.Clear();
        return Task.CompletedTask;
    }

    IsLoadingMugshots = true;
    SelectedModForMugshots = selectedModSetting;
    CurrentModNpcMugshots.ForEach(vm => vm.Dispose());
    CurrentModNpcMugshots.Clear();

    if (!ModsViewIsZoomLocked)
    {
        ModsViewHasUserManuallyZoomed = false;
    }

    _ = Task.Run(async () =>
    {
        try
        {
            // --- REVISED Phase 1: Cancellable Data Gathering ---
            var mugshotData = new List<(string ImagePath, FormKey NpcFormKey, string NpcDisplayName)>();

            // 1. Pre-scan and cache all existing mugshot file paths for this mod into a lookup dictionary.
            var existingMugshots = new Dictionary<FormKey, string>();
            var validFolders = (selectedModSetting.MugShotFolderPaths ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToList();

            if (validFolders.Any())
            {
                var imageFiles = validFolders
                    .SelectMany(folder => Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                    .Where(f => MugshotNameRegex.IsMatch(Path.GetFileName(f)));

                foreach (var imagePath in imageFiles)
                {
                    if (token.IsCancellationRequested) break;
                    var fileName = Path.GetFileName(imagePath);
                    var match = MugshotNameRegex.Match(fileName);
                    if (!match.Success) continue;

                    var hexPart = match.Groups["hex"].Value;
                    var pluginName = new DirectoryInfo(Path.GetDirectoryName(imagePath)!).Name;
                    var tail6 = hexPart.Length >= 6 ? hexPart[^6..] : hexPart;
                    var formKeyString = $"{tail6}:{pluginName}";

                    if (FormKey.TryFactory(formKeyString, out var npcFormKey) && !existingMugshots.ContainsKey(npcFormKey))
                    {
                        existingMugshots[npcFormKey] = imagePath;
                    }
                }
            }
            if (token.IsCancellationRequested) return;

            // 2. Iterate through ALL NPCs that belong to this mod.
            foreach (var (npcFormKey, npcDisplayName) in selectedModSetting.NpcFormKeysToDisplayName)
            {
                if (token.IsCancellationRequested) break;

                // 3. For each NPC, use its real image path if it exists in our lookup, otherwise use the placeholder.
                //    This ensures every NPC gets an entry.
                if (existingMugshots.TryGetValue(npcFormKey, out var imagePath))
                {
                    mugshotData.Add((imagePath, npcFormKey, npcDisplayName));
                }
                else
                {
                    mugshotData.Add((FullPlaceholderPath, npcFormKey, npcDisplayName));
                }
            }

            if (token.IsCancellationRequested || !mugshotData.Any())
            {
                 await Application.Current.Dispatcher.InvokeAsync(() => IsLoadingMugshots = false, System.Windows.Threading.DispatcherPriority.Normal, token);
                 return;
            }

            // --- Phase 2: UI Population with Correct Sizing ---
            int maxToFit = _settings.MaxMugshotsToFit;

            // Sort all data once by name before processing
            var sortedMugshotData = mugshotData.OrderBy(d => d.NpcDisplayName).ToList();

            if (sortedMugshotData.Count <= maxToFit)
            {
                // ALGORITHM 1: Small Mod - Load all at once, then resize.
                var vms = sortedMugshotData
                    .Select(data => CreateMugshotVmFromData(selectedModSetting, data.ImagePath, data.NpcFormKey, data.NpcDisplayName, token))
                    .ToList();

                await Application.Current.Dispatcher.InvokeAsync(() => {
                    if (token.IsCancellationRequested) return;
                    foreach (var vm in vms) CurrentModNpcMugshots.Add(vm);
                    _refreshMugshotSizesSubject.OnNext(Unit.Default); // Resize the entire batch
                }, System.Windows.Threading.DispatcherPriority.Normal, token);
            }
            else
            {
                // ALGORITHM 2: Large Mod - Two-phase loading to prevent layout issues.
                
                // 2a: Sizing Phase
                var firstChunkData = sortedMugshotData.Take(maxToFit).ToList();
                var firstChunkVMs = firstChunkData.Select(data => CreateMugshotVmFromData(selectedModSetting, data.ImagePath, data.NpcFormKey, data.NpcDisplayName, token)).ToList();
                
                _packingCompletionSource = new TaskCompletionSource<PackingResult>();

                // Add the first batch to the UI and trigger the resize calculation
                await Application.Current.Dispatcher.InvokeAsync(() => {
                    if (token.IsCancellationRequested) return;
                    foreach (var vm in firstChunkVMs) CurrentModNpcMugshots.Add(vm);
                    _refreshMugshotSizesSubject.OnNext(Unit.Default);
                }, System.Windows.Threading.DispatcherPriority.Normal, token);

                // Asynchronously wait for the UI to report back with the definitive calculated size
                PackingResult result = await _packingCompletionSource.Task;
                if (token.IsCancellationRequested) return;

                // 2b: Population Phase
                var remainingData = sortedMugshotData.Skip(maxToFit);
                const int batchSize = 100;
                var batchVms = new List<VM_ModsMenuMugshot>(batchSize);

                foreach (var data in remainingData)
                {
                    if (token.IsCancellationRequested) break;

                    var vm = CreateMugshotVmFromData(selectedModSetting, data.ImagePath, data.NpcFormKey, data.NpcDisplayName, token);

                    // CRITICAL: Apply the definitive size BEFORE adding to the UI
                    if (result.DefinitiveWidth > 0 && result.DefinitiveHeight > 0)
                    {
                        vm.ImageWidth = result.DefinitiveWidth;
                        vm.ImageHeight = result.DefinitiveHeight;
                    }
                    
                    batchVms.Add(vm);

                    if (batchVms.Count >= batchSize)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() => {
                            if (!token.IsCancellationRequested) foreach(var item in batchVms) CurrentModNpcMugshots.Add(item);
                        }, System.Windows.Threading.DispatcherPriority.Normal, token);
                        batchVms.Clear();
                        await Task.Yield(); // Allow UI to remain responsive
                    }
                }
                
                // Add the final batch
                if (batchVms.Any() && !token.IsCancellationRequested)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => {
                        foreach(var item in batchVms) CurrentModNpcMugshots.Add(item);
                    }, System.Windows.Threading.DispatcherPriority.Normal, token);
                }
            }
        }
        catch (TaskCanceledException) { /* Suppress cancellation error */ }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => ScrollableMessageBox.ShowWarning($"Failed to load mugshot data for {selectedModSetting.DisplayName}:\n{ExceptionLogger.GetExceptionStack(ex)}", "Mugshot Load Error"));
        }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(() => {
                if (!token.IsCancellationRequested) IsLoadingMugshots = false;
            });
        }
    }, token);

    return Task.CompletedTask;
}

// Helper method used by both algorithms
private VM_ModsMenuMugshot CreateMugshotVmFromData(VM_ModSetting modSetting, string imagePath, FormKey npcFormKey, string npcDisplayName, CancellationToken token)
{
    bool isAmbiguous = modSetting.AmbiguousNpcFormKeys.Contains(npcFormKey);
    var availableModKeys = modSetting.AvailablePluginsForNpcs.TryGetValue(npcFormKey, out var keys) ? keys : new List<ModKey>();
    var currentSource = modSetting.NpcPluginDisambiguation.TryGetValue(npcFormKey, out var source) ? (ModKey?)source : availableModKeys.FirstOrDefault();
    
    var vm = _mugshotFactory(
        imagePath, 
        npcFormKey, 
        npcDisplayName, 
        this, 
        isAmbiguous, 
        availableModKeys, 
        currentSource, 
        modSetting,
        token
    );
    
    return vm;
}

    // --- NEW or MODIFIED IF NEEDED: Dispose method for cleaning up subscriptions ---
    public void Dispose() // If VM_Mods needs to be disposable
    {
        _disposables.Dispose();
        CurrentModNpcMugshots.ForEach(vm => vm.Dispose());
        CurrentModNpcMugshots.Clear();
        _requestScrollToModSubject.Dispose(); // Dispose the subject
    }

    // *** NEW: Method to handle navigation triggered by VM_ModsMenuMugshot ***
    // In VM_Mods.cs
    public void NavigateToNpc(FormKey npcFormKey)
    {
        _lazyMainWindowVm.Value.IsNpcsTabSelected = true;

        // Use a slightly longer initial delay to ensure tab switch UI operations can start
        RxApp.MainThreadScheduler.Schedule(TimeSpan.FromMilliseconds(100), () =>
        {
            var npcToSelect = _npcSelectionBar.AllNpcs.FirstOrDefault(npc => npc.NpcFormKey == npcFormKey);
            if (npcToSelect != null)
            {
                Debug.WriteLine(
                    $"VM_Mods.NavigateToNpc: Found NPC {npcToSelect.DisplayName}. Initiating navigation sequence.");
                _npcSelectionBar.IsProgrammaticNavigationInProgress = true; // Set flag BEFORE clearing filters

                // Clear filters. This will reactively trigger _npcSelectionBar.ApplyFilter.
                // ApplyFilter will see IsProgrammaticNavigationInProgress = true and will NOT auto-select.
                _npcSelectionBar.SearchText1 = "";
                _npcSelectionBar.SearchText2 = "";
                _npcSelectionBar.SearchText3 = "";

                // Schedule the explicit selection and scroll signal to occur *after*
                // the filter clearing has triggered ApplyFilter and ApplyFilter has updated FilteredNpcs.
                // The WhenAnyValue for filters in VM_NpcSelectionBar is throttled by 300ms.
                // So, we schedule this for slightly after that throttle period.
                RxApp.MainThreadScheduler.Schedule(TimeSpan.FromMilliseconds(350), () =>
                {
                    Debug.WriteLine(
                        $"VM_Mods.NavigateToNpc: Attempting to explicitly select {npcToSelect.DisplayName}.");

                    // It's possible FilteredNpcs doesn't contain npcToSelect if ApplyFilter somehow
                    // didn't include it (e.g., if ApplyFilter was triggered by something else very quickly).
                    // A safeguard: if target not in list, ApplyFilter again (though this should be rare with blank filters).
                    if (!_npcSelectionBar.FilteredNpcs.Contains(npcToSelect))
                    {
                        Debug.WriteLine(
                            $"VM_Mods.NavigateToNpc: Target {npcToSelect.DisplayName} not in FilteredNpcs. Re-applying filter.");
                        // This ApplyFilter will also see IsProgrammaticNavigationInProgress = true.
                        _npcSelectionBar.ApplyFilter(false);
                        // Give this ApplyFilter a moment if it was needed.
                        RxApp.MainThreadScheduler.Schedule(TimeSpan.FromMilliseconds(50), SelectAndSignal);
                    }
                    else
                    {
                        SelectAndSignal();
                    }

                    void SelectAndSignal()
                    {
                        _npcSelectionBar.SelectedNpc = npcToSelect; // Explicitly set the selection
                        Debug.WriteLine(
                            $"VM_Mods.NavigateToNpc: _npcSelectionBar.SelectedNpc explicitly set to {npcToSelect.DisplayName}.");

                        // Schedule the scroll signal with a small delay for the selection to bind in the UI
                        RxApp.MainThreadScheduler.Schedule(TimeSpan.FromMilliseconds(5), () =>
                        {
                            if (_npcSelectionBar.SelectedNpc == npcToSelect) // Final check
                            {
                                Debug.WriteLine(
                                    $"VM_Mods.NavigateToNpc: Signaling scroll for {npcToSelect.DisplayName}.");
                                _npcSelectionBar.SignalScrollToNpc(npcToSelect);
                            }
                            else
                            {
                                Debug.WriteLine(
                                    $"VM_Mods.NavigateToNpc: ERROR - SelectedNpc is now '{_npcSelectionBar.SelectedNpc?.DisplayName ?? "null"}' " +
                                    $"but expected '{npcToSelect.DisplayName}' before signaling scroll. Scroll aborted.");
                            }

                            // Reset the flag AFTER all operations related to this navigation are complete.
                            _npcSelectionBar.IsProgrammaticNavigationInProgress = false;
                            Debug.WriteLine(
                                $"VM_Mods.NavigateToNpc: IsProgrammaticNavigationInProgress set to false for {npcToSelect.DisplayName}.");
                        });
                    }
                });
            }
            else
            {
                ScrollableMessageBox.ShowWarning(
                    $"Could not find NPC with FormKey {npcFormKey} in the main NPC list.", "NPC Not Found");
                // Ensure flag is reset even if NPC not found.
                if (_npcSelectionBar != null) _npcSelectionBar.IsProgrammaticNavigationInProgress = false;
            }
        });
    }

    // RecalculateMugshotValidity now sets VM_ModSetting.HasValidMugshots
    // based on whether *actual* mugshots can be found for its defined NPCs.
    public void RecalculateMugshotValidity(VM_ModSetting modSetting)
    {
        modSetting.HasValidMugshots =
            CheckMugshotValidity(modSetting.MugShotFolderPaths); // Pass the VM_ModSetting itself
        // If this was the selected mod, refresh the right panel
        if (SelectedModForMugshots == modSetting)
        {
            // Re-run ShowMugshotsAsync to reflect potential change from real to placeholder or vice-versa
            ShowMugshotsCommand.Execute(modSetting).Subscribe().DisposeWith(_disposables);
        }
    }

    private bool CheckMugshotValidity(IEnumerable<string>? mugshotFolderPaths)
    {
        if (mugshotFolderPaths is null) return false;

        foreach (var raw in mugshotFolderPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var path = raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(path)) continue;

            try
            {
                // Valid if ANY file matches 8-hex + image extension AND sits in a plugin-like folder
                var anyValid = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                    .Any(f =>
                    {
                        var fileName = Path.GetFileName(f);
                        if (!MugshotNameRegex.IsMatch(fileName)) return false;

                        var parent = new FileInfo(f).Directory?.Name ?? string.Empty;

                        // Prefer strict plugin-like names; keep your old lenient check as fallback
                        return parent.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)
                               || parent.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)
                               || parent.EndsWith(".esl", StringComparison.OrdinalIgnoreCase)
                               || parent.Contains('.'); // fallback to previous behavior
                    });

                if (anyValid) return true; // short-circuit: any folder passing makes the whole set valid
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking mugshot validity for path {path}: {ExceptionLogger.GetExceptionStack(ex)}");
                // keep scanning other folders
            }
        }

        return false;
    }

    public async Task PopulateModSettingsAsync(VM_SplashScreen? splashReporter)
    {
        _aux.ReinitializeModDependentProperties();
        
        // Phase 0: Cache FaceGen
        splashReporter?.UpdateStep("Pre-caching asset file paths...");
        var faceGenCache = await CacheFaceGenPathsOnLoadAsync(null, splashReporter); // pass null to force full scan of mods folder
        
        // Phase 1: Initialize and load data from disk
        var (tempList, loadedDisplayNames, claimedMugshotPaths, warnings) = InitializePopulation(splashReporter);

        LoadModsFromSettings(tempList, loadedDisplayNames, claimedMugshotPaths);

        var vmsFromMugshotsOnly =
            ScanForMugshotOnlyMods(loadedDisplayNames, claimedMugshotPaths, warnings, splashReporter);

        await ScanForModsInModFolderAsync(tempList, vmsFromMugshotsOnly, loadedDisplayNames, faceGenCache.allFaceGenLooseFiles, faceGenCache.allFaceGenBsaFiles, claimedMugshotPaths,
            splashReporter, warnings);

        // Phase 2: Consolidate and sort the gathered data
        FinalizeModList(tempList, vmsFromMugshotsOnly);
        AddBaseAndCreationClubMods(tempList);
        _allModSettingsInternal.Clear();
        _allModSettingsInternal.AddRange(SortVMs(tempList));

        // Phase 3: Perform heavy analysis on the consolidated data

        try
        {
            await AnalyzeModSettingsAsync(splashReporter, faceGenCache);

            // Phase 4: Run final UI-dependent work
            splashReporter?.UpdateStep("Finalizing mod settings...");
            await FinalizeAndApplySettingsOnUI(warnings);

            _aux.SaveRaceCache();
        }
        catch (AggregateException aggEx)
        {
            // Handle exceptions from the analysis phase
            foreach (var ex in aggEx.Flatten().InnerExceptions)
            {
                Debug.WriteLine($"Async NPC list refresh error (outer): {ExceptionLogger.GetExceptionStack(ex)}");
                Application.Current.Dispatcher.Invoke(() =>
                    warnings.Add($"Async NPC list refresh error: {ExceptionLogger.GetExceptionStack(ex)}"));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in PopulateModSettingsAsync after WhenAll: {ExceptionLogger.GetExceptionStack(ex)}");
            Application.Current.Dispatcher.Invoke(() => warnings.Add($"Unexpected error: {ExceptionLogger.GetExceptionStack(ex)}"));
        }
        finally
        {
            splashReporter?.UpdateStep("Mod settings populated.");
        }
    }
    
    // In VM_Mods.cs

    public async Task RefreshSingleModSettingAsync(VM_ModSetting vmToRefresh)
    {
        if (vmToRefresh == null) return;

        // 1. Generate caches for the specific mod being refreshed.
        var faceGenCache = await CacheFaceGenPathsOnLoadAsync(new[] { vmToRefresh }, null); // No splash screen

        // 2. Update the mod keys based on current folder contents
        vmToRefresh.UpdateCorrespondingModKeys();
        
        // Find and add any missing masters before proceeding with analysis. ***
        if (!string.IsNullOrWhiteSpace(_settings.ModsFolder) && Directory.Exists(_settings.ModsFolder))
        {
            var allModDirectories = Directory.EnumerateDirectories(_settings.ModsFolder).ToList();
            var warnings = new ConcurrentBag<string>(); // Warnings will be logged to debug output.
            FindAndAddMissingMasters(vmToRefresh, allModDirectories, warnings);
            if (warnings.Any())
            {
                Debug.WriteLine($"Warnings during master discovery for '{vmToRefresh.DisplayName}':\n{string.Join("\n", warnings)}");
            }
        }
        
        // 3. Load the necessary plugins for this mod
        var modFolderPathsForVm = vmToRefresh.CorrespondingFolderPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plugins = _pluginProvider.LoadPlugins(vmToRefresh.CorrespondingModKeys, modFolderPathsForVm);

        try
        {
            var originalContainedNpcs = vmToRefresh.NpcFormKeysToDisplayName.Keys.ToHashSet();
            // 4a. Re-evaluate the mod's fundamental type (Appearance vs. Non-Appearance)
            if (vmToRefresh.CorrespondingModKeys.Any())
            {
                // If there are plugins, check if they are appearance-related.
                vmToRefresh.IsFaceGenOnlyEntry = !await ContainsAppearancePluginsAsync(vmToRefresh.CorrespondingModKeys, modFolderPathsForVm);
            }
            else
            {
                // If there are NO plugins, it's only a valid appearance mod if it contains FaceGen files.
                bool hasFaceGen = faceGenCache.allFaceGenLooseFiles.Any() || 
                                  (faceGenCache.allFaceGenBsaFiles.TryGetValue(vmToRefresh.DisplayName, out var bsaFiles) && bsaFiles.Any());

                if (hasFaceGen)
                {
                    vmToRefresh.IsFaceGenOnlyEntry = true;
                }
                else
                {
                    // This mod has no plugins AND no FaceGen. It's no longer an appearance mod.
                    ScrollableMessageBox.Show(
                        $"The mod '{vmToRefresh.DisplayName}' no longer contains any plugins or FaceGen files. It will be removed from the appearance mods list.",
                        "Mod Removed");

                    // Note: Don't add it to cached non appearance mods. If the user deleted the facegen or plugin contents in error, they have a chance to rstore them.
                    // If the user doesn't restore them, this mod will be identified as non-appearance and chached at next startup.

                    // Remove the VM from the list and exit.
                    RemoveModSetting(vmToRefresh);
                    return; // Stop further processing for this mod.
                }
            }
            
            if (vmToRefresh.IsFaceGenOnlyEntry)
            {
                var scanResult = FaceGenScanner.CreateFaceGenScanResultFromCache(vmToRefresh,
                    faceGenCache.allFaceGenLooseFiles, faceGenCache.allFaceGenBsaFiles);

                vmToRefresh.FaceGenOnlyNpcFormKeys.Clear();
                foreach (var (pluginName, npcIds) in scanResult.FaceGenFiles)
                {
                    foreach (var id in npcIds.Where(id => id.Length == 8))
                    {
                        if (FormKey.TryFactory($"{id.Substring(2, 6)}:{pluginName}", out var formKey))
                        {
                            vmToRefresh.FaceGenOnlyNpcFormKeys.Add(formKey);
                        }
                    }
                }
            }
            
            // 4b. Re-run the core analysis functions
            vmToRefresh.RefreshNpcLists(faceGenCache.allFaceGenLooseFiles, faceGenCache.allFaceGenBsaFiles, plugins, _settings.LocalizationLanguage);
            
            var analysisTasks = new List<Task>
            {
                Task.Run(() => vmToRefresh.CheckMergeInSuitability(null)),
                vmToRefresh.FindPluginsWithOverrides(_pluginProvider)
            };

            if (!vmToRefresh.IsFaceGenOnlyEntry)
            {
                analysisTasks.Add(vmToRefresh.CheckForInjectedRecords(null, _settings.LocalizationLanguage));
            }

            await Task.WhenAll(analysisTasks);
            
            var environmentEditorIdMap = _environmentStateProvider.LoadOrder.PriorityOrder.Npc().WinningOverrides()
                .Where(npc => !string.IsNullOrWhiteSpace(npc.EditorID))
                .GroupBy(npc => npc.EditorID!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, 
                    g => g.Select(npc => npc.FormKey).ToHashSet(), 
                    StringComparer.OrdinalIgnoreCase);
            
            var modEditorIdMap = plugins.SelectMany(x => x.Npcs)
                .Where(npc => !string.IsNullOrWhiteSpace(npc.EditorID))
                .GroupBy(npc => npc.EditorID!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, 
                    g => g.Select(npc => npc.FormKey).ToHashSet(), 
                    StringComparer.OrdinalIgnoreCase);
        
            var guests = await vmToRefresh.GetSkyPatcherImportsAsync(environmentEditorIdMap, modEditorIdMap);
            foreach (var (target, source, modDisplayName, npcDisplayName) in guests)
            {
                AddGuestAppearanceToSettings(target, source, modDisplayName, npcDisplayName);
            }

            // 5. Update UI-dependent properties
            RecalculateMugshotValidity(vmToRefresh);
            
            // 6. Update NPC selection Bar
            var toUpdate = _npcSelectionBar.AllNpcs.Where(npc =>
                vmToRefresh.NpcFormKeysToDisplayName.Keys.Contains(npc.NpcFormKey)).ToList();
            foreach (var npc in toUpdate)
            {
                if (!npc.AppearanceMods.Contains(vmToRefresh))
                {
                    npc.AppearanceMods.Add(vmToRefresh);
                }
            }
            
            var removedNpcs = originalContainedNpcs.Where(formKey => !vmToRefresh.NpcFormKeysToDisplayName.Keys.Contains(formKey)).ToList();
            var toRemove = _npcSelectionBar.AllNpcs.Where(npc =>
                removedNpcs.Contains(npc.NpcFormKey)).ToList();

            foreach (var npc in toRemove)
            {
                if (npc.AppearanceMods.Contains(vmToRefresh))
                {
                    npc.AppearanceMods.Remove(vmToRefresh);
                }
            }

            RequestNpcSelectionBarRefreshView();
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"Failed to refresh '{vmToRefresh.DisplayName}':\n{ExceptionLogger.GetExceptionStack(ex)}");
        }
        finally
        {
            // 6. Unload the plugins
            _pluginProvider.UnloadPlugins(vmToRefresh.CorrespondingModKeys);
        }
    }

    private (List<VM_ModSetting> tempList, HashSet<string> loadedDisplayNames, HashSet<string> claimedMugshotPaths,
        List<string> warnings)
        InitializePopulation(VM_SplashScreen? splashReporter)
    {
        _allModSettingsInternal.Clear();
        _overridesCache.Clear();
        IsLoadingNpcData = true;
        var warnings = new List<string>();

        if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid ||
            _environmentStateProvider.LoadOrder == null)
        {
            splashReporter?.ShowMessagesOnClose("Mods Menu: InitializePopulation: Environment is not valid. Cannot accurately link plugins. You should only see this message if you launch this program and you don't have Skyrim SE/AE installed in your SteamApps directory. Go to your settings and point them at your correct Data folder and Game version.");
        }

        splashReporter?.UpdateStep("Processing configured mod settings...");

        return (new List<VM_ModSetting>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            warnings);
    }

    private void LoadModsFromSettings(List<VM_ModSetting> tempList, HashSet<string> loadedDisplayNames,
        HashSet<string> claimedMugshotPaths)
    {
        using (ContextualPerformanceTracer.Trace("PopulateMods.FromSettings"))
        {
            foreach (var settingModel in _settings.ModSettings)
            {
                if (string.IsNullOrWhiteSpace(settingModel.DisplayName)) continue;
                var vm = _modSettingFromModelFactory(settingModel, this);

                bool hasMugShots = false;
                foreach (var mugShotFolderPath in vm.MugShotFolderPaths)
                {
                    if (!string.IsNullOrWhiteSpace(mugShotFolderPath) && Directory.Exists(mugShotFolderPath))
                    {
                        claimedMugshotPaths.Add(mugShotFolderPath);
                        hasMugShots = true;
                    }
                }

                if (!hasMugShots)
                {
                    if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) &&
                        Directory.Exists(_settings.MugshotsFolder))
                    {
                        string potentialPathByName = Path.Combine(_settings.MugshotsFolder, vm.DisplayName);
                        if (Directory.Exists(potentialPathByName) && !claimedMugshotPaths.Contains(potentialPathByName))
                        {
                            vm.MugShotFolderPaths.Add(potentialPathByName);
                            claimedMugshotPaths.Add(potentialPathByName);
                        }
                    }
                }

                tempList.Add(vm);
                loadedDisplayNames.Add(vm.DisplayName);
            }
        }
    }

    private List<VM_ModSetting> ScanForMugshotOnlyMods(HashSet<string> loadedDisplayNames,
        HashSet<string> claimedMugshotPaths, List<string> warnings, VM_SplashScreen? splashReporter)
    {
        //splashReporter?.UpdateStep("Scanning for new Mugshots...");
        var vmsFromMugshotsOnly = new List<VM_ModSetting>();
        using (ContextualPerformanceTracer.Trace("PopulateMods.ScanMugshots"))
        {
            if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder))
            {
                try
                {
                    foreach (var dirPath in Directory.EnumerateDirectories(_settings.MugshotsFolder).ToList())
                    {
                        if (!claimedMugshotPaths.Contains(dirPath))
                        {
                            string folderName = Path.GetFileName(dirPath);
                            if (!loadedDisplayNames.Contains(folderName))
                                vmsFromMugshotsOnly.Add(_modSettingFromMugshotPathFactory(folderName, dirPath, this));
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add(
                        $"Error scanning Mugshots folder '{_settings.MugshotsFolder}': {Environment.NewLine}{ExceptionLogger.GetExceptionStack(ex)}");
                }
            }
        }

        return vmsFromMugshotsOnly;
    }

    #region Mod Folder Scan Result Types

    /// <summary>
    /// Base class for different outcomes of scanning a single mod folder.
    /// </summary>
    private abstract record ModFolderScanResult
    {
    }

    /// <summary>
    /// Represents a newly discovered mod that needs to be added to the list.
    /// </summary>
    private record NewVmResult(VM_ModSetting vm) : ModFolderScanResult
    {
        public VM_ModSetting Vm { get; } = vm;
    }

    /// <summary>
    /// Represents an action to upgrade an existing VM with a new mod folder path and plugins.
    /// </summary>
    private record UpgradeVmResult(string vmDisplayName, string modFolderPath, List<ModKey> modKeys)
        : ModFolderScanResult
    {
        public string VmDisplayName { get; } = vmDisplayName;
        public string ModFolderPath { get; } = modFolderPath;
        public List<ModKey> ModKeys { get; } = modKeys;
    }

    /// <summary>
    /// Represents a folder that should be cached as a non-appearance mod and skipped in the future.
    /// </summary>
    private record CacheNonAppearanceResult(string modFolderPath, string reason) : ModFolderScanResult
    {
        public string ModFolderPath { get; } = modFolderPath;
        public string Reason { get; } = reason;
    }
    
    /// <summary>
    /// A data-transfer object holding the necessary information to create a VM_ModSetting on the UI thread.
    /// </summary>
    private record NewVmCreationData(
        string ModFolderPath,
        List<ModKey> ModKeys,
        bool IsFaceGenOnly,
        HashSet<FormKey> FaceGenFormKeys,
        bool ShouldDisableMergeIn, // Result from CheckMergeInSuitability
        string MergeInTooltip,     // Result from CheckMergeInSuitability
        bool FoundInjectedRecords, // Result from CheckForInjectedRecords
        string InjectedTooltip,     // Result from CheckForInjectedRecords
        List<string> AllFolderPaths, // The final list of all paths
        HashSet<ModKey> ResourceOnlyKeys // The final set of resource keys
    ) : ModFolderScanResult;

    #endregion

    private async Task ScanForModsInModFolderAsync(List<VM_ModSetting> tempList,
        List<VM_ModSetting> vmsFromMugshotsOnly, HashSet<string> loadedDisplayNames,
        Dictionary<string, HashSet<string>> allFaceGenLooseFiles, Dictionary<string, HashSet<string>> allFaceGenBsaFiles,
        HashSet<string> claimedMugshotPaths, VM_SplashScreen? splashReporter, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(_settings.ModsFolder) || !Directory.Exists(_settings.ModsFolder)) return;

        var modDirectories = Directory.EnumerateDirectories(_settings.ModsFolder).ToList();
        if (!modDirectories.Any()) return;

        // Use a thread-safe bag to collect results from all parallel tasks.
        var scanResults = new ConcurrentBag<ModFolderScanResult>();
        var scannedModFolders = 0;
        const string tokenFileName = "NPC_Token.json";
        var cachedNonAppearanceDirs = _settings.CachedNonAppearanceMods.Keys.ToHashSet();

        splashReporter?.UpdateStep($"Scanning {modDirectories.Count} folders for new appearance mods",
            modDirectories.Count);

        // Create a collection of tasks, one for each mod directory.
        var processingTasks = modDirectories.Select(modFolderPath => Task.Run(async () =>
        {
            // -- This entire block runs in parallel for each folder. --

            string modFolderName = Path.GetFileName(modFolderPath);

            // Update progress in a thread-safe manner.
            var currentProgress = (double)Interlocked.Increment(ref scannedModFolders) / modDirectories.Count * 100.0;

            if (File.Exists(Path.Combine(modFolderPath, tokenFileName)) ||
                cachedNonAppearanceDirs.Contains(modFolderPath))
            {
                splashReporter?.IncrementProgress($"Scanned: {modFolderName}");
                return; // Skip this directory.
            }

            var modKeysInFolder = _aux.GetModKeysInDirectory(modFolderPath, warnings, false);

            // Perform READ-ONLY checks against the original lists. This is thread-safe.
            var existingVmFromSettings = tempList.FirstOrDefault(vm =>
                vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));
            var mugshotOnlyVmToUpgrade = vmsFromMugshotsOnly.FirstOrDefault(vm =>
                vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));

            if (existingVmFromSettings != null)
            {
                scanResults.Add(new UpgradeVmResult(existingVmFromSettings.DisplayName, modFolderPath,
                    modKeysInFolder));
            }
            else if (mugshotOnlyVmToUpgrade != null)
            {
                scanResults.Add(new UpgradeVmResult(mugshotOnlyVmToUpgrade.DisplayName, modFolderPath,
                    modKeysInFolder));
            }
            else
            {
                // This helper is called to create a new VM if warranted.
                var newVmResult =
                    await ProcessNewModFolderForParallelScanAsync(modFolderPath, modKeysInFolder, claimedMugshotPaths,
                        allFaceGenLooseFiles, allFaceGenBsaFiles, splashReporter, modDirectories);
                if (newVmResult != null)
                {
                    scanResults.Add(newVmResult);
                }
            }

            // Unload plugins used only in this task's scope.
            _pluginProvider.UnloadPlugins(modKeysInFolder);

            splashReporter?.IncrementProgress($"Scanned: {modFolderName}");
        })).ToList();

        // Await all parallel tasks to complete.
        try
        {
            await Task.WhenAll(processingTasks);
        }
        catch (AggregateException aex) // Catch the specific AggregateException
        {
            // Flatten the exception tree and log every single error that occurred.
            var flattenedExceptions = aex.Flatten();
            foreach (var innerEx in flattenedExceptions.InnerExceptions)
            {
                warnings.Add(
                    $"An error occurred during parallel mod scanning: {Environment.NewLine}{ExceptionLogger.GetExceptionStack(innerEx)}");
            }
        }
        catch (Exception ex)
        {
            warnings.Add(
                $"An error occurred during parallel mod scanning: {Environment.NewLine}{ExceptionLogger.GetExceptionStack(ex)}");
        }

        // -- All parallel work is done. Now, process the results sequentially. --

        // Create a lookup for fast access.
        var mugshotVmLookup = vmsFromMugshotsOnly.ToDictionary(vm => vm.DisplayName, StringComparer.OrdinalIgnoreCase);

        foreach (var result in scanResults)
        {
            switch (result)
            {
                case UpgradeVmResult upgrade:
                    var vmToUpgrade = tempList.FirstOrDefault(vm => vm.DisplayName == upgrade.VmDisplayName)
                                      ?? mugshotVmLookup.GetValueOrDefault(upgrade.VmDisplayName);

                    if (vmToUpgrade != null)
                    {
                        UpgradeVmWithPathAndPlugins(vmToUpgrade, upgrade.ModFolderPath, upgrade.ModKeys);

                        // If it was a mugshot-only VM, it now needs to be moved to the main list.
                        if (mugshotVmLookup.ContainsKey(upgrade.VmDisplayName))
                        {
                            tempList.Add(vmToUpgrade);
                            vmsFromMugshotsOnly.Remove(vmToUpgrade);
                            mugshotVmLookup.Remove(upgrade.VmDisplayName); // Prevent re-adding
                        }
                    }

                    break;

                case NewVmCreationData newData:
                    // This code now runs on the UI thread. It's safe to create the VM here.
                    var newVm = _modSettingFromModFolderFactory(newData.ModFolderPath, newData.ModKeys, this);
                    
                    // Replace the folder and key lists with the correctly ordered ones from the analysis.
                    // This preserves the dependency priority (dependencies first, main mod folder last).
                    newVm.CorrespondingFolderPaths.Clear();
                    foreach (var path in newData.AllFolderPaths)
                    {
                        newVm.CorrespondingFolderPaths.Add(path);
                    }

                    newVm.CorrespondingModKeys.Clear();
                    foreach (var modKey in newData.ModKeys)
                    {
                        newVm.CorrespondingModKeys.Add(modKey);
                    }

                    
                    newVm.ResourceOnlyModKeys = newData.ResourceOnlyKeys;
                    newVm.IsNewlyCreated = true;

                    // Apply the pre-calculated analysis results from the DTO
                    if (newData.ShouldDisableMergeIn)
                    {
                        newVm.MergeInDependencyRecords = false;
                        newVm.MergeInToolTip = newData.MergeInTooltip;
                        newVm.HasAlteredMergeLogic = true; // keeps the text color from being overwritten
                    }
                    if (newData.FoundInjectedRecords)
                    {
                        newVm.IsPerformingBatchAction = true; // suppress warning popup that would appear if user changes the setting manually
                        newVm.HandleInjectedRecords = true;
                        newVm.HandleInjectedOverridesToolTip = newData.InjectedTooltip;
                        newVm.IsPerformingBatchAction = false;
                    }
                    if (newData.IsFaceGenOnly)
                    {
                        newVm.IsFaceGenOnlyEntry = true;
                        newVm.FaceGenOnlyNpcFormKeys = newData.FaceGenFormKeys;
                    }
            
                    // Link to existing mugshot folder if one exists
                    string potentialMugshotPath = Path.Combine(_settings.MugshotsFolder, newVm.DisplayName);
                    if (Directory.Exists(potentialMugshotPath) && !claimedMugshotPaths.Contains(potentialMugshotPath))
                    {
                        newVm.MugShotFolderPaths.Add(potentialMugshotPath);
                        claimedMugshotPaths.Add(potentialMugshotPath);
                    }

                    tempList.Add(newVm);
                    loadedDisplayNames.Add(newVm.DisplayName);
                    break;

                case CacheNonAppearanceResult cache:
                    _settings.CachedNonAppearanceMods.TryAdd(cache.ModFolderPath, cache.Reason);
                    break;
            }
        }
    }

    /// <summary>
    /// A modified version of ProcessNewModFolderAsync designed to return a result object
    /// instead of directly modifying collections, making it safe for parallel execution.
    /// </summary>
    private async Task<ModFolderScanResult?> ProcessNewModFolderForParallelScanAsync(string modFolderPath,
        List<ModKey> modKeysInFolder, ICollection<string> claimedMugshotPaths, Dictionary<string, HashSet<string>> allFaceGenLooseFiles, 
        Dictionary<string, HashSet<string>> allFaceGenBsaFiles, VM_SplashScreen? splashReporter,
        IReadOnlyCollection<string> allModDirectories)
    {
        // This VM will be discarded and never touches the UI.
        var tempVmForAnalysis = _modSettingFromModFolderFactory(modFolderPath, modKeysInFolder, this);
        tempVmForAnalysis.IsNewlyCreated = true;
        
        var scanResult = FaceGenScanner.CreateFaceGenScanResultFromCache(tempVmForAnalysis, allFaceGenLooseFiles, allFaceGenBsaFiles);

        if (!scanResult.AnyFilesFound)
        {
            return new CacheNonAppearanceResult(modFolderPath, "No FaceGen Files Found");
        }

        _pluginProvider.LoadPlugins(modKeysInFolder, new HashSet<string> { modFolderPath });
        
        // *** CALL THE NEW MASTER DISCOVERY LOGIC ***
        var warnings = new ConcurrentBag<string>();
        FindAndAddMissingMasters(tempVmForAnalysis, allModDirectories, warnings);
        if (splashReporter != null && !warnings.IsEmpty)
        {
            foreach (var warning in warnings)
            {
                splashReporter.ShowMessagesOnClose(warning);
            }
        }
    
        // Now, tempVmForAnalysis has been updated with any discovered dependency folders and resource plugins.

        if (modKeysInFolder.Any() && await ContainsAppearancePluginsAsync(modKeysInFolder, new() { modFolderPath }))
        {
            // Run analysis using the temporary VM
            tempVmForAnalysis.CheckMergeInSuitability(
                splashReporter == null ? null : splashReporter.ShowMessagesOnClose);
            bool injectedFound =
                await tempVmForAnalysis.CheckForInjectedRecords(splashReporter == null
                    ? null
                    : splashReporter.ShowMessagesOnClose, _settings.LocalizationLanguage);

            // Return a DTO with the data, not the VM itself
            return new NewVmCreationData(
                modFolderPath,
                tempVmForAnalysis.CorrespondingModKeys.ToList(), // Use updated list
                IsFaceGenOnly: false,
                FaceGenFormKeys: new HashSet<FormKey>(),
                ShouldDisableMergeIn: !tempVmForAnalysis.MergeInDependencyRecords,
                MergeInTooltip: tempVmForAnalysis.MergeInToolTip,
                FoundInjectedRecords: injectedFound,
                InjectedTooltip: tempVmForAnalysis.HandleInjectedOverridesToolTip,
                AllFolderPaths: tempVmForAnalysis.CorrespondingFolderPaths.ToList(), // Use updated list
                ResourceOnlyKeys: new HashSet<ModKey>(tempVmForAnalysis.ResourceOnlyModKeys) // Use updated set
            );
        }
        else if (modKeysInFolder.Any())
        {
            return new CacheNonAppearanceResult(modFolderPath,
                "Does not provide new NPCs or modify any NPCs currently in load order");
        }
        else // FaceGen only
        {
            var faceGenKeys = new HashSet<FormKey>();
            foreach (var (pluginName, npcIds) in scanResult.FaceGenFiles)
            {
                foreach (var id in npcIds.Where(id => id.Length == 8))
                {
                    if (FormKey.TryFactory($"{id.Substring(2, 6)}:{pluginName}", out var formKey))
                    {
                        faceGenKeys.Add(formKey);
                    }
                }
            }

            tempVmForAnalysis.CheckMergeInSuitability(
                splashReporter == null ? null : splashReporter.ShowMessagesOnClose);

            // Return a DTO for a FaceGen-only mod
            return new NewVmCreationData(
                modFolderPath,
                tempVmForAnalysis.CorrespondingModKeys.ToList(), // Use updated list
                IsFaceGenOnly: true,
                FaceGenFormKeys: faceGenKeys,
                ShouldDisableMergeIn: !tempVmForAnalysis.MergeInDependencyRecords,
                MergeInTooltip: tempVmForAnalysis.MergeInToolTip,
                FoundInjectedRecords: false,
                InjectedTooltip: tempVmForAnalysis.HandleInjectedOverridesToolTip,
                AllFolderPaths: tempVmForAnalysis.CorrespondingFolderPaths.ToList(), // Use updated list
                ResourceOnlyKeys: new HashSet<ModKey>(tempVmForAnalysis.ResourceOnlyModKeys) // Use updated set
            );
        }
    }

    private void UpgradeVmWithPathAndPlugins(VM_ModSetting vm, string modFolderPath, List<ModKey> modKeysInFolder)
    {
        if (!vm.CorrespondingFolderPaths.Contains(modFolderPath, StringComparer.OrdinalIgnoreCase))
        {
            vm.CorrespondingFolderPaths.Add(modFolderPath);
        }

        foreach (var key in modKeysInFolder)
        {
            if (!vm.CorrespondingModKeys.Contains(key))
            {
                vm.CorrespondingModKeys.Add(key);
            }
        }
    }

    private void FinalizeModList(List<VM_ModSetting> tempList, List<VM_ModSetting> vmsFromMugshotsOnly)
    {
        foreach (var mugshotVm in vmsFromMugshotsOnly)
        {
            if (!tempList.Any(existing =>
                    existing.DisplayName.Equals(mugshotVm.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                tempList.Add(mugshotVm);
            }
        }

        foreach (var vm in tempList)
        {
            if (vm.IsMugshotOnlyEntry && (vm.CorrespondingFolderPaths.Any() || vm.CorrespondingModKeys.Any()))
            {
                vm.IsMugshotOnlyEntry = false;
            }
        }
    }

    private void AddBaseAndCreationClubMods(List<VM_ModSetting> tempList)
    {
        var baseGameModKeys = _environmentStateProvider.BaseGamePlugins ?? new();
        var creationClubModKeys = _environmentStateProvider.CreationClubPlugins ?? new();

        baseGameModKeys.RemoveWhere(mk => tempList.Any(vm =>
            !vm.IsFaceGenOnlyEntry && !vm.IsMugshotOnlyEntry && vm.CorrespondingModKeys.Contains(mk)));
        creationClubModKeys.RemoveWhere(mk => tempList.Any(vm =>
            !vm.IsFaceGenOnlyEntry && !vm.IsMugshotOnlyEntry && vm.CorrespondingModKeys.Contains(mk)));

        if (creationClubModKeys.Any() && !tempList.Any(vm =>
                vm.DisplayName.Equals(CreationClubModsettingName, StringComparison.OrdinalIgnoreCase)))
        {
            var ccMod = new ModSetting()
            {
                DisplayName = CreationClubModsettingName, CorrespondingModKeys = creationClubModKeys.ToList(),
                IsAutoGenerated = true, MergeInDependencyRecords = false
            };
            var ccModVm = _modSettingFromModelFactory(ccMod, this);
            ccModVm.MergeInDependencyRecordsVisible = false;
            tempList.Add(ccModVm);
        }

        if (baseGameModKeys.Any() && !tempList.Any(vm =>
                vm.DisplayName.Equals(BaseGameModSettingName, StringComparison.OrdinalIgnoreCase)))
        {
            var baseMod = new ModSetting()
            {
                DisplayName = BaseGameModSettingName, CorrespondingModKeys = baseGameModKeys.ToList(),
                IsAutoGenerated = true, MergeInDependencyRecords = false
            };
            var baseModVm = _modSettingFromModelFactory(baseMod, this);
            baseModVm.MergeInDependencyRecordsVisible = false;
            tempList.Add(baseModVm);
        }
    }

    private async Task AnalyzeModSettingsAsync(VM_SplashScreen? splashReporter,
        (Dictionary<string,HashSet<string>> allFaceGenLooseFiles, Dictionary<string, HashSet<string>> allFaceGenBsaFiles) faceGenCache)
    {
        var maxParallelism = Environment.ProcessorCount;
        var semaphore = new SemaphoreSlim(maxParallelism);
        
        // --- NEW: Setup for SkyPatcher import ---
        var environmentEditorIdMap = _environmentStateProvider.LoadOrder.PriorityOrder.Npc().WinningOverrides()
            .Where(npc => !string.IsNullOrWhiteSpace(npc.EditorID))
            .GroupBy(npc => npc.EditorID!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, 
                g => g.Select(npc => npc.FormKey).ToHashSet(), 
                StringComparer.OrdinalIgnoreCase);

        var allSkyPatcherGuests = new ConcurrentBag<(FormKey Target, FormKey Source, string ModDisplayName, string SourceNpcDisplayName)>();

        var allVMs = _allModSettingsInternal.ToList(); // Create a copy to iterate over
        var vmsToAnalyze = new List<VM_ModSetting>();

        var modSettingsToLogCount = _allModSettingsInternal.Count(x => x.IsNewlyCreated);
        var analyzedCount = 0;
        splashReporter?.UpdateStep($"Preparing to analyze data for {modSettingsToLogCount} Mods...");

        // --- CACHING LOGIC ---
        using (ContextualPerformanceTracer.Trace("AnalyzeModSettings.CacheValidation"))
        {
            foreach (var vm in allVMs)
            {
                // Don't use cache for newly discovered mods or facegen-only entries
                if (vm.IsNewlyCreated || vm.IsFaceGenOnlyEntry)
                {
                    vm.LastKnownState = null; // Ensure no old state is saved
                    vmsToAnalyze.Add(vm);
                    continue;
                }

                var currentSnapshot = vm.GenerateSnapshot();
                if (currentSnapshot != null && vm.LastKnownState != null && currentSnapshot.Equals(vm.LastKnownState))
                {
                    // CACHE HIT: The mod is unchanged. Do nothing.
                    Debug.WriteLine($"Cache HIT for: {vm.DisplayName}");
                    // The VM was already populated from the model, so we are done with it.
                    vm.LastKnownState = currentSnapshot; // Keep the snapshot updated in the VM to save it again.
                }
                else
                {
                    // CACHE MISS: Mod has changed or snapshot failed. Needs analysis.
                    Debug.WriteLine($"Cache MISS for: {vm.DisplayName}");
                    vmsToAnalyze.Add(vm);
                    vm.LastKnownState = currentSnapshot; // Store the NEW snapshot to be saved after analysis
                }
            }
        }
        // --- END CACHING LOGIC ---

        var refreshTasks = vmsToAnalyze.Select(async vm =>
        {
            await semaphore.WaitAsync();
            try
            {
                await Task.Run(async () =>
                {
                    var modFolderPathsForVm = vm.CorrespondingFolderPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var plugins = _pluginProvider.LoadPlugins(vm.CorrespondingModKeys, modFolderPathsForVm);
                    try
                    {
                        using (ContextualPerformanceTracer.Trace("RefreshNpcLists"))
                        {
                            vm.RefreshNpcLists(faceGenCache.allFaceGenLooseFiles, faceGenCache.allFaceGenBsaFiles,
                                plugins, _settings.LocalizationLanguage);
                        }

                        if (!vm.IsMugshotOnlyEntry)
                        {
                            if (vm.IsNewlyCreated)
                            {
                                using (ContextualPerformanceTracer.Trace("FindPluginsWithOverrides"))
                                {
                                    await vm.FindPluginsWithOverrides(_pluginProvider);
                                }
                            }

                            // --- NEW: Parse SkyPatcher files while plugins are loaded ---
                            // Make sure to profile this and gate behind IsNewlyCreated if necessary.
                            var modEditorIdMap = plugins.SelectMany(x => x.Npcs)
                                .Where(npc => !string.IsNullOrWhiteSpace(npc.EditorID))
                                .GroupBy(npc => npc.EditorID!, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, 
                                    g => g.Select(npc => npc.FormKey).ToHashSet(), 
                                    StringComparer.OrdinalIgnoreCase);

                            var guests = await vm.GetSkyPatcherImportsAsync(environmentEditorIdMap, modEditorIdMap);
                            foreach (var guest in guests)
                            {
                                allSkyPatcherGuests.Add(guest);
                            }
                        }
                    }
                    finally
                    {
                        _pluginProvider.UnloadPlugins(vm.CorrespondingModKeys);
                        var currentAnalyzed = Interlocked.Increment(ref analyzedCount);
                        var progress = modSettingsToLogCount > 0
                            ? (double)currentAnalyzed / modSettingsToLogCount * 100.0
                            : 0;
                        splashReporter?.UpdateProgress(progress, $"Analyzed: {vm.DisplayName}");
                    }
                });
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(refreshTasks);
        
        // --- Resolve and apply the collected SkyPatcher data after all analysis is done ---
        if (!allSkyPatcherGuests.IsEmpty)
        {
            await ResolveAndApplySkyPatcherGuests(allSkyPatcherGuests.ToList());
        }
    }
    
    public async Task<bool> RescanSingleModFolderAsync(string modFolderPath)
    {
        if (string.IsNullOrWhiteSpace(modFolderPath) || !Directory.Exists(modFolderPath))
        {
            return false;
        }

        string modFolderName = Path.GetFileName(modFolderPath);
        if (_allModSettingsInternal.Any(vm => vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase)))
        {
            ScrollableMessageBox.ShowWarning($"An appearance mod named '{modFolderName}' already exists. Cannot re-import from cached list.", "Mod Already Exists");
            return false;
        }

        var modKeysInFolder = _aux.GetModKeysInDirectory(modFolderPath, new List<string>(), false);
        var newVm = _modSettingFromModFolderFactory(modFolderPath, modKeysInFolder, this);
        newVm.IsNewlyCreated = true;

        _allModSettingsInternal.Add(newVm);
        SortVMsInPlace();
        
        await RefreshSingleModSettingAsync(newVm);

        bool wasSuccessfullyImported = _allModSettingsInternal.Contains(newVm);
        
        ApplyFilters();

        return wasSuccessfullyImported;
    }
    
    // This NEW helper method contains the logic to resolve and save guest appearances.
    // It is called only once by AnalyzeModSettingsAsync.
    private async Task ResolveAndApplySkyPatcherGuests(IReadOnlyCollection<(FormKey TargetNpc, FormKey SourceNpc, string ModDisplayName, string SourceNpcDisplayName)> guests)
    {
        Debug.WriteLine($"Resolving {guests.Count} discovered SkyPatcher guest appearances...");
        int addedCount = 0;
        foreach (var (targetNpcKey, sourceNpcKey, modDisplayName, npcDisplayName) in guests)
        {
            if (AddGuestAppearanceToSettings(targetNpcKey, sourceNpcKey, modDisplayName, npcDisplayName))
            {
                addedCount++;
            }
        }
        Debug.WriteLine($"Finished processing SkyPatcher imports. Added {addedCount} new guest appearances.");
    }

    private async Task FinalizeAndApplySettingsOnUI(List<string> warnings)
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var vm in _allModSettingsInternal)
            {
                RecalculateMugshotValidity(vm);
            }

            IsLoadingNpcData = false;
            ApplyFilters();

            if (warnings.Any())
            {
                ScrollableMessageBox.ShowWarning(string.Join("\n", warnings), "Mod Settings Population Warning");
            }
        });
    }
    
    private async Task<(Dictionary<string, HashSet<string>> allFaceGenLooseFiles, Dictionary<string, HashSet<string>>
            allFaceGenBsaFiles)>
        CacheFaceGenPathsOnLoadAsync(IEnumerable<VM_ModSetting>? vmsToProcess, VM_SplashScreen? splashReporter)
    {
        var vmsToProcessList = vmsToProcess?.ToList();

        // --- Part 1: Cache loose files ---
        Debug.WriteLine("Caching loose FaceGen file paths...");
        List<string> allPathsToScanForLooseFiles;
        if (vmsToProcessList != null && vmsToProcessList.Any())
        {
            // Scenario 1: Specific VMs are provided. Scan only their folders.
            allPathsToScanForLooseFiles = vmsToProcessList
                .SelectMany(vm => vm.CorrespondingFolderPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            // Scenario 2: No specific VMs provided. Scan all subdirectories in the main Mods folder.
            allPathsToScanForLooseFiles = new List<string>();
            if (!string.IsNullOrWhiteSpace(_settings.ModsFolder) && Directory.Exists(_settings.ModsFolder))
            {
                try
                {
                    allPathsToScanForLooseFiles.AddRange(Directory.EnumerateDirectories(_settings.ModsFolder));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error enumerating ModsFolder for loose FaceGen caching: {ExceptionLogger.GetExceptionStack(ex)}");
                }
            }
        }

        var allFaceGenLooseFiles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var modPath in allPathsToScanForLooseFiles)
        {
            if (!Directory.Exists(modPath)) continue;

            var looseFilesInMod = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var texturesPath = Path.Combine(modPath, "Textures");
            if (Directory.Exists(texturesPath))
            {
                foreach (var file in Directory.EnumerateFiles(texturesPath, "*.dds", SearchOption.AllDirectories))
                {
                    looseFilesInMod.Add(Path.GetRelativePath(modPath, file).Replace('\\', '/'));
                }
            }

            var meshesPath = Path.Combine(modPath, "Meshes");
            if (Directory.Exists(meshesPath))
            {
                foreach (var file in Directory.EnumerateFiles(meshesPath, "*.nif", SearchOption.AllDirectories))
                {
                    looseFilesInMod.Add(Path.GetRelativePath(modPath, file).Replace('\\', '/'));
                }
            }

            if (looseFilesInMod.Any())
            {
                allFaceGenLooseFiles[modPath] = looseFilesInMod;
            }
        }

        Debug.WriteLine($"Cached loose file paths for {allFaceGenLooseFiles.Count} mod folders.");

        // --- Part 2: Asynchronously cache BSA files with progress reporting ---
        splashReporter?.UpdateStep("Pre-caching BSA file paths...");
        Debug.WriteLine("Pre-caching all relevant BSA paths...");

        var (vmBsaPathsCache, allRelevantBsaPaths) = await Task.Run(() =>
        {
            var localVmBsaPathsCache = new Dictionary<string, Dictionary<ModKey, HashSet<string>>>();
            var localAllRelevantBsaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<VM_ModSetting> vmsToIterate;
            if (vmsToProcessList != null && vmsToProcessList.Any())
            {
                // Scenario 1: Specific VMs were provided.
                vmsToIterate = vmsToProcessList;
            }
            else
            {
                // Scenario 2: Full scan. Create temporary VMs for every mod folder to discover their plugins and associated BSAs.
                var tempVmsForFullScan = new List<VM_ModSetting>();
                if (!string.IsNullOrWhiteSpace(_settings.ModsFolder) && Directory.Exists(_settings.ModsFolder))
                {
                    foreach (var modDir in Directory.EnumerateDirectories(_settings.ModsFolder))
                    {
                        var modKeys = _aux.GetModKeysInDirectory(modDir, new(), false);
                        tempVmsForFullScan.Add(_modSettingFromModFolderFactory(modDir, modKeys, this));
                    }
                }

                // Also include Base Game and Creation Club in a full scan.
                var baseGameModKeys = _environmentStateProvider.BaseGamePlugins ?? new();
                if (baseGameModKeys.Any())
                {
                    var baseMod = new ModSetting()
                    {
                        DisplayName = BaseGameModSettingName, CorrespondingModKeys = baseGameModKeys.ToList(),
                        IsAutoGenerated = true
                    };
                    tempVmsForFullScan.Add(_modSettingFromModelFactory(baseMod, this));
                }

                var creationClubModKeys = _environmentStateProvider.CreationClubPlugins ?? new();
                if (creationClubModKeys.Any())
                {
                    var ccMod = new ModSetting()
                    {
                        DisplayName = CreationClubModsettingName, CorrespondingModKeys = creationClubModKeys.ToList(),
                        IsAutoGenerated = true
                    };
                    tempVmsForFullScan.Add(_modSettingFromModelFactory(ccMod, this));
                }

                vmsToIterate = tempVmsForFullScan;
            }

            var totalVmCount = vmsToIterate.Count;
            var processedVmCount = 0;

            foreach (var vm in vmsToIterate)
            {
                var pathsToSearch = new HashSet<string>(vm.CorrespondingFolderPaths);
                if (vm.IsAutoGenerated)
                {
                    pathsToSearch.Add(_environmentStateProvider.DataFolderPath);
                }

                var bsaDictForVm = _bsaHandler.GetBsaPathsForPluginsInDirs(vm.CorrespondingModKeys, pathsToSearch,
                    _settings.SkyrimRelease.ToGameRelease());

                localVmBsaPathsCache[vm.DisplayName] = bsaDictForVm;

                foreach (var bsaPath in bsaDictForVm.Values.SelectMany(paths => paths))
                {
                    localAllRelevantBsaPaths.Add(bsaPath);
                }

                processedVmCount++;
                var progress = totalVmCount > 0 ? (double)processedVmCount / totalVmCount * 100.0 : 100.0;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    splashReporter?.UpdateProgress(progress, $"Analyzing assets: {vm.DisplayName}");
                });
            }

            return (localVmBsaPathsCache, localAllRelevantBsaPaths);
        });
        Debug.WriteLine($"Found {allRelevantBsaPaths.Count} unique BSAs to process.");

        splashReporter?.UpdateStep("Caching asset contents...");
        var bsaContentCache = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var processingTasks = allRelevantBsaPaths.Select(bsaPath => Task.Run(() =>
        {
            var faceGenFilesInArchive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bsaReaders =
                _bsaHandler.OpenBsaArchiveReaders(new[] { bsaPath }, _settings.SkyrimRelease.ToGameRelease(), false);

            if (bsaReaders.TryGetValue(bsaPath, out var reader) && reader.Files.Any())
            {
                foreach (var fileRecord in reader.Files)
                {
                    string path = fileRecord.Path.ToLowerInvariant().Replace('\\', '/');
                    if (path.StartsWith("meshes/actors/character/facegendata/") ||
                        path.StartsWith("textures/actors/character/facegendata/"))
                    {
                        faceGenFilesInArchive.Add(path);
                    }
                }
            }

            bsaContentCache.TryAdd(bsaPath, faceGenFilesInArchive);
        })).ToList();

        await Task.WhenAll(processingTasks);

        Debug.WriteLine("Finished caching content from all BSAs.");
        splashReporter?.UpdateStep("Finalizing asset cache...");

        // --- Part 3: Assemble the final dictionary for each VM using the caches ---
        var allFaceGenBsaFiles = new Dictionary<string, HashSet<string>>();
        foreach (var vmDisplayName in vmBsaPathsCache.Keys)
        {
            var bsaFilePathsForVm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (vmBsaPathsCache.TryGetValue(vmDisplayName, out var bsaDict))
            {
                var uniqueBsaPathsForVm = bsaDict.Values.SelectMany(paths => paths)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var bsaPath in uniqueBsaPathsForVm)
                {
                    if (bsaContentCache.TryGetValue(bsaPath, out var cachedContent))
                    {
                        bsaFilePathsForVm.UnionWith(cachedContent);
                    }
                }
            }

            allFaceGenBsaFiles[vmDisplayName] = bsaFilePathsForVm;
        }

        Debug.WriteLine($"Assembled BSA file paths for {allFaceGenBsaFiles.Count} mod settings from cache.");
        return (allFaceGenLooseFiles, allFaceGenBsaFiles);
    }

    /// <summary>
    /// Scans the masters of a VM's plugins. If any masters are not in the load order or already part of the VM,
    /// it searches all other mod directories to find them, adding the best candidate folder as a resource.
    /// </summary>
    private void FindAndAddMissingMasters(
        VM_ModSetting vm,
        IReadOnlyCollection<string> allModDirectories,
        ConcurrentBag<string> warnings)
    {
        var loadOrderKeys = _environmentStateProvider.LoadOrderModKeys.ToHashSet();
        // Start with the plugins we know about before this process began.
        var knownPluginKeysInVm = new HashSet<ModKey>(vm.CorrespondingModKeys);
        var currentFoldersInVm = vm.CorrespondingFolderPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingMastersToFind = new HashSet<ModKey>();

        // Step 1: Find all missing masters from the VM's current set of plugins.
        var plugins = _pluginProvider.LoadPlugins(vm.CorrespondingModKeys, currentFoldersInVm);
        foreach (var plugin in plugins)
        {
            foreach (var masterRef in plugin.ModHeader.MasterReferences)
            {
                var masterKey = masterRef.Master;
                if (!loadOrderKeys.Contains(masterKey) && !knownPluginKeysInVm.Contains(masterKey))
                {
                    missingMastersToFind.Add(masterKey);
                }
            }
        }

        if (!missingMastersToFind.Any())
        {
            return; // Nothing to do
        }

        // Step 2: Find potential source folders for the missing masters.
        var foldersToSearch = allModDirectories.Where(d => !currentFoldersInVm.Contains(d)).ToList();
        var newResourceFoldersToAdd = new Dictionary<string, List<ModKey>>(StringComparer.OrdinalIgnoreCase);

        foreach (var master in missingMastersToFind)
        {
            var candidates = new List<(string Path, DateTime LastWrite)>();
            foreach (var folder in foldersToSearch)
            {
                string pluginPath = Path.Combine(folder, master.FileName.String);
                if (File.Exists(pluginPath))
                {
                    candidates.Add((folder, File.GetLastWriteTimeUtc(pluginPath)));
                }
            }

            if (candidates.Any())
            {
                var winner = candidates.OrderByDescending(c => c.LastWrite).First();
                if (candidates.Count > 1)
                {
                    var sources = string.Join(", ", candidates.Select(c => Path.GetFileName(c.Path)));
                    warnings.Add(
                        $"Found multiple sources for master '{master.FileName}' needed by '{vm.DisplayName}': [{sources}]. Choosing the newest version from '{Path.GetFileName(winner.Path)}'.");
                }

                // If we haven't already decided to add this folder, add it now.
                if (!newResourceFoldersToAdd.ContainsKey(winner.Path))
                {
                    var pluginsInWinnerFolder = _aux.GetModKeysInDirectory(winner.Path, new List<string>(), false);
                    newResourceFoldersToAdd[winner.Path] = pluginsInWinnerFolder;
                }
            }
            else
            {
                Debug.WriteLine(
                    $"Could not find a local source for missing master '{master.FileName}' for mod '{vm.DisplayName}'.");
            }
        }

        // Step 3: Apply the newly discovered folders and plugins to the VM.
        if (newResourceFoldersToAdd.Any())
        {
            vm.IsPerformingBatchAction = true; // prevent popups
            foreach (var (folderPath, pluginsInFolder) in newResourceFoldersToAdd)
            {
                if (!vm.CorrespondingFolderPaths.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
                {
                    vm.CorrespondingFolderPaths.Insert(0, folderPath);
                }

                foreach (var pluginKey in pluginsInFolder)
                {
                    vm.ResourceOnlyModKeys.Add(pluginKey);
                    if (!vm.CorrespondingModKeys.Contains(pluginKey))
                    {
                        vm.CorrespondingModKeys.Insert(0, pluginKey);
                    }
                }
            }

            vm.IsPerformingBatchAction = false;
        }
    }

    // Filtering Logic (Left Panel)
    public void ApplyFilters()
    {
        // If data is actively being loaded, the underlying collection is unstable.
        // Clear the public list and exit. The loading process will call this method again when complete.
        if (IsLoadingNpcData)
        {
            ModSettingsList.Clear();
            return;
        }

        IEnumerable<VM_ModSetting> filtered = _allModSettingsInternal;

        if (!string.IsNullOrWhiteSpace(NameFilterText))
        {
            filtered = filtered.Where(vm =>
                vm.DisplayName.Contains(NameFilterText, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(PluginFilterText))
        {
            filtered = filtered.Where(vm => vm.CorrespondingModKeys.Any(key =>
                key.FileName.String.Contains(PluginFilterText, StringComparison.OrdinalIgnoreCase) ||
                key.ToString().Contains(PluginFilterText, StringComparison.OrdinalIgnoreCase)
            ));
        }

        // *** Apply NPC Filter ***
        if (!IsLoadingNpcData && !string.IsNullOrWhiteSpace(NpcSearchText))
        {
            string searchTextLower = NpcSearchText.Trim().ToLowerInvariant(); // Use invariant culture lowercase

            switch (SelectedNpcSearchType)
            {
                case ModNpcSearchType.Name:
                    filtered = filtered.Where(vm =>
                        vm.NpcNames.Any(name =>
                            name.Contains(searchTextLower, StringComparison.OrdinalIgnoreCase)) ||
                        vm.NpcFormKeysToDisplayName.Values.Any(dName =>
                            dName.Contains(searchTextLower,
                                StringComparison.OrdinalIgnoreCase))); // Also check dictionary values
                    break;
                case ModNpcSearchType.EditorID:
                    filtered = filtered.Where(vm =>
                        vm.NpcEditorIDs.Any(
                            eid => eid.Contains(searchTextLower, StringComparison.OrdinalIgnoreCase)));
                    break;
                case ModNpcSearchType.FormKey:
                    // Compare string representations of FormKeys
                    filtered = filtered.Where(vm => vm.NpcFormKeys.Any(fk =>
                        fk.ToString().Contains(searchTextLower, StringComparison.OrdinalIgnoreCase)));
                    break;
            }
        }
        // *** End NPC Filter ***

        var previouslySelectedMod = SelectedModForMugshots; // Preserve selection if possible

        ModSettingsList.Clear();
        var filteredList = filtered.ToList(); // Materialize the list
        foreach (var vm in filteredList)
        {
            ModSettingsList.Add(vm);
        }

        // Check if the previously selected item for mugshots is still in the filtered list
        if (previouslySelectedMod != null && !filteredList.Contains(previouslySelectedMod))
        {
            // It was filtered out, clear the right panel
            SelectedModForMugshots = null;
            CurrentModNpcMugshots.Clear();
        }


        System.Diagnostics.Debug.WriteLine(
            $"ApplyFilters: Displaying {ModSettingsList.Count} of {_allModSettingsInternal.Count} items.");
    }

    // Add this helper to centralize the logic for adding a guest to settings.
    private bool AddGuestAppearanceToSettings(FormKey targetNpcKey, FormKey guestNpcKey, string guestModName, string guestDisplayStr)
    {
        if (!_settings.GuestAppearances.TryGetValue(targetNpcKey, out var guestSet))
        {
            guestSet = new HashSet<(string, FormKey, string)>();
            _settings.GuestAppearances[targetNpcKey] = guestSet;
        }
        // The tuple now matches the required (string ModName, FormKey NpcFormKey, string NpcDisplayName) format.
        return guestSet.Add((guestModName, guestNpcKey, guestDisplayStr));
    }

    public bool TryGetWinningNpc(FormKey fk, out INpcGetter? npcGetter)
    {
        var matchingNpc = _environmentStateProvider.LinkCache.TryResolve<INpcGetter>(fk, out npcGetter);
        return matchingNpc;
    }


    // Save Logic
    public void SaveModSettingsToModel()
    {
        _settings.ModSettings.Clear();
        foreach (var vm in _allModSettingsInternal) // Save from the full list
        {
            // Only save if it has meaningful data (Key, Folder Paths, or Mugshot Path)
            if (!string.IsNullOrWhiteSpace(vm.DisplayName) &&
                (vm.CorrespondingModKeys.Any() || vm.CorrespondingFolderPaths.Any())) // Check if any keys exist
            {
                // Create a new ModSetting model instance
                var model = vm.SaveToModel();
                _settings.ModSettings.Add(model);
            }
        }

        System.Diagnostics.Debug.WriteLine(
            $"DEBUG: SaveModSettingsToModel preparing to save {_settings.ModSettings.Count} items.");

        // Saving the main settings file is handled by VM_Settings on App Exit
        // string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
        // JSONhandler<Settings>.SaveJSONFile(_settings, settingsPath, out bool success, out string exception);
        // if (!success) { MessageBox.Show($"Error saving settings: {exception}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error); }
        // else { System.Diagnostics.Debug.WriteLine("DEBUG: Settings successfully updated in memory by SaveModSettingsToModel."); }
    }

    /// <summary>
    /// Removes the specified VM_ModSetting from the internal list and refreshes the filtered view.
    /// </summary>
    /// <param name="modSettingToRemove">The VM_ModSetting to remove.</param>
    /// <returns>True if the item was successfully found and removed; otherwise, false.</returns>
    public bool RemoveModSetting(VM_ModSetting modSettingToRemove)
    {
        if (modSettingToRemove == null) return false;

        bool removed = _allModSettingsInternal.Remove(modSettingToRemove);
        if (removed)
        {
            Debug.WriteLine($"VM_Mods: Removed ModSetting '{modSettingToRemove.DisplayName}' from internal list.");
            // If the removed mod was selected for mugshots, clear the selection
            if (SelectedModForMugshots == modSettingToRemove)
            {
                SelectedModForMugshots = null;
                CurrentModNpcMugshots.Clear();
            }

            ApplyFilters(); // Refresh the ModSettingsList (left panel)
        }
        else
        {
            Debug.WriteLine(
                $"VM_Mods: ModSetting '{modSettingToRemove.DisplayName}' not found in internal list for removal.");
        }

        return removed;
    }

    /// <summary>
    /// Tries to find an existing VM_ModSetting matching the plugin key.
    /// It searches based on the CorrespondingModKey property of the VM_ModSettings.
    /// </summary>
    /// <param name="appearancePluginKey">The ModKey of the appearance plugin to search for.</param>
    /// <param name="foundVms">Output: The found VM_ModSetting instances if a match exists; otherwise, empty list.</param>
    /// <returns>True if a matching VM_ModSetting was found based on the CorrespondingModKey, false otherwise.</returns>
    public bool TryGetModSettingForPlugin(ModKey appearancePluginKey, out List<VM_ModSetting> foundVms)
    {
        // Initialize output parameters
        foundVms = new();

        // Check if the input ModKey is valid (not null or default)
        if (appearancePluginKey.IsNull)
        {
            Debug.WriteLine($"TryGetModSettingForPlugin: Received an invalid (IsNull) ModKey.");
            // Keep foundVm as null and modDisplayName as the default set above.
            return false; // Cannot find a match for an invalid key.
        }

        // Search the internal list of all loaded/created mod settings.
        // Find the first VM where *any* of its CorrespondingModKeys matches the input key.
        foundVms = _allModSettingsInternal.Where(vm =>
            vm.CorrespondingModKeys.Any(key => key.Equals(appearancePluginKey))).ToList();

        if (foundVms.Count > 1)
        {
            foundVms = foundVms.Where(x => !x.IsFaceGenOnlyEntry).ToList();
        }

        return foundVms.Any();
    }

    /// <summary>
    /// Called after a Mod Folder Path or Mugshot Folder Path is potentially changed on a VM_ModSetting.
    /// Checks if this change links it to another complementary VM (one with only mugshots, one with only mods)
    /// and performs an automatic merge if conditions are met.
    /// </summary>
    /// <param name="modifiedVm">The VM that the user directly modified.</param>
    /// <param name="addedOrSetPath">The specific path that was added or set.</param>
    /// <param name="pathType">Indicates whether a ModFolder or MugshotFolder was changed.</param>
    /// <param name="hadMugshotPathBefore">Did modifiedVm have a mugshot path BEFORE this change?</param>
    /// <param name="hadModPathsBefore">Did modifiedVm have mod paths BEFORE this change?</param>
    public async Task CheckForAndPerformMergeAsync(VM_ModSetting modifiedVm, string addedOrSetPath, PathType pathType,
        bool hadMugshotPathBefore, bool hadModPathsBefore)
    {
        if (modifiedVm == null || string.IsNullOrEmpty(addedOrSetPath)) return;

        VM_ModSetting? sourceVm = null; // The potential VM to merge *from*

        // Find a potential source VM that contains the path added/set to the modified VM
        foreach (var vm in _allModSettingsInternal)
        {
            if (vm == modifiedVm) continue; // Don't compare to self

            bool pathMatches = false;
            if (pathType == PathType.ModFolder &&
                vm.CorrespondingFolderPaths.Contains(addedOrSetPath, StringComparer.OrdinalIgnoreCase))
            {
                pathMatches = true;
            }
            else if (pathType == PathType.MugshotFolder &&
                     vm.MugShotFolderPaths.Contains(addedOrSetPath, StringComparer.OrdinalIgnoreCase))
            {
                pathMatches = true;
            }

            if (pathMatches)
            {
                sourceVm = vm;
                break; // Found a potential source containing the path
            }
        }

        if (sourceVm == null) return; // No other VM contains this specific path

        // Now check if the merge conditions based on initial states are met
        bool mergeConditionsMet = false;
        VM_ModSetting winner = modifiedVm; // Assume the modified VM is the winner initially
        VM_ModSetting loser = sourceVm;

        if (pathType == PathType.ModFolder)
        {
            // User added a Mod Folder path to 'modifiedVm'
            // Conditions:
            // 1. 'modifiedVm' previously ONLY had mugshots (hadMugshotPathBefore=true, hadModPathsBefore=false)
            // 2. 'sourceVm' previously ONLY had this specific mod path and NO mugshots
            bool sourceHadOnlyThisModPath = sourceVm.CorrespondingFolderPaths.Count == 1 &&
                                            sourceVm.CorrespondingFolderPaths.Contains(addedOrSetPath,
                                                StringComparer.OrdinalIgnoreCase);
            bool sourceHadNoMugshots = !sourceVm.MugShotFolderPaths.Any();

            if (hadMugshotPathBefore && !hadModPathsBefore && sourceHadOnlyThisModPath && sourceHadNoMugshots)
            {
                mergeConditionsMet = true;
                // Winner = modifiedVm, Loser = sourceVm (Correctly initialized)
            }
        }
        else // pathType == PathType.MugshotFolder
        {
            // User set the Mugshot Folder path on 'modifiedVm'
            // Conditions:
            // 1. 'modifiedVm' previously ONLY had mod paths (hadMugshotPathBefore=false, hadModPathsBefore=true)
            // 2. 'sourceVm' previously had this specific mugshot path and NO mod paths
            bool sourceHadOnlyThisMugshot =
                sourceVm.MugShotFolderPaths.Contains(addedOrSetPath, StringComparer.OrdinalIgnoreCase) &&
                !sourceVm.HasModPathsAssigned; // Check current state is okay
            bool sourceHadNoModPaths = !sourceVm.HasModPathsAssigned; // Redundant check, but clear

            if (!hadMugshotPathBefore && hadModPathsBefore && sourceHadOnlyThisMugshot)
            {
                mergeConditionsMet = true;
                // Winner = modifiedVm, Loser = sourceVm (Correctly initialized)
            }
        }


        // Perform the merge if conditions are met
        if (mergeConditionsMet)
        {
            Debug.WriteLine($"Merge Condition Met: Merging '{loser.DisplayName}' into '{winner.DisplayName}'");

            // --- Perform Merge Actions ---
            // 1. Transfer necessary data (loser -> winner)
            // Mugshot Path (only if winner doesn't have one)
            if (!winner.HasMugshotPathAssigned && loser.HasMugshotPathAssigned)
            {
                foreach (var path in loser.MugShotFolderPaths)
                {
                    winner.MugShotFolderPaths.Add(path);
                }
            }

            // Mod Folder Paths (add paths from loser not already in winner)
            foreach (var path in loser.CorrespondingFolderPaths)
            {
                if (!winner.CorrespondingFolderPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    winner.CorrespondingFolderPaths.Add(path);
                }
            }

            // Merge Corresponding Mod Keys (add keys from loser not already in winner)
            foreach (var key in loser.CorrespondingModKeys)
            {
                if (!winner.CorrespondingModKeys.Contains(key))
                {
                    winner.CorrespondingModKeys.Add(key);
                }
            }

            // Merge NpcPluginDisambiguation: Loser's choices might be relevant if winner didn't have them
            foreach (var disambiguationEntry in loser.NpcPluginDisambiguation)
            {
                if (!winner.NpcPluginDisambiguation.ContainsKey(disambiguationEntry.Key))
                {
                    // Only add if the plugin is now part of the winner's CorrespondingModKeys
                    if (winner.CorrespondingModKeys.Contains(disambiguationEntry.Value))
                    {
                        winner.NpcPluginDisambiguation[disambiguationEntry.Key] = disambiguationEntry.Value;
                    }
                }
            }

            // IsMugshotOnlyEntry should remain based on the WINNER's original status
            // Although, if a merge happens, it's unlikely the winner was mugshot-only. Let's set it to false.
            winner.IsMugshotOnlyEntry = false;

            // Refresh NPC lists for the winner as its sources may have changed/**/

            var faceGenCache = await CacheFaceGenPathsOnLoadAsync(new[] { winner }, null);

            var plugins = _pluginProvider.LoadPlugins(winner.CorrespondingModKeys,
                winner.CorrespondingFolderPaths.ToHashSet());
            Task.Run(() => winner.RefreshNpcLists(faceGenCache.allFaceGenLooseFiles, faceGenCache.allFaceGenBsaFiles, plugins, _settings.LocalizationLanguage));
            _pluginProvider.UnloadPlugins(winner.CorrespondingModKeys);

            // 2. Update NPC Selections (_model.SelectedAppearanceMods via _consistencyProvider)
            string loserName = loser.DisplayName;
            string winnerName = winner.DisplayName;
            var selectionsToUpdate = _settings.SelectedAppearanceMods
                .Where(kvp => kvp.Value.ModName.Equals(loserName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (selectionsToUpdate.Any())
            {
                Debug.WriteLine(
                    $"Updating {selectionsToUpdate.Count} NPC selections from '{loserName}' to '{winnerName}'.");
                foreach (var selection in selectionsToUpdate)
                {
                    var targetNpcKey = selection.Key;
                    var originalSourceNpcKey = selection.Value.NpcFormKey;

                    // Call SetSelectedMod with the new winner mod name, but the original source NPC key.
                    _consistencyProvider.SetSelectedMod(targetNpcKey, winnerName, originalSourceNpcKey);
                }
            }

            // 3. Update the stale data cache within the NPC view model's list
            foreach (var npcVM in _npcSelectionBar.AllNpcs)
            {
                var modToRemove = npcVM.AppearanceMods.FirstOrDefault(m => m == loser);
                if (modToRemove != null)
                {
                    npcVM.AppearanceMods.Remove(modToRemove);
                    if (!npcVM.AppearanceMods.Contains(winner))
                    {
                        npcVM.AppearanceMods.Add(winner);
                    }
                }
            }

            // 4. Remove Loser VM
            bool removed = _allModSettingsInternal.Remove(loser);
            Debug.WriteLine($"Removed loser VM '{loserName}': {removed}");

            // 5. Refresh UI
            ApplyFilters(); // Refreshes the Mods view
            _npcSelectionBar.RefreshCurrentNpcAppearanceSources();
        }
    }

    public async Task RefreshAllModSettingsAsync(VM_SplashScreen? splashReporter)
    {
        bool createdSplashReporter = false;
        if (splashReporter == null)
        {
            splashReporter = VM_SplashScreen.InitializeAndShow(App.ProgramVersion, keepTopMost: false);
            createdSplashReporter = true;
        }

        try
        {
            splashReporter.UpdateStep("Backing up current settings...");
            await Task.Delay(100); // give UI time to update

            // a) Backup selections from the consistency provider
            var selectionBackup =
                new Dictionary<FormKey, (string ModName, FormKey NpcFormKey)>(_settings.SelectedAppearanceMods);

            // b) Backup specific mod settings
            var settingsBackup = _allModSettingsInternal.ToDictionary(
                vm => vm.DisplayName,
                vm => new ModSettingsBackup(
                    new List<string>(vm.MugShotFolderPaths),
                    vm.MergeInDependencyRecords,
                    vm.HasAlteredMergeLogic,
                    vm.IncludeOutfits,
                    vm.HandleInjectedRecords,
                    vm.HasAlteredHandleInjectedRecordsLogic,
                    vm.OverrideRecordOverrideHandlingMode
                )
            );
            
            if (ShouldRescanNonAppearanceMods)
            {
                splashReporter.UpdateStep("Clearing non-appearance mod cache...");
                await Task.Delay(100);
                _settings.CachedNonAppearanceMods.Clear();
                ShouldRescanNonAppearanceMods = false; // Reset after use
            }

            splashReporter.UpdateStep("Clearing existing mod data...");
            await Task.Delay(100);

            // c) Clear internal lists to generate a blank slate
            _consistencyProvider.ClearAllSelections();
            _allModSettingsInternal.Clear();
            ModSettingsList.Clear();
            SelectedModForMugshots = null;
            CurrentModNpcMugshots.Clear();
            _settings.ModSettings.Clear(); // Clear from the persistent model

            // d) Repopulate all mods from scratch
            await PopulateModSettingsAsync(splashReporter);

            splashReporter.UpdateStep("Restoring user settings...");
            await Task.Delay(100);

            // Prepare to find and remove redundant mugshot-only entries
            var redundantMugshotOnlyVmsToRemove = new HashSet<VM_ModSetting>();
            var mugshotOnlyVmLookup = _allModSettingsInternal
                .Where(vm => vm.IsMugshotOnlyEntry)
                .ToDictionary(vm => vm.DisplayName, StringComparer.OrdinalIgnoreCase);

            // e) Restore settings for each mod that still exists
            foreach (var vm in _allModSettingsInternal)
            {
                if (settingsBackup.TryGetValue(vm.DisplayName, out var backup))
                {
                    try
                    {
                        // Suppress confirmation pop-ups during restoration
                        vm.IsPerformingBatchAction = true;

                        // Restore mugshot folders and check for redundancy
                        foreach (var path in backup.MugShotFolderPaths)
                        {
                            if (!vm.MugShotFolderPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                            {
                                vm.MugShotFolderPaths.Add(path);
                                string folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,
                                    Path.AltDirectorySeparatorChar));
                                if (!string.IsNullOrEmpty(folderName) &&
                                    mugshotOnlyVmLookup.TryGetValue(folderName, out var redundantVm))
                                {
                                    redundantMugshotOnlyVmsToRemove.Add(redundantVm);
                                }
                            }
                        }

                        // Restore settings
                        vm.OverrideRecordOverrideHandlingMode = backup.OverrideRecordOverrideHandlingMode;
                        vm.IncludeOutfits = backup.IncludeOutfits;

                        // Only restore merge/injected settings if they were manually altered by the user before
                        if (backup.HasAlteredMergeLogic)
                        {
                            vm.MergeInDependencyRecords = backup.MergeInDependencyRecords;
                            vm.HasAlteredMergeLogic = true;
                        }

                        if (backup.HasAlteredHandleInjectedRecordsLogic)
                        {
                            vm.HandleInjectedRecords = backup.HandleInjectedRecords;
                            vm.HasAlteredHandleInjectedRecordsLogic = true;
                        }
                    }
                    finally
                    {
                        // Ensure the flag is always reset
                        vm.IsPerformingBatchAction = false;
                    }
                }
            }

            // Remove the identified redundant VMs from the master list
            if (redundantMugshotOnlyVmsToRemove.Any())
            {
                _allModSettingsInternal.RemoveAll(redundantMugshotOnlyVmsToRemove.Contains);
                ApplyFilters(); // Refresh the UI list to reflect the removals
            }

            splashReporter.UpdateStep("Restoring NPC selections...");
            await Task.Delay(100);

            // Restore the backed-up NPC appearance selections
            _consistencyProvider.RestoreSelections(selectionBackup);

            // Rebuild the main NPC list based on the newly refreshed mod data
            splashReporter.UpdateStep("Rebuilding NPC list...");
            await _npcSelectionBar.InitializeAsync(splashReporter);

            splashReporter.UpdateStep("Refresh complete.");
            await Task.Delay(500); // let user see the final message
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"An unexpected error occurred during the refresh process:\n\n{ExceptionLogger.GetExceptionStack(ex)}");
        }
        finally
        {
            if (createdSplashReporter)
            {
                await splashReporter.CloseSplashScreenAsync();
            }
        }
    }

    public void SignalScrollToMod(VM_ModSetting? modSetting)
    {
        if (modSetting != null)
        {
            Debug.WriteLine($"VM_Mods: Explicit signal to scroll to ModSetting {modSetting.DisplayName}");
            _requestScrollToModSubject.OnNext(modSetting);
        }
        else
        {
            _requestScrollToModSubject.OnNext(null);
        }
    }

    /// <summary>
    /// Called by VM_ModSetting when a single NPC's source plugin has changed.
    /// This might trigger a refresh of the mugshots if the display depends on the chosen source.
    /// </summary>
    public void NotifyNpcSourceChanged(VM_ModSetting modSetting, FormKey npcKey)
    {
        Debug.WriteLine(
            $"VM_Mods: Notified that source for NPC {npcKey} changed in ModSetting '{modSetting.DisplayName}'.");
        // If the affected modSetting is the one currently displayed for mugshots,
        // and the mugshot display logic considers the *chosen* source, then refresh.
        if (SelectedModForMugshots == modSetting)
        {
            // Reload mugshots for the selected mod
            ShowMugshotsCommand.Execute(modSetting).Subscribe().DisposeWith(_disposables);
        }
    }

    /// <summary>
    /// Called by VM_ModSetting when multiple NPC source plugins might have changed (e.g., by global set).
    /// </summary>
    public void NotifyMultipleNpcSourcesChanged(VM_ModSetting modSetting)
    {
        Debug.WriteLine(
            $"VM_Mods: Notified that multiple NPC sources may have changed in ModSetting '{modSetting.DisplayName}'.");
        if (SelectedModForMugshots == modSetting)
        {
            ShowMugshotsCommand.Execute(modSetting).Subscribe().DisposeWith(_disposables);
        }
    }

    // For passing plugin provider to sub-view-models (seems faster than doing it via AutoFac)
    public PluginProvider GetPluginProvider()
    {
        return _pluginProvider;
    }

    /// <summary>
    /// Asynchronously checks if any of the given mods in a folder modify NPC appearance.
    /// </summary>
    private async Task<bool> ContainsAppearancePluginsAsync(IEnumerable<ModKey> modKeysInMod,
        HashSet<string> modFolderPaths)
    {
        foreach (var modKey in modKeysInMod)
        {
            if (_environmentStateProvider.BaseGamePlugins.Contains(modKey))
            {
                return true;
            }

            // TryGetPlugin is likely a fast, synchronous operation.
            if (_pluginProvider.TryGetPlugin(modKey, modFolderPaths, out var plugin) && plugin != null)
            {
                bool pluginProvidesNewNpcs = false;
                using (ContextualPerformanceTracer.Trace("PopulateMods.PluginProvidesNewNpcs"))
                {
                    if (await PluginProvidesNewNpcs(plugin))
                    {
                        return true;
                    }
                }

                using (ContextualPerformanceTracer.Trace("PopulateMods.PluginModifiesAppearanceAsync"))
                {
                    if (await PluginModifiesAppearanceAsync(plugin, modKeysInMod))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private Task<bool> PluginProvidesNewNpcs(ISkyrimModGetter mod)
    {
        // Use Task.Run to execute the synchronous, CPU-bound logic on a thread pool thread.
        return Task.Run(() =>
        {
            foreach (var npc in mod.Npcs)
            {
                if (npc.FormKey.ModKey.Equals(mod.ModKey))
                {
                    return true; // not an overridden NPC
                }
            }

            return false;
        });
    }

    /// <summary>
    /// Offloads the CPU-intensive work of checking for NPC appearance modifications to a background thread.
    /// </summary>
    private Task<bool> PluginModifiesAppearanceAsync(ISkyrimModGetter mod, IEnumerable<ModKey> allKeysInCurrentMod)
    {
        // Use Task.Run to execute the synchronous, CPU-bound logic on a thread pool thread.
        return Task.Run(() =>
        {
            var candidateMasters = mod.ModHeader.MasterReferences.Select(x => x.Master)
                .Where(x => !allKeysInCurrentMod.Contains(x))
                .ToHashSet();

            var enabledMasters = _environmentStateProvider.LoadOrder.PriorityOrder
                .Where(x => candidateMasters.Contains(x.ModKey))
                .ToList();

            var tempLinkCache = mod.ToImmutableLinkCache();
            foreach (var npc in mod.Npcs)
            {
                if (npc.FormKey.ModKey.Equals(mod.ModKey))
                {
                    continue; // not an overridden NPC
                }

                if (!tempLinkCache.TryResolve<INpcGetter>(npc.FormKey, out var npcGetter))
                {
                    continue;
                }

                foreach (var listing in enabledMasters)
                {
                    var baseNpcGetter = listing.Mod?.Npcs.FirstOrDefault(x => x.FormKey.Equals(npc.FormKey));
                    if (baseNpcGetter != null)
                    {
                        // A series of comparisons to check for appearance changes.
                        if ((npcGetter.FaceMorph != null && baseNpcGetter.FaceMorph == null) ||
                            (npcGetter.FaceMorph != null && !npcGetter.FaceMorph.Equals(baseNpcGetter.FaceMorph)) ||
                            (npcGetter.FaceParts != null && baseNpcGetter.FaceParts == null) ||
                            (npcGetter.FaceParts != null && !npcGetter.FaceParts.Equals(baseNpcGetter.FaceParts)) ||
                            !npcGetter.Height.Equals(baseNpcGetter.Height) ||
                            !npcGetter.Weight.Equals(baseNpcGetter.Weight) ||
                            !npcGetter.TextureLighting.Equals(baseNpcGetter.TextureLighting) ||
                            !npcGetter.HeadTexture.Equals(baseNpcGetter.HeadTexture) ||
                            !npcGetter.WornArmor.Equals(baseNpcGetter.WornArmor) ||
                            !npcGetter.HeadParts.Count.Equals(baseNpcGetter.HeadParts.Count) ||
                            !npcGetter.TintLayers.Count.Equals(baseNpcGetter.TintLayers.Count) ||
                            !npcGetter.HairColor.Equals(baseNpcGetter.HairColor)
                           )
                        {
                            return true;
                        }

                        foreach (var headPart in npcGetter.HeadParts)
                        {
                            if (!baseNpcGetter.HeadParts.Contains(headPart))
                            {
                                return true;
                            }
                        }

                        foreach (var tintLayer in npcGetter.TintLayers)
                        {
                            if (!baseNpcGetter.TintLayers.Contains(tintLayer))
                            {
                                return true;
                            }
                        }

                        break; // Analyzed highest priority mod containing this NPC; no need to look further
                    }
                }
            }

            return false;
        });
    }

    public ConcurrentDictionary<(string pluginSourcePath, ModKey modKey), bool> GetOverrideCache()
    {
        return _overridesCache;
    }

    public bool UpdateTemplates(FormKey npcFormKey, VM_ModSetting modSettingVM)
    {
        int maxCycleCount = 50; // this should be way overkill
        List<(FormKey formKey, string displayName)> templateChain = new();
        List<string> errorMessages = new();

        Dictionary<ModKey, ISkyrimModGetter> plugins = new();
        foreach (var modKey in modSettingVM.CorrespondingModKeys)
        {
            if (_pluginProvider.TryGetPlugin(modKey, modSettingVM.CorrespondingFolderPaths.ToHashSet(),
                    out var plugin) && plugin != null)
            {
                plugins.Add(modKey, plugin);
            }
        }

        int cycleCount = 0;
        ISkyrimModGetter? sourcePlugin = null;
        INpcGetter? currentNpcGetter = null;
        List<FormKey> fromLinkCacheOnly = new(); // don't try to set the appearance mod for these NPCs
        while (cycleCount < maxCycleCount)
        {
            var availablePlugins = modSettingVM.AvailablePluginsForNpcs.TryGetValue(npcFormKey);
            // note: availablePlugins might be null if the given template doesn't come with FaceGen, causing the modSetting to reject it as an appearance mod.
            // Fall back to the link cache in this case
            if (availablePlugins != null && availablePlugins.Any() || (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey, out currentNpcGetter) && currentNpcGetter != null))
            {
                if (availablePlugins != null && availablePlugins.Count == 1)
                {
                    if (plugins.TryGetValue(availablePlugins.First(), out var plugin))
                    {
                        sourcePlugin = plugin;
                    }
                    else
                    {
                        errorMessages.Add(
                            $"Could not find plugin {availablePlugins.First()} for {npcFormKey} within {modSettingVM.DisplayName}.");
                        break;
                    }
                }
                else if (modSettingVM.NpcPluginDisambiguation.TryGetValue(npcFormKey, out var disambiguation))
                {
                    if (plugins.TryGetValue(disambiguation, out var plugin))
                    {
                        sourcePlugin = plugin;
                    }
                    else
                    {
                        errorMessages.Add(
                            $"Could not find plugin {disambiguation} for {npcFormKey} within {modSettingVM.DisplayName}.");
                        break;
                    }
                }
                else
                {
                    errorMessages.Add(
                        $"Could not determine source plugin for {npcFormKey} within plugin {modSettingVM.DisplayName}: [{string.Join(", ", availablePlugins)}])");
                    break;
                }
            }
            else if (_environmentStateProvider.LinkCache.TryResolve<ILeveledNpcGetter>(npcFormKey,
                         out var leveledNpcGetter))
            {
                var newEntry = (leveledNpcGetter.FormKey, Auxilliary.GetLogString(leveledNpcGetter, _settings.LocalizationLanguage, true));
                templateChain.Add(newEntry);
                
                ScrollableMessageBox.ShowWarning("This NPC appearance uses a template whose template chain ends with a Leveled NPC. Therefore, you cannot select a unique appearance for it." 
                                                 + Environment.NewLine + $"Template Chain: {string.Join(" -> ", templateChain.Select(x => x.displayName))}");
                return false;
            }
            else if (currentNpcGetter != null)
            {
                fromLinkCacheOnly.Add(currentNpcGetter.FormKey);
            }
            else
            {
                 errorMessages.Add(
                    $"Could not find any available plugins for {npcFormKey} within {modSettingVM.DisplayName}");
                break;
            }

            if (sourcePlugin != null || currentNpcGetter != null)
            {
                if (sourcePlugin != null)
                {
                    currentNpcGetter =  sourcePlugin.Npcs.Where(x => x.FormKey.Equals(npcFormKey)).FirstOrDefault();   
                }
                
                if (currentNpcGetter is null)
                {
                    errorMessages.Add(
                        $"Could not find {npcFormKey} in {sourcePlugin.ModKey.FileName} even though analysis indicates it should be there");
                    break;
                }

                var newEntry = (currentNpcGetter.FormKey, Auxilliary.GetLogString(currentNpcGetter, _settings.LocalizationLanguage, true));
                templateChain.Add(newEntry);

                if (currentNpcGetter.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits))
                {
                    if (currentNpcGetter.Template is null || currentNpcGetter.Template.IsNull)
                    {
                        errorMessages.Add(
                            $"The appearance template for {Auxilliary.GetLogString(currentNpcGetter, _settings.LocalizationLanguage)} in {sourcePlugin.ModKey.FileName} is blank despite it having a Traits template flag");
                        break;
                    }
                    else
                    {
                        npcFormKey = currentNpcGetter.Template.FormKey; // repeat for the next template
                    }
                }
                else
                {
                    break; // template chain stops here
                }
            }
        }

        if (templateChain.Any())
        {
            StringBuilder message = new();
            message.AppendLine(
                "This NPC inherits appearance from a template, which means that it needs to come from the same mod as the template.");
            message.AppendLine($"Template Chain: {string.Join(" -> ", templateChain.Select(x => x.displayName))}");
            message.AppendLine();
            if (errorMessages.Any())
            {
                message.AppendLine("Note: the following error(s) occured when analyzing the template chain:");
                message.AppendLine(string.Join(Environment.NewLine, errorMessages));
            }

            message.AppendLine();
            message.AppendLine("Would you like to apply this mod selection for all NPCs in the chain?");

            if (ScrollableMessageBox.Confirm(message.ToString(), "Update template chain?"))
            {
                int index = 0;
                foreach (var entry in templateChain)
                {
                    index++;
                    if (index == 1)
                    {
                        continue;
                    } // the current mugshot has already been set by the caller

                    if (fromLinkCacheOnly.Contains(entry.formKey))
                    {
                        continue;
                    } // don't set the appearance for templates without FaceGen.

                    _consistencyProvider.SetSelectedMod(entry.formKey, modSettingVM.DisplayName, entry.formKey);
                }
            }
        }

        return true;
    }

    public CancellationToken GetCurrentMugshotLoadToken()
    {
        return _mugshotLoadingCts?.Token ?? CancellationToken.None;
    }

    public string GetStatusReport()
    {
        StringBuilder sb = new();
        sb.AppendLine("Installed Appearance Mods:");
        foreach (var mod in _allModSettingsInternal)
        {
            sb.AppendLine($"{mod.DisplayName}" + 
                          (mod.IsFaceGenOnlyEntry ? " (FaceGen-Only)" : string.Empty) + 
                          (mod.IsMugshotOnlyEntry ? " (Mugshots-Only)" : string.Empty));
            if (!mod.IsFaceGenOnlyEntry && !mod.IsMugshotOnlyEntry)
            {
                sb.AppendLine($"\t{mod.NpcFormKeys.Count} NPCs in plugin(s).");
            }

            sb.AppendLine($"\tMerge-in: {mod.MergeInDependencyRecords}");
            sb.AppendLine($"\tInjected Record Handling: {mod.HandleInjectedRecords}");
            sb.AppendLine($"\tInclude Outfits: {mod.IncludeOutfits}");
        }
        
        return sb.ToString();
    }
}
