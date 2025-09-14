using System.Collections.Concurrent;
using System.IO;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NPC_Plugin_Chooser_2.Models; // For Settings

public class PortraitCreator
{
    private readonly Settings _settings;
    private readonly string _executablePath;
    private string _executableVersion = "0.0.0"; // Default if query fails
    private readonly SemaphoreSlim _renderSemaphore;
    private static readonly ConcurrentDictionary<(string, string), Task<bool>> _renderTasks = new();

    public PortraitCreator(Settings settings)
    {
        _settings = settings;
        _executablePath = Path.Combine(AppContext.BaseDirectory, "NPC Portrait Creator", "NPCPortraitCreator.exe");

        // Initialize the semaphore with the value from settings.
        _renderSemaphore =
            new SemaphoreSlim(_settings.MaxParallelPortraitRenders, _settings.MaxParallelPortraitRenders);
    }
    
    /// <summary>
    /// Asynchronously queries the C++ executable for its version number.
    /// This should be called once during application startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!File.Exists(_executablePath))
        {
            Debug.WriteLine($"[NPC Creator ERROR]: Executable not found at '{_executablePath}' for version check.");
            return;
        }

        var startInfo = new ProcessStartInfo(_executablePath, "--version")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            Debug.WriteLine("[NPC Creator ERROR]: Failed to start process for version check.");
            return;
        }
            
        // Read the single line of output from the process.
        string version = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(version))
        {
            _executableVersion = version.Trim();
            Debug.WriteLine($"[NPC Creator]: Detected version {_executableVersion}");
        }
        else
        {
            Debug.WriteLine("[NPC Creator ERROR]: Failed to get version from executable.");
        }
    }

    /// <summary>
    /// Finds the absolute path to an NPC's FaceGen NIF file by searching through a list of mod folders.
    /// Respects override behavior by returning the last valid path found.
    /// </summary>
    /// <param name="npcFormKey">The FormKey of the NPC.</param>
    /// <param name="correspondingModFolders">An ordered list of mod folders to search, where later entries override earlier ones.</param>
    /// <returns>The full path to the winning NIF file, or an empty string if not found.</returns>
    public string FindNpcNifPath(FormKey npcFormKey, IEnumerable<string> correspondingModFolders)
    {
        if (npcFormKey.IsNull || correspondingModFolders == null)
        {
            return string.Empty;
        }

        // 1. Construct the relative path based on FaceGen conventions.
        // The structure is derived from the comments and logic in FaceGenScanner.cs.
        string pluginName = npcFormKey.ModKey.FileName;
        string nifFileName = $"{npcFormKey.ID:X8}.nif"; // e.g., "0001A696.nif"

        string relativeNifPath = Path.Combine(
            "Meshes", "actors", "character", "facegendata", "facegeom", pluginName, nifFileName);

        string winningPath = string.Empty;

        // 2. Iterate through the provided mod folders to find the file.
        foreach (var modFolder in correspondingModFolders)
        {
            if (string.IsNullOrWhiteSpace(modFolder)) continue;

            string fullPath = Path.Combine(modFolder, relativeNifPath);
            if (File.Exists(fullPath))
            {
                // This path is a valid candidate. By always overwriting the 'winningPath',
                // we ensure that the last valid file found in the list is the one we return.
                winningPath = fullPath;
            }
        }

        return winningPath;
    }

    // Checks if an existing auto-generated mugshot is outdated.
    public bool NeedsRegeneration(string pngPath)
    {
        if (!File.Exists(pngPath)) return true;
/*
        try
        {
            // NOTE: This requires a library capable of reading PNG tEXt chunks.
            // For this example, we'll assume a utility class "PngMetadataReader" exists.
            var metadataJson = PngMetadataReader.ReadTextChunk(pngPath, "Parameters");
            if (string.IsNullOrWhiteSpace(metadataJson)) return true; // No metadata, regenerate

            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;

            var version = root.GetProperty("program_version").GetString();
            var topOffset = root.GetProperty("mugshot_offsets").GetProperty("top").GetSingle();
            var bottomOffset = root.GetProperty("mugshot_offsets").GetProperty("bottom").GetSingle();
            var resX = root.GetProperty("resolution_x").GetInt32();
            var resY = root.GetProperty("resolution_y").GetInt32();

            // Compare saved metadata with current settings
            if (version != CurrentVersion ||
                Math.Abs(topOffset - _settings.HeadTopOffset) > 0.001f ||
                Math.Abs(bottomOffset - _settings.HeadBottomOffset) > 0.001f ||
                resX != _settings.ImageXRes ||
                resY != _settings.ImageYRes)
            {
                return true; // Parameters have changed, regenerate
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not parse metadata for {pngPath}. Regenerating. Error: {ex.Message}");
            return true;
        }
*/
        return false; // Everything matches, no regeneration needed
    }

    /// <summary>
    /// Queues a request to generate a portrait. This method is thread-safe and
    /// prevents duplicate render jobs for the same input/output pair.
    /// </summary>
    public Task<bool> GeneratePortraitAsync(string nifPath, string outputPath)
    {
        var renderKey = (nifPath, outputPath);

        // GetOrAdd ensures that for a given NIF/output pair, the render process
        // is only ever created and queued once. Subsequent calls will await the same task.
        return _renderTasks.GetOrAdd(renderKey, key => ProcessRenderRequestAsync(key.Item1, key.Item2));
    }

    /// <summary>
    /// Waits for an available render slot and then executes the portrait creator process.
    /// </summary>
    private async Task<bool> ProcessRenderRequestAsync(string nifPath, string outputPath)
    {
        // Wait until a slot in the semaphore is free.
        await _renderSemaphore.WaitAsync();
        try
        {
            // Once a slot is acquired, run the actual process.
            return await RunProcessInternalAsync(nifPath, outputPath);
        }
        finally
        {
            // CRITICAL: Release the semaphore slot so another queued task can start.
            _renderSemaphore.Release();
        }
    }

    /// <summary>
    /// Contains the core logic for launching and monitoring the external executable.
    /// </summary>
    private async Task<bool> RunProcessInternalAsync(string nifPath, string outputPath)
    {
        if (!File.Exists(_executablePath))
        {
            Debug.WriteLine($"[NPC Creator ERROR]: Executable not found at '{_executablePath}'");
            return false;
        }

        bool redirectOutput = true; 

        var args = new StringBuilder();
        args.Append("--headless ");
        args.Append($"-f \"{nifPath}\" ");
        args.Append($"-o \"{outputPath}\" ");
        args.Append($"--imgX {_settings.ImageXRes} ");
        args.Append($"--imgY {_settings.ImageYRes} ");

        // NEW: Logic to select which camera parameters to use
        bool useFixedCamera = _settings.SelectedCameraMode == PortraitCameraMode.Fixed &&
                              (_settings.CamX != 0.0f || _settings.CamY != 0.0f || _settings.CamZ != 0.0f ||
                               _settings.CamPitch != 0.0f || _settings.CamYaw != 0.0f);

        if (useFixedCamera)
        {
            args.Append($"--camX {_settings.CamX} ");
            args.Append($"--camY {_settings.CamY} ");
            args.Append($"--camZ {_settings.CamZ} ");
            args.Append($"--pitch {_settings.CamPitch} ");
            args.Append($"--yaw {_settings.CamYaw}");
        }
        else // Use relative mode by default or if fixed values are all zero
        {
            args.Append($"--head-top-offset {_settings.HeadTopOffset} ");
            args.Append($"--head-bottom-offset {_settings.HeadBottomOffset}");
        }

        string executableDir = Path.GetDirectoryName(_executablePath);
        
        var startInfo = new ProcessStartInfo(_executablePath, args.ToString())
        {
            WorkingDirectory = executableDir,
            CreateNoWindow = true,
            UseShellExecute = false, // Must be false to redirect I/O
        };
        
        if (redirectOutput)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        using var process = new Process { StartInfo = startInfo };

        if (redirectOutput)
        {
            // Capture standard output
            process.OutputDataReceived += (sender, e) => 
            {
                if (e.Data != null) Debug.WriteLine($"[NPC Creator]: {e.Data}");
            };
            // Capture standard error
            process.ErrorDataReceived += (sender, e) => 
            {
                if (e.Data != null) Debug.WriteLine($"[NPC Creator ERROR]: {e.Data}");
            };
        }

        process.Start();

        if (redirectOutput)
        {
            // Asynchronously begin reading the output streams.
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        await process.WaitForExitAsync();
            
        if (process.ExitCode != 0)
        {
            Debug.WriteLine($"[NPC Creator ERROR]: Process exited with code {process.ExitCode}.");
        }
            
        return process.ExitCode == 0;
    }
}