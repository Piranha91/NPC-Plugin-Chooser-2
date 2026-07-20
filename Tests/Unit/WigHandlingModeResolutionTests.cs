using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Locks the effective-mode resolution matrix shared by the patcher
/// (<see cref="Settings.GetEffectiveWigMode"/>) and the renderer/staleness
/// side (<see cref="Settings.GetEffectiveRenderWigMode"/>): active in
/// Create-and-Patch record mode and in SkyPatcher mode (either PatchingMode),
/// inert in plain Create record mode or without detections; per-mod override
/// beats the global default; the harness-only render override forces the
/// depicted mode when (and only when) the mod has detections.
/// </summary>
public class WigHandlingModeResolutionTests
{
    private static readonly FormKey WigKey = MutagenFixtures.Fk("000808:FoxGloveAuri.esp");

    private static ModSetting ModWithWig(WigHandlingMode? perMod = null) => new()
    {
        DisplayName = "FoxGlove",
        DetectedWigArmors = { WigKey },
        ModWigHandlingMode = perMod,
    };

    private static Settings NewSettings(PatchingMode mode, bool skyPatcher = false,
        WigHandlingMode globalDefault = WigHandlingMode.ForwardToSkin) => new()
    {
        PatchingMode = mode,
        UseSkyPatcherMode = skyPatcher,
        DefaultWigHandlingMode = globalDefault,
    };

    [Fact]
    public void NoDetections_IsAlwaysNone()
    {
        var settings = NewSettings(PatchingMode.CreateAndPatch);
        var mod = new ModSetting { DisplayName = "Plain" };

        settings.GetEffectiveWigMode(mod).Should().Be(WigHandlingMode.None);
        settings.GetEffectiveWigMode(null).Should().Be(WigHandlingMode.None);
        settings.GetEffectiveRenderWigMode(mod).Should().Be(WigHandlingMode.None);
    }

    [Fact]
    public void PlainCreateRecordMode_IsInert()
    {
        NewSettings(PatchingMode.Create).GetEffectiveWigMode(ModWithWig())
            .Should().Be(WigHandlingMode.None);
    }

    [Fact]
    public void CreateAndPatch_UsesGlobalDefault_WhenPerModIsNull()
    {
        NewSettings(PatchingMode.CreateAndPatch, globalDefault: WigHandlingMode.ForwardToOutfit)
            .GetEffectiveWigMode(ModWithWig())
            .Should().Be(WigHandlingMode.ForwardToOutfit);
    }

    [Fact]
    public void PerModOverride_BeatsGlobalDefault()
    {
        var settings = NewSettings(PatchingMode.CreateAndPatch, globalDefault: WigHandlingMode.ForwardToSkin);

        settings.GetEffectiveWigMode(ModWithWig(WigHandlingMode.None)).Should().Be(WigHandlingMode.None);
        settings.GetEffectiveWigMode(ModWithWig(WigHandlingMode.ForwardToOutfit))
            .Should().Be(WigHandlingMode.ForwardToOutfit);
    }

    [Fact]
    public void SkyPatcherMode_ActivatesInEitherPatchingMode()
    {
        NewSettings(PatchingMode.Create, skyPatcher: true).GetEffectiveWigMode(ModWithWig())
            .Should().Be(WigHandlingMode.ForwardToSkin);
        NewSettings(PatchingMode.CreateAndPatch, skyPatcher: true).GetEffectiveWigMode(ModWithWig())
            .Should().Be(WigHandlingMode.ForwardToSkin);
    }

    [Fact]
    public void RenderOverride_ForcesDepictedMode_OnlyWithDetections()
    {
        var settings = NewSettings(PatchingMode.Create); // patch-side inert
        settings.InternalMugshot.WigModeOverride = WigHandlingMode.ForwardToSkin;

        settings.GetEffectiveRenderWigMode(ModWithWig()).Should().Be(WigHandlingMode.ForwardToSkin,
            "the harness override wins regardless of the output-mode gate");
        settings.GetEffectiveWigMode(ModWithWig()).Should().Be(WigHandlingMode.None,
            "the override must never leak into the patcher");
        settings.GetEffectiveRenderWigMode(new ModSetting { DisplayName = "Plain" })
            .Should().Be(WigHandlingMode.None, "detection is still required");
    }

    [Fact]
    public void RenderMode_MatchesPatchMode_WhenNoOverride()
    {
        var settings = NewSettings(PatchingMode.CreateAndPatch, globalDefault: WigHandlingMode.ForwardToOutfit);
        settings.GetEffectiveRenderWigMode(ModWithWig())
            .Should().Be(settings.GetEffectiveWigMode(ModWithWig()));
    }

    [Fact]
    public void ModSetting_HasWigs_ReflectsEitherDetectionSet()
    {
        new ModSetting().HasWigs.Should().BeFalse();
        new ModSetting { DetectedWigArmors = { WigKey } }.HasWigs.Should().BeTrue();
        new ModSetting { DetectedAntlerArmors = { WigKey } }.HasWigs.Should().BeTrue();
    }
}
