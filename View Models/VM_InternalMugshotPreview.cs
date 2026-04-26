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
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

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
    private readonly CompositeDisposable _disposables = new();

    public VM_CharacterViewer Viewer { get; }

    /// <summary>The currently-loaded preview NPC's FormKey (or Null if nothing loaded yet).</summary>
    [Reactive] public FormKey CurrentNpcFormKey { get; private set; } = FormKey.Null;
    [Reactive] public string CurrentNpcDisplayLabel { get; private set; } = "(no NPC loaded)";
    [Reactive] public string StatusText { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> LoadSelectedNpcCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadCommand { get; }
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
        EnvironmentStateProvider env)
    {
        Viewer = viewer;
        _settings = settings;
        _npcSelectionBar = npcSelectionBar;
        _env = env;

        LoadSelectedNpcCommand = ReactiveCommand.CreateFromTask(LoadSelectedNpcAsync).DisposeWith(_disposables);
        ReloadCommand = ReactiveCommand.CreateFromTask(ReloadAsync,
            this.WhenAnyValue(x => x.CurrentNpcFormKey, fk => !fk.IsNull)).DisposeWith(_disposables);
        ResetCommand = ReactiveCommand.Create(ResetSettingsToDefaults).DisposeWith(_disposables);
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

    private async Task ReloadAsync()
    {
        if (CurrentNpcFormKey.IsNull) return;
        await LoadAsync(CurrentNpcFormKey);
    }

    public async Task LoadAsync(FormKey formKey)
    {
        if (formKey.IsNull) return;
        if (_env.LinkCache == null)
        {
            StatusText = "Environment not ready yet.";
            return;
        }

        try
        {
            StatusText = $"Loading {formKey}…";
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

    private void ResetSettingsToDefaults()
    {
        var defaults = new InternalMugshotSettings();
        var current = _settings.InternalMugshot;
        // Preserve the user's persisted lighting presets — those aren't tunable
        // via the Reset button, only via the lighting dropdown's Save/Delete.
        defaults.UserLightingLayouts = current.UserLightingLayouts;
        defaults.UserLightingColorSchemes = current.UserLightingColorSchemes;
        _settings.InternalMugshot = defaults;
        StatusText = "Reset to defaults — toggle the panel or reload the preview to refresh.";
        // The VM_Settings flat properties drive the underlying model; we also
        // need to push these defaults back into them for the UI to reflect.
        // The host VM_Settings owns those properties, so it watches a reset signal.
        ResetRequested?.Invoke();
    }

    /// <summary>Raised by the Reset button so the host VM_Settings can
    /// refresh its bound flat properties from the new InternalMugshotSettings.</summary>
    public event Action? ResetRequested;

    public void Dispose()
    {
        _disposables.Dispose();
        Viewer.Dispose();
    }
}
