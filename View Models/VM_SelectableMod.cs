using System.Diagnostics;
using Mutagen.Bethesda.Plugins;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

[DebuggerDisplay("{DisplayText}")]
public class VM_SelectableMod : ReactiveObject
{
    public ModKey ModKey { get; }

    [Reactive] public bool IsSelected { get; set; }
    [Reactive] public bool IsMissingFromEnvironment { get; set; } // New Property

    // Per-plugin merge-in state, used by the resource-plugin selector. Whether records from
    // this plugin may be copied into the output patch; only meaningful (and only editable)
    // for plugins marked resource-only — see VM_ResourcePluginSelector and MergeEligibility.
    [Reactive] public bool IsMergedIn { get; set; }
    [Reactive] public bool IsMergeEditable { get; set; }
    [Reactive] public string MergeInToolTip { get; set; } = string.Empty;

    public string DisplayText => ModKey.ToString();

    // Updated constructor to include the missing flag
    public VM_SelectableMod(ModKey modKey, bool isSelected = false, bool isMissing = false)
    {
        ModKey = modKey;
        IsSelected = isSelected;
        IsMissingFromEnvironment = isMissing; // Initialize the new property
    }
}
