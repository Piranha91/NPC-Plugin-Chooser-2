using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Mutagen.Bethesda.Strings;
using Newtonsoft.Json;
using NPC_Plugin_Chooser_2.BackEnd; // Required for HashSet

namespace NPC_Plugin_Chooser_2.Models;

public class Settings
{
    public string ProgramVersion { get; set; } = string.Empty;
    public bool HasBeenLaunched { get; set; } = false;
    // Mod Environment
    public string ModsFolder { get; set; } = string.Empty;
    public string MugshotsFolder { get; set; } = string.Empty;
    public Dictionary<string, string> CachedNonAppearanceMods { get; set; } = new(); // These have been examined and determined to not have NPC mods. Used to speed up startup

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

    // UI / Other
    public bool ShowNpcDescriptions { get; set; } = true;
    public bool ShowSingleOptionNpcs { get; set; } = true;
    public bool ShowUnloadedNpcs { get; set; } = true;
    public List<ModSetting> ModSettings { get; set; } = new();
    // The string is the ModName, the FormKey is the NPC within that mod providing the appearance.
    public Dictionary<FormKey, (string ModName, FormKey NpcFormKey)> SelectedAppearanceMods { get; set; } = new();
    // Key: FormKey of NPC receiving the appearance.
    // Value: A set of tuples, where each tuple represents a "guest" mugshot.
    // Tuple: (string ModName of the guest appearance, FormKey of the guest NPC, string DisplayName of the guest NPC).
    public Dictionary<FormKey, HashSet<(string ModName, FormKey NpcFormKey, string NpcDisplayName)>> GuestAppearances { get; set; } = new();
    public HashSet<string> HiddenModNames { get; set; } = new();
    public Dictionary<FormKey, HashSet<string>> HiddenModsPerNpc { get; set; } = new();
    public Dictionary<FormKey, HashSet<string>> NpcGroupAssignments { get; set; } = new();
    public Dictionary<FormKey, OutfitOverride> NpcOutfitOverrides { get; set; } = new();
    public HashSet<ModKey> ImportFromLoadOrderExclusions { get; set; } = new();
    public HashSet<(FormKey NpcFormKey, string ModName)> FavoriteFaces { get; set; } = new();
    public bool NormalizeImageDimensions { get; set; } = false;
    public int MaxMugshotsToFit { get; set; } = 50;
    public int MaxNpcsPerPageSummaryView { get; set; } = 100;
    public bool SuppressPopupWarnings { get; set; } = false;
    public Language? LocalizationLanguage { get; set; } = null;
    public bool IsDarkMode { get; set; } = false;
    
    // --- NPC Display ---
    public bool ShowNpcNameInList { get; set; } = true;
    public bool ShowNpcEditorIdInList { get; set; }
    public bool ShowNpcFormKeyInList { get; set; }
    public bool ShowNpcFormIdInList { get; set; }
    public string NpcListSeparator { get; set; } = " | ";

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
    public string FaceFinderApiKey { get; set; } = string.Empty; // User must provide this
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(true)]
    public bool CacheFaceFinderImages { get; set; } = true;
    public Dictionary<string, List<string>> FaceFinderModNameMappings { get; set; } = new();

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(false)]
    public bool UsePortraitCreatorFallback { get; set; } = false;
    
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
    public PortraitCameraMode SelectedCameraMode { get; set; } = PortraitCameraMode.Relative;
    // These should match the defaults or CLI options in your C++ app
    public string DefaultLightingJsonString { get; set; } = @"
{
	""lights"": [
		{
			""type"": ""ambient"",
			""color"": [ 0.3, 0.25, 0.2 ],
			""intensity"": 1.0
		},
		{
			""type"": ""directional"",
			""direction"": [ 0.5, 0.4, 1.0 ],
			""color"": [ 1.0, 0.85, 0.7 ],
			""intensity"": 1.1
		},
		{
			""type"": ""directional"",
			""direction"": [ -0.6, 0.2, 0.5 ],
			""color"": [ 0.8, 0.7, 0.6 ],
			""intensity"": 0.25
		}
	]
}";
    [JsonConverter(typeof(ColorJsonConverter))] // Apply the converter
    public Color MugshotBackgroundColor { get; set; } = Color.FromRgb(227, 239, 251);

    
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(0.20f)]
    public float HeadTopOffset { get; set; } = 0.20f;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(-0.05f)]
    public float HeadBottomOffset { get; set; } = -0.05f;
    
    // Fixed camera position properties
    public float CamX { get; set; } = 0.0f;
    public float CamY { get; set; } = 0.0f;
    public float CamZ { get; set; } = 0.0f;
    public float CamPitch { get; set; } = 0.0f;
    public float CamYaw { get; set; } = 0.0f;
    
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(750)]
    public int ImageXRes { get; set; } = 750;
    
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(750)]
    public int ImageYRes { get; set; } = 750;
    
    // --- Window State Properties ---
    public double MainWindowTop { get; set; } = 100; // Default a reasonable position
    public double MainWindowLeft { get; set; } = 100;
    public double MainWindowHeight { get; set; } = 700; // Default to your design height
    public double MainWindowWidth { get; set; } = 1000; // Default to your design width
    public WindowState MainWindowState { get; set; } = WindowState.Normal;
}