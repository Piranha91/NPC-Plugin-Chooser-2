using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CharacterViewer.Rendering;
using Mutagen.Bethesda.Plugins;
using Newtonsoft.Json.Linq;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Builds the "Parameters" tEXt JSON the Internal renderer stamps on every
/// mugshot it produces. The schema's job is to support staleness detection
/// in <see cref="MugshotStalenessChecker"/> — it's not a general-purpose
/// reproducibility manifest. Three top-level fields drive staleness:
///
/// <list type="bullet">
/// <item><c>renderer</c>: <c>"Internal"</c> — distinguishes from legacy PNGs
/// (which lack this field) so the checker can detect cross-renderer
/// switches and force regen.</item>
/// <item><c>renderer_version</c>: <see cref="CharacterViewerRendering.Version"/>
/// at write time — checked against the bundled DLL's version on each load
/// (gated on <c>AutoUpdateOldMugshots</c>).</item>
/// <item><c>settings_hash</c>: SHA256 over every InternalMugshotSettings
/// field that influences rendering — checked on each load (gated on
/// <c>AutoUpdateStaleMugshots</c>).</item>
/// </list>
///
/// The remaining fields (npc_form_key, individual setting values) are stored
/// for diagnostics only — comparing the hash is cheaper than comparing each
/// field, and the hash captures combinations the legacy renderer didn't have
/// (manual orbit state, framing fractions, etc.) without growing the check
/// path each time a new field is added.
/// </summary>
public static class InternalMugshotMetadata
{
    public const string RendererName = "Internal";

