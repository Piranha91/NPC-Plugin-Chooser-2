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
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins.Records;
using Noggog;
using NPC_Plugin_Chooser_2.Views;
using Serilog; // Needed for LinkCache Interface

namespace NPC_Plugin_Chooser_2.View_Models;

public class VM_Run : ReactiveObject, IDisposable
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Settings _settings;
    private readonly VM_Settings _vmSettings;
    private readonly Lazy<VM_Mods> _lazyVmMods;
    private readonly Patcher _patcher;
    private readonly Validator _validator;
    private readonly AssetHandler _assetHandler;
    private readonly BsaHandler _bsaHandler;
    private readonly SkyPatcherInterface _skyPatcherInterface;
    private readonly RecordDeltaPatcher _recordDeltaPatcher;
    private readonly PluginProvider _pluginProvider;
    private readonly RecordHandler _recordHandler;
    private readonly Auxilliary _aux;
    private readonly MasterAnalyzer _masterAnalyzer;
    private CancellationTokenSource? _patchingCts;
    private readonly CompositeDisposable _disposables = new();
    private readonly Subject<string> _logMessageSubject = new Subject<string>();

    // --- Constants ---
    public const string ALL_NPCS_GROUP = "<All NPCs>";


    // --- Logging & State ---
    [Reactive] public string LogOutput { get; private set; } = string.Empty;
    [Reactive] public bool IsRunning { get; private set; }
    [Reactive] public string RunButtonText { get; private set; } = "Run Patch Generation";
    [Reactive] public double ProgressValue { get; private set; } = 0;
    [Reactive] public string ProgressText { get; private set; } = string.Empty;
    [Reactive] public bool IsVerboseModeEnabled { get; set; } = false; // Default to non-verbose

    // --- New Properties for Timestamps ---
    [Reactive] private DateTime? ValidationStartTime { get; set; }
    [Reactive] private DateTime? PatchingStartTime { get; set; }
    [Reactive] private string CurrentProgressMessage { get; set; } = string.Empty;
    [Reactive] private TimeSpan? FinalValidationTime { get; set; }


    // --- Group Filtering ---
    public ObservableCollection<string> AvailableNpcGroups { get; } = new();
    [Reactive] public string SelectedNpcGroup { get; set; } = ALL_NPCS_GROUP;


    // --- Configuration (Mirrored from V1 for backend use) ---
    // These could be exposed in Settings View later if desired, or kept internal
    private bool ClearOutputDirectoryOnRun => true; // Example: default to true

    // --- Internal Data for Patching Run ---

    private Dictionary<string, ModSetting> _modSettingsMap = new(); // Key: DisplayName, Value: ModSetting
    private string _currentRunOutputAssetPath = string.Empty;
    public string CurrentRunOutputAssetPath => _currentRunOutputAssetPath;


    public ReactiveCommand<Unit, Unit> RunCommand { get; }
    public ReactiveCommand<Unit, Unit> GenerateSpawnBatCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowStatusCommand { get; }
    
    public ReactiveCommand<Unit, Unit> AnalyzeMastersCommand { get; }

    public VM_Run(
        EnvironmentStateProvider environmentStateProvider,
        Settings settings,
        VM_Settings vmSettings,
        Lazy<VM_Mods> lazyVmMods,
        Patcher patcher,
        Validator validator,
        AssetHandler assetHandler,
        BsaHandler bsaHandler,
        SkyPatcherInterface skyPatcherInterface,
        RecordDeltaPatcher recordDeltaPatcher,
        Auxilliary aux,
        PluginProvider pluginProvider,
        RecordHandler recordHandler,
        MasterAnalyzer masterAnalyzer)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
        _vmSettings = vmSettings;
        _lazyVmMods = lazyVmMods;
        _patcher = patcher;
        _validator = validator;
        _assetHandler = assetHandler;
        _bsaHandler = bsaHandler;
        _skyPatcherInterface = skyPatcherInterface;
        _recordDeltaPatcher = recordDeltaPatcher;
        _aux = aux;
        _pluginProvider = pluginProvider;
        _recordHandler = recordHandler;
        _masterAnalyzer = masterAnalyzer;

        _patcher.ConnectToUILogger(AppendLog, UpdateProgress, ResetProgress, ResetLog);
        _validator.ConnectToUILogger(AppendLog, UpdateProgress, ResetProgress, ResetLog);
        _assetHandler.ConnectToUILogger(AppendLog, UpdateProgress, ResetProgress, ResetLog);
        _bsaHandler.ConnectToUILogger(AppendLog, UpdateProgress, ResetProgress, ResetLog);
        _recordDeltaPatcher.ConnectToUILogger(AppendLog, UpdateProgress, ResetProgress, ResetLog);
        _skyPatcherInterface.ConnectToUILogger(AppendLog, UpdateProgress, ResetProgress, ResetLog);

        this.WhenAnyValue(x => x.IsRunning)
            .Select(isRunning => isRunning ? "Cancel Patching" : "Run Patch Generation")
            .ObserveOn(RxApp.MainThreadScheduler)
            .BindTo(this, x => x.RunButtonText)
            .DisposeWith(_disposables);

        // Command should be executable if the environment is valid (to start) OR if it's already running (to cancel).
        var canExecute = this.WhenAnyValue(
            x => x.IsRunning,
            x => x._environmentStateProvider.Status,
            (running, status) => running || status == EnvironmentStateProvider.EnvironmentStatus.Valid);

        // This command's delegate is SYNCHRONOUS. It fires off the async work or cancels it.
        RunCommand = ReactiveCommand.Create(TogglePatcherExecution, canExecute).DisposeWith(_disposables);

        // DO NOT bind IsExecuting to IsRunning. We are managing IsRunning manually.

        // Note: Since the command's task is now synchronous and short-lived,
        // the ThrownExceptions subscription is less likely to fire for patching errors.
        // We will handle exceptions within the async method itself.
        RunCommand.ThrownExceptions.Subscribe(ex =>
        {
            // This will now only catch rare errors within TogglePatcherExecution itself.
            AppendLog($"FATAL UI ERROR: {ExceptionLogger.GetExceptionStack(ex)}", true);
        }).DisposeWith(_disposables);

        // --- Timestamp and Progress Text Composition Logic ---
        var runningTimer = this.WhenAnyValue(x => x.IsRunning)
            .Select(running => running
                ? Observable.Interval(TimeSpan.FromSeconds(1), RxApp.MainThreadScheduler).Select(_ => Unit.Default)
                : Observable.Empty<Unit>())
            .Switch();

        // React to changes in any property that affects the progress text
        var progressChanged = this.WhenAnyValue(x => x.CurrentProgressMessage, x => x.ValidationStartTime,
                x => x.PatchingStartTime, x => x.FinalValidationTime)
            .Select(_ => Unit.Default);

        Observable.Merge(runningTimer, progressChanged)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (!IsRunning && string.IsNullOrEmpty(CurrentProgressMessage))
                {
                    ProgressText = string.Empty;
                    return;
                }

                if (!IsRunning)
                {
                    ProgressText = CurrentProgressMessage;
                    return;
                }

                var sb = new StringBuilder();

                if (FinalValidationTime.HasValue) // If validation time is frozen, display it
                {
                    sb.Append($"Validation: {FinalValidationTime.Value:hh\\:mm\\:ss}");
                }
                else if (ValidationStartTime.HasValue) // Otherwise, calculate running time
                {
                    var validationTime = (DateTime.Now - ValidationStartTime.Value);
                    sb.Append($"Validation: {validationTime:hh\\:mm\\:ss}");
                }

                if (PatchingStartTime.HasValue) // Patching timer logic is separate and simple
                {
                    if (sb.Length > 0) sb.Append(" | ");
                    var patchingTime = (DateTime.Now - PatchingStartTime.Value);
                    sb.Append($"Execution: {patchingTime:hh\\:mm\\:ss}");
                }

                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(CurrentProgressMessage);

                ProgressText = sb.ToString();
            })
            .DisposeWith(_disposables);
        // --- End of New Logic ---

        // Bat command logic
        var canGenerateBat = this.WhenAnyValue(
            x => x.IsRunning,
            x => x.SelectedNpcGroup,
            (running, group) => !running && !string.IsNullOrEmpty(group) &&
                                _environmentStateProvider.Status == EnvironmentStateProvider.EnvironmentStatus.Valid
        );

        GenerateSpawnBatCommand = ReactiveCommand.CreateFromTask(GenerateSpawnBatFileAsync, canGenerateBat)
            .DisposeWith(_disposables);

        GenerateSpawnBatCommand.ThrownExceptions.Subscribe(ex =>
        {
            AppendLog($"ERROR: Failed to generate spawn bat file: {ExceptionLogger.GetExceptionStack(ex)}", true);
        }).DisposeWith(_disposables);

        ShowStatusCommand = ReactiveCommand.CreateFromTask(GenerateEnvironmentReportAsync).DisposeWith(_disposables);

        ShowStatusCommand.ThrownExceptions.Subscribe(ex =>
        {
            AppendLog($"ERROR: Failed to get status report: {ExceptionLogger.GetExceptionStack(ex)}", true);
        }).DisposeWith(_disposables);

        _logMessageSubject
            .Buffer(TimeSpan.FromMilliseconds(250), RxApp.TaskpoolScheduler) // Collect messages for 250ms
            .Where(buffer => buffer.Any()) // Only continue if there are messages in the buffer
            .ObserveOn(RxApp.MainThreadScheduler) // Switch to the UI thread to update the LogOutput property
            .Subscribe(messages =>
            {
                foreach (var msg in messages)
                {
                    _logBuilder.AppendLine(msg);
                }

                LogOutput = _logBuilder.ToString(); // Update the UI property only ONCE per batch
            })
            .DisposeWith(_disposables);

        // Update Available Groups when NpcGroupAssignments changes in settings
        UpdateAvailableGroups();

        // Subscribe to VM_Settings EnvironmentStatus changes to refresh groups when env becomes valid
        _vmSettings.WhenAnyValue(x => x.EnvironmentStatus)
            .Where(status => status == EnvironmentStateProvider.EnvironmentStatus.Valid)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateAvailableGroups())
            .DisposeWith(_disposables);

        // Subscribe to group change messages
        MessageBus.Current.Listen<NpcGroupsChangedMessage>()
            .ObserveOn(RxApp.MainThreadScheduler) // Ensure update happens on UI thread
            .Subscribe(_ =>
            {
                AppendLog("NPC Groups potentially changed. Refreshing dropdown..."); // Verbose only
                UpdateAvailableGroups();
            })
            .DisposeWith(_disposables); // Add subscription to disposables
        
        // Analyze Masters command - can execute when not running and environment is valid
        var canAnalyzeMasters = this.WhenAnyValue(
            x => x.IsRunning,
            x => x._environmentStateProvider.Status,
            (running, status) => !running && status == EnvironmentStateProvider.EnvironmentStatus.Valid);

        AnalyzeMastersCommand = ReactiveCommand.CreateFromTask(AnalyzeMastersAsync, canAnalyzeMasters)
            .DisposeWith(_disposables);

        AnalyzeMastersCommand.ThrownExceptions.Subscribe(ex =>
        {
            AppendLog($"ERROR: Failed to analyze masters: {ExceptionLogger.GetExceptionStack(ex)}", true);
        }).DisposeWith(_disposables);
    }

    private void TogglePatcherExecution()
    {
        if (IsRunning)
        {
            // If it's running, cancel.
            AppendLog("Cancellation requested by user.");
            _patchingCts?.Cancel();
        }
        else
        {
            // If it's not running, start the patching process in the background.
            // We use `_ = ` to discard the task, telling the compiler we are intentionally not awaiting it.
            _ = ExecutePatchingAsync();
        }
    }


    private async Task ExecutePatchingAsync()
    {
        _patchingCts = new CancellationTokenSource();
        var token = _patchingCts.Token;

        try
        {
            // MANUALLY set IsRunning to true. This will update the UI.
            IsRunning = true;
            ValidationStartTime = null;
            PatchingStartTime = null;

            // --- *** Save Mod Settings Before Proceeding *** ---
            try
            {
                var vmMods = _lazyVmMods.Value;
                if (vmMods == null) throw new InvalidOperationException("VM_Mods instance could not be resolved.");
                vmMods.SaveModSettingsToModel();
            }
            catch (Exception ex)
            {
                AppendLog($"CRITICAL ERROR: Failed to save Mod Settings: {ExceptionLogger.GetExceptionStack(ex)}",
                    true);
                return; // Abort
            }

            // --- *** End Save Mod Settings *** ---
            if (_settings.ModSettings == null || !_settings.ModSettings.Any())
            {
                AppendLog("ERROR: No Mod Settings configured. Aborting.", true);
                return; // Abort
            }

            var modSettingsMap = _patcher.BuildModSettingsMap();
            await _patcher.PreInitializationLogicAsync();

            ValidationStartTime = DateTime.Now;

            var validationReport = await _validator.ScreenSelectionsAsync(modSettingsMap, SelectedNpcGroup, token);
            FinalValidationTime = DateTime.Now - ValidationStartTime.Value;

            bool continuePatching = true;
            if (validationReport.InvalidSelections.Any())
            {
                CurrentProgressMessage = "Waiting for user input...";

                var message =
                    new StringBuilder(
                        $"Found {validationReport.InvalidSelections.Count} invalid NPC selection(s) that will be skipped:\n\n");
                message.AppendLine(string.Join("\n", validationReport.InvalidSelections));
                message.AppendLine(
                    "\nThese selections point to missing NPCs or mod folders. Continue with the valid selections?");

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    continuePatching = ScrollableMessageBox.Confirm(message.ToString(), "Invalid Selections Found");
                });
            }

            if (continuePatching)
            {
                PatchingStartTime = DateTime.Now;

                var validSelections = _validator.GetScreeningCache().Where(kv => kv.Value.SelectionIsValid).ToList();

                // --- NEW: Splitting Logic ---
                if (_settings.SplitOutput && validSelections.Any())
                {
                    AppendLog($"\nSplitting output based on user settings...", forceLog: true);
                    var batches = CreatePatchingBatches(validSelections);
                    AppendLog($"Created {batches.Count} patching batches.", forceLog: true);

                    for (int i = 0; i < batches.Count; i++)
                    {
                        var batch = batches[i];
                        token.ThrowIfCancellationRequested();

                        string originalPluginName =
                            Path.GetFileNameWithoutExtension(_environmentStateProvider.OutputPluginName);
                        string newPluginName = string.IsNullOrWhiteSpace(batch.Suffix)
                            ? originalPluginName
                            : $"{originalPluginName}_{batch.Suffix}";

                        AppendLog(
                            $"\n--- Processing Batch {i + 1}/{batches.Count}: {newPluginName} ({batch.Selections.Count} NPCs) ---",
                            forceLog: true);

                        // Update environment with the new output mod name for this batch run
                        _environmentStateProvider.OutputMod = new SkyrimMod(
                            ModKey.FromName(newPluginName, ModType.Plugin), _environmentStateProvider.SkyrimVersion);

                        // Call patcher with just the NPCs for this specific batch
                        await _patcher.RunPatchingLogic(batch.Selections, false, i == 0, token);
                    }
                }
                else
                {
                    // If not splitting, run the patcher once with all valid selections
                    await _patcher.RunPatchingLogic(validSelections, false, true, token);
                }
                // --- END: Splitting Logic ---

                if (PatchingStartTime.HasValue && FinalValidationTime.HasValue)
                {
                    var patchingDuration = DateTime.Now - PatchingStartTime.Value;
                    var totalDuration = FinalValidationTime.Value + patchingDuration;
                    AppendLog($"\nPatch generation process completed in {totalDuration:hh\\:mm\\:ss}.", forceLog: true);
                }
            }
            else
            {
                AppendLog("Patching cancelled by user due to invalid selections.");
                ResetProgress();
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Patching was cancelled.", false, true);
            ResetProgress();
        }
        catch (Exception ex)
        {
            // Centralized exception handling for the async process
            AppendLog($"ERROR: {ex.GetType().Name} - {ExceptionLogger.GetExceptionStack(ex)}", true);
            AppendLog(ExceptionLogger.GetExceptionStack(ex), true);
            AppendLog("ERROR: Patching failed.", true);
            ResetProgress();
        }
        finally
        {
            // CRITICAL: Ensure IsRunning is always set back to false,
            // and the CancellationTokenSource is disposed.
            IsRunning = false;
            _patchingCts?.Dispose();
            _patchingCts = null;
        }
    }

    private async Task GenerateSpawnBatFileAsync()
    {
        string initialDirectory;
        string outputDirSetting = _settings.OutputDirectory;

        // If the OutputDirectory is a full, absolute path, use it directly.
        if (!string.IsNullOrWhiteSpace(outputDirSetting) && Path.IsPathRooted(outputDirSetting))
        {
            initialDirectory = outputDirSetting;
        }
        // If the ModsFolder is valid, combine it with the relative OutputDirectory.
        else if (!string.IsNullOrWhiteSpace(_settings.ModsFolder) && Directory.Exists(_settings.ModsFolder))
        {
            initialDirectory = Path.Combine(_settings.ModsFolder, outputDirSetting);
        }
        else
        {
            // As a fallback, use the game's Data folder.
            initialDirectory = _environmentStateProvider.DataFolderPath;
        }
        // --- End of new logic ---

        string groupNameForFile = SelectedNpcGroup;
        groupNameForFile = Regex.Replace(groupNameForFile, @"[^a-zA-Z0-9]", "");

        var saveFileDialog = new SaveFileDialog
        {
            // Use the newly calculated directory path.
            InitialDirectory = initialDirectory,
            FileName = $"{groupNameForFile}.txt",
            DefaultExt = ".txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };

        if (saveFileDialog.ShowDialog() != true)
        {
            AppendLog("Spawn bat file generation cancelled by user.");
            return;
        }

        try
        {
            AppendLog($"Generating spawn bat file for group '{SelectedNpcGroup}'...");

            List<FormKey> npcsToProcess;

            if (SelectedNpcGroup == ALL_NPCS_GROUP)
            {
                // If "All NPCs" is selected, get all NPCs that have any appearance mod selected.
                // This data comes from the dictionary updated by the NpcConsistencyProvider.
                npcsToProcess = _settings.SelectedAppearanceMods.Keys.ToList();
            }
            else
            {
                // Otherwise, get NPCs from the specifically selected group.
                npcsToProcess = _settings.NpcGroupAssignments
                    .Where(kvp =>
                        kvp.Value != null && kvp.Value.Contains(SelectedNpcGroup, StringComparer.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key)
                    .ToList();
            }

            if (!npcsToProcess.Any())
            {
                // Provide a more specific warning message based on the user's selection.
                string warningMessage = SelectedNpcGroup == ALL_NPCS_GROUP
                    ? "Warning: No appearance selections have been made. File will be empty."
                    : $"Warning: No NPCs found in group '{SelectedNpcGroup}'. File will be empty.";

                AppendLog(warningMessage, isError: true);
                await File.WriteAllTextAsync(saveFileDialog.FileName, string.Empty);
                return;
            }

            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(_settings.BatFilePreCommands))
            {
                sb.AppendLine(_settings.BatFilePreCommands);
            }

            int successCount = 0;
            foreach (var npcFormKey in npcsToProcess)
            {
                string formId = _aux.FormKeyToFormIDString(npcFormKey);
                if (!string.IsNullOrEmpty(formId))
                {
                    sb.AppendLine($"player.placeatme {formId}");
                    successCount++;
                }
                else
                {
                    AppendLog($"Warning: Could not resolve FormID for {npcFormKey}. It will be skipped.",
                        isError: true);
                }
            }

            if (!string.IsNullOrWhiteSpace(_settings.BatFilePostCommands))
            {
                sb.AppendLine(_settings.BatFilePostCommands);
            }

            await File.WriteAllTextAsync(saveFileDialog.FileName, sb.ToString());
            AppendLog($"Successfully generated spawn bat file with {successCount} NPC(s) at: {saveFileDialog.FileName}",
                forceLog: true);
        }
        catch (Exception ex)
        {
            AppendLog(
                $"FATAL: An unexpected error occurred during bat file generation: {ExceptionLogger.GetExceptionStack(ex)}",
                true);
        }
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

    /// <summary>
    /// Handles the Analyze Masters command execution.
    /// Prompts user to select a plugin, shows master selection dialog, then displays analysis results.
    /// </summary>
    private async Task AnalyzeMastersAsync()
    {
        // Step 1: Prompt user to select an ESP/ESM/ESL file
        var openFileDialog = new OpenFileDialog
        {
            Title = "Select Plugin to Analyze",
            Filter = "Plugin files (*.esp;*.esm;*.esl)|*.esp;*.esm;*.esl|All files (*.*)|*.*",
            InitialDirectory = _environmentStateProvider.DataFolderPath,
            CheckFileExists = true
        };

        if (openFileDialog.ShowDialog() != true)
        {
            AppendLog("Master analysis cancelled - no file selected.");
            return;
        }

        string targetPluginPath = openFileDialog.FileName;
        AppendLog($"Selected plugin for analysis: {Path.GetFileName(targetPluginPath)}", forceLog: true);

        // Step 2: Read masters from the selected plugin
        var masters = _masterAnalyzer.GetMastersFromPlugin(targetPluginPath);

        if (!masters.Any())
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ScrollableMessageBox.ShowWarning(
                    $"The selected plugin '{Path.GetFileName(targetPluginPath)}' has no master files listed in its header.",
                    "No Masters Found");
            });
            return;
        }

        AppendLog($"Found {masters.Count} master(s) in plugin header.", forceLog: true);

        // Step 3: Show master selection dialog
        List<ModKey>? selectedMasters = null;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            var selectionWindow = new Views.MasterSelectionWindow();
    
            // Find the main window safely - avoid setting Owner to itself
            var mainWindow = Application.Current.Windows
                                 .OfType<Window>()
                                 .FirstOrDefault(w => w is not Views.MasterSelectionWindow && w.IsActive)
                             ?? Application.Current.Windows
                                 .OfType<Window>()
                                 .FirstOrDefault(w => w is not Views.MasterSelectionWindow);
    
            if (mainWindow != null && mainWindow != selectionWindow)
            {
                selectionWindow.Owner = mainWindow;
            }
    
            selectionWindow.Initialize(targetPluginPath, masters);

            if (selectionWindow.ShowDialog() == true)
            {
                selectedMasters = selectionWindow.SelectedMasters;
            }
        });

        if (selectedMasters == null || !selectedMasters.Any())
        {
            AppendLog("Master analysis cancelled - no masters selected.");
            return;
        }

        AppendLog($"Analyzing {selectedMasters.Count} selected master(s)...", forceLog: true);

        // Step 4: Run the analysis
        MasterAnalysisResult? result = null;

        try
        {

            // Run analysis on a background thread
            result = await Task.Run(() =>
                _masterAnalyzer.AnalyzeMasterReferences(
                    targetPluginPath,
                    selectedMasters,
                    IsVerboseModeEnabled));
        }
        catch (OperationCanceledException)
        {
            AppendLog("Master analysis was cancelled.");
            ResetProgress();
            return;
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR during master analysis: {ex.Message}", true);
            ResetProgress();
            return;
        }

        ResetProgress();

        if (result == null)
        {
            AppendLog("ERROR: Analysis returned no results.", true);
            return;
        }

        // Step 5: Format and display results
        string report = _masterAnalyzer.FormatAnalysisReport(result);

        // Log summary to the Run view log
        int totalReferences = result.ReferencesByMaster.Values.Sum(list => list.Count);
        AppendLog(
            $"Analysis complete. Found {totalReferences} total reference(s) across {selectedMasters.Count} master(s).",
            forceLog: true);

        // Show detailed results in ScrollableMessageBox
        Application.Current?.Dispatcher.Invoke(() => { ScrollableMessageBox.Show(report, "Master Analysis Results"); });
    }

    private record PatchingBatch(string Suffix, List<KeyValuePair<FormKey, ScreeningResult>> Selections);

    /// <summary>
    /// Creates virtual groups ("batches") of NPCs to be processed into separate plugin files.
    /// </summary>
    private List<PatchingBatch> CreatePatchingBatches(List<KeyValuePair<FormKey, ScreeningResult>> validSelections)
    {
        var batches = new List<PatchingBatch>();
        if (!validSelections.Any()) return batches;

        // Group selections by the chosen criteria (gender and/or race).
        var groupedByCriteria = validSelections.GroupBy(kvp =>
        {
            var npc = kvp.Value.WinningNpcOverride;

            string genderKey = _settings.SplitOutputByGender ? Auxilliary.GetGender(npc).ToString() : string.Empty;
            string raceKey = string.Empty;
            if (_settings.SplitOutputByRace)
            {
                raceKey = npc.Race.TryResolve(_environmentStateProvider.LinkCache, out var raceRecord)
                    ? (raceRecord.EditorID ?? "UnknownRace")
                    : "UnknownRace";
            }

            // Sanitize raceKey to be filename-friendly
            raceKey = Regex.Replace(raceKey, @"[^a-zA-Z0-9]", "");

            return (Gender: genderKey, Race: raceKey);
        });

        int maxNpcsPerPlugin = _settings.SplitOutputMaxNpcs ?? int.MaxValue;

        // Process each criteria group (e.g., all Male Nords).
        foreach (var group in groupedByCriteria)
        {
            var npcsInGroup = group.ToList();
            int totalInGroup = npcsInGroup.Count;
            int currentOffset = 0;
            int subBatchCounter = 1;

            // Sub-divide the criteria group by the max number of NPCs.
            while (currentOffset < totalInGroup)
            {
                var chunk = npcsInGroup.Skip(currentOffset).Take(maxNpcsPerPlugin).ToList();

                var nameParts = new List<string>();
                if (_settings.SplitOutputByGender && !string.IsNullOrEmpty(group.Key.Gender))
                    nameParts.Add(group.Key.Gender);
                if (_settings.SplitOutputByRace && !string.IsNullOrEmpty(group.Key.Race))
                    nameParts.Add(group.Key.Race);

                // Only add a numeric suffix if the group was large enough to be split.
                if (totalInGroup > maxNpcsPerPlugin)
                {
                    nameParts.Add(subBatchCounter.ToString());
                }

                string batchSuffix = string.Join("_", nameParts.Where(s => !string.IsNullOrEmpty(s)));

                batches.Add(new PatchingBatch(batchSuffix, chunk));

                currentOffset += maxNpcsPerPlugin;
                subBatchCounter++;
            }
        }

        return batches;
    }

    private void ResetProgress()
    {
        ProgressValue = 0;
        CurrentProgressMessage = string.Empty;
        ValidationStartTime = null;
        PatchingStartTime = null;
    }

    private void UpdateProgress(int current, int total, string message)
    {
        RxApp.MainThreadScheduler.Schedule(() =>
        {
            if (total > 0)
            {
                ProgressValue = (double)current / total * 100.0;
                CurrentProgressMessage = $"[{current}/{total}] {message}";
            }
            else
            {
                ProgressValue = 0;
                CurrentProgressMessage = message;
            }
        });
    }

    private async Task GenerateEnvironmentReportAsync()
    {
        AppendLog("Program Version: " + App.ProgramVersion, forceLog: true);
        AppendLog("===Game Environment===", forceLog: true);
        AppendLog("Game Type: " + _environmentStateProvider.SkyrimVersion, forceLog: true);
        AppendLog("Game Directory: " + _environmentStateProvider.DataFolderPath, forceLog: true);
        AppendLog("Creation Club Path: " + _environmentStateProvider.CreationClubListingsFilePath, forceLog: true);
        AppendLog(
            "Core Plugins:" + Environment.NewLine + string.Join(Environment.NewLine,
                _environmentStateProvider.BaseGamePlugins.Select(x => "\t" + x.ToString())), forceLog: true);
        AppendLog(
            "CC Plugins:" + Environment.NewLine + string.Join(Environment.NewLine,
                _environmentStateProvider.CreationClubPlugins.Select(x => "\t" + x.ToString())), forceLog: true);
        AppendLog(
            "Load Order:" + Environment.NewLine + string.Join(Environment.NewLine,
                _environmentStateProvider.LoadOrder.Select(x =>
                    "\t" + (x.Value.Enabled ? "*" : "-") + x.Key.ToString())), forceLog: true);
        AppendLog("Environment Status: " + _environmentStateProvider.Status, forceLog: true);
        AppendLog(Environment.NewLine, forceLog: true);
        AppendLog("===Program Variables===", forceLog: true);
        AppendLog(_vmSettings.GetStatusReport(), forceLog: true);
        AppendLog(_lazyVmMods.Value.GetStatusReport(), forceLog: true);
        AppendLog(Environment.NewLine, forceLog: true);
        AppendLog("Plugin Provider: " + _pluginProvider.GetStatusReport(), forceLog: true);
        AppendLog("Record Handler: " + _recordHandler.GetStatusReport(), forceLog: true);
        AppendLog("BSA Handler: " + _bsaHandler.GetStatusReport(), forceLog: true);
    }

    // Add Dispose method if not present
    public void Dispose()
    {
        _disposables.Dispose();
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

        // Instead of scheduling directly on the UI thread,
        // push the message to the subject, which will handle batching and updating.
        _logMessageSubject.OnNext(message);
    }

    public void ResetLog()
    {
        LogOutput = string.Empty;
    }
}