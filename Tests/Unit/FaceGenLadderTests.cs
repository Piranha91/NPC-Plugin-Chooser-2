using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.View_Models;
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
        bool? sourceCompatible = null,
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
            FlattenTemplateChain: flatten,
            SourceNifCompatible: sourceCompatible);

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
        // Flattened, because that is the only mode in which a resolved chain reaches the rows at
        // all — see the inherited-face section below.
        var d = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.Resolved,
            subject: Terminus,
            flatten: true,
            sourceNif: FaceGenAssetPresence.NotFound,
            originCompatible: true));

        d.Abort.Should().BeFalse();
        d.Row.Should().Be(FaceGenLadderRow.DdsOnlyWithRecord);
        d.Inputs.SubjectFormKey.Should().Be(Terminus);
    }

    // ---- Inherited faces (TemplateHandlingMode.InheritFromTemplate) ---------------------------
    //
    // The output record keeps inheriting, so the engine reads the TERMINUS's FaceGen path — a path
    // whose record this pass does not patch. Writing there pairs the mod's mesh with the terminus's
    // unpatched head parts, which is the dark-face bug, and lands it on an NPC the user never
    // selected. So nothing is written and no source is chosen.
    //
    // This was the measured defect of 2026-07-28: record mode wrote the mesh at the terminus's path
    // and the record at the selected NPC's, and the two halves never met.

    [Fact]
    public void Inherit_ResolvedChain_RecordMode_AsksForNoFaceGen()
    {
        // Row 1 inputs — the mod ships BOTH halves at the terminus's path, which is exactly the
        // repro (High Poly NPC Overhaul ships the terminus's face). Availability is not the issue;
        // there is nowhere this pass may put them.
        var d = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.Resolved,
            subject: Terminus));

        d.Abort.Should().BeFalse("the NPC is patched normally, it just keeps its template's face");
        d.NifChoice.Should().Be(FaceGenSourceChoice.None);
        d.DdsChoice.Should().Be(FaceGenSourceChoice.None);
        d.InheritedFaceLeftToTemplate.Should().BeTrue();
        d.LogLine.Should().Contain("template");
    }

    [Fact]
    public void Inherit_ResolvedChain_FaceSwapMode_AlsoAsksForNoFaceGen()
    {
        // A shared/guest appearance whose DONOR inherits: the target's own record is written and
        // then mirrors the donor's Traits state, so the engine reads the terminus's path there too.
        var d = FaceGenLadder.Classify(Inputs(
            mode: FaceGenDestinationMode.FaceSwap,
            chain: FaceGenChainStatus.Resolved,
            subject: Terminus));

        d.NifChoice.Should().Be(FaceGenSourceChoice.None);
        d.DdsChoice.Should().Be(FaceGenSourceChoice.None);
    }

    [Fact]
    public void Inherit_ResolvedChain_DoesNotAbort_EvenWithNoMeshAnywhere()
    {
        // Row 3's abort exists to stop an incompatible mesh being written. Nothing is being
        // written here, so aborting would refuse to patch an NPC over a file it was never going to
        // produce — and the record half of the patch is still wanted.
        var d = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.Resolved,
            subject: Terminus,
            sourceNif: FaceGenAssetPresence.NotFound,
            originNif: FaceGenAssetPresence.NotFound,
            winnerNif: false,
            originCompatible: false,
            winnerCompatible: false));

        d.Abort.Should().BeFalse();
    }

    [Fact]
    public void Inherit_ResolvedChain_RaisesNoTintWarning()
    {
        // MissingTintEverywhere means "a mesh is being copied with no tint to go with it". No mesh
        // is being copied, so flagging the tint would be noise on every templated NPC in the run.
        FaceGenLadder.Classify(Inputs(
                chain: FaceGenChainStatus.Resolved,
                subject: Terminus,
                sourceDds: FaceGenAssetPresence.NotFound,
                originDds: FaceGenAssetPresence.NotFound,
                winnerDds: false))
            .MissingTintEverywhere.Should().BeFalse();
    }

    [Fact]
    public void Inherit_SkyPatcherMode_ClassifiesNormally()
    {
        // SkyPatcher's destination is the surrogate's own path — a record this pass DOES write — so
        // the rule does not apply. That combination is inert rather than wrong, and the validator
        // rejects it per NPC upstream (Validator.CanSkyPatcherApplyAppearance).
        var d = FaceGenLadder.Classify(Inputs(
            mode: FaceGenDestinationMode.SkyPatcher,
            chain: FaceGenChainStatus.Resolved,
            subject: Terminus));

        d.InheritedFaceLeftToTemplate.Should().BeFalse();
        d.NifChoice.Should().Be(FaceGenSourceChoice.AppearanceMod);
        d.DdsChoice.Should().Be(FaceGenSourceChoice.AppearanceMod);
    }

    [Theory]
    // The predicate the ladder gates on and the end-of-run report reads, stated once. Only a chain
    // that RESOLVES to a concrete NPC is affected: a levelled or unfollowable chain has no terminus
    // record to collide with, and is handled by its own branch above.
    [InlineData(FaceGenDestinationMode.Record, FaceGenChainStatus.Resolved, false, true)]
    [InlineData(FaceGenDestinationMode.FaceSwap, FaceGenChainStatus.Resolved, false, true)]
    [InlineData(FaceGenDestinationMode.Record, FaceGenChainStatus.Resolved, true, false)]
    [InlineData(FaceGenDestinationMode.SkyPatcher, FaceGenChainStatus.Resolved, false, false)]
    [InlineData(FaceGenDestinationMode.Record, FaceGenChainStatus.NotTemplated, false, false)]
    [InlineData(FaceGenDestinationMode.Record, FaceGenChainStatus.LeveledTerminus, false, false)]
    [InlineData(FaceGenDestinationMode.Record, FaceGenChainStatus.Unfollowable, false, false)]
    public void KeepsInheritedFace_IsExactlyResolvedChainPlusNoFlattenPlusNotSkyPatcher(
        FaceGenDestinationMode mode, FaceGenChainStatus chain, bool flatten, bool expected)
    {
        FaceGenLadder.KeepsInheritedFace(Inputs(mode: mode, chain: chain, flatten: flatten))
            .Should().Be(expected);
    }

    [Fact]
    public void Inherit_UntemplatedNpc_IsUnaffected()
    {
        var d = FaceGenLadder.Classify(Inputs());

        d.InheritedFaceLeftToTemplate.Should().BeFalse();
        d.NifChoice.Should().Be(FaceGenSourceChoice.AppearanceMod);
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
    public void Flatten_ResolvedChain_IsWhatMakesTheModsFaceReachTheNpc()
    {
        // The two template settings are NOT two spellings of the same classification. Inheriting,
        // the destination belongs to another NPC's record and nothing may be written; flattening
        // makes the destination this NPC's own path, and only then does the mod's face apply.
        var inherit = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.Resolved, subject: Terminus,
            sourceDds: FaceGenAssetPresence.NotFound));
        var flattened = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.Resolved, subject: Terminus,
            sourceDds: FaceGenAssetPresence.NotFound, flatten: true));

        inherit.NifChoice.Should().Be(FaceGenSourceChoice.None);
        flattened.Row.Should().Be(FaceGenLadderRow.NifOnly);
        flattened.NifChoice.Should().Be(FaceGenSourceChoice.AppearanceMod);
        flattened.DdsChoice.Should().Be(FaceGenSourceChoice.Origin, "the mod ships no tint of its own");
    }

    // The open question this section used to leave deliberately unasserted — "what should own-copy
    // produce when the selected mod ships no FaceGen at the terminus's path?" — was DECIDED
    // 2026-07-30: it should read exactly like inheriting from a template that has no selection of
    // its own, i.e. the NPC keeps the face it would have had and the user is TOLD the choice could
    // not be delivered. The classification already produced the right face; what was missing was
    // saying so outside verbose mode, which is what FlattenedFaceCameFromElsewhere drives.

    [Fact]
    public void Flatten_ModSuppliesNeitherHalf_IsReportedAsUndeliverable()
    {
        // Row 5 under a flatten: nothing from the mod, nothing from the origin, so the terminus's
        // winning face is copied onto the NPC's own path. In game that is the face it already had.
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

        d.FlattenedFaceCameFromElsewhere.Should().BeTrue(
            "the user's selection reached neither half of the face, which is the same disappointment " +
            "as inheriting from a template with no selection — and earns the same forced report");
    }

    [Fact]
    public void Flatten_ModSuppliesTheTint_IsNotReportedAsUndeliverable()
    {
        // Row 3/4: the mod's tint IS on the face and only the geometry is borrowed. Calling that
        // "could not be applied" would be false, and would bury the real cases in noise.
        var d = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.Resolved,
            subject: Terminus,
            flatten: true,
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.LooseFile,
            hasPluginRecord: false,
            originNif: FaceGenAssetPresence.LooseFile,
            originRecordExists: true));

        d.DdsChoice.Should().Be(FaceGenSourceChoice.AppearanceMod);
        d.FlattenedFaceCameFromElsewhere.Should().BeFalse();
    }

    [Fact]
    public void Inherit_IsNotAlsoReportedAsAFlattenedFallback()
    {
        // The two reports must not both fire for one NPC: inheriting is its own outcome, and
        // FlattenedFaceCameFromElsewhere is specifically about a flatten that had nothing to carry.
        var d = FaceGenLadder.Classify(Inputs(
            chain: FaceGenChainStatus.Resolved,
            subject: Terminus,
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.NotFound));

        d.InheritedFaceLeftToTemplate.Should().BeTrue();
        d.FlattenedFaceCameFromElsewhere.Should().BeFalse();
    }

    [Fact]
    public void UntemplatedNpc_IsNeverAFlattenedFallback()
    {
        // The mode is global. An NPC with no chain to flatten must not be swept into the report just
        // because the setting is on and its mesh came from the winner.
        var d = FaceGenLadder.Classify(Inputs(
            flatten: true,
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.NotFound,
            hasPluginRecord: false,
            originNif: FaceGenAssetPresence.NotFound,
            originDds: FaceGenAssetPresence.NotFound,
            originRecordExists: true));

        d.FlattenedFaceCameFromElsewhere.Should().BeFalse();
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
        //
        // Flattened so the row is reached at all: an inheriting NPC short-circuits before the rows
        // (see the inherited-face section). LegacyAction is recorded on that branch too.
        var i = Inputs(chain: FaceGenChainStatus.Resolved, subject: Terminus, flatten: true) with
        {
            LegacyDonorNif = FaceGenAssetPresence.NotFound,
            LegacyDonorDds = FaceGenAssetPresence.NotFound,
        };

        var d = FaceGenLadder.Classify(i);

        d.Row.Should().Be(FaceGenLadderRow.NifAndDds, "the terminus has both halves");
        d.LegacyAction.Should().Be("CopyNothing", "but the old code looked at the donor and found neither");
    }

    // ---- MissingTintEverywhere ---------------------------------------------------------------
    //
    // Carried apart from LogLine because it is a non-abort outcome the user must act on: the
    // NPCs it flags are reported after the run by NpcWarningReporter (kind MissingFaceTint),
    // where LogLine stays verbose-only.

    [Fact]
    public void MissingTint_IsFlagged_WhenAMeshIsCopiedWithNoTintAnywhere()
    {
        // Mesh from the mod, and no tint from the mod, the origin, or the load order.
        var d = FaceGenLadder.Classify(Inputs(
            sourceDds: FaceGenAssetPresence.NotFound,
            originDds: FaceGenAssetPresence.NotFound,
            winnerDds: false));

        d.Abort.Should().BeFalse();
        d.NifChoice.Should().NotBe(FaceGenSourceChoice.None);
        d.DdsChoice.Should().Be(FaceGenSourceChoice.None);
        d.MissingTintEverywhere.Should().BeTrue();
    }

    [Fact]
    public void MissingTint_IsFlagged_ForABorrowedWinnerMesh_WhoseOwnSentenceNeverMentionsTint()
    {
        // Row 5 falling through to another mod's mesh. Deriving the flag from the choices (not
        // per branch) is what covers this one — its LogLine says nothing about tint.
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.NotFound,
            originNif: FaceGenAssetPresence.NotFound,
            originDds: FaceGenAssetPresence.NotFound,
            winnerNif: true,
            winnerDds: false));

        d.NifChoice.Should().BeOneOf(FaceGenSourceChoice.Winner, FaceGenSourceChoice.WinnerInPlace);
        d.DdsChoice.Should().Be(FaceGenSourceChoice.None);
        d.MissingTintEverywhere.Should().BeTrue();
        d.LogLine.Should().NotContain("discoloured",
            "the consequence belongs to the end-of-run warning report; duplicating it would double-log it");
    }

    [Fact]
    public void MissingTint_IsNotFlagged_ForALevelledTerminus()
    {
        // Also has no tint, but needs none: the game resolves the actor and draws its face at
        // runtime. Flagging here would fire on a large, perfectly healthy population.
        var d = FaceGenLadder.Classify(Inputs(chain: FaceGenChainStatus.LeveledTerminus));

        d.DdsChoice.Should().Be(FaceGenSourceChoice.None);
        d.NifChoice.Should().Be(FaceGenSourceChoice.None, "nothing is copied at all");
        d.MissingTintEverywhere.Should().BeFalse();
    }

    [Fact]
    public void MissingTint_IsNotFlagged_WhenATintIsFound()
    {
        FaceGenLadder.Classify(Inputs()).MissingTintEverywhere.Should().BeFalse();
    }

    // ---- OriginMeshFailedCompatCheck ---------------------------------------------------------
    //
    // Rows 4/5 take the origin mesh UNGATED (decided 2026-07-30): a mod shipping no mesh is almost
    // always authored against the origin's data, so a hard gate would refuse NPCs that render
    // fine. When the probe POSITIVELY failed — another mod overrode the subject's origin data, RS
    // Children being the measured wild case (Britte/Sissel, docs/KnownLimitations.md #5) — the NPC
    // is flagged for the end-of-run warning report (NpcWarningReporter, kind
    // OriginMeshCompatibility) instead of silently shipping a face the app knows is suspect.

    /// <summary>Row 4: FaceGen-only selection whose mesh must come from the origin.</summary>
    private static FaceGenLadderInputs Row4OriginInputs(bool? originCompatible) => Inputs(
        sourceNif: FaceGenAssetPresence.NotFound,
        hasPluginRecord: false,
        originCompatible: originCompatible);

    [Fact]
    public void OriginCompat_IsFlagged_WhenTheProbeSaidTheOriginMeshDoesNotFit()
    {
        var d = FaceGenLadder.Classify(Row4OriginInputs(originCompatible: false));

        d.Abort.Should().BeFalse("the assumption is deliberately kept — warn, don't gate");
        d.NifChoice.Should().Be(FaceGenSourceChoice.Origin,
            "the origin mesh is still taken; the flag rides along instead of vetoing it");
        d.OriginMeshFailedCompatCheck.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]  // probe ran and passed — the assumption held
    [InlineData(null)]  // probe never ran — nothing is known, so nothing to warn about
    public void OriginCompat_IsNotFlagged_WhenTheProbePassedOrNeverRan(bool? originCompatible)
    {
        var d = FaceGenLadder.Classify(Row4OriginInputs(originCompatible));

        d.NifChoice.Should().Be(FaceGenSourceChoice.Origin);
        d.OriginMeshFailedCompatCheck.Should().BeFalse();
    }

    [Fact]
    public void OriginCompat_IsNotFlagged_OnRowThree_WhichGatesInsteadOfWarning()
    {
        // Row 3 rejects an incompatible origin mesh outright (falls to winner/abort), so there is
        // no suspect pairing left to warn about.
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            hasPluginRecord: true,
            originCompatible: false,
            winnerCompatible: true));

        d.Row.Should().Be(FaceGenLadderRow.DdsOnlyWithRecord);
        d.NifChoice.Should().NotBe(FaceGenSourceChoice.Origin);
        d.OriginMeshFailedCompatCheck.Should().BeFalse();
    }

    [Fact]
    public void OriginCompat_IsFlagged_OnRowFive_WithARecord()
    {
        // Row 5 with a record rides the same ungated origin branch as row 4.
        var d = FaceGenLadder.Classify(Inputs(
            sourceNif: FaceGenAssetPresence.NotFound,
            sourceDds: FaceGenAssetPresence.NotFound,
            hasPluginRecord: true,
            originCompatible: false));

        d.Row.Should().Be(FaceGenLadderRow.Neither);
        d.NifChoice.Should().Be(FaceGenSourceChoice.Origin);
        d.OriginMeshFailedCompatCheck.Should().BeTrue();
    }

    // ---- ModMeshFailedCompatCheck ------------------------------------------------------------
    //
    // Row 2 with a FaceGen-only selection ships the MOD's mesh against the ORIGIN's record — the
    // mirror image of the rows-4/5 pairing above, and the same stance: probe, warn, never gate.

    [Fact]
    public void ModMeshCompat_IsFlagged_WhenTheProbeSaidTheModMeshDoesNotFit()
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceDds: FaceGenAssetPresence.NotFound, // row 2: mesh only
            hasPluginRecord: false,                   // FaceGen-only: the origin's record ships
            sourceCompatible: false));

        d.Row.Should().Be(FaceGenLadderRow.NifOnly);
        d.Abort.Should().BeFalse("warn, don't gate");
        d.NifChoice.Should().Be(FaceGenSourceChoice.AppearanceMod,
            "the mod's mesh is still used; the flag rides along instead of vetoing it");
        d.ModMeshFailedCompatCheck.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]  // probe ran and passed — the assumption held
    [InlineData(null)]  // probe never ran — nothing is known, so nothing to warn about
    public void ModMeshCompat_IsNotFlagged_WhenTheProbePassedOrNeverRan(bool? sourceCompatible)
    {
        var d = FaceGenLadder.Classify(Inputs(
            sourceDds: FaceGenAssetPresence.NotFound,
            hasPluginRecord: false,
            sourceCompatible: sourceCompatible));

        d.NifChoice.Should().Be(FaceGenSourceChoice.AppearanceMod);
        d.ModMeshFailedCompatCheck.Should().BeFalse();
    }

    [Fact]
    public void ModMeshCompat_CountsAsCompatibilityEvaluated()
    {
        FaceGenLadder.Classify(Inputs(
                sourceDds: FaceGenAssetPresence.NotFound,
                hasPluginRecord: false,
                sourceCompatible: false))
            .CompatibilityEvaluated.Should().BeTrue();
    }
}
