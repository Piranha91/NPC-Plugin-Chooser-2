using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="Auxilliary.BuildFormIdPrefixes"/> / <see cref="Auxilliary.FormatFormId"/> — the
/// load-order-relative FormID shown in the validation report's FormID column.
///
/// <para>The rule that is easy to get wrong: full masters and light (ESL-flagged) masters are
/// numbered by TWO INDEPENDENT counters. An ESL sitting between two full masters does not consume
/// a full-master index, so a plugin's FormID prefix is NOT its position in the list. Shared with
/// <c>EnvironmentStateProvider.ComputeFormIdPrefixes</c> so the app and the report cannot disagree
/// about what a FormID is.</para>
///
/// <para>In-memory mods and listings; no game install.</para>
/// </summary>
public class FormIdPrefixTests
{
    private static IModListingGetter<ISkyrimModGetter> Listing(string name, bool esl = false)
    {
        var mod = new SkyrimMod(ModKey.FromFileName(name), SkyrimRelease.SkyrimSE);
        if (esl) mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
        return new ModListing<ISkyrimModGetter>(mod, enabled: true);
    }

    private static Dictionary<ModKey, string> Prefixes(params IModListingGetter<ISkyrimModGetter>[] listings) =>
        Auxilliary.BuildFormIdPrefixes(listings);

    [Fact]
    public void FullMasters_AreNumberedSequentiallyInHex()
    {
        var p = Prefixes(Listing("Skyrim.esm"), Listing("Update.esm"), Listing("Dawnguard.esm"));

        p[ModKey.FromFileName("Skyrim.esm")].Should().Be("00");
        p[ModKey.FromFileName("Update.esm")].Should().Be("01");
        p[ModKey.FromFileName("Dawnguard.esm")].Should().Be("02");
    }

    [Fact]
    public void LightMasters_UseTheirOwnCounterAndDoNotConsumeAFullIndex()
    {
        // The whole point: Light.esl sits between two full masters and must not shift Third.esp.
        var p = Prefixes(
            Listing("Skyrim.esm"),
            Listing("Light.esl", esl: true),
            Listing("Third.esp"));

        p[ModKey.FromFileName("Skyrim.esm")].Should().Be("00");
        p[ModKey.FromFileName("Light.esl")].Should().Be("FE000");
        p[ModKey.FromFileName("Third.esp")].Should().Be("01");
    }

    [Fact]
    public void SeveralLightMasters_CountUpIndependently()
    {
        var p = Prefixes(
            Listing("Skyrim.esm"),
            Listing("A.esl", esl: true),
            Listing("B.esp"),
            Listing("C.esl", esl: true));

        p[ModKey.FromFileName("A.esl")].Should().Be("FE000");
        p[ModKey.FromFileName("C.esl")].Should().Be("FE001");
        p[ModKey.FromFileName("B.esp")].Should().Be("01");
    }

    [Fact]
    public void FullMasterFormId_IsPrefixPlusTheLowSixHexDigits()
    {
        var p = Prefixes(Listing("Skyrim.esm"), Listing("RSChildren.esp"));

        Auxilliary.FormatFormId(FormKey.Factory("01C18A:Skyrim.esm"), p).Should().Be("0001C18A");
        Auxilliary.FormatFormId(FormKey.Factory("000801:RSChildren.esp"), p).Should().Be("01000801");
    }

    [Fact]
    public void LightMasterFormId_KeepsOnlyTheLowTwelveBits()
    {
        // An ESL's local ID is 3 hex digits; the rest of the FormKey's ID is not addressable.
        var p = Prefixes(Listing("Skyrim.esm"), Listing("Light.esl", esl: true));

        Auxilliary.FormatFormId(FormKey.Factory("000ABC:Light.esl"), p).Should().Be("FE000ABC");
        Auxilliary.FormatFormId(FormKey.Factory("123ABC:Light.esl"), p).Should().Be("FE000ABC");
    }

    [Fact]
    public void PluginOutsideTheLoadOrder_HasNoFormId()
    {
        // Better blank than a wrong number: the report says nothing rather than pointing the user
        // at an address that resolves to some other record.
        var p = Prefixes(Listing("Skyrim.esm"));

        Auxilliary.FormatFormId(FormKey.Factory("000801:NotInstalled.esp"), p).Should().BeEmpty();
    }

    // ── The report column ───────────────────────────────────────────────────────

    [Fact]
    public void StampFormIds_FillsEveryRowFromTheReportedLoadOrder()
    {
        var issues = new List<ValidationIssue>
        {
            new() { NpcFormKey = "01C18A:Skyrim.esm" },
            new() { NpcFormKey = "000801:RSChildren.esp" },
        };

        OutputValidator.StampFormIds(issues, new[] { Listing("Skyrim.esm"), Listing("RSChildren.esp") });

        issues[0].NpcFormId.Should().Be("0001C18A");
        issues[1].NpcFormId.Should().Be("01000801");
    }

    [Fact]
    public void StampFormIds_LeavesRowsWithNoNpcAlone()
    {
        // Environment-level notes carry no FormKey and must not throw or invent one.
        var issues = new List<ValidationIssue> { new() { NpcFormKey = "" } };

        OutputValidator.StampFormIds(issues, new[] { Listing("Skyrim.esm") });

        issues[0].NpcFormId.Should().BeEmpty();
    }

    [Fact]
    public void StampFormIds_LeavesUnparseableFormKeysBlank()
    {
        var issues = new List<ValidationIssue> { new() { NpcFormKey = "not a form key" } };

        OutputValidator.StampFormIds(issues, new[] { Listing("Skyrim.esm") });

        issues[0].NpcFormId.Should().BeEmpty();
    }

    [Fact]
    public void StampFormIds_GivesRepeatedNpcsTheSameId()
    {
        // Several checks fire for one NPC; the per-run cache must not make them disagree.
        var issues = Enumerable.Range(0, 3)
            .Select(_ => new ValidationIssue { NpcFormKey = "01C18A:Skyrim.esm" })
            .ToList();

        OutputValidator.StampFormIds(issues, new[] { Listing("Skyrim.esm") });

        issues.Select(i => i.NpcFormId).Should().AllBe("0001C18A");
    }
}
