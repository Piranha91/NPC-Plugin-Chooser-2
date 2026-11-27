using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="WigHeadgearChoiceCoordinator"/>: where the answers of the wig-and-headgear window
/// go. The patcher reads answers only from <see cref="Settings.WigHeadgearConflictChoices"/>, so
/// an answer given WITHOUT "remember" is written there for the run and must be gone again when
/// the run ends, however it ends. Plain logic on a Settings model: no WPF, no game install.
/// </summary>
public class WigHeadgearChoiceCoordinatorTests
{
    private static readonly FormKey Rikke = MutagenFixtures.Fk("000001:Skyrim.esm");
    private static readonly FormKey GuardOne = MutagenFixtures.Fk("000002:Skyrim.esm");
    private static readonly FormKey GuardTwo = MutagenFixtures.Fk("000003:Skyrim.esm");

    private static (Settings Settings, WigHeadgearChoiceCoordinator Coordinator) Make()
    {
        var settings = new Settings();
        return (settings, new WigHeadgearChoiceCoordinator(settings));
    }

    // ------------------------------------------------------------------------------------------
    // Remembered
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Remembered_IsStoredForEveryNpc_AndStaysAfterTheRun()
    {
        var (settings, coordinator) = Make();

        using (coordinator.ApplyForRun(new[] { Rikke, GuardOne }, WigHeadgearChoice.Omit, remember: true))
        {
            settings.GetWigHeadgearChoice(Rikke).Should().Be(WigHeadgearChoice.Omit);
            settings.GetWigHeadgearChoice(GuardOne).Should().Be(WigHeadgearChoice.Omit);
        }

        settings.GetWigHeadgearChoice(Rikke).Should().Be(WigHeadgearChoice.Omit, "a remembered answer outlives the run");
        settings.GetWigHeadgearChoice(GuardOne).Should().Be(WigHeadgearChoice.Omit);
        coordinator.RememberedCount.Should().Be(2);
    }

