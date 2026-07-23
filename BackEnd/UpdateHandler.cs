using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
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
    public async Task InitialCheckForUpdatesAndPatch(VM_SplashScreen? splashReporter = null)
    {
        // If the settings version is empty (e.g., a new user), there's nothing to migrate.
        if (string.IsNullOrWhiteSpace(_settings.ProgramVersion))
        {
            Debug.WriteLine("New user or fresh settings, skipping update check.");
            // Fresh settings have no stale analysis caches, and every migration is now
            // version-gated. A fresh user's ProgramVersion is stamped to current on first
            // save, so none of the < X.Y.Z gates re-fire — nothing to pre-stamp here.
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

        if (settingsVersion < "2.0.7")
        {
            UpdateTo2_0_7_Initial();
        }

        if (settingsVersion < "2.1.7")
        {
            UpdateTo2_1_7_Initial();
            // Must run BEFORE the NPCs/Mods VMs are constructed: those VMs auto-load the
            // previously-selected NPC, which can re-populate the new FaceFinder/AutoGen
            // roots and collide with the migration's File.Move destinations.
            await MaybeMoveLegacyMugshotFiles_Initial(splashReporter);
        }

        if (settingsVersion < "2.2.1")
        {
            UpdateTo2_2_1_Initial();
        }

        // 2.2.3: re-run analysis on mods whose cached snapshot would otherwise hit and skip
        // the current record-less-FaceGen and wig/antler detection. Both null every mod's
        // LastKnownState; calling them together is idempotent.
        if (settingsVersion < "2.2.3")
        {
            InvalidateAnalysisCachesForRecordlessFaceGenNpcs_Initial();
            InvalidateAnalysisCachesForWigScan_Initial();
        }

        Debug.WriteLine("Settings update process complete.");
    }

    // <summary>
    /// Checks the settings version against the current program version and applies any necessary updates.
    /// Runs after UI initializes
    /// </summary>
    public async Task FinalCheckForUpdatesAndPatch(VM_NpcSelectionBar npcsVm, VM_Mods modsVm, PluginProvider pluginProvider,
        Auxilliary aux, EnvironmentStateProvider environmentStateProvider, VM_SplashScreen? splashReporter)
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

        if (settingsVersion < "2.0.9")
        {
            await UpdateTo2_0_7_Final(modsVm, npcsVm, splashReporter);
        }

        if (settingsVersion < "2.1.1")
        {
            await UpdateTo2_1_1_Final(modsVm, splashReporter);
        }
        
        if (settingsVersion < "2.1.3")
        {
            await UpdateTo2_1_3_Final(modsVm, npcsVm, pluginProvider, aux, environmentStateProvider, splashReporter);
        }

        if (settingsVersion < "2.1.6")
        {
            await UpdateTo2_1_6_Final(modsVm, pluginProvider, environmentStateProvider, splashReporter);
        }

        if (settingsVersion < "2.1.7")
        {
            await UpdateTo2_1_7_Final(modsVm, splashReporter);
        }

        if (settingsVersion < "2.2.2")
        {
            await UpdateTo2_2_2_Final(modsVm, splashReporter);
        }

        Debug.WriteLine("Settings update process complete.");
    }

    /// <summary>
    /// 2.1.6 migration. Three passes over every mod setting:
    ///   1. Populate <see cref="VM_ModSetting.SkyPatcherTargetModKeys"/> by re-parsing the
    ///      mod's SkyPatcher INIs against the live load order + the plugins currently
    ///      attached to the VM. SkyPatcher template replacers (e.g. <c>t_Amalee_Replacer.esp</c>
    ///      patching Amalee in <c>3DNPC.esp</c>) don't override the foundation's NPC records,
    ///      so without this signal step 2 can't recognise the foundation and would re-attach
    ///      its folder. <see cref="VM_Mods.AnalyzeModSettingsAsync"/> populates this set for
    ///      mods that pass full analysis, but cache-hit mods come into the migration with it
    ///      empty — hence the explicit population here.
    ///   2. <see cref="VM_Mods.CleanupCorrespondingFolders"/> — drops stale entries from
    ///      <c>CorrespondingFolderPaths</c> that the current missing-master detector would
    ///      not re-add. Older versions of that detector attached foundation-mod folders
    ///      (e.g. "Interesting NPCs SE") to replacers (e.g. "Amalee Replacer - by Taranis")
    ///      even when the master plugin was already in the load order, or when the replacer
    ///      patched it via SkyPatcher rather than via record overrides.
    ///   3. <see cref="VM_ModSetting.RecomputeResourceOnlyPlugins"/> — re-derives
    ///      <c>ResourceOnlyModKeys</c> from master relationships and folder co-residence,
    ///      so multi-ESP foundations (e.g. 3DNPC.esp + 3DNPC0.esp + 3DNPC1.esp) are all
    ///      flagged together. Belt-and-suspenders for mods skipped by step 2.
    /// Together these prevent the NPC-base-plugin records from being duplicated into the
    /// output patch during dependency merge.
    /// </summary>
    private async Task UpdateTo2_1_6_Final(VM_Mods modsVm, PluginProvider pluginProvider,
        EnvironmentStateProvider environmentStateProvider, VM_SplashScreen? splashReporter)
    {
        splashReporter?.UpdateStep("Updating to 2.1.6: cleaning up resource-only plugins and stale folders...");

        var warnings = new System.Collections.Concurrent.ConcurrentBag<NPC_Plugin_Chooser_2.View_Models.InitializationWarning>();

        // Built once and reused across all mods. Captures the user's actual load order so
        // EditorID-based filterByNpcs entries (e.g. "Amalee3DNPC") that don't include a
        // plugin scope can still resolve when the foundation is enabled in the LO.
        var environmentEditorIdMap = environmentStateProvider.LoadOrder.PriorityOrder.Npc().WinningOverrides()
            .Where(npc => !string.IsNullOrWhiteSpace(npc.EditorID))
            .GroupBy(npc => npc.EditorID!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key,
                g => g.Select(npc => npc.FormKey).ToHashSet(),
                StringComparer.OrdinalIgnoreCase);

        await Task.Run(() =>
        {
            var modsToProcess = modsVm.AllModSettings.ToList();
            int processed = 0;
            foreach (var modVm in modsToProcess)
            {
                try
                {
                    PopulateSkyPatcherTargetsForMigration(modVm, pluginProvider, environmentEditorIdMap);

                    modsVm.CleanupCorrespondingFolders(modVm, warnings);
                    modVm.RecomputeResourceOnlyPlugins();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UpdateTo2_1_6_Final: failed for {modVm.DisplayName}: {ex.Message}");
                }
                processed++;
                splashReporter?.UpdateProgress((double)processed / modsToProcess.Count * 100,
                    $"Updating mod entry: {modVm.DisplayName}...");
            }
        });

        if (!warnings.IsEmpty)
        {
            Debug.WriteLine("2.1.6 cleanup warnings:" + Environment.NewLine +
                            string.Join(Environment.NewLine, warnings));
        }

        Debug.WriteLine("2.1.6 resource-only / corresponding-folder cleanup complete.");
    }

    /// <summary>
    /// 2.1.7 recovery. Repairs mods whose <see cref="VM_ModSetting.ResourceOnlyModKeys"/>
    /// got corrupted by the transient-IO race fixed in <see cref="VM_ModSetting.RecomputeResourceOnlyPlugins"/>.
    ///
    /// Failure shape: every plugin in <c>CorrespondingModKeys</c> ends up in
    /// <c>ResourceOnlyModKeys</c>, <c>NpcFormKeysToDisplayName</c> is empty, and the mod
    /// disappears from the NPC tab dropdown even though it's still visible in the Mods tab.
    /// Root cause was <see cref="Auxilliary.GetModKeysInDirectory"/> swallowing IO errors
    /// from <c>Directory.EnumerateFiles</c> and returning an empty list; the caller then
    /// flipped every key to resource-only and the throttled <c>SaveSettings</c> persisted it.
    ///
    /// Conservative detection (see <see cref="IsCorruptedAllResourceOnly"/>): only mods that
    /// have evidence of a prior successful analysis (non-empty <c>PluginsWithOverrideRecords</c>
    /// or <c>NpcFormKeysToNotifications</c>) qualify, so legitimate all-resource entries
    /// aren't touched. Recovery clears <c>ResourceOnlyModKeys</c> and delegates the
    /// re-analysis to <see cref="VM_Mods.RefreshSingleModSettingAsync"/> — the same code
    /// path the user's "Refresh Mod Data" button uses — so plugin loading, FaceGen cache
    /// scoping, <c>RefreshNpcLists</c>, and NPC-bar <c>AppearanceMods</c> re-linking stay
    /// in one place. Once fix #1 is in place, <c>RecomputeResourceOnlyPlugins</c> can't
    /// re-corrupt the state on a second IO blip.
    /// </summary>
    private async Task UpdateTo2_1_7_Final(VM_Mods modsVm, VM_SplashScreen? splashReporter)
    {
        await RepairResourceOnlyCorruption_2_1_7(modsVm, splashReporter);

        PurgeLegacyFFAndAutogenMugshotFolderPaths(modsVm);

        StripNowEmptyCuratedMugshotFolderPaths(modsVm);

        // Mirror the existing post-migration save pattern (see App.xaml.cs:315) so the
        // repaired / cleaned state is written back through the model before the next
        // throttled SaveSettings.
        modsVm.SaveModSettingsToModel();
    }

    /// <summary>
    /// 2.2.2 migration: one-time scan of every existing mod for assets that sit at base game /
    /// Creation Club asset paths (e.g. loose skin textures shipped by overhauls like Cathedral
    /// HMB). From 2.2.2 on, the patcher skips copying such assets unless the user opts in via
    /// the per-mod "Overwrite Base Game Assets" checkbox; this scan populates the persisted
    /// <c>HasBaseGameAssetPaths</c>/<c>BaseGameAssetPathCount</c> flags that make the checkbox
    /// appear. Mods imported or refreshed after this migration are scanned by
    /// <see cref="VM_Mods.AnalyzeModSettingsAsync"/> / <see cref="VM_Mods.RefreshSingleModSettingAsync"/>
    /// and never need it. Gated by <c>settingsVersion &lt; "2.2.2"</c>, so it runs once when
    /// upgrading from an older release and never again after the version stamp advances.
    /// </summary>
    private async Task UpdateTo2_2_2_Final(VM_Mods modsVm, VM_SplashScreen? splashReporter)
    {
        splashReporter?.UpdateStep("Updating to 2.2.2: scanning mods for base game asset overlaps...");

        var modsToProcess = modsVm.AllModSettings.ToList();
        if (modsToProcess.Count > 0)
        {
            // Same parallelism shape as VM_Mods.AnalyzeModSettingsAsync — the scan is disk-bound
            // per mod, and this migration runs exactly once.
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount);
            int processed = 0;
            var scanTasks = modsToProcess.Select(async modVm =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await modsVm.ScanForBaseGameAssetPathsAsync(modVm);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UpdateTo2_2_2_Final: scan failed for {modVm.DisplayName}: {ex.Message}");
                }
                finally
                {
                    int current = Interlocked.Increment(ref processed);
                    splashReporter?.UpdateProgress((double)current / modsToProcess.Count * 100,
                        $"Scanned: {modVm.DisplayName}");
                    semaphore.Release();
                }
            }).ToList();
            await Task.WhenAll(scanTasks);
        }

        // Mirror the existing post-migration save pattern so the scan results are written back
        // through the model before the next throttled SaveSettings.
        modsVm.SaveModSettingsToModel();

        Debug.WriteLine("2.2.2 base-game-asset scan complete.");
    }

    private async Task RepairResourceOnlyCorruption_2_1_7(VM_Mods modsVm, VM_SplashScreen? splashReporter)
    {
        splashReporter?.UpdateStep("Checking for mods left in a corrupted resource-only state...");

        var corrupted = modsVm.AllModSettings.Where(IsCorruptedAllResourceOnly).ToList();
        if (corrupted.Count == 0)
        {
            Debug.WriteLine("2.1.7 recovery: no corrupted mods detected.");
            return;
        }

        Debug.WriteLine($"2.1.7 recovery: detected {corrupted.Count} corrupted mod(s): " +
                        string.Join(", ", corrupted.Select(m => m.DisplayName)));

        int processed = 0;
        foreach (var vm in corrupted)
        {
            try
            {
                // Wipe the corrupted set so RefreshSingleModSettingAsync's downstream
                // RefreshNpcLists no longer skips every plugin. RecomputeResourceOnlyPlugins
                // (called inside Refresh via UpdateCorrespondingModKeys) will recompute the
                // correct set from disk — and with the bail-out guard added in 2.1.7,
                // it can no longer re-corrupt on a second IO blip.
                vm.ResourceOnlyModKeys.Clear();

                splashReporter?.UpdateStep($"Re-analyzing '{vm.DisplayName}'...");
                var (ok, reason) = await modsVm.RefreshSingleModSettingAsync(vm);
                if (!ok)
                {
                    Debug.WriteLine($"2.1.7 recovery: re-analysis of '{vm.DisplayName}' returned (false, '{reason}').");
                }
                else
                {
                    Debug.WriteLine($"2.1.7 recovery: re-analyzed '{vm.DisplayName}'; " +
                                    $"NpcFormKeysToDisplayName now has {vm.NpcFormKeysToDisplayName.Count} entries, " +
                                    $"ResourceOnlyModKeys now has {vm.ResourceOnlyModKeys.Count} entries.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"2.1.7 recovery: failed for '{vm.DisplayName}': {ex.Message}");
            }

            processed++;
            splashReporter?.UpdateProgress((double)processed / corrupted.Count * 100,
                $"Recovered: {vm.DisplayName}");
        }

        Debug.WriteLine("2.1.7 recovery complete.");
    }

    /// <summary>
    /// Strips every <see cref="VM_ModSetting.MugShotFolderPaths"/> entry whose normalized
    /// root resolves under the effective AutoGen or FaceFinder roots. Earlier builds
    /// appended those per-mod cache folders to <c>MugShotFolderPaths</c> after each
    /// render/cache-hit, turning a user-config field into a dumping ground for ephemeral
    /// runtime state. As of 2.1.7 <c>MugShotFolderPaths</c> is reserved for curated
    /// mugshot folders only.
    /// </summary>
    private void PurgeLegacyFFAndAutogenMugshotFolderPaths(VM_Mods modsVm)
    {
        var autogenRoot = Auxilliary.NormalizeFolderForCompare(
            Settings.GetEffectiveAutogenMugshotsFolder(_settings));
        var faceFinderRoot = Auxilliary.NormalizeFolderForCompare(
            Settings.GetEffectiveFaceFinderMugshotsFolder(_settings));

        int affectedMods = 0;
        int removedEntries = 0;

        foreach (var vm in modsVm.AllModSettings)
        {
            if (vm.MugShotFolderPaths.Count == 0) continue;

            var keep = vm.MugShotFolderPaths
                .Where(p =>
                {
                    var normalized = Auxilliary.NormalizeFolderForCompare(p);
                    if (string.IsNullOrEmpty(normalized)) return false;
                    return !Auxilliary.IsUnderRoot(normalized, autogenRoot)
                        && !Auxilliary.IsUnderRoot(normalized, faceFinderRoot);
                })
                .ToList();

            if (keep.Count == vm.MugShotFolderPaths.Count) continue;

            removedEntries += vm.MugShotFolderPaths.Count - keep.Count;
            affectedMods++;

            // Replace contents of the ObservableCollection in place so the reactive
            // subscriptions on MugShotFolderPaths fire — the property is `private set`
            // on the VM, so we can't reassign it.
            vm.MugShotFolderPaths.Clear();
            foreach (var p in keep) vm.MugShotFolderPaths.Add(p);
        }

        if (affectedMods > 0)
        {
            Debug.WriteLine($"2.1.7 mugshot-split: stripped {removedEntries} legacy FF/AutoGen entr(ies) " +
                            $"from MugShotFolderPaths across {affectedMods} mod(s).");
        }
    }

    /// <summary>
    /// Strips entries from each VM's <c>MugShotFolderPaths</c> that point under the
    /// curated <see cref="Settings.MugshotsFolder"/> at a folder tree that no longer
    /// holds any files — typically because the Initial-phase migration just moved the
    /// FF/AutoGen content out. Folders that still hold curated content, or untracked
    /// leftovers the migration deliberately didn't touch, keep their entry.
    /// </summary>
    private void StripNowEmptyCuratedMugshotFolderPaths(VM_Mods modsVm)
    {
        if (string.IsNullOrWhiteSpace(_settings.MugshotsFolder)) return;

        var oldRoot = Auxilliary.NormalizeFolderForCompare(_settings.MugshotsFolder);
        if (string.IsNullOrEmpty(oldRoot)) return;

        int strippedPathEntries = 0, strippedFromMods = 0;

        foreach (var vm in modsVm.AllModSettings)
        {
            if (vm.MugShotFolderPaths.Count == 0) continue;

            var keep = vm.MugShotFolderPaths
                .Where(p =>
                {
                    var n = Auxilliary.NormalizeFolderForCompare(p);
                    if (string.IsNullOrEmpty(n)) return true;
                    if (!Auxilliary.IsUnderRoot(n, oldRoot)) return true;
                    return !IsFolderTreeEmptyOfFiles(p);
                })
                .ToList();

            if (keep.Count == vm.MugShotFolderPaths.Count) continue;

            strippedPathEntries += vm.MugShotFolderPaths.Count - keep.Count;
            strippedFromMods++;

            vm.MugShotFolderPaths.Clear();
            foreach (var p in keep) vm.MugShotFolderPaths.Add(p);
        }

        if (strippedPathEntries > 0)
        {
            Debug.WriteLine(
                $"2.1.7 mugshot-split: stripped {strippedPathEntries} now-empty curated " +
                $"MugShotFolderPaths entr(ies) from {strippedFromMods} mod(s).");
        }
    }

    /// <summary>
    /// Asks the user whether to migrate their existing FaceFinder and auto-generated
    /// mugshots out of the curated <see cref="Settings.MugshotsFolder"/> into the new
    /// dedicated FF / AutoGen roots. Silently no-ops when the curated folder is unset
    /// or when both fallbacks are off with empty tracker sets — there is nothing to
    /// move and no future writes to surprise the user with.
    ///
    /// MUST run in the Initial phase (before <c>VM_NpcSelectionBar</c> / <c>VM_Mods</c>
    /// are constructed). Those VMs auto-load the previously-selected NPC, which can
    /// trigger fresh FaceFinder downloads into the new cache root and collide with
    /// this migration's <c>File.Move</c> destinations.
    /// </summary>
    private async Task MaybeMoveLegacyMugshotFiles_Initial(VM_SplashScreen? splashReporter)
    {
        if (string.IsNullOrWhiteSpace(_settings.MugshotsFolder))
        {
            return;
        }

        bool nothingTracked = _settings.CachedFaceFinderPaths.Count == 0
                              && _settings.GeneratedMugshotPaths.Count == 0;
        bool bothFallbacksOff = !_settings.UseFaceFinderFallback
                                && !_settings.UsePortraitCreatorFallback;
        if (bothFallbacksOff && nothingTracked)
        {
            return;
        }

        var message =
            """
            Starting in 2.1.7, FaceFinder downloads and auto-generated mugshots live in their own dedicated folders instead of sharing the curated Mugshots Folder you set up. This lets you switch between curated, FaceFinder, and auto-generated images per NPC without those sources overwriting each other.

            Defaults:
              • FaceFinder cache → <install dir>\FaceFinder Cache
              • Auto-generated   → <install dir>\AutoGen Mugshots
              • Curated mugshots stay where you set them.

            Would you like NPC2 to move your existing FaceFinder and auto-generated mugshots out of the curated folder into these new locations now? (Recommended)
            """;

        if (!ScrollableMessageBox.Confirm(message, "2.1.7 Update: Mugshot Folder Split",
                MessageBoxImage.Information))
        {
            Debug.WriteLine("2.1.7 mugshot-split: user declined the file migration.");
            return;
        }

        splashReporter?.UpdateStep("Moving legacy FaceFinder and auto-generated mugshots to their new folders...");

        var oldRoot = Auxilliary.NormalizeFolderForCompare(_settings.MugshotsFolder);
        var ffRoot = Settings.GetEffectiveFaceFinderMugshotsFolder(_settings);
        var autogenRoot = Settings.GetEffectiveAutogenMugshotsFolder(_settings);

        int ffMoved = 0, ffSkipped = 0, ffFailed = 0;
        int agMoved = 0, agSkipped = 0, agFailed = 0;
        // Top-level subdirectories of oldRoot that we moved files out of. We prune each
        // tree bottom-up after the move so emptied intermediate folders disappear too,
        // not just the immediate leaf the file lived in.
        var touchedTopLevelDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failureLines = new List<string>();

        await Task.Run(() =>
        {
            // FaceFinder writes a ".ffmeta.json" sidecar next to each .webp; without
            // moving it the source folder stays non-empty and the leaf-prune + the
            // Final-phase MugShotFolderPaths strip both decline to act on it.
            (ffMoved, ffSkipped, ffFailed) = MoveTrackedFiles(
                _settings.CachedFaceFinderPaths, oldRoot, ffRoot, touchedTopLevelDirs,
                failureLines, "FaceFinder",
                sidecarSuffix: FaceFinderClient.MetadataFileExtension);
            // AutoGen mugshots embed their metadata inside the PNG (tEXt chunks),
            // so no sidecar to chase.
            (agMoved, agSkipped, agFailed) = MoveTrackedFiles(
                _settings.GeneratedMugshotPaths, oldRoot, autogenRoot, touchedTopLevelDirs,
                failureLines, "AutoGen",
                sidecarSuffix: null);

            // Recursively delete empty directory trees we touched. Stops as soon as it
            // hits any file, so this can never delete curated content.
            foreach (var dir in touchedTopLevelDirs)
            {
                DeleteEmptyDirectoryTree(dir);
            }
        });

        string? failureLogPath = null;
        if (failureLines.Count > 0)
        {
            failureLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "2.1.7-mugshot-migration.log");
            try
            {
                var header = new[]
                {
                    $"NPC2 2.1.7 mugshot migration — failures recorded {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"Curated root: {_settings.MugshotsFolder}",
                    $"FaceFinder destination: {ffRoot}",
                    $"AutoGen destination: {autogenRoot}",
                    new string('-', 72),
                };
                File.WriteAllLines(failureLogPath, header.Concat(failureLines));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"2.1.7 mugshot-split: could not write failure log: {ex.Message}");
                failureLogPath = null;
            }
        }

        // MugShotFolderPaths cleanup runs in the Final phase (see
        // StripNowEmptyCuratedMugshotFolderPaths) once the per-mod VMs exist and have
        // their reactive subscriptions wired up.

        Debug.WriteLine(
            $"2.1.7 mugshot-split: FaceFinder moved={ffMoved}, skipped={ffSkipped}, failed={ffFailed}; " +
            $"AutoGen moved={agMoved}, skipped={agSkipped}, failed={agFailed}.");

        ShowMugshotMigrationReport(ffMoved, ffSkipped, ffFailed, agMoved, agSkipped, agFailed,
            failureLogPath);
    }

    private static void ShowMugshotMigrationReport(
        int ffMoved, int ffSkipped, int ffFailed,
        int agMoved, int agSkipped, int agFailed,
        string? failureLogPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FaceFinder cache:  moved {ffMoved}, skipped {ffSkipped}, failed {ffFailed}");
        sb.AppendLine($"Auto-generated:    moved {agMoved}, skipped {agSkipped}, failed {agFailed}");

        if (ffFailed > 0 || agFailed > 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                "Some files could not be moved (in use, locked, or destination already " +
                "occupied). They remain in the curated Mugshots Folder.");
            if (!string.IsNullOrEmpty(failureLogPath))
            {
                sb.AppendLine();
                sb.AppendLine($"Failure details written to:");
                sb.AppendLine(failureLogPath);
            }
            ScrollableMessageBox.ShowWarning(sb.ToString(), "2.1.7 Mugshot Migration Complete");
        }
        else
        {
            ScrollableMessageBox.Show(sb.ToString(), "2.1.7 Mugshot Migration Complete",
                MessageBoxImage.Information);
        }
    }

    /// <summary>True when <paramref name="path"/> doesn't exist, or exists but contains
    /// no files at any depth (empty subdirectories are allowed). Used to decide whether
    /// a stale <see cref="VM_ModSetting.MugShotFolderPaths"/> entry can be safely
    /// dropped after the migration.</summary>
    private static bool IsFolderTreeEmptyOfFiles(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        try
        {
            if (!Directory.Exists(path)) return true;
            return !Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
        }
        catch
        {
            // Can't enumerate (permissions, etc.) → assume non-empty so we keep the entry.
            return false;
        }
    }

    /// <summary>Recursively deletes empty subdirectories of <paramref name="dir"/>,
    /// then deletes <paramref name="dir"/> itself if it's empty. Stops at the first
    /// file encountered, so curated content prevents the prune from biting in.</summary>
    private static void DeleteEmptyDirectoryTree(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            if (!Directory.Exists(dir)) return;

            foreach (var sub in Directory.GetDirectories(dir))
            {
                DeleteEmptyDirectoryTree(sub);
            }

            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch
        {
            // Best effort — leave behind anything the OS won't let us touch.
        }
    }

    /// <summary>Moves files whose tracked absolute paths sit under <paramref name="oldRoot"/>
    /// into <paramref name="newRoot"/>, preserving their sub-path. Updates the tracker set
    /// in place: old paths come out, new paths go in. Counts returned as (moved, skipped, failed).
    /// Skipped covers files that no longer exist on disk and entries already outside
    /// <paramref name="oldRoot"/>; failed covers IO / access errors during the move.</summary>
    private static (int moved, int skipped, int failed) MoveTrackedFiles(
        HashSet<string> trackerSet,
        string normalizedOldRoot,
        string newRoot,
        HashSet<string> touchedTopLevelDirs,
        List<string> failureLines,
        string bucketLabel,
        string? sidecarSuffix)
    {
        if (trackerSet.Count == 0) return (0, 0, 0);

        var normalizedNewRoot = Auxilliary.NormalizeFolderForCompare(newRoot);
        if (string.IsNullOrEmpty(normalizedOldRoot)
            || normalizedNewRoot.Equals(normalizedOldRoot, StringComparison.OrdinalIgnoreCase))
        {
            // Either no curated folder to migrate from, or the user has manually pointed
            // the new root at the curated folder — nothing to move.
            return (0, trackerSet.Count, 0);
        }

        int moved = 0, skipped = 0, failed = 0;

        // Snapshot before mutating, to avoid concurrent-modification on the HashSet.
        foreach (var oldPath in trackerSet.ToList())
        {
            if (string.IsNullOrWhiteSpace(oldPath))
            {
                trackerSet.Remove(oldPath);
                skipped++;
                continue;
            }

            var normalizedOldPath = Auxilliary.NormalizeFolderForCompare(oldPath);
            if (!Auxilliary.IsUnderRoot(normalizedOldPath, normalizedOldRoot))
            {
                // Already outside the curated folder — leave it alone.
                skipped++;
                continue;
            }

            if (!File.Exists(oldPath))
            {
                // Stale tracker entry — drop it so subsequent "Delete All" sweeps don't
                // chase phantoms.
                trackerSet.Remove(oldPath);
                skipped++;
                continue;
            }

            // Preserve sub-path under the curated root when rebasing onto the new root.
            // normalizedOldPath starts with normalizedOldRoot + separator (IsUnderRoot
            // returned true), so this slice is always safe.
            var relative = normalizedOldPath.Substring(normalizedOldRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var newPath = Path.Combine(newRoot, relative);

            try
            {
                Auxilliary.CreateDirectoryIfNeeded(newPath, Auxilliary.PathType.File);
                File.Move(oldPath, newPath, overwrite: false);

                trackerSet.Remove(oldPath);
                trackerSet.Add(newPath);

                // Move the metadata sidecar alongside the image (FaceFinder: ".ffmeta.json").
                // Sidecar issues are not failures — we still count the main file as moved.
                if (!string.IsNullOrEmpty(sidecarSuffix))
                {
                    var oldSidecar = oldPath + sidecarSuffix;
                    var newSidecar = newPath + sidecarSuffix;
                    if (File.Exists(oldSidecar))
                    {
                        try
                        {
                            File.Move(oldSidecar, newSidecar, overwrite: false);
                        }
                        catch (Exception sidecarEx) when (sidecarEx is IOException || sidecarEx is UnauthorizedAccessException)
                        {
                            var line = $"[{bucketLabel}/sidecar] {oldSidecar} → {newSidecar} : {sidecarEx.Message}";
                            Debug.WriteLine($"2.1.7 mugshot-split: {line}");
                            lock (failureLines) failureLines.Add(line);
                        }
                    }
                }

                // Top-level subdirectory under the curated root (e.g. "<oldRoot>/<modName>")
                // — bottom-up prune at the caller will collapse this whole tree if empty.
                var firstSegment = relative.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    2, StringSplitOptions.RemoveEmptyEntries);
                if (firstSegment.Length > 0)
                {
                    touchedTopLevelDirs.Add(Path.Combine(normalizedOldRoot, firstSegment[0]));
                }
                else
                {
                    var sourceDir = Path.GetDirectoryName(oldPath);
                    if (!string.IsNullOrEmpty(sourceDir)) touchedTopLevelDirs.Add(sourceDir);
                }

                moved++;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                var line = $"[{bucketLabel}] {oldPath} → {newPath} : {ex.Message}";
                Debug.WriteLine($"2.1.7 mugshot-split: {line}");
                lock (failureLines) failureLines.Add(line);
                failed++;
            }
        }

        return (moved, skipped, failed);
    }

    /// <summary>
    /// Strong signature for the IO-blip ResourceOnlyModKeys corruption surfaced in
    /// caracal100's 2.1.6 bug report. Every condition must hold to qualify for
    /// auto-repair, so legitimate all-resource entries (e.g. master-only dependency
    /// mods) aren't disturbed.
    /// </summary>
    private static bool IsCorruptedAllResourceOnly(VM_ModSetting vm)
    {
        if (vm.IsFaceGenOnlyEntry) return false;
        if (vm.IsMugshotOnlyEntry) return false;
        if (vm.IsAutoGenerated) return false;
        if (vm.CorrespondingFolderPaths.Count == 0) return false;
        if (vm.CorrespondingModKeys.Count == 0) return false;

        // Every plugin in CorrespondingModKeys is flagged as resource-only.
        if (!vm.CorrespondingModKeys.All(k => vm.ResourceOnlyModKeys.Contains(k)))
            return false;

        // The NPC dropdown source is empty — i.e. the mod actually disappeared
        // from the NPC tab from the user's perspective.
        if (vm.NpcFormKeysToDisplayName.Count != 0) return false;

        // Evidence of prior successful analysis — proves the plugins really did
        // have appearance content the analyzer recognised. This is what
        // distinguishes a true IO-corruption victim from a mod that's
        // legitimately all-resource.
        return vm.HasPluginWithOverrideRecords
               || vm.NpcFormKeysToNotifications.Count > 0;
    }

    /// <summary>
    /// Loads the plugins currently associated with <paramref name="vm"/>, builds a per-VM
    /// editor-id map from them, and uses it to resolve the SkyPatcher INI targets so
    /// <see cref="VM_ModSetting.SkyPatcherTargetModKeys"/> reflects the foundation plugins
    /// the mod patches. Must run BEFORE <see cref="VM_Mods.CleanupCorrespondingFolders"/>
    /// so the cleanup pass sees the targets and excludes them from missing-master discovery.
    /// </summary>
    private static void PopulateSkyPatcherTargetsForMigration(
        VM_ModSetting vm,
        PluginProvider pluginProvider,
        IReadOnlyDictionary<string, HashSet<FormKey>> environmentEditorIdMap)
    {
        if (vm.IsMugshotOnlyEntry || vm.IsAutoGenerated) return;
        if (vm.CorrespondingFolderPaths.Count == 0 || vm.CorrespondingModKeys.Count == 0) return;

        var modFolderPaths = vm.CorrespondingFolderPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plugins = pluginProvider.LoadPlugins(vm.CorrespondingModKeys, modFolderPaths, out var loadedPaths);
        try
        {
            // Per-VM map mirrors AnalyzeModSettingsAsync: gives EditorID lookups access to the
            // foundation's NPCs while the foundation folder is still attached (which it is at
            // this point — that's exactly the polluted state we're about to clean up).
            var modEditorIdMap = plugins.SelectMany(x => x.Npcs)
                .Where(npc => !string.IsNullOrWhiteSpace(npc.EditorID))
                .GroupBy(npc => npc.EditorID!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key,
                    g => g.Select(npc => npc.FormKey).ToHashSet(),
                    StringComparer.OrdinalIgnoreCase);

            // .Result is acceptable here: we're already on a Task.Run thread, and
            // GetSkyPatcherTargetModKeysAsync's only async work is File.ReadAllLinesAsync.
            // Widened lookup (env→mod fallback) catches foundations that exist on disk but
            // aren't enabled in the user's LO — exactly the polluted-state case the
            // migration is designed to clean up.
            vm.SkyPatcherTargetModKeys = vm
                .GetSkyPatcherTargetModKeysAsync(environmentEditorIdMap, modEditorIdMap).Result;
        }
        finally
        {
            pluginProvider.UnloadPlugins(loadedPaths);
        }
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
    }

    /// <summary>
    /// 2.1.7 migration: silently switch <c>SubsurfaceStrength</c> from the prior 2.0
    /// default to 0.1 for users who never touched it.
    ///
    /// The 2.0 default introduced earlier was meant to push skin toward a more
    /// pronounced "warm-flesh" portrait look, but in practice it desaturates
    /// high-chroma races (Orcs go olive, Redguards go Mediterranean) — likely an
    /// SSS implementation or lighting-setup interaction issue to revisit later.
    /// Until then, the default is a faint 0.1 — enough warmth to be visible
    /// without the desaturation 2.0 produced.
    ///
    /// Strict equality at 2.0f is the "untouched" signal: the prior default was
    /// literal <c>2.0f</c>, JSON serialization preserves it exactly, and any user
    /// who tuned it (1.5, 0.5, etc.) keeps their value. The migration is silent
    /// and idempotent — after first run the value is 0.1, so the equality check
    /// fails on every subsequent run until the program version is bumped.
    /// </summary>
    private void UpdateTo2_1_7_Initial()
    {
        const float priorDefault = 2.0f;
        if (_settings.InternalMugshot.SubsurfaceStrength == priorDefault)
        {
            _settings.InternalMugshot.SubsurfaceStrength = 0.1f;
            Debug.WriteLine("2.1.7 Update: SubsurfaceStrength was at the prior default of 2.0; reverted to 0.1.");
        }
    }

    /// <summary>
    /// 2.2.1 migration: bump <c>SubsurfaceStrength</c> from the prior 0.1 default to
    /// 1.0 for users who never touched it.
    ///
    /// 1.0 is "honest" SSS at the source NIF rolloff values, pairing with the
    /// game-faithful skin soft-lighting path in CharacterViewer.Rendering
    /// (SkinFaithfulSoftLight) added this release. At the prior faint 0.1 the warm
    /// terminator band was effectively invisible.
    ///
    /// Strict equality at 0.1f is the "untouched" signal: 0.1 was the shipped
    /// default from 2.1.7 through 2.2.0 (and the value the 2.1.7 migration wrote for
    /// users coming from the 2.0 default). Any user who tuned it (0.5, 1.5, 2.0, ...)
    /// keeps their value. Silent and idempotent — after first run the value is 1.0,
    /// so the equality check fails on every subsequent run until the version bumps.
    /// </summary>
    private void UpdateTo2_2_1_Initial()
    {
        const float priorDefault = 0.1f;
        if (_settings.InternalMugshot.SubsurfaceStrength == priorDefault)
        {
            _settings.InternalMugshot.SubsurfaceStrength = 1.0f;
            Debug.WriteLine("2.2.1 Update: SubsurfaceStrength was at the prior default of 0.1; bumped to 1.0.");
        }
    }

    /// <summary>
    /// One-time (2.2.3) invalidation of every mod's analysis cache (LastKnownState)
    /// so the next population pass runs <see cref="VM_ModSetting.ScanForWigs"/> on
    /// existing mods. Without this, mods with a valid cached snapshot would never
    /// get wig/antler detection and the Wig Handling Mode dropdown would never
    /// appear for them. Gated by the settings <c>ProgramVersion</c> (&lt; 2.2.3),
    /// consistent with the other one-time migrations. Covers both the initial
    /// wig/antler detection and the later extension to WornArmor-baked ArmorAddons
    /// and FaceGen head parts (persisted DetectedAntlerArmatures /
    /// DetectedAntlerHeadParts), since both landed in the 2.2.3 cycle.
    /// </summary>
    private void InvalidateAnalysisCachesForWigScan_Initial()
    {
        foreach (var modSetting in _settings.ModSettings)
        {
            modSetting.LastKnownState = null;
        }

        Debug.WriteLine(
            "One-time analysis-cache invalidation applied so wig/antler detection runs on this launch.");
    }

    /// <summary>
    /// One-time (&lt; 2.2.3) invalidation of every mod's analysis cache (LastKnownState) so
    /// the next population pass re-runs RefreshNpcLists, which detects FaceGen files shipped
    /// without a plugin record inside plugin-backed mods and surfaces those NPCs as
    /// selectable FaceGen-only entries (with an issue-notification icon). Without this,
    /// mods with a valid cached snapshot would never recompute FaceGenOnlyNpcFormKeys.
    /// Gated by the settings <c>ProgramVersion</c> (&lt; 2.2.3), consistent with the other
    /// one-time migrations. (Historically counter-guarded to force re-scans as the detection
    /// evolved — resource-only records counting as record-backed, tooltip rewording — but all
    /// of that predates 2.2.3, so a single version gate now covers every upgrader.)
    /// </summary>
    private void InvalidateAnalysisCachesForRecordlessFaceGenNpcs_Initial()
    {
        foreach (var modSetting in _settings.ModSettings)
        {
            modSetting.LastKnownState = null;
        }

        Debug.WriteLine(
            "One-time analysis-cache invalidation applied so record-less FaceGen NPCs get discovered on this launch.");
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
                if (modVm.CheckForInjectedRecords(splashReporter == null ? null : splashReporter.ReportWarning,
                        _settings.LocalizationLanguage).Result)
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
        string messageStr =
            "Previous versions of NPC Plugin Chooser allowed you to select appearances for NPCs with invalid templates using the Select All From Mod batch action. This could result in bugged appearances in-game for those NPCs. Would you like to scan and automatically de-select these NPCs?";
        if (!ScrollableMessageBox.Confirm(messageStr, "2.0.7 Update"))
        {
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
                foreach (var (npcKey, modName, _) in invalidSelections)
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
    }

    private async Task UpdateTo2_1_1_Final(VM_Mods modsVm, VM_SplashScreen? splashReporter)
    {
        splashReporter?.UpdateStep("Caching SkyPatcher Templates...");

        // We need empty maps because GetSkyPatcherImportsAsync requires them for resolving editor IDs,
        // though for this specific update we are mostly interested in FormKey matches which don't need the map.
        // If your mods rely heavily on EditorID mapping for SkyPatcher, we might miss some here without full maps,
        // but building full maps is expensive. 
        // Ideally, we reuse the maps if available, but passing empty ones is safe to prevent crashes.
        var emptyMap = new Dictionary<string, HashSet<FormKey>>();

        var allMods = modsVm.AllModSettings.ToList();
        int count = 0;

        await Task.Run(async () =>
        {
            foreach (var mod in allMods)
            {
                try
                {
                    // Re-parse the INIs for this mod
                    var imports = await mod.GetSkyPatcherImportsAsync(emptyMap, emptyMap);

                    foreach (var import in imports)
                    {
                        // Cache the SOURCE NPC (the template)
                        lock (_settings.CachedSkyPatcherTemplates)
                        {
                            _settings.CachedSkyPatcherTemplates.Add(import.SourceNpc);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error scanning SkyPatcher INIs for {mod.DisplayName}: {ex.Message}");
                }

                count++;
                splashReporter?.UpdateProgress((double)count / allMods.Count * 100, $"Scanning {mod.DisplayName}...");
            }
        });

        Debug.WriteLine(
            $"SkyPatcher Template Scan Complete. Cached {_settings.CachedSkyPatcherTemplates.Count} templates.");
    }
    
    /// <summary>
    /// Prunes NPCs from all mod entries that no longer pass the updated race/template
    /// validation (e.g. templated dragons, spiders, etc. that were previously allowed
    /// through because any templated NPC bypassed the ActorTypeNPC check).
    /// 
    /// When the same NPC appears in multiple plugins within a single VM_ModSetting,
    /// priority is determined by CorrespondingModKeys order (last = highest priority).
    /// If the highest-priority version passes the check, the NPC is kept even if a
    /// lower-priority version would fail.
    /// </summary>
    private async Task UpdateTo2_1_3_Final(VM_Mods modsVm, VM_NpcSelectionBar npcsVm,
        PluginProvider pluginProvider, Auxilliary aux, EnvironmentStateProvider environmentStateProvider, 
        VM_SplashScreen? splashReporter)
    {
        splashReporter?.UpdateStep("Updating to 2.1.3: Pruning invalid templated NPCs and leveled NPC template chains...");

        var modsToCheck = modsVm.AllModSettings.ToList();

        if (!modsToCheck.Any())
        {
            Debug.WriteLine("2.1.3 Update: No mod entries found.");
            return;
        }

        // --- Phase 1: Scan all mods and compile the full removal manifest on a background thread ---
        // Key: VM_ModSetting  Value: list of (FormKey, logString) pairs flagged for removal
        var removalManifest = new Dictionary<VM_ModSetting, List<(FormKey NpcFormKey, string LogString)>>();
        // Track which plugins were loaded per mod so we can unload them afterwards
        var loadedPathsByMod = new Dictionary<VM_ModSetting, HashSet<string>>();
        // Keep the loaded plugins alive for Phase 3 (NpcNames/NpcEditorIDs rebuild)
        var pluginsByMod = new Dictionary<VM_ModSetting, HashSet<ISkyrimModGetter>>();

        await Task.Run(() =>
        {
            int modIndex = 0;
            foreach (var vm in modsToCheck)
            {
                modIndex++;
                splashReporter?.UpdateProgress((double)modIndex / modsToCheck.Count * 50,
                    $"Scanning {vm.DisplayName}...");

                var modFolderPaths = vm.CorrespondingFolderPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var plugins = pluginProvider.LoadPlugins(vm.CorrespondingModKeys, modFolderPaths, out var loadedPaths);
                loadedPathsByMod[vm] = loadedPaths;
                pluginsByMod[vm] = plugins;

                // Build a lookup of NPC records respecting CorrespondingModKeys priority.
                // Iterate plugins in CorrespondingModKeys order so that later (higher-priority)
                // entries overwrite earlier ones.
                var npcLookup = new Dictionary<FormKey, INpcGetter>();
                foreach (var modKey in vm.CorrespondingModKeys)
                {
                    var plugin = plugins.FirstOrDefault(p => p.ModKey == modKey);
                    if (plugin == null) continue;

                    foreach (var npc in plugin.Npcs)
                    {
                        npcLookup[npc.FormKey] = npc; // last-wins: higher-priority ModKey overwrites
                    }
                }

                var flaggedForRemoval = new List<(FormKey, string)>();

                foreach (var npcFormKey in vm.NpcFormKeys)
                {
                    // If the NPC isn't in the loaded plugins, it may have come from another source
                    // (e.g. FaceGen-only or mugshot-only). Leave it alone.
                    if (!npcLookup.TryGetValue(npcFormKey, out var npcGetter))
                    {
                        continue;
                    }

                    if (!aux.IsValidAppearanceRace(npcGetter.Race.FormKey, npcGetter,
                            _settings.LocalizationLanguage, out string rejectionMessage, out var resolvedRace))
                    {
                        var raceLogStr = npcGetter.Race.FormKey.ToString();
                            if (resolvedRace != null)
                            {
                                raceLogStr = Auxilliary.GetLogString(resolvedRace, _settings.LocalizationLanguage,
                                    fullString: false);
                            }
                            else if (environmentStateProvider.LinkCache.TryResolve<IRaceGetter>(npcGetter.Race, out var raceGetter) && raceGetter != null)
                            {
                                raceLogStr = Auxilliary.GetLogString(raceGetter, _settings.LocalizationLanguage,
                                    fullString: false);
                            }
                        var logStr = Auxilliary.GetLogString(npcGetter, _settings.LocalizationLanguage, fullString: true)
                                     + $" [Race: {raceLogStr}]";
                        flaggedForRemoval.Add((npcFormKey, logStr));
                        Debug.WriteLine(
                            $"2.1.3 Update: Flagging {logStr} from {vm.DisplayName} because {rejectionMessage}");
                    }
                    // Second check: if the NPC passed the race check, see if its template
                    // chain terminates in a Leveled NPC (these can't have unique appearances).
                    else if (Auxilliary.IsValidTemplatedNpc(npcGetter) &&
                             aux.TemplateChainTerminatesInLeveledNpc(npcGetter, plugins))
                    {
                        var logStr = Auxilliary.GetLogString(npcGetter, _settings.LocalizationLanguage, fullString: true);
                        var leveledReason = "its template chain terminates in a Leveled NPC.";
                        flaggedForRemoval.Add((npcFormKey, logStr));
                        Debug.WriteLine(
                            $"2.1.3 Update: Flagging {logStr} from {vm.DisplayName} because {leveledReason}");
                    }
                }

                if (flaggedForRemoval.Any())
                {
                    removalManifest[vm] = flaggedForRemoval;
                }
            }
        });

        // If nothing to remove, clean up and return early
        if (!removalManifest.Any())
        {
            Debug.WriteLine("2.1.3 Update: No invalid templated NPCs or leveled NPC template chains found across any mods.");
            foreach (var kvp in loadedPathsByMod)
            {
                pluginProvider.UnloadPlugins(kvp.Value);
            }
            return;
        }

        // --- Phase 2: User notification and optional backup (UI thread) ---
        int totalFlagged = removalManifest.Values.Sum(list => list.Count);

        // Build a combined display message
        var displayMessage = new StringBuilder();
        displayMessage.AppendLine(
            "2.1.3 has updated its NPC loader to exclude non-humanoid template NPCs and NPCs whose " +
            "template chain terminates in a Leveled NPC, which previously had been erroneously included " +
            "in the NPC list. The following NPCs are slated for removal:");
        displayMessage.AppendLine();

        foreach (var (vm, flagged) in removalManifest)
        {
            displayMessage.AppendLine($"[{vm.DisplayName}] ({flagged.Count} NPC(s)):");
            foreach (var (_, logStr) in flagged)
            {
                displayMessage.AppendLine($"  • {logStr}");
            }
            displayMessage.AppendLine();
        }

        // Check if any flagged NPCs have user assignments
        var allFlaggedFormKeys = removalManifest.Values
            .SelectMany(list => list.Select(entry => entry.NpcFormKey))
            .ToHashSet();

        var flaggedWithAssignments = allFlaggedFormKeys
            .Where(fk => _settings.SelectedAppearanceMods.ContainsKey(fk))
            .ToList();

        if (flaggedWithAssignments.Any())
        {
            var backupMessage = new StringBuilder();
            backupMessage.AppendLine(
                "2.1.3 has updated its NPC loader to exclude non-humanoid template NPCs and NPCs whose " +
                "template chain terminates in a Leveled NPC, which previously had been erroneously included " +
                "in the NPC list. You have made a selection for the following " +
                "NPCs which are slated for removal. Would you like to make a backup of your selections now " +
                "so that if any of the removals are erroneous, you can restore them by re-importing your list?");
            backupMessage.AppendLine();

            foreach (var fk in flaggedWithAssignments)
            {
                var (modName, _) = _settings.SelectedAppearanceMods[fk];
                // Find the log string from the manifest
                var logStr = removalManifest.Values
                    .SelectMany(list => list)
                    .FirstOrDefault(entry => entry.NpcFormKey == fk).LogString ?? fk.ToString();
                backupMessage.AppendLine($"  • {logStr}  →  [{modName}]");
            }

            if (ScrollableMessageBox.Confirm(backupMessage.ToString(), "Backup Selections Before 2.1.3 Update",
                    MessageBoxImage.Warning))
            {
                // Execute the same export that the Export button uses
                try
                {
                    await npcsVm.ExportChoicesCommand.Execute().FirstAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"2.1.3 Update: Export failed or was cancelled: {ex.Message}");
                    // If the export failed, we still proceed with the removal
                }
            }
        }

        // --- Phase 3: Perform the removal and rebuild NpcNames/NpcEditorIDs on a background thread ---
        splashReporter?.UpdateStep("Updating to 2.1.3: Removing invalid NPCs...");

        await Task.Run(() =>
        {
            int totalRemoved = 0;

            foreach (var (vm, flagged) in removalManifest)
            {
                var formKeysToRemove = flagged.Select(entry => entry.NpcFormKey).ToHashSet();

                // Remove from all mod-level collections
                foreach (var formKey in formKeysToRemove)
                {
                    vm.NpcFormKeys.Remove(formKey);
                    vm.NpcFormKeysToDisplayName.Remove(formKey);
                    vm.AvailablePluginsForNpcs.Remove(formKey);
                    vm.NpcFormKeysToNotifications.Remove(formKey);
                    vm.AmbiguousNpcFormKeys.Remove(formKey);

                    // Clear any user selection for the pruned NPC
                    _settings.SelectedAppearanceMods.Remove(formKey);
                }

                // Rebuild NpcNames and NpcEditorIDs from the remaining NPCs
                var remainingNpcNames = new HashSet<string>();
                var remainingNpcEditorIDs = new HashSet<string>();
                var npcFormKeysFoundInPlugins = new HashSet<FormKey>();

                if (pluginsByMod.TryGetValue(vm, out var plugins))
                {
                    foreach (var plugin in plugins)
                    {
                        foreach (var npc in plugin.Npcs)
                        {
                            // Only include NPCs that are still in the mod's NPC list
                            if (!vm.NpcFormKeys.Contains(npc.FormKey)) continue;
                            npcFormKeysFoundInPlugins.Add(npc.FormKey);

                            if (Auxilliary.TryGetName(npc, _settings.LocalizationLanguage,
                                    _settings.FixGarbledText, out string name))
                            {
                                remainingNpcNames.Add(name);
                            }

                            if (!string.IsNullOrEmpty(npc.EditorID))
                            {
                                remainingNpcEditorIDs.Add(npc.EditorID);
                            }
                        }
                    }
                }
                
                // For remaining NPCs not found in plugins (mugshot-only, FaceGen-only),
                // preserve their display names so search still works
                foreach (var npcFormKey in vm.NpcFormKeys)
                {
                    if (npcFormKeysFoundInPlugins.Contains(npcFormKey)) continue;
                    if (vm.NpcFormKeysToDisplayName.TryGetValue(npcFormKey, out var displayName)
                        && !string.IsNullOrEmpty(displayName))
                    {
                        remainingNpcNames.Add(displayName);
                    }
                }

                vm.NpcNames = remainingNpcNames;
                vm.NpcEditorIDs = remainingNpcEditorIDs;
                vm.RefreshNpcCount();

                totalRemoved += formKeysToRemove.Count;
                Debug.WriteLine(
                    $"2.1.3 Update: Removed {formKeysToRemove.Count} invalid NPC(s) from {vm.DisplayName}");
            }

            Debug.WriteLine($"2.1.3 Update: Pruning complete. Removed {totalRemoved} invalid NPC(s) total.");
        });
        
        // --- Phase 4: Synchronize the NPC selection bar (UI thread) ---
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            npcsVm.PruneRemovedNpcs(allFlaggedFormKeys);
        });

        // --- Cleanup: Unload all plugins that were loaded during the scan ---
        foreach (var kvp in loadedPathsByMod)
        {
            pluginProvider.UnloadPlugins(kvp.Value);
        }
    }
}