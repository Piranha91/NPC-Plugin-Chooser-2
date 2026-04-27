using System.Collections.Generic;
using System.Linq;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Wraps a transient <see cref="VM_InternalMugshotPreview"/> for the per-tile
/// "Show 3D Preview" popup launched from the mugshot context menus. Carries
/// the title text and a snapshot of the lighting selection at popup-open so
/// the host view can prompt the user on close if they edited the dropdowns
/// (the shared lighting panel writes layout / scheme name changes back to
/// <see cref="Settings.InternalMugshot"/> live).
/// </summary>
public class VM_FullScreen3DPreview : ReactiveObject
{
    private readonly Settings _settings;
    private readonly string _initialLayoutName;
    private readonly string _initialColorSchemeName;
    private readonly List<CharacterViewer.Rendering.CharacterViewerLightingLayout> _initialUserLayouts;
    private readonly List<CharacterViewer.Rendering.CharacterViewerLightingColorScheme> _initialUserColorSchemes;

    public VM_InternalMugshotPreview Inner { get; }

    [Reactive] public string Title { get; private set; }

    public VM_FullScreen3DPreview(VM_InternalMugshotPreview inner, Settings settings, string title)
    {
        Inner = inner;
        Title = title;
        _settings = settings;

        // Snapshot every lighting field that the panel can mutate live so the
        // close-time revert prompt has something to roll back to. Names cover
        // the dropdown selections; the user-preset lists cover Save / Delete
        // commands inside the panel that mutate
        // Settings.InternalMugshot.UserLighting* directly.
        var cfg = settings.InternalMugshot;
        _initialLayoutName = cfg.LightingLayoutName ?? string.Empty;
        _initialColorSchemeName = cfg.LightingColorSchemeName ?? string.Empty;
        _initialUserLayouts = cfg.UserLightingLayouts.ToList();
        _initialUserColorSchemes = cfg.UserLightingColorSchemes.ToList();
    }

    /// <summary>True if any persisted lighting field changed while the popup
    /// was open — selection name change OR user-preset add/delete. The view's
    /// closing handler uses this to decide whether to prompt.</summary>
    public bool LightingChanged()
    {
        var cfg = _settings.InternalMugshot;
        if ((cfg.LightingLayoutName ?? string.Empty) != _initialLayoutName) return true;
        if ((cfg.LightingColorSchemeName ?? string.Empty) != _initialColorSchemeName) return true;
        if (!UserListsEqual(cfg.UserLightingLayouts, _initialUserLayouts,
                (a, b) => a.Name == b.Name)) return true;
        if (!UserListsEqual(cfg.UserLightingColorSchemes, _initialUserColorSchemes,
                (a, b) => a.Name == b.Name)) return true;
        return false;
    }

    /// <summary>Restore the snapshot. Called when the user declines the
    /// "Save these lighting changes globally?" prompt on close.</summary>
    public void RevertLightingToSnapshot()
    {
        var cfg = _settings.InternalMugshot;
        cfg.LightingLayoutName = _initialLayoutName;
        cfg.LightingColorSchemeName = _initialColorSchemeName;
        cfg.UserLightingLayouts.Clear();
        foreach (var layout in _initialUserLayouts) cfg.UserLightingLayouts.Add(layout);
        cfg.UserLightingColorSchemes.Clear();
        foreach (var scheme in _initialUserColorSchemes) cfg.UserLightingColorSchemes.Add(scheme);
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
