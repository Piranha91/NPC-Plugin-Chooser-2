using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="EnvironmentStateProvider.IsVanillaOnlyLoadOrder"/> — the advisory that catches a load
/// order resolved from Windows' own Plugins.txt instead of the mod manager's profile (i.e. N.P.C.2
/// launched outside MO2/Vortex, so patching sees the wrong conflict winners and the NPCs menu shows
/// only base-game / CC NPCs). Pure logic.
/// </summary>
public class VanillaOnlyLoadOrderTests
{
    private static ModKey Key(string name) => ModKey.FromFileName(name);

    private static readonly HashSet<ModKey> BaseGame = new()
    {
        Key("Skyrim.esm"), Key("Update.esm"), Key("Dawnguard.esm"),
        Key("HearthFires.esm"), Key("Dragonborn.esm"),
    };

    private static readonly HashSet<ModKey> CreationClub = new()
    {
        Key("ccBGSSSE001-Fish.esm"), Key("ccQDRSSE001-SurvivalMode.esl"),
    };

    private static bool IsVanillaOnly(IEnumerable<ModKey> enabled, ModKey? ownOutput = null) =>
        EnvironmentStateProvider.IsVanillaOnlyLoadOrder(enabled, BaseGame, CreationClub, ownOutput);

    [Fact]
    public void BaseGameOnly_IsVanillaOnly()
    {
        IsVanillaOnly(BaseGame).Should().BeTrue();
    }

    [Fact]
    public void BaseGamePlusCreationClub_IsVanillaOnly()
    {
        IsVanillaOnly(BaseGame.Concat(CreationClub)).Should().BeTrue();
    }

    [Fact]
    public void SingleThirdPartyPlugin_IsNotVanillaOnly()
    {
        IsVanillaOnly(BaseGame.Append(Key("SomeAppearanceMod.esp"))).Should().BeFalse(
            "one mod plugin is enough to prove the mod manager's load order was seen");
    }

    [Fact]
    public void OwnOutputPluginDoesNotCount_AsAThirdPartyPlugin()
    {
        // A stale output plugin left in the load order must not mask the warning.
        IsVanillaOnly(BaseGame.Append(Key("NPC.esp")), ownOutput: Key("NPC.esp"))
            .Should().BeTrue();
    }

    [Fact]
    public void OwnOutputPluginAlongsideRealMods_IsNotVanillaOnly()
    {
        IsVanillaOnly(BaseGame.Append(Key("NPC.esp")).Append(Key("SomeAppearanceMod.esp")),
                ownOutput: Key("NPC.esp"))
            .Should().BeFalse();
    }

    [Fact]
    public void EmptyLoadOrder_IsNotFlagged()
    {
        // An empty load order is already surfaced as Invalid; don't double-report it as a warning.
        IsVanillaOnly(Array.Empty<ModKey>()).Should().BeFalse();
    }

    [Fact]
    public void CreationClubOnly_IsVanillaOnly()
    {
        IsVanillaOnly(CreationClub).Should().BeTrue();
    }
}
