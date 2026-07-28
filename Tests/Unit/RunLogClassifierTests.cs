using FluentAssertions;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="RunLogClassifier"/> decides the colour of every line in the Run tab's log. It has
/// to work off the message text because <c>AppendLog</c>'s <c>isError</c> flag is a
/// verbose-filter bypass rather than a severity — most warning call sites pass
/// <c>isError: true</c> purely to get past the verbose gate, so trusting the flag alone would
/// paint the entire warning vocabulary red.
///
/// The marker strings asserted here are the ones that actually occur in the backend
/// (Patcher/BsaHandler/AssetHandler/VM_Run): "ERROR: ", "  WARNING: ", "CRITICAL ERROR: ",
/// "FATAL: ", "SCREENING WARNING: ", "NIF TEXTURE ERROR: ". The splitting tests pin the
/// blank-line parity with the <c>StringBuilder.AppendLine</c> pipeline this replaced.
/// </summary>
public class RunLogClassifierTests
{
    // ==================================================================
    // Classify — marker recognition
    // ==================================================================

    [Theory]
    [InlineData("ERROR: Environment is not valid. Aborting.")]
    [InlineData("  ERROR stripping something")]
    [InlineData("CRITICAL ERROR: everything is on fire")]
    [InlineData("FATAL: An unexpected error occurred")]
    [InlineData("FATAL UI ERROR: dispatcher died")]
    [InlineData("SCREENING ERROR: bad master")]
    [InlineData("NIF TEXTURE ERROR: missing slot")]
    [InlineData("error: lowercase markers count too")]
    public void Classify_ErrorMarkers_AreErrors(string line)
    {
        RunLogClassifier.Classify(line, RunLogSeverity.Info).Should().Be(RunLogSeverity.Error);
    }

    [Theory]
    [InlineData("WARNING: Could not write bootstrap NPC_Token.json marker")]
    [InlineData("  WARNING: SomeNpc has no FaceGen")]
    [InlineData("      WARNING: SkyPatcher entry skipped")]
    [InlineData("SCREENING WARNING: race mismatch")]
    [InlineData("Warning: No NPCs found in group 'Bandits'. File will be empty.")]
    public void Classify_WarningMarkers_AreWarnings(string line)
    {
        RunLogClassifier.Classify(line, RunLogSeverity.Info).Should().Be(RunLogSeverity.Warning);
    }

    [Fact]
    public void Classify_WarningMarker_BeatsTheIsErrorFallback()
    {
        // The single most important case: ~70 backend call sites pass warnings with
        // isError: true (the only "always show" channel), so the marker must win.
        RunLogClassifier.Classify("WARNING: Could not write marker", RunLogSeverity.Error)
            .Should().Be(RunLogSeverity.Warning);
    }

    [Theory]
    [InlineData("Pre-Indexing loose file paths...")]
    [InlineData("Note: some NPCs were skipped")]
    [InlineData("--- Loading resources for batch: Bandits ---")]
    public void Classify_PlainLines_KeepTheFallback(string line)
    {
        RunLogClassifier.Classify(line, RunLogSeverity.Info).Should().Be(RunLogSeverity.Info);
        RunLogClassifier.Classify(line, RunLogSeverity.Error).Should().Be(RunLogSeverity.Error);
    }

    // ==================================================================
    // Classify — successful non-asset file writes
    // ==================================================================

    /// <summary>
    /// The exact strings the backend emits when it finishes writing a file the user cares
    /// about. If one of these is reworded and the leading verb is lost it silently stops being
    /// green, which is what this test is here to catch.
    /// </summary>
    [Theory]
    [InlineData("Saved plugin: C:\\Out\\MyPatch.esp.")]                                  // Patcher.cs
    [InlineData("Successfully wrote unified NPC_Token.json to C:\\Out\\NPC_Token.json")]  // Patcher.cs
    [InlineData("Wrote bootstrap NPC_Token.json marker.")]                               // Patcher.cs
    [InlineData("Saved SkyPatcher Ini File to C:\\Out\\npc.ini")]                        // SkyPatcherInterface.cs
    [InlineData("Saved SPID outfit ini (42 NPC(s)) to C:\\Out\\_DISTR.ini")]             // ForwardedOutfitDistributor.cs
    [InlineData("Successfully generated spawn bat file with 12 NPC(s) at: C:\\spawn.txt")] // VM_Run.cs
    public void Classify_FileWriteConfirmations_AreSuccesses(string line)
    {
        RunLogClassifier.Classify(line, RunLogSeverity.Info).Should().Be(RunLogSeverity.Success);
    }

    /// <summary>
    /// The exclusions that justify anchoring the success markers to the first word: NPC-asset
    /// traffic and non-write progress chatter must stay plain, even though several of these
    /// lines do contain a marker word.
    /// </summary>
    [Theory]
    [InlineData("Verification complete. All assets copied successfully.")] // NPC assets - excluded by request
    [InlineData("      Wig conversion: wrote rewritten physics XML 'a.xml' (2 shape entries renamed).")]
    [InlineData("Asset Transfer: 120 remaining files (80/200 total)")]
    [InlineData("Created 3 patching batches.")]            // not a file write
    [InlineData("Finished Pre-Indexing loose file paths.")] // not a file write
    [InlineData("      Copied appearance fields from Mod.esp to 000ABC in patch.")]
    [InlineData("Output mod not saved as no changes were made.")]
    public void Classify_NonWriteAndAssetLines_AreNotSuccesses(string line)
    {
        RunLogClassifier.Classify(line, RunLogSeverity.Info).Should().Be(RunLogSeverity.Info);
    }

