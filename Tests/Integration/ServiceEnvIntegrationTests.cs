using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Backend services (<see cref="PluginProvider"/>, <see cref="Auxilliary"/>) driven against the
/// real resolved Skyrim SE environment. Skips gracefully when no game is available.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class ServiceEnvIntegrationTests
{
    private readonly ITestOutputHelper _output;
    public ServiceEnvIntegrationTests(ITestOutputHelper output) => _output = output;

    private static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm");
    private static readonly ModKey Dawnguard = ModKey.FromFileName("Dawnguard.esm");

    [Fact]
    public void PluginProvider_TryGetPlugin_ResolvesVanillaPluginFromDataFolder()
    {
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip)) { _output.WriteLine("SKIPPED: " + skip); return; }
        using (env)
        {
            var provider = new PluginProvider(env!.Provider, new Settings());
            try
            {
                // Null fallback folders -> provider falls back to the game Data folder.
                provider.TryGetPlugin(Skyrim, null, out var plugin, out var path, asReadOnly: true)
                    .Should().BeTrue("Skyrim.esm lives in the Data folder");
                plugin.Should().NotBeNull();
                plugin!.ModKey.Should().Be(Skyrim);
                path.Should().NotBeNullOrEmpty();
                _output.WriteLine("Resolved Skyrim.esm at " + path);
            }
            finally
            {
                provider.Dispose();
            }
        }
    }

    [Fact]
    public void PluginProvider_GetMasterPlugins_ReturnsDeclaredMasters()
    {
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip)) { _output.WriteLine("SKIPPED: " + skip); return; }
        using (env)
        {
            var provider = new PluginProvider(env!.Provider, new Settings());
            try
            {
                // Dawnguard.esm declares Skyrim.esm (and Update.esm) as masters.
                var masters = provider.GetMasterPlugins(Dawnguard, null);
                if (masters.Count == 0)
                {
                    _output.WriteLine("SKIPPED: Dawnguard.esm not present in this install.");
                    return;
                }
                masters.Should().Contain(Skyrim);
            }
            finally
            {
                provider.Dispose();
            }
        }
    }

    [Fact]
    public void PluginProvider_TryGetPlugin_UnresolvablePlugin_ReturnsFalse()
    {
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip)) { _output.WriteLine("SKIPPED: " + skip); return; }
        using (env)
        {
            var provider = new PluginProvider(env!.Provider, new Settings());
            try
            {
                provider.TryGetPlugin(ModKey.FromFileName("ZZZ_NoSuchPlugin_987.esp"), null, out var plugin, out _)
                    .Should().BeFalse();
                plugin.Should().BeNull();
            }
            finally
            {
                provider.Dispose();
            }
        }
    }

    [Fact]
    public void Auxilliary_TryFormKeyToFormIDString_VanillaPlugin_PrefixesWithZeroZero()
    {
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip)) { _output.WriteLine("SKIPPED: " + skip); return; }
        using (env)
        {
            var aux = new Auxilliary(env!.Provider);
            // Prefix is looked up by ModKey from the load order; the record need not exist.
            aux.TryFormKeyToFormIDString(FormKey.Factory("012345:Skyrim.esm"), out var formId).Should().BeTrue();
            formId.Should().Be("00012345");
        }
    }

    [Fact]
    public void Auxilliary_TryFormKeyToFormIDString_UnknownPlugin_ReturnsFalse()
    {
        if (!NpcChooserTestEnvironment.TryBuild(out var env, out var skip)) { _output.WriteLine("SKIPPED: " + skip); return; }
        using (env)
        {
            var aux = new Auxilliary(env!.Provider);
            aux.TryFormKeyToFormIDString(FormKey.Factory("000800:ZZZ_NoSuch.esp"), out var formId).Should().BeFalse();
            formId.Should().BeEmpty();
        }
    }
}
