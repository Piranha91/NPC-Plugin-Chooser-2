using System;
using System.Windows;
using CharacterViewer.Rendering;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;

/// <summary>
/// Marshals to <see cref="Application.Current"/>'s dispatcher — the WPF UI
/// thread that owns the <c>UC_InternalMugshotPreview</c>'s GL context. The
/// offscreen renderer doesn't use this; it constructs its <see cref="VM_CharacterViewer"/>
/// with the default <c>InlineRenderThreadMarshaller</c>.
/// </summary>
public sealed class WpfDispatcherMarshaller : IRenderThreadMarshaller
{
    public void Invoke(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher == null)
        {
            action();
            return;
        }
        app.Dispatcher.Invoke(action);
    }
}
