using FluentAssertions;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The four <see cref="InitializationWarning"/> records: GroupKey pooling and Render output.
/// Pure logic over string lists.
/// </summary>
public class InitializationWarningTests
{
    [Fact]
    public void MultiSourceMaster_GroupKey_IsCaseInsensitiveOnMasterAndChosen()
    {
        var a = new MultiSourceMasterWarning("Skyrim.ESM", new[] { "ModA" }, "ModA", "Req1");
        var b = new MultiSourceMasterWarning("skyrim.esm", new[] { "modb" }, "moda", "Req2");
        a.GroupKey.Should().Be(b.GroupKey, "master + chosen source are lowercased into the pool key");
    }

    [Fact]
    public void MultiSourceMaster_Render_SingleVsMulti()
    {
        var single = new MultiSourceMasterWarning("Dawnguard.esm", new[] { "X", "Y" }, "Y", "ModZ");
        single.Render(new InitializationWarning[] { single })
            .Should().Contain("Dawnguard.esm").And.Contain("ModZ").And.Contain("[X, Y]").And.Contain("'Y'");

        var w1 = new MultiSourceMasterWarning("Dawnguard.esm", new[] { "X" }, "Y", "Beta");
        var w2 = new MultiSourceMasterWarning("Dawnguard.esm", new[] { "Y" }, "Y", "Alpha");
        var rendered = w1.Render(new InitializationWarning[] { w1, w2 });
        // RequestingMods are alpha-sorted in the multi render.
        rendered.IndexOf("Alpha").Should().BeLessThan(rendered.IndexOf("Beta"));
        rendered.Should().Contain("(2 mods need it)");
    }

    [Fact]
    public void UnscannedInjected_GroupKey_IsOrderIndependent()
    {
        var a = new UnscannedInjectedRecordsWarning("P.esp", "ModA", new[] { "B.esm", "A.esm" });
        var b = new UnscannedInjectedRecordsWarning("Q.esp", "ModB", new[] { "A.esm", "B.esm" });
        a.GroupKey.Should().Be(b.GroupKey, "missing-master sets pool regardless of order");
    }

    [Fact]
    public void UnscannedInjected_Render_JoinsMastersWithAnd()
    {
        var only = new UnscannedInjectedRecordsWarning("P.esp", "ModA", new[] { "A.esm", "B.esm" });
        only.Render(new InitializationWarning[] { only }).Should().Contain("A.esm and B.esm");
    }

    [Fact]
    public void SkippedMissingMaster_Render_ListsUnresolved()
    {
        var only = new SkippedMissingMasterWarning("ModA", new[] { "X.esm", "Y.esm" });
        only.Render(new InitializationWarning[] { only })
            .Should().Contain("ModA").And.Contain("X.esm, Y.esm");
    }

    [Fact]
    public void GenericWarning_GroupKey_IsUniquePerInstanceAndStable()
    {
        var a = new GenericWarning("hello");
        var b = new GenericWarning("hello");
        a.GroupKey.Should().NotBe(b.GroupKey, "each generic warning pools alone");
        a.GroupKey.Should().Be(a.GroupKey, "GroupKey is stable across reads");
        a.Render(new InitializationWarning[] { a }).Should().Be("hello");
    }
}
