using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Models;

public class ModSetting
{
    public string DisplayName { get; set; } = string.Empty;
    public List<ModKey> ModKeys { get; set; } = new();
    public List<string> CorrespondingFolderPaths { get; set; } = new(); // first should be the base mod, and others are overrides where the program should find conflict winning data if it exists
    public string MugShotFolderPath { get; set; } = string.Empty; // Path to the mugshot folder FOR THIS MOD SETTING
    public Dictionary<FormKey, ModKey> NpcPluginDisambiguation { get; set; } = new(); // Maps NPC FormKey to the ModKey from which it should inherit data, specifically for NPCs appearing in multiple plugins within this ModSetting.
}