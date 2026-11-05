using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="OutfitDisplayResolver"/> only applies a SkyPatcher / SPID outfit config when
/// that distributor's DLL is in the data folder — without it SKSE never loads the tool and
/// the config is inert, so rendering its outfit would show something the game never puts
/// on. The inert config is instead reported through
/// <see cref="OutfitDisplayResolver.FindDormantDistributions"/> (the tile's missing-patcher
/// badge), but only for the mod that ships it.
///
/// Runs against an in-memory load order and a temp data folder; the DLLs are empty
/// placeholder files, since presence is all the gate checks.
/// </summary>
public class OutfitDistributorInstallGateTests : IDisposable
{
    private const string PluginName = "Gate.esp";
    private const string SkyIniName = "GateOutfits.ini";
    private const string SpidIniName = "GateOutfits_DISTR.ini";

    private readonly TempDir _temp = new("outfitgate");
    private readonly string _dataFolder;
    private readonly string _modFolder;
    private readonly FormKey _npc;
    private readonly FormKey _pluginOutfit;
    private readonly FormKey _distributedOutfit;
    private readonly OutfitDisplayResolver _resolver;

    public OutfitDistributorInstallGateTests()
    {
        _dataFolder = Directory.CreateDirectory(Path.Combine(_temp.Path, "Data")).FullName;
        _modFolder = Directory.CreateDirectory(Path.Combine(_temp.Path, "Mods", "Outfit Mod")).FullName;

        var mod = MutagenFixtures.NewMod(PluginName);
        var pluginOutfit = mod.Outfits.AddNew();
        pluginOutfit.EditorID = "GatePluginOutfit";
        var distributedOutfit = mod.Outfits.AddNew();
        distributedOutfit.EditorID = "GateDistributedOutfit";
        var npc = MutagenFixtures.NewNpc(mod, "GateNpc");
        npc.DefaultOutfit.SetTo(pluginOutfit);
        _npc = npc.FormKey;
        _pluginOutfit = pluginOutfit.FormKey;
        _distributedOutfit = distributedOutfit.FormKey;

        var lo = new LoadOrder<IModListingGetter<ISkyrimModGetter>>();
        lo.Add(new ModListing<ISkyrimModGetter>(mod, enabled: true));
        var env = new EnvironmentStateProvider(null);
        env.UseInMemoryLoadOrderForTest(lo);
        Reflect.SetField(env, "DataFolderPath", new Noggog.DirectoryPath(_dataFolder));

        var settings = new Settings { PatchingMode = PatchingMode.CreateAndPatch };
        // RecordHandler is only consulted for mod-scoped donor lookups; these tests resolve
        // with a null ModSetting (or call FindDormantDistributions, which never reaches it).
        _resolver = new OutfitDisplayResolver(settings, env, null!);
    }

    public void Dispose() => _temp.Dispose();

    private string SkyIniRelative => Path.Combine("SKSE", "Plugins", "SkyPatcher", "npc", SkyIniName);

    private string SkyLine =>
        $"filterByNpcs={PluginName}|{_npc.ID:X}:outfitDefault={PluginName}|{_distributedOutfit.ID:X}";

    private string SpidLine =>
        $"Outfit = 0x{_distributedOutfit.ID:X}~{PluginName}|NONE|0x{_npc.ID:X}~{PluginName}";

    /// <summary>Writes the config into the data folder (what the game/VFS sees) and,
    /// optionally, into the mod's own folder (what makes the mod its owner).</summary>
    private void Ship(string relativePath, string line, bool inModFolder = true)
    {
        Write(Path.Combine(_dataFolder, relativePath), line);
        if (inModFolder) Write(Path.Combine(_modFolder, relativePath), line);
    }

    private void InstallDll(string relativePath) => Write(Path.Combine(_dataFolder, relativePath), string.Empty);

    private static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private ModSetting OwningMod() => new()
    {
        DisplayName = "Outfit Mod",
        CorrespondingFolderPaths = { _modFolder },
    };

    private OutfitDisplayResult Display() => _resolver.ResolveForDisplay(_npc, _npc, null, true);

