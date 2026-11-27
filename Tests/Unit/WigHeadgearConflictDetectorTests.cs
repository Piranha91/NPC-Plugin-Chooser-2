using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="WigHeadgearConflictDetector"/>: the record-level test behind the wig-and-headgear
/// warning. Wig mode "Forward to Outfit" adds a hair-slot wig Armor to the outfit the NPC wears;
/// when that outfit already holds headgear on the same biped slot the game shows only one of the
/// two (Legate Rikke's wig on slot 31 against the Imperial officer helmet on slots 31 and 42).
/// Pure logic on in-memory records: no game install.
/// </summary>
public class WigHeadgearConflictDetectorTests
{
    private readonly SkyrimMod _mod = MutagenFixtures.NewMod("WigHeadgear.esp");

    private Armor NewArmor(string editorId, BipedObjectFlag slots, string? name = null)
    {
        var armor = _mod.Armors.AddNew();
        armor.EditorID = editorId;
        if (name != null) armor.Name = name;
        armor.BodyTemplate = new BodyTemplate { FirstPersonFlags = slots, ArmorType = ArmorType.Clothing };
        return armor;
    }

    private Outfit NewOutfit(string editorId, params IMajorRecordGetter[] items)
    {
        var outfit = _mod.Outfits.AddNew();
        outfit.EditorID = editorId;
        outfit.Items = new Noggog.ExtendedList<IFormLinkGetter<IOutfitTargetGetter>>();
        foreach (var item in items) outfit.Items.Add(item.FormKey.ToLink<IOutfitTargetGetter>());
        return outfit;
    }

    private LeveledItem NewList(string editorId, params IMajorRecordGetter[] entries)
    {
        var list = _mod.LeveledItems.AddNew();
        list.EditorID = editorId;
        list.Entries = new Noggog.ExtendedList<LeveledItemEntry>();
        foreach (var entry in entries) AddEntry(list, entry.FormKey);
        return list;
    }

