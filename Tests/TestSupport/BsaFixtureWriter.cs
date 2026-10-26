using System.IO;
using System.Text;

namespace NPC_Plugin_Chooser_2.Tests.TestSupport;

/// <summary>
/// Writes small, uncompressed Skyrim BSA archives (version 105 = SE, 104 = LE) for tests — the format the
/// game and Mutagen read, with real folder/file name tables and file data. Hash fields are left zero:
/// Mutagen resolves folders and files by name, so they are irrelevant to every reader under test.
///
/// <para>The knobs (<see cref="Options"/>) deliberately produce archives a careful reader must not trust —
/// an unsupported version, missing name tables, a header whose totals disagree with the folder table,
/// duplicate folder names — so the FaceGen discovery probe's fallbacks can be exercised.</para>
/// </summary>
public static class BsaFixtureWriter
{
    public sealed record Options
    {
        public uint Version { get; init; } = 105;
        public bool FolderNames { get; init; } = true;
        public bool FileNames { get; init; } = true;
        /// <summary>Added to the header's total-folder-name-length field (0 = consistent).</summary>
        public int FolderNameLengthSkew { get; init; }
        /// <summary>Added to the header's file-count field (0 = consistent).</summary>
        public int FileCountSkew { get; init; }
    }

    /// <param name="folders">Folder path (backslashes, as stored) → files (name, content). The same folder
    /// path may appear twice to build an archive with duplicate folder names.</param>
    public static void Write(string path, IReadOnlyList<(string Folder, IReadOnlyList<(string Name, byte[] Data)> Files)> folders,
        Options? options = null)
    {
        options ??= new Options();
        var encoding = Encoding.Latin1; // single-byte, identical to Windows-1252 for the names tests use
        int folderRecordLength = options.Version == 105 ? 24 : 16;
        uint archiveFlags = (options.FolderNames ? 1u : 0) | (options.FileNames ? 2u : 0);
        int folderCount = folders.Count;
        int fileCount = folders.Sum(f => f.Files.Count);
        var folderNameBytes = folders.Select(f => encoding.GetBytes(f.Folder)).ToList();
        int totalFolderNameLength = folderNameBytes.Sum(b => b.Length + 1);
        var fileNameBytes = folders.SelectMany(f => f.Files).Select(f => encoding.GetBytes(f.Name)).ToList();
        int totalFileNameLength = fileNameBytes.Sum(b => b.Length + 1);

        long folderRecordsStart = 36;
        long blocksStart = folderRecordsStart + (long)folderRecordLength * folderCount;
        long blocksLength = (options.FolderNames ? folderCount + totalFolderNameLength : 0) + 16L * fileCount;
        long namesStart = blocksStart + blocksLength;
        long dataStart = namesStart + (options.FileNames ? totalFileNameLength : 0);

        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write(Encoding.ASCII.GetBytes("BSA\0"));
        w.Write(options.Version);
        w.Write((uint)folderRecordsStart);
        w.Write(archiveFlags);
        w.Write((uint)folderCount);
        w.Write((uint)(fileCount + options.FileCountSkew));
        w.Write((uint)(totalFolderNameLength + options.FolderNameLengthSkew));
        w.Write((uint)totalFileNameLength);
        w.Write(0u); // file flags

        // Folder records.
        long blockCursor = blocksStart;
        for (int i = 0; i < folderCount; i++)
        {
            w.Write(0UL);
            w.Write((uint)folders[i].Files.Count);
            if (options.Version == 105)
            {
                w.Write(0u);
                w.Write((ulong)(blockCursor + totalFileNameLength));
            }
            else
            {
                w.Write((uint)(blockCursor + totalFileNameLength));
            }
            blockCursor += (options.FolderNames ? 1 + folderNameBytes[i].Length + 1 : 0) + 16L * folders[i].Files.Count;
        }

        // Per-folder blocks: bzstring folder name + file records pointing at the data section.
        long dataCursor = dataStart;
        for (int i = 0; i < folderCount; i++)
        {
            if (options.FolderNames)
            {
                w.Write((byte)(folderNameBytes[i].Length + 1));
                w.Write(folderNameBytes[i]);
                w.Write((byte)0);
            }
            foreach (var file in folders[i].Files)
            {
                w.Write(0UL);
                w.Write((uint)file.Data.Length);
                w.Write((uint)dataCursor);
                dataCursor += file.Data.Length;
            }
        }

        if (options.FileNames)
        {
            foreach (var name in fileNameBytes)
            {
                w.Write(name);
                w.Write((byte)0);
            }
        }

        foreach (var file in folders.SelectMany(f => f.Files))
        {
            w.Write(file.Data);
        }
    }

    /// <summary>Shorthand: one file per entry, given as full archive paths with backslashes.</summary>
    public static void WriteFiles(string path, IEnumerable<string> archivePaths, Options? options = null)
    {
        var folders = archivePaths
            .GroupBy(p => p.Substring(0, p.LastIndexOf('\\')), StringComparer.Ordinal)
            .Select(g => (g.Key, (IReadOnlyList<(string, byte[])>)g
                .Select(p => (p.Substring(p.LastIndexOf('\\') + 1), Encoding.ASCII.GetBytes("data:" + p)))
                .ToList()))
            .ToList();
        Write(path, folders, options);
    }
}
