// RunLog.cs
namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>Severity used to colour a line in the Run tab's log view.</summary>
public enum RunLogSeverity
{
    Info,
    Warning,
    Error,

    /// <summary>
    /// A file was successfully written — the output plugin, the NPC token, a SkyPatcher/SPID
    /// ini, a spawn bat. Deliberately *not* used for NPC asset copies: those run in the
    /// thousands and would drown the handful of results a user actually looks for.
    /// </summary>
    Success
}

/// <summary>
/// One log message on its way to the Run tab. Instances travel through
/// <c>VM_Run</c>'s log subject as whole messages (which may span several physical
/// lines) and end up in <c>VM_Run.LogLines</c> split to one entry per rendered line.
/// </summary>
/// <remarks>
/// Deliberately a class and not a record: identity must be by reference. A log routinely
/// repeats the same text, and WPF locates items by equality — with value equality
/// <c>ScrollIntoView(lastLine)</c> would jump to the first identical line instead of the
/// newest one, and a selection would smear across every duplicate.
/// </remarks>
public sealed class RunLogEntry
{
    public RunLogEntry(string text, RunLogSeverity severity)
    {
        Text = text;
        Severity = severity;
    }

    public string Text { get; }
    public RunLogSeverity Severity { get; }
}

/// <summary>
/// Turns raw log text into per-line <see cref="RunLogEntry"/> values with a severity the
/// view can colour.
///
/// The backend cannot simply tell us the severity: <c>AppendLog</c>'s <c>isError</c> flag is
/// a *verbose-filter bypass*, and ~70 call sites pass warnings through it because it is the
/// only "always show this" channel available. So the authority here is the line's own leading
/// marker text — the long-standing convention of <c>"ERROR: "</c>, <c>"  WARNING: "</c>,
/// <c>"CRITICAL ERROR: "</c>, <c>"FATAL UI ERROR: "</c>, <c>"SCREENING WARNING: "</c> and
/// friends — and the flag is only the fallback for lines that carry no marker (most usefully,
/// the continuation lines of an exception stack that follow an <c>ERROR:</c> header).
///
/// Successful non-asset file writes ("Saved plugin: …", "Successfully wrote unified
/// NPC_Token.json to …") are recognised the same way, from a leading verb; see
/// <see cref="SuccessMarkers"/> for why that has to be anchored to the first word.
///
/// <para>What a marker is FOR (user standard, 2026-08-02): a coloured line is reserved for
/// something the user will notice in game — the dark face bug, missing or wrong meshes and
/// textures, wrong or missing outfits, null references that can crash or hang a load — plus
/// outright failures (exceptions, save failures, skipped NPCs, failed copies). A finding that
/// is real but inert in game gets a neutral "Note: " line instead, forced past the verbose
/// gate for the summary and verbose-only for the per-item detail; see
/// <c>Patcher.LogOrphanedDuplicates</c> for the shape and the comment at the bottom of
/// <c>NpcWarningKind</c> for the same rule applied to the grouped end-of-run report. Note when
/// wording one of those neutral lines that the search below is by WORD, not by prefix: a bare
/// "warning" or "error" anywhere in the first <see cref="MarkerWindow"/> characters colours the
/// line, while the plurals ("warnings", "errors") do not.</para>
/// </summary>
public static class RunLogClassifier
{
    /// <summary>
    /// How far into a line a marker is looked for. Markers live in the prefix by convention;
    /// bounding the window keeps a passing mention of "error" later in a sentence from
    /// recolouring an informational line.
    /// </summary>
    private const int MarkerWindow = 64;

    private static readonly string[] WarningMarkers = { "WARNING", "WARN" };
    private static readonly string[] ErrorMarkers = { "ERROR", "FATAL", "CRITICAL" };

    /// <summary>
    /// Recognised only as a line's *first* word, which is what confines the success colour to
    /// genuine write confirmations. Anchoring is not cosmetic — it is the whole mechanism:
    /// "Verification complete. All assets copied successfully." (an NPC-asset summary, which
    /// must stay plain) and "Wig conversion: wrote rewritten physics XML" both contain a marker
    /// word but do not lead with one.
    ///
    /// "Created" and "Finished" are deliberately absent: "Created N patching batches." and
    /// "Finished Pre-Indexing loose file paths." lead with them and are not file writes.
    /// </summary>
    private static readonly string[] SuccessMarkers = { "SAVED", "WROTE", "SUCCESSFULLY" };

    /// <summary>
    /// Splits <paramref name="message"/> into one entry per physical line, classifying each
    /// line individually and falling back to the message's own severity where a line carries
    /// no marker of its own.
    /// </summary>
    /// <remarks>
    /// The split deliberately mirrors the old <c>StringBuilder.AppendLine(message)</c>
    /// behaviour: a message with a leading or trailing newline still produces the blank line
    /// it used to render.
    /// </remarks>
    public static List<RunLogEntry> SplitIntoLines(RunLogEntry message)
    {
        var text = message.Text ?? string.Empty;
        var lines = new List<RunLogEntry>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            lines.Add(new RunLogEntry(line, Classify(line, message.Severity)));
        }

        return lines;
    }

    /// <summary>
    /// Resolves the severity of a single line, returning <paramref name="fallback"/> when the
    /// line carries no severity marker.
    /// </summary>
    public static RunLogSeverity Classify(string line, RunLogSeverity fallback)
    {
        if (string.IsNullOrWhiteSpace(line)) return fallback;

        var head = line.AsSpan().TrimStart();
        if (head.Length > MarkerWindow) head = head[..MarkerWindow];

        int warning = EarliestMarker(head, WarningMarkers);
        int error = EarliestMarker(head, ErrorMarkers);

        if (warning >= 0 && error >= 0)
        {
            // Both appear ("SCREENING WARNING" vs "CRITICAL ERROR" style prefixes can collide
            // with wording later in the head); whichever comes first names the line.
            return warning < error ? RunLogSeverity.Warning : RunLogSeverity.Error;
        }

        if (warning >= 0) return RunLogSeverity.Warning;
        if (error >= 0) return RunLogSeverity.Error;

        // Checked last so that a problem reported on the same line still wins — a
        // "Saved plugin: X (WARNING: ...)" line is a warning, not a success.
        if (EarliestMarker(head, SuccessMarkers) == 0) return RunLogSeverity.Success;

        return fallback;
    }

    private static int EarliestMarker(ReadOnlySpan<char> head, string[] markers)
    {
        int best = -1;
        foreach (var marker in markers)
        {
            int at = IndexOfWord(head, marker);
            if (at >= 0 && (best < 0 || at < best)) best = at;
        }

        return best;
    }

    /// <summary>
    /// Case-insensitive whole-word search. The word boundary matters: it is what keeps
    /// "Errors: 0" / "0 warnings" style summary wording from being treated as a marker.
    /// </summary>
    private static int IndexOfWord(ReadOnlySpan<char> head, string word)
    {
        int from = 0;
        while (from <= head.Length - word.Length)
        {
            int hit = head[from..].IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) return -1;

            int start = from + hit;
            int end = start + word.Length;
            bool leftIsBoundary = start == 0 || !char.IsLetterOrDigit(head[start - 1]);
            bool rightIsBoundary = end == head.Length || !char.IsLetterOrDigit(head[end]);
            if (leftIsBoundary && rightIsBoundary) return start;

            from = start + 1;
        }

        return -1;
    }
}
