using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Collections.Generic;
using System.Windows; // Required for HashSet

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
    public bool NormalizeImageDimensions { get; set; } = false;
    public int MaxMugshotsToFit { get; set; } = 50;

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

    // Last Selected NPC ***
    public FormKey LastSelectedNpcFormKey { get; set; } // Will be FormKey.Null if none or invalid
    
    // --- Window State Properties ---
    public double MainWindowTop { get; set; } = 100; // Default a reasonable position
    public double MainWindowLeft { get; set; } = 100;
    public double MainWindowHeight { get; set; } = 700; // Default to your design height
    public double MainWindowWidth { get; set; } = 1000; // Default to your design width
    public WindowState MainWindowState { get; set; } = WindowState.Normal;
}