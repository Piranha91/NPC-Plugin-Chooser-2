namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// Whether <see cref="WigHandlingMode.ConvertToHeadParts"/> writes the NPC's hair colour into a
/// converted wig's baked <c>BSLightingShaderProperty.hairTintColor</c>. Three modes: Auto, On
/// (<see cref="Always"/>) and Off (<see cref="Never"/>). The member names and numbers are
/// persisted in Settings.json and must not change.
///
/// <para>The vanilla engine never tints worn armor. RaceMenu does: its SKSE plugin recolours a
/// worn item with the actor's hair colour when the item's armor and armor addon both occupy a
/// slot of its <c>uTintHairSlot</c> mask and its <c>bEnableTintHairSlot</c> feature is on. It
/// does not look at the colour the mesh already carries. Wig authors rely on that and commonly
/// ship a dark placeholder tint (High Poly NPC Overhaul's KS Hairdos wigs bake
/// (0.133, 0.133, 0.133)), which is why those mods look black-haired without RaceMenu.</para>
///
/// <para>Converting the wig to head parts bakes it into the FaceGen NIF. It is then no longer a
/// worn item, so RaceMenu cannot reach it and whatever tint the bake leaves is permanent. Writing
/// the NPC's hair colour in at conversion time is what the Creation Kit does when it exports
/// FaceGen for a real hair head part.</para>
///
/// <para>Precedence: the NPC's own override (<see cref="Settings.NpcWigHairTintModes"/>), then
/// the mod's (<see cref="ModSetting.ModWigHairTintMode"/>), then the global default
/// (<see cref="Settings.DefaultWigHairTintMode"/>). Only shapes that use the HairTint shader are
/// ever touched, in every mode.</para>
/// </summary>
public enum WigHairTintMode
{
    /// <summary>
    /// Reproduce what the game shows without this app. The converted wig gets the NPC's hair
    /// colour when RaceMenu would have tinted the worn wig: RaceMenu is installed, its hair-slot
    /// tint setting is on in its ini, and the armor (outfit item or skin) and armor addon the wig
    /// came from pass RaceMenu's slot test. Otherwise the wig keeps the tint its author shipped,
    /// which is also what the game showed. The default.
    /// <para>The decision is <c>WigHairTintDecision.ShouldTint</c>; what RaceMenu does in this
    /// installation is read by <c>RaceMenuDetector</c>.</para>
    /// </summary>
    Auto = 0,

    /// <summary>
    /// On. Write the NPC's hair colour into every converted wig shape that uses the HairTint
    /// shader, whether or not RaceMenu is installed and whatever slots the wig occupied.
    /// </summary>
    Always = 1,

    /// <summary>
    /// Off. Never touch the baked tint: the wig keeps whatever its author shipped.
    /// </summary>
    Never = 2
}
