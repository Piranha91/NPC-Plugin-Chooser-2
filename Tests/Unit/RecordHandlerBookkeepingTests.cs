using System.Collections.Generic;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pure-bookkeeping seams on <see cref="RecordHandler"/> that never touch the link cache,
/// environment, or plugin provider: the merged-record provenance map
/// (<see cref="RecordHandler.MergedRecordOrigin"/>, RecordMergedRecordOrigin /
/// TryGetMergedRecordOrigin / ResetMergedRecordTracking), the duplicate-in mapping
/// (ProtectRecordFromDuplication / ResetMapping), the private static NormalizePath, the
/// <see cref="RecordHandler.RecordLookupFallBack"/> enum ordering, and the empty-cache
/// branch of GetStatusReport.
///
/// The ctor stores its three args into fields and never dereferences them, and every
/// method exercised here only touches the in-memory dictionaries/hash-sets — so the env
/// and plugin provider are passed as null. No Skyrim install or link cache is required.
///
/// NOTE: all record-merge / override-discovery / link-cache seams (DuplicateInOrAddFormLink,
/// DuplicateFromOnlyReferencedGetters, DeepGetOverriddenDependencyRecords,
/// DuplicateInOverrideRecords, TryGetRecordFromMod(s), TryGetRecordGetterFromMod,
/// PrimeLinkCachesFor, TryAddPluginToCaches, EvictIfSourcePathChanged, PrimeIfEsl,
/// TryWarmPlugin, and the populated branch of GetStatusReport) are NOT covered: they
/// require a real EnvironmentStateProvider / PluginProvider / link cache or on-disk
/// plugins, which belong to the separate integration wave.
/// </summary>
public class RecordHandlerBookkeepingTests
{
    private static readonly FormKey Out1 = FormKey.Factory("000801:Output.esp");
    private static readonly FormKey Out2 = FormKey.Factory("000802:Output.esp");
    private static readonly FormKey Src1 = FormKey.Factory("0A0001:Source.esp");
    private static readonly FormKey Src2 = FormKey.Factory("0A0002:Source.esp");

    /// <summary>
    /// The bookkeeping paths never dereference the env or plugin provider, so they can be
    /// constructed with nulls; only a real <see cref="Settings"/> is supplied.
    /// </summary>
    private static RecordHandler MakeHandler() =>
        new RecordHandler(null!, null!, new Settings());

    // ---------------------------------------------------------------------
    // MergedRecordOrigin struct
    // ---------------------------------------------------------------------

    [Fact]
    public void MergedRecordOrigin_DefaultValue_HasNullFormKeyAndNullEditorId()
    {
        var origin = default(RecordHandler.MergedRecordOrigin);
        origin.SourceFormKey.Should().Be(FormKey.Null);
        origin.SourceEditorId.Should().BeNull();
    }

    [Fact]
    public void MergedRecordOrigin_InitializerStoresFields()
    {
        var origin = new RecordHandler.MergedRecordOrigin
        {
            SourceFormKey = Src1,
            SourceEditorId = "MyEditorID",
        };
        origin.SourceFormKey.Should().Be(Src1);
        origin.SourceEditorId.Should().Be("MyEditorID");
    }

    // ---------------------------------------------------------------------
    // RecordMergedRecordOrigin + TryGetMergedRecordOrigin
    // ---------------------------------------------------------------------

    [Fact]
    public void RecordMergedRecordOrigin_ThenLookup_ReturnsStoredProvenance()
    {
        var handler = MakeHandler();

        handler.RecordMergedRecordOrigin(Src1, Out1, "Donor");

        handler.TryGetMergedRecordOrigin(Out1, out var origin).Should().BeTrue();
        origin.SourceFormKey.Should().Be(Src1);
        origin.SourceEditorId.Should().Be("Donor");
    }

    [Fact]
    public void RecordMergedRecordOrigin_AllowsNullEditorId()
    {
        var handler = MakeHandler();

        handler.RecordMergedRecordOrigin(Src1, Out1, null);

        handler.TryGetMergedRecordOrigin(Out1, out var origin).Should().BeTrue();
        origin.SourceFormKey.Should().Be(Src1);
        origin.SourceEditorId.Should().BeNull();
    }

    [Fact]
    public void TryGetMergedRecordOrigin_Unknown_ReturnsFalseWithDefaultOrigin()
    {
        var handler = MakeHandler();

        handler.TryGetMergedRecordOrigin(Out1, out var origin).Should().BeFalse();
        origin.SourceFormKey.Should().Be(FormKey.Null);
        origin.SourceEditorId.Should().BeNull();
    }

    [Fact]
    public void RecordMergedRecordOrigin_NullOutputFormKey_IsNoOp()
    {
        var handler = MakeHandler();

        handler.RecordMergedRecordOrigin(Src1, FormKey.Null, "Donor");

        // Nothing was recorded; the null output key resolves nothing.
        handler.TryGetMergedRecordOrigin(FormKey.Null, out var origin).Should().BeFalse();
        origin.SourceEditorId.Should().BeNull();
    }

