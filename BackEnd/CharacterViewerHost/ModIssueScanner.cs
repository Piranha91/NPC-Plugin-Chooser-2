using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkyRender;
using SkyRender.Character;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Render-free scanner behind the Mod Issues tab: walks every eligible
/// appearance mod's NPCs and checks that the assets the ENGINE actually reads
/// exist — the FaceGen NIF + tint, the ArmorAddon world models the resolver
/// would draw (skin parts and the full effective outfit, including
/// SkyPatcher/SPID contests and LeveledItem recursion), the textures baked
/// inside those NIFs, and AlternateTextures TXST entries. Record-resolved
/// paths beyond that are deliberately NOT probed: ground-truth geometry and
/// texture paths live inside the NIFs, so anything reachable from the FaceGen
/// NIF supersedes what the records claim.
///
/// <para>Resolution goes through the same <see cref="NpcMeshResolver"/> +
/// <see cref="GameAssetResolver"/> scope chain the mugshot renderer uses
/// (mirroring <see cref="MeshSurveyRunner"/>), so a "missing" verdict here is
/// exactly a wireframe/white/invisible outcome in a render. Results are cached
/// per mod in <see cref="ModIssuesCache"/> and invalidated by
/// <see cref="ModStateSnapshot"/> + loose-asset-tree drift.</para>
/// </summary>
public sealed class ModIssueScanner
{
    private readonly Settings _settings;
    private readonly NpcMeshResolver _resolver;
    private readonly GameAssetResolver _assetResolver;
    private readonly IBsaArchiveProvider _bsa;
    private readonly BsaHandler _bsaHandler;
    private readonly FaceGenConsistencyAnalyzer _faceGenConsistency;
    private readonly ModIssuesCache _cache;

    /// <summary>(folder, mod display name) pairs over every ModSetting folder,
    /// longest folder first, rebuilt per run. Attributes a resolved asset's
    /// disk/BSA path to the installed mod that supplies it — outfit meshes
    /// usually come from an outfit mod, not the appearance mod under scan, and
    /// the report should say so (<see cref="ModIssue.SourceModName"/>).</summary>
    private List<(string Folder, string ModName)> _folderOwners = new();

    /// <summary>Base game + Creation Club asset paths (backslash separators,
    /// OrdinalIgnoreCase), fetched once per run. A data-folder-fallback hit at
    /// one of these paths is not a keep-activated dependency — the base game
    /// always supplies it — mirroring the mugshot badge's vanilla filter.</summary>
    private IReadOnlySet<string> _vanillaAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cross-reference into the parent Mods folder, rebuilt per run so
    /// out-of-scope rows can name the installed mod(s) supplying each asset —
    /// the attribution the mugshot badge's per-render time budget can't afford.</summary>
    private ModsFolderAssetLocator _modsLocator = new(null);

    public ModIssueScanner(
        Settings settings,
        NpcMeshResolver resolver,
        GameAssetResolver assetResolver,
        IBsaArchiveProvider bsa,
        BsaHandler bsaHandler,
        FaceGenConsistencyAnalyzer faceGenConsistency,
        ModIssuesCache cache,
        EnvironmentStateProvider env)
    {
        _settings = settings;
        _resolver = resolver;
        _assetResolver = assetResolver;
        _bsa = bsa;
        _bsaHandler = bsaHandler;
        _faceGenConsistency = faceGenConsistency;
        _cache = cache;
        _env = env;
    }

    private readonly EnvironmentStateProvider _env;

    /// <summary>Winning head parts by EditorID, built once per run. Types the
    /// orphan baked shapes so the analyzer can say which same-slot shape the
    /// .nif carries in place of a missing one. EditorIDs are effectively
    /// globally unique and a same-named part has the same Type regardless of
    /// which override wins, so winning-override resolution is safe here even
    /// though the consistency check itself is Origin-scoped.</summary>
    private Dictionary<string, IHeadPartGetter> _headPartsByEditorId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Progress notification. Units are NPCs, not mods — mods vary from
    /// one NPC to thousands, so a mod-granular bar sits still through the big
    /// ones and then leaps, which reads as a hang. Each mod contributes its NPC
    /// count to <see cref="Total"/>; a cache hit (or the mod-not-installed fast
    /// path) advances <see cref="Completed"/> by the whole mod's weight at once.</summary>
    public sealed record ProgressInfo(int Completed, int Total, string CurrentLabel);

    /// <summary>A mod's contribution to the progress total. Never zero so even a
    /// degenerate entry still ticks the bar when it completes.</summary>
    private static int Weight(ModScanTarget target) =>
        Math.Max(1, target.OnlyNpcs?.Count ?? target.Model.NpcFormKeys?.Count ?? 0);

    /// <summary>One mod queued for scanning. The snapshot is produced by the
    /// caller (the VM owns snapshot generation) so the scanner stays VM-free.
    /// <para><paramref name="OnlyNpcs"/> non-null requests a PARTIAL rescan: only
    /// those NPCs are scanned and their fresh rows are spliced into the mod's
    /// previous result (see <see cref="MergePartialResult"/>) — the post-switch
    /// flow, where re-pinned NPCs must regrade but the mod's files didn't change.
    /// Requires a still-valid cached baseline; without one the scan quietly widens
    /// to the whole mod, since a partial result stored alone would erase every
    /// other NPC's rows.</para></summary>
    public sealed record ModScanTarget(ModSetting Model, ModStateSnapshot? Snapshot,
        IReadOnlySet<FormKey>? OnlyNpcs = null);

    /// <summary>Mods eligible for scanning: installed appearance mods that
    /// provide NPCs. Mugshot-only entries and the synthetic Base Game /
    /// Creation Club entries carry no folder paths, so both fall out of the
    /// folder requirement; IsAutoGenerated is excluded explicitly as a belt.
    /// "Installed" requires at least one folder to exist with content: a
    /// ModSetting whose folders were all deleted keeps its settings entry, but
    /// it must drop out of the Mod Issues tab entirely — mod list, unscanned
    /// list, and cache (pruned by <see cref="RunAsync"/>) — rather than sit
    /// there as a permanent "mod not installed" row (user-requested
    /// 2026-08-16).</summary>
    public static bool IsEligible(ModSetting mod)
        => IsEligibleBySettings(mod) && HasInstalledContent(mod);

    /// <summary>The settings-only half of <see cref="IsEligible"/> — everything
    /// except the <see cref="HasInstalledContent"/> disk probe, which is too
    /// expensive to run per keystroke (the Mod Issues scan-target search box
    /// filters mod names on this predicate).</summary>
    public static bool IsEligibleBySettings(ModSetting mod)
        => !mod.IsAutoGenerated
           && mod.CorrespondingFolderPaths is { Count: > 0 }
           && mod.NpcFormKeys is { Count: > 0 };

    /// <summary>At least one of the mod's folders exists and is non-empty.</summary>
    public static bool HasInstalledContent(ModSetting mod)
        => mod.CorrespondingFolderPaths.Any(f =>
        {
            try { return Directory.Exists(f) && Directory.EnumerateFileSystemEntries(f).Any(); }
            catch { return false; }
        });

    /// <summary>
    /// Scans the given mods, reusing valid cache entries unless
    /// <paramref name="ignoreCache"/>. Returns the per-mod results actually in
    /// effect after the run (cache hits included). Cancellation preserves every
    /// completed mod (already stored + saved); the in-flight mod is discarded so
    /// a previous valid entry survives.
    /// </summary>
    /// <param name="onModCompleted">Invoked (on a background thread) as each
    /// mod's result becomes final — after a fresh scan is stored, or when a
    /// cache entry is reused — so the UI can display results incrementally
    /// instead of waiting for the whole run.</param>
    public async Task<Dictionary<string, ModIssueScanResult>> RunAsync(
        IReadOnlyList<ModScanTarget> mods,
        bool ignoreCache,
        IProgress<ProgressInfo> progress,
        CancellationToken ct,
        Action<string, ModIssueScanResult>? onModCompleted = null)
    {
        var results = new Dictionary<string, ModIssueScanResult>(StringComparer.OrdinalIgnoreCase);
        if (mods.Count == 0) return results;

        _cache.Load();

        // Opening hundreds of archives can take seconds; do it once, off the UI thread.
        await Task.Run(() => _bsa.EnsureAllArchivesOpened(), ct).ConfigureAwait(false);

        // Parse results shared across mods/NPCs within one run: Skyrim NPCs share
        // body/armor NIFs heavily, and the extraction cache hands back a stable
        // disk path per (bsa, rel), so the resolved path is a safe dedupe key.
        var nifParseCache = new ConcurrentDictionary<string, Lazy<IReadOnlyList<NifHandler.NifShapeTextureInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        _folderOwners = _settings.ModSettings
            .SelectMany(ms => ms.CorrespondingFolderPaths
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => (Folder: f.TrimEnd('\\', '/'), ModName: ms.DisplayName)))
            .OrderByDescending(t => t.Folder.Length)
            .ToList();

