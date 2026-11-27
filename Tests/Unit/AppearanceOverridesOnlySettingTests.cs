using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Persistence of the per-mod "Appearance records only" switch
/// (<see cref="ModSetting.AppearanceOverridesOnly"/>): off unless the user ticks it, including for
/// every mod entry in a settings file written before the option existed.
/// </summary>
public class AppearanceOverridesOnlySettingTests
{
    [Fact]
    public void IsOffByDefault() =>
        new ModSetting().AppearanceOverridesOnly.Should().BeFalse();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SurvivesTheSettingsRoundTrip(bool value)
    {
        var json = JSONhandler<ModSetting>.Serialize(
            new ModSetting { DisplayName = "Mod", AppearanceOverridesOnly = value }, out var serOk, out var serErr);
        serOk.Should().BeTrue("serialization should succeed: {0}", serErr);

        var clone = JSONhandler<ModSetting>.Deserialize(json, out var deOk, out var deErr);

        deOk.Should().BeTrue("deserialization should succeed: {0}", deErr);
        clone!.AppearanceOverridesOnly.Should().Be(value);
    }

    [Fact]
    public void AModEntryWrittenBeforeTheOptionExisted_LoadsWithItOff()
    {
        var clone = JSONhandler<ModSetting>.Deserialize(
            "{ \"DisplayName\": \"Old Mod\", \"IncludeAllOverrides\": true }", out var ok, out var err);

        ok.Should().BeTrue("deserialization should succeed: {0}", err);
        clone!.DisplayName.Should().Be("Old Mod");
        clone.IncludeAllOverrides.Should().BeTrue();
        clone.AppearanceOverridesOnly.Should().BeFalse();
    }
}
