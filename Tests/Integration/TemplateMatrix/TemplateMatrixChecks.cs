using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// Every invariant the matrix asserts, evaluated in one place so the tests and the HTML report cannot
/// drift apart: the tests filter these by group and require them to pass, the report renders all of them.
/// </summary>
internal static class TemplateMatrixChecks
{
    public const string Presence = "Presence";
    public const string Control = "Inertness control";
    public const string FaceGen = "Per-NPC FaceGen";
    public const string Record = "Flattened record";
    public const string SkyPatcher = "SkyPatcher surrogate";
    public const string Ladder = "Ladder decision";

    private const float Tol = 0.001f;

    public static List<MatrixCheck> Evaluate(TemplateFixture fx, IReadOnlyList<CellResult> cells)
    {
        var checks = new List<MatrixCheck>();
        foreach (var cell in cells)
        {
            EvaluatePresence(fx, cell, checks);
            EvaluateLadder(fx, cell, checks);
            EvaluateFaceGen(cell, checks);
            EvaluateRecords(cell, checks);
            if (cell.Cell.UseSkyPatcher) EvaluateSkyPatcher(cell, checks);
        }

        EvaluateControls(cells, checks);
        return checks;
    }

    // ------------------------------------------------------------------ presence

    /// <summary>
    /// The gate every other assertion hangs off. Note it is <see cref="SpecimenObservation.Processed"/>
    /// (the run's own NPC_Token.json) and the presence of a record, NOT
    /// <c>GoldenPatchResult.PatchedTargets</c>: that list is built from the screening cache BEFORE the
    /// patch loop runs, so an NPC the FaceGen ladder later aborts still appears in it. #8 is exactly
    /// that case, and gating on PatchedTargets would let every assertion about it pass vacuously.
    /// </summary>
    private static void EvaluatePresence(TemplateFixture fx, CellResult cell, List<MatrixCheck> checks)
    {
        // Plain Create cannot express a cross-NPC appearance swap; the validator rejects those up front.
        // SkyPatcher performs the swap at runtime, so it is permitted there regardless of patching mode.
        bool swapsRejected = cell.Cell.PatchingMode != PatchingMode.CreateAndPatch && !cell.Cell.UseSkyPatcher;

        foreach (var role in TemplateMatrixSettingsBuilder.SpecimenRoles)
        {
            var o = cell[role];

            if (role == SpecimenRole.TemplatedUnfollowable)
            {
                Add(checks, Presence, cell, role,
                    !o.Processed && !o.RecordPresent && o.Ladder?.Abort == true,
                    $"template cycle: absent from the output (processed={o.Processed}, record={o.RecordPresent}, " +
                    $"ladderAbort={o.Ladder?.Abort}). It DOES pass screening (screened={o.ScreenedValid}), which is " +
                    "why this suite gates on the output rather than on PatchedTargets.");
                continue;
            }

            bool isSwap = role is SpecimenRole.PlainShared or SpecimenRole.TemplatedShared;
            if (isSwap && swapsRejected)
            {
                Add(checks, Presence, cell, role,
                    !o.Processed && !o.RecordPresent && o.InvalidReason != null,
                    $"appearance swap rejected in {cell.Cell.PatchingMode} mode as expected " +
                    $"(reason='{o.InvalidReason ?? "NONE — the validator accepted it"}').");
                continue;
            }

            Add(checks, Presence, cell, role, o.Processed && o.RecordPresent,
                $"present in the output (processed={o.Processed}, record={o.RecordPresent}" +
                (o.InvalidReason == null ? "" : $", validator='{o.InvalidReason}'") + ").");
        }
    }

    // ------------------------------------------------------------------ ladder (§3d)

