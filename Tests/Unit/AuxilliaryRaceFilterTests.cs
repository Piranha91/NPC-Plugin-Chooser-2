using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The pure string logic behind the Race filter (NPCs menu + Favorite Faces window):
/// <see cref="Auxilliary.ParseRaceSearchTerm"/> (the trailing-'~' exact terminator) and
/// <see cref="Auxilliary.RaceMatches"/> (partial vs. exact matching over race Name/EditorID).
/// No environment or game install.
/// </summary>
public class AuxilliaryRaceFilterTests
{
    [Theory]
    [InlineData("NordRace", "NordRace", false)]
    [InlineData("NordRace~", "NordRace", true)]
    [InlineData("  NordRace~  ", "NordRace", true)]
    [InlineData("Nord", "Nord", false)]
    [InlineData("~", "", true)]
    [InlineData("", "", false)]
    [InlineData(null, "", false)]
    public void ParseRaceSearchTerm_StripsTerminatorAndTrims(string? input, string term, bool exact)
    {
        var result = Auxilliary.ParseRaceSearchTerm(input);
        result.Term.Should().Be(term);
        result.Exact.Should().Be(exact);
    }

    [Fact]
    public void RaceMatches_Partial_MatchesSubstringInEditorIdOrName()
    {
        // EditorID substring (case-insensitive)
        Auxilliary.RaceMatches("Nord", "NordRace", "nordrace", exact: false).Should().BeTrue();
        // A partial term still matches the longer variant
        Auxilliary.RaceMatches("Nord", "NordRaceVampire", "NordRace", exact: false).Should().BeTrue();
        // Name substring
        Auxilliary.RaceMatches("Nord", "SomeEditorId", "nor", exact: false).Should().BeTrue();
        // No match
        Auxilliary.RaceMatches("Nord", "NordRace", "imperial", exact: false).Should().BeFalse();
    }

    [Fact]
    public void RaceMatches_Exact_RequiresWholeStringMatch()
    {
        // The point of '~': exact EditorID excludes the longer variant.
        Auxilliary.RaceMatches("Nord", "NordRace", "NordRace", exact: true).Should().BeTrue();
        Auxilliary.RaceMatches("Nord", "NordRaceVampire", "NordRace", exact: true).Should().BeFalse();
        // Exact is case-insensitive.
        Auxilliary.RaceMatches("Nord", "NordRace", "nordrace", exact: true).Should().BeTrue();
        // Exact can match the Name side too.
        Auxilliary.RaceMatches("Nord", "NordRace", "nord", exact: true).Should().BeTrue();
        Auxilliary.RaceMatches("Nord", "NordRace", "nor", exact: true).Should().BeFalse();
    }

    [Fact]
    public void RaceMatches_EmptyTerm_ReturnsFalse()
    {
        Auxilliary.RaceMatches("Nord", "NordRace", "", exact: false).Should().BeFalse();
        Auxilliary.RaceMatches("Nord", "NordRace", "", exact: true).Should().BeFalse();
    }

    [Fact]
    public void RaceMatches_NullNameAndEditorId_DoNotThrowAndDoNotMatch()
    {
        Auxilliary.RaceMatches(null, null, "NordRace", exact: false).Should().BeFalse();
        Auxilliary.RaceMatches(null, null, "NordRace", exact: true).Should().BeFalse();
    }

    [Theory]
    [InlineData("Nord", "NordRace", "Nord (NordRace)")]
    [InlineData(null, "NordRace", "NordRace")]
    [InlineData("Nord", null, "Nord")]
    [InlineData("NordRace", "NordRace", "NordRace")]   // identical => no redundant "(…)"
    [InlineData("  ", "  ", null)]
    [InlineData(null, null, null)]
    public void CombineRaceLabel_FormatsNameEditorId(string? name, string? editorId, string? expected)
    {
        Auxilliary.CombineRaceLabel(name, editorId).Should().Be(expected);
    }

    [Fact]
    public void RaceMatches_PickedCombinedLabel_MatchesThatRaceOnly()
    {
        // Selecting "Nord (NordRace)" from the dropdown must match the NordRace race...
        Auxilliary.RaceMatches("Nord", "NordRace", "Nord (NordRace)", exact: false).Should().BeTrue();
        // ...but not the vampire variant (the trailing ')' keeps it from matching).
        Auxilliary.RaceMatches("Nord", "NordRaceVampire", "Nord (NordRace)", exact: false).Should().BeFalse();
        // Exact on the combined label also works.
        Auxilliary.RaceMatches("Nord", "NordRace", "Nord (NordRace)", exact: true).Should().BeTrue();
    }

    [Fact]
    public void BuildRaceFilterOptions_CombinesNameAndEditorId_SortedAndDistinct()
    {
        var options = Auxilliary.BuildRaceFilterOptions(new (string?, string?)[]
        {
            ("Nord", "NordRace"),
            ("Imperial", "ImperialRace"),
            ("Nord", "NordRaceVampire"),   // same Name, different EditorID => distinct entries
        });

        options.Should().Equal("Imperial (ImperialRace)", "Nord (NordRace)", "Nord (NordRaceVampire)");
    }

    [Fact]
    public void BuildRaceFilterOptions_SkipsNullAndWhitespace()
    {
        var options = Auxilliary.BuildRaceFilterOptions(new (string?, string?)[]
        {
            (null, "NordRace"),
            ("   ", "  "),
            ("Breton", null),
        });

        options.Should().Equal("Breton", "NordRace");
    }

    [Fact]
    public void BuildRaceFilterOptions_DedupesCaseInsensitively()
    {
        var options = Auxilliary.BuildRaceFilterOptions(new (string?, string?)[]
        {
            ("Nord", "NordRace"),
            ("nord", "nordrace"),
        });

        // Both collapse to one entry; the first-seen casing is kept.
        options.Should().ContainSingle().Which.Should().Be("Nord (NordRace)");
    }
}
