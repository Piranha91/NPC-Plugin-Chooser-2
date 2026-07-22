using System.Collections.Generic;
using System.Linq;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Wraps a transient <see cref="VM_InternalMugshotPreview"/> for the per-tile
/// "Show 3D Preview" popup launched from the mugshot context menus. Carries
/// the title text and a snapshot of every render-affecting field at popup-open
/// so the host view can prompt the user on close if they edited anything via
/// the embedded shared lighting / render panel (which writes back to global
/// settings live).
///
/// Camera state (auto-mode framing fields, manual-mode distance/azimuth/
/// elevation/target) is intentionally excluded from the snapshot — those are
/// transient per-preview adjustments that persist on every drag-end and
/// shouldn't trigger a "save changes globally?" prompt at close.
/// </summary>
public class VM_FullScreen3DPreview : ReactiveObject
{
    private readonly Settings _settings;

    // --- Lighting selection (dropdowns + user-saved presets) ---
    private readonly string _initialLayoutName;
    private readonly string _initialColorSchemeName;
    private readonly List<CharacterViewer.Rendering.CharacterViewerLightingLayout> _initialUserLayouts;
    private readonly List<CharacterViewer.Rendering.CharacterViewerLightingColorScheme> _initialUserColorSchemes;

    // --- Background ---
    private readonly byte _initialBackgroundR;
    private readonly byte _initialBackgroundG;
    private readonly byte _initialBackgroundB;

    // --- Render-quality flags ---
    private readonly bool _initialEnableToneMapping;
    private readonly bool _initialEnableShadows;
    private readonly bool _initialEnableAmbientOcclusion;
    private readonly bool _initialEnableEyeCatchlight;
    private readonly bool _initialRenderMissingTextureAsWireframe;

    // --- SSAO tunables ---
    private readonly float _initialSsaoRadius;
    private readonly float _initialSsaoBias;
    private readonly float _initialSsaoIntensity;

    // --- Other render params ---
    private readonly float _initialSubsurfaceStrength;
    private readonly float _initialVignetteRadius;
    private readonly float _initialVignetteIntensity;
    private readonly float _initialSkinSaturationBoost;
    private readonly float _initialExposure;
    private readonly bool _initialTonemapHairRelief;
    private readonly float _initialHairAlbedoCompensate;
    private readonly bool _initialDaylightBoost;
    private readonly float _initialDaylightBoostIntensity;
    private readonly bool _initialEnableBloom;
    private readonly float _initialBloomIntensity;

    public VM_InternalMugshotPreview Inner { get; }

    [Reactive] public string Title { get; private set; }

    public VM_FullScreen3DPreview(VM_InternalMugshotPreview inner, Settings settings, string title)
    {
        Inner = inner;
        Title = title;
        _settings = settings;

        // Snapshot every render-affecting field that the lighting / render
        // panel can mutate live. The close-time revert prompt rolls these back
        // if the user declines. Camera state (auto-mode framing + manual-mode
        // distance/azimuth/target) is deliberately excluded so per-preview
        // pose adjustments don't trigger the prompt.
        var cfg = settings.InternalMugshot;

        _initialLayoutName = cfg.LightingLayoutName ?? string.Empty;
        _initialColorSchemeName = cfg.LightingColorSchemeName ?? string.Empty;
        _initialUserLayouts = cfg.UserLightingLayouts.ToList();
        _initialUserColorSchemes = cfg.UserLightingColorSchemes.ToList();

        _initialBackgroundR = cfg.BackgroundR;
        _initialBackgroundG = cfg.BackgroundG;
        _initialBackgroundB = cfg.BackgroundB;

        _initialEnableToneMapping = cfg.EnableToneMapping;
        _initialEnableShadows = cfg.EnableShadows;
        _initialEnableAmbientOcclusion = cfg.EnableAmbientOcclusion;
        _initialEnableEyeCatchlight = cfg.EnableEyeCatchlight;
        _initialRenderMissingTextureAsWireframe = cfg.RenderMissingTextureAsWireframe;

        _initialSsaoRadius = cfg.SsaoRadius;
        _initialSsaoBias = cfg.SsaoBias;
        _initialSsaoIntensity = cfg.SsaoIntensity;

        _initialSubsurfaceStrength = cfg.SubsurfaceStrength;
        _initialVignetteRadius = cfg.VignetteRadius;
        _initialVignetteIntensity = cfg.VignetteIntensity;
        _initialSkinSaturationBoost = cfg.SkinSaturationBoost;
        _initialExposure = cfg.Exposure;
        _initialTonemapHairRelief = cfg.TonemapHairRelief;
        _initialHairAlbedoCompensate = cfg.HairAlbedoCompensate;
        _initialDaylightBoost = cfg.DaylightBoost;
        _initialDaylightBoostIntensity = cfg.DaylightBoostIntensity;
        _initialEnableBloom = cfg.EnableBloom;
        _initialBloomIntensity = cfg.BloomIntensity;
    }

