using System;
using System.Windows;
using CharacterViewer.Rendering;
using OpenTK.Wpf;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;

/// <summary>
/// Per-viewer marshaller: dispatches to the WPF UI thread <em>and</em> makes the
/// owning <see cref="GLWpfControl"/>'s GL context current before running the
/// action.
///
/// <para>The plain <see cref="WpfDispatcherMarshaller"/> only gets the thread
/// right, which is enough when exactly one GL context exists in the process. It
/// is not enough here: every 3D-preview popup mints its OWN context (
/// <c>UC_InternalMugshotPreview.TryStartGl</c> passes neither <c>ContextToUse</c>
/// nor <c>SharedContext</c>), GL object names are per-context, and two freshly
/// created contexts hand out the SAME low integers for the same allocation
/// sequence. GLWpfControl makes its context current to render and never releases
/// it, so between render callbacks the context current on the UI thread belongs
/// to whichever popup rendered most recently — a coin flip once two are open.
/// GL work issued from there lands in the wrong context and deletes or
/// overwrites the sibling window's meshes and textures by ID collision.</para>
///
/// <para>Making our own context current first pins every such call to the
/// objects this viewer actually owns. Cheap enough to do per call: it is one
/// <c>wglMakeCurrent</c>, and GLWpfControl already issues one per frame.</para>
/// </summary>
public sealed class GlContextPinningMarshaller : IRenderThreadMarshaller
{
    private readonly GLWpfControl _control;

    public GlContextPinningMarshaller(GLWpfControl control)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
    }

    public void Invoke(Action action)
    {
        // Explicit CheckAccess rather than leaning on Dispatcher.Invoke's
        // same-thread fast path: a UI-thread caller (the popup's close handler
        // disposing the viewer, an attire toggle) must never be able to block
        // on the thread it is already running.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            RunPinned(action);
            return;
        }

        dispatcher.Invoke(() => RunPinned(action));
    }

    private void RunPinned(Action action)
    {
        // Null before Start() ran (window closed before the control was ever
        // sized) and after Dispose(). Nothing this viewer owns exists in that
        // case, so run the action unpinned rather than dropping it — the caller
        // may be doing bookkeeping alongside its GL work.
        try
        {
            _control.Context?.MakeCurrent();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "GlContextPinningMarshaller: MakeCurrent failed: " + ex.Message);
        }

        action();
    }
}
