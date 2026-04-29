using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CharacterViewer.Rendering;
using CharacterViewer.Rendering.Offscreen;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Wpf;
using Splat;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// Slim live-preview UserControl for the Internal mugshot renderer. Hosts a
/// GLWpfControl bound to a transient <see cref="VM_CharacterViewer"/> exposed
/// by <see cref="VM_InternalMugshotPreview"/>, applies the Auto-mode framing
/// via <see cref="MeshAwareCameraFitter.ApplyTo"/>, attaches mouse-orbit
/// handlers in Manual mode, and overlays a yellow crop rectangle showing
/// where the saved PNG will be framed.
/// </summary>
public partial class UC_InternalMugshotPreview : UserControl
{
    private VM_InternalMugshotPreview? _vm;
    private VM_CharacterViewer? _viewer;
    private Settings? _settings;
    private bool _glStarted;
    private bool _manualHandlersAttached;
    private CameraModeWatcher? _cameraModeWatcher;

    // Auto-mode drag-to-rotate throttle. WPF MouseMove can fire 100+ Hz; the
    // refit is cheap but visible camera "jitter" can creep in if Distance
    // updates outpace the render loop. Capping refits to ~30 Hz during the
    // drag keeps the orbit smooth without lag (camera Az/El still mutate at
    // full mouse-event rate; only the bbox refit is throttled). A final
    // refit fires unconditionally on MouseUp so the resting state is exact.
    private DateTime _lastAutoRefit;
    private const int AutoRefitMinIntervalMs = 33;

    // Suppresses the CameraModeWatcher's react-to-tunables loop while an
    // Auto-mode drag is in flight. The drag's MouseMove handler writes
    // cfg.Yaw/Pitch (so the textboxes live-update), but the watcher polls
    // those same fields every 250 ms and would otherwise call
    // ApplyAutoFraming() with preserveCameraOrientation=false — which
    // overwrites Camera.Az/El with the slightly-stale cfg values, snapping
    // the camera back ~250 ms and causing visible flicker. The drag's own
    // refit (preserveCameraOrientation=true) is the source of truth while
    // the user is dragging; the watcher resumes its normal duty (e.g.
    // typed-into-textbox edits) after MouseUp.
    private bool _isAutoDragging;

    // Multisampled FBO for the live preview viewport. GLWpfControl 4.3.3
    // doesn't expose a Samples / MSAA setting, so we render the scene into
    // our own 4x multisampled FBO and blit the resolve down to GLWpfControl's
    // backbuffer. Without this, alpha-tested cutout edges (hair, eyebrows)
    // look pixel-jagged. Recreated when the viewport resizes or the
    // host's GL context resets.
    private int _msaaFbo = -1;
    private int _msaaColorRbo = -1;
    private int _msaaDepthRbo = -1;
    private (int W, int H) _msaaSize = (0, 0);
    private const int LivePreviewMsaaSamples = 4;

