using System.Diagnostics;
using Mutagen.Bethesda.Plugins;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models
{
    [DebuggerDisplay("{DisplayText}")]
    public class VM_SelectableMod : ReactiveObject
    {
        public ModKey ModKey { get; }

        [Reactive] public bool IsSelected { get; set; }
        [Reactive] public bool IsMissingFromEnvironment { get; set; } // New Property

        public string DisplayText => ModKey.ToString();

        // Updated constructor to include the missing flag
        public VM_SelectableMod(ModKey modKey, bool isSelected = false, bool isMissing = false)
        {
            ModKey = modKey;
            IsSelected = isSelected;
            IsMissingFromEnvironment = isMissing; // Initialize the new property
        }
    }
}