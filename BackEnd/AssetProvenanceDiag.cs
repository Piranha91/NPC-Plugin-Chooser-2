using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Opt-in, per-run provenance report answering "why did asset file X get copied into the
/// output, and which NPC(s)/mod/record pulled it in?". The normal asset pipeline
/// (<see cref="AssetHandler"/>) de-duplicates copies by <c>{destRelPath}|{modDisplayName}</c>
/// and drops the requesting NPC once a copy is scheduled, so that question is otherwise
/// unanswerable. This accumulator records EVERY reference (independent of the copy dedup),
/// plus the resolved source (loose vs BSA) for each unique file, then writes one CSV row per
/// atomic reference at the end of a patch run — sort/pivot in a spreadsheet to view by-file or
/// by-NPC.
///
/// <para>Columns: <c>DestFile, Reason, Referencer, NPC, TargetFormKey, Mod, DonorFormKey,
/// DonorEditorID, SourceKind, SourcePath</c>. <c>Reason</c> is one of FaceGen (the NPC's own
/// FaceGen nif/dds), PluginRef (referenced by the appearance NPC's records), NifTexture (a
/// texture found inside a copied NIF), SmpXml (an SMP/HDT physics config linked from a NIF or
/// XML), or AssetLink (a direct asset link on a record). <c>Referencer</c> names the specific
/// referencing record for PluginRef (e.g. <c>HeadPart 'Hair01' [ID]</c>) or the source NIF/XML
/// for NifTexture/SmpXml.</para>
///
/// <para>Disabled by default. End users enable it with the <b>"Log Asset Provenance"</b>
/// checkbox in Settings &gt; Logging (persisted, applied at runtime — it takes effect on the
/// next patch run, no restart needed). For a quick dev repro it can also be forced on by
/// dropping a file named <c>LogAssetProvenance.txt</c> next to the exe (mirrors
/// <see cref="BsaContentsDiag"/>/<c>StartupLogger</c>); when that file is present the log stays
/// on regardless of the checkbox. When disabled, every call site is a single early-return on a
/// static bool, so the record-keeping is skipped entirely.</para>
///
/// <para>Writes to <c>&lt;exe-dir&gt;\AssetProvenance.csv</c>, overwritten each run (it is a
/// snapshot of the latest run, not a rolling trace).</para>
/// </summary>
public static class AssetProvenanceDiag
{
    private const string CsvFileName = "AssetProvenance.csv";
    private const string TriggerFileName = "LogAssetProvenance.txt";

    /// <summary>SourceKind recorded when a copy was suppressed by base-game-overwrite
    /// protection: the file sits at a base game / Creation Club asset path and the source
    /// mod's "Overwrite Base Game Assets" option is unchecked, so nothing was written.</summary>
    public const string SkippedBaseGameOverwriteKind = "SkippedBaseGameOverwrite";

    /// <summary>Effective on/off state. Hot-path call sites check this to skip all
    /// record-keeping when nobody is listening. Driven by the user setting via
    /// <see cref="SetEnabled"/>, and force-on by the dev file trigger.</summary>
    public static bool IsEnabled { get; private set; }

    // True when the dev file trigger (LogAssetProvenance.txt) was found at startup. When set, the
    // log stays on regardless of the user setting, so SetEnabled(false) can't turn it off.
    private static bool _fileTriggerPresent;

    // destRelPath (case-insensitive) -> its accumulated provenance.
    private static readonly ConcurrentDictionary<string, FileProvenance> _byFile =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call once during app startup, before <see cref="SetEnabled"/>. Presence of
    /// <c>LogAssetProvenance.txt</c> next to the exe force-enables the log (a dev fallback).
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
    /// Applies the persisted "Log Asset Provenance" user setting. The dev file trigger, if
    /// present, always keeps the log on regardless of <paramref name="enabledInSettings"/>.
    /// Safe to call at startup (after <see cref="InitializeFromFileTrigger"/>) and at runtime —
    /// the Settings checkbox calls this, and because <see cref="IsEnabled"/> is read afresh each
    /// patch run, toggling it between runs takes effect with no restart.
    /// </summary>
    public static void SetEnabled(bool enabledInSettings)
    {
        IsEnabled = enabledInSettings || _fileTriggerPresent;
    }

    /// <summary>Clears the accumulated map. Called once at the start of a patch run.</summary>
    public static void Reset()
    {
        if (!IsEnabled) return;
        _byFile.Clear();
    }

