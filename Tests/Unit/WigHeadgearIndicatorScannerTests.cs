using System.Collections.Concurrent;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="WigHeadgearIndicatorScanner"/>: the worker that answers the NPC list's
/// wig-and-headgear question. The record work behind an answer resolves an outfit per NPC, so
/// the contract is about cost: never on the calling thread, never under a wig mode that cannot
/// forward into an outfit, never twice for the same key, and nothing more after a reset. The
/// back end is a stand-in here; no WPF, no game install.
/// </summary>
public class WigHeadgearIndicatorScannerTests
{
    private static readonly FormKey Rikke = MutagenFixtures.Fk("000001:Skyrim.esm");
    private static readonly FormKey Guard = MutagenFixtures.Fk("000002:Skyrim.esm");
    private static readonly ModSetting Mod = new() { DisplayName = "High Poly NPC Overhaul" };

    private static readonly IReadOnlyList<WigHeadgearConflict> OneConflict = new[]
    {
        new WigHeadgearConflict(
            new WigHeadgearConflictItem(MutagenFixtures.Fk("000801:HighPoly.esp"), "Wig", null, new[] { 31 }),
            new WigHeadgearConflictItem(MutagenFixtures.Fk("013EDC:Skyrim.esm"), "Helmet", "Helmet", new[] { 31, 42 }),
            MutagenFixtures.Fk("000D3E:Skyrim.esm"), "Outfit", new[] { 31 }),
    };

    private static WigConflictQuery Query(FormKey npc, WigHandlingMode mode = WigHandlingMode.ForwardToOutfit,
        string signature = "s1", string modName = "High Poly NPC Overhaul") =>
        new(new WigConflictQueryKey(npc, modName, npc, mode, signature), Mod);

    /// <summary>A stand-in back end that records who asked what, and on which thread.</summary>
    private sealed class FakeBackEnd
    {
        public readonly ConcurrentQueue<(WigConflictQueryKey Key, int ThreadId)> Calls = new();
        public Func<WigConflictQuery, IReadOnlyList<WigHeadgearConflict>> Answer = _ => OneConflict;

        public IReadOnlyList<WigHeadgearConflict> Ask(WigConflictQuery query)
        {
            Calls.Enqueue((query.Key, Environment.CurrentManagedThreadId));
            return Answer(query);
        }
    }

