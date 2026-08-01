using System.IO;
using System.Text;
using System.Windows;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class Validator : OptionalUIModule
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Settings _settings;
    private readonly AssetHandler _assetHandler;
    private readonly PluginProvider _pluginProvider;
    private readonly RecordHandler _recordHandler;

    private Dictionary<FormKey, ScreeningResult> _screeningCache = new();
    private Dictionary<ModKey, HashSet<ModKey>> _masterPluginCache = new();

    /// <summary>Per mod entry (by DisplayName), the plugins the output could not legally
    /// reference — see <see cref="GetUnsatisfiablePlugins"/>. Same lifetime as
    /// <see cref="_masterPluginCache"/>: valid for one screening pass, cleared after it.</summary>
    private Dictionary<string, HashSet<ModKey>> _unsatisfiablePluginCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One rejected selection, kept in parts (who / from which mod / why) so the confirmation
    /// dialog can group hundreds of rejections by reason and by mod instead of repeating an
    /// identical explanation on every line. <see cref="ToLine"/> is the flat form used by the run
    /// log and by callers that only want a human-readable string.
    /// </summary>
    public record InvalidSelection(string NpcDescription, string ModName, string Reason)
    {
        public string ToLine() => $"{NpcDescription} -> '{ModName}' ({Reason})";
    }

    /// <param name="InvalidSelections">Flat one-line form of each rejection, in screening order.</param>
    /// <param name="Entries">The same rejections in parts; null only for reports built by callers
    /// that predate the structured form.</param>
    public record ValidationReport(List<string> InvalidSelections, List<InvalidSelection>? Entries = null)
    {
        public IReadOnlyList<InvalidSelection> DetailedSelections => Entries ?? (IReadOnlyList<InvalidSelection>)Array.Empty<InvalidSelection>();
    }

    /// <summary>
    /// Renders rejected selections grouped by reason, and within each reason by the mod the
    /// appearance was chosen from. A load order can produce hundreds of rejections that share one
    /// explanation, so the explanation is printed once per group rather than once per NPC.
    /// Pure — no state, no logging — so it can be tested directly.
    /// </summary>
    public static string FormatInvalidSelectionsReport(IEnumerable<InvalidSelection> entries)
    {
        var sb = new StringBuilder();

        // GroupBy preserves first-appearance order of keys and the original order within each
        // group, so the report reads in the same order as the run log.
        foreach (var reasonGroup in entries.GroupBy(e => e.Reason, StringComparer.Ordinal))
        {
            var count = reasonGroup.Count();
            sb.AppendLine($"{reasonGroup.Key} ({count} selection{(count == 1 ? string.Empty : "s")}):");
            foreach (var modGroup in reasonGroup.GroupBy(e => e.ModName, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- {modGroup.Key}");
                foreach (var entry in modGroup)
                {
                    sb.AppendLine($"-- {entry.NpcDescription}");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // Constructor updated to include AssetHandler for optimized directory checks.
    public Validator(EnvironmentStateProvider environmentStateProvider, Settings settings, AssetHandler assetHandler,
        PluginProvider pluginProvider, RecordHandler recordHandler)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
        _assetHandler = assetHandler;
        _pluginProvider = pluginProvider;
        _recordHandler = recordHandler;
    }

    public Dictionary<FormKey, ScreeningResult> GetScreeningCache()
    {
        return _screeningCache;
    }

    public async Task<ValidationReport> ScreenSelectionsAsync(Dictionary<string, ModSetting> modSettingsMap,
        string selectedNpcGroup, CancellationToken ct)
    {
        ContextualPerformanceTracer.Reset();
        AppendLog("\nStarting pre-run screening of NPC selections...", false, false);
        _screeningCache = new Dictionary<FormKey, ScreeningResult>();
        var invalidSelections = new List<string>();
        var invalidEntries = new List<InvalidSelection>();

        // Single place a rejection is recorded, so the flat log line and the grouped dialog form
        // can never drift apart.
        void Reject(string npcDescription, string modName, string reason)
        {
            var entry = new InvalidSelection(npcDescription, modName, reason);
            invalidEntries.Add(entry);
            invalidSelections.Add(entry.ToLine());
        }

        var selections = _settings.SelectedAppearanceMods;

        if (selections == null || !selections.Any())
        {
            AppendLog("No selections to screen.");
            // Return an empty report if there's nothing to do.
            return new ValidationReport(new List<string>(), new List<InvalidSelection>());
        }

        IReadOnlyDictionary<FormKey, (string ModName, FormKey AppearanceNpcFormKey)> selectionsToScreen;
        if (selectedNpcGroup != "<All NPCs>")
        {
            AppendLog($"Screening selections for group: '{selectedNpcGroup}'");
            var npcsInGroup = _settings.NpcGroupAssignments
                .Where(kvp => kvp.Value != null && kvp.Value.Contains(selectedNpcGroup, StringComparer.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToHashSet();

            selectionsToScreen = selections
                .Where(kvp => npcsInGroup.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        
            if (!selectionsToScreen.Any())
            {
                AppendLog($"No selections found for the group '{selectedNpcGroup}'.");
                return new ValidationReport(new List<string>(), new List<InvalidSelection>());
            }
        }
        else
        {
            selectionsToScreen = selections;
        }

        var selectionsList = selectionsToScreen.ToList();
        int totalToScreen = selectionsList.Count;
        // The SkyPatcher/templated-NPC limitation is explained once, not once per NPC — a load order
        // can have hundreds of templated selections and the reason is identical for all of them.
        bool explainedSkyPatcherTemplateLimit = false;
        // Likewise for donors that reference a plugin the output cannot point at — one mod entry can
        // produce hundreds of these and the remedy is identical for all of them.
        bool explainedUnreferenceablePluginLimit = false;
        INpcGetter? winningNpcOverride = null;
        ModSetting? appearanceModSetting = null;
        
        // Get the load order once to avoid repeated lookups in the loop
        var loadOrderList = _environmentStateProvider.LoadOrder?.ListedOrder.Select(x => x.ModKey).ToList() ?? new List<ModKey>();

        // Implicitly-active masters (vanilla base masters + Creation Club plugins from
        // Skyrim.ccc). Skyrim loads these regardless of plugins.txt, so a plugin
        // declaring them as masters is valid even if Mutagen's load-order discovery
        // didn't surface them (e.g. non-standard install paths where Skyrim.ccc isn't
        // found by registry-based lookup). BaseGamePlugins is a fresh-allocating getter,
        // so snapshot it once outside the screening loop.
        var implicitMasters = new HashSet<ModKey>(_environmentStateProvider.BaseGamePlugins);
        implicitMasters.UnionWith(_environmentStateProvider.CreationClubPlugins);

        // Same cross-mod index the patcher builds, so screening judges a missing master by the
        // rule that will actually be applied to it (see the master check below).
        var npcProvidingOwnersByPlugin = MergeEligibility.BuildNpcProvidingOwnerIndex(_settings.ModSettings);

        for (int i = 0; i < totalToScreen; i++)
        {
            ct.ThrowIfCancellationRequested();

            KeyValuePair<FormKey, (string ModName, FormKey AppearanceNpcFormKey)> kvp = selectionsList[i];
            var npcFormKey = kvp.Key;
            var selectedModDisplayName = kvp.Value.ModName;
            var appearanceNpcFormKey = kvp.Value.AppearanceNpcFormKey;
            string npcIdentifier = npcFormKey.ToString();

            // Route this NPC's screening trace to its per-NPC diagnostic file
            // (no-op unless the user added this NPC to the logging list).
            NpcDiagnosticLogger.BeginNpc(npcFormKey);
            NpcDiagnosticLogger.LogSection("VALIDATION (pre-patch screening)");

            bool shouldUpdateUI = (i % 100 == 0) || (i == totalToScreen - 1);

            using (ContextualPerformanceTracer.Trace("Validator.ResolveNpcOverride"))
            {
                if (!_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey, out winningNpcOverride))
                {
                    var errorMsg =
                        $"Could not resolve winning NPC override for {npcFormKey}. The NPC may not exist in your current load order. This selection will be skipped.";
                    // forceLog: a screening warning means the selection is being dropped, which
                    // the user has to see whether or not verbose logging is on.
                    AppendLog($"  SCREENING WARNING: {errorMsg}", forceLog: true);
                    Reject(npcFormKey.ToString(), selectedModDisplayName, "Base NPC not found in load order");
                    if (shouldUpdateUI)
                    {
                        UpdateProgress(i + 1, totalToScreen, $"Screening: {npcIdentifier}");
                    }

                    await Task.Delay(1, ct);
                    continue;
                }
            }

            npcIdentifier = Auxilliary.GetLogString(winningNpcOverride, _settings.LocalizationLanguage);
            
            using (ContextualPerformanceTracer.Trace("Validator.CheckFaceSwap"))
            {
                // A cross-NPC appearance swap (donor FormKey != target FormKey) is only impossible in
                // plain Create mode, which can merely forward a single plugin record. SkyPatcher mode
                // performs the swap at runtime (filterByNPCs=target : copyVisualStyle=donor), so it is
                // permitted there regardless of PatchingMode.
                if (_settings.PatchingMode != PatchingMode.CreateAndPatch && !_settings.UseSkyPatcherMode &&
                    !npcFormKey.Equals(appearanceNpcFormKey))
                {
                    var appearanceNpcIdenentifier = appearanceNpcFormKey.ToString();
                    if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(appearanceNpcFormKey,
                            out var appearanceNpcGetter) && appearanceNpcGetter != null)
                    {
                        appearanceNpcIdenentifier = Auxilliary.GetLogString(appearanceNpcGetter, _settings.LocalizationLanguage);
                    }
                    
                    var errorMsg =
                        $"Can't swap {npcIdentifier} to use {appearanceNpcIdenentifier}'s appearance in {_settings.PatchingMode} mode. Skipping.";
                    // forceLog: a screening warning means the selection is being dropped, which
                    // the user has to see whether or not verbose logging is on.
                    AppendLog($"  SCREENING WARNING: {errorMsg}", forceLog: true);
                    Reject($"{npcIdentifier} (from {appearanceNpcIdenentifier})", selectedModDisplayName,
                        $"Can't appearance swap in {_settings.PatchingMode} mode");
                    if (shouldUpdateUI)
                    {
                        UpdateProgress(i + 1, totalToScreen, $"Screening: {npcIdentifier}");
                    }

                    await Task.Delay(1, ct);
                    continue;
                }
            }

            if (shouldUpdateUI)
            {
                UpdateProgress(i + 1, totalToScreen, $"Screening: {npcIdentifier}");
            }
            
            using (ContextualPerformanceTracer.Trace("Validator.GetModSetting"))
            {
                if (!modSettingsMap.TryGetValue(selectedModDisplayName, out appearanceModSetting))
                {
                    AppendLog(
                        $"  SCREENING ERROR: Cannot find Mod '{selectedModDisplayName}' for NPC {npcIdentifier}. This selection is invalid or a placeholder.",
                        true);
                    Reject(npcIdentifier, selectedModDisplayName, "Mod not installed or doesn't contain this NPC");
                    await Task.Delay(1, ct);
                    continue;
                }
            }

            using (ContextualPerformanceTracer.Trace("Validator.CheckFolderPaths"))
            {

                if (appearanceModSetting.CorrespondingFolderPaths.Any() &&
                    !appearanceModSetting.CorrespondingFolderPaths.Any(path =>
                        _assetHandler.IsModFolderPathCached(appearanceModSetting.DisplayName, path)))
                {
                    AppendLog(
                        $"  SCREENING ERROR: For NPC {npcIdentifier}, none of the specified folders for mod '{selectedModDisplayName}' exist on disk. This selection is invalid.",
                        true);
                    Reject(npcIdentifier, selectedModDisplayName, "Mod folder not found");
                    continue;
                }
            }

            using (ContextualPerformanceTracer.Trace("Validator.CheckMasters"))
            {
                ModKey? sourcePlugin = null;
                // Determine the specific plugin providing the NPC's appearance
                bool isFaceGenOnlySelection = appearanceModSetting.IsFaceGenOnlyEntry ||
                                              appearanceModSetting.FaceGenOnlyNpcFormKeys.Contains(
                                                  appearanceNpcFormKey);
                if (isFaceGenOnlySelection)
                {
                    // No plugin in the selected mod carries this NPC's record. At patch time the
                    // appearance DONOR's origin record is resolved from the load order (LinkCache,
                    // ResolveTarget.Origin) and paired with this mod's FaceGen files — so that
                    // record must actually resolve. Catch a missing defining plugin here instead
                    // of letting the patcher silently skip the NPC mid-run.
                    if (!_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(appearanceNpcFormKey, out _,
                            ResolveTarget.Origin))
                    {
                        var errorMsg =
                            $"For NPC {npcIdentifier}, the selected mod '{selectedModDisplayName}' provides only FaceGen files for this NPC, and the record it would inherit ({appearanceNpcFormKey}) cannot be resolved from the load order (its defining plugin '{appearanceNpcFormKey.ModKey.FileName}' is missing). This selection is invalid.";
                        AppendLog($"  SCREENING ERROR: {errorMsg}", true);
                        Reject(npcIdentifier, selectedModDisplayName,
                            $"FaceGen-only selection; NPC record unresolvable - missing '{appearanceNpcFormKey.ModKey.FileName}'");
                        continue;
                    }

                    sourcePlugin = appearanceNpcFormKey.ModKey;
                }
                else if (appearanceModSetting.NpcPluginDisambiguation.TryGetValue(appearanceNpcFormKey, out var disambiguatedPlugin))
                {
                    sourcePlugin = disambiguatedPlugin;
                }
                else if (appearanceModSetting.AvailablePluginsForNpcs.TryGetValue(appearanceNpcFormKey, out var availablePlugins) && availablePlugins.Any())
                {
                    // Must match the plugin the PATCHER will use, or screening vets the wrong
                    // plugin's masters and clears a selection that then fails the save. See
                    // ResolvePatcherSourcePlugin.
                    sourcePlugin = ResolvePatcherSourcePlugin(appearanceModSetting, availablePlugins);

                    if (NpcDiagnosticLogger.IsActive && availablePlugins.Count > 1)
                    {
                        NpcDiagnosticLogger.Log(
                            $"  Master check: {availablePlugins.Count} plugin(s) in this mod carry the record " +
                            $"[{string.Join(", ", availablePlugins.Select(p => p.FileName.String))}]; the patcher would " +
                            $"use '{sourcePlugin?.FileName}', so its masters are the ones screened.");
                    }
                }

                if (sourcePlugin.HasValue && !sourcePlugin.Value.IsNull)
                {
                    HashSet<ModKey> masters;
                    // Try to get the master list from the cache first.
                    if (!_masterPluginCache.TryGetValue(sourcePlugin.Value, out masters))
                    {
                        // If not cached, call the provider and store the result in the cache.
                        masters = _pluginProvider.GetMasterPlugins(sourcePlugin.Value, appearanceModSetting.CorrespondingFolderPaths);
                        _masterPluginCache[sourcePlugin.Value] = masters;
                    }

                    // Which plugin was checked, and the verdict per master, so the per-NPC log
                    // shows the reasoning rather than a bare "screening passed".
                    if (NpcDiagnosticLogger.IsActive)
                    {
                        NpcDiagnosticLogger.Log(
                            $"  Master check: source plugin '{sourcePlugin.Value.FileName}' declares {masters.Count} master(s).");
                        foreach (var master in masters)
                        {
                            NpcDiagnosticLogger.Log(
                                $"    - {master.FileName}: {DescribeMasterVerdict(master, appearanceModSetting, loadOrderList, implicitMasters, npcProvidingOwnersByPlugin)}");
                        }
                    }

                    bool mastersAreValid = true;
                    foreach (var master in masters)
                    {
                        if (IsMasterSatisfied(master, appearanceModSetting, loadOrderList, implicitMasters,
                                npcProvidingOwnersByPlugin, out var rejectionDetail))
                        {
                            continue;
                        }

                        var errorMsg = $"For NPC {npcIdentifier}, the selected plugin '{sourcePlugin.Value.FileName}' is missing a required master: '{master.FileName}'{rejectionDetail}. This selection is invalid.";
                        AppendLog($"  SCREENING ERROR: {errorMsg}", true);
                        Reject(npcIdentifier, selectedModDisplayName, $"Missing required master: {master.FileName}");
                        mastersAreValid = false;
                        break; // A single missing master invalidates the selection.
                    }
                    if (!mastersAreValid)
                    {
                        continue; // Move to the next NPC.
                    }
                }
            }

            using (ContextualPerformanceTracer.Trace("Validator.CheckSkyPatcherTemplateChain"))
            {
                if (!CanSkyPatcherApplyAppearance(appearanceNpcFormKey, appearanceModSetting, out var terminusDetail))
                {
                    if (!explainedSkyPatcherTemplateLimit)
                    {
                        explainedSkyPatcherTemplateLimit = true;
                        AppendLog(
                            "  NOTE: SkyPatcher mode cannot apply a chosen appearance to a TEMPLATED NPC while " +
                            "Templated NPCs is set to \"Use the template's appearance\". Such an NPC has no face of " +
                            "its own — the game resolves it through the Traits chain and draws the FaceGen belonging " +
                            "to the record at the end of that chain, so the appearance selected here never reaches " +
                            "it and the NPC dark-faces. The affected selections are listed below; either skip them, " +
                            "or set Templated NPCs to \"Give each NPC its own copy\" (globally in Settings, or per " +
                            "mod in Mods) so each NPC owns its face.",
                            false, true);
                    }

                    // forceLog: a screening warning means the selection is being dropped, which
                    // the user has to see whether or not verbose logging is on.
                    AppendLog(
                        $"  SCREENING WARNING: {npcIdentifier} inherits its appearance ({terminusDetail}), and " +
                        $"SkyPatcher mode cannot redirect an inherited face. This selection will be skipped.",
                        forceLog: true);
                    Reject(npcIdentifier, selectedModDisplayName,
                        "Templated NPC — SkyPatcher can't apply an appearance through a template chain; " +
                        "set Templated NPCs to \"Give each NPC its own copy\"");
                    continue;
                }
            }

            using (ContextualPerformanceTracer.Trace("Validator.CheckWrittenLinks"))
            {
                if (!WrittenLinksAreSatisfiable(npcFormKey, appearanceNpcFormKey, appearanceModSetting,
                        loadOrderList, implicitMasters, npcProvidingOwnersByPlugin,
                        out var offendingField, out var offendingPlugin, out var linkRejectionDetail))
                {
                    if (!explainedUnreferenceablePluginLimit)
                    {
                        explainedUnreferenceablePluginLimit = true;
                        AppendLog(
                            "  NOTE: the appearances listed below come from records that reference a plugin your " +
                            "output cannot point at — it is neither enabled in your load order nor set to merge " +
                            "in. Patching them anyway produces an output plugin that either refuses to save at " +
                            "the end of the run or loads into the game with broken references, so they are " +
                            "skipped. Enabling the missing plugin, turning on 'Merge In' for it under Set " +
                            "Resource Plugins, or choosing a different appearance for these NPCs all resolve it.",
                            false, true);
                    }

                    var errorMsg =
                        $"For NPC {npcIdentifier}, the appearance chosen from '{selectedModDisplayName}' writes " +
                        $"{offendingField} pointing at plugin '{offendingPlugin}'{linkRejectionDetail}. The output " +
                        "plugin cannot reference it. This selection is invalid.";
                    AppendLog($"  SCREENING ERROR: {errorMsg}", true);
                    Reject(npcIdentifier, selectedModDisplayName,
                        $"Appearance references missing plugin: {offendingPlugin}");
                    continue;
                }
            }

            _screeningCache[npcFormKey] = new ScreeningResult(
                true,
                winningNpcOverride,
                appearanceModSetting,
                appearanceNpcFormKey
            );

            NpcDiagnosticLogger.Log($"Screening passed for '{npcIdentifier}' -> mod '{selectedModDisplayName}' (appearance source {appearanceNpcFormKey}).");

            /*
             * Task.Delay(1) does not pause for exactly one millisecond. It pauses for at least one millisecond, but the actual duration is limited by the OS timer resolution.
             * On Windows, the default timer resolution is typically ~15.6 milliseconds. This means any delay request shorter than that gets rounded up to the next "tick" of the system clock.
             * Therefore, add a reasonable polling interval for the delay. It doesn't need to be responsive down to 15 ms.
             */
            if (i % 100 == 0)
            {
                await Task.Delay(1, ct);
            }
        }

        NpcDiagnosticLogger.EndNpc();

        _masterPluginCache.Clear();
        _unsatisfiablePluginCache.Clear();

        UpdateProgress(totalToScreen, totalToScreen, "Screening Complete.");
        AppendLog($"Screening finished. Found {invalidSelections.Count} invalid selections.");

        ct.ThrowIfCancellationRequested();
        
        // Keep the performance report calls commented out here in case this ever needs to be revisited
        //var perfReport = ContextualPerformanceTracer.GenerateValidationReport();
        //AppendLog(perfReport, true, true);

        // The logic for showing the popup is removed from this class.
        // We now simply return the list of invalid selections.
        return new ValidationReport(invalidSelections, invalidEntries);
    }

    /// <summary>
    /// Whether SkyPatcher can actually deliver the chosen appearance to this NPC.
    ///
    /// <para>It cannot, for an NPC that inherits its appearance through a Traits template chain, while
    /// Templated NPCs is set to <see cref="TemplateHandlingMode.InheritFromTemplate"/>. Such an NPC has
    /// no face of its own: the game resolves the chain natively and draws the FaceGen belonging to the
    /// record at the END of it. NPC2's surrogate keeps the Traits flag in this mode, so the FaceGen it
    /// writes under the surrogate's own FormID is never opened, and the NPC renders the terminus's
    /// face instead — which, once the appearance plugin has been merged away, is a mod's mesh judged
    /// against a vanilla record. That mismatch is the dark-face bug.</para>
    ///
    /// <para>Selecting an appearance for the terminus as well does NOT rescue it: SkyPatcher patches
    /// the terminus through its own surrogate and redirects only the terminus's own actor, while
    /// inheritors read the terminus's FaceGen path. So this is a per-NPC hard no, and the remedy is
    /// <see cref="TemplateHandlingMode.GiveEachNpcOwnCopy"/>, which clears the Traits flag and gives
    /// the NPC its own face.</para>
    ///
    /// <para>Only a chain that resolves to a concrete NPC is rejected. A levelled terminus is normal —
    /// the game picks an actor at runtime and there is no fixed face to redirect — and an unfollowable
    /// chain is handled by the FaceGen ladder. Record mode is unaffected: there the patched record
    /// keeps inheriting and the engine reads its head parts and its mesh from the same place.</para>
    /// </summary>
    /// <param name="terminusDetail">Human-readable chain outcome, for the per-NPC log.</param>
    private bool CanSkyPatcherApplyAppearance(FormKey appearanceNpcFormKey, ModSetting appearanceModSetting,
        out string terminusDetail)
    {
        terminusDetail = string.Empty;

        if (!_settings.UseSkyPatcherMode) return true;
        if (_settings.GetEffectiveTemplateHandlingMode(appearanceModSetting) !=
            TemplateHandlingMode.InheritFromTemplate)
        {
            return true;
        }

        // Resolved through the selected mod's own plugins, exactly as the patcher resolves the donor
        // and every chain hop — a mod may set or clear the Traits flag, and screening has to judge the
        // record that will actually be patched. Reached only in SkyPatcher + Inherit, so record-mode
        // runs never pay for the lookup.
        bool isFaceGenOnly = appearanceModSetting.IsFaceGenOnlyEntry ||
                             appearanceModSetting.FaceGenOnlyNpcFormKeys.Contains(appearanceNpcFormKey);
        var folderPaths = appearanceModSetting.CorrespondingFolderPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var donor = _recordHandler.ResolveNpcPreferringMod(appearanceNpcFormKey, appearanceModSetting,
            folderPaths, isFaceGenOnly);
        if (donor == null) return true; // unresolvable donor is another check's business

        var linkCache = _environmentStateProvider.LinkCache;
        var hops = new List<string>();
        var status = Auxilliary.TryResolveAppearanceTerminus(
            donor,
            fk => _recordHandler.ResolveNpcPreferringMod(fk, appearanceModSetting, folderPaths, isFaceGenOnly),
            out var terminus,
            fk => linkCache != null && linkCache.TryResolve<ILeveledNpcGetter>(fk, out _),
            hops.Add);

        if (status != FaceGenChainStatus.Resolved) return true;

        // The hop strings already carry their own arrows, so they are joined with a space rather than
        // an arrow — otherwise the trail reads "-> A -> -> B".
        terminusDetail = $"chain {donor.FormKey} {string.Join(" ", hops)}".TrimEnd();
        return false;
    }

    /// <summary>
    /// Whether every link this selection will WRITE onto the output record can legally be
    /// referenced from the output plugin.
    ///
    /// <para><b>Why the declared-master check is not enough.</b> <see cref="IsMasterSatisfied"/>
    /// vets the masters the source plugin DECLARES. A plugin's references to its OWN records
    /// declare no master at all — but once such a record is copied into the output, that
    /// self-reference becomes a reference to the source plugin, which then has to be a master of
    /// the output. If that plugin is neither in the load order nor merged in, Mutagen refuses to
    /// write the plugin at the very end of the run ("A referenced mod was not present on the load
    /// order being sorted against"), throwing away the whole run's work for a condition that was
    /// knowable before it started.</para>
    ///
    /// <para><b>Scope differs by mode.</b> Record mode writes only the appearance fields onto an
    /// override of the recipient's WINNING record, so only those are screened. SkyPatcher mode
    /// copies the donor wholesale into a surrogate, so the whole record is screened — see the note
    /// on the sweep below for why repairing individual fields is not viable there.</para>
    ///
    /// <para><b>Cost.</b> Gated on the mod having at least one plugin that is neither in the load
    /// order nor merge-eligible — for an ordinary mod that set is empty and this returns
    /// immediately, without resolving anything.</para>
    /// </summary>
    /// <param name="offendingField">Record field holding the first bad link, e.g. "Template" or
    /// "HeadParts[2]", so the rejection names something the user can act on.</param>
    private bool WrittenLinksAreSatisfiable(FormKey npcFormKey, FormKey appearanceNpcFormKey,
        ModSetting appearanceModSetting, List<ModKey> loadOrderList, HashSet<ModKey> implicitMasters,
        IReadOnlyDictionary<ModKey, ModSetting> npcProvidingOwnersByPlugin,
        out string offendingField, out string offendingPlugin, out string rejectionDetail)
    {
        offendingField = string.Empty;
        offendingPlugin = string.Empty;
        rejectionDetail = string.Empty;

        var unsatisfiable = GetUnsatisfiablePlugins(appearanceModSetting, loadOrderList, implicitMasters,
            npcProvidingOwnersByPlugin);
        if (unsatisfiable.Count == 0) return true;

        // The wig/antler pipeline re-points WornArmor, HeadParts and DefaultOutfit at records it
        // mints in the OUTPUT, so the donor's own links are not the ones that get written and
        // screening them would reject selections that patch cleanly.
        if (_settings.WigOrAntlerHandlingActive(appearanceModSetting)) return true;

        bool isFaceGenOnly = appearanceModSetting.IsFaceGenOnlyEntry ||
                             appearanceModSetting.FaceGenOnlyNpcFormKeys.Contains(appearanceNpcFormKey);
        var folderPaths = appearanceModSetting.CorrespondingFolderPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var donor = _recordHandler.ResolveNpcPreferringMod(appearanceNpcFormKey, appearanceModSetting,
            folderPaths, isFaceGenOnly);
        if (donor == null) return true; // unresolvable donor is another check's business

        // Which record's appearance actually lands on the output: under "give each NPC its own
        // copy" the patcher overlays the TERMINUS's fields (Auxilliary.CopyInheritedAppearance),
        // so the donor's own head parts/skin/race are overwritten and must not be screened.
        var appearanceRecord = donor;
        bool flattening = _settings.GetEffectiveTemplateHandlingMode(appearanceModSetting) ==
                          TemplateHandlingMode.GiveEachNpcOwnCopy;
        if (flattening && Auxilliary.TryResolveAppearanceTerminus(donor,
                fk => _recordHandler.ResolveNpcPreferringMod(fk, appearanceModSetting, folderPaths, isFaceGenOnly),
                out var terminusKey) == FaceGenChainStatus.Resolved &&
            !terminusKey.Equals(donor.FormKey))
        {
            appearanceRecord = _recordHandler.ResolveNpcPreferringMod(terminusKey, appearanceModSetting,
                folderPaths, isFaceGenOnly) ?? donor;
        }

        bool includeOutfit = ResolveIncludeOutfit(appearanceModSetting, npcFormKey, _settings.NpcOutfitOverrides);

        // Named fields first, so the common failures are reported as "HeadParts[2]=..." rather than
        // as a bare FormKey.
        var candidates = EnumerateWrittenLinks(appearanceRecord, donor, includeOutfit,
            _settings.UseSkyPatcherMode);

        // SkyPatcher mode screens the WHOLE donor record, not just the appearance fields it copies.
        // The surrogate is a DeepCopyIn of the donor, so every link on it lands in the output, and
        // an unsatisfiable one breaks the run in one of two ways: kept, it dangles and Mutagen
        // refuses the save; stripped (SkyPatcherInterface.StripNonAppearanceData), it can punch a
        // hole in a REQUIRED subrecord and the game stalls on launch with a null reference. Repairing
        // those field by field is whack-a-mole — one donor's Class, the next one's something else —
        // so a donor that touches a plugin the output cannot reference is rejected outright.
        //
        // Deliberately conservative in one direction: under a flatten the donor's own appearance
        // links are overwritten by the terminus's, so a foreign one is screened here even though it
        // would never have been written. Rejecting a selection that might have worked beats shipping
        // a record the engine chokes on.
        if (_settings.UseSkyPatcherMode)
        {
            candidates = candidates.Concat(donor.EnumerateFormLinks()
                .Where(l => !l.FormKey.IsNull)
                .Select(l => ("record data", l.FormKey)));
        }

        foreach (var (field, key) in candidates)
        {
            if (IsMasterSatisfied(key.ModKey, appearanceModSetting, loadOrderList, implicitMasters,
                    npcProvidingOwnersByPlugin, out var detail))
            {
                continue;
            }

            offendingField = $"{field}={key}";
            offendingPlugin = key.ModKey.FileName.ToString();
            rejectionDetail = detail;

            NpcDiagnosticLogger.Log(
                $"  Written-link check: {field} resolves to {key}, whose plugin is neither in the load " +
                $"order nor merged in{detail}.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// The links the patcher writes onto the output record for this selection. Deliberately NOT
    /// every link on the donor: the non-appearance ones are either left as the recipient's own
    /// (record mode overrides the WINNING record) or stripped from the surrogate against this same
    /// "is it in the load order" test (<c>SkyPatcherInterface.StripNonAppearanceData</c>).
    ///
    /// <para><c>Template</c> is read from the DONOR even under a flatten, because that is the record
    /// whose inheritance is mirrored. Record mode writes it only when the donor inherits its FACE —
    /// <c>Patcher.SyncTemplateInheritance</c> mirrors the TPLT only for a donor carrying the Traits
    /// flag — whereas the SkyPatcher surrogate is a <c>DeepCopyIn</c> and carries it either way.</para>
    ///
    /// <para>Pure — no state, no logging — so it can be tested directly.</para>
    /// </summary>
    private static IEnumerable<(string Field, FormKey Key)> EnumerateWrittenLinks(INpcGetter appearanceRecord,
        INpcGetter donor, bool includeOutfit, bool useSkyPatcherMode)
    {
        if (!appearanceRecord.Race.IsNull) yield return ("Race", appearanceRecord.Race.FormKey);
        if (!appearanceRecord.WornArmor.IsNull) yield return ("WornArmor(skin)", appearanceRecord.WornArmor.FormKey);
        if (!appearanceRecord.HeadTexture.IsNull) yield return ("HeadTexture", appearanceRecord.HeadTexture.FormKey);
        if (!appearanceRecord.HairColor.IsNull) yield return ("HairColor", appearanceRecord.HairColor.FormKey);

        int hpIndex = 0;
        foreach (var hp in appearanceRecord.HeadParts)
        {
            if (!hp.IsNull) yield return ($"HeadParts[{hpIndex}]", hp.FormKey);
            hpIndex++;
        }

        if (includeOutfit && !appearanceRecord.DefaultOutfit.IsNull)
        {
            yield return ("DefaultOutfit", appearanceRecord.DefaultOutfit.FormKey);
        }

        if ((useSkyPatcherMode || Auxilliary.HasTraitsFlag(donor)) && donor.Template is { IsNull: false })
        {
            yield return ("Template", donor.Template.FormKey);
        }
    }

    /// <summary>Mirrors the patcher's own outfit resolution (per-NPC override, else the mod's
    /// setting) so screening judges DefaultOutfit only when it is actually copied. Pure.</summary>
    private static bool ResolveIncludeOutfit(ModSetting appearanceModSetting, FormKey npcFormKey,
        IReadOnlyDictionary<FormKey, OutfitOverride> npcOutfitOverrides)
    {
        if (!npcOutfitOverrides.TryGetValue(npcFormKey, out var choice))
        {
            return appearanceModSetting.IncludeOutfits;
        }

        return choice switch
        {
            OutfitOverride.No => false,
            OutfitOverride.Yes => true,
            _ => appearanceModSetting.IncludeOutfits,
        };
    }

    /// <summary>
    /// The mod's plugins that the output could not legally reference: not in the load order, not
    /// implicitly active, and not merged into the output. Empty for an ordinary mod, which is what
    /// keeps <see cref="WrittenLinksAreSatisfiable"/> free for the overwhelming majority of
    /// selections. Cached per mod entry for the duration of one screening pass.
    /// </summary>
    private HashSet<ModKey> GetUnsatisfiablePlugins(ModSetting appearanceModSetting, List<ModKey> loadOrderList,
        HashSet<ModKey> implicitMasters, IReadOnlyDictionary<ModKey, ModSetting> npcProvidingOwnersByPlugin)
    {
        if (_unsatisfiablePluginCache.TryGetValue(appearanceModSetting.DisplayName, out var cached))
        {
            return cached;
        }

        var unsatisfiable = ComputeUnsatisfiablePlugins(appearanceModSetting, loadOrderList, implicitMasters,
            npcProvidingOwnersByPlugin);
        _unsatisfiablePluginCache[appearanceModSetting.DisplayName] = unsatisfiable;
        return unsatisfiable;
    }

    /// <summary>The uncached decision behind <see cref="GetUnsatisfiablePlugins"/>. Pure — no
    /// state, no logging — so it can be tested directly.</summary>
    private static HashSet<ModKey> ComputeUnsatisfiablePlugins(ModSetting appearanceModSetting,
        List<ModKey> loadOrderList, HashSet<ModKey> implicitMasters,
        IReadOnlyDictionary<ModKey, ModSetting> npcProvidingOwnersByPlugin)
    {
        var unsatisfiable = new HashSet<ModKey>();
        foreach (var plugin in appearanceModSetting.CorrespondingModKeys.Distinct())
        {
            if (loadOrderList.Contains(plugin)) continue;
            if (implicitMasters.Contains(plugin)) continue;
            if (MergeEligibility.IsPluginMergeEligible(appearanceModSetting, plugin, npcProvidingOwnersByPlugin))
            {
                continue;
            }

            unsatisfiable.Add(plugin);
        }

        return unsatisfiable;
    }

    /// <summary>
    /// The plugin the PATCHER will treat as this NPC's appearance source, so screening vets the
    /// masters of the right plugin. The patcher walks <see cref="ModSetting.CorrespondingModKeys"/>
    /// from the bottom up (lowest wins), skipping resource-only plugins, and takes the first that
    /// carries the record; screening used to take the FIRST available plugin instead, so with more
    /// than one candidate it could clear a selection whose actual source has a missing master.
    /// <paramref name="availablePlugins"/> is the record-carrying set, so intersecting the two
    /// reproduces the patcher's choice without loading any plugin.
    /// </summary>
    private static ModKey? ResolvePatcherSourcePlugin(ModSetting appearanceModSetting, List<ModKey> availablePlugins)
    {
        for (int i = appearanceModSetting.CorrespondingModKeys.Count - 1; i >= 0; i--)
        {
            var candidate = appearanceModSetting.CorrespondingModKeys[i];
            if (appearanceModSetting.ResourceOnlyModKeys.Contains(candidate)) continue;
            if (availablePlugins.Contains(candidate)) return candidate;
        }

        // No candidate is listed in CorrespondingModKeys (stale analysis data). Fall back to the
        // old behaviour rather than skipping the master check entirely.
        return availablePlugins.FirstOrDefault();
    }

    /// <summary>
    /// Whether a master declared by the appearance plugin will actually be satisfiable at write
    /// time. Beyond the load order and the implicitly-active vanilla/CC masters, a master that
    /// belongs to this same mod entry is acceptable ONLY if that plugin's records get merged into
    /// the output (<see cref="MergeEligibility"/>): merging copies them, so nothing ends up
    /// referencing the absent plugin. A non-merged sibling is NOT acceptable — its records stay as
    /// references, and Mutagen cannot write a master that isn't in the load order, which fails the
    /// entire save at the end of the run rather than just this NPC.
    /// </summary>
    private static bool IsMasterSatisfied(ModKey master, ModSetting appearanceModSetting,
        List<ModKey> loadOrderList, HashSet<ModKey> implicitMasters,
        IReadOnlyDictionary<ModKey, ModSetting> npcProvidingOwnersByPlugin, out string rejectionDetail)
    {
        rejectionDetail = string.Empty;

        if (loadOrderList.Contains(master)) return true;
        if (implicitMasters.Contains(master)) return true;

        if (appearanceModSetting.CorrespondingModKeys.Contains(master))
        {
            if (MergeEligibility.IsPluginMergeEligible(appearanceModSetting, master, npcProvidingOwnersByPlugin))
            {
                return true; // its records are copied into the output, so the master isn't needed
            }

            rejectionDetail =
                $" (it belongs to this mod entry but is not in your load order, and its records are not set to " +
                $"merge in — enable 'Merge In' for '{master.FileName}' under Set Resource Plugins, or enable the plugin)";
            return false;
        }

        return false;
    }

    /// <summary>Human-readable form of <see cref="IsMasterSatisfied"/>, for the per-NPC log.</summary>
    private static string DescribeMasterVerdict(ModKey master, ModSetting appearanceModSetting,
        List<ModKey> loadOrderList, HashSet<ModKey> implicitMasters,
        IReadOnlyDictionary<ModKey, ModSetting> npcProvidingOwnersByPlugin)
    {
        if (loadOrderList.Contains(master)) return "in load order";
        if (implicitMasters.Contains(master)) return "implicitly active (vanilla/CC)";

        if (appearanceModSetting.CorrespondingModKeys.Contains(master))
        {
            return MergeEligibility.IsPluginMergeEligible(appearanceModSetting, master, npcProvidingOwnersByPlugin)
                ? "NOT in load order, but belongs to this mod entry and its records merge in — OK"
                : "NOT in load order, belongs to this mod entry, and does NOT merge in — records referencing it " +
                  "cannot be written to the output plugin";
        }

        return "MISSING";
    }
}