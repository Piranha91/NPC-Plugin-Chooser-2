using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The two NPC-list verdicts <see cref="Auxilliary"/> memoises — the appearance-race verdict and the
/// "template chain ends in a Leveled NPC" verdict — must not depend on which mod was analysed first.
///
/// <para>Mod analysis runs in parallel, so the order varies between launches. Both caches used to be
/// keyed by FormKey alone while the verdict depended on the CALLER's plugins: a race only the
/// caller's own (out-of-load-order) plugins supply, or a template chain one mod re-points. The first
/// mod to be analysed then decided for every other one. Observed on a real instance:
/// <c>000D67:EMCompViljaSkyrim.esp</c> under Vilja Re-Imagined SSE (race supplied only by other Vilja
/// mods) and <c>0B28A5:Skyrim.esm</c> under Base Game / USSEP / Twisted's Lawless overhaul (which
/// re-points its template at a Leveled NPC).</para>
///
/// <para>Each case runs in both orders against a fresh <see cref="Auxilliary"/>; the verdicts must be
/// the same either way. In-memory Mutagen mods; no game install.</para>
/// </summary>
public class OrderIndependentNpcVerdictTests
{
    private static readonly ModKey BaseKey = ModKey.FromFileName("Base.esm");

    private static Auxilliary AuxOver(params SkyrimMod[] loadOrder)
    {
        var lo = new LoadOrder<IModListingGetter<ISkyrimModGetter>>();
        foreach (var mod in loadOrder)
        {
            lo.Add(new ModListing<ISkyrimModGetter>(mod, enabled: true));
        }

        var env = new EnvironmentStateProvider(null);
        env.UseInMemoryLoadOrderForTest(lo);
        return new Auxilliary(env);
    }

    private static Race FaceRace(SkyrimMod mod, string editorId)
    {
        var race = MutagenFixtures.NewRace(mod, editorId);
        race.Flags |= Race.Flag.FaceGenHead;
        return race;
    }

    // ---- Race verdict --------------------------------------------------------------------------

    /// <summary>
    /// A companion mod defines its own race and the companion NPC; it is NOT in the load order (the
    /// benchmark's effective load order lacked EMCompViljaSkyrim.esp the same way). A restyle mod
    /// overrides the NPC but ships no race. The companion mod can supply the race from its own plugin
    /// — offer it. The restyle mod cannot: the race exists neither in the load order nor in its plugins,
    /// so its output could not be written — reject it. Neither verdict may borrow from the other.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RaceSuppliedOnlyByAnotherMod_IsRejected_InEitherOrder(bool supplierFirst)
    {
        var baseMod = new SkyrimMod(BaseKey, MutagenFixtures.Release);
        var companion = MutagenFixtures.NewMod("Companion.esp");
        var race = FaceRace(companion, "CompanionRace");
        var companionNpc = MutagenFixtures.NewNpc(companion, "Companion", race: race);

        var restyle = MutagenFixtures.NewMod("Restyle.esp");
        var restyledNpc = restyle.Npcs.GetOrAddAsOverride(companionNpc);

        var aux = AuxOver(baseMod);

        bool SupplierVerdict() => aux.IsValidAppearanceRace(race.FormKey, companionNpc, null, out _, out _,
            sourcePluginRace: race);

        string restyleMessage = "";
        bool RestyleVerdict() => aux.IsValidAppearanceRace(race.FormKey, restyledNpc, null, out restyleMessage, out _);

        bool supplier, restyled;
        if (supplierFirst)
        {
            supplier = SupplierVerdict();
            restyled = RestyleVerdict();
        }
        else
        {
            restyled = RestyleVerdict();
            supplier = SupplierVerdict();
        }

        supplier.Should().BeTrue("the companion mod supplies the race from its own plugin");
        restyled.Should().BeFalse("no plugin the restyle mod has, and nothing in the load order, defines the race");
        restyleMessage.Should().Contain("not in the current load order");
    }

    /// <summary>
    /// Two mods each supply their own copy of the same out-of-load-order race, and the copies disagree
    /// about the FaceGen head. Each mod is judged on its own copy, whichever is analysed first.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuppliedRaceCopies_AreJudgedPerSupplier_InEitherOrder(bool headlessCopyFirst)
    {
        var baseMod = new SkyrimMod(BaseKey, MutagenFixtures.Release);
        var companion = MutagenFixtures.NewMod("Companion.esp");
        var race = FaceRace(companion, "CompanionRace");
        var npc = MutagenFixtures.NewNpc(companion, "Companion", race: race);

        var patch = MutagenFixtures.NewMod("CompanionCreaturePatch.esp");
        var headlessCopy = patch.Races.GetOrAddAsOverride(race);
        headlessCopy.Flags &= ~Race.Flag.FaceGenHead;

        var aux = AuxOver(baseMod);

        bool WithFace() => aux.IsValidAppearanceRace(race.FormKey, npc, null, out _, out _, sourcePluginRace: race);
        bool Headless() => aux.IsValidAppearanceRace(race.FormKey, npc, null, out _, out _, sourcePluginRace: headlessCopy);

        bool withFace, headless;
        if (headlessCopyFirst)
        {
            headless = Headless();
            withFace = WithFace();
        }
        else
        {
            withFace = WithFace();
            headless = Headless();
        }

        withFace.Should().BeTrue();
        headless.Should().BeFalse();
    }