    // ------------------------------------------------------------------------------------------
    // This run only
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WigHeadgearChoice.Forward)]
    [InlineData(WigHeadgearChoice.Omit)]
    [InlineData(WigHeadgearChoice.ConvertToHeadParts)]
    public void RunOnly_IsInSettingsDuringTheRun_AndGoneAfterIt(WigHeadgearChoice choice)
    {
        var (settings, coordinator) = Make();

        var handle = coordinator.ApplyForRun(new[] { Rikke, GuardOne }, choice, remember: false);

        settings.GetWigHeadgearChoice(Rikke).Should().Be(choice, "the patcher reads the answer from Settings");
        settings.GetWigHeadgearChoice(GuardOne).Should().Be(choice);

        handle.Dispose();

        settings.GetWigHeadgearChoice(Rikke).Should().BeNull("an answer for this run only is removed when the run ends");
        settings.GetWigHeadgearChoice(GuardOne).Should().BeNull();
        settings.WigHeadgearConflictChoices.Should().BeEmpty();
    }

    [Fact]
    public async Task RunOnly_IsRemovedWhenTheRunThrows()
    {
        var (settings, coordinator) = Make();

        // The shape of VM_Run.ExecutePatchingAsync: the handle is taken before the patcher runs
        // and disposed in the finally.
        async Task Run()
        {
            IDisposable? runOnly = null;
            try
            {
                runOnly = coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.ConvertToHeadParts, remember: false);
                await Task.Yield();
                settings.GetWigHeadgearChoice(Rikke).Should().Be(WigHeadgearChoice.ConvertToHeadParts);
                throw new InvalidOperationException("the patcher failed");
            }
            finally
            {
                runOnly?.Dispose();
            }
        }

        await FluentActions.Awaiting(Run).Should().ThrowAsync<InvalidOperationException>();

        settings.GetWigHeadgearChoice(Rikke).Should().BeNull("a failed run leaves no answer behind");
    }

    [Fact]
    public async Task RunOnly_IsRemovedWhenTheRunIsCancelled()
    {
        var (settings, coordinator) = Make();
        using var cts = new CancellationTokenSource();

        async Task Run()
        {
            IDisposable? runOnly = null;
            try
            {
                runOnly = coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.Omit, remember: false);
                cts.Cancel();
                await Task.Yield();
                cts.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                runOnly?.Dispose();
            }
        }

        await FluentActions.Awaiting(Run).Should().ThrowAsync<OperationCanceledException>();

        settings.GetWigHeadgearChoice(Rikke).Should().BeNull();
    }

    [Fact]
    public void RunOnly_DisposedTwice_RemovesOnce()
    {
        var (settings, coordinator) = Make();
        var handle = coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.Omit, remember: false);
        handle.Dispose();

        // The user remembers an answer for the same NPC in a later run...
        coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.Forward, remember: true).Dispose();
        // ...and a stray second dispose of the first run's handle must not take it away.
        handle.Dispose();

        settings.GetWigHeadgearChoice(Rikke).Should().Be(WigHeadgearChoice.Forward);
    }

    [Fact]
    public void RunOnly_LeavesRememberedAnswersOfOtherNpcsAlone()
    {
        var (settings, coordinator) = Make();
        coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.Omit, remember: true).Dispose();

        coordinator.ApplyForRun(new[] { GuardOne, GuardTwo }, WigHeadgearChoice.Forward, remember: false).Dispose();

        settings.WigHeadgearConflictChoices.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<FormKey, WigHeadgearChoice>(Rikke, WigHeadgearChoice.Omit));
    }

    [Fact]
    public void RunOnly_WrittenOverARememberedAnswer_PutsTheRememberedAnswerBack()
    {
        // The scan lists only NPCs with no answer, so the window never does this; the coordinator
        // must still not lose a remembered answer if it is asked to.
        var (settings, coordinator) = Make();
        coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.Omit, remember: true).Dispose();

        var handle = coordinator.ApplyForRun(new[] { Rikke, GuardOne }, WigHeadgearChoice.Forward, remember: false);
        settings.GetWigHeadgearChoice(Rikke).Should().Be(WigHeadgearChoice.Forward);
        coordinator.RememberedCount.Should().Be(1, "Rikke is still a remembered NPC; the guard is not");
        handle.Dispose();

        settings.GetWigHeadgearChoice(Rikke).Should().Be(WigHeadgearChoice.Omit);
        settings.GetWigHeadgearChoice(GuardOne).Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------
    // Counting and clearing
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void RememberedCount_LeavesOutAnswersHeldForARun()
    {
        var (_, coordinator) = Make();
        coordinator.RememberedCount.Should().Be(0);

        coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.Omit, remember: true).Dispose();
        var runOnly = coordinator.ApplyForRun(new[] { GuardOne, GuardTwo }, WigHeadgearChoice.Forward, remember: false);

        coordinator.RememberedCount.Should().Be(1);
        runOnly.Dispose();
        coordinator.RememberedCount.Should().Be(1);
    }

    [Fact]
    public void ClearRemembered_ForgetsEveryRememberedAnswer()
    {
        var (settings, coordinator) = Make();
        coordinator.ApplyForRun(new[] { Rikke, GuardOne }, WigHeadgearChoice.Omit, remember: true).Dispose();

        coordinator.ClearRemembered().Should().BeTrue();

        settings.WigHeadgearConflictChoices.Should().BeEmpty();
        coordinator.RememberedCount.Should().Be(0);
    }

    [Fact]
    public void ClearRemembered_DoesNotTouchAnswersHeldForARun()
    {
        var (settings, coordinator) = Make();
        coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.Omit, remember: true).Dispose();
        var runOnly = coordinator.ApplyForRun(new[] { GuardOne }, WigHeadgearChoice.ConvertToHeadParts, remember: false);

        coordinator.ClearRemembered().Should().BeTrue();

        settings.GetWigHeadgearChoice(Rikke).Should().BeNull();
        settings.GetWigHeadgearChoice(GuardOne).Should().Be(WigHeadgearChoice.ConvertToHeadParts,
            "the run in progress still needs its answer");
        runOnly.Dispose();
        settings.WigHeadgearConflictChoices.Should().BeEmpty();
    }

    [Fact]
    public void ClearRemembered_IsRefusedWhileARunIsInProgress()
    {
        var (settings, coordinator) = Make();
        coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.Omit, remember: true).Dispose();

        var run = coordinator.BeginRun();
        coordinator.IsRunActive.Should().BeTrue();
        coordinator.ClearRemembered().Should().BeFalse("the patcher is reading the answers");
        settings.GetWigHeadgearChoice(Rikke).Should().Be(WigHeadgearChoice.Omit);

        run.Dispose();
        coordinator.IsRunActive.Should().BeFalse();
        coordinator.ClearRemembered().Should().BeTrue();
        settings.WigHeadgearConflictChoices.Should().BeEmpty();
    }

    [Fact]
    public void Changed_FiresWhenAnswersAreStoredRemovedOrCleared_AndWhenARunStartsOrEnds()
    {
        var (_, coordinator) = Make();
        int changes = 0;
        using var subscription = coordinator.Changed.Subscribe(_ => changes++);

        var remembered = coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.Omit, remember: true);
        changes.Should().Be(1);
        remembered.Dispose();
        changes.Should().Be(1, "nothing changes when a remembered answer's handle is disposed");

        var runOnly = coordinator.ApplyForRun(new[] { GuardOne }, WigHeadgearChoice.Omit, remember: false);
        changes.Should().Be(2);
        runOnly.Dispose();
        changes.Should().Be(3);

        coordinator.ClearRemembered();
        changes.Should().Be(4);

        var run = coordinator.BeginRun();
        changes.Should().Be(5);
        run.Dispose();
        changes.Should().Be(6);
    }
}
