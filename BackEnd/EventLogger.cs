using System;
using System.IO;
using System.Text;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class EventLogger
{
    private readonly Settings _settings;
    private const string LogFileName = "EventLog.txt";
    private static readonly object _logLock = new();

    public EventLogger(Settings settings)
    {
        _settings = settings;
        InitializeLog();
    }

    private void InitializeLog()
    {
        // Always clear the log on startup to keep it fresh
        lock (_logLock)
        {
            try
            {
                File.WriteAllText(LogFileName, $"--- NPC Plugin Chooser 2 Event Log ---\nInitialized: {DateTime.Now}\nVersion: {App.ProgramVersion}\n\n");
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
            try
            {
                string logEntry = $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFileName, logEntry);
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
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("================================================================================");
                sb.AppendLine($"  {title.ToUpperInvariant()}");
                sb.AppendLine("================================================================================");
                File.AppendAllText(LogFileName, sb.ToString());
            }
            catch { /* Ignore logging errors */ }
        }
    }
}