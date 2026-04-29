using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using CharacterViewer.Rendering;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Wraps a transient <see cref="VM_CharacterViewer"/> for the Settings panel's
/// live mugshot preview. Owns the FormKey-of-NPC-being-previewed, loads it on
/// demand, and exposes the inner VM via <see cref="Viewer"/> so the UC can bind
/// its GL viewport. The Auto/Manual mode plumbing lives in the UC code-behind
/// since it requires direct access to the GLWpfControl mouse events.
/// </summary>
public class VM_InternalMugshotPreview : ReactiveObject, IDisposable
{
    private readonly Settings _settings;
    private readonly Lazy<VM_NpcSelectionBar> _npcSelectionBar;
    private readonly EnvironmentStateProvider _env;
    private readonly NpcMeshResolver _resolver;
    private readonly CompositeDisposable _disposables = new();

    public VM_CharacterViewer Viewer { get; }

    /// <summary>The currently-loaded preview NPC's FormKey (or Null if nothing loaded yet).</summary>
    [Reactive] public FormKey CurrentNpcFormKey { get; private set; } = FormKey.Null;
    [Reactive] public string CurrentNpcDisplayLabel { get; private set; } = "(no NPC loaded)";
    [Reactive] public string StatusText { get; private set; } = "";

    // Last explicit ModSetting for the "Show 3D Preview" popup path. When
    // non-null, GL-context-reset re-fires re-uses this scope; when null,
    // the re-fire falls back to the active selection (Settings-panel path).
    private ModSetting? _lastExplicitModSetting;

