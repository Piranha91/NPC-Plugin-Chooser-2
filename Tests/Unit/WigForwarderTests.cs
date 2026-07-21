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
/// Patch-time wig/antler forwarding against in-memory mods, evaluating the two
/// classes independently. The donor plugin is seeded straight into
/// RecordHandler's ModKey-keyed link caches (the same seam
/// <c>RecordHandlerCacheProvenanceTests</c> uses) so mod-scoped record resolution
/// works without a Skyrim install; the EnvironmentStateProvider is an
/// uninitialized instance carrying only the in-memory output mod (its LinkCache
/// is null, so the effective-outfit stack resolves to "no outfit" — which also
/// exercises the authored wig/antler-only outfit branch). The ForwardToOutfit
/// path against a REAL winning outfit needs a game environment and is covered by
/// the manual patch-run verification instead.
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
        public ArmorAddon? WnamAntlerArma;
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
        public HeadPart? AntlerHeadPart;

        public Dictionary<FormKey, FormKey> Mappings =>
            Reflect.GetField<Dictionary<FormKey, FormKey>>(RecordHandler, "_currentDuplicateInMappings");
    }

    private static Fixture Make(WigHandlingMode? wigMode, AntlerHandlingMode? antlerMode,
        bool donorHasWnam = true, bool wnamBakedAntler = false, bool faceGenAntlerHeadPart = false)
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

        // Source 2: an antler ArmorAddon baked directly into the WornArmor.
        if (wnamBakedAntler)
        {
            f.WnamAntlerArma = f.DonorMod.ArmorAddons.AddNew();
            f.WnamAntlerArma.EditorID = "AuriWnamAntlerAddon";
            f.WnamAntlerArma.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Circlet };
            f.SkinArmor.Armature.Add(f.WnamAntlerArma.ToLink());
        }

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

        // Source 3: an antler head part baked into the FaceGen. (Type is
        // irrelevant — antler head parts are keyed by FormKey, not Type.)
        if (faceGenAntlerHeadPart)
        {
            f.AntlerHeadPart = f.DonorMod.HeadParts.AddNew();
            f.AntlerHeadPart.EditorID = "AuriAntlerHeadPart";
        }

        f.DonorNpc = MutagenFixtures.NewNpc(f.DonorMod, editorId: "Auri");
        if (donorHasWnam) f.DonorNpc.WornArmor.SetTo(f.SkinArmor);
        f.DonorNpc.DefaultOutfit.SetTo(f.DonorOutfit);
        f.DonorNpc.HeadParts.Add(f.HairHeadPart.ToLink());
        f.DonorNpc.HeadParts.Add(f.EyesHeadPart.ToLink());
        if (f.AntlerHeadPart != null) f.DonorNpc.HeadParts.Add(f.AntlerHeadPart.ToLink());

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
            ModWigHandlingMode = wigMode,
            ModAntlerHandlingMode = antlerMode,
        };
        // The scan folds an antler ARMO's own addons into the ARMA set; a
        // WornArmor-baked antler adds its own.
        f.ModSetting.DetectedAntlerArmatures.Add(f.AntlerArma.FormKey);
        if (f.WnamAntlerArma != null) f.ModSetting.DetectedAntlerArmatures.Add(f.WnamAntlerArma.FormKey);
        if (f.AntlerHeadPart != null) f.ModSetting.DetectedAntlerHeadParts.Add(f.AntlerHeadPart.FormKey);
        return f;
    }

    private static WigForwarder.Result? Apply(Fixture f, bool mergeIn = true, bool includeOutfit = false) =>
        f.Forwarder.Apply(f.DonorNpc.FormKey, f.DonorNpc, f.ModSetting, DonorKey,
            new HashSet<string>(), mergeIn, includeOutfit, "TestNpc", (_, _, _) => { });

    [Fact]
    public void ForwardToSkin_BothClasses_DuplicatesWnam_TransfersArmatures_AndSeedsMapping()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.ForwardToSkin);

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

        // The duplicate's own BOD2 mask must be widened to cover the transferred
        // ARMAs — Body must gain Hair+LongHair (wig) and Circlet (antler).
        dup.BodyTemplate.Should().NotBeNull();
        dup.BodyTemplate!.FirstPersonFlags.Should().Be(
            BipedObjectFlag.Body | BipedObjectFlag.Hair | BipedObjectFlag.LongHair | BipedObjectFlag.Circlet);

        // Armature: skin ARMA + wig hair ARMA + ALL antler ARMAs.
        dup.Armature.Should().HaveCount(3);
        f.OutputMod.ArmorAddons.Should().HaveCount(3);

        f.Mappings.Should().ContainKey(f.SkinArmor.FormKey)
            .WhoseValue.Should().Be(dup.FormKey);
        result.MergedRecords.Should().Contain(r => r.FormKey == dup.FormKey);
    }

    [Fact]
    public void ForwardToSkin_WithoutMergeIn_KeepsDonorArmatureLinks()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.ForwardToSkin);

        Apply(f, mergeIn: false);

        var dup = f.OutputMod.Armors.First();
        dup.Armature.Select(a => a.FormKey).Should().BeEquivalentTo(new[]
        {
            f.SkinArma.FormKey, f.WigArma.FormKey, f.AntlerArma.FormKey,
        }, "without merge-in the output depends on the donor plugin as a master");
        f.OutputMod.ArmorAddons.Should().BeEmpty();
    }

    [Fact]
    public void WigOnlyToSkin_DoesNotTransferTheAntler()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.None);

        Apply(f, mergeIn: false);

        var dup = f.OutputMod.Armors.Single();
        dup.Armature.Select(a => a.FormKey).Should().BeEquivalentTo(new[]
        {
            f.SkinArma.FormKey, f.WigArma.FormKey,
        }, "antler mode None leaves the antler out of the skin");
        dup.BodyTemplate!.FirstPersonFlags.Should().NotHaveFlag(BipedObjectFlag.Circlet);
    }

    [Fact]
    public void ForwardToSkin_NoDonorWnam_FallsBackToOutfitForwarding()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.ForwardToSkin, donorHasWnam: false);

        var result = Apply(f, mergeIn: false);

        result.Should().NotBeNull();
        result!.SkinDuplicateKey.Should().BeNull();
        result.OutfitDuplicateKey.Should().NotBeNull();

        // No resolvable effective outfit here (null LinkCache) -> a minimal
        // wig/antler-only outfit is authored so both still reach the NPC.
        var outfit = f.OutputMod.Outfits.First();
        outfit.FormKey.Should().Be(result.OutfitDuplicateKey!.Value);
        outfit.Items!.Select(i => i.FormKey).Should().BeEquivalentTo(new[]
        {
            f.WigArmor.FormKey, f.AntlerArmor.FormKey,
        });
    }

    [Fact]
    public void ForwardToOutfit_BothClasses_AddsToOutfit_AndLeavesSkinAlone()
    {
        var f = Make(WigHandlingMode.ForwardToOutfit, AntlerHandlingMode.ForwardToOutfit);

        var result = Apply(f, mergeIn: false);

        result!.SkinDuplicateKey.Should().BeNull();
        result.OutfitDuplicateKey.Should().NotBeNull();
        f.OutputMod.Armors.Should().BeEmpty();
        f.Mappings.Should().NotContainKey(f.DonorOutfit.FormKey,
            "outfit duplicates must never be seeded — other NPCs legitimately share the original outfit");
    }

    [Fact]
    public void WigToSkin_AntlerToOutfit_ProducesBothDuplicates()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.ForwardToOutfit);

        var result = Apply(f, mergeIn: false)!;

        result.SkinDuplicateKey.Should().NotBeNull("the wig forwards to the skin");
        result.OutfitDuplicateKey.Should().NotBeNull("the antler forwards to an outfit");

        // Skin dup carries the wig ARMA but not the antler ARMA.
        var skinDup = f.OutputMod.Armors.Single();
        skinDup.Armature.Select(a => a.FormKey).Should().Contain(f.WigArma.FormKey);
        skinDup.Armature.Select(a => a.FormKey).Should().NotContain(f.AntlerArma.FormKey);

        // Authored outfit (null LinkCache) carries the antler only.
        var outfit = f.OutputMod.Outfits.Single(o => o.FormKey == result.OutfitDuplicateKey!.Value);
        outfit.Items!.Select(i => i.FormKey).Should().Contain(f.AntlerArmor.FormKey);
        outfit.Items!.Select(i => i.FormKey).Should().NotContain(f.WigArmor.FormKey);

        var patchNpc = f.OutputMod.Npcs.AddNew();
        result.ApplyLinksTo(patchNpc);
        patchNpc.WornArmor.FormKey.Should().Be(result.SkinDuplicateKey!.Value);
        patchNpc.DefaultOutfit.FormKey.Should().Be(result.OutfitDuplicateKey!.Value);
    }

    [Fact]
    public void BothModesNone_DoesNothing()
    {
        var f = Make(WigHandlingMode.None, AntlerHandlingMode.None);
        Apply(f).Should().BeNull();
        f.OutputMod.Armors.Should().BeEmpty();
        f.OutputMod.Outfits.Should().BeEmpty();
    }

    [Fact]
    public void NoDetectedPieceInDonorOutfit_DoesNothing()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.ForwardToSkin);
        f.ModSetting.DetectedWigArmors.Clear();
        f.ModSetting.DetectedAntlerArmors.Clear();
        f.ModSetting.DetectedAntlerArmatures.Clear();
        f.ModSetting.DetectedWigArmors.Add(MutagenFixtures.Fk("0FFFFF:Other.esp")); // exists, but not in this NPC's outfit

        Apply(f).Should().BeNull();
        f.OutputMod.Armors.Should().BeEmpty();
    }

    [Fact]
    public void RepeatApply_ReusesTheSameDuplicate()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.ForwardToSkin);

        var first = Apply(f);
        var second = Apply(f);

        second!.SkinDuplicateKey.Should().Be(first!.SkinDuplicateKey);
        f.OutputMod.Armors.Should().HaveCount(1,
            "NPCs sharing the same WNAM + config + sets must share one +Wig duplicate");
    }

    [Fact]
    public void ForwardToSkin_CollectsHairHeadPartsAndShapeNames()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.None);

        var result = Apply(f)!;

        result.DonorHairHeadPartKeys.Should().BeEquivalentTo(new[] { f.HairHeadPart.FormKey },
            "only the Hair-type head part is superseded by the wig — eyes must stay");
        result.FaceGenShapeNamesToStrip.Should().BeEquivalentTo(new[] { "FoxGloveHairMesh", "FoxGloveHairline" },
            "the FaceGen strip needs the hair's EditorID plus its ExtraParts' EditorIDs");
    }

    [Fact]
    public void FinalizeNpcRecord_ReplacesHairHeadParts_WithSharedBaldRecord()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.None);
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
    }

    [Fact]
    public void AntlerOnlyForwarding_KeepsTheHairHeadPart()
    {
        var f = Make(WigHandlingMode.None, AntlerHandlingMode.ForwardToSkin);

        var result = Apply(f)!;

        result.SkinDuplicateKey.Should().NotBeNull("antlers still forward to the skin");
        result.DonorHairHeadPartKeys.Should().BeEmpty(
            "no hair-slot piece was transferred, so the NPC's real hair must survive (Option B specimen)");
        result.FaceGenShapeNamesToStrip.Should().BeEmpty();

        var patchNpc = f.OutputMod.Npcs.AddNew();
        patchNpc.HeadParts.Add(f.HairHeadPart.ToLink());
        f.Forwarder.FinalizeNpcRecord(result, patchNpc, "TestNpc", (_, _, _) => { });
        patchNpc.HeadParts.Select(h => h.FormKey).Should().Contain(f.HairHeadPart.FormKey);
        f.OutputMod.HeadParts.Should().BeEmpty("no bald record is generated when the real hair stays");
    }

    // ── The motivating case: forward the wig to skin, keep antlers OFF ──

    [Fact]
    public void WigForwardToSkin_AntlerRemove_WithIncludeOutfit_TheMotivatingCase()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.Remove);

        var result = Apply(f, mergeIn: false, includeOutfit: true)!;

        result.SkinDuplicateKey.Should().NotBeNull();
        result.OutfitDuplicateKey.Should().NotBeNull();

        // WNAM duplicate carries the wig ARMA but NOT the antler ARMA, and its
        // slot mask gained Hair/LongHair but NOT Circlet.
        var skinDup = f.OutputMod.Armors.Single();
        skinDup.Armature.Select(a => a.FormKey).Should().Contain(f.WigArma.FormKey);
        skinDup.Armature.Select(a => a.FormKey).Should().NotContain(f.AntlerArma.FormKey);
        skinDup.BodyTemplate!.FirstPersonFlags.Should().Be(
            BipedObjectFlag.Body | BipedObjectFlag.Hair | BipedObjectFlag.LongHair);

        // Forwarded outfit contains the dress only — wig (to skin) and antler
        // (Remove) both stripped.
        var outfit = f.OutputMod.Outfits.Single(o => o.FormKey == result.OutfitDuplicateKey!.Value);
        outfit.Items!.Select(i => i.FormKey).Should().BeEquivalentTo(new[] { f.DressArmor.FormKey });

        // Hair still supplied by the forwarded wig -> hair head part removed.
        result.DonorHairHeadPartKeys.Should().BeEquivalentTo(new[] { f.HairHeadPart.FormKey });
    }

    // ── Source 2: antler baked into the WornArmor ──

    [Fact]
    public void AntlerRemove_StripsBakedAntlerFromWornArmorDuplicate()
    {
        var f = Make(WigHandlingMode.None, AntlerHandlingMode.Remove, wnamBakedAntler: true);

        var result = Apply(f, mergeIn: false)!;

        result.SkinDuplicateKey.Should().NotBeNull(
            "a WornArmor duplicate is built even with nothing forwarded to skin, so the baked antler can be stripped");
        var skinDup = f.OutputMod.Armors.Single();
        skinDup.Armature.Select(a => a.FormKey).Should().BeEquivalentTo(new[] { f.SkinArma.FormKey },
            "the baked antler ArmorAddon is removed; the real skin ARMA stays");
        skinDup.Armature.Select(a => a.FormKey).Should().NotContain(f.WnamAntlerArma!.FormKey);
    }

    [Fact]
    public void AntlerForwardToSkin_LeavesBakedWornArmorAntlerInPlace()
    {
        var f = Make(WigHandlingMode.None, AntlerHandlingMode.ForwardToSkin, wnamBakedAntler: true);

        var result = Apply(f, mergeIn: false);

        // The outfit antler forwards to the skin dup; the baked WornArmor antler
        // is already in the skin, so it is not stripped.
        result!.SkinDuplicateKey.Should().NotBeNull();
        var skinDup = f.OutputMod.Armors.Single();
        skinDup.Armature.Select(a => a.FormKey).Should().Contain(f.WnamAntlerArma!.FormKey,
            "ForwardToSkin does not strip a WornArmor-baked antler");
    }

    // ── Source 3: antler baked into a FaceGen head part ──

    [Fact]
    public void AntlerRemove_CollectsAntlerHeadPart_ForRecordAndFaceGenRemoval()
    {
        var f = Make(WigHandlingMode.None, AntlerHandlingMode.Remove, faceGenAntlerHeadPart: true);

        var result = Apply(f, mergeIn: false)!;

        result.DonorAntlerHeadPartKeys.Should().BeEquivalentTo(new[] { f.AntlerHeadPart!.FormKey });
        result.FaceGenShapeNamesToStrip.Should().Contain("AuriAntlerHeadPart");
        // No hair replacement for an antler removal.
        result.DonorHairHeadPartKeys.Should().BeEmpty();

        var patchNpc = f.OutputMod.Npcs.AddNew();
        patchNpc.HeadParts.Add(f.HairHeadPart.ToLink());
        patchNpc.HeadParts.Add(f.AntlerHeadPart!.ToLink());
        f.Forwarder.FinalizeNpcRecord(result, patchNpc, "TestNpc", (_, _, _) => { });

        patchNpc.HeadParts.Select(h => h.FormKey).Should().BeEquivalentTo(new[] { f.HairHeadPart.FormKey },
            "the antler head part is removed with NO bald replacement; the real hair stays");
        f.OutputMod.HeadParts.Should().NotContain(h => h.EditorID == WigForwarder.BaldHairEditorId);
    }

    // ── Source 3: antlers baked as an ExtraPart of another head part (e.g. the
    //    hair), stripped per-shape. The ExtraPart must leave BOTH the baked NIF and
    //    the parent head-part record (a record/NIF ExtraParts mismatch dark-faces
    //    the NPC, engine-verified), so the parent head part is duplicated minus the
    //    ExtraPart. Scope decides shared (SeedDuplicateMapping) vs per-NPC repoint. ──

    [Fact]
    public void AntlerRemove_WholeHeadPart_RemovesRecord_AndStripsAllShapes()
    {
        // Designating the head part's OWN EditorID = "remove the whole head part":
        // record dropped + its own shape AND every ExtraPart shape stripped.
        var f = Make(WigHandlingMode.None, AntlerHandlingMode.Remove);
        f.Settings.AddManualAntlerHeadPart("FoxGloveHairMesh", f.ModSetting.DisplayName, f.DonorNpc.FormKey);

        var result = Apply(f, mergeIn: false)!;

        result.DonorAntlerHeadPartKeys.Should().BeEquivalentTo(new[] { f.HairHeadPart.FormKey });
        result.FaceGenShapeNamesToStrip.Should().Contain(new[] { "FoxGloveHairMesh", "FoxGloveHairline" },
            "removing the whole head part strips its own shape and every ExtraPart shape");
        result.AntlerHeadPartRepoints.Should().BeEmpty();

        var patchNpc = f.OutputMod.Npcs.AddNew();
        patchNpc.HeadParts.Add(f.HairHeadPart.ToLink());
        patchNpc.HeadParts.Add(f.EyesHeadPart.ToLink());
        f.Forwarder.FinalizeNpcRecord(result, patchNpc, "TestNpc", (_, _, _) => { });

        patchNpc.HeadParts.Select(h => h.FormKey).Should().BeEquivalentTo(new[] { f.EyesHeadPart.FormKey },
            "the whole head part is removed (no bald replacement — it is not Hair-slot wig removal)");
    }

    [Fact]
    public void AntlerRemove_ExtraPart_AllNpcsScope_DuplicatesParentAndSeedsSharedRemap()
    {
        // The antler is the hair head part's ExtraPart ("FoxGloveHairline"). Under
        // AllNpcs scope, the parent head part is duplicated (minus the ExtraPart) and
        // every reference redirected via SeedDuplicateMapping — the hair stays.
        var f = Make(WigHandlingMode.None, AntlerHandlingMode.Remove);
        f.Settings.ManualAntlerBlockScope = AntlerBlockScope.AllNpcs;
        f.Settings.AddManualAntlerHeadPart("FoxGloveHairline", f.ModSetting.DisplayName, f.DonorNpc.FormKey);

        var result = Apply(f, mergeIn: false)!;

        result.DonorAntlerHeadPartKeys.Should().BeEmpty("the hair head part stays; only its ExtraPart is stripped");
        result.FaceGenShapeNamesToStrip.Should().Contain("FoxGloveHairline");
        result.FaceGenShapeNamesToStrip.Should().NotContain("FoxGloveHairMesh");
        result.AntlerHeadPartRepoints.Should().BeEmpty("shared scope redirects via SeedDuplicateMapping, not a per-NPC repoint");

        // A stripped-hair duplicate (same EditorID, minus the antler ExtraPart) exists,
        var dup = f.OutputMod.HeadParts.Single(h =>
            string.Equals(h.EditorID, "FoxGloveHairMesh", System.StringComparison.OrdinalIgnoreCase));
        dup.FormKey.Should().NotBe(f.HairHeadPart.FormKey);
        dup.ExtraParts.Select(p => p.FormKey).Should().NotContain(f.HairlinePart.FormKey);
        dup.ExtraParts.Should().BeEmpty("the only ExtraPart was the stripped antler");

        // and every reference to the original hair head part redirects to it.
        f.RecordHandler.TryGetDuplicateMapping(f.HairHeadPart.FormKey, out var mapped).Should().BeTrue();
        mapped.Should().Be(dup.FormKey);
    }

    [Fact]
    public void AntlerRemove_ExtraPart_SpecificNpcScope_DuplicatesParentAndRepointsThisNpcOnly()
    {
        // Under SpecificNpc scope the parent head part is duplicated per-NPC and only
        // THIS NPC is repointed — no global remap, so other NPCs keep their antlers.
        var f = Make(WigHandlingMode.None, AntlerHandlingMode.Remove);
        f.Settings.ManualAntlerBlockScope = AntlerBlockScope.SpecificNpc;
        f.Settings.AddManualAntlerHeadPart("FoxGloveHairline", f.ModSetting.DisplayName, f.DonorNpc.FormKey);

        var result = Apply(f, mergeIn: false)!;

        result.AntlerHeadPartRepoints.Should().ContainKey(f.HairHeadPart.FormKey);
        var dupKey = result.AntlerHeadPartRepoints[f.HairHeadPart.FormKey];
        f.RecordHandler.TryGetDuplicateMapping(f.HairHeadPart.FormKey, out _).Should().BeFalse(
            "SpecificNpc must not globally redirect the head part — other NPCs keep their antlers");

        var dup = f.OutputMod.HeadParts.Single(h => h.FormKey == dupKey);
        dup.EditorID.Should().Be("FoxGloveHairMesh");
        dup.ExtraParts.Select(p => p.FormKey).Should().NotContain(f.HairlinePart.FormKey);

        // FinalizeNpcRecord repoints THIS NPC's hair head part to the duplicate.
        var patchNpc = f.OutputMod.Npcs.AddNew();
        patchNpc.HeadParts.Add(f.HairHeadPart.ToLink());
        patchNpc.HeadParts.Add(f.EyesHeadPart.ToLink());
        f.Forwarder.FinalizeNpcRecord(result, patchNpc, "TestNpc", (_, _, _) => { });

        patchNpc.HeadParts.Select(h => h.FormKey).Should().BeEquivalentTo(new[] { dupKey, f.EyesHeadPart.FormKey },
            "the original hair head part is repointed to the stripped duplicate for this NPC only");
    }

    // ── Include-Outfit strip variants ──

    [Fact]
    public void ForwardToSkin_WithIncludeOutfit_StripsBothClassesFromForwardedOutfit()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.ForwardToSkin);

        var result = Apply(f, mergeIn: false, includeOutfit: true);

        result!.SkinDuplicateKey.Should().NotBeNull();
        result.OutfitDuplicateKey.Should().NotBeNull(
            "the forwarded outfit must not carry pieces the skin now provides — a slot clash in game");

        var dup = f.OutputMod.Outfits.Single(o => o.FormKey == result.OutfitDuplicateKey!.Value);
        dup.EditorID.Should().Be("AuriOutfit");
        dup.Items!.Select(i => i.FormKey).Should().BeEquivalentTo(new[] { f.DressArmor.FormKey },
            "wig and antler items are removed; the rest of the outfit is preserved");

        var patchNpc = f.OutputMod.Npcs.AddNew();
        result.ApplyLinksTo(patchNpc);
        patchNpc.WornArmor.FormKey.Should().Be(result.SkinDuplicateKey!.Value);
        patchNpc.DefaultOutfit.FormKey.Should().Be(result.OutfitDuplicateKey!.Value);

        var second = Apply(f, mergeIn: false, includeOutfit: true);
        second!.OutfitDuplicateKey.Should().Be(result.OutfitDuplicateKey);
        f.OutputMod.Outfits.Should().HaveCount(1);
    }

    [Fact]
    public void ForwardToSkin_WithoutIncludeOutfit_LeavesTheOutfitAlone()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.ForwardToSkin);

        var result = Apply(f, mergeIn: false, includeOutfit: false);

        result!.SkinDuplicateKey.Should().NotBeNull();
        result.OutfitDuplicateKey.Should().BeNull(
            "with Include Outfit off the patcher does not touch outfits; the load order decides");
        f.OutputMod.Outfits.Should().BeEmpty();
    }

    [Fact]
    public void DuplicateReuse_StillCollectsHairRemovalForTheLaterNpc()
    {
        var f = Make(WigHandlingMode.ForwardToSkin, AntlerHandlingMode.None);

        var first = Apply(f);
        var second = Apply(f);

        second!.SkinDuplicateKey.Should().Be(first!.SkinDuplicateKey);
        second.DonorHairHeadPartKeys.Should().BeEquivalentTo(new[] { f.HairHeadPart.FormKey },
            "hair removal is per-NPC and must not be lost on a duplicate-reuse cache hit");
        second.FaceGenShapeNamesToStrip.Should().Contain("FoxGloveHairMesh");
    }
}
