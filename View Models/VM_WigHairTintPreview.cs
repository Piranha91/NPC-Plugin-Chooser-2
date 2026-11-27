using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>Sent when an NPC's kept converted-wig hair colour is stored, changed or removed
/// (<see cref="Settings.NpcWigHairTintModes"/>), so that NPC's shown mugshot tiles can redraw
/// their wig badge, its row can offer or withdraw "Clear kept wig hair color", and an open 3D
/// preview can follow a clear made from a menu.</summary>
public sealed record NpcWigHairTintKeptChangedMessage(FormKey NpcFormKey);

/// <summary>
/// The words and decisions of the converted-wig hair colour controls, free of WPF state.
/// </summary>
public static class WigHairTintPreviewRules
{
    /// <summary>The short names used on the 3D preview's three-position toggle and in the
    /// badge's tooltip.</summary>
    public static string ShortLabel(WigHairTintMode mode) => mode switch
    {
        WigHairTintMode.Always => "On",
        WigHairTintMode.Never => "Off",
        _ => "Auto",
    };

    /// <summary>
    /// Whether the toggle is offered: the NPC's wig will be converted to head parts, the NPC has
    /// a wig at all, the one wig the converter would take was found, and that wig's mesh has a
    /// shape the hair colour can be written to. A mesh that could not be read (null) counts as
    /// having one.
    /// </summary>
    public static bool IsEligible(bool wigIsConvertedToHeadParts, bool hasEffectiveWigSources,
        bool wigCarrierFound, bool? wigHasHairTintShape) =>
        wigIsConvertedToHeadParts && hasEffectiveWigSources && wigCarrierFound && wigHasHairTintShape != false;

    /// <summary>What Auto does for this NPC right now, and why, in a few words.</summary>
    public static string AutoCaption(RaceMenuHairTintState raceMenu, uint carrierArmorSlots, uint carrierAddonSlots)
    {
        if (!raceMenu.Installed) return "Auto: not tinted, RaceMenu not found";
        if (!raceMenu.TintEnabled) return "Auto: not tinted, RaceMenu's hair tint is off";
        return raceMenu.TintsWornItem(carrierArmorSlots, carrierAddonSlots)
            ? "Auto: tinted, RaceMenu found"
            : "Auto: not tinted, the wig is outside RaceMenu's slots";
    }

    public static string KeptTooltip(WigHairTintMode mode) =>
        $"Wig hair color kept for this NPC: {ShortLabel(mode)}";
}

/// <summary>
/// The 3D preview's converted-wig hair colour controls: a three-position toggle (Auto / On / Off)
/// that shows the NPC as each <see cref="WigHairTintMode"/> would bake it, and "Keep for this
/// NPC", which stores the toggle's position as that NPC's own override
/// (<see cref="Settings.NpcWigHairTintModes"/>). Offered only for an NPC whose wig is converted
/// to head parts and can take a hair colour.
///
/// <para>The toggle starts at the mode that applies to the NPC (its own kept choice, else its
/// mod's, else the global default). Flipping it changes only what this preview draws; nothing is
/// stored unless Keep is checked. While Keep is checked each flip updates the stored mode, and
/// unchecking removes it.</para>
///
/// <para>No renderer and no window here: the host preview asks <see cref="RenderTintOverride"/>
/// when it resolves the NPC's meshes and reloads on <see cref="RenderInvalidated"/>.</para>
/// </summary>
public sealed class VM_WigHairTintPreview : ReactiveObject, IDisposable
{
    private readonly Settings _settings;
    private readonly Func<RaceMenuHairTintState> _raceMenuState;
    private readonly Action? _persist;
    private readonly IDisposable _keptChangedSubscription;

    private ModSetting? _mod;
    private FormKey _targetNpc = FormKey.Null;
    private uint _carrierArmorSlots;
    private uint _carrierAddonSlots;
    private WigHairTintMode _mode = WigHairTintMode.Auto;
    private bool _keep;

