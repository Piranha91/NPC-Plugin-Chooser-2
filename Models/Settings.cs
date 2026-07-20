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
    /// matching their stamped hash. Fresh installs originally defaulted
    /// to 2.0 (a noticeable boost matching pronounced SSS in professional
    /// portrait reference), but 2.0 desaturates high-chroma races; the
    /// 2.1.7 program-version migration in <c>UpdateHandler</c> revised
    /// the default to 0.1 (faint warmth). The schema-side upgrade target
    /// stays 0 — it's a hash-stability anchor, not a user-facing default.</item>
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

    /// <summary>
    /// Dedicated folder for FaceFinder downloads/cache. When empty, falls back
    /// to <see cref="GetDefaultFaceFinderMugshotsFolder"/> (<c>&lt;BaseDir&gt;/FaceFinder Cache</c>).
    /// Decoupled from <see cref="MugshotsFolder"/> as of 2026 so the user-curated
    /// mugshot library and the FaceFinder cache can live in separate roots.
    /// </summary>
    public string FaceFinderMugshotsFolder { get; set; } = string.Empty;

    /// <summary>
    /// Dedicated folder for auto-generated mugshots (Internal renderer + Legacy
    /// Portrait Creator output). When empty, falls back to
    /// <see cref="GetDefaultAutogenMugshotsFolder"/> (<c>&lt;BaseDir&gt;/AutoGen Mugshots</c>).
    /// </summary>
    public string AutogenMugshotsFolder { get; set; } = string.Empty;

    /// <summary>Default fallback path for the FaceFinder cache folder.</summary>
    public static string GetDefaultFaceFinderMugshotsFolder() =>
        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FaceFinder Cache");

    /// <summary>Default fallback path for the auto-generated mugshots folder.</summary>
    public static string GetDefaultAutogenMugshotsFolder() =>
        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AutoGen Mugshots");

    /// <summary>Returns the configured FaceFinder folder, or the default if unset.</summary>
    public static string GetEffectiveFaceFinderMugshotsFolder(Settings s) =>
        string.IsNullOrWhiteSpace(s.FaceFinderMugshotsFolder)
            ? GetDefaultFaceFinderMugshotsFolder()
            : s.FaceFinderMugshotsFolder;

    /// <summary>Returns the configured Autogen folder, or the default if unset.</summary>
    public static string GetEffectiveAutogenMugshotsFolder(Settings s) =>
        string.IsNullOrWhiteSpace(s.AutogenMugshotsFolder)
            ? GetDefaultAutogenMugshotsFolder()
            : s.AutogenMugshotsFolder;

    public bool FilterByActiveModsMO2 { get; set; } = false;
    public string MO2ModlistPath { get; set; } = string.Empty;
    public Dictionary<string, string> CachedNonAppearanceMods { get; set; } = new(); // These have been examined and determined to not have NPC mods. Used to speed up startup
    // Path -> list of master plugin filenames that were not found in any mod folder
    // at scan time. Subset of CachedNonAppearanceMods (every key here is also a key
    // there). Drives the red-highlight + warning UX for actionable scan failures
    // (user can install the master and click refresh to retry).
    public Dictionary<string, List<string>> CachedMissingMasterMods { get; set; } = new();
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
    // Automatically split the output plugin into multiple files (<name>.esp, <name>_2.esp, ...)
    // if it would otherwise exceed Skyrim's 255-master limit. Only triggers on overflow.
    public bool AutoSplitOutput { get; set; } = true;
    // --- NEW: Split Output Settings ---
    public bool SplitOutput { get; set; } = false;
    public bool SplitOutputByGender { get; set; } = false;
    public bool SplitOutputByRace { get; set; } = false;
    public int? SplitOutputMaxNpcs { get; set; } = null; 

    
    // Default Overrideable Settings

    public RecordOverrideHandlingMode DefaultRecordOverrideHandlingMode { get; set; } = RecordOverrideHandlingMode.Ignore;
    public int DefaultMaxNestedIntervalDepth { get; set; } = 2;
    public bool DefaultIncludeAllOverrides { get; set; } = false;
    public WigHandlingMode DefaultWigHandlingMode { get; set; } = WigHandlingMode.ForwardToSkin;

    // Antlers are handled independently of hair-slot wigs (see AntlerHandlingMode).
    // Default ForwardToSkin preserves the pre-split behavior (the old unified mode
    // forwarded antlers to the skin), so existing installs are unchanged; an absent
    // field in an old Settings.json deserializes to this default (no migration).
    public AntlerHandlingMode DefaultAntlerHandlingMode { get; set; } = AntlerHandlingMode.ForwardToSkin;

    // User-designated antler head parts, per mod (keyed by ModSetting.DisplayName),
    // from the 3D preview's "Set Antler Head Parts" selector — for antler head
    // parts whose names lack the "antler" keyword (e.g. non-intelligible names the
    // scan can't detect). Stored on the root Settings (not ModSetting) so it
    // survives VM_Mods.SaveModSettingsToModel, which rebuilds ModSettings from the
    // VM list. Folded into the effective antler head-part set by
    // GetEffectiveAntlerHeadParts. Additive to DetectedAntlerHeadParts.
    public Dictionary<string, HashSet<FormKey>> ManualAntlerHeadPartsByMod { get; set; } = new();

    /// <summary>True when wig/antler handling is active for the current output
    /// mode: Create-and-Patch record mode, or SkyPatcher output in either
    /// PatchingMode. Inert in plain Create record mode.</summary>
    private bool WigHandlingActiveForOutputMode =>
        UseSkyPatcherMode || PatchingMode == PatchingMode.CreateAndPatch;

    /// <summary>
    /// The wig handling mode that will actually apply to
    /// <paramref name="modSetting"/> on the next patch run: the per-mod override
    /// when set, else the global default — and None whenever wig handling is inert
    /// (no wig ARMOs detected, or plain Create record mode; Create-and-Patch and
    /// SkyPatcher output both activate it). Wig-class only (antlers are gated
    /// separately by <see cref="GetEffectiveAntlerMode"/>). Centralized so the
    /// patcher, renderer, metadata stamp, and staleness checker all agree.
    /// </summary>
    public WigHandlingMode GetEffectiveWigMode(ModSetting? modSetting)
    {
        if (modSetting == null || !modSetting.HasWigArmors) return WigHandlingMode.None;
        if (!WigHandlingActiveForOutputMode) return WigHandlingMode.None;
        return modSetting.ModWigHandlingMode ?? DefaultWigHandlingMode;
    }

    /// <summary>
    /// The antler handling mode that will actually apply to
    /// <paramref name="modSetting"/> on the next patch run. Mirrors
    /// <see cref="GetEffectiveWigMode"/> but gates on the antler detection sets
    /// (any of ARMO / ArmorAddon / HeadPart) and resolves against
    /// <see cref="DefaultAntlerHandlingMode"/>.
    /// </summary>
    public AntlerHandlingMode GetEffectiveAntlerMode(ModSetting? modSetting)
    {
        if (modSetting == null || !ModHasAntlers(modSetting)) return AntlerHandlingMode.None;
        if (!WigHandlingActiveForOutputMode) return AntlerHandlingMode.None;
        return modSetting.ModAntlerHandlingMode ?? DefaultAntlerHandlingMode;
    }

    /// <summary>The user's manually-designated antler head parts for
    /// <paramref name="modName"/> (empty when none). See
    /// <see cref="ManualAntlerHeadPartsByMod"/>.</summary>
    public IReadOnlyCollection<FormKey> GetManualAntlerHeadParts(string? modName)
    {
        if (!string.IsNullOrEmpty(modName) &&
            ManualAntlerHeadPartsByMod.TryGetValue(modName!, out var set) && set != null)
        {
            return set;
        }
        return Array.Empty<FormKey>();
    }

    /// <summary>The full antler head-part set for a mod: scan-detected
    /// (<see cref="ModSetting.DetectedAntlerHeadParts"/>) plus user-designated
    /// (<see cref="ManualAntlerHeadPartsByMod"/>). Single source of truth for
    /// antler head-part removal (patcher, renderer, validator).</summary>
    public HashSet<FormKey> GetEffectiveAntlerHeadParts(ModSetting? modSetting)
    {
        if (modSetting == null) return new HashSet<FormKey>();
        var manual = GetManualAntlerHeadParts(modSetting.DisplayName);
        if (manual.Count == 0) return modSetting.DetectedAntlerHeadParts;
        var set = new HashSet<FormKey>(modSetting.DetectedAntlerHeadParts);
        set.UnionWith(manual);
        return set;
    }

    /// <summary>Whether a mod has any antlers at all — scan-detected OR
    /// user-designated head parts. The effective gate for antler handling and
    /// the per-mod dropdown's visibility (a manual-only mod still needs the
    /// mode dropdown so its designation can be set to Remove).</summary>
    public bool ModHasAntlers(ModSetting? modSetting)
    {
        if (modSetting == null) return false;
        return modSetting.HasDetectedAntlers || GetManualAntlerHeadParts(modSetting.DisplayName).Count > 0;
    }

    /// <summary>True when either wig or antler handling will act on
    /// <paramref name="modSetting"/> this run — the patcher's outer gate.</summary>
    public bool WigOrAntlerHandlingActive(ModSetting? modSetting) =>
        GetEffectiveWigMode(modSetting) != WigHandlingMode.None ||
        GetEffectiveAntlerMode(modSetting) != AntlerHandlingMode.None;

    /// <summary>
    /// The wig handling mode the RENDERER depicts for <paramref name="modSetting"/>.
    /// Identical to <see cref="GetEffectiveWigMode"/> except that the dev/harness
    /// override <see cref="InternalMugshotSettings.WigModeOverride"/> (when set and
    /// the mod has wig detections) wins regardless of output mode, so RenderHarness
    /// variants can A/B the modes without touching the patching settings.
    /// </summary>
    public WigHandlingMode GetEffectiveRenderWigMode(ModSetting? modSetting)
    {
        if (modSetting == null || !modSetting.HasWigArmors) return WigHandlingMode.None;
        if (InternalMugshot.WigModeOverride is { } forced) return forced;
        return GetEffectiveWigMode(modSetting);
    }

    /// <summary>The antler handling mode the RENDERER depicts. Mirrors
    /// <see cref="GetEffectiveRenderWigMode"/> with the antler harness override
    /// <see cref="InternalMugshotSettings.AntlerModeOverride"/>.</summary>
    public AntlerHandlingMode GetEffectiveRenderAntlerMode(ModSetting? modSetting)
    {
        if (modSetting == null || !ModHasAntlers(modSetting)) return AntlerHandlingMode.None;
        if (InternalMugshot.AntlerModeOverride is { } forced) return forced;
        return GetEffectiveAntlerMode(modSetting);
    }

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
    // Subset of GuestAppearances created by the Randomize feature (NOT manual shares).
    // Tracked so a re-randomize can remove its own previous shares without disturbing the
    // user's curated/manual shares. Same tuple shape as GuestAppearances.
    public Dictionary<FormKey, HashSet<(string ModName, FormKey NpcFormKey, string NpcDisplayName)>> RandomizedGuestAppearances { get; set; } = new();
    // NPCs whose CURRENT selection was assigned by the Randomize feature (own face OR shared),
    // mapped to the (mod, sourceNpc) it assigned. Lets "Clear Randomized NPCs" deselect only the
    // NPCs still carrying a randomized choice (manual overrides are left alone). Same shape as
    // SelectedAppearanceMods.
    public Dictionary<FormKey, (string ModName, FormKey NpcFormKey)> RandomizedSelections { get; set; } = new();
    public HashSet<string> HiddenModNames { get; set; } = new();
    public Dictionary<FormKey, HashSet<string>> HiddenModsPerNpc { get; set; } = new();
    public HashSet<FormKey> CachedSkyPatcherTemplates { get; set; } = new();
    public Dictionary<FormKey, HashSet<string>> NpcGroupAssignments { get; set; } = new();
    public Dictionary<FormKey, OutfitOverride> NpcOutfitOverrides { get; set; } = new();

    // Per-NPC override of the character-preview / mugshot attire toggles
    // (Include Default Outfit / Include Headgear). Keyed by the NPC record that
    // is actually rendered (the appearance source NPC) — for the common case of
    // a mod overriding the same NPC, that equals the NPC selected in the list.
    // When an entry exists with OverrideGlobalAttire == true, the renderer,
    // mugshot generator, metadata stamp, and staleness checker all use the
    // per-NPC IncludeDefaultOutfit / IncludeHeadgear instead of the global
    // InternalMugshot values. See GetEffectiveAttireFlags.
    public Dictionary<FormKey, NpcRenderOverride> NpcRenderOverrides { get; set; } = new();

    /// <summary>
    /// Returns the effective Include Default Outfit / Include Headgear flags for
    /// rendering <paramref name="npcFormKey"/>: the per-NPC override when one is
    /// present and enabled, otherwise the global <see cref="InternalMugshot"/>
    /// values. Centralized so the renderer, metadata stamp, and staleness checker
    /// all agree on what a given NPC's mugshot should depict.
    /// </summary>
    public (bool IncludeDefaultOutfit, bool IncludeHeadgear) GetEffectiveAttireFlags(FormKey npcFormKey)
    {
        bool outfit, headgear;
        if (NpcRenderOverrides.TryGetValue(npcFormKey, out var ovr) && ovr != null && ovr.OverrideGlobalAttire)
        {
            (outfit, headgear) = (ovr.IncludeDefaultOutfit, ovr.IncludeHeadgear);
        }
        else
        {
            (outfit, headgear) = (InternalMugshot.IncludeDefaultOutfit, InternalMugshot.IncludeHeadgear);
        }
        // Outfit is the dominant toggle: headgear is part of the outfit, so it
        // never renders on its own (Outfit Off / Headgear On == Outfit Off /
        // Headgear Off). Normalizing here keeps the renderer, metadata stamp and
        // staleness checker agreed, and avoids a spurious re-render when the
        // (inert) headgear flag changes while the outfit is off.
        return (outfit, headgear && outfit);
    }
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

    // Order in which the three mugshot sources are tried at resolution time.
    // The list is user-rearrangeable via a drag-and-drop widget in the settings
    // menu. Disabled sources (UseFaceFinderFallback off / UsePortraitCreatorFallback
    // off / no curated MugshotsFolder) are skipped in-place rather than reordered,
    // so re-enabling restores the user's previous priority choice. LoadSettings
    // back-fills missing entries so old JSONs lacking the field load cleanly.
    public List<MugshotSourceType> MugshotSourcePriority { get; set; } = new()
    {
        MugshotSourceType.DownloadedMugshots,
        MugshotSourceType.FaceFinder,
        MugshotSourceType.AutoGeneration,
    };

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

    /// <summary>When on, an Internal-renderer mugshot whose stamped metadata
    /// records any missing meshes or textures is treated as stale, prompting
    /// the next session to re-render it (and pick up newly-installed assets).
    /// Off keeps the wireframe/placeholder PNG in place across sessions.
    /// Independent of <see cref="AutoUpdateStaleMugshots"/>, which gates the
    /// settings-hash drift check.</summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    [DefaultValue(true)]
    public bool AutoUpdateMugshotsWithMissingAssets { get; set; } = true;

    /// <summary>Controls the "Generate All Mugshots" batch: when on, NPCs whose
    /// Internal-renderer render reports any missing meshes or textures are
    /// skipped (no PNG is written) so the gallery only persists complete
    /// renders. Off lets the wireframe-placeholder PNG be saved as before.
    /// Per-tile renders (clicking an NPC) ignore this — the user there wants
    /// to see the wireframe and the overlay rather than a silent skip.</summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    [DefaultValue(true)]
    public bool AssetValidatedMugshotsOnly { get; set; } = true;

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

    // ── FaceGen Analysis (per-mugshot polycount / size overlay) ───────────────
    // Opt-in. When on, each tile parses its FaceGen NIF (or just stats its file
    // size) to surface authoring metrics — chiefly for spotting absurdly heavy
    // hair / head meshes. Persisted stats live in FaceGenAnalysisCache below,
    // SHA-keyed so a mod re-install / author update auto-invalidates the entry.
    public bool EnableFaceGenAnalysis { get; set; } = false;
    public bool ReportFaceGenSize { get; set; } = true;
    public bool ReportFaceGenPolys { get; set; } = true;
    public bool ReportFaceGenVerts { get; set; } = false;
    public FaceGenAnalysisDisplayMode FaceGenDisplayMode { get; set; } = FaceGenAnalysisDisplayMode.TextOverlay;
    public double FaceGenTextHeightPercent { get; set; } = 8.0;
    public FaceGenTooltipPosition FaceGenTooltipPosition { get; set; } = FaceGenTooltipPosition.CenterLeft;
    public FaceGenHighlightCriterion FaceGenHighlightCriterion { get; set; } = FaceGenHighlightCriterion.Spectrum;
    public double FaceGenHighlightThreshold { get; set; } = 25.0;
    [JsonConverter(typeof(ColorJsonConverter))]
    public Color FaceGenHighlightColor { get; set; } = Colors.Red;
    [JsonConverter(typeof(ColorJsonConverter))]
    public Color FaceGenNoHighlightColor { get; set; } = Colors.White;
    [JsonConverter(typeof(ColorJsonConverter))]
    public Color FaceGenSpectrumLowColor { get; set; } = Colors.Blue;
    [JsonConverter(typeof(ColorJsonConverter))]
    public Color FaceGenSpectrumMidColor { get; set; } = Colors.White;
    [JsonConverter(typeof(ColorJsonConverter))]
    public Color FaceGenSpectrumHighColor { get; set; } = Colors.Red;
    /// <summary>Persisted FaceGen stats keyed by "{ModKey}|{NpcFormKey}". Each
    /// entry carries the source NIF's SHA256 so a mod author bumping their
    /// FaceGen geometry auto-invalidates the cached numbers on next view.</summary>
    public Dictionary<string, CachedFaceGenStats> FaceGenAnalysisCache { get; set; } = new();
    
    // --- Window State Properties ---
    public double MainWindowTop { get; set; } = 100; // Default a reasonable position
    public double MainWindowLeft { get; set; } = 100;
    public double MainWindowHeight { get; set; } = 700; // Default to your design height
    public double MainWindowWidth { get; set; } = 1000; // Default to your design width
    public WindowState MainWindowState { get; set; } = WindowState.Normal;
    
    // --- Manual Update Logging
    public bool HasUpdatedTo2_0_7 { get; set; } = false;
    public bool HasUpdatedTo2_0_7_templates { get; set; } = false;
    // One-shot guard for the 2.2.2 base-game-asset scan migration, so dev builds still
    // versioned below 2.2.2 don't re-run the full-mod-list scan on every launch.
    public bool HasUpdatedTo2_2_2 { get; set; } = false;
    // Guard for the record-less FaceGen NPC rescan: when below
    // UpdateHandler.RecordlessFaceGenRescanTarget, every mod's analysis cache
    // (LastKnownState) is invalidated once so RefreshNpcLists re-discovers FaceGen files
    // shipped without plugin records inside plugin-backed mods. Versioned (not a bool) so
    // a revision to the detection logic can force another rescan by bumping the target.
    public int RecordlessFaceGenRescanVersion { get; set; } = 0;
    // Guard for the wig/antler detection rescan: when below
    // UpdateHandler.WigScanRescanTarget, every mod's analysis cache (LastKnownState)
    // is invalidated once so the next population pass runs ScanForWigs on mods whose
    // snapshot would otherwise hit and skip detection forever. Versioned (not a bool)
    // so a revision to the detection keywords/slot guard can force another rescan.
    public int WigScanRescanVersion { get; set; } = 0;
    
    // --- Troubleshooting / Logging ---
    public bool LogActivity { get; set; } = false;
    public bool LogStartup { get; set; } = false;
    // When on, a patch run writes RecordProvenance.csv (every non-NPC record merged into the
    // output plugin + the reference chain that pulled it in). Applied at runtime via
    // RecordProvenanceDiag.SetEnabled.
    public bool LogRecordProvenance { get; set; } = false;
    // When on, a patch run writes AssetProvenance.log (why each output asset was copied + which
    // NPCs/mods pulled it in). Applied at runtime via AssetProvenanceDiag.SetEnabled.
    public bool LogAssetProvenance { get; set; } = false;
    public bool FixGarbledText { get; set; } = true;

    // NPCs (by FormKey) for which the Validator and Patcher emit a full per-NPC
    // activity trace to "{exe}\NPC Logs\{display}.txt". Membership in this list is
    // the on/off switch; an empty list means no per-NPC logging. See NpcDiagnosticLogger.
    public List<FormKey> NpcsToLog { get; set; } = new();
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

