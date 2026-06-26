using System.Collections.ObjectModel;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Deterministic, env-free helpers on <see cref="VM_Settings"/> (and its small companion
/// row VMs). The full VM constructor needs the whole DI graph + a resolved Skyrim
/// environment, so these tests never run it: a heavy-ctor instance is allocated via
/// <see cref="Reflect.Uninitialized{T}"/> and only the handful of private fields each
/// helper reads are populated before invoking it.
///
/// ReactiveUI 20.x keeps a <see cref="VM_Settings"/>'s change-notification state in a
/// static ConditionalWeakTable keyed off object identity (not in ctor-initialised
/// instance fields), so the <c>[Reactive]</c> property setters that helpers like
/// <c>UpdateHasMissingMasterMods</c> write are safe on an uninitialised instance. Tests
/// that touch that reactive path still run on the STA fixture under a
/// <see cref="StaticStateGuard"/> (immediate schedulers + global-state restore), matching
/// the integration-collection contract. The pure static / model-only helpers are plain
/// facts — they touch no WPF, reactive, or process-global state.
///
/// SKIPPED (require a live env / unreachable without the DI graph): every command body and
/// async flow (they dereference the lazy VMs, file dialogs, or the environment provider);
/// the model-mirroring constructor itself.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class VM_SettingsPureTests
{
    private readonly WpfStaFixture _sta;
    public VM_SettingsPureTests(WpfStaFixture sta) => _sta = sta;

    private static VM_Settings Bare() => Reflect.Uninitialized<VM_Settings>();

    // =====================================================================
    // IsValidJson (private instance; reads no fields)
    // =====================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void IsValidJson_NullOrWhitespace_IsFalse(string? input)
    {
        var vm = Bare();
        Reflect.Invoke<bool>(vm, "IsValidJson", input).Should().BeFalse();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"a\":1,\"b\":[1,2,3],\"c\":{\"d\":null}}")]
    [InlineData("{ \"name\": \"hero\" }")]
    public void IsValidJson_ValidObject_IsTrue(string input)
    {
        var vm = Bare();
        Reflect.Invoke<bool>(vm, "IsValidJson", input).Should().BeTrue();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1,2,3]")]
    [InlineData("[{\"x\":1},{\"x\":2}]")]
    public void IsValidJson_ValidArray_IsTrue(string input)
    {
        var vm = Bare();
        Reflect.Invoke<bool>(vm, "IsValidJson", input).Should().BeTrue();
    }

    [Theory]
    [InlineData("{ not json")]          // unterminated object
    [InlineData("{\"a\":}")]            // missing value
    [InlineData("just some words")]     // garbage / bareword
    public void IsValidJson_MalformedOrGarbage_IsFalse(string input)
    {
        var vm = Bare();
        // The production catch swallows JsonReaderException; these inputs surface as
        // reader errors, so a clean false (not a thrown exception) is the contract.
        Reflect.Invoke<bool>(vm, "IsValidJson", input).Should().BeFalse();
    }

    [Fact]
    public void IsValidJson_UnterminatedArray_CurrentlyThrows_KnownRobustnessGap()
    {
        // KNOWN GAP found by this suite: VM_Settings.IsValidJson only catches
        // JsonReaderException, but an untyped JsonConvert.DeserializeObject("[1,2,")
        // surfaces a JsonWriterException, which escapes the guard. A validation helper
        // arguably should return false for ALL malformed JSON (catch JsonException).
        // Documented here rather than hidden; flip to .Should().BeFalse() if the catch is widened.
        var vm = Bare();
        var act = () => Reflect.Invoke<bool>(vm, "IsValidJson", "[1,2,");
        act.Should().Throw<Exception>("only JsonReaderException is currently guarded");
    }

    // =====================================================================
    // FormatElapsed (private static)
    // =====================================================================

    private static string FormatElapsed(TimeSpan ts) =>
        Reflect.InvokeStatic<VM_Settings, string>("FormatElapsed", ts)!;

    [Fact]
    public void FormatElapsed_Zero_IsBareSeconds()
    {
        FormatElapsed(TimeSpan.Zero).Should().Be("0s");
    }

    [Fact]
    public void FormatElapsed_UnderOneMinute_IsBareSeconds()
    {
        FormatElapsed(TimeSpan.FromSeconds(5)).Should().Be("5s");
        FormatElapsed(TimeSpan.FromSeconds(59)).Should().Be("59s");
    }

    [Fact]
    public void FormatElapsed_OneMinuteAndFiveSeconds_PadsSeconds()
    {
        FormatElapsed(TimeSpan.FromSeconds(65)).Should().Be("1m 05s");
    }

    [Fact]
    public void FormatElapsed_AtOneMinuteBoundary_SwitchesToMinutesForm()
    {
        FormatElapsed(TimeSpan.FromSeconds(60)).Should().Be("1m 00s");
    }

    [Fact]
    public void FormatElapsed_OneHourOneMinuteOneSecond_PadsBoth()
    {
        FormatElapsed(TimeSpan.FromSeconds(3661)).Should().Be("1h 01m 01s");
    }

    [Fact]
    public void FormatElapsed_AtOneHourBoundary_SwitchesToHoursForm()
    {
        FormatElapsed(TimeSpan.FromHours(1)).Should().Be("1h 00m 00s");
    }

    [Fact]
    public void FormatElapsed_LargeHourCount_DoesNotPadHours()
    {
        // 25h 02m 03s — hour component uses (int)TotalHours, no D2 padding.
        FormatElapsed(new TimeSpan(25, 2, 3)).Should().Be("25h 02m 03s");
    }

    // =====================================================================
    // ResolveOutputModFolderForEnv (private instance; reads _model)
    // =====================================================================

    private static VM_Settings WithModel(Settings model)
    {
        var vm = Bare();
        Reflect.SetField(vm, "_model", model);
        return vm;
    }

    [Fact]
    public void ResolveOutputModFolderForEnv_NullModel_ReturnsNull()
    {
        // _model is left null (uninitialised) -> the null-conditional chain yields null.
        Reflect.Invoke<string>(Bare(), "ResolveOutputModFolderForEnv").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveOutputModFolderForEnv_BlankOutputDir_ReturnsNull(string outDir)
    {
        var vm = WithModel(new Settings { OutputDirectory = outDir, ModsFolder = @"C:\Mods" });
        Reflect.Invoke<string>(vm, "ResolveOutputModFolderForEnv").Should().BeNull();
    }

    [Fact]
    public void ResolveOutputModFolderForEnv_RootedPath_ReturnedVerbatim()
    {
        var rooted = @"D:\Skyrim\Output";
        var vm = WithModel(new Settings { OutputDirectory = rooted, ModsFolder = @"C:\Mods" });
        Reflect.Invoke<string>(vm, "ResolveOutputModFolderForEnv").Should().Be(rooted);
    }

    [Fact]
    public void ResolveOutputModFolderForEnv_RelativePath_CombinedWithModsFolder()
    {
        var vm = WithModel(new Settings { OutputDirectory = "NPC Output", ModsFolder = @"C:\Mods" });
        var expected = System.IO.Path.Combine(@"C:\Mods", "NPC Output");
        Reflect.Invoke<string>(vm, "ResolveOutputModFolderForEnv").Should().Be(expected);
    }

    [Fact]
    public void ResolveOutputModFolderForEnv_RelativePath_EmptyModsFolder_CombinesWithEmpty()
    {
        var vm = WithModel(new Settings { OutputDirectory = "Out", ModsFolder = string.Empty });
        var expected = System.IO.Path.Combine(string.Empty, "Out");
        Reflect.Invoke<string>(vm, "ResolveOutputModFolderForEnv").Should().Be(expected);
    }

    // =====================================================================
    // ApplyNonAppearanceFilter (private instance)
    //   reads: CachedNonAppearanceMods (collection), NonAppearanceModFilterText (reactive)
    //   writes: FilteredNonAppearanceMods (collection)
    // Runs on STA under StaticStateGuard because reading/writing the reactive filter-text
    // property exercises the ReactiveUI change machinery.
    // =====================================================================

    private static CachedNonAppearanceModEntry Entry(string path) =>
        new(path, "no FaceGen found", null);

    private static void SeedNonAppearance(VM_Settings vm, IEnumerable<CachedNonAppearanceModEntry> source, string filter)
    {
        // Read-only auto-property collections: plain compiler backing fields (not Fody-woven).
        Reflect.SetField(vm, "<CachedNonAppearanceMods>k__BackingField",
            new ObservableCollection<CachedNonAppearanceModEntry>(source));
        Reflect.SetField(vm, "<FilteredNonAppearanceMods>k__BackingField",
            new ObservableCollection<CachedNonAppearanceModEntry>());
        // [Reactive] property: set via its setter so the getter the helper reads stays
        // consistent regardless of how Fody named the backing field.
        vm.NonAppearanceModFilterText = filter;
    }

    [Fact]
    public async Task ApplyNonAppearanceFilter_NullCache_EarlyReturnsWithoutThrowing()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = Bare();
            // CachedNonAppearanceMods backing field left null -> guarded early return.
            // FilteredNonAppearanceMods must therefore not even be touched.
            var act = () => Reflect.InvokeVoid(vm, "ApplyNonAppearanceFilter");
            act.Should().NotThrow();
        });
    }

    [Fact]
    public async Task ApplyNonAppearanceFilter_BlankFilter_ShowsEveryEntry()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = Bare();
            var a = Entry(@"C:\Mods\Alpha");
            var b = Entry(@"C:\Mods\Beta");
            SeedNonAppearance(vm, new[] { a, b }, "   ");

            Reflect.InvokeVoid(vm, "ApplyNonAppearanceFilter");

            vm.FilteredNonAppearanceMods.Should().Equal(a, b);
        });
    }

    [Fact]
    public async Task ApplyNonAppearanceFilter_FilenameSubstringMatch_IsCaseInsensitive()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = Bare();
            var apachii = Entry(@"C:\Mods\Apachii Hair");
            var ks = Entry(@"C:\Mods\KS Hairdos");
            SeedNonAppearance(vm, new[] { apachii, ks }, "apachii");

            Reflect.InvokeVoid(vm, "ApplyNonAppearanceFilter");

            // Filter matches the file-name component only, case-insensitively.
            vm.FilteredNonAppearanceMods.Should().ContainSingle().Which.Should().BeSameAs(apachii);
        });
    }

    [Fact]
    public async Task ApplyNonAppearanceFilter_NoMatch_LeavesFilteredEmpty_AndClearsStale()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = Bare();
            SeedNonAppearance(vm, new[] { Entry(@"C:\Mods\Alpha") }, "zzz-no-such-mod");
            // Pre-stuff a stale row to prove the helper clears before re-filtering.
            vm.FilteredNonAppearanceMods.Add(Entry(@"C:\Mods\Stale"));

            Reflect.InvokeVoid(vm, "ApplyNonAppearanceFilter");

            vm.FilteredNonAppearanceMods.Should().BeEmpty();
        });
    }

    // =====================================================================
    // ApplyIgnoredModFilter (private instance)
    //   reads: IgnoredModFilterText (reactive), IgnoredMods (collection)
    //   writes: FilteredIgnoredMods (collection)
    // =====================================================================

    private static void SeedIgnored(VM_Settings vm, IEnumerable<string> ignored, string filter)
    {
        Reflect.SetField(vm, "<IgnoredMods>k__BackingField", new ObservableCollection<string>(ignored));
        Reflect.SetField(vm, "<FilteredIgnoredMods>k__BackingField", new ObservableCollection<string>());
        vm.IgnoredModFilterText = filter;
    }

    [Fact]
    public async Task ApplyIgnoredModFilter_BlankFilter_ShowsEveryPath()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = Bare();
            SeedIgnored(vm, new[] { @"C:\Mods\One", @"C:\Mods\Two" }, string.Empty);

            Reflect.InvokeVoid(vm, "ApplyIgnoredModFilter");

            vm.FilteredIgnoredMods.Should().Equal(@"C:\Mods\One", @"C:\Mods\Two");
        });
    }

    [Fact]
    public async Task ApplyIgnoredModFilter_FilenameMatch_IsCaseInsensitive()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = Bare();
            SeedIgnored(vm, new[] { @"C:\Mods\KeepThis", @"C:\Mods\DropThat" }, "keep");

            Reflect.InvokeVoid(vm, "ApplyIgnoredModFilter");

            vm.FilteredIgnoredMods.Should().ContainSingle().Which.Should().Be(@"C:\Mods\KeepThis");
        });
    }

    [Fact]
    public async Task ApplyIgnoredModFilter_NoMatch_EmptiesFiltered()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = Bare();
            SeedIgnored(vm, new[] { @"C:\Mods\One" }, "nomatch");
            vm.FilteredIgnoredMods.Add("stale");

            Reflect.InvokeVoid(vm, "ApplyIgnoredModFilter");

            vm.FilteredIgnoredMods.Should().BeEmpty();
        });
    }

    // =====================================================================
    // UpdateHasMissingMasterMods (private instance)
    //   reads: CachedNonAppearanceMods (collection)
    //   writes: HasMissingMasterMods (reactive private set)
    // =====================================================================

    private static CachedNonAppearanceModEntry MissingMasterEntry(string path) =>
        new(path, "missing masters", new[] { "Foo.esp" });

    [Fact]
    public async Task UpdateHasMissingMasterMods_NullCache_IsFalse()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = Bare();
            // CachedNonAppearanceMods left null -> "?? false" path.
            Reflect.InvokeVoid(vm, "UpdateHasMissingMasterMods");
            vm.HasMissingMasterMods.Should().BeFalse();
        });
    }

    [Fact]
    public async Task UpdateHasMissingMasterMods_NoneMissing_IsFalse()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = Bare();
            Reflect.SetField(vm, "<CachedNonAppearanceMods>k__BackingField",
                new ObservableCollection<CachedNonAppearanceModEntry>
                {
                    Entry(@"C:\Mods\A"),
                    Entry(@"C:\Mods\B"),
                });

            Reflect.InvokeVoid(vm, "UpdateHasMissingMasterMods");

            vm.HasMissingMasterMods.Should().BeFalse();
        });
    }

    [Fact]
    public async Task UpdateHasMissingMasterMods_SomeMissing_IsTrue()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = Bare();
            Reflect.SetField(vm, "<CachedNonAppearanceMods>k__BackingField",
                new ObservableCollection<CachedNonAppearanceModEntry>
                {
                    Entry(@"C:\Mods\A"),
                    MissingMasterEntry(@"C:\Mods\Broken"),
                });

            Reflect.InvokeVoid(vm, "UpdateHasMissingMasterMods");

            vm.HasMissingMasterMods.Should().BeTrue();
        });
    }

    // =====================================================================
    // GetStatusReport (public instance; reads only [Reactive] auto-props)
    // =====================================================================

    [Fact]
    public async Task GetStatusReport_DefaultUninitialised_RendersEveryLabelledLine()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = Bare();

            // All reactive backing fields default (null strings / default enums / false bools);
            // the report must still emit one labelled line per setting without throwing.
            var report = vm.GetStatusReport();

            report.Should().Contain("Mods Folder:");
            report.Should().Contain("Mugshots Folder:");
            report.Should().Contain("Patching Mode:");
            report.Should().Contain("Output Directory:");
            report.Should().Contain("Output Name:");
            report.Should().Contain("Default Override Handling:");
            report.Should().Contain("SkyPatcher Mode:");
            report.Should().Contain("AutoEslIfy:");
        });
    }

    [Fact]
    public async Task GetStatusReport_ReflectsSetReactiveValues()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            var vm = Bare();
            // Set through the [Reactive] setters (ReactiveUI's static-table change machinery
            // makes this safe on an uninitialised instance).
            vm.ModsFolder = @"C:\Mods";
            vm.MugshotsFolder = @"C:\Mugshots";
            vm.OutputDirectory = @"C:\Out";
            vm.TargetPluginName = "MyOutput.esp";
            vm.UseSkyPatcherMode = true;
            vm.AutoEslIfy = false;

            var report = vm.GetStatusReport();

            report.Should().Contain(@"Mods Folder: C:\Mods");
            report.Should().Contain(@"Mugshots Folder: C:\Mugshots");
            report.Should().Contain(@"Output Directory: C:\Out");
            report.Should().Contain("Output Name: MyOutput.esp");
            report.Should().Contain("SkyPatcher Mode: True");
            report.Should().Contain("AutoEslIfy: False");
        });
    }

    // =====================================================================
    // Companion row VMs (pure ctors / static display mapping)
    // =====================================================================

    [Fact]
    public void CachedNonAppearanceModEntry_NoMissingMasters_TooltipIsReason()
    {
        var e = new CachedNonAppearanceModEntry(@"C:\Mods\X", "no FaceGen", null);
        e.Path.Should().Be(@"C:\Mods\X");
        e.Reason.Should().Be("no FaceGen");
        e.IsMissingMasters.Should().BeFalse();
        e.MissingMasterNames.Should().BeEmpty();
        e.TooltipText.Should().Be("no FaceGen");
    }

    [Fact]
    public void CachedNonAppearanceModEntry_EmptyMissingList_TreatedAsNotMissing()
    {
        var e = new CachedNonAppearanceModEntry(@"C:\Mods\X", "reason", Array.Empty<string>());
        e.IsMissingMasters.Should().BeFalse();
        e.TooltipText.Should().Be("reason");
    }

    [Fact]
    public void CachedNonAppearanceModEntry_WithMissingMasters_BuildsBulletedTooltip()
    {
        var e = new CachedNonAppearanceModEntry(@"C:\Mods\X", "reason", new[] { "Foo.esp", "Bar.esp" });
        e.IsMissingMasters.Should().BeTrue();
        e.MissingMasterNames.Should().Equal("Foo.esp", "Bar.esp");
        // Missing-master tooltip ignores the plain reason and lists each master.
        e.TooltipText.Should().NotBe("reason");
        e.TooltipText.Should().Contain("Missing masters at scan time:");
        e.TooltipText.Should().Contain("Foo.esp");
        e.TooltipText.Should().Contain("Bar.esp");
    }

    [Theory]
    [InlineData(MugshotSourceType.DownloadedMugshots, "Downloaded Mugshots")]
    [InlineData(MugshotSourceType.FaceFinder, "FaceFinder")]
    [InlineData(MugshotSourceType.AutoGeneration, "Auto-Generation")]
    [InlineData(MugshotSourceType.None, "None")]
    public void VM_MugshotSourcePriorityItem_DisplayNameMapping(MugshotSourceType source, string expected)
    {
        VM_MugshotSourcePriorityItem.GetDisplayName(source).Should().Be(expected);
    }

    [Fact]
    public void VM_MugshotSourcePriorityItem_Ctor_SeedsSourceDisplayAndDefaults()
    {
        var item = new VM_MugshotSourcePriorityItem(MugshotSourceType.FaceFinder);
        item.Source.Should().Be(MugshotSourceType.FaceFinder);
        item.DisplayName.Should().Be("FaceFinder");
        item.IsEnabled.Should().BeTrue("the default-enabled flag is set in the field initializer");
        item.DisabledReason.Should().BeEmpty();
    }

    [Fact]
    public void VM_LoggedNpc_Ctor_PopulatesFieldsAndStringifiesFormKey()
    {
        var fk = FormKey.Factory("001234:Skyrim.esm");
        var npc = new VM_LoggedNpc(fk, "Hero (Skyrim.esm)", "Hero", "HeroEdid");

        npc.NpcFormKey.Should().Be(fk);
        npc.NpcFormKeyString.Should().Be(fk.ToString());
        npc.DisplayName.Should().Be("Hero (Skyrim.esm)");
        npc.NpcName.Should().Be("Hero");
        npc.NpcEditorId.Should().Be("HeroEdid");
    }

    [Fact]
    public void VM_LoggedNpc_BlankDisplayName_FallsBackToFormKeyString()
    {
        var fk = FormKey.Factory("0ABCDE:Test.esp");
        var npc = new VM_LoggedNpc(fk, "   ", null!, null!);

        // Blank display name -> FormKey string; null name/editorId -> empty (never null).
        npc.DisplayName.Should().Be(fk.ToString());
        npc.NpcName.Should().BeEmpty();
        npc.NpcEditorId.Should().BeEmpty();
    }
}
