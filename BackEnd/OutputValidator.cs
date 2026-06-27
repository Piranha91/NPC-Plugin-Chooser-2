using System.IO;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Validates that this app's generated output actually takes effect in the user's
/// real, deployed load order. For each selected NPC it checks three things:
///   1. Record:     does the conflict-winning NPC record's appearance match the chosen mod?
///   2. Asset:      does the deployed FaceGen (esp. the .nif) match the chosen mod's FaceGen?
///   3. SkyPatcher: does any SkyPatcher .ini set this NPC's visual style (and, in SkyPatcher
///                  mode, does a higher-priority .ini override this app)?
///
/// Validation runs against an UNTRIMMED environment (see
/// <see cref="EnvironmentStateProvider.TryBuildUntrimmedEnvironment"/>) so this app's own
/// deployed output is visible — the normal environment trims it out. Per the user's choice,
/// validation requires the output to be deployed and active first; if it isn't, the run is
/// blocked with an explanation rather than producing a misleading report.
/// </summary>
public class OutputValidator
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Settings _settings;
    private readonly RecordHandler _recordHandler;
    private readonly BsaHandler _bsaHandler;
    private readonly FaceGenConsistencyAnalyzer _faceGenConsistency;

    private const float FloatEpsilon = 0.0001f;

    // Action directives (lowercased) that change an NPC's visual appearance. Used to decide
    // whether a SkyPatcher config line is relevant to appearance validation.
    private static readonly HashSet<string> VisualActionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "copyvisualstyle", "setrandomvisualstyle", "skin", "race", "height", "weight",
        "headparts", "headpart", "haircolor", "haircolour", "hair", "headtexture",
        "facetextureset", "tintlayers", "facemorph"
    };

    public OutputValidator(EnvironmentStateProvider environmentStateProvider, Settings settings, RecordHandler recordHandler, BsaHandler bsaHandler, FaceGenConsistencyAnalyzer faceGenConsistency)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
        _recordHandler = recordHandler;
        _bsaHandler = bsaHandler;
        _faceGenConsistency = faceGenConsistency;
    }

    /// <summary>
    /// Runs validation for the supplied NPCs (FormKeys that must each have an appearance
    /// selection). Heavy work (building the load order, hashing FaceGen) — call from a
    /// background thread. Progress is reported as (current, total, message).
    /// </summary>
    public ValidationRunResult Validate(
        IReadOnlyList<FormKey> npcsToValidate,
        IProgress<(int current, int total, string message)>? progress,
        CancellationToken ct)
    {
        var result = new ValidationRunResult();
        var log = new StringBuilder();
        log.AppendLine("=== Validate Output ===");
        log.AppendLine($"Mode: {(_settings.UseSkyPatcherMode ? "SkyPatcher" : _settings.PatchingMode.ToString())}");
        log.AppendLine($"NPCs requested: {npcsToValidate.Count}");

        // Opt-in performance breakdown: phase timings go to the validation log, and a
        // hierarchical per-check report (aggregated across NPCs) is appended at the end.
        ContextualPerformanceTracer.Reset();
        using var _perfCtx = ContextualPerformanceTracer.BeginContext("OutputValidator");
        var swPhase = System.Diagnostics.Stopwatch.StartNew();

        if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
        {
            result.Blocked = true;
            result.BlockReason = "The game environment is not valid. Resolve it on the Settings page (a valid load order and data folder are required) and try again.";
            WriteLog(log, result);
            return result;
        }

        progress?.Report((0, 0, "Building untrimmed load order..."));
        log.AppendLine("Building untrimmed environment...");
        using var env = _environmentStateProvider.TryBuildUntrimmedEnvironment(out var envError);
        if (env == null)
        {
            result.Blocked = true;
            result.BlockReason = "Could not build a load order to validate against:\n" + envError;
            WriteLog(log, result);
            return result;
        }

        var linkCache = env.LinkCache;
        var listings = env.LoadOrder.ListedOrder.ToList();
        var dataFolder = env.DataFolderPath.Path;
        log.AppendLine($"[perf] Untrimmed environment built in {swPhase.ElapsedMilliseconds} ms ({listings.Count} plugins).");
        swPhase.Restart();

        // --- Deploy gate (user chose "require deploy first") ---
        // skyPatcherNpcRoot / npc2IniPath are also reused below for the SkyPatcher index + .ini parse.
        string outputModName = Path.GetFileNameWithoutExtension(_environmentStateProvider.OutputPluginName ?? EnvironmentStateProvider.DefaultPluginName);
        string skyPatcherNpcRoot = Path.Combine(dataFolder, "SKSE", "Plugins", "SkyPatcher", "npc");
        string npc2IniPath = Path.Combine(skyPatcherNpcRoot, "NPC Plugin Chooser", outputModName + ".ini");

        var gateBlock = EvaluateDeployGate(listings, npc2IniPath, log);
        if (gateBlock != null)
        {
            result.Blocked = true;
            result.BlockReason = gateBlock;
            WriteLog(log, result);
            return result;
        }

        // --- SkyPatcher index (parse all npc configs once) ---
        progress?.Report((0, 0, "Scanning SkyPatcher configs..."));
        var skyIndex = BuildSkyPatcherIndex(skyPatcherNpcRoot, npc2IniPath, log);
        log.AppendLine($"[perf] SkyPatcher index built in {swPhase.ElapsedMilliseconds} ms.");
        swPhase.Restart();
        if (skyIndex.UnevaluableBroadFilterLineCount > 0)
        {
            result.Notes.Add(
                $"{skyIndex.UnevaluableBroadFilterLineCount} SkyPatcher config line(s) use broad filters " +
                "this tool cannot evaluate per-NPC (e.g. by level/class spell/actor value, or an unrecognized " +
                "filter). Broad filters by race/faction/keyword/mod/gender/class/combat-style/voice ARE checked " +
                "against each validated NPC; only these residual lines are not. If an NPC's appearance is wrong " +
                "despite a clean report, review them manually.");
        }

        // In SkyPatcher mode, parse this app's own .ini once to map each recipient NPC to its
        // surrogate template (for the .ini-line and surrogate record/FaceGen checks).
        var npc2IniMap = _settings.UseSkyPatcherMode ? ParseNpc2SkyPatcherIni(npc2IniPath) : null;

        // --- Per-NPC checks ---
        var modSettingsByName = _settings.ModSettings
            .GroupBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int total = npcsToValidate.Count;
        var run = new RunContext
        {
            Release = _settings.SkyrimRelease.ToGameRelease(),
            TempDir = CreateValidationTempDir()
        };
        try
        {
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var npcFk = npcsToValidate[i];

                if (i % 10 == 0 || i == total - 1)
                {
                    progress?.Report((i + 1, total, $"Validating {i + 1}/{total}..."));
                }

                try
                {
                    using (ContextualPerformanceTracer.Trace("ValidateNpc"))
                        ValidateNpc(npcFk, linkCache, listings, modSettingsByName, skyIndex, npc2IniMap, dataFolder, run, result, log);
                }
                catch (Exception ex)
                {
                    result.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Check = ValidationCheckKind.Environment,
                        NpcFormKey = npcFk.ToString(),
                        Issue = "Validation threw an exception for this NPC and was skipped.",
                        Details = ex.Message
                    });
                    log.AppendLine($"  EXCEPTION for {npcFk}: {ex}");
                }
            }
        }
        finally
        {
            CleanupRun(run, log);
        }

        result.NpcsChecked = total;
        progress?.Report((total, total, "Validation complete."));
        log.AppendLine($"Done. NPCs checked: {total}. Issues: {result.Issues.Count}.");
        log.AppendLine($"[perf] Per-NPC validation phase: {swPhase.ElapsedMilliseconds} ms for {total} NPC(s).");
        log.AppendLine(ContextualPerformanceTracer.GenerateDetailedReport("Validate Output"));
        WriteLog(log, result);
        return result;
    }

    /// <summary>Outcome of the up-front deploy-readiness probe. <see cref="Ok"/> is true
    /// when validation can proceed; otherwise <see cref="BlockReason"/> explains why.</summary>
    public sealed record DeployReadiness(bool Ok, string? BlockReason);

    /// <summary>
    /// Cheaply answers "is this app's output actually installed and active right now?"
    /// without iterating any NPCs. Lets the UI fail fast — surfacing the block reason the
    /// instant the user clicks Validate Output, rather than after they pick NPCs. Builds
    /// the same untrimmed load order <see cref="Validate"/> uses (the normal environment
    /// trims this app's output out), but resolves nothing, so it stays light.
    /// </summary>
    public DeployReadiness CheckDeployReadiness()
    {
        if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
            return new DeployReadiness(false,
                "The game environment is not valid. Resolve it on the Settings page (a valid load order and data folder are required) and try again.");

        using var env = _environmentStateProvider.TryBuildUntrimmedEnvironment(out var envError);
        if (env == null)
            return new DeployReadiness(false, "Could not build a load order to validate against:\n" + envError);

        var listings = env.LoadOrder.ListedOrder.ToList();
        var dataFolder = env.DataFolderPath.Path;
        string outputModName = Path.GetFileNameWithoutExtension(_environmentStateProvider.OutputPluginName ?? EnvironmentStateProvider.DefaultPluginName);
        string npc2IniPath = Path.Combine(dataFolder, "SKSE", "Plugins", "SkyPatcher", "npc", "NPC Plugin Chooser", outputModName + ".ini");

        var block = EvaluateDeployGate(listings, npc2IniPath, log: null);
        return new DeployReadiness(block == null, block);
    }

    /// <summary>
    /// The deploy gate: is this app's output installed and active in the real load order
    /// (and, in SkyPatcher mode, is its .ini deployed)? Returns null when ready, else a
    /// human-readable block reason. Shared by <see cref="Validate"/> and
    /// <see cref="CheckDeployReadiness"/> so both apply identical rules.
    /// </summary>
    private string? EvaluateDeployGate(
        IReadOnlyList<IModListingGetter<ISkyrimModGetter>> listings,
        string npc2IniPath,
        StringBuilder? log)
    {
        string outputPluginFileName = _environmentStateProvider.OutputPluginFileName;
        bool outputPluginActive = listings.Any(l =>
        {
            var desc = l.Mod?.ModHeader.Description;
            if (desc != null && desc.Equals(Patcher.PluginDescriptionSignature, StringComparison.Ordinal)) return true;
            return l.ModKey.FileName.String.Equals(outputPluginFileName, StringComparison.OrdinalIgnoreCase);
        });
        log?.AppendLine($"Output plugin '{outputPluginFileName}' active in load order: {outputPluginActive}");

        if (!outputPluginActive)
        {
            return $"This app's output plugin ('{outputPluginFileName}') is not active in your current load order.\n\n" +
                   "Validation checks the real, deployed game state, so the output must be installed and enabled in your " +
                   "mod manager first. Deploy the generated output (and sort/activate the plugin), then re-run Validate Output.";
        }

        if (_settings.UseSkyPatcherMode && !File.Exists(npc2IniPath))
        {
            return "SkyPatcher mode is selected, but this app's SkyPatcher .ini was not found in the deployed Data folder at:\n" +
                   npc2IniPath + "\n\n" +
                   "Install/activate the generated SkyPatcher output, then re-run Validate Output.";
        }
        return null;
    }

    private void ValidateNpc(
        FormKey npcFk,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        List<IModListingGetter<ISkyrimModGetter>> listings,
        Dictionary<string, ModSetting> modSettingsByName,
        SkyPatcherIndex skyIndex,
        Dictionary<string, Npc2SkyPatcherLine>? npc2IniMap,
        string dataFolder,
        RunContext run,
        ValidationRunResult result,
        StringBuilder log)
    {
        if (!_settings.SelectedAppearanceMods.TryGetValue(npcFk, out var selection))
        {
            return; // Caller only passes NPCs with selections; defensive.
        }

        string selectedModName = selection.ModName;
        FormKey donorFk = selection.NpcFormKey;

        // Resolve the recipient's conflict winner (winner-first) — for display, its EditorID, and the
        // record-mode comparison.
        var winningCtx = linkCache.ResolveAllContexts<INpc, INpcGetter>(npcFk).FirstOrDefault();
        INpcGetter? winningRecord = winningCtx?.Record;
        ModKey winningModKey = winningCtx?.ModKey ?? npcFk.ModKey;

        string displayName = winningRecord != null
            ? Auxilliary.GetLogString(winningRecord, _settings.LocalizationLanguage)
            : npcFk.ToString();

        log.AppendLine($"NPC {displayName} [{npcFk}] -> '{selectedModName}' (donor {donorFk}, winner {winningModKey.FileName})");

        if (!modSettingsByName.TryGetValue(selectedModName, out var modSetting))
        {
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Error,
                Check = ValidationCheckKind.Selection,
                NpcDisplayName = displayName,
                NpcFormKey = npcFk.ToString(),
                SelectedMod = selectedModName,
                Issue = $"The selected mod '{selectedModName}' is no longer among the configured mods.",
            });
            return;
        }

        if (_settings.UseSkyPatcherMode)
        {
            // SkyPatcher mode doesn't patch the recipient's record. It builds a surrogate "_Template"
            // NPC (a copy of the donor) in the output plugin and an .ini line that copies the
            // surrogate's visual style onto the recipient at runtime. So validate the .ini line and
            // the surrogate — not the recipient's record/FaceGen.
            ValidateNpcSkyPatcher(npcFk, donorFk, selectedModName, displayName, winningRecord,
                modSetting, npc2IniMap ?? new(), linkCache, listings, skyIndex, dataFolder, run, result, log);
            return;
        }

        // ---------- Record mode ----------
        // Check 1: the conflict-winning record's appearance should match the selected mod.
        if (winningRecord == null)
        {
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Error,
                Check = ValidationCheckKind.Record,
                NpcDisplayName = displayName,
                NpcFormKey = npcFk.ToString(),
                SelectedMod = selectedModName,
                Issue = "This NPC could not be resolved in the current load order.",
            });
        }
        else
        {
            using (ContextualPerformanceTracer.Trace("CheckRecord"))
                CheckRecord(npcFk, displayName, selectedModName, donorFk, modSetting, winningRecord, winningModKey, listings, linkCache, result, log);
        }

        // Check 2: the recipient's deployed FaceGen should match the selected mod's.
        using (ContextualPerformanceTracer.Trace("CheckFaceGen"))
            CheckFaceGen(npcFk, npcFk, donorFk, displayName, selectedModName, modSetting, dataFolder, linkCache, run, result, log);

        // Check 3: any SkyPatcher mod that would override this NPC at runtime.
        using (ContextualPerformanceTracer.Trace("CheckSkyPatcher"))
            CheckSkyPatcher(npcFk, displayName, selectedModName, winningRecord, linkCache, skyIndex, result);
    }

    // ----------------------------------------------------------------------------------
    // SkyPatcher-mode per-NPC validation
    // ----------------------------------------------------------------------------------
    private void ValidateNpcSkyPatcher(
        FormKey npcFk,
        FormKey donorFk,
        string selectedModName,
        string displayName,
        INpcGetter? recipientRecord,
        ModSetting modSetting,
        Dictionary<string, Npc2SkyPatcherLine> npc2IniMap,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        List<IModListingGetter<ISkyrimModGetter>> listings,
        SkyPatcherIndex skyIndex,
        string dataFolder,
        RunContext run,
        ValidationRunResult result,
        StringBuilder log)
    {
        // ---- Check A: this app's own .ini must carry the visual-transfer line for this NPC ----
        string targetKey = FormKeyToSkyPatcherKey(npcFk);
        npc2IniMap.TryGetValue(targetKey, out var iniLine);
        if (iniLine == null || !iniLine.HasSurrogate)
        {
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Error,
                Check = ValidationCheckKind.SkyPatcher,
                NpcDisplayName = displayName,
                NpcFormKey = npcFk.ToString(),
                SelectedMod = selectedModName,
                Issue = iniLine == null
                    ? "SkyPatcher mode: this app's output .ini has no line for this NPC, so its appearance is never applied."
                    : "SkyPatcher mode: this app's output .ini line for this NPC has no 'copyVisualStyle' directive, so the visual transfer won't happen.",
                WinningSource = "this app's SkyPatcher .ini",
                Details = iniLine?.RawLine ?? string.Empty,
            });
            // Can't locate the surrogate without it; still report other SkyPatcher overrides.
            CheckSkyPatcher(npcFk, displayName, selectedModName, recipientRecord, linkCache, skyIndex, result);
            return;
        }
        FormKey surrogateFk = iniLine.Surrogate;

        // ---- Check B: the surrogate template's appearance must match the donor ----
        var surrogateCtx = linkCache.ResolveAllContexts<INpc, INpcGetter>(surrogateFk).FirstOrDefault();
        INpcGetter? surrogateRec = surrogateCtx?.Record;
        if (surrogateRec == null)
        {
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Error,
                Check = ValidationCheckKind.Record,
                NpcDisplayName = displayName,
                NpcFormKey = npcFk.ToString(),
                SelectedMod = selectedModName,
                Issue = "SkyPatcher mode: the surrogate template NPC referenced by copyVisualStyle could not be resolved in the load order (the output plugin may not be active, or the template is missing).",
                WinningSource = surrogateFk.ToString(),
                Details = iniLine.RawLine,
            });
        }
        else
        {
            var donorRec = TryResolveSelectedSourceNpc(modSetting, donorFk);
            if (donorRec == null)
            {
                if (!modSetting.IsFaceGenOnlyEntry)
                {
                    result.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Check = ValidationCheckKind.Record,
                        NpcDisplayName = displayName,
                        NpcFormKey = npcFk.ToString(),
                        SelectedMod = selectedModName,
                        Issue = "SkyPatcher mode: could not resolve the selected mod's appearance NPC to compare against the surrogate template.",
                        Details = $"Donor FormKey {donorFk}",
                    });
                }
            }
            else
            {
                var diffs = CompareAppearance(surrogateRec, donorRec, linkCache, modSetting);
                if (diffs.Count > 0)
                {
                    result.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Error,
                        Check = ValidationCheckKind.Record,
                        NpcDisplayName = displayName,
                        NpcFormKey = npcFk.ToString(),
                        SelectedMod = selectedModName,
                        Issue = "SkyPatcher mode: the surrogate template's appearance does not match the selected mod's appearance NPC, so the visual style copied at runtime will be wrong.",
                        WinningSource = DescribeWinner(surrogateCtx!.ModKey, modSetting, listings),
                        Details = "Differing fields: " + string.Join(" | ", diffs),
                    });
                    log.AppendLine($"  SKYPATCHER surrogate mismatch ({string.Join(" | ", diffs)})");
                }
            }
        }

        // ---- Check C: the surrogate's deployed FaceGen must match the donor's FaceGen ----
        CheckFaceGen(npcFk, surrogateFk, donorFk, displayName, selectedModName, modSetting, dataFolder, linkCache, run, result, log);

        // ---- Check 3: other SkyPatcher mods that also set this NPC's visual style ----
        CheckSkyPatcher(npcFk, displayName, selectedModName, recipientRecord, linkCache, skyIndex, result);
    }

    // ----------------------------------------------------------------------------------
    // Check 1: record appearance
    // ----------------------------------------------------------------------------------
    private void CheckRecord(
        FormKey npcFk,
        string displayName,
        string selectedModName,
        FormKey donorFk,
        ModSetting modSetting,
        INpcGetter winningRecord,
        ModKey winningModKey,
        List<IModListingGetter<ISkyrimModGetter>> listings,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        ValidationRunResult result,
        StringBuilder log)
    {
        // Split CheckRecord into its two sub-steps so the perf report shows which one is hot:
        // ResolveSourceNpc (loads the selected mod's plugin from its folder — can force a full
        // NPC-GRUP parse of a huge plugin) vs CompareAppearance (resolves head parts/tints).
        var swResolve = System.Diagnostics.Stopwatch.StartNew();
        INpcGetter? sourceRecord;
        using (ContextualPerformanceTracer.Trace("ResolveSourceNpc"))
            sourceRecord = TryResolveSelectedSourceNpc(modSetting, donorFk);
        long resolveMs = swResolve.ElapsedMilliseconds;
        if (resolveMs > 1000)
            log.AppendLine($"[perf] SLOW ResolveSourceNpc: {displayName} [{npcFk}] -> '{selectedModName}' (donor {donorFk}) took {resolveMs} ms.");

        if (sourceRecord == null)
        {
            if (!modSetting.IsFaceGenOnlyEntry)
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Check = ValidationCheckKind.Record,
                    NpcDisplayName = displayName,
                    NpcFormKey = npcFk.ToString(),
                    SelectedMod = selectedModName,
                    Issue = "Could not resolve the selected mod's source NPC record to compare against.",
                    WinningSource = DescribeWinner(winningModKey, modSetting, listings),
                    Details = $"Donor FormKey {donorFk}",
                });
            }
            // FaceGen-only entries intentionally leave the record vanilla; nothing to compare.
            return;
        }

        var swCompare = System.Diagnostics.Stopwatch.StartNew();
        List<string> diffs;
        using (ContextualPerformanceTracer.Trace("CompareAppearance"))
            diffs = CompareAppearance(winningRecord, sourceRecord, linkCache, modSetting);
        long compareMs = swCompare.ElapsedMilliseconds;
        if (compareMs > 1000)
            log.AppendLine($"[perf] SLOW CompareAppearance: {displayName} [{npcFk}] -> '{selectedModName}' took {compareMs} ms.");

        if (diffs.Count == 0)
        {
            return; // Winning record's appearance matches the chosen mod.
        }

        // CheckRecord only runs in record (non-SkyPatcher) mode; SkyPatcher mode validates the
        // surrogate template instead (see ValidateNpcSkyPatcher).
        string winnerDesc = DescribeWinner(winningModKey, modSetting, listings);
        result.Issues.Add(new ValidationIssue
        {
            Severity = ValidationSeverity.Error,
            Check = ValidationCheckKind.Record,
            NpcDisplayName = displayName,
            NpcFormKey = npcFk.ToString(),
            SelectedMod = selectedModName,
            Issue = "The conflict-winning record's appearance does not match the selected mod.",
            WinningSource = winnerDesc,
            Details = "Differing fields: " + string.Join(" | ", diffs),
        });
        log.AppendLine($"  RECORD mismatch ({string.Join(" | ", diffs)}); winner={winnerDesc}");
    }

    /// <summary>
    /// Resolves the NPC record the selected mod would supply for <paramref name="donorFk"/>,
    /// mirroring the patcher's priority: explicit plugin disambiguation, then the mod's
    /// plugins in reverse (last-wins) order, then the record's origin plugin as a fallback.
    /// </summary>
    private INpcGetter? TryResolveSelectedSourceNpc(ModSetting modSetting, FormKey donorFk)
    {
        // FaceGen-only "mods" (e.g. Base Game/vanilla FaceGen replacers, mugshot-only entries)
        // supply no plugin record, so there is nothing to compare the winning record against —
        // check 1 is N/A for them and the assets (check 2) carry the appearance.
        if (modSetting.IsFaceGenOnlyEntry) return null;

        var donorLink = donorFk.ToLink<INpcGetter>();
        var folders = modSetting.CorrespondingFolderPaths?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (modSetting.NpcPluginDisambiguation != null &&
            modSetting.NpcPluginDisambiguation.TryGetValue(donorFk, out var disambiguatedKey) &&
            _recordHandler.TryGetRecordGetterFromMod(donorLink, disambiguatedKey, folders, RecordHandler.RecordLookupFallBack.None, out var disRec) &&
            disRec is INpcGetter disNpc)
        {
            return disNpc;
        }

        if (modSetting.CorrespondingModKeys != null && modSetting.CorrespondingModKeys.Any() &&
            _recordHandler.TryGetRecordFromMods(donorLink, modSetting.CorrespondingModKeys, folders, RecordHandler.RecordLookupFallBack.None, out var modRec, reverseOrder: true) &&
            modRec is INpcGetter modNpc)
        {
            return modNpc;
        }

        // Unmatched donor in a real mod: fall back to the origin record so we still compare something.
        if (_recordHandler.TryGetRecordGetterFromMod(donorLink, donorFk.ModKey, folders, RecordHandler.RecordLookupFallBack.None, out var originRec) &&
            originRec is INpcGetter originNpc)
        {
            return originNpc;
        }

        return null;
    }

    /// <summary>
    /// Returns the appearance fields that differ between two NPC records. FormLink fields are compared
    /// by the EditorID of the resolved record, NOT by FormKey: this app preserves EditorIDs when it
    /// remaps/duplicates dependency records into the output (which happens in both record and SkyPatcher
    /// mode), and the in-game dark-face bug is itself keyed on HeadPart EditorIDs matching the FaceGen
    /// NIF node names. HeadParts are compared as an unordered set of EditorIDs (order is not significant).
    /// </summary>
    private List<string> CompareAppearance(INpcGetter a, INpcGetter b, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, ModSetting sourceMod)
    {
        // a = actual (this app's output / surrogate); b = expected (selected mod / donor).
        var src = new SourceModRefs(sourceMod);
        var diffs = new List<string>();

        void CheckLink<TGetter>(string name, IFormLinkGetter<TGetter> aLink, IFormLinkGetter<TGetter> bLink)
            where TGetter : class, IMajorRecordGetter
        {
            if (!AppearanceLinkEquivalent(aLink, bLink, linkCache, src))
            {
                diffs.Add($"{name}: expected '{FormatLink(bLink, linkCache, src)}', got '{FormatLink(aLink, linkCache, src)}'");
            }
        }
        CheckLink("Race", a.Race, b.Race);
        CheckLink("Skin(WornArmor)", a.WornArmor, b.WornArmor);
        CheckLink("HeadTexture", a.HeadTexture, b.HeadTexture);
        CheckLink("HairColor", a.HairColor, b.HairColor);

        var aHead = HeadPartKeySet(a.HeadParts, linkCache, src);
        var bHead = HeadPartKeySet(b.HeadParts, linkCache, src);
        if (!aHead.SetEquals(bHead))
        {
            var missing = bHead.Except(aHead).Select(StripHeadPartPrefix).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); // expected but absent from output
            var extra = aHead.Except(bHead).Select(StripHeadPartPrefix).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();    // in output but not expected
            var parts = new List<string>();
            if (missing.Count > 0) parts.Add("missing [" + string.Join(", ", missing) + "]");
            if (extra.Count > 0) parts.Add("extra [" + string.Join(", ", extra) + "]");
            diffs.Add("HeadParts: " + string.Join("; ", parts));
        }

        if (Math.Abs(a.Height - b.Height) > FloatEpsilon)
            diffs.Add($"Height: expected {b.Height.ToString("0.###")}, got {a.Height.ToString("0.###")}");
        if (Math.Abs(a.Weight - b.Weight) > FloatEpsilon)
            diffs.Add($"Weight: expected {b.Weight.ToString("0.###")}, got {a.Weight.ToString("0.###")}");

        bool aFemale = a.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        bool bFemale = b.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        if (aFemale != bFemale)
            diffs.Add($"Gender: expected {(bFemale ? "Female" : "Male")}, got {(aFemale ? "Female" : "Male")}");

        int aTint = a.TintLayers?.Count ?? 0, bTint = b.TintLayers?.Count ?? 0;
        if (aTint != bTint)
            diffs.Add($"TintLayers(count): expected {bTint}, got {aTint}");

        return diffs;
    }

    /// <summary>Folders + plugins of the selected (donor) mod, used to resolve its records — which are
    /// often not active in the deployed load order — via the PluginProvider/RecordHandler.</summary>
    private readonly struct SourceModRefs
    {
        public readonly HashSet<string> Folders;
        public readonly IReadOnlyList<ModKey> ModKeys;

        public SourceModRefs(ModSetting mod)
        {
            Folders = (mod.CorrespondingFolderPaths ?? new List<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ModKeys = mod.CorrespondingModKeys ?? new List<ModKey>();
        }
    }

    /// <summary>Readable identity for a FormLink: the resolved record's EditorID, else its FormKey,
    /// or "(none)" for a null link.</summary>
    private string FormatLink<TGetter>(IFormLinkGetter<TGetter> link, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, SourceModRefs src)
        where TGetter : class, IMajorRecordGetter
    {
        if (link.IsNull) return "(none)";
        var eid = ResolveEditorId(link, linkCache, src);
        return !string.IsNullOrEmpty(eid) ? eid : link.FormKey.ToString();
    }

    private static string StripHeadPartPrefix(string token)
    {
        if (token.StartsWith("eid:", StringComparison.Ordinal)) return token.Substring(4);
        if (token.StartsWith("fk:", StringComparison.Ordinal)) return token.Substring(3);
        return token;
    }

    /// <summary>
    /// FormLink equivalence by resolved EditorID. The same FormKey is trivially equal; differing
    /// FormKeys are equivalent when both resolve to records with the same (non-empty) EditorID — this
    /// handles records the patcher remapped/duplicated into the output. Falls back to FormKey identity
    /// when an EditorID isn't available on both sides.
    /// </summary>
    private bool AppearanceLinkEquivalent<TGetter>(IFormLinkGetter<TGetter> a, IFormLinkGetter<TGetter> b, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, SourceModRefs src)
        where TGetter : class, IMajorRecordGetter
    {
        if (a.IsNull && b.IsNull) return true;
        if (a.IsNull != b.IsNull) return false;
        if (a.FormKey.Equals(b.FormKey)) return true;

        string? aEid = ResolveEditorId(a, linkCache, src);
        string? bEid = ResolveEditorId(b, linkCache, src);
        if (!string.IsNullOrEmpty(aEid) && !string.IsNullOrEmpty(bEid))
        {
            return string.Equals(aEid, bEid, StringComparison.OrdinalIgnoreCase);
        }
        return false; // different FormKeys with no EditorID to vouch for equivalence
    }

    /// <summary>
    /// Builds the unordered identity set for an NPC's HeadParts. Each is keyed by its resolved EditorID
    /// (preserved across remapping; this is what the FaceGen NIF node names must match), or by FormKey
    /// when no EditorID is available.
    /// </summary>
    private HashSet<string> HeadPartKeySet(
        IReadOnlyList<IFormLinkGetter<IHeadPartGetter>> headParts,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        SourceModRefs src)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hp in headParts)
        {
            if (hp.IsNull) continue;
            var eid = ResolveEditorId(hp, linkCache, src);
            set.Add(!string.IsNullOrEmpty(eid) ? "eid:" + eid : "fk:" + hp.FormKey);
        }
        return set;
    }

    /// <summary>
    /// Resolves a record's EditorID. Tries the active load order first (vanilla, active mods, and this
    /// app's output), then the selected mod's own plugins via the RecordHandler. The donor and its
    /// appearance records frequently come from a mod whose plugin is NOT active (this app's output
    /// replaces it), and some are INJECTED records (defined in the mod's plugin but keyed to a master's
    /// FormID space, e.g. a custom head part keyed to 3DNPC.esp). Searching the mod's whole plugin set
    /// (with an origin fallback) resolves both cases — without it a donor link reads back as a bare
    /// FormKey and falsely mismatches the output's remapped-but-EditorID-preserving copy.
    /// </summary>
    private string? ResolveEditorId<TGetter>(IFormLinkGetter<TGetter> link, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, SourceModRefs src)
        where TGetter : class, IMajorRecordGetter
    {
        if (link.IsNull) return null;

        // Resolve by the SPECIFIC record type, not IMajorRecordGetter. Resolving the universal base
        // forces Mutagen to build the load order's untyped/global record index (every record of every
        // type across all plugins, incl. the huge NPC groups) on first use — a one-time multi-second
        // cost on a big load order, observed as ~9s on the first validated NPC. A typed resolve builds
        // only that type's (small) index. And since we only need the EditorID, resolve the identifier
        // rather than materializing the record (per Mutagen's overlay best practices).
        if (linkCache.TryResolveIdentifier<TGetter>(link.FormKey, out var eid) && !string.IsNullOrEmpty(eid))
        {
            return eid;
        }

        if (_recordHandler.TryGetRecordFromMods(link, src.ModKeys, src.Folders,
                RecordHandler.RecordLookupFallBack.Origin, out var modRec) && modRec != null && !string.IsNullOrEmpty(modRec.EditorID))
        {
            return modRec.EditorID;
        }

        return null;
    }

    private string DescribeWinner(ModKey winningModKey, ModSetting modSetting, List<IModListingGetter<ISkyrimModGetter>> listings)
    {
        var listing = listings.FirstOrDefault(l => l.ModKey.Equals(winningModKey));
        bool isNpc2 = listing?.Mod?.ModHeader.Description != null &&
                      listing.Mod.ModHeader.Description.Equals(Patcher.PluginDescriptionSignature, StringComparison.Ordinal);
        if (!isNpc2 && winningModKey.FileName.String.Equals(_environmentStateProvider.OutputPluginFileName, StringComparison.OrdinalIgnoreCase))
        {
            isNpc2 = true;
        }

        if (isNpc2) return $"{winningModKey.FileName} (this app's output)";
        if (modSetting.CorrespondingModKeys != null && modSetting.CorrespondingModKeys.Contains(winningModKey))
        {
            return $"{winningModKey.FileName} (selected mod's own plugin)";
        }
        return winningModKey.FileName.String;
    }

    // ----------------------------------------------------------------------------------
    // Check 2: deployed FaceGen assets
    // ----------------------------------------------------------------------------------
    private void CheckFaceGen(
        FormKey npcFk,        // recipient NPC — used for row identity (the NPC the user cares about)
        FormKey subjectFk,    // whose deployed FaceGen to check: recipient in record mode, surrogate in SkyPatcher mode
        FormKey donorFk,
        string displayName,
        string selectedModName,
        ModSetting modSetting,
        string dataFolder,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        RunContext run,
        ValidationRunResult result,
        StringBuilder log)
    {
        // The deployed/winning FaceGen lives under the SUBJECT's path (the recipient NPC in record
        // mode; the surrogate "_Template" NPC in SkyPatcher mode — this app always writes it loose).
        // The selected mod supplies it under the DONOR's path, loose OR packed in a BSA.
        var (targetMeshRel, _) = Auxilliary.GetFaceGenSubPathStrings(subjectFk, regularized: true);
        var (donorMeshRel, _) = Auxilliary.GetFaceGenSubPathStrings(donorFk, regularized: true);

        string subjectPath = Path.Combine(dataFolder, targetMeshRel);
        bool subjectExists = File.Exists(subjectPath);

        // Independent of the source-matching below: does the deployed FaceGen's baked
        // geometry actually line up with the head parts this NPC resolves to in the
        // live load order? A mismatch (wrong plugin version, missing master, a null or
        // swapped head part, or a mod author shipping a .nif that doesn't match its
        // plugin) is the classic cause of the in-game dark-face bug — and neither the
        // renderer nor the patcher surfaces it. Only meaningful when a loose FaceGen is
        // actually deployed to Data (the BSA-provided case is handled by the order
        // checks below; extending the consistency scan to it is future work).
        if (subjectExists)
        {
            using (ContextualPerformanceTracer.Trace("FaceGenConsistency"))
                CheckFaceGenHeadPartConsistency(npcFk, subjectFk, subjectPath, targetMeshRel, displayName, selectedModName, linkCache, result);
        }

        // Resolve the expected source: loose first, then the selected mod's BSAs (extract to temp).
        string? sourcePath;
        bool sourceFromBsa = false;
        string? sourceTemp = null;
        using (ContextualPerformanceTracer.Trace("FaceGenSourceResolve"))
        {
            sourcePath = FindLooseInModFolders(modSetting, donorMeshRel);
            if (sourcePath == null)
            {
                sourceTemp = TryExtractSelectedModBsaFaceGen(modSetting, donorMeshRel, dataFolder, run);
                if (sourceTemp != null) { sourcePath = sourceTemp; sourceFromBsa = true; }
            }
        }

        try
        {
            if (!subjectExists && sourcePath == null)
            {
                // No loose deployed FaceGen and the selected mod has none (loose or BSA): vanilla
                // FaceGen-in-BSA, or an NPC with no custom FaceGen. Nothing to compare; stay quiet.
                return;
            }

            if (subjectExists && sourcePath == null)
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Check = ValidationCheckKind.Asset,
                    NpcDisplayName = displayName,
                    NpcFormKey = npcFk.ToString(),
                    SelectedMod = selectedModName,
                    Issue = "FaceGen .nif is deployed to Data, but the selected mod provides no FaceGen for this NPC (no loose file and none in its BSAs). Verify the correct mod/donor.",
                    Details = targetMeshRel,
                });
                return;
            }

            if (!subjectExists && sourcePath != null)
            {
                // No loose FaceGen in Data: the game would fall back to a BSA-packed one. Resolve the
                // BSA candidates (among plugins that override this NPC) and compare to the selection.
                // BSA-vs-BSA conflicts resolve by archive order (first-loaded wins, opposite of
                // plugins/loose) — and the true order spans ini-listed vanilla archives too — so we
                // classify honestly instead of asserting a single definitive winner.
                var candidates = ResolveBsaFaceGenCandidates(subjectFk, targetMeshRel, linkCache, dataFolder, run);
                try
                {
                    if (candidates.Count == 0)
                    {
                        result.Issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Warning,
                            Check = ValidationCheckKind.Asset,
                            NpcDisplayName = displayName,
                            NpcFormKey = npcFk.ToString(),
                            SelectedMod = selectedModName,
                            Issue = $"The selected mod provides FaceGen for this NPC ({(sourceFromBsa ? "in a BSA" : "loose")}), but it is not deployed: no loose file in Data, and no active BSA (among plugins that override this NPC) contains it. Deploy/extract the output's FaceGen.",
                            Details = targetMeshRel,
                        });
                        return;
                    }

                    var matching = candidates.Where(c => FilesEqual(c.TempPath, sourcePath!)).ToList();
                    var differing = candidates.Where(c => !FilesEqual(c.TempPath, sourcePath!)).ToList();

                    if (differing.Count == 0)
                    {
                        // Every active BSA that provides it matches the selection.
                        result.Issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Info,
                            Check = ValidationCheckKind.Asset,
                            NpcDisplayName = displayName,
                            NpcFormKey = npcFk.ToString(),
                            SelectedMod = selectedModName,
                            Issue = "No loose FaceGen is deployed, but the selected mod's FaceGen is provided via BSA with no conflicting BSA found. The game should display it correctly.",
                            WinningSource = DescribeBsaCandidates(matching),
                            Details = targetMeshRel,
                        });
                    }
                    else if (matching.Count > 0)
                    {
                        // Selected version is in a BSA, but another BSA provides a different one → order-dependent.
                        result.Issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Warning,
                            Check = ValidationCheckKind.Asset,
                            NpcDisplayName = displayName,
                            NpcFormKey = npcFk.ToString(),
                            SelectedMod = selectedModName,
                            Issue = "No loose FaceGen, and active BSAs disagree: the selected mod's FaceGen is in one BSA but another provides a different one. BSA conflicts resolve by archive order (first-loaded wins, opposite of plugins/loose), so the result is fragile — extract the selected FaceGen to loose to guarantee it.",
                            WinningSource = "selected in " + DescribeBsaCandidates(matching) + " | conflicting: " + DescribeBsaCandidates(differing),
                            Details = targetMeshRel,
                        });
                    }
                    else
                    {
                        // No candidate matches the selection → a different BSA's FaceGen will show.
                        string winner = differing.Count == 1
                            ? DescribeBsaCandidates(differing)
                            : "one of: " + DescribeBsaCandidates(differing) + " (BSA archive order decides)";
                        result.Issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Error,
                            Check = ValidationCheckKind.Asset,
                            NpcDisplayName = displayName,
                            NpcFormKey = npcFk.ToString(),
                            SelectedMod = selectedModName,
                            Issue = "No loose FaceGen is deployed, and the active BSA(s) provide a DIFFERENT FaceGen than the selected mod, so the game will not show the selected appearance.",
                            WinningSource = winner,
                            Details = targetMeshRel,
                        });
                    }
                }
                finally
                {
                    foreach (var c in candidates) TryDelete(c.TempPath);
                }
                return;
            }

            // Step 1: both exist — does the deployed FaceGen match the selected mod's source?
            bool deployedMatchesSource;
            using (ContextualPerformanceTracer.Trace("FaceGenFilesEqual"))
                deployedMatchesSource = FilesEqual(subjectPath, sourcePath!);
            if (deployedMatchesSource)
            {
                return; // Match.
            }

            // Mismatch — identify what is actually supplying the deployed file.
            // Step 2: other mods' loose FaceGen.
            var looseCulprits = FindLooseFaceGenProviders(targetMeshRel, subjectPath, modSetting);
            string winningSource;
            if (looseCulprits.Count > 0)
            {
                winningSource = string.Join("; ", looseCulprits);
            }
            else
            {
                // Step 3: BSAs of plugins that provide an entry for this NPC.
                winningSource = FindBsaFaceGenCulprit(subjectFk, targetMeshRel, subjectPath, linkCache, modSetting, dataFolder, run)
                                ?? $"Unknown (no byte-identical copy among loose mods in '{_settings.ModsFolder}' or NPC-providing plugin BSAs)";
            }

            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Error,
                Check = ValidationCheckKind.Asset,
                NpcDisplayName = displayName,
                NpcFormKey = npcFk.ToString(),
                SelectedMod = selectedModName,
                Issue = $"The deployed FaceGen .nif does not match the selected mod's FaceGen{(sourceFromBsa ? " (source read from the mod's BSA)" : "")}.",
                WinningSource = winningSource,
                Details = $"{targetMeshRel} (deployed {SafeFileLength(subjectPath)} bytes vs selected {SafeFileLength(sourcePath!)} bytes)",
            });
            log.AppendLine($"  FACEGEN mismatch; provider={winningSource}");
        }
        finally
        {
            if (sourceTemp != null) TryDelete(sourceTemp);
        }
    }

    /// <summary>
    /// Cross-checks the deployed FaceGen .nif's baked shapes against the head parts the
    /// NPC resolves to in the (untrimmed) live load order. Catches the general class of
    /// FaceGen/record mismatches that produce the in-game dark-face bug — wrong plugin
    /// version, missing master, null/swapped head part, or an author-side .nif/plugin
    /// mismatch — none of which the renderer or patcher can detect.
    /// </summary>
    private void CheckFaceGenHeadPartConsistency(
        FormKey npcFk, FormKey subjectFk, string nifPath, string relMeshPath, string displayName,
        string selectedModName, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, ValidationRunResult result)
    {
        if (!linkCache.TryResolve<INpcGetter>(subjectFk, out var npcGetter))
            return;

        FaceGenConsistencyAnalyzer.Result analysis;
        try
        {
            // Resolve head parts + race against the live load order — exactly what the engine sees.
            analysis = _faceGenConsistency.Analyze(
                npcGetter,
                fk => linkCache.TryResolve<IHeadPartGetter>(fk, out var hp) ? hp : null,
                fk => linkCache.TryResolve<IRaceGetter>(fk, out var r) ? r : null,
                nifPath);
        }
        catch
        {
            return; // a malformed NIF must never abort the validation run
        }

        if (!analysis.HasMismatch) return;

        result.Issues.Add(new ValidationIssue
        {
            Severity = ValidationSeverity.Warning,
            Check = ValidationCheckKind.FaceGen,
            NpcDisplayName = displayName,
            NpcFormKey = npcFk.ToString(),
            SelectedMod = selectedModName,
            Issue = analysis.BuildReason(),
            Details = relMeshPath,
        });
    }

    private static string? FindLooseInModFolders(ModSetting modSetting, string regularizedRelPath)
    {
        if (modSetting.CorrespondingFolderPaths == null) return null;
        // Reverse so the last folder wins, matching AssetHandler's loose-file resolution.
        for (int i = modSetting.CorrespondingFolderPaths.Count - 1; i >= 0; i--)
        {
            var candidate = Path.Combine(modSetting.CorrespondingFolderPaths[i], regularizedRelPath);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Step 2 culprit search: scans the top-level mod folders for a loose, byte-identical copy of
    /// the deployed file to name the mod actually supplying it. Skips the selected mod's own folders
    /// (already compared) and stops after a few matches.
    /// </summary>
    private List<string> FindLooseFaceGenProviders(string regularizedRelPath, string subjectPath, ModSetting selectedMod)
    {
        var matches = new List<string>();
        var modsFolder = _settings.ModsFolder;
        if (string.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder)) return matches;

        var selectedFolders = (selectedMod.CorrespondingFolderPaths ?? new List<string>())
            .Select(Auxilliary.NormalizeFolderForCompare)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var modDir in Directory.EnumerateDirectories(modsFolder))
        {
            if (selectedFolders.Contains(Auxilliary.NormalizeFolderForCompare(modDir))) continue;
            var candidate = Path.Combine(modDir, regularizedRelPath);
            if (File.Exists(candidate) && FilesEqual(candidate, subjectPath))
            {
                matches.Add(Path.GetFileName(modDir));
                if (matches.Count >= 5) break; // Cap; one provider is the norm.
            }
        }
        return matches;
    }

    /// <summary>
    /// Step 3 culprit search: for each plugin that overrides this NPC, looks in its BSA(s) (as seen
    /// in the deployed Data folder) for the FaceGen and, if present, extracts and compares it to the
    /// deployed file. Returns a description of the first byte-identical BSA source, or null.
    /// </summary>
    private string? FindBsaFaceGenCulprit(
        FormKey npcFk,
        string targetMeshRel,
        string subjectPath,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        ModSetting selectedMod,
        string dataFolder,
        RunContext run)
    {
        var selectedKeys = selectedMod.CorrespondingModKeys != null
            ? new HashSet<ModKey>(selectedMod.CorrespondingModKeys)
            : new HashSet<ModKey>();

        var candidateKeys = linkCache.ResolveAllContexts<INpc, INpcGetter>(npcFk)
            .Select(c => c.ModKey)
            .Where(k => !selectedKeys.Contains(k))
            .Distinct()
            .ToList();

        foreach (var modKey in candidateKeys)
        {
            // Through the mod manager the active plugin's BSAs are visible in the (virtual) Data folder.
            HashSet<string> bsaPaths;
            try { bsaPaths = _bsaHandler.GetBsaPathsForPluginInDir(modKey, dataFolder, run.Release); }
            catch { continue; }

            foreach (var bsaPath in bsaPaths)
            {
                var temp = TryExtractFromBsa(bsaPath, targetMeshRel, run);
                if (temp == null) continue;
                try
                {
                    if (FilesEqual(temp, subjectPath))
                    {
                        return $"{modKey.FileName} (BSA: {Path.GetFileName(bsaPath)})";
                    }
                }
                finally
                {
                    TryDelete(temp);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Gathers every BSA (among plugins that override this NPC, as seen in the deployed Data folder)
    /// that contains the FaceGen, extracting each to a temp file. The caller compares them to the
    /// selected source and must delete the returned temp files.
    /// </summary>
    private List<(ModKey ModKey, string BsaPath, string TempPath)> ResolveBsaFaceGenCandidates(
        FormKey npcFk, string targetMeshRel, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, string dataFolder, RunContext run)
    {
        var results = new List<(ModKey ModKey, string BsaPath, string TempPath)>();
        var candidateKeys = linkCache.ResolveAllContexts<INpc, INpcGetter>(npcFk)
            .Select(c => c.ModKey)
            .Distinct()
            .ToList();

        foreach (var modKey in candidateKeys)
        {
            HashSet<string> bsaPaths;
            try { bsaPaths = _bsaHandler.GetBsaPathsForPluginInDir(modKey, dataFolder, run.Release); }
            catch { continue; }

            foreach (var bsaPath in bsaPaths)
            {
                var temp = TryExtractFromBsa(bsaPath, targetMeshRel, run);
                if (temp != null) results.Add((modKey, bsaPath, temp));
            }
        }
        return results;
    }

    private static string DescribeBsaCandidates(IEnumerable<(ModKey ModKey, string BsaPath, string TempPath)> candidates)
        => string.Join("; ", candidates.Select(c => $"{c.ModKey.FileName} (BSA: {Path.GetFileName(c.BsaPath)})"));

    /// <summary>
    /// Extracts the FaceGen from the selected mod's BSA(s) to a temp file, or null. Normal mods
    /// carry their BSAs in their own folder(s); the synthetic auto-generated "Base Game"/"Creation
    /// Club" entries have NO folder paths — their assets live in the vanilla/CC BSAs in the game
    /// Data folder (registered via CorrespondingModKeys). For those, search the Data folder too, or
    /// a vanilla/CC donor's FaceGen never resolves and validation falsely reports "the selected mod
    /// provides no FaceGen for this NPC" (e.g. an NPC sharing a Base Game appearance). Mirrors
    /// BsaHandler's Base Game/CC handling and the dataFolder BSA lookups used elsewhere in this file.
    /// </summary>
    private string? TryExtractSelectedModBsaFaceGen(ModSetting modSetting, string donorMeshRel, string dataFolder, RunContext run)
    {
        if (modSetting.CorrespondingModKeys == null || modSetting.CorrespondingModKeys.Count == 0) return null;
        var folders = new List<string>(modSetting.CorrespondingFolderPaths ?? new List<string>());
        if (modSetting.IsAutoGenerated || folders.Count == 0) folders.Add(dataFolder);
        if (folders.Count == 0) return null;

        Dictionary<ModKey, HashSet<string>> bsaByKey;
        try { bsaByKey = _bsaHandler.GetBsaPathsForPluginsInDirs(modSetting.CorrespondingModKeys, folders, run.Release); }
        catch { return null; }

        foreach (var bsaPath in bsaByKey.Values.SelectMany(x => x).Distinct())
        {
            var temp = TryExtractFromBsa(bsaPath, donorMeshRel, run);
            if (temp != null) return temp;
        }
        return null;
    }

    /// <summary>
    /// If the BSA contains <paramref name="relPath"/>, extracts it to a fresh temp file and returns
    /// the path; otherwise null. The reader is opened once per run (cached + refcounted) and released
    /// in <see cref="CleanupRun"/>.
    /// </summary>
    private string? TryExtractFromBsa(string bsaPath, string relPath, RunContext run)
    {
        var index = EnsureBsaOpen(bsaPath, run);
        if (index == null) return null;

        string normalized = relPath.Replace('/', '\\');
        if (!index.Contains(normalized)) return null;

        string temp = NewTempPath(run);
        try
        {
            var (ok, _) = _bsaHandler.ExtractFileAsync(bsaPath, relPath, temp).GetAwaiter().GetResult();
            if (ok && File.Exists(temp)) return temp;
        }
        catch
        {
            // fall through to cleanup
        }
        TryDelete(temp);
        return null;
    }

    /// <summary>
    /// Opens the BSA reader for <paramref name="bsaPath"/> (cached + refcounted for the run) and
    /// returns its file-path index (case-insensitive, backslash-normalized) so per-NPC existence
    /// checks are O(1). Returns null (and caches null) if the BSA is missing/unreadable.
    /// </summary>
    private HashSet<string>? EnsureBsaOpen(string bsaPath, RunContext run)
    {
        if (run.BsaIndex.TryGetValue(bsaPath, out var existing)) return existing;

        HashSet<string>? index = null;
        if (File.Exists(bsaPath))
        {
            try
            {
                var readers = _bsaHandler.OpenBsaArchiveReaders(new[] { bsaPath }, run.Release, cacheReaders: true);
                if (readers.TryGetValue(bsaPath, out var reader) && reader != null)
                {
                    run.OpenedBsaPaths.Add(bsaPath); // we hold one refcount; released in CleanupRun
                    index = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in reader.Files)
                    {
                        index.Add(f.Path.Replace('/', '\\'));
                    }
                }
            }
            catch
            {
                index = null;
            }
        }

        run.BsaIndex[bsaPath] = index; // cache the (possibly null) result so we don't retry
        return index;
    }

    private static string CreateValidationTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NPC2_ValidateFaceGen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string NewTempPath(RunContext run)
        => Path.Combine(run.TempDir, Guid.NewGuid().ToString("N") + ".nif");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private void CleanupRun(RunContext run, StringBuilder log)
    {
        try
        {
            if (run.OpenedBsaPaths.Count > 0)
            {
                // Each entry is a full BSA file path; UnloadReadersInFolders matches by prefix, so
                // passing the exact paths releases exactly the readers we opened (refcount-aware,
                // won't disturb readers other subsystems hold).
                _bsaHandler.UnloadReadersInFolders(run.OpenedBsaPaths.ToList());
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("  Cleanup (BSA readers) error: " + ex.Message);
        }

        try
        {
            if (!string.IsNullOrEmpty(run.TempDir) && Directory.Exists(run.TempDir))
            {
                Directory.Delete(run.TempDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("  Cleanup (temp dir) error: " + ex.Message);
        }
    }

    /// <summary>Per-run state: game release, temp extraction dir, and cached BSA readers/indexes.</summary>
    private sealed class RunContext
    {
        public GameRelease Release;
        public string TempDir = string.Empty;
        public readonly HashSet<string> OpenedBsaPaths = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, HashSet<string>?> BsaIndex = new(StringComparer.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------------------------
    // Check 3: SkyPatcher overrides
    // ----------------------------------------------------------------------------------
    private void CheckSkyPatcher(
        FormKey npcFk,
        string displayName,
        string selectedModName,
        INpcGetter? npcRecord,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        SkyPatcherIndex skyIndex,
        ValidationRunResult result)
    {
        // Exact hits: lines that name this NPC directly (filterByNpcs / EditorID).
        var hits = skyIndex.Lookup(npcFk, npcRecord?.EditorID);

        // Broad-filter hits: lines gated only by race/faction/keyword/mod/gender/class/combat-style/voice.
        // Evaluate each against this NPC's resolved record to see whether it is actually captured.
        foreach (var rule in skyIndex.BroadFilterRules)
        {
            if (rule.IsNpc2) continue; // This app never writes broad filters; defensive.
            if (MatchesBroadFilter(rule, npcFk, npcRecord, linkCache, out var matchedBy))
            {
                hits.Add(rule.ToHit(matchedBy));
            }
        }

        if (hits.Count == 0) return;

        foreach (var hit in hits)
        {
            if (hit.IsNpc2) continue; // Don't flag this app's own ini.

            // For broad-filter hits, spell out which filter dimension captured the NPC.
            string via = hit.MatchNote != null ? $" (captured by broad filter: {hit.MatchNote})" : string.Empty;

            if (_settings.UseSkyPatcherMode)
            {
                bool higherPriority = string.Compare(hit.SortKey, skyIndex.Npc2SortKey, StringComparison.OrdinalIgnoreCase) > 0;
                if (higherPriority)
                {
                    result.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Error,
                        Check = ValidationCheckKind.SkyPatcher,
                        NpcDisplayName = displayName,
                        NpcFormKey = npcFk.ToString(),
                        SelectedMod = selectedModName,
                        Issue = "Another SkyPatcher .ini sets this NPC's visual style and appears to load AFTER this app in alphanumeric order, so it would override the output." + via,
                        WinningSource = hit.IniRelPath,
                        Details = hit.RawLine,
                    });
                }
                else
                {
                    result.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Info,
                        Check = ValidationCheckKind.SkyPatcher,
                        NpcDisplayName = displayName,
                        NpcFormKey = npcFk.ToString(),
                        SelectedMod = selectedModName,
                        Issue = "Another SkyPatcher .ini sets this NPC's visual style at lower priority than this app (this app's .ini wins, by alphanumeric order)." + via,
                        WinningSource = hit.IniRelPath,
                        Details = hit.RawLine,
                    });
                }
            }
            else
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Check = ValidationCheckKind.SkyPatcher,
                    NpcDisplayName = displayName,
                    NpcFormKey = npcFk.ToString(),
                    SelectedMod = selectedModName,
                    Issue = "A SkyPatcher .ini sets this NPC's visual style. SkyPatcher applies at runtime and will override this app's record-based appearance." + via,
                    WinningSource = hit.IniRelPath,
                    Details = hit.RawLine,
                });
            }
        }
    }

    // ----------------------------------------------------------------------------------
    // Broad-filter evaluation
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Decides whether a broad-filter SkyPatcher line (one gated by race/faction/keyword/mod/gender/
    /// class/combat-style/voice rather than an explicit NPC list) actually captures this NPC. A rule
    /// matches when every inclusion clause is satisfied and no exclusion clause is triggered; a rule
    /// with only exclusion clauses ("apply to everyone except...") matches any non-excluded NPC.
    /// <paramref name="matchedBy"/> reports which dimension(s) captured it (for the issue message).
    /// </summary>
    private bool MatchesBroadFilter(
        BroadFilterRule rule,
        FormKey npcFk,
        INpcGetter? npc,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        out string matchedBy)
    {
        matchedBy = string.Empty;
        HashSet<FormKey>? keywordCache = null; // resolved lazily (NPC + race keywords)
        var matchedDims = new List<string>();

        foreach (var clause in rule.Clauses)
        {
            bool satisfies = EvaluateClause(clause, npcFk, npc, linkCache, ref keywordCache);
            if (clause.Excluded)
            {
                if (satisfies) return false; // NPC is on this clause's exclusion set → rule skips it.
            }
            else
            {
                if (!satisfies) return false; // a required inclusion clause failed.
                matchedDims.Add(clause.Label);
            }
        }

        matchedBy = matchedDims.Count > 0
            ? string.Join("+", matchedDims.Distinct(StringComparer.OrdinalIgnoreCase))
            : "all NPCs except the excluded set";
        return true;
    }

    /// <summary>Evaluates a single filter clause's positive condition against the NPC (ignoring the
    /// excluded flag, which <see cref="MatchesBroadFilter"/> applies).</summary>
    private bool EvaluateClause(
        FilterClause clause,
        FormKey npcFk,
        INpcGetter? npc,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        ref HashSet<FormKey>? keywordCache)
    {
        switch (clause.Dim)
        {
            case FilterDim.Npc:
                return clause.FormKeys.Contains(npcFk);

            case FilterDim.Mod:
                // filterByModNames targets NPCs that originate from the named plugin (the defining
                // master of the FormID). Override-only relationships are not treated as a match.
                return clause.Names.Contains(npcFk.ModKey.FileName.String.ToLowerInvariant());

            case FilterDim.Race:
                return npc != null && !npc.Race.IsNull && clause.FormKeys.Contains(npc.Race.FormKey);

            case FilterDim.Class:
                return npc != null && !npc.Class.IsNull && clause.FormKeys.Contains(npc.Class.FormKey);

            case FilterDim.CombatStyle:
                return npc != null && !npc.CombatStyle.IsNull && clause.FormKeys.Contains(npc.CombatStyle.FormKey);

            case FilterDim.VoiceType:
                return npc != null && !npc.Voice.IsNull && clause.FormKeys.Contains(npc.Voice.FormKey);

            case FilterDim.Gender:
                if (npc == null) return false;
                bool female = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
                return clause.Female == female;

            case FilterDim.Faction:
            {
                if (npc == null) return false;
                // Factions are AND-combined: the NPC must belong to every listed faction.
                var npcFactions = npc.Factions.Select(f => f.Faction.FormKey).ToHashSet();
                return clause.FormKeys.All(npcFactions.Contains);
            }

            case FilterDim.Keyword:
            {
                if (npc == null) return false;
                keywordCache ??= ResolveNpcKeywords(npc, linkCache);
                return clause.OrWithin
                    ? clause.FormKeys.Any(keywordCache.Contains)
                    : clause.FormKeys.All(keywordCache.Contains);
            }

            default:
                return false;
        }
    }

    /// <summary>The keyword FormKeys an NPC carries for filtering: its own KWDA plus its race's
    /// keywords (best effort; template-inherited keywords are not resolved).</summary>
    private static HashSet<FormKey> ResolveNpcKeywords(INpcGetter npc, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
    {
        var set = new HashSet<FormKey>();
        if (npc.Keywords != null)
        {
            foreach (var kw in npc.Keywords) set.Add(kw.FormKey);
        }
        if (!npc.Race.IsNull && linkCache.TryResolve<IRaceGetter>(npc.Race.FormKey, out var race) && race.Keywords != null)
        {
            foreach (var kw in race.Keywords) set.Add(kw.FormKey);
        }
        return set;
    }

    // ----------------------------------------------------------------------------------
    // SkyPatcher parsing
    // ----------------------------------------------------------------------------------

    /// <summary>One parsed line of this app's own SkyPatcher .ini: the recipient maps to the
    /// surrogate FormKey its <c>copyVisualStyle</c> directive points at.</summary>
    private sealed class Npc2SkyPatcherLine
    {
        public FormKey Surrogate;
        public bool HasSurrogate;
        public string RawLine = string.Empty;
    }

    /// <summary>
    /// Parses this app's own SkyPatcher .ini into a map of recipient-NPC key -> the surrogate FormKey
    /// referenced by <c>copyVisualStyle</c>. Used to confirm the visual-transfer line exists and to
    /// locate the surrogate template for the record/FaceGen checks.
    /// </summary>
    private static Dictionary<string, Npc2SkyPatcherLine> ParseNpc2SkyPatcherIni(string npc2IniPath)
    {
        var map = new Dictionary<string, Npc2SkyPatcherLine>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(npc2IniPath)) return map;

        string[] lines;
        try { lines = File.ReadAllLines(npc2IniPath); }
        catch { return map; }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";")) continue;

            int colon = line.IndexOf(':');
            if (colon < 0) continue;

            // Filter part: filterByNPCs=<recipient>[,<recipient>...]
            var filterParts = line.Substring(0, colon).Split('=', 2);
            if (filterParts.Length != 2) continue;
            var filterKey = filterParts[0].Trim().ToLowerInvariant();
            if (filterKey != "filterbynpcs" && filterKey != "filterbynpcsformid") continue;

            // Actions part: copyVisualStyle=<surrogate>,skin=...,height=... (the surrogate FormKey
            // has no comma, so a simple per-segment scan is safe).
            FormKey surrogate = default;
            bool hasSurrogate = false;
            foreach (var seg in line.Substring(colon + 1).Split(','))
            {
                var trimmed = seg.Trim();
                if (trimmed.StartsWith("copyVisualStyle=", StringComparison.OrdinalIgnoreCase))
                {
                    hasSurrogate = TryParseSkyPatcherFormKey(trimmed.Substring("copyVisualStyle=".Length).Trim(), out surrogate);
                    break;
                }
            }

            var entry = new Npc2SkyPatcherLine
            {
                Surrogate = surrogate,
                HasSurrogate = hasSurrogate,
                RawLine = line.Length > 400 ? line.Substring(0, 400) + "..." : line
            };

            foreach (var token in filterParts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var key = NormalizeSkyPatcherFormId(token);
                if (key != null) map[key] = entry;
            }
        }
        return map;
    }

    /// <summary>Converts a SkyPatcher form token (<c>Plugin.esp|hexid</c>) to a FormKey.</summary>
    private static bool TryParseSkyPatcherFormKey(string token, out FormKey fk)
    {
        fk = default;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('|');
        if (parts.Length != 2) return false;
        var plugin = parts[0].Trim();
        var idHex = parts[1].Trim();
        if (plugin.Length == 0 || idHex.Length == 0) return false;
        if (idHex.Length > 6) idHex = idHex.Substring(idHex.Length - 6);
        idHex = idHex.PadLeft(6, '0');
        try { fk = FormKey.Factory(idHex + ":" + plugin); return true; }
        catch { return false; }
    }

    private SkyPatcherIndex BuildSkyPatcherIndex(string skyPatcherNpcRoot, string npc2IniPath, StringBuilder log)
    {
        var index = new SkyPatcherIndex
        {
            Npc2SortKey = MakeSortKey(skyPatcherNpcRoot, npc2IniPath)
        };

        if (!Directory.Exists(skyPatcherNpcRoot))
        {
            log.AppendLine("No SkyPatcher npc config folder found.");
            return index;
        }

        foreach (var iniPath in Directory.EnumerateFiles(skyPatcherNpcRoot, "*.ini", SearchOption.AllDirectories))
        {
            string sortKey = MakeSortKey(skyPatcherNpcRoot, iniPath);
            string relPath = Path.GetRelativePath(skyPatcherNpcRoot, iniPath);
            bool isNpc2 = string.Equals(Path.GetFullPath(iniPath), Path.GetFullPath(npc2IniPath), StringComparison.OrdinalIgnoreCase);

            string[] lines;
            try { lines = File.ReadAllLines(iniPath); }
            catch { continue; }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;

                var targets = new List<string>();
                bool hasNpcFilter = false;
                bool hasBroadFilter = false;
                bool hasVisual = false;

                foreach (var seg in line.Split(':'))
                {
                    if (seg.Length == 0) continue;
                    var eq = seg.IndexOf('=');
                    string key = (eq >= 0 ? seg.Substring(0, eq) : seg).Trim().ToLowerInvariant();
                    string val = eq >= 0 ? seg.Substring(eq + 1).Trim() : string.Empty;

                    if (key is "filterbynpcs" or "filterbynpcsformid")
                    {
                        hasNpcFilter = true;
                        foreach (var t in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            targets.Add(t);
                        }
                    }
                    else if (key.StartsWith("filterby"))
                    {
                        hasBroadFilter = true;
                    }
                    else if (VisualActionKeys.Contains(key))
                    {
                        hasVisual = true;
                    }
                }

                if (!hasVisual) continue;

                if (hasNpcFilter && targets.Count > 0)
                {
                    var hit = new SkyPatcherHit
                    {
                        IniRelPath = relPath,
                        SortKey = sortKey,
                        IsNpc2 = isNpc2,
                        RawLine = line.Length > 400 ? line.Substring(0, 400) + "..." : line
                    };

                    foreach (var t in targets)
                    {
                        if (t.Contains('|'))
                        {
                            var key = NormalizeSkyPatcherFormId(t);
                            if (key != null) index.AddByFormKey(key, hit);
                        }
                        else
                        {
                            index.AddByEditorId(t.ToLowerInvariant(), hit);
                        }
                    }
                }
                else if (!hasNpcFilter && hasBroadFilter)
                {
                    // Visual action gated only by broad filters (race/faction/keyword/etc.). Parse the
                    // filter criteria so we can evaluate per-NPC whether each validated NPC is captured.
                    // Lines that contain a filter we can't evaluate are counted toward the manual-review note.
                    if (TryParseBroadFilterRule(line, relPath, sortKey, isNpc2, out var rule))
                    {
                        index.BroadFilterRules.Add(rule);
                    }
                    else
                    {
                        index.UnevaluableBroadFilterLineCount++;
                    }
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Parses a broad-filter visual line into an evaluable <see cref="BroadFilterRule"/>. Returns false
    /// (so the caller treats it as un-evaluable) if the line contains any filter dimension this tool does
    /// not understand, or a filter value it cannot parse — in those cases we cannot honestly claim whether
    /// an NPC is captured, so we fall back to the manual-review note rather than risk a false verdict.
    /// </summary>
    private static bool TryParseBroadFilterRule(string line, string relPath, string sortKey, bool isNpc2, out BroadFilterRule rule)
    {
        rule = new BroadFilterRule
        {
            IniRelPath = relPath,
            SortKey = sortKey,
            IsNpc2 = isNpc2,
            RawLine = line.Length > 400 ? line.Substring(0, 400) + "..." : line
        };

        foreach (var seg in line.Split(':'))
        {
            if (seg.Length == 0) continue;
            int eq = seg.IndexOf('=');
            string key = (eq >= 0 ? seg.Substring(0, eq) : seg).Trim().ToLowerInvariant();
            string val = eq >= 0 ? seg.Substring(eq + 1).Trim() : string.Empty;

            if (!key.StartsWith("filterby"))
            {
                continue; // an action directive (copyVisualStyle=, skin=, setFlags=, ...) — not a filter.
            }

            string dimToken = key.Substring("filterby".Length);
            bool excluded = false;
            if (dimToken.EndsWith("excluded"))
            {
                excluded = true;
                dimToken = dimToken.Substring(0, dimToken.Length - "excluded".Length);
            }

            var clause = new FilterClause { Excluded = excluded };
            switch (dimToken)
            {
                case "npcs": case "npc": case "npcsformid":
                    clause.Dim = FilterDim.Npc; clause.Label = "npc"; break;
                case "races": case "race":
                    clause.Dim = FilterDim.Race; clause.Label = "race"; break;
                case "factions": case "faction":
                    clause.Dim = FilterDim.Faction; clause.Label = "faction"; break;
                case "keywords": case "keyword":
                    clause.Dim = FilterDim.Keyword; clause.Label = "keyword"; clause.OrWithin = false; break;
                case "keywordsor":
                    clause.Dim = FilterDim.Keyword; clause.Label = "keyword"; clause.OrWithin = true; break;
                case "modnames": case "modname": case "mods": case "mod":
                    clause.Dim = FilterDim.Mod; clause.Label = "mod"; break;
                case "classes": case "class":
                    clause.Dim = FilterDim.Class; clause.Label = "class"; break;
                case "combatstyles": case "combatstyle":
                    clause.Dim = FilterDim.CombatStyle; clause.Label = "combat style"; break;
                case "voicetypes": case "voicetype":
                    clause.Dim = FilterDim.VoiceType; clause.Label = "voice type"; break;
                case "gender":
                    clause.Dim = FilterDim.Gender; clause.Label = "gender"; break;
                default:
                    return false; // unknown filter dimension → can't evaluate this line.
            }

            if (!PopulateClauseValues(clause, val)) return false;
            rule.Clauses.Add(clause);
        }

        // A pure-broad line must have at least one filter clause to be meaningful.
        return rule.Clauses.Count > 0;
    }

    /// <summary>Fills a clause's value set from the raw filter value. Returns false when the value cannot
    /// be parsed (empty, or a malformed form token), forcing the line to be treated as un-evaluable.</summary>
    private static bool PopulateClauseValues(FilterClause clause, string val)
    {
        if (clause.Dim == FilterDim.Gender)
        {
            switch (val.Trim().ToLowerInvariant())
            {
                case "female": clause.Female = true; return true;
                case "male": clause.Female = false; return true;
                default: return false;
            }
        }

        var tokens = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return false;

        if (clause.Dim == FilterDim.Mod)
        {
            foreach (var t in tokens) clause.Names.Add(t.ToLowerInvariant());
            return clause.Names.Count > 0;
        }

        // All remaining dimensions are FormID-based (Plugin.esp|hexid).
        foreach (var t in tokens)
        {
            if (!TryParseSkyPatcherFormKey(t, out var fk)) return false;
            clause.FormKeys.Add(fk);
        }
        return clause.FormKeys.Count > 0;
    }

    /// <summary>A broad SkyPatcher filter dimension this tool can evaluate per-NPC.</summary>
    private enum FilterDim { Npc, Mod, Race, Faction, Keyword, Class, CombatStyle, VoiceType, Gender }

    /// <summary>One parsed <c>filterByXxx</c> clause from a broad-filter line.</summary>
    private sealed class FilterClause
    {
        public FilterDim Dim;
        public bool Excluded;
        public bool OrWithin;            // keyword OR vs AND; ignored for other dims
        public string Label = string.Empty;
        public readonly HashSet<FormKey> FormKeys = new();
        public readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
        public bool? Female;
    }

    /// <summary>A SkyPatcher visual line gated only by broad filters, parsed for per-NPC evaluation.</summary>
    private sealed class BroadFilterRule
    {
        public string IniRelPath = string.Empty;
        public string SortKey = string.Empty;
        public bool IsNpc2;
        public string RawLine = string.Empty;
        public readonly List<FilterClause> Clauses = new();

        public SkyPatcherHit ToHit(string matchedBy) => new()
        {
            IniRelPath = IniRelPath,
            SortKey = SortKey,
            IsNpc2 = IsNpc2,
            RawLine = RawLine,
            MatchNote = matchedBy
        };
    }

    private static string MakeSortKey(string root, string iniPath)
    {
        // SkyPatcher loads .ini files in alphanumeric order (last wins). Approximate that
        // ordering with the path relative to the npc config root, lowercased.
        try { return Path.GetRelativePath(root, iniPath).ToLowerInvariant().Replace('/', '\\'); }
        catch { return iniPath.ToLowerInvariant(); }
    }

    private static string? NormalizeSkyPatcherFormId(string token)
    {
        var parts = token.Split('|');
        if (parts.Length != 2) return null;
        string plugin = parts[0].Trim().ToLowerInvariant();
        string id = parts[1].Trim().TrimStart('0').ToLowerInvariant();
        if (id.Length == 0) id = "0";
        if (plugin.Length == 0) return null;
        return plugin + "|" + id;
    }

    private static string FormKeyToSkyPatcherKey(FormKey fk)
    {
        string id = fk.ID.ToString("X").TrimStart('0').ToLowerInvariant();
        if (id.Length == 0) id = "0";
        return fk.ModKey.FileName.String.ToLowerInvariant() + "|" + id;
    }

    private sealed class SkyPatcherHit
    {
        public string IniRelPath { get; init; } = string.Empty;
        public string SortKey { get; init; } = string.Empty;
        public bool IsNpc2 { get; init; }
        public string RawLine { get; init; } = string.Empty;

        /// Non-null when this hit came from a broad filter; names the dimension(s) that captured the NPC.
        public string? MatchNote { get; init; }
    }

    private sealed class SkyPatcherIndex
    {
        private readonly Dictionary<string, List<SkyPatcherHit>> _byFormKey = new();
        private readonly Dictionary<string, List<SkyPatcherHit>> _byEditorId = new();

        public string Npc2SortKey { get; set; } = string.Empty;

        /// Broad-filter visual lines parsed and evaluable per-NPC (race/faction/keyword/mod/...).
        public List<BroadFilterRule> BroadFilterRules { get; } = new();

        /// Broad-filter visual lines we could NOT evaluate (unrecognized/unparseable filter); surfaced
        /// as a run-level manual-review note rather than per-NPC.
        public int UnevaluableBroadFilterLineCount { get; set; }

        public void AddByFormKey(string key, SkyPatcherHit hit)
        {
            if (!_byFormKey.TryGetValue(key, out var list)) { list = new(); _byFormKey[key] = list; }
            list.Add(hit);
        }

        public void AddByEditorId(string editorId, SkyPatcherHit hit)
        {
            if (editorId.Length == 0) return;
            if (!_byEditorId.TryGetValue(editorId, out var list)) { list = new(); _byEditorId[editorId] = list; }
            list.Add(hit);
        }

        public List<SkyPatcherHit> Lookup(FormKey fk, string? editorId)
        {
            var seen = new HashSet<(string, string)>();
            var results = new List<SkyPatcherHit>();

            if (_byFormKey.TryGetValue(FormKeyToSkyPatcherKey(fk), out var byFk))
            {
                foreach (var h in byFk)
                {
                    if (seen.Add((h.IniRelPath, h.RawLine))) results.Add(h);
                }
            }
            if (!string.IsNullOrEmpty(editorId) && _byEditorId.TryGetValue(editorId.ToLowerInvariant(), out var byEd))
            {
                foreach (var h in byEd)
                {
                    if (seen.Add((h.IniRelPath, h.RawLine))) results.Add(h);
                }
            }
            return results;
        }
    }

    // ----------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------
    private static bool FilesEqual(string pathA, string pathB)
    {
        try
        {
            var a = new FileInfo(pathA);
            var b = new FileInfo(pathB);
            if (!a.Exists || !b.Exists) return false;
            if (a.Length != b.Length) return false;

            using var sa = a.OpenRead();
            using var sb = b.OpenRead();
            const int bufSize = 64 * 1024;
            byte[] ba = new byte[bufSize];
            byte[] bb = new byte[bufSize];
            int readA;
            while ((readA = sa.Read(ba, 0, bufSize)) > 0)
            {
                int offset = 0;
                while (offset < readA)
                {
                    int readB = sb.Read(bb, offset, readA - offset);
                    if (readB == 0) return false;
                    offset += readB;
                }
                for (int i = 0; i < readA; i++)
                {
                    if (ba[i] != bb[i]) return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return -1; }
    }

    private static void WriteLog(StringBuilder log, ValidationRunResult result)
    {
        if (result.Blocked) log.AppendLine("BLOCKED: " + result.BlockReason);
        try
        {
            File.WriteAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ValidationLog.txt"),
                log.ToString(),
                new UTF8Encoding(false));
        }
        catch
        {
            // Logging is best-effort.
        }
    }
}
