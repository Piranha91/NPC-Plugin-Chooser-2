using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Which of the five FaceGen-availability cases an NPC falls into, measured against the
/// SUBJECT's FaceGen paths (see <see cref="FaceGenLadderInputs.SubjectFormKey"/>).
/// </summary>
public enum FaceGenLadderRow
{
    /// <summary>The appearance mod ships both halves. The ordinary case.</summary>
    NifAndDds = 1,

    /// <summary>Mesh only — the tint must be sourced elsewhere or the head renders untinted.</summary>
    NifOnly = 2,

    /// <summary>Tint only, and the mod also edits the NPC record. The mesh must come from
    /// somewhere that is head-part-compatible with that record or the face goes dark.</summary>
    DdsOnlyWithRecord = 3,

    /// <summary>Tint only, and the mod ships no record for this NPC (a FaceGen-only mod), so
    /// the record and the mesh both come from the mod of origin.</summary>
    DdsOnlyNoRecord = 4,

    /// <summary>Neither half. Falls through to the same handling as
    /// <see cref="DdsOnlyNoRecord"/>, differing only in where the tint comes from.</summary>
    Neither = 5,
}

/// <summary>Where the output's FaceGen has to land, which differs by patching mode.</summary>
public enum FaceGenDestinationMode
{
    /// <summary>Output overrides the NPC at its own FormKey, so source and destination
    /// FaceGen paths coincide and a conflict winner already sitting there can simply be
    /// left alone.</summary>
    Record,

    /// <summary>Shared/guest appearance: the donor's face lands at the TARGET's path, so
    /// anything reused has to be copied and renamed.</summary>
    FaceSwap,

    /// <summary>The surrogate owns a brand-new FormKey in the output plugin, so nothing in
    /// the load order can fall through to its path — everything must be copied.</summary>
    SkyPatcher,
}

/// <summary>Outcome of walking the appearance donor's Traits template chain.</summary>
public enum FaceGenChainStatus
{
    /// <summary>Donor renders its own face.</summary>
    NotTemplated,

    /// <summary>Donor inherits, and the chain reached a concrete NPC record.</summary>
    Resolved,

    /// <summary>Donor inherits from a Leveled NPC. Normal, not broken: the game picks an actor
    /// from the list at runtime and renders ITS face, so this NPC has no FaceGen of its own to
    /// supply and nothing to abort over. Common — generic encounter actors are built this way,
    /// and a first measurement pass over a real load order found 18 of them.</summary>
    LeveledTerminus,

    /// <summary>Donor inherits but the chain is genuinely broken — a null or dangling template
    /// link, a cycle, or a pathologically long chain. There is no face to evaluate anywhere,
    /// so this aborts.</summary>
    Unfollowable,
}

/// <summary>How a FaceGen half was found (mirrors AssetHandler's private AssetSourceType).</summary>
public enum FaceGenAssetPresence
{
    NotFound,
    LooseFile,
    BsaFile,
}

/// <summary>Which source the ladder settled on for one half of an NPC's FaceGen.</summary>
public enum FaceGenSourceChoice
{
    /// <summary>Nothing usable was found. On the mesh side this aborts; on the tint side it
    /// is a warning (the head renders, just untinted).</summary>
    None,

    /// <summary>The appearance mod the user selected.</summary>
    AppearanceMod,

    /// <summary>The mod that first defines the subject NPC.</summary>
    Origin,

    /// <summary>Whatever currently wins this path in the load order, COPIED to the
    /// destination.</summary>
    Winner,

    /// <summary>Whatever currently wins this path in the load order, left where it is —
    /// only possible in record mode, where the destination path is the one the winner
    /// already occupies.</summary>
    WinnerInPlace,
}

