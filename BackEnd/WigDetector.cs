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
/// A skin-carried (WNAM) wig is a hair-slot ArmorAddon carried directly in
/// an NPC's WornArmor rather than in an outfit wig ARMO (the High Poly NPC
/// Overhaul pattern: bald FaceGen + skin hair ARMA) — detected slot-first,
/// scoped to ARMAs actually reachable from NPC records, with the NPC's
/// race default skin excluded (Khajiit/Argonian and custom-race anatomy is
/// not a wig). Manual per-ARMA designations
/// (<see cref="Models.Settings.IsWigArmature"/>) refine the persisted set
/// at consumption time. Pure static logic so it is unit-testable with
/// in-memory mods.
/// </summary>
public static class WigDetector
{
    public const BipedObjectFlag HairSlots = BipedObjectFlag.Hair | BipedObjectFlag.LongHair;

    /// <summary>Detection result. Wigs and antlers are classified independently
    /// (see <see cref="Models.WigHandlingMode"/> / <see cref="Models.AntlerHandlingMode"/>).
    /// Wigs come from two sources: a wig ARMO in an outfit (<see cref="Wigs"/>)
    /// and a hair-slot ArmorAddon carried in an NPC's WornArmor
    /// (<see cref="WigArmatures"/>). Antlers come from three sources so
    /// <c>Remove</c> can reach all of them:
    /// an antler ARMO in an outfit (<see cref="AntlerArmors"/>), antler
    /// ArmorAddon(s) baked into a WornArmor (<see cref="AntlerArmatures"/>), and
    /// an antler head part baked into the FaceGen (<see cref="AntlerHeadParts"/>).</summary>
    public readonly record struct WigScanResult(
        HashSet<FormKey> Wigs,
        HashSet<FormKey> WigArmatures,
        HashSet<FormKey> AntlerArmors,
        HashSet<FormKey> AntlerArmatures,
        HashSet<FormKey> AntlerHeadParts);

