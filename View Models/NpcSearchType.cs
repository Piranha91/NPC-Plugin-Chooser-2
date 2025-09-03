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
        
        [Description("Chosen In Mod")]
        ChosenInMod,

        [Description("From Plugin")]
        FromPlugin,

        [Description("FormKey")]
        FormKey,

        [Description("Selection State")]
        SelectionState,
        
        [Description("Shared/Guest Appearance")]
        ShareStatus,
        
        [Description("Group")]
        Group
    }
}