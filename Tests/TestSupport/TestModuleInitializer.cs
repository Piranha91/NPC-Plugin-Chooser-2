using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using ReactiveUI.Builder;

namespace NPC_Plugin_Chooser_2.Tests.TestSupport;

/// <summary>
/// Runs once when the test assembly loads, before any test. Reproduces the global registrations
/// that <c>App.InitializeCoreApplicationAsync</c> performs at startup:
/// <list type="bullet">
/// <item>Associates a <see cref="FormKeyTypeConverter"/> with <see cref="FormKey"/> so Newtonsoft
/// can (de)serialize <c>Dictionary&lt;FormKey, …&gt;</c> KEYS. Without it, any JSON round-trip of a
/// FormKey-keyed dictionary fails with "Could not convert string '…' to dictionary key type
/// 'FormKey'" — exactly as the app itself would before that registration runs.</item>
/// <item>Initializes ReactiveUI via the RxAppBuilder. ReactiveUI 20+ no longer self-initializes on
/// first use; any <c>WhenAnyValue</c>/<c>WhenAny</c> throws "ReactiveUI has not been initialized"
/// until the builder has run. Tests construct ReactiveObjects directly (outside the app bootstrap),
/// so we set up the same WPF services here once for the whole assembly.</item>
/// </list>
/// </summary>
internal static class TestModuleInitializer
{
    [ModuleInitializer]
    internal static void Init()
    {
        TypeDescriptor.AddAttributes(typeof(FormKey),
            new TypeConverterAttribute(typeof(FormKeyTypeConverter)));

        RxAppBuilder.CreateReactiveUIBuilder()
            .WithWpf()
            .BuildApp();
    }
}