    /// <summary>Visibility of the top toolbar (Load Selected NPC / Reload /
    /// Reset). Defaults to true for the Settings panel; the per-tile 3D
    /// preview popup sets false because Load/Reload/Reset don't make sense
    /// in a one-shot single-NPC popup, and the global Reset would clobber
    /// the user's whole Internal-renderer settings from inside a preview.</summary>
    public bool ShowSettingsControls
    {
        get => SettingsToolbar.Visibility == Visibility.Visible;
        set => SettingsToolbar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>When true, the UC ignores
    /// <c>Settings.InternalMugshot.CameraMode</c> and runs in
    /// "show whole NPC + free orbit" mode: full-body framing on first
    /// scene load, mouse handlers always attached for orbit/zoom, no
    /// persistence of drag state to settings, and the yellow mugshot-crop
    /// overlay is hidden. Used by the per-tile 3D preview popup.</summary>
    public bool IsFullBodyOrbitMode { get; set; }

    // Reset on every UC instance — false until this instance has run its first
    // OnRender. Used to detect the "WPF recreated the UC but the persistent VM
    // still holds GL IDs from the dead context" case (see GlControl_OnRender).
    private bool _firstRenderProcessed;

    public UC_InternalMugshotPreview()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        Loaded += (_, _) => TryStartGl();
        Unloaded += (_, _) =>
        {
            DetachManualHandlers();
            GlControl.MouseDown -= GlControl_MouseDown;
            if (_viewer != null)
            {
                _viewer.SceneCommitted -= OnSceneCommitted;
                _viewer.ReframeRequested -= OnReframeRequested;
            }
            _cameraModeWatcher?.Dispose();
            _cameraModeWatcher = null;
        };

        GlControl.SizeChanged += (_, _) =>
        {
            TryStartGl();
            UpdateCropOverlay();
        };

        // MouseDown is attached permanently so light-arrow picking works in
        // both Auto and Manual camera modes — the gizmos render in either
        // mode whenever Show Lights is on, and a click should pick. The
        // remaining handlers (Move / Up / Wheel) only matter for the
        // camera-orbit drag and stay manual-only.
        GlControl.MouseDown += GlControl_MouseDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachManualHandlers();
        if (_vm != null)
        {
            _vm.PreviewLoaded -= OnPreviewLoaded;
        }
        if (_viewer != null)
        {
            _viewer.SceneCommitted -= OnSceneCommitted;
            _viewer.ReframeRequested -= OnReframeRequested;
        }
        _cameraModeWatcher?.Dispose();
        _cameraModeWatcher = null;

        _vm = DataContext as VM_InternalMugshotPreview;
        _viewer = _vm?.Viewer;

        if (_vm != null)
        {
            _vm.PreviewLoaded += OnPreviewLoaded;
        }
        if (_viewer != null)
        {
            // SceneCommitted fires after VM_CharacterViewer.ProcessPendingScene
            // finishes uploading meshes to the renderer. This is the only reliable
            // signal that vm.Renderer.Meshes is populated, which MeshAwareCameraFitter
            // requires — LoadByIdentityAsync returns before the mesh upload completes.
            _viewer.SceneCommitted += OnSceneCommitted;

            // ReframeRequested fires when the FOV slider moves so the character
            // stays the same on-screen size across FOV changes (mesh-aware
            // distance compensates). Manual mode handles this naturally — the
            // re-apply path just re-seeds Camera from the persisted Manual*
            // values, leaving the orbit untouched while picking up the new FOV.
            _viewer.ReframeRequested += OnReframeRequested;
        }

        // The Settings instance is shared; pull it from the current Application
        // resources via the splat container, but the simpler path is for the
        // hosting view to forward it. NPC2 stashes it on the wrapper VM via the
        // settings model accessible through the InternalMugshot block.
        if (_vm != null)
        {
            _settings = SettingsAccessor.GetSettings();
            _cameraModeWatcher = new CameraModeWatcher(_settings, OnCameraModeChanged, OnAutoTunablesChanged, OnOutputDimsChanged);
            ApplyCameraModeImmediately();
            UpdateCropOverlay();
        }

        TryStartGl();
    }

    private void OnPreviewLoaded()
    {
        // LoadByIdentityAsync returns once the load is queued, NOT once meshes
        // are uploaded — refreshing the crop overlay (a pure-pixel calculation)
        // is fine here, but framing is deferred to OnSceneCommitted so it runs
        // after vm.Renderer.Meshes is populated.
        UpdateCropOverlay();
    }