    /// <summary>True if any persisted render-affecting field changed while
    /// the popup was open. The view's closing handler uses this to decide
    /// whether to prompt the user to save the changes globally or revert.</summary>
    public bool RenderSettingsChanged()
    {
        var cfg = _settings.InternalMugshot;

        // Lighting selection
        if ((cfg.LightingLayoutName ?? string.Empty) != _initialLayoutName) return true;
        if ((cfg.LightingColorSchemeName ?? string.Empty) != _initialColorSchemeName) return true;
        if (!UserListsEqual(cfg.UserLightingLayouts, _initialUserLayouts,
                (a, b) => a.Name == b.Name)) return true;
        if (!UserListsEqual(cfg.UserLightingColorSchemes, _initialUserColorSchemes,
                (a, b) => a.Name == b.Name)) return true;

        // Background
        if (cfg.BackgroundR != _initialBackgroundR) return true;
        if (cfg.BackgroundG != _initialBackgroundG) return true;
        if (cfg.BackgroundB != _initialBackgroundB) return true;

        // Render-quality flags
        if (cfg.EnableToneMapping != _initialEnableToneMapping) return true;
        if (cfg.EnableShadows != _initialEnableShadows) return true;
        if (cfg.EnableAmbientOcclusion != _initialEnableAmbientOcclusion) return true;
        if (cfg.EnableEyeCatchlight != _initialEnableEyeCatchlight) return true;
        if (cfg.RenderMissingTextureAsWireframe != _initialRenderMissingTextureAsWireframe) return true;

        // SSAO tunables
        if (cfg.SsaoRadius != _initialSsaoRadius) return true;
        if (cfg.SsaoBias != _initialSsaoBias) return true;
        if (cfg.SsaoIntensity != _initialSsaoIntensity) return true;

        // Other render params
        if (cfg.SubsurfaceStrength != _initialSubsurfaceStrength) return true;
        if (cfg.VignetteRadius != _initialVignetteRadius) return true;
        if (cfg.VignetteIntensity != _initialVignetteIntensity) return true;
        if (cfg.SkinSaturationBoost != _initialSkinSaturationBoost) return true;
        if (cfg.Exposure != _initialExposure) return true;
        if (cfg.TonemapHairRelief != _initialTonemapHairRelief) return true;
        if (cfg.HairAlbedoCompensate != _initialHairAlbedoCompensate) return true;
        if (cfg.DaylightBoost != _initialDaylightBoost) return true;
        if (cfg.DaylightBoostIntensity != _initialDaylightBoostIntensity) return true;
        if (cfg.EnableBloom != _initialEnableBloom) return true;
        if (cfg.BloomIntensity != _initialBloomIntensity) return true;

        return false;
    }

    /// <summary>Restore the snapshot. Called when the user declines the
    /// "Save these render changes globally?" prompt on close.</summary>
    public void RevertRenderSettingsToSnapshot()
    {
        var cfg = _settings.InternalMugshot;

        cfg.LightingLayoutName = _initialLayoutName;
        cfg.LightingColorSchemeName = _initialColorSchemeName;
        cfg.UserLightingLayouts.Clear();
        foreach (var layout in _initialUserLayouts) cfg.UserLightingLayouts.Add(layout);
        cfg.UserLightingColorSchemes.Clear();
        foreach (var scheme in _initialUserColorSchemes) cfg.UserLightingColorSchemes.Add(scheme);

        cfg.BackgroundR = _initialBackgroundR;
        cfg.BackgroundG = _initialBackgroundG;
        cfg.BackgroundB = _initialBackgroundB;

        cfg.EnableToneMapping = _initialEnableToneMapping;
        cfg.EnableShadows = _initialEnableShadows;
        cfg.EnableAmbientOcclusion = _initialEnableAmbientOcclusion;
        cfg.EnableEyeCatchlight = _initialEnableEyeCatchlight;
        cfg.RenderMissingTextureAsWireframe = _initialRenderMissingTextureAsWireframe;

        cfg.SsaoRadius = _initialSsaoRadius;
        cfg.SsaoBias = _initialSsaoBias;
        cfg.SsaoIntensity = _initialSsaoIntensity;

        cfg.SubsurfaceStrength = _initialSubsurfaceStrength;
        cfg.VignetteRadius = _initialVignetteRadius;
        cfg.VignetteIntensity = _initialVignetteIntensity;
        cfg.SkinSaturationBoost = _initialSkinSaturationBoost;
        cfg.Exposure = _initialExposure;
        cfg.TonemapHairRelief = _initialTonemapHairRelief;
        cfg.HairAlbedoCompensate = _initialHairAlbedoCompensate;
        cfg.DaylightBoost = _initialDaylightBoost;
        cfg.DaylightBoostIntensity = _initialDaylightBoostIntensity;
        cfg.EnableBloom = _initialEnableBloom;
        cfg.BloomIntensity = _initialBloomIntensity;
    }

    private static bool UserListsEqual<T>(IList<T> a, IList<T> b, System.Func<T, T, bool> eq)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!eq(a[i], b[i])) return false;
        }
        return true;
    }
}
