using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Logic of the opt-in <see cref="RecordProvenanceDiag"/> accumulator and its CSV output: one
/// row per output record (first discovery wins), provenance chains are rooted at the current
/// NPC (prepended when the walk root is a different record), sub-walks join onto already-known
/// chains, override rows consume the chain captured at discovery time, rollback removes rows,
/// and <see cref="RecordProvenanceDiag.Flush"/> emits RFC-4180-escaped CSV. Also verifies the
/// <see cref="RecordProvenanceDiag.IsEnabled"/> gate makes recording a true no-op.
///
/// The diag is a process-global static that writes <c>RecordProvenance.csv</c> to the app base
/// directory, so each test enables the gate + clears the state in the ctor and restores/cleans
/// up in <see cref="Dispose"/>. Methods within a class run serially, and no other suite touches
/// these statics, so the global state is safe here.
/// </summary>
public sealed class RecordProvenanceDiagTests : IDisposable
{
    private static readonly FormKey Npc = FormKey.Factory("0A2C94:Skyrim.esm"); // "Lydia"
    private static readonly FormKey SrcArmor = FormKey.Factory("000801:CoolNpcs.esp");
    private static readonly FormKey SrcArma = FormKey.Factory("000802:CoolNpcs.esp");
    private static readonly FormKey SrcTexSet = FormKey.Factory("000803:CoolNpcs.esp");
    private static readonly FormKey OutArmor = FormKey.Factory("000D01:NPC.esp");
    private static readonly FormKey OutArma = FormKey.Factory("000D02:NPC.esp");
    private static readonly FormKey OutTexSet = FormKey.Factory("000D03:NPC.esp");
    private static readonly FormKey OverrideRec = FormKey.Factory("03AB12:Update.esm");

    private const string CsvFileName = "RecordProvenance.csv";

    // Column indices in the emitted CSV.
    private const int OutFk = 0, SrcFk = 1, Edid = 2, RecType = 3, Kind = 4, History = 5;

    public RecordProvenanceDiagTests()
    {
        SetEnabled(true);          // must precede Reset() — Reset() no-ops while disabled
        RecordProvenanceDiag.Reset();
        DeleteCsv();
    }

    public void Dispose()
    {
        RecordProvenanceDiag.Reset();
        SetEnabled(false);
        DeleteCsv();
    }

    // ---------------------------------------------------------------------
    // CSV shape
    // ---------------------------------------------------------------------

    [Fact]
    public void Csv_StartsWithHeaderRow()
    {
        RecordProvenanceDiag.RecordMergedAsNew(SrcArmor, "SkinArmor", "Armor", OutArmor, null);

        var csv = FlushAndRead();

        csv.Split('\n')[0].TrimEnd('\r').Should()
            .Be("OutputFormKey,SourceFormKey,EditorID,Type,Kind,ProvenanceHistory");
    }

    [Fact]
    public void Csv_EscapesFieldsContainingCommas()
    {
        RecordProvenanceDiag.RecordMergedAsNew(SrcArmor, "Skin,Armor", "Armor", OutArmor, null);

        var csv = FlushAndRead();

        csv.Should().Contain("\"Skin,Armor\"", "a field with a comma must be quoted per RFC 4180");
    }

    // ---------------------------------------------------------------------
    // MergedAsNew chains
    // ---------------------------------------------------------------------

    [Fact]
    public void MergedAsNew_ChainRunsFromNpcThroughParentsToSelf()
    {
        RecordProvenanceDiag.SetCurrentNpc(Npc, "Lydia");
        var parents = new List<RecordProvenanceDiag.Node>
        {
            new(Npc, "Lydia"),
            new(SrcArmor, "SkinArmor"),
        };
        RecordProvenanceDiag.RecordMergedAsNew(SrcArma, "SkinArma", "ArmorAddon", OutArma, parents);

        var row = SingleRowFor(OutArma);

        Cells(row)[SrcFk].Should().Be(SrcArma.ToString());
        Cells(row)[Edid].Should().Be("SkinArma");
        Cells(row)[RecType].Should().Be("ArmorAddon");
        Cells(row)[Kind].Should().Be("MergedAsNew");
        Cells(row)[History].Should().Be(
            $"{Npc} (Lydia) -> {SrcArmor} (SkinArmor) -> {SrcArma} (SkinArma)",
            "the walk already roots at the current NPC, so no extra prepend");
    }