    /// <param name="raceMenuState">What RaceMenu does to a worn wig in this installation
    /// (<see cref="RaceMenuDetector.State"/>).</param>
    /// <param name="persist">Asks for the settings to be saved after a kept choice changes.</param>
    public VM_WigHairTintPreview(Settings settings, Func<RaceMenuHairTintState> raceMenuState, Action? persist = null)
    {
        _settings = settings;
        _raceMenuState = raceMenuState;
        _persist = persist;

        // A kept choice cleared from the NPC list while this preview is open.
        _keptChangedSubscription = MessageBus.Current.Listen<NpcWigHairTintKeptChangedMessage>()
            .Where(m => m.NpcFormKey == _targetNpc)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => SyncKeepFromSettings());
    }

    /// <summary>Whether the controls are shown at all.</summary>
    [Reactive] public bool IsAvailable { get; private set; }

    /// <summary>"Auto: tinted, RaceMenu found" and the like; what Auto means for this NPC.</summary>
    [Reactive] public string AutoCaption { get; private set; } = string.Empty;

    /// <summary>Raised when the picture must be drawn again because the toggle was flipped.</summary>
    public event Action? RenderInvalidated;

    /// <summary>The toggle's position.</summary>
    public WigHairTintMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            RaiseModeChanged();
            if (!IsAvailable) return;
            if (_keep) StoreKept();
            RenderInvalidated?.Invoke();
        }
    }

    // The three positions as the view's radio buttons bind them.
    public bool IsAuto { get => _mode == WigHairTintMode.Auto; set { if (value) Mode = WigHairTintMode.Auto; } }
    public bool IsOn { get => _mode == WigHairTintMode.Always; set { if (value) Mode = WigHairTintMode.Always; } }
    public bool IsOff { get => _mode == WigHairTintMode.Never; set { if (value) Mode = WigHairTintMode.Never; } }

    /// <summary>"Keep for this NPC": checked stores the toggle's position, unchecked removes it.</summary>
    public bool Keep
    {
        get => _keep;
        set
        {
            if (_keep == value) return;
            _keep = value;
            this.RaisePropertyChanged();
            if (!IsAvailable || _targetNpc.IsNull) return;
            if (value)
            {
                StoreKept();
            }
            else
            {
                _settings.SetNpcWigHairTintMode(new[] { _targetNpc }, null);
                AnnounceKeptChanged();
            }
        }
    }

    /// <summary>Whether the bake would write the NPC's hair colour into the wig at the toggle's
    /// current position.</summary>
    public bool TintApplied =>
        WigHairTintDecision.ShouldTint(_mode, _raceMenuState(), _carrierArmorSlots, _carrierAddonSlots);

    /// <summary>What the host passes to the mesh resolver: the answer of
    /// <see cref="TintApplied"/> while the controls are offered, else null (the preview then
    /// follows the mugshot setting as it always did).</summary>
    public bool? RenderTintOverride => IsAvailable ? TintApplied : null;

    /// <summary>
    /// Points the controls at the NPC the preview is loading. A first load of an NPC puts the
    /// toggle at the mode that applies to it; loading the same NPC and mod again (an attire
    /// toggle, a wig designation, a lost graphics context) keeps the position the user flipped to
    /// and only refreshes whether the controls are offered.
    /// </summary>
    /// <param name="targetNpc">The NPC being patched, which is what a kept choice is stored
    /// under; not the appearance donor.</param>
    public void Load(ModSetting? mod, FormKey targetNpc, bool eligible, uint carrierArmorSlots, uint carrierAddonSlots)
    {
        bool sameSubject = _mod != null && mod != null && targetNpc == _targetNpc &&
                           string.Equals(_mod.DisplayName, mod.DisplayName, StringComparison.OrdinalIgnoreCase);

        _mod = mod;
        _targetNpc = targetNpc;
        _carrierArmorSlots = carrierArmorSlots;
        _carrierAddonSlots = carrierAddonSlots;
        IsAvailable = eligible && mod != null && !targetNpc.IsNull;

        if (!IsAvailable)
        {
            AutoCaption = string.Empty;
            return;
        }

        AutoCaption = WigHairTintPreviewRules.AutoCaption(_raceMenuState(), carrierArmorSlots, carrierAddonSlots);

        if (!sameSubject)
        {
            _mode = _settings.GetEffectiveWigHairTintMode(mod, targetNpc);
            RaiseModeChanged();
        }

        SyncKeepFromSettings();
    }

    /// <summary>Hides the controls (the preview shows an NPC they do not apply to).</summary>
    public void Clear()
    {
        _mod = null;
        _targetNpc = FormKey.Null;
        IsAvailable = false;
        AutoCaption = string.Empty;
    }

    private void SyncKeepFromSettings()
    {
        bool kept = !_targetNpc.IsNull && _settings.GetNpcWigHairTintMode(_targetNpc) != null;
        if (_keep == kept) return;
        _keep = kept;
        this.RaisePropertyChanged(nameof(Keep));
    }

    private void StoreKept()
    {
        _settings.SetNpcWigHairTintMode(new[] { _targetNpc }, _mode);
        AnnounceKeptChanged();
    }

    private void AnnounceKeptChanged()
    {
        _persist?.Invoke();
        MessageBus.Current.SendMessage(new NpcWigHairTintKeptChangedMessage(_targetNpc));
    }

    private void RaiseModeChanged()
    {
        this.RaisePropertyChanged(nameof(Mode));
        this.RaisePropertyChanged(nameof(IsAuto));
        this.RaisePropertyChanged(nameof(IsOn));
        this.RaisePropertyChanged(nameof(IsOff));
    }

    public void Dispose() => _keptChangedSubscription.Dispose();
}
