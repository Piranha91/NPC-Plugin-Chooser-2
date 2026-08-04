using System.Collections.Generic;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <c>Patcher.CollectHeadPartShapeRenames</c> — decides which of a patched NPC's head parts need
/// their baked FaceGen shape renamed to follow a record this run duplicated under a new EditorID.
///
/// <para>Include As New mints <c>&lt;original&gt;_&lt;sourcePlugin&gt;</c> so one mod's copy of a
/// shared record stays off every other mod's NPCs, but the engine pairs head parts to baked
/// geometry BY NAME — leaving the record pointing at a shape the mesh doesn't carry. Assur and
/// Svari were confirmed dark-faced in game. This map is what the post-copy NIF pass consumes.</para>
///
/// <para>The selection rules are what matter and what this pins: only records in the OUTPUT
/// plugin, only ones with recorded provenance, only ones whose EditorID actually changed. Getting
/// any of those wrong renames a shape that should have been left alone — which breaks the pairing
/// in the other direction.</para>
///
/// <para>In-memory Mutagen records and a bare RecordHandler; no game install, no disk.</para>
/// </summary>
public class PatcherHeadPartRenameMapTests
{
    private static readonly ModKey OutputKey = ModKey.FromFileName("NPC.esp");
    private static readonly ModKey SourceKey = ModKey.FromFileName("RSkyrimChildren.esm");

    private sealed class Fixture
    {
        public required Patcher Patcher { get; init; }
        public required RecordHandler RecordHandler { get; init; }
        public required SkyrimMod OutputMod { get; init; }
        public required SkyrimMod SourceMod { get; init; }
    }

    private static Fixture Build()
    {
        var outputMod = new SkyrimMod(OutputKey, SkyrimRelease.SkyrimSE);
        var sourceMod = new SkyrimMod(SourceKey, SkyrimRelease.SkyrimSE);

        var recordHandler = new RecordHandler(null!, null!, new Settings());

        var env = Reflect.Uninitialized<EnvironmentStateProvider>();
        Reflect.SetField(env, "OutputMod", outputMod);

        var patcher = Reflect.Uninitialized<Patcher>();
        Reflect.SetField(patcher, "_environmentStateProvider", env);
        Reflect.SetField(patcher, "_recordHandler", recordHandler);

        return new Fixture
        {
            Patcher = patcher, RecordHandler = recordHandler,
            OutputMod = outputMod, SourceMod = sourceMod,
        };
    }

    /// <summary>Mints an output head part the way Include As New does — duplicated from a source
    /// record, EditorID suffixed — and registers its provenance.</summary>
    private static HeadPart MintDuplicate(Fixture fx, string originalEditorId)
    {
        var source = fx.SourceMod.HeadParts.AddNew();
        source.EditorID = originalEditorId;

        var duplicate = fx.OutputMod.HeadParts.AddNew();
        fx.RecordHandler.RecordMergedRecordOrigin(source.FormKey, duplicate.FormKey, source.EditorID);
        duplicate.EditorID = originalEditorId + "_" + SourceKey.FileName;
        return duplicate;
    }

    private static Dictionary<string, string> Collect(Fixture fx, Npc npc) =>
        Reflect.Invoke<Dictionary<string, string>>(fx.Patcher, "CollectHeadPartShapeRenames", npc)!;

    private static Npc NewNpc(Fixture fx) => fx.OutputMod.Npcs.AddNew();

    // ── The specimen ────────────────────────────────────────────────────────────

