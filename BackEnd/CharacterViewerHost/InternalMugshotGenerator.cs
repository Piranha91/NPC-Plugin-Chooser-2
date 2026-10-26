using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CharacterViewer.Rendering;
using CharacterViewer.Rendering.Offscreen;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Bridges NPC2's mugshot pipeline to the in-process CharacterViewer.Rendering
/// offscreen renderer. Resolves mesh paths via <see cref="NpcMeshResolver"/>,
/// translates <see cref="InternalMugshotSettings"/> into an
/// <see cref="OffscreenRenderRequest"/>, and writes the resulting PNG to disk.
/// </summary>
public sealed class InternalMugshotGenerator
{
    private readonly NpcMeshResolver _resolver;
    private readonly IOffscreenRenderer _renderer;
    private readonly Settings _settings;
    private readonly ICharacterViewerSettings _viewerSettings;
    private readonly EnvironmentStateProvider _env;
    private readonly IBsaArchiveProvider _bsa;
    private readonly BsaHandler _bsaHandler;
    private readonly GeneratedMugshotTracker _tracker;
    private readonly CharacterViewerLogGate _logGate;
    private readonly Lazy<VM_NpcSelectionBar> _npcSelectionBar;
    private readonly FaceGenConsistencyAnalyzer _faceGenConsistency;

    // Bounds host-side concurrency into the renderer. The offscreen renderer
    // already serializes the GL render itself (single render thread draining a
    // queue), but every caller's pre-render work — mesh/attire resolution, BSA
    // pre-warm, request build — runs on the calling task. Unbounded callers
    // (notably the Mods-tab per-tile fan-out, which fires one GenerateAsync per
    // NPC at once) would otherwise pile hundreds of in-flight resolve states
    // and queued jobs into memory while the single render thread drains them
    // one at a time. Gating the whole method caps that backlog. Sized from
    // MaxParallelPortraitRenders to match the Legacy PortraitCreator's own
    // render semaphore (and the comment in GenerateAsync's diagnostics), and
    // resized live by SetMaxParallelRenders when the setting changes.
    private readonly ResizableConcurrencyLimiter _renderSemaphore;

    public InternalMugshotGenerator(
        NpcMeshResolver resolver,
        IOffscreenRenderer renderer,
        Settings settings,
        ICharacterViewerSettings viewerSettings,
        EnvironmentStateProvider env,
        IBsaArchiveProvider bsa,
        BsaHandler bsaHandler,
        GeneratedMugshotTracker tracker,
        CharacterViewerLogGate logGate,
        Lazy<VM_NpcSelectionBar> npcSelectionBar,
        FaceGenConsistencyAnalyzer faceGenConsistency,
        CharacterPreviewCache previewCache)
    {
        _resolver = resolver;
        _renderer = renderer;
        _settings = settings;
        _viewerSettings = viewerSettings;
        _env = env;
        _bsa = bsa;
        _bsaHandler = bsaHandler;
        _tracker = tracker;
        _logGate = logGate;
        _npcSelectionBar = npcSelectionBar;
        _faceGenConsistency = faceGenConsistency;

        // The limiter clamps a misconfigured 0/negative setting to 1 so it can't
        // deadlock every render. Later changes arrive via SetMaxParallelRenders.
        _renderSemaphore = new ResizableConcurrencyLimiter(_settings.MaxParallelPortraitRenders);

        // On an environment rebuild (game path / load order change) the renderer's
        // cached resolved paths, decoded pixels, and uploaded GL textures may be
        // stale — drop them so subsequent renders re-resolve against the new
        // environment. Singleton, so the subscription lives for the app's life.
        _env.OnEnvironmentUpdated.Subscribe(_ => _renderer.InvalidateCaches());

        // Per-decode attribution beside RenderTimings.csv (same opt-in trigger).
        if (RenderTimingsEnabled)
            previewCache.DecodeObserver = WriteDecodeEventCsv;
    }

