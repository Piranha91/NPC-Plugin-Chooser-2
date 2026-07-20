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

    /// <summary>
    /// Scans every ARMO in <paramref name="plugins"/> (a mod's loaded plugin
    /// set). ARMA links resolve against the mod's own plugins first (later
    /// plugin wins, matching intra-mod override order), then through
    /// <paramref name="fallbackArmaResolver"/> (typically the load-order
    /// link cache) for armatures the mod inherits from its masters.
    /// An armor matching both keyword classes counts as an antler (the more
    /// permissive class — all its ArmorAddons get forwarded, not just
    /// hair-slot ones).
    /// </summary>
    public static (HashSet<FormKey> Wigs, HashSet<FormKey> Antlers) Scan(
        IReadOnlyCollection<ISkyrimModGetter> plugins,
        Func<FormKey, IArmorAddonGetter?>? fallbackArmaResolver = null)
    {
        var wigs = new HashSet<FormKey>();
        var antlers = new HashSet<FormKey>();

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
            foreach (var armor in plugin.Armors)
            {
                string name = armor.Name?.String ?? string.Empty;
                string editorId = armor.EditorID ?? string.Empty;

                if (name.Contains("antler", StringComparison.OrdinalIgnoreCase) ||
                    editorId.Contains("antler", StringComparison.OrdinalIgnoreCase))
                {
                    antlers.Add(armor.FormKey);
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

        return (wigs, antlers);
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