    /// <summary>Pipeline schema version stamped into each PNG's "Parameters"
    /// JSON. Bumped whenever a new pixel-affecting toggle is added. The
    /// staleness checker reads the stamped value and compares the PNG's
    /// hash against the corresponding-version hash computed from current
    /// settings, so a PNG stamped at v0 isn't invalidated by the addition
    /// of v1 toggles — its hash only included the v0 fields, and the
    /// current-cfg-at-v0 hash will match it as long as none of the v0
    /// fields changed. Stamped PNGs at older schemas keep their look
    /// across upgrades; user can manually regen to upgrade them.
    /// <para>History:
    /// <list type="bullet">
    /// <item>0 (absent <c>pipeline_schema</c> field): pre-2.5.9 PNGs.</item>
    /// <item>1: 2.5.9 added <c>EnableToneMapping</c>.</item>
    /// <item>2: 2.5.10 added <c>EnableShadows</c>.</item>
    /// <item>3: 2.5.11 added <c>EnableAmbientOcclusion</c>.</item>
    /// <item>4: 2.5.12 added <c>SsaoRadius</c>, <c>SsaoBias</c>,
    /// <c>SsaoIntensity</c>.</item>
    /// <item>5: 2.5.13 added <c>EnableEyeCatchlight</c> + fresnel
    /// contour darkening (folded under <c>EnableToneMapping</c>, so
    /// no separate hash entry for fresnel).</item>
    /// <item>6: 2.5.14 corrected SSS math + added
    /// <c>SubsurfaceStrength</c> multiplier.</item>
    /// <item>7: 2.5.15 made the tone-mapping vignette tunable via
    /// <c>VignetteRadius</c> + <c>VignetteIntensity</c>. Both are
    /// included in the v7 hash so a user changing either flags the
    /// affected tiles stale, but the staleness checker continues to
    /// skip these fields when comparing pre-v7 PNGs (those were
    /// stamped under the hardcoded vignette).</item>
    /// <item>8: added <c>SkinSaturationBoost</c> — skin-only chroma
    /// multiplier applied post-tint, pre-lighting on shapes flagged as
    /// skin (BSLSP_FACE / BSLSP_SKINTINT). Default 1.0 is no-op, so v7
    /// tiles compared at schemaVersion=7 (no SkinSaturationBoost entry)
    /// continue to hash-match a v8 cfg whose user hasn't changed the
    /// new field.</item>
    /// <item>9: added <c>IncludeDefaultOutfit</c> + <c>IncludeHeadgear</c> —
    /// the character-preview attire toggles. Both default false (no extra
    /// meshes), so a v8 tile compared at schemaVersion=8 (which excludes these
    /// entries) keeps hash-matching a v9 cfg whose user hasn't enabled either.</item>
    /// <item>10: 2.5.19 added <c>Exposure</c> — the tone-map exposure
    /// multiplier. Default 1.0 is no-op (reproduces the pre-2.5.19 hardcoded
    /// 0.6 baseline), so a v9 tile compared at schemaVersion=9 (which excludes
    /// this entry) keeps hash-matching a v10 cfg whose user hasn't changed the
    /// field.</item>
    /// <item>12: added <c>effective_outfit</c> — the IDENTITY (FormKey string,
    /// or "none" when attire is off / the NPC has no outfit) of the outfit the
    /// render depicted, as resolved by OutfitDisplayResolver (patch-mode plugin
    /// level + SkyPatcher/SPID runtime layers). Identity only, deliberately
    /// excluding the provenance source: switching Create ↔ CreateAndPatch (or
    /// any other input change) that lands on the SAME outfit FormKey must not
    /// re-stale the tile. Pre-v12 PNGs lack the field and are compared at
    /// their stamped schema, so the feature's introduction regenerates
    /// nothing by itself.</item>
    /// <item>13: added <c>SsaoThickness</c> + <c>SsaoHairGap</c>. Thickness
    /// default 1.5 reproduces the prior hardcoded value; a v12 tile compared at
    /// schemaVersion=12 stays valid.</item>
    /// <item>14: added <c>HairAlbedoCompensate</c> — neutral-white-tint hair
    /// albedo compensation (the sRGB->linear the gamma-space pipeline skips for
    /// un-attenuated hair). Unlike prior additions the default (1.0) changes
    /// output for white-tint hair, but a v13 tile compared at schemaVersion=13
    /// excludes it and stays valid until regenerated.</item>
    /// <item>15: added the hair-shadow "brow ridge" mitigations
    /// <c>ExcludeHairShadowCaster</c> (A), <c>SoftenShadowEdges</c> +
    /// <c>ShadowPcfRadius</c> (B), <c>TightShadowFrustum</c> +
    /// <c>ShadowFrustumRadius</c> (C). B defaults ON and softens the cast
    /// shadow (de-warps the forehead ridge, relieves the neck-under-jaw
    /// darkening), so like v14 the default changes output for new tiles; a
    /// v14 tile compared at schemaVersion=14 excludes these and stays valid
    /// until regenerated.</item>
    /// </list>
    /// </para></summary>
    public const int PipelineSchemaVersion = 15;

    // JSON keys for the missing-asset arrays embedded in the "Parameters"
    // tEXt chunk. Kept as constants so the read path in
    // TryReadMissingAssets can match on the same names without drift.
    private const string MissingMeshesKey = "missing_meshes";
    private const string MissingTexturesKey = "missing_textures";
    // A FaceGen-vs-records mismatch reason (dark-face risk) stamped at render time so
    // the tile's existing missing-asset overlay can surface it after app restarts,
    // exactly like the missing-asset arrays. Like those, it's an output of the render
    // (NOT folded into the settings hash that drives staleness).
    private const string FaceGenMismatchKey = "facegen_mismatch";
    // Stale-physics-config notices (an attire mesh links an SMP/HDT physics XML
    // that doesn't exist — a broken link in the mod itself; the render is still
    // correct). Stamped so the tile can show its informational outfit-asset icon
    // across restarts. Deliberately NOT read by TryReadMissingAssets and NOT
    // consulted by MugshotStalenessChecker — this key must never make a cached
    // mugshot stale (nothing the user installs can clear it).
    private const string PhysicsConfigNoticesKey = "physics_config_notices";
    public const string PipelineSchemaKey = "pipeline_schema";
    /// <summary>JSON key of the depicted-outfit identity stamp (v12+).</summary>
    public const string EffectiveOutfitKey = "effective_outfit";
    /// <summary>Identity value stamped when no outfit is depicted (attire
    /// toggle off, or the NPC resolves to no outfit).</summary>
    public const string NoOutfitIdentity = "none";

