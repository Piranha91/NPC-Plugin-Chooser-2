// VM_Run.cs (Revised based on clarifications)
using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using static NPC_Plugin_Chooser_2.BackEnd.PatcherExtensions;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.IO;
using Mutagen.Bethesda;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Windows;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Noggog; // Needed for LinkCache Interface

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_Run : ReactiveObject
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly Auxilliary _auxilliary;
        private readonly VM_Settings _vmSettings;
        private readonly Lazy<VM_Mods> _lazyVmMods;
        private readonly CompositeDisposable _disposables = new();

        // --- Constants ---
        private const string ALL_NPCS_GROUP = "<All NPCs>";
        // Define Base Game Plugins (matching V1) - used for DuplicateFromOnlyReferenced exclusion
        private static readonly HashSet<ModKey> BaseGamePlugins = new()
        {
            ModKey.FromNameAndExtension("Skyrim.esm"),
            ModKey.FromNameAndExtension("Update.esm"),
            ModKey.FromNameAndExtension("Dawnguard.esm"),
            ModKey.FromNameAndExtension("HearthFires.esm"),
            ModKey.FromNameAndExtension("Dragonborn.esm")
            // Add USSEP potentially? V1 didn't, so let's stick to that for now.
        };


        // --- Logging & State ---
        [Reactive] public string LogOutput { get; private set; } = string.Empty;
        [Reactive] public bool IsRunning { get; private set; }
        [Reactive] public double ProgressValue { get; private set; } = 0;
        [Reactive] public string ProgressText { get; private set; } = string.Empty;


        // --- Group Filtering ---
        public ObservableCollection<string> AvailableNpcGroups { get; } = new();
        [Reactive] public string SelectedNpcGroup { get; set; } = ALL_NPCS_GROUP;


        // --- Configuration (Mirrored from V1 for backend use) ---
        // These could be exposed in Settings View later if desired, or kept internal
        private bool ClearOutputDirectoryOnRun => true; // Example: default to true
        private bool AbortIfMissingFaceGen => true; // Example: default to true
        private bool AbortIfMissingExtraAssets => false; // Example: default to false
        private bool GetMissingExtraAssetsFromAvailableWinners => false; // Example: default to false
        private bool SuppressAllMissingFileWarnings => false; // Example: default to false
        private bool SuppressKnownMissingFileWarnings => true; // Example: default to true
        private bool HandleBSAFiles_Patching => true; // Example: default to true
        private bool CopyExtraAssets => true; // Example: default to true (common request)
        private bool FindExtraTexturesInNifs => true; // Example: default to true


        // --- Internal Data for Patching Run ---
        private HashSet<string> _pathsToIgnore = new();
        private Dictionary<string, HashSet<string>> _warningsToSuppress = new(); // Key: Plugin Name (lowercase), Value: Set of paths
        private HashSet<string> _warningsToSuppress_Global = new();
        private Dictionary<string, ModSetting> _modSettingsMap = new(); // Key: DisplayName, Value: ModSetting
        private string _currentRunOutputAssetPath = string.Empty;


        public ReactiveCommand<Unit, Unit> RunCommand { get; }

        public VM_Run(
            EnvironmentStateProvider environmentStateProvider,
            Settings settings,
            NpcConsistencyProvider consistencyProvider,
            Auxilliary auxilliary,
            VM_Settings vmSettings,
            Lazy<VM_Mods> lazyVmMods)
        {
            _environmentStateProvider = environmentStateProvider;
            _settings = settings;
            _consistencyProvider = consistencyProvider;
            _auxilliary = auxilliary;
            _vmSettings = vmSettings;
            _lazyVmMods = lazyVmMods;

            // Command can only execute if environment is valid and not already running
            var canExecute = this.WhenAnyValue(x => x.IsRunning, x => x._environmentStateProvider.EnvironmentIsValid,
                                               (running, valid) => !running && valid);

            RunCommand = ReactiveCommand.CreateFromTask(RunPatchingLogic, canExecute);

            // Update IsRunning status when command executes/completes
            RunCommand.IsExecuting.BindTo(this, x => x.IsRunning);

            // Log exceptions from the command
            RunCommand.ThrownExceptions.Subscribe(ex =>
            {
                AppendLog($"ERROR: {ex.GetType().Name} - {ex.Message}");
                AppendLog(ExceptionLogger.GetExceptionStack(ex)); // Use ExceptionLogger
                AppendLog("Patching failed.");
                ResetProgress();
            });

            // Update Available Groups when NpcGroupAssignments changes in settings
            UpdateAvailableGroups();

            // Subscribe to VM_Settings EnvironmentIsValid changes to refresh groups when env becomes valid
             _vmSettings.WhenAnyValue(x => x.EnvironmentIsValid)
                 .Where(isValid => isValid)
                 .ObserveOn(RxApp.MainThreadScheduler)
                 .Subscribe(_ => UpdateAvailableGroups());
             
             // Subscribe to group change messages
             MessageBus.Current.Listen<NpcGroupsChangedMessage>()
                 .ObserveOn(RxApp.MainThreadScheduler) // Ensure update happens on UI thread
                 .Subscribe(_ =>
                 {
                     AppendLog("NPC Groups potentially changed. Refreshing dropdown...");
                     UpdateAvailableGroups();
                 })
                 .DisposeWith(_disposables); // Add subscription to disposables
        }


        private void UpdateAvailableGroups()
        {
             // (Implementation remains the same as before)
             RxApp.MainThreadScheduler.Schedule(() =>
             {
                 string currentSelection = SelectedNpcGroup;
                 AvailableNpcGroups.Clear();
                 AvailableNpcGroups.Add(ALL_NPCS_GROUP);

                 if (_settings.NpcGroupAssignments != null)
                 {
                     var distinctGroups = _settings.NpcGroupAssignments.Values
                                             .SelectMany(set => set ?? Enumerable.Empty<string>())
                                             .Where(g => !string.IsNullOrWhiteSpace(g))
                                             .Select(g => g.Trim())
                                             .Distinct(StringComparer.OrdinalIgnoreCase)
                                             .OrderBy(g => g);

                     foreach (var group in distinctGroups)
                     {
                         AvailableNpcGroups.Add(group);
                     }
                 }

                 if (AvailableNpcGroups.Contains(currentSelection))
                 {
                     SelectedNpcGroup = currentSelection;
                 }
                 else
                 {
                     SelectedNpcGroup = ALL_NPCS_GROUP;
                 }
             });
        }

        private void ResetProgress() {
            ProgressValue = 0;
            ProgressText = string.Empty;
        }

        private void UpdateProgress(int current, int total, string message)
        {
             RxApp.MainThreadScheduler.Schedule(() =>
             {
                  if (total > 0) {
                      ProgressValue = (double)current / total * 100.0;
                      ProgressText = $"[{current}/{total}] {message}";
                  } else {
                      ProgressValue = 0;
                      ProgressText = message;
                  }
             });
        }

        private bool LoadAuxiliaryFiles()
        {
            AppendLog("Loading auxiliary configuration files...");
            bool success = true;
            // **Correction 3:** Use Resources path
            string resourcesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");

            // Load Ignored Paths
            _pathsToIgnore.Clear();
            string ignorePathFile = Path.Combine(resourcesPath, "Paths To Ignore.json"); // Corrected path
            if (File.Exists(ignorePathFile))
            {
                try
                {
                    var tempList = JSONhandler<List<string>>.LoadJSONFile(ignorePathFile, out bool loadSuccess, out string loadEx);
                    if (loadSuccess && tempList != null)
                    {
                         _pathsToIgnore = new HashSet<string>(tempList.Select(p => p.Replace(@"\\", @"\")), StringComparer.OrdinalIgnoreCase);
                         AppendLog($"Loaded {_pathsToIgnore.Count} paths to ignore.");
                    }
                    else { throw new Exception(loadEx); }
                }
                catch (Exception ex)
                {
                     // **Correction 4:** Use ExceptionLogger
                     AppendLog($"WARNING: Could not load or parse '{ignorePathFile}'. No paths will be ignored. Error: {ExceptionLogger.GetExceptionStack(ex)}");
                }
            } else {
                AppendLog($"INFO: Ignore paths file not found at '{ignorePathFile}'. No paths will be ignored.");
            }


            // Load Suppressed Warnings
            _warningsToSuppress.Clear();
            _warningsToSuppress_Global.Clear();
            string suppressWarningsFile = Path.Combine(resourcesPath, "Warnings To Suppress.json"); // Corrected path
            if (File.Exists(suppressWarningsFile))
            {
                 try
                 {
                     var tempList = JSONhandler<List<suppressedWarnings>>.LoadJSONFile(suppressWarningsFile, out bool loadSuccess, out string loadEx);
                     if (loadSuccess && tempList != null)
                     {
                         foreach(var sw in tempList)
                         {
                             var cleanedPaths = new HashSet<string>(sw.Paths.Select(p => p.Replace(@"\\", @"\")), StringComparer.OrdinalIgnoreCase);
                             string pluginKeyLower = sw.Plugin.ToLowerInvariant();

                             if(pluginKeyLower == "global")
                             {
                                 _warningsToSuppress_Global = cleanedPaths;
                             }
                             else
                             {
                                 _warningsToSuppress[pluginKeyLower] = cleanedPaths;
                             }
                         }
                         AppendLog($"Loaded suppressed warnings for {_warningsToSuppress.Count} specific plugins and global scope.");
                     }
                     else { throw new Exception(loadEx); }
                 }
                 catch (Exception ex)
                 {
                      // **Correction 4:** Use ExceptionLogger
                      AppendLog($"ERROR: Could not load or parse '{suppressWarningsFile}'. Suppressed warnings will not be used. Error: {ExceptionLogger.GetExceptionStack(ex)}");
                      success = false;
                 }
            } else {
                 AppendLog($"ERROR: Suppressed warnings file not found at '{suppressWarningsFile}'. Cannot proceed.");
                 success = false;
            }

            return success;
        }

        private bool ShouldSkipNpc(INpcGetter npc, string selectedGroup)
        {
             // (Implementation remains the same as before)
            if (selectedGroup == ALL_NPCS_GROUP) return false;

            if (_settings.NpcGroupAssignments != null &&
                _settings.NpcGroupAssignments.TryGetValue(npc.FormKey, out var assignedGroups) &&
                assignedGroups != null &&
                assignedGroups.Contains(selectedGroup, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        private async Task<(Dictionary<FormKey, ScreeningResult> screeningCache, List<string> invalidSelections)> ScreenSelectionsAsync()
        {
            AppendLog("\nStarting pre-run screening of NPC selections...");
            var screeningCache = new Dictionary<FormKey, ScreeningResult>();
            var invalidSelections = new List<string>(); // List of strings for user message
            var selections = _settings.SelectedAppearanceMods;

            if (selections == null || !selections.Any())
            {
                AppendLog("No selections to screen.");
                return (screeningCache, invalidSelections);
            }

            int totalToScreen = selections.Count;
            int currentScreened = 0;

            foreach (var kvp in selections)
            {
                currentScreened++;
                var npcFormKey = kvp.Key;
                var selectedModDisplayName = kvp.Value;
                string npcIdentifier = npcFormKey.ToString(); // Default identifier

                UpdateProgress(currentScreened, totalToScreen, $"Screening {npcIdentifier}");

                // 1. Resolve winning override (needed for context and potentially EasyNPC mode base)
                if (!_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey, out var winningNpcOverride))
                {
                    AppendLog($"  SCREENING WARNING: Could not resolve base/winning NPC {npcFormKey}. Skipping screening for this NPC.");
                    // Don't add to cache or invalid list if the base NPC itself is unresolvable
                    await Task.Delay(1); // Throttle slightly
                    continue;
                }
                npcIdentifier = $"{winningNpcOverride.Name?.String ?? winningNpcOverride.EditorID ?? npcFormKey.ToString()} ({npcFormKey})"; // Better identifier

                // 2. Find the ModSetting for the selection
                if (!_modSettingsMap.TryGetValue(selectedModDisplayName, out var appearanceModSetting))
                {
                    AppendLog($"  SCREENING ERROR: Cannot find Mod Setting '{selectedModDisplayName}' for NPC {npcIdentifier}. Invalid selection.");
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Mod Setting not found)");
                    // Add a placeholder invalid result to cache? Or just rely on the invalidSelections list? Let's rely on the list for now.
                    await Task.Delay(1);
                    continue;
                }

                // 3. Check for Plugin Record Override
                bool hasPluginOverride = false;
                INpcGetter? sourceNpcRecord = null;
                
                ModKey? specificAppearancePluginKey = null; // Track the *specific* key providing the override
                INpcGetter? specificSourceNpcRecord = null; // Track the *specific* record

                int overrideCount = 0;
                List<string> sourcePluginNames = new List<string>(); // For logging ambiguity

                if (appearanceModSetting.ModKeys.Any())
                {
                    // Resolve all contexts ONCE
                    var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcFormKey).ToList();

                    foreach(var potentialKey in appearanceModSetting.ModKeys)
                    {
                        if (potentialKey.IsNull) continue;
                        var context = contexts.FirstOrDefault(x => x.ModKey.Equals(potentialKey));

                        if (context != null && context.Record != null)
                        {
                            overrideCount++;
                            sourcePluginNames.Add(potentialKey.FileName);
                            if (overrideCount == 1) // Store the first one found
                            {
                                specificSourceNpcRecord = context.Record;
                                specificAppearancePluginKey = potentialKey;
                            }
                            else // Found more than one
                            {
                                // Clear the stored specifics as it's now ambiguous *for this selection*
                                specificSourceNpcRecord = null;
                                specificAppearancePluginKey = null;
                            }
                        }
                    }

                    if (overrideCount == 1)
                    {
                        hasPluginOverride = true; // Valid override found
                        AppendLog($"    Screening: Found unique plugin record override in {specificAppearancePluginKey.Value.FileName}. Using this as source.");
                    }
                    else if (overrideCount > 1)
                    {
                        // Ambiguous: Found in multiple plugins *within this ModSetting*
                        hasPluginOverride = false; // Treat as invalid for override purposes
                        AppendLog($"    SCREENING ERROR (Ambiguous): NPC {npcIdentifier} found in multiple plugins within ModSetting '{selectedModDisplayName}': {string.Join(", ", sourcePluginNames)}. Cannot determine unique source record.");
                        // This selection is invalid for plugin override, but might still be valid if FaceGen exists.
                        // Add to invalidSelections list *if* FaceGen is also missing later.
                    }
                    else // overrideCount == 0
                    {
                        AppendLog($"    Screening: No plugin record override found in any associated plugins ({string.Join(", ", appearanceModSetting.ModKeys.Select(k => k.FileName))}).");
                    }
                }
                else
                {
                     AppendLog($"    Screening: Mod Setting '{selectedModDisplayName}' has no associated plugin keys. Checking assets only.");
                }

                // 4. Check for FaceGen Assets
                var assetSourceDirs = appearanceModSetting.CorrespondingFolderPaths ?? new List<string>();
                bool hasFaceGen = false;
                if (assetSourceDirs.Any())
                {
                    // Use the existing FaceGenExists but pass the *effective* plugin key (could be null if only assets defined)
                    // Pass the original NPC FormKey for path generation
                    // Use the *specific* key found above, or the original NPC's key if no override was found
                    ModKey keyForFaceGenCheck = specificAppearancePluginKey ?? npcFormKey.ModKey;
                    hasFaceGen = FaceGenExists(npcFormKey, keyForFaceGenCheck, assetSourceDirs);
                    AppendLog($"    Screening: FaceGen assets found in source directories: {hasFaceGen}.");
                }
                else
                {
                    AppendLog($"    Screening: Mod Setting '{selectedModDisplayName}' has no asset source directories defined.");
                }


                // 5. Determine Validity and Cache Result
                bool isValid = hasPluginOverride || hasFaceGen;
                screeningCache[npcFormKey] = new ScreeningResult(
                    isValid,
                    hasPluginOverride,
                    hasFaceGen,
                    specificSourceNpcRecord,
                    winningNpcOverride, // Cache the winning override
                    appearanceModSetting,
                    specificAppearancePluginKey
                );

                if (!isValid)
                {
                    string reason = "";
                    if (overrideCount > 1) reason = "Ambiguous plugin sources";
                    else if (overrideCount == 0) reason = "No plugin override found";

                    if (!hasFaceGen)
                    {
                        if (!string.IsNullOrEmpty(reason)) reason += " and ";
                        reason += "No FaceGen assets found";
                    }
                    AppendLog($"  SCREENING INVALID: NPC {npcIdentifier} -> Selected Mod '{selectedModDisplayName}'. Reason: {reason}.");
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' ({reason})");
                }

                await Task.Delay(1); // Throttle loop slightly
            } // End foreach selection

            UpdateProgress(totalToScreen, totalToScreen, "Screening Complete.");
            AppendLog($"Screening finished. Found {invalidSelections.Count} invalid selections.");

            return (screeningCache, invalidSelections);
        }

        // Add Dispose method if not present
        public void Dispose()
        {
            _disposables.Dispose();
        }

        private async Task RunPatchingLogic()
        {
            LogOutput = string.Empty;
            ResetProgress();
            UpdateProgress(0, 1, "Initializing...");
            AppendLog("Starting patch generation...");

            // --- Pre-Run Checks ---
            if (!_environmentStateProvider.EnvironmentIsValid || _environmentStateProvider.LoadOrder == null)
             { AppendLog("Environment is not valid..."); ResetProgress(); return; }
            if (string.IsNullOrWhiteSpace(_settings.OutputDirectory))
             { AppendLog("Output Directory is not set..."); ResetProgress(); return; }
            
            // --- *** Save Mod Settings Before Proceeding *** ---
            try
            {
                var vmMods = _lazyVmMods.Value; // Resolve the VM_Mods instance
                if (vmMods == null)
                {
                    // This indicates a setup error in dependency injection
                    throw new InvalidOperationException("VM_Mods instance could not be resolved via Lazy<T>.");
                }
                vmMods.SaveModSettingsToModel(); // Call the save method directly
            }
            catch (Exception ex)
            {
                AppendLog($"CRITICAL ERROR: Failed to save Mod Settings before patching: {ExceptionLogger.GetExceptionStack(ex)}");
                AppendLog("Aborting patch generation as settings may be inconsistent.");
                ResetProgress();
                return; // Stop if save fails
            }
            // --- *** End Save Mod Settings *** ---

            // --- Now check if ModSettings list itself is populated after saving ---
            if (_settings.ModSettings == null || !_settings.ModSettings.Any())
            { AppendLog("No Mod Settings configured (or saved from Mods tab). Cannot determine asset sources."); ResetProgress(); return; }

            // --- Load Auxiliary Config Files ---
            if (!LoadAuxiliaryFiles())
             { AppendLog("Failed to load auxiliary files..."); ResetProgress(); return; }

            // --- Build Mod Settings Map ---
            _modSettingsMap = _settings.ModSettings
                .Where(ms => !string.IsNullOrWhiteSpace(ms.DisplayName))
                .GroupBy(ms => ms.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
             AppendLog($"Built lookup map for {_modSettingsMap.Count} unique Mod Settings.");

            // --- *** Pre-Run Screening (Requirement 2) *** ---
            UpdateProgress(0, 1, "Screening selections...");
            var (screeningCache, invalidSelections) = await ScreenSelectionsAsync();

            if (invalidSelections.Any())
            {
                var message = new StringBuilder($"Found {invalidSelections.Count} NPC selection(s) where the chosen appearance mod provides neither a plugin record override nor FaceGen assets:\n\n");
                message.AppendLine(string.Join("\n", invalidSelections.Take(15))); // Show first 15
                if (invalidSelections.Count > 15) message.AppendLine("[...]");
                message.AppendLine("\nThese selections will be skipped. Continue with the valid selections?");

                // Show confirmation dialog - Needs to run on UI thread if called from background
                bool continuePatching = false;
                // Use Dispatcher.Invoke to run synchronously on the UI thread and get the result back
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    System.Diagnostics.Debug.WriteLine($"[VM_Run.RunPatchingLogic] Showing MessageBox on Thread: {System.Threading.Thread.CurrentThread.ManagedThreadId}");
                    var result = MessageBox.Show(message.ToString(), "Invalid Selections Found", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    continuePatching = (result == MessageBoxResult.Yes);
                    System.Diagnostics.Debug.WriteLine($"[VM_Run.RunPatchingLogic] MessageBox result: {result}, continuePatching: {continuePatching}");
                });

                // Execution of RunPatchingLogic will pause here until the MessageBox is closed
                // because Dispatcher.Invoke is synchronous.

                if (!continuePatching)
                {
                    AppendLog("Patching cancelled by user due to invalid selections.");
                    ResetProgress();
                    return; // Exit RunPatchingLogic
                }
                AppendLog("Proceeding with valid selections, skipping invalid ones...");
            }
            // --- *** End Screening *** ---

            // --- Prepare Output Paths ---
            // (Logic for determining _currentRunOutputAssetPath remains the same)
            string baseOutputDirectory;
            bool isSpecifiedDirectory = false;
            var testSplit = _settings.OutputDirectory.Split(Path.DirectorySeparatorChar);
            if (testSplit.Length > 1 && Directory.Exists(_settings.OutputDirectory)) { baseOutputDirectory = _settings.OutputDirectory; isSpecifiedDirectory = true; }
            else if (testSplit.Length == 1) { baseOutputDirectory = Path.Combine(_settings.ModsFolder, _settings.OutputDirectory); }
            else { AppendLog("Error: Could not locate directory " + _settings.OutputDirectory); ResetProgress(); return; }

            _currentRunOutputAssetPath = baseOutputDirectory;
            if (_settings.AppendTimestampToOutputDirectory && !isSpecifiedDirectory)
            { string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss"); _currentRunOutputAssetPath = Path.Combine(baseOutputDirectory, timestamp); }
            AppendLog($"Using output asset directory: {_currentRunOutputAssetPath}");
            try { Directory.CreateDirectory(_currentRunOutputAssetPath); AppendLog("Ensured output asset directory exists."); }
            catch (Exception ex) { AppendLog($"ERROR: Could not create output asset directory... Aborting. Error: {ExceptionLogger.GetExceptionStack(ex)}"); ResetProgress(); return; }


            // --- Initialize Output Mod ---
            _environmentStateProvider.OutputMod = new SkyrimMod(ModKey.FromName(_environmentStateProvider.OutputPluginName, ModType.Plugin), _environmentStateProvider.SkyrimVersion);
             AppendLog($"Initialized output mod: {_environmentStateProvider.OutputPluginName}");

            // --- Clear Output Asset Directory ---
            if (ClearOutputDirectoryOnRun)
            { /* (Logic remains the same) */
                AppendLog("Clearing output asset directory...");
                try { ClearDirectory(_currentRunOutputAssetPath); AppendLog("Output asset directory cleared."); }
                catch (Exception ex) { AppendLog($"ERROR: Failed to clear output asset directory: {ExceptionLogger.GetExceptionStack(ex)}. Aborting."); ResetProgress(); return; }
            }
            
            // --- *** Data Collection for Custom Dependency Duplication *** ---
            // Key: ModKey of the appearance plugin that *sourced* the record/appearance
            // Value: List of records *in the output patch* that were created/modified using that source
            var sourcePluginToPatchedRecordsMap = new Dictionary<ModKey, List<IMajorRecordGetter>>();

            // --- Main Processing Loop (Using Screening Cache) ---
            AppendLog("\nProcessing Valid NPC Appearance Selections...");
            int processedCount = 0;
            int skippedCount = 0; // Counts skips *within* this loop (invalid were already accounted for)

            var selectionsToProcess = screeningCache.Where(kv => kv.Value.SelectionIsValid).ToList(); // Only process valid ones

            if (!selectionsToProcess.Any())
            {
                AppendLog("No valid NPC selections found or remaining after screening.");
            }
            else
            {
                int totalToProcess = selectionsToProcess.Count;
                for (int i = 0; i < totalToProcess; i++)
                {
                    var kvp = selectionsToProcess[i];
                    var npcFormKey = kvp.Key;
                    var result = kvp.Value; // The ScreeningResult

                    // Resolve necessary components from cache
                    var winningNpcOverride =
                        result.WinningNpcOverride; // Already resolved, guaranteed non-null if in cache
                    var appearanceModSetting = result.AppearanceModSetting; // Already looked up
                    var specificAppearancePluginKey =
                        result.SpecificAppearancePluginKey; // Cached specific key, might be null
                    var sourceNpc = result.SpecificSourceNpcRecord; // Cached specific record, might be null
                    string selectedModDisplayName =
                        appearanceModSetting?.DisplayName ?? "N/A"; // Get name from cached setting
                    string npcIdentifier =
                        $"{winningNpcOverride.Name?.String ?? winningNpcOverride.EditorID ?? npcFormKey.ToString()} ({npcFormKey})";

                    // Apply Group Filter (still needed)
                    if (ShouldSkipNpc(winningNpcOverride, SelectedNpcGroup))
                    {
                        AppendLog($"  Skipping {npcIdentifier} (Group Filter)...");
                        skippedCount++;
                        UpdateProgress(i + 1, totalToProcess, $"Skipped {npcIdentifier} (Group Filter)");
                        await Task.Delay(1);
                        continue;
                    }

                    UpdateProgress(i + 1, totalToProcess,
                        $"Processing {winningNpcOverride.EditorID ?? npcFormKey.ToString()}");
                    AppendLog($"- Processing: {npcIdentifier} -> Selected Mod: '{selectedModDisplayName}'");

                    Npc? patchNpc = null; // The NPC record to be placed in the patch
                    INpcGetter? npcForAssetLookup = null; // Which NPC record to use for finding *extra* assets
                    bool copyOnlyFaceGenAssets = false; // Flag for asset copying
                    bool usedPluginRecord = false; // Track if we used the plugin override

                    // --- *** Apply Logic Based on Screening Result (Requirement 1) *** ---
                    if (result.HasPluginRecordOverride && sourceNpc != null) // Scenario: Plugin override exists
                    {
                        AppendLog("    Source: Plugin Record Override");
                        npcForAssetLookup = sourceNpc; // Use appearance mod's record for extra asset lookup
                        usedPluginRecord = true;

                        switch (_settings.PatchingMode)
                        {
                            case PatchingMode.EasyNPC_Like:
                                AppendLog(
                                    $"      Mode: EasyNPC-Like. Patching winning override ({winningNpcOverride.FormKey.ModKey.FileName}) with appearance from {specificAppearancePluginKey?.FileName ?? "N/A"}.");
                                patchNpc =
                                    _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(winningNpcOverride);
                                CopyAppearanceData(sourceNpc, patchNpc);
                                break;
                            case PatchingMode.Default:
                            default:
                                AppendLog(
                                    $"      Mode: Default. Forwarding record from source plugin ({specificAppearancePluginKey?.FileName ?? "N/A"}).");
                                patchNpc = _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(sourceNpc);
                                break;
                        }
                    }
                    else if
                        (result.HasFaceGenAssets) // Scenario: Plugin override missing OR invalid, but FaceGen exists
                    {
                        AppendLog("    Source: FaceGen Assets Only");
                        copyOnlyFaceGenAssets = true; // Only copy FaceGen related assets

                        switch (_settings.PatchingMode)
                        {
                            case PatchingMode.EasyNPC_Like:
                                AppendLog(
                                    $"      Mode: EasyNPC-Like. Using winning override ({winningNpcOverride.FormKey.ModKey.FileName}) as base.");
                                patchNpc =
                                    _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(winningNpcOverride);
                                // No appearance data copied - we're using the winning override's data but replacing assets
                                break;
                            case PatchingMode.Default:
                            default:
                                AppendLog($"      Mode: Default. Using original record ({npcFormKey}) as base.");
                                // Resolve the original master record directly
                                if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey,
                                        out var baseNpcRecord))
                                {
                                    patchNpc =
                                        _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(baseNpcRecord);
                                }
                                else
                                {
                                    AppendLog(
                                        $"      ERROR: Could not resolve original master record for {npcFormKey}. Skipping this NPC.");
                                    skippedCount++;
                                    await Task.Delay(1);
                                    continue; // Skip if base record fails
                                }

                                break;
                        }
                        // Don't track plugin for dependencies here, as we didn't use its record override
                        // npcForAssetLookup remains null, asset copying should only handle FaceGen
                    }
                    else
                    {
                        // This case should have been filtered by the screening result, but handle defensively
                        AppendLog(
                            $"  UNEXPECTED: Selection for {npcIdentifier} was marked valid but has neither plugin record nor FaceGen. Skipping.");
                        skippedCount++;
                        await Task.Delay(1);
                        continue;
                    }
                    // --- *** End Scenario Logic *** ---

                    // --- *** Populate Map for Dependency Duplication *** ---
                    // Add the *record currently in the patch* (patchNpc) to the map,
                    // keyed by the *source appearance plugin*, but only if we actually used that plugin's *record data*.
                    // We don't add if only FaceGen assets were used, as record dependencies aren't relevant then.
                    // Use the *specific* key that provided the record override
                    if (usedPluginRecord && specificAppearancePluginKey.HasValue &&
                        !specificAppearancePluginKey.Value.IsNull &&
                        !BaseGamePlugins.Contains(specificAppearancePluginKey.Value))
                    {
                        ModKey sourceKey = specificAppearancePluginKey.Value;
                        if (!sourcePluginToPatchedRecordsMap.TryGetValue(sourceKey, out var recordList))
                        {
                            recordList = new List<IMajorRecordGetter>();
                            sourcePluginToPatchedRecordsMap[sourceKey] = recordList;
                        }

                        recordList.Add(patchNpc); // Add the record *from the patch*
                    }

                    // --- Copy Assets ---
                    if (patchNpc != null &&
                        appearanceModSetting != null) // Ensure we have a patch record and mod settings
                    {
                        // The key used for path generation should ideally be the one associated with the assets.
                        // If we used a plugin override, use its key. If FaceGen only, use the original NPC's key.
                        ModKey keyForFaceGenPath = result.HasPluginRecordOverride && specificAppearancePluginKey.HasValue ? specificAppearancePluginKey.Value : npcFormKey.ModKey;

                        // Determine if the NPC record being used for assets is templated
                        // If FaceGen only, templating isn't relevant to *asset copying* in the same way.
                        bool isEffectivelyTemplated = !copyOnlyFaceGenAssets && npcForAssetLookup != null &&
                                                      NPCisTemplated(npcForAssetLookup);

                        CopyNpcAssets(patchNpc, keyForFaceGenPath, npcForAssetLookup, appearanceModSetting,
                            isEffectivelyTemplated, copyOnlyFaceGenAssets);
                    }
                    else
                    {
                        AppendLog(
                            $"  ERROR: Could not proceed with asset copying due to missing patch record or mod setting for {npcIdentifier}.");
                        skippedCount++;
                        await Task.Delay(1);
                        continue;
                    }


                    processedCount++;
                    await Task.Delay(5);
                } // End For Loop
            } // End else (selectionsToProcess.Any())

            UpdateProgress(processedCount + skippedCount, processedCount + skippedCount, "Finalizing...");

            // --- *** Post-Processing: Duplicate Dependencies (Using Custom Function) *** ---
            // Initialize the *single* mapping dictionary that will accumulate results
            Dictionary<FormKey, FormKey> globalMapping = new();
            AppendLog($"\nDuplicating referenced records using custom function...");
            int totalDuplicatedCount = 0;

            // Iterate through the map we built
            foreach (var kvp in sourcePluginToPatchedRecordsMap)
            {
                 ModKey modToDuplicateFrom = kvp.Key;
                 List<IMajorRecordGetter> recordsInPatchSourcedFromMod = kvp.Value;

                 if (!recordsInPatchSourcedFromMod.Any()) continue; // Skip if list is empty

                 AppendLog($"  Processing dependencies for: {modToDuplicateFrom.FileName} (from {recordsInPatchSourcedFromMod.Count} patched records)");
                 try
                 {
                     // Call the custom extension method, passing the list of records from the *output patch*
                     // and the *accumulating* mapping dictionary by reference.
                     _environmentStateProvider.OutputMod.DuplicateFromOnlyReferencedGetters(
                         recordsInPatchSourcedFromMod,
                         _environmentStateProvider.LinkCache,
                         modToDuplicateFrom,
                         ref globalMapping // Pass by reference
                         // typesToInspect: null or empty array uses default behavior
                     );
                     // Note: The custom function internally handles adding to the mapping and removing old links
                     // We don't get a count back directly from this version. We can infer from mapping size change if needed.
                     AppendLog($"    Completed dependency processing for {modToDuplicateFrom.FileName}.");
                 }
                 catch (Exception ex)
                 {
                      AppendLog($"  ERROR duplicating dependencies for {modToDuplicateFrom.FileName}: {ExceptionLogger.GetExceptionStack(ex)}");
                      // Continue processing other plugins
                 }
            }
            AppendLog($"Finished dependency duplication. Total mappings created/accumulated: {globalMapping.Count}.");

            // --- *** Final Remapping (Potentially Redundant but Safe) *** ---
            // Although the custom function calls RemapLinks internally for the mappings *it* created,
            // calling it once more at the end with the *full* accumulated mapping ensures
            // that links in records processed *earlier* are correctly remapped if their
            // target was duplicated by a *later* call to the custom function.
            if (globalMapping.Any())
            {
                AppendLog("Performing final link remapping pass...");
                try
                {
                    _environmentStateProvider.OutputMod.RemapLinks(globalMapping);
                    AppendLog("Final remapping complete.");
                }
                catch (Exception ex)
                {
                     AppendLog($"ERROR during final remapping: {ExceptionLogger.GetExceptionStack(ex)}");
                }
            }

            // --- Final Steps (Save Output Mod) ---
            if (processedCount > 0 || globalMapping.Any())
            { /* (Logic remains the same) */
                AppendLog($"\nProcessed {processedCount} NPC(s).");
                if (skippedCount > 0) AppendLog($"{skippedCount} NPC(s) were skipped.");
                string outputPluginPath = Path.Combine(_currentRunOutputAssetPath, _environmentStateProvider.OutputPluginFileName );
                AppendLog($"Attempting to save output mod to: {outputPluginPath}");
                try { _environmentStateProvider.OutputMod.WriteToBinary(outputPluginPath); AppendLog($"Output mod saved successfully."); }
                catch (Exception ex) { AppendLog($"FATAL SAVE ERROR: Could not write output plugin: {ExceptionLogger.GetExceptionStack(ex)}"); AppendLog($"Output mod NOT saved."); ResetProgress(); return; }
            }
            else
            { /* (Logic remains the same) */
                AppendLog("\nNo NPC appearances processed or dependencies duplicated.");
                if (skippedCount > 0) AppendLog($"{skippedCount} NPC(s) were skipped.");
                AppendLog("Output mod not saved as no changes were made.");
            }

            AppendLog("\nPatch generation process completed.");
            UpdateProgress(processedCount + skippedCount, processedCount + skippedCount, "Finished.");
        } // End RunPatchingLogic

        // --- Helper Methods (Partially Revised) ---

        private void ClearDirectory(string path)
        {
            // (Implementation remains the same as before)
            DirectoryInfo di = new DirectoryInfo(path);
            if (!di.Exists) return;

            foreach (FileInfo file in di.EnumerateFiles()) file.Delete();
            foreach (DirectoryInfo dir in di.EnumerateDirectories())
            {
                string dirNameLower = dir.Name.ToLowerInvariant();
                if (dirNameLower == "meshes" || dirNameLower == "textures" || dirNameLower == "facegendata" || dirNameLower == "actors")
                { dir.Delete(true); }
                else { AppendLog($"  Skipping deletion of non-asset directory: {dir.Name}"); }
            }
        }


        /// <summary>
        /// Copies appearance-related fields from sourceNpc to targetNpc.
        /// Used only in EasyNPC-Like mode.
        /// </summary>
        private void CopyAppearanceData(INpcGetter sourceNpc, Npc targetNpc)
        {
            // (Implementation remains the same as before - this copies *appearance* fields)
            targetNpc.FaceMorph = sourceNpc.FaceMorph?.DeepCopy();
            targetNpc.FaceParts = sourceNpc.FaceParts?.DeepCopy();
            if (sourceNpc.HairColor != null && !sourceNpc.HairColor.IsNull) targetNpc.HairColor.SetTo(sourceNpc.HairColor); else targetNpc.HairColor.SetToNull();
            targetNpc.HeadParts.Clear(); targetNpc.HeadParts.AddRange(sourceNpc.HeadParts);
            if (sourceNpc.HeadTexture != null && !sourceNpc.HeadTexture.IsNull) targetNpc.HeadTexture.SetTo(sourceNpc.HeadTexture); else targetNpc.HeadTexture.SetToNull();
            targetNpc.Height = sourceNpc.Height;
            targetNpc.Weight = sourceNpc.Weight;
            targetNpc.TextureLighting = sourceNpc.TextureLighting;
            targetNpc.TintLayers.Clear(); targetNpc.TintLayers.AddRange(sourceNpc.TintLayers?.Select(t => t.DeepCopy()) ?? Enumerable.Empty<TintLayer>());
            if (sourceNpc.WornArmor != null && !sourceNpc.WornArmor.IsNull) targetNpc.WornArmor.SetTo(sourceNpc.WornArmor); else targetNpc.WornArmor.SetToNull();

            AppendLog($"      Copied appearance fields from {sourceNpc.FormKey.ModKey.FileName} to {targetNpc.FormKey} in patch.");
        }

        private bool NPCisTemplated(INpcGetter? npc)
        {
            // (Implementation remains the same as before)
            if (npc == null) return false;
            return !npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits);
        }

        /// <summary>
        /// Main asset copying orchestrator. Calls helpers for identification and copying.
        /// Handles scenarios where only FaceGen assets should be copied.
        /// </summary>
        /// <param name="npcInPatch">The NPC record being added/modified in the output patch.</param>
        /// <param name="keyForFaceGenPath">The ModKey to use when constructing FaceGen paths (usually from appearance plugin, or original if FaceGen only).</param>
        /// <param name="npcForExtraAssetLookup">The NPC record used to look up EXTRA assets (HeadParts, Armor). Null if only copying FaceGen.</param>
        /// <param name="appearanceModSetting">The ModSetting defining source paths.</param>
        /// <param name="isEffectivelyTemplated">Whether the source NPC uses template traits (relevant if npcForExtraAssetLookup is not null).</param>
        /// <param name="copyOnlyFaceGenAssets">If true, only copies FaceGen NIF/DDS and assets referenced within the FaceGen NIF.</param>
        private void CopyNpcAssets(
            Npc npcInPatch,
            ModKey keyForFaceGenPath, // Use this for constructing FaceGen paths
            INpcGetter? npcForExtraAssetLookup, // Null if FaceGen only
            ModSetting appearanceModSetting,
            bool isEffectivelyTemplated, // Only relevant if npcForExtraAssetLookup is not null
            bool copyOnlyFaceGenAssets)
        {
            AppendLog($"    Copying assets for {npcInPatch.EditorID ?? npcInPatch.FormKey.ToString()} from sources related to '{appearanceModSetting.DisplayName}'...");

            var assetSourceDirs = appearanceModSetting.CorrespondingFolderPaths ?? new List<string>();
            if (!assetSourceDirs.Any())
            {
                AppendLog($"      WARNING: Mod Setting '{appearanceModSetting.DisplayName}' has no Corresponding Folder Paths. Cannot copy assets.");
                return;
            }
            AppendLog($"      Asset source directories: {string.Join(", ", assetSourceDirs)}");

            // --- Identify Required Assets ---
            HashSet<string> meshesToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> texturesToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ModKey effectivePluginKey = keyForFaceGenPath; // Default key for asset lookup/warning context

            // 1. FaceGen Assets (Always check if requested, templating handled by caller passing null npcForExtraAssetLookup)
            // Construct FaceGen paths using the provided key
            var (faceMeshPath, faceTexPath) = GetFaceGenSubPathStrings(npcInPatch.FormKey); // Use the keyForFaceGenPath here!
            meshesToCopy.Add(faceMeshPath);
            texturesToCopy.Add(faceTexPath);
            AppendLog($"      Identified FaceGen paths (using key {keyForFaceGenPath.FileName}): {faceMeshPath}, {faceTexPath}");

            // Check FaceGen existence using the keyForFaceGenPath
            if (!FaceGenExists(npcInPatch.FormKey, keyForFaceGenPath, assetSourceDirs))
            {
                string errorMsg = $"Missing expected FaceGen for NPC {npcInPatch.EditorID ?? npcInPatch.FormKey.ToString()} in source directories using key {keyForFaceGenPath.FileName}.";
                AppendLog($"      WARNING: {errorMsg}");
                if (AbortIfMissingFaceGen)
                { throw new FileNotFoundException(errorMsg); }
                // If not aborting, remove the missing paths so we don't try to copy them later
                meshesToCopy.Remove(faceMeshPath);
                texturesToCopy.Remove(faceTexPath);
            }

            // 2. Extra Assets (Only if NOT FaceGen-only AND CopyExtraAssets is true)
            if (!copyOnlyFaceGenAssets && CopyExtraAssets && npcForExtraAssetLookup != null)
            {
                AppendLog($"      Identifying extra assets referenced by plugin record {npcForExtraAssetLookup.FormKey}...");
                GetAssetsReferencedByPlugin(npcForExtraAssetLookup, meshesToCopy, texturesToCopy);
                effectivePluginKey = npcForExtraAssetLookup.FormKey.ModKey; // Use this plugin key for BSA/warning context if looking up extra assets
            }
            else if (copyOnlyFaceGenAssets)
            {
                 AppendLog($"      Skipping extra asset identification (FaceGen Only mode).");
            }
            else if (!CopyExtraAssets)
            {
                 AppendLog($"      Skipping extra asset identification (CopyExtraAssets disabled).");
            }
             else if (npcForExtraAssetLookup == null)
            {
                 AppendLog($"      Skipping extra asset identification (No source NPC record provided).");
            }


            // --- Handle BSAs ---
            HashSet<string> extractedMeshFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> extractedTextureFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (HandleBSAFiles_Patching)
            {
                AppendLog($"      Checking BSAs associated with plugin key {effectivePluginKey.FileName} in source directories...");
                UnpackAssetsFromBSA(meshesToCopy, texturesToCopy, extractedMeshFiles, extractedTextureFiles, effectivePluginKey, assetSourceDirs, _currentRunOutputAssetPath);
                AppendLog($"      Extracted {extractedMeshFiles.Count} meshes and {extractedTextureFiles.Count} textures from BSAs.");
            }

            // --- Handle NIF Scanning ---
            // Scan NIFs if CopyExtraAssets OR copyOnlyFaceGenAssets (because FaceGen NIF itself needs scanning) AND FindExtraTexturesInNifs is true
            if ((CopyExtraAssets || copyOnlyFaceGenAssets) && FindExtraTexturesInNifs)
            {
                 AppendLog($"      Scanning NIF files for additional texture references...");
                 HashSet<string> texturesFromNifs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                 HashSet<string> alreadyHandledTextures = new HashSet<string>(texturesToCopy, StringComparer.OrdinalIgnoreCase);
                 alreadyHandledTextures.UnionWith(extractedTextureFiles);

                 // Scan loose NIFs (check all source dirs)
                 GetExtraTexturesFromNifSet(meshesToCopy, assetSourceDirs, texturesFromNifs, alreadyHandledTextures);
                 // Scan extracted NIFs (check output dir)
                 GetExtraTexturesFromNifSet(extractedMeshFiles, new List<string> { _currentRunOutputAssetPath }, texturesFromNifs, alreadyHandledTextures);

                 AppendLog($"        Found {texturesFromNifs.Count} additional textures in NIFs.");

                 // Try extracting newly found textures from BSAs
                 if (HandleBSAFiles_Patching && texturesFromNifs.Any())
                 {
                     HashSet<string> newlyExtractedNifTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                     UnpackAssetsFromBSA(new HashSet<string>(), texturesFromNifs, new HashSet<string>(), newlyExtractedNifTextures, effectivePluginKey, assetSourceDirs, _currentRunOutputAssetPath);
                     AppendLog($"        Extracted {newlyExtractedNifTextures.Count} of these additional textures from BSAs.");
                     texturesFromNifs.ExceptWith(newlyExtractedNifTextures);
                 }
                 texturesToCopy.UnionWith(texturesFromNifs);
            }
            else {
                AppendLog($"      Skipping NIF scanning for textures.");
            }


            // --- Copy Loose Files ---
            AppendLog($"      Copying {meshesToCopy.Count} loose mesh files...");
            CopyAssetFiles(assetSourceDirs, meshesToCopy, "Meshes", effectivePluginKey.FileName.String);

            AppendLog($"      Copying {texturesToCopy.Count} loose texture files...");
            CopyAssetFiles(assetSourceDirs, texturesToCopy, "Textures", effectivePluginKey.FileName.String);

            AppendLog($"    Finished asset copying for {npcInPatch.EditorID ?? npcInPatch.FormKey.ToString()}.");
        }


        // --- Asset Identification Helpers (No changes needed here) ---
        private void GetAssetsReferencedByPlugin(INpcGetter npc, HashSet<string> meshPaths, HashSet<string> texturePaths)
        { /* Implementation remains the same */
             if (npc.HeadParts != null) foreach (var hpLink in npc.HeadParts) GetHeadPartAssetPaths(hpLink, texturePaths, meshPaths);
             if (!npc.WornArmor.IsNull && npc.WornArmor.TryResolve(_environmentStateProvider.LinkCache, out var wornArmorGetter) && wornArmorGetter.Armature != null)
                foreach (var aaLink in wornArmorGetter.Armature) GetARMAAssetPaths(aaLink, texturePaths, meshPaths);
        }
        private void GetHeadPartAssetPaths(IFormLinkGetter<IHeadPartGetter> hpLink, HashSet<string> texturePaths, HashSet<string> meshPaths)
        { /* Implementation remains the same */
            if (hpLink.IsNull || !hpLink.TryResolve(_environmentStateProvider.LinkCache, out var hpGetter)) return;
            if (hpGetter.Model?.File != null) meshPaths.Add(hpGetter.Model.File);
            if (hpGetter.Parts != null) foreach (var part in hpGetter.Parts) if (part?.FileName != null) meshPaths.Add(part.FileName);
            if (!hpGetter.TextureSet.IsNull) GetTextureSetPaths(hpGetter.TextureSet, texturePaths);
            if (hpGetter.ExtraParts != null) foreach (var extraPartLink in hpGetter.ExtraParts) GetHeadPartAssetPaths(extraPartLink, texturePaths, meshPaths);
        }
        private void GetARMAAssetPaths(IFormLinkGetter<IArmorAddonGetter> aaLink, HashSet<string> texturePaths, HashSet<string> meshPaths)
        { /* Implementation remains the same */
             if (aaLink.IsNull || !aaLink.TryResolve(_environmentStateProvider.LinkCache, out var aaGetter)) return;
             if (aaGetter.WorldModel?.Male?.File != null) meshPaths.Add(aaGetter.WorldModel.Male.File);
             if (aaGetter.WorldModel?.Female?.File != null) meshPaths.Add(aaGetter.WorldModel.Female.File);
             if (!aaGetter.SkinTexture?.Male.IsNull ?? false) GetTextureSetPaths(aaGetter.SkinTexture.Male, texturePaths);
             if (!aaGetter.SkinTexture?.Female.IsNull ?? false) GetTextureSetPaths(aaGetter.SkinTexture.Female, texturePaths);
        }
        private void GetTextureSetPaths(IFormLinkGetter<ITextureSetGetter> txstLink, HashSet<string> texturePaths)
        { /* Implementation remains the same */
             if (txstLink.IsNull || !txstLink.TryResolve(_environmentStateProvider.LinkCache, out var txstGetter)) return;
             if (!string.IsNullOrEmpty(txstGetter.Diffuse?.GivenPath)) texturePaths.Add(txstGetter.Diffuse.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.NormalOrGloss?.GivenPath)) texturePaths.Add(txstGetter.NormalOrGloss.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.EnvironmentMaskOrSubsurfaceTint?.GivenPath)) texturePaths.Add(txstGetter.EnvironmentMaskOrSubsurfaceTint.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.GlowOrDetailMap?.GivenPath)) texturePaths.Add(txstGetter.GlowOrDetailMap.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.Height?.GivenPath)) texturePaths.Add(txstGetter.Height.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.Environment?.GivenPath)) texturePaths.Add(txstGetter.Environment.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.Multilayer?.GivenPath)) texturePaths.Add(txstGetter.Multilayer.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.BacklightMaskOrSpecular?.GivenPath)) texturePaths.Add(txstGetter.BacklightMaskOrSpecular.GivenPath);
        }
        /// <summary>
        /// Gets the relative file paths for FaceGen NIF and DDS files,
        /// ensuring the FormID component is an 8-character, zero-padded hex string.
        /// </summary>
        /// <param name="npcFormKey">The FormKey of the NPC.</param>
        /// <returns>A tuple containing the relative mesh path and texture path (lowercase).</returns>
        private (string MeshPath, string TexturePath) GetFaceGenSubPathStrings(FormKey npcFormKey)
        {
            // Get the plugin filename string
            string pluginFileName = npcFormKey.ModKey.FileName.String; // Use .String property

            // Get the Form ID and format it as an 8-character uppercase hex string (X8)
            string formIDHex = npcFormKey.ID.ToString("X8"); // e.g., 0001A696

            // Construct the paths
            string meshPath = $"actors\\character\\facegendata\\facegeom\\{pluginFileName}\\{formIDHex}.nif";
            string texPath = $"actors\\character\\facegendata\\facetint\\{pluginFileName}\\{formIDHex}.dds";

            // Return lowercase paths for case-insensitive comparisons later
            return (meshPath.ToLowerInvariant(), texPath.ToLowerInvariant());
        }

        /// <summary>
        /// Checks if FaceGen files exist loose or in BSAs within the provided source directories.
        /// **Revised for Clarification 2.**
        /// </summary>
               private bool FaceGenExists(FormKey npcFormKey, ModKey appearancePluginKey, List<string> assetSourceDirs)
        {
            var (faceMeshPath, faceTexPath) = GetFaceGenSubPathStrings(npcFormKey);
            bool nifFound = false;
            bool ddsFound = false;

            // Check Loose Files (Iterate all source dirs)
            foreach (var sourceDir in assetSourceDirs)
            {
                if (!nifFound && File.Exists(Path.Combine(sourceDir, "meshes", faceMeshPath))) nifFound = true;
                if (!ddsFound && File.Exists(Path.Combine(sourceDir, "textures", faceTexPath))) ddsFound = true;
                if (nifFound && ddsFound) break;
            }

            // Check BSAs (if not found loose and BSA handling is enabled)
            if ((!nifFound || !ddsFound) && HandleBSAFiles_Patching)
            {
                // Iterate source directories *backwards*
                for (int i = assetSourceDirs.Count - 1; i >= 0; i--)
                {
                    var sourceDir = assetSourceDirs[i];
                    HashSet<IArchiveReader> readers = BsaHandler.OpenBsaArchiveReaders(sourceDir, appearancePluginKey);
                    if (readers.Any())
                    {
                        string bsaMeshPath = Path.Combine("meshes", faceMeshPath).Replace('/', '\\');
                        string bsaTexPath = Path.Combine("textures", faceTexPath).Replace('/', '\\');

                        // Check *this specific set* of readers using HaveFile
                        // *** Use HaveFile here (out var _ discards the file) ***
                        if (!nifFound && BsaHandler.HaveFile(bsaMeshPath, readers, out _)) nifFound = true;
                        if (!ddsFound && BsaHandler.HaveFile(bsaTexPath, readers, out _)) ddsFound = true;

                        if (nifFound && ddsFound) break; // Found both
                    }
                }
            }
            return nifFound && ddsFound;
        }


        // --- Asset Copying/Extraction Helpers (Revised for Clarification 2) ---

        /// <summary>
        /// Extracts assets from BSAs found in any of the assetSourceDirs, prioritizing later directories.
        /// **Revised for Clarification 2.**
        /// </summary>
                private void UnpackAssetsFromBSA(
            HashSet<string> MeshesToExtract, HashSet<string> TexturesToExtract,
            HashSet<string> extractedMeshes, HashSet<string> extractedTextures,
            ModKey currentPluginKey, List<string> assetSourceDirs, string targetAssetPath)
        {
            if (!assetSourceDirs.Any()) return;

            var foundMeshSources = new Dictionary<string, (IArchiveFile file, string dest)>(StringComparer.OrdinalIgnoreCase);
            var foundTextureSources = new Dictionary<string, (IArchiveFile file, string dest)>(StringComparer.OrdinalIgnoreCase);

            // Iterate source directories *backwards*
            for (int i = assetSourceDirs.Count - 1; i >= 0; i--)
            {
                string sourceDir = assetSourceDirs[i];
                var readers = BsaHandler.OpenBsaArchiveReaders(sourceDir, currentPluginKey); // Use V2's OpenBsaArchiveReaders
                if (!readers.Any()) continue;

                // Check remaining meshes
                foreach (string subPath in MeshesToExtract.ToList())
                {
                    if (foundMeshSources.ContainsKey(subPath)) continue;

                    string bsaMeshPath = Path.Combine("meshes", subPath).Replace('/', '\\');
                    // *** Use HaveFile here ***
                    if (BsaHandler.HaveFile(bsaMeshPath, readers, out var file) && file != null)
                    {
                        foundMeshSources[subPath] = (file, Path.Combine(targetAssetPath, "meshes", subPath));
                    }
                }

                // Check remaining textures
                foreach (string subPath in TexturesToExtract.ToList())
                {
                    if (foundTextureSources.ContainsKey(subPath)) continue;

                    string bsaTexPath = Path.Combine("textures", subPath).Replace('/', '\\');
                     // *** Use HaveFile here ***
                    if (BsaHandler.HaveFile(bsaTexPath, readers, out var file) && file != null)
                    {
                        foundTextureSources[subPath] = (file, Path.Combine(targetAssetPath, "textures", subPath));
                    }
                }
            }

            // Extract winning sources
            foreach (var kvp in foundMeshSources)
            {
                string subPath = kvp.Key;
                if (BsaHandler.ExtractFileFromBsa(kvp.Value.file, kvp.Value.dest)) // Assumes ExtractFileFromBSA returns bool
                {
                    extractedMeshes.Add(subPath);
                    MeshesToExtract.Remove(subPath);
                } else { AppendLog($"          ERROR extracting winning BSA mesh: {subPath}"); }
            }
            foreach (var kvp in foundTextureSources)
            {
                string subPath = kvp.Key;
                if (BsaHandler.ExtractFileFromBsa(kvp.Value.file, kvp.Value.dest)) // Assumes ExtractFileFromBSA returns bool
                {
                    extractedTextures.Add(subPath);
                    TexturesToExtract.Remove(subPath);
                } else { AppendLog($"          ERROR extracting winning BSA texture: {subPath}"); }
            }
        }


        /// <summary>
        /// Scans NIFs found in the source directories for textures.
        /// **Revised for Clarification 2.**
        /// </summary>
        private void GetExtraTexturesFromNifSet(HashSet<string> nifSubPaths, List<string> sourceBaseDirs, HashSet<string> outputTextures, HashSet<string> ignoredTextures)
        {
             int foundCount = 0;
             foreach (var nifPathRelative in nifSubPaths)
             {
                 if (!nifPathRelative.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) continue;

                 string? foundNifFullPath = null;
                 // Iterate source dirs to find the NIF file
                 foreach(var baseDir in sourceBaseDirs)
                 {
                     string potentialPath = Path.Combine(baseDir, "meshes", nifPathRelative);
                     if(File.Exists(potentialPath))
                     {
                         foundNifFullPath = potentialPath;
                         break; // Found it
                     }
                 }

                 if (foundNifFullPath != null)
                 {
                     try
                     {
                         var nifTextures = NifHandler.GetExtraTexturesFromNif(foundNifFullPath);
                         foreach (var texPathRelative in nifTextures)
                         {
                             if (!ignoredTextures.Contains(texPathRelative) && !IsIgnored(texPathRelative, _pathsToIgnore))
                             {
                                 if (outputTextures.Add(texPathRelative)) foundCount++;
                             }
                         }
                     }
                     catch (Exception ex)
                     { AppendLog($"        WARNING: Failed to scan NIF '{foundNifFullPath}': {ExceptionLogger.GetExceptionStack(ex)}"); } // Use logger
                 }
             }
        }

        /// <summary>
        /// Copies loose asset files, checking all source directories.
        /// **Revised for Clarification 2.**
        /// </summary>
        private void CopyAssetFiles(List<string> sourceDataPaths, HashSet<string> assetRelativePathList, string assetType /*"Meshes" or "Textures"*/, string sourcePluginName)
        {
            string outputBase = Path.Combine(_currentRunOutputAssetPath, assetType);
            Directory.CreateDirectory(outputBase);

            var warningsToSuppressSet = _warningsToSuppress_Global;
            string pluginKeyLower = sourcePluginName.ToLowerInvariant();
            if (_warningsToSuppress.TryGetValue(pluginKeyLower, out var specificWarnings)) { warningsToSuppressSet = specificWarnings; }

            foreach (string relativePath in assetRelativePathList)
            {
                if (IsIgnored(relativePath, _pathsToIgnore)) continue;

                string? foundSourcePath = null;
                // Check Source Directories
                foreach (var sourcePathBase in sourceDataPaths)
                {
                     string potentialPath = Path.Combine(sourcePathBase, assetType, relativePath);
                     if(File.Exists(potentialPath))
                     {
                         foundSourcePath = potentialPath;
                         break; // Found it
                     }
                }

                // Check Game Data Folder (if configured and not FaceGen)
                bool isFaceGen = relativePath.Contains("facegendata", StringComparison.OrdinalIgnoreCase);
                if (foundSourcePath == null && GetMissingExtraAssetsFromAvailableWinners && !isFaceGen)
                {
                     string gameDataPath = Path.Combine(_environmentStateProvider.DataFolderPath.ToString(), assetType, relativePath);
                     if (File.Exists(gameDataPath))
                     {
                         foundSourcePath = gameDataPath;
                         AppendLog($"        Found missing asset '{relativePath}' in game data folder.");
                     }
                }

                if (foundSourcePath == null)
                {
                    // Handle Missing File Warning/Error
                    bool suppressWarning = SuppressAllMissingFileWarnings ||
                                          (SuppressKnownMissingFileWarnings && warningsToSuppressSet.Contains(relativePath)) ||
                                          GetExtensionOfMissingFile(relativePath) == ".tri";
                     if (!suppressWarning)
                     {
                          string errorMsg = $"Asset '{relativePath}' not found in any source directories";
                          if(GetMissingExtraAssetsFromAvailableWinners && !isFaceGen) errorMsg += " or game data folder";
                          errorMsg += $" (needed by {sourcePluginName}).";
                          AppendLog($"      WARNING: {errorMsg}");
                          if (AbortIfMissingExtraAssets && !isFaceGen)
                          { throw new FileNotFoundException(errorMsg, relativePath); }
                     }
                }
                else
                {
                     // Copy the found file
                     string destPath = Path.Combine(outputBase, relativePath);
                     try
                     {
                          FileInfo fileInfo = Auxilliary.CreateDirectoryIfNeeded(destPath, Auxilliary.PathType.File);
                          File.Copy(foundSourcePath, destPath, true);
                     }
                     catch (Exception ex)
                     { AppendLog($"      ERROR copying '{foundSourcePath}' to '{destPath}': {ExceptionLogger.GetExceptionStack(ex)}"); } // Use logger
                }
            }
        }


        private bool IsIgnored(string relativePath, HashSet<string> toIgnore)
        {
            // (Implementation remains the same as before)
             return toIgnore.Contains(relativePath);
        }

        private string GetExtensionOfMissingFile(string input)
        {
            // (Implementation remains the same as before)
             if (string.IsNullOrEmpty(input)) { return ""; }
             return Path.GetExtension(input).ToLowerInvariant();
        }

        private void AppendLog(string message)
        {
             // (Implementation remains the same as before)
             RxApp.MainThreadScheduler.Schedule(() =>
             {
                 LogOutput += message + Environment.NewLine;
             });
        }
    }


    // Temporary class matching V1's structure for loading JSON
    public class suppressedWarnings
    {
        public string Plugin { get; set; } = "";
        public HashSet<string> Paths { get; set; } = new HashSet<string>();
    }

    // *** Need an Adapter or Modification for BSAHandler ***
    // Add a method like this to your BSAHandler class:
    /*
    public static class BSAHandler // Assuming static class
    {
        // ... other methods ...

        /// <summary>
        /// Tries to find a file within any BSA reader in the provided set.
        /// </summary>
        /// <returns>True if found, false otherwise. Outputs the file if found.</returns>
        public static bool GetFileFromReaders(string subpath, HashSet<IArchiveReader> bsaReaders, out IArchiveFile? file)
        {
            file = null;
            foreach (var reader in bsaReaders)
            {
                // Assuming TryGetFile exists and checks reader.Files case-insensitively
                if (TryGetFile(subpath, reader, out file))
                {
                    return true; // Found it
                }
            }
            return false; // Not found in any reader in this set
        }

        // Make sure ExtractFileFromBSA returns bool indicating success/failure
        public static bool ExtractFileFromBSA(IArchiveFile file, string destPath)
        {
            string? dirPath = Path.GetDirectoryName(destPath);
            if (dirPath == null) return false; // Invalid destination path

            try
            {
                Directory.CreateDirectory(dirPath); // Ensure directory exists
                using (var fileStream = File.Create(destPath)) // Use 'using' for proper disposal
                {
                    file.CopyDataTo(fileStream);
                }
                return true; // Success
            }
            catch (Exception ex) // Catch specific exceptions if needed (IOException, UnauthorizedAccessException)
            {
                Console.WriteLine($"==========================================================================================================");
                Console.WriteLine($"Could not extract file from BSA: {file.Path} to {destPath}. Error: {ExceptionLogger.GetExceptionStack(ex)}");
                Console.WriteLine($"==========================================================================================================");
                // Optionally re-throw or log differently
                return false; // Failure
            }
        }
    }
    */
}