    [Fact]
    public void SkyPatcher_DllPresent_AppliesTheDistributedOutfit()
    {
        Ship(SkyIniRelative, SkyLine);
        InstallDll(OutfitDisplayResolver.SkyPatcherDllRelativePath);

        var display = Display();

        display.Source.Should().Be(OutfitDisplaySource.SkyPatcher);
        display.OutfitFormKey.Should().Be(_distributedOutfit);
        _resolver.FindDormantDistributions(_npc, OwningMod()).Should().BeEmpty();
    }

    [Fact]
    public void SkyPatcher_DllAbsent_SkipsTheOutfitAndReportsTheMissingPatcher()
    {
        Ship(SkyIniRelative, SkyLine);

        var display = Display();

        display.Source.Should().Be(OutfitDisplaySource.WinningOverride);
        display.OutfitFormKey.Should().Be(_pluginOutfit);
        display.Contest.SkyPatcher.Should().BeFalse("an uninstalled distributor contests nothing");

        var dormant = _resolver.FindDormantDistributions(_npc, OwningMod());
        dormant.Should().ContainSingle();
        dormant[0].ConfigFile.Should().Be(SkyIniRelative);
        dormant[0].MissingDll.Should().Be(OutfitDisplayResolver.SkyPatcherDllRelativePath);
        dormant[0].Describe().Should().Contain(SkyIniName).And.Contain("SkyPatcher.dll");
    }

    [Fact]
    public void Spid_DllPresent_AppliesTheDistributedOutfit()
    {
        Ship(SpidIniName, SpidLine);
        InstallDll(OutfitDisplayResolver.SpidDllRelativePath);

        var display = Display();

        display.Source.Should().Be(OutfitDisplaySource.Spid);
        display.OutfitFormKey.Should().Be(_distributedOutfit);
        _resolver.FindDormantDistributions(_npc, OwningMod()).Should().BeEmpty();
    }

    [Fact]
    public void Spid_DllAbsent_SkipsTheOutfitAndReportsTheMissingPatcher()
    {
        Ship(SpidIniName, SpidLine);

        var display = Display();

        display.Source.Should().Be(OutfitDisplaySource.WinningOverride);
        display.OutfitFormKey.Should().Be(_pluginOutfit);
        display.Contest.Spid.Should().BeFalse();

        var dormant = _resolver.FindDormantDistributions(_npc, OwningMod());
        dormant.Should().ContainSingle();
        dormant[0].ConfigFile.Should().Be(SpidIniName);
        dormant[0].MissingDll.Should().Be(OutfitDisplayResolver.SpidDllRelativePath);
        dormant[0].Describe().Should().Contain(SpidIniName).And.Contain("po3_SpellPerkItemDistributor.dll");
    }

    [Fact]
    public void DllAbsent_ConfigFromAnotherMod_IsNotReportedOnThisModsTile()
    {
        // The config reaches the data folder through some other mod; the tile's mod
        // doesn't ship it, so its tile carries no missing-patcher badge.
        Ship(SpidIniName, SpidLine, inModFolder: false);

        Display().OutfitFormKey.Should().Be(_pluginOutfit);
        _resolver.FindDormantDistributions(_npc, OwningMod()).Should().BeEmpty();
        _resolver.FindDormantDistributions(_npc, null).Should().ContainSingle(
            "without a mod scope every dormant config targeting the NPC is reported");
    }

    [Fact]
    public void InstallingTheDll_IsPickedUpWithoutAnInvalidate()
    {
        // DLL presence is part of the config-cache signature, so installing the patcher
        // re-activates its configs on the next recheck rather than staying dormant.
        Ship(SkyIniRelative, SkyLine);
        Display().OutfitFormKey.Should().Be(_pluginOutfit);

        InstallDll(OutfitDisplayResolver.SkyPatcherDllRelativePath);
        // Skip the 2s recheck throttle (0 = "checked at boot", always long past).
        Reflect.SetField(_resolver, "_lastSignatureCheckTick", 0L);

        Display().OutfitFormKey.Should().Be(_distributedOutfit);
    }
}
