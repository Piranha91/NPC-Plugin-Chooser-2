using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// A lightweight record to hold only the necessary NPC data for UI display,
/// avoiding the high memory cost of caching the full INpcGetter.
/// </summary>
public record NpcDisplayData
{
    public required FormKey FormKey { get; init; }
    public string? Name { get; init; }
    public string? EditorID { get; init; }
    public bool IsTemplateUser { get; init; }
    public FormKey TemplateFormKey { get; init; }

    /// <summary>
    /// A helper property to consistently determine the best display name.
    /// </summary>
    public string DisplayName => Name ?? EditorID ?? FormKey.ToString();

    /// <summary>
    /// Creates an NpcDisplayData instance from a full INpcGetter record.
    /// </summary>
    public static NpcDisplayData FromGetter(INpcGetter npcGetter)
    {
        return new NpcDisplayData
        {
            FormKey = npcGetter.FormKey,
            Name = npcGetter.Name?.String,
            EditorID = npcGetter.EditorID,
            IsTemplateUser = npcGetter.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits),
            TemplateFormKey = npcGetter.Template.FormKey
        };
    }
}