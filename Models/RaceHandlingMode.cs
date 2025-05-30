// Models/RaceHandlingMode.cs
namespace NPC_Plugin_Chooser_2.Models
{
    public enum RaceHandlingMode
    {
        /// <summary>
        /// For vanilla races modified by appearance mods:
        /// - Default Patching: Overrides the race in the patch with the appearance mod's version. Generates a YAML diff.
        /// - EasyNPC-Like Patching: If the race differs, properties from the appearance mod's race are forwarded to the NPC's original race in the patch.
        /// </summary>
        ForwardWinningOverrides,
        /*
        /// <summary>
        /// Clones the modified race record from the appearance mod as a new race (EditorID + "_NPC") and assigns it to the NPC.
        /// </summary>
        DuplicateAndRemapRace,*/
        /// <summary>
        /// Does not make any specific changes to race records based on appearance mod edits. User is responsible for compatibility.
        /// </summary>
        IgnoreRace
    }
}