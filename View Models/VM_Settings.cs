using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.IO;
using System.Reactive;
using CharacterViewer.Rendering;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using NPC_Plugin_Chooser_2.Themes;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat;

namespace NPC_Plugin_Chooser_2.View_Models;

public enum MugshotSearchMode
{
    Fast,
    Comprehensive
}

/// <summary>
/// Row VM for the "Non-Appearance Mods" list in the Settings view. Wraps the
/// path/reason pair from <see cref="Settings.CachedNonAppearanceMods"/> with
/// the optional missing-master annotation from
/// <see cref="Settings.CachedMissingMasterMods"/> so the row can render red and
/// surface a tooltip telling the user which masters to install.
/// </summary>
public class CachedNonAppearanceModEntry
{
    public string Path { get; }
    public string Reason { get; }
    public bool IsMissingMasters { get; }
    public IReadOnlyList<string> MissingMasterNames { get; }
    public string TooltipText { get; }

    public CachedNonAppearanceModEntry(string path, string reason, IReadOnlyList<string>? missingMasters)
    {
        Path = path;
        Reason = reason;
        MissingMasterNames = missingMasters ?? System.Array.Empty<string>();
        IsMissingMasters = MissingMasterNames.Count > 0;

        if (IsMissingMasters)
        {
            TooltipText =
                "Missing masters at scan time:\n  • " + string.Join("\n  • ", MissingMasterNames)
                + "\n\nInstall these masters as separate mod folders, then click the refresh button to re-scan this entry.";
        }
        else
        {
            TooltipText = reason;
        }
    }
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
    private readonly BatchMugshotGenerator _batchMugshotGenerator;
    private readonly FaceFinderClient _faceFinderClient;
    private readonly EventLogger _eventLogger;

    public ViewModelActivator Activator { get; } = new ViewModelActivator();

    public int NumPluginsInEnvironment => _environmentStateProvider.NumPlugins;
    public int NumActivePluginsInEnvironment => _environmentStateProvider.NumActivePlugins;

    // Environment Status diagnostics. Surfaced under the existing status line in
    // SettingsView so users on non-standard installs (renamed folders, moved
    // drives) can see whether Mutagen's plugins.txt / Skyrim.ccc lookup succeeded
    // and how many CC plugins were detected vs how many actually reached the
    // resolved load order. All raised together from the OnEnvironmentUpdated
    // subscription that already fires for NumPluginsInEnvironment.
    public string EnvironmentDataFolderPath => _environmentStateProvider.DataFolderPath;
    public string EnvironmentLoadOrderFilePath => _environmentStateProvider.LoadOrderFilePath;
    public bool EnvironmentLoadOrderFileExists => _environmentStateProvider.LoadOrderFileExists;
    public string EnvironmentCreationClubListingsFilePath => _environmentStateProvider.CreationClubListingsFilePath;
    public bool EnvironmentCreationClubListingsFileExists => _environmentStateProvider.CreationClubListingsFileExists;
    public string EnvironmentCreationClubListingsSource => _environmentStateProvider.CreationClubListingsSource.ToString();
    public int EnvironmentCreationClubPluginsCount => _environmentStateProvider.CreationClubPluginsCount;
    public int EnvironmentCreationClubPluginsInLoadOrderCount => _environmentStateProvider.CreationClubPluginsInLoadOrderCount;

    // --- Existing & Modified Properties ---
    [Reactive] public string ModsFolder { get; set; }
    [Reactive] public string MugshotsFolder { get; set; }
    [Reactive] public string FaceFinderMugshotsFolder { get; set; }
    [Reactive] public string AutogenMugshotsFolder { get; set; }
    [Reactive] public bool FilterByActiveModsMO2 { get; set; }
    [Reactive] public string MO2ModlistPath { get; set; }
    [Reactive] public SkyrimRelease SkyrimRelease { get; set; }
    
    // --- NEW: Mugshot Fallback Properties ---
    [Reactive] public bool UseFaceFinderFallback { get; set; }
    [Reactive] public bool CacheFaceFinderImages { get; set; } 
    [Reactive] public bool LogFaceFinderRequests { get; set; }
    [Reactive] public bool UsePortraitCreatorFallback { get; set; }
    [Reactive] public int MaxParallelPortraitRenders { get; set; }

    /// <summary>Ordered, user-rearrangeable list of mugshot sources tried at
    /// resolution time. Backed by <see cref="Settings.MugshotSourcePriority"/>;
    /// reordering this collection (via gong-wpf-dragdrop on the settings
    /// ListBox) immediately writes the new order back to the model. Each item
    /// carries an <see cref="VM_MugshotSourcePriorityItem.IsEnabled"/> flag
    /// driven by the matching feature toggle / folder check, so disabled
    /// sources render greyed-out but stay draggable.</summary>
    public ObservableCollection<VM_MugshotSourcePriorityItem> MugshotSourcePriority { get; } = new();

    // --- Renderer Selection ---
    [Reactive] public MugshotRenderer SelectedRenderer { get; set; }
    public IEnumerable<MugshotRenderer> RendererChoices { get; } = Enum.GetValues(typeof(MugshotRenderer)).Cast<MugshotRenderer>();
    [ObservableAsProperty] public bool IsInternalRenderer { get; }
    [ObservableAsProperty] public bool IsLegacyRenderer { get; }

    // --- Internal Mugshot (CharacterViewer) settings ---
    [Reactive] public InternalMugshotCameraMode InternalCameraMode { get; set; }
    public IEnumerable<InternalMugshotCameraMode> InternalCameraModeChoices { get; } = Enum.GetValues(typeof(InternalMugshotCameraMode)).Cast<InternalMugshotCameraMode>();

    // Decode-cache budget for the renderer's in-RAM texture/geometry caches.
    [Reactive] public RenderCacheMode MugshotCacheMode { get; set; }
    public IEnumerable<RenderCacheMode> MugshotCacheModeChoices { get; } = Enum.GetValues(typeof(RenderCacheMode)).Cast<RenderCacheMode>();
    [Reactive] public double MugshotCacheFixedGB { get; set; }
    [Reactive] public double MugshotCacheFreeRamPercent { get; set; }

    [Reactive] public float InternalHeadTopFraction { get; set; }
    [Reactive] public float InternalHeadBottomFraction { get; set; }
    [Reactive] public float InternalYaw { get; set; }
    [Reactive] public float InternalPitch { get; set; }
    [Reactive] public float InternalHairAbovePadding { get; set; }
    [Reactive] public bool InternalIncludeAccessories { get; set; }

    /// <summary>Persisted attire defaults: applied to both the mugshot renderer
    /// and the 3D preview. The full-screen 3D popup exposes the same two toggles
    /// as non-persistent per-view overrides seeded from these.</summary>
    [Reactive] public bool InternalIncludeDefaultOutfit { get; set; }
    [Reactive] public bool InternalIncludeHeadgear { get; set; }
    /// <summary>Warning-icon visibility (mugshot tiles). Each also gates the
    /// stamping of its warning class into PNG metadata; unchecking re-stales
    /// exactly the mugshots that were displaying that icon (see
    /// MugshotStalenessChecker's icon-visibility drift check).</summary>
    [Reactive] public bool InternalShowMissingNpcAssetsIcon { get; set; }
    [Reactive] public bool InternalShowMissingOutfitAssetsIcon { get; set; }
    /// <summary>SolidColorBrush wrapping InternalMugshot.BackgroundR/G/B.
    /// Surfaced as a single property so the SettingsView color-picker swatch
    /// + "Change..." button can mirror the legacy Portrait Creator's UI shape
    /// instead of three separate byte textboxes. The WhenAnyValue subscription
    /// in the constructor decomposes it back into the model's R/G/B fields.</summary>
    [Reactive] public SolidColorBrush InternalBackgroundColor { get; set; } = new SolidColorBrush(Colors.DimGray);
    [Reactive] public int InternalOutputWidth { get; set; }
    [Reactive] public int InternalOutputHeight { get; set; }
    [Reactive] public bool InternalVanillaLooseOverridesBsa { get; set; }
    [Reactive] public bool InternalVanillaLooseOverridesModLoose { get; set; }

    /// <summary>Per-render diagnostic capture toggle. When on, the next
    /// live-preview load writes the full [AssetResolution] + renderer trace
    /// to <c>&lt;ExeDir&gt;\RenderLogs\&lt;ModName&gt;_&lt;FormKey&gt;.txt</c>.
    /// See <see cref="InternalMugshotSettings.LogRenderLogic"/>.</summary>
    [Reactive] public bool InternalLogRenderLogic { get; set; }

    /// <summary>Session-only toggle for the live-preview UC's visibility in
    /// the Settings panel. Starts false on each app open so the panel loads
    /// in its compact form (preview is heavyweight - GLWpfControl + scene
    /// upload - and most users edit non-preview settings far more often).
    /// The Show Preview button flips it on when the user wants to render.</summary>
    [Reactive] public bool InternalShowPreview { get; set; } = false;

    /// <summary>Toggles <see cref="InternalShowPreview"/>. Bound to the
    /// Show/Hide Preview button in the Internal renderer panel.</summary>
    public ReactiveCommand<Unit, Unit> ToggleInternalShowPreviewCommand { get; }

    // Render-pipeline params (RenderMissingTextureAsWireframe, EnableToneMapping,
    // EnableShadows, EnableAmbientOcclusion + SSAO tunables, EnableEyeCatchlight,
    // SubsurfaceStrength, VignetteRadius, VignetteIntensity) used to be flat
    // [Reactive] props on this VM. They're now bound directly to VM_CharacterViewer
    // via the lib's UC_CharacterViewerRenderPanel embedded in the live-preview
    // toolbar - VM_InternalMugshotPreview owns the persistence bridge. The model
    // fields on InternalMugshotSettings stay (consumed by the staleness hash and
    // the offscreen render request) but the host-side VM mirrors are gone.

    /// <summary>Live preview view-model for the Internal renderer's mugshot
    /// preview UC. Lazily resolved from the Splat container — the GLWpfControl
    /// requires WPF UI thread + a real surface, so creating it before the
    /// Settings panel is shown is wasted work.</summary>
    [Reactive] public VM_InternalMugshotPreview? InternalMugshotPreviewVM { get; private set; }
    [Reactive] public SolidColorBrush MugshotBackgroundColor { get; set; }
    [Reactive] public string DefaultLightingJsonString { get; set; }
    [ObservableAsProperty] public bool IsLightingJsonValid { get; }

    // --- NEW: Portrait Creator Camera Properties ---
    [Reactive] public bool AutoUpdateOldMugshots { get; set; }
    [Reactive] public bool AutoUpdateStaleMugshots { get; set; }
    [Reactive] public bool AutoUpdateMugshotsWithMissingAssets { get; set; }
    [Reactive] public bool AssetValidatedMugshotsOnly { get; set; }
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

    // --- "Batch Generate Mugshots" scope selector ---
    // Dropdown to the left of the button: "All" plus the display name of each
    // data-containing (non-mugshot-only) mod that has NPCs to render. Selecting a
    // specific mod limits the batch to that mod; "All" processes every mod.
    public const string AllMugshotModsOption = "All";
    public ObservableCollection<string> MugshotGenerationModOptions { get; } = new() { AllMugshotModsOption };
    [Reactive] public string SelectedMugshotGenerationMod { get; set; } = AllMugshotModsOption;

