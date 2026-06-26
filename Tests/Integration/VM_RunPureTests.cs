using System.Collections;
using System.Reactive.Subjects;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Exercises the deterministic, in-memory helper surface of <see cref="VM_Run"/> WITHOUT
/// running its heavy constructor or standing up a DI graph. A <see cref="Reflect.Uninitialized{T}"/>
/// VM is hydrated with only the private fields each method reads:
/// <c>_settings</c>, <c>_environmentStateProvider</c>, <c>_aux</c> and the private
/// <c>_logMessageSubject</c>.
///
/// Covered seams:
/// * <see cref="VM_Run.ALL_NPCS_GROUP"/> constant value.
/// * private <c>CreatePatchingBatches</c> — empty input, no-split single batch, MaxNpcs
///   chunking with numeric suffixes, ByGender splitting using in-memory NPCs, and the
///   "group size == MaxNpcs" boundary (no suffix).
/// * private <c>BuildNpcLogTargets</c> — null/empty <see cref="Settings.NpcsToLog"/>,
///   <see cref="FormKey.Null"/> entries skipped, and the null-LinkCache ToString fallback.
/// * public <c>AppendLog</c> verbose-mode gating across the 4 isError/forceLog/verbose combos.
/// * the private nested <c>PatchingBatch</c> record's value-equality (via reflection, since
///   the type is private and cannot be named).
///
/// Runs in the integration collection on the STA fixture because the VM is a
/// <see cref="ReactiveUI.ReactiveObject"/> (setting the <c>IsVerboseModeEnabled</c>
/// [Reactive] property and the <c>_logMessageSubject</c> pipeline touch ReactiveUI
/// scheduling); an immediate-scheduler <see cref="StaticStateGuard"/> keeps it deterministic.
///
/// NOTE: The SplitOutputByRace branch of CreatePatchingBatches resolves
/// <c>npc.Race</c> against <c>_environmentStateProvider.LinkCache</c>; building a real
/// link cache requires a live load order, so that single case is skipped here (a separate
/// game-install wave covers race-split). Gender splitting needs no link cache and is covered.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class VM_RunPureTests
{
    private readonly WpfStaFixture _sta;
    public VM_RunPureTests(WpfStaFixture sta) => _sta = sta;

    // ------------------------------------------------------------------
    // Construction helper: an uninitialized VM_Run with just the fields a
    // given helper reads. The constructor is never run, so no DI graph,
    // no subscriptions, no Mutagen environment are needed.
    // ------------------------------------------------------------------

    /// <summary>An EnvironmentStateProvider whose private _environment is left null -> LinkCache == null.</summary>
    private static EnvironmentStateProvider NullEnvProvider() =>
        Reflect.Uninitialized<EnvironmentStateProvider>();

    private static VM_Run MakeVm(
        Settings settings,
        EnvironmentStateProvider? env = null,
        Auxilliary? aux = null,
        Subject<string>? logSubject = null)
    {
        var vm = Reflect.Uninitialized<VM_Run>();
        Reflect.SetField(vm, "_settings", settings);
        Reflect.SetField(vm, "_environmentStateProvider", env ?? NullEnvProvider());
        Reflect.SetField(vm, "_aux", aux!); // many helpers never touch _aux; null is fine for those
        Reflect.SetField(vm, "_logMessageSubject", logSubject ?? new Subject<string>());
        return vm;
    }

    /// <summary>
    /// Builds a list of (FormKey -> ScreeningResult) selections from freshly-created in-memory NPCs.
    /// Each NPC is its own WinningNpcOverride; gender is driven by the <paramref name="female"/> flags.
    /// </summary>
    private static List<KeyValuePair<FormKey, ScreeningResult>> Selections(SkyrimMod mod, params bool[] female)
    {
        var modSetting = new ModSetting { DisplayName = "AppMod" };
        var list = new List<KeyValuePair<FormKey, ScreeningResult>>();
        for (int i = 0; i < female.Length; i++)
        {
            var npc = MutagenFixtures.NewNpc(mod, editorId: $"Npc{i}", female: female[i]);
            var result = new ScreeningResult(true, npc, modSetting, npc.FormKey);
            list.Add(new KeyValuePair<FormKey, ScreeningResult>(npc.FormKey, result));
        }

        return list;
    }

    // --- reflection accessors for the private nested PatchingBatch record ---

    private static string BatchSuffix(object batch) => Reflect.GetField<string>(batch, "<Suffix>k__BackingField");

    private static IList BatchSelections(object batch) =>
        Reflect.GetField<IList>(batch, "<Selections>k__BackingField");

    private static List<object> InvokeCreateBatches(
        VM_Run vm, List<KeyValuePair<FormKey, ScreeningResult>> selections)
    {
        var result = Reflect.Invoke<IEnumerable>(vm, "CreatePatchingBatches", selections)!;
        return result.Cast<object>().ToList();
    }

    // ==================================================================
    // ALL_NPCS_GROUP constant
    // ==================================================================

    [Fact]
    public async Task AllNpcsGroup_ConstantHasExpectedLiteral()
    {
        await _sta.RunOnStaAsync(() =>
        {
            VM_Run.ALL_NPCS_GROUP.Should().Be("<All NPCs>");
        });
    }

    // ==================================================================
    // CreatePatchingBatches
    // ==================================================================

    [Fact]
    public async Task CreatePatchingBatches_EmptyInput_ReturnsEmpty()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = MakeVm(new Settings { SplitOutputByGender = false, SplitOutputByRace = false });

            var batches = InvokeCreateBatches(vm, new List<KeyValuePair<FormKey, ScreeningResult>>());

            batches.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task CreatePatchingBatches_NoSplitCriteria_ReturnsSingleUnsuffixedBatchWithAllNpcs()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var mod = MutagenFixtures.NewMod("App.esp");
            // No gender/race split, no max -> everything lands in one batch with an empty suffix.
            var vm = MakeVm(new Settings
            {
                SplitOutputByGender = false,
                SplitOutputByRace = false,
                SplitOutputMaxNpcs = null
            });

            var selections = Selections(mod, female: new[] { false, true, false, true });
            var batches = InvokeCreateBatches(vm, selections);

            batches.Should().HaveCount(1);
            BatchSuffix(batches[0]).Should().BeEmpty();
            BatchSelections(batches[0]).Count.Should().Be(4);
        });
    }

    [Fact]
    public async Task CreatePatchingBatches_MaxNpcsChunking_AddsNumericSuffixWhenSplit()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var mod = MutagenFixtures.NewMod("App.esp");
            // 5 NPCs, max 2 per plugin, no gender/race -> chunks of [2,2,1] each suffixed 1/2/3.
            var vm = MakeVm(new Settings
            {
                SplitOutputByGender = false,
                SplitOutputByRace = false,
                SplitOutputMaxNpcs = 2
            });

            var selections = Selections(mod, female: new[] { false, false, false, false, false });
            var batches = InvokeCreateBatches(vm, selections);

            batches.Should().HaveCount(3);
            BatchSuffix(batches[0]).Should().Be("1");
            BatchSuffix(batches[1]).Should().Be("2");
            BatchSuffix(batches[2]).Should().Be("3");
            BatchSelections(batches[0]).Count.Should().Be(2);
            BatchSelections(batches[1]).Count.Should().Be(2);
            BatchSelections(batches[2]).Count.Should().Be(1);
            // No NPC is dropped or duplicated across the chunks.
            batches.Sum(b => BatchSelections(b).Count).Should().Be(5);
        });
    }

    [Fact]
    public async Task CreatePatchingBatches_GroupSizeEqualsMaxNpcs_NoNumericSuffix()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var mod = MutagenFixtures.NewMod("App.esp");
            // Exactly 2 NPCs with max 2: totalInGroup (2) is NOT > max (2) -> single batch, no suffix.
            var vm = MakeVm(new Settings
            {
                SplitOutputByGender = false,
                SplitOutputByRace = false,
                SplitOutputMaxNpcs = 2
            });

            var selections = Selections(mod, female: new[] { false, false });
            var batches = InvokeCreateBatches(vm, selections);

            batches.Should().HaveCount(1);
            BatchSuffix(batches[0]).Should().BeEmpty("group size == max means the group was not split");
            BatchSelections(batches[0]).Count.Should().Be(2);
        });
    }

    [Fact]
    public async Task CreatePatchingBatches_ByGender_ProducesTwoBatchesSuffixedByGender()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var mod = MutagenFixtures.NewMod("App.esp");
            // 3 male + 2 female, gender split only -> two batches keyed "Male" / "Female".
            var vm = MakeVm(new Settings
            {
                SplitOutputByGender = true,
                SplitOutputByRace = false,
                SplitOutputMaxNpcs = null
            });

            var selections = Selections(mod, female: new[] { false, true, false, true, false });
            var batches = InvokeCreateBatches(vm, selections);

            batches.Should().HaveCount(2);
            var suffixes = batches.Select(BatchSuffix).ToList();
            suffixes.Should().BeEquivalentTo(new[] { "Male", "Female" });

            var male = batches.Single(b => BatchSuffix(b) == "Male");
            var femaleBatch = batches.Single(b => BatchSuffix(b) == "Female");
            BatchSelections(male).Count.Should().Be(3);
            BatchSelections(femaleBatch).Count.Should().Be(2);
        });
    }

    [Fact]
    public async Task CreatePatchingBatches_ByGenderWithMaxNpcs_AppendsGenderThenNumericSuffix()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var mod = MutagenFixtures.NewMod("App.esp");
            // 3 males, max 2 -> the male group splits into Male_1 (2 NPCs) and Male_2 (1 NPC).
            var vm = MakeVm(new Settings
            {
                SplitOutputByGender = true,
                SplitOutputByRace = false,
                SplitOutputMaxNpcs = 2
            });

            var selections = Selections(mod, female: new[] { false, false, false });
            var batches = InvokeCreateBatches(vm, selections);

            batches.Should().HaveCount(2);
            batches.Select(BatchSuffix).Should().BeEquivalentTo(new[] { "Male_1", "Male_2" });
            BatchSelections(batches.Single(b => BatchSuffix(b) == "Male_1")).Count.Should().Be(2);
            BatchSelections(batches.Single(b => BatchSuffix(b) == "Male_2")).Count.Should().Be(1);
        });
    }

    // NOTE: SplitOutputByRace requires resolving npc.Race against a real link cache
    // (_environmentStateProvider.LinkCache); that needs a live load order, so the
    // race-split case is intentionally not covered here.

    // ==================================================================
    // BuildNpcLogTargets
    // ==================================================================

    [Fact]
    public async Task BuildNpcLogTargets_NullNpcsToLog_ReturnsEmpty()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = MakeVm(new Settings { NpcsToLog = null! });

            var targets = Reflect.Invoke<IList>(vm, "BuildNpcLogTargets")!;
            targets.Count.Should().Be(0);
        });
    }

    [Fact]
    public async Task BuildNpcLogTargets_EmptyNpcsToLog_ReturnsEmpty()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = MakeVm(new Settings { NpcsToLog = new List<FormKey>() });

            var targets = Reflect.Invoke<IList>(vm, "BuildNpcLogTargets")!;
            targets.Count.Should().Be(0);
        });
    }

    [Fact]
    public async Task BuildNpcLogTargets_NullFormKeysAreSkipped()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var fk = MutagenFixtures.Fk("000800:Real.esp");
            var settings = new Settings
            {
                NpcsToLog = new List<FormKey> { FormKey.Null, fk, FormKey.Null }
            };
            // Null LinkCache -> display falls back to FormKey.ToString().
            var vm = MakeVm(settings, env: NullEnvProvider());

            var targets = Reflect.Invoke<IList>(vm, "BuildNpcLogTargets")!;

            // Only the single non-null FormKey survives.
            targets.Count.Should().Be(1);
            var tuple = ((FormKey FormKey, string DisplayString))targets[0]!;
            tuple.FormKey.Should().Be(fk);
            tuple.DisplayString.Should().Be(fk.ToString());
        });
    }

    [Fact]
    public async Task BuildNpcLogTargets_NullLinkCache_UsesFormKeyToStringForDisplay()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var fk1 = MutagenFixtures.Fk("000ABC:A.esp");
            var fk2 = MutagenFixtures.Fk("000DEF:B.esp");
            var vm = MakeVm(
                new Settings { NpcsToLog = new List<FormKey> { fk1, fk2 } },
                env: NullEnvProvider());

            var targets = Reflect.Invoke<IList>(vm, "BuildNpcLogTargets")!;

            targets.Count.Should().Be(2);
            var t0 = ((FormKey FormKey, string DisplayString))targets[0]!;
            var t1 = ((FormKey FormKey, string DisplayString))targets[1]!;
            t0.FormKey.Should().Be(fk1);
            t0.DisplayString.Should().Be(fk1.ToString());
            t1.FormKey.Should().Be(fk2);
            t1.DisplayString.Should().Be(fk2.ToString());
        });
    }

    // ==================================================================
    // AppendLog verbose gating
    // ==================================================================

    private static List<string> CaptureAppendLog(VM_Run vm, Subject<string> subject, Action emit)
    {
        var captured = new List<string>();
        using var sub = subject.Subscribe(captured.Add);
        emit();
        return captured;
    }

    [Fact]
    public async Task AppendLog_VerboseOff_PlainMessageSuppressed()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var subject = new Subject<string>();
            var vm = MakeVm(new Settings(), logSubject: subject);
            vm.IsVerboseModeEnabled = false;

            var captured = CaptureAppendLog(vm, subject, () => vm.AppendLog("routine"));

            captured.Should().BeEmpty("plain messages are gated out when verbose mode is off");
        });
    }

    [Fact]
    public async Task AppendLog_VerboseOff_ErrorMessageEmitted()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var subject = new Subject<string>();
            var vm = MakeVm(new Settings(), logSubject: subject);
            vm.IsVerboseModeEnabled = false;

            var captured = CaptureAppendLog(vm, subject, () => vm.AppendLog("boom", isError: true));

            captured.Should().ContainSingle().Which.Should().Be("boom");
        });
    }

    [Fact]
    public async Task AppendLog_VerboseOff_ForceLogMessageEmitted()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var subject = new Subject<string>();
            var vm = MakeVm(new Settings(), logSubject: subject);
            vm.IsVerboseModeEnabled = false;

            var captured = CaptureAppendLog(vm, subject, () => vm.AppendLog("important", forceLog: true));

            captured.Should().ContainSingle().Which.Should().Be("important");
        });
    }

    [Fact]
    public async Task AppendLog_VerboseOn_PlainMessageEmitted()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var subject = new Subject<string>();
            var vm = MakeVm(new Settings(), logSubject: subject);
            vm.IsVerboseModeEnabled = true;

            var captured = CaptureAppendLog(vm, subject, () => vm.AppendLog("verbose routine"));

            captured.Should().ContainSingle().Which.Should().Be("verbose routine");
        });
    }

    [Fact]
    public async Task AppendLog_VerboseOn_AllFlagCombinationsEmit()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var subject = new Subject<string>();
            var vm = MakeVm(new Settings(), logSubject: subject);
            vm.IsVerboseModeEnabled = true;

            var captured = CaptureAppendLog(vm, subject, () =>
            {
                vm.AppendLog("plain");
                vm.AppendLog("err", isError: true);
                vm.AppendLog("forced", forceLog: true);
                vm.AppendLog("both", isError: true, forceLog: true);
            });

            captured.Should().Equal("plain", "err", "forced", "both");
        });
    }

    // ==================================================================
    // private nested PatchingBatch record value-equality
    // ==================================================================

    [Fact]
    public async Task PatchingBatch_RecordEquality_IsValueBased()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var mod = MutagenFixtures.NewMod("App.esp");
            // Two batches produced from the SAME selection list with the same suffix should be
            // value-equal (positional record), while different suffixes / contents differ.
            var vm = MakeVm(new Settings
            {
                SplitOutputByGender = false,
                SplitOutputByRace = false,
                SplitOutputMaxNpcs = null
            });

            var selections = Selections(mod, female: new[] { false, false });

            // Construct the private PatchingBatch via its compiler-generated constructor so we can
            // assert record value-equality directly (the type cannot be named from the test asm).
            var batchType = typeof(VM_Run)
                .GetNestedType("PatchingBatch", System.Reflection.BindingFlags.NonPublic)!;
            var ctor = batchType.GetConstructors().Single();

            var b1 = ctor.Invoke(new object[] { "Suffix", selections });
            var b2 = ctor.Invoke(new object[] { "Suffix", selections });
            var b3 = ctor.Invoke(new object[] { "Other", selections });

            b1.Equals(b2).Should().BeTrue("records with equal positional members are value-equal");
            b1.GetHashCode().Should().Be(b2.GetHashCode());
            b1.Equals(b3).Should().BeFalse("a different Suffix makes the records unequal");
        });
    }
}
