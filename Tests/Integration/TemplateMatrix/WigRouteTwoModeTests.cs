using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// Two-mode coverage for the wig/antler routes that had none — the routes listed in
/// <c>docs/WigPathTwoModeCoverage-Handoff-2026-07.md</c> §3.
///
/// <para>The defect class these guard against is not "a link nobody merged" (a structural audit
/// already ruled that out) but "a merge that only happens incidentally". <c>fb177cb</c>'s bug
/// survived for months because <c>CopyAppearanceData</c> happened to traverse the donor's WornArmor
/// and pick the same ArmorAddon up; that traversal is skipped in SkyPatcher mode, because
/// <c>DuplicateInOrAddFormLink</c> early-returns when the TARGET link is already mapped — which a
/// <c>DeepCopyIn</c> surrogate's links always are once something has seeded them.</para>
///
/// <para>So every route runs as a <c>[Theory]</c> over both output modes, and each asserts two
/// things: <see cref="OutputLinkSweep.AssertNoLinksOutsideLoadOrder"/> (which catches the whole
/// defect class at once, across every record the run produced), AND the route's intended visible
/// effect — otherwise a run that quietly produced nothing would pass as clean.</para>
///
/// <para>The axis is two modes, not three: <c>Settings.WigHandlingActiveForOutputMode</c> is
/// <c>UseSkyPatcherMode || PatchingMode == CreateAndPatch</c>, so wig handling is inert in plain
/// Create record mode.</para>
///
/// <para>All of these skip gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class WigRouteTwoModeTests
{
    private readonly ITestOutputHelper _output;

    public WigRouteTwoModeTests(ITestOutputHelper output) => _output = output;

    private static string Label(bool skyPatcherMode) => skyPatcherMode ? "skypatcher" : "record";

    /// <summary>The checks every route shares: the plugin was written, and nothing in it references a
    /// plugin outside the load order.</summary>
    private void AssertCleanWrite(RouteRun run)
    {
        run.Log.Should().NotContain("FATAL SAVE ERROR",
            $"[{run.Label}] a dangling reference into an unloaded plugin makes the output unwritable");
        run.PluginExists.Should().BeTrue($"[{run.Label}] the patcher must write an output plugin");
        OutputLinkSweep.DumpRecords(run.Output, _output, run.Label);
        OutputLinkSweep.AssertNoLinksOutsideLoadOrder(run.Output, run.LoadOrderKeys, _output,
            $"[{run.Label}] the donor's records all live outside the load order, so every one the " +
            "patcher references must have been merged in");
    }

    /// <summary>The single patched NPC — the winning-record override in record mode, the
    /// "_Template" surrogate in SkyPatcher mode.</summary>
    private static INpcGetter PatchedNpc(RouteRun run) =>
        run.Output.Npcs.Should().ContainSingle($"[{run.Label}] exactly one NPC is patched").Subject;

    private static IArmorGetter OutputArmor(RouteRun run, string editorId) =>
        run.Output.Armors.Should()
            .ContainSingle(a => a.EditorID == editorId, $"[{run.Label}] '{editorId}' must be in the output")
            .Subject;

    /// <summary>EditorIDs of the ArmorAddons an output Armor's Armature resolves to, so armature
    /// content can be asserted without depending on which FormKey the run allocated.</summary>
    private static IEnumerable<string?> ArmatureEditorIds(RouteRun run, IArmorGetter armor) =>
        armor.Armature.Select(l =>
            run.Output.ArmorAddons.FirstOrDefault(a => a.FormKey == l.FormKey)?.EditorID
            ?? $"(not in output: {l.FormKey})");

    /// <summary>EditorIDs of the records an output Outfit's items resolve to.</summary>
    private static IEnumerable<string?> OutfitItemEditorIds(RouteRun run, IOutfitGetter outfit) =>
        (outfit.Items ?? Enumerable.Empty<IFormLinkGetter<IOutfitTargetGetter>>()).Select(l =>
            run.Output.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == l.FormKey)?.EditorID
            ?? $"(load order: {l.FormKey})");

    private static IOutfitGetter AssignedOutfit(RouteRun run, INpcGetter npc)
    {
        npc.DefaultOutfit.FormKey.ModKey.Should().Be(run.Output.ModKey,
            $"[{run.Label}] the NPC must wear an outfit the patcher owns");
        return run.Output.Outfits.Single(o => o.FormKey == npc.DefaultOutfit.FormKey);
    }

    // =============================================================================================
    // Route 1 — ForwardToSkin, outfit-carried wig.
    // =============================================================================================

    /// <summary>
    /// The route most likely to share the fixed bug's shape: the wig ARMO lives in the donor's
    /// outfit, and its ArmorAddons are transferred into a duplicate of the donor's WornArmor. The
    /// duplicate is an OUTPUT record, so the merge walker will never recurse into it later — the
    /// armature has to be merged by the walker that runs on the duplicate at build time.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route1_ForwardToSkin_OutfitCarriedWig(bool skyPatcherMode)
    {
        using var fx = new WigRouteFixture("r1");
        var npc = fx.AddBaseNpc("NPC2Route_R1");

        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma);
        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        var wigArmo = fx.AddResArmor("NPC2Route_Wig", wigArma);
        var donorOutfit = fx.AddResOutfit("NPC2Route_DonorOutfit", wigArmo);

        var donorHair = fx.ResMod.HeadParts.AddNew();
        donorHair.EditorID = "NPC2Route_DonorHair";
        donorHair.Type = HeadPart.TypeEnum.Hair;

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.DefaultOutfit.SetTo(donorOutfit);
        modNpc.HeadParts.Clear();
        modNpc.HeadParts.Add(donorHair.FormKey);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode));
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin;
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmors.Add(wigArmo.FormKey);
        Select(fx, settings, modSetting, npc.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode));
        if (run == null) return;

        AssertCleanWrite(run);

        // Visible effect: the wig's ArmorAddon really did move onto the skin duplicate, and it points
        // at the MERGED copy rather than at the resource plugin.
        var outNpc = PatchedNpc(run);
        outNpc.WornArmor.FormKey.ModKey.Should().Be(run.Output.ModKey,
            "the NPC must wear the +Wig duplicate, not the donor's original skin");
        var outSkin = run.Output.Armors.Single(a => a.FormKey == outNpc.WornArmor.FormKey);
        ArmatureEditorIds(run, outSkin).Should().BeEquivalentTo(
            new[] { "NPC2Route_BodyAA", "NPC2Route_WigAA" },
            "the skin duplicate keeps the donor's own armature and gains the wig's");

        // A skin-carried hair-slot wig does not suppress head-part hair, so the donor hair is
        // replaced with the modeless bald record.
        var hairEids = outNpc.HeadParts.Select(l =>
            run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID
            ?? $"(not in output: {l.FormKey})").ToList();
        hairEids.Should().Contain(WigForwarder.BaldHairEditorId,
            "removing the donor's hair without a modeless replacement back-fills a random race hair");
        hairEids.Should().NotContain("NPC2Route_DonorHair", "the forwarded wig supplies the hair now");
    }

    // =============================================================================================
    // Route 2 — ForwardToSkin, skin-carried wig (documented no-op).
    // =============================================================================================

    /// <summary>
    /// A skin-carried wig is already in its ForwardToSkin end state, so the forwarder does nothing.
    /// Worth pinning in both modes anyway: "nothing happened" and "the appearance copy silently
    /// dropped the wig" look identical from the outside, and only the second is a bug.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route2_ForwardToSkin_SkinCarriedWig_IsANoOpButKeepsTheWig(bool skyPatcherMode)
    {
        using var fx = new WigRouteFixture("r2");
        var npc = fx.AddBaseNpc("NPC2Route_R2");

        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma, wigArma);

        var donorHair = fx.ResMod.HeadParts.AddNew();
        donorHair.EditorID = "NPC2Route_DonorHair";
        donorHair.Type = HeadPart.TypeEnum.Hair;

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.HeadParts.Clear();
        modNpc.HeadParts.Add(donorHair.FormKey);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode));
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin;
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmatures.Add(wigArma.FormKey);
        Select(fx, settings, modSetting, npc.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode));
        if (run == null) return;

        AssertCleanWrite(run);

        var outNpc = PatchedNpc(run);
        outNpc.WornArmor.FormKey.ModKey.Should().Be(run.Output.ModKey, "the donor skin is merged in");
        var outSkin = run.Output.Armors.Single(a => a.FormKey == outNpc.WornArmor.FormKey);
        ArmatureEditorIds(run, outSkin).Should().BeEquivalentTo(
            new[] { "NPC2Route_BodyAA", "NPC2Route_WigAA" },
            "the skin-carried wig is already where ForwardToSkin wants it — it must survive untouched");

        // No bald back-fill on this route: the forwarder never builds a skin duplicate here, so it
        // never collects hair removal. The donor's own hair stays.
        run.Output.HeadParts.Select(h => h.EditorID).Should().NotContain(WigForwarder.BaldHairEditorId,
            "no hair was removed, so nothing needs the modeless replacement");
        outNpc.HeadParts.Select(l =>
                run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID)
            .Should().Contain("NPC2Route_DonorHair", "the donor's hair head part is untouched");
    }

    // =============================================================================================
    // Route 3 — ForwardToOutfit, outfit-carried wig.
    // =============================================================================================

    /// <summary>
    /// The sibling of the path that broke. The wig ARMO is a donor outfit item rather than a skin
    /// armature, so instead of minting a wrapper ARMO the forwarder duplicates the outfit the NPC
    /// actually wears and adds the donor's wig ARMO to it — a raw link into the resource plugin
    /// placed on an output record. It survives only because the walker that runs on the duplicate
    /// runs AFTER the item is added.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route3_ForwardToOutfit_OutfitCarriedWig(bool skyPatcherMode)
    {
        using var fx = new WigRouteFixture("r3");
        // The NPC's base outfit is vanilla and the donor's is a different, mod-owned one, so the
        // effective outfit does NOT already contain the wig — otherwise the forwarder short-circuits.
        var npc = fx.AddBaseNpc("NPC2Route_R3");

        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma);
        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        var wigArmo = fx.AddResArmor("NPC2Route_Wig", wigArma);
        var donorOutfit = fx.AddResOutfit("NPC2Route_DonorOutfit", wigArmo);

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.DefaultOutfit.SetTo(donorOutfit);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode));
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToOutfit;
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmors.Add(wigArmo.FormKey);
        Select(fx, settings, modSetting, npc.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode));
        if (run == null) return;

        AssertCleanWrite(run);

        var outNpc = PatchedNpc(run);
        var outfit = AssignedOutfit(run, outNpc);
        OutfitItemEditorIds(run, outfit).Should().Contain("NPC2Route_Wig",
            "the forwarded wig must be an item of the outfit the NPC wears, merged into the output");

        // ...and the merged wig ARMO must itself carry the merged armature, not the resource one.
        var outWig = OutputArmor(run, "NPC2Route_Wig");
        ArmatureEditorIds(run, outWig).Should().BeEquivalentTo(new[] { "NPC2Route_WigAA" });
    }

    // =============================================================================================
    // Route 4 — ConvertToHeadParts.
    // =============================================================================================

    /// <summary>
    /// The converter mints HDPT records for the outfit wig AND strips the superseded skin-carried
    /// wig ARMA from the WornArmor duplicate, so both wig sources are in play in one run.
    ///
    /// <para>Its three NIF-reading seams are stubbed the same way <c>HeadPartWigConverterTests</c>
    /// stubs them — a synthetic fixture cannot supply parseable meshes, and without the stubs the
    /// converter declines and this would silently degrade into a ForwardToSkin test. Everything
    /// downstream of the seams (record minting, the WNAM strip, the NPC's head-part rewrite, and the
    /// merge behaviour this file exists to check) is the real code path.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route4_ConvertToHeadParts_BothWigSources(bool skyPatcherMode)
    {
        const string wigNifRecordPath = @"actors\NPC2Route\wig_1.nif";
        using var fx = new WigRouteFixture("r4");
        var npc = fx.AddBaseNpc("NPC2Route_R4");

        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var skinWigArma = fx.AddResArmorAddon("NPC2Route_SkinWigAA");
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma, skinWigArma);

        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        wigArma.WorldModel = new GenderedItem<Model?>(
            new Model { File = wigNifRecordPath }, new Model { File = wigNifRecordPath });
        var wigArmo = fx.AddResArmor("NPC2Route_Wig", wigArma);
        var donorOutfit = fx.AddResOutfit("NPC2Route_DonorOutfit", wigArmo);

        var donorHair = fx.ResMod.HeadParts.AddNew();
        donorHair.EditorID = "NPC2Route_DonorHair";
        donorHair.Type = HeadPart.TypeEnum.Hair;

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.DefaultOutfit.SetTo(donorOutfit);
        modNpc.HeadParts.Clear();
        modNpc.HeadParts.Add(donorHair.FormKey);

        fx.WriteLooseFile(@"meshes\actors\NPC2Route\wig_1.nif", "dummy");
        fx.WriteLooseFile(@"meshes\actors\NPC2Route\wig_0.nif", "dummy");
        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode));
        settings.DefaultWigHandlingMode = WigHandlingMode.ConvertToHeadParts;
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmors.Add(wigArmo.FormKey);
        modSetting.DetectedWigArmatures.Add(skinWigArma.FormKey);
        Select(fx, settings, modSetting, npc.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode), configure: h =>
        {
            var converter = h.HeadPartWigConverter;
            converter.RenderShapeNamesProvider = _ => new[] { "wigMain", "wigExtra" };
            converter.PartitionProbe = (_, _) => true;
            converter.PhysicsXmlProvider = _ => Array.Empty<string>();
        });
        if (run == null) return;

        AssertCleanWrite(run);

        var outNpc = PatchedNpc(run);
        var mintedParts = run.Output.HeadParts
            .Where(h => h.EditorID != null && h.EditorID.StartsWith("NPC2Wig_", StringComparison.Ordinal))
            .ToList();
        mintedParts.Should().NotBeEmpty("ConvertToHeadParts mints a HDPT set for the wig");

        var npcHeadPartEids = outNpc.HeadParts.Select(l =>
            run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID
            ?? $"(not in output: {l.FormKey})").ToList();
        npcHeadPartEids.Should().Contain(e => e != null && e.StartsWith("NPC2Wig_", StringComparison.Ordinal),
            "the minted parent head part replaces the donor's hair on the NPC record");
        npcHeadPartEids.Should().NotContain("NPC2Route_DonorHair",
            "the converted wig supersedes the donor's hair head part");

        // The other half of the route: the skin-carried wig ARMA is stripped from the WornArmor
        // duplicate so it cannot double-render against the baked head parts.
        outNpc.WornArmor.FormKey.ModKey.Should().Be(run.Output.ModKey);
        var outSkin = run.Output.Armors.Single(a => a.FormKey == outNpc.WornArmor.FormKey);
        ArmatureEditorIds(run, outSkin).Should().BeEquivalentTo(new[] { "NPC2Route_BodyAA" },
            "the converter's superseded skin wig armature is stripped from the duplicate");
    }

    // =============================================================================================
    // Route 5 — Antler Remove, all three sources.
    // =============================================================================================

    /// <summary>
    /// Antler <c>Remove</c> is the only mode that reaches all three places an antler can live: an
    /// item in the worn outfit, an ArmorAddon baked into the WornArmor, and a head part baked into
    /// the FaceGen. Include Outfit is ON because the outfit source is only reachable when there is a
    /// forwarded outfit to strip.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route5_AntlerRemove_AllThreeSources(bool skyPatcherMode)
    {
        using var fx = new WigRouteFixture("r5");
        var npc = fx.AddBaseNpc("NPC2Route_R5");

        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var antlerArma = fx.AddResArmorAddon("NPC2Route_AntlerAA", BipedObjectFlag.Circlet);
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma, antlerArma);   // source 2

        var antlerArmo = fx.AddResArmor("NPC2Route_AntlerArmor", antlerArma);
        var dress = fx.AddResArmor("NPC2Route_Dress", bodyArma);
        var donorOutfit = fx.AddResOutfit("NPC2Route_DonorOutfit", antlerArmo, dress); // source 1

        var antlerHdpt = fx.ResMod.HeadParts.AddNew();                        // source 3
        antlerHdpt.EditorID = "NPC2Route_AntlerHP";
        antlerHdpt.Type = HeadPart.TypeEnum.Misc;
        var donorHair = fx.ResMod.HeadParts.AddNew();
        donorHair.EditorID = "NPC2Route_DonorHair";
        donorHair.Type = HeadPart.TypeEnum.Hair;

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.DefaultOutfit.SetTo(donorOutfit);
        modNpc.HeadParts.Clear();
        modNpc.HeadParts.Add(donorHair.FormKey);
        modNpc.HeadParts.Add(antlerHdpt.FormKey);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode));
        settings.DefaultAntlerHandlingMode = AntlerHandlingMode.Remove;
        var modSetting = fx.NewModSetting();
        modSetting.IncludeOutfits = true;
        modSetting.DetectedAntlerArmors.Add(antlerArmo.FormKey);
        modSetting.DetectedAntlerArmatures.Add(antlerArma.FormKey);
        modSetting.DetectedAntlerHeadParts.Add(antlerHdpt.FormKey);
        Select(fx, settings, modSetting, npc.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode));
        if (run == null) return;

        AssertCleanWrite(run);

        var outNpc = PatchedNpc(run);

        // Source 2 — stripped from the WornArmor duplicate.
        outNpc.WornArmor.FormKey.ModKey.Should().Be(run.Output.ModKey);
        var outSkin = run.Output.Armors.Single(a => a.FormKey == outNpc.WornArmor.FormKey);
        ArmatureEditorIds(run, outSkin).Should().BeEquivalentTo(new[] { "NPC2Route_BodyAA" },
            "the baked-in antler armature is removed from the skin");

        // Source 1 — stripped from the forwarded outfit, without losing the rest of it.
        var outfit = AssignedOutfit(run, outNpc);
        var itemEids = OutfitItemEditorIds(run, outfit).ToList();
        itemEids.Should().NotContain("NPC2Route_AntlerArmor", "the outfit antler is removed");
        itemEids.Should().Contain("NPC2Route_Dress", "the rest of the outfit is preserved");

        // Source 3 — removed from the NPC record, with NO back-fill (an antler is not a required
        // head-part type the way hair is).
        var headPartEids = outNpc.HeadParts.Select(l =>
            run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID
            ?? $"(not in output: {l.FormKey})").ToList();
        headPartEids.Should().NotContain("NPC2Route_AntlerHP", "the antler head part is removed");
        headPartEids.Should().Contain("NPC2Route_DonorHair", "the NPC's real hair is untouched");
    }

    // =============================================================================================
    // Route 6 — Include Outfit ON: StripWigsFromForwardedOutfit.
    // =============================================================================================

    /// <summary>
    /// The duplicate-and-strip path that is only reachable when the ForwardToOutfit step did NOT
    /// already produce an outfit duplicate: wig goes to the skin, Include Outfit is on, so the
    /// donor's outfit is forwarded — with the wig taken back out of it, or the NPC would wear the
    /// wig on top of the one now baked into its skin.
    ///
    /// <para>Note on strength: with Include Outfit ON, <c>CopyAppearanceData</c> merges the donor's
    /// outfit in BOTH modes, and that traversal also reaches the wig ARMO and its armature. So this
    /// route's merge coverage is partly incidental by construction — deliberately breaking the skin
    /// duplicate's own merge does not make it fail. Route 1 is the same code path without that
    /// safety net, and is the one that pins the merge; this test pins the strip behaviour.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route6_IncludeOutfitOn_StripsTheWigFromTheForwardedOutfit(bool skyPatcherMode)
    {
        using var fx = new WigRouteFixture("r6");
        var npc = fx.AddBaseNpc("NPC2Route_R6");

        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma);
        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        var wigArmo = fx.AddResArmor("NPC2Route_Wig", wigArma);
        var dress = fx.AddResArmor("NPC2Route_Dress", bodyArma);
        var donorOutfit = fx.AddResOutfit("NPC2Route_DonorOutfit", wigArmo, dress);

        var donorHair = fx.ResMod.HeadParts.AddNew();
        donorHair.EditorID = "NPC2Route_DonorHair";
        donorHair.Type = HeadPart.TypeEnum.Hair;

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.DefaultOutfit.SetTo(donorOutfit);
        modNpc.HeadParts.Clear();
        modNpc.HeadParts.Add(donorHair.FormKey);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode));
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin;
        var modSetting = fx.NewModSetting();
        modSetting.IncludeOutfits = true;
        modSetting.DetectedWigArmors.Add(wigArmo.FormKey);
        Select(fx, settings, modSetting, npc.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode));
        if (run == null) return;

        AssertCleanWrite(run);

        var outNpc = PatchedNpc(run);

        // The wig went to the skin...
        var outSkin = run.Output.Armors.Single(a => a.FormKey == outNpc.WornArmor.FormKey);
        ArmatureEditorIds(run, outSkin).Should().Contain("NPC2Route_WigAA");

        // ...and came out of the forwarded outfit, which is otherwise intact.
        var outfit = AssignedOutfit(run, outNpc);
        var itemEids = OutfitItemEditorIds(run, outfit).ToList();
        itemEids.Should().NotContain("NPC2Route_Wig",
            "the wig moved to the skin, so wearing it as well would double-render it");
        itemEids.Should().Contain("NPC2Route_Dress", "the rest of the forwarded outfit is preserved");
    }

    // =============================================================================================
    // Route 7 — No-WNAM fallback (ForwardToSkin with no usable WornArmor -> ForwardToOutfit).
    // =============================================================================================

    /// <summary>
    /// With no WornArmor to transfer into, ForwardToSkin flips to ForwardToOutfit for that NPC. The
    /// interesting part is that the flip happens mid-run, so the outfit path executes with the skin
    /// path's configuration — worth confirming it merges the same way the deliberate
    /// ForwardToOutfit route does.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route7_NoWnam_FallsBackToForwardToOutfit(bool skyPatcherMode)
    {
        using var fx = new WigRouteFixture("r7");
        var npc = fx.AddBaseNpc("NPC2Route_R7");

        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        var wigArmo = fx.AddResArmor("NPC2Route_Wig", wigArma);
        var donorOutfit = fx.AddResOutfit("NPC2Route_DonorOutfit", wigArmo);

        // Deliberately NO WornArmor anywhere in the chain.
        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.DefaultOutfit.SetTo(donorOutfit);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode));
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin;
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmors.Add(wigArmo.FormKey);
        Select(fx, settings, modSetting, npc.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode));
        if (run == null) return;

        AssertCleanWrite(run);

        run.Log.Should().Contain("falls back to ForwardToOutfit",
            "the fixture must actually take the no-WNAM fallback, not some other branch");

        var outNpc = PatchedNpc(run);
        outNpc.WornArmor.IsNull.Should().BeTrue("there was no skin to forward into");

        var outfit = AssignedOutfit(run, outNpc);
        OutfitItemEditorIds(run, outfit).Should().Contain("NPC2Route_Wig",
            "the fallback must still get the wig onto the NPC, via the outfit");
    }

    private static void Select(WigRouteFixture fx, Settings settings, ModSetting modSetting, FormKey npcKey)
    {
        settings.ModSettings = new List<ModSetting> { modSetting };
        settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
        {
            [npcKey] = (WigRouteFixture.ModName, npcKey),
        };
    }
}
