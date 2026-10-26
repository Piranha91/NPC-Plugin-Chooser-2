using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using CharacterViewer.Rendering;
using OpenTK.Mathematics;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>How a locked group maps the moved tile's camera onto the others.</summary>
public enum LiveTilePivotMode
{
    /// <summary>Each tile orbits its OWN original framing center: the leader's
    /// change relative to its framing is replayed relative to each member's
    /// framing. Keeps every face where its mugshot put it, whatever the NPCs'
    /// heights or head sizes.</summary>
    Original,
    /// <summary>Every tile takes the leader's absolute camera (same pivot point,
    /// distance and angles in world space), so NPCs of different heights sit
    /// at different heights in frame — a true side-by-side scale comparison.</summary>
    Shared
}

/// <summary>
/// "Lock 3D View" state for one view's live tiles (NPCs menu, Mods menu, or a
/// Compare window). Each <see cref="Views.UC_LiveTileViewport"/> under the view
/// joins with its camera via the inherited
/// <see cref="Views.UC_LiveTileViewport.SyncGroupProperty"/>; while
/// <see cref="IsLocked"/> is set, a user orbit/pan/zoom on any tile is mirrored
/// onto every other member according to <see cref="PivotMode"/>.
///
/// Only USER changes are mirrored (the viewport calls
/// <see cref="PublishUserChange"/> from its mouse handlers) rather than
/// subscribing to <see cref="OrbitCamera.ViewChanged"/>: that event also fires
/// for the programmatic mugshot framing applied when a tile's scene commits, so
/// turning one more tile live would snap every locked tile back to its framing.
/// A tile that loads into a locked group frames itself first (recording that as
/// its baseline via <see cref="SetBaseline"/>), then adopts the leader's view via
/// <see cref="TryAdoptLockedView"/>.
/// </summary>
public sealed class LiveTileCameraSyncGroup : ReactiveObject
{
    private readonly record struct Pose(float Azimuth, float Elevation, float Distance, Vector3 Target);

    private readonly List<OrbitCamera> _members = new();

    // Each member's own mugshot framing — the reference Original mode measures
    // the leader's change against and replays it from.
    private readonly Dictionary<OrbitCamera, Pose> _baselines = new();

    // The camera the user last moved: the view a locked group converges on.
    // Cleared when its tile leaves (e.g. NPC switch), so a stale pose from a
    // different NPC's tiles is never imposed on the next set.
    private OrbitCamera? _leader;

    /// <summary>Not persisted: every session (and every Compare window) starts locked.</summary>
    [Reactive] public bool IsLocked { get; set; } = true;

    /// <summary>Not persisted; toggled from a live tile's context menu.</summary>
    [Reactive] public LiveTilePivotMode PivotMode { get; set; } = LiveTilePivotMode.Original;

    [ObservableAsProperty] public string TogglePivotHeader { get; }

    public ReactiveCommand<Unit, Unit> TogglePivotCommand { get; }

    public LiveTileCameraSyncGroup()
    {
        TogglePivotCommand = ReactiveCommand.Create(() =>
        {
            PivotMode = PivotMode == LiveTilePivotMode.Original
                ? LiveTilePivotMode.Shared
                : LiveTilePivotMode.Original;
        });

        this.WhenAnyValue(x => x.PivotMode)
            .Select(mode => mode == LiveTilePivotMode.Original
                ? "Toggle Pivot (now: each tile's own center)"
                : "Toggle Pivot (now: shared pivot)")
            .ToPropertyEx(this, x => x.TogglePivotHeader);

        // Re-locking or switching pivot snaps everyone to the last-moved tile
        // under the new rule, so the change is visible immediately rather than
        // on the next drag.
        this.WhenAnyValue(x => x.IsLocked, x => x.PivotMode)
            .Skip(1)
            .Where(t => t.Item1)
            .Subscribe(_ => { if (_leader != null) Broadcast(_leader); });
    }

    public void Join(OrbitCamera camera)
    {
        if (!_members.Contains(camera)) _members.Add(camera);
    }

    public void Leave(OrbitCamera camera)
    {
        _members.Remove(camera);
        _baselines.Remove(camera);
        if (ReferenceEquals(_leader, camera)) _leader = null;
    }

    /// <summary>Records <paramref name="camera"/>'s current pose as its own
    /// framing. Called right after the tile applies its mugshot framing.</summary>
    public void SetBaseline(OrbitCamera camera)
    {
        if (!_members.Contains(camera)) return;
        _baselines[camera] = Capture(camera);
    }

    /// <summary>Called after the user moves <paramref name="source"/>'s camera.</summary>
    public void PublishUserChange(OrbitCamera source)
    {
        _leader = source;
        if (IsLocked) Broadcast(source);
    }

    /// <summary>
    /// When locked and another member has been moved by the user, maps that
    /// view onto <paramref name="target"/> and returns true.
    /// </summary>
    public bool TryAdoptLockedView(OrbitCamera target)
    {
        if (!IsLocked || _leader == null || ReferenceEquals(_leader, target)) return false;
        return ApplyLeaderView(_leader, target);
    }

    private void Broadcast(OrbitCamera source)
    {
        foreach (var member in _members)
        {
            if (!ReferenceEquals(member, source)) ApplyLeaderView(source, member);
        }
    }

    private bool ApplyLeaderView(OrbitCamera leader, OrbitCamera member)
    {
        if (PivotMode == LiveTilePivotMode.Shared)
        {
            member.CopyViewFrom(leader);
            return true;
        }

        // Original pivot: replay the leader's delta from its own framing onto
        // this member's framing. Either side still loading (no baseline yet)
        // is skipped; the member catches up when its framing lands.
        if (!_baselines.TryGetValue(leader, out var leaderBase)
            || !_baselines.TryGetValue(member, out var memberBase)
            || leaderBase.Distance <= 0f)
        {
            return false;
        }

        // Zoom is proportional and pan is scaled by the framing distances, so a
        // member framed closer (smaller head) moves the same on-screen amount.
        float scale = memberBase.Distance / leaderBase.Distance;
        member.Azimuth = memberBase.Azimuth + (leader.Azimuth - leaderBase.Azimuth);
        member.Elevation = Math.Clamp(memberBase.Elevation + (leader.Elevation - leaderBase.Elevation), -89f, 89f);
        member.Distance = Math.Clamp(memberBase.Distance * (leader.Distance / leaderBase.Distance),
            member.MinDistance, member.MaxDistance);
        member.Target = memberBase.Target + (leader.Target - leaderBase.Target) * scale;
        return true;
    }

    private static Pose Capture(OrbitCamera c) => new(c.Azimuth, c.Elevation, c.Distance, c.Target);
}
