using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.Models;

public class Settings
{
    public string ModsFolder { get; set; } = string.Empty;
    public string MugshotsFolder { get; set; } = string.Empty;
    public SkyrimRelease SkyrimRelease { get; set; } = SkyrimRelease.SkyrimSE;
    public string SkyrimGamePath { get; set; } = string.Empty;
    public string OutputModName { get; set; } = "NPC.esp";
    public List<ModSetting> ModSettings { get; set; } = new();
    public Dictionary<FormKey, string> SelectedAppearanceMods { get; set; } = new();
    public HashSet<string> HiddenModNames = new();
    public Dictionary<FormKey, HashSet<string>> HiddenModsPerNpc = new();
}