/// <summary>
/// Everything <see cref="FaceGenLadder.Classify"/> needs, gathered by the caller so the
/// decision itself stays a pure function of its inputs and can be unit-tested without an
/// environment, a mod list, or a disk.
/// </summary>
/// <param name="SubjectFormKey">
/// The NPC whose FaceGen the engine actually loads: the appearance donor, or the end of its
/// Traits template chain. Every source lookup below is measured at THIS FormKey's paths.
/// </param>
/// <param name="SourceHasPluginRecord">
/// False for a FaceGen-only selection (the appearance mod ships assets but no record for this
/// NPC), which is what separates row 3 from row 4.
/// </param>
/// <param name="OriginNifCompatible">
/// Whether the mod-of-origin's mesh has a baked shape for every geometry-bearing head part the
/// final patched record resolves to. Null when the check was not run — classification then
/// proceeds optimistically and the decision is flagged
/// <see cref="FaceGenLadderDecision.CompatibilityEvaluated"/> = false, so a cheap baseline run
/// can classify without parsing NIFs.
/// </param>
/// <param name="FlattenTemplateChain">
/// True when the user's Template Handling Mode is "give each NPC its own copy". For a
/// <see cref="FaceGenChainStatus.Resolved"/> chain the FaceGen destination is then the NPC's OWN
/// path instead of the terminus's shared one, so a load-order winner sitting at the subject's
/// path is no longer "already at the destination" — it has to be copied. Does not change where
/// FaceGen is SOURCED from (always the subject's paths), and is irrelevant to untemplated NPCs.
/// </param>
/// <param name="SourceNifCompatible">
/// Whether the APPEARANCE MOD's own mesh fits the record that ships. Only evaluated for the one
/// shape where the two have different authors — row 2 with a FaceGen-only selection, which pairs
/// the mod's mesh with the origin's record — and null everywhere else. Never gates: it feeds
/// <see cref="FaceGenLadderDecision.ModMeshFailedCompatCheck"/>, the same warn-don't-gate stance
/// rows 4/5 take for the mirrored pairing.
/// </param>
/// <param name="CompatProbeNotes">
/// The evidence behind any failed compatibility probe, composed by the Patcher for the detailed
/// warning log: the graded record's identity, the override chains of the NPC record and its race
/// (naming which plugins rewrite the head data), and each failed probe's record-needs versus
/// mesh-bakes lists. Null when every probe passed or none ran. Rides
/// <see cref="FaceGenLadderDecision.TechnicalSummary"/>; deliberately NOT written to the diag
/// CSV, where multi-line evidence would bloat every row.
/// </param>
public sealed record FaceGenLadderInputs(
    string NpcIdentifier,
    FormKey TargetFormKey,
    FormKey DonorFormKey,
    FormKey SubjectFormKey,
    FaceGenChainStatus ChainStatus,
    string ModName,
    FaceGenDestinationMode Mode,
    FaceGenAssetPresence SourceNif,
    FaceGenAssetPresence SourceDds,
    bool SourceHasPluginRecord,
    bool OriginRecordExists,
    FaceGenAssetPresence OriginNif,
    FaceGenAssetPresence OriginDds,
    bool WinnerNifExists,
    string? WinnerNifOwner,
    bool WinnerDdsExists,
    bool? OriginNifCompatible,
    bool? WinnerNifCompatible,
    FaceGenAssetPresence LegacyDonorNif,
    FaceGenAssetPresence LegacyDonorDds,
    string ChainTrace = "",
    bool FlattenTemplateChain = false,
    bool? SourceNifCompatible = null,
    string? CompatProbeNotes = null);

