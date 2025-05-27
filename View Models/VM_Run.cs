// VM_Run.cs
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.IO;
using Mutagen.Bethesda;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reflection;
using System.Windows;
using Mutagen.Bethesda.Archives;
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
        private readonly RaceHandler _raceHandler;
        private readonly BsaHandler _bsaHandler;
        private readonly DuplicateInManager _duplicateInManager;
        private readonly CompositeDisposable _disposables = new();

        // --- Constants ---
        private const string ALL_NPCS_GROUP = "<All NPCs>";


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
            Lazy<VM_Mods> lazyVmMods,
            BsaHandler bsaHandler,
            RaceHandler raceHandler,
            DuplicateInManager duplicateInManager)
        {
            _environmentStateProvider = environmentStateProvider;
            _settings = settings;
            _consistencyProvider = consistencyProvider;
            _auxilliary = auxilliary;
            _bsaHandler = bsaHandler;
            _vmSettings = vmSettings;
            _lazyVmMods = lazyVmMods;
            _raceHandler = raceHandler;
            _duplicateInManager = duplicateInManager;

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

                // 3. Check for Plugin Record
                bool correspondingRecordFound = false;
                INpcGetter appearanceModRecord;
                ModKey? appearanceModKey;

                List<string> sourcePluginNames = new List<string>(); // For logging ambiguity

                var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcFormKey)
                    .ToList();
                
                var baseNpcRecord = contexts.FirstOrDefault(x => x.ModKey.Equals(npcFormKey.ModKey))?.Record;
                
                if (baseNpcRecord == null)
                {
                    AppendLog(
                        $"  SCREENING ERROR: Cannot find the base record for NPC {npcIdentifier} in the current load order. Invalid selection.",
                        true);
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Base record not found in current load order)");
                    // Add a placeholder invalid result to cache? Or just rely on the invalidSelections list? Let's rely on the list for now.
                    await Task.Delay(1);
                    continue;
                }
                
                appearanceModRecord = baseNpcRecord; // placeholder to initialize
                appearanceModKey = npcFormKey.ModKey; // placeholder to initialize

                var npcContextModKeys = contexts.Select(x => x.ModKey).ToList();
                var availableModKeysForThisNpcInSelectedAppearanceMod = npcContextModKeys.Intersect(appearanceModSetting.CorrespondingModKeys).ToList();

                if (!availableModKeysForThisNpcInSelectedAppearanceMod.Any() && appearanceModSetting.CorrespondingModKeys.Any()) // If there are no appearanceModSetting.CorrespondingModKeys, the baseNpcRecord is used
                {
                    AppendLog(
                        $"  SCREENING ERROR: Cannot find any plugins in Mod Setting '{selectedModDisplayName}' for NPC {npcIdentifier}. Invalid selection.",
                        true);
                    invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Mod Setting doesn't contain any plugin for this NPC)");
                    // Add a placeholder invalid result to cache? Or just rely on the invalidSelections list? Let's rely on the list for now.
                    await Task.Delay(1);
                    continue;
                }
                
                if (appearanceModSetting.NpcPluginDisambiguation.TryGetValue(npcFormKey, out ModKey disambiguation))
                {
                    if (npcContextModKeys.Contains(disambiguation))
                    {
                        var disambiguatedContext = contexts.First(x => x.ModKey == disambiguation);
                        appearanceModRecord = disambiguatedContext.Record;
                        appearanceModKey = disambiguatedContext.ModKey;
                        correspondingRecordFound = true;
                        AppendLog(
                            $"    Screening: Found assigned plugin record override in {appearanceModRecord.FormKey.ModKey.FileName}. Using this as source."); // Verbose only
                    }
                    else
                    {
                        AppendLog(
                            $"    Screening: Source plugin is set to {disambiguation.FileName} but this plugin is not in the load order or doesn't contain this NPC. Falling back to first available plugin"); // Verbose only
                    }
                }

                if (!correspondingRecordFound)
                {
                    if (!appearanceModSetting.CorrespondingModKeys.Any())
                    {
                        AppendLog(
                            "    Screening: Selected appearance mod doesn't have any associated ModKeys. Using the base record as the appearance source."); // Verbose only
                        appearanceModRecord = baseNpcRecord;
                        appearanceModKey = baseNpcRecord.FormKey.ModKey;
                        correspondingRecordFound = true;
                    }
                    else if (availableModKeysForThisNpcInSelectedAppearanceMod.Any())
                    {
                        var firstCandidate = availableModKeysForThisNpcInSelectedAppearanceMod.First();
                        var firstContext = contexts.
                            First(x => x.ModKey.Equals(firstCandidate));
                        appearanceModRecord = firstContext.Record;
                        appearanceModKey = firstContext.ModKey;
                        AppendLog(
                            $"    Screening: Selected plugin record override in {appearanceModRecord.FormKey.ModKey.FileName}. Using this as source."); // Verbose only
                        correspondingRecordFound = true;
                    }
                    else
                    {
                        AppendLog(
                            $"  SCREENING ERROR: Cannot find any plugins in Mod Setting '{selectedModDisplayName}' for NPC {npcIdentifier}. Invalid selection.",
                            true);
                        invalidSelections.Add($"{npcIdentifier} -> '{selectedModDisplayName}' (Mod Setting doesn't contain any plugin for this NPC)");
                        // Add a placeholder invalid result to cache? Or just rely on the invalidSelections list? Let's rely on the list for now.
                        await Task.Delay(1);
                        continue;
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
                    ModKey keyForFaceGenCheck = npcFormKey.ModKey;
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
                screeningCache[npcFormKey] = new ScreeningResult(
                    correspondingRecordFound,
                    hasFaceGen,
                    appearanceModRecord,
                    appearanceModKey,
                    winningNpcOverride, // Cache the winning override
                    appearanceModSetting
                );

                if (!correspondingRecordFound)
                {
                    string reason = "No plugin override found";

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
            
            _raceHandler.Reinitialize();
            _duplicateInManager.Reinitialize();

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
                    var appearanceNpcRecord = result.AppearanceModRecord; // Cached specific record, might be null
                    var appearanceModKey = result.AppearanceModKey;
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
                    bool usedPluginRecord = false; // Track if we used the plugin override

                    // --- *** Apply Logic Based on Screening Result (Requirement 1) *** ---
                    if (appearanceNpcRecord != null) // Scenario: Plugin override exists
                    {
                        AppendLog("    Source: Plugin Record Override"); // Verbose only
                        npcForAssetLookup = appearanceNpcRecord; // Use appearance mod's record for extra asset lookup

                        switch (_settings.PatchingMode)
                        {
                            case PatchingMode.EasyNPC_Like:
                                AppendLog(
                                    $"      Mode: EasyNPC-Like. Patching winning override ({winningNpcOverride.FormKey.ModKey.FileName}) with appearance from {appearanceModKey?.FileName ?? "N/A"}."); // Verbose only
                                patchNpc =
                                    _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(winningNpcOverride);
                                CopyAppearanceData(appearanceNpcRecord, patchNpc);
                                break;
                            case PatchingMode.Default:
                            default:
                                AppendLog(
                                    $"      Mode: Default. Forwarding record from source plugin ({appearanceModKey?.FileName ?? "N/A"})."); // Verbose only
                                patchNpc = _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(appearanceNpcRecord);
                                break;
                        }
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

                    // --- Copy Assets ---
                    if (patchNpc != null && appearanceModSetting != null) // Ensure we have a patch record and mod settings
                    {
                        await CopyNpcAssets(appearanceNpcRecord, appearanceModSetting);
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

                    // Handle race deep-copy if needed
                    _raceHandler.ProcessNpcRace(patchNpc, appearanceNpcRecord, winningNpcOverride, appearanceModKey.Value, appearanceModSetting);
                    
                    // Handle record merge-in
                    try
                    {
                        _duplicateInManager.DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod,
                            appearanceNpcRecord,
                            appearanceModSetting.CorrespondingModKeys);
                        AppendLog(
                            $"    Completed dependency processing for {npcIdentifier}."); // Verbose only
                    }
                    catch (Exception ex)
                    {
                        AppendLog(
                            $"  ERROR duplicating dependencies for {npcIdentifier}: {ExceptionLogger.GetExceptionStack(ex)}",
                            true);
                        // Continue processing other plugins
                    }

                    processedCount++;
                    await Task.Delay(5);
                } // End For Loop
            } // End else (selectionsToProcess.Any())

            UpdateProgress(processedCount + skippedCount, processedCount + skippedCount, "Finalizing...");

            // --- Final Steps (Save Output Mod) ---
            if (processedCount > 0)
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
        /// <param name="appearanceNpcRecord">The NPC record being added/modified in the output patch.</param>
        /// <param name="appearanceModSetting">The ModSetting chosen for the selected NPC.</param>
        private async Task CopyNpcAssets(
            INpcGetter appearanceNpcRecord,
            ModSetting appearanceModSetting)
        {
            AppendLog(
                $"    Copying assets for {appearanceNpcRecord.EditorID ?? appearanceNpcRecord.FormKey.ToString()} from sources related to '{appearanceModSetting.DisplayName}'..."); // Verbose only

            var assetSourceDirs = appearanceModSetting.CorrespondingFolderPaths ?? new List<string>();
            if (!assetSourceDirs.Any())
            {
                AppendLog(
                    $"      WARNING: Mod Setting '{appearanceModSetting.DisplayName}' has no Corresponding Folder Paths. Cannot copy assets."); // Verbose only (Warning)
                return;
            }

            AppendLog($"      Asset source directories: {string.Join(", ", assetSourceDirs)}"); // Verbose only

            // --- Identify Required Assets ---
            HashSet<string> meshToCopyRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> textureToCopyRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> handledRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var baseModKey = appearanceNpcRecord.FormKey.ModKey;

            // 1. FaceGen Assets (Always check if requested, templating handled by caller passing null npcForExtraAssetLookup)
            // Construct FaceGen paths using the provided key
            var (faceMeshRelativePath, faceTexRelativePath) =
                Auxilliary.GetFaceGenSubPathStrings(appearanceNpcRecord.FormKey); // Use the keyForFaceGenPath here!
            meshToCopyRelativePaths.Add(faceMeshRelativePath);
            textureToCopyRelativePaths.Add(faceTexRelativePath);
            AppendLog(
                $"      Identified FaceGen paths (using key {baseModKey.FileName}): {faceMeshRelativePath}, {faceTexRelativePath}"); // Verbose only

            // 2. Extra Assets (Only if CopyExtraAssets is true)
            if (CopyExtraAssets)
            {
                AppendLog(
                    $"      Identifying extra assets referenced by plugin record {appearanceNpcRecord.FormKey}..."); // Verbose only
                GetAssetsReferencedByPlugin(appearanceNpcRecord, meshToCopyRelativePaths, textureToCopyRelativePaths);
            }
            else if (!CopyExtraAssets)
            {
                AppendLog($"      Skipping extra asset identification (CopyExtraAssets disabled)."); // Verbose only
            }

            // --- Handle BSAs ---
            HashSet<string> extractedMeshFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> extractedTextureFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (HandleBSAFiles_Patching)
            {
                AppendLog(
                    $"      Checking BSAs associated with plugin key {baseModKey.FileName} in source directories..."); // Verbose only
                await Task.Run(() =>
                    UnpackAssetsFromBSA(meshToCopyRelativePaths, textureToCopyRelativePaths,
                        extractedMeshFiles, extractedTextureFiles,
                        baseModKey, assetSourceDirs, _currentRunOutputAssetPath, AppendLog));
                AppendLog(
                    $"      Extracted {extractedMeshFiles.Count} meshes and {extractedTextureFiles.Count} textures from BSAs."); // Verbose only
                
                handledRelativePaths.UnionWith(extractedMeshFiles);
                handledRelativePaths.UnionWith(extractedTextureFiles);
            }

            // --- Handle NIF Scanning ---
            // Scan NIFs if CopyExtraAssets OR copyOnlyFaceGenAssets (because FaceGen NIF itself needs scanning) AND FindExtraTexturesInNifs is true
            if (CopyExtraAssets && FindExtraTexturesInNifs)
            {
                AppendLog($"      Scanning NIF files for additional texture references..."); // Verbose only
                HashSet<string> texturesFromNifsRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> alreadyDetectedTextures =
                    new HashSet<string>(textureToCopyRelativePaths, StringComparer.OrdinalIgnoreCase);
                alreadyDetectedTextures.UnionWith(extractedTextureFiles);

                // Scan loose NIFs (check all source dirs)
                GetExtraTexturesFromNifSet(meshToCopyRelativePaths, assetSourceDirs, texturesFromNifsRelativePaths, alreadyDetectedTextures);
                // Scan extracted NIFs (check output dir)
                GetExtraTexturesFromNifSet(extractedMeshFiles, new List<string> { _currentRunOutputAssetPath },
                    texturesFromNifsRelativePaths, alreadyDetectedTextures);

                AppendLog($"        Found {texturesFromNifsRelativePaths.Count} additional textures in NIFs."); // Verbose only

                // Try extracting newly found textures from BSAs
                if (HandleBSAFiles_Patching && texturesFromNifsRelativePaths.Any())
                {
                    HashSet<string> newlyExtractedNifTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    UnpackAssetsFromBSA(new HashSet<string>(), texturesFromNifsRelativePaths, new HashSet<string>(),
                        newlyExtractedNifTextures, baseModKey, assetSourceDirs, _currentRunOutputAssetPath, AppendLog);
                    AppendLog(
                        $"        Extracted {newlyExtractedNifTextures.Count} of these additional textures from BSAs."); // Verbose only
                    texturesFromNifsRelativePaths.ExceptWith(newlyExtractedNifTextures);
                    handledRelativePaths.UnionWith(newlyExtractedNifTextures);
                }

                textureToCopyRelativePaths.UnionWith(texturesFromNifsRelativePaths);
            }
            else
            {
                AppendLog($"      Skipping NIF scanning for textures."); // Verbose only
            }


            // --- Copy Loose Files ---
            AppendLog($"      Copying {meshToCopyRelativePaths.Count} loose mesh files..."); // Verbose only
            var texResultStatus = CopyAssetFiles(assetSourceDirs, meshToCopyRelativePaths, "Meshes", baseModKey.FileName.String);

            AppendLog($"      Copying {textureToCopyRelativePaths.Count} loose texture files..."); // Verbose only
            var meshResultStatus = CopyAssetFiles(assetSourceDirs, textureToCopyRelativePaths, "Textures", baseModKey.FileName.String);

            handledRelativePaths.UnionWith(texResultStatus.Where(x => x.Value == true).Select(x => x.Key));
            handledRelativePaths.UnionWith(meshResultStatus.Where(x => x.Value == true).Select(x => x.Key));
            
            // Make sure facegen has been copied. If not, try to source it from the original record.
            bool faceGenTexCopied = handledRelativePaths.Contains(faceTexRelativePath);
            bool faceGenMeshCopied = handledRelativePaths.Contains(faceMeshRelativePath);

            if (!faceGenTexCopied)
            {
                faceGenTexCopied = CopySourceFaceGen(faceTexRelativePath, _currentRunOutputAssetPath, "Textures", assetSourceDirs,
                    appearanceNpcRecord.FormKey.ModKey, _settings.ModsFolder, AppendLog);
            }
            
            if (!faceGenMeshCopied)
            {
                faceGenMeshCopied = CopySourceFaceGen(faceMeshRelativePath, _currentRunOutputAssetPath, "Meshes", assetSourceDirs,
                    appearanceNpcRecord.FormKey.ModKey, _settings.ModsFolder, AppendLog);
            }
            
            if (!faceGenTexCopied)
            {
                AppendLog($"ERROR: Failed to find any FaceGen texture: {faceTexRelativePath}", true);
            }
            if (!faceGenMeshCopied)
            {
                AppendLog($"ERROR: Failed to find any FaceGen mesh: {faceMeshRelativePath}", true);
            }
            
            AppendLog(
                $"    Finished asset copying for {appearanceNpcRecord.EditorID ?? appearanceNpcRecord.FormKey.ToString()}."); // Verbose only
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
        /// Checks if FaceGen files exist loose or in BSAs within the provided source directories.
        /// **Revised for Clarification 2.**
        /// </summary>
        private bool FaceGenExists(FormKey npcFormKey, ModKey appearancePluginKey, List<string> assetSourceDirs)
        {
            var (faceMeshPath, faceTexPath) = Auxilliary.GetFaceGenSubPathStrings(npcFormKey);
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
                    HashSet<IArchiveReader> readers = _bsaHandler.OpenBsaArchiveReaders(sourceDir, appearancePluginKey);
                    if (readers.Any())
                    {
                        string bsaMeshPath = Path.Combine("meshes", faceMeshPath).Replace('/', '\\');
                        string bsaTexPath = Path.Combine("textures", faceTexPath).Replace('/', '\\');

                        // Check *this specific set* of readers using HaveFile
                        // *** Use HaveFile here (out var _ discards the file) ***
                        if (!nifFound && _bsaHandler.HaveFile(bsaMeshPath, readers, out _)) nifFound = true;
                        if (!ddsFound && _bsaHandler.HaveFile(bsaTexPath, readers, out _)) ddsFound = true;

                        if (nifFound && ddsFound) break; // Found both
                    }
                }
            }

            return nifFound || ddsFound;
        }


        // --- Asset Copying/Extraction Helpers ---

        /// <summary>
        /// Attempts to copy facegen from a mod or plugin's source
        /// </summary>
        ///
        private bool CopySourceFaceGen(string relativePath, string outputDirPath, string assetType /*"Meshes" or "Textures"*/, List<string> assetSourceDirs, ModKey baseNpcPlugin, string modsFolderPath, Action<string, bool, bool>? log = null)
        {
            string dataRelativePath = Path.Combine(assetType, relativePath);
            string destPath = Path.Combine(outputDirPath, dataRelativePath);
            
            // try to find the missing FaceGen in a BSA corresponding to the base NPC record
            var directoriesToQueryForBsa = assetSourceDirs.And(_environmentStateProvider.DataFolderPath.Path);

            foreach (var dir in directoriesToQueryForBsa)
            {
                if (_bsaHandler.DirectoryHasCorrespondingBsaFile(dir, baseNpcPlugin))
                {
                    var readers = _bsaHandler.OpenBsaArchiveReaders(dir, baseNpcPlugin, log);

                    if (_bsaHandler.TryGetFileFromReaders(dataRelativePath, readers, out IArchiveFile? archiveFile) && 
                        archiveFile != null && 
                        _bsaHandler.ExtractFileFromBsa(archiveFile, destPath))
                    {
                        return true;
                    }
                }
            }
            
            // try to find the missing FaceGen in the mods folder where the base NPC plugin lives
            var candidateDirectories = GetContainingSubdirectories(modsFolderPath, baseNpcPlugin.FileName);
            foreach (var dir in candidateDirectories)
            {
                var candidatePath = System.IO.Path.Combine(dir, dataRelativePath);
                if (File.Exists(candidatePath))
                {
                    List<string> sourceDirAsList = new() { dir };
                    HashSet<string> relativePathAsSet = new() { relativePath };
                    var status = CopyAssetFiles(sourceDirAsList, relativePathAsSet, assetType,
                        baseNpcPlugin.FileName);
                    if (status[relativePath] == true)
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Returns the direct sub-directories of <paramref name="rootDir"/> that contain
        /// <paramref name="relativeFilePath"/> (e.g.  "Textures\Foo.png").
        /// Only the first level of sub-directories is inspected—no recursion.
        /// </summary>
        public static string[] GetContainingSubdirectories(
            string rootDir,
            string relativeFilePath)
        {
            if (rootDir is null)            throw new ArgumentNullException(nameof(rootDir));
            if (relativeFilePath is null)   throw new ArgumentNullException(nameof(relativeFilePath));

            return Directory                          // 1️⃣  list immediate sub-folders
                .EnumerateDirectories(rootDir, "*", SearchOption.TopDirectoryOnly)
                .Where(subDir =>                    // 2️⃣  keep those that contain the file
                    File.Exists(Path.Combine(subDir, relativeFilePath)))
                .ToArray();                         // 3️⃣  materialise as string[]
        }

        /// <summary>
        /// Extracts assets from BSAs found in any of the assetSourceDirs, prioritizing later directories.
        /// </summary>
        private void UnpackAssetsFromBSA(
            HashSet<string> MeshesToExtract, HashSet<string> TexturesToExtract,
            HashSet<string> extractedMeshes, HashSet<string> extractedTextures,
            ModKey currentPluginKey, List<string> assetSourceDirs, string targetAssetPath,
            Action<string, bool, bool>? log = null)
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
                    _bsaHandler.OpenBsaArchiveReaders(sourceDir, currentPluginKey, log);
                if (!readers.Any()) continue;

                // Check remaining meshes
                foreach (string subPath in MeshesToExtract.ToList())
                {
                    if (foundMeshSources.ContainsKey(subPath)) continue;

                    string bsaMeshPath = Path.Combine("meshes", subPath).Replace('/', '\\');
                    // *** Use HaveFile here ***
                    if (_bsaHandler.HaveFile(bsaMeshPath, readers, out var file) && file != null)
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
                    if (_bsaHandler.HaveFile(bsaTexPath, readers, out var file) && file != null)
                    {
                        foundTextureSources[subPath] = (file, Path.Combine(targetAssetPath, "textures", subPath));
                    }
                }
            }

            // Extract winning sources
            foreach (var kvp in foundMeshSources)
            {
                string subPath = kvp.Key;
                if (_bsaHandler.ExtractFileFromBsa(kvp.Value.file,
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
                if (_bsaHandler.ExtractFileFromBsa(kvp.Value.file,
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
        private Dictionary<string, bool> CopyAssetFiles(List<string> sourceDataDirPaths, HashSet<string> assetRelativePathList,
            string assetType /*"Meshes" or "Textures"*/, string sourcePluginName)
        {
            Dictionary<string, bool> result = new();
            
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
                result[relativePath] = false;
                if (IsIgnored(relativePath, _pathsToIgnore)) continue;

                string? foundSourcePath = null;
                // Check Source Directories
                foreach (var sourcePathBase in sourceDataDirPaths)
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
                        result[relativePath] = true;
                    }
                    catch (Exception ex)
                    {
                        AppendLog(
                            $"      ERROR copying '{foundSourcePath}' to '{destPath}': {ExceptionLogger.GetExceptionStack(ex)}",
                            true);
                    }
                }
            }
            
            return result;
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
        
        private readonly StringBuilder _logBuilder = new();
        
        /// <summary>
        /// Appends a log line in a way that keeps the UI thread responsive **and** scales well
        /// with large logs.
        ///
        /// * **Thread-safety / UI affinity** – The work that mutates <see cref="LogOutput"/> must
        ///   run on the UI thread, so the method posts a delegate to
        ///   <see cref="RxApp.MainThreadScheduler"/> instead of touching the property directly.
        ///   This lets callers invoke <c>AppendLog</c> freely from background threads without
        ///   risking cross-thread-access exceptions.
        ///
        /// * **Low allocation pressure** – Rather than concatenating strings
        ///   (<c>LogOutput += …</c>)—which reallocates the entire buffer each time—the method
        ///   maintains a single <see cref="StringBuilder"/> (<c>_logBuilder</c>).  
        ///   Each scheduled delegate appends the new line and then publishes the builder’s
        ///   current contents to the bound property, avoiding O(<i>n</i><sup>2</sup>) growth as
        ///   the log gets longer.
        ///
        /// * **Minimal closure capture** – The overload of
        ///   <see cref="IScheduler.Schedule{TState}(TState, Func{IScheduler,TState,IDisposable})"/>
        ///   passes the <paramref name="message"/> as explicit <c>TState</c>.  
        ///   This keeps the closure tiny (no hidden field for the outer scope) and eliminates an
        ///   extra heap allocation per call.
        ///
        /// * **Return value** – The delegate must return an <see cref="IDisposable"/>; the method
        ///   has no follow-up work to cancel, so it simply returns
        ///   <see cref="Disposable.Empty"/>.
        ///
        /// The optional flags allow you to suppress routine messages unless verbose mode is
        /// enabled, while still forcing important or error messages to appear.
        /// </summary>
        /// <param name="message">Text to write to the log.</param>
        /// <param name="isError">Marks the entry as an error so it bypasses verbose filtering.</param>
        /// <param name="forceLog">
        /// When <c>true</c>, the entry is logged even if verbose mode is off and
        /// <paramref name="isError"/> is <c>false</c>.
        /// </param>

        public void AppendLog(string message, bool isError = false, bool forceLog = false)
        {
            if (!IsVerboseModeEnabled && !isError && !forceLog) return;

            RxApp.MainThreadScheduler.Schedule(message, (sched, msg) =>
            {
                _logBuilder.AppendLine(msg);
                LogOutput = _logBuilder.ToString();
                return Disposable.Empty;
            });
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