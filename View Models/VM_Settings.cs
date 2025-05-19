// VM_Settings.cs (Updated)

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Noggog; // For IsNullOrWhitespace
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_Settings : ReactiveObject, IDisposable, IActivatableViewModel
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _model; // Renamed from settings to _model for clarity
        private readonly Lazy<VM_NpcSelectionBar> _lazyNpcSelectionBar;
        private readonly Lazy<VM_Mods> _lazyModListVM;
        private readonly NpcConsistencyProvider _consistencyProvider; 
        
        public ViewModelActivator Activator { get; } = new ViewModelActivator(); 

        // --- Existing & Modified Properties ---
        [Reactive] public string ModsFolder { get; set; }
        [Reactive] public string MugshotsFolder { get; set; }
        [Reactive] public SkyrimRelease SkyrimRelease { get; set; }
        [Reactive] public string SkyrimGamePath { get; set; }
        // OutputModName now maps to the conceptual name in the model
        [Reactive] public string OutputModName { get; set; }

        // --- New Output Settings Properties ---
        [Reactive] public string OutputDirectory { get; set; }
        [Reactive] public bool AppendTimestampToOutputDirectory { get; set; }
        [Reactive] public string OutputPluginName { get; set; } // New property for the actual plugin filename
        [Reactive] public PatchingMode SelectedPatchingMode { get; set; }
        public IEnumerable<PatchingMode> PatchingModes { get; } = Enum.GetValues(typeof(PatchingMode)).Cast<PatchingMode>();

        // --- New EasyNPC Transfer Properties ---
        // Assuming ModKeyMultiPicker binds directly to an ObservableCollection-like source
        // We will wrap the HashSet from the model. Consider a more robust solution if direct binding causes issues.
        [Reactive] public VM_ModSelector ExclusionSelectorViewModel { get; private set; }
        [ObservableAsProperty] public IEnumerable<ModKey> AvailablePluginsForExclusion { get; }

        // --- Read-only properties reflecting environment state ---
        [ObservableAsProperty] public bool EnvironmentIsValid { get; }
        [ObservableAsProperty] public string EnvironmentErrorText { get; }
        
        // --- Properties for modifying EasyNPC Import/Export ---
        [Reactive] public bool AddMissingNpcsOnUpdate { get; set; } = true; // Default to true
        
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

        public VM_Settings(EnvironmentStateProvider environmentStateProvider, Settings settings, Lazy<VM_NpcSelectionBar> lazyNpcSelectionBar, Lazy<VM_Mods> lazyModListVm, NpcConsistencyProvider consistencyProvider)
        {
            _environmentStateProvider = environmentStateProvider;
            _lazyNpcSelectionBar = lazyNpcSelectionBar;
            _lazyModListVM = lazyModListVm;
            _model = settings; // settings is already loaded/newed by DI or caller
            _consistencyProvider = consistencyProvider;

            Application.Current.Exit += (_, __) => { SaveSettings(); };

            // --- Step 1: Initialize VM properties from the model ---
            // These assignments happen BEFORE any WhenAnyValue subscriptions for them are set up.
            ModsFolder = _model.ModsFolder;
            MugshotsFolder = _model.MugshotsFolder;
            SkyrimRelease = _model.SkyrimRelease;
            SkyrimGamePath = _model.SkyrimGamePath;
            OutputPluginName = _model.OutputPluginName; // This is the conceptual name / actual filename
            OutputModName = _model.OutputPluginName; // Keep OutputModName in sync if it represents the same thing
            OutputDirectory = _model.OutputDirectory;
            AppendTimestampToOutputDirectory = _model.AppendTimestampToOutputDirectory;
            SelectedPatchingMode = _model.PatchingMode;
            AddMissingNpcsOnUpdate = _model.AddMissingNpcsOnUpdate; // Assuming this is in your model

            // --- Step 2: Directly set initial values on _environmentStateProvider ---
            // This ensures the provider has the correct state BEFORE the first UpdateEnvironment() call.
            _environmentStateProvider.DataFolderPath = _model.SkyrimGamePath;
            _environmentStateProvider.SkyrimVersion = _model.SkyrimRelease;
            _environmentStateProvider.OutputPluginName = _model.OutputPluginName;

            // --- Step 3: Initialize ExclusionSelectorViewModel ---
            // It's initialized here. It will be reactively updated with AvailablePluginsForExclusion later.
            ExclusionSelectorViewModel = new VM_ModSelector();
            ExclusionSelectorViewModel.LoadFromModel(
                _environmentStateProvider.LoadOrder?.Keys ?? Enumerable.Empty<ModKey>(), // Use current (possibly empty) load order
                _model.EasyNpcDefaultPluginExclusions
            );

            // --- Step 4: Perform the SINGLE initial environment update ---
            // This is the crucial one-time call for startup, using the settings loaded above.
            Debug.WriteLine("VM_Settings Constructor: Performing initial UpdateEnvironmentAndNotify.");
            UpdateEnvironmentAndNotify();

            // --- Step 5: Set up subscriptions to update the model and/or trigger further updates ---
            // Use .Skip(1) to ignore the initial values that were just programmatically set.

            // Subscriptions that only update the model
            this.WhenAnyValue(x => x.ModsFolder).Skip(1).Subscribe(s => _model.ModsFolder = s).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.MugshotsFolder).Skip(1).Subscribe(s => _model.MugshotsFolder = s).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.OutputDirectory).Skip(1).Subscribe(s => _model.OutputDirectory = s).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.AppendTimestampToOutputDirectory).Skip(1).Subscribe(b => _model.AppendTimestampToOutputDirectory = b).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.SelectedPatchingMode).Skip(1).Subscribe(pm => _model.PatchingMode = pm).DisposeWith(_disposables);
            this.WhenAnyValue(x => x.AddMissingNpcsOnUpdate).Skip(1).Subscribe(b => _model.AddMissingNpcsOnUpdate = b).DisposeWith(_disposables);

            // Subscriptions for properties that trigger environment update (for *subsequent* changes)
            this.WhenAnyValue(x => x.SkyrimGamePath)
                .Skip(1) // Skip the initial value set during construction
                .Do(s => // For subsequent changes, update model and provider's direct property
                {
                    _model.SkyrimGamePath = s;
                    _environmentStateProvider.DataFolderPath = s;
                })
                .Subscribe(_ => UpdateEnvironmentAndNotify()) // Then call the full update
                .DisposeWith(_disposables);

            this.WhenAnyValue(x => x.SkyrimRelease)
                .Skip(1)
                .Do(r =>
                {
                    _model.SkyrimRelease = r;
                    _environmentStateProvider.SkyrimVersion = r;
                })
                .Subscribe(_ => UpdateEnvironmentAndNotify())
                .DisposeWith(_disposables);

            // OutputPluginName is the source of truth for the plugin's filename.
            // OutputModName is also observed if it means the same thing or has other UI implications.
            this.WhenAnyValue(x => x.OutputPluginName)
                .Where(x => !x.IsNullOrWhitespace())
                .Skip(1)
                .Do(s =>
                {
                    _model.OutputPluginName = s;
                    _environmentStateProvider.OutputPluginName = s; // Critical: update provider
                    if (OutputModName != s) OutputModName = s; // Keep OutputModName in sync if it's meant to be the same
                })
                .Subscribe(_ => UpdateEnvironmentAndNotify())
                .DisposeWith(_disposables);

            // If OutputModName can be independently changed and should also drive OutputPluginName:
            this.WhenAnyValue(x => x.OutputModName)
                .Where(x => !x.IsNullOrWhitespace())
                .Skip(1) // If OutputModName is initialized from _model.OutputPluginName, skip initial
                .Subscribe(s => {
                    if (OutputPluginName != s) OutputPluginName = s; // This will trigger the OutputPluginName subscription
                })
                .DisposeWith(_disposables);


            // --- Step 6: Expose environment state reactively (OAPHs) ---
            // These will reflect the state changes from the initial UpdateEnvironmentAndNotify call.
            _environmentStateProvider.WhenAnyValue(x => x.EnvironmentIsValid)
                .ToPropertyEx(this, x => x.EnvironmentIsValid)
                .DisposeWith(_disposables);

            _environmentStateProvider.WhenAnyValue(x => x.EnvironmentBuilderError)
                 .Select(err => string.IsNullOrWhiteSpace(err) ? string.Empty : $"Environment Error: {err}")
                .ToPropertyEx(this, x => x.EnvironmentErrorText)
                .DisposeWith(_disposables);

            // Populate AvailablePluginsForExclusion (reacts to EnvironmentIsValid)
            this.WhenAnyValue(x => x.EnvironmentIsValid) // Triggered by the initial UpdateEnvironmentAndNotify
                .Select(_ => _environmentStateProvider.EnvironmentIsValid && _environmentStateProvider.LoadOrder != null
                    ? _environmentStateProvider.LoadOrder.Keys.OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase).ToList()
                    : Enumerable.Empty<ModKey>())
                .ObserveOn(RxApp.MainThreadScheduler)
                .ToPropertyEx(this, x => x.AvailablePluginsForExclusion)
                .DisposeWith(_disposables);

            // Update ModSelectorViewModel when available plugins change
            this.WhenAnyValue(x => x.AvailablePluginsForExclusion)
                .Where(list => list != null) // An empty list is valid for LoadFromModel
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(availablePlugins =>
                {
                    var currentUISelctions = ExclusionSelectorViewModel.SaveToModel();
                    ExclusionSelectorViewModel.LoadFromModel(availablePlugins, currentUISelctions);
                }).DisposeWith(_disposables);

            // --- Step 7: Commands ---
            SelectGameFolderCommand = ReactiveCommand.Create(SelectGameFolder);
            SelectModsFolderCommand = ReactiveCommand.Create(SelectModsFolder);
            SelectMugshotsFolderCommand = ReactiveCommand.Create(SelectMugshotsFolder);
            SelectOutputDirectoryCommand = ReactiveCommand.Create(SelectOutputDirectory);
            ShowPatchingModeHelpCommand = ReactiveCommand.Create(ShowPatchingModeHelp);
            ImportEasyNpcCommand = ReactiveCommand.Create(ImportEasyNpc);
            ExportEasyNpcCommand = ReactiveCommand.Create(ExportEasyNpc);
            UpdateEasyNpcProfileCommand = ReactiveCommand.CreateFromTask<bool>(UpdateEasyNpcProfile);

            // Throttled save
            _saveRequestSubject
                .Throttle(_saveThrottleTime, RxApp.TaskpoolScheduler)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => SaveSettings())
                .DisposeWith(_disposables);

            // --- Step 8: WhenActivated for UI-dependent subscriptions or interactions ---
            this.WhenActivated(disposables =>
            {
                Debug.WriteLine("VM_Settings Activated. Setting up NpcSelectionBar subscription.");
                // Environment should be initialized by now.
                // This is a good place for subscriptions that depend on other VMs being ready
                // or for UI events that only make sense when the view is active.

                if (_lazyNpcSelectionBar.IsValueCreated) // Initialize() was called in UpdateEnvironmentAndNotify
                {
                    _lazyNpcSelectionBar.Value
                        .WhenAnyValue(x => x.SelectedNpc)
                        .Skip(1) // Skip initial null/default
                        .Subscribe(npc =>
                        {
                            _model.LastSelectedNpcFormKey = npc?.NpcFormKey ?? FormKey.Null;
                            RequestThrottledSave();
                        })
                        .DisposeWith(disposables);
                }
                else
                {
                    // This could happen if UpdateEnvironmentAndNotify didn't run or failed to initialize it.
                    Debug.WriteLine("WARNING: VM_Settings activated, but NpcSelectionBar is not yet initialized. Subscription might be missed or delayed.");
                }
            });
        }

        public static Settings LoadSettings()
        {
            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
            Settings loadedSettings = null;
            if (File.Exists(settingsPath))
            {
                 loadedSettings = JSONhandler<Settings>.LoadJSONFile(settingsPath, out bool success, out string exception);
                 if (!success)
                 {
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


            return loadedSettings;
        }
        
        public void RequestThrottledSave()
        {
            _saveRequestSubject.OnNext(Unit.Default);
        }

        private void SaveSettings()
        {
            _model.EasyNpcDefaultPluginExclusions = new HashSet<ModKey>(ExclusionSelectorViewModel.SaveToModel());

            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
            JSONhandler<Settings>.SaveJSONFile(_model, settingsPath, out bool success, out string exception);
             if (!success)
             {
                  // Maybe show a non-modal warning or log? Saving happens on exit.
                  System.Diagnostics.Debug.WriteLine($"ERROR saving settings: {exception}");
             }
        }

         private void UpdateEnvironmentAndNotify()
         {
            // Crucially, ensure the provider uses the *correct* name for the output file
            _environmentStateProvider.OutputPluginName = _model.OutputPluginName;
            _environmentStateProvider.UpdateEnvironment();

            this.RaisePropertyChanged(nameof(EnvironmentIsValid));
            this.RaisePropertyChanged(nameof(EnvironmentErrorText));

            _lazyNpcSelectionBar.Value.Initialize();
            _lazyModListVM.Value.PopulateModSettings();
            _lazyModListVM.Value.ApplyFilters(); // Re-apply filters after mods repopulate
         }

        // --- Folder Selection Methods ---
        private void SelectGameFolder()
        {
            // Use WindowsAPICodePack for a modern dialog
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select Skyrim Game Folder",
                InitialDirectory = GetSafeInitialDirectory(SkyrimGamePath)
            };

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                SkyrimGamePath = dialog.FileName;
            }
        }

        private void SelectModsFolder()
        {
             var dialog = new CommonOpenFileDialog
             {
                 IsFolderPicker = true,
                 Title = "Select Mods Folder (e.g., MO2 Mods Path)",
                 InitialDirectory = GetSafeInitialDirectory(ModsFolder)
             };

             if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
             {
                 ModsFolder = dialog.FileName;
                 _lazyModListVM.Value.PopulateModSettings(); // Refresh mods list view
                 _lazyModListVM.Value.ApplyFilters();
             }
        }

        private void SelectMugshotsFolder()
        {
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select Mugshots Folder",
                InitialDirectory = GetSafeInitialDirectory(MugshotsFolder)
            };

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                MugshotsFolder = dialog.FileName;
                _lazyNpcSelectionBar.Value.Initialize();
                _lazyModListVM.Value.PopulateModSettings();
                _lazyModListVM.Value.ApplyFilters();
            }
        }

        private void SelectOutputDirectory() // New
        {
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select Output Directory",
                 // Start in current output directory, or My Documents if not set/invalid
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

Default:
NPC Plugins are imported directly from their selected Appearance Mods.
When the output plugin is generated, place it as high as it will go in your load order, and then use Synthesis FaceFixer (or manually perform conflict resolution) to forward the faces to your final load order.

EasyNPC-Like:
NPC plugins are imported from their conflict-winning override in your load order, and their appearance is modified to match their selected Appearance Mod.
When the ouptut plugin is generated, put it at the end of your load order.";

            ScrollableMessageBox.Show(helpText, "Patching Mode Information");
        }

        private void ImportEasyNpc()
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "EasyNPC Profile (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Select EasyNPC Profile Text File"
            };

            if (openFileDialog.ShowDialog() != true) return; // User cancelled

            string filePath = openFileDialog.FileName;
            // Store *successfully matched* potential changes
            var potentialChanges = new List<(FormKey NpcKey, ModKey DefaultKey, ModKey AppearanceKey, string NpcName, string TargetModDisplayName)>();
            var errors = new List<string>();
            var missingAppearancePlugins = new HashSet<ModKey>(); // Track plugins without matching VM_ModSetting
            int lineNum = 0;

            // --- Pass 1: Parse file and identify missing plugins ---
            try
            {
                foreach (string line in File.ReadLines(filePath))
                {
                    lineNum++;
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//")) continue;

                    var equalSplit = line.Split(new[] { '=' }, 2);
                    if (equalSplit.Length != 2) { errors.Add($"Line {lineNum}: Invalid format (missing '=')."); continue; }
                    string formStringPart = equalSplit[0].Trim();
                    string pluginInfoPart = equalSplit[1].Trim();

                    FormKey npcKey = default;
                    ModKey defaultKey = default;
                    ModKey appearanceKey = default;

                    // Parse FormKey
                    var formSplit = formStringPart.Split('#');
                    if (formSplit.Length == 2 && !string.IsNullOrWhiteSpace(formSplit[0]) && !string.IsNullOrWhiteSpace(formSplit[1])) {
                        try { npcKey = FormKey.Factory($"{formSplit[1]}:{formSplit[0]}"); }
                        catch (Exception ex) { errors.Add($"Line {lineNum}: Cannot parse FormKey '{formStringPart}'. {ex.Message}"); continue; }
                    } else { errors.Add($"Line {lineNum}: Invalid FormString '{formStringPart}'."); continue; }

                    // Parse Plugins
                    var pipeSplit = pluginInfoPart.Split('|');
                    if (pipeSplit.Length < 2 || string.IsNullOrWhiteSpace(pipeSplit[0]) || string.IsNullOrWhiteSpace(pipeSplit[1])) { errors.Add($"Line {lineNum}: Invalid Plugin Info '{pluginInfoPart}'."); continue; }
                    string defaultPluginName = pipeSplit[0].Trim();
                    string appearancePluginName = pipeSplit[1].Trim();

                    try { defaultKey = ModKey.FromFileName(defaultPluginName); }
                    catch (Exception ex) { errors.Add($"Line {lineNum}: Cannot parse Default Plugin '{defaultPluginName}'. {ex.Message}"); continue; }

                    try { appearanceKey = ModKey.FromFileName(appearancePluginName); }
                    catch (Exception ex) { errors.Add($"Line {lineNum}: Cannot parse Appearance Plugin '{appearancePluginName}'. {ex.Message}"); continue; }

                    // Check if a VM_ModSetting exists for the appearance plugin
                    // TryGetModSettingForPlugin now finds a VM where *any* key matches.
                    // We still need the DisplayName for the consistency provider.
                    if (!_lazyModListVM.Value.TryGetModSettingForPlugin(appearanceKey, out var foundModSettingVm, out string targetModDisplayName))
                    {
                        // Not found - track it and skip adding to potentialChanges for now
                        missingAppearancePlugins.Add(appearanceKey);
                        Debug.WriteLine($"ImportEasyNpc: Appearance Plugin '{appearanceKey}' not found in Mods Menu for NPC {npcKey}.");
                        // We don't add this to potentialChanges yet
                    }
                    else
                    {
                        // Found - add to potential changes
                        string npcName = _lazyNpcSelectionBar.Value.AllNpcs.FirstOrDefault(n => n.NpcFormKey == npcKey)?.DisplayName ?? npcKey.ToString();
                        potentialChanges.Add((npcKey, defaultKey, appearanceKey, npcName, targetModDisplayName));
                    }
                } // End foreach line
            }
            catch (Exception ex)
            {
                ScrollableMessageBox.ShowError($"Error reading file '{filePath}':\n{ex.Message}", "File Read Error");
                return;
            }

            // --- Handle Parsing Errors (Optional: Show before Missing Plugin check) ---
            if (errors.Any())
            {
                var errorMsg = new StringBuilder($"Encountered {errors.Count} errors while parsing '{Path.GetFileName(filePath)}':\n\n");
                errorMsg.AppendLine(string.Join("\n", errors.Take(20)));
                if (errors.Count > 20) errorMsg.AppendLine("\n...");
                errorMsg.AppendLine("\nThese lines were skipped. Continue processing?");
                if (!ScrollableMessageBox.Confirm(errorMsg.ToString(), "Parsing Errors"))
                {
                    return; // Cancel based on parsing errors
                }
            }

            // --- Handle Missing Appearance Plugins ---
            if (missingAppearancePlugins.Any())
            {
                var missingMsg = new StringBuilder("The following Appearance Plugins are assigned in your EasyNPC profile, ");
                missingMsg.AppendLine("but there are no Mods in your Mods Menu that list them as Corresponding Plugins:");
                missingMsg.AppendLine();
                foreach (var missingKey in missingAppearancePlugins.Take(15)) // Show max 15
                {
                    missingMsg.AppendLine($"- {missingKey.FileName}");
                }
                if (missingAppearancePlugins.Count > 15) missingMsg.AppendLine("  ...");
                missingMsg.AppendLine("\nHow would you like to proceed?");

                // Simulate custom buttons with Yes/No mapping
                missingMsg.AppendLine("\n[Yes] = Continue and Skip NPCs assigned these plugins");
                missingMsg.AppendLine("[No]  = Cancel Import");

                if (!ScrollableMessageBox.Confirm(missingMsg.ToString(), "Missing Appearance Plugin Mappings", MessageBoxImage.Warning)) // User chose Cancel
                {
                    MessageBox.Show("Import cancelled.", "Import Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                // If Yes, we simply proceed with the already filtered 'potentialChanges' list.
                Debug.WriteLine("User chose to continue, skipping NPCs with missing appearance plugins.");
            }


            // Check if there are any changes left to process
            if (!potentialChanges.Any())
            {
                ScrollableMessageBox.Show("No valid changes found to apply after processing the file (possibly due to skipping or parsing errors).", "Import Empty");
                return;
            }


            // --- Prepare Confirmation (using the filtered potentialChanges) ---
            var changesToConfirm = new List<string>(); // List of strings for the message box
            // No need for finalApplyList, potentialChanges already has targetModDisplayName

            foreach (var change in potentialChanges)
            {
                // Get current selection display name
                string? currentSelectionDisplayName = _consistencyProvider.GetSelectedMod(change.NpcKey);

                // Add to confirmation list ONLY if overwriting an EXISTING selection
                if (!string.IsNullOrEmpty(currentSelectionDisplayName) &&
                    currentSelectionDisplayName != change.TargetModDisplayName)
                {
                    const int maxLen = 40;
                    string oldDisplay = currentSelectionDisplayName;
                    if (oldDisplay.Length > maxLen) oldDisplay = oldDisplay.Substring(0, maxLen - 3) + "...";
                    string newDisplay = change.TargetModDisplayName;
                    if (newDisplay.Length > maxLen) newDisplay = newDisplay.Substring(0, maxLen - 3) + "...";

                    changesToConfirm.Add($"{change.NpcName}: [{oldDisplay}] -> [{newDisplay}]");
                }
                // We will apply all items in potentialChanges list later
            }

            // --- Show Confirmation Dialog ---
            string confirmationMessage;
            int totalToProcess = potentialChanges.Count; // Based on successfully matched items

            if (changesToConfirm.Any()) // Specific *overwrites* will occur
            {
                confirmationMessage = $"The following {changesToConfirm.Count} existing NPC appearance assignments will be changed:\n\n" +
                                      string.Join("\n", changesToConfirm.Take(30));
                if (changesToConfirm.Count > 30) confirmationMessage += "\n...";
            }
            else // No overwrites, but settings ARE being applied
            {
                confirmationMessage = "No existing NPC appearance assignments will be changed.";
            }

            confirmationMessage += $"\n\nEasyNPC Default Plugin settings will also be updated for {totalToProcess} processed NPCs.";
            // No longer need message about adding Mods list entries
            confirmationMessage += "\n\nDo you want to apply these changes?";

            if (ScrollableMessageBox.Confirm(confirmationMessage, "Confirm Import"))
            {
                // --- Apply Changes ---
                int appliedCount = 0;
                foreach (var applyItem in potentialChanges) // Iterate the filtered list
                {
                    // Update EasyNPC Default Plugin in the model
                    _model.EasyNpcDefaultPlugins[applyItem.NpcKey] = applyItem.DefaultKey;

                    // Update Selected Appearance Mod using the consistency provider
                    _consistencyProvider.SetSelectedMod(applyItem.NpcKey, applyItem.TargetModDisplayName);
                    appliedCount++;
                }

                // No longer need to check modSettingAdded or call ResortAndRefreshFilters

                // Refresh NPC list filter in case selection state changed
                _lazyNpcSelectionBar.Value.ApplyFilter(false);

                ScrollableMessageBox.Show($"Successfully imported settings for {appliedCount} NPCs.", "Import Complete");
            }
            else
            {
                 ScrollableMessageBox.Show("Import cancelled.", "Import Cancelled");
            }
        }


        /// <summary>
        /// Exports the current NPC appearance assignments and default plugin information
        /// to a text file compatible with EasyNPC's profile format.
        /// </summary>
        private void ExportEasyNpc()
        {
             // --- Step 1: Compile NPCs that need to be exported ---

             // Get the FormKeys of all NPCs that currently have an appearance mod selected.
             // This is the primary list of NPCs we need to process for export.
             var assignedAppearanceNpcFormKeys = _model.SelectedAppearanceMods.Keys.ToList();

             // Check if there are any assignments to export.
             if (!assignedAppearanceNpcFormKeys.Any())
             {
                 ScrollableMessageBox.Show("No NPC appearance assignments have been made yet. Nothing to export.", "Export Empty");
                 return;
             }

             // --- Step 2: Prepare Helper Data and Output Storage ---

             // Retrieve the set of ModKeys that the user has configured to exclude
             // when determining the "default" plugin (usually the conflict winner).
             // Store this locally for efficient lookup within the loop.
             var excludedDefaultPlugins = new HashSet<ModKey>(ExclusionSelectorViewModel.SaveToModel()); // Ensure it's a HashSet for O(1) lookups

             // List to hold the formatted strings for each successfully processed NPC.
             List<string> outputStrs = new();
             // Lists to collect errors encountered during processing.
             List<string> formKeyErrors = new List<string>();
             List<string> appearanceModErrors = new List<string>();
             List<string> defaultPluginErrors = new List<string>();


             // --- Step 3: Process each assigned NPC ---

             // Iterate through each NPC FormKey that has an appearance assignment.
             foreach (var npcFormKey in assignedAppearanceNpcFormKeys)
             {
                 // --- 3a: Convert FormKey to EasyNPC Form String format ---
                 string formString = string.Empty;
                 try
                 {
                     // The EasyNPC format is "PluginFileName.esm#IDHexPart".
                     // FormKey.ToString() gives "IDHexPart:PluginFileName.esm". We need to reverse this.
                     // ModKey.ToString() usually gives "PluginFileName.esm".
                     // IDString() gives the FormID hex part (e.g., "001F3F").
                     formString = $"{npcFormKey.ModKey.FileName}#{npcFormKey.IDString()}"; // Use FileName for consistency
                 }
                 catch (Exception e)
                 {
                     // If conversion fails (e.g., null ModKey or IDString issues, though unlikely for valid FormKey),
                     // record the error and skip this NPC.
                     formKeyErrors.Add($"Failed to convert FormKey '{npcFormKey}' to string format: {e.Message}");
                     continue; // Skip to the next NPC FormKey
                 }

                 // --- 3b: Get the assigned Appearance Plugin ModKey ---
                 ModKey? appearancePlugin = null; // Use nullable ModKey
                 // Retrieve the display name of the selected appearance mod for this NPC.
                 // We assume if the key exists in SelectedAppearanceMods, the value (name) is valid.
                 var appearanceModName = _model.SelectedAppearanceMods[npcFormKey];

                 // Find the VM_ModSetting in the Mods View list that corresponds to this display name.
                 var appearanceMod = _lazyModListVM.Value.AllModSettings.FirstOrDefault(mod => mod.DisplayName == appearanceModName);
                 if (appearanceMod == null)
                 {
                     // If no VM_ModSetting is found (e.g., inconsistency after import/manual changes), record error.
                     appearanceModErrors.Add($"NPC {formString}: Could not find Mod Setting entry for assigned appearance '{appearanceModName}'.");
                     continue; // Skip this NPC
                 }

                 // Get the CorrespondingModKey from the found VM_ModSetting. This is the plugin we need for the output.
                 // *** Use the NpcSourcePluginMap to find the specific key for this NPC within this ModSetting ***
                 if (appearanceMod.NpcPluginDisambiguation.TryGetValue(npcFormKey, out var specificKey))
                 {
                     appearancePlugin = specificKey;
                 }
                 else if (appearanceMod.AmbiguousNpcFormKeys.Contains(npcFormKey))
                 {
                     // NPC is ambiguous within this setting
                     appearanceModErrors.Add($"NPC {formString}: Source plugin is ambiguous within Mod Setting '{appearanceModName}'. Cannot export.");
                     continue; // Skip this NPC
                 }
                 else
                 {
                     // NPC not found in this setting's map (or setting has no plugins)
                     appearanceModErrors.Add($"NPC {formString}: Mod Setting '{appearanceModName}' does not list a unique source plugin for this NPC.");
                     continue; // Skip this NPC
                 }

                 // No need to check IsNull here, as TryGetValue succeeded with a valid key from the map.

                 // --- 3c: Determine the Default Plugin ModKey ---
                 ModKey defaultPlugin = default; // Use default ModKey struct (represents null/invalid state)
                 // First, check if a default plugin has been explicitly set for this NPC (e.g., via import).
                 if (_model.EasyNpcDefaultPlugins.TryGetValue(npcFormKey, out var presetDefaultPlugin))
                 {
                     defaultPlugin = presetDefaultPlugin;
                 }
                 else // If no preset default, determine it from the load order context.
                 {
                     // Resolve all plugins that provide a record for this NPC, ordered by load order priority (winners first).
                     // This requires the LinkCache from the EnvironmentStateProvider.
                      if (_environmentStateProvider.LinkCache == null)
                      {
                           defaultPluginErrors.Add($"NPC {formString}: Cannot determine default plugin because Link Cache is not available.");
                           continue; // Cannot proceed without LinkCache
                      }
                     var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcFormKey);
                     if (!contexts.Any())
                     {
                         // Should be unlikely if the NPC exists, but handle defensively.
                         defaultPluginErrors.Add($"NPC {formString}: Cannot determine default plugin because no context found in Link Cache.");
                         continue; // Skip this NPC
                     }

                     // Iterate through the overriding plugins (highest priority first).
                     foreach (var context in contexts)
                     {
                         // Check if the plugin providing this override is in the exclusion list.
                         if (!excludedDefaultPlugins.Contains(context.ModKey))
                         {
                             // This is the first non-excluded plugin, consider it the default.
                             defaultPlugin = context.ModKey;
                             break; // Stop searching once the default is found.
                         }
                     }
                     // If the loop completes without finding a non-excluded plugin, defaultPlugin remains default(ModKey).
                 }

                 // Validate the determined default plugin.
                 if (defaultPlugin.IsNull) // Use IsNull check for default(ModKey)
                 {
                     // This could happen if all overrides were excluded or if resolution failed.
                     defaultPluginErrors.Add($"NPC {formString}: Could not determine a non-excluded Default Plugin.");
                     continue; // Skip this NPC
                 }

                 // --- 3d: Assemble the output string ---
                 // Format: PluginName#IDHex=DefaultPluginFileName|AppearancePluginFileName|
                 // Use FileName property for cleaner output, matching EasyNPC expectation.
                 outputStrs.Add($"{formString}={defaultPlugin.FileName}|{appearancePlugin.Value.FileName}|");
             } // End foreach npcFormKey


             // --- Step 4: Report Errors and Confirm Save ---

             // Consolidate all errors found during processing.
             var allErrors = formKeyErrors.Concat(appearanceModErrors).Concat(defaultPluginErrors).ToList();

             // If any errors occurred, display them and ask the user whether to proceed with saving the valid entries.
             if (allErrors.Any())
             {
                 var errorMsg = new StringBuilder($"Encountered {allErrors.Count} errors during export processing:\n\n");
                 errorMsg.AppendLine(string.Join("\n", allErrors.Take(20))); // Show first 20 errors
                 if (allErrors.Count > 20) errorMsg.AppendLine("\n...");
                 errorMsg.AppendLine("\nDo you want to save the successfully processed entries?");

                 if (!ScrollableMessageBox.Confirm(errorMsg.ToString(), "Export Errors"))
                 {
                     MessageBox.Show("Export cancelled due to errors.", "Export Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
                     return; // Cancel the export.
                 }
                 // If Yes, proceed to save the outputStrs list which contains only successful entries.
             }

            // Check if there's anything to save after potential errors/skips
            if (!outputStrs.Any())
            {
                 ScrollableMessageBox.Show("No valid NPC assignments could be exported.", "Export Empty");
                 return;
            }

             // --- Step 5: Get Output File Path ---

             // Prompt user to select an output file path using a Save File Dialog.
             var saveFileDialog = new SaveFileDialog
             {
                 Filter = "EasyNPC Profile (*.txt)|*.txt|All files (*.*)|*.*",
                 Title = "Save EasyNPC Profile As...",
                 FileName = "EasyNPC_Profile_Export.txt" // Suggest a default filename
             };

             if (saveFileDialog.ShowDialog() != true)
             {
                 ScrollableMessageBox.Show("Export cancelled by user.", "Export Cancelled");
                 return; // User cancelled the save dialog.
             }
             string outputFilePath = saveFileDialog.FileName;


             // --- Step 6: Write Output File ---

             try
             {
                 // Save outputStrs (separated by Environment.NewLine()) to the selected output file.
                 // Use UTF-8 encoding without BOM, which is common for config files.
                 File.WriteAllLines(outputFilePath, outputStrs, new UTF8Encoding(false));

                 ScrollableMessageBox.Show($"Successfully exported assignments for {outputStrs.Count} NPCs to:\n{outputFilePath}", "Export Complete");
             }
             catch (Exception ex)
             {
                 // Handle potential file writing errors (permissions, disk full, etc.).
                 ScrollableMessageBox.ShowError($"Failed to save the export file:\n{ex.Message}", "File Save Error");
             }
        }
        
        /// <summary>
        /// Updates an existing EasyNPC profile file with the current application's
        /// NPC appearance selections. Can optionally add NPCs missing from the file.
        /// </summary>
        /// <param name="addMissingNPCs">If true, NPCs selected in the application but not found in the profile file will be added.</param>
        private async Task UpdateEasyNpcProfile(bool addMissingNPCs)
        {
            // --- Step 1: Get File to Update ---
            var openFileDialog = new OpenFileDialog
            {
                Filter = "EasyNPC Profile (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Select EasyNPC Profile File to Update",
                CheckFileExists = true // Ensure the file exists
            };

            if (openFileDialog.ShowDialog() != true)
            {
                Debug.WriteLine("UpdateEasyNpcProfile: File selection cancelled.");
                return; // User cancelled
            }
            string filePath = openFileDialog.FileName;

            // --- Step 2: Read Existing Profile and Prepare Data ---
            List<string> originalLines;
            var lineLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // Maps FormString -> Line Index
            var errors = new List<string>();
            int lineNum = 0;

            try
            {
                originalLines = File.ReadAllLines(filePath).ToList(); // Read all lines into memory

                // Build the lookup dictionary from valid lines
                foreach (string line in originalLines)
                {
                    lineNum++;
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//")) continue; // Skip comments/empty

                    var equalSplit = line.Split(new[] { '=' }, 2);
                    if (equalSplit.Length != 2) continue; // Skip invalid format lines during lookup build
                    string formStringPart = equalSplit[0].Trim();

                    // Basic validation of FormString format (Plugin#ID)
                    var formSplit = formStringPart.Split('#');
                    if (formSplit.Length == 2 && !string.IsNullOrWhiteSpace(formSplit[0]) && !string.IsNullOrWhiteSpace(formSplit[1]))
                    {
                        if (!lineLookup.TryAdd(formStringPart, lineNum - 1)) // Store 0-based index
                        {
                             errors.Add($"Duplicate FormString '{formStringPart}' found at line {lineNum}. Only the first occurrence will be updated.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ScrollableMessageBox.ShowError($"Error reading existing profile file '{filePath}':\n{ex.Message}", "File Read Error");
                return;
            }

             // --- Step 3: Get App Selections and Prepare Updates ---
             var appNpcSelections = _model.SelectedAppearanceMods.ToList(); // Get current selections as pairs
             if (!appNpcSelections.Any())
             {
                 ScrollableMessageBox.Show("No NPC appearance assignments are currently selected in the application. Nothing to update.", "Update Empty");
                 return;
             }

            var updatedLines = new List<string>(originalLines); // Create a mutable copy
            var processedFormStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Track which FormStrings from the file were updated/found
            var addedLines = new List<string>(); // Track lines added for missing NPCs
            var skippedMissingNpcs = new List<string>(); // Track NPCs skipped because addMissingNPCs was false
            var lookupErrors = new List<string>(); // Track errors finding plugins/defaults

            // Retrieve excluded plugins once
            var excludedDefaultPlugins = new HashSet<ModKey>(ExclusionSelectorViewModel.SaveToModel());


            // --- Step 4: Iterate Through App Selections and Update/Prepare Additions ---
            foreach (var kvp in appNpcSelections)
            {
                var npcFormKey = kvp.Key;
                var selectedAppearanceModName = kvp.Value; // This is the VM_ModSetting.DisplayName

                // --- 4a: Convert FormKey to EasyNPC Form String ---
                string formString;
                try
                {
                    formString = $"{npcFormKey.ModKey.FileName}#{npcFormKey.IDString()}";
                }
                catch (Exception e)
                {
                    lookupErrors.Add($"Skipping NPC {npcFormKey}: Cannot format FormKey - {e.Message}");
                    continue;
                }

                // --- 4b: Get Appearance Plugin ModKey from selected VM_ModSetting name ---
                var appearanceModSetting = _lazyModListVM.Value.AllModSettings.FirstOrDefault(m => m.DisplayName == selectedAppearanceModName);
                if (appearanceModSetting == null)
                {
                    lookupErrors.Add($"Skipping NPC {formString}: Cannot find Mod Setting entry named '{selectedAppearanceModName}'.");
                    continue;
                }
                // *** Use the NpcSourcePluginMap to find the specific key for this NPC within this ModSetting ***
                ModKey appearancePluginKey;
                if (appearanceModSetting.NpcPluginDisambiguation.TryGetValue(npcFormKey, out var specificKey))
                {
                    appearancePluginKey = specificKey;
                }
                else if (appearanceModSetting.AmbiguousNpcFormKeys.Contains(npcFormKey))
                {
                    lookupErrors.Add($"Skipping NPC {formString}: Source plugin is ambiguous within Mod Setting '{selectedAppearanceModName}'.");
                    continue;
                }
                else
                {
                    lookupErrors.Add($"Skipping NPC {formString}: Mod Setting '{selectedAppearanceModName}' does not list a unique source plugin for this NPC.");
                    continue;
                }

                // --- 4c: Determine Default Plugin ---
                ModKey defaultPluginKey = default;
                if (!_model.EasyNpcDefaultPlugins.TryGetValue(npcFormKey, out defaultPluginKey))
                {
                     // Not explicitly set, find winning context
                     if (_environmentStateProvider.LinkCache != null)
                     {
                         var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcFormKey); // Highest first
                         defaultPluginKey = contexts.FirstOrDefault(ctx => !excludedDefaultPlugins.Contains(ctx.ModKey)).ModKey; // Get first non-excluded
                     }
                }
                 // Validate default key after attempting to find it
                if (defaultPluginKey.IsNull)
                {
                    lookupErrors.Add($"Skipping NPC {formString}: Could not determine a valid Default Plugin.");
                    continue;
                }

                // --- 4d: Construct the new line's content ---
                string newLineContent = $"{defaultPluginKey.FileName}|{appearancePluginKey.FileName}|";
                string fullNewLine = $"{formString}={newLineContent}";

                // --- 4e: Find existing line or handle missing ---
                if (lineLookup.TryGetValue(formString, out int lineIndex))
                {
                     // NPC exists in the file, update the line in our copy
                     if (updatedLines[lineIndex] != fullNewLine) // Only mark as processed if changed
                     {
                          updatedLines[lineIndex] = fullNewLine;
                          processedFormStrings.Add(formString); // Mark as updated/found
                     }
                     else {
                          processedFormStrings.Add(formString); // Mark as found even if not changed
                     }
                }
                else
                {
                    // NPC not found in the original file
                    if (addMissingNPCs)
                    {
                        addedLines.Add(fullNewLine); // Add to a separate list for appending later
                        processedFormStrings.Add(formString); // Mark as processed (added)
                    }
                    else
                    {
                        skippedMissingNpcs.Add(formString); // Track skipped NPC
                    }
                }
            } // End foreach app selection

            // --- Step 5: Report Errors and Skipped NPCs ---
            errors.AddRange(lookupErrors); // Combine parsing and lookup errors
            if (errors.Any() || skippedMissingNpcs.Any())
            {
                var reportMsg = new StringBuilder();
                if (errors.Any())
                {
                    reportMsg.AppendLine($"Encountered {errors.Count} errors during processing (these NPCs were skipped):");
                    reportMsg.AppendLine(string.Join("\n", errors.Take(10).Select(e => $"- {e}")));
                    if (errors.Count > 10) reportMsg.AppendLine("  ...");
                    reportMsg.AppendLine();
                }
                if (skippedMissingNpcs.Any())
                {
                     reportMsg.AppendLine($"Skipped {skippedMissingNpcs.Count} NPCs selected in the app because they were not found in the profile file (Add Missing NPCs was disabled):");
                     reportMsg.AppendLine(string.Join("\n", skippedMissingNpcs.Take(10).Select(s => $"- {s}")));
                     if (skippedMissingNpcs.Count > 10) reportMsg.AppendLine("  ...");
                     reportMsg.AppendLine();
                }
                reportMsg.AppendLine("Do you want to save the updates for the successfully processed NPCs?");

                if (!ScrollableMessageBox.Confirm(reportMsg.ToString(), "Update Issues"))
                {
                    ScrollableMessageBox.Show("Update cancelled.", "Update Cancelled");
                     return;
                }
            }

            // Add the newly generated lines (if any) to the end of the updated list
            if (addedLines.Any())
            {
                updatedLines.AddRange(addedLines);
            }

            int updatedCount = processedFormStrings.Count - addedLines.Count; // Number of existing lines modified
            int addedCount = addedLines.Count;

            if (updatedCount == 0 && addedCount == 0)
            {
                 ScrollableMessageBox.Show("No changes were made to the profile file (assignments might already match).", "No Changes");
                 return;
            }

            // --- Step 6: Confirm Save Location ---
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "EasyNPC Profile (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Save Updated EasyNPC Profile As...",
                FileName = Path.GetFileName(filePath), // Suggest original name
                InitialDirectory = Path.GetDirectoryName(filePath) // Start in original directory
            };

            if (saveFileDialog.ShowDialog() != true)
            {
                ScrollableMessageBox.Show("Update cancelled by user.", "Update Cancelled");
                return;
            }
            string outputFilePath = saveFileDialog.FileName;

            // --- Step 7: Write Updated File ---
            try
            {
                 // Write the potentially modified list (preserving comments/order, appending new)
                 File.WriteAllLines(outputFilePath, updatedLines, new UTF8Encoding(false)); // Use UTF-8 without BOM

                 string successMessage = $"Successfully updated profile file:\n{outputFilePath}\n\n";
                 successMessage += $"Existing NPCs Updated: {updatedCount}\n";
                 successMessage += $"Missing NPCs Added: {addedCount}";

                 ScrollableMessageBox.Show(successMessage, "Update Complete");
            }
            catch (Exception ex)
            {
                ScrollableMessageBox.ShowError($"Failed to save the updated profile file:\n{ex.Message}", "File Save Error");
            }
        }
        
        public void Dispose()
        {
            _disposables.Dispose(); // Dispose all subscriptions
        }
    }
}