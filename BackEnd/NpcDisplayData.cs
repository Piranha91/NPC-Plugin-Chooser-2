using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// A lightweight record to hold only the necessary NPC data for UI display,
/// avoiding the high memory cost of caching the full INpcGetter.
/// </summary>
public record NpcDisplayData
{
    public required FormKey FormKey { get; init; }
    public ITranslatedStringGetter? Name { get; init; }
    public string? EditorID { get; init; }
    public bool IsTemplateUser { get; init; }
    public FormKey TemplateFormKey { get; init; }
    public bool IsInLoadOrder { get; init; }
    public bool IsUnique { get; init; }

    /// <summary>
    /// Creates an NpcDisplayData instance from a full INpcGetter record.
    /// </summary>
    public static NpcDisplayData FromGetter(INpcGetter npcGetter)
    {
        return new NpcDisplayData
        {
            FormKey = npcGetter.FormKey,
            Name = npcGetter.Name,
            EditorID = npcGetter.EditorID,
            IsTemplateUser = Auxilliary.IsValidTemplatedNpc(npcGetter),
            TemplateFormKey = npcGetter.Template.FormKey,
            IsInLoadOrder = true,
            IsUnique = npcGetter.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique),
        };
    }

    /// <summary>
    /// Creates an NpcDisplayData instance from a FormKey if the getter isn't available
    /// </summary>
    public static NpcDisplayData FromFormKey(FormKey formKey)
    {
        return new NpcDisplayData
        {
            FormKey = formKey,
            IsInLoadOrder = false
        };
    }
}