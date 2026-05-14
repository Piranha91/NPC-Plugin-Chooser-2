using System;
using System.Threading;
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
    // Per-flow diagnostic sink override. Installed by the offscreen renderer
    // for the duration of each render so the renderer's dedicated render thread
    // (which doesn't inherit the host's outer AsyncLocal flow context) can
    // still reach the per-mugshot RenderLogCapture file the host opened.
    // AsyncLocal (rather than ThreadLocal) is required because vm.LoadAsync
    // internally `await Task.Run(...)`s the NIF parse + texture decode onto
    // thread-pool threads; ThreadLocal would only propagate to the render
    // thread itself and lose the sink for the dominant share of verbose
    // emission (Built shape, [Skinning], [VertexColor], shader properties).
    // AsyncLocal captures the value at the time of the await and replays it
    // on continuation threads, so the sink survives the queue→render-thread→pool
    // hand-off without further plumbing. When non-null, routes through the
    // sink instead of RenderLogCapture.Write — the sink already prefixes with
    // "[CharacterViewer] " (see InternalMugshotGenerator's
    // BuildRenderDiagnosticLog).
    private static readonly AsyncLocal<Action<string>?> _flowSink = new();

    public void LogMessage(string message)
    {
        string line = "[CharacterViewer] " + message;
        System.Diagnostics.Debug.WriteLine(line);
        var sink = _flowSink.Value;
        if (sink != null)
        {
            try { sink(message); } catch { /* writer may be closing */ }
        }
        else
        {
            RenderLogCapture.Write(line);
        }
    }

    public void LogError(string message)
    {
        string line = "[CharacterViewer:ERROR] " + message;
        System.Diagnostics.Debug.WriteLine(line);
        var sink = _flowSink.Value;
        if (sink != null)
        {
            // Preserve the ERROR marker by passing the prefixed line as a raw
            // string; the sink will then re-prefix with "[CharacterViewer] ",
            // yielding "[CharacterViewer] [ERROR] ...". Acceptable degradation
            // versus "[CharacterViewer:ERROR] ..." — errors stay greppable.
            try { sink("[ERROR] " + message); } catch { /* writer may be closing */ }
        }
        else
        {
            RenderLogCapture.Write(line);
        }
    }

    public void LogError(string message, Exception ex)
    {
        string body = message + ": " + ExceptionLogger.GetExceptionStack(ex);
        string line = "[CharacterViewer:ERROR] " + body;
        System.Diagnostics.Debug.WriteLine(line);
        var sink = _flowSink.Value;
        if (sink != null)
        {
            try { sink("[ERROR] " + body); } catch { /* writer may be closing */ }
        }
        else
        {
            RenderLogCapture.Write(line);
        }
    }

    public IDisposable PushDiagnosticSink(Action<string>? sink)
    {
        if (sink == null) return NoOpScope.Instance;
        var prev = _flowSink.Value;
        _flowSink.Value = sink;
        return new SinkScope(prev);
    }

    private sealed class SinkScope : IDisposable
    {
        private readonly Action<string>? _prev;
        private bool _disposed;
        public SinkScope(Action<string>? prev) { _prev = prev; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _flowSink.Value = _prev;
        }
    }

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();
        public void Dispose() { }
    }
}
