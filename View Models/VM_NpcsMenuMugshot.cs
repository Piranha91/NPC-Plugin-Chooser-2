using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat;
using System.Linq;
using GongSolutions.Wpf.DragDrop;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SixLabors.ImageSharp;
using CharacterViewer.Rendering;

namespace NPC_Plugin_Chooser_2.View_Models;

[DebuggerDisplay("{ModName}")]
public class VM_NpcsMenuMugshot : ReactiveObject, IDisposable, IHasMugshotImage, IDragSource, IDropTarget
{
// --- Existing fields ---
    private readonly FormKey _targetNpcFormKey;
    private readonly Settings _settings;
    private readonly NpcConsistencyProvider _consistencyProvider;
    private readonly VM_NpcSelectionBar _vmNpcSelectionBar;
    private readonly Lazy<VM_Mods> _lazyMods;
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly FaceFinderClient _faceFinderClient;
    private readonly PortraitCreator _portraitCreator;
    private readonly InternalMugshotGenerator _internalMugshotGenerator;
    private readonly GeneratedMugshotTracker _tracker;
    private readonly FaceFinderCacheTracker _faceFinderTracker;
    private readonly MugshotStalenessChecker _stalenessChecker;
    private readonly BatchMugshotGenerator _batchGenerator;
    private readonly EventLogger _eventLogger;
    private readonly Func<VM_InternalMugshotPreview> _internalPreviewFactory;
    private readonly FaceGenAnalysisCache _faceGenAnalysisCache = null!;
    private readonly CompositeDisposable Disposables = new();
    private readonly SolidColorBrush _selectedWithDataBrush = new(Colors.LimeGreen);
    private readonly SolidColorBrush _selectedWithoutDataBrush = new(Colors.DarkMagenta);
    private readonly SolidColorBrush _deselectedWithDataBrush = new(Colors.Transparent);
    //private readonly SolidColorBrush _deselectedWithoutDataBrush = new(Colors.Coral); // Now handled with an overlay
    private bool _isImageLoadingOrLoaded = false;
    private readonly object _imageLoadLock = new();


    // --- Existing properties ---
    public ModKey? ModKey { get; }
    public string ModName { get; }
    public FormKey SourceNpcFormKey { get; } // The NPC that provides the appearance
    [Reactive] public string ImagePath { get; set; } = string.Empty;
    [Reactive] public double ImageWidth { get; set; } // Displayed width
    [Reactive] public double ImageHeight { get; set; } // Displayed height
    [Reactive] public bool IsSelected { get; set; }
    [Reactive] public SolidColorBrush BorderColor { get; set; } = new(Colors.Transparent);
    [Reactive] public bool HasMugshot { get; private set; }
    [Reactive] public bool HasNoData { get; private set; }
    [Reactive] public string NoDataNotificationText { get; set; } = string.Empty;
    [Reactive] public bool IsVisible { get; set; } = true;
    [Reactive] public bool IsSetHidden { get; set; } = false;
    [Reactive] public bool CanJumpToMod { get; set; } = false;
    public VM_ModSetting? AssociatedModSetting { get; }
    [Reactive] public string ToolTipString { get; set; } = string.Empty;
    [Reactive] public bool HasIssueNotification { get; set; } = false;
    [Reactive] public NpcIssueType IssueType { get; set; } = NpcIssueType.Template;
    [Reactive] public string IssueNotificationText { get; set; } = string.Empty;
    /// <summary>True when the most recent in-process mugshot render
    /// reported any unresolved mesh OR texture paths. Drives a single
    /// "missing asset" overlay; the per-kind detail is in
    /// <see cref="MissingAssetNotificationText"/>.</summary>
    [Reactive] public bool HasMissingAssets { get; set; } = false;
    [Reactive] public string MissingAssetNotificationText { get; set; } = string.Empty;
    [Reactive] public FormKey? TemplateNpcKey { get; set; }
    [Reactive] public bool CanJumpToTemplate { get; set; }
    public bool IsAmbiguousSource { get; }
    public ObservableCollection<ModKey> AvailableSourcePlugins { get; } = new();
    [Reactive] public ModKey? CurrentSourcePlugin { get; set; }
    public bool IsGuestAppearance { get; }
    public string TargetDisplayName { get; }
    public string OriginalTargetName { get; set; }
    [Reactive] public bool IsFavorite { get; set; }
    [Reactive] public bool IsShareSource { get; private set; }
    [Reactive] public bool IsSelectedByGuest { get; private set; }
    [Reactive] public string ShareSourceTooltipText { get; private set; } = string.Empty;
    
    public bool CanOpenModFolder => AssociatedModSetting != null && AssociatedModSetting.CorrespondingFolderPaths.Any();
    public bool CanOpenMugshotFolder => HasMugshot;
    public string MugshotFolderPath => HasMugshot && !string.IsNullOrEmpty(ImagePath) ? Path.GetDirectoryName(ImagePath) : string.Empty;
    public ObservableCollection<ModPageInfo> ModPageUrls { get; } = new();
    [ObservableAsProperty] public bool CanVisitModPage { get; }
    [ObservableAsProperty] public bool HasSingleModPage { get; }


    // --- NEW IHasMugshotImage properties ---
    public int OriginalPixelWidth { get; set; }
    public int OriginalPixelHeight { get; set; }
    public double OriginalDipWidth { get; set; }
    public double OriginalDipHeight { get; set; }
    public double OriginalDipDiagonal { get; set; }
    [Reactive] public ImageSource? MugshotSource { get; set; }

    // --- NEW Property for Compare Checkbox ---
    [Reactive] public bool IsCheckedForCompare { get; set; } = false;

    [Reactive] public bool IsLoading { get; private set; }

    // --- FaceGen Analysis (per-tile overlay) ---
    /// <summary>Raw stats populated by the analysis cache on first tile load.
    /// Null until analysis runs (or if it can't locate a NIF, e.g. an
    /// uninstalled mod). The outlier coordinator reads non-null entries to
    /// rank the visible tiles by metric.</summary>
    [Reactive] public NifMeshBuilder.FaceGenStats? FaceGenStats { get; set; }
    /// <summary>Composite visibility flag — true when analysis is enabled,
    /// stats arrived, and the selected display mode is TextOverlay AND at
    /// least one metric is enabled. Drives the XAML overlay's Visibility.</summary>
    [Reactive] public bool ShowFaceGenTextOverlay { get; set; }
    [Reactive] public bool ShowFaceGenIndicator { get; set; }
    [Reactive] public bool ShowFaceGenSizeLine { get; set; }
    [Reactive] public bool ShowFaceGenPolyLine { get; set; }
    [Reactive] public bool ShowFaceGenVertLine { get; set; }
    [Reactive] public string FaceGenSizeText { get; set; } = string.Empty;
    [Reactive] public string FaceGenPolyText { get; set; } = string.Empty;
    [Reactive] public string FaceGenVertText { get; set; } = string.Empty;
    [Reactive] public SolidColorBrush FaceGenSizeColor { get; set; } = new(Colors.White);
    [Reactive] public SolidColorBrush FaceGenPolyColor { get; set; } = new(Colors.White);
    [Reactive] public SolidColorBrush FaceGenVertColor { get; set; } = new(Colors.White);
    [Reactive] public SolidColorBrush FaceGenIndicatorColor { get; set; } = new(Colors.White);
    [Reactive] public string FaceGenStatsTooltip { get; set; } = string.Empty;
    [Reactive] public double FaceGenTextFontSize { get; set; } = 10.0;
    /// <summary>Drives the indicator dot's HorizontalAlignment + VerticalAlignment
    /// + Margin via Style DataTriggers in the XAML. Mirrors the persisted
    /// FaceGenTooltipPosition setting verbatim; default is CenterLeft.</summary>
    [Reactive] public FaceGenTooltipPosition FaceGenIndicatorPosition { get; set; } = FaceGenTooltipPosition.CenterLeft;
    [Reactive] public bool IsFaceGenSizeOutlier { get; set; }
    [Reactive] public bool IsFaceGenPolyOutlier { get; set; }
    [Reactive] public bool IsFaceGenVertOutlier { get; set; }

    // --- Existing Commands ---
    public ReactiveCommand<Unit, Unit> SelectCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
    public ReactiveCommand<Unit, Unit> Show3DPreviewCommand { get; }
    public ReactiveCommand<Unit, Unit> HideCommand { get; }
    public ReactiveCommand<Unit, Unit> UnhideCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectAllFromThisModCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectAvailableFromThisModCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectVisibleFromThisModCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectVisibleAndAvailableFromThisModCommand { get; }
    public ReactiveCommand<Unit, Unit> UnselectAllFromThisModCommand { get; } 
    public ReactiveCommand<Unit, Unit> UnselectVisibleFromThisModCommand { get; }
    public ReactiveCommand<Unit, Unit> HideAllFromThisModCommand { get; }
    public ReactiveCommand<Unit, Unit> UnhideAllFromThisModCommand { get; }
    public ReactiveCommand<Unit, Unit> JumpToModCommand { get; }
    public ReactiveCommand<Unit, Unit> JumpToTemplateCommand { get; }
    public ReactiveCommand<ModKey, Unit> SetNpcSourcePluginCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectSameSourcePluginWherePossibleCommand { get; }
    public ReactiveCommand<Unit, Unit> ShareWithNpcCommand { get; }
    public ReactiveCommand<Unit, Unit> UnshareFromNpcCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToFavoritesCommand { get; }
    public ReactiveCommand<string, Unit> OpenFolderCommand { get; }
    public ReactiveCommand<string, Unit> VisitModPageCommand { get; }



