﻿using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Noggog;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class BsaHandler : OptionalUIModule
{
    private Dictionary<FilePath, IArchiveReader> _openBsaArchiveReaders = new();
    
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