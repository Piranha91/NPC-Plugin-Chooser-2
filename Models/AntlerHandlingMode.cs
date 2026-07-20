namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// How the patcher (and the renderer, which previews the post-patch result)
/// treats detected antler armors/head parts in an appearance mod. Split out
/// from <see cref="WigHandlingMode"/> so antlers can be controlled
/// independently of hair-slot wigs — notably to keep antlers OFF an NPC
/// entirely (<see cref="Remove"/>) while still forwarding a wig to the skin.
///
/// <para>Antlers are detected from three sources: an antler ARMO in the NPC's
/// Default Outfit, antler ArmorAddon(s) baked into the NPC's WornArmor, and an
/// antler head part baked into the FaceGen (keyword-detected — non-intelligible
/// head-part names still need manual designation, a separate feature). Active
/// when patching in Create-and-Patch mode or when SkyPatcher output is enabled;
/// inert in plain Create mode.</para>
/// </summary>
public enum AntlerHandlingMode
{
    /// <summary>
    /// Transfer the antler's ArmorAddon(s) into a duplicate of the NPC's
    /// WornArmor (WNAM) so the antler becomes part of the skin and shows
    /// regardless of the outfit worn. Falls back to <see cref="ForwardToOutfit"/>
    /// when the appearance plugin assigns no WNAM. This is the legacy default
    /// (the pre-split unified mode treated antlers this way), so existing
    /// installs keep their behavior. No-op for antlers already carried by the
    /// WornArmor or baked into the FaceGen — they already show.
    /// </summary>
    ForwardToSkin,

    /// <summary>
    /// Detect the outfit the NPC will actually wear in game (winning override
    /// or SkyPatcher/SPID distribution), duplicate it, and add the antler to the
    /// duplicate. No-op when that outfit already contains the antler, or when the
    /// antler is carried by the WornArmor / baked into the FaceGen (moving those
    /// into an outfit is not supported — they are left in place).
    /// </summary>
    ForwardToOutfit,

    /// <summary>
    /// Keep the antler OFF the NPC entirely: never forward it, AND strip it from
    /// every place the patcher forwards. Removes the antler ARMO from a forwarded
    /// outfit, strips antler ArmorAddon(s) from the forwarded WornArmor duplicate,
    /// and removes keyword-detected antler head parts from the NPC record plus
    /// their baked FaceGen shapes. When the worn outfit is NOT forwarded (the load
    /// order owns it) Remove cannot reach a still-load-order-owned outfit — that is
    /// logged.
    /// </summary>
    Remove,

    /// <summary>
    /// No special handling — the antler is forwarded only if Include Outfit is
    /// selected, like any other outfit element.
    /// </summary>
    None
}
