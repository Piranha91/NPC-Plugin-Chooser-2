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
}
