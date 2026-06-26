using System.Reactive.Linq;
using FluentAssertions;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Drives the Randomize dialog VM by invoking its command bodies and helpers directly — the
/// "simulate a button click without a UI" pattern. Runs on the STA fixture because the VM
/// subscribes to ReactiveUI schedulers; an immediate-scheduler guard makes command execution
/// synchronous. No game install required.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class VM_RandomizeOptionsTests
{
    private readonly WpfStaFixture _sta;
    public VM_RandomizeOptionsTests(WpfStaFixture sta) => _sta = sta;

    private static VM_RandomizeOptions Make(IEnumerable<string> mods, Action? clear = null) =>
        new(mods, clear ?? (() => { }));

    [Fact]
    public async Task Ctor_DedupsSortsAndDropsBlankModNames()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            using var vm = Make(new[] { "Zeta", "alpha", "ALPHA", "  ", "", "Beta", null! });

            // FilteredMods mirrors _allMods initially: distinct (case-insensitive), blanks dropped, sorted.
            vm.FilteredMods.Select(m => m.DisplayName).Should().Equal("alpha", "Beta", "Zeta");
        });
    }

    [Fact]
    public async Task Ctor_HasSpecDefaults()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            using var vm = Make(new[] { "A" });

            vm.Scope.Should().Be(RandomizeScope.VisibleNpcs);
            vm.AllowBaseMod.Should().BeFalse();
            vm.ForceSharedAppearance.Should().BeTrue();
            vm.AllowSharedAppearance.Should().BeFalse();
            vm.AppearanceSource.Should().Be(RandomizeAppearanceSource.ModsAndFavorites);
            vm.AvailableAppearanceSources.Should().HaveCount(3);
            vm.Confirmed.Should().BeFalse();
        });
    }

    [Fact]
    public async Task GetSelectedModNames_ReturnsCheckedModsCaseInsensitively()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            using var vm = Make(new[] { "Apachii", "KS Hair", "SG Brows" });

            vm.FilteredMods.Single(m => m.DisplayName == "Apachii").IsSelected = true;
            vm.FilteredMods.Single(m => m.DisplayName == "SG Brows").IsSelected = true;

            var names = vm.GetSelectedModNames();
            names.Should().BeEquivalentTo(new[] { "Apachii", "SG Brows" });
            names.Contains("apachii").Should().BeTrue("the set is OrdinalIgnoreCase");
        });
    }

    [Fact]
    public async Task SelectAllCommand_ThenSelectNoneCommand_ToggleEveryMod()
    {
        await _sta.RunOnStaAsync(async () =>
        {
            using var _ = new StaticStateGuard();
            using var vm = Make(new[] { "A", "B", "C" });

            await vm.SelectAllCommand.Execute();
            vm.GetSelectedModNames().Should().BeEquivalentTo(new[] { "A", "B", "C" });

            await vm.SelectNoneCommand.Execute();
            vm.GetSelectedModNames().Should().BeEmpty();
        });
    }

    [Fact]
    public async Task SelectVisibleCommand_OnlySelectsFilteredMods()
    {
        await _sta.RunOnStaAsync(async () =>
        {
            using var _ = new StaticStateGuard();
            using var vm = Make(new[] { "Apachii", "Apachii Female", "KS Hair" });

            // Narrow the visible set, then select-visible should only check the filtered rows.
            vm.ModFilterText = "apachii";
            Reflect.InvokeVoid(vm, "ApplyModFilter");
            vm.FilteredMods.Select(m => m.DisplayName).Should().BeEquivalentTo(new[] { "Apachii", "Apachii Female" });

            await vm.SelectVisibleCommand.Execute();
            vm.GetSelectedModNames().Should().BeEquivalentTo(new[] { "Apachii", "Apachii Female" });
        });
    }

    [Fact]
    public async Task ClearRandomizedNpcsCommand_InvokesSuppliedAction()
    {
        await _sta.RunOnStaAsync(async () =>
        {
            using var _ = new StaticStateGuard();
            int calls = 0;
            using var vm = Make(new[] { "A" }, clear: () => calls++);

            await vm.ClearRandomizedNpcsCommand.Execute();
            calls.Should().Be(1);
        });
    }

    [Fact]
    public async Task ApplyModFilter_PreservesSelectionState_WhenFilterClears()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var _ = new StaticStateGuard();
            using var vm = Make(new[] { "Apachii", "KS Hair" });

            vm.FilteredMods.Single(m => m.DisplayName == "Apachii").IsSelected = true;

            vm.ModFilterText = "KS";
            Reflect.InvokeVoid(vm, "ApplyModFilter");
            vm.FilteredMods.Select(m => m.DisplayName).Should().ContainSingle().Which.Should().Be("KS Hair");

            // Clearing the filter brings Apachii back with its check intact (same VM instances).
            vm.ModFilterText = "";
            Reflect.InvokeVoid(vm, "ApplyModFilter");
            vm.FilteredMods.Single(m => m.DisplayName == "Apachii").IsSelected.Should().BeTrue();
        });
    }

    [Fact]
    public void AppearanceSourceOption_ToStringIsDisplay_AndValueEquality()
    {
        var a = new AppearanceSourceOption("Mods and Favorites", RandomizeAppearanceSource.ModsAndFavorites);
        var b = new AppearanceSourceOption("Mods and Favorites", RandomizeAppearanceSource.ModsAndFavorites);
        a.ToString().Should().Be("Mods and Favorites");
        a.Should().Be(b);
    }

    [Fact]
    public void VM_SelectableModSetting_Ctor_StoresNameAndSelection()
    {
        var m = new VM_SelectableModSetting("Apachii", isSelected: true);
        m.DisplayName.Should().Be("Apachii");
        m.IsSelected.Should().BeTrue();
        new VM_SelectableModSetting("X").IsSelected.Should().BeFalse();
    }
}
