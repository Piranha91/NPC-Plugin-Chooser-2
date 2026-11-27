using System.IO;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.Integration;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// <see cref="SkyPatcherInterface.MirrorAppearance"/> and <see cref="SkyPatcherInterface.MirrorRace"/>:
/// the line an NPC is given when it takes its face from a patched NPC through its Traits template
/// (bench finding N11). Which NPCs get one is the patcher's decision
/// (<c>InheritedAppearanceMirrorTests</c>); these pin what the line holds, read back from the written
/// .ini. No game install: an Invalid provider with an in-memory output plugin, as in
/// <see cref="SkyPatcherInterfaceTests"/>.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class SkyPatcherInterfaceMirrorTests
{
    private static readonly FormKey Template = FormKey.Factory("0132A1:Skyrim.esm");
    private static readonly FormKey Twin = FormKey.Factory("0D0573:Skyrim.esm");
    private static readonly FormKey Surrogate = FormKey.Factory("00088B:NPCTest.esp");
    private static readonly FormKey Skin = FormKey.Factory("00088A:NPCTest.esp");
    private static readonly FormKey Race = FormKey.Factory("013741:Skyrim.esm");
    private static readonly FormKey Outfit = FormKey.Factory("000900:NPCTest.esp");
    private static readonly FormKey TwinOutfit = FormKey.Factory("000901:NPCTest.esp");

    private static SkyPatcherInterface NewInterface()
    {
        var env = NpcChooserTestEnvironment.Invalid();
        env.OutputMod = new SkyrimMod(ModKey.FromName("NPCTest", ModType.Plugin), SkyrimRelease.SkyrimSE);
        return new SkyPatcherInterface(env, new SurrogateNpcRegistry(env));
    }

    /// <summary>The template's line as the patcher queues it: appearance, race, sex, both outfits.</summary>
    private static void QueueTemplateLine(SkyPatcherInterface spi, bool withAppearance = true)
    {
        spi.AddNpc(Template);
        if (withAppearance)
        {
            spi.ApplyFace(Template, Surrogate);
            spi.ApplySkin(Template, Skin);
            spi.ApplyHeight(Template, 1f);
            spi.ApplyWeight(Template, 100f);
        }

        spi.ApplyRace(Template, Race);
        spi.ToggleGender(Template, Gender.Female);
        spi.SetOutfit(Template, Outfit);
        spi.SetSleepOutfit(Template, Outfit);
    }

    private static (Dictionary<FormKey, IReadOnlyDictionary<string, string>> Lines, string Text) Write(SkyPatcherInterface spi)
    {
        using var tmp = new TempDir("spimirror");
        spi.WriteIni(tmp.Path).Should().BeTrue();
        var path = Path.Combine(tmp.Path, "SKSE", "Plugins", "SkyPatcher", "npc", "NPC Plugin Chooser", "NPCTest.ini");
        return (SkyPatcherIniComparer.DirectivesByTarget(path), File.ReadAllText(path));
    }

    [Fact]
    public void MirrorAppearance_RepeatsTheTraitDirectives_AndClearsTheTraitsFlag()
    {
        var spi = NewInterface();
        QueueTemplateLine(spi);

        spi.MirrorAppearance(Template, Twin).Should().BeTrue();

        var (lines, text) = Write(spi);
        text.Should().Contain(
            "filterByNPCs=Skyrim.esm|D0573:copyVisualStyle=NPCTest.esp|88B:height=1:race=Skyrim.esm|13741:" +
            "removeTemplateFlags=traits:setFlags=female:skin=NPCTest.esp|88A:weight=100",
            "the template's trait directives with the inheriting NPC as the target, plus the traits clear; no outfit");
        lines[Twin].Should().NotContainKey("outfitDefault").And.NotContainKey("outfitSleep");
        lines[Template].Should().ContainKey("outfitDefault", "the template's own line is untouched");
        lines[Template].Should().NotContainKey("removeTemplateFlags");
    }

    [Fact]
    public void MirrorAppearance_KeepsWhatTheInheritingNpcAlreadyHad_ThatIsNotATrait()
    {
        var spi = NewInterface();
        QueueTemplateLine(spi);
        spi.AddNpc(Twin);
        spi.SetOutfit(Twin, TwinOutfit);                  // its own outfit stays
        spi.ApplyRace(Twin, FormKey.Factory("013746:Skyrim.esm")); // a trait of its own is replaced

        spi.MirrorAppearance(Template, Twin).Should().BeTrue();

        var twin = Write(spi).Lines[Twin];
        twin["outfitDefault"].Should().Be("NPCTest.esp|901");
        twin["race"].Should().Be("Skyrim.esm|13741", "one race directive, the template's");
        twin["copyVisualStyle"].Should().Be("NPCTest.esp|88B");
    }

    [Fact]
    public void MirrorAppearance_DoesNothing_ForAnNpcWithItsOwnAppearance_OrATemplateWithoutOne()
    {
        var spi = NewInterface();
        QueueTemplateLine(spi);
        var ownSurrogate = FormKey.Factory("000999:NPCTest.esp");
        spi.AddNpc(Twin);
        spi.ApplyFace(Twin, ownSurrogate);

        spi.MirrorAppearance(Template, Twin).Should().BeFalse("the NPC was patched itself");
        Write(spi).Lines[Twin].Should().Equal(new Dictionary<string, string> { ["copyVisualStyle"] = "NPCTest.esp|999" });

        var hybrid = NewInterface();
        QueueTemplateLine(hybrid, withAppearance: false);
        hybrid.MirrorAppearance(Template, Twin).Should().BeFalse("the template has no appearance line to repeat");
        hybrid.MirrorAppearance(Twin, Twin).Should().BeFalse();
        Write(hybrid).Lines.Should().NotContainKey(Twin);
    }

    [Fact]
    public void MirrorRace_RepeatsOnlyTheRace_AndOnlyWhenTheTemplatesLineHasOne()
    {
        // Recast Hybrid: the face travels through Recast, the ini carries race, outfits and keywords.
        var spi = NewInterface();
        QueueTemplateLine(spi, withAppearance: false);

        spi.MirrorRace(Template, Twin).Should().BeTrue();
        Write(spi).Lines[Twin].Should().Equal(new Dictionary<string, string> { ["race"] = "Skyrim.esm|13741" });

        var noRace = NewInterface();
        noRace.AddNpc(Template);
        noRace.SetOutfit(Template, Outfit);
        noRace.MirrorRace(Template, Twin).Should().BeFalse();
        Write(noRace).Lines.Should().NotContainKey(Twin);
    }
}