    /// <summary>
    /// Rebuilds <see cref="MugshotGenerationModOptions"/> from the current mod list
    /// ("All" + each data-containing, non-mugshot-only mod that has NPCs). Called
    /// when the dropdown opens so it always reflects the live Mods tab. Preserves
    /// the current selection if it still exists, otherwise falls back to "All".
    /// </summary>
    public void RefreshMugshotGenerationModOptions()
    {
        var previousSelection = SelectedMugshotGenerationMod;

        var dataModNames = _lazyModListVM.IsValueCreated
            ? _lazyModListVM.Value.AllModSettings
                .Where(m => !m.IsMugshotOnlyEntry
                            && m.NpcFormKeysToDisplayName != null
                            && m.NpcFormKeysToDisplayName.Count > 0
                            && !string.IsNullOrWhiteSpace(m.DisplayName))
                .Select(m => m.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

        MugshotGenerationModOptions.Clear();
        MugshotGenerationModOptions.Add(AllMugshotModsOption);
        foreach (var name in dataModNames)
        {
            MugshotGenerationModOptions.Add(name);
        }

        SelectedMugshotGenerationMod = MugshotGenerationModOptions.Contains(previousSelection)
            ? previousSelection
            : AllMugshotModsOption;
    }


    // --- FaceGen Analysis (per-mugshot polycount / size overlay) ---
    [Reactive] public bool EnableFaceGenAnalysis { get; set; }
    [Reactive] public bool ReportFaceGenSize { get; set; }
    [Reactive] public bool ReportFaceGenPolys { get; set; }
    [Reactive] public bool ReportFaceGenVerts { get; set; }
    [Reactive] public FaceGenAnalysisDisplayMode FaceGenDisplayMode { get; set; }
    public IEnumerable<FaceGenAnalysisDisplayMode> FaceGenDisplayModes { get; } =
        Enum.GetValues(typeof(FaceGenAnalysisDisplayMode)).Cast<FaceGenAnalysisDisplayMode>();
    [Reactive] public double FaceGenTextHeightPercent { get; set; }
    [Reactive] public FaceGenTooltipPosition FaceGenTooltipPosition { get; set; }
    public IEnumerable<FaceGenTooltipPosition> FaceGenTooltipPositions { get; } =
        Enum.GetValues(typeof(FaceGenTooltipPosition)).Cast<FaceGenTooltipPosition>();
    [Reactive] public FaceGenHighlightCriterion FaceGenHighlightCriterion { get; set; }
    public IEnumerable<FaceGenHighlightCriterion> FaceGenHighlightCriteria { get; } =
        Enum.GetValues(typeof(FaceGenHighlightCriterion)).Cast<FaceGenHighlightCriterion>();
    [Reactive] public double FaceGenHighlightThreshold { get; set; }
    /// <summary>SolidColorBrush wrappers around the model's Color fields.
    /// Mirrors the InternalBackgroundColor pattern — the WhenAnyValue
    /// subscription in the constructor decomposes the brush back to a Color
    /// so persistence stays the same shape as MugshotBackgroundColor.</summary>
    [Reactive] public SolidColorBrush FaceGenHighlightColor { get; set; } = new SolidColorBrush(Colors.Red);
    [Reactive] public SolidColorBrush FaceGenNoHighlightColor { get; set; } = new SolidColorBrush(Colors.White);
    [Reactive] public SolidColorBrush FaceGenSpectrumLowColor { get; set; } = new SolidColorBrush(Colors.Blue);
    [Reactive] public SolidColorBrush FaceGenSpectrumMidColor { get; set; } = new SolidColorBrush(Colors.White);
    [Reactive] public SolidColorBrush FaceGenSpectrumHighColor { get; set; } = new SolidColorBrush(Colors.Red);
    [Reactive] public bool IsSpectrumMode { get; private set; }
    public ReactiveCommand<Unit, Unit> SelectFaceGenHighlightColorCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectFaceGenNoHighlightColorCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectFaceGenSpectrumLowColorCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectFaceGenSpectrumMidColorCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectFaceGenSpectrumHighColorCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearFaceGenAnalysisCacheCommand { get; }

    // TargetPluginName now maps to the conceptual name in the model
    [Reactive] public string TargetPluginName { get; set; }

    // --- New Output Settings Properties ---
    [Reactive] public string OutputDirectory { get; set; }
    [Reactive] public bool AppendTimestampToOutputDirectory { get; set; }
    [Reactive] public bool UseSkyPatcherMode { get; set; }
    [Reactive] public bool AutoEslIfy { get; set; } = true;
    [Reactive] public bool AutoSplitOutput { get; set; } = true;
    [Reactive] public bool SplitOutput { get; set; }
    [Reactive] public bool SplitOutputByGender { get; set; }
    [Reactive] public bool SplitOutputByRace { get; set; }
    [Reactive] public int? SplitOutputMaxNpcs { get; set; }

    [Reactive] public PatchingMode SelectedPatchingMode { get; set; }
    public IEnumerable<PatchingMode> PatchingModes { get; } = Enum.GetValues(typeof(PatchingMode)).Cast<PatchingMode>();
    [Reactive] public RecordOverrideHandlingMode SelectedRecordOverrideHandlingMode { get; set; }
    [Reactive] public int DefaultMaxNestedIntervalDepth { get; set; }
    [Reactive] public bool DefaultIncludeAllOverrides { get; set; }
    [Reactive] public bool IsDefaultOverrideHandlingControlsVisible { get; set; }
    [Reactive] public bool IsDefaultMaxNestedIntervalDepthVisible { get; set; }

    public IEnumerable<RecordOverrideHandlingMode> RecordOverrideHandlingModes { get; } =
        Enum.GetValues(typeof(RecordOverrideHandlingMode)).Cast<RecordOverrideHandlingMode>();

    // Global default Wig / Antler Handling Modes (per-mod entries with "Default"
    // resolve to these; see Models.WigHandlingMode / Models.AntlerHandlingMode /
    // Settings.GetEffectiveWigMode / GetEffectiveAntlerMode). The dropdown lists
    // carry friendly display names in display order (None last — see
    // HandlingModeDisplay); the XAML binds SelectedValue/Key like the antler
    // block scope dropdown, so the persisted enum stays the source of truth.
    [Reactive] public WigHandlingMode SelectedDefaultWigHandlingMode { get; set; }
    [Reactive] public AntlerHandlingMode SelectedDefaultAntlerHandlingMode { get; set; }

    public IEnumerable<KeyValuePair<WigHandlingMode, string>> WigHandlingModes { get; } =
        HandlingModeDisplay.WigModesInDisplayOrder
            .Select(m => new KeyValuePair<WigHandlingMode, string>(m, HandlingModeDisplay.ToDisplayString(m)))
            .ToList();

    public IEnumerable<KeyValuePair<AntlerHandlingMode, string>> AntlerHandlingModes { get; } =
        HandlingModeDisplay.AntlerModesInDisplayOrder
            .Select(m => new KeyValuePair<AntlerHandlingMode, string>(m, HandlingModeDisplay.ToDisplayString(m)))
            .ToList();

    // Scope of manually-designated antler head-part blocking (Settings.ManualAntlerBlockScope):
    // how broadly a designated EditorID is treated as an antler across the load order.
    [Reactive] public AntlerBlockScope SelectedAntlerBlockScope { get; set; }

    public IEnumerable<KeyValuePair<AntlerBlockScope, string>> AntlerBlockScopes { get; } = new[]
    {
        new KeyValuePair<AntlerBlockScope, string>(AntlerBlockScope.AllNpcs, "All NPCs (any mod)"),
        new KeyValuePair<AntlerBlockScope, string>(AntlerBlockScope.SameMod, "Same mod only"),
        new KeyValuePair<AntlerBlockScope, string>(AntlerBlockScope.SpecificNpc, "Specific NPC(s) only"),
    };

    // Scope of manual is/is-not-a-wig ArmorAddon designations
    // (Settings.ManualWigBlockScope) — same semantics as the antler scope,
    // applied to the "Set Wig Meshes" selector's entries.
    [Reactive] public AntlerBlockScope SelectedWigBlockScope { get; set; }

    public IEnumerable<KeyValuePair<AntlerBlockScope, string>> WigBlockScopes => AntlerBlockScopes;

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
    public ObservableCollection<CachedNonAppearanceModEntry> FilteredNonAppearanceMods { get; } = new();

    // MODIFIED: Type changed to handle dictionary from model
    public ObservableCollection<CachedNonAppearanceModEntry> CachedNonAppearanceMods { get; private set; }

    // True when at least one entry in CachedNonAppearanceMods has missing masters.
    // Drives the red banner above the list in the Settings view. Updated explicitly
    // by the few code paths that mutate the collection (constructor, refresh, rescan,
    // remove); a reactive composition over the non-reactive collection property is
    // brittle and was failing to fire on initial population.
    [Reactive] public bool HasMissingMasterMods { get; private set; }

    // --- Properties for Ignored Mods ---
    [Reactive] public string IgnoredModFilterText { get; set; } = string.Empty;
    public ObservableCollection<string> IgnoredMods { get; } = new();
    public ObservableCollection<string> FilteredIgnoredMods { get; } = new();

    // --- New: Properties for display settings
    [Reactive] public bool NormalizeImageDimensions { get; set; }
    [Reactive] public int MaxMugshotsToFit { get; set; }
    [Reactive] public bool IsDarkMode { get; set; }
    [Reactive] public string SelectedThemeName { get; set; } = "Dark";
    public ObservableCollection<string> AvailableThemes { get; } = new();
    [Reactive] public string SelectedTabStyle { get; set; } = "Box";
    public List<string> AvailableTabStyles { get; } = new() { "Box", "Underline" };
    [Reactive] public string SelectedNpcSelectionIndicator { get; set; } = "Bar";
    public List<string> AvailableNpcSelectionIndicators { get; } = new() { "None", "Bar", "Text Color" };

    [Reactive] public bool SuppressPopupWarnings { get; set; }
    [Reactive] public bool AutoAdvanceAfterSelection { get; set; }

    // --- NEW: Localization Settings ---
    [Reactive] public bool IsLocalizationEnabled { get; set; }
    [Reactive] public Language? SelectedLocalizationLanguage { get; set; }

    public IEnumerable<Language> AvailableLanguages { get; } = Enum.GetValues(typeof(Language)).Cast<Language>();
    [Reactive] public bool FixGarbledText { get; set; } = true;

    // --- NPC Display ---
    [Reactive] public bool ShowNpcNameInList { get; set; } = true;
    [Reactive] public bool ShowNpcEditorIdInList { get; set; }
    [Reactive] public bool ShowNpcFormKeyInList { get; set; }
    [Reactive] public bool ShowNpcFormIdInList { get; set; }
    [Reactive] public string NpcListSeparator { get; set; } = " | ";
    [Reactive] public bool ShowTemplateStatusInList { get; set; } = true;
    [Reactive] public TemplateIconPosition TemplateIconPosition { get; set; } = TemplateIconPosition.Left;
    public IEnumerable<TemplateIconPosition> TemplateIconPositions { get; } = Enum.GetValues(typeof(TemplateIconPosition)).Cast<TemplateIconPosition>();
    
    [Reactive] public bool LogActivity { get; set; }
    [Reactive] public bool LogStartup { get; set; }
    [Reactive] public bool LogRecordProvenance { get; set; }
    [Reactive] public bool LogAssetProvenance { get; set; }

    // --- Per-NPC logging (Settings > Logging) ---
    // Editable list of NPCs the Validator/Patcher write a full trace for. The
    // search box filters the user's *selected* NPCs by name/EditorID/FormKey;
    // clicking a match adds it to the list. Backed by Settings.NpcsToLog.
    [Reactive] public string NpcLogSearchText { get; set; } = string.Empty;
    [Reactive] public bool HasNpcLogSearchResults { get; set; }
    public ObservableCollection<VM_LoggedNpc> LoggedNpcs { get; } = new();
    public ObservableCollection<VM_LoggedNpc> NpcLogSearchResults { get; } = new();

    // For throttled saving
    private readonly Subject<Unit> _saveRequestSubject = new Subject<Unit>();
    private readonly CompositeDisposable _disposables = new CompositeDisposable(); // To manage subscriptions
    private readonly TimeSpan _saveThrottleTime = TimeSpan.FromMilliseconds(1500);

    // --- Commands ---
    public ReactiveCommand<Unit, Unit> SelectGameFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectModsFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectMugshotsFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectFaceFinderMugshotsFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectAutogenMugshotsFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetFaceFinderMugshotsFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetAutogenMugshotsFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectOutputDirectoryCommand { get; } // New
    public ReactiveCommand<Unit, Unit> ShowPatchingModeHelpCommand { get; } // New
    public ReactiveCommand<Unit, Unit> ImportEasyNpcCommand { get; } // New
    public ReactiveCommand<Unit, Unit> ExportEasyNpcCommand { get; } // New
    public ReactiveCommand<bool, Unit> UpdateEasyNpcProfileCommand { get; } // Takes bool parameter
    public ReactiveCommand<string, Unit> RemoveCachedModCommand { get; }
    public ReactiveCommand<string, Unit> RescanCachedModCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenModLinkerCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCachedFaceFinderImagesCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectMO2ModlistCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectBackgroundColorCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectInternalBackgroundColorCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteAutoGeneratedMugshotsCommand { get; }
    public ReactiveCommand<Unit, Unit> GenerateAllMugshotsCommand { get; }
    public ReactiveCommand<Unit, Unit> BatchDownloadFaceFinderMugshotsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowFullEnvironmentErrorCommand { get; }
    public ReactiveCommand<Unit, Unit> AddIgnoredModCommand { get; }
    public ReactiveCommand<string, Unit> RemoveIgnoredModCommand { get; }
    public ReactiveCommand<VM_LoggedNpc, Unit> AddNpcToLogCommand { get; }
    public ReactiveCommand<VM_LoggedNpc, Unit> RemoveNpcFromLogCommand { get; }
    public ReactiveCommand<Unit, Unit> ValidateOutputCommand { get; }

    private readonly FaceGenAnalysisCache _faceGenAnalysisCache;
    private readonly OutputValidator _outputValidator;

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
        BatchMugshotGenerator batchMugshotGenerator,
        FaceFinderClient faceFinderClient,
        EventLogger eventLogger,
        FaceGenAnalysisCache faceGenAnalysisCache,
        OutputValidator outputValidator)
    {
        _model = settingsModel;
        _faceGenAnalysisCache = faceGenAnalysisCache;
        _outputValidator = outputValidator;
        
        _environmentStateProvider = environmentStateProvider;
        _environmentStateProvider.OnEnvironmentUpdated
            .ObserveOn(RxApp.MainThreadScheduler) // Ensure UI updates happen on the UI thread
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(NumPluginsInEnvironment));
                this.RaisePropertyChanged(nameof(NumActivePluginsInEnvironment));
                this.RaisePropertyChanged(nameof(EnvironmentDataFolderPath));
                this.RaisePropertyChanged(nameof(EnvironmentLoadOrderFilePath));
                this.RaisePropertyChanged(nameof(EnvironmentLoadOrderFileExists));
                this.RaisePropertyChanged(nameof(EnvironmentCreationClubListingsFilePath));
                this.RaisePropertyChanged(nameof(EnvironmentCreationClubListingsFileExists));
                this.RaisePropertyChanged(nameof(EnvironmentCreationClubListingsSource));
                this.RaisePropertyChanged(nameof(EnvironmentCreationClubPluginsCount));
                this.RaisePropertyChanged(nameof(EnvironmentCreationClubPluginsInLoadOrderCount));
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
        StartupLogger.Log($"Setting environment target: {SkyrimRelease}, path: {SkyrimGamePath}");
        _environmentStateProvider.SetEnvironmentTarget(SkyrimRelease, SkyrimGamePath, TargetPluginName, ResolveOutputModFolderForEnv());
        StartupLogger.Log("Updating environment (load order, link cache)");
        _environmentStateProvider.UpdateEnvironment();
        StartupLogger.Log($"Environment initialized, status: {_environmentStateProvider.Status}");
        // Finished environment initialization

        _aux = aux;
        _lazyNpcSelectionBar = lazyNpcSelectionBar;
        _lazyModListVM = lazyModListVm;
        _consistencyProvider = consistencyProvider;
        _lazyMainWindowVm = lazyMainWindowVm;
        _easyNpcTranslator = easyNpcTranslator;
        _portraitCreator = portraitCreator;
        _batchMugshotGenerator = batchMugshotGenerator;
        _faceFinderClient = faceFinderClient;
        _eventLogger = eventLogger;

        // Initialize other VM properties from the model
        ModsFolder = _model.ModsFolder;
        MugshotsFolder = _model.MugshotsFolder;
        // Show the effective path so the user sees the actual folder being used.
        // The user-set value lives on the model; the VM displays the resolved
        // path (model value if non-empty, default fallback otherwise).
        FaceFinderMugshotsFolder = Settings.GetEffectiveFaceFinderMugshotsFolder(_model);
        AutogenMugshotsFolder = Settings.GetEffectiveAutogenMugshotsFolder(_model);
        FilterByActiveModsMO2 = _model.FilterByActiveModsMO2;
        MO2ModlistPath = _model.MO2ModlistPath;

        // --- NEW: Mugshot Fallback Initialization ---
        
        UseFaceFinderFallback = _model.UseFaceFinderFallback;
        CacheFaceFinderImages = _model.CacheFaceFinderImages;
        LogFaceFinderRequests = _model.LogFaceFinderRequests;
        SelectedMugshotSearchModeFF = MugshotSearchMode.Fast;
        UsePortraitCreatorFallback = _model.UsePortraitCreatorFallback;
        MaxParallelPortraitRenders = _model.MaxParallelPortraitRenders;

