using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Mutagen.Bethesda.Plugins;
using Hjg.Pngcs;
using Hjg.Pngcs.Chunks;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace NPC_Plugin_Chooser_2.BackEnd;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NPC_Plugin_Chooser_2.Models; // For Settings

public class PortraitCreator
{
    private readonly Settings _settings;
    private readonly EnvironmentStateProvider _environmentProvider;
    private readonly string _executablePath;
    private string _executableVersion = "0.0.0"; // Default if query fails
    private readonly SemaphoreSlim _renderSemaphore;
    private static readonly ConcurrentDictionary<(string, string), Task<bool>> _renderTasks = new();

    public PortraitCreator(Settings settings, EnvironmentStateProvider environmentProvider)
    {
        _settings = settings;
        _environmentProvider = environmentProvider;
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

    // Checks if an existing auto-generated mugshot is outdated.s
    public bool NeedsRegeneration(string pngPath, string nifPath)
    {
        if (!File.Exists(pngPath)) return true;

        try
        {
            string? metadataJson = null;
            using (var fs = File.OpenRead(pngPath))
            {
                var pngr = new PngReader(fs);
                pngr.ChunkLoadBehaviour = ChunkLoadBehaviour.LOAD_CHUNK_ALWAYS;
                pngr.ReadSkippingAllRows();
                metadataJson = pngr.GetMetadata()?.GetTxtForKey("Parameters");
                pngr.End();
            }

            if (string.IsNullOrWhiteSpace(metadataJson))
            {
                Debug.WriteLine($"No 'Parameters' metadata found in {pngPath}. Must be a manually-created mugshot.");
                return false;
            }

            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;

            string? version = root.GetProperty("program_version").GetString();
            float topOffset = root.GetProperty("mugshot_offsets").GetProperty("top").GetSingle();
            float bottomOffset = root.GetProperty("mugshot_offsets").GetProperty("bottom").GetSingle();
            int resX = root.GetProperty("resolution_x").GetInt32();
            int resY = root.GetProperty("resolution_y").GetInt32();

            // --- Camera Mismatch Logic ---
            const float EPS = 1e-3f;
            bool hasCamera = root.TryGetProperty("camera", out var camEl);
            
            // Safe float getter (works even if the JSON value is double)
            static bool TryGetFloat(JsonElement parent, string name, out float value)
            {
                value = 0f;
                if (!parent.TryGetProperty(name, out var el)) return false;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
                {
                    value = (float)d;
                    return true;
                }
                return false;
            }
            
            bool cameraMismatch = false;
            if (hasCamera)
            {
                bool okYaw   = TryGetFloat(camEl, "yaw",   out float mYaw);
                bool okPitch = TryGetFloat(camEl, "pitch", out float mPitch);
                bool okX     = TryGetFloat(camEl, "pos_x", out float mX);
                bool okY     = TryGetFloat(camEl, "pos_y", out float mY);
                bool okZ     = TryGetFloat(camEl, "pos_z", out float mZ);

                // If any camera field is missing, treat as stale to be safe.
                if (!(okYaw && okPitch && okX && okY && okZ))
                {
                    cameraMismatch = true;
                }
                else
                {
                    // Compare against your current camera settings/state.
                    // Replace these with your actual variables if they differ.
                    cameraMismatch =
                        Math.Abs(mYaw   - _settings.CamYaw)   > EPS ||
                        Math.Abs(mPitch - _settings.CamPitch) > EPS ||
                        Math.Abs(mX     - _settings.CamX)  > EPS ||
                        Math.Abs(mY     - _settings.CamY)  > EPS ||
                        Math.Abs(mZ     - _settings.CamZ)  > EPS;
                }
            }
            else
            {
                // If the old PNGs don’t have camera info, you can decide policy:
                // treat as stale so they get regenerated when AutoUpdateStaleMugshots is on
                cameraMismatch = true;
            }

            // Compare NIF hash
            bool hashMismatch = false;
            if (root.TryGetProperty("nif_sha256", out var hashEl) && hashEl.ValueKind == JsonValueKind.String)
            {
                string metadataHash = hashEl.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(nifPath) && File.Exists(nifPath))
                {
                    string currentNifHash = CalculateSha256(nifPath);
                    if (!metadataHash.Equals(currentNifHash, StringComparison.OrdinalIgnoreCase))
                    {
                        hashMismatch = true;
                    }
                }
            }
            else { hashMismatch = true; } // If old PNG has no hash, treat as stale.

            // Compare background color
            bool bgColorMismatch = false;
            if (root.TryGetProperty("background_color", out var bgEl) && bgEl.ValueKind == JsonValueKind.Array && bgEl.GetArrayLength() == 3)
            {
                var currentColor = _settings.MugshotBackgroundColor;
                if (Math.Abs(bgEl[0].GetSingle() - currentColor.R / 255.0f) > EPS ||
                    Math.Abs(bgEl[1].GetSingle() - currentColor.G / 255.0f) > EPS ||
                    Math.Abs(bgEl[2].GetSingle() - currentColor.B / 255.0f) > EPS)
                {
                    bgColorMismatch = true;
                }
            }
            else { bgColorMismatch = true; } // If old PNG lacks color info, treat as stale.

            // Compare lighting configuration (whitespace-agnostic)
            bool lightingMismatch = false;
            if (root.TryGetProperty("lighting_profile", out var lightingEl))
            {
                try
                {
                    JToken metaLighting = JToken.Parse(lightingEl.GetRawText());
                    JToken currentLighting = JToken.Parse(_settings.DefaultLightingJsonString);

                    if (!JToken.DeepEquals(metaLighting, currentLighting))
                    {
                        lightingMismatch = true;
                    }
                }
                catch { lightingMismatch = true; } // If parsing fails for either, treat as stale.
            }
            else { lightingMismatch = true; } // If old PNG lacks lighting info, treat as stale.

            bool isDeprecated = _settings.AutoUpdateOldMugshots && IsOlderVersion(version, _executableVersion);
            bool isStale =
                hashMismatch ||
                bgColorMismatch ||
                lightingMismatch ||
                Math.Abs(topOffset - _settings.HeadTopOffset) > EPS ||
                Math.Abs(bottomOffset - _settings.HeadBottomOffset) > EPS ||
                resX != _settings.ImageXRes ||
                resY != _settings.ImageYRes ||
                cameraMismatch;

            if ((_settings.AutoUpdateOldMugshots && isDeprecated) ||
                (_settings.AutoUpdateStaleMugshots && isStale))
            {
                Debug.WriteLine($"Metadata mismatch for {pngPath}. Regenerating.");
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not parse metadata for {pngPath}. Regenerating. Error: {ex.Message}");
            return true;
        }

        return false;
    }

    private static bool IsOlderVersion(string? pngVersion, string exeVersion)
    {
        if (string.IsNullOrWhiteSpace(pngVersion)) return false; // treat missing current to avoid overwriting non-autogen mugshots
        // System.Version handles a.b.c cleanly
        if (!Version.TryParse(pngVersion.Trim(), out var vPng)) return true;
        if (!Version.TryParse(exeVersion.Trim(), out var vExe)) return true;
        return vPng.CompareTo(vExe) < 0;
    }

    /// <summary>
    /// Queues a request to generate a portrait. This method is thread-safe and
    /// prevents duplicate render jobs for the same input/output pair.
    /// </summary>
    public Task<bool> GeneratePortraitAsync(string nifPath, IEnumerable<string> dataFolderPaths, string outputPath)
    {
        var renderKey = (nifPath, outputPath);

        return _renderTasks.GetOrAdd(renderKey, key =>
        {
            // 1. Create the task to process the render request.
            var renderTask = ProcessRenderRequestAsync(key.Item1, dataFolderPaths, key.Item2);

            // 2. Attach a continuation. This tells the task to perform an action
            //    (removing itself from the dictionary) as soon as it's finished,
            //    regardless of whether it succeeded, failed, or was canceled.
            //    This allows re-render requests for mugshots with stale metadata
            renderTask.ContinueWith(
                _ => _renderTasks.TryRemove(key, out _),
                TaskScheduler.Default // Use a background thread for the removal
            );

            // 3. Return the original task to the caller.
            return renderTask;
        });
    }

    /// <summary>
    /// Waits for an available render slot and then executes the portrait creator process.
    /// </summary>
    private async Task<bool> ProcessRenderRequestAsync(string nifPath, IEnumerable<string> dataFolderPaths, string outputPath)
    {
        // Wait until a slot in the semaphore is free.
        await _renderSemaphore.WaitAsync();
        try
        {
            // Once a slot is acquired, run the actual process.
            return await RunProcessInternalAsync(nifPath, dataFolderPaths, outputPath);
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
    private async Task<bool> RunProcessInternalAsync(string nifPath, IEnumerable<string> dataFolderPaths, string outputPath)
    {
        if (!File.Exists(_executablePath))
        {
            Debug.WriteLine($"[NPC Creator ERROR]: Executable not found at '{_executablePath}'");
            return false;
        }

        var gameDataDirectory = _environmentProvider.DataFolderPath;

        bool redirectOutput = true; 

        var args = new StringBuilder();
        args.Append("--headless ");
        args.Append($"-f \"{nifPath}\" ");
        args.Append($"-o \"{outputPath}\" ");
        
        // --- Add Game and Mod Data Paths ---
        // The base game "Data" folder (lowest priority)
        if (!string.IsNullOrWhiteSpace(gameDataDirectory))
        {
            args.Append($"--gamedata \"{gameDataDirectory}\" ");
        }
        // Each individual mod folder (higher priority)
        foreach (var dataFolder in dataFolderPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            args.Append($"--data \"{dataFolder}\" ");
        }
        
        // --- Add Image and Scene Settings ---
        args.Append($"--imgX {_settings.ImageXRes} ");
        args.Append($"--imgY {_settings.ImageYRes} ");

        // Format the background color from 0-255 bytes to 0.0-1.0 floats.
        // Use InvariantCulture to ensure '.' is used as the decimal separator, regardless of system locale.
        var color = _settings.MugshotBackgroundColor;
        string bgColorString = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", 
            color.R / 255.0f, color.G / 255.0f, color.B / 255.0f);
        args.Append($"--bgcolor \"{bgColorString}\" ");

        // Pass the lighting profile as a direct JSON string.
        if (!string.IsNullOrWhiteSpace(_settings.DefaultLightingJsonString))
        {
            // It's crucial to escape the quotes within the JSON string itself so that the
            // command line parser correctly interprets the entire JSON block as a single argument value.
            string escapedJson = _settings.DefaultLightingJsonString.Replace("\"", "\\\"");
            args.Append($"--lighting-json \"{escapedJson}\" ");
        }

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
    
    private static string CalculateSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}