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
    private static readonly FormKey AntlerKey = MutagenFixtures.Fk("000A0C:FoxGloveAuri.esp");

    private static ModSetting ModWithWig(WigHandlingMode? perMod = null) => new()
    {
        DisplayName = "FoxGlove",
        DetectedWigArmors = { WigKey },
        ModWigHandlingMode = perMod,
    };

    private static ModSetting ModWithAntler(AntlerHandlingMode? perMod = null) => new()
    {
        DisplayName = "FoxGlove",
        DetectedAntlerArmors = { AntlerKey },
        ModAntlerHandlingMode = perMod,
    };

    private static Settings NewSettings(PatchingMode mode, bool skyPatcher = false,
        WigHandlingMode globalDefault = WigHandlingMode.ForwardToSkin,
        AntlerHandlingMode antlerDefault = AntlerHandlingMode.ForwardToSkin) => new()
    {
        PatchingMode = mode,
        UseSkyPatcherMode = skyPatcher,
        DefaultWigHandlingMode = globalDefault,
        DefaultAntlerHandlingMode = antlerDefault,
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
    public void ModSetting_DetectionFlags_ReflectTheirOwnSets()
    {
        new ModSetting().HasWigArmors.Should().BeFalse();
        new ModSetting().HasAntlers.Should().BeFalse();

        new ModSetting { DetectedWigArmors = { WigKey } }.HasWigArmors.Should().BeTrue();
        new ModSetting { DetectedWigArmors = { WigKey } }.HasAntlers.Should().BeFalse();

        // Antlers count from any of the three sources.
        new ModSetting { DetectedAntlerArmors = { AntlerKey } }.HasAntlers.Should().BeTrue();
        new ModSetting { DetectedAntlerArmatures = { AntlerKey } }.HasAntlers.Should().BeTrue();
        new ModSetting { DetectedAntlerHeadParts = { AntlerKey } }.HasAntlers.Should().BeTrue();
        new ModSetting { DetectedAntlerArmors = { AntlerKey } }.HasWigArmors.Should().BeFalse();
    }

    // ── Antler mode resolution (mirrors the wig matrix; independent gating) ──

    [Fact]
    public void Antler_NoDetections_IsAlwaysNone()
    {
        var settings = NewSettings(PatchingMode.CreateAndPatch);
        settings.GetEffectiveAntlerMode(new ModSetting { DisplayName = "Plain" })
            .Should().Be(AntlerHandlingMode.None);
        settings.GetEffectiveAntlerMode(null).Should().Be(AntlerHandlingMode.None);
        // A wig-only mod resolves to no antler handling, and vice versa.
        settings.GetEffectiveAntlerMode(ModWithWig()).Should().Be(AntlerHandlingMode.None);
        settings.GetEffectiveWigMode(ModWithAntler()).Should().Be(WigHandlingMode.None);
    }

    [Fact]
    public void Antler_PlainCreateRecordMode_IsInert()
    {
        NewSettings(PatchingMode.Create).GetEffectiveAntlerMode(ModWithAntler())
            .Should().Be(AntlerHandlingMode.None);
    }

    [Fact]
    public void Antler_CreateAndPatch_UsesGlobalDefault_WhenPerModIsNull()
    {
        NewSettings(PatchingMode.CreateAndPatch, antlerDefault: AntlerHandlingMode.Remove)
            .GetEffectiveAntlerMode(ModWithAntler())
            .Should().Be(AntlerHandlingMode.Remove);
    }

    [Fact]
    public void Antler_PerModOverride_BeatsGlobalDefault()
    {
        var settings = NewSettings(PatchingMode.CreateAndPatch, antlerDefault: AntlerHandlingMode.ForwardToSkin);
        settings.GetEffectiveAntlerMode(ModWithAntler(AntlerHandlingMode.Remove)).Should().Be(AntlerHandlingMode.Remove);
        settings.GetEffectiveAntlerMode(ModWithAntler(AntlerHandlingMode.None)).Should().Be(AntlerHandlingMode.None);
    }

    [Fact]
    public void Antler_DefaultsToForwardToSkin_PreservingPreSplitBehavior()
    {
        NewSettings(PatchingMode.CreateAndPatch).GetEffectiveAntlerMode(ModWithAntler())
            .Should().Be(AntlerHandlingMode.ForwardToSkin);
    }

    [Fact]
    public void Antler_RenderOverride_ForcesDepictedMode_OnlyWithDetections()
    {
        var settings = NewSettings(PatchingMode.Create); // patch-side inert
        settings.InternalMugshot.AntlerModeOverride = AntlerHandlingMode.Remove;

        settings.GetEffectiveRenderAntlerMode(ModWithAntler()).Should().Be(AntlerHandlingMode.Remove,
            "the harness override wins regardless of the output-mode gate");
        settings.GetEffectiveAntlerMode(ModWithAntler()).Should().Be(AntlerHandlingMode.None,
            "the override must never leak into the patcher");
        settings.GetEffectiveRenderAntlerMode(new ModSetting { DisplayName = "Plain" })
            .Should().Be(AntlerHandlingMode.None, "detection is still required");
    }

    [Fact]
    public void WigOrAntlerHandlingActive_TrueWhenEitherClassActs()
    {
        var settings = NewSettings(PatchingMode.CreateAndPatch);
        settings.WigOrAntlerHandlingActive(ModWithWig()).Should().BeTrue();
        settings.WigOrAntlerHandlingActive(ModWithAntler()).Should().BeTrue();
        settings.WigOrAntlerHandlingActive(new ModSetting { DisplayName = "Plain" }).Should().BeFalse();

        // A mod with an antler set to None (and no wig) is inert.
        settings.WigOrAntlerHandlingActive(ModWithAntler(AntlerHandlingMode.None)).Should().BeFalse();

        // Plain Create record mode: inert even with detections.
        NewSettings(PatchingMode.Create).WigOrAntlerHandlingActive(ModWithAntler()).Should().BeFalse();
    }

    // ── Manually-designated antler head parts (the "Set Antler Head Parts" selector) ──

    [Fact]
    public void ManualAntlerHeadParts_ActivateHandling_ForAScanUndetectedMod()
    {
        var headPart = MutagenFixtures.Fk("00A1B2:FoxGloveAuri.esp");
        var settings = NewSettings(PatchingMode.CreateAndPatch);
        var mod = new ModSetting { DisplayName = "FoxGlove" }; // no keyword detection at all

        settings.ModHasAntlers(mod).Should().BeFalse();
        settings.GetEffectiveAntlerHeadParts(mod).Should().BeEmpty();
        settings.GetEffectiveAntlerMode(mod).Should().Be(AntlerHandlingMode.None);

        // The user designates a head part (keyed by mod name on the root Settings).
        settings.ManualAntlerHeadPartsByMod["FoxGlove"] = new HashSet<FormKey> { headPart };

        settings.ModHasAntlers(mod).Should().BeTrue("a manual designation counts as having antlers");
        settings.GetEffectiveAntlerHeadParts(mod).Should().Contain(headPart);
        settings.GetEffectiveAntlerMode(mod).Should().Be(AntlerHandlingMode.ForwardToSkin,
            "manual-only mod resolves to the global default until a per-mod mode is set");

        mod.ModAntlerHandlingMode = AntlerHandlingMode.Remove;
        settings.GetEffectiveAntlerMode(mod).Should().Be(AntlerHandlingMode.Remove);
    }

    [Fact]
    public void EffectiveAntlerHeadParts_UnionsDetectedAndManual_ByModName()
    {
        var detected = MutagenFixtures.Fk("000111:M.esp");
        var manual = MutagenFixtures.Fk("000222:M.esp");
        var settings = NewSettings(PatchingMode.CreateAndPatch);
        var mod = new ModSetting { DisplayName = "M", DetectedAntlerHeadParts = { detected } };
        settings.ManualAntlerHeadPartsByMod["M"] = new HashSet<FormKey> { manual };

        settings.GetEffectiveAntlerHeadParts(mod).Should().BeEquivalentTo(new[] { detected, manual });
        // Manual entries are keyed by DisplayName — a different mod isn't affected.
        settings.GetEffectiveAntlerHeadParts(new ModSetting { DisplayName = "Other" }).Should().BeEmpty();
    }
}
