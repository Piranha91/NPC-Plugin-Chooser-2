namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// How the patcher (and the renderer, which previews the post-patch result)
/// treats detected wig/antler armors in a mod's outfits. Wigs are hair-slot
/// ARMOs shipped in Default Outfits (the "bald FaceGen + wig" pattern);
/// antlers are keyword-detected ARMOs treated identically. Active when
/// patching in Create-and-Patch mode or when SkyPatcher output is enabled;
/// inert in plain Create mode.
/// </summary>
public enum WigHandlingMode
{
    /// <summary>
    /// Transfer the wig's hair ArmorAddon(s) (all ArmorAddons for antlers)
    /// into a duplicate of the NPC's WornArmor (WNAM), so the wig becomes
    /// part of the skin and shows regardless of what outfit the NPC wears.
    /// Falls back to <see cref="ForwardToOutfit"/> when the appearance
    /// plugin assigns no WNAM.
    /// </summary>
    ForwardToSkin,

    /// <summary>
    /// Detect the outfit the NPC will actually wear in game (winning
    /// override or SkyPatcher/SPID distribution), duplicate it, and add the
    /// wig to the duplicate. No-op when that outfit already contains the wig.
    /// </summary>
    ForwardToOutfit,

    /// <summary>
    /// No special handling — the wig is forwarded only if Include Outfit is
    /// selected, like any other outfit element.
    /// </summary>
    None,

    /// <summary>
    /// Discard the wig armor system entirely and deliver the wig as HeadParts:
    /// mint one Hair-type parent HDPT (+ one IsExtraPart HDPT per additional
    /// wig render shape), replace the donor's Hair head parts with the parent,
    /// and bake the wig scene into the copied FaceGen NIF (see
    /// <c>BackEnd.HeadPartWigConverter</c> / <c>NifHandler.BakeWigIntoFaceGen</c>).
    /// The wig ARMO is stripped from any forwarded outfit. Falls back to
    /// <see cref="ForwardToSkin"/> per NPC when the bake would be risky (no
    /// donor hair to harvest dismember partitions from, unresolvable wig NIF).
    /// Appended after <see cref="None"/> — persisted integer values must not shift.
    /// </summary>
    ConvertToHeadParts
}
