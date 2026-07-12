using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Opt-in, per-run provenance report answering "why did record X end up in the output plugin,
/// and through which chain of references?". Covers every non-NPC record written to the output:
/// dependencies deep-copied in as new records, overrides forwarded (or delta-patched) into the
/// output, bulk 'Include All' override imports, and NPC2's own generated keywords. The patched
/// NPC records themselves are deliberately excluded (trivially one row per selection) — but an
/// NPC pulled in as a NEW record (e.g. via a Template chain) IS logged, since that is exactly
/// the kind of surprise this report exists to explain.
///
/// <para>Columns: <c>OutputFormKey, SourceFormKey, EditorID, Type, Kind, ProvenanceHistory</c>.
/// <c>OutputFormKey</c> is the record's FormKey in the generated plugin; <c>SourceFormKey</c> is
/// the record it was copied from (identical for overrides; empty for generated records).
/// <c>Type</c> is the record type's registration name (e.g. Armor, ArmorAddon, TextureSet).
/// <c>Kind</c> is one of MergedAsNew, Override, DeltaPatchedOverride, BulkOverrideImport, or
/// Generated. <c>ProvenanceHistory</c> is a single cell of the form
/// <c>FormKey (EditorID) -> FormKey (EditorID) -> ... -> FormKey (EditorID)</c> — the reference
/// chain from the root NPC being patched, through each intermediate record, down to this record
/// (last element). All history FormKeys/EditorIDs are SOURCE-side identities (what the traversal
/// actually walked), with <c>(NULL)</c> for records without an EditorID. Bulk imports have no
/// reference chain (nothing was traversed), so their history is a
/// <c>(bulk override import from plugin.esp)</c> placeholder.</para>
///
/// <para>One row per output record, first discovery wins. This mirrors the merge machinery
/// itself: once a record is merged in, later NPCs re-use the mapped copy and the walkers never
/// re-traverse it, so the first chain is the only complete one that exists.</para>
///
/// <para>Disabled by default. End users enable it with the <b>"Log Record Provenance"</b>
/// checkbox in Settings &gt; Logging (persisted, applied at runtime — it takes effect on the
/// next patch run, no restart needed). For a quick dev repro it can also be forced on by
/// dropping a file named <c>LogRecordProvenance.txt</c> next to the exe (mirrors
/// <see cref="AssetProvenanceDiag"/>); when that file is present the log stays on regardless of
/// the checkbox. When disabled, every call site is a single early-return on a static bool, so
/// the record-keeping is skipped entirely.</para>
///
/// <para>Writes to <c>&lt;exe-dir&gt;\RecordProvenance.csv</c>, overwritten each run (it is a
/// snapshot of the latest run, not a rolling trace). NOT thread-hardened beyond a coarse lock:
/// the patcher processes NPCs sequentially, and only patcher code calls in.</para>
/// </summary>
public static class RecordProvenanceDiag
{
    private const string CsvFileName = "RecordProvenance.csv";
    private const string TriggerFileName = "LogRecordProvenance.txt";

    /// <summary>One element of a provenance chain: a traversed record's source-side identity.
    /// Walkers accumulate these; formatting to "FormKey (EditorID)" happens inside the diag.</summary>
    public readonly record struct Node(FormKey FormKey, string? EditorId);

    /// <summary>Effective on/off state. Hot-path call sites check this to skip all
    /// record-keeping when nobody is listening. Driven by the user setting via
    /// <see cref="SetEnabled"/>, and force-on by the dev file trigger.</summary>
    public static bool IsEnabled { get; private set; }

    // True when the dev file trigger (LogRecordProvenance.txt) was found at startup. When set,
    // the log stays on regardless of the user setting, so SetEnabled(false) can't turn it off.
    private static bool _fileTriggerPresent;

    private static readonly object _lock = new();

    // The NPC currently being patched (patching is strictly sequential). Chains whose walk root
    // is not this NPC get its node prepended, so every history starts at the root NPC record.
    private static FormKey _currentNpcFormKey;
    private static string? _currentNpcEditorId;

