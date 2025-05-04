// Add this record definition somewhere accessible, e.g., near VM_Run or in a Models/Helpers folder

using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.Models // Or appropriate namespace
{
    public record ScreeningResult(
        bool SelectionIsValid,        // Overall flag: Is there at least *some* valid data?
        bool HasPluginRecordOverride, // Does the selected plugin contain an override for this NPC?
        bool HasFaceGenAssets,        // Do FaceGen assets exist in the specified paths?
        INpcGetter? SourceNpcRecord,  // The resolved NPC record from the appearance plugin (if HasPluginRecordOverride is true)
        INpcGetter? WinningNpcOverride, // The winning override from the load order (always needed for context/EasyNPC mode)
        ModSetting? AppearanceModSetting, // The looked-up ModSetting
        ModKey? AppearancePluginKey    // The ModKey from the ModSetting (if it exists)
    );
}