/// <summary>
/// The ladder's verdict for one NPC. <see cref="LegacyAction"/> records what the pre-ladder
/// code path does with the same inputs, so a single run yields the before/after diff without
/// needing a separate baseline run.
/// </summary>
public sealed record FaceGenLadderDecision(
    FaceGenLadderInputs Inputs,
    FaceGenLadderRow Row,
    FaceGenSourceChoice NifChoice,
    FaceGenSourceChoice DdsChoice,
    bool ForwardOriginRecord,
    bool Abort,
    string? AbortReason,
    bool CompatibilityEvaluated,
    string PlannedAction,
    string LegacyAction,
    string LogLine)
{
    /// <summary>True when nothing was copied because the output record keeps inheriting — see
    /// <see cref="FaceGenLadder.KeepsInheritedFace"/>. Named at the end of the run by
    /// <c>Patcher.ReportInheritedFaceNpcs</c> rather than passing silently: the user picked a mod
    /// for this NPC and it could not reach it.</summary>
    public bool InheritedFaceLeftToTemplate => FaceGenLadder.KeepsInheritedFace(Inputs);

    /// <summary>
    /// True when this NPC's Traits chain WAS flattened but the selected mod supplied neither half
    /// of the face, so what lands on its own record came from the mod of origin or from the
    /// load-order winner instead.
    ///
    /// <para>The user's selection is as undeliverable here as it is under
    /// <see cref="InheritedFaceLeftToTemplate"/> — the NPC ends up wearing the face it would have
    /// worn anyway — so it earns the same forced end-of-run naming rather than a verbose-only line
    /// (<c>Patcher.ReportFlattenedFallbackNpcs</c>). Settled 2026-07-30: own-copy with nothing to
    /// copy should read exactly like inheriting from a template that has no selection of its own.</para>
    ///
    /// <para>Requires BOTH halves to come from elsewhere. A tint-only mod (row 3/4) really is
    /// applying the user's choice to the face — only the geometry is borrowed — and reporting that
    /// as undeliverable would be wrong.</para>
    /// </summary>
    public bool FlattenedFaceCameFromElsewhere =>
        Inputs.FlattenTemplateChain
        && Inputs.ChainStatus == FaceGenChainStatus.Resolved
        && !Abort
        && NifChoice != FaceGenSourceChoice.AppearanceMod
        && DdsChoice != FaceGenSourceChoice.AppearanceMod;

    /// <summary>
    /// True when a face mesh is being copied but no tint could be found for it anywhere — the one
    /// outcome here the user has to act on, so it is reported after the run by
    /// <see cref="NpcWarningReporter"/> (<see cref="NpcWarningKind.MissingFaceTint"/>) instead of
    /// riding the verbose-gated <see cref="LogLine"/>. Derived rather than set per branch so every
    /// route that lands on a tintless mesh reports it, including the borrowed-winner mesh whose
    /// own sentence says nothing about tint. Deliberately requires a mesh: an NPC taking its face
    /// from a levelled list also has no tint, and there it is the correct, silent outcome.
    /// </summary>
    public bool MissingTintEverywhere =>
        DdsChoice == FaceGenSourceChoice.None && NifChoice != FaceGenSourceChoice.None;

    /// <summary>
    /// True when the origin's mesh was taken even though the compatibility probe POSITIVELY said
    /// it does not fit the record that ships. Rows 4/5 deliberately do not GATE on the probe
    /// (decided 2026-07-30): a mod that ships no face mesh is almost always authored against the
    /// origin's data, so refusing the pairing would abort NPCs that render fine. The one known way
    /// the assumption breaks in the wild is another mod overriding the subject's ORIGIN data — RS
    /// Children rewriting a child race's head parts is the measured case (docs/KnownLimitations.md
    /// #5) — so the NPC is named in the end-of-run warning report
    /// (<see cref="NpcWarningKind.OriginMeshCompatibility"/>) with the suggestion to spawn-test
    /// it. Row 3 gates the identical pairing instead of warning, so it never sets this; false too
    /// when the probe passed or never ran.
    /// </summary>
    public bool OriginMeshFailedCompatCheck =>
        NifChoice == FaceGenSourceChoice.Origin && Inputs.OriginNifCompatible == false;

    /// <summary>
    /// True when the APPEARANCE MOD's own mesh ships even though the compatibility probe
    /// POSITIVELY said it does not fit the record that ships. The probe only runs for row 2 with
    /// a FaceGen-only selection — the mod's mesh against the origin's record, the mirror image of
    /// the rows-4/5 pairing <see cref="OriginMeshFailedCompatCheck"/> covers — and takes the same
    /// warn-don't-gate stance: the mesh is used, and the NPC is named in the end-of-run warning
    /// report (<see cref="NpcWarningKind.ModMeshCompatibility"/>). A mod that ships its mesh AND
    /// its own record is self-consistent by authorship and is never probed, like row 1.
    /// </summary>
    public bool ModMeshFailedCompatCheck =>
        NifChoice == FaceGenSourceChoice.AppearanceMod && Inputs.SourceNifCompatible == false;

    /// <summary>
    /// The decision's full context as plain text, for the detailed warning log
    /// (<see cref="NpcWarningReporter"/>): everything a maintainer would otherwise ask a user to
    /// produce by re-running with the FaceGenLadder.csv trigger — identifiers, row, mode, the
    /// planned action, each source's presence and compatibility verdict, and the chain trace.
    /// Line shapes follow the contract of
    /// <see cref="NpcWarningReporter.ClassifyTechnicalLines"/>, which renders them as field
    /// rows and a source-comparison table in the HTML report.
    /// </summary>
    public string TechnicalSummary =>
        $"decision: row={(int)Row} ({Row}), mode={Inputs.Mode}\n" +
        $"planned: {PlannedAction}\n" +
        $"target={Inputs.TargetFormKey} donor={Inputs.DonorFormKey} subject={Inputs.SubjectFormKey} " +
        $"chain={Inputs.ChainStatus}{(Inputs.FlattenTemplateChain ? " (flattened)" : string.Empty)}\n" +
        $"selected mod '{Inputs.ModName}': record={(Inputs.SourceHasPluginRecord ? "yes" : "no")}, " +
        $"nif={Inputs.SourceNif}, dds={Inputs.SourceDds}, meshCompat={Tri(Inputs.SourceNifCompatible)}\n" +
        $"origin: record={(Inputs.OriginRecordExists ? "yes" : "no")}, nif={Inputs.OriginNif}, " +
        $"dds={Inputs.OriginDds}, meshCompat={Tri(Inputs.OriginNifCompatible)}\n" +
        $"winner: nif={(Inputs.WinnerNifExists ? $"yes ({Inputs.WinnerNifOwner})" : "no")}, " +
        $"dds={(Inputs.WinnerDdsExists ? "yes" : "no")}, meshCompat={Tri(Inputs.WinnerNifCompatible)}" +
        (string.IsNullOrWhiteSpace(Inputs.ChainTrace) ? string.Empty : $"\nchain trace: {Inputs.ChainTrace}") +
        (string.IsNullOrWhiteSpace(Inputs.CompatProbeNotes) ? string.Empty : $"\n{Inputs.CompatProbeNotes}");

    private static string Tri(bool? v) => v is null ? "NotEvaluated" : v.Value.ToString();
}

