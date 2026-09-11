using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

public class NpcChoiceImportPlanTests
{
    private static readonly FormKey A = FormKey.Factory("000801:Skyrim.esm");
    private static readonly FormKey B = FormKey.Factory("000802:Skyrim.esm");
    private static readonly FormKey C = FormKey.Factory("000803:Skyrim.esm");
    private static readonly FormKey D = FormKey.Factory("000804:Skyrim.esm");
    private static readonly FormKey Donor = FormKey.Factory("000D01:SharedFaces.esp");

    private static Settings Current() => new()
    {
        SelectedAppearanceMods = new()
        {
            [A] = ("Old Mod", A), [B] = ("Old Mod", B), [C] = ("Shared Faces", Donor)
        }
    };

    private static NpcConsistencyProvider Provider(Settings settings) =>
        new(settings, new Lazy<VM_Settings>(() => null!));

    private static Dictionary<string, NpcChoiceDto?> Read(string json)
    {
        NpcChoiceImport.TryParse(json, out var choices, out var error).Should().BeTrue(error);
        return choices;
    }

    // Valid fixtures stand in for the load-order validation report. Tests with rejected
    // entries supply the report explicitly, so membership and eligibility stay distinct.
    private static NpcChoiceImportPlan Plan(string json, Settings settings,
        Dictionary<FormKey, (string ModName, FormKey NpcFormKey)>? validSelections = null)
    {
        var choices = Read(json);
        validSelections ??= choices.ToDictionary(kvp => FormKey.Factory(kvp.Key),
            kvp => (kvp.Value!.ModName!, FormKey.Factory(kvp.Value.SourceNpcFormKey!)));
        return NpcChoiceImport.CreatePlan(choices, validSelections, settings.SelectedAppearanceMods);
    }

