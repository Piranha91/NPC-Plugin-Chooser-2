using System;
using System.Linq;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pins the "Rejected NPCs" log format the Settings viewer reads back. The logs are free text
/// produced by VM_ModSetting.RefreshNpcLists, so the parser has to survive the awkward cases the
/// writer can emit: NPC names and mod names that themselves contain " from " / " because ", the
/// doubled period from a reason that already ends in one, identifiers where either the name or
/// the EditorID is absent, and exception entries whose stack trace spans several lines.
/// </summary>
public class RejectedNpcLogParserTests
{
    [Fact]
    public void ParsesNameOnlyDiscardLine()
    {
        var records = RejectedNpcLogParser.ParseLines(new[]
        {
            "Discarded Shadow Atronach from Abyss because its race is missing the ActorTypeNPC keyword.."
        });

        var record = records.Should().ContainSingle().Subject;
        record.Label.Should().Be("Shadow Atronach");
        record.ModName.Should().Be("Abyss");
        record.EditorId.Should().BeEmpty();
        record.FormKey.Should().BeEmpty();
        // Trailing period run collapsed, sentence fragment capitalized for standalone display.
        record.Reason.Should().Be("Its race is missing the ActorTypeNPC keyword.");
    }

    [Fact]
    public void ParsesFullIdentifierIntoNameEditorIdAndFormKey()
    {
        var records = RejectedNpcLogParser.ParseLines(new[]
        {
            "Discarded Jyrik Gauldurson | JyrikGauldurson | 01BB28:Skyrim.esm from ESLifier Compactor Output " +
            "because its template chain terminates in a Leveled NPC."
        });

        var record = records.Should().ContainSingle().Subject;
        record.Label.Should().Be("Jyrik Gauldurson");
        record.EditorId.Should().Be("JyrikGauldurson");
        record.FormKey.Should().Be("01BB28:Skyrim.esm");
        record.ModName.Should().Be("ESLifier Compactor Output");
        record.Reason.Should().Be("Its template chain terminates in a Leveled NPC.");
    }

    [Fact]
    public void SplitsOnTheLastFromWhenOnlyTheNpcNameContainsIt()
    {
        var records = RejectedNpcLogParser.ParseLines(new[]
        {
            "Discarded Ghost from the Past from Abyss because it has no FaceGen and does not use a template."
        });

        var record = records.Should().ContainSingle().Subject;
        record.Label.Should().Be("Ghost from the Past");
        record.ModName.Should().Be("Abyss");
        record.Reason.Should().Be("It has no FaceGen and does not use a template.");
    }

    [Fact]
    public void UsesTheFileNameHintWhenBothNamesContainFrom()
    {
        const string line =
            "Discarded Ghost from the Past from Tales from Skyrim because its race is null..";

        // Without the hint the boundary is genuinely ambiguous and the last " from " wins.
        var unhinted = RejectedNpcLogParser.ParseLines(new[] { line }).Single();
        unhinted.ModName.Should().Be("Skyrim");

        // The log's file name resolves it exactly.
        var hinted = RejectedNpcLogParser.ParseLines(new[] { line }, "Tales from Skyrim").Single();
        hinted.Label.Should().Be("Ghost from the Past");
        hinted.ModName.Should().Be("Tales from Skyrim");
    }

    [Fact]
    public void MatchesTheHintThroughPathSafeSubstitution()
    {
        // MakeStringPathSafe replaces ':' with '_' when naming the log file, so the hint never
        // equals the display name verbatim for mods with punctuation in their names.
        var record = RejectedNpcLogParser.ParseLines(
            new[] { "Discarded Lydia from Bijin NPCs: Redux because its race is null.." },
            "Bijin NPCs_ Redux").Single();

        record.Label.Should().Be("Lydia");
        record.ModName.Should().Be("Bijin NPCs: Redux");
    }

    [Fact]
    public void TreatsSingleUnspacedIdentifierPartAsEditorId()
    {
        var records = RejectedNpcLogParser.ParseLines(new[]
        {
            "Discarded EncBanditMelee01 | 0206A3:Skyrim.esm from Base Game because its race is missing Keywords.."
        });

        var record = records.Should().ContainSingle().Subject;
        record.EditorId.Should().Be("EncBanditMelee01");
        record.FormKey.Should().Be("0206A3:Skyrim.esm");
        record.Label.Should().Be("EncBanditMelee01");
    }

    [Fact]
    public void ParsesAmbiguousSourcePluginError()
    {
        var records = RejectedNpcLogParser.ParseLines(new[]
        {
            "ERROR for ModSetting 'Bijin NPCs': NPC 0198B0:Skyrim.esm found in multiple associated plugins: " +
            "A.esp, B.esp, but no valid default source could be determined. This NPC will be skipped for this Mod Setting."
        });

        var record = records.Should().ContainSingle().Subject;
        record.ModName.Should().Be("Bijin NPCs");
        record.FormKey.Should().Be("0198B0:Skyrim.esm");
        record.Label.Should().Be("0198B0:Skyrim.esm");
        record.Reason.Should().StartWith("Found in multiple associated plugins:");
    }

    [Fact]
    public void AttachesContinuationLinesToTheEntryThatStartedThem()
    {
        var records = RejectedNpcLogParser.ParseLines(new[]
        {
            "Error loading NPC data for ModSetting 'Broken Mod': ",
            "System.InvalidOperationException: something went wrong",
            "   at Some.Method()",
            "Discarded Lydia from Broken Mod because its race is null.."
        });

        records.Should().HaveCount(2);

        var error = records[0];
        error.ModName.Should().Be("Broken Mod");
        error.Label.Should().Be("(error loading NPC data)");
        error.Reason.Should().Contain("System.InvalidOperationException");
        error.Reason.Should().Contain("at Some.Method()");
        error.RawText.Should().Contain("at Some.Method()");

        records[1].Label.Should().Be("Lydia");
        records[1].Reason.Should().Be("Its race is null.");
    }

    [Fact]
    public void SkipsBlankLines()
    {
        var records = RejectedNpcLogParser.ParseLines(new[]
        {
            string.Empty,
            "Discarded Lydia from A Mod because its race is null..",
            "   ",
            string.Empty
        });

        records.Should().ContainSingle();
        records.Single().Reason.Should().Be("Its race is null.");
    }
}
