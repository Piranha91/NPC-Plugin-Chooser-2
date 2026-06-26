using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="NpcDisplayData"/> — the lightweight display projection of an
/// <see cref="INpcGetter"/>. Pure mapping logic, no environment. Tests build real
/// in-memory Mutagen NPC records (not mocks) and assert the mapped fields, including
/// the <see cref="Auxilliary.IsValidTemplatedNpc"/> / <see cref="Auxilliary.GetGender"/>
/// derivations that <see cref="NpcDisplayData.FromGetter"/> delegates to.
/// </summary>
public class NpcDisplayDataTests
{
    // ---- FromFormKey: placeholder/defaults ---------------------------------

    [Fact]
    public void FromFormKey_SetsFormKeyAndMarksNotInLoadOrder()
    {
        var fk = FormKey.Factory("001234:Some.esp");

        var data = NpcDisplayData.FromFormKey(fk);

        data.FormKey.Should().Be(fk);
        data.IsInLoadOrder.Should().BeFalse();
    }

    [Fact]
    public void FromFormKey_LeavesAllOtherFieldsAtDefault()
    {
        var data = NpcDisplayData.FromFormKey(FormKey.Factory("00ABCD:Some.esp"));

        data.Name.Should().BeNull();
        data.EditorID.Should().BeNull();
        data.IsTemplateUser.Should().BeFalse();
        data.TemplateFormKey.Should().Be(FormKey.Null);
        data.IsUnique.Should().BeFalse();
        // Gender enum default ordinal is 0 == Female (see Auxilliary.Gender).
        data.Gender.Should().Be(Gender.Female);
    }

    [Fact]
    public void FromFormKey_AcceptsFormKeyNull()
    {
        var data = NpcDisplayData.FromFormKey(FormKey.Null);

        data.FormKey.Should().Be(FormKey.Null);
        data.IsInLoadOrder.Should().BeFalse();
    }

    // ---- FromGetter: core field mapping ------------------------------------

    [Fact]
    public void FromGetter_MapsFormKeyNameEditorIdAndInLoadOrder()
    {
        var mod = MutagenFixtures.NewMod("App.esp");
        var npc = MutagenFixtures.NewNpc(mod, editorId: "TestGuard", name: "Test Guard");

        var data = NpcDisplayData.FromGetter(npc);

        data.FormKey.Should().Be(npc.FormKey);
        data.EditorID.Should().Be("TestGuard");
        data.Name!.String.Should().Be("Test Guard");
        data.IsInLoadOrder.Should().BeTrue("a getter was supplied, so the NPC is present");
    }

    [Fact]
    public void FromGetter_NullNameAndEditorId_AreCarriedThroughAsNull()
    {
        var mod = MutagenFixtures.NewMod("App.esp");
        var npc = mod.Npcs.AddNew(); // no EditorID / Name assigned

        var data = NpcDisplayData.FromGetter(npc);

        data.EditorID.Should().BeNull();
        // Mutagen leaves Name null when unset; FromGetter copies the reference verbatim.
        data.Name.Should().BeNull();
        data.IsInLoadOrder.Should().BeTrue();
    }

    // ---- FromGetter: gender (Auxilliary.GetGender via Female flag) ----------

    [Fact]
    public void FromGetter_FemaleFlag_MapsToFemaleGender()
    {
        var mod = MutagenFixtures.NewMod("App.esp");
        var npc = MutagenFixtures.NewNpc(mod, female: true);

        NpcDisplayData.FromGetter(npc).Gender.Should().Be(Gender.Female);
    }

    [Fact]
    public void FromGetter_NoFemaleFlag_MapsToMaleGender()
    {
        var mod = MutagenFixtures.NewMod("App.esp");
        var npc = MutagenFixtures.NewNpc(mod, female: false);

        NpcDisplayData.FromGetter(npc).Gender.Should().Be(Gender.Male);
    }

    // ---- FromGetter: IsUnique (Configuration.Flags Unique) -----------------

    [Fact]
    public void FromGetter_UniqueFlagSet_MapsIsUniqueTrue()
    {
        var mod = MutagenFixtures.NewMod("App.esp");
        var npc = mod.Npcs.AddNew();
        npc.Configuration.Flags |= NpcConfiguration.Flag.Unique;

        NpcDisplayData.FromGetter(npc).IsUnique.Should().BeTrue();
    }

    [Fact]
    public void FromGetter_UniqueFlagClear_MapsIsUniqueFalse()
    {
        var mod = MutagenFixtures.NewMod("App.esp");
        var npc = mod.Npcs.AddNew();

        NpcDisplayData.FromGetter(npc).IsUnique.Should().BeFalse();
    }

    [Fact]
    public void FromGetter_UniqueAndFemaleAreIndependent()
    {
        var mod = MutagenFixtures.NewMod("App.esp");
        var npc = MutagenFixtures.NewNpc(mod, female: true);
        npc.Configuration.Flags |= NpcConfiguration.Flag.Unique;

        var data = NpcDisplayData.FromGetter(npc);

        data.IsUnique.Should().BeTrue();
        data.Gender.Should().Be(Gender.Female);
    }