    // --- Placeholder Image Configuration --- 
    private const string PlaceholderResourceRelativePath = @"Resources\No Mugshot.png";

    private static readonly string FullPlaceholderPath =
        Path.Combine(AppContext.BaseDirectory, PlaceholderResourceRelativePath);

    private static readonly bool PlaceholderExists = File.Exists(FullPlaceholderPath);
    
    public record ModPageInfo(string DisplayName, string Url);

    public VM_NpcsMenuMugshot(
        string modName,
        string npcDisplayName,
        FormKey targetNpcFormKey,
        FormKey sourceNpcFormKey,
        ModKey? overrideModeKey,
        string? imagePath, // This is the path to the *actual* mugshot if one exists for this mod/NPC combo
        Settings settings,
        NpcConsistencyProvider consistencyProvider,
        VM_NpcSelectionBar vmNpcSelectionBar,
        Lazy<VM_Mods> lazyMods,
        EnvironmentStateProvider environmentStateProvider,
        FaceFinderClient faceFinderClient,
        PortraitCreator portraitCreator,
        InternalMugshotGenerator internalMugshotGenerator,
        MugshotStalenessChecker stalenessChecker,
        BatchMugshotGenerator batchGenerator,
        EventLogger eventLogger,
        Func<VM_InternalMugshotPreview> internalPreviewFactory,
        GeneratedMugshotTracker tracker,
        FaceFinderCacheTracker faceFinderTracker,
        FaceGenAnalysisCache faceGenAnalysisCache)
    {
        ModName = modName;
        _lazyMods = lazyMods;
        AssociatedModSetting = _lazyMods.Value?.AllModSettings.FirstOrDefault(m => m.DisplayName == modName);
        ModKey = overrideModeKey ?? AssociatedModSetting?.CorrespondingModKeys.FirstOrDefault();
        _targetNpcFormKey = targetNpcFormKey;
        SourceNpcFormKey = sourceNpcFormKey;
        IsGuestAppearance = !targetNpcFormKey.Equals(sourceNpcFormKey);
        TargetDisplayName = npcDisplayName;
        OriginalTargetName = npcDisplayName;
        _settings = settings;
        _consistencyProvider = consistencyProvider;
        _vmNpcSelectionBar = vmNpcSelectionBar;
        _environmentStateProvider = environmentStateProvider;
        _faceFinderClient = faceFinderClient;
        _portraitCreator = portraitCreator;
        _internalMugshotGenerator = internalMugshotGenerator;
        _stalenessChecker = stalenessChecker;
        _batchGenerator = batchGenerator;
        _eventLogger = eventLogger;
        _internalPreviewFactory = internalPreviewFactory;
        _tracker = tracker;
        _faceFinderTracker = faceFinderTracker;
        _faceGenAnalysisCache = faceGenAnalysisCache;

        // FaceGen analysis: derived display properties tied to settings + Stats.
        // Reactive subscriptions live in InitFaceGenAnalysis() so the full block
        // is together; called at the end of the constructor.
        InitFaceGenAnalysis();

        HasNoData = (AssociatedModSetting == null || (!AssociatedModSetting.CorrespondingFolderPaths.Any() &&
                                                      !AssociatedModSetting.IsAutoGenerated));
        IsFavorite = _settings.FavoriteFaces.Contains((this.SourceNpcFormKey, this.ModName));

        if (HasNoData)
        {
            NoDataNotificationText =
                $"You have Mugshots installed for {AssociatedModSetting?.DisplayName ?? "this mod"} but the mod itself is not installed. {Environment.NewLine}You can still select this as a placeholder, but {npcDisplayName} won't be included in the output until the actual mod is installed.";
        }

        // --- NEW Ambiguous Source Initialization ---
        IsAmbiguousSource = AssociatedModSetting?.AmbiguousNpcFormKeys.Contains(_targetNpcFormKey) ?? false;
        CurrentSourcePlugin = AssociatedModSetting?.NpcPluginDisambiguation.GetValueOrDefault(_targetNpcFormKey);

        if (IsAmbiguousSource && AssociatedModSetting != null &&
            AssociatedModSetting.AvailablePluginsForNpcs.TryGetValue(_targetNpcFormKey, out var available))
        {
            AvailableSourcePlugins = new ObservableCollection<ModKey>(available.OrderBy(k => k.FileName.String));
        }

        var canSetNpcSource = this.WhenAnyValue(x => x.IsAmbiguousSource).Select(isAmbiguous => isAmbiguous);
        SetNpcSourcePluginCommand = ReactiveCommand.Create<ModKey>(SetNpcSourcePluginInternal, canSetNpcSource)
            .DisposeWith(Disposables);

        SelectSameSourcePluginWherePossibleCommand = ReactiveCommand.Create(() =>
            {
                if (this.AssociatedModSetting != null && this.CurrentSourcePlugin.HasValue)
                {
                    this.AssociatedModSetting.SetAndNotifySourcePluginForAll(this.CurrentSourcePlugin.Value);
                }
            },
            this.WhenAnyValue(x => x.IsAmbiguousSource, x => x.CurrentSourcePlugin,
                (ambiguous, source) => ambiguous && source.HasValue)).DisposeWith(Disposables);

        SetNpcSourcePluginCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error setting NPC source plugin: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        SelectSameSourcePluginWherePossibleCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error setting NPC source plugin: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);

        // --- Image Path and HasMugshot Logic ---
        // --- REPLACED SECTION ---
        // Remove the entire block that sets ImagePath, creates BitmapImage, and sets dimensions.
        // It started with: "bool realMugshotExists = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);"
    
        ImagePath = imagePath ?? string.Empty; // Just store the path initially

        // Asynchronously load the initial image (placeholder or real) without blocking the constructor.
        // When AutoGeneration outranks DownloadedMugshots in the user's priority,
        // skip pre-loading the curated bitmap so the priority loop can decide
        // first (avoiding a curated-then-autogen flicker, or curated "winning by
        // inertia" if the AutoGen render fails).
        _ = LoadInitialImageAsync(placeholderOnly: ShouldDeferCuratedLoad());
        // --- END REPLACED SECTION ---