public enum FaceGenAnalysisDisplayMode
{
    TextOverlay,  // numeric stats drawn on the tile itself
    Tooltip,      // small indicator dot, full stats on hover
}

public enum FaceGenTooltipPosition
{
    // CenterLeft / CenterRight are the default-ish picks because they sit on
    // the side-border space of typical mugshot portraits — less intrusive
    // than TopCenter (often on hair) or BottomCenter (sometimes on the chin).
    CenterLeft,
    CenterRight,
    BottomCenter,
    TopCenter,
}

public enum FaceGenHighlightCriterion
{
    TopPercent,    // mark the heaviest N% of visible tiles per metric
    StdDevAbove,   // mark tiles whose value exceeds mean + N*stddev
    Spectrum,      // continuous gradient: each tile mapped along Low→Mid→High by its position between min and max of visible tiles
}

/// <summary>Persisted FaceGen stats for a single (mod, NPC) pair. SHA is the
/// NIF's SHA256 at capture time — checked against the live NIF on the next
/// load to detect mod-author updates and force a recompute.</summary>
public sealed class CachedFaceGenStats
{
    public string Sha { get; set; } = string.Empty;
    public int Vertices { get; set; }
    public int Triangles { get; set; }
    public int Shapes { get; set; }
    public long FileSizeBytes { get; set; }
    public bool MeasuredGeometry { get; set; }
}

