using System.IO;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// Create-and-Patch with override handling <b>Include</b>: how the appearance mod's overrides of
/// records it does not own reach the output. Both faults pinned here were found by the in-game
/// bench on 2026-11-25 (<c>NPC2-bench-report-2026-11-25.md</c>, D1 and D6).
///
/// <para><b>D6, the plugin could not be written.</b> To patch an override the patcher needs the
/// record's ORIGINAL. It looked for the original's plugin in the appearance mod's own folders and
/// then in the game Data folder. An original that lives in another mod's folder (a follower's
/// outfit, defined by the follower's own plugin) was found only when the app ran through a mod
/// manager, whose virtual Data folder shows every enabled mod. Anywhere else the override was
/// copied whole, and a whole copy was never handed to merge-in: the new records it pointed at
/// stayed in the appearance plugin, which is not in the load order, so Mutagen refused to write the
/// output. The base plugin of this fixture sits in a folder that is neither the appearance mod's
/// nor the Data folder, which is that situation.</para>
///
/// <para><b>D1, the game never loaded.</b> A property-level patch copies the load order's winning
/// record and applies the mod's differences to it. The form version was applied as if it were
/// content, while the model's texture-hash block (whose layout follows the form version) stayed
/// the winner's: old-layout data under a new stamp.</para>
///
/// <para>Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class IncludeOverridePatchingTests
{
    private readonly ITestOutputHelper _output;
    public IncludeOverridePatchingTests(ITestOutputHelper output) => _output = output;

    private static readonly ModKey[] BaseMasters = Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();
    private static readonly ModKey BaseKey = ModKey.FromFileName("NPC2Inc.esp");
    private static readonly ModKey LookKey = ModKey.FromFileName("NPC2IncLook.esp");
    private const string LookMod = "Include Fixture Look";

    // 12-byte texture hashes. Form version 39 stores them bare; 44 puts a 12-byte header in front.
    private static readonly byte[] OldLayoutModelData = Enumerable.Range(1, 24).Select(i => (byte)i).ToArray();
    private static readonly byte[] NewLayoutModelData =
        new byte[] { 2, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0 }.Concat(OldLayoutModelData).ToArray();

    private static bool IsVanillaOrCc(ModKey key)
    {
        if (BaseMasters.Contains(key)) return true;
        var fn = key.FileName.String;
        return fn.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
               || fn.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture
    {
        public required FormKey Npc, Outfit, BaseBoots, Armature, TextureSet, Skin;
        public required FormKey NewHat, NewGloves;
        public FormKey? InjectedOutfit;
    }

    /// <summary>What the fixture's appearance plugin does to the records it does not own.</summary>
    [Flags]
    private enum Look
    {
        None = 0,
        /// <summary>Adds two NEW armors to the base outfit.</summary>
        OutfitGainsNewItems = 1,
        /// <summary>Re-saves the armature at form version 44 (new-layout model data) and widens its slots.</summary>
        ArmatureResavedAndEdited = 2,
        /// <summary>Carries a copy of the texture set that changes nothing.</summary>
        TextureSetIdenticalCopy = 4,
        /// <summary>Carries an outfit under a FormKey of the BASE plugin that the base plugin does not
        /// define (an injected record), holding a new armor; the NPC override points at it.</summary>
        InjectedOutfit = 8,
    }

    private async Task<(ISkyrimModGetter? Out, Fixture Fx, string Log, IDisposable? Handle)> Run(string root, Look look)
    {
        var lookFolder = Path.Combine(root, "LookMod");
        var outDir = Path.Combine(root, "Output");
        var envOutDir = Path.Combine(root, "EnvOutput");
        var writeOrder = BaseMasters.Concat(new[] { BaseKey, LookKey }).ToArray();

        // --- base: everything the NPC wears and is, in a plugin of its own -----------------------
        var baseMod = new SkyrimMod(BaseKey, SkyrimRelease.SkyrimSE);
        var race = baseMod.Races.AddNew();
        race.EditorID = "NPC2Inc_Race";
        race.Flags |= Race.Flag.FaceGenHead; // or screening drops every NPC on it

        var headPart = baseMod.HeadParts.AddNew();
        headPart.EditorID = "NPC2Inc_HP";
        headPart.Type = HeadPart.TypeEnum.Face;

        var textureSet = baseMod.TextureSets.AddNew();
        textureSet.EditorID = "NPC2Inc_TXST";
        textureSet.Diffuse = "textures/npc2inc/body.dds";

        var armature = baseMod.ArmorAddons.AddNew();
        armature.EditorID = "NPC2Inc_AA";
        armature.Race.SetTo(race);
        armature.FormVersion = 39;
        armature.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.Clothing };
        armature.WorldModel = new GenderedItem<Model?>(
            new Model { File = "meshes/npc2inc/body_m.nif", Data = OldLayoutModelData },
            new Model { File = "meshes/npc2inc/body_f.nif", Data = OldLayoutModelData });
        armature.SkinTexture = new GenderedItem<IFormLinkNullableGetter<ITextureSetGetter>>(
            textureSet.ToNullableLink(), textureSet.ToNullableLink());

        var skin = baseMod.Armors.AddNew();
        skin.EditorID = "NPC2Inc_Skin";
        skin.Race.SetTo(race);
        skin.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.Clothing };
        skin.Armature.Add(armature.FormKey.ToLink<IArmorAddonGetter>());

        var boots = baseMod.Armors.AddNew();
        boots.EditorID = "NPC2Inc_Boots";
        boots.Race.SetTo(race);
        boots.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Feet, ArmorType = ArmorType.Clothing };

        var outfit = baseMod.Outfits.AddNew();
        outfit.EditorID = "NPC2Inc_Outfit";
        outfit.Items = new Noggog.ExtendedList<IFormLinkGetter<IOutfitTargetGetter>> { boots.FormKey.ToLink<IOutfitTargetGetter>() };

        var npc = baseMod.Npcs.AddNew();
        npc.EditorID = "NPC2Inc_Npc";
        npc.Name = "NPC2Inc_Npc";
        npc.Race.SetTo(race.FormKey);
        npc.HeadParts.Add(headPart.FormKey);
        npc.WornArmor.SetTo(skin.FormKey);
        npc.DefaultOutfit.SetTo(outfit.FormKey);

        var basePath = Path.Combine(root, "Base", BaseKey.FileName);
        WriteMod(baseMod, basePath, writeOrder);

        // --- the appearance plugin: never in the load order --------------------------------------
        var lookMod = new SkyrimMod(LookKey, SkyrimRelease.SkyrimSE);
        var npcOverride = lookMod.Npcs.GetOrAddAsOverride(npc);
        npcOverride.Height = 1.05f;

        Armor NewArmor(string edid, BipedObjectFlag slot)
        {
            var a = lookMod.Armors.AddNew();
            a.EditorID = edid;
            a.Race.SetTo(race);
            a.BodyTemplate = new BodyTemplate { FirstPersonFlags = slot, ArmorType = ArmorType.Clothing };
            return a;
        }
        var hat = NewArmor("NPC2Inc_NewHat", BipedObjectFlag.Circlet);
        var gloves = NewArmor("NPC2Inc_NewGloves", BipedObjectFlag.Hands);

        if (look.HasFlag(Look.OutfitGainsNewItems))
        {
            var o = lookMod.Outfits.GetOrAddAsOverride(outfit);
            o.Items!.Add(hat.FormKey.ToLink<IOutfitTargetGetter>());
            o.Items.Add(gloves.FormKey.ToLink<IOutfitTargetGetter>());
        }
        if (look.HasFlag(Look.ArmatureResavedAndEdited))
        {
            var a = lookMod.ArmorAddons.GetOrAddAsOverride(armature);
            a.FormVersion = 44;
            a.BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = BipedObjectFlag.Body | BipedObjectFlag.Hands, ArmorType = ArmorType.Clothing, ActsLike44 = true,
            };
            a.WorldModel = new GenderedItem<Model?>(
                new Model { File = "meshes/npc2inc/body_m.nif", Data = NewLayoutModelData },
                new Model { File = "meshes/npc2inc/body_f.nif", Data = NewLayoutModelData });
        }
        if (look.HasFlag(Look.TextureSetIdenticalCopy))
        {
            lookMod.TextureSets.GetOrAddAsOverride(textureSet);
            // Reached from the NPC only through a record the mod carries, so the armature is carried too.
            lookMod.ArmorAddons.GetOrAddAsOverride(armature);
        }
        FormKey? injectedKey = null;
        if (look.HasFlag(Look.InjectedOutfit))
        {
            injectedKey = new FormKey(BaseKey, 0x0ABCDE);
            var injected = new Outfit(injectedKey.Value, SkyrimRelease.SkyrimSE)
            {
                EditorID = "NPC2Inc_InjectedOutfit",
                Items = new Noggog.ExtendedList<IFormLinkGetter<IOutfitTargetGetter>> { hat.FormKey.ToLink<IOutfitTargetGetter>() },
            };
            lookMod.Outfits.Add(injected);
            npcOverride.DefaultOutfit.SetTo(injectedKey.Value);
        }
        WriteMod(lookMod, Path.Combine(lookFolder, LookKey.FileName), writeOrder);
        WriteFaceGen(lookFolder, npc.FormKey);

        var fx = new Fixture
        {
            Npc = npc.FormKey, Outfit = outfit.FormKey, BaseBoots = boots.FormKey, Armature = armature.FormKey,
            TextureSet = textureSet.FormKey, Skin = skin.FormKey, NewHat = hat.FormKey, NewGloves = gloves.FormKey,
            InjectedOutfit = injectedKey,
        };

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
            return (null, fx, string.Empty, null);
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
            DefaultRecordOverrideHandlingMode = RecordOverrideHandlingMode.Include,
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
                // Off: the outfit FIELD is not written (screening rightly rejects writing a link to
                // an injected record while Injected Record Handling is off). The override walk still
                // roots at the donor's outfit link, which is what these tests are about.
                IncludeOutfits = false,
                CopyAssets = false,
                ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.Include,
                IncludeAllOverrides = false,
            },
        };
        settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)> { [npc.FormKey] = (LookMod, npc.FormKey) };

        Directory.CreateDirectory(outDir);
        var run = await GoldenPatchRunner.RunAsync(provider, settings);
        _output.WriteLine("--- RUN LOG ---\n" + run.Log + "\n--- END RUN LOG ---");

        var outPlugin = Path.Combine(outDir, "NPC.esp");
        if (!File.Exists(outPlugin)) return (null, fx, run.Log, null);
        var handle = SkyrimMod.CreateFromBinaryOverlay(outPlugin, SkyrimRelease.SkyrimSE);
        _output.WriteLine("output records: " + string.Join(", ",
            ((ISkyrimModGetter)handle).EnumerateMajorRecords().Select(r => $"{r.Registration.Name} {r.FormKey} '{r.EditorID}'")));
        return (handle, fx, run.Log, handle);
    }

    private static bool Skipped((ISkyrimModGetter? Out, Fixture Fx, string Log, IDisposable? Handle) r) =>
        r.Out == null && r.Log.Length == 0;

    // ------------------------------------------------------------------ N2 (D6)

    [Fact]
    public async Task OverrideWhoseOriginalLivesInAnotherModsFolder_IsPatched_AndItsNewRecordsAreMergedIn()
    {
        var root = NewRoot();
        try
        {
            var r = await Run(root, Look.OutfitGainsNewItems);
            if (Skipped(r)) return;
            using var _ = r.Handle;

            r.Log.Should().NotContain("FATAL SAVE ERROR");
            r.Out.Should().NotBeNull("the output plugin must be written");
            var masters = r.Out!.ModHeader.MasterReferences.Select(m => m.Master).ToList();
            masters.Should().NotContain(LookKey, "the appearance plugin is not in the load order");
            masters.Should().Contain(BaseKey);

            var outfit = r.Out.Outfits.SingleOrDefault(o => o.FormKey == r.Fx.Outfit);
            outfit.Should().NotBeNull("the mod's outfit edit must reach the output as an override of the base outfit");
            var items = outfit!.Items!.Select(i => i.FormKey).ToList();
            items.Should().HaveCount(3);
            items.Should().Contain(r.Fx.BaseBoots);
            items.Where(k => k != r.Fx.BaseBoots).Select(k => k.ModKey).Should().AllBeEquivalentTo(r.Out.ModKey,
                "the two armors the mod adds must be copies in the output, not links into the appearance plugin");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task InjectedRecord_HasNoOriginal_IsCopiedWhole_AndItsNewRecordsAreMergedIn()
    {
        var root = NewRoot();
        try
        {
            var r = await Run(root, Look.InjectedOutfit);
            if (Skipped(r)) return;
            using var _ = r.Handle;

            r.Log.Should().NotContain("FATAL SAVE ERROR");
            r.Out.Should().NotBeNull("the output plugin must be written");
            r.Out!.ModHeader.MasterReferences.Select(m => m.Master).Should().NotContain(LookKey);

            var injected = r.Out.Outfits.SingleOrDefault(o => o.FormKey == r.Fx.InjectedOutfit);
            injected.Should().NotBeNull("an injected record is copied whole: the base plugin holds nothing to patch");
            injected!.Items!.Select(i => i.FormKey.ModKey).Should().AllBeEquivalentTo(r.Out.ModKey,
                "a whole-copied record's new records must be merged in like a patched record's");
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ N1 (D1)

    [Fact]
    public async Task PatchedOverride_KeepsTheWinnersFormVersionAndModelData_AndTakesTheModsEdit()
    {
        var root = NewRoot();
        try
        {
            var r = await Run(root, Look.ArmatureResavedAndEdited);
            if (Skipped(r)) return;
            using var _ = r.Handle;

            r.Out.Should().NotBeNull("the output plugin must be written");
            var armature = r.Out!.ArmorAddons.SingleOrDefault(a => a.FormKey == r.Fx.Armature);
            armature.Should().NotBeNull("the mod really edits the armature's slots, so an override belongs in the output");
            armature!.BodyTemplate!.FirstPersonFlags.Should().Be(BipedObjectFlag.Body | BipedObjectFlag.Hands, "the mod's edit");

            armature.FormVersion.Should().Be((ushort)39,
                "the form version says how the record's bytes are laid out; it is the winner's, like the bytes");
            foreach (var model in new[] { armature.WorldModel!.Male, armature.WorldModel.Female })
            {
                model!.Data!.Value.ToArray().Should().Equal(OldLayoutModelData,
                    "model data is laid out by form version and must never be mixed across versions");
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task OverrideThatChangesNothing_WritesNoOverride()
    {
        var root = NewRoot();
        try
        {
            var r = await Run(root, Look.TextureSetIdenticalCopy);
            if (Skipped(r)) return;
            using var _ = r.Handle;

            r.Out.Should().NotBeNull("the output plugin must be written");
            r.Out!.TextureSets.Select(t => t.FormKey).Should().NotContain(r.Fx.TextureSet,
                "the mod's copy says nothing the original does not, so the load order's winner stands");
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ helpers

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "NpcIncludePatch_" + Guid.NewGuid().ToString("N"));

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
            File.WriteAllText(path, "npc2-include-fixture");
        }
    }
}
