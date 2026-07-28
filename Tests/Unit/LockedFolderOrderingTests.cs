using System.Collections.Generic;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Position preservation for locked mod folders across a Refresh
/// (<see cref="LockedFolderOrdering.Reconcile"/>).
///
/// <para>A Refresh resets a mod's folder list to its primary folder and lets the master-chain detector
/// re-add what it can find, inserting each discovery at index 0. Folders with no plugin are never
/// re-found, so locked folders have to be re-inserted by anchoring on surviving neighbours rather than
/// by remembered index. These tests pin that anchoring behaviour, including the cases where anchors
/// themselves disappear.</para>
/// </summary>
public class LockedFolderOrderingTests
{
    private const string Primary = @"C:\Mods\Bijin AIO";
    private const string SilentA = @"C:\Mods\Bijin Textures";
    private const string SilentB = @"C:\Mods\Bijin Meshes";
    private const string Detected = @"C:\Mods\Song of the Green";
    private const string Detected2 = @"C:\Mods\Interesting NPCs";

    [Fact]
    public void LockedFolderAfterPrimaryStaysAfterIt()
    {
        var result = LockedFolderOrdering.Reconcile(
            rebuilt: new[] { Primary },
            originalOrder: new[] { Primary, SilentA },
            lockedPaths: new[] { SilentA });

        result.Should().Equal(Primary, SilentA);
    }

    [Fact]
    public void LockedFolderBeforePrimaryStaysBeforeIt()
    {
        var result = LockedFolderOrdering.Reconcile(
            rebuilt: new[] { Primary },
            originalOrder: new[] { SilentA, Primary },
            lockedPaths: new[] { SilentA });

        result.Should().Equal(SilentA, Primary);
    }

    [Fact]
    public void LockedFolderIsInterleavedBetweenSurvivingNeighbours()
    {
        // Detector re-found both plugin-bearing folders and put them at the front, as it always does.
        var result = LockedFolderOrdering.Reconcile(
            rebuilt: new[] { Detected2, Detected, Primary },
            originalOrder: new[] { Detected, SilentA, Detected2, Primary },
            lockedPaths: new[] { SilentA });

        // SilentA anchors on Detected, wherever the rebuild left Detected.
        result.Should().Equal(Detected2, Detected, SilentA, Primary);
    }

    [Fact]
    public void ConsecutiveLockedFoldersKeepTheirRelativeOrder()
    {
        var result = LockedFolderOrdering.Reconcile(
            rebuilt: new[] { Primary },
            originalOrder: new[] { Primary, SilentA, SilentB },
            lockedPaths: new[] { SilentA, SilentB });

        result.Should().Equal(Primary, SilentA, SilentB);
    }

    [Fact]
    public void LockedFoldersWhoseAnchorsAllVanishedLeadTheListInOriginalOrder()
    {
        // Detected was dropped by the rebuild, so neither locked folder has a surviving predecessor.
        var result = LockedFolderOrdering.Reconcile(
            rebuilt: new[] { Primary },
            originalOrder: new[] { Detected, SilentA, SilentB, Primary },
            lockedPaths: new[] { SilentA, SilentB });

        result.Should().Equal(SilentA, SilentB, Primary);
    }

    [Fact]
    public void LockedFolderFallsBackToAnEarlierAnchorWhenItsImmediatePredecessorVanished()
    {
        var result = LockedFolderOrdering.Reconcile(
            rebuilt: new[] { Primary },
            originalOrder: new[] { Primary, Detected, SilentA },
            lockedPaths: new[] { SilentA });

        // Detected is gone; SilentA falls back to Primary rather than jumping to the front.
        result.Should().Equal(Primary, SilentA);
    }

    [Fact]
    public void RediscoveredLockedFolderIsRepositionedRatherThanDuplicated()
    {
        // The detector happened to re-find the locked folder and inserted it at the front.
        var result = LockedFolderOrdering.Reconcile(
            rebuilt: new[] { SilentA, Primary },
            originalOrder: new[] { Primary, SilentA },
            lockedPaths: new[] { SilentA });

        result.Should().Equal(Primary, SilentA);
    }

    [Fact]
    public void NewlyDetectedFoldersAreLeftWhereTheRebuildPutThem()
    {
        var result = LockedFolderOrdering.Reconcile(
            rebuilt: new[] { Detected, Primary },
            originalOrder: new[] { Primary, SilentA },
            lockedPaths: new[] { SilentA });

        result.Should().Equal(Detected, Primary, SilentA);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        // Paths reach the VM from folder enumeration, dialogs and persisted settings, which do not
        // agree on casing. The locked path is matched regardless, and the entry re-inserted is the one
        // from the pre-refresh list, so the casing the user was shown is what survives.
        var result = LockedFolderOrdering.Reconcile(
            rebuilt: new[] { Primary.ToUpperInvariant() },
            originalOrder: new[] { Primary.ToUpperInvariant(), SilentA },
            lockedPaths: new[] { SilentA.ToLowerInvariant() });

        result.Should().Equal(Primary.ToUpperInvariant(), SilentA);
    }

    [Fact]
    public void LockedFolderMissingFromTheOriginalOrderIsAppendedRatherThanDropped()
    {
        var result = LockedFolderOrdering.Reconcile(
            rebuilt: new[] { Primary },
            originalOrder: new[] { Primary },
            lockedPaths: new[] { SilentA });

        result.Should().Equal(Primary, SilentA);
    }

    [Fact]
    public void NoLocksLeavesTheRebuiltListUntouched()
    {
        var result = LockedFolderOrdering.Reconcile(
            rebuilt: new[] { Detected, Primary },
            originalOrder: new[] { Primary, SilentA },
            lockedPaths: new List<string>());

        result.Should().Equal(Detected, Primary);
    }
}