/// <summary>
/// Per-NPC override of the character-preview / mugshot attire toggles. Persisted
/// in <see cref="Settings.NpcRenderOverrides"/>. Only takes effect when
/// <see cref="OverrideGlobalAttire"/> is true; otherwise the global
/// <see cref="InternalMugshotSettings.IncludeDefaultOutfit"/> /
/// <see cref="InternalMugshotSettings.IncludeHeadgear"/> apply.
/// </summary>
public sealed class NpcRenderOverride
{
    public bool OverrideGlobalAttire { get; set; } = false;
    public bool IncludeDefaultOutfit { get; set; } = false;
    public bool IncludeHeadgear { get; set; } = false;
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

    // --- Decode-cache budget ---
    // Controls how the renderer's in-RAM decode caches (decoded DDS textures + parsed NIF geometry) size
    // themselves. Default reproduces the historical "grow into a share of free RAM" behaviour. FixedRam caps
    // the total at CacheFixedBudgetGB; Disabled turns caching off (renders decode fresh each time).
    public RenderCacheMode CacheMode { get; set; } = RenderCacheMode.PercentFreeRam;
    public double CacheFixedBudgetGB { get; set; } = 4.0;
    // Collective share of free RAM (0-100) the decode caches may use together when CacheMode is
    // PercentFreeRam. Default 85 reproduces the historical behaviour (each cache gets its calibrated
    // fraction); one knob scales all three caches together while holding their 75:9:1 ratio.
    public double CacheFreeRamPercent { get; set; } = 85.0;

