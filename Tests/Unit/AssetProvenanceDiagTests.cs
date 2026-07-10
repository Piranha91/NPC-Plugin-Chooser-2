using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Logic of the opt-in <see cref="AssetProvenanceDiag"/> accumulator and its CSV output: every
/// reference is recorded (independent of the copy dedup), identical tuples collapse, the resolved
/// source is captured per file+mod, and <see cref="AssetProvenanceDiag.Flush"/> emits one CSV row
/// per atomic reference with proper RFC-4180 escaping. Also verifies the
/// <see cref="AssetProvenanceDiag.IsEnabled"/> gate makes recording a true no-op.
///
/// The diag is a process-global static that writes <c>AssetProvenance.csv</c> to the app base
/// directory, so each test enables the gate + clears the map in the ctor and restores/cleans up
/// in <see cref="Dispose"/>. Methods within a class run serially, and no other suite touches
/// these statics, so the global state is safe here.
/// </summary>
public sealed class AssetProvenanceDiagTests : IDisposable
{
    private static readonly FormKey Target1 = FormKey.Factory("0A2C94:Skyrim.esm"); // "Lydia"
    private static readonly FormKey Target2 = FormKey.Factory("01B07C:Skyrim.esm"); // "Aela"
    private static readonly FormKey Donor = FormKey.Factory("000800:CoolNpcs.esp");

    private const string CsvFileName = "AssetProvenance.csv";

    // Column indices in the emitted CSV.
    private const int DestFile = 0, Reason = 1, Referencer = 2, Npc = 3, TargetFk = 4,
        Mod = 5, DonorFk = 6, DonorEid = 7, SourceKind = 8, SourcePath = 9;

    public AssetProvenanceDiagTests()
    {
        SetEnabled(true);          // must precede Reset() — Reset() no-ops while disabled
        AssetProvenanceDiag.Reset();
        DeleteCsv();
    }

    public void Dispose()
    {
        AssetProvenanceDiag.Reset();
        SetEnabled(false);
        DeleteCsv();
    }

    // ---------------------------------------------------------------------
    // CSV shape
    // ---------------------------------------------------------------------

    [Fact]
    public void Csv_StartsWithHeaderRow()
    {
        AssetProvenanceDiag.RecordReference(@"meshes\x.nif", "Cool NPCs", Ctx(Target1, "Lydia"));

        var csv = FlushAndRead();

        csv.Split('\n')[0].TrimEnd('\r').Should()
            .Be("DestFile,Reason,Referencer,NPC,TargetFormKey,Mod,DonorFormKey,DonorEditorID,SourceKind,SourcePath");
    }

    [Fact]
    public void Csv_EscapesFieldsContainingCommas()
    {
        AssetProvenanceDiag.RecordReference(@"meshes\x.nif", "Cool, Awesome NPCs", Ctx(Target1, "Lydia"));

        var csv = FlushAndRead();

        csv.Should().Contain("\"Cool, Awesome NPCs\"", "a field with a comma must be quoted per RFC 4180");
    }

    // ---------------------------------------------------------------------
    // References
    // ---------------------------------------------------------------------

