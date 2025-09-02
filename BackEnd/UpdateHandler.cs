using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Media;
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

        // If the settings version is the same or newer, no action is needed.
        if (settingsVersion >= currentVersion)
        {
            return;
        }

        Debug.WriteLine($"Updating settings from version {settingsVersion} to {currentVersion}");

        // --- Update Logic ---
        // Updates should be cumulative. If a user jumps from 2.0.7 to 2.1.0,
        // all intermediate patches (for < 2.0.8, < 2.0.9, etc.) should be applied in order.
        // The `if` statements are not `else if` for this reason.

        if (settingsVersion < "2.0.4")
        {
            UpdateTo2_0_4_Initial();
        }

        Debug.WriteLine("Settings update process complete.");
    }
    
    // <summary>
    /// Checks the settings version against the current program version and applies any necessary updates.
    /// Runs after UI initializes
    /// </summary>
    public async Task FinalCheckForUpdatesAndPatch(VM_Mods modsVm, VM_SplashScreen? splashReporter)
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
}