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
        
        [Description("Uniqueness")]
        Uniqueness,

        [Description("Race")]
        Race,

        [Description("Gender")]
        Gender,

        [Description("Group")]
        Group,
        
        [Description("Template")]
        Template
    }
    
    /// <summary>
    /// Whether a filter row keeps the items its criterion matches (<see cref="Is"/>) or keeps
    /// everything the criterion does *not* match (<see cref="IsNot"/>). A row that contributes no
    /// criterion at all (e.g. an empty text box, or Group left on "All ...") stays
    /// inactive either way — inverting "no filter" is still "no filter".
    /// <para>
    /// The UI surfaces this as a "Not" checkbox in front of the field dropdown rather than an
    /// Is/Is Not dropdown after it (see <c>FilterInversionToBooleanConverter</c>): a leading
    /// "Not" negates any field label, whereas a copula only reads correctly for the fields
    /// that name an attribute ("Name Is …") and not for those that name a relation
    /// ("In Appearance Mod Is …").
    /// </para>
    /// </summary>
    public enum FilterInversionType
    {
        [Description("Is")]
        Is,

        [Description("Is Not")]
        IsNot
    }

    public enum UniquenessFilterType
    {
        Any,
        Unique,
        Generic
    }

    public enum GenderFilterType
    {
        Any,
        Male,
        Female
    }
}