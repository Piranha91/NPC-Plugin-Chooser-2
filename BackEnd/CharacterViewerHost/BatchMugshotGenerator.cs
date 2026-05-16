using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mutagen.Bethesda.Plugins;
using Newtonsoft.Json;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using SixLabors.ImageSharp;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

public enum GenerationSource
{
    None,
    FaceFinderCache,
    FaceFinderDownload,
    /// <summary>
    /// FaceFinder confirmed the API has a mugshot for this NPC, but the
    /// caller asked us not to download the bytes — used by the batch flow
    /// when <see cref="Settings.CacheFaceFinderImages"/> is off, so the
    /// roundtrip-per-NPC cost still buys availability info (skipping local
    /// generation for NPCs FaceFinder will serve at view time) without
    /// burning bandwidth on bytes nobody persists or displays.
    /// </summary>
    FaceFinderAvailable,
    InternalRenderer,
    LegacyRenderer,
}

/// <summary>
/// Outcome of a single-NPC mugshot generation attempt.
/// <para><see cref="Generated"/> = a new file was written this call.
/// <see cref="AlreadyCurrent"/> = an up-to-date file already existed and was
/// reused. Either of those two means the NPC has a mugshot at
/// <see cref="OutputPath"/>; <see cref="Source"/> tells the caller which
/// pipeline produced it. <see cref="Source"/> = <see cref="GenerationSource.None"/>
/// means nothing was produced (caller should try the next pipeline or skip).</para>
/// </summary>
public sealed record GenerationResult(
    bool Generated,
    bool AlreadyCurrent,
    string? OutputPath,
    GenerationSource Source,
    IReadOnlyList<string> MissingMeshes,
    IReadOnlyList<string> MissingTextures,
    string? FaceFinderExternalUrl,
    byte[]? InMemoryImageBytes)
{
    public static readonly GenerationResult None = new(
        Generated: false,
        AlreadyCurrent: false,
        OutputPath: null,
        Source: GenerationSource.None,
        MissingMeshes: Array.Empty<string>(),
        MissingTextures: Array.Empty<string>(),
        FaceFinderExternalUrl: null,
        InMemoryImageBytes: null);

    public bool ProducedFile => OutputPath != null && (Generated || AlreadyCurrent);
    public bool ProducedAnything => ProducedFile || InMemoryImageBytes != null;
}

/// <summary>
/// File-producing core of the per-NPC mugshot pipeline. Owns the pipeline
/// branching (FaceFinder cache → FaceFinder API → Internal renderer → Legacy
/// renderer) but does NOT touch any UI state. Two callers consume it:
///
/// <list type="bullet">
/// <item><see cref="VM_NpcsMenuMugshot"/> wraps each call with the UI side-effects
/// (setting the tile's image, updating the selection-bar cache, applying the
/// missing-asset overlay, adding the FaceFinder external URL chip).</item>
/// <item>The Settings → "Generate All Mugshots" batch flow drives it directly
/// across every NPC in every mod.</item>
/// </list>
///
/// Splitting the file-producing work out of the tile VM ensures the batch flow
/// uses exactly the pipeline the user has configured (Internal vs Legacy via
/// <see cref="MugshotRenderer"/>; FaceFinder optional) and stays in sync if
/// the per-NPC path evolves.
/// </summary>
public sealed class BatchMugshotGenerator
{
    private readonly Settings _settings;
    private readonly InternalMugshotGenerator _internalGenerator;
    private readonly PortraitCreator _portraitCreator;
    private readonly FaceFinderClient _faceFinderClient;
    private readonly MugshotStalenessChecker _stalenessChecker;
    private readonly GeneratedMugshotTracker _autoGenTracker;
    private readonly FaceFinderCacheTracker _faceFinderTracker;
    private readonly EventLogger _eventLogger;

