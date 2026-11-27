using System.IO;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// The user-chosen output split (<see cref="Settings.SplitOutput"/> by gender, race or NPC count),
/// end to end through the headless runner. The grouping and the per-group patch loop used to live
/// in <c>VM_Run</c> only, so a headless build never split; they are now
/// <see cref="SplitOutputRunner"/>, which <c>VM_Run</c> and <see cref="GoldenPatchRunner"/> share.
///
/// <para>Not to be confused with <see cref="AutoSplitOutputTests"/>: that is Mutagen's master-limit
/// split inside one save (<see cref="Settings.AutoSplitOutput"/>), a separate feature.</para>
///
/// <para>Fixture pattern copied from <c>IncludeOverridePatchingTests</c>: a base plugin injected
/// into the load order, an appearance plugin in its own folder that is not in the load order, and
/// FaceGen stub files. Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class UserSplitOutputTests
{
    private readonly ITestOutputHelper _output;
    public UserSplitOutputTests(ITestOutputHelper output) => _output = output;

    private static readonly ModKey[] BaseMasters = Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();
    private static readonly ModKey BaseKey = ModKey.FromFileName("NPC2Split.esp");
    private static readonly ModKey LookKey = ModKey.FromFileName("NPC2SplitLook.esp");
    private const string LookMod = "Split Fixture Look";

    private static bool IsVanillaOrCc(ModKey key)
    {
        if (BaseMasters.Contains(key)) return true;
        var fn = key.FileName.String;
        return fn.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
               || fn.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Outcome
    {
        public required string OutDir { get; init; }
        public required List<FormKey> Males { get; init; }
        public required List<FormKey> Females { get; init; }
        public required GoldenPatchResult Run { get; init; }
        /// <summary>Plugin file name -> the NPC records it holds. Root of the output folder only.</summary>
        public required Dictionary<string, List<FormKey>> NpcsByPlugin { get; init; }
        public List<FormKey> All => Males.Concat(Females).ToList();
    }

    /// <summary>Builds the fixture (two men, one woman), runs the headless patcher with the given
    /// split settings, and reads back every plugin in the output folder. Null when no Skyrim SE
    /// environment resolves.</summary>
    private async Task<Outcome?> Run(string root, Action<Settings> split)
    {
        var lookFolder = Path.Combine(root, "LookMod");
        var outDir = Path.Combine(root, "Output");
        var envOutDir = Path.Combine(root, "EnvOutput");
        var writeOrder = BaseMasters.Concat(new[] { BaseKey, LookKey }).ToArray();

        // --- base: three NPCs of one race, in a plugin of its own --------------------------------
        var baseMod = new SkyrimMod(BaseKey, SkyrimRelease.SkyrimSE);
        var race = baseMod.Races.AddNew();
        race.EditorID = "NPC2Split_Race";
        race.Flags |= Race.Flag.FaceGenHead; // or screening drops every NPC on it

        var headPart = baseMod.HeadParts.AddNew();
        headPart.EditorID = "NPC2Split_HP";
        headPart.Type = HeadPart.TypeEnum.Face;

        Npc NewNpc(string edid, bool female)
        {
            var npc = baseMod.Npcs.AddNew();
            npc.EditorID = edid;
            npc.Name = edid;
            npc.Race.SetTo(race.FormKey);
            npc.HeadParts.Add(headPart.FormKey);
            if (female) npc.Configuration.Flags |= NpcConfiguration.Flag.Female;
            return npc;
        }
        var man1 = NewNpc("NPC2Split_Man1", female: false);
        var woman = NewNpc("NPC2Split_Woman", female: true);
        var man2 = NewNpc("NPC2Split_Man2", female: false);
        var npcs = new[] { man1, woman, man2 };

        var basePath = Path.Combine(root, "Base", BaseKey.FileName);
        WriteMod(baseMod, basePath, writeOrder);

        // --- the appearance plugin: never in the load order --------------------------------------
        var lookMod = new SkyrimMod(LookKey, SkyrimRelease.SkyrimSE);
        foreach (var npc in npcs)
        {
            var npcOverride = lookMod.Npcs.GetOrAddAsOverride(npc);
            npcOverride.Height = 1.05f;
        }
        WriteMod(lookMod, Path.Combine(lookFolder, LookKey.FileName), writeOrder);
        foreach (var npc in npcs) WriteFaceGen(lookFolder, npc.FormKey);

        // --- environment: vanilla + the base plugin; the appearance plugin stays out --------------
        IEnumerable<IModListingGetter<ISkyrimModGetter>> Transform(IEnumerable<IModListingGetter<ISkyrimModGetter>> input)
        {
            var listings = input.OnlyEnabledAndExisting().Where(l => IsVanillaOrCc(l.ModKey)).ToList();
            listings.Add(new ModListing<ISkyrimModGetter>(
                BaseKey, SkyrimMod.CreateFromBinaryOverlay(basePath, SkyrimRelease.SkyrimSE), enabled: true));
            return listings;
        }

        Directory.CreateDirectory(envOutDir);
        var provider = new EnvironmentStateProvider(null);
        provider.SetEnvironmentTarget(SkyrimRelease.SkyrimSE, string.Empty, "NPC", envOutDir);
        provider.UpdateEnvironmentForTest(Transform);
        if (provider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
        {
            _output.WriteLine("SKIPPED: no valid Skyrim SE environment. " + provider.EnvironmentBuilderError);
            return null;
        }

        var settings = new Settings
        {
            SkyrimRelease = SkyrimRelease.SkyrimSE,
            OutputPluginName = string.Empty,
            OutputDirectory = outDir,
            AppendTimestampToOutputDirectory = false,
            ModsFolder = string.Empty,
            SplitOutput = false,
            AutoEslIfy = false,
            AutoSplitOutput = false,
            PatchingMode = PatchingMode.CreateAndPatch,
            OutputBackend = OutputBackend.ESP,
            DefaultWigHandlingMode = WigHandlingMode.None,
            DefaultAntlerHandlingMode = AntlerHandlingMode.None,
            TemplateHandlingMode = TemplateHandlingMode.InheritFromTemplate,
            DefaultRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
            DefaultMaxNestedIntervalDepth = 2,
            DefaultIncludeAllOverrides = false,
            PublishForwardedOutfitsToDistributors = false,
            LocalizationLanguage = null,
            BatFilePreCommands = string.Empty,
            BatFilePostCommands = string.Empty,
        };
        split(settings);
        settings.ModSettings = new List<ModSetting>
        {
            new()
            {
                DisplayName = LookMod,
                CorrespondingModKeys = new List<ModKey> { LookKey },
                CorrespondingFolderPaths = new List<string> { lookFolder },
                MergeInDependencyRecords = true,
                IncludeOutfits = false,
                CopyAssets = false,
                ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
                IncludeAllOverrides = false,
            },
        };
        settings.SelectedAppearanceMods = npcs.ToDictionary(n => n.FormKey, n => (LookMod, n.FormKey));

        Directory.CreateDirectory(outDir);
        var run = await GoldenPatchRunner.RunAsync(provider, settings);
        _output.WriteLine("--- RUN LOG ---\n" + run.Log + "\n--- END RUN LOG ---");

        var npcsByPlugin = new Dictionary<string, List<FormKey>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(outDir, "*", SearchOption.TopDirectoryOnly)
                     .Where(p => new[] { ".esp", ".esm", ".esl" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)))
        {
            using var plugin = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            npcsByPlugin[Path.GetFileName(path)] = plugin.Npcs.Select(n => n.FormKey).ToList();
        }
        _output.WriteLine("output plugins: " + string.Join("; ",
            npcsByPlugin.Select(kv => $"{kv.Key} [{string.Join(", ", kv.Value)}]")));

        return new Outcome
        {
            OutDir = outDir,
            Males = new List<FormKey> { man1.FormKey, man2.FormKey },
            Females = new List<FormKey> { woman.FormKey },
            Run = run,
            NpcsByPlugin = npcsByPlugin,
        };
    }

    [Fact]
    public async Task SplitByGender_WritesOnePluginPerGender_AndEveryNpcIsInExactlyOne()
    {
        var root = NewRoot();
        try
        {
            var r = await Run(root, s =>
            {
                s.SplitOutput = true;
                s.SplitOutputByGender = true;
            });
            if (r == null) return;

            r.Run.Log.Should().NotContain("FATAL SAVE ERROR");
            r.Run.PatchedTargets.Should().BeEquivalentTo(r.All, "all three selections must pass screening");

            r.NpcsByPlugin.Keys.Should().BeEquivalentTo(new[] { "NPC_Male.esp", "NPC_Female.esp" },
                "a gender split names each plugin <output name>_<gender>, and writes nothing else");
            r.NpcsByPlugin["NPC_Male.esp"].Should().BeEquivalentTo(r.Males);
            r.NpcsByPlugin["NPC_Female.esp"].Should().BeEquivalentTo(r.Females);

            foreach (var npc in r.All)
            {
                r.NpcsByPlugin.Count(kv => kv.Value.Contains(npc)).Should().Be(1,
                    $"{npc} must be patched by exactly one of the split plugins");
            }

            File.Exists(Path.Combine(r.OutDir, "NPC_Token.json")).Should().BeTrue(
                "the unified token is written once, after the last group");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task SplitOff_WritesASinglePlugin_EvenWithASplitCriterionTicked()
    {
        var root = NewRoot();
        try
        {
            // The criterion alone does nothing: SplitOutput is the master switch.
            var r = await Run(root, s =>
            {
                s.SplitOutput = false;
                s.SplitOutputByGender = true;
            });
            if (r == null) return;

            r.Run.Log.Should().NotContain("FATAL SAVE ERROR");
            r.Run.PatchedTargets.Should().BeEquivalentTo(r.All, "all three selections must pass screening");

            r.NpcsByPlugin.Keys.Should().BeEquivalentTo(new[] { "NPC.esp" });
            r.NpcsByPlugin["NPC.esp"].Should().BeEquivalentTo(r.All);

            File.Exists(Path.Combine(r.OutDir, "NPC_Token.json")).Should().BeTrue();
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ helpers

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "NpcUserSplit_" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* overlay may hold a map */ }
    }

    private static void WriteMod(SkyrimMod mod, string path, ModKey[] writeOrder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        mod.BeginWrite.ToPath(path).WithLoadOrder(writeOrder).WithNoDataFolder().Write();
    }

    private static void WriteFaceGen(string modFolder, FormKey npcKey)
    {
        var (meshRel, texRel) = Auxilliary.GetFaceGenSubPathStrings(npcKey, regularized: true);
        foreach (var rel in new[] { meshRel, texRel })
        {
            var path = Path.Combine(modFolder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "npc2-split-fixture");
        }
    }
}