    // When true, the next live-preview load in the Settings panel captures the
    // full asset-resolution + renderer trace into a per-render text file under
    // <ExeDir>\RenderLogs\<ModName>_<FormKey>.txt. Diagnostic-only — used when
    // a mugshot tile fails to render but the live preview shows the same NPC
    // fine, so the two traces can be diffed. Off by default; the toggle lives
    // next to "Reset Settings" in the Internal-renderer panel header.
    public bool LogRenderLogic { get; set; } = false;

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

    // Warning-icon visibility. Each gates one mugshot-tile warning icon AND
    // whether its backing state is stamped into the PNG metadata at render
    // time, so a mugshot generated while a toggle is off carries no trace of
    // that warning class. Deliberately NOT part of the settings hash — instead
    // MugshotStalenessChecker treats "stamp present while its toggle is off"
    // as stale, so unchecking re-renders exactly the mugshots that were
    // displaying that icon (once; the regen stamps clean and the check goes
    // quiet). Re-checking cannot resurrect icons for PNGs rendered while off —
    // the information was never recorded; those refresh only when something
    // else re-stales them.
    //
    // ShowMissingNpcAssetsIcon: the missing meshes/textures/FaceGen-mismatch
    // overlay. NOTE: while off, newly-rendered PNGs record no missing assets,
    // so the "Re-render When: Missing Assets" auto-update has no signal for
    // them. ShowMissingOutfitAssetsIcon: the stale-physics-config (broken
    // SMP/HDT xml link) icon.
    public bool ShowMissingNpcAssetsIcon { get; set; } = true;
    public bool ShowMissingOutfitAssetsIcon { get; set; } = true;

