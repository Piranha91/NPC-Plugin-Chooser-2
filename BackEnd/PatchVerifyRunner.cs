using System.IO;
using System.Text;
using Autofac;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using Newtonsoft.Json;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Developer verification harness: drop <c>PatchVerify.json</c> next to the exe, launch the app
/// THROUGH MO2 (so it sees the real virtual file system, not the raw Steam load order), and it
/// runs a full patch against the user's live settings, then writes everything needed to check the
/// result in game — a <see cref="FaceGenLadderDiag"/> CSV, console spawn bats, and an HTML
/// manifest pairing each spawned NPC with the mugshot the app showed when the mod was chosen.
///
/// <para>Why in-game rather than automated: the ladder's SkyPatcher branches only exist at
/// runtime (SkyPatcher patches base records as the game loads), and the dark-face failure it
/// guards against is a rendering outcome. There is no assertion to make from disk. So the harness
/// optimises for making a HUMAN check cheap and unambiguous: it picks the NPCs worth looking at,
/// spawns them next to each other, and states what each one should look like and why.</para>
///
/// <para>The interesting ladder rows are rare — a full run produced roughly fifty row-2 cases and
/// a single row-3 — so the sampler takes ALL of the rare rows and only samples the common one.</para>
///
/// <para>Follows the same trigger-file shape as <see cref="CharacterViewerHost.AuditScanRunner"/>
/// and <see cref="CharacterViewerHost.RenderHarnessRunner"/>.</para>
/// </summary>
public static class PatchVerifyRunner
{
    public const string TriggerFileName = "PatchVerify.json";

    public static string TriggerPath => Path.Combine(AppContext.BaseDirectory, TriggerFileName);

    public static bool ConfigExists => File.Exists(TriggerPath);

    public class PatchVerifyConfig
    {
        /// <summary>Absolute path of the mod folder to patch into. Deliberately separate from the
        /// user's real output mod so a verification run can never clobber it.</summary>
        public string OutputDirectory { get; set; } = "";

        public bool ExitWhenDone { get; set; } = true;

        /// <summary>"Record", "SkyPatcher", or empty to use whatever the settings already say.</summary>
        public string ModeOverride { get; set; } = "";

        /// <summary>"Inherit" (keep templated NPCs on their template's face) or "OwnCopy"
        /// (flatten resolved template chains onto each NPC's own record); empty uses whatever
        /// the settings already say. The enum names ("InheritFromTemplate" /
        /// "GiveEachNpcOwnCopy") are accepted too. Sets the GLOBAL setting only — a per-mod
        /// override (ModSetting.ModTemplateHandlingMode) still wins for that mod's NPCs.</summary>
        public string TemplateHandlingOverride { get; set; } = "";

        /// <summary>Per-row spawn budget keyed "Row1".."Row5". Missing or 0 means take every one,
        /// which is the right default for the rare rows.</summary>
        public Dictionary<string, int> SamplePerRow { get; set; } = new();

        public bool IncludeAborts { get; set; } = true;

        /// <summary>NPCs per bat file, so spawning does not produce one indistinguishable pile.</summary>
        public int SpawnChunkSize { get; set; } = 6;

        /// <summary>Explicit target FormKeys ("013BA5:Skyrim.esm"). Non-empty overrides sampling.</summary>
        public List<string> NpcFilter { get; set; } = new();

        /// <summary>Render a mugshot for any sampled NPC that has none, so the manifest always has
        /// a reference image to compare the in-game face against. Written to the normal autogen
        /// cache, so they also show up in the app afterwards.</summary>
        public bool GenerateMissingMugshots { get; set; } = true;

        /// <summary>Ceiling on renders, since a large sample can mean hundreds. Anything beyond it
        /// is reported in the log rather than silently dropped.</summary>
        public int MaxMugshotRenders { get; set; } = 300;

        /// <summary>
        /// Rewrite the output plugin's Name on each <see cref="NpcFilter"/> NPC so a pile of
        /// spawned specimens can be told apart on sight. Several specimens in a template group
        /// legitimately share a display name ("Imperial Soldier"), which makes a screenshot
        /// unattributable. Off unless a filter is set, since it only makes sense for a pinned set.
        /// </summary>
        public bool RenameSpecimensInOutput { get; set; } = true;
    }

