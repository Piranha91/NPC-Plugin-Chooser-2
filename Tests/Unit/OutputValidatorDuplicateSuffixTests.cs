using System;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <c>OutputValidator.StripDuplicateSuffix</c> — normalises the source-plugin suffix this app
/// appends when it duplicates a record into the output (<c>RecordHandler</c> mints
/// <c>EditorID + "_" + ModKey</c> at three sites).
///
/// <para><b>The bug it fixes.</b> "Include As New" exists to give a mod its own copy of a shared
/// record — the patcher's own race-drift advice tells users to switch it on — but the renamed
/// duplicate then failed the output validator's EditorID comparison, so every NPC the mode touched
/// was reported as an Error. On the measuring run that was all 11 RS Children NPCs
/// (<c>NordRaceChild</c> vs <c>NordRaceChild_RSChildren.esp</c>,
/// <c>HairMaleImperialChild01</c> vs <c>HairMaleImperialChild01_RSkyrimChildren.esm</c>) — the app
/// flagging its own correct output, on the strength of its own advice.</para>
///
/// <para>Matching is against WHOLE plugin names, never "text after the last underscore", because
/// plugin names contain underscores of their own. Pure static, no game install.</para>
/// </summary>
public class OutputValidatorDuplicateSuffixTests
{
    private static readonly ModKey RsChildren = ModKey.FromFileName("RSChildren.esp");
    private static readonly ModKey RsKyrim = ModKey.FromFileName("RSkyrimChildren.esm");
    private static readonly ModKey Ocw = ModKey.FromFileName("OCW_Obscure's_CollegeofWinterhold.esp");
    private static readonly ModKey Children = ModKey.FromFileName("Children.esp");

    private static string Strip(string? eid, params ModKey[] candidates) =>
        OutputValidator.StripDuplicateSuffix(eid, candidates);

    // ── The real specimens ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("NordRaceChild_RSChildren.esp", "NordRaceChild")]
    [InlineData("RedguardRaceChild_RSChildren.esp", "RedguardRaceChild")]
    [InlineData("ImperialRaceChild_RSChildren.esp", "ImperialRaceChild")]
    public void RaceDuplicate_NormalisesToTheRecordItCopied(string minted, string expected)
    {
        Strip(minted, RsChildren, RsKyrim).Should().Be(expected);
    }

    [Theory]
    [InlineData("HairMaleImperialChild01_RSkyrimChildren.esm", "HairMaleImperialChild01")]
    [InlineData("HairLineFemaleNordChild02_RSkyrimChildren.esm", "HairLineFemaleNordChild02")]
    public void HeadPartDuplicate_NormalisesToTheRecordItCopied(string minted, string expected)
    {
        Strip(minted, RsChildren, RsKyrim).Should().Be(expected);
    }

    [Fact]
    public void BothSidesNormalise_ToTheSameKey()
    {
        // The property that actually matters: the comparison equates them.
        var donorSide = Strip("NordRaceChild", RsChildren, RsKyrim);
        var outputSide = Strip("NordRaceChild_RSChildren.esp", RsChildren, RsKyrim);

        outputSide.Should().Be(donorSide);
    }

    // ── Narrowness ──────────────────────────────────────────────────────────────

    [Fact]
    public void UnsuffixedEditorId_IsUntouched()
    {
        Strip("HairMaleImperialChild01", RsChildren, RsKyrim).Should().Be("HairMaleImperialChild01");
    }

    [Fact]
    public void SuffixFromAPluginNotInTheCandidateSet_IsNotStripped()
    {
        // Whole-name matching against a known set is what keeps this from eating real text.
        Strip("HairMaleImperialChild01_SomeOtherMod.esp", RsChildren, RsKyrim)
            .Should().Be("HairMaleImperialChild01_SomeOtherMod.esp");
    }

    [Fact]
    public void PluginNameContainingUnderscores_StripsWhole()
    {
        // Scanning back to the last underscore would leave "HairFemaleOrc07_OCW_Obscure's" here.
        Strip("HairFemaleOrc07_OCW_Obscure's_CollegeofWinterhold.esp", Ocw)
            .Should().Be("HairFemaleOrc07");
    }

    [Fact]
    public void LongestMatchWins_WhenOnePluginNameTailsAnother()
    {
        // "Children.esp" is a tail of "RSChildren.esp"; matching the short one would leave a
        // dangling "_RS" on the stem.
        Strip("NordRaceChild_RSChildren.esp", Children, RsChildren).Should().Be("NordRaceChild");
    }

    [Fact]
    public void SuffixAloneLeavesNothing_SoItIsKept()
    {
        // "_RSChildren.esp" would strip to the empty string; an empty identity compares equal to
        // every other empty identity, so refuse it.
        Strip("_RSChildren.esp", RsChildren).Should().Be("_RSChildren.esp");
    }

    [Fact]
    public void MatchingIsCaseInsensitive_LikeEveryOtherEditorIdComparison()
    {
        Strip("NordRaceChild_rschildren.ESP", RsChildren).Should().Be("NordRaceChild");
    }

    [Fact]
    public void NoCandidates_IsANoOp()
    {
        Strip("NordRaceChild_RSChildren.esp").Should().Be("NordRaceChild_RSChildren.esp");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_ReturnsEmpty(string? eid)
    {
        Strip(eid, RsChildren).Should().BeEmpty();
    }

    [Fact]
    public void OnlyOneSuffixIsRemoved()
    {
        // A record duplicated twice would carry two suffixes; stripping one per comparison keeps
        // the operation predictable rather than chewing back through the whole EditorID.
        Strip("HairA_RSkyrimChildren.esm_RSChildren.esp", RsChildren, RsKyrim)
            .Should().Be("HairA_RSkyrimChildren.esm");
    }
}
