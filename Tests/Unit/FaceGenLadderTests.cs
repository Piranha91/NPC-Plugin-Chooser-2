using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="FaceGenLadder.Classify"/> — the decision that says where each half of an NPC's
/// FaceGen comes from and whether the NPC can be patched at all.
///
/// <para>Pure by construction (presence, compatibility and chain resolution all arrive as
/// inputs), so these tests need no environment, mod list, or disk. That is the whole point of
/// splitting it out: the branch matrix is five rows times three destination modes times the
/// origin/winner/compatibility fallbacks, which is impractical to cover through a real patch run
/// — a full run over ~3000 NPCs produced roughly fifty row-2 cases and a single row-3.</para>
/// </summary>
public class FaceGenLadderTests
{
    private static readonly FormKey Target = FormKey.Factory("013BA5:Skyrim.esm");
    private static readonly FormKey Donor = FormKey.Factory("013BA5:Skyrim.esm");
    private static readonly FormKey Terminus = FormKey.Factory("01A696:Skyrim.esm");

    /// <summary>Row 1 by default — every fallback is available so a test only states its variable.</summary>
    private static FaceGenLadderInputs Inputs(
        FaceGenAssetPresence sourceNif = FaceGenAssetPresence.LooseFile,
        FaceGenAssetPresence sourceDds = FaceGenAssetPresence.LooseFile,
        bool hasPluginRecord = true,
        bool originRecordExists = true,
        FaceGenAssetPresence originNif = FaceGenAssetPresence.LooseFile,
        FaceGenAssetPresence originDds = FaceGenAssetPresence.LooseFile,
        bool winnerNif = true,
        bool winnerDds = true,
        bool? originCompatible = null,
        bool? winnerCompatible = null,
        FaceGenDestinationMode mode = FaceGenDestinationMode.Record,
        FaceGenChainStatus chain = FaceGenChainStatus.NotTemplated,
        FormKey? subject = null,
        bool flatten = false) =>
        new(
            NpcIdentifier: "Test NPC (013BA5:Skyrim.esm)",
            TargetFormKey: Target,
            DonorFormKey: Donor,
            SubjectFormKey: subject ?? Donor,
            ChainStatus: chain,
            ModName: "Some Mod",
            Mode: mode,
            SourceNif: sourceNif,
            SourceDds: sourceDds,
            SourceHasPluginRecord: hasPluginRecord,
            OriginRecordExists: originRecordExists,
            OriginNif: originNif,
            OriginDds: originDds,
            WinnerNifExists: winnerNif,
            WinnerNifOwner: "Some Other Mod",
            WinnerDdsExists: winnerDds,
            OriginNifCompatible: originCompatible,
            WinnerNifCompatible: winnerCompatible,
            LegacyDonorNif: sourceNif,
            LegacyDonorDds: sourceDds,
            FlattenTemplateChain: flatten);

    // ---- Row identification ------------------------------------------------------------------

    [Theory]
    [InlineData(FaceGenAssetPresence.LooseFile, FaceGenAssetPresence.LooseFile, true, FaceGenLadderRow.NifAndDds)]
    [InlineData(FaceGenAssetPresence.BsaFile, FaceGenAssetPresence.BsaFile, true, FaceGenLadderRow.NifAndDds)]
    [InlineData(FaceGenAssetPresence.LooseFile, FaceGenAssetPresence.NotFound, true, FaceGenLadderRow.NifOnly)]
    [InlineData(FaceGenAssetPresence.NotFound, FaceGenAssetPresence.LooseFile, true, FaceGenLadderRow.DdsOnlyWithRecord)]
    [InlineData(FaceGenAssetPresence.NotFound, FaceGenAssetPresence.LooseFile, false, FaceGenLadderRow.DdsOnlyNoRecord)]
    [InlineData(FaceGenAssetPresence.NotFound, FaceGenAssetPresence.NotFound, true, FaceGenLadderRow.Neither)]
    [InlineData(FaceGenAssetPresence.NotFound, FaceGenAssetPresence.NotFound, false, FaceGenLadderRow.Neither)]
    public void Row_IsDeterminedByWhatTheModShipsPlusWhetherItHasARecord(
        FaceGenAssetPresence nif, FaceGenAssetPresence dds, bool hasRecord, FaceGenLadderRow expected)
    {
        FaceGenLadder.Classify(Inputs(sourceNif: nif, sourceDds: dds, hasPluginRecord: hasRecord))
            .Row.Should().Be(expected);
    }

