using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;
using Counts = NPC_Plugin_Chooser_2.BackEnd.MergeInClassifier.Counts;
using Verdict = NPC_Plugin_Chooser_2.BackEnd.MergeInClassifier.Verdict;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="MergeInClassifier"/>: the NPC-provenance rule behind the "Merge Dependencies"
/// default and the dependency-folder keep logic. The Classify anchors below reproduce the
/// real mods that calibrated each parameter during the 2026-07 six-library validation
/// (see classifier_model/ next to the repo) — if a parameter change flips one of these,
/// the corresponding real-world mod regresses.
/// </summary>
public class MergeInClassifierTests
{
    private static Verdict Classify(int overrides, int news, int support, int hard, int sp = 0) =>
        MergeInClassifier.Classify(new Counts(overrides, news, support, hard), sp);

    // ---- Classify: no overrides -> base ------------------------------------------------------

    [Fact]
    public void Classify_NoOverridesNoSkyPatcher_IsBaseMod_AmuletsOfSkyrim()
    {
        // Amulets of Skyrim: 801 appearance-type records (the amulets) fooled the old
        // count-based heuristic; zero NPC overrides means nothing is being replaced.
        Classify(overrides: 0, news: 1, support: 800, hard: 521).Should().Be(Verdict.BaseMod);
    }

    [Fact]
    public void Classify_EmptyCounts_IsBaseMod()
    {
        Classify(0, 0, 0, 0).Should().Be(Verdict.BaseMod);
    }

    // ---- Classify: ratio test -----------------------------------------------------------------

    [Fact]
    public void Classify_NewNpcsDominate_IsBaseMod_Lawless()
    {
        // Lawless: 606 vanilla bandit overrides but 2373 new bandit variants -> variety
        // adder that stays in the load order, not a replacer.
        Classify(overrides: 606, news: 2373, support: 2380, hard: 820).Should().Be(Verdict.BaseMod);
    }

    [Fact]
    public void Classify_OverridesDominate_IsAppearanceReplacer_MenOfSkyrimShape()
    {
        // Men of Skyrim Refined: 467 overrides, few new NPCs, small hard tail.
        Classify(overrides: 467, news: 20, support: 400, hard: 36).Should().Be(Verdict.AppearanceReplacer);
    }

    [Fact]
    public void Classify_SkyPatcherTargets_BypassRatioTest_BijinRedux()
    {
        // Bijin Redux - SkyPatched: 47 template NPCs (donor payloads) vs 45 INI targets.
        // Without the bypass the templates would outvote their own targets.
        Classify(overrides: 0, news: 47, support: 350, hard: 0, sp: 45).Should().Be(Verdict.AppearanceReplacer);
    }

    // ---- Classify: volume guard ---------------------------------------------------------------

    [Fact]
    public void Classify_HardWithinFloorAllowance_IsAppearanceReplacer_BijinWarmaidens()
    {
        // Bijin Warmaidens: 22 overrides, 23 hard records (outfits etc.). MinHardAllowance
        // and the per-override bonus must keep this a replacer.
        Classify(overrides: 22, news: 0, support: 375, hard: 23).Should().Be(Verdict.AppearanceReplacer);
    }

    [Fact]
    public void Classify_BonusCapStopsContentVolume_ProjectAhoRequiemPatch()
    {
        // Requiem - Project AHO patch: 85 overrides would earn 340 hard records without the
        // cap; its 254 hard records must still classify base.
        Classify(overrides: 85, news: 10, support: 50, hard: 254).Should().Be(Verdict.BaseMod);
    }

    [Fact]
    public void Classify_EffTermJudgesMegaPatches_Ussep()
    {
        // USSEP: 952 NPC overrides riding on 8383 hard records -> base.
        Classify(overrides: 952, news: 5, support: 900, hard: 8383).Should().Be(Verdict.BaseMod);
    }