    private static async Task Drain(WigHeadgearIndicatorScanner scanner) =>
        await scanner.Idle.WaitAsync(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task AQuestion_IsAnsweredOffTheCallingThread_AndTheRowIsTold()
    {
        var backEnd = new FakeBackEnd();
        using var asked = new ManualResetEventSlim(false);
        backEnd.Answer = _ =>
        {
            asked.Set();
            return OneConflict;
        };
        var told = new ConcurrentQueue<FormKey>();
        using var scanner = new WigHeadgearIndicatorScanner(backEnd.Ask, npcs => { foreach (var n in npcs) told.Enqueue(n); });
        int callingThread = Environment.CurrentManagedThreadId;

        var query = Query(Rikke);
        scanner.TryGetAnswer(query.Key, out _).Should().BeFalse("nothing is known before the worker has run");
        scanner.Request(query).Should().BeTrue();

        // The calling thread stays blocked here until the back end has been asked, so the
        // question can only have been put on another thread.
        asked.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the worker must not need the calling thread");
        await Drain(scanner);

        backEnd.Calls.Should().ContainSingle();
        backEnd.Calls.Single().ThreadId.Should().NotBe(callingThread, "the record work must not run where it was asked for");
        told.Should().Equal(Rikke);
        scanner.TryGetAnswer(query.Key, out var conflicts).Should().BeTrue();
        conflicts.Should().BeSameAs(OneConflict);
        scanner.QueriesRun.Should().Be(1);
    }

    [Theory]
    [InlineData(WigHandlingMode.None)]
    [InlineData(WigHandlingMode.ForwardToSkin)]
    [InlineData(WigHandlingMode.ConvertToHeadParts)]
    public async Task UnderAnyOtherWigMode_TheBackEndIsNotAskedAtAll(WigHandlingMode mode)
    {
        var backEnd = new FakeBackEnd();
        using var scanner = new WigHeadgearIndicatorScanner(backEnd.Ask, _ => { });

        scanner.Request(Query(Rikke, mode)).Should().BeFalse();
        await Drain(scanner);

        backEnd.Calls.Should().BeEmpty();
        scanner.QueriesRun.Should().Be(0);
    }

    [Fact]
    public async Task TheSameKey_IsAskedOnce()
    {
        var backEnd = new FakeBackEnd();
        using var scanner = new WigHeadgearIndicatorScanner(backEnd.Ask, _ => { });

        scanner.Request(Query(Rikke)).Should().BeTrue();
        await Drain(scanner);
        scanner.Request(Query(Rikke)).Should().BeFalse("the answer for this key is stored");
        scanner.Request(Query(Rikke)).Should().BeFalse();
        await Drain(scanner);

        backEnd.Calls.Should().ContainSingle();
    }

    [Theory]
    [InlineData("s2", "High Poly NPC Overhaul")]  // other outfit-affecting settings
    [InlineData("s1", "Another Mod")]              // another mod selected
    public async Task AChangedKey_IsAskedAgain_AndReplacesTheStoredAnswer(string signature, string modName)
    {
        var backEnd = new FakeBackEnd();
        using var scanner = new WigHeadgearIndicatorScanner(backEnd.Ask, _ => { });

        var first = Query(Rikke);
        scanner.Request(first);
        await Drain(scanner);

        backEnd.Answer = _ => Array.Empty<WigHeadgearConflict>();
        var second = Query(Rikke, signature: signature, modName: modName);
        scanner.TryGetAnswer(second.Key, out _).Should().BeFalse("an answer is only good for the key it was asked under");
        scanner.Request(second).Should().BeTrue();
        await Drain(scanner);

        backEnd.Calls.Should().HaveCount(2);
        scanner.TryGetAnswer(second.Key, out var conflicts).Should().BeTrue();
        conflicts.Should().BeEmpty();
        scanner.TryGetAnswer(first.Key, out _).Should().BeFalse("one answer per NPC: the old key's is gone");
    }

    [Fact]
    public async Task ManyRows_AreAllAnswered_OneAtATime_AndReportedInBatches()
    {
        var backEnd = new FakeBackEnd();
        int concurrent = 0, maxConcurrent = 0;
        backEnd.Answer = _ =>
        {
            int now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, now);
            Thread.SpinWait(200);
            Interlocked.Decrement(ref concurrent);
            return OneConflict;
        };
        var batches = new ConcurrentQueue<int>();
        using var scanner = new WigHeadgearIndicatorScanner(backEnd.Ask, npcs => batches.Enqueue(npcs.Count),
            batchSize: 50, batchInterval: TimeSpan.FromHours(1));

        var npcs = Enumerable.Range(1, 500).Select(i => MutagenFixtures.Fk($"{i:X6}:Skyrim.esm")).ToList();
        foreach (var npc in npcs) scanner.Request(Query(npc));
        await Drain(scanner);

        backEnd.Calls.Select(c => c.Key.Npc).Should().BeEquivalentTo(npcs);
        maxConcurrent.Should().Be(1, "one worker at a time");
        batches.Sum().Should().Be(500);
        batches.Should().OnlyContain(count => count <= 50);
        npcs.All(npc => scanner.TryGetAnswer(Query(npc).Key, out _)).Should().BeTrue("every row has its answer");
    }

    [Fact]
    public async Task ABackEndFailure_CountsAsNoConflict_AndTheWorkerGoesOn()
    {
        var backEnd = new FakeBackEnd
        {
            Answer = q => q.Key.Npc == Rikke ? throw new InvalidOperationException("unreadable record") : OneConflict,
        };
        using var scanner = new WigHeadgearIndicatorScanner(backEnd.Ask, _ => { });

        scanner.Request(Query(Rikke));
        scanner.Request(Query(Guard));
        await Drain(scanner);

        scanner.TryGetAnswer(Query(Rikke).Key, out var failed).Should().BeTrue();
        failed.Should().BeEmpty();
        scanner.TryGetAnswer(Query(Guard).Key, out var ok).Should().BeTrue();
        ok.Should().BeSameAs(OneConflict);
    }

