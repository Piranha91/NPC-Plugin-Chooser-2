namespace NPC_Plugin_Chooser_2.View_Models;

public enum NpcSearchType
{
    Name,
    EditorID,
    InAppearanceMod, // Checks plugins AND mugshot folders
    FromMod,         // Checks NpcFormKey.ModKey
    FormKey          // Checks NpcFormKey.ToString()
}