using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Locks the <see cref="OutputBackend"/> migration from the old <c>UseSkyPatcherMode</c> bool and
/// the intent properties every call site reads instead of the backend itself
/// (<see cref="Settings.UsesSurrogates"/> and friends).
/// </summary>
public class OutputBackendSettingsTests
{
    private static Settings Load(string json)
    {
        var settings = JSONhandler<Settings>.Deserialize(json, out bool success, out string exception);
        success.Should().BeTrue(exception);
        return settings!;
    }

    [Fact]
    public void LegacySkyPatcherTrue_MigratesToSkyPatcherBackend()
    {
        Load("{ \"UseSkyPatcherMode\": true }").OutputBackend.Should().Be(OutputBackend.SkyPatcher);
    }

    [Fact]
    public void LegacySkyPatcherFalse_StaysOnEsp()
    {
        Load("{ \"UseSkyPatcherMode\": false }").OutputBackend.Should().Be(OutputBackend.ESP);
    }

    [Fact]
    public void AbsentKeys_DefaultToEspHybridAndPriority1000()
    {
        var s = Load("{}");
        s.OutputBackend.Should().Be(OutputBackend.ESP);
        s.RecastMode.Should().Be(RecastMode.Hybrid);
        s.RecastPatchPriority.Should().Be(1000);
    }

    [Fact]
    public void NewKeys_RoundTripByName_AndTheLegacyKeyIsNeverWritten()
    {
        var original = new Settings
        {
            OutputBackend = OutputBackend.Recast,
            RecastMode = RecastMode.RecastOnly,
            RecastPatchPriority = 250,
        };

        string json = JSONhandler<Settings>.Serialize(original, out bool success, out string exception);
        success.Should().BeTrue(exception);
        json.Should().Contain("\"OutputBackend\": \"Recast\"")
            .And.Contain("\"RecastMode\": \"RecastOnly\"")
            .And.NotContain("UseSkyPatcherMode")
            .And.NotContain("UsesSurrogates");

        var back = Load(json);
        back.OutputBackend.Should().Be(OutputBackend.Recast);
        back.RecastMode.Should().Be(RecastMode.RecastOnly);
        back.RecastPatchPriority.Should().Be(250);
    }

    [Theory]
    //          backend                    recastMode               surrogates skyAppearance skyDirectives recast
    [InlineData(OutputBackend.ESP,        RecastMode.Hybrid,       false,     false,        false,        false)]
    [InlineData(OutputBackend.ESP,        RecastMode.RecastOnly,   false,     false,        false,        false)]
    [InlineData(OutputBackend.SkyPatcher, RecastMode.Hybrid,       true,      true,         true,         false)]
    [InlineData(OutputBackend.SkyPatcher, RecastMode.RecastOnly,   true,      true,         true,         false)]
    [InlineData(OutputBackend.Recast,     RecastMode.Hybrid,       true,      false,        true,         true)]
    [InlineData(OutputBackend.Recast,     RecastMode.RecastOnly,   true,      false,        false,        true)]
    public void IntentProperties_FollowTheBackend(OutputBackend backend, RecastMode recastMode,
        bool surrogates, bool skyAppearance, bool skyDirectives, bool recast)
    {
        var s = new Settings { OutputBackend = backend, RecastMode = recastMode };

        s.UsesSurrogates.Should().Be(surrogates);
        s.UsesSkyPatcherAppearance.Should().Be(skyAppearance);
        s.SkyPatcherDirectivesAvailable.Should().Be(skyDirectives);
        s.UsesRecast.Should().Be(recast);
    }

    [Theory]
    [InlineData(OutputBackend.ESP, PatchingMode.Create, false)]
    [InlineData(OutputBackend.ESP, PatchingMode.CreateAndPatch, true)]
    [InlineData(OutputBackend.SkyPatcher, PatchingMode.Create, true)]
    [InlineData(OutputBackend.Recast, PatchingMode.Create, true)]
    public void WigHandling_IsActiveForEverySurrogateBackend(OutputBackend backend, PatchingMode mode, bool active)
    {
        new Settings { OutputBackend = backend, PatchingMode = mode }
            .WigHandlingActiveForOutputMode.Should().Be(active);
    }

    [Theory]
    [InlineData(OutputBackend.SkyPatcher, RecastMode.Hybrid, false)]
    [InlineData(OutputBackend.Recast, RecastMode.Hybrid, false)]
    [InlineData(OutputBackend.Recast, RecastMode.RecastOnly, true)]
    public void OutfitField_IsInertOnlyWhereNothingCanDeliverAnOutfit(OutputBackend backend, RecastMode recastMode,
        bool inert)
    {
        // A null record has no Inventory template, so only the backend decides here.
        new Settings { OutputBackend = backend, RecastMode = recastMode }
            .OutfitFieldIsInert(null).Should().Be(inert);
    }

    [Fact]
    public void Token_OldBoolStillReportsItsBackend()
    {
        new NpcToken { UseSkyPatcherMode = true }.GetEffectiveOutputBackend().Should().Be("SkyPatcher");
        new NpcToken { UseSkyPatcherMode = false }.GetEffectiveOutputBackend().Should().Be("ESP");
        new NpcToken().GetEffectiveOutputBackend().Should().BeNull();
        new NpcToken { OutputBackend = "Recast", UseSkyPatcherMode = true }.GetEffectiveOutputBackend()
            .Should().Be("Recast", "the new field wins over the legacy one");
    }
}