    public BatchMugshotGenerator(
        Settings settings,
        InternalMugshotGenerator internalGenerator,
        PortraitCreator portraitCreator,
        FaceFinderClient faceFinderClient,
        MugshotStalenessChecker stalenessChecker,
        GeneratedMugshotTracker autoGenTracker,
        FaceFinderCacheTracker faceFinderTracker,
        EventLogger eventLogger)
    {
        _settings = settings;
        _internalGenerator = internalGenerator;
        _portraitCreator = portraitCreator;
        _faceFinderClient = faceFinderClient;
        _stalenessChecker = stalenessChecker;
        _autoGenTracker = autoGenTracker;
        _faceFinderTracker = faceFinderTracker;
        _eventLogger = eventLogger;
    }

    /// <summary>
    /// Computes the per-mod mugshot folder root (one level above the
    /// FormKey-named PNG). Used by the per-NPC VM to register the folder on
    /// the associated mod's MugShotFolderPaths after a successful render.
    /// </summary>
    public static string GetAutoGenModFolder(Settings settings, string modName)
    {
        return Path.Combine(Settings.GetEffectiveAutogenMugshotsFolder(settings), modName);
    }

    public static string GetAutoGenSavePath(Settings settings, string modName, FormKey npcFormKey)
    {
        return Path.Combine(
            GetAutoGenModFolder(settings, modName),
            npcFormKey.ModKey.ToString(),
            $"{npcFormKey.ID:X8}.png");
    }

    /// <summary>
    /// Per-mod FaceFinder cache root (one level above the FormKey-named
    /// PNG/WEBP). Used by the per-NPC VM to register the folder on the
    /// associated mod's MugShotFolderPaths after a successful download or
    /// cache hit, so the file is discoverable by the mugshot lookup on the
    /// next NPC switch and on subsequent app launches.
    /// </summary>
    public static string GetFaceFinderModFolder(Settings settings, string modName)
    {
        return Path.Combine(Settings.GetEffectiveFaceFinderMugshotsFolder(settings), modName);
    }

    private static string GetFaceFinderBaseSavePath(Settings settings, string modName, FormKey npcFormKey)
    {
        return Path.Combine(
            GetFaceFinderModFolder(settings, modName),
            npcFormKey.ModKey.ToString(),
            $"{npcFormKey.ID:X8}");
    }

    /// <summary>
    /// Mirrors phase 1 of <c>VM_NpcsMenuMugshot.GenerateMugshotAsync</c>:
    /// uses the local FaceFinder cache when current, otherwise queries the
    /// FaceFinder API and (optionally) downloads a fresh image.
    ///
    /// <para>When <paramref name="downloadBytesIfHit"/> is true (the per-NPC
    /// tile path): on an API hit, the bytes are fetched and either persisted
    /// to disk (when <see cref="Settings.CacheFaceFinderImages"/> is on) or
    /// returned via <see cref="GenerationResult.InMemoryImageBytes"/> for
    /// session-only display.</para>
    ///
    /// <para>When <paramref name="downloadBytesIfHit"/> is false (the batch
    /// flow with caching off): the API roundtrip still happens — its answer
    /// is the whole point — but the image download is skipped. The result
    /// reports <see cref="GenerationSource.FaceFinderAvailable"/> so the
    /// batch can count the NPC as "FaceFinder will serve this at view time"
    /// and skip the local renderer for it.</para>
    /// </summary>
    /// <summary>Cheap check used by tile VMs on construction: returns true
    /// (and the path) when a FaceFinder-cached image exists for this NPC+mod
    /// AND its sidecar metadata is parseable. Sidecar-less caches are
    /// treated as fresh — same policy as <see cref="FaceFinderClient.IsCacheStaleAsync"/>'s
    /// "no metadata = preserve" branch (manually-downloaded files). No HTTP
    /// call. Used to skip the priority loop's HTTP staleness roundtrip when
    /// the user revisits an NPC whose FaceFinder image is already cached.
    /// </summary>
    public bool TryGetExistingFreshFaceFinderPath(
        FormKey npcFormKey, string modName, out string? path)
    {
        path = null;
        if (!_settings.UseFaceFinderFallback) return false;
        if (!_settings.CacheFaceFinderImages) return false;

        var baseSavePath = GetFaceFinderBaseSavePath(_settings, modName, npcFormKey);
        var existing = Auxilliary.FindExistingCachedImage(baseSavePath);
        if (existing == null) return false;

        var metadataPath = existing + FaceFinderClient.MetadataFileExtension;
        if (!File.Exists(metadataPath))
        {
            path = existing;
            return true;
        }

        try
        {
            var json = File.ReadAllText(metadataPath);
            var metadata = JsonConvert.DeserializeObject<FaceFinderClient.FaceFinderMetadata>(json);
            if (metadata != null && metadata.Source == "FaceFinder")
            {
                path = existing;
                return true;
            }
        }
        catch
        {
            // Corrupt sidecar — fall through and let the priority loop's
            // API path re-validate.
        }
        return false;
    }

