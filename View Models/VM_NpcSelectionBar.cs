// [VM_NpcSelectionBar.cs] - Updated with Search Functionality
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat;
using System.ComponentModel; // For Enum Description
using DynamicData.Binding; // If using ObservableCollectionExtended or similar

namespace NPC_Plugin_Chooser_2.View_Models
{
    // Add NpcSearchType enum definition here or in a separate file within the namespace

    public class VM_NpcSelectionBar : ReactiveObject, IDisposable
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly Auxilliary _auxilliary;
        private readonly CompositeDisposable _disposables = new();

        // --- Search Properties ---
        [Reactive] public string SearchText1 { get; set; } = string.Empty;
        [Reactive] public NpcSearchType SearchType1 { get; set; } = NpcSearchType.Name;
        [Reactive] public string SearchText2 { get; set; } = string.Empty;
        [Reactive] public NpcSearchType SearchType2 { get; set; } = NpcSearchType.Name;
        [Reactive] public string SearchText3 { get; set; } = string.Empty;
        [Reactive] public NpcSearchType SearchType3 { get; set; } = NpcSearchType.Name;
        [Reactive] public bool IsSearchAndLogic { get; set; } = true; // True for AND, False for OR

        // Available search types for dropdowns
        public Array AvailableSearchTypes => Enum.GetValues(typeof(NpcSearchType));
        // --- End Search Properties ---

        // Original list of all NPCs
        private List<VM_NpcSelection> _allNpcs = new();

        // *** CHANGED: This is now the FILTERED list for the UI ***
        public ObservableCollection<VM_NpcSelection> FilteredNpcs { get; } = new();

        // Stores mugshot data (used in filtering)
        private Dictionary<string, List<(string ModName, string ImagePath)>> _mugshotData = new();

        // Keep original Npcs property name for binding compatibility if needed,
        // but it now points to the filtered list. Consider renaming if possible.
        // public ObservableCollection<VM_NpcSelection> Npcs => FilteredNpcs; // Alias if needed

        [Reactive] public VM_NpcSelection? SelectedNpc { get; set; }

        [ObservableAsProperty] public ObservableCollection<VM_AppearanceMod>? CurrentNpcAppearanceMods { get; }

        public VM_NpcSelectionBar(EnvironmentStateProvider environmentStateProvider, Settings settings, Auxilliary auxilliary, NpcConsistencyProvider consistencyProvider)
        {
            _environmentStateProvider = environmentStateProvider;
            _settings = settings;
            _auxilliary = auxilliary;
            _consistencyProvider = consistencyProvider;

            // Create Appearance Mod VMs based on SelectedNpc
            this.WhenAnyValue(x => x.SelectedNpc)
                .Select(selectedNpc => selectedNpc != null
                    ? CreateAppearanceModViewModels(selectedNpc, _mugshotData)
                    : new ObservableCollection<VM_AppearanceMod>())
                .ObserveOn(RxApp.MainThreadScheduler)
                .ToPropertyEx(this, x => x.CurrentNpcAppearanceMods);

            // Subscribe to selection changes from the consistency provider
             _consistencyProvider.NpcSelectionChanged
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(args => UpdateSelectionState(args.NpcFormKey, args.SelectedMod))
                .DisposeWith(_disposables);

            // --- Setup Search Filtering Trigger ---
            this.WhenAnyValue(
                    x => x.SearchText1, x => x.SearchType1,
                    x => x.SearchText2, x => x.SearchType2,
                    x => x.SearchText3, x => x.SearchType3,
                    x => x.IsSearchAndLogic)
                .Throttle(TimeSpan.FromMilliseconds(250), RxApp.MainThreadScheduler) // Debounce search
                .Subscribe(_ => ApplyFilter())
                .DisposeWith(_disposables);
            // --- End Search Filtering Trigger ---

            // Initial population
            Initialize(); // Populates _allNpcs and applies initial filter
        }

