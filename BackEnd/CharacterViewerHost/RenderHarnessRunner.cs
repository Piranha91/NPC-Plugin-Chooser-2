using System;
using System.Collections.Generic;
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
    public static async Task<bool> RunAsync(Settings settings, InternalMugshotGenerator generator)
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

        try
        {
            Directory.CreateDirectory(outDir);

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

                    foreach (var render in config.Renders)
                    {
                        await RunOneAsync(settings, generator, render, variantName, variantDir, log);
                    }
                }
            }
            finally
            {
                JsonConvert.PopulateObject(snapshot, settings.InternalMugshot);
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"FATAL: {ex}");
        }

        try { File.WriteAllText(Path.Combine(outDir, "RenderHarness.log"), log.ToString()); }
        catch { /* best-effort */ }

        return config.ExitWhenDone;
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

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}
