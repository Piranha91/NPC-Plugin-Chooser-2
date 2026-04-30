using CharacterViewer.Rendering;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Mutagen.Bethesda.Strings;
using Newtonsoft.Json;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.View_Models; // Required for HashSet

namespace NPC_Plugin_Chooser_2.Models;

public class Settings
{
    public string ProgramVersion { get; set; } = string.Empty;
    public bool HasBeenLaunched { get; set; } = false;

    /// <summary>Bumped whenever a pixel-affecting render toggle is added that
    /// would otherwise invalidate every existing autogen mugshot. The C#
    /// initializer is -1 (sentinel) so deserializing from a pre-upgrade
    /// JSON (which has no SchemaVersion field) leaves it at -1; LoadSettings
    /// detects that and runs a one-shot migration that flips newly-added
    /// toggles to "legacy" defaults so the user's existing tiles aren't
    /// invalidated. Fresh installs (no Settings.json) bypass deserialize
    /// and are stamped with the current value directly.
    /// <para>Migration history:
    /// <list type="bullet">
    /// <item>0 → 1: 2.5.9 added <c>InternalMugshot.EnableToneMapping</c>.
    /// Migration sets it to <c>false</c> so pre-2.5.9 tiles keep matching
    /// the regenerated output.</item>
    /// <item>1 → 2: 2.5.10 added <c>InternalMugshot.EnableShadows</c>.
    /// Migration sets it to <c>false</c> for the same reason.</item>
    /// <item>2 → 3: 2.5.11 added <c>InternalMugshot.EnableAmbientOcclusion</c>.
    /// Migration sets it to <c>false</c> for the same reason.</item>
    /// <item>3 → 4: 2.5.12 added <c>InternalMugshot.SsaoRadius/Bias/Intensity</c>.
    /// No migration needed - the C# defaults match the hardcoded values
    /// 2.5.11 used, so v3-stamped tiles validate against v3 hash (which
    /// doesn't include these fields) regardless of what the user picks
    /// from the new UI sliders.</item>
    /// <item>4 → 5: 2.5.13 added <c>InternalMugshot.EnableEyeCatchlight</c>.
    /// Migration sets it to <c>false</c> on upgrade so existing autogen
    /// tiles aren't invalidated.</item>
    /// <item>5 → 6: 2.5.14 corrected the SSS math (proper wrap parameter,
    /// extracted baseColor multiplier, added back-scatter / translucency)
    /// AND added <c>InternalMugshot.SubsurfaceStrength</c>. Migration
    /// sets the strength to 0 on upgrade so the corrected pipeline
    /// produces zero SSS contribution - existing v5 tiles stay
    /// matching their stamped hash. Fresh installs get 2.0 (a noticeable
    /// boost matching the pronounced SSS in professional portrait
    /// reference).</item>
    /// <item>6 → 7: 2.5.15 made the tone-mapping vignette tunable via
    /// <c>InternalMugshot.VignetteRadius</c> + <c>VignetteIntensity</c>.
    /// Migration forces VignetteIntensity to 0 on upgrade so the
    /// vignette has no visible effect on existing v6 tiles when they
    /// re-render. Fresh installs default to Radius 0.7 / Intensity 0.3
    /// (approximates the pre-2.5.15 hardcoded vignette visual).</item>
    /// </list>
    /// </para></summary>
    public const int CurrentSchemaVersion = 7;
    public int SchemaVersion { get; set; } = -1;
    // Mod Environment
    public string ModsFolder { get; set; } = string.Empty;
    public string MugshotsFolder { get; set; } = string.Empty;
    public bool FilterByActiveModsMO2 { get; set; } = false;
    public string MO2ModlistPath { get; set; } = string.Empty;
    public Dictionary<string, string> CachedNonAppearanceMods { get; set; } = new(); // These have been examined and determined to not have NPC mods. Used to speed up startup
    public HashSet<string> IgnoredMods { get; set; } = new(); // Manually specified mod folders to skip during import

    // Game Environment
    public SkyrimRelease SkyrimRelease { get; set; } = SkyrimRelease.SkyrimSE;
    public string SkyrimGamePath { get; set; } = string.Empty;

