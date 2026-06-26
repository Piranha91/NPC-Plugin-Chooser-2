using System.Collections;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="RecordDeltaPatcher"/> — diff/apply engine that computes property-level
/// deltas between two records and replays them onto a destination record. These tests
/// exercise the deterministic, in-memory surface: <see cref="RecordDeltaPatcher.GetPropertyDiffs"/>,
/// <see cref="RecordDeltaPatcher.ApplyPropertyDiffs"/>, the public nested
/// <c>ValueDiff</c>/<c>ListDiff</c> classes, and the private <c>IsSimpleType</c> classifier.
///
/// The patcher's only ctor dependency is a real <see cref="Settings"/> (used for the
/// localization language during log-context formatting), so it is constructed directly.
/// No Skyrim install or link cache is needed: records are built in-memory via
/// <see cref="MutagenFixtures"/>.
/// </summary>
public class RecordDeltaPatcherDiffTests
{
    private static RecordDeltaPatcher MakePatcher() => new(new Settings());

    /// <summary>The diff/apply methods log against a non-null record context; any real getter works.</summary>
    private static (IMajorRecordGetter ctx, ModKey mk) Ctx(INpcGetter npc, SkyrimMod mod) =>
        (npc, mod.ModKey);

    /// <summary>Pull the single diff for a named property out of a diff list (or null if absent).</summary>
    private static RecordDeltaPatcher.PropertyDiff? DiffFor(
        IReadOnlyList<RecordDeltaPatcher.PropertyDiff> diffs, string propertyName) =>
        diffs.SingleOrDefault(d => d.PropertyName == propertyName);

    // ---------------------------------------------------------------------
    // GetPropertyDiffs — scalar behavior
    // ---------------------------------------------------------------------

    [Fact]
    public void GetPropertyDiffs_IdenticalScalars_ProducesNoDiffForThatProperty()
    {
        var patcher = MakePatcher();
        var modA = MutagenFixtures.NewMod("A.esp");
        var modB = MutagenFixtures.NewMod("B.esp");
        var a = MutagenFixtures.NewNpc(modA, editorId: "Hero", name: "Hero");
        var b = MutagenFixtures.NewNpc(modB, editorId: "Hero", name: "Hero");
        a.Height = 1.0f;
        b.Height = 1.0f;

        var (ctx, mk) = Ctx(a, modA);
        var diffs = patcher.GetPropertyDiffs(a, b, ctx, mk);

        DiffFor(diffs, nameof(Npc.Height)).Should().BeNull("equal scalars are not a difference");
        DiffFor(diffs, nameof(Npc.Name)).Should().BeNull("equal translated strings compare equal");
    }

    [Fact]
    public void GetPropertyDiffs_ScalarDiffers_ProducesValueDiffCarryingSourceValue()
    {
        var patcher = MakePatcher();
        var modA = MutagenFixtures.NewMod("A.esp");
        var modB = MutagenFixtures.NewMod("B.esp");
        var a = MutagenFixtures.NewNpc(modA, editorId: "Hero");
        var b = MutagenFixtures.NewNpc(modB, editorId: "Hero");
        a.Height = 1.25f;
        b.Height = 0.5f;

        var (ctx, mk) = Ctx(a, modA);
        var diffs = patcher.GetPropertyDiffs(a, b, ctx, mk);

        var heightDiff = DiffFor(diffs, nameof(Npc.Height));
        heightDiff.Should().NotBeNull();
        heightDiff.Should().BeOfType<RecordDeltaPatcher.ValueDiff>();
        // The ValueDiff carries the SOURCE value (the override we want to splice in).
        heightDiff!.GetValue().Should().Be(1.25f);
        ((RecordDeltaPatcher.ValueDiff)heightDiff).NewValue.Should().Be(1.25f);
    }