    [Fact]
    public void RecordMergedRecordOrigin_NullSourceFormKey_IsNoOp()
    {
        var handler = MakeHandler();

        handler.RecordMergedRecordOrigin(FormKey.Null, Out1, "Donor");

        handler.TryGetMergedRecordOrigin(Out1, out _).Should().BeFalse();
    }

    [Fact]
    public void RecordMergedRecordOrigin_FirstWins_LaterCallForSameOutputKeyIgnored()
    {
        var handler = MakeHandler();

        handler.RecordMergedRecordOrigin(Src1, Out1, "FirstSource");
        handler.RecordMergedRecordOrigin(Src2, Out1, "SecondSource");

        handler.TryGetMergedRecordOrigin(Out1, out var origin).Should().BeTrue();
        origin.SourceFormKey.Should().Be(Src1, "output FormKeys are unique so the root source is retained");
        origin.SourceEditorId.Should().Be("FirstSource");
    }

    [Fact]
    public void RecordMergedRecordOrigin_DistinctOutputKeys_TrackedIndependently()
    {
        var handler = MakeHandler();

        handler.RecordMergedRecordOrigin(Src1, Out1, "A");
        handler.RecordMergedRecordOrigin(Src2, Out2, "B");

        handler.TryGetMergedRecordOrigin(Out1, out var o1).Should().BeTrue();
        handler.TryGetMergedRecordOrigin(Out2, out var o2).Should().BeTrue();
        o1.SourceFormKey.Should().Be(Src1);
        o2.SourceFormKey.Should().Be(Src2);
    }

    // ---------------------------------------------------------------------
    // ResetMergedRecordTracking
    // ---------------------------------------------------------------------

    [Fact]
    public void ResetMergedRecordTracking_ClearsAllProvenance()
    {
        var handler = MakeHandler();
        handler.RecordMergedRecordOrigin(Src1, Out1, "A");
        handler.RecordMergedRecordOrigin(Src2, Out2, "B");

        handler.ResetMergedRecordTracking();

        handler.TryGetMergedRecordOrigin(Out1, out _).Should().BeFalse();
        handler.TryGetMergedRecordOrigin(Out2, out _).Should().BeFalse();
    }

    [Fact]
    public void ResetMergedRecordTracking_AllowsReRecordingAfterReset()
    {
        var handler = MakeHandler();
        handler.RecordMergedRecordOrigin(Src1, Out1, "First");
        handler.ResetMergedRecordTracking();

        // After a reset the same output key is no longer "first-seen", so a new source sticks.
        handler.RecordMergedRecordOrigin(Src2, Out1, "Second");

        handler.TryGetMergedRecordOrigin(Out1, out var origin).Should().BeTrue();
        origin.SourceFormKey.Should().Be(Src2);
        origin.SourceEditorId.Should().Be("Second");
    }