    // ---- Row 1 -------------------------------------------------------------------------------

    [Fact]
    public void Row1_TakesBothHalvesFromTheMod_AndNeverAborts()
    {
        var d = FaceGenLadder.Classify(Inputs());

        d.Abort.Should().BeFalse();
        d.NifChoice.Should().Be(FaceGenSourceChoice.AppearanceMod);
        d.DdsChoice.Should().Be(FaceGenSourceChoice.AppearanceMod);
    }

    // ---- Row 2: mesh present, tint missing ---------------------------------------------------

    [Fact]
    public void Row2_PrefersTheOriginsTint()
    {
        // A mod shipping one half of an NPC's FaceGen is signalling that it expects the origin's
        // counterpart for the other. The winner is only a backstop: preferring it would make the
        // outcome depend on whatever else happens to be installed.
        var d = FaceGenLadder.Classify(Inputs(sourceDds: FaceGenAssetPresence.NotFound));

        d.NifChoice.Should().Be(FaceGenSourceChoice.AppearanceMod);
        d.DdsChoice.Should().Be(FaceGenSourceChoice.Origin);
        d.Abort.Should().BeFalse();
    }

    [Fact]
    public void Row2_FallsBackToTheWinningTintWhenTheOriginHasNone()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceDds: FaceGenAssetPresence.NotFound,
            originDds: FaceGenAssetPresence.NotFound));

        d.DdsChoice.Should().Be(FaceGenSourceChoice.WinnerInPlace,
            "record mode's destination is where the winner already sits");
    }

    [Fact]
    public void Row2_CopiesTheWinningTintOutsideRecordMode()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceDds: FaceGenAssetPresence.NotFound,
            originDds: FaceGenAssetPresence.NotFound,
            mode: FaceGenDestinationMode.SkyPatcher));

        d.DdsChoice.Should().Be(FaceGenSourceChoice.Winner, "the surrogate's path is new, so nothing can fall through to it");
    }

    [Fact]
    public void Row2_WarnsRatherThanAbortsWhenNoTintExistsAnywhere()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceDds: FaceGenAssetPresence.NotFound,
            originDds: FaceGenAssetPresence.NotFound,
            winnerDds: false));

        d.Abort.Should().BeFalse("an untinted head still renders — refusing to patch would be worse");
        d.DdsChoice.Should().Be(FaceGenSourceChoice.None);
        d.NifChoice.Should().Be(FaceGenSourceChoice.AppearanceMod);
    }

    // ---- Row 3: tint present, mesh missing, mod edits the record ------------------------------

    [Fact]
    public void Row3_ForwardsTheOriginsMeshWhenItFitsTheRecord()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            originCompatible: true));

        d.NifChoice.Should().Be(FaceGenSourceChoice.Origin);
        d.DdsChoice.Should().Be(FaceGenSourceChoice.AppearanceMod);
        d.Abort.Should().BeFalse();
    }

    [Fact]
    public void Row3_LeavesTheWinnerInPlaceInRecordMode_WhenTheOriginsMeshDoesNotFit()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            originCompatible: false,
            winnerCompatible: true));

        d.NifChoice.Should().Be(FaceGenSourceChoice.WinnerInPlace);
        d.Abort.Should().BeFalse();
    }

    [Theory]
    [InlineData(FaceGenDestinationMode.FaceSwap)]
    [InlineData(FaceGenDestinationMode.SkyPatcher)]
    public void Row3_CopiesTheWinnerWhenTheDestinationIsADifferentFormKey(FaceGenDestinationMode mode)
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            originCompatible: false,
            winnerCompatible: true,
            mode: mode));

        d.NifChoice.Should().Be(FaceGenSourceChoice.Winner,
            "retargeting to another FormKey means the bytes must be copied under the new name");
    }

    [Fact]
    public void Row3_AbortsWhenNoCompatibleMeshExistsAnywhere()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            originCompatible: false,
            winnerCompatible: false));

        d.Abort.Should().BeTrue("patching would produce the dark-face bug");
        d.AbortReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Row3_TriesTheWinnerWhenTheOriginHasNoMeshAtAll()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            originNif: FaceGenAssetPresence.NotFound,
            winnerCompatible: true));

        d.NifChoice.Should().Be(FaceGenSourceChoice.WinnerInPlace);
        d.Abort.Should().BeFalse();
    }

    [Fact]
    public void Row3_UnevaluatedCompatibilityIsTreatedOptimistically()
    {
        // A measurement pass skips the NIF parse; classification must still produce a usable
        // verdict, flagged so the report can say the check did not run.
        var d = FaceGenLadder.Classify(Inputs(sourceNif: FaceGenAssetPresence.NotFound));

        d.NifChoice.Should().Be(FaceGenSourceChoice.Origin);
        d.CompatibilityEvaluated.Should().BeFalse();
        d.Abort.Should().BeFalse();
    }

    // ---- Rows 4 and 5: the mod ships no record -----------------------------------------------

    [Fact]
    public void Row4_ForwardsTheOriginsRecordAndMesh()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            hasPluginRecord: false));

        d.Row.Should().Be(FaceGenLadderRow.DdsOnlyNoRecord);
        d.ForwardOriginRecord.Should().BeTrue();
        d.NifChoice.Should().Be(FaceGenSourceChoice.Origin);
        d.DdsChoice.Should().Be(FaceGenSourceChoice.AppearanceMod, "the mod's own tint is still the point of the selection");
    }

    [Fact]
    public void Row4_NeedsNoCompatibilityCheck_BecauseRecordAndMeshShareASource()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            hasPluginRecord: false,
            originCompatible: false));

        d.NifChoice.Should().Be(FaceGenSourceChoice.Origin);
        d.Abort.Should().BeFalse();
    }

    [Fact]
    public void Row4_AbortsWhenTheOriginRecordCannotBeRead()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            hasPluginRecord: false,
            originRecordExists: false));

        d.Abort.Should().BeTrue();
    }

    [Fact]
    public void Row5_FallsBackToTheOriginForBothHalves()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.NotFound,
            hasPluginRecord: false));

        d.Row.Should().Be(FaceGenLadderRow.Neither);
        d.ForwardOriginRecord.Should().BeTrue();
        d.NifChoice.Should().Be(FaceGenSourceChoice.Origin);
        d.DdsChoice.Should().Be(FaceGenSourceChoice.Origin, "same origin-first pairing as row 2");
    }

    [Fact]
    public void Row5_FallsBackToTheWinningTintWhenTheOriginHasNone()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.NotFound,
            hasPluginRecord: false,
            originDds: FaceGenAssetPresence.NotFound));

        d.DdsChoice.Should().Be(FaceGenSourceChoice.WinnerInPlace);
    }

    [Fact]
    public void Row5_KeepsTheModsRecordWhenItHasOne()
    {
        // Row 5 arrives here both ways. A mod that edits the record but ships no face files must
        // keep its edits — handing the record to the origin would silently discard the appearance
        // the user picked, and would also check the borrowed mesh against the wrong record.
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.NotFound,
            hasPluginRecord: true));

        d.Row.Should().Be(FaceGenLadderRow.Neither);
        d.ForwardOriginRecord.Should().BeFalse();
        d.NifChoice.Should().Be(FaceGenSourceChoice.Origin);
        d.Abort.Should().BeFalse();
    }

    [Fact]
    public void Row5_WithAModRecord_DoesNotNeedTheOriginRecordToExist()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.NotFound,
            hasPluginRecord: true,
            originRecordExists: false));

        d.Abort.Should().BeFalse("the mod's own record is what ships");
    }

    [Fact]
    public void Row5_AbortsWhenNothingSuppliesAMesh()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.NotFound,
            hasPluginRecord: false,
            originNif: FaceGenAssetPresence.NotFound,
            winnerNif: false));

        d.Abort.Should().BeTrue();
    }

    // ---- Template chain ----------------------------------------------------------------------

    [Fact]
    public void UnfollowableChain_AbortsBeforeAnythingElseIsConsidered()
    {
        // Every source is present; the chain alone must still stop the patch, because a donor
        // that inherits has no face of its own and there is no terminus to borrow one from.
        var d = FaceGenLadder.Classify(Inputs(chain: FaceGenChainStatus.Unfollowable));

        d.Abort.Should().BeTrue();
        d.AbortReason.Should().Contain("inherits");
    }

    [Fact]
    public void LeveledTerminus_IsNotAFailure_AndAsksForNoFaceGen()
    {
        // Generic encounter actors template into a levelled list; the game resolves one at
        // runtime and draws ITS face. A first measurement pass over a real load order classified
        // 18 of these as unfollowable and would have refused to patch every one, so this guards
        // the distinction rather than the happy path.
        var d = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.LeveledTerminus,
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.NotFound,
            originNif: FaceGenAssetPresence.NotFound,
            originDds: FaceGenAssetPresence.NotFound,
            winnerNif: false,
            winnerDds: false));

        d.Abort.Should().BeFalse("a levelled terminus is normal, not broken");
        d.NifChoice.Should().Be(FaceGenSourceChoice.None);
        d.DdsChoice.Should().Be(FaceGenSourceChoice.None);
        d.LogLine.Should().Contain("levelled list");
    }

    [Fact]
    public void ResolvedChain_ClassifiesNormally_AtTheTerminus()
    {
        var d = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.Resolved,
            subject: Terminus,
            sourceNif: FaceGenAssetPresence.NotFound,
            originCompatible: true));

        d.Abort.Should().BeFalse();
        d.Row.Should().Be(FaceGenLadderRow.DdsOnlyWithRecord);
        d.Inputs.SubjectFormKey.Should().Be(Terminus);
    }

    // ---- Template flattening (TemplateHandlingMode.GiveEachNpcOwnCopy) -----------------------
    //
    // Flattening changes only the DESTINATION (the NPC's own path instead of the terminus's
    // shared one) — never the source, which is always measured at the subject's paths. The one
    // classification consequence: in record mode "use the winner" normally means the winner is
    // already at the destination and nothing needs copying, but a flattened NPC's destination is
    // its own path, so the winner's bytes must be copied across.

    [Fact]
    public void Flatten_ResolvedChain_RecordMode_CopiesTheWinnerInsteadOfLeavingItInPlace()
    {
        // Row 5 at the terminus, everything falling through to the winner — the orc Adventurer
        // shape. Without flattening this is WinnerInPlace/WinnerInPlace (copy nothing).
        var d = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.Resolved,
            subject: Terminus,
            flatten: true,
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.NotFound,
            hasPluginRecord: false,
            originNif: FaceGenAssetPresence.NotFound,
            originDds: FaceGenAssetPresence.NotFound,
            originRecordExists: true));

        d.Abort.Should().BeFalse();
        d.NifChoice.Should().Be(FaceGenSourceChoice.Winner,
            "the destination is the NPC's own path, so the winner at the terminus's path must be copied");
        d.DdsChoice.Should().Be(FaceGenSourceChoice.Winner);
    }

    [Fact]
    public void Flatten_ResolvedChain_DoesNotChangeTheSourceOrTheRow()
    {
        // Same inputs with and without the flag: identical row and identical sources whenever the
        // winner is not involved. Only the in-place shortcut may differ.
        var inherit = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.Resolved, subject: Terminus,
            sourceDds: FaceGenAssetPresence.NotFound));
        var flattened = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.Resolved, subject: Terminus,
            sourceDds: FaceGenAssetPresence.NotFound, flatten: true));

        flattened.Row.Should().Be(inherit.Row);
        flattened.NifChoice.Should().Be(inherit.NifChoice);
        flattened.DdsChoice.Should().Be(inherit.DdsChoice, "the origin tint is a copy either way");
    }

    [Fact]
    public void Flatten_UntemplatedNpc_KeepsTheInPlaceShortcut()
    {
        // The mode is global, but an untemplated NPC's destination is unchanged — its own path,
        // where the winner already sits — so WinnerInPlace stays correct with the flag on.
        var d = FaceGenLadder.Classify(Inputs(
            flatten: true,
            sourceDds: FaceGenAssetPresence.NotFound,
            originDds: FaceGenAssetPresence.NotFound));

        d.DdsChoice.Should().Be(FaceGenSourceChoice.WinnerInPlace);
    }

    [Fact]
    public void Flatten_LeveledTerminus_StillAsksForNoFaceGen()
    {
        // No fixed face exists — the game picks an actor at runtime — so the own-copy mode must
        // leave these inheriting exactly as the default does.
        var d = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.LeveledTerminus,
            flatten: true,
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.NotFound));

        d.Abort.Should().BeFalse();
        d.NifChoice.Should().Be(FaceGenSourceChoice.None);
        d.DdsChoice.Should().Be(FaceGenSourceChoice.None);
    }

    [Fact]
    public void Flatten_UnfollowableChain_StillAborts()
    {
        FaceGenLadder.Classify(Inputs(chain: FaceGenChainStatus.Unfollowable, flatten: true))
            .Abort.Should().BeTrue();
    }

    [Fact]
    public void Flatten_SkyPatcherMode_IsUnchanged()
    {
        // SkyPatcher already copies everything (the surrogate's path is brand new), so the flag
        // must not alter its choices.
        var inherit = FaceGenLadder.Classify(Inputs(
            mode: FaceGenDestinationMode.SkyPatcher,
            chain: FaceGenChainStatus.Resolved, subject: Terminus,
            sourceNif: FaceGenAssetPresence.NotFound, sourceDds: FaceGenAssetPresence.NotFound,
            hasPluginRecord: false,
            originNif: FaceGenAssetPresence.NotFound, originDds: FaceGenAssetPresence.NotFound));
        var flattened = FaceGenLadder.Classify(Inputs(
            mode: FaceGenDestinationMode.SkyPatcher,
            chain: FaceGenChainStatus.Resolved, subject: Terminus,
            sourceNif: FaceGenAssetPresence.NotFound, sourceDds: FaceGenAssetPresence.NotFound,
            hasPluginRecord: false,
            originNif: FaceGenAssetPresence.NotFound, originDds: FaceGenAssetPresence.NotFound,
            flatten: true));

        flattened.NifChoice.Should().Be(inherit.NifChoice);
        flattened.DdsChoice.Should().Be(inherit.DdsChoice);
    }

    // ---- Legacy comparison -------------------------------------------------------------------

    [Theory]
    [InlineData(FaceGenAssetPresence.LooseFile, FaceGenAssetPresence.LooseFile, "CopyNifAndDds")]
    [InlineData(FaceGenAssetPresence.LooseFile, FaceGenAssetPresence.NotFound, "CopyNifOnly")]
    [InlineData(FaceGenAssetPresence.NotFound, FaceGenAssetPresence.LooseFile, "CopyDdsOnly")]
    [InlineData(FaceGenAssetPresence.NotFound, FaceGenAssetPresence.NotFound, "CopyNothing")]
    public void LegacyAction_DescribesTheOldDonorScopedBehaviour(
        FaceGenAssetPresence nif, FaceGenAssetPresence dds, string expected)
    {
        FaceGenLadder.Classify(Inputs(sourceNif: nif, sourceDds: dds))
            .LegacyAction.Should().Be(expected);
    }

    [Fact]
    public void LegacyAction_IsMeasuredAtTheDonorPath_NotTheSubjectPath()
    {
        // The pre-ladder code derives its paths from the donor, so a templated donor resolves to
        // a path that by definition holds nothing — even when the terminus is fully supplied.
        // This divergence is the whole reason the report carries both columns.
        var i = Inputs(chain: FaceGenChainStatus.Resolved, subject: Terminus) with
        {
            LegacyDonorNif = FaceGenAssetPresence.NotFound,
            LegacyDonorDds = FaceGenAssetPresence.NotFound,
        };

        var d = FaceGenLadder.Classify(i);

        d.Row.Should().Be(FaceGenLadderRow.NifAndDds, "the terminus has both halves");
        d.LegacyAction.Should().Be("CopyNothing", "but the old code looked at the donor and found neither");
    }
}
