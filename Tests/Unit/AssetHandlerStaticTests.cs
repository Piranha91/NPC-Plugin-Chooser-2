using System.IO;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pure / deterministic static helpers on <see cref="AssetHandler"/>.
///
/// Covered:
///  - <see cref="AssetHandler.AddCorrespondingNumericalNifPaths"/> — body-double NIF pairing
///    ("foo_0.nif" &lt;-&gt; "foo_1.nif"), with edge cases around non-NIF / non-numeric paths,
///    the "_0.nifx" suffix boundary, idempotency, and the <c>addedRelPaths</c> out-set tracking.
///  - <see cref="AssetHandler.GetContainingSubdirectories"/> — first-level-only directory scan
///    against real temp dirs, plus the null-argument guards.
///  - <see cref="AssetHandler.ShouldSkipAsBaseGameOverwrite"/> — the base-game-overwrite
///    protection decision (opt-in flag, FaceGen exemptions by reason and by path, vanilla-set
///    membership with slash/case normalization).
///
/// All targets are <c>public static</c>, so they are invoked directly (no Reflect needed).
/// Neither requires a constructed AssetHandler, a live environment, nor a Skyrim install.
///
/// NOTE: every other helper on AssetHandler (FindAssetSource, RequestAssetCopyAsync,
/// CopyAssetFiles, ScheduleCopyNpcAssets, LoadAuxiliaryFiles, etc.) is an instance member that
/// depends on the heavy ctor (EnvironmentStateProvider / BsaHandler / RecordHandler /
/// SkyPatcherInterface / Lazy&lt;VM_Run&gt;) and on disk/BSA/environment state, so they are out of
/// scope for this pure-static suite and not covered here.
/// </summary>
public class AssetHandlerStaticTests
{
    // ----------------------------------------------------------------------
    // AddCorrespondingNumericalNifPaths
    // ----------------------------------------------------------------------

    [Fact]
    public void AddNumericalNif_ZeroVariant_AddsOneVariant()
    {
        var set = new HashSet<string> { @"meshes\armor\foo_0.nif" };
        var added = new HashSet<string>();

        AssetHandler.AddCorrespondingNumericalNifPaths(set, added);

        set.Should().BeEquivalentTo(new[]
        {
            @"meshes\armor\foo_0.nif",
            @"meshes\armor\foo_1.nif",
        });
        added.Should().BeEquivalentTo(new[] { @"meshes\armor\foo_1.nif" });
    }

    [Fact]
    public void AddNumericalNif_OneVariant_AddsZeroVariant()
    {
        var set = new HashSet<string> { @"meshes\armor\foo_1.nif" };
        var added = new HashSet<string>();

        AssetHandler.AddCorrespondingNumericalNifPaths(set, added);

        set.Should().BeEquivalentTo(new[]
        {
            @"meshes\armor\foo_0.nif",
            @"meshes\armor\foo_1.nif",
        });
        added.Should().BeEquivalentTo(new[] { @"meshes\armor\foo_0.nif" });
    }

    [Fact]
    public void AddNumericalNif_BothVariantsPresent_IsNoOpAndAddsNothing()
    {
        var set = new HashSet<string>
        {
            @"meshes\foo_0.nif",
            @"meshes\foo_1.nif",
        };
        var added = new HashSet<string>();

        AssetHandler.AddCorrespondingNumericalNifPaths(set, added);

        set.Should().HaveCount(2);
        set.Should().Contain(@"meshes\foo_0.nif").And.Contain(@"meshes\foo_1.nif");
        added.Should().BeEmpty("both counterparts already exist, so nothing new is introduced");
    }

