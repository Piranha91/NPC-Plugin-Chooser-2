// NpcSearchType.cs
using System.ComponentModel; // Required for Description attribute

namespace NPC_Plugin_Chooser_2.View_Models
{
    public enum NpcSearchType
    {
        [Description("Name")] // Add Description attributes
        Name,

        [Description("EditorID")]
        EditorID,

        [Description("In Appearance Mod")]
        InAppearanceMod, // Checks plugins AND mugshot folders

        [Description("From Mod")]
        FromMod,         // Checks NpcFormKey.ModKey

        [Description("FormKey")]
        FormKey,          // Checks NpcFormKey.ToString()

        [Description("Selection State")] // New value
        SelectionState
    }
}