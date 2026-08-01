using System.Linq;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="NpcWarningReporter.FormatReport"/> and <see cref="NpcWarningReporter.Header"/> —
/// the end-of-run, grouped-by-type presentation of per-NPC warnings (user direction 2026-07-30:
/// one explanatory paragraph per warning type, then the affected NPCs, instead of scattered
/// as-they-happen lines).
///
/// <para>Deliberately tests only the PURE half. The static Record/Reset/Flush state is shared
/// with live patch runs, and integration tests elsewhere in the suite run full patches in
/// parallel test collections — asserting on the shared state here would race them.</para>
/// </summary>
public class NpcWarningReporterTests
{
    private static (NpcWarningKind, string, string?, string?) Entry(
        NpcWarningKind kind, string npc, string? detail = null, string? technical = null) =>
        (kind, npc, detail, technical);

    private static string HeaderLine(NpcWarningKind kind) =>
        "WARNING: " + NpcWarningReporter.Header(kind);

    [Fact]
    public void FormatReport_EmitsNothing_WhenThereAreNoEntries()
    {
        NpcWarningReporter.FormatReport([]).Should().BeEmpty();
    }

    [Fact]
    public void FormatReport_EmitsOneHeaderPerKind_WithItsNpcsListedBeneath()
    {
        var lines = NpcWarningReporter.FormatReport(
        [
            Entry(NpcWarningKind.OriginMeshCompatibility, "Britte (0136B9:Skyrim.esm)"),
            Entry(NpcWarningKind.OriginMeshCompatibility, "Sissel (0136BA:Skyrim.esm)"),
            Entry(NpcWarningKind.MissingFaceTint, "Adrianne Avenicci (013BA5:Skyrim.esm)"),
        ]).ToList();

        lines.Should().ContainSingle(l => l == HeaderLine(NpcWarningKind.OriginMeshCompatibility));
        lines.Should().ContainSingle(l => l == HeaderLine(NpcWarningKind.MissingFaceTint));
        lines.Should().Contain("  - Britte (0136B9:Skyrim.esm)")
            .And.Contain("  - Sissel (0136BA:Skyrim.esm)")
            .And.Contain("  - Adrianne Avenicci (013BA5:Skyrim.esm)");

        // The NPCs of a group sit between their own header and the next one.
        int originHeader = lines.IndexOf(HeaderLine(NpcWarningKind.OriginMeshCompatibility));
        int tintHeader = lines.IndexOf(HeaderLine(NpcWarningKind.MissingFaceTint));
        lines.IndexOf("  - Britte (0136B9:Skyrim.esm)")
            .Should().BeInRange(originHeader + 1, tintHeader - 1);
    }

    [Fact]
    public void FormatReport_ListsNpcsAlphabetically_SoTheReportIsScannable()
    {
        var lines = NpcWarningReporter.FormatReport(
        [
            Entry(NpcWarningKind.MissingFaceTint, "Zedras (067777:tsr.esp)"),
            Entry(NpcWarningKind.MissingFaceTint, "Britte (0136B9:Skyrim.esm)"),
        ]).ToList();

        lines.IndexOf("  - Britte (0136B9:Skyrim.esm)").Should()
            .BeLessThan(lines.IndexOf("  - Zedras (067777:tsr.esp)"));
    }

    [Fact]
    public void FormatReport_MergesAnNpcsDetails_IntoOneLine()
    {
        // The textureless report records once per shape; the NPC must still get ONE line.
        var lines = NpcWarningReporter.FormatReport(
        [
            Entry(NpcWarningKind.TexturelessShapes, "Adrianne (013BA5:Skyrim.esm)",
                "hair.nif 'Hair' (missing: a.dds)"),
            Entry(NpcWarningKind.TexturelessShapes, "Adrianne (013BA5:Skyrim.esm)",
                "brows.nif 'Brows' (missing: b.dds)"),
        ]).ToList();

        lines.Should().ContainSingle(l => l.StartsWith("  - Adrianne"));
        lines.Single(l => l.StartsWith("  - Adrianne")).Should().Be(
            "  - Adrianne (013BA5:Skyrim.esm): hair.nif 'Hair' (missing: a.dds); brows.nif 'Brows' (missing: b.dds)");
    }

    [Fact]
    public void FormatReport_DoesNotDuplicateAnNpc_RecordedTwiceForTheSameKind()
    {
        var lines = NpcWarningReporter.FormatReport(
        [
            Entry(NpcWarningKind.MissingFaceTint, "Britte (0136B9:Skyrim.esm)"),
            Entry(NpcWarningKind.MissingFaceTint, "Britte (0136B9:Skyrim.esm)"),
        ]).ToList();

        lines.Should().ContainSingle(l => l.StartsWith("  - Britte"));
    }

