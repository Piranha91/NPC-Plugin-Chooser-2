using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Opt-in, per-NPC diagnostic logger. The user picks a set of NPCs (from the
/// ones they've made a selection for) in the Settings &gt; Logging panel; for
/// each of those NPCs the <see cref="Validator"/> and <see cref="Patcher"/>
/// emit a full activity trace — every modification to the NPC record plus the
/// dependency merge-in logic (discovered records, remaps, abort conditions) —
/// to <c>{exe}\NPC Logs\{display}.txt</c>.
///
/// <para>Lifecycle: <see cref="Configure"/> is called once at the start of a
/// Validate/Patch run with the NPCs to log; it discards any previously open
/// files and arms a fresh per-NPC target whose file is truncated (overwritten)
/// on its first write this run. Both the validation and patching phases then
/// append to the same per-NPC file for the remainder of the run.
/// <see cref="Shutdown"/> closes the handles in the run's finally block.</para>
///
/// <para>Writes flow only while a <see cref="BeginNpc"/>/<see cref="EndNpc"/>
/// context is active and the active NPC is in the configured set, so call sites
/// stay cheap (a bool + dictionary probe) when logging is off. The active NPC
/// is tracked in an <see cref="AsyncLocal{T}"/> so it flows across the per-NPC
/// <c>Task.Run</c> in the patch loop without threading the FormKey through every
/// <see cref="RecordHandler"/> method.</para>
///
/// <para>Static, mirroring <c>StartupLogger</c> / <c>BsaContentsDiag</c>, so the
/// deep merge-in code paths can log without taking a constructor dependency.</para>
/// </summary>
public static class NpcDiagnosticLogger
{
    private const string LogFolderName = "NPC Logs";
    private const int MaxFileNameLength = 150;

    private static readonly object _lock = new();
    // Replaced wholesale (atomic reference swap) by Configure so the hot-path
    // reads below can snapshot it without locking.
    private static Dictionary<FormKey, NpcLogTarget> _targets = new();
    private static readonly AsyncLocal<FormKey?> _currentNpc = new();

    /// <summary>True when at least one NPC is configured for logging. Hot-path
    /// guard so <see cref="Log"/> / <see cref="IsActive"/> are a single bool
    /// check when nobody's listening.</summary>
    public static bool IsEnabled { get; private set; }

    /// <summary>True when logging is enabled, a per-NPC context is active, and
    /// the active NPC is in the configured set — i.e. a <see cref="Log"/> call
    /// right now would actually write. Use this to gate expensive log-string
    /// construction on the merge-in hot paths.</summary>
    public static bool IsActive
    {
        get
        {
            if (!IsEnabled) return false;
            var fk = _currentNpc.Value;
            if (fk == null) return false;
            return _targets.ContainsKey(fk.Value);
        }
    }

    /// <summary>Resets all per-NPC log targets to the supplied set. Call once at
    /// the start of a Validate/Patch run, before any <see cref="BeginNpc"/> /
    /// <see cref="Log"/> calls. Each target's file is truncated on its first
    /// write this run. Passing null or an empty set disables logging.</summary>
    public static void Configure(IEnumerable<(FormKey FormKey, string DisplayString)>? npcsToLog)
    {
        lock (_lock)
        {
            foreach (var t in _targets.Values) t.Close();

            var newTargets = new Dictionary<FormKey, NpcLogTarget>();
            if (npcsToLog != null)
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogFolderName);
                foreach (var (formKey, displayString) in npcsToLog)
                {
                    if (formKey.IsNull || newTargets.ContainsKey(formKey)) continue;
                    // ".txt" must be appended explicitly: the filename ends in the
                    // FormKey (e.g. "..._00442D_3DNPC.esp"), so without this the file
                    // would be saved with a ".esp" extension.
                    string fileName = BuildFileName(displayString, formKey) + ".txt";
                    newTargets[formKey] = new NpcLogTarget(Path.Combine(dir, fileName), displayString, formKey);
                }
            }

            _targets = newTargets;
            IsEnabled = newTargets.Count > 0;
        }
    }

    /// <summary>Whether the given NPC is configured for logging this run.</summary>
    public static bool IsLogged(FormKey formKey)
    {
        if (!IsEnabled) return false;
        return _targets.ContainsKey(formKey);
    }

    /// <summary>Marks the NPC whose processing is starting. Subsequent
    /// <see cref="Log"/> calls on this async flow route to this NPC's file.</summary>
    public static void BeginNpc(FormKey formKey)
    {
        if (!IsEnabled) return;
        _currentNpc.Value = formKey;
    }

    /// <summary>Clears the active NPC context for this async flow.</summary>
    public static void EndNpc()
    {
        _currentNpc.Value = null;
    }

    /// <summary>Writes a blank line and a banner (e.g. "VALIDATION" / "PATCHING")
    /// to the active NPC's file.</summary>
    public static void LogSection(string section)
    {
        if (!IsActive) return;
        Log(string.Empty);
        Log("==================== " + section + " ====================");
    }

    /// <summary>Logs to the currently-active NPC (set via <see cref="BeginNpc"/>).
    /// No-op if logging is off, no context is active, or the active NPC isn't
    /// tracked.</summary>
    public static void Log(string message)
    {
        if (!IsEnabled) return;
        var fk = _currentNpc.Value;
        if (fk == null) return;
        WriteTo(fk.Value, message);
    }

    /// <summary>Logs to a specific NPC regardless of the active context.</summary>
    public static void LogFor(FormKey formKey, string message)
    {
        if (!IsEnabled) return;
        WriteTo(formKey, message);
    }

    /// <summary>Flushes and closes all open per-NPC files. Call in the run's
    /// finally so handles don't linger between runs.</summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            foreach (var t in _targets.Values) t.Close();
        }
    }

    private static void WriteTo(FormKey formKey, string message)
    {
        var targets = _targets; // snapshot the reference (Configure swaps it atomically)
        if (!targets.TryGetValue(formKey, out var target)) return;
        target.Write(message);
    }

    private static string BuildFileName(string displayString, FormKey formKey)
    {
        string cleaned = Sanitize(string.IsNullOrWhiteSpace(displayString) ? formKey.ToString() : displayString);
        // FormKey.ToString() and the EditorID are unique-ish, but two NPCs can
        // share a display string (e.g. "Guard"); disambiguate with the FormKey's
        // numeric ID so files never collide.
        string fkTag = "_" + Sanitize(formKey.ToString());
        if (cleaned.Length + fkTag.Length > MaxFileNameLength)
        {
            cleaned = cleaned.Substring(0, Math.Max(0, MaxFileNameLength - fkTag.Length)).TrimEnd();
        }
        return cleaned + fkTag;
    }

    private static readonly char[] _invalid = BuildInvalidChars();

    private static char[] BuildInvalidChars()
    {
        var set = new HashSet<char>(Path.GetInvalidFileNameChars());
        // FormKey.ToString() uses ':' (e.g. "012345:Skyrim.esm"); '|' is the
        // default NPC list separator. Neither is legal in a Windows filename.
        set.Add(':');
        set.Add('|');
        return set.ToArray();
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(Array.IndexOf(_invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString().Trim().TrimEnd('.', ' ');
    }

    /// <summary>One per-NPC output file. Opens lazily (truncating) on the first
    /// write of a run and stays open until <see cref="Close"/>, so the
    /// validation and patching phases append to the same file.</summary>
    private sealed class NpcLogTarget
    {
        private readonly string _path;
        private readonly string _displayString;
        private readonly FormKey _formKey;
        private readonly object _wlock = new();
        private StreamWriter? _writer;
        private bool _openFailed;

        public NpcLogTarget(string path, string displayString, FormKey formKey)
        {
            _path = path;
            _displayString = displayString;
            _formKey = formKey;
        }

        public void Write(string message)
        {
            lock (_wlock)
            {
                if (_writer == null)
                {
                    if (_openFailed) return;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                        // FileMode.Create truncates: each run starts fresh on first write.
                        var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
                        _writer = new StreamWriter(stream) { AutoFlush = true };
                        _writer.WriteLine($"=== NPC2 per-NPC diagnostic — {_displayString} ({_formKey}) ===");
                        _writer.WriteLine($"=== Run started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                    }
                    catch
                    {
                        _openFailed = true;
                        _writer = null;
                        return;
                    }
                }

                try
                {
                    if (string.IsNullOrEmpty(message)) _writer.WriteLine();
                    else _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
                }
                catch
                {
                    // Swallow — diagnostics must never break a patch run.
                }
            }
        }

        public void Close()
        {
            lock (_wlock)
            {
                try
                {
                    _writer?.Flush();
                    _writer?.Dispose();
                }
                catch
                {
                    // Swallow.
                }
                _writer = null;
            }
        }
    }
}
