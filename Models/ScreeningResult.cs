// Add this record definition somewhere accessible, e.g., near VM_Run or in a Models/Helpers folder

using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.Models // Or appropriate namespace
{
    // Located in a file like /Models/ScreeningResult.cs
    public class ScreeningResult
    {
        // --- Keep These ---
        public bool SelectionIsValid { get; }
        public INpcGetter WinningNpcOverride { get; }
        public ModSetting AppearanceModSetting { get; }
        public FormKey AppearanceNpcFormKey { get; }

        // --- Remove These ---
        // public bool HasFaceGen { get; }
        // public INpcGetter? AppearanceModRecord { get; }
        // public ModKey? AppearanceModKey { get; }

        // Update the constructor to match
        public ScreeningResult(bool isValid, INpcGetter winningNpcOverride, ModSetting appearanceModSetting, FormKey appearanceNpcFormKey)
        {
            this.SelectionIsValid = isValid;
            this.WinningNpcOverride = winningNpcOverride;
            this.AppearanceModSetting = appearanceModSetting;
            this.AppearanceNpcFormKey = appearanceNpcFormKey;
        }
    }
}