    public async Task<GenerationResult> TryFaceFinderAsync(
        FormKey npcFormKey, string modName, CancellationToken token,
        bool downloadBytesIfHit = true)
    {
        if (!_settings.UseFaceFinderFallback) return GenerationResult.None;

        try
        {
            var baseSavePath = GetFaceFinderBaseSavePath(_settings, modName, npcFormKey);

            // Cache check first — staleness is decided per-file via API
            // metadata sidecar so we don't spend an HTTP roundtrip on
            // already-current images.
            string? existingCachedFile = Auxilliary.FindExistingCachedImage(baseSavePath);
            string? metaExternalUrl = null;
            if (_settings.CacheFaceFinderImages && existingCachedFile != null)
            {
                var metadata = await _faceFinderClient.ReadMetadataAsync(existingCachedFile);
                metaExternalUrl = metadata?.ExternalUrl;

                bool isStale = await _faceFinderClient.IsCacheStaleAsync(
                    existingCachedFile, npcFormKey, modName);
                if (!isStale)
                {
                    return new GenerationResult(
                        Generated: false,
                        AlreadyCurrent: true,
                        OutputPath: existingCachedFile,
                        Source: GenerationSource.FaceFinderCache,
                        MissingMeshes: Array.Empty<string>(),
                        MissingTextures: Array.Empty<string>(),
                        FaceFinderExternalUrl: metaExternalUrl,
                        InMemoryImageBytes: null);
                }
            }

            // No fresh cache — query the API and (if requested) write to disk.
            var faceData = await _faceFinderClient.GetFaceDataAsync(npcFormKey, modName);
            if (faceData == null || string.IsNullOrWhiteSpace(faceData.ImageUrl))
            {
                return GenerationResult.None;
            }

            // Availability-only short-circuit: caller wants the API's answer
            // (so it can skip local generation for NPCs FaceFinder will serve
            // at view time) but doesn't want us to download the bytes — used
            // by the batch when CacheFaceFinderImages is off, where there's
            // nothing to persist and nothing to display this session.
            if (!downloadBytesIfHit)
            {
                return new GenerationResult(
                    Generated: false,
                    AlreadyCurrent: false,
                    OutputPath: null,
                    Source: GenerationSource.FaceFinderAvailable,
                    MissingMeshes: Array.Empty<string>(),
                    MissingTextures: Array.Empty<string>(),
                    FaceFinderExternalUrl: faceData.ExternalUrl,
                    InMemoryImageBytes: null);
            }

            using var client = new HttpClient();
            var imageData = await client.GetByteArrayAsync(faceData.ImageUrl, token);

            string? finalSavePath = null;
            if (_settings.CacheFaceFinderImages)
            {
                var format = Image.DetectFormat(imageData);
                var extension = format?.FileExtensions.FirstOrDefault() ?? "png";
                finalSavePath = $"{baseSavePath}.{extension}";

                Directory.CreateDirectory(Path.GetDirectoryName(finalSavePath)!);
                try
                {
                    await File.WriteAllBytesAsync(finalSavePath, imageData, token);
                    await _faceFinderClient.WriteMetadataAsync(finalSavePath, faceData);
                    _faceFinderTracker.Track(finalSavePath);
                }
                catch
                {
                    _faceFinderTracker.TrackIfFileExists(finalSavePath);
                    throw;
                }
            }

            return new GenerationResult(
                Generated: finalSavePath != null,
                AlreadyCurrent: false,
                OutputPath: finalSavePath,
                Source: GenerationSource.FaceFinderDownload,
                MissingMeshes: Array.Empty<string>(),
                MissingTextures: Array.Empty<string>(),
                FaceFinderExternalUrl: faceData.ExternalUrl,
                // Surface the bytes only when we didn't persist — caller (the
                // per-NPC tile) can paint from memory; the batch flow ignores
                // this since it only invokes us when caching is on.
                InMemoryImageBytes: finalSavePath == null ? imageData : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"BatchMugshotGenerator FaceFinder failed for {npcFormKey} ({modName}): {ex.Message}");
            _eventLogger.Log($"BATCH_GEN FaceFinder error for {npcFormKey}: {ex.Message}", "BATCH_GEN_ERROR");
            return GenerationResult.None;
        }
    }

