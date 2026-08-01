using System.Collections.Concurrent;
using System.IO;
using NPC_Plugin_Chooser_2.BackEnd.Logging;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>Per-NPC warning classes collected during a patch run and reported together after
/// patching. Declaration order is report order.</summary>
public enum NpcWarningKind
{
    /// <summary>Rows 4/5 borrowed the origin's face mesh and the compatibility probe positively
    /// said it does not fit the record that ships — see
    /// <see cref="FaceGenLadderDecision.OriginMeshFailedCompatCheck"/>.</summary>
    OriginMeshCompatibility,

    /// <summary>Row 2 with a FaceGen-only selection ships the MOD's mesh against the ORIGIN's
    /// record, and the probe positively said the pairing does not fit — see
    /// <see cref="FaceGenLadderDecision.ModMeshFailedCompatCheck"/>.</summary>
    ModMeshCompatibility,

    /// <summary>The mod's own mesh ships with the record it was authored with — a pairing the
    /// row legs never probe, being self-consistent by authorship — but the RACE's chargen
    /// default head parts resolve differently in the live load order than through the mod's
    /// own plugins, and the probe positively said the mesh does not fit what the game will
    /// resolve. RS Children with its plugins merged into the output is the measured case
    /// (docs/KnownLimitations.md #5); an unrelated mod winning the race record is the other.
    /// Recorded by the Patcher at the probe site (not via a ladder flag — see the trigger in
    /// <c>Patcher.ComputeFaceGenDecisionAsync</c>).</summary>
    RaceDefaultsDrift,

    /// <summary>A face mesh is being copied with no tint to pair with it anywhere — see
    /// <see cref="FaceGenLadderDecision.MissingTintEverywhere"/>.</summary>
    MissingFaceTint,

    /// <summary>A copied NIF shape has no resolvable texture in the selected mod, the game Data
    /// folder, or the vanilla archives, so it renders untextured (detail names the NIF, the shape
    /// and the missing paths). Meshes reached only through a HeadPart record are exempt — the game
    /// draws the face from the FaceGen NIF's baked shapes, so a stale HDPT mesh is not rendered
    /// and warning on it is a false positive; see <see cref="AssetRequestContext.HeadPartOnly"/>.</summary>
    TexturelessShapes,
}

/// <summary>
/// Collects per-NPC warnings raised during a patch run and reports them AFTER patching, grouped
/// by warning type: one explanatory paragraph per type, then the affected NPCs.
///
/// <para>Shaped this way at the user's direction (2026-07-30), optimising for a lay reader
/// actually acting on the result: warnings logged as they happened were scattered through
/// thousands of progress lines and each re-explained itself per NPC, where one paragraph followed
/// by a name list reads once and scans. The wording avoids "this"/"that" — every sentence names
/// what it means. The four per-NPC summaries the Patcher already emits (skipped faces, inherited
/// faces, flattened fallbacks, inert outfits) follow the same grouped end-of-run shape.</para>
///
/// <para>The run log stays lay-readable; the TECHNICAL payload goes to a companion HTML file
/// (<see cref="DetailedLogPath"/>, written by <see cref="Flush"/> whenever any warning fired, and
/// pointed at by the report's closing line) so a user with the wherewithal — or the maintainer
/// they ask for help — can see per-NPC ladder context without re-running with diag triggers.</para>
///
/// <para>Static with a per-run <see cref="Reset"/>, like <see cref="FaceGenLadderDiag"/>: callers
/// (AssetHandler's copy pipeline) record from background tasks, and the Patcher flushes once the
/// task drain guarantees nothing is still recording. The formatting itself is pure
/// (<see cref="FormatReport"/> / <see cref="BuildDetailedGroups"/>) so tests can pin grouping and
/// wording without touching the shared state that live patch runs (including ones running
/// concurrently in other test classes) mutate.</para>
/// </summary>
public static class NpcWarningReporter
{
    private static readonly ConcurrentQueue<(NpcWarningKind Kind, string Npc, string? Detail, string? TechnicalDetail)>
        _entries = new();

    /// <summary>The companion file holding the technical breakdown of the last run's warnings.
    /// Next to the exe, like every other NPC2 log; overwritten per run, deleted when a run
    /// produces no warnings so a stale file cannot describe a clean run.</summary>
    public static string DetailedLogPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PatchWarnings.html");

