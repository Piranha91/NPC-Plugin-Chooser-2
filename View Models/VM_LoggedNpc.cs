using Mutagen.Bethesda.Plugins;
using ReactiveUI;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Lightweight row VM for the Settings &gt; Logging panel — both the editable
/// "NPCs to log" list and the search-match dropdown. Mirrors the display format
/// of the main NPC menu (<see cref="VM_NpcsMenuSelection"/>): the
/// <see cref="DisplayName"/> shown is the same string the user sees there, and
/// the name / EditorID / FormKey fields back the search filter. Add/Remove are
/// driven by commands on <see cref="VM_Settings"/> (the row carries no behavior).
/// </summary>
public class VM_LoggedNpc : ReactiveObject
{
    public FormKey NpcFormKey { get; }
    public string DisplayName { get; }
    public string NpcName { get; }
    public string NpcEditorId { get; }
    public string NpcFormKeyString { get; }

    public VM_LoggedNpc(FormKey npcFormKey, string displayName, string npcName, string npcEditorId)
    {
        NpcFormKey = npcFormKey;
        NpcFormKeyString = npcFormKey.ToString();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? NpcFormKeyString : displayName;
        NpcName = npcName ?? string.Empty;
        NpcEditorId = npcEditorId ?? string.Empty;
    }
}
