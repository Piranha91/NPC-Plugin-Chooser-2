using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// Screening for the one case SkyPatcher cannot deliver: an NPC that inherits its appearance through
/// a Traits template chain, while Templated NPCs is set to "Use the template's appearance".
///
/// <para>Such an NPC has no face of its own. The game resolves the chain natively and draws the
/// FaceGen belonging to the record at the END of it, so the FaceGen NPC2 writes under the surrogate's
/// own FormID is never opened — and once the appearance plugin is merged away, the terminus path
/// pairs a mod's mesh with a vanilla record. That is the dark-face bug reported for Captain Hargar
/// (01E38B -> 0DC8DA) and Woodcutter (02236C -> 0328DF -> 039D33).</para>
///
/// <para>Selecting an appearance for the terminus as well does not rescue it — in the reported case
/// both termini WERE selected — so the rejection is unconditional for a resolved chain, and the
/// remedy is "Give each NPC its own copy".</para>
///
/// <para>Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class SkyPatcherTemplatedScreeningTests
{
    private readonly ITestOutputHelper _output;

    public SkyPatcherTemplatedScreeningTests(ITestOutputHelper output) => _output = output;

    private const string RejectionMarker = "Templated NPC";

    /// <summary>Makes <paramref name="npc"/> inherit its appearance from <paramref name="template"/>.</summary>
    private static void InheritTraitsFrom(Npc npc, INpcGetter template)
    {
        npc.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
        npc.Template.SetTo(template.FormKey);
    }

    [Theory]
    // SkyPatcher + inherit: the case that cannot work.
    [InlineData(true, TemplateHandlingMode.InheritFromTemplate, false)]
    // SkyPatcher + flatten: Traits is cleared and the NPC owns its face, so it is patchable.
    [InlineData(true, TemplateHandlingMode.GiveEachNpcOwnCopy, true)]
    // Record mode is unaffected either way: the patched record keeps inheriting, and the engine reads
    // its head parts and its mesh from the same place.
    [InlineData(false, TemplateHandlingMode.InheritFromTemplate, true)]
    [InlineData(false, TemplateHandlingMode.GiveEachNpcOwnCopy, true)]
    public async Task TemplatedNpc_IsScreenedOutOnlyForSkyPatcherInherit(
        bool skyPatcherMode, TemplateHandlingMode templateMode, bool expectPatched)
    {
        using var fx = new WigRouteFixture("tmpl-screen");

        var template = fx.AddBaseNpc("NPC2Route_Template");   // owns its face
        var inheritor = fx.AddBaseNpc("NPC2Route_Inheritor");
        InheritTraitsFrom(inheritor, template);

        var bodyArma = fx.AddResArmorAddon("TS_BodyAA", BipedObjectFlag.Body);
        var skin = fx.AddResArmor("TS_Skin", bodyArma);
        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(inheritor);
        modNpc.WornArmor.SetTo(skin);

        // The terminus is where a templated NPC's FaceGen actually lives.
        fx.WriteFaceGen(template.FormKey);
        fx.WriteFaceGen(inheritor.FormKey);
        fx.WritePlugins();

        var label = $"{(skyPatcherMode ? "skypatcher" : "record")}-{templateMode}";
        var settings = fx.NewSettings(skyPatcherMode, label);
        settings.TemplateHandlingMode = templateMode;
        var modSetting = fx.NewModSetting();
        settings.ModSettings = new List<ModSetting> { modSetting };
        settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
        {
            [inheritor.FormKey] = (WigRouteFixture.ModName, inheritor.FormKey),
        };

        using var run = await fx.RunAsync(settings, _output, label);
        if (run == null) return;

        _output.WriteLine("INVALID: " + string.Join(" | ", run.Result.InvalidSelections));

        if (expectPatched)
        {
            run.Result.InvalidSelections.Should().NotContain(s => s.Contains(RejectionMarker),
                $"[{label}] this combination can deliver the appearance");
            run.Result.PatchedTargets.Should().Contain(inheritor.FormKey, $"[{label}] the NPC is patched");
        }
        else
        {
            run.Result.InvalidSelections.Should().ContainSingle(s => s.Contains(RejectionMarker),
                "SkyPatcher cannot apply an appearance through a template chain")
                .Which.Should().Contain("Give each NPC its own copy",
                    "the rejection must name the setting that fixes it");
            run.Result.PatchedTargets.Should().NotContain(inheritor.FormKey,
                "a screened-out NPC must not be patched");
            run.PluginExists.Should().BeFalse(
                "it was the only selection, so once it is screened out there is nothing left to write " +
                "— the NPC cannot reach the output by any path");
        }
    }

    /// <summary>
    /// The check is per NPC, not a global go/no-go: a templated NPC is dropped while an untemplated
    /// one in the same run, same mod, is still patched.
    /// </summary>
    [Fact]
    public async Task Screening_DropsOnlyTheTemplatedNpc_AndPatchesTheRest()
    {
        using var fx = new WigRouteFixture("tmpl-per-npc");

        var template = fx.AddBaseNpc("NPC2Route_Template");
        var inheritor = fx.AddBaseNpc("NPC2Route_Inheritor");
        InheritTraitsFrom(inheritor, template);
        var plain = fx.AddBaseNpc("NPC2Route_Plain");   // owns its face

        var bodyArma = fx.AddResArmorAddon("TS_BodyAA", BipedObjectFlag.Body);
        var skin = fx.AddResArmor("TS_Skin", bodyArma);
        foreach (var baseNpc in new[] { inheritor, plain })
        {
            fx.AppearanceMod.Npcs.GetOrAddAsOverride(baseNpc).WornArmor.SetTo(skin);
        }

        fx.WriteFaceGen(template.FormKey);
        fx.WriteFaceGen(inheritor.FormKey);
        fx.WriteFaceGen(plain.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: true, "per-npc");
        settings.TemplateHandlingMode = TemplateHandlingMode.InheritFromTemplate;
        var modSetting = fx.NewModSetting();
        settings.ModSettings = new List<ModSetting> { modSetting };
        settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
        {
            [inheritor.FormKey] = (WigRouteFixture.ModName, inheritor.FormKey),
            [plain.FormKey] = (WigRouteFixture.ModName, plain.FormKey),
        };

        using var run = await fx.RunAsync(settings, _output, "per-npc");
        if (run == null) return;

        _output.WriteLine("INVALID: " + string.Join(" | ", run.Result.InvalidSelections));

        run.Result.InvalidSelections.Should().ContainSingle(s => s.Contains(RejectionMarker),
            "exactly the templated NPC is rejected");
        run.Result.PatchedTargets.Should().NotContain(inheritor.FormKey);
        run.Result.PatchedTargets.Should().Contain(plain.FormKey,
            "the untemplated NPC in the same run must still be patched — this is a per-NPC check, " +
            "not a global abort");

        run.Log.Should().NotContain("FATAL SAVE ERROR");
        run.PluginExists.Should().BeTrue("the run proceeds with the valid selections");
        run.Output.Npcs.Should().ContainSingle().Which.EditorID.Should().Contain("Plain");
    }

    /// <summary>
    /// A multi-hop chain — the Woodcutter shape (02236C -> 0328DF -> 039D33), where the NPC's own
    /// template is itself templated. The walk has to follow through to the concrete terminus rather
    /// than stopping at the first hop.
    /// </summary>
    [Fact]
    public async Task MultiHopChain_IsAlsoScreenedOut()
    {
        using var fx = new WigRouteFixture("tmpl-multihop");

        var terminus = fx.AddBaseNpc("NPC2Route_Terminus");
        var middle = fx.AddBaseNpc("NPC2Route_Middle");
        InheritTraitsFrom(middle, terminus);
        var inheritor = fx.AddBaseNpc("NPC2Route_Inheritor");
        InheritTraitsFrom(inheritor, middle);

        var bodyArma = fx.AddResArmorAddon("TS_BodyAA", BipedObjectFlag.Body);
        var skin = fx.AddResArmor("TS_Skin", bodyArma);
        fx.AppearanceMod.Npcs.GetOrAddAsOverride(inheritor).WornArmor.SetTo(skin);

        fx.WriteFaceGen(terminus.FormKey);
        fx.WriteFaceGen(inheritor.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: true, "multihop");
        settings.TemplateHandlingMode = TemplateHandlingMode.InheritFromTemplate;
        var modSetting = fx.NewModSetting();
        settings.ModSettings = new List<ModSetting> { modSetting };
        settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
        {
            [inheritor.FormKey] = (WigRouteFixture.ModName, inheritor.FormKey),
        };

        using var run = await fx.RunAsync(settings, _output, "multihop");
        if (run == null) return;

        _output.WriteLine("INVALID: " + string.Join(" | ", run.Result.InvalidSelections));
        run.Result.InvalidSelections.Should().ContainSingle(s => s.Contains(RejectionMarker));
        run.Result.PatchedTargets.Should().NotContain(inheritor.FormKey);
    }

    /// <summary>
    /// The per-MOD override is honoured, so a user can keep Inherit globally and flatten just the mod
    /// whose NPCs need it. Same NPC, same global setting, opposite outcome.
    /// </summary>
    [Fact]
    public async Task PerModOverride_ToOwnCopy_LetsTheTemplatedNpcThrough()
    {
        using var fx = new WigRouteFixture("tmpl-permod");

        var template = fx.AddBaseNpc("NPC2Route_Template");
        var inheritor = fx.AddBaseNpc("NPC2Route_Inheritor");
        InheritTraitsFrom(inheritor, template);

        var bodyArma = fx.AddResArmorAddon("TS_BodyAA", BipedObjectFlag.Body);
        var skin = fx.AddResArmor("TS_Skin", bodyArma);
        fx.AppearanceMod.Npcs.GetOrAddAsOverride(inheritor).WornArmor.SetTo(skin);

        fx.WriteFaceGen(template.FormKey);
        fx.WriteFaceGen(inheritor.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: true, "permod");
        settings.TemplateHandlingMode = TemplateHandlingMode.InheritFromTemplate; // global says inherit
        var modSetting = fx.NewModSetting();
        modSetting.ModTemplateHandlingMode = TemplateHandlingMode.GiveEachNpcOwnCopy; // this mod says flatten
        settings.ModSettings = new List<ModSetting> { modSetting };
        settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
        {
            [inheritor.FormKey] = (WigRouteFixture.ModName, inheritor.FormKey),
        };

        using var run = await fx.RunAsync(settings, _output, "permod");
        if (run == null) return;

        _output.WriteLine("INVALID: " + string.Join(" | ", run.Result.InvalidSelections));
        run.Result.InvalidSelections.Should().NotContain(s => s.Contains(RejectionMarker),
            "the per-mod override flattens this mod's chains, so the NPC owns its face");
        run.Result.PatchedTargets.Should().Contain(inheritor.FormKey);
    }
}
