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
/// Pure static Mutagen-record helpers on <see cref="Auxilliary"/> exercised against small
/// in-memory Skyrim records (no env, no game install, no clock/network). Covers gender/flag
/// predicates, templated-NPC validity, the NPC grouping key (sort + build), the log-string /
/// name fallbacks, and the appearance-record-type set.
/// </summary>
public class AuxilliaryRecordTests
{
    // ---- IsFemale / GetGender ---------------------------------------------------------------

    [Fact]
    public void IsFemale_FemaleFlagSet_IsTrue()
    {
        var mod = MutagenFixtures.NewMod("Gender.esp");
        var npc = MutagenFixtures.NewNpc(mod, female: true);
        Auxilliary.IsFemale(npc).Should().BeTrue();
    }

    [Fact]
    public void IsFemale_FemaleFlagUnset_IsFalse()
    {
        var mod = MutagenFixtures.NewMod("Gender.esp");
        var npc = MutagenFixtures.NewNpc(mod, female: false);
        Auxilliary.IsFemale(npc).Should().BeFalse();
    }

    [Fact]
    public void IsFemale_OnlyChecksFemaleFlag_NotOtherConfigFlags()
    {
        // Setting an unrelated configuration flag must not flip IsFemale.
        var mod = MutagenFixtures.NewMod("Gender.esp");
        var npc = MutagenFixtures.NewNpc(mod, female: false);
        npc.Configuration.Flags |= NpcConfiguration.Flag.Essential;
        Auxilliary.IsFemale(npc).Should().BeFalse();

        npc.Configuration.Flags |= NpcConfiguration.Flag.Female;
        Auxilliary.IsFemale(npc).Should().BeTrue();
    }

    [Theory]
    [InlineData(true, Gender.Female)]
    [InlineData(false, Gender.Male)]
    public void GetGender_MapsFemaleFlagToGender(bool female, Gender expected)
    {
        var mod = MutagenFixtures.NewMod("Gender.esp");
        var npc = MutagenFixtures.NewNpc(mod, female: female);
        Auxilliary.GetGender(npc).Should().Be(expected);
    }

    // ---- HasTraitsFlag ----------------------------------------------------------------------

    [Fact]
    public void HasTraitsFlag_TraitsTemplateFlagSet_IsTrue()
    {
        var mod = MutagenFixtures.NewMod("Traits.esp");
        var npc = MutagenFixtures.NewNpc(mod, traitsTemplate: true);
        Auxilliary.HasTraitsFlag(npc).Should().BeTrue();
    }

    [Fact]
    public void HasTraitsFlag_NoTemplateFlags_IsFalse()
    {
        var mod = MutagenFixtures.NewMod("Traits.esp");
        var npc = MutagenFixtures.NewNpc(mod, traitsTemplate: false);
        Auxilliary.HasTraitsFlag(npc).Should().BeFalse();
    }

    [Fact]
    public void HasTraitsFlag_DifferentTemplateFlagOnly_IsFalse()
    {
        // A template flag other than Traits must not count as "has traits".
        var mod = MutagenFixtures.NewMod("Traits.esp");
        var npc = MutagenFixtures.NewNpc(mod);
        npc.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Stats;
        Auxilliary.HasTraitsFlag(npc).Should().BeFalse();
    }

    // ---- IsValidTemplatedNpc (all flag/template combos) -------------------------------------

    [Fact]
    public void IsValidTemplatedNpc_NullNpc_IsFalse()
    {
        Auxilliary.IsValidTemplatedNpc(null).Should().BeFalse();
    }

    [Fact]
    public void IsValidTemplatedNpc_TraitsFlagAndTemplateSet_IsTrue()
    {
        var mod = MutagenFixtures.NewMod("Tmpl.esp");
        var templateTarget = MutagenFixtures.NewNpc(mod, editorId: "TemplateTarget");
        var npc = MutagenFixtures.NewNpc(mod, traitsTemplate: true, template: templateTarget);
        Auxilliary.IsValidTemplatedNpc(npc).Should().BeTrue();
    }

    [Fact]
    public void IsValidTemplatedNpc_TraitsFlagButNullTemplate_IsFalse()
    {
        // Traits flag present but the template link is null -> not a valid templated NPC.
        var mod = MutagenFixtures.NewMod("Tmpl.esp");
        var npc = MutagenFixtures.NewNpc(mod, traitsTemplate: true);
        npc.Template.IsNull.Should().BeTrue("no template was assigned");
        Auxilliary.IsValidTemplatedNpc(npc).Should().BeFalse();
    }