        // Seed the priority-list VMs from the persisted order. LoadSettings
        // back-fills missing entries, so the model list is guaranteed to
        // contain all three sources at this point.
        foreach (var src in _model.MugshotSourcePriority)
        {
            MugshotSourcePriority.Add(new VM_MugshotSourcePriorityItem(src));
        }
        RefreshMugshotSourceEnabledStates();

        // --- Internal Renderer Initialization ---
        SelectedRenderer = _model.SelectedRenderer;
        InternalCameraMode = _model.InternalMugshot.CameraMode;
        MugshotCacheMode = _model.InternalMugshot.CacheMode;
        MugshotCacheFixedGB = _model.InternalMugshot.CacheFixedBudgetGB;
        MugshotCacheFreeRamPercent = _model.InternalMugshot.CacheFreeRamPercent;
        InternalHeadTopFraction = _model.InternalMugshot.HeadTopFraction;
        InternalHeadBottomFraction = _model.InternalMugshot.HeadBottomFraction;
        InternalYaw = _model.InternalMugshot.Yaw;
        InternalPitch = _model.InternalMugshot.Pitch;
        InternalHairAbovePadding = _model.InternalMugshot.HairAbovePadding;
        InternalIncludeAccessories = _model.InternalMugshot.IncludeAccessories;
        InternalIncludeDefaultOutfit = _model.InternalMugshot.IncludeDefaultOutfit;
        InternalIncludeHeadgear = _model.InternalMugshot.IncludeHeadgear;
        InternalShowMissingNpcAssetsIcon = _model.InternalMugshot.ShowMissingNpcAssetsIcon;
        InternalShowMissingOutfitAssetsIcon = _model.InternalMugshot.ShowMissingOutfitAssetsIcon;
        InternalBackgroundColor = new SolidColorBrush(Color.FromRgb(
            _model.InternalMugshot.BackgroundR,
            _model.InternalMugshot.BackgroundG,
            _model.InternalMugshot.BackgroundB));
        InternalOutputWidth = _model.InternalMugshot.OutputWidth;
        InternalOutputHeight = _model.InternalMugshot.OutputHeight;
        InternalVanillaLooseOverridesBsa = _model.InternalMugshot.VanillaLooseOverridesBsa;
        InternalVanillaLooseOverridesModLoose = _model.InternalMugshot.VanillaLooseOverridesModLoose;
        InternalLogRenderLogic = _model.InternalMugshot.LogRenderLogic;
        // Render-pipeline params are owned by VM_InternalMugshotPreview now -
        // it pushes saved values into VM_CharacterViewer at construction and
        // mirrors edits back to _model.InternalMugshot.* with throttled save.

        this.WhenAnyValue(x => x.SelectedRenderer)
            .Select(r => r == MugshotRenderer.Internal)
            .ToPropertyEx(this, x => x.IsInternalRenderer);
        this.WhenAnyValue(x => x.SelectedRenderer)
            .Select(r => r == MugshotRenderer.LegacyPortraitCreator)
            .ToPropertyEx(this, x => x.IsLegacyRenderer);