    [Fact]
    public void RenamedDuplicate_MapsOldShapeNameToNew()
    {
        var fx = Build();
        var hair = MintDuplicate(fx, "HairMaleImperialChild01");
        var hairLine = MintDuplicate(fx, "HairLineMaleImperialChild01");

        var npc = NewNpc(fx);
        npc.HeadParts.Add(hair.FormKey);
        npc.HeadParts.Add(hairLine.FormKey);

        Collect(fx, npc).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["HairMaleImperialChild01"] = "HairMaleImperialChild01_RSkyrimChildren.esm",
            ["HairLineMaleImperialChild01"] = "HairLineMaleImperialChild01_RSkyrimChildren.esm",
        });
    }

    // ── The three ways a head part is excluded ──────────────────────────────────

    [Fact]
    public void HeadPartOutsideTheOutputPlugin_IsIgnored()
    {
        // An unduplicated head part still points at its original record, whose EditorID the mesh
        // already matches. Renaming its shape would CREATE the mismatch.
        var fx = Build();
        var vanilla = fx.SourceMod.HeadParts.AddNew();
        vanilla.EditorID = "HairMaleNord01";

        var npc = NewNpc(fx);
        npc.HeadParts.Add(vanilla.FormKey);

        Collect(fx, npc).Should().BeEmpty();
    }

    [Fact]
    public void OutputRecordWithNoRecordedProvenance_IsIgnored()
    {
        // Records the wig pipeline mints itself land here: it creates them outside the merge
        // walker and bakes shapes already carrying the new names, so there is nothing to rename
        // and no old name to rename FROM.
        var fx = Build();
        var minted = fx.OutputMod.HeadParts.AddNew();
        minted.EditorID = "NPC2Wig_FoxGlove_F_Hair";

        var npc = NewNpc(fx);
        npc.HeadParts.Add(minted.FormKey);

        Collect(fx, npc).Should().BeEmpty();
    }

    [Fact]
    public void DuplicateThatKeptItsEditorId_IsIgnored()
    {
        // Plain "Include" duplicates without renaming; the mesh already matches.
        var fx = Build();
        var source = fx.SourceMod.HeadParts.AddNew();
        source.EditorID = "HairMaleImperialChild01";

        var duplicate = fx.OutputMod.HeadParts.AddNew();
        duplicate.EditorID = "HairMaleImperialChild01"; // no suffix
        fx.RecordHandler.RecordMergedRecordOrigin(source.FormKey, duplicate.FormKey, source.EditorID);

        var npc = NewNpc(fx);
        npc.HeadParts.Add(duplicate.FormKey);

        Collect(fx, npc).Should().BeEmpty();
    }

    [Fact]
    public void SourceRecordWithNoEditorId_IsIgnored()
    {
        // "NoEditorID_<plugin>" is what the mint produces here; there is no old shape name to
        // match against, so there is nothing safe to do.
        var fx = Build();
        var source = fx.SourceMod.HeadParts.AddNew();
        source.EditorID = null;

        var duplicate = fx.OutputMod.HeadParts.AddNew();
        fx.RecordHandler.RecordMergedRecordOrigin(source.FormKey, duplicate.FormKey, source.EditorID);
        duplicate.EditorID = "NoEditorID_" + SourceKey.FileName;

        var npc = NewNpc(fx);
        npc.HeadParts.Add(duplicate.FormKey);

        Collect(fx, npc).Should().BeEmpty();
    }

    [Fact]
    public void NpcWithNoHeadParts_YieldsAnEmptyMap()
    {
        var fx = Build();
        Collect(fx, NewNpc(fx)).Should().BeEmpty();
    }

    // ── Extra parts ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExtraPartsAreWalked_BecauseAHairlineIsNotATopLevelHeadPart()
    {
        // The regression this pins: a hairline is an EXTRA PART of its hair head part, and gets
        // its own baked shape and its own duplicate. Reading only the NPC's own list renamed
        // Assur's hair and left his hairline behind — still dark-faced, just for one part.
        var fx = Build();
        var hair = MintDuplicate(fx, "HairMaleImperialChild01");
        var hairLine = MintDuplicate(fx, "HairLineMaleImperialChild01");
        hair.ExtraParts.Add(hairLine.FormKey);

        var npc = NewNpc(fx);
        npc.HeadParts.Add(hair.FormKey); // hairline is NOT on the NPC

        Collect(fx, npc).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["HairMaleImperialChild01"] = "HairMaleImperialChild01_RSkyrimChildren.esm",
            ["HairLineMaleImperialChild01"] = "HairLineMaleImperialChild01_RSkyrimChildren.esm",
        });
    }

    [Fact]
    public void ExtraPartOfAnUnrenamedPart_IsStillFound()
    {
        // Descent must not be gated on the parent having been renamed.
        var fx = Build();
        var parent = fx.OutputMod.HeadParts.AddNew();
        parent.EditorID = "SomeHair"; // in the output but with no recorded provenance
        var hairLine = MintDuplicate(fx, "HairLineMaleImperialChild01");
        parent.ExtraParts.Add(hairLine.FormKey);

        var npc = NewNpc(fx);
        npc.HeadParts.Add(parent.FormKey);

        Collect(fx, npc).Should().ContainKey("HairLineMaleImperialChild01");
    }

    [Fact]
    public void CyclicExtraParts_Terminate()
    {
        var fx = Build();
        var a = MintDuplicate(fx, "PartA");
        var b = MintDuplicate(fx, "PartB");
        a.ExtraParts.Add(b.FormKey);
        b.ExtraParts.Add(a.FormKey);

        var npc = NewNpc(fx);
        npc.HeadParts.Add(a.FormKey);

        Collect(fx, npc).Should().HaveCount(2);
    }

    // ── Race defaults ───────────────────────────────────────────────────────────

    /// <summary>Points an NPC at a race in the output plugin carrying <paramref name="defaults"/>
    /// as its male head data — what Include As New leaves behind once it has minted the race and
    /// remapped its head-part links to the duplicates.</summary>
    private static void GiveMintedRace(Fixture fx, Npc npc, params IHeadPartGetter[] defaults)
    {
        var race = fx.OutputMod.Races.AddNew();
        race.HeadData = new GenderedItem<HeadData?>(MaleHeadData(defaults), null);
        npc.Race.SetTo(race.FormKey);
    }

    private static HeadData MaleHeadData(params IHeadPartGetter[] parts)
    {
        var headData = new HeadData();
        foreach (var part in parts)
        {
            var hpRef = new HeadPartReference();
            hpRef.Head.SetTo(part.FormKey);
            headData.HeadParts.Add(hpRef);
        }

        return headData;
    }

    [Fact]
    public void RaceDefaultHeadPart_IsRenamedToo()
    {
        // The Kayd/Alesan/Francois specimen: RS Children's children carry only brows and eyes and
        // take their hair from the race, so walking the NPC's own list alone left the minted race
        // naming 'HairMaleRedguardChild01_RSkyrimChildren.esm' over a mesh still carrying
        // 'HairMaleRedguardChild01'. All three dark-faced in game.
        var fx = Build();
        var brows = MintDuplicate(fx, "0RCOChildBrows09");
        var raceHair = MintDuplicate(fx, "HairMaleRedguardChild01");

        var npc = NewNpc(fx);
        npc.HeadParts.Add(brows.FormKey); // no hair of his own
        GiveMintedRace(fx, npc, raceHair);

        Collect(fx, npc).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["0RCOChildBrows09"] = "0RCOChildBrows09_RSkyrimChildren.esm",
            ["HairMaleRedguardChild01"] = "HairMaleRedguardChild01_RSkyrimChildren.esm",
        });
    }

    [Fact]
    public void RaceDefaultExtraParts_AreWalked()
    {
        // Francois' half of the specimen: the race default hair owns the hairline, and both were
        // minted. Same rule as the NPC-owned case — the hairline is never a top-level entry.
        var fx = Build();
        var raceHair = MintDuplicate(fx, "HairMaleImperialChild01");
        var raceHairLine = MintDuplicate(fx, "HairLineMaleImperialChild01");
        raceHair.ExtraParts.Add(raceHairLine.FormKey);

        var npc = NewNpc(fx);
        GiveMintedRace(fx, npc, raceHair);

        Collect(fx, npc).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["HairMaleImperialChild01"] = "HairMaleImperialChild01_RSkyrimChildren.esm",
            ["HairLineMaleImperialChild01"] = "HairLineMaleImperialChild01_RSkyrimChildren.esm",
        });
    }

    [Fact]
    public void RaceOutsideTheOutputPlugin_ContributesNothing()
    {
        // A race this run did not mint still names the records the mesh was baked against —
        // RemapLinks only rewrites records the run wrote. Renaming off it would CREATE a mismatch.
        var fx = Build();
        var vanillaRace = fx.SourceMod.Races.AddNew();
        var sourceHair = fx.SourceMod.HeadParts.AddNew();
        sourceHair.EditorID = "HairMaleRedguardChild01";
        vanillaRace.HeadData = new GenderedItem<HeadData?>(MaleHeadData(sourceHair), null);

        var npc = NewNpc(fx);
        npc.Race.SetTo(vanillaRace.FormKey);

        Collect(fx, npc).Should().BeEmpty();
    }

    [Fact]
    public void FemaleNpc_ReadsTheFemaleHeadData()
    {
        // Mirrors FaceGenConsistencyAnalyzer, which grades the result: the wrong gender's defaults
        // are parts this NPC never wears.
        var fx = Build();
        var maleOnly = MintDuplicate(fx, "HairMaleRedguardChild01");

        var npc = NewNpc(fx);
        npc.Configuration.Flags |= NpcConfiguration.Flag.Female;
        GiveMintedRace(fx, npc, maleOnly); // male head data only

        Collect(fx, npc).Should().BeEmpty();
    }

    [Fact]
    public void RaceDefaultSharedWithTheNpcsOwnPart_IsMappedOnce()
    {
        // Include As New remaps every link to a given source, so an NPC and its race can arrive at
        // the same duplicate. The FormKey visit guard has to hold across both walks.
        var fx = Build();
        var hair = MintDuplicate(fx, "HairMaleImperialChild01");

        var npc = NewNpc(fx);
        npc.HeadParts.Add(hair.FormKey);
        GiveMintedRace(fx, npc, hair);

        Collect(fx, npc).Should().HaveCount(1);
    }

    [Fact]
    public void MixedNpc_MapsOnlyTheRenamedOnes()
    {
        var fx = Build();
        var renamed = MintDuplicate(fx, "HairMaleImperialChild01");

        var vanillaEyes = fx.SourceMod.HeadParts.AddNew();
        vanillaEyes.EditorID = "MaleEyesHumanBrown";

        var npc = NewNpc(fx);
        npc.HeadParts.Add(renamed.FormKey);
        npc.HeadParts.Add(vanillaEyes.FormKey);

        var map = Collect(fx, npc);

        map.Should().ContainKey("HairMaleImperialChild01");
        map.Should().NotContainKey("MaleEyesHumanBrown");
        map.Should().HaveCount(1);
    }
}