    private static void AddEntry(LeveledItem list, FormKey reference)
    {
        list.Entries ??= new Noggog.ExtendedList<LeveledItemEntry>();
        list.Entries.Add(new LeveledItemEntry
        {
            Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = reference.ToLink<IItemGetter>() },
        });
    }

    /// <summary>Resolves against everything in the fixture mod at the time of the call.</summary>
    private Func<FormKey, IMajorRecordGetter?> Resolver()
    {
        var records = _mod.EnumerateMajorRecords().ToDictionary(r => r.FormKey, r => (IMajorRecordGetter)r);
        return formKey => records.TryGetValue(formKey, out var record) ? record : null;
    }

    private Armor Wig() => NewArmor("RikkeWig", BipedObjectFlag.Hair, "Rikke Wig");

    private Armor Helmet() =>
        NewArmor("ArmorImperialHelmetOfficer", BipedObjectFlag.Hair | BipedObjectFlag.Circlet, "Imperial Officer's Helmet");

    [Fact]
    public void DirectItemOnTheSameSlot_IsReportedWithNamesAndSlotNumbersFromTheRecords()
    {
        var wig = Wig();
        var helmet = Helmet();
        var cuirass = NewArmor("ArmorImperialCuirass", BipedObjectFlag.Body, "Imperial Armor");
        var outfit = NewOutfit("GeneralTulliusOutfit", cuirass, helmet);

        var conflicts = WigHeadgearConflictDetector.Find(new IArmorGetter[] { wig }, outfit, Resolver());

        var conflict = conflicts.Should().ContainSingle().Subject;
        conflict.Wig.FormKey.Should().Be(wig.FormKey);
        conflict.Wig.EditorID.Should().Be("RikkeWig");
        conflict.Wig.Name.Should().Be("Rikke Wig");
        conflict.Wig.Slots.Should().Equal(31);
        conflict.Headgear.FormKey.Should().Be(helmet.FormKey);
        conflict.Headgear.EditorID.Should().Be("ArmorImperialHelmetOfficer");
        conflict.Headgear.Name.Should().Be("Imperial Officer's Helmet");
        conflict.Headgear.Slots.Should().Equal(31, 42);
        conflict.OutfitFormKey.Should().Be(outfit.FormKey);
        conflict.OutfitEditorID.Should().Be("GeneralTulliusOutfit");
        conflict.SharedSlots.Should().Equal(31);
    }

    [Fact]
    public void HeadgearTwoLeveledListsDeep_IsFound()
    {
        // The observed outfit holds a leveled list beside the helmet, so lists must be followed.
        var wig = Wig();
        var helmet = Helmet();
        var inner = NewList("LItemImperialHelmets", helmet);
        var outer = NewList("LItemImperialOfficerGear", inner);
        var outfit = NewOutfit("ImperialOfficerOutfit", outer);

        var conflicts = WigHeadgearConflictDetector.Find(new IArmorGetter[] { wig }, outfit, Resolver());

        conflicts.Should().ContainSingle().Which.Headgear.FormKey.Should().Be(helmet.FormKey);
    }

    [Fact]
    public void LeveledListsThatReferenceEachOther_TerminateAndReportTheHeadgearOnce()
    {
        var wig = Wig();
        var helmet = Helmet();
        var listA = NewList("LItemCycleA");
        var listB = NewList("LItemCycleB", helmet);
        AddEntry(listA, listB.FormKey);
        AddEntry(listB, listA.FormKey);
        AddEntry(listA, listA.FormKey); // and a list that names itself
        var outfit = NewOutfit("CycleOutfit", listA);

        var conflicts = WigHeadgearConflictDetector.Find(new IArmorGetter[] { wig }, outfit, Resolver());

        conflicts.Should().ContainSingle().Which.Headgear.FormKey.Should().Be(helmet.FormKey);
    }

    [Fact]
    public void HeadgearReachedByTwoRoutes_IsReportedOnce()
    {
        var wig = Wig();
        var helmet = Helmet();
        var list = NewList("LItemHelmets", helmet);
        var outfit = NewOutfit("HelmetTwiceOutfit", helmet, list);

        WigHeadgearConflictDetector.Find(new IArmorGetter[] { wig }, outfit, Resolver())
            .Should().ContainSingle();
    }

    [Fact]
    public void ItemsOnOtherSlots_AreNotConflicts()
    {
        var wig = Wig();
        var cuirass = NewArmor("ArmorImperialCuirass", BipedObjectFlag.Body);
        var circlet = NewArmor("ClothesCirclet01", BipedObjectFlag.Circlet);
        var boots = NewList("LItemBoots", NewArmor("ArmorImperialBoots", BipedObjectFlag.Feet));
        var outfit = NewOutfit("NoHeadgearOutfit", cuirass, circlet, boots);

        WigHeadgearConflictDetector.Find(new IArmorGetter[] { wig }, outfit, Resolver())
            .Should().BeEmpty("slot 31 against slots 32, 42 and 37 shares nothing");
    }

    [Fact]
    public void TheWigPieceAlreadyInTheOutfit_IsNotAConflictWithItself()
    {
        var wig = Wig();
        var cuirass = NewArmor("ArmorImperialCuirass", BipedObjectFlag.Body);
        var nested = NewList("LItemHoldsTheWigToo", wig);
        var outfit = NewOutfit("WigAlreadyThereOutfit", wig, cuirass, nested);

        WigHeadgearConflictDetector.Find(new IArmorGetter[] { wig }, outfit, Resolver())
            .Should().BeEmpty("the wig shares its own slots with itself, which is not headgear");
    }

    [Fact]
    public void ItemsThatAreNeitherArmorNorLeveledItem_AndUnresolvableLinks_AreIgnored()
    {
        var wig = Wig();
        var misc = _mod.MiscItems.AddNew();
        misc.EditorID = "Lockpick";
        var list = NewList("LItemMixed", misc);
        AddEntry(list, FormKey.Factory("0ABCDE:NotLoaded.esp"));
        var outfit = NewOutfit("MixedOutfit", list);
        outfit.Items!.Add(FormKey.Factory("0ABCDF:NotLoaded.esp").ToLink<IOutfitTargetGetter>());

        WigHeadgearConflictDetector.Find(new IArmorGetter[] { wig }, outfit, Resolver())
            .Should().BeEmpty();
    }

    [Fact]
    public void EveryWigPieceIsTestedAgainstEveryItem()
    {
        var wig = Wig();
        var longWig = NewArmor("RikkeWigLong", BipedObjectFlag.LongHair);
        var helmet = Helmet();                                          // 31 and 42
        var hood = NewArmor("ClothesHood", BipedObjectFlag.Hair | BipedObjectFlag.LongHair); // 31 and 41
        var outfit = NewOutfit("TwoHatsOutfit", helmet, hood);

        var conflicts = WigHeadgearConflictDetector.Find(new IArmorGetter[] { wig, longWig }, outfit, Resolver());

        conflicts.Select(c => (c.Wig.EditorID, c.Headgear.EditorID, Slots: string.Join(",", c.SharedSlots)))
            .Should().BeEquivalentTo(new[]
            {
                ("RikkeWig", "ArmorImperialHelmetOfficer", "31"),
                ("RikkeWig", "ClothesHood", "31"),
                ("RikkeWigLong", "ClothesHood", "41"),
            });
    }

    [Fact]
    public void NoOutfitOrNoWigPieces_FindsNothing()
    {
        var wig = Wig();
        var outfit = NewOutfit("HelmetOutfit", Helmet());

        WigHeadgearConflictDetector.Find(new IArmorGetter[] { wig }, null, Resolver()).Should().BeEmpty();
        WigHeadgearConflictDetector.Find(Array.Empty<IArmorGetter>(), outfit, Resolver()).Should().BeEmpty();
    }

    [Fact]
    public void SlotNumbers_CountFromThirty()
    {
        WigHeadgearConflictDetector.SlotNumbers(BipedObjectFlag.Hair).Should().Equal(31);
        WigHeadgearConflictDetector.SlotNumbers(BipedObjectFlag.Hair | BipedObjectFlag.Circlet).Should().Equal(31, 42);
        WigHeadgearConflictDetector.SlotNumbers(BipedObjectFlag.Body | BipedObjectFlag.LongHair).Should().Equal(32, 41);
        WigHeadgearConflictDetector.SlotNumbers((BipedObjectFlag)1).Should().Equal(30);
        WigHeadgearConflictDetector.SlotNumbers((BipedObjectFlag)0x80000000).Should().Equal(61);
        WigHeadgearConflictDetector.SlotNumbers(0).Should().BeEmpty();
    }

    [Fact]
    public void Describe_NamesTheWigTheOutfitTheHeadgearAndTheSlots()
    {
        var wig = Wig();
        var helmet = Helmet();
        var outfit = NewOutfit("GeneralTulliusOutfit", helmet);

        var conflict = WigHeadgearConflictDetector.Find(new IArmorGetter[] { wig }, outfit, Resolver())
            .Should().ContainSingle().Subject;

        conflict.Describe().Should().Be(
            "wig 'Rikke Wig (RikkeWig)' (slot 31) was added to outfit 'GeneralTulliusOutfit', which holds " +
            "'Imperial Officer's Helmet (ArmorImperialHelmetOfficer)' (slots 31 and 42); both use slot 31");
    }

    [Fact]
    public void FormatSlots_ReadsAsAPhrase()
    {
        WigHeadgearConflictDetector.FormatSlots(new[] { 31 }).Should().Be("slot 31");
        WigHeadgearConflictDetector.FormatSlots(new[] { 31, 42 }).Should().Be("slots 31 and 42");
        WigHeadgearConflictDetector.FormatSlots(new[] { 31, 41, 42 }).Should().Be("slots 31, 41 and 42");
        WigHeadgearConflictDetector.FormatSlots(Array.Empty<int>()).Should().Be("no slot");
    }
}
