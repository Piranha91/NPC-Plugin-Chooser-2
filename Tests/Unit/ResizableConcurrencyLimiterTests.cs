using System.Threading.Tasks;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The live Max parallel renders limit. WaitAsync on a SemaphoreSlim completes synchronously
/// when a slot is free and stays pending otherwise, so IsCompleted right after the call is a
/// deterministic "admitted?" check with no timing involved.
/// </summary>
public class ResizableConcurrencyLimiterTests
{
    private static Task[] Acquire(ResizableConcurrencyLimiter limiter, int count)
    {
        var tasks = new Task[count];
        for (int i = 0; i < count; i++) tasks[i] = limiter.WaitAsync();
        return tasks;
    }

    [Fact]
    public void AdmitsUpToTheLimit()
    {
        var limiter = new ResizableConcurrencyLimiter(2);
        var t = Acquire(limiter, 3);
        t[0].IsCompleted.Should().BeTrue();
        t[1].IsCompleted.Should().BeTrue();
        t[2].IsCompleted.Should().BeFalse();
        limiter.Release();
        t[2].IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void RaisingTheLimit_AdmitsWaitersImmediately()
    {
        var limiter = new ResizableConcurrencyLimiter(1);
        var t = Acquire(limiter, 3);
        t[1].IsCompleted.Should().BeFalse();
        limiter.SetLimit(3);
        t[1].IsCompleted.Should().BeTrue();
        t[2].IsCompleted.Should().BeTrue();
        limiter.Limit.Should().Be(3);
    }

    [Fact]
    public void LoweringTheLimit_ReclaimsFreeSlotsAtOnce()
    {
        var limiter = new ResizableConcurrencyLimiter(4);
        Acquire(limiter, 1);                 // 1 in flight, 3 free
        limiter.SetLimit(2);                 // reclaims 2 free slots now
        var t = Acquire(limiter, 2);
        t[0].IsCompleted.Should().BeTrue();  // in flight 2 = limit
        t[1].IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void LoweringBelowInFlight_LetsWorkFinish_ThenHoldsTheNewLimit()
    {
        var limiter = new ResizableConcurrencyLimiter(3);
        Acquire(limiter, 3);                 // all 3 in flight
        limiter.SetLimit(1);                 // nothing free: 2 slots owed
        var waiter = limiter.WaitAsync();
        waiter.IsCompleted.Should().BeFalse();
        limiter.Release();                   // pays debt
        limiter.Release();                   // pays debt
        waiter.IsCompleted.Should().BeFalse();   // 1 still in flight = limit
        limiter.Release();                   // the last original finishes
        waiter.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void RaisingWhileInDebt_CancelsTheDebtFirst()
    {
        var limiter = new ResizableConcurrencyLimiter(3);
        Acquire(limiter, 3);
        limiter.SetLimit(1);                 // owes 2
        limiter.SetLimit(2);                 // forgives 1, still owes 1
        var waiter = limiter.WaitAsync();
        waiter.IsCompleted.Should().BeFalse();
        limiter.Release();                   // pays the remaining debt
        waiter.IsCompleted.Should().BeFalse();   // 2 in flight = limit
        limiter.Release();
        waiter.IsCompleted.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveLimits_ClampToOne(int limit)
    {
        var limiter = new ResizableConcurrencyLimiter(limit);
        limiter.Limit.Should().Be(1);
        limiter.WaitAsync().IsCompleted.Should().BeTrue();
        limiter.SetLimit(limit);
        limiter.Limit.Should().Be(1);
    }
}
