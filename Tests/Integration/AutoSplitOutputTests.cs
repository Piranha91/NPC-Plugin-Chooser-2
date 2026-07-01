using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Analysis;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// End-to-end coverage for Mutagen's automatic master-splitting (the <c>AutoSplitOutput</c>
/// feature) at the write + discovery layer, without needing a real Skyrim install.
///
/// The over-limit scenario is synthesised the way a heavy SkyPatcher run produces it: an output
/// plugin "NPCTest.esp" with <see cref="MasterCount"/> new NPCs, each carrying a single distinct
/// master dependency (a Race FormLink into "Master{i}.esp"). That is &gt; 255 masters, so the exact
/// builder chain the patcher uses (<c>BeginWrite.ToPath(..).WithLoadOrder(..).WithAutoSplit()</c>)
/// splits it into "NPCTest.esp" + "NPCTest_2.esp" (+ ...). Each new NPC clusters with its master,
/// so surrogate-style records spread across the sibling files exactly as real ones would.
///
/// Joins the integration collection so it runs serially with the other environment-touching tests.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class AutoSplitOutputTests
{
    private const int MasterCount = 300; // comfortably over the 255-master limit -> forces a split

    private static ModKey OutputKey => ModKey.FromName("NPCTest", ModType.Plugin);

    /// <summary>
    /// Builds an output mod that masters to <see cref="MasterCount"/> distinct plugins by giving each
    /// of that many new NPCs a Race FormLink into a distinct "Master{i}.esp". Returns the master keys
    /// (the load order the write needs for master ordering).
    /// </summary>
    private static SkyrimMod BuildOverLimitOutput(out List<ModKey> masters)
    {
        var mod = new SkyrimMod(OutputKey, SkyrimRelease.SkyrimSE);
        masters = new List<ModKey>();
        for (int i = 0; i < MasterCount; i++)
        {
            var master = ModKey.FromName($"Master{i}", ModType.Plugin);
            masters.Add(master);
            var npc = mod.Npcs.AddNew();
            npc.EditorID = $"Surrogate{i}";
            npc.Race.SetTo(new FormKey(master, 0x800)); // exactly one master dependency per record
        }
        return mod;
    }

    /// <summary>
    /// Like <see cref="BuildOverLimitOutput"/>, but every NPC also shares ONE new Armor (as its WNAM /
    /// WornArmor) and ONE new Keyword, both local to the output plugin. When the mod splits, those
    /// two shared records can live in only one cluster; the other cluster's NPCs must reference them
    /// cross-plugin (mastering the owner) rather than getting duplicate copies.
    /// </summary>
    private static SkyrimMod BuildOverLimitOutputWithSharedRecords(
        out List<ModKey> masters, out FormKey armorKey, out FormKey keywordKey)
    {
        var mod = new SkyrimMod(OutputKey, SkyrimRelease.SkyrimSE);

        var armor = mod.Armors.AddNew();
        armor.EditorID = "SharedArmor";
        var keyword = mod.Keywords.AddNew();
        keyword.EditorID = "SharedKeyword";
        armorKey = armor.FormKey;
        keywordKey = keyword.FormKey;

        masters = new List<ModKey>();
        for (int i = 0; i < MasterCount; i++)
        {
            var master = ModKey.FromName($"Master{i}", ModType.Plugin);
            masters.Add(master);
            var npc = mod.Npcs.AddNew();
            npc.EditorID = $"Surrogate{i}";
            npc.Race.SetTo(new FormKey(master, 0x800)); // distinct master dependency -> forces the split
            npc.WornArmor.SetTo(armor);                 // WNAM -> the shared armor
            npc.Keywords ??= new();
            npc.Keywords.Add(new FormLink<IKeywordGetter>(keyword.FormKey)); // the shared keyword
        }
        return mod;
    }

    private static async Task WriteWithAutoSplitAsync(SkyrimMod mod, string path, IEnumerable<ModKey> masters)
    {
        // Mirror BackEnd/Patcher.cs's write chain. WithLoadOrder must be given LOADED mods (not bare
        // ModKeys) so the builder knows each master's style without a data folder - matching how the
        // patcher passes the environment's real load order. Empty stand-ins are enough here.
        var loadOrder = masters
            .Select(mk => (ISkyrimModGetter)new SkyrimMod(mk, SkyrimRelease.SkyrimSE))
            .ToArray();
        await mod.BeginWrite
            .ToPath(path)
            .WithLoadOrder(loadOrder)
            .WithAutoSplit()
            .WriteAsync();
    }

    [Fact]
    public async Task WithAutoSplit_OverMasterLimit_SplitsIntoBasePlusNumberedSiblings()
    {
        using var tmp = new TempDir();
        var mod = BuildOverLimitOutput(out var masters);
        var path = Path.Combine(tmp.Path, mod.ModKey.FileName);

        await WriteWithAutoSplitAsync(mod, path, masters);

        // The base file keeps the original name; the overflow produces at least one _2 sibling.
        File.Exists(path).Should().BeTrue("the base split file keeps the original name");
        File.Exists(Path.Combine(tmp.Path, "NPCTest_2.esp"))
            .Should().BeTrue("overflow must produce at least one numbered sibling");

        var splitFiles = MultiModFileAnalysis.GetSplitModFiles(new ModPath(mod.ModKey, path));
        splitFiles.Count.Should().BeGreaterThan(1, "GetSplitModFiles must report the split set");

        // Every cluster must be under the 255-master limit - that is the whole point of the split.
        foreach (var fp in splitFiles)
        {
            using var cluster = SkyrimMod.CreateFromBinaryOverlay((string)fp, SkyrimRelease.SkyrimSE);
            cluster.ModHeader.MasterReferences.Count.Should().BeLessThanOrEqualTo(255);
        }
    }

    [Fact]
    public async Task WithAutoSplit_SharedNewRecords_AreNotDuplicated_ButCrossReferenced()
    {
        using var tmp = new TempDir();
        var mod = BuildOverLimitOutputWithSharedRecords(out var masters, out var armorKey, out var keywordKey);
        var path = Path.Combine(tmp.Path, mod.ModKey.FileName);

        await WriteWithAutoSplitAsync(mod, path, masters);

        var splitFiles = MultiModFileAnalysis.GetSplitModFiles(new ModPath(mod.ModKey, path));
        splitFiles.Count.Should().BeGreaterThan(1, "the 300 masters must force a split");

        var armorOwners = new List<ModKey>();
        var keywordOwners = new List<ModKey>();
        var mastersByFile = new Dictionary<ModKey, HashSet<ModKey>>();

        foreach (var fp in splitFiles)
        {
            var fileModKey = ModKey.FromFileName(Path.GetFileName((string)fp));
            using var cluster = SkyrimMod.CreateFromBinaryOverlay((string)fp, SkyrimRelease.SkyrimSE);
            mastersByFile[fileModKey] = cluster.ModHeader.MasterReferences.Select(m => m.Master).ToHashSet();
            if (cluster.Armors.Any(a => a.FormKey.ID == armorKey.ID)) armorOwners.Add(fileModKey);
            if (cluster.Keywords.Any(k => k.FormKey.ID == keywordKey.ID)) keywordOwners.Add(fileModKey);
        }

        // The shared records must exist in exactly ONE split file - never duplicated.
        armorOwners.Should().ContainSingle("the shared WNAM armor must live in one file, not be duplicated");
        keywordOwners.Should().ContainSingle("the shared keyword must live in one file, not be duplicated");

        // All NPCs reference both shared records and NPCs are spread across every split file, so each
        // file that does NOT own a shared record must MASTER the file that does (a cross-plugin
        // reference) rather than carrying its own copy.
        foreach (var (fileModKey, fileMasters) in mastersByFile)
        {
            if (!fileModKey.Equals(armorOwners[0]))
                fileMasters.Should().Contain(armorOwners[0],
                    "a sibling referencing the shared armor must master its owner instead of duplicating it");
            if (!fileModKey.Equals(keywordOwners[0]))
                fileMasters.Should().Contain(keywordOwners[0],
                    "a sibling referencing the shared keyword must master its owner instead of duplicating it");
        }
    }

    [Fact]
    public async Task BuildSplitFormKeyRemap_MapsMovedRecordsToTrueFiles_AgainstGroundTruth()
    {
        using var tmp = new TempDir();
        var mod = BuildOverLimitOutput(out var masters);
        var path = Path.Combine(tmp.Path, mod.ModKey.FileName);
        await WriteWithAutoSplitAsync(mod, path, masters);

        // Ground truth: for every non-base sibling, each record mastered to that sibling was
        // originally "NPCTest.esp|ID" (same local id) and now lives at "<sibling>|ID".
        var expected = new Dictionary<FormKey, FormKey>();
        foreach (var fp in MultiModFileAnalysis.GetSplitModFiles(new ModPath(mod.ModKey, path)))
        {
            var fileModKey = ModKey.FromFileName(Path.GetFileName((string)fp));
            if (fileModKey.Equals(mod.ModKey)) continue; // base file keeps the original key -> no remap
            using var cluster = SkyrimMod.CreateFromBinaryOverlay((string)fp, SkyrimRelease.SkyrimSE);
            foreach (var rec in cluster.EnumerateMajorRecords())
            {
                if (!rec.FormKey.ModKey.Equals(fileModKey)) continue; // skip overrides; new records only
                expected[new FormKey(mod.ModKey, rec.FormKey.ID)] = rec.FormKey;
            }
        }
        expected.Should().NotBeEmpty("the splitter must place some records into numbered siblings");

        // Exercise the REAL private helper on a Patcher (Invalid env is enough: the helper only reads
        // OutputMod.ModKey + SkyrimVersion).
        var env = NpcChooserTestEnvironment.Invalid(outputPluginName: "NPCTest");
        env.OutputMod = new SkyrimMod(OutputKey, SkyrimRelease.SkyrimSE);
        using var harness = new NpcChooserHarness(env, new Settings());
        var patcher = harness.Patcher;

        var actual = Reflect.Invoke<IReadOnlyDictionary<FormKey, FormKey>?>(patcher, "BuildSplitFormKeyRemap", path);

        actual.Should().NotBeNull("a split occurred, so the helper must return a remap");
        actual!.Should().BeEquivalentTo(expected, "the helper must map every moved record to its true file");
        // Sanity: ids preserved, and nothing points back at the base plugin.
        foreach (var (orig, mapped) in actual!)
        {
            mapped.ID.Should().Be(orig.ID);
            mapped.ModKey.Should().NotBe(mod.ModKey);
        }
    }

    [Fact]
    public async Task BuildSplitFormKeyRemap_WhenNotSplit_ReturnsNull()
    {
        // A small mod that never trips the master limit: no split files, so no remap.
        using var tmp = new TempDir();
        var mod = new SkyrimMod(OutputKey, SkyrimRelease.SkyrimSE);
        var npc = mod.Npcs.AddNew();
        npc.EditorID = "Solo";
        var path = Path.Combine(tmp.Path, mod.ModKey.FileName);
        await WriteWithAutoSplitAsync(mod, path, System.Array.Empty<ModKey>());

        var env = NpcChooserTestEnvironment.Invalid(outputPluginName: "NPCTest");
        env.OutputMod = new SkyrimMod(OutputKey, SkyrimRelease.SkyrimSE);
        using var harness = new NpcChooserHarness(env, new Settings());

        Reflect.Invoke<IReadOnlyDictionary<FormKey, FormKey>?>(harness.Patcher, "BuildSplitFormKeyRemap", path)
            .Should().BeNull("a single-file output needs no remap");
    }

    [Fact]
    public async Task SplitFiles_DoNotInheritHeaderStamp_DocumentsKnownLimitation()
    {
        // Characterisation: Mutagen's splitter builds each cluster as a fresh mod and does NOT copy
        // the header, so the PluginDescriptionSignature stamp set before the write is lost on ALL
        // split files (base included). NPC2 tolerates this because output exclusion from the rebuilt
        // environment is folder-based (GetOwnOutputModKeys enumerates every .esp in the output
        // folder) and OutputValidator's "is output active" check still matches the base filename.
        // If a future change re-stamps the split files, flip this test.
        using var tmp = new TempDir();
        var mod = BuildOverLimitOutput(out var masters);
        mod.ModHeader.Description = Patcher.PluginDescriptionSignature;
        var path = Path.Combine(tmp.Path, mod.ModKey.FileName);

        await WriteWithAutoSplitAsync(mod, path, masters);

        foreach (var fp in MultiModFileAnalysis.GetSplitModFiles(new ModPath(mod.ModKey, path)))
        {
            using var cluster = SkyrimMod.CreateFromBinaryOverlay((string)fp, SkyrimRelease.SkyrimSE);
            cluster.ModHeader.Description.Should().NotBe(Patcher.PluginDescriptionSignature,
                "the splitter does not currently propagate the header stamp to cluster files");
        }
    }
}
