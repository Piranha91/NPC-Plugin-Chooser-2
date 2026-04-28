using System;
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

    public static string Build(FormKey npcFormKey, InternalMugshotSettings cfg)
    {
        var obj = new JObject
        {
            ["renderer"] = RendererName,
            ["renderer_version"] = CharacterViewerRendering.Version.ToString(),
            ["npc_form_key"] = npcFormKey.ToString(),
            ["settings_hash"] = ComputeSettingsHash(cfg),
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

        return obj.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>SHA256 over every InternalMugshotSettings field that affects
    /// pixel output. Order is fixed; keep it stable across releases —
    /// changing the byte layout invalidates every previously-stamped
    /// mugshot. When adding a new pixel-affecting setting, append it at
    /// the end (don't reorder) so old PNGs stay loadable / hashable.
    ///
    /// <para>Top-level Settings fields that affect rendering (e.g.
    /// <c>EnableNormalMapHack</c>, <c>UseModdedFallbackTextures</c>) are
    /// NOT folded in here yet because the in-process renderer documents
    /// them as unused (cf. OffscreenRenderRequest XML doc). When those
    /// get wired into the renderer, fold them in by adding new
    /// <c>sb.Append(...)</c> calls at the bottom of this method and
    /// updating the JSON in <see cref="Build"/> in parallel.</para></summary>
    public static string ComputeSettingsHash(InternalMugshotSettings cfg)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
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
        // Advanced asset-resolution toggles (renderer 2.3.0+). Folded into
        // the hash so flipping either invalidates auto-generated PNGs and
        // MugshotStalenessChecker regenerates them on next access.
        sb.Append(cfg.VanillaLooseOverridesBsa ? '1' : '0').Append(',');
        sb.Append(cfg.VanillaLooseOverridesModLoose ? '1' : '0').Append('|');
        // Wireframe-fallback toggle (renderer 2.5.8+). When off, alpha
        // shapes with missing diffuse are silently culled instead of
        // drawn as a green wireframe — the saved PNG differs in pixels
        // wherever an affected shape would have been, so flipping the
        // toggle must invalidate the stamped mugshot.
        sb.Append(cfg.RenderMissingTextureAsWireframe ? '1' : '0');

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) hex.Append(b.ToString("x2"));
        return hex.ToString();
    }
}
