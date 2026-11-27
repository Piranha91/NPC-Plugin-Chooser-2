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
/// <b>Include As New</b> makes PRIVATE COPIES of races that exist in the load order: a race the
/// appearance mod overrides, and a race it does not touch but that links to something it does (a
/// bridge parent; High Poly NPC Overhaul's hair-colour edits make every playable race one). The NPC
/// is pointed at the copy. Found by the in-game bench on 2026-11-25 (report D2):
///
/// <para><b>Floating heads.</b> The copy had no armor race, so the game matched armatures against
/// the copy itself. Every vanilla body and clothing armature names the ORIGINAL race: nothing
/// matched, and the NPC drew as a head with no body and no outfit.</para>
///
/// <para><b>Bald, once that is fixed.</b> Giving the copy the original as its armor race makes the
/// game match armatures against the original INSTEAD of the copy (measured in game 2026-11-24: an
/// armor race replaces the actor's own race, it does not add to it). But the same remap that
/// pointed the NPC at the copy also rewrote the race lists of every armature brought along (the
/// mod's wig armatures): they name the copy, and would stop matching. Under Recast Only the NPC
/// even keeps its original race in the game, so such an armature never matched there.</para>
///
/// <para>Hence two rules, pinned here: a private copy of a load-order race names as its armor race
/// what its original would be matched against (the original's own armor race, else the original);
/// and no armature in the output is left naming such a copy.</para>
///
/// <para>Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class IncludeAsNewRaceCopyTests
{
    private readonly ITestOutputHelper _output;
    public IncludeAsNewRaceCopyTests(ITestOutputHelper output) => _output = output;

    private static readonly ModKey[] BaseMasters = Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();
    private static readonly ModKey BaseKey = ModKey.FromFileName("NPC2Ian.esp");
    private static readonly ModKey LookKey = ModKey.FromFileName("NPC2IanLook.esp");
    private const string LookMod = "Include As New Race Fixture";

    private static bool IsVanillaOrCc(ModKey key)
    {
        if (BaseMasters.Contains(key)) return true;
        var fn = key.FileName.String;
        return fn.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
               || fn.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrivateRaceCopies_KeepTheOriginalsArmorRace_AndNoArmatureNamesACopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "NpcIanRace_" + Guid.NewGuid().ToString("N"));
        var lookFolder = Path.Combine(root, "LookMod");
        var outDir = Path.Combine(root, "Output");
        var envOutDir = Path.Combine(root, "EnvOutput");
        var writeOrder = BaseMasters.Concat(new[] { BaseKey, LookKey }).ToArray();

        try
        {
            // --- base: three races, each of which Include As New will copy -----------------------
            var baseMod = new SkyrimMod(BaseKey, SkyrimRelease.SkyrimSE);

            Race AddRace(string edid)
            {
                var r = baseMod.Races.AddNew();
                r.EditorID = edid;
                r.Flags |= Race.Flag.FaceGenHead; // or screening drops every NPC on it
                return r;
            }

            // Overridden by the mod (the mod edits the race itself).
            var overridden = AddRace("NPC2Ian_RaceOverridden");
            // Its "vampire" variant: like the vanilla ones, it wears what the plain race wears.
            var variant = AddRace("NPC2Ian_RaceVariant");
            variant.ArmorRace.SetTo(overridden);
            // Not touched by the mod, but its skin is: a bridge parent.
            var bridged = AddRace("NPC2Ian_RaceBridged");
            var raceSkin = baseMod.Armors.AddNew();
            raceSkin.EditorID = "NPC2Ian_RaceSkin";
            raceSkin.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.Clothing };
            bridged.Skin.SetTo(raceSkin);
            // A race nothing copies: the armature's main race.
            var untouched = AddRace("NPC2Ian_RaceUntouched");

            Npc AddNpc(string edid, Race race)
            {
                var hp = baseMod.HeadParts.AddNew();
                hp.EditorID = edid + "_HP";
                hp.Type = HeadPart.TypeEnum.Face;
                var n = baseMod.Npcs.AddNew();
                n.EditorID = edid;
                n.Name = edid;
                n.Race.SetTo(race.FormKey);
                n.HeadParts.Add(hp.FormKey);
                return n;
            }
            var npcs = new[]
            {
                (Npc: AddNpc("NPC2Ian_NpcOverridden", overridden), Race: overridden),
                (Npc: AddNpc("NPC2Ian_NpcVariant", variant), Race: variant),
                (Npc: AddNpc("NPC2Ian_NpcBridged", bridged), Race: bridged),
            };

            var basePath = Path.Combine(root, "Base", BaseKey.FileName);
            WriteMod(baseMod, basePath, writeOrder);

            // --- the appearance plugin: never in the load order ----------------------------------
            var lookMod = new SkyrimMod(LookKey, SkyrimRelease.SkyrimSE);
            lookMod.Races.GetOrAddAsOverride(overridden).Description = "edited";
            lookMod.Races.GetOrAddAsOverride(variant).Description = "edited";
            lookMod.Armors.GetOrAddAsOverride(raceSkin).Name = "edited";

            // The mod's own wig-like armature, for all three races, on a skin of its own.
            var wigArma = lookMod.ArmorAddons.AddNew();
            wigArma.EditorID = "NPC2Ian_WigAA";
            wigArma.Race.SetTo(untouched);
            wigArma.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Hair, ArmorType = ArmorType.Clothing };
            foreach (var r in new[] { overridden, variant, bridged })
                wigArma.AdditionalRaces.Add(r.FormKey.ToLink<IRaceGetter>());
            var lookSkin = lookMod.Armors.AddNew();
            lookSkin.EditorID = "NPC2Ian_LookSkin";
            lookSkin.Race.SetTo(untouched);
            lookSkin.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Hair, ArmorType = ArmorType.Clothing };
            lookSkin.Armature.Add(wigArma.FormKey.ToLink<IArmorAddonGetter>());

            foreach (var (npc, _) in npcs)
            {
                var o = lookMod.Npcs.GetOrAddAsOverride(npc);
                o.Height = 1.05f;
                o.WornArmor.SetTo(lookSkin.FormKey);
                WriteFaceGen(lookFolder, npc.FormKey);
            }
            WriteMod(lookMod, Path.Combine(lookFolder, LookKey.FileName), writeOrder);

            // --- environment ----------------------------------------------------------------------
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
                OutputBackend = OutputBackend.ESP,
                DefaultWigHandlingMode = WigHandlingMode.None,
                DefaultAntlerHandlingMode = AntlerHandlingMode.None,
                TemplateHandlingMode = TemplateHandlingMode.InheritFromTemplate,
                DefaultRecordOverrideHandlingMode = RecordOverrideHandlingMode.IncludeAsNew,
                DefaultMaxNestedIntervalDepth = 2,
                DefaultIncludeAllOverrides = false,
                PublishForwardedOutfitsToDistributors = false,
                LocalizationLanguage = null,
                BatFilePreCommands = string.Empty,
                BatFilePostCommands = string.Empty,
            };
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
                    ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.IncludeAsNew,
                    IncludeAllOverrides = false,
                },
            };
            settings.SelectedAppearanceMods = npcs.ToDictionary(n => n.Npc.FormKey, n => (LookMod, n.Npc.FormKey));

            Directory.CreateDirectory(outDir);
            var run = await GoldenPatchRunner.RunAsync(provider, settings);
            _output.WriteLine("--- RUN LOG ---\n" + run.Log + "\n--- END RUN LOG ---");

            var outPlugin = Path.Combine(outDir, "NPC.esp");
            File.Exists(outPlugin).Should().BeTrue("the patcher must write an output plugin");
            using var handle = SkyrimMod.CreateFromBinaryOverlay(outPlugin, SkyrimRelease.SkyrimSE);
            ISkyrimModGetter outMod = handle;
            _output.WriteLine("output records: " + string.Join(", ",
                outMod.EnumerateMajorRecords().Select(r => $"{r.Registration.Name} {r.FormKey} '{r.EditorID}'")));

            IRaceGetter CopyOf(Race original)
            {
                var copy = outMod.Races.SingleOrDefault(r =>
                    r.FormKey.ModKey == outMod.ModKey && r.EditorID != null &&
                    r.EditorID.StartsWith(original.EditorID!, StringComparison.Ordinal));
                copy.Should().NotBeNull("Include As New must copy {0}, or this fixture reproduces nothing", original.EditorID);
                return copy!;
            }
            var copyOverridden = CopyOf(overridden);
            var copyVariant = CopyOf(variant);
            var copyBridged = CopyOf(bridged);

            // The fixture's premise: each NPC really is pointed at its race's private copy.
            foreach (var (npc, race) in npcs)
            {
                outMod.Npcs.Single(n => n.FormKey == npc.FormKey).Race.FormKey.Should().Be(CopyOf(race).FormKey,
                    "{0} is expected on the private copy of {1}", npc.EditorID, race.EditorID);
            }

            // Rule 1: a copy is matched against what its original was matched against.
            copyOverridden.ArmorRace.FormKey.Should().Be(overridden.FormKey,
                "a copy of a race with no armor race wears what that race wears");
            copyBridged.ArmorRace.FormKey.Should().Be(bridged.FormKey,
                "a bridge-parent copy is a private copy like any other");
            copyVariant.ArmorRace.FormKey.Should().Be(overridden.FormKey,
                "a copy keeps its original's armor race, and never names another private copy");

            // Rule 2: no armature is left naming a copy.
            var wig = outMod.ArmorAddons.Single(a => a.EditorID == "NPC2Ian_WigAA");
            var named = wig.AdditionalRaces.Select(r => r.FormKey).Append(wig.Race.FormKey).ToList();
            named.Select(k => k.ModKey).Should().NotContain(outMod.ModKey,
                "the game matches an armature against the armor race, so a private copy in its list matches nobody");
            named.Should().Contain(new[] { overridden.FormKey, variant.FormKey, bridged.FormKey, untouched.FormKey });
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
        foreach (var rel in new[] { meshRel, texRel })
        {
            var path = Path.Combine(modFolder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "npc2-ian-fixture");
        }
    }
}
