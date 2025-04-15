// [VM_NpcSelectionBar.cs] - Updated with JumpToModCommand
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics; // For Debug.WriteLine
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency; // Required for Unit
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Windows;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat;


namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_NpcSelectionBar : ReactiveObject, IDisposable
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly NpcDescriptionProvider _descriptionProvider;
        private readonly Auxilliary _auxilliary;
        private readonly CompositeDisposable _disposables = new();

        // *** NEW: Lazy references to break circular dependencies ***
        private readonly Lazy<VM_Mods> _lazyModsVm;
        private readonly Lazy<VM_MainWindow> _lazyMainWindowVm;

        private HashSet<string> _hiddenModNames = new();
        private Dictionary<FormKey, HashSet<string>> _hiddenModsPerNpc = new();

        // --- Search Properties ---
        [Reactive] public string SearchText1 { get; set; } = string.Empty;
        [Reactive] public NpcSearchType SearchType1 { get; set; } = NpcSearchType.Name;
        [Reactive] public string SearchText2 { get; set; } = string.Empty;
        [Reactive] public NpcSearchType SearchType2 { get; set; } = NpcSearchType.Name;
        [Reactive] public string SearchText3 { get; set; } = string.Empty;
        [Reactive] public NpcSearchType SearchType3 { get; set; } = NpcSearchType.Name;
        [Reactive] public bool IsSearchAndLogic { get; set; } = true;
        public Array AvailableSearchTypes => Enum.GetValues(typeof(NpcSearchType));
        // --- End Search Properties ---

        [Reactive] public bool ShowHiddenMods { get; set; } = false;
        [Reactive] public bool ShowNpcDescriptions { get; set; } // Moved from VM_Settings

        public List<VM_NpcSelection> AllNpcs { get; } = new();
        public ObservableCollection<VM_NpcSelection> FilteredNpcs { get; } = new();
        private Dictionary<string, List<(string ModName, string ImagePath)>> _mugshotData = new();

        [Reactive] public VM_NpcSelection? SelectedNpc { get; set; }
        [ObservableAsProperty] public ObservableCollection<VM_AppearanceMod>? CurrentNpcAppearanceMods { get; }

        [Reactive] public string? CurrentNpcDescription { get; private set; }
        public ReactiveCommand<Unit, string?> LoadDescriptionCommand { get; }
        [ObservableAsProperty] public bool IsLoadingDescription { get; }


        private readonly ISubject<Unit> _refreshImageSizesSubject = new Subject<Unit>();
        public IObservable<Unit> RefreshImageSizesObservable => _refreshImageSizesSubject.AsObservable();

        // *** UPDATED CONSTRUCTOR SIGNATURE ***
        public VM_NpcSelectionBar(EnvironmentStateProvider environmentStateProvider,
            Settings settings, 
            Auxilliary auxilliary,
            NpcConsistencyProvider consistencyProvider,
            NpcDescriptionProvider descriptionProvider,
            Lazy<VM_Mods> lazyModsVm,
            Lazy<VM_MainWindow> lazyMainWindowVm) 
        {
            _environmentStateProvider = environmentStateProvider;
            _settings = settings; 
            _auxilliary = auxilliary;
            _consistencyProvider = consistencyProvider;
            _descriptionProvider = descriptionProvider;
            _lazyModsVm = lazyModsVm;
            _lazyMainWindowVm = lazyMainWindowVm;

            _hiddenModNames = _settings.HiddenModNames ?? new(); // Ensure initialized
            _hiddenModsPerNpc = _settings.HiddenModsPerNpc ?? new(); // Ensure initialized

            // --- Existing Property Setup ---
            this.WhenAnyValue(x => x.SelectedNpc)
                .Select(selectedNpc => selectedNpc != null
                    ? CreateAppearanceModViewModels(selectedNpc, _mugshotData)
                    : new ObservableCollection<VM_AppearanceMod>())
                .ObserveOn(RxApp.MainThreadScheduler)
                .ToPropertyEx(this, x => x.CurrentNpcAppearanceMods);

            this.WhenAnyValue(x => x.CurrentNpcAppearanceMods)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ToggleModVisibility())
                .DisposeWith(_disposables);

             _consistencyProvider.NpcSelectionChanged
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(args => UpdateSelectionState(args.NpcFormKey, args.SelectedMod))
                .DisposeWith(_disposables);

            this.WhenAnyValue(
                    x => x.SearchText1, x => x.SearchType1,
                    x => x.SearchText2, x => x.SearchType2,
                    x => x.SearchText3, x => x.SearchType3,
                    x => x.IsSearchAndLogic)
                .Throttle(TimeSpan.FromMilliseconds(250), RxApp.MainThreadScheduler)
                .Subscribe(_ => ApplyFilter(false))
                .DisposeWith(_disposables);

            this.WhenAnyValue(x => x.ShowHiddenMods)
                .Subscribe(_ => ToggleModVisibility())
                .DisposeWith(_disposables);

            // --- Description Command Setup ---
            ShowNpcDescriptions = _settings.ShowNpcDescriptions;
            this.WhenAnyValue(x => x.ShowNpcDescriptions)
                .Subscribe(b => _settings.ShowNpcDescriptions = b) // Update model when VM changes
                .DisposeWith(_disposables);
            
            LoadDescriptionCommand = ReactiveCommand.CreateFromTask<Unit, string?>(
                async (_, ct) =>
                {
                    var npc = SelectedNpc;
                    // Use the local ShowNpcDescriptions property now
                    if (npc != null && ShowNpcDescriptions)
                    {
                        try { return await _descriptionProvider.GetDescriptionAsync(npc.NpcFormKey, npc.DisplayName, npc.NpcGetter?.EditorID); }
                        catch (Exception ex) { Debug.WriteLine($"Error executing LoadDescriptionCommand: {ex}"); return null; }
                    } return null;
                },
                // Update command CanExecute to use local property
                this.WhenAnyValue(x => x.SelectedNpc, x => x.ShowNpcDescriptions, (npc, show) => npc != null && show)
            );
            LoadDescriptionCommand.ObserveOn(RxApp.MainThreadScheduler).BindTo(this, x => x.CurrentNpcDescription).DisposeWith(_disposables);
            LoadDescriptionCommand.IsExecuting.ToPropertyEx(this, x => x.IsLoadingDescription).DisposeWith(_disposables);
            // Update trigger for InvokeCommand to use local property
            this.WhenAnyValue(x => x.SelectedNpc, x => x.ShowNpcDescriptions)
                .Throttle(TimeSpan.FromMilliseconds(200)).Select(_ => Unit.Default)
                .InvokeCommand(LoadDescriptionCommand).DisposeWith(_disposables);

            Initialize();
        }

        // --- Methods ---

        // *** NEW: JumpToMod Command Execution Logic ***
        public bool CanJumpToMod(string appearanceModName)
        {
            // Access VM_Mods via Lazy object
            var modsVm = _lazyModsVm.Value;
            if (modsVm == null)
            {
                return false;
            }
            
            var targetModSetting = modsVm.AllModSettings.FirstOrDefault(ms => ms.DisplayName.Equals(appearanceModName, StringComparison.OrdinalIgnoreCase));
            return targetModSetting != null;
        }
        
        public void JumpToMod(VM_AppearanceMod appearanceMod)
        {
            if (appearanceMod == null || string.IsNullOrWhiteSpace(appearanceMod.ModName)) return;

            string targetModName = appearanceMod.ModName;
            Debug.WriteLine($"JumpToMod requested for: {targetModName}");

            // Access VM_Mods via Lazy object
            var modsVm = _lazyModsVm.Value;
            if (modsVm == null)
            {
                MessageBox.Show("Mods view model is not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Find the VM_ModSetting by DisplayName
            // Search the *full* list in VM_Mods
            var targetModSetting = modsVm.AllModSettings.FirstOrDefault(ms => ms.DisplayName.Equals(targetModName, StringComparison.OrdinalIgnoreCase));

            if (targetModSetting != null)
            {
                 Debug.WriteLine($"Found target VM_ModSetting: {targetModSetting.DisplayName}");
                // 1. Switch to the Mods tab using Lazy MainWindow VM
                var mainWindowVm = _lazyMainWindowVm.Value;
                if (mainWindowVm == null) {
                     MessageBox.Show("Main window view model is not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                     return;
                 }
                 mainWindowVm.IsModsTabSelected = true;

                // 2. Tell VM_Mods to show the mugshots for the found setting
                // Schedule this to run after the tab switch might have occurred
                 RxApp.MainThreadScheduler.Schedule(() =>
                 {
                      // Ensure the target mod is visible in the filtered list *if possible*
                      // This is tricky. Simplest is to just clear filters if not visible.
                      if (!modsVm.ModSettingsList.Contains(targetModSetting))
                      {
                            Debug.WriteLine($"Target mod {targetModSetting.DisplayName} not in filtered list. Clearing filters.");
                            modsVm.NameFilterText = string.Empty;
                            modsVm.PluginFilterText = string.Empty;
                            modsVm.NpcSearchText = string.Empty;
                            // ApplyFilters() will be called automatically due to property changes
                      }

                      // Execute the command to show mugshots
                      modsVm.ShowMugshotsCommand.Execute(targetModSetting).Subscribe(
                          _ => { Debug.WriteLine($"Successfully triggered ShowMugshots for {targetModSetting.DisplayName}"); },
                          ex => { Debug.WriteLine($"Error executing ShowMugshotsCommand: {ex}"); }
                      ).DisposeWith(_disposables); // Dispose subscription

                      // TODO: Implement scrolling in ModsView.xaml.cs if needed.
                      // This would likely involve VM_Mods raising an event/signal
                      // with the target VM_ModSetting, and ModsView handling it.
                      Debug.WriteLine($"Scrolling to {targetModSetting.DisplayName} in ModsView is not yet implemented.");
                 });

            }
            else
            {
                Debug.WriteLine($"Could not find VM_ModSetting with DisplayName: {targetModName}");
                MessageBox.Show($"Could not find the mod '{targetModName}' in the Mods list.", "Mod Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


        private void UpdateSelectionState(FormKey npcFormKey, string selectedMod)
        {
            var npcVM = FilteredNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(npcFormKey))
                     ?? AllNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(npcFormKey));

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
            var previouslySelectedNpcKey = SelectedNpc?.NpcFormKey;
            SelectedNpc = null;
            AllNpcs.Clear();
            FilteredNpcs.Clear();
            CurrentNpcDescription = null; // Clear description on init

            if (!_environmentStateProvider.EnvironmentIsValid) {
                MessageBox.Show($"Environment is not valid. Check settings.\nError: {_environmentStateProvider.EnvironmentBuilderError}", "Environment Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                _mugshotData.Clear(); return;
            }
            _mugshotData = ScanMugshotDirectory();
            var processedMugshotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var npcRecords = _environmentStateProvider.LoadOrder.PriorityOrder.WinningOverrides<INpcGetter>()
                                 .Where(npc => npc.EditorID?.Contains("Preset", StringComparison.OrdinalIgnoreCase) == false && !npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.IsCharGenFacePreset))
                                 .OrderBy(x => _auxilliary.FormKeyStringToFormIDString(x.FormKey.ToString())).ToArray();
            foreach (var npc in npcRecords) {
                try {
                    var npcSelector = new VM_NpcSelection(npc, _environmentStateProvider, _consistencyProvider);
                    AllNpcs.Add(npcSelector);
                    string npcFormKeyString = npc.FormKey.ToString();
                    if (_mugshotData.ContainsKey(npcFormKeyString)) { processedMugshotKeys.Add(npcFormKeyString); }
                } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error initializing VM for NPC {npc.EditorID ?? npc.FormKey.ToString()}: {ex.Message}"); }
            }
            foreach (var kvp in _mugshotData) {
                 string mugshotFormKeyString = kvp.Key; List<(string ModName, string ImagePath)> mugshots = kvp.Value;
                 if (!processedMugshotKeys.Contains(mugshotFormKeyString) && mugshots.Any()) {
                     try {
                         FormKey mugshotFormKey = FormKey.Factory(mugshotFormKeyString);
                         var npcSelector = new VM_NpcSelection(mugshotFormKey, _environmentStateProvider, _consistencyProvider);
                         if (npcSelector.DisplayName == mugshotFormKeyString) { string firstModName = mugshots[0].ModName; string pluginBaseName = Path.GetFileNameWithoutExtension(mugshotFormKey.ModKey.FileName); npcSelector.DisplayName = $"{firstModName} - {pluginBaseName} [{mugshotFormKeyString}]"; }
                         AllNpcs.Add(npcSelector);
                     } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error creating VM for mugshot-only NPC {mugshotFormKeyString}: {ex.Message}"); }
                 }
            }
            ApplyFilter(true); // Apply filter to populate FilteredNpcs
             if (previouslySelectedNpcKey != null) { SelectedNpc = FilteredNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(previouslySelectedNpcKey)); }
             else if (!FilteredNpcs.Any()) { SelectedNpc = null; } // Explicitly null if no results
        }


        public void ApplyFilter(bool initializing) // Made public for potential external use
        {
            IEnumerable<VM_NpcSelection> results = AllNpcs;
            bool filter1Active = !string.IsNullOrWhiteSpace(SearchText1); bool filter2Active = !string.IsNullOrWhiteSpace(SearchText2); bool filter3Active = !string.IsNullOrWhiteSpace(SearchText3);
            Func<VM_NpcSelection, bool>? predicate1 = filter1Active ? BuildPredicate(SearchType1, SearchText1) : null;
            Func<VM_NpcSelection, bool>? predicate2 = filter2Active ? BuildPredicate(SearchType2, SearchText2) : null;
            Func<VM_NpcSelection, bool>? predicate3 = filter3Active ? BuildPredicate(SearchType3, SearchText3) : null;
            var activePredicates = new List<Func<VM_NpcSelection, bool>>();
            if (predicate1 != null) activePredicates.Add(predicate1); if (predicate2 != null) activePredicates.Add(predicate2); if (predicate3 != null) activePredicates.Add(predicate3);
            if (activePredicates.Any()) {
                if (IsSearchAndLogic) { results = results.Where(npc => activePredicates.All(p => p(npc))); }
                else { results = results.Where(npc => activePredicates.Any(p => p(npc))); }
            }
            var previouslySelectedNpcKey = SelectedNpc?.NpcFormKey;
            var orderedResults = results.OrderBy(x => _auxilliary.FormKeyStringToFormIDString(x.NpcFormKey.ToString())).ToList();

            // Efficiently update ObservableCollection
            FilteredNpcs.Clear(); // Clear once
            foreach (var npc in orderedResults) { FilteredNpcs.Add(npc); } // Add all new items


             // Restore selection or select first
             if (previouslySelectedNpcKey != null) {
                 SelectedNpc = FilteredNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(previouslySelectedNpcKey));
             }
              // If previous selection is gone or wasn't there, select the first item if any
              if (SelectedNpc == null && FilteredNpcs.Any() && !initializing) {
                  SelectedNpc = FilteredNpcs[0];
              } else if (!FilteredNpcs.Any()) { // Explicitly null if no results
                  SelectedNpc = null;
              }
        }

        private Func<VM_NpcSelection, bool> BuildPredicate(NpcSearchType type, string searchText)
        {
            return type switch {
                NpcSearchType.Name => npc => (npc.DisplayName?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0) || (npc.NpcGetter?.Name?.String?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0),
                NpcSearchType.EditorID => npc => npc.NpcGetter?.EditorID?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0,
                NpcSearchType.InAppearanceMod => npc => npc.AppearanceMods.Any(m => m.FileName.String.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0) || (_mugshotData.TryGetValue(npc.NpcFormKey.ToString(), out var mugshots) && mugshots.Any(m => m.ModName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)),
                NpcSearchType.FromMod => npc => npc.NpcFormKey.ModKey.FileName.String.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0,
                NpcSearchType.FormKey => npc => npc.NpcFormKey.ToString().IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0,
                _ => npc => true
            };
        }


        private ObservableCollection<VM_AppearanceMod> CreateAppearanceModViewModels(VM_NpcSelection npcVM, Dictionary<string, List<(string ModName, string ImagePath)>> mugshotData)
        {
            var modVMs = new ObservableCollection<VM_AppearanceMod>(); if (npcVM == null) return modVMs;
            string npcFormKeyString = npcVM.NpcFormKey.ToString(); var npcMugshotList = mugshotData.GetValueOrDefault(npcFormKeyString, new List<(string ModName, string ImagePath)>());
            var addedModSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (npcVM.NpcGetter != null) {
                foreach (var modKey in npcVM.AppearanceMods.Distinct()) {
                    string pluginModName = modKey.FileName; string? imagePathForThisPlugin = null;
                    var matchingMugshot = npcMugshotList.FirstOrDefault(m => m.ModName.Equals(pluginModName, StringComparison.OrdinalIgnoreCase));
                    if (matchingMugshot != default) { imagePathForThisPlugin = matchingMugshot.ImagePath; }
                    modVMs.Add(new VM_AppearanceMod(pluginModName, modKey, npcVM.NpcFormKey, imagePathForThisPlugin, _settings, _consistencyProvider, this)); addedModSources.Add(pluginModName);
                }
            }
            foreach (var mugshotInfo in npcMugshotList) {
                string mugshotModName = mugshotInfo.ModName; string mugshotImagePath = mugshotInfo.ImagePath;
                if (!addedModSources.Contains(mugshotModName)) { modVMs.Add(new VM_AppearanceMod(mugshotModName, npcVM.NpcFormKey.ModKey, npcVM.NpcFormKey, mugshotImagePath, _settings, _consistencyProvider, this)); addedModSources.Add(mugshotModName); }
            }
            if (npcVM.NpcGetter == null && !modVMs.Any() && npcVM.AppearanceMods.Any()) {
                 var baseModKey = npcVM.AppearanceMods.First();
                  if (!addedModSources.Contains(baseModKey.FileName)) { modVMs.Add(new VM_AppearanceMod(baseModKey.FileName, baseModKey, npcVM.NpcFormKey, null, _settings, _consistencyProvider, this)); }
            }
            var sortedVMs = modVMs.OrderBy(vm => vm.ModName).ToList(); modVMs.Clear(); foreach (var vm in sortedVMs) { modVMs.Add(vm); }

            // hide global hidden mods
            foreach (var m in sortedVMs)
            {
                if (_hiddenModNames.Contains(m.ModName))
                {
                    m.IsSetHidden = true;
                }
                else if (SelectedNpc is not null && _hiddenModsPerNpc.ContainsKey(SelectedNpc.NpcFormKey) &&
                         _hiddenModsPerNpc[SelectedNpc.NpcFormKey].Contains(m.ModName))
                {
                    m.IsSetHidden = true;
                }
            }

            return new(sortedVMs);
        }

        public void HideSelectedMod(VM_AppearanceMod referenceMod)
        {
            referenceMod.IsSetHidden = true;
            if (SelectedNpc != null) // Check if SelectedNpc is not null
            {
                 if (!_hiddenModsPerNpc.ContainsKey(SelectedNpc.NpcFormKey))
                 {
                     _hiddenModsPerNpc[SelectedNpc.NpcFormKey] = new HashSet<string>();
                 }
                 _hiddenModsPerNpc[SelectedNpc.NpcFormKey].Add(referenceMod.ModName);
            }
            ToggleModVisibility(); // Update visibility after modifying hidden state
        }

        public void UnhideSelectedMod(VM_AppearanceMod referenceMod)
        {
            referenceMod.IsSetHidden = false; // Unhide first
             if (SelectedNpc != null && _hiddenModsPerNpc.TryGetValue(SelectedNpc.NpcFormKey, out var hiddenSet))
            {
                 if (hiddenSet.Remove(referenceMod.ModName))
                 {
                     if (!hiddenSet.Any()) // Remove the key if the set becomes empty
                     {
                          _hiddenModsPerNpc.Remove(SelectedNpc.NpcFormKey);
                     }
                 }
            }
            ToggleModVisibility(); // Update visibility after modifying hidden state
        }

        public void SelectAllFromMod(VM_AppearanceMod referenceMod)
        {
             // Implementation needed - Iterate AllNpcs, find those with this mod in AppearanceMods,
             // and call _consistencyProvider.SetSelectedMod for each.
              MessageBox.Show($"Select All From Mod '{referenceMod.ModName}' - Not Yet Implemented.");
              // throw new NotImplementedException();
        }

        public void HideAllFromMod(VM_AppearanceMod referenceMod)
        {
            if (_hiddenModNames.Add(referenceMod.ModName)) // Add returns true if added successfully (wasn't there)
            {
                // Also update the IsSetHidden state for the *current* NPC's mods if visible
                 if (CurrentNpcAppearanceMods != null)
                 {
                      foreach (var modVM in CurrentNpcAppearanceMods)
                      {
                          if (modVM.ModName.Equals(referenceMod.ModName, StringComparison.OrdinalIgnoreCase))
                          {
                              modVM.IsSetHidden = true;
                          }
                      }
                 }
            }
            ToggleModVisibility(); // Update visibility for the current NPC
        }

        public void UnhideAllFromMod(VM_AppearanceMod referenceMod)
        {
             if (_hiddenModNames.Remove(referenceMod.ModName)) // Remove returns true if removed successfully
             {
                  // Also update the IsSetHidden state for the *current* NPC's mods if visible
                 if (CurrentNpcAppearanceMods != null)
                 {
                      foreach (var modVM in CurrentNpcAppearanceMods)
                      {
                          if (modVM.ModName.Equals(referenceMod.ModName, StringComparison.OrdinalIgnoreCase))
                          {
                               // Only unhide if it's not *also* hidden per-NPC
                               bool isHiddenPerNpc = SelectedNpc != null &&
                                                    _hiddenModsPerNpc.TryGetValue(SelectedNpc.NpcFormKey, out var hiddenSet) &&
                                                    hiddenSet.Contains(modVM.ModName);
                              modVM.IsSetHidden = isHiddenPerNpc; // Set based on per-NPC state now
                          }
                      }
                 }
             }
             ToggleModVisibility(); // Update visibility for the current NPC
        }

        public void ToggleModVisibility()
        {
            if (CurrentNpcAppearanceMods == null || !CurrentNpcAppearanceMods.Any()) return;

            bool needsRefresh = false;
            var npcSpecificHidden = SelectedNpc != null ? _hiddenModsPerNpc.GetValueOrDefault(SelectedNpc.NpcFormKey) : null;

            foreach (var mod in CurrentNpcAppearanceMods)
            {
                // Determine the definitive hidden state based on global AND specific lists
                bool shouldBeHidden = _hiddenModNames.Contains(mod.ModName) || (npcSpecificHidden?.Contains(mod.ModName) ?? false);
                mod.IsSetHidden = shouldBeHidden; // Update the source-of-truth hidden state

                // Determine if it should be VISIBLE based on the ShowHiddenMods toggle
                bool shouldBeVisible = ShowHiddenMods || !mod.IsSetHidden;

                if (mod.IsVisible != shouldBeVisible)
                {
                     mod.IsVisible = shouldBeVisible;
                     needsRefresh = true; // Visibility actually changed
                }
            }
            if (needsRefresh)
            {
                _refreshImageSizesSubject.OnNext(Unit.Default); // Sends the signal only if visibility changed
            }
        }


        public void Dispose() { _disposables.Dispose(); ClearAppearanceModViewModels(); }
        private void ClearAppearanceModViewModels() { if (CurrentNpcAppearanceMods != null) { foreach (var vm in CurrentNpcAppearanceMods) { vm.Dispose(); } CurrentNpcAppearanceMods.Clear(); } }
    }
}