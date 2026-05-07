using System.Windows;
using System.Windows.Input;
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// Modal popup hosting <see cref="UC_InternalMugshotPreview"/> bound to a
/// per-tile <see cref="VM_FullScreen3DPreview"/>. ESC closes; on close, if
/// the user changed any render-affecting field (lighting selection, render
/// quality flags, SSAO/SSS/vignette tunables, skin saturation, background)
/// via the embedded shared panels — all of which write back to global
/// settings live — the window prompts whether to keep the changes globally
/// or revert to the snapshot captured at popup-open. Camera state (auto-
/// mode framing + manual-mode pose) is excluded so per-preview pose
/// adjustments don't trigger the prompt.
/// </summary>
public partial class FullScreen3DPreviewView : ReactiveWindow<VM_FullScreen3DPreview>
{
    public FullScreen3DPreviewView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        var vm = DataContext as VM_FullScreen3DPreview;
        if (vm == null) return;
        if (!vm.RenderSettingsChanged()) return;

        bool keep = ScrollableMessageBox.Confirm(
            "You changed render settings while previewing this NPC.\n\n" +
            "Save these changes as your global render defaults?\n\n" +
            "Yes — keep changes globally.\n" +
            "No — revert to the settings that were active before the preview opened.",
            title: "Save Render Changes?");
        if (!keep)
        {
            vm.RevertRenderSettingsToSnapshot();
        }
    }
}
