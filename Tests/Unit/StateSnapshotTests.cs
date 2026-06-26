using FluentAssertions;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="ModStateSnapshot.Equals"/> — the analysis-cache short-circuit. Pure logic.
/// </summary>
public class StateSnapshotTests
{
    private static FileSnapshot File(string name, long size, DateTime t) =>
        new() { FileName = name, FileSize = size, LastWriteTimeUtc = t };

    private static readonly DateTime T = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Equals_Null_IsFalse()
    {
        new ModStateSnapshot().Equals(null).Should().BeFalse();
    }

    [Fact]
    public void Equals_IdenticalSinglePlugin_IsTrue()
    {
        var a = new ModStateSnapshot { PluginSnapshots = { File("a.esp", 10, T) } };
        var b = new ModStateSnapshot { PluginSnapshots = { File("a.esp", 10, T) } };
        a.Equals(b).Should().BeTrue();
    }

    [Fact]
    public void Equals_FileNameCaseInsensitive()
    {
        var a = new ModStateSnapshot { PluginSnapshots = { File("A.ESP", 10, T) } };
        var b = new ModStateSnapshot { PluginSnapshots = { File("a.esp", 10, T) } };
        a.Equals(b).Should().BeTrue();
    }

    [Fact]
    public void Equals_FileSizeDiffers_IsFalse()
    {
        var a = new ModStateSnapshot { PluginSnapshots = { File("a.esp", 10, T) } };
        var b = new ModStateSnapshot { PluginSnapshots = { File("a.esp", 11, T) } };
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equals_TimestampDiffers_IsFalse()
    {
        var a = new ModStateSnapshot { PluginSnapshots = { File("a.esp", 10, T) } };
        var b = new ModStateSnapshot { PluginSnapshots = { File("a.esp", 10, T.AddSeconds(1)) } };
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equals_CountDiffers_ShortCircuitsFalse()
    {
        var a = new ModStateSnapshot { PluginSnapshots = { File("a.esp", 10, T) } };
        var b = new ModStateSnapshot();
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equals_ReorderedLists_IsTrue()
    {
        var a = new ModStateSnapshot { PluginSnapshots = { File("a.esp", 1, T), File("b.esp", 2, T) } };
        var b = new ModStateSnapshot { PluginSnapshots = { File("b.esp", 2, T), File("a.esp", 1, T) } };
        a.Equals(b).Should().BeTrue("order-independent set comparison");
    }

    [Fact]
    public void Equals_DirectorySnapshot_PathCaseInsensitive_CountSensitive()
    {
        var a = new ModStateSnapshot
        {
            DirectorySnapshots = { new DirectorySnapshot { Path = @"C:\Mod\Meshes", FileCount = 3, LastWriteTimeUtc = T } },
        };
        var same = new ModStateSnapshot
        {
            DirectorySnapshots = { new DirectorySnapshot { Path = @"c:\mod\meshes", FileCount = 3, LastWriteTimeUtc = T } },
        };
        var diff = new ModStateSnapshot
        {
            DirectorySnapshots = { new DirectorySnapshot { Path = @"C:\Mod\Meshes", FileCount = 4, LastWriteTimeUtc = T } },
        };
        a.Equals(same).Should().BeTrue();
        a.Equals(diff).Should().BeFalse();
    }

    [Fact]
    public void Equals_EmptyVsEmpty_IsTrue()
    {
        new ModStateSnapshot().Equals(new ModStateSnapshot()).Should().BeTrue();
    }

    [Fact]
    public void GetHashCode_EqualSnapshotsShareHash()
    {
        var a = new ModStateSnapshot { PluginSnapshots = { File("a.esp", 10, T) } };
        var b = new ModStateSnapshot { PluginSnapshots = { File("a.esp", 10, T) } };
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equals_Object_HandlesNullAndWrongType()
    {
        var a = new ModStateSnapshot();
        a.Equals((object?)null).Should().BeFalse();
        a.Equals((object)"not a snapshot").Should().BeFalse();
        a.Equals((object)new ModStateSnapshot()).Should().BeTrue();
    }
}