    private sealed record Row(FormKey OutputFormKey, FormKey SourceFormKey, string? EditorId,
        string? RecordType, string Kind, string History);

    // Output FormKey -> its row. First discovery wins (output FormKeys are unique anyway;
    // overrides can be reported twice — delta-patch then forward — and the first report sticks).
    private static readonly Dictionary<FormKey, Row> _rowsByOutput = new();

    // Full formatted chain (INCLUDING the record's own node as last element), keyed by BOTH the
    // source and the output FormKey. Later walks rooted at an already-merged record (e.g. the
    // sub-record sweep that follows override merge-in) join onto this instead of starting fresh.
    private static readonly Dictionary<FormKey, List<string>> _chainByFormKey = new();

    // Chains captured at override-DISCOVERY time (CollectOverriddenDependencyRecords), keyed by
    // the override's FormKey. Consumed when the override is actually written to the output.
    private static readonly Dictionary<FormKey, List<string>> _pendingOverrideChains = new();

    /// <summary>Call once during app startup, before <see cref="SetEnabled"/>. Presence of
    /// <c>LogRecordProvenance.txt</c> next to the exe force-enables the log (a dev fallback).
    /// Safe to call multiple times.</summary>
    public static void InitializeFromFileTrigger()
    {
        try
        {
            string triggerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TriggerFileName);
            if (File.Exists(triggerPath))
            {
                _fileTriggerPresent = true;
                IsEnabled = true;
            }
        }
        catch
        {
            // Swallow — if the probe itself throws, leave IsEnabled false.
        }
    }

    /// <summary>
    /// Applies the persisted "Log Record Provenance" user setting. The dev file trigger, if
    /// present, always keeps the log on regardless of <paramref name="enabledInSettings"/>.
    /// Safe to call at startup (after <see cref="InitializeFromFileTrigger"/>) and at runtime —
    /// the Settings checkbox calls this, and because <see cref="IsEnabled"/> is read afresh each
    /// patch run, toggling it between runs takes effect with no restart.
    /// </summary>
    public static void SetEnabled(bool enabledInSettings)
    {
        IsEnabled = enabledInSettings || _fileTriggerPresent;
    }

    /// <summary>Clears all accumulated state. Called once at the start of a patch run.</summary>
    public static void Reset()
    {
        if (!IsEnabled) return;
        lock (_lock)
        {
            _rowsByOutput.Clear();
            _chainByFormKey.Clear();
            _pendingOverrideChains.Clear();
            _currentNpcFormKey = FormKey.Null;
            _currentNpcEditorId = null;
        }
    }

    /// <summary>Marks the NPC whose patching is about to run. Every chain recorded until the
    /// next call is rooted at (or prepended with) this NPC. Safe as a static because the
    /// patcher processes NPCs strictly sequentially.</summary>
    public static void SetCurrentNpc(FormKey npcFormKey, string? editorId)
    {
        if (!IsEnabled) return;
        lock (_lock)
        {
            _currentNpcFormKey = npcFormKey;
            _currentNpcEditorId = editorId;
        }
    }

    /// <summary>
    /// Records that <paramref name="sourceFormKey"/> was deep-copied into the output as the new
    /// record <paramref name="outputFormKey"/>. <paramref name="parentChain"/> is the traversal
    /// path from the walk's root down to (but excluding) this record, in source-side identities;
    /// the diag joins it onto an already-known chain for the walk root (or prepends the current
    /// NPC) so the history always starts at the root NPC record.
    /// </summary>
    public static void RecordMergedAsNew(FormKey sourceFormKey, string? sourceEditorId,
        string? recordType, FormKey outputFormKey, IReadOnlyList<Node>? parentChain)
    {
        if (!IsEnabled || sourceFormKey.IsNull || outputFormKey.IsNull) return;
        lock (_lock)
        {
            if (_rowsByOutput.ContainsKey(outputFormKey)) return;
            var chain = BuildChain(parentChain, sourceFormKey, sourceEditorId);
            _rowsByOutput[outputFormKey] = new Row(outputFormKey, sourceFormKey, sourceEditorId,
                recordType, "MergedAsNew", string.Join(" -> ", chain));
            _chainByFormKey.TryAdd(sourceFormKey, chain);
            _chainByFormKey.TryAdd(outputFormKey, chain);
        }
    }

    /// <summary>
    /// Records the traversal chain by which the override-discovery walk reached
    /// <paramref name="formKey"/>. Called at DISCOVERY time; the row itself is only emitted if
    /// the override is later actually written (<see cref="RecordOverrideWritten"/>).
    /// </summary>
    public static void RecordOverrideDiscoveryChain(FormKey formKey, string? editorId,
        IReadOnlyList<Node>? parentChain)
    {
        if (!IsEnabled || formKey.IsNull) return;
        lock (_lock)
        {
            _pendingOverrideChains.TryAdd(formKey, BuildChain(parentChain, formKey, editorId));
        }
    }

    /// <summary>
    /// Records that an override of <paramref name="formKey"/> (same FormKey in the output) was
    /// written to the output plugin, either forwarded whole or delta-patched. Uses the chain
    /// captured at discovery time when one exists; otherwise falls back to
    /// <paramref name="discoveryNote"/> (e.g. for the all-overrides plugin scan, which does no
    /// traversal) or to a minimal current-NPC -> record chain.
    /// </summary>
    public static void RecordOverrideWritten(FormKey formKey, string? editorId, string? recordType,
        bool deltaPatched, string? discoveryNote = null)
    {
        if (!IsEnabled || formKey.IsNull) return;
        lock (_lock)
        {
            if (_rowsByOutput.ContainsKey(formKey)) return;
            List<string> chain;
            if (_pendingOverrideChains.TryGetValue(formKey, out var discovered))
            {
                chain = discovered;
            }
            else if (discoveryNote != null)
            {
                chain = new List<string> { $"({discoveryNote})", FormatNode(formKey, editorId) };
            }
            else
            {
                chain = BuildChain(null, formKey, editorId);
            }

            _rowsByOutput[formKey] = new Row(formKey, formKey, editorId, recordType,
                deltaPatched ? "DeltaPatchedOverride" : "Override", string.Join(" -> ", chain));
            _chainByFormKey.TryAdd(formKey, chain);
        }
    }

    /// <summary>
    /// Records a record imported by the 'Include All' bulk override scan
    /// (<c>DuplicateAllOverrideRecordsAsNew</c>). No reference chain exists — the record was
    /// found by enumerating <paramref name="scannedPlugin"/>, not by traversal — so the history
    /// is a placeholder naming the scanned plugin.
    /// </summary>
    public static void RecordBulkOverrideImport(FormKey sourceFormKey, string? sourceEditorId,
        string? recordType, FormKey outputFormKey, ModKey scannedPlugin)
    {
        if (!IsEnabled || sourceFormKey.IsNull || outputFormKey.IsNull) return;
        lock (_lock)
        {
            if (_rowsByOutput.ContainsKey(outputFormKey)) return;
            var chain = new List<string>
            {
                $"(bulk override import from {scannedPlugin.FileName})",
                FormatNode(sourceFormKey, sourceEditorId),
            };
            _rowsByOutput[outputFormKey] = new Row(outputFormKey, sourceFormKey, sourceEditorId,
                recordType, "BulkOverrideImport", string.Join(" -> ", chain));
            _chainByFormKey.TryAdd(sourceFormKey, chain);
            _chainByFormKey.TryAdd(outputFormKey, chain);
        }
    }

    /// <summary>Records a record NPC2 authored itself (currently: generated keywords). It has
    /// no source record and no reference chain.</summary>
    public static void RecordGenerated(FormKey outputFormKey, string? editorId, string? recordType)
    {
        if (!IsEnabled || outputFormKey.IsNull) return;
        lock (_lock)
        {
            if (_rowsByOutput.ContainsKey(outputFormKey)) return;
            var chain = new List<string> { "(generated by NPC2)", FormatNode(outputFormKey, editorId) };
            _rowsByOutput[outputFormKey] = new Row(outputFormKey, FormKey.Null, editorId,
                recordType, "Generated", string.Join(" -> ", chain));
        }
    }

    /// <summary>Drops the row for an output record that was removed again during rollback
    /// (an NPC's patching failed partway and its orphaned contributions were deleted), so the
    /// report only lists records actually present in the output.</summary>
    public static void RemoveOutputRecord(FormKey outputFormKey)
    {
        if (!IsEnabled || outputFormKey.IsNull) return;
        lock (_lock)
        {
            _rowsByOutput.Remove(outputFormKey);
        }
    }

    /// <summary>
    /// Writes the accumulated report to <c>RecordProvenance.csv</c> (overwrite), one row per
    /// output record, sorted by output FormKey. No-op when disabled or nothing was recorded.
    /// Call after a patch run's record work has finished. Never throws.
    /// </summary>
    public static void Flush()
    {
        if (!IsEnabled) return;
        try
        {
            List<Row> rows;
            lock (_lock)
            {
                rows = _rowsByOutput.Values
                    .OrderBy(r => r.OutputFormKey.ToString(), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var sb = new StringBuilder();
            sb.Append("OutputFormKey,SourceFormKey,EditorID,Type,Kind,ProvenanceHistory\r\n");
            foreach (var r in rows)
            {
                sb.Append(Csv(r.OutputFormKey.ToString())).Append(',')
                  .Append(Csv(r.SourceFormKey.IsNull ? string.Empty : r.SourceFormKey.ToString())).Append(',')
                  .Append(Csv(r.EditorId)).Append(',')
                  .Append(Csv(r.RecordType)).Append(',')
                  .Append(Csv(r.Kind)).Append(',')
                  .Append(Csv(r.History)).Append("\r\n");
            }

            string outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CsvFileName);
            File.WriteAllText(outPath, sb.ToString());
        }
        catch
        {
            // Diagnostic only — never let logging failure affect a patch run.
        }
    }

    // ---------------------------------------------------------------------
    // Chain assembly
    // ---------------------------------------------------------------------

    /// <summary>
    /// Builds the full formatted chain for a record: [root NPC ...] parents ... self.
    /// If the walk's root (first parent) is itself a record whose chain is already known, that
    /// chain replaces the root node (joining sub-walks onto their originating walk); otherwise
    /// the current NPC's node is prepended unless the root already IS the current NPC.
    /// Caller must hold <see cref="_lock"/>.
    /// </summary>
    private static List<string> BuildChain(IReadOnlyList<Node>? parentChain, FormKey selfFormKey,
        string? selfEditorId)
    {
        var chain = new List<string>();
        if (parentChain != null && parentChain.Count > 0)
        {
            var root = parentChain[0];
            if (_chainByFormKey.TryGetValue(root.FormKey, out var knownRootChain))
            {
                chain.AddRange(knownRootChain); // already ends with the root's own node
            }
            else
            {
                PrependCurrentNpcIfForeign(chain, root.FormKey);
                chain.Add(FormatNode(root.FormKey, root.EditorId));
            }

            for (int i = 1; i < parentChain.Count; i++)
            {
                chain.Add(FormatNode(parentChain[i].FormKey, parentChain[i].EditorId));
            }
        }
        else
        {
            PrependCurrentNpcIfForeign(chain, selfFormKey);
        }

        chain.Add(FormatNode(selfFormKey, selfEditorId));
        return chain;
    }

    private static void PrependCurrentNpcIfForeign(List<string> chain, FormKey chainRoot)
    {
        if (!_currentNpcFormKey.IsNull && chainRoot != _currentNpcFormKey)
        {
            chain.Add(FormatNode(_currentNpcFormKey, _currentNpcEditorId));
        }
    }

    private static string FormatNode(FormKey formKey, string? editorId)
        => $"{formKey} ({(string.IsNullOrEmpty(editorId) ? "NULL" : editorId)})";

    /// <summary>Escapes a field per RFC 4180: quote when it contains a comma, quote, or newline,
    /// doubling any embedded quotes.</summary>
    private static string Csv(string? field)
    {
        field ??= string.Empty;
        if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
        return field;
    }
}