    [Fact]
    public void FormatReport_SkipsKindsWithNoEntries_EntirelyIncludingTheirSpacerLine()
    {
        var lines = NpcWarningReporter.FormatReport(
            [Entry(NpcWarningKind.TexturelessShapes, "Britte (0136B9:Skyrim.esm)", "d")]);

        lines.Should().HaveCount(3); // spacer + header + one NPC
        lines[0].Should().BeEmpty();
    }

    [Fact]
    public void BuildDetailedGroups_EmitsNothing_WhenThereAreNoEntries()
    {
        NpcWarningReporter.BuildDetailedGroups([]).Should().BeEmpty();
    }

    [Fact]
    public void BuildDetailedGroups_GroupsByKind_WithHeaderNoteAndSplitTechnicalDetail()
    {
        var groups = NpcWarningReporter.BuildDetailedGroups(
        [
            Entry(NpcWarningKind.OriginMeshCompatibility, "Britte (0136B9:Skyrim.esm)",
                technical: "row=4 (DdsOnlyNoRecord)\norigin: meshCompat=False"),
        ]).ToList();

        var group = groups.Should().ContainSingle().Subject;
        group.Kind.Should().Be(NpcWarningKind.OriginMeshCompatibility);
        group.Header.Should().Be(NpcWarningReporter.Header(NpcWarningKind.OriginMeshCompatibility),
            "the lay paragraph gives the technical reader the same context the run log gave");
        group.TechnicalNote.Should().Be(
            NpcWarningReporter.TechnicalNote(NpcWarningKind.OriginMeshCompatibility));

        var npc = group.Npcs.Should().ContainSingle().Subject;
        npc.Heading.Should().Be("Britte (0136B9:Skyrim.esm)");
        npc.TechnicalLines.Should().Equal("row=4 (DdsOnlyNoRecord)", "origin: meshCompat=False");
    }

    [Fact]
    public void BuildDetailedGroups_KeepsTheUserFacingDetail_OnTheNpcHeading()
    {
        var groups = NpcWarningReporter.BuildDetailedGroups(
        [
            Entry(NpcWarningKind.TexturelessShapes, "Adrianne (013BA5:Skyrim.esm)",
                detail: "hair.nif 'Hair' (missing: a.dds)",
                technical: "mod='Some Mod' nif=C:/full/path/hair.nif"),
        ]);

        groups.Should().ContainSingle().Which.Npcs.Should().ContainSingle().Which.Heading
            .Should().Be("Adrianne (013BA5:Skyrim.esm): hair.nif 'Hair' (missing: a.dds)");
    }

    [Fact]
    public void ClassifyTechnicalLines_MergesTheSourceLines_IntoOneComparisonTable()
    {
        // The ladder's three source lines share their keys, so the reader gets a grid instead of
        // three prose lines. The winner row lacks 'record' — its cell must be null, not shifted.
        var blocks = NpcWarningReporter.ClassifyTechnicalLines(new[]
        {
            "selected mod 'RedBag's Rorikstead FOMOD': record=no, nif=NotFound, dds=LooseFile, meshCompat=NotEvaluated",
            "origin: record=yes, nif=BsaFile, dds=BsaFile, meshCompat=False",
            "winner: nif=yes (Skyrim - Meshes0.bsa), dds=yes, meshCompat=False",
        });

        var table = blocks.Should().ContainSingle().Subject
            .Should().BeOfType<NPC_Plugin_Chooser_2.BackEnd.Logging.HtmlDetailTable>().Subject;
        table.Columns.Should().Equal("record", "nif", "dds", "meshCompat");
        table.Rows.Should().HaveCount(3);
        table.Rows[0].Label.Should().Be("selected mod 'RedBag's Rorikstead FOMOD'");
        table.Rows[2].Label.Should().Be("winner");
        table.Rows[2].Cells[0].Should().BeNull("the winner line has no record= field");
        table.Rows[2].Cells[1].Should().Be("yes (Skyrim - Meshes0.bsa)");
    }

    [Fact]
    public void ClassifyTechnicalLines_ShapesFactsFieldRowsAndProbeGroups()
    {
        var blocks = NpcWarningReporter.ClassifyTechnicalLines(new[]
        {
            "decision: row=4 (DdsOnlyNoRecord), mode=Record",
            "target=0136B9:Skyrim.esm donor=0136B9:Skyrim.esm subject=0136B9:Skyrim.esm chain=NotTemplated (flattened)",
            "graded record: 'Britte' (0136B9:Skyrim.esm)",
            "origin mesh failed the probe:",
            "  record needs (no baked shape in mesh): '0RCOChildHeadNord' (000011:RSkyrimChildren.esm)",
            "  mesh bakes (no matching head part): ChildEyes, ChildMouth",
        });

        blocks.Should().HaveCount(4);

        var decision = blocks[0].Should()
            .BeOfType<NPC_Plugin_Chooser_2.BackEnd.Logging.HtmlDetailFieldRow>().Subject;
        decision.Label.Should().Be("decision");
        decision.Fields.Should().Contain(f => f.Key == "row" && f.Value == "4 (DdsOnlyNoRecord)");

        var identifiers = blocks[1].Should()
            .BeOfType<NPC_Plugin_Chooser_2.BackEnd.Logging.HtmlDetailFieldRow>().Subject;
        identifiers.Label.Should().BeNull();
        identifiers.Fields.Should().Contain(f => f.Key == "chain" && f.Value == "NotTemplated (flattened)",
            "a bare token continues the previous field's value");

        blocks[2].Should().BeOfType<NPC_Plugin_Chooser_2.BackEnd.Logging.HtmlDetailFact>()
            .Which.Key.Should().Be("graded record");

        var group = blocks[3].Should()
            .BeOfType<NPC_Plugin_Chooser_2.BackEnd.Logging.HtmlDetailGroup>().Subject;
        group.Title.Should().Be("origin mesh failed the probe");
        group.Children.Should().HaveCount(2)
            .And.AllBeOfType<NPC_Plugin_Chooser_2.BackEnd.Logging.HtmlDetailFact>();
    }

