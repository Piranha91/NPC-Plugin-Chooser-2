using System.IO;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// Deterministic file-tree helpers on <see cref="UpdateHandler"/> exercised against real
/// temp directories. All three targets are <c>private static</c>, so they are reached via
/// <see cref="Reflect.InvokeStatic{TOwner,T}"/> / <see cref="Reflect.InvokeVoid"/>-equivalents.
///
/// Surface under test (exact signatures copied from UpdateHandler.cs):
///   - private static bool IsFolderTreeEmptyOfFiles(string path)
///   - private static void DeleteEmptyDirectoryTree(string dir)
///   - private static (int moved, int skipped, int failed) MoveTrackedFiles(
///         HashSet&lt;string&gt; trackerSet, string normalizedOldRoot, string newRoot,
///         HashSet&lt;string&gt; touchedTopLevelDirs, List&lt;string&gt; failureLines,
///         string bucketLabel, string? sidecarSuffix)
///
/// MoveTrackedFiles treats <c>normalizedOldRoot</c> as already canonicalised (the production
/// caller passes <see cref="Auxilliary.NormalizeFolderForCompare"/>); <c>newRoot</c> is the
/// raw destination (it is normalized internally only for the same-root short-circuit). Tests
/// mirror that contract by normalizing the old root and the tracked entries with the same
/// helper the production code uses, so the slice math in MoveTrackedFiles stays valid.
/// </summary>
public class UpdateHandlerFileOpsTests
{
    // ---- helpers -----------------------------------------------------------

    private static bool IsEmpty(string path) =>
        Reflect.InvokeStatic<UpdateHandler, bool>("IsFolderTreeEmptyOfFiles", path);

    private static void DeleteTree(string dir) =>
        Reflect.InvokeStatic<UpdateHandler, object?>("DeleteEmptyDirectoryTree", dir);

    private static (int moved, int skipped, int failed) Move(
        HashSet<string> trackerSet,
        string normalizedOldRoot,
        string newRoot,
        HashSet<string> touchedTopLevelDirs,
        List<string> failureLines,
        string bucketLabel = "Test",
        string? sidecarSuffix = null)
    {
        var result = Reflect.InvokeStatic<UpdateHandler, ValueTuple<int, int, int>>(
            "MoveTrackedFiles",
            trackerSet, normalizedOldRoot, newRoot, touchedTopLevelDirs,
            failureLines, bucketLabel, sidecarSuffix);
        return (result.Item1, result.Item2, result.Item3);
    }

    private static string Norm(string? p) => Auxilliary.NormalizeFolderForCompare(p);

    // =======================================================================
    //  IsFolderTreeEmptyOfFiles
    // =======================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsFolderTreeEmptyOfFiles_NullOrWhitespace_True(string? path)
    {
        IsEmpty(path!).Should().BeTrue();
    }

    [Fact]
    public void IsFolderTreeEmptyOfFiles_MissingDirectory_True()
    {
        using var tmp = new TempDir();
        var missing = Path.Combine(tmp.Path, "does-not-exist");
        IsEmpty(missing).Should().BeTrue();
    }

    [Fact]
    public void IsFolderTreeEmptyOfFiles_EmptyDirectory_True()
    {
        using var tmp = new TempDir();
        var empty = tmp.Dir("empty");
        IsEmpty(empty).Should().BeTrue();
    }

