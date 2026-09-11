using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// A data class to store the appearance details for a processed NPC.
/// </summary>
public class NpcAppearanceData
{
    public string ModName { get; set; } = string.Empty;
    /// <summary>
    /// The selected appearance donor, including shared faces. Older tokens omit this;
    /// importing those tokens falls back to the target NPC's own face.
    /// </summary>
    public FormKey? SourceNpcFormKey { get; set; }
    public ModKey AppearancePlugin { get; set; }
    public ModKey OutputPlugin { get; set; }
}