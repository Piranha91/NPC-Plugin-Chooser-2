using System.Windows.Media;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// <see cref="UpdateHandler"/> per-version DATA migration steps. Only the steps that
/// mutate a <see cref="Settings"/> WITHOUT showing a dialog or needing DI services are
/// exercised here: <c>UpdateTo2_1_7_Initial</c>, <c>UpdateTo2_2_1_Initial</c> (both
/// SubsurfaceStrength tweaks), <c>UpdateTo2_0_7_Initial</c> (Portrait Creator defaults
/// reset, reachable dialog-free while <c>UsePortraitCreatorFallback == false</c>), and the
/// new-user early return of <c>InitialCheckForUpdatesAndPatch</c> /
/// <c>FinalCheckForUpdatesAndPatch</c>. The private steps are reached via
/// <see cref="Reflect"/>. No live Skyrim install or clock is touched.
///
/// NOTE: UpdateTo2_0_4_Initial not covered: it unconditionally calls
///   ScrollableMessageBox.Show (a WPF dialog), so it can't run headless.
/// NOTE: All *_Final steps + MaybeMoveLegacyMugshotFiles_Initial not covered: they
///   require VM_Mods / VM_NpcSelectionBar / PluginProvider / EnvironmentStateProvider
///   (a live load order) and/or show dialogs — those belong to the integration wave.
///   Exception: the GATE around <c>UpdateTo2_2_2_Final</c> (version + one-shot
///   <c>HasUpdatedTo2_2_2</c> flag) is exercised below via null-service probes.
/// </summary>
public class UpdateHandlerMigrationTests
{
    private static UpdateHandler Make(Settings s) => new(s);

    // ---- UpdateTo2_1_7_Initial : SubsurfaceStrength 2.0f -> 0.1f ----------------------

    [Fact]
    public void UpdateTo2_1_7_Initial_PriorDefault_RevertsToPointOne()
    {
        var s = new Settings();
        s.InternalMugshot.SubsurfaceStrength = 2.0f;

        Reflect.InvokeVoid(Make(s), "UpdateTo2_1_7_Initial");

        s.InternalMugshot.SubsurfaceStrength.Should().Be(0.1f);
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(0.5f)]
    [InlineData(0.0f)]
    [InlineData(1.0f)]
    [InlineData(2.0001f)]
    [InlineData(1.9999f)]
    public void UpdateTo2_1_7_Initial_TunedValue_LeftUntouched(float tuned)
    {
        var s = new Settings();
        s.InternalMugshot.SubsurfaceStrength = tuned;

        Reflect.InvokeVoid(Make(s), "UpdateTo2_1_7_Initial");

        // Strict equality — the migration's "untouched" signal is literal 2.0f only.
        s.InternalMugshot.SubsurfaceStrength.Should().Be(tuned);
    }

    [Fact]
    public void UpdateTo2_1_7_Initial_IsIdempotent()
    {
        var s = new Settings();
        s.InternalMugshot.SubsurfaceStrength = 2.0f;
        var handler = Make(s);

        Reflect.InvokeVoid(handler, "UpdateTo2_1_7_Initial");
        Reflect.InvokeVoid(handler, "UpdateTo2_1_7_Initial");

        // After the first run the value is 0.1, which no longer matches 2.0f, so the
        // second run is a no-op (it does NOT cascade into the 2.2.1 step).
        s.InternalMugshot.SubsurfaceStrength.Should().Be(0.1f);
    }

    [Fact]
    public void UpdateTo2_1_7_Initial_TouchesOnlySubsurfaceStrength()
    {
        var s = new Settings();
        s.InternalMugshot.SubsurfaceStrength = 2.0f;
        // Sample a few unrelated InternalMugshot fields to prove they're left alone.
        s.InternalMugshot.EnableToneMapping = true;
        s.InternalMugshot.Exposure = 1.0f;
        s.InternalMugshot.BloomIntensity = 0.7f;

        Reflect.InvokeVoid(Make(s), "UpdateTo2_1_7_Initial");

        s.InternalMugshot.EnableToneMapping.Should().BeTrue();
        s.InternalMugshot.Exposure.Should().Be(1.0f);
        s.InternalMugshot.BloomIntensity.Should().Be(0.7f);
    }