    [Fact]
    public void SameFileFromTwoNpcs_EmitsARowForEach()
    {
        var relPath = @"textures\actors\character\facegendata\facegeom\coolnpcs.esp\shared.dds";
        AssetProvenanceDiag.RecordReference(relPath, "Cool NPCs", Ctx(Target1, "Lydia [0A2C94:Skyrim.esm]"));
        AssetProvenanceDiag.RecordReference(relPath, "Cool NPCs", Ctx(Target2, "Aela [01B07C:Skyrim.esm]"));
        AssetProvenanceDiag.RecordSource(relPath, "Cool NPCs", "LooseFile", @"S:\Mods\CoolNpcs\" + relPath);

        var forFile = DataRows(FlushAndRead()).Where(r => Cells(r)[DestFile] == relPath).ToArray();

        forFile.Should().HaveCount(2);
        forFile.Should().Contain(r => Cells(r)[Npc] == "Lydia [0A2C94:Skyrim.esm]");
        forFile.Should().Contain(r => Cells(r)[Npc] == "Aela [01B07C:Skyrim.esm]");
        forFile.Should().OnlyContain(r => Cells(r)[SourceKind] == "Loose");
        forFile.Should().OnlyContain(r => Cells(r)[SourcePath] == @"S:\Mods\CoolNpcs\" + relPath);
    }

    [Fact]
    public void IdenticalTupleRecordedTwice_IsDeduped()
    {
        var relPath = @"meshes\head.nif";
        var ctx = Ctx(Target1, "Lydia [0A2C94:Skyrim.esm]");
        AssetProvenanceDiag.RecordReference(relPath, "Cool NPCs", ctx);
        AssetProvenanceDiag.RecordReference(relPath, "Cool NPCs", ctx); // exact duplicate

        DataRows(FlushAndRead()).Count(r => Cells(r)[DestFile] == relPath)
            .Should().Be(1, "identical (file, NPC, mod, reason, referencer) references collapse into one row");
    }

    [Fact]
    public void BsaSource_IsRenderedAsBsaKind()
    {
        var relPath = @"meshes\body.nif";
        AssetProvenanceDiag.RecordReference(relPath, "Cool NPCs", Ctx(Target1, "Lydia"));
        AssetProvenanceDiag.RecordSource(relPath, "Cool NPCs", "BsaFile", @"S:\Mods\CoolNpcs\CoolNpcs.bsa");

        var row = DataRows(FlushAndRead()).Single(r => Cells(r)[DestFile] == relPath);

        Cells(row)[SourceKind].Should().Be("BSA");
        Cells(row)[SourcePath].Should().Be(@"S:\Mods\CoolNpcs\CoolNpcs.bsa");
    }

    // ---------------------------------------------------------------------
    // Referencer column (the "which record" the user asked for)
    // ---------------------------------------------------------------------

    [Fact]
    public void PluginRef_ReferencerColumn_NamesTheRecord()
    {
        var ctx = new AssetRequestContext("Lydia [0A2C94:Skyrim.esm]", Target1, Target1, "Lydia",
            "PluginRef", "HeadPart 'Hair01' [012345:CoolNpcs.esp]");
        AssetProvenanceDiag.RecordReference(@"meshes\hair.nif", "Cool NPCs", ctx);

        var row = DataRows(FlushAndRead()).Single(r => Cells(r)[DestFile] == @"meshes\hair.nif");

        Cells(row)[Reason].Should().Be("PluginRef");
        Cells(row)[Referencer].Should().Be("HeadPart 'Hair01' [012345:CoolNpcs.esp]");
    }

    [Fact]
    public void FaceGen_HasNoReferencer()
    {
        AssetProvenanceDiag.RecordReference(@"meshes\face.nif", "Cool NPCs", Ctx(Target1, "Lydia"));

        var row = DataRows(FlushAndRead()).Single(r => Cells(r)[DestFile] == @"meshes\face.nif");

        Cells(row)[Reason].Should().Be("FaceGen");
        Cells(row)[Referencer].Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // Donor columns (face swap)
    // ---------------------------------------------------------------------

    [Fact]
    public void FaceSwap_PopulatesDonorColumns()
    {
        var swapCtx = new AssetRequestContext("Lydia [0A2C94:Skyrim.esm]", Target1, Donor, "CoolLydia", "FaceGen");
        AssetProvenanceDiag.RecordReference(@"meshes\swap.nif", "Cool NPCs", swapCtx);

        var row = DataRows(FlushAndRead()).Single(r => Cells(r)[DestFile] == @"meshes\swap.nif");

        Cells(row)[DonorFk].Should().Be(Donor.ToString());
        Cells(row)[DonorEid].Should().Be("CoolLydia");
    }

    [Fact]
    public void NoFaceSwap_LeavesDonorColumnsEmpty()
    {
        // Donor == target -> not a swap.
        var selfCtx = new AssetRequestContext("Lydia [0A2C94:Skyrim.esm]", Target1, Target1, "Lydia", "FaceGen");
        AssetProvenanceDiag.RecordReference(@"meshes\self.nif", "Cool NPCs", selfCtx);

        var row = DataRows(FlushAndRead()).Single(r => Cells(r)[DestFile] == @"meshes\self.nif");

        Cells(row)[DonorFk].Should().BeEmpty();
        Cells(row)[DonorEid].Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // "By NPC" is just filtering rows — one NPC, its files with reasons.
    // ---------------------------------------------------------------------

    [Fact]
    public void AllFilesForOneNpc_AppearAsRowsWithTheirReasons()
    {
        var ctxFace = new AssetRequestContext("Lydia [0A2C94:Skyrim.esm]", Target1, Target1, "Lydia", "FaceGen");
        var ctxRef = ctxFace.WithReason("PluginRef", "HeadPart 'Hair01' [012345:CoolNpcs.esp]");
        AssetProvenanceDiag.RecordReference(@"textures\face.dds", "Cool NPCs", ctxFace);
        AssetProvenanceDiag.RecordReference(@"meshes\hair.nif", "Cool NPCs", ctxRef);

        var rows = DataRows(FlushAndRead()).Where(r => Cells(r)[Npc] == "Lydia [0A2C94:Skyrim.esm]").ToArray();

        rows.Should().HaveCount(2);
        rows.Should().Contain(r => Cells(r)[DestFile] == @"textures\face.dds" && Cells(r)[Reason] == "FaceGen");
        rows.Should().Contain(r => Cells(r)[DestFile] == @"meshes\hair.nif" && Cells(r)[Reason] == "PluginRef"
                                   && Cells(r)[Referencer] == "HeadPart 'Hair01' [012345:CoolNpcs.esp]");
    }

    // ---------------------------------------------------------------------
    // Enable/disable gate
    // ---------------------------------------------------------------------

    [Fact]
    public void Disabled_RecordReference_IsNoOp()
    {
        SetEnabled(false);
        AssetProvenanceDiag.RecordReference(@"meshes\ignored.nif", "Cool NPCs", Ctx(Target1, "Lydia"));
        SetEnabled(true);

        var rows = DataRows(FlushAndRead());

        rows.Should().NotContain(r => Cells(r)[DestFile] == @"meshes\ignored.nif",
            "recording is skipped entirely while disabled");
    }

    [Fact]
    public void Disabled_Flush_WritesNothingAndDoesNotThrow()
    {
        AssetProvenanceDiag.RecordReference(@"meshes\x.nif", "Cool NPCs", Ctx(Target1, "Lydia"));
        SetEnabled(false);

        var act = () => AssetProvenanceDiag.Flush();

        act.Should().NotThrow();
        File.Exists(CsvPath).Should().BeFalse("a disabled Flush must not write the report");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>A non-face-swap context (donor == target) with reason "FaceGen".</summary>
    private static AssetRequestContext Ctx(FormKey target, string identifier)
        => new AssetRequestContext(identifier, target, target, null, "FaceGen");

    private static string FlushAndRead()
    {
        AssetProvenanceDiag.Flush();
        File.Exists(CsvPath).Should().BeTrue("Flush should have written the report");
        return File.ReadAllText(CsvPath);
    }

    /// <summary>Non-header, non-empty CSV lines.</summary>
    private static string[] DataRows(string csv) =>
        csv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();

    /// <summary>Naive comma split — safe because the test data never contains commas or quotes.</summary>
    private static string[] Cells(string row) => row.Split(',');

    private static string CsvPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CsvFileName);

    private static void DeleteCsv()
    {
        try { if (File.Exists(CsvPath)) File.Delete(CsvPath); } catch { /* best effort */ }
    }

    /// <summary>Toggles the diag on/off via its public settings hook — the same one the Settings
    /// checkbox calls. No dev file trigger is present in tests, so this cleanly enables/disables.</summary>
    private static void SetEnabled(bool value) => AssetProvenanceDiag.SetEnabled(value);
}
