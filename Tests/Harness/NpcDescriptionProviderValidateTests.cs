using System;
using System.Collections.Generic;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// <see cref="NpcDescriptionProvider"/> — only the deterministic, pure
/// <c>ValidateDescription(string?, HashSet&lt;string&gt;)</c> helper is exercised here.
/// It is a private <em>instance</em> method that reads no instance state, so we allocate
/// an uninitialised provider (skipping the heavy ctor, which builds an HttpClient and
/// touches the filesystem) and invoke it via <see cref="Reflect"/>.
///
/// Contract (read from source): returns <c>false</c> when the description is null/empty/
/// whitespace OR when the keyword set is empty; otherwise returns <c>true</c> iff the
/// description contains at least one keyword via <see cref="StringComparison.OrdinalIgnoreCase"/>.
///
/// NOTE: GetDescriptionAsync / SearchWikiAsync / FetchAndParseDescriptionAsync not covered:
/// they require HTTP (network) and/or the file-seeding ctor + IHttpClientFactory; those
/// gating + wiki-fetch tests belong to the later integration wave.
/// NOTE: ctor / Initialize() not covered: same reason (filesystem seeding + HttpClient).
/// </summary>
public class NpcDescriptionProviderValidateTests
{
    private static readonly NpcDescriptionProvider Provider =
        Reflect.Uninitialized<NpcDescriptionProvider>();

    private static bool Validate(string? description, HashSet<string> keywords) =>
        Reflect.Invoke<bool>(Provider, "ValidateDescription", description, keywords);

    private static HashSet<string> Keywords(params string[] words) =>
        new(words, StringComparer.OrdinalIgnoreCase);

    // --- Null / empty / whitespace description -> false (regardless of keywords) ---

    [Fact]
    public void NullDescription_ReturnsFalse()
    {
        Validate(null, Keywords("Lydia")).Should().BeFalse();
    }

    [Fact]
    public void EmptyDescription_ReturnsFalse()
    {
        Validate(string.Empty, Keywords("Lydia")).Should().BeFalse();
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    [InlineData(" \t \r\n ")]
    public void WhitespaceOnlyDescription_ReturnsFalse(string description)
    {
        Validate(description, Keywords("Lydia")).Should().BeFalse();
    }

    // --- Empty keyword set -> false (even with a real description) ---

    [Fact]
    public void EmptyKeywords_ReturnsFalse()
    {
        Validate("Lydia is a housecarl in Whiterun.", Keywords()).Should().BeFalse();
    }

    [Fact]
    public void EmptyKeywords_AndEmptyDescription_ReturnsFalse()
    {
        Validate(string.Empty, Keywords()).Should().BeFalse();
    }

    [Fact]
    public void NullDescription_AndEmptyKeywords_ReturnsFalse()
    {
        Validate(null, Keywords()).Should().BeFalse();
    }

    // --- Match present -> true ---

    [Fact]
    public void ContainsKeyword_ReturnsTrue()
    {
        Validate("Lydia is a housecarl in Whiterun.", Keywords("Lydia")).Should().BeTrue();
    }

    [Fact]
    public void ContainsOneOfSeveralKeywords_ReturnsTrue()
    {
        // Only "Whiterun" is present; one match is sufficient.
        Validate("Lydia is a housecarl in Whiterun.", Keywords("Solitude", "Whiterun", "Riften"))
            .Should().BeTrue();
    }

    // --- Case-insensitive matching (OrdinalIgnoreCase) ---

    [Theory]
    [InlineData("lydia")]
    [InlineData("LYDIA")]
    [InlineData("LyDiA")]
    public void KeywordCasing_IsIgnored(string keyword)
    {
        Validate("Lydia is a housecarl in Whiterun.", Keywords(keyword)).Should().BeTrue();
    }

    [Theory]
    [InlineData("lydia is a housecarl.")]
    [InlineData("LYDIA IS A HOUSECARL.")]
    public void DescriptionCasing_IsIgnored(string description)
    {
        Validate(description, Keywords("Lydia")).Should().BeTrue();
    }

    // --- Substring (Contains) semantics: keyword need not be a whole word ---

    [Fact]
    public void KeywordAsSubstring_ReturnsTrue()
    {
        // "carl" is a substring of "housecarl".
        Validate("Lydia is a housecarl.", Keywords("carl")).Should().BeTrue();
    }

    // --- No match -> false ---

    [Fact]
    public void NoKeywordMatches_ReturnsFalse()
    {
        Validate("Lydia is a housecarl in Whiterun.", Keywords("Argonian", "Markarth"))
            .Should().BeFalse();
    }

    [Fact]
    public void SingleKeywordAbsent_ReturnsFalse()
    {
        Validate("A simple bandit.", Keywords("Lydia")).Should().BeFalse();
    }
}
