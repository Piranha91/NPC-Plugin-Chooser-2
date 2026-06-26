using System.Text;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// <see cref="MasterAnalyzer.FormatAnalysisReport"/> — pure formatter that turns a
/// <see cref="MasterAnalysisResult"/> into a human-readable report string.
///
/// The method reads ONLY its <see cref="MasterAnalysisResult"/> argument: it never
/// touches the injected <c>_environmentStateProvider</c>, <c>_npcConsistencyProvider</c>,
/// or <c>_settings</c> fields. So the analyzer is allocated via
/// <see cref="Reflect.Uninitialized{T}"/> (skipping its real constructor) and those
/// fields are left null — exercising the formatter in complete isolation, with no live
/// Skyrim install, no link cache, and no clock except the trailing timestamp line.
///
/// The trailing "Analysis completed at {DateTime.Now}" line is non-deterministic, so
/// assertions target everything ABOVE it (header, rules, grouping, per-reference lines)
/// and only structurally check the footer (its presence + a 60-char '=' rule).
///
/// The other public seams (GetMastersFromPlugin, AnalyzeMasterReferences) and the private
/// recursive helpers (FindMasterReferences, GetLogString, GetLogStringForLink,
/// GetAppearanceModInfo) require a real plugin file on disk and a live
/// EnvironmentStateProvider / LinkCache / NpcConsistencyProvider, so they belong to the
/// integration wave and are intentionally NOT exercised here.
/// </summary>
public class MasterAnalyzerFormatTests
{
    private static readonly string NL = Environment.NewLine;

    /// <summary>Allocate the analyzer without running its heavy constructor — the formatter reads no fields.</summary>
    private static MasterAnalyzer NewAnalyzer() => Reflect.Uninitialized<MasterAnalyzer>();

    private static string Format(MasterAnalysisResult result) =>
        NewAnalyzer().FormatAnalysisReport(result);

    private static MasterReference Ref(string source, string path, string? appearance = null) =>
        new() { SourceRecord = source, ReferencePath = path, AppearanceModInfo = appearance };

    // ---------------------------------------------------------------------
    // ErrorMessage short-circuit
    // ---------------------------------------------------------------------

    [Fact]
    public void ErrorMessageSet_EmitsErrorLineOnly()
    {
        var result = new MasterAnalysisResult { ErrorMessage = "No masters selected for analysis." };

        var report = Format(result);

        report.Should().Be($"ERROR: No masters selected for analysis.{NL}");
    }

    [Fact]
    public void ErrorMessageSet_DoesNotEmitHeaderOrMasters()
    {
        var result = new MasterAnalysisResult
        {
            ErrorMessage = "Failed to load target plugin: boom",
            // These should all be ignored once an error is present.
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { ModKey.FromFileName("Skyrim.esm") },
        };
        result.ReferencesByMaster[ModKey.FromFileName("Skyrim.esm")] = new List<MasterReference>
        {
            Ref("SomeRecord", "Path -> Skyrim.esm:001234"),
        };

        var report = Format(result);

        report.Should().Be($"ERROR: Failed to load target plugin: boom{NL}");
        report.Should().NotContain("Master Analysis Report");
        report.Should().NotContain("Master:");
        report.Should().NotContain("=");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ErrorMessageNullOrEmpty_FallsThroughToFullReport(string? error)
    {
        // string.IsNullOrEmpty short-circuit must NOT trigger for null/empty; the full
        // report (header + footer) is produced instead.
        var result = new MasterAnalysisResult
        {
            ErrorMessage = error,
            TargetPlugin = ModKey.FromFileName("Target.esp"),
        };

        var report = Format(result);

        report.Should().NotStartWith("ERROR:");
        report.Should().Contain("Master Analysis Report for: Target.esp");
        report.Should().Contain("Analysis completed at ");
    }

    // ---------------------------------------------------------------------
    // Header + footer scaffolding
    // ---------------------------------------------------------------------

    [Fact]
    public void NoMasters_EmitsHeaderRuleAndFooterOnly()
    {
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("MyMod.esp"),
            // AnalyzedMasters is empty -> the per-master loop produces nothing.
        };

        var report = Format(result);

        var equalsRule = new string('=', 60);
        report.Should().StartWith(
            $"Master Analysis Report for: MyMod.esp{NL}{equalsRule}{NL}{NL}");
        report.Should().Contain($"{equalsRule}{NL}Analysis completed at ");
        report.Should().NotContain("Master:");
    }

    [Fact]
    public void HeaderUsesTargetPluginFileName()
    {
        var target = ModKey.FromFileName("SomeAppearanceMod.esp");
        var result = new MasterAnalysisResult { TargetPlugin = target };

        var report = Format(result);

        // Self-consistent with the production interpolation: {result.TargetPlugin.FileName}.
        report.Should().StartWith($"Master Analysis Report for: {target.FileName}{NL}");
    }

