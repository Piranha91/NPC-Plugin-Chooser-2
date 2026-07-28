using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// One parsed line from a "Rejected NPCs" log. The logs are free text written by
/// <c>VM_ModSetting.RefreshNpcLists</c>, so everything here is best-effort: whatever cannot be
/// split out stays available verbatim in <see cref="RawText"/>.
/// </summary>
public sealed class RejectedNpcRecord
{
    /// <summary>Short label for a tree node — name, else EditorID, else FormKey.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The full NPC identifier as it appeared in the log (may be "Name | EditorID | FormKey").</summary>
    public string NpcIdentifier { get; set; } = string.Empty;

    public string EditorId { get; set; } = string.Empty;
    public string FormKey { get; set; } = string.Empty;

    /// <summary>Mod display name as written in the line; may be empty for the error-shaped lines.</summary>
    public string ModName { get; set; } = string.Empty;

    /// <summary>Why the NPC was discarded, normalized for display (leading capital, single trailing period).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The originating line(s), including any continuation lines of a stack trace.</summary>
    public string RawText { get; set; } = string.Empty;
}

/// <summary>
/// Reads the per-mod "Rejected NPCs\{mod}.txt" logs back into structured records for the
/// Settings &gt; Mod Import Settings &gt; Rejected NPCs viewer.
///
/// The writer emits three line shapes (see <c>VM_ModSetting.RefreshNpcLists</c>):
///   "Discarded {npc} from {mod} because {reason}."
///   "ERROR for ModSetting '{mod}': NPC {formKey} found in multiple associated plugins: ..."
///   "Error loading NPC data for ModSetting '{mod}': " followed by a multi-line stack trace.
/// Anything that does not start one of those is treated as a continuation of the previous
/// record, which is what keeps exception stacks attached to their entry.
/// </summary>
public static class RejectedNpcLogParser
{
    public const string LogFolderName = "Rejected NPCs";

    private const string DiscardedPrefix = "Discarded ";
    private const string AmbiguousPrefix = "ERROR for ModSetting ";
    private const string LoadErrorPrefix = "Error loading NPC data for ModSetting ";
    private const string BecauseToken = " because ";
    private const string FromToken = " from ";

