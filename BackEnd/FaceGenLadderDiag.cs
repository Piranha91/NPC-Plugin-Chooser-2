using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Per-run capture of every <see cref="FaceGenLadder"/> verdict, written to
/// <c>FaceGenLadder.csv</c> next to the exe (or into a harness-supplied folder).
///
/// <para>Exists because the ladder's interesting rows are rare — a full run over ~3000 NPCs
/// produced roughly fifty row-2 cases and a single row-3 — so reasoning about coverage from a
/// patch log is guesswork. The CSV turns "which NPCs actually exercise which branch" into a
/// sortable table, and <see cref="FaceGenLadderDecision.LegacyAction"/> is recorded alongside
/// the planned action so one run yields the before/after diff with no separate baseline run.</para>
///
/// <para>Opt-in and hot-path cheap, following the same shape as
/// <see cref="AssetProvenanceDiag"/>: <see cref="IsEnabled"/> is checked before any work, the
/// dev file trigger force-enables it, and <see cref="Reset"/> runs at the start of a patch run.</para>
/// </summary>
public static class FaceGenLadderDiag
{
    public const string CsvFileName = "FaceGenLadder.csv";
    private const string TriggerFileName = "LogFaceGenLadder.txt";

    /// <summary>Effective on/off state. Call sites check this before building any inputs, since
    /// gathering them costs disk lookups the patch run does not otherwise need.</summary>
    public static bool IsEnabled { get; private set; }

    private static bool _fileTriggerPresent;

    // Keyed by target FormKey so a re-processed NPC replaces rather than duplicates its row.
    private static readonly ConcurrentDictionary<string, FaceGenLadderDecision> _byNpc =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Presence of <c>LogFaceGenLadder.txt</c> next to the exe force-enables the report.
    /// Call once at startup, before <see cref="SetEnabled"/>.</summary>
    public static void InitializeFromFileTrigger()
    {
        try
        {
            if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TriggerFileName)))
            {
                _fileTriggerPresent = true;
                IsEnabled = true;
            }
        }
        catch
        {
            // Swallow — a failed probe just leaves the report off.
        }
    }

    /// <summary>Turns the report on for a run. The verification harness calls this directly;
    /// the file trigger, if present, keeps it on regardless.</summary>
    public static void SetEnabled(bool enabled) => IsEnabled = enabled || _fileTriggerPresent;

    /// <summary>Clears accumulated rows. Called at the start of a patch run.</summary>
    public static void Reset()
    {
        _byNpc.Clear();
    }

    public static void Record(FaceGenLadderDecision decision)
    {
        if (!IsEnabled) return;
        _byNpc[decision.Inputs.TargetFormKey.ToString()] = decision;
    }

    /// <summary>Every verdict captured this run, ordered so the rare rows sort first — those are
    /// the ones worth spawning in game, and the harness samples straight off this order.</summary>
    public static IReadOnlyList<FaceGenLadderDecision> Decisions =>
        _byNpc.Values
            .OrderByDescending(d => d.Abort)
            .ThenByDescending(d => (int)d.Row)
            .ThenBy(d => d.Inputs.NpcIdentifier, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Writes the CSV. <paramref name="outputDirectory"/> lets the harness collect it
    /// alongside its other artifacts; null writes next to the exe like the other diagnostics.</summary>
    public static string? Flush(string? outputDirectory = null)
    {
        if (!IsEnabled || _byNpc.IsEmpty) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Npc,TargetFormKey,DonorFormKey,SubjectFormKey,ChainStatus,Mod,Mode,FlattenTemplateChain,Row," +
                      "SourceNif,SourceDds,HasPluginRecord,OriginRecord,OriginNif,OriginDds," +
                      "WinnerNif,WinnerNifOwner,WinnerDds,OriginNifCompatible,WinnerNifCompatible," +
                      "CompatibilityEvaluated,NifChoice,DdsChoice,ForwardOriginRecord," +
                      "PlannedAction,LegacyAction,Aborted,AbortReason,Explanation,ChainTrace");

        foreach (var d in Decisions)
        {
            var i = d.Inputs;
            sb.AppendLine(string.Join(",",
                Csv(i.NpcIdentifier), Csv(i.TargetFormKey.ToString()), Csv(i.DonorFormKey.ToString()),
                Csv(i.SubjectFormKey.ToString()), i.ChainStatus, Csv(i.ModName), i.Mode,
                i.FlattenTemplateChain, (int)d.Row,
                i.SourceNif, i.SourceDds, i.SourceHasPluginRecord, i.OriginRecordExists,
                i.OriginNif, i.OriginDds, i.WinnerNifExists, Csv(i.WinnerNifOwner), i.WinnerDdsExists,
                Tri(i.OriginNifCompatible), Tri(i.WinnerNifCompatible), d.CompatibilityEvaluated,
                d.NifChoice, d.DdsChoice, d.ForwardOriginRecord,
                Csv(d.PlannedAction), Csv(d.LegacyAction), d.Abort, Csv(d.AbortReason), Csv(d.LogLine),
                Csv(i.ChainTrace)));
        }

        string dir = outputDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, CsvFileName);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return null; // A diagnostic must never take a patch run down with it.
        }
    }

    private static string Tri(bool? v) => v.HasValue ? v.Value.ToString() : "NotEvaluated";

    private static string Csv(string? field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;
        if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
