using System.Collections.Concurrent;
using System.Text;
using SkyRender;
using SkyRender.Character;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Detects mismatches between a baked FaceGen geometry NIF and the head parts an
/// NPC actually references in a given resolution context. The Creation Kit bakes
/// each head part's morphed geometry into the facegeom .nif as a sub-shape named
/// after that head part's EditorID; in-game, the engine reconciles the NPC's
/// resolved head parts against those baked shapes (keyed on the EditorID / shape
/// name) when it applies the face tint. When that reconciliation fails the head
/// renders untinted — the classic dark/grey "black face" bug.
///
/// <para>This analyzer reproduces the reconciliation as a static check and reports
/// four general failure classes, regardless of root cause (records and mesh coming
/// from different mods because a plugin or file conflict was lost, a same-named plugin
/// loaded at the wrong version, a missing master, a null head part link, or a
/// mod author shipping a .nif that simply doesn't match its plugin). The shape of the
/// disagreement discriminates those causes — see <see cref="MismatchKind"/>, which
/// drives the wording and the remedies in <see cref="Result.BuildReason"/>:</para>
/// <list type="bullet">
///   <item><b>Missing baked shape</b> — a geometry-bearing head part the NPC
///   resolves to has no shape of that EditorID baked into the .nif (proven
///   dark-face trigger on an unmodified engine — see <see cref="Result.HasMismatch"/>).</item>
///   <item><b>Unresolved link</b> — a non-null head part FormKey does not resolve
///   in the current load order (missing plugin/master, or a wrong-version plugin
///   that lacks the FormID).</item>
///   <item><b>Null link</b> — a null entry in the head part list (data error).</item>
///   <item><b>Orphan baked shape</b> — the .nif contains a baked head-part-like
///   shape with no matching resolved head part. The comparison set is the full
///   baked set (head mesh, the NPC's explicit head parts, their recursive Extra
///   Parts, and the race's defaults for slot types the NPC does not set), so an
///   orphan is real evidence that the .nif and the record came from different
///   mods. It does not set <see cref="Result.HasMismatch"/> and is never reported
///   on its own — the proven dark-face trigger is the forward direction, and an
///   extra baked shape is clean-engine-verified inert (matrix variant A6 — see the
///   remarks on <see cref="Result.HasMismatch"/>). It appears only as corroborating
///   detail alongside a head part that IS missing.</item>
/// </list>
/// Caller-agnostic: the head part resolution strategy is supplied as a delegate so
/// the same analyzer serves both the load-order link cache (Validate Output) and
/// mod-scoped resolution (mugshot generation).
/// </summary>
public sealed class FaceGenConsistencyAnalyzer
{
    private readonly NifMeshBuilder _meshBuilder;
    private readonly GameAssetResolver _assetResolver;

    // Session cache of the (expensive) NIF survey, keyed by path + size + mtime so a
    // re-validation or a mugshot re-generation doesn't re-parse an unchanged FaceGen.
    // Entries are tiny (a handful of short shape names) and never mutated after creation,
    // so concurrent readers are safe — ready for a future parallel validation loop.
    private readonly ConcurrentDictionary<string, CachedSurvey> _surveyCache = new();

    private sealed record CachedSurvey(bool LoadOk, string? PrimaryHeadShapeName, HashSet<string> ShapeNames, string? Error);

    public FaceGenConsistencyAnalyzer(CharacterPreviewCache previewCache)
    {
        // Reuse the shared mesh builder the rest of the rendering pipeline uses
        // (same wiring as FaceGenAnalysisCache) rather than constructing a parallel parser.
        _meshBuilder = previewCache.MeshBuilder;
        _assetResolver = previewCache.Assets.Resolver;
    }

    /// <summary>Surveys the NIF's baked shape names, memoized by (path, length, mtime).
    /// On a cache hit no parse happens; a content change (different size/mtime) misses
    /// and re-parses, so a stale entry can't mask a regenerated FaceGen.</summary>
    private CachedSurvey GetSurvey(string nifPath)
    {
        string key;
        try
        {
            // Through the resolver: a BSA asset served from memory has a resolved path
            // but no file, and its length/mtime come from the archive.
            key = _assetResolver.AssetFileExists(nifPath)
                ? System.IO.Path.GetFullPath(nifPath) + "|" + _assetResolver.GetResolvedLength(nifPath) + "|" +
                  _assetResolver.GetResolvedLastWriteTicks(nifPath)
                : nifPath + "|missing";
        }
        catch
        {
            key = nifPath; // unstat-able path: fall back to path-only (still correct, just less precise)
        }

        if (_surveyCache.TryGetValue(key, out var hit)) return hit;

        var survey = _meshBuilder.SurveyNif(nifPath);
        var names = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (survey.LoadOk)
        {
            foreach (var s in survey.Shapes)
                if (!string.IsNullOrWhiteSpace(s.ShapeName)) names.Add(s.ShapeName.Trim());
        }
        var entry = new CachedSurvey(survey.LoadOk, survey.PrimaryHeadShapeName, names, survey.Error);
        _surveyCache[key] = entry;
        return entry;
    }

    public readonly record struct HeadPartRef(FormKey FormKey, string EditorId)
    {
        /// <summary>True when this part entered the effective set through the RACE's chargen
        /// default head parts (a slot the NPC's own record does not occupy) rather than the
        /// NPC's own head part list. The distinction matters for cause attribution: a missing
        /// race-default part means the winning RACE record disagrees with the mesh — a race
        /// edit was lost (e.g. an RS Children-style plugin merged away) or another mod
        /// overrides the race — while the NPC's own record may match the mesh perfectly.</summary>
        public bool FromRaceDefaults { get; init; }

        /// <summary>The part's slot Type as resolved (Brows, Eyes, Hair, …), when known.</summary>
        public HeadPart.TypeEnum? Type { get; init; }

        /// <summary>True when this part entered the effective set through another part's
        /// ExtraParts list (hairlines, beard underlays) rather than as a top-level part.
        /// Extras reconcile by PRESENCE, not name — see the remarks on
        /// <see cref="Result.HasMismatch"/>.</summary>
        public bool FromExtraParts { get; init; }

        /// <summary>The top-level part this entry belongs to: itself for a top-level part,
        /// the root ancestor for parts reached via ExtraParts. Groups an extra with its
        /// siblings for the presence check.</summary>
        public FormKey AncestorFormKey { get; init; }

        /// <summary>The part's own model file path (Model.File), when it has one. Fuels the
        /// shared-model clause of the extras presence check: a vanilla-convention "_1bit"
        /// twin references the SAME mesh as its parent, so the parent's baked shape already
        /// carries its geometry — see <see cref="IsExtraPresenceSatisfied"/>.</summary>
        public string? ModelPath { get; init; }

        /// <summary>For a MISSING baked shape: the orphan baked shape(s) of the SAME slot
        /// Type that the .nif carries instead (e.g. record asks for BrowsMaleHumanoid04,
        /// the .nif was baked with BrowsMaleHumanoid01). Best-effort — requires the
        /// caller's EditorID→head-part resolver to type the orphan shapes; null when no
        /// same-type counterpart was identified. A list so <see cref="Result.BuildReason"/>
        /// can subtract these from the orphan section instead of naming shapes twice.</summary>
        public IReadOnlyList<string>? BakedSameTypeShapes { get; init; }
    }

