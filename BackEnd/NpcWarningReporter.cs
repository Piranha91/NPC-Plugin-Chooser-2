using System.Collections.Concurrent;
using System.IO;
using System.Text;

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

    /// <summary>A face mesh is being copied with no tint to pair with it anywhere — see
    /// <see cref="FaceGenLadderDecision.MissingTintEverywhere"/>.</summary>
    MissingFaceTint,

    /// <summary>A copied NIF shape has no resolvable texture in the selected mod, the game Data
    /// folder, or the vanilla archives, so it renders untextured (detail names the NIF, the shape
    /// and the missing paths).</summary>
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
/// <para>The run log stays lay-readable; the TECHNICAL payload goes to a companion file
/// (<see cref="DetailedLogPath"/>, written by <see cref="Flush"/> whenever any warning fired, and
/// pointed at by the report's closing line) so a user with the wherewithal — or the maintainer
/// they ask for help — can see per-NPC ladder context without re-running with diag triggers.</para>
///
/// <para>Static with a per-run <see cref="Reset"/>, like <see cref="FaceGenLadderDiag"/>: callers
/// (AssetHandler's copy pipeline) record from background tasks, and the Patcher flushes once the
/// task drain guarantees nothing is still recording. The formatting itself is pure
/// (<see cref="FormatReport"/> / <see cref="FormatDetailedLog"/>) so tests can pin grouping and
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
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PatchWarnings.log");

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
            "selected for them, so NPC2 forwarded their original meshes. However, other mods in " +
            "your load order may make changes that are incompatible with the original meshes, " +
            "causing the dark face bug. Spawn these NPCs in game to check their faces before " +
            "starting a playthrough:",

        NpcWarningKind.ModMeshCompatibility =>
            "The appearance mods you selected for the following NPCs provide face meshes but do " +
            "not change the NPC records, so the meshes must match the records the NPCs already " +
            "use — and a check found they do not. Other mods in your load order may have changed " +
            "those records' head data. Spawn these NPCs in game to check their faces before " +
            "starting a playthrough:",

        NpcWarningKind.MissingFaceTint =>
            "No face tint textures could be found for the following NPCs — not in the appearance " +
            "mods you selected for them, not in the mods that originally added the NPCs, and not " +
            "anywhere else in your load order. The faces of these NPCs may look discoloured in game:",

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

    /// <summary>
    /// Pure formatting of the detailed companion log: per warning kind, the lay header for
    /// context, the <see cref="TechnicalNote"/>, then each NPC with its technical detail
    /// indented beneath it.
    /// </summary>
    public static IReadOnlyList<string> FormatDetailedLog(
        IReadOnlyCollection<(NpcWarningKind Kind, string Npc, string? Detail, string? TechnicalDetail)> entries)
    {
        var lines = new List<string>();

        foreach (var kind in Enum.GetValues<NpcWarningKind>())
        {
            var byNpc = GroupForKind(entries, kind);
            if (byNpc.Count == 0) continue;

            if (lines.Count > 0) lines.Add(string.Empty);
            lines.Add($"=== {kind} ===");
            lines.Add(Header(kind));
            lines.Add(TechnicalNote(kind));

            foreach (var npc in byNpc)
            {
                lines.Add(string.Empty);
                var details = Distinct(npc.Select(e => e.Detail));
                lines.Add("--- " + npc.Key +
                          (details.Count > 0 ? ": " + string.Join("; ", details) : string.Empty));

                foreach (var technical in Distinct(npc.Select(e => e.TechnicalDetail)))
                {
                    foreach (var techLine in technical.Split('\n'))
                    {
                        lines.Add("    " + techLine.TrimEnd('\r'));
                    }
                }
            }
        }

        return lines;
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

    /// <summary>Writes the detailed log beside the exe. Returns false on failure — a diagnostic
    /// must never take a patch run down with it, and the caller then skips the pointer line.</summary>
    private static bool WriteDetailedLog(
        IReadOnlyCollection<(NpcWarningKind Kind, string Npc, string? Detail, string? TechnicalDetail)> entries)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("NPC2 patch warnings — detailed breakdown");
            sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}. The run log holds the plain-language summary; " +
                          "this file adds the per-NPC technical context behind it.");
            sb.AppendLine();
            foreach (var line in FormatDetailedLog(entries))
            {
                sb.AppendLine(line);
            }

            File.WriteAllText(DetailedLogPath, sb.ToString(), new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
