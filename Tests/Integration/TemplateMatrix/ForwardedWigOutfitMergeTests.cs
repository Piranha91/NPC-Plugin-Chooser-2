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
/// WigHandlingMode.ForwardToOutfit on a SKIN-CARRIED wig whose ArmorAddon lives in a resource
/// plugin outside the load order (the High Poly NPC Overhaul shape). The forwarder mints a
/// wearable wig ARMO wrapping that ARMA and puts it in a duplicated outfit; the ARMA itself has
/// to be merged into the output or the plugin cannot be written at all — Mutagen refuses with
/// "A referenced mod was not present on the load order being sorted against".
///
/// <para>Run against BOTH output modes from one fixture, because the mint happens the same way
/// in each but the record the merge walker starts from does not: an NPC override in record mode,
/// a freshly-created surrogate in SkyPatcher mode.</para>
///
/// <para>Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class ForwardedWigOutfitMergeTests
{
    private readonly ITestOutputHelper _output;

    public ForwardedWigOutfitMergeTests(ITestOutputHelper output) => _output = output;

    private static readonly ModKey[] BaseMasters =
        Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();

    private static bool IsVanillaOrCc(ModKey key)
    {
        if (BaseMasters.Contains(key)) return true;
        var fn = key.FileName.String;
        return fn.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
               || fn.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
    }

    private const string ModName = "Wig Outfit Fixture Mod";
    private const string EidNpc = "NPC2Wig_Subject";

    /// <summary>ArmorLeatherSimpleOutfit — a real vanilla outfit, so the forwarder takes its
    /// duplicate-an-existing-outfit path rather than authoring a bare wig-only one.</summary>
    private static readonly FormKey VanillaOutfit = FormKey.Factory("057A34:Skyrim.esm");

    [Theory]
    [InlineData(false)] // record mode (CreateAndPatch)
    [InlineData(true)]  // SkyPatcher mode
    public async Task ForwardedSkinWig_MergesItsArmorAddon_SoThePluginCanBeWritten(bool skyPatcherMode)
    {
        var root = Path.Combine(Path.GetTempPath(), "NpcWigMerge_" + Guid.NewGuid().ToString("N"));
        var modFolder = Path.Combine(root, "Mod");
        var outDir = Path.Combine(root, "Output");
        var envOutDir = Path.Combine(root, "EnvOutput");

        try
        {
            var baseKey = ModKey.FromFileName("NPC2WigBase.esp");
            var resKey = ModKey.FromFileName("NPC2WigRes.esp");
            var modKey = ModKey.FromFileName("NPC2WigMod.esp");
            var writeOrder = BaseMasters.Concat(new[] { baseKey, resKey, modKey }).ToArray();

            // --- base: the NPC, plain, wearing a vanilla outfit -------------------------------
            var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
            var hp = baseMod.HeadParts.AddNew();
            hp.EditorID = "NPC2Wig_HP";
            hp.Type = HeadPart.TypeEnum.Face;

            var npc = baseMod.Npcs.AddNew();
            npc.EditorID = EidNpc;
            npc.Name = EidNpc;
            npc.Race.SetTo(Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Race.NordRace);
            npc.HeadParts.Add(hp.FormKey);
            npc.DefaultOutfit.SetTo(VanillaOutfit);
            var npcKey = npc.FormKey;

            var basePath = Path.Combine(root, "Base", baseKey.FileName);
            WriteMod(baseMod, basePath, writeOrder);

            // --- resource plugin (never enabled): the wig ARMA + the skin ARMO carrying it ----
            var resMod = new SkyrimMod(resKey, SkyrimRelease.SkyrimSE);
            var wigArma = resMod.ArmorAddons.AddNew();
            wigArma.EditorID = "NPC2Wig_WigAA";
            wigArma.Race.SetTo(Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Race.DefaultRace);
            wigArma.BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = BipedObjectFlag.Hair,
                ArmorType = ArmorType.Clothing,
            };

            var skin = resMod.Armors.AddNew();
            skin.EditorID = "NPC2Wig_Skin";
            skin.Armature.Add(wigArma.FormKey.ToLink<IArmorAddonGetter>());
            skin.Race.SetTo(Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Race.DefaultRace);
            skin.BodyTemplate = new BodyTemplate { ArmorType = ArmorType.Clothing };

            var resPath = Path.Combine(modFolder, resKey.FileName);
            WriteMod(resMod, resPath, writeOrder);

            // --- appearance mod: points the NPC's WornArmor at that skin ----------------------
            var appearanceMod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            var modNpc = appearanceMod.Npcs.GetOrAddAsOverride(npc);
            modNpc.WornArmor.SetTo(skin.FormKey);

            var modPath = Path.Combine(modFolder, modKey.FileName);
            WriteMod(appearanceMod, modPath, writeOrder);

            var (meshRel, texRel) = Auxilliary.GetFaceGenSubPathStrings(npcKey, regularized: true);
            WriteText(Path.Combine(modFolder, meshRel), "npc2-wigmerge-nif");
            WriteText(Path.Combine(modFolder, texRel), "npc2-wigmerge-dds");

            // --- environment: base only; the mod and its resources stay out of the load order --
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
                DefaultWigHandlingMode = WigHandlingMode.ForwardToOutfit,
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

            var modSetting = new ModSetting
            {
                DisplayName = ModName,
                CorrespondingModKeys = new List<ModKey> { resKey, modKey },
                ResourceOnlyModKeys = new HashSet<ModKey> { resKey },
                CorrespondingFolderPaths = new List<string> { modFolder },
                MergeInDependencyRecords = true,
                IncludeOutfits = false,
                CopyAssets = false,
                ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
            };
            modSetting.DetectedWigArmatures.Add(wigArma.FormKey);
            settings.ModSettings = new List<ModSetting> { modSetting };
            settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
            {
                [npcKey] = (ModName, npcKey),
            };

            settings.ModHasWigs(modSetting).Should().BeTrue("the fixture must actually trip wig detection");
            settings.GetEffectiveWigMode(modSetting).Should().Be(WigHandlingMode.ForwardToOutfit);

            Directory.CreateDirectory(outDir);
            var run = await GoldenPatchRunner.RunAsync(provider, settings);
            _output.WriteLine("--- RUN LOG ---\n" + run.Log + "\n--- END RUN LOG ---");

            // --- the decisive check -------------------------------------------------------------
            run.Log.Should().NotContain("FATAL SAVE ERROR",
                "a dangling reference into the unloaded resource plugin makes the plugin unwritable");

            var outPlugin = Path.Combine(outDir, "NPC.esp");
            File.Exists(outPlugin).Should().BeTrue("the patcher must write an output plugin");

            using var outHandle = SkyrimMod.CreateFromBinaryOverlay(outPlugin, SkyrimRelease.SkyrimSE);
            ISkyrimModGetter outMod = outHandle;

            _output.WriteLine("output records: " + string.Join(", ",
                outMod.EnumerateMajorRecords().Select(r => $"{r.Registration.Name} {r.FormKey} '{r.EditorID}'")));

            outMod.ModHeader.MasterReferences.Select(m => m.Master).Should().NotContain(resKey,
                "every reference into the resource plugin must have been merged into the output");

            var mintedWig = outMod.Armors.SingleOrDefault(a =>
                a.EditorID != null && a.EditorID.StartsWith("NPC2WigArmor_", StringComparison.Ordinal));
            mintedWig.Should().NotBeNull("ForwardToOutfit mints a wearable ARMO for the skin-carried wig");
            mintedWig!.Armature.Should().ContainSingle()
                .Which.FormKey.ModKey.Should().Be(outMod.ModKey,
                    "the minted ARMO's armature must point at the merged copy, not the unloaded plugin");
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

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
