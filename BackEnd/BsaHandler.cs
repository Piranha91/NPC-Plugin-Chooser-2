using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class BsaHandler : OptionalUIModule
{
    private Dictionary<ModKey, Dictionary<string, HashSet<string>>> _bsaContents = new();
    
    private Dictionary<FilePath, IArchiveReader> _openBsaArchiveReaders = new();

    private Dictionary<string /*bsa filepath*/, List<(string relativePath, string destinationPath, string requestingModName)>> _extractionQueue = new();
    
    private readonly EnvironmentStateProvider _environmentStateProvider;

    public BsaHandler(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
    }

    public async Task PopulateBsaContentPathsAsync(IEnumerable<ModSetting> mods, GameRelease gameRelease)
    {
        _bsaContents.Clear();

        // Use Task.Run to offload the blocking I/O of reading BSA headers.
        await Task.Run(() =>
        {
            foreach (var mod in mods)
            {
                foreach (var key in mod.CorrespondingModKeys)
                {
                    if (_bsaContents.ContainsKey(key))
                    {
                        continue;
                    }

                    var subDict = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                    foreach (var directory in mod.CorrespondingFolderPaths)
                    {
                        try
                        {
                            // These are the blocking calls.
                            foreach (var bsaPath in Archive.GetApplicableArchivePaths(gameRelease, directory, key))
                            {
                                var bsaReader = Archive.CreateReader(gameRelease, bsaPath);
                                var containedFiles = new HashSet<string>(bsaReader.Files.Select(x => x.Path),
                                    StringComparer.OrdinalIgnoreCase);
                                subDict.Add(bsaPath, containedFiles);
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            string prefix = key.FileName.NameWithoutExtension;
                            string searchPattern = $"{prefix}*.bsa";

                            foreach (var bsaPath in Directory.EnumerateFiles(directory, searchPattern,
                                         SearchOption.TopDirectoryOnly))
                            {
                                var bsaReader = Archive.CreateReader(gameRelease, bsaPath);
                                var containedFiles = new HashSet<string>(bsaReader.Files.Select(x => x.Path),
                                    StringComparer.OrdinalIgnoreCase);
                                subDict.Add(bsaPath, containedFiles);
                            }
                        }
                    }

                    _bsaContents.Add(key, subDict);
                }
            }
        });
    }

    public bool FileExists(string path, ModKey modKey, string bsaPath, bool convertSlashes = true)
    {
        if (convertSlashes)
        {
            path = path.Replace('/', '\\');
        }
        if (_bsaContents.ContainsKey(modKey) &&
            _bsaContents[modKey].ContainsKey(bsaPath) &&
            _bsaContents[modKey][bsaPath].Contains(path))
        {
            return true;
        }
        return false;
    }

    public bool FileExists(string path, ModKey modKey, out string? bsaPath, bool convertSlashes = true)
    {
        bsaPath = null;
        if (convertSlashes)
        {
            path = path.Replace('/', '\\');
        }

        if (_bsaContents.TryGetValue(modKey, out var bsaFiles))
        {
            foreach (var entry in bsaFiles)
            {
                if (entry.Value.Contains(path)) // This is now O(1)
                {
                    bsaPath = entry.Key;
                    return true;
                }
            }
        }
        return false;
    }
    
    
    public bool FileExists(string path, IEnumerable<ModKey> modKeys, out ModKey? modKey, out string? bsaPath, bool convertSlashes = true)
    {
        bsaPath = null;
        modKey = null;
        if (convertSlashes)
        {
            path = path.Replace('/', '\\');
        }

        foreach (var candidateModKey in modKeys)
        {
            if (_bsaContents.ContainsKey(candidateModKey))
            {
                foreach (var entry in _bsaContents[candidateModKey])
                {
                    if (entry.Value.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        bsaPath = entry.Key;
                        modKey = candidateModKey;
                        return true;
                    }
                }
            }
        }

        return false;
    }
    
    public Dictionary<string, bool> ScheduleExtractAssetFiles(
        HashSet<string> assetRelativePathList,
        ModSetting modSetting,
        string assetType /*"Meshes" or "Textures"*/,
        IEnumerable<string> autoPredictedExtraPaths, string outputBaseDirPath)
    {
        Dictionary<string, bool> result = new();

        string outputBase = Path.Combine(outputBaseDirPath, assetType);
        
        foreach (string relativePath in assetRelativePathList.And(autoPredictedExtraPaths))
        {
            result[relativePath] = false;
            
            if (FileExists(relativePath, modSetting.CorrespondingModKeys, out var correspondingModKey, out var bsaPath)
                && correspondingModKey != null && bsaPath != null)
            {
                string outputPath = Path.Combine(outputBase, relativePath);
                QueueFileForExtraction(bsaPath, relativePath, outputPath, modSetting.DisplayName);
            }
        }

        return result;
    }

    public void QueueFileForExtraction(string bsaPath, string relativePath, string destinationPath, string requestingModName)
    {
        List<(string, string, string)> files;
        if (_extractionQueue.ContainsKey(bsaPath))
        {
            files = _extractionQueue[bsaPath];
        }
        else
        {
            files = new List<(string relativePath, string destinationPath, string requestingModName)>();
            _extractionQueue.Add(bsaPath, files);
        }
        
        files.Add((relativePath, destinationPath, requestingModName));
    }

    public Dictionary<string, HashSet<string>> ExtractQueuedFiles(Dictionary<string, HashSet<string>>? extractedNifPaths = null)
    {
        if (extractedNifPaths == null)
        {
            extractedNifPaths = new(); // needed for followup texture search
        }

        foreach (var entry in _extractionQueue)
        {
            var bsaFilePath = entry.Key;
            var bsaReader = Archive.CreateReader(_environmentStateProvider.SkyrimVersion.ToGameRelease(), bsaFilePath);
            foreach (var pathInfo in entry.Value)
            {
                if (!TryGetFileFromSingleReader(pathInfo.relativePath, bsaReader, out var archiveFile) || archiveFile == null)
                {
                    AppendLog($"Could not extract {pathInfo.relativePath} from {bsaFilePath}.", true, true);
                    continue;
                }

                if (!ExtractFileFromBsa(archiveFile, pathInfo.destinationPath))
                {
                    AppendLog($"Could not extract {pathInfo.relativePath} from {bsaFilePath}.", true, true);
                }

                if (pathInfo.Item1.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                {
                    if (!extractedNifPaths.TryGetValue(pathInfo.requestingModName, out var modEntry))
                    {
                        modEntry = new HashSet<string>();
                        extractedNifPaths.Add(pathInfo.requestingModName, modEntry);
                    }
                    
                    modEntry.Add(pathInfo.destinationPath);
                }
            }
        }
        
        _extractionQueue.Clear();
        
        return extractedNifPaths;
    }
    
    /// <summary>
    /// Tries to find a file within any BSA reader in the provided set.
    /// Use this when checking readers associated with a specific source directory.
    /// </summary>
    /// <param name="subpath">The relative path within the BSA (e.g., "meshes\\actor.nif").</param>
    /// <param name="bsaReaders">The set of readers (usually for one directory) to check.</param>
    /// <param name="file">The found archive file, or null.</param>
    /// <returns>True if found in any reader in the set, false otherwise.</returns>
    public bool TryGetFileFromReaders(string subpath, HashSet<IArchiveReader> bsaReaders, out IArchiveFile? file)
    {
        file = null;
        if (bsaReaders == null || !bsaReaders.Any())
        {
            return false; // No readers to check
        }

        // Normalize path separators for BSA consistency if needed, depends on how paths are stored/compared
        string normalizedSubpath = subpath.Replace('/', '\\');

        foreach (var reader in bsaReaders)
        {
            // Use the existing TryGetFileFromSingleReader which presumably checks a single reader efficiently
            if (TryGetFileFromSingleReader(normalizedSubpath, reader, out file)) // Assuming TryGetFileFromSingleReader exists from V1
            {
                return true; // Found it in this reader
            }
        }

        return false; // Not found in any reader in this set
    }

// Ensure your existing V1 TryGetFileFromSingleReader handles case-insensitivity correctly:
    public bool TryGetFileFromSingleReader(string subpath, IArchiveReader bsaReader, out IArchiveFile? file)
    {
        file = null;
        // Use OrdinalIgnoreCase for path comparison in BSA lookups
        var foundFile = bsaReader.Files.FirstOrDefault(candidate =>
            candidate.Path.Equals(subpath, StringComparison.OrdinalIgnoreCase));
        if (foundFile != null)
        {
            file = foundFile;
            return true;
        }

        return false;
    }
    
    public bool HaveFile(string subpath, HashSet<IArchiveReader> bsaReaders, out IArchiveFile? archiveFile)
    {
        foreach (var reader in bsaReaders)
        {
            if (TryGetFileFromSingleReader(subpath, reader, out archiveFile))
            {
                return true;
            }
        }

        archiveFile = null;
        return false;
    }
    
    public bool ExtractFileFromBsa(IArchiveFile file, string destPath)
    {
        string? dirPath = Path.GetDirectoryName(destPath);
        if (string.IsNullOrEmpty(dirPath)) // Also check for empty string
        {
            AppendLog($"ERROR: Could not get directory path from destination '{destPath}'", true);
            return false; // Invalid destination path
        }

        try
        {
            Directory.CreateDirectory(dirPath); // Ensure directory exists

            // Get the stream from the archive file
            using (Stream sourceStream = file.AsStream())
            {
                // Create the destination file stream
                using (var destStream = File.Create(destPath))
                {
                    // Copy the contents from the source stream to the destination stream
                    sourceStream.CopyTo(destStream);
                }
            }
            return true; // Success
        }
        catch (IOException ioEx) // Catch specific IO errors
        {
            AppendLog($"IO ERROR extracting BSA file: {file.Path} to {destPath}. Error: {ExceptionLogger.GetExceptionStack(ioEx)}", true);
            // Common issues: File locked, disk full, path too long
            return false; // Failure
        }
        catch (UnauthorizedAccessException authEx) // Catch permission errors
        {
            AppendLog($"ACCESS ERROR extracting BSA file: {file.Path} to {destPath}. Check permissions. Error: {ExceptionLogger.GetExceptionStack(authEx)}", true);
            return false; // Failure
        }
        catch (Exception ex) // Catch any other unexpected errors
        {
            AppendLog($"GENERAL ERROR extracting BSA file: {file.Path} to {destPath}. Error: {ExceptionLogger.GetExceptionStack(ex)}", true);
            return false; // Failure
        }
    }

    // Keep OpenBsaArchiveReaders (no change needed based on clarifications)
    public HashSet<IArchiveReader> OpenBsaArchiveReaders(string sourceDirectory, ModKey pluginKey)
    {
        var readers = new HashSet<IArchiveReader>();
        // Use the SkyrimRelease from your EnvironmentStateProvider if needed
        var gameRelease = GameRelease.SkyrimSE; // Or get dynamically

        try
        {
            // GetApplicableArchivePaths handles finding BSAs like Skyrim - Textures.bsa etc.
            foreach (var bsaFile in Archive.GetApplicableArchivePaths(gameRelease, sourceDirectory, pluginKey))
            {
                try
                {
                    if (_openBsaArchiveReaders.TryGetValue(bsaFile, out var reader))
                    {
                        readers.Add(reader);
                    }
                    else if (File.Exists(bsaFile))
                    {
                        AppendLog($"Loading BSA archive for {bsaFile}");  // ❷  safe-invoke
                        var bsaReader = Archive.CreateReader(gameRelease, bsaFile);
                        readers.Add(bsaReader);
                        _openBsaArchiveReaders[bsaFile] = bsaReader;
                    }
                    else
                    {
                        // Log if an expected BSA path doesn't exist? Optional.
                        AppendLog($"INFO: Applicable BSA path not found: {bsaFile}");
                    }
                }
                catch (Exception exInner)
                {
                    AppendLog(
                        $"ERROR opening archive '{bsaFile}': {ExceptionLogger.GetExceptionStack(exInner)}", true);
                    // Decide whether to continue or throw
                }
            }
        }
        catch (Exception exOuter)
        {
            // Error enumerating paths?
            AppendLog(
                $"ERROR getting applicable archive paths for {pluginKey} in {sourceDirectory}: {ExceptionLogger.GetExceptionStack(exOuter)}", true);
            // Decide whether to throw
        }

        return readers;
    }

    public bool DirectoryHasCorrespondingBsaFile(string sourceDirectory, ModKey pluginKey)
    {
        var gameRelease = GameRelease.SkyrimSE;
        return Archive.GetApplicableArchivePaths(gameRelease, sourceDirectory, pluginKey).Any();
    }
}