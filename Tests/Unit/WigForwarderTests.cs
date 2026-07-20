using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Patch-time wig forwarding against in-memory mods. The donor plugin is
/// seeded straight into RecordHandler's ModKey-keyed link caches (the same
/// seam <c>RecordHandlerCacheProvenanceTests</c> uses) so mod-scoped record
/// resolution works without a Skyrim install; the EnvironmentStateProvider is
/// an uninitialized instance carrying only the in-memory output mod (its
/// LinkCache is null, so the effective-outfit stack resolves to "no outfit" —
/// which also exercises the authored wig-only outfit branch). The
/// ForwardToOutfit path against a REAL winning outfit needs a game
/// environment and is covered by the manual patch-run verification instead.
/// </summary>
public class WigForwarderTests
{
    private static readonly ModKey DonorKey = ModKey.FromNameAndExtension("FoxGloveAuri.esp");

    private sealed class Fixture
    {
        public SkyrimMod DonorMod = null!;
        public SkyrimMod OutputMod = null!;
        public Settings Settings = null!;
        public RecordHandler RecordHandler = null!;
        public WigForwarder Forwarder = null!;
        public Npc DonorNpc = null!;
        public Armor SkinArmor = null!;
        public ArmorAddon SkinArma = null!;
        public Armor WigArmor = null!;
        public ArmorAddon WigArma = null!;
        public Armor AntlerArmor = null!;
        public ArmorAddon AntlerArma = null!;
        public Armor DressArmor = null!;
        public Outfit DonorOutfit = null!;
        public ModSetting ModSetting = null!;
        public HeadPart HairHeadPart = null!;
        public HeadPart HairlinePart = null!;
        public HeadPart EyesHeadPart = null!;

        public Dictionary<FormKey, FormKey> Mappings =>
            Reflect.GetField<Dictionary<FormKey, FormKey>>(RecordHandler, "_currentDuplicateInMappings");
    }