    // Output Settings
    public string OutputDirectory { get; set; } = "NPC Output"; 
    public bool AppendTimestampToOutputDirectory { get; set; } = false; 
    public string OutputPluginName { get; set; } = string.Empty;
    public PatchingMode PatchingMode { get; set; } = PatchingMode.CreateAndPatch; 
    public bool UseSkyPatcherMode { get; set; } = false;
    public bool AutoEslIfy { get; set; } = true;
    // --- NEW: Split Output Settings ---
    public bool SplitOutput { get; set; } = false;
    public bool SplitOutputByGender { get; set; } = false;
    public bool SplitOutputByRace { get; set; } = false;
    public int? SplitOutputMaxNpcs { get; set; } = null; 

    
    // Default Overrideable Settings

    public RecordOverrideHandlingMode DefaultRecordOverrideHandlingMode { get; set; } = RecordOverrideHandlingMode.Ignore;
    public int DefaultMaxNestedIntervalDepth { get; set; } = 2;
    public bool DefaultIncludeAllOverrides { get; set; } = false;

    // UI / Other
    public bool ShowNpcDescriptions { get; set; } = true;
    public bool ShowSingleOptionNpcs { get; set; } = true;
    public bool ShowUnloadedNpcs { get; set; } = true;
    public bool ShowSkyPatcherTemplates { get; set; } = false;
    public bool ShowUninstalledMods { get; set; } = true;
    public bool AutoAdvanceAfterSelection { get; set; } = true;
    public List<ModSetting> ModSettings { get; set; } = new();
    // The string is the ModName, the FormKey is the NPC within that mod providing the appearance.
    public Dictionary<FormKey, (string ModName, FormKey NpcFormKey)> SelectedAppearanceMods { get; set; } = new();
    // Key: FormKey of NPC receiving the appearance.
    // Value: A set of tuples, where each tuple represents a "guest" mugshot.
    // Tuple: (string ModName of the guest appearance, FormKey of the guest NPC, string DisplayName of the guest NPC).
    public Dictionary<FormKey, HashSet<(string ModName, FormKey NpcFormKey, string NpcDisplayName)>> GuestAppearances { get; set; } = new();
    public HashSet<string> HiddenModNames { get; set; } = new();
    public Dictionary<FormKey, HashSet<string>> HiddenModsPerNpc { get; set; } = new();
    public HashSet<FormKey> CachedSkyPatcherTemplates { get; set; } = new();
    public Dictionary<FormKey, HashSet<string>> NpcGroupAssignments { get; set; } = new();
    public Dictionary<FormKey, OutfitOverride> NpcOutfitOverrides { get; set; } = new();
    public HashSet<ModKey> ImportFromLoadOrderExclusions { get; set; } = new();
    public HashSet<(FormKey NpcFormKey, string ModName)> FavoriteFaces { get; set; } = new();
    public bool NormalizeImageDimensions { get; set; } = false;
    public int MaxMugshotsToFit { get; set; } = 50;
    public int MaxNpcsPerPageSummaryView { get; set; } = 100;
    public bool SuppressPopupWarnings { get; set; } = false;
    public Language? LocalizationLanguage { get; set; } = null;
    public bool IsDarkMode { get; set; } = true;
    public string? ThemeName { get; set; }
    public string TabStyle { get; set; } = "Underline";
    public string NpcSelectionIndicator { get; set; } = "Text Color";
    public string CotRKeyword { get; set; } = "CotR";
    
    // --- NPC Display ---
    public bool ShowNpcNameInList { get; set; } = true;
    public bool ShowNpcEditorIdInList { get; set; }
    public bool ShowNpcFormKeyInList { get; set; }
    public bool ShowNpcFormIdInList { get; set; }
    public string NpcListSeparator { get; set; } = " | ";
    public bool ShowTemplateStatusInList { get; set; } = true;
    public TemplateIconPosition TemplateIconPosition { get; set; } = TemplateIconPosition.Right;

    // EasyNPC Interchangeability / Settings
    public Dictionary<FormKey, ModKey> EasyNpcDefaultPlugins { get; set; } = new(); 
    public HashSet<ModKey> EasyNpcDefaultPluginExclusions { get; set; } = new() { ModKey.FromFileName("Synthesis.esp")};
    public bool AddMissingNpcsOnUpdate { get; set; } = false;
    
    // Bat File Settings
    public string BatFilePreCommands { get; set; } = string.Empty;
    public string BatFilePostCommands { get; set; } = string.Empty;

