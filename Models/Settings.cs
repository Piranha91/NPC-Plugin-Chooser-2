using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Collections.Generic;
using System.Windows; // Required for HashSet

namespace NPC_Plugin_Chooser_2.Models;

public class Settings
{
    public string ProgramVersion { get; set; } = string.Empty;
    // Mod Environment
    public string ModsFolder { get; set; } = string.Empty;
    public string MugshotsFolder { get; set; } = string.Empty;

    // Game Environment
    public SkyrimRelease SkyrimRelease { get; set; } = SkyrimRelease.SkyrimSE;
    public string SkyrimGamePath { get; set; } = string.Empty;

    // Output Settings
    public string OutputDirectory { get; set; } = "NPC Output"; 
    public bool AppendTimestampToOutputDirectory { get; set; } = false; 
    public string OutputPluginName { get; set; } = "NPC"; 
    public PatchingMode PatchingMode { get; set; } = PatchingMode.Default; 
    public RaceHandlingMode RaceHandlingMode { get; set; } = RaceHandlingMode.ForwardWinningOverrides;

    // UI / Other
    public bool ShowNpcDescriptions { get; set; } = true;
    public List<ModSetting> ModSettings { get; set; } = new();
    public Dictionary<FormKey, string> SelectedAppearanceMods { get; set; } = new();
    public HashSet<string> HiddenModNames { get; set; } = new();
    public Dictionary<FormKey, HashSet<string>> HiddenModsPerNpc { get; set; } = new();
    public Dictionary<FormKey, HashSet<string>> NpcGroupAssignments { get; set; } = new();

    // EasyNPC Interchangeability / Settings
    public Dictionary<FormKey, ModKey> EasyNpcDefaultPlugins { get; set; } = new(); 
    public HashSet<ModKey> EasyNpcDefaultPluginExclusions { get; set; } = new() { ModKey.FromFileName("Synthesis.esp")};
    public bool AddMissingNpcsOnUpdate { get; set; } = false;

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