using System.IO;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// <see cref="VM_Settings.LoadSettings"/> reads <c>Settings.json</c> from the app base directory
/// and runs schema-version migrations that flip newly-added pixel-affecting toggles to "legacy"
/// values on upgrade (so existing autogen mugshots aren't invalidated). These tests stage a
/// fixture at that path, assert the migrated result, and restore the original file. Serial
/// (process-global path); every fixture is valid JSON so the error-path warning dialog never fires.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class LoadSettingsMigrationTests
{
    private static string SettingsPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");

    /// <summary>Stages <paramref name="json"/> (or removes the file when null), runs LoadSettings,
    /// hands the result to <paramref name="assert"/>, then restores the pre-existing file.</summary>
    private static void WithSettingsJson(string? json, Action<Settings> assert)
    {
        var path = SettingsPath;
        var backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try
        {
            if (json == null) { if (File.Exists(path)) File.Delete(path); }
            else File.WriteAllText(path, json);

            assert(VM_Settings.LoadSettings());
        }
        finally
        {
            if (backup != null) File.WriteAllBytes(path, backup);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void NoFile_UsesDefaults_StampedAtCurrentSchema_NoMigrationFlips()
    {
        WithSettingsJson(null, s =>
        {
            s.SchemaVersion.Should().Be(Settings.CurrentSchemaVersion);
            // Fresh install keeps the new portrait look (no legacy flips).
            s.InternalMugshot.EnableToneMapping.Should().BeTrue();
            s.InternalMugshot.EnableShadows.Should().BeTrue();
            s.InternalMugshot.SubsurfaceStrength.Should().Be(1.0f);
        });
    }

    [Fact]
    public void SentinelSchema_RunsAllLegacyFlips()
    {
        // SchemaVersion -1 (a pre-upgrade Settings.json) => every migration step fires.
        WithSettingsJson("{\"SchemaVersion\": -1}", s =>
        {
            s.InternalMugshot.EnableToneMapping.Should().BeFalse();      // < 1
            s.InternalMugshot.EnableShadows.Should().BeFalse();          // < 2
            s.InternalMugshot.EnableAmbientOcclusion.Should().BeFalse(); // < 3
            s.InternalMugshot.EnableEyeCatchlight.Should().BeFalse();    // < 5
            s.InternalMugshot.SubsurfaceStrength.Should().Be(0f);        // < 6
            s.InternalMugshot.VignetteIntensity.Should().Be(0f);         // < 7
            s.SchemaVersion.Should().Be(Settings.CurrentSchemaVersion);
        });
    }

    [Fact]
    public void PartialSchema3_RunsOnlyLaterFlips()
    {
        WithSettingsJson("{\"SchemaVersion\": 3}", s =>
        {
            // < 1/<2/<3 do NOT run -> these keep the loaded (default-true) values.
            s.InternalMugshot.EnableToneMapping.Should().BeTrue();
            s.InternalMugshot.EnableShadows.Should().BeTrue();
            s.InternalMugshot.EnableAmbientOcclusion.Should().BeTrue();
            // < 5/<6/<7 DO run.
            s.InternalMugshot.EnableEyeCatchlight.Should().BeFalse();
            s.InternalMugshot.SubsurfaceStrength.Should().Be(0f);
            s.InternalMugshot.VignetteIntensity.Should().Be(0f);
            s.SchemaVersion.Should().Be(Settings.CurrentSchemaVersion);
        });
    }

    [Fact]
    public void CurrentSchema_NoFlips()
    {
        WithSettingsJson("{\"SchemaVersion\": 7}", s =>
        {
            s.InternalMugshot.EnableToneMapping.Should().BeTrue();
            s.InternalMugshot.SubsurfaceStrength.Should().Be(1.0f);
            s.InternalMugshot.VignetteIntensity.Should().Be(0.3f);
        });
    }

    [Fact]
    public void MugshotSourcePriority_BackfillsMissingEntriesAtTail()
    {
        WithSettingsJson("{\"SchemaVersion\": 7, \"MugshotSourcePriority\": [\"AutoGeneration\"]}", s =>
        {
            s.MugshotSourcePriority.Should().HaveCount(3);
            s.MugshotSourcePriority[0].Should().Be(MugshotSourceType.AutoGeneration, "the user's chosen first entry is preserved");
            s.MugshotSourcePriority.Should().Contain(MugshotSourceType.DownloadedMugshots)
                .And.Contain(MugshotSourceType.FaceFinder);
            s.MugshotSourcePriority.Should().NotContain(MugshotSourceType.None);
        });
    }

    [Fact]
    public void MugshotSourcePriority_NullOrEmpty_GetsDefaultOrder()
    {
        WithSettingsJson("{\"SchemaVersion\": 7, \"MugshotSourcePriority\": []}", s =>
        {
            s.MugshotSourcePriority.Should().Equal(
                MugshotSourceType.DownloadedMugshots,
                MugshotSourceType.FaceFinder,
                MugshotSourceType.AutoGeneration);
        });
    }

    [Fact]
    public void NullOutputPluginName_DefaultsToNpcEsp()
    {
        WithSettingsJson("{\"SchemaVersion\": 7, \"OutputPluginName\": null}", s =>
        {
            s.OutputPluginName.Should().Be("NPC.esp");
        });
    }

    [Fact]
    public void NullEasyNpcExclusions_DefaultsToSynthesis()
    {
        WithSettingsJson("{\"SchemaVersion\": 7, \"EasyNpcDefaultPluginExclusions\": null}", s =>
        {
            s.EasyNpcDefaultPluginExclusions.Should().Contain(ModKey.FromFileName("Synthesis.esp"));
        });
    }
}
