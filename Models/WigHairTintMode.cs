namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// What <see cref="WigHandlingMode.ConvertToHeadParts"/> does to a converted
/// wig's baked <c>BSLightingShaderProperty.hairTintColor</c>.
///
/// Hair-slot wig armors are commonly authored as BSLSP_HAIRTINT with a dark
/// PLACEHOLDER tint (High Poly NPC Overhaul's KS Hairdos wigs bake
/// (0.133, 0.133, 0.133)) because the tint is expected to be replaced at
/// runtime: the vanilla engine never tints worn armor, but RaceMenu's skee64
/// does — <c>bEnableTintHairSlot</c>, "automatically tinting worn items in the
/// hair slot where they have the Hair Tint Shader". That is why those mods look
/// black-haired without RaceMenu and correct with it.
///
/// Converting the wig to HeadParts bakes it into the FaceGen NIF, so it is no
/// longer a worn hair-slot item and skee64 stops tinting it — the placeholder
/// becomes permanent and RaceMenu can no longer help. Baking the NPC's hair
/// color in at conversion time is what the CK itself does when it exports
/// FaceGen for a real hair head part.
/// </summary>
public enum WigHairTintMode
{
    /// <summary>
    /// Apply the NPC's hair color only when the wig's baked tint is NOT neutral
    /// white. A white tint is a no-op multiply, the signature of a wig whose
    /// texture is already pre-colored by its author (FoxGlove Auri's wig bakes
    /// (1,1,1) over a red texture) — re-tinting those would change a look that
    /// is already correct. The default.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Apply the NPC's hair color to every converted wig shape carrying the
    /// HairTint shader, regardless of its baked tint. Matches what skee64's
    /// <c>bEnableTintHairSlot</c> does to ALL hair-slot wigs in game, so it is
    /// the most faithful reproduction of a RaceMenu-equipped load order.
    /// </summary>
    Always = 1,

    /// <summary>
    /// Never touch the baked tint — the wig keeps whatever its author shipped.
    /// </summary>
    Never = 2
}
