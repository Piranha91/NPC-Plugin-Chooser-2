// FavoriteFaceSearchType.cs
using System.ComponentModel; // Required for Description attribute

namespace NPC_Plugin_Chooser_2.View_Models
{
    /// <summary>
    /// The set of fields the Favorite Faces menu can filter on. Deliberately a
    /// curated subset of the concepts in <see cref="NpcSearchType"/>: only the
    /// fields that are meaningful for a saved face (its source NPC + the mod that
    /// supplies it), plus the favorites-only <see cref="Group"/>. NPC-menu-only
    /// notions (Selection State, Shared/Guest, In Appearance Mod, Chosen In Mod,
    /// From Plugin, Template) don't apply to a favorite and are intentionally
    /// omitted.
    /// </summary>
    public enum FavoriteFaceSearchType
    {
        [Description("Name")]
        Name,

        [Description("EditorID")]
        EditorID,

        [Description("FormKey")]
        FormKey,

        [Description("Mod")]
        Mod,

        [Description("Race")]
        Race,

        [Description("Gender")]
        Gender,

        [Description("Uniqueness")]
        Uniqueness,

        [Description("Group")]
        Group
    }
}