    [Fact]
    public void IsValidTemplatedNpc_TemplateSetButNoTraitsFlag_IsFalse()
    {
        // Template link present but Traits flag missing -> the template doesn't drive appearance.
        var mod = MutagenFixtures.NewMod("Tmpl.esp");
        var templateTarget = MutagenFixtures.NewNpc(mod, editorId: "TemplateTarget");
        var npc = MutagenFixtures.NewNpc(mod, traitsTemplate: false, template: templateTarget);
        Auxilliary.IsValidTemplatedNpc(npc).Should().BeFalse();
    }

    [Fact]
    public void IsValidTemplatedNpc_NoFlagsNoTemplate_IsFalse()
    {
        var mod = MutagenFixtures.NewMod("Tmpl.esp");
        var npc = MutagenFixtures.NewNpc(mod);
        Auxilliary.IsValidTemplatedNpc(npc).Should().BeFalse();
    }

    // ---- BuildNpcGroupingKey ----------------------------------------------------------------

    [Fact]
    public void BuildNpcGroupingKey_BareNpc_AllLinkFieldsEmpty()
    {
        // No race/worn-armor/hair-color/head-parts set -> every string field is empty,
        // IsFemale reflects the (unset) flag.
        var mod = MutagenFixtures.NewMod("Key.esp");
        var npc = MutagenFixtures.NewNpc(mod);

        var key = Auxilliary.BuildNpcGroupingKey(npc);

        key.IsFemale.Should().BeFalse();
        key.Race.Should().BeEmpty();
        key.WornArmor.Should().BeEmpty();
        key.HeadPartsHash.Should().BeEmpty();
        key.HairColor.Should().BeEmpty();
    }

    [Fact]
    public void BuildNpcGroupingKey_PopulatedFields_UsesFormKeyStrings()
    {
        var mod = MutagenFixtures.NewMod("Key.esp");
        var race = MutagenFixtures.NewRace(mod, "TestRace");
        var npc = MutagenFixtures.NewNpc(mod, female: true, race: race);

        var key = Auxilliary.BuildNpcGroupingKey(npc);

        key.IsFemale.Should().BeTrue();
        key.Race.Should().Be(race.FormKey.ToString());
        // Unset links remain empty even when the race is populated.
        key.WornArmor.Should().BeEmpty();
        key.HairColor.Should().BeEmpty();
        key.HeadPartsHash.Should().BeEmpty();
    }

    [Fact]
    public void BuildNpcGroupingKey_HeadPartsHash_IsOrderIndependent()
    {
        // The same set of head parts in different orders must yield the same hash.
        var mod = MutagenFixtures.NewMod("Key.esp");
        var hp1 = mod.HeadParts.AddNew();
        var hp2 = mod.HeadParts.AddNew();
        var hp3 = mod.HeadParts.AddNew();

        var npcA = MutagenFixtures.NewNpc(mod, editorId: "A");
        npcA.HeadParts.Add(hp1.ToLink());
        npcA.HeadParts.Add(hp2.ToLink());
        npcA.HeadParts.Add(hp3.ToLink());

        var npcB = MutagenFixtures.NewNpc(mod, editorId: "B");
        npcB.HeadParts.Add(hp3.ToLink());
        npcB.HeadParts.Add(hp1.ToLink());
        npcB.HeadParts.Add(hp2.ToLink());

        var keyA = Auxilliary.BuildNpcGroupingKey(npcA);
        var keyB = Auxilliary.BuildNpcGroupingKey(npcB);

        keyA.HeadPartsHash.Should().NotBeEmpty();
        keyA.HeadPartsHash.Should().Be(keyB.HeadPartsHash, "head-part order must not change the hash");
    }

    [Fact]
    public void BuildNpcGroupingKey_DifferentHeadPartSets_ProduceDifferentHashes()
    {
        var mod = MutagenFixtures.NewMod("Key.esp");
        var hp1 = mod.HeadParts.AddNew();
        var hp2 = mod.HeadParts.AddNew();

        var npcA = MutagenFixtures.NewNpc(mod, editorId: "A");
        npcA.HeadParts.Add(hp1.ToLink());

        var npcB = MutagenFixtures.NewNpc(mod, editorId: "B");
        npcB.HeadParts.Add(hp1.ToLink());
        npcB.HeadParts.Add(hp2.ToLink());

        var keyA = Auxilliary.BuildNpcGroupingKey(npcA);
        var keyB = Auxilliary.BuildNpcGroupingKey(npcB);

        keyA.HeadPartsHash.Should().NotBe(keyB.HeadPartsHash);
    }

