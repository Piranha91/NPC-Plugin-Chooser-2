using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Scans a mod's plugins for wig and antler armors (see
/// <see cref="Models.WigHandlingMode"/> for what happens to them at patch /
/// render time). A wig is an ARMO whose Name contains "wig" or "hair"
/// (case-insensitive) AND whose ArmorAddon(s) occupy a hair biped slot
/// (31 Hair / 41 LongHair) — the slot guard also rejects incidental
/// substring hits like "Draugr Wight Armor" or "Hairband" jewelry. An
/// antler is an ARMO whose EditorID or Name contains "antler", with no
/// slot guard because antler slots aren't standardized (FoxGlove Auri uses
/// 42/Circlet; guarding on 42 alone would false-positive real circlets).
/// Pure static logic so it is unit-testable with in-memory mods.
/// </summary>
public static class WigDetector
{
    public const BipedObjectFlag HairSlots = BipedObjectFlag.Hair | BipedObjectFlag.LongHair;

    /// <summary>Detection result. Wigs and antlers are classified independently
    /// (see <see cref="Models.WigHandlingMode"/> / <see cref="Models.AntlerHandlingMode"/>).
    /// Antlers come from three sources so <c>Remove</c> can reach all of them:
    /// an antler ARMO in an outfit (<see cref="AntlerArmors"/>), antler
    /// ArmorAddon(s) baked into a WornArmor (<see cref="AntlerArmatures"/>), and
    /// an antler head part baked into the FaceGen (<see cref="AntlerHeadParts"/>).</summary>
    public readonly record struct WigScanResult(
        HashSet<FormKey> Wigs,
        HashSet<FormKey> AntlerArmors,
        HashSet<FormKey> AntlerArmatures,
        HashSet<FormKey> AntlerHeadParts);

    /// <summary>
    /// Scans every ARMO / ARMA / HeadPart in <paramref name="plugins"/> (a mod's
    /// loaded plugin set). Wig ARMA links resolve against the mod's own plugins
    /// first (later plugin wins, matching intra-mod override order), then through
    /// <paramref name="fallbackArmaResolver"/> (typically the load-order link
    /// cache) for armatures the mod inherits from its masters. An armor matching
    /// both keyword classes counts as an antler (the more permissive class — all
    /// its ArmorAddons get forwarded, not just hair-slot ones). The antler ARMO's
    /// own addons are folded into <see cref="WigScanResult.AntlerArmatures"/> so a
    /// WornArmor that references them directly is still caught.
    /// </summary>
    public static WigScanResult Scan(
        IReadOnlyCollection<ISkyrimModGetter> plugins,
        Func<FormKey, IArmorAddonGetter?>? fallbackArmaResolver = null)
    {
        var wigs = new HashSet<FormKey>();
        var antlerArmors = new HashSet<FormKey>();
        var antlerArmatures = new HashSet<FormKey>();
        var antlerHeadParts = new HashSet<FormKey>();

        var localArmas = new Dictionary<FormKey, IArmorAddonGetter>();
        foreach (var plugin in plugins)
        {
            foreach (var arma in plugin.ArmorAddons)
            {
                localArmas[arma.FormKey] = arma;
            }
        }

        foreach (var plugin in plugins)
        {
            // Source 2: an antler ArmorAddon baked into a WornArmor. ARMAs have
            // EditorIDs; an antler addon usually names itself even when its parent
            // armor doesn't. (No slot guard — antler slots aren't standardized.)
            foreach (var arma in plugin.ArmorAddons)
            {
                if ((arma.EditorID ?? string.Empty).Contains("antler", StringComparison.OrdinalIgnoreCase))
                {
                    antlerArmatures.Add(arma.FormKey);
                }
            }

            // Source 3: an antler head part baked into the FaceGen. Keyword on
            // Name or EditorID only — non-intelligible head-part names (e.g.
            // "000CotG_FaendalHairlineExtra04") need manual designation, which is
            // a separate feature; those are out of scope here.
            foreach (var hp in plugin.HeadParts)
            {
                if ((hp.Name?.String ?? string.Empty).Contains("antler", StringComparison.OrdinalIgnoreCase) ||
                    (hp.EditorID ?? string.Empty).Contains("antler", StringComparison.OrdinalIgnoreCase))
                {
                    antlerHeadParts.Add(hp.FormKey);
                }
            }

            foreach (var armor in plugin.Armors)
            {
                string name = armor.Name?.String ?? string.Empty;
                string editorId = armor.EditorID ?? string.Empty;

                if (name.Contains("antler", StringComparison.OrdinalIgnoreCase) ||
                    editorId.Contains("antler", StringComparison.OrdinalIgnoreCase))
                {
                    antlerArmors.Add(armor.FormKey);
                    if (armor.Armature != null)
                    {
                        foreach (var armaLink in armor.Armature)
                        {
                            if (armaLink != null && !armaLink.IsNull) antlerArmatures.Add(armaLink.FormKey);
                        }
                    }
                    continue;
                }

                if (!name.Contains("wig", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("hair", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (armor.Armature == null) continue;
                foreach (var armaLink in armor.Armature)
                {
                    if (armaLink == null || armaLink.IsNull) continue;
                    var arma = localArmas.TryGetValue(armaLink.FormKey, out var local)
                        ? local
                        : fallbackArmaResolver?.Invoke(armaLink.FormKey);
                    if (arma?.BodyTemplate == null) continue;
                    if ((arma.BodyTemplate.FirstPersonFlags & HairSlots) != 0)
                    {
                        wigs.Add(armor.FormKey);
                        break;
                    }
                }
            }
        }

        return new WigScanResult(wigs, antlerArmors, antlerArmatures, antlerHeadParts);
    }

    /// <summary>
    /// The ArmorAddons of <paramref name="armor"/> that wig forwarding
    /// transfers into a WNAM duplicate: hair-slot ARMAs for wigs, ALL ARMAs
    /// for antlers (their slots aren't standardized).
    /// </summary>
    public static IEnumerable<IFormLinkGetter<IArmorAddonGetter>> GetForwardableArmatures(
        IArmorGetter armor, bool isAntler, Func<FormKey, IArmorAddonGetter?> armaResolver)
    {
        if (armor.Armature == null) yield break;
        foreach (var armaLink in armor.Armature)
        {
            if (armaLink == null || armaLink.IsNull) continue;
            if (isAntler)
            {
                yield return armaLink;
                continue;
            }

            var arma = armaResolver(armaLink.FormKey);
            if (arma?.BodyTemplate != null && (arma.BodyTemplate.FirstPersonFlags & HairSlots) != 0)
            {
                yield return armaLink;
            }
        }
    }
}
