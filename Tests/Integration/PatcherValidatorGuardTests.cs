using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Exercises the real <see cref="Patcher.RunPatchingLogic"/> and
/// <see cref="Validator.ScreenSelectionsAsync"/> entry points through the backend Autofac graph,
/// asserting their guard / early-return branches. These reach the real methods without needing a
/// game install (the Invalid-environment path returns before any link-cache access). Log output is
/// captured via <c>OptionalUIModule.ConnectToUILogger</c>.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class PatcherValidatorGuardTests
{
    private readonly ITestOutputHelper _output;
    public PatcherValidatorGuardTests(ITestOutputHelper output) => _output = output;

    private static List<(string msg, bool err)> Capture(OptionalUIModule module)
    {
        var logs = new List<(string, bool)>();
        module.ConnectToUILogger((m, e, _) => logs.Add((m, e)), null, null, null);
        return logs;
    }

    [Fact]
    public async Task RunPatchingLogic_InvalidEnvironment_AbortsWithError()
    {
        using var harness = NpcChooserHarness.Invalid();
        var patcher = harness.Patcher;
        var logs = Capture(patcher);

        await patcher.RunPatchingLogic(
            new List<KeyValuePair<FormKey, ScreeningResult>>(),
            showFinalMessage: false, isFirstIteration: true, ct: CancellationToken.None);

        logs.Should().Contain(l => l.err && l.msg.Contains("Environment is not valid"),
            "an invalid environment must abort before any patching");
    }

    [Fact]
    public async Task RunPatchingLogic_BlankOutputDirectory_AbortsWithError()
    {
        // Build a valid environment if available; otherwise the invalid-env guard fires first and
        // this assertion is vacuously satisfied via the skip.
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip))
        {
            _output.WriteLine("SKIPPED (needs Skyrim to reach the output-dir guard): " + skip);
            return;
        }
        using (env)
        {
            var settings = new Settings { OutputDirectory = "" };
            using var harness = new NpcChooserHarness(env!.Provider, settings);
            var patcher = harness.Patcher;
            var logs = Capture(patcher);

            await patcher.RunPatchingLogic(
                new List<KeyValuePair<FormKey, ScreeningResult>>(),
                showFinalMessage: false, isFirstIteration: true, ct: CancellationToken.None);

            logs.Should().Contain(l => l.err && l.msg.Contains("Output Directory is not set"));
        }
    }

    [Fact]
    public async Task ScreenSelectionsAsync_NoSelections_ReturnsEmptyReport()
    {
        using var harness = NpcChooserHarness.Invalid(new Settings()); // no SelectedAppearanceMods
        var validator = harness.Validator;

        var report = await validator.ScreenSelectionsAsync(
            new Dictionary<string, ModSetting>(), Patcher.ALL_NPCS_GROUP, CancellationToken.None);

        report.Should().NotBeNull();
        report.InvalidSelections.Should().BeEmpty("there is nothing to screen");
        validator.GetScreeningCache().Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task ScreenSelectionsAsync_GroupWithNoMembers_ReturnsEmptyReport()
    {
        var settings = new Settings();
        // A selection exists, but the requested group has no members -> empty report.
        settings.SelectedAppearanceMods[FormKey.Factory("000801:Skyrim.esm")] =
            ("SomeMod", FormKey.Factory("000801:Skyrim.esm"));
        using var harness = NpcChooserHarness.Invalid(settings);

        var report = await harness.Validator.ScreenSelectionsAsync(
            new Dictionary<string, ModSetting>(), "NonexistentGroup", CancellationToken.None);

        report.InvalidSelections.Should().BeEmpty();
    }
}