        this.WhenAnyValue(x => x.IsSelected)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isSelected => SetBorderAndTooltip(isSelected))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.ModPageUrls.Count)
            .Select(count => count > 0)
            .ToPropertyEx(this, x => x.CanVisitModPage)
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.ModPageUrls.Count)
            .Select(count => count == 1)
            .ToPropertyEx(this, x => x.HasSingleModPage)
            .DisposeWith(Disposables);
        
        if (AssociatedModSetting != null)
        {
            foreach (var modPath in AssociatedModSetting.CorrespondingFolderPaths)
            {
                var metaPath = Path.Combine(modPath, "meta.ini");
                if (File.Exists(metaPath))
                {
                    var (gameName, modId) = ParseMetaIni(metaPath);
                    if (!string.IsNullOrWhiteSpace(gameName) && !string.IsNullOrWhiteSpace(modId))
                    {
                        var url = $"https://www.nexusmods.com/{gameName}/mods/{modId}";
                        var folderName = Path.GetFileName(modPath.TrimEnd(Path.DirectorySeparatorChar));
                        ModPageUrls.Add(new ModPageInfo(folderName, url));
                    }
                }
            }
        }
        
        CanJumpToMod = _vmNpcSelectionBar.CanJumpToMod(modName);
        IsSelected = _consistencyProvider.IsModSelected(_targetNpcFormKey, ModName, SourceNpcFormKey);
        
        SelectCommand = ReactiveCommand.Create(SelectThisMod).DisposeWith(Disposables);
        // Gate on "is there an image we can show?" — broader than HasMugshot,
        // which excludes auto-generated mugshots even though the FullScreen
        // view loads them just fine. Accept either an in-memory source or a
        // path that exists on disk.
        var canShowFullImage = this.WhenAnyValue(x => x.ImagePath, x => x.MugshotSource,
            (path, src) => src != null
                || (!string.IsNullOrWhiteSpace(path) && File.Exists(path)));
        ToggleFullScreenCommand = ReactiveCommand.Create(() =>
        {
            // Prioritize the in-memory source if it exists, otherwise fall back to the path
            var fullScreenVM = MugshotSource != null
                ? new VM_FullScreenImage(MugshotSource)
                : new VM_FullScreenImage(ImagePath);

            var fullScreenView = Locator.Current.GetService<IViewFor<VM_FullScreenImage>>() as Window;
            if (fullScreenView != null)
            {
                fullScreenView.DataContext = fullScreenVM;
                fullScreenView.ShowDialog();
            }
        }, canShowFullImage).DisposeWith(Disposables);

        // 3D preview: any non-mugshot-only entry. Base Game ships with empty
        // CorrespondingFolderPaths (records + assets come from the vanilla
        // data folder and BSAs, which the renderer's vanilla scope already
        // covers). Per-mod-scoped — the popup resolves records + assets
        // against THIS tile's mod, not the user's active selection.
        var canShow3DPreview = Observable.Return(
            AssociatedModSetting != null
            && !AssociatedModSetting.IsMugshotOnlyEntry
            && !targetNpcFormKey.IsNull);
        Show3DPreviewCommand =
            ReactiveCommand.Create(Show3DPreview, canShow3DPreview).DisposeWith(Disposables);

        HideCommand = ReactiveCommand.Create(HideThisMod).DisposeWith(Disposables);
        UnhideCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.UnhideSelectedMod(this))
            .DisposeWith(Disposables);
        SelectAllFromThisModCommand = ReactiveCommand
            .CreateFromTask(() => _vmNpcSelectionBar.SelectAllFromMod(this, false))
            .DisposeWith(Disposables);
        SelectAvailableFromThisModCommand = ReactiveCommand
            .CreateFromTask(() => _vmNpcSelectionBar.SelectAllFromMod(this, true)).DisposeWith(Disposables);
        SelectVisibleFromThisModCommand = ReactiveCommand
            .CreateFromTask(() => _vmNpcSelectionBar.SelectVisibleFromMod(this, false)).DisposeWith(Disposables);
        SelectVisibleAndAvailableFromThisModCommand = ReactiveCommand
            .CreateFromTask(() => _vmNpcSelectionBar.SelectVisibleFromMod(this, true)).DisposeWith(Disposables);
        UnselectAllFromThisModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.UnselectAllFromMod(this))
            .DisposeWith(Disposables);
        UnselectVisibleFromThisModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.UnselectVisibleFromMod(this))
            .DisposeWith(Disposables);
        HideAllFromThisModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.HideAllFromMod(this))
            .DisposeWith(Disposables);
        UnhideAllFromThisModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.UnhideAllFromMod(this))
            .DisposeWith(Disposables);
        JumpToModCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.JumpToMod(this),
            this.WhenAnyValue(x => x.CanJumpToMod)).DisposeWith(Disposables);
        JumpToTemplateCommand = ReactiveCommand.Create(() => _vmNpcSelectionBar.JumpToTemplate(this),
            this.WhenAnyValue(x => x.CanJumpToTemplate)).DisposeWith(Disposables);

        // The command now sends a message containing itself.
        ShareWithNpcCommand = ReactiveCommand.Create(() =>
        {
            MessageBus.Current.SendMessage(new ShareAppearanceRequest(this));
        }).DisposeWith(Disposables);
        ShareWithNpcCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error sharing NPC appearance: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);

        UnshareFromNpcCommand = ReactiveCommand.Create(() =>
        {
            MessageBus.Current.SendMessage(new UnshareAppearanceRequest(this));
        }).DisposeWith(Disposables);
        UnshareFromNpcCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error un-sharing NPC appearance: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);

        AddToFavoritesCommand = ReactiveCommand.Create(ToggleFavorite).DisposeWith(Disposables);
        AddToFavoritesCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error updating favorites: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        
        OpenFolderCommand = ReactiveCommand.Create<string>(Auxilliary.OpenFolder).DisposeWith(Disposables);
        
        VisitModPageCommand = ReactiveCommand.Create<string>(Auxilliary.OpenUrl).DisposeWith(Disposables);
        
        SelectCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.Show($"Error selecting mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        ToggleFullScreenCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.Show($"Error showing image: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        HideCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.Show($"Error hiding mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        UnhideCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.Show($"Error unhiding mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        SelectAllFromThisModCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.Show($"Error selecting all from mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        SelectAvailableFromThisModCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.Show($"Error selecting available from mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        SelectVisibleFromThisModCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.Show($"Error selecting visible from mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        SelectVisibleAndAvailableFromThisModCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.Show($"Error selecting visible and available from mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        UnselectAllFromThisModCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.Show($"Error unselecting all from mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        UnselectVisibleFromThisModCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.Show($"Error unselecting visible from mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        HideAllFromThisModCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.Show($"Error hiding all from mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        UnhideAllFromThisModCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.Show($"Error unhiding all from mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        JumpToModCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.Show($"Error jumping to mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        JumpToTemplateCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error jumping to template: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        OpenFolderCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error opening folder: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        VisitModPageCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Could not open URL: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);


        _consistencyProvider.NpcSelectionChanged
            .Where(args => args.NpcFormKey == _targetNpcFormKey)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(args =>
                IsSelected = (args.SelectedModName == ModName && args.SourceNpcFormKey.Equals(this.SourceNpcFormKey)))
            .DisposeWith(Disposables);

        SetBorderAndTooltip(IsSelected);

        InitializeShareSourceListener();
    }

    /// <summary>True when AutoGeneration appears before DownloadedMugshots in
    /// <see cref="Settings.MugshotSourcePriority"/>. Drives the deferral of
    /// the curated-image load in <see cref="LoadInitialImageAsync"/> so the
    /// priority loop can render AutoGen first; the Downloaded branch then
    /// actively loads curated only if AutoGen (and FaceFinder, if also ahead)
    /// produce nothing.</summary>
    private bool ShouldDeferCuratedLoad()
    {
        var priority = _settings.MugshotSourcePriority;
        if (priority == null) return false;
        int autoGenIdx = priority.IndexOf(MugshotSourceType.AutoGeneration);
        int downloadedIdx = priority.IndexOf(MugshotSourceType.DownloadedMugshots);
        // Only defer when AutoGen explicitly precedes Downloaded. If either
        // is missing from the list (shouldn't happen post-LoadSettings backfill
        // but defensive), keep the legacy "load curated up front" behavior.
        return autoGenIdx >= 0 && downloadedIdx >= 0 && autoGenIdx < downloadedIdx;
    }

    /// <summary>Deferred-mode entry into the Downloaded source. Loads the
    /// curated mugshot bitmap synchronously (the bitmap I/O is small) from
    /// the constructor-supplied <see cref="ImagePath"/> if it points to a
    /// real, non-placeholder, non-auto-generated file. Returns true if loaded;
    /// false if no curated mugshot is available (priority loop falls through).
    /// Bypasses LoadInitialImageAsync's re-entry gate since that already fired
    /// in placeholder-only mode and won't re-run.</summary>
    private bool TryLoadCuratedMugshot()
    {
        if (string.IsNullOrWhiteSpace(ImagePath)
            || !File.Exists(ImagePath)
            || string.Equals(ImagePath, FullPlaceholderPath, StringComparison.OrdinalIgnoreCase)
            || _portraitCreator.IsAutoGenerated(ImagePath))
        {
            return false;
        }

        SetImageSource(ImagePath);
        HasMugshot = true;
        return true;
    }

    /// <summary>Initial image load called from the constructor and (awaited)
    /// from <see cref="GenerateMugshotAsync"/>. By default loads the curated
    /// mugshot from <see cref="ImagePath"/> if one exists, otherwise the
    /// placeholder. When <paramref name="placeholderOnly"/> is true, skips
    /// the curated load entirely and shows only the placeholder — used when
    /// AutoGeneration outranks DownloadedMugshots in
    /// <see cref="Settings.MugshotSourcePriority"/>, so the curated doesn't
    /// flicker into view (and then "win by inertia" if the AutoGen render
    /// fails) before the priority loop has had a chance to decide.</summary>
    public async Task LoadInitialImageAsync(bool placeholderOnly = false)
    {
        if (_isImageLoadingOrLoaded) return;

        lock (_imageLoadLock)
        {
            if (_isImageLoadingOrLoaded) return;
            _isImageLoadingOrLoaded = true;
        }

        string pathToLoad;
        bool realMugshotExists = !placeholderOnly
                                 && !string.IsNullOrWhiteSpace(ImagePath)
                                 && File.Exists(ImagePath);
        // An image is only considered a "real" mugshot if it exists on disk AND was not auto-generated.
        // Auto-generated images are treated as placeholders that need staleness checks.
        HasMugshot = realMugshotExists && !_portraitCreator.IsAutoGenerated(ImagePath);

        if (realMugshotExists)
        {
            pathToLoad = ImagePath;
        }
        else if (PlaceholderExists)
        {
            pathToLoad = FullPlaceholderPath;
            _eventLogger.Log($"Loading placeholder for {ModName}", "IMAGE_LOAD");
        }
        else
        {
            _eventLogger.Log($"No mugshot or placeholder found for {ModName}", "IMAGE_LOAD_WARNING");
            return; // No image to load
        }


        // In placeholderOnly mode the curated ImagePath must stay intact so
        // the priority loop's Downloaded branch can still find and load it
        // if AutoGen falls through. Only overwrite ImagePath when this call
        // is actually loading the real / curated mugshot.
        if (!placeholderOnly || realMugshotExists)
        {
            ImagePath = pathToLoad;
        }

        // Read the bitmap and (for auto-generated Internal-renderer PNGs) the
        // stamped missing-asset arrays on a background thread so the UI thread
        // doesn't block. The metadata read is decoupled from the bitmap result
        // so a malformed / older PNG still loads its image even if the JSON
        // parse fails.
        bool tryReadAssetMeta = realMugshotExists && _portraitCreator.IsAutoGenerated(pathToLoad);
        var loadResult = await Task.Run(() =>
        {
            BitmapImage? bitmap = null;
            try
            {
                bitmap = new BitmapImage();
                using var stream = new FileStream(pathToLoad, FileMode.Open, FileAccess.Read, FileShare.Read);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze(); // This is crucial for making it thread-safe
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading initial image '{pathToLoad}': {ExceptionLogger.GetExceptionStack(ex)}");
                _eventLogger.Log($"Error loading image '{pathToLoad}': {ex.Message}", "IMAGE_LOAD_ERROR");
                bitmap = null;
            }

            List<string> meshes = new();
            List<string> textures = new();
            if (tryReadAssetMeta)
            {
                var json = MugshotPngMetadata.TryRead(pathToLoad);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    InternalMugshotMetadata.TryReadMissingAssets(json, out meshes, out textures);
                }
            }

            // FaceGen analysis lives inside this Task.Run so it shares the
            // worker thread with the bitmap+metadata load — one thread-pool
            // ticket per tile instead of two. The cache handles SHA-keyed
            // dedup so this is a no-op on cache hits (~sub-ms).
            NifMeshBuilder.FaceGenStats? facegen = FetchFaceGenStatsSync();

            return (bitmap, meshes, textures, facegen);
        });

        // Always apply (even with empty lists) so a re-load of a tile whose
        // PNG was regenerated without missing assets clears any stale
        // overlay state from the in-memory VM.
        if (tryReadAssetMeta)
        {
            ApplyMissingAssetNotifications(loadResult.meshes, loadResult.textures);
        }

        // FaceGen stats (if any) — set after the await so it lands on the
        // UI thread, triggering the reactive overlay-state refresh.
        if (loadResult.facegen.HasValue)
        {
            FaceGenStats = loadResult.facegen;
        }

        // This assignment happens back on the UI thread after the await.
        var loadedBitmap = loadResult.bitmap;
        if (loadedBitmap != null)
        {
            this.MugshotSource = loadedBitmap;

            // Set original dimensions after loading
            var (pixelWidth, pixelHeight, dipWidth, dipHeight) = ImagePacker.GetImageDimensions(pathToLoad);
            OriginalPixelWidth = pixelWidth;
            OriginalPixelHeight = pixelHeight;
            OriginalDipWidth = dipWidth;
            OriginalDipHeight = dipHeight;
            OriginalDipDiagonal = Math.Sqrt(dipWidth * dipWidth + dipHeight * dipHeight);

            ImageWidth = OriginalDipWidth;
            ImageHeight = OriginalDipHeight;
            
            if (realMugshotExists)
            {
                _eventLogger.Log($"Successfully loaded real mugshot for {ModName} from {pathToLoad}", "IMAGE_LOAD");
            }
        }
    }

    /// <summary>Reactive wiring for the FaceGen-analysis overlay. The
    /// settings model is a plain POCO (no INPC), so cross-cutting setting
    /// changes are pushed by VM_NpcSelectionBar via
    /// <see cref="RefreshFaceGenOverlayState"/>. This method only wires the
    /// per-tile reactive pieces: image-zoom font scaling and stats-arrival
    /// refresh.</summary>
    private void InitFaceGenAnalysis()
    {
        if (_settings == null) return;

        // Font size scales with mugshot height so the overlay reads the
        // same at any zoom level. Clamped to a 7pt floor so text stays
        // legible at thumbnail zoom.
        this.WhenAnyValue(x => x.ImageHeight)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(h => FaceGenTextFontSize = Math.Max(7.0, h * (_settings.FaceGenTextHeightPercent / 100.0)))
            .DisposeWith(Disposables);

        // Stats-arrival → overlay refresh. Cross-tile settings changes are
        // pushed in by VM_NpcSelectionBar, which already iterates the
        // visible tiles for outlier recomputation.
        this.WhenAnyValue(x => x.FaceGenStats)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshFaceGenOverlayState())
            .DisposeWith(Disposables);

        FaceGenIndicatorPosition = _settings.FaceGenTooltipPosition;
    }

    /// <summary>Computes per-line visibility + text + tooltip from current
    /// settings and <see cref="FaceGenStats"/>. Color comes from the outlier
    /// flags via <see cref="ApplyFaceGenOutlierColors"/>, which the
    /// VM_NpcSelectionBar coordinator calls after recomputing the ranks.
    /// Public so the coordinator can push cross-cutting settings changes
    /// (display mode, enabled metrics, indicator position, text size %)
    /// without the tile having to subscribe to the POCO settings object.</summary>
    public void RefreshFaceGenOverlayState()
    {
        bool enabled = _settings.EnableFaceGenAnalysis;
        bool textMode = _settings.FaceGenDisplayMode == FaceGenAnalysisDisplayMode.TextOverlay;
        bool anyMetric = _settings.ReportFaceGenSize || _settings.ReportFaceGenPolys || _settings.ReportFaceGenVerts;
        bool haveStats = FaceGenStats != null;

        ShowFaceGenTextOverlay = enabled && textMode && anyMetric && haveStats;
        ShowFaceGenIndicator = enabled && !textMode && anyMetric && haveStats;
        ShowFaceGenSizeLine = _settings.ReportFaceGenSize && haveStats;
        ShowFaceGenPolyLine = _settings.ReportFaceGenPolys && haveStats;
        ShowFaceGenVertLine = _settings.ReportFaceGenVerts && haveStats;
        FaceGenIndicatorPosition = _settings.FaceGenTooltipPosition;
        FaceGenTextFontSize = Math.Max(7.0, ImageHeight * (_settings.FaceGenTextHeightPercent / 100.0));

        if (FaceGenStats is { } s)
        {
            FaceGenSizeText = $"Size: {FormatFileSize(s.FileSizeBytes)}";
            FaceGenPolyText = $"Faces: {s.TotalTriangles:N0}";
            FaceGenVertText = $"Verts: {s.TotalVertices:N0}";

            var tip = new StringBuilder();
            if (_settings.ReportFaceGenSize) tip.AppendLine(FaceGenSizeText);
            if (_settings.ReportFaceGenPolys) tip.AppendLine(FaceGenPolyText);
            if (_settings.ReportFaceGenVerts) tip.AppendLine(FaceGenVertText);
            FaceGenStatsTooltip = tip.ToString().TrimEnd();
        }
        else
        {
            FaceGenSizeText = FaceGenPolyText = FaceGenVertText = FaceGenStatsTooltip = string.Empty;
        }
    }

    /// <summary>Forces a fresh analysis attempt — used when the user toggles
    /// "Enable FaceGen Analysis" on for a tile whose first load happened
    /// while the toggle was off. The result lands on the UI thread via the
    /// reactive <see cref="FaceGenStats"/> property; the overlay refreshes
    /// itself off that.</summary>
    public void TriggerFaceGenAnalysisAsync()
    {
        if (!_settings.EnableFaceGenAnalysis) return;
        if (FaceGenStats.HasValue) return;
        _ = Task.Run(() =>
        {
            var stats = FetchFaceGenStatsSync();
            if (stats.HasValue)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    FaceGenStats = stats;
                });
            }
        });
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        return $"{mb:0.##} MB";
    }

    /// <summary>Called by the outlier coordinator on
    /// <see cref="VM_NpcSelectionBar"/> after it ranks the visible tiles.
    /// Per-line color + the composite indicator color follow from which
    /// metrics, if any, this tile is an outlier in.</summary>
    public void ApplyFaceGenOutlierColors(bool sizeOutlier, bool polyOutlier, bool vertOutlier,
        SolidColorBrush highlightBrush, SolidColorBrush normalBrush)
    {
        IsFaceGenSizeOutlier = sizeOutlier;
        IsFaceGenPolyOutlier = polyOutlier;
        IsFaceGenVertOutlier = vertOutlier;
        FaceGenSizeColor = sizeOutlier ? highlightBrush : normalBrush;
        FaceGenPolyColor = polyOutlier ? highlightBrush : normalBrush;
        FaceGenVertColor = vertOutlier ? highlightBrush : normalBrush;
        bool anyOutlier = sizeOutlier || polyOutlier || vertOutlier;
        FaceGenIndicatorColor = anyOutlier ? highlightBrush : normalBrush;
    }

    /// <summary>Background-thread analysis trigger fired from
    /// <see cref="LoadInitialImageAsync"/>. Skips when analysis is off, when
    /// stats already populated for this tile, or when geometry isn't needed
    /// AND neither poly / vert is enabled (size-only fast path). Returns
    /// the computed stats so the caller can marshal them onto the UI
    /// thread.</summary>
    private NifMeshBuilder.FaceGenStats? FetchFaceGenStatsSync()
    {
        if (!_settings.EnableFaceGenAnalysis) return null;
        if (AssociatedModSetting == null) return null;
        if (FaceGenStats.HasValue) return FaceGenStats; // already populated

        bool measureGeometry = _settings.ReportFaceGenPolys || _settings.ReportFaceGenVerts;
        try
        {
            return _faceGenAnalysisCache?.Get(AssociatedModSetting, SourceNpcFormKey, measureGeometry);
        }
        catch (Exception ex)
        {
            _eventLogger?.Log($"FaceGen analysis failed for {ModName} / {SourceNpcFormKey}: {ex.Message}", "FACEGEN_ANALYSIS_ERROR");
            return null;
        }
    }

    private (string? gameName, string? modId) ParseMetaIni(string filePath)
    {
        string? gameName = null;
        string? modId = null;
        try
        {
            var lines = File.ReadAllLines(filePath);
            foreach (var line in lines)
            {
                if (line.StartsWith("gameName=", StringComparison.OrdinalIgnoreCase))
                {
                    gameName = line.Split('=').Last().Trim();
                    // Add special case for SkyrimSE
                    if (gameName.Equals("SkyrimSE", StringComparison.OrdinalIgnoreCase))
                    {
                        gameName = "skyrimspecialedition";
                    }
                }
                else if (line.StartsWith("modid=", StringComparison.OrdinalIgnoreCase))
                {
                    modId = line.Split('=').Last().Trim();
                }
                if (gameName != null && modId != null) break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error parsing {filePath}: {ExceptionLogger.GetExceptionStack(ex)}");
        }
        return (gameName, modId);
    }

    /// <summary>
    /// Launches the per-tile 3D preview popup scoped to this tile's source
    /// mod (records + assets resolved against
    /// <see cref="AssociatedModSetting"/>'s plugins / folders rather than
    /// the user's currently-selected appearance mod). The popup hosts
    /// <see cref="UC_InternalMugshotPreview"/> in a fresh
    /// <see cref="VM_InternalMugshotPreview"/> instance — its own GL
    /// context, independent of the Settings-panel preview.
    /// </summary>
    private void Show3DPreview()
    {
        if (AssociatedModSetting == null) return;
        try
        {
            var inner = _internalPreviewFactory();
            var modSetting = AssociatedModSetting.SaveToModel();
            var title = $"3D Preview — {TargetDisplayName} ({ModName})";
            var fsVm = new VM_FullScreen3DPreview(inner, _settings, title);

            if (Locator.Current.GetService<IViewFor<VM_FullScreen3DPreview>>() is not Window window)
            {
                ScrollableMessageBox.ShowError("Could not create FullScreen3DPreviewView.");
                return;
            }
            window.DataContext = fsVm;
            // Fire LoadAsync on Loaded so the UC's GLWpfControl is ready by
            // the time the scene rebuild flushes. Fire-and-forget;
            // exceptions surface via inner.StatusText.
            window.Loaded += async (_, _) =>
            {
                try { await inner.LoadAsync(SourceNpcFormKey, modSetting); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Show3DPreview: LoadAsync failed: {ExceptionLogger.GetExceptionStack(ex)}");
                }
            };
            window.ShowDialog();
            inner.Dispose();
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError(
                $"Failed to open 3D preview:\n{ExceptionLogger.GetExceptionStack(ex)}");
        }
    }

    private void SelectThisMod()
    {
        if (IsSelected)
        {
            System.Diagnostics.Debug.WriteLine($"Deselecting mod '{ModName}' for NPC '{_targetNpcFormKey}'");
            _consistencyProvider.ClearSelectedMod(_targetNpcFormKey);
        }
        else
        {
            var previousSelection = _consistencyProvider.GetSelectedMod(_targetNpcFormKey);

            System.Diagnostics.Debug.WriteLine($"Selecting mod '{ModName}' for NPC '{_targetNpcFormKey}'");
            _consistencyProvider.SetSelectedMod(_targetNpcFormKey, ModName, SourceNpcFormKey);

            if (HasIssueNotification && IssueType == NpcIssueType.Template)
            {
                if (AssociatedModSetting != null && _lazyMods.IsValueCreated)
                {
                    if (!_lazyMods.Value.UpdateTemplates(_targetNpcFormKey, AssociatedModSetting))
                    {
                        if (previousSelection.ModName != null)
                        {
                            _consistencyProvider.SetSelectedMod(_targetNpcFormKey, previousSelection.ModName,
                                previousSelection.SourceNpcFormKey);
                        }
                        else
                        {
                            _consistencyProvider.ClearSelectedMod(_targetNpcFormKey);
                        }
                        return; // Selection was reverted, don't auto-advance
                    }
                }
                else // fall back to simple analzyer
                {
                    CheckAndHandleTemplates();
                }
            }

            // Auto-advance to next NPC after a brief delay
            if (_settings.AutoAdvanceAfterSelection)
            {
                Observable.Timer(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
                    .Subscribe(_ => _vmNpcSelectionBar.NavigateNextNpcCommand.Execute().Subscribe())
                    .DisposeWith(Disposables);
            }
        }
    }

    private void SetBorderAndTooltip(bool isSelected)
    {
        bool hasData = !HasNoData;

        if (isSelected && hasData)
        {
            BorderColor = _selectedWithDataBrush;
            ToolTipString = "Selected. Mugshot has associated Mod Data and is ready for patch generation.";
        }
        else if (isSelected && !hasData)
        {
            BorderColor = _selectedWithoutDataBrush;
            ToolTipString =
                "Selected but Mugshot has no associated Mod Data. Patcher run will skip this NPC until Mod Data is linked to this mugshot";
        }

        if (!isSelected && hasData)
        {
            BorderColor = _deselectedWithDataBrush;
            ToolTipString = "Not Selected. Mugshot has associated Mod Data and is ready to go if you select it.";
        }
        else if (!isSelected && !hasData)
        {
            //BorderColor = _deselectedWithoutDataBrush; // Now handled with an overlay
            BorderColor = _deselectedWithDataBrush;
            ToolTipString =
                "Not Selected. Mugshot has no associated Mod Data. If you select it, Patcher run will skip this NPC until Mod Data is linked to this mugshot";
        }
    }

    private void CheckAndHandleTemplates()
    {
        if (_targetNpcFormKey != null && ModKey != null)
        {
            string imagePath = @"Resources\Face Bug.png";

            var context = _environmentStateProvider.LinkCache
                .ResolveAllContexts<INpc, INpcGetter>(_targetNpcFormKey)
                .FirstOrDefault(x => x.ModKey.Equals(ModKey));

            if (context != null &&
                Auxilliary.IsValidTemplatedNpc(context.Record))
            {
                string message = String.Empty;
                string title = String.Empty;
                string templateDispName = String.Empty;
                if (context.Record.Template == null || context.Record.Template.IsNull)
                {
                    message =
                        "The associated data for this NPC shows that it is supposed to have a template, but there is no template set. This will probably result in a bugged appearance.";
                    title = "Are you sure?";
                    if (!ScrollableMessageBox.Confirm(message, title, displayImagePath: imagePath))
                    {
                        _consistencyProvider.ClearSelectedMod(_targetNpcFormKey);
                    }
                }
                else if (AssociatedModSetting != null)
                {
                    if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(context.Record.Template.FormKey,
                            out INpcGetter? templateGetter) && templateGetter.EditorID != null)
                    {
                        templateDispName = templateGetter.EditorID + " (" +
                                           context.Record.Template.FormKey.ToString() + ")";
                    }
                    else
                    {
                        templateDispName = context.Record.Template.FormKey.ToString();
                    }

                    if (!AssociatedModSetting.NpcFormKeys.Contains(context.Record.Template.FormKey))
                    {
                        message =
                            "The associated data for this NPC shows that it is supposed to use " + templateDispName +
                            " as its template, but " + (ModKey?.FileName ?? AssociatedModSetting.DisplayName) +
                            " doesn't appear to contain this NPC. This may result in a bugged appearance.";
                        title = "Are you sure?";
                        if (!ScrollableMessageBox.Confirm(message, title, displayImagePath: imagePath))
                        {
                            _consistencyProvider.ClearSelectedMod(_targetNpcFormKey);
                        }
                    }
                    else if (AssociatedModSetting.NpcFormKeys.Contains(context.Record.Template.FormKey) &&
                             !_consistencyProvider.IsModSelected(context.Record.Template.FormKey,
                                 AssociatedModSetting.DisplayName, context.Record.Template.FormKey))
                    {
                        message =
                            "The associated data for this NPC shows that it is supposed to use " +
                            templateDispName +
                            " as its template. Would you like to select " + AssociatedModSetting.DisplayName +
                            " as the Appearance Mod for " + templateDispName + "?" +
                            " Failing to do so is likely to result in a bugged appearance.";
                        title = "Auto-Select Template?";
                        if (ScrollableMessageBox.Confirm(message, title, displayImagePath: imagePath))
                        {
                            _consistencyProvider.SetSelectedMod(context.Record.Template.FormKey,
                                AssociatedModSetting.DisplayName, context.Record.Template.FormKey);
                        }
                    }
                }
            }
        }
    }

    private void SetNpcSourcePluginInternal(ModKey selectedPluginKey)
    {
        if (AssociatedModSetting == null || !IsAmbiguousSource)
        {
            Debug.WriteLine(
                $"SetNpcSourcePluginInternal called for non-ambiguous NPC {_targetNpcFormKey}. This should not happen.");
            return;
        }

        if (selectedPluginKey.IsNull)
        {
            Debug.WriteLine(
                $"SetNpcSourcePluginInternal called with a null/invalid ModKey for NPC {_targetNpcFormKey}.");
            return;
        }

        // Call back to the parent VM_ModSetting to handle the logic
        bool successfullyUpdated = AssociatedModSetting.SetSingleNpcSourcePlugin(_targetNpcFormKey, selectedPluginKey);

        if (successfullyUpdated)
        {
            // The parent VM_ModSetting has updated its NpcPluginDisambiguation map.
            // Now, this specific VM_NpcsMenuMugshot instance should update its own CurrentSourcePlugin
            // to reflect the new choice for the context menu checkmark.
            if (AssociatedModSetting.NpcPluginDisambiguation.TryGetValue(this._targetNpcFormKey,
                    out var newResolvedSource))
            {
                this.CurrentSourcePlugin = newResolvedSource;
            }
            else
            {
                this.CurrentSourcePlugin = selectedPluginKey;
                Debug.WriteLine(
                    $"Warning: Could not re-resolve source for NPC {_targetNpcFormKey} from NpcPluginDisambiguation map after setting. Displayed checkmark might be based on direct selection.");
            }
        }
    }

    private void ToggleFullScreen()
    {
        // Use ImagePath directly as it points to either the real mugshot or the placeholder
        if (!string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath))
        {
            try
            {
                var fullScreenVM = new VM_FullScreenImage(ImagePath);
                var fullScreenView = Locator.Current.GetService<IViewFor<VM_FullScreenImage>>() as Window;

                if (fullScreenView != null)
                {
                    fullScreenView.DataContext = fullScreenVM;
                    fullScreenView.ShowDialog();
                }
                else
                {
                    ScrollableMessageBox.ShowError("Could not create or resolve the FullScreenImageView.");
                }
            }
            catch (Exception ex)
            {
                // This catch might be redundant if File.Exists is reliable, but good for safety.
                ScrollableMessageBox.ShowWarning(
                    $"Mugshot not found or path is invalid (exception during display):\n{ImagePath}\n{ExceptionLogger.GetExceptionStack(ex)}");
            }
        }
        else
        {
            ScrollableMessageBox.ShowWarning($"Mugshot not found or path is invalid:\n{ImagePath}");
        }
    }

    public void HideThisMod()
    {
        _vmNpcSelectionBar.HideSelectedMod(this);
    }

    private void ToggleFavorite()
    {
        var favoriteTuple = (this.SourceNpcFormKey, this.ModName);
        if (IsFavorite)
        {
            _settings.FavoriteFaces.Remove(favoriteTuple);
            IsFavorite = false;
            Debug.WriteLine($"Removed {favoriteTuple} from favorites.");
        }
        else
        {
            _settings.FavoriteFaces.Add(favoriteTuple);
            IsFavorite = true;
            Debug.WriteLine($"Added {favoriteTuple} to favorites.");
        }
    }

    private void InitializeShareSourceListener()
    {
        if (_settings.GuestAppearances == null || !_settings.GuestAppearances.Any())
        {
            IsShareSource = false;
            return;
        }

        var reverseGuestLookup = new Dictionary<(FormKey, string), List<FormKey>>();
        foreach (var entry in _settings.GuestAppearances)
        {
            var targetNpcKey = entry.Key;
            foreach (var (modName, sourceNpcKey, _) in entry.Value)
            {
                var sourceTuple = (sourceNpcKey, modName);
                if (!reverseGuestLookup.TryGetValue(sourceTuple, out var targets))
                {
                    targets = new List<FormKey>();
                    reverseGuestLookup[sourceTuple] = targets;
                }

                targets.Add(targetNpcKey);
            }
        }

        var thisAppearanceKey = (this.SourceNpcFormKey, this.ModName);
        if (reverseGuestLookup.TryGetValue(thisAppearanceKey, out var guestTargetKeys))
        {
            IsShareSource = true;

            _consistencyProvider.NpcSelectionChanged
                .Where(args => guestTargetKeys.Contains(args.NpcFormKey))
                .Throttle(TimeSpan.FromMilliseconds(50))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => UpdateShareSourceStatusAndTooltip(guestTargetKeys))
                .DisposeWith(Disposables);

            UpdateShareSourceStatusAndTooltip(guestTargetKeys);
        }
    }

    private void UpdateShareSourceStatusAndTooltip(List<FormKey> guestTargetKeys)
    {
        var selectedGuests = new List<string>();
        var unselectedGuests = new List<string>();

        var npcNameMap = _vmNpcSelectionBar.AllNpcs.ToDictionary(n => n.NpcFormKey, n => n.DisplayName);

        foreach (var guestNpcKey in guestTargetKeys)
        {
            bool isSelectedForGuest =
                _consistencyProvider.IsModSelected(guestNpcKey, this.ModName, this.SourceNpcFormKey);
            string guestNpcName = npcNameMap.TryGetValue(guestNpcKey, out var name) ? name : guestNpcKey.ToString();

            if (isSelectedForGuest)
            {
                selectedGuests.Add(guestNpcName);
            }
            else
            {
                unselectedGuests.Add(guestNpcName);
            }
        }

        IsSelectedByGuest = selectedGuests.Any();

        var sb = new System.Text.StringBuilder();
        if (unselectedGuests.Any())
        {
            sb.AppendLine("Shared with: " + string.Join(", ", unselectedGuests.OrderBy(n => n)));
        }

        if (selectedGuests.Any())
        {
            sb.AppendLine("Selected for: " + string.Join(", ", selectedGuests.OrderBy(n => n)));
        }

        ShareSourceTooltipText = sb.ToString().Trim();
    }

    public async Task GenerateMugshotAsync(CancellationToken token)
    {
        try
        {
            _eventLogger.Log($"Loading mugshot for {SourceNpcFormKey} from {ModName}", "Load_START");

            // Determine once whether the curated mugshot should be loaded up
            // front or deferred. Deferring happens when AutoGen outranks
            // Downloaded: the priority loop should then drive AutoGen first
            // and only fall back to loading curated if the render fails.
            bool deferCurated = ShouldDeferCuratedLoad();

            // First, ensure the initial image is loaded and visible. In
            // non-deferred mode this also pulls the user-curated mugshot
            // (setting HasMugshot=true) so the Downloaded branch's bool check
            // succeeds. In deferred mode this only loads the placeholder; the
            // Downloaded branch actively loads curated on its turn instead.
            await LoadInitialImageAsync(placeholderOnly: deferCurated);
            if (token.IsCancellationRequested) return;

            IsLoading = true;

            // Walk the user's mugshot-source priority order. The first source that
            // produces a result wins; disabled sources (UseFaceFinderFallback off,
            // UsePortraitCreatorFallback off, no curated mugshot loaded) report
            // "not handled" so the loop falls through to the next source.
            foreach (var source in _settings.MugshotSourcePriority)
            {
                if (token.IsCancellationRequested) return;

                bool handled = source switch
                {
                    MugshotSourceType.DownloadedMugshots => deferCurated
                                                            ? TryLoadCuratedMugshot()
                                                            : HasMugshot,
                    MugshotSourceType.FaceFinder         => _settings.UseFaceFinderFallback
                                                            && await TryFaceFinderSourceAsync(token),
                    MugshotSourceType.AutoGeneration     => _settings.UsePortraitCreatorFallback
                                                            && await TryAutoGenerationSourceAsync(token),
                    _ => false,
                };

                if (handled) return;
            }
        }
        catch (TaskCanceledException)
        {
            /* Swallow cancellation */
        }
        catch (OperationCanceledException)
        {
            /* Swallow cancellation */
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error generating real image for {SourceNpcFormKey}: {ExceptionLogger.GetExceptionStack(ex)}");
            _eventLogger.Log($"Error loading mugshot for {SourceNpcFormKey}: {ex.Message}", "LOADING_ERROR");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>FaceFinder branch of the priority loop. Returns true when a
    /// cache hit or successful download produced a visible image, false when
    /// FaceFinder had nothing to offer (so the next priority source runs).</summary>
    private async Task<bool> TryFaceFinderSourceAsync(CancellationToken token)
    {
        var ffResult = await _batchGenerator.TryFaceFinderAsync(SourceNpcFormKey, ModName, token);

        if (ffResult.Source == GenerationSource.FaceFinderCache)
        {
            Debug.WriteLine($"Using cached mugshot for {SourceNpcFormKey} from FaceFinder.");
            _eventLogger.Log($"FaceFinder cache hit for {SourceNpcFormKey}", "FACEFINDER");
            SetImageSource(ffResult.OutputPath!);
            AddFaceFinderExternalUrl(ffResult.FaceFinderExternalUrl);
            return true;
        }

        if (ffResult.Source == GenerationSource.FaceFinderDownload && ffResult.ProducedAnything)
        {
            if (ffResult.ProducedFile)
            {
                SetImageSource(ffResult.OutputPath!);
            }
            else if (ffResult.InMemoryImageBytes != null)
            {
                SetImageSourceFromMemory(ffResult.InMemoryImageBytes);
            }

            _eventLogger.Log($"FaceFinder download successful for {SourceNpcFormKey}: {ModName}", "FACEFINDER");
            AddFaceFinderExternalUrl(ffResult.FaceFinderExternalUrl);
            return true;
        }

        return false;
    }

    /// <summary>Auto-generation branch of the priority loop. Runs the
    /// selected renderer (Internal in-process or Legacy NPC Portrait Creator).
    /// Returns true when a file was produced or reused, false when the
    /// preconditions weren't met or the renderer produced nothing.</summary>
    private async Task<bool> TryAutoGenerationSourceAsync(CancellationToken token)
    {
        if (AssociatedModSetting == null)
        {
            // Legacy path requires a local mod for the FaceGen NIF lookup;
            // Internal can render against a model-side ModSetting, but the
            // tile's saveFolder bookkeeping below still needs the VM. If
            // the VM is missing we can't bind the produced PNG back to a
            // mod entry, so this source cannot run for this tile - report
            // "not handled" so the next priority source still gets a turn.
            Debug.WriteLine($"Cannot generate mugshot locally for {ModName}; AssociatedModSetting not found.");
            _eventLogger.Log($"Cannot generate portrait locally for {ModName}; Mod not found", "PORTRAIT_GEN_ERROR");
            return false;
        }

        // Mugshot-only / phantom mod entries (e.g. an entry NPC2 synthesized
        // from a leftover empty subfolder under MugshotsFolder, or a
        // FaceFinder-discovery entry whose Nexus mod isn't installed locally)
        // have no CorrespondingFolderPaths. The renderer would still produce
        // a render against the vanilla scope alone, attributing a generic
        // base-game face to this mod's name — visually misleading, and the
        // resulting PNG would be self-registered into the entry's
        // MugShotFolderPaths, perpetuating the phantom every session.
        // BaseGame / CC synthesized entries (IsAutoGenerated=true) intentionally
        // have empty CorrespondingFolderPaths and DO want the vanilla-scoped
        // render, so allow those through.
        if (!AssociatedModSetting.CorrespondingFolderPaths.Any()
            && !AssociatedModSetting.IsAutoGenerated)
        {
            Debug.WriteLine($"Skipping autogen for {ModName}; mod has no installable data (no CorrespondingFolderPaths).");
            _eventLogger.Log($"Skipping autogen for {ModName}; no mod data", "PORTRAIT_GEN_SKIPPED");
            return false;
        }

        _eventLogger.Log($"Falling back to {_settings.SelectedRenderer} renderer for {SourceNpcFormKey}", "PORTRAIT_GEN");

        var rendererResult = await _batchGenerator.RunSelectedRendererAsync(
            SourceNpcFormKey, AssociatedModSetting, token);

        // The Internal renderer reports per-render missing-asset paths on a
        // fresh render; surface them via the tile overlay so the user can
        // see which shapes rendered as wireframes. Skip on AlreadyCurrent —
        // the result carries empty arrays there (nothing was rendered this
        // call), and overwriting would wipe the missing-asset state that
        // LoadInitialImageAsync restored from the PNG's stamped JSON metadata.
        if (rendererResult.Source == GenerationSource.InternalRenderer && rendererResult.Generated)
        {
            ApplyMissingAssetNotifications(rendererResult.MissingMeshes, rendererResult.MissingTextures);
        }

        // ProducedFile covers both Generated == true (just rendered) and
        // AlreadyCurrent == true (existing PNG was fresh). The AlreadyCurrent
        // branch is the one that fails after relaunch when MugshotsFolder is
        // blank and the tile was constructed with an empty ImagePath: without
        // this we'd leave the placeholder up even though a valid PNG sits on disk.
        if (rendererResult.ProducedFile && rendererResult.OutputPath != null)
        {
            if (rendererResult.Generated)
            {
                Debug.WriteLine($"Generated mugshot for {SourceNpcFormKey}.");
                _eventLogger.Log($"Portrait generation successful for {SourceNpcFormKey}", "PORTRAIT_GEN");
            }
            else
            {
                Debug.WriteLine($"Reused existing mugshot for {SourceNpcFormKey}.");
            }
            SetImageSource(rendererResult.OutputPath);
            return true;
        }

        return false;
    }

    private void AddFaceFinderExternalUrl(string? externalUrl)
    {
        if (string.IsNullOrWhiteSpace(externalUrl)) return;
        if (ModPageUrls.All(p => p.Url != externalUrl))
        {
            ModPageUrls.Add(new ModPageInfo("FaceFinder", externalUrl));
        }
    }

    /// <summary>Sets the unified missing-asset overlay state from the two
    /// lists the internal mugshot generator populated. Both empty clears
    /// the overlay; otherwise the overlay shows and the tooltip lists
    /// each kind under its own heading, omitting any section with no
    /// entries so the tooltip stays compact.</summary>
    private void ApplyMissingAssetNotifications(
        IReadOnlyList<string> missingMeshes,
        IReadOnlyList<string> missingTextures)
    {
        bool hasMeshes = missingMeshes != null && missingMeshes.Count > 0;
        bool hasTextures = missingTextures != null && missingTextures.Count > 0;
        if (!hasMeshes && !hasTextures)
        {
            HasMissingAssets = false;
            MissingAssetNotificationText = string.Empty;
            return;
        }

        var sb = new StringBuilder();
        if (hasMeshes)
        {
            sb.Append("The following expected mesh paths could not be found:");
            foreach (var p in missingMeshes) sb.Append('\n').Append(p);
        }
        if (hasTextures)
        {
            if (hasMeshes) sb.Append("\n\n");
            sb.Append("The following expected texture paths could not be found:");
            foreach (var p in missingTextures) sb.Append('\n').Append(p);
        }

        HasMissingAssets = true;
        MissingAssetNotificationText = sb.ToString();
    }

    private void SetImageSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var bitmap = new BitmapImage();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }
        bitmap.Freeze();

        // Update this VM's properties to reflect the new image
        this.MugshotSource = bitmap;
        this.ImagePath = path;
        this.HasMugshot = true;
    }
    
    private void SetImageSourceFromMemory(byte[] imageData)
    {
        if (imageData == null || imageData.Length == 0) return;

        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(imageData))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // Read fully into memory
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }
        bitmap.Freeze(); // Make it thread-safe for the UI

        // Update the UI properties
        this.MugshotSource = bitmap;
        this.ImagePath = "in-memory"; // A non-file path to indicate it's not saved
        this.HasMugshot = true;

        // We also need to update the dimensions from the in-memory data
        var info = Image.Identify(imageData);
        OriginalPixelWidth = info.Width;
        OriginalPixelHeight = info.Height;
        OriginalDipWidth = info.Width;
        OriginalDipHeight = info.Height;
        OriginalDipDiagonal = Math.Sqrt(OriginalDipWidth * OriginalDipWidth + OriginalDipHeight * OriginalDipHeight);
    }

    public void Dispose()
    {
        Disposables.Dispose();
    }

    // --- IDragSource Implementation ---

    public bool CanStartDrag(IDragInfo dragInfo)
    {
        return true;
    }

    public void StartDrag(IDragInfo dragInfo)
    {
        dragInfo.Data = this;
        dragInfo.Effects = DragDropEffects.Move | DragDropEffects.Copy;
        Debug.WriteLine($"VM_NpcsMenuMugshot.StartDrag: Dragging '{this.ModName}'");
    }

    public void Dropped(IDropInfo dropInfo)
    {
        Debug.WriteLine(
            $"VM_NpcsMenuMugshot.Dropped (Source): '{this.ModName}' was dropped with effect {dropInfo.Effects}");
    }

    public void DragCancelled()
    {
        Debug.WriteLine($"VM_NpcsMenuMugshot.DragCancelled: Drag of '{this.ModName}' cancelled.");
    }

    public void DragDropOperationFinished(DragDropEffects operationResult, IDragInfo dragInfo)
    {
        Debug.WriteLine(
            $"VM_NpcsMenuMugshot.DragDropOperationFinished: Operation for '{this.ModName}' finished with result {operationResult}.");
    }

    public bool TryMove(IDropInfo dropInfo)
    {
        return false;
    }

    public bool TryCatchOccurredException(Exception exception)
    {
        Debug.WriteLine(
            $"ERROR VM_NpcsMenuMugshot.TryCatchOccurredException (Source): Exception during D&D for '{this.ModName}': {exception}");
        return true;
    }

    // --- IDropTarget Implementation ---

    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        var sourceItem = dropInfo.Data as VM_NpcsMenuMugshot;
        dropInfo.Effects = DragDropEffects.None;

        if (sourceItem == null || sourceItem == this) return;

        // --- NEW: Allow linking a FaceFinder-only mugshot to a local mod ---
        bool sourceIsFaceFinderOnly = sourceItem.AssociatedModSetting == null && sourceItem.HasMugshot;
        bool targetIsLocalMod = this.AssociatedModSetting != null;
        bool sourceIsLocalMod = sourceItem.AssociatedModSetting != null;
        bool targetIsFaceFinderOnly = this.AssociatedModSetting == null && this.HasMugshot;

        if ((sourceIsFaceFinderOnly && targetIsLocalMod) || (targetIsFaceFinderOnly && sourceIsLocalMod))
        {
            dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
            dropInfo.Effects = DragDropEffects.Move;
            return;
        }
        
        // --- Existing Logic for other D&D scenarios ---
        bool sourceIsRealMugshotVm = sourceItem.HasMugshot;
        bool targetIsRealMugshotVm = this.HasMugshot;
        
        if (sourceIsRealMugshotVm && targetIsRealMugshotVm)
        {
            dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
            dropInfo.Effects = DragDropEffects.Move;
            return;
        }

        if (!((sourceIsRealMugshotVm && !targetIsRealMugshotVm) ||
              (!sourceIsRealMugshotVm && targetIsRealMugshotVm)))
        {
            return;
        }

        var mugshotVmApp = sourceIsRealMugshotVm ? sourceItem : this;
        var placeholderVmApp = sourceIsRealMugshotVm ? this : sourceItem;

        var mugshotModSetting = mugshotVmApp.AssociatedModSetting;
        var placeholderModSetting = placeholderVmApp.AssociatedModSetting;

        bool mugshotPathValid = mugshotModSetting != null &&
                                !string.IsNullOrWhiteSpace(mugshotVmApp.ImagePath) &&
                                File.Exists(mugshotVmApp.ImagePath);
        bool placeholderPathsValid = placeholderModSetting != null &&
                                     (placeholderModSetting.CorrespondingFolderPaths.Any() ||
                                      placeholderModSetting.IsAutoGenerated);

        if (mugshotPathValid && placeholderPathsValid)
        {
            dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
            dropInfo.Effects = DragDropEffects.Move;
        }
    }


    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        var sourceItem = dropInfo.Data as VM_NpcsMenuMugshot;
        if (sourceItem == null || sourceItem == this) return;
        
        // --- NEW: Handle linking a FaceFinder-only mugshot to a local mod ---
        var faceFinderVm = sourceItem.AssociatedModSetting == null && sourceItem.HasMugshot ? sourceItem : (this.AssociatedModSetting == null && this.HasMugshot ? this : null);
        var localVm = sourceItem.AssociatedModSetting != null ? sourceItem : (this.AssociatedModSetting != null ? this : null);

        var sb = new StringBuilder();
        string imagePath = @"Resources\Dragon Drop.png";
        
        if (faceFinderVm != null && localVm != null && localVm.AssociatedModSetting != null)
        {
            var serverModName = faceFinderVm.ModName;
            var localModName = localVm.AssociatedModSetting.DisplayName;

            if (serverModName.Equals(localModName, StringComparison.OrdinalIgnoreCase)) return;
            
            sb.AppendLine($"Link the server mod '{serverModName}' to your local mod '{localModName}'?");
            sb.AppendLine($"\nFuture searches for '{localModName}' will use the server name to find mugshots.");

            if (ScrollableMessageBox.Confirm(sb.ToString(), "Confirm FaceFinder Link", displayImagePath: imagePath))
            {
                if (!_settings.FaceFinderModNameMappings.TryGetValue(localModName, out var mappings))
                {
                    mappings = new List<string>();
                    _settings.FaceFinderModNameMappings[localModName] = mappings;
                }
                if (!mappings.Contains(serverModName, StringComparer.OrdinalIgnoreCase))
                {
                    mappings.Add(serverModName);
                    Debug.WriteLine($"Linked server mod '{serverModName}' to local mod '{localModName}'.");
                    _vmNpcSelectionBar?.RefreshCurrentNpcAppearanceSources();
                }
            }
            return; // Exit after handling this case
        }

        bool sourceIsRealMugshotVm = sourceItem.HasMugshot;
        bool targetIsRealMugshotVm = this.HasMugshot;

        // NEW: Handle drop between two real mugshots
        if (sourceIsRealMugshotVm && targetIsRealMugshotVm)
        {
            var droppedMod = sourceItem.ModName;
            var droppedNpc = sourceItem.SourceNpcFormKey;
            var targetMod = this.ModName;
            var targetNpc = this.SourceNpcFormKey;
            _vmNpcSelectionBar.MassUpdateNpcSelections(targetMod, targetNpc, droppedMod, droppedNpc);
            return; // Exit after handling this case
        }

        // Original case: Handle drop between a real mugshot and a placeholder
        if (!((sourceIsRealMugshotVm && !targetIsRealMugshotVm) ||
              (!sourceIsRealMugshotVm && targetIsRealMugshotVm))) return;

        var mugshotVmApp = sourceIsRealMugshotVm ? sourceItem : this;
        var placeholderVmApp = sourceIsRealMugshotVm ? this : sourceItem;

        var mugshotSourceModSetting = mugshotVmApp.AssociatedModSetting;
        var placeholderTargetModSetting = placeholderVmApp.AssociatedModSetting;

        if (mugshotSourceModSetting == null || placeholderTargetModSetting == null ||
            string.IsNullOrWhiteSpace(sourceItem.ImagePath) ||
            !File.Exists(sourceItem.ImagePath) ||
            (!placeholderTargetModSetting.CorrespondingFolderPaths.Any() &&
             !placeholderTargetModSetting.IsAutoGenerated))
        {
            ScrollableMessageBox.ShowError(
                "Drop conditions not met (Validation failed in Drop). Ensure mugshot provider has valid path and placeholder has mod folders.",
                "Drop Error");
            return;
        }

        sb = new StringBuilder();
        sb.AppendLine(
            $"Are you sure you want to associate the Mugshots from [{mugshotSourceModSetting.DisplayName}] with the Mod Folder(s) from [{placeholderTargetModSetting.DisplayName}]?");
        bool mugshotProviderHasGameDataFolders = mugshotSourceModSetting.CorrespondingFolderPaths.Any();

        if (mugshotProviderHasGameDataFolders)
        {
            sb.AppendLine(
                $"\n[{placeholderTargetModSetting.DisplayName}] will now use mugshots from [{mugshotSourceModSetting.DisplayName}]. Both mod entries will remain.");
        }
        else
        {
            sb.AppendLine(
                $"\n[{placeholderTargetModSetting.DisplayName}] will take over the mugshots from [{mugshotSourceModSetting.DisplayName}].");
            sb.AppendLine($"The separate entry for [{mugshotSourceModSetting.DisplayName}] will be removed.");
        }

        if (ScrollableMessageBox.Confirm(
                message: sb.ToString(),
                title: "Confirm Dragon Drop Operation",
                displayImagePath: imagePath))
        {
            // NEW: Check if the drop target is an auto-generated mugshot.
            bool isTargetAutoGenerated = !string.IsNullOrWhiteSpace(placeholderVmApp.ImagePath) &&
                                         File.Exists(placeholderVmApp.ImagePath) &&
                                         _portraitCreator.IsAutoGenerated(placeholderVmApp.ImagePath);

            if (isTargetAutoGenerated)
            {
                // Case: Real mugshot dropped on an auto-generated one.
                Debug.WriteLine($"Detected drop of real mugshot onto auto-generated mugshot for mod '{placeholderTargetModSetting.DisplayName}'.");

                // 1. Link mugshot folders by adding the real mugshot provider's folders to the target mod.
                foreach (var realMugshotModFolder in mugshotSourceModSetting.MugShotFolderPaths)
                {
                    if (!placeholderTargetModSetting.MugShotFolderPaths.Contains(realMugshotModFolder, StringComparer.OrdinalIgnoreCase))
                    {
                        placeholderTargetModSetting.MugShotFolderPaths.Add(realMugshotModFolder);
                        Debug.WriteLine($"Associated real mugshot folder '{realMugshotModFolder}' with mod '{placeholderTargetModSetting.DisplayName}'.");
                    }
                }

                // 2. Find the specific auto-generated folder and replace its contents.
                string? autoGenImageModFolder = placeholderTargetModSetting.MugShotFolderPaths
                    .FirstOrDefault(p => placeholderVmApp.ImagePath.StartsWith(p, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(autoGenImageModFolder) && Directory.Exists(autoGenImageModFolder))
                {
                    // Iterate through all "real" folders from the source.
                    foreach (var realMugshotModFolder in mugshotSourceModSetting.MugShotFolderPaths)
                    {
                        if (string.IsNullOrWhiteSpace(realMugshotModFolder) || !Directory.Exists(realMugshotModFolder)) continue;

                        // Delete any auto-generated files that are now superseded by real files.
                        try
                        {
                            var realMugshotFiles = Directory.EnumerateFiles(realMugshotModFolder, "*.*",
                                SearchOption.AllDirectories);
                            foreach (var realFilePath in realMugshotFiles)
                            {
                                var relativePath = Path.GetRelativePath(realMugshotModFolder, realFilePath);
                                var correspondingAutoGenPath = Path.Combine(autoGenImageModFolder, relativePath);

                                if (File.Exists(correspondingAutoGenPath) &&
                                    _portraitCreator.IsAutoGenerated(correspondingAutoGenPath))
                                {
                                    try
                                    {
                                        File.Delete(correspondingAutoGenPath);
                                        Debug.WriteLine(
                                            $"Deleted auto-generated mugshot '{correspondingAutoGenPath}' which is superseded by a real one.");

                                        // NEW: Clean up empty parent directories from the bottom up.
                                        var parentDir = Path.GetDirectoryName(correspondingAutoGenPath);
                                        while (parentDir != null &&
                                               !parentDir.Equals(autoGenImageModFolder,
                                                   StringComparison.OrdinalIgnoreCase) &&
                                               !Directory.EnumerateFileSystemEntries(parentDir).Any())
                                        {
                                            try
                                            {
                                                Directory.Delete(parentDir);
                                                Debug.WriteLine($"Deleted empty parent folder '{parentDir}'.");
                                                parentDir = Path.GetDirectoryName(
                                                    parentDir); // Move up to the next parent.
                                            }
                                            catch (Exception deleteEx)
                                            {
                                                Debug.WriteLine(
                                                    $"Failed to delete empty parent folder '{parentDir}'. Error: {deleteEx.Message}");
                                                break; // Stop if a delete fails.
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine(
                                            $"Failed to delete auto-generated mugshot '{correspondingAutoGenPath}'. Error: {ex.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error enumerating files in '{realMugshotModFolder}': {ex.Message}");
                        }
                    }

                    // 3. Clean up the auto-gen directory if it's now empty and is not one of the "real" directories.
                    bool isDistinctFolder = mugshotSourceModSetting.MugShotFolderPaths.All(p => !p.Equals(autoGenImageModFolder, StringComparison.OrdinalIgnoreCase));
                    
                    // Use EnumerateFileSystemEntries to check for remaining files OR directories.
                    bool autoGenDirIsEmpty = !Directory.EnumerateFileSystemEntries(autoGenImageModFolder).Any();

                    if (autoGenDirIsEmpty && isDistinctFolder)
                    {
                        if (placeholderTargetModSetting.MugShotFolderPaths.Remove(autoGenImageModFolder))
                        {
                            Debug.WriteLine($"Removed empty auto-gen folder '{autoGenImageModFolder}' from mod settings.");
                        }

                        try
                        {
                            Directory.Delete(autoGenImageModFolder, true);
                            Debug.WriteLine($"Deleted empty auto-gen folder '{autoGenImageModFolder}' from disk.");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to delete empty auto-gen folder '{autoGenImageModFolder}'. Error: {ex.Message}");
                        }
                    }
                }
            }
            else
            {
                // Original logic for dropping on a true placeholder.
                placeholderTargetModSetting.MugShotFolderPaths.AddRange(mugshotSourceModSetting.MugShotFolderPaths);
            }
            
            _lazyMods.Value?.RecalculateMugshotValidity(placeholderTargetModSetting);

            if (!mugshotProviderHasGameDataFolders)
            {
                var npcKeysToUpdate = _settings.SelectedAppearanceMods
                    .Where(kvp =>
                        kvp.Value.ModName.Equals(mugshotSourceModSetting.DisplayName,
                            StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key).ToList();

                // Re-assign their selection to the newly merged mod.
                foreach (var npcKey in npcKeysToUpdate)
                {
                    // The source of the new selection is the NPC's own FormKey.
                    _consistencyProvider.SetSelectedMod(npcKey, placeholderTargetModSetting.DisplayName, npcKey);
                }

                // Remove the now-redundant mugshot-only mod setting.
                bool wasRemoved = _lazyMods.Value?.RemoveModSetting(mugshotSourceModSetting) ?? false;
                if (!wasRemoved)
                {
                    Debug.WriteLine(
                        $"Warning: Failed to remove mugshotSourceModSetting '{mugshotSourceModSetting.DisplayName}' via VM_Mods.RemoveModSetting.");
                }

                Debug.WriteLine(
                    $"Merge complete. [{placeholderTargetModSetting.DisplayName}] now uses mugshots from the former [{mugshotSourceModSetting.DisplayName}] entry, which has been removed.");
            }
            else
            {
                Debug.WriteLine(
                    $"Association complete. [{placeholderTargetModSetting.DisplayName}] will now use mugshots from [{mugshotSourceModSetting.DisplayName}].");
            }

            _vmNpcSelectionBar?.RefreshCurrentNpcAppearanceSources();
        }
    }
}