/// <summary>
/// Decides where each half of an NPC's FaceGen should come from, and whether the NPC can be
/// patched at all.
///
/// <para>The problem it solves: an appearance mod may ship a face tint without the matching
/// mesh (or the reverse). Nothing forwards the missing half today — the asset lookup is scoped
/// to the selected mod — so in record mode the game silently falls through to whatever wins
/// that path in the load order, and in the other modes it gets nothing at all. This makes the
/// choice explicit, and refuses to patch an NPC whose face would provably be broken.</para>
///
/// <para>Two invariants drive the whole table. First, everything is measured at the SUBJECT's
/// paths — the end of the donor's Traits chain — because that is the record the engine builds
/// a face from; a templated NPC's own head data is inert. Second, a mesh is only acceptable if
/// it is head-part-compatible with the record that will ship, since the engine reconciles baked
/// shape names against resolved head-part EditorIDs and renders an untinted head when that
/// fails.</para>
///
/// <para>Pure by construction: presence, compatibility and chain resolution all arrive as
/// inputs. Callers do the I/O.</para>
/// </summary>
public static class FaceGenLadder
{
    /// <summary>In record mode the destination path normally IS the path the load-order winner
    /// already occupies, so "use the winner" means copying nothing. Flattening a resolved
    /// template chain breaks that coincidence: the destination becomes the NPC's own path while
    /// the winner sits at the terminus's shared one, so the winner's bytes must be copied.</summary>
    private static bool WinnerAlreadyAtDestination(FaceGenLadderInputs i) =>
        i.Mode == FaceGenDestinationMode.Record
        && !(i.FlattenTemplateChain && i.ChainStatus == FaceGenChainStatus.Resolved);