    [Fact]
    public void HeaderAndClosingRulesAreSixtyEqualsChars()
    {
        var result = new MasterAnalysisResult { TargetPlugin = ModKey.FromFileName("X.esp") };

        var report = Format(result);

        var lines = report.Split(NL);
        // Two distinct '=' rules: one under the header, one above the footer.
        var ruleLines = lines.Where(l => l.Length > 0 && l.All(c => c == '=')).ToList();
        ruleLines.Should().HaveCount(2);
        ruleLines.Should().OnlyContain(l => l.Length == 60);
    }

    [Fact]
    public void Footer_AlwaysEndsWithCompletionTimestampLine()
    {
        var result = new MasterAnalysisResult { TargetPlugin = ModKey.FromFileName("X.esp") };

        var report = Format(result);

        // AppendLine adds a trailing newline after the timestamp line.
        var lines = report.Split(NL);
        // last element is "" because of the trailing newline; the real last line precedes it.
        lines[^1].Should().BeEmpty();
        lines[^2].Should().StartWith("Analysis completed at ");
        lines[^2].Should().MatchRegex(@"^Analysis completed at \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$");
    }

    // ---------------------------------------------------------------------
    // Master with no references
    // ---------------------------------------------------------------------

    [Fact]
    public void MasterWithNoReferences_EmitsNoReferencesHintLines()
    {
        var master = ModKey.FromFileName("Dawnguard.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { master },
        };
        // ReferencesByMaster has no entry -> GetValueOrDefault yields an empty list.

        var report = Format(result);

        var dashRule = new string('-', 40);
        report.Should().Contain(
            $"Master: {master.FileName}{NL}" +
            $"{dashRule}{NL}" +
            $"  No references found to this master.{NL}" +
            $"  This master may be unnecessary, or references may be in record types not analyzed.{NL}");
        report.Should().NotContain("Found ");
        report.Should().NotContain("    -> ");
    }

    [Fact]
    public void MasterWithEmptyReferenceList_TreatedAsNoReferences()
    {
        var master = ModKey.FromFileName("Update.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { master },
        };
        // Explicitly present but empty list takes the same Count == 0 branch.
        result.ReferencesByMaster[master] = new List<MasterReference>();

        var report = Format(result);

        report.Should().Contain("  No references found to this master.");
        report.Should().NotContain("Found 0 reference(s):");
    }

    [Fact]
    public void MasterSubHeaderRuleIsFortyDashChars()
    {
        var master = ModKey.FromFileName("Skyrim.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { master },
        };

        var report = Format(result);

        var lines = report.Split(NL);
        var dashRules = lines.Where(l => l.Length > 0 && l.All(c => c == '-')).ToList();
        dashRules.Should().ContainSingle();
        dashRules[0].Should().HaveLength(40);
    }

    // ---------------------------------------------------------------------
    // Master with references — counting, grouping, prefixes
    // ---------------------------------------------------------------------

    [Fact]
    public void SingleReference_EmitsFoundCountHeaderAndArrowLine()
    {
        var master = ModKey.FromFileName("Skyrim.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { master },
        };
        result.ReferencesByMaster[master] = new List<MasterReference>
        {
            Ref("Lydia | HousecarlNPC | 0A0001:Target.esp", "Race -> NordRace [001234:Skyrim.esm]"),
        };

        var report = Format(result);

        report.Should().Contain($"  Found 1 reference(s):{NL}");
        report.Should().Contain("  [Lydia | HousecarlNPC | 0A0001:Target.esp]");
        report.Should().Contain("    -> Race -> NordRace [001234:Skyrim.esm]");
    }

    [Fact]
    public void ReferenceCount_ReflectsTotalReferencesAcrossGroups()
    {
        var master = ModKey.FromFileName("Skyrim.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { master },
        };
        result.ReferencesByMaster[master] = new List<MasterReference>
        {
            Ref("RecordA", "p1"),
            Ref("RecordA", "p2"),
            Ref("RecordB", "p3"),
        };

        var report = Format(result);

        // Count is the raw reference count (3), independent of the 2 groups.
        report.Should().Contain("  Found 3 reference(s):");
    }

    [Fact]
    public void MultipleReferencesSameSource_GroupedUnderOneHeader()
    {
        var master = ModKey.FromFileName("Skyrim.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { master },
        };
        result.ReferencesByMaster[master] = new List<MasterReference>
        {
            Ref("SharedNpc", "WornArmor -> ArmorA"),
            Ref("SharedNpc", "Race -> RaceA"),
        };

        var report = Format(result);

        // The source-record header appears exactly once for the grouped references.
        CountOccurrences(report, "  [SharedNpc]").Should().Be(1);
        report.Should().Contain("    -> WornArmor -> ArmorA");
        report.Should().Contain("    -> Race -> RaceA");
    }