    public ReactiveCommand<Unit, Unit> LoadSelectedNpcCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }

    /// <summary>
    /// Raised by the wrapper after a successful load so the hosting UC can
    /// re-apply auto-framing (in Auto mode) or refresh the yellow crop overlay.
    /// </summary>
    public event Action? PreviewLoaded;

    public VM_InternalMugshotPreview(
        VM_CharacterViewer viewer,
        Settings settings,
        Lazy<VM_NpcSelectionBar> npcSelectionBar,
        EnvironmentStateProvider env,
        NpcMeshResolver resolver)
    {
        Viewer = viewer;
        _settings = settings;
        _npcSelectionBar = npcSelectionBar;
        _env = env;
        _resolver = resolver;

        LoadSelectedNpcCommand = ReactiveCommand.CreateFromTask(LoadSelectedNpcAsync).DisposeWith(_disposables);
        ResetCommand = ReactiveCommand.Create(ResetSettingsToDefaults).DisposeWith(_disposables);

        // When the host UC's GL context is reset (WPF recreated the UC; see
        // UC_InternalMugshotPreview.GlControl_OnRender), the renderer drops all
        // GL IDs and the previously-loaded scene with them. Re-fire the last
        // load so the user sees their NPC instead of an empty viewport — they
        // shouldn't need to click Reload after every tab switch.
        Viewer.GlContextReset += OnViewerGlContextReset;

        // Push saved render-pipeline params into the lib viewer once so the
        // shared UC_CharacterViewerRenderPanel (bound to Viewer) shows the
        // user's persisted values from app start, not the lib defaults.
        SyncSettingsToViewer();

        // Mirror edits back: any WhenAnyValue tick on a render-pipeline
        // property writes through to _settings.InternalMugshot.* and asks
        // VM_Settings to throttled-save. Skip(1) so the initial value
        // emission doesn't spam saves before the user has touched anything.
        Viewer.WhenAnyValue(x => x.RenderMissingTextureAsWireframe).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.RenderMissingTextureAsWireframe = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.EnableToneMapping).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.EnableToneMapping = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.EnableShadows).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.EnableShadows = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.EnableAmbientOcclusion).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.EnableAmbientOcclusion = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.SsaoRadius).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.SsaoRadius = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.SsaoBias).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.SsaoBias = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.SsaoIntensity).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.SsaoIntensity = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.EnableEyeCatchlight).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.EnableEyeCatchlight = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.SubsurfaceStrength).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.SubsurfaceStrength = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.VignetteRadius).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.VignetteRadius = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.VignetteIntensity).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.VignetteIntensity = v; PersistThrottled(); }).DisposeWith(_disposables);

        // RenderMissingTextureAsWireframe is consumed at mesh-upload time, so
        // the lib raises ReloadRequested when it changes. Re-load the current
        // NPC so the new wireframe-vs-cull decision applies to already-loaded
        // shapes. Hosts call their own load path here since the lib doesn't
        // know how to resolve scopes / mod settings.
        Viewer.ReloadRequested += OnViewerReloadRequested;
    }

    /// <summary>Pushes every persisted render-pipeline param from
    /// <c>_settings.InternalMugshot</c> into <see cref="Viewer"/>. Called once
    /// at construction (so the shared render panel binds to current values
    /// from the start) and again from VM_Settings.RefreshInternalFromModel
    /// after a Reset so the panel updates without reloading the UC.</summary>
    public void SyncSettingsToViewer()
    {
        var c = _settings.InternalMugshot;
        Viewer.RenderMissingTextureAsWireframe = c.RenderMissingTextureAsWireframe;
        Viewer.EnableToneMapping = c.EnableToneMapping;
        Viewer.EnableShadows = c.EnableShadows;
        Viewer.EnableAmbientOcclusion = c.EnableAmbientOcclusion;
        Viewer.SsaoRadius = c.SsaoRadius;
        Viewer.SsaoBias = c.SsaoBias;
        Viewer.SsaoIntensity = c.SsaoIntensity;
        Viewer.EnableEyeCatchlight = c.EnableEyeCatchlight;
        Viewer.SubsurfaceStrength = c.SubsurfaceStrength;
        Viewer.VignetteRadius = c.VignetteRadius;
        Viewer.VignetteIntensity = c.VignetteIntensity;
    }

    /// <summary>Calls VM_Settings.RequestThrottledSave via Splat. Resolved
    /// lazily because VM_Settings -> VM_InternalMugshotPreview is already a
    /// dependency edge; resolving the reverse via the container instead of
    /// a constructor parameter keeps the cycle implicit.</summary>
    private void PersistThrottled()
    {
        var vmSettings = Locator.Current.GetService<VM_Settings>();
        vmSettings?.RequestThrottledSave();
    }

    private async void OnViewerReloadRequested()
    {
        if (CurrentNpcFormKey.IsNull) return;
        try
        {
            if (_lastExplicitModSetting != null)
                await LoadAsync(CurrentNpcFormKey, _lastExplicitModSetting).ConfigureAwait(false);
            else
                await LoadAsync(CurrentNpcFormKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "VM_InternalMugshotPreview: re-load after ReloadRequested failed: " + ex.Message);
        }
    }

    private async void OnViewerGlContextReset()
    {
        if (CurrentNpcFormKey.IsNull) return;
        try
        {
            // If the last load came through the per-tile popup path, re-fire
            // with the same ModSetting; otherwise fall back to active-selection
            // resolution. Without this branch, the popup would silently switch
            // to the user's globally-selected mod after a context reset.
            if (_lastExplicitModSetting != null)
            {
                await LoadAsync(CurrentNpcFormKey, _lastExplicitModSetting).ConfigureAwait(false);
            }
            else
            {
                await LoadAsync(CurrentNpcFormKey).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "VM_InternalMugshotPreview: re-load after GlContextReset failed: " + ex.Message);
        }
    }

    /// <summary>Loads the NPC currently highlighted in the NPC list (if any).</summary>
    private async Task LoadSelectedNpcAsync()
    {
        var fk = _npcSelectionBar.Value?.SelectedNpc?.NpcFormKey ?? _settings.LastSelectedNpcFormKey;
        if (fk.IsNull)
        {
            StatusText = "No NPC selected — pick one from the NPCs tab first.";
            return;
        }
        await LoadAsync(fk);
    }

    public async Task LoadAsync(FormKey formKey)
    {
        if (formKey.IsNull) return;
        if (_env.LinkCache == null)
        {
            StatusText = "Environment not ready yet.";
            return;
        }

        _lastExplicitModSetting = null;
        try
        {
            StatusText = $"Loading {formKey}…";
            // Strict per-mod asset-resolution chain (vanilla + active mod's
            // CorrespondingFolderPaths/ModKeys). The VM snapshots this
            // property at LoadAsync entry and clears it after SceneCommitted
            // so per-load state can't leak into the next load.
            // AdditionalScopes (1.2.0+) supersedes AdditionalDataFolders.
            Viewer.AdditionalScopes = _resolver.BuildResolutionScopesForActiveSelection(formKey);
            ApplyAdvancedResolutionToggles();
            // The viewer's INpcMeshDataSource adapter parses CacheKey as a FormKey;
            // DisplayLabel is purely diagnostic.
            var identity = new NpcIdentity(formKey.ToString(), formKey.ToString());
            await Viewer.LoadByIdentityAsync(identity);

            CurrentNpcFormKey = formKey;
            CurrentNpcDisplayLabel = formKey.ToString();
            StatusText = $"Loaded {formKey}";
            PreviewLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine("VM_InternalMugshotPreview: " + ExceptionLogger.GetExceptionStack(ex));
        }
    }

    /// <summary>
    /// Loads <paramref name="formKey"/> rendered through the explicit
    /// <paramref name="modSetting"/>'s plugins + folders rather than the
    /// user's globally-selected appearance mod. Used by the per-tile
    /// "Show 3D Preview" popup so each mugshot tile renders the appearance
    /// owned by its own source mod, regardless of the user's active
    /// selection. Bypasses the <see cref="INpcMeshDataSource"/> adapter
    /// (which reads from the consistency provider) by resolving paths +
    /// scopes directly via <see cref="NpcMeshResolver"/> and feeding them
    /// into the underlying viewer's path-accepting <c>LoadAsync</c>.
    /// </summary>
    public async Task LoadAsync(FormKey formKey, ModSetting? modSetting)
    {
        if (formKey.IsNull) return;
        if (_env.LinkCache == null)
        {
            StatusText = "Environment not ready yet.";
            return;
        }

        _lastExplicitModSetting = modSetting;
        try
        {
            StatusText = $"Loading {formKey}…";
            Viewer.AdditionalScopes = _resolver.BuildResolutionScopes(modSetting);
            ApplyAdvancedResolutionToggles();
            var paths = _resolver.Resolve(formKey, modSetting);
            if (paths == null)
            {
                StatusText = $"Could not resolve mesh paths for {formKey}";
                return;
            }

            var identity = new NpcIdentity(formKey.ToString(), formKey.ToString());
            await Viewer.LoadAsync(identity, paths);

            CurrentNpcFormKey = formKey;
            CurrentNpcDisplayLabel = formKey.ToString();
            StatusText = $"Loaded {formKey}";
            PreviewLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine("VM_InternalMugshotPreview: " + ExceptionLogger.GetExceptionStack(ex));
        }
    }

    /// <summary>Pushes the user's current advanced asset-resolution toggles
    /// onto the underlying viewer VM so the next render sees them. Pulled
    /// out so both <see cref="LoadAsync(FormKey)"/> overloads stay
    /// in sync.</summary>
    private void ApplyAdvancedResolutionToggles()
    {
        // Asset-resolution toggles: still pushed at LoadAsync entry because
        // they only matter when the resolver runs. Render-pipeline params
        // are kept in sync continuously by the bridge subscriptions in the
        // constructor (lib UC -> Viewer -> _settings.InternalMugshot.*),
        // so they don't need to be re-pushed here.
        Viewer.VanillaLooseOverridesBsa = _settings.InternalMugshot.VanillaLooseOverridesBsa;
        Viewer.VanillaLooseOverridesModLoose = _settings.InternalMugshot.VanillaLooseOverridesModLoose;
    }

    private void ResetSettingsToDefaults()
    {
        var defaults = new InternalMugshotSettings();
        var current = _settings.InternalMugshot;
        // Preserve the user's persisted lighting presets — those aren't tunable
        // via the Reset button, only via the lighting dropdown's Save/Delete.
        defaults.UserLightingLayouts = current.UserLightingLayouts;
        defaults.UserLightingColorSchemes = current.UserLightingColorSchemes;
        _settings.InternalMugshot = defaults;
        StatusText = "Reset to defaults.";
        // The VM_Settings flat properties drive the underlying model; we also
        // need to push these defaults back into them for the UI to reflect.
        // The host VM_Settings owns those properties, so it watches a reset signal.
        ResetRequested?.Invoke();
    }

    /// <summary>Raised by the Reset button so the host VM_Settings can
    /// refresh its bound flat properties from the new InternalMugshotSettings.</summary>
    public event Action? ResetRequested;

    /// <summary>Raised while the user drags-to-rotate the model in Auto
    /// camera mode. Carries the new yaw/elevation. The host
    /// <c>VM_Settings</c> subscribes and forwards to its bound
    /// <c>InternalYaw</c>/<c>InternalPitch</c> properties so the Yaw and
    /// Pitch textboxes live-update during the drag (and the model gets
    /// written through via the existing WhenAnyValue subscription).</summary>
    public event Action<float, float>? AutoFramingYawPitchDragged;

    /// <summary>Invoked by <see cref="UC_InternalMugshotPreview"/> after each
    /// throttled refit during a drag. See <see cref="AutoFramingYawPitchDragged"/>.</summary>
    public void RaiseAutoFramingYawPitchDragged(float yaw, float pitch)
    {
        AutoFramingYawPitchDragged?.Invoke(yaw, pitch);
    }

    public void Dispose()
    {
        Viewer.GlContextReset -= OnViewerGlContextReset;
        _disposables.Dispose();
        Viewer.Dispose();
    }
}
