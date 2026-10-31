using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Mutagen.Bethesda.Plugins;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Developer render harness: when a <c>RenderHarness.json</c> file exists next
/// to the exe, the app — after normal startup completes — renders the mugshots
/// it lists (optionally once per parameter variant), writes the PNGs plus a
/// <c>RenderHarness.log</c> to the configured output directory, and (by
/// default) exits. Built for iterative renderer tuning: sweep a parameter like
/// SsaoThickness across variants on a fixed set of NPCs and diff the PNGs,
/// without clicking through the UI per value.
///
/// Config shape:
/// <code>
/// {
///   "outputDirectory": "S:\\temp\\harness",
///   "exitWhenDone": true,
///   "renders": [
///     { "modName": "Beards of Power - Sons of Skyrim", "npcFormKey": "013261:Skyrim.esm" }
///   ],
///   "variants": [
///     { "name": "thick-0.5", "settings": { "SsaoThickness": 0.5 } },
///     { "name": "thick-1.5", "settings": { "SsaoThickness": 1.5 } }
///   ]
/// }
/// </code>
/// <c>settings</c> keys are <see cref="InternalMugshotSettings"/> property
/// names (case-insensitive), applied in-memory for the variant's renders and
/// restored afterwards so the user's persisted settings are untouched. With no
/// <c>variants</c>, everything renders once under the current settings.
/// Renders always bypass the mugshot staleness cache (PNGs go to the harness
/// output directory, not the autogen folder).
///
/// <para><b>Determinism (measured 2026-07-14, RTX 5090):</b> output is fully
/// deterministic WITHIN a process — render #2 onward is bit-identical, and
/// render #1 differs only by ±1 LSB on a handful of alpha-blended fragments
/// (brows / lashes / hairline; ≤15 px at 900×900) as the driver's shader
/// microcode settles. ACROSS processes it is NOT bit-exact: the NVIDIA
/// driver's async shader compiler nondeterministically lands on one of a few
/// discrete SASS schedules per process, so ~95% of processes agree to ±1 LSB
/// but ~2-5% land in a distinct, internally-deterministic state that shades
/// skin/hair ~+2.8 luma warmer (bit-identical whenever it recurs — two
/// independent processes reproduced it exactly). That per-process state is
/// fixed for the whole process, so <b>burn-in does NOT remove it</b> (a
/// burn-in render lands in the same state as the real one). It is not
/// fixable from portable GL app code; see the render-determinism memory.
/// <c>burnInRenders</c> only removes the small render-#1 warm-up wobble by
/// making the sweep's renders use the process's settled steady-state
/// microcode — set <c>"burnInRenders": 1</c> (the first listed render is
/// drawn that many times up front, under the base settings before any
/// variant, and discarded). Launch with <c>DOTNET_TieredCompilation=0</c> to
/// remove JIT re-tiering as a confound, though it was verified not to affect
/// pixels.</para>
/// </summary>
public static class RenderHarnessRunner
{
    public const string TriggerFileName = "RenderHarness.json";

    public static string TriggerPath => Path.Combine(AppContext.BaseDirectory, TriggerFileName);

    public static bool ConfigExists => File.Exists(TriggerPath);

    /// <summary>The user's persisted UsePortraitCreatorFallback, stashed by
    /// App startup when it suppresses the NPC bar's autogen kick for a harness
    /// run (see App.xaml.cs). Restored onto the settings model at the end of
    /// <see cref="RunAsync"/> so the exit-time settings save writes the user's
    /// real value. Null when no suppression happened.</summary>
    public static bool? SuppressedUsePortraitCreatorFallback;

    public class HarnessConfig
    {
        public string OutputDirectory { get; set; } = "";
        public bool ExitWhenDone { get; set; } = true;
        /// <summary>Number of throwaway renders of the first listed mugshot to
        /// draw before the sweep, letting the GPU driver's background shader
        /// optimization settle so the sweep's PNGs are bit-stable. See the
        /// class-level Determinism remarks. 0 (default) = no burn-in.</summary>
        public int BurnInRenders { get; set; } = 0;
        public List<HarnessRender> Renders { get; set; } = new();
        public List<HarnessVariant> Variants { get; set; } = new();