    /// <summary>Clears accumulated warnings. Called at the start of a patch run.</summary>
    public static void Reset()
    {
        _entries.Clear();
    }

    /// <summary>Records one warning against one NPC. <paramref name="detail"/> is appended to the
    /// NPC's line in the run-log report; several details for the same NPC and kind are joined
    /// with "; " (how the textureless report lists each affected shape once per NPC).
    /// <paramref name="technicalDetail"/> goes only to the detailed log file — multi-line is
    /// fine; it is indented under the NPC's heading there.</summary>
    public static void Record(NpcWarningKind kind, string npcIdentifier, string? detail = null,
        string? technicalDetail = null)
    {
        _entries.Enqueue((kind, npcIdentifier, detail, technicalDetail));
    }

    /// <summary>Emits the grouped report, writes the detailed companion log when any warning
    /// fired (deletes a stale one when none did), and clears the collected warnings. Every
    /// run-log line is forced (survives the verbose filter) and non-error; the "WARNING: " lead
    /// on each group header is what RunLogClassifier colours on.</summary>
    public static void Flush(Action<string, bool, bool> log)
    {
        var entries = _entries.ToList();
        _entries.Clear();

        foreach (var line in FormatReport(entries))
        {
            log(line, false, true);
        }

        if (entries.Count == 0)
        {
            try { File.Delete(DetailedLogPath); } catch { /* best-effort; a stale file is cosmetic */ }
            return;
        }

        // The pointer line only prints when the file really exists — a failed write must not
        // send a user hunting for a log that is not there.
        if (WriteDetailedLog(entries))
        {
            log(string.Empty, false, true);
            log($"See the log at {DetailedLogPath} for a detailed breakdown of the above warnings.",
                false, true);
        }
    }