    private static void EvaluateLadder(TemplateFixture fx, CellResult cell, List<MatrixCheck> checks)
    {
        var expected = new (string Role, FaceGenChainStatus Status, string SubjectRole, bool Abort)[]
        {
            (SpecimenRole.PlainSelf, FaceGenChainStatus.NotTemplated, SpecimenRole.PlainSelf, false),
            (SpecimenRole.PlainShared, FaceGenChainStatus.NotTemplated, SpecimenRole.PlainSharedDonor, false),
            (SpecimenRole.TemplatedA, FaceGenChainStatus.Resolved, SpecimenRole.Terminus, false),
            (SpecimenRole.TemplatedB, FaceGenChainStatus.Resolved, SpecimenRole.Terminus, false),
            (SpecimenRole.Terminus, FaceGenChainStatus.NotTemplated, SpecimenRole.Terminus, false),
            (SpecimenRole.TemplatedShared, FaceGenChainStatus.Resolved, SpecimenRole.TemplatedSharedDonorTerminus, false),
            // A levelled terminus is not a failure: the game picks an actor at runtime, so there is no
            // face to copy and nothing to abort over.
            (SpecimenRole.TemplatedLeveled, FaceGenChainStatus.LeveledTerminus, SpecimenRole.TemplatedLeveled, false),
            (SpecimenRole.TemplatedUnfollowable, FaceGenChainStatus.Unfollowable, SpecimenRole.TemplatedUnfollowable, true),
        };

        bool flattenExpected = cell.Cell.TemplateMode == TemplateHandlingMode.GiveEachNpcOwnCopy;

        foreach (var (role, status, subjectRole, abort) in expected)
        {
            var o = cell[role];
            if (o.Ladder == null)
            {
                // No decision is recorded for a selection the validator rejected before the patch loop.
                if (o.InvalidReason != null) continue;
                Add(checks, Ladder, cell, role, false, "no ladder decision was recorded for a processed NPC.");
                continue;
            }

            var i = o.Ladder.Inputs;
            bool ok = i.ChainStatus == status
                      && i.SubjectFormKey == fx.Npc(subjectRole)
                      && i.FlattenTemplateChain == flattenExpected
                      && o.Ladder.Abort == abort;

            Add(checks, Ladder, cell, role, ok,
                $"expected {status}/subject={subjectRole}/flatten={flattenExpected}/abort={abort}; " +
                $"observed {i.ChainStatus}/subject={i.SubjectFormKey}/flatten={i.FlattenTemplateChain}/abort={o.Ladder.Abort}.");
        }

        // The carve-outs: even with the toggle on, these must not be flattened.
        foreach (var role in new[] { SpecimenRole.TemplatedLeveled, SpecimenRole.TemplatedUnfollowable })
        {
            var o = cell[role];
            if (o.Ladder == null) continue;
            Add(checks, Ladder, cell, role + " carve-out",
                o.Ladder.NifChoice == FaceGenSourceChoice.None && o.Ladder.DdsChoice == FaceGenSourceChoice.None,
                $"no FaceGen source is chosen for it (nif={o.Ladder.NifChoice}, dds={o.Ladder.DdsChoice}).");
        }
    }

    // ------------------------------------------------------------------ FaceGen on disk (§3b)