    [Fact]
    public void ResetMergedRecordTracking_OnEmptyMap_IsSafe()
    {
        var handler = MakeHandler();
        var act = () => handler.ResetMergedRecordTracking();
        act.Should().NotThrow();
        handler.TryGetMergedRecordOrigin(Out1, out _).Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // ProtectRecordFromDuplication + ResetMapping
    // ---------------------------------------------------------------------

    /// <summary>Reads the private identity-remap table so we can assert on its contents.</summary>
    private static Dictionary<FormKey, FormKey> Mappings(RecordHandler handler) =>
        Reflect.GetField<Dictionary<FormKey, FormKey>>(handler, "_currentDuplicateInMappings");

    [Fact]
    public void ProtectRecordFromDuplication_SeedsIdentityRemap()
    {
        var handler = MakeHandler();

        handler.ProtectRecordFromDuplication(Src1);

        var map = Mappings(handler);
        map.Should().ContainKey(Src1);
        map[Src1].Should().Be(Src1, "the record is remapped to itself so it is treated as already handled");
    }

    [Fact]
    public void ProtectRecordFromDuplication_NullFormKey_IsNoOp()
    {
        var handler = MakeHandler();

        handler.ProtectRecordFromDuplication(FormKey.Null);

        Mappings(handler).Should().BeEmpty();
    }

    [Fact]
    public void ProtectRecordFromDuplication_DoesNotOverwriteExistingMapping()
    {
        var handler = MakeHandler();
        var map = Mappings(handler);
        // Pre-seed a NON-identity mapping for Src1 (as the merge walker would do for a
        // record that was actually deep-copied into a new output FormKey).
        map[Src1] = Out1;

        handler.ProtectRecordFromDuplication(Src1);

        Mappings(handler)[Src1].Should().Be(Out1, "an existing remap target must not be clobbered with an identity remap");
    }

    [Fact]
    public void ProtectRecordFromDuplication_IsIdempotent()
    {
        var handler = MakeHandler();

        handler.ProtectRecordFromDuplication(Src1);
        handler.ProtectRecordFromDuplication(Src1);

        var map = Mappings(handler);
        map.Should().HaveCount(1);
        map[Src1].Should().Be(Src1);
    }

    [Fact]
    public void ResetMapping_ClearsDuplicateInMappingsAndTraversedLinks()
    {
        var handler = MakeHandler();
        handler.ProtectRecordFromDuplication(Src1);
        handler.ProtectRecordFromDuplication(Src2);
        Mappings(handler).Should().HaveCount(2);

        handler.ResetMapping();

        Mappings(handler).Should().BeEmpty();
        Reflect.GetField<HashSet<IFormLinkGetter>>(handler, "_currenTraversedFormLinks")
            .Should().BeEmpty();
    }

    [Fact]
    public void ResetMapping_DoesNotAffectMergedRecordOrigins()
    {
        var handler = MakeHandler();
        handler.RecordMergedRecordOrigin(Src1, Out1, "A");
        handler.ProtectRecordFromDuplication(Src2);

        handler.ResetMapping();

        // The per-batch ResetMapping must NOT touch the run-scoped provenance map.
        Mappings(handler).Should().BeEmpty();
        handler.TryGetMergedRecordOrigin(Out1, out var origin).Should().BeTrue();
        origin.SourceFormKey.Should().Be(Src1);
    }

    [Fact]
    public void ResetMapping_OnEmptyHandler_IsSafe()
    {
        var handler = MakeHandler();
        var act = () => handler.ResetMapping();
        act.Should().NotThrow();
    }

    // ---------------------------------------------------------------------
    // NormalizePath (private static)
    // ---------------------------------------------------------------------

    private static string Normalize(string path) =>
        Reflect.InvokeStatic<RecordHandler, string>("NormalizePath", path)!;

    [Fact]
    public void NormalizePath_UppercasesResult()
    {
        Normalize(@"C:\Data\Some.esp").Should().Be(Normalize(@"C:\Data\Some.esp").ToUpperInvariant());
        Normalize(@"C:\Data\Some.esp").Should().NotContain("some");
    }

    [Fact]
    public void NormalizePath_IsCaseInsensitiveAcrossCasings()
    {
        Normalize(@"C:\Data\Some.esp").Should().Be(Normalize(@"c:\data\SOME.ESP"));
    }

    [Fact]
    public void NormalizePath_CollapsesRelativeSegments()
    {
        // Path.GetFullPath resolves "." and ".." segments; both inputs name the same file.
        Normalize(@"C:\Data\Sub\..\Some.esp").Should().Be(Normalize(@"C:\Data\Some.esp"));
    }

    [Fact]
    public void NormalizePath_TreatsForwardAndBackSlashSeparatorsEquivalently()
    {
        // On Windows both separators are accepted by GetFullPath and normalize identically.
        Normalize(@"C:\Data\Some.esp").Should().Be(Normalize("C:/Data/Some.esp"));
    }

    [Fact]
    public void NormalizePath_PlainFileName_FullyQualifiesAndUppercases()
    {
        var result = Normalize("Plugin.esp");
        result.Should().EndWith("PLUGIN.ESP");
        // GetFullPath qualifies against the current directory -> an absolute, rooted path.
        System.IO.Path.IsPathRooted(result).Should().BeTrue();
    }

    [Fact]
    public void NormalizePath_InvalidPath_FallsBackToUpperInvariant()
    {
        // Embedded null char makes GetFullPath throw; the catch returns ToUpperInvariant().
        var bad = "bad\0path.esp";
        Normalize(bad).Should().Be(bad.ToUpperInvariant());
    }

    // ---------------------------------------------------------------------
    // RecordLookupFallBack enum
    // ---------------------------------------------------------------------

    [Fact]
    public void RecordLookupFallBack_OrdinalsAndMembership()
    {
        ((int)RecordHandler.RecordLookupFallBack.None).Should().Be(0);
        ((int)RecordHandler.RecordLookupFallBack.Origin).Should().Be(1);
        ((int)RecordHandler.RecordLookupFallBack.Winner).Should().Be(2);
        System.Enum.GetValues<RecordHandler.RecordLookupFallBack>().Should().HaveCount(3);
    }

    [Fact]
    public void RecordLookupFallBack_DefaultIsNone()
    {
        default(RecordHandler.RecordLookupFallBack).Should().Be(RecordHandler.RecordLookupFallBack.None);
    }

    // ---------------------------------------------------------------------
    // GetStatusReport (empty-cache branch)
    // ---------------------------------------------------------------------

    [Fact]
    public void GetStatusReport_NoLinkCaches_ReturnsEmptyMessage()
    {
        var handler = MakeHandler();
        handler.GetStatusReport().Should().Be("No plugins link caches currently created.");
    }
}
