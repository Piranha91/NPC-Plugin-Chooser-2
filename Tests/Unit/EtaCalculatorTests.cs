using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="EtaCalculator"/> — recency-weighted ETA for batch work. Pure arithmetic, no env.
/// </summary>
public class EtaCalculatorTests
{
    [Fact]
    public void Estimate_NothingRecorded_ReturnsNull()
    {
        new EtaCalculator().Estimate(100).Should().BeNull();
    }

    [Fact]
    public void Estimate_NoRemainingItems_ReturnsNull()
    {
        var calc = new EtaCalculator();
        calc.RecordItem(1.0);
        calc.Estimate(0).Should().BeNull();
        calc.Estimate(-5).Should().BeNull();
    }

    [Fact]
    public void Estimate_PerItemNonPositive_ReturnsNull()
    {
        var calc = new EtaCalculator();
        calc.RecordItem(0); // only zero-cost items -> per-item avg 0 -> null
        calc.Estimate(10).Should().BeNull();
    }

    [Fact]
    public void RecordItem_NaN_TreatedAsZero()
    {
        var calc = new EtaCalculator();
        calc.RecordItem(double.NaN);
        calc.Estimate(10).Should().BeNull(); // 0 cost -> null
    }

    [Fact]
    public void RecordItem_Negative_TreatedAsZero()
    {
        var calc = new EtaCalculator();
        calc.RecordItem(-3.0);
        calc.Estimate(10).Should().BeNull();
    }

    [Fact]
    public void Estimate_SingleSample_BlendsTowardCumulative()
    {
        // window=25, one sample of 2s. windowWeight = 1/25 = 0.04.
        // windowAvg = 2, cumulativeAvg = 2 -> perItem = 2. Estimate(10) = 20s.
        var calc = new EtaCalculator(25);
        calc.RecordItem(2.0);
        calc.Estimate(10).Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Estimate_FullWindow_TrustsWindowAverage()
    {
        // window=2. Record 10,10 (fills window), then 1,1 evicts the 10s.
        // window now [1,1] avg=1, weight=1.0 -> perItem ~ 1. cumulative = (10+10+1+1)/4=5.5.
        var calc = new EtaCalculator(2);
        calc.RecordItem(10);
        calc.RecordItem(10);
        calc.RecordItem(1);
        calc.RecordItem(1);
        // weight = min(1, 2/2) = 1 -> perItem = windowAvg = 1.0
        calc.Estimate(4).Should().Be(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void Estimate_WindowFasterThanHistory_ReactsDown()
    {
        // Heavy early items then a fast steady window should pull the estimate below
        // the cumulative average once the window is full.
        var calc = new EtaCalculator(3);
        for (int i = 0; i < 5; i++) calc.RecordItem(10); // history heavy
        for (int i = 0; i < 3; i++) calc.RecordItem(1);  // window now all 1s
        var est = calc.Estimate(1)!.Value.TotalSeconds;
        est.Should().BeApproximately(1.0, 1e-9); // full window dominates
    }

    [Fact]
    public void Ctor_ClampsWindowSizeToAtLeastOne()
    {
        var calc = new EtaCalculator(0);
        calc.RecordItem(5);
        calc.RecordItem(5);
        // With window clamped to 1, only the most recent sample feeds the window avg.
        calc.Estimate(2).Should().Be(TimeSpan.FromSeconds(10));
    }
}