    // Character-preview attire toggles (mesh-override channel; CharacterViewer.Rendering
    // neutral MeshOverride pipeline). Both resolve Mutagen Armor/Outfit/NPC records
    // host-side (NpcMeshResolver.ResolveAttireMeshOverrides) and feed the renderer's
    // ApplyMeshOverrides; slot occupancy makes clothing hide the nude body and a
    // helmet hide hair automatically.
    //
    // IncludeDefaultOutfit: ON (default) renders the NPC's DefaultOutfit attire
    //   (Kind=Armor); body-covering armor hides the skin it covers. OFF is the
    //   plain skin preview.
    // IncludeHeadgear: ON renders worn/outfit head-slot armor (Kind=Headgear) with
    //   hair hidden, as in game. OFF (default) shows hair/face — the sensible
    //   default for a face-picking tool. Only applies with the outfit on (outfit
    //   is the dominant toggle; see GetEffectiveAttireFlags). The defaults depict
    //   an NPC clothed but bareheaded.
    public bool IncludeDefaultOutfit { get; set; } = true;
    public bool IncludeHeadgear { get; set; } = false;

    // Dev/harness overrides for the renderer's wig / antler handling modes (see
    // Settings.GetEffectiveRenderWigMode / GetEffectiveRenderAntlerMode): when set
    // and the tile's mod has detections of that class, the renderer depicts this
    // mode regardless of the patching-mode gate. Not exposed in the UI;
    // RenderHarness variants set them by property name via reflection for per-mode
    // A/B renders.
    public WigHandlingMode? WigModeOverride { get; set; } = null;
    public AntlerHandlingMode? AntlerModeOverride { get; set; } = null;

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

