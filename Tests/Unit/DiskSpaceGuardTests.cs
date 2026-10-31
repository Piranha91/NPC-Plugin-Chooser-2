using System;
using System.IO;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Locks the pure parts of <see cref="DiskSpaceGuard"/>: which exceptions count
/// as out-of-space, the event counter a render compares before and after, and
/// the warning text. No game, no UI thread (the dialog is skipped when there is
/// no WPF Application).
/// </summary>
public class DiskSpaceGuardTests
{
    private static IOException Win32IoException(int win32Code) =>
        new("io failure", unchecked((int)0x80070000) | win32Code);

    [Theory]
    [InlineData(112)] // ERROR_DISK_FULL
    [InlineData(39)]  // ERROR_HANDLE_DISK_FULL
    public void IsDiskFullError_RecognisesOutOfSpaceCodes(int code)
    {
        DiskSpaceGuard.IsDiskFullError(Win32IoException(code)).Should().BeTrue();
    }

    [Theory]
    [InlineData(32)] // ERROR_SHARING_VIOLATION
    [InlineData(5)]  // ERROR_ACCESS_DENIED
    [InlineData(3)]  // ERROR_PATH_NOT_FOUND
    public void IsDiskFullError_IgnoresOtherIoErrors(int code)
    {
        DiskSpaceGuard.IsDiskFullError(Win32IoException(code)).Should().BeFalse();
    }

    [Fact]
    public void IsDiskFullError_FindsWrappedException()
    {
        var wrapped = new InvalidOperationException("render failed", Win32IoException(112));
        DiskSpaceGuard.IsDiskFullError(wrapped).Should().BeTrue();
    }

    [Fact]
    public void IsDiskFullError_IgnoresNonIoExceptionWithMatchingHResult()
    {
        var ex = new Exception("not io") { HResult = unchecked((int)0x80070070) };
        DiskSpaceGuard.IsDiskFullError(ex).Should().BeFalse();
        DiskSpaceGuard.IsDiskFullError(null).Should().BeFalse();
    }

    [Fact]
    public void ReportDiskFull_AdvancesEventCount()
    {
        long before = DiskSpaceGuard.DiskFullEventCount;
        DiskSpaceGuard.ReportDiskFull(Path.Combine(Path.GetTempPath(), "x.dds"));
        DiskSpaceGuard.DiskFullEventCount.Should().BeGreaterThan(before);
    }

    [Fact]
    public void BuildWarningMessage_NamesDriveFreeSpaceAndCachePath()
    {
        string msg = DiskSpaceGuard.BuildWarningMessage(@"S:\", 84 * 1024, legacyCacheExists: false);
        msg.Should().Contain(@"Drive S:\ is out of space (84 KB free)");
        msg.Should().Contain(DiskSpaceGuard.RenderCacheFolder);
        msg.Should().Contain("25 MB");
        msg.Should().NotContain("CharacterViewerCache");
    }

    [Fact]
    public void BuildWarningMessage_PointsAtLegacyCacheWhenPresent()
    {
        string msg = DiskSpaceGuard.BuildWarningMessage(@"S:\", null, legacyCacheExists: true);
        msg.Should().StartWith(@"Drive S:\ is out of space.");
        msg.Should().Contain("CharacterViewerCache");
    }
}