    /// <summary>How faithfully the deployed record + mesh reproduce the selected mod, when the
    /// caller can prove it. Drives cause attribution in <see cref="Result.BuildReason"/>: once
    /// delivery is known-faithful, "your load order overwrote it" is the one cause that is
    /// ruled OUT, and the mismatch must be inherent to the data the selection supplies.</summary>
    public enum DeliveryFidelity
    {
        /// Delivery not verified (or verified unfaithful) — keep the conflict-first remedies.
        Unknown,

        /// The deployed mesh is byte-identical to the selected mod's own file, the output
        /// record is winning, and the mod supplies both record and mesh: the mod's own
        /// .esp and .nif disagree with each other.
        SelectedModOwnData,

        /// Same fidelity, but the selection is FaceGen-only (the mod ships no plugin record
        /// for this NPC): the mod's mesh is paired with the origin plugin's record by design,
        /// and the pairing does not fit.
        SelectedModMeshOnly,

        /// Same fidelity, and the "mod" is the synthetic Base Game / Creation Club entry:
        /// the mismatch is in the base game's own shipped data.
        VanillaOwnData,
    }

    /// <summary>What the evidence most likely means, used to pick the headline and the
    /// remedies in <see cref="Result.BuildReason"/>. The analyzer only observes that the
    /// baked shapes and the resolved head parts disagree; the <i>shape</i> of the
    /// disagreement is what discriminates the causes.</summary>
    public enum MismatchKind
    {
        /// Nothing to report.
        None,

        /// A single head part slot differs (one unmatched head part, at most one unmatched
        /// baked shape). Small, surgical drift — consistent with the FaceGen having been
        /// generated against a different version of the same plugin.
        SingleHeadPartDifference,

        /// Several head parts and/or a whole set of unmatched baked shapes disagree: the mesh
        /// was baked for a different appearance than the records currently resolve to, i.e.
        /// the records and the mesh are coming from different mods.
        DifferentSource,

        /// No baked-shape disagreement — the head part list itself is broken (unresolved
        /// FormKeys or null entries).
        BrokenHeadPartLinks,

        /// Only unmatched baked shapes, with every resolved head part accounted for. The .nif is
        /// carrying head parts the record never asks for, which usually means .nif and record
        /// came from different mods — but this direction is not the dark-face trigger (see
        /// <see cref="Result.HasMismatch"/> for the evidence) and produces nothing the user can
        /// see in game, so it is NOT reported: <see cref="Result.BuildReason"/> returns empty for
        /// it. The classification exists for diagnostics only.
        ExtraBakedShapesOnly,
    }

    /// <summary>Which resolution context produced the analysis, so the remedies name the
    /// right knobs: the live load order (Validate Output) or a single mod's own plugins
    /// (mugshot/preview generation).</summary>
    public enum ReasonScope
    {
        /// Head parts resolved through the live load order — conflict winners apply.
        LoadOrder,

        /// Head parts resolved mod-scoped (the tile's own mod first, load order as fallback).
        SelectedMod,
    }

