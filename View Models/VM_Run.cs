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
using NPC_Plugin_Chooser_2.Views;
using Serilog; // Needed for LinkCache Interface

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_Run : ReactiveObject
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _settings;
        private readonly VM_Settings _vmSettings;
        private readonly Lazy<VM_Mods> _lazyVmMods;
        private readonly Patcher _patcher;
        private readonly Validator _validator;
        private readonly AssetHandler _assetHandler;
        private readonly BsaHandler _bsaHandler;
        private readonly RecordDeltaPatcher _recordDeltaPatcher;
        private CancellationTokenSource? _patchingCts;
        private readonly CompositeDisposable _disposables = new();

        // --- Constants ---
        public const string ALL_NPCS_GROUP = "<All NPCs>";


        // --- Logging & State ---
        [Reactive] public string LogOutput { get; private set; } = string.Empty;
        [Reactive] public bool IsRunning { get; private set; }
        [Reactive] public string RunButtonText { get; private set; } = "Run Patch Generation";
        [Reactive] public double ProgressValue { get; private set; } = 0;
        [Reactive] public string ProgressText { get; private set; } = string.Empty;
        [Reactive] public bool IsVerboseModeEnabled { get; set; } = false; // Default to non-verbose


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

        public VM_Run(
            EnvironmentStateProvider environmentStateProvider,
            Settings settings,
            VM_Settings vmSettings,
            Lazy<VM_Mods> lazyVmMods,
            Patcher patcher,
            Validator validator,
            AssetHandler assetHandler,
            BsaHandler bsaHandler,
            RecordDeltaPatcher recordDeltaPatcher)
        {
            _environmentStateProvider = environmentStateProvider;
            _settings = settings;
            _vmSettings = vmSettings;
            _lazyVmMods = lazyVmMods;
            _patcher = patcher;
            _validator = validator;
            _assetHandler = assetHandler;
            _bsaHandler = bsaHandler;
            _recordDeltaPatcher = recordDeltaPatcher;
            
            _patcher.ConnectToUILogger(AppendLog, UpdateProgress, ResetProgress, ResetLog);
            _validator.ConnectToUILogger(AppendLog, UpdateProgress, ResetProgress, ResetLog);
            _assetHandler.ConnectToUILogger(AppendLog, UpdateProgress, ResetProgress, ResetLog);
            _bsaHandler.ConnectToUILogger(AppendLog, UpdateProgress, ResetProgress, ResetLog);
            _recordDeltaPatcher.ConnectToUILogger(AppendLog, UpdateProgress, ResetProgress, ResetLog);
            
            this.WhenAnyValue(x => x.IsRunning)
                .Select(isRunning => isRunning ? "Cancel Patching" : "Run Patch Generation")
                .ObserveOn(RxApp.MainThreadScheduler)
                .BindTo(this, x => x.RunButtonText);

            // Command should be executable if the environment is valid (to start) OR if it's already running (to cancel).
            var canExecute = this.WhenAnyValue(
                x => x.IsRunning,
                x => x._environmentStateProvider.EnvironmentIsValid,
                (running, valid) => running || valid);

            // This command's delegate is SYNCHRONOUS. It fires off the async work or cancels it.
            RunCommand = ReactiveCommand.Create(TogglePatcherExecution, canExecute);

            // DO NOT bind IsExecuting to IsRunning. We are managing IsRunning manually.
            
            // Note: Since the command's task is now synchronous and short-lived,
            // the ThrownExceptions subscription is less likely to fire for patching errors.
            // We will handle exceptions within the async method itself.
            RunCommand.ThrownExceptions.Subscribe(ex =>
            {
                // This will now only catch rare errors within TogglePatcherExecution itself.
                AppendLog($"FATAL UI ERROR: {ExceptionLogger.GetExceptionStack(ex)}", true);
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

                // --- *** Save Mod Settings Before Proceeding *** ---
                try
                {
                    var vmMods = _lazyVmMods.Value;
                    if (vmMods == null) throw new InvalidOperationException("VM_Mods instance could not be resolved.");
                    vmMods.SaveModSettingsToModel();
                }
                catch (Exception ex)
                {
                    AppendLog($"CRITICAL ERROR: Failed to save Mod Settings: {ExceptionLogger.GetExceptionStack(ex)}", true);
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

                bool canRun = await _validator.ScreenSelectionsAsync(modSettingsMap, token);

                if (canRun)
                {
                    await _patcher.RunPatchingLogic(SelectedNpcGroup, token);
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
                AppendLog($"ERROR: {ex.GetType().Name} - {ex.Message}", true);
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

            RxApp.MainThreadScheduler.Schedule(message, (sched, msg) =>
            {
                _logBuilder.AppendLine(msg);
                LogOutput = _logBuilder.ToString();
                return Disposable.Empty;
            });
        }

        public void ResetLog()
        {
            LogOutput = string.Empty;
        }
    }
}