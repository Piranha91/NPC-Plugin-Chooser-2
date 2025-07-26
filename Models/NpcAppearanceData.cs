using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// A data class to store the appearance details for a processed NPC.
/// </summary>
public class NpcAppearanceData
{
    public string ModName { get; set; } = string.Empty;
    public ModKey AppearancePlugin { get; set; }
}