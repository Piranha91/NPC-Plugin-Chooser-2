// VM_Run.cs
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
using System.Reflection;
using System.Windows;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Noggog;
using NPC_Plugin_Chooser_2.Views; // Needed for LinkCache Interface

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

        // --- Data for Race Handling ---
        private Dictionary<FormKey, List<(ModKey SourceMod, IRaceGetter RaceRecord, FormKey NpcFormKeyWhoCausedIt)>>
            _vanillaRaceModifications = new();

        private List<RaceSerializationInfo>
            _racesToSerializeForYaml = new(); // For ForwardWinningOverrides (Default) YAML generation
        
        private Dictionary<INpcGetter, RaceSerializationInfo> _raceSerializationNpcAssignments = new();

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
        [Reactive] public bool IsVerboseModeEnabled { get; set; } = false; // Default to non-verbose


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

        private Dictionary<string, HashSet<string>>
            _warningsToSuppress = new(); // Key: Plugin Name (lowercase), Value: Set of paths

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
                AppendLog($"ERROR: {ex.GetType().Name} - {ex.Message}", true);
                AppendLog(ExceptionLogger.GetExceptionStack(ex), true); // Use ExceptionLogger
                AppendLog("ERROR: Patching failed.", true);
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
                    AppendLog("NPC Groups potentially changed. Refreshing dropdown..."); // Verbose only
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

        private void ResetProgress()
        {
            ProgressValue = 0;
            ProgressText = string.Empty;
        }

        private void UpdateProgress(int current, int total, string message)
        {
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                if (total > 0)
                {
                    ProgressValue = (double)current / total * 100.0;
                    ProgressText = $"[{current}/{total}] {message}";
                }
                else
                {
                    ProgressValue = 0;
                    ProgressText = message;
                }
            });
        }

        private bool LoadAuxiliaryFiles()
        {
            AppendLog("Loading auxiliary configuration files..."); // Verbose only
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
                    var tempList =
                        JSONhandler<List<string>>.LoadJSONFile(ignorePathFile, out bool loadSuccess, out string loadEx);
                    if (loadSuccess && tempList != null)
                    {
                        _pathsToIgnore = new HashSet<string>(tempList.Select(p => p.Replace(@"\\", @"\")),
                            StringComparer.OrdinalIgnoreCase);
                        AppendLog($"Loaded {_pathsToIgnore.Count} paths to ignore."); // Verbose only
                    }
                    else
                    {
                        throw new Exception(loadEx);
                    }
                }
                catch (Exception ex)
                {
                    // **Correction 4:** Use ExceptionLogger
                    AppendLog(
                        $"WARNING: Could not load or parse '{ignorePathFile}'. No paths will be ignored. Error: {ExceptionLogger.GetExceptionStack(ex)}"); // Verbose only (Warning)
                }
            }
            else
            {
                AppendLog(
                    $"INFO: Ignore paths file not found at '{ignorePathFile}'. No paths will be ignored."); // Verbose only
            }


            // Load Suppressed Warnings
            _warningsToSuppress.Clear();
            _warningsToSuppress_Global.Clear();
            string suppressWarningsFile = Path.Combine(resourcesPath, "Warnings To Suppress.json"); // Corrected path
            if (File.Exists(suppressWarningsFile))
            {
                try
                {
                    var tempList = JSONhandler<List<SuppressedWarnings>>.LoadJSONFile(suppressWarningsFile,
                        out bool loadSuccess, out string loadEx);
                    if (loadSuccess && tempList != null)
                    {
                        foreach (var sw in tempList)
                        {
                            var cleanedPaths = new HashSet<string>(sw.Paths.Select(p => p.Replace(@"\\", @"\")),
                                StringComparer.OrdinalIgnoreCase);
                            string pluginKeyLower = sw.Plugin.ToLowerInvariant();

                            if (pluginKeyLower == "global")
                            {
                                _warningsToSuppress_Global = cleanedPaths;
                            }
                            else
                            {
                                _warningsToSuppress[pluginKeyLower] = cleanedPaths;
                            }
                        }

                        AppendLog(
                            $"Loaded suppressed warnings for {_warningsToSuppress.Count} specific plugins and global scope."); // Verbose only
                    }
                    else
                    {
                        throw new Exception(loadEx);
                    }
                }
                catch (Exception ex)
                {
                    // **Correction 4:** Use ExceptionLogger
                    AppendLog(
                        $"ERROR: Could not load or parse '{suppressWarningsFile}'. Suppressed warnings will not be used. Error: {ExceptionLogger.GetExceptionStack(ex)}",
                        true);
                    success = false;
                }
            }
            else
            {
                AppendLog($"ERROR: Suppressed warnings file not found at '{suppressWarningsFile}'. Cannot proceed.",
                    true);
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

        private async Task<(Dictionary<FormKey, ScreeningResult> screeningCache, List<string> invalidSelections)>
            ScreenSelectionsAsync()
        {
            AppendLog("\nStarting pre-run screening of NPC selections..."); // Verbose only
            var screeningCache = new Dictionary<FormKey, ScreeningResult>();
            var invalidSelections = new List<string>(); // List of strings for user message
            var selections = _settings.SelectedAppearanceMods;

            if (selections == null || !selections.Any())
            {
                AppendLog("No selections to screen."); // Verbose only
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
                    AppendLog(
                        $"  SCREENING WARNING: Could not resolve base/winning NPC {npcFormKey}. Skipping screening for this NPC."); // Verbose only (Warning)
                    // Don't add to cache or invalid list if the base NPC itself is unresolvable
                    await Task.Delay(1); // Throttle slightly
                    continue;
                }

                npcIdentifier =
                    $"{winningNpcOverride.Name?.String ?? winningNpcOverride.EditorID ?? npcFormKey.ToString()} ({npcFormKey})"; // Better identifier

                // 2. Find the ModSetting for the selection
                if (!_modSettingsMap.TryGetValue(selectedModDisplayName, out var appearanceModSetting))
                {
                    AppendLog(
                        $"  SCREENING ERROR: Cannot find Mod Setting '{selectedModDisplayName}' for NPC {npcIdentifier}. Invalid selection.",
                        true);
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

                List<string> sourcePluginNames = new List<string>(); // For logging ambiguity

                var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcFormKey)
                    .ToList();

                if (appearanceModSetting.NpcPluginDisambiguation.TryGetValue(npcFormKey, out ModKey disambiguation))
                {
                    if (_environmentStateProvider.LoadOrder.ContainsKey(disambiguation))
                    {
                        var disambiguatedContext = contexts.FirstOrDefault(x => x.ModKey == disambiguation);
                        if (disambiguatedContext != null)
                        {
                            specificAppearancePluginKey = disambiguatedContext.ModKey;
                            specificSourceNpcRecord = disambiguatedContext.Record;
                            hasPluginOverride = true;
                            AppendLog(
                                $"    Screening: Found assigned plugin record override in {specificAppearancePluginKey.Value.FileName}. Using this as source."); // Verbose only
                        }
                        else
                        {
                            AppendLog(
                                $"    Screening: Source plugin is set to {disambiguation.FileName} but this plugin doesn't contain this NPC. Falling back to first available plugin"); // Verbose only
                        }
                    }
                    else
                    {
                        AppendLog(
                            $"    Screening: Source plugin is set to {disambiguation.FileName} but this plugin is not in the load order. Falling back to first available plugin"); // Verbose only
                    }
                }

                if (!hasPluginOverride)
                {
                    if (appearanceModSetting.AvailablePluginsForNpcs.TryGetValue(npcFormKey, out var availableModKeys))
                    {
                        foreach (var plugin in availableModKeys)
                        {
                            var context = contexts.FirstOrDefault(x => x.ModKey == plugin);
                            if (context != null)
                            {
                                specificAppearancePluginKey = context.ModKey;
                                specificSourceNpcRecord = context.Record;
                                hasPluginOverride = true;
                                AppendLog(
                                    $"    Screening: Selected plugin record override in {specificAppearancePluginKey.Value.FileName}. Using this as source."); // Verbose only
                                break;
                            }
                        }
                    }
                    else
                    {
                        AppendLog(
                            $"    Screening: Mod Setting '{selectedModDisplayName}' has no associated plugin keys. Checking assets only."); // Verbose only
                    }
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
                    AppendLog(
                        $"    Screening: FaceGen assets found in source directories: {hasFaceGen}."); // Verbose only
                }
                else
                {
                    AppendLog(
                        $"    Screening: Mod Setting '{selectedModDisplayName}' has no asset source directories defined."); // Verbose only
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
                    if (!hasPluginOverride)
                    {
                        if (!string.IsNullOrEmpty(reason)) reason += " and ";
                        reason += "No plugin override found";
                    }

                    if (!hasFaceGen)
                    {
                        if (!string.IsNullOrEmpty(reason)) reason += " and ";
                        reason += "No FaceGen assets found";
                    }

                    AppendLog(
                        $"ERROR: SCREENING INVALID: NPC {npcIdentifier} -> Selected Mod '{selectedModDisplayName}'. Reason: {reason}.",
                        true);
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' ({reason})");
                }

                await Task.Delay(1); // Throttle loop slightly
            } // End foreach selection

            UpdateProgress(totalToScreen, totalToScreen, "Screening Complete.");
            AppendLog(
                $"Screening finished. Found {invalidSelections.Count} invalid selections."); // Verbose only (summarizes errors previously logged)

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
            AppendLog("Starting patch generation..."); // Verbose only

            // --- Pre-Run Checks ---
            if (!_environmentStateProvider.EnvironmentIsValid || _environmentStateProvider.LoadOrder == null)
            {
                AppendLog("ERROR: Environment is not valid. Aborting.", true);
                ResetProgress();
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.OutputDirectory))
            {
                AppendLog("ERROR: Output Directory is not set. Aborting.", true);
                ResetProgress();
                return;
            }

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
                AppendLog(
                    $"CRITICAL ERROR: Failed to save Mod Settings before patching: {ExceptionLogger.GetExceptionStack(ex)}",
                    true);
                AppendLog("ERROR: Aborting patch generation as settings may be inconsistent.", true);
                ResetProgress();
                return; // Stop if save fails
            }
            // --- *** End Save Mod Settings *** ---

            // --- Now check if ModSettings list itself is populated after saving ---
            if (_settings.ModSettings == null || !_settings.ModSettings.Any())
            {
                AppendLog(
                    "ERROR: No Mod Settings configured (or saved from Mods tab). Cannot determine asset sources. Aborting.",
                    true);
                ResetProgress();
                return;
            }

            // --- Load Auxiliary Config Files ---
            if (!LoadAuxiliaryFiles())
            {
                AppendLog("ERROR: Failed to load auxiliary files. Aborting.", true);
                ResetProgress();
                return;
            }

            // --- Build Mod Settings Map ---
            _modSettingsMap = _settings.ModSettings
                .Where(ms => !string.IsNullOrWhiteSpace(ms.DisplayName))
                .GroupBy(ms => ms.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            AppendLog($"Built lookup map for {_modSettingsMap.Count} unique Mod Settings."); // Verbose only

            // --- *** Pre-Run Screening (Requirement 2) *** ---
            UpdateProgress(0, 1, "Screening selections...");
            var (screeningCache, invalidSelections) = await ScreenSelectionsAsync();

            if (invalidSelections.Any())
            {
                var message =
                    new StringBuilder(
                        $"Found {invalidSelections.Count} NPC selection(s) where the chosen appearance mod provides neither a plugin record override nor FaceGen assets:\n\n");
                message.AppendLine(string.Join("\n", invalidSelections.Take(15))); // Show first 15
                if (invalidSelections.Count > 15) message.AppendLine("[...]");
                message.AppendLine("\nThese selections will be skipped. Continue with the valid selections?");

                // Show confirmation dialog - Needs to run on UI thread if called from background
                bool continuePatching = false;
                // Use Dispatcher.Invoke to run synchronously on the UI thread and get the result back
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[VM_Run.RunPatchingLogic] Showing MessageBox on Thread: {System.Threading.Thread.CurrentThread.ManagedThreadId}");
                    continuePatching = ScrollableMessageBox.Confirm(message.ToString(), "Invalid Selections Found");
                    System.Diagnostics.Debug.WriteLine(
                        $"[VM_Run.RunPatchingLogic] continuePatching: {continuePatching}");
                });

                // Execution of RunPatchingLogic will pause here until the MessageBox is closed
                // because Dispatcher.Invoke is synchronous.

                if (!continuePatching)
                {
                    AppendLog("Patching cancelled by user due to invalid selections."); // Verbose only
                    ResetProgress();
                    return; // Exit RunPatchingLogic
                }

                AppendLog("Proceeding with valid selections, skipping invalid ones..."); // Verbose only
            }
            // --- *** End Screening *** ---

            // --- Prepare Output Paths ---
            // (Logic for determining _currentRunOutputAssetPath remains the same)
            string baseOutputDirectory;
            bool isSpecifiedDirectory = false;
            var testSplit = _settings.OutputDirectory.Split(Path.DirectorySeparatorChar);
            if (testSplit.Length > 1 && Directory.Exists(_settings.OutputDirectory))
            {
                baseOutputDirectory = _settings.OutputDirectory;
                isSpecifiedDirectory = true;
            }
            else if (testSplit.Length == 1)
            {
                baseOutputDirectory = Path.Combine(_settings.ModsFolder, _settings.OutputDirectory);
            }
            else
            {
                AppendLog("ERROR: Could not locate directory " + _settings.OutputDirectory, true);
                ResetProgress();
                return;
            }

            _currentRunOutputAssetPath = baseOutputDirectory;
            if (_settings.AppendTimestampToOutputDirectory && !isSpecifiedDirectory)
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _currentRunOutputAssetPath = Path.Combine(baseOutputDirectory, timestamp);
            }

            AppendLog($"Using output asset directory: {_currentRunOutputAssetPath}"); // Verbose only
            try
            {
                Directory.CreateDirectory(_currentRunOutputAssetPath);
                AppendLog("Ensured output asset directory exists."); /* Verbose only */
            }
            catch (Exception ex)
            {
                AppendLog(
                    $"ERROR: Could not create output asset directory... Aborting. Error: {ExceptionLogger.GetExceptionStack(ex)}",
                    true);
                ResetProgress();
                return;
            }


            // --- Initialize Output Mod ---
            _environmentStateProvider.OutputMod = new SkyrimMod(
                ModKey.FromName(_environmentStateProvider.OutputPluginName, ModType.Plugin),
                _environmentStateProvider.SkyrimVersion);
            AppendLog($"Initialized output mod: {_environmentStateProvider.OutputPluginName}"); // Verbose only

            // --- Clear Output Asset Directory ---
            if (ClearOutputDirectoryOnRun)
            {
                /* (Logic remains the same) */
                AppendLog("Clearing output asset directory..."); // Verbose only
                try
                {
                    ClearDirectory(_currentRunOutputAssetPath);
                    AppendLog("Output asset directory cleared."); /* Verbose only */
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"ERROR: Failed to clear output asset directory: {ExceptionLogger.GetExceptionStack(ex)}. Aborting.",
                        true);
                    ResetProgress();
                    return;
                }
            }

            // --- *** Data Collection for Custom Dependency Duplication *** ---
            // Key: ModKey of the appearance plugin that *sourced* the record/appearance
            // Value: List of records *in the output patch* that were created/modified using that source
            var sourcePluginToPatchedRecordsMap = new Dictionary<ModKey, List<IMajorRecordGetter>>();

            // --- Main Processing Loop (Using Screening Cache) ---
            AppendLog("\nProcessing Valid NPC Appearance Selections..."); // Verbose only
            int processedCount = 0;
            int skippedCount = 0; // Counts skips *within* this loop (invalid were already accounted for)

            var selectionsToProcess =
                screeningCache.Where(kv => kv.Value.SelectionIsValid).ToList(); // Only process valid ones

            if (!selectionsToProcess.Any())
            {
                AppendLog("No valid NPC selections found or remaining after screening."); // Verbose only
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
                        AppendLog($"  Skipping {npcIdentifier} (Group Filter)..."); // Verbose only
                        skippedCount++;
                        UpdateProgress(i + 1, totalToProcess, $"Skipped {npcIdentifier} (Group Filter)");
                        await Task.Delay(1);
                        continue;
                    }

                    UpdateProgress(i + 1, totalToProcess,
                        $"Processing {winningNpcOverride.EditorID ?? npcFormKey.ToString()}");
                    AppendLog(
                        $"- Processing: {npcIdentifier} -> Selected Mod: '{selectedModDisplayName}'"); // Verbose only

                    Npc? patchNpc = null; // The NPC record to be placed in the patch
                    INpcGetter? npcForAssetLookup = null; // Which NPC record to use for finding *extra* assets
                    bool copyOnlyFaceGenAssets = false; // Flag for asset copying
                    bool usedPluginRecord = false; // Track if we used the plugin override

                    // --- *** Apply Logic Based on Screening Result (Requirement 1) *** ---
                    if (result.HasPluginRecordOverride && sourceNpc != null) // Scenario: Plugin override exists
                    {
                        AppendLog("    Source: Plugin Record Override"); // Verbose only
                        npcForAssetLookup = sourceNpc; // Use appearance mod's record for extra asset lookup
                        usedPluginRecord = true;

                        switch (_settings.PatchingMode)
                        {
                            case PatchingMode.EasyNPC_Like:
                                AppendLog(
                                    $"      Mode: EasyNPC-Like. Patching winning override ({winningNpcOverride.FormKey.ModKey.FileName}) with appearance from {specificAppearancePluginKey?.FileName ?? "N/A"}."); // Verbose only
                                patchNpc =
                                    _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(winningNpcOverride);
                                CopyAppearanceData(sourceNpc, patchNpc);
                                break;
                            case PatchingMode.Default:
                            default:
                                AppendLog(
                                    $"      Mode: Default. Forwarding record from source plugin ({specificAppearancePluginKey?.FileName ?? "N/A"})."); // Verbose only
                                patchNpc = _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(sourceNpc);
                                break;
                        }
                    }
                    else if
                        (result.HasFaceGenAssets) // Scenario: Plugin override missing OR invalid, but FaceGen exists
                    {
                        AppendLog("    Source: FaceGen Assets Only"); // Verbose only
                        copyOnlyFaceGenAssets = true; // Only copy FaceGen related assets

                        switch (_settings.PatchingMode)
                        {
                            case PatchingMode.EasyNPC_Like:
                                AppendLog(
                                    $"      Mode: EasyNPC-Like. Using winning override ({winningNpcOverride.FormKey.ModKey.FileName}) as base."); // Verbose only
                                patchNpc =
                                    _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(winningNpcOverride);
                                // No appearance data copied - we're using the winning override's data but replacing assets
                                break;
                            case PatchingMode.Default:
                            default:
                                AppendLog(
                                    $"      Mode: Default. Using original record ({npcFormKey}) as base."); // Verbose only
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
                                        $"      ERROR: Could not resolve original master record for {npcFormKey}. Skipping this NPC.",
                                        true);
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
                            $"ERROR: UNEXPECTED: Selection for {npcIdentifier} was marked valid but has neither plugin record nor FaceGen. Skipping.",
                            true);
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
                        ModKey keyForFaceGenPath =
                            result.HasPluginRecordOverride && specificAppearancePluginKey.HasValue
                                ? specificAppearancePluginKey.Value
                                : npcFormKey.ModKey;

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
                            $"ERROR: Could not proceed with asset copying due to missing patch record or mod setting for {npcIdentifier}.",
                            true);
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
            AppendLog($"\nDuplicating referenced records using custom function..."); // Verbose only
            int totalDuplicatedCount = 0;

            // Iterate through the map we built
            foreach (var kvp in sourcePluginToPatchedRecordsMap)
            {
                ModKey modToDuplicateFrom = kvp.Key;
                List<IMajorRecordGetter> recordsInPatchSourcedFromMod = kvp.Value;

                if (!recordsInPatchSourcedFromMod.Any()) continue; // Skip if list is empty

                AppendLog(
                    $"  Processing dependencies for: {modToDuplicateFrom.FileName} (from {recordsInPatchSourcedFromMod.Count} patched records)"); // Verbose only
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
                    AppendLog(
                        $"    Completed dependency processing for {modToDuplicateFrom.FileName}."); // Verbose only
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"  ERROR duplicating dependencies for {modToDuplicateFrom.FileName}: {ExceptionLogger.GetExceptionStack(ex)}",
                        true);
                    // Continue processing other plugins
                }
            }

            AppendLog(
                $"Finished dependency duplication. Total mappings created/accumulated: {globalMapping.Count}."); // Verbose only

            // --- *** Final Remapping (Potentially Redundant but Safe) *** ---
            // Although the custom function calls RemapLinks internally for the mappings *it* created,
            // calling it once more at the end with the *full* accumulated mapping ensures
            // that links in records processed *earlier* are correctly remapped if their
            // target was duplicated by a *later* call to the custom function.
            if (globalMapping.Any())
            {
                AppendLog("Performing final link remapping pass..."); // Verbose only
                try
                {
                    _environmentStateProvider.OutputMod.RemapLinks(globalMapping);
                    AppendLog("Final remapping complete."); // Verbose only
                }
                catch (Exception ex)
                {
                    AppendLog($"ERROR during final remapping: {ExceptionLogger.GetExceptionStack(ex)}", true);
                }
            }

            // --- Final Steps (Save Output Mod) ---
            if (processedCount > 0 || globalMapping.Any())
            {
                AppendLog($"\nProcessed {processedCount} NPC(s).", false, true); // Force log
                if (skippedCount > 0) AppendLog($"{skippedCount} NPC(s) were skipped.", false, true); // Force log
                string outputPluginPath = Path.Combine(_currentRunOutputAssetPath,
                    _environmentStateProvider.OutputPluginFileName);
                AppendLog($"Attempting to save output mod to: {outputPluginPath}", true); // Force log
                try
                {
                    _environmentStateProvider.OutputMod.WriteToBinary(outputPluginPath);
                    AppendLog($"Output mod saved successfully.", false, true); // Force log
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"FATAL SAVE ERROR: Could not write output plugin: {ExceptionLogger.GetExceptionStack(ex)}",
                        true); // isError true, will log
                    AppendLog($"ERROR: Output mod NOT saved.", true); // isError true, will log
                    ResetProgress();
                    return;
                }
            }
            else
            {
                AppendLog("\nNo NPC appearances processed or dependencies duplicated.", false, true); // Force log
                if (skippedCount > 0) AppendLog($"{skippedCount} NPC(s) were skipped.", false, true); // Force log
                AppendLog("Output mod not saved as no changes were made.", false, true); // Force log
            }

            AppendLog("\nPatch generation process completed.", false, true); // Force log
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
                if (dirNameLower == "meshes" || dirNameLower == "textures" || dirNameLower == "facegendata" ||
                    dirNameLower == "actors")
                {
                    dir.Delete(true);
                }
                else
                {
                    AppendLog($"  Skipping deletion of non-asset directory: {dir.Name}");
                } // Verbose only
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
            if (sourceNpc.HairColor != null && !sourceNpc.HairColor.IsNull)
                targetNpc.HairColor.SetTo(sourceNpc.HairColor);
            else targetNpc.HairColor.SetToNull();
            targetNpc.HeadParts.Clear();
            targetNpc.HeadParts.AddRange(sourceNpc.HeadParts);
            if (sourceNpc.HeadTexture != null && !sourceNpc.HeadTexture.IsNull)
                targetNpc.HeadTexture.SetTo(sourceNpc.HeadTexture);
            else targetNpc.HeadTexture.SetToNull();
            targetNpc.Height = sourceNpc.Height;
            targetNpc.Weight = sourceNpc.Weight;
            targetNpc.TextureLighting = sourceNpc.TextureLighting;
            targetNpc.TintLayers.Clear();
            targetNpc.TintLayers.AddRange(sourceNpc.TintLayers?.Select(t => t.DeepCopy()) ??
                                          Enumerable.Empty<TintLayer>());
            if (sourceNpc.WornArmor != null && !sourceNpc.WornArmor.IsNull)
                targetNpc.WornArmor.SetTo(sourceNpc.WornArmor);
            else targetNpc.WornArmor.SetToNull();

            AppendLog(
                $"      Copied appearance fields from {sourceNpc.FormKey.ModKey.FileName} to {targetNpc.FormKey} in patch."); // Verbose only
        }

        private bool NPCisTemplated(INpcGetter? npc)
        {
            // (Implementation remains the same as before)
            if (npc == null) return false;
            return !npc.Template.IsNull &&
                   npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits);
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
            AppendLog(
                $"    Copying assets for {npcInPatch.EditorID ?? npcInPatch.FormKey.ToString()} from sources related to '{appearanceModSetting.DisplayName}'..."); // Verbose only

            var assetSourceDirs = appearanceModSetting.CorrespondingFolderPaths ?? new List<string>();
            if (!assetSourceDirs.Any())
            {
                AppendLog(
                    $"      WARNING: Mod Setting '{appearanceModSetting.DisplayName}' has no Corresponding Folder Paths. Cannot copy assets."); // Verbose only (Warning)
                return;
            }

            AppendLog($"      Asset source directories: {string.Join(", ", assetSourceDirs)}"); // Verbose only

            // --- Identify Required Assets ---
            HashSet<string> meshesToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> texturesToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ModKey effectivePluginKey = keyForFaceGenPath; // Default key for asset lookup/warning context

            // 1. FaceGen Assets (Always check if requested, templating handled by caller passing null npcForExtraAssetLookup)
            // Construct FaceGen paths using the provided key
            var (faceMeshPath, faceTexPath) =
                GetFaceGenSubPathStrings(npcInPatch.FormKey); // Use the keyForFaceGenPath here!
            meshesToCopy.Add(faceMeshPath);
            texturesToCopy.Add(faceTexPath);
            AppendLog(
                $"      Identified FaceGen paths (using key {keyForFaceGenPath.FileName}): {faceMeshPath}, {faceTexPath}"); // Verbose only

            // Check FaceGen existence using the keyForFaceGenPath
            if (!FaceGenExists(npcInPatch.FormKey, keyForFaceGenPath, assetSourceDirs))
            {
                string errorMsg =
                    $"Missing expected FaceGen for NPC {npcInPatch.EditorID ?? npcInPatch.FormKey.ToString()} in source directories using key {keyForFaceGenPath.FileName}.";
                AppendLog($"      WARNING: {errorMsg}"); // Verbose only (Warning)
                if (AbortIfMissingFaceGen)
                {
                    throw new FileNotFoundException(errorMsg);
                }

                // If not aborting, remove the missing paths so we don't try to copy them later
                meshesToCopy.Remove(faceMeshPath);
                texturesToCopy.Remove(faceTexPath);
            }

            // 2. Extra Assets (Only if NOT FaceGen-only AND CopyExtraAssets is true)
            if (!copyOnlyFaceGenAssets && CopyExtraAssets && npcForExtraAssetLookup != null)
            {
                AppendLog(
                    $"      Identifying extra assets referenced by plugin record {npcForExtraAssetLookup.FormKey}..."); // Verbose only
                GetAssetsReferencedByPlugin(npcForExtraAssetLookup, meshesToCopy, texturesToCopy);
                effectivePluginKey =
                    npcForExtraAssetLookup.FormKey
                        .ModKey; // Use this plugin key for BSA/warning context if looking up extra assets
            }
            else if (copyOnlyFaceGenAssets)
            {
                AppendLog($"      Skipping extra asset identification (FaceGen Only mode)."); // Verbose only
            }
            else if (!CopyExtraAssets)
            {
                AppendLog($"      Skipping extra asset identification (CopyExtraAssets disabled)."); // Verbose only
            }
            else if (npcForExtraAssetLookup == null)
            {
                AppendLog(
                    $"      Skipping extra asset identification (No source NPC record provided)."); // Verbose only
            }


            // --- Handle BSAs ---
            HashSet<string> extractedMeshFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> extractedTextureFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (HandleBSAFiles_Patching)
            {
                AppendLog(
                    $"      Checking BSAs associated with plugin key {effectivePluginKey.FileName} in source directories..."); // Verbose only
                UnpackAssetsFromBSA(meshesToCopy, texturesToCopy, extractedMeshFiles, extractedTextureFiles,
                    effectivePluginKey, assetSourceDirs, _currentRunOutputAssetPath);
                AppendLog(
                    $"      Extracted {extractedMeshFiles.Count} meshes and {extractedTextureFiles.Count} textures from BSAs."); // Verbose only
            }

            // --- Handle NIF Scanning ---
            // Scan NIFs if CopyExtraAssets OR copyOnlyFaceGenAssets (because FaceGen NIF itself needs scanning) AND FindExtraTexturesInNifs is true
            if ((CopyExtraAssets || copyOnlyFaceGenAssets) && FindExtraTexturesInNifs)
            {
                AppendLog($"      Scanning NIF files for additional texture references..."); // Verbose only
                HashSet<string> texturesFromNifs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> alreadyHandledTextures =
                    new HashSet<string>(texturesToCopy, StringComparer.OrdinalIgnoreCase);
                alreadyHandledTextures.UnionWith(extractedTextureFiles);

                // Scan loose NIFs (check all source dirs)
                GetExtraTexturesFromNifSet(meshesToCopy, assetSourceDirs, texturesFromNifs, alreadyHandledTextures);
                // Scan extracted NIFs (check output dir)
                GetExtraTexturesFromNifSet(extractedMeshFiles, new List<string> { _currentRunOutputAssetPath },
                    texturesFromNifs, alreadyHandledTextures);

                AppendLog($"        Found {texturesFromNifs.Count} additional textures in NIFs."); // Verbose only

                // Try extracting newly found textures from BSAs
                if (HandleBSAFiles_Patching && texturesFromNifs.Any())
                {
                    HashSet<string> newlyExtractedNifTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    UnpackAssetsFromBSA(new HashSet<string>(), texturesFromNifs, new HashSet<string>(),
                        newlyExtractedNifTextures, effectivePluginKey, assetSourceDirs, _currentRunOutputAssetPath);
                    AppendLog(
                        $"        Extracted {newlyExtractedNifTextures.Count} of these additional textures from BSAs."); // Verbose only
                    texturesFromNifs.ExceptWith(newlyExtractedNifTextures);
                }

                texturesToCopy.UnionWith(texturesFromNifs);
            }
            else
            {
                AppendLog($"      Skipping NIF scanning for textures."); // Verbose only
            }


            // --- Copy Loose Files ---
            AppendLog($"      Copying {meshesToCopy.Count} loose mesh files..."); // Verbose only
            CopyAssetFiles(assetSourceDirs, meshesToCopy, "Meshes", effectivePluginKey.FileName.String);

            AppendLog($"      Copying {texturesToCopy.Count} loose texture files..."); // Verbose only
            CopyAssetFiles(assetSourceDirs, texturesToCopy, "Textures", effectivePluginKey.FileName.String);

            AppendLog(
                $"    Finished asset copying for {npcInPatch.EditorID ?? npcInPatch.FormKey.ToString()}."); // Verbose only
        }


        // --- Asset Identification Helpers (No changes needed here) ---
        private void GetAssetsReferencedByPlugin(INpcGetter npc, HashSet<string> meshPaths,
            HashSet<string> texturePaths)
        {
            /* Implementation remains the same */
            if (npc.HeadParts != null)
                foreach (var hpLink in npc.HeadParts)
                    GetHeadPartAssetPaths(hpLink, texturePaths, meshPaths);
            if (!npc.WornArmor.IsNull &&
                npc.WornArmor.TryResolve(_environmentStateProvider.LinkCache, out var wornArmorGetter) &&
                wornArmorGetter.Armature != null)
                foreach (var aaLink in wornArmorGetter.Armature)
                    GetARMAAssetPaths(aaLink, texturePaths, meshPaths);
        }

        private void GetHeadPartAssetPaths(IFormLinkGetter<IHeadPartGetter> hpLink, HashSet<string> texturePaths,
            HashSet<string> meshPaths)
        {
            /* Implementation remains the same */
            if (hpLink.IsNull || !hpLink.TryResolve(_environmentStateProvider.LinkCache, out var hpGetter)) return;
            if (hpGetter.Model?.File != null) meshPaths.Add(hpGetter.Model.File);
            if (hpGetter.Parts != null)
                foreach (var part in hpGetter.Parts)
                    if (part?.FileName != null)
                        meshPaths.Add(part.FileName);
            if (!hpGetter.TextureSet.IsNull) GetTextureSetPaths(hpGetter.TextureSet, texturePaths);
            if (hpGetter.ExtraParts != null)
                foreach (var extraPartLink in hpGetter.ExtraParts)
                    GetHeadPartAssetPaths(extraPartLink, texturePaths, meshPaths);
        }

        private void GetARMAAssetPaths(IFormLinkGetter<IArmorAddonGetter> aaLink, HashSet<string> texturePaths,
            HashSet<string> meshPaths)
        {
            /* Implementation remains the same */
            if (aaLink.IsNull || !aaLink.TryResolve(_environmentStateProvider.LinkCache, out var aaGetter)) return;
            if (aaGetter.WorldModel?.Male?.File != null) meshPaths.Add(aaGetter.WorldModel.Male.File);
            if (aaGetter.WorldModel?.Female?.File != null) meshPaths.Add(aaGetter.WorldModel.Female.File);
            if (!aaGetter.SkinTexture?.Male.IsNull ?? false)
                GetTextureSetPaths(aaGetter.SkinTexture.Male, texturePaths);
            if (!aaGetter.SkinTexture?.Female.IsNull ?? false)
                GetTextureSetPaths(aaGetter.SkinTexture.Female, texturePaths);
        }

        private void GetTextureSetPaths(IFormLinkGetter<ITextureSetGetter> txstLink, HashSet<string> texturePaths)
        {
            /* Implementation remains the same */
            if (txstLink.IsNull ||
                !txstLink.TryResolve(_environmentStateProvider.LinkCache, out var txstGetter)) return;
            if (!string.IsNullOrEmpty(txstGetter.Diffuse?.GivenPath)) texturePaths.Add(txstGetter.Diffuse.GivenPath);
            if (!string.IsNullOrEmpty(txstGetter.NormalOrGloss?.GivenPath))
                texturePaths.Add(txstGetter.NormalOrGloss.GivenPath);
            if (!string.IsNullOrEmpty(txstGetter.EnvironmentMaskOrSubsurfaceTint?.GivenPath))
                texturePaths.Add(txstGetter.EnvironmentMaskOrSubsurfaceTint.GivenPath);
            if (!string.IsNullOrEmpty(txstGetter.GlowOrDetailMap?.GivenPath))
                texturePaths.Add(txstGetter.GlowOrDetailMap.GivenPath);
            if (!string.IsNullOrEmpty(txstGetter.Height?.GivenPath)) texturePaths.Add(txstGetter.Height.GivenPath);
            if (!string.IsNullOrEmpty(txstGetter.Environment?.GivenPath))
                texturePaths.Add(txstGetter.Environment.GivenPath);
            if (!string.IsNullOrEmpty(txstGetter.Multilayer?.GivenPath))
                texturePaths.Add(txstGetter.Multilayer.GivenPath);
            if (!string.IsNullOrEmpty(txstGetter.BacklightMaskOrSpecular?.GivenPath))
                texturePaths.Add(txstGetter.BacklightMaskOrSpecular.GivenPath);
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

            var foundMeshSources =
                new Dictionary<string, (IArchiveFile file, string dest)>(StringComparer.OrdinalIgnoreCase);
            var foundTextureSources =
                new Dictionary<string, (IArchiveFile file, string dest)>(StringComparer.OrdinalIgnoreCase);

            // Iterate source directories *backwards*
            for (int i = assetSourceDirs.Count - 1; i >= 0; i--)
            {
                string sourceDir = assetSourceDirs[i];
                var readers =
                    BsaHandler.OpenBsaArchiveReaders(sourceDir, currentPluginKey); // Use V2's OpenBsaArchiveReaders
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
                if (BsaHandler.ExtractFileFromBsa(kvp.Value.file,
                        kvp.Value.dest)) // Assumes ExtractFileFromBSA returns bool
                {
                    extractedMeshes.Add(subPath);
                    MeshesToExtract.Remove(subPath);
                }
                else
                {
                    AppendLog($"ERROR: Failed to extract winning BSA mesh: {subPath}", true);
                }
            }

            foreach (var kvp in foundTextureSources)
            {
                string subPath = kvp.Key;
                if (BsaHandler.ExtractFileFromBsa(kvp.Value.file,
                        kvp.Value.dest)) // Assumes ExtractFileFromBSA returns bool
                {
                    extractedTextures.Add(subPath);
                    TexturesToExtract.Remove(subPath);
                }
                else
                {
                    AppendLog($"ERROR: Failed to extract winning BSA texture: {subPath}", true);
                }
            }
        }


        /// <summary>
        /// Scans NIFs found in the source directories for textures.
        /// **Revised for Clarification 2.**
        /// </summary>
        private void GetExtraTexturesFromNifSet(HashSet<string> nifSubPaths, List<string> sourceBaseDirs,
            HashSet<string> outputTextures, HashSet<string> ignoredTextures)
        {
            int foundCount = 0;
            foreach (var nifPathRelative in nifSubPaths)
            {
                if (!nifPathRelative.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) continue;

                string? foundNifFullPath = null;
                // Iterate source dirs to find the NIF file
                foreach (var baseDir in sourceBaseDirs)
                {
                    string potentialPath = Path.Combine(baseDir, "meshes", nifPathRelative);
                    if (File.Exists(potentialPath))
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
                            if (!ignoredTextures.Contains(texPathRelative) &&
                                !IsIgnored(texPathRelative, _pathsToIgnore))
                            {
                                if (outputTextures.Add(texPathRelative)) foundCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog(
                            $"        WARNING: Failed to scan NIF '{foundNifFullPath}': {ExceptionLogger.GetExceptionStack(ex)}");
                    } // Verbose only (Warning)
                }
            }
        }

        /// <summary>
        /// Copies loose asset files, checking all source directories.
        /// **Revised for Clarification 2.**
        /// </summary>
        private void CopyAssetFiles(List<string> sourceDataPaths, HashSet<string> assetRelativePathList,
            string assetType /*"Meshes" or "Textures"*/, string sourcePluginName)
        {
            string outputBase = Path.Combine(_currentRunOutputAssetPath, assetType);
            Directory.CreateDirectory(outputBase);

            var warningsToSuppressSet = _warningsToSuppress_Global;
            string pluginKeyLower = sourcePluginName.ToLowerInvariant();
            if (_warningsToSuppress.TryGetValue(pluginKeyLower, out var specificWarnings))
            {
                warningsToSuppressSet = specificWarnings;
            }

            foreach (string relativePath in assetRelativePathList)
            {
                if (IsIgnored(relativePath, _pathsToIgnore)) continue;

                string? foundSourcePath = null;
                // Check Source Directories
                foreach (var sourcePathBase in sourceDataPaths)
                {
                    string potentialPath = Path.Combine(sourcePathBase, assetType, relativePath);
                    if (File.Exists(potentialPath))
                    {
                        foundSourcePath = potentialPath;
                        break; // Found it
                    }
                }

                // Check Game Data Folder (if configured and not FaceGen)
                bool isFaceGen = relativePath.Contains("facegendata", StringComparison.OrdinalIgnoreCase);
                if (foundSourcePath == null && GetMissingExtraAssetsFromAvailableWinners && !isFaceGen)
                {
                    string gameDataPath = Path.Combine(_environmentStateProvider.DataFolderPath.ToString(), assetType,
                        relativePath);
                    if (File.Exists(gameDataPath))
                    {
                        foundSourcePath = gameDataPath;
                        AppendLog($"        Found missing asset '{relativePath}' in game data folder."); // Verbose only
                    }
                }

                if (foundSourcePath == null)
                {
                    // Handle Missing File Warning/Error
                    bool suppressWarning = SuppressAllMissingFileWarnings ||
                                           (SuppressKnownMissingFileWarnings &&
                                            warningsToSuppressSet.Contains(relativePath)) ||
                                           GetExtensionOfMissingFile(relativePath) == ".tri";
                    if (!suppressWarning)
                    {
                        string errorMsg = $"Asset '{relativePath}' not found in any source directories";
                        if (GetMissingExtraAssetsFromAvailableWinners && !isFaceGen) errorMsg += " or game data folder";
                        errorMsg += $" (needed by {sourcePluginName}).";
                        AppendLog($"      WARNING: {errorMsg}"); // Verbose only (Warning)
                        if (AbortIfMissingExtraAssets && !isFaceGen)
                        {
                            throw new FileNotFoundException(errorMsg, relativePath);
                        }
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
                    {
                        AppendLog(
                            $"      ERROR copying '{foundSourcePath}' to '{destPath}': {ExceptionLogger.GetExceptionStack(ex)}",
                            true);
                    }
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
            if (string.IsNullOrEmpty(input))
            {
                return "";
            }

            return Path.GetExtension(input).ToLowerInvariant();
        }

        private void AppendLog(string message, bool isError = false, bool forceLog = false)
        {
            if (IsVerboseModeEnabled || isError || forceLog)
            {
                RxApp.MainThreadScheduler.Schedule(() => { LogOutput += message + Environment.NewLine; });
            }
        }

        // --- Race Processing Logic ---
        private void ProcessNpcRace(
            Npc patchNpc, // The NPC record in our output patch
            INpcGetter? sourceNpcIfUsed, // The NPC record from the appearance mod (if used for NPC data)
            INpcGetter winningNpcOverride, // The original conflict-winning NPC
            ModKey appearanceModKey, // The .esp key of the appearance mod providing the NPC override / assets
            ModSetting appearanceModSetting, // Full mod setting
            Dictionary<ModKey, Dictionary<IRaceGetter, IRaceGetter>> sourcePluginToPatchedRecordsMap)
        {
            // Check if the selected mod provided a plugin. If not, the race could not have been modified.
            if (sourceNpcIfUsed == null)
            {
                AppendLog(
                    $"      NPC {patchNpc.EditorID} has no plugin from {appearanceModSetting.DisplayName}. Skipping race handling.");
                return;
            }

            // Check if the selected plugin's race record exits
            if (sourceNpcIfUsed.Race.IsNull)
            {
                AppendLog(
                    $"      NPC {patchNpc.EditorID} has a null Race record from {appearanceModSetting.DisplayName}. Skipping race handling.");
                return;
            }

            // Check if the appearance mod provides an override for the NPC's current race
            var raceContexts =
                _environmentStateProvider.LinkCache
                    .ResolveAllContexts<IRace, IRaceGetter>(sourceNpcIfUsed.Race.FormKey);
            var raceGetterFromAppearanceMod =
                raceContexts.FirstOrDefault(x => x.ModKey.Equals(appearanceModKey)).Record ?? null;

            // SCENARIO 1: NPC uses a NEW race that's defined IN ITS OWN appearance mod.
            // Criteria:
            // 1. The race record (`raceGetterFromAppearanceMod`) is defined by `appearanceModKey`.
            // This scenario implies that `sourceNpcIfUsed` (if available and used for `patchNpc` creation) had this custom race.
            bool isNewCustomRaceFromAppearanceMod = raceGetterFromAppearanceMod != null &&
                                                    raceGetterFromAppearanceMod.FormKey.ModKey.Equals(appearanceModKey);

            if (isNewCustomRaceFromAppearanceMod)
            {
                AppendLog(
                    $"      NPC {patchNpc.EditorID} uses new custom race {raceGetterFromAppearanceMod.EditorID} ({raceGetterFromAppearanceMod.FormKey}) defined in its appearance mod {appearanceModKey.FileName}.");
                AppendLog($"        Merging race and its dependencies from {appearanceModKey.FileName}.");

                // Ensure patchNpc points to this race (it should if sourceNpcIfUsed was its origin and had this race)
                patchNpc.Race.SetTo(raceGetterFromAppearanceMod);

                // Add the race record (as it exists in the patch, which is an override of currentNpcRaceRecord)
                // to the map for dependency duplication.
                var raceInPatch =
                    _environmentStateProvider.OutputMod.Races.GetOrAddAsOverride(raceGetterFromAppearanceMod);
                return; // Handled
            }

            // SCENARIO 2: NPC uses an existing race, and that race is present as a record in the appearance mod
            // (i.e., appearance mod MODIFIES a race the NPC uses).
            // Criteria:
            // 1. `raceGetterFromAppearanceMod` is not null (the appearance-providing mod contains this race as a new record or override)
            // 2. `raceGetterFromAppearanceMod' has a root ModKey that's not appearanceModKey (implying that it's an overrride)
            bool isOverriddenRace = raceGetterFromAppearanceMod != null &&
                                    !raceGetterFromAppearanceMod.FormKey.ModKey.Equals(appearanceModKey);

            if (isOverriddenRace)
            {
                AppendLog(
                    $"      NPC {patchNpc.EditorID} uses overriden race {raceGetterFromAppearanceMod.EditorID} ({raceGetterFromAppearanceMod.FormKey}).");
                AppendLog($"        Appearance mod {appearanceModKey.FileName} modifies this race.");

                // Track this modification for conflict checking
                TrackRaceModification(appearanceModKey, raceGetterFromAppearanceMod, patchNpc.FormKey);

                switch (_settings.RaceHandlingMode)
                {
                    case RaceHandlingMode.ForwardWinningOverrides:
                        HandleForwardWinningOverridesRace(patchNpc, raceGetterFromAppearanceMod,
                            raceContexts, appearanceModKey);
                        break;

                    case RaceHandlingMode.DuplicateAndRemapRace:
                        HandleDuplicateAndRemapRace(patchNpc, raceGetterFromAppearanceMod, appearanceModKey,
                            sourcePluginToPatchedRecordsMap);
                        break;

                    case RaceHandlingMode.IgnoreRace:
                        AppendLog(
                            $"        Race Handling Mode: IgnoreRace. No changes made to race record {raceGetterFromAppearanceMod.EditorID} itself by this logic.");
                        break;
                }
            }
        }

        private void HandleForwardWinningOverridesRace(
            Npc patchNpc,
            IRaceGetter appearanceModRaceVersion,
            IEnumerable<IModContext<ISkyrimMod,ISkyrimModGetter,IRace,IRaceGetter>>? raceContexts,
            ModKey appearanceModKey)
        {

            var originalRaceGetter = raceContexts.Last().Record;
            if (originalRaceGetter == null)
            {
                AppendLog("Error: Could not determine original race context for race handling.");
                return;
            }
            
            if (_settings.PatchingMode == PatchingMode.EasyNPC_Like)
            {
                AppendLog(
                    $"        Race Handling: ForwardWinningOverrides (EasyNPC-Like) for race {originalRaceGetter.EditorID}.");
                // In EasyNPC-Like mode, patchNpc is an override of winningNpcOverride.
                // So patchNpc.Race should already point to originalRaceContext.FormKey.
                // We need to override originalRaceContext in the patch and forward differing properties.
                
                var racePropertyDeltas = RaceHelpers.GetDifferingPropertyNames(appearanceModRaceVersion, originalRaceGetter);
                if (racePropertyDeltas.Any())
                {
                    var winningRaceGetter = raceContexts.First().Record;
                    if (winningRaceGetter == null)
                    {
                        AppendLog("Error: Could not determine winning race context for race handling.");
                        return;
                    }
                    
                    var winningRaceRecord = _environmentStateProvider.OutputMod.Races.GetOrAddAsOverride(winningRaceGetter);
                    RaceHelpers.CopyPropertiesToNewRace(appearanceModRaceVersion, winningRaceRecord, racePropertyDeltas);
                }
                else
                {
                    AppendLog(
                        $"          Race properties are identical to original context. No race forwarding needed.");
                }
            }
            else // Default Patching Mode
            {

                AppendLog($"          Generating YAML record of race version from {appearanceModKey.FileName} for {patchNpc.EditorID}.");

                var existingSerialization = _racesToSerializeForYaml.FirstOrDefault(
                    x => x.AppearanceModKey.Equals(appearanceModKey) &&
                         x.OriginalRaceFormKey.Equals(originalRaceGetter.FormKey));
                
                if (existingSerialization != null)
                {
                    AppendLog($"          Applying previously generated race {existingSerialization.RaceToSerialize.EditorID}.");
                    _raceSerializationNpcAssignments.Add(patchNpc, existingSerialization);
                    return;
                }
                
                // Schedule new YAML generation for the diff.
                var toSerialize = new RaceSerializationInfo
                {
                    RaceToSerialize = appearanceModRaceVersion, // This is the one from the appearance mod
                    OriginalRaceFormKey = originalRaceGetter.FormKey, // This is the vanilla FormKey
                    AppearanceModKey = appearanceModKey,
                    DiffProperties = RaceHelpers.GetDifferingPropertyNames(appearanceModRaceVersion, originalRaceGetter)
                        .ToList()
                };
                
                _racesToSerializeForYaml.Add(toSerialize);
                _raceSerializationNpcAssignments.Add(patchNpc, toSerialize);
                AppendLog($"          Scheduled YAML generation for race {appearanceModRaceVersion.EditorID}.");
            }
        }

        private void HandleDuplicateAndRemapRace(
            Npc patchNpc,
            IRaceGetter appearanceModRaceVersion,
            ModKey appearanceModKey,
            Dictionary<ModKey, Dictionary<IRaceGetter, IRaceGetter>> sourcePluginToPatchedRecordsMap)
        {
            AppendLog($"        Race Handling: DuplicateAndRemapRace for {appearanceModRaceVersion.EditorID}.");
            
            // Check if a race has already been processed from this mod
            Dictionary<IRaceGetter, IRaceGetter> currentMappings = new();
            if (sourcePluginToPatchedRecordsMap.TryGetValue(appearanceModKey, out var mappings))
            {
                currentMappings = mappings;
            }
            else
            {
                sourcePluginToPatchedRecordsMap.Add(appearanceModKey, currentMappings);
            }

            if (currentMappings.TryGetValue(appearanceModRaceVersion, out var importedRace))
            {
                AppendLog($"        Race Handling: DuplicateAndRemapRace has previously been called for {appearanceModRaceVersion.EditorID} in {appearanceModKey.FileName}. Setting NPC to race {importedRace.EditorID ?? importedRace.FormKey.ToString()}.");
                patchNpc.Race.SetTo(importedRace);
                return;
            }
            
            // If not cached, import the new race.
            string baseEditorID = appearanceModRaceVersion.EditorID ?? $"Race_{appearanceModRaceVersion.FormKey.ID:X8}";
            string newEditorID = $"{baseEditorID}_{appearanceModKey.Name}";
            if (newEditorID.Length > 90) newEditorID = newEditorID.Substring(0, 90); // Crude truncation for EDID length

            // Ensure unique EditorID in the patch
            int attempt = 0;
            string finalNewEditorID = newEditorID;
            while (_environmentStateProvider.OutputMod.Races.Any(x => x.EditorID == finalNewEditorID))
            {
                attempt++;
                finalNewEditorID = $"{newEditorID}_{attempt}";
                if (finalNewEditorID.Length > 90) finalNewEditorID = finalNewEditorID.Substring(0, 90);
            }

            newEditorID = finalNewEditorID;

            AppendLog($"          Cloning as new race with EditorID: {newEditorID}");
            var clonedRace =
                _environmentStateProvider.OutputMod.Races.DuplicateInAsNewRecord(appearanceModRaceVersion, newEditorID);
            clonedRace.EditorID = newEditorID; // Set EditorID post-duplication as well

            patchNpc.Race.SetTo(clonedRace); // Assign the new cloned race to the NPC
            currentMappings.Add(appearanceModRaceVersion, clonedRace);
        }
        
        private void TrackRaceModification(ModKey appearanceSourceMod, IRaceGetter modifiedRaceRecord,
            FormKey npcFormKey)
        {
            if (!_vanillaRaceModifications.TryGetValue(modifiedRaceRecord.FormKey, out var modList))
            {
                modList = new List<(ModKey, IRaceGetter, FormKey)>();
                _vanillaRaceModifications[modifiedRaceRecord.FormKey] = modList;
            }

            // Avoid adding if this exact mod+race combo for this NPC is already noted (though unlikely per NPC)
            if (!modList.Any(x =>
                    x.SourceMod == appearanceSourceMod && x.RaceRecord.FormKey == modifiedRaceRecord.FormKey &&
                    x.NpcFormKeyWhoCausedIt == npcFormKey))
            {
                modList.Add((appearanceSourceMod, modifiedRaceRecord, npcFormKey));
            }
        }

        private string ResolveNpcName(FormKey npcKey) =>
            _environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcKey, out var npc)
                ? (npc.Name?.String ?? npc.EditorID ?? npcKey.ToString())
                : npcKey.ToString();

        private string ResolveRaceName(FormKey raceKey) =>
            _environmentStateProvider.LinkCache.TryResolve<IRaceGetter>(raceKey, out var race)
                ? (race.Name?.String ?? race.EditorID ?? raceKey.ToString())
                : raceKey.ToString();


        private void GenerateRaceYamlFiles()
        {
            
        }

        // --- Forward Race Edits From YAML ---
        private async Task ForwardRaceEditsFromYamlAsync()
        {
            AppendLog("\n--- Initiating Forward Race Edits from YAML ---");
            if (!_environmentStateProvider.EnvironmentIsValid)
            {
                AppendLog("Environment not valid. Cannot proceed.");
                return;
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "YAML files (*.yaml)|*.yaml|All files (*.*)|*.*",
                Title = "Select Race Diff YAML file",
                InitialDirectory = Path.Combine(_currentRunOutputAssetPath, "RaceDiffs"), // Default to RaceDiffs folder
                Multiselect = false // For now, one file at a time
            };

            if (openFileDialog.ShowDialog() != true)
            {
                AppendLog("No YAML file selected. Operation cancelled.");
                return;
            }

            string yamlFilePath = openFileDialog.FileName;
            AppendLog($"Processing YAML file: {yamlFilePath}");

            try
            {
                string yamlContent = File.ReadAllText(yamlFilePath);

                var yamlDeserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .WithTypeConverter(new FormLinkYamlConverter(_environmentStateProvider.LinkCache))
                    .Build();

                RaceYamlWrapper? raceWrapper = null;
                try
                {
                    raceWrapper = yamlDeserializer.Deserialize<RaceYamlWrapper>(yamlContent);
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"ERROR: Failed to deserialize YAML content from {Path.GetFileName(yamlFilePath)}. Ensure it's a valid RaceDiff YAML. Details: {ex.Message}");
                    return;
                }


                if (raceWrapper == null || raceWrapper.OriginalRace.IsNull || raceWrapper.ModifiedRace == null)
                {
                    AppendLog("ERROR: YAML file content is invalid or missing crucial race data.");
                    return;
                }

                FormKey originalRaceFormKey = raceWrapper.OriginalRace.FormKey;
                IRaceGetter modifiedRaceDataFromYaml = raceWrapper.ModifiedRace; // This is a concrete Race instance
                ModKey appearanceSourceModKeyForDependencies = raceWrapper.AppearanceSourceMod;


                // Resolve the original race in the current load order
                if (!_environmentStateProvider.LinkCache.TryResolve<IRaceGetter>(originalRaceFormKey,
                        out var currentWinningOriginalRace))
                {
                    AppendLog($"ERROR: Could not find original race {originalRaceFormKey} in the current load order.");
                    return;
                }

                AppendLog(
                    $"Found original race: {currentWinningOriginalRace.EditorID} ({currentWinningOriginalRace.FormKey}) from {currentWinningOriginalRace.FormKey.ModKey.FileName}");

                // Create a new patch plugin for these edits
                // For simplicity, let's name it based on the original YAML file.
                string patchFileName = $"ForwardedRace_{Path.GetFileNameWithoutExtension(yamlFilePath)}.esp";
                var raceEditPatch =
                    new SkyrimMod(ModKey.FromName(patchFileName, ModType.Plugin), _settings.SkyrimRelease);
                AppendLog($"Created new patch: {patchFileName}");

                // Create an override of the current winning original race in the new patch
                var raceToPatch = raceEditPatch.Races.GetOrAddAsOverride(currentWinningOriginalRace);
                AppendLog($"Overriding {raceToPatch.EditorID} in {patchFileName}.");

                // Apply the modified data from YAML to this override
                // Important: modifiedRaceDataFromYaml.FormKey might be a temporary one from YAML.
                // We need to preserve raceToPatch.FormKey.
                var originalFormKeyOfPatchedRace = raceToPatch.FormKey; // Save it
                raceToPatch.DeepCopyIn(modifiedRaceDataFromYaml,
                    new MajorRecordContext<IRaceGetter>(raceToPatch, raceEditPatch.ModKey),
                    out var remappedLinks);

                // Restore original FormKey just in case DeepCopyIn changed it (it shouldn't if target already exists)
                if (raceToPatch.FormKey != originalFormKeyOfPatchedRace)
                {
                    // This would be highly unusual for GetOrAddAsOverride target.
                    // More likely if it was a new record. For safety:
                    // raceToPatch.FormKey = originalFormKeyOfPatchedRace; // Mutagen doesn't allow FormKey changes like this.
                    // Instead, ensure the source for DeepCopyIn has a throwaway FormKey or is known to not affect target's FK.
                    // The concrete 'Race' from YAML should have its FormKey set to the OriginalRace's FormKey before DeepCopyIn
                    // if there's any risk. Or, ensure ModifiedRace in YAML is serialized with the original FormKey.
                    // For now, assume modifiedRaceDataFromYaml itself doesn't have a conflicting FormKey that DeepCopyIn would try to impose.
                }


                AppendLog($"Applied changes from YAML to {raceToPatch.EditorID}.");

                // Handle dependencies from the appearance mod
                Dictionary<FormKey, FormKey> localMapping = new();
                var recordsForDepDuplication = new List<IMajorRecordGetter> { raceToPatch };

                raceEditPatch.DuplicateFromOnlyReferencedGetters(
                    recordsForDepDuplication,
                    _environmentStateProvider.LinkCache, // Use the main LO link cache for resolving sources
                    appearanceSourceModKeyForDependencies, // Source mod for dependencies
                    ref localMapping);

                if (localMapping.Any())
                {
                    raceEditPatch.RemapLinks(localMapping);
                    AppendLog(
                        $"Duplicated and remapped {localMapping.Count} dependencies from {appearanceSourceModKeyForDependencies.FileName}.");
                }


                // Save the new patch plugin
                string savePath = Path.Combine(_currentRunOutputAssetPath, patchFileName); // Save alongside main patch
                try
                {
                    raceEditPatch.WriteToBinary(savePath);
                    AppendLog($"Successfully saved race edit patch to: {savePath}");
                    AppendLog(
                        $"IMPORTANT: Add {patchFileName} to your load order and ensure it loads AFTER any other mods modifying {originalRaceFormKey}.");
                }
                catch (Exception ex)
                {
                    AppendLog($"ERROR saving race edit patch {patchFileName}: {ExceptionLogger.GetExceptionStack(ex)}");
                }
            }
            catch (Exception ex)
            {
                AppendLog(
                    $"An unexpected error occurred while forwarding race edits: {ExceptionLogger.GetExceptionStack(ex)}");
            }
            finally
            {
                AppendLog("--- Finished Forward Race Edits from YAML ---");
            }
        }
    }

    // Helper struct for YAML data
    internal class RaceSerializationInfo
    {
        public IRaceGetter RaceToSerialize { get; set; }
        public FormKey OriginalRaceFormKey { get; set; } // FormKey of the race this is a diff against
        public ModKey AppearanceModKey { get; set; } // Source of the modified race
        public List<string> DiffProperties { get; set; } // Properties to forward when diff patching
    }

    // Helper class for deserializing our specific YAML structure for the "Forward Race Edits" button
    // This is what we expect to find in the YAML files generated by "ForwardWinningOverrides" (Default)
    public class RaceYamlWrapper
    {
        public FormLink<IRaceGetter> OriginalRace { get; set; }
        public Race ModifiedRace { get; set; } // The actual race data from the appearance mod
        public ModKey AppearanceSourceMod { get; set; }
    }

    // Comparer for checking if race records have different content
    public class MajorRecordContentComparer<T> : IEqualityComparer<T> where T : IMajorRecordGetter
    {
        public bool Equals(T? x, T? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            // ignoreFormKeyFixedUp:true compares content regardless of specific FormKey values if they were part of a duplication.
            // ignoreTargetLinks:false means links must point to the same target FormKey.
            return x.Equals(y); 
        }

        public int GetHashCode(T obj)
        {
            // This hash code might not be perfect for content equality but is required.
            // For Distinct(), proper Equals is more important.
            return obj.FormKey.GetHashCode();
        }
    }

    // YamlDotNet TypeConverter for FormLink<T>
    // This helps serialize/deserialize FormLinks in a more readable way (e.g., "Skyrim.esm:00000007")
    public class FormLinkYamlConverter : IYamlTypeConverter
    {
        private readonly ILinkCache _linkCache;

        public FormLinkYamlConverter(ILinkCache linkCache)
        {
            _linkCache = linkCache;
        }

        public bool Accepts(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FormLink<>);
        }

        public object ReadYaml(IParser parser, Type type)
        {
            var scalar = parser.Consume<YamlDotNet.Core.Events.Scalar>();
            var fk = FormKey.Factory(scalar.Value);
            // Create a FormLink of the correct generic type, e.g. FormLink<IRaceGetter>
            var formLinkType = typeof(FormLink<>).MakeGenericType(type.GetGenericArguments()[0]);
            return Activator.CreateInstance(formLinkType, fk)!;
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type)
        {
            if (value == null) return;
            // value is FormLink<T>. Access its FormKey property.
            var formKeyProperty = type.GetProperty("FormKey");
            if (formKeyProperty != null)
            {
                var fk = (FormKey)formKeyProperty.GetValue(value)!;
                string? editorId = null;
                if (!fk.IsNull &&
                    _linkCache.TryResolve<IMajorRecordGetter>(fk, out var rec)) // Attempt to resolve for EditorID
                {
                    editorId = rec.EditorID;
                }

                string output = fk.ToString();
                if (!string.IsNullOrEmpty(editorId))
                {
                    output = $"{output} #{editorId}"; // e.g., Skyrim.esm:00013746 #NordRace
                }

                emitter.Emit(new YamlDotNet.Core.Events.Scalar(output));
            }
        }
    }
}

