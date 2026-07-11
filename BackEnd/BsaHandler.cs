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
    private readonly object _bsaContentsLock = new();

    /// <summary>
    /// Cache entry for an open <see cref="IArchiveReader"/>. <see cref="RefCount"/>
    /// tracks how many logical "openers" hold the reader so that
    /// <see cref="UnloadReadersInFolders"/> only disposes when the last opener
    /// releases. Without this, <see cref="PortraitCreator.FindNpcNifPath"/>'s
    /// open/extract/unload sequence could yank a reader that an in-flight
    /// preview render still needs, producing a stochastic BSA-CACHE-MISS on
    /// the next extraction (head NIF missing, etc.).
    /// </summary>
    private sealed class ReaderEntry
    {
        public IArchiveReader Reader = null!;
        public int RefCount;
    }

    private Dictionary<string, ReaderEntry> _openBsaArchiveReaders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _readersLock = new();

    private readonly EnvironmentStateProvider _environmentStateProvider;

    public BsaHandler(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
    }

    /// <summary>
    /// Hard-wipe: dispose every cached reader and clear the cache regardless of
    /// outstanding refcounts. Intended for end-of-patcher / shutdown only —
    /// callers that just want to release a single mod's readers should use
    /// <see cref="UnloadReadersInFolders"/> so other concurrent users (e.g. an
    /// in-flight preview render) keep their readers.
    /// </summary>
    public void UnloadAllBsaReaders()
    {
        lock (_readersLock)
        {
            foreach (var entry in _openBsaArchiveReaders.Values)
            {
                (entry.Reader as IDisposable)?.Dispose();
            }
            _openBsaArchiveReaders.Clear();
        }
        AppendLog("Unloaded all cached BSA readers.");
    }

    /// <summary>
    /// Refcount-aware release: decrements the refcount of every cached reader
    /// whose BSA path lives under one of <paramref name="folderPaths"/>, and
    /// only disposes+removes the entry when its refcount reaches zero. Pairs
    /// with <see cref="OpenBsaReadersFor"/> / <see cref="OpenBsaArchiveReaders"/>
    /// (with <c>cacheReaders=true</c>) which increment on each open call.
    /// </summary>
    public void UnloadReadersInFolders(IEnumerable<string> folderPaths)
    {
        var folderList = folderPaths as IList<string> ?? folderPaths.ToList();
        lock (_readersLock)
        {
            var toRelease = new List<string>();
            foreach (var bsaPath in _openBsaArchiveReaders.Keys)
            {
                foreach (var folderPath in folderList)
                {
                    if (bsaPath.StartsWith(folderPath, StringComparison.InvariantCultureIgnoreCase))
                    {
                        toRelease.Add(bsaPath);
                        break;
                    }
                }
            }

            foreach (var bsaPath in toRelease)
            {
                ReleaseReader_NoLock(bsaPath);
            }
        }
    }

    /// <summary>Caller MUST hold <see cref="_readersLock"/>.</summary>
    private void ReleaseReader_NoLock(string bsaPath)
    {
        if (!_openBsaArchiveReaders.TryGetValue(bsaPath, out var entry))
        {
            return;
        }
        entry.RefCount--;
        if (entry.RefCount <= 0)
        {
            (entry.Reader as IDisposable)?.Dispose();
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
    /// On failure the returned tuple's <c>error</c> carries a diagnostic string
    /// (BSA-cache miss, file-not-found in archive, or the underlying exception
    /// stack from the extraction). Callers that only need the success/failure
    /// boolean can ignore the second tuple element.
    /// </summary>
    public Task<(bool ok, string? error)> ExtractFileAsync(string bsaPath, string relativePath, string destinationPath)
    {
        return Task.Run<(bool ok, string? error)>(() =>
        {
            IArchiveReader? bsaReader;
            lock (_readersLock)
            {
                if (!_openBsaArchiveReaders.TryGetValue(bsaPath, out var entry))
                {
                    string msg = $"BSA-CACHE-MISS: The reader for {bsaPath} was not pre-cached. This indicates a logic error.";
                    AppendLog(msg, true, true);
                    return (false, msg);
                }
                bsaReader = entry.Reader;
            }

            try
            {
                if (TryGetFileFromSingleReader(relativePath, bsaReader, out var archiveFile) && archiveFile != null)
                {
                    return ExtractFileFromBsa(archiveFile, destinationPath);
                }
                else
                {
                    string msg = $"Could not find {relativePath} within {bsaPath} for extraction.";
                    AppendLog(msg, true, true);
                    return (false, msg);
                }
            }
            catch (Exception ex)
            {
                string stack = ExceptionLogger.GetExceptionStack(ex);
                AppendLog($"Failed to read from cached BSA reader for {bsaPath}: {stack}", true, true);
                return (false, $"Failed to read from cached BSA reader for {bsaPath}: {stack}");
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
        lock (_bsaContentsLock)
        {
            if (_bsaContents.ContainsKey(modKey) &&
                _bsaContents[modKey].ContainsKey(bsaPath) &&
                _bsaContents[modKey][bsaPath].Contains(path))
            {
                return true;
            }
            return false;
        }
    }

    public bool FileExists(string path, ModKey modKey, out string? bsaPath, bool convertSlashes = true)
    {
        bsaPath = null;
        if (convertSlashes)
        {
            path = path.Replace('/', '\\');
        }

        lock (_bsaContentsLock)
        {
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
    }


    public bool FileExists(string path, IEnumerable<ModKey> modKeys, out ModKey? modKey, out string? bsaPath, bool convertSlashes = true)
    {
        bsaPath = null;
        modKey = null;
        if (convertSlashes)
        {
            path = path.Replace('/', '\\');
        }

        lock (_bsaContentsLock)
        {
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
    
    public (bool ok, string? error) ExtractFileFromBsa(IArchiveFile file, string destPath)
    {
        string? dirPath = Path.GetDirectoryName(destPath);
        if (string.IsNullOrEmpty(dirPath)) // Also check for empty string
        {
            string msg = $"ERROR: Could not get directory path from destination '{destPath}'";
            AppendLog(msg, true);
            return (false, msg);
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
            return (true, null);
        }
        catch (IOException ioEx) // Catch specific IO errors
        {
            string msg = $"IO ERROR extracting BSA file: {file.Path} to {destPath}. Error: {ExceptionLogger.GetExceptionStack(ioEx)}";
            AppendLog(msg, true);
            // Common issues: File locked, disk full, path too long
            return (false, msg);
        }
        catch (UnauthorizedAccessException authEx) // Catch permission errors
        {
            string msg = $"ACCESS ERROR extracting BSA file: {file.Path} to {destPath}. Check permissions. Error: {ExceptionLogger.GetExceptionStack(authEx)}";
            AppendLog(msg, true);
            return (false, msg);
        }
        catch (Exception ex) // Catch any other unexpected errors
        {
            string msg = $"GENERAL ERROR extracting BSA file: {file.Path} to {destPath}. Error: {ExceptionLogger.GetExceptionStack(ex)}";
            AppendLog(msg, true);
            return (false, msg);
        }
    }

    // Override for reading specific BSA files which are already assumed to exist.
    // When cacheReaders=true, every successful open increments the cached
    // entry's refcount — pair with UnloadReadersInFolders so transient users
    // (PortraitCreator.FindNpcNifPath, etc.) don't yank readers out from
    // under longer-lived users (CharacterViewer adapter's
    // EnsureAllArchivesOpened).
    public Dictionary<string, IArchiveReader> OpenBsaArchiveReaders(IEnumerable<string> bsaPaths, GameRelease gameRelease,
        bool cacheReaders = false)
    {
        var readers = new Dictionary<string, IArchiveReader>();
        foreach (var bsaPath in bsaPaths.Distinct())
        {
            if (cacheReaders)
            {
                lock (_readersLock)
                {
                    if (_openBsaArchiveReaders.TryGetValue(bsaPath, out var existing))
                    {
                        existing.RefCount++;
                        readers.Add(bsaPath, existing.Reader);
                        continue;
                    }
                    if (!File.Exists(bsaPath))
                    {
                        AppendLog($"ERROR opening archive '{bsaPath}': Expected file does not exist", true);
                        continue;
                    }
                    AppendLog($"Loading BSA archive for {bsaPath}");
                    var bsaReader = Archive.CreateReader(gameRelease, bsaPath);
                    if (bsaReader == null)
                    {
                        AppendLog($"ERROR opening archive '{bsaPath}': Reader is null", true);
                        continue;
                    }
                    _openBsaArchiveReaders[bsaPath] = new ReaderEntry { Reader = bsaReader, RefCount = 1 };
                    readers.Add(bsaPath, bsaReader);
                }
            }
            else
            {
                // Uncached path: read existing cache opportunistically (no
                // refcount bump — these readers don't belong to us), otherwise
                // open a one-shot reader the caller is responsible for.
                lock (_readersLock)
                {
                    if (_openBsaArchiveReaders.TryGetValue(bsaPath, out var existing))
                    {
                        readers.Add(bsaPath, existing.Reader);
                        continue;
                    }
                }
                if (!File.Exists(bsaPath))
                {
                    AppendLog($"ERROR opening archive '{bsaPath}': Expected file does not exist", true);
                    continue;
                }
                AppendLog($"Loading BSA archive for {bsaPath}");
                var bsaReader = Archive.CreateReader(gameRelease, bsaPath);
                if (bsaReader == null)
                {
                    AppendLog($"ERROR opening archive '{bsaPath}': Reader is null", true);
                    continue;
                }
                readers.Add(bsaPath, bsaReader);
            }
        }

        return readers;
    }
    
    public bool CacheContainsModKey(ModKey modKey)
    {
        lock (_bsaContentsLock)
        {
            return _bsaContents.ContainsKey(modKey);
        }
    }

    /// <summary>
    /// Snapshot of every ModKey whose BSAs have been indexed via
    /// <see cref="PopulateBsaContentPathsAsync"/> / <see cref="AddMissingModToCache"/>.
    /// Used by the CharacterViewer BSA adapter to satisfy lookups that don't know
    /// which mod a file belongs to.
    /// </summary>
    public IReadOnlyCollection<ModKey> GetIndexedModKeys()
    {
        lock (_bsaContentsLock)
        {
            return _bsaContents.Keys.ToList();
        }
    }

    /// <summary>
    /// Strict-scoped existence check: tests whether <paramref name="path"/>
    /// exists inside any indexed BSA owned by <paramref name="modKey"/>
    /// AND physically located under <paramref name="folderPath"/> (i.e.
    /// <c>bsaPath.StartsWith(folderPath)</c> case-insensitively). Used by
    /// the CharacterViewer renderer's per-mod-folder scope chain — the
    /// "is this file in the mod's BSA at this folder" question that the
    /// asset resolver asks for each scope in order.
    /// </summary>
    public bool FileExistsInArchiveAtFolder(string path, ModKey modKey, string folderPath, out string? bsaPath, bool convertSlashes = true)
    {
        bsaPath = null;
        if (string.IsNullOrEmpty(folderPath)) return false;
        if (convertSlashes) path = path.Replace('/', '\\');
        lock (_bsaContentsLock)
        {
            if (!_bsaContents.TryGetValue(modKey, out var bsaFiles)) return false;
            foreach (var entry in bsaFiles)
            {
                if (!entry.Key.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Value.Contains(path))
                {
                    bsaPath = entry.Key;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Snapshot of every BSA file path whose contents have been indexed —
    /// the actual on-disk archive paths that <see cref="FileExists"/>
    /// scans during a broadcast lookup. Deduped case-insensitively. Used
    /// by the CharacterViewer BSA adapter to log exactly which archives
    /// are being searched on each lookup, so the user can correlate a
    /// missing-asset trace with the BSA inventory.
    /// </summary>
    public IReadOnlyCollection<string> GetIndexedBsaPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_bsaContentsLock)
        {
            foreach (var modEntry in _bsaContents.Values)
            {
                foreach (var bsaPath in modEntry.Keys)
                {
                    paths.Add(bsaPath);
                }
            }
        }
        return paths;
    }
    
    public async Task AddMissingModToCache(ModSetting mod, GameRelease gameRelease)
    {
        BsaContentsDiag.Log($"AddMissingModToCache ENTER mod='{mod.DisplayName}' modKeys=[{string.Join(",", mod.CorrespondingModKeys.Select(k => k.FileName.String))}] folders=[{string.Join("|", mod.CorrespondingFolderPaths)}]");

        // Only short-circuit when EVERY modKey for this mod is already indexed.
        // The previous "any modKey×folder pair already indexed → skip" check was
        // too lenient: for a mod listing [USSEP.esp, MyMod.esp] with folders
        // [USSEP folder, MyMod folder], a prior mod that indexed USSEP would
        // satisfy the check and cause MyMod.esp to never get scanned —
        // leaving the mod's own BSA invisible to FileExistsInArchiveAtFolder.
        // Delegating to PopulateBsaContentPathsAsync is safe here: it skips
        // already-cached modKeys per-key, so the cached USSEP entry isn't
        // redundantly re-scanned.
        bool allCached;
        lock (_bsaContentsLock)
        {
            allCached = mod.CorrespondingModKeys.All(_bsaContents.ContainsKey);
        }

        if (!allCached)
        {
            BsaContentsDiag.Log($"AddMissingModToCache not all modKeys cached → delegating to PopulateBsaContentPathsAsync mod='{mod.DisplayName}'");
            await PopulateBsaContentPathsAsync(new List<ModSetting>() {mod}, gameRelease, reinitializeCache: false);
        }
        else
        {
            BsaContentsDiag.Log($"AddMissingModToCache all modKeys already cached mod='{mod.DisplayName}' — no populate needed");
        }
    }
    
    public async Task PopulateBsaContentPathsAsync(IEnumerable<ModSetting> mods, GameRelease gameRelease, bool cacheReaders = false, bool reinitializeCache = true)
    {
        if (reinitializeCache)
        {
            lock (_bsaContentsLock)
            {
                BsaContentsDiag.Log($"PopulateBsaContentPathsAsync reinitializeCache=TRUE — clearing _bsaContents (prior count={_bsaContents.Count})");
                _bsaContents.Clear();
            }
        }

        // Snapshot DataFolderPath once so the diag log makes the empty-vs-set
        // value at the moment of the call obvious. Otherwise we have to
        // correlate with timestamps in the env trace.
        string dfp = _environmentStateProvider.DataFolderPath.ToString() ?? "";
        BsaContentsDiag.Log($"PopulateBsaContentPathsAsync ENTER modsToProcess={mods.Count()} reinit={reinitializeCache} envDataFolderPath=[{dfp}] envDataFolderExists={(string.IsNullOrWhiteSpace(dfp) ? "(empty)" : Directory.Exists(dfp).ToString())}");

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
                BsaContentsDiag.Log($"  processing mod='{mod.DisplayName}' pathsToSearch=[{string.Join("|", pathsToSearch)}]");

                var bsaDict = GetBsaPathsForPluginsInDirs(mod.CorrespondingModKeys, pathsToSearch, gameRelease);
                foreach (var modkey in mod.CorrespondingModKeys)
                {
                    // Pre-I/O short-circuit under the lock: another caller may have
                    // populated this modkey already. Skip only if the existing entry has
                    // real content — an EMPTY placeholder (a plugin whose BSA wasn't in an
                    // earlier mod's folders) must stay upgradeable, or that empty entry would
                    // permanently mask a BSA that this mod's folders actually contain.
                    lock (_bsaContentsLock)
                    {
                        if (_bsaContents.TryGetValue(modkey, out var existing) && existing.Count > 0)
                        {
                            int existingFileCount = existing.Values.Sum(s => s.Count);
                            BsaContentsDiag.Log($"    SKIP modkey={modkey.FileName.String} — already populated (bsaCount={existing.Count}, fileCount={existingFileCount})");
                            continue;
                        }
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

                    int totalFiles = filesInArchives.Values.Sum(s => s.Count);
                    BsaContentsDiag.Log($"    ADD modkey={modkey.FileName.String} bsaCount={filesInArchives.Count} fileCount={totalFiles} bsaPaths=[{string.Join("|", bsaPaths)}]");
                    // An empty entry is only a problem when the plugin actually owns BSAs that
                    // failed to open/index (that genuinely masks reachable assets). A plugin that
                    // owns no BSA at all is expected — e.g. Update/Dawnguard/HearthFires/Dragonborn
                    // in Skyrim SE, whose assets are consolidated into the "Skyrim - *.bsa" set and
                    // resolve under Skyrim.esm — so caching it empty is correct, not poisoning.
                    if (filesInArchives.Count == 0 && bsaPaths.Count > 0)
                    {
                        BsaContentsDiag.Log($"    !!! WARNING: modkey={modkey.FileName.String} owns BSA(s) but none opened/indexed — this empty entry will mask reachable assets. bsaPaths=[{string.Join("|", bsaPaths)}]");
                    }

                    // Commit policy (handles the pre-check→commit race too):
                    //  - First CONTENT wins: real content installs over a prior empty
                    //    placeholder, but never clobbers existing content.
                    //  - Empty is provisional: recorded only if nothing is there yet, so a
                    //    genuinely BSA-less plugin isn't rescanned, yet a later mod whose
                    //    folders hold the BSA can still upgrade it to content.
                    lock (_bsaContentsLock)
                    {
                        bool hasExisting = _bsaContents.TryGetValue(modkey, out var existing);
                        bool existingHasContent = existing is { Count: > 0 };

                        if (filesInArchives.Count > 0)
                        {
                            if (!existingHasContent)
                            {
                                _bsaContents[modkey] = filesInArchives;
                                if (hasExisting)
                                {
                                    BsaContentsDiag.Log($"    UPGRADE modkey={modkey.FileName.String} — replaced empty placeholder with {filesInArchives.Count} BSA(s), {totalFiles} files");
                                }
                            }
                            else
                            {
                                BsaContentsDiag.Log($"    KEEP modkey={modkey.FileName.String} — existing content retained (first-content-wins)");
                            }
                        }
                        else if (!hasExisting)
                        {
                            _bsaContents[modkey] = filesInArchives; // provisional empty
                        }
                    }
                }
            }
            int finalCount;
            lock (_bsaContentsLock) { finalCount = _bsaContents.Count; }
            BsaContentsDiag.Log($"PopulateBsaContentPathsAsync EXIT — _bsaContents.Count now={finalCount}");
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

    // --- Vanilla (base game + Creation Club) asset-path index ---------------------------------
    // Union of every archive-internal path shipped in the base game + Creation Club BSAs. Used
    // for base-game-overwrite protection: detecting (VM_Mods scan) and skipping (AssetHandler)
    // mod assets that sit at vanilla paths and would otherwise stomp the user's installed
    // replacers (e.g. skin mods) game-wide. Built lazily on first request and cached for the
    // session, keyed on data folder + release so a game-path change rebuilds it. Paths follow
    // the _bsaContents convention: backslash separators, OrdinalIgnoreCase comparison.
    private HashSet<string>? _vanillaAssetPaths;
    private string? _vanillaAssetPathsKey;
    private readonly SemaphoreSlim _vanillaAssetPathsLock = new(1, 1);

    /// <summary>
    /// Returns the set of all asset paths contained in the base game + Creation Club BSAs
    /// (see field comment above). The stock game ships its assets exclusively in BSAs, so
    /// membership in this set is the "would overwrite a base game asset" test. Returns an
    /// empty set when the game environment is not resolved. Thread-safe; the potentially
    /// expensive build runs at most once per session per (data folder, release).
    /// </summary>
    public async Task<IReadOnlySet<string>> GetVanillaAssetPathsAsync()
    {
        string dataFolder = _environmentStateProvider.DataFolderPath.ToString() ?? string.Empty;
        var gameRelease = _environmentStateProvider.SkyrimVersion.ToGameRelease();
        string cacheKey = $"{dataFolder}|{gameRelease}";

        // Benign race: the field is only ever assigned a fully-built set.
        if (_vanillaAssetPaths != null &&
            cacheKey.Equals(_vanillaAssetPathsKey, StringComparison.OrdinalIgnoreCase))
        {
            return _vanillaAssetPaths;
        }

        await _vanillaAssetPathsLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_vanillaAssetPaths != null &&
                cacheKey.Equals(_vanillaAssetPathsKey, StringComparison.OrdinalIgnoreCase))
            {
                return _vanillaAssetPaths;
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(dataFolder) && Directory.Exists(dataFolder))
            {
                await Task.Run(() =>
                {
                    var vanillaKeys = _environmentStateProvider.BaseGamePlugins
                        .Concat(_environmentStateProvider.CreationClubPlugins)
                        .ToHashSet();
                    var contents = GetAllFilePathsForMod(vanillaKeys, new[] { dataFolder }, gameRelease);
                    foreach (var containedPaths in contents.Values)
                    {
                        result.UnionWith(containedPaths);
                    }
                }).ConfigureAwait(false);
                AppendLog($"Indexed {result.Count} base game / Creation Club asset paths for overwrite protection.");
            }

            _vanillaAssetPaths = result;
            _vanillaAssetPathsKey = cacheKey;
            return result;
        }
        finally
        {
            _vanillaAssetPathsLock.Release();
        }
    }

    public string GetStatusReport()
    {
        string output = "";
        List<string> snapshot;
        lock (_readersLock)
        {
            snapshot = _openBsaArchiveReaders.Keys.ToList();
        }
        if (snapshot.Count == 0)
        {
            output = "No BSA archives currently loaded.";
        }
        else
        {
            output = "Loaded BSA Archives at: " + Environment.NewLine + string.Join(Environment.NewLine, snapshot.Select(x => "\t" + x));
        }

        output += Environment.NewLine;

        List<string> cachedModKeyLines;
        lock (_bsaContentsLock)
        {
            cachedModKeyLines = _bsaContents.Keys.Select(k => "\t" + k.ToString()).ToList();
        }
        if (cachedModKeyLines.Count == 0)
        {
            output += "No BSA contents currently cached";
        }
        else
        {
            output += "Cached BSA archive contents for plugins: " + Environment.NewLine + string.Join(Environment.NewLine, cachedModKeyLines);
        }

        return output;
    }
}