    private static Fixture Make(WigHandlingMode? perModMode, bool donorHasWnam = true)
    {
        var f = new Fixture
        {
            DonorMod = new SkyrimMod(DonorKey, SkyrimRelease.SkyrimSE),
            Settings = new Settings { PatchingMode = PatchingMode.CreateAndPatch },
        };

        f.SkinArma = f.DonorMod.ArmorAddons.AddNew();
        f.SkinArma.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body };
        f.SkinArmor = f.DonorMod.Armors.AddNew();
        f.SkinArmor.EditorID = "AuriSkin";
        f.SkinArmor.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body };
        f.SkinArmor.Armature.Add(f.SkinArma.ToLink());

        f.WigArma = f.DonorMod.ArmorAddons.AddNew();
        f.WigArma.BodyTemplate = new BodyTemplate
            { FirstPersonFlags = BipedObjectFlag.Hair | BipedObjectFlag.LongHair };
        f.WigArmor = f.DonorMod.Armors.AddNew();
        f.WigArmor.Name = "Auri Red Wig";
        f.WigArmor.EditorID = "AuriWig";
        f.WigArmor.Armature.Add(f.WigArma.ToLink());

        f.AntlerArma = f.DonorMod.ArmorAddons.AddNew();
        f.AntlerArma.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Circlet };
        f.AntlerArmor = f.DonorMod.Armors.AddNew();
        f.AntlerArmor.EditorID = "AuriAntlers";
        f.AntlerArmor.Armature.Add(f.AntlerArma.ToLink());

        f.DressArmor = f.DonorMod.Armors.AddNew();
        f.DressArmor.EditorID = "AuriDress";

        f.DonorOutfit = f.DonorMod.Outfits.AddNew();
        f.DonorOutfit.EditorID = "AuriOutfit";
        f.DonorOutfit.Items = new Noggog.ExtendedList<IFormLinkGetter<IOutfitTargetGetter>>
        {
            f.WigArmor.FormKey.ToLink<IOutfitTargetGetter>(),
            f.AntlerArmor.FormKey.ToLink<IOutfitTargetGetter>(),
            f.DressArmor.FormKey.ToLink<IOutfitTargetGetter>(),
        };

        f.HairlinePart = f.DonorMod.HeadParts.AddNew();
        f.HairlinePart.EditorID = "FoxGloveHairline";
        f.HairlinePart.Type = HeadPart.TypeEnum.Hair;
        f.HairHeadPart = f.DonorMod.HeadParts.AddNew();
        f.HairHeadPart.EditorID = "FoxGloveHairMesh";
        f.HairHeadPart.Type = HeadPart.TypeEnum.Hair;
        f.HairHeadPart.ExtraParts.Add(f.HairlinePart.ToLink());
        f.EyesHeadPart = f.DonorMod.HeadParts.AddNew();
        f.EyesHeadPart.EditorID = "FoxGloveEyeMesh";
        f.EyesHeadPart.Type = HeadPart.TypeEnum.Eyes;

        f.DonorNpc = MutagenFixtures.NewNpc(f.DonorMod, editorId: "Auri");
        if (donorHasWnam) f.DonorNpc.WornArmor.SetTo(f.SkinArmor);
        f.DonorNpc.DefaultOutfit.SetTo(f.DonorOutfit);
        f.DonorNpc.HeadParts.Add(f.HairHeadPart.ToLink());
        f.DonorNpc.HeadParts.Add(f.EyesHeadPart.ToLink());

        // Environment: uninitialized (no game install) with only the output
        // mod attached. LinkCache stays null.
        var env = (EnvironmentStateProvider)RuntimeHelpers.GetUninitializedObject(typeof(EnvironmentStateProvider));
        f.OutputMod = new SkyrimMod(ModKey.FromNameAndExtension("NPC.esp"), SkyrimRelease.SkyrimSE);
        env.OutputMod = f.OutputMod;

        var pluginProvider = new PluginProvider(env, f.Settings);
        f.RecordHandler = new RecordHandler(env, pluginProvider, f.Settings);

        // Seed the donor plugin into the ModKey-keyed link-cache trio so
        // mod-scoped resolution works without disk plugins.
        Reflect.GetField<ConcurrentDictionary<ModKey, ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>>>(
                f.RecordHandler, "_modLinkCaches")[DonorKey] =
            new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(f.DonorMod, new LinkCachePreferences());
        Reflect.GetField<ConcurrentDictionary<ModKey, ISkyrimModGetter>>(
            f.RecordHandler, "_modLinkCachePlugins")[DonorKey] = f.DonorMod;
        Reflect.GetField<ConcurrentDictionary<ModKey, string>>(
            f.RecordHandler, "_modLinkCacheSourcePaths")[DonorKey] = @"c:\mods\foxglove\foxgloveauri.esp";

        var outfitDisplayResolver = new OutfitDisplayResolver(f.Settings, env, f.RecordHandler);
        f.Forwarder = new WigForwarder(env, f.RecordHandler, f.Settings, outfitDisplayResolver);

        f.ModSetting = new ModSetting
        {
            DisplayName = "FoxGlove",
            CorrespondingModKeys = { DonorKey },
            DetectedWigArmors = { f.WigArmor.FormKey },
            DetectedAntlerArmors = { f.AntlerArmor.FormKey },
            ModWigHandlingMode = perModMode,
        };
        return f;
    }

    private static WigForwarder.Result? Apply(Fixture f, bool mergeIn = true) =>
        f.Forwarder.Apply(f.DonorNpc.FormKey, f.DonorNpc, f.ModSetting, DonorKey,
            new HashSet<string>(), mergeIn, "TestNpc", (_, _, _) => { });

    [Fact]
    public void ForwardToSkin_DuplicatesWnam_TransfersArmatures_AndSeedsMapping()
    {
        var f = Make(WigHandlingMode.ForwardToSkin);

        var result = Apply(f);

        result.Should().NotBeNull();
        result!.SkinDuplicateKey.Should().NotBeNull();
        result.OutfitDuplicateKey.Should().BeNull();

        // Exactly one Armor in the output: the +Wig duplicate. The ORIGINAL
        // WNAM must not be merged as its own record.
        f.OutputMod.Armors.Should().HaveCount(1);
        var dup = f.OutputMod.Armors.First();
        dup.FormKey.Should().Be(result.SkinDuplicateKey!.Value);
        dup.EditorID.Should().Be("AuriSkin",
            "the duplicate IS the merged WNAM; OutputValidator equates skins by EditorID");

        // The duplicate's own BOD2 mask must be widened to cover the
        // transferred ARMAs — the engine ignores addons on slots the parent
        // armor doesn't declare (user-verified in game), so the original Body
        // mask must gain Hair+LongHair (wig) and Circlet (antler).
        dup.BodyTemplate.Should().NotBeNull();
        dup.BodyTemplate!.FirstPersonFlags.Should().Be(
            BipedObjectFlag.Body | BipedObjectFlag.Hair | BipedObjectFlag.LongHair | BipedObjectFlag.Circlet);

        // Armature: original skin ARMA + the wig's hair ARMA + ALL antler
        // ARMAs — merged into the output and remapped there.
        dup.Armature.Should().HaveCount(3);
        f.OutputMod.ArmorAddons.Should().HaveCount(3);
        dup.Armature.Select(a => a.FormKey.ModKey).Should().OnlyContain(mk => mk == f.OutputMod.ModKey,
            "the duplicate's ARMA chain must be self-contained after merge-in");

        // The seeded mapping is what makes CopyAppearanceData's skin merge
        // redirect to the duplicate instead of re-duplicating the original.
        f.Mappings.Should().ContainKey(f.SkinArmor.FormKey)
            .WhoseValue.Should().Be(dup.FormKey);

        result.MergedRecords.Should().Contain(r => r.FormKey == dup.FormKey);
    }

    [Fact]
    public void ForwardToSkin_WithoutMergeIn_KeepsDonorArmatureLinks()
    {
        var f = Make(WigHandlingMode.ForwardToSkin);

        var result = Apply(f, mergeIn: false);

        result!.SkinDuplicateKey.Should().NotBeNull();
        var dup = f.OutputMod.Armors.First();
        dup.Armature.Select(a => a.FormKey).Should().BeEquivalentTo(new[]
        {
            f.SkinArma.FormKey, f.WigArma.FormKey, f.AntlerArma.FormKey,
        }, "without merge-in the output depends on the donor plugin as a master");
        f.OutputMod.ArmorAddons.Should().BeEmpty();
    }

    [Fact]
    public void ForwardToSkin_NoDonorWnam_FallsBackToOutfitForwarding()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, donorHasWnam: false);

        var result = Apply(f, mergeIn: false);

        result.Should().NotBeNull();
        result!.SkinDuplicateKey.Should().BeNull();
        result.OutfitDuplicateKey.Should().NotBeNull();

        // No resolvable effective outfit here (null LinkCache) -> a minimal
        // wig-only outfit is authored so the wig still reaches the NPC.
        var outfit = f.OutputMod.Outfits.First();
        outfit.FormKey.Should().Be(result.OutfitDuplicateKey!.Value);
        outfit.Items!.Select(i => i.FormKey).Should().BeEquivalentTo(new[]
        {
            f.WigArmor.FormKey, f.AntlerArmor.FormKey,
        });
    }

    [Fact]
    public void ForwardToOutfit_AddsWigsToOutfit_AndLeavesSkinAlone()
    {
        var f = Make(WigHandlingMode.ForwardToOutfit);

        var result = Apply(f, mergeIn: false);

        result!.SkinDuplicateKey.Should().BeNull();
        result.OutfitDuplicateKey.Should().NotBeNull();
        f.OutputMod.Armors.Should().BeEmpty();
        f.Mappings.Should().NotContainKey(f.DonorOutfit.FormKey,
            "outfit duplicates must never be seeded — other NPCs legitimately share the original outfit");
    }

    [Fact]
    public void ModeNone_DoesNothing()
    {
        var f = Make(WigHandlingMode.None);
        Apply(f).Should().BeNull();
        f.OutputMod.Armors.Should().BeEmpty();
        f.OutputMod.Outfits.Should().BeEmpty();
    }

    [Fact]
    public void NoDetectedWigInDonorOutfit_DoesNothing()
    {
        var f = Make(WigHandlingMode.ForwardToSkin);
        f.ModSetting.DetectedWigArmors.Clear();
        f.ModSetting.DetectedAntlerArmors.Clear();
        f.ModSetting.DetectedWigArmors.Add(MutagenFixtures.Fk("0FFFFF:Other.esp")); // detection exists, but not in this NPC's outfit

        Apply(f).Should().BeNull();
        f.OutputMod.Armors.Should().BeEmpty();
    }

    [Fact]
    public void RepeatApply_ReusesTheSameDuplicate()
    {
        var f = Make(WigHandlingMode.ForwardToSkin);

        var first = Apply(f);
        var second = Apply(f);

        second!.SkinDuplicateKey.Should().Be(first!.SkinDuplicateKey);
        f.OutputMod.Armors.Should().HaveCount(1,
            "NPCs sharing the same WNAM + wig set must share one +Wig duplicate");
    }

    [Fact]
    public void ApplyLinksTo_PointsThePatchedNpcAtTheDuplicates()
    {
        var f = Make(WigHandlingMode.ForwardToSkin);
        var result = Apply(f)!;

        var patchNpc = f.OutputMod.Npcs.AddNew();
        result.ApplyLinksTo(patchNpc);

        patchNpc.WornArmor.FormKey.Should().Be(result.SkinDuplicateKey!.Value);
    }

    [Fact]
    public void ForwardToSkin_CollectsHairHeadPartsAndShapeNames()
    {
        var f = Make(WigHandlingMode.ForwardToSkin);

        var result = Apply(f)!;

        result.DonorHairHeadPartKeys.Should().BeEquivalentTo(new[] { f.HairHeadPart.FormKey },
            "only the Hair-type head part is superseded by the wig — eyes must stay");
        result.HairShapeNames.Should().BeEquivalentTo(new[] { "FoxGloveHairMesh", "FoxGloveHairline" },
            "the FaceGen strip needs the hair's EditorID plus its ExtraParts' EditorIDs");
    }

    [Fact]
    public void FinalizeNpcRecord_ReplacesHairHeadParts_WithSharedBaldRecord()
    {
        var f = Make(WigHandlingMode.ForwardToSkin);
        var result = Apply(f)!;

        // Simulate CopyAppearanceData's merge outcome: the hair head part got
        // remapped to an output duplicate, eyes kept their donor key.
        var mergedHair = f.OutputMod.HeadParts.AddNew();
        mergedHair.EditorID = "FoxGloveHairMesh";
        f.RecordHandler.SeedDuplicateMapping(f.HairHeadPart.FormKey, mergedHair.FormKey);

        var patchNpc = f.OutputMod.Npcs.AddNew();
        patchNpc.HeadParts.Add(mergedHair.ToLink());
        patchNpc.HeadParts.Add(f.EyesHeadPart.ToLink());

        f.Forwarder.FinalizeNpcRecord(result, patchNpc, "TestNpc", (_, _, _) => { });

        // The MAPPED hair duplicate is removed via the duplicate-in mapping
        // lookup and replaced with the generated modeless bald hair — an NPC
        // with NO hair head part gets a random race-chargen hair back-filled
        // by the engine (and dark-faces), so removal alone is not enough.
        var bald = f.OutputMod.HeadParts.Single(h => h.EditorID == WigForwarder.BaldHairEditorId);
        bald.Type.Should().Be(HeadPart.TypeEnum.Hair);
        bald.Model.Should().BeNull("the bald hair must render nothing — the wig supplies the visuals");
        bald.Flags.Should().Be(HeadPart.Flag.Male | HeadPart.Flag.Female,
            "non-playable, both sexes — mirroring High Poly NPC Overhaul's HighPoly_HairBald");
        bald.ValidRaces.FormKey.Should().Be(FormKey.Factory("0A803F:Skyrim.esm"),
            "HeadPartsAllRacesMinusBeast, as HPNO uses");

        patchNpc.HeadParts.Select(h => h.FormKey).Should().BeEquivalentTo(
            new[] { f.EyesHeadPart.FormKey, bald.FormKey });
        patchNpc.WornArmor.FormKey.Should().Be(result.SkinDuplicateKey!.Value);

        // Non-merge path (links still carry donor keys) — and the SAME bald
        // record is reused across NPCs, not re-created.
        var patchNpc2 = f.OutputMod.Npcs.AddNew();
        patchNpc2.HeadParts.Add(f.HairHeadPart.ToLink());
        patchNpc2.HeadParts.Add(f.EyesHeadPart.ToLink());

        f.Forwarder.FinalizeNpcRecord(result, patchNpc2, "TestNpc", (_, _, _) => { });

        patchNpc2.HeadParts.Select(h => h.FormKey).Should().BeEquivalentTo(
            new[] { f.EyesHeadPart.FormKey, bald.FormKey });
        f.OutputMod.HeadParts.Count(h => h.EditorID == WigForwarder.BaldHairEditorId).Should().Be(1);
    }

    [Fact]
    public void AntlerOnlyForwarding_KeepsTheHairHeadPart()
    {
        var f = Make(WigHandlingMode.ForwardToSkin);
        f.ModSetting.DetectedWigArmors.Clear(); // antlers remain detected

        var result = Apply(f)!;

        result.SkinDuplicateKey.Should().NotBeNull("antlers still forward to the skin");
        result.DonorHairHeadPartKeys.Should().BeEmpty(
            "no hair-slot piece was transferred, so the NPC's real hair must survive (Option B specimen)");
        result.HairShapeNames.Should().BeEmpty();

        var patchNpc = f.OutputMod.Npcs.AddNew();
        patchNpc.HeadParts.Add(f.HairHeadPart.ToLink());
        f.Forwarder.FinalizeNpcRecord(result, patchNpc, "TestNpc", (_, _, _) => { });
        patchNpc.HeadParts.Select(h => h.FormKey).Should().Contain(f.HairHeadPart.FormKey);
        f.OutputMod.HeadParts.Should().BeEmpty("no bald record is generated when the real hair stays");
    }

    [Fact]
    public void DuplicateReuse_StillCollectsHairRemovalForTheLaterNpc()
    {
        var f = Make(WigHandlingMode.ForwardToSkin);

        var first = Apply(f);
        var second = Apply(f);

        second!.SkinDuplicateKey.Should().Be(first!.SkinDuplicateKey);
        second.DonorHairHeadPartKeys.Should().BeEquivalentTo(new[] { f.HairHeadPart.FormKey },
            "hair removal is per-NPC and must not be lost on a duplicate-reuse cache hit");
        second.HairShapeNames.Should().Contain("FoxGloveHairMesh");
    }
}