    /// <summary>
    /// Whether this NPC's face is left entirely to its template. The output record still inherits
    /// (the Traits flag is kept), so the engine never opens this NPC's own FaceGen path — it opens
    /// the TERMINUS's, which is another NPC's record that this pass does not patch.
    ///
    /// <para>The rule underneath: a FaceGen file may only be written to a FormKey's path by the
    /// pass that patches that FormKey's record. The engine reconciles a mesh's baked shape names
    /// against the head parts of the record sitting at that path, so writing a mod's mesh under a
    /// record nobody patched pairs the two from different sources — the dark-face bug, landing on
    /// an NPC the user never selected. An inheritor can satisfy that rule for no path at all, so it
    /// writes nothing and keeps showing its template's face, which is what
    /// <c>TemplateHandlingMode.InheritFromTemplate</c> promises. Where the terminus has an
    /// appearance selection of its own, that NPC's own pass writes both halves and this one
    /// inherits the result.</para>
    ///
    /// <para>SkyPatcher is excluded: its destination is the surrogate's own path, a record this
    /// pass does write. That combination is inert rather than wrong, and
    /// <c>Validator.CanSkyPatcherApplyAppearance</c> rejects it per NPC before it reaches here.</para>
    /// </summary>
    public static bool KeepsInheritedFace(FaceGenLadderInputs i) =>
        i.Mode != FaceGenDestinationMode.SkyPatcher
        && i.ChainStatus == FaceGenChainStatus.Resolved
        && !i.FlattenTemplateChain;

    public static FaceGenLadderDecision Classify(FaceGenLadderInputs i)
    {
        string legacy = DescribeLegacy(i);

        // A levelled terminus is not a failure: the game resolves the actor at runtime and draws
        // its face, so there is no FaceGen for this NPC to own and nothing for the ladder to do.
        // Treating it as unfollowable would refuse to patch a large, perfectly healthy population
        // of generic encounter actors.
        if (i.ChainStatus == FaceGenChainStatus.LeveledTerminus)
        {
            return Build(i, FaceGenLadderRow.Neither, legacy,
                FaceGenSourceChoice.None, FaceGenSourceChoice.None, false,
                $"{i.NpcIdentifier} takes its appearance from a levelled list, so the game picks a " +
                $"face for it at runtime. No face files are needed and none were copied.");
        }

        // An unfollowable chain means there is no face to evaluate anywhere — not the donor's
        // (inert, it inherits) and not a terminus (there isn't one). Nothing downstream can
        // recover, so stop before any record is written.
        if (i.ChainStatus == FaceGenChainStatus.Unfollowable)
        {
            return Abort(i, FaceGenLadderRow.Neither, legacy,
                "this NPC inherits its appearance from another NPC, but that chain could not be " +
                "followed (the template is missing, points at a levelled NPC, or loops)");
        }

        // The output record keeps inheriting, so there is no path this pass may write to — see
        // KeepsInheritedFace. Sourcing is skipped entirely rather than resolved and then discarded:
        // the rows below all answer "where does the mesh come from", and the answer cannot matter
        // when there is nowhere to put it. Not an abort — the NPC is patched normally and simply
        // keeps showing its template's face.
        if (KeepsInheritedFace(i))
        {
            return Build(i, FaceGenLadderRow.Neither, legacy,
                FaceGenSourceChoice.None, FaceGenSourceChoice.None, false,
                $"{i.NpcIdentifier} takes its appearance from another NPC, and Templated NPCs is set to " +
                $"use the template's appearance, so nothing from '{i.ModName}' can reach it. No face " +
                $"files were copied: they would have to be written under the template's name, which " +
                $"would break that NPC's face as well as leaving this one unchanged.");
        }

        bool hasNif = i.SourceNif != FaceGenAssetPresence.NotFound;
        bool hasDds = i.SourceDds != FaceGenAssetPresence.NotFound;

        var row = (hasNif, hasDds, i.SourceHasPluginRecord) switch
        {
            (true, true, _) => FaceGenLadderRow.NifAndDds,
            (true, false, _) => FaceGenLadderRow.NifOnly,
            (false, true, true) => FaceGenLadderRow.DdsOnlyWithRecord,
            (false, true, false) => FaceGenLadderRow.DdsOnlyNoRecord,
            (false, false, _) => FaceGenLadderRow.Neither,
        };

        return row switch
        {
            FaceGenLadderRow.NifAndDds => Build(i, row, legacy,
                FaceGenSourceChoice.AppearanceMod, FaceGenSourceChoice.AppearanceMod, false,
                $"'{i.ModName}' supplies both the face mesh and the face tint."),

            FaceGenLadderRow.NifOnly => ResolveNifOnly(i, row, legacy),

            FaceGenLadderRow.DdsOnlyWithRecord => ResolveDdsOnlyWithRecord(i, row, legacy),

            _ => ResolveOriginFallback(i, row, legacy),
        };
    }

