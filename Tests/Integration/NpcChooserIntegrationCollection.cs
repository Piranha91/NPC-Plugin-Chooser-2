using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// xUnit collection binding every integration / WPF-VM test to a single
/// <see cref="WpfStaFixture"/>. Membership also guarantees these tests run sequentially,
/// which matters because NPC2 carries process-global static state: the static
/// <c>NpcDiagnosticLogger</c>, <c>PluginArchiveIndex</c>'s per-directory cache, the
/// fixed-path file writers (Settings.json / EventLog.txt at the base/working dir), and
/// the shared ReactiveUI schedulers. Sequential execution keeps each test deterministic.
/// </summary>
[CollectionDefinition(Name)]
public sealed class NpcChooserIntegrationCollection : ICollectionFixture<WpfStaFixture>
{
    public const string Name = "NpcChooserIntegration";
}