    // SSAO occluder-thickness rejection distance in view-space units
    // (2.5.20+). An occluder only darkens a fragment when its depth is
    // within roughly this distance; geometry farther behind (e.g. a shirt
    // collar a few units behind beard strands) is treated as a separate
    // surface seen through a gap, not a crevice wall. Also drives the
    // bilateral SSAO blur's depth threshold. Default matches the renderer's
    // previously hardcoded value.
    public float SsaoThickness { get; set; } = 1.5f;

    // Max view-space gap between a hair/beard fragment and the opaque surface
    // behind it for screen AO to still shade the hair (2.5.20+). Hair is
    // excluded from the SSAO depth prepass, so its AO texel belongs to the
    // surface behind the strands: within the gap (scalp, chin) that AO is a
    // fair stand-in; beyond it (a collar behind the beard) it is background
    // structure that would ghost through the beard and is faded out.
    public float SsaoHairGap { get; set; } = 0.8f;

    // Eye catch-light toggle (2.5.13+). When on, eye shapes get a tight,
    // bright specular highlight from the key light layered on top of
    // their normal Blinn-Phong specular. Real portrait photography
    // always has a visible catch-light in the iris - it's the single
    // biggest "alive vs. dead" cue for eyes.
    public bool EnableEyeCatchlight { get; set; } = true;