        /// <summary>When non-null, runs the click simulation (before any
        /// renders/variants): for each target NPC, every renderable mod
        /// providing it gets a mugshot render fired CONCURRENTLY — the same
        /// burst VM_NpcSelectionBar.TriggerAsyncMugshotGeneration produces on
        /// an NPC click, throttled by the generator's own render semaphore and
        /// the renderer's serialized GL queue. Per-burst wall time and
        /// broadcast-lookup stats land in RenderHarness.log; per-render phase
        /// timings in RenderLogs\RenderTimings.csv when LogRenderTimings.txt
        /// exists.</summary>
        public ClickSimulationConfig? ClickSimulation { get; set; }

        /// <summary>Renders each variant's <see cref="Renders"/> all at once
        /// (one GenerateAsync per render, bounded by the generator's render
        /// limiter) instead of one after another: the load a mugshot batch puts
        /// on the asset resolver. Off by default.</summary>
        public bool ParallelBatch { get; set; }

        /// <summary>With <see cref="ParallelBatch"/>, runs <see cref="ClickSimulation"/>
        /// at the same time as the first variant's batch instead of before it, so
        /// tile bursts compete with the batch as live tiles do.</summary>
        public bool ConcurrentClickSimulation { get; set; }

        /// <summary>Render limit for the run (the UI allows 1-32), restored
        /// afterwards. Null keeps the user's MaxParallelPortraitRenders.</summary>
        public int? MaxParallelRenders { get; set; }

        /// <summary>Mods (by DisplayName) to run the Mod Issues scan on after
        /// the renders, ignoring its cache; the results go to ModIssueScan.json.
        /// The scan also stores them in ModIssuesCache.json, as the tab does.</summary>
        public List<string> ModIssueScanMods { get; set; } = new();
    }

    public class ClickSimulationConfig
    {
        /// <summary>NPCs to "click", in order. Empty → auto-pick the
        /// <see cref="NpcCount"/> NPCs provided by the most renderable mods,
        /// which maximizes queue depth like a popular vanilla NPC does.</summary>
        public List<string> NpcFormKeys { get; set; } = new();

        /// <summary>How many NPCs to auto-pick when <see cref="NpcFormKeys"/> is empty.</summary>
        public int NpcCount { get; set; } = 3;

        /// <summary>Tile cap per click (the UI renders one tile per providing mod).</summary>
        public int MaxTiles { get; set; } = 25;

        /// <summary>Re-click the FIRST NPC again at the end: a fully-warm burst
        /// (per-NPC caches hot) separating session-warmup cost from steady state.</summary>
        public bool RepeatFirstNpc { get; set; } = true;

        /// <summary>"engineOrder" (default; current algorithm) or "legacy"
        /// (pre-2.8.0 strict resolution via <see cref="RenderResolutionMode.ForceLegacy"/>).</summary>
        public string ResolutionMode { get; set; } = "engineOrder";
    }

    public class HarnessRender
    {
        public string ModName { get; set; } = "";
        public string NpcFormKey { get; set; } = "";
        /// <summary>Optional file name (no directory); defaults to
        /// "&lt;ModName&gt;_&lt;FormKey&gt;.png" sanitized for the filesystem.</summary>
        public string? FileName { get; set; }
    }

    public class HarnessVariant
    {
        public string Name { get; set; } = "";
        public Dictionary<string, JToken> Settings { get; set; } = new();
    }

    /// <summary>
    /// Loads the trigger config and runs every variant × render combination.
    /// Returns true when the config asked the app to exit afterwards.
    /// Never throws — all failures land in RenderHarness.log (and the return
    /// still honors ExitWhenDone so an unattended sweep can't hang the app
    /// open on an error).
    /// </summary>
    public static async Task<bool> RunAsync(Settings settings, InternalMugshotGenerator generator,
        SkyRender.GameAssetResolver assetResolver, ModIssueScanner modIssueScanner)
    {
        HarnessConfig config;
        var log = new StringBuilder();
        log.AppendLine($"RenderHarness run started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        try
        {
            config = JsonConvert.DeserializeObject<HarnessConfig>(File.ReadAllText(TriggerPath))
                     ?? throw new InvalidOperationException("config deserialized to null");
        }
        catch (Exception ex)
        {
            // No output dir known yet — fall back to a log next to the exe.
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "RenderHarness.log"),
                $"FATAL: could not parse {TriggerFileName}: {ex.Message}");
            return true; // unparseable config: still exit rather than hang a broken sweep
        }

