using System.ComponentModel;
using System.Reflection;
using System.Windows.Media;
using CharacterViewer.Rendering;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pure, link-cache-free surface of <see cref="VM_NpcSelectionBar"/>:
///   * the private static FaceGen-stat math helpers — <c>NormalizeMetric</c> (min-max scale
///     to [0,1] with the degenerate "all equal / single tile" → 0.5 rule),
///     <c>InterpolateSpectrumColor</c> (piecewise low→mid→high gradient with t-clamping),
///     and <c>FlagOutliers</c> (TopPercent vs StdDevAbove, both no-ops for n &lt; 2 and
///     for a zero-spread set);
///   * the private <c>GetPluginKeyForNpc</c> resolution order
///     (disambiguation → AvailablePlugins → CorrespondingModKeys → null);
///   * the enums declared in this file plus <c>NpcSearchType.cs</c> / <c>ModNpcSearchType.cs</c>
///     (membership, ordinals, [Description] labels, Enum.Parse round-trip).
///
/// All helpers under test are pure functions of their arguments (no field reads beyond the
/// passed-in <see cref="VM_ModSetting"/> / <see cref="Settings"/> / tile list), so the VM and
/// the per-mod VM are allocated with <see cref="Reflect.Uninitialized{T}"/> — their heavy
/// ReactiveUI constructors never run. Static privates are reached via
/// <see cref="Reflect.InvokeStatic{TOwner,T}"/>; the instance private via
/// <see cref="Reflect.Invoke{T}"/> on an uninitialized receiver. No STA / scheduler /
/// game install required, so this lives in the pure-unit namespace.
/// </summary>
public class VM_NpcSelectionBarPureTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Allocates a tile with only its [Reactive] FaceGenStats backing field set
    /// — enough for the static metric/outlier helpers, which read nothing else.</summary>
    private static VM_NpcsMenuMugshot Tile(long fileSizeBytes, int triangles = 0, int vertices = 0)
    {
        var tile = Reflect.Uninitialized<VM_NpcsMenuMugshot>();
        NifMeshBuilder.FaceGenStats? stats =
            new NifMeshBuilder.FaceGenStats(true, vertices, triangles, 0, fileSizeBytes);
        // Set the [Reactive] FaceGenStats backing field directly — bypassing RaiseAndSetIfChanged,
        // which the uninitialized object can't run. Reflect resolves Fody's "$FaceGenStats" field.
        Reflect.SetField(tile, "FaceGenStats", stats);
        return tile;
    }

    private static double[] NormalizeBySize(List<VM_NpcsMenuMugshot> tiles) =>
        Reflect.InvokeStatic<VM_NpcSelectionBar, double[]>(
            "NormalizeMetric",
            tiles,
            (Func<VM_NpcsMenuMugshot, double>)(t => t.FaceGenStats!.Value.FileSizeBytes))!;

    private static SolidColorBrush Spectrum(double t, Color low, Color mid, Color high) =>
        Reflect.InvokeStatic<VM_NpcSelectionBar, SolidColorBrush>(
            "InterpolateSpectrumColor", t, low, mid, high)!;

    private static bool[] FlagBySize(List<VM_NpcsMenuMugshot> tiles, Settings s) =>
        Reflect.InvokeStatic<VM_NpcSelectionBar, bool[]>(
            "FlagOutliers",
            tiles,
            (Func<VM_NpcsMenuMugshot, double>)(t => t.FaceGenStats!.Value.FileSizeBytes),
            s)!;

    private static string? Description<T>(T value) where T : Enum =>
        typeof(T).GetField(value.ToString())!
            .GetCustomAttribute<DescriptionAttribute>()?.Description;

    // ------------------------------------------------------------------
    // NormalizeMetric
    // ------------------------------------------------------------------

    [Fact]
    public void NormalizeMetric_SpreadValues_ScaledToZeroToOneByMinMax()
    {
        var t = NormalizeBySize(new List<VM_NpcsMenuMugshot> { Tile(0), Tile(5), Tile(10) });
        t.Should().Equal(0.0, 0.5, 1.0);
    }

    [Fact]
    public void NormalizeMetric_UnsortedInput_NormalizesAgainstGlobalMinMax()
    {
        // min=2, max=10, range=8: (6-2)/8 = 0.5, (10-2)/8 = 1, (2-2)/8 = 0.
        var t = NormalizeBySize(new List<VM_NpcsMenuMugshot> { Tile(6), Tile(10), Tile(2) });
        t.Should().Equal(0.5, 1.0, 0.0);
    }

    [Fact]
    public void NormalizeMetric_AllEqual_ReturnsHalfForEvery_ZeroRangeGuard()
    {
        var t = NormalizeBySize(new List<VM_NpcsMenuMugshot> { Tile(7), Tile(7), Tile(7) });
        t.Should().Equal(0.5, 0.5, 0.5);
    }

    [Fact]
    public void NormalizeMetric_SingleTile_ReturnsHalf()
    {
        var t = NormalizeBySize(new List<VM_NpcsMenuMugshot> { Tile(123) });
        t.Should().Equal(0.5);
    }

    [Fact]
    public void NormalizeMetric_EmptyList_ReturnsEmptyArray()
    {
        var t = NormalizeBySize(new List<VM_NpcsMenuMugshot>());
        t.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // InterpolateSpectrumColor
    // ------------------------------------------------------------------

    [Fact]
    public void InterpolateSpectrumColor_TZero_ReturnsLowColor()
    {
        var low = Color.FromRgb(10, 20, 30);
        var brush = Spectrum(0.0, low, Color.FromRgb(100, 100, 100), Color.FromRgb(200, 210, 220));
        brush.Color.Should().Be(low);
    }

    [Fact]
    public void InterpolateSpectrumColor_THalf_ReturnsMidColorExactly()
    {
        var mid = Color.FromRgb(40, 80, 120);
        var brush = Spectrum(0.5, Color.FromRgb(0, 0, 0), mid, Color.FromRgb(255, 255, 255));
        brush.Color.Should().Be(mid);
    }

    [Fact]
    public void InterpolateSpectrumColor_TOne_ReturnsHighColor()
    {
        var high = Color.FromRgb(7, 8, 9);
        var brush = Spectrum(1.0, Color.FromRgb(0, 0, 0), Color.FromRgb(50, 50, 50), high);
        brush.Color.Should().Be(high);
    }

    [Fact]
    public void InterpolateSpectrumColor_LowerQuarter_BlendsLowTowardMid()
    {
        // t=0.25 -> localT = 0.5 on the low..mid leg -> exact midpoint of the two endpoints.
        var brush = Spectrum(0.25, Color.FromRgb(0, 0, 0), Color.FromRgb(100, 200, 40),
            Color.FromRgb(255, 255, 255));
        brush.Color.Should().Be(Color.FromRgb(50, 100, 20));
    }

    [Fact]
    public void InterpolateSpectrumColor_UpperQuarter_BlendsMidTowardHigh()
    {
        // t=0.75 -> localT = 0.5 on the mid..high leg -> midpoint of mid and high.
        var brush = Spectrum(0.75, Color.FromRgb(0, 0, 0), Color.FromRgb(100, 100, 100),
            Color.FromRgb(200, 200, 200));
        brush.Color.Should().Be(Color.FromRgb(150, 150, 150));
    }

    [Fact]
    public void InterpolateSpectrumColor_TBelowZero_ClampsToLow()
    {
        var low = Color.FromRgb(11, 22, 33);
        var brush = Spectrum(-5.0, low, Color.FromRgb(100, 100, 100), Color.FromRgb(200, 200, 200));
        brush.Color.Should().Be(low);
    }

    [Fact]
    public void InterpolateSpectrumColor_TAboveOne_ClampsToHigh()
    {
        var high = Color.FromRgb(44, 55, 66);
        var brush = Spectrum(42.0, Color.FromRgb(0, 0, 0), Color.FromRgb(100, 100, 100), high);
        brush.Color.Should().Be(high);
    }

    [Fact]
    public void InterpolateSpectrumColor_ReturnsFrozenBrush()
    {
        var brush = Spectrum(0.3, Colors.Black, Colors.Gray, Colors.White);
        brush.IsFrozen.Should().BeTrue("the helper freezes the brush for cross-thread reuse");
    }

    // ------------------------------------------------------------------
    // FlagOutliers — TopPercent
    // ------------------------------------------------------------------

    private static Settings TopPercent(double thresholdPercent) => new()
    {
        FaceGenHighlightCriterion = FaceGenHighlightCriterion.TopPercent,
        FaceGenHighlightThreshold = thresholdPercent
    };

    private static Settings StdDev(double sigmas) => new()
    {
        FaceGenHighlightCriterion = FaceGenHighlightCriterion.StdDevAbove,
        FaceGenHighlightThreshold = sigmas
    };

    [Fact]
    public void FlagOutliers_EmptyList_ReturnsEmptyArray()
    {
        FlagBySize(new List<VM_NpcsMenuMugshot>(), TopPercent(50)).Should().BeEmpty();
    }

    [Fact]
    public void FlagOutliers_SingleTile_NeverSelfHighlights_TopPercent()
    {
        // n < 2 short-circuits: a lone tile is never flagged regardless of threshold.
        FlagBySize(new List<VM_NpcsMenuMugshot> { Tile(1000) }, TopPercent(100))
            .Should().Equal(false);
    }

    [Fact]
    public void FlagOutliers_TopPercent_FlagsCeilOfPercentTimesN_HeaviestTiles()
    {
        // 4 tiles, 50% -> ceil(2) = 2 heaviest flagged: sizes 40 and 30 (indices 3 and 2).
        var tiles = new List<VM_NpcsMenuMugshot> { Tile(10), Tile(20), Tile(30), Tile(40) };
        FlagBySize(tiles, TopPercent(50)).Should().Equal(false, false, true, true);
    }

    [Fact]
    public void FlagOutliers_TopPercent_RoundsUpFractionalCount()
    {
        // 3 tiles, 25% -> ceil(0.75) = 1 heaviest flagged (size 30 at index 2).
        var tiles = new List<VM_NpcsMenuMugshot> { Tile(10), Tile(20), Tile(30) };
        FlagBySize(tiles, TopPercent(25)).Should().Equal(false, false, true);
    }

    [Fact]
    public void FlagOutliers_TopPercent_ZeroThreshold_FlagsNone()
    {
        var tiles = new List<VM_NpcsMenuMugshot> { Tile(10), Tile(20), Tile(30) };
        FlagBySize(tiles, TopPercent(0)).Should().AllBeEquivalentTo(false);
    }

    [Fact]
    public void FlagOutliers_TopPercent_FullThreshold_FlagsAll()
    {
        var tiles = new List<VM_NpcsMenuMugshot> { Tile(10), Tile(20), Tile(30) };
        FlagBySize(tiles, TopPercent(100)).Should().AllBeEquivalentTo(true);
    }

    // ------------------------------------------------------------------
    // FlagOutliers — StdDevAbove
    // ------------------------------------------------------------------

    [Fact]
    public void FlagOutliers_StdDev_ZeroSpread_FlagsNone()
    {
        // Identical values -> std == 0 -> early return, nothing flagged.
        var tiles = new List<VM_NpcsMenuMugshot> { Tile(50), Tile(50), Tile(50) };
        FlagBySize(tiles, StdDev(1.0)).Should().AllBeEquivalentTo(false);
    }

    [Fact]
    public void FlagOutliers_StdDev_SingleTile_FlagsNone()
    {
        FlagBySize(new List<VM_NpcsMenuMugshot> { Tile(999) }, StdDev(1.0))
            .Should().Equal(false);
    }

    [Fact]
    public void FlagOutliers_StdDev_FlagsValuesAboveMeanPlusNSigma()
    {
        // values 0,0,0,0,100 -> mean 20, population std = sqrt(8000/5) = 40.
        // cutoff = 20 + 1*40 = 60; only the 100 tile (index 4) exceeds it.
        var tiles = new List<VM_NpcsMenuMugshot>
        {
            Tile(0), Tile(0), Tile(0), Tile(0), Tile(100)
        };
        FlagBySize(tiles, StdDev(1.0)).Should().Equal(false, false, false, false, true);
    }

    [Fact]
    public void FlagOutliers_StdDev_HighSigma_RaisesCutoffAboveEveryValue()
    {
        // Same data, 5 sigma -> cutoff = 20 + 5*40 = 220 > max(100): nothing flagged.
        var tiles = new List<VM_NpcsMenuMugshot>
        {
            Tile(0), Tile(0), Tile(0), Tile(0), Tile(100)
        };
        FlagBySize(tiles, StdDev(5.0)).Should().AllBeEquivalentTo(false);
    }

    // ------------------------------------------------------------------
    // GetPluginKeyForNpc — private instance method, pure over its argument
    // ------------------------------------------------------------------

    private static ModKey? GetPluginKey(VM_ModSetting? ms, FormKey npc)
    {
        var bar = Reflect.Uninitialized<VM_NpcSelectionBar>();
        return Reflect.Invoke<ModKey?>(bar, "GetPluginKeyForNpc", ms, npc);
    }

    /// <summary>Builds a VM_ModSetting without its heavy ctor, seeding only the three lookup
    /// members that GetPluginKeyForNpc consults.</summary>
    private static VM_ModSetting ModSettingWith(
        Dictionary<FormKey, ModKey>? disambig = null,
        Dictionary<FormKey, List<ModKey>>? available = null,
        IEnumerable<ModKey>? corresponding = null)
    {
        var ms = Reflect.Uninitialized<VM_ModSetting>();
        Reflect.SetField(ms, "<NpcPluginDisambiguation>k__BackingField",
            disambig ?? new Dictionary<FormKey, ModKey>());
        Reflect.SetField(ms, "<AvailablePluginsForNpcs>k__BackingField",
            available ?? new Dictionary<FormKey, List<ModKey>>());
        Reflect.SetField(ms, "<CorrespondingModKeys>k__BackingField",
            new System.Collections.ObjectModel.ObservableCollection<ModKey>(
                corresponding ?? Enumerable.Empty<ModKey>()));
        return ms;
    }

    private static readonly FormKey Npc = MutagenFixtures.Fk("000800:Source.esp");
    private static readonly ModKey Disambig = MutagenFixtures.Mk("Disambig.esp");
    private static readonly ModKey AvailA = MutagenFixtures.Mk("AvailA.esp");
    private static readonly ModKey AvailB = MutagenFixtures.Mk("AvailB.esp");
    private static readonly ModKey Corr = MutagenFixtures.Mk("Corr.esp");

    [Fact]
    public void GetPluginKeyForNpc_NullModSetting_ReturnsNull()
    {
        GetPluginKey(null, Npc).Should().BeNull();
    }

    [Fact]
    public void GetPluginKeyForNpc_DisambiguationEntry_WinsOverEverything()
    {
        var ms = ModSettingWith(
            disambig: new Dictionary<FormKey, ModKey> { [Npc] = Disambig },
            available: new Dictionary<FormKey, List<ModKey>> { [Npc] = new() { AvailA } },
            corresponding: new[] { Corr });

        GetPluginKey(ms, Npc).Should().Be(Disambig);
    }

    [Fact]
    public void GetPluginKeyForNpc_NoDisambiguation_UsesFirstAvailablePlugin()
    {
        var ms = ModSettingWith(
            available: new Dictionary<FormKey, List<ModKey>> { [Npc] = new() { AvailA, AvailB } },
            corresponding: new[] { Corr });

        GetPluginKey(ms, Npc).Should().Be(AvailA, "the first AvailablePlugins entry is taken before CorrespondingModKeys");
    }

    [Fact]
    public void GetPluginKeyForNpc_EmptyAvailableList_FallsThroughToCorrespondingModKeys()
    {
        // AvailablePluginsForNpcs has the key but an EMPTY list -> .Any() is false -> fall through.
        var ms = ModSettingWith(
            available: new Dictionary<FormKey, List<ModKey>> { [Npc] = new() },
            corresponding: new[] { Corr });

        GetPluginKey(ms, Npc).Should().Be(Corr);
    }

    [Fact]
    public void GetPluginKeyForNpc_NoDisambigNoAvailable_UsesFirstCorrespondingModKey()
    {
        var ms = ModSettingWith(corresponding: new[] { Corr, AvailB });
        GetPluginKey(ms, Npc).Should().Be(Corr);
    }

    [Fact]
    public void GetPluginKeyForNpc_NothingMatches_ReturnsNullKeyFromEmptyCorresponding()
    {
        // No disambiguation, no available entry, empty CorrespondingModKeys ->
        // FirstOrDefault() yields default(ModKey) (the null key), boxed as a non-null ModKey?.
        var ms = ModSettingWith();
        GetPluginKey(ms, Npc).Should().Be(default(ModKey));
    }

    // ------------------------------------------------------------------
    // NpcSearchType (NpcSearchType.cs)
    // ------------------------------------------------------------------

    [Fact]
    public void NpcSearchType_HasExpectedMembersAndOrdinals()
    {
        Enum.GetValues<NpcSearchType>().Should().HaveCount(13);
        ((int)NpcSearchType.Name).Should().Be(0);
        ((int)NpcSearchType.EditorID).Should().Be(1);
        ((int)NpcSearchType.InAppearanceMod).Should().Be(2);
        ((int)NpcSearchType.ChosenInMod).Should().Be(3);
        ((int)NpcSearchType.FromPlugin).Should().Be(4);
        ((int)NpcSearchType.FormKey).Should().Be(5);
        ((int)NpcSearchType.SelectionState).Should().Be(6);
        ((int)NpcSearchType.ShareStatus).Should().Be(7);
        ((int)NpcSearchType.Uniqueness).Should().Be(8);
        ((int)NpcSearchType.Race).Should().Be(9);
        ((int)NpcSearchType.Gender).Should().Be(10);
        ((int)NpcSearchType.Group).Should().Be(11);
        ((int)NpcSearchType.Template).Should().Be(12);
    }

    [Theory]
    [InlineData(NpcSearchType.Name, "Name")]
    [InlineData(NpcSearchType.EditorID, "EditorID")]
    [InlineData(NpcSearchType.InAppearanceMod, "In Appearance Mod")]
    [InlineData(NpcSearchType.ChosenInMod, "Chosen In Mod")]
    [InlineData(NpcSearchType.FromPlugin, "From Plugin")]
    [InlineData(NpcSearchType.FormKey, "FormKey")]
    [InlineData(NpcSearchType.SelectionState, "Selection State")]
    [InlineData(NpcSearchType.ShareStatus, "Shared/Guest Appearance")]
    [InlineData(NpcSearchType.Uniqueness, "Uniqueness")]
    [InlineData(NpcSearchType.Race, "Race")]
    [InlineData(NpcSearchType.Gender, "Gender")]
    [InlineData(NpcSearchType.Group, "Group")]
    [InlineData(NpcSearchType.Template, "Template")]
    public void NpcSearchType_DescriptionLabels(NpcSearchType value, string label)
    {
        Description(value).Should().Be(label);
    }

    [Fact]
    public void NpcSearchType_RoundTripsThroughEnumParse()
    {
        foreach (var v in Enum.GetValues<NpcSearchType>())
            Enum.Parse<NpcSearchType>(v.ToString()).Should().Be(v);
    }

    [Fact]
    public void UniquenessFilterType_Members()
    {
        Enum.GetValues<UniquenessFilterType>().Should().HaveCount(3);
        ((int)UniquenessFilterType.Any).Should().Be(0);
        ((int)UniquenessFilterType.Unique).Should().Be(1);
        ((int)UniquenessFilterType.Generic).Should().Be(2);
    }

    [Fact]
    public void GenderFilterType_Members()
    {
        Enum.GetValues<GenderFilterType>().Should().HaveCount(3);
        ((int)GenderFilterType.Any).Should().Be(0);
        ((int)GenderFilterType.Male).Should().Be(1);
        ((int)GenderFilterType.Female).Should().Be(2);
    }

    // ------------------------------------------------------------------
    // ModNpcSearchType (ModNpcSearchType.cs)
    // ------------------------------------------------------------------

    [Fact]
    public void ModNpcSearchType_Members()
    {
        Enum.GetValues<ModNpcSearchType>().Should().HaveCount(3);
        ((int)ModNpcSearchType.Name).Should().Be(0);
        ((int)ModNpcSearchType.EditorID).Should().Be(1);
        ((int)ModNpcSearchType.FormKey).Should().Be(2);
        // No [Description] attributes on this enum.
        Description(ModNpcSearchType.Name).Should().BeNull();
    }

    [Fact]
    public void ModNpcSearchType_RoundTripsThroughEnumParse()
    {
        foreach (var v in Enum.GetValues<ModNpcSearchType>())
            Enum.Parse<ModNpcSearchType>(v.ToString()).Should().Be(v);
    }

    // ------------------------------------------------------------------
    // Enums declared in VM_NpcSelectionBar.cs
    // ------------------------------------------------------------------

    [Fact]
    public void SearchLogic_Members()
    {
        Enum.GetValues<SearchLogic>().Should().HaveCount(2);
        ((int)SearchLogic.AND).Should().Be(0);
        ((int)SearchLogic.OR).Should().Be(1);
    }

    [Fact]
    public void SelectionStateFilterType_MembersAndDescriptions()
    {
        Enum.GetValues<SelectionStateFilterType>().Should().HaveCount(2);
        ((int)SelectionStateFilterType.NotMade).Should().Be(0);
        ((int)SelectionStateFilterType.Made).Should().Be(1);
        Description(SelectionStateFilterType.NotMade).Should().Be("Selection Not Made");
        Description(SelectionStateFilterType.Made).Should().Be("Selection Made");
    }

    [Fact]
    public void ShareStatusFilterType_MembersAndDescriptions()
    {
        Enum.GetValues<ShareStatusFilterType>().Should().HaveCount(5);
        ((int)ShareStatusFilterType.Any).Should().Be(0);
        ((int)ShareStatusFilterType.GuestAvailable).Should().Be(1);
        ((int)ShareStatusFilterType.GuestSelected).Should().Be(2);
        ((int)ShareStatusFilterType.Shared).Should().Be(3);
        ((int)ShareStatusFilterType.SharedAndSelected).Should().Be(4);
        Description(ShareStatusFilterType.Any).Should().Be("Any");
        Description(ShareStatusFilterType.GuestAvailable).Should().Be("Guest Available");
        Description(ShareStatusFilterType.GuestSelected).Should().Be("Guest Selected");
        Description(ShareStatusFilterType.Shared).Should().Be("Shared");
        Description(ShareStatusFilterType.SharedAndSelected).Should().Be("Shared & Selected");
    }

    [Fact]
    public void NpcSortProperty_Members()
    {
        Enum.GetValues<NpcSortProperty>().Should().HaveCount(4);
        ((int)NpcSortProperty.FormID).Should().Be(0);
        ((int)NpcSortProperty.Name).Should().Be(1);
        ((int)NpcSortProperty.EditorID).Should().Be(2);
        ((int)NpcSortProperty.FormKey).Should().Be(3);
    }

    [Fact]
    public void TemplateFilterType_MembersAndDescriptions()
    {
        Enum.GetValues<TemplateFilterType>().Should().HaveCount(6);
        ((int)TemplateFilterType.BaseHasTemplate).Should().Be(0);
        ((int)TemplateFilterType.BaseIsTemplate).Should().Be(1);
        ((int)TemplateFilterType.WinnerHasTemplate).Should().Be(2);
        ((int)TemplateFilterType.WinnerIsTemplate).Should().Be(3);
        ((int)TemplateFilterType.AppModsHaveTemplate).Should().Be(4);
        ((int)TemplateFilterType.AppModsUseAsTemplate).Should().Be(5);
        Description(TemplateFilterType.BaseHasTemplate).Should().Be("Base Record Has Template");
        Description(TemplateFilterType.BaseIsTemplate).Should().Be("Base Record Is Template");
        Description(TemplateFilterType.WinnerHasTemplate).Should().Be("Winning Override Has Template");
        Description(TemplateFilterType.WinnerIsTemplate).Should().Be("Winning Override Is Template");
        Description(TemplateFilterType.AppModsHaveTemplate).Should().Be("Appearance Mod(s) Have Template");
        Description(TemplateFilterType.AppModsUseAsTemplate).Should().Be("Appearance Mod(s) Use as Template");
    }

    [Fact]
    public void TemplateFilterType_RoundTripsThroughEnumParse()
    {
        foreach (var v in Enum.GetValues<TemplateFilterType>())
            Enum.Parse<TemplateFilterType>(v.ToString()).Should().Be(v);
    }

    // NOTE: SKIPPED — CheckSelectionState / CheckShareStatus / CheckTemplate / the FromPlugin
    // & InAppearanceMod predicates and the sort comparator all read instance state populated
    // from a live LinkCache / NpcConsistencyProvider / VM_NpcsMenuSelection graph, so they
    // are out of scope for this pure-unit file (covered by a separate integration wave).
}
