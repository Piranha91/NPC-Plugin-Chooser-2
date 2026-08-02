using System.IO;
using System.Text;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <c>OutputValidator.ClassifyLastRunCoverage</c> — the triage that decides whether the deeper
/// record/FaceGen/asset checks mean anything for a given NPC.
///
/// This exists because "Validate Output" scopes itself from the LIVE selections, while the
/// patcher only writes the ones that survived pre-run screening. Without this triage an NPC the
/// patcher deliberately never touched was graded as though it had been patched, and reported as
/// a stack of Errors naming a conflict winner that this app never competed with — i.e. blaming
/// NPC2 for output it did not produce. The rows must instead read as Info: the user already
/// acknowledged the skip in the screening dialog.
///
/// Pure and deterministic: an in-memory <see cref="NpcToken"/>, no game install, no filesystem
/// except the one round-trip test that proves the ledger survives serialization.
/// </summary>
public class OutputValidatorLastRunCoverageTests
{
    private static readonly FormKey Aera = FormKey.Factory("07D7AA:BSHeartland.esm");
    private static readonly FormKey Assur = FormKey.Factory("01C18A:Skyrim.esm");
    private static readonly ModKey Output = ModKey.FromFileName("NPC.esp");
    private static readonly ModKey Appearance = ModKey.FromFileName("RSkyrimChildren.esm");

    private static NpcToken Ledger(
        (FormKey Npc, string Mod)[]? processed = null,
        (FormKey Npc, string Reason)[]? skipped = null)
    {
        var token = new NpcToken { CreationDate = "2026-08-01T22:05:03.0000000-07:00" };
        token.CreatedPlugins.Add(Output);
        foreach (var (npc, mod) in processed ?? [])
        {
            token.ProcessedNpcs[npc] = new NpcAppearanceData
            {
                ModName = mod, AppearancePlugin = Appearance, OutputPlugin = Output
            };
        }
        foreach (var (npc, reason) in skipped ?? []) token.SkippedNpcs[npc] = reason;
        return token;
    }

    // ── Covered ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PatchedWithTheSelectedMod_IsCovered()
    {
        var ledger = Ledger(processed: [(Assur, "RS Children Overhaul")]);

        var coverage = OutputValidator.ClassifyLastRunCoverage(Assur, "RS Children Overhaul", ledger, out var detail);

        coverage.Should().Be(OutputValidator.LastRunCoverage.Covered);
        detail.Should().BeNull();
    }

    [Fact]
    public void ModNameComparison_IsCaseInsensitive()
    {
        // Display names round-trip through JSON and the settings UI; a case difference is not a
        // selection change and must not fire the stale-output warning.
        var ledger = Ledger(processed: [(Assur, "RS Children Overhaul")]);

        OutputValidator.ClassifyLastRunCoverage(Assur, "rs children overhaul", ledger, out _)
            .Should().Be(OutputValidator.LastRunCoverage.Covered);
    }

    // ── Not patched ─────────────────────────────────────────────────────────────

    [Fact]
    public void AbsentFromTheLedger_IsNotPatched_AndQuotesTheRecordedReason()
    {
        // The real shape of the 2026-08-01 run: screening rejected the selection, the patcher
        // never saw the NPC, and BSHeartland.esm stayed the conflict winner.
        var ledger = Ledger(
            processed: [(Assur, "RS Children Overhaul")],
            skipped: [(Aera, "Skipped before patching: Appearance references a record missing from your 'BSHeartland.esm'")]);

        var coverage = OutputValidator.ClassifyLastRunCoverage(Aera, "Beyond Skyrim Bruma NPC replacer", ledger, out var detail);

        coverage.Should().Be(OutputValidator.LastRunCoverage.NotPatched);
        detail.Should().Contain("Appearance references a record missing");
    }

    [Fact]
    public void AbsentWithNoRecordedReason_IsStillNotPatched()
    {
        // Tokens written before SkippedNpcs existed carry no reasons. Absence from the processed
        // set is the load-bearing signal; a missing reason must not be read as "covered".
        var ledger = Ledger(processed: [(Assur, "RS Children Overhaul")]);

        var coverage = OutputValidator.ClassifyLastRunCoverage(Aera, "Beyond Skyrim Bruma NPC replacer", ledger, out var detail);

        coverage.Should().Be(OutputValidator.LastRunCoverage.NotPatched);
        detail.Should().BeNull();
    }

    [Fact]
    public void FaceGenLadderAbort_IsNotPatched()
    {
        // The other skip source: NPC2 patched nothing on purpose, to avoid the dark-face bug.
        // Same verdict as a screening rejection — nothing of ours is in the game either way.
        var ledger = Ledger(
            processed: [(Assur, "RS Children Overhaul")],
            skipped: [(Aera, "Face could not be assembled safely: supplies a face tint but no face mesh")]);

        OutputValidator.ClassifyLastRunCoverage(Aera, "Beyond Skyrim - Assets", ledger, out var detail)
            .Should().Be(OutputValidator.LastRunCoverage.NotPatched);
        detail.Should().Contain("assembled safely");
    }

    // ── Selection changed ───────────────────────────────────────────────────────

