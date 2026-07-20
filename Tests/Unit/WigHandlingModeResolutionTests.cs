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

    private static readonly FormKey NpcA = MutagenFixtures.Fk("000D62:018Auri.esp");
    private static readonly FormKey NpcB = MutagenFixtures.Fk("000E71:018Auri.esp");

    [Fact]
    public void ManualDesignation_ActivatesHandling_AndResolvesMode_ForAScanUndetectedMod()
    {
        var settings = NewSettings(PatchingMode.CreateAndPatch);
        var mod = new ModSetting { DisplayName = "FoxGlove" }; // no keyword detection at all

        settings.ModHasAntlers(mod).Should().BeFalse();
        settings.GetEffectiveAntlerMode(mod).Should().Be(AntlerHandlingMode.None);

        // The user designates head part "Antler01" on NpcA in FoxGlove.
        settings.AddManualAntlerHeadPart("Antler01", "FoxGlove", NpcA);

        settings.ModHasAntlers(mod).Should().BeTrue("a designation made in the mod counts as having antlers");
        settings.GetEffectiveAntlerMode(mod).Should().Be(AntlerHandlingMode.ForwardToSkin,
            "manual-only mod resolves to the global default until a per-mod mode is set");

        // IsAntlerHeadPart matches the designated EditorID (eligibility; removal still needs Remove).
        settings.IsAntlerHeadPart(mod, MutagenFixtures.Fk("000111:M.esp"), "Antler01", NpcA).Should().BeTrue();
        settings.IsAntlerHeadPart(mod, MutagenFixtures.Fk("000111:M.esp"), "NotAnAntler", NpcA).Should().BeFalse();

        mod.ModAntlerHandlingMode = AntlerHandlingMode.Remove;
        settings.GetEffectiveAntlerMode(mod).Should().Be(AntlerHandlingMode.Remove);
    }

    [Fact]
    public void IsAntlerHeadPart_UnionsDetected_AndManual()
    {
        var detected = MutagenFixtures.Fk("000111:M.esp");
        var settings = NewSettings(PatchingMode.CreateAndPatch);
        var mod = new ModSetting { DisplayName = "M", DetectedAntlerHeadParts = { detected } };
        settings.AddManualAntlerHeadPart("ManualAntler", "M", NpcA);

        settings.IsAntlerHeadPart(mod, detected, "AnyEid", NpcA).Should().BeTrue("keyword-detected by FormKey");
        settings.IsAntlerHeadPart(mod, MutagenFixtures.Fk("000999:M.esp"), "ManualAntler", NpcA)
            .Should().BeTrue("manually designated by EditorID");
    }

    [Fact]
    public void BlockScope_AllNpcs_BlocksTheEditorIdEverywhere()
    {
        var settings = NewSettings(PatchingMode.CreateAndPatch);
        settings.ManualAntlerBlockScope = AntlerBlockScope.AllNpcs;
        settings.AddManualAntlerHeadPart("Antler01", "FoxGlove", NpcA);

        settings.IsManualAntlerHeadPart("Antler01", "FoxGlove", NpcA).Should().BeTrue();
        settings.IsManualAntlerHeadPart("Antler01", "OtherMod", NpcB).Should().BeTrue("All NPCs = any mod, any NPC");
        settings.IsManualAntlerHeadPart("antler01", "OtherMod", NpcB).Should().BeTrue("EditorID match is case-insensitive");
    }

    [Fact]
    public void BlockScope_SameMod_BlocksOnlyWithinTheDesignatingMod()
    {
        var settings = NewSettings(PatchingMode.CreateAndPatch);
        settings.ManualAntlerBlockScope = AntlerBlockScope.SameMod;
        settings.AddManualAntlerHeadPart("Antler01", "FoxGlove", NpcA);

        settings.IsManualAntlerHeadPart("Antler01", "FoxGlove", NpcB).Should().BeTrue("same mod, any NPC");
        settings.IsManualAntlerHeadPart("Antler01", "OtherMod", NpcA).Should().BeFalse("different mod");
    }

    [Fact]
    public void BlockScope_SpecificNpc_BlocksOnlyOnTheDesignatedNpc()
    {
        var settings = NewSettings(PatchingMode.CreateAndPatch);
        settings.ManualAntlerBlockScope = AntlerBlockScope.SpecificNpc;
        settings.AddManualAntlerHeadPart("Antler01", "FoxGlove", NpcA);

        settings.IsManualAntlerHeadPart("Antler01", "OtherMod", NpcA).Should().BeTrue("same NPC, regardless of source mod");
        settings.IsManualAntlerHeadPart("Antler01", "FoxGlove", NpcB).Should().BeFalse("different NPC");
    }

    [Fact]
    public void RemoveManualAntlerHeadPart_DropsOnlyThatSource()
    {
        var settings = NewSettings(PatchingMode.CreateAndPatch);
        settings.ManualAntlerBlockScope = AntlerBlockScope.SpecificNpc;
        settings.AddManualAntlerHeadPart("Antler01", "FoxGlove", NpcA);
        settings.AddManualAntlerHeadPart("Antler01", "FoxGlove", NpcB);

        settings.RemoveManualAntlerHeadPart("Antler01", "FoxGlove", NpcA);

        settings.IsManualAntlerHeadPart("Antler01", "FoxGlove", NpcA).Should().BeFalse("its source was removed");
        settings.IsManualAntlerHeadPart("Antler01", "FoxGlove", NpcB).Should().BeTrue("the other NPC's source survives");

        settings.RemoveManualAntlerHeadPart("Antler01", "FoxGlove", NpcB);
        settings.ManualAntlerHeadParts.Should().BeEmpty("the entry is dropped when its last source is gone");
    }
}
