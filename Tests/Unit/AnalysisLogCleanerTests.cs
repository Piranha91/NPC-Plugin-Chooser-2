using System;
using System.IO;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pins the cleanup contract for the two per-mod analysis log folders next to the .exe. These logs
/// now back the Settings &gt; Mod Import Settings &gt; Rejected NPCs tree, so a file left behind is
/// not just clutter — it shows up as a mod the user no longer has, with rejections that no longer
/// happen. The rules under test: a full clear takes both folders, a per-mod clear takes exactly one
/// mod's three files, and neither is allowed to throw at its caller (a scan) when the folder is
/// missing or the name is unusable.
/// </summary>
public class AnalysisLogCleanerTests
{
    private const string Rejected = AnalysisLogCleaner.RejectedNpcsFolderName;
    private const string Errors = AnalysisLogCleaner.LoadingErrorsFolderName;

    [Fact]
    public void ClearAllRemovesLogsFromBothFolders()
    {
        using var temp = new TempDir(nameof(ClearAllRemovesLogsFromBothFolders));
        var rejected = temp.WriteText(Path.Combine(Rejected, "Some Mod.txt"), "Discarded ...");
        var errors = temp.WriteText(Path.Combine(Errors, "Some Mod.txt"), "boom");
        var injection = temp.WriteText(Path.Combine(Errors, "Some Mod_InjectionCheck.txt"), "boom");

        AnalysisLogCleaner.ClearAll(temp.Path);

        File.Exists(rejected).Should().BeFalse();
        File.Exists(errors).Should().BeFalse();
        File.Exists(injection).Should().BeFalse();
    }

    /// <summary>
    /// The folders themselves survive, because the Settings panel offers an "open folder" button and
    /// an Explorer window the user already opened from it must not be invalidated by a re-scan.
    /// </summary>
    [Fact]
    public void ClearAllKeepsTheFoldersThemselves()
    {
        using var temp = new TempDir(nameof(ClearAllKeepsTheFoldersThemselves));
        temp.WriteText(Path.Combine(Rejected, "Some Mod.txt"), "Discarded ...");
        temp.Dir(Errors);

        AnalysisLogCleaner.ClearAll(temp.Path);

        Directory.Exists(Path.Combine(temp.Path, Rejected)).Should().BeTrue();
        Directory.Exists(Path.Combine(temp.Path, Errors)).Should().BeTrue();
    }

    /// <summary>Only the logs are app-owned; anything else parked in the folder is the user's.</summary>
    [Fact]
    public void ClearAllLeavesNonTxtFilesAlone()
    {
        using var temp = new TempDir(nameof(ClearAllLeavesNonTxtFilesAlone));
        var log = temp.WriteText(Path.Combine(Rejected, "Some Mod.txt"), "Discarded ...");
        var keep = temp.WriteText(Path.Combine(Rejected, "notes.csv"), "mine");

        AnalysisLogCleaner.ClearAll(temp.Path);

        File.Exists(log).Should().BeFalse();
        File.Exists(keep).Should().BeTrue();
    }

    [Fact]
    public void ClearAllOnMissingFoldersDoesNotThrow()
    {
        using var temp = new TempDir(nameof(ClearAllOnMissingFoldersDoesNotThrow));

        var act = () => AnalysisLogCleaner.ClearAll(temp.Path);

        act.Should().NotThrow();
    }

    [Fact]
    public void ClearForModRemovesAllThreeOfThatModsLogs()
    {
        using var temp = new TempDir(nameof(ClearForModRemovesAllThreeOfThatModsLogs));
        var rejected = temp.WriteText(Path.Combine(Rejected, "Lawless.txt"), "Discarded ...");
        var errors = temp.WriteText(Path.Combine(Errors, "Lawless.txt"), "boom");
        var injection = temp.WriteText(Path.Combine(Errors, "Lawless_InjectionCheck.txt"), "boom");

        AnalysisLogCleaner.ClearForMod("Lawless", temp.Path);

        File.Exists(rejected).Should().BeFalse();
        File.Exists(errors).Should().BeFalse();
        File.Exists(injection).Should().BeFalse();
    }

    [Fact]
    public void ClearForModLeavesOtherModsAlone()
    {
        using var temp = new TempDir(nameof(ClearForModLeavesOtherModsAlone));
        var target = temp.WriteText(Path.Combine(Rejected, "Lawless.txt"), "Discarded ...");
        var other = temp.WriteText(Path.Combine(Rejected, "Bijin.txt"), "Discarded ...");

        AnalysisLogCleaner.ClearForMod("Lawless", temp.Path);

        File.Exists(target).Should().BeFalse();
        File.Exists(other).Should().BeTrue();
    }