    // Fixed filenames across LE/SE/AE/VR. Used only to sharpen the wording when every
    // unmatched head part is vanilla — a strong tell that the NPC's record is simply not
    // being overridden by the appearance mod at all.
    private static readonly HashSet<string> BaseGameMasters = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
    };

    public sealed class Result
    {
        public bool NifParsed { get; init; }
        public string? NifError { get; init; }
        public int BakedShapeCount { get; init; }
        public int ResolvedHeadPartCount { get; init; }

        /// Geometry-bearing head parts the NPC resolves to that have no baked shape
        /// of that EditorID in the .nif.
        public IReadOnlyList<HeadPartRef> MissingBakedShapes { get; init; } = System.Array.Empty<HeadPartRef>();

        /// <summary>Parts DROPPED from the expected set by the singular-slot rule: the
        /// engine (and the CK when baking) keeps only the FIRST-LISTED part of a singular
        /// slot Type — see <see cref="IsSingularSlotType"/>. Applies to the FLATTENED set:
        /// top-level parts AND extras whose own singular Type is already occupied
        /// (<see cref="IsSurplusSingularExtra"/>; entries carry FromExtraParts).
        /// Diagnostic only; sets nothing.</summary>
        public IReadOnlyList<HeadPartRef> SurplusSlotParts { get; init; } = System.Array.Empty<HeadPartRef>();

        /// <summary>Unbaked EXTRA parts satisfied by presence — a sibling extra of the same
        /// top-level part is baked, an orphan shape stands in for the renamed extra, or the
        /// extra shares its baked ancestor's model file (the "_1bit" beard-twin convention).
        /// Engine-inert (extras reconcile by presence, not name); diagnostic only.</summary>
        public IReadOnlyList<HeadPartRef> PresenceSatisfiedExtras { get; init; } = System.Array.Empty<HeadPartRef>();

        /// Baked head-part-like shapes with no matching resolved head part (detail only).
        public IReadOnlyList<string> OrphanBakedShapes { get; init; } = System.Array.Empty<string>();

        /// Non-null head part FormKeys that do not resolve in the supplied context.
        public IReadOnlyList<FormKey> UnresolvedHeadParts { get; init; } = System.Array.Empty<FormKey>();

        /// Count of null entries encountered in the (recursive) head part list.
        public int NullHeadPartLinks { get; init; }

        /// <summary>True when a head part the record needs is unaccounted for. Deliberately
        /// FORWARD-DIRECTION ONLY: orphan baked shapes do not set it.
        /// <para>The forward direction is a PROVEN dark-face trigger on an unmodified engine
        /// (in-game mutation matrix on Ysolda, 2026-08-15,
        /// docs/DarkFaceTriggerInvestigation-2026-08.md): deleting any single baked shape
        /// (brows, eyes, hair, mouth, or the head itself) and swapping / removing / adding any
        /// modeled record part (brows, eyes, hair, a modeled scar) EACH produced the dark face,
        /// with no SKSE plugins loaded. No part type is exempt, one part suffices, and
        /// race-default parts count the same as explicit ones. Face Discoloration Fix-class
        /// plugins force-regenerate tints and mask the symptom entirely, which is why
        /// mismatched mods are often reported as "renders fine".</para>
        /// <para>Two structural rules narrow the comparison (field reports 3–5 in the doc):
        /// singular slot types keep only the FIRST-LISTED part of the FLATTENED set —
        /// surplus same-type parts are dropped whether top-level or EXTRA, exactly as the
        /// CK's own bakes show (<see cref="IsSingularSlotType"/> /
        /// <see cref="IsSurplusSingularExtra"/>; re-derives the B6/Anoriath tolerances, the
        /// vanilla Khajiit ear-tufts state, and the two in-game-verified inert extras:
        /// Men of Winter's FacialHair "_1bit" twin and Miggyluv Hjoromir's Eyebrows
        /// lashes) — and the remaining EXTRA parts reconcile by PRESENCE, not name
        /// (Gaiden Shinji's renamed hairline and Brand-Shei's foreign-named hairlines
        /// render fine, while matrix variant A7 — a Misc-typed hairline removed with
        /// nothing standing in — dark-faces; an unbaked extra sharing its baked ancestor's
        /// MODEL FILE is additionally exempt, a belt kept from the MQ304Ulfric
        /// derivation). Dropped/satisfied parts land in <see cref="SurplusSlotParts"/> /
        /// <see cref="PresenceSatisfiedExtras"/>, not here.</para>
        /// <para>Race-default parts of <c>OverlayHeadPartList</c> races (the vampire races)
        /// never enter the comparison at all — their HeadData is a runtime overlay, not a
        /// slot-fill contract; see <see cref="RaceDefaultsParticipateInReconciliation"/>.</para>
        /// <para>Reverse direction CONFIRMED inert (matrix variant A6: an extra baked brows
        /// shape with a real HDPT EditorID, record untouched, rendered normal on the clean
        /// engine; matches the 2026-07-24 wild specimen and the forward-only "Dark Face Issue
        /// Reporter" xEdit script). Extras stay corroborating detail, never a defect. B4's
        /// dark face (record part removed) is thereby attributed to race-default substitution
        /// — the race's default part fills the vacated slot and is itself unbaked, a forward
        /// miss — not to the leftover baked shape.</para></summary>
        public bool HasMismatch =>
            MissingBakedShapes.Count > 0 || UnresolvedHeadParts.Count > 0 || NullHeadPartLinks > 0;

        /// <summary>
        /// Classifies the disagreement so the reported message can name the *likely cause*
        /// rather than always blaming a plugin version. A single unmatched head part (with at
        /// most one unmatched baked shape opposite it) really is consistent with version drift;
        /// once several parts are unmatched — or the mesh carries a whole set of foreign baked
        /// shapes — the far more common cause is that the records and the mesh are coming from
        /// different mods (a plugin conflict the appearance mod lost, or a FaceGen file conflict).
        /// </summary>
        public MismatchKind Kind
        {
            get
            {
                if (MissingBakedShapes.Count == 0)
                {
                    if (UnresolvedHeadParts.Count > 0 || NullHeadPartLinks > 0) return MismatchKind.BrokenHeadPartLinks;
                    return OrphanBakedShapes.Count > 0 ? MismatchKind.ExtraBakedShapesOnly : MismatchKind.None;
                }
                return (MissingBakedShapes.Count == 1 && OrphanBakedShapes.Count <= 1)
                    ? MismatchKind.SingleHeadPartDifference
                    : MismatchKind.DifferentSource;
            }
        }

        /// <summary>Distinct plugins the unmatched head parts come from (capped), for the
        /// remedy text — this is the plugin set currently *winning* for those slots.</summary>
        private List<string> MissingSourcePlugins(int max = 4)
        {
            var names = new List<string>();
            foreach (var m in MissingBakedShapes)
            {
                var name = m.FormKey.ModKey.FileName.ToString();
                if (!names.Contains(name, System.StringComparer.OrdinalIgnoreCase)) names.Add(name);
                if (names.Count >= max) break;
            }
            return names;
        }

        private bool AllMissingAreBaseGame() =>
            MissingBakedShapes.Count > 0 &&
            MissingBakedShapes.All(m => BaseGameMasters.Contains(m.FormKey.ModKey.FileName.ToString()));

        private static string Join(IReadOnlyList<string> items) => string.Join(", ", items);

        /// <summary>A concise, multi-line explanation suitable for a validation row's
        /// Issue text or a mugshot tooltip. Empty when there is nothing to report.
        /// Structure: what the check found → what it most likely means → what to try.
        /// </summary>
        /// <param name="maxPerCategory">Per-list cap before the "…and N more" tail.</param>
        /// <param name="scope">Which resolution context produced the analysis; picks the
        /// remedies that actually apply (load-order conflicts vs a single mod's own files).</param>
        /// <param name="fidelity">What the caller has PROVEN about delivery. When it is not
        /// <see cref="DeliveryFidelity.Unknown"/>, the conflict remedies are replaced with the
        /// causes that are still possible — the mismatch is inherent to the selection's own
        /// data (or the race record), not to the deployment. LoadOrder scope only.</param>
        /// <param name="subjectSuppliesRecord">Whether the mod under analysis supplies the NPC's
        /// plugin record itself (true), pairs its mesh with another plugin's record (false), or
        /// the caller doesn't know (null). Picks WHOSE versions the single-slot remedy blames —
        /// naming the plugin that merely DEFINES the missing part (often a base-game master,
        /// frozen for a decade) sent users chasing impossible version mismatches.</param>
        public string BuildReason(int maxPerCategory = 8, ReasonScope scope = ReasonScope.LoadOrder,
            DeliveryFidelity fidelity = DeliveryFidelity.Unknown, bool? subjectSuppliesRecord = null)
        {
            // Report only what the user would actually see in game — which is exactly what
            // HasMismatch flags. A purely additive .nif (Kind == ExtraBakedShapesOnly) causes no
            // visible defect, so it gets no row and no tooltip at all, not a softened one.
            // Orphan shapes still appear as corroborating detail below when a head part IS missing.
            if (!HasMismatch) return string.Empty;
            var kind = Kind;

            var sb = new StringBuilder();

            // ---- Headline: state what the evidence shows, not a presumed cause -------------
            switch (kind)
            {
                case MismatchKind.DifferentSource:
                    sb.Append("FaceGen / plugin mismatch (causes dark-face bug): ")
                      .Append("the FaceGen .nif has a different set of head parts than the NPC's .esp plugin record");
                    // "Not coming from the same mod" is an inference that only the load-order
                    // context can even entertain (hedged: it can also just be a mis-authored
                    // mod). SelectedMod scope resolves nothing but the mod's own data, so no
                    // other mod is in scope to blame — and with proven-faithful delivery the
                    // inference is FALSE (vanilla's own vampires ship exactly this way); the
                    // remedies then carry the accurate cause instead.
                    sb.Append(scope == ReasonScope.LoadOrder && fidelity == DeliveryFidelity.Unknown
                        ? ", so the mesh and the record may not be coming from the same appearance mod."
                        : ".");
                    break;
                case MismatchKind.SingleHeadPartDifference:
                    sb.Append("FaceGen / plugin mismatch (causes dark-face bug): ")
                      .Append("the NPC's .esp plugin record uses one head part that isn't in the FaceGen .nif.");
                    break;
                case MismatchKind.BrokenHeadPartLinks:
                    sb.Append("Broken head part references (a common cause of the in-game dark-face bug): ")
                      .Append("the NPC's .esp plugin record asks for head parts that aren't in your load order.");
                    break;
                    // No MismatchKind.ExtraBakedShapesOnly case: unreachable past the guard above.
            }

            // ---- Evidence -----------------------------------------------------------------
            var namedAsCounterpart = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (MissingBakedShapes.Count > 0)
            {
                sb.Append("\nThe following are in the .esp but not the .nif:");
                int shown = 0;
                foreach (var m in MissingBakedShapes)
                {
                    if (m.BakedSameTypeShapes != null)
                        foreach (var c in m.BakedSameTypeShapes) namedAsCounterpart.Add(c);
                    if (shown++ >= maxPerCategory) continue;
                    sb.Append("\n • '").Append(m.EditorId).Append("' (").Append(m.FormKey).Append(')');
                    if (m.FromRaceDefaults) sb.Append(" (race default)");
                    if (m.FromExtraParts) sb.Append(" (extra part)");
                    if (m.BakedSameTypeShapes is { Count: > 0 })
                        sb.Append(" — the .nif has '")
                          .Append(string.Join("' / '", m.BakedSameTypeShapes))
                          .Append("' instead");
                }
                if (MissingBakedShapes.Count > maxPerCategory)
                    sb.Append("\n • …and ").Append(MissingBakedShapes.Count - maxPerCategory)
                      .Append(" more head part(s) with no baked shape.");
            }

            // Orphans already named as an "instead" counterpart above are not repeated.
            var remainingOrphans = OrphanBakedShapes.Where(o => !namedAsCounterpart.Contains(o)).ToList();
            if (remainingOrphans.Count > 0)
            {
                sb.Append("\nThe following are in the .nif but not the .esp:");
                int n = System.Math.Min(remainingOrphans.Count, maxPerCategory);
                for (int i = 0; i < n; i++)
                    sb.Append("\n • ").Append(remainingOrphans[i]);
                if (remainingOrphans.Count > maxPerCategory)
                    sb.Append("\n • …and ").Append(remainingOrphans.Count - maxPerCategory).Append(" more");
            }

            if (UnresolvedHeadParts.Count > 0)
            {
                sb.Append("\nThe .esp asks for these head parts, but no plugin in your load order has them ")
                  .Append("(the mod that owns them isn't installed or isn't enabled):");
                int shown = 0;
                foreach (var u in UnresolvedHeadParts)
                {
                    if (shown++ >= maxPerCategory) break;
                    sb.Append("\n • ").Append(u);
                }
                if (UnresolvedHeadParts.Count > maxPerCategory)
                    sb.Append("\n • …and ").Append(UnresolvedHeadParts.Count - maxPerCategory)
                      .Append(" more unresolved head part(s).");
            }

            if (NullHeadPartLinks > 0)
                sb.Append("\nThe .esp's head part list also has ").Append(NullHeadPartLinks)
                  .Append(" empty entry(s).");

            // ---- Conclusion / remedies ----------------------------------------------------
            // SelectedMod scope compares the mod's own record against the mod's own mesh —
            // there is no deployment, no load order, and no other mod in the verdict, so a
            // "likely causes" menu is meaningless: the mod's own files disagree, which is an
            // authoring issue and nothing the user's setup can fix. Saying so is redundant in
            // a mod scan, so the conclusion is just the pointer (user ruling 2026-08-16).
            if (scope == ReasonScope.SelectedMod &&
                kind is MismatchKind.DifferentSource or MismatchKind.SingleHeadPartDifference)
            {
                sb.Append("\nCheck the mod page's Comments and Bugs sections for known issues or an updated version.");
                return sb.ToString();
            }

            // ---- Likely causes / remedies -------------------------------------------------
            var remedies = BuildRemedies(kind, scope, fidelity, subjectSuppliesRecord);
            if (remedies.Count > 0)
            {
                sb.Append("\nLikely cause(s), most common first:");
                foreach (var r in remedies) sb.Append("\n • ").Append(r);
            }

            return sb.ToString();
        }

        /// <summary>The prescriptive half of <see cref="BuildReason"/>: the causes that can
        /// actually produce this <paramref name="kind"/> in this <paramref name="scope"/>,
        /// each paired with the check or fix that resolves it. Proven-faithful delivery
        /// (<paramref name="fidelity"/>) replaces the conflict remedies entirely — see
        /// <see cref="BuildFaithfulDeliveryRemedies"/>.</summary>
        private List<string> BuildRemedies(MismatchKind kind, ReasonScope scope, DeliveryFidelity fidelity,
            bool? subjectSuppliesRecord = null)
        {
            if (scope == ReasonScope.LoadOrder && fidelity != DeliveryFidelity.Unknown &&
                kind is MismatchKind.DifferentSource or MismatchKind.SingleHeadPartDifference)
            {
                return BuildFaithfulDeliveryRemedies(kind, fidelity);
            }

            var list = new List<string>();
            var plugins = MissingSourcePlugins();
            string pluginList = plugins.Count > 0 ? Join(plugins) : "the winning plugin(s)";

            switch (kind)
            {
                case MismatchKind.DifferentSource when scope == ReasonScope.LoadOrder:
                    // Deliberately no "the head parts currently come from X" sentence: X is the
                    // plugin that DEFINES those head part records (often a resource master like
                    // High Poly Head.esm), which says nothing about who won the NPC record — the
                    // Winning Source column already names that, and the sentence read as a plugin
                    // conflict that wasn't happening.
                    list.Add(
                        "Plugin conflict — NPC2's output plugin may be overwritten in your load order. " +
                        "Make sure it is enabled and loads after every other plugin that edits this NPC.");
                    list.Add(
                        "Asset conflict — NPC2's FaceGen .nif may be overwritten in your mod manager. " +
                        "Make sure NPC2's output assets are enabled and win all file conflicts.");
                    list.Add(
                        "If the record and the .nif do both come from the selected mod, the mod itself might have a bug or " +
                        "have been installed incorrectly. Check its mod page for special installation instructions.");
                    break;

                // No SelectedMod cases: that scope concludes with the authoring-issue line in
                // BuildReason and never reaches this remedy builder for mismatch kinds.

                case MismatchKind.SingleHeadPartDifference:
                    // Deliberately no "different version of <defining plugin>" phrasing: the
                    // plugin that DEFINES the missing part is usually a resource master or the
                    // base game (frozen for a decade) and says nothing about which plugin's
                    // version drifted — the record-vs-mesh pairing is what's out of sync.
                    if (subjectSuppliesRecord == true)
                    {
                        // No slot-pairing claim ("this one slot") — the single orphan opposite a
                        // single miss need not share its slot Type (observed: a Face-type miss
                        // opposite a Brows-type orphan), and the same-type case already says so
                        // in the evidence via the "instead" clause.
                        list.Add(
                            "This mod ships both the plugin record and the FaceGen .nif, and they don't match — " +
                            "the .nif was probably exported from a different revision of the mod than the plugin (or the record " +
                            "was edited without regenerating FaceGen). Check the mod page (the Comments and Bugs sections) for " +
                            "known issues or an updated version.");
                    }
                    else if (subjectSuppliesRecord == false)
                    {
                        list.Add(AllMissingAreBaseGame()
                            ? "This mod ships a FaceGen .nif but no plugin record for this NPC, so the mesh is paired with the " +
                              "base game's record — and base-game records don't change. The mesh was probably baked against " +
                              "ANOTHER mod's record edits (overhauls routinely replace or rename the vanilla head parts, and " +
                              "the game pairs baked shapes to parts by name); check the mod page for the overhaul or patch it " +
                              "expects underneath."
                            : "This mod ships a FaceGen .nif but no plugin record for this NPC, so the mesh is paired with the " +
                              "record from the NPC's original plugin — check that the version of that mod the FaceGen was made " +
                              "for is installed.");
                    }
                    else
                    {
                        list.Add(
                            "The FaceGen .nif was baked with a different part in this slot than the record now uses — usually " +
                            "the mod's plugin and .nif coming from different revisions, or a conflict replacing one of them. " +
                            "Re-install the mod so its plugin and its FaceGen match.");
                    }
                    // Load-order scope only: a THIRD-party plugin renaming the part can flip the
                    // slot there, because that context resolves conflict winners. The Mod Issues
                    // scanner's mod-scoped analysis (SelectedMod) resolves only the mod's own
                    // plugins + the record's origin, so a foreign rename structurally cannot be
                    // the cause of ITS mismatches — in game it can only make this pairing WORK
                    // (the mesh-only wording above covers that as the expected-overhaul case).
                    if (scope == ReasonScope.LoadOrder)
                    {
                        list.Add(
                            "Or another mod changed that one head part. The game matches head parts to the .nif by name, so a mod that renames it breaks the match — check whether another plugin edits that head part.");
                    }
                    // No trailing "verified to dark-face / FDF masks it" note: accurate but not
                    // actionable, so it is documentation, not remedy (user ruling 2026-08-16).
                    // The evidence lives in the HasMismatch remarks and the investigation doc.
                    break;

                case MismatchKind.BrokenHeadPartLinks:
                    if (UnresolvedHeadParts.Count > 0)
                    {
                        var missingPlugins = UnresolvedHeadParts
                            .Select(u => u.ModKey.FileName.ToString())
                            .Distinct(System.StringComparer.OrdinalIgnoreCase)
                            .Take(4).ToList();
                        list.Add(
                            $"Install and enable the mod that owns these head parts ({Join(missingPlugins)}). " +
                            "If you don't have it, pick a different appearance for this NPC.");
                    }
                    if (NullHeadPartLinks > 0)
                        list.Add(
                            "An empty head part entry means the mod's plugin is damaged — clean it in xEdit, or pick a different appearance for this NPC.");
                    break;
            }

            return list;
        }

        /// <summary>
        /// Remedies for the case where the caller PROVED the deployment faithful (output record
        /// winning, deployed mesh byte-identical to the selected mod's own file). "Your load
        /// order overwrote it" is then the one cause that is ruled out — telling the user to go
        /// check plugin/asset conflicts sends them chasing a conflict that demonstrably is not
        /// happening. What remains: the selection's own data is mismatched (three flavours, per
        /// <see cref="DeliveryFidelity"/>), or the RACE record's default head parts changed out
        /// from under the mesh (the RS Children pattern: a race-editing plugin merged away, or an
        /// unrelated mod overriding the race).
        /// </summary>
        private List<string> BuildFaithfulDeliveryRemedies(MismatchKind kind, DeliveryFidelity fidelity)
        {
            var list = new List<string>
            {
                "Not a deployment problem: NPC2's output record is winning and the deployed FaceGen .nif is " +
                "byte-identical to the selected mod's own file, so nothing in your load order is overwriting this NPC.",
            };

            bool anyRace = MissingBakedShapes.Any(m => m.FromRaceDefaults);
            bool allRace = MissingBakedShapes.Count > 0 && MissingBakedShapes.All(m => m.FromRaceDefaults);

            if (allRace)
            {
                // The NPC's own parts all matched; the disagreement is confined to slots the record
                // leaves to the RACE's defaults. Two distinct causes share that observable — a race
                // edit that is not reaching the load order (RS Children with its plugin merged away,
                // or another mod winning the race record), and a mod whose record simply stopped
                // listing parts its mesh was baked with (observed in the wild both ways) — so both
                // are named; the analyzer cannot tell them apart from one resolution context.
                list.Add(
                    "Every mismatched part is a RACE default (marked '(race default)'): the NPC's own head parts " +
                    "all match the mesh. For the slots its record leaves unset the game falls back to the RACE's " +
                    "default parts — and those defaults are not what the mesh was baked with.");
                list.Add(
                    "If the selected mod normally edits that race (RS Children-style overhauls), the race edit is " +
                    "not reaching your load order: NPC2 merges NPC records into its output, but race edits are not " +
                    "carried over, and another mod can also win the race record. Note a race edit is global — " +
                    "appearance mods that disagree about a race's default parts cannot all match at once.");
                list.Add(
                    "Otherwise the mod's own files disagree: its FaceGen was baked with head parts its plugin " +
                    "record no longer lists (they appear in the '.nif but not the .esp' list above). Re-install " +
                    "the mod, or pick a different appearance for this NPC.");
                return list;
            }

            switch (fidelity)
            {
                case DeliveryFidelity.VanillaOwnData:
                    list.Add(
                        "The mismatch is in the base game's own files: Bethesda edited this NPC's record without " +
                        "regenerating its FaceGen mesh (common for vampire NPCs updated by the DLC), so the same " +
                        "face bug exists in an unmodded game. NPC2 reproduced vanilla exactly; a mod that " +
                        "regenerates this NPC's FaceGen — or a different appearance selection here — is the only fix.");
                    break;

                case DeliveryFidelity.SelectedModMeshOnly:
                    list.Add(
                        "The selected mod supplies only FaceGen files for this NPC (no plugin record), so its mesh " +
                        "is paired with the record from the NPC's original plugin — and the pairing does not fit. " +
                        "The mod was probably built to sit on top of another overhaul or a different version of " +
                        "those records; install what it expects underneath, or pick a different appearance for this NPC.");
                    break;

                default: // SelectedModOwnData
                    list.Add(
                        "The selected mod's own files disagree with each other: its plugin record asks for head " +
                        "parts its FaceGen .nif was not baked with. Re-install the mod and check its page for " +
                        "required patches or load-order notes; if the mismatch persists, pick a different " +
                        "appearance for this NPC.");
                    break;
            }

            if (kind == MismatchKind.SingleHeadPartDifference)
            {
                // A single flipped slot can also be a rename: the NPC record still points at the
                // same FormKey, but a mod overriding that head part changed its EditorID, and the
                // game matches mesh shapes by NAME.
                list.Add(
                    "Or another mod in your load order edits that one head part record: the game matches mesh " +
                    "shapes to head parts by name, so a renamed head part breaks the match even when the NPC " +
                    "record itself is untouched.");
            }

            if (anyRace)
            {
                list.Add(
                    "Part(s) marked '(race default)' come from the NPC's RACE record rather than the NPC itself — " +
                    "check whether another mod overrides that race's default head parts.");
            }

            return list;
        }
    }

    /// <summary>
    /// Analyze a deployed/rendered FaceGen .nif against the head parts an NPC actually
    /// uses in-game: the NPC's own head parts, plus the race's default head parts for
    /// any slot the NPC does not specify (commonly the mouth, which lives on the RACE,
    /// not the NPC). The Creation Kit bakes exactly this "effective" set, and the engine
    /// reconciles against it, so race-provided defaults must be counted as legitimately
    /// referenced — otherwise they look like orphan baked shapes (e.g. MaleMouthHumanoidDefault).
    /// </summary>
    /// <param name="npc">The NPC whose deployed/rendered FaceGen is being checked.</param>
    /// <param name="resolveHeadPart">Resolves a head part FormKey to its record in the
    /// caller's chosen context (link cache, mod-scoped, etc.). Null when unresolvable.</param>
    /// <param name="resolveRace">Resolves the NPC's race FormKey to its record (for the
    /// race's default head parts). Null when unresolvable — race defaults are then skipped.</param>
    /// <param name="nifPath">Absolute path to the FaceGen geometry .nif to inspect.</param>
    /// <param name="resolveHeadPartByEditorId">Optional EditorID → head part resolver used to
    /// TYPE the orphan baked shapes, so a missing part can name the same-slot shape the .nif
    /// carries instead ("record asks for BrowsMaleHumanoid04 — the .nif carries
    /// BrowsMaleHumanoid01"). Null skips the pairing.</param>
    public Result Analyze(
        INpcGetter npc,
        System.Func<FormKey, IHeadPartGetter?> resolveHeadPart,
        System.Func<FormKey, IRaceGetter?> resolveRace,
        string nifPath,
        System.Func<string, IHeadPartGetter?>? resolveHeadPartByEditorId = null)
    {
        // 1. Survey the baked shapes (memoized by path+size+mtime). nifly geometry shapes
        //    only (NiShape-derived), so node markers like "NPC Head [Head]" don't appear here.
        var survey = GetSurvey(nifPath);
        bool nifParsed = survey.LoadOk;
        var baked = survey.ShapeNames; // OrdinalIgnoreCase, trimmed; read-only (never mutated here)

        // 2. Walk the effective head parts, recursing Extra Parts, collecting resolved
        //    EditorIDs, geometry-bearing parts, null links, and unresolved links.
        var resolvedEditorIds = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var geometryBearing = new List<HeadPartRef>();
        var unresolved = new List<FormKey>();
        int nullLinks = 0;
        int resolvedCount = 0;
        var visited = new HashSet<FormKey>();

        var surplus = new List<HeadPartRef>();

        // Singular slot types already claimed by an earlier top-level part (declared
        // here, above Walk, because extras consult it mid-walk — see the surplus-extra
        // branch below; populated by the 2a loop's first-listed contest).
        var singularSeen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // Walk one part (and its extras) into the EXPECTED set. `ancestor` is null for a
        // top-level part; extras carry their root ancestor so the presence check can group
        // siblings.
        void Walk(IFormLinkGetter<IHeadPartGetter>? link, bool fromRaceDefaults, FormKey? ancestor = null)
        {
            if (link is null || link.IsNull) { nullLinks++; return; }
            var fk = link.FormKey;
            if (!visited.Add(fk)) return; // guard against circular Extra Parts
            var hp = resolveHeadPart(fk);
            if (hp is null) { unresolved.Add(fk); return; }
            resolvedCount++;
            if (!string.IsNullOrEmpty(hp.EditorID)) resolvedEditorIds.Add(hp.EditorID!);

            bool geo = BearsBakedGeometry(hp);

            // Extras contest the singular slots too — rule 2 applies to the FLATTENED
            // set, not just top-level parts: an extra whose own singular Type is
            // already occupied by an earlier expected part is dropped exactly like
            // surplus top-level parts. The CK bakes neither and the engine expects
            // neither (field reports 4/5: Men of Winter's FacialHair "_1bit" twin and
            // Miggyluv's Eyebrows lashes — both absent from their CK bakes, both
            // in-game verified inert 2026-08-17/18 — while A7's hairline, typed Misc,
            // the multi grab-bag, still triggers). Extras CHECK occupancy but never
            // CLAIM the slot: the evidence covers only the check direction, so
            // top-level behavior is unchanged.
            if (ancestor != null && IsSurplusSingularExtra(hp.Type, singularSeen))
            {
                if (geo && !string.IsNullOrEmpty(hp.EditorID))
                    surplus.Add(new HeadPartRef(fk, hp.EditorID!)
                        { Type = hp.Type, FromExtraParts = true, AncestorFormKey = ancestor.Value });
                if (hp.ExtraParts != null)
                    foreach (var ep in hp.ExtraParts) Walk(ep, fromRaceDefaults, ancestor ?? fk);
                return;
            }

            if (geo && !string.IsNullOrEmpty(hp.EditorID))
                geometryBearing.Add(new HeadPartRef(fk, hp.EditorID!)
                {
                    FromRaceDefaults = fromRaceDefaults, Type = hp.Type,
                    FromExtraParts = ancestor != null, AncestorFormKey = ancestor ?? fk,
                    ModelPath = hp.Model?.File.GivenPath,
                });

            if (hp.ExtraParts != null)
                foreach (var ep in hp.ExtraParts) Walk(ep, fromRaceDefaults, ancestor ?? fk);
        }

        // Names-only walk for SURPLUS parts (singular-slot rule): the engine drops them
        // from the expected set, but their baked shapes must not read as orphans either.
        void WalkNamesOnly(IFormLinkGetter<IHeadPartGetter>? link)
        {
            if (link is null || link.IsNull) return;
            var fk = link.FormKey;
            if (!visited.Add(fk)) return;
            var hp = resolveHeadPart(fk);
            if (hp is null) { unresolved.Add(fk); return; }
            resolvedCount++;
            if (!string.IsNullOrEmpty(hp.EditorID)) resolvedEditorIds.Add(hp.EditorID!);
            if (hp.ExtraParts != null)
                foreach (var ep in hp.ExtraParts) WalkNamesOnly(ep);
        }

        // 2a. The NPC's own head parts, IN RECORD ORDER. Record the slot Types it occupies
        //     (keyed by the enum's name to stay robust to the getter's nullability) so we
        //     can tell which race defaults it overrides — surplus parts still occupy their
        //     slot. For singular slot types only the FIRST-LISTED part enters the expected
        //     set (see IsSingularSlotType); later same-type parts are dropped, exactly as
        //     the CK's own bakes show (the vanilla Khajiit ear-tufts specimen).
        var npcSlotTypes = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (npc.HeadParts != null)
        {
            foreach (var link in npc.HeadParts)
            {
                if (link == null || link.IsNull) { nullLinks++; continue; }
                var peek = resolveHeadPart(link.FormKey);
                var slot = peek?.Type.ToString();
                if (!string.IsNullOrEmpty(slot)) npcSlotTypes.Add(slot!);

                bool isSurplus = peek != null && !string.IsNullOrEmpty(slot) &&
                                 IsSingularSlotType(peek.Type) && !singularSeen.Add(slot!);
                if (isSurplus)
                {
                    if (BearsBakedGeometry(peek) && !string.IsNullOrEmpty(peek!.EditorID))
                        surplus.Add(new HeadPartRef(link.FormKey, peek.EditorID!)
                            { Type = peek.Type, AncestorFormKey = link.FormKey });
                    WalkNamesOnly(link);
                }
                else
                {
                    Walk(link, fromRaceDefaults: false);
                }
            }
        }

        // 2b. Race default head parts for slots the NPC does NOT override. The engine
        //     uses the NPC's part for any slot it specifies; unspecified slots (mouth,
        //     etc.) fall back to the race, and those ARE baked.
        //     Skipped entirely when any of the NPC's OWN head parts failed to resolve:
        //     an unresolved link's slot Type is unknowable, so its slot looks
        //     unoccupied and the race default would be added on top — one absent
        //     dependency plugin then fabricates several extra "missing baked shape"
        //     findings per NPC (measured: Familiar Faces, 2,929 rows from one absent
        //     Eyes Nouveaux.esp). The unresolved links themselves are still reported.
        var race = (unresolved.Count == 0 && !npc.Race.IsNull) ? resolveRace(npc.Race.FormKey) : null;
        if (race != null && RaceDefaultsParticipateInReconciliation(race))
        {
            var headData = Auxilliary.IsFemale(npc) ? race.HeadData?.Female : race.HeadData?.Male;
            if (headData?.HeadParts != null)
            {
                foreach (var hpRef in headData.HeadParts)
                {
                    var link = hpRef.Head;
                    if (link.IsNull) continue;
                    var slot = resolveHeadPart(link.FormKey)?.Type.ToString();
                    if (!string.IsNullOrEmpty(slot) && npcSlotTypes.Contains(slot!)) continue;
                    Walk(link, fromRaceDefaults: true);
                }
            }
        }

        // 3. Reverse first: baked shapes with no matching resolved head part (excluding the
        //    primary head and obvious scene/utility names). Detail only — and input to the
        //    extras presence check below (an orphan can be a RENAMED extra standing in).
        var orphans = nifParsed
            ? baked.Where(b => !resolvedEditorIds.Contains(b) && !IsGenericNode(b, survey.PrimaryHeadShapeName)).ToList()
            : new List<string>();

        // 4. Forward: expected parts absent from the baked shapes. Only meaningful when the
        //    NIF actually parsed (else every part looks "missing"). Top-level parts match
        //    by NAME; EXTRA parts match by PRESENCE — see IsExtraPresenceSatisfied for the
        //    three satisfying clauses and their field/matrix evidence.
        var unbaked = nifParsed
            ? geometryBearing.Where(r => !baked.Contains(r.EditorId)).ToList()
            : new List<HeadPartRef>();
        var missing = new List<HeadPartRef>();
        var satisfiedExtras = new List<HeadPartRef>();
        foreach (var r in unbaked)
        {
            if (IsExtraPresenceSatisfied(r, orphans.Count > 0, geometryBearing, baked))
                satisfiedExtras.Add(r);
            else missing.Add(r);
        }

        // 5. Pair each missing part with the orphan shape(s) of the SAME slot Type the
        //    .nif carries instead — "the record asks for Brows04, the mesh was baked with
        //    Brows01" is far more actionable than the missing name alone. Best-effort:
        //    orphans are typed by resolving their shape name as a head part EditorID.
        if (resolveHeadPartByEditorId != null && missing.Count > 0 && orphans.Count > 0)
        {
            var typedOrphans = orphans
                .Select(name => (Name: name, Type: resolveHeadPartByEditorId(name)?.Type))
                .Where(t => t.Type != null)
                .ToList();
            for (int i = 0; i < missing.Count; i++)
            {
                if (missing[i].Type == null) continue;
                var counterparts = typedOrphans
                    .Where(o => o.Type == missing[i].Type)
                    .Select(o => o.Name)
                    .ToList();
                if (counterparts.Count > 0)
                    missing[i] = missing[i] with { BakedSameTypeShapes = counterparts };
            }
        }

        return new Result
        {
            NifParsed = nifParsed,
            NifError = survey.Error,
            BakedShapeCount = baked.Count,
            ResolvedHeadPartCount = resolvedCount,
            MissingBakedShapes = missing,
            SurplusSlotParts = surplus,
            PresenceSatisfiedExtras = satisfiedExtras,
            OrphanBakedShapes = orphans,
            UnresolvedHeadParts = unresolved,
            NullHeadPartLinks = nullLinks,
        };
    }

    /// <summary>
    /// The extras half of reconciliation rule 3 (extras reconcile by PRESENCE, not name),
    /// applied to one unbaked geometry-bearing part. Satisfied — engine-inert — when the
    /// part is an EXTRA and (a) any orphan shape stands in for a renamed extra (field
    /// report 3: Gaiden Shinji's renamed hairline, Brand-Shei's foreign-named hairlines),
    /// or (b) a sibling extra of the same top-level part is baked, or (c) the extra's
    /// model file is the SAME as its baked ancestor's — the vanilla "_1bit" beard-twin
    /// convention, where the extra references the parent's own mesh and so contributes no
    /// geometry the bake doesn't already carry (MQ304Ulfric / Men of Winter specimen,
    /// in-game verified 2026-08-17: patched record kept the _1bit expectation, byte-identical
    /// bake lacked the shape, no competing override or FDF, rendered fine). A hairline is
    /// DISTINCT geometry, so clause (c) leaves matrix variant A7 (hairline absent with
    /// nothing standing in = dark face) untouched. Top-level parts are never satisfied
    /// here — they reconcile by name.
    /// </summary>
    internal static bool IsExtraPresenceSatisfied(HeadPartRef part, bool anyOrphans,
        IReadOnlyList<HeadPartRef> geometryBearing, IReadOnlySet<string> baked)
    {
        if (!part.FromExtraParts) return false;
        if (anyOrphans) return true;

        foreach (var o in geometryBearing)
        {
            if (!baked.Contains(o.EditorId)) continue;

            // (b) Baked sibling extra of the same top-level part.
            if (o.FromExtraParts && o.AncestorFormKey.Equals(part.AncestorFormKey) &&
                !o.FormKey.Equals(part.FormKey))
            {
                return true;
            }

            // (c) Shared-model twin: the baked ANCESTOR itself uses the same mesh file.
            if (!o.FromExtraParts && o.FormKey.Equals(part.AncestorFormKey) &&
                ModelPathsEqual(o.ModelPath, part.ModelPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Model-path equality for the shared-model clause: separator- and
    /// case-insensitive; null/empty paths never match anything.</summary>
    internal static bool ModelPathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string Norm(string s) => s.Trim().Replace('/', '\\');
        return string.Equals(Norm(a), Norm(b), System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a race's default head parts enter the engine's FaceGen reconciliation.
    /// Races flagged <c>OverlayHeadPartList</c> (the vanilla vampire races) use their
    /// HeadData as a runtime OVERLAY — applied on top of an actor's own parts by the
    /// vampirism transform — not as slot-fill defaults the baked head must carry.
    /// In-game verified 2026-08-16: Bruma's CYREncVampire00Template (NordRaceVampire,
    /// whose Dawnguard-era default head FemaleHeadNordVampire is absent from the baked
    /// .nif) renders WITHOUT the dark face, both patched and on plain Bruma — while the
    /// mutation matrix proved race-default misses on a NON-overlay race (NordRace mouth
    /// and head, variants A4/A5) DO dark-face. Also explains the long-standing benign
    /// "MaleEyesHumanVampire01 vs mesh" rows. Explicit record parts are unaffected —
    /// the EncVampire02BossBretonM incident (explicit demon eyes missing from the mesh)
    /// dark-faced and must keep flagging.
    /// </summary>
    public static bool RaceDefaultsParticipateInReconciliation(IRaceGetter race) =>
        !race.Flags.HasFlag(Race.Flag.OverlayHeadPartList);

    /// <summary>
    /// Slot types where an actor can carry only ONE part: the engine (and the CK when it
    /// bakes) keeps the FIRST-LISTED part of such a type and drops the rest from the
    /// expected set. Evidence (docs/DarkFaceTriggerInvestigation-2026-08.md, field report 3):
    /// vanilla's own Khajiit bakes omit the SECOND-listed Hair part (KhajiitMaleEarTufts,
    /// modeled) because modelless HairKhajiit00 is listed first — and the engine renders
    /// them fine; the same rule re-derives the Anoriath and B6 tolerances (first-listed
    /// Eyebrows part was the baked one in both). Scars is proven MULTI (matrix cell B5:
    /// a second gash was expected and its miss dark-faced). Misc is a grab-bag (mouths,
    /// hairlines, earrings) — treated as multi so a mouth never excuses another Misc part.
    /// FacialHair/Face/Eyes singular is the engine's one-per-actor reality; only Eyebrows
    /// and Hair are directly evidenced.
    /// </summary>
    public static bool IsSingularSlotType(HeadPart.TypeEnum? type) => type is
        HeadPart.TypeEnum.Eyes or HeadPart.TypeEnum.Hair or HeadPart.TypeEnum.Face or
        HeadPart.TypeEnum.Eyebrows or HeadPart.TypeEnum.FacialHair;

    /// <summary>The extras half of the singular-slot rule (rule 2 on the FLATTENED
    /// set): true when an EXTRA part's own Type is singular and that slot is already
    /// occupied by an earlier expected part — the extra is then dropped, never
    /// expected in the bake. Field-derived: Men of Winter's "_1bit" FacialHair twin
    /// (behind the baked beard) and Miggyluv Hjoromir's Eyebrows lashes (behind his
    /// top-level brows) are both absent from their CK bakes and both in-game verified
    /// inert (2026-08-17/18), while matrix variant A7's hairline — typed Misc, the
    /// multi grab-bag — still dark-faces when absent. Callers pass the singular slot
    /// TYPE NAMES already claimed ("Eyebrows", "FacialHair", …).</summary>
    internal static bool IsSurplusSingularExtra(HeadPart.TypeEnum? extraType,
        IReadOnlySet<string> occupiedSingularSlots)
        => IsSingularSlotType(extraType) &&
           occupiedSingularSlots.Contains(extraType!.Value.ToString());

    /// <summary>
    /// True when <paramref name="headPart"/> contributes a shape to the baked FaceGen mesh —
    /// it has a model (MODL) or chargen morph parts (NAM0/NAM1). This is the predicate
    /// <see cref="Analyze"/> grades against, so a part it says bears no geometry has no baked
    /// shape named after its EditorID and never did.
    ///
    /// <para>Exposed because the wig/antler pipeline has to reach the same verdict. A MODELESS
    /// head part is a placeholder that renders nothing: it cannot clash with a forwarded wig,
    /// there is nothing in the NIF to strip for it, and swapping it out changes only the record.
    /// High Poly NPC Overhaul's <c>HighPoly_HairBald</c> is the canonical specimen (EDID + DATA +
    /// PNAM + RNAM, no MODL, no NAM0/NAM1) — treating it as real hair produced one bogus
    /// "baked shape not found" warning and one bogus validation error per NPC, 3,400 of each.
    /// Callers: <c>WigForwarder.CollectHairRemoval</c> / <c>CollectAntlerHeadPartRemoval</c>,
    /// <c>HeadPartWigConverter.CollectHairRemoval</c>, <c>OutputValidator.DonorHasModeledHair</c>.</para>
    /// </summary>
    public static bool BearsBakedGeometry(IHeadPartGetter? headPart) =>
        headPart != null && (headPart.Model?.File != null || (headPart.Parts?.Count ?? 0) > 0);

    /// <summary>
    /// Collects the FaceGen shape names that belong to head parts of
    /// <paramref name="type"/> for this NPC: the EditorIDs of every effective
    /// head part of that type plus, recursively, their Extra Parts — the
    /// Creation Kit bakes each geometry-bearing part as a shape named after
    /// its EditorID, and Extra Parts inherit the classification of the typed
    /// part that references them (their own Type is typically null/Misc).
    /// "Effective" mirrors <see cref="Analyze"/>: the NPC's own head parts,
    /// plus the race's defaults for any slot Type the NPC does not occupy.
    /// The returned set is OrdinalIgnoreCase to match the engine's
    /// case-insensitive shape-name/EditorID reconciliation. Feeds
    /// <see cref="ResolvedNpcMeshPaths.EyeShapeNames"/> (authoritative
    /// renderer IsEye classification) when called with
    /// <see cref="HeadPart.TypeEnum.Eyes"/>.
    /// </summary>
    public static HashSet<string> CollectShapeNamesOfType(
        INpcGetter npc,
        System.Func<FormKey, IHeadPartGetter?> resolveHeadPart,
        System.Func<FormKey, IRaceGetter?> resolveRace,
        HeadPart.TypeEnum type)
    {
        var names = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<FormKey>();

        void Collect(IFormLinkGetter<IHeadPartGetter>? link)
        {
            if (link is null || link.IsNull || !visited.Add(link.FormKey)) return;
            var hp = resolveHeadPart(link.FormKey);
            if (hp is null) return;
            if (!string.IsNullOrEmpty(hp.EditorID)) names.Add(hp.EditorID!);
            if (hp.ExtraParts != null)
                foreach (var ep in hp.ExtraParts) Collect(ep);
        }

        // NPC's own head parts; record the slot Types it occupies so race
        // defaults for those slots are skipped, same as Analyze.
        var npcSlotTypes = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (npc.HeadParts != null)
        {
            foreach (var link in npc.HeadParts)
            {
                if (link == null || link.IsNull) continue;
                var hp = resolveHeadPart(link.FormKey);
                var slot = hp?.Type.ToString();
                if (!string.IsNullOrEmpty(slot)) npcSlotTypes.Add(slot!);
                if (hp?.Type == type) Collect(link);
            }
        }

        var race = npc.Race.IsNull ? null : resolveRace(npc.Race.FormKey);
        if (race != null)
        {
            var headData = Auxilliary.IsFemale(npc) ? race.HeadData?.Female : race.HeadData?.Male;
            if (headData?.HeadParts != null)
            {
                foreach (var hpRef in headData.HeadParts)
                {
                    var link = hpRef.Head;
                    if (link.IsNull) continue;
                    var hp = resolveHeadPart(link.FormKey);
                    if (hp is null) continue;
                    var slot = hp.Type.ToString();
                    if (!string.IsNullOrEmpty(slot) && npcSlotTypes.Contains(slot!)) continue;
                    if (hp.Type == type) Collect(link);
                }
            }
        }

        return names;
    }

    private static bool IsGenericNode(string shapeName, string? primaryHeadShapeName)
    {
        if (!string.IsNullOrEmpty(primaryHeadShapeName) &&
            string.Equals(shapeName, primaryHeadShapeName, System.StringComparison.OrdinalIgnoreCase))
            return true;
        if (shapeName.IndexOf("NPC Head", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (shapeName.StartsWith("BSFaceGen", System.StringComparison.OrdinalIgnoreCase)) return true;
        if (shapeName.StartsWith("FaceGen", System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
