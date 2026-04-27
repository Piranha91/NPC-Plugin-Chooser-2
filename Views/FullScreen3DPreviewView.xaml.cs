using System.Windows;
using System.Windows.Input;
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// Modal popup hosting <see cref="UC_InternalMugshotPreview"/> bound to a
/// per-tile <see cref="VM_FullScreen3DPreview"/>. ESC closes; on close, if
/// the user changed the lighting layout / color scheme via the embedded
/// shared lighting panel (which writes back to global settings live), the
/// window prompts whether to keep the changes globally or revert to the
/// snapshot captured at popup-open.
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
        if (!vm.LightingChanged()) return;

        bool keep = ScrollableMessageBox.Confirm(
            "You changed the lighting setup while previewing this NPC.\n\n" +
            "Save these lighting changes as your global lighting settings?\n\n" +
            "Yes — keep changes globally.\n" +
            "No — revert to the lighting that was active before the preview opened.",
            title: "Save Lighting Changes?");
        if (!keep)
        {
            vm.RevertLightingToSnapshot();
        }
    }
}