    [Fact]
    public void DifferentSourceRecords_ProduceSeparateGroupHeaders()
    {
        var master = ModKey.FromFileName("Skyrim.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { master },
        };
        result.ReferencesByMaster[master] = new List<MasterReference>
        {
            Ref("RecordOne", "path1"),
            Ref("RecordTwo", "path2"),
        };

        var report = Format(result);

        report.Should().Contain("  [RecordOne]");
        report.Should().Contain("  [RecordTwo]");
        CountOccurrences(report, "  [RecordOne]").Should().Be(1);
        CountOccurrences(report, "  [RecordTwo]").Should().Be(1);
    }

    [Fact]
    public void SameSourceRecord_DifferentAppearanceMod_AreDistinctGroups()
    {
        // Grouping key is { SourceRecord, AppearanceModInfo } — differing appearance info splits groups.
        var master = ModKey.FromFileName("Skyrim.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { master },
        };
        result.ReferencesByMaster[master] = new List<MasterReference>
        {
            Ref("SameRecord", "p1", appearance: "ModA"),
            Ref("SameRecord", "p2", appearance: "ModB"),
        };

        var report = Format(result);

        report.Should().Contain("  [SameRecord | Appearance Mod: ModA]");
        report.Should().Contain("  [SameRecord | Appearance Mod: ModB]");
    }

    [Fact]
    public void AppearanceModInfo_AppendedToGroupHeaderWhenPresent()
    {
        var master = ModKey.FromFileName("Skyrim.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { master },
        };
        result.ReferencesByMaster[master] = new List<MasterReference>
        {
            Ref("Lydia", "Race -> NordRace", appearance: "High Poly NPC (Shared from Mjoll | 0B0002:Mod.esp)"),
        };

        var report = Format(result);

        report.Should().Contain(
            "  [Lydia | Appearance Mod: High Poly NPC (Shared from Mjoll | 0B0002:Mod.esp)]");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AppearanceModInfo_NullOrEmpty_OmittedFromHeader(string? appearance)
    {
        var master = ModKey.FromFileName("Skyrim.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { master },
        };
        result.ReferencesByMaster[master] = new List<MasterReference>
        {
            Ref("PlainRecord", "some -> path", appearance: appearance),
        };

        var report = Format(result);

        report.Should().Contain("  [PlainRecord]");
        report.Should().NotContain("Appearance Mod:");
    }

    [Fact]
    public void EveryReferenceLineUsesFourSpaceArrowPrefix()
    {
        var master = ModKey.FromFileName("Skyrim.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { master },
        };
        result.ReferencesByMaster[master] = new List<MasterReference>
        {
            Ref("A", "p1"),
            Ref("A", "p2"),
            Ref("B", "p3"),
        };

        var report = Format(result);

        var arrowLines = report.Split(NL).Where(l => l.Contains("->") && l.TrimStart().StartsWith("->")).ToList();
        arrowLines.Should().HaveCount(3);
        arrowLines.Should().OnlyContain(l => l.StartsWith("    -> "));
    }

    // ---------------------------------------------------------------------
    // Multiple masters
    // ---------------------------------------------------------------------

    [Fact]
    public void MultipleMasters_EachGetsItsOwnSection()
    {
        var skyrim = ModKey.FromFileName("Skyrim.esm");
        var dawnguard = ModKey.FromFileName("Dawnguard.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { skyrim, dawnguard },
        };
        result.ReferencesByMaster[skyrim] = new List<MasterReference> { Ref("NpcA", "Race -> NordRace") };
        // Dawnguard has no references entry -> "No references found" branch.

        var report = Format(result);

        report.Should().Contain("Master: Skyrim.esm");
        report.Should().Contain("Master: Dawnguard.esm");
        report.Should().Contain("  Found 1 reference(s):");
        report.Should().Contain("  No references found to this master.");
    }

    [Fact]
    public void MultipleMasters_RenderedInAnalyzedMastersOrder()
    {
        var first = ModKey.FromFileName("AAA.esm");
        var second = ModKey.FromFileName("ZZZ.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            // Deliberately NOT alphabetical to prove the loop preserves list order.
            AnalyzedMasters = { second, first },
        };

        var report = Format(result);

        var idxSecond = report.IndexOf("Master: ZZZ.esm", StringComparison.Ordinal);
        var idxFirst = report.IndexOf("Master: AAA.esm", StringComparison.Ordinal);
        idxSecond.Should().BeGreaterThan(0);
        idxFirst.Should().BeGreaterThan(0);
        idxSecond.Should().BeLessThan(idxFirst, "AnalyzedMasters order (ZZZ then AAA) is preserved");
    }

    [Fact]
    public void EachMasterSection_HasItsOwnDashRule()
    {
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters =
            {
                ModKey.FromFileName("A.esm"),
                ModKey.FromFileName("B.esm"),
                ModKey.FromFileName("C.esm"),
            },
        };

        var report = Format(result);

        var dashRules = report.Split(NL).Where(l => l.Length > 0 && l.All(c => c == '-')).ToList();
        dashRules.Should().HaveCount(3, "one 40-char dash rule per master section");
        dashRules.Should().OnlyContain(l => l.Length == 40);
    }

