// Add this record definition somewhere accessible, e.g., near VM_Run or in a Models/Helpers folder

using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.Models // Or appropriate namespace
{
    public record ScreeningResult(
        bool SelectionIsValid,        // Overall flag: Is there at least *some* valid data?
        bool HasFaceGenAssets,        // Do FaceGen assets exist in the specified paths?
        INpcGetter? AppearanceModRecord, // The resolved NPC record from the appearance plugin
        ModKey? AppearanceModKey,        // The ModKey of the context from which AppearanceModRecord is found (for logging)
        INpcGetter? WinningNpcOverride, // The winning override from the load order (always needed for context/EasyNPC mode)
        ModSetting? AppearanceModSetting // The looked-up ModSetting
    );
}