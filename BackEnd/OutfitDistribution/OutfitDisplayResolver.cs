using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;

/// <summary>Where the displayed outfit comes from, in priority order of the
/// runtime stack the game actually applies.</summary>
public enum OutfitDisplaySource
{
    /// <summary>No outfit is rendered (attire toggle off, or the NPC resolves
    /// to no outfit).</summary>
    None,
    /// <summary>The appearance donor's outfit (what NPC2 will splice when
    /// "Include Outfit" applies, or Create mode's forwarded record).</summary>
    AppearanceMod,
    /// <summary>The conflict-winning override's outfit in the load order.</summary>
    WinningOverride,
    /// <summary>An external SkyPatcher config's outfitDefault directive.</summary>
    SkyPatcher,
    /// <summary>NPC2's own SkyPatcher ini entry (SkyPatcher mode + Include
    /// Outfit — simulated from current selections).</summary>
    Npc2SkyPatcherIni,
    /// <summary>A SPID Outfit distribution.</summary>
    Spid,
}

/// <summary>
/// Which runtime distributors carry an outfit assignment for this NPC —
/// independent of which one finally WINS. <see cref="OutfitDisplayResult.Source"/>
/// answers "what gets worn"; this answers "who else is competing for the slot",
/// which is what the patcher needs when it has to republish a forwarded
/// wig/antler outfit through those same channels (see
/// <see cref="ForwardedOutfitDistributor"/>).
/// </summary>
public sealed record RuntimeOutfitContest
{
    /// <summary>An EXTERNAL SkyPatcher config assigns this NPC an
    /// <c>outfitDefault</c> (NPC2's own simulated entry doesn't count — it is
    /// not something NPC2 has to fight).</summary>
    public bool SkyPatcher { get; init; }
    public string? SkyPatcherDetail { get; init; }

    /// <summary>True when that winning external SkyPatcher line is read AFTER
    /// NPC2's own ini slot in the config walk. SkyPatcher applies the LAST
    /// matching assignment, so in that case an NPC2 <c>outfitDefault</c>
    /// directive would be overwritten rather than winning.</summary>
    public bool SkyPatcherOutranksNpc2Ini { get; init; }

    /// <summary>A SPID <c>*_DISTR.ini</c> entry distributes an outfit to this
    /// NPC. Reported even when the SkyPatcher layer suspends it in the CURRENT
    /// setup: republishing changes what the record and the runtime layers hold,
    /// which can lift that suspension, so the patcher must still cover SPID.</summary>
    public bool Spid { get; init; }
    public string? SpidDetail { get; init; }

    /// <summary>File name of the contesting <c>*_DISTR.ini</c>. SPID applies the
    /// FIRST matching entry in ordinal file-name order, so NPC2's own file has to
    /// sort before this one to win.</summary>
    public string? SpidSourceFile { get; init; }

    /// <summary>The config that actually supplied this NPC's outfit BEFORE NPC2
    /// republished it — the conflict winner of the runtime stack (SkyPatcher outranks
    /// SPID), matching <see cref="OutfitDisplayResult.SourceDetail"/>. Recorded as a
    /// comment on each generated line so the provenance is readable in the ini.</summary>
    public string? WinningSourceDetail { get; init; }

    public bool Any => SkyPatcher || Spid;

    public static readonly RuntimeOutfitContest None = new();
}

/// <summary>The outcome of outfit-display resolution for one NPC.</summary>
/// <summary>Why a Default-Outfit wig will not reach the patch output. Both values
/// mean "the wig is dropped"; they differ in what the user has to change.</summary>
public enum WigDropReason
{
    /// <summary>Nothing is dropped.</summary>
    None,

    /// <summary>Wig Handling Mode is Leave As Is and the outfit carrying the wig is
    /// not being forwarded either. Fix: pick a handling mode, or Include Outfits.</summary>
    ModeLeaveAsIs,

    /// <summary>Wig handling cannot run at all in plain Create record mode, so the
    /// mode dropdown is not the lever here — the output mode is.</summary>
    InertInCreateMode
}

/// <summary>Result of <see cref="OutfitDisplayResolver.ComputeWigPersistence"/>:
/// whether an NPC's outfit wig(s) survive into the output, and which don't.</summary>
public sealed record WigPersistenceResult(WigDropReason Reason, IReadOnlyList<NpcWigSource> DroppedSources)
{
    public bool AnyDropped => Reason != WigDropReason.None;

    public static readonly WigPersistenceResult Persisted =
        new(WigDropReason.None, Array.Empty<NpcWigSource>());

    /// <summary>One-line statement of the problem, for a banner or the first line
    /// of a tooltip. Callers add their own record detail — the mugshot tile already
    /// names each wig record in its badge tooltip.</summary>
    public string Headline => DroppedSources.Count > 1
        ? "The wigs this mod gives this NPC will NOT be in your output."
        : "The wig this mod gives this NPC will NOT be in your output.";

    /// <summary>What the user has to change to keep the wig, phrased with the
    /// UI's own labels. Differs by <see cref="WigDropReason"/>: in plain Create
    /// mode the Wig Handling Mode dropdown is not the lever.</summary>
    public string FixAdvice => Reason switch
    {
        WigDropReason.ModeLeaveAsIs =>
            "To keep it, set this mod's Wig Handling Mode (Mods tab — or Settings > Head Editing "
            + "for the default) to something other than \"Leave As Is\". Enabling Include Outfits "
            + "for the mod also keeps it, by carrying the whole outfit across.",
        WigDropReason.InertInCreateMode =>
            "Wig handling does nothing in plain Create output mode, whatever Wig Handling Mode is "
            + "set to. To keep it, switch to Create and Patch or SkyPatcher output and pick a Wig "
            + "Handling Mode — or enable Include Outfits for the mod, which carries the whole "
            + "outfit across.",
        _ => string.Empty
    };
}

public sealed record OutfitDisplayResult
{
    public FormKey? OutfitFormKey { get; init; }
    public OutfitDisplaySource Source { get; init; } = OutfitDisplaySource.None;

    /// <summary>True when the outfit record should be resolved through the
    /// appearance mod's plugins (donor content); false = resolve the
    /// conflict-winning record from the load order.</summary>
    public bool UseModScopedResolution { get; init; }

    /// <summary>Human-readable provenance ("SkyPatcher: config.ini (line 12)"),
    /// for tooltips. Null when the outfit is the plain plugin-level one.</summary>
    public string? SourceDetail { get; init; }

    /// <summary>Set for the two spec'd conflict cases: (a) "Include Outfit" is
    /// requested but a runtime distributor overrides it; (b) NPC2's own
    /// SkyPatcher entry is not conflict-winning. Null otherwise.</summary>
    public string? WarningText { get; init; }

    /// <summary>Filters the simulation could not evaluate at record level;
    /// appended to tooltips so approximations are visible.</summary>
    public IReadOnlyList<string> Approximations { get; init; } = Array.Empty<string>();

    /// <summary>Every runtime distributor competing for this NPC's outfit slot,
    /// not just the winner. Consumed by the wig/antler outfit republishing pass.</summary>
    public RuntimeOutfitContest Contest { get; init; } = RuntimeOutfitContest.None;

    /// <summary>True when the NPC's record-level <c>DefaultOutfit</c> is a DEAD FIELD: the
    /// Inventory template flag makes the engine take the whole inventory — outfit included —
    /// from the template, so nothing written to the record's own outfit is ever worn. The
    /// display side of <c>Patcher.RecordOutfitIsInert</c>, which stays the patch-side authority;
    /// keep the two in step. False in SkyPatcher output mode, where the runtime
    /// <c>outfitDefault=</c> directive acts on the actor and bypasses template resolution.</summary>
    public bool RecordOutfitInert { get; init; }

    /// <summary>The record the inventory — and so the worn outfit — is inherited from.
    /// Null unless <see cref="RecordOutfitInert"/>.</summary>
    public FormKey? InventoryTemplateSource { get; init; }

    /// <summary>Cache-identity stamp: the resolved outfit FormKey string, or
    /// "none". Deliberately excludes Source/mode so switching patching modes
    /// that land on the same outfit does not re-stale mugshots.</summary>
    public string IdentityStamp => OutfitFormKey?.ToString() ?? "none";

    public static readonly OutfitDisplayResult NoOutfit = new();
}

