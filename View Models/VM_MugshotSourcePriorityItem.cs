using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

public sealed class VM_MugshotSourcePriorityItem : ReactiveObject
{
    public MugshotSourceType Source { get; }
    public string DisplayName { get; }

    [Reactive] public bool IsEnabled { get; set; } = true;
    [Reactive] public string DisabledReason { get; set; } = string.Empty;

    public VM_MugshotSourcePriorityItem(MugshotSourceType source)
    {
        Source = source;
        DisplayName = GetDisplayName(source);
    }

    public static string GetDisplayName(MugshotSourceType source) => source switch
    {
        MugshotSourceType.DownloadedMugshots => "Downloaded Mugshots",
        MugshotSourceType.FaceFinder         => "FaceFinder",
        MugshotSourceType.AutoGeneration     => "Auto-Generation",
        _                                    => source.ToString(),
    };
}