    /// <summary>Variant of <see cref="TryFaceFinderAsync"/> for the
    /// mod-batch path: the caller has already fetched the server-side face
    /// metadata via <see cref="FaceFinderClient.GetAllFacesForModAsync"/>,
    /// so we skip the per-NPC API probe and compare the supplied
    /// <paramref name="serverUpdatedAt"/> directly against the cached
    /// sidecar's timestamp. Same return-shape contract as TryFaceFinderAsync
    /// so the batch loop's outcome counters stay symmetric (FaceFinderCache /
    /// FaceFinderAvailable / FaceFinderDownload).</summary>
    public async Task<GenerationResult> ProcessKnownFaceAsync(
        FormKey npcFormKey,
        string modName,
        string imageUrl,
        DateTime serverUpdatedAt,
        string? externalUrl,
        bool downloadBytesIfHit,
        CancellationToken token)
    {
        if (!_settings.UseFaceFinderFallback) return GenerationResult.None;

        try
        {
            var baseSavePath = GetFaceFinderBaseSavePath(_settings, modName, npcFormKey);

            // Cache check — direct timestamp compare against the metadata
            // sidecar. Skips the IsCacheStaleAsync HTTP roundtrip since the
            // caller already has the authoritative server timestamp.
            string? existingCachedFile = Auxilliary.FindExistingCachedImage(baseSavePath);
            if (_settings.CacheFaceFinderImages && existingCachedFile != null)
            {
                var metadata = await _faceFinderClient.ReadMetadataAsync(existingCachedFile);
                if (metadata != null && metadata.UpdatedAt >= serverUpdatedAt)
                {
                    return new GenerationResult(
                        Generated: false,
                        AlreadyCurrent: true,
                        OutputPath: existingCachedFile,
                        Source: GenerationSource.FaceFinderCache,
                        MissingMeshes: Array.Empty<string>(),
                        MissingTextures: Array.Empty<string>(),
                        FaceFinderExternalUrl: metadata.ExternalUrl ?? externalUrl,
                        InMemoryImageBytes: null);
                }
            }

            // Availability-only short-circuit — same semantics as
            // TryFaceFinderAsync: caller asked us to not pay the download
            // cost. The batch's CacheFaceFinderImages=off branch uses this.
            if (!downloadBytesIfHit)
            {
                return new GenerationResult(
                    Generated: false,
                    AlreadyCurrent: false,
                    OutputPath: null,
                    Source: GenerationSource.FaceFinderAvailable,
                    MissingMeshes: Array.Empty<string>(),
                    MissingTextures: Array.Empty<string>(),
                    FaceFinderExternalUrl: externalUrl,
                    InMemoryImageBytes: null);
            }

            using var client = new HttpClient();
            var imageData = await client.GetByteArrayAsync(imageUrl, token);

            string? finalSavePath = null;
            if (_settings.CacheFaceFinderImages)
            {
                var format = Image.DetectFormat(imageData);
                var extension = format?.FileExtensions.FirstOrDefault() ?? "png";
                finalSavePath = $"{baseSavePath}.{extension}";

                Directory.CreateDirectory(Path.GetDirectoryName(finalSavePath)!);
                try
                {
                    await File.WriteAllBytesAsync(finalSavePath, imageData, token);
                    await _faceFinderClient.WriteMetadataAsync(finalSavePath, new FaceFinderResult
                    {
                        ImageUrl = imageUrl,
                        UpdatedAt = serverUpdatedAt,
                        ExternalUrl = externalUrl,
                    });
                    _faceFinderTracker.Track(finalSavePath);
                }
                catch
                {
                    _faceFinderTracker.TrackIfFileExists(finalSavePath);
                    throw;
                }
            }

            return new GenerationResult(
                Generated: finalSavePath != null,
                AlreadyCurrent: false,
                OutputPath: finalSavePath,
                Source: GenerationSource.FaceFinderDownload,
                MissingMeshes: Array.Empty<string>(),
                MissingTextures: Array.Empty<string>(),
                FaceFinderExternalUrl: externalUrl,
                InMemoryImageBytes: finalSavePath == null ? imageData : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"BatchMugshotGenerator ProcessKnownFace failed for {npcFormKey} ({modName}): {ex.Message}");
            _eventLogger.Log($"BATCH_GEN ProcessKnownFace error for {npcFormKey}: {ex.Message}", "BATCH_GEN_ERROR");
            return GenerationResult.None;
        }
    }