/// <summary>
/// Decides which outfit the CharacterViewer should display for an NPC so the
/// preview matches what the game will actually show after NPC2 patching AND
/// runtime distributors (SkyPatcher / SPID) have run:
///
/// <list type="number">
/// <item>Plugin level — mirrors <see cref="Patcher"/>: SkyPatcher mode leaves
/// the winning override untouched; CreateAndPatch keeps the winner's outfit
/// unless "Include Outfit" applies (donor's); Create forwards the donor
/// record wholesale (donor's outfit either way).</item>
/// <item>SkyPatcher runtime — last matching <c>outfitDefault=</c> across the
/// npc configs wins (config walk order per the SkyPatcher source). When NPC2
/// itself is in SkyPatcher mode with Include Outfit, its own (simulated) ini
/// entry participates at its true position:
/// <c>...\npc\NPC Plugin Chooser\&lt;OutputPlugin&gt;.ini</c>.</item>
/// <item>SPID — suppressed entirely when SkyPatcher changed the NPC's default
/// outfit (SPID suspends replacements when the actor's outfit differs from
/// the initial record value — OutfitManager::IsSuspendedReplacement);
/// otherwise the FIRST matching deterministic (chance 100) <c>Outfit=</c>
/// entry in config order applies.</item>
/// <item>Any runtime candidate whose outfit doesn't resolve in the current
/// load order is skipped (falls through to the next candidate).</item>
/// </list>
/// </summary>
public class OutfitDisplayResolver
{
    private readonly Settings _settings;
    private readonly EnvironmentStateProvider _env;
    private readonly RecordHandler _recordHandler;
    private readonly SkyPatcherOutfitConfigParser _skyPatcherParser = new();
    private readonly SpidOutfitConfigParser _spidParser = new();

    /// <summary>NPC2's dedicated SkyPatcher output subfolder (must match
    /// <see cref="SkyPatcherInterface"/>.GetOutputPath).</summary>
    public const string Npc2SkyPatcherSubfolder = "NPC Plugin Chooser";

    private const long ConfigSignatureRecheckMs = 2000;

    private readonly object _gate = new();
    private ConfigCache? _configs;
    private long _lastSignatureCheckTick = long.MinValue;
    private ILinkCache? _cacheOwner; // LinkCache identity the caches were built against

    private readonly ConcurrentDictionary<FormKey, NpcRuntimeFacts?> _factsCache = new();
    private readonly ConcurrentDictionary<string, FormKey?> _skyPatcherIdCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ResolvedFilterForm?> _spidFormCache = new();
    private Dictionary<string, (FormKey Key, SpidFilterFormType Type)>? _editorIdIndex;

    public OutfitDisplayResolver(Settings settings, EnvironmentStateProvider env, RecordHandler recordHandler)
    {
        _settings = settings;
        _env = env;
        _recordHandler = recordHandler;
    }

    /// <summary>One ordered slot in the SkyPatcher config walk: either a real
    /// parsed instruction or the marker where NPC2's own ini would load.</summary>
    private sealed record OrderedSkyPatcherEntry(SkyPatcherOutfitInstruction? Instruction, bool IsNpc2Marker, string SourceFile);

    private sealed class ConfigCache
    {
        public List<OrderedSkyPatcherEntry> SkyPatcherEntries = new();
        public List<SpidOutfitEntry> SpidEntries = new();
        public string Signature = string.Empty;
    }

