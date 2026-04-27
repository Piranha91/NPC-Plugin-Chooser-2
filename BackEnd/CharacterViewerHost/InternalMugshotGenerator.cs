using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CharacterViewer.Rendering;
using CharacterViewer.Rendering.Offscreen;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;

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

    public InternalMugshotGenerator(
        NpcMeshResolver resolver,
        IOffscreenRenderer renderer,
        Settings settings,
        ICharacterViewerSettings viewerSettings,
        EnvironmentStateProvider env,
        IBsaArchiveProvider bsa)
    {
        _resolver = resolver;
        _renderer = renderer;
        _settings = settings;
        _viewerSettings = viewerSettings;
        _env = env;
        _bsa = bsa;
    }

    /// <summary>
    /// Renders the NPC at <paramref name="npcFormKey"/> to a PNG at
    /// <paramref name="outputPath"/>. <paramref name="modSetting"/> scopes
    /// the render to a specific mod — for mugshot tiles each tile passes
    /// its own source mod so tiles for the same NPC produced by different
    /// mods render distinct appearances. Returns false if mesh resolution
    /// fails or the renderer throws; the caller can fall back to FaceFinder
    /// / Legacy.
    /// </summary>
    public async Task<bool> GenerateAsync(FormKey npcFormKey, ModSetting? modSetting, string outputPath, CancellationToken token = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int tid = Environment.CurrentManagedThreadId;
        Trace($"ENTER tid={tid} npc={npcFormKey} mod=[{modSetting?.DisplayName ?? "(none)"}]");
        try
        {
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

            var cfg = _settings.InternalMugshot;
            var request = new OffscreenRenderRequest
            {
                MeshPaths = paths,
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
                // Each mugshot tile is generated once per (NPC, mod) and
                // never re-rendered — keeping extracted source NIFs / DDS
                // around in %TEMP%\SynthEBD_ViewerCache between renders
                // would just balloon disk usage across a session.
                ClearExtractionCacheAfterRender = true,
            };

            long preRender = sw.ElapsedMilliseconds;
            byte[] png = await _renderer.RenderToPngAsync(request).ConfigureAwait(false);
            Trace($"  RenderToPngAsync tid={Environment.CurrentManagedThreadId} bytes={png.Length} elapsed={sw.ElapsedMilliseconds - preRender}ms");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, png, token).ConfigureAwait(false);

            // Stamp the "Parameters" tEXt chunk so the staleness checker can
            // detect cross-renderer switches and version drift, and so
            // IsAutoGenerated returns true for these PNGs.
            try
            {
                var parametersJson = InternalMugshotMetadata.Build(npcFormKey, _settings.InternalMugshot);
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
            // know this PNG was auto-generated.
            _settings.GeneratedMugshotPaths.Add(outputPath);
            Trace($"EXIT tid={Environment.CurrentManagedThreadId} npc={npcFormKey} ok totalElapsed={sw.ElapsedMilliseconds}ms");
            return true;
        }
        catch (OperationCanceledException)
        {
            Trace($"EXIT tid={Environment.CurrentManagedThreadId} npc={npcFormKey} cancelled totalElapsed={sw.ElapsedMilliseconds}ms");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"InternalMugshotGenerator failed for {npcFormKey}: {ExceptionLogger.GetExceptionStack(ex)}");
            Trace($"EXIT tid={Environment.CurrentManagedThreadId} npc={npcFormKey} ERROR totalElapsed={sw.ElapsedMilliseconds}ms err={ex.Message}");
            return false;
        }
    }

    private static void Trace(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[InternalMugshotGenerator] {message}");
        System.Diagnostics.Trace.WriteLine($"[InternalMugshotGenerator] {message}");
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