    // ---------------------------------------------------------------------
    // Full exact-string golden (everything except the timestamp line)
    // ---------------------------------------------------------------------

    [Fact]
    public void FullReport_ExactStructureAboveTimestamp()
    {
        var master = ModKey.FromFileName("Skyrim.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("MyAppearanceMod.esp"),
            AnalyzedMasters = { master },
        };
        result.ReferencesByMaster[master] = new List<MasterReference>
        {
            Ref("Lydia", "Race -> NordRace [0013BB9:Skyrim.esm]", appearance: "High Poly NPC"),
            Ref("Lydia", "WornArmor -> SkinNaked [00000019:Skyrim.esm]", appearance: "High Poly NPC"),
        };

        var report = Format(result);

        var eq = new string('=', 60);
        var dash = new string('-', 40);
        var expectedPrefix =
            $"Master Analysis Report for: MyAppearanceMod.esp{NL}" +
            $"{eq}{NL}" +
            $"{NL}" +
            $"Master: Skyrim.esm{NL}" +
            $"{dash}{NL}" +
            $"  Found 2 reference(s):{NL}" +
            $"{NL}" +
            $"  [Lydia | Appearance Mod: High Poly NPC]{NL}" +
            $"    -> Race -> NordRace [0013BB9:Skyrim.esm]{NL}" +
            $"    -> WornArmor -> SkinNaked [00000019:Skyrim.esm]{NL}" +
            $"{NL}" +
            $"{NL}" +
            $"{eq}{NL}" +
            $"Analysis completed at ";

        report.Should().StartWith(expectedPrefix);
    }

    [Fact]
    public void FullReport_NoReferences_ExactStructureAboveTimestamp()
    {
        var master = ModKey.FromFileName("Dragonborn.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Mod.esp"),
            AnalyzedMasters = { master },
        };

        var report = Format(result);

        var eq = new string('=', 60);
        var dash = new string('-', 40);
        var expectedPrefix =
            $"Master Analysis Report for: Mod.esp{NL}" +
            $"{eq}{NL}" +
            $"{NL}" +
            $"Master: Dragonborn.esm{NL}" +
            $"{dash}{NL}" +
            $"  No references found to this master.{NL}" +
            $"  This master may be unnecessary, or references may be in record types not analyzed.{NL}" +
            $"{NL}" +
            $"{eq}{NL}" +
            $"Analysis completed at ";

        report.Should().StartWith(expectedPrefix);
    }

    // ---------------------------------------------------------------------
    // Edge cases on field contents
    // ---------------------------------------------------------------------

    [Fact]
    public void DefaultTargetPlugin_RendersNullModKeyFileName()
    {
        // TargetPlugin left at default(ModKey) — should not throw; renders the null key's FileName.
        var result = new MasterAnalysisResult();

        var report = Format(result);

        report.Should().StartWith($"Master Analysis Report for: {default(ModKey).FileName}{NL}");
    }

    [Fact]
    public void EmptySourceRecordAndPath_StillRenderBracketsAndArrow()
    {
        var master = ModKey.FromFileName("Skyrim.esm");
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModKey.FromFileName("Target.esp"),
            AnalyzedMasters = { master },
        };
        result.ReferencesByMaster[master] = new List<MasterReference>
        {
            // Defaults: SourceRecord = "", ReferencePath = "".
            new MasterReference(),
        };

        var report = Format(result);

        // Empty SourceRecord with no appearance info -> group header is exactly "  []".
        report.Should().Contain($"  []{NL}");
        // Empty ReferencePath -> arrow line is exactly "    -> " (trailing space, then newline).
        report.Should().Contain($"    -> {NL}");
    }

    [Fact]
    public void ReportAlwaysEndsWithNewline()
    {
        var result = new MasterAnalysisResult { TargetPlugin = ModKey.FromFileName("X.esp") };
        Format(result).Should().EndWith(NL);
    }

    [Fact]
    public void ErrorReportAlsoEndsWithNewline()
    {
        var result = new MasterAnalysisResult { ErrorMessage = "x" };
        Format(result).Should().EndWith(NL).And.Subject.Should().Be($"ERROR: x{NL}");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