    [Fact]
    public void MergedAsNew_WalkNotRootedAtNpc_PrependsCurrentNpc()
    {
        RecordProvenanceDiag.SetCurrentNpc(Npc, "Lydia");
        // A walk rooted at the armor itself (e.g. DuplicateInOrAddFormLink on WornArmor).
        RecordProvenanceDiag.RecordMergedAsNew(SrcArmor, "SkinArmor", "Armor", OutArmor, null);

        Cells(SingleRowFor(OutArmor))[History].Should().Be(
            $"{Npc} (Lydia) -> {SrcArmor} (SkinArmor)");
    }

    [Fact]
    public void MergedAsNew_MissingEditorId_RendersNull()
    {
        RecordProvenanceDiag.SetCurrentNpc(Npc, null);
        RecordProvenanceDiag.RecordMergedAsNew(SrcArmor, null, "Armor", OutArmor, null);

        var row = SingleRowFor(OutArmor);

        Cells(row)[Edid].Should().BeEmpty();
        Cells(row)[History].Should().Be($"{Npc} (NULL) -> {SrcArmor} (NULL)");
    }

    [Fact]
    public void SubWalkRootedAtKnownRecord_JoinsOntoItsChain()
    {
        RecordProvenanceDiag.SetCurrentNpc(Npc, "Lydia");
        // First walk merges the armor (chain: NPC -> armor).
        RecordProvenanceDiag.RecordMergedAsNew(SrcArmor, "SkinArmor", "Armor", OutArmor, null);
        // A later sub-walk roots at the armor's OUTPUT duplicate (the merged record itself) and
        // pulls in a texture set — its chain must extend the armor's, not start fresh.
        var parents = new List<RecordProvenanceDiag.Node> { new(OutArmor, "SkinArmor_x") };
        RecordProvenanceDiag.RecordMergedAsNew(SrcTexSet, "SkinTex", "TextureSet", OutTexSet, parents);

        Cells(SingleRowFor(OutTexSet))[History].Should().Be(
            $"{Npc} (Lydia) -> {SrcArmor} (SkinArmor) -> {SrcTexSet} (SkinTex)",
            "the walk root's already-recorded chain replaces its bare node");
    }

    [Fact]
    public void SameOutputRecordReportedTwice_FirstReportWins()
    {
        RecordProvenanceDiag.SetCurrentNpc(Npc, "Lydia");
        RecordProvenanceDiag.RecordMergedAsNew(SrcArmor, "First", "Armor", OutArmor, null);
        RecordProvenanceDiag.RecordMergedAsNew(SrcArmor, "Second", "Armor", OutArmor, null);

        Cells(SingleRowFor(OutArmor))[Edid].Should().Be("First");
    }

    // ---------------------------------------------------------------------
    // Overrides
    // ---------------------------------------------------------------------

    [Fact]
    public void OverrideWritten_UsesChainCapturedAtDiscoveryTime()
    {
        RecordProvenanceDiag.SetCurrentNpc(Npc, "Lydia");
        var parents = new List<RecordProvenanceDiag.Node> { new(SrcArmor, "SkinArmor") };
        RecordProvenanceDiag.RecordOverrideDiscoveryChain(OverrideRec, "ChildClothes", parents);
        RecordProvenanceDiag.RecordOverrideWritten(OverrideRec, "ChildClothes", "Outfit", deltaPatched: false);

        var row = SingleRowFor(OverrideRec);

        Cells(row)[SrcFk].Should().Be(OverrideRec.ToString(), "an override keeps its FormKey");
        Cells(row)[RecType].Should().Be("Outfit");
        Cells(row)[Kind].Should().Be("Override");
        Cells(row)[History].Should().Be(
            $"{Npc} (Lydia) -> {SrcArmor} (SkinArmor) -> {OverrideRec} (ChildClothes)");
    }

    [Fact]
    public void DeltaPatchedOverride_IsMarkedAsSuch()
    {
        RecordProvenanceDiag.SetCurrentNpc(Npc, "Lydia");
        RecordProvenanceDiag.RecordOverrideWritten(OverrideRec, "ChildClothes", "Outfit", deltaPatched: true);

        Cells(SingleRowFor(OverrideRec))[Kind].Should().Be("DeltaPatchedOverride");
    }

