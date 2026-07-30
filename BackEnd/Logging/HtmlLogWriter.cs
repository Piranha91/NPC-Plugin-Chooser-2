using System;
using System.Collections.Generic;
using System.IO;

namespace NPC_Plugin_Chooser_2.BackEnd.Logging;

/// <summary>
/// Streaming HTML log writer for the loggers that hold a file open across their lifetime
/// (StartupLogger, MemoryLogger, BsaContentsDiag, the per-NPC diagnostic targets). The document
/// prologue — including all CSS/JS — is written up front and every row is flushed immediately
/// (AutoFlush), so a session that hangs or crashes still leaves a file a browser renders; only
/// the closing tags are missing. <see cref="Dispose"/> writes the epilogue on clean shutdown.
///
/// <para>Not thread-safe by itself — callers keep the same locks they used for the txt writers.</para>
/// </summary>
public sealed class HtmlLogWriter : IDisposable
{
    private readonly StreamWriter _out;
    private readonly bool _tableMode;
    private bool _sectionOpen;
    private bool _disposed;

    /// <summary>Opens <paramref name="path"/> (truncating) and writes the document prologue.
    /// Throws on failure — callers wrap construction in their existing try/catch guards.</summary>
    public HtmlLogWriter(
        string path,
        string title,
        IReadOnlyList<KeyValuePair<string, string>>? meta = null,
        IReadOnlyList<string>? tableColumns = null)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _out = new StreamWriter(stream) { AutoFlush = true };
        _tableMode = tableColumns != null;
        _out.Write(HtmlLog.Prologue(title, meta, tableColumns));
    }

    /// <summary>Writes one row. Optional cells render as columns before the message; optional
    /// fields render as labeled chips after it, and indent shifts the message right.</summary>
    public void WriteRow(HtmlLogSeverity severity, string message,
        string? time = null, string? thread = null, string? chip = null,
        IReadOnlyList<KeyValuePair<string, string>>? fields = null, int indent = 0)
    {
        _out.Write(HtmlLog.Row(severity, message, time, thread, chip, fields, indent));
    }

    /// <summary>Writes one table-mode row.</summary>
    public void WriteTableRow(HtmlLogSeverity severity, IReadOnlyList<string> cells)
    {
        _out.Write(HtmlLog.TableRow(severity, cells));
    }

    /// <summary>Vertical spacer — the HTML replacement for a blank txt line.</summary>
    public void WriteSpacer()
    {
        _out.Write(HtmlLog.Spacer);
    }

    /// <summary>Starts a new collapsible section, closing the previous one if open.
    /// Rows written afterwards land inside it.</summary>
    public void BeginSection(string title)
    {
        if (_sectionOpen) _out.Write(HtmlLog.SectionClose);
        _out.Write(HtmlLog.SectionOpen(title));
        _sectionOpen = true;
    }

    /// <summary>Closes any open section and writes the document epilogue + disposes the stream.
    /// Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_sectionOpen) _out.Write(HtmlLog.SectionClose);
            _out.Write(HtmlLog.Epilogue(_tableMode));
        }
        catch
        {
            // Best-effort: the file renders without the epilogue too.
        }
        finally
        {
            try { _out.Dispose(); } catch { /* swallow */ }
        }
    }
}
