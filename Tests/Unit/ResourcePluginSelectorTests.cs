using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="VM_ResourcePluginSelector"/> — the Set Resource-Only Plugins dialog's state.
///
/// <para>The contract that matters most is what gets PERSISTED. Only rows that disagree with
/// the live default are written back as overrides; storing an agreeing value would freeze the
/// default, so a later change to the owning mod's Merge Dependencies toggle would silently stop
/// propagating. The tests below pin that, plus the rule that non-resource rows are never
/// independently settable (they mirror the mod, per the dialog's disabled checkbox).</para>
/// </summary>
public class ResourcePluginSelectorTests
{
    private static readonly ModKey NpcPlugin = MutagenFixtures.Mk("BanditWar.esp");
    private static readonly ModKey ResourcePlugin = MutagenFixtures.Mk("ProjectJaKhaJay.esp");
    private static readonly FormKey SomeNpc = FormKey.Factory("000801:BanditWar.esp");

    private static ModSetting Owner(bool mergeIn, Dictionary<ModKey, bool>? overrides = null) => new()
    {
        DisplayName = "Lawless",
        MergeInDependencyRecords = mergeIn,
        CorrespondingModKeys = new List<ModKey> { NpcPlugin, ResourcePlugin },
        ResourceOnlyModKeys = new HashSet<ModKey> { ResourcePlugin },
        NpcFormKeys = new HashSet<FormKey> { SomeNpc },
        PluginMergeInOverrides = overrides ?? new Dictionary<ModKey, bool>(),
    };

    private static VM_ResourcePluginSelector Make(ModSetting owner,
        IReadOnlyDictionary<ModKey, ModSetting>? index = null) =>
        new(owner.CorrespondingModKeys, new HashSet<ModKey>(owner.ResourceOnlyModKeys), owner, index);

    private static VM_SelectableMod Row(VM_ResourcePluginSelector vm, ModKey key) =>
        vm.SelectablePlugins.Single(r => r.ModKey.Equals(key));

    [Fact]
    public void NonResourceRow_IsNotEditable_AndMirrorsTheModsToggle()
    {
        using var on = Make(Owner(mergeIn: true));
        using var off = Make(Owner(mergeIn: false));

        Row(on, NpcPlugin).IsMergeEditable.Should().BeFalse();
        Row(on, NpcPlugin).IsMergedIn.Should().BeTrue();
        Row(off, NpcPlugin).IsMergedIn.Should().BeFalse();
    }

    [Fact]
    public void ResourceRow_IsEditable_AndStartsAtTheLiveDefault()
    {
        using var vm = Make(Owner(mergeIn: false));

        var row = Row(vm, ResourcePlugin);
        row.IsMergeEditable.Should().BeTrue();
        row.IsMergedIn.Should().BeTrue("unclaimed resource plugins default to merging");
    }

    [Fact]
    public void ResourceRow_DefaultFollowsTheOwningModEntry()
    {
        var jaKhajay = new ModSetting
        {
            DisplayName = "Project ja-Kha'jay",
            MergeInDependencyRecords = false,
            CorrespondingModKeys = new List<ModKey> { ResourcePlugin },
            NpcFormKeys = new HashSet<FormKey> { FormKey.Factory("0008C4:ProjectJaKhaJay.esp") },
        };
        var index = MergeEligibility.BuildNpcProvidingOwnerIndex(new[] { jaKhajay });

        using var vm = Make(Owner(mergeIn: true), index);

        Row(vm, ResourcePlugin).IsMergedIn.Should().BeFalse();
    }

    [Fact]
    public void UntouchedRows_PersistNoOverrides_SoDefaultsStayLive()
    {
        using var vm = Make(Owner(mergeIn: false));

        vm.GetMergeInOverrides().Should().BeEmpty();
    }

    [Fact]
    public void ChangingAResourceRow_PersistsExactlyThatOverride()
    {
        using var vm = Make(Owner(mergeIn: false));

        Row(vm, ResourcePlugin).IsMergedIn = false; // default was true

        var overrides = vm.GetMergeInOverrides();
        overrides.Should().HaveCount(1);
        overrides[ResourcePlugin].Should().BeFalse();
    }

    [Fact]
    public void NonResourceRows_AreNeverPersisted()
    {
        using var vm = Make(Owner(mergeIn: true));

        // Even if something flipped the bound value, a row that isn't resource-only has no
        // independent state to save — it must keep mirroring the mod.
        Row(vm, NpcPlugin).IsMergedIn = false;

        vm.GetMergeInOverrides().Should().NotContainKey(NpcPlugin);
    }

    [Fact]
    public void MarkingARowResourceOnly_EnablesMergeAndSeedsItsDefault()
    {
        using var vm = Make(Owner(mergeIn: false));
        var row = Row(vm, NpcPlugin);

        row.IsSelected = true;

        row.IsMergeEditable.Should().BeTrue();
        row.IsMergedIn.Should().BeTrue("it now resolves as an unclaimed resource plugin");
        vm.GetMergeInOverrides().Should().BeEmpty("agreeing with the default stores nothing");
    }

    [Fact]
    public void UnmarkingAResourceRow_DropsItsOverrideAndReturnsToMirroring()
    {
        using var vm = Make(Owner(mergeIn: false,
            overrides: new Dictionary<ModKey, bool> { [ResourcePlugin] = false }));
        var row = Row(vm, ResourcePlugin);
        row.IsMergedIn.Should().BeFalse("the saved override is loaded");

        row.IsSelected = false;

        row.IsMergeEditable.Should().BeFalse();
        row.IsMergedIn.Should().BeFalse("mirrors the mod's own toggle, which is off");
        vm.GetMergeInOverrides().Should().BeEmpty();
    }

    [Fact]
    public void Cancel_ReportsNoChange_EvenAfterEdits()
    {
        using var vm = Make(Owner(mergeIn: false));
        Row(vm, ResourcePlugin).IsMergedIn = false;

        vm.CancelCommand.Execute().Subscribe();

        vm.HasChanged.Should().BeFalse();
    }

    [Fact]
    public void Ok_FlagsAChange_WhenOnlyTheMergeToggleMoved()
    {
        using var vm = Make(Owner(mergeIn: false));
        Row(vm, ResourcePlugin).IsMergedIn = false; // resource selection itself is untouched

        vm.OKCommand.Execute().Subscribe();

        vm.HasChanged.Should().BeTrue("a merge-only edit must still trigger the refresh + save");
    }

    [Fact]
    public void Ok_ReportsNoChange_WhenNothingWasEdited()
    {
        using var vm = Make(Owner(mergeIn: false));

        vm.OKCommand.Execute().Subscribe();

        vm.HasChanged.Should().BeFalse();
    }
}