    /// <summary>
    /// Renders the NPC at <paramref name="npcFormKey"/> to a PNG at
    /// <paramref name="outputPath"/>. <paramref name="modSetting"/> scopes
    /// the render to a specific mod — for mugshot tiles each tile passes
    /// its own source mod so tiles for the same NPC produced by different
    /// mods render distinct appearances. Returns false if mesh resolution
    /// fails or the renderer throws; the caller can fall back to FaceFinder
    /// / Legacy.
    /// <para>Pass <paramref name="missingMeshPathsOut"/> as a fresh
    /// <c>List&lt;string&gt;</c> to receive any host-expected mesh game-paths
    /// the resolver could not locate during the load. Non-empty after a
    /// successful return means the saved PNG is missing one or more shapes.
    /// Pass <paramref name="missingTexturePathsOut"/> the same way to
    /// receive texture game-paths the NIFs referenced that couldn't be
    /// decoded — those shapes render as a wireframe placeholder and the
    /// list drives a parallel "missing texture" tile overlay.</para>
    /// <para>Pass <paramref name="missingOutfitAssetsOut"/> to receive the
    /// outfit/headgear (attire-override) asset problems as pre-formatted display
    /// strings: override meshes that didn't resolve / render, plus attire
    /// textures the override build couldn't decode. These route to the
    /// outfit-asset tile icon rather than the base missing-asset icon, and — like
    /// the base lists but unlike the stale-physics notices — count as re-render-
    /// eligible missing assets. <paramref name="missingMeshPathsOut"/> and
    /// <paramref name="missingTexturePathsOut"/> stay limited to the base NPC's
    /// own head/body/hair meshes and textures.</para>
    /// <para>Pass <paramref name="dataFolderFallbackAssetsOut"/> to receive the
    /// game-relative paths of non-vanilla assets that engine-order resolution
    /// pulled from the data folder instead of this mod's Corresponding Mod
    /// Folders (runtime dependencies — whichever mod supplies them must stay
    /// activated). Informational only: they never gate a validated-only render
    /// and never count as missing assets.</para>
    /// <para>When <paramref name="assetValidatedOnly"/> is true and the
    /// renderer reported any missing meshes or textures, the rendered
    /// bytes are discarded — no PNG is written, no tracker entry is
    /// added, and the method returns false. Used by the "Generate All
    /// Mugshots" batch so the gallery only persists complete renders;
    /// per-tile callers leave the default (false) so the user still
    /// sees the wireframe + overlay for diagnostic purposes.</para>
    /// </summary>
    public async Task<bool> GenerateAsync(
        FormKey npcFormKey,
        ModSetting? modSetting,
        string outputPath,
        CancellationToken token = default,
        List<string>? missingMeshPathsOut = null,
        List<string>? missingTexturePathsOut = null,
        bool assetValidatedOnly = false,
        List<string>? faceGenMismatchOut = null,
        FormKey? targetNpcFormKey = null,
        List<string>? physicsConfigNoticesOut = null,
        List<string>? missingOutfitAssetsOut = null,
        List<string>? dataFolderFallbackAssetsOut = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int tid = Environment.CurrentManagedThreadId;
        // Per-render diagnostic capture. Flow-scoped via AsyncLocal so parallel
        // tile generations each get their own file (mugshots are rendered with
        // up to MaxParallelPortraitRenders concurrency and a single global
        // session would interleave them into one unreadable log). Forces
        // CharacterViewerLogGate.Verbose on for the duration so the renderer
        // emits its own [CharacterViewer] verbose lines.
        using var captureScope = MaybeStartRenderLogCapture(npcFormKey, modSetting);
        // Flows into the prewarm worker (Task.Run carries the ExecutionContext) so
        // DecodeEvents.csv can name the tile; the dedicated GL thread sees null.
        _currentTile.Value = (modSetting?.DisplayName ?? "(none)", npcFormKey.ToString());
        Trace($"ENTER tid={tid} npc={npcFormKey} mod=[{modSetting?.DisplayName ?? "(none)"}]");
        bool acquired = false;
        // Queued or running; the next-NPC prewarm only works while this is zero.
        Interlocked.Increment(ref _inFlightRenders);
        try
        {
            // Acquire before any resolve/extraction work so the in-flight
            // backlog (not just the GL render) stays bounded. WaitAsync throws
            // OCE if cancelled before a slot frees — caught below, returns
            // false, and the finally skips Release since acquired is still false.
            // Host-side phase split for RenderTimings.csv (wall ms on this flow).
            var host = new HostPhases();
            long preAcquire = sw.ElapsedMilliseconds;
            await _renderSemaphore.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            host.SlotWaitMs = sw.ElapsedMilliseconds - preAcquire;
            Trace($"  acquired-slot tid={Environment.CurrentManagedThreadId} waited={host.SlotWaitMs}ms");

            var linkCache = _env.LinkCache;
            if (linkCache == null) { Trace($"EXIT tid={tid} npc={npcFormKey} no-linkcache elapsed={sw.ElapsedMilliseconds}ms"); return false; }

            // Resolve scoped to the explicit per-tile mod. Records come from
            // its plugins (LinkCache.Winner fallback for anything the mod
            // doesn't override) and asset paths get rebased to mod folders
            // when present.
            long preResolve = sw.ElapsedMilliseconds;
            var paths = _resolver.Resolve(npcFormKey, modSetting);
            host.ResolveMs = sw.ElapsedMilliseconds - preResolve;
            Trace($"  resolve tid={tid} npc={npcFormKey} ok={paths != null} elapsed={sw.ElapsedMilliseconds}ms");
            if (paths == null) { Trace($"EXIT tid={tid} npc={npcFormKey} resolve-null elapsed={sw.ElapsedMilliseconds}ms"); return false; }

            // The renderer's asset resolver will need to pull body/skeleton/textures
            // from BSAs as well as loose files; pre-warm the BSA index so the first
            // render doesn't pay the open-all-archives cost. Run on a thread pool
            // thread — opening readers for hundreds of mods can take several seconds
            // and would otherwise block the UI thread that triggered the call.
            long preEnsure = sw.ElapsedMilliseconds;
            await Task.Run(() => _bsa.EnsureAllArchivesOpened(), token).ConfigureAwait(false);
            host.ArchivesMs = sw.ElapsedMilliseconds - preEnsure;
            Trace($"  EnsureAllArchivesOpened tid={Environment.CurrentManagedThreadId} elapsed={host.ArchivesMs}ms");

            // No FaceGen anywhere in this mod's scope: rendering would produce a
            // headless body, since every other mesh (body / hands / skin) resolves
            // cleanly off the race/armature. Abort so the caller leaves the
            // placeholder up instead of writing a misleading PNG. Mirrors the
            // Legacy path's FindNpcNifPath null-check in
            // BatchMugshotGenerator.RunSelectedRendererAsync.
            // A Traits-templated NPC is NOT this case — the probe (like Resolve
            // above) follows the template chain and finds the face the game draws
            // for it. Only a chain that can't be followed (Leveled-NPC terminus,
            // dangling link) or a genuinely FaceGen-less NPC lands here.
            long preFaceGen = sw.ElapsedMilliseconds;
            bool faceGenExists = _resolver.FaceGenExists(npcFormKey, modSetting);
            host.FaceGenCheckMs = sw.ElapsedMilliseconds - preFaceGen;
            if (!faceGenExists)
            {
                Trace($"EXIT tid={Environment.CurrentManagedThreadId} npc={npcFormKey} no-facegen totalElapsed={sw.ElapsedMilliseconds}ms");
                return false;
            }

            var cfg = _settings.InternalMugshot;

            // Attire / headgear (Include Default Outfit / Include headgear).
            // Honors a per-NPC override (right-click > Render in the NPC list)
            // when set, else the persisted Settings-tab defaults. Resolved
            // through this tile's explicit mod scope so the mugshot matches the
            // preview; the offscreen renderer loads/skins/hides them like the
            // live preview. The same effective flags are stamped into the PNG
            // metadata below so the staleness checker stays consistent.
            var (effectiveIncludeDefaultOutfit, effectiveIncludeHeadgear) =
                _settings.GetEffectiveAttireFlags(npcFormKey);
            // targetNpcFormKey is the NPC being patched in the load order (only
            // differs from npcFormKey for guest appearances); the effective-
            // outfit simulation (patch mode + SkyPatcher/SPID) keys on it. The
            // resulting outfit IDENTITY is stamped into the PNG metadata below
            // so the staleness checker can re-render only when the depicted
            // outfit actually changes.
            // alwaysRenderOutfitWigsAndAntlers: a mugshot shows the face this mod gives the
            // NPC, and a wig or antler is part of that face — so a detected Default-Outfit
            // wig/antler is drawn even under Handling Mode "Leave As Is", where the patch
            // would leave it behind, and regardless of both attire toggles. Without this
            // they vanish from the tile entirely, since the outfit walk treats their
            // head-slot armature as headgear. The tile flags the wig gap on its has-wig
            // badge; the live 3D preview stays output-faithful and does not pass this.
            // (Batch generation routes through here too.)
            long preAttire = sw.ElapsedMilliseconds;
            var meshOverrides = _resolver.ResolveAttireMeshOverrides(
                npcFormKey, modSetting, effectiveIncludeDefaultOutfit, effectiveIncludeHeadgear,
                targetNpcFormKey, out var outfitDisplay, alwaysRenderOutfitWigsAndAntlers: true);
            host.AttireMs = sw.ElapsedMilliseconds - preAttire;
            var meshOverrideWarningsOut = new List<MeshOverrideWarning>();
            // Attire (outfit/headgear) textures the override build couldn't decode.
            // The renderer attributes these as the post-override delta of its
            // missing-texture set, keeping them out of the base missingTexturePathsOut.
            var missingOutfitTextures = new List<string>();
            // Assets engine-order resolution pulled from the data folder rather
            // than this mod's scopes (Tier 2 loose / Tier 3 broadcast). Always
            // requested — the stamp below needs them even when the caller
            // didn't pass an out-list. Filtered against the vanilla asset-path
            // index after the render so Base Game content doesn't count as a
            // keep-activated dependency.
            var dataFolderFallbackRaw = new List<string>();

            // Opt-in per-render phase timing (drop a LogRenderTimings.txt file
            // next to the exe). Pure data, no logging — so timings are
            // representative with the verbose render trace OFF.
            var renderTimings = RenderTimingsEnabled ? new RenderTimings() : null;

            long preScopes = sw.ElapsedMilliseconds;
            var resolutionScopes = _resolver.BuildResolutionScopes(modSetting, npcFormKey);
            host.ScopesMs = sw.ElapsedMilliseconds - preScopes;

            var request = new OffscreenRenderRequest
            {
                MeshPaths = paths,
                MeshOverrides = meshOverrides.Count > 0 ? meshOverrides : null,
                MeshOverrideWarningDetailsOut = meshOverrideWarningsOut,
                Width = cfg.OutputWidth,
                Height = cfg.OutputHeight,
                BackgroundRgb = (cfg.BackgroundR, cfg.BackgroundG, cfg.BackgroundB),
                Lighting = ResolveLayout(cfg.LightingLayoutName) ?? CharacterViewerLightingPresets.DefaultLayout,
                Colors = ResolveColors(cfg.LightingColorSchemeName) ?? CharacterViewerLightingPresets.DefaultColorScheme,
                Camera = cfg.CameraMode == InternalMugshotCameraMode.Manual
                    ? BuildOrbitStateFromManual(cfg)
                    : BuildAutoFraming(cfg),
                EnableNormalMapHack = _settings.EnableNormalMapHack,
                UseModdedFallbackTextures = _settings.UseModdedFallbackTextures,
                // Strict per-mod asset-resolution chain: vanilla (data folder
                // + Base Game / Creation Club plugin filenames) at index 0,
                // then each of the mod's CorrespondingFolderPaths paired with
                // its CorrespondingModKeys. The renderer iterates last-to-first
                // in two phases (all loose, then all scoped BSA) per the
                // 8-step contract. AdditionalScopes (1.2.0+) supersedes
                // AdditionalDataFolders.
                AdditionalScopes = resolutionScopes,
                // Engine-order resolution (2.8.0+): after the scope chain and
                // data-folder loose, EVERY asset may fall back to the broadcast
                // archive tier (enabled load-order plugins' data-folder BSAs,
                // ranked by load order). This is what lets headpart/skin
                // textures that a mod references from ANOTHER mod's BSA (e.g.
                // hairs reusing the original hair mod's paths) resolve the way
                // the game resolves them, instead of rendering as missing.
                // ForceLegacy is the render harness's A/B switch — never set in
                // normal operation (see RenderResolutionMode).
                AllowLoadOrderFallback = !RenderResolutionMode.ForceLegacy,
                Cancellation = token,
                MissingMeshPathsOut = missingMeshPathsOut,
                MissingTexturePathsOut = missingTexturePathsOut,
                MissingOutfitTexturePathsOut = missingOutfitTextures,
                DataFolderFallbackPathsOut = dataFolderFallbackRaw,
                // Per-render extraction cache clearing was removed: it raced
                // with concurrent interactive 3D-preview loads (both share the
                // resolver's extraction directory), causing the preview to lose
                // mid-load extracted files and render wireframes. The cache
                // now lives at <exe-dir>\CharacterViewerCache\ and accumulates
                // across the session — bounded by |unique BSAs| × |unique
                // touched paths|, and intentionally reusable across runs via
                // the resolver's per-BSA SHA token. Surface a manual clear
                // action if disk pressure becomes a real concern.
                // Advanced asset-resolution toggles (CharacterViewer.Rendering 2.3.0+).
                // FaceGen NIFs / FaceTint DDS at the vanilla scope (i=0) are
                // hard-skipped by the renderer's Phase 1 loose walk regardless
                // of these toggles, so Base Game / Creation Club tiles always
                // get vanilla FaceGen from base-game BSAs. Non-FaceGen paths
                // (body / skin / hair textures) still honor the user's toggle —
                // a CBBE / skin replacer in Data legitimately leaks into the
                // Base Game preview when VanillaLooseOverridesBsa is true.
                VanillaLooseOverridesBsa = cfg.VanillaLooseOverridesBsa,
                VanillaLooseOverridesModLoose = cfg.VanillaLooseOverridesModLoose,
                RenderMissingTextureAsWireframe = cfg.RenderMissingTextureAsWireframe,
                EnableToneMapping = cfg.EnableToneMapping,
                EnableShadows = cfg.EnableShadows,
                ExcludeHairShadowCaster = cfg.ExcludeHairShadowCaster,
                SoftenShadowEdges = cfg.SoftenShadowEdges,
                ShadowPcfRadius = cfg.ShadowPcfRadius,
                TightShadowFrustum = cfg.TightShadowFrustum,
                ShadowFrustumRadius = cfg.ShadowFrustumRadius,
                EnableAmbientOcclusion = cfg.EnableAmbientOcclusion,
                SsaoRadius = cfg.SsaoRadius,
                SsaoBias = cfg.SsaoBias,
                SsaoIntensity = cfg.SsaoIntensity,
                SsaoThickness = cfg.SsaoThickness,
                SsaoHairGap = cfg.SsaoHairGap,
                EnableEyeCatchlight = cfg.EnableEyeCatchlight,
                SubsurfaceStrength = cfg.SubsurfaceStrength,
                VignetteRadius = cfg.VignetteRadius,
                VignetteIntensity = cfg.VignetteIntensity,
                SkinSaturationBoost = cfg.SkinSaturationBoost,
                Exposure = cfg.Exposure,
                TonemapHairRelief = cfg.TonemapHairRelief,
                HairAlbedoCompensate = cfg.HairAlbedoCompensate,
                DaylightBoost = cfg.DaylightBoost,
                DaylightBoostIntensity = cfg.DaylightBoostIntensity,
                EnableBloom = cfg.EnableBloom,
                BloomIntensity = cfg.BloomIntensity,
                // Snapshot the active flow-scoped writer NOW (still on the
                // host's logical call context). The renderer's dedicated
                // render thread doesn't inherit this AsyncLocal, so we hand
                // it a thread-agnostic closure instead. Null when LogRenderLogic
                // is off (no capture scope active) — the renderer skips the
                // diagnostic emission entirely.
                DiagnosticLog = BuildRenderDiagnosticLog(),
                TimingsOut = renderTimings,
            };

            // Pre-warm this NPC's NIF parse + DDS decode on a worker thread before
            // the GL render. The renderer's single GL thread is the serial
            // bottleneck; with up to MaxParallelPortraitRenders tiles in flight,
            // this NPC's heavy CPU phases (parse + decode) overlap the render
            // thread's GL work on the other tiles, so when its own turn comes the
            // render hits the warm caches and pays only GL upload + draw + readback.
            // Best-effort and non-throwing — a miss just decodes on the render
            // thread as before.
            long prePrewarm = sw.ElapsedMilliseconds;
            await _renderer.PrewarmAsync(request).ConfigureAwait(false);
            Trace($"  PrewarmAsync tid={Environment.CurrentManagedThreadId} elapsed={sw.ElapsedMilliseconds - prePrewarm}ms");

            long preRender = sw.ElapsedMilliseconds;
            byte[] png = await _renderer.RenderToPngAsync(request).ConfigureAwait(false);
            long hostRenderMs = sw.ElapsedMilliseconds - preRender;
            Trace($"  RenderToPngAsync tid={Environment.CurrentManagedThreadId} bytes={png.Length} elapsed={hostRenderMs}ms");
            host.PrewarmWallMs = preRender - prePrewarm;
            host.RenderMs = hostRenderMs;
            long postRenderStart = sw.ElapsedMilliseconds;
            // Written at each exit below so post-render work (consistency check,
            // PNG write, metadata stamp) is included.
            void LogTimings()
            {
                if (renderTimings == null) return;
                host.PostRenderMs = sw.ElapsedMilliseconds - postRenderStart;
                WriteRenderTimingsCsv(npcFormKey, modSetting, renderTimings, host, png.Length,
                    hostElapsedMs: sw.ElapsedMilliseconds);
            }

            // Asset-validation gate (batch only). The renderer doesn't expose
            // an early-abort hook for missing assets — the lists are populated
            // during the render, so the bytes already exist. Discard them
            // before any disk I/O when the caller asked for validated-only
            // output, so an incomplete render leaves no PNG, no tracker
            // entry, and no metadata behind.
            int missingMeshCount = missingMeshPathsOut?.Count ?? 0;
            int missingTextureCount = missingTexturePathsOut?.Count ?? 0;
            if (assetValidatedOnly && (missingMeshCount > 0 || missingTextureCount > 0))
            {
                LogTimings();
                Trace($"EXIT tid={Environment.CurrentManagedThreadId} npc={npcFormKey} skipped-missing-assets meshes={missingMeshCount} textures={missingTextureCount} totalElapsed={sw.ElapsedMilliseconds}ms");
                return false;
            }

            // Attire/headgear override warnings are surfaced for display only,
            // AFTER the validated-only gate above — a helmet that needs an absent
            // skeleton shouldn't discard an otherwise-complete face mugshot.
            // These are OUTFIT assets, so they route to the outfit-asset surface
            // (its own icon + metadata key), NOT the base NPC missing-mesh list —
            // missingMeshPathsOut stays limited to the NPC's own head/body/hair.
            // StalePhysicsConfig is split off further: the mod's own physics-XML
            // link is broken but the render is correct, and nothing the user
            // installs can clear it — persisting it as a missing asset would
            // re-stale this mugshot every session. It is stamped under its own
            // staleness-neutral metadata key instead, so the tile can show the
            // informational outfit notice across restarts while the real missing
            // outfit assets stay re-render-eligible.
            var missingOutfitAssets = new List<string>();
            List<string>? physicsConfigNotices = null;
            if (meshOverrideWarningsOut.Count > 0)
            {
                Trace($"  meshOverrideWarnings tid={Environment.CurrentManagedThreadId} count={meshOverrideWarningsOut.Count}");
                missingOutfitAssets.AddRange(meshOverrideWarningsOut
                    .Where(w => w.Kind != MeshOverrideWarningKind.StalePhysicsConfig)
                    .Select(w => w.Message));
                physicsConfigNotices = meshOverrideWarningsOut
                    .Where(w => w.Kind == MeshOverrideWarningKind.StalePhysicsConfig)
                    .Select(w => w.Message).ToList();
                if (physicsConfigNotices.Count > 0)
                    physicsConfigNoticesOut?.AddRange(physicsConfigNotices);
            }
            // Attire textures the override build couldn't decode (renderer delta).
            // Formatted so they read clearly beside the mesh lines in the shared
            // outfit-asset tooltip.
            foreach (var texPath in missingOutfitTextures)
                missingOutfitAssets.Add("Outfit texture not found: " + texPath);
            if (missingOutfitAssets.Count > 0)
                missingOutfitAssetsOut?.AddRange(missingOutfitAssets);

            // Data-folder-fallback assets, minus anything living at a vanilla
            // asset path — a fallback hit that the base game itself ships is
            // not a keep-activated dependency (and this is also what keeps the
            // Base Game / Creation Club tiles badge-free). Vanilla index paths
            // use backslash separators (OrdinalIgnoreCase), so normalize
            // before membership testing.
            // Hits an earlier next-NPC prewarm recorded for this tile (its own request);
            // this render may have found those assets cached without re-resolving them.
            if (_prewarmFallbackHits.TryRemove(TileKey(npcFormKey, modSetting, targetNpcFormKey), out var prewarmed))
            {
                foreach (var hit in prewarmed)
                    if (!dataFolderFallbackRaw.Contains(hit, StringComparer.OrdinalIgnoreCase))
                        dataFolderFallbackRaw.Add(hit);
            }
            var dataFolderAssets = new List<string>();
            if (dataFolderFallbackRaw.Count > 0)
            {
                var vanillaPaths = await _bsaHandler.GetVanillaAssetPathsAsync().ConfigureAwait(false);
                foreach (var gamePath in dataFolderFallbackRaw)
                {
                    string normalized = gamePath.Replace('/', '\\').TrimStart('\\');
                    if (!vanillaPaths.Contains(normalized))
                        dataFolderAssets.Add(gamePath);
                }
                if (dataFolderAssets.Count > 0)
                {
                    Trace($"  dataFolderFallbackAssets tid={Environment.CurrentManagedThreadId} count={dataFolderAssets.Count} (raw={dataFolderFallbackRaw.Count})");
                    dataFolderFallbackAssetsOut?.AddRange(dataFolderAssets);
                }
            }

            // FaceGen-vs-records consistency. CV.R renders the baked FaceGen geometry
            // directly, so a perfectly-rendered mugshot can still hide the in-game
            // dark-face bug (a head part the NPC's records resolve to has no baked
            // shape — wrong plugin version, missing master, null/swapped part, or an
            // author .nif/plugin mismatch). Surfaced through the same missing-asset
            // overlay. Best-effort and non-throwing; only inspectable when the head
            // NIF resolved to a loose file on disk (the BSA-only case is skipped).
            string? faceGenMismatch = null;
            try
            {
                string? headNif = paths.HeadMeshPath;
                if (!string.IsNullOrEmpty(headNif) && Path.IsPathRooted(headNif) && File.Exists(headNif))
                {
                    var (npcForCheck, resolveHeadPart, resolveRace, npcFromModPlugins) =
                        _resolver.ResolveNpcForConsistency(npcFormKey, modSetting);
                    if (npcForCheck != null)
                    {
                        var analysis = _faceGenConsistency.Analyze(npcForCheck, resolveHeadPart, resolveRace, headNif);
                        if (analysis.HasMismatch)
                        {
                            // Mod-scoped resolution (the tile's own mod first), so the remedies
                            // must talk about that mod's files — not load-order conflict winners.
                            faceGenMismatch = analysis.BuildReason(
                                scope: FaceGenConsistencyAnalyzer.ReasonScope.SelectedMod,
                                subjectSuppliesRecord: npcFromModPlugins);
                            faceGenMismatchOut?.Add(faceGenMismatch);
                            Trace($"  facegen-mismatch tid={Environment.CurrentManagedThreadId} npc={npcFormKey} missing={analysis.MissingBakedShapes.Count} unresolved={analysis.UnresolvedHeadParts.Count} null={analysis.NullHeadPartLinks} orphans={analysis.OrphanBakedShapes.Count}");
                        }
                    }
                }
            }
            catch (Exception fcEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"InternalMugshotGenerator: FaceGen consistency check failed for {npcFormKey}: {fcEx.Message}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, png, token).ConfigureAwait(false);

            // Stamp the "Parameters" tEXt chunk so the staleness checker can
            // detect cross-renderer switches and version drift, IsAutoGenerated
            // returns true for these PNGs, and the per-render missing-asset
            // arrays survive across app restarts (the tile's overlay reads
            // them back on load).
            try
            {
                // Warning-icon visibility gates the STAMP, not the out-lists:
                // callers still receive the diagnostics (assetValidatedOnly
                // gating, logs), but a PNG rendered while a toggle is off
                // carries no trace of that warning class — which is what lets
                // MugshotStalenessChecker's toggle-off check settle after one
                // regeneration instead of looping.
                bool stampNpcAssets = _settings.InternalMugshot.ShowMissingNpcAssetsIcon;
                bool stampOutfitAssets = _settings.InternalMugshot.ShowMissingOutfitAssetsIcon;
                bool stampDataFolderAssets = _settings.InternalMugshot.ShowDataFolderAssetsIcon;
                // The attire identity = effective outfit + the wig-forwarding
                // contribution (WigHandlingMode) — same composition the
                // staleness checker's identity providers use, so a mode or
                // wig-set change re-stales exactly the affected tiles.
                var parametersJson = InternalMugshotMetadata.Build(
                    npcFormKey, _settings.InternalMugshot,
                    effectiveIncludeDefaultOutfit, effectiveIncludeHeadgear,
                    outfitDisplay.IdentityStamp + _resolver.ComputeWigIdentitySuffix(
                        npcFormKey, modSetting, effectiveIncludeDefaultOutfit),
                    stampNpcAssets ? missingMeshPathsOut : null,
                    stampNpcAssets ? missingTexturePathsOut : null,
                    stampNpcAssets ? faceGenMismatch : null,
                    stampOutfitAssets ? physicsConfigNotices : null,
                    stampOutfitAssets ? missingOutfitAssets : null,
                    stampDataFolderAssets ? dataFolderAssets : null);
                MugshotPngMetadata.InjectParameters(outputPath, parametersJson);
            }
            catch (Exception metaEx)
            {
                // Metadata injection failure is non-fatal — the PNG is still valid,
                // it just won't auto-update on settings/version drift.
                System.Diagnostics.Debug.WriteLine(
                    $"InternalMugshotGenerator: metadata injection failed for {outputPath}: {metaEx.Message}");
            }

            // Mirror the legacy renderer's behavior so the "Fast" search modes
            // know this PNG was auto-generated. Track + immediate throttled
            // save so a subsequent abnormal exit (debugger stop, crash, OS
            // shutdown) doesn't lose the cache entry.
            _tracker.Track(outputPath);
            LogTimings();
            Trace($"EXIT tid={Environment.CurrentManagedThreadId} npc={npcFormKey} ok totalElapsed={sw.ElapsedMilliseconds}ms");
            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancellation may have fired mid-write or after the file landed
            // on disk but before we got to track it. Either way, the partial
            // / complete PNG is now an orphan unless we track it — Fast-mode
            // delete would never see it. Track-if-exists handles both cases.
            _tracker.TrackIfFileExists(outputPath);
            Trace($"EXIT tid={Environment.CurrentManagedThreadId} npc={npcFormKey} cancelled totalElapsed={sw.ElapsedMilliseconds}ms");
            return false;
        }
        catch (Exception ex)
        {
            // Same fallback for non-cancellation errors that fired after the
            // write succeeded (e.g. metadata-injection IO error, post-write
            // disk thrash). The PNG itself may still be valid; track so the
            // user can purge it later via Fast-mode delete.
            _tracker.TrackIfFileExists(outputPath);
            System.Diagnostics.Debug.WriteLine(
                $"InternalMugshotGenerator failed for {npcFormKey}: {ExceptionLogger.GetExceptionStack(ex)}");
            Trace($"EXIT tid={Environment.CurrentManagedThreadId} npc={npcFormKey} ERROR totalElapsed={sw.ElapsedMilliseconds}ms err={ex.Message}");
            return false;
        }
        finally
        {
            if (acquired) _renderSemaphore.Release();
            Interlocked.Decrement(ref _inFlightRenders);
        }
    }

    private int _inFlightRenders;

    /// <summary>GenerateAsync calls queued or running (any tile, any caller).</summary>
    public int InFlightRenders => Volatile.Read(ref _inFlightRenders);

    // Data-folder-fallback hits captured by PrewarmTileAsync, merged into that tile's
    // render. The renderer records hits per request; a separate prewarm request warms
    // the caches, so the real render may resolve those assets from cache and never
    // re-record them. Keyed by (source NPC, mod, target NPC).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(FormKey Npc, string Mod, FormKey? Target), string[]>
        _prewarmFallbackHits = new();

    private static (FormKey, string, FormKey?) TileKey(FormKey npc, ModSetting? mod, FormKey? target)
        => (npc, mod?.DisplayName ?? "", target);

    /// <summary>
    /// Warms the renderer's CPU caches (NIF parse, transparency verdicts, any decode the
    /// upload will need) for one tile without rendering it, so a later GenerateAsync of
    /// the same tile pays only GL upload, draw and encode. Mirrors GenerateAsync's
    /// pre-render steps and the request fields <see cref="IOffscreenRenderer.PrewarmAsync"/>
    /// reads. Takes no render slot: callers schedule it only while the renderer is idle.
    /// Best-effort and non-throwing; returns false when there was nothing to warm.
    /// </summary>
    public async Task<bool> PrewarmTileAsync(FormKey npcFormKey, ModSetting? modSetting,
        FormKey? targetNpcFormKey, CancellationToken token)
    {
        try
        {
            if (_env.LinkCache == null) return false;
            var paths = _resolver.Resolve(npcFormKey, modSetting);
            if (paths == null) return false;
            await Task.Run(() => _bsa.EnsureAllArchivesOpened(), token).ConfigureAwait(false);
            if (!_resolver.FaceGenExists(npcFormKey, modSetting)) return false;
            var (includeOutfit, includeHeadgear) = _settings.GetEffectiveAttireFlags(npcFormKey);
            var meshOverrides = _resolver.ResolveAttireMeshOverrides(
                npcFormKey, modSetting, includeOutfit, includeHeadgear,
                targetNpcFormKey, out _, alwaysRenderOutfitWigsAndAntlers: true);
            var cfg = _settings.InternalMugshot;
            var fallbackHits = new List<string>();
            var request = new OffscreenRenderRequest
            {
                MeshPaths = paths,
                MeshOverrides = meshOverrides.Count > 0 ? meshOverrides : null,
                AdditionalScopes = _resolver.BuildResolutionScopes(modSetting, npcFormKey),
                AllowLoadOrderFallback = !RenderResolutionMode.ForceLegacy,
                VanillaLooseOverridesBsa = cfg.VanillaLooseOverridesBsa,
                VanillaLooseOverridesModLoose = cfg.VanillaLooseOverridesModLoose,
                PrewarmFallbackPathsOut = fallbackHits,
                Cancellation = token,
                Width = cfg.OutputWidth,
                Height = cfg.OutputHeight,
            };
            token.ThrowIfCancellationRequested();
            await _renderer.PrewarmAsync(request).ConfigureAwait(false);
            if (token.IsCancellationRequested) return false;
            // Entries for tiles that were never rendered (the user went elsewhere) would
            // otherwise accumulate for the session; losing one only drops a badge hint.
            if (_prewarmFallbackHits.Count > 512) _prewarmFallbackHits.Clear();
            _prewarmFallbackHits[TileKey(npcFormKey, modSetting, targetNpcFormKey)] = fallbackHits.ToArray();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"InternalMugshotGenerator.PrewarmTileAsync failed for {npcFormKey}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Applies a new Max parallel renders value without a restart; see
    /// <see cref="ResizableConcurrencyLimiter.SetLimit"/>.</summary>
    public void SetMaxParallelRenders(int maxParallel) => _renderSemaphore.SetLimit(maxParallel);

    private static void Trace(string message)
    {
        string line = $"[InternalMugshotGenerator] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        System.Diagnostics.Trace.WriteLine(line);
        RenderLogCapture.Write(line);
    }

    // --- Lightweight per-render phase profiling (opt-in, verbose-independent) ---
    // Enabled by dropping a "LogRenderTimings.txt" file next to the exe (checked
    // once at startup, mirroring BsaContentsDiag). Writes one CSV row per
    // completed render to RenderLogs/RenderTimings.csv. Deliberately separate
    // from LogRenderLogic so timings can be collected WITHOUT the verbose
    // per-asset trace, whose I/O would otherwise inflate the very numbers being
    // measured.
    internal static readonly bool RenderTimingsEnabled =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "LogRenderTimings.txt"));
    private static readonly object _timingsCsvLock = new();
    private static readonly string _timingsCsvPath =
        Path.Combine(AppContext.BaseDirectory, "RenderLogs", "RenderTimings.csv");
    // New columns are appended so existing column positions stay stable.
    // prewarmDecodeMs = DDS decode on the prewarm worker (not in rendererTotalMs);
    // prewarmWallMs = host wall time awaiting PrewarmAsync; hostElapsedMs =
    // GenerateAsync start (incl. semaphore wait) to CSV write; completedAt =
    // local wall-clock time of the write, for batch throughput/concurrency.
    // host*Ms split the host flow before/after the renderer (see HostPhases);
    // maxParallel and ddsPolicy name the run's configuration for A/B runs.
    private const string TimingsCsvHeader =
        "mod,npcFormKey,npcName,setupMs,buildMs,resolveMs,parseMs,loadMs,buildShapesMs,installMs,decodeMs,uploadMs,drawMs,readbackMs,encodeMs,rendererTotalMs,hostRenderMs,pngBytes,prewarmDecodeMs,prewarmWallMs,hostElapsedMs,completedAt," +
        "hostSlotWaitMs,hostResolveMs,hostArchivesMs,hostFaceGenCheckMs,hostAttireMs,hostScopesMs,hostPostRenderMs,maxParallel,ddsPolicy";
    private static bool _timingsCsvHeaderChecked;

    private static readonly AsyncLocal<(string Mod, string Npc)?> _currentTile = new();
    private static readonly string _decodeCsvPath =
        Path.Combine(AppContext.BaseDirectory, "RenderLogs", "DecodeEvents.csv");
    private const string DecodeCsvHeader = "at,mod,npcFormKey,reason,cubemap,width,height,decodeMs,gamePath";
    private static bool _decodeCsvHeaderChecked;

    /// <summary>One row per CPU DDS decode (renderer cache miss). Decodes on the GL render
    /// thread, or from the interactive preview, carry an empty tile.</summary>
    private static void WriteDecodeEventCsv(DdsDecodeEvent e)
    {
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            var tile = _currentTile.Value;
            string row = string.Join(",",
                DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", ci),
                Q(tile?.Mod ?? ""), Q(tile?.Npc ?? ""), e.Reason, e.Cubemap ? "1" : "0",
                e.Width.ToString(ci), e.Height.ToString(ci), e.Milliseconds.ToString("F1", ci),
                Q(e.RelativeGamePath));
            lock (_timingsCsvLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_decodeCsvPath)!);
                if (!_decodeCsvHeaderChecked)
                {
                    _decodeCsvHeaderChecked = true;
                    if (File.Exists(_decodeCsvPath) &&
                        File.ReadLines(_decodeCsvPath).FirstOrDefault() != DecodeCsvHeader)
                    {
                        File.Move(_decodeCsvPath, Path.Combine(Path.GetDirectoryName(_decodeCsvPath)!,
                            $"DecodeEvents_oldheader_{DateTime.Now:yyyyMMdd-HHmmss}.csv"));
                    }
                }
                if (!File.Exists(_decodeCsvPath))
                    File.AppendAllText(_decodeCsvPath, DecodeCsvHeader + "\n");
                File.AppendAllText(_decodeCsvPath, row + "\n");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WriteDecodeEventCsv failed: {ex.Message}");
        }
    }

    /// <summary>Host wall-clock ms per phase of one GenerateAsync, for RenderTimings.csv.
    /// Covers the work outside the renderer that its own <see cref="RenderTimings"/> can't see.</summary>
    private sealed class HostPhases
    {
        public long SlotWaitMs, ResolveMs, ArchivesMs, FaceGenCheckMs, AttireMs, ScopesMs;
        public long PrewarmWallMs, RenderMs, PostRenderMs;
    }

    private void WriteRenderTimingsCsv(
        FormKey npcFormKey, ModSetting? modSetting, RenderTimings t, HostPhases host, int bytes,
        long hostElapsedMs)
    {
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string mod = modSetting?.DisplayName ?? "(none)";
            string npcName = _npcSelectionBar.Value?.AllNpcs
                .FirstOrDefault(n => n.NpcFormKey.Equals(npcFormKey))?.DisplayName ?? string.Empty;
            // Escape the two free-text fields for CSV (quote + double inner quotes).
            string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

            // installMs includes decodeMs; uploadMs below is the derived
            // GL-upload remainder (install minus decode).
            double uploadMs = t.InstallMs - t.DecodeMs;
            string row = string.Join(",",
                Q(mod), Q(npcFormKey.ToString()), Q(npcName),
                t.SetupMs.ToString("F1", ci), t.BuildMs.ToString("F1", ci),
                t.ResolveMs.ToString("F1", ci), t.ParseMs.ToString("F1", ci),
                t.LoadMs.ToString("F1", ci), t.BuildShapesMs.ToString("F1", ci),
                t.InstallMs.ToString("F1", ci), t.DecodeMs.ToString("F1", ci),
                uploadMs.ToString("F1", ci), t.DrawMs.ToString("F1", ci),
                t.ReadbackMs.ToString("F1", ci), t.EncodeMs.ToString("F1", ci),
                t.TotalMs.ToString("F1", ci), host.RenderMs.ToString(ci), bytes.ToString(ci),
                t.PrewarmDecodeMs.ToString("F1", ci), host.PrewarmWallMs.ToString(ci),
                hostElapsedMs.ToString(ci), DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", ci),
                host.SlotWaitMs.ToString(ci), host.ResolveMs.ToString(ci), host.ArchivesMs.ToString(ci),
                host.FaceGenCheckMs.ToString(ci), host.AttireMs.ToString(ci), host.ScopesMs.ToString(ci),
                host.PostRenderMs.ToString(ci), _renderSemaphore.Limit.ToString(ci),
                TextureUploadPolicy.StampValue);

            lock (_timingsCsvLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_timingsCsvPath)!);
                // Once per session: a file written under an older header would
                // misalign the appended columns, so archive it and start fresh.
                if (!_timingsCsvHeaderChecked)
                {
                    _timingsCsvHeaderChecked = true;
                    if (File.Exists(_timingsCsvPath) &&
                        File.ReadLines(_timingsCsvPath).FirstOrDefault() != TimingsCsvHeader)
                    {
                        string archive = Path.Combine(Path.GetDirectoryName(_timingsCsvPath)!,
                            $"RenderTimings_oldheader_{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                        File.Move(_timingsCsvPath, archive);
                    }
                }
                if (!File.Exists(_timingsCsvPath))
                {
                    File.AppendAllText(_timingsCsvPath, TimingsCsvHeader + "\n");
                }
                File.AppendAllText(_timingsCsvPath, row + "\n");
            }
        }
        catch (Exception ex)
        {
            // Profiling must never disrupt a render.
            System.Diagnostics.Debug.WriteLine($"WriteRenderTimingsCsv failed: {ex.Message}");
        }
    }

    /// <summary>Captures the active <see cref="RenderLogCapture"/> writer
    /// into a thread-agnostic closure, prefixing each line with
    /// <c>[CharacterViewer]</c> so the framing diagnostics interleave
    /// cleanly with the rest of the lib's verbose trace in the log file.
    /// Returns null when no capture session is active so the renderer
    /// short-circuits the diagnostic build-up entirely.</summary>
    private static Action<string>? BuildRenderDiagnosticLog()
    {
        var snapshot = RenderLogCapture.SnapshotWriter();
        if (snapshot == null) return null;
        return msg => snapshot("[CharacterViewer] " + msg);
    }

    /// <summary>If <see cref="InternalMugshotSettings.LogRenderLogic"/> is on,
    /// opens a per-render flow-scoped capture writing to
    /// <c>&lt;ExeDir&gt;\RenderLogs\&lt;ModName&gt;_&lt;NpcLabel&gt;_Mugshot.txt</c> and
    /// forces <see cref="CharacterViewerLogGate.Verbose"/> on for the
    /// session's duration so the renderer's verbose lines are emitted.
    /// Disposing the scope flushes the file and restores the prior verbose
    /// state. AsyncLocal scoping isolates parallel tile renders into
    /// separate files (host-side messages on each render's async flow land
    /// in that flow's file). Returns a no-op disposable when the toggle is
    /// off so callers can <c>using var</c> unconditionally.</summary>
    private IDisposable MaybeStartRenderLogCapture(FormKey formKey, ModSetting? modSetting)
    {
        if (!_settings.InternalMugshot.LogRenderLogic) return EmptyDisposable.Instance;

        // Sanitize all fields — FormKey.ToString() is "xxxxxxxx:Plugin.esp",
        // the colon is illegal in Windows filenames; mod and NPC display
        // names can legitimately contain slashes / colons too.
        string modName = modSetting?.DisplayName ?? "Unscoped";
        string npcLabel = _npcSelectionBar.Value?.AllNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(formKey))?.DisplayName
                          ?? formKey.ToString();
        string safeModName = SanitizeForFileName(modName);
        string safeNpcLabel = SanitizeForFileName(npcLabel);
        string folder = Path.Combine(AppContext.BaseDirectory, "RenderLogs");
        string filePath = Path.Combine(folder, $"{safeModName}_{safeNpcLabel}_Mugshot.txt");

        bool prevVerbose = _logGate.Verbose;
        _logGate.Verbose = true;
        var capture = RenderLogCapture.BeginScopedCapture(filePath,
            $"NPC2 Mugshot Render Log — Mod=[{modName}] NPC=[{formKey}]");

        return new ActionDisposable(() =>
        {
            try { capture.Dispose(); }
            finally { _logGate.Verbose = prevVerbose; }
        });
    }

    private static string SanitizeForFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        }
        return new string(chars);
    }

    private sealed class ActionDisposable : IDisposable
    {
        private Action? _action;
        public ActionDisposable(Action action) { _action = action; }
        public void Dispose() { var a = _action; _action = null; a?.Invoke(); }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }

    private CharacterViewerLightingLayout? ResolveLayout(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return CharacterViewerLightingPresets.FindLayoutOrDefault(name, _viewerSettings.UserLightingLayouts);
    }

    private CharacterViewerLightingColorScheme? ResolveColors(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return CharacterViewerLightingPresets.FindColorSchemeOrDefault(name, _viewerSettings.UserLightingColorSchemes);
    }

    /// <summary>
    /// Mirrors NPC Portrait Creator's hair-aware framing: include the head and
    /// any head accessories whose lower bound sits above the head's bottom Y
    /// (so floor-length hair doesn't blow out the frame, but eyebrows / hair-above
    /// pass through unchanged).
    /// </summary>
    private static CameraFraming BuildAutoFraming(InternalMugshotSettings cfg)
    {
        var shapes = new List<FramingShape>
        {
            new() { Selector = FramingShapeSelector.PrimaryHead.Instance },
        };
        if (cfg.IncludeAccessories)
        {
            shapes.Add(new FramingShape
            {
                Selector = FramingShapeSelector.HeadAccessories.Instance,
                Filter = FramingShapeFilter.AboveLowerYOfPrimaryHead.Instance,
                Padding = cfg.HairAbovePadding,
            });
        }
        return new CameraFraming.MeshAware(
            shapes,
            FrameTopFraction: cfg.HeadTopFraction,
            FrameBottomFraction: cfg.HeadBottomFraction,
            Yaw: cfg.Yaw,
            Pitch: cfg.Pitch);
    }

    /// <summary>
    /// Manual camera state is OrbitCamera-native (Distance, Azimuth, Elevation, Target).
    /// CameraFraming.OrbitState takes the same shape verbatim — no lossy eye-to-orbit
    /// conversion — so the saved PNG matches the live preview's framing bit-for-bit.
    /// </summary>
    private static CameraFraming BuildOrbitStateFromManual(InternalMugshotSettings cfg) =>
        new CameraFraming.OrbitState(
            Distance: cfg.ManualDistance,
            Azimuth: cfg.ManualAzimuth,
            Elevation: cfg.ManualElevation,
            TargetX: cfg.ManualTargetX,
            TargetY: cfg.ManualTargetY,
            TargetZ: cfg.ManualTargetZ);
}