    /// <summary>The explanatory paragraph shown above each warning group. Specific by design:
    /// no pronoun in it depends on context a reader skimming the end of a long log lacks.</summary>
    public static string Header(NpcWarningKind kind) => kind switch
    {
        NpcWarningKind.OriginMeshCompatibility =>
            "The following NPCs did not have face meshes provided by the appearance mods you " +
            "selected for them, so NPC2 forwarded the meshes from the mods that originally added " +
            "them. A check found those meshes do not match the head parts of the records these " +
            "NPCs will ship with — either the mod you picked edits the head parts but ships no " +
            "matching mesh, or another mod has changed the record (or its race) that the original " +
            "mesh was built for. Either way this can cause the dark face bug. Spawn these NPCs in " +
            "game to check their faces before starting a playthrough:",

        NpcWarningKind.ModMeshCompatibility =>
            "The appearance mods you selected for the following NPCs supply face meshes but no " +
            "plugin record, so NPC2 pairs each mesh with the record from the plugin that " +
            "originally added the NPC — and a check found the mesh does not match that record's " +
            "head parts. The mod was likely built against a different version of these NPCs (a " +
            "high-poly head replacer, USSEP, or an overhaul it expects underneath it). Spawn " +
            "these NPCs in game to check their faces before starting a playthrough:",

        NpcWarningKind.RaceDefaultsDrift =>
            "The faces of the following NPCs depend on RACE-level default head parts, and your " +
            "load order supplies different defaults than the ones their face meshes were built " +
            "against. Mods that restyle NPCs by editing the race itself (RS Children is the " +
            "classic example) only work while their race edit is active — NPC2 merges NPC " +
            "records into its output, but race edits are only carried over when the mod's " +
            "Record Override Handling Mode says so, and another mod winning the race record has " +
            "the same effect. A check found these meshes do not match the head parts the game " +
            "will resolve, which can cause the dark face bug. Each NPC below names the Record " +
            "Override Handling Mode that fits your selections:",

        NpcWarningKind.MissingFaceTint =>
            "No face tint textures could be found for the following NPCs — not in the appearance " +
            "mods you selected for them, not in the mods that originally added the NPCs, and not " +
            "anywhere else in your mod list. The faces of these NPCs may look discoloured in game:",

        NpcWarningKind.TexturelessShapes =>
            "The following NPCs use mesh shapes whose textures could not be found in the " +
            "appearance mods you selected for them, in your game folder, or in the base game " +
            "archives. The shapes listed below will render untextured in game. A common cause is " +
            "an appearance mod that relies on textures from a separate mod — if so, add the folder " +
            "of the mod that provides the textures to the appearance mod's entry in the Mods menu:",

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The technical framing printed under each group heading in the detailed log:
    /// what the check actually measured, and where to go for more.</summary>
    public static string TechnicalNote(NpcWarningKind kind) => kind switch
    {
        NpcWarningKind.OriginMeshCompatibility or NpcWarningKind.ModMeshCompatibility =>
            "The probe parses the candidate FaceGen NIF and compares its baked shape names " +
            "against the head parts the shipping record resolves to (race chargen defaults " +
            "included) — the reconciliation the engine performs when applying the face tint. " +
            "Run Validate Output for the exact per-NPC head-part difference.",

        NpcWarningKind.RaceDefaultsDrift =>
            "The trigger compares the race's chargen default head parts as the selected mod's " +
            "own plugins supply them (falling back to the implicit base-game masters' winner) " +
            "against the live load order's winner. On drift, the probe parses the mod's FaceGen " +
            "NIF and reconciles its baked shape names against the head parts the shipping " +
            "record resolves to in the live load order — the reconciliation the engine performs " +
            "when applying the face tint. The recommended mode comes from every selection " +
            "sharing the race: Include when they all agree about its defaults, IncludeAsNew " +
            "when they disagree (a shared override would break the other side). Mods already " +
            "set to Include/IncludeAsNew ship their race edits and are not warned about. Run " +
            "Validate Output for the exact per-NPC difference.",

        NpcWarningKind.MissingFaceTint =>
            "No facetint .dds was found at the subject's FaceGen path in the selected mod's " +
            "folders or archives, the origin mod's, or the deployed load order.",

        NpcWarningKind.TexturelessShapes =>
            "Every texture slot of each listed shape failed to resolve in the selected mod's " +
            "folders and archives, the game Data folder, and the vanilla archives. Paths are " +
            "Data-relative.",

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Pure formatting of the run-log report: one block per <see cref="NpcWarningKind"/> that has
    /// entries, in enum order — a blank spacer line, the "WARNING: "-prefixed <see cref="Header"/>,
    /// then one "  - " line per NPC (alphabetical), with that NPC's details joined by "; ".
    /// </summary>
    public static IReadOnlyList<string> FormatReport(
        IReadOnlyCollection<(NpcWarningKind Kind, string Npc, string? Detail, string? TechnicalDetail)> entries)
    {
        var lines = new List<string>();

        foreach (var kind in Enum.GetValues<NpcWarningKind>())
        {
            var byNpc = GroupForKind(entries, kind);
            if (byNpc.Count == 0) continue;

            lines.Add(string.Empty);
            lines.Add("WARNING: " + Header(kind));
            foreach (var npc in byNpc)
            {
                var details = Distinct(npc.Select(e => e.Detail));
                lines.Add("  - " + npc.Key +
                          (details.Count > 0 ? ": " + string.Join("; ", details) : string.Empty));
            }
        }

        return lines;
    }

    /// <summary>One NPC in a detailed warning group: the heading (identifier plus the run-log
    /// detail, when any) and the flattened technical lines shown beneath it.</summary>
    public sealed record DetailedWarningNpc(string Heading, IReadOnlyList<string> TechnicalLines);

    /// <summary>One warning kind's block in the detailed companion log.</summary>
    public sealed record DetailedWarningGroup(
        NpcWarningKind Kind, string Header, string TechnicalNote, IReadOnlyList<DetailedWarningNpc> Npcs);

    /// <summary>
    /// Pure structure of the detailed companion log: per warning kind (enum order, kinds with no
    /// entries skipped), the lay header for context, the <see cref="TechnicalNote"/>, then each
    /// NPC (alphabetical) with its distinct technical details split into individual lines.
    /// <see cref="WriteDetailedLog"/> renders this to HTML; tests pin the grouping and content
    /// here without coupling to markup.
    /// </summary>
    public static IReadOnlyList<DetailedWarningGroup> BuildDetailedGroups(
        IReadOnlyCollection<(NpcWarningKind Kind, string Npc, string? Detail, string? TechnicalDetail)> entries)
    {
        var groups = new List<DetailedWarningGroup>();

        foreach (var kind in Enum.GetValues<NpcWarningKind>())
        {
            var byNpc = GroupForKind(entries, kind);
            if (byNpc.Count == 0) continue;

            var npcs = new List<DetailedWarningNpc>();
            foreach (var npc in byNpc)
            {
                var details = Distinct(npc.Select(e => e.Detail));
                string heading = npc.Key +
                                 (details.Count > 0 ? ": " + string.Join("; ", details) : string.Empty);

                var technicalLines = new List<string>();
                foreach (var technical in Distinct(npc.Select(e => e.TechnicalDetail)))
                {
                    foreach (var techLine in technical.Split('\n'))
                    {
                        technicalLines.Add(techLine.TrimEnd('\r'));
                    }
                }

                npcs.Add(new DetailedWarningNpc(heading, technicalLines));
            }

            groups.Add(new DetailedWarningGroup(kind, Header(kind), TechnicalNote(kind), npcs));
        }

        return groups;
    }

    private static List<IGrouping<string, (NpcWarningKind Kind, string Npc, string? Detail, string? TechnicalDetail)>>
        GroupForKind(
            IReadOnlyCollection<(NpcWarningKind Kind, string Npc, string? Detail, string? TechnicalDetail)> entries,
            NpcWarningKind kind) =>
        entries
            .Where(e => e.Kind == kind)
            .GroupBy(e => e.Npc, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> Distinct(IEnumerable<string?> values) =>
        values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Pure classifier turning a technical detail's plain-text lines into the structured blocks
    /// the HTML card renders (facts, field-chip rows, comparison tables, titled sub-groups), so
    /// a reader follows a table instead of a text blob. The producers
    /// (<see cref="FaceGenLadderDecision.TechnicalSummary"/>, the Patcher's probe context, the
    /// AssetHandler's textureless report) commit to these line shapes:
    /// <list type="bullet">
    /// <item>"label: k=v, k=v, ..." — a labeled field row; consecutive rows sharing ≥2 keys merge
    /// into one comparison table (how selected mod / origin / winner become a grid).</item>
    /// <item>"k=v k=v ..." — an unlabeled field row (space-separated; a bare token continues the
    /// previous value, so "chain=NotTemplated (flattened)" stays one field).</item>
    /// <item>"key: value" — a single fact (override chains, graded record, chain trace).</item>
    /// <item>a line ending in ":" followed by indented lines — a sub-group (probe evidence);
    /// the indented lines classify recursively.</item>
    /// <item>anything else — preformatted text, so unknown producers still render.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<HtmlDetailBlock> ClassifyTechnicalLines(IReadOnlyList<string> lines)
    {
        var blocks = new List<HtmlDetailBlock>();
        int i = 0;
        while (i < lines.Count)
        {
            string line = lines[i];

            // Sub-group: "title:" with indented lines beneath it.
            if (line.Length > 1 && line.TrimEnd().EndsWith(':') && !line.StartsWith(' ') &&
                i + 1 < lines.Count && lines[i + 1].StartsWith(' '))
            {
                var children = new List<string>();
                int j = i + 1;
                while (j < lines.Count && lines[j].StartsWith(' '))
                {
                    children.Add(lines[j].TrimStart());
                    j++;
                }
                blocks.Add(new HtmlDetailGroup(line.TrimEnd().TrimEnd(':'),
                    ClassifyTechnicalLines(children)));
                i = j;
                continue;
            }

            blocks.Add(ClassifyLine(line));
            i++;
        }

        return MergeTables(blocks);
    }

    private static HtmlDetailBlock ClassifyLine(string line)
    {
        // Labeled field row: try each ": " split point left to right; the first whose remainder
        // fully parses as a comma-separated k=v list wins (so a label containing ": " still
        // resolves — the earlier split fails to parse and the search moves on).
        int search = 0;
        while (true)
        {
            int idx = line.IndexOf(": ", search, StringComparison.Ordinal);
            if (idx <= 0) break;
            if (TryParseCommaFields(line[(idx + 2)..], out var labeled))
            {
                return new HtmlDetailFieldRow(line[..idx], labeled);
            }
            search = idx + 1;
        }

        if (TryParseCommaFields(line, out var commaFields))
        {
            return new HtmlDetailFieldRow(null, commaFields);
        }

        if (TryParseSpaceFields(line, out var spaceFields))
        {
            return new HtmlDetailFieldRow(null, spaceFields);
        }

        int factIdx = line.IndexOf(": ", StringComparison.Ordinal);
        if (factIdx > 0)
        {
            return new HtmlDetailFact(line[..factIdx], line[(factIdx + 2)..]);
        }

        return new HtmlDetailText(line);
    }

    /// <summary>"k=v, k=v, ..." — delegates to the shared <see cref="HtmlLog.TryParseFieldList"/>
    /// (conservative: every comma-separated segment must parse, so prose never misparses).</summary>
    private static bool TryParseCommaFields(string text, out List<KeyValuePair<string, string>> fields) =>
        HtmlLog.TryParseFieldList(text, out fields);

    /// <summary>"k=v k=v ..." — space-separated; a token without '=' continues the previous
    /// field's value ("chain=NotTemplated (flattened)").</summary>
    private static bool TryParseSpaceFields(string text, out List<KeyValuePair<string, string>> fields)
    {
        fields = new List<KeyValuePair<string, string>>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq > 0 && !token[..eq].Contains(':'))
            {
                fields.Add(new KeyValuePair<string, string>(token[..eq], token[(eq + 1)..]));
            }
            else if (fields.Count > 0)
            {
                var last = fields[^1];
                fields[^1] = new KeyValuePair<string, string>(last.Key, last.Value + " " + token);
            }
            else
            {
                return false;
            }
        }
        return fields.Count >= 2;
    }

    /// <summary>Merges runs of consecutive LABELED field rows that share at least two keys into
    /// one comparison table (columns = union of keys in first-seen order; a row missing a key
    /// gets an empty cell).</summary>
    private static IReadOnlyList<HtmlDetailBlock> MergeTables(List<HtmlDetailBlock> blocks)
    {
        var result = new List<HtmlDetailBlock>();
        int i = 0;
        while (i < blocks.Count)
        {
            if (blocks[i] is not HtmlDetailFieldRow { Label: not null } first)
            {
                result.Add(blocks[i]);
                i++;
                continue;
            }

            var run = new List<HtmlDetailFieldRow> { first };
            int j = i + 1;
            while (j < blocks.Count && blocks[j] is HtmlDetailFieldRow { Label: not null } next &&
                   next.Fields.Select(f => f.Key)
                       .Intersect(run[^1].Fields.Select(f => f.Key), StringComparer.Ordinal)
                       .Count() >= 2)
            {
                run.Add(next);
                j++;
            }

            if (run.Count >= 2)
            {
                var columns = new List<string>();
                foreach (var row in run)
                foreach (var f in row.Fields)
                {
                    if (!columns.Contains(f.Key)) columns.Add(f.Key);
                }

                result.Add(new HtmlDetailTable(
                    columns,
                    run.Select(r => (r.Label!, (IReadOnlyList<string?>)columns
                            .Select(c => r.Fields.FirstOrDefault(f => f.Key == c) is { Key: not null } m
                                ? m.Value : null)
                            .ToList()))
                        .ToList()));
            }
            else
            {
                result.Add(first);
            }

            i = j;
        }
        return result;
    }

    /// <summary>Writes the detailed HTML log beside the exe. Returns false on failure — a
    /// diagnostic must never take a patch run down with it, and the caller then skips the
    /// pointer line.</summary>
    private static bool WriteDetailedLog(
        IReadOnlyCollection<(NpcWarningKind Kind, string Npc, string? Detail, string? TechnicalDetail)> entries)
    {
        try
        {
            var doc = new HtmlLogDocument("NPC2 Patch Warnings — Detailed Breakdown", new[]
            {
                new KeyValuePair<string, string>("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
            });
            doc.AddParagraph("The run log holds the plain-language summary; this file adds the " +
                             "per-NPC technical context behind it.", muted: true);

            foreach (var group in BuildDetailedGroups(entries))
            {
                doc.BeginSection(group.Kind.ToString(),
                    badge: group.Npcs.Count.ToString(), badgeSeverity: HtmlLogSeverity.Warning);
                doc.AddParagraph(group.Header);
                doc.AddParagraph(group.TechnicalNote, muted: true);
                foreach (var npc in group.Npcs)
                {
                    doc.AddCard(npc.Heading, ClassifyTechnicalLines(npc.TechnicalLines));
                }
            }

            File.WriteAllText(DetailedLogPath, doc.Render());
            return true;
        }
        catch
        {
            return false;
        }
    }
}
