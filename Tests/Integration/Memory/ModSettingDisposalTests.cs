using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reflection;
using Autofac;
using FluentAssertions;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Memory;

/// <summary>
/// The <see cref="VM_ModSetting"/> half of the leak class fixed in commit 2312cb6 — the twin of
/// <see cref="NpcBrowseMemoryTests"/>, which covers the <c>VM_NpcsMenuMugshot</c> half. A VM_ModSetting
/// subscribes to the SingleInstance <c>VM_Settings</c> (among other singletons), so dropping one from
/// <c>VM_Mods</c>'s list without disposing it leaves it rooted for the life of the app.
///
/// <para>2312cb6 disposed at the population / prune / consolidation-loser / redundant / blank-slate
/// sites but not in <c>RemoveModSetting</c>, and nothing tested any of them — which is how that site
/// stayed open while a later commit (d46a680) routed refresh-rejected entries through it. This test
/// covers the contract so the next removal path added can't quietly skip disposal.</para>
///
/// <para>Runs without a Skyrim install: it builds the frontend graph over an Invalid environment and
/// injects a synthetic entry straight into the list, so no population is needed.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class ModSettingDisposalTests
{
    // The VM's private subscription composite. Its IsDisposed flag is the observable proof that the
    // singleton subscriptions were severed. Reflection into a private is the project's sanctioned seam.
    private static readonly FieldInfo DisposablesField = typeof(VM_ModSetting)
        .GetField("_disposables", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static bool IsDisposed(VM_ModSetting vm) =>
        ((CompositeDisposable)DisposablesField.GetValue(vm)!).IsDisposed;

    private readonly WpfStaFixture _sta;
    private readonly ITestOutputHelper _out;

    public ModSettingDisposalTests(WpfStaFixture sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    /// <summary>
    /// Builds the frontend graph over an Invalid (no-game) environment and injects a synthetic
    /// VM_ModSetting into <c>VM_Mods</c>'s internal list — the state a removal acts on, without paying
    /// for a full mod population.
    /// </summary>
    private static VM_ModSetting AddProbeEntry(FrontendVmHarness harness, string displayName)
    {
        var modsVm = harness.ModsVm;
        var factory = harness.Container.Resolve<VM_ModSetting.FromModelFactory>();
        var vm = factory(new ModSetting { DisplayName = displayName }, modsVm);

        Reflect.GetField<List<VM_ModSetting>>(modsVm, "_allModSettingsInternal").Add(vm);
        return vm;
    }

    /// <summary>Pumps the STA dispatcher until <paramref name="condition"/> holds or the timeout elapses.</summary>
    private static async Task PumpUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !condition())
        {
            await Task.Delay(15);
        }
    }

    private static Settings BuildSettings() => new() { SkyrimRelease = SkyrimRelease.SkyrimSE };

    [Fact]
    public async Task RemoveModSetting_DisposesTheRemovedVm()
    {
        await _sta.RunOnStaAsync(async () =>
        {
            using var _ = new StaticStateGuard(immediateSchedulers: false);
            FrontendVmHarness.InstallStaMainThreadScheduler();
            using var harness = new FrontendVmHarness(NpcChooserTestEnvironment.Invalid(), BuildSettings());

            var modsVm = harness.ModsVm;
            var vm = AddProbeEntry(harness, "Disposal Probe");

            IsDisposed(vm).Should().BeFalse("an entry still in the list must not be disposed");

            modsVm.RemoveModSetting(vm).Should().BeTrue("the probe entry was in the internal list");
            modsVm.AllModSettings.Should().NotContain(vm);

            // Disposal is deferred to the next main-thread scheduler tick (RemoveModSetting is called
            // from inside the VM's own ReactiveCommands, so it cannot dispose inline), hence the pump.
            await PumpUntilAsync(() => IsDisposed(vm));

            _out.WriteLine($"Disposed after removal: {IsDisposed(vm)}");
            IsDisposed(vm).Should().BeTrue(
                "removing a VM_ModSetting must dispose it — its subscription to the SingleInstance " +
                "VM_Settings is what roots it for the app's lifetime, so an undisposed entry is the leak " +
                "class fixed in 2312cb6 reappearing through a removal path");
        });
    }

    /// <summary>
    /// Locks the membership re-check on the deferred dispose: an entry removed and then put back before
    /// the scheduler tick fires must survive. Without the guard the queued callback would dispose a VM
    /// that is live in the list again, silently deadening its commands and subscriptions.
    /// </summary>
    [Fact]
    public async Task RemoveThenReAddBeforeTheTick_LeavesTheVmAlive()
    {
        await _sta.RunOnStaAsync(async () =>
        {
            using var _ = new StaticStateGuard(immediateSchedulers: false);
            FrontendVmHarness.InstallStaMainThreadScheduler();
            using var harness = new FrontendVmHarness(NpcChooserTestEnvironment.Invalid(), BuildSettings());

            var modsVm = harness.ModsVm;
            var vm = AddProbeEntry(harness, "Re-add Probe");

            modsVm.RemoveModSetting(vm).Should().BeTrue();

            // Put it back synchronously, before the queued disposal callback runs.
            Reflect.GetField<List<VM_ModSetting>>(modsVm, "_allModSettingsInternal").Add(vm);

            // Give the scheduler ample opportunity to fire the callback, then confirm it declined.
            await PumpUntilAsync(() => false, 500);

            IsDisposed(vm).Should().BeFalse(
                "the deferred dispose re-checks list membership, so an entry that was re-added before " +
                "the tick must not be torn down under the live list");
        });
    }
}
