using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Mutagen.Bethesda.Plugins;
using Hjg.Pngcs;
using Hjg.Pngcs.Chunks;
using System.Security.Cryptography;
using Mutagen.Bethesda.Skyrim;
using Newtonsoft.Json.Linq;
using NPC_Plugin_Chooser_2.View_Models;

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
    private readonly BsaHandler _bsaHandler;
    private readonly string _executablePath;
    private string _executableVersion = "0.0.0"; // Default if query fails
    private readonly SemaphoreSlim _renderSemaphore;
    private static readonly ConcurrentDictionary<(string, string), Task<bool>> _renderTasks = new();
    
    private static readonly ConcurrentQueue<string> _outputBuffer = new ConcurrentQueue<string>();
    private const int MaxBufferedRuns = 2;
    
    public readonly string TempExtractionPath = Path.Combine(AppContext.BaseDirectory, "tmpExtraction");

    public PortraitCreator(Settings settings, EnvironmentStateProvider environmentProvider, BsaHandler bsaHandler)
    {
        _settings = settings;
        _environmentProvider = environmentProvider;
        _bsaHandler = bsaHandler;
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
    /// Finds the absolute path to an NPC's FaceGen NIF file by searching through loose files and BSA archives.
    /// Respects override behavior by returning the last valid path found.
    /// If a NIF is found in a BSA, it is extracted to a temporary location and that path is returned.
    /// </summary>
    /// <param name="npcFormKey">The FormKey of the NPC.</param>
    /// <param name="correspondingModFolders">An ordered list of mod folders to search, where later entries override earlier ones.</param>
    /// <returns>The full path to the winning NIF file (either loose or temporarily extracted), or an empty string if not found.</returns>
    public async Task<string> FindNpcNifPath(FormKey npcFormKey, VM_ModSetting modSetting)
    {
        HashSet<string> folderToSearch = new(modSetting.CorrespondingFolderPaths);
        if (!folderToSearch.Any() && (modSetting.DisplayName == VM_Mods.BaseGameModSettingName ||
            modSetting.DisplayName == VM_Mods.CreationClubModsettingName))
        {
            folderToSearch.Add(_environmentProvider.DataFolderPath);
        }
        if (npcFormKey.IsNull || !folderToSearch.Any())
        {
            return string.Empty;
        }

        // 1. Construct the relative path based on FaceGen conventions.
        string pluginName = npcFormKey.ModKey.FileName;
        string nifFileName = $"{npcFormKey.ID:X8}.nif";
        string relativeNifPath = Path.Combine(
            "Meshes", "actors", "character", "facegendata", "facegeom", pluginName, nifFileName);

        string winningPath = string.Empty;
        
        // ModKey info for BSA access
        var modKey = modSetting.CorrespondingModKeys.Last();
        if (modSetting.NpcPluginDisambiguation.TryGetValue(npcFormKey, out var trueSourcePLugin))
        {
            modKey = trueSourcePLugin;
        }
        else if (modSetting.IsAutoGenerated)
        {
            modKey = npcFormKey.ModKey;
        }

        // 2. Iterate through the provided mod folders to find the file.
        // This loop respects asset overrides: the last one found wins.
        foreach (var modFolder in folderToSearch)
        {
            if (string.IsNullOrWhiteSpace(modFolder)) continue;

            string fullPath = Path.Combine(modFolder, relativeNifPath);
            if (File.Exists(fullPath))
            {
                // Found a loose file. This is a candidate for the winning path.
                winningPath = fullPath;
            }
            // 3. If no loose file, check associated BSAs.
            else
            {
                var modSettingModel = modSetting.SaveToModel();
                var gameRelease = _environmentProvider.SkyrimVersion.ToGameRelease();
                await _bsaHandler.AddMissingModToCache(modSettingModel, gameRelease);

                if (_bsaHandler.FileExists(relativeNifPath, modKey, out var bsaPath))
                {
                    if (string.IsNullOrWhiteSpace(bsaPath)) continue;
                    
                    // open the reader for the BSA
                    _bsaHandler.OpenBsaReadersFor(modSettingModel, gameRelease);

                    // Define a unique temporary path for the extracted file.
                    string tempExtractionDir = Path.Combine(TempExtractionPath, "FaceGen", pluginName);
                    Directory.CreateDirectory(tempExtractionDir);
                    string tempNifPath = Path.Combine(tempExtractionDir, nifFileName);

                    // Extract the file synchronously. This is acceptable here as it's part of a
                    // user-initiated action that expects a result before proceeding.
                    bool extracted = _bsaHandler.ExtractFileAsync(bsaPath, relativeNifPath, tempNifPath).Result;

                    if (extracted)
                    {
                        // The extracted file is now a candidate for the winning path.
                        winningPath = tempNifPath;
                    }
                    _bsaHandler.UnloadReadersInFolders(modSetting.CorrespondingFolderPaths);
                }
            }
        }

        return winningPath;
    }

    /// <summary>
    /// Checks if a given PNG file contains metadata indicating it was generated by this tool.
    /// </summary>
    /// <param name="pngPath">The path to the PNG file.</param>
    /// <returns>True if the file exists and contains "Parameters" metadata, false otherwise.</returns>
    public bool IsAutoGenerated(string pngPath)
    {
        if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath))
        {
            return false;
        }

        try
        {
            string? metadataJson;
            using (var fs = File.OpenRead(pngPath))
            {
                var pngr = new PngReader(fs);
                // We only need the metadata, so we can skip loading the image rows for performance.
                pngr.ChunkLoadBehaviour = ChunkLoadBehaviour.LOAD_CHUNK_ALWAYS;
                pngr.ReadSkippingAllRows();
                metadataJson = pngr.GetMetadata()?.GetTxtForKey("Parameters");
                pngr.End();
            }

            // If the "Parameters" key exists and has content, it's an auto-generated image.
            return !string.IsNullOrWhiteSpace(metadataJson);
        }
        catch (Exception ex)
        {
            // If the file is not a valid PNG or is otherwise corrupt, treat it as not auto-generated.
            Debug.WriteLine($"Could not read PNG metadata for '{pngPath}'. Assuming not auto-generated. Error: {ex.Message}");
            return false;
        }
    }
    
    // Checks if an existing auto-generated mugshot is outdated.s
    public bool NeedsRegeneration(string pngPath, string nifPath, IEnumerable<string> currentDataFolders)
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
                // Not an auto-generated image, so don't regenerate it.
                return false;
            }

            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            const float EPS = 1e-3f;
            bool needsRegen = false;

            // 1. Check Program Version
            string? pngVersion = root.GetProperty("program_version").GetString();
            if (_settings.AutoUpdateOldMugshots && IsOlderVersion(pngVersion, _executableVersion))
            {
                Debug.WriteLine(
                    $"[Regen Trigger] Version mismatch. PNG: '{pngVersion}', Current: '{_executableVersion}'");
                needsRegen = true;
            }

            // 2. Check NIF Hash
            if (root.TryGetProperty("nif_sha256", out var hashEl) && hashEl.ValueKind == JsonValueKind.String)
            {
                string metadataHash = hashEl.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(nifPath) && File.Exists(nifPath))
                {
                    string currentNifHash = CalculateSha256(nifPath);
                    if (!metadataHash.Equals(currentNifHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine(
                            $"[Regen Trigger] NIF hash mismatch. PNG: '{metadataHash}', Current: '{currentNifHash}'");
                        needsRegen = true;
                    }
                }
            }
            else
            {
                Debug.WriteLine("[Regen Trigger] NIF hash missing from PNG metadata.");
                needsRegen = true;
            }

            // 3. Check Background Color
            if (root.TryGetProperty("background_color", out var bgEl) && bgEl.ValueKind == JsonValueKind.Array &&
                bgEl.GetArrayLength() == 3)
            {
                var currentColor = _settings.MugshotBackgroundColor;
                var pngColor = (R: bgEl[0].GetSingle(), G: bgEl[1].GetSingle(), B: bgEl[2].GetSingle());
                var currColorFloat = (R: currentColor.R / 255.0f, G: currentColor.G / 255.0f,
                    B: currentColor.B / 255.0f);

                if (Math.Abs(pngColor.R - currColorFloat.R) > EPS ||
                    Math.Abs(pngColor.G - currColorFloat.G) > EPS ||
                    Math.Abs(pngColor.B - currColorFloat.B) > EPS)
                {
                    Debug.WriteLine(
                        $"[Regen Trigger] Background color mismatch. PNG: ({pngColor.R:F3}, {pngColor.G:F3}, {pngColor.B:F3}), Current: ({currColorFloat.R:F3}, {currColorFloat.G:F3}, {currColorFloat.B:F3})");
                    needsRegen = true;
                }
            }
            else
            {
                Debug.WriteLine("[Regen Trigger] Background color missing from PNG metadata.");
                needsRegen = true;
            }

            // 4. Check Lighting Profile
            if (root.TryGetProperty("lighting_profile", out var lightingEl))
            {
                try
                {
                    JToken metaLighting = JToken.Parse(lightingEl.GetRawText());
                    JToken currentLighting = JToken.Parse(_settings.DefaultLightingJsonString);
                    if (!JToken.DeepEquals(metaLighting, currentLighting))
                    {
                        // Convert JSON tokens to compact strings for easy comparison in the debug output
                        string pngJson = metaLighting.ToString(Newtonsoft.Json.Formatting.None);
                        string currentJson = currentLighting.ToString(Newtonsoft.Json.Formatting.None);

                        Debug.WriteLine($"[Regen Trigger] Lighting profile mismatch.\n  PNG:     {pngJson}\n  Current: {currentJson}");
                        needsRegen = true;
                    }
                }
                catch
                {
                    Debug.WriteLine("[Regen Trigger] Failed to parse lighting JSON from PNG metadata.");
                    needsRegen = true;
                }
            }
            else
            {
                Debug.WriteLine("[Regen Trigger] Lighting profile missing from PNG metadata.");
                needsRegen = true;
            }
            
            // 4B: Check normal map hack
            if (root.TryGetProperty("normal_hack", out var normalHackEl) && 
                (normalHackEl.ValueKind == JsonValueKind.True || normalHackEl.ValueKind == JsonValueKind.False))
            {
                bool metaNormalHack = normalHackEl.GetBoolean();
                if (metaNormalHack != _settings.EnableNormalMapHack)
                {
                    Debug.WriteLine($"[Regen Trigger] Normal hack mismatch. PNG: {metaNormalHack}, Current: {_settings.EnableNormalMapHack}");
                    needsRegen = true;
                }
            }
            else
            {
                Debug.WriteLine("[Regen Trigger] Normal hack setting missing from PNG metadata.");
                needsRegen = true;
            }

            // 5. Check Camera Mode and Parameters
            bool hasCamera = root.TryGetProperty("camera", out var camEl);
            bool hasOffsets = root.TryGetProperty("mugshot_offsets", out var offsetEl);

            if (_settings.SelectedCameraMode == PortraitCameraMode.Fixed)
            {
                if (!hasCamera)
                {
                    Debug.WriteLine("[Regen Trigger] Camera data missing from PNG, but current mode is Fixed.");
                    needsRegen = true;
                }
                else
                {
                    // Check fixed camera parameters
                    camEl.TryGetProperty("yaw", out var mYawEl);
                    camEl.TryGetProperty("pitch", out var mPitchEl);
                    camEl.TryGetProperty("pos_x", out var mXEl);
                    camEl.TryGetProperty("pos_y", out var mYEl);
                    camEl.TryGetProperty("pos_z", out var mZEl);

                    float mYaw = mYawEl.GetSingle(),
                        mPitch = mPitchEl.GetSingle(),
                        mX = mXEl.GetSingle(),
                        mY = mYEl.GetSingle(),
                        mZ = mZEl.GetSingle();

                    if (Math.Abs(mYaw - _settings.CamYaw) > EPS)
                    {
                        Debug.WriteLine(
                            $"[Regen Trigger] Camera Yaw mismatch. PNG: {mYaw}, Current: {_settings.CamYaw}");
                        needsRegen = true;
                    }

                    if (Math.Abs(mPitch - _settings.CamPitch) > EPS)
                    {
                        Debug.WriteLine(
                            $"[Regen Trigger] Camera Pitch mismatch. PNG: {mPitch}, Current: {_settings.CamPitch}");
                        needsRegen = true;
                    }

                    if (Math.Abs(mX - _settings.CamX) > EPS)
                    {
                        Debug.WriteLine($"[Regen Trigger] Camera X mismatch. PNG: {mX}, Current: {_settings.CamX}");
                        needsRegen = true;
                    }

                    if (Math.Abs(mY - _settings.CamY) > EPS)
                    {
                        Debug.WriteLine($"[Regen Trigger] Camera Y mismatch. PNG: {mY}, Current: {_settings.CamY}");
                        needsRegen = true;
                    }

                    if (Math.Abs(mZ - _settings.CamZ) > EPS)
                    {
                        Debug.WriteLine($"[Regen Trigger] Camera Z mismatch. PNG: {mZ}, Current: {_settings.CamZ}");
                        needsRegen = true;
                    }
                }
            }
            else // Portrait Mode
            {
                if (!hasOffsets)
                {
                    Debug.WriteLine("[Regen Trigger] Head offsets missing from PNG, but current mode is Portrait.");
                    needsRegen = true;
                }
                else
                {
                    // Check relative camera offsets
                    float topOffset = offsetEl.GetProperty("top").GetSingle();
                    float bottomOffset = offsetEl.GetProperty("bottom").GetSingle();

                    if (Math.Abs(topOffset - _settings.HeadTopOffset) > EPS)
                    {
                        Debug.WriteLine(
                            $"[Regen Trigger] Head Top Offset mismatch. PNG: {topOffset}, Current: {_settings.HeadTopOffset}");
                        needsRegen = true;
                    }

                    if (Math.Abs(bottomOffset - _settings.HeadBottomOffset) > EPS)
                    {
                        Debug.WriteLine(
                            $"[Regen Trigger] Head Bottom Offset mismatch. PNG: {bottomOffset}, Current: {_settings.HeadBottomOffset}");
                        needsRegen = true;
                    }
                }
            }

            // 6. Check Resolution
            int resX = root.GetProperty("resolution_x").GetInt32();
            int resY = root.GetProperty("resolution_y").GetInt32();
            if (resX != _settings.ImageXRes)
            {
                Debug.WriteLine($"[Regen Trigger] X Resolution mismatch. PNG: {resX}, Current: {_settings.ImageXRes}");
                needsRegen = true;
            }

            if (resY != _settings.ImageYRes)
            {
                Debug.WriteLine($"[Regen Trigger] Y Resolution mismatch. PNG: {resY}, Current: {_settings.ImageYRes}");
                needsRegen = true;
            }
            
            // 7. Check Data Folders
            if (root.TryGetProperty("data_folders", out var dfEl) && dfEl.ValueKind == JsonValueKind.Array)
            {
                var pngDataFolders = dfEl.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                if (!pngDataFolders.SequenceEqual(currentDataFolders))
                {
                    Debug.WriteLine("[Regen Trigger] Data folders mismatch.");
                    Debug.WriteLine($"  PNG:     [{string.Join(", ", pngDataFolders)}]");
                    Debug.WriteLine($"  Current: [{string.Join(", ", currentDataFolders)}]");
                    needsRegen = true;
                }
            }
            else
            {
                Debug.WriteLine("[Regen Trigger] Data folders missing from PNG metadata.");
                needsRegen = true;
            }

            // Final decision
            if (needsRegen && _settings.AutoUpdateStaleMugshots)
            {
                Debug.WriteLine($"-> Regenerating '{Path.GetFileName(pngPath)}' due to stale metadata.");
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not parse metadata for {pngPath}. Regenerating. Error: {ex.Message}");
            return true; // Regenerate if metadata is corrupt or unreadable
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
        
        // Pass the FOV setting
        args.Append($"--fov {_settings.VerticalFOV} ");
        
        // --- Add Image and Scene Settings ---
        args.Append($"--imgX {_settings.ImageXRes} ");
        args.Append($"--imgY {_settings.ImageYRes} ");

        // Format the background color from 0-255 bytes to 0.0-1.0 floats.
        // Use InvariantCulture to ensure '.' is used as the decimal separator, regardless of system locale.
        var color = _settings.MugshotBackgroundColor;
        string bgColorString = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", 
            color.R / 255.0f, color.G / 255.0f, color.B / 255.0f);
        args.Append($"--bgcolor \"{bgColorString}\" ");
        
        args.Append($"--normal-hack {(_settings.EnableNormalMapHack ? "true" : "false")} ");

        // Pass the lighting profile as a direct JSON string.
        if (!string.IsNullOrWhiteSpace(_settings.DefaultLightingJsonString))
        {
            // It's crucial to escape the quotes within the JSON string itself so that the
            // command line parser correctly interprets the entire JSON block as a single argument value.
            string escapedJson = _settings.DefaultLightingJsonString.Replace("\"", "\\\"");
            args.Append($"--lighting-json \"{escapedJson}\" ");
        }

        // Always add pitch, yaw, and roll (they work in both modes per main.cpp)
        args.Append($"--pitch {_settings.CamPitch} ");
        args.Append($"--yaw {_settings.CamYaw} ");
        args.Append($"--roll {_settings.CamRoll} ");

        // Determine which camera mode to use
        bool useFixedCamera = _settings.SelectedCameraMode == PortraitCameraMode.Fixed &&
                              (_settings.CamX != 0.0f || _settings.CamY != 0.0f || _settings.CamZ != 0.0f);

        if (useFixedCamera)
        {
            args.Append($"--camX {_settings.CamX} ");
            args.Append($"--camY {_settings.CamY} ");
            args.Append($"--camZ {_settings.CamZ} ");
        }
        else // Use Portrait mode (relative positioning)
        {
            args.Append($"--head-top-offset {_settings.HeadTopOffset} ");
            args.Append($"--head-bottom-offset {_settings.HeadBottomOffset} ");
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
        
        var processOutput = new StringBuilder();
        processOutput.AppendLine($"--- Log for process started at {DateTime.Now:O} with args: {args.ToString()}");

        using var process = new Process { StartInfo = startInfo };

        if (redirectOutput)
        {
            // Capture standard output
            process.OutputDataReceived += (sender, e) => 
            {
                if (e.Data != null)
                {
                    Debug.WriteLine($"[NPC Creator]: {e.Data}");
                    processOutput.AppendLine($"[OUT] {e.Data}"); // ## MODIFY THIS LINE ##
                }
            };
            // Capture standard error
            process.ErrorDataReceived += (sender, e) => 
            {
                if (e.Data != null)
                {
                    Debug.WriteLine($"[NPC Creator ERROR]: {e.Data}");
                    processOutput.AppendLine($"[ERR] {e.Data}"); // ## MODIFY THIS LINE ##
                }
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
        
        processOutput.AppendLine($"--- Process exited with code {process.ExitCode} ---");
        _outputBuffer.Enqueue(processOutput.ToString());
        while (_outputBuffer.Count > MaxBufferedRuns)
        {
            _outputBuffer.TryDequeue(out _);
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
    
    public void SaveOutputLog()
    {
        if (_outputBuffer.IsEmpty)
        {
            return;
        }

        try
        {
            string? executableDir = Path.GetDirectoryName(_executablePath);
            if (executableDir == null || !Directory.Exists(executableDir))
            {
                Debug.WriteLine("[NPC Creator ERROR]: Cannot save output log, executable directory not found.");
                return;
            }

            string logPath = Path.Combine(executableDir, "NPCPortraitCreatorOutput.txt");
            var logContent = new StringBuilder();
            logContent.AppendLine($"Log saved at {DateTime.Now:O}");
            logContent.AppendLine("=====================================================");

            while (_outputBuffer.TryDequeue(out var runOutput))
            {
                logContent.AppendLine(runOutput);
                logContent.AppendLine("-----------------------------------------------------");
            }

            File.WriteAllText(logPath, logContent.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NPC Creator ERROR]: Failed to write output log. {ex.Message}");
        }
    }
}