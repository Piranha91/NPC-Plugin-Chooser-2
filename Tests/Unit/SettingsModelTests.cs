using System.IO;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="Settings"/> model: attire-flag resolution, the mugshot-folder default/effective
/// helpers, schema constant, and the defaults that downstream code relies on. Pure logic.
/// </summary>
public class SettingsModelTests
{
    private static readonly FormKey Npc = FormKey.Factory("000801:Skyrim.esm");

    // ── GetEffectiveAttireFlags ────────────────────────────────────────────────
    [Fact]
    public void AttireFlags_NoOverride_UsesGlobalInternalMugshot()
    {
        var s = new Settings();
        s.InternalMugshot.IncludeDefaultOutfit = true;
        s.InternalMugshot.IncludeHeadgear = true;
        s.GetEffectiveAttireFlags(Npc).Should().Be((true, true));
    }

    [Fact]
    public void AttireFlags_HeadgearSuppressedWhenOutfitOff()
    {
        var s = new Settings();
        s.InternalMugshot.IncludeDefaultOutfit = false;
        s.InternalMugshot.IncludeHeadgear = true;
        // Outfit is dominant: headgear can't render alone.
        s.GetEffectiveAttireFlags(Npc).Should().Be((false, false));
    }

    [Fact]
    public void AttireFlags_OverrideIgnoredWhenOverrideGlobalAttireFalse()
    {
        var s = new Settings();
        s.InternalMugshot.IncludeDefaultOutfit = true;
        s.NpcRenderOverrides[Npc] = new NpcRenderOverride
        {
            OverrideGlobalAttire = false, IncludeDefaultOutfit = false, IncludeHeadgear = false,
        };
        s.GetEffectiveAttireFlags(Npc).Should().Be((true, false), "override is inert until enabled");
    }

    [Fact]
    public void AttireFlags_PerNpcOverrideWinsWhenEnabled()
    {
        var s = new Settings();
        s.InternalMugshot.IncludeDefaultOutfit = false;
        s.NpcRenderOverrides[Npc] = new NpcRenderOverride
        {
            OverrideGlobalAttire = true, IncludeDefaultOutfit = true, IncludeHeadgear = true,
        };
        s.GetEffectiveAttireFlags(Npc).Should().Be((true, true));
    }

    [Fact]
    public void AttireFlags_NullFormKey_FallsThroughToGlobal()
    {
        var s = new Settings();
        s.InternalMugshot.IncludeDefaultOutfit = true;
        s.InternalMugshot.IncludeHeadgear = false;
        s.GetEffectiveAttireFlags(FormKey.Null).Should().Be((true, false));
    }

    // ── Mugshot folder helpers ─────────────────────────────────────────────────
    [Fact]
    public void DefaultFaceFinderFolder_IsUnderBaseDir()
    {
        Settings.GetDefaultFaceFinderMugshotsFolder()
            .Should().Be(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FaceFinder Cache"));
    }

    [Fact]
    public void DefaultAutogenFolder_IsUnderBaseDir()
    {
        Settings.GetDefaultAutogenMugshotsFolder()
            .Should().Be(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AutoGen Mugshots"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EffectiveFaceFinderFolder_BlankFallsBackToDefault(string? configured)
    {
        var s = new Settings { FaceFinderMugshotsFolder = configured! };
        Settings.GetEffectiveFaceFinderMugshotsFolder(s)
            .Should().Be(Settings.GetDefaultFaceFinderMugshotsFolder());
    }

    [Fact]
    public void EffectiveFaceFinderFolder_CustomIsReturned()
    {
        var s = new Settings { FaceFinderMugshotsFolder = @"C:\Custom\FF" };
        Settings.GetEffectiveFaceFinderMugshotsFolder(s).Should().Be(@"C:\Custom\FF");
    }

    [Fact]
    public void EffectiveAutogenFolder_CustomElseDefault()
    {
        Settings.GetEffectiveAutogenMugshotsFolder(new Settings { AutogenMugshotsFolder = @"C:\A" })
            .Should().Be(@"C:\A");
        Settings.GetEffectiveAutogenMugshotsFolder(new Settings { AutogenMugshotsFolder = "  " })
            .Should().Be(Settings.GetDefaultAutogenMugshotsFolder());
    }

    // ── Defaults ───────────────────────────────────────────────────────────────
    [Fact]
    public void SchemaVersionConstant_Is7()
    {
        Settings.CurrentSchemaVersion.Should().Be(7);
    }

    [Fact]
    public void NewSettings_HasExpectedDefaults()
    {
        var s = new Settings();
        s.SchemaVersion.Should().Be(-1, "sentinel for pre-upgrade migration");
        s.PatchingMode.Should().Be(PatchingMode.CreateAndPatch);
        s.OutputDirectory.Should().Be("NPC Output");
        s.SkyrimRelease.Should().Be(SkyrimRelease.SkyrimSE);
        s.DefaultRecordOverrideHandlingMode.Should().Be(RecordOverrideHandlingMode.Ignore);
        s.DefaultMaxNestedIntervalDepth.Should().Be(2);
        s.MaxMugshotsToFit.Should().Be(50);
        s.AutoEslIfy.Should().BeTrue();
        s.AutoSplitOutput.Should().BeTrue();
        s.EasyNpcDefaultPluginExclusions.Should().Contain(ModKey.FromFileName("Synthesis.esp"));
        s.InternalMugshot.Should().NotBeNull();
        s.ModSettings.Should().NotBeNull().And.BeEmpty();
        s.SelectedAppearanceMods.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void MugshotSourcePriority_DefaultOrder()
    {
        new Settings().MugshotSourcePriority.Should().Equal(
            MugshotSourceType.DownloadedMugshots,
            MugshotSourceType.FaceFinder,
            MugshotSourceType.AutoGeneration);
    }

    [Fact]
    public void InternalMugshotSettings_PortraitTogglesDefaultOn()
    {
        var m = new InternalMugshotSettings();
        m.EnableToneMapping.Should().BeTrue();
        m.EnableShadows.Should().BeTrue();
        m.EnableAmbientOcclusion.Should().BeTrue();
        m.EnableEyeCatchlight.Should().BeTrue();
        m.SubsurfaceStrength.Should().Be(1.0f);
        m.VignetteRadius.Should().Be(0.7f);
        m.VignetteIntensity.Should().Be(0.3f);
        m.IncludeDefaultOutfit.Should().BeTrue();
        m.IncludeHeadgear.Should().BeFalse();
    }

    [Fact]
    public void NpcRenderOverride_DefaultsAllFalse()
    {
        var o = new NpcRenderOverride();
        o.OverrideGlobalAttire.Should().BeFalse();
        o.IncludeDefaultOutfit.Should().BeFalse();
        o.IncludeHeadgear.Should().BeFalse();
    }
}