    // e.g. "01BB28:Skyrim.esm"
    private static readonly Regex FormKeyPattern =
        new(@"^[0-9A-Fa-f]{6}:.+\.(esm|esp|esl)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string DefaultLogDirectory =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogFolderName);

    public static List<RejectedNpcRecord> ParseFile(string filePath) =>
        ParseLines(File.ReadLines(filePath), Path.GetFileNameWithoutExtension(filePath));

    /// <param name="modNameHint">
    /// The log's file name, which is the path-safe form of the mod's display name. Used to place
    /// the NPC / mod boundary exactly when either name contains " from " (see
    /// <see cref="FindModSplitIndex"/>); optional, and only affects those ambiguous lines.
    /// </param>
    public static List<RejectedNpcRecord> ParseLines(IEnumerable<string> lines, string modNameHint = null)
    {
        var records = new List<RejectedNpcRecord>();
        RejectedNpcRecord current = null;

        foreach (var rawLine in lines)
        {
            var line = (rawLine ?? string.Empty).TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            if (StartsRecord(line))
            {
                current = ParseLine(line, modNameHint);
                records.Add(current);
            }
            else if (current != null)
            {
                // Continuation of a multi-line entry (exception stacks are written with embedded newlines).
                current.RawText += Environment.NewLine + line;
                current.Reason = current.Reason.Length == 0 ? line : current.Reason + Environment.NewLine + line;
            }
            else
            {
                current = new RejectedNpcRecord { Label = "(unrecognized entry)", Reason = line, RawText = line };
                records.Add(current);
            }
        }

        return records;
    }

    private static bool StartsRecord(string line) =>
        line.StartsWith(DiscardedPrefix, StringComparison.Ordinal) ||
        line.StartsWith(AmbiguousPrefix, StringComparison.Ordinal) ||
        line.StartsWith(LoadErrorPrefix, StringComparison.Ordinal);

    private static RejectedNpcRecord ParseLine(string line, string modNameHint)
    {
        var record = new RejectedNpcRecord { RawText = line };

        if (line.StartsWith(DiscardedPrefix, StringComparison.Ordinal))
        {
            var body = line.Substring(DiscardedPrefix.Length);

            // Both the NPC name and the mod name can themselves contain " from " / " because ",
            // so split on the last occurrence of each — the writer appends them in that order.
            // (The mod boundary gets a better answer than "last" when the file name is known.)
            string head;
            var becauseIndex = body.LastIndexOf(BecauseToken, StringComparison.Ordinal);
            if (becauseIndex >= 0)
            {
                head = body.Substring(0, becauseIndex);
                record.Reason = NormalizeReason(body.Substring(becauseIndex + BecauseToken.Length));
            }
            else
            {
                head = body;
                record.Reason = "(no reason recorded)";
            }

            var fromIndex = FindModSplitIndex(head, modNameHint);
            if (fromIndex >= 0)
            {
                record.ModName = head.Substring(fromIndex + FromToken.Length);
                head = head.Substring(0, fromIndex);
            }

            ApplyIdentifier(record, head);
        }
        else if (line.StartsWith(AmbiguousPrefix, StringComparison.Ordinal))
        {
            record.ModName = ExtractQuoted(line);

            const string npcToken = ": NPC ";
            var npcIndex = line.IndexOf(npcToken, StringComparison.Ordinal);
            if (npcIndex >= 0)
            {
                var rest = line.Substring(npcIndex + npcToken.Length);
                var space = rest.IndexOf(' ');
                ApplyIdentifier(record, space > 0 ? rest.Substring(0, space) : rest);
                record.Reason = NormalizeReason(space > 0 ? rest.Substring(space + 1) : rest);
            }
            else
            {
                record.Label = "(ambiguous source plugin)";
                record.Reason = NormalizeReason(line);
            }
        }
        else // LoadErrorPrefix
        {
            record.ModName = ExtractQuoted(line);
            record.Label = "(error loading NPC data)";
            record.NpcIdentifier = record.Label;
            record.Reason = NormalizeReason(line);
        }

        if (record.Label.Length == 0)
        {
            record.Label = "(unnamed NPC)";
        }

        return record;
    }

    /// <summary>
    /// Finds the " from " that separates the NPC from the mod in a "Discarded ..." line.
    ///
    /// Both halves can contain " from " ("Ghost from the Past" out of "Tales from Skyrim"), which
    /// makes any single positional rule wrong somewhere. When the caller knows the log's file name
    /// — the path-safe form of the mod's display name — the split whose suffix reproduces that
    /// name is the correct one, so candidates are tried from the right and the first exact match
    /// wins. Without a hint (or with none matching) the last occurrence is the fallback, which is
    /// right whenever the mod name itself has no " from " in it.
    /// </summary>
    /// <returns>Index of the separating " from ", or -1 if the line has none.</returns>
    private static int FindModSplitIndex(string head, string modNameHint)
    {
        var candidates = new List<int>();
        for (var i = head.IndexOf(FromToken, StringComparison.Ordinal);
             i >= 0;
             i = head.IndexOf(FromToken, i + 1, StringComparison.Ordinal))
        {
            candidates.Add(i);
        }

        if (candidates.Count == 0)
        {
            return -1;
        }

        if (!string.IsNullOrEmpty(modNameHint))
        {
            for (var i = candidates.Count - 1; i >= 0; i--)
            {
                var suffix = head.Substring(candidates[i] + FromToken.Length);
                if (string.Equals(Auxilliary.MakeStringPathSafe(suffix), modNameHint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return candidates[i];
                }
            }
        }

        return candidates[candidates.Count - 1];
    }

    /// <summary>
    /// Splits an "Name | EditorID | FormKey" identifier (any subset of which may be missing —
    /// see <c>Auxilliary.GetLogString</c>) into its parts and picks the tree label.
    /// </summary>
    private static void ApplyIdentifier(RejectedNpcRecord record, string identifier)
    {
        identifier = identifier.Trim();
        record.NpcIdentifier = identifier;
        if (identifier.Length == 0)
        {
            return;
        }

        var parts = new List<string>();
        foreach (var part in identifier.Split('|'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                parts.Add(trimmed);
            }
        }

        if (parts.Count > 0 && FormKeyPattern.IsMatch(parts[parts.Count - 1]))
        {
            record.FormKey = parts[parts.Count - 1];
            parts.RemoveAt(parts.Count - 1);
        }

        string name = string.Empty;
        if (parts.Count >= 2)
        {
            name = parts[0];
            record.EditorId = parts[1];
        }
        else if (parts.Count == 1)
        {
            // With only one part left the writer's format is ambiguous (either the name or the
            // EditorID was null). EditorIDs are single tokens; display names usually are not.
            if (parts[0].Contains(' '))
            {
                name = parts[0];
            }
            else
            {
                record.EditorId = parts[0];
            }
        }

        record.Label = name.Length > 0 ? name
            : record.EditorId.Length > 0 ? record.EditorId
            : record.FormKey.Length > 0 ? record.FormKey
            : identifier;
    }

    private static string ExtractQuoted(string line)
    {
        var open = line.IndexOf('\'');
        if (open < 0) return string.Empty;
        var close = line.IndexOf('\'', open + 1);
        return close > open ? line.Substring(open + 1, close - open - 1) : string.Empty;
    }

    /// <summary>
    /// The writer appends its own period after reasons that already end in one, so trailing runs
    /// of dots collapse to a single one. The first letter is capitalized because the reasons are
    /// written as sentence fragments ("its race is ...") but are shown standalone.
    /// </summary>
    private static string NormalizeReason(string reason)
    {
        reason = (reason ?? string.Empty).Trim();
        while (reason.EndsWith("..", StringComparison.Ordinal))
        {
            reason = reason.Substring(0, reason.Length - 1);
        }

        if (reason.Length > 0 && char.IsLower(reason[0]))
        {
            reason = char.ToUpperInvariant(reason[0]) + reason.Substring(1);
        }

        return reason;
    }
}
