using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <c>Validator.FindInjectedRecordSource</c> — the seam that tells an INJECTED record apart from a
/// version-drifted one. Both look identical to the written-link check: a link whose plugin is in
/// the load order and does not contain the record.
///
/// <para><b>The bug it fixes.</b> An injected record is one the appearance plugin defines ITSELF
/// inside a master's FormID space — the standard "don't add a new master" replacer technique.
/// Screening called every one of them version drift and dropped the selection, silently, because
/// the appearance plugin is disabled in the mod manager (the normal NPC2 workflow) so the load
/// order cannot resolve it. Measured on a real 8338-selection run: 30 selections dropped across
/// three mods — 26/127 of Beyond Skyrim Bruma NPC replacer's records (<c>ARA_*</c> head parts in
/// BSHeartland.esm's space), 16/26 of Citizens of Tamriel Visual Overhaul's, and <b>103/103</b> of
/// Interesting NPCs Visual Overhaul's, which made that mod entirely unusable. All three already
/// had Injected Record Handling auto-enabled, so the patcher would have merged the records in.
///
/// <para>Real plugin files on disk, resolved through a real PluginProvider — the whole point is
/// that the record is found in the MOD's plugin and not in the master's. No Skyrim install
/// required: the fallback folder paths are consulted before the data folder, and hit.</para>
/// </summary>
public class ValidatorInjectedRecordTests
{
    private static readonly ModKey MasterKey = ModKey.FromFileName("TestMaster.esm");
    private static readonly ModKey ReplacerKey = ModKey.FromFileName("Replacer.esp");

    /// A FormKey in the MASTER's space that only the replacer defines — an injected record.
    private static readonly FormKey InjectedHeadPart = FormKey.Factory("000D62:TestMaster.esm");

    /// A FormKey in the master's space that nothing defines — genuine version drift.
    private static readonly FormKey DriftedHeadPart = FormKey.Factory("000E77:TestMaster.esm");

    /// <summary>Writes a master with one head part of its own, and a replacer that injects another
    /// into the master's ID space. Returns the folder both live in.</summary>
    private static string BuildFixture(TempDir dir)
    {
        var folder = dir.Path;

        var master = new SkyrimMod(MasterKey, SkyrimRelease.SkyrimSE);
        master.HeadParts.AddNew().EditorID = "MasterOwnHeadPart";
        master.WriteToBinary(Path.Combine(folder, MasterKey.FileName));

        var replacer = new SkyrimMod(ReplacerKey, SkyrimRelease.SkyrimSE);
        var injected = new HeadPart(InjectedHeadPart, SkyrimRelease.SkyrimSE) { EditorID = "ARA_Injected_HAIR" };
        replacer.HeadParts.Add(injected);
        replacer.WriteToBinary(Path.Combine(folder, ReplacerKey.FileName));

        return folder;
    }

    private static ModSetting Mod(string folder, bool handleInjected) => new()
    {
        DisplayName = "Beyond Skyrim Bruma NPC replacer",
        CorrespondingModKeys = new List<ModKey> { ReplacerKey },
        CorrespondingFolderPaths = new List<string> { folder },
        MergeInDependencyRecords = true,
        HandleInjectedRecords = handleInjected,
    };

    /// <summary>A Validator with only the two collaborators this seam touches.</summary>
    private static Validator MakeValidator()
    {
        var settings = new Settings();

        // PluginProvider reads exactly one thing off the environment on this path — the release to
        // parse binaries as — and never reaches the data folder, because the mod's own folder paths
        // are consulted first and hit. So a stubbed release is the whole environment this needs.
        var env = Reflect.Uninitialized<EnvironmentStateProvider>();
        Reflect.SetField(env, "SkyrimVersion", SkyrimRelease.SkyrimSE);

        var pluginProvider = new PluginProvider(env, settings);
        var recordHandler = new RecordHandler(env, pluginProvider, settings);

        var validator = Reflect.Uninitialized<Validator>();
        Reflect.SetField(validator, "_recordHandler", recordHandler);
        Reflect.SetField(validator, "_pluginProvider", pluginProvider);
        Reflect.SetField(validator, "_settings", settings);
        return validator;
    }

    private static ModKey? FindSource(Validator validator, FormKey key, ModSetting mod, string folder) =>
        Reflect.Invoke<ModKey?>(validator, "FindInjectedRecordSource",
            key, typeof(IHeadPartGetter), mod,
            new HashSet<string>(new[] { folder }, StringComparer.OrdinalIgnoreCase),
            FormKey.Factory("000ABC:SomeOtherPlugin.esp").ModKey,
            new Dictionary<ModKey, ModSetting>());

    [Fact]
    public void InjectedRecord_IsFoundInTheModsOwnPlugin()
    {
        using var dir = new TempDir("injected");
        var folder = BuildFixture(dir);
        var validator = MakeValidator();

        var source = FindSource(validator, InjectedHeadPart, Mod(folder, handleInjected: true), folder);

        source.Should().NotBeNull("the replacer defines this record even though its FormKey belongs to the master");
        source!.Value.Should().Be(ReplacerKey);
    }

    [Fact]
    public void RecordNothingDefines_IsNotFound_SoTheDriftVerdictStands()
    {
        // The LOTD v5-vs-v6 case the check was written for: the appearance mod references a record
        // that the installed master no longer has, and does not carry it either. Must stay rejected.
        using var dir = new TempDir("drift");
        var folder = BuildFixture(dir);
        var validator = MakeValidator();

        FindSource(validator, DriftedHeadPart, Mod(folder, handleInjected: true), folder)
            .Should().BeNull();
    }

    [Fact]
    public void TheFlagDoesNotAffectTheLookup_OnlyTheVerdictBuiltFromIt()
    {
        // FindInjectedRecordSource answers "is it injected", never "should we allow it" — the
        // HandleInjectedRecords branch lives in the caller. Keeping the two separate is what lets
        // the flag-off case report the actionable reason instead of the drift one.
        using var dir = new TempDir("flagoff");
        var folder = BuildFixture(dir);
        var validator = MakeValidator();

        FindSource(validator, InjectedHeadPart, Mod(folder, handleInjected: false), folder)
            .Should().Be(ReplacerKey);
    }

    [Fact]
    public void ModWithNoMergeEligiblePlugins_FindsNothing()
    {
        // Merge-in off means the patcher copies no records from this mod at all, so an injected
        // record could not be delivered and the rejection must stand.
        using var dir = new TempDir("nomerge");
        var folder = BuildFixture(dir);
        var validator = MakeValidator();

        var mod = Mod(folder, handleInjected: true);
        mod.MergeInDependencyRecords = false;

        FindSource(validator, InjectedHeadPart, mod, folder).Should().BeNull();
    }

    [Fact]
    public void DonorsOwnPluginIsExcluded_MirroringThePatcher()
    {
        // The patcher builds importSourceModKeys excluding the donor NPC's defining plugin, so a
        // record that only that plugin carries is never duplicated. Screening must agree.
        using var dir = new TempDir("donorexcl");
        var folder = BuildFixture(dir);
        var validator = MakeValidator();

        var source = Reflect.Invoke<ModKey?>(validator, "FindInjectedRecordSource",
            InjectedHeadPart, typeof(IHeadPartGetter), Mod(folder, handleInjected: true),
            new HashSet<string>(new[] { folder }, StringComparer.OrdinalIgnoreCase),
            ReplacerKey, // donor's own plugin == the only plugin that could supply it
            new Dictionary<ModKey, ModSetting>());

        source.Should().BeNull();
    }
}
