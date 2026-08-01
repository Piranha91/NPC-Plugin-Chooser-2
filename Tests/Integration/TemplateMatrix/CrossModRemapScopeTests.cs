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
/// One appearance mod's merge must not rewrite another appearance mod's already-patched records.
///
/// <para><b>The bug.</b> The merge walker ended with
/// <c>modToDuplicateInto.RemapLinks(mapping)</c> — the whole output plugin — while <c>mapping</c>
/// is per-appearance-mod. Batches are processed alphabetically by display name and the output
/// plugin is cumulative, so every merge retroactively rewrote records written for earlier mods,
/// whose own merge settings were different and often "merge nothing at all".</para>
///
/// <para><b>Why it stayed hidden.</b> Ordinarily <c>mapping</c> only holds FormKeys defined in the
/// merging mod's own plugins, which no unrelated mod's NPC references. <b>Include As New</b> breaks
/// that: it duplicates a mod's overrides of records it does NOT own, so VANILLA FormKeys enter the
/// map — and vanilla FormKeys are referenced by half the output. In the reported load order, one
/// mod's RS Children child-race override was stamped onto 70+ NPCs whose selected mod had merge-in
/// switched off, silently giving them an appearance from a mod that was never chosen for them.</para>
///
/// <para>The fixture is that shape, minimised: mod "Aaa" patches its NPC with nothing merged, then
/// mod "Zzz" (later alphabetically, so its batch runs second) duplicates a vanilla RACE override as
/// a new record. Aaa's NPC must still point at the vanilla race.</para>
///
/// <para>Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class CrossModRemapScopeTests
{
    private readonly ITestOutputHelper _output;

    public CrossModRemapScopeTests(ITestOutputHelper output) => _output = output;

    private static readonly ModKey[] BaseMasters =
        Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();

    private static bool IsVanillaOrCc(ModKey key)
    {
        if (BaseMasters.Contains(key)) return true;
        var fn = key.FileName.String;
        return fn.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
               || fn.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
    }

    // Batches run in DisplayName order, so these names fix which mod is patched first.
    private const string InnocentMod = "Aaa Innocent Mod";
    private const string MergingMod = "Zzz Merging Mod";

    [Theory]
    [InlineData(false)] // record mode (CreateAndPatch)
    [InlineData(true)]  // SkyPatcher mode
    public async Task OneModsMerge_DoesNotRewriteAnotherModsPatchedRecords(bool skyPatcherMode)
    {
        var root = Path.Combine(Path.GetTempPath(), "NpcRemapScope_" + Guid.NewGuid().ToString("N"));
        var innocentFolder = Path.Combine(root, "InnocentMod");
        var mergingFolder = Path.Combine(root, "MergingMod");
        var outDir = Path.Combine(root, "Output");
        var envOutDir = Path.Combine(root, "EnvOutput");

        try
        {
            var baseKey = ModKey.FromFileName("NPC2Scope.esp");
            var innocentKey = ModKey.FromFileName("NPC2ScopeInnocent.esp");
            var mergingKey = ModKey.FromFileName("NPC2ScopeMerging.esp");
            var writeOrder = BaseMasters.Concat(new[] { baseKey, innocentKey, mergingKey }).ToArray();

            // --- base: a shared race plus plain NPCs on it, one per appearance mod -------------
            // The race lives in the BASE plugin, i.e. it belongs to neither appearance mod. That is
            // the property that matters: the merging mod duplicates a record it does not own, so the
            // FormKey entering the remap map is one OTHER mods' NPCs also reference.
            var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
            var sharedRace = baseMod.Races.AddNew();
            sharedRace.EditorID = "NPC2Scope_SharedRace";
            sharedRace.Flags |= Race.Flag.FaceGenHead; // or screening drops every NPC on it

            Npc AddBaseNpc(string edid)
            {
                var hp = baseMod.HeadParts.AddNew();
                hp.EditorID = edid + "_HP";
                hp.Type = HeadPart.TypeEnum.Face;

                var n = baseMod.Npcs.AddNew();
                n.EditorID = edid;
                n.Name = edid;
                n.Race.SetTo(sharedRace.FormKey);
                n.HeadParts.Add(hp.FormKey);
                return n;
            }

            var innocentNpc = AddBaseNpc("NPC2Scope_Innocent");
            // The merging mod needs TWO NPCs: its first NPC is what puts the vanilla race into the
            // duplicate map (override handling runs after that NPC's merge), and the whole-mod
            // remap that leaked ran during the SECOND NPC's merge.
            var mergingNpcA = AddBaseNpc("NPC2Scope_MergingA");
            var mergingNpcB = AddBaseNpc("NPC2Scope_MergingB");

            var basePath = Path.Combine(root, "Base", baseKey.FileName);
            WriteMod(baseMod, basePath, writeOrder);

            // --- innocent mod: a plain appearance override, nothing to merge -------------------
            var innocentMod = new SkyrimMod(innocentKey, SkyrimRelease.SkyrimSE);
            var innocentOverride = innocentMod.Npcs.GetOrAddAsOverride(innocentNpc);
            innocentOverride.Height = 1.05f;
            WriteMod(innocentMod, Path.Combine(innocentFolder, innocentKey.FileName), writeOrder);

            // --- merging mod: overrides the SHARED race, and Include As New duplicates it -------
            var mergingMod = new SkyrimMod(mergingKey, SkyrimRelease.SkyrimSE);
            var raceOverride = mergingMod.Races.GetOrAddAsOverride(sharedRace);
            raceOverride.Description = "overridden"; // any edit; it just has to be a real override
            foreach (var npc in new[] { mergingNpcA, mergingNpcB })
            {
                var o = mergingMod.Npcs.GetOrAddAsOverride(npc);
                o.Height = 1.1f;
            }

            WriteMod(mergingMod, Path.Combine(mergingFolder, mergingKey.FileName), writeOrder);

            // Each mod must ship its NPCs' FaceGen or the patcher leaves them unchanged.
            WriteFaceGen(innocentFolder, innocentNpc.FormKey);
            WriteFaceGen(mergingFolder, mergingNpcA.FormKey);
            WriteFaceGen(mergingFolder, mergingNpcB.FormKey);

            // --- environment: base only; both appearance plugins stay out of the load order ----
            IEnumerable<IModListingGetter<ISkyrimModGetter>> Transform(
                IEnumerable<IModListingGetter<ISkyrimModGetter>> input)
            {
                var listings = input.OnlyEnabledAndExisting()
                    .Where(l => IsVanillaOrCc(l.ModKey))
                    .ToList();
                listings.Add(new ModListing<ISkyrimModGetter>(
                    baseKey, SkyrimMod.CreateFromBinaryOverlay(basePath, SkyrimRelease.SkyrimSE), enabled: true));
                return listings;
            }

            Directory.CreateDirectory(envOutDir);
            var provider = new EnvironmentStateProvider(null);
            provider.SetEnvironmentTarget(SkyrimRelease.SkyrimSE, string.Empty, "NPC", envOutDir);
            provider.UpdateEnvironmentForTest(Transform);

            if (provider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
            {
                _output.WriteLine("SKIPPED: no valid Skyrim SE environment. " + provider.EnvironmentBuilderError);
                return;
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
                UseSkyPatcherMode = skyPatcherMode,
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

            // Merge-in OFF and overrides ignored: this mod asks for nothing to be copied, so its
            // NPC's race link must survive the run untouched.
            var innocentSetting = new ModSetting
            {
                DisplayName = InnocentMod,
                CorrespondingModKeys = new List<ModKey> { innocentKey },
                CorrespondingFolderPaths = new List<string> { innocentFolder },
                MergeInDependencyRecords = false,
                IncludeOutfits = false,
                CopyAssets = false,
                ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
            };

            // The user-reported configuration: Include As New, which duplicates this mod's
            // overrides of records it does not own — putting a VANILLA FormKey in the remap map.
            var mergingSetting = new ModSetting
            {
                DisplayName = MergingMod,
                CorrespondingModKeys = new List<ModKey> { mergingKey },
                CorrespondingFolderPaths = new List<string> { mergingFolder },
                MergeInDependencyRecords = true,
                IncludeOutfits = false,
                CopyAssets = false,
                ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.IncludeAsNew,
                IncludeAllOverrides = false,
            };

            settings.ModSettings = new List<ModSetting> { innocentSetting, mergingSetting };
            settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
            {
                [innocentNpc.FormKey] = (InnocentMod, innocentNpc.FormKey),
                [mergingNpcA.FormKey] = (MergingMod, mergingNpcA.FormKey),
                [mergingNpcB.FormKey] = (MergingMod, mergingNpcB.FormKey),
            };

            Directory.CreateDirectory(outDir);
            var run = await GoldenPatchRunner.RunAsync(provider, settings);
            _output.WriteLine("--- RUN LOG ---\n" + run.Log + "\n--- END RUN LOG ---");

            var outPlugin = Path.Combine(outDir, "NPC.esp");
            File.Exists(outPlugin).Should().BeTrue("the patcher must write an output plugin");

            using var outHandle = SkyrimMod.CreateFromBinaryOverlay(outPlugin, SkyrimRelease.SkyrimSE);
            ISkyrimModGetter outMod = outHandle;

            _output.WriteLine("output records: " + string.Join(", ",
                outMod.EnumerateMajorRecords().Select(r => $"{r.Registration.Name} {r.FormKey} '{r.EditorID}'")));
            _output.WriteLine("output NPC races: " + string.Join(", ",
                outMod.Npcs.Select(n => $"{n.EditorID}->{n.Race.FormKey}")));

            // The merging mod really did duplicate the race into the output — without this the test
            // would pass vacuously, having never created the mapping that used to leak.
            outMod.Races.Should().NotBeEmpty(
                "Include As New must duplicate the merging mod's race override, or this fixture " +
                "never reproduces the condition");

            var innocentOut = outMod.Npcs.SingleOrDefault(n =>
                n.EditorID != null && n.EditorID.StartsWith("NPC2Scope_Innocent", StringComparison.Ordinal));
            innocentOut.Should().NotBeNull("the innocent mod's NPC must be in the output");

            innocentOut!.Race.FormKey.Should().Be(sharedRace.FormKey,
                "'{0}' merges nothing, so '{1}' duplicating that race in a LATER batch must not " +
                "re-point this NPC at the duplicate", InnocentMod, MergingMod);
            innocentOut.Race.FormKey.ModKey.Should().NotBe(outMod.ModKey);

            // The other half of the contract: within the merging mod's OWN batch the remap must
            // still happen, including for the NPC processed after the duplication. Narrowing the
            // remap to the roots of the current call would break exactly this and leave the mod's
            // own NPCs pointing at a record that is no longer in the output.
            var mergingOut = outMod.Npcs.Where(n =>
                n.EditorID != null && n.EditorID.StartsWith("NPC2Scope_Merging", StringComparison.Ordinal)).ToList();
            mergingOut.Should().HaveCount(2);
            mergingOut.Select(n => n.Race.FormKey.ModKey).Should().AllBeEquivalentTo(outMod.ModKey,
                "'{0}' duplicated that race for itself, so its own NPCs must use the duplicate", MergingMod);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* overlay may hold a map */ }
        }
    }

    private static void WriteMod(SkyrimMod mod, string path, ModKey[] writeOrder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        mod.BeginWrite.ToPath(path).WithLoadOrder(writeOrder).WithNoDataFolder().Write();
    }

    private static void WriteFaceGen(string modFolder, FormKey npcKey)
    {
        var (meshRel, texRel) = Auxilliary.GetFaceGenSubPathStrings(npcKey, regularized: true);
        WriteText(Path.Combine(modFolder, meshRel), "npc2-remapscope-nif");
        WriteText(Path.Combine(modFolder, texRel), "npc2-remapscope-dds");
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