    // Subsurface scattering strength multiplier (2.5.14+). The renderer's
    // SSS math uses subsurfaceRolloff as the proper wrap parameter (per
    // Bethesda BSLighting spec) and adds a back-scatter / translucency
    // term for thin-area light transmission (ear edges, nostril rims,
    // backlit cheeks). This multiplier lets users dial the visible
    // strength up or down without needing to override per-NIF rolloff
    // values. 1.0 is "honest" SSS at the source values.
    //
    // Default is 1.0 — "honest" SSS at the source NIF rolloff values, pairing
    // with the game-faithful soft-lighting path (SkinFaithfulSoftLight) so the
    // warm terminator band reads at material strength instead of the near-
    // invisible contribution the old 0.1 default produced. Caveats: very high
    // values can desaturate high-chroma races (Orc olive, dark skin) — keep
    // at/near 1.0; and many skin sets ship a black/placeholder _sk subsurface
    // map, in which case SSS is zero regardless of this value (matches the
    // engine / NifSkope, which also honor the mask).
    public float SubsurfaceStrength { get; set; } = 1.0f;

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

    // Skin-only saturation multiplier applied post-tint, pre-lighting on
    // shapes flagged as skin (BSLSP_FACE / BSLSP_SKINTINT). 1.0 is no-op.
    // >1 boosts chroma, restoring race-distinguishing skin character that
    // the downstream pipeline (SSS + tonemap) tends to compress toward
    // neutral. Hair / eyes / brows are excluded from the boost.
    public float SkinSaturationBoost { get; set; } = 1.0f;

