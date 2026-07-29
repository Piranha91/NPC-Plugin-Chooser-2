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
/// <c>Patcher.SyncTemplateInheritance</c> — keeping the Traits flag and the TPLT link together.
///
/// Regression origin: the patcher set the Traits flag from the donor but never wrote the template
/// link, so an NPC whose chosen appearance is inherited (e.g. Redguard Woman 0B85AB, donated by a
/// High Poly record that inherits from TreasCorpseCommonerRedguardFemale 048117) was written with
/// Traits set and an empty TPLT. That record inherits nothing, and its own head parts then
/// disagree with whatever FaceGen is deployed — which is how it surfaced, as a FaceGen/plugin
/// mismatch on an NPC nobody had touched.
///
/// The method needs no patcher state (AppendLog is null-safe until a UI logger is connected), so
/// these run against an uninitialised instance with in-memory records.
/// </summary>
public class PatcherTemplateInheritanceTests
{
    private static void Sync(Npc target, INpcGetter source) =>
        Reflect.InvokeVoid(Reflect.Uninitialized<Patcher>(), "SyncTemplateInheritance", target, source);

    private static bool HasTraits(INpcGetter npc) =>
        npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits);

    [Fact]
    public void DonorInheritsItsAppearance_FlagAndLinkAreBothWritten()
    {
        // The reported bug: recipient plain, donor templated -> output must carry BOTH.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "TreasCorpseCommonerRedguardFemale");
        var recipient = MutagenFixtures.NewNpc(mod, "RedguardWoman");
        var donor = MutagenFixtures.NewNpc(mod, "RedguardWomanHighPoly", traitsTemplate: true, template: template);

        Sync(recipient, donor);

        HasTraits(recipient).Should().BeTrue();
        recipient.Template.FormKey.Should().Be(template.FormKey,
            "a Traits flag with a null template inherits no face at all");
    }

    [Fact]
    public void BothInherit_FromDifferentTemplates_LinkFollowsTheDonor()
    {
        // The flag does not change here, so a fix that only ran on flag transitions would miss it.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var oldTemplate = MutagenFixtures.NewNpc(mod, "OldTemplate");
        var newTemplate = MutagenFixtures.NewNpc(mod, "NewTemplate");
        var recipient = MutagenFixtures.NewNpc(mod, "Recipient", traitsTemplate: true, template: oldTemplate);
        var donor = MutagenFixtures.NewNpc(mod, "Donor", traitsTemplate: true, template: newTemplate);

        Sync(recipient, donor);

        HasTraits(recipient).Should().BeTrue();
        recipient.Template.FormKey.Should().Be(newTemplate.FormKey);
    }

    [Fact]
    public void BothInherit_FromTheSameTemplate_IsANoOp()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "Template");
        var recipient = MutagenFixtures.NewNpc(mod, "Recipient", traitsTemplate: true, template: template);
        var donor = MutagenFixtures.NewNpc(mod, "Donor", traitsTemplate: true, template: template);

        Sync(recipient, donor);

        HasTraits(recipient).Should().BeTrue();
        recipient.Template.FormKey.Should().Be(template.FormKey);
    }

    [Fact]
    public void DonorDoesNotInherit_FlagIsClearedButTheLinkIsKept()
    {
        // TPLT also drives inventory/AI/faction inheritance whose flags this app never touches,
        // so clearing the link along with the Traits bit would break unrelated behaviour.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "Template");
        var recipient = MutagenFixtures.NewNpc(mod, "Recipient", traitsTemplate: true, template: template);
        var donor = MutagenFixtures.NewNpc(mod, "Donor");

        Sync(recipient, donor);

        HasTraits(recipient).Should().BeFalse("the chosen appearance is the donor's own, not an inherited one");
        recipient.Template.FormKey.Should().Be(template.FormKey);
    }

    [Fact]
    public void MalformedDonor_TraitsWithNoTemplate_LeavesTheRecipientLinkAlone()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var existing = MutagenFixtures.NewNpc(mod, "ExistingTemplate");
        var recipient = MutagenFixtures.NewNpc(mod, "Recipient", traitsTemplate: true, template: existing);
        var donor = MutagenFixtures.NewNpc(mod, "BrokenDonor", traitsTemplate: true); // flag, no link

        Sync(recipient, donor);

        recipient.Template.FormKey.Should().Be(existing.FormKey, "there was nothing to copy");
    }

    [Fact]
    public void NeitherInherits_NothingIsWritten()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var recipient = MutagenFixtures.NewNpc(mod, "Recipient");
        var donor = MutagenFixtures.NewNpc(mod, "Donor");

        Sync(recipient, donor);

        HasTraits(recipient).Should().BeFalse();
        recipient.Template.FormKey.Should().Be(FormKey.Null);
    }

    [Fact]
    public void SkyPatcherSurrogate_FlattensAnInheritedAppearanceInsteadOfPassingTheChainOn()
    {
        // Per SkyPatcher's source (github.com/Zzyxz/SkyPatcher npc_patcher.cpp @ main),
        // copyVisualStyle sets the recipient's faceNPC to the surrogate and copies raw fields — it
        // does not walk a template chain. Proven in game, the ENGINE then resolves the surrogate's
        // own Traits chain and loads FaceGen from the TERMINUS's path.
        //
        // That was previously left in place (DeepCopyIn carried the donor's Traits flag + TPLT), but
        // it makes the result load-order-dependent and, worse, not per-NPC: every NPC inheriting
        // from one terminus resolves to that single shared path, so two NPCs given different
        // appearance mods would be forced to look identical. Flattening gives the surrogate the
        // terminus's appearance outright so its own path is authoritative.
        var mod = MutagenFixtures.NewMod("Donor.esp");
        var template = MutagenFixtures.NewNpc(mod, "TreasCorpseCommonerRedguardFemale");
        var donor = MutagenFixtures.NewNpc(mod, "RedguardWomanHighPoly", traitsTemplate: true, template: template);

        var output = MutagenFixtures.NewMod("NPC.esp");
        var env = Reflect.Uninitialized<EnvironmentStateProvider>();
        Reflect.SetField(env, "OutputMod", output);

        var skyPatcher = Reflect.Uninitialized<SkyPatcherInterface>();
        Reflect.SetField(skyPatcher, "_environmentStateProvider", env);
        Reflect.SetField(skyPatcher, "_outputs", CreateBackingDictionary(skyPatcher, "_outputs"));
        Reflect.SetField(skyPatcher, "_keyOriginalValSurrogate", new Dictionary<FormKey, FormKey>());
        Reflect.SetField(skyPatcher, "_keySurrogateValOrriginal", new Dictionary<FormKey, FormKey>());

        var recipientFk = MutagenFixtures.Fk("0B85AB:Skyrim.esm");
        // appearanceOnly:false — Reflect.Invoke matches on arg count, so the optional parameters
        // must be passed explicitly. These tests are about the traits/flattening overlay, not the
        // non-appearance strip (SurrogateAppearanceOnlyTests owns that), so keep it off.
        var surrogate = Reflect.Invoke<Npc>(skyPatcher, "CreateSkyPatcherNpc",
            recipientFk, (INpcGetter)donor, (INpcGetter)template, false, false)!;

        HasTraits(surrogate).Should().BeFalse("the surrogate must own its face, not inherit it");
        surrogate.EditorID.Should().Be("RedguardWomanHighPoly_Template");
    }

    [Fact]
    public void SkyPatcherSurrogate_WithNoTerminus_LeavesTheDonorCopyAlone()
    {
        // An untemplated donor has nothing to flatten; the surrogate is a straight copy.
        var mod = MutagenFixtures.NewMod("Donor.esp");
        var donor = MutagenFixtures.NewNpc(mod, "PlainDonor");

        var (_, surrogate) = NewSurrogate(MutagenFixtures.Fk("0B85AB:Skyrim.esm"), donor);

        HasTraits(surrogate).Should().BeFalse();
        surrogate.EditorID.Should().Be("PlainDonor_Template");
    }

    /// <summary>Builds the private NpcContainer dictionary the surrogate factory writes into,
    /// whose value type is not visible from the test assembly.</summary>
    private static object CreateBackingDictionary(object owner, string fieldName)
    {
        var fieldType = owner.GetType()
            .GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .FieldType;
        return System.Activator.CreateInstance(fieldType)!;
    }

    // ---- SkyPatcher-mode Traits directives ---------------------------------------------------
    //
    // The directive lands on the RECIPIENT (filterByNPCs=recipient) while the appearance arrives
    // through the surrogate, so the two directions are NOT symmetric: clearing the bit stops the
    // recipient inheriting a face the user did not choose, while setting it could only point at
    // the recipient's own template — never the donor's, which SkyPatcher cannot re-point.

    private static (SkyPatcherInterface Sky, Npc Surrogate) NewSurrogate(FormKey recipientFk, INpcGetter donor)
    {
        var env = Reflect.Uninitialized<EnvironmentStateProvider>();
        Reflect.SetField(env, "OutputMod", MutagenFixtures.NewMod("NPC.esp"));

        var sky = Reflect.Uninitialized<SkyPatcherInterface>();
        Reflect.SetField(sky, "_environmentStateProvider", env);
        Reflect.SetField(sky, "_outputs", CreateBackingDictionary(sky, "_outputs"));
        Reflect.SetField(sky, "_keyOriginalValSurrogate", new Dictionary<FormKey, FormKey>());
        Reflect.SetField(sky, "_keySurrogateValOrriginal", new Dictionary<FormKey, FormKey>());

        // Null terminus = "the donor does not inherit", which is what these traits-directive tests
        // are about; the flattening path has its own tests below. appearanceOnly:false for the same
        // reason as above — the strip is SurrogateAppearanceOnlyTests' subject, not this file's.
        var surrogate = Reflect.Invoke<Npc>(sky, "CreateSkyPatcherNpc",
            recipientFk, donor, null, false, false)!;
        return (sky, surrogate);
    }

    /// <summary>Reads back the .ini directives queued for an NPC (NpcContainer/SkyPatcherAction
    /// are private nested types, so this goes through reflection).</summary>
    private static List<string> QueuedActions(SkyPatcherInterface sky, FormKey npcFk)
    {
        var outputs = Reflect.GetField<System.Collections.IDictionary>(sky, "_outputs");
        var container = outputs[npcFk]!;
        var actions = (System.Collections.IEnumerable)container.GetType().GetProperty("Actions")!.GetValue(container)!;
        var texts = new List<string>();
        foreach (var action in actions)
            texts.Add((string)action.GetType().GetProperty("Text")!.GetValue(action)!);
        return texts;
    }

    private static List<string> EmitDirectives(INpcGetter recipient, FormKey recipientFk, INpcGetter donor)
    {
        var (sky, surrogate) = NewSurrogate(recipientFk, donor);
        var patcher = Reflect.Uninitialized<Patcher>();
        Reflect.SetField(patcher, "_skyPatcherInterface", sky);
        Reflect.InvokeVoid(patcher, "ApplySkyPatcherDirectives", recipientFk, recipient, surrogate, false);
        return QueuedActions(sky, recipientFk);
    }

    [Fact]
    public void SkyPatcher_RecipientInheritsButDonorDoesNot_ClearsTheFlag()
    {
        // Without this the recipient keeps showing its template's face instead of the chosen mod's.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "Template");
        var recipient = MutagenFixtures.NewNpc(mod, "Recipient", traitsTemplate: true, template: template);
        var donor = MutagenFixtures.NewNpc(mod, "Donor");

        EmitDirectives(recipient, recipient.FormKey, donor)
            .Should().Contain("removeTemplateFlags=traits");
    }

    [Fact]
    public void SkyPatcher_DonorInheritsButRecipientDoesNot_EmitsNoTraitsDirective()
    {
        // Setting the bit could only make the recipient inherit from its OWN template, never the
        // donor's; the inherited face reaches it through faceNPC = surrogate instead.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "Template");
        var recipient = MutagenFixtures.NewNpc(mod, "Recipient");
        var donor = MutagenFixtures.NewNpc(mod, "Donor", traitsTemplate: true, template: template);

        EmitDirectives(recipient, recipient.FormKey, donor)
            .Should().NotContain(a => a.Contains("TemplateFlags"));
    }

    [Fact]
    public void SkyPatcher_TraitsStateMatches_EmitsNoTraitsDirective()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "Template");
        var recipient = MutagenFixtures.NewNpc(mod, "Recipient", traitsTemplate: true, template: template);
        var donor = MutagenFixtures.NewNpc(mod, "Donor", traitsTemplate: true, template: template);

        EmitDirectives(recipient, recipient.FormKey, donor)
            .Should().NotContain(a => a.Contains("TemplateFlags"));
    }

    [Fact]
    public void TemplateFlagsOtherThanTraits_AreNotDisturbed()
    {
        // Only the Traits bit describes appearance; the rest belong to the recipient's own setup.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "Template");
        var recipient = MutagenFixtures.NewNpc(mod, "Recipient", traitsTemplate: true, template: template);
        recipient.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Inventory;
        var donor = MutagenFixtures.NewNpc(mod, "Donor");

        Sync(recipient, donor);

        // Exact equality, so any other bit being cleared as collateral fails here.
        recipient.Configuration.TemplateFlags.Should().Be(NpcConfiguration.TemplateFlag.Inventory);
    }
}
