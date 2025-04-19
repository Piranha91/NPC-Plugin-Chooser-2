// VM_Settings.cs (Updated)

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
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
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_Settings : ReactiveObject
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _model; // Renamed from settings to _model for clarity
        private readonly VM_NpcSelectionBar _npcSelectionBar;
        private readonly VM_Mods _modListVM;
        private readonly NpcConsistencyProvider _consistencyProvider; 

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

        // --- Commands ---
        public ReactiveCommand<Unit, Unit> SelectGameFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectModsFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectMugshotsFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectOutputDirectoryCommand { get; } // New
        public ReactiveCommand<Unit, Unit> ShowPatchingModeHelpCommand { get; } // New
        public ReactiveCommand<Unit, Unit> ImportEasyNpcCommand { get; } // New
        public ReactiveCommand<Unit, Unit> ExportEasyNpcCommand { get; } // New

        public VM_Settings(EnvironmentStateProvider environmentStateProvider, Settings settings, VM_NpcSelectionBar npcSelectionBar, VM_Mods modListVM, NpcConsistencyProvider consistencyProvider)
        {
            _environmentStateProvider = environmentStateProvider;
            _npcSelectionBar = npcSelectionBar;
            _modListVM = modListVM;
            _model = settings; // Use the injected model instance
            _consistencyProvider = consistencyProvider;

            Application.Current.Exit += (_, __) => { SaveSettings(); };

            // Initialize VM properties from the model
            ModsFolder = _model.ModsFolder;
            MugshotsFolder = _model.MugshotsFolder;
            SkyrimRelease = _model.SkyrimRelease;
            SkyrimGamePath = _model.SkyrimGamePath;
            OutputPluginName = _model.OutputPluginName; 
            OutputDirectory = _model.OutputDirectory;
            AppendTimestampToOutputDirectory = _model.AppendTimestampToOutputDirectory;
            SelectedPatchingMode = _model.PatchingMode;
            ExclusionSelectorViewModel = new VM_ModSelector();
            ExclusionSelectorViewModel.LoadFromModel(_environmentStateProvider.LoadOrder.Keys, _model.EasyNpcDefaultPluginExclusions);

            // Update model when VM properties change
            this.WhenAnyValue(x => x.ModsFolder).Subscribe(s => _model.ModsFolder = s);
            this.WhenAnyValue(x => x.MugshotsFolder).Subscribe(s => _model.MugshotsFolder = s);
            this.WhenAnyValue(x => x.OutputDirectory).Subscribe(s => _model.OutputDirectory = s);
            this.WhenAnyValue(x => x.AppendTimestampToOutputDirectory).Subscribe(b => _model.AppendTimestampToOutputDirectory = b);
            this.WhenAnyValue(x => x.SelectedPatchingMode).Subscribe(pm => _model.PatchingMode = pm);

            // Properties that trigger environment update
            this.WhenAnyValue(x => x.SkyrimGamePath).Subscribe(s =>
            {
                _model.SkyrimGamePath = s;
                _environmentStateProvider.DataFolderPath = s;
                UpdateEnvironmentAndNotify();
            });
            this.WhenAnyValue(x => x.SkyrimRelease).Subscribe(r =>
            {
                _model.SkyrimRelease = r;
                _environmentStateProvider.SkyrimVersion = r;
                UpdateEnvironmentAndNotify();
            });
            this.WhenAnyValue(x => x.OutputPluginName) // Trigger on actual plugin name change
                .Where(x => !x.IsNullOrWhitespace())
                .Subscribe(s =>
                {
                    _model.OutputPluginName = s;
                    // Update the provider's understanding of the output mod's name
                    _environmentStateProvider.OutputPluginName = s;
                    UpdateEnvironmentAndNotify();
                });

            // Expose environment state reactively
            this.WhenAnyValue(x => x._environmentStateProvider.EnvironmentIsValid)
                .ToPropertyEx(this, x => x.EnvironmentIsValid);

            this.WhenAnyValue(x => x._environmentStateProvider.EnvironmentBuilderError)
                 .Select(err => string.IsNullOrWhiteSpace(err) ? string.Empty : $"Environment Error: {err}")
                .ToPropertyEx(this, x => x.EnvironmentErrorText);

            // Populate AvailablePluginsForExclusion (existing code...)
            this.WhenAnyValue(x => x.EnvironmentIsValid)
                .Select(_ => _environmentStateProvider.EnvironmentIsValid
                    ? _environmentStateProvider.LoadOrder.Keys.ToList()
                    : Enumerable.Empty<ModKey>())
                .ObserveOn(RxApp.MainThreadScheduler)
                .ToPropertyEx(this, x => x.AvailablePluginsForExclusion);

            // --- Update ModSelectorViewModel when available plugins change --- NEW
            this.WhenAnyValue(x => x.AvailablePluginsForExclusion)
                .Where(list => list != null)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(availablePlugins =>
                {
                    // --- Get the currently selected items from the UI's ViewModel ---
                    var currentUISelctions = ExclusionSelectorViewModel.SaveToModel();

                    // --- Reload using the current UI selections, not the model's ---
                    ExclusionSelectorViewModel.LoadFromModel(availablePlugins, currentUISelctions);
                });


            // Folder Browser Commands
            SelectGameFolderCommand = ReactiveCommand.Create(SelectGameFolder);
            SelectModsFolderCommand = ReactiveCommand.Create(SelectModsFolder);
            SelectMugshotsFolderCommand = ReactiveCommand.Create(SelectMugshotsFolder);
            SelectOutputDirectoryCommand = ReactiveCommand.Create(SelectOutputDirectory); // New
            ShowPatchingModeHelpCommand = ReactiveCommand.Create(ShowPatchingModeHelp); // New
            ImportEasyNpcCommand = ReactiveCommand.Create(ImportEasyNpc); // New
            ExportEasyNpcCommand = ReactiveCommand.Create(ExportEasyNpc); // New

            // Initial environment check
            UpdateEnvironmentAndNotify();
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
                     MessageBox.Show($"Error loading settings from {settingsPath}:\n{exception}\n\nDefault settings will be used.", "Settings Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            _npcSelectionBar.Initialize();
            _modListVM.PopulateModSettings();
            _modListVM.ApplyFilters(); // Re-apply filters after mods repopulate
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
                 _modListVM.PopulateModSettings(); // Refresh mods list view
                 _modListVM.ApplyFilters();
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
                _npcSelectionBar.Initialize();
                _modListVM.PopulateModSettings();
                _modListVM.ApplyFilters();
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

            MessageBox.Show(helpText, "Patching Mode Information", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    if (!_modListVM.TryGetModSettingForPlugin(appearanceKey, out _, out string targetModDisplayName))
                    {
                        // Not found - track it and skip adding to potentialChanges for now
                        missingAppearancePlugins.Add(appearanceKey);
                        Debug.WriteLine($"ImportEasyNpc: Appearance Plugin '{appearanceKey}' not found in Mods Menu for NPC {npcKey}.");
                        // We don't add this to potentialChanges yet
                    }
                    else
                    {
                        // Found - add to potential changes
                        string npcName = _npcSelectionBar.AllNpcs.FirstOrDefault(n => n.NpcFormKey == npcKey)?.DisplayName ?? npcKey.ToString();
                        potentialChanges.Add((npcKey, defaultKey, appearanceKey, npcName, targetModDisplayName));
                    }
                } // End foreach line
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading file '{filePath}':\n{ex.Message}", "File Read Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // --- Handle Parsing Errors (Optional: Show before Missing Plugin check) ---
            if (errors.Any())
            {
                var errorMsg = new StringBuilder($"Encountered {errors.Count} errors while parsing '{Path.GetFileName(filePath)}':\n\n");
                errorMsg.AppendLine(string.Join("\n", errors.Take(20)));
                if (errors.Count > 20) errorMsg.AppendLine("\n...");
                errorMsg.AppendLine("\nThese lines were skipped. Continue processing?");
                if (MessageBox.Show(errorMsg.ToString(), "Parsing Errors", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
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


                MessageBoxResult choice = MessageBox.Show(missingMsg.ToString(), "Missing Appearance Plugin Mappings", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (choice == MessageBoxResult.No) // User chose Cancel
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
                MessageBox.Show("No valid changes found to apply after processing the file (possibly due to skipping or parsing errors).", "Import Empty", MessageBoxButton.OK, MessageBoxImage.Information);
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

            if (MessageBox.Show(confirmationMessage, "Confirm Import", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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
                _npcSelectionBar.ApplyFilter(false);

                MessageBox.Show($"Successfully imported settings for {appliedCount} NPCs.", "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                 MessageBox.Show("Import cancelled.", "Import Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }


        private void ExportEasyNpc() // New Placeholder
        {
             MessageBox.Show("Export to EasyNPC - Not yet implemented.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            // Future implementation here
        }
    }
}