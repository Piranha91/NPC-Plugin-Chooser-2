using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Models;


/// <summary>
/// A data class to structure the contents of the NPC_Token.json file.
/// </summary>
public class NpcToken
{
    public string CreationDate { get; set; } = string.Empty;
    public Dictionary<FormKey, NpcAppearanceData> ProcessedNpcs { get; set; } = new();
}