using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>One side of a wig-and-headgear conflict, as read from its record.</summary>
/// <param name="FormKey">The record's FormKey.</param>
/// <param name="EditorID">The record's EditorID, when it has one.</param>
/// <param name="Name">The record's in-game name, when it has one.</param>
/// <param name="Slots">The biped slots the record declares, as the usual slot numbers
/// (30 to 61), ascending.</param>
public sealed record WigHeadgearConflictItem(
    FormKey FormKey, string? EditorID, string? Name, IReadOnlyList<int> Slots)
{
    /// <summary>"Name (EditorID)", or whichever of the two exists, or the FormKey.</summary>
    public string Label =>
        !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(EditorID) ? $"{Name} ({EditorID})"
        : !string.IsNullOrWhiteSpace(Name) ? Name!
        : !string.IsNullOrWhiteSpace(EditorID) ? EditorID!
        : FormKey.ToString();
}

/// <summary>
/// A wig that <see cref="NPC_Plugin_Chooser_2.Models.WigHandlingMode.ForwardToOutfit"/> would add
/// to an outfit, and one item that outfit already holds on a biped slot the wig also claims. The
/// game shows only one of two items on the same slot.
/// </summary>
/// <param name="Wig">The wig piece. For a wig the mod carries in an outfit this is the wig Armor.
/// For a wig the mod carries on the skin it is the wig ArmorAddon (which has no Name): the
/// wearable Armor NPC2 wraps around it is only created during the run, with the same slots.</param>
/// <param name="Headgear">The Armor already in the outfit (directly, or through leveled lists).</param>
/// <param name="OutfitFormKey">The outfit the wig would be added to.</param>
/// <param name="OutfitEditorID">That outfit's EditorID, when it has one.</param>
/// <param name="SharedSlots">The slot numbers both claim, ascending.</param>
public sealed record WigHeadgearConflict(
    WigHeadgearConflictItem Wig,
    WigHeadgearConflictItem Headgear,
    FormKey OutfitFormKey,
    string? OutfitEditorID,
    IReadOnlyList<int> SharedSlots)
{
    /// <summary>The outfit's EditorID, or its FormKey when it has none.</summary>
    public string OutfitLabel =>
        string.IsNullOrWhiteSpace(OutfitEditorID) ? OutfitFormKey.ToString() : OutfitEditorID!;

    /// <summary>One sentence naming the wig, the outfit, the headgear and the slot numbers, for the
    /// run log and for a list row.</summary>
    public string Describe() =>
        $"wig '{Wig.Label}' ({WigHeadgearConflictDetector.FormatSlots(Wig.Slots)}) was added to outfit " +
        $"'{OutfitLabel}', which holds '{Headgear.Label}' " +
        $"({WigHeadgearConflictDetector.FormatSlots(Headgear.Slots)}); both use " +
        $"{WigHeadgearConflictDetector.FormatSlots(SharedSlots)}";
}

/// <summary>
/// One NPC whose wig would be forwarded into an outfit that holds headgear on the same slot, with
/// no stored answer yet. Returned by <see cref="WigForwarder.FindUnansweredHeadgearConflicts"/>.
/// </summary>
/// <param name="TargetNpcFormKey">The NPC being patched: the key to store the answer under in
/// <see cref="NPC_Plugin_Chooser_2.Models.Settings.WigHeadgearConflictChoices"/>.</param>
/// <param name="TargetNpcDisplay">That NPC as the run log names it.</param>
/// <param name="AppearanceModName">Display name of the appearance mod selected for the NPC.</param>
/// <param name="Conflicts">Every wig and headgear pair found for the NPC (at least one).</param>
public sealed record WigHeadgearConflictCase(
    FormKey TargetNpcFormKey,
    string TargetNpcDisplay,
    string AppearanceModName,
    IReadOnlyList<WigHeadgearConflict> Conflicts);

/// <summary>
/// The record-level test behind the wig-and-headgear warning: does an outfit already hold an item
/// on a biped slot a forwarded wig claims? Pure; links are resolved through a delegate so it runs
/// on in-memory records.
/// </summary>
public static class WigHeadgearConflictDetector
{
    /// <summary>A wig piece about to be added to an outfit, reduced to what the test reads.</summary>
    public sealed record WigPiece(FormKey FormKey, string? EditorID, string? Name, BipedObjectFlag Slots)
    {
        public static WigPiece From(IArmorGetter armor) => new(
            armor.FormKey, armor.EditorID, armor.Name?.String,
            armor.BodyTemplate?.FirstPersonFlags ?? 0);
    }

