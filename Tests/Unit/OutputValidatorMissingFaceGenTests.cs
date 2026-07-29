using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <c>OutputValidator.SubjectShouldHaveOwnFaceGen</c> — the gate in front of the "no FaceGen
/// anywhere" finding.
///
/// The finding itself is new: that case used to return silently, on the reasoning that it was
/// indistinguishable from vanilla-FaceGen-in-a-BSA. It is worth reporting now, but only for records
/// the engine actually builds a head for — and most records that legitimately have no FaceGen of
/// their own are exactly the templated and creature ones. Getting the exclusions wrong turns a new
/// signal into thousands of false rows, so they are pinned here.
///
/// Pure and deterministic: in-memory Mutagen records, no game install, no link cache.
/// </summary>
public class OutputValidatorMissingFaceGenTests
{
    /// <summary>Races resolve from the same in-memory mod; anything else is "unresolvable".</summary>
    private static bool ShouldHaveFaceGen(INpcGetter npc, SkyrimMod mod) =>
        OutputValidator.SubjectShouldHaveOwnFaceGen(
            npc,
            fk => mod.Races.FirstOrDefault(r => r.FormKey == fk));

    private static Race RaceWithFace(SkyrimMod mod, string editorId)
    {
        var race = MutagenFixtures.NewRace(mod, editorId);
        race.Flags |= Race.Flag.FaceGenHead;
        return race;
    }

    private static Npc NpcWithHead(SkyrimMod mod, string editorId, IRaceGetter race)
    {
        var npc = MutagenFixtures.NewNpc(mod, editorId, race: race);
        npc.HeadParts.Add(mod.HeadParts.AddNew().FormKey);
        return npc;
    }

    [Fact]
    public void OrdinaryNpc_OnAFaceGenHeadRace_ShouldHaveItsOwnFaceGen()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = NpcWithHead(mod, "Lydia", RaceWithFace(mod, "NordRace"));

        ShouldHaveFaceGen(npc, mod).Should().BeTrue();
    }

    [Fact]
    public void TraitsTemplatedNpc_IsExcluded()
    {
        // Its face belongs to the record at the end of the chain, and the Template rows already
        // explain that. Reporting "no FaceGen" here would blame an NPC for a design it did not choose.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var race = RaceWithFace(mod, "NordRace");
        var terminus = NpcWithHead(mod, "TheTemplate", race);
        var npc = MutagenFixtures.NewNpc(mod, "GenericBandit", traitsTemplate: true, template: terminus, race: race);
        npc.HeadParts.Add(mod.HeadParts.AddNew().FormKey);

        ShouldHaveFaceGen(npc, mod).Should().BeFalse();
    }

    [Fact]
    public void TemplateLinkWithoutTheTraitsFlag_IsNotExcluded()
    {
        // Inventory/AI inheritance does not redirect the face, so this NPC still renders its own.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var race = RaceWithFace(mod, "NordRace");
        var other = NpcWithHead(mod, "InventoryDonor", race);
        var npc = MutagenFixtures.NewNpc(mod, "Shopkeeper", template: other, race: race);
        npc.HeadParts.Add(mod.HeadParts.AddNew().FormKey);

        ShouldHaveFaceGen(npc, mod).Should().BeTrue();
    }

    [Fact]
    public void TraitsFlagWithoutATemplateLink_IsNotExcluded()
    {
        // The flag alone inherits nothing (IsValidTemplatedNpc requires the link), so the record's
        // own face is what renders and a missing FaceGen is a real defect.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, "FlagOnly", traitsTemplate: true, race: RaceWithFace(mod, "NordRace"));
        npc.HeadParts.Add(mod.HeadParts.AddNew().FormKey);

        ShouldHaveFaceGen(npc, mod).Should().BeTrue();
    }

    [Fact]
    public void RaceWithoutTheFaceGenHeadFlag_IsExcluded()
    {
        // The engine's own signal for "this actor gets a built head". Automatons, skeletons and
        // helmeted VIGILANT-class monsters fail it; they were the deliberate FaceGen-ladder aborts.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = NpcWithHead(mod, "ClockworkGilded", MutagenFixtures.NewRace(mod, "GildedRace"));

        ShouldHaveFaceGen(npc, mod).Should().BeFalse();
    }

    [Fact]
    public void UnresolvableRace_IsNotExcluded()
    {
        // The flag cannot be read, and a race that will not resolve is usually a missing master —
        // exactly the broken state this finding exists to surface, so fail loud rather than silent.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var otherMod = MutagenFixtures.NewMod("Elsewhere.esp");
        var npc = NpcWithHead(mod, "Orphan", RaceWithFace(otherMod, "UnreachableRace"));

        ShouldHaveFaceGen(npc, mod).Should().BeTrue();
    }

    [Fact]
    public void NpcWithNoHeadParts_IsExcluded()
    {
        // Nothing to build a face out of, so nothing can be missing.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, "Headless", race: RaceWithFace(mod, "NordRace"));

        ShouldHaveFaceGen(npc, mod).Should().BeFalse();
    }

    [Fact]
    public void NpcWithNullRace_FallsBackToTheHeadPartTest()
    {
        // A null race cannot be judged by the flag; the head-part test still applies.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var withHead = MutagenFixtures.NewNpc(mod, "NullRaceWithHead");
        withHead.HeadParts.Add(mod.HeadParts.AddNew().FormKey);
        var withoutHead = MutagenFixtures.NewNpc(mod, "NullRaceNoHead");

        ShouldHaveFaceGen(withHead, mod).Should().BeTrue();
        ShouldHaveFaceGen(withoutHead, mod).Should().BeFalse();
    }
}