public static class RaceHelpers
{
    /// <summary>
    /// Compares two <see cref="IRaceGetter"/>s and returns the names of every public
    /// property whose values are not equal.
    /// </summary>
    public static IReadOnlyList<string> GetDifferingPropertyNames(
        IRaceGetter first,
        IRaceGetter second)
    {
        if (first  is null) throw new ArgumentNullException(nameof(first));
        if (second is null) throw new ArgumentNullException(nameof(second));

        var differences = new List<string>();

        // All public instance properties declared on the interface
        foreach (PropertyInfo pi in typeof(IRaceGetter)
                                     .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? a = pi.GetValue(first);
            object? b = pi.GetValue(second);

            // NB: object.Equals handles null‐checks for us
            if (!Equals(a, b))
                differences.Add(pi.Name);
        }

        return differences;
    }

    /// <summary>
    /// Copies the properties named in <paramref name="propertiesToCopy"/> from
    /// <paramref name="source"/> onto <paramref name="destinationRace"/>.
    /// </summary>
    public static void CopyPropertiesToNewRace(
        IRaceGetter source,
        Race destinationRace,
        IEnumerable<string> propertiesToCopy)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (destinationRace is null) throw new ArgumentNullException(nameof(destinationRace));
        if (propertiesToCopy is null) throw new ArgumentNullException(nameof(propertiesToCopy));
        
        // Cache reflection once for speed
        var getterProps = typeof(IRaceGetter)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name);

        var setterProps = typeof(IRace)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name);

        foreach (string propName in propertiesToCopy)
        {
            if (!getterProps.TryGetValue(propName, out PropertyInfo? srcPi)) continue; // unknown
            if (!setterProps.TryGetValue(propName, out PropertyInfo? dstPi)) continue; // not writable

            object? value = srcPi.GetValue(source);
            dstPi.SetValue(destinationRace, value);
        }
    }
}

// Temporary class matching V1's structure for loading JSON
public class SuppressedWarnings
{
    public string Plugin { get; set; } = "";
    public HashSet<string> Paths { get; set; } = new HashSet<string>();
}