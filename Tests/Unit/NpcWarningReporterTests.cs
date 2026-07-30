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
    public void FormatDetailedLog_EmitsNothing_WhenThereAreNoEntries()
    {
        NpcWarningReporter.FormatDetailedLog([]).Should().BeEmpty();
    }

    [Fact]
    public void FormatDetailedLog_GroupsByKind_WithHeaderNoteAndIndentedTechnicalDetail()
    {
        var lines = NpcWarningReporter.FormatDetailedLog(
        [
            Entry(NpcWarningKind.OriginMeshCompatibility, "Britte (0136B9:Skyrim.esm)",
                technical: "row=4 (DdsOnlyNoRecord)\norigin: meshCompat=False"),
        ]).ToList();

        lines.Should().Contain("=== OriginMeshCompatibility ===");
        lines.Should().Contain(NpcWarningReporter.Header(NpcWarningKind.OriginMeshCompatibility),
            "the lay paragraph gives the technical reader the same context the run log gave");
        lines.Should().Contain(NpcWarningReporter.TechnicalNote(NpcWarningKind.OriginMeshCompatibility));
        lines.Should().Contain("--- Britte (0136B9:Skyrim.esm)");
        lines.Should().Contain("    row=4 (DdsOnlyNoRecord)",
            "multi-line technical detail is split and indented under the NPC");
        lines.Should().Contain("    origin: meshCompat=False");
    }

    [Fact]
    public void FormatDetailedLog_KeepsTheUserFacingDetail_OnTheNpcHeading()
    {
        var lines = NpcWarningReporter.FormatDetailedLog(
        [
            Entry(NpcWarningKind.TexturelessShapes, "Adrianne (013BA5:Skyrim.esm)",
                detail: "hair.nif 'Hair' (missing: a.dds)",
                technical: "mod='Some Mod' nif=C:/full/path/hair.nif"),
        ]);

        lines.Should().Contain("--- Adrianne (013BA5:Skyrim.esm): hair.nif 'Hair' (missing: a.dds)");
    }

    [Theory]
    [InlineData(NpcWarningKind.OriginMeshCompatibility)]
    [InlineData(NpcWarningKind.ModMeshCompatibility)]
    [InlineData(NpcWarningKind.MissingFaceTint)]
    [InlineData(NpcWarningKind.TexturelessShapes)]
    public void TechnicalNotes_ExistForEveryKind(NpcWarningKind kind)
    {
        NpcWarningReporter.TechnicalNote(kind).Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(NpcWarningKind.OriginMeshCompatibility)]
    [InlineData(NpcWarningKind.ModMeshCompatibility)]
    [InlineData(NpcWarningKind.MissingFaceTint)]
    [InlineData(NpcWarningKind.TexturelessShapes)]
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
