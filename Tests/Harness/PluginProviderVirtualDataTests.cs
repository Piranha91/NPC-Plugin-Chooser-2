using System.IO;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// <see cref="PluginProvider.VirtualDataPluginPaths"/>: the seam a headless build of a mod-manager
/// profile uses to find a plugin that lives in ANOTHER mod's folder, as the manager's virtual Data
/// folder would show it. Found on the in-game bench (2026-11-25): without it, a Create-and-Patch
/// build never found the original of a record whose plugin was neither the appearance mod's own nor
/// a base-game file, and copied the override whole instead of patching it.
/// </summary>
public class PluginProviderVirtualDataTests
{
    private static async Task<(ModKey Key, string Path)> WritePlugin(TempDir tmp, string folder, string fileName)
    {
        var mod = MutagenFixtures.NewMod(fileName);
        MutagenFixtures.NewNpc(mod, editorId: "Someone");
        var path = tmp.Combine(folder, fileName);
        await mod.BeginWrite.ToPath(path).WithLoadOrderFromHeaderMasters().WithNoDataFolder().WriteAsync();
        return (mod.ModKey, path);
    }

    [Fact]
    public async Task WithoutTheSeam_APluginInAnotherModsFolder_IsNotFound()
    {
        using var tmp = new TempDir("vdata");
        var (key, _) = await WritePlugin(tmp, "OtherMod", "Elsewhere.esp");
        using var provider = new PluginProvider(NpcChooserTestEnvironment.Invalid(), new Settings());

        provider.TryGetPlugin(key, new HashSet<string> { tmp.Combine("AppearanceMod", "x") }, out var plugin)
            .Should().BeFalse("neither the mod's own folders nor the Data folder hold it");
        plugin.Should().BeNull();
    }

    [Fact]
    public async Task WithTheSeam_ThePluginIsFoundWhereTheVirtualDataFolderHasIt()
    {
        using var tmp = new TempDir("vdata");
        var (key, path) = await WritePlugin(tmp, "OtherMod", "Elsewhere.esp");
        using var provider = new PluginProvider(NpcChooserTestEnvironment.Invalid(), new Settings())
        {
            VirtualDataPluginPaths = new Dictionary<ModKey, string> { [key] = path },
        };

        provider.TryGetPlugin(key, new HashSet<string>(), out var plugin, out var source).Should().BeTrue();
        plugin.Should().NotBeNull();
        plugin!.Npcs.Should().ContainSingle();
        source.Should().Be(path);
    }

    [Fact]
    public async Task TheModsOwnFolder_StillWinsOverTheVirtualDataFolder()
    {
        using var tmp = new TempDir("vdata");
        var (key, elsewhere) = await WritePlugin(tmp, "OtherMod", "Shared.esp");
        var (_, own) = await WritePlugin(tmp, "AppearanceMod", "Shared.esp");
        using var provider = new PluginProvider(NpcChooserTestEnvironment.Invalid(), new Settings())
        {
            VirtualDataPluginPaths = new Dictionary<ModKey, string> { [key] = elsewhere },
        };

        provider.TryGetPlugin(key, new HashSet<string> { Path.GetDirectoryName(own)! }, out _, out var source)
            .Should().BeTrue();
        source.Should().Be(own, "the appearance mod's own copy is searched first, as before");
    }
}
