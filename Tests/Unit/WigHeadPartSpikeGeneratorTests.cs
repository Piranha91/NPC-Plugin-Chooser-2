using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Wig→HeadPart feasibility spike, checkpoint B setup: generates the complete
/// MO2-ready test package for the FoxGlove Auri wig — a plugin with the minted
/// HeadPart records (1 parent Hair + 12 IsExtraPart extras), an Auri NPC
/// override whose hair is replaced by the wig parent, a wig-free duplicate of
/// her outfit, the baked FaceGen NIF, the rewritten SMP physics XML, and the
/// wig NIF at its original rel path (the parent HeadPart's Model target).
/// The user installs the emitted folder in MO2 (its FaceGen loose file must
/// win over the FoxGlove mod's), inspects the plugin in SSEEdit, and runs the
/// in-game checklist (dark face / SMP sway / collision shapes / expressions).
///
/// Machine-local: gracefully skips when the FoxGlove or Song of the Green
/// specimens aren't installed. Output goes to a staging folder NEXT TO the
/// MO2 mods directory (never into it) and is regenerated on each run; source
/// mods are never modified.
/// </summary>
public class WigHeadPartSpikeGeneratorTests
{
    private const string FoxGloveModRoot =
        @"S:\Skyrim NPC Selection\mods\FoxGlove - Auri Visual Overhaul - The FoxGlove - Classic Red - No Warpaint - Test";

    private const string FoxGloveEsp = FoxGloveModRoot + @"\FoxGloveAuri.esp";

    private const string AuriEsp =
        @"S:\Skyrim NPC Selection\mods\Song of the Green (Auri Follower)\018Auri.esp";

    private const string StagingModDir =
        @"S:\Skyrim NPC Selection\NPC2 WigSpike Staging\NPC2 WigSpike";

    private const string SpikePluginName = "NPC2_WigSpike.esp";

    private const string WigNifRelPath = @"actors\FoxGlove Auri\Wig";
    private const string FaceGenRelDir = @"actors\character\FaceGenData\FaceGeom\018auri.esp";
    private const string NewPhysicsXmlRelPath = @"meshes\NPC2\WigPhysics\FoxGlove_Wig.xml";

    private const string RenamePrefix = "NPC2Wig_FoxGlove_Wig_";

    /// <summary>Same ValidRaces list the bald-hair mint uses (High Poly NPC
    /// Overhaul's HeadPartsAllRacesMinusBeast FLST).</summary>
    private static readonly FormKey HeadPartsAllRacesMinusBeastKey = FormKey.Factory("0A803F:Skyrim.esm");

    private static bool SpecimensMissing =>
        !File.Exists(FoxGloveEsp) || !File.Exists(AuriEsp) ||
        !File.Exists(Path.Combine(FoxGloveModRoot, "meshes", FaceGenRelDir, "00000D63.NIF"));

    private const string RoundtripStagingDir =
        @"S:\Skyrim NPC Selection\NPC2 WigSpike Staging\NPC2 WigSpike A - Roundtrip";

    private const string SingleShapeStagingDir =
        @"S:\Skyrim NPC Selection\NPC2 WigSpike Staging\NPC2 WigSpike B - SingleShape";

    /// <summary>The single wig sub-shape used for the bisect variant — the
    /// main back mass, the most visible piece.</summary>
    private const string BisectShape = "01b";

    /// <summary>
    /// Diagnostic control A: the FoxGlove facegen passed through a pure nifly
    /// load→save round-trip with NO edits and NO plugin. In game (enabled
    /// INSTEAD of the other spike variants, winning the facegen conflict over
    /// the FoxGlove Test variant) Auri must look completely vanilla-FoxGlove.
    /// If this alone dark-faces, the engine rejects nifly's serialization
    /// itself and no amount of shape/record work can succeed.
    /// </summary>
    [Fact]
    public void GenerateRoundtripControlVariant()
    {
        if (SpecimensMissing) return;

        if (Directory.Exists(RoundtripStagingDir)) Directory.Delete(RoundtripStagingDir, true);
        string faceGenDest = Path.Combine(RoundtripStagingDir, "meshes", FaceGenRelDir, "00000D63.NIF");
        Directory.CreateDirectory(Path.GetDirectoryName(faceGenDest)!);
        File.Copy(Path.Combine(FoxGloveModRoot, "meshes", FaceGenRelDir, "00000D63.NIF"), faceGenDest);

        using var nif = new nifly.NifFile();
        nif.Load(faceGenDest).Should().Be(0);
        nif.Save(faceGenDest).Should().Be(0);

        using var reload = new nifly.NifFile();
        reload.Load(faceGenDest).Should().Be(0, "the round-tripped facegen must stay loadable");
    }

