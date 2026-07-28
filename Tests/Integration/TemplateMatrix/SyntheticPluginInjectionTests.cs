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

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// The seam the whole templated-NPC matrix rests on, kept as its own minimal test: a plugin authored
/// by the test itself — not loaded from a real mod folder — is injected into
/// <see cref="EnvironmentStateProvider"/> via <c>UpdateEnvironmentForTest</c>, resolved through the
/// link cache, screened, patched, and read back. One plugin, one NPC, one selection, one patch run, so
/// a failure here points at the seam rather than at anything the matrix builds on top of it.
///
/// <para>The second case (<c>writeFaceGen: false</c>) pins the trap the matrix's presence gate exists
/// for: an NPC with no resolvable face mesh passes screening and lands in
/// <c>GoldenPatchResult.PatchedTargets</c>, yet is skipped by the patch loop and is wholly absent from
/// the output plugin. Any assertion gated on <c>PatchedTargets</c> would pass vacuously against it.</para>
///
/// <para>Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class SyntheticPluginInjectionTests
{
    private readonly ITestOutputHelper _output;

    public SyntheticPluginInjectionTests(ITestOutputHelper output) => _output = output;

    private static readonly ModKey[] BaseMasters =
        Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();

    private static bool IsVanillaOrCc(ModKey key)
    {
        if (BaseMasters.Contains(key)) return true;
        var fn = key.FileName.String;
        return fn.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
               || fn.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="writeFaceGen">
    /// When false the fixture ships no FaceGen at all, so the ladder can find no face mesh for the NPC
    /// "in the selected mod, in the mod that originally added it, or anywhere else in the load order"
    /// and the patcher leaves it unchanged. The selection still passes screening and still appears in
    /// <c>PatchedTargets</c> — which is measured from the screening cache, before the patch loop runs —
    /// while the output plugin comes back empty. This is why the matrix gates on the run's own
    /// NPC_Token.json plus the written record, not on <c>PatchedTargets</c>.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyntheticPlugin_IsInjected_ScreenedAndPatched(bool writeFaceGen)
    {
        var root = Path.Combine(Path.GetTempPath(), "NpcSpike_" + Guid.NewGuid().ToString("N"));
        var modFolder = Path.Combine(root, "SpikeMod");
        var outDir = Path.Combine(root, "Output");
        var envOutDir = Path.Combine(root, "EnvOutput");
        Directory.CreateDirectory(modFolder);
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(envOutDir);

        try
        {
            // --- 1. Author the fixture plugin ------------------------------------------------
            var fixtureKey = ModKey.FromFileName("NPC2SpikeFixture.esp");
            var mod = new SkyrimMod(fixtureKey, SkyrimRelease.SkyrimSE);
            var npc = mod.Npcs.AddNew();
            npc.EditorID = "NPC2Spike_PlainSelf";
            npc.Name = "NPC2 Spike Plain Self";
            npc.Race.SetTo(Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Race.NordRace);
            var npcFormKey = npc.FormKey;

            var pluginPath = Path.Combine(modFolder, fixtureKey.FileName);
            var writeOrder = BaseMasters.Append(fixtureKey).ToArray();
            mod.BeginWrite
                .ToPath(pluginPath)
                .WithLoadOrder(writeOrder)
                .WithNoDataFolder()
                .Write();

            File.Exists(pluginPath).Should().BeTrue("the fixture plugin must be written to disk");
            _output.WriteLine($"Authored {pluginPath} with NPC {npcFormKey} ({npc.EditorID}).");

            // --- 2. Placeholder FaceGen under the NPC's own FormID path ------------------------
            // NOTE: regularized paths already carry their "meshes\" / "textures\" root.
            var (meshSub, texSub) = Auxilliary.GetFaceGenSubPathStrings(npcFormKey, regularized: true);
            if (writeFaceGen)
            {
                var meshPath = Path.Combine(modFolder, meshSub);
                var texPath = Path.Combine(modFolder, texSub);
                Directory.CreateDirectory(Path.GetDirectoryName(meshPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(texPath)!);
                File.WriteAllText(meshPath, "spike-placeholder-nif");
                File.WriteAllText(texPath, "spike-placeholder-dds");
            }
            _output.WriteLine($"FaceGen sub-paths ({(writeFaceGen ? "written" : "OMITTED")}): {meshSub} | {texSub}");

            // --- 3. Inject it into the environment ---------------------------------------------
            IEnumerable<IModListingGetter<ISkyrimModGetter>> Transform(
                IEnumerable<IModListingGetter<ISkyrimModGetter>> input)
            {
                var result = input.OnlyEnabledAndExisting()
                    .Where(l => IsVanillaOrCc(l.ModKey))
                    .ToList();
                var loaded = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
                result.Add(new ModListing<ISkyrimModGetter>(fixtureKey, loaded, enabled: true));
                return result;
            }

            var provider = new EnvironmentStateProvider(null);
            provider.SetEnvironmentTarget(SkyrimRelease.SkyrimSE, string.Empty, "NPC", envOutDir);
            provider.UpdateEnvironmentForTest(Transform);

            if (provider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
            {
                _output.WriteLine("SKIPPED: no valid Skyrim SE environment. " + provider.EnvironmentBuilderError);
                return;
            }

            provider.LoadOrder!.ListedOrder.Select(l => l.ModKey).Should().Contain(fixtureKey,
                "the synthetic plugin must land in the resolved load order");

            // The decisive seam question: can the link cache resolve a record the test authored?
            provider.LinkCache!.TryResolve<INpcGetter>(npcFormKey, out var resolved).Should().BeTrue(
                "the patcher resolves every target NPC through the link cache");
            resolved!.EditorID.Should().Be("NPC2Spike_PlainSelf");
            _output.WriteLine("Link cache resolved the synthetic NPC.");

            // --- 4. Settings: one mod, one selection (self donor) -------------------------------
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
                PatchingMode = PatchingMode.Create,
                UseSkyPatcherMode = false,
                DefaultRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
                DefaultMaxNestedIntervalDepth = 2,
                DefaultIncludeAllOverrides = false,
                LocalizationLanguage = null,
                BatFilePreCommands = string.Empty,
                BatFilePostCommands = string.Empty,
            };
            settings.ModSettings = new List<ModSetting>
            {
                new()
                {
                    DisplayName = "SpikeMod",
                    CorrespondingModKeys = new List<ModKey> { fixtureKey },
                    CorrespondingFolderPaths = new List<string> { modFolder },
                    MergeInDependencyRecords = false,
                    IncludeOutfits = false,
                    CopyAssets = true,
                    ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
                },
            };
            settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
            {
                [npcFormKey] = ("SpikeMod", npcFormKey),
            };

            // --- 5. Run the real patcher --------------------------------------------------------
            var run = await GoldenPatchRunner.RunAsync(provider, settings);
            _output.WriteLine($"patched={run.PatchedTargets.Count} invalid={run.InvalidSelections.Count}");
            foreach (var inv in run.InvalidSelections) _output.WriteLine("  INVALID: " + inv);
            _output.WriteLine("--- RUN LOG ---\n" + run.Log + "\n--- END RUN LOG ---");

            run.PatchedTargets.Should().Contain(npcFormKey,
                "the synthetic NPC must survive screening and be patched");

            // --- 6. Read the output back ---------------------------------------------------------
            var outPlugin = Path.Combine(outDir, "NPC.esp");
            File.Exists(outPlugin).Should().BeTrue("the patcher must write an output plugin");
            _output.WriteLine($"output plugin size: {new FileInfo(outPlugin).Length} bytes");
            using var outHandle = SkyrimMod.CreateFromBinaryOverlay(outPlugin, SkyrimRelease.SkyrimSE);
            ISkyrimModGetter outMod = outHandle;
            _output.WriteLine("output masters: " + string.Join(", ",
                outMod.ModHeader.MasterReferences.Select(m => m.Master.FileName.String)));
            _output.WriteLine("output major records: " + string.Join(", ",
                outMod.EnumerateMajorRecords()
                    .Select(r => $"{r.Registration.Name} {r.FormKey} '{r.EditorID}'")));
            var outNpcs = outMod.Npcs.ToList();
            _output.WriteLine("output NPCs: " + string.Join(", ",
                outNpcs.Select(n => $"{n.FormKey} '{n.EditorID}'")));

            if (!writeFaceGen)
            {
                // Measurement only — this case exists to characterise what a FaceGen-less specimen
                // does, not to pin desired behaviour. Read the run log above for the verdict.
                _output.WriteLine("NO-FACEGEN OBSERVATION: record in output = " +
                                  outNpcs.Any(n => n.EditorID == "NPC2Spike_PlainSelf"));
                return;
            }

            outNpcs.Should().Contain(n => n.EditorID == "NPC2Spike_PlainSelf",
                "the patched record must be identifiable by EditorID (FormKeys are remapped)");

            // --- 7. Did the FaceGen land? ---------------------------------------------------------
            var writtenMeshes = Directory.Exists(Path.Combine(outDir, "meshes"))
                ? Directory.GetFiles(Path.Combine(outDir, "meshes"), "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
            _output.WriteLine("output meshes:\n  " + string.Join("\n  ",
                writtenMeshes.Select(p => Path.GetRelativePath(outDir, p))));
            writtenMeshes.Should().NotBeEmpty("the placeholder FaceGen mesh should be copied to the output");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* overlay may hold a map */ }
        }
    }
}
