using System.IO;
using System.Text;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <c>OutputValidator.ResolveTraitsAppearanceSource</c> — the Traits-template walker that decides
/// whose record and whose FaceGen the game actually renders for an NPC.
///
/// This matters because an NPC with the Traits flag never uses its own head parts or its own
/// facegeom .nif: validating them reports files the game does not load (observed in the field as
/// a leftover .nif from an unrelated mod raising a head-part mismatch on a generic NPC).
///
/// Pure and deterministic: in-memory Mutagen records and a single-mod link cache, no game install.
/// </summary>
public class OutputValidatorTemplateTests
{
    private static (INpcGetter? Record, ModKey ModKey, List<FormKey> Chain, string? Failure) Resolve(
        INpcGetter start, ILinkCache<ISkyrimMod, ISkyrimModGetter> cache, int maxDepth = 25) =>
        Reflect.InvokeStatic<OutputValidator, (INpcGetter?, ModKey, List<FormKey>, string?)>(
            "ResolveTraitsAppearanceSource", start, cache, maxDepth);

    private static ILinkCache<ISkyrimMod, ISkyrimModGetter> Cache(SkyrimMod mod) =>
        mod.ToImmutableLinkCache();

    [Fact]
    public void NonTemplatedNpc_ResolvesToItself()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, "PlainNpc");

        var (record, _, chain, failure) = Resolve(npc, Cache(mod));

        record.Should().NotBeNull();
        record!.FormKey.Should().Be(npc.FormKey);
        chain.Should().BeEmpty();
        failure.Should().BeNull();
    }

    [Fact]
    public void TraitsFlagWithoutTemplate_ResolvesToItself()
    {
        // The flag alone is inert — IsValidTemplatedNpc requires a non-null Template link.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, "FlagOnly", traitsTemplate: true);

        var (record, _, chain, failure) = Resolve(npc, Cache(mod));

        record!.FormKey.Should().Be(npc.FormKey);
        chain.Should().BeEmpty();
        failure.Should().BeNull();
    }

    [Fact]
    public void TemplateLinkWithoutTraitsFlag_IsNotFollowed()
    {
        // Template links exist for non-appearance inheritance (inventory, AI...); only the Traits
        // bit redirects the appearance, so the walker must stop at the NPC itself.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "TheTemplate");
        var npc = MutagenFixtures.NewNpc(mod, "InventoryOnly", template: template);

        var (record, _, chain, _) = Resolve(npc, Cache(mod));

        record!.FormKey.Should().Be(npc.FormKey);
        chain.Should().BeEmpty();
    }

    [Fact]
    public void SingleHop_ResolvesTheTemplate()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "RedguardWomanTemplate");
        var npc = MutagenFixtures.NewNpc(mod, "RedguardWoman", traitsTemplate: true, template: template);

        var (record, modKey, chain, failure) = Resolve(npc, Cache(mod));

        record!.FormKey.Should().Be(template.FormKey);
        modKey.Should().Be(mod.ModKey);
        chain.Should().Equal(template.FormKey);
        failure.Should().BeNull();
    }

    [Fact]
    public void MultiHop_ResolvesTheTerminalNpc_AndRecordsTheWholeChain()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var terminal = MutagenFixtures.NewNpc(mod, "Terminal");
        var middle = MutagenFixtures.NewNpc(mod, "Middle", traitsTemplate: true, template: terminal);
        var start = MutagenFixtures.NewNpc(mod, "Start", traitsTemplate: true, template: middle);

        var (record, _, chain, failure) = Resolve(start, Cache(mod));

        record!.FormKey.Should().Be(terminal.FormKey);
        chain.Should().Equal(middle.FormKey, terminal.FormKey);
        failure.Should().BeNull();
    }

    [Fact]
    public void SelfReferencingTemplate_ReportsALoop()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, "Ouroboros", traitsTemplate: true);
        npc.Template.SetTo(npc.FormKey);

        var (record, _, _, failure) = Resolve(npc, Cache(mod));

        record.Should().BeNull();
        failure.Should().Contain("loops back on itself");
    }

    [Fact]
    public void TwoNodeCycle_ReportsALoop()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var a = MutagenFixtures.NewNpc(mod, "A", traitsTemplate: true);
        var b = MutagenFixtures.NewNpc(mod, "B", traitsTemplate: true);
        a.Template.SetTo(b.FormKey);
        b.Template.SetTo(a.FormKey);

        var (record, _, _, failure) = Resolve(a, Cache(mod));

        record.Should().BeNull();
        failure.Should().Contain("loops back on itself");
    }

    [Fact]
    public void MissingTemplateRecord_ReportsItAsNotFound()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, "Orphaned", traitsTemplate: true);
        var absent = MutagenFixtures.Fk("0DEAD0:Absent.esp");
        npc.Template.SetTo(absent);

        var (record, _, chain, failure) = Resolve(npc, Cache(mod));

        record.Should().BeNull();
        chain.Should().Equal(absent);
        failure.Should().Contain("could not be found in your load order");
        failure.Should().Contain(absent.ToString());
    }

    [Fact]
    public void LeveledNpcTemplate_IsCalledOutSpecifically()
    {
        // A Traits template pointing at an LVLN means the game picks the appearance at runtime,
        // so there is nothing static to validate — and the user needs a different explanation
        // than "the plugin is missing".
        var mod = MutagenFixtures.NewMod("Test.esp");
        var leveled = new LeveledNpc(mod) { EditorID = "LvlBandit" };
        mod.LeveledNpcs.Add(leveled);
        var npc = MutagenFixtures.NewNpc(mod, "Bandit", traitsTemplate: true);
        npc.Template.SetTo(leveled.FormKey);

        var (record, _, _, failure) = Resolve(npc, Cache(mod));

        record.Should().BeNull();
        failure.Should().Contain("Leveled NPC");
        failure.Should().NotContain("could not be found");
    }

    [Fact]
    public void ChainLongerThanMaxDepth_BailsOutInsteadOfSpinning()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var terminal = MutagenFixtures.NewNpc(mod, "Terminal");
        var current = terminal;
        for (int i = 0; i < 6; i++)
            current = MutagenFixtures.NewNpc(mod, "Link" + i, traitsTemplate: true, template: current);

        var (record, _, _, failure) = Resolve(current, Cache(mod), maxDepth: 3);

        record.Should().BeNull();
        failure.Should().Contain("unreasonably long");
    }

    // ---- Reporting policy (TryRedirectToTemplate) -------------------------------------------
    //
    // Validation reports only what changes what the user gets. A batch selection sets the same
    // mod on an NPC and on its template, so the inherited face IS the chosen one — that must
    // produce no row at all. A row is warranted only when the template carries a different
    // choice, or none.

    private sealed class RedirectOutcome
    {
        public bool Continue;
        public string SelectedModName = string.Empty;
        public FormKey DonorFk;
        public FormKey SubjectFk;
        public ValidationRunResult Result = new();
    }

    private static RedirectOutcome Redirect(
        SkyrimMod mod, Npc recipient, string selectedModName, Settings settings, params FormKey[] scoped)
    {
        var validator = Reflect.Uninitialized<OutputValidator>();
        Reflect.SetField(validator, "_settings", settings);

        var outcome = new RedirectOutcome();
        object?[] args =
        {
            recipient.FormKey,                       // npcFk
            (INpcGetter)recipient,                   // recipientRecord
            "TestNpc",                               // displayName
            selectedModName,                         // ref selectedModName
            recipient.FormKey,                       // ref donorFk
            (INpcGetter?)recipient,                  // ref winningRecord
            mod.ModKey,                              // ref winningModKey
            recipient.FormKey,                       // ref subjectFk
            Cache(mod),                              // linkCache
            Path.Combine(Path.GetTempPath(), "npc2-tests-no-such-data-folder"), // dataFolder (absent)
            scoped.ToHashSet(),                      // scopedNpcs
            outcome.Result,                          // result
            new StringBuilder(),                     // log
        };

        outcome.Continue = Reflect.Invoke<bool>(validator, "TryRedirectToTemplate", args);
        outcome.SelectedModName = (string)args[3]!;
        outcome.DonorFk = (FormKey)args[4]!;
        outcome.SubjectFk = (FormKey)args[7]!;
        return outcome;
    }

    private static Settings SettingsWith(params (FormKey Npc, string Mod, FormKey Donor)[] selections)
    {
        var s = new Settings();
        foreach (var (npc, modName, donor) in selections)
            s.SelectedAppearanceMods[npc] = (modName, donor);
        return s;
    }

    [Fact]
    public void Redirect_TemplateCarriesTheSameSelection_ReportsNothing()
    {
        // The reported case: every NPC batch-selected to one overhaul, so the template is set to
        // the same mod. The face in game is the one the user picked — silence is correct.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "EncBandit01Melee1HImperialM");
        var npc = MutagenFixtures.NewNpc(mod, "Adventurer", traitsTemplate: true, template: template);
        var settings = SettingsWith(
            (npc.FormKey, "High Poly NPC Overhaul", npc.FormKey),
            (template.FormKey, "High Poly NPC Overhaul", template.FormKey));

        var outcome = Redirect(mod, npc, "High Poly NPC Overhaul", settings, npc.FormKey, template.FormKey);

        outcome.Result.Issues.Should().BeEmpty();
        outcome.Continue.Should().BeFalse("the template is in the report and validates itself");
    }

    [Fact]
    public void Redirect_SameSelectionButTemplateNotInReport_IsSilentAndStillChecksTheTemplate()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "TheTemplate");
        var npc = MutagenFixtures.NewNpc(mod, "Adventurer", traitsTemplate: true, template: template);
        var templateDonor = MutagenFixtures.Fk("000900:Overhaul.esp");
        var settings = SettingsWith(
            (npc.FormKey, "Overhaul", npc.FormKey),
            (template.FormKey, "Overhaul", templateDonor));

        var outcome = Redirect(mod, npc, "Overhaul", settings, npc.FormKey);

        outcome.Result.Issues.Should().BeEmpty();
        outcome.Continue.Should().BeTrue();
        outcome.SubjectFk.Should().Be(template.FormKey, "the template's FaceGen is what renders");
        outcome.DonorFk.Should().Be(templateDonor);
        outcome.SelectedModName.Should().Be("Overhaul");
    }

    [Fact]
    public void Redirect_TemplateCarriesADifferentSelection_WarnsWithBothMods()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "TheTemplate");
        var npc = MutagenFixtures.NewNpc(mod, "Adventurer", traitsTemplate: true, template: template);
        var settings = SettingsWith(
            (npc.FormKey, "Bijin", npc.FormKey),
            (template.FormKey, "High Poly NPC Overhaul", template.FormKey));

        var outcome = Redirect(mod, npc, "Bijin", settings, npc.FormKey, template.FormKey);

        outcome.Result.Issues.Should().HaveCount(1);
        var issue = outcome.Result.Issues[0];
        issue.Severity.Should().Be(ValidationSeverity.Warning, "the user's choice does not take effect");
        issue.Check.Should().Be(ValidationCheckKind.Template);
        issue.Issue.Should().Contain("You selected 'Bijin'");
        issue.Issue.Should().Contain("set to 'High Poly NPC Overhaul'");
        issue.Issue.Should().Contain("select 'Bijin' for 'TheTemplate' as well");
        outcome.Continue.Should().BeFalse();
    }

    [Fact]
    public void Redirect_TemplateHasNoSelection_WarnsAndSaysWhatToDo()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "TheTemplate");
        var npc = MutagenFixtures.NewNpc(mod, "Adventurer", traitsTemplate: true, template: template);
        var settings = SettingsWith((npc.FormKey, "Bijin", npc.FormKey));

        var outcome = Redirect(mod, npc, "Bijin", settings, npc.FormKey);

        outcome.Result.Issues.Should().HaveCount(1);
        outcome.Result.Issues[0].Severity.Should().Be(ValidationSeverity.Warning);
        outcome.Result.Issues[0].Issue.Should().Contain("no appearance selection");
        outcome.Result.Issues[0].Issue.Should().Contain("select an appearance for 'TheTemplate'");
        outcome.Continue.Should().BeFalse("there is nothing to compare the template against");
    }

    [Fact]
    public void Redirect_UnfollowableChain_IsInfoNotWarning()
    {
        // Can't tell whether the selection takes effect, so this is context, not a defect.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, "Adventurer", traitsTemplate: true);
        npc.Template.SetTo(MutagenFixtures.Fk("0DEAD0:Absent.esp"));
        var settings = SettingsWith((npc.FormKey, "Bijin", npc.FormKey));

        var outcome = Redirect(mod, npc, "Bijin", settings, npc.FormKey);

        outcome.Result.Issues.Should().HaveCount(1);
        outcome.Result.Issues[0].Severity.Should().Be(ValidationSeverity.Info);
        outcome.Result.Issues[0].Issue.Should().Contain("could not be found in your load order");
        outcome.Continue.Should().BeFalse();
    }

    [Fact]
    public void Redirect_NamelessTemplate_IsDescribedWithoutATrailingPipe()
    {
        // GetLogString renders a Name-less record as "EditorID | "; the row must not inherit that.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "EncBandit01Melee1HImperialM");
        var npc = MutagenFixtures.NewNpc(mod, "Adventurer", traitsTemplate: true, template: template);
        var settings = SettingsWith((npc.FormKey, "Bijin", npc.FormKey));

        var outcome = Redirect(mod, npc, "Bijin", settings, npc.FormKey);

        var issue = outcome.Result.Issues.Should().ContainSingle().Subject;
        issue.Issue.Should().Contain("'EncBandit01Melee1HImperialM'");
        issue.Issue.Should().NotContain("|");
        issue.WinningSource.Should().StartWith("EncBandit01Melee1HImperialM [");
    }

    // ---- Donor-side walker (SkyPatcher surrogates) ------------------------------------------
    //
    // ResolveDonorAppearanceRoot walks the chain through the SELECTED MOD, falling back to the
    // load order for links the mod does not override. A FaceGen-only ModSetting short-circuits
    // the mod-side lookup, so these exercise the fallback path and the guards without a
    // RecordHandler or a game install.

    private static FormKey ResolveDonorRoot(
        ModSetting modSetting, FormKey donorFk, ILinkCache<ISkyrimMod, ISkyrimModGetter> cache, int maxDepth = 25)
    {
        var validator = Reflect.Uninitialized<OutputValidator>();
        return Reflect.Invoke<FormKey>(validator, "ResolveDonorAppearanceRoot", modSetting, donorFk, cache, maxDepth);
    }

    private static ModSetting FaceGenOnlyMod() => new() { DisplayName = "Test Mod", IsFaceGenOnlyEntry = true };

    [Fact]
    public void DonorRoot_NonTemplatedDonor_IsTheDonorItself()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var donor = MutagenFixtures.NewNpc(mod, "Donor");

        ResolveDonorRoot(FaceGenOnlyMod(), donor.FormKey, Cache(mod)).Should().Be(donor.FormKey);
    }

    [Fact]
    public void DonorRoot_TemplatedDonor_WalksToTheRoot()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var root = MutagenFixtures.NewNpc(mod, "Root");
        var middle = MutagenFixtures.NewNpc(mod, "Middle", traitsTemplate: true, template: root);
        var donor = MutagenFixtures.NewNpc(mod, "Donor", traitsTemplate: true, template: middle);

        ResolveDonorRoot(FaceGenOnlyMod(), donor.FormKey, Cache(mod)).Should().Be(root.FormKey);
    }

    [Fact]
    public void DonorRoot_Cycle_ReturnsNull()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var a = MutagenFixtures.NewNpc(mod, "A", traitsTemplate: true);
        var b = MutagenFixtures.NewNpc(mod, "B", traitsTemplate: true);
        a.Template.SetTo(b.FormKey);
        b.Template.SetTo(a.FormKey);

        ResolveDonorRoot(FaceGenOnlyMod(), a.FormKey, Cache(mod)).IsNull.Should().BeTrue();
    }

    [Fact]
    public void DonorRoot_UnresolvableLink_ReturnsNull()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var donor = MutagenFixtures.NewNpc(mod, "Donor", traitsTemplate: true);
        donor.Template.SetTo(MutagenFixtures.Fk("0DEAD0:Absent.esp"));

        ResolveDonorRoot(FaceGenOnlyMod(), donor.FormKey, Cache(mod)).IsNull.Should().BeTrue();
    }

    [Fact]
    public void DonorRoot_UnknownDonor_ReturnsNull()
    {
        // Donor not present anywhere: nothing to walk from, and the caller must not treat the
        // unresolved FormKey as a valid root.
        var mod = MutagenFixtures.NewMod("Test.esp");

        ResolveDonorRoot(FaceGenOnlyMod(), MutagenFixtures.Fk("0BEEF0:Absent.esp"), Cache(mod))
            .IsNull.Should().BeTrue();
    }

    [Fact]
    public void DonorRoot_ChainLongerThanMaxDepth_ReturnsNull()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var current = MutagenFixtures.NewNpc(mod, "Root");
        for (int i = 0; i < 6; i++)
            current = MutagenFixtures.NewNpc(mod, "Link" + i, traitsTemplate: true, template: current);

        ResolveDonorRoot(FaceGenOnlyMod(), current.FormKey, Cache(mod), maxDepth: 3).IsNull.Should().BeTrue();
    }

    [Fact]
    public void WalkerStopsAtTheFirstNonTemplatedRecord_EvenIfItHasAppearanceOfItsOwn()
    {
        // Guard the contract the caller depends on: the returned record is the one whose FaceGen
        // path the checks are re-targeted at, so it must be the first NPC without the flag.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var terminal = MutagenFixtures.NewNpc(mod, "Terminal");
        var middle = MutagenFixtures.NewNpc(mod, "Middle", traitsTemplate: true, template: terminal);
        // 'middle' also carries head parts; they are inert because it is itself templated.
        middle.HeadParts.Add(MutagenFixtures.Fk("000801:Test.esp").ToLink<IHeadPartGetter>());
        var start = MutagenFixtures.NewNpc(mod, "Start", traitsTemplate: true, template: middle);

        var (record, _, _, _) = Resolve(start, Cache(mod));

        record!.FormKey.Should().Be(terminal.FormKey);
        record.HeadParts.Should().BeEmpty();
    }
}
