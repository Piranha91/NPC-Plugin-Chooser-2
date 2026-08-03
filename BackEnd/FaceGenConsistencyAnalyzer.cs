using System.Collections.Concurrent;
using System.Text;
using CharacterViewer.Rendering;
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
///   resolves to has no shape of that EditorID baked into the .nif (high
///   confidence; the in-game tint pass fails for it).</item>
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
///   on its own — the dark-face trigger is the forward direction, and an extra
///   baked shape has been observed in game without it (see the remarks on
///   <see cref="Result.HasMismatch"/>). It appears only as corroborating detail
///   alongside a head part that IS missing.</item>
/// </list>
/// Caller-agnostic: the head part resolution strategy is supplied as a delegate so
/// the same analyzer serves both the load-order link cache (Validate Output) and
/// mod-scoped resolution (mugshot generation).
/// </summary>
public sealed class FaceGenConsistencyAnalyzer
{
    private readonly NifMeshBuilder _meshBuilder;

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
    }

    /// <summary>Surveys the NIF's baked shape names, memoized by (path, length, mtime).
    /// On a cache hit no parse happens; a content change (different size/mtime) misses
    /// and re-parses, so a stale entry can't mask a regenerated FaceGen.</summary>
    private CachedSurvey GetSurvey(string nifPath)
    {
        string key;
        try
        {
            var fi = new System.IO.FileInfo(nifPath);
            key = fi.Exists
                ? fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks
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

        /// Baked head-part-like shapes with no matching resolved head part (detail only).
        public IReadOnlyList<string> OrphanBakedShapes { get; init; } = System.Array.Empty<string>();

        /// Non-null head part FormKeys that do not resolve in the supplied context.
        public IReadOnlyList<FormKey> UnresolvedHeadParts { get; init; } = System.Array.Empty<FormKey>();

        /// Count of null entries encountered in the (recursive) head part list.
        public int NullHeadPartLinks { get; init; }

        /// <summary>True when a head part the record needs is unaccounted for. Deliberately
        /// FORWARD-DIRECTION ONLY: orphan baked shapes do not set it.
        /// <para>Evidence (2026-07-24), since no engine-level documentation of the rule exists:
        /// a NIF carrying a shape with no matching head part (0_HAIRLINE_Male_Human_CurlyScalp,
        /// plus a duplicate of another part) rendered with no dark-face bug in game; and the
        /// established detector for this bug, the "Dark Face Issue Reporter" xEdit script, tests
        /// only that every HeadPart in the record is present in the NIF. The community sources
        /// that describe the engine behaviour ("the game regenerates the face but doesn't load
        /// tint data when the mesh doesn't match the head parts information") never state the
        /// reverse direction. So an extra baked shape is reported as corroborating detail, not
        /// as a defect on its own.</para></summary>
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
        public string BuildReason(int maxPerCategory = 8, ReasonScope scope = ReasonScope.LoadOrder,
            DeliveryFidelity fidelity = DeliveryFidelity.Unknown)
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
                    sb.Append("FaceGen / plugin mismatch (a common cause of the in-game dark-face bug): ")
                      .Append("the FaceGen .nif has a different set of head parts than the NPC's .esp plugin record");
                    // "Not coming from the same mod" is an inference, and with proven-faithful
                    // delivery it is a FALSE one (vanilla's own vampires ship exactly this way) —
                    // the remedies then carry the accurate cause instead.
                    sb.Append(scope == ReasonScope.LoadOrder && fidelity != DeliveryFidelity.Unknown
                        ? "."
                        : ", so the mesh and the record are not coming from the same appearance mod.");
                    break;
                case MismatchKind.SingleHeadPartDifference:
                    sb.Append("FaceGen / plugin mismatch (a common cause of the in-game dark-face bug): ")
                      .Append("the NPC's .esp plugin record uses one head part that isn't in the FaceGen .nif.");
                    break;
                case MismatchKind.BrokenHeadPartLinks:
                    sb.Append("Broken head part references (a common cause of the in-game dark-face bug): ")
                      .Append("the NPC's .esp plugin record asks for head parts that aren't in your load order.");
                    break;
                    // No MismatchKind.ExtraBakedShapesOnly case: unreachable past the guard above.
            }

            // ---- Evidence -----------------------------------------------------------------
            if (MissingBakedShapes.Count > 0)
            {
                sb.Append("\nThe following are in the .esp but not the .nif:");
                int shown = 0;
                foreach (var m in MissingBakedShapes)
                {
                    if (shown++ >= maxPerCategory) break;
                    sb.Append("\n • '").Append(m.EditorId).Append("' (").Append(m.FormKey).Append(')');
                    if (m.FromRaceDefaults) sb.Append(" (race default)");
                }
                if (MissingBakedShapes.Count > maxPerCategory)
                    sb.Append("\n • …and ").Append(MissingBakedShapes.Count - maxPerCategory)
                      .Append(" more head part(s) with no baked shape.");
            }

            if (OrphanBakedShapes.Count > 0)
            {
                sb.Append("\nThe following are in the .nif but not the .esp: ");
                int n = System.Math.Min(OrphanBakedShapes.Count, maxPerCategory);
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(OrphanBakedShapes[i]);
                }
                if (OrphanBakedShapes.Count > maxPerCategory)
                    sb.Append(", +").Append(OrphanBakedShapes.Count - maxPerCategory).Append(" more");
                sb.Append('.');
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

            // ---- Likely causes / remedies -------------------------------------------------
            var remedies = BuildRemedies(kind, scope, fidelity);
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
        private List<string> BuildRemedies(MismatchKind kind, ReasonScope scope, DeliveryFidelity fidelity)
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

                case MismatchKind.DifferentSource when scope == ReasonScope.SelectedMod:
                    list.Add(AllMissingAreBaseGame()
                        ? "This NPC's head parts are all vanilla Skyrim ones, so the mod supplies a FaceGen .nif but no matching plugin record. Check that the mod's plugin is assigned to it in the Mods menu, and that the right source NPC is selected."
                        : "The mod's own .esp and .nif don't match — or another mod in your mod manager is overwriting this NPC's FaceGen .nif.");
                    list.Add(
                        "Re-install the mod (and check its mod page for special installation instructions); " +
                        "if it needs a different version of another mod, install the matching version.");
                    list.Add(
                        "Run Validate Output to see which plugin and which files actually win in your setup.");
                    break;

                case MismatchKind.SingleHeadPartDifference:
                    list.Add(
                        $"Version mismatch — only one head part differs, so the .nif was probably made for a different version of {pluginList} than the one you have. " +
                        "Re-install the mod so its plugin and its FaceGen match.");
                    list.Add(
                        "Or another mod changed that one head part. The game matches head parts to the .nif by name, so a mod that renames it breaks the match — check whether another plugin edits that head part.");
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
    public Result Analyze(
        INpcGetter npc,
        System.Func<FormKey, IHeadPartGetter?> resolveHeadPart,
        System.Func<FormKey, IRaceGetter?> resolveRace,
        string nifPath)
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

        void Walk(IFormLinkGetter<IHeadPartGetter>? link, bool fromRaceDefaults)
        {
            if (link is null || link.IsNull) { nullLinks++; return; }
            var fk = link.FormKey;
            if (!visited.Add(fk)) return; // guard against circular Extra Parts
            var hp = resolveHeadPart(fk);
            if (hp is null) { unresolved.Add(fk); return; }
            resolvedCount++;
            if (!string.IsNullOrEmpty(hp.EditorID)) resolvedEditorIds.Add(hp.EditorID!);

            bool geo = BearsBakedGeometry(hp);
            if (geo && !string.IsNullOrEmpty(hp.EditorID))
                geometryBearing.Add(new HeadPartRef(fk, hp.EditorID!) { FromRaceDefaults = fromRaceDefaults });

            if (hp.ExtraParts != null)
                foreach (var ep in hp.ExtraParts) Walk(ep, fromRaceDefaults);
        }

        // 2a. The NPC's own head parts. Record the slot Types it occupies (keyed by the
        //     enum's name to stay robust to the getter's nullability) so we can tell which
        //     race defaults it overrides.
        var npcSlotTypes = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (npc.HeadParts != null)
        {
            foreach (var link in npc.HeadParts)
            {
                if (link != null && !link.IsNull)
                {
                    var slot = resolveHeadPart(link.FormKey)?.Type.ToString();
                    if (!string.IsNullOrEmpty(slot)) npcSlotTypes.Add(slot!);
                }
                Walk(link, fromRaceDefaults: false);
            }
        }

        // 2b. Race default head parts for slots the NPC does NOT override. The engine
        //     uses the NPC's part for any slot it specifies; unspecified slots (mouth,
        //     etc.) fall back to the race, and those ARE baked.
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
                    var slot = resolveHeadPart(link.FormKey)?.Type.ToString();
                    if (!string.IsNullOrEmpty(slot) && npcSlotTypes.Contains(slot!)) continue;
                    Walk(link, fromRaceDefaults: true);
                }
            }
        }

        // 3. Forward: geometry-bearing resolved head parts absent from the baked shapes.
        //    Only meaningful when the NIF actually parsed (else every part looks "missing").
        var missing = nifParsed
            ? geometryBearing.Where(r => !baked.Contains(r.EditorId)).ToList()
            : new List<HeadPartRef>();

        // 4. Reverse: baked shapes with no matching resolved head part (excluding the
        //    primary head and obvious scene/utility names). Detail only.
        var orphans = nifParsed
            ? baked.Where(b => !resolvedEditorIds.Contains(b) && !IsGenericNode(b, survey.PrimaryHeadShapeName)).ToList()
            : new List<string>();

        return new Result
        {
            NifParsed = nifParsed,
            NifError = survey.Error,
            BakedShapeCount = baked.Count,
            ResolvedHeadPartCount = resolvedCount,
            MissingBakedShapes = missing,
            OrphanBakedShapes = orphans,
            UnresolvedHeadParts = unresolved,
            NullHeadPartLinks = nullLinks,
        };
    }

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
