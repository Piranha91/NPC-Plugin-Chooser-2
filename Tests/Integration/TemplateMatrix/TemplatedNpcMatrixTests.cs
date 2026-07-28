using FluentAssertions;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// Runs the REAL patcher headlessly over a fixed set of eight synthetic specimen NPCs across every
/// output-mode x template-setting cell, and asserts invariants on what lands on disk. This is the
/// middle layer between the unit tests of <c>TemplateHandlingMode</c>'s pieces
/// (<c>Tests/Unit/TemplateFlatteningTests.cs</c>, <c>FaceGenLadderTests.cs</c>) and human in-game
/// verification.
///
/// <para>Sibling of the golden-output suite, not an extension of it: it shares the plumbing
/// (<c>GoldenPatchRunner</c>, <c>SkyPatcherIniComparer</c>) but asserts invariants rather than
/// comparing against a stored reference set the user would have to regenerate.</para>
///
/// <para>Every cell is run once and cached on the fixture; <see cref="TemplateMatrixChecks"/> evaluates
/// all invariants in one place so these tests and the HTML report cannot disagree. Skips gracefully
/// without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class TemplatedNpcMatrixTests : IClassFixture<TemplateMatrixFixture>
{
    private readonly TemplateMatrixFixture _fixture;
    private readonly ITestOutputHelper _output;

    public TemplatedNpcMatrixTests(TemplateMatrixFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private bool Skip()
    {
        if (_fixture.Available) return false;
        _output.WriteLine("SKIPPED: " + _fixture.SkipReason);
        return true;
    }

    private async Task AssertGroup(string group)
    {
        using var _ = new StaticStateGuard();
        var cells = await _fixture.AllCellsAsync();
        var checks = TemplateMatrixChecks.Evaluate(_fixture.Fixture!, cells);
        var mine = checks.Where(c => c.Name.StartsWith("[" + group + "]", StringComparison.Ordinal)).ToList();

        mine.Should().NotBeEmpty($"group '{group}' must actually evaluate something");

        var failures = mine.Where(c => !c.Passed)
            .Select(c => $"{c.Name}\n    {c.Detail}")
            .ToList();
        if (failures.Count > 0) _output.WriteLine(string.Join("\n", failures));
        _output.WriteLine($"{group}: {mine.Count - failures.Count}/{mine.Count} passed.");
        failures.Should().BeEmpty($"every '{group}' invariant must hold");
    }

    /// <summary>
    /// §7.2's trap, closed. Every specimen must be present in the OUTPUT before anything is asserted
    /// about its output — otherwise an absent record makes every later assertion pass vacuously.
    /// Deliberately measured from the run's own NPC_Token.json plus the written plugin, NOT from
    /// <c>PatchedTargets</c>: that is built from the screening cache before the patch loop, so an NPC
    /// the FaceGen ladder later aborts (specimen #8, and any NPC whose face cannot be assembled) still
    /// appears in it while being wholly absent from the output.
    /// </summary>
    [Fact]
    public async Task EverySpecimen_IsAccountedFor_InEveryCell()
    {
        if (Skip()) return;
        await AssertGroup(TemplateMatrixChecks.Presence);
    }

    /// <summary>
    /// DECISIVE (§3a). Untemplated specimens and the two carve-outs must produce identical records and
    /// identical face files under both template settings. The single strongest guard that the feature is
    /// inert where it must be.
    /// </summary>
    [Fact]
    public async Task UntouchedSpecimens_AreIdentical_AcrossBothTemplateSettings()
    {
        if (Skip()) return;
        await AssertGroup(TemplateMatrixChecks.Control);
    }

    /// <summary>
    /// DECISIVE (§3b). Two NPCs sharing one terminus, given different mods: under own-copy each gets its
    /// own FaceGen file and the two files differ; under inherit neither gets one and the terminus's
    /// shared path keeps the terminus's own selection. Nothing else distinguishes the feature working
    /// from the feature merely not crashing.
    /// </summary>
    [Fact]
    public async Task SharedTerminusSpecimens_GetIndependentFaceGen_UnderOwnCopy()
    {
        if (Skip()) return;
        await AssertGroup(TemplateMatrixChecks.FaceGen);
    }

    /// <summary>§3a. Flattened records carry the terminus's appearance with Traits cleared and TPLT kept;
    /// inheriting records keep both.</summary>
    [Fact]
    public async Task TemplatedRecords_FollowTheTemplateSetting()
    {
        if (Skip()) return;
        await AssertGroup(TemplateMatrixChecks.Record);
    }

    /// <summary>§3c. A surrogate exists per patched NPC and the .ini points at it.</summary>
    [Fact]
    public async Task SkyPatcherSurrogates_AreEmittedAndReferenced()
    {
        if (Skip()) return;
        await AssertGroup(TemplateMatrixChecks.SkyPatcher);
    }

    /// <summary>
    /// §3d. The classifier's verdict, asserted separately from its effect — when a disk assertion fails
    /// this says whether the ladder decided wrong or the writer failed to act on a correct decision.
    /// </summary>
    [Fact]
    public async Task LadderDecisions_MatchEachSpecimensShape()
    {
        if (Skip()) return;
        await AssertGroup(TemplateMatrixChecks.Ladder);
    }

    /// <summary>
    /// §3c's regression guard, called out by name. Before this feature, <c>CopyAppearanceData</c> ran on
    /// the surrogate AFTER <c>CreateSkyPatcherNpc</c> had flattened it, re-copying the donor's fields and
    /// re-mirroring its Traits flag — so SkyPatcher's supposedly unconditional flatten was silently inert
    /// in exactly this cell. A suite that only covered Create + SkyPatcher would have missed it entirely.
    /// </summary>
    [Fact]
    public async Task CreateAndPatch_SkyPatcher_OwnCopy_ActuallyFlattensTheSurrogate()
    {
        if (Skip()) return;
        using var _ = new StaticStateGuard();

        var cell = TemplateMatrixCells.All.Single(c =>
            c.PatchingMode == PatchingMode.CreateAndPatch
            && c.UseSkyPatcher
            && c.TemplateMode == TemplateHandlingMode.GiveEachNpcOwnCopy);

        var result = await _fixture.CellAsync(cell);

        foreach (var role in new[] { SpecimenRole.TemplatedA, SpecimenRole.TemplatedB })
        {
            var o = result[role];
            _output.WriteLine($"{role}: surrogate={o.RecordFormKey} traits={o.TraitsFlag} " +
                              $"height={o.Height} weight={o.Weight} female={o.Female} " +
                              $"headParts=[{string.Join(",", o.HeadPartEditorIds)}]");

            o.Processed.Should().BeTrue($"{role} must be patched in {cell.Name}");
            o.RecordPresent.Should().BeTrue($"{role}'s surrogate must exist in the output plugin");
            o.TraitsFlag.Should().BeFalse(
                $"{role}'s surrogate must have Traits CLEARED — the re-applied terminus overlay is what " +
                "stops CopyAppearanceData silently undoing the flatten");
            o.Height.Should().BeApproximately(TemplateFixtureBuilder.ModZHeight, 0.001f,
                "the surrogate must carry the terminus's appearance, not the donor's");
            o.HeadPartEditorIds.Should().Equal(TemplateFixtureBuilder.HeadPartModZ);
        }

        result[SpecimenRole.TemplatedA].OwnFaceGenHash.Should().NotBeNull();
        result[SpecimenRole.TemplatedB].OwnFaceGenHash.Should().NotBeNull();
        result[SpecimenRole.TemplatedA].OwnFaceGenHash.Should()
            .NotBe(result[SpecimenRole.TemplatedB].OwnFaceGenHash,
                "the two surrogates must carry the different faces their different mods supplied");
    }

    /// <summary>
    /// The per-mod override. Its resolution logic (<c>Settings.GetEffectiveTemplateHandlingMode</c>) is
    /// already unit-tested; what is untested is that the PATCHER reads the effective mode rather than the
    /// global one. #3 (Mod X) and #4 (Mod Y) share a terminus and differ only by mod, so overriding Mod X
    /// alone must split them — in both directions.
    /// </summary>
    [Theory]
    [InlineData(TemplateHandlingMode.InheritFromTemplate, TemplateHandlingMode.GiveEachNpcOwnCopy)]
    [InlineData(TemplateHandlingMode.GiveEachNpcOwnCopy, TemplateHandlingMode.InheritFromTemplate)]
    public async Task PerModTemplateOverride_BeatsTheGlobalSetting(
        TemplateHandlingMode global, TemplateHandlingMode modXOverride)
    {
        if (Skip()) return;
        using var _ = new StaticStateGuard();

        // CreateAndPatch without SkyPatcher: the plain record path, where "flattened" and "inheriting"
        // are visible as a FaceGen file existing at the NPC's own FormID path or not.
        var cell = TemplateMatrixCells.All.Single(c =>
            c.PatchingMode == PatchingMode.CreateAndPatch && !c.UseSkyPatcher && c.TemplateMode == global);

        var outDir = System.IO.Path.Combine(_fixture.CellsDirectory,
            $"override - global {global} - ModX {modXOverride}");

        var result = await TemplateMatrixRunner.RunAsync(
            _fixture.Fixture!, _fixture.Provider!, cell, outDir,
            new Dictionary<string, TemplateHandlingMode>
            {
                [TemplateFixtureBuilder.ModXName] = modXOverride,
            });

        var a = result[SpecimenRole.TemplatedA];   // Mod X -> follows the override
        var b = result[SpecimenRole.TemplatedB];   // Mod Y -> follows the global setting

        _output.WriteLine($"global={global} ModX={modXOverride}");
        _output.WriteLine($"  #3 (Mod X): flatten={a.Ladder?.Inputs.FlattenTemplateChain} traits={a.TraitsFlag} " +
                          $"facegen={a.OwnFaceGenHash ?? "none"}");
        _output.WriteLine($"  #4 (Mod Y): flatten={b.Ladder?.Inputs.FlattenTemplateChain} traits={b.TraitsFlag} " +
                          $"facegen={b.OwnFaceGenHash ?? "none"}");

        bool xFlattens = modXOverride == TemplateHandlingMode.GiveEachNpcOwnCopy;
        bool yFlattens = global == TemplateHandlingMode.GiveEachNpcOwnCopy;
        xFlattens.Should().NotBe(yFlattens, "the two specimens must land on opposite sides for this to test anything");

        a.Ladder!.Inputs.FlattenTemplateChain.Should().Be(xFlattens,
            "#3's mod carries the per-mod override, so the patcher must use it rather than the global setting");
        b.Ladder!.Inputs.FlattenTemplateChain.Should().Be(yFlattens,
            "#4's mod has no override, so it must follow the global setting");

        a.TraitsFlag.Should().Be(!xFlattens);
        b.TraitsFlag.Should().Be(!yFlattens);

        (a.OwnFaceGenHash != null).Should().Be(xFlattens,
            "a flattened NPC gets a face file at its own FormID path; an inheriting one does not");
        (b.OwnFaceGenHash != null).Should().Be(yFlattens);
    }

    /// <summary>
    /// Writes the HTML side-by-side. Its own test so it runs whether or not the assertions above passed —
    /// a failing run is exactly when the report is wanted. It explains; it does not assert.
    /// </summary>
    [Fact]
    public async Task EmitsHtmlReport()
    {
        if (Skip()) return;
        using var _ = new StaticStateGuard();

        var cells = await _fixture.AllCellsAsync();
        var checks = TemplateMatrixChecks.Evaluate(_fixture.Fixture!, cells);
        var path = TemplateMatrixReport.Write(_fixture, cells, checks);

        _output.WriteLine("Report: " + path);
        _output.WriteLine($"{checks.Count(c => c.Passed)}/{checks.Count} checks passed.");
        System.IO.File.Exists(path).Should().BeTrue("the report must be written on every run, pass or fail");
    }
}