    private void OnSceneCommitted()
    {
        // SceneCommitted may be raised from the inline marshaller (UI thread for
        // the live preview path) but route through the dispatcher anyway so we
        // can't accidentally clobber an in-progress drag callback.
        if (Dispatcher.CheckAccess())
        {
            ApplyCameraModeImmediately();
            UpdateCropOverlay();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyCameraModeImmediately();
                UpdateCropOverlay();
            }));
        }
    }

    private void OnReframeRequested()
    {
        // Only re-apply framing in Auto mode, where MeshAwareCameraFitter
        // recomputes Camera.Distance from FieldOfView so the character stays
        // the same size on-screen across FOV changes (matching Portrait
        // Creator's slider behavior). In Manual mode the user has explicitly
        // set Distance/Az/El — respect that and let FOV act as a real lens
        // zoom. In Full-Body-Orbit popup mode the user may have orbited /
        // zoomed already; preserving that state is the right call.
        if (_settings == null || _viewer == null) return;
        if (IsFullBodyOrbitMode) return;
        if (_settings.InternalMugshot.CameraMode != InternalMugshotCameraMode.Auto) return;

        if (Dispatcher.CheckAccess())
        {
            ApplyCameraModeImmediately();
            UpdateCropOverlay();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyCameraModeImmediately();
                UpdateCropOverlay();
            }));
        }
    }

    /// <summary>If Auto mode: apply MeshAware framing now. If Manual: attach handlers
    /// and seed Camera with the persisted Manual* values. If
    /// <see cref="IsFullBodyOrbitMode"/>: ignore both modes and apply
    /// full-body orbit defaults — popup-only, no persistence.</summary>
    private void ApplyCameraModeImmediately()
    {
        if (_viewer == null || _settings == null) return;

        if (IsFullBodyOrbitMode)
        {
            ApplyFullBodyOrbit();
            AttachManualHandlers();
            UpdateCropOverlay();
            return;
        }

        var cfg = _settings.InternalMugshot;
        if (cfg.CameraMode == InternalMugshotCameraMode.Auto)
        {
            // Auto mode now supports drag-to-rotate. The mouse handlers run
            // in both modes; the per-handler branches below check
            // CameraMode to decide whether to refit (Auto) or persist
            // ManualDistance / ManualAzimuth / ManualElevation (Manual).
            AttachManualHandlers();
            ApplyAutoFraming();
        }
        else
        {
            // Manual: copy persisted state into the live camera, then attach handlers.
            _viewer.Camera.Distance = cfg.ManualDistance;
            _viewer.Camera.Azimuth = cfg.ManualAzimuth;
            _viewer.Camera.Elevation = cfg.ManualElevation;
            _viewer.Camera.Target = new OpenTK.Mathematics.Vector3(
                cfg.ManualTargetX, cfg.ManualTargetY, cfg.ManualTargetZ);
            AttachManualHandlers();
        }
    }

    /// <summary>Full-body framing for the per-tile 3D preview popup. Asks
    /// <see cref="MeshAwareCameraFitter"/> to tightly fit every loaded
    /// mesh shape (head + body + accessories) so the camera distance
    /// matches the actual model's bounds rather than a fixed multiplier
    /// of nominal Skyrim height. Front-facing, level pitch — the user
    /// can orbit and zoom from there; nothing persists.</summary>
    private void ApplyFullBodyOrbit()
    {
        if (_viewer == null) return;
        if (!_viewer.IsSceneReady) return; // Will retry on next load completion.

        var framing = new CameraFraming.MeshAware(
            new List<FramingShape>
            {
                new() { Selector = FramingShapeSelector.AllLoaded.Instance },
            },
            FrameTopFraction: 0.97f,
            FrameBottomFraction: 0.03f,
            // 180° = front of the model. Yaw is camera-relative, and Skyrim
            // NPCs sit at world origin facing -Z; mugshot Settings.Yaw
            // defaults to 180 for the same reason.
            Yaw: 180f,
            Pitch: 0f);

        int w = Math.Max(1, (int)GlControl.ActualWidth);
        int h = Math.Max(1, (int)GlControl.ActualHeight);
        try
        {
            MeshAwareCameraFitter.ApplyTo(_viewer, framing, w, h);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "MeshAwareCameraFitter.ApplyTo (full body) failed: " + ex.Message);
        }
    }

    private void ApplyAutoFraming(bool preserveCameraOrientation = false)
    {
        if (_viewer == null || _settings == null) return;
        if (!_viewer.IsSceneReady) return; // Will retry on next load completion.

        var cfg = _settings.InternalMugshot;
        var framing = BuildAutoFramingSpec(cfg);
        int w = Math.Max(1, (int)GlControl.ActualWidth);
        int h = Math.Max(1, (int)GlControl.ActualHeight);
        try
        {
            MeshAwareCameraFitter.ApplyTo(_viewer, framing, w, h, preserveCameraOrientation);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("MeshAwareCameraFitter.ApplyTo failed: " + ex.Message);
        }
    }

    private static CameraFraming.MeshAware BuildAutoFramingSpec(InternalMugshotSettings cfg)
    {
        var shapes = new List<FramingShape>
        {
            new() { Selector = FramingShapeSelector.PrimaryHead.Instance },
        };
        if (cfg.IncludeAccessories)
        {
            shapes.Add(new FramingShape
            {
                Selector = FramingShapeSelector.HeadAccessories.Instance,
                Filter = FramingShapeFilter.AboveLowerYOfPrimaryHead.Instance,
                Padding = cfg.HairAbovePadding,
            });
        }
        return new CameraFraming.MeshAware(shapes,
            FrameTopFraction: cfg.HeadTopFraction,
            FrameBottomFraction: cfg.HeadBottomFraction,
            Yaw: cfg.Yaw,
            Pitch: cfg.Pitch);
    }

    private void OnCameraModeChanged() => ApplyCameraModeImmediately();

    private void OnAutoTunablesChanged()
    {
        // Skip while a drag is in flight: the drag is already calling
        // ApplyAutoFraming(preserveCameraOrientation=true) at ~30 Hz, and
        // calling it again with preserve=false here would snap Camera.Az/El
        // back to the slightly-stale cfg.Yaw/Pitch on every watcher tick
        // (visible flicker as the live drag and the watcher fight). After
        // MouseUp the flag clears, the next tick fires through, and any
        // genuine textbox edit picks up the right behavior.
        if (_isAutoDragging) return;
        if (_settings?.InternalMugshot.CameraMode == InternalMugshotCameraMode.Auto)
        {
            ApplyAutoFraming();
        }
    }

    private void OnOutputDimsChanged() => UpdateCropOverlay();

    // -------- Mouse handlers (Manual mode only) --------

    private void AttachManualHandlers()
    {
        if (_manualHandlersAttached) return;
        // MouseDown is permanently attached in the constructor (for light
        // picking in either mode). These three drive the orbit drag and only
        // need to be live in Manual mode.
        GlControl.MouseMove += GlControl_MouseMove;
        GlControl.MouseUp += GlControl_MouseUp;
        GlControl.MouseWheel += GlControl_MouseWheel;
        _manualHandlersAttached = true;
    }

    private void DetachManualHandlers()
    {
        if (!_manualHandlersAttached) return;
        GlControl.MouseMove -= GlControl_MouseMove;
        GlControl.MouseUp -= GlControl_MouseUp;
        GlControl.MouseWheel -= GlControl_MouseWheel;
        _manualHandlersAttached = false;
    }

    private void GlControl_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewer == null) return;
        var pos = e.GetPosition(GlControl);

        // Light-arrow picking: when the gizmos are visible, a left-click that
        // hits an arrow selects that light for editing in the lighting panel
        // and short-circuits the camera orbit. DPI scaling matters because
        // the GL viewport is in physical pixels but pos is in WPF DIPs.
        if (e.ChangedButton == MouseButton.Left && _viewer.ShowLightControls)
        {
            var source = PresentationSource.FromVisual(GlControl);
            double dpiScaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiScaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            int w = (int)(GlControl.ActualWidth * dpiScaleX);
            int h = (int)(GlControl.ActualHeight * dpiScaleY);
            int hit = _viewer.HitTestLightArrow(
                (float)(pos.X * dpiScaleX), (float)(pos.Y * dpiScaleY), w, h);
            if (hit > 0)
            {
                _viewer.SelectedLightIndex = hit;
                e.Handled = true;
                return;
            }
        }

        // Camera orbit. Allowed in:
        //   - Manual mode: full orbit + pan (matches saved ManualTarget).
        //   - Full-body orbit popup: full orbit + pan (transient view).
        //   - Auto mode: orbit only (left-button); pan is suppressed because
        //     MeshAwareCameraFitter re-anchors Camera.Target on every refit,
        //     so any panned target would be immediately undone. The drag
        //     rotates the camera around the bbox center and the move handler
        //     re-fits distance for the new orientation (see MouseMove below).
        bool isAutoMode = !IsFullBodyOrbitMode
            && _settings?.InternalMugshot.CameraMode == InternalMugshotCameraMode.Auto;
        bool orbitAllowed = IsFullBodyOrbitMode
            || _settings?.InternalMugshot.CameraMode == InternalMugshotCameraMode.Manual
            || isAutoMode;
        if (orbitAllowed)
        {
            _viewer.Camera.OnMouseDown((float)pos.X, (float)pos.Y,
                e.LeftButton == MouseButtonState.Pressed,
                // Block middle-button panning in Auto mode — the framer would
                // immediately stomp Camera.Target on the next refit anyway.
                middleButton: !isAutoMode && e.MiddleButton == MouseButtonState.Pressed);
            GlControl.CaptureMouse();
            // Engages the watcher-suppression flag for the duration of the
            // drag; cleared in MouseUp.
            if (isAutoMode && e.LeftButton == MouseButtonState.Pressed)
                _isAutoDragging = true;
        }
    }

    private void GlControl_MouseMove(object sender, MouseEventArgs e)
    {
        if (_viewer == null) return;
        var pos = e.GetPosition(GlControl);
        _viewer.Camera.OnMouseMove((float)pos.X, (float)pos.Y);

        // Auto mode: re-fit distance for the new camera angle so the
        // character stays inside the framing band as the user rotates.
        // Throttled - see _lastAutoRefit comment for rationale. The same
        // throttle gate also drives the Yaw/Pitch textbox live-update so
        // the textbox redraw rate matches the refit rate.
        //
        // Gate on _isAutoDragging so plain cursor hover (no button held)
        // doesn't fire the writeback. Without this gate, hovering the
        // viewport while the scene was still mid-load would push
        // Camera.Elevation (default 15) back into cfg.Pitch, clobbering
        // the user's saved value before SceneCommitted ran.
        if (_isAutoDragging
            && !IsFullBodyOrbitMode
            && _settings?.InternalMugshot.CameraMode == InternalMugshotCameraMode.Auto)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastAutoRefit).TotalMilliseconds >= AutoRefitMinIntervalMs)
            {
                _lastAutoRefit = now;
                ApplyAutoFraming(preserveCameraOrientation: true);
                _vm?.RaiseAutoFramingYawPitchDragged(
                    _viewer.Camera.Azimuth, _viewer.Camera.Elevation);
            }
        }
    }

    private void GlControl_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewer == null) return;
        _viewer.Camera.OnMouseUp();
        GlControl.ReleaseMouseCapture();
        // Snapshot before clearing so the post-drag writeback below can
        // distinguish "real drag just ended" from "non-drag click ended"
        // (e.g. a light-arrow gizmo pick). Without this gate the writeback
        // fires for every click and clobbers cfg.Yaw/Pitch with the
        // current Camera.Az/El even when the user never orbited.
        bool wasDragging = _isAutoDragging;
        _isAutoDragging = false;

        if (_settings == null || IsFullBodyOrbitMode) return;

        var cfg = _settings.InternalMugshot;
        if (wasDragging && cfg.CameraMode == InternalMugshotCameraMode.Auto)
        {
            // Final unthrottled refit so the resting state matches exactly
            // where the user released the mouse, then push the new
            // orientation through VM_Settings so both the bound Yaw/Pitch
            // textboxes and the underlying InternalMugshot.Yaw/Pitch land
            // on the dragged values.
            ApplyAutoFraming(preserveCameraOrientation: true);
            _vm?.RaiseAutoFramingYawPitchDragged(
                _viewer.Camera.Azimuth, _viewer.Camera.Elevation);
        }
        else if (cfg.CameraMode == InternalMugshotCameraMode.Manual)
        {
            // Manual: persist the full orbit state — distance, az/el, and the
            // panned target — so the offscreen renderer reads the same values
            // via CameraFraming.OrbitState.
            cfg.ManualDistance = _viewer.Camera.Distance;
            cfg.ManualAzimuth = _viewer.Camera.Azimuth;
            cfg.ManualElevation = _viewer.Camera.Elevation;
            cfg.ManualTargetX = _viewer.Camera.Target.X;
            cfg.ManualTargetY = _viewer.Camera.Target.Y;
            cfg.ManualTargetZ = _viewer.Camera.Target.Z;
        }
    }

    private void GlControl_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewer == null) return;

        // Auto mode: distance is auto-managed by MeshAwareCameraFitter, so
        // wheel-zoom would be immediately undone on the next refit. Swallow
        // the event so it doesn't bubble to the parent ScrollViewer either.
        if (!IsFullBodyOrbitMode
            && _settings?.InternalMugshot.CameraMode == InternalMugshotCameraMode.Auto)
        {
            e.Handled = true;
            return;
        }

        _viewer.Camera.OnMouseWheel(e.Delta);
        if (_settings != null && !IsFullBodyOrbitMode)
        {
            // Wheel doesn't generate a MouseUp; persist Distance immediately.
            _settings.InternalMugshot.ManualDistance = _viewer.Camera.Distance;
        }
        // Stop the event bubbling to the parent ScrollViewer in SettingsView —
        // otherwise zooming the camera also scrolls the settings page.
        e.Handled = true;
    }

    // -------- GL init --------

    private void TryStartGl()
    {
        if (_glStarted) return;
        if (!IsLoaded) return;
        if (GlControl.ActualWidth <= 0 || GlControl.ActualHeight <= 0) return;

        var settings = new GLWpfControlSettings
        {
            MajorVersion = 3,
            MinorVersion = 3,
            RenderContinuously = true
        };
        GlControl.Start(settings);
        _glStarted = true;

        // GLWpfControl bug workaround: visibility-toggle to force the
        // CompositionTarget.Rendering subscription to register.
        GlControl.Visibility = Visibility.Collapsed;
        GlControl.Visibility = Visibility.Visible;
    }

    private void GlControl_OnRender(TimeSpan delta)
    {
        if (_viewer == null) return;

        // Stale-VM / dead-context detection. GLWpfControl creates a new GL context
        // per UC instance, but VM_InternalMugshotPreview (and its VM_CharacterViewer)
        // is cached on the singleton VM_Settings — so when WPF recreates the
        // SettingsView (e.g. user tabs away and back), this UC is fresh while the
        // VM still holds shader/VAO/VBO/texture IDs minted by the dead context.
        // Render()-ing those IDs in the new context writes 0 pixels, and the user
        // sees an empty viewport. HandleGlContextLoss drops the dead IDs without
        // issuing GL.Delete* against the dead context, clears scene-tracking state,
        // and arms a one-shot rebuild so the next load re-uploads. The companion
        // GlContextReset event triggers VM_InternalMugshotPreview to re-fire its
        // last load so the user doesn't have to click Reload.
        if (!_firstRenderProcessed && _viewer.IsGlInitialized)
        {
            _viewer.HandleGlContextLoss();
            // Drop our private MSAA FBO IDs without issuing GL.Delete*
            // against the dead context — same idea as VM-side context loss.
            _msaaFbo = -1;
            _msaaColorRbo = -1;
            _msaaDepthRbo = -1;
            _msaaSize = (0, 0);
        }

        if (!_viewer.IsGlInitialized)
        {
            try
            {
                _viewer.InitializeGl(ModuleResourceLocator.ShaderDirectory);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Viewer InitializeGl failed: " + ex.Message);
                return;
            }
        }

        _firstRenderProcessed = true;
        _viewer.ProcessPendingScene();

        // Device-pixel viewport size for high-DPI correctness.
        var src = PresentationSource.FromVisual(GlControl);
        double scaleX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        int w = Math.Max(1, (int)(GlControl.ActualWidth * scaleX));
        int h = Math.Max(1, (int)(GlControl.ActualHeight * scaleY));

        // Capture GLWpfControl's currently-bound framebuffer so we can blit
        // the MSAA-resolved image back into it after rendering. The control
        // sets up its own internal FBO before this callback fires.
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int gwpfFbo);

        try
        {
            EnsureLivePreviewMsaaFbo(w, h);

            if (_msaaFbo > 0)
            {
                // Render into our private MSAA FBO. SAMPLE_ALPHA_TO_COVERAGE
                // in GlRenderer's Pass 1 turns hard cutout edges into smooth
                // ones when the bound target is multisampled — that's the
                // smoothness win for hairline alpha edges.
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _msaaFbo);
                GL.Enable(EnableCap.Multisample);
                _viewer.Renderer.Render(_viewer.Camera, w, h);

                // Resolve MSAA → GLWpfControl's backbuffer.
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _msaaFbo);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, gwpfFbo);
                GL.BlitFramebuffer(
                    0, 0, w, h,
                    0, 0, w, h,
                    ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            }
            else
            {
                // MSAA setup failed (FBO incomplete on this driver / size).
                // Fall back to a direct render into GLWpfControl's
                // backbuffer — alpha edges won't be smoothed but at least
                // the preview still draws.
                _viewer.Renderer.Render(_viewer.Camera, w, h);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Viewer.Render failed: " + ex.Message);
        }
        finally
        {
            // Restore GLWpfControl's framebuffer binding so its post-render
            // present sees the surface bound as expected.
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, gwpfFbo);
        }
    }

    /// <summary>Lazily creates / resizes the live-preview multisampled FBO.
    /// Color + depth are renderbuffers (no Texture2D needed since we never
    /// sample from them — just blit-resolve to the GLWpfControl backbuffer).</summary>
    private void EnsureLivePreviewMsaaFbo(int width, int height)
    {
        if (_msaaFbo != -1 && _msaaSize == (width, height)) return;

        DestroyLivePreviewMsaaFbo();

        _msaaFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _msaaFbo);

        _msaaColorRbo = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msaaColorRbo);
        GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer,
            LivePreviewMsaaSamples, RenderbufferStorage.Rgba8, width, height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            RenderbufferTarget.Renderbuffer, _msaaColorRbo);

        _msaaDepthRbo = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msaaDepthRbo);
        GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer,
            LivePreviewMsaaSamples, RenderbufferStorage.Depth24Stencil8, width, height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthStencilAttachment,
            RenderbufferTarget.Renderbuffer, _msaaDepthRbo);

        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
        {
            System.Diagnostics.Debug.WriteLine(
                $"UC_InternalMugshotPreview: live-preview MSAA FBO incomplete: {status} ({width}x{height}, samples={LivePreviewMsaaSamples}). Falling back to direct GLWpfControl render.");
            DestroyLivePreviewMsaaFbo();
            return;
        }

        _msaaSize = (width, height);
    }

    private void DestroyLivePreviewMsaaFbo()
    {
        if (_msaaDepthRbo != -1) { GL.DeleteRenderbuffer(_msaaDepthRbo); _msaaDepthRbo = -1; }
        if (_msaaColorRbo != -1) { GL.DeleteRenderbuffer(_msaaColorRbo); _msaaColorRbo = -1; }
        if (_msaaFbo != -1) { GL.DeleteFramebuffer(_msaaFbo); _msaaFbo = -1; }
        _msaaSize = (0, 0);
    }

    // -------- Yellow crop overlay --------

    /// <summary>
    /// Computes the yellow rect's size + position inside the preview viewport.
    /// Both the live preview and the offscreen render use the same vertical
    /// FOV, so vertical extents always match. The output's aspect ratio
    /// determines how much horizontal width the crop covers within the preview.
    /// </summary>
    private void UpdateCropOverlay()
    {
        if (_settings == null) return;
        // The yellow crop rect previews where the saved mugshot PNG will be
        // framed. Irrelevant in the per-tile popup (which is a free-orbit
        // viewer, not a mugshot composer).
        if (IsFullBodyOrbitMode)
        {
            CropRect.Visibility = Visibility.Collapsed;
            return;
        }
        double prevW = ViewportRoot.ActualWidth;
        double prevH = ViewportRoot.ActualHeight;
        if (prevW <= 0 || prevH <= 0)
        {
            CropRect.Visibility = Visibility.Collapsed;
            return;
        }
        double outW = Math.Max(1, _settings.InternalMugshot.OutputWidth);
        double outH = Math.Max(1, _settings.InternalMugshot.OutputHeight);

        // Same FOV ⇒ vertical extent matches. The output aspect (outW/outH)
        // tells us how much horizontal world is covered relative to vertical.
        // Convert to preview-pixel space: rectH = prevH (or smaller if cropping
        // forced by the preview's narrower aspect); rectW = rectH * outAspect.
        double outAspect = outW / outH;
        double prevAspect = prevW / prevH;

        double rectH;
        double rectW;
        if (outAspect <= prevAspect)
        {
            // Preview is wider than output — crop sits at full preview height,
            // horizontally narrower.
            rectH = prevH;
            rectW = rectH * outAspect;
        }
        else
        {
            // Preview is narrower than output — crop sits at full preview width,
            // vertically shorter.
            rectW = prevW;
            rectH = rectW / outAspect;
        }

        double left = (prevW - rectW) * 0.5;
        double top = (prevH - rectH) * 0.5;

        OverlayCanvas.Width = prevW;
        OverlayCanvas.Height = prevH;
        CropRect.Width = Math.Max(0, rectW);
        CropRect.Height = Math.Max(0, rectH);
        Canvas.SetLeft(CropRect, left);
        Canvas.SetTop(CropRect, top);
        CropRect.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Watches the in-process Settings model for changes that should refresh
    /// the preview camera or the crop overlay. Settings is a plain POCO without
    /// INPC; we hook VM_Settings for reactive notifications via the wrapper VM.
    /// For now we just poll on the Settings instance via short-lived inspection
    /// — the host VM_Settings already raises PropertyChanged on its own flat
    /// properties when sliders move, so we subscribe through that.
    /// </summary>
    private sealed class CameraModeWatcher : IDisposable
    {
        private readonly Settings _settings;
        private readonly Action _onCameraModeChanged;
        private readonly Action _onAutoTunablesChanged;
        private readonly Action _onOutputDimsChanged;
        private InternalMugshotCameraMode _lastMode;
        private float _lastTopFrac, _lastBotFrac, _lastYaw, _lastPitch, _lastPad;
        private bool _lastAccessories;
        private int _lastOutW, _lastOutH;
        private System.Windows.Threading.DispatcherTimer _timer;

        public CameraModeWatcher(Settings settings, Action onMode, Action onAuto, Action onOut)
        {
            _settings = settings;
            _onCameraModeChanged = onMode;
            _onAutoTunablesChanged = onAuto;
            _onOutputDimsChanged = onOut;
            Snapshot();
            _timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        private void Snapshot()
        {
            var c = _settings.InternalMugshot;
            _lastMode = c.CameraMode;
            _lastTopFrac = c.HeadTopFraction;
            _lastBotFrac = c.HeadBottomFraction;
            _lastYaw = c.Yaw;
            _lastPitch = c.Pitch;
            _lastPad = c.HairAbovePadding;
            _lastAccessories = c.IncludeAccessories;
            _lastOutW = c.OutputWidth;
            _lastOutH = c.OutputHeight;
        }

        private void Tick()
        {
            var c = _settings.InternalMugshot;
            if (c.CameraMode != _lastMode)
            {
                _lastMode = c.CameraMode;
                _onCameraModeChanged();
            }
            if (c.HeadTopFraction != _lastTopFrac
                || c.HeadBottomFraction != _lastBotFrac
                || c.Yaw != _lastYaw
                || c.Pitch != _lastPitch
                || c.HairAbovePadding != _lastPad
                || c.IncludeAccessories != _lastAccessories)
            {
                _lastTopFrac = c.HeadTopFraction;
                _lastBotFrac = c.HeadBottomFraction;
                _lastYaw = c.Yaw;
                _lastPitch = c.Pitch;
                _lastPad = c.HairAbovePadding;
                _lastAccessories = c.IncludeAccessories;
                _onAutoTunablesChanged();
            }
            if (c.OutputWidth != _lastOutW || c.OutputHeight != _lastOutH)
            {
                _lastOutW = c.OutputWidth;
                _lastOutH = c.OutputHeight;
                _onOutputDimsChanged();
            }
        }

        public void Dispose()
        {
            _timer.Stop();
        }
    }
}

/// <summary>
/// Exposes the current <see cref="Settings"/> model registered in the Splat /
/// Autofac container so the UC code-behind can read it without taking a
/// constructor dependency (UCs are XAML-instantiated with no DI).
/// </summary>
internal static class SettingsAccessor
{
    public static Settings GetSettings() =>
        Locator.Current.GetService<Settings>()
            ?? throw new InvalidOperationException("Settings not registered with Splat.");
}
