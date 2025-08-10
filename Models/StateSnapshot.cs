using System;
using System.Collections.Generic;

namespace NPC_Plugin_Chooser_2.Models
{
    public class FileSnapshot
    {
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
    }

    public class DirectorySnapshot
    {
        public string Path { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
    }

    public class ModStateSnapshot
    {
        public List<FileSnapshot> PluginSnapshots { get; set; } = new();
        public List<FileSnapshot> BsaSnapshots { get; set; } = new();
        public List<DirectorySnapshot> DirectorySnapshots { get; set; } = new();

        public bool Equals(ModStateSnapshot? other)
        {
            if (other == null) return false;

            // Simple count checks first
            if (PluginSnapshots.Count != other.PluginSnapshots.Count ||
                BsaSnapshots.Count != other.BsaSnapshots.Count ||
                DirectorySnapshots.Count != other.DirectorySnapshots.Count)
            {
                return false;
            }

            // Deep equality check for each list
            var fileComparer = new Func<FileSnapshot, FileSnapshot, bool>((f1, f2) =>
                f1.FileName.Equals(f2.FileName, StringComparison.OrdinalIgnoreCase) &&
                f1.FileSize == f2.FileSize &&
                f1.LastWriteTimeUtc == f2.LastWriteTimeUtc);

            var dirComparer = new Func<DirectorySnapshot, DirectorySnapshot, bool>((d1, d2) =>
                d1.Path.Equals(d2.Path, StringComparison.OrdinalIgnoreCase) &&
                d1.FileCount == d2.FileCount &&
                d1.LastWriteTimeUtc == d2.LastWriteTimeUtc);

            return PluginSnapshots.All(ps1 => other.PluginSnapshots.Any(ps2 => fileComparer(ps1, ps2))) &&
                   BsaSnapshots.All(bs1 => other.BsaSnapshots.Any(bs2 => fileComparer(bs1, bs2))) &&
                   DirectorySnapshots.All(ds1 => other.DirectorySnapshots.Any(ds2 => dirComparer(ds1, ds2)));
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as ModStateSnapshot);
        }

        public override int GetHashCode()
        {
            // Note: GetHashCode is not strictly needed for this logic but is good practice to override if Equals is overridden.
            return HashCode.Combine(PluginSnapshots.Count, BsaSnapshots.Count, DirectorySnapshots.Count);
        }
    }
}