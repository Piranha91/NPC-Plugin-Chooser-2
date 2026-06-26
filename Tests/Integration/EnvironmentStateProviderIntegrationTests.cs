using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Exercises the real <see cref="EnvironmentStateProvider"/> against the machine's Skyrim SE
/// install. Skips gracefully (passes as a no-op, logging the reason) when no environment can be
/// resolved, mirroring SynthEBD.Tests' integration contract.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class EnvironmentStateProviderIntegrationTests
{
    private readonly ITestOutputHelper _output;
    public EnvironmentStateProviderIntegrationTests(ITestOutputHelper output) => _output = output;

    private static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm");

    [Fact]
    public void UpdateEnvironment_ResolvesValidEnvironment_WithVanillaMasters()
    {
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip))
        {
            _output.WriteLine("SKIPPED: " + skip);
            return;
        }
        using (env)
        {
            var p = env!.Provider;
            p.Status.Should().Be(EnvironmentStateProvider.EnvironmentStatus.Valid);
            p.LoadOrder.Should().NotBeNull();
            p.LinkCache.Should().NotBeNull();
            p.LoadOrder!.ListedOrder.Should().NotBeEmpty();
            p.LoadOrder.ContainsKey(Skyrim).Should().BeTrue("Skyrim.esm anchors a valid load order");
            System.IO.Directory.Exists(p.DataFolderPath).Should().BeTrue();
            p.NumPlugins.Should().BeGreaterThan(0);
            p.NumActivePlugins.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(p.NumPlugins);

            _output.WriteLine($"Resolved {p.NumPlugins} plugins ({p.NumActivePlugins} active), data folder {p.DataFolderPath}");
        }
    }

    [Fact]
    public void BaseGamePlugins_ContainsVanillaMasters()
    {
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip))
        {
            _output.WriteLine("SKIPPED: " + skip);
            return;
        }
        using (env)
        {
            var baseGame = env!.Provider.BaseGamePlugins;
            baseGame.Should().Contain(Skyrim);
            baseGame.Should().Contain(ModKey.FromFileName("Update.esm"));
            baseGame.Should().Contain(ModKey.FromFileName("Dawnguard.esm"));
            baseGame.Should().Contain(ModKey.FromFileName("HearthFires.esm"));
            baseGame.Should().Contain(ModKey.FromFileName("Dragonborn.esm"));
        }
    }

    [Fact]
    public void FormIdPrefix_SkyrimEsm_IsZeroZero()
    {
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip))
        {
            _output.WriteLine("SKIPPED: " + skip);
            return;
        }
        using (env)
        {
            // Skyrim.esm is the first full master in any valid Skyrim load order -> prefix "00".
            env!.Provider.TryGetPluginIndex(Skyrim, out var prefix).Should().BeTrue();
            prefix.Should().Be("00");
        }
    }

    [Fact]
    public void TryGetPluginIndex_UnknownPlugin_ReturnsFalse()
    {
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip))
        {
            _output.WriteLine("SKIPPED: " + skip);
            return;
        }
        using (env)
        {
            env!.Provider.TryGetPluginIndex(ModKey.FromFileName("ZZZ_DoesNotExist_123.esp"), out _)
                .Should().BeFalse();
        }
    }

    [Fact]
    public void InvalidProvider_ReportsInvalidStatus_AndEmptyLoadOrder()
    {
        // No UpdateEnvironment call -> needs no game install.
        var p = NpcChooserTestEnvironment.Invalid();
        p.Status.Should().Be(EnvironmentStateProvider.EnvironmentStatus.Invalid);
        p.LinkCache.Should().BeNull();
        p.LoadOrder!.ListedOrder.Should().BeEmpty();
    }
}
