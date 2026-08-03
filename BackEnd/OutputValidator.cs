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
using NPC_Plugin_Chooser_2.BackEnd.Logging;
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
        AppendPerfLine(log, $"[perf] Untrimmed environment built in {swPhase.ElapsedMilliseconds} ms ({listings.Count} plugins).");
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
        AppendPerfLine(log, $"[perf] SkyPatcher index built in {swPhase.ElapsedMilliseconds} ms.");
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

        // --- What the last run actually patched ---
        // Read from the DEPLOYED Data folder rather than the configured output directory, so it
        // describes the output the game (and everything below) is actually seeing.
        var lastRun = LoadDeployedRunLedger(dataFolder, log);

        if (DescribeModeMismatch(lastRun, _settings) is { } modeMismatch)
        {
            result.Notes.Add(modeMismatch);
            log.AppendLine($"MODE MISMATCH: {modeMismatch}");
        }

        if (lastRun != null && lastRun.ProcessedNpcs.Count == 0)
        {
            // A bootstrap marker from a crashed run, or a token written before this field existed.
            // Either way its emptiness proves nothing, and trusting it would report every NPC as
            // unpatched. Fall back to grading everything.
            result.Notes.Add(
                "NPC_Token.json in your Data folder lists no patched NPCs, so this report could not tell " +
                "which NPCs the last run actually covered. Every NPC was graded as if it had been patched; " +
                "if the last run was interrupted, re-run the patcher before trusting the findings below.");
            lastRun = null;
        }

        // --- Per-NPC checks ---
        var modSettingsByName = _settings.ModSettings
            .GroupBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int total = npcsToValidate.Count;
        // Lets a Traits-templated NPC defer to its template's own rows instead of duplicating them.
        var scopedNpcs = npcsToValidate.ToHashSet();
        var run = new RunContext
        {
            Release = _settings.SkyrimRelease.ToGameRelease(),
            TempDir = CreateValidationTempDir()
        };
        if (lastRun != null) run.EditedFaceGen.UnionWith(lastRun.EditedFaceGen);
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
                        ValidateNpc(npcFk, linkCache, listings, modSettingsByName, skyIndex, npc2IniMap, dataFolder, scopedNpcs, lastRun, run, result, log);
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

        // Stamp each row's FormID from the load order this report was generated against — the
        // UNTRIMMED one, so it matches what the console and xEdit show for the deployed game
        // rather than the trimmed order the app patches with. Done in one pass here so no check
        // has to remember to fill it in.
        StampFormIds(result.Issues, listings);

        result.NpcsChecked = total;
        progress?.Report((total, total, "Validation complete."));
        log.AppendLine($"Done. NPCs checked: {total}. Issues: {result.Issues.Count}.");
        AppendPerfLine(log, $"[perf] Per-NPC validation phase: {swPhase.ElapsedMilliseconds} ms for {total} NPC(s).");
        if (_settings.LogPerformance)
        {
            log.AppendLine(ContextualPerformanceTracer.GenerateDetailedReport("Validate Output"));
        }
        WriteLog(log, result);
        return result;
    }

    /// <summary>
    /// Fills in <see cref="ValidationIssue.NpcFormId"/> for every row from the load order the
    /// report was generated against. Rows with no NPC (environment-level notes) and NPCs whose
    /// plugin isn't in that order are left blank rather than guessed at. Internal for tests.
    /// </summary>
    internal static void StampFormIds(
        IEnumerable<ValidationIssue> issues,
        IEnumerable<IModListingGetter<ISkyrimModGetter>> listings)
    {
        var prefixes = Auxilliary.BuildFormIdPrefixes(listings);
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var issue in issues)
        {
            if (string.IsNullOrEmpty(issue.NpcFormKey)) continue;

            if (!cache.TryGetValue(issue.NpcFormKey, out var formId))
            {
                formId = FormKey.TryFactory(issue.NpcFormKey, out var fk)
                    ? Auxilliary.FormatFormId(fk, prefixes)
                    : string.Empty;
                cache[issue.NpcFormKey] = formId;
            }

            issue.NpcFormId = formId;
        }
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
        IReadOnlySet<FormKey> scopedNpcs,
        NpcToken? lastRun,
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
        INpcGetter? recipientRecord = winningCtx?.Record;
        ModKey winningModKey = winningCtx?.ModKey ?? npcFk.ModKey;

        // The record + FaceGen the game actually renders for this NPC. Same as the recipient
        // unless a Traits template redirects them (below).
        INpcGetter? winningRecord = recipientRecord;
        FormKey subjectFk = npcFk;

        string displayName = recipientRecord != null
            ? Auxilliary.GetLogString(recipientRecord, _settings.LocalizationLanguage)
            : npcFk.ToString();

        log.AppendLine($"NPC {displayName} [{npcFk}] -> '{selectedModName}' (donor {donorFk}, winner {winningModKey.FileName})");

        // Did the last run take responsibility for this NPC at all? Everything below grades the
        // output against the selection, which only means something once the answer is yes.
        if (lastRun != null && !CheckLastRunCoverage(npcFk, displayName, selectedModName, lastRun, result, log))
        {
            return;
        }

        if (!modSettingsByName.TryGetValue(selectedModName, out var modSetting))
        {
            ReportUnconfiguredMod(npcFk, displayName, selectedModName, result);
            return;
        }

        // --- Traits template --------------------------------------------------------------
        // An NPC with the Traits flag renders the TEMPLATE's appearance: the game never loads
        // this record's head parts nor this FormID's FaceGen, and the user's selection here has
        // no effect at all. Checking its own record/mesh reports files the game never touches —
        // e.g. a leftover facegeom .nif from an unrelated mod flagged as a head-part mismatch.
        // So follow the chain and validate what actually renders.
        // In SkyPatcher mode the recipient's record is never patched — this app emits an .ini line
        // that copies a surrogate's visual style onto it at runtime — so there is nothing of ours
        // to re-target on the recipient. Report the inheritance and let the .ini/surrogate checks
        // run; the surrogate gets the same treatment inside ValidateNpcSkyPatcher.
        //
        // ALL of that is conditional on the mod's effective Template Handling Mode. Under
        // TemplateHandlingMode.GiveEachNpcOwnCopy the patcher copies the terminus's appearance onto
        // this NPC's own record, CLEARS the Traits flag and writes FaceGen under this NPC's own
        // FormID — the selection is delivered here, so redirecting the checks at the terminus would
        // grade the output against a shape it deliberately no longer has, and the "whatever you
        // select has no effect" row would be false. In record mode a deployed, conflict-winning
        // output already reads as untemplated so the branch self-disarms; in SkyPatcher mode the
        // recipient's record is never patched, so it still looks templated and only this gate stops
        // the wrong row. (SkyPatcher + Inherit does not reach the output at all — the pre-patch
        // Validator.CanSkyPatcherApplyAppearance rejects those NPCs per NPC.)
        bool inheritsFaceFromTemplate =
            _settings.GetEffectiveTemplateHandlingMode(modSetting) == TemplateHandlingMode.InheritFromTemplate;

        if (inheritsFaceFromTemplate && recipientRecord != null && Auxilliary.IsValidTemplatedNpc(recipientRecord))
        {
            if (_settings.UseSkyPatcherMode)
            {
                // ...unless this run already told SkyPatcher to clear the bit. The recipient's
                // record still READS as templated — SkyPatcher removes the flag at load, not in the
                // plugin — but the face the user picked does land, so the inheritance is not news.
                // Patcher.ApplySkyPatcherDirectives emits removeTemplateFlags=traits exactly when
                // the recipient inherits its face and the surrogate does not; without this gate the
                // check reports every one of them as a face the user will not get (80 rows on the
                // reporting run, all false).
                if (!SkyPatcherClearsTraits(npc2IniMap, npcFk))
                {
                    NoteSkyPatcherRecipientTemplate(npcFk, recipientRecord, displayName, selectedModName, linkCache, result, log);
                }
                else
                {
                    log.AppendLine("  TEMPLATE inheritance cleared at runtime by removeTemplateFlags=traits; no row");
                }
            }
            else if (!TryRedirectToTemplate(
                         npcFk, recipientRecord, displayName, ref selectedModName, ref donorFk,
                         ref winningRecord, ref winningModKey, ref subjectFk,
                         linkCache, dataFolder, scopedNpcs, result, log))
            {
                return; // Deferred to the template's own rows, unresolvable, or consistency-only.
            }

            // The redirect can retarget the checks at the TEMPLATE's own selection, which is often a
            // different mod; everything below must then be graded against that mod's files.
            if (!modSettingsByName.TryGetValue(selectedModName, out modSetting))
            {
                ReportUnconfiguredMod(npcFk, displayName, selectedModName, result);
                return;
            }
        }

        // --- Flattened inheritance --------------------------------------------------------
        // Under GiveEachNpcOwnCopy the patcher does NOT deliver the donor's own appearance: it
        // overlays the TERMINUS's fields (Auxilliary.CopyInheritedAppearance) and forwards the
        // TERMINUS's FaceGen file onto this NPC's own path. Grading either against the donor
        // compares the output to a record and a mesh it deliberately no longer carries — the
        // stub's dead face, which the game never rendered because the NPC inherited. That is
        // 78 false Errors on the reporting run. The pre-run Validator already redirects the same
        // way (Validator.FindUnwritableLink); this is the post-run half of it.
        donorFk = ResolveFlattenedDonor(modSetting, donorFk, out bool donorInheritsFace, log);

        if (_settings.UseSkyPatcherMode)
        {
            // SkyPatcher mode doesn't patch the recipient's record. It builds a surrogate "_Template"
            // NPC (a copy of the donor) in the output plugin and an .ini line that copies the
            // surrogate's visual style onto the recipient at runtime. So validate the .ini line and
            // the surrogate — not the recipient's record/FaceGen.
            ValidateNpcSkyPatcher(npcFk, donorFk, donorInheritsFace, selectedModName, displayName, recipientRecord,
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

        // Check 2: the deployed FaceGen should match the selected mod's. subjectFk is the NPC whose
        // FaceGen the game actually loads — the recipient, or its Traits template. It only leaves
        // npcFk when TryRedirectToTemplate above re-pointed it, so equality IS "no redirect".
        using (ContextualPerformanceTracer.Trace("CheckFaceGen"))
            CheckFaceGen(npcFk, subjectFk, subjectFk.Equals(npcFk), donorInheritsFace, donorFk, displayName,
                selectedModName, modSetting, dataFolder, linkCache, run, result, log);

        // Check 3: any SkyPatcher mod that would override this NPC at runtime. Filters (race,
        // faction, keyword...) match on the RECIPIENT's own record, not the template's.
        using (ContextualPerformanceTracer.Trace("CheckSkyPatcher"))
            CheckSkyPatcher(npcFk, displayName, selectedModName, recipientRecord, linkCache, skyIndex, result);
    }

    // ----------------------------------------------------------------------------------
    // Last-run coverage
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Reads the deployed NPC_Token.json — the ledger the patcher writes naming every NPC it
    /// processed and every one it deliberately skipped. Returns null when it is missing or
    /// unreadable, in which case validation grades every NPC exactly as it did before this
    /// check existed. Deliberately a SOFT dependency: an old or hand-placed output should still
    /// validate, just without the extra attribution.
    /// </summary>
    /// <summary>
    /// The run-level note shown when the deployed output was produced under a DIFFERENT output
    /// mode than the one being validated with, or null when the modes match (or the token predates
    /// the stamp and cannot say). Every check grades the deployed files against the CURRENT
    /// settings — the effective wig/antler modes among them, which flip to inert in plain Create —
    /// so a mode switch without a re-run mass-reports the old mode's deliberate rewrites
    /// (converted wigs, stripped antlers) as appearance mismatches. Said once, up front; grading
    /// still runs, because the token attributes and never filters scope.
    /// </summary>
    internal static string? DescribeModeMismatch(NpcToken? lastRun, Settings settings)
    {
        if (lastRun?.PatchingMode == null) return null;
        bool tokenSkyPatcher = lastRun.UseSkyPatcherMode ?? false;
        if (string.Equals(lastRun.PatchingMode, settings.PatchingMode.ToString(), StringComparison.OrdinalIgnoreCase) &&
            tokenSkyPatcher == settings.UseSkyPatcherMode)
        {
            return null;
        }

        static string Describe(string mode, bool skyPatcher) => skyPatcher ? $"{mode} + SkyPatcher" : mode;
        return $"The deployed output was produced in {Describe(lastRun.PatchingMode, tokenSkyPatcher)} mode, " +
               $"but you are validating with {Describe(settings.PatchingMode.ToString(), settings.UseSkyPatcherMode)} " +
               "settings. The checks below grade against the current mode's expectations, so intentional " +
               "differences from the old mode (e.g. wig conversions) will read as errors. Re-run the patcher " +
               "before trusting this report.";
    }

    private static NpcToken? LoadDeployedRunLedger(string dataFolder, StringBuilder log)
    {
        var tokenPath = Path.Combine(dataFolder, "NPC_Token.json");
        if (!File.Exists(tokenPath))
        {
            log.AppendLine($"No NPC_Token.json in the data folder ({tokenPath}); last-run coverage unknown.");
            return null;
        }

        var token = JSONhandler<NpcToken>.LoadJSONFile(tokenPath, out bool success, out string exception);
        if (!success || token == null)
        {
            log.AppendLine($"Could not read NPC_Token.json: {exception}");
            return null;
        }

        // Newtonsoft rebuilds a HashSet with the DEFAULT comparer, discarding the
        // OrdinalIgnoreCase one the model declares — so a deserialized ledger is case-sensitive
        // until it is rebuilt. Paths are compared against Auxilliary.GetFaceGenSubPathStrings
        // output, and depending on both ends staying lowercase is the kind of coupling that
        // breaks silently, so restore the comparer here rather than at each use.
        token.EditedFaceGen = new HashSet<string>(token.EditedFaceGen, StringComparer.OrdinalIgnoreCase);

        log.AppendLine($"Deployed NPC_Token.json: {token.ProcessedNpcs.Count} processed, " +
                       $"{token.SkippedNpcs.Count} skipped, {token.EditedFaceGen.Count} edited FaceGen, " +
                       $"written {token.CreationDate}, plugins [{string.Join(", ", token.CreatedPlugins)}].");
        return token;
    }

    /// <summary>How the last run's ledger accounts for one NPC.</summary>
    internal enum LastRunCoverage
    {
        /// The last run patched it with the mod selected now: grade the output.
        Covered,

        /// The last run never patched it — screening rejected the selection, or the FaceGen
        /// ladder deliberately left it alone.
        NotPatched,

        /// The last run patched it with a DIFFERENT mod than the one selected now.
        SelectionChanged
    }

    /// <summary>
    /// Three-way triage of one NPC against the last run's ledger. Pure — no state, no logging —
    /// so it can be tested directly. <paramref name="detail"/> carries the recorded skip reason
    /// (NotPatched, null when the run recorded none) or the mod the last run used
    /// (SelectionChanged); it is empty for Covered.
    /// </summary>
    internal static LastRunCoverage ClassifyLastRunCoverage(
        FormKey npcFk, string selectedModName, NpcToken lastRun, out string? detail)
    {
        if (lastRun.ProcessedNpcs.TryGetValue(npcFk, out var processed))
        {
            if (string.Equals(processed.ModName, selectedModName, StringComparison.OrdinalIgnoreCase))
            {
                detail = null;
                return LastRunCoverage.Covered;
            }

            detail = processed.ModName;
            return LastRunCoverage.SelectionChanged;
        }

        // Absent from the processed set. The skipped map names the reason when the run recorded
        // one; tokens written before that map existed simply have nothing to say, which is why
        // a missing reason is not itself treated as "covered".
        detail = lastRun.SkippedNpcs.TryGetValue(npcFk, out var reason) ? reason : null;
        return LastRunCoverage.NotPatched;
    }

    /// <summary>
    /// Applies <see cref="ClassifyLastRunCoverage"/>. Returns true when the deeper checks should
    /// run — i.e. the last run patched this NPC with the mod that is selected NOW, so anything
    /// wrong from here on really is this app's output or its deployment.
    ///
    /// <para>The other two outcomes each get one row and stop:</para>
    /// <list type="bullet">
    /// <item><b>Not patched</b> — Info. The selection was rejected by pre-run screening (the user
    /// was shown the reason and chose to continue) or the FaceGen ladder deliberately left the NPC
    /// alone. Nothing of ours is in the game for this NPC, so grading the winning record against
    /// the selection would report a mismatch this app did not cause and cannot fix.</item>
    /// <item><b>Patched with a different mod</b> — Warning, not Info. The selection changed after
    /// the last run, so the deployed output is stale. Unlike the Info case the user has had no
    /// notice of it, and it does change the face they get.</item>
    /// </list>
    /// </summary>
    private bool CheckLastRunCoverage(
        FormKey npcFk,
        string displayName,
        string selectedModName,
        NpcToken lastRun,
        ValidationRunResult result,
        StringBuilder log)
    {
        var coverage = ClassifyLastRunCoverage(npcFk, selectedModName, lastRun, out var detail);
        if (coverage == LastRunCoverage.Covered) return true;

        if (coverage == LastRunCoverage.SelectionChanged)
        {
            var processed = lastRun.ProcessedNpcs[npcFk];
            log.AppendLine($"  STALE: last run patched this NPC with '{detail}', now selected '{selectedModName}'.");
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Check = ValidationCheckKind.Selection,
                NpcDisplayName = displayName,
                NpcFormKey = npcFk.ToString(),
                SelectedMod = selectedModName,
                Issue = "Your selection for this NPC changed after the last patch run, so the deployed output " +
                        "does not reflect it. Re-run the patcher to apply it. The remaining checks were skipped, " +
                        "since the deployed output was built for a different mod.",
                WinningSource = processed.OutputPlugin.FileName,
                Details = $"Last run patched this NPC with '{detail}' " +
                          $"(appearance plugin {processed.AppearancePlugin.FileName}).",
            });
            return false;
        }

        log.AppendLine($"  NOT PATCHED by the last run{(detail == null ? string.Empty : $": {detail}")}.");
        result.Issues.Add(new ValidationIssue
        {
            Severity = ValidationSeverity.Info,
            Check = ValidationCheckKind.Selection,
            NpcDisplayName = displayName,
            NpcFormKey = npcFk.ToString(),
            SelectedMod = selectedModName,
            Issue = "Not patched. The last run did not include this NPC, so its appearance is whatever your " +
                    "load order already supplies and nothing below applies to it. Either the selection was " +
                    "skipped before patching (you were shown the reason and chose to continue), or it was " +
                    "deliberately left alone to avoid the dark-face bug.",
            Details = detail ?? "The deployed output does not record why. Re-run the patcher to have the reason " +
                                "recorded, or check the run log's screening section.",
        });
        return false;
    }

    // ----------------------------------------------------------------------------------
    // Traits templates
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Emits the explanatory row for a Traits-templated NPC and points the appearance checks at
    /// the NPC the game actually renders. Returns true when the caller should continue with the
    /// (re-targeted) checks, false when this NPC is finished — because the template is validated
    /// on its own rows, could not be followed, or has no selection to compare against (in which
    /// case the selection-independent head-part scan is run here first).
    /// </summary>
    private bool TryRedirectToTemplate(
        FormKey npcFk,
        INpcGetter recipientRecord,
        string displayName,
        ref string selectedModName,
        ref FormKey donorFk,
        ref INpcGetter? winningRecord,
        ref ModKey winningModKey,
        ref FormKey subjectFk,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        string dataFolder,
        IReadOnlySet<FormKey> scopedNpcs,
        ValidationRunResult result,
        StringBuilder log)
    {
        var (templateRecord, templateModKey, chain, failure) = ResolveTraitsAppearanceSource(recipientRecord, linkCache);
        string chainText = "Template chain: " + string.Join(" -> ", chain.Prepend(npcFk));

        const string preamble =
            "This NPC takes its appearance from another NPC (the Traits template flag), so the game shows the " +
            "template's face and ignores this NPC's own head parts and FaceGen — whatever you select here has no effect. ";

        if (templateRecord == null)
        {
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Info,
                Check = ValidationCheckKind.Template,
                NpcDisplayName = displayName,
                NpcFormKey = npcFk.ToString(),
                SelectedMod = selectedModName,
                Issue = preamble + "Its appearance could not be checked because " + failure + ".",
                Details = chainText,
            });
            log.AppendLine($"  TEMPLATE unresolved ({failure}); {chainText}");
            return false;
        }

        FormKey templateFk = templateRecord.FormKey;
        string templateName = DescribeNpc(templateRecord);
        bool templateInReport = scopedNpcs.Contains(templateFk);
        bool templateHasSelection = _settings.SelectedAppearanceMods.TryGetValue(templateFk, out var templateSelection)
                                    && !string.IsNullOrEmpty(templateSelection.ModName);

        // The inheritance only matters to the user when it changes what they get. If the template
        // is set to the same mod they picked here — the normal case after a batch selection — the
        // face in game IS the one they asked for, so say nothing and just check the right files.
        bool selectionHonoured = templateHasSelection &&
            string.Equals(templateSelection.ModName, selectedModName, StringComparison.OrdinalIgnoreCase);

        if (!selectionHonoured)
        {
            string tail = templateInReport
                ? $" '{templateName}' is validated separately in this report — see its own rows."
                : templateHasSelection
                    ? $" The checks below were run against '{templateName}' and '{templateSelection.ModName}' instead."
                    : " Only its FaceGen was checked.";

            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Check = ValidationCheckKind.Template,
                NpcDisplayName = displayName,
                NpcFormKey = npcFk.ToString(),
                SelectedMod = selectedModName,
                Issue = templateHasSelection
                    ? $"You selected '{selectedModName}' for this NPC, but it takes its appearance from another NPC (the Traits template flag): " +
                      $"'{templateName}', which is set to '{templateSelection.ModName}'. The face shown in game will be the one from " +
                      $"'{templateSelection.ModName}'. To change it, select '{selectedModName}' for '{templateName}' as well." + tail
                    : $"You selected '{selectedModName}' for this NPC, but it takes its appearance from another NPC (the Traits template flag): " +
                      $"'{templateName}', which has no appearance selection in this app. The face shown in game will be whatever wins in your " +
                      $"load order, not '{selectedModName}'. To change it, select an appearance for '{templateName}'." + tail,
                WinningSource = $"{templateName} [{templateFk}]",
                Details = chainText,
            });
        }
        log.AppendLine($"  TEMPLATE -> {templateName} [{templateFk}] (inReport={templateInReport}, hasSelection={templateHasSelection}, honoured={selectionHonoured})");

        if (templateInReport) return false;

        subjectFk = templateFk;
        winningRecord = templateRecord;
        winningModKey = templateModKey;

        if (!templateHasSelection)
        {
            // Nothing to compare the template against, but the head-part scan needs no selection
            // and is exactly the check that would otherwise have run against the wrong .nif.
            var (relMesh, _) = Auxilliary.GetFaceGenSubPathStrings(subjectFk, regularized: true);
            string nifPath = Path.Combine(dataFolder, relMesh);
            if (File.Exists(nifPath))
            {
                // subjectFk is the TEMPLATE here, whose FaceGen renders on this NPC — a preset in
                // that seat keeps the full warning, so the stand-in flag is false.
                using (ContextualPerformanceTracer.Trace("FaceGenConsistency"))
                    CheckFaceGenHeadPartConsistency(npcFk, subjectFk, subjectStandsInForNpc: false, nifPath,
                        relMesh, displayName, selectedModName, linkCache, result);
            }
            return false;
        }

        selectedModName = templateSelection.ModName;
        donorFk = templateSelection.NpcFormKey;
        return true;
    }

    /// <summary>The selection names a mod this app no longer has configured. Emitted from two
    /// places — the NPC's own selection, and the template's after a redirect — so it lives here
    /// rather than being written twice.</summary>
    private static void ReportUnconfiguredMod(
        FormKey npcFk, string displayName, string selectedModName, ValidationRunResult result) =>
        result.Issues.Add(new ValidationIssue
        {
            Severity = ValidationSeverity.Error,
            Check = ValidationCheckKind.Selection,
            NpcDisplayName = displayName,
            NpcFormKey = npcFk.ToString(),
            SelectedMod = selectedModName,
            Issue = $"The selected mod '{selectedModName}' is no longer among the configured mods.",
        });

    /// <summary><see cref="Auxilliary.GetLogString"/> appends " | " after the EditorID for a record
    /// with no Name; trim it so a row reads as a name rather than a fragment.</summary>
    private string DescribeNpc(INpcGetter npc) =>
        Auxilliary.GetLogString(npc, _settings.LocalizationLanguage).TrimEnd(' ', '|');

    /// <summary>True when this run's own .ini strips the recipient's Traits inheritance at load, so
    /// its still-templated plugin record does not describe what the game renders.</summary>
    private static bool SkyPatcherClearsTraits(Dictionary<string, Npc2SkyPatcherLine>? npc2IniMap, FormKey npcFk) =>
        npc2IniMap != null &&
        npc2IniMap.TryGetValue(FormKeyToSkyPatcherKey(npcFk), out var line) &&
        line.ClearsTraitsTemplate;

    /// <summary>
    /// SkyPatcher-mode recipient: nothing of this app's can be re-targeted (the record is never
    /// patched — an .ini line copies a surrogate's visual style onto it at runtime), so this only
    /// reports the inheritance, and only when it changes what the user gets.
    /// </summary>
    private void NoteSkyPatcherRecipientTemplate(
        FormKey npcFk,
        INpcGetter record,
        string displayName,
        string selectedModName,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        ValidationRunResult result,
        StringBuilder log)
    {
        var (templateRecord, _, chain, failure) = ResolveTraitsAppearanceSource(record, linkCache);
        string chainText = "Template chain: " + string.Join(" -> ", chain.Prepend(npcFk));

        if (templateRecord == null)
        {
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Info,
                Check = ValidationCheckKind.Template,
                NpcDisplayName = displayName,
                NpcFormKey = npcFk.ToString(),
                SelectedMod = selectedModName,
                Issue = "This NPC takes its appearance from another NPC (the Traits template flag), so the game shows the template's face. " +
                        "The chain could not be followed: " + failure + ".",
                Details = chainText,
            });
            log.AppendLine($"  TEMPLATE unresolved for {npcFk} ({failure})");
            return;
        }

        string templateName = DescribeNpc(templateRecord);
        bool hasSelection = _settings.SelectedAppearanceMods.TryGetValue(templateRecord.FormKey, out var templateSelection)
                            && !string.IsNullOrEmpty(templateSelection.ModName);
        if (hasSelection && string.Equals(templateSelection.ModName, selectedModName, StringComparison.OrdinalIgnoreCase))
        {
            // Template carries the same choice — the user gets what they picked. Nothing to say.
            log.AppendLine($"  TEMPLATE -> {templateName} (same selection '{selectedModName}'); no row");
            return;
        }

        result.Issues.Add(new ValidationIssue
        {
            Severity = ValidationSeverity.Warning,
            Check = ValidationCheckKind.Template,
            NpcDisplayName = displayName,
            NpcFormKey = npcFk.ToString(),
            SelectedMod = selectedModName,
            Issue = hasSelection
                ? $"You selected '{selectedModName}' for this NPC, but it takes its appearance from another NPC (the Traits template flag): " +
                  $"'{templateName}', which is set to '{templateSelection.ModName}'. The face shown in game will be the one from " +
                  $"'{templateSelection.ModName}'. To change it, select '{selectedModName}' for '{templateName}' as well."
                : $"You selected '{selectedModName}' for this NPC, but it takes its appearance from another NPC (the Traits template flag): " +
                  $"'{templateName}', which has no appearance selection in this app. The face shown in game will be whatever wins in your " +
                  $"load order, not '{selectedModName}'. To change it, select an appearance for '{templateName}'.",
            WinningSource = $"{templateName} [{templateRecord.FormKey}]",
            Details = chainText,
        });
        log.AppendLine($"  TEMPLATE -> {templateName} [{templateRecord.FormKey}] (hasSelection={hasSelection}); warned");
    }

    /// <summary>What <see cref="RedirectSurrogateToTemplate"/> decided about a SkyPatcher surrogate.</summary>
    private enum SurrogateRedirect
    {
        /// The surrogate is not templated; validate it directly.
        None,

        /// Subject and donor were both re-pointed at their template roots; run all checks.
        Redirected,

        /// The subject root is known but the selected mod supplies no record for it, so there is
        /// nothing to compare against — run only the selection-independent head-part scan.
        ConsistencyOnly,

        /// The chain could not be followed; the rendered face is unknown, so skip the checks.
        Unresolved
    }

    /// <summary>
    /// Applies the same Traits-template rule to a SkyPatcher surrogate. The surrogate is a copy of
    /// the donor, so a templated donor produces a templated surrogate — and then neither the
    /// surrogate's head parts nor its FaceGen are what the game renders. Follows both chains (the
    /// surrogate through the load order, the donor through the selected mod) so checks B and C see
    /// the records and the .nif that actually apply.
    /// </summary>
    private SurrogateRedirect RedirectSurrogateToTemplate(
        FormKey npcFk,
        string displayName,
        string selectedModName,
        ModSetting modSetting,
        INpcGetter surrogateRec,
        ref INpcGetter subjectRec,
        ref ModKey subjectModKey,
        ref FormKey subjectFk,
        ref FormKey subjectDonorFk,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        ValidationRunResult result,
        StringBuilder log)
    {
        if (!Auxilliary.IsValidTemplatedNpc(surrogateRec)) return SurrogateRedirect.None;

        var (rootRec, rootModKey, chain, failure) = ResolveTraitsAppearanceSource(surrogateRec, linkCache);
        string chainText = "Surrogate template chain: " + string.Join(" -> ", chain.Prepend(surrogateRec.FormKey));

        const string preamble =
            "SkyPatcher mode: the surrogate NPC this app created takes its appearance from another NPC " +
            "(the Traits template flag), so its own head parts and FaceGen are not what the game renders. ";

        void AddRow(string tail, string winningSource) => result.Issues.Add(new ValidationIssue
        {
            Severity = ValidationSeverity.Info,
            Check = ValidationCheckKind.Template,
            NpcDisplayName = displayName,
            NpcFormKey = npcFk.ToString(),
            SelectedMod = selectedModName,
            Issue = preamble + tail,
            WinningSource = winningSource,
            Details = chainText,
        });

        if (rootRec == null)
        {
            AddRow("Its appearance could not be checked because " + failure + ".", string.Empty);
            log.AppendLine($"  SKYPATCHER surrogate template unresolved ({failure}); {chainText}");
            return SurrogateRedirect.Unresolved;
        }

        string rootName = DescribeNpc(rootRec);
        subjectRec = rootRec;
        subjectModKey = rootModKey;
        subjectFk = rootRec.FormKey;

        // Donor side: the mod's own version of the face that renders.
        var donorRootFk = ResolveDonorAppearanceRoot(modSetting, subjectDonorFk, linkCache);
        if (donorRootFk.IsNull || TryResolveSelectedSourceNpc(modSetting, donorRootFk) == null)
        {
            AddRow($"The face it shows comes from '{rootName}', but '{selectedModName}' supplies no record for that NPC, " +
                   $"so the appearance could not be compared against '{selectedModName}' — only its FaceGen was checked. " +
                   "That usually means the selected mod does not change the face this NPC actually shows.",
                   $"{rootName} [{subjectFk}]");
            log.AppendLine($"  SKYPATCHER surrogate -> {rootName} [{subjectFk}]; donor root unresolved in '{selectedModName}'");
            return SurrogateRedirect.ConsistencyOnly;
        }

        // Both sides resolved: the checks below now look at the right record and the right .nif,
        // and will speak up if either is wrong. A redirect that worked is not news — stay silent.
        subjectDonorFk = donorRootFk;
        log.AppendLine($"  SKYPATCHER surrogate -> {rootName} [{subjectFk}]; donor root {donorRootFk}");
        return SurrogateRedirect.Redirected;
    }

    /// <summary>
    /// Walks the donor's Traits chain inside the SELECTED MOD (falling back to the load order for
    /// links the mod does not override), so the FaceGen comparison uses the file the mod ships for
    /// the face that actually renders. Returns a null FormKey when the chain cannot be followed.
    /// </summary>
    private FormKey ResolveDonorAppearanceRoot(
        ModSetting modSetting,
        FormKey donorFk,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        int maxDepth = 25)
    {
        var visited = new HashSet<FormKey> { donorFk };
        FormKey currentFk = donorFk;
        INpcGetter? current = TryResolveSelectedSourceNpc(modSetting, donorFk);
        if (current == null) linkCache.TryResolve<INpcGetter>(donorFk, out current);

        for (int depth = 0; depth < maxDepth && current != null; depth++)
        {
            if (!Auxilliary.IsValidTemplatedNpc(current)) return currentFk;

            var nextFk = current.Template.FormKey;
            if (!visited.Add(nextFk)) return FormKey.Null;

            var nextRec = TryResolveSelectedSourceNpc(modSetting, nextFk);
            if (nextRec == null && !linkCache.TryResolve<INpcGetter>(nextFk, out nextRec)) return FormKey.Null;

            currentFk = nextFk;
            current = nextRec;
        }

        return FormKey.Null;
    }

    /// <summary>
    /// Walks the Traits template chain to the NPC whose appearance is actually rendered: the
    /// first record in the chain that does not itself carry the flag. Each link resolves
    /// winner-first, so the chain follows the deployed load order.
    /// Returns a null record when the chain cannot be followed — a Leveled NPC template (the
    /// appearance is picked at runtime), an unresolvable link, a loop, or an absurd depth —
    /// with <c>Failure</c> phrased for the report.
    /// </summary>
    private static (INpcGetter? Record, ModKey ModKey, List<FormKey> Chain, string? Failure)
        ResolveTraitsAppearanceSource(
            INpcGetter start,
            ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
            int maxDepth = 25)
    {
        var chain = new List<FormKey>();
        var visited = new HashSet<FormKey> { start.FormKey };
        INpcGetter current = start;
        ModKey currentModKey = start.FormKey.ModKey;

        for (int depth = 0; depth < maxDepth; depth++)
        {
            if (!Auxilliary.IsValidTemplatedNpc(current))
                return (current, currentModKey, chain, null);

            var templateFk = current.Template.FormKey;
            chain.Add(templateFk);

            if (!visited.Add(templateFk))
                return (null, default, chain, "its template chain loops back on itself");

            var ctx = linkCache.ResolveAllContexts<INpc, INpcGetter>(templateFk).FirstOrDefault();
            if (ctx == null)
            {
                bool isLeveled = linkCache.TryResolve<ILeveledNpcGetter>(templateFk, out _);
                return (null, default, chain, isLeveled
                    ? "its template is a Leveled NPC, so the game picks the appearance at runtime"
                    : $"its template ({templateFk}) could not be found in your load order");
            }

            current = ctx.Record;
            currentModKey = ctx.ModKey;
        }

        return (null, default, chain, "its template chain is unreasonably long");
    }

    // ----------------------------------------------------------------------------------
    // SkyPatcher-mode per-NPC validation
    // ----------------------------------------------------------------------------------
    private void ValidateNpcSkyPatcher(
        FormKey npcFk,
        FormKey donorFk,
        bool donorInheritsFace,
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

        // Whose record and FaceGen the game actually uses for the surrogate, and which donor
        // FormKey the selected mod supplies them under. Both move to the template root when the
        // surrogate carries the Traits flag (see RedirectSurrogateToTemplate).
        FormKey subjectFk = surrogateFk;
        FormKey subjectDonorFk = donorFk;
        var redirect = SurrogateRedirect.None;

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
            // A templated surrogate renders its template's face, not its own — re-point both
            // sides at the root before comparing anything.
            INpcGetter subjectRec = surrogateRec;
            ModKey subjectModKey = surrogateCtx!.ModKey;
            redirect = RedirectSurrogateToTemplate(
                npcFk, displayName, selectedModName, modSetting, surrogateRec,
                ref subjectRec, ref subjectModKey, ref subjectFk, ref subjectDonorFk,
                linkCache, result, log);

            if (redirect is SurrogateRedirect.None or SurrogateRedirect.Redirected)
            {
                var donorRec = TryResolveSelectedSourceNpc(modSetting, subjectDonorFk);
                if (donorRec == null)
                {
                    if (!modSetting.IsFaceGenOnlyEntry && !modSetting.FaceGenOnlyNpcFormKeys.Contains(subjectDonorFk))
                    {
                        result.Issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Warning,
                            Check = ValidationCheckKind.Record,
                            NpcDisplayName = displayName,
                            NpcFormKey = npcFk.ToString(),
                            SelectedMod = selectedModName,
                            Issue = "SkyPatcher mode: could not resolve the selected mod's appearance NPC to compare against the surrogate template.",
                            Details = $"Donor FormKey {subjectDonorFk}",
                        });
                    }
                }
                else
                {
                    var diffs = CompareAppearance(subjectRec, donorRec, linkCache, modSetting);
                    if (diffs.Count > 0)
                    {
                        result.Issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Error,
                            Check = ValidationCheckKind.Record,
                            NpcDisplayName = displayName,
                            NpcFormKey = npcFk.ToString(),
                            SelectedMod = selectedModName,
                            Issue = redirect == SurrogateRedirect.Redirected
                                ? "SkyPatcher mode: the appearance the surrogate inherits from its template does not match the selected mod's appearance NPC, so the face shown in game will be wrong."
                                : "SkyPatcher mode: the surrogate template's appearance does not match the selected mod's appearance NPC, so the visual style copied at runtime will be wrong.",
                            WinningSource = DescribeWinner(subjectModKey, modSetting, listings),
                            Details = "Differing fields: " + string.Join(" | ", diffs),
                        });
                        log.AppendLine($"  SKYPATCHER surrogate mismatch ({string.Join(" | ", diffs)})");
                    }
                }
            }
        }

        // ---- Check C: the deployed FaceGen of whatever renders must match the donor's FaceGen ----
        switch (redirect)
        {
            case SurrogateRedirect.Unresolved:
                break; // Rendered face unknown — checking any .nif would be guesswork.

            case SurrogateRedirect.ConsistencyOnly:
                // No mod-side counterpart to compare against, but the head-part scan needs none.
                // The subject is the surrogate's TEMPLATE root, so it does not stand in for npcFk.
                var (relMesh, _) = Auxilliary.GetFaceGenSubPathStrings(subjectFk, regularized: true);
                string nifPath = Path.Combine(dataFolder, relMesh);
                if (File.Exists(nifPath))
                {
                    using (ContextualPerformanceTracer.Trace("FaceGenConsistency"))
                        CheckFaceGenHeadPartConsistency(npcFk, subjectFk, subjectStandsInForNpc: false, nifPath,
                            relMesh, displayName, selectedModName, linkCache, result);
                }
                break;

            default:
                // Only an un-redirected surrogate is this NPC's own 1:1 stand-in — it is minted per
                // target, so its FaceGen renders on npcFk and nothing else. A surrogate redirect
                // has already moved subjectDonorFk to the donor's own template root, so the "owns
                // no mesh" suppression applies to the un-redirected case alone.
                CheckFaceGen(npcFk, subjectFk, redirect == SurrogateRedirect.None,
                    donorInheritsFace && redirect == SurrogateRedirect.None, subjectDonorFk, displayName,
                    selectedModName, modSetting, dataFolder, linkCache, run, result, log);
                break;
        }

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
            if (!modSetting.IsFaceGenOnlyEntry && !modSetting.FaceGenOnlyNpcFormKeys.Contains(donorFk))
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
    /// The record whose appearance the patcher actually delivered for this selection. Normally the
    /// donor itself; under <see cref="TemplateHandlingMode.GiveEachNpcOwnCopy"/> a donor that
    /// inherits its face is flattened, so the output carries the TERMINUS's fields and the
    /// terminus's FaceGen file — and that is what the checks have to be graded against. Mirrors the
    /// pre-run screen in <c>Validator.FindUnwritableLink</c>, which redirects the same way.
    /// </summary>
    /// <param name="donorInheritsFace">True whenever the donor carries the Traits flag, whether or
    /// not the chain could be followed and regardless of mode. Used to suppress the "ships no face
    /// mesh" row: a templated stub owning no mesh is the definition of inheriting one, not a
    /// finding the user can act on.</param>
    private FormKey ResolveFlattenedDonor(ModSetting modSetting, FormKey donorFk,
        out bool donorInheritsFace, StringBuilder log)
    {
        donorInheritsFace = false;

        // RecordHandler.ResolveNpcPreferringMod, NOT TryResolveSelectedSourceNpc: the latter is
        // scoped to the mod's own plugins and returns null outright for a FaceGen-only entry, so
        // the two commonest flatten cases would never be seen — a mesh-only overhaul (VIGILANT -
        // NPC Overhaul ships no plugin at all) and a mod whose plugin simply does not override
        // this NPC (Botox). The patcher resolves the chain with the load-order fallback, and the
        // grading has to follow the record it actually flattened from.
        bool isFaceGenOnly = modSetting.IsFaceGenOnlyEntry ||
                             modSetting.FaceGenOnlyNpcFormKeys.Contains(donorFk);
        var folderPaths = modSetting.CorrespondingFolderPaths?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                          ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        INpcGetter? Resolve(FormKey fk) =>
            _recordHandler.ResolveNpcPreferringMod(fk, modSetting, folderPaths, isFaceGenOnly);

        var donor = Resolve(donorFk);
        if (donor == null || !Auxilliary.IsValidTemplatedNpc(donor)) return donorFk;

        donorInheritsFace = true;

        // Inherit mode leaves the inheritance standing, so the donor IS what was delivered; the
        // Traits-template branch above already re-pointed the checks at what renders.
        if (_settings.GetEffectiveTemplateHandlingMode(modSetting) !=
            TemplateHandlingMode.GiveEachNpcOwnCopy)
        {
            return donorFk;
        }

        var status = Auxilliary.TryResolveAppearanceTerminus(donor, Resolve, out var terminusFk);
        if (status != FaceGenChainStatus.Resolved || terminusFk.Equals(donorFk)) return donorFk;

        log.AppendLine($"  FLATTENED donor {donorFk} -> terminus {terminusFk} (grading against the terminus)");
        return terminusFk;
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

        // Wig handling deliberately rewrites the donor's Hair-type head parts (ForwardToSkin
        // removes them, ConvertToHeadParts replaces them with the minted wig parent; see
        // WigForwarder / HeadPartWigConverter), and antler Remove deletes keyword-detected antler
        // head parts. Exclude both from the comparison when they apply to this NPC so the
        // intentional rewrites aren't reported as mismatches. The wig mode is resolved PER NPC
        // off the output record — the patcher converts instead of forwarding when that record's
        // outfit field is inert, and the mode alone does not say so.
        bool excludeHair = WigHandlingRewritesHair(b, a, sourceMod, linkCache, src);
        var antlerRemovals = AntlerRemovalHeadPartKeys(b, sourceMod, linkCache, src);
        var aHead = HeadPartKeySet(a.HeadParts, linkCache, src, excludeHair, antlerRemovals);
        var bHead = HeadPartKeySet(b.HeadParts, linkCache, src, excludeHair, antlerRemovals);
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
    /// Removes the source-plugin suffix this app appends when it duplicates a record into the
    /// output — <c>NordRaceChild</c> -> <c>NordRaceChild_RSChildren.esp</c>. RecordHandler mints it
    /// at three sites, twice from the mod's own plugin and once from the record's defining plugin,
    /// always as <c>"_" + ModKey</c>.
    ///
    /// <para>Without this, "Include As New" made every NPC it touched fail the record comparison:
    /// the mode exists to give a mod its own copy of a shared record, so a difference here is the
    /// feature working, not a defect. On the measuring run all 11 RS Children NPCs reported as
    /// Errors — and the patcher's own race-drift advice is what tells users to turn the mode on.</para>
    ///
    /// <para>Matched against WHOLE candidate plugin names rather than by scanning for the last
    /// underscore, because plugin names contain underscores of their own
    /// (<c>OCW_Obscure's_CollegeofWinterhold.esp</c>) and there is no way to tell the separator
    /// from the name's own underscores. The longest match wins, so one plugin name being the tail
    /// of another cannot under-strip. Both sides are normalised the same way, and a stem is only
    /// taken when something is left of it. Pure — no state — so it can be tested directly.</para>
    /// </summary>
    internal static string StripDuplicateSuffix(string? editorId, IEnumerable<ModKey> candidatePlugins)
    {
        if (string.IsNullOrEmpty(editorId)) return editorId ?? string.Empty;

        string best = editorId;
        foreach (var plugin in candidatePlugins)
        {
            var suffix = "_" + plugin.FileName;
            if (editorId.Length <= suffix.Length) continue;
            if (!editorId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            var stem = editorId.Substring(0, editorId.Length - suffix.Length);
            if (stem.Length < best.Length) best = stem;
        }

        return best;
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
            // Normalised so an "Include As New" duplicate matches the record it was copied from:
            // that rename is the mode working, not a mismatch. The candidates are the selected
            // mod's own plugins (two of RecordHandler's three mint sites) plus each side's own
            // defining plugin (the third).
            var candidates = src.ModKeys.Append(a.FormKey.ModKey).Append(b.FormKey.ModKey);
            return string.Equals(
                StripDuplicateSuffix(aEid, candidates),
                StripDuplicateSuffix(bEid, candidates),
                StringComparison.OrdinalIgnoreCase);
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
        SourceModRefs src,
        bool excludeHair = false,
        HashSet<FormKey>? excludeKeys = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hp in headParts)
        {
            if (hp.IsNull) continue;
            if (excludeKeys != null && excludeKeys.Contains(hp.FormKey)) continue;
            if (excludeHair && IsHairHeadPart(hp, linkCache, src)) continue;
            var eid = ResolveEditorId(hp, linkCache, src);
            // Same normalisation as AppearanceLinkEquivalent, so an "Include As New" copy of a head
            // part is the same set member as the part it was copied from. Each side is keyed
            // independently here, hence the candidates covering both the mod's plugins and this
            // link's own defining plugin.
            if (!string.IsNullOrEmpty(eid))
            {
                set.Add("eid:" + StripDuplicateSuffix(eid, src.ModKeys.Append(hp.FormKey.ModKey)));
            }
            else
            {
                set.Add("fk:" + hp.FormKey);
            }
        }
        return set;
    }

    /// <summary>The donor NPC's antler head parts that antler Remove deletes from
    /// the output — excluded from the head-part comparison so the intentional
    /// removal isn't a mismatch. A head part qualifies when keyword-detected OR
    /// manually designated (by EditorID, scope-filtered; see
    /// <see cref="Settings.IsAntlerHeadPart"/>). Null unless antler Remove is the
    /// effective mode or nothing qualifies.</summary>
    private HashSet<FormKey>? AntlerRemovalHeadPartKeys(INpcGetter donor, ModSetting sourceMod,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, SourceModRefs src)
    {
        if (_settings.GetEffectiveAntlerMode(sourceMod) != AntlerHandlingMode.Remove) return null;
        var result = new HashSet<FormKey>();
        foreach (var hp in donor.HeadParts)
        {
            if (hp.IsNull) continue;
            var eid = ResolveEditorId(hp, linkCache, src);
            if (_settings.IsAntlerHeadPart(sourceMod, hp.FormKey, eid, donor.FormKey))
                result.Add(hp.FormKey);
        }
        return result.Count > 0 ? result : null;
    }

    private bool IsHairHeadPart(IFormLinkGetter<IHeadPartGetter> link,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, SourceModRefs src)
    {
        if (linkCache.TryResolve<IHeadPartGetter>(link.FormKey, out var rec) && rec.Type != null)
        {
            return rec.Type == HeadPart.TypeEnum.Hair;
        }

        if (_recordHandler.TryGetRecordFromMods(link, src.ModKeys, src.Folders,
                RecordHandler.RecordLookupFallBack.Origin, out var modRec) && modRec is IHeadPartGetter hpRec)
        {
            return hpRec.Type == HeadPart.TypeEnum.Hair;
        }

        return false;
    }

    /// <summary>True when <paramref name="donor"/> carries at least one Hair-type head part that
    /// actually contributes baked geometry — the only kind the wig handling removes. Geometry may
    /// sit on the part itself or on an ExtraPart (a modeless parent owning a modeled hairline still
    /// renders), so both are tested, exactly as <c>WigForwarder.CollectBakedShapeNames</c> does.
    /// Records resolve through the mod's own plugins first, then the load order, matching the
    /// patcher's <c>ResolveFromModsOrWinner</c>.
    ///
    /// <para>The walk is static and takes its resolver, for the same reason
    /// <see cref="WigDetector.EffectiveWnamWigArmatures"/> does: the resolution strategy is the
    /// only thing that differs between callers, and the walk itself is what must not drift.</para></summary>
    private bool DonorHasModeledHair(INpcGetter donor, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        SourceModRefs src) =>
        DonorHasModeledHair(donor, link =>
            _recordHandler.TryGetRecordFromMods(link, src.ModKeys, src.Folders,
                RecordHandler.RecordLookupFallBack.Origin, out var modRec) && modRec is IHeadPartGetter scoped
                ? scoped
                : linkCache.TryResolve<IHeadPartGetter>(link.FormKey, out var winner)
                    ? winner
                    : null);

    /// <inheritdoc cref="DonorHasModeledHair(INpcGetter, ILinkCache{ISkyrimMod, ISkyrimModGetter}, SourceModRefs)"/>
    internal static bool DonorHasModeledHair(INpcGetter donor,
        Func<IFormLinkGetter<IHeadPartGetter>, IHeadPartGetter?> resolveHeadPart)
    {
        if (donor.HeadParts == null) return false;

        foreach (var hpLink in donor.HeadParts)
        {
            if (hpLink == null || hpLink.IsNull) continue;
            var hpRec = resolveHeadPart(hpLink);
            if (hpRec?.Type != HeadPart.TypeEnum.Hair) continue;
            if (FaceGenConsistencyAnalyzer.BearsBakedGeometry(hpRec)) return true;

            if (hpRec.ExtraParts == null) continue;
            foreach (var extraLink in hpRec.ExtraParts)
            {
                if (extraLink == null || extraLink.IsNull) continue;
                if (FaceGenConsistencyAnalyzer.BearsBakedGeometry(resolveHeadPart(extraLink))) return true;
            }
        }

        return false;
    }

    /// <summary>Mirrors the wig handling's Hair-head-part rewrite for validation: ForwardToSkin
    /// (needs a donor WNAM to forward into) replaces the donor hair with the modeless bald record,
    /// and ConvertToHeadParts (no WNAM requirement) replaces it with the minted wig parent — both
    /// are Hair-type on the output side, so excluding Hair-type parts from BOTH sides of the
    /// comparison covers either rewrite, in either direction (the converter ADDS a part where a
    /// bald donor had none), and the converter's per-NPC ForwardToSkin fallback, which this
    /// record-level check cannot distinguish. A declined WNAM conversion (multi-ARMA, beast race,
    /// unresolvable NIF) leaves the donor hair intact — the same accepted record-level imprecision
    /// as the outfit note above.
    ///
    /// <para><b>Both wig sources, both modes.</b> The WNAM branch used to be gated to
    /// ConvertToHeadParts, on the reading that ForwardToSkin only ever acts on an outfit wig.
    /// It does not: <c>WigForwarder.Apply</c>'s already-skin-carried branch strips the hair for
    /// a wig the WNAM ALREADY carries, because a skin-carried hair-slot wig does not suppress
    /// head-part hair the way an equipped one does. This mirror never learned about that branch,
    /// so every NPC of a mod that ships its wigs on the skin reported its intended hair
    /// replacement as an appearance mismatch. ForwardToSkin narrows the walk to hair-slot ARMAs
    /// to match <c>CollectWnamWigArmas(hairSlotOnly: true)</c> — <c>BipedObjectFlag.Hair</c>
    /// alone, NOT <c>WigDetector.HairSlots</c>, which is load-bearing there and here.</para>
    ///
    /// <para><b>Per NPC, not per mod.</b> The mode is resolved through
    /// <see cref="Settings.GetEffectiveWigModeForNpc"/> off <paramref name="outputRecord"/>,
    /// because ForwardToOutfit converts to head parts for any NPC whose outfit field is inert —
    /// the majority of them on a full load order, since whole vanilla classes are
    /// inventory-templated. Reading the mod-level mode alone reported all 1,621 of those
    /// conversions as an appearance mismatch. The output record is the right subject in both
    /// patching modes: Create-and-Patch overrides the winning record and inherits its template
    /// flags, and plain Create copies the donor the patcher tested.</para></summary>
    private bool WigHandlingRewritesHair(INpcGetter donor, INpcGetter outputRecord, ModSetting sourceMod,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, SourceModRefs src)
    {
        var wigMode = _settings.GetEffectiveWigModeForNpc(sourceMod, outputRecord);
        if (wigMode != WigHandlingMode.ForwardToSkin && wigMode != WigHandlingMode.ConvertToHeadParts) return false;

        // ForwardToSkin only ever REMOVES, and the collectors skip geometry-less Hair parts
        // outright — so a donor whose only Hair part is a bald placeholder keeps it and its head
        // parts come through unchanged. Claiming a removal there would blind the comparison to
        // real head-part damage on those NPCs. ConvertToHeadParts gets no such precondition: it
        // ADDS the minted wig parent whether or not the donor had hair (the WNAM source treats a
        // bald donor as legal and synthesizes the partition template instead of harvesting), so
        // the output's Hair parts differ from the donor's either way.
        if (wigMode == WigHandlingMode.ForwardToSkin && !DonorHasModeledHair(donor, linkCache, src)) return false;

        // Skin-carried (WNAM) wig source.
        if (!donor.WornArmor.IsNull)
        {
            IArmorGetter? wnam =
                _recordHandler.TryGetRecordFromMods(donor.WornArmor, src.ModKeys, src.Folders,
                    RecordHandler.RecordLookupFallBack.Origin, out var wnamRec) && wnamRec is IArmorGetter scopedWnam
                    ? scopedWnam
                    : linkCache.TryResolve<IArmorGetter>(donor.WornArmor.FormKey, out var wnamWinner)
                        ? wnamWinner
                        : null;
            // Shared walk. One deliberate alignment comes with it: this site used to test an
            // armature link even when the record resolved NOWHERE, matching a FormKey against
            // DetectedWigArmatures with a null EditorID. The converter this method exists to mirror
            // skips unresolvable armatures and therefore removes no hair, so claiming otherwise made
            // the validator disagree with the patcher on exactly the broken mods where it matters.
            if (WigDetector.EffectiveWnamWigArmatures(
                    wnam,
                    link => _recordHandler.TryGetRecordFromMods(link, src.ModKeys, src.Folders,
                                RecordHandler.RecordLookupFallBack.Origin, out var armaRec) &&
                            armaRec is IArmorAddonGetter scopedArma
                        ? scopedArma
                        : linkCache.TryResolve<IArmorAddonGetter>(link.FormKey, out var armaWinner)
                            ? armaWinner
                            : null,
                    arma => _settings.IsWigArmature(sourceMod, arma.FormKey, arma.EditorID, donor.FormKey),
                    wigMode == WigHandlingMode.ConvertToHeadParts
                        ? null
                        : arma => arma.BodyTemplate?.FirstPersonFlags is { } flags &&
                                  (flags & BipedObjectFlag.Hair) != 0)
                .Any())
            {
                return true;
            }
        }

        if (sourceMod.DetectedWigArmors.Count == 0) return false;
        if (wigMode == WigHandlingMode.ForwardToSkin && donor.WornArmor.IsNull) return false;
        if (donor.DefaultOutfit == null || donor.DefaultOutfit.IsNull) return false;

        IOutfitGetter? outfit =
            _recordHandler.TryGetRecordFromMods(donor.DefaultOutfit, src.ModKeys, src.Folders,
                RecordHandler.RecordLookupFallBack.Origin, out var modRec) && modRec is IOutfitGetter scoped
                ? scoped
                : linkCache.TryResolve<IOutfitGetter>(donor.DefaultOutfit.FormKey, out var winner)
                    ? winner
                    : null;
        if (outfit?.Items == null) return false;

        return outfit.Items.Any(i => i != null && !i.IsNull && sourceMod.DetectedWigArmors.Contains(i.FormKey));
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
        bool subjectStandsInForNpc, // subject renders on npcFk alone — false once a Traits template is in the seat
        bool donorInheritsFace,     // donor carries Traits: owning no mesh is expected, not a finding
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
            // Shared by the consistency check's fidelity and the step-1 comparison below.
            bool deployedMatchesSource = false;
            if (subjectExists && sourcePath != null)
            {
                using (ContextualPerformanceTracer.Trace("FaceGenFilesEqual"))
                    deployedMatchesSource = FilesEqual(subjectPath, sourcePath);
            }

            // ...unless the last run rewrote this very file on purpose (hair/antler strip, wig
            // bake, head-part shape rename). Then it CANNOT be byte-identical to the mod's copy,
            // and requiring that would report every deliberately-edited mesh as a lost conflict.
            // Delivery is still ours and still faithful, so both consumers treat it as a match.
            bool weEditedIt = subjectExists && run.EditedFaceGen.Contains(targetMeshRel);
            bool deployedIsOurs = deployedMatchesSource || weEditedIt;

            // Independent of the source-matching below: does the deployed FaceGen's baked
            // geometry actually line up with the head parts this NPC resolves to in the
            // live load order? A mismatch (wrong plugin version, missing master, a null or
            // swapped head part, or a mod author shipping a .nif that doesn't match its
            // plugin) is the classic cause of the in-game dark-face bug — and neither the
            // renderer nor the patcher surfaces it. Only meaningful when a loose FaceGen is
            // actually deployed to Data (the BSA-provided case is handled by the order
            // checks below; extending the consistency scan to it is future work).
            //
            // When the deployed mesh is byte-identical to the selected mod's own file, delivery
            // is proven faithful and the row's remedies must stop pointing at load-order
            // conflicts — the mismatch is inherent to the data the selection supplies. Which
            // flavour depends on where that data came from: the base game's own files (the
            // synthetic auto-generated entries), a FaceGen-only selection paired with the
            // origin's record by design, or the mod's own record + mesh disagreeing.
            if (subjectExists)
            {
                var fidelity = FaceGenConsistencyAnalyzer.DeliveryFidelity.Unknown;
                if (deployedIsOurs)
                {
                    bool faceGenOnly = modSetting.IsFaceGenOnlyEntry ||
                                       modSetting.FaceGenOnlyNpcFormKeys.Contains(donorFk);
                    fidelity = modSetting.IsAutoGenerated
                        ? FaceGenConsistencyAnalyzer.DeliveryFidelity.VanillaOwnData
                        : faceGenOnly
                            ? FaceGenConsistencyAnalyzer.DeliveryFidelity.SelectedModMeshOnly
                            : FaceGenConsistencyAnalyzer.DeliveryFidelity.SelectedModOwnData;
                }

                using (ContextualPerformanceTracer.Trace("FaceGenConsistency"))
                    CheckFaceGenHeadPartConsistency(npcFk, subjectFk, subjectStandsInForNpc, subjectPath,
                        targetMeshRel, displayName, selectedModName, linkCache, result, fidelity);
            }

            if (!subjectExists && sourcePath == null)
            {
                // No loose deployed FaceGen and the selected mod has none (loose or BSA). Often
                // benign — vanilla FaceGen packed in a BSA, or an NPC the engine never builds a head
                // for — but it is also exactly what a genuinely faceless NPC looks like, so decide
                // which instead of returning silently as this branch used to.
                ReportMissingFaceGen(npcFk, subjectFk, displayName, selectedModName, targetMeshRel,
                    linkCache, dataFolder, run, result, log);
                return;
            }

            if (subjectExists && sourcePath == null)
            {
                // A donor that inherits its face owns no mesh BY DEFINITION — that is what the
                // Traits flag means. Saying so per NPC is noise the user cannot act on and which
                // signals nothing about how the NPC looks in game, and templated stubs come in
                // hundreds (532 rows on the reporting run). Worth an Info row for a non-templated
                // mod, which really might have shipped a mesh and did not; never for these.
                if (donorInheritsFace)
                {
                    log.AppendLine("  FACEGEN mod ships no mesh for a templated donor; expected, no row");
                    return;
                }

                // The selected mod ships no face MESH for this NPC. Since the FaceGen ladder that is
                // not a fault, it is the definition of rows 3-5: the patcher deliberately forwards
                // the mesh from the mod that originally added the NPC (or, failing that, from
                // whatever already wins that path) so the mod's tint has geometry to sit on. Calling
                // that a warning accused the patcher of its own designed behaviour on every
                // tint-only and plugin-only selection. Whether the forwarded mesh actually FITS
                // this NPC's head parts is the dark-face question, and it is answered independently
                // by CheckFaceGenHeadPartConsistency above.
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Info,
                    Check = ValidationCheckKind.Asset,
                    NpcDisplayName = displayName,
                    NpcFormKey = npcFk.ToString(),
                    SelectedMod = selectedModName,
                    Issue = $"'{selectedModName}' ships no face mesh for this NPC, so the deployed mesh was " +
                            "forwarded from elsewhere — normally the mod that originally added the NPC. " +
                            "That is the expected result for a tint-only or plugin-only mod.",
                    WinningSource = DescribeDeployedFaceGenProvider(
                        subjectFk, targetMeshRel, subjectPath, linkCache, modSetting, dataFolder, run),
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
            // (Compared once at the top of the try, where the consistency check's fidelity needed it.)
            if (deployedIsOurs)
            {
                return; // Match — the mod's own file, or our own deliberate edit of it.
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
    /// Names whatever is actually supplying the deployed FaceGen when it did not come from the
    /// selected mod: a loose byte-identical copy in another mod folder first, then the BSAs of
    /// plugins that provide this NPC. Same two-step search the mismatch path uses.
    /// </summary>
    private string DescribeDeployedFaceGenProvider(
        FormKey subjectFk,
        string targetMeshRel,
        string subjectPath,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        ModSetting selectedMod,
        string dataFolder,
        RunContext run)
    {
        var loose = FindLooseFaceGenProviders(targetMeshRel, subjectPath, selectedMod);
        if (loose.Count > 0) return string.Join("; ", loose);

        return FindBsaFaceGenCulprit(subjectFk, targetMeshRel, subjectPath, linkCache, selectedMod, dataFolder, run)
               ?? "source not identified (no byte-identical copy among your mod folders or the BSAs of plugins providing this NPC)";
    }

    /// <summary>
    /// Nothing is deployed at the subject's FaceGen path and the selected mod has nothing to deploy.
    /// Decides whether that is a defect and reports it, instead of the blanket silence this case used
    /// to get — "no FaceGen anywhere" became a defined, nameable condition with the FaceGen ladder.
    ///
    /// <para>Template-aware in two directions, because most NPCs that legitimately have no FaceGen of
    /// their own are exactly the templated ones. A subject that still carries the Traits flag is
    /// mid-chain — its face lives further down and the Template rows already explain that — and a
    /// subject whose RACE lacks <see cref="Race.Flag.FaceGenHead"/> has no built head at all (the
    /// engine's own signal; see <c>Auxilliary.IsValidAppearanceRace</c>). Neither is a finding. An NPC
    /// with no head parts is treated the same way: there is no face to be missing.</para>
    ///
    /// <para>Only when the subject genuinely should have a face does this report — and it distinguishes
    /// "some archive supplies one, just not yours" (the selection is not being delivered) from "nothing
    /// anywhere supplies one" (the head will not build). The latter is an Error in SkyPatcher mode
    /// because the surrogate owns a brand-new FormKey and nothing in the load order can fall through
    /// to its path, so there is no recovery; in record mode the NPC keeps whatever it had.</para>
    /// </summary>
    /// <summary>
    /// Whether the engine builds a FaceGen head for this record at its OWN FormID — i.e. whether
    /// "no FaceGen anywhere" is a defect for it or simply how it is built. Pure so the three
    /// exclusions can be pinned without an environment; the I/O lives in
    /// <see cref="ReportMissingFaceGen"/>.
    ///
    /// <para>Excluded: a record still carrying a usable Traits template (its face is another
    /// record's, further down the chain); a record whose RACE lacks
    /// <see cref="Race.Flag.FaceGenHead"/> (the engine's own "this actor gets a built head" signal —
    /// automatons, creatures and helmeted monsters fail it, and shipped FaceGen files are no
    /// counter-evidence because the Creation Kit auto-exports them regardless); and a record with
    /// no head parts, which has no face to be missing.</para>
    ///
    /// <para>An unresolvable race is treated as "should have one": the flag cannot be read, and
    /// staying silent on an unreadable race would hide exactly the broken-master cases this check
    /// exists to surface.</para>
    /// </summary>
    internal static bool SubjectShouldHaveOwnFaceGen(INpcGetter subject, Func<FormKey, IRaceGetter?> resolveRace)
    {
        if (Auxilliary.IsValidTemplatedNpc(subject)) return false;

        if (!subject.Race.IsNull)
        {
            var race = resolveRace(subject.Race.FormKey);
            if (race != null && !race.Flags.HasFlag(Race.Flag.FaceGenHead)) return false;
        }

        return subject.HeadParts != null && subject.HeadParts.Count > 0;
    }

    private void ReportMissingFaceGen(
        FormKey npcFk,
        FormKey subjectFk,
        string displayName,
        string selectedModName,
        string targetMeshRel,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        string dataFolder,
        RunContext run,
        ValidationRunResult result,
        StringBuilder log)
    {
        if (!linkCache.TryResolve<INpcGetter>(subjectFk, out var subjectRecord) || subjectRecord == null)
        {
            return; // Unresolvable subject is CheckRecord's business, not this check's.
        }

        if (!SubjectShouldHaveOwnFaceGen(subjectRecord,
                fk => linkCache.TryResolve<IRaceGetter>(fk, out var r) ? r : null))
        {
            return;
        }

        var candidates = ResolveBsaFaceGenCandidates(subjectFk, targetMeshRel, linkCache, dataFolder, run);
        try
        {
            if (candidates.Count > 0)
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Check = ValidationCheckKind.Asset,
                    NpcDisplayName = displayName,
                    NpcFormKey = npcFk.ToString(),
                    SelectedMod = selectedModName,
                    Issue = $"'{selectedModName}' provides no FaceGen for this NPC (no loose file and none in " +
                            "its BSAs), and none is deployed loose either, so the game will build this face from " +
                            "an archive instead of from your selection.",
                    WinningSource = DescribeBsaCandidates(candidates),
                    Details = targetMeshRel,
                });
                log.AppendLine($"  FACEGEN missing from selection; BSA fallback: {DescribeBsaCandidates(candidates)}");
                return;
            }

            result.Issues.Add(new ValidationIssue
            {
                Severity = _settings.UseSkyPatcherMode ? ValidationSeverity.Error : ValidationSeverity.Warning,
                Check = ValidationCheckKind.Asset,
                NpcDisplayName = displayName,
                NpcFormKey = npcFk.ToString(),
                SelectedMod = selectedModName,
                Issue = _settings.UseSkyPatcherMode
                    ? "No FaceGen exists for this NPC anywhere: none loose in Data, none from the selected mod, " +
                      "and no active BSA provides one. In SkyPatcher mode the appearance is carried by a new " +
                      "record of this app's own, so nothing in your load order can supply the missing face — it " +
                      "will not render correctly."
                    : "No FaceGen exists for this NPC anywhere: none loose in Data, none from the selected mod, " +
                      "and no active BSA provides one. The game has no face to build for it.",
                Details = targetMeshRel,
            });
            log.AppendLine("  FACEGEN missing everywhere (no loose, no mod source, no BSA).");
        }
        finally
        {
            foreach (var c in candidates) TryDelete(c.TempPath);
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
        FormKey npcFk, FormKey subjectFk, bool subjectStandsInForNpc, string nifPath, string relMeshPath,
        string displayName, string selectedModName, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        ValidationRunResult result,
        FaceGenConsistencyAnalyzer.DeliveryFidelity fidelity = FaceGenConsistencyAnalyzer.DeliveryFidelity.Unknown)
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

        // A character-creation preset never renders as an actor: it has no placed references, and
        // the race menu builds preset faces from the record's morph data, not from FaceGen files.
        // A mismatch here is real on disk but has no visible effect, so warning about the
        // dark-face bug (78 rows on the reporting run — WICO ships FaceGen for every vanilla
        // preset) would send the user chasing a defect the game cannot show. Downgraded only when
        // the subject stands in for the row's own NPC: a preset serving as another NPC's Traits
        // template DOES get its FaceGen rendered — on the inheritor — and keeps the full warning.
        //
        // The flag is read off the ROW'S NPC, not the subject. "Never spawns in the world" is a
        // property of the NPC the user selected for, and in SkyPatcher mode the subject is a
        // surrogate this app minted — testing subjectFk == npcFk there can never hold, which
        // silently killed the downgrade for every SkyPatcher run (all 78 came back as warnings).
        if (subjectStandsInForNpc &&
            linkCache.TryResolve<INpcGetter>(npcFk, out var rowNpc) &&
            rowNpc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.IsCharGenFacePreset))
        {
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Info,
                Check = ValidationCheckKind.FaceGen,
                NpcDisplayName = displayName,
                NpcFormKey = npcFk.ToString(),
                SelectedMod = selectedModName,
                Issue = "This NPC is a character-creation preset (the 'Is CharGen Face Preset' record flag): " +
                        "it never spawns in the world, and the race menu builds preset faces from the record's " +
                        "morph data rather than from FaceGen files. Its deployed FaceGen does not match its " +
                        "record, but the mismatch has no visible effect in game.",
                Details = relMeshPath,
            });
            return;
        }

        // Name the plugin whose NPC record is currently winning: the message's first remedy is
        // about the record conflict, and this is the single fact that tells the user whether the
        // appearance mod won it. Best-effort — a missing winner just leaves the column blank.
        var winnerModKey = linkCache.ResolveAllContexts<INpc, INpcGetter>(subjectFk).FirstOrDefault()?.ModKey;

        // The faithful-delivery remedies assert "NPC2's output record is winning". The caller
        // proved the mesh half (deployed == the selected mod's file); the record half is proven
        // here. If some other plugin wins the subject after all, the conflict-first remedies are
        // the right ones — drop the claim.
        if (fidelity != FaceGenConsistencyAnalyzer.DeliveryFidelity.Unknown &&
            !(winnerModKey.HasValue && winnerModKey.Value.FileName.String.Equals(
                _environmentStateProvider.OutputPluginFileName, StringComparison.OrdinalIgnoreCase)))
        {
            fidelity = FaceGenConsistencyAnalyzer.DeliveryFidelity.Unknown;
        }

        result.Issues.Add(new ValidationIssue
        {
            Severity = ValidationSeverity.Warning,
            Check = ValidationCheckKind.FaceGen,
            NpcDisplayName = displayName,
            NpcFormKey = npcFk.ToString(),
            SelectedMod = selectedModName,
            Issue = analysis.BuildReason(fidelity: fidelity),
            WinningSource = winnerModKey.HasValue
                ? $"NPC record from '{winnerModKey.Value.FileName}'"
                : string.Empty,
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

        /// <summary>Output-relative FaceGen paths the last run rewrote in place after copying
        /// (hair/antler strip, wig bake, head-part shape rename). Such a file is intentionally no
        /// longer byte-identical to the appearance mod's copy, so the byte comparison alone would
        /// read this app's own edit as a lost conflict. Empty when the ledger is absent.</summary>
        public HashSet<string> EditedFaceGen = new(StringComparer.OrdinalIgnoreCase);
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

        /// <summary>The line carries <c>removeTemplateFlags=traits</c>, so SkyPatcher clears the
        /// recipient's Traits bit at load and the record's own (still-templated) plugin state is
        /// NOT what the game renders. See <see cref="Patcher.ApplySkyPatcherDirectives"/>.</summary>
        public bool ClearsTraitsTemplate;

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
            // has no comma, so a simple per-segment scan is safe). SkyPatcherInterface.WriteIni
            // joins actions with ',' and emits removeTemplateFlags with the single value "traits",
            // so that directive is always its own segment here.
            FormKey surrogate = default;
            bool hasSurrogate = false;
            bool clearsTraits = false;
            foreach (var seg in line.Substring(colon + 1).Split(','))
            {
                var trimmed = seg.Trim();
                if (!hasSurrogate && trimmed.StartsWith("copyVisualStyle=", StringComparison.OrdinalIgnoreCase))
                {
                    hasSurrogate = TryParseSkyPatcherFormKey(trimmed.Substring("copyVisualStyle=".Length).Trim(), out surrogate);
                }
                else if (trimmed.StartsWith("removeTemplateFlags=", StringComparison.OrdinalIgnoreCase))
                {
                    // SkyPatcher lower-cases each comma-separated value before matching it against
                    // its flag map (npc.cpp), so match the same way.
                    clearsTraits |= trimmed.Substring("removeTemplateFlags=".Length)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(v => v.Equals("traits", StringComparison.OrdinalIgnoreCase));
                }
            }

            var entry = new Npc2SkyPatcherLine
            {
                Surrogate = surrogate,
                HasSurrogate = hasSurrogate,
                ClearsTraitsTemplate = clearsTraits,
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

    /// <summary>
    /// Appends a phase-timing line to the validation log only when performance logging is on
    /// (the Run tab's "Performance Logging" checkbox, <see cref="Settings.LogPerformance"/>).
    /// The conditional "[perf] SLOW ..." lines are deliberately NOT routed through here — those
    /// fire only on an anomaly and read as warnings rather than as routine perf reporting.
    /// </summary>
    private void AppendPerfLine(StringBuilder log, string message)
    {
        if (_settings.LogPerformance) log.AppendLine(message);
    }

    private static void WriteLog(StringBuilder log, ValidationRunResult result)
    {
        if (result.Blocked) log.AppendLine("BLOCKED: " + result.BlockReason);
        try
        {
            File.WriteAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ValidationLog.html"),
                RenderValidationHtml(log));
        }
        catch
        {
            // Logging is best-effort.
        }
    }

    /// <summary>
    /// Renders the run's accumulated log lines as a ValidationLog.html document. The line
    /// conventions the validator has always used drive the structure: an unindented
    /// "NPC ..." line opens a collapsible per-NPC section, the two-space-indented lines
    /// beneath it are that NPC's findings (badged on the section header), "[perf]" lines are
    /// muted, "[perf] SLOW" and per-NPC findings read as warnings, and "BLOCKED:"/"EXCEPTION"
    /// lines as errors. Every line of the original text is preserved verbatim.
    /// </summary>
    private static string RenderValidationHtml(StringBuilder log)
    {
        var lines = log.ToString().Replace("\r\n", "\n").Split('\n').ToList();
        // Drop the trailing empty entry the final AppendLine leaves behind.
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        // The banner and the two run parameters are the first lines Validate appends; lift
        // them into the document metadata before the prologue is rendered.
        var meta = new List<KeyValuePair<string, string>>
        {
            new("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
        };
        while (lines.Count > 0)
        {
            string head = lines[0];
            if (head == "=== Validate Output ===") { lines.RemoveAt(0); continue; }
            if (head.StartsWith("Mode: ", StringComparison.Ordinal))
            {
                meta.Add(new("Mode", head["Mode: ".Length..]));
                lines.RemoveAt(0);
                continue;
            }
            if (head.StartsWith("NPCs requested: ", StringComparison.Ordinal))
            {
                meta.Add(new("NPCs requested", head["NPCs requested: ".Length..]));
                lines.RemoveAt(0);
                continue;
            }
            break;
        }
        var doc = new HtmlLogDocument("NPC2 — Validate Output", meta);

        // Findings are buffered per NPC so the section header can carry a count badge.
        string? npcHeading = null;
        var npcRows = new List<(HtmlLogSeverity Severity, string Text)>();

        void FlushNpc()
        {
            if (npcHeading == null) return;
            int flagged = npcRows.Count(r =>
                r.Severity is HtmlLogSeverity.Warning or HtmlLogSeverity.Error);
            var badgeSeverity = npcRows.Any(r => r.Severity == HtmlLogSeverity.Error)
                ? HtmlLogSeverity.Error
                : HtmlLogSeverity.Warning;
            var (title, headingFields) = DecomposeNpcHeading(npcHeading);
            doc.BeginSection(title,
                badge: flagged > 0 ? flagged.ToString() : null, badgeSeverity: badgeSeverity);
            if (headingFields != null)
            {
                doc.AddRow(HtmlLogSeverity.Muted, string.Empty, fields: headingFields);
            }
            foreach (var (severity, text) in npcRows)
            {
                if (text.Length == 0)
                {
                    doc.AddSpacer();
                    continue;
                }
                var (message, chip, fields) = DecomposeFinding(text);
                doc.AddRow(severity, message, chip: chip, fields: fields);
            }
            doc.EndSection();
            npcHeading = null;
            npcRows.Clear();
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("NPC ", StringComparison.Ordinal))
            {
                FlushNpc();
                npcHeading = line;
                continue;
            }

            if (npcHeading != null && (line.Length == 0 || line.StartsWith("  ", StringComparison.Ordinal)))
            {
                string finding = line.TrimStart();
                var severity =
                    finding.StartsWith("EXCEPTION", StringComparison.Ordinal) ? HtmlLogSeverity.Error :
                    finding.StartsWith("[perf] SLOW", StringComparison.Ordinal) ? HtmlLogSeverity.Warning :
                    finding.StartsWith("[perf]", StringComparison.Ordinal) ? HtmlLogSeverity.Muted :
                    finding.StartsWith("TEMPLATE", StringComparison.Ordinal) ? HtmlLogSeverity.Info :
                    HtmlLogSeverity.Warning;
                npcRows.Add((severity, finding));
                continue;
            }

            FlushNpc();
            if (line.Length == 0)
            {
                doc.AddSpacer();
                continue;
            }
            var topSeverity =
                line.StartsWith("BLOCKED:", StringComparison.Ordinal) ? HtmlLogSeverity.Error :
                line.StartsWith("Done.", StringComparison.Ordinal) ? HtmlLogSeverity.Success :
                line.StartsWith("[perf] SLOW", StringComparison.Ordinal) ? HtmlLogSeverity.Warning :
                line.StartsWith("[perf]", StringComparison.Ordinal) ? HtmlLogSeverity.Muted :
                HtmlLogSeverity.Info;
            var (topMessage, topChip, topFields) = DecomposeFinding(line);
            doc.AddRow(topSeverity, topMessage, chip: topChip, fields: topFields);
        }

        FlushNpc();
        return doc.Render();
    }

    /// <summary>
    /// Splits the per-NPC heading line
    /// ("NPC {display} -> '{mod}' (donor {fk}, winner {file})") into a section title (the NPC's
    /// identity) and a fact row naming the selection, donor, and winner. Falls back to the whole
    /// line as the title when the shape doesn't match.
    /// </summary>
    private static (string Title, List<KeyValuePair<string, string>>? Fields) DecomposeNpcHeading(string heading)
    {
        int arrow = heading.LastIndexOf(" -> '", StringComparison.Ordinal);
        if (arrow <= 0) return (heading, null);
        string title = heading[..arrow];
        string rest = heading[(arrow + " -> '".Length)..];

        int donorSep = rest.LastIndexOf("' (donor ", StringComparison.Ordinal);
        if (donorSep <= 0 || !rest.EndsWith(")", StringComparison.Ordinal)) return (heading, null);
        string mod = rest[..donorSep];
        string tail = rest[(donorSep + "' (donor ".Length)..^1];

        int winnerSep = tail.LastIndexOf(", winner ", StringComparison.Ordinal);
        if (winnerSep <= 0) return (heading, null);

        return (title, new List<KeyValuePair<string, string>>
        {
            new("selected", mod),
            new("donor", tail[..winnerSep]),
            new("winner", tail[(winnerSep + ", winner ".Length)..]),
        });
    }

    /// <summary>
    /// Surfaces the latent structure of one finding line: a leading all-caps check name
    /// (RECORD / FACEGEN / TEMPLATE / SKYPATCHER / EXCEPTION / BLOCKED) or "[perf]" becomes the
    /// row's chip, trailing "; key=value" clauses and a trailing "(k=v, ...)" group become
    /// labeled field chips, and a trailing "(a | b | c)" diff list becomes value-only chips.
    /// Everything not recognized stays in the message verbatim.
    /// </summary>
    private static (string Message, string? Chip, List<KeyValuePair<string, string>>? Fields)
        DecomposeFinding(string finding)
    {
        string? chip = null;
        string msg = finding;

        if (msg.StartsWith("[perf] ", StringComparison.Ordinal))
        {
            chip = "perf";
            msg = msg["[perf] ".Length..];
        }
        else
        {
            int caps = 0;
            while (caps < msg.Length && msg[caps] >= 'A' && msg[caps] <= 'Z') caps++;
            if (caps >= 2 && (caps == msg.Length || msg[caps] == ' ' || msg[caps] == ':'))
            {
                chip = msg[..caps];
                msg = msg[caps..].TrimStart(':').TrimStart();
            }
        }

        var fields = new List<KeyValuePair<string, string>>();

        // Trailing "; key=value" clauses, peeled right to left.
        while (true)
        {
            int sep = msg.LastIndexOf("; ", StringComparison.Ordinal);
            if (sep < 0) break;
            string clause = msg[(sep + 2)..].TrimEnd('.');
            int eq = clause.IndexOf('=');
            if (eq <= 0) break;
            string key = clause[..eq];
            if (key.Contains(' ') || key.Contains(':')) break;
            fields.Insert(0, new KeyValuePair<string, string>(key, clause[(eq + 1)..]));
            msg = msg[..sep];
        }

        // Trailing parenthesized group: "(k=v, ...)" → labeled chips; "(a | b)" → a diff list.
        if (msg.EndsWith(")", StringComparison.Ordinal))
        {
            int open = msg.LastIndexOf('(');
            if (open > 0)
            {
                string content = msg[(open + 1)..^1];
                if (HtmlLog.TryParseFieldList(content, out var parsed))
                {
                    fields.InsertRange(0, parsed);
                    msg = msg[..open].TrimEnd();
                }
                else if (content.Contains(" | ", StringComparison.Ordinal))
                {
                    fields.InsertRange(0, content.Split(" | ")
                        .Select(d => new KeyValuePair<string, string>(string.Empty, d.Trim())));
                    msg = msg[..open].TrimEnd();
                }
            }
        }

        return (msg, chip, fields.Count > 0 ? fields : null);
    }
}