    public static string Build(
        FormKey npcFormKey,
        InternalMugshotSettings cfg,
        bool effectiveIncludeDefaultOutfit,
        bool effectiveIncludeHeadgear,
        string effectiveOutfitIdentity,
        IReadOnlyList<string>? missingMeshes = null,
        IReadOnlyList<string>? missingTextures = null,
        string? faceGenMismatch = null,
        IReadOnlyList<string>? physicsConfigNotices = null)
    {
        var obj = new JObject
        {
            ["renderer"] = RendererName,
            ["renderer_version"] = CharacterViewerRendering.Version.ToString(),
            [PipelineSchemaKey] = PipelineSchemaVersion,
            ["npc_form_key"] = npcFormKey.ToString(),
            ["settings_hash"] = ComputeSettingsHashAtSchema(cfg, PipelineSchemaVersion,
                effectiveIncludeDefaultOutfit, effectiveIncludeHeadgear, effectiveOutfitIdentity),
            [EffectiveOutfitKey] = string.IsNullOrEmpty(effectiveOutfitIdentity) ? NoOutfitIdentity : effectiveOutfitIdentity,
            ["camera_mode"] = cfg.CameraMode.ToString(),
            ["background_color"] = new JArray(cfg.BackgroundR, cfg.BackgroundG, cfg.BackgroundB),
            ["output_size"] = new JArray(cfg.OutputWidth, cfg.OutputHeight),
            ["lighting_layout"] = cfg.LightingLayoutName ?? "",
            ["lighting_color_scheme"] = cfg.LightingColorSchemeName ?? "",
            ["head_top_fraction"] = cfg.HeadTopFraction,
            ["head_bottom_fraction"] = cfg.HeadBottomFraction,
            ["yaw"] = cfg.Yaw,
            ["pitch"] = cfg.Pitch,
            ["hair_above_padding"] = cfg.HairAbovePadding,
            ["include_accessories"] = cfg.IncludeAccessories,
            ["vanilla_loose_overrides_bsa"] = cfg.VanillaLooseOverridesBsa,
            ["vanilla_loose_overrides_mod_loose"] = cfg.VanillaLooseOverridesModLoose,
            ["render_missing_texture_as_wireframe"] = cfg.RenderMissingTextureAsWireframe,
            ["enable_tone_mapping"] = cfg.EnableToneMapping,
            ["enable_shadows"] = cfg.EnableShadows,
            ["exclude_hair_shadow_caster"] = cfg.ExcludeHairShadowCaster,
            ["soften_shadow_edges"] = cfg.SoftenShadowEdges,
            ["shadow_pcf_radius"] = cfg.ShadowPcfRadius,
            ["tight_shadow_frustum"] = cfg.TightShadowFrustum,
            ["shadow_frustum_radius"] = cfg.ShadowFrustumRadius,
            ["enable_ambient_occlusion"] = cfg.EnableAmbientOcclusion,
            ["ssao_radius"] = cfg.SsaoRadius,
            ["ssao_bias"] = cfg.SsaoBias,
            ["ssao_intensity"] = cfg.SsaoIntensity,
            ["enable_eye_catchlight"] = cfg.EnableEyeCatchlight,
            ["subsurface_strength"] = cfg.SubsurfaceStrength,
            ["vignette_radius"] = cfg.VignetteRadius,
            ["vignette_intensity"] = cfg.VignetteIntensity,
            ["skin_saturation_boost"] = cfg.SkinSaturationBoost,
            ["exposure"] = cfg.Exposure,
            ["tonemap_hair_relief"] = cfg.TonemapHairRelief,
            ["hair_albedo_compensate"] = cfg.HairAlbedoCompensate,
            ["daylight_boost"] = cfg.DaylightBoost,
            ["daylight_boost_intensity"] = cfg.DaylightBoostIntensity,
            ["enable_bloom"] = cfg.EnableBloom,
            ["bloom_intensity"] = cfg.BloomIntensity,
            ["include_default_outfit"] = effectiveIncludeDefaultOutfit,
            ["include_headgear"] = effectiveIncludeHeadgear,
        };

        if (cfg.CameraMode == InternalMugshotCameraMode.Manual)
        {
            obj["manual"] = new JObject
            {
                ["distance"] = cfg.ManualDistance,
                ["azimuth"] = cfg.ManualAzimuth,
                ["elevation"] = cfg.ManualElevation,
                ["target"] = new JArray(cfg.ManualTargetX, cfg.ManualTargetY, cfg.ManualTargetZ),
            };
        }

        // Per-render diagnostic arrays so the tile's missing-asset overlay
        // can persist across app restarts. NOT folded into the settings
        // hash — these are outputs of the render, not inputs that drive it.
        // Omit empty lists to keep the JSON small for the common success
        // case (most renders have neither array populated).
        if (missingMeshes != null && missingMeshes.Count > 0)
        {
            obj[MissingMeshesKey] = new JArray(missingMeshes);
        }
        if (missingTextures != null && missingTextures.Count > 0)
        {
            obj[MissingTexturesKey] = new JArray(missingTextures);
        }
        if (!string.IsNullOrWhiteSpace(faceGenMismatch))
        {
            obj[FaceGenMismatchKey] = faceGenMismatch;
        }
        if (physicsConfigNotices != null && physicsConfigNotices.Count > 0)
        {
            obj[PhysicsConfigNoticesKey] = new JArray(physicsConfigNotices);
        }

        return obj.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>Parses the missing-mesh / missing-texture arrays out of a
    /// previously-stamped "Parameters" JSON. Either or both lists may be
    /// empty (or absent from the JSON) — older PNGs stamped before this
    /// field existed simply yield two empty lists, which the host treats
    /// as "no overlay needed". Robust to malformed JSON / missing keys —
    /// returns empty lists rather than throwing.</summary>
    public static void TryReadMissingAssets(
        string parametersJson,
        out List<string> missingMeshes,
        out List<string> missingTextures)
    {
        missingMeshes = new List<string>();
        missingTextures = new List<string>();
        if (string.IsNullOrWhiteSpace(parametersJson)) return;

        try
        {
            var obj = JObject.Parse(parametersJson);
            ReadStringArray(obj, MissingMeshesKey, missingMeshes);
            ReadStringArray(obj, MissingTexturesKey, missingTextures);
        }
        catch
        {
            // Malformed JSON or unexpected schema — treat as "no missing
            // assets recorded" rather than propagating the parse error.
        }
    }

    /// <summary>Parses the stale-physics-config notices out of a
    /// previously-stamped "Parameters" JSON. Empty when absent (older PNGs, or
    /// renders with no broken physics links) or on any parse error. Kept
    /// separate from <see cref="TryReadMissingAssets"/> on purpose: these are
    /// informational (the render is correct) and must never count as missing
    /// assets for the staleness checker.</summary>
    public static List<string> TryReadPhysicsConfigNotices(string parametersJson)
    {
        var notices = new List<string>();
        if (string.IsNullOrWhiteSpace(parametersJson)) return notices;
        try
        {
            var obj = JObject.Parse(parametersJson);
            ReadStringArray(obj, PhysicsConfigNoticesKey, notices);
        }
        catch
        {
            // Malformed JSON — treat as "no notices recorded".
        }
        return notices;
    }

    /// <summary>Parses the FaceGen-mismatch reason out of a previously-stamped
    /// "Parameters" JSON. Returns null when absent (older PNGs, or renders with no
    /// detected mismatch) or on any parse error.</summary>
    public static string? TryReadFaceGenMismatch(string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson)) return null;
        try
        {
            var obj = JObject.Parse(parametersJson);
            if (obj.TryGetValue(FaceGenMismatchKey, out var token))
            {
                var s = token?.Value<string>();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch
        {
            // Malformed JSON / unexpected schema — treat as "no mismatch recorded".
        }
        return null;
    }

    private static void ReadStringArray(JObject obj, string key, List<string> dest)
    {
        if (obj.TryGetValue(key, out var token) && token is JArray arr)
        {
            foreach (var entry in arr)
            {
                var s = entry?.Value<string>();
                if (!string.IsNullOrWhiteSpace(s)) dest.Add(s);
            }
        }
    }

    /// <summary>Reads the depicted-outfit identity stamped at v12+. Returns
    /// null when absent (pre-v12 PNGs) so callers can skip the comparison for
    /// tiles rendered before the field existed.</summary>
    public static string? TryReadEffectiveOutfit(string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson)) return null;
        try
        {
            var obj = JObject.Parse(parametersJson);
            if (obj.TryGetValue(EffectiveOutfitKey, out var token))
            {
                var s = token?.Value<string>();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch
        {
            // Malformed JSON / unexpected schema — treat as "not stamped".
        }
        return null;
    }

    /// <summary>Convenience: hash at the current pipeline schema. Equivalent to
    /// <c>ComputeSettingsHashAtSchema(cfg, PipelineSchemaVersion)</c>.</summary>
    public static string ComputeSettingsHash(InternalMugshotSettings cfg)
        => ComputeSettingsHashAtSchema(cfg, PipelineSchemaVersion);

    /// <summary>SHA256 over every InternalMugshotSettings field that affects
    /// pixel output AT THE GIVEN SCHEMA VERSION. Order is fixed and
    /// append-only: each schema-version step appends new fields at the
    /// bottom so older versions can still be reproduced bit-for-bit by
    /// stopping at the appropriate boundary. The staleness checker uses
    /// this to validate a stamped PNG against the schema it was generated
    /// at, so adding a new toggle in v(N+1) doesn't invalidate v(N) PNGs
    /// (their hash was computed without the new field, and we recompute
    /// against current cfg the same way).
    ///
    /// <para>Top-level Settings fields that affect rendering (e.g.
    /// <c>EnableNormalMapHack</c>, <c>UseModdedFallbackTextures</c>) are
    /// NOT folded in here yet because the in-process renderer documents
    /// them as unused (cf. OffscreenRenderRequest XML doc).</para></summary>
    public static string ComputeSettingsHashAtSchema(InternalMugshotSettings cfg, int schemaVersion,
        bool? effectiveIncludeDefaultOutfit = null, bool? effectiveIncludeHeadgear = null,
        string? effectiveOutfitIdentity = null)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        // === schema v0 fields (everything from before pipeline_schema existed) ===
        sb.Append(cfg.CameraMode).Append('|');
        sb.Append(cfg.HeadTopFraction.ToString("R", inv)).Append('|');
        sb.Append(cfg.HeadBottomFraction.ToString("R", inv)).Append('|');
        sb.Append(cfg.Yaw.ToString("R", inv)).Append('|');
        sb.Append(cfg.Pitch.ToString("R", inv)).Append('|');
        sb.Append(cfg.HairAbovePadding.ToString("R", inv)).Append('|');
        sb.Append(cfg.IncludeAccessories).Append('|');
        sb.Append(cfg.ManualDistance.ToString("R", inv)).Append('|');
        sb.Append(cfg.ManualAzimuth.ToString("R", inv)).Append('|');
        sb.Append(cfg.ManualElevation.ToString("R", inv)).Append('|');
        sb.Append(cfg.ManualTargetX.ToString("R", inv)).Append('|');
        sb.Append(cfg.ManualTargetY.ToString("R", inv)).Append('|');
        sb.Append(cfg.ManualTargetZ.ToString("R", inv)).Append('|');
        sb.Append(cfg.LightingLayoutName ?? "").Append('|');
        sb.Append(cfg.LightingColorSchemeName ?? "").Append('|');
        sb.Append(cfg.BackgroundR).Append(',').Append(cfg.BackgroundG).Append(',').Append(cfg.BackgroundB).Append('|');
        sb.Append(cfg.OutputWidth).Append('x').Append(cfg.OutputHeight).Append('|');
        sb.Append(cfg.VanillaLooseOverridesBsa ? '1' : '0').Append(',');
        sb.Append(cfg.VanillaLooseOverridesModLoose ? '1' : '0').Append('|');
        sb.Append(cfg.RenderMissingTextureAsWireframe ? '1' : '0');

        // === schema v1 fields (2.5.9: portrait-quality toggles) ===
        if (schemaVersion >= 1)
        {
            sb.Append('|').Append(cfg.EnableToneMapping ? '1' : '0');
        }

        // === schema v2 fields (2.5.10: shadow maps) ===
        if (schemaVersion >= 2)
        {
            sb.Append('|').Append(cfg.EnableShadows ? '1' : '0');
        }

        // === schema v3 fields (2.5.11: SSAO) ===
        if (schemaVersion >= 3)
        {
            sb.Append('|').Append(cfg.EnableAmbientOcclusion ? '1' : '0');
        }

        // === schema v4 fields (2.5.12: SSAO tunables) ===
        if (schemaVersion >= 4)
        {
            sb.Append('|').Append(cfg.SsaoRadius.ToString("R", inv));
            sb.Append('|').Append(cfg.SsaoBias.ToString("R", inv));
            sb.Append('|').Append(cfg.SsaoIntensity.ToString("R", inv));
        }

        // === schema v5 fields (2.5.13: eye catch-light + fresnel) ===
        // Fresnel folds under EnableToneMapping (no separate hash bit),
        // so v5 only adds the eye-catchlight toggle.
        if (schemaVersion >= 5)
        {
            sb.Append('|').Append(cfg.EnableEyeCatchlight ? '1' : '0');
        }

        // === schema v6 fields (2.5.14: SSS math correction + strength) ===
        // The math correction itself doesn't get a hash bit - the
        // SubsurfaceStrength multiplier captures the user-facing dial.
        // At strength=0 the corrected pipeline contributes zero SSS, so
        // upgrades hash-match v5 tiles after the migration sets it to 0.
        if (schemaVersion >= 6)
        {
            sb.Append('|').Append(cfg.SubsurfaceStrength.ToString("R", inv));
        }

        // === schema v7 fields (2.5.15: tunable vignette) ===
        // Both radius + intensity contribute to the final pixel values
        // when EnableToneMapping is on, so both must hash. Pre-v7 PNGs
        // are compared at schemaVersion=6 (no entry here), preserving
        // their stamped hash under the legacy hardcoded vignette.
        if (schemaVersion >= 7)
        {
            sb.Append('|').Append(cfg.VignetteRadius.ToString("R", inv));
            sb.Append('|').Append(cfg.VignetteIntensity.ToString("R", inv));
        }

        // === schema v8 fields (skin saturation boost) ===
        // Skin-only chroma multiplier applied post-tint, pre-lighting.
        // Default 1.0 is no-op, so a v7 tile compared at schemaVersion=7
        // (which excludes this entry) continues to match a v8 cfg whose
        // user hasn't changed the field.
        if (schemaVersion >= 8)
        {
            sb.Append('|').Append(cfg.SkinSaturationBoost.ToString("R", inv));
        }

        // === schema v9 fields (character-preview attire toggles) ===
        // Both default false (no extra meshes synthesized), so a v8 tile
        // compared at schemaVersion=8 (which excludes these entries) keeps
        // hash-matching a v9 cfg whose user hasn't enabled either toggle.
        if (schemaVersion >= 9)
        {
            // Per-NPC override (when present) supersedes the global toggle so a
            // mugshot pinned hoodless doesn't re-hash stale when the global
            // setting changes. Callers without a per-NPC context pass null and
            // fall back to the global cfg values.
            sb.Append('|').Append((effectiveIncludeDefaultOutfit ?? cfg.IncludeDefaultOutfit) ? '1' : '0');
            sb.Append('|').Append((effectiveIncludeHeadgear ?? cfg.IncludeHeadgear) ? '1' : '0');
        }

        // === schema v10 fields (tone-map exposure) ===
        // Scales the tone-mapper's baseline exposure when EnableToneMapping
        // is on. Default 1.0 is no-op (reproduces the legacy hardcoded 0.6),
        // so a v9 tile compared at schemaVersion=9 (which excludes this entry)
        // continues to match a v10 cfg whose user hasn't changed the field.
        if (schemaVersion >= 10)
        {
            sb.Append('|').Append(cfg.Exposure.ToString("R", inv));
        }

        // === schema v11 fields (hair-relief / daylight / bloom finishing) ===
        // TonemapHairRelief and EnableBloom default ON, DaylightBoost OFF; but
        // a v10 tile is compared at schemaVersion=10 (which excludes these
        // entries), so the default flip does NOT drift v10 tiles. Only tiles
        // stamped at v11 carry these, and changing any of them re-hashes those.
        if (schemaVersion >= 11)
        {
            sb.Append('|').Append(cfg.TonemapHairRelief ? '1' : '0');
            sb.Append('|').Append(cfg.DaylightBoost ? '1' : '0');
            sb.Append('|').Append(cfg.DaylightBoostIntensity.ToString("R", inv));
            sb.Append('|').Append(cfg.EnableBloom ? '1' : '0');
            sb.Append('|').Append(cfg.BloomIntensity.ToString("R", inv));
        }

        // === schema v12 fields (depicted-outfit identity) ===
        // The resolved outfit FormKey string ("none" when attire is off or the
        // NPC has no outfit) — IDENTITY only, never the provenance source, so
        // any combination of patching mode / Include Outfit / distributor
        // configs that lands on the same outfit keeps the same hash. Callers
        // without a per-NPC outfit context pass null and fall back to "none";
        // the staleness checker always supplies the stamped/current value.
        if (schemaVersion >= 12)
        {
            sb.Append('|').Append(string.IsNullOrEmpty(effectiveOutfitIdentity)
                ? NoOutfitIdentity
                : effectiveOutfitIdentity);
        }

        // === schema v13 fields (SSAO occluder thickness + hair AO gap) ===
        // Thickness default 1.5 matches the value the renderer hardcoded from
        // 2.5.16 through v12, and a v12 tile is compared at schemaVersion=12
        // (which excludes these entries), so existing tiles stay valid until
        // the user actually changes a field. HairGap is new at v13 (the fade
        // itself changes hair shading, which the schema bump re-stales).
        if (schemaVersion >= 13)
        {
            sb.Append('|').Append(cfg.SsaoThickness.ToString("R", inv));
            sb.Append('|').Append(cfg.SsaoHairGap.ToString("R", inv));
        }

        // === schema v14 fields (neutral-white-tint hair albedo compensation) ===
        // Unlike the earlier no-op-default additions, this default (1.0 = full)
        // DOES change output for hair with a neutral-white baked tint (a red wig
        // renders deep instead of clipping to pink). A v13 tile is compared at
        // schemaVersion=13 (which excludes this entry), so existing tiles stay
        // valid until regenerated; the corrected rendering applies to new tiles,
        // and changing the field re-stales v14 tiles.
        if (schemaVersion >= 14)
        {
            sb.Append('|').Append(cfg.HairAlbedoCompensate.ToString("R", inv));
        }

        // === schema v15 fields (hair-shadow brow-ridge mitigations A/B/C) ===
        // B (SoftenShadowEdges) defaults ON and changes the cast-shadow look;
        // A/C default OFF and their radii are inert until their toggle is on.
        // Only relevant when EnableShadows is on, but hashed unconditionally
        // (matching the SSAO tunables). A v14 tile compared at schemaVersion=14
        // excludes these entries and stays valid until regenerated.
        if (schemaVersion >= 15)
        {
            sb.Append('|').Append(cfg.ExcludeHairShadowCaster ? '1' : '0');
            sb.Append('|').Append(cfg.SoftenShadowEdges ? '1' : '0');
            sb.Append('|').Append(cfg.ShadowPcfRadius.ToString("R", inv));
            sb.Append('|').Append(cfg.TightShadowFrustum ? '1' : '0');
            sb.Append('|').Append(cfg.ShadowFrustumRadius.ToString("R", inv));
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) hex.Append(b.ToString("x2"));
        return hex.ToString();
    }
}
