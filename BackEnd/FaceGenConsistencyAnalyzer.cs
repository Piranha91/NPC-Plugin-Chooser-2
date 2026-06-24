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
/// four general failure classes, regardless of root cause (a same-named plugin
/// loaded at the wrong version, a missing master, a null head part link, or a
/// mod author shipping a .nif that simply doesn't match its plugin):</para>
/// <list type="bullet">
///   <item><b>Missing baked shape</b> — a geometry-bearing head part the NPC
///   resolves to has no shape of that EditorID baked into the .nif (high
///   confidence; the in-game tint pass fails for it).</item>
///   <item><b>Unresolved link</b> — a non-null head part FormKey does not resolve
///   in the current load order (missing plugin/master, or a wrong-version plugin
///   that lacks the FormID).</item>
///   <item><b>Null link</b> — a null entry in the head part list (data error).</item>
///   <item><b>Orphan baked shape</b> — the .nif contains a baked head-part-like
///   shape with no matching resolved head part. Reported as corroborating detail
///   only; on its own it does not raise the flag (a hand-named custom shape would
///   otherwise false-positive).</item>
/// </list>
/// Caller-agnostic: the head part resolution strategy is supplied as a delegate so
/// the same analyzer serves both the load-order link cache (Validate Output) and
/// mod-scoped resolution (mugshot generation).
/// </summary>
public sealed class FaceGenConsistencyAnalyzer
{
    private readonly NifMeshBuilder _meshBuilder;

    public FaceGenConsistencyAnalyzer(CharacterPreviewCache previewCache)
    {
        // Reuse the shared mesh builder the rest of the rendering pipeline uses
        // (same wiring as FaceGenAnalysisCache) rather than constructing a parallel parser.
        _meshBuilder = previewCache.MeshBuilder;
    }

    public readonly record struct HeadPartRef(FormKey FormKey, string EditorId);

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

        /// <summary>True when a high-confidence inconsistency was found. Orphan baked
        /// shapes do not, on their own, set this (see class remarks).</summary>
        public bool HasMismatch =>
            MissingBakedShapes.Count > 0 || UnresolvedHeadParts.Count > 0 || NullHeadPartLinks > 0;

        /// <summary>A concise, multi-line explanation suitable for a validation row's
        /// Issue text or a mugshot tooltip. Empty when there is nothing to report.</summary>
        public string BuildReason(int maxPerCategory = 8)
        {
            if (!HasMismatch && OrphanBakedShapes.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.Append("FaceGen / plugin mismatch (a common cause of the in-game dark-face bug):");

            int shown = 0;
            foreach (var m in MissingBakedShapes)
            {
                if (shown++ >= maxPerCategory) break;
                sb.Append("\n • Head part '").Append(m.EditorId).Append("' (").Append(m.FormKey)
                  .Append(") is referenced by the NPC but has no matching shape in the FaceGen mesh — ")
                  .Append("the FaceGen was likely generated against a different version of '")
                  .Append(m.FormKey.ModKey.FileName).Append("'.");
            }
            if (MissingBakedShapes.Count > maxPerCategory)
                sb.Append("\n • …and ").Append(MissingBakedShapes.Count - maxPerCategory)
                  .Append(" more head part(s) with no baked shape.");

            if (NullHeadPartLinks > 0)
                sb.Append("\n • ").Append(NullHeadPartLinks)
                  .Append(" null head part reference(s) in the NPC's head part list.");

            shown = 0;
            foreach (var u in UnresolvedHeadParts)
            {
                if (shown++ >= maxPerCategory) break;
                sb.Append("\n • Head part ").Append(u)
                  .Append(" is referenced but does not resolve in the current load order (missing plugin/master).");
            }
            if (UnresolvedHeadParts.Count > maxPerCategory)
                sb.Append("\n • …and ").Append(UnresolvedHeadParts.Count - maxPerCategory)
                  .Append(" more unresolved head part(s).");

            if (OrphanBakedShapes.Count > 0)
            {
                sb.Append("\n • The FaceGen also contains baked shape(s) with no matching head part: ");
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

            return sb.ToString();
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
        // 1. Survey the baked shapes. nifly geometry shapes only (NiShape-derived),
        //    so node markers like "NPC Head [Head]" don't appear here.
        var survey = _meshBuilder.SurveyNif(nifPath);
        bool nifParsed = survey.LoadOk;

        var baked = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (nifParsed)
        {
            foreach (var s in survey.Shapes)
                if (!string.IsNullOrWhiteSpace(s.ShapeName)) baked.Add(s.ShapeName.Trim());
        }

        // 2. Walk the effective head parts, recursing Extra Parts, collecting resolved
        //    EditorIDs, geometry-bearing parts, null links, and unresolved links.
        var resolvedEditorIds = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var geometryBearing = new List<HeadPartRef>();
        var unresolved = new List<FormKey>();
        int nullLinks = 0;
        int resolvedCount = 0;
        var visited = new HashSet<FormKey>();

        void Walk(IFormLinkGetter<IHeadPartGetter>? link)
        {
            if (link is null || link.IsNull) { nullLinks++; return; }
            var fk = link.FormKey;
            if (!visited.Add(fk)) return; // guard against circular Extra Parts
            var hp = resolveHeadPart(fk);
            if (hp is null) { unresolved.Add(fk); return; }
            resolvedCount++;
            if (!string.IsNullOrEmpty(hp.EditorID)) resolvedEditorIds.Add(hp.EditorID!);

            bool geo = hp.Model?.File != null || (hp.Parts?.Count ?? 0) > 0;
            if (geo && !string.IsNullOrEmpty(hp.EditorID))
                geometryBearing.Add(new HeadPartRef(fk, hp.EditorID!));

            if (hp.ExtraParts != null)
                foreach (var ep in hp.ExtraParts) Walk(ep);
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
                Walk(link);
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
                    Walk(link);
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
            ? baked.Where(b => !resolvedEditorIds.Contains(b) && !IsGenericNode(b, survey)).ToList()
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

    private static bool IsGenericNode(string shapeName, NifMeshBuilder.NifSurveyResult survey)
    {
        if (!string.IsNullOrEmpty(survey.PrimaryHeadShapeName) &&
            string.Equals(shapeName, survey.PrimaryHeadShapeName, System.StringComparison.OrdinalIgnoreCase))
            return true;
        if (shapeName.IndexOf("NPC Head", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (shapeName.StartsWith("BSFaceGen", System.StringComparison.OrdinalIgnoreCase)) return true;
        if (shapeName.StartsWith("FaceGen", System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
