// [VM_NpcSelectionBar.cs] - Full Code After Modifications
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics; // For Debug.WriteLine
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency; // Required for Unit
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Threading.Tasks; // Added for Task
using System.Windows; // Added for MessageBox
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
        // --- Define the Factory Delegate ---
        public delegate VM_AppearanceMod AppearanceModFactory(
            string modName,
            FormKey npcFormKey,
            ModKey? overrideModeKey,
            string? imagePath
        );

        // --- Dependencies ---
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly NpcDescriptionProvider _descriptionProvider;
        private readonly Auxilliary _auxilliary;
        private readonly CompositeDisposable _disposables = new();
        private readonly Lazy<VM_Mods> _lazyModsVm;
        private readonly Lazy<VM_MainWindow> _lazyMainWindowVm;
        private readonly AppearanceModFactory _appearanceModFactory;

        // --- Internal State ---
        private HashSet<string> _hiddenModNames = new();
        private Dictionary<FormKey, HashSet<string>> _hiddenModsPerNpc = new();
        private Dictionary<string, List<(string ModName, string ImagePath)>> _mugshotData = new();
        private readonly ISubject<Unit> _refreshImageSizesSubject = new Subject<Unit>();

        // --- Search Properties ---
        [Reactive] public string SearchText1 { get; set; } = string.Empty;
        [Reactive] public NpcSearchType SearchType1 { get; set; } = NpcSearchType.Name;
        [Reactive] public string SearchText2 { get; set; } = string.Empty;
        [Reactive] public NpcSearchType SearchType2 { get; set; } = NpcSearchType.EditorID;
        [Reactive] public string SearchText3 { get; set; } = string.Empty;
        [Reactive] public NpcSearchType SearchType3 { get; set; } = NpcSearchType.InAppearanceMod;

        // Visibility & Selection State Filters
        [ObservableAsProperty] public bool IsSelectionStateSearch1 { get; }
        [Reactive] public SelectionStateFilterType SelectedStateFilter1 { get; set; } = SelectionStateFilterType.NotMade;
        [ObservableAsProperty] public bool IsSelectionStateSearch2 { get; }
        [Reactive] public SelectionStateFilterType SelectedStateFilter2 { get; set; } = SelectionStateFilterType.NotMade;
        [ObservableAsProperty] public bool IsSelectionStateSearch3 { get; }
        [Reactive] public SelectionStateFilterType SelectedStateFilter3 { get; set; } = SelectionStateFilterType.NotMade;
        public Array AvailableSelectionStateFilters => Enum.GetValues(typeof(SelectionStateFilterType));

        // Group Filter Visibility & Selection
        [ObservableAsProperty] public bool IsGroupSearch1 { get; }
        [Reactive] public string? SelectedGroupFilter1 { get; set; }
        [ObservableAsProperty] public bool IsGroupSearch2 { get; }
        [Reactive] public string? SelectedGroupFilter2 { get; set; }
        [ObservableAsProperty] public bool IsGroupSearch3 { get; }
        [Reactive] public string? SelectedGroupFilter3 { get; set; }

        [Reactive] public bool IsSearchAndLogic { get; set; } = true;
        public Array AvailableSearchTypes => Enum.GetValues(typeof(NpcSearchType));
        // --- End Search Properties ---

        // --- UI / Display Properties ---
        [Reactive] public bool ShowHiddenMods { get; set; } = false;
        [Reactive] public bool ShowNpcDescriptions { get; set; }
        public List<VM_NpcSelection> AllNpcs { get; } = new();
        public ObservableCollection<VM_NpcSelection> FilteredNpcs { get; } = new();
        [Reactive] public VM_NpcSelection? SelectedNpc { get; set; }
        [ObservableAsProperty] public ObservableCollection<VM_AppearanceMod>? CurrentNpcAppearanceMods { get; }
        [Reactive] public string? CurrentNpcDescription { get; private set; }
        public ReactiveCommand<Unit, string?> LoadDescriptionCommand { get; }
        [ObservableAsProperty] public bool IsLoadingDescription { get; }
        public IObservable<Unit> RefreshImageSizesObservable => _refreshImageSizesSubject.AsObservable();
        public Interaction<VM_NpcSelection, Unit> ScrollToNpcInteraction { get; }

        // --- NPC Group Properties ---
        [Reactive] public string SelectedGroupName { get; set; } = string.Empty;
        public ObservableCollection<string> AvailableNpcGroups { get; } = new();
        public ReactiveCommand<Unit, Unit> AddCurrentNpcToGroupCommand { get; }
        public ReactiveCommand<Unit, Unit> RemoveCurrentNpcFromGroupCommand { get; }
        public ReactiveCommand<Unit, Unit> AddAllVisibleNpcsToGroupCommand { get; }
        public ReactiveCommand<Unit, Unit> RemoveAllVisibleNpcsFromGroupCommand { get; }
        // --- End NPC Group Properties ---

        // --- Constructor ---
        public VM_NpcSelectionBar(EnvironmentStateProvider environmentStateProvider,
            Settings settings,
            Auxilliary auxilliary,
            NpcConsistencyProvider consistencyProvider,
            NpcDescriptionProvider descriptionProvider,
            Lazy<VM_Mods> lazyModsVm,
            Lazy<VM_MainWindow> lazyMainWindowVm,
            AppearanceModFactory appearanceModFactory)
        {
            _environmentStateProvider = environmentStateProvider;
            _settings = settings;
            _auxilliary = auxilliary;
            _consistencyProvider = consistencyProvider;
            _descriptionProvider = descriptionProvider;
            _lazyModsVm = lazyModsVm;
            _lazyMainWindowVm = lazyMainWindowVm;
            _appearanceModFactory = appearanceModFactory;

            // Initialize internal state from settings
            _hiddenModNames = _settings.HiddenModNames ?? new(StringComparer.OrdinalIgnoreCase); // Use comparer
            _hiddenModsPerNpc = _settings.HiddenModsPerNpc ?? new();
            _settings.NpcGroupAssignments ??= new(); // Ensure group dictionary is initialized

            // Initialize UI elements
            ScrollToNpcInteraction = new Interaction<VM_NpcSelection, Unit>();

            // --- Property Setup ---
            this.WhenAnyValue(x => x.SelectedNpc)
                .Select(selectedNpc => selectedNpc != null
                    ? CreateAppearanceModViewModels(selectedNpc, _mugshotData)
                    : new ObservableCollection<VM_AppearanceMod>())
                .ObserveOn(RxApp.MainThreadScheduler)
                .ToPropertyEx(this, x => x.CurrentNpcAppearanceMods);

            this.WhenAnyValue(x => x.CurrentNpcAppearanceMods)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ToggleModVisibility()) // Also handles initial size refresh via ToggleModVisibility
                .DisposeWith(_disposables);

            _consistencyProvider.NpcSelectionChanged
               .ObserveOn(RxApp.MainThreadScheduler)
               .Subscribe(args => UpdateSelectionState(args.NpcFormKey, args.SelectedMod))
               .DisposeWith(_disposables);

            // --- Search Type -> Visibility Property Setup ---
            this.WhenAnyValue(x => x.SearchType1)
                .Select(type => type == NpcSearchType.SelectionState)
                .ToPropertyEx(this, x => x.IsSelectionStateSearch1);
            this.WhenAnyValue(x => x.SearchType1)
                .Select(type => type == NpcSearchType.Group)
                .ToPropertyEx(this, x => x.IsGroupSearch1);

            this.WhenAnyValue(x => x.SearchType2)
                .Select(type => type == NpcSearchType.SelectionState)
                .ToPropertyEx(this, x => x.IsSelectionStateSearch2);
            this.WhenAnyValue(x => x.SearchType2)
                .Select(type => type == NpcSearchType.Group)
                .ToPropertyEx(this, x => x.IsGroupSearch2);

            this.WhenAnyValue(x => x.SearchType3)
                .Select(type => type == NpcSearchType.SelectionState)
                .ToPropertyEx(this, x => x.IsSelectionStateSearch3);
            this.WhenAnyValue(x => x.SearchType3)
                .Select(type => type == NpcSearchType.Group)
                .ToPropertyEx(this, x => x.IsGroupSearch3);
            // --- End Search Type -> Visibility ---

            // --- Clear irrelevant search inputs when type changes ---
            this.WhenAnyValue(x => x.SearchType1)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(type => {
                    if (type == NpcSearchType.Group || type == NpcSearchType.SelectionState) SearchText1 = string.Empty;
                    if (type != NpcSearchType.Group) SelectedGroupFilter1 = null;
                })
                .DisposeWith(_disposables);
            this.WhenAnyValue(x => x.SearchType2)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(type => {
                    if (type == NpcSearchType.Group || type == NpcSearchType.SelectionState) SearchText2 = string.Empty;
                    if (type != NpcSearchType.Group) SelectedGroupFilter2 = null;
                })
                .DisposeWith(_disposables);
            this.WhenAnyValue(x => x.SearchType3)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(type => {
                    if (type == NpcSearchType.Group || type == NpcSearchType.SelectionState) SearchText3 = string.Empty;
                    if (type != NpcSearchType.Group) SelectedGroupFilter3 = null;
                })
                .DisposeWith(_disposables);
            // --- End Clear Logic ---

            // --- Observe changes in filter groups and logic ---
            var filter1Changes = this.WhenAnyValue(
                x => x.SearchText1, x => x.SearchType1, x => x.SelectedStateFilter1, x => x.SelectedGroupFilter1
            ).Select(_ => Unit.Default);
            var filter2Changes = this.WhenAnyValue(
                x => x.SearchText2, x => x.SearchType2, x => x.SelectedStateFilter2, x => x.SelectedGroupFilter2
            ).Select(_ => Unit.Default);
            var filter3Changes = this.WhenAnyValue(
                x => x.SearchText3, x => x.SearchType3, x => x.SelectedStateFilter3, x => x.SelectedGroupFilter3
            ).Select(_ => Unit.Default);
            var logicChanges = this.WhenAnyValue(
                x => x.IsSearchAndLogic
            ).Select(_ => Unit.Default);

            Observable.Merge(filter1Changes, filter2Changes, filter3Changes, logicChanges)
                .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler) // Debounce filter changes
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ApplyFilter(false)) // Apply filter on UI thread
                .DisposeWith(_disposables);
            // --- End Filter Trigger Setup ---

            // Observe ShowHiddenMods toggle
            this.WhenAnyValue(x => x.ShowHiddenMods)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ToggleModVisibility())
                .DisposeWith(_disposables);

            // --- Description Command Setup ---
            ShowNpcDescriptions = _settings.ShowNpcDescriptions;
            this.WhenAnyValue(x => x.ShowNpcDescriptions)
                .Subscribe(b => _settings.ShowNpcDescriptions = b) // Save setting change
                .DisposeWith(_disposables);

            LoadDescriptionCommand = ReactiveCommand.CreateFromTask<Unit, string?>(
                async (_, ct) =>
                {
                    var npc = SelectedNpc;
                    if (npc != null && ShowNpcDescriptions) // Check the local VM property
                    {
                        try { return await _descriptionProvider.GetDescriptionAsync(npc.NpcFormKey, npc.DisplayName, npc.NpcGetter?.EditorID); }
                        catch (Exception ex) { Debug.WriteLine($"Error executing LoadDescriptionCommand: {ex}"); return null; }
                    } return null;
                },
                this.WhenAnyValue(x => x.SelectedNpc, x => x.ShowNpcDescriptions, (npc, show) => npc != null && show) // Command CanExecute depends on local property
            );
            LoadDescriptionCommand.ObserveOn(RxApp.MainThreadScheduler).BindTo(this, x => x.CurrentNpcDescription).DisposeWith(_disposables);
            LoadDescriptionCommand.IsExecuting.ToPropertyEx(this, x => x.IsLoadingDescription).DisposeWith(_disposables);
            // Trigger description load when selected NPC or ShowNpcDescriptions changes
            this.WhenAnyValue(x => x.SelectedNpc, x => x.ShowNpcDescriptions)
                .Throttle(TimeSpan.FromMilliseconds(200)).Select(_ => Unit.Default)
                .InvokeCommand(LoadDescriptionCommand).DisposeWith(_disposables);
            // --- End Description Command Setup ---

            // --- NPC Group Command Setup ---
            var canExecuteGroupAction = this.WhenAnyValue(
                x => x.SelectedNpc,
                x => x.SelectedGroupName,
                (npc, groupName) => npc != null && !string.IsNullOrWhiteSpace(groupName));

            var canExecuteAllGroupAction = this.WhenAnyValue(
                x => x.FilteredNpcs.Count,
                x => x.SelectedGroupName,
                (count, groupName) => count > 0 && !string.IsNullOrWhiteSpace(groupName));

            AddCurrentNpcToGroupCommand = ReactiveCommand.Create(AddCurrentNpcToGroup, canExecuteGroupAction);
            RemoveCurrentNpcFromGroupCommand = ReactiveCommand.Create(RemoveCurrentNpcFromGroup, canExecuteGroupAction);
            AddAllVisibleNpcsToGroupCommand = ReactiveCommand.Create(AddAllVisibleNpcsToGroup, canExecuteAllGroupAction);
            RemoveAllVisibleNpcsFromGroupCommand = ReactiveCommand.Create(RemoveAllVisibleNpcsFromGroup, canExecuteAllGroupAction);

            // Exception Handling for Commands
            AddCurrentNpcToGroupCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error adding NPC to group: {ex.Message}")).DisposeWith(_disposables);
            RemoveCurrentNpcFromGroupCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error removing NPC from group: {ex.Message}")).DisposeWith(_disposables);
            AddAllVisibleNpcsToGroupCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error adding all visible NPCs to group: {ex.Message}")).DisposeWith(_disposables);
            RemoveAllVisibleNpcsFromGroupCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error removing all visible NPCs from group: {ex.Message}")).DisposeWith(_disposables);
            // --- End NPC Group Command Setup ---

            // Populate available groups initially from settings
            UpdateAvailableNpcGroups();

            // NOTE: Initialize() is called externally (e.g., by VM_Settings after environment validation)
        }

        // --- Methods ---

        public async Task RequestScrollIntoView(VM_NpcSelection npcToScrollTo)
        {
            if (npcToScrollTo == null) return;
            try
            {
                await ScrollToNpcInteraction.Handle(npcToScrollTo);
                Debug.WriteLine($"Requested scroll for {npcToScrollTo.DisplayName}");
            }
            catch (Exception ex)
            {
                // Handle cases where the interaction wasn't handled by the view (e.g., View not ready)
                Debug.WriteLine($"Error invoking ScrollToNpcInteraction: {ex.Message}. Was the handler registered in NpcsView?");
            }
        }

        public bool CanJumpToMod(string appearanceModName)
        {
            var modsVm = _lazyModsVm.Value;
            if (modsVm == null)
            {
                return false;
            }

            // Check if any mod setting in the full list matches the display name
            var targetModSetting = modsVm.AllModSettings.FirstOrDefault(ms => ms.DisplayName.Equals(appearanceModName, StringComparison.OrdinalIgnoreCase));
            return targetModSetting != null;
        }

        public void JumpToMod(VM_AppearanceMod appearanceMod)
        {
            if (appearanceMod == null || string.IsNullOrWhiteSpace(appearanceMod.ModName)) return;

            string targetModName = appearanceMod.ModName;
            Debug.WriteLine($"JumpToMod requested for: {targetModName}");

            var modsVm = _lazyModsVm.Value;
            if (modsVm == null)
            {
                ScrollableMessageBox.ShowError("Mods view model is not available.");
                return;
            }

            var targetModSetting = modsVm.AllModSettings.FirstOrDefault(ms => ms.DisplayName.Equals(targetModName, StringComparison.OrdinalIgnoreCase));

            if (targetModSetting != null)
            {
                Debug.WriteLine($"Found target VM_ModSetting: {targetModSetting.DisplayName}");
                var mainWindowVm = _lazyMainWindowVm.Value;
                if (mainWindowVm == null)
                {
                    ScrollableMessageBox.ShowError("Main window view model is not available.");
                    return;
                }
                mainWindowVm.IsModsTabSelected = true; // Switch to Mods tab

                // Schedule the rest to run after the tab switch might have occurred
                RxApp.MainThreadScheduler.Schedule(() =>
                {
                    // Ensure the target mod is visible in the filtered list if possible
                    if (!modsVm.ModSettingsList.Contains(targetModSetting))
                    {
                        Debug.WriteLine($"Target mod {targetModSetting.DisplayName} not in filtered list. Clearing filters.");
                        modsVm.NameFilterText = string.Empty;
                        modsVm.PluginFilterText = string.Empty;
                        modsVm.NpcSearchText = string.Empty;
                        // ApplyFilters() will be called automatically due to property changes
                    }

                    // Execute the command to show mugshots for the target mod
                    modsVm.ShowMugshotsCommand.Execute(targetModSetting).Subscribe(
                        _ => { Debug.WriteLine($"Successfully triggered ShowMugshots for {targetModSetting.DisplayName}"); },
                        ex => { Debug.WriteLine($"Error executing ShowMugshotsCommand: {ex}"); }
                    ).DisposeWith(_disposables); // Dispose subscription when viewmodel is disposed

                    // TODO: Implement scrolling in ModsView if needed.
                    Debug.WriteLine($"Scrolling to {targetModSetting.DisplayName} in ModsView is not yet implemented.");
                });
            }
            else
            {
                Debug.WriteLine($"Could not find VM_ModSetting with DisplayName: {targetModName}");
                ScrollableMessageBox.ShowWarning($"Could not find the mod '{targetModName}' in the Mods list.", "Mod Not Found");
            }
        }

        private void UpdateSelectionState(FormKey npcFormKey, string selectedMod)
        {
            // Find the VM in either the filtered or the full list
            var npcVM = FilteredNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(npcFormKey))
                     ?? AllNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(npcFormKey));

            if (npcVM != null)
            {
                // Update the selected appearance mod property on the NPC VM
                npcVM.SelectedAppearanceMod = selectedMod;

                // If this NPC is the currently selected one in the UI, update the IsSelected state of its appearance mod VMs
                if (SelectedNpc == npcVM && CurrentNpcAppearanceMods != null)
                {
                    foreach (var modVM in CurrentNpcAppearanceMods)
                    {
                        modVM.IsSelected = modVM.ModName.Equals(selectedMod, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }

        // Regex for parsing mugshot file structure
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
                // Find all files matching the hex pattern within the mugshot directory structure
                var potentialFiles = Directory.EnumerateFiles(_settings.MugshotsFolder, "*.*", SearchOption.AllDirectories)
                                              .Where(f => HexFileRegex.IsMatch(Path.GetFileName(f)));

                foreach (var filePath in potentialFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        string hexFileName = fileInfo.Name;

                        // Traverse up to find plugin and mod directories
                        DirectoryInfo? pluginDir = fileInfo.Directory;
                        if (pluginDir == null || !PluginRegex.IsMatch(pluginDir.Name)) continue; // Directory name must be a plugin
                        string pluginName = pluginDir.Name;

                        DirectoryInfo? modDir = pluginDir.Parent;
                        if (modDir == null || string.IsNullOrWhiteSpace(modDir.Name)) continue; // Mod directory must exist
                        string modName = modDir.Name;

                        // Ensure the structure is ModName/PluginName/HexFile.ext directly under the MugshotsFolder
                        if (modDir.Parent == null || !modDir.Parent.FullName.Equals(expectedParentPath, StringComparison.OrdinalIgnoreCase)) continue;

                        // Extract FormID hex part and construct FormKey string
                        string hexPart = Path.GetFileNameWithoutExtension(hexFileName);
                        if (hexPart.Length != 8) continue; // Ensure correct hex length
                        string formKeyString = $"{hexPart.Substring(hexPart.Length - 6)}:{pluginName}"; // Last 6 digits for FormID

                        // Validate the constructed FormKey string before adding
                        try { FormKey.Factory(formKeyString); }
                        catch { continue; } // Skip if invalid format

                        var mugshotInfo = (ModName: modName, ImagePath: filePath);

                        // Add to results, avoiding duplicate entries for the same NPC/Mod combination
                        if (results.TryGetValue(formKeyString, out var list))
                        {
                            if (!list.Any(i => i.ModName.Equals(modName, StringComparison.OrdinalIgnoreCase)))
                            {
                                list.Add(mugshotInfo);
                            }
                        }
                        else
                        {
                            results[formKeyString] = new List<(string ModName, string ImagePath)> { mugshotInfo };
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log individual file processing errors but continue scanning
                        Debug.WriteLine($"Error processing mugshot file '{filePath}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                 // Log directory access errors
                 Debug.WriteLine($"Error scanning mugshot directory '{_settings.MugshotsFolder}': {ex.Message}");
            }
            System.Diagnostics.Debug.WriteLine($"Mugshot scan complete. Found entries for {results.Count} unique FormKeys.");
            return results;
        }

        public void Initialize()
        {
            var previouslySelectedNpcKey = SelectedNpc?.NpcFormKey;
            SelectedNpc = null; // Deselect NPC
            AllNpcs.Clear();
            FilteredNpcs.Clear();
            CurrentNpcDescription = null; // Clear description

            if (!_environmentStateProvider.EnvironmentIsValid)
            {
                ScrollableMessageBox.ShowWarning($"Environment is not valid. Check settings.\nError: {_environmentStateProvider.EnvironmentBuilderError}", "Environment Error");
                _mugshotData.Clear(); // Clear potentially stale mugshot data
                return;
            }

            // Scan for mugshots and update available groups from settings
            _mugshotData = ScanMugshotDirectory();
            UpdateAvailableNpcGroups(); // Update groups based on loaded settings

            var processedMugshotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Query and filter NPC records from the load order
            var npcRecords = (
                from npc in _environmentStateProvider.LoadOrder.PriorityOrder.WinningOverrides<INpcGetter>()
                let resolvedRace = npc.Race.TryResolve(_environmentStateProvider.LinkCache) // Resolve race once
                where npc.EditorID?.Contains("Preset", StringComparison.OrdinalIgnoreCase) == false &&
                      !npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.IsCharGenFacePreset) &&
                      !npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) &&
                      !npc.Race.IsNull &&
                      resolvedRace is not null && // Check resolved race
                      (resolvedRace.Keywords?.Contains(Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Keyword.ActorTypeNPC) ?? false) // Check keyword on resolved race
                orderby _auxilliary.FormKeyStringToFormIDString(npc.FormKey.ToString()) // Order by FormID
                select npc
            ).ToArray(); // Execute query

            // Create VM_NpcSelection for each valid NPC record
            foreach (var npc in npcRecords)
            {
                try
                {
                    var npcSelector = new VM_NpcSelection(npc, _environmentStateProvider, _consistencyProvider);
                    AllNpcs.Add(npcSelector);
                    string npcFormKeyString = npc.FormKey.ToString();
                    // Mark if this NPC's FormKey was found in mugshots (for later processing)
                    if (_mugshotData.ContainsKey(npcFormKeyString))
                    {
                        processedMugshotKeys.Add(npcFormKeyString);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error initializing VM for NPC {npc.EditorID ?? npc.FormKey.ToString()}: {ex.Message}");
                }
            }

            // Create VM_NpcSelection for NPCs found ONLY in mugshots
            foreach (var kvp in _mugshotData)
            {
                string mugshotFormKeyString = kvp.Key;
                List<(string ModName, string ImagePath)> mugshots = kvp.Value;

                // If this FormKey wasn't processed from game records and has mugshots
                if (!processedMugshotKeys.Contains(mugshotFormKeyString) && mugshots.Any())
                {
                    try
                    {
                        FormKey mugshotFormKey = FormKey.Factory(mugshotFormKeyString);
                        var npcSelector = new VM_NpcSelection(mugshotFormKey, _environmentStateProvider, _consistencyProvider);

                        // If the display name defaults to the FormKey string, it likely means the NPC record is missing
                        if (npcSelector.DisplayName == mugshotFormKeyString)
                        {
                            npcSelector.DisplayName += " (Missing)"; // Indicate missing record
                            string containedIn = string.Join(", ", mugshots.Select(m => m.ModName));
                            npcSelector.NpcName = "Imported from Mugshots: " + containedIn;
                            npcSelector.NpcEditorId = "Not in current Load Order";
                        }
                        AllNpcs.Add(npcSelector);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error creating VM for mugshot-only NPC {mugshotFormKeyString}: {ex.Message}");
                    }
                }
            }

            ApplyFilter(true); // Apply filter to populate FilteredNpcs initially

            // Restore previous selection if possible
            if (previouslySelectedNpcKey != null)
            {
                SelectedNpc = FilteredNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(previouslySelectedNpcKey));
            }
            else if (!FilteredNpcs.Any()) // Explicitly null if no results after filter
            {
                SelectedNpc = null;
            }
        }

        public void ApplyFilter(bool initializing)
        {
            IEnumerable<VM_NpcSelection> results = AllNpcs;
            var predicates = new List<Func<VM_NpcSelection, bool>>();

            // --- Build individual filter predicates ---

            // Filter 1
            if (SearchType1 == NpcSearchType.SelectionState) { predicates.Add(npc => CheckSelectionState(npc, SelectedStateFilter1)); }
            else if (SearchType1 == NpcSearchType.Group) { var p = BuildGroupPredicate(SelectedGroupFilter1); if (p != null) predicates.Add(p); } // Use group predicate
            else if (!string.IsNullOrWhiteSpace(SearchText1)) { var p = BuildTextPredicate(SearchType1, SearchText1); if (p != null) predicates.Add(p); }

            // Filter 2
            if (SearchType2 == NpcSearchType.SelectionState) { predicates.Add(npc => CheckSelectionState(npc, SelectedStateFilter2)); }
            else if (SearchType2 == NpcSearchType.Group) { var p = BuildGroupPredicate(SelectedGroupFilter2); if (p != null) predicates.Add(p); }
            else if (!string.IsNullOrWhiteSpace(SearchText2)) { var p = BuildTextPredicate(SearchType2, SearchText2); if (p != null) predicates.Add(p); }

            // Filter 3
            if (SearchType3 == NpcSearchType.SelectionState) { predicates.Add(npc => CheckSelectionState(npc, SelectedStateFilter3)); }
            else if (SearchType3 == NpcSearchType.Group) { var p = BuildGroupPredicate(SelectedGroupFilter3); if (p != null) predicates.Add(p); }
            else if (!string.IsNullOrWhiteSpace(SearchText3)) { var p = BuildTextPredicate(SearchType3, SearchText3); if (p != null) predicates.Add(p); }
            // --- End building predicates ---


            // Apply combined filter logic
            if (predicates.Any())
            {
                if (IsSearchAndLogic) // AND logic: Must match ALL predicates
                {
                    results = results.Where(npc => predicates.All(p => p(npc)));
                }
                else // OR logic: Must match AT LEAST ONE predicate
                {
                    results = results.Where(npc => predicates.Any(p => p(npc)));
                }
            }

            // Preserve selection if possible
            var previouslySelectedNpcKey = SelectedNpc?.NpcFormKey;

            // Order results and update the observable collection
            var orderedResults = results.OrderBy(x => _auxilliary.FormKeyStringToFormIDString(x.NpcFormKey.ToString())).ToList();

            // Efficiently update ObservableCollection
            FilteredNpcs.Clear();
            foreach (var npc in orderedResults) { FilteredNpcs.Add(npc); }


            // Restore selection or select the first item if list is not empty
            if (previouslySelectedNpcKey != null)
            {
                SelectedNpc = FilteredNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(previouslySelectedNpcKey));
            }
            // Select the first item only if selection was lost AND the list is not empty AND not during initial load
            if (SelectedNpc == null && FilteredNpcs.Any() && !initializing)
            {
                SelectedNpc = FilteredNpcs[0];
            }
            else if (!FilteredNpcs.Any()) // Ensure selection is null if list becomes empty
            {
                SelectedNpc = null;
            }
        }

        private bool CheckSelectionState(VM_NpcSelection npc, SelectionStateFilterType filterState)
        {
            // Check if a specific appearance mod has been chosen via the consistency provider.
            bool isSelected = !string.IsNullOrEmpty(_consistencyProvider.GetSelectedMod(npc.NpcFormKey));
            return filterState == SelectionStateFilterType.Made ? isSelected : !isSelected;
        }

        private Func<VM_NpcSelection, bool>? BuildGroupPredicate(string? selectedGroup)
        {
            if (string.IsNullOrWhiteSpace(selectedGroup)) return null; // No group selected/typed

            // Case-insensitive check against the stored groups for the NPC
            return npc => _settings.NpcGroupAssignments.TryGetValue(npc.NpcFormKey, out var groups) &&
                          groups != null &&
                          groups.Contains(selectedGroup); // HashSet uses its comparer (OrdinalIgnoreCase set in Add method)
        }

        private Func<VM_NpcSelection, bool>? BuildTextPredicate(NpcSearchType type, string searchText)
        {
            // Ignore SelectionState and Group types here, handled by other methods
            if (type == NpcSearchType.SelectionState || type == NpcSearchType.Group || string.IsNullOrWhiteSpace(searchText))
            {
                return null;
            }

            string searchTextLower = searchText.Trim().ToLowerInvariant(); // Optimization for case-insensitive checks

            switch (type)
            {
                case NpcSearchType.Name:
                    return npc => npc.DisplayName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false;
                case NpcSearchType.EditorID:
                    // Null-conditional access for NpcGetter
                    return npc => npc.NpcGetter?.EditorID?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false;
                case NpcSearchType.InAppearanceMod:
                    // Check both plugin-based appearance mods and mugshot-based ones
                    return npc => npc.AppearanceMods.Any(m => m.FileName.String.Contains(searchText, StringComparison.OrdinalIgnoreCase)) ||
                                  (_mugshotData.TryGetValue(npc.NpcFormKey.ToString(), out var mugshots) &&
                                   mugshots.Any(m => m.ModName.Contains(searchText, StringComparison.OrdinalIgnoreCase)));
                case NpcSearchType.FromMod:
                    // Check the filename of the NPC's base record ModKey
                    return npc => npc.NpcFormKey.ModKey.FileName.String.Contains(searchText, StringComparison.OrdinalIgnoreCase);
                case NpcSearchType.FormKey:
                    // Check the full FormKey string representation
                    return npc => npc.NpcFormKey.ToString().Contains(searchText, StringComparison.OrdinalIgnoreCase);
                default:
                    return null; // Should not happen with current enum values
            };
        }

        private ObservableCollection<VM_AppearanceMod> CreateAppearanceModViewModels(VM_NpcSelection npcVM,
            Dictionary<string, List<(string ModName, string ImagePath)>> mugshotData) // mugshotData is the _mugshotData cache
        {
            // Use a dictionary for the final VMs to prevent duplicates by display name
            var finalModVMs = new Dictionary<string, VM_AppearanceMod>(StringComparer.OrdinalIgnoreCase);
            if (npcVM == null) return new ObservableCollection<VM_AppearanceMod>(); // Return empty observable

            string npcFormKeyString = npcVM.NpcFormKey.ToString();

            // --- Step 1: Identify all unique VM_ModSettings that could provide an appearance for this NPC ---
            var relevantModSettings = new HashSet<VM_ModSetting>();

            // 1a: Add ModSettings that have a direct mugshot for this NPC.
            if (mugshotData.TryGetValue(npcFormKeyString, out var npcMugshotListForThisNpc))
            {
                foreach (var mugshotInfo in npcMugshotListForThisNpc) // mugshotInfo.ModName is the DisplayName of a VM_ModSetting
                {
                    var modSettingViaMugshotName = _lazyModsVm.Value?.AllModSettings.FirstOrDefault(ms =>
                        ms.DisplayName.Equals(mugshotInfo.ModName, StringComparison.OrdinalIgnoreCase));

                    if (modSettingViaMugshotName != null)
                    {
                        if (!string.IsNullOrWhiteSpace(modSettingViaMugshotName.MugShotFolderPath))
                        {
                            string expectedMugshotParentDir = Path.GetFileName(modSettingViaMugshotName.MugShotFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                            if (mugshotInfo.ModName.Equals(expectedMugshotParentDir, StringComparison.OrdinalIgnoreCase))
                            {
                                relevantModSettings.Add(modSettingViaMugshotName);
                            }
                        }
                        else { relevantModSettings.Add(modSettingViaMugshotName); }
                    }
                }
            }

            // 1b: Add ModSettings associated with plugins that alter this NPC's appearance.
            if (npcVM.NpcGetter != null)
            {
                foreach (var appearanceModKey in npcVM.AppearanceMods.Distinct())
                {
                    var modSettingsForPlugin = _lazyModsVm.Value?.AllModSettings
                        .Where(ms => ms.CorrespondingModKeys.Contains(appearanceModKey))
                        .ToList();
                    if (modSettingsForPlugin != null) { foreach (var ms in modSettingsForPlugin) relevantModSettings.Add(ms); }
                }
            }

            // 1c: Ensure the ModSetting for the NPC's base plugin is included (if it exists).
            ModKey baseModKey = npcVM.NpcFormKey.ModKey;
            if (!baseModKey.IsNull)
            {
                var modSettingsForBasePlugin = _lazyModsVm.Value?.AllModSettings
                    .Where(ms => ms.CorrespondingModKeys.Contains(baseModKey))
                    .ToList();
                if (modSettingsForBasePlugin != null) { foreach (var ms in modSettingsForBasePlugin) relevantModSettings.Add(ms); }
            }

            // --- Step 2: Create a VM_AppearanceMod for each relevant VM_ModSetting ---
            bool baseKeyHandledByAModSettingVM = false;

            foreach (var modSettingVM in relevantModSettings)
            {
                string displayName = modSettingVM.DisplayName;
                ModKey? specificPluginKey = null;

                if (modSettingVM.NpcSourcePluginMap.TryGetValue(npcVM.NpcFormKey, out var mappedSourceKey)) { specificPluginKey = mappedSourceKey; }
                if ((specificPluginKey == null || specificPluginKey.Value.IsNull) && npcVM.NpcGetter != null)
                {
                    var commonKeys = modSettingVM.CorrespondingModKeys.Intersect(npcVM.AppearanceMods).ToList();
                    if (commonKeys.Any()) { specificPluginKey = commonKeys.FirstOrDefault(); }
                }
                if ((specificPluginKey == null || specificPluginKey.Value.IsNull) && !baseModKey.IsNull && modSettingVM.CorrespondingModKeys.Contains(baseModKey)) { specificPluginKey = baseModKey; }
                if ((specificPluginKey == null || specificPluginKey.Value.IsNull)) { specificPluginKey = modSettingVM.CorrespondingModKeys.FirstOrDefault(); }

                string? imagePath = null;
                if (!string.IsNullOrWhiteSpace(modSettingVM.MugShotFolderPath) && Directory.Exists(modSettingVM.MugShotFolderPath) &&
                    mugshotData.TryGetValue(npcFormKeyString, out var availableMugshotsForNpcViaCache))
                {
                    string mugshotDirNameForThisSetting = Path.GetFileName(modSettingVM.MugShotFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    var specificMugshotInfo = availableMugshotsForNpcViaCache.FirstOrDefault(m => m.ModName.Equals(mugshotDirNameForThisSetting, StringComparison.OrdinalIgnoreCase));
                    if (specificMugshotInfo != default && !string.IsNullOrWhiteSpace(specificMugshotInfo.ImagePath) && File.Exists(specificMugshotInfo.ImagePath))
                    {
                        imagePath = specificMugshotInfo.ImagePath;
                        if (specificPluginKey == null || specificPluginKey.Value.IsNull)
                        {
                            try
                            {
                                FileInfo fi = new FileInfo(imagePath);
                                DirectoryInfo? pluginDirFromFile = fi.Directory;
                                if (pluginDirFromFile != null && PluginRegex.IsMatch(pluginDirFromFile.Name))
                                {
                                    string pluginNameFromPath = pluginDirFromFile.Name;
                                    var inferredKey = modSettingVM.CorrespondingModKeys.FirstOrDefault(mk => mk.FileName.String.Equals(pluginNameFromPath, StringComparison.OrdinalIgnoreCase));
                                    if (inferredKey != null && !inferredKey.IsNull) { specificPluginKey = inferredKey; }
                                    else { try { specificPluginKey = ModKey.FromFileName(pluginNameFromPath); } catch { } }
                                }
                            }
                            catch (Exception ex) { Debug.WriteLine($"Error inferring plugin key from image path '{imagePath}' for modSetting '{displayName}': {ex.Message}"); }
                        }
                    }
                }

                var appearanceVM = _appearanceModFactory(displayName, npcVM.NpcFormKey, specificPluginKey, imagePath);
                finalModVMs[displayName] = appearanceVM;

                if (!baseModKey.IsNull && specificPluginKey != null && specificPluginKey.Value.Equals(baseModKey)) { baseKeyHandledByAModSettingVM = true; }
            }

            // --- Step 3: Create a Placeholder VM_AppearanceMod for the Base Plugin if not already handled ---
            if (!baseModKey.IsNull && !baseKeyHandledByAModSettingVM)
            {
                if (!finalModVMs.ContainsKey(baseModKey.FileName))
                {
                    Debug.WriteLine($"Creating placeholder VM_AppearanceMod for unhandled base plugin: {baseModKey.FileName} for NPC {npcVM.NpcFormKey}");
                    var placeholderBaseVM = _appearanceModFactory(baseModKey.FileName, npcVM.NpcFormKey, baseModKey, null);
                    finalModVMs[baseModKey.FileName] = placeholderBaseVM;
                }
            }

            // --- Step 4: Sort, Apply Hidden State, Apply Selected State ---
            var sortedVMs = finalModVMs.Values.OrderBy(vm => vm.ModName).ToList();

            foreach (var m in sortedVMs)
            {
                bool isGloballyHidden = _hiddenModNames.Contains(m.ModName);
                bool isPerNpcHidden = _hiddenModsPerNpc.TryGetValue(npcVM.NpcFormKey, out var hiddenSet) && hiddenSet.Contains(m.ModName);
                m.IsSetHidden = isGloballyHidden || isPerNpcHidden;
            }

            var selectedModName = _consistencyProvider.GetSelectedMod(npcVM.NpcFormKey);
            if (!string.IsNullOrEmpty(selectedModName))
            {
                var selectedVmInstance = sortedVMs.FirstOrDefault(x => x.ModName.Equals(selectedModName, StringComparison.OrdinalIgnoreCase));
                if (selectedVmInstance != null) { selectedVmInstance.IsSelected = true; }
            }

            return new ObservableCollection<VM_AppearanceMod>(sortedVMs);
        }
        
        // Called by VM_AppearanceMod after a successful drop operation modifies underlying data.
        public void RefreshAppearanceSources()
        {
            Debug.WriteLine("VM_NpcSelectionBar: Refreshing appearance sources after drop...");
            // Re-setting SelectedNpc triggers the reactive chain that rebuilds CurrentNpcAppearanceMods
            // It uses the updated VM_ModSetting data when CreateAppearanceModViewModels runs again.
            var currentNpc = this.SelectedNpc;
            if (currentNpc != null) // Only refresh if an NPC is actually selected
            {
                this.SelectedNpc = null; // Temporarily set to null
                this.SelectedNpc = currentNpc; // Set back to trigger update
            }
        }

        public void HideSelectedMod(VM_AppearanceMod referenceMod)
        {
            if (referenceMod == null) return;
            referenceMod.IsSetHidden = true; // Mark the VM itself as hidden

            if (SelectedNpc != null) // Check if an NPC is actually selected
            {
                // Ensure the dictionary entry exists for this NPC
                if (!_hiddenModsPerNpc.ContainsKey(SelectedNpc.NpcFormKey))
                {
                    _hiddenModsPerNpc[SelectedNpc.NpcFormKey] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                // Add the mod name to the per-NPC hidden set
                _hiddenModsPerNpc[SelectedNpc.NpcFormKey].Add(referenceMod.ModName);
            }
            ToggleModVisibility(); // Update visibility of mods for the current NPC
        }

        public void UnhideSelectedMod(VM_AppearanceMod referenceMod)
        {
             if (referenceMod == null) return;
             referenceMod.IsSetHidden = false; // Mark the VM itself as not hidden (initially)

             // Remove from the per-NPC hidden list if it exists
             if (SelectedNpc != null && _hiddenModsPerNpc.TryGetValue(SelectedNpc.NpcFormKey, out var hiddenSet))
             {
                 if (hiddenSet.Remove(referenceMod.ModName)) // Returns true if removed
                 {
                     // Optional: Clean up dictionary if set becomes empty
                     if (!hiddenSet.Any())
                     {
                          _hiddenModsPerNpc.Remove(SelectedNpc.NpcFormKey);
                     }
                 }
             }
             // Recalculate visibility (it might still be hidden globally)
             ToggleModVisibility();
        }

        public void SelectAllFromMod(VM_AppearanceMod referenceMod)
        {
            if (referenceMod == null || string.IsNullOrWhiteSpace(referenceMod.ModName))
            {
                Debug.WriteLine("SelectAllFromMod: referenceMod or its ModName is null/empty.");
                return;
            }

            string targetModName = referenceMod.ModName;
            int updatedCount = 0;

            Debug.WriteLine($"SelectAllFromMod: Attempting to select '{targetModName}' for all applicable NPCs.");

            // Iterate through the master list of all NPCs
            foreach (var npcVM in AllNpcs)
            {
                if (npcVM == null) continue; // Safety check

                // Check if the target mod is a valid source for this NPC
                if (IsModAnAppearanceSourceForNpc(npcVM, referenceMod))
                {
                    // Set the selected mod using the consistency provider
                    _consistencyProvider.SetSelectedMod(npcVM.NpcFormKey, targetModName);
                    updatedCount++; // Increment count
                }
            }

            Debug.WriteLine($"SelectAllFromMod: Finished processing. Attempted to set '{targetModName}' for {updatedCount} NPCs where it was an available source.");
            // Consider adding user feedback like a status message or MessageBox
        }

        private bool IsModAnAppearanceSourceForNpc(VM_NpcSelection npcVM, VM_AppearanceMod referenceMod)
        {
            if (npcVM == null || referenceMod == null || string.IsNullOrEmpty(referenceMod.ModName)) return false;

            // Check 1: Does the NPC have a mugshot associated with this reference Mod's display name?
            string npcFormKeyString = npcVM.NpcFormKey.ToString();
            if (_mugshotData.TryGetValue(npcFormKeyString, out var mugshots))
            {
                // Check if any mugshot entry for this NPC has a matching ModName (folder name derived from VM_ModSetting)
                if (mugshots.Any(m => m.ModName.Equals(referenceMod.ModName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            // Check 2: Does *any* CorrespondingModKey from the associated ModSetting appear in the NPC's list of appearance-altering plugins?
            if (referenceMod.AssociatedModSetting != null && referenceMod.AssociatedModSetting.CorrespondingModKeys.Any(key => npcVM.AppearanceMods.Contains(key)))
            {
                // This confirms the plugin associated with the reference mod *does* modify this NPC's appearance.
                return true;
                // Potential enhancement: Ensure this ModKey is uniquely tied to this VM_ModSetting.DisplayName
                // if multiple VM_ModSettings could theoretically share a ModKey.
            }

            return false; // Not found as a source via mugshot or plugin check
        }

        public void HideAllFromMod(VM_AppearanceMod referenceMod)
        {
             if (referenceMod == null || string.IsNullOrWhiteSpace(referenceMod.ModName)) return;

             // Add the mod name to the global hidden set. HashSet.Add returns true if it wasn't already present.
             if (_hiddenModNames.Add(referenceMod.ModName))
             {
                 // If added globally, update the IsSetHidden state for the *current* NPC's mods if they are visible
                 if (CurrentNpcAppearanceMods != null)
                 {
                      foreach (var modVM in CurrentNpcAppearanceMods)
                      {
                          if (modVM.ModName.Equals(referenceMod.ModName, StringComparison.OrdinalIgnoreCase))
                          {
                              modVM.IsSetHidden = true; // Ensure the current view reflects the change
                          }
                      }
                 }
             }
             ToggleModVisibility(); // Update visibility for the current NPC based on new hidden state
        }

        public void UnhideAllFromMod(VM_AppearanceMod referenceMod)
        {
            if (referenceMod == null || string.IsNullOrWhiteSpace(referenceMod.ModName)) return;

            // Remove the mod name from the global hidden set. HashSet.Remove returns true if it was present.
             if (_hiddenModNames.Remove(referenceMod.ModName))
             {
                 // If removed globally, update the IsSetHidden state for the *current* NPC's mods if visible
                 if (CurrentNpcAppearanceMods != null)
                 {
                      foreach (var modVM in CurrentNpcAppearanceMods)
                      {
                          if (modVM.ModName.Equals(referenceMod.ModName, StringComparison.OrdinalIgnoreCase))
                          {
                               // Recalculate IsSetHidden based only on per-NPC state now
                               bool isHiddenPerNpc = SelectedNpc != null &&
                                                    _hiddenModsPerNpc.TryGetValue(SelectedNpc.NpcFormKey, out var hiddenSet) &&
                                                    hiddenSet.Contains(modVM.ModName);
                              modVM.IsSetHidden = isHiddenPerNpc;
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
            // Get the per-NPC hidden set for the currently selected NPC (if any)
            var npcSpecificHidden = SelectedNpc != null ? _hiddenModsPerNpc.GetValueOrDefault(SelectedNpc.NpcFormKey) : null;

            foreach (var mod in CurrentNpcAppearanceMods)
            {
                // Determine the definitive hidden state based on global AND specific lists
                bool isGloballyHidden = _hiddenModNames.Contains(mod.ModName);
                bool isSpecificallyHidden = npcSpecificHidden?.Contains(mod.ModName) ?? false;
                bool shouldBeHidden = isGloballyHidden || isSpecificallyHidden;

                mod.IsSetHidden = shouldBeHidden; // Update the source-of-truth hidden state

                // Determine if it should be VISIBLE based on the ShowHiddenMods toggle
                bool shouldBeVisible = ShowHiddenMods || !mod.IsSetHidden;

                // Update IsVisible only if it changes, to avoid unnecessary UI updates
                if (mod.IsVisible != shouldBeVisible)
                {
                     mod.IsVisible = shouldBeVisible;
                     needsRefresh = true; // Visibility actually changed
                }
            }

            // If any mod's visibility changed, signal the View to potentially repack/resize images
            if (needsRefresh)
            {
                _refreshImageSizesSubject.OnNext(Unit.Default);
            }
        }


        // --- NPC Group Methods ---

        private void AddCurrentNpcToGroup()
        {
            if (SelectedNpc == null || string.IsNullOrWhiteSpace(SelectedGroupName)) return;
            var npcKey = SelectedNpc.NpcFormKey;
            var groupName = SelectedGroupName.Trim(); // Use trimmed name

            if (!_settings.NpcGroupAssignments.TryGetValue(npcKey, out var groups))
            {
                // Use case-insensitive comparer for the set
                groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _settings.NpcGroupAssignments[npcKey] = groups;
            }

            if (groups.Add(groupName)) // Add returns true if the item was added (not already present)
            {
                Debug.WriteLine($"Added NPC {npcKey} to group '{groupName}'");
                UpdateAvailableNpcGroups(); // Update dropdown if a new group was effectively created
                ApplyFilter(false); // Re-apply filter in case the group filter is active
            }
            else
            {
                Debug.WriteLine($"NPC {npcKey} already in group '{groupName}'");
            }
        }

        private void RemoveCurrentNpcFromGroup()
        {
            if (SelectedNpc == null || string.IsNullOrWhiteSpace(SelectedGroupName)) return;
            var npcKey = SelectedNpc.NpcFormKey;
            var groupName = SelectedGroupName.Trim(); // Use trimmed name

            if (_settings.NpcGroupAssignments.TryGetValue(npcKey, out var groups))
            {
                if (groups.Remove(groupName)) // Remove returns true if the item was found and removed
                {
                    Debug.WriteLine($"Removed NPC {npcKey} from group '{groupName}'");
                    // Optional: Remove NPC entry from dictionary if no groups left
                    if (!groups.Any())
                    {
                        _settings.NpcGroupAssignments.Remove(npcKey);
                        Debug.WriteLine($"Removed group entry for NPC {npcKey} as it's now empty.");
                    }
                    UpdateAvailableNpcGroups(); // Update dropdown in case this was the last NPC in a group
                    ApplyFilter(false); // Re-apply filter in case the group filter is active
                }
                else
                {
                     Debug.WriteLine($"NPC {npcKey} was not in group '{groupName}'");
                }
            }
             else
             {
                 // The NPC wasn't assigned to any groups
                 Debug.WriteLine($"NPC {npcKey} has no group assignments.");
             }
        }

        private bool AreAnyFiltersActive()
        {
            // Check text filters (excluding state/group types)
            if (SearchType1 != NpcSearchType.SelectionState && SearchType1 != NpcSearchType.Group && !string.IsNullOrWhiteSpace(SearchText1)) return true;
            if (SearchType2 != NpcSearchType.SelectionState && SearchType2 != NpcSearchType.Group && !string.IsNullOrWhiteSpace(SearchText2)) return true;
            if (SearchType3 != NpcSearchType.SelectionState && SearchType3 != NpcSearchType.Group && !string.IsNullOrWhiteSpace(SearchText3)) return true;

            // Check if SelectionState filter is active
            if (SearchType1 == NpcSearchType.SelectionState) return true;
            if (SearchType2 == NpcSearchType.SelectionState) return true;
            if (SearchType3 == NpcSearchType.SelectionState) return true;

            // Check if Group filter is active (requires a selection)
            if (SearchType1 == NpcSearchType.Group && !string.IsNullOrWhiteSpace(SelectedGroupFilter1)) return true;
            if (SearchType2 == NpcSearchType.Group && !string.IsNullOrWhiteSpace(SelectedGroupFilter2)) return true;
            if (SearchType3 == NpcSearchType.Group && !string.IsNullOrWhiteSpace(SelectedGroupFilter3)) return true;

            return false; // No filters active
        }

        private void AddAllVisibleNpcsToGroup()
        {
            if (FilteredNpcs.Count == 0 || string.IsNullOrWhiteSpace(SelectedGroupName)) return;
            var groupName = SelectedGroupName.Trim();
            int count = FilteredNpcs.Count;
            int totalNpcCount = AllNpcs.Count; // Get total count for confirmation message

            // Confirmation Dialog
            if (!AreAnyFiltersActive())
            {
                // No filters are active, meaning FilteredNpcs == AllNpcs
                if (ScrollableMessageBox.Confirm($"No filters are currently applied. Are you sure you want to add ALL {totalNpcCount} NPCs in your game to the group '{groupName}'?",
                        "Confirm Add All NPCs"))
                {
                    Debug.WriteLine("Add All Visible NPCs to Group cancelled by user (no filters active).");
                    return;
                }
            }
            else
            {
                 // Filters are active, confirm adding only the visible ones
                 if (ScrollableMessageBox.Confirm($"Add all {count} currently visible NPCs to the group '{groupName}'?",
                         "Confirm Add Visible NPCs"))
                 {
                     Debug.WriteLine("Add All Visible NPCs to Group cancelled by user.");
                     return;
                 }
            }

            int addedCount = 0;
            bool groupListChanged = false; // Track if a potentially new group name was added anywhere

            // Add each visible NPC to the group
            foreach (var npc in FilteredNpcs)
            {
                if (!_settings.NpcGroupAssignments.TryGetValue(npc.NpcFormKey, out var groups))
                {
                    groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _settings.NpcGroupAssignments[npc.NpcFormKey] = groups;
                }
                if (groups.Add(groupName)) // If the NPC was not already in the group
                {
                    addedCount++;
                    groupListChanged = true; // A group was added to at least one NPC
                }
            }

            // Update the available groups list if a new group might have been created overall
            if (groupListChanged) { UpdateAvailableNpcGroups(); }
            ApplyFilter(false); // Re-apply filter in case the view depends on group membership
            Debug.WriteLine($"Added {addedCount} visible NPCs to group '{groupName}'.");
            ScrollableMessageBox.Show($"Added {addedCount} visible NPCs to group '{groupName}'.", "Operation Complete");
        }

        private void RemoveAllVisibleNpcsFromGroup()
        {
            if (FilteredNpcs.Count == 0 || string.IsNullOrWhiteSpace(SelectedGroupName)) return;
            var groupName = SelectedGroupName.Trim();
            int count = FilteredNpcs.Count;
            int totalNpcCount = AllNpcs.Count;

            // Confirmation Dialog
            if (!AreAnyFiltersActive())
            {
                 if (ScrollableMessageBox.Confirm($"No filters are currently applied. Are you sure you want to attempt removing ALL {totalNpcCount} NPCs in your game from the group '{groupName}'?",
                         "Confirm Remove All NPCs", MessageBoxImage.Warning))
                 {
                     Debug.WriteLine("Remove All Visible NPCs from Group cancelled by user (no filters active).");
                     return;
                 }
            }
            else
            {
                 if (ScrollableMessageBox.Confirm($"Remove all {count} currently visible NPCs from the group '{groupName}'?",
                         "Confirm Remove Visible NPCs"))
                 {
                     Debug.WriteLine("Remove All Visible NPCs from Group cancelled by user.");
                     return;
                 }
            }

            int removedCount = 0;
            bool groupListMayNeedUpdate = false; // Track if a group might have become empty overall

            // Remove each visible NPC from the group
            foreach (var npc in FilteredNpcs)
            {
                if (_settings.NpcGroupAssignments.TryGetValue(npc.NpcFormKey, out var groups))
                {
                    if (groups.Remove(groupName)) // If the NPC was in the group
                    {
                        removedCount++;
                        groupListMayNeedUpdate = true; // A removal happened, group might need update
                        // Optional: Clean up empty sets
                        if (!groups.Any())
                        {
                            _settings.NpcGroupAssignments.Remove(npc.NpcFormKey);
                        }
                    }
                }
            }

            // Update the available groups list if any removals happened
            if (groupListMayNeedUpdate) { UpdateAvailableNpcGroups(); }
            ApplyFilter(false); // Re-apply filter
            Debug.WriteLine($"Removed {removedCount} visible NPCs from group '{groupName}'.");
            ScrollableMessageBox.Show($"Removed {removedCount} visible NPCs from group '{groupName}'.", "Operation Complete");
        }

        private void UpdateAvailableNpcGroups()
        {
            // Use a case-insensitive HashSet to get distinct group names from settings
            var distinctGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_settings.NpcGroupAssignments != null)
            {
                foreach (var groupSet in _settings.NpcGroupAssignments.Values)
                {
                    if (groupSet != null)
                    {
                        foreach (var groupName in groupSet)
                        {
                             if (!string.IsNullOrWhiteSpace(groupName)) // Avoid adding empty/whitespace groups
                             {
                                 distinctGroups.Add(groupName.Trim()); // Store trimmed version
                             }
                        }
                    }
                }
            }

            // Order the distinct groups alphabetically
            var sortedGroups = distinctGroups.OrderBy(g => g).ToList();

            // Efficiently update the ObservableCollection bound to the UI
            // Preserve selection in the main group combo box if possible
            string? currentSelection = SelectedGroupName;
            bool selectionStillExists = false;

            // Use a temporary list for comparison to minimize UI churn
            var tempNewList = new List<string>();

            AvailableNpcGroups.Clear(); // Clear the existing list first
            foreach (var group in sortedGroups)
            {
                AvailableNpcGroups.Add(group);
                if (group.Equals(currentSelection, StringComparison.OrdinalIgnoreCase))
                {
                    selectionStillExists = true;
                }
            }

            // Restore selection if it no longer exists in the list (e.g., last member removed)
            if (!selectionStillExists)
            {
                SelectedGroupName = string.Empty; // Or set to null, depending on desired behavior
            }
            
            MessageBus.Current.SendMessage(new NpcGroupsChangedMessage());  // Send a message indicating groups might have changed

            Debug.WriteLine($"Updated AvailableNpcGroups. Count: {AvailableNpcGroups.Count}");
        }

        // --- End NPC Group Methods ---

        // --- Disposal ---
        public void Dispose()
        {
            _disposables.Dispose(); // Dispose all subscriptions
            ClearAppearanceModViewModels(); // Clean up child VMs
        }

        private void ClearAppearanceModViewModels()
        {
            // Dispose child VMs to prevent memory leaks
            if (CurrentNpcAppearanceMods != null)
            {
                // Create a temporary list to iterate over, as modifying the collection while iterating can cause issues
                var vmsToDispose = CurrentNpcAppearanceMods.ToList();
                CurrentNpcAppearanceMods.Clear(); // Clear the bound collection
                foreach (var vm in vmsToDispose)
                {
                    vm.Dispose();
                }
            }
        }
    }
}