    // Zoom Control Settings
    public double NpcsViewZoomLevel { get; set; } = 100.0; 
    public bool NpcsViewIsZoomLocked { get; set; } = false;
    public double ModsViewZoomLevel { get; set; } = 100.0; 
    public bool ModsViewIsZoomLocked { get; set; } = false;
    public double SummaryViewZoomLevel { get; set; } = 100.0;
    public bool SummaryViewIsZoomLocked { get; set; } = false;

    // Last Selected NPC ***
    public FormKey LastSelectedNpcFormKey { get; set; } // Will be FormKey.Null if none or invalid
    
    // --- Mugshot Fallback Settings ---
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(false)]
    public bool UseFaceFinderFallback { get; set; } = false;
    
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(false)]
    public bool LogFaceFinderRequests { get; set; } = false;
    
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(true)]
    public bool CacheFaceFinderImages { get; set; } = true;
    public HashSet<string> CachedFaceFinderPaths { get; set; } = new();
    public MugshotSearchMode SelectedMugshotSearchModeFF { get; set; } = MugshotSearchMode.Fast;
    public Dictionary<string, List<string>> FaceFinderModNameMappings { get; set; } = new();

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(false)]
    public bool UsePortraitCreatorFallback { get; set; } = false;

    // Which mugshot renderer to use when UsePortraitCreatorFallback fires.
    // Internal = in-process .NET CharacterViewer; Legacy = NPC Portrait Creator subprocess.
    public MugshotRenderer SelectedRenderer { get; set; } = MugshotRenderer.Internal;

    // Configuration block for the Internal renderer. Persisted as a nested object
    // so the legacy fields below aren't shadowed when the user toggles back.
    public InternalMugshotSettings InternalMugshot { get; set; } = new();

    // -- Portrait Creator Parameters --
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    [DefaultValue(4)]
    public int MaxParallelPortraitRenders { get; set; } = 4;    
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    [DefaultValue(true)]
    public bool AutoUpdateOldMugshots { get; set; } = true;
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    [DefaultValue(true)]
    public bool AutoUpdateStaleMugshots { get; set; } = true;
    
    [JsonConverter(typeof(PortraitCameraModeConverter))]
    public PortraitCameraMode SelectedCameraMode { get; set; } = PortraitCameraMode.Portrait;
    // These should match the defaults or CLI options in your C++ app
    public string DefaultLightingJsonString { get; set; } = @"
{
    ""lights"": [
        {
            ""color"": [
                1.0,
                0.8799999952316284,
                0.699999988079071
            ],
            ""intensity"": 0.6499999761581421,
            ""type"": ""ambient""
        },
        {
            ""color"": [
                1.0,
                0.8500000238418579,
                0.6499999761581421
            ],
            ""direction"": [
                -0.0798034518957138,
                -0.99638432264328,
                -0.029152285307645798
            ],
            ""intensity"": 1.600000023841858,
            ""type"": ""directional""
        },
        {
            ""color"": [
                1.0,
                0.8700000047683716,
                0.6800000071525574
            ],
            ""direction"": [
                0.12252168357372284,
                -0.6893905401229858,
                0.7139532566070557
            ],
            ""intensity"": 0.800000011920929,
            ""type"": ""directional""
        }
    ]
}";
    [JsonConverter(typeof(ColorJsonConverter))] // Apply the converter
    public Color MugshotBackgroundColor { get; set; } = Color.FromRgb(58, 61, 64);
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(true)]
    public bool EnableNormalMapHack { get; set; } = true;
    
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(true)]
    public bool UseModdedFallbackTextures { get; set; } = true;

    
    public float VerticalFOV { get; set; } = 25;
    
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(0.20f)]
    public float HeadTopOffset { get; set; } = 0.0f;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(-0.05f)]
    public float HeadBottomOffset { get; set; } = -0.05f;
    
    // Fixed camera position properties
    public float CamX { get; set; } = 0.0f;
    public float CamY { get; set; } = 0.0f;
    public float CamZ { get; set; } = 0.0f;
    public float CamPitch { get; set; } = 2.0f;
    public float CamYaw { get; set; } = 90.0f;
    public float CamRoll { get; set; } = 0.0f;
    
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(750)]
    public int ImageXRes { get; set; } = 750;
    
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(750)]
    public int ImageYRes { get; set; } = 750;
    public HashSet<string> GeneratedMugshotPaths { get; set; } = new();
    public MugshotSearchMode SelectedMugshotSearchModePC { get; set; } = MugshotSearchMode.Fast;
    
    // --- Window State Properties ---
    public double MainWindowTop { get; set; } = 100; // Default a reasonable position
    public double MainWindowLeft { get; set; } = 100;
    public double MainWindowHeight { get; set; } = 700; // Default to your design height
    public double MainWindowWidth { get; set; } = 1000; // Default to your design width
    public WindowState MainWindowState { get; set; } = WindowState.Normal;
    
    // --- Manual Update Logging
    public bool HasUpdatedTo2_0_7 { get; set; } = false;
    public bool HasUpdatedTo2_0_7_templates { get; set; } = false;
    
    // --- Troubleshooting ---
    public bool LogActivity { get; set; } = false;
    public bool LogStartup { get; set; } = false;
    public bool FixGarbledText { get; set; } = true;
}

