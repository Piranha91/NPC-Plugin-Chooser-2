using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Noggog; // For IsNullOrWhitespace
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using NPC_Plugin_Chooser_2.Themes;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_Settings : ReactiveObject, IDisposable, IActivatableViewModel
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Auxilliary _aux;
        private readonly Settings _model; // Renamed from settings to _model for clarity
        private readonly Lazy<VM_NpcSelectionBar> _lazyNpcSelectionBar;
        private readonly Lazy<VM_Mods> _lazyModListVM;
        private readonly NpcConsistencyProvider _consistencyProvider; 
        private readonly Lazy<VM_MainWindow> _lazyMainWindowVm;
        private readonly EasyNpcTranslator _easyNpcTranslator;
        
        public ViewModelActivator Activator { get; } = new ViewModelActivator();

        public int NumPluginsInEnvironment => _environmentStateProvider.NumPlugins;
        public int NumActivePluginsInEnvironment => _environmentStateProvider.NumActivePlugins;

        // --- Existing & Modified Properties ---
        [Reactive] public string ModsFolder { get; set; }
        [Reactive] public string MugshotsFolder { get; set; }
        [Reactive] public SkyrimRelease SkyrimRelease { get; set; }
        [Reactive] public string SkyrimGamePath { get; set; }
        // TargetPluginName now maps to the conceptual name in the model
        [Reactive] public string TargetPluginName { get; set; }

        // --- New Output Settings Properties ---
        [Reactive] public string OutputDirectory { get; set; }
        [Reactive] public bool AppendTimestampToOutputDirectory { get; set; }
        [Reactive] public bool UseSkyPatcherMode { get; set; }
        [Reactive] public bool AutoEslIfy { get; set; } = true;
        [Reactive] public bool SplitOutput { get; set; }
        [Reactive] public bool SplitOutputByGender { get; set; }
        [Reactive] public bool SplitOutputByRace { get; set; }
        [Reactive] public int? SplitOutputMaxNpcs { get; set; }
        
        [Reactive] public PatchingMode SelectedPatchingMode { get; set; }
        public IEnumerable<PatchingMode> PatchingModes { get; } = Enum.GetValues(typeof(PatchingMode)).Cast<PatchingMode>();
        [Reactive] public RecordOverrideHandlingMode SelectedRecordOverrideHandlingMode { get; set; }
        public IEnumerable<RecordOverrideHandlingMode> RecordOverrideHandlingModes { get; } = Enum.GetValues(typeof(RecordOverrideHandlingMode)).Cast<RecordOverrideHandlingMode>();
        public ReactiveCommand<Unit, Unit> ShowOverrideHandlingModeHelpCommand { get; }

        // --- New EasyNPC Transfer Properties ---
        // Assuming ModKeyMultiPicker binds directly to an ObservableCollection-like source
        // We will wrap the HashSet from the model. Consider a more robust solution if direct binding causes issues.
        [Reactive] public VM_ModSelector ExclusionSelectorViewModel { get; private set; }
        [ObservableAsProperty] public IEnumerable<ModKey> AvailablePluginsForExclusion { get; }
        
        // --- Bat File Properties
        [Reactive] public string BatFilePreCommands { get; set; }
        [Reactive] public string BatFilePostCommands { get; set; }

        // --- Read-only properties reflecting environment state ---
        [ObservableAsProperty] public EnvironmentStateProvider.EnvironmentStatus EnvironmentStatus { get; }
        [ObservableAsProperty] public string EnvironmentErrorText { get; }
        
        // --- Properties for modifying EasyNPC Import/Export ---
        [Reactive] public bool AddMissingNpcsOnUpdate { get; set; } = true; // Default to true
        
        // --- Properties for Auto-Selection of NPC Appearances
        [Reactive] public VM_ModSelector ImportFromLoadOrderExclusionSelectorViewModel { get; private set; }
        
        // --- NEW: Properties for Non-Appearance Mod Filtering ---
        [Reactive] public string NonAppearanceModFilterText { get; set; } = string.Empty;
        public ObservableCollection<KeyValuePair<string, string>> FilteredNonAppearanceMods { get; } = new();

        // MODIFIED: Type changed to handle dictionary from model
        public ObservableCollection<KeyValuePair<string, string>> CachedNonAppearanceMods { get; private set; }
        
        // --- New: Properties for display settings
        [Reactive] public bool NormalizeImageDimensions { get; set; }
        [Reactive] public int MaxMugshotsToFit { get; set; }
        [Reactive] public bool IsDarkMode { get; set; }
        [Reactive] public bool SuppressPopupWarnings { get; set; }
        // --- NEW: Localization Settings ---
        [Reactive] public bool IsLocalizationEnabled { get; set; }
        [Reactive] public Language? SelectedLocalizationLanguage { get; set; }
        public IEnumerable<Language> AvailableLanguages { get; } = Enum.GetValues(typeof(Language)).Cast<Language>();

        
        // For throttled saving
        private readonly Subject<Unit> _saveRequestSubject = new Subject<Unit>();
        private readonly CompositeDisposable _disposables = new CompositeDisposable(); // To manage subscriptions
        private readonly TimeSpan _saveThrottleTime = TimeSpan.FromMilliseconds(1500);

        // --- Commands ---
        public ReactiveCommand<Unit, Unit> SelectGameFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectModsFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectMugshotsFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectOutputDirectoryCommand { get; } // New
        public ReactiveCommand<Unit, Unit> ShowPatchingModeHelpCommand { get; } // New
        public ReactiveCommand<Unit, Unit> ImportEasyNpcCommand { get; } // New
        public ReactiveCommand<Unit, Unit> ExportEasyNpcCommand { get; } // New
        public ReactiveCommand<bool, Unit> UpdateEasyNpcProfileCommand { get; } // Takes bool parameter
        public ReactiveCommand<string, Unit> RemoveCachedModCommand { get; }
        public ReactiveCommand<string, Unit> RescanCachedModCommand { get; }

        public VM_Settings(
            EnvironmentStateProvider environmentStateProvider,
            Auxilliary aux,
            Settings settingsModel,
            Lazy<VM_NpcSelectionBar> lazyNpcSelectionBar,
            Lazy<VM_Mods> lazyModListVm,
            NpcConsistencyProvider consistencyProvider,
            Lazy<VM_MainWindow> lazyMainWindowVm,
            EasyNpcTranslator easyNpcTranslator) 
        {
            _model = settingsModel;
            
            _environmentStateProvider = environmentStateProvider;
            _environmentStateProvider.OnEnvironmentUpdated
                .ObserveOn(RxApp.MainThreadScheduler) // Ensure UI updates happen on the UI thread
                .Subscribe(_ =>
                {
                    this.RaisePropertyChanged(nameof(NumPluginsInEnvironment));
                    this.RaisePropertyChanged(nameof(NumActivePluginsInEnvironment));
                })
                .DisposeWith(_disposables);
                        
            // At the time ths constructor is called, the model has already been read in via App.xaml.cs
            // This should be refactored later, but for now treat the _model values as the ground truth
            SkyrimRelease = _model?.SkyrimRelease ?? SkyrimRelease.SkyrimSE;
            SkyrimGamePath = _model?.SkyrimGamePath ?? string.Empty;
            TargetPluginName = _model?.OutputPluginName ?? EnvironmentStateProvider.DefaultPluginName; // Model's OutputPluginName is the source of truth
            if (TargetPluginName.IsNullOrEmpty())
            {
                TargetPluginName = EnvironmentStateProvider.DefaultPluginName;
            }
            // Now that the environment parameters have been set, initialize the environment
            _environmentStateProvider.SetEnvironmentTarget(SkyrimRelease, SkyrimGamePath, TargetPluginName);
            _environmentStateProvider.UpdateEnvironment();
            // Finished environment initialization
            
            _aux = aux;
            _lazyNpcSelectionBar = lazyNpcSelectionBar;
            _lazyModListVM = lazyModListVm;
            _consistencyProvider = consistencyProvider;
            _lazyMainWindowVm = lazyMainWindowVm;
            _easyNpcTranslator = easyNpcTranslator;

            // Initialize other VM properties from the model
            ModsFolder = _model.ModsFolder;
            MugshotsFolder = _model.MugshotsFolder;
            OutputDirectory = _model.OutputDirectory;
            UseSkyPatcherMode = _model.UseSkyPatcherMode;
            AutoEslIfy = _model.AutoEslIfy;
            SplitOutput = _model.SplitOutput;
            SplitOutputByGender = _model.SplitOutputByGender;
            SplitOutputByRace = _model.SplitOutputByRace;
            SplitOutputMaxNpcs = _model.SplitOutputMaxNpcs;
            AppendTimestampToOutputDirectory = _model.AppendTimestampToOutputDirectory;
            SelectedPatchingMode = _model.PatchingMode;
            SelectedRecordOverrideHandlingMode = _model.DefaultRecordOverrideHandlingMode;
            AddMissingNpcsOnUpdate = _model.AddMissingNpcsOnUpdate;
            BatFilePreCommands = _model.BatFilePreCommands;
            BatFilePostCommands = _model.BatFilePostCommands;
            NormalizeImageDimensions = _model.NormalizeImageDimensions;
            MaxMugshotsToFit = _model.MaxMugshotsToFit;
            SuppressPopupWarnings = _model.SuppressPopupWarnings;
            IsLocalizationEnabled = _model.LocalizationLanguage.HasValue;
            SelectedLocalizationLanguage = _model.LocalizationLanguage;
            IsDarkMode = _model.IsDarkMode;
            
            ExclusionSelectorViewModel = new VM_ModSelector(); // Initialize early
            ImportFromLoadOrderExclusionSelectorViewModel = new VM_ModSelector();
            
            // Initialize the collection from the model, ordered by folder name for consistent display.
            CachedNonAppearanceMods = new ObservableCollection<KeyValuePair<string, string>>(_model.CachedNonAppearanceMods.OrderBy(kvp => Path.GetFileName(kvp.Key) ?? kvp.Key, StringComparer.OrdinalIgnoreCase));

            // Commands (as before)
            SelectGameFolderCommand = ReactiveCommand.CreateFromTask(SelectGameFolderAsync);
            SelectModsFolderCommand = ReactiveCommand.CreateFromTask(SelectModsFolderAsync);
            SelectMugshotsFolderCommand = ReactiveCommand.CreateFromTask(SelectMugshotsFolderAsync);
            SelectOutputDirectoryCommand = ReactiveCommand.CreateFromTask(SelectOutputDirectoryAsync);
            ShowPatchingModeHelpCommand = ReactiveCommand.Create(ShowPatchingModeHelp);
            ShowOverrideHandlingModeHelpCommand = ReactiveCommand.Create(ShowOverrideHandlingModeHelp);
            ImportEasyNpcCommand = ReactiveCommand.Create(_easyNpcTranslator.ImportEasyNpc);
            ExportEasyNpcCommand = ReactiveCommand.Create(_easyNpcTranslator.ExportEasyNpc);
            UpdateEasyNpcProfileCommand = ReactiveCommand.CreateFromTask<bool>(_easyNpcTranslator.UpdateEasyNpcProfile);
            RemoveCachedModCommand = ReactiveCommand.Create<string>(path =>
            {
                if (string.IsNullOrWhiteSpace(path)) return;

                // Remove from the underlying model dictionary
                _model.CachedNonAppearanceMods.Remove(path);

                // Find and remove the KeyValuePair from the view model collections
                var itemToRemove = CachedNonAppearanceMods.FirstOrDefault(kvp => kvp.Key == path);
                if (!itemToRemove.Equals(default(KeyValuePair<string, string>)))
                {
                    CachedNonAppearanceMods.Remove(itemToRemove);
                    FilteredNonAppearanceMods.Remove(itemToRemove);
                }
            });
            RescanCachedModCommand = ReactiveCommand.CreateFromTask<string>(RescanCachedModAsync);
            RescanCachedModCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Failed to re-scan mod: {ex.Message}"));


            // Property subscriptions (as before, ensure .Skip(1) where appropriate if values are set above)
            // These update the model or _environmentStateProvider's direct properties
            this.WhenAnyValue(x => x.ModsFolder).Skip(1).Subscribe(s => _model.ModsFolder = s).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.MugshotsFolder).Skip(1).Subscribe(s => _model.MugshotsFolder = s).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.OutputDirectory).Skip(1).Subscribe(s => _model.OutputDirectory = s).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.AppendTimestampToOutputDirectory).Skip(1).Subscribe(b => _model.AppendTimestampToOutputDirectory = b).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.SelectedPatchingMode).Skip(1).Subscribe(pm => _model.PatchingMode = pm).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.AddMissingNpcsOnUpdate).Skip(1).Subscribe(b => _model.AddMissingNpcsOnUpdate = b).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.BatFilePreCommands).Skip(1).Subscribe(s => _model.BatFilePreCommands = s).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.BatFilePostCommands).Skip(1).Subscribe(s => _model.BatFilePostCommands = s).DisposeWith(_disposables);
            
            this.WhenAnyValue(x => x.SuppressPopupWarnings)
                .Skip(1)
                .Subscribe(b => _model.SuppressPopupWarnings = b)
                .DisposeWith(_disposables);
            
            this.WhenAnyValue(x => x.IsLocalizationEnabled)
                .Skip(1) // Skip initial value set from model
                .Subscribe(enabled =>
                {
                    if (!enabled)
                    {
                        // If localization is disabled, clear the selected language
                        SelectedLocalizationLanguage = null;
                    }
                    else if (SelectedLocalizationLanguage == null)
                    {
                        // If it's enabled and no language is selected, default to English
                        SelectedLocalizationLanguage = Language.English;
                    }
                })
                .DisposeWith(_disposables);
            
            this.WhenAnyValue(x => x.IsDarkMode)
                .Skip(1) // Skip initial value set from model
                .Subscribe(isDark =>
                {
                    _model.IsDarkMode = isDark;
                    ThemeManager.ApplyTheme(isDark);
                })
                .DisposeWith(_disposables);

            this.WhenAnyValue(x => x.SelectedLocalizationLanguage)
                .Skip(1) // Skip initial value
                .Subscribe(lang =>
                {
                    _model.LocalizationLanguage = lang;
                    foreach (var entry in _lazyNpcSelectionBar.Value.AllNpcs)
                    {
                        entry.RefreshName(lang);
                    }
                })
                .DisposeWith(_disposables);
            
            this.WhenAnyValue(x => x.SelectedRecordOverrideHandlingMode)
                .Skip(1) // Skip the initial value loaded from the model
                .Subscribe(mode =>
                {
                    // Only show the warning if the user selects a mode other than the default 'Ignore'
                    if (mode != RecordOverrideHandlingMode.Ignore && !SuppressPopupWarnings)
                    {
                        const string message = "WARNING: Setting the override handling mode to anything other than 'Ignore' is generally not recommended.\n\n" +
                                               "It can significantly increase patching time and is only necessary in very specific, rare scenarios.\n\n" +
                                               "You're almost certainly better off changing this setting only for specific mods in the Mods Menu.\n\n" +
                                               "Are you sure you want to change this setting?";

                        if (!ScrollableMessageBox.Confirm(message, "Confirm Override Handling Mode", MessageBoxImage.Warning))
                        {
                            // If the user clicks "No", use Observable.Timer to schedule the reversion.
                            // This runs on the UI thread after a 1ms delay, breaking the call-stack and ensuring the ComboBox updates.
                            Observable.Timer(TimeSpan.FromMilliseconds(1), RxApp.MainThreadScheduler)
                                .Subscribe(_ =>
                                {
                                    SelectedRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore;
                                });
                        }
                    }

                    // Persist the final value to the model. This will be the original value if the
                    // user confirmed, or it will be re-triggered with 'Ignore' if they cancelled.
                    _model.DefaultRecordOverrideHandlingMode = mode;
                })
                .DisposeWith(_disposables);

            this.WhenAnyValue(x => x.UseSkyPatcherMode)
                .Skip(1) // Skip initial value set on load
                .Subscribe(useSkyPatcher =>
                {
                    if (useSkyPatcher) // If the checkbox was just checked
                    {
                        const string message = "SkyPatcher is a powerful tool for overwriting conflicts. Be aware that it might conflict with other runtime editing tools such as RSV and SynthEBD. Are you sure you want to use SkyPatcher Mode?";
                        if (!SuppressPopupWarnings && !ScrollableMessageBox.Confirm(message, "Confirm SkyPatcher Mode"))
                        {
                            // If user clicks "No", revert the checkbox state
                            UseSkyPatcherMode = false;
                        }
                    }
                    // Persist the final state (whether confirmed true or set to false) to the model
                    _model.UseSkyPatcherMode = UseSkyPatcherMode;
                })
                .DisposeWith(_disposables);
            
            this.WhenAnyValue(x => x.AutoEslIfy)
                .Skip(1)
                .Subscribe(b => _model.AutoEslIfy = b)
                .DisposeWith(_disposables);
            
            this.WhenAnyValue(x => x.SplitOutput).Skip(1).Subscribe(b => _model.SplitOutput = b).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.SplitOutputByGender).Skip(1).Subscribe(b => _model.SplitOutputByGender = b).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.SplitOutputByRace).Skip(1).Subscribe(b => _model.SplitOutputByRace = b).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.SplitOutputMaxNpcs).Skip(1).Subscribe(i => _model.SplitOutputMaxNpcs = i).DisposeWith(_disposables);

            
            // Consolidate all environment update logic into a single, throttled subscription.
            var gameEnvironmentChanged = Observable.Merge(
                this.WhenAnyValue(x => x.SkyrimRelease).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.SkyrimGamePath).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.TargetPluginName).Select(_ => Unit.Default));

            gameEnvironmentChanged
                .Skip(3) // Skip the 3 initial values set in the constructor
                .Throttle(TimeSpan.FromMilliseconds(250), RxApp.MainThreadScheduler) // Throttle to prevent rapid-fire updates
                .Subscribe(_ =>
                {
                    // 1. Update the model to persist the new settings
                    _model.SkyrimRelease = SkyrimRelease;
                    _model.SkyrimGamePath = SkyrimGamePath;
                    _model.OutputPluginName = TargetPluginName;

                    // 2. Set the target and update the environment
                    _environmentStateProvider.SetEnvironmentTarget(SkyrimRelease, SkyrimGamePath, TargetPluginName);
                    _environmentStateProvider.UpdateEnvironment();
                })
                .DisposeWith(_disposables);

            // OAPHs for environment state
            _environmentStateProvider.WhenAnyValue(x => x.Status)
                .ToPropertyEx(this, x => x.EnvironmentStatus)
                .DisposeWith(_disposables);
            
            _environmentStateProvider.WhenAnyValue(x => x.EnvironmentBuilderError)
                .Select(err => string.IsNullOrWhiteSpace(err) ? string.Empty : $"Environment Error: {err}")
                .ToPropertyEx(this, x => x.EnvironmentErrorText)
                .DisposeWith(_disposables);
            
            // Populate AvailablePluginsForExclusion
            this.WhenAnyValue(x => x.EnvironmentStatus)
                .Select(_ => _environmentStateProvider.Status == EnvironmentStateProvider.EnvironmentStatus.Valid && _environmentStateProvider.LoadOrder != null
                    ? _environmentStateProvider.LoadOrder.Keys.OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase).ToList()
                    : Enumerable.Empty<ModKey>())
                .ObserveOn(RxApp.MainThreadScheduler)
                .ToPropertyEx(this, x => x.AvailablePluginsForExclusion)
                .DisposeWith(_disposables);
            
            
            Observable.Merge(
                this.WhenAnyValue(x => x.NonAppearanceModFilterText).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.CachedNonAppearanceMods.Count).Select(_ => Unit.Default) // Use Count property
            )
            .ObserveOn(RxApp.MainThreadScheduler) 
            .Subscribe(_ => {
                ApplyNonAppearanceFilter();
            })
            .DisposeWith(_disposables);
            
            this.WhenAnyValue(x => x.AvailablePluginsForExclusion)
                .Where(list => list != null)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(availablePlugins =>
                {
                    var currentUISelctions = ExclusionSelectorViewModel.SaveToModel(); // Save current UI state
                    ExclusionSelectorViewModel.LoadFromModel(availablePlugins, currentUISelctions, _environmentStateProvider?.LoadOrder?.ListedOrder?.Select(x => x.ModKey).ToList() ?? new List<ModKey>()); // Reload with new available, preserving selections
                    
                    // ADDED: Do the same for the new selector
                    var currentLoadOrderExclusionSelections = ImportFromLoadOrderExclusionSelectorViewModel.SaveToModel();
                    ImportFromLoadOrderExclusionSelectorViewModel.LoadFromModel(availablePlugins, currentLoadOrderExclusionSelections, _environmentStateProvider?.LoadOrder?.ListedOrder?.Select(x => x.ModKey).ToList() ?? new List<ModKey>());
                }).DisposeWith(_disposables);
            
            this.WhenAnyValue(x => x.NormalizeImageDimensions)
                .Skip(1) // Skip the initial value set from the model
                .Subscribe(b => _model.NormalizeImageDimensions = b)
                .DisposeWith(_disposables);
            
            this.WhenAnyValue(x => x.MaxMugshotsToFit)
                .Skip(1)
                .Subscribe(val => _model.MaxMugshotsToFit = val)
                .DisposeWith(_disposables);

            _saveRequestSubject
                .Throttle(_saveThrottleTime, RxApp.TaskpoolScheduler)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => SaveSettings())
                .DisposeWith(_disposables);

            this.WhenActivated(d =>
            {
                // Initial load of exclusions (if EnvironmentStatus might already be true from construction)
                if (EnvironmentStatus == EnvironmentStateProvider.EnvironmentStatus.Valid && AvailablePluginsForExclusion != null && AvailablePluginsForExclusion.Any())
                {
                     ExclusionSelectorViewModel.LoadFromModel(AvailablePluginsForExclusion, _model.EasyNpcDefaultPluginExclusions, _environmentStateProvider.LoadOrder?.ListedOrder?.Select(x => x.ModKey).ToList() ?? new List<ModKey>());
                     ImportFromLoadOrderExclusionSelectorViewModel.LoadFromModel(AvailablePluginsForExclusion, _model.ImportFromLoadOrderExclusions, _environmentStateProvider.LoadOrder?.ListedOrder?.Select(x => x.ModKey).ToList() ?? new List<ModKey>());
                }
                
                // Subscribe to NPC Selection changes for saving LastSelectedNpc
                _lazyNpcSelectionBar.Value
                    .WhenAnyValue(x => x.SelectedNpc)
                    .Skip(1) // Skip initial
                    .Subscribe(npc =>
                    {
                        _model.LastSelectedNpcFormKey = npc?.NpcFormKey ?? FormKey.Null;
                        RequestThrottledSave();
                    })
                    .DisposeWith(d);

                _disposables.Add(d);
            });
        }

        // New method for heavy initialization, called from App.xaml.cs
        public async Task InitializeAsync(VM_SplashScreen? splashReporter)
        {
            const int totalSteps = 3; // Define the total number of steps

            if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
            {
                Thread.Sleep(1000);
            }

            if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
            {
                return;
            }

            try
            {
                // --- STEP 1 ---
                splashReporter?.UpdateStep($"Step 1 of {totalSteps}: Populating mod list...");

                using (ContextualPerformanceTracer.Trace("VM_Settings.PopulateModSettings"))
                {
                    // **NEW ORDER: Populate mods first**
                    splashReporter?.UpdateProgress(70, "Populating mod list...");
                    await _lazyModListVM.Value
                        .PopulateModSettingsAsync(splashReporter); // Base 70%, span 10% (e.g. 70-80)
                }

                // --- STEP 2 ---
                splashReporter?.UpdateStep($"Step 2 of {totalSteps}: Initializing NPC selection bar...");

                using (ContextualPerformanceTracer.Trace("VM_Settings.InitializeNpcSelectionBar"))
                {
                    splashReporter?.UpdateProgress(80, "Initializing NPC selection bar...");
                    await _lazyNpcSelectionBar.Value.InitializeAsync(splashReporter);
                }

                // --- STEP 3 ---
                splashReporter?.UpdateStep($"Step 3 of {totalSteps}: Applying default settings...");

                if (!_model.HasBeenLaunched && _environmentStateProvider.LoadOrder != null)
                {
                    var defaultExclusions = new HashSet<ModKey>();
                    var loadOrderSet = new HashSet<ModKey>(_environmentStateProvider.LoadOrder.Keys);

                    // 1. Add official master files (DLC, etc.) from the current game release if they are in the load order
                    defaultExclusions.UnionWith(_environmentStateProvider.BaseGamePlugins);

                    // 2. Add Creation Club plugins by name
                    defaultExclusions.UnionWith(_environmentStateProvider.CreationClubPlugins);

                    // 3. Add the Unofficial Patch if it exists in the load order
                    var ussepKey = ModKey.FromFileName("unofficial skyrim special edition patch.esp");
                    if (loadOrderSet.Contains(ussepKey))
                    {
                        defaultExclusions.Add(ussepKey);
                    }

                    _model.ImportFromLoadOrderExclusions = defaultExclusions;
                    _model.HasBeenLaunched = true; // Mark defaults as set
                }
                // END: Added Logic

                this.RaisePropertyChanged(nameof(EnvironmentStatus));
                this.RaisePropertyChanged(nameof(EnvironmentErrorText));
                this.RaisePropertyChanged(nameof(AvailablePluginsForExclusion));

                if (EnvironmentStatus == EnvironmentStateProvider.EnvironmentStatus.Valid &&
                    AvailablePluginsForExclusion != null)
                {
                    ExclusionSelectorViewModel.LoadFromModel(AvailablePluginsForExclusion,
                        _model.EasyNpcDefaultPluginExclusions,
                        _environmentStateProvider.LoadOrder.ListedOrder.Select(x => x.ModKey).ToList());
                    ImportFromLoadOrderExclusionSelectorViewModel.LoadFromModel(AvailablePluginsForExclusion,
                        _model.ImportFromLoadOrderExclusions,
                        _environmentStateProvider.LoadOrder.ListedOrder.Select(x => x.ModKey).ToList());
                }

                RefreshNonAppearanceMods();

                // Add the report generation at the very end
                ContextualPerformanceTracer.GenerateDetailedReport("Initial Validation and Load");
            }
            catch (Exception e)
            {
                splashReporter?.ShowMessagesOnClose("An error occured during initialization: " + Environment.NewLine + Environment.NewLine + ExceptionLogger.GetExceptionStack(e));
            }
        }

        public static Settings LoadSettings()
        {
            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
            Settings? loadedSettings = null;
            if (File.Exists(settingsPath))
            {
                 loadedSettings = JSONhandler<Settings>.LoadJSONFile(settingsPath, out bool success, out string exception);
                 if (!success || loadedSettings == null) // Add the null check here
                 {
                     if (success && loadedSettings == null) // Optional: more specific error message
                     {
                         exception = "Settings file was empty or invalid.";
                     }
                     ScrollableMessageBox.ShowWarning($"Error loading settings from {settingsPath}:\n{exception}\n\nDefault settings will be used.", "Settings Load Error");
                     loadedSettings = new Settings(); // Use defaults on error
                 }
            }
            else
            {
                 loadedSettings = new Settings(); // Use defaults if file doesn't exist
            }

            // Ensure defaults for new/potentially missing fields after loading old file
            loadedSettings.OutputPluginName ??= "NPC.esp";
            loadedSettings.OutputDirectory ??= default;
            // AppendTimestampToOutputDirectory default is false (bool default)
            // PatchingMode default is Default (enum default)
            loadedSettings.EasyNpcDefaultPluginExclusions ??= new() { ModKey.FromFileName("Synthesis.esp") };
            loadedSettings.ImportFromLoadOrderExclusions ??= new();
            loadedSettings.BatFilePreCommands ??= string.Empty;
            loadedSettings.BatFilePostCommands ??= string.Empty;
            
            return loadedSettings;
        }
        
        public void RequestThrottledSave()
        {
            _saveRequestSubject.OnNext(Unit.Default);
        }

        public void SaveSettings()
        {
            if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
            {
                return;
            }
            _model.EasyNpcDefaultPluginExclusions = new HashSet<ModKey>(ExclusionSelectorViewModel.SaveToModel());
            _model.ImportFromLoadOrderExclusions = new HashSet<ModKey>(ImportFromLoadOrderExclusionSelectorViewModel.SaveToModel());
            _lazyModListVM.Value.SaveModSettingsToModel();

            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
            _model.ProgramVersion = App.ProgramVersion;
            JSONhandler<Settings>.SaveJSONFile(_model, settingsPath, out bool success, out string exception);
             if (!success)
             {
                  // Maybe show a non-modal warning or log? Saving happens on exit.
                  System.Diagnostics.Debug.WriteLine($"ERROR saving settings: {exception}");
             }
        }

        // --- Folder Selection Methods ---
        private async Task SelectGameFolderAsync() // Renamed for consistency, though it wasn't async before
        {
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select Skyrim Game Folder",
                InitialDirectory = GetSafeInitialDirectory(SkyrimGamePath)
            };

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                SkyrimGamePath = dialog.FileName;
                // SkyrimGamePath change will trigger InitializeAsyncInternal via its WhenAnyValue subscription
            }
        }

       private async Task SelectModsFolderAsync()
        {
             var dialog = new CommonOpenFileDialog
             {
                 IsFolderPicker = true,
                 Title = "Select Mods Folder (e.g., MO2 Mods Path)",
                 InitialDirectory = GetSafeInitialDirectory(ModsFolder)
             };

             if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;

             ModsFolder = dialog.FileName;
             
             VM_SplashScreen? splashScreen = null;
             try
             {
                 // clear existing ModSettings to avoid cross-contamination
                 _model.ModSettings.Clear();
                 _model.CachedNonAppearanceMods.Clear();
                 //
                 
                 _lazyMainWindowVm.Value.IsLoadingFolders = true;
                 var footerMsg = "First time analyzing mods folder. Subsequent runs will be faster.";
                 splashScreen = VM_SplashScreen.InitializeAndShow(App.ProgramVersion, footerMessage: footerMsg);
                 
                // *** FIX: Removed the incorrect Task.Run wrapper. ***
                // The called methods are already async and handle their own threading internally.
                if (_lazyModListVM.IsValueCreated)
                {
                    await _lazyModListVM.Value.PopulateModSettingsAsync(splashScreen);
                }
                if (_lazyNpcSelectionBar.IsValueCreated)
                {
                    await _lazyNpcSelectionBar.Value.InitializeAsync(splashScreen);
                }
                 
                 if (_lazyModListVM.IsValueCreated)
                 {
                     _lazyModListVM.Value.ApplyFilters();
                 }
                 
                 RefreshNonAppearanceMods();
             }
             finally
             {
                 _lazyMainWindowVm.Value.IsLoadingFolders = false;
                 // Ensure the splash screen is closed and the main window is re-enabled
                 if (splashScreen != null)
                 {
                    await splashScreen.CloseSplashScreenAsync();
                 }
             }
        }

        private async Task SelectMugshotsFolderAsync()
        {
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select Mugshots Folder",
                InitialDirectory = GetSafeInitialDirectory(MugshotsFolder)
            };

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;
            
            MugshotsFolder = dialog.FileName;
            
            VM_SplashScreen? splashScreen = null;
            try
            {
                _lazyMainWindowVm.Value.IsLoadingFolders = true;
                splashScreen = VM_SplashScreen.InitializeAndShow(App.ProgramVersion, isModal: false);

                // *** FIX: Removed the incorrect Task.Run wrapper. ***
                if (_lazyNpcSelectionBar.IsValueCreated)
                {
                    await _lazyNpcSelectionBar.Value.InitializeAsync(splashScreen);
                }
                if (_lazyModListVM.IsValueCreated)
                {
                    await _lazyModListVM.Value.PopulateModSettingsAsync(splashScreen);
                }
                
                if (_lazyModListVM.IsValueCreated)
                {
                    _lazyModListVM.Value.ApplyFilters();
                }
            }
            finally
            {
                _lazyMainWindowVm.Value.IsLoadingFolders = false;
                // Ensure the splash screen is closed and the main window is re-enabled
                if (splashScreen != null)
                {
                    await splashScreen.CloseSplashScreenAsync();
                }
            }
        }

        private async Task SelectOutputDirectoryAsync() // Renamed for consistency
        {
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select Output Directory",
                InitialDirectory = GetSafeInitialDirectory(OutputDirectory, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
            };

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                OutputDirectory = dialog.FileName;
            }
        }

        // Helper to get a valid initial directory for folder pickers
        private string GetSafeInitialDirectory(string preferredPath, string fallbackPath = null)
        {
            if (!string.IsNullOrWhiteSpace(preferredPath) && Directory.Exists(preferredPath))
            {
                return preferredPath;
            }
            if (!string.IsNullOrWhiteSpace(fallbackPath) && Directory.Exists(fallbackPath))
            {
                return fallbackPath;
            }
            // Final fallback if neither preferred nor specific fallback exists
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }


        // --- Placeholder Command Handlers ---
        private void ShowPatchingModeHelp() // New
        {
            string helpText = @"Patching Mode Help:

Create:
NPC Plugins are imported directly from their selected Appearance Mods.
When the output plugin is generated, place it as high as it will go in your load order, and then use Synthesis FaceFixer (or manually perform conflict resolution) to forward the faces to your final load order.

Create And Patch:
NPC plugins are imported from their conflict-winning override in your load order, and their appearance is modified to match their selected Appearance Mod.
When the ouptut plugin is generated, put it at the end of your load order.";

            ScrollableMessageBox.Show(helpText, "Patching Mode Information");
        }
        
        private void ShowOverrideHandlingModeHelp()
        {
            string helpText = @"Override Handling Mode:

This setting determines how the default behavior for how the patcher handles mods that override/modify non-appearance records from the base game or other mods.

IMPORANT: Handling overrides makes patching take longer, and is only needed in very rare cases. You almost certainly should leave the default behavior as Ignore.

Options:

- Ignore:
  - The overriden records will be left as-is. If there are conflicts, it'll be up to you to patch them yourself.
  - The output plugin will be mastered to the mods providing the records being overridden.

- Include:
  - The overriden records will be copied into the output plugin.
  - In Create And Patch mode, only the changed aspects of the record will be applied.
  - The output plugin will still be mastered to the mods providing the records being overridden.

- Include As New:
  - The overridden records will be added to the output plugin as new records.
  - This mode may be appropriate if you have two Appearance Mods overriding the same record in different ways, which would make them incompatible with each other otherwise.";
            ScrollableMessageBox.Show(helpText, "Override Handling Mode Information");
        }

        


        
        
        

        private void RefreshNonAppearanceMods()
        {
            CachedNonAppearanceMods = new ObservableCollection<KeyValuePair<string, string>>(_model.CachedNonAppearanceMods.OrderBy(kvp => Path.GetFileName(kvp.Key) ?? kvp.Key, StringComparer.OrdinalIgnoreCase));
            ApplyNonAppearanceFilter();
        }

        /// <summary>
        /// Clears and repopulates the filtered list based on the current filter text.
        /// </summary>
        private void ApplyNonAppearanceFilter()
        {
            if (CachedNonAppearanceMods == null) return;

            FilteredNonAppearanceMods.Clear();

            var filter = NonAppearanceModFilterText;
            var sourceList = CachedNonAppearanceMods;

            IEnumerable<KeyValuePair<string, string>> itemsToDisplay;

            if (string.IsNullOrWhiteSpace(filter))
            {
                itemsToDisplay = sourceList;
            }
            else
            {
                // Filter is applied to the Key (the path)
                itemsToDisplay = sourceList.Where(kvp =>
                    (Path.GetFileName(kvp.Key) ?? kvp.Key).Contains(filter, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var item in itemsToDisplay)
            {
                FilteredNonAppearanceMods.Add(item);
            }
        }
        
        private async Task RescanCachedModAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                ScrollableMessageBox.ShowWarning($"The path '{path}' no longer exists and cannot be scanned.", "Path Not Found");
                return;
            }

            var modsVM = _lazyModListVM.Value;

            bool wasReimported = await modsVM.RescanSingleModFolderAsync(path);

            if (wasReimported)
            {
                _model.CachedNonAppearanceMods.Remove(path);

                var itemToRemove = CachedNonAppearanceMods.FirstOrDefault(kvp => kvp.Key == path);
                if (!itemToRemove.Equals(default(KeyValuePair<string, string>)))
                {
                    CachedNonAppearanceMods.Remove(itemToRemove);
                    // Filtered list will update automatically due to subscription
                }

                ScrollableMessageBox.Show($"Successfully re-imported '{Path.GetFileName(path)}' as an appearance mod. You can now find it in the Mods menu.", "Re-Scan Successful");
            }
            else
            {
                ScrollableMessageBox.Show($"The mod at '{Path.GetFileName(path)}' is still not recognized as an appearance mod.", "Re-Scan Complete");
            }
        }

        public string GetStatusReport()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Mods Folder: " + ModsFolder);
            sb.AppendLine("Mugshots Folder: " + MugshotsFolder);
            sb.AppendLine("Patching Mode: " + SelectedPatchingMode);
            sb.AppendLine("Output Directory: " + OutputDirectory);
            sb.AppendLine("Output Name: " + TargetPluginName);
            sb.AppendLine("Default Override Handling: " + SelectedRecordOverrideHandlingMode);
            sb.AppendLine("SkyPatcher Mode: " + UseSkyPatcherMode);
            sb.AppendLine("AutoEslIfy: " + AutoEslIfy);
            return sb.ToString();
        }
        
        public void Dispose()
        {
            _disposables.Dispose(); // Dispose all subscriptions
        }
    }
}