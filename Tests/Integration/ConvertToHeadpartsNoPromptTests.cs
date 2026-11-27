using System.Windows;
using System.Windows.Threading;
using Autofac;
using FluentAssertions;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.Memory;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Views;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Choosing the wig mode "Convert To Headparts" simply takes effect: no confirmation box, on the
/// global default (Settings) or on a single mod (Mods tab). Both are driven through the property
/// their dropdown binds to, on the real view models of the frontend graph.
///
/// <para>Each test ends with a choice that still asks, on the same view model (Forward To Outfit
/// while SkyPatcher directives are written; Include Outfits on a mod). That is the control: it
/// shows the watcher sees a confirmation box when there is one, so the empty list before it means
/// none was shown.</para>
///
/// <para>The watcher closes every <see cref="ScrollableMessageBox"/> without an answer, which the
/// callers read as "No". While it lives the boxes open off screen and without taking focus, so
/// the run does not put a window in front of other work on the machine.</para>
/// No game install needed.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class ConvertToHeadpartsNoPromptTests
{
    private readonly WpfStaFixture _sta;
    public ConvertToHeadpartsNoPromptTests(WpfStaFixture sta) => _sta = sta;

    // Longer than the 1 ms timer a declined box uses to put the previous selection back.
    private const int SettleMs = 200;

    private sealed class PromptWatcher : IDisposable
    {
        // ScrollableMessageBox.xaml takes its style from this application resource.
        private const string WindowStyleKey = "ThemedWindowStyle";

        private readonly DispatcherTimer _timer;
        private readonly bool _hadStyle;
        private readonly object? _previousStyle;

        /// <summary>Titles of the boxes that opened, in order.</summary>
        public List<string> Titles { get; } = new();

        public PromptWatcher()
        {
            var resources = Application.Current.Resources;
            _hadStyle = resources.Contains(WindowStyleKey);
            _previousStyle = _hadStyle ? resources[WindowStyleKey] : null;

            var offScreen = new Style(typeof(Window));
            offScreen.Setters.Add(new Setter(Window.ShowActivatedProperty, false));
            offScreen.Setters.Add(new Setter(Window.ShowInTaskbarProperty, false));
            offScreen.Setters.Add(new Setter(Window.LeftProperty, -20000.0));
            offScreen.Setters.Add(new Setter(Window.TopProperty, -20000.0));
            resources[WindowStyleKey] = offScreen;

            // A timer rather than one queued callback: it also ticks inside the modal loop of a
            // box, and it stays armed for a second box opened by the revert of a declined one.
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Normal,
                (_, _) => Sweep(), Dispatcher.CurrentDispatcher);
            _timer.Start();
        }

        private void Sweep()
        {
            foreach (var box in Application.Current.Windows.OfType<ScrollableMessageBox>()
                         .Where(w => w.IsVisible).ToList())
            {
                Titles.Add(box.Title);
                box.Close();
            }
        }

        public void Dispose()
        {
            _timer.Stop();
            Sweep();
            var resources = Application.Current.Resources;
            if (_hadStyle) resources[WindowStyleKey] = _previousStyle!;
            else resources.Remove(WindowStyleKey);
        }
    }

    [Fact]
    public async Task GlobalDefault_ConvertToHeadparts_TakesEffectWithoutAPrompt()
    {
        await _sta.RunOnStaAsync(async () =>
        {
            using var guard = new StaticStateGuard(immediateSchedulers: false);
            FrontendVmHarness.InstallStaMainThreadScheduler();

            var settings = new Settings
            {
                SkyrimRelease = SkyrimRelease.SkyrimSE,
                DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin,
            };
            using var harness = new FrontendVmHarness(NpcChooserTestEnvironment.Invalid(), settings);
            var vm = harness.Container.Resolve<VM_Settings>();
            vm.SelectedDefaultWigHandlingMode.Should().Be(WigHandlingMode.ForwardToSkin);

            using var prompts = new PromptWatcher();

            vm.SelectedDefaultWigHandlingMode = WigHandlingMode.ConvertToHeadParts;
            await Task.Delay(SettleMs);

            prompts.Titles.Should().BeEmpty("choosing Convert To Headparts asks nothing");
            vm.SelectedDefaultWigHandlingMode.Should().Be(WigHandlingMode.ConvertToHeadParts);
            settings.DefaultWigHandlingMode.Should().Be(WigHandlingMode.ConvertToHeadParts);

            // Control, and the warning that stays. The box reads the output backend from the model.
            settings.OutputBackend = OutputBackend.SkyPatcher;
            vm.SelectedDefaultWigHandlingMode = WigHandlingMode.ForwardToOutfit;
            await Task.Delay(SettleMs);

            prompts.Titles.Should().Equal(new[] { HandlingModeDisplay.SkyPatcherForwardToOutfitWarningTitle },
                "Forward To Outfit with SkyPatcher output still asks, and going back to Convert To Headparts " +
                "after the 'No' asks nothing");
            vm.SelectedDefaultWigHandlingMode.Should().Be(WigHandlingMode.ConvertToHeadParts,
                "the watcher answers 'No', which puts the previous mode back");
            settings.DefaultWigHandlingMode.Should().Be(WigHandlingMode.ConvertToHeadParts);
        });
    }

    [Fact]
    public async Task PerMod_ConvertToHeadparts_TakesEffectWithoutAPrompt()
    {
        await _sta.RunOnStaAsync(async () =>
        {
            using var guard = new StaticStateGuard(immediateSchedulers: false);
            FrontendVmHarness.InstallStaMainThreadScheduler();

            using var harness = new FrontendVmHarness(NpcChooserTestEnvironment.Invalid(),
                new Settings { SkyrimRelease = SkyrimRelease.SkyrimSE });
            var factory = harness.Container.Resolve<VM_ModSetting.FromModelFactory>();
            var mod = factory(new ModSetting { DisplayName = "Wig Probe" }, harness.ModsVm);
            mod.OverrideWigHandlingMode.Should().BeNull("a new mod follows the default");

            using var prompts = new PromptWatcher();

            // What the dropdown on the Mods tab does.
            mod.SelectedWigHandlingItem =
                mod.WigHandlingModes.Single(kv => kv.Key == WigHandlingMode.ConvertToHeadParts);
            await Task.Delay(SettleMs);

            prompts.Titles.Should().BeEmpty("choosing Convert To Headparts for one mod asks nothing");
            mod.OverrideWigHandlingMode.Should().Be(WigHandlingMode.ConvertToHeadParts);
            mod.SelectedWigHandlingItem.Key.Should().Be(WigHandlingMode.ConvertToHeadParts);

            // Control: a per-mod choice that still asks.
            mod.IncludeOutfits = true;
            await Task.Delay(SettleMs);

            prompts.Titles.Should().Equal(new[] { "Confirm Outfit Forwarding" });
            mod.IncludeOutfits.Should().BeFalse("the watcher answers 'No'");
            mod.OverrideWigHandlingMode.Should().Be(WigHandlingMode.ConvertToHeadParts);
        });
    }
}
