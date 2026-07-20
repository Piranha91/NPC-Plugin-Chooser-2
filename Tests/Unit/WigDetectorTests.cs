using System;
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
/// substring hits ("Draugr Wight Armor", "Hairband" circlets). Antlers are
/// keyword-only ("antler" in an ARMO EditorID/Name, an ARMA EditorID, or a
/// HeadPart Name/EditorID — the three sources antler Remove must reach). No
/// slot guard (antler slots aren't standardized).
/// </summary>
public class WigDetectorTests
{
    private static ArmorAddon NewArma(SkyrimMod mod, BipedObjectFlag slots, string? editorId = null)
    {
        var arma = mod.ArmorAddons.AddNew();
        arma.BodyTemplate = new BodyTemplate { FirstPersonFlags = slots };
        if (editorId != null) arma.EditorID = editorId;
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

    private static WigDetector.WigScanResult Scan(SkyrimMod mod,
        Func<FormKey, IArmorAddonGetter?>? fallback = null)
        => WigDetector.Scan(new[] { mod }, fallback);

    [Fact]
    public void WigName_WithHairSlotArma_IsDetected()
    {
        var mod = MutagenFixtures.NewMod("FoxGloveAuri.esp");
        var armor = NewArmor(mod, "Auri Red Wig", "AuriWig", NewArma(mod, BipedObjectFlag.Hair));

        var r = Scan(mod);

        r.Wigs.Should().BeEquivalentTo(new[] { armor.FormKey });
        r.AntlerArmors.Should().BeEmpty();
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

        var r = Scan(mod);

        r.Wigs.Should().BeEmpty();
        r.AntlerArmors.Should().BeEmpty();
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
        var arma = NewArma(mod, BipedObjectFlag.Circlet);
        var armor = NewArmor(mod, "Forest Crown", "AuriAntlers", arma);

        var r = Scan(mod);

        r.AntlerArmors.Should().BeEquivalentTo(new[] { armor.FormKey });
        r.Wigs.Should().BeEmpty();
        // The antler ARMO's own addons fold into the ARMA set (source 2 matching:
        // a WornArmor that references them directly is then caught).
        r.AntlerArmatures.Should().Contain(arma.FormKey);
    }

    [Fact]
    public void Antler_ByName_EvenWithNoArmatures()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var armor = NewArmor(mod, "Elk Antlers", null);

        Scan(mod).AntlerArmors.Should().BeEquivalentTo(new[] { armor.FormKey });
    }

    [Fact]
    public void AntlerAndWigKeywords_ClassifiesAsAntler()
    {
        // Antler wins: all its ARMAs get forwarded, not just hair-slot ones.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var armor = NewArmor(mod, "Antler Hair Piece", null, NewArma(mod, BipedObjectFlag.Hair));

        var r = Scan(mod);

        r.AntlerArmors.Should().BeEquivalentTo(new[] { armor.FormKey });
        r.Wigs.Should().BeEmpty();
    }

    [Fact]
    public void AntlerArmature_ByEditorId_IsDetected_ForWornArmorBakedAntlers()
    {
        // Source 2: an antler ArmorAddon baked directly into a WornArmor. The ARMA
        // names itself even though no antler ARMO exists.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var arma = NewArma(mod, BipedObjectFlag.Circlet, editorId: "CustomAntlerAddon");

        var r = Scan(mod);

        r.AntlerArmatures.Should().Contain(arma.FormKey);
        r.AntlerArmors.Should().BeEmpty();
        r.Wigs.Should().BeEmpty();
    }

    [Fact]
    public void AntlerHeadPart_ByNameOrEditorId_IsDetected()
    {
        // Source 3: an antler head part baked into the FaceGen.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var byName = mod.HeadParts.AddNew();
        byName.Name = "Great Antlers";
        var byEid = mod.HeadParts.AddNew();
        byEid.EditorID = "CotG_AntlerCrown";
        var unrelated = mod.HeadParts.AddNew();
        unrelated.EditorID = "PlainScalp";

        var r = Scan(mod);

        r.AntlerHeadParts.Should().BeEquivalentTo(new[] { byName.FormKey, byEid.FormKey });
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

        Scan(mod, fk => fk == masterArma.FormKey ? masterArma : null).Wigs
            .Should().BeEquivalentTo(new[] { armor.FormKey });
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