    [Fact]
    public void Classify_EffTermProtectsMegaReplacers()
    {
        // A 500-override replacer shipping 300 outfit/LVLI records must stay a replacer:
        // its allowance is its override count, above the 100-record bonus cap.
        Classify(overrides: 500, news: 0, support: 600, hard: 300).Should().Be(Verdict.AppearanceReplacer);
    }

    [Fact]
    public void Classify_ZeroSupportRecords_GetNoAllowance_SnazzyInteriorsShape()
    {
        // Snazzy Interiors Bryling's House: 2 NPC overrides with regenerated facegen but no
        // appearance-support records, plus 17 interior records -> location patch, base.
        Classify(overrides: 2, news: 1, support: 0, hard: 17).Should().Be(Verdict.BaseMod);
    }

    [Fact]
    public void Classify_SmallReplacerWithSupportRecords_KeepsFloorAllowance_GabShape()
    {
        // GAB-style small replacer: 1 override, a few headparts, 2 hard records.
        Classify(overrides: 1, news: 0, support: 8, hard: 2).Should().Be(Verdict.AppearanceReplacer);
    }

    // ---- CountPlugin: provenance attribution --------------------------------------------------

    [Fact]
    public void CountPlugin_AttributesNpcOriginByInternalKeySet()
    {
        var mod = MutagenFixtures.NewMod("Rep.esp");
        mod.Npcs.Add(new Npc(MutagenFixtures.Fk("000123:Skyrim.esm"), MutagenFixtures.Release));
        mod.Npcs.Add(new Npc(MutagenFixtures.Fk("000456:3DNPC.esp"), MutagenFixtures.Release));
        mod.Npcs.AddNew();
        mod.HeadParts.AddNew();
        mod.Weapons.AddNew();
        mod.Quests.AddNew();

        var counts = MergeInClassifier.CountPlugin(mod, new HashSet<ModKey> { mod.ModKey });

        counts.Should().Be(new Counts(OverrideNpcs: 2, NewNpcs: 1, SupportRecords: 1, HardRecords: 2));
    }

    [Fact]
    public void CountPlugin_IntraSetOverridesCountAsOwnNpcs()
    {
        // An NPC originating from another plugin of the SAME ModSetting (e.g. a bundled
        // foundation) is the mod's own NPC, not an external override.
        var mod = MutagenFixtures.NewMod("Rep.esp");
        mod.Npcs.Add(new Npc(MutagenFixtures.Fk("000123:Foundation.esp"), MutagenFixtures.Release));

        var counts = MergeInClassifier.CountPlugin(mod,
            new HashSet<ModKey> { mod.ModKey, MutagenFixtures.Mk("Foundation.esp") });

        counts.Should().Be(new Counts(OverrideNpcs: 0, NewNpcs: 1, SupportRecords: 0, HardRecords: 0));
    }

    // ---- CountSkyPatcherVisualTargets ---------------------------------------------------------

    [Fact]
    public void CountSkyPatcherVisualTargets_CountsDistinctTargetsOnVisualStyleLines()
    {
        using var tmp = new TempDir("skypatcher");
        var ini = tmp.Combine("SKSE", "Plugins", "SkyPatcher", "npc", "replacer.ini");
        System.IO.File.WriteAllLines(ini, new[]
        {
            "; comment",
            "filterByNpcs=Skyrim.esm|13BBF:copyVisualStyle=Templates.esp|800",
            "filterByNpcs=Skyrim.esm|13BBB,Skyrim.esm|13BBC:copyVisualStyle=Templates.esp|801",
            "filterByNpcs=Skyrim.esm|13BBF:copyVisualStyle=Templates.esp|802", // duplicate target
            "filterByNpcs=Skyrim.esm|99999:removeSpells=whatever",             // not a visual line
        });

        MergeInClassifier.CountSkyPatcherVisualTargets(new[] { tmp.Path }).Should().Be(3);
    }

    [Fact]
    public void CountSkyPatcherVisualTargets_NoIniFolder_ReturnsZero()
    {
        using var tmp = new TempDir("skypatcher");
        MergeInClassifier.CountSkyPatcherVisualTargets(new[] { tmp.Path }).Should().Be(0);
    }
}
