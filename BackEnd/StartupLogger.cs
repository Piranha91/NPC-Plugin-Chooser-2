using System;
using System.IO;
using System.Threading;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// A static logger that writes startup diagnostics with immediate flush to survive hangs.
/// Initialized before DI so it can capture the entire startup sequence.
/// </summary>
public static class StartupLogger
{
    private static StreamWriter? _writer;
    private static readonly object _lock = new();
    private static bool _isEnabled;

    private const string TriggerFileName = "LogStartup.txt";
    private const string LogFileName = "StartupLog.txt";

    /// <summary>
    /// Checks for the file-based trigger (LogStartup.txt adjacent to exe) and opens the log stream if found.
    /// Call this at the very top of OnStartup, before anything else.
    /// </summary>
    public static void InitializeFromFileTrigger()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string triggerPath = Path.Combine(baseDir, TriggerFileName);

        if (File.Exists(triggerPath))
        {
            Enable();
        }
    }

    /// <summary>
    /// Enables startup logging if the LogStartup setting is true.
    /// Call this after settings are loaded from disk.
    /// </summary>
    public static void InitializeFromSettings(bool logStartup)
    {
        if (logStartup && !_isEnabled)
        {
            Enable();
        }
    }

    private static void Enable()
    {
        lock (_lock)
        {
            if (_isEnabled) return;

            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogFileName);
                var stream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream) { AutoFlush = true };
                _isEnabled = true;

                _writer.WriteLine($"=== NPC Plugin Chooser 2 - Startup Log ===");
                _writer.WriteLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                _writer.WriteLine($"Version: {App.ProgramVersion}");
                _writer.WriteLine($"OS: {Environment.OSVersion}");
                _writer.WriteLine($"Processors: {Environment.ProcessorCount}");
                _writer.WriteLine();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize StartupLogger: {ex.Message}");
                _isEnabled = false;
            }
        }
    }

    public static bool IsEnabled => _isEnabled;

    /// <summary>
    /// Logs a message with timestamp and thread ID. Flushes immediately.
    /// </summary>
    public static void Log(string message, string category = "INFO")
    {
        if (!_isEnabled) return;

        lock (_lock)
        {
            try
            {
                _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Thread {Thread.CurrentThread.ManagedThreadId,3}] [{category}] {message}");
            }
            catch
            {
                // Swallow — logging must never crash the app
            }
        }
    }

    /// <summary>
    /// Logs a phase header to visually separate startup stages.
    /// </summary>
    public static void LogPhase(string title)
    {
        if (!_isEnabled) return;

        lock (_lock)
        {
            try
            {
                _writer?.WriteLine();
                _writer?.WriteLine($"──── {title.ToUpperInvariant()} ────");
            }
            catch { }
        }
    }

    /// <summary>
    /// Logs that startup completed without a mods folder, but keeps the log stream open
    /// so that initialization after the user selects a mods folder is also captured.
    /// </summary>
    public static void DeferCompletion()
    {
        if (!_isEnabled) return;

        lock (_lock)
        {
            try
            {
                _writer?.WriteLine();
                _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Thread {Thread.CurrentThread.ManagedThreadId,3}] [INFO] No mods folder configured — deferring log completion until folder is selected.");
            }
            catch { }
        }
    }

    /// <summary>
    /// Marks startup as complete and closes the log stream.
    /// </summary>
    public static void Complete()
    {
        if (!_isEnabled) return;

        lock (_lock)
        {
            try
            {
                _writer?.WriteLine();
                _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Thread {Thread.CurrentThread.ManagedThreadId,3}] [DONE] Startup complete.");
                _writer?.Dispose();
                _writer = null;
                _isEnabled = false;
            }
            catch { }
        }
    }
}