    [Fact]
    public async Task ANullAnswer_MeansCannotSayNow_NothingIsStoredOrReported_AndTheRowMayAskAgain()
    {
        var told = new ConcurrentQueue<FormKey>();
        IReadOnlyList<WigHeadgearConflict>? answer = null; // e.g. a patch run is using the records
        int calls = 0;
        using var scanner = new WigHeadgearIndicatorScanner(
            _ =>
            {
                Interlocked.Increment(ref calls);
                return answer;
            },
            npcs => { foreach (var n in npcs) told.Enqueue(n); });

        scanner.Request(Query(Rikke)).Should().BeTrue();
        await Drain(scanner);

        scanner.TryGetAnswer(Query(Rikke).Key, out _).Should().BeFalse("no answer was given, so none is stored");
        told.Should().BeEmpty();

        answer = OneConflict;
        scanner.Request(Query(Rikke)).Should().BeTrue("an unanswered row may ask again");
        await Drain(scanner);

        calls.Should().Be(2);
        scanner.TryGetAnswer(Query(Rikke).Key, out var conflicts).Should().BeTrue();
        conflicts.Should().BeSameAs(OneConflict);
        told.Should().Equal(Rikke);
    }

    [Fact]
    public async Task Reset_CancelsWhatIsWaiting_AndForgetsTheAnswers()
    {
        var backEnd = new FakeBackEnd();
        using var firstStarted = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        backEnd.Answer = _ =>
        {
            firstStarted.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return OneConflict;
        };
        var told = new ConcurrentQueue<FormKey>();
        using var scanner = new WigHeadgearIndicatorScanner(backEnd.Ask, npcs => { foreach (var n in npcs) told.Enqueue(n); });

        // The worker is inside the first question; 99 more are waiting.
        var npcs = Enumerable.Range(1, 100).Select(i => MutagenFixtures.Fk($"{i:X6}:Skyrim.esm")).ToList();
        foreach (var npc in npcs) scanner.Request(Query(npc));
        firstStarted.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        var interruptedWorker = scanner.Idle;

        scanner.Reset();
        release.Set();
        await interruptedWorker.WaitAsync(TimeSpan.FromSeconds(30));

        backEnd.Calls.Should().ContainSingle("nothing further starts after a reset");
        told.Should().BeEmpty("the answer in flight describes records no longer in use");
        scanner.TryGetAnswer(Query(npcs[0]).Key, out _).Should().BeFalse();

        // The scanner still works afterwards.
        backEnd.Answer = _ => OneConflict;
        scanner.Request(Query(Rikke)).Should().BeTrue();
        await Drain(scanner);
        scanner.TryGetAnswer(Query(Rikke).Key, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Forget_DropsOneNpcsAnswer()
    {
        var backEnd = new FakeBackEnd();
        using var scanner = new WigHeadgearIndicatorScanner(backEnd.Ask, _ => { });
        scanner.Request(Query(Rikke));
        scanner.Request(Query(Guard));
        await Drain(scanner);

        scanner.Forget(Rikke);

        scanner.TryGetAnswer(Query(Rikke).Key, out _).Should().BeFalse();
        scanner.TryGetAnswer(Query(Guard).Key, out _).Should().BeTrue();
    }

    [Fact]
    public void AfterDispose_NothingIsAsked()
    {
        var backEnd = new FakeBackEnd();
        var scanner = new WigHeadgearIndicatorScanner(backEnd.Ask, _ => { });
        scanner.Dispose();

        scanner.Request(Query(Rikke)).Should().BeFalse();
        backEnd.Calls.Should().BeEmpty();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen) return;
        } while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