    [Fact]
    public void BuildNpcGroupingKey_NullHeadPartLinks_AreSkipped()
    {
        // A null head-part link contributes nothing; the resulting hash matches the
        // single real part as if the null entry were absent.
        var mod = MutagenFixtures.NewMod("Key.esp");
        var hp1 = mod.HeadParts.AddNew();

        var withNull = MutagenFixtures.NewNpc(mod, editorId: "WithNull");
        withNull.HeadParts.Add(hp1.ToLink());
        withNull.HeadParts.Add(new FormLink<IHeadPartGetter>(FormKey.Null));

        var withoutNull = MutagenFixtures.NewNpc(mod, editorId: "WithoutNull");
        withoutNull.HeadParts.Add(hp1.ToLink());

        Auxilliary.BuildNpcGroupingKey(withNull).HeadPartsHash
            .Should().Be(Auxilliary.BuildNpcGroupingKey(withoutNull).HeadPartsHash);
    }

    [Fact]
    public void BuildNpcGroupingKey_OnlyNullHeadPartLinks_HashIsEmpty()
    {
        var mod = MutagenFixtures.NewMod("Key.esp");
        var npc = MutagenFixtures.NewNpc(mod);
        npc.HeadParts.Add(new FormLink<IHeadPartGetter>(FormKey.Null));

        Auxilliary.BuildNpcGroupingKey(npc).HeadPartsHash.Should().BeEmpty();
    }

    // ---- NpcGroupingKey.CompareTo -----------------------------------------------------------

    private static Auxilliary.NpcGroupingKey Key(
        bool female = false, string race = "", string wornArmor = "",
        string headParts = "", string hairColor = "") =>
        new(female, race, wornArmor, headParts, hairColor);

    [Fact]
    public void NpcGroupingKey_CompareTo_EqualKeys_ReturnZero()
    {
        var a = Key(true, "r", "w", "h", "c");
        var b = Key(true, "r", "w", "h", "c");
        a.CompareTo(b).Should().Be(0);
    }

    [Fact]
    public void NpcGroupingKey_CompareTo_IsFemaleIsPrimaryField()
    {
        // false (Male) sorts before true (Female): bool.CompareTo(false, true) < 0.
        var male = Key(female: false, race: "zzz");
        var female = Key(female: true, race: "aaa");
        male.CompareTo(female).Should().BeLessThan(0, "the IsFemale field dominates all later fields");
        female.CompareTo(male).Should().BeGreaterThan(0);
    }

    [Fact]
    public void NpcGroupingKey_CompareTo_FallsThroughFieldsInOrder()
    {
        // Equal IsFemale + Race; differ on WornArmor -> WornArmor decides.
        var a = Key(false, "race", "armorA", "zzz", "zzz");
        var b = Key(false, "race", "armorB", "aaa", "aaa");
        a.CompareTo(b).Should().BeLessThan(0);
    }

    [Fact]
    public void NpcGroupingKey_CompareTo_HeadPartsHashTieBrokenByHairColor()
    {
        var a = Key(false, "race", "armor", "head", "colorA");
        var b = Key(false, "race", "armor", "head", "colorB");
        a.CompareTo(b).Should().BeLessThan(0);
    }

    [Fact]
    public void NpcGroupingKey_CompareTo_UsesOrdinalComparison()
    {
        // Ordinal: uppercase 'B' (0x42) sorts before lowercase 'a' (0x61).
        var upper = Key(race: "B");
        var lower = Key(race: "a");
        upper.CompareTo(lower).Should().BeLessThan(0);
    }

    [Fact]
    public void NpcGroupingKey_SortingClustersByKey()
    {
        var k1 = Key(false, "a");
        var k2 = Key(false, "b");
        var k3 = Key(true, "a");
        var list = new List<Auxilliary.NpcGroupingKey> { k3, k2, k1 };
        list.Sort();
        list.Should().ContainInOrder(k1, k2, k3);
    }

    // ---- GetLogString -----------------------------------------------------------------------

    [Fact]
    public void GetLogString_NamedRecord_ReturnsNameByDefault()
    {
        var mod = MutagenFixtures.NewMod("Log.esp");
        var npc = MutagenFixtures.NewNpc(mod, editorId: "MyEditorId", name: "Display Name");

        Auxilliary.GetLogString(npc, language: null).Should().Be("Display Name");
    }

    [Fact]
    public void GetLogString_NoName_FallsBackToEditorId()
    {
        var mod = MutagenFixtures.NewMod("Log.esp");
        var npc = MutagenFixtures.NewNpc(mod, editorId: "MyEditorId");

        Auxilliary.GetLogString(npc, language: null).Should().Be("MyEditorId | ");
    }

    [Fact]
    public void GetLogString_NoNameNoEditorId_FallsBackToFormKey()
    {
        var mod = MutagenFixtures.NewMod("Log.esp");
        var npc = MutagenFixtures.NewNpc(mod);

        Auxilliary.GetLogString(npc, language: null).Should().Be(npc.FormKey.ToString());
    }