    [Fact]
    public void Classify_ProblemOnASavedLine_OutranksTheSuccessMarker()
    {
        RunLogClassifier.Classify("Saved plugin: X (WARNING: masters were trimmed)", RunLogSeverity.Info)
            .Should().Be(RunLogSeverity.Warning);
        RunLogClassifier.Classify("Saved plugin: X but ERROR followed", RunLogSeverity.Info)
            .Should().Be(RunLogSeverity.Error);
    }

    [Fact]
    public void Classify_SuccessMarkerIsNotInheritedByFollowingLines()
    {
        // Success is only ever recognised per-line, never seeded as a fallback, so the detail
        // lines under a write confirmation stay plain.
        var lines = RunLogClassifier.SplitIntoLines(new RunLogEntry(
            "Saved plugin: MyPatch.esp.\n  12 records written.", RunLogSeverity.Info));

        lines.Select(l => l.Severity).Should().Equal(RunLogSeverity.Success, RunLogSeverity.Info);
    }

    [Theory]
    [InlineData("Errors: 0")]          // plural -> not the "ERROR" marker word
    [InlineData("0 warnings raised")]  // plural -> not the "WARNING" marker word
    [InlineData("Copied Errored.dds")] // marker word must stand alone
    public void Classify_WordBoundariesPreventFalsePositives(string line)
    {
        RunLogClassifier.Classify(line, RunLogSeverity.Info).Should().Be(RunLogSeverity.Info);
    }

    [Fact]
    public void Classify_MarkerBeyondThePrefixWindow_IsIgnored()
    {
        // A passing mention deep in a sentence must not recolour an informational line.
        var line = new string('x', 80) + " error";
        RunLogClassifier.Classify(line, RunLogSeverity.Info).Should().Be(RunLogSeverity.Info);
    }

    [Fact]
    public void Classify_BothMarkersInPrefix_EarliestWins()
    {
        RunLogClassifier.Classify("WARNING: an ERROR may follow", RunLogSeverity.Info)
            .Should().Be(RunLogSeverity.Warning);
        RunLogClassifier.Classify("ERROR: warning signs were ignored", RunLogSeverity.Info)
            .Should().Be(RunLogSeverity.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_BlankLines_KeepTheFallback(string line)
    {
        RunLogClassifier.Classify(line, RunLogSeverity.Warning).Should().Be(RunLogSeverity.Warning);
    }

    // ==================================================================
    // SplitIntoLines
    // ==================================================================

    [Fact]
    public void SplitIntoLines_SingleLine_ProducesOneEntry()
    {
        var lines = RunLogClassifier.SplitIntoLines(new RunLogEntry("hello", RunLogSeverity.Info));

        lines.Should().ContainSingle();
        lines[0].Text.Should().Be("hello");
        lines[0].Severity.Should().Be(RunLogSeverity.Info);
    }

    [Fact]
    public void SplitIntoLines_ClassifiesEachLineIndependently()
    {
        var message = new RunLogEntry(
            "Finished processing.\nWARNING: 3 NPCs had no FaceGen.\nDone.",
            RunLogSeverity.Info);

        var lines = RunLogClassifier.SplitIntoLines(message);

        lines.Select(l => l.Severity).Should().Equal(
            RunLogSeverity.Info, RunLogSeverity.Warning, RunLogSeverity.Info);
    }

    [Fact]
    public void SplitIntoLines_ExceptionStackInheritsTheHeaderSeverity()
    {
        // "ERROR: ..." followed by unmarked stack frames: the frames carry no marker, so the
        // message-level severity (seeded from isError) has to colour them red as well.
        var message = new RunLogEntry(
            "ERROR: Failed to copy file\n   at NPC_Plugin_Chooser_2.BackEnd.AssetHandler.Copy()\n   at Patcher.Run()",
            RunLogSeverity.Error);

        var lines = RunLogClassifier.SplitIntoLines(message);

        lines.Should().HaveCount(3);
        lines.Should().OnlyContain(l => l.Severity == RunLogSeverity.Error);
    }

    [Fact]
    public void SplitIntoLines_StripsCarriageReturns()
    {
        var lines = RunLogClassifier.SplitIntoLines(
            new RunLogEntry("first\r\nsecond", RunLogSeverity.Info));

        lines.Select(l => l.Text).Should().Equal("first", "second");
    }

    [Fact]
    public void SplitIntoLines_LeadingNewline_KeepsTheBlankSeparatorLine()
    {
        // Parity with the old StringBuilder.AppendLine("\nProcessed...") behaviour: the blank
        // spacer line the patcher relies on for readability must survive.
        var lines = RunLogClassifier.SplitIntoLines(
            new RunLogEntry("\nProcessed 5 NPC(s).", RunLogSeverity.Info));

        lines.Select(l => l.Text).Should().Equal("", "Processed 5 NPC(s).");
    }

    [Fact]
    public void SplitIntoLines_EnvironmentNewLineOnly_ProducesTwoBlankLines()
    {
        // AppendLog(Environment.NewLine) used to render two blank lines via AppendLine; keep it.
        var lines = RunLogClassifier.SplitIntoLines(
            new RunLogEntry(Environment.NewLine, RunLogSeverity.Info));

        lines.Select(l => l.Text).Should().Equal("", "");
    }
}
