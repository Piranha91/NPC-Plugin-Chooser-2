// NpcSearchType.cs
using System.ComponentModel; // Required for Description attribute

namespace NPC_Plugin_Chooser_2.View_Models
{
    public enum NpcSearchType
    {
        [Description("Name")]
        Name,

        [Description("EditorID")]
        EditorID,

        [Description("In Appearance Mod")]
        InAppearanceMod,

        [Description("From Mod")]
        FromMod,

        [Description("FormKey")]
        FormKey,

        [Description("Selection State")]
        SelectionState,

        // *** NEW: Group Filter ***
        [Description("Group")]
        Group
    }
}