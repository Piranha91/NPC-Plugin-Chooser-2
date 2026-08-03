using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// The FaceGen-baked antler segment of
/// <see cref="OutfitDisplayResolver.ComputeWigIdentitySuffix"/> — the only thing
/// that re-stales a cached mugshot when a mod's Antler Handling Mode flips to
/// Remove for antlers that live in HEAD PARTS rather than in an outfit ARMO.
///
/// <para>The bug these pin: the segment resolved head parts through the load-order
/// link cache alone. An appearance mod's plugin is normally NOT in the load order
/// — which is exactly what <see cref="WigRouteFixture"/> models — so the head
/// parts that mod defines resolved to nothing, the segment stayed empty, and the
/// stamp was byte-identical before and after the mode change. The tile therefore
/// looked current forever: even the AG button reused it (its forced re-render is
/// scoped to renders with missing assets), and only deleting the PNG produced a
/// correct image. The render itself was never wrong — NpcMeshResolver resolves the
/// same head parts mod-scoped — so this was purely a cache-invalidation miss.</para>
///
/// <para>The outfit-ARMO antler segment never had the bug because it is a plain
/// FormKey set-membership test that resolves no record, which is why the same mod
/// list showed the fault on one NPC and not on another.</para>
///
/// <para>Needs a real link cache, so it runs against the fixture environment and
/// skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class AntlerIdentityStampTests
{
    private readonly ITestOutputHelper _output;

    public AntlerIdentityStampTests(ITestOutputHelper output) => _output = output;

    private sealed record Built(
        OutfitDisplayResolver Resolver,
        ModSetting ModSetting,
        FormKey Npc,
        string AntlerEditorId,
        string AntlerExtraEditorId,
        string HairEditorId);

    /// <summary>
    /// An NPC whose appearance mod bakes an antler head part into its FaceGen. The antler
    /// (and its ExtraPart) live in <see cref="WigRouteFixture.ResMod"/>, which is NOT in
    /// the load order — the real-world shape, where the user selects a mod by folder and
    /// never enables its plugin.
    /// </summary>
    private Built? Build(WigRouteFixture fx, Settings settings)
    {
        var antlerExtra = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Antler_Tines", HeadPart.TypeEnum.Misc);
        var antler = MutagenFixtures.NewHeadPart(fx.ResMod, "NPC2Antler_Antlers", HeadPart.TypeEnum.Misc);
        antler.ExtraParts.Add(antlerExtra.FormKey.ToLink<IHeadPartGetter>());

        // AddBaseNpc gives the NPC a Hair head part in BaseMod, which IS in the load order.
        // It stays out of the stamp, proving the segment reports antlers rather than every
        // head part it managed to resolve.
        var npc = fx.AddBaseNpc("NPC2Antler_Npc");

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.HeadParts.Add(antler.FormKey);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var provider = fx.TryBuildProvider(_output);
        if (provider == null) return null;

        var recordHandler = new RecordHandler(provider, new PluginProvider(provider, settings), settings);
        var modSetting = fx.NewModSetting();
        modSetting.DetectedAntlerHeadParts.Add(antler.FormKey);

        return new Built(
            new OutfitDisplayResolver(settings, provider, recordHandler),
            modSetting, npc.FormKey,
            antler.EditorID!, antlerExtra.EditorID!, "NPC2Antler_Npc_Hair");
    }

    /// <summary>
    /// The core regression. Remove hides the baked antler shapes, so the stamp has to name
    /// them — resolved through the mod's own plugins, not the load order.
    /// </summary>
    [Fact]
    public void AntlerRemove_StampsTheHiddenShapes_WhenTheModsPluginIsNotInTheLoadOrder()
    {
        using var fx = new WigRouteFixture("antlerstamp1");
        var settings = fx.NewSettings(skyPatcherMode: false, "antlerstamp1");
        settings.DefaultAntlerHandlingMode = AntlerHandlingMode.Remove;
        var built = Build(fx, settings);
        if (built == null) return;

        var suffix = built.Resolver.ComputeWigIdentitySuffix(
            built.Npc, built.ModSetting, includeDefaultOutfitRenderFlag: false);
        _output.WriteLine("suffix: " + suffix);

        suffix.Should().Contain("+fgantler[",
            "a head-part antler is only reachable through the mod's own plugins — resolving it " +
            "against the load order alone silently emitted nothing and the tile never went stale");
        suffix.Should().Contain(built.AntlerEditorId);
        suffix.Should().Contain(built.AntlerExtraEditorId,
            "ExtraPart shapes are hidden with their parent, so they must be stamped with it");
        suffix.Should().NotContain(built.HairEditorId,
            "the segment names the antler shapes being hidden, not every head part on the NPC");
    }

    /// <summary>
    /// The staleness claim itself: flipping the mode to Remove must MOVE the stamp. This is
    /// the assertion that actually fails for the shipped bug — both sides were empty, so the
    /// cached mugshot compared equal and was reused with its antlers still on.
    /// </summary>
    [Fact]
    public void FlippingToRemove_DriftsTheIdentityStamp()
    {
        using var fx = new WigRouteFixture("antlerstamp2");
        var settings = fx.NewSettings(skyPatcherMode: false, "antlerstamp2");
        settings.DefaultAntlerHandlingMode = AntlerHandlingMode.None;
        var built = Build(fx, settings);
        if (built == null) return;

        var asIs = built.Resolver.ComputeWigIdentitySuffix(built.Npc, built.ModSetting, false);
        asIs.Should().NotContain("+fgantler", "Leave As Is draws the antlers, so nothing is hidden");

        settings.DefaultAntlerHandlingMode = AntlerHandlingMode.Remove;
        var removed = built.Resolver.ComputeWigIdentitySuffix(built.Npc, built.ModSetting, false);

        removed.Should().NotBe(asIs,
            "the depicted image changes, so the stamp must too — otherwise the staleness checker " +
            "reuses the antlered PNG and only deleting it produces a correct render");
    }

    /// <summary>
    /// The per-mod override has to reach the stamp the same way the global default does — the
    /// user hits this bug from either dropdown.
    /// </summary>
    [Fact]
    public void PerModOverride_DriftsTheIdentityStamp_IndependentlyOfTheGlobalDefault()
    {
        using var fx = new WigRouteFixture("antlerstamp3");
        var settings = fx.NewSettings(skyPatcherMode: false, "antlerstamp3");
        settings.DefaultAntlerHandlingMode = AntlerHandlingMode.None;
        var built = Build(fx, settings);
        if (built == null) return;

        var asIs = built.Resolver.ComputeWigIdentitySuffix(built.Npc, built.ModSetting, false);

        built.ModSetting.ModAntlerHandlingMode = AntlerHandlingMode.Remove;
        var removed = built.Resolver.ComputeWigIdentitySuffix(built.Npc, built.ModSetting, false);

        removed.Should().Contain("+fgantler[");
        removed.Should().NotBe(asIs);
    }

    /// <summary>
    /// Plain Create record mode cannot act on antlers at all, so the mode reads as inert and
    /// the depiction is unchanged — stamping there would re-render the library for an image
    /// that stays identical. Mirrors <see cref="Settings.WigHandlingActiveForOutputMode"/>.
    /// </summary>
    [Fact]
    public void PlainCreateRecordMode_StampsNothing_BecauseAntlerHandlingIsInert()
    {
        using var fx = new WigRouteFixture("antlerstamp4");
        var settings = fx.NewSettings(skyPatcherMode: false, "antlerstamp4",
            patchingMode: PatchingMode.Create);
        settings.DefaultAntlerHandlingMode = AntlerHandlingMode.Remove;
        var built = Build(fx, settings);
        if (built == null) return;

        built.Resolver.ComputeWigIdentitySuffix(built.Npc, built.ModSetting, false)
            .Should().NotContain("+fgantler");
    }

    // ---------------------------------------------------------------------
    // Outfit-carried antlers (source 1) and the mode-None DEPICTION segment.
    //
    // Antlers in a Default Outfit sit on a head slot, so the ordinary outfit walk
    // promotes them to headgear — and with the mugshot's attire toggles off they
    // disappeared entirely under "Leave As Is". The mugshot now draws them anyway
    // (PieceForward.Depiction), exactly as it already did for outfit wigs, and
    // +antlerdepict is what re-renders the tiles cached before that.
    // ---------------------------------------------------------------------

    private sealed record BuiltOutfit(
        OutfitDisplayResolver Resolver, ModSetting ModSetting, FormKey Npc, FormKey AntlerArmor);

    /// <summary>An NPC whose Default Outfit carries a detected antler ARMO and NO wig — so
    /// the mod's only reason to walk the outfit at all is the antler. That is the shape the
    /// stamp's both-modes-inert early bail used to discard.</summary>
    private BuiltOutfit? BuildOutfitAntler(WigRouteFixture fx, Settings settings)
    {
        var antler = fx.AddResArmor("NPC2Antler_OutfitAntlers",
            fx.AddResArmorAddon("NPC2Antler_OutfitAntlersAA"));
        var outfit = fx.AddResOutfit("NPC2Antler_Outfit",
            fx.AddResArmor("NPC2Antler_Dress", fx.AddResArmorAddon("NPC2Antler_DressAA", BipedObjectFlag.Body)),
            antler);

        var npc = fx.AddBaseNpc("NPC2Antler_OutfitNpc");
        fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc).DefaultOutfit.SetTo(outfit);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var provider = fx.TryBuildProvider(_output);
        if (provider == null) return null;

        var recordHandler = new RecordHandler(provider, new PluginProvider(provider, settings), settings);
        var modSetting = fx.NewModSetting();
        modSetting.DetectedAntlerArmors.Add(antler.FormKey);

        return new BuiltOutfit(
            new OutfitDisplayResolver(settings, provider, recordHandler),
            modSetting, npc.FormKey, antler.FormKey);
    }

    /// <summary>
    /// The core of this half: under Leave As Is the mugshot still draws the outfit antler, so
    /// the stamp has to say so or every already-cached PNG keeps its old antler-less render
    /// forever — nothing else about the NPC changed. Also pins the early-bail fix: this mod
    /// has no wigs and both modes are None, which used to return before reaching any segment.
    /// </summary>
    [Fact]
    public void OutfitAntler_LeaveAsIs_StampsTheAntlerTheMugshotDrawsAnyway()
    {
        using var fx = new WigRouteFixture("antlerstamp5");
        var settings = fx.NewSettings(skyPatcherMode: false, "antlerstamp5");
        settings.DefaultAntlerHandlingMode = AntlerHandlingMode.None;
        var built = BuildOutfitAntler(fx, settings);
        if (built == null) return;

        var suffix = built.Resolver.ComputeWigIdentitySuffix(built.Npc, built.ModSetting, false);
        _output.WriteLine("suffix: " + suffix);

        suffix.Should().Contain("+antlerdepict[",
            "the mugshot renders this antler despite the inert mode, so a PNG cached before the " +
            "change has to be re-rendered");
        suffix.Should().Contain(built.AntlerArmor.ToString(), "content-based on the antler's FormKey");
        suffix.Should().NotContain("+antler[",
            "nothing is being FORWARDED — the mode is None; this is a depiction-only segment");
    }

    /// <summary>
    /// The depiction is emitted with the outfit toggle OFF — that is the whole point, since the
    /// toggles are what hid the antler. Both toggle states must stamp it, or turning Include
    /// Outfit on and off would churn re-renders of an identical image.
    /// </summary>
    [Fact]
    public void OutfitAntler_LeaveAsIs_DepictionIgnoresTheOutfitToggle()
    {
        using var fx = new WigRouteFixture("antlerstamp6");
        var settings = fx.NewSettings(skyPatcherMode: false, "antlerstamp6");
        settings.DefaultAntlerHandlingMode = AntlerHandlingMode.None;
        var built = BuildOutfitAntler(fx, settings);
        if (built == null) return;

        var off = built.Resolver.ComputeWigIdentitySuffix(built.Npc, built.ModSetting, false);
        var on = built.Resolver.ComputeWigIdentitySuffix(built.Npc, built.ModSetting, true);

        off.Should().Contain("+antlerdepict[");
        on.Should().Be(off, "the depiction ignores both attire toggles, so neither may drift the stamp");
    }

    /// <summary>
    /// Depiction and forward are mutually exclusive: exactly one segment describes any tile.
    /// An active mode forwards, so nothing is depicted-only.
    /// </summary>
    [Fact]
    public void OutfitAntler_ActiveMode_StampsAForwardNotADepiction()
    {
        using var fx = new WigRouteFixture("antlerstamp7");
        var settings = fx.NewSettings(skyPatcherMode: false, "antlerstamp7");
        settings.DefaultAntlerHandlingMode = AntlerHandlingMode.ForwardToOutfit;
        var built = BuildOutfitAntler(fx, settings);
        if (built == null) return;

        var suffix = built.Resolver.ComputeWigIdentitySuffix(built.Npc, built.ModSetting, true);

        suffix.Should().Contain("+antler[ForwardToOutfit");
        suffix.Should().NotContain("+antlerdepict");
    }

    /// <summary>
    /// Plain Create record mode makes antler handling inert whatever the dropdown says, so an
    /// actively configured mode still renders — and stamps — as a depiction rather than a
    /// forward. Stamping a forward the output mode cannot perform would cache the wrong image.
    /// </summary>
    [Fact]
    public void OutfitAntler_PlainCreateMode_StampsADepictionNotAForward()
    {
        using var fx = new WigRouteFixture("antlerstamp8");
        var settings = fx.NewSettings(skyPatcherMode: false, "antlerstamp8",
            patchingMode: PatchingMode.Create);
        settings.DefaultAntlerHandlingMode = AntlerHandlingMode.ForwardToSkin;
        var built = BuildOutfitAntler(fx, settings);
        if (built == null) return;

        var suffix = built.Resolver.ComputeWigIdentitySuffix(built.Npc, built.ModSetting, true);

        suffix.Should().Contain("+antlerdepict[",
            "Create mode cannot forward the antler, so the mugshot depicts it unforwarded");
        suffix.Should().NotContain("+antler[ForwardToSkin");
    }
}