    // Row 2 — mesh present, tint missing.
    //
    // The origin is tried FIRST, on the principle that a mod shipping one half of an NPC's FaceGen
    // is signalling that it expects the origin's counterpart for the other. The load-order winner
    // is only a fallback: preferring it would make the result depend on whatever else happens to
    // be installed, where the origin is a deterministic, authored pairing.
    //
    // (This was briefly inverted to winner-first, on a mistaken reading of an in-game report of
    // missing hair/brow/eye textures. Those head parts carry their own TextureSets and are not
    // regions of the face tint, so no choice made here could have affected them. That case was
    // ModSetting-scoped asset resolution instead — see docs/KnownLimitations.md #4.)
    //
    // Neither source being available is a warning rather than an abort: an untinted head still
    // renders, which beats refusing to patch.
    private static FaceGenLadderDecision ResolveNifOnly(
        FaceGenLadderInputs i, FaceGenLadderRow row, string legacy)
    {
        if (i.OriginDds != FaceGenAssetPresence.NotFound)
        {
            return Build(i, row, legacy, FaceGenSourceChoice.AppearanceMod, FaceGenSourceChoice.Origin, false,
                $"'{i.ModName}' supplies the face mesh but no face tint, so the tint is being taken " +
                $"from the mod that originally added this NPC. If the skin tone looks wrong, that mod " +
                $"is the reason.");
        }

        if (i.WinnerDdsExists)
        {
            // Same record-mode subtlety as the mesh side: when the winning tint already sits at
            // the destination path, using it means copying nothing.
            var choice = WinnerAlreadyAtDestination(i)
                ? FaceGenSourceChoice.WinnerInPlace
                : FaceGenSourceChoice.Winner;

            return Build(i, row, legacy, FaceGenSourceChoice.AppearanceMod, choice, false,
                $"'{i.ModName}' supplies the face mesh but no face tint, and the mod that originally " +
                $"added this NPC has none either, so the tint already installed by another mod is being " +
                $"used. If the skin tone looks wrong, that is why.");
        }

        // The discoloured-face consequence rides on MissingTintEverywhere (reported after the run
        // by NpcWarningReporter) rather than being stated here, where it would be verbose-gated.
        return Build(i, row, legacy, FaceGenSourceChoice.AppearanceMod, FaceGenSourceChoice.None, false,
            $"'{i.ModName}' supplies the face mesh but no face tint, and no replacement tint could be " +
            $"found.");
    }