    [Fact]
    public void OverrideWithoutDiscoveryChain_UsesDiscoveryNote()
    {
        RecordProvenanceDiag.RecordOverrideWritten(OverrideRec, "ChildClothes", "Outfit", deltaPatched: false,
            "discovered by all-overrides plugin scan");

        Cells(SingleRowFor(OverrideRec))[History].Should().Be(
            $"(discovered by all-overrides plugin scan) -> {OverrideRec} (ChildClothes)");
    }

    [Fact]
    public void BulkOverrideImport_HasPlaceholderHistoryNamingThePlugin()
    {
        RecordProvenanceDiag.RecordBulkOverrideImport(OverrideRec, "ChildClothes", "Outfit", OutArmor,
            ModKey.FromNameAndExtension("CoolNpcs.esp"));

        var row = SingleRowFor(OutArmor);

        Cells(row)[RecType].Should().Be("Outfit");
        Cells(row)[Kind].Should().Be("BulkOverrideImport");
        Cells(row)[SrcFk].Should().Be(OverrideRec.ToString());
        Cells(row)[History].Should().Be(
            $"(bulk override import from CoolNpcs.esp) -> {OverrideRec} (ChildClothes)");
    }

    // ---------------------------------------------------------------------
    // Generated records & rollback
    // ---------------------------------------------------------------------

    [Fact]
    public void GeneratedRecord_HasNoSourceAndAGeneratedMarker()
    {
        RecordProvenanceDiag.RecordGenerated(OutArmor, "MyKeyword", "Keyword");

        var row = SingleRowFor(OutArmor);

        Cells(row)[SrcFk].Should().BeEmpty();
        Cells(row)[RecType].Should().Be("Keyword");
        Cells(row)[Kind].Should().Be("Generated");
        Cells(row)[History].Should().Be($"(generated by NPC2) -> {OutArmor} (MyKeyword)");
    }

    [Fact]
    public void RemoveOutputRecord_DropsTheRow()
    {
        RecordProvenanceDiag.RecordMergedAsNew(SrcArmor, "SkinArmor", "Armor", OutArmor, null);
        RecordProvenanceDiag.RemoveOutputRecord(OutArmor);

        DataRows(FlushAndRead()).Should().BeEmpty("a rolled-back record must not be reported");
    }

    // ---------------------------------------------------------------------
    // Enable/disable gate
    // ---------------------------------------------------------------------

    [Fact]
    public void Disabled_Recording_IsNoOp()
    {
        SetEnabled(false);
        RecordProvenanceDiag.RecordMergedAsNew(SrcArmor, "SkinArmor", "Armor", OutArmor, null);
        SetEnabled(true);

        DataRows(FlushAndRead()).Should()
            .NotContain(r => Cells(r)[OutFk] == OutArmor.ToString(),
                "recording is skipped entirely while disabled");
    }

    [Fact]
    public void Disabled_Flush_WritesNothingAndDoesNotThrow()
    {
        RecordProvenanceDiag.RecordMergedAsNew(SrcArmor, "SkinArmor", "Armor", OutArmor, null);
        SetEnabled(false);

        var act = () => RecordProvenanceDiag.Flush();

        act.Should().NotThrow();
        File.Exists(CsvPath).Should().BeFalse("a disabled Flush must not write the report");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static string SingleRowFor(FormKey outputFormKey)
        => DataRows(FlushAndRead()).Single(r => Cells(r)[OutFk] == outputFormKey.ToString());

    private static string FlushAndRead()
    {
        RecordProvenanceDiag.Flush();
        File.Exists(CsvPath).Should().BeTrue("Flush should have written the report");
        return File.ReadAllText(CsvPath);
    }

    /// <summary>Non-header, non-empty CSV lines.</summary>
    private static string[] DataRows(string csv) =>
        csv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();

    /// <summary>Splits one CSV row honoring RFC-4180 quoting (histories contain no commas in the
    /// test data, but quoted fields must still parse in the escaping test).</summary>
    private static string[] Cells(string row)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < row.Length; i++)
        {
            char c = row[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < row.Length && row[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { cells.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        cells.Add(current.ToString());
        return cells.ToArray();
    }

    private static string CsvPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CsvFileName);

    private static void DeleteCsv()
    {
        try { if (File.Exists(CsvPath)) File.Delete(CsvPath); } catch { /* best effort */ }
    }

    /// <summary>Toggles the diag on/off via its public settings hook — the same one the Settings
    /// checkbox calls. No dev file trigger is present in tests, so this cleanly enables/disables.</summary>
    private static void SetEnabled(bool value) => RecordProvenanceDiag.SetEnabled(value);
}