    public static async Task<bool> RunAsync(IComponentContext container)
    {
        PatchVerifyConfig config;
        try
        {
            config = JsonConvert.DeserializeObject<PatchVerifyConfig>(File.ReadAllText(TriggerPath))
                     ?? new PatchVerifyConfig();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "PatchVerify.log"),
                $"FATAL: could not parse {TriggerFileName}: {ex.Message}");
            return true;
        }

        var log = new StringBuilder();
        log.AppendLine($"PatchVerify started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var settings = container.Resolve<Settings>();
        var patcher = container.Resolve<Patcher>();
        var validator = container.Resolve<Validator>();
        var env = container.Resolve<EnvironmentStateProvider>();

        string outDir = string.IsNullOrWhiteSpace(config.OutputDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "PatchVerifyOutput")
            : config.OutputDirectory;

        // The patch run reads these off the live Settings object, which is also what gets persisted
        // on exit — so every override is restored in the finally below. A verification run must not
        // silently repoint the user's real output folder or flip their patching mode.
        string origOutputDir = settings.OutputDirectory;
        bool origAppendTimestamp = settings.AppendTimestampToOutputDirectory;
        bool origSkyPatcher = settings.UseSkyPatcherMode;
        var origTemplateHandling = settings.TemplateHandlingMode;

        try
        {
            settings.OutputDirectory = outDir;
            settings.AppendTimestampToOutputDirectory = false; // keep the path predictable for the manifest
            if (string.Equals(config.ModeOverride, "SkyPatcher", StringComparison.OrdinalIgnoreCase))
                settings.UseSkyPatcherMode = true;
            else if (string.Equals(config.ModeOverride, "Record", StringComparison.OrdinalIgnoreCase))
                settings.UseSkyPatcherMode = false;

            if (string.Equals(config.TemplateHandlingOverride, "OwnCopy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(config.TemplateHandlingOverride, nameof(TemplateHandlingMode.GiveEachNpcOwnCopy), StringComparison.OrdinalIgnoreCase))
                settings.TemplateHandlingMode = TemplateHandlingMode.GiveEachNpcOwnCopy;
            else if (string.Equals(config.TemplateHandlingOverride, "Inherit", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(config.TemplateHandlingOverride, nameof(TemplateHandlingMode.InheritFromTemplate), StringComparison.OrdinalIgnoreCase))
                settings.TemplateHandlingMode = TemplateHandlingMode.InheritFromTemplate;
            else if (!string.IsNullOrWhiteSpace(config.TemplateHandlingOverride))
                log.AppendLine($"WARNING: unrecognized TemplateHandlingOverride '{config.TemplateHandlingOverride}' — using the settings value.");

            log.AppendLine($"Output      : {outDir}");
            log.AppendLine($"Mode        : {(settings.UseSkyPatcherMode ? "SkyPatcher" : "Record")}");
            log.AppendLine($"Templates   : {settings.TemplateHandlingMode}");
            log.AppendLine($"Environment : {env.Status}");

            FaceGenLadderDiag.SetEnabled(true);
            FaceGenLadderDiag.Reset();

            // Route the patcher's log into ours; without this the harness runs blind, since the
            // UI logger is normally wired up by VM_Run, which never constructs here.
            patcher.ConnectToUILogger(
                (msg, isError, _) => log.AppendLine((isError ? "ERROR: " : "") + msg),
                null, null, null);

            await RunPatchAsync(patcher, validator, env, log);

            var decisions = FaceGenLadderDiag.Decisions;
            log.AppendLine($"Ladder decisions captured: {decisions.Count}");

            string reportDir = Path.Combine(outDir, "_PatchVerify");
            Directory.CreateDirectory(reportDir);

            string? csv = FaceGenLadderDiag.Flush(reportDir);
            log.AppendLine($"CSV: {csv ?? "(not written)"}");

            var sample = Sample(decisions, config, log);
            log.AppendLine($"Sampled for in-game spawn: {sample.Count}");

            if (config.RenameSpecimensInOutput && config.NpcFilter is { Count: > 0 })
            {
                RenameSpecimensInOutputPlugin(env, outDir, sample, log);
            }

            if (config.GenerateMissingMugshots)
            {
                await GenerateMissingMugshotsAsync(container, settings, sample, config, log);
            }

            var spawns = BuildSpawnEntries(sample, env, log);
            WriteSpawnBats(spawns, outDir, config.SpawnChunkSize, log);
            WriteManifest(spawns, settings, reportDir, outDir, settings.UseSkyPatcherMode, log);
        }
        catch (Exception ex)
        {
            log.AppendLine("FATAL: " + ExceptionLogger.GetExceptionStack(ex));
        }
        finally
        {
            settings.OutputDirectory = origOutputDir;
            settings.AppendTimestampToOutputDirectory = origAppendTimestamp;
            settings.UseSkyPatcherMode = origSkyPatcher;
            settings.TemplateHandlingMode = origTemplateHandling;

            try { File.WriteAllText(Path.Combine(outDir, "_PatchVerify", "PatchVerify.log"), log.ToString()); }
            catch { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "PatchVerify.log"), log.ToString()); }
        }

        return config.ExitWhenDone;
    }

    /// <summary>
    /// Mirrors VM_Run's patch sequence with the UI removed. The one deliberate divergence: VM_Run
    /// stops on invalid selections to ask the user whether to continue, which cannot be answered
    /// headlessly — so those are recorded to the log and the run proceeds with the valid ones.
    /// Output splitting is also skipped, because one plugin keeps FormIDs stable for the bats.
    /// </summary>
    private static async Task RunPatchAsync(Patcher patcher, Validator validator,
        EnvironmentStateProvider env, StringBuilder log)
    {
        var modSettingsMap = patcher.BuildModSettingsMap();
        await patcher.PreInitializationLogicAsync();

        var report = await validator.ScreenSelectionsAsync(modSettingsMap, "<All NPCs>", CancellationToken.None);
        var invalid = report?.InvalidSelections;
        if (invalid is { Count: > 0 })
        {
            log.AppendLine($"Skipping {invalid.Count} invalid selection(s):");
            foreach (var line in invalid) log.AppendLine("  " + line);
        }

        // Same hand-over VM_Run does, so the headless harness writes the same token ledger.
        patcher.RecordScreenedOutNpcs(validator.GetRejectedSelections());

        var validSelections = validator.GetScreeningCache()
            .Where(kv => kv.Value.SelectionIsValid)
            .ToList();
        log.AppendLine($"Patching {validSelections.Count} NPC(s).");

        env.OutputMod = new SkyrimMod(
            ModKey.FromName(env.OutputPluginName, ModType.Plugin), env.SkyrimVersion);

        await patcher.RunPatchingLogic(validSelections, false, true, CancellationToken.None);
        patcher.WriteUnifiedTokenFile();
    }

    // ----------------------------------------------------------------------------------------
    // Sampling
    // ----------------------------------------------------------------------------------------

    private static List<FaceGenLadderDecision> Sample(
        IReadOnlyList<FaceGenLadderDecision> decisions, PatchVerifyConfig config, StringBuilder log)
    {
        if (config.NpcFilter is { Count: > 0 })
        {
            var wanted = config.NpcFilter
                .Select(s => { try { return FormKey.Factory(s); } catch { return FormKey.Null; } })
                .Where(fk => fk != FormKey.Null)
                .ToHashSet();
            log.AppendLine($"NpcFilter active ({wanted.Count} key(s)); sampling bypassed.");
            return decisions.Where(d => wanted.Contains(d.Inputs.TargetFormKey)).ToList();
        }

        var picked = new List<FaceGenLadderDecision>();

        if (config.IncludeAborts)
        {
            var aborts = decisions.Where(d => d.Abort).ToList();
            picked.AddRange(aborts);
            log.AppendLine($"  aborts: {aborts.Count} (all included)");
        }

        foreach (var row in Enum.GetValues<FaceGenLadderRow>())
        {
            var inRow = decisions.Where(d => !d.Abort && d.Row == row).ToList();
            if (inRow.Count == 0) continue;

            int budget = config.SamplePerRow.TryGetValue($"Row{(int)row}", out var n) && n > 0
                ? n
                : inRow.Count;

            // Prefer distinct mods so a sample of six is six different mods' behaviour rather than
            // six NPCs from whichever mod happens to sort first.
            var take = inRow
                .GroupBy(d => d.Inputs.ModName, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => g.Select((d, idx) => (d, idx)))
                .OrderBy(x => x.idx)
                .Select(x => x.d)
                .Take(budget)
                .ToList();

            picked.AddRange(take);
            log.AppendLine($"  Row{(int)row}: {inRow.Count} found, {take.Count} sampled" +
                           (take.Count < inRow.Count ? $" (capped at {budget})" : ""));
        }

        return picked;
    }

    // ----------------------------------------------------------------------------------------
    // Specimen labelling
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Renames the pinned specimens in the written output plugin so they are identifiable on sight
    /// when spawned together. A template group routinely contains several NPCs sharing one display
    /// name — "Imperial Soldier" three times over — and a screenshot of an unidentifiable face
    /// proves nothing. The new name carries the spawn ordinal and the mod that was selected, which
    /// is the fact the comparison is actually about.
    ///
    /// <para>Runs as a post-pass over the finished plugin rather than inside the patcher: this is a
    /// verification affordance, not patch behaviour, and it must not alter what the patcher
    /// produces for anyone else. Only the Name (FULL) field is touched.</para>
    /// </summary>
    private static void RenameSpecimensInOutputPlugin(
        EnvironmentStateProvider env, string outDir, List<FaceGenLadderDecision> sample, StringBuilder log)
    {
        string pluginPath = Path.Combine(outDir, env.OutputPluginFileName);
        if (!File.Exists(pluginPath))
        {
            log.AppendLine($"Rename: output plugin not found at {pluginPath}; skipped.");
            return;
        }

        // Spawn order is the order the bats are written, so the ordinal here matches what the
        // manifest and the console will show.
        var labels = new Dictionary<FormKey, string>();
        for (int i = 0; i < sample.Count; i++)
        {
            var inputs = sample[i].Inputs;
            string mod = inputs.ModName.Length > 26 ? inputs.ModName[..26].TrimEnd() + "…" : inputs.ModName;
            string role = inputs.ChainStatus == FaceGenChainStatus.Resolved ? "follows" : "own";
            labels[inputs.TargetFormKey] = $"[{i + 1}] {role} «{mod}»";
        }

        try
        {
            var mod = SkyrimMod.CreateFromBinary(pluginPath, env.SkyrimVersion);
            int renamed = 0;

            foreach (var npc in mod.Npcs)
            {
                if (!labels.TryGetValue(npc.FormKey, out var label)) continue;
                string original = npc.Name?.String ?? npc.EditorID ?? npc.FormKey.ToString();
                npc.Name = $"{label} {original}";
                log.AppendLine($"  renamed {npc.FormKey} -> \"{npc.Name}\"");
                renamed++;
            }

            if (renamed == 0)
            {
                log.AppendLine("Rename: no specimen records present in the output plugin " +
                               "(were they patched at all?); nothing written.");
                return;
            }

            mod.WriteToBinary(pluginPath, new BinaryWriteParameters
            {
                MastersListContent = MastersListContentOption.Iterate,
            });
            log.AppendLine($"Rename: {renamed} specimen(s) relabelled in {env.OutputPluginFileName}.");
        }
        catch (Exception ex)
        {
            // Labelling is a convenience; a failure here must leave the patch output intact and
            // usable rather than taking the run down.
            log.AppendLine($"Rename: FAILED, output left as patched — {ex.Message}");
        }
    }

    // ----------------------------------------------------------------------------------------
    // Reference mugshots
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Renders a mugshot for every sampled NPC that lacks one, so the manifest can always show
    /// what the chosen mod is SUPPOSED to look like next to what the game actually renders.
    /// Without this the manifest is full of "none rendered" for exactly the NPCs worth checking —
    /// the rare ladder rows are, by their nature, the ones nobody has browsed to in the UI.
    ///
    /// <para>Output goes to the normal autogen cache path rather than somewhere harness-specific,
    /// so the renders are not throwaway: the app picks them up afterwards like any other mugshot.</para>
    ///
    /// <para>Runs strictly after the patch, which is deliberate — that ordering used to break
    /// (the patcher wiped the BSA readers the renderer depends on, latching a not-found result),
    /// so a successful pass here doubles as a live check that the fix holds.</para>
    /// </summary>
    private static async Task GenerateMissingMugshotsAsync(
        IComponentContext container, Settings settings, List<FaceGenLadderDecision> sample,
        PatchVerifyConfig config, StringBuilder log)
    {
        var missing = sample
            .Where(d => FindMugshot(settings, d.Inputs.ModName, d.Inputs.DonorFormKey).Length == 0)
            .ToList();

        if (missing.Count == 0)
        {
            log.AppendLine("Mugshots: all sampled NPCs already have one.");
            return;
        }

        CharacterViewerHost.InternalMugshotGenerator generator;
        try
        {
            generator = container.Resolve<CharacterViewerHost.InternalMugshotGenerator>();
        }
        catch (Exception ex)
        {
            log.AppendLine($"Mugshots: generator unavailable, skipping ({ex.Message}).");
            return;
        }

        int budget = config.MaxMugshotRenders > 0 ? config.MaxMugshotRenders : missing.Count;
        var toRender = missing.Take(budget).ToList();
        if (missing.Count > toRender.Count)
        {
            log.AppendLine($"Mugshots: {missing.Count} missing, rendering {toRender.Count} " +
                           $"(MaxMugshotRenders={config.MaxMugshotRenders}); " +
                           $"{missing.Count - toRender.Count} left without a reference image.");
        }
        else
        {
            log.AppendLine($"Mugshots: rendering {toRender.Count} missing.");
        }

        int ok = 0, failed = 0;
        foreach (var d in toRender)
        {
            var i = d.Inputs;
            try
            {
                var modSetting = settings.ModSettings?.FirstOrDefault(
                    m => string.Equals(m.DisplayName, i.ModName, StringComparison.OrdinalIgnoreCase));
                if (modSetting == null)
                {
                    log.AppendLine($"  {i.NpcIdentifier}: no ModSetting named '{i.ModName}'");
                    failed++;
                    continue;
                }

                string savePath = CharacterViewerHost.BatchMugshotGenerator.GetAutoGenSavePath(
                    settings, i.ModName, i.DonorFormKey);
                Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);

                // targetNpcFormKey matters for shared appearances: the renderer must build the
                // donor's face as it will be applied to the recipient, not the donor in isolation.
                var missingMeshes = new List<string>();
                var missingTextures = new List<string>();
                var faceGenMismatch = new List<string>();

                bool rendered = await generator.GenerateAsync(
                    i.DonorFormKey, modSetting, savePath,
                    missingMeshPathsOut: missingMeshes,
                    missingTexturePathsOut: missingTextures,
                    faceGenMismatchOut: faceGenMismatch,
                    targetNpcFormKey: i.TargetFormKey);

                if (rendered)
                {
                    ok++;
                }
                else
                {
                    // A bare count tells us nothing actionable; name what the renderer could not
                    // find so a failure is diagnosable without re-running with render logs on.
                    failed++;
                    string why = missingMeshes.Count > 0 ? $"missing meshes: {string.Join("; ", missingMeshes.Take(3))}"
                        : missingTextures.Count > 0 ? $"missing textures: {string.Join("; ", missingTextures.Take(3))}"
                        : faceGenMismatch.Count > 0 ? $"FaceGen mismatch: {string.Join("; ", faceGenMismatch.Take(3))}"
                        : "no reason reported";
                    log.AppendLine($"  {i.NpcIdentifier} [{i.ModName}] row{(int)d.Row}: {why}");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"  {i.NpcIdentifier}: render failed — {ex.Message}");
                failed++;
            }
        }

        log.AppendLine($"Mugshots: {ok} rendered, {failed} failed.");
    }

    // ----------------------------------------------------------------------------------------
    // Spawn bats
    // ----------------------------------------------------------------------------------------

    private sealed record SpawnEntry(
        FaceGenLadderDecision Decision,
        string RuntimeFormId,
        bool RuntimeIdApproximate,
        string Group);

    private static List<SpawnEntry> BuildSpawnEntries(
        List<FaceGenLadderDecision> sample, EnvironmentStateProvider env, StringBuilder log)
    {
        var indices = BuildLoadOrderIndices(env, log);
        var entries = new List<SpawnEntry>();

        foreach (var d in sample)
        {
            var fk = d.Inputs.TargetFormKey;
            string group = d.Abort ? "abort" : $"row{(int)d.Row}";

            if (!indices.TryGetValue(fk.ModKey, out var idx))
            {
                log.AppendLine($"  (no load order index for {fk.ModKey} — {d.Inputs.NpcIdentifier} omitted from bats)");
                continue;
            }

            string id = idx.IsLight
                ? $"FE{idx.Index:X3}{fk.ID & 0xFFF:X3}"
                : $"{idx.Index:X2}{fk.ID & 0xFFFFFF:X6}";

            entries.Add(new SpawnEntry(d, id, idx.Approximate, group));
        }

        return entries;
    }

    private readonly record struct LoadOrderIndex(int Index, bool IsLight, bool Approximate);

    /// <summary>
    /// Maps each enabled plugin to the mod index the game will give it, so the bats carry real
    /// spawnable FormIDs instead of a placeholder the user has to substitute by hand.
    ///
    /// <para>Light plugins occupy the shared FE space and do NOT consume a normal index, so the two
    /// counters are tracked separately. Light-ness is read from the loaded mod header where the
    /// environment already has it (an ESL-FLAGGED .esp is invisible to an extension check); where
    /// the mod is not loaded, the extension is the fallback and the row is marked approximate so
    /// the manifest can say so rather than quietly emitting a wrong ID.</para>
    /// </summary>
    private static Dictionary<ModKey, LoadOrderIndex> BuildLoadOrderIndices(
        EnvironmentStateProvider env, StringBuilder log)
    {
        var map = new Dictionary<ModKey, LoadOrderIndex>();
        var listings = env.LoadOrder?.ListedOrder?.Where(l => l.Enabled).ToList();
        if (listings == null)
        {
            log.AppendLine("WARNING: no load order available; spawn bats will be empty.");
            return map;
        }

        int normal = 0, light = 0;
        foreach (var listing in listings)
        {
            bool approximate = false;
            bool isLight;
            try
            {
                if (listing.Mod is { } mod)
                {
                    isLight = mod.IsSmallMaster;
                }
                else
                {
                    isLight = HasEslExtension(listing.ModKey);
                    approximate = true;
                }
            }
            catch
            {
                isLight = HasEslExtension(listing.ModKey);
                approximate = true;
            }

            map[listing.ModKey] = isLight
                ? new LoadOrderIndex(light++, true, approximate)
                : new LoadOrderIndex(normal++, false, approximate);
        }

        log.AppendLine($"Load order indexed: {normal} normal, {light} light.");
        return map;
    }

    /// <summary>Extension-only light check. Correct for real .esl files and wrong for an
    /// ESL-FLAGGED .esp, which is exactly why callers mark this path approximate — it is only
    /// reached when the mod header was not available to ask directly.</summary>
    private static bool HasEslExtension(ModKey modKey) =>
        modKey.ToString().EndsWith(".esl", StringComparison.OrdinalIgnoreCase);

    private static void WriteSpawnBats(
        List<SpawnEntry> entries, string outDir, int chunkSize, StringBuilder log)
    {
        if (chunkSize < 1) chunkSize = 6;
        Directory.CreateDirectory(outDir);

        // Sweep this harness's own bats from previous runs first. The patch run's output clear
        // spares loose files at the mod root, so they accumulate — and a stale bat is worse than a
        // missing one: an earlier abort list still names NPCs that later stopped aborting, so
        // spawning it shows them correctly patched and reads as a failure that isn't one.
        int swept = 0;
        foreach (var old in Directory.EnumerateFiles(outDir, "verify_*.txt"))
        {
            try { File.Delete(old); swept++; }
            catch (Exception ex) { log.AppendLine($"  (could not remove stale bat {Path.GetFileName(old)}: {ex.Message})"); }
        }
        if (swept > 0) log.AppendLine($"Removed {swept} spawn bat(s) from previous runs.");

        foreach (var group in entries.GroupBy(e => e.Group))
        {
            var list = group.ToList();
            for (int chunk = 0; chunk * chunkSize < list.Count; chunk++)
            {
                var slice = list.Skip(chunk * chunkSize).Take(chunkSize).ToList();
                // Pure console commands only: Skyrim's `bat` has no comment syntax, so anything
                // that is not a command would just spam errors. The manifest carries the notes.
                var lines = new List<string> { "tai" };
                lines.AddRange(slice.Select(e => $"player.placeatme {e.RuntimeFormId}"));

                string name = $"verify_{group.Key}_{chunk + 1:00}.txt";
                File.WriteAllLines(Path.Combine(outDir, name), lines);
                log.AppendLine($"  bat {name}: {slice.Count} NPC(s)");
            }
        }
    }

    // ----------------------------------------------------------------------------------------
    // Manifest
    // ----------------------------------------------------------------------------------------

    private static void WriteManifest(List<SpawnEntry> entries, Settings settings,
        string reportDir, string outDir, bool skyPatcherMode, StringBuilder log)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>NPC2 patch verification</title><style>");
        sb.AppendLine("body{font-family:Segoe UI,sans-serif;margin:24px;background:#1e1e1e;color:#ddd}");
        sb.AppendLine("h1,h2{font-weight:600}table{border-collapse:collapse;width:100%;margin-bottom:32px}");
        sb.AppendLine("th,td{border:1px solid #444;padding:8px;text-align:left;vertical-align:top;font-size:14px}");
        sb.AppendLine("th{background:#2d2d2d}img{max-width:200px;border:1px solid #555}");
        sb.AppendLine(".abort{color:#ff8080}.ok{color:#8fd18f}.warn{color:#e0c060}code{color:#9cdcfe}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h1>NPC2 patch verification</h1>");
        sb.AppendLine($"<p>Generated {DateTime.Now:yyyy-MM-dd HH:mm}. Mode: <b>{(skyPatcherMode ? "SkyPatcher" : "Record")}</b>. " +
                      $"Templated NPCs: <b>{settings.TemplateHandlingMode}</b>. " +
                      $"Output mod: <code>{Esc(outDir)}</code></p>");
        sb.AppendLine("<p>Enable the output mod in MO2 (and <b>disable your real NPC output</b> so the two do not " +
                      "both patch), load a save, then run each bat from the console. Click a spawned NPC to see its " +
                      "name. Compare its face to the reference mugshot — that is the image the app showed you when " +
                      "you picked the mod. Run <code>tai</code> again afterwards to restore AI.</p>");

        foreach (var group in entries.GroupBy(e => e.Group).OrderBy(g => g.Key))
        {
            sb.AppendLine($"<h2>{Esc(group.Key)} &mdash; {group.Count()} NPC(s)</h2>");
            sb.AppendLine("<p>" + Esc(GroupExpectation(group.Key)) + "</p>");
            sb.AppendLine("<table><tr><th>#</th><th>NPC</th><th>Chosen mod</th><th>Spawn ID</th>" +
                          "<th>What the ladder decided</th><th>Before this change</th><th>Reference mugshot</th></tr>");

            int n = 1;
            foreach (var e in group)
            {
                var i = e.Decision.Inputs;
                string mug = FindMugshot(settings, i.ModName, i.DonorFormKey);
                string img = mug.Length > 0
                    ? $"<img src=\"file:///{Esc(mug.Replace('\\', '/'))}\" alt=\"mugshot\">"
                    : "<i>none rendered</i>";

                // The warning flags are carried separately from LogLine (the run log reports
                // them per NPC after patching, via NpcWarningReporter), so the manifest has to
                // render them explicitly or a flagged NPC would read as an unqualified success.
                string tint = !e.Decision.MissingTintEverywhere
                    ? string.Empty
                    : "<br><span class=\"warn\">WARNING: no face tint could be found anywhere " +
                      "for this NPC, so its face may look discoloured in game.</span>";
                string originCompat = !e.Decision.OriginMeshFailedCompatCheck
                    ? string.Empty
                    : "<br><span class=\"warn\">WARNING: the forwarded origin mesh failed the " +
                      "head-part compatibility check — another mod appears to have changed the " +
                      "NPC's original head data. Spawn to verify.</span>";
                string modMeshCompat = !e.Decision.ModMeshFailedCompatCheck
                    ? string.Empty
                    : "<br><span class=\"warn\">WARNING: the mod's own face mesh failed the " +
                      "head-part compatibility check against the record that ships. Spawn to " +
                      "verify.</span>";

                string verdict = e.Decision.Abort
                    ? $"<span class=\"abort\">SKIPPED</span><br>{Esc(e.Decision.AbortReason)}"
                    : $"<span class=\"ok\">{Esc(e.Decision.PlannedAction)}</span><br>{Esc(e.Decision.LogLine)}{tint}{originCompat}{modMeshCompat}";

                string idCell = e.RuntimeIdApproximate
                    ? $"<code>{e.RuntimeFormId}</code> <span class=\"warn\">(approx)</span>"
                    : $"<code>{e.RuntimeFormId}</code>";

                sb.AppendLine($"<tr><td>{n++}</td><td>{Esc(i.NpcIdentifier)}<br><small>{Esc(i.TargetFormKey.ToString())}" +
                              (i.ChainStatus == FaceGenChainStatus.Resolved
                                  ? (i.FlattenTemplateChain
                                      ? $"<br>own copy of {Esc(i.SubjectFormKey.ToString())}'s appearance"
                                      : $"<br>inherits face from {Esc(i.SubjectFormKey.ToString())}")
                                  : "") +
                              $"</small></td><td>{Esc(i.ModName)}</td><td>{idCell}</td>" +
                              $"<td>{verdict}</td><td><code>{Esc(e.Decision.LegacyAction)}</code></td><td>{img}</td></tr>");
            }

            sb.AppendLine("</table>");
        }

        sb.AppendLine("</body></html>");

        string path = Path.Combine(reportDir, "VerifyManifest.html");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        log.AppendLine($"Manifest: {path}");
    }

    private static string GroupExpectation(string group) => group switch
    {
        "row1" => "The mod ships both halves of the face. These should match their mugshot exactly — " +
                  "they are the control group, and any mismatch here means something more basic is wrong.",
        "row2" => "The mod ships the face mesh but no face tint. The face shape should match the mugshot; " +
                  "skin tone may differ, because the tint came from elsewhere. The CSV names the source.",
        "row3" => "The mod ships a face tint but no mesh. Expect the mod's colouring on the ORIGINAL mod's " +
                  "head shape — so a close but not identical match to the mugshot is correct here. Where the " +
                  "CSV says the mesh was left in place, the face comes from whichever mod it names, not the " +
                  "one you picked.",
        "row4" => "The mod ships assets but no record for these NPCs, so both the record and the face come " +
                  "from the mod that originally added them. They should look like that mod's version.",
        "row5" => "The mod ships neither half at the subject's paths; everything falls back to the mod of " +
                  "origin. These should look unchanged from a vanilla-ish baseline.",
        "abort" => "These were deliberately NOT patched, because their face could not be assembled safely. " +
                   "They should look like whatever your load order already gave them — NOT like the mod you " +
                   "picked. If one of these DOES look like your chosen mod, the abort did not take effect.",
        _ => "",
    };

    /// <summary>
    /// Locates the mugshot the app would show for this NPC under this mod, so the manifest can put
    /// it beside the in-game face. Searches the mod's registered folders plus the autogen and
    /// FaceFinder caches, all of which use the same &lt;mod&gt;\&lt;plugin&gt;\&lt;FormID&gt;.png layout.
    /// </summary>
    private static string FindMugshot(Settings settings, string modName, FormKey donorFormKey)
    {
        var roots = new List<string>();

        var modSetting = settings.ModSettings?.FirstOrDefault(
            ms => string.Equals(ms.DisplayName, modName, StringComparison.OrdinalIgnoreCase));
        if (modSetting?.MugShotFolderPaths != null) roots.AddRange(modSetting.MugShotFolderPaths);

        try
        {
            roots.Add(Path.Combine(Settings.GetEffectiveAutogenMugshotsFolder(settings), modName));
            roots.Add(Path.Combine(Settings.GetEffectiveFaceFinderMugshotsFolder(settings), modName));
        }
        catch
        {
            // A missing cache root just means fewer places to look.
        }

        string leaf = $"{donorFormKey.ID:X8}";
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
            {
                try
                {
                    string candidate = Path.Combine(root, donorFormKey.ModKey.ToString(), leaf + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // Malformed path — try the next candidate.
                }
            }
        }

        return string.Empty;
    }

    private static string Esc(string? s) => string.IsNullOrEmpty(s)
        ? string.Empty
        : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
