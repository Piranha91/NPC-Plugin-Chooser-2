using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>A row in the "Set Antler Head Parts" selector that can be hovered to
/// glow-highlight the matching baked FaceGen shape(s) in the viewport.</summary>
public interface IAntlerHoverTarget
{
    /// <summary>Baked FaceGen shape name(s) to highlight on hover.</summary>
    IReadOnlyList<string> ShapeNames { get; }
    event Action<IAntlerHoverTarget, bool>? HoverChanged;
    void RaiseHover(bool entered);
}

/// <summary>
/// One ExtraPart baked shape (a child row) in the "Set Antler Head Parts" selector.
/// Checking it strips that single baked shape from the output AND removes the
/// ExtraPart link from the parent head-part record; hovering highlights just this
/// shape. Auto-detected antlers are shown checked and locked
/// (<see cref="CanToggle"/> = false).
/// </summary>
public class VM_AntlerShapeCandidate : ReactiveObject, IAntlerHoverTarget
{
    /// <summary>The baked NIF shape name (= the ExtraPart's EditorID) — the
    /// highlight key AND the manual-designation key (persisted).</summary>
    public string ShapeName { get; }

    public string DisplayName { get; }

    /// <summary>True when the parent head part is keyword-detected as an antler.</summary>
    public bool IsAutoDetected { get; }

    public bool CanToggle => !IsAutoDetected;

    public IReadOnlyList<string> ShapeNames { get; }

    [Reactive] public bool IsDesignated { get; set; }

    /// <summary>Raised when the USER toggles <see cref="IsDesignated"/> (not on
    /// auto-detected rows).</summary>
    public event Action<VM_AntlerShapeCandidate>? UserToggled;

    public event Action<IAntlerHoverTarget, bool>? HoverChanged;

    public VM_AntlerShapeCandidate(NpcMeshResolver.AntlerHeadPartShape model)
    {
        ShapeName = model.ShapeName;
        DisplayName = model.DisplayName;
        IsAutoDetected = model.IsAutoDetected;
        IsDesignated = model.IsDesignated;
        ShapeNames = string.IsNullOrEmpty(ShapeName) ? Array.Empty<string>() : new[] { ShapeName };

        this.WhenAnyValue(x => x.IsDesignated)
            .Skip(1)
            .Subscribe(_ => { if (CanToggle) UserToggled?.Invoke(this); });
    }

    public void RaiseHover(bool entered) => HoverChanged?.Invoke(this, entered);
}

/// <summary>
/// One top-level head part (a parent row) in the "Set Antler Head Parts" selector.
/// The parent checkbox removes the WHOLE head part — its record reference AND every
/// baked shape (keyed on <see cref="MainShapeName"/>, the head part's own EditorID).
/// <see cref="ExtraShapes"/> are its ExtraParts, each an independently strippable
/// child (disabled while the whole head part is being removed, since it goes too).
/// The parent is tri-state for display: checked = whole head part removed,
/// indeterminate = only some ExtraParts stripped, unchecked = nothing.
/// </summary>
public class VM_AntlerHeadPartGroup : ReactiveObject, IAntlerHoverTarget
{
    public FormKey HeadPartFormKey { get; }

    /// <summary>The head part's own EditorID — the "remove whole head part" key.</summary>
    public string MainShapeName { get; }

    public string DisplayName { get; }
    public string TypeLabel { get; }
    public bool IsAutoDetected { get; }
    public bool CanToggle => !IsAutoDetected;

    public ObservableCollection<VM_AntlerShapeCandidate> ExtraShapes { get; } = new();
    public bool HasExtras => ExtraShapes.Count > 0;

    /// <summary>Every shape's name (main + ExtraParts) — hovering the parent
    /// highlights the whole head part in the viewport.</summary>
    public IReadOnlyList<string> ShapeNames { get; }

    /// <summary>Whether the whole head part is designated for removal (record +
    /// all shapes). Set by the parent checkbox.</summary>
    [Reactive] public bool IsMainDesignated { get; set; }

    /// <summary>ExtraPart child rows are editable only while the whole head part is
    /// NOT being removed (removing it takes the ExtraParts with it).</summary>
    public bool ExtrasEnabled => CanToggle && !IsMainDesignated;

    /// <summary>Tri-state parent: true = whole head part removed, null = only some
    /// ExtraParts stripped, false = nothing. Setter toggles the whole-head-part
    /// designation (a two-state checkbox never sets null).</summary>
    public bool? IsChecked
    {
        get => IsMainDesignated ? true : (ExtraShapes.Any(s => s.IsDesignated) ? (bool?)null : false);
        set { if (CanToggle && value != null) IsMainDesignated = value.Value; }
    }

    /// <summary>Raised once after any user-driven designation change in this group
    /// (parent toggle or a child toggle), so the host persists + reloads once.</summary>
    public event Action<VM_AntlerHeadPartGroup>? DesignationsChanged;

    public event Action<IAntlerHoverTarget, bool>? HoverChanged;

    public VM_AntlerHeadPartGroup(NpcMeshResolver.AntlerHeadPartGroup model)
    {
        HeadPartFormKey = model.HeadPartFormKey;
        MainShapeName = model.MainShapeName;
        DisplayName = model.DisplayName;
        TypeLabel = model.TypeLabel;
        IsAutoDetected = model.IsAutoDetected;
        IsMainDesignated = model.IsMainDesignated;

        var names = new List<string>();
        if (!string.IsNullOrEmpty(MainShapeName)) names.Add(MainShapeName);
        foreach (var e in model.ExtraShapes)
        {
            var vm = new VM_AntlerShapeCandidate(e);
            vm.UserToggled += OnChildUserToggled;
            ExtraShapes.Add(vm);
            if (!string.IsNullOrEmpty(vm.ShapeName)) names.Add(vm.ShapeName);
        }
        ShapeNames = names;

        this.WhenAnyValue(x => x.IsMainDesignated)
            .Skip(1)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsChecked));
                this.RaisePropertyChanged(nameof(ExtrasEnabled));
                if (CanToggle) DesignationsChanged?.Invoke(this);
            });
    }

    private void OnChildUserToggled(VM_AntlerShapeCandidate _)
    {
        this.RaisePropertyChanged(nameof(IsChecked));
        DesignationsChanged?.Invoke(this);
    }

    public void RaiseHover(bool entered) => HoverChanged?.Invoke(this, entered);

    /// <summary>Detaches child event handlers; call when clearing the selector.</summary>
    public void Detach()
    {
        foreach (var e in ExtraShapes) e.UserToggled -= OnChildUserToggled;
    }
}
