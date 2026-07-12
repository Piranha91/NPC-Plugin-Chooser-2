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
    // render semaphore (and the comment in GenerateAsync's diagnostics).
    private readonly SemaphoreSlim _renderSemaphore;

    public InternalMugshotGenerator(
        NpcMeshResolver resolver,
        IOffscreenRenderer renderer,
        Settings settings,
        ICharacterViewerSettings viewerSettings,
        EnvironmentStateProvider env,
        IBsaArchiveProvider bsa,
        GeneratedMugshotTracker tracker,
        CharacterViewerLogGate logGate,
        Lazy<VM_NpcSelectionBar> npcSelectionBar,
        FaceGenConsistencyAnalyzer faceGenConsistency)
    {
        _resolver = resolver;
        _renderer = renderer;
        _settings = settings;
        _viewerSettings = viewerSettings;
        _env = env;
        _bsa = bsa;
        _tracker = tracker;
        _logGate = logGate;
        _npcSelectionBar = npcSelectionBar;
        _faceGenConsistency = faceGenConsistency;

        // Math.Max(1, ...) guards a misconfigured 0/negative setting from
        // deadlocking every render. Captured once at construction (matching
        // PortraitCreator); a runtime change takes effect on next launch.
        int maxParallel = Math.Max(1, _settings.MaxParallelPortraitRenders);
        _renderSemaphore = new SemaphoreSlim(maxParallel, maxParallel);

        // On an environment rebuild (game path / load order change) the renderer's
        // cached resolved paths, decoded pixels, and uploaded GL textures may be
        // stale — drop them so subsequent renders re-resolve against the new
        // environment. Singleton, so the subscription lives for the app's life.
        _env.OnEnvironmentUpdated.Subscribe(_ => _renderer.InvalidateCaches());
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
        FormKey? targetNpcFormKey = null)
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
        Trace($"ENTER tid={tid} npc={npcFormKey} mod=[{modSetting?.DisplayName ?? "(none)"}]");
        bool acquired = false;
        try
        {
            // Acquire before any resolve/extraction work so the in-flight
            // backlog (not just the GL render) stays bounded. WaitAsync throws
            // OCE if cancelled before a slot frees — caught below, returns
            // false, and the finally skips Release since acquired is still false.
            long preAcquire = sw.ElapsedMilliseconds;
            await _renderSemaphore.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            Trace($"  acquired-slot tid={Environment.CurrentManagedThreadId} waited={sw.ElapsedMilliseconds - preAcquire}ms");

            var linkCache = _env.LinkCache;
            if (linkCache == null) { Trace($"EXIT tid={tid} npc={npcFormKey} no-linkcache elapsed={sw.ElapsedMilliseconds}ms"); return false; }

            // Resolve scoped to the explicit per-tile mod. Records come from
            // its plugins (LinkCache.Winner fallback for anything the mod
            // doesn't override) and asset paths get rebased to mod folders
            // when present.
            var paths = _resolver.Resolve(npcFormKey, modSetting);
            Trace($"  resolve tid={tid} npc={npcFormKey} ok={paths != null} elapsed={sw.ElapsedMilliseconds}ms");
            if (paths == null) { Trace($"EXIT tid={tid} npc={npcFormKey} resolve-null elapsed={sw.ElapsedMilliseconds}ms"); return false; }

            // The renderer's asset resolver will need to pull body/skeleton/textures
            // from BSAs as well as loose files; pre-warm the BSA index so the first
            // render doesn't pay the open-all-archives cost. Run on a thread pool
            // thread — opening readers for hundreds of mods can take several seconds
            // and would otherwise block the UI thread that triggered the call.
            long preEnsure = sw.ElapsedMilliseconds;
            await Task.Run(() => _bsa.EnsureAllArchivesOpened(), token).ConfigureAwait(false);
            Trace($"  EnsureAllArchivesOpened tid={Environment.CurrentManagedThreadId} elapsed={sw.ElapsedMilliseconds - preEnsure}ms");

            // Templated NPCs ship no exported FaceGen NIF; rendering them would
            // produce a headless body since every other mesh (body / hands /
            // skin) resolves cleanly off the race/armature. Abort here so the
            // caller leaves the placeholder up instead of writing a misleading
            // PNG. Mirrors the Legacy path's FindNpcNifPath null-check in
            // BatchMugshotGenerator.RunSelectedRendererAsync.
            if (!_resolver.FaceGenExists(npcFormKey, modSetting))
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
            var meshOverrides = _resolver.ResolveAttireMeshOverrides(
                npcFormKey, modSetting, effectiveIncludeDefaultOutfit, effectiveIncludeHeadgear,
                targetNpcFormKey, out var outfitDisplay);
            var meshOverrideWarningsOut = new List<string>();

            // Opt-in per-render phase timing (drop a LogRenderTimings.txt file
            // next to the exe). Pure data, no logging — so timings are
            // representative with the verbose render trace OFF.
            var renderTimings = RenderTimingsEnabled ? new RenderTimings() : null;

            var request = new OffscreenRenderRequest
            {
                MeshPaths = paths,
                MeshOverrides = meshOverrides.Count > 0 ? meshOverrides : null,
                MeshOverrideWarningsOut = meshOverrideWarningsOut,
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
                AdditionalScopes = _resolver.BuildResolutionScopes(modSetting),
                Cancellation = token,
                MissingMeshPathsOut = missingMeshPathsOut,
                MissingTexturePathsOut = missingTexturePathsOut,
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
            if (renderTimings != null)
                WriteRenderTimingsCsv(npcFormKey, modSetting, renderTimings, hostRenderMs, png.Length);

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
                Trace($"EXIT tid={Environment.CurrentManagedThreadId} npc={npcFormKey} skipped-missing-assets meshes={missingMeshCount} textures={missingTextureCount} totalElapsed={sw.ElapsedMilliseconds}ms");
                return false;
            }

            // Attire/headgear override warnings are surfaced for display only,
            // AFTER the validated-only gate above — a helmet that needs an absent
            // skeleton shouldn't discard an otherwise-complete face mugshot. Fold
            // them into the persisted missing-mesh list so the tile's existing
            // missing-asset overlay (and the cached metadata) shows them.
            if (meshOverrideWarningsOut.Count > 0)
            {
                Trace($"  meshOverrideWarnings tid={Environment.CurrentManagedThreadId} count={meshOverrideWarningsOut.Count}");
                missingMeshPathsOut?.AddRange(meshOverrideWarningsOut);
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
                    var (npcForCheck, resolveHeadPart, resolveRace) = _resolver.ResolveNpcForConsistency(npcFormKey, modSetting);
                    if (npcForCheck != null)
                    {
                        var analysis = _faceGenConsistency.Analyze(npcForCheck, resolveHeadPart, resolveRace, headNif);
                        if (analysis.HasMismatch)
                        {
                            faceGenMismatch = analysis.BuildReason();
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
                var parametersJson = InternalMugshotMetadata.Build(
                    npcFormKey, _settings.InternalMugshot,
                    effectiveIncludeDefaultOutfit, effectiveIncludeHeadgear,
                    outfitDisplay.IdentityStamp,
                    missingMeshPathsOut, missingTexturePathsOut, faceGenMismatch);
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
        }
    }

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
    private static readonly bool RenderTimingsEnabled =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "LogRenderTimings.txt"));
    private static readonly object _timingsCsvLock = new();
    private static readonly string _timingsCsvPath =
        Path.Combine(AppContext.BaseDirectory, "RenderLogs", "RenderTimings.csv");

    private void WriteRenderTimingsCsv(
        FormKey npcFormKey, ModSetting? modSetting, RenderTimings t, long hostRenderMs, int bytes)
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
                t.TotalMs.ToString("F1", ci), hostRenderMs.ToString(ci), bytes.ToString(ci));

            lock (_timingsCsvLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_timingsCsvPath)!);
                if (!File.Exists(_timingsCsvPath))
                {
                    File.AppendAllText(_timingsCsvPath,
                        "mod,npcFormKey,npcName,setupMs,buildMs,resolveMs,parseMs,loadMs,buildShapesMs,installMs,decodeMs,uploadMs,drawMs,readbackMs,encodeMs,rendererTotalMs,hostRenderMs,pngBytes\n");
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
