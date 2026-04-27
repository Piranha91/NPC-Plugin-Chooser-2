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
            if (_viewer != null) _viewer.SceneCommitted -= OnSceneCommitted;
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

    /// <summary>If Auto mode: apply MeshAware framing now. If Manual: attach handlers
    /// and seed Camera with the persisted Manual* values.</summary>
    private void ApplyCameraModeImmediately()
    {
        if (_viewer == null || _settings == null) return;
        var cfg = _settings.InternalMugshot;

        if (cfg.CameraMode == InternalMugshotCameraMode.Auto)
        {
            DetachManualHandlers();
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

    private void ApplyAutoFraming()
    {
        if (_viewer == null || _settings == null) return;
        if (!_viewer.IsSceneReady) return; // Will retry on next load completion.

        var cfg = _settings.InternalMugshot;
        var framing = BuildAutoFramingSpec(cfg);
        int w = Math.Max(1, (int)GlControl.ActualWidth);
        int h = Math.Max(1, (int)GlControl.ActualHeight);
        try
        {
            MeshAwareCameraFitter.ApplyTo(_viewer, framing, w, h);
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

        // Camera orbit: Manual mode only. Auto-mode camera is mesh-fitted by
        // MeshAwareCameraFitter on load; we don't want a click to perturb it.
        if (_settings?.InternalMugshot.CameraMode == InternalMugshotCameraMode.Manual)
        {
            _viewer.Camera.OnMouseDown((float)pos.X, (float)pos.Y,
                e.LeftButton == MouseButtonState.Pressed,
                e.MiddleButton == MouseButtonState.Pressed);
            GlControl.CaptureMouse();
        }
    }

    private void GlControl_MouseMove(object sender, MouseEventArgs e)
    {
        if (_viewer == null) return;
        var pos = e.GetPosition(GlControl);
        _viewer.Camera.OnMouseMove((float)pos.X, (float)pos.Y);
    }

    private void GlControl_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewer == null) return;
        _viewer.Camera.OnMouseUp();
        GlControl.ReleaseMouseCapture();
        // Persist the new orbit state to settings so it survives a restart and
        // the offscreen renderer reads the same values via CameraFraming.OrbitState.
        if (_settings != null)
        {
            var cfg = _settings.InternalMugshot;
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
        _viewer.Camera.OnMouseWheel(e.Delta);
        if (_settings != null)
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

        try
        {
            _viewer.Renderer.Render(_viewer.Camera, w, h);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Viewer.Render failed: " + ex.Message);
        }
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
