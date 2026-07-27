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

    private Dictionary<FormKey, ScreeningResult> _screeningCache = new();
    private Dictionary<ModKey, HashSet<ModKey>> _masterPluginCache = new();

    public record ValidationReport(List<string> InvalidSelections);

    // Constructor updated to include AssetHandler for optimized directory checks.
    public Validator(EnvironmentStateProvider environmentStateProvider, Settings settings, AssetHandler assetHandler, PluginProvider pluginProvider)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
        _assetHandler = assetHandler;
        _pluginProvider = pluginProvider;
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
        var selections = _settings.SelectedAppearanceMods;

        if (selections == null || !selections.Any())
        {
            AppendLog("No selections to screen.");
            // Return an empty report if there's nothing to do.
            return new ValidationReport(new List<string>());
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
                return new ValidationReport(new List<string>());
            }
        }
        else
        {
            selectionsToScreen = selections;
        }

        var selectionsList = selectionsToScreen.ToList();
        int totalToScreen = selectionsList.Count;
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
                    AppendLog($"  SCREENING WARNING: {errorMsg}");
                    invalidSelections.Add(
                        $"{npcFormKey} -> '{selectedModDisplayName}' (Base NPC not found in load order)");
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
                    AppendLog($"  SCREENING WARNING: {errorMsg}");
                    invalidSelections.Add(
                        $"{npcIdentifier} -> '{selectedModDisplayName}' ({appearanceNpcIdenentifier}) - (Can't appearance swap in {_settings.PatchingMode} mode)");
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
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Mod not installed or doesn't contain this NPC)");
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
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Mod folder not found)");
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
                        invalidSelections.Add(
                            $"{npcIdentifier} -> '{selectedModDisplayName}' (FaceGen-only selection; NPC record unresolvable - missing '{appearanceNpcFormKey.ModKey.FileName}')");
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
                        invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Missing required master: {master.FileName})");
                        mastersAreValid = false;
                        break; // A single missing master invalidates the selection.
                    }
                    if (!mastersAreValid)
                    {
                        continue; // Move to the next NPC.
                    }
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

        _masterPluginCache.Clear();;

        UpdateProgress(totalToScreen, totalToScreen, "Screening Complete.");
        AppendLog($"Screening finished. Found {invalidSelections.Count} invalid selections.");

        ct.ThrowIfCancellationRequested();
        
        // Keep the performance report calls commented out here in case this ever needs to be revisited
        //var perfReport = ContextualPerformanceTracer.GenerateValidationReport();
        //AppendLog(perfReport, true, true);

        // The logic for showing the popup is removed from this class.
        // We now simply return the list of invalid selections.
        return new ValidationReport(invalidSelections);
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