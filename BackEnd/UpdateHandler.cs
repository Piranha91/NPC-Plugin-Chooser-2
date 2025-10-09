using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Views;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Handles migrating user settings from older versions of the application to the current version.
/// </summary>
public class UpdateHandler
{
    private readonly Settings _settings;

    public UpdateHandler(Settings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Checks the settings version against the current program version and applies any necessary updates.
    /// Runs as soon as the settings are read in
    /// </summary>
    public void InitialCheckForUpdatesAndPatch()
    {
        // If the settings version is empty (e.g., a new user), there's nothing to migrate.
        if (string.IsNullOrWhiteSpace(_settings.ProgramVersion))
        {
            Debug.WriteLine("New user or fresh settings, skipping update check.");
            return;
        }

        // Use the custom ProgramVersion class for comparison.
        // The string from settings is implicitly converted to a ProgramVersion object.
        ProgramVersion settingsVersion = _settings.ProgramVersion;
        ProgramVersion currentVersion = App.ProgramVersion;

        Debug.WriteLine($"Updating settings from version {settingsVersion} to {currentVersion}");

        // --- Update Logic ---
        // Updates should be cumulative. If a user jumps from 2.0.7 to 2.1.0,
        // all intermediate patches (for < 2.0.8, < 2.0.9, etc.) should be applied in order.
        // The `if` statements are not `else if` for this reason.

        if (settingsVersion < "2.0.4")
        {
            UpdateTo2_0_4_Initial();
        }
        
        if (!_settings.HasUpdatedTo2_0_7)
        {
            UpdateTo2_0_7_Initial();
        }

        Debug.WriteLine("Settings update process complete.");
    }
    
    // <summary>
    /// Checks the settings version against the current program version and applies any necessary updates.
    /// Runs after UI initializes
    /// </summary>
    public async Task FinalCheckForUpdatesAndPatch(VM_NpcSelectionBar npcsVm, VM_Mods modsVm, VM_SplashScreen? splashReporter)
    {
        // If the settings version is empty (e.g., a new user), there's nothing to migrate.
        if (string.IsNullOrWhiteSpace(_settings.ProgramVersion))
        {
            Debug.WriteLine("New user or fresh settings, skipping update check.");
            return;
        }

        // Use the custom ProgramVersion class for comparison.
        // The string from settings is implicitly converted to a ProgramVersion object.
        ProgramVersion settingsVersion = _settings.ProgramVersion;
        ProgramVersion currentVersion = App.ProgramVersion;

        // If the settings version is the same or newer, no action is needed.
        if (settingsVersion >= currentVersion)
        {
            //return;
        }

        splashReporter?.UpdateStep($"Updating settings from version {settingsVersion} to {currentVersion}");
        Debug.WriteLine($"Updating settings from version {settingsVersion} to {currentVersion}");

        // --- Update Logic ---
        // Updates should be cumulative. If a user jumps from 2.0.7 to 2.1.0,
        // all intermediate patches (for < 2.0.8, < 2.0.9, etc.) should be applied in order.
        // The `if` statements are not `else if` for this reason.

        if (settingsVersion < "2.0.4")
        {
            await UpdateTo2_0_4_Final(modsVm, splashReporter);
        }
        
        if (settingsVersion < "2.0.5")
        {
            await UpdateTo2_0_5_Final(modsVm, splashReporter);
        }
        
        if (!_settings.HasUpdatedTo2_0_7_templates)
        {
            await UpdateTo2_0_7_Final(modsVm, npcsVm, splashReporter);
        }

        Debug.WriteLine("Settings update process complete.");
    }

    private void UpdateTo2_0_4_Initial()
    {
        var message =
            """
            In previous versions, the "Include Outfits" option was erroneously defaulted to "Enabled". 
            Changing outfits on an existing save can be problematic because it causes NPCs with 
            modified outfits to unequip their clothes. 

            If you would like to disable this option, there is now a batch option in the Mods Menu 
            to enable/disable outfits for all mods.
            """;
        ScrollableMessageBox.Show(message, "Updating to 2.0.4");
    }
    
    private void UpdateTo2_0_7_Initial()
    {
        bool shouldReset = true;

        if (_settings.UsePortraitCreatorFallback)
        {
            var message =
                """
                The Portrait Creator has received significant updates in the 2.0.7 release. 
                It is strongly recommended to reset Portrait Creator settings to default. 

                Would you like to do so?
                """;
        
            shouldReset = ScrollableMessageBox.Confirm(message, "Portrait Creator Settings Update");
        }

        if (shouldReset)
        {
            // Reset all Portrait Creator settings to their new defaults
            _settings.MugshotBackgroundColor = Color.FromRgb(58, 61, 64);
            _settings.VerticalFOV = 25;
            _settings.HeadTopOffset = 0.0f;
            _settings.HeadBottomOffset = -0.05f;
            _settings.CamPitch = 2.0f;
            _settings.CamYaw = 90.0f;
            _settings.CamRoll = 0.0f;
            _settings.CamX = 0.0f;
            _settings.CamY = 0.0f;
            _settings.CamZ = 0.0f;
            _settings.SelectedCameraMode = PortraitCameraMode.Portrait;
        
            _settings.DefaultLightingJsonString = @"
{
    ""lights"": [
        {
            ""color"": [
                1.0,
                0.8799999952316284,
                0.699999988079071
            ],
            ""intensity"": 0.6499999761581421,
            ""type"": ""ambient""
        },
        {
            ""color"": [
                1.0,
                0.8500000238418579,
                0.6499999761581421
            ],
            ""direction"": [
                -0.0798034518957138,
                -0.99638432264328,
                -0.029152285307645798
            ],
            ""intensity"": 1.600000023841858,
            ""type"": ""directional""
        },
        {
            ""color"": [
                1.0,
                0.8700000047683716,
                0.6800000071525574
            ],
            ""direction"": [
                0.12252168357372284,
                -0.6893905401229858,
                0.7139532566070557
            ],
            ""intensity"": 0.800000011920929,
            ""type"": ""directional""
        }
    ]
}";
            _settings.EnableNormalMapHack = true;
            
            Debug.WriteLine("Portrait Creator settings reset to 1.0.7 defaults.");
        }

        // Always mark as updated, even if user declined the reset
        _settings.HasUpdatedTo2_0_7 = true;
    }

    private async Task UpdateTo2_0_4_Final(VM_Mods modsVm, VM_SplashScreen? splashReporter)
    {
        var modsToScan = modsVm.AllModSettings.Where(modVm =>
            modVm.DisplayName != VM_Mods.BaseGameModSettingName &&
            modVm.DisplayName != VM_Mods.CreationClubModsettingName &&
            !modVm.IsFaceGenOnlyEntry).ToList();

        splashReporter?.UpdateStep($"Updating to 2.0.4: Scanning mods for injected records...", modsToScan.Count);
            
        var modsWithInjectedRecords = new ConcurrentBag<VM_ModSetting>();

        // 1. Perform the expensive, IO-bound work on background threads
        await Task.Run(() =>
        {
            Parallel.ForEach(modsToScan, modVm =>
            {
                // .Result is acceptable here as we are already inside a background thread via Task.Run
                if (modVm.CheckForInjectedRecords(splashReporter == null ? null : splashReporter.ShowMessagesOnClose, _settings.LocalizationLanguage).Result)
                {
                    modsWithInjectedRecords.Add(modVm);
                }
                splashReporter?.IncrementProgress(string.Empty);
            });
        });

        // 2. Perform the UI update on the UI thread after all parallel work is complete
        foreach (var modVm in modsWithInjectedRecords)
        {
            modVm.HasAlteredHandleInjectedRecordsLogic = true;
        }
    }
    
    private async Task UpdateTo2_0_5_Final(VM_Mods modsVm, VM_SplashScreen? splashReporter)
    {
        // Call the public refresh coordinator, passing the existing splash screen reporter
        await modsVm.RefreshAllModSettingsAsync(splashReporter);
    }

    private async Task UpdateTo2_0_7_Final(VM_Mods modsVm, VM_NpcSelectionBar npcSelectionBar,
        VM_SplashScreen? splashReporter)
    {
        string messageStr = "Previous versions of NPC Plugin Chooser allowed you to select appearances for NPCs with invalid templates using the Select All From Mod batch action. This could result in bugged appearances in-game for those NPCs. Would you like to scan and automatically de-select these NPCs?";
        if (!ScrollableMessageBox.Confirm(messageStr, "2.0.7 Update"))
        {
            _settings.HasUpdatedTo2_0_7_templates = true;
            return;
        }
        
        splashReporter?.UpdateStep("Validating existing NPC selections...");

        var invalidSelections = new List<(FormKey npcKey, string modName, string reason)>();

        // Check all existing selections
        foreach (var selection in _settings.SelectedAppearanceMods.ToList())
        {
            var npcFormKey = selection.Key;
            var (modName, sourceNpcFormKey) = selection.Value;

            // Find the corresponding mod setting
            var modSetting = modsVm.AllModSettings.FirstOrDefault(m =>
                m.DisplayName.Equals(modName, StringComparison.OrdinalIgnoreCase));

            if (modSetting == null)
            {
                // Mod no longer exists - this is a different issue, skip for now
                continue;
            }

            // Validate the selection
            var (isValid, failureReason) = npcSelectionBar.ValidateSelection(npcFormKey, modSetting);

            if (!isValid)
            {
                invalidSelections.Add((npcFormKey, modName, failureReason));
            }
        }

        if (invalidSelections.Any())
        {
            var message = new StringBuilder();
            message.AppendLine($"Found {invalidSelections.Count} invalid NPC selection(s) from previous versions.");
            message.AppendLine();
            message.AppendLine(
                "These selections have template chain issues that will likely cause incorrect appearances in-game.");
            message.AppendLine();
            message.AppendLine("Would you like to deselect these NPCs? (Recommended)");
            message.AppendLine();
            message.AppendLine("Details:");
            message.AppendLine();
            
            foreach (var (npcKey, modName, reason) in invalidSelections)
            {
                message.AppendLine($"• {reason}");
            }

            if (ScrollableMessageBox.Confirm(message.ToString(), "Invalid NPC Selections Found",
                    MessageBoxImage.Warning))
            {
                // User confirmed - deselect all problematic NPCs
                foreach (var(npcKey, modName, _) in invalidSelections)
                {
                    _settings.SelectedAppearanceMods.Remove(npcKey);
                    Debug.WriteLine($"Deselected invalid selection: {npcKey} -> {modName}");
                }

                ScrollableMessageBox.Show($"Deselected {invalidSelections.Count} invalid NPC selection(s).",
                    "Selections Cleared");
            }
            else
            {
                ScrollableMessageBox.ShowWarning(
                    "Invalid selections were kept. These NPCs may have incorrect appearances in-game until you manually correct them.",
                    "Selections Kept");
            }
        }
        else
        {
            Debug.WriteLine("No invalid NPC selections found during update check.");
        }

        _settings.HasUpdatedTo2_0_7_templates = true;
    }
}