        string outDir = string.IsNullOrWhiteSpace(config.OutputDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "RenderHarnessOutput")
            : config.OutputDirectory;

        int userMaxParallel = settings.MaxParallelPortraitRenders;
        try
        {
            Directory.CreateDirectory(outDir);

            if (config.MaxParallelRenders is { } maxParallel)
            {
                generator.SetMaxParallelRenders(maxParallel);
                log.AppendLine($"MaxParallelRenders={maxParallel} (user setting {userMaxParallel})");
            }

            string cacheDir = Path.Combine(AppContext.BaseDirectory, "SkyRenderCache");
            var statsBefore = assetResolver.GetBsaAssetStats();
            var (cacheFilesBefore, cacheBytesBefore) = CountFiles(cacheDir);
            log.AppendLine($"BsaAssetMode: inMemory={assetResolver.ServesBsaAssetsFromMemory} " +
                           $"budget={assetResolver.InMemoryAssetBudgetBytes / (1024 * 1024)}MB");
            log.AppendLine($"SkyRenderCache before: files={cacheFilesBefore} bytes={cacheBytesBefore}");
            log.AppendLine($"BsaAssetStats before: {statsBefore}");
            var runSw = Stopwatch.StartNew();

            // Own buffer: when concurrent it appends while the batch logs.
            var clickLog = new StringBuilder();
            Task clickSimulation = Task.CompletedTask;
            if (config.ClickSimulation != null)
            {
                clickSimulation = RunClickSimulationAsync(settings, generator, config.ClickSimulation, outDir, clickLog);
                if (!(config.ParallelBatch && config.ConcurrentClickSimulation))
                {
                    await clickSimulation;
                }
            }

            // Snapshot the mugshot settings so variant overrides never leak
            // into the user's persisted Settings.json (the app saves settings
            // on exit). PopulateObject restores into the same instance.
            string snapshot = JsonConvert.SerializeObject(settings.InternalMugshot);

            var variants = config.Variants.Count > 0
                ? config.Variants
                : new List<HarnessVariant> { new() { Name = "base" } };

            // Burn-in: render the first mugshot N times under the base settings
            // and discard the PNGs, so the sweep proper starts from the GPU
            // driver's settled steady state (see class Determinism remarks).
            if (config.BurnInRenders > 0 && config.Renders.Count > 0)
            {
                string burnDir = Path.Combine(outDir, "_burnin");
                Directory.CreateDirectory(burnDir);
                for (int i = 0; i < config.BurnInRenders; i++)
                {
                    await RunOneAsync(settings, generator, config.Renders[0],
                        $"burn-in {i + 1}/{config.BurnInRenders}", burnDir, log);
                }
                try { Directory.Delete(burnDir, recursive: true); }
                catch { /* best-effort — leftover burn-in PNGs are harmless */ }
            }

            try
            {
                foreach (var variant in variants)
                {
                    string variantName = string.IsNullOrWhiteSpace(variant.Name) ? "unnamed" : variant.Name;
                    string variantDir = Path.Combine(outDir, Sanitize(variantName));
                    Directory.CreateDirectory(variantDir);

                    // Reset to the snapshot, then apply this variant's overrides,
                    // so variants are independent rather than cumulative.
                    JsonConvert.PopulateObject(snapshot, settings.InternalMugshot);
                    ApplyOverrides(settings.InternalMugshot, variant.Settings, log, variantName);

                    if (config.ParallelBatch)
                    {
                        var batchSw = Stopwatch.StartNew();
                        var lines = await Task.WhenAll(config.Renders.Select(render => Task.Run(async () =>
                        {
                            var renderLog = new StringBuilder();
                            await RunOneAsync(settings, generator, render, variantName, variantDir, renderLog);
                            return renderLog.ToString();
                        })));
                        foreach (var line in lines) log.Append(line);
                        log.AppendLine($"[{variantName}] ParallelBatch: renders={config.Renders.Count} wallMs={batchSw.ElapsedMilliseconds}");
                    }
                    else
                    {
                        foreach (var render in config.Renders)
                        {
                            await RunOneAsync(settings, generator, render, variantName, variantDir, log);
                        }
                    }
                }
            }
            finally
            {
                JsonConvert.PopulateObject(snapshot, settings.InternalMugshot);
            }

            await clickSimulation;
            runSw.Stop();
            log.Append(clickLog);

            var statsAfter = assetResolver.GetBsaAssetStats();
            var (cacheFilesAfter, cacheBytesAfter) = CountFiles(cacheDir);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            log.AppendLine($"Run wallMs={runSw.ElapsedMilliseconds}");
            log.AppendLine($"BsaAssetStats after: {statsAfter}");
            log.AppendLine($"BsaAssetStats delta: materialized={statsAfter.Materialized - statsBefore.Materialized} " +
                           $"failures={statsAfter.MaterializeFailures - statsBefore.MaterializeFailures} " +
                           $"bytes={statsAfter.MaterializedBytes - statsBefore.MaterializedBytes} " +
                           $"ms={statsAfter.MaterializeMs - statsBefore.MaterializeMs:F0} " +
                           $"storeHits={statsAfter.StoreHits - statsBefore.StoreHits} " +
                           $"reReads={statsAfter.ReReads - statsBefore.ReReads} " +
                           $"reReadBytes={statsAfter.ReReadBytes - statsBefore.ReReadBytes} " +
                           $"reReadMs={statsAfter.ReReadMs - statsBefore.ReReadMs:F0} " +
                           $"residentBytes={statsAfter.StoreResidentBytes}");
            log.AppendLine($"SkyRenderCache after: files={cacheFilesAfter} bytes={cacheBytesAfter} " +
                           $"(+{cacheFilesAfter - cacheFilesBefore} files)");
            log.AppendLine($"Process: peakWorkingSet={process.PeakWorkingSet64} peakPagedMemory={process.PeakPagedMemorySize64} " +
                           $"privateBytes={process.PrivateMemorySize64} gcTotalAllocated={GC.GetTotalAllocatedBytes()}");

            if (config.ModIssueScanMods.Count > 0)
            {
                await RunModIssueScanAsync(settings, modIssueScanner, assetResolver, config.ModIssueScanMods, outDir, log);
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"FATAL: {ex}");
        }
        finally
        {
            generator.SetMaxParallelRenders(userMaxParallel);
        }