    // ---- UpdateTo2_2_1_Initial : SubsurfaceStrength 0.1f -> 1.0f ----------------------

    [Fact]
    public void UpdateTo2_2_1_Initial_PriorDefault_BumpsToOne()
    {
        var s = new Settings();
        s.InternalMugshot.SubsurfaceStrength = 0.1f;

        Reflect.InvokeVoid(Make(s), "UpdateTo2_2_1_Initial");

        s.InternalMugshot.SubsurfaceStrength.Should().Be(1.0f);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.5f)]
    [InlineData(2.0f)]
    [InlineData(0.0f)]
    [InlineData(0.2f)]
    [InlineData(0.09999f)]
    public void UpdateTo2_2_1_Initial_TunedValue_LeftUntouched(float tuned)
    {
        var s = new Settings();
        s.InternalMugshot.SubsurfaceStrength = tuned;

        Reflect.InvokeVoid(Make(s), "UpdateTo2_2_1_Initial");

        s.InternalMugshot.SubsurfaceStrength.Should().Be(tuned);
    }

    [Fact]
    public void UpdateTo2_2_1_Initial_IsIdempotent()
    {
        var s = new Settings();
        s.InternalMugshot.SubsurfaceStrength = 0.1f;
        var handler = Make(s);

        Reflect.InvokeVoid(handler, "UpdateTo2_2_1_Initial");
        Reflect.InvokeVoid(handler, "UpdateTo2_2_1_Initial");

        s.InternalMugshot.SubsurfaceStrength.Should().Be(1.0f);
    }

    // ---- Chaining the two SSS migrations: 2.0 -> 0.1 -> 1.0 --------------------------

    [Fact]
    public void Chained_2_1_7_Then_2_2_1_FromPrior2_0_LandsAtOne()
    {
        // A user jumping across both versions runs 2.1.7 (2.0 -> 0.1) and THEN 2.2.1
        // (0.1 -> 1.0) in order, ending at 1.0.
        var s = new Settings();
        s.InternalMugshot.SubsurfaceStrength = 2.0f;
        var handler = Make(s);

        Reflect.InvokeVoid(handler, "UpdateTo2_1_7_Initial");
        s.InternalMugshot.SubsurfaceStrength.Should().Be(0.1f);

        Reflect.InvokeVoid(handler, "UpdateTo2_2_1_Initial");
        s.InternalMugshot.SubsurfaceStrength.Should().Be(1.0f);
    }

    [Fact]
    public void Chained_OrderMatters_2_2_1_BeforeNothing_NoOpFrom2_0()
    {
        // Running 2.2.1 alone on a value of 2.0 (i.e. a user who somehow only triggers
        // the later step) is a no-op: 2.0 != 0.1.
        var s = new Settings();
        s.InternalMugshot.SubsurfaceStrength = 2.0f;

        Reflect.InvokeVoid(Make(s), "UpdateTo2_2_1_Initial");

        s.InternalMugshot.SubsurfaceStrength.Should().Be(2.0f);
    }

    // ---- UpdateTo2_0_7_Initial : Portrait Creator defaults reset ---------------------
    // Dialog-free path: with UsePortraitCreatorFallback == false (the Settings default),
    // shouldReset stays true and no ScrollableMessageBox.Confirm is shown.

    [Fact]
    public void UpdateTo2_0_7_Initial_ResetsCoreFields_AndMarksFlag()
    {
        var s = new Settings();
        s.UsePortraitCreatorFallback.Should().BeFalse("the dialog-free path requires this default");

        // Pre-set fields to non-default values to prove the migration overwrites them.
        s.MugshotBackgroundColor = Colors.Black;
        s.VerticalFOV = 99f;
        s.HeadBottomOffset = 0.42f;
        s.CamYaw = 12.5f;
        s.SelectedCameraMode = PortraitCameraMode.Fixed;
        s.EnableNormalMapHack = false;
        s.HasUpdatedTo2_0_7 = false;

        Reflect.InvokeVoid(Make(s), "UpdateTo2_0_7_Initial");

        s.MugshotBackgroundColor.Should().Be(Color.FromRgb(58, 61, 64));
        s.VerticalFOV.Should().Be(25f);
        s.HeadBottomOffset.Should().Be(-0.05f);
        s.CamYaw.Should().Be(90.0f);
        s.SelectedCameraMode.Should().Be(PortraitCameraMode.Portrait);
        s.EnableNormalMapHack.Should().BeTrue();
        s.HasUpdatedTo2_0_7.Should().BeTrue();
    }

    [Fact]
    public void UpdateTo2_0_7_Initial_ResetsRemainingCameraAndOffsetFields()
    {
        var s = new Settings();
        s.HeadTopOffset = 9f;
        s.CamPitch = 9f;
        s.CamRoll = 9f;
        s.CamX = 9f;
        s.CamY = 9f;
        s.CamZ = 9f;

        Reflect.InvokeVoid(Make(s), "UpdateTo2_0_7_Initial");

        s.HeadTopOffset.Should().Be(0.0f);
        s.CamPitch.Should().Be(2.0f);
        s.CamRoll.Should().Be(0.0f);
        s.CamX.Should().Be(0.0f);
        s.CamY.Should().Be(0.0f);
        s.CamZ.Should().Be(0.0f);
    }

    [Fact]
    public void UpdateTo2_0_7_Initial_SetsNonEmptyDefaultLightingJson()
    {
        var s = new Settings();
        s.DefaultLightingJsonString = string.Empty;

        Reflect.InvokeVoid(Make(s), "UpdateTo2_0_7_Initial");

        s.DefaultLightingJsonString.Should().NotBeNullOrWhiteSpace();
        // The reset writes a lights array; sanity-check a couple of structural tokens.
        s.DefaultLightingJsonString.Should().Contain("\"lights\"");
        s.DefaultLightingJsonString.Should().Contain("ambient");
        s.DefaultLightingJsonString.Should().Contain("directional");
    }

    [Fact]
    public void UpdateTo2_0_7_Initial_AlwaysMarksFlag_EvenWhenAlreadyTrue()
    {
        var s = new Settings();
        s.HasUpdatedTo2_0_7 = true;

        Reflect.InvokeVoid(Make(s), "UpdateTo2_0_7_Initial");

        s.HasUpdatedTo2_0_7.Should().BeTrue();
    }

    [Fact]
    public void UpdateTo2_0_7_Initial_DoesNotTouchSubsurfaceStrength()
    {
        // The 2.0.7 reset is orthogonal to the later SSS migrations.
        var s = new Settings();
        s.InternalMugshot.SubsurfaceStrength = 1.23f;

        Reflect.InvokeVoid(Make(s), "UpdateTo2_0_7_Initial");

        s.InternalMugshot.SubsurfaceStrength.Should().Be(1.23f);
    }

    // ---- InitialCheckForUpdatesAndPatch : new-user early return ----------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InitialCheck_EmptyOrWhitespaceProgramVersion_EarlyReturns_NoMutation(string version)
    {
        var s = new Settings { ProgramVersion = version };
        // Seed values the various migrations WOULD overwrite if they ran, to prove the
        // new-user fast path skips all of them (no dialogs, no DI).
        s.InternalMugshot.SubsurfaceStrength = 2.0f;
        s.HasUpdatedTo2_0_7 = false;
        s.VerticalFOV = 99f;

        // splashReporter is optional (defaults to null); empty version returns before any
        // version comparison or migration runs.
        await Make(s).InitialCheckForUpdatesAndPatch();

        s.InternalMugshot.SubsurfaceStrength.Should().Be(2.0f, "no migration ran for a new user");
        s.HasUpdatedTo2_0_7.Should().BeFalse();
        s.VerticalFOV.Should().Be(99f);
    }

    // ---- FinalCheckForUpdatesAndPatch : new-user early return ------------------------
    // Empty ProgramVersion returns at the top, before any of the (non-null typed) service
    // arguments are dereferenced, so passing null! is safe and exercises only the guard.

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task FinalCheck_EmptyOrWhitespaceProgramVersion_EarlyReturns_NoMutation(string version)
    {
        var s = new Settings { ProgramVersion = version };
        s.InternalMugshot.SubsurfaceStrength = 2.0f;

        await Make(s).FinalCheckForUpdatesAndPatch(
            npcsVm: null!,
            modsVm: null!,
            pluginProvider: null!,
            aux: null!,
            environmentStateProvider: null!,
            splashReporter: null);

        s.InternalMugshot.SubsurfaceStrength.Should().Be(2.0f);
    }

    // ---- FinalCheckForUpdatesAndPatch : 2.2.2 base-game-asset scan gate ---------------
    // UpdateTo2_2_2_Final itself needs a live VM_Mods (integration wave), but its GATE is
    // testable headless: with ProgramVersion >= 2.1.7 every earlier *_Final gate is closed,
    // so whether the null modsVm gets dereferenced tells us whether the 2.2.2 gate opened.

    [Fact]
    public async Task FinalCheck_From2_2_1_FlagAlreadySet_SkipsBaseGameScan()
    {
        var s = new Settings { ProgramVersion = "2.2.1", HasUpdatedTo2_2_2 = true };

        // Gate closed by the one-shot flag: completes without touching the null services.
        await Make(s).FinalCheckForUpdatesAndPatch(
            npcsVm: null!, modsVm: null!, pluginProvider: null!, aux: null!,
            environmentStateProvider: null!, splashReporter: null);

        s.HasUpdatedTo2_2_2.Should().BeTrue();
    }

    [Fact]
    public async Task FinalCheck_From2_2_2_OrNewer_SkipsBaseGameScan()
    {
        var s = new Settings { ProgramVersion = "2.2.2", HasUpdatedTo2_2_2 = false };

        await Make(s).FinalCheckForUpdatesAndPatch(
            npcsVm: null!, modsVm: null!, pluginProvider: null!, aux: null!,
            environmentStateProvider: null!, splashReporter: null);

        // Gate closed by version: the migration didn't run, so the flag stays false.
        s.HasUpdatedTo2_2_2.Should().BeFalse();
    }

    [Fact]
    public async Task FinalCheck_From2_2_1_WithoutFlag_OpensBaseGameScanGate()
    {
        var s = new Settings { ProgramVersion = "2.2.1", HasUpdatedTo2_2_2 = false };

        // Gate-open probe: UpdateTo2_2_2_Final immediately enumerates modsVm.AllModSettings,
        // so the null modsVm faulting proves the gate fired (the real scan belongs to the
        // integration wave).
        var act = () => Make(s).FinalCheckForUpdatesAndPatch(
            npcsVm: null!, modsVm: null!, pluginProvider: null!, aux: null!,
            environmentStateProvider: null!, splashReporter: null);

        await act.Should().ThrowAsync<NullReferenceException>();
    }

    // ---- Constructor sanity ----------------------------------------------------------

    [Fact]
    public void Ctor_AcceptsSettings_AndDoesNotMutateThem()
    {
        var s = new Settings();
        s.InternalMugshot.SubsurfaceStrength = 0.1f;
        s.ProgramVersion = "2.0.0";

        var handler = Make(s);

        handler.Should().NotBeNull();
        // Construction alone runs no migration.
        s.InternalMugshot.SubsurfaceStrength.Should().Be(0.1f);
        s.ProgramVersion.Should().Be("2.0.0");
    }
}