    private static void EvaluateFaceGen(CellResult cell, List<MatrixCheck> checks)
    {
        var a = cell[SpecimenRole.TemplatedA];
        var b = cell[SpecimenRole.TemplatedB];
        var terminus = cell[SpecimenRole.Terminus];
        bool ownCopy = cell.Cell.TemplateMode == TemplateHandlingMode.GiveEachNpcOwnCopy;

        if (cell.Cell.UseSkyPatcher)
        {
            // Every target gets its own surrogate FormKey, so each owns a distinct destination path in
            // BOTH settings. What the setting changes here is the surrogate RECORD (see EvaluateRecords):
            // under inherit the surrogate keeps its Traits flag, so the engine reads the terminus's path
            // and the file written at the surrogate's own path is inert.
            Add(checks, FaceGen, cell, "#3 vs #4 (surrogate paths)",
                a.OwnFaceGenHash != null && b.OwnFaceGenHash != null && a.OwnFaceGenHash != b.OwnFaceGenHash,
                $"each surrogate owns a FaceGen file and the two differ: #3={Describe(a)}, #4={Describe(b)}.");
            return;
        }

        if (ownCopy)
        {
            // THE decisive assertion. Two NPCs sharing one terminus, given different mods, each end up
            // with their own face file, and the two files are not the same file.
            Add(checks, FaceGen, cell, "#3 vs #4 must DIFFER",
                a.OwnFaceGenHash != null && b.OwnFaceGenHash != null && a.OwnFaceGenHash != b.OwnFaceGenHash,
                $"#3={Describe(a)}, #4={Describe(b)}. Identical hashes would mean flattening ran but both " +
                "still resolved to the terminus's single shared file.");

            Add(checks, FaceGen, cell, "#3 carries Mod X's copy",
                a.OwnFaceGenSource?.StartsWith(TemplateFixtureBuilder.ModXName, StringComparison.Ordinal) == true,
                $"#3's file came from {a.OwnFaceGenSource ?? "nowhere"}.");
            Add(checks, FaceGen, cell, "#4 carries Mod Y's copy",
                b.OwnFaceGenSource?.StartsWith(TemplateFixtureBuilder.ModYName, StringComparison.Ordinal) == true,
                $"#4's file came from {b.OwnFaceGenSource ?? "nowhere"}.");
        }
        else
        {
            Add(checks, FaceGen, cell, "#3 and #4 get NO file of their own",
                a.OwnFaceGenHash == null && b.OwnFaceGenHash == null,
                $"#3={Describe(a)}, #4={Describe(b)}. While inheriting, anything written under their own " +
                "FormIDs would be inert — the engine reads the terminus's path.");

            Add(checks, FaceGen, cell, "the terminus's path holds the terminus's own choice",
                a.SubjectFaceGenSource?.StartsWith(TemplateFixtureBuilder.ModZName, StringComparison.Ordinal) == true,
                $"the shared path holds {a.SubjectFaceGenSource ?? "nothing"} (Mod Z is #5's own selection).");
        }

        // In every record-mode cell, #5's own path must hold #5's own selection — never #3's or #4's.
        Add(checks, FaceGen, cell, "#5's own path is not stamped by its followers",
            terminus.OwnFaceGenSource?.StartsWith(TemplateFixtureBuilder.ModZName, StringComparison.Ordinal) == true,
            $"#5's path holds {terminus.OwnFaceGenSource ?? "nothing"}.");
    }

    // ------------------------------------------------------------------ output record (§3a)

    private static void EvaluateRecords(CellResult cell, List<MatrixCheck> checks)
    {
        bool ownCopy = cell.Cell.TemplateMode == TemplateHandlingMode.GiveEachNpcOwnCopy;

        foreach (var role in new[] { SpecimenRole.TemplatedA, SpecimenRole.TemplatedB })
        {
            var o = cell[role];
            if (!o.RecordPresent) continue;

            if (ownCopy)
            {
                // TPLT must stay set: it also drives inventory / AI / faction inheritance this app does
                // not touch, so dropping it would be a bug rather than a cleanup.
                Add(checks, Record, cell, role + " is flattened",
                    o.TraitsFlag == false && o.TemplateTarget != null,
                    $"Traits cleared and TPLT still set (traits={o.TraitsFlag}, tplt={o.TemplateTarget ?? "CLEARED"}).");

                Add(checks, Record, cell, role + " carries the terminus's appearance",
                    Math.Abs((o.Height ?? 0) - TemplateFixtureBuilder.ModZHeight) < Tol
                    && o.Weight == TemplateFixtureBuilder.ModZWeight
                    && o.Female == true
                    && o.HeadPartEditorIds.SequenceEqual(new[] { TemplateFixtureBuilder.HeadPartModZ }),
                    $"height={o.Height} weight={o.Weight} female={o.Female} headParts=[{string.Join(",", o.HeadPartEditorIds)}] " +
                    $"— must be the terminus's ({TemplateFixtureBuilder.ModZHeight}/{TemplateFixtureBuilder.ModZWeight}/" +
                    $"True/{TemplateFixtureBuilder.HeadPartModZ}), not the NPC's own mod's.");
            }
            else
            {
                Add(checks, Record, cell, role + " still inherits",
                    o.TraitsFlag == true && o.TemplateTarget != null,
                    $"Traits kept and TPLT set (traits={o.TraitsFlag}, tplt={o.TemplateTarget ?? "CLEARED"}).");
            }
        }

        // The carve-out records must not be flattened whatever the setting says.
        var leveled = cell[SpecimenRole.TemplatedLeveled];
        if (leveled.RecordPresent)
        {
            Add(checks, Record, cell, "#7 (levelled terminus) still inherits",
                leveled.TraitsFlag == true && leveled.TemplateTarget != null,
                $"traits={leveled.TraitsFlag}, tplt={leveled.TemplateTarget ?? "CLEARED"} — there is no fixed face " +
                "to copy from a levelled list, so it must keep inheriting in both settings.");
        }
    }