    // Row 3 — tint present, mesh missing, and the mod edits the record. The mesh has to match
    // that record's head parts or the tint pass fails and the face renders dark, so each
    // candidate is gated on compatibility rather than mere existence.
    private static FaceGenLadderDecision ResolveDdsOnlyWithRecord(
        FaceGenLadderInputs i, FaceGenLadderRow row, string legacy)
    {
        bool originUsable = i.OriginNif != FaceGenAssetPresence.NotFound
                            && (i.OriginNifCompatible ?? true);

        if (originUsable)
        {
            return Build(i, row, legacy, FaceGenSourceChoice.Origin, FaceGenSourceChoice.AppearanceMod, false,
                $"'{i.ModName}' supplies a face tint but no face mesh, so the mesh is being taken from " +
                $"the mod that originally added this NPC. This is the normal shape of a tint-only mod.");
        }

        bool winnerUsable = i.WinnerNifExists && (i.WinnerNifCompatible ?? true);
        if (winnerUsable)
        {
            string owner = string.IsNullOrWhiteSpace(i.WinnerNifOwner) ? "another mod" : $"'{i.WinnerNifOwner}'";

            // When the destination IS the path the winner already occupies, the correct action is
            // to copy nothing and let the game resolve it as it already does. The other cases
            // retarget the output to a different FormKey's path, so the same bytes have to be
            // copied under the new name.
            return WinnerAlreadyAtDestination(i)
                ? Build(i, row, legacy, FaceGenSourceChoice.WinnerInPlace, FaceGenSourceChoice.AppearanceMod, false,
                    $"'{i.ModName}' supplies a face tint but no face mesh, and the original mod's mesh does " +
                    $"not fit. The face mesh already installed by {owner} does fit, so it is being left in " +
                    $"place and used as-is.")
                : Build(i, row, legacy, FaceGenSourceChoice.Winner, FaceGenSourceChoice.AppearanceMod, false,
                    $"'{i.ModName}' supplies a face tint but no face mesh, and the original mod's mesh does " +
                    $"not fit. The face mesh installed by {owner} does fit, so it is being copied for this NPC.");
        }

        return Abort(i, row, legacy,
            $"'{i.ModName}' supplies a face tint but no face mesh, and no other installed mesh matches " +
            $"this NPC's head parts. Patching it would produce the dark-face bug, so it is being left " +
            $"unchanged. Pick a different mod for this NPC, or install one that provides its face mesh.");
    }

