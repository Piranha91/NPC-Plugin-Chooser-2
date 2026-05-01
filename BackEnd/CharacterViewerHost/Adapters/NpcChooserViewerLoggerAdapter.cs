using System;
using CharacterViewer.Rendering;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;

/// <summary>
/// Routes the CharacterViewer subsystem's diagnostic messages into NPC2's
/// existing log sink. NPC2 uses a mix of Debug.WriteLine and EventLogger;
/// this adapter forwards through System.Diagnostics.Debug so messages show up
/// in the VS output window during development. EventLogger expects an opcode
/// and isn't a great fit for the viewer's free-form messages.
/// </summary>
public sealed class NpcChooserViewerLoggerAdapter : ICharacterViewerLogger
{
    public void LogMessage(string message)
    {
        string line = "[CharacterViewer] " + message;
        System.Diagnostics.Debug.WriteLine(line);
        RenderLogCapture.Write(line);
    }

    public void LogError(string message)
    {
        string line = "[CharacterViewer:ERROR] " + message;
        System.Diagnostics.Debug.WriteLine(line);
        RenderLogCapture.Write(line);
    }

    public void LogError(string message, Exception ex)
    {
        string line = "[CharacterViewer:ERROR] " + message + ": " +
            ExceptionLogger.GetExceptionStack(ex);
        System.Diagnostics.Debug.WriteLine(line);
        RenderLogCapture.Write(line);
    }
}
