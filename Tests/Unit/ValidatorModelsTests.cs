using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pure model/result types from the validation + master-analysis seams, exercised without any
/// game environment:
///   <see cref="Validator.ValidationReport"/> — positional record wrapping an invalid-selection list.
///   <see cref="MasterAnalysisResult"/> / <see cref="MasterReference"/> — plain result containers.
/// Plus the one env-free formatter, <see cref="MasterAnalyzer.FormatAnalysisReport"/>, which reads
/// only its argument (no instance fields), so it can run on an uninitialized analyzer.
///
/// NOTE: Validator.ScreenSelectionsAsync / Validator.GetScreeningCache not covered: require a live
///       EnvironmentStateProvider (LinkCache/LoadOrder) — those belong to the integration wave.
/// NOTE: MasterAnalyzer.GetMastersFromPlugin / AnalyzeMasterReferences not covered: read real plugin
///       binaries off disk via SkyrimMod.CreateFromBinaryOverlay and need the load order — integration.
/// </summary>
public class ValidatorModelsTests
{
    private static readonly ModKey ModA = ModKey.FromFileName("ModA.esp");
    private static readonly ModKey ModB = ModKey.FromFileName("ModB.esp");

    // ----------------------------------------------------------------------
    // Validator.ValidationReport (positional record: List<string> InvalidSelections)
    // ----------------------------------------------------------------------

    [Fact]
    public void ValidationReport_StoresList()
    {
        var list = new List<string> { "a", "b" };
        var report = new Validator.ValidationReport(list);

        report.InvalidSelections.Should().BeSameAs(list);
        report.InvalidSelections.Should().Equal("a", "b");
    }

