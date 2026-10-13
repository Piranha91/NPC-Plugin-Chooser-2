using System.IO;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Skyrim.ccc discovery for installs Mutagen's registry/Steam locator can't find (moved drive,
/// renamed folder). Mutagen 0.54 builds the .ccc path from that locator even when given a target
/// Data folder, so without the injection the free CC plugins (ccBGSSSE001-Fish.esm, ...) drop out
/// of the load order and mods mastering them are skipped as having a missing master.
/// </summary>
public class CreationClubListingsTests : IDisposable
{
    private readonly string _gameDir;
    private readonly string _dataDir;

    public CreationClubListingsTests()
    {
        _gameDir = Path.Combine(Path.GetTempPath(), "NPC2_CccTests_" + Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_gameDir, "Data");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_gameDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void FindNextTo_ReturnsCccInDataFolderParent()
    {
        var ccc = Path.Combine(_gameDir, "Skyrim.ccc");
        File.WriteAllText(ccc, "ccBGSSSE001-Fish.esm\r\n");

        EnvironmentStateProvider.FindCreationClubListingsNextTo(_dataDir).Should().Be(ccc);
        EnvironmentStateProvider.FindCreationClubListingsNextTo(_dataDir + "\\").Should().Be(ccc);
    }

    [Fact]
    public void FindNextTo_ReturnsNullWhenAbsentOrUnset()
    {
        EnvironmentStateProvider.FindCreationClubListingsNextTo(_dataDir).Should().BeNull();
        EnvironmentStateProvider.FindCreationClubListingsNextTo(null).Should().BeNull();
        EnvironmentStateProvider.FindCreationClubListingsNextTo("").Should().BeNull();
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void GetCreationClubPlugins_ParsesEitherLineEnding(string newline)
    {
        var ccc = Path.Combine(_gameDir, "Skyrim.ccc");
        File.WriteAllText(ccc, string.Join(newline,
            "ccBGSSSE001-Fish.esm", "ccQDRSSE001-SurvivalMode.esl", "ccBGSSSE025-AdvDSGS.esm", ""));

        var provider = new EnvironmentStateProvider(null) { CreationClubListingsFilePath = ccc };

        provider.GetCreationClubPlugins().Should().BeEquivalentTo(new[]
        {
            ModKey.FromFileName("ccBGSSSE001-Fish.esm"),
            ModKey.FromFileName("ccQDRSSE001-SurvivalMode.esl"),
            ModKey.FromFileName("ccBGSSSE025-AdvDSGS.esm"),
        });
    }

    /// <summary>
    /// End-to-end against Mutagen's real builder. The probe plugin's name is one no real
    /// Skyrim.ccc lists, so on a machine whose registry DOES locate a Skyrim install the control
    /// build still can't pick it up; only the injected .ccc can put it in the load order.
    /// </summary>
    [Fact]
    public void InjectedCcc_PutsCcPluginIntoLoadOrder()
    {
        var probe = ModKey.FromFileName("ccNPC2Test001-Probe.esm");
        WriteEmptyPlugin("Skyrim.esm");
        WriteEmptyPlugin(probe.FileName);
        var ccc = Path.Combine(_gameDir, "Skyrim.ccc");
        File.WriteAllText(ccc, probe.FileName + "\n");

        GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter> NewBuilder() =>
            GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE)
                .WithTargetDataFolder(_dataDir);

        using (var control = NewBuilder().Build())
        {
            control.LoadOrder.ContainsKey(probe).Should().BeFalse(
                "Mutagen's own .ccc lookup ignores the target Data folder");
        }

        using var env = EnvironmentStateProvider
            .WithCreationClubListingsPath(NewBuilder(), EnvironmentStateProvider.FindCreationClubListingsNextTo(_dataDir))
            .Build();

        env.LoadOrder.ContainsKey(probe).Should().BeTrue();
        ((string?)env.CreationClubListingsFilePath).Should().Be(ccc);
    }

    private void WriteEmptyPlugin(string fileName)
    {
        var mod = new SkyrimMod(ModKey.FromFileName(fileName), SkyrimRelease.SkyrimSE);
        mod.BeginWrite.ToPath(Path.Combine(_dataDir, fileName))
            .WithLoadOrderFromHeaderMasters().WithNoDataFolder().Write();
    }
}
