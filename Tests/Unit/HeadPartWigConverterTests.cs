using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Wig→HeadPart conversion (<see cref="WigHandlingMode.ConvertToHeadParts"/> /
/// <see cref="HeadPartWigConverter"/>) against in-memory mods, using the same
/// link-cache seeding seam as <see cref="WigForwarderTests"/>. The NIF-touching
/// probes (render shape enumeration, dismember-partition check, physics-XML
/// discovery) are stubbed through the converter's internal provider seams; the
/// mod's loose files are dummy files in a per-test temp folder so path
/// resolution (weight variants, facegen presence) runs for real. The actual
/// bake and the engine-proven record shape against the real FoxGlove specimen
/// are covered by <see cref="NifHandlerWigBakeTests"/> /
/// <see cref="WigHeadPartSpikeGeneratorTests"/>.
/// </summary>
public class HeadPartWigConverterTests : IDisposable
{
    private static readonly ModKey DonorKey = ModKey.FromNameAndExtension("FoxGloveAuri.esp");
    private static readonly FormKey ValidRacesFlst = FormKey.Factory("0A803F:Skyrim.esm");

    private const string WigNifRecordPath = @"actors\TestWig\wig_1.nif";
    private static readonly string[] WigShapes = { "01b", "01a", "Hl" };

    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { /* best effort */ }
        }
    }

    private sealed class Fixture
    {
        public SkyrimMod DonorMod = null!;
        public SkyrimMod OutputMod = null!;
        public Settings Settings = null!;
        public RecordHandler RecordHandler = null!;
        public HeadPartWigConverter Converter = null!;
        public WigForwarder Forwarder = null!;
        public Npc DonorNpc = null!;
        public Armor WigArmor = null!;
        public ArmorAddon WigArma = null!;
        public Armor DressArmor = null!;
        public Outfit DonorOutfit = null!;
        public ModSetting ModSetting = null!;
        public HeadPart HairHeadPart = null!;
        public HeadPart HairlinePart = null!;
        public HeadPart EyesHeadPart = null!;
        public string ModFolder = null!;
    }

    private Fixture Make(
        bool donorHasHair = true,
        bool createWigNif = true,
        bool createFaceGen = true,
        float donorWeight = 100f,
        bool secondWigInOutfit = false,
        WigHandlingMode? modWigMode = WigHandlingMode.ConvertToHeadParts)
    {
        var f = new Fixture
        {
            DonorMod = new SkyrimMod(DonorKey, SkyrimRelease.SkyrimSE),
            Settings = new Settings { PatchingMode = PatchingMode.CreateAndPatch },
        };

        f.WigArma = f.DonorMod.ArmorAddons.AddNew();
        f.WigArma.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Hair };
        f.WigArma.WorldModel = new GenderedItem<Model?>(
            new Model { File = WigNifRecordPath }, new Model { File = WigNifRecordPath });
        f.WigArmor = f.DonorMod.Armors.AddNew();
        f.WigArmor.Name = "FoxGlove Red Wig";
        f.WigArmor.EditorID = "FoxGlove_Wig";
        f.WigArmor.Armature.Add(f.WigArma.ToLink());

        f.DressArmor = f.DonorMod.Armors.AddNew();
        f.DressArmor.EditorID = "AuriDress";

        f.DonorOutfit = f.DonorMod.Outfits.AddNew();
        f.DonorOutfit.EditorID = "AuriOutfit";
        f.DonorOutfit.Items = new Noggog.ExtendedList<IFormLinkGetter<IOutfitTargetGetter>>
        {
            f.WigArmor.FormKey.ToLink<IOutfitTargetGetter>(),
            f.DressArmor.FormKey.ToLink<IOutfitTargetGetter>(),
        };
        if (secondWigInOutfit)
        {
            var wig2 = f.DonorMod.Armors.AddNew();
            wig2.Name = "Second Hair Wig";
            wig2.EditorID = "FoxGlove_Wig2";
            wig2.Armature.Add(f.WigArma.ToLink());
            f.DonorOutfit.Items.Add(wig2.FormKey.ToLink<IOutfitTargetGetter>());
        }

        f.HairlinePart = f.DonorMod.HeadParts.AddNew();
        f.HairlinePart.EditorID = "FoxGloveHairlineMesh";
        f.HairlinePart.Type = HeadPart.TypeEnum.Misc;
        f.HairHeadPart = f.DonorMod.HeadParts.AddNew();
        f.HairHeadPart.EditorID = "FoxGloveHairMesh";
        f.HairHeadPart.Type = HeadPart.TypeEnum.Hair;
        f.HairHeadPart.ExtraParts.Add(f.HairlinePart.ToLink());
        f.EyesHeadPart = f.DonorMod.HeadParts.AddNew();
        f.EyesHeadPart.EditorID = "FoxGloveEyeMesh";
        f.EyesHeadPart.Type = HeadPart.TypeEnum.Eyes;

        f.DonorNpc = MutagenFixtures.NewNpc(f.DonorMod, editorId: "Auri");
        f.DonorNpc.Weight = donorWeight;
        f.DonorNpc.DefaultOutfit.SetTo(f.DonorOutfit);
        if (donorHasHair) f.DonorNpc.HeadParts.Add(f.HairHeadPart.ToLink());
        f.DonorNpc.HeadParts.Add(f.EyesHeadPart.ToLink());

        var env = (EnvironmentStateProvider)RuntimeHelpers.GetUninitializedObject(typeof(EnvironmentStateProvider));
        f.OutputMod = new SkyrimMod(ModKey.FromNameAndExtension("NPC.esp"), SkyrimRelease.SkyrimSE);
        env.OutputMod = f.OutputMod;

        var pluginProvider = new PluginProvider(env, f.Settings);
        f.RecordHandler = new RecordHandler(env, pluginProvider, f.Settings);
        Reflect.GetField<ConcurrentDictionary<ModKey, ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>>>(
                f.RecordHandler, "_modLinkCaches")[DonorKey] =
            new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(f.DonorMod, new LinkCachePreferences());
        Reflect.GetField<ConcurrentDictionary<ModKey, ISkyrimModGetter>>(
            f.RecordHandler, "_modLinkCachePlugins")[DonorKey] = f.DonorMod;
        Reflect.GetField<ConcurrentDictionary<ModKey, string>>(
            f.RecordHandler, "_modLinkCacheSourcePaths")[DonorKey] = @"c:\mods\foxglove\foxgloveauri.esp";

        // Mod folder with the loose dummy files the converter's path resolution
        // touches (the NIF-parsing itself is stubbed below).
        f.ModFolder = Path.Combine(Path.GetTempPath(), "NPC2_WigConvertTests", Guid.NewGuid().ToString("N"));
        _tempDirs.Add(f.ModFolder);
        Directory.CreateDirectory(f.ModFolder);
        if (createWigNif)
        {
            WriteDummy(Path.Combine(f.ModFolder, @"meshes\actors\TestWig\wig_1.nif"));
            WriteDummy(Path.Combine(f.ModFolder, @"meshes\actors\TestWig\wig_0.nif"));
            WriteDummy(Path.Combine(f.ModFolder, @"meshes\actors\TestWig\wig.xml"));
        }
        if (createFaceGen)
        {
            var (fgRel, _) = Auxilliary.GetFaceGenSubPathStrings(f.DonorNpc.FormKey, regularized: true);
            WriteDummy(Path.Combine(f.ModFolder, fgRel));
        }

        var bsaHandler = new BsaHandler(env);
        f.Converter = new HeadPartWigConverter(env, f.RecordHandler, bsaHandler)
        {
            RenderShapeNamesProvider = _ => WigShapes,
            PartitionProbe = (_, _) => true,
            PhysicsXmlProvider = _ => new[] { "wig.xml" },
        };

        var outfitDisplayResolver = new OutfitDisplayResolver(f.Settings, env, f.RecordHandler);
        f.Forwarder = new WigForwarder(env, f.RecordHandler, f.Settings, outfitDisplayResolver);

        f.ModSetting = new ModSetting
        {
            DisplayName = "FoxGlove",
            CorrespondingModKeys = { DonorKey },
            CorrespondingFolderPaths = { f.ModFolder },
            DetectedWigArmors = { f.WigArmor.FormKey },
            ModWigHandlingMode = modWigMode,
        };
        if (secondWigInOutfit)
        {
            f.ModSetting.DetectedWigArmors.UnionWith(
                f.DonorMod.Armors.Where(a => a.EditorID == "FoxGlove_Wig2").Select(a => a.FormKey));
        }
        return f;
    }

    private static void WriteDummy(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "dummy");
    }

    private static HeadPartWigConverter.Result? Apply(Fixture f, out bool fallback) =>
        f.Converter.Apply(f.DonorNpc, f.ModSetting, new HashSet<string>(), "TestNpc",
            (_, _, _) => { }, out fallback);

    // ---- Persisted enum stability ------------------------------------------------------------

    [Fact]
    public void WigHandlingMode_PersistedIntegerValues_DoNotShift()
    {
        // Settings.json serializes these as integers; ConvertToHeadParts was
        // appended AFTER None so existing configs keep their meaning.
        ((int)WigHandlingMode.ForwardToSkin).Should().Be(0);
        ((int)WigHandlingMode.ForwardToOutfit).Should().Be(1);
        ((int)WigHandlingMode.None).Should().Be(2);
        ((int)WigHandlingMode.ConvertToHeadParts).Should().Be(3);
    }

    // ---- Record minting ----------------------------------------------------------------------

    [Fact]
    public void Apply_MintsEngineProvenRecordStructure()
    {
        var f = Make();
        var result = Apply(f, out bool fallback);

        fallback.Should().BeFalse();
        result.Should().NotBeNull();
        result!.MintedRecords.Should().HaveCount(WigShapes.Length);
        f.OutputMod.HeadParts.Should().HaveCount(WigShapes.Length);

        var parent = f.OutputMod.HeadParts.Single(h => h.FormKey == result.ParentHeadPartKey);
        parent.EditorID.Should().Be("NPC2Wig_FoxGlove_Wig_01b",
            "shape 0 is the parent and EDID must equal the future baked shape name");
        parent.Type.Should().Be(HeadPart.TypeEnum.Hair);
        parent.Flags.Should().Be(HeadPart.Flag.Male | HeadPart.Flag.Female);
        parent.Flags.Should().NotHaveFlag(HeadPart.Flag.Playable);
        parent.ValidRaces.FormKey.Should().Be(ValidRacesFlst);
        parent.Model.Should().NotBeNull();
        parent.Model!.File.GivenPath.Should().Be(WigNifRecordPath);
        parent.ExtraParts.Select(l => l.FormKey).Should().BeEquivalentTo(
            f.OutputMod.HeadParts.Where(h => h.FormKey != parent.FormKey).Select(h => h.FormKey));

        foreach (var extra in f.OutputMod.HeadParts.Where(h => h.FormKey != parent.FormKey))
        {
            extra.Type.Should().Be(HeadPart.TypeEnum.Misc);
            extra.Flags.Should().HaveFlag(HeadPart.Flag.IsExtraPart);
            extra.Model.Should().NotBeNull(
                "every part must be geometry-bearing or the engine orphans its baked shape (dark face)");
            extra.Model!.File.GivenPath.Should().Be(WigNifRecordPath);
            extra.ValidRaces.FormKey.Should().Be(ValidRacesFlst);
        }

        // EDID == baked shape name for every part, via the rename map.
        result.ShapeRenames.Keys.Should().BeEquivalentTo(WigShapes);
        result.ShapeRenames.Values.Should().BeEquivalentTo(
            f.OutputMod.HeadParts.Select(h => h.EditorID));

        // Hair removal collected: donor hair key + its own and ExtraParts' EDIDs.
        result.DonorHairHeadPartKeys.Should().BeEquivalentTo(new[] { f.HairHeadPart.FormKey });
        result.FaceGenShapeNamesToStrip.Should().BeEquivalentTo(
            new[] { "FoxGloveHairMesh", "FoxGloveHairlineMesh" });

        // Physics XML: rewritten copy goes to the NPC2-owned path.
        result.PhysicsXmlSourcePath.Should().NotBeNull();
        result.PhysicsXmlNewDataRelPath.Should().Be(@"meshes\NPC2\WigPhysics\FoxGlove_Wig.xml");
    }

    [Fact]
    public void Apply_ReusesMintedSetAcrossNpcsSharingTheWig()
    {
        var f = Make();
        var npc2 = MutagenFixtures.NewNpc(f.DonorMod, editorId: "Auri2");
        npc2.Weight = 100f;
        npc2.DefaultOutfit.SetTo(f.DonorOutfit);
        npc2.HeadParts.Add(f.HairHeadPart.ToLink());
        var (fgRel2, _) = Auxilliary.GetFaceGenSubPathStrings(npc2.FormKey, regularized: true);
        WriteDummy(Path.Combine(f.ModFolder, fgRel2));

        var r1 = Apply(f, out _);
        var r2 = f.Converter.Apply(npc2, f.ModSetting, new HashSet<string>(), "TestNpc2",
            (_, _, _) => { }, out bool fallback2);

        fallback2.Should().BeFalse();
        r2.Should().NotBeNull();
        r2!.ParentHeadPartKey.Should().Be(r1!.ParentHeadPartKey,
            "NPCs sharing a wig share one HDPT set and identical baked names");
        f.OutputMod.HeadParts.Should().HaveCount(WigShapes.Length, "no second set may be minted");
    }

    [Fact]
    public void Apply_WeightVariants_PickNearestNoInterpolation()
    {
        var fHigh = Make(donorWeight: 100f);
        var rHigh = Apply(fHigh, out _);
        rHigh!.WigNifSourcePath.Should().EndWith("wig_1.nif");

        var fLow = Make(donorWeight: 0f);
        var rLow = Apply(fLow, out _);
        rLow!.WigNifSourcePath.Should().EndWith("wig_0.nif");
    }

    [Fact]
    public void SwapWeightSuffix_HandlesBothDirectionsAndSuffixlessPaths()
    {
        HeadPartWigConverter.SwapWeightSuffix(@"a\wig_1.nif", wantHighWeight: false).Should().Be(@"a\wig_0.nif");
        HeadPartWigConverter.SwapWeightSuffix(@"a\wig_0.nif", wantHighWeight: true).Should().Be(@"a\wig_1.nif");
        HeadPartWigConverter.SwapWeightSuffix(@"a\wig_1.nif", wantHighWeight: true).Should().Be(@"a\wig_1.nif");
        HeadPartWigConverter.SwapWeightSuffix(@"a\wig.nif", wantHighWeight: false).Should().Be(@"a\wig.nif");
    }

    // ---- FinalizeNpcRecord -------------------------------------------------------------------

    [Fact]
    public void FinalizeNpcRecord_ReplacesHairWithMintedParent_NoBaldBackFill()
    {
        var f = Make();
        var result = Apply(f, out _)!;

        // Record path: patchNpc is an override whose head parts were copied from
        // the donor (CopyAppearanceData ran before FinalizeNpcRecord).
        var patchNpc = f.OutputMod.Npcs.GetOrAddAsOverride(f.DonorNpc);
        patchNpc.HeadParts.Select(h => h.FormKey).Should().Contain(f.HairHeadPart.FormKey);

        f.Converter.FinalizeNpcRecord(result, patchNpc, "TestNpc", (_, _, _) => { });

        patchNpc.HeadParts.Select(h => h.FormKey).Should().NotContain(f.HairHeadPart.FormKey);
        patchNpc.HeadParts.Select(h => h.FormKey).Should().Contain(result.ParentHeadPartKey);
        patchNpc.HeadParts.Select(h => h.FormKey).Should().Contain(f.EyesHeadPart.FormKey,
            "non-hair head parts stay untouched");
        f.OutputMod.HeadParts.Should().NotContain(h => h.EditorID == WigForwarder.BaldHairEditorId,
            "the wig parent IS the Hair part — no NPC2_HairBald back-fill");
    }

    [Fact]
    public void FinalizeNpcRecord_OnSkyPatcherSurrogate_ReplacesHair()
    {
        var f = Make();
        var result = Apply(f, out _)!;

        // SkyPatcher path: the surrogate is a NEW NPC record in the output mod
        // carrying the donor's head-part links (not an override of the donor).
        var surrogate = f.OutputMod.Npcs.AddNew();
        surrogate.EditorID = "NPC2_Surrogate_Auri";
        surrogate.HeadParts.Add(f.HairHeadPart.ToLink());
        surrogate.HeadParts.Add(f.EyesHeadPart.ToLink());

        f.Converter.FinalizeNpcRecord(result, surrogate, "TestNpc", (_, _, _) => { });

        surrogate.HeadParts.Select(h => h.FormKey).Should().NotContain(f.HairHeadPart.FormKey);
        surrogate.HeadParts.Select(h => h.FormKey).Should().Contain(result.ParentHeadPartKey);
        surrogate.HeadParts.Select(h => h.FormKey).Should().Contain(f.EyesHeadPart.FormKey);
    }

    [Fact]
    public void FinalizeNpcRecord_ExpandsDuplicateMappings()
    {
        var f = Make();
        var result = Apply(f, out _)!;

        // Simulate CopyAppearanceData's merge remap: the patched NPC references
        // an output-side duplicate of the donor hair, not the donor key.
        var mergedHair = f.OutputMod.HeadParts.AddNew();
        mergedHair.EditorID = "FoxGloveHairMesh";
        mergedHair.Type = HeadPart.TypeEnum.Hair;
        f.RecordHandler.SeedDuplicateMapping(f.HairHeadPart.FormKey, mergedHair.FormKey);

        var patchNpc = f.OutputMod.Npcs.AddNew();
        patchNpc.HeadParts.Add(mergedHair.ToLink());

        f.Converter.FinalizeNpcRecord(result, patchNpc, "TestNpc", (_, _, _) => { });

        patchNpc.HeadParts.Select(h => h.FormKey).Should().NotContain(mergedHair.FormKey,
            "hair links remapped by the merge must be removed via the duplicate mapping");
        patchNpc.HeadParts.Select(h => h.FormKey).Should().Contain(result.ParentHeadPartKey);
    }

    // ---- Per-NPC fallback --------------------------------------------------------------------

    [Fact]
    public void Apply_BaldDonor_FallsBackToForwardToSkin()
    {
        var f = Make(donorHasHair: false);
        var result = Apply(f, out bool fallback);

        result.Should().BeNull();
        fallback.Should().BeTrue("no donor hair means no dismember-partition template for the bake");
        f.OutputMod.HeadParts.Should().BeEmpty("nothing may be minted for a declined NPC");
    }

    [Fact]
    public void Apply_DonorFaceGenWithoutPartitions_FallsBack()
    {
        var f = Make();
        f.Converter.PartitionProbe = (_, _) => false;
        var result = Apply(f, out bool fallback);

        result.Should().BeNull();
        fallback.Should().BeTrue();
        f.OutputMod.HeadParts.Should().BeEmpty();
    }

    [Fact]
    public void Apply_MissingDonorFaceGen_FallsBack()
    {
        var f = Make(createFaceGen: false);
        var result = Apply(f, out bool fallback);

        result.Should().BeNull();
        fallback.Should().BeTrue();
    }

    [Fact]
    public void Apply_MissingWigNif_FallsBack()
    {
        var f = Make(createWigNif: false);
        var result = Apply(f, out bool fallback);

        result.Should().BeNull();
        fallback.Should().BeTrue();
    }

    [Fact]
    public void Apply_WigNifWithoutRenderShapes_FallsBack()
    {
        var f = Make();
        f.Converter.RenderShapeNamesProvider = _ => Array.Empty<string>();
        var result = Apply(f, out bool fallback);

        result.Should().BeNull();
        fallback.Should().BeTrue();
    }

    [Fact]
    public void Apply_MultipleWigsInOutfit_FallsBack()
    {
        var f = Make(secondWigInOutfit: true);
        var result = Apply(f, out bool fallback);

        result.Should().BeNull();
        fallback.Should().BeTrue("only a single wig can become the NPC's Hair head part");
    }

    [Fact]
    public void Apply_NoWigInDonorOutfit_ReturnsNullWithoutFallback()
    {
        var f = Make();
        f.DonorOutfit.Items!.RemoveAll(i => i.FormKey == f.WigArmor.FormKey);
        var result = Apply(f, out bool fallback);

        result.Should().BeNull();
        fallback.Should().BeFalse("no wig at all is not a conversion failure");
    }

    // ---- WigForwarder interplay --------------------------------------------------------------

    [Fact]
    public void ForwarderInConvertMode_StripsWigFromForwardedOutfit_NoSkinDup_NoHairRemoval()
    {
        var f = Make();
        var result = f.Forwarder.Apply(f.DonorNpc.FormKey, f.DonorNpc, f.ModSetting, DonorKey,
            new HashSet<string>(), mergeInDependencyRecords: false, includeOutfit: true,
            "TestNpc", (_, _, _) => { });

        result.Should().NotBeNull("the wig must be stripped from the forwarded outfit");
        result!.SkinDuplicateKey.Should().BeNull("ConvertToHeadParts does no skin forwarding");
        result.DonorHairHeadPartKeys.Should().BeEmpty("hair removal is the converter's, with no bald back-fill");
        result.FaceGenShapeNamesToStrip.Should().BeEmpty();
        result.OutfitDuplicateKey.Should().NotBeNull();

        var dup = f.OutputMod.Outfits.Single(o => o.FormKey == result.OutfitDuplicateKey);
        dup.Items!.Select(i => i.FormKey).Should().NotContain(f.WigArmor.FormKey,
            "the armor wig must not be equipped on top of the baked one");
        dup.Items!.Select(i => i.FormKey).Should().Contain(f.DressArmor.FormKey,
            "the rest of the outfit is preserved");
    }

    [Fact]
    public void ForwarderInConvertMode_IncludeOutfitOff_DoesNothing()
    {
        var f = Make();
        var result = f.Forwarder.Apply(f.DonorNpc.FormKey, f.DonorNpc, f.ModSetting, DonorKey,
            new HashSet<string>(), mergeInDependencyRecords: false, includeOutfit: false,
            "TestNpc", (_, _, _) => { });

        result.Should().BeNull("no outfit is forwarded, so there is nothing to strip and no other wig work");
    }

    [Fact]
    public void ForwarderWigModeOverride_ForwardToSkin_RunsProvenFallbackFlow()
    {
        // Settings say ConvertToHeadParts, but the converter declined this NPC —
        // the Patcher passes the override and the full ForwardToSkin flow runs
        // (WNAM duplicate + hair removal + bald back-fill in FinalizeNpcRecord).
        var f = Make();
        var skinArma = f.DonorMod.ArmorAddons.AddNew();
        skinArma.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body };
        var skin = f.DonorMod.Armors.AddNew();
        skin.EditorID = "AuriSkin";
        skin.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body };
        skin.Armature.Add(skinArma.ToLink());
        f.DonorNpc.WornArmor.SetTo(skin);

        var result = f.Forwarder.Apply(f.DonorNpc.FormKey, f.DonorNpc, f.ModSetting, DonorKey,
            new HashSet<string>(), mergeInDependencyRecords: false, includeOutfit: false,
            "TestNpc", (_, _, _) => { }, wigModeOverride: WigHandlingMode.ForwardToSkin);

        result.Should().NotBeNull();
        result!.SkinDuplicateKey.Should().NotBeNull("the override must run the full ForwardToSkin flow");
        result.DonorHairHeadPartKeys.Should().BeEquivalentTo(new[] { f.HairHeadPart.FormKey });

        var dup = f.OutputMod.Armors.Single(a => a.FormKey == result.SkinDuplicateKey);
        dup.Armature.Select(a => a.FormKey).Should().Contain(f.WigArma.FormKey,
            "the wig ARMA transfers into the WNAM duplicate");
    }

    // ---- EditorID sanitation -----------------------------------------------------------------

    [Fact]
    public void SanitizeForEditorId_MatchesSpikeRule()
    {
        HeadPartWigConverter.SanitizeForEditorId("01b").Should().Be("01b");
        HeadPartWigConverter.SanitizeForEditorId("s4studio mesh-3").Should().Be("s4studio_mesh_3");
        HeadPartWigConverter.SanitizeForEditorId("FoxGlove_Wig").Should().Be("FoxGlove_Wig");
        HeadPartWigConverter.SanitizeForEditorId(null).Should().BeNull();
    }
}
