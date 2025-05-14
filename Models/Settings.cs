using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Collections.Generic; // Required for HashSet

namespace NPC_Plugin_Chooser_2.Models;

public class Settings
{
    // Mod Environment
    public string ModsFolder { get; set; } = string.Empty;
    public string MugshotsFolder { get; set; } = string.Empty;

    // Game Environment
    public SkyrimRelease SkyrimRelease { get; set; } = SkyrimRelease.SkyrimSE;
    public string SkyrimGamePath { get; set; } = string.Empty;

    // Output Settings
    public string OutputDirectory { get; set; } = "NPC Output"; // New
    public bool AppendTimestampToOutputDirectory { get; set; } = false; // New
    public string OutputPluginName { get; set; } = "NPC"; // Actual Filename - New
    public PatchingMode PatchingMode { get; set; } = PatchingMode.Default; // New

    // UI / Other
    public bool ShowNpcDescriptions { get; set; } = true;
    public List<ModSetting> ModSettings { get; set; } = new();
    public Dictionary<FormKey, string> SelectedAppearanceMods { get; set; } = new();
    public HashSet<string> HiddenModNames { get; set; } = new();
    public Dictionary<FormKey, HashSet<string>> HiddenModsPerNpc { get; set; } = new();

    // *** NEW: NPC Group Assignments ***
    // Stores which groups each NPC belongs to. Key is NPC FormKey, Value is a set of group names.
    public Dictionary<FormKey, HashSet<string>> NpcGroupAssignments { get; set; } = new();

    // EasyNPC Interchangeability / Settings
    public Dictionary<FormKey, ModKey> EasyNpcDefaultPlugins { get; set; } = new(); // not used by this program, but kept as a holding variable to facilitate interchangeability with EasyNPC
    public HashSet<ModKey> EasyNpcDefaultPluginExclusions { get; set; } = new() { ModKey.FromFileName("Synthesis.esp")};

    // *** NEW: Zoom Control Settings ***
    public double NpcsViewZoomLevel { get; set; } = 100.0; // Percentage, e.g., 100.0 for 100%
    public bool NpcsViewIsZoomLocked { get; set; } = false;
    public double ModsViewZoomLevel { get; set; } = 100.0; // Percentage, e.g., 100.0 for 100%
    public bool ModsViewIsZoomLocked { get; set; } = false;
}