    /// <summary>
    /// The writers name files with Auxilliary.MakeStringPathSafe, so the cleaner has to apply the
    /// same transform or a mod whose display name contains a path-invalid character (colons are
    /// common in mod titles) would never have its log found.
    /// </summary>
    [Fact]
    public void ClearForModMatchesThePathSafeFormOfTheDisplayName()
    {
        using var temp = new TempDir(nameof(ClearForModMatchesThePathSafeFormOfTheDisplayName));
        const string displayName = "Bandits: A Overhaul";
        var safeName = Auxilliary.MakeStringPathSafe(displayName);
        safeName.Should().NotBe(displayName, "the test is meaningless if the name needs no sanitizing");

        var log = temp.WriteText(Path.Combine(Rejected, safeName + ".txt"), "Discarded ...");

        AnalysisLogCleaner.ClearForMod(displayName, temp.Path);

        File.Exists(log).Should().BeFalse();
    }

    [Fact]
    public void ClearForModOnMissingFilesDoesNotThrow()
    {
        using var temp = new TempDir(nameof(ClearForModOnMissingFilesDoesNotThrow));

        var act = () => AnalysisLogCleaner.ClearForMod("Never Scanned", temp.Path);

        act.Should().NotThrow();
    }

    /// <summary>
    /// A blank display name has no path-safe form, and the writers decline to create a file in that
    /// case. The cleaner must agree rather than deriving some "" path and clearing a folder root.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ClearForModWithNoUsableNameTargetsNothing(string? displayName)
    {
        using var temp = new TempDir(nameof(ClearForModWithNoUsableNameTargetsNothing));
        var bystander = temp.WriteText(Path.Combine(Rejected, "Some Mod.txt"), "Discarded ...");

        AnalysisLogCleaner.GetLogPathsForMod(displayName!, temp.Path).Should().BeEmpty();

        var act = () => AnalysisLogCleaner.ClearForMod(displayName!, temp.Path);
        act.Should().NotThrow();
        File.Exists(bystander).Should().BeTrue();
    }

    /// <summary>
    /// The regression this whole change exists for: RefreshNpcLists used to write only when it had
    /// something to say, so a mod whose rejections were all resolved kept the previous run's file.
    /// </summary>
    [Fact]
    public void WriteOrClearRejectionLogDeletesTheLogWhenNothingWasRejected()
    {
        using var temp = new TempDir(nameof(WriteOrClearRejectionLogDeletesTheLogWhenNothingWasRejected));
        var log = temp.WriteText(Path.Combine(Rejected, "Lawless.txt"), "Discarded someone ...");

        AnalysisLogCleaner.WriteOrClearRejectionLog("Lawless", Array.Empty<string>(), temp.Path);

        File.Exists(log).Should().BeFalse();
    }

    [Fact]
    public void WriteOrClearRejectionLogReplacesTheWholeFileRatherThanAppending()
    {
        using var temp = new TempDir(nameof(WriteOrClearRejectionLogReplacesTheWholeFileRatherThanAppending));
        var log = temp.WriteText(Path.Combine(Rejected, "Lawless.txt"), "Discarded a stale NPC ...");

        AnalysisLogCleaner.WriteOrClearRejectionLog("Lawless", new[] { "Discarded a current NPC ..." }, temp.Path);

        File.ReadAllLines(log).Should().Equal("Discarded a current NPC ...");
    }

    [Fact]
    public void WriteOrClearRejectionLogCreatesTheFolderOnFirstWrite()
    {
        using var temp = new TempDir(nameof(WriteOrClearRejectionLogCreatesTheFolderOnFirstWrite));

        AnalysisLogCleaner.WriteOrClearRejectionLog("Lawless", new[] { "Discarded ..." }, temp.Path);

        File.ReadAllLines(Path.Combine(temp.Path, Rejected, "Lawless.txt")).Should().Equal("Discarded ...");
    }

    /// <summary>Nothing rejected and nothing on disk is the common case; it must stay a no-op.</summary>
    [Fact]
    public void WriteOrClearRejectionLogWithNothingToDoDoesNotCreateAnything()
    {
        using var temp = new TempDir(nameof(WriteOrClearRejectionLogWithNothingToDoDoesNotCreateAnything));

        var act = () => AnalysisLogCleaner.WriteOrClearRejectionLog("Lawless", Array.Empty<string>(), temp.Path);

        act.Should().NotThrow();
        Directory.Exists(Path.Combine(temp.Path, Rejected)).Should().BeFalse();
    }

    /// <summary>
    /// Guards the shape VM_Mods relies on: one Rejected NPCs entry and the two LoadingErrors
    /// entries, since CheckForInjectedRecords writes its failures to a separate suffixed file.
    /// </summary>
    [Fact]
    public void GetLogPathsForModCoversBothFoldersAndTheInjectionCheckFile()
    {
        using var temp = new TempDir(nameof(GetLogPathsForModCoversBothFoldersAndTheInjectionCheckFile));

        var paths = AnalysisLogCleaner.GetLogPathsForMod("Lawless", temp.Path);

        paths.Should().BeEquivalentTo(new[]
        {
            Path.Combine(temp.Path, Rejected, "Lawless.txt"),
            Path.Combine(temp.Path, Errors, "Lawless.txt"),
            Path.Combine(temp.Path, Errors, "Lawless_InjectionCheck.txt"),
        });
    }
}
