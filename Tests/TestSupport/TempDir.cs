using System.IO;

namespace NPC_Plugin_Chooser_2.Tests.TestSupport;

/// <summary>
/// Disposable scratch directory under the OS temp folder. Created on construction,
/// recursively deleted (best-effort) on dispose. Use for any test that reads/writes
/// real files so nothing leaks into the source tree.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string? label = null)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "NPC2.Tests",
            (label ?? "t") + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Combines a relative path under this temp dir, creating parent dirs.</summary>
    public string Combine(params string[] parts)
    {
        var full = System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
        var parent = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        return full;
    }

    /// <summary>Writes text to a relative path (creating parents) and returns the absolute path.</summary>
    public string WriteText(string relative, string contents)
    {
        var full = Combine(relative);
        File.WriteAllText(full, contents);
        return full;
    }

    /// <summary>Creates an empty subdirectory and returns its absolute path.</summary>
    public string Dir(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort; leave the temp dir if a handle is still held.
        }
    }
}