    /// <summary>
    /// Mirrors phase 2 of <c>VM_NpcsMenuMugshot.GenerateMugshotAsync</c>:
    /// dispatches to the user-selected renderer (Internal vs
    /// LegacyPortraitCreator) gated by <see cref="MugshotStalenessChecker.NeedsRegeneration"/>
    /// so up-to-date PNGs are reused. The Internal path needs the persisted
    /// <see cref="ModSetting"/> for asset-resolution scoping; the Legacy path
    /// needs the live <see cref="VM_ModSetting"/> for the FaceGen NIF lookup
    /// (which walks <c>CorrespondingFolderPaths</c>). Both are looked up by
    /// display name internally so callers only have to supply the VM.
    /// </summary>
    /// <summary>Cheap check used by tile VMs on construction: returns true
    /// (and the path) when a previously-generated autogen PNG exists at the
    /// canonical save path AND the staleness checker classifies it fresh.
    /// Lets the tile skip the priority loop on NPC revisit. The staleness
    /// check only reads PNG metadata + JSON parse — no rendering, no NIF
    /// lookup, no network. Safe for both Internal and Legacy renderers:
    /// Legacy's NIF-path input to <see cref="MugshotStalenessChecker.NeedsRegeneration"/>
    /// is only used for an SHA compare against stamped metadata, not for a
    /// fresh FaceGen lookup, so passing null still catches every drift case
    /// (renderer/resolution/version/settings hash).</summary>
    public bool TryGetExistingFreshAutoGenPath(
        FormKey npcFormKey, VM_ModSetting modSetting, out string? path)
    {
        path = null;
        if (!_settings.UsePortraitCreatorFallback) return false;
        if (modSetting == null) return false;

        var savePath = GetAutoGenSavePath(_settings, modSetting.DisplayName, npcFormKey);
        if (!File.Exists(savePath)) return false;

        if (_stalenessChecker.NeedsRegeneration(savePath, npcFormKey)) return false;

        path = savePath;
        return true;
    }