        // Undo the startup autogen suppression (see App.xaml.cs) so the
        // exit-time settings save persists the user's real value.
        if (SuppressedUsePortraitCreatorFallback is { } restored)
        {
            settings.UsePortraitCreatorFallback = restored;
            SuppressedUsePortraitCreatorFallback = null;
        }

        try { File.WriteAllText(Path.Combine(outDir, "RenderHarness.log"), log.ToString()); }
        catch { /* best-effort */ }

        return config.ExitWhenDone;
    }

    /// <summary>
    /// Simulates clicking NPC(s) in the NPCs tab: for each target, the burst
    /// of concurrent GenerateAsync calls the tile kick produces (one per
    /// renderable providing mod, all launched at once via Task.Run — the
    /// generator's MaxParallelPortraitRenders semaphore and the serialized GL
    /// queue then shape execution exactly as in the UI). PNGs go to
    /// per-burst scratch dirs so every render is real (no staleness cache).
    /// </summary>
    private static async Task RunClickSimulationAsync(
        Settings settings,
        InternalMugshotGenerator generator,
        ClickSimulationConfig cfg,
        string outDir,
        StringBuilder log)
    {
        bool legacy = string.Equals(cfg.ResolutionMode, "legacy", StringComparison.OrdinalIgnoreCase);
        RenderResolutionMode.ForceLegacy = legacy;
        log.AppendLine($"ClickSim: resolutionMode={(legacy ? "legacy" : "engineOrder")} maxTiles={cfg.MaxTiles}");
        try
        {
            static bool Renderable(ModSetting ms) =>
                ms.CorrespondingFolderPaths.Any() || ms.IsAutoGenerated;

            var targets = new List<FormKey>();
            if (cfg.NpcFormKeys is { Count: > 0 })
            {
                foreach (var s in cfg.NpcFormKeys)
                {
                    if (FormKey.TryFactory(s, out var fk)) targets.Add(fk);
                    else log.AppendLine($"ClickSim: invalid FormKey '{s}' — skipped");
                }
            }
            else
            {
                // Auto-pick: NPCs provided by the most renderable mods (deepest queues).
                var counts = new Dictionary<FormKey, int>();
                foreach (var ms in settings.ModSettings)
                {
                    if (!Renderable(ms)) continue;
                    foreach (var fk in ms.NpcFormKeys)
                    {
                        counts[fk] = counts.TryGetValue(fk, out var c) ? c + 1 : 1;
                    }
                }

                targets = counts
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key.ToString(), StringComparer.OrdinalIgnoreCase)
                    .Take(Math.Max(1, cfg.NpcCount))
                    .Select(kv => kv.Key)
                    .ToList();
            }

            if (targets.Count == 0)
            {
                log.AppendLine("ClickSim: no target NPCs resolved — nothing to do");
                return;
            }

            var bursts = new List<(string Label, FormKey Npc)>();
            for (int i = 0; i < targets.Count; i++) bursts.Add(($"click{i + 1}", targets[i]));
            if (cfg.RepeatFirstNpc) bursts.Add(("reclick1-warm", targets[0]));

            foreach (var (label, npc) in bursts)
            {
                // Tile set in the UI's order: the mod natively defining the NPC
                // first, then name — the same sort CreateMugShotViewModelsAsync
                // applies before the generation kick iterates.
                var tiles = settings.ModSettings
                    .Where(ms => Renderable(ms) && ms.NpcFormKeys.Contains(npc))
                    .OrderByDescending(ms => ms.CorrespondingModKeys.Contains(npc.ModKey))
                    .ThenBy(ms => ms.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Take(Math.Max(1, cfg.MaxTiles))
                    .ToList();

                if (tiles.Count == 0)
                {
                    log.AppendLine($"ClickSim {label}: no renderable mod provides {npc} — skipped");
                    continue;
                }

                string burstDir = Path.Combine(outDir, Sanitize($"{(legacy ? "legacy" : "engine")}_{label}"));
                Directory.CreateDirectory(burstDir);
                BroadcastLookupStats.Reset();
                var sw = Stopwatch.StartNew();

                // Per-tile wall time measured around the whole GenerateAsync —
                // semaphore wait + NPC2-side pre-render (record resolution,
                // attire walk, archive sweeps) + the renderer. RenderTimings.csv
                // only covers the RenderToPngAsync slice, so this is what
                // catches host-side pre-render regressions.
                var tasks = tiles
                    .Select(ms =>
                    {
                        string path = Path.Combine(burstDir, Sanitize(ms.DisplayName) + ".png");
                        return Task.Run(async () =>
                        {
                            var tileSw = Stopwatch.StartNew();
                            bool ok = await generator.GenerateAsync(npc, ms, path);
                            return (Ok: ok, Ms: tileSw.ElapsedMilliseconds, Mod: ms.DisplayName);
                        });
                    })
                    .ToList();
                var results = await Task.WhenAll(tasks);
                sw.Stop();

                int okCount = results.Count(r => r.Ok);
                var tileMs = results.Select(r => r.Ms).OrderBy(v => v).ToList();
                long sumTileMs = tileMs.Sum();
                long medTileMs = tileMs[tileMs.Count / 2];
                var slowest = results.OrderByDescending(r => r.Ms).First();
                log.AppendLine(
                    $"ClickSim {label}: npc={npc} tiles={tiles.Count} ok={okCount} fail={tiles.Count - okCount} " +
                    $"wallMs={sw.ElapsedMilliseconds} tileTotalMs sum={sumTileMs} med={medTileMs} " +
                    $"max={slowest.Ms}({slowest.Mod}) | " + BroadcastLookupStats.Report());
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"ClickSim FATAL: {ex}");
        }
        finally
        {
            RenderResolutionMode.ForceLegacy = false;
        }
    }

    private static async Task RunOneAsync(
        Settings settings,
        InternalMugshotGenerator generator,
        HarnessRender render,
        string variantName,
        string variantDir,
        StringBuilder log)
    {
        string label = $"[{variantName}] {render.ModName} / {render.NpcFormKey}";
        try
        {
            if (!FormKey.TryFactory(render.NpcFormKey, out var npcFormKey))
            {
                log.AppendLine($"{label}: FAIL — invalid FormKey string");
                return;
            }

            var modSetting = settings.ModSettings.FirstOrDefault(m =>
                string.Equals(m.DisplayName, render.ModName, StringComparison.OrdinalIgnoreCase));
            if (modSetting == null)
            {
                log.AppendLine($"{label}: FAIL — no ModSetting with that DisplayName");
                return;
            }

            string fileName = string.IsNullOrWhiteSpace(render.FileName)
                ? Sanitize($"{render.ModName}_{render.NpcFormKey}") + ".png"
                : render.FileName;
            string outputPath = Path.Combine(variantDir, fileName);

            var missingMeshes = new List<string>();
            var missingTextures = new List<string>();
            bool ok = await generator.GenerateAsync(
                npcFormKey, modSetting, outputPath,
                missingMeshPathsOut: missingMeshes,
                missingTexturePathsOut: missingTextures);

            log.AppendLine($"{label}: {(ok ? "OK" : "FAIL")} -> {outputPath}" +
                           (missingMeshes.Count > 0 ? $" | missing meshes: {missingMeshes.Count}" : "") +
                           (missingTextures.Count > 0 ? $" | missing textures: {missingTextures.Count}" : ""));
        }
        catch (Exception ex)
        {
            log.AppendLine($"{label}: FAIL — {ex.Message}");
        }
    }

    /// <summary>Sets InternalMugshotSettings properties by (case-insensitive)
    /// name from the variant's override map. Unknown names and conversion
    /// failures are logged and skipped, never fatal.</summary>
    private static void ApplyOverrides(
        InternalMugshotSettings target,
        Dictionary<string, JToken> overrides,
        StringBuilder log,
        string variantName)
    {
        foreach (var (name, token) in overrides)
        {
            var prop = typeof(InternalMugshotSettings).GetProperty(
                name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null || !prop.CanWrite)
            {
                log.AppendLine($"[{variantName}] override '{name}': no such writable InternalMugshotSettings property — skipped");
                continue;
            }

            try
            {
                prop.SetValue(target, token.ToObject(prop.PropertyType));
            }
            catch (Exception ex)
            {
                log.AppendLine($"[{variantName}] override '{name}': could not convert '{token}' to {prop.PropertyType.Name} — skipped ({ex.Message})");
            }
        }
    }

    /// <summary>Runs the Mod Issues scan on the named mods (cache ignored) and
    /// writes the issues each one reports to ModIssueScan.json, for comparing
    /// scans across BSA serving modes.</summary>
    private static async Task RunModIssueScanAsync(Settings settings, ModIssueScanner scanner,
        SkyRender.GameAssetResolver assetResolver, List<string> modNames, string outDir, StringBuilder log)
    {
        try
        {
            var targets = settings.ModSettings
                .Where(m => modNames.Contains(m.DisplayName, StringComparer.OrdinalIgnoreCase))
                .Where(ModIssueScanner.IsEligible)
                .Select(m => new ModIssueScanner.ModScanTarget(m, null))
                .ToList();
            var statsBefore = assetResolver.GetBsaAssetStats();
            var sw = Stopwatch.StartNew();
            var results = await scanner.RunAsync(targets, ignoreCache: true,
                new Progress<ModIssueScanner.ProgressInfo>(_ => { }), System.Threading.CancellationToken.None);
            var statsAfter = assetResolver.GetBsaAssetStats();
            log.AppendLine($"ModIssueScan: mods={targets.Count}/{modNames.Count} wallMs={sw.ElapsedMilliseconds} " +
                           string.Join(" ", results.Select(r => $"[{r.Key}: npcs={r.Value.ScannedNpcCount} failed={r.Value.FailedNpcCount} issues={r.Value.Issues.Count}]")) +
                           $" materialized+={statsAfter.Materialized - statsBefore.Materialized} " +
                           $"failures+={statsAfter.MaterializeFailures - statsBefore.MaterializeFailures}");
            var dump = results.ToDictionary(r => r.Key, r => r.Value.Issues);
            File.WriteAllText(Path.Combine(outDir, "ModIssueScan.json"),
                JsonConvert.SerializeObject(dump, Formatting.Indented));
        }
        catch (Exception ex)
        {
            log.AppendLine($"ModIssueScan FATAL: {ex}");
        }
    }

    private static (int Files, long Bytes) CountFiles(string dir)
    {
        if (!Directory.Exists(dir)) return (0, 0);
        int files = 0;
        long bytes = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            files++;
            bytes += f.Length;
        }
        return (files, bytes);
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}