    [Fact]
    public void ClassifyTechnicalLines_FallsBackToText_ForUnrecognizedProse()
    {
        var blocks = NpcWarningReporter.ClassifyTechnicalLines(new[]
        {
            "something a future producer wrote in plain prose",
        });

        blocks.Should().ContainSingle().Which
            .Should().BeOfType<NPC_Plugin_Chooser_2.BackEnd.Logging.HtmlDetailText>()
            .Which.Text.Should().Be("something a future producer wrote in plain prose");
    }

    [Theory]
    [InlineData(NpcWarningKind.OriginMeshCompatibility)]
    [InlineData(NpcWarningKind.ModMeshCompatibility)]
    [InlineData(NpcWarningKind.RaceDefaultsDrift)]
    [InlineData(NpcWarningKind.MissingFaceTint)]
    [InlineData(NpcWarningKind.TexturelessShapes)]
    [InlineData(NpcWarningKind.OrphanedRecordDuplicate)]
    public void TechnicalNotes_ExistForEveryKind(NpcWarningKind kind)
    {
        NpcWarningReporter.TechnicalNote(kind).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void EveryDeclaredKind_HasAHeaderAndTechnicalNote()
    {
        // Both switches throw on an unhandled member, so a kind added to the enum without its
        // two arms would otherwise only surface at report time of the first run that records it.
        foreach (var kind in System.Enum.GetValues<NpcWarningKind>())
        {
            NpcWarningReporter.Header(kind).Should().NotBeNullOrWhiteSpace(
                $"kind {kind} must have a Header arm");
            NpcWarningReporter.TechnicalNote(kind).Should().NotBeNullOrWhiteSpace(
                $"kind {kind} must have a TechnicalNote arm");
        }
    }

    [Fact]
    public void OrphanedDuplicates_GroupUnderTheirOwnHeader_WithSourceDetail()
    {
        // The orphan check records the RECORD identity in the NPC slot (one line per orphaned
        // chain head) and where it was copied from as the detail.
        var lines = NpcWarningReporter.FormatReport(
        [
            Entry(NpcWarningKind.OrphanedRecordDuplicate,
                "ChildOutfit01_Skyrim [Outfit] 0035F8:NPC.esp",
                "copied from ChildOutfit01 (06D92E:Skyrim.esm); minted for 2 NPC selection(s) from RS Children Overhaul"),
        ]).ToList();

        lines.Should().ContainSingle(l => l == HeaderLine(NpcWarningKind.OrphanedRecordDuplicate));
        lines.Should().Contain(l =>
            l.StartsWith("  - ChildOutfit01_Skyrim [Outfit] 0035F8:NPC.esp") &&
            l.Contains("copied from ChildOutfit01"));
    }

    [Theory]
    [InlineData(NpcWarningKind.OriginMeshCompatibility)]
    [InlineData(NpcWarningKind.ModMeshCompatibility)]
    [InlineData(NpcWarningKind.RaceDefaultsDrift)]
    [InlineData(NpcWarningKind.MissingFaceTint)]
    [InlineData(NpcWarningKind.TexturelessShapes)]
    [InlineData(NpcWarningKind.OrphanedRecordDuplicate)]
    public void Headers_AreSpecific_AndClassifyAsWarnings(NpcWarningKind kind)
    {
        string header = NpcWarningReporter.Header(kind);

        // "Optimize for readability so that lay users will act": every header names its subject
        // instead of pointing at context with a bare pronoun, and ends by introducing the list.
        header.Should().NotBeNullOrWhiteSpace();
        header.TrimEnd().Should().EndWith(":", "the NPC list follows immediately");
        header.Should().NotContain("this NPC",
            "group headers stand alone above a list; bare pronouns have no antecedent there");

        // The run log emits "WARNING: <header>"; RunLogClassifier reads the lead marker for the
        // colour, and a stray ERROR/FATAL word inside the text would outrank it.
        RunLogClassifier.Classify("WARNING: " + header, RunLogSeverity.Info)
            .Should().Be(RunLogSeverity.Warning);
    }
}