    // Rows 4 and 5 — the mod ships no record for this NPC, so the record and the mesh both come
    // from the mod of origin and are compatible with each other by construction. The winner is
    // still worth trying when the origin has no mesh, but there it must be checked, because it
    // would be paired with the origin's record rather than its own.
    private static FaceGenLadderDecision ResolveOriginFallback(
        FaceGenLadderInputs i, FaceGenLadderRow row, string legacy)
    {
        // Row 4 is FaceGen-only by definition, but row 5 arrives here either way — a mod can edit
        // the record and still ship no face files at all. Only the former hands the record over to
        // the origin; the latter keeps the mod's own edits, and that distinction has to be honest
        // because it decides which record a borrowed mesh is checked for compatibility against.
        bool forwardOriginRecord = !i.SourceHasPluginRecord;

        if (forwardOriginRecord && !i.OriginRecordExists)
        {
            return Abort(i, row, legacy,
                $"'{i.ModName}' does not change this NPC's appearance record, and the mod that " +
                $"originally added the NPC could not be read, so there is nothing to copy from.");
        }

        // Row 5 has no tint from the appearance mod either, so it falls back the same way row 2 does.
        // Row 5's tint falls back the same way row 2's does, and in the same order: origin first,
        // since that is the pairing the mod implies, with the winner only as a backstop.
        var ddsChoice = FaceGenSourceChoice.AppearanceMod;
        if (row == FaceGenLadderRow.Neither)
        {
            ddsChoice = i.OriginDds != FaceGenAssetPresence.NotFound
                ? FaceGenSourceChoice.Origin
                : i.WinnerDdsExists
                    ? (WinnerAlreadyAtDestination(i)
                        ? FaceGenSourceChoice.WinnerInPlace
                        : FaceGenSourceChoice.Winner)
                    : FaceGenSourceChoice.None;
        }

        if (i.OriginNif != FaceGenAssetPresence.NotFound)
        {
            // Deliberately UNGATED, unlike row 3 (decided 2026-07-30): a mod that ships no mesh is
            // almost always authored against the origin's data, and gating here would refuse NPCs
            // that render fine. When the probe has POSITIVELY said the pairing does not fit — in
            // practice another mod overriding the subject's origin data out from under it, RS
            // Children being the one known wild case — OriginMeshFailedCompatCheck carries the
            // fact to the end-of-run warning report instead of vetoing the copy. A missing tint
            // rides on MissingTintEverywhere the same way; neither consequence is stated in this
            // verbose-gated sentence.
            return Build(i, row, legacy, FaceGenSourceChoice.Origin, ddsChoice, forwardOriginRecord,
                forwardOriginRecord
                    ? $"'{i.ModName}' does not change this NPC's appearance record, so both the record and " +
                      $"the face mesh are being taken from the mod that originally added the NPC."
                    : $"'{i.ModName}' changes this NPC's appearance but ships no face files for it, so the " +
                      $"face mesh is being taken from the mod that originally added the NPC.");
        }

        if (i.WinnerNifExists && (i.WinnerNifCompatible ?? true))
        {
            string owner = string.IsNullOrWhiteSpace(i.WinnerNifOwner) ? "another mod" : $"'{i.WinnerNifOwner}'";
            var choice = WinnerAlreadyAtDestination(i)
                ? FaceGenSourceChoice.WinnerInPlace
                : FaceGenSourceChoice.Winner;

            return Build(i, row, legacy, choice, ddsChoice, forwardOriginRecord,
                $"'{i.ModName}' ships no face mesh for this NPC and neither does the mod that originally " +
                $"added it, so the mesh installed by {owner} is being used.");
        }

        return Abort(i, row, legacy,
            $"no face mesh could be found for this NPC in '{i.ModName}', in the mod that originally " +
            $"added it, or anywhere else in your load order, so it is being left unchanged.");
    }

    /// <summary>
    /// What the pre-ladder code does with the same NPC: it derives both FaceGen paths from the
    /// DONOR's FormKey (not the subject's, so a templated donor resolves to a path that by
    /// definition holds nothing), looks only inside the selected mod, and silently copies
    /// whichever halves it happens to find.
    /// </summary>
    private static string DescribeLegacy(FaceGenLadderInputs i)
    {
        bool nif = i.LegacyDonorNif != FaceGenAssetPresence.NotFound;
        bool dds = i.LegacyDonorDds != FaceGenAssetPresence.NotFound;
        return (nif, dds) switch
        {
            (true, true) => "CopyNifAndDds",
            (true, false) => "CopyNifOnly",
            (false, true) => "CopyDdsOnly",
            (false, false) => "CopyNothing",
        };
    }

    private static FaceGenLadderDecision Build(
        FaceGenLadderInputs i, FaceGenLadderRow row, string legacy,
        FaceGenSourceChoice nif, FaceGenSourceChoice dds, bool forwardOriginRecord, string logLine) =>
        new(i, row, nif, dds, forwardOriginRecord, false, null,
            CompatibilityEvaluated: i.OriginNifCompatible.HasValue || i.WinnerNifCompatible.HasValue
                                    || i.SourceNifCompatible.HasValue,
            PlannedAction: $"nif={nif}, dds={dds}" + (forwardOriginRecord ? ", record=Origin" : string.Empty),
            LegacyAction: legacy,
            LogLine: logLine);

    private static FaceGenLadderDecision Abort(
        FaceGenLadderInputs i, FaceGenLadderRow row, string legacy, string reason) =>
        new(i, row, FaceGenSourceChoice.None, FaceGenSourceChoice.None, false, true, reason,
            CompatibilityEvaluated: i.OriginNifCompatible.HasValue || i.WinnerNifCompatible.HasValue
                                    || i.SourceNifCompatible.HasValue,
            PlannedAction: "Abort",
            LegacyAction: legacy,
            LogLine: $"Skipping {i.NpcIdentifier}: {reason}");
}
