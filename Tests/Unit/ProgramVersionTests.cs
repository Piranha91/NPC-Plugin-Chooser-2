using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Smoke + correctness tests for <see cref="ProgramVersion"/>, the version parser/comparer the
/// update handler keys migrations on (e.g. "2.0.8b"). Pure logic, no harness required — also serves
/// as the test-project's compile/run smoke test.
/// </summary>
public class ProgramVersionTests
{
    [Theory]
    [InlineData("2.0.8", 2, 0, 8, 0)]
    [InlineData("2.0.8a", 2, 0, 8, 1)]
    [InlineData("2.0.8b", 2, 0, 8, 2)]
    [InlineData("10.20.30", 10, 20, 30, 0)]
    public void Parse_ExtractsComponents(string s, int major, int minor, int patch, int rev)
    {
        var v = new ProgramVersion(s);
        v.Major.Should().Be(major);
        v.Minor.Should().Be(minor);
        v.Patch.Should().Be(patch);
        v.Revision.Should().Be(rev);
    }

    [Fact]
    public void Parse_EmptyString_IsZeroVersion()
    {
        var v = new ProgramVersion("");
        (v.Major, v.Minor, v.Patch, v.Revision).Should().Be((0, 0, 0, 0));
    }

    [Theory]
    [InlineData("2.0.8", "2.0.9")]   // patch
    [InlineData("2.0.8", "2.1.0")]   // minor
    [InlineData("2.0.8", "3.0.0")]   // major
    [InlineData("2.0.8", "2.0.8a")]  // revision letter beats no letter
    [InlineData("2.0.8a", "2.0.8b")] // revision letter ordering
    public void Compare_LeftIsLessThanRight(string lesser, string greater)
    {
        var lo = new ProgramVersion(lesser);
        var hi = new ProgramVersion(greater);
        (lo < hi).Should().BeTrue();
        (hi > lo).Should().BeTrue();
        (lo <= hi).Should().BeTrue();
        lo.CompareTo(hi).Should().BeNegative();
    }

    [Fact]
    public void Equality_SameComponents_AreEqual()
    {
        var a = new ProgramVersion("2.2.0");
        var b = new ProgramVersion("2.2.0");
        (a == b).Should().BeTrue();
        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ImplicitStringConversion_Works()
    {
        ProgramVersion v = "2.1.7";
        v.Patch.Should().Be(7);
    }

    [Theory]
    [InlineData("2.0.8", "2.0.8")]
    [InlineData("2.0.10b", "2.0.10b")]
    public void ToString_RoundTrips(string s, string expected)
    {
        new ProgramVersion(s).ToString().Should().Be(expected);
    }
}