    [Fact]
    public void ValidationReport_EmptyList_HasNoEntries()
    {
        var report = new Validator.ValidationReport(new List<string>());
        report.InvalidSelections.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void ValidationReport_Deconstruct_ExposesList()
    {
        var list = new List<string> { "x" };
        var report = new Validator.ValidationReport(list);
        report.Deconstruct(out var invalid);
        invalid.Should().BeSameAs(list);
    }

    [Fact]
    public void ValidationReport_Equality_IsReferenceBasedForList()
    {
        // Positional records compare members with EqualityComparer<T>.Default; for List<string>
        // that is reference equality — distinct-but-equal lists are NOT equal.
        var shared = new List<string> { "same" };
        var a = new Validator.ValidationReport(shared);
        var b = new Validator.ValidationReport(shared);
        var c = new Validator.ValidationReport(new List<string> { "same" });

        a.Should().Be(b, "they wrap the same list instance");
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Should().NotBe(c, "List<string> equality is by reference, not contents");
    }

    [Fact]
    public void ValidationReport_With_ReplacesList()
    {
        var original = new Validator.ValidationReport(new List<string> { "old" });
        var replacement = new List<string> { "new1", "new2" };

        var copy = original with { InvalidSelections = replacement };

        copy.InvalidSelections.Should().BeSameAs(replacement);
        original.InvalidSelections.Should().Equal(new[] { "old" }, "original is unchanged");
    }

    [Fact]
    public void ValidationReport_ToString_NamesTheRecord()
    {
        var report = new Validator.ValidationReport(new List<string>());
        report.ToString().Should().StartWith("ValidationReport");
    }

    [Fact]
    public void ValidationReport_MutatingList_IsVisibleThroughReport()
    {
        // The record holds the list by reference; this mirrors how the Validator accumulates
        // invalidSelections then hands the same list to the report.
        var list = new List<string>();
        var report = new Validator.ValidationReport(list);
        list.Add("first");
        list.Add("second");

        report.InvalidSelections.Should().Equal("first", "second");
    }

    // ----------------------------------------------------------------------
    // MasterAnalysisResult — plain class, non-null empty collections, null error
    // ----------------------------------------------------------------------

    [Fact]
    public void MasterAnalysisResult_Defaults()
    {
        var r = new MasterAnalysisResult();

        r.TargetPlugin.Should().Be(default(ModKey));
        r.AnalyzedMasters.Should().NotBeNull().And.BeEmpty();
        r.ReferencesByMaster.Should().NotBeNull().And.BeEmpty();
        r.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void MasterAnalysisResult_DefaultCollections_AreDistinctInstancesPerObject()
    {
        var a = new MasterAnalysisResult();
        var b = new MasterAnalysisResult();
        a.AnalyzedMasters.Should().NotBeSameAs(b.AnalyzedMasters);
        a.ReferencesByMaster.Should().NotBeSameAs(b.ReferencesByMaster);
    }

    [Fact]
    public void MasterAnalysisResult_RoundTripsAllProperties()
    {
        var masters = new List<ModKey> { ModA, ModB };
        var refsA = new List<MasterReference>
        {
            new() { SourceRecord = "NPC_:001234", ReferencePath = "Race -> 0:ModA.esp" }
        };

        var r = new MasterAnalysisResult
        {
            TargetPlugin = ModB,
            AnalyzedMasters = masters,
            ReferencesByMaster = new Dictionary<ModKey, List<MasterReference>> { [ModA] = refsA },
            ErrorMessage = "boom",
        };

        r.TargetPlugin.Should().Be(ModB);
        r.AnalyzedMasters.Should().BeSameAs(masters);
        r.AnalyzedMasters.Should().Equal(ModA, ModB);
        r.ReferencesByMaster.Should().ContainKey(ModA);
        r.ReferencesByMaster[ModA].Should().BeSameAs(refsA);
        r.ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public void MasterAnalysisResult_ReferenceEquality_NotValueEquality()
    {
        // Plain class (no record) — two equally-populated instances are not equal.
        var a = new MasterAnalysisResult { ErrorMessage = "x" };
        var b = new MasterAnalysisResult { ErrorMessage = "x" };
        a.Should().NotBe(b);
        a.Should().Be(a);
    }

    [Fact]
    public void MasterAnalysisResult_ReferencesByMaster_KeyedByModKey()
    {
        var r = new MasterAnalysisResult();
        r.ReferencesByMaster[ModA] = new List<MasterReference>();
        r.ReferencesByMaster[ModB] = new List<MasterReference>();

        r.ReferencesByMaster.Should().HaveCount(2);
        r.ReferencesByMaster.Keys.Should().BeEquivalentTo(new[] { ModA, ModB });
        // GetValueOrDefault on a missing key (as FormatAnalysisReport relies on) yields the fallback.
        r.ReferencesByMaster.GetValueOrDefault(ModKey.FromFileName("Missing.esp"), new List<MasterReference>())
            .Should().BeEmpty();
    }

    // ----------------------------------------------------------------------
    // MasterReference — plain class, empty-string defaults, nullable info
    // ----------------------------------------------------------------------

    [Fact]
    public void MasterReference_Defaults()
    {
        var m = new MasterReference();
        m.SourceRecord.Should().BeEmpty();
        m.ReferencePath.Should().BeEmpty();
        m.AppearanceModInfo.Should().BeNull();
    }

    [Fact]
    public void MasterReference_RoundTripsProperties()
    {
        var m = new MasterReference
        {
            SourceRecord = "Bandit | 00ABCD:ModA.esp",
            ReferencePath = "WornArmor -> 0:Skyrim.esm",
            AppearanceModInfo = "Cool Faces (Shared from Lydia)",
        };

        m.SourceRecord.Should().Be("Bandit | 00ABCD:ModA.esp");
        m.ReferencePath.Should().Be("WornArmor -> 0:Skyrim.esm");
        m.AppearanceModInfo.Should().Be("Cool Faces (Shared from Lydia)");
    }

    [Fact]
    public void MasterReference_ReferenceEquality_NotValueEquality()
    {
        var a = new MasterReference { SourceRecord = "r" };
        var b = new MasterReference { SourceRecord = "r" };
        a.Should().NotBe(b);
        a.Should().Be(a);
    }

    // ----------------------------------------------------------------------
    // MasterAnalyzer.FormatAnalysisReport — env-free formatter (reads only its argument).
    // Built on an uninitialized analyzer because the method touches no instance fields.
    // ----------------------------------------------------------------------

    private static string Format(MasterAnalysisResult result) =>
        Reflect.Uninitialized<MasterAnalyzer>().FormatAnalysisReport(result);

    [Fact]
    public void FormatAnalysisReport_ErrorMessage_ShortCircuits()
    {
        var report = Format(new MasterAnalysisResult { ErrorMessage = "could not load" });

        report.Should().Be("ERROR: could not load" + Environment.NewLine);
    }

    [Fact]
    public void FormatAnalysisReport_NoReferences_EmitsUnnecessaryHint()
    {
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModB,
            AnalyzedMasters = new List<ModKey> { ModA },
            ReferencesByMaster = new Dictionary<ModKey, List<MasterReference>>
            {
                [ModA] = new List<MasterReference>(),
            },
        };

        var report = Format(result);

        report.Should().Contain("Master Analysis Report for: ModB.esp");
        report.Should().Contain("Master: ModA.esp");
        report.Should().Contain("No references found to this master.");
        report.Should().Contain("This master may be unnecessary");
    }

    [Fact]
    public void FormatAnalysisReport_MissingMasterEntry_FallsBackToNoReferences()
    {
        // AnalyzedMasters lists ModA but ReferencesByMaster has no entry for it: the formatter
        // uses GetValueOrDefault and must treat it as "no references" rather than throwing.
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModB,
            AnalyzedMasters = new List<ModKey> { ModA },
            ReferencesByMaster = new Dictionary<ModKey, List<MasterReference>>(),
        };

        var report = Format(result);

        report.Should().Contain("Master: ModA.esp");
        report.Should().Contain("No references found to this master.");
    }

    [Fact]
    public void FormatAnalysisReport_WithReferences_GroupsBySourceRecord()
    {
        var refs = new List<MasterReference>
        {
            new() { SourceRecord = "Bandit", ReferencePath = "Race -> 0:ModA.esp" },
            new() { SourceRecord = "Bandit", ReferencePath = "Voice -> 1:ModA.esp" },
            new() { SourceRecord = "Guard", ReferencePath = "Class -> 2:ModA.esp" },
        };
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModB,
            AnalyzedMasters = new List<ModKey> { ModA },
            ReferencesByMaster = new Dictionary<ModKey, List<MasterReference>> { [ModA] = refs },
        };

        var report = Format(result);

        report.Should().Contain("Found 3 reference(s):");
        report.Should().Contain("[Bandit]");
        report.Should().Contain("[Guard]");
        report.Should().Contain("-> Race -> 0:ModA.esp");
        report.Should().Contain("-> Voice -> 1:ModA.esp");
        report.Should().Contain("-> Class -> 2:ModA.esp");
    }