    private static string PartialImport(bool token)
    {
        var entries = $$"""
            {
                "{{A}}": { "ModName": "New Mod", "SourceNpcFormKey": "{{A}}" },
                "{{D}}": { "ModName": "Shared Faces", "SourceNpcFormKey": "{{Donor}}" }
            }
            """;
        return token ? "{\"ProcessedNpcs\":" + entries + "}" : entries;
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void PartialImport_OnlyClearsMissingChoicesWhenRequested(bool token, bool keepMissing)
    {
        var settings = Current();
        var provider = Provider(settings);
        var events = new List<NpcSelectionChangedEventArgs>();
        using var subscription = provider.NpcSelectionChanged.Subscribe(events.Add);
        var plan = Plan(PartialImport(token), settings);

        plan.MissingNpcs.Should().BeEquivalentTo(new[] { B, C });
        NpcChoiceImport.Apply(plan, provider, keepMissing).Should().BeTrue();

        provider.GetSelectedMod(A).Should().Be(("New Mod", A));
        provider.GetSelectedMod(D).Should().Be(("Shared Faces", Donor));
        settings.SelectedAppearanceMods[A].Should().Be(("New Mod", A));
        settings.SelectedAppearanceMods[D].Should().Be(("Shared Faces", Donor));
        if (keepMissing)
        {
            settings.SelectedAppearanceMods[B].Should().Be(("Old Mod", B));
            settings.SelectedAppearanceMods[C].Should().Be(("Shared Faces", Donor));
            provider.GetSelectedMod(C).Should().Be(("Shared Faces", Donor));
            events.Should().HaveCount(2);
        }
        else
        {
            settings.SelectedAppearanceMods.Should().NotContainKey(B).And.NotContainKey(C);
            provider.DoesNpcHaveSelection(B).Should().BeFalse();
            provider.DoesNpcHaveSelection(C).Should().BeFalse();
            events.Where(e => e.SelectedModName == null).Select(e => e.NpcFormKey)
                .Should().BeEquivalentTo(new[] { B, C });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancel_DoesNotMutateSettingsCacheOrNotify(bool token)
    {
        var settings = Current();
        var before = settings.SelectedAppearanceMods.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var provider = Provider(settings);
        int events = 0;
        using var subscription = provider.NpcSelectionChanged.Subscribe(_ => events++);

        NpcChoiceImport.Apply(Plan(PartialImport(token), settings), provider, null).Should().BeFalse();

        settings.SelectedAppearanceMods.Should().BeEquivalentTo(before);
        foreach (var (target, choice) in before)
            provider.GetSelectedMod(target).Should().Be(choice);
        events.Should().Be(0);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void MissingNpcDetection_UsesMembershipEvenForEqualOrLargerImports(int importCount)
    {
        var settings = Current();
        var choices = Enumerable.Range(4, importCount).ToDictionary(
            i => $"00080{i}:Skyrim.esm", i => (NpcChoiceDto?)new("New Mod", $"00080{i}:Skyrim.esm"));

        var plan = NpcChoiceImport.CreatePlan(choices, new Dictionary<FormKey, (string, FormKey)>(),
            settings.SelectedAppearanceMods);

        plan.MissingNpcs.Should().BeEquivalentTo(new[] { A, B, C });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresentButRejectedChoice_IsKeptEvenWhenClearingMissingNpcs(bool token)
    {
        var settings = Current();
        var provider = Provider(settings);
        var entries = $$"""
            { "{{A}}": { "ModName": "New Mod", "SourceNpcFormKey": "{{A}}" }, "{{B}}": null }
            """;
        var plan = Plan(token ? "{\"ProcessedNpcs\":" + entries + "}" : entries, settings,
            new() { [A] = ("New Mod", A) });

        plan.MissingNpcs.Should().BeEquivalentTo(new[] { C });
        NpcChoiceImport.Apply(plan, provider, false).Should().BeTrue();

        settings.SelectedAppearanceMods[B].Should().Be(("Old Mod", B));
        provider.GetSelectedMod(B).Should().Be(("Old Mod", B));
        settings.SelectedAppearanceMods.Should().NotContainKey(C);
    }

    [Fact]
    public void EmptyImport_CannotClearCurrentChoices()
    {
        var settings = Current();
        var provider = Provider(settings);

        NpcChoiceImport.Apply(Plan("{}", settings), provider, false).Should().BeFalse();

        settings.SelectedAppearanceMods.Should().HaveCount(3);
        provider.GetSelectedMod(C).Should().Be(("Shared Faces", Donor));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyToken_ProtectsExistingSharedFaceEvenWithDifferentMod(bool keepMissing)
    {
        var settings = Current();
        settings.SelectedAppearanceMods[A] = ("Existing Shared Face", Donor);
        var provider = Provider(settings);
        var plan = Plan($$"""
            { "ProcessedNpcs": { "{{A}}": { "ModName": "Old Mod" }, "{{B}}": { "ModName": "New Mod" } } }
            """, settings);

        plan.ProtectedSharedNpcs.Should().BeEquivalentTo(new[] { A });
        plan.InferredSourceCount.Should().Be(2);
        NpcChoiceImport.Apply(plan, provider, keepMissing).Should().BeTrue();

        settings.SelectedAppearanceMods[A].Should().Be(("Existing Shared Face", Donor));
        provider.GetSelectedMod(A).Should().Be(("Existing Shared Face", Donor));
        provider.GetSelectedMod(B).Should().Be(("New Mod", B));
    }

    [Fact]
    public void ModernToken_ExplicitOwnFaceCanReplaceExistingSharedFace()
    {
        var settings = Current();
        settings.SelectedAppearanceMods[A] = ("Shared Faces", Donor);
        var provider = Provider(settings);
        var plan = Plan(PartialImport(true), settings);

        plan.ProtectedSharedNpcs.Should().BeEmpty();
        plan.InferredSourceCount.Should().Be(0);
        NpcChoiceImport.Apply(plan, provider, true).Should().BeTrue();

        provider.GetSelectedMod(A).Should().Be(("New Mod", A));
    }

    [Fact]
    public void FullyCoveredCurrentChoices_DoNotRequireMissingNpcDecision()
    {
        var settings = new Settings { SelectedAppearanceMods = new() { [A] = ("Old Mod", A) } };

        Plan(PartialImport(false), settings).MissingNpcs.Should().BeEmpty();
    }
}
