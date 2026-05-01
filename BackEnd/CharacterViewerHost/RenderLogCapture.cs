using System;
using System.IO;
using System.Threading;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Capture sink for the Internal renderer's per-render diagnostic trace. Two
/// scopes are supported:
///
/// <list type="bullet">
/// <item><b>Global single-session</b> (<see cref="BeginCapture"/>) — used by
/// the live-preview path. One file at a time, replaces any prior session.
/// Captures messages from any thread.</item>
/// <item><b>AsyncLocal flow-scoped</b> (<see cref="BeginScopedCapture"/>) — used
/// by the offscreen mugshot path. Multiple sessions can be active concurrently;
/// each captures only the messages emitted on its own async flow. Lets parallel
/// mugshot renders write to separate files instead of interleaving.</item>
/// </list>
///
/// <para><see cref="Write"/> dispatches to the AsyncLocal session first; if
/// none is bound on the current flow, it writes to the global session if one
/// exists. Threads that the renderer spawns without preserving the AsyncLocal
/// context (e.g. internal worker pools) fall back to the global, so when both
/// are active those messages land in the live-preview's file. Acceptable for
/// the diagnostic — the host-side resolver trace is captured cleanly either way.</para>
///
/// <para>All operations are best-effort: file IO failures swallow silently so
/// a missing-permissions log directory can't break the render itself.</para>
/// </summary>
public static class RenderLogCapture
{
    private static readonly object _globalLock = new();
    private static StreamWriter? _globalWriter;

    private static readonly AsyncLocal<StreamWriter?> _flowWriter = new();

    /// <summary>Opens a global single-session capture. Closes any prior global
    /// session first. Returns an <see cref="IDisposable"/> that ends the
    /// session — call from a <c>using</c> or finally.</summary>
    public static IDisposable BeginCapture(string filePath, string headerLine)
    {
        var writer = TryOpen(filePath, headerLine);
        lock (_globalLock)
        {
            EndGlobalLocked();
            _globalWriter = writer;
        }
        return new GlobalScope();
    }

    /// <summary>Opens an AsyncLocal flow-scoped capture. The writer is
    /// installed on the calling logical-call-context so messages emitted on
    /// async continuations of this flow land here; other concurrent flows are
    /// unaffected. Disposing the scope flushes the file and restores any
    /// outer flow-scoped writer (sessions can nest).</summary>
    public static IDisposable BeginScopedCapture(string filePath, string headerLine)
    {
        var writer = TryOpen(filePath, headerLine);
        if (writer == null) return EmptyDisposable.Instance;
        var prev = _flowWriter.Value;
        _flowWriter.Value = writer;
        return new FlowScope(writer, prev);
    }

    /// <summary>Appends <paramref name="message"/> to the active session's
    /// file. AsyncLocal flow takes precedence; falls back to the global
    /// session. No-op when neither is active. Thread-safe.</summary>
    public static void Write(string message)
    {
        var flow = _flowWriter.Value;
        if (flow != null)
        {
            try { lock (flow) flow.WriteLine(message); }
            catch { /* writer may be closing */ }
            return;
        }
        lock (_globalLock)
        {
            if (_globalWriter == null) return;
            try { _globalWriter.WriteLine(message); }
            catch { /* writer may be closing */ }
        }
    }

    /// <summary>True when any capture session is active on the current flow
    /// or globally. Hot-path callers can check this to skip building expensive
    /// trace strings when nobody's listening.</summary>
    public static bool IsCapturing
    {
        get
        {
            if (_flowWriter.Value != null) return true;
            lock (_globalLock) return _globalWriter != null;
        }
    }

    private static StreamWriter? TryOpen(string filePath, string headerLine)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var w = new StreamWriter(filePath, append: false) { AutoFlush = true };
            w.WriteLine(headerLine);
            w.WriteLine("Started: " + DateTime.Now.ToString("o"));
            w.WriteLine();
            return w;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[RenderLogCapture] Open failed: " + ex.Message);
            return null;
        }
    }

    private static void EndGlobalLocked()
    {
        if (_globalWriter == null) return;
        CloseWriter(_globalWriter);
        _globalWriter = null;
    }

    private static void CloseWriter(StreamWriter w)
    {
        try
        {
            lock (w)
            {
                w.WriteLine();
                w.WriteLine("Ended: " + DateTime.Now.ToString("o"));
                w.Flush();
                w.Dispose();
            }
        }
        catch { /* swallow */ }
    }

    private sealed class GlobalScope : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_globalLock) EndGlobalLocked();
        }
    }

    private sealed class FlowScope : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly StreamWriter? _prev;
        private bool _disposed;
        public FlowScope(StreamWriter writer, StreamWriter? prev)
        {
            _writer = writer;
            _prev = prev;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CloseWriter(_writer);
            // Restore outer flow's writer (or null). Setting AsyncLocal here
            // only affects the current flow, so concurrent peers are intact.
            _flowWriter.Value = _prev;
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