        // Out-of-scope reporting inputs: the vanilla-path filter (session-cached in
        // BsaHandler) and a fresh Mods-folder cross-reference. Both are shared by
        // every parallel ScanNpc in the run — the vanilla set is read-only and the
        // locator memoizes per path, so the same KS-hair texture referenced by a
        // hundred NPCs is attributed once.
        _modsLocator = new ModsFolderAssetLocator(_settings.ModsFolder);
        _vanillaAssetPaths = await _bsaHandler.GetVanillaAssetPathsAsync().ConfigureAwait(false);

        _headPartsByEditorId = await Task.Run(() =>
        {
            var map = new Dictionary<string, IHeadPartGetter>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var hp in _env.LoadOrder?.PriorityOrder.HeadPart().WinningOverrides()
                                   ?? Enumerable.Empty<IHeadPartGetter>())
                {
                    if (!string.IsNullOrEmpty(hp.EditorID)) map.TryAdd(hp.EditorID!, hp);
                }
            }
            catch { /* typing orphan shapes is best-effort */ }
            return map;
        }, ct).ConfigureAwait(false);

        int totalNpcs = mods.Sum(Weight);
        int completedNpcs = 0;

        bool anyStored = false;
        try
        {
            for (int i = 0; i < mods.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var target = mods[i];
                var mod = target.Model;
                int baseNpcs = completedNpcs;

                progress.Report(new ProgressInfo(baseNpcs, totalNpcs, mod.DisplayName));

                var trees = await Task.Run(
                    () => ModIssuesCache.BuildLooseAssetTrees(mod.CorrespondingFolderPaths), ct)
                    .ConfigureAwait(false);

                // Partial (per-NPC) rescans splice fresh rows into the mod's
                // previous result, so they need a baseline that still matches the
                // on-disk state (the post-switch flow: pins changed, files did
                // not). Without one, widen to a full scan — a partial result
                // stored alone would erase every other NPC's rows.
                var onlyNpcs = target.OnlyNpcs;
                ModIssueScanResult? baseline = null;
                if (onlyNpcs != null)
                {
                    if (_cache.TryGetValid(mod.DisplayName, target.Snapshot, trees, out var b))
                        baseline = b;
                    else
                        onlyNpcs = null;
                }

                if (!ignoreCache && onlyNpcs == null &&
                    _cache.TryGetValid(mod.DisplayName, target.Snapshot, trees, out var cached))
                {
                    results[mod.DisplayName] = cached;
                    completedNpcs = baseNpcs + Weight(target);
                    progress.Report(new ProgressInfo(completedNpcs, totalNpcs,
                        $"{mod.DisplayName} (unchanged — cached)"));
                    onModCompleted?.Invoke(mod.DisplayName, cached);
                    continue;
                }

                var result = await Task.Run(
                    () => ScanModAsync(mod, target.Snapshot, trees, nifParseCache,
                        (j, n) => progress.Report(new ProgressInfo(baseNpcs + j, totalNpcs,
                            $"{mod.DisplayName} ({j}/{n} NPCs)")), ct, onlyNpcs), ct)
                    .ConfigureAwait(false);
                if (onlyNpcs != null && baseline != null)
                {
                    result = MergePartialResult(baseline, result, onlyNpcs);
                }

                results[mod.DisplayName] = result;
                _cache.Store(mod.DisplayName, result);
                anyStored = true;
                completedNpcs = baseNpcs + Weight(target);
                progress.Report(new ProgressInfo(completedNpcs, totalNpcs, mod.DisplayName));
                onModCompleted?.Invoke(mod.DisplayName, result);
                // Throttle: persist after each mod so a crash/cancel keeps progress.
                await _cache.SaveAsync().ConfigureAwait(false);
            }

            // Eligibility-based prune: entries whose mod was removed from the settings OR
            // whose folders no longer exist with content (deleted/uninstalled mods) both
            // drop out, so a deleted mod's row disappears on the next rescan instead of
            // lingering as "mod not installed".
            int pruned = _cache.Prune(_settings.ModSettings.Where(IsEligible).Select(m => m.DisplayName));
            if (anyStored || pruned > 0) await _cache.SaveAsync().ConfigureAwait(false);
            progress.Report(new ProgressInfo(totalNpcs, totalNpcs, "Done."));
        }
        catch (OperationCanceledException)
        {
            if (anyStored) await _cache.SaveAsync().ConfigureAwait(false);
            progress.Report(new ProgressInfo(completedNpcs, totalNpcs, "Cancelled."));
            throw;
        }
        finally
        {
            _assetResolver.SetAdditionalScopes(null);
            _assetResolver.SetAdditionalFolders(null);
        }

        return results;
    }

    /// <summary>Splices a partial (per-NPC) rescan into the mod's previous full
    /// result: rows for the rescanned NPCs are replaced, every other row — other
    /// NPCs' and mod-level — is kept, and the mod-wide counters keep describing
    /// the full mod. Used after the post-scan switch dialog re-pins NPCs: the
    /// pin-dependent checks must regrade for exactly those NPCs, while the mod's
    /// files (and therefore everyone else's rows) are unchanged.</summary>
    internal static ModIssueScanResult MergePartialResult(ModIssueScanResult previous,
        ModIssueScanResult partial, IReadOnlySet<FormKey> rescannedNpcs)
        => new()
        {
            ScanTimeUtc = partial.ScanTimeUtc,
            Snapshot = partial.Snapshot,
            LooseAssetTrees = partial.LooseAssetTrees,
            ScanCompleted = true,
            ScannedNpcCount = previous.ScannedNpcCount,
            // Which NPCs failed isn't recorded, so the previous count carries
            // over unchanged; fresh failures among the rescanned NPCs add on top.
            FailedNpcCount = previous.FailedNpcCount + partial.FailedNpcCount,
            Issues = previous.Issues
                .Where(i => i.NpcFormKey.IsNull || !rescannedNpcs.Contains(i.NpcFormKey))
                .Concat(partial.Issues)
                .OrderBy(i => i.NpcDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Type)
                .ThenBy(i => i.AffectedPath, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

    private async Task<ModIssueScanResult> ScanModAsync(
        ModSetting mod,
        ModStateSnapshot? snapshot,
        List<LooseAssetTreeSnapshot> trees,
        ConcurrentDictionary<string, Lazy<IReadOnlyList<NifHandler.NifShapeTextureInfo>>> nifParseCache,
        Action<int, int> reportNpcProgress,
        CancellationToken ct,
        IReadOnlySet<FormKey>? onlyNpcs = null)
    {
        var result = new ModIssueScanResult
        {
            ScanTimeUtc = DateTime.UtcNow,
            Snapshot = snapshot,
            LooseAssetTrees = trees,
        };

        // Fast path: mod effectively uninstalled — every folder is missing or
        // empty. One mod-level issue instead of one per NPC asset.
        bool anyContent = mod.CorrespondingFolderPaths.Any(f =>
        {
            try { return Directory.Exists(f) && Directory.EnumerateFileSystemEntries(f).Any(); }
            catch { return false; }
        });
        if (!anyContent)
        {
            result.Issues.Add(new ModIssue
            {
                Type = ModIssueType.ModNotInstalled,
                AffectedPath = string.Join("; ", mod.CorrespondingFolderPaths),
                Detail = "None of this mod's folders exist (or they are empty) — the mod appears to be uninstalled.",
            });
            result.ScanCompleted = true;
            return result;
        }

        var npcKeys = mod.NpcFormKeys
            .Where(fk => onlyNpcs == null || onlyNpcs.Contains(fk))
            .OrderBy(fk => fk.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var issues = new ConcurrentBag<ModIssue>();

        // NPCs run in parallel: the resolver stack is exercised concurrently by
        // the batch mugshot generator already, GameAssetResolver's scope state
        // is AsyncLocal (isolated per task flow), and nifly loads are
        // thread-safe. NIF parses dominate the cost and dedupe via the shared cache.
        int completed = 0;
        int failedNpcs = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount - 1));
        var tasks = npcKeys.Select(async npcKey =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bool ok = await Task.Run(() => ScanNpc(mod, npcKey, nifParseCache, issues, ct), ct).ConfigureAwait(false);
                if (!ok) Interlocked.Increment(ref failedNpcs);
            }
            finally
            {
                gate.Release();
                int done = Interlocked.Increment(ref completed);
                if (done % 10 == 0 || done == npcKeys.Count) reportNpcProgress(done, npcKeys.Count);
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        result.Issues.AddRange(issues
            .OrderBy(iss => iss.NpcDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(iss => iss.Type)
            .ThenBy(iss => iss.AffectedPath, StringComparer.OrdinalIgnoreCase));
        result.ScannedNpcCount = npcKeys.Count;
        result.FailedNpcCount = failedNpcs;
        result.ScanCompleted = true;
        return result;
    }

    /// <summary>Returns false when the NPC's scan threw and was swallowed, so the
    /// mod's <see cref="ModIssueScanResult.FailedNpcCount"/> can surface the gap.</summary>
    private bool ScanNpc(
        ModSetting mod,
        FormKey npcKey,
        ConcurrentDictionary<string, Lazy<IReadOnlyList<NifHandler.NifShapeTextureInfo>>> nifParseCache,
        ConcurrentBag<ModIssue> issues,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string npcName = mod.NpcFormKeysToDisplayName.TryGetValue(npcKey, out var n) ? n : npcKey.ToString();
        // Per-NPC dedupe: the same shape/texture miss can surface through several
        // body parts sharing one NIF.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIssue(ModIssueType type, string affectedPath, string? nifPath = null,
            string? shapeName = null, string? referencer = null, string? detail = null,
            bool isOutfit = false, ModIssueSeverity severity = ModIssueSeverity.Issue,
            string? sourceMod = null, string? recordPlugin = null,
            IReadOnlyList<string>? recordPlugins = null, IReadOnlyList<string>? cleanSiblingPlugins = null)
        {
            // recordPlugin joins the dedupe key: per-plugin dark-face verdicts share
            // one AffectedPath (the FaceGen NIF), and the second variant's row must
            // not be swallowed as a duplicate of the first.
            if (!seen.Add($"{type}|{nifPath}|{shapeName}|{affectedPath}|{recordPlugin}")) return;
            issues.Add(new ModIssue
            {
                Type = type,
                NpcFormKey = npcKey,
                NpcDisplayName = npcName,
                AffectedPath = affectedPath,
                NifPath = nifPath,
                ShapeName = shapeName,
                ReferencingRecord = referencer,
                Detail = detail,
                IsOutfitIssue = isOutfit,
                Severity = severity,
                SourceModName = sourceMod,
                RecordPluginName = recordPlugin,
                RecordPlugins = recordPlugins?.ToList(),
                CleanSiblingPlugins = cleanSiblingPlugins?.ToList(),
            });
        }

        // Out-of-scope hits: assets that RESOLVED, but via the engine-order
        // data-folder fallback (Tier 2 loose / Tier 3 broadcast archive) rather
        // than this mod's own folders — the same class the mugshot tiles badge.
        // Keyed by regularized path so one asset referenced through several body
        // parts makes one row (the first referencer supplies the row's context);
        // reported after the NIF walk, filtered against vanilla paths and
        // attributed via the parent Mods folder.
        var outOfScope = new Dictionary<string, (AssetSource Source, string Referencer, bool IsOutfit)>(
            StringComparer.OrdinalIgnoreCase);

        void CollectOutOfScope(string? gamePath, AssetSource? source, string referencer, bool isOutfit)
        {
            if (source is not { ViaDataFolderFallback: true }) return;
            if (string.IsNullOrWhiteSpace(gamePath)) return;
            if (!Auxilliary.TryRegularizePath(gamePath!, out var rel) || string.IsNullOrWhiteSpace(rel)) return;
            outOfScope.TryAdd(rel, (source, referencer, isOutfit));
        }

        try
        {
            // Same scope chain the mugshot renderer would use for this (mod, NPC).
            _assetResolver.SetAdditionalScopes(_resolver.BuildResolutionScopes(mod, npcKey));

            var (npc, resolveHeadPart, resolveRace, npcFromModPlugins) =
                _resolver.ResolveNpcForConsistency(npcKey, mod);
            if (npc == null) return true; // Record-level problems are the Validator's domain.

            // Never-manifested NPCs (Player, chargen presets): nothing they carry can
            // render in game, so the whole scan is moot for them.
            if (IsNeverManifestedNpc(npcKey, npc)) return true;

            var appearanceKey = _resolver.ResolveAppearanceNpcKey(npcKey, mod);
            bool shouldHaveFaceGen = OutputValidator.SubjectShouldHaveOwnFaceGen(npc, resolveRace);
            var (faceGenMeshRel, faceGenTintRel) = Auxilliary.GetFaceGenSubPathStrings(appearanceKey, regularized: true);

            // FaceGen paths belong to the appearance TERMINUS: a Traits-templated NPC has
            // no face of its own and the whole template group misses the SAME file — say
            // so, or "5 NPCs missing one .dds" reads like a display bug.
            string? templateNote = appearanceKey.Equals(npcKey)
                ? null
                : $"This NPC inherits its appearance from template {appearanceKey}; the file is the template's, shared by every NPC inheriting it.";

            if (shouldHaveFaceGen && !_resolver.FaceGenExists(npcKey, mod))
            {
                AddIssue(ModIssueType.MissingFaceGenMesh, faceGenMeshRel,
                    detail: "No FaceGen head mesh anywhere the game would look — this NPC renders with the dark-face/no-head bug."
                            + (templateNote == null ? "" : "\n" + templateNote));
            }

            var paths = _resolver.Resolve(npcKey, mod);
            if (paths == null) return true;

            // Tint-symptom rows (missing tint, dark-face mismatch, unreadable FaceGen)
            // demote to Warning on ghost-keyword NPCs — the ghost effect usually hides
            // them, but a mod that alters the effect could expose them.
            bool ghostMasked = HasGhostKeyword(npc);

            // FaceGen tint DDS. Only meaningful for NPCs that own FaceGen.
            // allowLoadOrderFallback true everywhere below (2.8.0): the scan
            // must agree with the renderer's engine-order mode — an asset an
            // enabled load-order archive provides is NOT missing in game, so
            // flagging it here would be a false positive. A resolved-but-
            // fallback tint is instead an out-of-scope dependency (broadcast-
            // resolved FaceGen counts, per the mugshot badge's ruling).
            if (shouldHaveFaceGen && !string.IsNullOrWhiteSpace(paths.FaceTintPath))
            {
                var tintSource = ResolveSource(paths.FaceTintPath!, allowLoadOrderFallback: true);
                if (tintSource == null)
                {
                    AddIssue(ModIssueType.MissingFaceGenTint, faceGenTintRel,
                        severity: ghostMasked ? ModIssueSeverity.Warning : ModIssueSeverity.Issue,
                        detail: (ghostMasked ? GhostMaskedPrefix : string.Empty)
                                + "FaceGen tint texture is missing — faces typically render grey/mismatched without it."
                                + (templateNote == null ? "" : "\n" + templateNote));
                }
                else
                {
                    CollectOutOfScope(paths.FaceTintPath, tintSource, "FaceGen tint", isOutfit: false);
                }
            }

            // The NPC's own drawn meshes (skin ARMA world models + FaceGen head).
            // No weight-sibling checks here: ResolvedNpcMeshPaths carries no
            // weight-slider flag, and without it a single-weight skin file is
            // indistinguishable from a genuinely missing counterpart.
            // Name WHERE the skin meshes come from (the WornArmor record or the race's
            // skin) so "why is femalebody_1.nif being checked" answers itself.
            string skinVia = npc.WornArmor.IsNull
                ? $" via race skin ({npc.Race.FormKey})"
                : $" via WornArmor {npc.WornArmor.FormKey}";

            var nifJobs = new List<NifJob>();
            // isSkin: the engine textures every shape reached through the worn-skin
            // ArmorAddon chain — body/hands/feet AND the race-specific extras like Khajiit/
            // Argonian tails and worn hair — from the record chain's TextureSet (the ARMA's,
            // or the race skin's). The paths baked in these NIFs are runtime-superseded,
            // so their misses demote to Note below. Only FaceGen bakes are final.
            CheckMesh(mod, paths.BodyMeshPath, "Skin ARMA (Body)" + skinVia, true, false, false, nifJobs, AddIssue, CollectOutOfScope, isSkin: true);
            CheckMesh(mod, paths.HandsMeshPath, "Skin ARMA (Hands)" + skinVia, true, false, false, nifJobs, AddIssue, CollectOutOfScope, isSkin: true);
            CheckMesh(mod, paths.FeetMeshPath, "Skin ARMA (Feet)" + skinVia, true, false, false, nifJobs, AddIssue, CollectOutOfScope, isSkin: true);
            CheckMesh(mod, paths.HairMeshPath, "Worn hair ARMA", true, false, false, nifJobs, AddIssue, CollectOutOfScope, isSkin: true);
            CheckMesh(mod, paths.TailMeshPath, "Tail ARMA", true, false, false, nifJobs, AddIssue, CollectOutOfScope, isSkin: true);

            // FaceGen head: existence already handled above (FaceGenExists knows
            // the renderer's vanilla-loose-skip rule, which plain resolution
            // doesn't), so only queue the texture walk when it resolves. A head
            // that itself resolved via the data-folder fallback reports ITSELF
            // as out-of-scope and suppresses its internal references (the
            // mugshot badge's referencer-scoping rule, mirrored in NifJob.IsOutOfScope).
            var headSource = ResolveSource(paths.HeadMeshPath, allowLoadOrderFallback: true);
            string? headDisk = headSource?.ResolvedDiskPath;
            if (headDisk != null)
            {
                CollectOutOfScope(paths.HeadMeshPath, headSource, "FaceGen head", isOutfit: false);
                nifJobs.Add(new NifJob(headDisk, paths.HeadMeshPath!, "FaceGen head", true, IsFaceGen: true,
                    SourceDescription: headSource!.LoosePath ??
                        (headSource.BsaPath != null ? $"{headSource.BsaPath} :: {headSource.InternalBsaPath}" : headDisk),
                    IsOutOfScope: headSource.ViaDataFolderFallback));
            }

            // Full effective outfit (incl. headgear): what the renderer would
            // actually dress the NPC in, LeveledItems and runtime distributors included.
            IReadOnlyList<MeshOverride> overrides;
            try
            {
                overrides = _resolver.ResolveAttireMeshOverrides(npcKey, mod,
                    includeDefaultOutfit: true, includeHeadgear: true);
            }
            catch
            {
                overrides = Array.Empty<MeshOverride>();
            }

            foreach (var over in overrides)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(over.MeshPath)) continue;
                // Weight siblings only when the ARMA's weight slider is on for
                // this sex — the engine's own signal that a _0/_1 pair exists.
                var overrideSource = CheckMesh(mod, over.MeshPath, over.Key, over.AllowLoadOrderFallback,
                    isOutfit: true, checkWeightSibling: over.HasWeightVariants, nifJobs, AddIssue, CollectOutOfScope,
                    npcWeight: npc.Weight);
                // Referencer scoping for the record-driven textures below: only an
                // in-scope (or missing — moot) mesh lets its retexture entries
                // report as out-of-scope hits.
                bool overrideMeshInScope = overrideSource is not { ViaDataFolderFallback: true };

                // AlternateTextures (MODS): the one record-side texture channel the
                // engine applies over NIF-baked paths.
                if (over.AlternateTextures != null)
                {
                    foreach (var spec in over.AlternateTextures)
                    {
                        foreach (var tex in spec.Textures.Values)
                        {
                            CheckAltTexture(mod, tex, over, spec.ShapeName, AddIssue,
                                CollectOutOfScope, overrideMeshInScope);
                        }
                    }
                }
                else if (over.ShapeTextures != null)
                {
                    foreach (var (shapeName, slots) in over.ShapeTextures)
                    {
                        foreach (var tex in slots.Values)
                        {
                            CheckAltTexture(mod, tex, over, shapeName, AddIssue,
                                CollectOutOfScope, overrideMeshInScope);
                        }
                    }
                }
            }

            // Textures baked inside every rendered NIF — ground truth for what the
            // engine samples. Per-texture verdicts, grouped per shape via ShapeName.
            // Which SLOT a path occupies decides whether the engine reads it at all
            // and how visible a miss is — see ClassifyNifTextureSlot.
            foreach (var job in nifJobs)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<NifHandler.NifShapeTextureInfo> byShape;
                try
                {
                    byShape = nifParseCache.GetOrAdd(job.DiskPath,
                        dp => new Lazy<IReadOnlyList<NifHandler.NifShapeTextureInfo>>(
                            () => NifHandler.GetShapeTextureDetails(dp),
                            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
                }
                catch
                {
                    continue; // Unparseable NIF — the renderer has its own reporting for that.
                }

                foreach (var shape in byShape)
                {
                    // A shape the engine cannot draw cannot render untextured
                    // (wig mods keep alpha-0 hair shapes whose textures were
                    // never meant to be installed).
                    if (!shape.DrawnInGame) continue;

                    foreach (var slotTex in shape.Slots)
                    {
                        var tex = slotTex.Path;
                        if (string.IsNullOrWhiteSpace(tex)) continue;
                        if (!Auxilliary.TryRegularizePath(tex, out var rel) || string.IsNullOrWhiteSpace(rel)) continue;

                        var severity = ClassifyNifTextureSlot(slotTex.Slot, job.IsFaceGen,
                            shape.ShaderType, shape.ShaderFlags1, rel);
                        if (severity == null) continue; // slot the engine never reads
                        var texSource = ResolveSource(rel, job.AllowLoadOrderFallback);
                        if (texSource != null)
                        {
                            // Resolved — not missing. It may still be an out-of-scope
                            // hit, reportable only when the REFERENCING NIF is itself
                            // in scope (referencer scoping; see NifJob.IsOutOfScope).
                            // Worn-skin bakes are excluded outright: the engine textures
                            // those shapes from the record chain's TextureSet, never the
                            // baked path (the same ruling that demotes their misses
                            // below), so a "keep that mod activated" row would assert a
                            // dependency the engine never reads.
                            if (!job.IsOutOfScope && !job.IsSkin)
                            {
                                CollectOutOfScope(rel, texSource,
                                    $"{job.Referencer} ({Path.GetFileName(job.GamePath)})", job.IsOutfit);
                            }
                            continue;
                        }

                        // Worn-skin shapes never sample their baked paths in game: the engine
                        // applies the record chain's TextureSet (the skin ARMA's, or the race
                        // skin's) over them — only FaceGen bakes are final. A missing baked
                        // texture here is invisible (Bijin Brelyna: the body NIF bakes Astrid's
                        // burnt textures and a dead _msn path; in game she wears her WornArmor
                        // ARMA's textures), so it is a Note, never an Issue.
                        if (job.IsSkin) severity = ModIssueSeverity.Note;

                        string shapeDisplay = string.IsNullOrWhiteSpace(shape.ShapeName)
                            ? "(unnamed shape)"
                            : shape.ShapeName;
                        string detail = $"Texture slot {slotTex.Slot} of shape '{shapeDisplay}'.";
                        if (job.IsSkin)
                            detail += " Skin shapes get their textures from the ArmorAddon / race skin " +
                                      "TextureSet at runtime, so the path baked in the NIF is not what the " +
                                      "engine samples — this miss is not visible in game.";

                        // Partition slots are only worth mentioning when they imply
                        // redundancy — another drawn shape covering the same biped
                        // slot(s) (BodySlide variants, replaced brows/hair) means the
                        // broken shape may not even be the one that renders. A bare
                        // slot listing carries no signal on its own.
                        if (shape.PartitionSlots.Count > 0)
                        {
                            var sameSlotShapes = byShape
                                .Where(o => !ReferenceEquals(o, shape) && o.DrawnInGame &&
                                            !string.Equals(o.ShapeName, shape.ShapeName, StringComparison.OrdinalIgnoreCase) &&
                                            o.PartitionSlots.Intersect(shape.PartitionSlots).Any())
                                .Select(o => string.IsNullOrWhiteSpace(o.ShapeName) ? "(unnamed shape)" : o.ShapeName)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            if (sameSlotShapes.Count > 0)
                            {
                                string others = string.Join("', '", sameSlotShapes.Take(4));
                                if (sameSlotShapes.Count > 4) others += $"', … +{sameSlotShapes.Count - 4} more";
                                detail += $" Shape(s) '{others}' occupy the same biped slot(s) " +
                                          $"({string.Join(", ", shape.PartitionSlots)}).";
                            }
                        }

                        if (!string.IsNullOrEmpty(job.SourceDescription))
                            detail += $"\nReferencing NIF resolved from: {job.SourceDescription}";

                        AddIssue(ModIssueType.MissingNifTexture, rel,
                            nifPath: job.GamePath,
                            shapeName: string.IsNullOrWhiteSpace(shape.ShapeName) ? "(unnamed shape)" : shape.ShapeName,
                            referencer: job.Referencer,
                            detail: detail,
                            isOutfit: job.IsOutfit,
                            severity: severity.Value,
                            sourceMod: job.SourceModName);
                    }
                }
            }

            // Out-of-scope hits collected above: everything that resolved via the
            // data-folder fallback with an in-scope referencer. Vanilla paths drop
            // out (the base game always supplies them — also what keeps body
            // meshes at vanilla paths silent); the rest are attributed to the
            // installed mod(s) shipping them by cross-referencing the parent Mods
            // folder. Warning severity: nothing is broken today, but the mod only
            // renders correctly while the supplying mod stays activated — these
            // are keep-activated dependencies, the scan-time twin of the mugshot
            // tiles' data-folder badge.
            foreach (var (rel, hit) in outOfScope)
            {
                if (IsVanillaPath(rel, _vanillaAssetPaths)) continue;
                var (providerColumn, detail) = DescribeOutOfScopeHit(rel, hit.Source, _modsLocator);
                AddIssue(ModIssueType.OutOfScopeAsset, rel,
                    referencer: hit.Referencer,
                    detail: detail,
                    isOutfit: hit.IsOutfit,
                    severity: ModIssueSeverity.Warning,
                    sourceMod: providerColumn);
            }

            // Dark-face class: records vs. the baked FaceGen shapes. Resolution is
            // Origin-scoped (mod plugins → the FormKey's defining plugin), so the
            // verdict describes THIS mod's own data, independent of the load order.
            // Multi-plugin mods grade PER PLUGIN record — the WICO field case
            // (2026-08-17): the two plugins carry different head-part sets for the
            // same NPC and only one matches the shipped bake, so a single verdict
            // would silently follow the user's per-NPC source-plugin pin. Head
            // parts/races still resolve through the shared mod-wide scope: the
            // record is the variable under test, not the part definitions.
            if (headDisk != null)
            {
                try
                {
                    var variants = BuildRecordVariants(npc, appearanceKey, mod, npcFromModPlugins);

                    // Analyze every variant up front so a broken plugin's row can
                    // point at a sibling plugin that grades clean (the repin remedy
                    // the user otherwise discovers by hand).
                    var graded = variants
                        .Select(v => (Variant: v,
                            Analysis: _faceGenConsistency.Analyze(v.Record, resolveHeadPart, resolveRace, headDisk,
                                edid => _headPartsByEditorId.TryGetValue(edid, out var hp) ? hp : null)))
                        .ToList();

                    // File present but unreadable: the engine can't parse it either and
                    // falls back to runtime face regeneration — the dark-face outcome —
                    // while the forward checks below see an empty bake and would stay
                    // SILENT (DFIR-review gap: its "broken facegen NIF" class). File-level,
                    // so emitted once, not per variant.
                    if (graded.Count > 0 && !graded[0].Analysis.NifParsed)
                    {
                        string? nifError = graded[0].Analysis.NifError;
                        AddIssue(ModIssueType.DarkFaceMismatch, faceGenMeshRel,
                            nifPath: paths.HeadMeshPath,
                            severity: ghostMasked ? ModIssueSeverity.Warning : ModIssueSeverity.Issue,
                            detail: (ghostMasked ? GhostMaskedPrefix : string.Empty) +
                                    "The FaceGen head mesh exists but could not be parsed" +
                                    (string.IsNullOrEmpty(nifError) ? "" : $" ({nifError})") +
                                    " — a broken/corrupt .nif. If the game engine cannot read it either, " +
                                    "this NPC dark-faces (runtime face regeneration).");
                    }

                    // Individual clean carrier filenames, in the mod's plugin order —
                    // structured (never a joined label) so the green remedy line and
                    // the post-scan switch dialog can consume them without parsing.
                    // Resource-only carriers are excluded: RefreshNpcLists never
                    // offers them as an NPC's source plugin, so "switch to it" would
                    // be advice the Mods tab cannot follow.
                    var resourceOnlyPlugins = ResourceOnlyPluginFileNames(mod);
                    var cleanSiblingPlugins = graded
                        .Where(g => g.Analysis.MissingBakedShapes.Count == 0 &&
                                    g.Analysis.NullHeadPartLinks == 0 &&
                                    g.Analysis.UnresolvedHeadParts.Count == 0)
                        .SelectMany(g => g.Variant.Plugins ?? Enumerable.Empty<string>())
                        .Where(p => !resourceOnlyPlugins.Contains(p))
                        .ToList();

                    foreach (var (variant, analysis) in graded)
                    {
                        // Head parts from a plugin that resolves nowhere = a missing hard
                        // dependency. One rollup row per absent plugin instead of folding
                        // it into the dark-face wall of text — one absent plugin used to
                        // produce thousands of DarkFaceMismatch rows across a mod.
                        foreach (var group in analysis.UnresolvedHeadParts.GroupBy(fk => fk.ModKey))
                        {
                            AddIssue(ModIssueType.MissingHeadPartPlugin, group.Key.FileName.String,
                                nifPath: paths.HeadMeshPath,
                                recordPlugin: variant.Label,
                                recordPlugins: variant.Plugins,
                                detail: "This NPC's record uses head part(s) " +
                                        string.Join(", ", group.Select(fk => fk.ToString())) +
                                        " from a plugin that is not in this mod's folders and could not be resolved. " +
                                        "The mod likely requires it (check the mod page's requirements); without it these NPCs dark-face in game.");
                        }

                        if (analysis.MissingBakedShapes.Count > 0 || analysis.NullHeadPartLinks > 0)
                        {
                            // Traits-inert file (SOGS field specimen): the appearance hop used for
                            // RENDERING is Winner-scoped and can follow a load-order override that
                            // strips the vanilla Traits template — but in the mod's OWN context the
                            // template stands, the raw engine renders the terminus's face, and the
                            // graded file at this NPC's own path is never loaded. Note, not Issue:
                            // whether the file ever matters is decided by this app's template
                            // handling at patch time, and Validate Output checks the real output.
                            // Evaluated per VARIANT: one plugin can keep the template while
                            // another strips it.
                            bool traitsInert = appearanceKey.Equals(npcKey) && variant.KeepsTraitsTemplate;

                            // Carried only by resource-only plugins (the Auri No
                            // Antlers case): such a record can never be an NPC's
                            // source-plugin selection, so this mod cannot forward the
                            // mismatch into a patch — the NPC's actual source is one
                            // of the remaining plugins. Note, not Issue: the row
                            // documents the mod's own data, not a selection the user
                            // could be bitten by.
                            bool resourceOnlyCarriers = variant.Plugins is { Count: > 0 } &&
                                variant.Plugins.All(resourceOnlyPlugins.Contains);

                            // The repin remedy travels as STRUCTURED fields (below) —
                            // the UI composes and colors the sentence under the row's
                            // headline, and the post-scan switch dialog reads them.
                            AddIssue(ModIssueType.DarkFaceMismatch, faceGenMeshRel,
                                nifPath: paths.HeadMeshPath,
                                recordPlugin: variant.Label,
                                recordPlugins: variant.Plugins,
                                cleanSiblingPlugins: variant.Label != null && cleanSiblingPlugins.Count > 0
                                    ? cleanSiblingPlugins
                                    : null,
                                // Engine-inert demotions (traits-inert, resource-only
                                // carriers) are Notes; the ghost demotion is only a
                                // masking effect, so it grades Warning instead.
                                severity: traitsInert || resourceOnlyCarriers
                                    ? ModIssueSeverity.Note
                                    : ghostMasked
                                        ? ModIssueSeverity.Warning
                                        : ModIssueSeverity.Issue,
                                detail: (resourceOnlyCarriers
                                            ? "This record only comes from plugin(s) marked resource-only in this mod " +
                                              "entry, which are never offered as an NPC's source plugin — this mod " +
                                              "cannot forward the mismatched record into a patch, so the problem below " +
                                              "is reported as a note about the mod's own data.\n"
                                            : string.Empty) +
                                        (traitsInert
                                            ? "In the mod's own context this NPC keeps the Traits template flag, so the " +
                                              "unpatched engine renders its template's face and never loads this file — " +
                                              "the mismatch below cannot show in game unless a patch or another plugin " +
                                              "removes the template.\n"
                                            : string.Empty) +
                                        (ghostMasked ? GhostMaskedPrefix : string.Empty) +
                                        analysis.BuildReason(
                                            scope: FaceGenConsistencyAnalyzer.ReasonScope.SelectedMod,
                                            subjectSuppliesRecord: variant.SubjectSuppliesRecord));
                        }
                    }
                }
                catch
                {
                    // Best-effort, mirroring the mugshot generator: an analyzer
                    // failure must not fail the scan.
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // One NPC's failure must not sink the mod's scan; the caller counts it.
            return false;
        }
        finally
        {
            _assetResolver.SetAdditionalScopes(null);
        }
    }

    /// <summary><paramref name="IsOutOfScope"/>: the NIF itself resolved via the
    /// data-folder fallback. Its internal texture references then never report as
    /// out-of-scope (the mugshot badge's referencer-scoping rule): the file that
    /// carries the reference isn't the scanned mod's to begin with — without this,
    /// the installed body replacer's internal texture names would flag on every
    /// NPC of every mod. Missing-texture checks are unaffected.</summary>
    private sealed record NifJob(string DiskPath, string GamePath, string Referencer,
        bool AllowLoadOrderFallback, bool IsFaceGen = false, bool IsOutfit = false,
        string? SourceModName = null, string? SourceDescription = null, bool IsSkin = false,
        bool IsOutOfScope = false);

    private delegate void AddIssueDelegate(ModIssueType type, string affectedPath, string? nifPath = null,
        string? shapeName = null, string? referencer = null, string? detail = null, bool isOutfit = false,
        ModIssueSeverity severity = ModIssueSeverity.Issue, string? sourceMod = null, string? recordPlugin = null,
        IReadOnlyList<string>? recordPlugins = null, IReadOnlyList<string>? cleanSiblingPlugins = null);

    /// <summary>Collects a resolved asset as an out-of-scope (data-folder-fallback)
    /// hit; a no-op for null/in-scope sources, so call sites pass every resolved
    /// source unconditionally and the flag decides.</summary>
    private delegate void CollectOutOfScopeDelegate(string? gamePath, AssetSource? source,
        string referencer, bool isOutfit);

    /// <summary>One dark-face grading target: a record and the plugin(s) within the
    /// scanned mod that carry it. Label is null for the single-variant case (a
    /// single-plugin mod, or no mod plugin carries the record and the pinned/origin
    /// resolution stands in) — those rows stay untagged, the pre-v9 presentation.</summary>
    private sealed record RecordVariant(INpcGetter Record, string? Label,
        bool SubjectSuppliesRecord, bool KeepsTraitsTemplate,
        IReadOnlyList<string>? Plugins = null);

    private static readonly FormKey PlayerNpcFormKey =
        Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Npc.Player.FormKey;

    private static readonly FormKey ActorTypeGhostKeyword =
        Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Keyword.ActorTypeGhost.FormKey;

    /// <summary>NPCs that never manifest as placed actors — the Player record and
    /// character-creation presets ('Is CharGen Face Preset'): the race menu builds
    /// preset faces from the record's MORPH data, not FaceGen files, so nothing a
    /// preset carries can show in game (same rationale as OutputValidator's preset
    /// downgrade; DFIR-derived). The scanner skips them entirely — WICO ships
    /// FaceGen for every vanilla preset and produced dark-face rows the game cannot
    /// display. A preset serving as another NPC's Traits terminus still surfaces
    /// through that NPC's appearance hop, which grades the terminus's FaceGen path
    /// under the inheritor's row.</summary>
    internal static bool IsNeverManifestedNpc(FormKey npcKey, INpcGetter npc)
        => npcKey.Equals(PlayerNpcFormKey) ||
           npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.IsCharGenFacePreset);

    /// <summary>ActorTypeGhost on the graded record itself (mod scope; race-carried
    /// keywords deliberately not consulted — DFIR reads the NPC record too). The
    /// ghost visual effect usually hides face-tint problems (user-observed on sparse
    /// in-game tests 2026-08-18), but that observation may not generalize — mods can
    /// alter the ghost effect — so ghost rows demote to Note instead of vanishing
    /// (DFIR skips ghosts outright; we deliberately do not).</summary>
    internal static bool HasGhostKeyword(INpcGetter npc)
        => npc.Keywords?.Any(k => k.FormKey.Equals(ActorTypeGhostKeyword)) == true;

    /// <summary>Filenames of the mod entry's resource-only plugins (case-insensitive).
    /// A resource-only plugin is excluded from NPC sourcing entirely
    /// (VM_ModSetting.RefreshNpcLists skips it), so a record variant carried only by
    /// such plugins can never be an NPC's source-plugin selection: its dark-face rows
    /// demote to Note, and resource-only carriers never appear as switch targets in
    /// CleanSiblingPlugins (the Auri No Antlers case, 2026-08-18).</summary>
    internal static HashSet<string> ResourceOnlyPluginFileNames(ModSetting mod) =>
        (mod.ResourceOnlyModKeys ?? new HashSet<ModKey>())
        .Select(k => k.FileName.String)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Prefix for tint-symptom rows (dark-face class, missing tint,
    /// unreadable FaceGen) demoted because of <see cref="HasGhostKeyword"/>.</summary>
    private const string GhostMaskedPrefix =
        "This NPC carries the ActorTypeGhost keyword: the ghost visual effect usually hides " +
        "face-tint problems, so this is reported as a warning. A mod that changes the ghost " +
        "effect could still expose it.\n";

    private static bool KeepsTraitsTemplate(INpcGetter npc) =>
        npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) &&
        !npc.Template.IsNull;

    /// <summary>The dark-face grading targets for one NPC. Multi-plugin mods yield
    /// one variant per DISTINCT record signature among the plugins carrying the
    /// record (identical records collapse, labeled with every carrier); otherwise
    /// the already-resolved pinned/origin record is the single unlabeled variant.</summary>
    private List<RecordVariant> BuildRecordVariants(INpcGetter pinnedNpc, FormKey appearanceKey,
        ModSetting mod, bool pinnedFromModPlugins)
    {
        if (mod.CorrespondingModKeys.Count > 1)
        {
            var perPlugin = _resolver.ResolveNpcRecordPerPlugin(appearanceKey, mod);
            if (perPlugin.Count > 0)
            {
                return GroupPluginRecordsBySignature(perPlugin)
                    .Select(g => new RecordVariant(g.Record, g.Label,
                        SubjectSuppliesRecord: true, KeepsTraitsTemplate(g.Record), g.Plugins))
                    .ToList();
            }
        }

        return new List<RecordVariant>
        {
            new(pinnedNpc, Label: null, pinnedFromModPlugins, KeepsTraitsTemplate(pinnedNpc)),
        };
    }

    /// <summary>Collapses per-plugin records to one entry per distinct dark-face
    /// signature, preserving plugin order; the label joins every carrier's filename
    /// ("A.esp, B.esp") so identical records produce one row instead of N. Plugins
    /// carries the INDIVIDUAL filenames — consumers must never split the label
    /// (filenames can legally contain commas).</summary>
    internal static List<(INpcGetter Record, string Label, IReadOnlyList<string> Plugins)>
        GroupPluginRecordsBySignature(IReadOnlyList<(ModKey Plugin, INpcGetter Record)> perPlugin)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, (INpcGetter Record, List<string> Plugins)>(StringComparer.Ordinal);
        foreach (var (plugin, record) in perPlugin)
        {
            var sig = NpcDarkFaceSignature(record);
            if (groups.TryGetValue(sig, out var existing))
            {
                existing.Plugins.Add(plugin.FileName.String);
            }
            else
            {
                groups[sig] = (record, new List<string> { plugin.FileName.String });
                order.Add(sig);
            }
        }
        return order
            .Select(sig => (groups[sig].Record, string.Join(", ", groups[sig].Plugins),
                (IReadOnlyList<string>)groups[sig].Plugins))
            .ToList();
    }

    /// <summary>Equality key for per-plugin dark-face grading: two plugin records
    /// with the same signature produce the same verdict, so they collapse to one
    /// labeled row. Covers exactly the analyzer's record-side inputs: head parts IN
    /// ORDER (the singular-slot rule is first-listed-wins), race, sex, and the
    /// Traits-template state (the inert demotion).</summary>
    internal static string NpcDarkFaceSignature(INpcGetter npc)
    {
        var parts = npc.HeadParts != null
            ? string.Join("|", npc.HeadParts.Select(hp => hp.FormKey.ToString()))
            : string.Empty;
        return parts +
               "§" + npc.Race.FormKey +
               "§" + (Auxilliary.IsFemale(npc) ? 'F' : 'M') +
               "§" + (KeepsTraitsTemplate(npc) ? 'T' : '-');
    }

    /// <summary>Returns the resolved <see cref="AssetSource"/> (null when the mesh
    /// is missing) so the caller can gate record-driven follow-ups — alt-texture
    /// out-of-scope reporting is suppressed when the referencing mesh itself came
    /// from the data folder.</summary>
    private AssetSource? CheckMesh(ModSetting scannedMod, string? gamePath, string referencer,
        bool allowLoadOrderFallback, bool isOutfit, bool checkWeightSibling,
        List<NifJob> nifJobs, AddIssueDelegate addIssue, CollectOutOfScopeDelegate collectOutOfScope,
        bool isSkin = false, float? npcWeight = null)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return null;

        var source = ResolveSource(gamePath, allowLoadOrderFallback);
        string? disk = source?.ResolvedDiskPath;
        if (disk == null)
        {
            // The mesh itself is missing, so there is no disk path to attribute —
            // fall back to the referencing record's plugin for "whose mod is this".
            addIssue(ModIssueType.MissingArmaMesh, gamePath, referencer: referencer,
                detail: "The mesh could not be found in the mod, vanilla archives, or the Data folder — it will not render.",
                isOutfit: isOutfit,
                sourceMod: isOutfit ? AttributeProviderByReferencer(referencer, scannedMod) : null);
            return null;
        }

        // A mesh the mod references but the data folder supplies is a
        // keep-activated dependency in its own right (it reports itself; its
        // internal references are then suppressed via NifJob.IsOutOfScope).
        collectOutOfScope(gamePath, source, referencer, isOutfit);

        // Which installed mod supplies this NIF: outfit meshes usually come from
        // an outfit/armor mod rather than the appearance mod under scan, and the
        // report should point users at the right author.
        string? sourceMod = isOutfit ? AttributeProviderMod(source, scannedMod) : null;
        string sourceDescription = source!.LoosePath
            ?? (source.BsaPath != null ? $"{source.BsaPath} :: {source.InternalBsaPath}" : disk);
        nifJobs.Add(new NifJob(disk, gamePath!, referencer, allowLoadOrderFallback,
            IsOutfit: isOutfit, SourceModName: sourceMod, SourceDescription: sourceDescription,
            IsSkin: isSkin, IsOutOfScope: source.ViaDataFolderFallback));

        // _0/_1 weight sibling: only when the source ARMA's weight slider is
        // enabled — then the engine morphs between both files and a missing
        // counterpart pops or crashes at non-matching weights. With the slider
        // off, a lone weight file is the normal shipping shape.
        if (!checkWeightSibling) return source;
        string? sibling = DeriveWeightSibling(gamePath!);
        if (sibling != null)
        {
            var siblingSource = ResolveSource(sibling, allowLoadOrderFallback);
            if (siblingSource?.ResolvedDiskPath == null)
            {
                // The engine interpolates between the _0 and _1 models at the
                // NPC's fixed weight, so grade per NPC: an absent file the
                // weight interpolation reaches into is an Issue; an NPC sitting
                // exactly at the extreme the present file covers is a Warning
                // (the pair is still incomplete — a weight edit or another
                // wearer exposes it). Unknown weight fails conservative (Issue).
                bool missingHighWeight = sibling.EndsWith("_1.nif", StringComparison.OrdinalIgnoreCase);
                bool neededAtThisWeight = npcWeight == null ||
                    (missingHighWeight ? npcWeight.Value > 0f : npcWeight.Value < 100f);
                string siblingDetail = $"The weight counterpart of {Path.GetFileName(gamePath)} is missing.";
                if (npcWeight != null)
                {
                    siblingDetail += neededAtThisWeight
                        ? $" This NPC's weight ({npcWeight.Value:0.#}) reaches into the missing file — the shape renders wrong or not at all."
                        : $" This NPC's weight ({npcWeight.Value:0.#}) sits exactly at the extreme the present file covers, so nothing breaks for this NPC today.";
                }
                addIssue(ModIssueType.MissingWeightSibling, sibling, referencer: referencer,
                    detail: siblingDetail,
                    isOutfit: isOutfit, sourceMod: sourceMod,
                    severity: neededAtThisWeight ? ModIssueSeverity.Issue : ModIssueSeverity.Warning);
            }
            else if (!source.ViaDataFolderFallback)
            {
                // The sibling of an in-scope mesh living out of scope is the same
                // keep-activated dependency as any other hit; an out-of-scope
                // mesh's sibling is suppressed with it (referencer scoping).
                collectOutOfScope(sibling, siblingSource, referencer, isOutfit);
            }
        }
        return source;
    }

    private void CheckAltTexture(ModSetting scannedMod, string texPath, MeshOverride over, string shapeName,
        AddIssueDelegate addIssue, CollectOutOfScopeDelegate collectOutOfScope, bool referencingMeshInScope)
    {
        if (string.IsNullOrWhiteSpace(texPath)) return;
        if (!Auxilliary.TryRegularizePath(texPath, out var rel) || string.IsNullOrWhiteSpace(rel)) return;
        var source = ResolveSource(rel, over.AllowLoadOrderFallback);
        if (source != null)
        {
            // Resolved, possibly from the data folder. Record-driven TXST textures
            // applied to an out-of-scope mesh are suppressed with it — the mugshot
            // badge's documented trade-off, mirrored so scan and badge agree.
            if (referencingMeshInScope)
                collectOutOfScope(rel, source, over.Key, isOutfit: true);
            return;
        }

        addIssue(ModIssueType.MissingAltTexture, rel, over.MeshPath,
            string.IsNullOrWhiteSpace(shapeName) ? "(unnamed shape)" : shapeName, over.Key,
            "An AlternateTextures entry points at this texture; the shape it retextures will render untextured.",
            isOutfit: true, sourceMod: AttributeProviderByReferencer(over.Key, scannedMod));
    }

    /// <summary>
    /// Which slots the engine actually samples, and how visible a missing file
    /// there is. Null = the engine never reads this slot, so a missing file is
    /// not reportable at any tier:
    /// <list type="bullet">
    /// <item>Anything under facegendata\facetint, and slot 6 of a FaceGen head:
    /// the engine loads the face tint ONLY from the canonical FormID-derived
    /// path (proven in-game 2026-08-15, docs/FaceTintEngineTest-2026-08.md) —
    /// the baked slot-6 string is never consulted, which is also why authors get
    /// away with junk strings there. The canonical tint has its own check
    /// (MissingFaceGenTint).</item>
    /// <item>Slots 4/5 (environment cubemap/mask) when neither the shader type
    /// nor the SLSF1 flags enable environment mapping.</item>
    /// </list>
    /// Slots 0/1 (diffuse/normal) are Issues — a miss renders visibly broken
    /// (white/flat mesh). Every other sampled slot is a Note: real but subtle in
    /// game — vanilla itself ships meshes referencing secondary maps it does not
    /// include (mouthhuman_s/_sk, teeth_e: 58% of all texture rows measured on a
    /// live setup). Unknown shader info fails conservative (report as Note).
    /// </summary>
    internal static ModIssueSeverity? ClassifyNifTextureSlot(int slot, bool isFaceGenHead,
        uint shaderType, uint shaderFlags1, string regularizedPath)
    {
        const uint Slsf1EnvironmentMapping = 0x00000080;
        const uint Slsf1EyeEnvironmentMapping = 0x00020000;
        const uint ShaderTypeEnvMap = 1;
        const uint ShaderTypeEyeEnvMap = 16;

        if (regularizedPath.Contains(@"facegendata\facetint", StringComparison.OrdinalIgnoreCase))
            return null;

        switch (slot)
        {
            case 0:
            case 1:
                return ModIssueSeverity.Issue;

            case 6:
                return isFaceGenHead ? null : ModIssueSeverity.Note;

            case 4:
            case 5:
                bool envActive =
                    shaderType == ShaderTypeEnvMap ||
                    shaderType == ShaderTypeEyeEnvMap ||
                    shaderType == NifHandler.UnknownShaderType ||
                    (shaderFlags1 & (Slsf1EnvironmentMapping | Slsf1EyeEnvironmentMapping)) != 0;
                return envActive ? ModIssueSeverity.Note : null;

            default: // 2 glow/subsurface, 3 detail/palette, 7 specular/backlight, 8 spare
                return ModIssueSeverity.Note;
        }
    }

    /// <summary>Vanilla filter for out-of-scope reporting: a data-folder-fallback
    /// hit whose path the base game / Creation Club ships is not a keep-activated
    /// dependency. The vanilla index stores backslash-separated paths
    /// (OrdinalIgnoreCase), so normalize before membership testing — same rule as
    /// the mugshot generator's stamp filter.</summary>
    internal static bool IsVanillaPath(string regularizedRel, IReadOnlySet<string> vanillaPaths)
        => vanillaPaths.Contains(regularizedRel.Replace('/', '\\').TrimStart('\\'));

    /// <summary>Composes an out-of-scope row's "Provided by" column and Detail
    /// text: where the asset actually came from (loose data-folder file or a
    /// named archive) and which mod folder(s) in the parent Mods folder ship it.
    /// Layout and wording are the user's verbatim template (2026-08-21): the
    /// supplying mods listed one per line (capped, "(+N more)"), then a plain
    /// consequence + remedy sentence — not inline clause-prose. Internal for
    /// tests.</summary>
    internal static (string ProviderColumn, string Detail) DescribeOutOfScopeHit(
        string regularizedRel, AssetSource source, ModsFolderAssetLocator locator)
    {
        bool fromArchive = !string.IsNullOrEmpty(source.BsaPath);
        string archiveName = fromArchive ? Path.GetFileName(source.BsaPath!) : string.Empty;
        var providers = fromArchive
            ? locator.FindArchiveProviders(archiveName)
            : locator.FindLooseProviders(regularizedRel);

        var sb = new System.Text.StringBuilder();
        sb.Append("This asset is not in this mod's Corresponding Mod Folders. The game finds it ")
          .Append(fromArchive
              ? $"inside archive '{archiveName}' in your data folder"
              : "from a loose file in your data folder");

        if (providers.Count > 0)
        {
            const int maxListed = 4;
            sb.Append(providers.Count == 1
                ? ", which comes from the following mod:"
                : ", which comes from one of the following:");
            foreach (var p in providers.Take(maxListed)) sb.Append('\n').Append(p);
            if (providers.Count > maxListed)
                sb.Append("\n(+").Append(providers.Count - maxListed).Append(" more)");
            sb.Append('\n').Append(providers.Count == 1
                ? "The asset will fail to load in-game if you deactivate this mod. To forward the " +
                  "asset to the NPC2 output folder, allowing you to deactivate it, add the " +
                  "supplying mod's folder to this mod entry's Corresponding Mod Folders."
                : "The asset will fail to load in-game if you deactivate all of these mods. To " +
                  "forward the asset to the NPC2 output folder, allowing you to deactivate these " +
                  "mods, add (one of) the supplying mod's folder(s) to this mod entry's " +
                  "Corresponding Mod Folders.");
        }
        else if (locator.IsAvailable)
        {
            sb.Append(fromArchive
                ? ". No folder in your Mods folder ships that archive — it may be installed " +
                  "directly in the game's Data folder."
                : ". No folder in your Mods folder ships this file — it may be installed " +
                  "directly in the game's Data folder.");
            sb.Append(" The asset will fail to load in-game if its source is removed.");
        }
        else
        {
            // No Mods folder configured/available — nothing to cross-reference.
            sb.Append(". The asset will fail to load in-game if the mod supplying it is " +
                      "deactivated. To forward the asset to the NPC2 output folder, allowing you " +
                      "to deactivate that mod, add its folder to this mod entry's Corresponding " +
                      "Mod Folders.");
        }

        string providerColumn = providers.Count == 0
            ? string.Empty
            : string.Join(", ", providers.Take(3)) + (providers.Count > 3 ? ", …" : string.Empty);
        return (providerColumn, sb.ToString());
    }

    /// <summary>Resolves a game path (or passes through an already-rooted disk
    /// path) via the renderer's scope chain, keeping the full
    /// <see cref="AssetSource"/> so callers can attribute the winning provider
    /// (loose folder or BSA) to an installed mod and read the
    /// data-folder-fallback flag. Null = missing/unresolvable.</summary>
    private AssetSource? ResolveSource(string? path, bool allowLoadOrderFallback)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Path.IsPathRooted(path))
            {
                return File.Exists(path)
                    ? new AssetSource(AssetOriginKind.Loose, path, path, path, null, null)
                    : null;
            }
            using (_assetResolver.PushLoadOrderFallback(allowLoadOrderFallback))
            {
                var source = _assetResolver.ResolveAssetSource(path!);
                return source.ResolvedDiskPath == null ? null : source;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Maps the winning provider's disk location (loose file or BSA —
    /// both live inside some mod's folder) to the owning ModSetting via
    /// longest-prefix match. Null when the provider is the scanned mod itself
    /// (nothing to point at), the game Data folder, or unattributable.</summary>
    private string? AttributeProviderMod(AssetSource? source, ModSetting scannedMod)
    {
        string? path = source?.LoosePath ?? source?.BsaPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var (folder, name) in _folderOwners)
        {
            if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                (path.Length == folder.Length || path[folder.Length] == '\\' || path[folder.Length] == '/'))
            {
                return name.Equals(scannedMod.DisplayName, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : name;
            }
        }
        return null;
    }

    /// <summary>Fallback attribution when there is no resolved file to map: the
    /// referencer string carries the source record's plugin
    /// ("Outfit:&lt;FormID&gt;:&lt;plugin&gt;:&lt;slots&gt;" per the attire walk's Key
    /// convention); the ModSetting owning that plugin is the mod to look at.</summary>
    private string? AttributeProviderByReferencer(string? referencer, ModSetting scannedMod)
    {
        if (string.IsNullOrWhiteSpace(referencer)) return null;
        var parts = referencer.Split(':');
        if (parts.Length < 3) return null;
        string pluginToken = parts[2].Trim();
        if (pluginToken.Length == 0) return null;

        var owner = _settings.ModSettings.FirstOrDefault(ms =>
            ms.CorrespondingModKeys.Any(k =>
                k.FileName.String.Equals(pluginToken, StringComparison.OrdinalIgnoreCase)));
        if (owner == null) return null;
        return owner.DisplayName.Equals(scannedMod.DisplayName, StringComparison.OrdinalIgnoreCase)
            ? null
            : owner.DisplayName;
    }

    /// <summary>Returns the _0/_1 weight-sibling path of a world-model NIF, or
    /// null when the filename doesn't follow the weight-pair convention.</summary>
    internal static string? DeriveWeightSibling(string nifPath)
    {
        if (string.IsNullOrWhiteSpace(nifPath)) return null;
        if (nifPath.EndsWith("_0.nif", StringComparison.OrdinalIgnoreCase))
            return nifPath[..^"_0.nif".Length] + "_1.nif";
        if (nifPath.EndsWith("_1.nif", StringComparison.OrdinalIgnoreCase))
            return nifPath[..^"_1.nif".Length] + "_0.nif";
        return null;
    }
}
