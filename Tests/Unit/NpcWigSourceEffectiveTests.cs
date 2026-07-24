using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Locks <see cref="Settings.GetEffectiveNpcWigSources"/> — the read-time
/// filter over the scan-persisted per-NPC wig-source map
/// (<see cref="ModSetting.NpcWigSources"/>) that drives the NPC-menu "has wig"
/// badge. The map stores only the NPC→record ASSOCIATION; classification is
/// applied here: WornArmor candidates via <see cref="Settings.IsWigArmature"/>
/// (scan detection + manual promotions and vetoes), Outfit entries via
/// <see cref="ModSetting.DetectedWigArmors"/> membership. Pure persisted data —
/// no record resolution, no environment.
/// </summary>
public class NpcWigSourceEffectiveTests
{
    private const string ModName = "FoxGlove";
    private static readonly FormKey Npc = MutagenFixtures.Fk("000D63:018Auri.esp");
    private static readonly FormKey WigArmo = MutagenFixtures.Fk("000808:FoxGloveAuri.esp");
    private static readonly FormKey HairArma = MutagenFixtures.Fk("000900:FoxGloveAuri.esp");

    private static NpcWigSource OutfitEntry() => new()
        { Kind = NpcWigSourceKind.Outfit, RecordFormKey = WigArmo, EditorId = "FoxGloveWigArmo" };

    private static NpcWigSource WnamEntry() => new()
        { Kind = NpcWigSourceKind.WornArmor, RecordFormKey = HairArma, EditorId = "FoxGloveHairArma" };

    private static ModSetting Mod(bool armoDetected = true, bool armaDetected = true,
        params NpcWigSource[] entries)
    {
        var mod = new ModSetting { DisplayName = ModName };
        if (armoDetected) mod.DetectedWigArmors.Add(WigArmo);
        if (armaDetected) mod.DetectedWigArmatures.Add(HairArma);
        mod.NpcWigSources[Npc] = entries.ToList();
        return mod;
    }

    [Fact]
    public void NullModOrNoEntries_YieldsNothing()
    {
        var settings = new Settings();

        settings.GetEffectiveNpcWigSources(null, Npc).Should().BeEmpty();
        settings.GetEffectiveNpcWigSources(new ModSetting(), Npc).Should().BeEmpty();
        settings.GetEffectiveNpcWigSources(Mod(), Npc).Should().BeEmpty("no entries were stored");
    }

    [Fact]
    public void DetectedOutfitEntry_IsEffective()
    {
        var effective = new Settings().GetEffectiveNpcWigSources(Mod(entries: OutfitEntry()), Npc);

        effective.Should().ContainSingle();
        effective[0].Kind.Should().Be(NpcWigSourceKind.Outfit);
        effective[0].RecordFormKey.Should().Be(WigArmo);
    }

    [Fact]
    public void OutfitEntry_WhoseArmoIsNotDetected_IsFilteredOut()
    {
        // A stale association whose ARMO fell out of the detection set (e.g.
        // re-scan reclassified it) must not light the badge.
        new Settings().GetEffectiveNpcWigSources(
                Mod(armoDetected: false, entries: OutfitEntry()), Npc)
            .Should().BeEmpty();
    }

    [Fact]
    public void DetectedWnamEntry_IsEffective()
    {
        var effective = new Settings().GetEffectiveNpcWigSources(Mod(entries: WnamEntry()), Npc);

        effective.Should().ContainSingle();
        effective[0].Kind.Should().Be(NpcWigSourceKind.WornArmor);
    }

    [Fact]
    public void ManualVeto_RemovesDetectedWnamEntry()
    {
        var settings = new Settings();
        var mod = Mod(entries: WnamEntry());

        settings.GetEffectiveNpcWigSources(mod, Npc).Should().ContainSingle("detected before the veto");

        settings.AddManualWigArmature("FoxGloveHairArma", ModName, Npc, isWig: false);

        settings.GetEffectiveNpcWigSources(mod, Npc).Should().BeEmpty("the veto un-wigs the candidate");
    }

    [Fact]
    public void ManualPromotion_MakesUndetectedWnamCandidateEffective()
    {
        var settings = new Settings();
        var mod = Mod(armaDetected: false, entries: WnamEntry());

        settings.GetEffectiveNpcWigSources(mod, Npc).Should().BeEmpty("candidate only, not detected");

        settings.AddManualWigArmature("FoxGloveHairArma", ModName, Npc, isWig: true);

        settings.GetEffectiveNpcWigSources(mod, Npc).Should().ContainSingle("promotion wigs the candidate");
    }

    [Fact]
    public void EntryOrder_IsPreserved_WornArmorBeforeOutfit()
    {
        // The scan stores WornArmor entries before Outfit ones; the tile's
        // tooltip precedence relies on that order surviving the filter.
        var effective = new Settings().GetEffectiveNpcWigSources(
            Mod(entries: new[] { WnamEntry(), OutfitEntry() }), Npc);

        effective.Should().HaveCount(2);
        effective[0].Kind.Should().Be(NpcWigSourceKind.WornArmor);
        effective[1].Kind.Should().Be(NpcWigSourceKind.Outfit);
    }
}