    public void InvalidateCaches()
    {
        lock (_gate)
        {
            _configs = null;
            _lastSignatureCheckTick = long.MinValue;
            _factsCache.Clear();
            _skyPatcherIdCache.Clear();
            _spidFormCache.Clear();
            _editorIdIndex = null;
            _cacheOwner = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Public entry point
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the outfit to display for <paramref name="targetNpcFormKey"/>
    /// when rendered from <paramref name="modSetting"/> (whose donor record is
    /// <paramref name="sourceNpcFormKey"/> — equal to the target except for
    /// guest appearances). <paramref name="includeDefaultOutfitRenderFlag"/>
    /// is the attire render toggle; when off, the result is the "none" stamp
    /// so mugshot staleness ignores outfit churn entirely.
    /// </summary>
    public OutfitDisplayResult ResolveForDisplay(
        FormKey targetNpcFormKey,
        FormKey sourceNpcFormKey,
        ModSetting? modSetting,
        bool includeDefaultOutfitRenderFlag)
    {
        if (!includeDefaultOutfitRenderFlag) return OutfitDisplayResult.NoOutfit;

        var linkCache = _env.LinkCache;
        if (linkCache == null) return OutfitDisplayResult.NoOutfit;
        EnsureCachesCurrent(linkCache);

        // 1. Donor + winner plugin-level outfits — the RAW record fields, since those are the
        // values the patcher moves around.
        FormKey? donorOutfit = ResolveDonorOutfit(sourceNpcFormKey, modSetting, linkCache);
        linkCache.TryResolve<INpcGetter>(targetNpcFormKey, out var winnerNpc);
        FormKey? winnerOutfit = (winnerNpc?.DefaultOutfit != null && !winnerNpc.DefaultOutfit.IsNull)
            ? winnerNpc.DefaultOutfit.FormKey
            : null;

        // Can a record-level write reach the actor at all? The Inventory template flag makes the
        // engine take the whole inventory — the default outfit with it — from the template, so the
        // record's own DefaultOutfit is a dead field and the outfit actually worn is the
        // template's. The record examined is the one NPC2 WRITES — the recipient's winning override
        // in Create-and-Patch, the donor in Create — exactly as Patcher.RecordOutfitIsInert picks
        // it. SkyPatcher mode is exempt: its outfitDefault= directive acts on the actor and
        // bypasses record-level template resolution entirely.
        var writtenNpc = _settings.PatchingMode == PatchingMode.CreateAndPatch
            ? winnerNpc
            : ResolveDonorNpc(sourceNpcFormKey, modSetting, linkCache);
        FormKey? inventoryTemplateSource = null;
        FormKey? inheritedWornOutfit = writtenNpc == null
            ? null
            : ResolveWornOutfit(writtenNpc, linkCache, modSetting, out inventoryTemplateSource);
        bool recordOutfitInert = !_settings.UseSkyPatcherMode && inventoryTemplateSource != null;

        bool includeOutfit = ComputeIncludeOutfitIntent(targetNpcFormKey, modSetting);

        // 2. Plugin-level effective outfit (mirrors Patcher.cs).
        FormKey? pluginLevel;
        OutfitDisplaySource pluginLevelSource;
        bool pluginLevelModScoped;
        if (modSetting == null)
        {
            (pluginLevel, pluginLevelSource, pluginLevelModScoped) = (winnerOutfit, OutfitDisplaySource.WinningOverride, false);
        }
        else if (_settings.UseSkyPatcherMode)
        {
            // Recipient record is untouched at plugin level in SkyPatcher mode.
            (pluginLevel, pluginLevelSource, pluginLevelModScoped) = (winnerOutfit, OutfitDisplaySource.WinningOverride, false);
        }
        else if (recordOutfitInert)
        {
            // The record write cannot reach the actor at all — the engine resolves the outfit
            // through the inventory template — so whatever NPC2 writes to DefaultOutfit is
            // irrelevant and what gets worn is the template's outfit. This is exactly why
            // Include Outfit is inert for these NPCs.
            (pluginLevel, pluginLevelSource, pluginLevelModScoped) =
                (inheritedWornOutfit, OutfitDisplaySource.WinningOverride, false);
        }
        else if (_settings.PatchingMode == PatchingMode.CreateAndPatch)
        {
            (pluginLevel, pluginLevelSource, pluginLevelModScoped) = includeOutfit && donorOutfit != null
                ? (donorOutfit, OutfitDisplaySource.AppearanceMod, true)
                : (winnerOutfit, OutfitDisplaySource.WinningOverride, false);
        }
        else // PatchingMode.Create — the donor record is forwarded wholesale.
        {
            (pluginLevel, pluginLevelSource, pluginLevelModScoped) = donorOutfit != null
                ? (donorOutfit, OutfitDisplaySource.AppearanceMod, true)
                : (winnerOutfit, OutfitDisplaySource.WinningOverride, false);
        }

        // 3. Runtime layers need the winner record's facts (that is the record
        // the distributors see; NPC2's own plugin edit is modeled through
        // pluginLevel, which doubles as SPID's "initial outfit").
        var facts = winnerNpc != null ? GetFacts(targetNpcFormKey, winnerNpc, linkCache) : null;
        bool npc2IniActive = _settings.UseSkyPatcherMode && modSetting != null && includeOutfit && donorOutfit != null;

        FormKey? skyOutfit = null;
        string? skySourceFile = null;
        int skySourceLine = 0;
        bool skyIsNpc2 = false;
        // Whether the winning EXTERNAL SkyPatcher line sits after NPC2's own ini
        // slot in the walk — i.e. whether an NPC2 outfitDefault directive would be
        // overwritten by it (last assignment wins).
        bool markerPassed = false;
        bool skyMatchAfterMarker = false;
        var approximations = new List<string>();

        if (facts != null)
        {
            foreach (var entry in GetConfigs().SkyPatcherEntries)
            {
                if (entry.IsNpc2Marker)
                {
                    markerPassed = true;
                    if (npc2IniActive)
                    {
                        // NPC2 writes filterByNPCs=<target>:outfitDefault=<donor
                        // outfit> — a direct-list entry, so it always applies to
                        // its own target.
                        skyOutfit = donorOutfit;
                        skySourceFile = entry.SourceFile;
                        skySourceLine = 0;
                        skyIsNpc2 = true;
                    }
                    continue;
                }

                var instruction = entry.Instruction!;
                var match = _skyPatcherParser.MatchesNpc(instruction, facts, ResolveSkyPatcherIdentifier);
                if (!match.Applies) continue;

                // "In the user's load order" gate: the outfit must resolve as
                // an Outfit record, otherwise SkyPatcher leaves the previous
                // assignment in place — mirrored by keeping the prior winner.
                var outfitKey = ResolveSkyPatcherIdentifier(instruction.OutfitIdentifier);
                if (outfitKey == null || !linkCache.TryResolve<IOutfitGetter>(outfitKey.Value, out _))
                {
                    Debug.WriteLine($"OutfitDisplayResolver: SkyPatcher outfit '{instruction.OutfitIdentifier}' " +
                                    $"({instruction.SourceFile}:{instruction.LineNumber}) not in load order; skipped.");
                    continue;
                }

                skyOutfit = outfitKey;
                skySourceFile = instruction.SourceFile;
                skySourceLine = instruction.LineNumber;
                skyIsNpc2 = false;
                skyMatchAfterMarker = markerPassed;
                foreach (var a in match.Approximations) approximations.Add($"SkyPatcher {instruction.SourceFile}: {a}");
            }
        }

        // 4. SPID — suspended for DISPLAY when SkyPatcher changed the outfit away
        // from the record's initial value (OutfitManager::IsSuspendedReplacement:
        // SPID snapshots defaultOutfit at TESNPC::InitItemImpl, SkyPatcher patches
        // later at kDataLoaded, so the two differ and SPID stands down). The match
        // itself is still evaluated so the contest report can list SPID: once NPC2
        // republishes the outfit that suspension may no longer hold.
        FormKey? spidOutfit = null;
        string? spidSourceFile = null;
        int spidSourceLine = 0;
        bool spidSuspended = skyOutfit != null && !Equals(skyOutfit, pluginLevel);
        var spidApproximations = new List<string>();
        if (facts != null)
        {
            foreach (var entry in GetConfigs().SpidEntries)
            {
                var match = _spidParser.MatchesNpc(entry, facts, ResolveSpidFilterForm);
                if (!match.Applies) continue;

                var outfitKey = ResolveSpidFormIdentifier(entry.OutfitForm);
                if (outfitKey == null || !linkCache.TryResolve<IOutfitGetter>(outfitKey.Value, out _))
                {
                    Debug.WriteLine($"OutfitDisplayResolver: SPID outfit '{entry.OutfitForm.Raw}' " +
                                    $"({entry.SourceFile}:{entry.LineNumber}) not in load order; skipped.");
                    continue;
                }

                spidOutfit = outfitKey;
                spidSourceFile = entry.SourceFile;
                spidSourceLine = entry.LineNumber;
                foreach (var a in match.Approximations) spidApproximations.Add($"SPID {entry.SourceFile}: {a}");
                break; // for_first_form: first passing entry wins
            }
        }
        if (!spidSuspended) approximations.AddRange(spidApproximations);

        // 5. Final pick: SkyPatcher > SPID > plugin level.
        FormKey? finalOutfit;
        OutfitDisplaySource finalSource;
        string? sourceDetail = null;
        bool modScoped;
        if (skyOutfit != null)
        {
            finalOutfit = skyOutfit;
            finalSource = skyIsNpc2 ? OutfitDisplaySource.Npc2SkyPatcherIni : OutfitDisplaySource.SkyPatcher;
            sourceDetail = skyIsNpc2
                ? $"NPC2 SkyPatcher ini ({skySourceFile})"
                : $"SkyPatcher: {skySourceFile} (line {skySourceLine})";
            modScoped = skyIsNpc2; // NPC2's directive points at donor content
        }
        else if (spidOutfit != null && !spidSuspended)
        {
            finalOutfit = spidOutfit;
            finalSource = OutfitDisplaySource.Spid;
            sourceDetail = $"SPID: {spidSourceFile} (line {spidSourceLine})";
            modScoped = false;
        }
        else
        {
            finalOutfit = pluginLevel;
            finalSource = pluginLevel != null ? pluginLevelSource : OutfitDisplaySource.None;
            modScoped = pluginLevelModScoped;
        }

        // 6. Warnings (only when the user's Include Outfit intent is defeated —
        // a runtime layer landing on the SAME outfit is not a visible conflict).
        string? warning = null;
        bool intentDefeated = includeOutfit && modSetting != null && donorOutfit != null &&
                              finalOutfit != null && !finalOutfit.Value.Equals(donorOutfit.Value);
        if (intentDefeated)
        {
            string intended = DescribeOutfit(donorOutfit.Value, linkCache);
            string actual = DescribeOutfit(finalOutfit!.Value, linkCache);
            if (recordOutfitInert)
            {
                // Ordered first: this is a RECORD-level defeat, not a runtime one. The outfit never
                // reaches the actor at all, so naming a contesting distributor would misattribute it.
                warning = $"'Include Outfit' is set, but this NPC takes its inventory from template " +
                          $"{DescribeNpc(inventoryTemplateSource!.Value, linkCache)} — the outfit written to " +
                          $"its own record is never worn in game. {actual} is worn instead of {intended}.";
            }
            else if (_settings.UseSkyPatcherMode && skyOutfit != null && !skyIsNpc2)
            {
                warning = $"NPC2's SkyPatcher outfit entry is not conflict-winning: '{skySourceFile}' " +
                          $"(line {skySourceLine}) loads later and sets {actual} instead of {intended}.";
            }
            else if (finalSource == OutfitDisplaySource.SkyPatcher)
            {
                warning = $"'Include Outfit' is set, but SkyPatcher config '{skySourceFile}' (line {skySourceLine}) " +
                          $"overrides the outfit at runtime: {actual} replaces {intended}.";
            }
            else if (finalSource == OutfitDisplaySource.Spid)
            {
                warning = $"'Include Outfit' is set, but SPID file '{spidSourceFile}' (line {spidSourceLine}) " +
                          $"distributes {actual} over {intended} at runtime.";
            }
        }

        bool skyContests = skyOutfit != null && !skyIsNpc2;
        return new OutfitDisplayResult
        {
            OutfitFormKey = finalOutfit,
            Source = finalOutfit != null ? finalSource : OutfitDisplaySource.None,
            UseModScopedResolution = modScoped,
            SourceDetail = sourceDetail,
            WarningText = warning,
            Approximations = approximations.Count > 0 ? approximations.Distinct().ToList() : Array.Empty<string>(),
            RecordOutfitInert = recordOutfitInert,
            InventoryTemplateSource = recordOutfitInert ? inventoryTemplateSource : null,
            Contest = new RuntimeOutfitContest
            {
                SkyPatcher = skyContests,
                SkyPatcherDetail = skyContests ? $"{skySourceFile} (line {skySourceLine})" : null,
                SkyPatcherOutranksNpc2Ini = skyContests && skyMatchAfterMarker,
                Spid = spidOutfit != null,
                SpidDetail = spidOutfit != null ? $"{spidSourceFile} (line {spidSourceLine})" : null,
                SpidSourceFile = spidSourceFile,
                WinningSourceDetail = finalSource is OutfitDisplaySource.SkyPatcher or OutfitDisplaySource.Spid
                    or OutfitDisplaySource.Npc2SkyPatcherIni
                    ? sourceDetail
                    : null,
            },
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Patcher-intent helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether every wig this NPC carries from <paramref name="modSetting"/> will
    /// actually reach the patch output, and if not, which ones won't and why.
    ///
    /// <para>Only Default-Outfit wigs can be lost. A skin-carried (WornArmor) wig
    /// rides the appearance forward whatever the mode is, so it is never reported.
    /// An outfit wig survives if ANY handling mode is active (each one forwards or
    /// bakes it), or if the outfit itself is forwarded — Include Outfits carries
    /// the wig along with everything else in it, per-NPC override included.</para>
    ///
    /// <para>Patch-side deliberately: this reads <see cref="Settings.GetEffectiveWigMode"/>,
    /// not the Render variant, so the dev/harness override cannot change a claim
    /// about the output.</para>
    /// </summary>
    public WigPersistenceResult ComputeWigPersistence(
        FormKey sourceNpcFormKey, FormKey targetNpcFormKey, ModSetting? modSetting)
    {
        if (modSetting == null) return WigPersistenceResult.Persisted;

        var outfitWigs = _settings.GetEffectiveNpcWigSources(modSetting, sourceNpcFormKey)
            .Where(s => s.Kind == NpcWigSourceKind.Outfit).ToList();
        if (outfitWigs.Count == 0) return WigPersistenceResult.Persisted;

        if (_settings.GetEffectiveWigMode(modSetting) != WigHandlingMode.None)
            return WigPersistenceResult.Persisted;
        if (ComputeIncludeOutfitIntent(targetNpcFormKey, modSetting))
            return WigPersistenceResult.Persisted;

        // Mode is inert either because the user left it at Leave As Is, or because
        // plain Create record mode cannot act on wigs at all — different fixes, so
        // the reason is carried out to the notice text rather than flattened.
        var reason = _settings.WigHandlingActiveForOutputMode
            ? WigDropReason.ModeLeaveAsIs
            : WigDropReason.InertInCreateMode;
        return new WigPersistenceResult(reason, outfitWigs);
    }

    /// <summary>Mirrors Patcher.cs's includeOutfit resolution: the per-NPC
    /// override (keyed by the TARGET NPC) wins over the mod-level flag.</summary>
    private bool ComputeIncludeOutfitIntent(FormKey targetNpcFormKey, ModSetting? modSetting)
    {
        if (modSetting == null) return false;
        if (_settings.NpcOutfitOverrides.TryGetValue(targetNpcFormKey, out var choice))
        {
            return choice switch
            {
                OutfitOverride.No => false,
                OutfitOverride.Yes => true,
                _ => modSetting.IncludeOutfits,
            };
        }
        return modSetting.IncludeOutfits;
    }

    /// <summary>Resolves the donor NPC's DefaultOutfit through the mod's
    /// plugins (disambiguation key first, then CorrespondingModKeys in reverse
    /// with Winner fallback — the same scoping NpcMeshResolver uses). Deliberately the RAW
    /// record field, not the inventory-template-resolved one: this is the value
    /// <c>CopyAppearanceData</c> writes when Include Outfit is on, so it is what the intent is
    /// measured against. Whether that write can actually reach the actor is a separate question,
    /// answered by <see cref="ResolveInventoryOutfitSource"/>.</summary>
    private FormKey? ResolveDonorOutfit(FormKey sourceNpcFormKey, ModSetting? modSetting, ILinkCache linkCache)
    {
        var donor = ResolveDonorNpc(sourceNpcFormKey, modSetting, linkCache);
        if (donor?.DefaultOutfit == null || donor.DefaultOutfit.IsNull) return null;
        return donor.DefaultOutfit.FormKey;
    }

    /// <summary>The outfit an NPC actually wears once the INVENTORY template chain is followed —
    /// the template's when the flag is set, the record's own otherwise. Null when neither names
    /// one.</summary>
    private FormKey? ResolveWornOutfit(INpcGetter npc, ILinkCache linkCache, ModSetting? modSetting,
        out FormKey? inheritedFrom)
    {
        var source = ResolveInventoryOutfitSource(npc, linkCache, modSetting, out inheritedFrom);
        if (source.DefaultOutfit == null || source.DefaultOutfit.IsNull) return null;
        return source.DefaultOutfit.FormKey;
    }

    /// <summary>
    /// Walks the INVENTORY template chain and returns the record whose <c>DefaultOutfit</c> the
    /// engine actually resolves. The Inventory template flag makes an actor take its whole
    /// inventory — default outfit included — from its template, so the NPC's own
    /// <c>DefaultOutfit</c> is a dead field and displaying it shows an outfit the game will
    /// never put on. Returns <paramref name="npc"/> unchanged when it does not inherit its
    /// inventory, or when the chain is unfollowable (dangling, leveled, or cyclic) — the record's
    /// own field is then the best available answer.
    ///
    /// <para>Deliberately distinct from the TRAITS chain the FaceGen ladder walks
    /// (<c>Auxilliary.TryResolveAppearanceTerminus</c>): the two flags are independent, and an
    /// NPC may inherit one without the other. Each hop resolves mod-scoped first so a mod that
    /// re-points the template is honoured, matching <c>NpcMeshResolver.ResolveAppearanceNpcKey</c>.
    /// </para>
    /// </summary>
    private INpcGetter ResolveInventoryOutfitSource(INpcGetter npc, ILinkCache linkCache,
        ModSetting? modSetting, out FormKey? inheritedFrom)
    {
        inheritedFrom = null;
        var current = npc;
        var visited = new HashSet<FormKey> { npc.FormKey };
        int guard = 0;
        while (guard++ < 10)
        {
            if (!current.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Inventory)) break;
            if (current.Template is not { IsNull: false } templateLink) break;
            if (!visited.Add(templateLink.FormKey)) break; // cycle
            var next = ResolveDonorNpc(templateLink.FormKey, modSetting, linkCache);
            if (next == null) break; // dangling, or a leveled template — nothing to read an outfit off
            inheritedFrom = next.FormKey;
            current = next;
        }

        return current;
    }

    /// <summary>The donor NPC record resolved through the mod's plugins
    /// (disambiguation key first, then CorrespondingModKeys in reverse with
    /// Winner fallback), falling back to the load-order winner.</summary>
    private INpcGetter? ResolveDonorNpc(FormKey sourceNpcFormKey, ModSetting? modSetting, ILinkCache linkCache)
    {
        INpcGetter? donor = null;
        if (modSetting != null && modSetting.CorrespondingModKeys.Count > 0)
        {
            var link = sourceNpcFormKey.ToLink<INpcGetter>();
            var folders = modSetting.CorrespondingFolderPaths.ToHashSet();
            if (modSetting.NpcPluginDisambiguation.TryGetValue(sourceNpcFormKey, out var disKey) &&
                _recordHandler.TryGetRecordGetterFromMod(link, disKey, folders,
                    RecordHandler.RecordLookupFallBack.None, out var disRec) &&
                disRec is INpcGetter disNpc)
            {
                donor = disNpc;
            }
            else if (_recordHandler.TryGetRecordFromMods(link, modSetting.CorrespondingModKeys, folders,
                         RecordHandler.RecordLookupFallBack.Winner, out var rec, reverseOrder: true) &&
                     rec is INpcGetter recNpc)
            {
                donor = recNpc;
            }
        }
        if (donor == null)
        {
            linkCache.TryResolve<INpcGetter>(sourceNpcFormKey, out donor);
        }
        return donor;
    }

    /// <summary>
    /// The wig-forwarding contribution to the depicted-attire identity stamp
    /// (appended to <see cref="OutfitDisplayResult.IdentityStamp"/> by the
    /// mugshot metadata stamp and the staleness checker's identity provider).
    /// Empty when nothing wig-related is depicted — mode inert, no detected
    /// wig/antler in the donor's outfit, or a ForwardToOutfit wig with the
    /// outfit toggle off. Mirrors NpcMeshResolver's render plan (including the
    /// no-WNAM ForwardToSkin → ForwardToOutfit fallback) so a stale PNG is
    /// re-rendered exactly when the depicted wig state changes. Deliberately
    /// content-based (mode + sorted wig FormKeys), not a mode-only tag: adding
    /// or removing a wig from the mod's outfit re-stales the tile too.
    /// </summary>
    public string ComputeWigIdentitySuffix(FormKey sourceNpcFormKey, ModSetting? modSetting,
        bool includeDefaultOutfitRenderFlag)
    {
        if (modSetting == null) return string.Empty;

        // RaceMenu tint-emulation marker. The emulation only repaints a WORN
        // hair-slot wig, so it belongs on the per-tile identity rather than the
        // global settings hash — hashing it for everyone made one toggle flip
        // re-render the whole library. It is emitted here, LIVE from the current
        // wig detections, deliberately NOT from a value stamped into the PNG: a
        // stamped "this NPC has no wig" goes wrong the moment a mod updates to
        // add one, and would then suppress the very re-render that update needs.
        // Computed before the handling-mode gate below because a worn wig is
        // repainted whatever the handling mode is — including None, and
        // including plain Create record mode where wig handling is inert.
        string tintMarker =
            _settings.InternalMugshot.EmulateRaceMenuHairSlotTint &&
            _settings.GetEffectiveNpcWigSources(modSetting, sourceNpcFormKey).Count > 0
                ? "+wigtint"
                : string.Empty;

        var wigMode = _settings.GetEffectiveRenderWigMode(modSetting);
        var antlerMode = _settings.GetEffectiveRenderAntlerMode(modSetting);
        // Both modes inert still needs the walk when the mod has outfit wigs or
        // antlers: the mugshot depicts those regardless of mode (+wigdepict /
        // +antlerdepict below). HasWigArmors/HasAntlerArmors are the cheap
        // outfit-ARMO-only prechecks, so a mod with nothing to depict keeps the old
        // zero-cost bail.
        if (wigMode == WigHandlingMode.None && antlerMode == AntlerHandlingMode.None &&
            !modSetting.HasWigArmors && !modSetting.HasAntlerArmors)
        {
            return tintMarker;
        }
        var linkCache = _env.LinkCache;
        if (linkCache == null) return tintMarker;

        var donor = ResolveDonorNpc(sourceNpcFormKey, modSetting, linkCache);
        if (donor == null) return tintMarker;
        bool hasWnam = donor.WornArmor != null && !donor.WornArmor.IsNull;

        // Same INVENTORY-template walk ResolveForDisplay does, so the wig stamp and the outfit
        // stamp cannot disagree about which outfit is depicted — INCLUDING the SkyPatcher-mode
        // exemption. There the outfitDefault= directive acts on the actor and reaches it whatever
        // the record's template flag says, so the donor's own outfit IS what gets worn and the
        // chain must not be followed. (The WNAM segment further down is Traits-governed, not
        // Inventory-governed, and deliberately does not walk this chain at all.)
        var outfitSource = _settings.UseSkyPatcherMode
            ? donor
            : ResolveInventoryOutfitSource(donor, linkCache, modSetting, out _);

        var folders = modSetting.CorrespondingFolderPaths.ToHashSet();
        var sb = new StringBuilder(tintMarker);
        bool outfitHasDetectedWig = false;

        // Outfit (source 1) segments — unchanged semantics; skipped (not bailed)
        // for an outfit-less donor so the WNAM segment below can still emit.
        if (outfitSource.DefaultOutfit != null && !outfitSource.DefaultOutfit.IsNull)
        {
            IOutfitGetter? donorOutfit = null;
            if (_recordHandler.TryGetRecordFromMods(outfitSource.DefaultOutfit, modSetting.CorrespondingModKeys, folders,
                    RecordHandler.RecordLookupFallBack.Winner, out var outfitRec) && outfitRec is IOutfitGetter scoped)
            {
                donorOutfit = scoped;
            }
            else
            {
                linkCache.TryResolve<IOutfitGetter>(outfitSource.DefaultOutfit.FormKey, out donorOutfit);
            }

            if (donorOutfit?.Items != null)
            {
                var itemKeys = donorOutfit.Items.Where(i => i != null && !i.IsNull).Select(i => i.FormKey).ToList();
                var wigKeys = itemKeys.Where(k => modSetting.DetectedWigArmors.Contains(k))
                    .Select(k => k.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToList();
                var antlerKeys = itemKeys.Where(k => modSetting.DetectedAntlerArmors.Contains(k))
                    .Select(k => k.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToList();
                outfitHasDetectedWig = wigKeys.Count > 0;

                // Depiction segment — the mode-None outfit wig the MUGSHOT draws anyway
                // (NpcMeshResolver's PieceForward.Depiction, alwaysRenderOutfitWigsAndAntlers). No
                // mode tag: the mode is None by construction, and the keys alone re-stale
                // when the mod's outfit gains or loses a wig. Emitted regardless of the
                // outfit toggle because the depiction ignores both toggles. This suffix is
                // mugshot-only (metadata stamp + staleness), so the output-faithful live
                // preview is unaffected by it.
                if (wigMode == WigHandlingMode.None && wigKeys.Count > 0)
                {
                    sb.Append("+wigdepict[" + string.Join(",", wigKeys) + "]");
                }

                // Wig segment — content-based (mode + sorted keys) so adding/removing a wig
                // re-stales too. ForwardToSkin and ConvertToHeadParts depict regardless of
                // the outfit toggle (post-patch the wig is skin / baked FaceGen hair); a
                // ForwardToOutfit wig (incl. the no-WNAM fallback) only with the outfit on.
                if (wigMode != WigHandlingMode.None && wigKeys.Count > 0)
                {
                    var effWig = (wigMode == WigHandlingMode.ForwardToSkin && !hasWnam)
                        ? WigHandlingMode.ForwardToOutfit
                        : wigMode;
                    if (effWig == WigHandlingMode.ForwardToSkin || effWig == WigHandlingMode.ConvertToHeadParts ||
                        includeDefaultOutfitRenderFlag)
                    {
                        sb.Append("+wig[" + effWig + ":" + string.Join(",", wigKeys) + "]");
                    }
                }

                // Depiction segment — the mode-None outfit antler the MUGSHOT draws
                // anyway (PieceForward.Depiction), the antler twin of +wigdepict above
                // and emitted on the same terms: no mode tag (None by construction),
                // keys only, and regardless of the outfit toggle because the depiction
                // ignores both toggles. Mugshot-only, so the output-faithful live
                // preview is unaffected.
                if (antlerMode == AntlerHandlingMode.None && antlerKeys.Count > 0)
                {
                    sb.Append("+antlerdepict[" + string.Join(",", antlerKeys) + "]");
                }

                // Antler segment — the outfit (source 1) antlers the preview reflects.
                // Skin shows always; Remove changes the depiction (hidden) so switching to
                // it re-stales; ForwardToOutfit only with the outfit on. (Sources 2/3 —
                // baked WornArmor/FaceGen antlers — are not depicted in the preview yet, so
                // they're deliberately left out to avoid needless re-renders.)
                if (antlerMode != AntlerHandlingMode.None && antlerKeys.Count > 0)
                {
                    var effAntler = (antlerMode == AntlerHandlingMode.ForwardToSkin && !hasWnam)
                        ? AntlerHandlingMode.ForwardToOutfit
                        : antlerMode;
                    bool depicted = effAntler == AntlerHandlingMode.ForwardToSkin ||
                                    effAntler == AntlerHandlingMode.Remove ||
                                    includeDefaultOutfitRenderFlag;
                    if (depicted)
                    {
                        sb.Append("+antler[" + effAntler + ":" + string.Join(",", antlerKeys) + "]");
                    }
                }
            }
        }

        // Skin-carried (WNAM) wig segment — ConvertToHeadParts only, and only
        // when the conversion changes the depicted image: the preview hides the
        // donor's facegen hair in favor of the skin wig (NpcMeshResolver.
        // ComputeWigHideHeadShapeNames). Mirrors its gates — exactly one
        // effective wig ARMA (Settings.IsWigArmature, race-filtered), no
        // detected outfit wig (that flow's segment is above), and the donor
        // actually HAS Hair-type head parts to hide. Content-based (the
        // effective ARMA key) so a manual is/is-not designation re-stales
        // exactly when it changes the depiction. A bald donor (the High Poly
        // NPC Overhaul pattern) previews identically pre/post conversion and
        // deliberately emits nothing — populating DetectedWigArmatures on
        // upgrade must not mass-re-render tiles whose image cannot change.
        if (wigMode == WigHandlingMode.ConvertToHeadParts && hasWnam && !outfitHasDetectedWig)
        {
            IArmorGetter? wnamGetter = null;
            if (_recordHandler.TryGetRecordFromMods(donor.WornArmor!, modSetting.CorrespondingModKeys, folders,
                    RecordHandler.RecordLookupFallBack.Winner, out var wnamRec) && wnamRec is IArmorGetter scopedWnam)
            {
                wnamGetter = scopedWnam;
            }
            else
            {
                linkCache.TryResolve<IArmorGetter>(donor.WornArmor!.FormKey, out wnamGetter);
            }

            FormKey? raceKey = donor.Race.IsNull ? null : donor.Race.FormKey;

            // ArmorRace (RNAM) is what the engine matches armatures against —
            // mod-scoped first, then the winner, mirroring the WNAM lookup above.
            FormKey? armorRaceKey = null;
            if (!donor.Race.IsNull)
            {
                IRaceGetter? raceGetter = null;
                if (_recordHandler.TryGetRecordFromMods(donor.Race, modSetting.CorrespondingModKeys, folders,
                        RecordHandler.RecordLookupFallBack.Winner, out var raceRec) && raceRec is IRaceGetter scopedRace)
                {
                    raceGetter = scopedRace;
                }
                else
                {
                    linkCache.TryResolve<IRaceGetter>(donor.Race.FormKey, out raceGetter);
                }
                armorRaceKey = Auxilliary.GetArmorRaceKey(raceGetter);
            }
            var effectiveArmaKeys = WigDetector.EffectiveWnamWigArmatures(
                wnamGetter,
                link => _recordHandler.TryGetRecordFromMods(link, modSetting.CorrespondingModKeys, folders,
                            RecordHandler.RecordLookupFallBack.Winner, out var armaRec) &&
                        armaRec is IArmorAddonGetter scopedArma
                    ? scopedArma
                    : linkCache.TryResolve<IArmorAddonGetter>(link.FormKey, out var armaWinner)
                        ? armaWinner
                        : null,
                arma => _settings.IsWigArmature(modSetting, arma.FormKey, arma.EditorID, donor.FormKey),
                arma => ArmaAppliesToRace(arma, raceKey, armorRaceKey))
                .Select(arma => arma.FormKey.ToString())
                .ToList();

            if (effectiveArmaKeys.Count == 1 && DonorHasHairHeadPart(donor, modSetting, folders, linkCache))
            {
                sb.Append("+wnamwig[" + WigHandlingMode.ConvertToHeadParts + ":" + effectiveArmaKeys[0] + "]");
            }
        }

        // FaceGen-baked antler segment (antler source 3). Remove hides these
        // shapes from the rendered head (NpcMeshResolver.HideHeadShapeNames), so
        // switching a mod to Remove — or designating a head part through the 3D
        // preview's "Set Antler Head Parts" — changes the image and must
        // re-stale the tile. The outfit-antler segment above only covers ARMOs
        // in the donor's outfit (source 1); source 2 (a WornArmor antler ARMA)
        // stays out because the preview never draws those slots.
        // Deliberately shares NpcMeshResolver's collector rather than mirroring
        // it, so the stamp cannot drift from what is actually hidden — and for
        // the same reason it must be handed the MOD-SCOPED resolver every other
        // lookup here uses. An appearance mod's plugin is normally NOT in the
        // active load order, so a linkCache-only resolve returns nothing for the
        // head parts that mod defines: the segment stayed silently empty and
        // switching the mod to Remove never re-staled the tile. The render hid
        // the antlers correctly all along (NpcMeshResolver resolves mod-scoped),
        // so only the cached mugshot was wrong, and only deleting it fixed that.
        var hiddenAntlerShapes = CharacterViewerHost.NpcMeshResolver.CollectAntlerHiddenShapeNames(
            donor, modSetting, _settings,
            link => ResolveHeadPart(link, modSetting, folders, linkCache));
        if (hiddenAntlerShapes.Count > 0)
        {
            sb.Append("+fgantler[" + string.Join(",",
                hiddenAntlerShapes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)) + "]");
        }

        return sb.ToString();
    }

    /// <summary>Race applicability mirror of the converter's ARMA filter (null
    /// race / no restrictions = universal).</summary>
    private static bool ArmaAppliesToRace(IArmorAddonGetter arma, FormKey? npcRaceKey, FormKey? armorRaceKey)
    {
        if (npcRaceKey == null) return true;
        if (arma.Race.IsNull && (arma.AdditionalRaces == null || arma.AdditionalRaces.Count == 0)) return true;
        return Auxilliary.ArmaNamesRace(arma, npcRaceKey, armorRaceKey);
    }

    /// <summary>Whether the donor has any Hair-type head part (mod-scoped
    /// resolution first) — the WNAM wig segment only depicts when there is
    /// facegen hair to hide.</summary>
    private bool DonorHasHairHeadPart(INpcGetter donor, ModSetting modSetting, HashSet<string> folders,
        ILinkCache linkCache)
    {
        foreach (var hpLink in donor.HeadParts)
        {
            if (hpLink == null || hpLink.IsNull) continue;
            if (ResolveHeadPart(hpLink, modSetting, folders, linkCache)?.Type == HeadPart.TypeEnum.Hair) return true;
        }
        return false;
    }

    /// <summary>A head part resolved through the mod's own plugins first, with
    /// the load-order winner as fallback — the same order every other record
    /// lookup in this file uses. The mod-scoped leg is not an optimization: an
    /// appearance mod's plugin is usually absent from the active load order, so
    /// the linkCache alone cannot resolve the head parts that mod defines.</summary>
    private IHeadPartGetter? ResolveHeadPart(IFormLinkGetter<IHeadPartGetter> link, ModSetting modSetting,
        HashSet<string> folders, ILinkCache linkCache)
    {
        if (_recordHandler.TryGetRecordFromMods(link, modSetting.CorrespondingModKeys, folders,
                RecordHandler.RecordLookupFallBack.Winner, out var rec) && rec is IHeadPartGetter scoped)
        {
            return scoped;
        }
        return linkCache.TryResolve<IHeadPartGetter>(link.FormKey, out var winner) ? winner : null;
    }

    private static string DescribeOutfit(FormKey outfit, ILinkCache linkCache)
    {
        if (linkCache.TryResolve<IOutfitGetter>(outfit, out var rec) && !string.IsNullOrEmpty(rec.EditorID))
        {
            return $"'{rec.EditorID}'";
        }
        return $"'{outfit}'";
    }

    private static string DescribeNpc(FormKey npc, ILinkCache linkCache)
    {
        if (linkCache.TryResolve<INpcGetter>(npc, out var rec) && !string.IsNullOrEmpty(rec.EditorID))
        {
            return $"'{rec.EditorID}'";
        }
        return $"'{npc}'";
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Config cache
    // ─────────────────────────────────────────────────────────────────────

    private void EnsureCachesCurrent(ILinkCache linkCache)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_cacheOwner, linkCache))
            {
                // Environment was rebuilt — every derived cache is stale.
                _factsCache.Clear();
                _skyPatcherIdCache.Clear();
                _spidFormCache.Clear();
                _editorIdIndex = null;
                _configs = null;
                _lastSignatureCheckTick = long.MinValue;
                _cacheOwner = linkCache;
            }

            long now = Environment.TickCount64;
            if (_configs != null && now - _lastSignatureCheckTick < ConfigSignatureRecheckMs) return;
            _lastSignatureCheckTick = now;

            var (skyFiles, spidFiles) = DiscoverConfigInputs();
            var signature = BuildSignature(skyFiles, spidFiles);
            if (_configs != null && _configs.Signature == signature) return;

            _configs = BuildConfigCache(skyFiles, spidFiles, signature);
        }
    }

    private ConfigCache GetConfigs()
    {
        lock (_gate)
        {
            return _configs ??= new ConfigCache();
        }
    }

    private string SkyPatcherNpcRoot => Path.Combine(_env.DataFolderPath.ToString(), "SKSE", "Plugins", "SkyPatcher", "npc");

    private (List<SkyPatcherOutfitConfigParser.DiscoveredConfig> Sky, List<string> Spid) DiscoverConfigInputs()
    {
        // NPC2's own leftover ini(s) from previous runs live in the dedicated
        // subfolder — excluded from the external scan (the live entry is
        // simulated from current selections at the marker position instead).
        var sky = _skyPatcherParser.DiscoverConfigFiles(
            SkyPatcherNpcRoot,
            IsPluginInstalled,
            rel => rel.StartsWith(Npc2SkyPatcherSubfolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   rel.StartsWith(Npc2SkyPatcherSubfolder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        // NPC2's own _DISTR.ini from a previous run is likewise excluded: it points
        // at a previous output plugin's outfits and would read back as an external
        // contest for every NPC this run is about to republish.
        var spid = _spidParser.DiscoverConfigFiles(_env.DataFolderPath.ToString())
            .Where(f => !ForwardedOutfitDistributor.IsNpc2SpidConfig(Path.GetFileName(f)))
            .ToList();
        return (sky, spid);
    }

    private bool IsPluginInstalled(string pluginFileName)
    {
        if (!ModKey.TryFromNameAndExtension(pluginFileName, out var modKey)) return false;
        return _env.LoadOrderModKeys.Contains(modKey);
    }

    private static string BuildSignature(
        List<SkyPatcherOutfitConfigParser.DiscoveredConfig> skyFiles, List<string> spidFiles)
    {
        var sb = new StringBuilder();
        foreach (var f in skyFiles) AppendFileStamp(sb, f.AbsolutePath);
        sb.Append("||");
        foreach (var f in spidFiles) AppendFileStamp(sb, f);
        return sb.ToString();

        static void AppendFileStamp(StringBuilder sb, string path)
        {
            sb.Append(path).Append(';');
            try
            {
                var fi = new FileInfo(path);
                sb.Append(fi.LastWriteTimeUtc.Ticks).Append(';').Append(fi.Length).Append('|');
            }
            catch
            {
                sb.Append("?|");
            }
        }
    }

    private ConfigCache BuildConfigCache(
        List<SkyPatcherOutfitConfigParser.DiscoveredConfig> skyFiles, List<string> spidFiles, string signature)
    {
        var cache = new ConfigCache { Signature = signature };

        // The marker for NPC2's own ini must sit where SkyPatcher's BFS walk
        // would reach "<npc root>\NPC Plugin Chooser\<Output>.ini": after every
        // root-level file, ordered among the root's subfolders by name. Root
        // files always precede it; a root subfolder sorting before "NPC Plugin
        // Chooser" has its files land before the marker, one sorting after
        // lands after. (Deeper nesting shifts by queue order; those layouts are
        // rare enough that subfolder-name order is the faithful approximation.)
        bool markerPlaced = false;
        foreach (var file in skyFiles)
        {
            if (!markerPlaced && IsOrderedAfterNpc2Folder(file.RelativePath))
            {
                cache.SkyPatcherEntries.Add(MakeNpc2Marker());
                markerPlaced = true;
            }

            List<string> lines;
            try { lines = File.ReadAllLines(file.AbsolutePath).ToList(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"OutfitDisplayResolver: failed reading '{file.AbsolutePath}': {ex.Message}");
                continue;
            }
            foreach (var instruction in _skyPatcherParser.ParseFile(lines, file.RelativePath))
            {
                cache.SkyPatcherEntries.Add(new OrderedSkyPatcherEntry(instruction, false, file.RelativePath));
            }
        }
        if (!markerPlaced) cache.SkyPatcherEntries.Add(MakeNpc2Marker());

        foreach (var file in spidFiles)
        {
            List<string> lines;
            try { lines = File.ReadAllLines(file).ToList(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"OutfitDisplayResolver: failed reading '{file}': {ex.Message}");
                continue;
            }
            cache.SpidEntries.AddRange(_spidParser.ParseFile(lines, Path.GetFileName(file)));
        }

        Debug.WriteLine($"OutfitDisplayResolver: cached {cache.SkyPatcherEntries.Count(e => !e.IsNpc2Marker)} " +
                        $"SkyPatcher outfit instruction(s), {cache.SpidEntries.Count} SPID outfit entr(ies).");
        return cache;
    }

    private OrderedSkyPatcherEntry MakeNpc2Marker()
    {
        string outputName = _env.OutputMod?.ModKey.Name ?? _settings.OutputPluginName;
        return new OrderedSkyPatcherEntry(null, true,
            Path.Combine(Npc2SkyPatcherSubfolder, outputName + ".ini"));
    }

    /// <summary>True when a discovered config sits after the "NPC Plugin
    /// Chooser" subfolder in SkyPatcher's walk: root-level files come first,
    /// then subfolders in name order.</summary>
    private static bool IsOrderedAfterNpc2Folder(string relativePath)
    {
        int sep = relativePath.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        if (sep < 0) return false; // root-level file — before every subfolder
        var topFolder = relativePath.Substring(0, sep);
        return StringComparer.OrdinalIgnoreCase.Compare(topFolder, Npc2SkyPatcherSubfolder) > 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Identifier resolution
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Resolves a SkyPatcher identifier ("Plugin.esp|1B1D3" or bare
    /// EditorID) to the FormKey it denotes, or null. Local IDs are tried with
    /// the full 0xFFFFFF mask and the light-plugin 0xFFF mask.</summary>
    private FormKey? ResolveSkyPatcherIdentifier(string raw)
    {
        return _skyPatcherIdCache.GetOrAdd(raw, key =>
        {
            var id = SkyPatcherOutfitConfigParser.ParseIdentifier(key);
            return id.Kind switch
            {
                RuntimeFormIdentifierKind.ModAndLocalId => ResolveModLocalId(id.ModName!, id.LocalOrRuntimeId),
                RuntimeFormIdentifierKind.EditorId => LookupEditorId(id.EditorId!)?.Key,
                _ => null,
            };
        });
    }

    private FormKey? ResolveModLocalId(string modName, uint localId)
    {
        var linkCache = _env.LinkCache;
        if (linkCache == null) return null;
        if (!ModKey.TryFromNameAndExtension(modName, out var modKey)) return null;

        var full = new FormKey(modKey, localId & 0xFFFFFF);
        if (linkCache.TryResolve(full, out IMajorRecordGetter _)) return full;
        var light = new FormKey(modKey, localId & 0xFFF);
        if (!light.Equals(full) && linkCache.TryResolve(light, out IMajorRecordGetter _)) return light;
        // Not resolvable — return the nominal key so equality filters simply
        // never match, mirroring a missing form.
        return _env.LoadOrderModKeys.Contains(modKey) ? full : null;
    }

    /// <summary>Resolves a SPID form identifier to a typed filter form (or
    /// null when absent from the load order).</summary>
    private ResolvedFilterForm? ResolveSpidFilterForm(RuntimeFormIdentifier id)
    {
        return _spidFormCache.GetOrAdd(id.Raw + "" + id.Kind, _ =>
        {
            var fk = ResolveSpidFormIdentifier(id);
            return fk == null ? null : ClassifyForm(fk.Value, depth: 0);
        });
    }

    private FormKey? ResolveSpidFormIdentifier(RuntimeFormIdentifier id)
    {
        switch (id.Kind)
        {
            case RuntimeFormIdentifierKind.ModAndLocalId:
                return ResolveModLocalId(id.ModName!, id.LocalOrRuntimeId);
            case RuntimeFormIdentifierKind.EditorId:
                return LookupEditorId(id.EditorId!)?.Key;
            case RuntimeFormIdentifierKind.RuntimeFormId:
            {
                // Runtime FormIDs carry a load-order slot in the top byte. In
                // the wild these come from SPID's own sanitizer rewriting
                // zero-padded vanilla IDs, so only the base-game slots (0-4,
                // always the five vanilla masters at the head of any SE load
                // order, before light plugins can perturb full-index counting)
                // are mapped; anything else is load-order-dependent guesswork.
                uint slot = id.LocalOrRuntimeId >> 24;
                if (slot <= 4)
                {
                    var head = _env.LoadOrderModKeys.Take((int)slot + 1).ToList();
                    if (head.Count > (int)slot)
                    {
                        return new FormKey(head[(int)slot], id.LocalOrRuntimeId & 0xFFFFFF);
                    }
                }
                Debug.WriteLine($"OutfitDisplayResolver: runtime FormID '{id.Raw}' targets load-order slot " +
                                $"0x{slot:X2}; only base-game slots are simulated — treated as unresolved.");
                return null;
            }
            default:
                return null;
        }
    }

    private ResolvedFilterForm? ClassifyForm(FormKey fk, int depth)
    {
        var linkCache = _env.LinkCache;
        if (linkCache == null) return null;

        if (linkCache.TryResolve<IKeywordGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Keyword);
        if (linkCache.TryResolve<IFactionGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Faction);
        if (linkCache.TryResolve<IRaceGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Race);
        if (linkCache.TryResolve<IClassGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Class);
        if (linkCache.TryResolve<ICombatStyleGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.CombatStyle);
        if (linkCache.TryResolve<IVoiceTypeGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.VoiceType);
        if (linkCache.TryResolve<INpcGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Npc);
        if (linkCache.TryResolve<IOutfitGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Outfit);
        if (linkCache.TryResolve<ISpellGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Spell);
        if (linkCache.TryResolve<IPerkGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Perk);
        if (linkCache.TryResolve<IArmorGetter>(fk, out _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Armor);
        if (linkCache.TryResolve<IFormListGetter>(fk, out var formList))
        {
            var members = new List<ResolvedFilterForm>();
            if (depth < 5 && formList.Items != null)
            {
                foreach (var item in formList.Items)
                {
                    if (item == null || item.FormKey.IsNull) continue;
                    var member = ClassifyForm(item.FormKey, depth + 1);
                    if (member != null) members.Add(member);
                }
            }
            return new ResolvedFilterForm(fk, SpidFilterFormType.FormList, members);
        }
        if (linkCache.TryResolve(fk, out IMajorRecordGetter _)) return new ResolvedFilterForm(fk, SpidFilterFormType.Unsupported);
        return null;
    }

    /// <summary>Case-insensitive EditorID → FormKey index over the record
    /// types the distributors reference. Built lazily once per environment.</summary>
    private (FormKey Key, SpidFilterFormType Type)? LookupEditorId(string editorId)
    {
        var index = _editorIdIndex;
        if (index == null)
        {
            lock (_gate)
            {
                index = _editorIdIndex ??= BuildEditorIdIndex();
            }
        }
        return index.TryGetValue(editorId, out var hit) ? hit : null;
    }

    private Dictionary<string, (FormKey Key, SpidFilterFormType Type)> BuildEditorIdIndex()
    {
        var sw = Stopwatch.StartNew();
        var index = new Dictionary<string, (FormKey, SpidFilterFormType)>(StringComparer.OrdinalIgnoreCase);
        var loadOrder = _env.LoadOrder;
        if (loadOrder == null) return index;

        void Add(IEnumerable<IMajorRecordGetter> records, SpidFilterFormType type)
        {
            foreach (var rec in records)
            {
                var edid = rec.EditorID;
                if (string.IsNullOrEmpty(edid)) continue;
                index.TryAdd(edid, (rec.FormKey, type));
            }
        }

        try
        {
            var priority = loadOrder.PriorityOrder;
            Add(priority.Outfit().WinningOverrides(), SpidFilterFormType.Outfit);
            Add(priority.Npc().WinningOverrides(), SpidFilterFormType.Npc);
            Add(priority.Keyword().WinningOverrides(), SpidFilterFormType.Keyword);
            Add(priority.Faction().WinningOverrides(), SpidFilterFormType.Faction);
            Add(priority.Race().WinningOverrides(), SpidFilterFormType.Race);
            Add(priority.Class().WinningOverrides(), SpidFilterFormType.Class);
            Add(priority.CombatStyle().WinningOverrides(), SpidFilterFormType.CombatStyle);
            Add(priority.VoiceType().WinningOverrides(), SpidFilterFormType.VoiceType);
            Add(priority.FormList().WinningOverrides(), SpidFilterFormType.FormList);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OutfitDisplayResolver: EditorID index build failed: {ex.Message}");
        }

        Debug.WriteLine($"OutfitDisplayResolver: EditorID index built ({index.Count} entries, {sw.ElapsedMilliseconds}ms).");
        return index;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  NPC facts
    // ─────────────────────────────────────────────────────────────────────

    private NpcRuntimeFacts? GetFacts(FormKey targetNpcFormKey, INpcGetter winnerNpc, ILinkCache linkCache)
    {
        return _factsCache.GetOrAdd(targetNpcFormKey, _ => BuildFacts(winnerNpc, linkCache));
    }

    private NpcRuntimeFacts BuildFacts(INpcGetter npc, ILinkCache linkCache)
    {
        var keywordEdids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keywordKeys = new HashSet<FormKey>();
        void AddKeywords(IReadOnlyList<IFormLinkGetter<IKeywordGetter>>? keywords)
        {
            if (keywords == null) return;
            foreach (var link in keywords)
            {
                if (link == null || link.IsNull) continue;
                keywordKeys.Add(link.FormKey);
                if (linkCache.TryResolve<IKeywordGetter>(link.FormKey, out var kw) && !string.IsNullOrEmpty(kw.EditorID))
                {
                    keywordEdids.Add(kw.EditorID);
                }
            }
        }
        AddKeywords(npc.Keywords);

        IRaceGetter? race = null;
        if (npc.Race != null && !npc.Race.IsNull)
        {
            linkCache.TryResolve<IRaceGetter>(npc.Race.FormKey, out race);
            if (race != null) AddKeywords(race.Keywords);
        }

        bool isChild = race != null &&
                       (race.Flags.HasFlag(Race.Flag.Child) ||
                        (race.EditorID?.Contains("RaceChild", StringComparison.OrdinalIgnoreCase) ?? false));

        var factions = new HashSet<FormKey>();
        if (npc.Factions != null)
        {
            foreach (var rank in npc.Factions)
            {
                if (rank?.Faction != null && !rank.Faction.IsNull) factions.Add(rank.Faction.FormKey);
            }
        }

        var spells = new HashSet<FormKey>();
        if (npc.ActorEffect != null)
        {
            foreach (var link in npc.ActorEffect)
            {
                if (link != null && !link.FormKey.IsNull) spells.Add(link.FormKey);
            }
        }

        var perks = new HashSet<FormKey>();
        if (npc.Perks != null)
        {
            foreach (var perk in npc.Perks)
            {
                if (perk?.Perk != null && !perk.Perk.IsNull) perks.Add(perk.Perk.FormKey);
            }
        }

        var items = new HashSet<FormKey>();
        if (npc.Items != null)
        {
            foreach (var entry in npc.Items)
            {
                var itemLink = entry?.Item?.Item;
                if (itemLink != null && !itemLink.IsNull) items.Add(itemLink.FormKey);
            }
        }

        // Self + template chain (SPID matches NPC filters against template
        // bases too; a leveled template ends the walk).
        var selfKeys = new HashSet<FormKey> { npc.FormKey };
        var selfEdids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(npc.EditorID)) selfEdids.Add(npc.EditorID);
        bool isLeveled = false;
        var templateLink = npc.Template;
        int guard = 0;
        while (templateLink != null && !templateLink.IsNull && guard++ < 10)
        {
            var templateKey = templateLink.FormKey;
            if (!selfKeys.Add(templateKey)) break;
            if (linkCache.TryResolve<INpcGetter>(templateKey, out var templateNpc))
            {
                if (!string.IsNullOrEmpty(templateNpc.EditorID)) selfEdids.Add(templateNpc.EditorID);
                templateLink = templateNpc.Template;
                continue;
            }
            if (linkCache.TryResolve<ILeveledNpcGetter>(templateKey, out var lvln))
            {
                if (!string.IsNullOrEmpty(lvln.EditorID)) selfEdids.Add(lvln.EditorID);
                isLeveled = true;
            }
            break;
        }

        var originPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in selfKeys) originPlugins.Add(key.ModKey.FileName.String);

        var cfg = npc.Configuration;
        bool hasPcLevelMult = cfg.Level is IPcLevelMultGetter;
        ushort level = cfg.Level is INpcLevelGetter npcLevel ? (ushort)Math.Max((int)npcLevel.Level, 0) : (ushort)0;

        return new NpcRuntimeFacts
        {
            NpcFormKey = npc.FormKey,
            EditorId = npc.EditorID,
            Name = npc.Name?.String,
            IsFemale = Auxilliary.IsFemale(npc),
            IsUnique = cfg.Flags.HasFlag(NpcConfiguration.Flag.Unique),
            IsSummonable = cfg.Flags.HasFlag(NpcConfiguration.Flag.Summonable),
            IsEssential = cfg.Flags.HasFlag(NpcConfiguration.Flag.Essential),
            IsProtected = cfg.Flags.HasFlag(NpcConfiguration.Flag.Protected),
            IsChild = isChild,
            IsLeveled = isLeveled,
            // Record-level: StartsDead is a placed-reference flag the base
            // record doesn't carry; false keeps 'D'-trait entries (corpse
            // outfits) from matching, which is right for a living preview.
            StartsDead = false,
            HasPcLevelMult = hasPcLevelMult,
            Level = level,
            RaceFormKey = npc.Race != null && !npc.Race.IsNull ? npc.Race.FormKey : null,
            RaceEditorId = race?.EditorID,
            ClassFormKey = npc.Class != null && !npc.Class.IsNull ? npc.Class.FormKey : null,
            CombatStyleFormKey = npc.CombatStyle != null && !npc.CombatStyle.IsNull ? npc.CombatStyle.FormKey : null,
            VoiceTypeFormKey = npc.Voice != null && !npc.Voice.IsNull ? npc.Voice.FormKey : null,
            SkinFormKey = npc.WornArmor != null && !npc.WornArmor.IsNull ? npc.WornArmor.FormKey : null,
            DefaultOutfitFormKey = npc.DefaultOutfit != null && !npc.DefaultOutfit.IsNull ? npc.DefaultOutfit.FormKey : null,
            KeywordEditorIds = keywordEdids,
            KeywordFormKeys = keywordKeys,
            FactionFormKeys = factions,
            SpellFormKeys = spells,
            PerkFormKeys = perks,
            InventoryItemFormKeys = items,
            SelfAndTemplateFormKeys = selfKeys,
            SelfAndTemplateEditorIds = selfEdids,
            OriginPluginNames = originPlugins,
        };
    }
}
