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
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
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
using SkyRender;
using SkyRender.Character;

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
    private readonly BackEnd.OutfitDistribution.OutfitDisplayResolver _outfitDisplayResolver;
    private readonly DataFolderAssetAttributor _dataFolderAttributor;
    private readonly NpcMeshResolver _npcMeshResolver;
    private readonly CompositeDisposable Disposables = new();
    // Static + frozen so VM instances can be constructed off the UI thread
    // (see CreateMugShotViewModelsAsync's Task.Run) without WPF's
    // dispatcher-affinity check tripping on these brushes later.
    private static readonly SolidColorBrush _selectedWithDataBrush = CreateFrozenBrush(Colors.LimeGreen);
    private static readonly SolidColorBrush _selectedWithoutDataBrush = CreateFrozenBrush(Colors.DarkMagenta);
    private static readonly SolidColorBrush _deselectedWithDataBrush = CreateFrozenBrush(Colors.Transparent);
    //private static readonly SolidColorBrush _deselectedWithoutDataBrush = CreateFrozenBrush(Colors.Coral); // Now handled with an overlay

    private static SolidColorBrush CreateFrozenBrush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
    private bool _isImageLoadingOrLoaded = false;
    private readonly object _imageLoadLock = new();
    private volatile bool _generationInFlight = false;

    /// <summary>True while a <see cref="GenerateMugshotAsync"/> run for this tile
    /// is queued or executing. Set synchronously at the kick site
    /// (TriggerAsyncMugshotGeneration, main thread) and again at method entry
    /// (covers direct RegenerateAsync calls); cleared in the method's finally.
    /// Lets the trigger re-fire freely — repacks now happen every time a tile
    /// image lands — without cancelling and restarting in-flight renders or
    /// double-kicking a tile whose task hasn't started yet.</summary>
    public bool IsGenerationInFlight
    {
        get => _generationInFlight;
        set => _generationInFlight = value;
    }


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

    /// <summary>
    /// A Template issue that the current Template Handling Mode DEFUSES: the chain will be
    /// flattened, so this mod's copy of the template's appearance lands on the NPC's own record
    /// and the selection made here applies to it individually. Downgrades the issue "!" from red
    /// to the theme's warning colour — still worth pointing at (the NPC has no face of its own),
    /// but no longer the "your choice is ignored" red.
    /// </summary>
    [Reactive] public bool TemplateResolvesPerNpc { get; set; }
    /// <summary>True when the most recent in-process mugshot render
    /// reported any unresolved mesh OR texture paths. Drives a single
    /// "missing asset" overlay; the per-kind detail is in
    /// <see cref="MissingAssetNotificationText"/>.</summary>
    [Reactive] public bool HasMissingAssets { get; set; } = false;
    [Reactive] public string MissingAssetNotificationText { get; set; } = string.Empty;
    /// <summary>True when this NPC's outfit/headgear is missing assets: attire
    /// meshes that didn't resolve/render, attire textures that couldn't decode,
    /// and/or a stale-physics-config link (an attire mesh links an SMP/HDT XML
    /// that doesn't exist — a broken link in the mod itself). Drives the
    /// outfit-asset icon, kept separate from the base NPC's <see cref="HasMissingAssets"/>.
    /// The missing meshes/textures are re-render-eligible; the physics link is
    /// informational (render correct, never re-stales). Detail in
    /// <see cref="MissingOutfitAssetsText"/>.</summary>
    [Reactive] public bool HasMissingOutfitAssets { get; set; } = false;
    [Reactive] public string MissingOutfitAssetsText { get; set; } = string.Empty;

    /// <summary>True when the render pulled non-vanilla assets from the data
    /// folder because they weren't in this mod's Corresponding Mod Folders
    /// (runtime dependencies — whichever mod supplies them must stay activated).
    /// Drives the data-folder-asset icon. Informational only: the render is
    /// correct and the stamp is staleness-neutral. Detail in
    /// <see cref="DataFolderAssetsText"/>.</summary>
    [Reactive] public bool HasDataFolderAssets { get; set; } = false;
    [Reactive] public string DataFolderAssetsText { get; set; } = string.Empty;
    /// <summary>True when the effective-outfit simulation reports a runtime
    /// conflict for this tile: "Include Outfit" is overridden by a
    /// SkyPatcher/SPID config, or (SkyPatcher mode) NPC2's own ini entry is
    /// not conflict-winning. Drives the outfit-warning badge; the full text
    /// is in <see cref="OutfitNoticeText"/>. Computed live from the current
    /// configs at tile load / after generation, not read from stamped
    /// metadata, so it stays current when the overriding config changes
    /// without the depicted outfit changing.</summary>
    [Reactive] public bool HasOutfitNotice { get; set; } = false;
    [Reactive] public string OutfitNoticeText { get; set; } = string.Empty;
    /// <summary>True when this tile's mod has its effective Antler Handling Mode
    /// set to <see cref="AntlerHandlingMode.Remove"/> AND this NPC actually
    /// carries an antler the patch will strip (outfit / WornArmor / FaceGen head
    /// part). Drives the "no antlers" badge; the full text is in
    /// <see cref="AntlerRemovalNoticeText"/>. Computed live from the current
    /// configs at tile load / after generation via <see cref="NpcMeshResolver"/>,
    /// mirroring the 3D preview's removal notice.</summary>
    [Reactive] public bool HasAntlerRemovalNotice { get; set; } = false;
    [Reactive] public string AntlerRemovalNoticeText { get; set; } = string.Empty;
    /// <summary>True when this NPC actually carries a wig this mod supplies (a
    /// hair-slot armor via its WornArmor/skin, or a detected wig ARMO in its
    /// Default Outfit). Purely informational and mode-independent: it flags
    /// that the depicted appearance INCLUDES a wig regardless of the mod's Wig
    /// Handling Mode. Drives the "has wig" badge; the full text is in
    /// <see cref="WigNoticeText"/>. Set ONCE at construction from the analysis
    /// scan's per-NPC wig-source map + live manual designations
    /// (<see cref="Settings.GetEffectiveNpcWigSources"/>) — a plugin-record
    /// fact, deliberately independent of the mugshot generation pipeline.</summary>
    [Reactive] public bool HasWigNotice { get; set; } = false;
    [Reactive] public string WigNoticeText { get; set; } = string.Empty;
    /// <summary>True when this NPC's Default-Outfit wig will NOT reach the patch
    /// output — Wig Handling Mode is inert AND the outfit carrying it isn't being
    /// forwarded, or a SPID/SkyPatcher outfit replaces it at runtime. Swaps the has-wig
    /// badge for the "No Wig" icon. The mugshot leaves such a wig off to match the game
    /// (NpcMeshResolver), so the badge is what tells the user this mod supplies one
    /// they aren't getting. Skin-carried wigs always persist and
    /// never trip this. Unlike <see cref="HasWigNotice"/> — a fixed record fact —
    /// this depends on live settings, so it is recomputed on the post-generation
    /// notice refresh as well as at construction.</summary>
    [Reactive] public bool WigNotPersisted { get; set; } = false;
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
    // Backed rather than auto so a LIVE tile reports the mugshot OUTPUT
    // dimensions to the ImagePacker regardless of what any (possibly
    // concurrent) bitmap load stored: the embedded viewport must be packed at
    // the output aspect for its framing to match the autogenerated mugshot,
    // and flipping IsLiveTile back restores the stored image dimensions
    // without any cache/restore dance.
    private int _originalPixelWidth;
    private int _originalPixelHeight;
    private double _originalDipWidth;
    private double _originalDipHeight;
    private double _originalDipDiagonal;
    private int LiveTileOutputWidth => Math.Max(1, _settings.InternalMugshot.OutputWidth);
    private int LiveTileOutputHeight => Math.Max(1, _settings.InternalMugshot.OutputHeight);
    public int OriginalPixelWidth
    {
        get => IsLiveTile ? LiveTileOutputWidth : _originalPixelWidth;
        set => _originalPixelWidth = value;
    }
    public int OriginalPixelHeight
    {
        get => IsLiveTile ? LiveTileOutputHeight : _originalPixelHeight;
        set => _originalPixelHeight = value;
    }
    public double OriginalDipWidth
    {
        get => IsLiveTile ? LiveTileOutputWidth : _originalDipWidth;
        set => _originalDipWidth = value;
    }
    public double OriginalDipHeight
    {
        get => IsLiveTile ? LiveTileOutputHeight : _originalDipHeight;
        set => _originalDipHeight = value;
    }
    public double OriginalDipDiagonal
    {
        get => IsLiveTile
            ? Math.Sqrt((double)LiveTileOutputWidth * LiveTileOutputWidth
                        + (double)LiveTileOutputHeight * LiveTileOutputHeight)
            : _originalDipDiagonal;
        set => _originalDipDiagonal = value;
    }
    [Reactive] public ImageSource? MugshotSource { get; set; }

    // --- NEW Property for Compare Checkbox ---
    [Reactive] public bool IsCheckedForCompare { get; set; } = false;

    // --- Live Tile state ---
    /// <summary>True while this tile displays an embedded 3D viewport instead
    /// of its static image. Toggled via the context menu ("Make Live Tile" /
    /// "Make Mugshot"), the NPCs-menu bulk 3D/2D button, or resolution of the
    /// LiveTile mugshot source on NPC selection.</summary>
    [Reactive] public bool IsLiveTile { get; private set; }

    /// <summary>The per-tile preview VM backing the embedded viewport; null
    /// while the tile is a plain mugshot. The tile's UC_LiveTileViewport binds
    /// to it and owns the GL teardown via ViewShutdownRequested.</summary>
    [Reactive] public VM_InternalMugshotPreview? LiveTilePreview { get; private set; }

    /// <summary>Same eligibility as Show3DPreviewCommand: an installed
    /// (non-mugshot-only) mod entry and a real target NPC.</summary>
    public bool CanBeLiveTile => AssociatedModSetting != null
                                 && !AssociatedModSetting.IsMugshotOnlyEntry
                                 && !_targetNpcFormKey.IsNull;

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
    public ReactiveCommand<Unit, Unit> ToggleLiveTileCommand { get; }
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
    public ReactiveCommand<Unit, Unit> GenerateMugshotCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteMugshotCommand { get; }

    /// <summary>Whether this tile's mod has enough installed data for the renderer
    /// to draw it. Mirrors <see cref="TryAutoGenerationSourceAsync"/>'s two
    /// preconditions, so the Generate Mugshot menu entry is hidden on exactly the
    /// tiles where a render would bail out — mugshot-only / FaceFinder-discovery
    /// entries whose mod isn't installed. Base Game and Creation Club carry no
    /// folder paths by design and DO render, hence the IsAutoGenerated arm.</summary>
    public bool CanGenerateMugshot =>
        AssociatedModSetting != null
        && (AssociatedModSetting.CorrespondingFolderPaths.Any() || AssociatedModSetting.IsAutoGenerated);

    /// <summary>Which of the three sources the image currently on screen came from,
    /// or <see cref="MugshotSourceType.None"/> when what's showing is the bundled
    /// placeholder, an unsaved in-memory FaceFinder download, or nothing at all.
    /// Drives the Delete Mugshot entry — the user's mental model is "delete the
    /// picture I'm looking at", so the target is resolved from the displayed image
    /// rather than from the priority order.</summary>
    [Reactive] public MugshotSourceType VisibleMugshotSource { get; private set; } = MugshotSourceType.None;

    public bool CanDeleteMugshot => VisibleMugshotSource != MugshotSourceType.None;

    /// <summary>Menu header for the delete entry, naming the source being deleted so
    /// the user can tell which of a possible three images is about to go.</summary>
    public string DeleteMugshotHeader => VisibleMugshotSource switch
    {
        MugshotSourceType.DownloadedMugshots => "Delete Mugshot (Downloaded)",
        MugshotSourceType.FaceFinder         => "Delete Mugshot (FaceFinder)",
        MugshotSourceType.AutoGeneration     => "Delete Mugshot (Auto-Generated)",
        _                                    => "Delete Mugshot",
    };

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
        FaceGenAnalysisCache faceGenAnalysisCache,
        BackEnd.OutfitDistribution.OutfitDisplayResolver outfitDisplayResolver,
        NpcMeshResolver npcMeshResolver,
        DataFolderAssetAttributor dataFolderAttributor)
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
        _outfitDisplayResolver = outfitDisplayResolver;
        _npcMeshResolver = npcMeshResolver;
        _dataFolderAttributor = dataFolderAttributor;

        // FaceGen analysis: derived display properties tied to settings + Stats.
        // Reactive subscriptions live in InitFaceGenAnalysis() so the full block
        // is together; called at the end of the constructor.
        InitFaceGenAnalysis();

        // "Has wig" badge — record-derived, set once here; see the method doc.
        InitializeWigNotice();

        HasNoData = (AssociatedModSetting == null || (!AssociatedModSetting.CorrespondingFolderPaths.Any() &&
                                                      !AssociatedModSetting.IsAutoGenerated));
        IsFavorite = _settings.FavoriteFaces.Contains((this.SourceNpcFormKey, this.ModName));

        if (HasNoData)
        {
            NoDataNotificationText =
                $"You have Mugshots installed for {AssociatedModSetting?.DisplayName ?? "this mod"} but the mod itself is not installed. {Environment.NewLine}You can still select this as a placeholder, but {npcDisplayName} won't be included in the output until the actual mod is installed.";
        }

        // --- NEW Ambiguous Source Initialization ---
        // Disambiguation (which plugin within this ModSetting provides the appearance) is
        // keyed by the NPC whose record is actually spliced: the appearance DONOR. For a
        // normal replacer the donor equals the target, but for a guest/shared appearance
        // (IsGuestAppearance) they differ, and the Validator/Patcher both resolve the source
        // plugin via the donor's FormKey (appearanceNpcFormKey). Keying this UI on the target
        // instead let the user's source-plugin choice be silently dropped at patch time.
        IsAmbiguousSource = AssociatedModSetting?.AmbiguousNpcFormKeys.Contains(SourceNpcFormKey) ?? false;
        CurrentSourcePlugin = AssociatedModSetting?.NpcPluginDisambiguation.GetValueOrDefault(SourceNpcFormKey);

        if (IsAmbiguousSource && AssociatedModSetting != null &&
            AssociatedModSetting.AvailablePluginsForNpcs.TryGetValue(SourceNpcFormKey, out var available))
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

        // Show the spinner from the moment the tile first paints. LoadInitialImageAsync
        // clears it when a real curated mugshot loads (HasMugshot=true); otherwise
        // TriggerAsyncMugshotGeneration → GenerateMugshotAsync runs and its finally
        // block clears it. Without this, the spinner only appeared after the
        // ImagePacker.PackingCompleted callback fired, leaving the user staring at
        // a blank tile during the heavy first-paint work.
        IsLoading = true;

        // Asynchronously load the initial image (placeholder or real) without blocking the constructor.
        // Wrapped in Task.Run so LoadInitialImageAsync's synchronous prefix
        // (TryGetExistingFreshAutoGenPath → PNG metadata reads + staleness
        // probes, ~5-10ms per tile) runs on the thread pool instead of the
        // dispatcher. Otherwise N tiles × ~10ms blocks the UI thread for
        // hundreds of ms-to-seconds at NPC-selection time.
        // When AutoGeneration outranks DownloadedMugshots in the user's priority,
        // skip pre-loading the curated bitmap so the priority loop can decide
        // first (avoiding a curated-then-autogen flicker, or curated "winning by
        // inertia" if the AutoGen render fails).
        _ = Task.Run(() => LoadInitialImageAsync(placeholderOnly: ShouldDeferCuratedLoad()));
        // --- END REPLACED SECTION ---

        this.WhenAnyValue(x => x.IsSelected)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isSelected => SetBorderAndTooltip(isSelected))
            .DisposeWith(Disposables);

        // Re-run the tooltip builder when the placeholder/real state flips so the
        // expected-paths block (appended only for placeholders) appears or clears
        // as the async image load resolves. HasMugshot is set off the UI thread.
        this.WhenAnyValue(x => x.HasMugshot)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => SetBorderAndTooltip(IsSelected))
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

        // Live tile: same eligibility as the 3D preview (the embedded viewport
        // resolves records + assets against this tile's mod the same way).
        ToggleLiveTileCommand = ReactiveCommand.Create(() =>
        {
            if (IsLiveTile) DeactivateLiveTile();
            else ActivateLiveTile();
        }, canShow3DPreview).DisposeWith(Disposables);

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

        GenerateMugshotCommand = ReactiveCommand
            .CreateFromTask(GenerateMugshotOnDemandAsync, Observable.Return(CanGenerateMugshot))
            .DisposeWith(Disposables);

        // VisibleMugshotSource is refreshed by the view when the tile's context menu
        // is about to open, not reactively off ImagePath — classifying costs a PNG
        // metadata read, which is not something to spend once per tile per image
        // swap across a gallery. These two only have to follow it once it lands.
        this.WhenAnyValue(x => x.VisibleMugshotSource)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(CanDeleteMugshot));
                this.RaisePropertyChanged(nameof(DeleteMugshotHeader));
            })
            .DisposeWith(Disposables);
        DeleteMugshotCommand = ReactiveCommand
            .CreateFromTask(DeleteVisibleMugshotAsync,
                this.WhenAnyValue(x => x.VisibleMugshotSource).Select(s => s != MugshotSourceType.None))
            .DisposeWith(Disposables);

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
        GenerateMugshotCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error generating mugshot: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);
        DeleteMugshotCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error deleting mugshot: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(Disposables);


        _consistencyProvider.NpcSelectionChanged
            .Where(args => args.NpcFormKey == _targetNpcFormKey)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(args =>
                IsSelected = (args.SelectedModName == ModName && args.SourceNpcFormKey.Equals(this.SourceNpcFormKey)))
            .DisposeWith(Disposables);

        SetBorderAndTooltip(IsSelected);

        InitializeShareSourceListener();

        Debug.WriteLine($"[NpcPerf] T+{VM_NpcSelectionBar.SelectionPerfSw.ElapsedMilliseconds}ms tile-ctor-done {ModName}");
    }

    /// <summary>True when AutoGeneration appears before DownloadedMugshots in
    /// <see cref="Settings.MugshotSourcePriority"/>. Drives the deferral of
    /// the curated-image load in <see cref="LoadInitialImageAsync"/> so the
    /// priority loop can render AutoGen first; the Downloaded branch then
    /// actively loads curated only if AutoGen (and FaceFinder, if also ahead)
    /// produce nothing.</summary>
    private bool ShouldDeferCuratedLoad()
    {
        // Use the effective priority — honours any per-NPC override the user
        // has set in the NPCs view, so an AutoGen-first override defers the
        // curated load just like an AutoGen-first Settings configuration.
        var priority = _vmNpcSelectionBar.GetEffectiveMugshotPriority();
        if (priority == null) return false;
        int autoGenIdx = priority.IndexOf(MugshotSourceType.AutoGeneration);
        int downloadedIdx = priority.IndexOf(MugshotSourceType.DownloadedMugshots);
        // An ENABLED LiveTile source ahead of Downloaded defers the curated
        // load for the same reason AutoGen does: the priority loop's LiveTile
        // branch should get first shot, and a curated image that flickered in
        // would "win by inertia" (HasMugshot short-circuits the loop).
        int liveTileIdx = _settings.EnableLiveTilesOnSelection
            ? priority.IndexOf(MugshotSourceType.LiveTile)
            : -1;
        if (liveTileIdx >= 0 && downloadedIdx >= 0 && liveTileIdx < downloadedIdx) return true;
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

        // Fast-path on NPC revisit: when the fresh VM has no curated mugshot,
        // check whether the TOP-priority generated source already has a fresh
        // file on disk. Loading it directly lets TriggerAsyncMugshotGeneration
        // skip this tile (HasMugshot=true), which otherwise wastes ~5s per
        // revisit walking FaceFinder's HTTP cache check + the renderer's
        // metadata staleness check just to land on the same fresh file. The
        // probes themselves gate on UsePortraitCreatorFallback /
        // UseFaceFinderFallback, so this no-ops when those features are off.
        //
        // Honour effective priority — including the per-NPC MugshotSourceOverride
        // — and stop after the first probable source we encounter. If that
        // source has no fresh asset, do NOT probe lower-priority probable
        // sources: the priority loop in GenerateMugshotAsync must still get
        // a turn to actively run the top source (render AG, download FF).
        // Otherwise an AG override falls through to a stale FF cache hit and
        // never generates the render the user asked for.
        if (!realMugshotExists && AssociatedModSetting != null)
        {
            foreach (var source in _vmNpcSelectionBar.GetEffectiveMugshotPriority())
            {
                // Curated is handled by the realMugshotExists branch above /
                // the priority loop's Downloaded step; skip over it.
                if (source == MugshotSourceType.DownloadedMugshots) continue;

                // Skip DISABLED generated sources entirely — they can't produce
                // anything in the priority loop either (its branches gate on the
                // same flags), so they must not consume the single "first
                // probable source" consultation below. Without this, the default
                // priority order [Downloaded, FaceFinder, AutoGeneration] with
                // FaceFinder disabled made the loop consult the inert FaceFinder
                // slot and break — the AutoGeneration probes below never ran on
                // any normal view, so every launch / NPC switch started at the
                // placeholder even with a perfectly fresh cached render on disk.
                // (The per-NPC AG override radio "fixed" it by moving
                // AutoGeneration to index 0, which is what gave this away.)
                if (source == MugshotSourceType.FaceFinder
                    && !_settings.UseFaceFinderFallback) continue;

                // LiveTile has no on-disk asset to probe. Disabled → inert,
                // fall through. Enabled (and this tile eligible) → it IS the
                // first probable source: the priority loop will activate the
                // viewport, so no lower-priority source may preempt it here.
                if (source == MugshotSourceType.LiveTile)
                {
                    if (!_settings.EnableLiveTilesOnSelection || !CanBeLiveTile) continue;
                    break;
                }

                if (source == MugshotSourceType.AutoGeneration)
                {
                    // AutoGeneration is probed even when the feature is OFF. The
                    // probes are pure file lookups, and an auto-generated PNG that
                    // already exists — whether from a period with the setting on, or
                    // from the tile's explicit "Generate Mugshot" command — should
                    // keep displaying. Only RENDERING is gated (see the priority
                    // loop in GenerateMugshotAsync).
                    bool autoGenEnabled = _settings.UsePortraitCreatorFallback;

                    // A pending forced regeneration (the user just clicked AG on
                    // a mugshot with missing assets) must not be short-circuited
                    // by a "fresh" cached PNG: freshness is judged from stamped
                    // render settings, which say nothing about the mod's asset
                    // scope — the input the user just changed. Skipping the probe
                    // drops us into the stale-display branch below, which shows
                    // the existing PNG immediately but leaves HasMugshot false, so
                    // the tile is still kicked and still reaches AutoGeneration.
                    bool forcePending = autoGenEnabled && ShouldForceAutoGenRegeneration();
                    if (!forcePending && _batchGenerator.TryGetExistingFreshAutoGenPath(
                            SourceNpcFormKey, AssociatedModSetting, out var freshAutoGen, _targetNpcFormKey))
                    {
                        // Fresh cached render — display it and let the tile skip
                        // regeneration entirely (HasMugshot=true).
                        ImagePath = freshAutoGen!;
                        realMugshotExists = true;
                        HasMugshot = true;
                    }
                    else if (_batchGenerator.TryGetExistingAutoGenPath(
                                 SourceNpcFormKey, AssociatedModSetting, out var staleAutoGen))
                    {
                        // A cached render EXISTS but the freshness probe judged it
                        // stale. Display it now instead of the placeholder: the
                        // verdict may be genuine (a settings / renderer / schema
                        // change since the PNG was stamped) or spurious (the
                        // outfit/wig identity inputs weren't fully resolvable at
                        // probe time — e.g. Settings.ModSettings not yet synced),
                        // and in both cases an existing render beats a placeholder.
                        // HasMugshot stays FALSE so the priority loop still runs
                        // AutoGeneration: RunSelectedRendererAsync re-checks
                        // staleness and either re-confirms this same file
                        // (AlreadyCurrent) or renders and swaps in the fresh PNG.
                        ImagePath = staleAutoGen!;
                        realMugshotExists = true;
                        if (autoGenEnabled)
                        {
                            // HasMugshot intentionally left false — regeneration must run.
                            _eventLogger.Log(
                                $"Displaying existing (stale-flagged) cached mugshot for {ModName}; regeneration will run in the background",
                                "IMAGE_LOAD");
                        }
                        else
                        {
                            // Nothing will re-render it, so this file is final —
                            // claim it so the priority loop doesn't keep looking.
                            HasMugshot = true;
                        }
                    }
                    else if (!autoGenEnabled)
                    {
                        // Nothing on disk and, with the feature off, nothing will
                        // produce it. A source that cannot deliver must not consume
                        // the single "first probable source" consultation below —
                        // fall through to the next source instead.
                        continue;
                    }
                }
                else if (source == MugshotSourceType.FaceFinder
                    && _batchGenerator.TryGetExistingFreshFaceFinderPath(
                           SourceNpcFormKey, ModName, out var freshFf))
                {
                    ImagePath = freshFf!;
                    realMugshotExists = true;
                    HasMugshot = true;
                }

                // First probable source has been consulted (hit or miss).
                // Stop — lower-priority probable sources must not preempt
                // the priority loop.
                break;
            }
        }

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
        long bitmapStartMs = VM_NpcSelectionBar.SelectionPerfSw.ElapsedMilliseconds;
        Debug.WriteLine($"[NpcPerf] T+{bitmapStartMs}ms bitmap-decode-start {ModName} realMugshot={realMugshotExists}");
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
            List<string> physicsNotices = new();
            List<string> missingOutfitAssets = new();
            List<string> dataFolderAssets = new();
            string? faceGenMismatch = null;
            if (tryReadAssetMeta)
            {
                var json = MugshotPngMetadata.TryRead(pathToLoad);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    InternalMugshotMetadata.TryReadMissingAssets(json, out meshes, out textures);
                    faceGenMismatch = InternalMugshotMetadata.TryReadFaceGenMismatch(json);
                    physicsNotices = InternalMugshotMetadata.TryReadPhysicsConfigNotices(json);
                    missingOutfitAssets = InternalMugshotMetadata.TryReadMissingOutfitAssets(json);
                    dataFolderAssets = InternalMugshotMetadata.TryReadDataFolderAssets(json);
                }
            }

            // FaceGen analysis lives inside this Task.Run so it shares the
            // worker thread with the bitmap+metadata load — one thread-pool
            // ticket per tile instead of two. The cache handles SHA-keyed
            // dedup so this is a no-op on cache hits (~sub-ms).
            NifMeshBuilder.FaceGenStats? facegen = FetchFaceGenStatsSync();

            // Outfit-conflict notice (Include Outfit vs SkyPatcher/SPID) —
            // computed live from current configs, applies to any real mugshot
            // since it describes the runtime outcome, not the PNG's pedigree.
            string outfitNotice = ComputeOutfitNoticeSafe();

            // Antler-removal notice (effective Antler Handling Mode = Remove and
            // this NPC actually carries a strippable antler) — computed live like
            // the outfit notice; describes the patched outcome, not the PNG.
            // (The wig notice is NOT computed here: it is construction-time
            // scan data — see InitializeWigNotice.)
            string antlerNotice = ComputeAntlerRemovalNoticeSafe();

            // Wig persistence, now including the runtime outfit-replacement check the
            // constructor skipped (it simulates SPID/SkyPatcher, so it runs here).
            var wigPersistence = ComputeWigPersistenceSafe(includeRuntimeReplacement: true);

            return (bitmap, meshes, textures, physicsNotices, missingOutfitAssets, dataFolderAssets, facegen, faceGenMismatch, outfitNotice, antlerNotice, wigPersistence);
        });

        // Always apply (even with empty lists) so a re-load of a tile whose
        // PNG was regenerated without missing assets clears any stale
        // overlay state from the in-memory VM.
        if (tryReadAssetMeta)
        {
            ApplyMissingAssetNotifications(loadResult.meshes, loadResult.textures, loadResult.faceGenMismatch);
            ApplyOutfitAssetNotices(loadResult.missingOutfitAssets, loadResult.physicsNotices);
            ApplyDataFolderAssetNotices(loadResult.dataFolderAssets);
        }

        OutfitNoticeText = loadResult.outfitNotice;
        HasOutfitNotice = loadResult.outfitNotice.Length > 0;

        AntlerRemovalNoticeText = loadResult.antlerNotice;
        HasAntlerRemovalNotice = loadResult.antlerNotice.Length > 0;

        ApplyWigPersistence(loadResult.wigPersistence);

        // FaceGen stats (if any) — set after the await so it lands on the
        // UI thread, triggering the reactive overlay-state refresh.
        if (loadResult.facegen.HasValue)
        {
            FaceGenStats = loadResult.facegen;
        }

        long bitmapEndMs = VM_NpcSelectionBar.SelectionPerfSw.ElapsedMilliseconds;
        Debug.WriteLine($"[NpcPerf] T+{bitmapEndMs}ms bitmap-decode-end {ModName} took={bitmapEndMs - bitmapStartMs}ms gotBitmap={loadResult.bitmap != null}");

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

            Debug.WriteLine($"[NpcPerf] T+{VM_NpcSelectionBar.SelectionPerfSw.ElapsedMilliseconds}ms MugshotSource SET {ModName}");

            if (realMugshotExists)
            {
                _eventLogger.Log($"Successfully loaded real mugshot for {ModName} from {pathToLoad}", "IMAGE_LOAD");
            }

            // Dimensions are now set — ask the selection bar to (re-)pack. The
            // decode above ran off the UI thread and may have finished after the
            // NPC-switch re-pack already fired against an all-0×0 tile set, so
            // without this nudge the tile can stay at full display size. Throttled
            // on the VM side, so N tiles loading in a burst cause a single re-pack.
            _vmNpcSelectionBar.NotifyTileImageReady();
        }

        // HasMugshot=true means TriggerAsyncMugshotGeneration will skip this tile,
        // so nothing else is coming to clear the spinner — turn it off here.
        // When HasMugshot is false, GenerateMugshotAsync will run and clear
        // IsLoading in its finally block.
        if (HasMugshot)
        {
            IsLoading = false;
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

    /// <summary>Spectrum-mode counterpart to <see cref="ApplyFaceGenOutlierColors"/>:
    /// the coordinator has already interpolated each metric's gradient color, so we
    /// just assign. Clears the "outlier" booleans since every tile is colored.</summary>
    public void ApplyFaceGenSpectrumColors(SolidColorBrush sizeBrush, SolidColorBrush polyBrush,
        SolidColorBrush vertBrush, SolidColorBrush indicatorBrush)
    {
        IsFaceGenSizeOutlier = false;
        IsFaceGenPolyOutlier = false;
        IsFaceGenVertOutlier = false;
        FaceGenSizeColor = sizeBrush;
        FaceGenPolyColor = polyBrush;
        FaceGenVertColor = vertBrush;
        FaceGenIndicatorColor = indicatorBrush;
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
            // Popup attire toggles are non-persistent overrides of the Settings-
            // tab defaults — seeded from them, but never written back.
            inner.PersistAttireToggles = false;
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
                try { await inner.LoadAsync(SourceNpcFormKey, modSetting, _targetNpcFormKey); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Show3DPreview: LoadAsync failed: {ExceptionLogger.GetExceptionStack(ex)}");
                }
            };
            // Non-modal Show() so the main UI stays interactive and the
            // preview gets its own taskbar entry (ShowInTaskbar=True on the
            // window). Owner ties the preview to app lifecycle + keeps
            // CenterOwner positioning. Application.Current.MainWindow is
            // unreliable here (see VM_ModSetting.ShowMissingPluginsWindow)
            // and has been observed to return the freshly-resolved preview
            // itself, so search the live windows and exclude self.
            var loadedOtherWindows = Application.Current?.Windows
                .OfType<Window>()
                .Where(w => w != window && w.IsLoaded)
                .ToList();
            window.Owner = loadedOtherWindows?.FirstOrDefault(w => w.IsActive)
                           ?? loadedOtherWindows?.FirstOrDefault();
            // Dispose moves to Closed since Show() returns immediately.
            // FullScreen3DPreviewView.OnClosed already disposed the inner VM
            // with the popup's own GL context current (the GL objects must be
            // deleted in the context that minted them, or a sibling preview
            // loses its identically-numbered ones). This is the idempotent
            // safety net for any host that doesn't do that.
            window.Closed += (_, _) => inner.Dispose();
            window.Show();
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError(
                $"Failed to open 3D preview:\n{ExceptionLogger.GetExceptionStack(ex)}");
        }
    }

    // -------- Live Tile activation / deactivation --------

    /// <summary>
    /// Swaps this tile's static image for an embedded 3D viewport. Creates a
    /// per-tile transient <see cref="VM_InternalMugshotPreview"/> (same factory
    /// and load call as the 3D preview popup, scoped to THIS tile's mod) and
    /// flips <see cref="IsLiveTile"/>; the tile template shows
    /// UC_LiveTileViewport bound to <see cref="LiveTilePreview"/> and hides the
    /// Image + badge overlays. Must run on the UI thread (the preview VM's
    /// ctor wires WPF-affine state). No-op when already live or ineligible.
    /// Deliberately TRANSIENT state: it survives tab switches (the tile
    /// collection is not rebuilt) but is forgotten on NPC switch and app
    /// restart, so nobody pays a surprise re-render cost — the only automatic
    /// activation path is the LiveTile mugshot source, which the user opts
    /// into via "Enable Live Tiles on Selection".
    /// </summary>
    public void ActivateLiveTile()
    {
        if (LiveTilePreview != null) return;
        if (!CanBeLiveTile) return;
        try
        {
            var inner = _internalPreviewFactory();
            // Attire toggles shown in the full preview are absent here, but the
            // flag also gates settings write-back on load — keep it off like
            // the popup does.
            inner.PersistAttireToggles = false;
            var modSetting = AssociatedModSetting!.SaveToModel();

            LiveTilePreview = inner;
            IsLiveTile = true;
            // The live tile renders itself (with its own loading dim); the
            // static-path spinner would just sit on top of the viewport.
            IsLoading = false;

            // Re-pack: the Original* getters now report the mugshot output
            // dimensions, so the packer sizes this tile at the output aspect.
            _vmNpcSelectionBar.NotifyTileImageReady();

            _ = LoadLiveTileAsync(inner, modSetting);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"ActivateLiveTile failed for {ModName}: {ExceptionLogger.GetExceptionStack(ex)}");
            DeactivateLiveTile();
        }
    }

    private async Task LoadLiveTileAsync(VM_InternalMugshotPreview inner, ModSetting modSetting)
    {
        try
        {
            await inner.LoadAsync(SourceNpcFormKey, modSetting, _targetNpcFormKey);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Live tile LoadAsync failed for {ModName}: {ExceptionLogger.GetExceptionStack(ex)}");
        }
    }

    /// <summary>
    /// Returns this tile to its static mugshot. The viewport UC is asked to
    /// tear down GL with its own context current BEFORE the preview VM
    /// reference is cleared — clearing first would detach the UC's
    /// ViewShutdownRequested handler and force a context-less dispose (the
    /// documented cross-context GL.Delete hazard). When no view ever attached
    /// (activation on a hidden/filtered tile), the VM never created GL objects
    /// and is disposed directly.
    /// </summary>
    public void DeactivateLiveTile()
    {
        var preview = LiveTilePreview;
        if (preview == null && !IsLiveTile) return;

        IsLiveTile = false;
        if (preview != null && !preview.RequestViewShutdown())
        {
            preview.Dispose();
        }
        LiveTilePreview = null;

        // Original* getters report the stored image dimensions again.
        _vmNpcSelectionBar.NotifyTileImageReady();
    }

    /// <summary>
    /// Builds an independent live-preview VM for this tile's appearance — used
    /// by the Compare window so a live tile's analogue there is also live. A
    /// FRESH VM is required: one viewer VM can never be bound by two
    /// GLWpfControls at once (each control mints a private GL context and the
    /// VM tracks one set of GL object IDs). Copies the on-tile viewport's
    /// current camera pose so the comparison opens at the same angle the user
    /// is looking at. Fires the load and returns immediately; the CALLER owns
    /// disposal (RequestViewShutdown, falling back to Dispose).
    /// </summary>
    public VM_InternalMugshotPreview? CreateDetachedLivePreview()
    {
        if (!CanBeLiveTile) return null;
        try
        {
            var inner = _internalPreviewFactory();
            inner.PersistAttireToggles = false;
            if (LiveTilePreview?.Viewer.Camera is { } cam)
            {
                inner.InitialCameraPose = (cam.Azimuth, cam.Elevation, cam.Distance,
                    cam.Target.X, cam.Target.Y, cam.Target.Z);
            }
            var modSetting = AssociatedModSetting!.SaveToModel();
            _ = LoadLiveTileAsync(inner, modSetting);
            return inner;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"CreateDetachedLivePreview failed for {ModName}: {ExceptionLogger.GetExceptionStack(ex)}");
            return null;
        }
    }

    /// <summary>LiveTile branch of the mugshot-source priority loop. Runs on
    /// the generation worker thread, so the activation itself is marshalled to
    /// the UI thread. Returns true ("handled") when the tile went live.</summary>
    private bool TryActivateLiveTileSource()
    {
        if (!CanBeLiveTile) return false;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(ActivateLiveTile);
        }
        else
        {
            ActivateLiveTile();
        }
        return IsLiveTile;
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

        // Placeholder tiles additionally list where NPC2 looks for an image from
        // each source, so the user knows where to drop a curated image or find a
        // cached / auto-generated one.
        if (!HasMugshot)
        {
            ToolTipString += "\n\n" + BuildExpectedPathsTooltip();
        }
    }

    /// <summary>Builds the placeholder tooltip body listing where NPC2 looks
    /// for (and writes) this NPC's mugshot under each source. Mirrors the path
    /// conventions used by the priority loop in <see cref="GenerateMugshotAsync"/>
    /// and <see cref="BatchMugshotGenerator"/>: curated images live under the
    /// mod's MugShotFolderPaths, FaceFinder images in the FaceFinder cache, and
    /// auto-generated images in the AutoGen folder — all keyed by
    /// <c>&lt;Plugin&gt;\{FormID:X8}</c>.</summary>
    private string BuildExpectedPathsTooltip()
    {
        var modKey = SourceNpcFormKey.ModKey.ToString();
        var fileStem = $"{SourceNpcFormKey.ID:X8}";
        var sb = new StringBuilder();

        sb.Append("Expected image locations:");

        // Curated (user-supplied) mugshots: <MugshotFolder>\<Plugin>\<FormID>.png
        sb.Append("\n\nCurated:");
        var mugFolders = AssociatedModSetting?.MugShotFolderPaths;
        if (mugFolders != null && mugFolders.Count > 0)
        {
            foreach (var folder in mugFolders)
            {
                sb.Append('\n').Append(Path.Combine(folder, modKey, $"{fileStem}.png"));
            }
        }
        else if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder))
        {
            // No curated folder is linked yet (none exists on disk at
            // <MugshotsFolder>\<ModName>), so show the conventional location NPC2
            // would discover a curated image at if one were dropped there.
            sb.Append('\n').Append(Path.Combine(
                _settings.MugshotsFolder, ModName, modKey, $"{fileStem}.png"));
        }
        else
        {
            sb.Append("\n(no Mugshots folder configured in Settings)");
        }

        // FaceFinder cache: <FaceFinderCache>\<ModName>\<Plugin>\<FormID>.<ext>
        var faceFinderPath = Path.Combine(
            BatchMugshotGenerator.GetFaceFinderModFolder(_settings, ModName),
            modKey, $"{fileStem}.png");
        sb.Append("\n\nFaceFinder Cache:\n").Append(faceFinderPath);

        // Auto-generated: <AutoGenMugshots>\<ModName>\<Plugin>\<FormID>.png
        var autoGenPath = BatchMugshotGenerator.GetAutoGenSavePath(_settings, ModName, SourceNpcFormKey);
        sb.Append("\n\nAuto-Generated:\n").Append(autoGenPath);

        return sb.ToString();
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

        // Call back to the parent VM_ModSetting to handle the logic. Keyed on the appearance
        // DONOR (SourceNpcFormKey), not the target, so the choice lands on the same
        // disambiguation entry the Validator/Patcher read when splicing the donor's record.
        // For a normal replacer the two FormKeys are identical; for a guest/shared appearance
        // they differ and only the donor key has any effect at patch time.
        bool successfullyUpdated = AssociatedModSetting.SetSingleNpcSourcePlugin(SourceNpcFormKey, selectedPluginKey);

        if (successfullyUpdated)
        {
            // The parent VM_ModSetting has updated its NpcPluginDisambiguation map.
            // Now, this specific VM_NpcsMenuMugshot instance should update its own CurrentSourcePlugin
            // to reflect the new choice for the context menu checkmark.
            if (AssociatedModSetting.NpcPluginDisambiguation.TryGetValue(this.SourceNpcFormKey,
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
        long genStartMs = VM_NpcSelectionBar.SelectionPerfSw.ElapsedMilliseconds;
        Debug.WriteLine($"[NpcPerf] T+{genStartMs}ms GenerateMugshotAsync ENTER {ModName}");
        IsGenerationInFlight = true; // idempotent re-set; kick site set it already
        try
        {
            // A live tile renders itself — no image to produce or display.
            if (IsLiveTile) return;

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

            // Walk the effective mugshot-source priority order — honours any
            // per-NPC override set via the radio buttons in the NPCs view, then
            // falls back to Settings.MugshotSourcePriority. The first source
            // that produces a result wins; a source with nothing to offer (no
            // curated mugshot loaded, FaceFinder off, auto-generation off with
            // no render on disk) reports "not handled" so the loop falls through
            // to the next source.
            foreach (var source in _vmNpcSelectionBar.GetEffectiveMugshotPriority())
            {
                if (token.IsCancellationRequested) return;

                bool handled = source switch
                {
                    MugshotSourceType.DownloadedMugshots => deferCurated
                                                            ? TryLoadCuratedMugshot()
                                                            : HasMugshot,
                    MugshotSourceType.FaceFinder         => _settings.UseFaceFinderFallback
                                                            && await TryFaceFinderSourceAsync(token),
                    // Auto-generation OFF still gets a turn — as a display-only
                    // source. It can't render anything, but an auto-generated PNG
                    // already on disk is a perfectly good mugshot and beats the
                    // placeholder (see TryDisplayExistingAutoGen).
                    MugshotSourceType.AutoGeneration     => _settings.UsePortraitCreatorFallback
                                                            ? await TryAutoGenerationSourceAsync(token)
                                                            : TryDisplayExistingAutoGen(),
                    MugshotSourceType.LiveTile           => _settings.EnableLiveTilesOnSelection
                                                            && TryActivateLiveTileSource(),
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
            long genEndMs = VM_NpcSelectionBar.SelectionPerfSw.ElapsedMilliseconds;
            Debug.WriteLine($"[NpcPerf] T+{genEndMs}ms GenerateMugshotAsync EXIT {ModName} took={genEndMs - genStartMs}ms hasMugshot={HasMugshot}");

            // Only drop the spinner when this task actually finished its work.
            // TriggerAsyncMugshotGeneration cancels the entire in-flight batch
            // every time it re-runs (the 50ms CurrentNpcAppearanceMods backstop
            // firing just after the 100ms PackingCompleted trigger is the common
            // case) and then immediately re-kicks a fresh GenerateMugshotAsync
            // for every still-imageless tile. Clearing IsLoading on the cancelled
            // task dropped the spinner with no image; the re-kicked render then
            // painted the image ~5s later — the gap the user observed. Leaving
            // the spinner up on cancellation hands it off to the successor task,
            // which clears it when it assigns the image. The HasMugshot guard
            // covers the race where the image was assigned just as cancellation
            // fired: clear regardless so a tile that already has an image can
            // never be left spinning (the re-kick skips imaged tiles).
            bool cancelled = token.IsCancellationRequested;
            if (!cancelled || HasMugshot)
            {
                IsLoading = false;
            }

            // Always release the in-flight latch, whatever the outcome, so the
            // trigger can re-kick this tile if it still has no image.
            IsGenerationInFlight = false;
        }
    }

    /// <summary>Whether this tile owes a forced re-render: the user activated the
    /// AG source override AND this tile's cached render actually records missing
    /// assets.
    /// <para>The missing-asset condition is what keeps the button proportionate.
    /// AG promotes a source for the whole row, but the repair it stands in for is
    /// per-tile — the user fixed one mod's folders. Forcing every tile would
    /// re-render a row of intact mugshots at seconds each and change none of
    /// them. A tile with no cached PNG needs no force either: a missing file is
    /// already stale, so the normal path renders it.</para>
    /// <para>Consulted twice per generation pass (fast-path suppression, then the
    /// render itself), so it reads the same PNG twice rather than caching — the
    /// two callers must agree, and the file only changes once the render this
    /// gate authorises has finished.</para></summary>
    private bool ShouldForceAutoGenRegeneration()
    {
        if (AssociatedModSetting == null) return false;
        if (!_vmNpcSelectionBar.IsForcedAutoGenRegenerationPending(this)) return false;
        return _batchGenerator.ExistingAutoGenHasMissingAssets(SourceNpcFormKey, AssociatedModSetting);
    }

    /// <summary>True when this tile is currently displaying an auto-generated
    /// image (as opposed to a curated mugshot, a FaceFinder cache hit, or a
    /// placeholder). Lets the host re-render only the autogen tiles after
    /// something invalidates them (e.g. a per-NPC attire override change).</summary>
    public bool IsShowingAutoGenImage => IsImageAutoGen();

    /// <summary>Forces this tile to re-run its source-priority loop after the
    /// existing autogen PNG was invalidated. Clears <see cref="HasMugshot"/> so
    /// the loop no longer short-circuits on the already-loaded image and reaches
    /// the AutoGeneration source, whose staleness check now sees the stamped
    /// attire flags differ and re-renders. Without this a displayed autogen tile
    /// (HasMugshot=true via LoadInitialImageAsync's fast-path) is skipped by both
    /// the priority loop and TriggerAsyncMugshotGeneration, so it would only
    /// refresh on an NPC switch-away-and-back (a fresh rebuild re-probes
    /// staleness).</summary>
    public Task RegenerateAsync(CancellationToken token)
    {
        HasMugshot = false;
        return GenerateMugshotAsync(token);
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

    /// <summary>Display-only auto-generation branch, used when
    /// <see cref="Settings.UsePortraitCreatorFallback"/> is off: shows an
    /// auto-generated PNG that already exists on disk without rendering anything.
    /// <para>Reached both by users who turned automatic generation off after
    /// building a library of renders and by users who never turn it on and render
    /// individual NPCs through the tile's Generate Mugshot command. Staleness is
    /// deliberately not consulted — nothing is going to re-render the file, so a
    /// stale-flagged render is still the best image available.</para>
    /// Returns true when an image was displayed.</summary>
    private bool TryDisplayExistingAutoGen()
    {
        if (AssociatedModSetting == null) return false;
        if (!_batchGenerator.TryGetExistingAutoGenPath(
                SourceNpcFormKey, AssociatedModSetting, out var existing)
            || existing == null)
        {
            return false;
        }

        // Restore the stamped overlay state the render recorded, the same way
        // RunSelectedRendererAsync's AlreadyCurrent branch does — otherwise the
        // missing-asset badges would vanish whenever generation is switched off.
        var json = MugshotPngMetadata.TryRead(existing);
        if (!string.IsNullOrWhiteSpace(json))
        {
            InternalMugshotMetadata.TryReadMissingAssets(json, out var meshes, out var textures);
            ApplyMissingAssetNotifications(
                meshes, textures, InternalMugshotMetadata.TryReadFaceGenMismatch(json));
            ApplyOutfitAssetNotices(
                InternalMugshotMetadata.TryReadMissingOutfitAssets(json),
                InternalMugshotMetadata.TryReadPhysicsConfigNotices(json));
            ApplyDataFolderAssetNotices(InternalMugshotMetadata.TryReadDataFolderAssets(json));
            _ = RefreshTileNoticesAsync();
        }

        SetImageSource(existing);
        return true;
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

        // Consume-on-success, not on entry: a render cancelled by the trigger
        // churn documented in GenerateMugshotAsync's finally would otherwise burn
        // the tile's one forced attempt and let the re-kick reuse the stale PNG.
        bool force = ShouldForceAutoGenRegeneration();
        if (force)
        {
            _eventLogger.Log($"Forcing re-render for {SourceNpcFormKey} from {ModName} " +
                             "(AG override; cached render is missing assets)", "PORTRAIT_GEN");
        }

        var rendererResult = await _batchGenerator.RunSelectedRendererAsync(
            SourceNpcFormKey, AssociatedModSetting, token, targetNpcFormKey: _targetNpcFormKey,
            forceRegenerate: force);

        if (force && rendererResult.Generated)
        {
            _vmNpcSelectionBar.MarkForcedAutoGenRegenerationServed(this);
        }

        // The Internal renderer reports per-render missing-asset paths whether
        // it just rendered or reused a fresh PNG: in the Generated branch the
        // arrays come from the renderer pass; in the AlreadyCurrent branch
        // RunSelectedRendererAsync reads them back from the PNG's stamped JSON
        // metadata so this call point can apply them either way. Post-2.1.7
        // the autogen folder is no longer in MugShotFolderPaths, so a
        // freshly-created VM enters here with ImagePath empty and
        // LoadInitialImageAsync's metadata-read path can't pre-load the
        // overlay state — applying on ProducedFile (Generated || AlreadyCurrent)
        // restores it on every revisit.
        if (rendererResult.Source == GenerationSource.InternalRenderer && rendererResult.ProducedFile)
        {
            ApplyMissingAssetNotifications(rendererResult.MissingMeshes, rendererResult.MissingTextures, rendererResult.FaceGenMismatch);
            ApplyOutfitAssetNotices(rendererResult.MissingOutfitAssets, rendererResult.PhysicsConfigNotices);
            ApplyDataFolderAssetNotices(rendererResult.DataFolderAssets);
            _ = RefreshTileNoticesAsync();
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

    /// <summary>Reclassifies the on-screen image into one of the three mugshot
    /// sources. Called from the view just before the tile's context menu opens
    /// rather than reactively: the auto-generated test reads the PNG's tEXt chunk,
    /// which is far too expensive to run per tile on every image swap in a gallery
    /// that routinely holds dozens.</summary>
    public void RefreshVisibleMugshotSource()
    {
        VisibleMugshotSource = ClassifyVisibleImage();
    }

    private MugshotSourceType ClassifyVisibleImage()
    {
        // A live tile displays no image file at all — nothing to delete.
        if (IsLiveTile) return MugshotSourceType.None;

        // "in-memory" (a FaceFinder hit with caching off) and the bundled
        // placeholder both fail here, which is what we want — there is no file
        // to delete in either case.
        if (string.IsNullOrWhiteSpace(ImagePath)
            || string.Equals(ImagePath, FullPlaceholderPath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(ImagePath))
        {
            return MugshotSourceType.None;
        }

        if (IsImageAutoGen()) return MugshotSourceType.AutoGeneration;
        if (IsImageFaceFinderCache()) return MugshotSourceType.FaceFinder;
        return MugshotSourceType.DownloadedMugshots;
    }

    /// <summary>Renders this one tile on demand, bypassing
    /// <see cref="Settings.UsePortraitCreatorFallback"/>. The point of the feature
    /// is to render a single NPC without the round trip through Settings to switch
    /// automatic generation on and back off again, so the setting cannot gate it.
    /// <para>Always forced: the user asked for this specific render, and the
    /// staleness checker compares stamped render SETTINGS, so it would answer
    /// "already current" for the very cases people reach for this — a mod whose
    /// folders were just fixed. Forcing also re-indexes that mod's BSAs.</para></summary>
    private async Task GenerateMugshotOnDemandAsync()
    {
        if (AssociatedModSetting == null || !CanGenerateMugshot) return;

        // The renderer builds its per-mod asset scope from the PERSISTED model, so
        // push VM_Mods' in-memory list down first or a folder added this session
        // contributes nothing. Same reason ArmForcedAutoGenRegeneration does it.
        // Stays on the UI thread — it walks VM_Mods' observable collections.
        _vmNpcSelectionBar.SyncModSettingsForRender();

        IsLoading = true;
        try
        {
            _eventLogger.Log($"Manual mugshot generation requested for {SourceNpcFormKey} from {ModName}", "PORTRAIT_GEN");

            // MUST run off the dispatcher. RunSelectedRendererAsync has a long
            // synchronous prefix, and under forceRegenerate it reaches
            // NpcChooserBsaProviderAdapter.RefreshArchivesForMod, which blocks on
            // BsaHandler.AddMissingModToCache via GetAwaiter().GetResult().
            // Historically that hung the app outright — AddMissingModToCache
            // awaited without ConfigureAwait(false) and tried to resume on the very
            // thread the blocking call was holding. BsaHandler now uses
            // ConfigureAwait(false) on both awaits, so the hang is gone, but the
            // Task.Run still has to stay: the synchronous prefix plus the BSA
            // re-index would otherwise freeze the UI for seconds. This is why every
            // generation call site wraps the tile's work in Task.Run (see
            // TriggerAsyncMugshotGeneration). The continuation lands back on the
            // dispatcher, which the message box below requires.
            var result = await Task.Run(() => _batchGenerator.RunSelectedRendererAsync(
                SourceNpcFormKey, AssociatedModSetting, CancellationToken.None,
                targetNpcFormKey: _targetNpcFormKey,
                forceRegenerate: true,
                bypassSourceGate: true));

            if (result.ProducedFile && result.OutputPath != null)
            {
                if (result.Source == GenerationSource.InternalRenderer)
                {
                    ApplyMissingAssetNotifications(
                        result.MissingMeshes, result.MissingTextures, result.FaceGenMismatch);
                    ApplyOutfitAssetNotices(result.MissingOutfitAssets, result.PhysicsConfigNotices);
                    ApplyDataFolderAssetNotices(result.DataFolderAssets);
                    _ = RefreshTileNoticesAsync();
                }

                SetImageSource(result.OutputPath);
                RefreshVisibleMugshotSource();
                _eventLogger.Log($"Manual mugshot generation succeeded for {SourceNpcFormKey}", "PORTRAIT_GEN");
            }
            else
            {
                ScrollableMessageBox.ShowWarning(
                    $"Could not generate a mugshot for {TargetDisplayName} from {ModName}." + Environment.NewLine +
                    "The renderer produced no image — check the render log next to the exe for details.",
                    "Mugshot Generation Failed");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Deletes the image the tile is currently displaying — whichever of
    /// the three sources it came from — then re-runs the source-priority loop so
    /// whatever ranks next (or the placeholder) takes its place.
    /// <para>Also prunes the now-empty folders above the file, up to but never
    /// including that source's configured root, and forgets the path from the
    /// persisted cache that tracks it, so the two "Delete All" commands in Settings
    /// don't keep counting a file that is gone.</para></summary>
    private async Task DeleteVisibleMugshotAsync()
    {
        var source = VisibleMugshotSource;
        var path = ImagePath;
        if (source == MugshotSourceType.None || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            RefreshVisibleMugshotSource();
            return;
        }

        string root = source switch
        {
            MugshotSourceType.DownloadedMugshots => ResolveCuratedPruneRoot(path),
            MugshotSourceType.FaceFinder         => Settings.GetEffectiveFaceFinderMugshotsFolder(_settings),
            MugshotSourceType.AutoGeneration     => Settings.GetEffectiveAutogenMugshotsFolder(_settings),
            _                                    => string.Empty,
        };

        string consequence = source switch
        {
            MugshotSourceType.DownloadedMugshots =>
                "This is an image you downloaded. N.P.C.2 cannot recreate it — you would have to get it from the mugshot pack again.",
            MugshotSourceType.FaceFinder =>
                "It will be downloaded again the next time this NPC needs a mugshot, as long as FaceFinder is enabled.",
            MugshotSourceType.AutoGeneration when _settings.UsePortraitCreatorFallback =>
                "Auto-Generate missing mugshots is currently ON, so this NPC will be re-rendered right away.",
            MugshotSourceType.AutoGeneration =>
                "You can render it again at any time with Generate Mugshot.",
            _ => string.Empty,
        };

        if (!ScrollableMessageBox.Confirm(
                $"Delete this mugshot for {TargetDisplayName} from {ModName}?" + Environment.NewLine + Environment.NewLine +
                path + Environment.NewLine + Environment.NewLine + consequence,
                "Delete Mugshot",
                displayImagePath: path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError(
                $"Could not delete '{path}':{Environment.NewLine}{ExceptionLogger.GetExceptionStack(ex)}");
            return;
        }

        // FaceFinder images carry a sidecar holding the server timestamp the
        // staleness check compares against. Leaving it behind would make the next
        // download look at metadata describing a file that no longer exists.
        if (source == MugshotSourceType.FaceFinder)
        {
            var sidecar = path + FaceFinderClient.MetadataFileExtension;
            try
            {
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not delete FaceFinder sidecar '{sidecar}': {ex.Message}");
            }
        }

        switch (source)
        {
            case MugshotSourceType.AutoGeneration:
                _tracker.Untrack(path);
                break;
            case MugshotSourceType.FaceFinder:
                _faceFinderTracker.Untrack(path);
                break;
            case MugshotSourceType.DownloadedMugshots:
                // The curated index is scanned once at startup; without this the
                // reload below would resolve straight back to the deleted file.
                _vmNpcSelectionBar.ForgetCuratedMugshotPath(path);
                break;
        }

        PruneEmptyFoldersUpTo(Path.GetDirectoryName(path), root);

        _eventLogger.Log(
            $"Deleted {VM_MugshotSourcePriorityItem.GetDisplayName(source)} mugshot for {SourceNpcFormKey} ({ModName}): {path}",
            "MUGSHOT_DELETE");

        await ReloadAfterMugshotDeletedAsync();
    }

    /// <summary>Prune boundary for a curated mugshot. Normally the Mugshots Folder,
    /// so the per-mod and per-plugin folders under it can go but the root itself
    /// can't. A mugshot folder linked from somewhere else entirely (drag-drop can
    /// register any folder on a mod's MugShotFolderPaths) isn't under that root at
    /// all, so use the registered folder's own parent instead — that prunes the same
    /// two levels without ever climbing above a folder the user pointed us at.
    /// Returns empty when neither applies, which disables pruning rather than
    /// guessing at a boundary.</summary>
    private string ResolveCuratedPruneRoot(string imagePath)
    {
        var registered = AssociatedModSetting?.MugShotFolderPaths
            .FirstOrDefault(p => MugshotFolderPruner.Contains(p, imagePath));
        if (!string.IsNullOrWhiteSpace(registered))
        {
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered)));
            if (!string.IsNullOrWhiteSpace(parent)) return parent;
        }

        return MugshotFolderPruner.Contains(_settings.MugshotsFolder, imagePath)
            ? _settings.MugshotsFolder
            : string.Empty;
    }

    /// <summary>Deletes the folders the removed image left empty, then drops any of
    /// them the mod was still pointing at — a registered mugshot folder that no
    /// longer exists would otherwise keep the mod looking like it has mugshots.</summary>
    private void PruneEmptyFoldersUpTo(string? startDir, string root)
    {
        var deleted = MugshotFolderPruner.Prune(startDir, root);
        if (deleted.Count == 0 || AssociatedModSetting == null) return;

        var stale = AssociatedModSetting.MugShotFolderPaths
            .Where(p => !string.IsNullOrWhiteSpace(p)
                        && deleted.Any(d => Path.TrimEndingDirectorySeparator(p)
                            .Equals(d, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var path in stale)
        {
            AssociatedModSetting.MugShotFolderPaths.Remove(path);
            Debug.WriteLine($"Unregistered now-deleted mugshot folder '{path}' from {ModName}.");
        }
    }

    /// <summary>Clears the tile back to "no image known" and re-runs the normal
    /// source-priority resolution, so whatever ranks below the deleted image — a
    /// curated mugshot, a FaceFinder download, a render, or the placeholder — takes
    /// over. Resets the one-shot load latch, which would otherwise make
    /// <see cref="LoadInitialImageAsync"/> return immediately.</summary>
    private async Task ReloadAfterMugshotDeletedAsync()
    {
        MugshotSource = null;
        HasMugshot = false;
        ApplyMissingAssetNotifications(Array.Empty<string>(), Array.Empty<string>());
        ApplyOutfitAssetNotices(null, null);
        ApplyDataFolderAssetNotices(null);

        // Re-resolve the curated path from the (now-pruned) startup index rather
        // than keeping the deleted one.
        ImagePath = _vmNpcSelectionBar.GetMugshotPathForNpc(ModName, SourceNpcFormKey, _targetNpcFormKey)
                    ?? string.Empty;

        lock (_imageLoadLock)
        {
            _isImageLoadingOrLoaded = false;
        }

        // Off the dispatcher, for the same reason TriggerAsyncMugshotGeneration
        // does it: the priority loop's AutoGeneration branch runs the renderer's
        // long synchronous prefix inline, which would freeze the UI for the whole
        // re-render this delete just made necessary.
        await Task.Run(() => GenerateMugshotAsync(CancellationToken.None));
        RefreshVisibleMugshotSource();
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
        IReadOnlyList<string> missingTextures,
        string? faceGenMismatch = null)
    {
        bool hasMeshes = missingMeshes != null && missingMeshes.Count > 0;
        bool hasTextures = missingTextures != null && missingTextures.Count > 0;
        bool hasFaceGen = !string.IsNullOrWhiteSpace(faceGenMismatch);
        if ((!hasMeshes && !hasTextures && !hasFaceGen)
            || !_settings.InternalMugshot.ShowMissingNpcAssetsIcon)
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
        if (hasFaceGen)
        {
            if (hasMeshes || hasTextures) sb.Append("\n\n");
            sb.Append(faceGenMismatch);
        }

        HasMissingAssets = true;
        MissingAssetNotificationText = sb.ToString();
    }

    /// <summary>Sets the outfit-asset badge from render output or stamped
    /// metadata: missing outfit/headgear meshes+textures (re-render-eligible) and/or
    /// stale-physics-config links (informational). Always applied (even with both
    /// empty) so regenerating a fixed mod clears a previous notice.</summary>
    private void ApplyOutfitAssetNotices(
        IReadOnlyList<string>? missingOutfitAssets,
        IReadOnlyList<string>? physicsNotices)
    {
        bool hasAssets = missingOutfitAssets is { Count: > 0 };
        bool hasPhysics = physicsNotices is { Count: > 0 };
        if ((!hasAssets && !hasPhysics)
            || !_settings.InternalMugshot.ShowMissingOutfitAssetsIcon)
        {
            HasMissingOutfitAssets = false;
            MissingOutfitAssetsText = string.Empty;
            return;
        }

        var sb = new StringBuilder();
        if (hasAssets)
        {
            sb.Append("The following outfit assets could not be found:");
            foreach (var p in missingOutfitAssets!) sb.Append('\n').Append(p);
        }
        if (hasPhysics)
        {
            if (hasAssets) sb.Append("\n\n");
            sb.Append("An outfit mesh references a physics config that doesn't exist ")
              .Append("(a broken link inside the mod). The mugshot is rendered correctly; ")
              .Append("in game the piece's physics likely won't load:\n - ")
              .Append(string.Join("\n - ", physicsNotices!));
        }

        HasMissingOutfitAssets = true;
        MissingOutfitAssetsText = sb.ToString();
    }

    /// <summary>Sets the data-folder-asset badge from render output or stamped
    /// metadata: non-vanilla assets the render resolved from the data folder
    /// because they weren't in this mod's Corresponding Mod Folders. Always
    /// applied (even empty) so regenerating after the user adds the supplying
    /// mod's folder clears a previous notice. Gated on
    /// <see cref="InternalMugshotSettings.ShowDataFolderAssetsIcon"/> like the
    /// other badge appliers. The immediate text lists paths only; a background
    /// pass re-composes it with each asset's supplying mod folder(s)
    /// (VM_ModsMenuMugshot twin — keep in sync).</summary>
    private void ApplyDataFolderAssetNotices(IReadOnlyList<string>? dataFolderAssets)
    {
        int version = Interlocked.Increment(ref _dataFolderNoticeVersion);
        if (dataFolderAssets is not { Count: > 0 }
            || !_settings.InternalMugshot.ShowDataFolderAssetsIcon)
        {
            HasDataFolderAssets = false;
            DataFolderAssetsText = string.Empty;
            return;
        }

        DataFolderAssetsText = DataFolderAssetAttributor.ComposeNoticeText(
            ModName, dataFolderAssets, providersByPath: null);
        HasDataFolderAssets = true;
        _ = EnrichDataFolderAssetNoticesAsync(dataFolderAssets, version);
    }

    /// <summary>Guards a stale enrichment from overwriting a newer notice set
    /// (tiles re-apply notices on regeneration while a lookup may be in flight).</summary>
    private int _dataFolderNoticeVersion;

    private async Task EnrichDataFolderAssetNoticesAsync(IReadOnlyList<string> paths, int version)
    {
        try
        {
            var providers = await Task.Run(() =>
            {
                var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in paths)
                {
                    if (!map.ContainsKey(p)) map[p] = _dataFolderAttributor.GetProviders(p);
                }
                return map;
            }).ConfigureAwait(false);

            if (providers.Values.All(v => v.Count == 0)) return; // nothing attributable — keep the paths-only text
            if (Volatile.Read(ref _dataFolderNoticeVersion) != version) return;
            DataFolderAssetsText = DataFolderAssetAttributor.ComposeNoticeText(ModName, paths, providers);
        }
        catch (Exception ex)
        {
            // Attribution is a nicety; the paths-only tooltip stands.
            Debug.WriteLine($"EnrichDataFolderAssetNotices failed for {SourceNpcFormKey}: {ex.Message}");
        }
    }

    /// <summary>Computes the outfit-conflict notice for this tile via the
    /// effective-outfit simulation. Returns an empty string when there is no
    /// conflict (or on any failure). Safe on background threads.</summary>
    private string ComputeOutfitNoticeSafe()
    {
        try
        {
            var sourceMod = _settings.ModSettings.FirstOrDefault(m => m.DisplayName == ModName);
            if (sourceMod == null) return string.Empty;
            var (includeOutfit, _) = _settings.GetEffectiveAttireFlags(SourceNpcFormKey);
            var result = _outfitDisplayResolver.ResolveForDisplay(
                _targetNpcFormKey, SourceNpcFormKey, sourceMod, includeOutfit);
            if (string.IsNullOrEmpty(result.WarningText)) return string.Empty;

            var sb = new StringBuilder(result.WarningText);
            if (!string.IsNullOrEmpty(result.SourceDetail))
            {
                sb.Append("\n\nDisplayed outfit: ").Append(result.SourceDetail);
            }
            foreach (var approx in result.Approximations)
            {
                sb.Append("\nNote: approximated — ").Append(approx);
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ComputeOutfitNoticeSafe failed for {SourceNpcFormKey}: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>Recomputes <see cref="OutfitNoticeText"/> off the UI thread
    /// and applies it. Fire-and-forget from load/generation paths.</summary>
    private async Task RefreshOutfitNoticeAsync()
    {
        var notice = await Task.Run(ComputeOutfitNoticeSafe);
        OutfitNoticeText = notice;
        HasOutfitNotice = notice.Length > 0;
    }

    /// <summary>Computes the "antlers removed" notice for this tile: non-empty
    /// when the mod's effective Antler Handling Mode is Remove and this NPC
    /// actually carries a strippable antler (see
    /// <see cref="NpcMeshResolver.AntlerRemovalApplies"/>). Returns an empty
    /// string otherwise or on any failure. Safe on background threads.</summary>
    private string ComputeAntlerRemovalNoticeSafe()
    {
        try
        {
            var sourceMod = _settings.ModSettings.FirstOrDefault(m => m.DisplayName == ModName);
            if (sourceMod == null) return string.Empty;
            if (_npcMeshResolver.AntlerRemovalApplies(SourceNpcFormKey, sourceMod))
                return "Antlers are removed from this NPC in the patched output " +
                       "(this mod's Antler Handling Mode is set to Remove).";
            // Not removed on purpose, but lost anyway: Leave As Is keeps them in an
            // outfit that a SPID/SkyPatcher config replaces at runtime.
            return _npcMeshResolver.AntlerRuntimeLossNotice(SourceNpcFormKey, sourceMod, _targetNpcFormKey);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ComputeAntlerRemovalNoticeSafe failed for {SourceNpcFormKey}: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>Recomputes <see cref="AntlerRemovalNoticeText"/> off the UI
    /// thread and applies it. Fire-and-forget from load/generation paths.</summary>
    private async Task RefreshAntlerRemovalNoticeAsync()
    {
        var notice = await Task.Run(ComputeAntlerRemovalNoticeSafe);
        AntlerRemovalNoticeText = notice;
        HasAntlerRemovalNotice = notice.Length > 0;
    }

    /// <summary>Sets the "has wig" badge once at construction from the analysis
    /// scan's per-NPC wig-source map plus live manual designations
    /// (<see cref="Settings.GetEffectiveNpcWigSources"/>) — a pure
    /// record-derived lookup with no environment access or record resolution.
    /// Deliberately independent of the mugshot pipeline: wig carriage is a
    /// plugin-record fact, so nothing about generation or refresh may change
    /// it. Manual designation changes surface on the next tile rebuild (NPC
    /// re-selection). The tooltip names each effective source (skin/WornArmor
    /// vs Default Outfit, with the record's EditorID).</summary>
    private void InitializeWigNotice()
    {
        try
        {
            var sourceMod = _settings.ModSettings.FirstOrDefault(m => m.DisplayName == ModName);
            var effective = _settings.GetEffectiveNpcWigSources(sourceMod, SourceNpcFormKey);
            if (effective.Count > 0)
            {
                _eventLogger.Log(
                    $"Wig notice {ModName} / {SourceNpcFormKey}: " + string.Join("; ",
                        effective.Select(e => e.Kind + " " +
                            (string.IsNullOrEmpty(e.EditorId) ? e.RecordFormKey.ToString() : e.EditorId))),
                    "WIG_NOTICE");
                WigNoticeText = BuildWigNoticeText(effective);
                HasWigNotice = true;
                // Record-level reasons only here (constructor, UI thread); the runtime
                // outfit-replacement check joins on the tile's worker-thread refreshes.
                ApplyWigPersistence(ComputeWigPersistenceSafe(includeRuntimeReplacement: false));
            }
        }
        catch (Exception ex)
        {
            _eventLogger.Log(
                $"Wig notice {ModName} / {SourceNpcFormKey}: EXCEPTION {ex.GetType().Name}: {ex.Message}",
                "WIG_NOTICE");
            Debug.WriteLine($"InitializeWigNotice failed for {SourceNpcFormKey}: {ex.Message}");
        }
    }

    /// <summary>Computes whether this NPC's wig reaches the output. Without
    /// <paramref name="includeRuntimeReplacement"/> it is pure settings + persisted
    /// scan data, cheap enough for the constructor; with it, it also simulates the
    /// SPID/SkyPatcher outfit stack, so call it from the tile's worker thread. Null
    /// when the NPC has no wig or on failure.</summary>
    private (WigPersistenceResult Persistence, List<NpcWigSource> Effective)? ComputeWigPersistenceSafe(
        bool includeRuntimeReplacement)
    {
        try
        {
            var sourceMod = _settings.ModSettings.FirstOrDefault(m => m.DisplayName == ModName);
            var effective = _settings.GetEffectiveNpcWigSources(sourceMod, SourceNpcFormKey);
            if (effective.Count == 0) return null;
            return (_outfitDisplayResolver.ComputeWigPersistence(
                SourceNpcFormKey, _targetNpcFormKey, sourceMod, includeRuntimeReplacement), effective);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ComputeWigPersistenceSafe failed for {SourceNpcFormKey}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Sets the has-wig badge's No Wig state and appends (or removes) the
    /// "won't be in your output" paragraph on its tooltip. Rebuilds the tooltip from
    /// the effective sources each time rather than mutating the existing string, so
    /// repeated calls can't stack duplicate paragraphs.</summary>
    private void ApplyWigPersistence((WigPersistenceResult Persistence, List<NpcWigSource> Effective)? computed)
    {
        if (computed == null) return;
        var (persistence, effective) = computed.Value;
        WigNotPersisted = persistence.AnyDropped;
        WigNoticeText = persistence.AnyDropped
            ? BuildWigNoticeText(effective) + "\n\n⚠ " + persistence.Headline
              + "\n\n" + persistence.FixAdvice
            : BuildWigNoticeText(effective);
    }

    private static string BuildWigNoticeText(List<NpcWigSource> effective)
    {
        static string Describe(NpcWigSource e)
        {
            string id = string.IsNullOrEmpty(e.EditorId) ? e.RecordFormKey.ToString() : e.EditorId;
            return e.Kind == NpcWigSourceKind.WornArmor
                ? $"hair armor add-on '{id}' carried on its skin (WornArmor)"
                : $"armor '{id}' in its Default Outfit";
        }

        if (effective.Count == 1)
        {
            return "This NPC wears a wig — " + Describe(effective[0]) + ".";
        }

        var sb = new StringBuilder("This NPC wears a wig, supplied by:");
        foreach (var e in effective)
        {
            sb.Append("\n• ").Append(Describe(e));
        }
        return sb.ToString();
    }

    /// <summary>Runs the live-notice refreshes (outfit → antler) SEQUENTIALLY,
    /// mirroring LoadInitialImageAsync's in-order compute on a single worker —
    /// the computes share RecordHandler / link-cache state through the
    /// resolvers, so they are not fanned out as concurrent Task.Runs.
    /// Fire-and-forget from the generation path. (The wig notice is NOT here:
    /// it is construction-time scan data, independent of generation.)</summary>
    private async Task RefreshTileNoticesAsync()
    {
        await RefreshOutfitNoticeAsync();
        await RefreshAntlerRemovalNoticeAsync();
        // Whether the wig persists tracks live settings and runtime configs, unlike
        // the has-wig fact the badge itself is built from.
        ApplyWigPersistence(await Task.Run(() => ComputeWigPersistenceSafe(includeRuntimeReplacement: true)));
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

        // Keep the packer's source dimensions in sync with the image we just
        // swapped in. This path (the renderer / fresh-PNG reuse) previously left
        // OriginalDip* at whatever the placeholder set, so a tile that first
        // showed the placeholder and then had its real render assigned here was
        // sized/cropped by the ImagePacker against the placeholder's aspect
        // ratio. Mirrors SetImageSourceFromMemory + LoadInitialImageAsync.
        var (pixelWidth, pixelHeight, dipWidth, dipHeight) = ImagePacker.GetImageDimensions(path);
        if (pixelWidth > 0 && pixelHeight > 0)
        {
            OriginalPixelWidth = pixelWidth;
            OriginalPixelHeight = pixelHeight;
            OriginalDipWidth = dipWidth;
            OriginalDipHeight = dipHeight;
            OriginalDipDiagonal = Math.Sqrt(dipWidth * dipWidth + dipHeight * dipHeight);
        }

        // Newly-loaded image with fresh dimensions — trigger a (throttled) re-pack.
        _vmNpcSelectionBar.NotifyTileImageReady();
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

        // Newly-loaded image with fresh dimensions — trigger a (throttled) re-pack.
        _vmNpcSelectionBar.NotifyTileImageReady();
    }

    public void Dispose()
    {
        // Tear down a live viewport with its own GL context current (via the
        // view's ViewShutdownRequested handler) before the tile dies — tile
        // VMs are disposed on every NPC switch, and leaking a GL context +
        // GLFW window per live tile per switch would add up fast. Live-tile
        // state is transient by design: a fresh tile set starts static.
        DeactivateLiveTile();
        Disposables.Dispose();
    }

    // --- IDragSource Implementation ---

    public bool CanStartDrag(IDragInfo dragInfo)
    {
        // A live tile's left-drag orbits the camera; letting gong start a
        // drag-drop from the same gesture would fight the viewport (and the
        // drag adorner binds MugshotSource, which a live tile doesn't have).
        return !IsLiveTile;
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

    /// <summary>True when this tile's currently-displayed image was produced
    /// by the portrait creator (auto-generated). Drag-drop predicates need
    /// this independently of HasMugshot because LoadInitialImageAsync's
    /// fast-path sets HasMugshot=true for autogen reuse, conflating "tile
    /// shows a valid image" with "tile shows a real curated mugshot".</summary>
    private bool IsImageAutoGen() =>
        !string.IsNullOrWhiteSpace(ImagePath)
        && File.Exists(ImagePath)
        && _portraitCreator.IsAutoGenerated(ImagePath);

    /// <summary>True when this tile's currently-displayed image is a
    /// FaceFinder cached download. Drag-drop routes FF-cache sources through
    /// the FaceFinder name-mapping flow rather than the mugshot-folder
    /// linkage flow.</summary>
    private bool IsImageFaceFinderCache()
    {
        if (string.IsNullOrWhiteSpace(ImagePath)) return false;
        if (_settings.CachedFaceFinderPaths.Contains(ImagePath)) return true;
        var ffFolder = Settings.GetEffectiveFaceFinderMugshotsFolder(_settings);
        return !string.IsNullOrWhiteSpace(ffFolder)
            && ImagePath.StartsWith(ffFolder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when this tile's currently-displayed image is a real
    /// user-curated mugshot (under MugshotsFolder, not autogen, not FF cache).
    /// Drag-drop predicates use this in place of HasMugshot.
    /// Excludes the bundled "No Mugshot.png" placeholder explicitly: when a
    /// tile has no curated/AG/FF source LoadInitialImageAsync sets ImagePath
    /// to FullPlaceholderPath, which would otherwise pass the
    /// exists+not-autogen+not-FF checks and make placeholders look like real
    /// curated mugshots — routing the drop into MassUpdateNpcSelections
    /// instead of the folder-link path.</summary>
    private bool IsImageRealCurated() =>
        !string.IsNullOrWhiteSpace(ImagePath)
        && File.Exists(ImagePath)
        && !string.Equals(ImagePath, FullPlaceholderPath, StringComparison.OrdinalIgnoreCase)
        && !IsImageAutoGen()
        && !IsImageFaceFinderCache();

    /// <summary>For an unbound (no AssociatedModSetting) curated tile,
    /// returns the per-mod MugshotsFolder subdirectory that the curated PNG
    /// lives in — the folder that should be added to a target mod's
    /// MugShotFolderPaths when the user links them via drag-drop. Returns
    /// empty string if MugshotsFolder is unset or the candidate doesn't
    /// exist on disk.</summary>
    private string GetUnboundCuratedFolderPath()
    {
        if (string.IsNullOrWhiteSpace(_settings.MugshotsFolder)) return string.Empty;
        var candidate = Path.Combine(_settings.MugshotsFolder, ModName);
        return Directory.Exists(candidate) ? candidate : string.Empty;
    }

    /// <summary>For each PNG in <paramref name="realMugshotFolders"/>, deletes
    /// any auto-generated PNG at the same relative path inside the local mod's
    /// existing autogen folder, then prunes empty parent directories. Used
    /// after linking a curated mugshot folder to a mod whose tile was
    /// previously displaying an autogen image, so the curated set wins on
    /// next paint without leaving stale autogen PNGs around.</summary>
    private void CleanupSupersededAutogen(VM_ModSetting localModSetting, IEnumerable<string> realMugshotFolders)
    {
        // Find an existing autogen-image folder in the mod's MugShotFolderPaths
        // (the renderer writes autogen PNGs into one of these). Identified by
        // the per-mod autogen save path the BatchMugshotGenerator uses.
        var autoGenImageModFolder = localModSetting.MugShotFolderPaths
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)
                                 && Directory.Exists(p)
                                 && Directory.EnumerateFiles(p, "*.png", SearchOption.AllDirectories)
                                     .Any(f => _portraitCreator.IsAutoGenerated(f)));

        if (string.IsNullOrWhiteSpace(autoGenImageModFolder)) return;

        foreach (var realMugshotModFolder in realMugshotFolders)
        {
            if (string.IsNullOrWhiteSpace(realMugshotModFolder) || !Directory.Exists(realMugshotModFolder)) continue;
            try
            {
                foreach (var realFilePath in Directory.EnumerateFiles(realMugshotModFolder, "*.*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(realMugshotModFolder, realFilePath);
                    var correspondingAutoGenPath = Path.Combine(autoGenImageModFolder, relativePath);
                    if (!File.Exists(correspondingAutoGenPath) || !_portraitCreator.IsAutoGenerated(correspondingAutoGenPath)) continue;

                    try
                    {
                        File.Delete(correspondingAutoGenPath);
                        Debug.WriteLine($"Deleted auto-generated mugshot '{correspondingAutoGenPath}' which is superseded by a real one.");

                        var parentDir = Path.GetDirectoryName(correspondingAutoGenPath);
                        while (parentDir != null
                               && !parentDir.Equals(autoGenImageModFolder, StringComparison.OrdinalIgnoreCase)
                               && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                        {
                            try
                            {
                                Directory.Delete(parentDir);
                                Debug.WriteLine($"Deleted empty parent folder '{parentDir}'.");
                                parentDir = Path.GetDirectoryName(parentDir);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Failed to delete empty parent folder '{parentDir}'. Error: {ex.Message}");
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to delete auto-generated mugshot '{correspondingAutoGenPath}'. Error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error enumerating files in '{realMugshotModFolder}': {ex.Message}");
            }
        }

        bool isDistinctFolder = realMugshotFolders.All(p => !string.Equals(p, autoGenImageModFolder, StringComparison.OrdinalIgnoreCase));
        bool autoGenDirIsEmpty = !Directory.EnumerateFileSystemEntries(autoGenImageModFolder).Any();
        if (autoGenDirIsEmpty && isDistinctFolder)
        {
            if (localModSetting.MugShotFolderPaths.Remove(autoGenImageModFolder))
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

    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        var sourceItem = dropInfo.Data as VM_NpcsMenuMugshot;
        dropInfo.Effects = DragDropEffects.None;

        if (sourceItem == null || sourceItem == this) return;

        // FaceFinder name-mapping: an unbound FF-cached tile drops onto a
        // bound local mod (or vice versa). Tightened from the old
        // "AssociatedModSetting==null && HasMugshot" predicate so curated
        // mugshot-only tiles aren't misrouted into FF mapping (see Drop
        // for the curated-unbound branch below).
        bool sourceIsFaceFinderOnly = sourceItem.AssociatedModSetting == null && sourceItem.IsImageFaceFinderCache();
        bool targetIsLocalMod = this.AssociatedModSetting != null;
        bool sourceIsLocalMod = sourceItem.AssociatedModSetting != null;
        bool targetIsFaceFinderOnly = this.AssociatedModSetting == null && this.IsImageFaceFinderCache();

        if ((sourceIsFaceFinderOnly && targetIsLocalMod) || (targetIsFaceFinderOnly && sourceIsLocalMod))
        {
            dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
            dropInfo.Effects = DragDropEffects.Move;
            return;
        }

        // Curated unbound tile (e.g. a manually-downloaded mugshot whose folder
        // name doesn't match any local mod) drops onto a local mod with game
        // data — link the curated folder into the local mod's MugShotFolderPaths.
        bool sourceIsCuratedUnbound = sourceItem.AssociatedModSetting == null && sourceItem.IsImageRealCurated();
        bool targetIsCuratedUnbound = this.AssociatedModSetting == null && this.IsImageRealCurated();
        bool srcModHasGameData = sourceItem.AssociatedModSetting != null
            && (sourceItem.AssociatedModSetting.CorrespondingFolderPaths.Any()
                || sourceItem.AssociatedModSetting.IsAutoGenerated);
        bool tgtModHasGameData = this.AssociatedModSetting != null
            && (this.AssociatedModSetting.CorrespondingFolderPaths.Any()
                || this.AssociatedModSetting.IsAutoGenerated);

        if ((sourceIsCuratedUnbound && tgtModHasGameData) || (targetIsCuratedUnbound && srcModHasGameData))
        {
            dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
            dropInfo.Effects = DragDropEffects.Move;
            return;
        }

        // Both tiles back a mod with underlying game data (CorrespondingFolderPaths
        // or an auto-generated Base Game / Creation Club entry). Dragging one onto
        // the other is a bulk selection remap (Drop -> MassUpdateNpcSelections),
        // NOT a mugshot-folder link. Gated on game data rather than
        // IsImageRealCurated so a data-bearing mod whose tile currently shows an
        // auto-generated (or any) image still qualifies; the only requirement is
        // that neither side is a mugshot-only entry. (srcModHasGameData /
        // tgtModHasGameData are computed above for the curated-unbound check.)
        if (srcModHasGameData && tgtModHasGameData)
        {
            dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
            dropInfo.Effects = DragDropEffects.Move;
            return;
        }

        // Folder-link path: a curated mugshot tile linked to a mugshot-only
        // entry / placeholder. "Real curated" excludes auto-gen and FF-cache so
        // an autogen target routes through the placeholder branch below — which
        // already handles autogen targets via its isTargetAutoGenerated cleanup.
        bool sourceIsRealMugshotVm = sourceItem.IsImageRealCurated();
        bool targetIsRealMugshotVm = this.IsImageRealCurated();

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

        // Reject if the mugshot source also has underlying game data
        // (CorrespondingFolderPaths or IsAutoGenerated — the latter covers
        // Base Game / Creation Club auto-entries that supply vanilla data
        // without a folder). Linking would merge two data-bearing mods into
        // one entry, which is a data-folder clash. The mugshot-only side
        // case (e.g. a curated mugshot folder) keeps working: it has neither
        // CorrespondingFolderPaths nor IsAutoGenerated.
        bool mugshotSideHasGameData = mugshotModSetting != null &&
                                      (mugshotModSetting.CorrespondingFolderPaths.Any() ||
                                       mugshotModSetting.IsAutoGenerated);

        if (mugshotPathValid && placeholderPathsValid && !mugshotSideHasGameData)
        {
            dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
            dropInfo.Effects = DragDropEffects.Move;
        }
    }


    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        var sourceItem = dropInfo.Data as VM_NpcsMenuMugshot;
        if (sourceItem == null || sourceItem == this) return;

        var sb = new StringBuilder();
        string imagePath = @"Resources\Dragon Drop.png";

        // FaceFinder name-mapping: an unbound FF-cached tile + a bound local mod.
        // Tightened from the old "AssociatedModSetting==null && HasMugshot"
        // predicate so curated mugshot-only tiles fall through to the
        // curated-unbound branch below instead of being misrouted into FF mapping.
        var faceFinderVm = sourceItem.AssociatedModSetting == null && sourceItem.IsImageFaceFinderCache()
            ? sourceItem
            : (this.AssociatedModSetting == null && this.IsImageFaceFinderCache() ? this : null);
        var localVm = sourceItem.AssociatedModSetting != null
            ? sourceItem
            : (this.AssociatedModSetting != null ? this : null);

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
            return;
        }

        // Curated unbound tile dropped onto / from a bound local mod with game
        // data. The unbound side is a manually-downloaded mugshot whose folder
        // name doesn't match any local mod (likely a typo / dash difference);
        // append its per-mod folder to the local mod's MugShotFolderPaths so
        // future scans find it.
        bool sourceIsCuratedUnbound = sourceItem.AssociatedModSetting == null && sourceItem.IsImageRealCurated();
        bool targetIsCuratedUnbound = this.AssociatedModSetting == null && this.IsImageRealCurated();
        if (sourceIsCuratedUnbound || targetIsCuratedUnbound)
        {
            var unboundVm = sourceIsCuratedUnbound ? sourceItem : this;
            var localVmForUnbound = sourceIsCuratedUnbound ? this : sourceItem;
            var localModSetting = localVmForUnbound.AssociatedModSetting;
            bool localHasGameData = localModSetting != null
                && (localModSetting.CorrespondingFolderPaths.Any() || localModSetting.IsAutoGenerated);
            if (!localHasGameData) return;

            var unboundFolder = unboundVm.GetUnboundCuratedFolderPath();
            if (string.IsNullOrWhiteSpace(unboundFolder)) return;

            sb = new StringBuilder();
            sb.AppendLine(
                $"Add the curated mugshots from [{unboundVm.ModName}] to the mugshot folders of [{localModSetting!.DisplayName}]?");
            sb.AppendLine(
                $"\nFuture scans will treat '{unboundVm.ModName}' as the mugshot source for [{localModSetting.DisplayName}].");

            if (!ScrollableMessageBox.Confirm(sb.ToString(), "Confirm Mugshot Folder Link", displayImagePath: imagePath))
                return;

            if (!localModSetting.MugShotFolderPaths.Contains(unboundFolder, StringComparer.OrdinalIgnoreCase))
            {
                localModSetting.MugShotFolderPaths.Add(unboundFolder);
                Debug.WriteLine($"Added curated folder '{unboundFolder}' to mod '{localModSetting.DisplayName}'.");
            }

            // If the local tile is showing autogen, scrub the matching autogen
            // PNGs so the linked curated set wins on next paint.
            CleanupSupersededAutogen(localModSetting, new[] { unboundFolder });

            _lazyMods.Value?.RecalculateMugshotValidity(localModSetting);
            _vmNpcSelectionBar?.RefreshCurrentNpcAppearanceSources();
            return;
        }

        // Both tiles back a mod with underlying game data (CorrespondingFolderPaths
        // or an auto-generated Base Game / Creation Club entry) — bulk-swap NPC
        // selections. Gated on game data rather than IsImageRealCurated so a
        // data-bearing mod whose tile currently shows an auto-generated (or any)
        // image still qualifies; the only requirement is that neither side is a
        // mugshot-only entry. This is a distinct remap operation that does not
        // merge or remove mod entries, so the data-clash guard does not apply.
        bool sourceHasGameData = sourceItem.AssociatedModSetting != null
            && (sourceItem.AssociatedModSetting.CorrespondingFolderPaths.Any()
                || sourceItem.AssociatedModSetting.IsAutoGenerated);
        bool targetHasGameData = this.AssociatedModSetting != null
            && (this.AssociatedModSetting.CorrespondingFolderPaths.Any()
                || this.AssociatedModSetting.IsAutoGenerated);

        if (sourceHasGameData && targetHasGameData)
        {
            var droppedMod = sourceItem.ModName;
            var droppedNpc = sourceItem.SourceNpcFormKey;
            var targetMod = this.ModName;
            var targetNpc = this.SourceNpcFormKey;
            // For all NPCs where the target mod (B) is selected and the dragged
            // mod (A) is available, switch the selection from B to A. The
            // confirmation popup ("This will change ... N NPC(s) ...") lives in
            // MassUpdateNpcSelections.
            _vmNpcSelectionBar.MassUpdateNpcSelections(targetMod, targetNpc, droppedMod, droppedNpc);
            return;
        }

        // Folder-link path: a curated mugshot tile linked to a mugshot-only
        // entry / placeholder. Use IsImageRealCurated so autogen tiles aren't
        // treated as "real mugshots" — they route through the placeholder
        // branch's isTargetAutoGenerated cleanup instead.
        bool sourceIsRealMugshotVm = sourceItem.IsImageRealCurated();
        bool targetIsRealMugshotVm = this.IsImageRealCurated();

        // Original case: Handle drop between a real mugshot and a placeholder
        // (or autogen — handled by the isTargetAutoGenerated branch below).
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

        // Defense-in-depth mirror of the DragOver guard: reject if both
        // sides have underlying game data. DragOver should have already
        // refused the gesture, but if it didn't fire for some reason we
        // must not run the link/merge path — combining two data-bearing
        // mods produces a data-folder clash.
        bool mugshotSourceHasGameData = mugshotSourceModSetting.CorrespondingFolderPaths.Any()
                                        || mugshotSourceModSetting.IsAutoGenerated;
        if (mugshotSourceHasGameData)
        {
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

                // Guest/shared appearances reference the mod by DisplayName too; re-point
                // them at the merged entry so RemoveModSetting's stale-share sweep below
                // doesn't discard them as orphans of the retiring name.
                foreach (var guestDict in new[]
                             { _settings.GuestAppearances, _settings.RandomizedGuestAppearances })
                {
                    foreach (var guestSet in guestDict.Values)
                    {
                        var toMigrate = guestSet.Where(g =>
                                g.ModName.Equals(mugshotSourceModSetting.DisplayName,
                                    StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        foreach (var guest in toMigrate)
                        {
                            guestSet.Remove(guest);
                            guestSet.Add((placeholderTargetModSetting.DisplayName, guest.NpcFormKey,
                                guest.NpcDisplayName));
                        }
                    }
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