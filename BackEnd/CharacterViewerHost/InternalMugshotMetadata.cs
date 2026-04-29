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
    /// </list>
    /// </para></summary>
    public const int PipelineSchemaVersion = 5;

    // JSON keys for the missing-asset arrays embedded in the "Parameters"
    // tEXt chunk. Kept as constants so the read path in
    // TryReadMissingAssets can match on the same names without drift.
    private const string MissingMeshesKey = "missing_meshes";
    private const string MissingTexturesKey = "missing_textures";
    public const string PipelineSchemaKey = "pipeline_schema";

    public static string Build(
        FormKey npcFormKey,
        InternalMugshotSettings cfg,
        IReadOnlyList<string>? missingMeshes = null,
        IReadOnlyList<string>? missingTextures = null)
    {
        var obj = new JObject
        {
            ["renderer"] = RendererName,
            ["renderer_version"] = CharacterViewerRendering.Version.ToString(),
            [PipelineSchemaKey] = PipelineSchemaVersion,
            ["npc_form_key"] = npcFormKey.ToString(),
            ["settings_hash"] = ComputeSettingsHashAtSchema(cfg, PipelineSchemaVersion),
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
            ["enable_ambient_occlusion"] = cfg.EnableAmbientOcclusion,
            ["ssao_radius"] = cfg.SsaoRadius,
            ["ssao_bias"] = cfg.SsaoBias,
            ["ssao_intensity"] = cfg.SsaoIntensity,
            ["enable_eye_catchlight"] = cfg.EnableEyeCatchlight,
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
    public static string ComputeSettingsHashAtSchema(InternalMugshotSettings cfg, int schemaVersion)
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

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) hex.Append(b.ToString("x2"));
        return hex.ToString();
    }
}