    [Fact]
    public void PatchedWithADifferentMod_IsSelectionChanged_AndNamesTheModUsed()
    {
        var ledger = Ledger(processed: [(Assur, "RS Children Overhaul")]);

        var coverage = OutputValidator.ClassifyLastRunCoverage(Assur, "Children of the Hist", ledger, out var detail);

        coverage.Should().Be(OutputValidator.LastRunCoverage.SelectionChanged);
        detail.Should().Be("RS Children Overhaul");
    }

    [Fact]
    public void ProcessedSetWins_OverAStaleSkipEntry()
    {
        // Defensive: a re-run that patches an NPC previously skipped should read as patched even
        // if a stale reason lingers, since the processed set is what the output actually contains.
        var ledger = Ledger(
            processed: [(Aera, "Beyond Skyrim Bruma NPC replacer")],
            skipped: [(Aera, "Skipped before patching: some earlier reason")]);

        OutputValidator.ClassifyLastRunCoverage(Aera, "Beyond Skyrim Bruma NPC replacer", ledger, out _)
            .Should().Be(OutputValidator.LastRunCoverage.Covered);
    }

    // ── Ledger persistence ──────────────────────────────────────────────────────

    [Fact]
    public void SkippedNpcs_SurvivesTheJsonRoundTrip()
    {
        // The whole feature is worthless if the FormKey-keyed skip map does not persist, and the
        // validator reads the file the patcher wrote — never the in-memory object.
        var ledger = Ledger(
            processed: [(Assur, "RS Children Overhaul")],
            skipped: [(Aera, "Skipped before patching: Missing required master: SomeMod.esp")]);

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            JSONhandler<NpcToken>.SaveJSONFile(ledger, path, out bool saved, out string saveError);
            saved.Should().BeTrue(saveError);

            var reloaded = JSONhandler<NpcToken>.LoadJSONFile(path, out bool loaded, out string loadError);
            loaded.Should().BeTrue(loadError);
            reloaded.Should().NotBeNull();

            reloaded!.ProcessedNpcs.Should().ContainKey(Assur);
            reloaded.SkippedNpcs.Should().ContainKey(Aera);
            reloaded.SkippedNpcs[Aera].Should().Contain("Missing required master");

            OutputValidator.ClassifyLastRunCoverage(Aera, "Beyond Skyrim Bruma NPC replacer", reloaded, out var detail)
                .Should().Be(OutputValidator.LastRunCoverage.NotPatched);
            detail.Should().Contain("Missing required master");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void EditedFaceGen_SurvivesTheJsonRoundTrip()
    {
        // A FaceGen this run rewrote (shape rename / wig bake / hair strip) is deliberately no
        // longer byte-identical to the appearance mod's copy. That list is the only thing keeping
        // the validator from reporting our own edit as a lost conflict, so it has to persist.
        var ledger = Ledger(processed: [(Assur, "RS Children Overhaul")]);
        ledger.EditedFaceGen.Add(@"meshes\actors\character\facegendata\facegeom\skyrim.esm\0001c18a.nif");

        using var dir = new TempDir("ledger");
        JSONhandler<NpcToken>.SaveJSONFile(ledger, Path.Combine(dir.Path, "NPC_Token.json"),
            out bool saved, out string saveError);
        saved.Should().BeTrue(saveError);

        // Through the real read path, which is where the comparer is restored: Newtonsoft rebuilds
        // a HashSet with the DEFAULT comparer, so a raw deserialize is case-sensitive and the
        // lookup would depend on both ends happening to stay lowercase.
        var reloaded = Reflect.InvokeStatic<OutputValidator, NpcToken?>(
            "LoadDeployedRunLedger", dir.Path, new StringBuilder());

        reloaded.Should().NotBeNull();
        reloaded!.EditedFaceGen.Should().ContainSingle();
        reloaded.EditedFaceGen.Contains(
            @"MESHES\ACTORS\CHARACTER\FACEGENDATA\FACEGEOM\SKYRIM.ESM\0001C18A.NIF")
            .Should().BeTrue("the validator looks this up with a path built elsewhere");
    }

    [Fact]
    public void TokenWithoutSkippedNpcs_DeserializesToAnEmptyMap()
    {
        // Backwards compatibility: a token written before this field existed must load cleanly
        // rather than null-ref the triage.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(path,
                """
                {
                  "CreationDate": "2026-07-01T00:00:00.0000000-07:00",
                  "CreatedPlugins": [ "NPC.esp" ],
                  "ProcessedNpcs": {
                    "01C18A:Skyrim.esm": {
                      "ModName": "RS Children Overhaul",
                      "AppearancePlugin": "RSkyrimChildren.esm",
                      "OutputPlugin": "NPC.esp"
                    }
                  }
                }
                """);

            var reloaded = JSONhandler<NpcToken>.LoadJSONFile(path, out bool loaded, out string loadError);

            loaded.Should().BeTrue(loadError);
            reloaded!.SkippedNpcs.Should().NotBeNull().And.BeEmpty();
            OutputValidator.ClassifyLastRunCoverage(Assur, "RS Children Overhaul", reloaded, out _)
                .Should().Be(OutputValidator.LastRunCoverage.Covered);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