    /// <summary>
    /// Records that an NPC (via a mod, and for PluginRef via a specific record) referenced
    /// <paramref name="destRelPath"/>. Called on EVERY asset request — before the copy dedup —
    /// so a file pulled in by many NPCs/records lists them all. Identical tuples collapse.
    /// </summary>
    public static void RecordReference(string destRelPath, string modDisplayName, AssetRequestContext ctx)
    {
        if (!IsEnabled || string.IsNullOrEmpty(destRelPath)) return;
        var rec = _byFile.GetOrAdd(destRelPath, static p => new FileProvenance(p));
        rec.AddReference(new AssetReference(
            ctx.NpcDisplay(),
            ctx.TargetFormKeyString(),
            modDisplayName ?? "(unknown mod)",
            ctx.Reason ?? "?",
            ctx.Referencer ?? string.Empty,
            ctx.DonorFormKeyCsv(),
            ctx.DonorEditorIdCsv()));
    }

    /// <summary>
    /// Records where the bytes for <paramref name="destRelPath"/> came from, as resolved for a
    /// given mod (loose path, BSA path, or "not found"). Called once per unique file+mod, from
    /// inside the copy task. <paramref name="sourceKind"/> is the AssetHandler source-type name.
    /// </summary>
    public static void RecordSource(string destRelPath, string modDisplayName, string sourceKind, string? sourcePath)
    {
        if (!IsEnabled || string.IsNullOrEmpty(destRelPath)) return;
        var rec = _byFile.GetOrAdd(destRelPath, static p => new FileProvenance(p));
        rec.SetSource(modDisplayName ?? "(unknown mod)", FriendlyKind(sourceKind), sourcePath ?? string.Empty);
    }

