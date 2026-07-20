using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// One head part row in the 3D preview's "Set Antler Head Parts" selector.
/// Hovering the row highlights the matching baked FaceGen shape(s) in the
/// viewport; checking it designates the head part as an antler to remove.
/// Keyword-auto-detected antlers are shown checked and locked
/// (<see cref="CanToggle"/> = false) — the scan already handles them.
/// </summary>
public class VM_AntlerHeadPartCandidate : ReactiveObject
{
    public FormKey FormKey { get; }

    /// <summary>The head part's EditorID — the manual-block key (persisted).</summary>
    public string EditorId { get; }

    public string DisplayName { get; }
    public string TypeLabel { get; }

    /// <summary>Baked FaceGen shape name(s) for this head part (EditorID +
    /// ExtraParts EditorIDs) — the highlight/hide key.</summary>
    public IReadOnlyList<string> ShapeNames { get; }

    /// <summary>True when keyword detection already flagged this as an antler.</summary>
    public bool IsAutoDetected { get; }

    /// <summary>Auto-detected antlers can't be un-designated here (the scan owns
    /// them); only manual designations are user-editable.</summary>
    public bool CanToggle => !IsAutoDetected;

    [Reactive] public bool IsDesignated { get; set; }

    /// <summary>Raised when the user toggles <see cref="IsDesignated"/> (not on
    /// auto-detected rows, which are locked).</summary>
    public event Action<VM_AntlerHeadPartCandidate>? DesignationChanged;

    /// <summary>Raised on row mouse enter (true) / leave (false) for the
    /// viewport highlight.</summary>
    public event Action<VM_AntlerHeadPartCandidate, bool>? HoverChanged;

    public VM_AntlerHeadPartCandidate(NpcMeshResolver.AntlerHeadPartCandidate model)
    {
        FormKey = model.FormKey;
        EditorId = model.EditorId;
        DisplayName = model.DisplayName;
        TypeLabel = model.TypeLabel;
        ShapeNames = model.ShapeNames;
        IsAutoDetected = model.IsAutoDetected;
        IsDesignated = model.IsDesignated;

        this.WhenAnyValue(x => x.IsDesignated)
            .Skip(1) // ignore the initial value
            .Subscribe(_ => { if (CanToggle) DesignationChanged?.Invoke(this); });
    }

    public void RaiseHover(bool entered) => HoverChanged?.Invoke(this, entered);
}
