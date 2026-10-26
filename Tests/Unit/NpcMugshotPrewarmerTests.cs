using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>The prewarmer's memory gate and opt-out defaults. The gate needs
/// max(4 GB, 25% of total) free.</summary>
public class NpcMugshotPrewarmerTests
{
    private const long GB = 1L << 30;

    [Theory]
    [InlineData(32, 20, true)]    // 12 GB free of 32: above 8 GB (25%)
    [InlineData(32, 25, false)]   // 7 GB free of 32: below 25%
    [InlineData(16, 11, true)]    // 5 GB free of 16: above 4 GB floor (25% = 4 GB)
    [InlineData(16, 12.5, false)] // 3.5 GB free: below the 4 GB floor
    [InlineData(8, 3.5, true)]    // 4.5 GB free of 8: floor dominates
    [InlineData(8, 4.5, false)]   // 3.5 GB free of 8
    public void HasHeadroom_RequiresFourGbAndAQuarterFree(double totalGb, double loadGb, bool expected)
        => NpcMugshotPrewarmer.HasHeadroom((long)(totalGb * GB), (long)(loadGb * GB)).Should().Be(expected);

    [Fact]
    public void HasHeadroom_UnknownTotalsAreRefused()
        => NpcMugshotPrewarmer.HasHeadroom(0, 0).Should().BeFalse();

    [Fact]
    public void WarmUps_DefaultOn_AndCanBeTurnedOff()
    {
        var cfg = new InternalMugshotSettings();
        cfg.PrewarmNextNpcMugshots.Should().BeTrue();
        cfg.WarmUpRendererAtStartup.Should().BeTrue();
    }
}
