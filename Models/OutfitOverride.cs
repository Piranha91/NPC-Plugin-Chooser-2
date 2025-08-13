using System.ComponentModel;

namespace NPC_Plugin_Chooser_2.Models;

public enum OutfitOverride
{
    [Description("Use the setting from the mod")]
    UseModSetting,

    [Description("Always include the outfit")]
    Yes,

    [Description("Never include the outfit")]
    No
}