    [Fact]
    public void AddNumericalNif_EmptySet_StaysEmpty()
    {
        var set = new HashSet<string>();
        var added = new HashSet<string>();

        AssetHandler.AddCorrespondingNumericalNifPaths(set, added);

        set.Should().BeEmpty();
        added.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"textures\foo_0.dds")]   // wrong extension
    [InlineData(@"meshes\foo.nif")]       // no numeric suffix
    [InlineData(@"meshes\foo_2.nif")]     // numeric but not 0/1
    [InlineData(@"meshes\foo_0.nifx")]    // "_0.nif" boundary: does NOT end with "_0.nif"
    [InlineData(@"meshes\foo_1.nifx")]    // "_1.nif" boundary: does NOT end with "_1.nif"
    [InlineData("_0.nif")]                // bare suffix is still "_0.nif" -> handled separately below
    public void AddNumericalNif_NonPairablePaths_AreUntouched(string path)
    {
        // This theory only asserts that paths which are NOT _0.nif/_1.nif suffixed are left alone.
        // The bare "_0.nif" case is intentionally excluded from the no-op assertion (handled below),
        // so guard it here.
        if (path.EndsWith("_0.nif") || path.EndsWith("_1.nif")) return;

        var set = new HashSet<string> { path };
        var added = new HashSet<string>();

        AssetHandler.AddCorrespondingNumericalNifPaths(set, added);

        set.Should().ContainSingle().Which.Should().Be(path);
        added.Should().BeEmpty();
    }

    [Fact]
    public void AddNumericalNif_NifxBoundary_DoesNotPair()
    {
        // "_0.nifx" must NOT be treated as a "_0.nif" path: EndsWith("_0.nif") is false.
        var set = new HashSet<string> { @"meshes\foo_0.nifx" };
        var added = new HashSet<string>();

        AssetHandler.AddCorrespondingNumericalNifPaths(set, added);

        set.Should().ContainSingle().Which.Should().Be(@"meshes\foo_0.nifx");
        added.Should().BeEmpty();
    }

    [Fact]
    public void AddNumericalNif_BareSuffix_StillPairs()
    {
        // A path that is literally just "_0.nif" still ends with "_0.nif" and pairs to "_1.nif".
        var set = new HashSet<string> { "_0.nif" };
        var added = new HashSet<string>();

        AssetHandler.AddCorrespondingNumericalNifPaths(set, added);

        set.Should().BeEquivalentTo(new[] { "_0.nif", "_1.nif" });
        added.Should().BeEquivalentTo(new[] { "_1.nif" });
    }

    [Fact]
    public void AddNumericalNif_OnlyReplacesTrailingSuffix_NotMidPathOccurrence()
    {
        // The "_0.nif" inside the directory portion must not be rewritten; only the trailing
        // suffix drives pairing, and Replace acts on the matched trailing token.
        var set = new HashSet<string> { @"meshes\foo_0.nif\bar_1.nif" };
        var added = new HashSet<string>();

        AssetHandler.AddCorrespondingNumericalNifPaths(set, added);

        // Path ends with "_1.nif" -> counterpart replaces the FIRST "_1.nif" occurrence per
        // string.Replace semantics. There is only one "_1.nif" here, so result is deterministic.
        set.Should().Contain(@"meshes\foo_0.nif\bar_1.nif");
        set.Should().Contain(@"meshes\foo_0.nif\bar_0.nif");
        added.Should().BeEquivalentTo(new[] { @"meshes\foo_0.nif\bar_0.nif" });
    }

    [Fact]
    public void AddNumericalNif_MixedSet_PairsEachIndependently()
    {
        var set = new HashSet<string>
        {
            @"meshes\a_0.nif",            // -> add a_1.nif
            @"meshes\b_1.nif",            // -> add b_0.nif
            @"meshes\c_1.nif",            // counterpart already present below -> no add
            @"meshes\c_0.nif",
            @"textures\skin.dds",         // untouched
            @"meshes\plain.nif",          // untouched
        };
        var added = new HashSet<string>();

        AssetHandler.AddCorrespondingNumericalNifPaths(set, added);

        set.Should().Contain(@"meshes\a_1.nif");
        set.Should().Contain(@"meshes\b_0.nif");
        // Untouched entries survive unchanged.
        set.Should().Contain(@"textures\skin.dds");
        set.Should().Contain(@"meshes\plain.nif");

        added.Should().BeEquivalentTo(new[]
        {
            @"meshes\a_1.nif",
            @"meshes\b_0.nif",
        });
    }

    [Fact]
    public void AddNumericalNif_DoesNotRePairTheJustAddedCounterpart()
    {
        // Iteration is over a snapshot taken at entry (ToList()), so the newly-added "_1.nif"
        // is NOT itself re-examined to add "_0.nif" again. Net effect: exactly one new entry.
        var set = new HashSet<string> { @"meshes\foo_0.nif" };
        var added = new HashSet<string>();

        AssetHandler.AddCorrespondingNumericalNifPaths(set, added);

        set.Should().HaveCount(2);
        added.Should().ContainSingle();
    }

    [Fact]
    public void AddNumericalNif_Idempotent_SecondCallAddsNothing()
    {
        var set = new HashSet<string> { @"meshes\foo_0.nif" };
        var added1 = new HashSet<string>();
        AssetHandler.AddCorrespondingNumericalNifPaths(set, added1);

        var added2 = new HashSet<string>();
        AssetHandler.AddCorrespondingNumericalNifPaths(set, added2);

        set.Should().HaveCount(2);
        added2.Should().BeEmpty("the second pass finds both counterparts already present");
    }

    [Fact]
    public void AddNumericalNif_PreExistingAddedSet_IsAppendedNotCleared()
    {
        // The out-set is added to, never reset; a caller's pre-seeded entries are preserved.
        var set = new HashSet<string> { @"meshes\foo_0.nif" };
        var added = new HashSet<string> { "preexisting-token" };

        AssetHandler.AddCorrespondingNumericalNifPaths(set, added);

        added.Should().BeEquivalentTo(new[] { "preexisting-token", @"meshes\foo_1.nif" });
    }

    // ----------------------------------------------------------------------
    // GetContainingSubdirectories
    // ----------------------------------------------------------------------

    [Fact]
    public void GetContainingSubdirectories_ReturnsOnlyFirstLevelDirsThatHoldTheFile()
    {
        using var tmp = new TempDir();

        // root/A/Textures/Foo.png      -> match (immediate child A holds the relative file)
        // root/B/Textures/Foo.png      -> match
        // root/C/Textures/Other.png    -> no match (different file)
        tmp.WriteText(Path.Combine("A", "Textures", "Foo.png"), "a");
        tmp.WriteText(Path.Combine("B", "Textures", "Foo.png"), "b");
        tmp.WriteText(Path.Combine("C", "Textures", "Other.png"), "c");

        var result = AssetHandler.GetContainingSubdirectories(tmp.Path, Path.Combine("Textures", "Foo.png"));

        result.Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "A", "B" });
    }

    [Fact]
    public void GetContainingSubdirectories_DoesNotRecurseIntoNestedSubdirectories()
    {
        using var tmp = new TempDir();

        // The relative file lives two levels deep under "Outer\Inner". Only "Outer" is an
        // immediate child of root, and "Outer" itself does NOT directly contain the relative
        // file (it lives under Outer\Inner\...), so nothing should match.
        tmp.WriteText(Path.Combine("Outer", "Inner", "Textures", "Foo.png"), "x");

        var result = AssetHandler.GetContainingSubdirectories(tmp.Path, Path.Combine("Textures", "Foo.png"));

        result.Should().BeEmpty("only the first level of sub-directories is inspected");
    }

    [Fact]
    public void GetContainingSubdirectories_FileDirectlyInImmediateChild_Matches()
    {
        using var tmp = new TempDir();

        // root/ModA/Foo.txt  with relativeFilePath = "Foo.txt"
        tmp.WriteText(Path.Combine("ModA", "Foo.txt"), "1");
        tmp.Dir("ModB"); // empty immediate child -> no match

        var result = AssetHandler.GetContainingSubdirectories(tmp.Path, "Foo.txt");

        result.Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "ModA" });
    }

    [Fact]
    public void GetContainingSubdirectories_NoSubdirectories_ReturnsEmpty()
    {
        using var tmp = new TempDir();
        // A file directly in root is irrelevant: only sub-directories are enumerated.
        tmp.WriteText("Foo.txt", "1");

        var result = AssetHandler.GetContainingSubdirectories(tmp.Path, "Foo.txt");

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetContainingSubdirectories_NullRootDir_Throws()
    {
        var act = () => AssetHandler.GetContainingSubdirectories(null!, "Foo.txt");
        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("rootDir");
    }

    [Fact]
    public void GetContainingSubdirectories_NullRelativeFilePath_Throws()
    {
        using var tmp = new TempDir();
        var act = () => AssetHandler.GetContainingSubdirectories(tmp.Path, null!);
        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("relativeFilePath");
    }

    [Fact]
    public void GetContainingSubdirectories_BothArgsNull_ThrowsForRootDirFirst()
    {
        // rootDir is validated before relativeFilePath, so its ParamName surfaces.
        var act = () => AssetHandler.GetContainingSubdirectories(null!, null!);
        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("rootDir");
    }

    // ----------------------------------------------------------------------
    // ShouldSkipAsBaseGameOverwrite (base-game-overwrite protection)
    // ----------------------------------------------------------------------

    private static readonly IReadOnlySet<string> VanillaSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        @"textures\actors\character\female\femalebody_1.dds",
        @"meshes\actors\character\character assets\femalebody_1.nif",
        // Vanilla NPC FaceGen ships in the vanilla BSAs too — the exemption below must win.
        @"meshes\actors\character\facegendata\facegeom\Skyrim.esm\0001A696.nif",
        @"textures\actors\character\facegendata\facetint\Skyrim.esm\0001A696.dds",
    };

    [Fact]
    public void ShouldSkip_VanillaPath_NoOptIn_Skips()
    {
        AssetHandler.ShouldSkipAsBaseGameOverwrite(
                @"textures\actors\character\female\femalebody_1.dds", "PluginRef",
                overwriteBaseGameAssets: false, VanillaSet)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldSkip_VanillaPath_OptedIn_Copies()
    {
        AssetHandler.ShouldSkipAsBaseGameOverwrite(
                @"textures\actors\character\female\femalebody_1.dds", "PluginRef",
                overwriteBaseGameAssets: true, VanillaSet)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("NifTexture")]
    [InlineData("SmpXml")]
    [InlineData("AssetLink")]
    [InlineData("PluginRef")]
    public void ShouldSkip_AppliesToAllNonFaceGenReasons(string reason)
    {
        AssetHandler.ShouldSkipAsBaseGameOverwrite(
                @"meshes\actors\character\character assets\femalebody_1.nif", reason,
                overwriteBaseGameAssets: false, VanillaSet)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldSkip_FaceGenReason_NeverSkips()
    {
        // A vanilla NPC's own FaceGen collides with the vanilla set by construction, but
        // overwriting it is the entire point of the app.
        AssetHandler.ShouldSkipAsBaseGameOverwrite(
                @"meshes\actors\character\facegendata\facegeom\Skyrim.esm\0001A696.nif", "FaceGen",
                overwriteBaseGameAssets: false, VanillaSet)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldSkip_FaceGenPath_NeverSkips_RegardlessOfReason()
    {
        // Belt-and-suspenders: even under a non-FaceGen reason, a facegendata path is exempt.
        AssetHandler.ShouldSkipAsBaseGameOverwrite(
                @"textures\actors\character\facegendata\facetint\Skyrim.esm\0001A696.dds", "NifTexture",
                overwriteBaseGameAssets: false, VanillaSet)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldSkip_NonVanillaPath_Copies()
    {
        AssetHandler.ShouldSkipAsBaseGameOverwrite(
                @"textures\actors\character\KSHairdos\hair01.dds", "PluginRef",
                overwriteBaseGameAssets: false, VanillaSet)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("textures/actors/character/female/femalebody_1.dds")] // forward slashes
    [InlineData(@"TEXTURES\ACTORS\CHARACTER\FEMALE\FEMALEBODY_1.DDS")] // case
    public void ShouldSkip_NormalizesSlashesAndCase(string destRelPath)
    {
        AssetHandler.ShouldSkipAsBaseGameOverwrite(destRelPath, "PluginRef",
                overwriteBaseGameAssets: false, VanillaSet)
            .Should().BeTrue();
    }
}
