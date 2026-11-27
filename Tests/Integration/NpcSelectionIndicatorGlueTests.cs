using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.Memory;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// The NPC list's side of the selection indicator (VM_NpcSelectionBar.SelectionIndicators.cs):
/// that the facts the rules need are gathered from the right places and the result lands on the
/// row. The three original states must come out as the very brushes they always were; the two
/// hatched states must appear and disappear with their causes; and the wig question must go to
/// the worker, only under 'Forward to Outfit', and only once per unchanged row.
///
/// <para>The bar is built without its constructor and given only the fields this code reads; the
/// back end behind the wig question is a stand-in. What is NOT exercised: the real
/// <see cref="WigForwarder"/> query from the list (its own test is
/// WigHeadgearConflictTests.PerSelectionQuery...), the 'Include Outfit' wording as chosen from a
/// resolved record (no load order here, so the output mode decides it), and the XAML that draws
/// the result.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class NpcSelectionIndicatorGlueTests
{
    private const string ModName = "High Poly NPC Overhaul";
    private static readonly FormKey NpcKey = MutagenFixtures.Fk("01327A:Skyrim.esm");

    private readonly WpfStaFixture _sta;
    public NpcSelectionIndicatorGlueTests(WpfStaFixture sta) => _sta = sta;

    private static readonly IReadOnlyList<WigHeadgearConflict> OneConflict = new[] { WigHeadgearConflictUiTests.OfficerConflict() };

    private sealed class Rig
    {
        public Settings Settings = new();
        public VM_NpcSelectionBar Bar = null!;
        public VM_ModSetting Mod = null!;
        public VM_NpcsMenuSelection Npc = null!;
        public WigHeadgearIndicatorScanner Scanner = null!;
        public readonly ConcurrentQueue<(WigConflictQuery Query, int ThreadId)> Asked = new();
        public IReadOnlyList<WigHeadgearConflict> Answer = OneConflict;

        public SolidColorBrush BarBrush(string property) =>
            (SolidColorBrush)typeof(VM_NpcSelectionBar)
                .GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Bar)!;

        /// <summary>Lets the worker finish, then lets the UI thread take its "these rows have an
        /// answer" message.</summary>
        public async Task Settle()
        {
            await Scanner.Idle.WaitAsync(TimeSpan.FromSeconds(30));
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static Rig Build(bool modHasWigs)
    {
        var rig = new Rig();

        // The mod as the Mods tab holds it: only what the indicator and the snapshot read.
        var mod = Reflect.Uninitialized<VM_ModSetting>();
        mod.DisplayName = ModName;
        Reflect.SetField(mod, "CorrespondingModKeys", new ObservableCollection<ModKey> { MutagenFixtures.Mk("HighPoly.esp") });
        Reflect.SetField(mod, "CorrespondingFolderPaths", new ObservableCollection<string> { @"C:\Mods\HighPoly" });
        Reflect.SetField(mod, "LockedFolderPaths", new ObservableCollection<string>());
        Reflect.SetField(mod, "MugShotFolderPaths", new ObservableCollection<string>());
        mod.ResourceOnlyModKeys = new HashSet<ModKey>();
        mod.PluginMergeInOverrides = new Dictionary<ModKey, bool>();
        mod.NpcPluginDisambiguation = new Dictionary<FormKey, ModKey>();
        mod.AvailablePluginsForNpcs = new Dictionary<FormKey, List<ModKey>>();
        mod.DetectedWigArmors = new HashSet<FormKey>();
        mod.DetectedWigArmatures = modHasWigs
            ? new HashSet<FormKey> { MutagenFixtures.Fk("000801:HighPoly.esp") }
            : new HashSet<FormKey>();
        mod.DetectedAntlerArmors = new HashSet<FormKey>();
        mod.DetectedAntlerArmatures = new HashSet<FormKey>();
        mod.DetectedAntlerHeadParts = new HashSet<FormKey>();
        mod.NpcWigSources = new Dictionary<FormKey, List<NpcWigSource>>();
        mod.Keywords = new ObservableCollection<string>();
        rig.Mod = mod;

        var mods = Reflect.Uninitialized<VM_Mods>();
        Reflect.SetField(mods, "_allModSettingsInternal", new List<VM_ModSetting> { mod });

        var npc = Reflect.Uninitialized<VM_NpcsMenuSelection>();
        Reflect.SetField(npc, "NpcFormKey", NpcKey);
        npc.IsInLoadOrder = true;
        npc.SelectedModHasData = true;
        npc.SelectedAppearanceModName = ModName;
        rig.Npc = npc;

        var consistency = Reflect.Uninitialized<NpcConsistencyProvider>();
        Reflect.SetField(consistency, "_selectedMods",
            new Dictionary<FormKey, (string ModName, FormKey NpcFormKey)> { [NpcKey] = (ModName, NpcKey) });

        var bar = Reflect.Uninitialized<VM_NpcSelectionBar>();
        Reflect.SetField(bar, "_settings", rig.Settings);
        Reflect.SetField(bar, "_environmentStateProvider", Reflect.Uninitialized<EnvironmentStateProvider>());
        Reflect.SetField(bar, "_lazyModsVm", new Lazy<VM_Mods>(() => mods));
        Reflect.SetField(bar, "_consistencyProvider", consistency);
        Reflect.SetField(bar, "AllNpcs", new List<VM_NpcsMenuSelection> { npc });
        Reflect.SetField(bar, "_npcVmLookup", new Dictionary<FormKey, VM_NpcsMenuSelection> { [NpcKey] = npc });
        rig.Bar = bar;

        // The real worker and the bar's real "answers are in" handler, around a stand-in back end.
        var onAnswered = (Action<IReadOnlyList<FormKey>>)Delegate.CreateDelegate(
            typeof(Action<IReadOnlyList<FormKey>>), bar,
            typeof(VM_NpcSelectionBar).GetMethod("OnWigConflictAnswers", BindingFlags.Instance | BindingFlags.NonPublic)!);
        rig.Scanner = new WigHeadgearIndicatorScanner(
            query =>
            {
                rig.Asked.Enqueue((query, Environment.CurrentManagedThreadId));
                return rig.Answer;
            },
            onAnswered);
        Reflect.SetField(bar, "_wigConflictScanner", rig.Scanner);

        return rig;
    }

    /// <summary>Runs a test body on the STA thread with the main-thread scheduler pointing at it,
    /// as in the app, so the worker's answers are marshalled back to this thread.</summary>
    private Task OnSta(Func<Task> body) => _sta.RunOnStaAsync(async () =>
    {
        using var guard = new StaticStateGuard(immediateSchedulers: false);
        FrontendVmHarness.InstallStaMainThreadScheduler();
        // A theme, as the app always has one: the indicator brushes are then the theme's, made on
        // this thread. (The bar's fixed fallback brushes belong to whichever thread first touched
        // the class, which in a test run need not be this one.)
        using var theme = new WigHeadgearUiRenderTests.AppliedTheme("Dark");
        await body();
    });

    // =============================================================================================
    // The three original states
    // =============================================================================================

    [Theory]
    [InlineData(false, true, "NpcIndicatorRedBrush")]     // not in the load order
    [InlineData(false, false, "NpcIndicatorRedBrush")]
    [InlineData(true, false, "NpcIndicatorPurpleBrush")]  // the mod has no data
    [InlineData(true, true, "NpcIndicatorGreenBrush")]    // valid
    public async Task OriginalStates_GetTheVeryBrushTheyAlwaysGot(bool inLoadOrder, bool modHasData, string brushProperty)
    {
        await OnSta(async () =>
        {
            var rig = Build(modHasWigs: false);
            rig.Npc.IsInLoadOrder = inLoadOrder;
            rig.Npc.SelectedModHasData = modHasData;
            // Facts that would raise a note on a valid row must not touch a red or purple one.
            rig.Mod.IncludeOutfits = !(inLoadOrder && modHasData);
            rig.Npc.WinningOverrideDressedByTemplate = !(inLoadOrder && modHasData);

            rig.Bar.RefreshSelectionIndicators();
            await rig.Settle();

            var expected = rig.BarBrush(brushProperty);
            rig.Npc.SelectionIndicatorBrush.Should().BeSameAs(expected, "the bar is drawn with the same brush as before");
            rig.Npc.SelectionIndicatorTextBrush.Should().BeSameAs(expected, "and so is the text in 'Text Color' style");
            rig.Npc.SelectionIndicatorIsHatched.Should().BeFalse();
            rig.Npc.SelectionIndicatorNote.Should().BeNull();
            rig.Asked.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ARowWithoutASelection_IsLeftAlone()
    {
        await OnSta(async () =>
        {
            var rig = Build(modHasWigs: false);
            rig.Npc.SelectedAppearanceModName = null;

            rig.Bar.RefreshSelectionIndicators();
            await rig.Settle();

            rig.Npc.SelectionIndicatorBrush.Should().BeNull();
            rig.Npc.SelectionIndicatorIsHatched.Should().BeFalse();
        });
    }

    // =============================================================================================
    // Green with orange hatches: 'Include Outfit' on a template-dressed NPC
    // =============================================================================================

    [Theory]
    [InlineData(OutputBackend.ESP, NpcSelectionIndicatorRules.OutfitNeverWornNote)]
    [InlineData(OutputBackend.SkyPatcher, NpcSelectionIndicatorRules.OutfitReplacesTemplateNote)]
    public async Task IncludeOutfitOnATemplateDressedNpc_IsHatchedOrange_WithTheNoteForTheOutputMode(
        OutputBackend backend, string expectedNote)
    {
        await OnSta(async () =>
        {
            var rig = Build(modHasWigs: false);
            rig.Settings.OutputBackend = backend;
            rig.Mod.IncludeOutfits = true;
            rig.Npc.WinningOverrideDressedByTemplate = true;

            rig.Bar.RefreshSelectionIndicators();
            await rig.Settle();

            var green = rig.BarBrush("NpcIndicatorGreenBrush");
            rig.Npc.SelectionIndicatorIsHatched.Should().BeTrue();
            rig.Npc.SelectionIndicatorNote.Should().Be(expectedNote);
            rig.Npc.SelectionIndicatorTextBrush.Should().BeSameAs(green, "the state is still a valid, green one");
            var hatched = rig.Npc.SelectionIndicatorBrush.Should().BeOfType<LinearGradientBrush>().Subject;
            hatched.IsFrozen.Should().BeTrue("one brush is shared by every row");
            hatched.GradientStops.Select(s => s.Color).Distinct().Should().BeEquivalentTo(new[]
            {
                green.Color,
                Colors.Orange, // the Dark theme's own orange, which shows well against its green
            });
            rig.Asked.Should().BeEmpty("the outfit note needs no record work");
        });
    }

    [Fact]
    public async Task TheOutfitNote_FollowsTheModsSettingAndTheNpcsOwnOverride()
    {
        await OnSta(async () =>
        {
            var rig = Build(modHasWigs: false);
            rig.Npc.WinningOverrideDressedByTemplate = true;

            rig.Mod.IncludeOutfits = false;
            rig.Bar.RefreshSelectionIndicators();
            rig.Npc.SelectionIndicatorIsHatched.Should().BeFalse("the mod does not include outfits");
            var plainGreen = rig.Npc.SelectionIndicatorBrush;

            rig.Settings.NpcOutfitOverrides[NpcKey] = OutfitOverride.Yes;
            rig.Bar.RefreshSelectionIndicators();
            rig.Npc.SelectionIndicatorIsHatched.Should().BeTrue("this NPC's own override includes the outfit");

            rig.Mod.IncludeOutfits = true;
            rig.Settings.NpcOutfitOverrides[NpcKey] = OutfitOverride.No;
            rig.Bar.RefreshSelectionIndicators();
            rig.Npc.SelectionIndicatorIsHatched.Should().BeFalse("this NPC's own override leaves the outfit out");
            rig.Npc.SelectionIndicatorBrush.Should().BeSameAs(plainGreen);
            rig.Npc.SelectionIndicatorNote.Should().BeNull();

            rig.Settings.NpcOutfitOverrides.Remove(NpcKey);
            rig.Npc.WinningOverrideDressedByTemplate = false;
            rig.Bar.RefreshSelectionIndicators();
            rig.Npc.SelectionIndicatorIsHatched.Should().BeFalse("no template dresses the NPC");
            await rig.Settle();
        });
    }

    // =============================================================================================
    // Green with red hatches: wig and headgear on the same slot
    // =============================================================================================

    [Fact]
    public async Task UnderForwardToOutfit_TheWigQuestionGoesToTheWorker_AndTheRowTurnsRedHatchedWhenAnswered()
    {
        await OnSta(async () =>
        {
            var rig = Build(modHasWigs: true);
            rig.Settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToOutfit;
            int uiThread = Environment.CurrentManagedThreadId;

            rig.Bar.RefreshSelectionIndicators();

            // Not answered where it was asked: the row is plain green until the worker reports.
            rig.Npc.SelectionIndicatorIsHatched.Should().BeFalse();
            await rig.Settle();

            var asked = rig.Asked.Should().ContainSingle().Subject;
            asked.ThreadId.Should().NotBe(uiThread, "the record work never runs on the UI thread");
            asked.Query.Key.Npc.Should().Be(NpcKey);
            asked.Query.Key.SourceNpc.Should().Be(NpcKey);
            asked.Query.Key.ModName.Should().Be(ModName);
            asked.Query.Key.WigMode.Should().Be(WigHandlingMode.ForwardToOutfit);
            asked.Query.ModSetting.DisplayName.Should().Be(ModName, "the worker gets a snapshot of the mod");
            asked.Query.ModSetting.DetectedWigArmatures.Should().HaveCount(1);

            var green = rig.BarBrush("NpcIndicatorGreenBrush");
            var red = rig.BarBrush("NpcIndicatorRedBrush");
            rig.Npc.SelectionIndicatorIsHatched.Should().BeTrue("the answer redraws the row");
            rig.Npc.SelectionIndicatorTextBrush.Should().BeSameAs(green);
            rig.Npc.SelectionIndicatorBrush.Should().BeOfType<LinearGradientBrush>()
                .Which.GradientStops.Select(s => s.Color).Distinct().Should().BeEquivalentTo(new[] { green.Color, red.Color });
            rig.Npc.SelectionIndicatorNote.Should().Be(
                "Valid selection with a conflict: wig HighPoly_WigAA_HairFemaleNord03 (slot 31) would be added to " +
                "outfit ImperialOfficerOutfit, which holds Imperial Officer's Helmet (ArmorImperialHelmetOfficer) " +
                "(slots 31 and 42). Skyrim shows only one. Start a run to choose, or change the mod's wig handling.");

            // Nothing changed: refreshing again (theme change, tab shown again) asks nothing.
            rig.Bar.RefreshSelectionIndicators();
            rig.Bar.RefreshSelectionIndicators();
            await rig.Settle();
            rig.Asked.Should().ContainSingle("an unchanged row is answered from the cache");
            rig.Npc.SelectionIndicatorIsHatched.Should().BeTrue();
        });
    }

    [Fact]
    public async Task AStoredChoice_DecidesWhetherTheConflictStillShows()
    {
        await OnSta(async () =>
        {
            var rig = Build(modHasWigs: true);
            rig.Settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToOutfit;
            rig.Bar.RefreshSelectionIndicators();
            await rig.Settle();
            rig.Npc.SelectionIndicatorIsHatched.Should().BeTrue();

            foreach (var settled in new[] { WigHeadgearChoice.Omit, WigHeadgearChoice.ConvertToHeadParts })
            {
                rig.Settings.SetWigHeadgearChoice(new[] { NpcKey }, settled);
                rig.Bar.RefreshSelectionIndicators();
                rig.Npc.SelectionIndicatorIsHatched.Should().BeFalse($"a stored '{settled}' clears the red hatch");
                rig.Npc.SelectionIndicatorNote.Should().BeNull();
            }

            rig.Settings.SetWigHeadgearChoice(new[] { NpcKey }, WigHeadgearChoice.Forward);
            rig.Bar.RefreshSelectionIndicators();
            rig.Npc.SelectionIndicatorIsHatched.Should().BeTrue("a stored 'forward' keeps it");
            rig.Npc.SelectionIndicatorNote.Should().Contain("You chose to forward the wig for this NPC.");

            rig.Settings.ClearWigHeadgearChoices();
            rig.Bar.RefreshSelectionIndicators();
            rig.Npc.SelectionIndicatorNote.Should().EndWith("Start a run to choose, or change the mod's wig handling.");

            await rig.Settle();
            rig.Asked.Should().ContainSingle("none of this needed the records read again");
        });
    }

    [Fact]
    public async Task WhenBothNotesApply_RedHatchesWin_AndTheTooltipCarriesBoth()
    {
        await OnSta(async () =>
        {
            var rig = Build(modHasWigs: true);
            rig.Settings.OutputBackend = OutputBackend.SkyPatcher;
            rig.Settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToOutfit;
            rig.Mod.IncludeOutfits = true;
            rig.Npc.WinningOverrideDressedByTemplate = true;

            rig.Bar.RefreshSelectionIndicators();
            rig.Npc.SelectionIndicatorNote.Should().Be(NpcSelectionIndicatorRules.OutfitReplacesTemplateNote,
                "until the wig answer is in, the row shows what is already known");
            await rig.Settle();

            var green = rig.BarBrush("NpcIndicatorGreenBrush");
            var red = rig.BarBrush("NpcIndicatorRedBrush");
            rig.Npc.SelectionIndicatorBrush.Should().BeOfType<LinearGradientBrush>()
                .Which.GradientStops.Select(s => s.Color).Distinct().Should().BeEquivalentTo(new[] { green.Color, red.Color });
            rig.Npc.SelectionIndicatorNote.Should().StartWith("Valid selection with a conflict: wig ");
            rig.Npc.SelectionIndicatorNote.Should().EndWith(NpcSelectionIndicatorRules.OutfitReplacesTemplateNote);
        });
    }

    [Theory]
    [InlineData(WigHandlingMode.None, true)]
    [InlineData(WigHandlingMode.ForwardToSkin, true)]
    [InlineData(WigHandlingMode.ConvertToHeadParts, true)]
    [InlineData(WigHandlingMode.ForwardToOutfit, false)]  // the right mode, but the mod carries no wig
    public async Task UnlessTheModsEffectiveWigModeIsForwardToOutfit_NothingIsAskedAtAll(
        WigHandlingMode mode, bool modHasWigs)
    {
        await OnSta(async () =>
        {
            var rig = Build(modHasWigs);
            rig.Settings.DefaultWigHandlingMode = mode;

            rig.Bar.RefreshSelectionIndicators();
            await rig.Settle();

            rig.Asked.Should().BeEmpty();
            rig.Scanner.QueriesRun.Should().Be(0);
            rig.Npc.SelectionIndicatorIsHatched.Should().BeFalse();
            rig.Npc.SelectionIndicatorBrush.Should().BeSameAs(rig.BarBrush("NpcIndicatorGreenBrush"));
        });
    }

    [Fact]
    public async Task WhileAPatchRunIsInProgress_NothingIsAsked_AndTheRowAsksWhenItEnds()
    {
        await OnSta(async () =>
        {
            var rig = Build(modHasWigs: true);
            rig.Settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToOutfit;
            var coordinator = new WigHeadgearChoiceCoordinator(rig.Settings);
            Reflect.SetField(rig.Bar, "_wigHeadgearChoices", coordinator);

            var run = coordinator.BeginRun();
            rig.Bar.RefreshSelectionIndicators();
            await rig.Settle();
            rig.Asked.Should().BeEmpty("the patcher is using the same plugins on its own threads");
            rig.Npc.SelectionIndicatorIsHatched.Should().BeFalse();

            run.Dispose();
            rig.Bar.RefreshSelectionIndicators(); // what the coordinator's Changed does in the app
            await rig.Settle();
            rig.Asked.Should().ContainSingle();
            rig.Npc.SelectionIndicatorIsHatched.Should().BeTrue();
        });
    }

    [Fact]
    public async Task APerModWigMode_OverridesTheDefault_AndAChangeOfModeAsksAgain()
    {
        await OnSta(async () =>
        {
            var rig = Build(modHasWigs: true);
            rig.Settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToOutfit;
            rig.Mod.OverrideWigHandlingMode = WigHandlingMode.ConvertToHeadParts;

            rig.Bar.RefreshSelectionIndicators();
            await rig.Settle();
            rig.Asked.Should().BeEmpty("this mod converts its wigs, whatever the default says");

            rig.Mod.OverrideWigHandlingMode = null;
            rig.Bar.RefreshSelectionIndicators();
            await rig.Settle();
            rig.Asked.Should().ContainSingle();
            rig.Npc.SelectionIndicatorIsHatched.Should().BeTrue();

            // A setting that decides which outfit the NPC wears changes: the stored answer is
            // for another question, so the records are read again.
            rig.Answer = Array.Empty<WigHeadgearConflict>();
            rig.Mod.IncludeOutfits = true;
            rig.Bar.RefreshSelectionIndicators();
            await rig.Settle();
            rig.Asked.Should().HaveCount(2);
            rig.Npc.SelectionIndicatorIsHatched.Should().BeFalse("the new answer says there is no conflict");
        });
    }
}