    /// <summary>
    /// A race the load order resolves is judged on the load order's record, so its verdict is the same
    /// for every caller and may be shared — even a caller that hands in a different copy.
    /// </summary>
    [Fact]
    public void LoadOrderRace_IsJudgedOnTheLoadOrderRecord()
    {
        var baseMod = new SkyrimMod(BaseKey, MutagenFixtures.Release);
        var race = FaceRace(baseMod, "NordRace");
        var npc = MutagenFixtures.NewNpc(baseMod, "Nord", race: race);

        var stray = MutagenFixtures.NewMod("Stray.esp");
        var headlessCopy = stray.Races.GetOrAddAsOverride(race);
        headlessCopy.Flags &= ~Race.Flag.FaceGenHead;

        var aux = AuxOver(baseMod);

        aux.IsValidAppearanceRace(race.FormKey, npc, null, out _, out _, sourcePluginRace: headlessCopy)
            .Should().BeTrue("the load order's NordRace has a FaceGen head");
        aux.IsValidAppearanceRace(race.FormKey, npc, null, out _, out _).Should().BeTrue();
    }

    // ---- Leveled-terminus verdict --------------------------------------------------------------

    /// <summary>
    /// Shape of 0B28A5:Skyrim.esm: the base game templates an NPC (Traits) on an ordinary NPC; an
    /// overhaul outside the load order re-points the same NPC's template at a Leveled NPC. Judged on
    /// each mod's own record, the base game's chain is fine and the overhaul's ends in a Leveled NPC —
    /// whichever mod is analysed first. A load-order-only check (the NPC list's import path) sees the
    /// base record and must not inherit the overhaul's verdict either.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReTemplatedOverride_DoesNotDecideForOtherMods_InEitherOrder(bool overhaulFirst)
    {
        var baseMod = new SkyrimMod(BaseKey, MutagenFixtures.Release);
        var race = FaceRace(baseMod, "NordRace");
        var leveled = baseMod.LeveledNpcs.AddNew();
        leveled.EditorID = "LvlBanditWizard";
        var plain = MutagenFixtures.NewNpc(baseMod, "BanditWizardBase", race: race);
        var ambush = MutagenFixtures.NewNpc(baseMod, "dunAmbush", traitsTemplate: true, template: plain, race: race);

        var overhaul = MutagenFixtures.NewMod("LawlessOverhaul.esp");
        var overhauledAmbush = overhaul.Npcs.GetOrAddAsOverride(ambush);
        overhauledAmbush.Template.SetTo(leveled.FormKey);

        var aux = AuxOver(baseMod);

        bool BaseVerdict() => aux.TemplateChainTerminatesInLeveledNpc(ambush, new ISkyrimModGetter[] { baseMod });
        bool OverhaulVerdict() => aux.TemplateChainTerminatesInLeveledNpc(overhauledAmbush, new ISkyrimModGetter[] { overhaul });

        bool baseEndsLeveled, overhaulEndsLeveled;
        if (overhaulFirst)
        {
            overhaulEndsLeveled = OverhaulVerdict();
            baseEndsLeveled = BaseVerdict();
        }
        else
        {
            baseEndsLeveled = BaseVerdict();
            overhaulEndsLeveled = OverhaulVerdict();
        }

        baseEndsLeveled.Should().BeFalse("the base record's template is an ordinary NPC");
        overhaulEndsLeveled.Should().BeTrue("the overhaul re-points the template at a Leveled NPC");
        aux.TemplateChainTerminatesInLeveledNpc(ambush).Should().BeFalse(
            "the load order's record templates on an ordinary NPC, whatever an out-of-load-order mod did");
    }

    /// <summary>
    /// Verdicts a mod reaches are reused for that mod's other NPCs through its scope — one walk of a
    /// shared chain per mod — and still never leak to another mod's scope.
    /// </summary>
    [Fact]
    public void ScopeMemo_IsSharedWithinAModButNotAcrossMods()
    {
        var baseMod = new SkyrimMod(BaseKey, MutagenFixtures.Release);
        var race = FaceRace(baseMod, "NordRace");
        var leveled = baseMod.LeveledNpcs.AddNew();
        var plain = MutagenFixtures.NewNpc(baseMod, "Plain", race: race);
        var middle = MutagenFixtures.NewNpc(baseMod, "Middle", traitsTemplate: true, template: plain, race: race);
        var first = MutagenFixtures.NewNpc(baseMod, "First", traitsTemplate: true, template: middle, race: race);
        var second = MutagenFixtures.NewNpc(baseMod, "Second", traitsTemplate: true, template: middle, race: race);

        var overhaul = MutagenFixtures.NewMod("Overhaul.esp");
        overhaul.Npcs.GetOrAddAsOverride(middle).Template.SetTo(leveled.FormKey);

        var aux = AuxOver(baseMod);
        var overhaulScope = new Auxilliary.TemplateChainScope(new ISkyrimModGetter[] { overhaul });
        var baseScope = new Auxilliary.TemplateChainScope(new ISkyrimModGetter[] { baseMod });

        aux.TemplateChainTerminatesInLeveledNpc(first, overhaulScope).Should().BeTrue();
        aux.TemplateChainTerminatesInLeveledNpc(second, overhaulScope).Should().BeTrue(
            "the overhaul's Middle ends in a Leveled NPC, and the scope remembers it");
        aux.TemplateChainTerminatesInLeveledNpc(first, baseScope).Should().BeFalse(
            "the base game's Middle templates on an ordinary NPC");
    }
}
