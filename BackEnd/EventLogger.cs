using System;
using System.Collections.Generic;
using System.IO;
using NPC_Plugin_Chooser_2.BackEnd.Logging;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// General activity trace (opt-in via Settings.LogActivity), written to EventLog.html next to
/// the exe. The file is recreated on every launch so a stale log can't describe an old session.
/// Appends per call without holding a file handle (matching the txt-era behavior), so the file
/// is always openable while the app runs; the document renders without its closing tags.
/// </summary>
public class EventLogger
{
    private const string LogFileName = "EventLog.html";
    private static readonly object _logLock = new();

    private readonly Settings _settings;
    private readonly string _logPath;
    private bool _initialized;
    private bool _sectionOpen;

    public EventLogger(Settings settings)
    {
        _settings = settings;
        // Anchored to the exe dir — the txt-era logger used a bare relative path, which under a
        // mod-manager launch could land in whatever the process working directory happened to be.
        _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogFileName);
        InitializeLog();
    }

    private void InitializeLog()
    {
        // Always clear the log on startup to keep it fresh
        lock (_logLock)
        {
            try
            {
                File.WriteAllText(_logPath, HtmlLog.Prologue("NPC Plugin Chooser 2 — Event Log", new[]
                {
                    new KeyValuePair<string, string>("Initialized", DateTime.Now.ToString()),
                    new KeyValuePair<string, string>("Version", App.ProgramVersion.ToString()),
                }));
                _initialized = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize EventLog: {ex.Message}");
            }
        }
    }

    public void Log(string message, string category = "INFO")
    {
        if (!_settings.LogActivity) return;

        lock (_logLock)
        {
            if (!_initialized) return;
            try
            {
                File.AppendAllText(_logPath, HtmlLog.Row(
                    HtmlLog.SeverityFromCategory(category),
                    message,
                    time: DateTime.Now.ToString("HH:mm:ss.fff"),
                    chip: category));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EventLogger Write Failed: {ex.Message}");
            }
        }
    }

    public void LogHeader(string title)
    {
        if (!_settings.LogActivity) return;

        lock (_logLock)
        {
            if (!_initialized) return;
            try
            {
                string markup = (_sectionOpen ? HtmlLog.SectionClose : string.Empty)
                                + HtmlLog.SectionOpen(title.ToUpperInvariant());
                File.AppendAllText(_logPath, markup);
                _sectionOpen = true;
            }
            catch { /* Ignore logging errors */ }
        }
    }
}
