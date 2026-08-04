using System.IO;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
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
/// <para><b>In SkyPatcher mode the output plugin is only half the artifact.</b> The NPC in the
/// user's load order is never overridden; everything asserted about the surrogate record below
/// reaches the actor only through the <c>.ini</c> NPC2 emits alongside it. So the SkyPatcher leg of
/// every route also opens the ini (<see cref="AssertCleanWrite"/> pins <c>copyVisualStyle</c> at the
/// very surrogate being inspected, and <see cref="AssignedOutfit"/> pins <c>outfitDefault=</c>),
/// or a break in that wiring would leave these tests green while nothing reached the game.</para>
///
/// <para><b>The mode axis.</b> <c>Settings.WigHandlingActiveForOutputMode</c> is
/// <c>UseSkyPatcherMode || PatchingMode == CreateAndPatch</c>, so wig handling is active in three
/// combinations, not two — plain Create record mode is the only inert one. Each route's
/// <c>[Theory]</c> covers the two Create-and-Patch ones; the third (SkyPatcher + plain Create) takes
/// a different Patcher branch — <c>CreateSkyPatcherNpc</c> with no <c>CopyAppearanceData</c> after
/// it — and is covered once, by <see cref="Route1c_ForwardToSkin_SkyPatcherCreateMode"/>, rather
/// than by doubling every theory.</para>
///
/// <para>All of these skip gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class WigRouteTwoModeTests
{
    private readonly ITestOutputHelper _output;

    public WigRouteTwoModeTests(ITestOutputHelper output) => _output = output;

    private static string Label(bool skyPatcherMode) => skyPatcherMode ? "skypatcher" : "record";

    /// <summary>The checks every route shares: the plugin was written, nothing in it references a
    /// plugin outside the load order, and — in SkyPatcher mode — the ini really does point the
    /// patched NPC at the surrogate the rest of the route then inspects.</summary>
    private void AssertCleanWrite(RouteRun run, FormKey target)
    {
        run.Log.Should().NotContain("FATAL SAVE ERROR",
            $"[{run.Label}] a dangling reference into an unloaded plugin makes the output unwritable");
        run.PluginExists.Should().BeTrue($"[{run.Label}] the patcher must write an output plugin");
        OutputLinkSweep.DumpRecords(run.Output, _output, run.Label);
        OutputLinkSweep.AssertNoLinksOutsideLoadOrder(run.Output, run.LoadOrderKeys, _output,
            $"[{run.Label}] the donor's records all live outside the load order, so every one the " +
            "patcher references must have been merged in");

        if (!run.SkyPatcherMode) return;

        // Nothing in SkyPatcher mode overrides the NPC the user actually has, so a surrogate that
        // no directive names is a record the game never reads. Tying copyVisualStyle to the very
        // record PatchedNpc returns is what makes the rest of this file's SkyPatcher assertions
        // statements about the actor rather than about an orphan.
        File.Exists(run.IniPath).Should().BeTrue(
            $"[{run.Label}] SkyPatcher mode delivers through the ini — expected one at {run.IniPath}");
        var directives = run.DirectivesFor(target);
        directives.Should().NotBeNull(
            $"[{run.Label}] the ini must carry a filterByNPCs line for the patched NPC ({target})");
        directives!.Should().ContainKey("copyVisualStyle",
            $"[{run.Label}] without it the surrogate's appearance never transfers to the NPC");
        SkyPatcherIniComparer.TryDirectiveFormKey(directives["copyVisualStyle"], out var surrogate)
            .Should().BeTrue($"[{run.Label}] copyVisualStyle must name a FormKey");
        surrogate.Should().Be(PatchedNpc(run).FormKey,
            $"[{run.Label}] the directive must point at the surrogate this route asserts against");
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

    /// <summary>
    /// The outfit the NPC will actually be wearing, and the assertion that it reaches them. Record
    /// mode assigns it by overriding the NPC, so the record IS the delivery. SkyPatcher mode does
    /// not: the surrogate's DefaultOutfit is inert cargo, and the outfit only arrives via the ini's
    /// <c>outfitDefault=</c>. Routes 3 and 7 reach here with Include Outfit OFF, so for them that
    /// directive exists solely because the wig forwarder asked for it (<c>Patcher</c>:
    /// <c>includeOutfit || wigForward.OutfitForwarded</c>) — the wiring that carries a forwarded wig
    /// to the actor, and which a surrogate-only assertion cannot see break.
    /// </summary>
    private static IOutfitGetter AssignedOutfit(RouteRun run, INpcGetter npc, FormKey target)
    {
        npc.DefaultOutfit.FormKey.ModKey.Should().Be(run.Output.ModKey,
            $"[{run.Label}] the NPC must wear an outfit the patcher owns");

        if (run.SkyPatcherMode)
        {
            var directives = run.DirectivesFor(target);
            directives.Should().NotBeNull($"[{run.Label}] the ini must carry a line for {target}");
            directives!.Should().ContainKey("outfitDefault",
                $"[{run.Label}] in SkyPatcher mode the outfit reaches the actor only through this " +
                "directive — the surrogate's own DefaultOutfit field is never read");
            SkyPatcherIniComparer.TryDirectiveFormKey(directives["outfitDefault"], out var delivered)
                .Should().BeTrue($"[{run.Label}] outfitDefault must name a FormKey");
            delivered.Should().Be(npc.DefaultOutfit.FormKey,
                $"[{run.Label}] the delivered outfit must be the one the run built for this NPC");
        }

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

        var donorHair = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Route_DonorHair", HeadPart.TypeEnum.Hair);

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

        AssertCleanWrite(run, npc.FormKey);

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

    /// <summary>
    /// Route 1's shape in the third combination wig handling is active in: SkyPatcher output with
    /// <c>PatchingMode.Create</c>. That is a genuinely different Patcher branch — the surrogate comes
    /// from <c>CreateSkyPatcherNpc</c> and <c>CopyAppearanceData</c> never runs afterwards — which
    /// makes it the WORST case for this file's defect class, since the traversal that incidentally
    /// merged the wig's armature in the fixed bug does not happen here at all. If the walker on the
    /// skin duplicate is the only thing merging that armature, this is where it shows.
    ///
    /// <para>Covered once rather than as a third <c>[InlineData]</c> on every route: the branch
    /// difference is in how the patched record is produced, which is upstream of everything the
    /// individual routes vary.</para>
    /// </summary>
    [Fact]
    public async Task Route1c_ForwardToSkin_SkyPatcherCreateMode()
    {
        using var fx = new WigRouteFixture("r1c");
        var npc = fx.AddBaseNpc("NPC2Route_R1c");

        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma);
        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        var wigArmo = fx.AddResArmor("NPC2Route_Wig", wigArma);
        var donorOutfit = fx.AddResOutfit("NPC2Route_DonorOutfit", wigArmo);

        var donorHair = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Route_DonorHair", HeadPart.TypeEnum.Hair);

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.DefaultOutfit.SetTo(donorOutfit);
        modNpc.HeadParts.Clear();
        modNpc.HeadParts.Add(donorHair.FormKey);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: true, "skypatcher-create",
            patchingMode: PatchingMode.Create);
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin;
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmors.Add(wigArmo.FormKey);
        Select(fx, settings, modSetting, npc.FormKey);

        using var run = await fx.RunAsync(settings, _output, "skypatcher-create");
        if (run == null) return;

        AssertCleanWrite(run, npc.FormKey);

        var outNpc = PatchedNpc(run);
        outNpc.WornArmor.FormKey.ModKey.Should().Be(run.Output.ModKey,
            "the surrogate must wear the +Wig duplicate, not the donor's original skin");
        var outSkin = run.Output.Armors.Single(a => a.FormKey == outNpc.WornArmor.FormKey);
        ArmatureEditorIds(run, outSkin).Should().BeEquivalentTo(
            new[] { "NPC2Route_BodyAA", "NPC2Route_WigAA" },
            "with no CopyAppearanceData in this branch, the walker on the duplicate is the only " +
            "thing that can merge the wig's armature");

        var hairEids = outNpc.HeadParts.Select(l =>
            run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID
            ?? $"(not in output: {l.FormKey})").ToList();
        hairEids.Should().Contain(WigForwarder.BaldHairEditorId);
        hairEids.Should().NotContain("NPC2Route_DonorHair", "the forwarded wig supplies the hair now");
    }

    // =============================================================================================
    // Route 2 — ForwardToSkin, skin-carried wig.
    // =============================================================================================

    /// <summary>
    /// A skin-carried wig ARMA is already where ForwardToSkin wants it, so nothing is transferred
    /// and no skin duplicate is built — but the CLASH that transfer path guards against is still
    /// there. A skin-carried hair-slot wig does not suppress head-part hair the way an equipped one
    /// does (Route 1's comment: "both meshes render and clash"), so the head-part hair still has to
    /// come off, keyed on the wig set the skin already carries.
    ///
    /// <para>This route used to be pinned as a pure no-op. It was reachable in practice only
    /// because mods that ship skin-carried wigs usually also ship a bald head part — where they
    /// don't, both meshes rendered.</para>
    ///
    /// <para>Worth pinning in both modes anyway: "nothing happened" and "the appearance copy
    /// silently dropped the wig" look identical from the outside, and only the second is a bug.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route2_ForwardToSkin_SkinCarriedWig_KeepsTheWigAndRemovesTheClashingHair(
        bool skyPatcherMode)
    {
        using var fx = new WigRouteFixture("r2");
        var npc = fx.AddBaseNpc("NPC2Route_R2");

        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma, wigArma);

        var donorHair = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Route_DonorHair", HeadPart.TypeEnum.Hair);

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

        AssertCleanWrite(run, npc.FormKey);

        var outNpc = PatchedNpc(run);
        outNpc.WornArmor.FormKey.ModKey.Should().Be(run.Output.ModKey, "the donor skin is merged in");
        var outSkin = run.Output.Armors.Single(a => a.FormKey == outNpc.WornArmor.FormKey);
        ArmatureEditorIds(run, outSkin).Should().BeEquivalentTo(
            new[] { "NPC2Route_BodyAA", "NPC2Route_WigAA" },
            "the skin-carried wig is already where ForwardToSkin wants it — it must survive untouched");

        // The wig stays on the skin, but the head-part hair it would clash with comes off and is
        // replaced by the modeless bald record — same end state as Route 1, reached without a
        // transfer.
        var hairEids = outNpc.HeadParts.Select(l =>
            run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID
            ?? $"(not in output: {l.FormKey})").ToList();
        hairEids.Should().Contain(WigForwarder.BaldHairEditorId,
            "removing the donor's hair without a modeless replacement back-fills a random race hair");
        hairEids.Should().NotContain("NPC2Route_DonorHair",
            "the skin-carried wig supplies the hair; leaving the head part on renders both");
    }

    /// <summary>
    /// The slot gate on Route 2's new hair removal. A skin-carried piece the scan flagged as a wig
    /// but which occupies a NON-hair slot (a circlet here) does not replace the NPC's hair, so
    /// removing the head part would leave them bald. Mirrors <c>BuildSkinDuplicate</c>'s
    /// <c>transfersHairSlot</c> test exactly, which is why both use <c>BipedObjectFlag.Hair</c>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route2b_ForwardToSkin_SkinCarriedNonHairSlotWig_KeepsTheHair(bool skyPatcherMode)
    {
        using var fx = new WigRouteFixture("r2b");
        var npc = fx.AddBaseNpc("NPC2Route_R2b");

        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var circletArma = fx.AddResArmorAddon("NPC2Route_CircletAA", BipedObjectFlag.Circlet);
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma, circletArma);

        var donorHair = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Route_DonorHair", HeadPart.TypeEnum.Hair);

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.HeadParts.Clear();
        modNpc.HeadParts.Add(donorHair.FormKey);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode));
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin;
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmatures.Add(circletArma.FormKey);
        Select(fx, settings, modSetting, npc.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode));
        if (run == null) return;

        AssertCleanWrite(run, npc.FormKey);

        var outNpc = PatchedNpc(run);
        var hairEids = outNpc.HeadParts.Select(l =>
            run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID
            ?? $"(not in output: {l.FormKey})").ToList();
        hairEids.Should().Contain("NPC2Route_DonorHair",
            "a circlet-slot piece does not replace the NPC's hair, so removing it would bald them");
        run.Output.HeadParts.Select(h => h.EditorID).Should().NotContain(WigForwarder.BaldHairEditorId,
            "no hair was removed, so nothing needs the modeless replacement");
    }

    /// <summary>
    /// The High Poly NPC Overhaul shape, and the specimen that motivated the geometry gate. A mod
    /// that already parks its wig on the skin pairs it with a MODELESS bald hair head part —
    /// HPNO's <c>HighPoly_HairBald</c>, verified from its record bytes as EDID + DATA + PNAM +
    /// RNAM with no MODL and no NAM0/NAM1. It renders nothing, so there is nothing for the
    /// skin-carried wig to clash with.
    ///
    /// <para>Route 2's hair removal used to fire on it anyway, because the collector took every
    /// Hair-type part regardless of geometry. That swapped the mod's placeholder for our own
    /// functionally identical <see cref="WigForwarder.BaldHairEditorId"/> — a no-op in game that
    /// nonetheless queued a FaceGen strip for a shape that was never baked (one "no shape named
    /// [...] found" warning per NPC, 3,431 on the measuring run) and read as an appearance
    /// mismatch to the validator (3,446 Errors on the same run, every NPC of the mod).</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route2c_ForwardToSkin_ModelessBaldHair_IsLeftAlone(bool skyPatcherMode)
    {
        using var fx = new WigRouteFixture("r2c");
        var npc = fx.AddBaseNpc("NPC2Route_R2c");

        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma, wigArma);

        var baldHair = MutagenFixtures.NewHeadPart(
            fx.ResMod, "NPC2Route_BaldHair", HeadPart.TypeEnum.Hair, modeless: true);

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.HeadParts.Clear();
        modNpc.HeadParts.Add(baldHair.FormKey);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode));
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin;
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmatures.Add(wigArma.FormKey);
        Select(fx, settings, modSetting, npc.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode));
        if (run == null) return;

        AssertCleanWrite(run, npc.FormKey);

        var outNpc = PatchedNpc(run);
        var hairEids = outNpc.HeadParts.Select(l =>
            run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID
            ?? ResHeadPartEditorId(fx, l.FormKey)).ToList();

        hairEids.Should().Contain("NPC2Route_BaldHair",
            "the donor's own modeless bald hair already satisfies the engine's 'must have a Hair part' " +
            "requirement — it renders nothing and cannot clash with the wig");
        run.Output.HeadParts.Select(h => h.EditorID).Should().NotContain(WigForwarder.BaldHairEditorId,
            "minting our identical bald record on top would leave the NPC with two Hair parts, and is " +
            "the ONLY thing the validator would then see as a difference");
    }

    /// <summary>
    /// The 13-NPC minority in the same measuring run: real hair AND the bald placeholder on one
    /// record. The real hair is genuinely superseded by the skin-carried wig and must come off,
    /// which proves the gate is per-head-part rather than an escape hatch for the whole NPC. The
    /// placeholder then stands in for the bald back-fill, so nothing is minted here either.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route2d_ForwardToSkin_RealHairPlusBaldPlaceholder_RemovesOnlyTheRealHair(
        bool skyPatcherMode)
    {
        using var fx = new WigRouteFixture("r2d");
        var npc = fx.AddBaseNpc("NPC2Route_R2d");

        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma, wigArma);

        var realHair = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Route_DonorHair", HeadPart.TypeEnum.Hair);
        var baldHair = MutagenFixtures.NewHeadPart(
            fx.ResMod, "NPC2Route_BaldHair", HeadPart.TypeEnum.Hair, modeless: true);

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.HeadParts.Clear();
        modNpc.HeadParts.Add(realHair.FormKey);
        modNpc.HeadParts.Add(baldHair.FormKey);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode));
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin;
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmatures.Add(wigArma.FormKey);
        Select(fx, settings, modSetting, npc.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode));
        if (run == null) return;

        AssertCleanWrite(run, npc.FormKey);

        var outNpc = PatchedNpc(run);
        var hairEids = outNpc.HeadParts.Select(l =>
            run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID
            ?? ResHeadPartEditorId(fx, l.FormKey)).ToList();

        hairEids.Should().NotContain("NPC2Route_DonorHair",
            "modeled hair IS superseded by the skin-carried wig — leaving it on renders both");
        hairEids.Should().Contain("NPC2Route_BaldHair",
            "the placeholder survives the removal and discharges the bald back-fill");
        run.Output.HeadParts.Select(h => h.EditorID).Should().NotContain(WigForwarder.BaldHairEditorId,
            "the donor already supplies a modeless bald hair; ours would be a second Hair part");
    }

    /// <summary>EditorID of a head part that stayed in the resource plugin (was not merged into the
    /// output), so an assertion can name it rather than printing a bare FormKey.</summary>
    private static string ResHeadPartEditorId(WigRouteFixture fx, FormKey key) =>
        fx.ResMod.HeadParts.FirstOrDefault(h => h.FormKey == key)?.EditorID
        ?? $"(unknown: {key})";

    /// <summary>The EditorIDs of <paramref name="outNpc"/>'s Hair-TYPE head parts, resolved through
    /// the output plugin first and the resource plugin second (a part the run did not merge is
    /// still on the record). Typed rather than name-matched: the minted parent's EditorID is built
    /// from the wig ARMA and its render shape names, so nothing about it is predictable.</summary>
    private static List<string> HairHeadPartEditorIds(RouteRun run, WigRouteFixture fx, INpcGetter outNpc) =>
        outNpc.HeadParts
            .Select(l => (IHeadPartGetter?)run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)
                         ?? fx.ResMod.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey))
            .Where(h => h?.Type == HeadPart.TypeEnum.Hair)
            .Select(h => h!.EditorID ?? "(no EditorID)")
            .ToList();

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

        AssertCleanWrite(run, npc.FormKey);

        var outNpc = PatchedNpc(run);
        var outfit = AssignedOutfit(run, outNpc, npc.FormKey);
        OutfitItemEditorIds(run, outfit).Should().Contain("NPC2Route_Wig",
            "the forwarded wig must be an item of the outfit the NPC wears, merged into the output");

        // ...and the merged wig ARMO must itself carry the merged armature, not the resource one.
        var outWig = OutputArmor(run, "NPC2Route_Wig");
        ArmatureEditorIds(run, outWig).Should().BeEquivalentTo(new[] { "NPC2Route_WigAA" });
    }

    /// <summary>
    /// ForwardToOutfit on an NPC that takes its whole inventory — outfit included — from a
    /// template. The forwarded outfit could never reach it, so the patcher converts the wig to head
    /// parts instead (head parts ride Traits data, which has no equivalent dead-field problem).
    ///
    /// <para>This is the MAJORITY path for ForwardToOutfit on a real load order, not an edge case:
    /// whole vanilla NPC classes are inventory-templated (generic Enc*/Treas*/Lvl* actors), which
    /// was 1,621 of 3,550 NPCs on the measuring run. The output validator resolved the wig mode per
    /// MOD and so never knew, reporting every one of those conversions as
    /// "HeadParts: extra [NPC2Wig_...]". The record assertions below are the patcher half; the
    /// validator half is <c>Settings.GetEffectiveWigModeForNpc</c>, which both sides now call —
    /// hence the template-flag assertion, which is the premise that lets the validator reach the
    /// same answer from the output record alone.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route3b_ForwardToOutfit_InertOutfitField_ConvertsToHeadParts(bool skyPatcherMode)
    {
        const string wigNifRecordPath = @"actors\NPC2Route\wig_1.nif";
        using var fx = new WigRouteFixture("r3b");
        var inventorySource = fx.AddBaseNpc("NPC2Route_R3bTemplate");
        var npc = fx.AddBaseNpc("NPC2Route_R3b");
        // The vanilla generic-actor shape: appearance of its own, inventory from a template.
        npc.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Inventory;
        npc.Template.SetTo(inventorySource.FormKey);

        // The full High Poly NPC Overhaul shape: wig carried on the SKIN, paired with a modeless
        // bald placeholder in the Hair slot.
        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        wigArma.WorldModel = new GenderedItem<Model?>(
            new Model { File = wigNifRecordPath }, new Model { File = wigNifRecordPath });
        // Named for the NPC's own race: the WNAM conversion declines an armature that is not
        // applicable, and the fixture's default (DefaultRace) only matches through the RACE
        // record's RNAM, which this test has no reason to depend on.
        wigArma.Race.SetTo(WigRouteFixture.NordRace);
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma, wigArma);

        var baldHair = MutagenFixtures.NewHeadPart(
            fx.ResMod, "NPC2Route_BaldHair", HeadPart.TypeEnum.Hair, modeless: true);

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.HeadParts.Clear();
        modNpc.HeadParts.Add(baldHair.FormKey);

        fx.WriteLooseFile(@"meshes\actors\NPC2Route\wig_1.nif", "dummy");
        fx.WriteLooseFile(@"meshes\actors\NPC2Route\wig_0.nif", "dummy");
        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode));
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToOutfit;
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmatures.Add(wigArma.FormKey);
        Select(fx, settings, modSetting, npc.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode), configure: h =>
        {
            var converter = h.HeadPartWigConverter;
            converter.RenderShapeNamesProvider = _ => new[] { "wigMain", "wigExtra" };
            converter.PartitionProbe = (_, _) => true;
            converter.PhysicsXmlProvider = _ => Array.Empty<string>();
        });
        if (run == null) return;

        AssertCleanWrite(run, npc.FormKey);

        var outNpc = PatchedNpc(run);
        var npcHeadPartEids = outNpc.HeadParts.Select(l =>
            run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID
            ?? ResHeadPartEditorId(fx, l.FormKey)).ToList();

        if (skyPatcherMode)
        {
            // SkyPatcher applies the outfit by runtime directive, which bypasses record-level
            // template resolution — the field is not dead there, so nothing routes around it.
            npcHeadPartEids.Should().NotContain(e => e != null && e.StartsWith("NPC2Wig_", StringComparison.Ordinal),
                "the outfit field is only inert in record mode");
            return;
        }

        npcHeadPartEids.Should().Contain(e => e != null && e.StartsWith("NPC2Wig_", StringComparison.Ordinal),
            "a wig forwarded into an outfit this NPC never wears would silently vanish, so it " +
            "becomes head parts instead");
        npcHeadPartEids.Should().NotContain("NPC2Route_BaldHair",
            "the minted parent IS the NPC's Hair part, so the donor's placeholder — which existed " +
            "only to satisfy the engine's 'must have a Hair part' rule — is now redundant");
        HairHeadPartEditorIds(run, fx, outNpc).Should().ContainSingle(
            "two Hair-type parts on one record is exactly what leaving the placeholder would cause");

        outNpc.Configuration.TemplateFlags.Should().HaveFlag(NpcConfiguration.TemplateFlag.Inventory);
        outNpc.Template.IsNull.Should().BeFalse(
            "the output record must still carry the flag+template pair, or the validator cannot " +
            "tell that this NPC was converted rather than forwarded");

        // The link that closes the loop: asked about the record the patcher actually wrote, the
        // shared resolver the validator consults returns the mode the patcher actually used. Read
        // off the real output rather than a synthetic record, so a patcher change that dropped the
        // flags would fail here instead of silently re-opening the false-positive flood.
        settings.GetEffectiveWigModeForNpc(modSetting, outNpc)
            .Should().Be(WigHandlingMode.ConvertToHeadParts,
                "the validator must reach the patcher's per-NPC verdict from the output record alone");
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

        var donorHair = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Route_DonorHair", HeadPart.TypeEnum.Hair);

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

        AssertCleanWrite(run, npc.FormKey);

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

        // ...and the copy of it that the appearance merge made anyway is gone. The merge walks the
        // ORIGINAL WornArmor (the seeded duplicate mapping stops the armor itself from being copied,
        // not the traversal of its links), so the stripped ARMA is copied and then referenced by
        // nothing — 129 of them in one measured run. Suppressing the walk instead is NOT safe (the
        // same record can be reachable from another copy, which would then point into a plugin
        // outside the load order and Mutagen refuses to write at all), so the sweep judges the
        // finished output: see Patcher.PruneAndLogOrphanedDuplicates.
        run.Output.ArmorAddons.Should().NotContain(a => a.EditorID == "NPC2Route_SkinWigAA",
            "a private copy of the superseded wig armature is referenced by nothing and must not ship");
        run.Output.ArmorAddons.Should().Contain(a => a.EditorID == "NPC2Route_BodyAA",
            "the body armature is still worn, so its copy stays");
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

        var antlerHdpt = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Route_AntlerHP", HeadPart.TypeEnum.Misc);                        // source 3
        var donorHair = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Route_DonorHair", HeadPart.TypeEnum.Hair);

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

        AssertCleanWrite(run, npc.FormKey);

        var outNpc = PatchedNpc(run);

        // Source 2 — stripped from the WornArmor duplicate.
        outNpc.WornArmor.FormKey.ModKey.Should().Be(run.Output.ModKey);
        var outSkin = run.Output.Armors.Single(a => a.FormKey == outNpc.WornArmor.FormKey);
        ArmatureEditorIds(run, outSkin).Should().BeEquivalentTo(new[] { "NPC2Route_BodyAA" },
            "the baked-in antler armature is removed from the skin");

        // Source 1 — stripped from the forwarded outfit, without losing the rest of it.
        var outfit = AssignedOutfit(run, outNpc, npc.FormKey);
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

        // Each removal leaves behind the private copy the merge had already made of it, referenced
        // by nothing once the record that pointed at it was rewritten. The orphan sweep removes
        // them, transitively — the donor outfit copy goes, and with it the antler ARMO copy that
        // only it referenced (see Patcher.PruneAndLogOrphanedDuplicates).
        run.Output.HeadParts.Should().NotContain(h => h.EditorID == "NPC2Route_AntlerHP",
            "the removed antler head part's copy is referenced by nothing");
        run.Output.ArmorAddons.Should().NotContain(a => a.EditorID == "NPC2Route_AntlerAA",
            "and neither is the stripped antler armature's");
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

        var donorHair = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Route_DonorHair", HeadPart.TypeEnum.Hair);

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

        AssertCleanWrite(run, npc.FormKey);

        var outNpc = PatchedNpc(run);

        // The wig went to the skin...
        var outSkin = run.Output.Armors.Single(a => a.FormKey == outNpc.WornArmor.FormKey);
        ArmatureEditorIds(run, outSkin).Should().Contain("NPC2Route_WigAA");

        // ...and came out of the forwarded outfit, which is otherwise intact.
        var outfit = AssignedOutfit(run, outNpc, npc.FormKey);
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

        AssertCleanWrite(run, npc.FormKey);

        run.Log.Should().Contain("falls back to ForwardToOutfit",
            "the fixture must actually take the no-WNAM fallback, not some other branch");

        var outNpc = PatchedNpc(run);
        outNpc.WornArmor.IsNull.Should().BeTrue("there was no skin to forward into");

        var outfit = AssignedOutfit(run, outNpc, npc.FormKey);
        OutfitItemEditorIds(run, outfit).Should().Contain("NPC2Route_Wig",
            "the fallback must still get the wig onto the NPC, via the outfit");
    }

    // =============================================================================================
    // Routes 8/9 — the flatten seam: ConvertToHeadParts and ForwardToSkin under GiveEachNpcOwnCopy.
    // =============================================================================================

    /// <summary>
    /// Under <c>GiveEachNpcOwnCopy</c> the flatten (<c>Auxilliary.CopyInheritedAppearance</c>) writes
    /// the TERMINUS's Traits-governed appearance onto the NPC's own record — head parts, WornArmor,
    /// race, sex, weight, hair colour. The wig pass runs BEFORE that copy, and it used to read all
    /// of those off the DONOR, so:
    /// <list type="bullet">
    /// <item>the hair it collected for removal was the donor's, and <c>FinalizeNpcRecord</c>'s
    /// <c>RemoveAll</c> then matched nothing — the terminus's hair survived alongside the minted
    /// wig parent (two heads of hair);</item>
    /// <item>the sex it minted for was the donor's, so a male donor with a female terminus baked the
    /// male wig mesh onto a female face.</item>
    /// </list>
    /// This fixture makes donor and terminus differ in BOTH, so either regression fails it.
    ///
    /// <para>No test in the repo ran <c>ConvertToHeadParts</c> and <c>GiveEachNpcOwnCopy</c>
    /// together before this one, which is how the defect survived.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route8_ConvertToHeadParts_Flattened_ReadsTheTerminus(bool skyPatcherMode)
    {
        const string wigNifRecordPath = @"actors\NPC2Route\wig_1.nif";
        using var fx = new WigRouteFixture("r8");

        // Terminus: female, its own hair, and the wig-bearing outfit. Donor: male, different hair.
        var terminus = fx.AddBaseNpc("NPC2Route_R8_Terminus");
        var donor = fx.AddTemplatedNpc("NPC2Route_R8", terminus, NpcConfiguration.TemplateFlag.Traits);

        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        wigArma.WorldModel = new GenderedItem<Model?>(
            new Model { File = wigNifRecordPath }, new Model { File = wigNifRecordPath });
        var wigArmo = fx.AddResArmor("NPC2Route_Wig", wigArma);
        var wigOutfit = fx.AddResOutfit("NPC2Route_WigOutfit", wigArmo);

        var terminusHair = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Route_TerminusHair", HeadPart.TypeEnum.Hair);

        var donorHair = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Route_DonorHair", HeadPart.TypeEnum.Hair);

        var modTerminus = fx.AppearanceMod.Npcs.GetOrAddAsOverride(terminus);
        modTerminus.Configuration.Flags |= NpcConfiguration.Flag.Female;
        modTerminus.HeadParts.Clear();
        modTerminus.HeadParts.Add(terminusHair.FormKey);

        var modDonor = fx.AppearanceMod.Npcs.GetOrAddAsOverride(donor);
        modDonor.Configuration.Flags &= ~NpcConfiguration.Flag.Female;
        modDonor.DefaultOutfit.SetTo(wigOutfit); // outfit is Inventory-governed — stays the donor's
        modDonor.HeadParts.Clear();
        modDonor.HeadParts.Add(donorHair.FormKey);

        fx.WriteLooseFile(@"meshes\actors\NPC2Route\wig_1.nif", "dummy");
        fx.WriteLooseFile(@"meshes\actors\NPC2Route\wig_0.nif", "dummy");
        // The ladder measures FaceGen at the TERMINUS's path and copies it to the NPC's own.
        fx.WriteFaceGen(terminus.FormKey);
        fx.WriteFaceGen(donor.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode),
            TemplateHandlingMode.GiveEachNpcOwnCopy);
        settings.DefaultWigHandlingMode = WigHandlingMode.ConvertToHeadParts;
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmors.Add(wigArmo.FormKey);
        Select(fx, settings, modSetting, donor.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode), configure: h =>
        {
            var converter = h.HeadPartWigConverter;
            converter.RenderShapeNamesProvider = _ => new[] { "wigMain", "wigExtra" };
            converter.PartitionProbe = (_, _) => true;
            converter.PhysicsXmlProvider = _ => Array.Empty<string>();
        });
        if (run == null) return;

        AssertCleanWrite(run, donor.FormKey);

        var outNpc = PatchedNpc(run);
        var npcHeadPartEids = outNpc.HeadParts.Select(l =>
            run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID
            ?? $"(not in output: {l.FormKey})").ToList();

        npcHeadPartEids.Should().Contain(e => e != null && e.StartsWith("NPC2Wig_", StringComparison.Ordinal),
            "the minted wig parent must be on the flattened record");
        npcHeadPartEids.Should().NotContain("NPC2Route_TerminusHair",
            "the flatten put the TERMINUS's hair on the record, so that is the hair the converter " +
            "has to remove — reading the donor's left it in place and rendered two heads of hair");

        // The minted set must be the FEMALE one: sex is Traits-governed, so it comes from the
        // terminus even though the donor record says male.
        var mintedParents = run.Output.HeadParts
            .Where(h => h.EditorID != null && h.EditorID.StartsWith("NPC2Wig_", StringComparison.Ordinal))
            .Select(h => h.EditorID!)
            .ToList();
        mintedParents.Should().NotBeEmpty();
        mintedParents.Should().Contain(e => e.Contains("_F_", StringComparison.Ordinal),
            "sex follows the terminus under a flatten, so the female wig set is the one to mint");
        mintedParents.Should().NotContain(e => e.Contains("_M_", StringComparison.Ordinal),
            "minting BOTH sexes would satisfy the check above while still proving the converter read " +
            "the donor's sex — the male set must not exist at all (EditorIDs are " +
            "NPC2Wig_<wig>_<F|M>_<shape>)");
    }

    /// <summary>
    /// The forwarder half of the same seam. <c>ApplyLinksTo</c> points the patched NPC's WornArmor
    /// at the skin duplicate — and that duplicate used to be built from the DONOR's WornArmor, which
    /// silently overwrote the TERMINUS's skin that <c>CopyInheritedAppearance</c> had just written.
    /// The NPC then wore the wrong body, not merely the wrong wig.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route9_ForwardToSkin_Flattened_UsesTheTerminusSkin(bool skyPatcherMode)
    {
        using var fx = new WigRouteFixture("r9");

        var terminus = fx.AddBaseNpc("NPC2Route_R9_Terminus");
        var donor = fx.AddTemplatedNpc("NPC2Route_R9", terminus, NpcConfiguration.TemplateFlag.Traits);

        // Two distinct skins, so which one the duplicate was built from is visible in the output.
        var terminusBodyArma = fx.AddResArmorAddon("NPC2Route_TerminusBodyAA", BipedObjectFlag.Body);
        var terminusSkin = fx.AddResArmor("NPC2Route_TerminusSkin", terminusBodyArma);
        var donorBodyArma = fx.AddResArmorAddon("NPC2Route_DonorBodyAA", BipedObjectFlag.Body);
        var donorSkin = fx.AddResArmor("NPC2Route_DonorSkin", donorBodyArma);

        var wigArma = fx.AddResArmorAddon("NPC2Route_WigAA");
        var wigArmo = fx.AddResArmor("NPC2Route_Wig", wigArma);
        var wigOutfit = fx.AddResOutfit("NPC2Route_WigOutfit", wigArmo);

        var terminusHair = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Route_TerminusHair", HeadPart.TypeEnum.Hair);

        var modTerminus = fx.AppearanceMod.Npcs.GetOrAddAsOverride(terminus);
        modTerminus.WornArmor.SetTo(terminusSkin);
        modTerminus.HeadParts.Clear();
        modTerminus.HeadParts.Add(terminusHair.FormKey);

        var modDonor = fx.AppearanceMod.Npcs.GetOrAddAsOverride(donor);
        modDonor.WornArmor.SetTo(donorSkin);
        modDonor.DefaultOutfit.SetTo(wigOutfit);

        fx.WriteFaceGen(terminus.FormKey);
        fx.WriteFaceGen(donor.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode, Label(skyPatcherMode),
            TemplateHandlingMode.GiveEachNpcOwnCopy);
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin;
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmors.Add(wigArmo.FormKey);
        Select(fx, settings, modSetting, donor.FormKey);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode));
        if (run == null) return;

        AssertCleanWrite(run, donor.FormKey);

        var outNpc = PatchedNpc(run);
        outNpc.WornArmor.FormKey.ModKey.Should().Be(run.Output.ModKey,
            "the NPC must wear the +Wig duplicate");
        var outSkin = run.Output.Armors.Single(a => a.FormKey == outNpc.WornArmor.FormKey);
        ArmatureEditorIds(run, outSkin).Should().BeEquivalentTo(
            new[] { "NPC2Route_TerminusBodyAA", "NPC2Route_WigAA" },
            "WornArmor is Traits-governed, so the duplicate must be built from the TERMINUS's skin — " +
            "building it from the donor's overwrote the flatten and gave the NPC the wrong body");

        // And the hair removal likewise targets the terminus's head parts.
        var hairEids = outNpc.HeadParts.Select(l =>
            run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID
            ?? $"(not in output: {l.FormKey})").ToList();
        hairEids.Should().Contain(WigForwarder.BaldHairEditorId);
        hairEids.Should().NotContain("NPC2Route_TerminusHair",
            "the flattened record carries the terminus's hair, so that is what the forwarded wig " +
            "supersedes");
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