    [Fact]
    public void GetLogString_FullString_ConcatenatesNameEditorIdAndFormKey()
    {
        var mod = MutagenFixtures.NewMod("Log.esp");
        var npc = MutagenFixtures.NewNpc(mod, editorId: "MyEditorId", name: "Display Name");

        var expected = $"Display Name | MyEditorId | {npc.FormKey}";
        Auxilliary.GetLogString(npc, language: null, fullString: true).Should().Be(expected);
    }

    [Fact]
    public void GetLogString_FullString_NoName_IncludesEditorIdAndFormKey()
    {
        var mod = MutagenFixtures.NewMod("Log.esp");
        var npc = MutagenFixtures.NewNpc(mod, editorId: "MyEditorId");

        // An NPC is ITranslatedNamedGetter, so with no Name set the name slot is emitted as
        // empty followed by the " | " separator -> the string begins with " | " before the EditorID.
        var expected = $" | MyEditorId | {npc.FormKey}";
        Auxilliary.GetLogString(npc, language: null, fullString: true).Should().Be(expected);
    }

    [Fact]
    public void GetLogString_NonNamedRecord_UsesEditorIdThenFormKey()
    {
        // A Race is an IMajorRecordGetter but not ITranslatedNamedGetter -> name branch skipped.
        var mod = MutagenFixtures.NewMod("Log.esp");
        var race = MutagenFixtures.NewRace(mod, "TestRace");

        Auxilliary.GetLogString(race, language: null).Should().Be("TestRace | ");
    }

    [Fact]
    public void GetLogString_NonNamedRecordNoEditorId_UsesFormKey()
    {
        var mod = MutagenFixtures.NewMod("Log.esp");
        var race = MutagenFixtures.NewRace(mod);

        Auxilliary.GetLogString(race, language: null).Should().Be(race.FormKey.ToString());
    }

    // ---- TryGetName -------------------------------------------------------------------------

    [Fact]
    public void TryGetName_NameSet_ReturnsTrueAndName()
    {
        var mod = MutagenFixtures.NewMod("Name.esp");
        var npc = MutagenFixtures.NewNpc(mod, name: "Lydia");

        var ok = Auxilliary.TryGetName(npc, language: null, fixGarbled: false, out var name);

        ok.Should().BeTrue();
        name.Should().Be("Lydia");
    }

    [Fact]
    public void TryGetName_NameNull_ReturnsFalseAndEmpty()
    {
        var mod = MutagenFixtures.NewMod("Name.esp");
        var npc = MutagenFixtures.NewNpc(mod); // no name assigned -> Name getter null

        npc.Name.Should().BeNull("the fixture left the name unset");
        var ok = Auxilliary.TryGetName(npc, language: null, fixGarbled: false, out var name);

        ok.Should().BeFalse();
        name.Should().BeEmpty();
    }

    [Fact]
    public void TryGetName_PlainAsciiName_FixGarbledLeavesItUnchanged()
    {
        // FixMojibake must not corrupt well-formed ASCII names.
        var mod = MutagenFixtures.NewMod("Name.esp");
        var npc = MutagenFixtures.NewNpc(mod, name: "Brunhilda");

        var ok = Auxilliary.TryGetName(npc, language: null, fixGarbled: true, out var name);

        ok.Should().BeTrue();
        name.Should().Be("Brunhilda");
    }

    // ---- AppearanceRecordTypes --------------------------------------------------------------

    [Fact]
    public void AppearanceRecordTypes_HasExactlyTheEightAppearanceGetters()
    {
        Auxilliary.AppearanceRecordTypes.Should().HaveCount(8);
        Auxilliary.AppearanceRecordTypes.Should().BeEquivalentTo(new[]
        {
            typeof(INpcGetter),
            typeof(IArmorGetter),
            typeof(IArmorAddonGetter),
            typeof(ITextureSetGetter),
            typeof(IHeadPartGetter),
            typeof(IHairGetter),
            typeof(IColorRecordGetter),
            typeof(IEyesGetter),
        });
    }

    [Fact]
    public void AppearanceRecordTypes_ContainsNpcGetter_AndExcludesWeapon()
    {
        Auxilliary.AppearanceRecordTypes.Should().Contain(typeof(INpcGetter));
        Auxilliary.AppearanceRecordTypes.Should().NotContain(typeof(IWeaponGetter));
    }

    // ---- Gender enum ------------------------------------------------------------------------

    [Fact]
    public void Gender_HasFemaleFirstThenMale()
    {
        ((int)Gender.Female).Should().Be(0);
        ((int)Gender.Male).Should().Be(1);
        Enum.GetValues<Gender>().Should().HaveCount(2);
    }
}
