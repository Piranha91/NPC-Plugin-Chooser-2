using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// One row of the "Set Wig Meshes" selector: a hair-slot ArmorAddon carried in
/// the previewed NPC's WornArmor (WNAM). Hovering glow-highlights the hair mesh
/// in the viewport (the Hair channel renders it). Unlike the antler selector,
/// auto-detected rows are NOT locked — unchecking an auto-detected row records
/// an "is NOT a wig" veto (clearing a scan false positive is half the feature),
/// and checking an undetected row promotes it to wig status. The designation
/// key is the ARMA's EditorID (stable across FormKey remapping).
/// </summary>
public class VM_WigArmatureCandidate : ReactiveObject, IAntlerHoverTarget
{
    public FormKey ArmaFormKey { get; }

    /// <summary>The ARMA's EditorID — the manual-designation key (persisted).</summary>
    public string EditorId { get; }

    public string DisplayName { get; }

    /// <summary>True when the scan detected this ARMA
    /// (<see cref="Models.ModSetting.DetectedWigArmatures"/>).</summary>
    public bool IsAutoDetected { get; }

    /// <summary>Resolved hair NIF render shape names — the hover-highlight keys.
    /// Empty when the mesh could not be read (row stays toggleable, no glow).</summary>
    public IReadOnlyList<string> ShapeNames { get; }

    /// <summary>Whether this ARMA is currently an effective wig (scan minus
    /// vetoes plus promotions). Two-state; toggling raises
    /// <see cref="UserToggled"/> so the host persists the designation.</summary>
    [Reactive] public bool IsChecked { get; set; }

    public event Action<VM_WigArmatureCandidate>? UserToggled;

    public event Action<IAntlerHoverTarget, bool>? HoverChanged;

    public VM_WigArmatureCandidate(NpcMeshResolver.WigArmatureCandidate model)
    {
        ArmaFormKey = model.ArmaFormKey;
        EditorId = model.EditorId;
        DisplayName = model.DisplayName;
        IsAutoDetected = model.IsAutoDetected;
        IsChecked = model.IsEffectiveWig;
        ShapeNames = model.ShapeNames;

        this.WhenAnyValue(x => x.IsChecked)
            .Skip(1)
            .Subscribe(_ => UserToggled?.Invoke(this));
    }

    public void RaiseHover(bool entered) => HoverChanged?.Invoke(this, entered);
}