    [Fact]
    public void GetPropertyDiffs_NameDiffers_ProducesValueDiff()
    {
        var patcher = MakePatcher();
        var modA = MutagenFixtures.NewMod("A.esp");
        var modB = MutagenFixtures.NewMod("B.esp");
        var a = MutagenFixtures.NewNpc(modA, editorId: "Hero", name: "Aldric");
        var b = MutagenFixtures.NewNpc(modB, editorId: "Hero", name: "Bjorn");

        var (ctx, mk) = Ctx(a, modA);
        var diffs = patcher.GetPropertyDiffs(a, b, ctx, mk);

        DiffFor(diffs, nameof(Npc.Name)).Should().BeOfType<RecordDeltaPatcher.ValueDiff>();
    }

    [Fact]
    public void GetPropertyDiffs_NullSource_ThrowsArgumentNullException()
    {
        var patcher = MakePatcher();
        var mod = MutagenFixtures.NewMod("A.esp");
        var ctxNpc = MutagenFixtures.NewNpc(mod, editorId: "Ctx");
        var target = MutagenFixtures.NewNpc(mod, editorId: "Target");

        Action act = () => patcher.GetPropertyDiffs(null!, target, ctxNpc, mod.ModKey);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetPropertyDiffs_NullTarget_ThrowsArgumentNullException()
    {
        var patcher = MakePatcher();
        var mod = MutagenFixtures.NewMod("A.esp");
        var ctxNpc = MutagenFixtures.NewNpc(mod, editorId: "Ctx");
        var source = MutagenFixtures.NewNpc(mod, editorId: "Source");

        Action act = () => patcher.GetPropertyDiffs(source, null!, ctxNpc, mod.ModKey);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---------------------------------------------------------------------
    // GetPropertyDiffs — list (Keywords) behavior
    // ---------------------------------------------------------------------

    [Fact]
    public void GetPropertyDiffs_EqualKeywordLists_ProducesNoListDiff()
    {
        var patcher = MakePatcher();
        var modA = MutagenFixtures.NewMod("A.esp");
        var modB = MutagenFixtures.NewMod("B.esp");
        var kw = modA.Keywords.AddNew("KW_Shared");

        var a = MutagenFixtures.NewNpc(modA, editorId: "Hero");
        var b = MutagenFixtures.NewNpc(modB, editorId: "Hero");
        a.Keywords = new() { kw };
        b.Keywords = new() { kw };

        var (ctx, mk) = Ctx(a, modA);
        var diffs = patcher.GetPropertyDiffs(a, b, ctx, mk);

        DiffFor(diffs, nameof(Npc.Keywords))
            .Should().BeNull("ListDiff with no additions/removals is suppressed");
    }

    [Fact]
    public void GetPropertyDiffs_ExtraKeywordInSource_ProducesListDiffWithAddedItem()
    {
        var patcher = MakePatcher();
        var modA = MutagenFixtures.NewMod("A.esp");
        var modB = MutagenFixtures.NewMod("B.esp");
        var shared = modA.Keywords.AddNew("KW_Shared");
        var extra = modA.Keywords.AddNew("KW_Extra");

        var a = MutagenFixtures.NewNpc(modA, editorId: "Hero");
        var b = MutagenFixtures.NewNpc(modB, editorId: "Hero");
        a.Keywords = new() { shared, extra };
        b.Keywords = new() { shared };

        var (ctx, mk) = Ctx(a, modA);
        var diffs = patcher.GetPropertyDiffs(a, b, ctx, mk);

        var kwDiff = DiffFor(diffs, nameof(Npc.Keywords));
        kwDiff.Should().BeOfType<RecordDeltaPatcher.ListDiff>();
        var listDiff = (RecordDeltaPatcher.ListDiff)kwDiff!;
        listDiff.HasChanges.Should().BeTrue();
        listDiff.RemovedItems.Should().BeEmpty();
        listDiff.AddedItems.Should().ContainSingle();
        // FormLink equality is by FormKey; the added item is the "extra" keyword link.
        ((IFormLinkGetter)listDiff.AddedItems.Single()!).FormKey.Should().Be(extra.FormKey);
    }

    [Fact]
    public void GetPropertyDiffs_MissingKeywordInSource_ProducesListDiffWithRemovedItem()
    {
        var patcher = MakePatcher();
        var modA = MutagenFixtures.NewMod("A.esp");
        var modB = MutagenFixtures.NewMod("B.esp");
        var shared = modA.Keywords.AddNew("KW_Shared");
        var only = modA.Keywords.AddNew("KW_OnlyInBase");

        var a = MutagenFixtures.NewNpc(modA, editorId: "Hero");
        var b = MutagenFixtures.NewNpc(modB, editorId: "Hero");
        a.Keywords = new() { shared };
        b.Keywords = new() { shared, only };

        var (ctx, mk) = Ctx(a, modA);
        var diffs = patcher.GetPropertyDiffs(a, b, ctx, mk);

        var listDiff = (RecordDeltaPatcher.ListDiff)DiffFor(diffs, nameof(Npc.Keywords))!;
        listDiff.AddedItems.Should().BeEmpty();
        listDiff.RemovedItems.Should().ContainSingle();
        ((IFormLinkGetter)listDiff.RemovedItems.Single()!).FormKey.Should().Be(only.FormKey);
    }

    // ---------------------------------------------------------------------
    // ListDiff — direct construction / equality semantics
    // ---------------------------------------------------------------------

    [Fact]
    public void ListDiff_NullTargetList_TreatsEverythingAsAdded()
    {
        var mod = MutagenFixtures.NewMod("A.esp");
        var kw1 = mod.Keywords.AddNew("KW_1");
        var kw2 = mod.Keywords.AddNew("KW_2");
        var source = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { kw1.ToLink(), kw2.ToLink() };

        var diff = new RecordDeltaPatcher.ListDiff(nameof(Npc.Keywords), source, null);

        diff.HasChanges.Should().BeTrue();
        diff.AddedItems.Should().HaveCount(2);
        diff.RemovedItems.Should().BeEmpty();
    }

    [Fact]
    public void ListDiff_DeduplicatesByFormKey_AcrossLinkInstances()
    {
        var mod = MutagenFixtures.NewMod("A.esp");
        var kw = mod.Keywords.AddNew("KW_Same");

        // Two DISTINCT FormLink instances pointing at the same FormKey must be treated as equivalent.
        var source = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { kw.ToLink() };
        var target = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { kw.ToLink() };

        var diff = new RecordDeltaPatcher.ListDiff(nameof(Npc.Keywords), source, target);

        diff.HasChanges.Should().BeFalse("equivalent FormKeys are not adds or removes");
        diff.AddedItems.Should().BeEmpty();
        diff.RemovedItems.Should().BeEmpty();
    }

    [Fact]
    public void ListDiff_GetValue_ReturnsAddedItems()
    {
        var mod = MutagenFixtures.NewMod("A.esp");
        var kw = mod.Keywords.AddNew("KW_New");
        var source = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { kw.ToLink() };

        var diff = new RecordDeltaPatcher.ListDiff(nameof(Npc.Keywords), source, null);

        diff.GetValue().Should().BeSameAs(diff.AddedItems);
    }

    [Fact]
    public void ValueDiff_GetValue_ReturnsNewValue()
    {
        var diff = new RecordDeltaPatcher.ValueDiff(nameof(Npc.Height), 1.75f);
        diff.PropertyName.Should().Be(nameof(Npc.Height));
        diff.NewValue.Should().Be(1.75f);
        diff.GetValue().Should().Be(1.75f);
    }

    // ---------------------------------------------------------------------
    // ApplyPropertyDiffs — round-trips: B + diffs(A,B) == A (per property)
    // ---------------------------------------------------------------------

    [Fact]
    public void ApplyPropertyDiffs_ScalarValueDiff_SetsDestinationToSourceValue()
    {
        var patcher = MakePatcher();
        var modA = MutagenFixtures.NewMod("A.esp");
        var modB = MutagenFixtures.NewMod("B.esp");
        var a = MutagenFixtures.NewNpc(modA, editorId: "Hero");
        var b = MutagenFixtures.NewNpc(modB, editorId: "Hero");
        a.Height = 1.5f;
        b.Height = 0.25f;

        var (ctx, mk) = Ctx(a, modA);
        var diffs = patcher.GetPropertyDiffs(a, b, ctx, mk);
        var heightOnly = diffs.Where(d => d.PropertyName == nameof(Npc.Height)).ToList();
        heightOnly.Should().ContainSingle();

        patcher.ApplyPropertyDiffs(b, heightOnly, ctx, mk);

        b.Height.Should().Be(1.5f, "B + diff(A,B) == A for the Height property");
    }

    [Fact]
    public void ApplyPropertyDiffs_NameValueDiff_SetsDestinationName()
    {
        var patcher = MakePatcher();
        var modA = MutagenFixtures.NewMod("A.esp");
        var modB = MutagenFixtures.NewMod("B.esp");
        var a = MutagenFixtures.NewNpc(modA, editorId: "Hero", name: "Aldric");
        var b = MutagenFixtures.NewNpc(modB, editorId: "Hero", name: "Bjorn");

        var (ctx, mk) = Ctx(a, modA);
        var diffs = patcher.GetPropertyDiffs(a, b, ctx, mk);
        var nameOnly = diffs.Where(d => d.PropertyName == nameof(Npc.Name)).ToList();
        nameOnly.Should().ContainSingle();

        patcher.ApplyPropertyDiffs(b, nameOnly, ctx, mk);

        b.Name?.String.Should().Be("Aldric");
    }

    [Fact]
    public void ApplyPropertyDiffs_KeywordListDiff_AppendsMissingAndRemovesExtra()
    {
        var patcher = MakePatcher();
        var modA = MutagenFixtures.NewMod("A.esp");
        var modB = MutagenFixtures.NewMod("B.esp");
        var shared = modA.Keywords.AddNew("KW_Shared");
        var addInA = modA.Keywords.AddNew("KW_AddInA");
        var onlyInB = modA.Keywords.AddNew("KW_OnlyInB");

        var a = MutagenFixtures.NewNpc(modA, editorId: "Hero");
        var b = MutagenFixtures.NewNpc(modB, editorId: "Hero");
        a.Keywords = new() { shared, addInA };
        b.Keywords = new() { shared, onlyInB };

        var (ctx, mk) = Ctx(a, modA);
        var diffs = patcher.GetPropertyDiffs(a, b, ctx, mk);
        var kwOnly = diffs.Where(d => d.PropertyName == nameof(Npc.Keywords)).ToList();
        kwOnly.Should().ContainSingle();

        patcher.ApplyPropertyDiffs(b, kwOnly, ctx, mk);

        // After applying, B's keyword set should equal A's keyword set (shared + addInA, minus onlyInB).
        var resultFormKeys = b.Keywords!.Select(l => l.FormKey).ToHashSet();
        resultFormKeys.Should().BeEquivalentTo(new[] { shared.FormKey, addInA.FormKey });
        resultFormKeys.Should().NotContain(onlyInB.FormKey);
    }

    [Fact]
    public void ApplyPropertyDiffs_KeywordListDiff_DoesNotDuplicateAlreadyPresentItem()
    {
        // The diff's AddedItems are computed against B's base list, so re-applying must not
        // create duplicates even if the destination already contains a "shared" entry.
        var patcher = MakePatcher();
        var modA = MutagenFixtures.NewMod("A.esp");
        var modB = MutagenFixtures.NewMod("B.esp");
        var shared = modA.Keywords.AddNew("KW_Shared");
        var extra = modA.Keywords.AddNew("KW_Extra");

        var a = MutagenFixtures.NewNpc(modA, editorId: "Hero");
        var b = MutagenFixtures.NewNpc(modB, editorId: "Hero");
        a.Keywords = new() { shared, extra };
        b.Keywords = new() { shared };

        var (ctx, mk) = Ctx(a, modA);
        var diffs = patcher.GetPropertyDiffs(a, b, ctx, mk);
        patcher.ApplyPropertyDiffs(b, diffs.Where(d => d.PropertyName == nameof(Npc.Keywords)).ToList(), ctx, mk);

        b.Keywords!.Count(l => l.FormKey == shared.FormKey).Should().Be(1, "shared keyword not duplicated");
        b.Keywords!.Select(l => l.FormKey).Should().Contain(extra.FormKey);
    }

    [Fact]
    public void ApplyPropertyDiffs_FullRoundTrip_HeightAndName_TogetherMakeBMatchA()
    {
        var patcher = MakePatcher();
        var modA = MutagenFixtures.NewMod("A.esp");
        var modB = MutagenFixtures.NewMod("B.esp");
        var a = MutagenFixtures.NewNpc(modA, editorId: "Hero", name: "Aldric");
        var b = MutagenFixtures.NewNpc(modB, editorId: "Hero", name: "Bjorn");
        a.Height = 1.1f;
        b.Height = 0.9f;

        var (ctx, mk) = Ctx(a, modA);
        var diffs = patcher.GetPropertyDiffs(a, b, ctx, mk);
        var targeted = diffs
            .Where(d => d.PropertyName is nameof(Npc.Height) or nameof(Npc.Name))
            .ToList();

        patcher.ApplyPropertyDiffs(b, targeted, ctx, mk);

        b.Height.Should().Be(a.Height);
        b.Name?.String.Should().Be(a.Name?.String);
    }

    // ---------------------------------------------------------------------
    // ApplyPropertyDiffs — null guards
    // ---------------------------------------------------------------------

    [Fact]
    public void ApplyPropertyDiffs_NullDestination_ThrowsArgumentNullException()
    {
        var patcher = MakePatcher();
        var mod = MutagenFixtures.NewMod("A.esp");
        var ctxNpc = MutagenFixtures.NewNpc(mod, editorId: "Ctx");
        var diffs = new List<RecordDeltaPatcher.PropertyDiff>();

        Action act = () => patcher.ApplyPropertyDiffs(null!, diffs, ctxNpc, mod.ModKey);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ApplyPropertyDiffs_NullDiffs_ThrowsArgumentNullException()
    {
        var patcher = MakePatcher();
        var mod = MutagenFixtures.NewMod("A.esp");
        var dest = MutagenFixtures.NewNpc(mod, editorId: "Dest");

        Action act = () => patcher.ApplyPropertyDiffs(dest, null!, dest, mod.ModKey);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ApplyPropertyDiffs_NonMajorRecordDestination_IsNoOpAndDoesNotThrow()
    {
        // ApplyPropertyDiffs requires the destination to be a MajorRecord; anything else is
        // logged and skipped (returns) rather than throwing.
        var patcher = MakePatcher();
        var mod = MutagenFixtures.NewMod("A.esp");
        var ctxNpc = MutagenFixtures.NewNpc(mod, editorId: "Ctx");
        var diffs = new List<RecordDeltaPatcher.PropertyDiff>
        {
            new RecordDeltaPatcher.ValueDiff(nameof(Npc.Height), 1.0f)
        };

        Action act = () => patcher.ApplyPropertyDiffs("not a record", diffs, ctxNpc, mod.ModKey);
        act.Should().NotThrow();
    }

    [Fact]
    public void ApplyPropertyDiffs_EmptyDiffs_LeavesDestinationUnchanged()
    {
        var patcher = MakePatcher();
        var mod = MutagenFixtures.NewMod("A.esp");
        var dest = MutagenFixtures.NewNpc(mod, editorId: "Dest", name: "Same");
        dest.Height = 0.7f;

        var (ctx, mk) = Ctx(dest, mod);
        patcher.ApplyPropertyDiffs(dest, new List<RecordDeltaPatcher.PropertyDiff>(), ctx, mk);

        dest.Height.Should().Be(0.7f);
        dest.Name?.String.Should().Be("Same");
    }

    [Fact]
    public void ApplyPropertyDiffs_UnknownPropertyName_IsSkippedWithoutThrowing()
    {
        var patcher = MakePatcher();
        var mod = MutagenFixtures.NewMod("A.esp");
        var dest = MutagenFixtures.NewNpc(mod, editorId: "Dest");
        var diffs = new List<RecordDeltaPatcher.PropertyDiff>
        {
            new RecordDeltaPatcher.ValueDiff("ThisPropertyDoesNotExistOnNpc", 5)
        };

        var (ctx, mk) = Ctx(dest, mod);
        Action act = () => patcher.ApplyPropertyDiffs(dest, diffs, ctx, mk);
        act.Should().NotThrow();
    }

    // ---------------------------------------------------------------------
    // IsSimpleType — private classifier (via Reflect)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(float))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(string))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(Guid))]
    public void IsSimpleType_PrimitivesAndCommonValueTypes_AreSimple(Type t)
    {
        var patcher = MakePatcher();
        Reflect.Invoke<bool>(patcher, "IsSimpleType", t).Should().BeTrue();
    }

    [Fact]
    public void IsSimpleType_Enum_IsSimple()
    {
        var patcher = MakePatcher();
        Reflect.Invoke<bool>(patcher, "IsSimpleType", typeof(NpcConfiguration.Flag)).Should().BeTrue();
    }

    [Fact]
    public void IsSimpleType_FormLinkVariants_AreSimple()
    {
        var patcher = MakePatcher();
        // The getter/setter FormLink INTERFACES are treated as simple (copied by value).
        Reflect.Invoke<bool>(patcher, "IsSimpleType", typeof(IFormLinkGetter<IKeywordGetter>))
            .Should().BeTrue();
        Reflect.Invoke<bool>(patcher, "IsSimpleType", typeof(IFormLink<IKeywordGetter>))
            .Should().BeTrue();
        // Concrete FormLink<T> is a reference type in this Mutagen build, so it falls outside
        // the IsValueType-gated FormLink<> branch -> NOT simple (the field is recursed instead).
        Reflect.Invoke<bool>(patcher, "IsSimpleType", typeof(FormLink<IKeywordGetter>))
            .Should().BeFalse();
    }

    [Fact]
    public void IsSimpleType_ComplexMutagenSubObject_IsNotSimple()
    {
        var patcher = MakePatcher();
        // NpcConfiguration is a sub-record class that requires recursive property copying.
        Reflect.Invoke<bool>(patcher, "IsSimpleType", typeof(NpcConfiguration)).Should().BeFalse();
    }

    [Fact]
    public void IsSimpleType_RecordClass_IsNotSimple()
    {
        var patcher = MakePatcher();
        Reflect.Invoke<bool>(patcher, "IsSimpleType", typeof(Npc)).Should().BeFalse();
    }

    [Fact]
    public void IsSimpleType_SystemType_IsSimple()
    {
        // The classifier short-circuits System.Type (and subclasses) to simple.
        var patcher = MakePatcher();
        Reflect.Invoke<bool>(patcher, "IsSimpleType", typeof(Type)).Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // Reinitialize — clears recorded patched values / log without throwing
    // ---------------------------------------------------------------------

    [Fact]
    public void Reinitialize_ClearsInternalState_WithoutThrowing()
    {
        var patcher = MakePatcher();
        var modA = MutagenFixtures.NewMod("A.esp");
        var modB = MutagenFixtures.NewMod("B.esp");
        var a = MutagenFixtures.NewNpc(modA, editorId: "Hero");
        var b = MutagenFixtures.NewNpc(modB, editorId: "Hero");
        a.Height = 2.0f;
        b.Height = 1.0f;

        var (ctx, mk) = Ctx(a, modA);
        var diffs = patcher.GetPropertyDiffs(a, b, ctx, mk)
            .Where(d => d.PropertyName == nameof(Npc.Height)).ToList();
        patcher.ApplyPropertyDiffs(b, diffs, ctx, mk);

        // Patched-values cache now has an entry for B's FormKey; clearing it must not throw
        // and the cache must be emptied.
        Action act = () => patcher.Reinitialize(clearLog: true);
        act.Should().NotThrow();

        var patched = Reflect.GetField<IDictionary>(patcher, "_patchedValues");
        patched.Count.Should().Be(0);
    }
}
