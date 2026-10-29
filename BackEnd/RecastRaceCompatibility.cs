using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Recast-only output can't change race: Recast copies the donor's face, head parts, skin (WNAM) and
/// sex onto the target but leaves its race alone, so a cross-race selection renders the donor's
/// FaceGen head on the TARGET race's body. That is fine exactly when the target, after the swap,
/// renders the same body the donor renders, because the donor's FaceGen then meets the body its
/// author built it for. This decides that from the records (D2) instead of from race names.
///
/// <para>Compared: the donor <c>D</c> (donor race, donor sex) against the target after the swap
/// <c>T</c> (target race, donor sex):</para>
/// <list type="number">
/// <item><b>Effective skin.</b> The donor's WNAM on both sides, since Recast copies it; when it is
///   null, each side's own <c>Race.Skin</c>.</item>
/// <item><b>Body coverage.</b> Every body slot an ArmorAddon of D's skin covers for D must have an
///   ArmorAddon of T's skin that names T's race. A missing one is an invisible body part.</item>
/// <item><b>Mesh and skin tone.</b> Per slot, the two addons must be the same record or share the
///   world model and skin texture set for the sex. Anything else is a seam or a tone change (D2a).
///   Body tint is copied by Recast, so it is not compared.</item>
/// <item><b>Skeleton.</b> The two races' skeletons for the sex must be the same file; beast races
///   differ.</item>
/// <item><b>Height</b> only produces a note: the race multiplier scales the copied NPC height.</item>
/// </list>
///
/// <para>Race matching is <see cref="Auxilliary.ArmaNamesRace"/> with the ArmorRace (RNAM)
/// indirection, the matcher the wig and render paths use. The render path
/// (<c>NpcMeshResolver</c>) resolves the same effective skin; share it once the viewer extraction
/// has landed, rather than keep two copies.</para>
/// </summary>
public static class RecastRaceCompatibility
{
    /// <param name="Failure">Why the swap would render wrong, or null when it is fine.</param>
    /// <param name="Note">Something worth logging that doesn't block the swap.</param>
    public sealed record Verdict(string? Failure, string? Note)
    {
        public bool Ok => Failure == null;
    }

    public static Verdict Check(
        INpcGetter donor,
        IRaceGetter donorRace,
        IRaceGetter targetRace,
        Func<FormKey, IArmorGetter?> resolveArmor,
        Func<FormKey, IArmorAddonGetter?> resolveArma)
    {
        if (donorRace.FormKey.Equals(targetRace.FormKey)) return new Verdict(null, null);

        bool female = Auxilliary.GetGender(donor) == Gender.Female;
        string sexLabel = female ? "female" : "male";

        // 1. Effective skin.
        FormKey? donorSkin = !donor.WornArmor.IsNull ? donor.WornArmor.FormKey : NullableKey(donorRace.Skin);
        FormKey? targetSkin = !donor.WornArmor.IsNull ? donor.WornArmor.FormKey : NullableKey(targetRace.Skin);

        // 2-3. Body coverage, then mesh and skin tone per slot.
        var donorSlots = SlotAddons(donorSkin, donorRace, resolveArmor, resolveArma);
        var targetSlots = SlotAddons(targetSkin, targetRace, resolveArmor, resolveArma);
        foreach (var (slot, donorAddon) in donorSlots)
        {
            if (!targetSlots.TryGetValue(slot, out var targetAddon))
            {
                return new Verdict(
                    $"its {slot} would be invisible: the skin it would wear ({Describe(targetSkin)}) has no ArmorAddon " +
                    $"for {Describe(targetRace)}, while the donor's {Describe(donorRace)} body has one ({donorAddon.FormKey})", null);
            }

            if (donorAddon.FormKey.Equals(targetAddon.FormKey)) continue;

            string? donorModel = WorldModel(donorAddon, female);
            string? targetModel = WorldModel(targetAddon, female);
            FormKey? donorTexture = SkinTexture(donorAddon, female);
            FormKey? targetTexture = SkinTexture(targetAddon, female);
            if (!string.Equals(donorModel, targetModel, StringComparison.OrdinalIgnoreCase))
            {
                return new Verdict(
                    $"its {slot} would not match the face: {Describe(targetRace)} renders the {sexLabel} mesh " +
                    $"'{targetModel ?? "(none)"}' (ArmorAddon {targetAddon.FormKey}), but the donor's face was made " +
                    $"for '{donorModel ?? "(none)"}' (ArmorAddon {donorAddon.FormKey}), so a seam shows", null);
            }
            if (!Nullable.Equals(donorTexture, targetTexture))
            {
                return new Verdict(
                    $"its {slot} would not match the face: {Describe(targetRace)} renders the {sexLabel} skin texture " +
                    $"{Describe(targetTexture)} (ArmorAddon {targetAddon.FormKey}), but the donor's face was made for " +
                    $"{Describe(donorTexture)} (ArmorAddon {donorAddon.FormKey}), so the skin tone changes at the neck", null);
            }
        }

        // 4. Skeleton.
        string? donorSkeleton = Skeleton(donorRace, female);
        string? targetSkeleton = Skeleton(targetRace, female);
        if (!string.Equals(donorSkeleton, targetSkeleton, StringComparison.OrdinalIgnoreCase))
        {
            return new Verdict(
                $"{Describe(targetRace)} uses a different {sexLabel} skeleton ('{targetSkeleton ?? "none"}') from the " +
                $"donor's {Describe(donorRace)} ('{donorSkeleton ?? "none"}'), so the head would not sit on the body", null);
        }

        // 5. Height.
        float donorHeight = female ? donorRace.Height.Female : donorRace.Height.Male;
        float targetHeight = female ? targetRace.Height.Female : targetRace.Height.Male;
        string? note = Math.Abs(donorHeight - targetHeight) > 0.0001f
            ? $"race height differs ({Describe(donorRace)} {donorHeight:0.###}, {Describe(targetRace)} {targetHeight:0.###}), " +
              "so the NPC renders at a different size than the donor"
            : null;
        return new Verdict(null, note);
    }

