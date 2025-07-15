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

    private Dictionary<FormKey, ScreeningResult> _screeningCache = new();

    public record ValidationReport(List<string> InvalidSelections);

    // Constructor updated to include AssetHandler for optimized directory checks.
    public Validator(EnvironmentStateProvider environmentStateProvider, Settings settings, AssetHandler assetHandler)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
        _assetHandler = assetHandler;
    }

    public Dictionary<FormKey, ScreeningResult> GetScreeningCache()
    {
        return _screeningCache;
    }

    public async Task<ValidationReport> ScreenSelectionsAsync(Dictionary<string, ModSetting> modSettingsMap,
        CancellationToken ct)
    {
#if DEBUG
        ContextualPerformanceTracer.Reset();
#endif
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

        var selectionsList = selections.ToList();
        int totalToScreen = selectionsList.Count;
        INpcGetter? winningNpcOverride = null;
        ModSetting? appearanceModSetting = null;

        for (int i = 0; i < totalToScreen; i++)
        {
            ct.ThrowIfCancellationRequested();

            var kvp = selectionsList[i];
            var npcFormKey = kvp.Key;
            var selectedModDisplayName = kvp.Value;
            string npcIdentifier = npcFormKey.ToString();

            bool shouldUpdateUI = (i % 100 == 0) || (i == totalToScreen - 1);

#if DEBUG
            using (ContextualPerformanceTracer.Trace("Validator.ResolveNpcOverride"))
            {
#endif
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
#if DEBUG
            }
#endif
            npcIdentifier =
                $"{winningNpcOverride.Name?.String ?? winningNpcOverride.EditorID ?? npcFormKey.ToString()} ({npcFormKey})";

            if (shouldUpdateUI)
            {
                UpdateProgress(i + 1, totalToScreen, $"Screening: {npcIdentifier}");
            }

#if DEBUG
            using (ContextualPerformanceTracer.Trace("Validator.GetModSetting"))
            {
#endif
                if (!modSettingsMap.TryGetValue(selectedModDisplayName, out appearanceModSetting))
                {
                    AppendLog(
                        $"  SCREENING ERROR: Cannot find Mod Setting '{selectedModDisplayName}' for NPC {npcIdentifier}. This selection is invalid.",
                        true);
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Mod Setting not found)");
                    await Task.Delay(1, ct);
                    continue;
                }
#if DEBUG
            }
#endif
#if DEBUG
            using (ContextualPerformanceTracer.Trace("Validator.CheckFolderPaths"))
            {
#endif

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
#if DEBUG
            }
#endif
            _screeningCache[npcFormKey] = new ScreeningResult(
                true,
                winningNpcOverride,
                appearanceModSetting
            );

            await Task.Delay(1, ct);
        }

        UpdateProgress(totalToScreen, totalToScreen, "Screening Complete.");
        AppendLog($"Screening finished. Found {invalidSelections.Count} invalid selections.");

        ct.ThrowIfCancellationRequested();
        
#if DEBUG
        var perfReport = ContextualPerformanceTracer.GenerateValidationReport();
        AppendLog(perfReport, true, true);
#endif

        // The logic for showing the popup is removed from this class.
        // We now simply return the list of invalid selections.
        return new ValidationReport(invalidSelections);
    }
}