    /// <summary>
    /// Diagnostic control B: ONE wig sub-shape baked as the NPC's sole Hair
    /// head part — no extra parts at all. The smallest possible version of the
    /// conversion. If this works in game (partial hair, no dark face), scale
    /// up shape count; if it dark-faces, the defect is in the single-shape
    /// pipeline itself.
    /// </summary>
    [Fact]
    public async Task GenerateSingleShapeVariant()
    {
        if (SpecimensMissing) return;

        if (Directory.Exists(SingleShapeStagingDir)) Directory.Delete(SingleShapeStagingDir, true);
        string meshesDir = Path.Combine(SingleShapeStagingDir, "meshes");
        Directory.CreateDirectory(meshesDir);

        using var foxGlove = SkyrimMod.CreateFromBinaryOverlay(FoxGloveEsp, SkyrimRelease.SkyrimSE);
        using var auri = SkyrimMod.CreateFromBinaryOverlay(AuriEsp, SkyrimRelease.SkyrimSE);
        var donorNpc = foxGlove.Npcs.First();

        string wigVariant = donorNpc.Weight >= 50f ? "22a_1.nif" : "22a_0.nif";
        string wigNifSource = Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, wigVariant);

        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BisectShape] = RenamePrefix + BisectShape,
        };

        string physicsXmlSource = Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, "22a.xml");
        SmpXmlRewriter.RewriteShapeNames(physicsXmlSource,
            Path.Combine(SingleShapeStagingDir, NewPhysicsXmlRelPath), renames);

        string faceGenDest = Path.Combine(meshesDir, FaceGenRelDir, "00000D63.NIF");
        Directory.CreateDirectory(Path.GetDirectoryName(faceGenDest)!);
        File.Copy(Path.Combine(FoxGloveModRoot, "meshes", FaceGenRelDir, "00000D63.NIF"), faceGenDest);

        var bakeLog = new List<string>();
        int baked = NifHandler.BakeWigIntoFaceGen(new NifHandler.WigBakeInstruction(
                faceGenDest, wigNifSource, renames,
                new[] { "FoxGloveHairMesh", "FoxGloveHairlineMesh", "FoxGloveHairScalp" },
                NewPhysicsXmlRelPath,
                OnlyRenderShapes: new[] { BisectShape }),
            bakeLog.Add);
        baked.Should().Be(1, string.Join(Environment.NewLine, bakeLog));

        string wigNifDest = Path.Combine(meshesDir, WigNifRelPath, wigVariant);
        Directory.CreateDirectory(Path.GetDirectoryName(wigNifDest)!);
        File.Copy(wigNifSource, wigNifDest);

        var spike = new SkyrimMod(ModKey.FromNameAndExtension(SpikePluginName), SkyrimRelease.SkyrimSE);
        var hp = spike.HeadParts.AddNew();
        hp.EditorID = renames[BisectShape];
        hp.Type = HeadPart.TypeEnum.Hair;
        hp.Flags = HeadPart.Flag.Male | HeadPart.Flag.Female;
        hp.ValidRaces.SetTo(HeadPartsAllRacesMinusBeastKey);
        hp.Model = new Model { File = Path.Combine(WigNifRelPath, wigVariant) };

        var patchNpc = spike.Npcs.GetOrAddAsOverride(donorNpc);
        var headPartsByKey = foxGlove.HeadParts.Concat(auri.HeadParts)
            .GroupBy(h => h.FormKey).ToDictionary(g => g.Key, g => g.First());
        patchNpc.HeadParts.RemoveAll(l =>
            l != null && headPartsByKey.TryGetValue(l.FormKey, out var rec) &&
            rec.Type == HeadPart.TypeEnum.Hair).Should().BeGreaterThan(0);
        patchNpc.HeadParts.Add(hp.FormKey.ToLink<IHeadPartGetter>());

        var wigArmorKeys = foxGlove.Armors
            .Where(a => a.EditorID?.Contains("Wig", StringComparison.OrdinalIgnoreCase) == true)
            .Select(a => a.FormKey).ToHashSet();
        var donorOutfit = foxGlove.Outfits.Concat(auri.Outfits)
            .FirstOrDefault(o => o.FormKey == donorNpc.DefaultOutfit.FormKey);
        if (donorOutfit != null)
        {
            var outfitDup = spike.Outfits.AddNew();
            outfitDup.EditorID = "NPC2_WigSpike_Outfit";
            foreach (var item in donorOutfit.Items ?? Enumerable.Empty<IFormLinkGetter<IOutfitTargetGetter>>())
            {
                if (!wigArmorKeys.Contains(item.FormKey))
                {
                    outfitDup.Items ??= new();
                    outfitDup.Items.Add(item.FormKey.ToLink<IOutfitTargetGetter>());
                }
            }
            patchNpc.DefaultOutfit.SetTo(outfitDup.FormKey);
        }

        var loadOrderKeys = new List<ModKey>();
        foreach (var master in auri.ModHeader.MasterReferences.Select(m => m.Master)
                     .Concat(foxGlove.ModHeader.MasterReferences.Select(m => m.Master))
                     .Append(auri.ModKey).Append(foxGlove.ModKey))
        {
            if (!loadOrderKeys.Contains(master)) loadOrderKeys.Add(master);
        }
        await spike.BeginWrite.ToPath(Path.Combine(SingleShapeStagingDir, SpikePluginName))
            .WithLoadOrder(loadOrderKeys
                .Select(mk => (ISkyrimModGetter)new SkyrimMod(mk, SkyrimRelease.SkyrimSE)).ToArray())
            .WriteAsync();
    }

    private const string SynthPartitionStagingDir =
        @"S:\Skyrim NPC Selection\NPC2 WigSpike Staging\NPC2 WigSpike I - SynthPartition";

    /// <summary>
    /// Diagnostic I: the WNAM (skin-carried) wig pattern — a BALD donor facegen
    /// (hair shapes pre-stripped, so the bake has NO donor shape to harvest a
    /// partition template from) with one wig sub-shape baked as the sole Hair
    /// head part using the SYNTHESIZED SBP_131_HAIR template
    /// (<see cref="NifHandler.WigBakeInstruction"/>.SynthesizeHairPartitionIfNoDonor).
    /// Otherwise identical to variant B. In-game checklist: no dark face, tint
    /// correct, the partial hair renders, a HELMET HIDES the baked hair (the
    /// hair partition classification is exactly what helmets key on), SMP sway
    /// intact. If B is clean and I dark-faces or the helmet fails to hide the
    /// hair, the synthetic template values are wrong.
    /// </summary>
    [Fact]
    public async Task GenerateSynthesizedPartitionVariant()
    {
        if (SpecimensMissing) return;

        if (Directory.Exists(SynthPartitionStagingDir)) Directory.Delete(SynthPartitionStagingDir, true);
        string meshesDir = Path.Combine(SynthPartitionStagingDir, "meshes");
        Directory.CreateDirectory(meshesDir);

        using var foxGlove = SkyrimMod.CreateFromBinaryOverlay(FoxGloveEsp, SkyrimRelease.SkyrimSE);
        using var auri = SkyrimMod.CreateFromBinaryOverlay(AuriEsp, SkyrimRelease.SkyrimSE);
        var donorNpc = foxGlove.Npcs.First();

        string wigVariant = donorNpc.Weight >= 50f ? "22a_1.nif" : "22a_0.nif";
        string wigNifSource = Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, wigVariant);

        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BisectShape] = RenamePrefix + BisectShape,
        };

        SmpXmlRewriter.RewriteShapeNames(
            Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, "22a.xml"),
            Path.Combine(SynthPartitionStagingDir, NewPhysicsXmlRelPath), renames);

        string faceGenDest = Path.Combine(meshesDir, FaceGenRelDir, "00000D63.NIF");
        Directory.CreateDirectory(Path.GetDirectoryName(faceGenDest)!);
        File.Copy(Path.Combine(FoxGloveModRoot, "meshes", FaceGenRelDir, "00000D63.NIF"), faceGenDest);

        // Pre-strip the donor hair so the copied facegen matches the bald-donor
        // WNAM pattern before the bake ever sees it.
        var stripLog = new List<string>();
        NifHandler.RemoveShapesByName(faceGenDest,
                new[] { "FoxGloveHairMesh", "FoxGloveHairlineMesh", "FoxGloveHairScalp" }, stripLog.Add)
            .Should().Be(3, string.Join(Environment.NewLine, stripLog));

        var bakeLog = new List<string>();
        int baked = NifHandler.BakeWigIntoFaceGen(new NifHandler.WigBakeInstruction(
                faceGenDest, wigNifSource, renames,
                Array.Empty<string>(),
                NewPhysicsXmlRelPath,
                OnlyRenderShapes: new[] { BisectShape },
                SynthesizeHairPartitionIfNoDonor: true),
            bakeLog.Add);
        baked.Should().Be(1, string.Join(Environment.NewLine, bakeLog));
        bakeLog.Should().Contain(l => l.Contains("synthesized SBP_131_HAIR"),
            "the bald facegen must trigger the synthetic template, not a harvest");

        // Readback: the baked shape must carry the synthetic hair partition.
        using (var check = new nifly.NifFile())
        {
            check.Load(faceGenDest).Should().Be(0);
            nifly.NiShape? bakedShape = null;
            using var shapes = check.GetShapes();
            foreach (var shape in shapes)
            {
                if (renames[BisectShape].Equals(shape.name?.get(), StringComparison.OrdinalIgnoreCase))
                {
                    bakedShape = shape;
                    break;
                }
            }
            bakedShape.Should().NotBeNull("the renamed wig shape must exist in the baked facegen");
            using var partitions = new nifly.NiVectorBSDismemberSkinInstancePartitionInfo();
            using var triParts = new nifly.vectorint();
            check.GetShapePartitions(bakedShape, partitions, triParts)
                .Should().BeTrue("the baked shape must carry dismember partitions");
            using var items = partitions.items();
            items.Count.Should().BeGreaterThan(0);
            for (int pi = 0; pi < items.Count; pi++)
            {
                ((int)items[pi].partID).Should().Be(131, "every partition must be SBP_131_HAIR");
            }
            ((ushort)items[0].flags).Should().Be((ushort)0x0101,
                "first partition takes PF_EDITOR_VISIBLE | PF_START_NET_BONESET");
        }

        string wigNifDest = Path.Combine(meshesDir, WigNifRelPath, wigVariant);
        Directory.CreateDirectory(Path.GetDirectoryName(wigNifDest)!);
        File.Copy(wigNifSource, wigNifDest);

        var spike = new SkyrimMod(ModKey.FromNameAndExtension(SpikePluginName), SkyrimRelease.SkyrimSE);
        var hp = spike.HeadParts.AddNew();
        hp.EditorID = renames[BisectShape];
        hp.Type = HeadPart.TypeEnum.Hair;
        hp.Flags = HeadPart.Flag.Male | HeadPart.Flag.Female;
        hp.ValidRaces.SetTo(HeadPartsAllRacesMinusBeastKey);
        hp.Model = new Model { File = Path.Combine(WigNifRelPath, wigVariant) };

        var patchNpc = spike.Npcs.GetOrAddAsOverride(donorNpc);
        var headPartsByKey = foxGlove.HeadParts.Concat(auri.HeadParts)
            .GroupBy(h => h.FormKey).ToDictionary(g => g.Key, g => g.First());
        patchNpc.HeadParts.RemoveAll(l =>
            l != null && headPartsByKey.TryGetValue(l.FormKey, out var rec) &&
            rec.Type == HeadPart.TypeEnum.Hair).Should().BeGreaterThan(0);
        patchNpc.HeadParts.Add(hp.FormKey.ToLink<IHeadPartGetter>());

        var wigArmorKeys = foxGlove.Armors
            .Where(a => a.EditorID?.Contains("Wig", StringComparison.OrdinalIgnoreCase) == true)
            .Select(a => a.FormKey).ToHashSet();
        var donorOutfit = foxGlove.Outfits.Concat(auri.Outfits)
            .FirstOrDefault(o => o.FormKey == donorNpc.DefaultOutfit.FormKey);
        if (donorOutfit != null)
        {
            var outfitDup = spike.Outfits.AddNew();
            outfitDup.EditorID = "NPC2_WigSpike_Outfit";
            foreach (var item in donorOutfit.Items ?? Enumerable.Empty<IFormLinkGetter<IOutfitTargetGetter>>())
            {
                if (!wigArmorKeys.Contains(item.FormKey))
                {
                    outfitDup.Items ??= new();
                    outfitDup.Items.Add(item.FormKey.ToLink<IOutfitTargetGetter>());
                }
            }
            patchNpc.DefaultOutfit.SetTo(outfitDup.FormKey);
        }

        var loadOrderKeys = new List<ModKey>();
        foreach (var master in auri.ModHeader.MasterReferences.Select(m => m.Master)
                     .Concat(foxGlove.ModHeader.MasterReferences.Select(m => m.Master))
                     .Append(auri.ModKey).Append(foxGlove.ModKey))
        {
            if (!loadOrderKeys.Contains(master)) loadOrderKeys.Add(master);
        }
        await spike.BeginWrite.ToPath(Path.Combine(SynthPartitionStagingDir, SpikePluginName))
            .WithLoadOrder(loadOrderKeys
                .Select(mk => (ISkyrimModGetter)new SkyrimMod(mk, SkyrimRelease.SkyrimSE)).ToArray())
            .WriteAsync();
    }

    private const string WnamSpecimenStagingDir =
        @"S:\Skyrim NPC Selection\NPC2 WigSpike Staging\NPC2 WigSpike W - WnamSpecimen";

    private const string WnamSpecimenPluginName = "NPC2_WnamSpecimen.esp";

    private const string HpnoResourcesEsp =
        @"S:\Skyrim NPC Selection\mods\High Poly NPC Overhaul - Resources\High Poly NPC Overhaul - Resources.esp";

    /// <summary>
    /// WNAM-wig specimen: rebuilds the FoxGlove Auri donor as the skin-carried
    /// wig pattern (the High Poly NPC Overhaul shape) — a BALD FaceGen (hair
    /// shapes stripped), a modeless bald Hair head part replacing the donor
    /// hair, and the wig delivered as a hair-slot ArmorAddon inside a WNAM skin
    /// instead of an outfit ARMO. Install the emitted folder in MO2 and add it
    /// as an NPC2 appearance mod: the scan must detect the skin wig
    /// (DetectedWigArmatures), the "Set Wig Meshes" selector must list it, and
    /// a ConvertToHeadParts patch run must convert it end-to-end (synthesized
    /// SBP_131_HAIR partition — the bald-donor path) with the ARMA stripped
    /// from the output's WNAM duplicate. The specimen skin's body ArmorAddons
    /// are copied from a real HPNO wig skin (engine-proven vanilla naked-body
    /// links) rather than hardcoded FormKeys, so this fact also skips
    /// gracefully when HPNO Resources is absent.
    /// </summary>
    [Fact]
    public async Task GenerateWnamWigSpecimenModFolder()
    {
        if (SpecimensMissing || !File.Exists(HpnoResourcesEsp)) return;

        if (Directory.Exists(WnamSpecimenStagingDir)) Directory.Delete(WnamSpecimenStagingDir, true);
        string meshesDir = Path.Combine(WnamSpecimenStagingDir, "meshes");
        Directory.CreateDirectory(meshesDir);

        using var foxGlove = SkyrimMod.CreateFromBinaryOverlay(FoxGloveEsp, SkyrimRelease.SkyrimSE);
        using var auri = SkyrimMod.CreateFromBinaryOverlay(AuriEsp, SkyrimRelease.SkyrimSE);
        using var hpnoResources = SkyrimMod.CreateFromBinaryOverlay(HpnoResourcesEsp, SkyrimRelease.SkyrimSE);
        var donorNpc = foxGlove.Npcs.First();

        // Bald facegen: copy the donor FaceGen and strip its hair shapes —
        // exactly the state a WNAM-wig mod ships (nothing for the bake's
        // partition harvest; the converter must synthesize).
        string faceGenDest = Path.Combine(meshesDir, FaceGenRelDir, "00000D63.NIF");
        Directory.CreateDirectory(Path.GetDirectoryName(faceGenDest)!);
        File.Copy(Path.Combine(FoxGloveModRoot, "meshes", FaceGenRelDir, "00000D63.NIF"), faceGenDest);
        NifHandler.RemoveShapesByName(faceGenDest,
                new[] { "FoxGloveHairMesh", "FoxGloveHairlineMesh", "FoxGloveHairScalp" })
            .Should().Be(3);

        // Wig NIF + its SMP physics XML at their original rel paths.
        string wigVariant = donorNpc.Weight >= 50f ? "22a_1.nif" : "22a_0.nif";
        string wigRecordPath = Path.Combine(WigNifRelPath, wigVariant);
        string wigNifDest = Path.Combine(meshesDir, WigNifRelPath, wigVariant);
        Directory.CreateDirectory(Path.GetDirectoryName(wigNifDest)!);
        File.Copy(Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, wigVariant), wigNifDest);
        File.Copy(Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, "22a.xml"),
            Path.Combine(meshesDir, WigNifRelPath, "22a.xml"));

        // Vanilla naked-body ARMA links, copied from a real HPNO wig skin (the
        // non-HighPoly_WigAA_ armature entries) — engine-proven references.
        var hpnoWigSkin = hpnoResources.Armors.First(a =>
            a.Armature != null &&
            a.Armature.Any(l => l != null && !l.IsNull &&
                hpnoResources.ArmorAddons.Any(ad => ad.FormKey == l.FormKey &&
                    ad.EditorID?.StartsWith("HighPoly_WigAA_", StringComparison.OrdinalIgnoreCase) == true)));
        var bodyArmaLinks = hpnoWigSkin.Armature!
            .Where(l => l != null && !l.IsNull &&
                        !hpnoResources.ArmorAddons.Any(ad => ad.FormKey == l.FormKey &&
                            ad.EditorID?.StartsWith("HighPoly_WigAA_", StringComparison.OrdinalIgnoreCase) == true))
            .Select(l => l.FormKey)
            .ToList();
        bodyArmaLinks.Should().NotBeEmpty("the HPNO skin must carry vanilla body ARMAs to copy");

        var spike = new SkyrimMod(ModKey.FromNameAndExtension(WnamSpecimenPluginName), SkyrimRelease.SkyrimSE);

        // The wig as a skin-carried ARMA (hair slot, WorldModel = the wig NIF).
        var wigArma = spike.ArmorAddons.AddNew();
        wigArma.EditorID = "NPC2Spec_WigAA_Auri";
        wigArma.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Hair };
        wigArma.WorldModel = new GenderedItem<Model?>(
            new Model { File = wigRecordPath }, new Model { File = wigRecordPath });

        // The WNAM skin: vanilla body ARMAs + the wig ARMA, BOD2 widened to
        // cover the hair slot (the engine only dresses declared slots).
        var skin = spike.Armors.AddNew();
        skin.EditorID = "NPC2Spec_AuriSkin";
        skin.BodyTemplate = new BodyTemplate
        {
            FirstPersonFlags = BipedObjectFlag.Body | BipedObjectFlag.Hands |
                               BipedObjectFlag.Feet | BipedObjectFlag.Hair,
            ArmorType = ArmorType.Clothing,
        };
        foreach (var bodyKey in bodyArmaLinks) skin.Armature.Add(bodyKey.ToLink<IArmorAddonGetter>());
        skin.Armature.Add(wigArma.ToLink());

        // Modeless bald Hair head part (the HPNO HighPoly_HairBald pattern): an
        // NPC with NO Hair part back-fills a random race hair, so the donor
        // hair is REPLACED, not just removed.
        var bald = spike.HeadParts.AddNew();
        bald.EditorID = "NPC2Spec_HairBald";
        bald.Type = HeadPart.TypeEnum.Hair;
        bald.Flags = HeadPart.Flag.Male | HeadPart.Flag.Female;
        bald.ValidRaces.SetTo(HeadPartsAllRacesMinusBeastKey);

        var patchNpc = spike.Npcs.GetOrAddAsOverride(donorNpc);
        var headPartsByKey = foxGlove.HeadParts.Concat(auri.HeadParts)
            .GroupBy(h => h.FormKey).ToDictionary(g => g.Key, g => g.First());
        patchNpc.HeadParts.RemoveAll(l =>
            l != null && headPartsByKey.TryGetValue(l.FormKey, out var rec) &&
            rec.Type == HeadPart.TypeEnum.Hair).Should().BeGreaterThan(0);
        patchNpc.HeadParts.Add(bald.FormKey.ToLink<IHeadPartGetter>());
        patchNpc.WornArmor.SetTo(skin);

        // Outfit without the wig ARMO — the wig lives ONLY in the skin here.
        var wigArmorKeys = foxGlove.Armors
            .Where(a => a.EditorID?.Contains("Wig", StringComparison.OrdinalIgnoreCase) == true)
            .Select(a => a.FormKey).ToHashSet();
        var donorOutfit = foxGlove.Outfits.Concat(auri.Outfits)
            .FirstOrDefault(o => o.FormKey == donorNpc.DefaultOutfit.FormKey);
        if (donorOutfit != null)
        {
            var outfitDup = spike.Outfits.AddNew();
            outfitDup.EditorID = "NPC2Spec_Outfit";
            foreach (var item in donorOutfit.Items ?? Enumerable.Empty<IFormLinkGetter<IOutfitTargetGetter>>())
            {
                if (!wigArmorKeys.Contains(item.FormKey))
                {
                    outfitDup.Items ??= new();
                    outfitDup.Items.Add(item.FormKey.ToLink<IOutfitTargetGetter>());
                }
            }
            patchNpc.DefaultOutfit.SetTo(outfitDup.FormKey);
        }

        // Sanity: the production scan must classify the specimen as a WNAM wig
        // (both the NPC-scoped slot pass and the keyword net reach it).
        var scan = WigDetector.Scan(new ISkyrimModGetter[] { spike });
        scan.WigArmatures.Should().Contain(wigArma.FormKey,
            "the specimen exists to exercise DetectedWigArmatures end-to-end");
        scan.Wigs.Should().BeEmpty("no outfit wig ARMO may remain");

        var loadOrderKeys = new List<ModKey>();
        foreach (var master in auri.ModHeader.MasterReferences.Select(m => m.Master)
                     .Concat(foxGlove.ModHeader.MasterReferences.Select(m => m.Master))
                     .Concat(hpnoResources.ModHeader.MasterReferences.Select(m => m.Master))
                     .Append(auri.ModKey).Append(foxGlove.ModKey).Append(hpnoResources.ModKey))
        {
            if (!loadOrderKeys.Contains(master)) loadOrderKeys.Add(master);
        }
        await spike.BeginWrite.ToPath(Path.Combine(WnamSpecimenStagingDir, WnamSpecimenPluginName))
            .WithLoadOrder(loadOrderKeys
                .Select(mk => (ISkyrimModGetter)new SkyrimMod(mk, SkyrimRelease.SkyrimSE)).ToArray())
            .WriteAsync();
    }

    private const string SplitModelStagingDir =
        @"S:\Skyrim NPC Selection\NPC2 WigSpike Staging\NPC2 WigSpike D - SplitModel";

    private const string AdditiveStagingDir =
        @"S:\Skyrim NPC Selection\NPC2 WigSpike Staging\NPC2 WigSpike C - AdditiveShape";

    private const string SplitModelRelPath = @"NPC2\WigMeshes\FoxGlove_Wig_01b.nif";

    /// <summary>Extracts a single render shape (plus the bone tree and physics
    /// extra-data it needs) from the wig NIF into a standalone single-shape
    /// model NIF — the shape every working HDPT Model uses (KS SMP,
    /// ARMO_2_HDPT). Kept as source-format BSTriShape: the CK/engine convert
    /// on their own terms when they load a model NIF.</summary>
    private static void ExtractSingleShapeModelNif(string wigNifSource, string keepShape, string destPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var nif = new nifly.NifFile();
        nif.Load(wigNifSource).Should().Be(0);
        var doomed = new List<nifly.NiShape>();
        using (var shapes = nif.GetShapes())
        {
            foreach (var shape in shapes)
            {
                if (!keepShape.Equals(shape.name?.get(), StringComparison.OrdinalIgnoreCase))
                    doomed.Add(shape);
            }
        }
        foreach (var shape in doomed) nif.DeleteShape(shape);
        nif.Save(destPath).Should().Be(0);
    }

    /// <summary>
    /// Diagnostic D: identical to variant B (one sub-shape baked as the sole
    /// Hair head part) EXCEPT the HDPT Model points at a dedicated
    /// single-shape NIF instead of the whole 434-block SMP wig scene. The
    /// engine's dark-face flag is set inside BSFaceGenDB::GenerateHeadPartModel
    /// — head-part MODEL processing — and no working HDPT (KS SMP, vanilla,
    /// ARMO_2_HDPT output) ever models a multi-shape scene. If D is clean
    /// where B dark-faced, per-shape split model NIFs are a hard requirement.
    /// </summary>
    [Fact]
    public async Task GenerateSplitModelVariant()
    {
        if (SpecimensMissing) return;
        await GenerateSingleShapeVariantCore(SplitModelStagingDir, stripDonorHair: true);
    }

    /// <summary>
    /// Diagnostic C: the wig sub-shape baked ADDITIVELY — donor hair kept in
    /// both record and facegen, our HDPT (split model) appended as an extra
    /// head part. Distinguishes "replacing the hair breaks" from "adding any
    /// new baked part breaks" once D has answered the model-NIF question.
    /// </summary>
    [Fact]
    public async Task GenerateAdditiveShapeVariant()
    {
        if (SpecimensMissing) return;
        await GenerateSingleShapeVariantCore(AdditiveStagingDir, stripDonorHair: false);
    }

    private static async Task GenerateSingleShapeVariantCore(string stagingDir, bool stripDonorHair)
    {
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
        string meshesDir = Path.Combine(stagingDir, "meshes");
        Directory.CreateDirectory(meshesDir);

        using var foxGlove = SkyrimMod.CreateFromBinaryOverlay(FoxGloveEsp, SkyrimRelease.SkyrimSE);
        using var auri = SkyrimMod.CreateFromBinaryOverlay(AuriEsp, SkyrimRelease.SkyrimSE);
        var donorNpc = foxGlove.Npcs.First();

        string wigVariant = donorNpc.Weight >= 50f ? "22a_1.nif" : "22a_0.nif";
        string wigNifSource = Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, wigVariant);

        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BisectShape] = RenamePrefix + BisectShape,
        };

        SmpXmlRewriter.RewriteShapeNames(
            Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, "22a.xml"),
            Path.Combine(stagingDir, NewPhysicsXmlRelPath), renames);

        string faceGenDest = Path.Combine(meshesDir, FaceGenRelDir, "00000D63.NIF");
        Directory.CreateDirectory(Path.GetDirectoryName(faceGenDest)!);
        File.Copy(Path.Combine(FoxGloveModRoot, "meshes", FaceGenRelDir, "00000D63.NIF"), faceGenDest);

        var bakeLog = new List<string>();
        int baked = NifHandler.BakeWigIntoFaceGen(new NifHandler.WigBakeInstruction(
                faceGenDest, wigNifSource, renames,
                stripDonorHair
                    ? new[] { "FoxGloveHairMesh", "FoxGloveHairlineMesh", "FoxGloveHairScalp" }
                    : Array.Empty<string>(),
                NewPhysicsXmlRelPath,
                OnlyRenderShapes: new[] { BisectShape }),
            bakeLog.Add);
        baked.Should().Be(1, string.Join(Environment.NewLine, bakeLog));

        // Dedicated single-shape model NIF for the HDPT record.
        ExtractSingleShapeModelNif(wigNifSource, BisectShape,
            Path.Combine(meshesDir, SplitModelRelPath));

        var spike = new SkyrimMod(ModKey.FromNameAndExtension(SpikePluginName), SkyrimRelease.SkyrimSE);
        var hp = spike.HeadParts.AddNew();
        hp.EditorID = renames[BisectShape];
        hp.Type = stripDonorHair ? HeadPart.TypeEnum.Hair : HeadPart.TypeEnum.Misc;
        hp.Flags = HeadPart.Flag.Male | HeadPart.Flag.Female;
        hp.ValidRaces.SetTo(HeadPartsAllRacesMinusBeastKey);
        hp.Model = new Model { File = SplitModelRelPath };

        var patchNpc = spike.Npcs.GetOrAddAsOverride(donorNpc);
        if (stripDonorHair)
        {
            var headPartsByKey = foxGlove.HeadParts.Concat(auri.HeadParts)
                .GroupBy(h => h.FormKey).ToDictionary(g => g.Key, g => g.First());
            patchNpc.HeadParts.RemoveAll(l =>
                l != null && headPartsByKey.TryGetValue(l.FormKey, out var rec) &&
                rec.Type == HeadPart.TypeEnum.Hair).Should().BeGreaterThan(0);
        }
        patchNpc.HeadParts.Add(hp.FormKey.ToLink<IHeadPartGetter>());

        var wigArmorKeys = foxGlove.Armors
            .Where(a => a.EditorID?.Contains("Wig", StringComparison.OrdinalIgnoreCase) == true)
            .Select(a => a.FormKey).ToHashSet();
        var donorOutfit = foxGlove.Outfits.Concat(auri.Outfits)
            .FirstOrDefault(o => o.FormKey == donorNpc.DefaultOutfit.FormKey);
        if (donorOutfit != null)
        {
            var outfitDup = spike.Outfits.AddNew();
            outfitDup.EditorID = "NPC2_WigSpike_Outfit";
            foreach (var item in donorOutfit.Items ?? Enumerable.Empty<IFormLinkGetter<IOutfitTargetGetter>>())
            {
                if (!wigArmorKeys.Contains(item.FormKey))
                {
                    outfitDup.Items ??= new();
                    outfitDup.Items.Add(item.FormKey.ToLink<IOutfitTargetGetter>());
                }
            }
            patchNpc.DefaultOutfit.SetTo(outfitDup.FormKey);
        }

        var loadOrderKeys = new List<ModKey>();
        foreach (var master in auri.ModHeader.MasterReferences.Select(m => m.Master)
                     .Concat(foxGlove.ModHeader.MasterReferences.Select(m => m.Master))
                     .Append(auri.ModKey).Append(foxGlove.ModKey))
        {
            if (!loadOrderKeys.Contains(master)) loadOrderKeys.Add(master);
        }
        await spike.BeginWrite.ToPath(Path.Combine(stagingDir, SpikePluginName))
            .WithLoadOrder(loadOrderKeys
                .Select(mk => (ISkyrimModGetter)new SkyrimMod(mk, SkyrimRelease.SkyrimSE)).ToArray())
            .WriteAsync();
    }

    private const string PureOverrideStagingDir =
        @"S:\Skyrim NPC Selection\NPC2 WigSpike Staging\NPC2 WigSpike E - PureOverride";

    private const string MasqueradeStagingDir =
        @"S:\Skyrim NPC Selection\NPC2 WigSpike Staging\NPC2 WigSpike G - Masquerade";

    /// <summary>
    /// Diagnostic E: an esp whose NPC override is an EXACT copy of FoxGlove's
    /// record — zero changes, no facegen override, no head part edits. Field
    /// diff confirmed the copy is faithful. If this alone dark-faces, the
    /// problem is at the plugin/override layer, not head parts at all.
    /// </summary>
    [Fact]
    public async Task GeneratePureOverrideControlVariant()
    {
        if (SpecimensMissing) return;

        if (Directory.Exists(PureOverrideStagingDir)) Directory.Delete(PureOverrideStagingDir, true);
        Directory.CreateDirectory(PureOverrideStagingDir);

        using var foxGlove = SkyrimMod.CreateFromBinaryOverlay(FoxGloveEsp, SkyrimRelease.SkyrimSE);
        using var auri = SkyrimMod.CreateFromBinaryOverlay(AuriEsp, SkyrimRelease.SkyrimSE);
        var donorNpc = foxGlove.Npcs.First();

        var spike = new SkyrimMod(ModKey.FromNameAndExtension(SpikePluginName), SkyrimRelease.SkyrimSE);
        spike.Npcs.GetOrAddAsOverride(donorNpc); // untouched copy

        var loadOrderKeys = new List<ModKey>();
        foreach (var master in auri.ModHeader.MasterReferences.Select(m => m.Master)
                     .Concat(foxGlove.ModHeader.MasterReferences.Select(m => m.Master))
                     .Append(auri.ModKey).Append(foxGlove.ModKey))
        {
            if (!loadOrderKeys.Contains(master)) loadOrderKeys.Add(master);
        }
        await spike.BeginWrite.ToPath(Path.Combine(PureOverrideStagingDir, SpikePluginName))
            .WithLoadOrder(loadOrderKeys
                .Select(mk => (ISkyrimModGetter)new SkyrimMod(mk, SkyrimRelease.SkyrimSE)).ToArray())
            .WriteAsync();
    }

    /// <summary>
    /// Diagnostic G ("masquerade"): NO plugin at all. The facegen has the
    /// donor hair/hairline/scalp shapes stripped and three wig sub-shapes
    /// baked in under the ORIGINAL FoxGlove part EditorIDs (01b→HairMesh,
    /// 01a→HairlineMesh, Hl→HairScalp), so the untouched FoxGlove records
    /// reconcile by name against wig geometry. If this is clean, baked wig
    /// shapes are fully engine-acceptable and the fault lives in the minted
    /// HDPT record / link; if it dark-faces, the engine validates baked
    /// shapes against MORE than EditorID names (e.g. the part's Model NIF).
    /// </summary>
    [Fact]
    public void GenerateMasqueradeVariant()
    {
        if (SpecimensMissing) return;

        if (Directory.Exists(MasqueradeStagingDir)) Directory.Delete(MasqueradeStagingDir, true);
        string meshesDir = Path.Combine(MasqueradeStagingDir, "meshes");
        Directory.CreateDirectory(meshesDir);

        using var foxGlove = SkyrimMod.CreateFromBinaryOverlay(FoxGloveEsp, SkyrimRelease.SkyrimSE);
        var donorNpc = foxGlove.Npcs.First();
        string wigVariant = donorNpc.Weight >= 50f ? "22a_1.nif" : "22a_0.nif";
        string wigNifSource = Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, wigVariant);

        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["01b"] = "FoxGloveHairMesh",
            ["01a"] = "FoxGloveHairlineMesh",
            ["Hl"] = "FoxGloveHairScalp",
        };

        SmpXmlRewriter.RewriteShapeNames(
            Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, "22a.xml"),
            Path.Combine(MasqueradeStagingDir, NewPhysicsXmlRelPath), renames);

        string faceGenDest = Path.Combine(meshesDir, FaceGenRelDir, "00000D63.NIF");
        Directory.CreateDirectory(Path.GetDirectoryName(faceGenDest)!);
        File.Copy(Path.Combine(FoxGloveModRoot, "meshes", FaceGenRelDir, "00000D63.NIF"), faceGenDest);

        var bakeLog = new List<string>();
        int baked = NifHandler.BakeWigIntoFaceGen(new NifHandler.WigBakeInstruction(
                faceGenDest, wigNifSource, renames,
                new[] { "FoxGloveHairMesh", "FoxGloveHairlineMesh", "FoxGloveHairScalp" },
                NewPhysicsXmlRelPath,
                OnlyRenderShapes: new[] { "01b", "01a", "Hl" }),
            bakeLog.Add);
        baked.Should().Be(3, string.Join(Environment.NewLine, bakeLog));
    }

    private const string AdditionsOnlyStagingDir =
        @"S:\Skyrim NPC Selection\NPC2 WigSpike Staging\NPC2 WigSpike G1 - AdditionsOnly";

    private const string PipelineRebakeStagingDir =
        @"S:\Skyrim NPC Selection\NPC2 WigSpike Staging\NPC2 WigSpike H - PipelineRebake";

    /// <summary>
    /// Diagnostic G1: NO plugin; the original facegen with its hair shapes
    /// UNTOUCHED, plus ONLY the wig's SMP bone-node tree and physics
    /// extra-data cloned in at root level (no shapes at all). If this alone
    /// dark-faces, the engine's facegen/tint path chokes on the added root
    /// content (~330 extra NiNodes / the extra-data), not on any shape.
    /// </summary>
    [Fact]
    public void GenerateAdditionsOnlyVariant()
    {
        if (SpecimensMissing) return;

        if (Directory.Exists(AdditionsOnlyStagingDir)) Directory.Delete(AdditionsOnlyStagingDir, true);
        string faceGenDest = Path.Combine(AdditionsOnlyStagingDir, "meshes", FaceGenRelDir, "00000D63.NIF");
        Directory.CreateDirectory(Path.GetDirectoryName(faceGenDest)!);
        File.Copy(Path.Combine(FoxGloveModRoot, "meshes", FaceGenRelDir, "00000D63.NIF"), faceGenDest);

        SmpXmlRewriter.RewriteShapeNames(
            Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, "22a.xml"),
            Path.Combine(AdditionsOnlyStagingDir, NewPhysicsXmlRelPath),
            new Dictionary<string, string>());

        var bakeLog = new List<string>();
        int baked = NifHandler.BakeWigIntoFaceGen(new NifHandler.WigBakeInstruction(
                faceGenDest,
                Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, "22a_1.nif"),
                new Dictionary<string, string>(),
                Array.Empty<string>(),
                NewPhysicsXmlRelPath,
                OnlyRenderShapes: Array.Empty<string>()),
            bakeLog.Add);
        baked.Should().Be(0, string.Join(Environment.NewLine, bakeLog));

        // The additions must actually be present.
        using var nif = new nifly.NifFile();
        nif.Load(faceGenDest).Should().Be(0);
        NifHandler.GetPhysicsXmlPathsFromNif(faceGenDest).Should().Contain(NewPhysicsXmlRelPath);
    }

    /// <summary>
    /// Diagnostic H: NO plugin; the original hair shapes are stripped and then
    /// re-added from a second copy of the SAME original facegen through the
    /// same clone / bone-remap / save machinery the wig bake uses. Content is
    /// byte-for-byte known-good; only the pipeline touches it. If this
    /// dark-faces, the clone/remap/save layer mangles something the tint pass
    /// needs; if it's clean, the trigger is wig-shape content interacting
    /// with the facegen system.
    /// </summary>
    [Fact]
    public void GeneratePipelineRebakeVariant()
    {
        if (SpecimensMissing) return;

        if (Directory.Exists(PipelineRebakeStagingDir)) Directory.Delete(PipelineRebakeStagingDir, true);
        string faceGenDest = Path.Combine(PipelineRebakeStagingDir, "meshes", FaceGenRelDir, "00000D63.NIF");
        Directory.CreateDirectory(Path.GetDirectoryName(faceGenDest)!);
        string faceGenSource = Path.Combine(FoxGloveModRoot, "meshes", FaceGenRelDir, "00000D63.NIF");
        File.Copy(faceGenSource, faceGenDest);

        var hairShapes = new[] { "FoxGloveHairMesh", "FoxGloveHairlineMesh", "FoxGloveHairScalp" };

        using var dest = new nifly.NifFile();
        dest.Load(faceGenDest).Should().Be(0);
        using var src = new nifly.NifFile();
        src.Load(faceGenSource).Should().Be(0);

        // Source: keep ONLY the three hair shapes (under its skin node).
        var srcDoomed = new List<nifly.NiShape>();
        using (var shapes = src.GetShapes())
            foreach (var s in shapes)
                if (!hairShapes.Contains(s.name?.get(), StringComparer.OrdinalIgnoreCase)) srcDoomed.Add(s);
        foreach (var s in srcDoomed) src.DeleteShape(s);

        // Dest: strip the three hair shapes.
        var destDoomed = new List<nifly.NiShape>();
        using (var shapes = dest.GetShapes())
            foreach (var s in shapes)
                if (hairShapes.Contains(s.name?.get(), StringComparer.OrdinalIgnoreCase)) destDoomed.Add(s);
        foreach (var s in destDoomed) dest.DeleteShape(s);

        var destHeader = dest.GetHeader();
        uint preCount = destHeader.GetNumBlocks();

        // Clone the source skin node's children (now just the 3 hair shapes).
        nifly.NiNode? srcSkin = null, destSkin = null;
        var srcHeader = src.GetHeader();
        for (uint i = 0; i < srcHeader.GetNumBlocks(); i++)
            if (srcHeader.GetBlockById(i) is nifly.NiNode n &&
                n.name?.get() == NifHandler.FaceGenSkinNodeName) { srcSkin = n; break; }
        for (uint i = 0; i < preCount; i++)
            if (destHeader.GetBlockById(i) is nifly.NiNode n &&
                n.name?.get() == NifHandler.FaceGenSkinNodeName) { destSkin = n; break; }
        srcSkin.Should().NotBeNull();
        destSkin.Should().NotBeNull();

        var slots = new List<uint>();
        for (uint i = 0; i < srcSkin!.childRefs.GetSize(); i++) slots.Add(srcSkin.childRefs.GetBlockRef(i));
        dest.CloneChildren(srcSkin, src);

        // Attach clones under the dest skin node; remap bones by name.
        var nodeIdsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < destHeader.GetNumBlocks(); i++)
            if (destHeader.GetBlockById(i) is nifly.NiNode n)
            {
                var nm = n.name?.get();
                if (!string.IsNullOrEmpty(nm) && !nodeIdsByName.ContainsKey(nm)) nodeIdsByName[nm] = (int)i;
            }
        uint destRootId = dest.GetBlockID(dest.GetRootNode());

        int attached = 0;
        for (int slot = 0; slot < slots.Count; slot++)
        {
            if (slots[slot] == uint.MaxValue) continue;
            uint destId = srcSkin.childRefs.GetBlockRef((uint)slot);
            if (destId == uint.MaxValue || destId < preCount) continue;
            if (destHeader.GetBlockById(destId) is not nifly.NiShape destShape) continue;
            destSkin!.childRefs.AddBlockRef(destId);
            attached++;

            if (srcHeader.GetBlockById(slots[slot]) is not nifly.NiShape srcShape) continue;
            using var boneNames = new nifly.vectorstring();
            src.GetShapeBoneList(srcShape, boneNames);
            using var boneIds = new nifly.vectorint();
            foreach (string bn in boneNames)
            {
                nodeIdsByName.Should().ContainKey(bn);
                boneIds.Add(nodeIdsByName[bn]);
            }
            dest.SetShapeBoneIDList(destShape, boneIds);
            var skinRef = destShape.SkinInstanceRef();
            if (skinRef != null && !skinRef.IsEmpty() &&
                destHeader.GetBlockById(skinRef.index) is nifly.NiSkinInstance si)
            {
                si.targetRef.index = destRootId;
            }
        }
        attached.Should().Be(3);
        dest.Save(faceGenDest).Should().Be(0);
    }

    [Fact]
    public async Task GenerateSpikeModFolder()
    {
        if (SpecimensMissing) return;

        // ---- Fresh staging folder (ours alone; regenerated every run) ----
        if (Directory.Exists(StagingModDir)) Directory.Delete(StagingModDir, true);
        string meshesDir = Path.Combine(StagingModDir, "meshes");
        Directory.CreateDirectory(meshesDir);

        // ---- Load the source plugins (explicit paths — no game environment,
        //      the FoxGlove mod lives outside the Steam Data folder) ----
        using var foxGlove = SkyrimMod.CreateFromBinaryOverlay(FoxGloveEsp, SkyrimRelease.SkyrimSE);
        using var auri = SkyrimMod.CreateFromBinaryOverlay(AuriEsp, SkyrimRelease.SkyrimSE);

        var donorNpc = foxGlove.Npcs.FirstOrDefault();
        donorNpc.Should().NotBeNull("FoxGloveAuri.esp carries exactly one NPC override (Auri)");

        // ---- Pick the weight-matched worn wig NIF (_0 / _1 pair) ----
        string wigVariant = donorNpc!.Weight >= 50f ? "22a_1.nif" : "22a_0.nif";
        string wigNifSource = Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, wigVariant);
        File.Exists(wigNifSource).Should().BeTrue();

        // ---- Rename map: NIF shape name -> minted HeadPart EditorID ----
        var renderShapes = NifHandler.GetRenderShapeNames(wigNifSource);
        renderShapes.Should().HaveCount(13);
        var renames = renderShapes.ToDictionary(
            n => n,
            n => RenamePrefix + new string(n.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray()),
            StringComparer.OrdinalIgnoreCase);

        // ---- Rewritten physics XML (shape entries follow the renames) ----
        string physicsXmlSource = Path.Combine(FoxGloveModRoot, "meshes", WigNifRelPath, "22a.xml");
        string physicsXmlDest = Path.Combine(StagingModDir, NewPhysicsXmlRelPath);
        SmpXmlRewriter.RewriteShapeNames(physicsXmlSource, physicsXmlDest, renames)
            .Should().BeGreaterThan(0);

        // ---- Baked FaceGen: donor hair stripped, wig scene merged in ----
        string faceGenDest = Path.Combine(meshesDir, FaceGenRelDir, "00000D63.NIF");
        Directory.CreateDirectory(Path.GetDirectoryName(faceGenDest)!);
        File.Copy(Path.Combine(FoxGloveModRoot, "meshes", FaceGenRelDir, "00000D63.NIF"), faceGenDest);

        // Strip the donor hair AND all its ExtraParts (hairline + scalp) —
        // removing FoxGloveHairMesh from the record removes its ExtraParts
        // from the engine's reconciliation set, so any of their shapes left
        // baked becomes an orphan and dark-faces the NPC (engine-verified on
        // this exact specimen: leaving the scalp baked dark-faced Auri).
        // Mirrors WigForwarder.CollectHairRemoval + AddShapeNames.
        var bakeLog = new List<string>();
        int baked = NifHandler.BakeWigIntoFaceGen(new NifHandler.WigBakeInstruction(
                faceGenDest, wigNifSource, renames,
                new[] { "FoxGloveHairMesh", "FoxGloveHairlineMesh", "FoxGloveHairScalp" },
                NewPhysicsXmlRelPath),
            bakeLog.Add);
        baked.Should().Be(13, string.Join(Environment.NewLine, bakeLog));

        // ---- Wig NIF at its original rel path (parent HeadPart Model) ----
        string wigNifDest = Path.Combine(meshesDir, WigNifRelPath, wigVariant);
        Directory.CreateDirectory(Path.GetDirectoryName(wigNifDest)!);
        File.Copy(wigNifSource, wigNifDest);

        // ---- The spike plugin ----
        var spike = new SkyrimMod(ModKey.FromNameAndExtension(SpikePluginName), SkyrimRelease.SkyrimSE);

        // HeadParts: shape 0 is the parent (Type=Hair, carries the Model);
        // the rest are modeless IsExtraPart records linked via ExtraParts.
        // EditorID == baked FaceGen shape name for every one of them (the
        // engine reconciles records against baked shapes by name; SMP
        // addresses shapes by the same names via the rewritten XML).
        var mintedByShape = new Dictionary<string, HeadPart>();
        HeadPart? parent = null;
        foreach (var srcName in renderShapes)
        {
            var hp = spike.HeadParts.AddNew();
            hp.EditorID = renames[srcName];
            hp.ValidRaces.SetTo(HeadPartsAllRacesMinusBeastKey);
            // EVERY part carries a Model — the engine's facegen reconciliation
            // only expects baked geometry for geometry-bearing head parts
            // (modeless parts like NPC2_HairBald are excluded), so a modeless
            // extra leaves its baked shape orphaned → dark face. All working
            // precedents (vanilla hairlines, FoxGlove's own extras, the
            // ARMO_2_HDPT converter) model their extra parts. Facegen NPCs
            // don't load these NIFs, so all 13 can share the whole wig mesh.
            hp.Model = new Model { File = Path.Combine(WigNifRelPath, wigVariant) };
            if (parent == null)
            {
                hp.Type = HeadPart.TypeEnum.Hair;
                hp.Flags = HeadPart.Flag.Male | HeadPart.Flag.Female;
                parent = hp;
            }
            else
            {
                hp.Type = HeadPart.TypeEnum.Misc;
                hp.Flags = HeadPart.Flag.Male | HeadPart.Flag.Female | HeadPart.Flag.IsExtraPart;
                parent.ExtraParts.Add(hp.FormKey.ToLink<IHeadPartGetter>());
            }
            mintedByShape[srcName] = hp;
        }

        // NPC override: replace the Hair-type head part(s) with the wig parent.
        var patchNpc = spike.Npcs.GetOrAddAsOverride(donorNpc);
        var headPartsByKey = foxGlove.HeadParts.Concat(auri.HeadParts)
            .GroupBy(h => h.FormKey).ToDictionary(g => g.Key, g => g.First());
        int removedHair = patchNpc.HeadParts.RemoveAll(l =>
            l != null && headPartsByKey.TryGetValue(l.FormKey, out var rec) &&
            rec.Type == HeadPart.TypeEnum.Hair);
        removedHair.Should().BeGreaterThan(0, "Auri's FoxGlove hair must be replaced, not stacked under the wig");
        patchNpc.HeadParts.Add(parent!.FormKey.ToLink<IHeadPartGetter>());

        // Outfit: duplicate minus the wig ARMO so the armor wig isn't equipped
        // on top of the baked one.
        var wigArmorKeys = foxGlove.Armors
            .Where(a => a.EditorID?.Contains("Wig", StringComparison.OrdinalIgnoreCase) == true)
            .Select(a => a.FormKey)
            .ToHashSet();
        wigArmorKeys.Should().NotBeEmpty("FoxGloveAuri.esp defines the FoxGlove_Wig armor");

        var donorOutfitKey = donorNpc.DefaultOutfit.FormKey;
        var donorOutfit = foxGlove.Outfits.Concat(auri.Outfits)
            .FirstOrDefault(o => o.FormKey == donorOutfitKey);
        if (donorOutfit != null)
        {
            var outfitDup = spike.Outfits.AddNew();
            outfitDup.EditorID = "NPC2_WigSpike_Outfit";
            foreach (var item in donorOutfit.Items ?? Enumerable.Empty<IFormLinkGetter<IOutfitTargetGetter>>())
            {
                if (!wigArmorKeys.Contains(item.FormKey))
                {
                    outfitDup.Items ??= new();
                    outfitDup.Items.Add(item.FormKey.ToLink<IOutfitTargetGetter>());
                }
            }
            patchNpc.DefaultOutfit.SetTo(outfitDup.FormKey);
        }

        // Master sorting needs a load order; supply LOADED stand-ins (the
        // builder wants each master's style, and there's no data folder) —
        // union of the source mods' own masters plus the source mods.
        var loadOrderKeys = new List<ModKey>();
        foreach (var master in auri.ModHeader.MasterReferences.Select(m => m.Master)
                     .Concat(foxGlove.ModHeader.MasterReferences.Select(m => m.Master))
                     .Append(auri.ModKey)
                     .Append(foxGlove.ModKey))
        {
            if (!loadOrderKeys.Contains(master)) loadOrderKeys.Add(master);
        }
        var loadOrder = loadOrderKeys
            .Select(mk => (ISkyrimModGetter)new SkyrimMod(mk, SkyrimRelease.SkyrimSE))
            .ToArray();

        string pluginPath = Path.Combine(StagingModDir, SpikePluginName);
        await spike.BeginWrite.ToPath(pluginPath)
            .WithLoadOrder(loadOrder)
            .WriteAsync();

        // ---- Verify the emitted plugin round-trips ----
        using var reloaded = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        reloaded.HeadParts.Should().HaveCount(13);

        var reloadedParent = reloaded.HeadParts.First(h => h.EditorID == renames[renderShapes[0]]);
        reloadedParent.Type.Should().Be(HeadPart.TypeEnum.Hair);
        reloadedParent.ExtraParts.Should().HaveCount(12);
        reloadedParent.Model.Should().NotBeNull();
        reloadedParent.Flags.Should().NotHaveFlag(HeadPart.Flag.IsExtraPart);
        reloadedParent.Flags.Should().NotHaveFlag(HeadPart.Flag.Playable);

        foreach (var extra in reloaded.HeadParts.Where(h => h.FormKey != reloadedParent.FormKey))
        {
            extra.Flags.Should().HaveFlag(HeadPart.Flag.IsExtraPart);
            extra.Model.Should().NotBeNull(
                "every part must be geometry-bearing or the engine orphans its baked shape (dark face)");
        }

        var reloadedNpc = reloaded.Npcs.Single();
        reloadedNpc.HeadParts.Select(l => l.FormKey).Should().Contain(reloadedParent.FormKey);
        reloadedNpc.HeadParts.Select(l => l.FormKey).Should().NotContain(
            headPartsByKey.Values.Where(h => h.Type == HeadPart.TypeEnum.Hair).Select(h => h.FormKey));
    }
}