    [Fact]
    public void FormatAnalysisReport_AppearanceModInfo_AppearsInGroupHeader()
    {
        var refs = new List<MasterReference>
        {
            new()
            {
                SourceRecord = "Lydia",
                ReferencePath = "HeadParts -> 0:ModA.esp",
                AppearanceModInfo = "Pretty Faces",
            },
        };
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModB,
            AnalyzedMasters = new List<ModKey> { ModA },
            ReferencesByMaster = new Dictionary<ModKey, List<MasterReference>> { [ModA] = refs },
        };

        var report = Format(result);

        report.Should().Contain("[Lydia | Appearance Mod: Pretty Faces]");
    }

    [Fact]
    public void FormatAnalysisReport_DistinctAppearanceInfo_SplitsGroups()
    {
        // Same SourceRecord but different AppearanceModInfo are grouped separately
        // (GroupBy key = { SourceRecord, AppearanceModInfo }).
        var refs = new List<MasterReference>
        {
            new() { SourceRecord = "Npc", ReferencePath = "A", AppearanceModInfo = "ModOne" },
            new() { SourceRecord = "Npc", ReferencePath = "B", AppearanceModInfo = "ModTwo" },
        };
        var result = new MasterAnalysisResult
        {
            TargetPlugin = ModB,
            AnalyzedMasters = new List<ModKey> { ModA },
            ReferencesByMaster = new Dictionary<ModKey, List<MasterReference>> { [ModA] = refs },
        };

        var report = Format(result);

        report.Should().Contain("[Npc | Appearance Mod: ModOne]");
        report.Should().Contain("[Npc | Appearance Mod: ModTwo]");
    }

    [Fact]
    public void FormatAnalysisReport_NoMasters_StillEmitsHeaderAndFooter()
    {
        var result = new MasterAnalysisResult { TargetPlugin = ModB };

        var report = Format(result);

        report.Should().Contain("Master Analysis Report for: ModB.esp");
        report.Should().Contain("Analysis completed at");
        report.Should().Contain(new string('=', 60));
    }
}