    // Tone-map exposure multiplier (2.5.19+). Scales the tone-mapper's
    // baseline exposure: 1.0 = neutral (bit-identical to the pre-2.5.19
    // hardcoded look), >1 brightens toward the blown-out in-game look,
    // <1 darkens. Folded under EnableToneMapping, so it only takes effect
    // when tone-mapping is on. Default 1.0 keeps existing tiles unchanged,
    // so no settings-schema migration is needed.
    public float Exposure { get; set; } = 1.0f;

    // Hair finishing relief (default ON). Hair pixels skip the fresnel contour
    // darkening and use a gentler exposure pull-down into the ACES curve, so
    // blonde hair is not crushed toward brown by the skin-tuned tone-map chain.
    // Only takes effect when tone-mapping is on. Pixel-affecting -> included in
    // the mugshot staleness hash (pipeline_schema v11).
    public bool TonemapHairRelief { get; set; } = true;

    // Daylight boost (default ON at a gentle 1.1 gain). Scales the directional
    // lights by DaylightBoostIntensity + slight warmth (ambient untouched),
    // lifting blonde hair toward its in-game daylight look without hand-tuning
    // the key light. Intensity is the gain: 1.0 = warmth only, higher brightens.
    public bool DaylightBoost { get; set; } = true;
    public float DaylightBoostIntensity { get; set; } = 1.1f;

    // Bloom (default ON). Bright-pass + blur glow composited over the scene so
    // hair highlights bleed into the soft halo the engine produces. Intensity
    // is the composite gain (0 = no glow). Only takes effect when tone-mapping
    // is on. Pixel-affecting -> included in the staleness hash (schema v11).
    public bool EnableBloom { get; set; } = true;
    public float BloomIntensity { get; set; } = 0.7f;

    // User-defined lighting presets persisted across sessions. The settings
    // adapter wraps these in ObservableCollections at runtime.
    public List<CharacterViewerLightingLayout> UserLightingLayouts { get; set; } = new();
    public List<CharacterViewerLightingColorScheme> UserLightingColorSchemes { get; set; } = new();
}