    /// <summary>
    /// Scans every ARMO / ARMA / HeadPart / NPC in <paramref name="plugins"/> (a
    /// mod's loaded plugin set). Wig ARMA links resolve against the mod's own
    /// plugins first (later plugin wins, matching intra-mod override order), then
    /// through <paramref name="fallbackArmaResolver"/> (typically the load-order
    /// link cache) for armatures the mod inherits from its masters;
    /// <paramref name="fallbackArmorResolver"/> and
    /// <paramref name="fallbackRaceResolver"/> play the same role for the WNAM
    /// walk's WornArmor links and the race-default-skin guard. An armor matching
    /// both keyword classes counts as an antler (the more permissive class — all
    /// its ArmorAddons get forwarded, not just hair-slot ones). The antler ARMO's
    /// own addons are folded into <see cref="WigScanResult.AntlerArmatures"/> so a
    /// WornArmor that references them directly is still caught.
    /// </summary>
    public static WigScanResult Scan(
        IReadOnlyCollection<ISkyrimModGetter> plugins,
        Func<FormKey, IArmorAddonGetter?>? fallbackArmaResolver = null,
        Func<FormKey, IArmorGetter?>? fallbackArmorResolver = null,
        Func<FormKey, IRaceGetter?>? fallbackRaceResolver = null)
    {
        var wigs = new HashSet<FormKey>();
        var wigArmatures = new HashSet<FormKey>();
        var antlerArmors = new HashSet<FormKey>();
        var antlerArmatures = new HashSet<FormKey>();
        var antlerHeadParts = new HashSet<FormKey>();

        var localArmas = new Dictionary<FormKey, IArmorAddonGetter>();
        var localArmors = new Dictionary<FormKey, IArmorGetter>();
        var localRaces = new Dictionary<FormKey, IRaceGetter>();
        foreach (var plugin in plugins)
        {
            foreach (var arma in plugin.ArmorAddons)
            {
                localArmas[arma.FormKey] = arma;
            }
            foreach (var armor in plugin.Armors)
            {
                localArmors[armor.FormKey] = armor;
            }
            foreach (var race in plugin.Races)
            {
                localRaces[race.FormKey] = race;
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

        // Skin-carried (WNAM) wig passes. Run AFTER the main loop so the antler
        // exclusion below sees the complete antler-armature set regardless of
        // plugin order.
        foreach (var plugin in plugins)
        {
            // Pass 1 (slot, NPC-scoped): every hair-slot ARMA reachable from an
            // NPC's WornArmor auto-detects. Scoped to NPC records — not all
            // plugin ARMAs — so unreferenced addons never enter; a WNAM that IS
            // the NPC's race default skin is skipped entirely (beast-race manes
            // and custom-race anatomy are the race's skin, not a wig).
            foreach (var npc in plugin.Npcs)
            {
                if (npc.WornArmor.IsNull) continue;
                if (!npc.Race.IsNull)
                {
                    var race = localRaces.TryGetValue(npc.Race.FormKey, out var localRace)
                        ? localRace
                        : fallbackRaceResolver?.Invoke(npc.Race.FormKey);
                    if (race != null && !race.Skin.IsNull &&
                        race.Skin.FormKey == npc.WornArmor.FormKey)
                    {
                        continue;
                    }
                }

                var wnam = localArmors.TryGetValue(npc.WornArmor.FormKey, out var localWnam)
                    ? localWnam
                    : fallbackArmorResolver?.Invoke(npc.WornArmor.FormKey);
                if (wnam?.Armature == null) continue;
                foreach (var armaLink in wnam.Armature)
                {
                    if (armaLink == null || armaLink.IsNull) continue;
                    if (antlerArmatures.Contains(armaLink.FormKey)) continue;
                    var arma = localArmas.TryGetValue(armaLink.FormKey, out var localArma)
                        ? localArma
                        : fallbackArmaResolver?.Invoke(armaLink.FormKey);
                    if (arma?.BodyTemplate == null) continue;
                    if ((arma.BodyTemplate.FirstPersonFlags & HairSlots) == 0) continue;
                    if (IsRaceDefaultSkinArma(arma, localArmors, localRaces,
                            fallbackArmorResolver, fallbackRaceResolver))
                    {
                        continue;
                    }
                    wigArmatures.Add(armaLink.FormKey);
                }
            }

            // Pass 2 (keyword): a hair-slot ARMA named like hair still detects
            // even when the NPC wearing its WNAM lives in a plugin outside this
            // mod. The slot requirement stays mandatory — keyword alone never
            // detects, so "Wight"-style substring hits stay excluded.
            foreach (var arma in plugin.ArmorAddons)
            {
                if (wigArmatures.Contains(arma.FormKey) ||
                    antlerArmatures.Contains(arma.FormKey))
                {
                    continue;
                }
                if (arma.BodyTemplate == null ||
                    (arma.BodyTemplate.FirstPersonFlags & HairSlots) == 0)
                {
                    continue;
                }
                if (!ContainsWigKeyword(arma.EditorID) &&
                    !ContainsWigKeyword(arma.WorldModel?.Male?.File?.GivenPath) &&
                    !ContainsWigKeyword(arma.WorldModel?.Female?.File?.GivenPath))
                {
                    continue;
                }
                if (IsRaceDefaultSkinArma(arma, localArmors, localRaces,
                        fallbackArmorResolver, fallbackRaceResolver))
                {
                    continue;
                }
                wigArmatures.Add(arma.FormKey);
            }
        }

        return new WigScanResult(wigs, wigArmatures, antlerArmors, antlerArmatures, antlerHeadParts);
    }

    private static bool ContainsWigKeyword(string? s) =>
        s != null &&
        (s.Contains("wig", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("hair", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an ARMA is part of a default skin of any race it
    /// declares (Race / AdditionalRaces): authoritative "race anatomy"
    /// evidence, never a wig. This is the second half of the race-skin guard —
    /// the NPC-side WNAM==race.Skin check alone misses NPCs whose race is
    /// template-inherited (Race unset on the record), e.g. werewolf-form NPCs
    /// wearing SkinNakedWerewolfBeast (found by the HPNO benchmark).</summary>
    private static bool IsRaceDefaultSkinArma(IArmorAddonGetter arma,
        Dictionary<FormKey, IArmorGetter> localArmors,
        Dictionary<FormKey, IRaceGetter> localRaces,
        Func<FormKey, IArmorGetter?>? fallbackArmorResolver,
        Func<FormKey, IRaceGetter?>? fallbackRaceResolver)
    {
        IEnumerable<FormKey> RaceKeys()
        {
            if (!arma.Race.IsNull) yield return arma.Race.FormKey;
            if (arma.AdditionalRaces == null) yield break;
            foreach (var extra in arma.AdditionalRaces)
            {
                if (extra != null && !extra.IsNull) yield return extra.FormKey;
            }
        }

        foreach (var raceKey in RaceKeys())
        {
            var race = localRaces.TryGetValue(raceKey, out var localRace)
                ? localRace
                : fallbackRaceResolver?.Invoke(raceKey);
            if (race == null || race.Skin.IsNull) continue;
            var skin = localArmors.TryGetValue(race.Skin.FormKey, out var localSkin)
                ? localSkin
                : fallbackArmorResolver?.Invoke(race.Skin.FormKey);
            if (skin?.Armature == null) continue;
            foreach (var link in skin.Armature)
            {
                if (link != null && !link.IsNull && link.FormKey == arma.FormKey) return true;
            }
        }

        return false;
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
