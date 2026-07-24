using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
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

    // ---- Skin-carried (WNAM) wig armatures --------------------------------
    // Policy (user-decided): slot-based AUTO-detection — any hair-slot ARMA
    // reachable from an NPC's WornArmor detects with no keyword requirement
    // (manual "is NOT a wig" designations are the false-positive safety valve).
    // The keyword pass is a plugin-wide net for hair-slot ARMAs whose wearing
    // NPC lives outside the scanned plugins; keyword alone never detects.

    private static Npc NewNpcWithWnam(SkyrimMod mod, Armor wnam, Race? race = null)
    {
        var npc = MutagenFixtures.NewNpc(mod, editorId: "Wearer_" + Guid.NewGuid().ToString("N")[..8]);
        npc.WornArmor.SetTo(wnam);
        if (race != null) npc.Race.SetTo(race);
        return npc;
    }

    [Fact]
    public void WnamHairSlotArma_AutoDetects_WithoutAnyKeyword()
    {
        var mod = MutagenFixtures.NewMod("HPNO.esp");
        var arma = NewArma(mod, BipedObjectFlag.Hair, editorId: "0SkinArma205"); // no wig/hair keyword
        var skin = NewArmor(mod, null, "0Skin205", arma);
        NewNpcWithWnam(mod, skin);

        var r = Scan(mod);

        r.WigArmatures.Should().BeEquivalentTo(new[] { arma.FormKey });
        r.Wigs.Should().BeEmpty("the WNAM source is ARMA-level, not an outfit wig ARMO");
    }

    [Fact]
    public void WnamLongHairSlotArma_AutoDetects()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var arma = NewArma(mod, BipedObjectFlag.LongHair, editorId: "0SkinArmaLong");
        var skin = NewArmor(mod, null, "0SkinLong", arma);
        NewNpcWithWnam(mod, skin);

        Scan(mod).WigArmatures.Should().BeEquivalentTo(new[] { arma.FormKey });
    }

    [Fact]
    public void RaceDefaultSkinAsWnam_IsSkipped()
    {
        // Beast-race manes / custom-race anatomy: a WNAM that IS the NPC's race
        // default skin is the race's body, never a wig.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var arma = NewArma(mod, BipedObjectFlag.Hair, editorId: "ManeArma");
        var skin = NewArmor(mod, null, "RaceSkin", arma);
        var race = mod.Races.AddNew();
        race.EditorID = "CustomBeastRace";
        race.Skin.SetTo(skin);
        NewNpcWithWnam(mod, skin, race);

        Scan(mod).WigArmatures.Should().BeEmpty();
    }

    [Fact]
    public void RaceDefaultSkinArma_IsSkipped_EvenWhenTheNpcRaceIsTemplateInherited()
    {
        // The werewolf-form case found by the HPNO benchmark: the NPC's own Race
        // is unset (template-inherited), so the WNAM==race.Skin check can't
        // fire — but the ARMA itself declares its race, whose default skin
        // carries it. That relationship is authoritative anatomy evidence.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var arma = NewArma(mod, BipedObjectFlag.Hair | BipedObjectFlag.Body, editorId: "NakedTorsoWerewolfBeast");
        var skin = NewArmor(mod, null, "SkinNakedWerewolfBeast", arma);
        var beastRace = mod.Races.AddNew();
        beastRace.EditorID = "WerewolfBeastRace";
        beastRace.Skin.SetTo(skin);
        arma.Race.SetTo(beastRace);

        var npc = MutagenFixtures.NewNpc(mod, editorId: "FarkasWolf");
        npc.WornArmor.SetTo(skin); // Race deliberately unset (template-inherited)

        Scan(mod).WigArmatures.Should().BeEmpty();
    }

    [Fact]
    public void UnreferencedKeywordlessHairSlotArma_IsNotDetected()
    {
        // No NPC WNAM reaches it and it carries no wig/hair keyword — the
        // NPC-scoping is what keeps arbitrary hair-slot ARMAs out.
        var mod = MutagenFixtures.NewMod("Test.esp");
        NewArma(mod, BipedObjectFlag.Hair, editorId: "0Sky205Addon");

        Scan(mod).WigArmatures.Should().BeEmpty();
    }

    [Fact]
    public void UnreferencedHairSlotArma_WithKeyword_DetectsViaKeywordPass()
    {
        // Pass 2: keyword + slot, no NPC required (the wearing NPC may live in
        // a plugin outside this mod).
        var mod = MutagenFixtures.NewMod("Test.esp");
        var byEid = NewArma(mod, BipedObjectFlag.Hair, editorId: "KS_HairAddon_Ponytail");
        var byPath = NewArma(mod, BipedObjectFlag.LongHair, editorId: "0Sky310Addon");
        byPath.WorldModel = new GenderedItem<Model?>(
            new Model { File = @"actors\character\FoxGlove\Wig\22a_1.nif" }, null);
        NewArma(mod, BipedObjectFlag.Hair, editorId: "0Sky311Addon"); // keyword-less control

        var r = Scan(mod);

        r.WigArmatures.Should().BeEquivalentTo(new[] { byEid.FormKey, byPath.FormKey });
    }

    [Fact]
    public void KeywordWithoutHairSlot_IsNotDetected()
    {
        // The slot requirement stays mandatory in the keyword pass too.
        var mod = MutagenFixtures.NewMod("Test.esp");
        NewArma(mod, BipedObjectFlag.Body, editorId: "BodyHairAddon");

        Scan(mod).WigArmatures.Should().BeEmpty();
    }

    [Fact]
    public void AntlerClassifiedArma_IsExcludedFromWigArmatures()
    {
        // An ARMA the antler flow owns must not double-classify, even when an
        // NPC's WNAM carries it on a hair slot.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var arma = NewArma(mod, BipedObjectFlag.Hair, editorId: "AntlerHairAddon");
        var skin = NewArmor(mod, null, "Skin", arma);
        NewNpcWithWnam(mod, skin);

        var r = Scan(mod);

        r.AntlerArmatures.Should().Contain(arma.FormKey);
        r.WigArmatures.Should().BeEmpty();
    }

    [Fact]
    public void MasterInheritedWnamAndArma_ResolveThroughFallbacks()
    {
        var master = MutagenFixtures.NewMod("Master.esp");
        var masterArma = NewArma(master, BipedObjectFlag.Hair, editorId: "0MasterSkinArma");
        var masterSkin = NewArmor(master, null, "0MasterSkin", masterArma);

        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, editorId: "Wearer");
        npc.WornArmor.SetTo(masterSkin);

        // Without the armor fallback the WNAM link is unresolvable → no detection.
        Scan(mod).WigArmatures.Should().BeEmpty();

        WigDetector.Scan(new[] { mod },
                fallbackArmaResolver: fk => fk == masterArma.FormKey ? masterArma : null,
                fallbackArmorResolver: fk => fk == masterSkin.FormKey ? masterSkin : null)
            .WigArmatures.Should().BeEquivalentTo(new[] { masterArma.FormKey });
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

    // ---- Per-NPC wig-source association map (NpcWigSources) ---------------
    // The scan's last pass records which wig-relevant records each NPC
    // actually carries, so the mugshot tile's "has wig" badge is a pure
    // persisted-data lookup (association here; CLASSIFICATION stays with the
    // detection sets + manual designations at read time).

    private static Outfit NewOutfit(SkyrimMod mod, params Armor[] items)
    {
        var outfit = mod.Outfits.AddNew();
        outfit.Items = new();
        foreach (var item in items)
        {
            outfit.Items.Add(new FormLink<IOutfitTargetGetter>(item.FormKey));
        }
        return outfit;
    }

    [Fact]
    public void NpcWigSources_OutfitWig_IsAssociatedToWearer()
    {
        var mod = MutagenFixtures.NewMod("FoxGloveAuri.esp");
        var wig = NewArmor(mod, "Auri Red Wig", "FoxGloveWigArmo", NewArma(mod, BipedObjectFlag.Hair));
        var dress = NewArmor(mod, "Forest Dress", "ForestDress", NewArma(mod, BipedObjectFlag.Body));
        var outfit = NewOutfit(mod, wig, dress);
        var npc = MutagenFixtures.NewNpc(mod, editorId: "Auri");
        npc.DefaultOutfit.SetTo(outfit);

        var r = Scan(mod);

        r.NpcWigSources.Should().ContainKey(npc.FormKey);
        var entries = r.NpcWigSources[npc.FormKey];
        entries.Should().ContainSingle();
        entries[0].Kind.Should().Be(NpcWigSourceKind.Outfit);
        entries[0].RecordFormKey.Should().Be(wig.FormKey);
        entries[0].EditorId.Should().Be("FoxGloveWigArmo");
    }

    [Fact]
    public void NpcWigSources_WnamHairArma_IsStoredAsWornArmorCandidate()
    {
        // The HPNO pattern: bald FaceGen + skin-carried hair ARMA. The map
        // stores the association; effectiveness is decided at read time.
        var mod = MutagenFixtures.NewMod("HPNO.esp");
        var hairArma = NewArma(mod, BipedObjectFlag.Hair, editorId: "0SkinArma205");
        var bodyArma = NewArma(mod, BipedObjectFlag.Body, editorId: "0SkinArmaBody");
        var skin = NewArmor(mod, null, "0Skin205", hairArma, bodyArma);
        var npc = NewNpcWithWnam(mod, skin);

        var r = Scan(mod);

        r.NpcWigSources.Should().ContainKey(npc.FormKey);
        var entries = r.NpcWigSources[npc.FormKey];
        entries.Should().ContainSingle("only the hair-slot ARMA is a wig candidate");
        entries[0].Kind.Should().Be(NpcWigSourceKind.WornArmor);
        entries[0].RecordFormKey.Should().Be(hairArma.FormKey);
        entries[0].EditorId.Should().Be("0SkinArma205");
    }

    [Fact]
    public void NpcWigSources_RaceDefaultSkinWnam_StoresNothing()
    {
        // Race anatomy is not a wig: an NPC whose WNAM IS its race's default
        // skin stores no WornArmor candidates (same guard as detection).
        var mod = MutagenFixtures.NewMod("Test.esp");
        var arma = NewArma(mod, BipedObjectFlag.Hair, editorId: "ManeArma");
        var skin = NewArmor(mod, null, "RaceSkin", arma);
        var race = mod.Races.AddNew();
        race.EditorID = "CustomBeastRace";
        race.Skin.SetTo(skin);
        NewNpcWithWnam(mod, skin, race);

        Scan(mod).NpcWigSources.Should().BeEmpty();
    }

    [Fact]
    public void NpcWigSources_NpcWithoutWigRecords_HasNoEntry()
    {
        // Sparse map: a plain NPC (no WNAM, wigless outfit) stores nothing.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var dress = NewArmor(mod, "Forest Dress", null, NewArma(mod, BipedObjectFlag.Body));
        var npc = MutagenFixtures.NewNpc(mod, editorId: "Plain");
        npc.DefaultOutfit.SetTo(NewOutfit(mod, dress));

        Scan(mod).NpcWigSources.Should().BeEmpty();
    }

    [Fact]
    public void NpcWigSources_LaterPluginOverride_ReplacesEntries()
    {
        // Later plugin wins: an override that swaps the outfit for a wigless
        // one clears the earlier association.
        var mod1 = MutagenFixtures.NewMod("Base.esp");
        var wig = NewArmor(mod1, "Red Wig", "WigArmo", NewArma(mod1, BipedObjectFlag.Hair));
        var npc = MutagenFixtures.NewNpc(mod1, editorId: "Wearer");
        npc.DefaultOutfit.SetTo(NewOutfit(mod1, wig));

        var mod2 = MutagenFixtures.NewMod("Patch.esp");
        var dress = NewArmor(mod2, "Forest Dress", null, NewArma(mod2, BipedObjectFlag.Body));
        var npcOverride = mod2.Npcs.GetOrAddAsOverride(npc);
        npcOverride.DefaultOutfit.SetTo(NewOutfit(mod2, dress));

        WigDetector.Scan(new[] { mod1 }).NpcWigSources.Should().ContainKey(npc.FormKey);
        WigDetector.Scan(new ISkyrimModGetter[] { mod1, mod2 }).NpcWigSources.Should().BeEmpty();
    }
}
