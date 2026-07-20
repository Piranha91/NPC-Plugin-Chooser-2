using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Locks <see cref="WigDetector"/>'s classification contract: a wig is an ARMO
/// whose Name contains "wig"/"hair" AND whose ARMA(s) occupy a hair biped slot
/// (31 Hair / 41 LongHair) — the slot guard is what rejects incidental
/// substring hits ("Draugr Wight Armor", "Hairband" circlets). An antler is
/// keyword-only (EditorID or Name contains "antler", no slot guard — FoxGlove
/// Auri's antlers sit on slot 42/Circlet and other mods use other slots).
/// </summary>
public class WigDetectorTests
{
    private static ArmorAddon NewArma(SkyrimMod mod, BipedObjectFlag slots)
    {
        var arma = mod.ArmorAddons.AddNew();
        arma.BodyTemplate = new BodyTemplate { FirstPersonFlags = slots };
        return arma;
    }

    private static Armor NewArmor(SkyrimMod mod, string? name, string? editorId, params ArmorAddon[] armas)
    {
        var armor = mod.Armors.AddNew();
        if (name != null) armor.Name = name;
        if (editorId != null) armor.EditorID = editorId;
        foreach (var arma in armas) armor.Armature.Add(arma.ToLink());
        return armor;
    }

    private static (HashSet<FormKey> Wigs, HashSet<FormKey> Antlers) Scan(SkyrimMod mod,
        Func<FormKey, IArmorAddonGetter?>? fallback = null)
        => WigDetector.Scan(new[] { mod }, fallback);

    [Fact]
    public void WigName_WithHairSlotArma_IsDetected()
    {
        var mod = MutagenFixtures.NewMod("FoxGloveAuri.esp");
        var armor = NewArmor(mod, "Auri Red Wig", "AuriWig", NewArma(mod, BipedObjectFlag.Hair));

        var (wigs, antlers) = Scan(mod);

        wigs.Should().BeEquivalentTo(new[] { armor.FormKey });
        antlers.Should().BeEmpty();
    }

    [Fact]
    public void HairName_WithLongHairSlotArma_IsDetected()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var armor = NewArmor(mod, "FoxGlove Hair", null, NewArma(mod, BipedObjectFlag.LongHair));

        Scan(mod).Wigs.Should().BeEquivalentTo(new[] { armor.FormKey });
    }

    [Fact]
    public void WigSubstring_WithoutHairSlot_IsRejected()
    {
        // "Wight" contains "wig"; the slot guard must reject the body-slot armor.
        var mod = MutagenFixtures.NewMod("Test.esp");
        NewArmor(mod, "Draugr Wight Armor", "DraugrWightArmor", NewArma(mod, BipedObjectFlag.Body));

        var (wigs, antlers) = Scan(mod);

        wigs.Should().BeEmpty();
        antlers.Should().BeEmpty();
    }

    [Fact]
    public void HairSubstring_OnCircletSlotOnly_IsRejected()
    {
        // "Hairband" jewelry on the circlet slot must not classify as a wig.
        var mod = MutagenFixtures.NewMod("Test.esp");
        NewArmor(mod, "Golden Hairband", null, NewArma(mod, BipedObjectFlag.Circlet));

        Scan(mod).Wigs.Should().BeEmpty();
    }

    [Fact]
    public void EditorIdKeywordOnly_WithoutHairSlot_IsRejected()
    {
        // The wig keyword match is Name-only by spec; an EditorID hit alone
        // (with no display name) does not classify.
        var mod = MutagenFixtures.NewMod("Test.esp");
        NewArmor(mod, null, "RedWig", NewArma(mod, BipedObjectFlag.Hair));

        Scan(mod).Wigs.Should().BeEmpty();
    }

    [Fact]
    public void Antler_ByEditorId_NoSlotGuard()
    {
        var mod = MutagenFixtures.NewMod("FoxGloveAuri.esp");
        var armor = NewArmor(mod, "Forest Crown", "AuriAntlers", NewArma(mod, BipedObjectFlag.Circlet));

        var (wigs, antlers) = Scan(mod);

        antlers.Should().BeEquivalentTo(new[] { armor.FormKey });
        wigs.Should().BeEmpty();
    }

    [Fact]
    public void Antler_ByName_EvenWithNoArmatures()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var armor = NewArmor(mod, "Elk Antlers", null);

        Scan(mod).Antlers.Should().BeEquivalentTo(new[] { armor.FormKey });
    }

    [Fact]
    public void AntlerAndWigKeywords_ClassifiesAsAntler()
    {
        // Antler wins: all its ARMAs get forwarded, not just hair-slot ones.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var armor = NewArmor(mod, "Antler Hair Piece", null, NewArma(mod, BipedObjectFlag.Hair));

        var (wigs, antlers) = Scan(mod);

        antlers.Should().BeEquivalentTo(new[] { armor.FormKey });
        wigs.Should().BeEmpty();
    }

    [Fact]
    public void ArmaInheritedFromMaster_ResolvesThroughFallback()
    {
        var master = MutagenFixtures.NewMod("Master.esp");
        var masterArma = NewArma(master, BipedObjectFlag.Hair);

        var mod = MutagenFixtures.NewMod("Test.esp");
        var armor = mod.Armors.AddNew();
        armor.Name = "Borrowed Wig";
        armor.Armature.Add(masterArma.ToLink());

        // Without the fallback the ARMA is unresolvable -> no detection.
        Scan(mod).Wigs.Should().BeEmpty();

        var (wigs, _) = Scan(mod, fk => fk == masterArma.FormKey ? masterArma : null);
        wigs.Should().BeEquivalentTo(new[] { armor.FormKey });
    }

    [Fact]
    public void GetForwardableArmatures_WigTakesHairSlotsOnly_AntlerTakesAll()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var hairArma = NewArma(mod, BipedObjectFlag.Hair | BipedObjectFlag.LongHair);
        var bodyArma = NewArma(mod, BipedObjectFlag.Body);
        var armor = NewArmor(mod, "Combo Wig", null, hairArma, bodyArma);

        IArmorAddonGetter? Resolve(FormKey fk) =>
            mod.ArmorAddons.FirstOrDefault(a => a.FormKey == fk);

        var asWig = WigDetector.GetForwardableArmatures(armor, isAntler: false, Resolve)
            .Select(l => l.FormKey).ToList();
        var asAntler = WigDetector.GetForwardableArmatures(armor, isAntler: true, Resolve)
            .Select(l => l.FormKey).ToList();

        asWig.Should().BeEquivalentTo(new[] { hairArma.FormKey });
        asAntler.Should().BeEquivalentTo(new[] { hairArma.FormKey, bodyArma.FormKey });
    }
}
