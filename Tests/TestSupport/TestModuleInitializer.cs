using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;

namespace NPC_Plugin_Chooser_2.Tests.TestSupport;

/// <summary>
/// Runs once when the test assembly loads, before any test. Reproduces the global
/// type-descriptor registration that <c>App.InitializeCoreApplicationAsync</c> performs at
/// startup: associating a <see cref="FormKeyTypeConverter"/> with <see cref="FormKey"/> so
/// Newtonsoft can (de)serialize <c>Dictionary&lt;FormKey, …&gt;</c> KEYS. Without it, any
/// JSON round-trip of a FormKey-keyed dictionary fails with
/// "Could not convert string '…' to dictionary key type 'FormKey'" — exactly as the app
/// itself would before that registration runs.
/// </summary>
internal static class TestModuleInitializer
{
    [ModuleInitializer]
    internal static void Init()
    {
        TypeDescriptor.AddAttributes(typeof(FormKey),
            new TypeConverterAttribute(typeof(FormKeyTypeConverter)));
    }
}