    // ---- FromGetter: template / IsTemplateUser (Auxilliary.IsValidTemplatedNpc) ----

    [Fact]
    public void FromGetter_TraitsFlagAndTemplateSet_MapsIsTemplateUserTrue()
    {
        var mod = MutagenFixtures.NewMod("App.esp");
        var parent = MutagenFixtures.NewNpc(mod, editorId: "TemplateParent");
        var child = MutagenFixtures.NewNpc(mod, editorId: "Child", traitsTemplate: true, template: parent);

        var data = NpcDisplayData.FromGetter(child);

        data.IsTemplateUser.Should().BeTrue();
        data.TemplateFormKey.Should().Be(parent.FormKey);
    }

    [Fact]
    public void FromGetter_TraitsFlagButNoTemplate_IsNotTemplateUser()
    {
        // IsValidTemplatedNpc requires BOTH the Traits flag AND a non-null Template.
        var mod = MutagenFixtures.NewMod("App.esp");
        var npc = MutagenFixtures.NewNpc(mod, editorId: "TraitsNoTemplate", traitsTemplate: true);

        var data = NpcDisplayData.FromGetter(npc);

        data.IsTemplateUser.Should().BeFalse("the Template link is null");
        data.TemplateFormKey.Should().Be(FormKey.Null);
    }

    [Fact]
    public void FromGetter_TemplateSetButNoTraitsFlag_IsNotTemplateUser()
    {
        // A template link without the Traits template-flag is not a "valid templated NPC",
        // but TemplateFormKey is still copied from the link.
        var mod = MutagenFixtures.NewMod("App.esp");
        var parent = MutagenFixtures.NewNpc(mod, editorId: "TemplateParent");
        var child = MutagenFixtures.NewNpc(mod, editorId: "ChildNoTraits", template: parent);

        var data = NpcDisplayData.FromGetter(child);

        data.IsTemplateUser.Should().BeFalse("the Traits template flag is not set");
        data.TemplateFormKey.Should().Be(parent.FormKey, "the link FormKey is copied regardless of the flag");
    }

    [Fact]
    public void FromGetter_NoTemplateAtAll_TemplateFormKeyIsNull()
    {
        var mod = MutagenFixtures.NewMod("App.esp");
        var npc = MutagenFixtures.NewNpc(mod, editorId: "Plain");

        var data = NpcDisplayData.FromGetter(npc);

        data.IsTemplateUser.Should().BeFalse();
        data.TemplateFormKey.Should().Be(FormKey.Null);
    }

    // ---- record semantics: value equality + 'with' clone -------------------

    private static NpcDisplayData Sample(FormKey fk) => new()
    {
        FormKey = fk,
        EditorID = "Sample",
        IsTemplateUser = true,
        TemplateFormKey = FormKey.Factory("0000AA:Other.esp"),
        IsInLoadOrder = true,
        IsUnique = true,
        Gender = Gender.Male,
    };

    [Fact]
    public void Record_ValueEquality_HoldsForIdenticalFields()
    {
        var fk = FormKey.Factory("000801:Skyrim.esm");
        var a = Sample(fk);
        var b = Sample(fk);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Record_ValueEquality_DiffersWhenAnyFieldDiffers()
    {
        var fk = FormKey.Factory("000801:Skyrim.esm");
        var a = Sample(fk);
        var b = Sample(fk) with { IsUnique = false };

        a.Should().NotBe(b);
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Record_WithClone_CopiesUnchangedFieldsAndOverridesOne()
    {
        var fk = FormKey.Factory("000801:Skyrim.esm");
        var original = Sample(fk);

        var clone = original with { Gender = Gender.Female };

        clone.Gender.Should().Be(Gender.Female);
        clone.FormKey.Should().Be(original.FormKey);
        clone.EditorID.Should().Be(original.EditorID);
        clone.IsTemplateUser.Should().Be(original.IsTemplateUser);
        clone.TemplateFormKey.Should().Be(original.TemplateFormKey);
        clone.IsInLoadOrder.Should().Be(original.IsInLoadOrder);
        clone.IsUnique.Should().Be(original.IsUnique);
        original.Gender.Should().Be(Gender.Male, "the source record is immutable");
    }

    [Fact]
    public void Record_FromFormKey_And_FromGetter_DifferOnIsInLoadOrder()
    {
        var mod = MutagenFixtures.NewMod("App.esp");
        var npc = MutagenFixtures.NewNpc(mod, editorId: "X");

        var fromGetter = NpcDisplayData.FromGetter(npc);
        var fromKey = NpcDisplayData.FromFormKey(npc.FormKey);

        fromGetter.Should().NotBe(fromKey);
        (fromGetter with { IsInLoadOrder = false, EditorID = null, Gender = Gender.Female })
            .Should().Be(fromKey, "once the load-order/derived fields are equalised the records match");
    }
}