    public async Task<GenerationResult> RunSelectedRendererAsync(
        FormKey npcFormKey,
        VM_ModSetting modSetting,
        CancellationToken token,
        bool assetValidatedOnly = false)
    {
        if (!_settings.UsePortraitCreatorFallback) return GenerationResult.None;

        try
        {
            var savePath = GetAutoGenSavePath(_settings, modSetting.DisplayName, npcFormKey);

            if (_settings.SelectedRenderer == MugshotRenderer.Internal)
            {
                var sourceMod = _settings.ModSettings.FirstOrDefault(
                    m => m.DisplayName == modSetting.DisplayName);
                if (!_stalenessChecker.NeedsRegeneration(savePath, npcFormKey))
                {
                    // Surface the previously-stamped missing-asset arrays so the
                    // tile's overlay survives revisits. Post-2.1.7 the autogen
                    // folder is no longer in MugShotFolderPaths, so ImagePath is
                    // empty on a fresh VM and LoadInitialImageAsync's metadata
                    // read doesn't fire for autogen-only tiles. The caller
                    // applies these arrays on ProducedFile, matching pre-2.1.7
                    // behavior where the overlay state was loaded up front.
                    var json = MugshotPngMetadata.TryRead(savePath);
                    List<string> existingMeshes = new();
                    List<string> existingTextures = new();
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        InternalMugshotMetadata.TryReadMissingAssets(
                            json, out existingMeshes, out existingTextures);
                    }
                    return new GenerationResult(
                        Generated: false, AlreadyCurrent: true, OutputPath: savePath,
                        Source: GenerationSource.InternalRenderer,
                        MissingMeshes: existingMeshes,
                        MissingTextures: existingTextures,
                        FaceFinderExternalUrl: null,
                        InMemoryImageBytes: null);
                }

                var missingMeshes = new List<string>();
                var missingTextures = new List<string>();
                bool generated = await _internalGenerator.GenerateAsync(
                    npcFormKey, sourceMod, savePath, token, missingMeshes, missingTextures,
                    assetValidatedOnly);
                return new GenerationResult(
                    Generated: generated,
                    AlreadyCurrent: false,
                    OutputPath: generated ? savePath : null,
                    Source: generated ? GenerationSource.InternalRenderer : GenerationSource.None,
                    MissingMeshes: missingMeshes,
                    MissingTextures: missingTextures,
                    FaceFinderExternalUrl: null,
                    InMemoryImageBytes: null);
            }
            else
            {
                // Legacy path requires a local mod for the FaceGen NIF lookup —
                // mirrors the per-tile early-return when AssociatedModSetting
                // has no folders.
                if (modSetting.CorrespondingFolderPaths == null ||
                    modSetting.CorrespondingFolderPaths.Count == 0)
                {
                    return GenerationResult.None;
                }

                string nifPath = await _portraitCreator.FindNpcNifPath(npcFormKey, modSetting);
                if (string.IsNullOrWhiteSpace(nifPath)) return GenerationResult.None;

                bool autoGenerated = modSetting.DisplayName == VM_Mods.BaseGameModSettingName ||
                                     modSetting.DisplayName == VM_Mods.CreationClubModsettingName;
                if (!_stalenessChecker.NeedsRegeneration(
                        savePath, npcFormKey, nifPath,
                        modSetting.CorrespondingFolderPaths, autoGenerated))
                {
                    return new GenerationResult(
                        Generated: false, AlreadyCurrent: true, OutputPath: savePath,
                        Source: GenerationSource.LegacyRenderer,
                        MissingMeshes: Array.Empty<string>(),
                        MissingTextures: Array.Empty<string>(),
                        FaceFinderExternalUrl: null,
                        InMemoryImageBytes: null);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                bool generated = await _portraitCreator.GeneratePortraitAsync(
                    nifPath, modSetting.CorrespondingFolderPaths, savePath, token);
                return new GenerationResult(
                    Generated: generated,
                    AlreadyCurrent: false,
                    OutputPath: generated ? savePath : null,
                    Source: generated ? GenerationSource.LegacyRenderer : GenerationSource.None,
                    MissingMeshes: Array.Empty<string>(),
                    MissingTextures: Array.Empty<string>(),
                    FaceFinderExternalUrl: null,
                    InMemoryImageBytes: null);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"BatchMugshotGenerator renderer failed for {npcFormKey} ({modSetting.DisplayName}): {ex.Message}");
            _eventLogger.Log($"BATCH_GEN renderer error for {npcFormKey}: {ex.Message}", "BATCH_GEN_ERROR");
            return GenerationResult.None;
        }
    }
}