    [Fact]
    public void IsFolderTreeEmptyOfFiles_OnlyEmptySubdirectories_True()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("root");
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        Directory.CreateDirectory(Path.Combine(root, "c"));
        IsEmpty(root).Should().BeTrue("subdirectories with no files at any depth still count as empty");
    }

    [Fact]
    public void IsFolderTreeEmptyOfFiles_FileAtTopLevel_False()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("root");
        File.WriteAllText(Path.Combine(root, "x.txt"), "hi");
        IsEmpty(root).Should().BeFalse();
    }

    [Fact]
    public void IsFolderTreeEmptyOfFiles_FileDeepInTree_False()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("root");
        var deep = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "buried.png"), "data");
        IsEmpty(root).Should().BeFalse("a file at any depth makes the tree non-empty");
    }

    // =======================================================================
    //  DeleteEmptyDirectoryTree
    // =======================================================================

    [Fact]
    public void DeleteEmptyDirectoryTree_EmptyLeaf_Deleted()
    {
        using var tmp = new TempDir();
        var leaf = tmp.Dir("leaf");
        DeleteTree(leaf);
        Directory.Exists(leaf).Should().BeFalse();
    }

    [Fact]
    public void DeleteEmptyDirectoryTree_NestedEmpty_WholeTreeDeleted()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("root");
        Directory.CreateDirectory(Path.Combine(root, "a", "b", "c"));
        Directory.CreateDirectory(Path.Combine(root, "d"));

        DeleteTree(root);

        Directory.Exists(root).Should().BeFalse("an all-empty tree collapses bottom-up including the root");
    }

    [Fact]
    public void DeleteEmptyDirectoryTree_DirectoryWithFile_Kept()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("root");
        File.WriteAllText(Path.Combine(root, "keep.txt"), "content");

        DeleteTree(root);

        Directory.Exists(root).Should().BeTrue("a directory holding a file must survive the prune");
        File.Exists(Path.Combine(root, "keep.txt")).Should().BeTrue();
    }

    [Fact]
    public void DeleteEmptyDirectoryTree_PrunesEmptyButKeepsFileBearingBranch()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("root");

        // Empty branch -> should disappear.
        Directory.CreateDirectory(Path.Combine(root, "emptyBranch", "deeper"));
        // File-bearing branch -> root, fileBranch, and the file must remain.
        var fileBranch = Path.Combine(root, "fileBranch");
        Directory.CreateDirectory(fileBranch);
        File.WriteAllText(Path.Combine(fileBranch, "a.png"), "x");

        DeleteTree(root);

        Directory.Exists(root).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "emptyBranch")).Should().BeFalse("empty sub-branch is pruned");
        Directory.Exists(fileBranch).Should().BeTrue();
        File.Exists(Path.Combine(fileBranch, "a.png")).Should().BeTrue();
    }

    [Fact]
    public void DeleteEmptyDirectoryTree_MissingDirectory_NoOp()
    {
        using var tmp = new TempDir();
        var missing = Path.Combine(tmp.Path, "ghost");
        // Should not throw and should not create anything.
        var act = () => DeleteTree(missing);
        act.Should().NotThrow();
        Directory.Exists(missing).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeleteEmptyDirectoryTree_NullOrWhitespace_NoOp(string? dir)
    {
        var act = () => DeleteTree(dir!);
        act.Should().NotThrow();
    }

    // =======================================================================
    //  MoveTrackedFiles
    // =======================================================================

    [Fact]
    public void MoveTrackedFiles_EmptySet_ReturnsAllZero()
    {
        using var tmp = new TempDir();
        var tracker = new HashSet<string>();
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        var (moved, skipped, failed) = Move(
            tracker, Norm(tmp.Combine("old")), tmp.Combine("new"), touched, failures);

        moved.Should().Be(0);
        skipped.Should().Be(0);
        failed.Should().Be(0);
        tracker.Should().BeEmpty();
        touched.Should().BeEmpty();
        failures.Should().BeEmpty();
    }

    [Fact]
    public void MoveTrackedFiles_NewRootEqualsOldRoot_SkipsAllWithoutMoving()
    {
        using var tmp = new TempDir();
        var oldRoot = tmp.Dir("shared");
        var file = Path.Combine(oldRoot, "img.png");
        File.WriteAllText(file, "x");

        var tracker = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file };
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        // newRoot points at the same folder as oldRoot -> short-circuit: (0, count, 0).
        var (moved, skipped, failed) = Move(tracker, Norm(oldRoot), oldRoot, touched, failures);

        moved.Should().Be(0);
        skipped.Should().Be(1, "same-root means nothing to move, every entry is skipped");
        failed.Should().Be(0);
        File.Exists(file).Should().BeTrue("the file is untouched on a same-root short-circuit");
        tracker.Should().ContainSingle().Which.Should().Be(file, "the tracker is left intact on short-circuit");
        touched.Should().BeEmpty();
    }

    [Fact]
    public void MoveTrackedFiles_EmptyOldRoot_SkipsAll()
    {
        using var tmp = new TempDir();
        var tracker = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            tmp.WriteText("old/a.png", "x"),
            tmp.WriteText("old/b.png", "y"),
        };
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        // Empty normalized old root -> (0, count, 0).
        var (moved, skipped, failed) = Move(tracker, string.Empty, tmp.Combine("new"), touched, failures);

        moved.Should().Be(0);
        skipped.Should().Be(2);
        failed.Should().Be(0);
        tracker.Should().HaveCount(2, "tracker is untouched when there is no old root to migrate from");
    }

    [Fact]
    public void MoveTrackedFiles_SingleFile_RebasedAndTrackerSwapped()
    {
        using var tmp = new TempDir();
        var oldRoot = tmp.Dir("old");
        var newRoot = tmp.Combine("new");

        // File lives under <old>/ModName/sub/face.png so the sub-path is preserved on rebase.
        var src = Path.Combine(oldRoot, "ModName", "sub", "face.png");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "pixels");

        var tracker = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { src };
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        var (moved, skipped, failed) = Move(tracker, Norm(oldRoot), newRoot, touched, failures);

        moved.Should().Be(1);
        skipped.Should().Be(0);
        failed.Should().Be(0);

        var expectedDest = Path.Combine(newRoot, "ModName", "sub", "face.png");
        File.Exists(src).Should().BeFalse("the source is moved, not copied");
        File.Exists(expectedDest).Should().BeTrue("sub-path under the old root is preserved under the new root");
        File.ReadAllText(expectedDest).Should().Be("pixels");

        // Tracker swap: old path out, new (rebased) path in.
        tracker.Should().NotContain(src);
        tracker.Should().ContainSingle();
        Norm(tracker.Single()).Should().Be(Norm(expectedDest));

        // Top-level subdir under the old root recorded for the caller's bottom-up prune.
        touched.Should().ContainSingle();
        Norm(touched.Single()).Should().Be(Norm(Path.Combine(oldRoot, "ModName")));
        failures.Should().BeEmpty();
    }

    [Fact]
    public void MoveTrackedFiles_MovesSidecarAlongsideImage()
    {
        using var tmp = new TempDir();
        var oldRoot = tmp.Dir("old");
        var newRoot = tmp.Combine("new");

        var src = Path.Combine(oldRoot, "Mod", "img.webp");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "img");
        const string sidecarSuffix = ".ffmeta.json";
        File.WriteAllText(src + sidecarSuffix, "{}");

        var tracker = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { src };
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        var (moved, skipped, failed) = Move(
            tracker, Norm(oldRoot), newRoot, touched, failures,
            bucketLabel: "FaceFinder", sidecarSuffix: sidecarSuffix);

        moved.Should().Be(1);
        skipped.Should().Be(0);
        failed.Should().Be(0);

        var expectedDest = Path.Combine(newRoot, "Mod", "img.webp");
        File.Exists(expectedDest).Should().BeTrue();
        File.Exists(expectedDest + sidecarSuffix).Should().BeTrue("the sidecar follows the image");
        File.Exists(src + sidecarSuffix).Should().BeFalse("the old sidecar is moved, not left behind");
        failures.Should().BeEmpty();
    }

    [Fact]
    public void MoveTrackedFiles_MissingSidecar_StillMovesImageWithoutFailure()
    {
        using var tmp = new TempDir();
        var oldRoot = tmp.Dir("old");
        var newRoot = tmp.Combine("new");

        var src = Path.Combine(oldRoot, "Mod", "img.webp");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "img"); // no sidecar written

        var tracker = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { src };
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        var (moved, skipped, failed) = Move(
            tracker, Norm(oldRoot), newRoot, touched, failures,
            bucketLabel: "FaceFinder", sidecarSuffix: ".ffmeta.json");

        moved.Should().Be(1);
        failed.Should().Be(0);
        failures.Should().BeEmpty("an absent sidecar is not a failure");
        File.Exists(Path.Combine(newRoot, "Mod", "img.webp")).Should().BeTrue();
    }

    [Fact]
    public void MoveTrackedFiles_PhantomEntry_SkippedAndDroppedFromTracker()
    {
        using var tmp = new TempDir();
        var oldRoot = tmp.Dir("old");
        var newRoot = tmp.Combine("new");

        // Path is under the old root but no file exists on disk.
        var phantom = Path.Combine(oldRoot, "gone", "missing.png");

        var tracker = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { phantom };
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        var (moved, skipped, failed) = Move(tracker, Norm(oldRoot), newRoot, touched, failures);

        moved.Should().Be(0);
        skipped.Should().Be(1);
        failed.Should().Be(0);
        tracker.Should().BeEmpty("a stale tracker entry pointing at a non-existent file is dropped");
        touched.Should().BeEmpty();
    }

    [Fact]
    public void MoveTrackedFiles_WhitespaceEntry_SkippedAndDropped()
    {
        using var tmp = new TempDir();
        var oldRoot = tmp.Dir("old");

        var tracker = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "   " };
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        var (moved, skipped, failed) = Move(tracker, Norm(oldRoot), tmp.Combine("new"), touched, failures);

        moved.Should().Be(0);
        skipped.Should().Be(1);
        failed.Should().Be(0);
        tracker.Should().BeEmpty("a blank tracker entry is removed and skipped");
    }

    [Fact]
    public void MoveTrackedFiles_OutsideRoot_SkippedButRetainedInTracker()
    {
        using var tmp = new TempDir();
        var oldRoot = tmp.Dir("old");
        var newRoot = tmp.Combine("new");

        // A real file that lives OUTSIDE the old root (already migrated elsewhere).
        var outside = tmp.WriteText("elsewhere/already.png", "x");

        var tracker = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { outside };
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        var (moved, skipped, failed) = Move(tracker, Norm(oldRoot), newRoot, touched, failures);

        moved.Should().Be(0);
        skipped.Should().Be(1);
        failed.Should().Be(0);
        File.Exists(outside).Should().BeTrue("entries outside the old root are left alone");
        tracker.Should().ContainSingle().Which.Should().Be(outside,
            "an entry already outside the old root is retained, not dropped");
    }

    [Fact]
    public void MoveTrackedFiles_DestinationOccupied_CountedAsFailure()
    {
        using var tmp = new TempDir();
        var oldRoot = tmp.Dir("old");
        var newRoot = tmp.Combine("new");

        var src = Path.Combine(oldRoot, "Mod", "img.png");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "source");

        // Pre-occupy the exact destination so File.Move(overwrite:false) throws.
        var dest = Path.Combine(newRoot, "Mod", "img.png");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "already here");

        var tracker = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { src };
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        var (moved, skipped, failed) = Move(
            tracker, Norm(oldRoot), newRoot, touched, failures, bucketLabel: "AutoGen");

        moved.Should().Be(0);
        skipped.Should().Be(0);
        failed.Should().Be(1, "an occupied destination is an IO failure, not a skip");
        File.Exists(src).Should().BeTrue("a failed move leaves the source in place");
        File.ReadAllText(dest).Should().Be("already here", "the occupied destination is never overwritten");
        tracker.Should().ContainSingle().Which.Should().Be(src,
            "a failed move keeps the original tracker entry");
        failures.Should().ContainSingle();
        failures.Single().Should().Contain("[AutoGen]").And.Contain(src);
        touched.Should().BeEmpty();
    }

    [Fact]
    public void MoveTrackedFiles_MixedBatch_PartitionsCountsCorrectly()
    {
        using var tmp = new TempDir();
        var oldRoot = tmp.Dir("old");
        var newRoot = tmp.Combine("new");

        // 1) movable
        var movable = Path.Combine(oldRoot, "M", "good.png");
        Directory.CreateDirectory(Path.GetDirectoryName(movable)!);
        File.WriteAllText(movable, "g");

        // 2) phantom (under root, not on disk)
        var phantom = Path.Combine(oldRoot, "P", "ghost.png");

        // 3) outside root
        var outside = tmp.WriteText("outside/o.png", "o");

        // 4) destination occupied -> failure
        var occupiedSrc = Path.Combine(oldRoot, "C", "clash.png");
        Directory.CreateDirectory(Path.GetDirectoryName(occupiedSrc)!);
        File.WriteAllText(occupiedSrc, "s");
        var occupiedDest = Path.Combine(newRoot, "C", "clash.png");
        Directory.CreateDirectory(Path.GetDirectoryName(occupiedDest)!);
        File.WriteAllText(occupiedDest, "blocked");

        var tracker = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            movable, phantom, outside, occupiedSrc,
        };
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        var (moved, skipped, failed) = Move(tracker, Norm(oldRoot), newRoot, touched, failures);

        moved.Should().Be(1, "only the clean entry moves");
        skipped.Should().Be(2, "phantom + outside-root are both skipped");
        failed.Should().Be(1, "the occupied destination fails");

        File.Exists(Path.Combine(newRoot, "M", "good.png")).Should().BeTrue();
        tracker.Should().Contain(outside, "outside-root entry retained");
        tracker.Should().Contain(occupiedSrc, "failed move retains its entry");
        tracker.Should().NotContain(phantom, "phantom dropped");
        tracker.Should().NotContain(movable, "moved entry swapped out for its new path");
    }
}