    /// <summary>
    /// Writes the accumulated provenance report to <c>AssetProvenance.csv</c> (overwrite), one
    /// row per atomic reference (file × NPC × mod × reason × referencer). No-op when disabled or
    /// nothing was recorded. Call after all asset copying for the run has finished. Never throws.
    /// </summary>
    public static void Flush()
    {
        if (!IsEnabled) return;
        try
        {
            var sb = new StringBuilder();
            sb.Append("DestFile,Reason,Referencer,NPC,TargetFormKey,Mod,DonorFormKey,DonorEditorID,SourceKind,SourcePath\r\n");

            foreach (var f in _byFile.Values.OrderBy(f => f.DestRelPath, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var r in f.SnapshotReferences())
                {
                    var (kind, path) = f.SourceForMod(r.ModDisplayName);
                    sb.Append(Csv(f.DestRelPath)).Append(',')
                      .Append(Csv(r.Reason)).Append(',')
                      .Append(Csv(r.Referencer)).Append(',')
                      .Append(Csv(r.NpcDisplay)).Append(',')
                      .Append(Csv(r.TargetFormKey)).Append(',')
                      .Append(Csv(r.ModDisplayName)).Append(',')
                      .Append(Csv(r.DonorFormKey)).Append(',')
                      .Append(Csv(r.DonorEditorId)).Append(',')
                      .Append(Csv(kind)).Append(',')
                      .Append(Csv(path)).Append("\r\n");
                }
            }

            string outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CsvFileName);
            File.WriteAllText(outPath, sb.ToString());
        }
        catch
        {
            // Diagnostic only — never let logging failure affect a patch run.
        }
    }

    private static string FriendlyKind(string sourceKind) => sourceKind switch
    {
        "LooseFile" => "Loose",
        "BsaFile" => "BSA",
        SkippedBaseGameOverwriteKind => SkippedBaseGameOverwriteKind,
        _ => "NotFound",
    };

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

    /// <summary>Accumulated provenance for a single destination file. Thread-safe.</summary>
    private sealed class FileProvenance
    {
        public string DestRelPath { get; }
        private readonly object _lock = new();
        private readonly HashSet<AssetReference> _refs = new();
        // mod display name -> resolved source (kind + path). One entry per mod that sourced it.
        private readonly Dictionary<string, (string Kind, string Path)> _sourceByMod =
            new(StringComparer.OrdinalIgnoreCase);

        public FileProvenance(string destRelPath) => DestRelPath = destRelPath;

        public void AddReference(AssetReference reference)
        {
            lock (_lock) { _refs.Add(reference); }
        }

        public void SetSource(string modDisplayName, string kind, string path)
        {
            lock (_lock) { _sourceByMod[modDisplayName] = (kind, path); }
        }

        public (string Kind, string Path) SourceForMod(string modDisplayName)
        {
            lock (_lock)
            {
                return _sourceByMod.TryGetValue(modDisplayName, out var s) ? s : (string.Empty, string.Empty);
            }
        }

        public List<AssetReference> SnapshotReferences()
        {
            lock (_lock)
            {
                return _refs
                    .OrderBy(r => r.NpcDisplay, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Reason, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Referencer, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    /// <summary>One atomic (file, NPC, mod, reason, referencer) attribution — becomes one CSV row.</summary>
    private readonly record struct AssetReference(
        string NpcDisplay,
        string TargetFormKey,
        string ModDisplayName,
        string Reason,
        string Referencer,
        string DonorFormKey,
        string DonorEditorId);
}

/// <summary>
/// Lightweight, allocation-free context carried alongside an asset-copy request so the opt-in
/// <see cref="AssetProvenanceDiag"/> can attribute each copied file to the NPC/mod/record that
/// pulled it in and why. Holds references to strings/FormKeys the caller already has; display
/// strings are only materialized at flush time. Ignored entirely when the diag is disabled.
/// </summary>
public readonly struct AssetRequestContext
{
    /// <summary>Human-readable identity of the NPC being patched (the patcher's npcIdentifier).</summary>
    public readonly string? NpcIdentifier;
    /// <summary>The NPC in the user's load order whose selection caused this copy.</summary>
    public readonly FormKey TargetNpc;
    /// <summary>The appearance donor NPC supplying the look (== target when not a face swap).</summary>
    public readonly FormKey DonorNpc;
    /// <summary>Donor EditorID, if known.</summary>
    public readonly string? DonorEditorId;
    /// <summary>Why this asset was requested (FaceGen, PluginRef, NifTexture, SmpXml, AssetLink).</summary>
    public readonly string Reason;
    /// <summary>The specific thing that referenced this asset: for PluginRef, the record (e.g.
    /// "HeadPart 'Hair01' [ID]"); for NifTexture/SmpXml, the source NIF/XML file name; else empty.</summary>
    public readonly string Referencer;

    public AssetRequestContext(string? npcIdentifier, FormKey targetNpc, FormKey donorNpc, string? donorEditorId, string reason, string referencer = "")
    {
        NpcIdentifier = npcIdentifier;
        TargetNpc = targetNpc;
        DonorNpc = donorNpc;
        DonorEditorId = donorEditorId;
        Reason = reason;
        Referencer = referencer;
    }

    /// <summary>Copy with a different <see cref="Reason"/> and no referencer (recursively-discovered assets).</summary>
    public AssetRequestContext WithReason(string reason)
        => new(NpcIdentifier, TargetNpc, DonorNpc, DonorEditorId, reason, string.Empty);

    /// <summary>Copy with a different <see cref="Reason"/> and <see cref="Referencer"/>.</summary>
    public AssetRequestContext WithReason(string reason, string referencer)
        => new(NpcIdentifier, TargetNpc, DonorNpc, DonorEditorId, reason, referencer);

    /// <summary>Copy with a different <see cref="Referencer"/>, keeping the reason.</summary>
    public AssetRequestContext WithReferencer(string referencer)
        => new(NpcIdentifier, TargetNpc, DonorNpc, DonorEditorId, Reason, referencer);

    /// <summary>Display identity for the target NPC (identifier if present, else FormKey).</summary>
    public string NpcDisplay()
    {
        if (!string.IsNullOrEmpty(NpcIdentifier)) return NpcIdentifier!;
        return TargetNpc.IsNull ? "(unknown NPC)" : TargetNpc.ToString();
    }

    /// <summary>Target NPC FormKey as a string (empty when null).</summary>
    public string TargetFormKeyString() => TargetNpc.IsNull ? string.Empty : TargetNpc.ToString();

    /// <summary>True when the donor differs from the target (a face swap / shared appearance).</summary>
    public bool IsFaceSwap => !DonorNpc.IsNull && !DonorNpc.Equals(TargetNpc);

    /// <summary>Donor FormKey, shown only for a face swap (else empty).</summary>
    public string DonorFormKeyCsv() => IsFaceSwap ? DonorNpc.ToString() : string.Empty;

    /// <summary>Donor EditorID, shown only for a face swap (else empty).</summary>
    public string DonorEditorIdCsv() => IsFaceSwap ? (DonorEditorId ?? string.Empty) : string.Empty;
}