        private void UpdateSelectionState(FormKey npcFormKey, string selectedMod)
        {
            // Find in the *filtered* list first, as that's what the user sees
            var npcVM = FilteredNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(npcFormKey));
            // If not found in filtered list (e.g., due to search), find in the master list
            if (npcVM == null)
            {
                 npcVM = _allNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(npcFormKey));
            }

            if (npcVM != null)
            {
                npcVM.SelectedAppearanceMod = selectedMod;
                if (SelectedNpc == npcVM && CurrentNpcAppearanceMods != null)
                {
                    foreach (var modVM in CurrentNpcAppearanceMods)
                    {
                        modVM.IsSelected = modVM.ModName.Equals(selectedMod, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }


        private static readonly Regex PluginRegex = new(@"^.+\.(esm|esp|esl)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HexFileRegex = new(@"^[0-9A-F]{8}\.(png|jpg|jpeg|bmp)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private Dictionary<string, List<(string ModName, string ImagePath)>> ScanMugshotDirectory()
        {
            var results = new Dictionary<string, List<(string ModName, string ImagePath)>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(_settings.MugshotsFolder) || !Directory.Exists(_settings.MugshotsFolder)) return results;

            System.Diagnostics.Debug.WriteLine($"Scanning mugshot directory: {_settings.MugshotsFolder}");
            string expectedParentPath = _settings.MugshotsFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            try
            {
                var potentialFiles = Directory.EnumerateFiles(_settings.MugshotsFolder, "*.*", SearchOption.AllDirectories)
                                              .Where(f => HexFileRegex.IsMatch(Path.GetFileName(f)));
                foreach (var filePath in potentialFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath); string hexFileName = fileInfo.Name;
                        DirectoryInfo? pluginDir = fileInfo.Directory; if (pluginDir == null || !PluginRegex.IsMatch(pluginDir.Name)) continue; string pluginName = pluginDir.Name;
                        DirectoryInfo? modDir = pluginDir.Parent; if (modDir == null || string.IsNullOrWhiteSpace(modDir.Name)) continue; string modName = modDir.Name;
                        if (modDir.Parent == null || !modDir.Parent.FullName.Equals(expectedParentPath, StringComparison.OrdinalIgnoreCase)) continue;
                        string hexPart = Path.GetFileNameWithoutExtension(hexFileName); if (hexPart.Length != 8) continue;
                        string formKeyString = $"{hexPart.Substring(hexPart.Length - 6)}:{pluginName}";
                        try { FormKey.Factory(formKeyString); } catch { continue; }
                        var mugshotInfo = (ModName: modName, ImagePath: filePath);
                        if (results.TryGetValue(formKeyString, out var list)) { if (!list.Any(i => i.ModName.Equals(modName, StringComparison.OrdinalIgnoreCase))) list.Add(mugshotInfo); }
                        else { results[formKeyString] = new List<(string ModName, string ImagePath)> { mugshotInfo }; }
                    } catch { /* Ignore individual file errors during scan */ }
                }
            } catch { /* Ignore directory access errors */ }
            System.Diagnostics.Debug.WriteLine($"Mugshot scan complete. Found entries for {results.Count} unique FormKeys.");
            return results;
        }

        public void Initialize()
        {
            // Clear previous state
            var previouslySelectedNpcKey = SelectedNpc?.NpcFormKey;
            SelectedNpc = null;
            _allNpcs.Clear();
            FilteredNpcs.Clear(); // Clear UI list

            if (!_environmentStateProvider.EnvironmentIsValid)
            {
                MessageBox.Show($"Environment is not valid. Check settings.\nError: {_environmentStateProvider.EnvironmentBuilderError}", "Environment Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                _mugshotData.Clear();
                return; // Stop initialization
            }

            // 1. Scan Mugshots (needed for filtering and VM creation)
            _mugshotData = ScanMugshotDirectory();
            var processedMugshotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 2. Process NPCs from Load Order -> Populate _allNpcs
            var npcRecords = _environmentStateProvider.LoadOrder.PriorityOrder
                                 .WinningOverrides<INpcGetter>()
                                 .Where(npc => npc.EditorID?.Contains("Preset", StringComparison.OrdinalIgnoreCase) == false &&
                                               !npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.IsCharGenFacePreset))
                                 .OrderBy(x => _auxilliary.FormKeyStringToFormIDString(x.FormKey.ToString()))
                                 .ToArray();

            foreach (var npc in npcRecords)
            {
                try {
                    var npcSelector = new VM_NpcSelection(npc, _environmentStateProvider, _consistencyProvider);
                    _allNpcs.Add(npcSelector); // Add to master list
                    string npcFormKeyString = npc.FormKey.ToString();
                    if (_mugshotData.ContainsKey(npcFormKeyString)) { processedMugshotKeys.Add(npcFormKeyString); }
                } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error initializing VM for NPC {npc.EditorID ?? npc.FormKey.ToString()}: {ex.Message}"); }
            }

            // 3. Process NPCs found ONLY in Mugshots -> Populate _allNpcs
            foreach (var kvp in _mugshotData)
            {
                 string mugshotFormKeyString = kvp.Key;
                 List<(string ModName, string ImagePath)> mugshots = kvp.Value;
                 if (!processedMugshotKeys.Contains(mugshotFormKeyString) && mugshots.Any())
                 {
                     try {
                         FormKey mugshotFormKey = FormKey.Factory(mugshotFormKeyString);
                         var npcSelector = new VM_NpcSelection(mugshotFormKey, _environmentStateProvider, _consistencyProvider);
                         if (npcSelector.DisplayName == mugshotFormKeyString) {
                             string firstModName = mugshots[0].ModName; string pluginBaseName = Path.GetFileNameWithoutExtension(mugshotFormKey.ModKey.FileName);
                             npcSelector.DisplayName = $"{firstModName} - {pluginBaseName} [{mugshotFormKeyString}]";
                         }
                         _allNpcs.Add(npcSelector); // Add to master list
                     } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error creating VM for mugshot-only NPC {mugshotFormKeyString}: {ex.Message}"); }
                 }
            }

            // 4. Apply initial filter (which populates FilteredNpcs)
            ApplyFilter();

            // 5. Restore Selection (search within FilteredNpcs)
            if (previouslySelectedNpcKey != null) {
                SelectedNpc = FilteredNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(previouslySelectedNpcKey));
            }
            if (SelectedNpc == null && FilteredNpcs.Any()) {
                SelectedNpc = FilteredNpcs[0]; // Select first item in the filtered list
            }
        }

        // --- Filtering Logic ---
        private void ApplyFilter()
        {
            IEnumerable<VM_NpcSelection> results = _allNpcs;
            bool filter1Active = !string.IsNullOrWhiteSpace(SearchText1);
            bool filter2Active = !string.IsNullOrWhiteSpace(SearchText2);
            bool filter3Active = !string.IsNullOrWhiteSpace(SearchText3);

            // Build predicates for active filters
            Func<VM_NpcSelection, bool>? predicate1 = filter1Active ? BuildPredicate(SearchType1, SearchText1) : null;
            Func<VM_NpcSelection, bool>? predicate2 = filter2Active ? BuildPredicate(SearchType2, SearchText2) : null;
            Func<VM_NpcSelection, bool>? predicate3 = filter3Active ? BuildPredicate(SearchType3, SearchText3) : null;

            var activePredicates = new List<Func<VM_NpcSelection, bool>>();
            if (predicate1 != null) activePredicates.Add(predicate1);
            if (predicate2 != null) activePredicates.Add(predicate2);
            if (predicate3 != null) activePredicates.Add(predicate3);

            if (activePredicates.Any())
            {
                if (IsSearchAndLogic) // AND Logic
                {
                    results = results.Where(npc => activePredicates.All(p => p(npc)));
                }
                else // OR Logic
                {
                    results = results.Where(npc => activePredicates.Any(p => p(npc)));
                }
            }
            // If no filters active, results remains _allNpcs

            // Update the observable collection bound to the UI
            // Efficient update is tricky; Clear/Add is simplest for moderate lists.
            // Consider DynamicData library for large lists and complex updates.
            var previouslySelectedNpcKey = SelectedNpc?.NpcFormKey; // Preserve selection

            FilteredNpcs.Clear();
            foreach (var npc in results.OrderBy(x => _auxilliary.FormKeyStringToFormIDString(x.NpcFormKey.ToString()))) // Keep sorted
            {
                FilteredNpcs.Add(npc);
            }

            // Try to restore selection within the new filtered list
             if (previouslySelectedNpcKey != null) {
                 SelectedNpc = FilteredNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(previouslySelectedNpcKey));
             }
             // If selection lost and list has items, select the first one
              if (SelectedNpc == null && FilteredNpcs.Any()) {
                  SelectedNpc = FilteredNpcs[0];
              }
               // If selection lost and list is empty, ensure selection is null
              else if (!FilteredNpcs.Any()) {
                  SelectedNpc = null;
              }
        }

        private Func<VM_NpcSelection, bool> BuildPredicate(NpcSearchType type, string searchText)
        {
            // Pre-compile contains check for performance if needed, though likely minor here
            // StringComparison comparison = StringComparison.OrdinalIgnoreCase; // Use OrdinalIgnoreCase for non-linguistic comparison

            return type switch
            {
                NpcSearchType.Name => npc =>
                    (npc.DisplayName?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (npc.NpcGetter?.Name?.String?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0),

                NpcSearchType.EditorID => npc =>
                    npc.NpcGetter?.EditorID?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0,

                NpcSearchType.InAppearanceMod => npc =>
                    // Check plugin sources
                    npc.AppearanceMods.Any(m => m.FileName.String.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    // Check mugshot sources by ModName folder
                    (_mugshotData.TryGetValue(npc.NpcFormKey.ToString(), out var mugshots) &&
                     mugshots.Any(m => m.ModName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)),

                NpcSearchType.FromMod => npc =>
                    npc.NpcFormKey.ModKey.FileName.String.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0, // Contains check

                NpcSearchType.FormKey => npc =>
                    npc.NpcFormKey.ToString().IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0, // Contains check

                _ => npc => true // Should not happen
            };
        }
        // --- End Filtering Logic ---

        private ObservableCollection<VM_AppearanceMod> CreateAppearanceModViewModels(
            VM_NpcSelection npcVM,
            Dictionary<string, List<(string ModName, string ImagePath)>> mugshotData)
        {
            // (Logic from previous step - No changes needed here for search)
            var modVMs = new ObservableCollection<VM_AppearanceMod>();
            if (npcVM == null) return modVMs;
            string npcFormKeyString = npcVM.NpcFormKey.ToString();
            var npcMugshotList = mugshotData.GetValueOrDefault(npcFormKeyString, new List<(string ModName, string ImagePath)>());
            var addedModSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (npcVM.NpcGetter != null) {
                foreach (var modKey in npcVM.AppearanceMods.Distinct()) {
                    string pluginModName = modKey.FileName; string? imagePathForThisPlugin = null;
                    var matchingMugshot = npcMugshotList.FirstOrDefault(m => m.ModName.Equals(pluginModName, StringComparison.OrdinalIgnoreCase));
                    if (matchingMugshot != default) { imagePathForThisPlugin = matchingMugshot.ImagePath; }
                    modVMs.Add(new VM_AppearanceMod(pluginModName, modKey, npcVM.NpcFormKey, imagePathForThisPlugin, _settings, _consistencyProvider));
                    addedModSources.Add(pluginModName);
                }
            }
            foreach (var mugshotInfo in npcMugshotList) {
                string mugshotModName = mugshotInfo.ModName; string mugshotImagePath = mugshotInfo.ImagePath;
                if (!addedModSources.Contains(mugshotModName)) {
                    modVMs.Add(new VM_AppearanceMod(mugshotModName, npcVM.NpcFormKey.ModKey, npcVM.NpcFormKey, mugshotImagePath, _settings, _consistencyProvider));
                    addedModSources.Add(mugshotModName);
                }
            }
             if (npcVM.NpcGetter == null && !modVMs.Any() && npcVM.AppearanceMods.Any()) {
                 var baseModKey = npcVM.AppearanceMods.First();
                  if (!addedModSources.Contains(baseModKey.FileName)) {
                       modVMs.Add(new VM_AppearanceMod(baseModKey.FileName, baseModKey, npcVM.NpcFormKey, null, _settings, _consistencyProvider));
                  }
             }
            var sortedVMs = modVMs.OrderBy(vm => vm.ModName).ToList(); modVMs.Clear(); foreach (var vm in sortedVMs) { modVMs.Add(vm); }
            return modVMs;
        }

        public void Dispose() { _disposables.Dispose(); ClearAppearanceModViewModels(); }
        private void ClearAppearanceModViewModels() { if (CurrentNpcAppearanceMods != null) { foreach (var vm in CurrentNpcAppearanceMods) { vm.Dispose(); } } }
    }
}