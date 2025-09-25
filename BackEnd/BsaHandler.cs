using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class BsaHandler : OptionalUIModule
{
    private Dictionary<ModKey, Dictionary<string, HashSet<string>>> _bsaContents = new();
    
    private Dictionary<string, IArchiveReader> _openBsaArchiveReaders = new();
    
    private readonly EnvironmentStateProvider _environmentStateProvider;

    public BsaHandler(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
    }

    public void UnloadAllBsaReaders()
    {
        foreach (var reader in _openBsaArchiveReaders.Values)
        {
            (reader as IDisposable)?.Dispose();
        }
        _openBsaArchiveReaders.Clear();
        AppendLog("Unloaded all cached BSA readers.");
    }

    public void UnloadReadersInFolders(IEnumerable<string> folderPaths)
    {
        var toRemove = new HashSet<string>();
        foreach (var reader in _openBsaArchiveReaders)
        {
            foreach (var folderPath in folderPaths)
            {
                if (reader.Key.StartsWith(folderPath, StringComparison.InvariantCultureIgnoreCase))
                {
                    toRemove.Add(reader.Key);
                }
            }
        }

        foreach (var bsaPath in toRemove)
        {
            _openBsaArchiveReaders.Remove(bsaPath);
        }
    }
    
    public HashSet<string> GetBsaPathsForPluginInDir(ModKey modKey, string directory, GameRelease gameRelease)
    {
        if (directory.Equals(_environmentStateProvider.DataFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            // doesn't require re-enumerating directory
            return PluginArchiveIndex.GetOwnedBsaFiles(modKey, directory);
        }
        
        try
        {
            // Important: This should be the only call to Archive.GetApplicableArchivePaths in the entire application
            return Archive.GetApplicableArchivePaths(gameRelease, directory, modKey)
                .Select(x => x.Path)
                .ToHashSet();
        }
        catch (InvalidOperationException) // Archive.GetApplicableArchivePaths is prone to throwing this on some plugins. Cause unclear.
        {
            return PluginArchiveIndex.GetOwnedBsaFiles(modKey, directory);
        }
    }

    public HashSet<string> GetBsaPathsForPluginInDirs(ModKey modKey, IEnumerable<string> directories,
        GameRelease gameRelease)
    {
        HashSet<string> bsaPaths = new();
        foreach (var directoryPath in directories)
        {
            bsaPaths.UnionWith(GetBsaPathsForPluginInDir(modKey, directoryPath, gameRelease));
        }
        return bsaPaths;
    }

    public Dictionary<ModKey, HashSet<string>> GetBsaPathsForPluginsInDirs(IEnumerable<ModKey> modKeys,
        IEnumerable<string> directories, GameRelease gameRelease)
    {
        Dictionary<ModKey, HashSet<string>> bsaPaths = new();
        foreach (var modKey in modKeys.Distinct())
        {
            bsaPaths.Add(modKey, GetBsaPathsForPluginInDirs(modKey, directories, gameRelease));
        }
        return bsaPaths;
    }
    
    /// <summary>
    /// Extracts a single file from a specified BSA archive using a cached reader.
    /// This method now correctly returns a Task<bool> for the asynchronous operation.
    /// </summary>
    public Task<bool> ExtractFileAsync(string bsaPath, string relativePath, string destinationPath)
    {
        // This method now directly returns the Task<bool> produced by Task.Run.
        return Task.Run(() =>
        {
            if (!_openBsaArchiveReaders.TryGetValue(new FilePath(bsaPath), out var bsaReader))
            {
                AppendLog($"BSA-CACHE-MISS: The reader for {bsaPath} was not pre-cached. This indicates a logic error.", true, true);
                return false;
            }

            try
            {
                if (TryGetFileFromSingleReader(relativePath, bsaReader, out var archiveFile) && archiveFile != null)
                {
                    return ExtractFileFromBsa(archiveFile, destinationPath);
                }
                else
                {
                    AppendLog($"Could not find {relativePath} within {bsaPath} for extraction.", true, true);
                    return false;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to read from cached BSA reader for {bsaPath}: {ExceptionLogger.GetExceptionStack(ex)}", true, true);
                return false;
            }
        });
    }
    
    public void OpenBsaReadersFor(ModSetting modSetting, GameRelease gameRelease)
    {
        var bsaDict = GetBsaPathsForPluginsInDirs(modSetting.CorrespondingModKeys, modSetting.CorrespondingFolderPaths, gameRelease);
        
        if (modSetting.DisplayName == VM_Mods.BaseGameModSettingName ||
            modSetting.DisplayName == VM_Mods.CreationClubModsettingName)
        {
            foreach (var mk in modSetting.CorrespondingModKeys)
            {
               var entry = GetBsaPathsForPluginInDir(mk, _environmentStateProvider.DataFolderPath, gameRelease);
               bsaDict.TryAdd(mk, entry);
               if (!bsaDict[mk].Any())
               {
                   bsaDict[mk] = entry;
               }
            }
        }
        
        foreach (var bsaPaths in bsaDict.Values)
        {
            OpenBsaArchiveReaders(bsaPaths, gameRelease, true);
        }
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

    // Override for reading specific BSA files which are already assumed to exist
    public Dictionary<string, IArchiveReader> OpenBsaArchiveReaders(IEnumerable<string> bsaPaths, GameRelease gameRelease,
        bool cacheReaders = false)
    {
        var readers = new Dictionary<string, IArchiveReader>();
        foreach (var bsaPath in bsaPaths.Distinct())
        {
            if (_openBsaArchiveReaders.TryGetValue(bsaPath, out var reader) && reader is not null)
            {
                readers.Add(bsaPath, reader);
            }
            else if (File.Exists(bsaPath))
            {
                AppendLog($"Loading BSA archive for {bsaPath}");  // ❷  safe-invoke
                var bsaReader = Archive.CreateReader(gameRelease, bsaPath);
                if (bsaReader != null)
                {
                    readers.Add(bsaPath, bsaReader);
                    if (cacheReaders)
                    {
                        _openBsaArchiveReaders[bsaPath] = bsaReader;
                    }
                }
                else
                {
                    AppendLog(
                        $"ERROR opening archive '{bsaPath}': Reader is null", true);
                }
            }
            else
            {
                AppendLog(
                    $"ERROR opening archive '{bsaPath}': Expected file does not exist", true);
            }
        }
        
        return readers;
    }
    
    public bool CacheContainsModKey(ModKey modKey)
    {
        return _bsaContents.ContainsKey(modKey);
    }
    
    public async Task AddMissingModToCache(ModSetting mod, GameRelease gameRelease)
    {
        bool matched = false;
        
        foreach (var modKey in mod.CorrespondingModKeys)
        {
            if (_bsaContents.TryGetValue(modKey, out var contents))
            {
                foreach (var dataPath in mod.CorrespondingFolderPaths)
                {
                    if (contents.Any(x => x.Key.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        matched = true;
                        break;
                    }
                }
            }

            if (matched)
            {
                break;
            }
        }
        
        if (!matched)
        {
            await PopulateBsaContentPathsAsync(new List<ModSetting>() {mod}, gameRelease, reinitializeCache: false);
        }
    }
    
    public async Task PopulateBsaContentPathsAsync(IEnumerable<ModSetting> mods, GameRelease gameRelease, bool cacheReaders = false, bool reinitializeCache = true)
    {
        if (reinitializeCache)
        {
            _bsaContents.Clear();
        }

        // Use Task.Run to offload the blocking I/O of reading BSA headers.
        await Task.Run(() =>
        {
            foreach (var mod in mods)
            {
                var pathsToSearch = new HashSet<string>(mod.CorrespondingFolderPaths);
                if (mod.DisplayName == VM_Mods.BaseGameModSettingName ||
                    mod.DisplayName == VM_Mods.CreationClubModsettingName)
                {
                    pathsToSearch.Add(_environmentStateProvider.DataFolderPath);
                }
                
                var bsaDict = GetBsaPathsForPluginsInDirs(mod.CorrespondingModKeys, pathsToSearch, gameRelease);
                foreach (var modkey in mod.CorrespondingModKeys)
                {
                    if (_bsaContents.ContainsKey(modkey))
                    {
                        continue;
                    }
                    var bsaPaths = bsaDict[modkey];
                    var filesInArchives = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    var readers = OpenBsaArchiveReaders(bsaPaths, gameRelease, cacheReaders);
                    foreach (var entry in readers)
                    {
                        var (bsaPath, reader) = entry;
                        var containedFiles = new HashSet<string>(reader.Files.Select(x => x.Path),
                            StringComparer.OrdinalIgnoreCase);
                        filesInArchives.Add(bsaPath, containedFiles);
                    }

                    _bsaContents.Add(modkey, filesInArchives);
                }
            }
        });
    }
    
    public Dictionary<ModKey, HashSet<string>> GetAllFilePathsForMod(IEnumerable<ModKey> modKeys, IEnumerable<string> modDirs, GameRelease gameRelease)
    {
        Dictionary<ModKey, HashSet<string>> result = new();
        var bsaFilePaths = GetBsaPathsForPluginsInDirs(modKeys.Distinct(), modDirs, gameRelease);
        foreach (var bsaFilePath in bsaFilePaths)
        {
            var modKey = bsaFilePath.Key;
            var readers = OpenBsaArchiveReaders(bsaFilePath.Value, gameRelease, false);
            HashSet<string> currentContents = new();
            foreach (var entry in readers)
            {
                var (bsaPath, reader) = entry;
                var containedFiles = new HashSet<string>(reader.Files.Select(x => x.Path),
                    StringComparer.OrdinalIgnoreCase);
                currentContents.UnionWith(containedFiles);
            }
            result.Add(modKey, currentContents);
        }
        return result;
    }
    
    public string GetStatusReport()
    {
        string output = "";
        if (!_openBsaArchiveReaders.Any())
        {
            output = "No BSA archives currently loaded.";
        }
        else
        {
            output = "Loaded BSA Archives at: " + Environment.NewLine + string.Join(Environment.NewLine, _openBsaArchiveReaders.Select(x => "\t" + x.Key.ToString()));
        }

        output += Environment.NewLine;
        
        if (!_bsaContents.Any())
        {
            output += "No BSA contents currently cached";
        }
        else
        {
            output += "Cached BSA archive contents for plugins: " + Environment.NewLine + string.Join(Environment.NewLine, _bsaContents.Select(x => "\t" + x.Key.ToString()));
        }
        
        return output;
    }
}