using System;
using System.Collections.Generic;
using System.Linq;
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
/// The diagnostics that turn Mutagen's opaque "A referenced mod was not present on the load
/// order being sorted against" save failure into something actionable. Mutagen's
/// <c>EnumerateFormLinks</c> flattens a record to bare FormKeys with no field names, so the
/// patcher names the offending fields two ways:
///
/// <list type="bullet">
/// <item><c>EnumerateNamedAppearanceLinks</c> — the exact set of links the appearance copy
/// writes, used at copy time so the failing NPC is reported while it is being patched.</item>
/// <item><c>MapFieldNamesForLinks</c> — a reflection walk over an arbitrary output record,
/// used at save time to say WHICH field on the offending record dangles.</item>
/// </list>
///
/// Both are failure-path-only and must never throw; the walk degrades to "(field unknown)".
/// </summary>
public class PatcherDanglingMasterDiagnosticsTests
{
    private static IReadOnlyList<(string Field, FormKey Key)> NamedAppearanceLinks(INpcGetter npc, bool includeOutfit)
        => Reflect.InvokeStatic<IEnumerable<(string Field, FormKey Key)>>(
            typeof(Patcher), "EnumerateNamedAppearanceLinks", npc, includeOutfit)!.ToList();

    private static Dictionary<FormKey, List<string>> FieldNames(IMajorRecordGetter record, params FormKey[] wanted)
        => Reflect.InvokeStatic<Dictionary<FormKey, List<string>>>(
            typeof(Patcher), "MapFieldNamesForLinks", record, new HashSet<FormKey>(wanted))!;

    [Fact]
    public void NamedAppearanceLinks_LabelEveryCopiedField_AndIndexHeadParts()
    {
        var mod = MutagenFixtures.NewMod("Named.esp");
        var race = MutagenFixtures.NewRace(mod, "TestRace");
        var armor = mod.Armors.AddNew();
        var headPart0 = mod.HeadParts.AddNew();
        var headPart1 = mod.HeadParts.AddNew();
        var hairColor = mod.Colors.AddNew();
        var headTexture = mod.TextureSets.AddNew();
        var outfit = mod.Outfits.AddNew();

        var npc = MutagenFixtures.NewNpc(mod, "TestNpc", race: race);
        npc.WornArmor.SetTo(armor);
        npc.HeadParts.Add(headPart0.ToLink());
        npc.HeadParts.Add(headPart1.ToLink());
        npc.HairColor.SetTo(hairColor);
        npc.HeadTexture.SetTo(headTexture);
        npc.DefaultOutfit.SetTo(outfit);

        var withOutfit = NamedAppearanceLinks(npc, includeOutfit: true);

        withOutfit.Should().Contain(("Race", race.FormKey));
        withOutfit.Should().Contain(("WornArmor(skin)", armor.FormKey));
        withOutfit.Should().Contain(("HeadTexture", headTexture.FormKey));
        withOutfit.Should().Contain(("HairColor", hairColor.FormKey));
        withOutfit.Should().Contain(("DefaultOutfit", outfit.FormKey));

        // Head parts are indexed so a specific offending entry can be pointed at.
        withOutfit.Should().Contain(("HeadParts[0]", headPart0.FormKey));
        withOutfit.Should().Contain(("HeadParts[1]", headPart1.FormKey));

        // Same gating as GetAppearanceFormLinks: the outfit is excluded when not copied.
        NamedAppearanceLinks(npc, includeOutfit: false)
            .Select(l => l.Key).Should().NotContain(outfit.FormKey);
    }

    [Fact]
    public void NamedAppearanceLinks_SkipUnsetFields()
    {
        var mod = MutagenFixtures.NewMod("Sparse.esp");
        var race = MutagenFixtures.NewRace(mod, "TestRace");
        var npc = MutagenFixtures.NewNpc(mod, "Sparse", race: race);

        var links = NamedAppearanceLinks(npc, includeOutfit: true);

        links.Should().ContainSingle().Which.Should().Be(("Race", race.FormKey));
        links.Select(l => l.Key).Should().NotContain(FormKey.Null);
    }

    [Fact]
    public void FieldNames_NameTheNpcFieldHoldingADanglingLink()
    {
        // The real-world shape: an NPC patched from an appearance mod whose Race and head
        // parts live in a plugin that is not in the load order.
        var missing = MutagenFixtures.Mk("Project ja-Kha'jay- Khajiit Diversity.esp");
        var danglingRace = FormKey.Factory($"0008C4:{missing.FileName}");
        var danglingHeadPart = FormKey.Factory($"0008A0:{missing.FileName}");

        var mod = MutagenFixtures.NewMod("Output.esp");
        var npc = MutagenFixtures.NewNpc(mod, "DA05Hunter11_04Melee_Ahjisi");
        npc.Race.SetTo(danglingRace);
        npc.HeadParts.Add(new FormLink<IHeadPartGetter>(danglingHeadPart));

        var names = FieldNames(npc, danglingRace, danglingHeadPart);

        names.Should().ContainKey(danglingRace);
        names[danglingRace].Should().Contain("Race");

        names.Should().ContainKey(danglingHeadPart);
        names[danglingHeadPart].Should().Contain("HeadParts[0]");
    }

    [Fact]
    public void FieldNames_NameNestedFieldsOnNonNpcRecords()
    {
        // Non-NPC records dangle too (an armor merged in as an appearance dependency whose
        // armature points at a missing plugin). The link sits one level down, inside a list.
        var missing = MutagenFixtures.Mk("MissingDependency.esp");
        var danglingArmature = FormKey.Factory($"000801:{missing.FileName}");

        var mod = MutagenFixtures.NewMod("Output2.esp");
        var armor = mod.Armors.AddNew();
        armor.EditorID = "SomeMergedArmor";
        armor.Armature.Add(new FormLink<IArmorAddonGetter>(danglingArmature));

        var names = FieldNames(armor, danglingArmature);

        names.Should().ContainKey(danglingArmature);
        names[danglingArmature].Should().Contain(n => n.StartsWith("Armature", StringComparison.Ordinal));
    }

    [Fact]
    public void FieldNames_IgnoreLinksThatWereNotAskedAbout()
    {
        var mod = MutagenFixtures.NewMod("Output3.esp");
        var race = MutagenFixtures.NewRace(mod, "PresentRace");
        var npc = MutagenFixtures.NewNpc(mod, "Fine", race: race);

        // Nothing dangles, so nothing is named — the caller prints no reference lines.
        FieldNames(npc).Should().BeEmpty();
        FieldNames(npc, FormKey.Factory("000ABC:Unrelated.esp")).Should().BeEmpty();
    }

    [Fact]
    public void FieldNames_ReturnEmptyRatherThanThrowOnAnEmptyRecord()
    {
        // Guard the contract that matters most: this runs inside the catch block of a save
        // failure, so it must degrade to "(field unknown)" instead of masking the real error.
        var mod = MutagenFixtures.NewMod("Output4.esp");
        var npc = mod.Npcs.AddNew();

        Action act = () => FieldNames(npc, FormKey.Factory("000123:Missing.esp"));

        act.Should().NotThrow();
    }
}
