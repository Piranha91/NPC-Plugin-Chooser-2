using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using NPC_Plugin_Chooser_2.Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// <see cref="OutfitDisplayResolver"/> and the INVENTORY template flag.
///
/// <para>An NPC with that flag takes its whole inventory — the default outfit with it — from its
/// template, so the outfit its own record names is a dead field the engine never reads. The
/// resolver used to read <c>DefaultOutfit</c> straight off the record, which made the 3D preview
/// and the mugshot identity stamp depict an outfit the game would never put on, and made
/// 'Include Outfit' look like it had applied when it could not.</para>
///
/// <para>Distinct from the TRAITS chain the FaceGen ladder walks (covered by
/// <c>AppearanceTerminusTests</c> / the template matrix): the two flags are independent, and an NPC
/// may inherit one without the other. The last test here pins exactly that separation.</para>
///
/// <para>These need a real link cache, so they run against the fixture environment and skip
/// gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class OutfitDisplayInventoryTemplateTests
{
    private readonly ITestOutputHelper _output;

    public OutfitDisplayInventoryTemplateTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// One fixture shape reused by every case: a template NPC wearing <c>TemplateOutfit</c> and an
    /// heir wearing <c>OwnOutfit</c>, related by <paramref name="flag"/>.
    /// </summary>
    private sealed record Built(
        OutfitDisplayResolver Resolver,
        ModSetting ModSetting,
        FormKey Heir,
        FormKey Template,
        FormKey TemplateOutfit,
        FormKey OwnOutfit,
        FormKey OwnWigArmor);

    private Built? Build(WigRouteFixture fx, NpcConfiguration.TemplateFlag flag, Settings settings)
    {
        var templateOutfit = fx.AddResOutfit("NPC2Inv_TemplateOutfit",
            fx.AddResArmor("NPC2Inv_TemplateDress", fx.AddResArmorAddon("NPC2Inv_TemplateAA", BipedObjectFlag.Body)));

        // The heir's OWN outfit carries a detected wig; the template's does not. Which outfit the
        // resolver reads is therefore visible in the wig identity stamp as well as in the display.
        var ownWig = fx.AddResArmor("NPC2Inv_OwnWig", fx.AddResArmorAddon("NPC2Inv_OwnWigAA"));
        var ownOutfit = fx.AddResOutfit("NPC2Inv_OwnOutfit",
            fx.AddResArmor("NPC2Inv_OwnDress", fx.AddResArmorAddon("NPC2Inv_OwnAA", BipedObjectFlag.Body)),
            ownWig);

        var template = fx.AddBaseNpc("NPC2Inv_Template");
        var heir = fx.AddTemplatedNpc("NPC2Inv_Heir", template, flag);

        var modTemplate = fx.AppearanceMod.Npcs.GetOrAddAsOverride(template);
        modTemplate.DefaultOutfit.SetTo(templateOutfit);
        var modHeir = fx.AppearanceMod.Npcs.GetOrAddAsOverride(heir);
        modHeir.DefaultOutfit.SetTo(ownOutfit);

        fx.WriteFaceGen(heir.FormKey);
        fx.WriteFaceGen(template.FormKey);
        fx.WritePlugins();

        var provider = fx.TryBuildProvider(_output);
        if (provider == null) return null;

        var recordHandler = new RecordHandler(provider, new PluginProvider(provider, settings), settings);
        var modSetting = fx.NewModSetting();
        modSetting.DetectedWigArmors.Add(ownWig.FormKey);
        return new Built(
            new OutfitDisplayResolver(settings, provider, recordHandler),
            modSetting,
            heir.FormKey, template.FormKey, templateOutfit.FormKey, ownOutfit.FormKey, ownWig.FormKey);
    }

    /// <summary>
    /// The core case. The heir inherits its inventory, so the outfit it actually wears is the
    /// template's — not the one on its own record — and the result says the record field is inert
    /// and names where the inventory comes from.
    /// </summary>
    [Fact]
    public void InventoryTemplatedNpc_DepictsTheTemplatesOutfit_AndReportsTheRecordFieldInert()
    {
        using var fx = new WigRouteFixture("inv1");
        var settings = fx.NewSettings(skyPatcherMode: false, "inv1");
        var built = Build(fx, NpcConfiguration.TemplateFlag.Inventory, settings);
        if (built == null) return;

        var result = built.Resolver.ResolveForDisplay(built.Heir, built.Heir, built.ModSetting,
            includeDefaultOutfitRenderFlag: true);

        result.OutfitFormKey.Should().Be(built.TemplateOutfit,
            "the engine resolves the worn outfit through the inventory template, so that is what " +
            "the preview must draw");
        result.OutfitFormKey.Should().NotBe(built.OwnOutfit);
        result.RecordOutfitInert.Should().BeTrue();
        result.InventoryTemplateSource.Should().Be(built.Template);
    }

    /// <summary>
    /// With Include Outfit on, the write into the dead field cannot change what is worn — so the
    /// depiction stays the template's outfit and the user is told why, through the same
    /// <c>WarningText</c> surface the mugshot tiles and the 3D preview already render.
    /// </summary>
    [Fact]
    public void InventoryTemplatedNpc_IncludeOutfitOn_WarnsThatTheOutfitIsNeverWorn()
    {
        using var fx = new WigRouteFixture("inv2");
        var settings = fx.NewSettings(skyPatcherMode: false, "inv2");
        var built = Build(fx, NpcConfiguration.TemplateFlag.Inventory, settings);
        if (built == null) return;

        built.ModSetting.IncludeOutfits = true;

        var result = built.Resolver.ResolveForDisplay(built.Heir, built.Heir, built.ModSetting,
            includeDefaultOutfitRenderFlag: true);

        result.OutfitFormKey.Should().Be(built.TemplateOutfit,
            "'Include Outfit' writes DefaultOutfit, which this NPC never reads");
        result.RecordOutfitInert.Should().BeTrue();
        result.WarningText.Should().NotBeNull();
        result.WarningText.Should().Contain("takes its inventory from template");
    }

    /// <summary>
    /// SkyPatcher mode is exempt — its <c>outfitDefault=</c> directive acts on the actor and
    /// bypasses record-level template resolution entirely. Mirrors <c>Patcher.RecordOutfitIsInert</c>,
    /// which returns false in that mode for the same reason; the two must not drift apart.
    /// </summary>
    [Fact]
    public void InventoryTemplatedNpc_SkyPatcherMode_IsNotInert()
    {
        using var fx = new WigRouteFixture("inv3");
        var settings = fx.NewSettings(skyPatcherMode: true, "inv3");
        var built = Build(fx, NpcConfiguration.TemplateFlag.Inventory, settings);
        if (built == null) return;

        built.ModSetting.IncludeOutfits = true;

        var result = built.Resolver.ResolveForDisplay(built.Heir, built.Heir, built.ModSetting,
            includeDefaultOutfitRenderFlag: true);

        result.RecordOutfitInert.Should().BeFalse(
            "a SkyPatcher outfitDefault= directive reaches the actor whatever the record says");
        result.InventoryTemplateSource.Should().BeNull();
    }

    /// <summary>
    /// <see cref="OutfitDisplayResolver.ComputeWigIdentitySuffix"/> resolves the outfit
    /// INDEPENDENTLY of <c>ResolveForDisplay</c>, so the two can silently drift — and did: the stamp
    /// followed the inventory template chain unconditionally while the display exempted SkyPatcher
    /// mode. A stamp that disagrees with the depiction is the worst kind of bug here, because it
    /// caches the wrong PNG forever. These two pin the shared gate from both sides.
    ///
    /// <para>Record mode: the write is dead, the template's outfit is worn, and that outfit has no
    /// wig — so nothing is stamped.</para>
    /// </summary>
    [Fact]
    public void WigIdentityStamp_RecordMode_FollowsTheInventoryTemplate()
    {
        using var fx = new WigRouteFixture("inv5");
        var settings = fx.NewSettings(skyPatcherMode: false, "inv5");
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToOutfit;
        var built = Build(fx, NpcConfiguration.TemplateFlag.Inventory, settings);
        if (built == null) return;

        var display = built.Resolver.ResolveForDisplay(built.Heir, built.Heir, built.ModSetting, true);
        var suffix = built.Resolver.ComputeWigIdentitySuffix(built.Heir, built.ModSetting, true);

        display.OutfitFormKey.Should().Be(built.TemplateOutfit);
        suffix.Should().NotContain("+wig",
            "the depicted outfit is the template's, which carries no wig — stamping the heir's own " +
            "outfit would cache a render of a wig the game never puts on");
    }

    /// <summary>
    /// SkyPatcher mode: the <c>outfitDefault=</c> directive reaches the actor whatever the template
    /// flag says, so the heir's OWN outfit is worn — wig included — and the stamp must say so.
    /// </summary>
    [Fact]
    public void WigIdentityStamp_SkyPatcherMode_UsesTheNpcsOwnOutfit()
    {
        using var fx = new WigRouteFixture("inv6");
        var settings = fx.NewSettings(skyPatcherMode: true, "inv6");
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToOutfit;
        var built = Build(fx, NpcConfiguration.TemplateFlag.Inventory, settings);
        if (built == null) return;

        var display = built.Resolver.ResolveForDisplay(built.Heir, built.Heir, built.ModSetting, true);
        var suffix = built.Resolver.ComputeWigIdentitySuffix(built.Heir, built.ModSetting, true);

        display.RecordOutfitInert.Should().BeFalse("SkyPatcher mode is exempt");
        suffix.Should().Contain("+wig",
            "the runtime directive puts the heir's own outfit on the actor, so its wig IS depicted " +
            "and the stamp has to follow the same exemption ResolveForDisplay applies");
    }

    /// <summary>
    /// The separation that makes this a distinct walk rather than a reuse of the FaceGen ladder's:
    /// a TRAITS-templated NPC inherits its appearance but owns its inventory, so its own
    /// DefaultOutfit is live and must be depicted.
    /// </summary>
    [Fact]
    public void TraitsTemplatedNpc_KeepsItsOwnOutfit()
    {
        using var fx = new WigRouteFixture("inv4");
        var settings = fx.NewSettings(skyPatcherMode: false, "inv4");
        var built = Build(fx, NpcConfiguration.TemplateFlag.Traits, settings);
        if (built == null) return;

        built.ModSetting.IncludeOutfits = true;

        var result = built.Resolver.ResolveForDisplay(built.Heir, built.Heir, built.ModSetting,
            includeDefaultOutfitRenderFlag: true);

        result.OutfitFormKey.Should().Be(built.OwnOutfit,
            "Traits governs appearance, not inventory — this NPC wears the outfit its own record " +
            "names, so the Include Outfit write lands normally");
        result.RecordOutfitInert.Should().BeFalse();
        result.WarningText.Should().BeNull("nothing defeated the user's intent here");
    }
}