    /// <summary>The first ArmorAddon of <paramref name="skin"/> that names <paramref name="race"/>, per
    /// body slot it covers — the engine takes the first match in the armor's list.</summary>
    private static Dictionary<BipedObjectFlag, IArmorAddonGetter> SlotAddons(
        FormKey? skin,
        IRaceGetter race,
        Func<FormKey, IArmorGetter?> resolveArmor,
        Func<FormKey, IArmorAddonGetter?> resolveArma)
    {
        var slots = new Dictionary<BipedObjectFlag, IArmorAddonGetter>();
        if (skin == null || resolveArmor(skin.Value) is not { Armature: { } armature }) return slots;

        var armorRaceKey = Auxilliary.GetArmorRaceKey(race);
        foreach (var link in armature)
        {
            if (link.IsNull || resolveArma(link.FormKey) is not { } arma || arma.BodyTemplate == null) continue;
            if (!Auxilliary.ArmaNamesRace(arma, race.FormKey, armorRaceKey)) continue;

            var flags = arma.BodyTemplate.FirstPersonFlags;
            foreach (var slot in Enum.GetValues<BipedObjectFlag>())
            {
                if (slot != 0 && flags.HasFlag(slot)) slots.TryAdd(slot, arma);
            }
        }
        return slots;
    }

    private static FormKey? NullableKey(IFormLinkNullableGetter<IArmorGetter> link) =>
        link.IsNull ? null : link.FormKey;

    private static string? WorldModel(IArmorAddonGetter arma, bool female) =>
        (female ? arma.WorldModel?.Female : arma.WorldModel?.Male)?.File.GivenPath;

    private static FormKey? SkinTexture(IArmorAddonGetter arma, bool female)
    {
        var link = female ? arma.SkinTexture?.Female : arma.SkinTexture?.Male;
        return link == null || link.IsNull ? null : link.FormKey;
    }

    private static string? Skeleton(IRaceGetter race, bool female) =>
        (female ? race.SkeletalModel?.Female : race.SkeletalModel?.Male)?.File.GivenPath;

    private static string Describe(IRaceGetter race) => race.EditorID ?? race.FormKey.ToString();

    private static string Describe(FormKey? key) => key?.ToString() ?? "none";
}