        // Lazily instantiate the preview VM the first time the Internal panel
        // becomes visible, so we don't pay the GL setup cost during startup.
        this.WhenAnyValue(x => x.IsInternalRenderer, x => x.UsePortraitCreatorFallback,
                (internalSel, fallbackOn) => internalSel && fallbackOn)
            .DistinctUntilChanged()
            .Where(visible => visible && InternalMugshotPreviewVM == null)
            .Subscribe(_ =>
            {
                try
                {
                    InternalMugshotPreviewVM = Locator.Current.GetService<VM_InternalMugshotPreview>();
                    if (InternalMugshotPreviewVM != null)
                    {
                        InternalMugshotPreviewVM.ResetRequested += RefreshInternalFromModel;
                        // Drag-to-rotate in Auto mode: live-update the Yaw/Pitch
                        // textboxes. Writing the bound VM properties triggers
                        // INPC for the textbox and the existing WhenAnyValue
                        // subscriptions write back to InternalMugshot.Yaw/Pitch.
                        InternalMugshotPreviewVM.AutoFramingYawPitchDragged += OnAutoFramingYawPitchDragged;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to resolve VM_InternalMugshotPreview: " + ex.Message);
                }
            }).DisposeWith(_disposables);

        // --- NEW: Portrait Creator Initialization ---
        AutoUpdateOldMugshots = _model.AutoUpdateOldMugshots;
        AutoUpdateStaleMugshots = _model.AutoUpdateStaleMugshots;
        AutoUpdateMugshotsWithMissingAssets = _model.AutoUpdateMugshotsWithMissingAssets;
        AssetValidatedMugshotsOnly = _model.AssetValidatedMugshotsOnly;
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
        AutoSplitOutput = _model.AutoSplitOutput;
        SplitOutput = _model.SplitOutput;
        SplitOutputByGender = _model.SplitOutputByGender;
        SplitOutputByRace = _model.SplitOutputByRace;
        SplitOutputMaxNpcs = _model.SplitOutputMaxNpcs;
        AppendTimestampToOutputDirectory = _model.AppendTimestampToOutputDirectory;
        SelectedPatchingMode = _model.PatchingMode;
        SelectedRecordOverrideHandlingMode = _model.DefaultRecordOverrideHandlingMode;
        SelectedDefaultWigHandlingMode = _model.DefaultWigHandlingMode;
        SelectedDefaultAntlerHandlingMode = _model.DefaultAntlerHandlingMode;
        SelectedAntlerBlockScope = _model.ManualAntlerBlockScope;
        SelectedWigBlockScope = _model.ManualWigBlockScope;
        DefaultMaxNestedIntervalDepth = _model.DefaultMaxNestedIntervalDepth;
        DefaultIncludeAllOverrides = _model.DefaultIncludeAllOverrides;
        UpdateDefaultOverrideVisibility(); // Initialize visibility state
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

        // --- FaceGen Analysis ---
        EnableFaceGenAnalysis = _model.EnableFaceGenAnalysis;
        ReportFaceGenSize = _model.ReportFaceGenSize;
        ReportFaceGenPolys = _model.ReportFaceGenPolys;
        ReportFaceGenVerts = _model.ReportFaceGenVerts;
        FaceGenDisplayMode = _model.FaceGenDisplayMode;
        FaceGenTextHeightPercent = _model.FaceGenTextHeightPercent;
        FaceGenTooltipPosition = _model.FaceGenTooltipPosition;
        FaceGenHighlightCriterion = _model.FaceGenHighlightCriterion;
        FaceGenHighlightThreshold = _model.FaceGenHighlightThreshold;
        FaceGenHighlightColor = new SolidColorBrush(_model.FaceGenHighlightColor);
        if (FaceGenHighlightColor.CanFreeze) FaceGenHighlightColor.Freeze();
        FaceGenNoHighlightColor = new SolidColorBrush(_model.FaceGenNoHighlightColor);
        if (FaceGenNoHighlightColor.CanFreeze) FaceGenNoHighlightColor.Freeze();
        FaceGenSpectrumLowColor = new SolidColorBrush(_model.FaceGenSpectrumLowColor);
        if (FaceGenSpectrumLowColor.CanFreeze) FaceGenSpectrumLowColor.Freeze();
        FaceGenSpectrumMidColor = new SolidColorBrush(_model.FaceGenSpectrumMidColor);
        if (FaceGenSpectrumMidColor.CanFreeze) FaceGenSpectrumMidColor.Freeze();
        FaceGenSpectrumHighColor = new SolidColorBrush(_model.FaceGenSpectrumHighColor);
        if (FaceGenSpectrumHighColor.CanFreeze) FaceGenSpectrumHighColor.Freeze();
        IsSpectrumMode = _model.FaceGenHighlightCriterion == FaceGenHighlightCriterion.Spectrum;

        // Populate available themes from the Themes folder
        foreach (var theme in ThemeManager.GetAvailableThemes())
            AvailableThemes.Add(theme);

        // Resolve saved theme: prefer ThemeName, fall back to IsDarkMode for backward compatibility
        if (!string.IsNullOrEmpty(_model.ThemeName) && AvailableThemes.Contains(_model.ThemeName))
            SelectedThemeName = _model.ThemeName;
        else if (AvailableThemes.Count > 0)
            SelectedThemeName = _model.IsDarkMode ? "Dark" : "Light";
        SelectedTabStyle = _model.TabStyle;
        SelectedNpcSelectionIndicator = _model.NpcSelectionIndicator;
        AutoAdvanceAfterSelection = _model.AutoAdvanceAfterSelection;
        ShowNpcNameInList = _model.ShowNpcNameInList;
        ShowNpcEditorIdInList = _model.ShowNpcEditorIdInList;
        ShowNpcFormKeyInList = _model.ShowNpcFormKeyInList;
        ShowNpcFormIdInList = _model.ShowNpcFormIdInList;
        NpcListSeparator = _model.NpcListSeparator;
        ShowTemplateStatusInList = _model.ShowTemplateStatusInList;
        TemplateIconPosition = _model.TemplateIconPosition;
        
        LogActivity = _model.LogActivity;
        LogStartup = _model.LogStartup;
        LogRecordProvenance = _model.LogRecordProvenance;
        LogAssetProvenance = _model.LogAssetProvenance;

        ExclusionSelectorViewModel = new VM_ModSelector(); // Initialize early
        ImportFromLoadOrderExclusionSelectorViewModel = new VM_ModSelector();

        // Initialize the collection from the model, ordered by folder name for consistent display.
        CachedNonAppearanceMods = new ObservableCollection<CachedNonAppearanceModEntry>(
            _model.CachedNonAppearanceMods
                .OrderBy(kvp => Path.GetFileName(kvp.Key) ?? kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => new CachedNonAppearanceModEntry(
                    kvp.Key,
                    kvp.Value,
                    _model.CachedMissingMasterMods.TryGetValue(kvp.Key, out var missing) ? missing : null)));

        // Initialize the ignored mods collection from the model.
        foreach (var path in _model.IgnoredMods.OrderBy(p => Path.GetFileName(p) ?? p, StringComparer.OrdinalIgnoreCase))
        {
            IgnoredMods.Add(path);
        }

        // Commands (as before)
        SelectGameFolderCommand = ReactiveCommand.CreateFromTask(SelectGameFolderAsync).DisposeWith(_disposables);
        SelectModsFolderCommand = ReactiveCommand.CreateFromTask(SelectModsFolderAsync).DisposeWith(_disposables);
        SelectMugshotsFolderCommand =
            ReactiveCommand.CreateFromTask(SelectMugshotsFolderAsync).DisposeWith(_disposables);
        SelectFaceFinderMugshotsFolderCommand =
            ReactiveCommand.CreateFromTask(SelectFaceFinderMugshotsFolderAsync).DisposeWith(_disposables);
        SelectAutogenMugshotsFolderCommand =
            ReactiveCommand.CreateFromTask(SelectAutogenMugshotsFolderAsync).DisposeWith(_disposables);
        ResetFaceFinderMugshotsFolderCommand = ReactiveCommand.Create(() =>
        {
            FaceFinderMugshotsFolder = Settings.GetDefaultFaceFinderMugshotsFolder();
        }).DisposeWith(_disposables);
        ResetAutogenMugshotsFolderCommand = ReactiveCommand.Create(() =>
        {
            AutogenMugshotsFolder = Settings.GetDefaultAutogenMugshotsFolder();
        }).DisposeWith(_disposables);
        SelectMO2ModlistCommand = ReactiveCommand.Create(SelectMO2Modlist).DisposeWith(_disposables);
        ToggleInternalShowPreviewCommand = ReactiveCommand.Create(
            () => { InternalShowPreview = !InternalShowPreview; }).DisposeWith(_disposables);
        SelectInternalBackgroundColorCommand = ReactiveCommand.Create(SelectInternalBackgroundColor).DisposeWith(_disposables);
        SelectFaceGenHighlightColorCommand = ReactiveCommand.Create(
            () => PickFaceGenColor(c => FaceGenHighlightColor = c, () => FaceGenHighlightColor.Color))
            .DisposeWith(_disposables);
        SelectFaceGenNoHighlightColorCommand = ReactiveCommand.Create(
            () => PickFaceGenColor(c => FaceGenNoHighlightColor = c, () => FaceGenNoHighlightColor.Color))
            .DisposeWith(_disposables);
        SelectFaceGenSpectrumLowColorCommand = ReactiveCommand.Create(
            () => PickFaceGenColor(c => FaceGenSpectrumLowColor = c, () => FaceGenSpectrumLowColor.Color))
            .DisposeWith(_disposables);
        SelectFaceGenSpectrumMidColorCommand = ReactiveCommand.Create(
            () => PickFaceGenColor(c => FaceGenSpectrumMidColor = c, () => FaceGenSpectrumMidColor.Color))
            .DisposeWith(_disposables);
        SelectFaceGenSpectrumHighColorCommand = ReactiveCommand.Create(
            () => PickFaceGenColor(c => FaceGenSpectrumHighColor = c, () => FaceGenSpectrumHighColor.Color))
            .DisposeWith(_disposables);
        ClearFaceGenAnalysisCacheCommand = ReactiveCommand.Create(
            () => _faceGenAnalysisCache.Clear()).DisposeWith(_disposables);
        SelectOutputDirectoryCommand =
            ReactiveCommand.CreateFromTask(SelectOutputDirectoryAsync).DisposeWith(_disposables);
        ShowPatchingModeHelpCommand = ReactiveCommand.Create(ShowPatchingModeHelp).DisposeWith(_disposables);
        ShowOverrideHandlingModeHelpCommand =
            ReactiveCommand.Create(ShowOverrideHandlingModeHelp).DisposeWith(_disposables);
        ValidateOutputCommand = ReactiveCommand.CreateFromTask(ValidateOutputAsync).DisposeWith(_disposables);
        ImportEasyNpcCommand = ReactiveCommand.Create(_easyNpcTranslator.ImportEasyNpc).DisposeWith(_disposables);
        ExportEasyNpcCommand = ReactiveCommand.Create(_easyNpcTranslator.ExportEasyNpc).DisposeWith(_disposables);
        UpdateEasyNpcProfileCommand = ReactiveCommand.CreateFromTask<bool>(_easyNpcTranslator.UpdateEasyNpcProfile)
            .DisposeWith(_disposables);
        RemoveCachedModCommand = ReactiveCommand.Create<string>(path =>
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            // Remove from both underlying dictionaries
            _model.CachedNonAppearanceMods.Remove(path);
            _model.CachedMissingMasterMods.Remove(path);

            var itemToRemove = CachedNonAppearanceMods.FirstOrDefault(e => e.Path == path);
            if (itemToRemove != null)
            {
                CachedNonAppearanceMods.Remove(itemToRemove);
                FilteredNonAppearanceMods.Remove(itemToRemove);
            }

            UpdateHasMissingMasterMods();
        }).DisposeWith(_disposables);
        RescanCachedModCommand =
            ReactiveCommand.CreateFromTask<string>(RescanCachedModAsync).DisposeWith(_disposables);
        RescanCachedModCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Failed to re-scan mod: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        AddIgnoredModCommand = ReactiveCommand.Create(AddIgnoredMod).DisposeWith(_disposables);
        RemoveIgnoredModCommand = ReactiveCommand.Create<string>(path =>
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            _model.IgnoredMods.Remove(path);
            IgnoredMods.Remove(path);
            FilteredIgnoredMods.Remove(path);
        }).DisposeWith(_disposables);
        AddNpcToLogCommand = ReactiveCommand.Create<VM_LoggedNpc>(AddNpcToLog).DisposeWith(_disposables);
        RemoveNpcFromLogCommand = ReactiveCommand.Create<VM_LoggedNpc>(RemoveNpcFromLog).DisposeWith(_disposables);
        // Re-filter the selected NPCs as the user types (debounced).
        this.WhenAnyValue(x => x.NpcLogSearchText)
            .Throttle(TimeSpan.FromMilliseconds(200), RxApp.TaskpoolScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateNpcLogSearchResults())
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
        GenerateAllMugshotsCommand = ReactiveCommand.CreateFromTask(GenerateAllMugshotsAsync).DisposeWith(_disposables);
        GenerateAllMugshotsCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error generating mugshots: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        BatchDownloadFaceFinderMugshotsCommand = ReactiveCommand.CreateFromTask(BatchDownloadFaceFinderMugshotsAsync).DisposeWith(_disposables);
        BatchDownloadFaceFinderMugshotsCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error downloading FaceFinder mugshots: {ExceptionLogger.GetExceptionStack(ex)}"))
            .DisposeWith(_disposables);
        
        ShowFullEnvironmentErrorCommand = ReactiveCommand.Create(() => 
            ScrollableMessageBox.ShowError(EnvironmentErrorText, "Environment Error")).DisposeWith(_disposables);

        // Property subscriptions (as before, ensure .Skip(1) where appropriate if values are set above)
        // These update the model or _environmentStateProvider's direct properties
        this.WhenAnyValue(x => x.ModsFolder).Skip(1).Subscribe(s => _model.ModsFolder = s)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.MugshotsFolder).Skip(1).Subscribe(s => _model.MugshotsFolder = s)
            .DisposeWith(_disposables);
        // FaceFinder/Autogen folder write-through: persist to the model only
        // when the displayed value differs from the computed default. This way
        // the user's "use default" intent stays empty in Settings.json instead
        // of being stamped with an absolute path that would go stale if the
        // install relocates.
        this.WhenAnyValue(x => x.FaceFinderMugshotsFolder).Skip(1).Subscribe(s =>
        {
            _model.FaceFinderMugshotsFolder =
                string.Equals(s ?? string.Empty,
                    Settings.GetDefaultFaceFinderMugshotsFolder(),
                    StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : (s ?? string.Empty);
        }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.AutogenMugshotsFolder).Skip(1).Subscribe(s =>
        {
            _model.AutogenMugshotsFolder =
                string.Equals(s ?? string.Empty,
                    Settings.GetDefaultAutogenMugshotsFolder(),
                    StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : (s ?? string.Empty);
        }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.FilterByActiveModsMO2).Skip(1)
            .Subscribe(b => _model.FilterByActiveModsMO2 = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.MO2ModlistPath).Skip(1)
            .Subscribe(s => _model.MO2ModlistPath = s).DisposeWith(_disposables);
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

        // Re-evaluate each priority item's IsEnabled whenever an upstream
        // toggle/folder changes. Keeping this logic in a single helper means
        // the UI's greyed-out state and the runtime "is this source eligible?"
        // check derive from the same predicate.
        this.WhenAnyValue(
                x => x.MugshotsFolder,
                x => x.UseFaceFinderFallback,
                x => x.UsePortraitCreatorFallback)
            .Subscribe(_ => RefreshMugshotSourceEnabledStates())
            .DisposeWith(_disposables);

        // Persist any reorder (gong-wpf-dragdrop mutates the ObservableCollection
        // in place; CollectionChanged fires on Move). Cheap enough to do on
        // every change rather than throttling.
        MugshotSourcePriority.CollectionChanged += (_, _) =>
        {
            _model.MugshotSourcePriority =
                MugshotSourcePriority.Select(i => i.Source).ToList();
        };

        this.WhenAnyValue(x => x.SelectedRenderer).Skip(1)
            .Subscribe(r => _model.SelectedRenderer = r).DisposeWith(_disposables);
        // Internal-renderer settings: write through to the model AND schedule
        // a throttled save so each edit persists within ~1.5s, independent of
        // the app-exit save path. Without RequestThrottledSave the value lives
        // in the in-memory model only, and any path that bypasses
        // OnApplicationExit (crash, OS shutdown, env going invalid mid-session)
        // would lose the edit even though the live preview reflected it.
        this.WhenAnyValue(x => x.MugshotCacheMode).Skip(1)
            .Subscribe(m => { _model.InternalMugshot.CacheMode = m; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.MugshotCacheFixedGB).Skip(1)
            .Subscribe(g => { _model.InternalMugshot.CacheFixedBudgetGB = g; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.MugshotCacheFreeRamPercent).Skip(1)
            .Subscribe(p => { _model.InternalMugshot.CacheFreeRamPercent = p; RequestThrottledSave(); }).DisposeWith(_disposables);

        this.WhenAnyValue(x => x.InternalCameraMode).Skip(1)
            .Subscribe(m => { _model.InternalMugshot.CameraMode = m; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalHeadTopFraction).Skip(1)
            .Subscribe(f => { _model.InternalMugshot.HeadTopFraction = f; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalHeadBottomFraction).Skip(1)
            .Subscribe(f => { _model.InternalMugshot.HeadBottomFraction = f; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalYaw).Skip(1)
            .Subscribe(f => { _model.InternalMugshot.Yaw = f; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalPitch).Skip(1)
            .Subscribe(f => { _model.InternalMugshot.Pitch = f; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalHairAbovePadding).Skip(1)
            .Subscribe(f => { _model.InternalMugshot.HairAbovePadding = f; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalIncludeAccessories).Skip(1)
            .Subscribe(b => { _model.InternalMugshot.IncludeAccessories = b; RequestThrottledSave(); }).DisposeWith(_disposables);

        // Attire defaults: write through, save, and re-apply to the embedded
        // live preview (its floating overlay toggles are hidden — these panel
        // checkboxes are the control there). The popup uses its own
        // non-persistent overrides and isn't affected.
        this.WhenAnyValue(x => x.InternalIncludeDefaultOutfit).Skip(1)
            .Subscribe(b =>
            {
                _model.InternalMugshot.IncludeDefaultOutfit = b;
                RequestThrottledSave();
                InternalMugshotPreviewVM?.ReapplyAttireFromSettings();
            }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalIncludeHeadgear).Skip(1)
            .Subscribe(b =>
            {
                _model.InternalMugshot.IncludeHeadgear = b;
                RequestThrottledSave();
                InternalMugshotPreviewVM?.ReapplyAttireFromSettings();
            }).DisposeWith(_disposables);

        // Warning-icon visibility: write-through + save. Tiles pick the new
        // value up on their next apply/reload; unchecking additionally
        // re-stales stamped mugshots via MugshotStalenessChecker.
        this.WhenAnyValue(x => x.InternalShowMissingNpcAssetsIcon).Skip(1)
            .Subscribe(b => { _model.InternalMugshot.ShowMissingNpcAssetsIcon = b; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalShowMissingOutfitAssetsIcon).Skip(1)
            .Subscribe(b => { _model.InternalMugshot.ShowMissingOutfitAssetsIcon = b; RequestThrottledSave(); }).DisposeWith(_disposables);

        // --- FaceGen Analysis persistence ---
        this.WhenAnyValue(x => x.EnableFaceGenAnalysis).Skip(1)
            .Subscribe(b => { _model.EnableFaceGenAnalysis = b; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.ReportFaceGenSize).Skip(1)
            .Subscribe(b => { _model.ReportFaceGenSize = b; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.ReportFaceGenPolys).Skip(1)
            .Subscribe(b => { _model.ReportFaceGenPolys = b; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.ReportFaceGenVerts).Skip(1)
            .Subscribe(b => { _model.ReportFaceGenVerts = b; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.FaceGenDisplayMode).Skip(1)
            .Subscribe(m => { _model.FaceGenDisplayMode = m; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.FaceGenTextHeightPercent).Skip(1)
            .Subscribe(v => { _model.FaceGenTextHeightPercent = v; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.FaceGenTooltipPosition).Skip(1)
            .Subscribe(p => { _model.FaceGenTooltipPosition = p; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.FaceGenHighlightCriterion).Skip(1)
            .Subscribe(c => { _model.FaceGenHighlightCriterion = c; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.FaceGenHighlightThreshold).Skip(1)
            .Subscribe(v => { _model.FaceGenHighlightThreshold = v; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.FaceGenHighlightColor).Skip(1)
            .Subscribe(b => { if (b != null) { _model.FaceGenHighlightColor = b.Color; RequestThrottledSave(); } }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.FaceGenNoHighlightColor).Skip(1)
            .Subscribe(b => { if (b != null) { _model.FaceGenNoHighlightColor = b.Color; RequestThrottledSave(); } }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.FaceGenSpectrumLowColor).Skip(1)
            .Subscribe(b => { if (b != null) { _model.FaceGenSpectrumLowColor = b.Color; RequestThrottledSave(); } }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.FaceGenSpectrumMidColor).Skip(1)
            .Subscribe(b => { if (b != null) { _model.FaceGenSpectrumMidColor = b.Color; RequestThrottledSave(); } }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.FaceGenSpectrumHighColor).Skip(1)
            .Subscribe(b => { if (b != null) { _model.FaceGenSpectrumHighColor = b.Color; RequestThrottledSave(); } }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.FaceGenHighlightCriterion)
            .Subscribe(c => IsSpectrumMode = (c == FaceGenHighlightCriterion.Spectrum)).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalBackgroundColor).Skip(1)
            .Subscribe(brush =>
            {
                if (brush == null) return;
                _model.InternalMugshot.BackgroundR = brush.Color.R;
                _model.InternalMugshot.BackgroundG = brush.Color.G;
                _model.InternalMugshot.BackgroundB = brush.Color.B;
                // Forward to the live viewer so the preview reflects the new
                // color on the next frame; lib WhenAnyValue on Viewer's
                // BackgroundColor pushes it through to Renderer.ClearColor.
                if (InternalMugshotPreviewVM != null)
                {
                    InternalMugshotPreviewVM.Viewer.BackgroundColor = brush.Color;
                }
                RequestThrottledSave();
            }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalOutputWidth).Skip(1)
            .Subscribe(i => { _model.InternalMugshot.OutputWidth = i; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalOutputHeight).Skip(1)
            .Subscribe(i => { _model.InternalMugshot.OutputHeight = i; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalVanillaLooseOverridesBsa).Skip(1)
            .Subscribe(b => { _model.InternalMugshot.VanillaLooseOverridesBsa = b; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalVanillaLooseOverridesModLoose).Skip(1)
            .Subscribe(b => { _model.InternalMugshot.VanillaLooseOverridesModLoose = b; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.InternalLogRenderLogic).Skip(1)
            .Subscribe(b => { _model.InternalMugshot.LogRenderLogic = b; RequestThrottledSave(); }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.AutoUpdateOldMugshots).Skip(1)
            .Subscribe(b => _model.AutoUpdateOldMugshots = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.AutoUpdateStaleMugshots).Skip(1)
            .Subscribe(b => _model.AutoUpdateStaleMugshots = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.AutoUpdateMugshotsWithMissingAssets).Skip(1)
            .Subscribe(b => _model.AutoUpdateMugshotsWithMissingAssets = b).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.AssetValidatedMugshotsOnly).Skip(1)
            .Subscribe(b => _model.AssetValidatedMugshotsOnly = b).DisposeWith(_disposables);
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
        this.WhenAnyValue(x => x.SelectedDefaultWigHandlingMode).Skip(1)
            .Subscribe(m => _model.DefaultWigHandlingMode = m).DisposeWith(_disposables);
        // Convert To Headparts is experimental — confirm before enabling it as the
        // GLOBAL default (mirrors the override-handling-mode confirmation). Buffer(2,1)
        // yields (previous, current) pairs starting with the first user change (the
        // model-loaded value is the first buffered emission, never a trigger), and a
        // decline reverts to the previous selection rather than a hardcoded default.
        this.WhenAnyValue(x => x.SelectedDefaultWigHandlingMode)
            .Buffer(2, 1)
            .Subscribe(pair =>
            {
                var previous = pair[0];
                var current = pair[1];
                if (current != WigHandlingMode.ConvertToHeadParts ||
                    previous == WigHandlingMode.ConvertToHeadParts ||
                    SuppressPopupWarnings)
                {
                    return;
                }

                if (!ScrollableMessageBox.Confirm(HandlingModeDisplay.ConvertToHeadPartsWarning,
                        HandlingModeDisplay.ConvertToHeadPartsWarningTitle, MessageBoxImage.Warning))
                {
                    // Revert on the UI thread after a short delay so the ComboBox
                    // finishes processing the selection first (same pattern as the
                    // override-handling-mode revert).
                    Observable.Timer(TimeSpan.FromMilliseconds(1), RxApp.MainThreadScheduler)
                        .Subscribe(_ => { SelectedDefaultWigHandlingMode = previous; });
                }
            })
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SelectedDefaultAntlerHandlingMode).Skip(1)
            .Subscribe(m => _model.DefaultAntlerHandlingMode = m).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SelectedAntlerBlockScope).Skip(1)
            .Subscribe(s => _model.ManualAntlerBlockScope = s).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SelectedWigBlockScope).Skip(1)
            .Subscribe(s => _model.ManualWigBlockScope = s).DisposeWith(_disposables);
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
        this.WhenAnyValue(x => x.ShowTemplateStatusInList).Skip(1).Subscribe(b =>
            {
                _model.ShowTemplateStatusInList = b;
                if (_lazyNpcSelectionBar.IsValueCreated)
                    _lazyNpcSelectionBar.Value.ShowTemplateStatusInList = b;
            })
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.TemplateIconPosition).Skip(1).Subscribe(p =>
            {
                _model.TemplateIconPosition = p;
                if (_lazyNpcSelectionBar.IsValueCreated)
                    _lazyNpcSelectionBar.Value.TemplateIconPosition = p;
            })
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
            .Subscribe(x => 
            {
                _model.FixGarbledText = x;
                if (_lazyNpcSelectionBar.IsValueCreated)
                {
                    _lazyNpcSelectionBar.Value.RefreshAllNpcDisplayNames();
                }
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.SuppressPopupWarnings)
            .Skip(1)
            .Subscribe(b => _model.SuppressPopupWarnings = b)
            .DisposeWith(_disposables);
        
        this.WhenAnyValue(x => x.LogActivity)
            .Skip(1)
            .Subscribe(b => _model.LogActivity = b)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.LogStartup)
            .Skip(1)
            .Subscribe(b =>
            {
                _model.LogStartup = b;
                if (b && string.IsNullOrEmpty(ModsFolder))
                {
                    StartupLogger.InitializeFromSettings(true);
                }
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.LogRecordProvenance)
            .Skip(1)
            .Subscribe(b =>
            {
                _model.LogRecordProvenance = b;
                // Applied live — RecordProvenanceDiag reads IsEnabled at patch time, so the next
                // run picks this up with no restart (the dev file trigger still force-enables it).
                RecordProvenanceDiag.SetEnabled(b);
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.LogAssetProvenance)
            .Skip(1)
            .Subscribe(b =>
            {
                _model.LogAssetProvenance = b;
                // Applied live — AssetProvenanceDiag reads IsEnabled at patch time, so the next
                // run picks this up with no restart (the dev file trigger still force-enables it).
                AssetProvenanceDiag.SetEnabled(b);
            })
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

        this.WhenAnyValue(x => x.SelectedThemeName)
            .Skip(1) // Skip initial value set from model
            .Where(name => !string.IsNullOrEmpty(name))
            .Subscribe(themeName =>
            {
                _model.ThemeName = themeName;
                ThemeManager.ApplyTheme(themeName);
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.SelectedTabStyle)
            .Skip(1)
            .Subscribe(style => _model.TabStyle = style)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.SelectedNpcSelectionIndicator)
            .Skip(1)
            .Subscribe(indicator =>
            {
                _model.NpcSelectionIndicator = indicator;
                if (_lazyNpcSelectionBar.IsValueCreated)
                    _lazyNpcSelectionBar.Value.NpcSelectionIndicator = indicator;
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.AutoAdvanceAfterSelection)
            .Skip(1)
            .Subscribe(b => _model.AutoAdvanceAfterSelection = b)
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
                UpdateDefaultOverrideVisibility();
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.DefaultMaxNestedIntervalDepth)
            .Skip(1)
            .Subscribe(value => _model.DefaultMaxNestedIntervalDepth = value)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.DefaultIncludeAllOverrides)
            .Skip(1)
            .Subscribe(value =>
            {
                if (value && !SuppressPopupWarnings)
                {
                    const string message =
                        "WARNING: The 'Include All' option will grab ALL override records from the selected plugins, " +
                        "not just those linked to the NPCs being processed.\n\n" +
                        "This method might include overrides that aren't relevant to the NPCs being selected.\n\n" +
                        "This option should only be used if:\n" +
                        "• You are selecting ALL NPCs in this mod, OR\n" +
                        "• As a fallback if you can't set the right Max Nested Search Layers without your computer running out of memory and crashing.\n\n" +
                        "Are you sure you want to enable this option?";

                    if (!ScrollableMessageBox.Confirm(message, "Confirm Include All Overrides"))
                    {
                        Observable.Timer(TimeSpan.FromMilliseconds(1), RxApp.MainThreadScheduler)
                            .Subscribe(_ => { DefaultIncludeAllOverrides = false; });
                    }
                }
                _model.DefaultIncludeAllOverrides = DefaultIncludeAllOverrides;
                UpdateDefaultOverrideVisibility();
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

        this.WhenAnyValue(x => x.AutoSplitOutput)
            .Skip(1)
            .Subscribe(b => _model.AutoSplitOutput = b)
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
                _environmentStateProvider.SetEnvironmentTarget(SkyrimRelease, SkyrimGamePath, TargetPluginName, ResolveOutputModFolderForEnv());
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

        // Initial value for the banner-visibility flag.
        UpdateHasMissingMasterMods();

        this.WhenAnyValue(x => x.IgnoredModFilterText)
            .Select(_ => Unit.Default)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => { ApplyIgnoredModFilter(); })
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
            // Rebuild the "NPCs to log" rows from the model now that the NPC menu
            // (source of display strings) is populated.
            LoadLoggedNpcsFromModel();

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
            StartupLogger.LogPhase("Settings Init - Step 1: Populate Mod List");
            splashReporter?.UpdateStep($"Step 1 of {totalSteps}: Populating mod list...");

            using (ContextualPerformanceTracer.Trace("VM_Settings.PopulateModSettings"))
            {
                // **NEW ORDER: Populate mods first**
                splashReporter?.UpdateProgress(70, "Populating mod list...");
                await _lazyModListVM.Value
                    .PopulateModSettingsAsync(splashReporter); // Base 70%, span 10% (e.g. 70-80)
            }
            StartupLogger.Log("Mod population complete");

            // Sync the freshly-populated VM mod list into Settings.ModSettings NOW,
            // before Step 2 restores the last-viewed NPC and builds its mugshot
            // tiles. Each tile probes the autogen cache on construction
            // (VM_NpcsMenuMugshot.LoadInitialImageAsync →
            // BatchMugshotGenerator.TryGetExistingFreshAutoGenPath), and that
            // probe's freshness check derives an outfit/wig identity by resolving
            // the source mod from the PERSISTED _settings.ModSettings
            // (MakeOutfitIdentityProvider). Until this sync runs, that list is
            // empty on launch, so the provider resolves no mod and computes the
            // winning-override outfit identity instead of the donor's — a spurious
            // mismatch against the PNG's stamped identity that fails the freshness
            // probe. The tile still displays the cached PNG (stale-display
            // fallback), but every cached tile would kick a needless background
            // re-render on every launch. The post-InitializeAsync sync in
            // App.xaml.cs stays as the authoritative pre-BSA-warm sync (this is
            // idempotent).
            try
            {
                _lazyModListVM.Value.SaveModSettingsToModel();
                StartupLogger.Log("VM_Mods → Settings.ModSettings pre-sync (before NPC bar) complete");
            }
            catch (Exception ex)
            {
                StartupLogger.Log("VM_Mods → Settings.ModSettings pre-sync failed: " + ex.Message, "WARN");
            }

            // --- STEP 2 ---
            StartupLogger.LogPhase("Settings Init - Step 2: NPC Selection Bar");
            splashReporter?.UpdateStep($"Step 2 of {totalSteps}: Initializing NPC selection bar...");

            using (ContextualPerformanceTracer.Trace("VM_Settings.InitializeNpcSelectionBar"))
            {
                splashReporter?.UpdateProgress(80, "Initializing NPC selection bar...");
                await _lazyNpcSelectionBar.Value.InitializeAsync(splashReporter);
            }
            StartupLogger.Log("NPC selection bar initialized");

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

    // Resolves the absolute folder the patcher writes output plugins into, from the persisted model.
    // Passed to the environment provider so it can exclude this app's own output plugins from the
    // resolved load order before Mutagen maps them (preventing a self-inflicted file lock on re-runs).
    private string? ResolveOutputModFolderForEnv()
    {
        var outDir = _model?.OutputDirectory;
        if (string.IsNullOrWhiteSpace(outDir)) return null;
        return Path.IsPathRooted(outDir)
            ? outDir
            : Path.Combine(_model?.ModsFolder ?? string.Empty, outDir);
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
            NPC_Plugin_Chooser_2.BackEnd.BsaContentsDiag.Log($"LoadSettings: file exists at {settingsPath}, deserialized ModSettings.Count={loadedSettings?.ModSettings?.Count ?? -1}, SkyrimGamePath=[{loadedSettings?.SkyrimGamePath}]");
        }
        else
        {
            loadedSettings = new Settings(); // Use defaults if file doesn't exist
            // Fresh install: stamp the current schema version so we don't run
            // upgrade migrations on first launch.
            loadedSettings.SchemaVersion = Settings.CurrentSchemaVersion;
            NPC_Plugin_Chooser_2.BackEnd.BsaContentsDiag.Log($"LoadSettings: file did NOT exist at {settingsPath}, using defaults (ModSettings empty)");
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

        // MugshotSourcePriority was added after the initial release. Old
        // Settings.json files lack the field, deserializing as null. Empty or
        // partial lists would silently drop a source, so back-fill missing
        // entries (in default order) at the tail rather than dropping them.
        if (loadedSettings.MugshotSourcePriority == null || loadedSettings.MugshotSourcePriority.Count == 0)
        {
            loadedSettings.MugshotSourcePriority = new List<MugshotSourceType>
            {
                MugshotSourceType.DownloadedMugshots,
                MugshotSourceType.FaceFinder,
                MugshotSourceType.AutoGeneration,
            };
        }
        else
        {
            foreach (MugshotSourceType src in Enum.GetValues(typeof(MugshotSourceType)))
            {
                if (src == MugshotSourceType.None) continue;
                if (!loadedSettings.MugshotSourcePriority.Contains(src))
                    loadedSettings.MugshotSourcePriority.Add(src);
            }
        }

        // Schema-version migrations. SchemaVersion's C# initializer is -1
        // (sentinel) so a pre-upgrade Settings.json (which lacks the field)
        // deserializes to -1. Each migration step here flips newly-added
        // pixel-affecting toggles to their "legacy" values so existing
        // autogen mugshot tiles aren't invalidated by the upgrade — the
        // user opts in to the new look via the settings UI.
        if (loadedSettings.SchemaVersion < 1)
        {
            // 2.5.9 introduced ACES tone-mapping + sRGB framebuffer behind
            // EnableToneMapping. Default-true for fresh installs; off for
            // upgrades so the user's pre-2.5.9 autogen tiles stay matching
            // their stamped settings hash.
            loadedSettings.InternalMugshot.EnableToneMapping = false;
        }
        if (loadedSettings.SchemaVersion < 2)
        {
            // 2.5.10 introduced shadow maps for the key light behind
            // EnableShadows. Default-true for fresh installs; off for
            // upgrades so existing autogen tiles aren't invalidated.
            loadedSettings.InternalMugshot.EnableShadows = false;
        }
        if (loadedSettings.SchemaVersion < 3)
        {
            // 2.5.11 introduced screen-space ambient occlusion behind
            // EnableAmbientOcclusion. Same upgrade-preserves-old-look
            // reasoning as the prior steps.
            loadedSettings.InternalMugshot.EnableAmbientOcclusion = false;
        }
        // 3 -> 4: SSAO radius/bias/intensity exposed via UI. No
        // migration step - the float defaults match the hardcoded
        // values 2.5.11 used, so existing v3 PNGs stay valid.
        if (loadedSettings.SchemaVersion < 5)
        {
            // 2.5.13 introduced EnableEyeCatchlight (eye specular spot
            // from the key light). Default-true for fresh installs;
            // off for upgrades so existing tiles stay matching their
            // stamped settings hash.
            loadedSettings.InternalMugshot.EnableEyeCatchlight = false;
        }
        if (loadedSettings.SchemaVersion < 6)
        {
            // 2.5.14 corrected the SSS math. Setting strength to 0 on
            // upgrade means the corrected pipeline contributes zero SSS,
            // so existing v5-stamped tiles match their stamped hash
            // when re-rendered. User opts in to the new SSS look by
            // raising the slider above 0.
            loadedSettings.InternalMugshot.SubsurfaceStrength = 0f;
        }
        if (loadedSettings.SchemaVersion < 7)
        {
            // 2.5.15 made the tone-mapping vignette tunable. Force
            // intensity to 0 on upgrade so the vignette has no visible
            // effect on existing tiles when they re-render. User opts
            // into the vignette by raising intensity in the settings UI.
            loadedSettings.InternalMugshot.VignetteIntensity = 0f;
        }
        loadedSettings.SchemaVersion = Settings.CurrentSchemaVersion;

        return loadedSettings;
    }

    public void RequestThrottledSave()
    {
        _saveRequestSubject.OnNext(Unit.Default);
    }

    /// <summary>Bridges the live drag from the preview UC into the bound
    /// Yaw/Pitch textboxes. Setting the [Reactive] properties fires INPC
    /// (so the TextBoxes refresh in real time) and the WhenAnyValue
    /// subscriptions write through to <see cref="Settings.InternalMugshot"/>,
    /// so we don't have to update the model separately.</summary>
    private void OnAutoFramingYawPitchDragged(float yaw, float pitch)
    {
        InternalYaw = yaw;
        InternalPitch = pitch;
    }

    /// <summary>Re-pulls the Internal-renderer flat properties from the model.
    /// Called after the preview UC's Reset button replaces InternalMugshot
    /// with a fresh defaults instance — without this, the bound TextBoxes
    /// would still display the old values until the next app restart.</summary>
    private void RefreshInternalFromModel()
    {
        var c = _model.InternalMugshot;
        InternalCameraMode = c.CameraMode;
        InternalHeadTopFraction = c.HeadTopFraction;
        InternalHeadBottomFraction = c.HeadBottomFraction;
        InternalYaw = c.Yaw;
        InternalPitch = c.Pitch;
        InternalHairAbovePadding = c.HairAbovePadding;
        InternalIncludeAccessories = c.IncludeAccessories;
        InternalIncludeDefaultOutfit = c.IncludeDefaultOutfit;
        InternalIncludeHeadgear = c.IncludeHeadgear;
        InternalBackgroundColor = new SolidColorBrush(Color.FromRgb(c.BackgroundR, c.BackgroundG, c.BackgroundB));
        InternalOutputWidth = c.OutputWidth;
        InternalOutputHeight = c.OutputHeight;
        InternalVanillaLooseOverridesBsa = c.VanillaLooseOverridesBsa;
        InternalVanillaLooseOverridesModLoose = c.VanillaLooseOverridesModLoose;
        InternalLogRenderLogic = c.LogRenderLogic;
        // Render-pipeline params re-pushed into VM_CharacterViewer by
        // VM_InternalMugshotPreview.SyncSettingsToViewer (called on Reset).
        InternalMugshotPreviewVM?.SyncSettingsToViewer();
    }

    /// <summary>Re-derive each priority item's IsEnabled + DisabledReason from
    /// the current toggle/folder state. Drives the greyed-out styling in the
    /// settings ListBox only — the runtime resolution loop performs its own
    /// per-source eligibility check (file-exists / feature-flag) inside each
    /// TryXxxAsync branch.</summary>
    private void RefreshMugshotSourceEnabledStates()
    {
        foreach (var item in MugshotSourcePriority)
        {
            switch (item.Source)
            {
                case MugshotSourceType.DownloadedMugshots:
                    if (string.IsNullOrWhiteSpace(MugshotsFolder))
                    {
                        item.IsEnabled = false;
                        item.DisabledReason = "Set a Mugshots Folder above.";
                    }
                    else if (!Directory.Exists(MugshotsFolder))
                    {
                        item.IsEnabled = false;
                        item.DisabledReason = "Mugshots Folder does not exist on disk.";
                    }
                    else
                    {
                        item.IsEnabled = true;
                        item.DisabledReason = string.Empty;
                    }
                    break;
                case MugshotSourceType.FaceFinder:
                    item.IsEnabled = UseFaceFinderFallback;
                    item.DisabledReason = UseFaceFinderFallback
                        ? string.Empty
                        : "Enable 'Use FaceFinder API for missing mugshots' below.";
                    break;
                case MugshotSourceType.AutoGeneration:
                    item.IsEnabled = UsePortraitCreatorFallback;
                    item.DisabledReason = UsePortraitCreatorFallback
                        ? string.Empty
                        : "Enable 'Auto-Generate missing mugshots' below.";
                    break;
            }
        }
    }

    public void SaveSettings()
    {
        NPC_Plugin_Chooser_2.BackEnd.BsaContentsDiag.Log($"SaveSettings ENTER — env.Status={_environmentStateProvider.Status} _model.ModSettings.Count={_model.ModSettings.Count}");
        if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
        {
            NPC_Plugin_Chooser_2.BackEnd.BsaContentsDiag.Log($"SaveSettings EARLY RETURN — env not Valid");
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

        StartupLogger.LogPhase("Deferred Mods Folder Selection");
        StartupLogger.Log($"User selected mods folder: {dialog.FileName}");

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

                // Sync VM_Mods.AllModSettings → Settings.ModSettings. PopulateModSettingsAsync
                // writes only to _allModSettingsInternal; without this call the model
                // (which we just cleared at line 1708) stays empty until the next
                // throttled SaveSettings fires. Same root cause as the fresh-install
                // BSA pre-warm bug fixed in App.xaml.cs after the initial InitializeAsync.
                _lazyModListVM.Value.SaveModSettingsToModel();
            }
            StartupLogger.Log("Mod population complete");

            if (_lazyNpcSelectionBar.IsValueCreated)
            {
                await _lazyNpcSelectionBar.Value.InitializeAsync(splashScreen);
            }
            StartupLogger.Log("NPC selection bar initialized");

            if (_lazyModListVM.IsValueCreated)
            {
                _lazyModListVM.Value.ApplyFilters();
            }

            RefreshNonAppearanceMods();
        }
        finally
        {
            StartupLogger.Complete();
            _lazyMainWindowVm.Value.IsLoadingFolders = false;
            // Ensure the splash screen is closed and the main window is re-enabled
            if (splashScreen != null)
            {
                await splashScreen.CloseSplashScreenAsync();
            }
        }
    }

    private void SelectMO2Modlist()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select MO2 modlist.txt",
            Filter = "Text files (modlist.txt)|modlist.txt|All files (*.*)|*.*",
            FileName = "modlist.txt"
        };

        if (!string.IsNullOrWhiteSpace(MO2ModlistPath) && File.Exists(MO2ModlistPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(MO2ModlistPath);
        }

        if (dialog.ShowDialog() == true)
        {
            MO2ModlistPath = dialog.FileName;
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

    private Task SelectFaceFinderMugshotsFolderAsync()
    {
        var dialog = new CommonOpenFileDialog
        {
            IsFolderPicker = true,
            Title = "Select FaceFinder Cache Folder",
            InitialDirectory = GetSafeInitialDirectory(FaceFinderMugshotsFolder,
                Settings.GetDefaultFaceFinderMugshotsFolder())
        };

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            FaceFinderMugshotsFolder = dialog.FileName;
        }
        return Task.CompletedTask;
    }

    private Task SelectAutogenMugshotsFolderAsync()
    {
        var dialog = new CommonOpenFileDialog
        {
            IsFolderPicker = true,
            Title = "Select Auto-Generated Mugshots Folder",
            InitialDirectory = GetSafeInitialDirectory(AutogenMugshotsFolder,
                Settings.GetDefaultAutogenMugshotsFolder())
        };

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            AutogenMugshotsFolder = dialog.FileName;
        }
        return Task.CompletedTask;
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

    // Opens the "choose NPCs" dialog, runs OutputValidator against the real (untrimmed)
    // deployed load order on a background thread with a cancellable progress window, then
    // shows the findings table. See OutputValidator for the three checks performed.
    private async Task ValidateOutputAsync()
    {
        if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
        {
            ScrollableMessageBox.ShowWarning(
                "The game environment is not valid. Resolve it on this Settings page (a working load order and data folder are required) before validating output.",
                "Validate Output");
            return;
        }

        var selections = _model.SelectedAppearanceMods;
        if (selections == null || selections.Count == 0)
        {
            ScrollableMessageBox.ShowWarning("No appearance selections have been made yet, so there is nothing to validate.", "Validate Output");
            return;
        }

        // Fail fast: confirm this app's output is actually deployed & active BEFORE the user
        // invests effort picking NPCs in the scope dialog. Building the untrimmed load order
        // can take a moment, so run it off the UI thread (the command's IsExecuting disables
        // the button meanwhile). The same gate still runs inside Validate() as a backstop.
        var readiness = await Task.Run(() => _outputValidator.CheckDeployReadiness());
        if (!readiness.Ok)
        {
            ScrollableMessageBox.ShowWarning(readiness.BlockReason ?? "Validation could not run.", "Validate Output");
            return;
        }

        var items = BuildValidationScopeItems(selections);

        var scopeVm = new VM_ValidationScopeWindow(items);
        var scopeWindow = new ValidationScopeWindow { DataContext = scopeVm };
        TrySetOwner(scopeWindow);
        bool? scopeResult = scopeWindow.ShowDialog();
        var chosen = scopeVm.GetChosenFormKeys();
        scopeVm.Dispose();

        if (scopeResult != true) return;
        if (chosen.Count == 0)
        {
            ScrollableMessageBox.ShowWarning("No NPCs were selected to validate.", "Validate Output");
            return;
        }

        var progressVm = new VM_ProgressWindow
        {
            Title = "Validating Output",
            StatusMessage = "Preparing...",
            IsIndeterminate = true,
            ProgressMaximum = chosen.Count
        };
        var progressWindow = new ProgressWindow { ViewModel = progressVm };
        TrySetOwner(progressWindow);
        progressWindow.Show();

        using var cts = new CancellationTokenSource();
        using var cancelSub = progressVm.WhenAnyValue(x => x.IsCancellationRequested)
            .Where(requested => requested)
            .Subscribe(_ => { try { cts.Cancel(); } catch { /* already disposed */ } });

        var progress = new Progress<(int current, int total, string message)>(p =>
        {
            if (p.total > 0)
            {
                progressVm.IsIndeterminate = false;
                progressVm.ProgressMaximum = p.total;
                progressVm.ProgressValue = p.current;
            }
            else
            {
                progressVm.IsIndeterminate = true;
            }
            progressVm.StatusMessage = p.message;
        });

        ValidationRunResult? result = null;
        try
        {
            result = await Task.Run(() => _outputValidator.Validate(chosen, progress, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // User cancelled — fall through and close the progress window.
        }
        catch (Exception ex)
        {
            progressWindow.Close();
            progressVm.Dispose();
            ScrollableMessageBox.ShowError("Validation failed:\n" + ExceptionLogger.GetExceptionStack(ex), "Validate Output");
            return;
        }

        progressWindow.Close();
        progressVm.Dispose();

        if (result == null) return; // cancelled

        if (result.Blocked)
        {
            ScrollableMessageBox.ShowWarning(result.BlockReason ?? "Validation could not run.", "Validate Output");
            return;
        }

        var resultsVm = new VM_ValidationResultsWindow(result);
        var resultsWindow = new ValidationResultsWindow { DataContext = resultsVm };
        resultsWindow.Closed += (_, _) => resultsVm.Dispose(); // modeless: dispose VM subscriptions on close
        TrySetOwner(resultsWindow);
        resultsWindow.Show();
    }

    private List<VM_ValidationScopeItem> BuildValidationScopeItems(
        Dictionary<FormKey, (string ModName, FormKey NpcFormKey)> selections)
    {
        var items = new List<VM_ValidationScopeItem>(selections.Count);
        var linkCache = _environmentStateProvider.LinkCache;
        foreach (var kvp in selections)
        {
            string displayName;
            if (linkCache != null && linkCache.TryResolve<INpcGetter>(kvp.Key, out var npc) && npc != null)
            {
                displayName = Auxilliary.GetLogString(npc, _model.LocalizationLanguage);
            }
            else
            {
                displayName = kvp.Key.ToString();
            }
            items.Add(new VM_ValidationScopeItem(kvp.Key, displayName, kvp.Value.ModName));
        }
        return items;
    }

    private void TrySetOwner(Window window)
    {
        try
        {
            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow != null && mainWindow != window)
            {
                window.Owner = mainWindow;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not set window owner: {ex.Message}");
        }
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
                            // Persist the FaceFinder cache pruning immediately
                            // (parallel to the Generated-cache pruning above)
                            // so an abnormal exit doesn't leave stale-on-disk
                            // entries.
                            RequestThrottledSave();
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

                        // Remove from cache + immediate throttled save so the
                        // entry doesn't survive the next session as a stale
                        // pointer to a now-deleted file.
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _model.CachedFaceFinderPaths.Remove(imageFile);
                            RequestThrottledSave();
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
                            RequestThrottledSave();
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

    /// <summary>Color-picker dialog for the Internal renderer's background.
    /// Mirrors <see cref="SelectBackgroundColor"/>'s shape but writes back to
    /// <see cref="InternalBackgroundColor"/>; the WhenAnyValue subscription
    /// in the constructor decomposes the brush into R/G/B model bytes.</summary>
    private void SelectInternalBackgroundColor()
    {
        var dialog = new System.Windows.Forms.ColorDialog();
        var initialColor = InternalBackgroundColor.Color;
        dialog.Color = System.Drawing.Color.FromArgb(255, initialColor.R, initialColor.G, initialColor.B);

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var newColor = dialog.Color;
            InternalBackgroundColor = new SolidColorBrush(Color.FromRgb(newColor.R, newColor.G, newColor.B));
        }
    }

    /// <summary>Generic color-picker plumbing for the FaceGen highlight /
    /// no-highlight swatches. Wraps <c>System.Windows.Forms.ColorDialog</c>
    /// the same way SelectInternalBackgroundColor does, but parameterized
    /// so both color slots share one implementation.</summary>
    private void PickFaceGenColor(Action<SolidColorBrush> setter, Func<Color> getInitial)
    {
        var dialog = new System.Windows.Forms.ColorDialog();
        var initial = getInitial();
        dialog.Color = System.Drawing.Color.FromArgb(255, initial.R, initial.G, initial.B);
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dialog.Color;
            setter(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)));
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
    
    private async Task GenerateAllMugshotsAsync()
    {
        if (!_lazyModListVM.IsValueCreated)
        {
            ScrollableMessageBox.ShowWarning("Mod list has not been initialized yet.", "Cannot Generate Mugshots");
            return;
        }

        var modSettings = _lazyModListVM.Value.AllModSettings.ToList();
        if (modSettings.Count == 0)
        {
            ScrollableMessageBox.Show("No mod settings found to process.", "No Mods");
            return;
        }

        // Honor the scope dropdown to the left of the button: "All" (or empty)
        // processes every mod; otherwise restrict the batch to the chosen mod.
        string selectedModScope = SelectedMugshotGenerationMod;
        bool generateForAllMods = string.IsNullOrEmpty(selectedModScope)
                                  || selectedModScope == AllMugshotModsOption;
        if (!generateForAllMods)
        {
            modSettings = modSettings
                .Where(m => string.Equals(m.DisplayName, selectedModScope, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (modSettings.Count == 0)
            {
                ScrollableMessageBox.Show(
                    $"The selected mod '{selectedModScope}' was not found. It may have been removed or renamed; choose another option from the dropdown.",
                    "Mod Not Found");
                return;
            }
        }

        if (!UsePortraitCreatorFallback)
        {
            ScrollableMessageBox.ShowWarning(
                "The local renderer (Auto-Generate missing mugshots) is disabled. Enable it under FaceFinder/Auto-Generate settings before running this batch.\n\n" +
                "FaceFinder downloads have their own \"Batch Download Mugshots\" button in the FaceFinder section.",
                "Nothing To Generate");
            return;
        }

        // Build the work list. We resolve each NPC record once up-front so we
        // can sort by NpcGroupingKey (sex → race → worn-armor → head-parts →
        // hair) inside each mod. This minimizes the number of distinct
        // skeletons / body meshes / textures the renderer touches between
        // adjacent renders, without any NIF parsing.
        var linkCache = _environmentStateProvider.LinkCache;
        if (linkCache == null)
        {
            ScrollableMessageBox.ShowError(
                "The Skyrim load order isn't initialized; cannot resolve NPC records.",
                "Environment Not Ready");
            return;
        }

        var workList = new List<(VM_ModSetting Mod, FormKey Npc, string Display, Auxilliary.NpcGroupingKey Key)>();
        foreach (var mod in modSettings)
        {
            if (mod.NpcFormKeysToDisplayName == null || mod.NpcFormKeysToDisplayName.Count == 0) continue;
            // Group all of one mod's NPCs together (so the popup's mod name
            // is meaningful), then sort within the mod by the grouping key.
            var modBucket = new List<(VM_ModSetting Mod, FormKey Npc, string Display, Auxilliary.NpcGroupingKey Key)>(
                mod.NpcFormKeysToDisplayName.Count);
            foreach (var (fk, displayName) in mod.NpcFormKeysToDisplayName)
            {
                if (!linkCache.TryResolve<INpcGetter>(fk, out var npcGetter) || npcGetter == null)
                {
                    // Records that don't resolve in the current load order
                    // can't be rendered — skip silently. The batch counters
                    // never see them so progress totals stay accurate.
                    continue;
                }
                var key = Auxilliary.BuildNpcGroupingKey(npcGetter);
                modBucket.Add((mod, fk, displayName, key));
            }
            modBucket.Sort((a, b) => a.Key.CompareTo(b.Key));
            workList.AddRange(modBucket);
        }

        if (workList.Count == 0)
        {
            ScrollableMessageBox.Show("No NPCs found to process.", "Nothing To Generate");
            return;
        }

        var progressVM = new VM_ProgressWindow
        {
            Title = generateForAllMods ? "Generate All Mugshots" : $"Generate Mugshots — {selectedModScope}",
            StatusMessage = $"Preparing to scan {workList.Count} NPC{(workList.Count == 1 ? "" : "s")}...",
            CurrentSubItem = string.Empty,
            EtaText = string.Empty,
            CancelButtonText = "Abort",
            ProgressMaximum = workList.Count,
            ProgressValue = 0,
            IsIndeterminate = false,
        };

        var progressWindow = new ProgressWindow { ViewModel = progressVM };

        // Long-running batch: switch the shared ProgressWindow chrome over to
        // a regular minimizable window. Defaults stay tool-window/no-resize/
        // top-most for the brief callers (FaceFinder cache, mugshot delete);
        // mutating the instance here keeps the XAML defaults intact.
        progressWindow.ResizeMode = System.Windows.ResizeMode.CanMinimize;
        progressWindow.WindowStyle = System.Windows.WindowStyle.SingleBorderWindow;
        progressWindow.Topmost = false;
        progressWindow.ShowInTaskbar = true;

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

        int generatedCount = 0;
        int skippedCount = 0;
        int failedCount = 0;
        // Internal renders that completed but were discarded because the
        // user toggled AssetValidatedMugshotsOnly and the render reported
        // missing meshes/textures. Counted separately so the summary
        // distinguishes "no source could render this" (failed) from
        // "render produced an incomplete image and we suppressed it".
        int missingAssetSkippedCount = 0;
        // Snapshot the toggle at batch start: the WhenAnyValue write-through
        // could theoretically flip the model mid-batch if the user opens
        // settings while it runs, and we want consistent behavior across the
        // run rather than half-validated / half-not.
        bool assetValidatedOnly = AssetValidatedMugshotsOnly;
        // Generation-only timing for the ETA: pre-existing skips return in
        // milliseconds and would crash the average toward zero (then the ETA
        // creeps back up as real renders start). Tracking time spent only on
        // items that actually produced new bytes, paired with the running
        // rate-of-generation, keeps the estimate stable across mixed batches.
        var etaCalculator = new EtaCalculator();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await Task.Run(async () =>
            {
                for (int i = 0; i < workList.Count; i++)
                {
                    if (progressVM.IsCancellationRequested) break;

                    var (mod, npcFormKey, displayName, _) = workList[i];

                    // Marshal UI updates onto the dispatcher; everything else
                    // runs on the background pool so the UI stays responsive
                    // (and the popup remains minimizable / draggable).
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        progressVM.CurrentSubItem = $"{mod.DisplayName} — {displayName}";
                        progressVM.StatusMessage = $"Generating mugshots ({i + 1} of {workList.Count})";
                    });

                    bool produced = false;
                    var itemStart = stopwatch.Elapsed;
                    try
                    {
                        // The renderer is internally serialized; passing
                        // CancellationToken.None gives "soft abort" semantics
                        // (the in-flight render finishes naturally; the loop
                        // just stops dispatching new ones). Callers expecting
                        // a hard mid-render kill should use the per-tile
                        // pipeline which threads its own token through.
                        var rendererResult = await _batchMugshotGenerator.RunSelectedRendererAsync(
                            npcFormKey, mod, CancellationToken.None, assetValidatedOnly);
                        if (rendererResult.AlreadyCurrent)
                        {
                            produced = true;
                            skippedCount++;
                        }
                        else if (rendererResult.Generated)
                        {
                            produced = true;
                            generatedCount++;
                        }
                        else if (assetValidatedOnly
                                 && (rendererResult.MissingMeshes.Count > 0
                                     || rendererResult.MissingTextures.Count > 0))
                        {
                            // Render completed but the validated-only gate
                            // discarded the bytes. Counted as a deliberate
                            // skip, NOT a failure — the user opted into
                            // this outcome.
                            produced = true;
                            missingAssetSkippedCount++;
                        }

                        if (!produced)
                        {
                            // No path produced a file — record as failure so
                            // the summary is honest. Common causes: NPC's
                            // mod has no folders (Legacy needs a NIF),
                            // mesh resolution failed (Internal).
                            failedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        Debug.WriteLine($"Generate-all failed for {npcFormKey} ({mod.DisplayName}): {ex.Message}");
                        _eventLogger.Log($"Generate-all failed for {npcFormKey}: {ex.Message}", "BATCH_GEN_ERROR");
                    }

                    // Feed the wall-clock cost of EVERY item (cheap skips/cache hits
                    // included — the user waits for those too) into a recency-weighted
                    // estimator. Projecting from recent throughput rather than a
                    // cumulative average keeps the ETA honest when the heavy renders
                    // are front- or back-loaded in the work list.
                    etaCalculator.RecordItem((stopwatch.Elapsed - itemStart).TotalSeconds);

                    int processed = i + 1;
                    int remaining = workList.Count - processed;
                    TimeSpan? eta = etaCalculator.Estimate(remaining);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        progressVM.UpdateProgress(processed);
                        progressVM.SetEta(eta);
                    });
                }
            });
        }
        finally
        {
            stopwatch.Stop();
            try { progressWindow.Close(); }
            catch (Exception ex) { Debug.WriteLine($"Failed to close progress window: {ex.Message}"); }
        }

        var summary = new StringBuilder();
        summary.AppendLine(progressVM.IsCancellationRequested
            ? $"Mugshot generation aborted after {generatedCount + skippedCount + failedCount + missingAssetSkippedCount} of {workList.Count} NPCs."
            : $"Mugshot generation complete for {workList.Count} NPCs.");
        summary.AppendLine();
        summary.AppendLine($"Generated:    {generatedCount}");
        summary.AppendLine($"Already current: {skippedCount}");
        if (missingAssetSkippedCount > 0)
        {
            summary.AppendLine($"Skipped (missing assets): {missingAssetSkippedCount}");
        }
        summary.AppendLine($"Failed / no source: {failedCount}");
        summary.AppendLine();
        summary.AppendLine($"Elapsed: {FormatElapsed(stopwatch.Elapsed)}");

        ScrollableMessageBox.Show(summary.ToString(),
            progressVM.IsCancellationRequested ? "Generation Aborted" : "Generation Complete");
    }

    /// <summary>FaceFinder-only batch. Uses the server's mod-level enumeration
    /// (<c>/api/public/mods/search</c> + <c>/api/public/mod/faces/search</c>)
    /// to pre-filter to NPCs the server actually has before issuing any
    /// image downloads — the older per-NPC probe was hitting the API once
    /// per local NPC even though ~90% of those returned empty, wasting most
    /// of the batch on database misses. Two-phase flow:
    /// <list type="number">
    /// <item>Pull the server's full mod catalog (paginated) once, build a
    /// case-insensitive name → id map.</item>
    /// <item>For each local mod whose name maps to a server id, fetch all
    /// of that mod's server-side faces in one paginated call, intersect with
    /// the user's local NPCs, and run cache-check / download on the matches
    /// (one HTTP request per cache miss instead of one per NPC).</item>
    /// </list>
    /// Deliberately orthogonal to the local-renderer "Batch Generate Mugshots"
    /// — neither falls back to the other, so the user can run them
    /// independently and see exactly which source provided what.</summary>
    private async Task BatchDownloadFaceFinderMugshotsAsync()
    {
        if (!_lazyModListVM.IsValueCreated)
        {
            ScrollableMessageBox.ShowWarning("Mod list has not been initialized yet.", "Cannot Download Mugshots");
            return;
        }

        if (!UseFaceFinderFallback)
        {
            ScrollableMessageBox.ShowWarning(
                "FaceFinder is disabled. Enable \"Use FaceFinder API for missing mugshots\" before running this batch.",
                "FaceFinder Disabled");
            return;
        }

        var modSettings = _lazyModListVM.Value.AllModSettings.ToList();
        if (modSettings.Count == 0)
        {
            ScrollableMessageBox.Show("No mod settings found to process.", "No Mods");
            return;
        }

        // When caching is off, the batch can only ask the API "is this
        // available?" — no bytes get persisted. Warn the user up front so
        // they can either enable caching or back out, rather than running
        // a long batch that produces no on-disk artifacts.
        if (!CacheFaceFinderImages)
        {
            var confirmNoCache = MessageBox.Show(
                "FaceFinder image caching is OFF. The batch will query availability for every matched NPC " +
                "but will NOT download any images — they'll only be fetched on-demand at view time.\n\n" +
                "Continue with availability-only checks?",
                "FaceFinder Caching Disabled",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmNoCache != MessageBoxResult.Yes) return;
        }

        var linkCache = _environmentStateProvider.LinkCache;
        if (linkCache == null)
        {
            ScrollableMessageBox.ShowError(
                "The Skyrim load order isn't initialized; cannot resolve NPC records.",
                "Environment Not Ready");
            return;
        }

        // Mods with at least one resolvable NPC in the current load order.
        // We intentionally skip mods with empty NPC lists — the server-side
        // face enumeration would have nothing to intersect against, so the
        // mod-catalog and faces-for-mod calls would both be wasted work.
        var modsWithNpcs = new List<(VM_ModSetting Mod, Dictionary<string, (FormKey FormKey, string Display)> LocalByFormKeyString)>();
        foreach (var mod in modSettings)
        {
            if (mod.NpcFormKeysToDisplayName == null || mod.NpcFormKeysToDisplayName.Count == 0) continue;
            var localByFormKeyString = new Dictionary<string, (FormKey FormKey, string Display)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (fk, displayName) in mod.NpcFormKeysToDisplayName)
            {
                if (!linkCache.TryResolve<INpcGetter>(fk, out var npcGetter) || npcGetter == null) continue;
                // Mirror FaceFinderClient.GetFaceDataAsync's wire format so
                // the server's npc.form_key strings compare cleanly: uppercase
                // 8-char hex id + lowercase plugin filename.
                var keyStr = $"{fk.ID:X8}:{fk.ModKey.FileName.String.ToLowerInvariant()}";
                if (!localByFormKeyString.ContainsKey(keyStr))
                {
                    localByFormKeyString.Add(keyStr, (fk, displayName));
                }
            }
            if (localByFormKeyString.Count > 0)
            {
                modsWithNpcs.Add((mod, localByFormKeyString));
            }
        }

        if (modsWithNpcs.Count == 0)
        {
            ScrollableMessageBox.Show("No NPCs found to process.", "Nothing To Download");
            return;
        }

        // Build the progress window now so the user sees the catalog-index
        // phase up front rather than a frozen UI for the first several
        // seconds. The progress maximum is set to the number of mods until
        // we transition to the per-NPC download phase below.
        var progressVM = new VM_ProgressWindow
        {
            Title = "Batch Download FaceFinder Mugshots",
            StatusMessage = "Indexing FaceFinder mod catalog...",
            CurrentSubItem = string.Empty,
            EtaText = string.Empty,
            CancelButtonText = "Abort",
            ProgressMaximum = modsWithNpcs.Count,
            ProgressValue = 0,
            IsIndeterminate = true,
        };
        var progressWindow = new ProgressWindow { ViewModel = progressVM };
        progressWindow.ResizeMode = System.Windows.ResizeMode.CanMinimize;
        progressWindow.WindowStyle = System.Windows.WindowStyle.SingleBorderWindow;
        progressWindow.Topmost = false;
        progressWindow.ShowInTaskbar = true;
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

        int downloadedCount = 0;        // API hit, bytes fetched + cached.
        int availableCount = 0;         // API hit, caching off (not downloaded).
        int alreadyCachedCount = 0;     // Local cache file present + still current.
        int notAvailableCount = 0;      // Local NPC has no entry on server.
        int errorCount = 0;             // HTTP / parse error.
        int modsResolvedCount = 0;      // Local mods that matched a server mod.
        int modsNotOnServerCount = 0;   // Local mods absent from server catalog.
        // Snapshot the caching toggle once so a mid-batch flip can't
        // half-cache the run.
        bool cacheBytes = CacheFaceFinderImages;
        var etaCalculator = new EtaCalculator();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await Task.Run(async () =>
            {
                // Phase 1: index the server's mod catalog. One paginated
                // walk amortized across the whole batch.
                Dictionary<string, int> serverMods;
                try
                {
                    // v2 endpoint by default; reverts to the v1 amalgamation walk when
                    // FaceFinderSearchFallback.txt is present next to the exe.
                    serverMods = await _faceFinderClient.SearchAllModsAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FaceFinder batch: mod catalog fetch failed: {ex.Message}");
                    _eventLogger.Log($"FaceFinder batch: mod catalog fetch failed: {ex.Message}", "FACEFINDER");
                    serverMods = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                if (progressVM.IsCancellationRequested) return;

                // Phase 2: per-mod face enumeration + download. Build a flat
                // list of (mod, localNpcFormKey, displayName, serverFace)
                // tuples so the inner loop can drive the progress bar one
                // tick per NPC regardless of how the mod resolved.
                var workItems = new List<(VM_ModSetting Mod, FormKey Npc, string Display, FaceFinderModFaceResult? Face)>();
                int totalMods = modsWithNpcs.Count;
                int modIndex = 0;
                foreach (var (mod, localByFormKeyString) in modsWithNpcs)
                {
                    if (progressVM.IsCancellationRequested) break;
                    modIndex++;

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        progressVM.CurrentSubItem = mod.DisplayName;
                        progressVM.StatusMessage = $"Indexing FaceFinder mod catalog ({modIndex} of {totalMods})";
                    });

                    // Resolve the FaceFinder name via the existing manual
                    // mapping table (Settings > "Link Mod Names to
                    // FaceFinder"), falling back to the local display name.
                    string faceFinderModName = mod.DisplayName;
                    if (_model.FaceFinderModNameMappings.TryGetValue(mod.DisplayName, out var mappedNames)
                        && mappedNames.LastOrDefault() is { } lastMappedName)
                    {
                        faceFinderModName = lastMappedName;
                    }

                    if (!serverMods.TryGetValue(faceFinderModName, out int modId))
                    {
                        modsNotOnServerCount++;
                        // Every local NPC for this mod counts as "not on
                        // FaceFinder" — the per-NPC summary line should
                        // still tally them so the user sees the breakdown.
                        notAvailableCount += localByFormKeyString.Count;
                        // Still tick the progress bar by the mod's NPC count
                        // so the bar moves smoothly between mod transitions.
                        foreach (var entry in localByFormKeyString.Values)
                        {
                            workItems.Add((mod, entry.FormKey, entry.Display, null));
                        }
                        continue;
                    }

                    modsResolvedCount++;
                    List<FaceFinderModFaceResult> serverFaces;
                    try
                    {
                        // v2 endpoint by default; same FaceFinderSearchFallback.txt reverts to v1.
                        serverFaces = await _faceFinderClient.SearchFacesForModAsync(modId);
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        Debug.WriteLine($"FaceFinder batch: faces fetch failed for {mod.DisplayName} (modId={modId}): {ex.Message}");
                        _eventLogger.Log($"FaceFinder batch faces fetch failed for {mod.DisplayName}: {ex.Message}", "FACEFINDER");
                        // Still enqueue the NPCs so they get a progress tick.
                        foreach (var entry in localByFormKeyString.Values)
                        {
                            workItems.Add((mod, entry.FormKey, entry.Display, null));
                        }
                        continue;
                    }

                    var serverFacesByKey = new Dictionary<string, FaceFinderModFaceResult>(StringComparer.OrdinalIgnoreCase);
                    foreach (var face in serverFaces)
                    {
                        if (!serverFacesByKey.ContainsKey(face.FormKey))
                        {
                            serverFacesByKey.Add(face.FormKey, face);
                        }
                    }

                    foreach (var (keyStr, entry) in localByFormKeyString)
                    {
                        serverFacesByKey.TryGetValue(keyStr, out var face);
                        workItems.Add((mod, entry.FormKey, entry.Display, face));
                    }
                }

                if (progressVM.IsCancellationRequested) return;

                // Phase 3: drive the per-NPC cache-check / download loop
                // off the pre-built work plan.
                int totalNpcs = workItems.Count;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    progressVM.IsIndeterminate = false;
                    progressVM.ProgressMaximum = totalNpcs;
                    progressVM.ProgressValue = 0;
                });

                for (int i = 0; i < workItems.Count; i++)
                {
                    if (progressVM.IsCancellationRequested) break;

                    var (mod, npcFormKey, displayName, face) = workItems[i];
                    var itemStart = stopwatch.Elapsed;

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        progressVM.CurrentSubItem = $"{mod.DisplayName} — {displayName}";
                        progressVM.StatusMessage = $"Processing FaceFinder mugshots ({i + 1} of {totalNpcs})";
                    });

                    if (face == null)
                    {
                        // Already counted as notAvailable above (or counted
                        // as part of an errored mod); just advance the bar.
                    }
                    else
                    {
                        try
                        {
                            var ffResult = await _batchMugshotGenerator.ProcessKnownFaceAsync(
                                npcFormKey, mod.DisplayName, face.ImageUrl,
                                face.UpdatedAt, face.ExternalUrl,
                                downloadBytesIfHit: cacheBytes,
                                token: CancellationToken.None);

                            switch (ffResult.Source)
                            {
                                case GenerationSource.FaceFinderCache:
                                    alreadyCachedCount++;
                                    break;
                                case GenerationSource.FaceFinderDownload:
                                    if (ffResult.ProducedFile)
                                    {
                                        downloadedCount++;
                                    }
                                    else
                                    {
                                        // In-memory bytes only — shouldn't
                                        // happen for the batch path (caching
                                        // gate matches cacheBytes), but count
                                        // defensively rather than miscount.
                                        availableCount++;
                                    }
                                    break;
                                case GenerationSource.FaceFinderAvailable:
                                    availableCount++;
                                    break;
                                default:
                                    // ProcessKnownFaceAsync returned None
                                    // (UseFaceFinderFallback flipped off
                                    // mid-run, or an unexpected error
                                    // logged inside the helper).
                                    errorCount++;
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            errorCount++;
                            Debug.WriteLine($"FaceFinder batch failed for {npcFormKey} ({mod.DisplayName}): {ex.Message}");
                            _eventLogger.Log($"FaceFinder batch failed for {npcFormKey}: {ex.Message}", "FACEFINDER");
                        }

                    }

                    // Record the wall-clock cost of every item (instant cache hits and
                    // "not available" entries included) and estimate from recent
                    // throughput, so the ETA tracks the real download mix as it unfolds
                    // instead of lagging behind a cumulative average.
                    etaCalculator.RecordItem((stopwatch.Elapsed - itemStart).TotalSeconds);

                    int processed = i + 1;
                    int remaining = totalNpcs - processed;
                    TimeSpan? eta = etaCalculator.Estimate(remaining);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        progressVM.UpdateProgress(processed);
                        progressVM.SetEta(eta);
                    });
                }
            });
        }
        finally
        {
            stopwatch.Stop();
            try { progressWindow.Close(); }
            catch (Exception ex) { Debug.WriteLine($"Failed to close progress window: {ex.Message}"); }
        }

        int totalProcessed = downloadedCount + availableCount + alreadyCachedCount
                             + notAvailableCount + errorCount;
        var summary = new StringBuilder();
        int totalNpcsExpected = modsWithNpcs.Sum(m => m.LocalByFormKeyString.Count);
        summary.AppendLine(progressVM.IsCancellationRequested
            ? $"FaceFinder batch aborted after {totalProcessed} of {totalNpcsExpected} NPCs."
            : $"FaceFinder batch complete for {totalNpcsExpected} NPCs.");
        summary.AppendLine();
        summary.AppendLine($"Mods matched on server: {modsResolvedCount}");
        summary.AppendLine($"Mods not on FaceFinder: {modsNotOnServerCount}");
        summary.AppendLine();
        summary.AppendLine($"Downloaded:          {downloadedCount}");
        summary.AppendLine($"Already cached:      {alreadyCachedCount}");
        if (availableCount > 0)
        {
            summary.AppendLine($"Available (not cached): {availableCount}");
        }
        summary.AppendLine($"Not on FaceFinder:   {notAvailableCount}");
        if (errorCount > 0)
        {
            summary.AppendLine($"Errors:              {errorCount}");
        }
        summary.AppendLine();
        summary.AppendLine($"Elapsed: {FormatElapsed(stopwatch.Elapsed)}");

        ScrollableMessageBox.Show(summary.ToString(),
            progressVM.IsCancellationRequested ? "Download Aborted" : "Download Complete");
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m {elapsed.Seconds:D2}s";
        if (elapsed.TotalMinutes >= 1) return $"{elapsed.Minutes}m {elapsed.Seconds:D2}s";
        return $"{elapsed.Seconds}s";
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
                            // Persist the cache pruning immediately so an
                            // abnormal exit doesn't leave stale-on-disk
                            // entries that no longer exist.
                            RequestThrottledSave();
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

                        // Remove from cache + immediate throttled save so the
                        // entry doesn't survive the next session as a stale
                        // pointer to a now-deleted file.
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _model.GeneratedMugshotPaths.Remove(pngFile);
                            RequestThrottledSave();
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
                            RequestThrottledSave();
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
        CachedNonAppearanceMods = new ObservableCollection<CachedNonAppearanceModEntry>(
            _model.CachedNonAppearanceMods
                .OrderBy(kvp => Path.GetFileName(kvp.Key) ?? kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => new CachedNonAppearanceModEntry(
                    kvp.Key,
                    kvp.Value,
                    _model.CachedMissingMasterMods.TryGetValue(kvp.Key, out var missing) ? missing : null)));
        ApplyNonAppearanceFilter();
        UpdateHasMissingMasterMods();
    }

    private void UpdateHasMissingMasterMods()
    {
        HasMissingMasterMods = CachedNonAppearanceMods?.Any(e => e.IsMissingMasters) ?? false;
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

        IEnumerable<CachedNonAppearanceModEntry> itemsToDisplay;

        if (string.IsNullOrWhiteSpace(filter))
        {
            itemsToDisplay = sourceList;
        }
        else
        {
            // Filter is applied to the Path
            itemsToDisplay = sourceList.Where(entry =>
                (Path.GetFileName(entry.Path) ?? entry.Path).Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in itemsToDisplay)
        {
            FilteredNonAppearanceMods.Add(item);
        }
    }

    // ---- Per-NPC logging list (Settings > Logging) ----

    /// <summary>The user's selected NPCs, sourced from the main NPC menu so the
    /// rows carry the same display string / search fields. Empty until the NPC
    /// menu has been populated.</summary>
    private IEnumerable<VM_NpcsMenuSelection> GetLoggableNpcSource()
    {
        var bar = _lazyNpcSelectionBar.Value;
        var all = bar?.AllNpcs;
        if (all == null) return Enumerable.Empty<VM_NpcsMenuSelection>();
        return all.Where(n => _consistencyProvider.DoesNpcHaveSelection(n.NpcFormKey));
    }

    private VM_LoggedNpc BuildLoggedNpc(FormKey formKey)
    {
        var match = _lazyNpcSelectionBar.Value?.AllNpcs?.FirstOrDefault(n => n.NpcFormKey.Equals(formKey));
        if (match != null)
        {
            return new VM_LoggedNpc(formKey, match.DisplayName, match.NpcName, match.NpcEditorId);
        }
        // Selected NPC not present in the menu (e.g. no longer in the load order):
        // fall back to a FormKey-only row so it can still be seen and removed.
        return new VM_LoggedNpc(formKey, formKey.ToString(), string.Empty, string.Empty);
    }

    private void LoadLoggedNpcsFromModel()
    {
        LoggedNpcs.Clear();
        foreach (var loggedNpc in (_model.NpcsToLog ?? new List<FormKey>())
                 .Select(BuildLoggedNpc)
                 .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            LoggedNpcs.Add(loggedNpc);
        }
        UpdateNpcLogSearchResults();
    }

    private void UpdateNpcLogSearchResults()
    {
        NpcLogSearchResults.Clear();

        var text = NpcLogSearchText?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            HasNpcLogSearchResults = false;
            return;
        }

        var already = LoggedNpcs.Select(l => l.NpcFormKey).ToHashSet();

        var matches = GetLoggableNpcSource()
            .Where(n => !already.Contains(n.NpcFormKey))
            .Where(n =>
                (n.DisplayName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (n.NpcName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (n.NpcEditorId?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (n.NpcFormKeyString?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(n => new VM_LoggedNpc(n.NpcFormKey, n.DisplayName, n.NpcName, n.NpcEditorId))
            .ToList();

        foreach (var m in matches)
        {
            NpcLogSearchResults.Add(m);
        }

        HasNpcLogSearchResults = NpcLogSearchResults.Count > 0;
    }

    private void AddNpcToLog(VM_LoggedNpc npc)
    {
        if (npc == null || npc.NpcFormKey.IsNull) return;
        if (LoggedNpcs.Any(l => l.NpcFormKey.Equals(npc.NpcFormKey))) return;

        // Insert in sorted position to match the search/menu ordering.
        int insertIndex = 0;
        while (insertIndex < LoggedNpcs.Count &&
               string.Compare(LoggedNpcs[insertIndex].DisplayName, npc.DisplayName, StringComparison.OrdinalIgnoreCase) < 0)
        {
            insertIndex++;
        }
        LoggedNpcs.Insert(insertIndex, npc);

        if (_model.NpcsToLog == null) _model.NpcsToLog = new List<FormKey>();
        if (!_model.NpcsToLog.Contains(npc.NpcFormKey)) _model.NpcsToLog.Add(npc.NpcFormKey);

        NpcLogSearchText = string.Empty; // collapses the match dropdown
        UpdateNpcLogSearchResults();
        RequestThrottledSave();
    }

    private void RemoveNpcFromLog(VM_LoggedNpc npc)
    {
        if (npc == null) return;

        var existing = LoggedNpcs.FirstOrDefault(l => l.NpcFormKey.Equals(npc.NpcFormKey));
        if (existing != null) LoggedNpcs.Remove(existing);
        _model.NpcsToLog?.RemoveAll(fk => fk.Equals(npc.NpcFormKey));

        UpdateNpcLogSearchResults(); // removed NPC may re-appear as a match
        RequestThrottledSave();
    }

    private void AddIgnoredMod()
    {
        var dialog = new CommonOpenFileDialog
        {
            IsFolderPicker = true,
            Multiselect = true,
            Title = "Select folder(s) to ignore during mod import",
            InitialDirectory = GetSafeInitialDirectory(ModsFolder)
        };

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;

        var modsVm = _lazyModListVM.Value;

        foreach (var folderPath in dialog.FileNames)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) continue;

            // Skip if already ignored
            if (!_model.IgnoredMods.Add(folderPath)) continue;

            // Insert in sorted position
            string folderName = Path.GetFileName(folderPath) ?? folderPath;
            int insertIndex = 0;
            for (int i = 0; i < IgnoredMods.Count; i++)
            {
                string existingName = Path.GetFileName(IgnoredMods[i]) ?? IgnoredMods[i];
                if (string.Compare(folderName, existingName, StringComparison.OrdinalIgnoreCase) < 0)
                    break;
                insertIndex = i + 1;
            }
            IgnoredMods.Insert(insertIndex, folderPath);

            // Remove any existing VM_ModSettings that correspond to this folder
            var matchingModSettings = modsVm.AllModSettings
                .Where(ms => ms.CorrespondingFolderPaths.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
                .ToList();
            foreach (var modSetting in matchingModSettings)
            {
                modsVm.RemoveModSetting(modSetting);
            }
        }

        ApplyIgnoredModFilter();
    }

    private void ApplyIgnoredModFilter()
    {
        FilteredIgnoredMods.Clear();

        var filter = IgnoredModFilterText;

        IEnumerable<string> itemsToDisplay;

        if (string.IsNullOrWhiteSpace(filter))
        {
            itemsToDisplay = IgnoredMods;
        }
        else
        {
            itemsToDisplay = IgnoredMods.Where(path =>
                (Path.GetFileName(path) ?? path).Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in itemsToDisplay)
        {
            FilteredIgnoredMods.Add(item);
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
            _model.CachedMissingMasterMods.Remove(path);

            var itemToRemove = CachedNonAppearanceMods.FirstOrDefault(e => e.Path == path);
            if (itemToRemove != null)
            {
                CachedNonAppearanceMods.Remove(itemToRemove);

                // Also remove from the filtered list bound to the UI
                FilteredNonAppearanceMods.Remove(itemToRemove);
            }

            UpdateHasMissingMasterMods();

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
        sb.AppendLine("AutoSplitOutput: " + AutoSplitOutput);
        return sb.ToString();
    }

    private void UpdateDefaultOverrideVisibility()
    {
        // The entire row is visible when mode is not Ignore
        IsDefaultOverrideHandlingControlsVisible = SelectedRecordOverrideHandlingMode != RecordOverrideHandlingMode.Ignore;
        // Max Nested is visible only if mode is not Ignore AND "Include All" is not checked
        IsDefaultMaxNestedIntervalDepthVisible = SelectedRecordOverrideHandlingMode != RecordOverrideHandlingMode.Ignore && !DefaultIncludeAllOverrides;
    }

    public void Dispose()
    {
        _disposables.Dispose(); // Dispose all subscriptions
    }
}