    /// <summary>Guards against a leveled list that is merely very deep rather than cyclic.</summary>
    private const int MaxLeveledDepth = 32;

    /// <summary>
    /// Every (wig piece, outfit item) pair whose biped slots intersect. An outfit item is an Armor,
    /// or a LeveledItem whose entries are followed down to Armors (a list already visited is not
    /// entered again); other item types are ignored. An item that is itself one of
    /// <paramref name="wigPieces"/> is never reported.
    /// </summary>
    /// <param name="resolve">Returns the record a FormKey names (an Armor or a LeveledItem), or null
    /// when it is neither or cannot be found.</param>
    public static IReadOnlyList<WigHeadgearConflict> Find(
        IEnumerable<IArmorGetter> wigPieces, IOutfitGetter? outfit, Func<FormKey, IMajorRecordGetter?> resolve) =>
        Find(wigPieces.Select(WigPiece.From).ToList(), outfit, resolve);

    /// <summary>
    /// The same test for wig pieces already reduced to <see cref="WigPiece"/>: the caller that
    /// forwards a skin-carried wig knows its slots before the wearable Armor exists.
    /// </summary>
    public static IReadOnlyList<WigHeadgearConflict> Find(
        IReadOnlyCollection<WigPiece> wigPieces, IOutfitGetter? outfit, Func<FormKey, IMajorRecordGetter?> resolve)
    {
        if (outfit?.Items == null || wigPieces.Count == 0) return Array.Empty<WigHeadgearConflict>();

        var pieces = wigPieces.DistinctBy(p => p.FormKey).ToList();
        var wigKeys = pieces.Select(p => p.FormKey).ToHashSet();

        // Every Armor the outfit can put on the NPC, each once, in the order first reached.
        var armors = new List<IArmorGetter>();
        var seenArmors = new HashSet<FormKey>();
        var enteredLists = new HashSet<FormKey>();

        void Visit(FormKey formKey, int depth)
        {
            if (formKey.IsNull) return;

            switch (resolve(formKey))
            {
                case IArmorGetter armor:
                    if (seenArmors.Add(armor.FormKey)) armors.Add(armor);
                    break;

                case ILeveledItemGetter list:
                    // A list entered once is not entered again: that is the cycle guard, and it
                    // also keeps a list shared by several others from being walked repeatedly.
                    if (!enteredLists.Add(list.FormKey) || depth >= MaxLeveledDepth || list.Entries == null) break;
                    foreach (var entry in list.Entries)
                    {
                        var reference = entry?.Data?.Reference;
                        if (reference == null || reference.IsNull) continue;
                        Visit(reference.FormKey, depth + 1);
                    }
                    break;
            }
        }

        foreach (var item in outfit.Items)
        {
            if (item == null || item.IsNull) continue;
            Visit(item.FormKey, 0);
        }

        var conflicts = new List<WigHeadgearConflict>();
        foreach (var piece in pieces)
        {
            if (piece.Slots == 0) continue;

            foreach (var armor in armors)
            {
                // A wig piece found in the outfit is the wig, not headgear in its way.
                if (wigKeys.Contains(armor.FormKey)) continue;

                var armorSlots = armor.BodyTemplate?.FirstPersonFlags ?? 0;
                var shared = piece.Slots & armorSlots;
                if (shared == 0) continue;

                conflicts.Add(new WigHeadgearConflict(
                    new WigHeadgearConflictItem(piece.FormKey, piece.EditorID, piece.Name, SlotNumbers(piece.Slots)),
                    new WigHeadgearConflictItem(armor.FormKey, armor.EditorID, armor.Name?.String,
                        SlotNumbers(armorSlots)),
                    outfit.FormKey,
                    outfit.EditorID,
                    SlotNumbers(shared)));
            }
        }

        return conflicts;
    }

    /// <summary>Biped slot numbers of a slot mask, ascending. Bit 0 is slot 30.</summary>
    public static IReadOnlyList<int> SlotNumbers(BipedObjectFlag flags)
    {
        var numbers = new List<int>();
        uint bits = (uint)flags;
        for (int bit = 0; bit < 32; bit++)
        {
            if ((bits & (1u << bit)) != 0) numbers.Add(30 + bit);
        }
        return numbers;
    }

    /// <summary>"slot 31", "slots 31 and 42", "slots 31, 41 and 42"; "no slot" for an empty list.</summary>
    public static string FormatSlots(IReadOnlyList<int> slots) => slots.Count switch
    {
        0 => "no slot",
        1 => "slot " + slots[0],
        _ => "slots " + string.Join(", ", slots.Take(slots.Count - 1)) + " and " + slots[^1],
    };
}
