using System.Windows.Threading;
using Autofac;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.Memory;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// The view models of the wig-and-headgear feature, on the STA fixture:
/// <list type="bullet">
///   <item><see cref="VM_WigHeadgearConflictWindow"/>: each of the four buttons gives its answer
///     and asks the window to close; "remember" travels with the three that continue; a window
///     closed any other way cancels the run.</item>
///   <item><see cref="VM_WigHeadgearChoiceReset"/>: the Settings button exists only while there
///     are remembered answers, shows their number, follows stores and clears, and cannot be used
///     while a run is in progress.</item>
///   <item>the composition: <see cref="VM_Settings"/>, <see cref="VM_Run"/> and
///     <see cref="VM_NpcSelectionBar"/> resolve from the frontend graph with the one shared
///     <see cref="WigHeadgearChoiceCoordinator"/>.</item>
/// </list>
/// No game install needed.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class WigHeadgearConflictUiTests
{
    private readonly WpfStaFixture _sta;
    public WigHeadgearConflictUiTests(WpfStaFixture sta) => _sta = sta;

    private static readonly FormKey Rikke = MutagenFixtures.Fk("000001:Skyrim.esm");
    private static readonly FormKey GuardOne = MutagenFixtures.Fk("000002:Skyrim.esm");
    private static readonly FormKey GuardTwo = MutagenFixtures.Fk("000003:Skyrim.esm");

    internal static WigHeadgearConflict Conflict(string outfitKey, string outfitEditorId, string headgearKey,
        string headgearEditorId, string headgearName, string wigKey, string wigEditorId) =>
        new(new WigHeadgearConflictItem(MutagenFixtures.Fk(wigKey), wigEditorId, null, new[] { 31 }),
            new WigHeadgearConflictItem(MutagenFixtures.Fk(headgearKey), headgearEditorId, headgearName, new[] { 31, 42 }),
            MutagenFixtures.Fk(outfitKey), outfitEditorId, new[] { 31 });

    internal static WigHeadgearConflict OfficerConflict() => Conflict("0D3EAA:Skyrim.esm", "ImperialOfficerOutfit",
        "013EDC:Skyrim.esm", "ArmorImperialHelmetOfficer", "Imperial Officer's Helmet",
        "000801:High Poly NPC Overhaul.esp", "HighPoly_WigAA_HairFemaleNord03");

    internal static WigHeadgearConflict GuardConflict() => Conflict("0A1B2C:Skyrim.esm", "GuardWhiterunOutfit",
        "021508:Skyrim.esm", "ArmorGuardHelmetWhiterun", "Whiterun Guard's Helmet",
        "000802:High Poly NPC Overhaul.esp", "HighPoly_WigAA_HairMaleNord01");

    private static IReadOnlyList<WigHeadgearConflictCase> ThreeCases() => new[]
    {
        new WigHeadgearConflictCase(Rikke, "Legate Rikke | 000001:Skyrim.esm", "High Poly NPC Overhaul", new[] { OfficerConflict() }),
        new WigHeadgearConflictCase(GuardOne, "Whiterun Guard | 000002:Skyrim.esm", "High Poly NPC Overhaul", new[] { GuardConflict() }),
        new WigHeadgearConflictCase(GuardTwo, "Whiterun Guard | 000003:Skyrim.esm", "High Poly NPC Overhaul", new[] { GuardConflict() }),
    };

    // =============================================================================================
    // The window's view model
    // =============================================================================================

    [Fact]
    public async Task Window_StatesTheProblemOnce_AndTheTotals()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            using var vm = new VM_WigHeadgearConflictWindow(ThreeCases());

            vm.Title.Should().Be("Wig and headgear on the same slot");
            vm.Explanation.Should().Be(
                "Wig handling 'Forward to Outfit' would add a wig to the outfit of the NPCs below, and that outfit " +
                "already holds headgear on the same body slot. Skyrim shows only one of two items on the same slot, " +
                "so either the wig or the headgear would be missing in game. Choose what NPC2 should do for these NPCs.");
            vm.TotalsText.Should().Be("3 NPCs in 2 groups");
            vm.Groups.Should().HaveCount(2);
            vm.Rows.Should().HaveCount(5, "two group rows and three NPC rows");
            vm.NpcFormKeys.Should().Equal(Rikke, GuardOne, GuardTwo);
            vm.RememberChoice.Should().BeFalse("remembering is the user's to ask for");

            vm.ForwardToolTip.Should().Be("The wig is added to the outfit as asked. The headgear or the wig will not show.");
            vm.OmitToolTip.Should().Be("The wig is left out for these NPCs. They keep their headgear and have no wig.");
            vm.ConvertToolTip.Should().Be(
                "The wig becomes part of the head for these NPCs, so it stays under the headgear like normal hair.");
        });
    }

    [Fact]
    public async Task Window_ClosedWithoutAButton_CancelsTheRun()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            using var vm = new VM_WigHeadgearConflictWindow(ThreeCases());

            vm.Decision.Should().Be(WigHeadgearConflictDecision.CancelRun);
            vm.Decision.ContinuesRun.Should().BeFalse();
        });
    }

    [Theory]
    [InlineData(WigHeadgearChoice.Forward, false)]
    [InlineData(WigHeadgearChoice.Forward, true)]
    [InlineData(WigHeadgearChoice.Omit, false)]
    [InlineData(WigHeadgearChoice.Omit, true)]
    [InlineData(WigHeadgearChoice.ConvertToHeadParts, false)]
    [InlineData(WigHeadgearChoice.ConvertToHeadParts, true)]
    public async Task Window_EachContinueButton_GivesItsChoiceWithTheRememberTick_AndCloses(
        WigHeadgearChoice choice, bool remember)
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            using var vm = new VM_WigHeadgearConflictWindow(ThreeCases());
            int closeRequests = 0;
            vm.CloseRequested += () => closeRequests++;
            vm.RememberChoice = remember;

            var command = choice switch
            {
                WigHeadgearChoice.Forward => vm.ForwardCommand,
                WigHeadgearChoice.Omit => vm.OmitCommand,
                _ => vm.ConvertCommand,
            };
            command.Execute().Subscribe();

            vm.Decision.Should().Be(new WigHeadgearConflictDecision(choice, remember));
            vm.Decision.ContinuesRun.Should().BeTrue();
            closeRequests.Should().Be(1);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Window_CancelRun_StoresNothingToRemember_AndCloses(bool rememberTicked)
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            using var vm = new VM_WigHeadgearConflictWindow(ThreeCases());
            int closeRequests = 0;
            vm.CloseRequested += () => closeRequests++;
            vm.RememberChoice = rememberTicked;

            vm.CancelCommand.Execute().Subscribe();

            vm.Decision.Should().Be(WigHeadgearConflictDecision.CancelRun);
            vm.Decision.Choice.Should().BeNull();
            vm.Decision.Remember.Should().BeFalse("a cancelled run has no choice to remember");
            closeRequests.Should().Be(1);
        });
    }

    // =============================================================================================
    // The Settings button
    // =============================================================================================

    [Fact]
    public async Task SettingsButton_IsThereOnlyWhileThereAreRememberedChoices_AndShowsTheirNumber()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var settings = new Settings();
            var coordinator = new WigHeadgearChoiceCoordinator(settings);
            using var vm = new VM_WigHeadgearChoiceReset(coordinator);
            bool canClear = false;
            using var canExecute = vm.ClearCommand.CanExecute.Subscribe(can => canClear = can);

            vm.HasRememberedChoices.Should().BeFalse("nothing is remembered yet: the button is not shown");
            vm.RememberedCount.Should().Be(0);
            canClear.Should().BeFalse();

            // Answers for one run only are not remembered answers.
            var runOnly = coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.Omit, remember: false);
            vm.HasRememberedChoices.Should().BeFalse();
            runOnly.Dispose();

            coordinator.ApplyForRun(new[] { GuardOne, GuardTwo }, WigHeadgearChoice.Omit, remember: true).Dispose();
            vm.HasRememberedChoices.Should().BeTrue("storing a remembered answer brings the button up");
            vm.RememberedCount.Should().Be(2);
            vm.ButtonText.Should().Be("Clear remembered wig and headgear choices (2)");
            canClear.Should().BeTrue();

            coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.Forward, remember: true).Dispose();
            vm.ButtonText.Should().Be("Clear remembered wig and headgear choices (3)");

            vm.ClearCommand.Execute().Subscribe();

            settings.WigHeadgearConflictChoices.Should().BeEmpty("the button forgets every remembered answer");
            vm.HasRememberedChoices.Should().BeFalse("with nothing left the button goes away");
            vm.RememberedCount.Should().Be(0);
            canClear.Should().BeFalse();
        });
    }

    [Fact]
    public async Task SettingsButton_StartsVisible_WhenSettingsAlreadyHoldRememberedChoices()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var settings = new Settings();
            settings.SetWigHeadgearChoice(new[] { Rikke }, WigHeadgearChoice.ConvertToHeadParts); // as loaded from Settings.json
            using var vm = new VM_WigHeadgearChoiceReset(new WigHeadgearChoiceCoordinator(settings));

            vm.HasRememberedChoices.Should().BeTrue();
            vm.ButtonText.Should().Be("Clear remembered wig and headgear choices (1)");
        });
    }

    [Fact]
    public async Task SettingsButton_CannotBeUsedWhileARunIsInProgress()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var settings = new Settings();
            var coordinator = new WigHeadgearChoiceCoordinator(settings);
            coordinator.ApplyForRun(new[] { Rikke }, WigHeadgearChoice.Omit, remember: true).Dispose();
            using var vm = new VM_WigHeadgearChoiceReset(coordinator);
            bool canClear = false;
            using var canExecute = vm.ClearCommand.CanExecute.Subscribe(can => canClear = can);
            canClear.Should().BeTrue();

            var run = coordinator.BeginRun();
            vm.IsRunActive.Should().BeTrue();
            vm.HasRememberedChoices.Should().BeTrue("still shown, with its number");
            canClear.Should().BeFalse("the patcher is reading the answers");

            run.Dispose();
            canClear.Should().BeTrue();
        });
    }

    // =============================================================================================
    // Composition
    // =============================================================================================

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public async Task FrontendGraph_ResolvesSettingsRunAndNpcList_AroundOneCoordinator()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var guard = new StaticStateGuard(immediateSchedulers: false);
            FrontendVmHarness.InstallStaMainThreadScheduler();

            var settings = new Settings { SkyrimRelease = SkyrimRelease.SkyrimSE };
            using var harness = new FrontendVmHarness(NpcChooserTestEnvironment.Invalid(), settings);

            var coordinator = harness.Container.Resolve<WigHeadgearChoiceCoordinator>();
            var vmSettings = harness.Container.Resolve<VM_Settings>();
            harness.Container.Resolve<VM_Run>().Should().NotBeNull("VM_Run takes the forwarder and the coordinator");
            harness.Container.Resolve<VM_NpcSelectionBar>().Should().NotBeNull();

            vmSettings.WigHeadgearChoiceReset.HasRememberedChoices.Should().BeFalse();

            // The answer the run would store reaches the Settings button through the shared coordinator.
            coordinator.ApplyForRun(new[] { Rikke, GuardOne }, WigHeadgearChoice.Omit, remember: true).Dispose();
            DoEvents();

            vmSettings.WigHeadgearChoiceReset.HasRememberedChoices.Should().BeTrue();
            vmSettings.WigHeadgearChoiceReset.ButtonText.Should().Be("Clear remembered wig and headgear choices (2)");

            vmSettings.WigHeadgearChoiceReset.ClearCommand.Execute().Subscribe();
            DoEvents();

            settings.WigHeadgearConflictChoices.Should().BeEmpty();
            vmSettings.WigHeadgearChoiceReset.HasRememberedChoices.Should().BeFalse();
        });
    }
}