    // ------------------------------------------------------------------ SkyPatcher (§3c)

    private static void EvaluateSkyPatcher(CellResult cell, List<MatrixCheck> checks)
    {
        foreach (var role in TemplateMatrixSettingsBuilder.SpecimenRoles)
        {
            var o = cell[role];
            if (!o.Processed) continue;

            Add(checks, SkyPatcher, cell, role + " has an .ini directive",
                o.HasIniLine && o.SurrogateFormKey.HasValue,
                $"the .ini carries a copyVisualStyle line pointing at surrogate {o.SurrogateFormKey?.ToString() ?? "NONE"}.");

            Add(checks, SkyPatcher, cell, role + " surrogate is named <EditorID>_Template",
                o.RecordEditorId?.EndsWith("_Template", StringComparison.Ordinal) == true,
                $"surrogate EditorID = '{o.RecordEditorId}'.");
        }
    }

    // ------------------------------------------------------------------ the inertness controls (§3a)

    /// <summary>
    /// The single strongest guard that the feature is inert where it should be: untemplated specimens
    /// (#1, #2) and the two carve-outs (#7 levelled, #8 cycle) must produce byte-identical results under
    /// both template settings of the same output mode.
    /// </summary>
    private static void EvaluateControls(IReadOnlyList<CellResult> cells, List<MatrixCheck> checks)
    {
        var byIndex = cells.ToDictionary(c => c.Cell.Index);

        foreach (var (modeName, inherit, ownCopy) in TemplateMatrixCells.Pairs)
        {
            if (!byIndex.TryGetValue(inherit.Index, out var a) || !byIndex.TryGetValue(ownCopy.Index, out var b))
            {
                continue;
            }

            foreach (var role in new[]
                     {
                         SpecimenRole.PlainSelf, SpecimenRole.PlainShared,
                         SpecimenRole.TemplatedLeveled, SpecimenRole.TemplatedUnfollowable,
                     })
            {
                var x = a[role];
                var y = b[role];
                bool same = x.AppearanceSignature == y.AppearanceSignature && x.OwnFaceGenHash == y.OwnFaceGenHash;
                checks.Add(new MatrixCheck(
                    $"[{Control}] {modeName} / {role}",
                    same,
                    same
                        ? "identical under both template settings, as an untouched NPC must be."
                        : $"DIFFERS across template settings.\n    inherit : {x.AppearanceSignature} facegen={x.OwnFaceGenHash ?? "none"}" +
                          $"\n    own-copy: {y.AppearanceSignature} facegen={y.OwnFaceGenHash ?? "none"}"));
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string Describe(SpecimenObservation o) =>
        o.OwnFaceGenHash == null
            ? "no file"
            : $"{o.OwnFaceGenHash} ({o.OwnFaceGenSource})";

    private static void Add(List<MatrixCheck> checks, string group, CellResult cell, string subject,
        bool passed, string detail) =>
        checks.Add(new MatrixCheck($"[{group}] {cell.Cell.Name} / {subject}", passed, detail));
}
