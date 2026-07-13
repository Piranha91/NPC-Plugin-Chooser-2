using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CharacterViewer.Rendering;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;

/// <summary>
/// Adapts NPC2's plain-POCO <see cref="Settings"/> model to the renderer's
/// reactive <see cref="ICharacterViewerSettings"/>. The viewer subscribes to
/// PropertyChanged on this adapter via <c>WhenAnyValue</c>, so writes go
/// through the adapter (which raises INPC) rather than directly to the model.
/// User lighting layouts / color schemes are exposed as ObservableCollections
/// backed by the persisted lists in <see cref="InternalMugshotSettings"/>.
/// Every setter / collection-change schedules a throttled save on
/// <see cref="VM_Settings"/> so the lighting picker, the verbose-log toggle,
/// and Save-As-Layout / Save-As-Color-Scheme additions all persist within
/// ~1.5 s of the user action — independent of the app-exit save path.
/// </summary>
public sealed class NpcChooserSettingsAdapter : ICharacterViewerSettings
{
    private readonly Settings _settings;
    private readonly Lazy<VM_Settings> _lazyVmSettings;
    private readonly ObservableCollection<CharacterViewerLightingLayout> _userLayouts;
    private readonly ObservableCollection<CharacterViewerLightingColorScheme> _userColorSchemes;

    public NpcChooserSettingsAdapter(Settings settings, Lazy<VM_Settings> lazyVmSettings)
    {
        _settings = settings;
        _lazyVmSettings = lazyVmSettings;
        _userLayouts = new ObservableCollection<CharacterViewerLightingLayout>(_settings.InternalMugshot.UserLightingLayouts);
        _userColorSchemes = new ObservableCollection<CharacterViewerLightingColorScheme>(_settings.InternalMugshot.UserLightingColorSchemes);

        // Persist additions/removals back to the settings model so they survive a restart.
        _userLayouts.CollectionChanged += (_, _) =>
        {
            _settings.InternalMugshot.UserLightingLayouts = new List<CharacterViewerLightingLayout>(_userLayouts);
            RequestSave();
        };
        _userColorSchemes.CollectionChanged += (_, _) =>
        {
            _settings.InternalMugshot.UserLightingColorSchemes = new List<CharacterViewerLightingColorScheme>(_userColorSchemes);
            RequestSave();
        };
    }

    private void RequestSave() => _lazyVmSettings.Value?.RequestThrottledSave();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CharacterViewerLightingLayout
    {
        get => _settings.InternalMugshot.LightingLayoutName ?? "";
        set
        {
            if (_settings.InternalMugshot.LightingLayoutName == value) return;
            _settings.InternalMugshot.LightingLayoutName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CharacterViewerLightingLayout)));
            RequestSave();
        }
    }

    public string CharacterViewerLightingColorScheme
    {
        get => _settings.InternalMugshot.LightingColorSchemeName ?? "";
        set
        {
            if (_settings.InternalMugshot.LightingColorSchemeName == value) return;
            _settings.InternalMugshot.LightingColorSchemeName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CharacterViewerLightingColorScheme)));
            RequestSave();
        }
    }

    public IList<CharacterViewerLightingLayout> UserLightingLayouts => _userLayouts;
    public IList<CharacterViewerLightingColorScheme> UserLightingColorSchemes => _userColorSchemes;

    public bool CharacterViewerVerboseLog
    {
        get => _settings.InternalMugshot.VerboseLog;
        set
        {
            if (_settings.InternalMugshot.VerboseLog == value) return;
            _settings.InternalMugshot.VerboseLog = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CharacterViewerVerboseLog)));
            RequestSave();
        }
    }

    /// <summary>Decode-cache sizing mode. The renderer's caches read this live at each budget re-poll,
    /// so a change takes effect within a few renders without a restart.</summary>
    public RenderCacheMode CacheMode
    {
        get => _settings.InternalMugshot.CacheMode;
        set
        {
            if (_settings.InternalMugshot.CacheMode == value) return;
            _settings.InternalMugshot.CacheMode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CacheMode)));
            RequestSave();
        }
    }

    /// <summary>Fixed cache pool in bytes (from the persisted GB value), used only in FixedRam mode.</summary>
    public long FixedCacheBudgetBytes =>
        (long)(Math.Max(0, _settings.InternalMugshot.CacheFixedBudgetGB) * 1024L * 1024L * 1024L);

    /// <summary>Collective share of free RAM (0-100) the decode caches may use in PercentFreeRam mode.
    /// Read live at each budget re-poll, so a change takes effect within a few renders without a restart.</summary>
    public double FreeRamCachePercent => Math.Clamp(_settings.InternalMugshot.CacheFreeRamPercent, 0, 100);
}
