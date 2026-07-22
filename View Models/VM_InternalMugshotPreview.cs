using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Text;
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
    private readonly CharacterViewerLogGate _logGate;
    private readonly CompositeDisposable _disposables = new();

    public VM_CharacterViewer Viewer { get; }

    /// <summary>The currently-loaded preview NPC's FormKey (or Null if nothing loaded yet).</summary>
    [Reactive] public FormKey CurrentNpcFormKey { get; private set; } = FormKey.Null;
    [Reactive] public string CurrentNpcDisplayLabel { get; private set; } = "(no NPC loaded)";
    [Reactive] public string StatusText { get; private set; } = "";

    /// <summary>Attire mesh-override warning surface, mirroring the mugshot
    /// tile's missing-asset icon pattern (<see cref="VM_NpcsMenuMugshot"/>).
    /// Set from <see cref="VM_CharacterViewer.MeshOverrideWarnings"/> after each
    /// attire/headgear apply and again on <c>SceneCommitted</c> (the apply may
    /// queue behind a load). Covers unrenderable meshes and meshes rendered on
    /// an incompatible / absent skeleton.</summary>
    [Reactive] public bool HasMissingAssets { get; private set; }
    [Reactive] public string MissingAssetNotificationText { get; private set; } = string.Empty;

    /// <summary>Stale-physics-config surface: the attire mesh links an SMP/HDT
    /// physics XML that doesn't exist (a stale link in the mod itself), but its
    /// physics bones were classified via a sibling config so the render is
    /// correct. Informational only — kept separate from
    /// <see cref="HasMissingAssets"/> because it is NOT a missing asset, gets
    /// its own (placeholder) icon, and must never mark a cached mugshot
    /// stale.</summary>
    [Reactive] public bool HasPhysicsConfigNotice { get; private set; }
    [Reactive] public string PhysicsConfigNoticeText { get; private set; } = string.Empty;

    /// <summary>Outfit-conflict banner: set when "Include Outfit" is overridden
    /// at runtime by a SkyPatcher/SPID config, or (SkyPatcher mode) NPC2's own
    /// ini entry is not conflict-winning. Empty = no banner. The provenance
    /// line (which config supplies the displayed outfit) rides along in the
    /// text so the user can see WHY the preview wears what it wears.</summary>
    [Reactive] public bool HasOutfitNotice { get; private set; }
    [Reactive] public string OutfitNoticeText { get; private set; } = string.Empty;

    /// <summary>Antler-removal banner: set when the mod's effective antler mode is
    /// Remove AND this NPC carries an antler the patch will strip (outfit, worn
    /// armor, or FaceGen head part). The preview hides the antler to match the
    /// patched output; this notice tells the user why it's absent. Informational
    /// only — never marks a cached mugshot stale.</summary>
    [Reactive] public bool HasAntlerRemovalNotice { get; private set; }
    [Reactive] public string AntlerRemovalNoticeText { get; private set; } = string.Empty;

    // --- "Set Antler Head Parts" selector (per-tile 3D popup only) ---
    /// <summary>Whether the antler head-part selector can be offered: the preview
    /// was loaded through an explicit mod (the per-tile 3D popup) and that NPC has
    /// head parts. The Settings-tab embedded preview (active selection) omits it.</summary>
    [Reactive] public bool IsAntlerSelectorAvailable { get; private set; }
    [Reactive] public bool ShowAntlerSelector { get; set; }
    public System.Collections.ObjectModel.ObservableCollection<VM_AntlerHeadPartGroup>
        AntlerHeadPartCandidates { get; } = new();
    public ReactiveCommand<Unit, Unit> ToggleAntlerSelectorCommand { get; }

    private ModSetting? _antlerSelectorModSetting;
    private FormKey _antlerSelectorNpcFormKey = FormKey.Null;
    private bool _suppressAntlerRepopulate;

    // Non-persistent override state for the full-screen popup (see
    // PersistAttireToggles). Seeded from the persisted defaults at construction.
    private bool _localIncludeDefaultOutfit;
    private bool _localIncludeHeadgear;

    /// <summary>When true (default — the Settings-tab embedded preview and the
    /// mugshot defaults), the attire toggles read/write the persisted
    /// <see cref="InternalMugshotSettings"/>. When false (the full-screen 3D
    /// popup), they are NON-persistent overrides seeded from the persisted
    /// defaults; changes affect only this preview instance and never write back
    /// to settings.</summary>
    public bool PersistAttireToggles { get; set; } = true;

    /// <summary>"Include Default Outfit": ON renders the NPC's DefaultOutfit
    /// attire (body armor hides the nude body it covers); OFF is the plain skin
    /// preview.</summary>
    public bool IncludeDefaultOutfit
    {
        get => PersistAttireToggles ? _settings.InternalMugshot.IncludeDefaultOutfit : _localIncludeDefaultOutfit;
        set
        {
            if (IncludeDefaultOutfit == value) return;
            if (PersistAttireToggles)
            {
                _settings.InternalMugshot.IncludeDefaultOutfit = value;
                PersistThrottled();
            }
            else _localIncludeDefaultOutfit = value;
            this.RaisePropertyChanged();
            ReapplyAttireAfterToggle();
        }
    }

    /// <summary>"Include headgear": ON renders worn/outfit head-slot armor with
    /// hair hidden, as in game; OFF (default) shows hair/face.</summary>
    public bool IncludeHeadgear
    {
        get => PersistAttireToggles ? _settings.InternalMugshot.IncludeHeadgear : _localIncludeHeadgear;
        set
        {
            if (IncludeHeadgear == value) return;
            if (PersistAttireToggles)
            {
                _settings.InternalMugshot.IncludeHeadgear = value;
                PersistThrottled();
            }
            else _localIncludeHeadgear = value;
            this.RaisePropertyChanged();
            ReapplyAttireAfterToggle();
        }
    }

    /// <summary>Re-applies the attire overrides on the embedded Settings-tab
    /// preview after the persisted defaults change via the Settings-panel
    /// checkboxes (whose overlay toggles are hidden there). Raises change
    /// notification so any bound toggle UI refreshes.</summary>
    public void ReapplyAttireFromSettings()
    {
        this.RaisePropertyChanged(nameof(IncludeDefaultOutfit));
        this.RaisePropertyChanged(nameof(IncludeHeadgear));
        ReapplyAttireAfterToggle();
    }

    // Last explicit ModSetting for the "Show 3D Preview" popup path. When
    // non-null, GL-context-reset re-fires re-uses this scope; when null,
    // the re-fire falls back to the active selection (Settings-panel path).
    private ModSetting? _lastExplicitModSetting;
    /// <summary>Patch-target FormKey of the last explicit (per-tile) load —
    /// differs from <see cref="CurrentNpcFormKey"/> for guest appearances.
    /// Preserved so toggle/context-reset reloads keep simulating runtime
    /// outfit distribution against the right NPC.</summary>
    private FormKey? _lastExplicitTargetNpcFormKey;

    // Active render-log capture session (see BeginRenderLogSession). Held past
    // LoadAsync because the renderer-side work the log exists to document
    // (scene build, attire mesh overrides, AlternateTextures matching, texture
    // loads) drains on a later render tick; closed on the SceneCommitted that
    // follows the load. The generation counter keeps a stale commit from a
    // superseded load from closing the newer load's session.
    private IDisposable? _renderLogCaptureScope;
    private int _renderLogCaptureGeneration;

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
        NpcMeshResolver resolver,
        CharacterViewerLogGate logGate)
    {
        Viewer = viewer;
        _settings = settings;
        _npcSelectionBar = npcSelectionBar;
        _env = env;
        _resolver = resolver;
        _logGate = logGate;

        // Seed the non-persistent popup override state from the persisted
        // defaults. Harmless for the persistent (Settings-tab) instance, which
        // reads settings directly via PersistAttireToggles.
        _localIncludeDefaultOutfit = settings.InternalMugshot.IncludeDefaultOutfit;
        _localIncludeHeadgear = settings.InternalMugshot.IncludeHeadgear;

        LoadSelectedNpcCommand = ReactiveCommand.CreateFromTask(LoadSelectedNpcAsync).DisposeWith(_disposables);
        ResetCommand = ReactiveCommand.Create(ResetSettingsToDefaults).DisposeWith(_disposables);
        ToggleAntlerSelectorCommand = ReactiveCommand.Create(() => { ShowAntlerSelector = !ShowAntlerSelector; })
            .DisposeWith(_disposables);

        // When the host UC's GL context is reset (WPF recreated the UC; see
        // UC_InternalMugshotPreview.GlControl_OnRender), the renderer drops all
        // GL IDs and the previously-loaded scene with them. Re-fire the last
        // load so the user sees their NPC instead of an empty viewport — they
        // shouldn't need to click Reload after every tab switch.
        Viewer.GlContextReset += OnViewerGlContextReset;

        // Refresh the attire mesh-override warning surface once the scene commits:
        // ApplyMeshOverrides (fired right after a load) queues behind the in-flight
        // rebuild and only drains here, so this is when MeshOverrideWarnings first
        // reflects the real apply result.
        Viewer.SceneCommitted += OnViewerSceneCommitted;

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
        Viewer.WhenAnyValue(x => x.ExcludeHairShadowCaster).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.ExcludeHairShadowCaster = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.SoftenShadowEdges).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.SoftenShadowEdges = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.ShadowPcfRadius).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.ShadowPcfRadius = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.TightShadowFrustum).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.TightShadowFrustum = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.ShadowFrustumRadius).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.ShadowFrustumRadius = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.EnableAmbientOcclusion).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.EnableAmbientOcclusion = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.SsaoRadius).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.SsaoRadius = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.SsaoBias).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.SsaoBias = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.SsaoIntensity).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.SsaoIntensity = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.SsaoThickness).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.SsaoThickness = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.SsaoHairGap).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.SsaoHairGap = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.EnableEyeCatchlight).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.EnableEyeCatchlight = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.SubsurfaceStrength).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.SubsurfaceStrength = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.VignetteRadius).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.VignetteRadius = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.VignetteIntensity).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.VignetteIntensity = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.SkinSaturationBoost).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.SkinSaturationBoost = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.Exposure).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.Exposure = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.TonemapHairRelief).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.TonemapHairRelief = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.HairAlbedoCompensate).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.HairAlbedoCompensate = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.DaylightBoost).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.DaylightBoost = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.DaylightBoostIntensity).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.DaylightBoostIntensity = v; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.EnableBloom).Skip(1)
            .Subscribe(b => { _settings.InternalMugshot.EnableBloom = b; PersistThrottled(); }).DisposeWith(_disposables);
        Viewer.WhenAnyValue(x => x.BloomIntensity).Skip(1)
            .Subscribe(v => { _settings.InternalMugshot.BloomIntensity = v; PersistThrottled(); }).DisposeWith(_disposables);

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
        Viewer.ExcludeHairShadowCaster = c.ExcludeHairShadowCaster;
        Viewer.SoftenShadowEdges = c.SoftenShadowEdges;
        Viewer.ShadowPcfRadius = c.ShadowPcfRadius;
        Viewer.TightShadowFrustum = c.TightShadowFrustum;
        Viewer.ShadowFrustumRadius = c.ShadowFrustumRadius;
        Viewer.EnableAmbientOcclusion = c.EnableAmbientOcclusion;
        Viewer.SsaoRadius = c.SsaoRadius;
        Viewer.SsaoBias = c.SsaoBias;
        Viewer.SsaoIntensity = c.SsaoIntensity;
        Viewer.SsaoThickness = c.SsaoThickness;
        Viewer.SsaoHairGap = c.SsaoHairGap;
        Viewer.EnableEyeCatchlight = c.EnableEyeCatchlight;
        Viewer.SubsurfaceStrength = c.SubsurfaceStrength;
        Viewer.VignetteRadius = c.VignetteRadius;
        Viewer.VignetteIntensity = c.VignetteIntensity;
        Viewer.SkinSaturationBoost = c.SkinSaturationBoost;
        Viewer.Exposure = c.Exposure;
        Viewer.TonemapHairRelief = c.TonemapHairRelief;
        Viewer.HairAlbedoCompensate = c.HairAlbedoCompensate;
        Viewer.DaylightBoost = c.DaylightBoost;
        Viewer.DaylightBoostIntensity = c.DaylightBoostIntensity;
        Viewer.EnableBloom = c.EnableBloom;
        Viewer.BloomIntensity = c.BloomIntensity;
        Viewer.BackgroundColor = System.Windows.Media.Color.FromRgb(
            c.BackgroundR, c.BackgroundG, c.BackgroundB);
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

    /// <summary>Pushes the combined attire/headgear override set through the
    /// renderer's replace-on-reapply mesh channel (one call, full current set),
    /// then refreshes the warning surface. The apply queues behind an in-flight
    /// load and drains on <c>SceneCommitted</c>, where the warning is refreshed
    /// again.</summary>
    private void ApplyAttireOverrides(IReadOnlyList<MeshOverride> overrides)
    {
        Viewer.ApplyMeshOverrides(overrides);
        UpdateMeshOverrideWarning();
    }

    /// <summary>Mirrors <see cref="VM_CharacterViewer.MeshOverrideWarningDetails"/>
    /// onto the two warning surfaces (missing-asset icon vs the informational
    /// stale-physics-config icon). Called after each apply and on scene
    /// commit.</summary>
    private void UpdateMeshOverrideWarning()
    {
        var warnings = Viewer.MeshOverrideWarningDetails;
        var assetWarnings = new List<string>();
        var physicsNotices = new List<string>();
        if (warnings != null)
            foreach (var w in warnings)
                (w.Kind == MeshOverrideWarningKind.StalePhysicsConfig
                    ? physicsNotices : assetWarnings).Add(w.Message);

        if (assetWarnings.Count == 0)
        {
            HasMissingAssets = false;
            MissingAssetNotificationText = string.Empty;
        }
        else
        {
            HasMissingAssets = true;
            MissingAssetNotificationText =
                "Some attire/headgear preview meshes couldn't be rendered correctly "
                + "(mesh not found, missing skinning bones, or an incompatible/absent skeleton):"
                + Environment.NewLine + " - "
                + string.Join(Environment.NewLine + " - ", assetWarnings);
        }

        if (physicsNotices.Count == 0)
        {
            HasPhysicsConfigNotice = false;
            PhysicsConfigNoticeText = string.Empty;
        }
        else
        {
            HasPhysicsConfigNotice = true;
            PhysicsConfigNoticeText =
                "An attire mesh references an SMP/HDT physics config that doesn't exist "
                + "(a stale link inside the mod). The preview is rendered correctly; "
                + "in game the piece's physics likely won't load:"
                + Environment.NewLine + " - "
                + string.Join(Environment.NewLine + " - ", physicsNotices);
        }
    }

    private void OnViewerSceneCommitted()
    {
        // Snapshot the session generation on the event (render) thread; the
        // dispatched close is a no-op if a newer load has begun a new session
        // by the time it runs.
        int captureGeneration = _renderLogCaptureGeneration;
        void Handle()
        {
            UpdateMeshOverrideWarning();
            // The commit that follows a load is when the queued attire
            // overrides have drained (mesh build, AlternateTextures matching,
            // texture loads) — the render-log session can close now that the
            // full apply trace has been captured.
            EndRenderLogSession(captureGeneration);
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(new Action(Handle));
        else
            Handle();
    }

    /// <summary>Re-applies the attire/headgear overrides after a toggle flip.
    /// Goes through a full reload (the renderer's mesh-override apply does GL work
    /// and is only safe to run via the queue/drain path that a load triggers), so
    /// the new toggle state is picked up by <c>LoadAsync</c>'s attire resolve.</summary>
    private async void ReapplyAttireAfterToggle()
    {
        try { await ReloadCurrentAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "VM_InternalMugshotPreview: attire re-apply after toggle failed: " + ex.Message);
        }
    }

    /// <summary>Reloads the current NPC through the same scope it was last loaded
    /// with (explicit per-tile mod, else the active selection).</summary>
    private Task ReloadCurrentAsync()
    {
        if (CurrentNpcFormKey.IsNull) return Task.CompletedTask;
        return _lastExplicitModSetting != null
            ? LoadAsync(CurrentNpcFormKey, _lastExplicitModSetting, _lastExplicitTargetNpcFormKey)
            : LoadAsync(CurrentNpcFormKey);
    }

    private async void OnViewerReloadRequested()
    {
        if (CurrentNpcFormKey.IsNull) return;
        try
        {
            if (_lastExplicitModSetting != null)
                await LoadAsync(CurrentNpcFormKey, _lastExplicitModSetting, _lastExplicitTargetNpcFormKey).ConfigureAwait(false);
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
                await LoadAsync(CurrentNpcFormKey, _lastExplicitModSetting, _lastExplicitTargetNpcFormKey).ConfigureAwait(false);
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
        _lastExplicitTargetNpcFormKey = null;
        BeginRenderLogSession(formKey, modName: "ActiveSelection");
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
            // Resolve mesh paths EXPLICITLY and feed the path-accepting LoadAsync,
            // mirroring the per-tile overload below, instead of going through
            // LoadByIdentityAsync + the INpcMeshDataSource adapter. That identity
            // route memoizes ResolvedNpcMeshPaths in the rendering tier keyed on
            // (FormKey, LinkCache token) — but ResolveForActiveSelection depends on
            // the user's CURRENT appearance selection, which can change without an
            // environment rebuild, so the memo served the PREVIOUS selection's
            // record resolution (head parts / worn armor / TXST) after a switch.
            // Resolution is a cheap record walk; skipping the memo is correct.
            var paths = _resolver.ResolveForActiveSelection(formKey);
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

            // Attire / headgear (Include Default Outfit / Include headgear). Scoped
            // to the user's active appearance selection, matching the mesh load.
            var attire = _resolver.ResolveAttireMeshOverridesForActiveSelection(
                formKey, IncludeDefaultOutfit, IncludeHeadgear, out var outfitDisplay);
            ApplyAttireOverrides(attire);
            ApplyOutfitNotice(outfitDisplay);
            SetAntlerRemovalNotice(_resolver.AntlerRemovalAppliesForActiveSelection(formKey));
            ClearAntlerSelector(); // no explicit mod scope on the active-selection path

            PreviewLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine("VM_InternalMugshotPreview: " + ExceptionLogger.GetExceptionStack(ex));
            // A failed load may never reach SceneCommitted — close the session
            // here so the log gate isn't left forced-on indefinitely.
            EndRenderLogSession(_renderLogCaptureGeneration);
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
    public async Task LoadAsync(FormKey formKey, ModSetting? modSetting, FormKey? targetNpcFormKey = null)
    {
        if (formKey.IsNull) return;
        if (_env.LinkCache == null)
        {
            StatusText = "Environment not ready yet.";
            return;
        }

        _lastExplicitModSetting = modSetting;
        _lastExplicitTargetNpcFormKey = targetNpcFormKey;
        BeginRenderLogSession(formKey, modSetting?.DisplayName ?? "Unscoped");
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

            // Attire / headgear (Include Default Outfit / Include headgear),
            // resolved through this tile's explicit mod scope. targetNpcFormKey
            // (the patch target; differs from formKey for guest appearances)
            // keys the runtime-distribution simulation.
            var attire = _resolver.ResolveAttireMeshOverrides(
                formKey, modSetting, IncludeDefaultOutfit, IncludeHeadgear,
                targetNpcFormKey, out var outfitDisplay);
            ApplyAttireOverrides(attire);
            ApplyOutfitNotice(outfitDisplay);
            SetAntlerRemovalNotice(_resolver.AntlerRemovalApplies(formKey, modSetting));
            PopulateAntlerSelector(modSetting, formKey);

            PreviewLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine("VM_InternalMugshotPreview: " + ExceptionLogger.GetExceptionStack(ex));
            // A failed load may never reach SceneCommitted — close the session
            // here so the log gate isn't left forced-on indefinitely.
            EndRenderLogSession(_renderLogCaptureGeneration);
        }
    }

    /// <summary>Applies the outfit-conflict banner + provenance line from an
    /// effective-outfit resolution. Warnings (Include Outfit overridden /
    /// NPC2's SkyPatcher ini losing) drive the banner; a conflict-free
    /// runtime-distributed outfit only annotates <see cref="StatusText"/> so
    /// the user can still see why the preview wears what it wears.</summary>
    private void ApplyOutfitNotice(BackEnd.OutfitDistribution.OutfitDisplayResult outfitDisplay)
    {
        if (!string.IsNullOrEmpty(outfitDisplay.WarningText))
        {
            var sb = new StringBuilder(outfitDisplay.WarningText);
            foreach (var approx in outfitDisplay.Approximations)
            {
                sb.Append("\nNote: approximated — ").Append(approx);
            }
            OutfitNoticeText = sb.ToString();
            HasOutfitNotice = true;
        }
        else
        {
            OutfitNoticeText = string.Empty;
            HasOutfitNotice = false;
            if (!string.IsNullOrEmpty(outfitDisplay.SourceDetail))
            {
                StatusText += $" — outfit via {outfitDisplay.SourceDetail}";
            }
        }
    }

    /// <summary>Sets the antler-removal banner from the resolver's per-NPC check
    /// (antler mode Remove + this NPC actually carries a strippable antler). The
    /// preview already hides the antler; this explains the absence.</summary>
    private void SetAntlerRemovalNotice(bool applies)
    {
        HasAntlerRemovalNotice = applies;
        AntlerRemovalNoticeText = applies
            ? "Antlers are removed from this NPC in the patched output (this mod's Antler Handling Mode is "
              + "Remove) and are hidden here to match."
            : string.Empty;
    }

    /// <summary>Rebuilds the antler head-part selector list for the loaded NPC
    /// (explicit-mod / popup path only). Skipped during a designation-triggered
    /// reload so the list the user is interacting with isn't rebuilt underneath
    /// them.</summary>
    private void PopulateAntlerSelector(ModSetting? modSetting, FormKey formKey)
    {
        if (_suppressAntlerRepopulate) return;
        ClearAntlerSelectorItems();
        _antlerSelectorModSetting = modSetting;
        _antlerSelectorNpcFormKey = formKey;
        if (modSetting == null)
        {
            IsAntlerSelectorAvailable = false;
            ShowAntlerSelector = false;
            return;
        }

        foreach (var g in _resolver.GetAntlerHeadPartCandidates(formKey, modSetting))
        {
            var group = new VM_AntlerHeadPartGroup(g);
            group.DesignationsChanged += OnGroupDesignationsChanged;
            group.HoverChanged += OnCandidateHoverChanged;
            foreach (var shape in group.ExtraShapes)
                shape.HoverChanged += OnCandidateHoverChanged;
            AntlerHeadPartCandidates.Add(group);
        }
        IsAntlerSelectorAvailable = AntlerHeadPartCandidates.Count > 0;
        if (!IsAntlerSelectorAvailable) ShowAntlerSelector = false;
    }

    private void ClearAntlerSelector()
    {
        ClearAntlerSelectorItems();
        _antlerSelectorModSetting = null;
        IsAntlerSelectorAvailable = false;
        ShowAntlerSelector = false;
    }

    private void ClearAntlerSelectorItems()
    {
        foreach (var group in AntlerHeadPartCandidates)
        {
            group.DesignationsChanged -= OnGroupDesignationsChanged;
            group.HoverChanged -= OnCandidateHoverChanged;
            foreach (var shape in group.ExtraShapes)
                shape.HoverChanged -= OnCandidateHoverChanged;
            group.Detach();
        }
        AntlerHeadPartCandidates.Clear();
    }

    /// <summary>Row hover → glow-highlight the matching baked head shape(s) in the
    /// viewport (cleared on leave). A parent-group row highlights the whole head
    /// part; a shape row highlights just that shape.</summary>
    private void OnCandidateHoverChanged(IAntlerHoverTarget target, bool entered)
    {
        try { Viewer.SetHighlightedShapeNames(entered ? target.ShapeNames : null); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("VM_InternalMugshotPreview: antler highlight failed: " + ex.Message);
        }
    }

    /// <summary>A designation change within a head-part group (the parent
    /// whole-head-part toggle or an ExtraPart child toggle) → re-sync the manual
    /// designations (persisted on Settings, keyed by EditorID: the head part's own
    /// EditorID for whole removal, each ExtraPart's for a single shape), make the
    /// mod's Antler Handling dropdown appear, and reload once so the hides / notice
    /// update.</summary>
    private async void OnGroupDesignationsChanged(VM_AntlerHeadPartGroup group)
    {
        var mod = _antlerSelectorModSetting;
        if (mod == null) return;
        string modName = mod.DisplayName;

        // Parent = "remove whole head part", keyed on the head part's own EditorID.
        if (group.CanToggle && !string.IsNullOrEmpty(group.MainShapeName))
        {
            if (group.IsMainDesignated)
                _settings.AddManualAntlerHeadPart(group.MainShapeName, modName, _antlerSelectorNpcFormKey);
            else
                _settings.RemoveManualAntlerHeadPart(group.MainShapeName, modName, _antlerSelectorNpcFormKey);
        }

        // ExtraPart children, keyed on each ExtraPart's EditorID.
        foreach (var shape in group.ExtraShapes)
        {
            if (!shape.CanToggle || string.IsNullOrEmpty(shape.ShapeName)) continue; // auto shapes owned by the scan
            if (shape.IsDesignated)
                _settings.AddManualAntlerHeadPart(shape.ShapeName, modName, _antlerSelectorNpcFormKey);
            else
                _settings.RemoveManualAntlerHeadPart(shape.ShapeName, modName, _antlerSelectorNpcFormKey);
        }
        PersistThrottled();

        // A manual-only mod needs its Antler Handling dropdown to appear so the
        // user can set Remove.
        Locator.Current.GetService<VM_Mods>()?.RefreshModSettingAntlerState(modName);

        // Reload so HideHeadShapeNames (hide) + the removal notice reflect it,
        // without rebuilding the list the user is clicking in. HideHeadShapeNames
        // is applied at mesh-install time, so the SAME-NPC reload must force a
        // full rebuild (the same-identity short-circuit would otherwise skip it,
        // leaving the shape visible until a close+reopen). Clear the lingering
        // hover glow first — the rebuilt scene has fresh meshes.
        try { Viewer.SetHighlightedShapeNames(null); } catch { /* best-effort */ }
        Viewer.ForceRebuildNextLoad();
        _suppressAntlerRepopulate = true;
        try { await ReloadCurrentAsync(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("VM_InternalMugshotPreview: antler designation reload failed: " + ex.Message);
        }
        finally { _suppressAntlerRepopulate = false; }
    }

    /// <summary>Begins a render-log session for one preview load: closes any
    /// prior session, then opens the capture via
    /// <see cref="MaybeStartRenderLogCapture"/>. Ended by the SceneCommitted
    /// that follows the load (<see cref="OnViewerSceneCommitted"/>), by the
    /// load's catch block, by the next load, or by <see cref="Dispose"/> —
    /// NOT by LoadAsync returning, because the renderer applies the scene and
    /// the attire overrides on a later render tick and the capture must span
    /// that work to document outfit asset resolution.</summary>
    private void BeginRenderLogSession(FormKey formKey, string modName)
    {
        _renderLogCaptureScope?.Dispose();
        _renderLogCaptureGeneration++;
        _renderLogCaptureScope = MaybeStartRenderLogCapture(formKey, modName);
    }

    /// <summary>Closes the active render-log session if
    /// <paramref name="generation"/> still identifies it; a stale generation
    /// (a newer load already began its own session) is a no-op. Always called
    /// on the UI thread (LoadAsync flow or dispatched from SceneCommitted), so
    /// no locking is needed.</summary>
    private void EndRenderLogSession(int generation)
    {
        if (generation != _renderLogCaptureGeneration) return;
        var scope = _renderLogCaptureScope;
        _renderLogCaptureScope = null;
        scope?.Dispose();
    }

    /// <summary>If <see cref="InternalMugshotSettings.LogRenderLogic"/> is on,
    /// opens a per-render capture session writing to
    /// <c>&lt;ExeDir&gt;\RenderLogs\&lt;modName&gt;_&lt;NpcLabel&gt;_Preview.txt</c> and forces the
    /// shared <see cref="CharacterViewerLogGate.Verbose"/> flag on for the
    /// session's duration. Disposing the returned scope flushes the file and
    /// restores the prior verbose state. When the toggle is off, returns a
    /// no-op disposable. Lifecycle is owned by
    /// <see cref="BeginRenderLogSession"/>/<see cref="EndRenderLogSession"/>.</summary>
    private IDisposable MaybeStartRenderLogCapture(FormKey formKey, string modName)
    {
        if (!_settings.InternalMugshot.LogRenderLogic) return EmptyDisposable.Instance;

        // Sanitize all fields — FormKey.ToString() is "xxxxxxxx:Plugin.esp",
        // the colon is illegal in Windows filenames; mod display names and
        // NPC display names can legitimately contain slashes / colons too.
        string npcLabel = _npcSelectionBar.Value?.AllNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(formKey))?.DisplayName
                          ?? formKey.ToString();
        string safeModName = SanitizeForFileName(modName);
        string safeNpcLabel = SanitizeForFileName(npcLabel);
        string folder = Path.Combine(AppContext.BaseDirectory, "RenderLogs");
        // "_Preview" suffix mirrors the offscreen mugshot path's "_Mugshot"
        // suffix so the two render paths' files for the same NPC sort next to
        // each other and are easy to diff when debugging tile-vs-preview
        // discrepancies.
        string filePath = Path.Combine(folder, $"{safeModName}_{safeNpcLabel}_Preview.txt");

        bool prevVerbose = _logGate.Verbose;
        _logGate.Verbose = true;
        var capture = RenderLogCapture.BeginCapture(filePath,
            $"NPC2 Preview Render Log — Mod=[{modName}] NPC=[{formKey}]");

        return new ActionDisposable(() =>
        {
            try { capture.Dispose(); }
            finally { _logGate.Verbose = prevVerbose; }
        });
    }

    private static string SanitizeForFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        }
        return new string(chars);
    }

    private sealed class ActionDisposable : IDisposable
    {
        private Action? _action;
        public ActionDisposable(Action action) { _action = action; }
        public void Dispose() { var a = _action; _action = null; a?.Invoke(); }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
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
        Viewer.SceneCommitted -= OnViewerSceneCommitted;
        ClearAntlerSelectorItems();
        _renderLogCaptureScope?.Dispose();
        _renderLogCaptureScope = null;
        _disposables.Dispose();
        Viewer.Dispose();
    }
}
