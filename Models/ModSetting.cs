using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Models;

public class ModSetting
{
    public ModKey ModKey { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public List<string> CorrespondingFolderPaths { get; set; } = new(); // first should be the base mod, and others are overrides where the program should find conflict winning data if it exists
    public List<string> CorrespondingMugshotDirectories { get; set; } = new();
}