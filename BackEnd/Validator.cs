using System.IO;
using System.Text;
using System.Windows;
using Mutagen.Bethesda.Plugins;
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

        for (int i = 0; i < totalToScreen; i++)
        {
            ct.ThrowIfCancellationRequested();

            KeyValuePair<FormKey, (string ModName, FormKey AppearanceNpcFormKey)> kvp = selectionsList[i];
            var npcFormKey = kvp.Key;
            var selectedModDisplayName = kvp.Value.ModName;
            var appearanceNpcFormKey = kvp.Value.AppearanceNpcFormKey;
            string npcIdentifier = npcFormKey.ToString();

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
                if (_settings.PatchingMode != PatchingMode.CreateAndPatch && !npcFormKey.Equals(appearanceNpcFormKey))
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
                if (appearanceModSetting.IsFaceGenOnlyEntry)
                {
                    sourcePlugin = npcFormKey.ModKey;
                }
                else if (appearanceModSetting.NpcPluginDisambiguation.TryGetValue(appearanceNpcFormKey, out var disambiguatedPlugin))
                {
                    sourcePlugin = disambiguatedPlugin;
                }
                else if (appearanceModSetting.AvailablePluginsForNpcs.TryGetValue(appearanceNpcFormKey, out var availablePlugins) && availablePlugins.Any())
                {
                    sourcePlugin = availablePlugins.FirstOrDefault();
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

                    bool mastersAreValid = true;
                    foreach (var master in masters)
                    {
                        // A master is valid if it's in the load order OR part of the same ModSetting group.
                        if (!loadOrderList.Contains(master) && !appearanceModSetting.CorrespondingModKeys.Contains(master))
                        {
                            var errorMsg = $"For NPC {npcIdentifier}, the selected plugin '{sourcePlugin.Value.FileName}' is missing a required master: '{master.FileName}'. This selection is invalid.";
                            AppendLog($"  SCREENING ERROR: {errorMsg}", true);
                            invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Missing required master: {master.FileName})");
                            mastersAreValid = false;
                            break; // A single missing master invalidates the selection.
                        }
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
}