public enum TemplateIconPosition
{
    Left,
    Right
}

public enum MugshotRenderer
{
    Internal,            // default — in-process CharacterViewer.Rendering
    LegacyPortraitCreator,
}

public enum InternalMugshotCameraMode
{
    Auto,    // CameraFraming.MeshAware — head + hair-above-head's-bottom
    Manual,  // user's saved Distance/Azimuth/Elevation/Target
}

public sealed class InternalMugshotSettings
{
    public InternalMugshotCameraMode CameraMode { get; set; } = InternalMugshotCameraMode.Auto;

    // Auto-mode tunables (mirror Portrait Creator's existing knobs).
    public float HeadTopFraction { get; set; } = 1.0f;
    public float HeadBottomFraction { get; set; } = 0.0f;
    public float Yaw { get; set; } = 180f;
    public float Pitch { get; set; } = 4.5f;
    public float HairAbovePadding { get; set; } = 0f;
    public bool IncludeAccessories { get; set; } = true;

    // Manual-mode camera state — saved on every drag-end in the live preview.
    public float ManualDistance { get; set; } = 200f;
    public float ManualAzimuth { get; set; } = 180f;
    public float ManualElevation { get; set; } = 0f;
    public float ManualTargetX { get; set; } = 0f;
    public float ManualTargetY { get; set; } = 120f;
    public float ManualTargetZ { get; set; } = 0f;

    // Lighting: named preset selected in the preview's lighting dropdown.
    public string LightingLayoutName { get; set; } = "";
    public string LightingColorSchemeName { get; set; } = "";

    // FBO clear color.
    public byte BackgroundR { get; set; } = 105;
    public byte BackgroundG { get; set; } = 105;
    public byte BackgroundB { get; set; } = 105;

    // Saved PNG dimensions.
    public int OutputWidth { get; set; } = 750;
    public int OutputHeight { get; set; } = 750;

    // Verbose log toggle bound through the settings adapter.
    public bool VerboseLog { get; set; } = false;

    // Advanced asset-resolution toggles. Pushed onto every OffscreenRenderRequest
    // and per-load on VM_CharacterViewer (CharacterViewer.Rendering 2.3.0+).
    // VanillaLooseOverridesBsa: default true, mirrors Skyrim's actual rule that
    // a loose Data file overrides any BSA copy. Off = strict-BSA (preview the
    // original mod content without the user's installed loose-file overrides).
    // VanillaLooseOverridesModLoose: default false. On, vanilla LOOSE files
    // (never vanilla BSA) preempt mod-folder loose files for non-FaceGen paths,
    // letting the user's installed body/skin/texture replacers leak into
    // mod-specific previews. The FaceGenData tree is excluded regardless so the
    // mod's actual face overrides aren't defeated by vanilla copies.
    public bool VanillaLooseOverridesBsa { get; set; } = true;
    public bool VanillaLooseOverridesModLoose { get; set; } = false;

    // When true (default), shapes whose diffuse texture failed to load are
    // rendered as a green wireframe placeholder so the missing-texture state
    // is visible alongside the missing-asset overlay. Off: those shapes are
    // silently culled (cleaner preview at the cost of hiding the failure).
    public bool RenderMissingTextureAsWireframe { get; set; } = true;

    // Portrait-quality rendering toggles (CharacterViewer.Rendering 2.5.9+).
    // Each gates a feature in the in-process renderer that improves the
    // "looks-like-a-portrait vs. looks-like-a-render" perception. Defaults
    // to true for NEW installs; on upgrade we detect the absence of these
    // fields via Settings.SchemaVersion and run a one-shot migration that
    // flips them to false to preserve the pre-upgrade look on existing
    // autogen tiles.
    public bool EnableToneMapping { get; set; } = true;

    // Shadow-map toggle (CharacterViewer.Rendering 2.5.10+). Enables a
    // depth-only render pass from the key light's POV, sampled with PCF
    // in the main fragment shader to cast real shadows from brow / nose /
    // hair onto the face. Single-largest portrait-quality jump after
    // tone-mapping.
    public bool EnableShadows { get; set; } = true;

    // Screen-space ambient occlusion toggle (2.5.11+). Adds soft shadowing
    // in concave crevices (eye sockets, nostrils, lip line, ear creases)
    // by sampling depth in a hemisphere around each fragment. Smaller
    // visual impact than shadow maps but fills in micro-detail darkening
    // that real photography has plenty of and a flat render lacks.
    public bool EnableAmbientOcclusion { get; set; } = true;

    // SSAO tunables (2.5.12+). All three only matter when
    // EnableAmbientOcclusion is on. Defaults match the hardcoded
    // values that shipped in 2.5.11; existing v3-stamped tiles
    // (which baked these defaults implicitly) stay valid because the
    // schema-versioned hash for v3 doesn't include these fields.
    //
    // - Radius: how far away (world units) an occluder can be and
    //   still contribute to the sample. Larger = softer, broader
    //   AO; smaller = tight crevice-only AO. Skyrim NPC heads are
    //   ~22 units tall, so values ~2-8 are typical.
    // - Bias: minimum depth difference (world units) before a sample
    //   counts as occluded. Higher values reduce self-shadowing
    //   artifacts on flat surfaces; too high erases real AO.
    // - Intensity: power-curve exponent on the final occlusion
    //   factor. Higher = harder darkening in deep crevices, more
    //   subtle elsewhere; lower = uniformly darker AO. Typical
    //   range 0.5-4.0.
    public float SsaoRadius { get; set; } = 4.0f;
    public float SsaoBias { get; set; } = 0.05f;
    public float SsaoIntensity { get; set; } = 1.5f;

    // Eye catch-light toggle (2.5.13+). When on, eye shapes get a tight,
    // bright specular highlight from the key light layered on top of
    // their normal Blinn-Phong specular. Real portrait photography
    // always has a visible catch-light in the iris - it's the single
    // biggest "alive vs. dead" cue for eyes.
    public bool EnableEyeCatchlight { get; set; } = true;

    // Subsurface scattering strength multiplier (2.5.14+). The renderer's
    // SSS math now uses subsurfaceRolloff as the proper wrap parameter
    // (per Bethesda BSLighting spec) and adds a back-scatter / translucency
    // term for thin-area light transmission (ear edges, nostril rims,
    // backlit cheeks). This multiplier lets users dial the visible
    // strength up or down without needing to override per-NIF rolloff
    // values. 1.0 is "honest" SSS at the source values; 2.0 (the default)
    // boosts the warm-flesh look toward the more pronounced SSS in
    // professional portrait reference.
    public float SubsurfaceStrength { get; set; } = 2.0f;

    // Vignette params (2.5.15+) for the tone-mapping path's subtle
    // radial darkening. Folded under EnableToneMapping (no separate
    // toggle), so toggling tone-mapping off bypasses the vignette
    // regardless of these values. Intensity = 0 turns the vignette off
    // even when tone-mapping is on.
    //   Radius (NDC, ~0..1.4): pixels within this distance of screen
    //     center are unaffected; falloff smoothsteps from here out to
    //     the corner. Lower = vignette closes in toward the center.
    //   Intensity (0..1): how dark the corner pixels go. 0 = off,
    //     1 = corners to black. Pre-2.5.15 hardcoded behavior is
    //     approximately Radius 0.7 / Intensity 0.3.
    public float VignetteRadius { get; set; } = 0.7f;
    public float VignetteIntensity { get; set; } = 0.3f;

    // User-defined lighting presets persisted across sessions. The settings
    // adapter wraps these in ObservableCollections at runtime.
    public List<CharacterViewerLightingLayout> UserLightingLayouts { get; set; } = new();
    public List<CharacterViewerLightingColorScheme> UserLightingColorSchemes { get; set; } = new();
}