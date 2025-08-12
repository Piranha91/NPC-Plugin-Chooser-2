// View Models/VM_Mods.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq; // Added for Throttle, ObserveOn
using System.Reactive.Subjects; // Added for Subject
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks; // Added for Task
using System.Windows;
using System.Windows.Media;
using Microsoft.Build.Experimental.BuildCheck;
using Mutagen.Bethesda.Archives; // For MessageBox
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat; // For Locator


namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_Mods : ReactiveObject
    {
        private readonly Settings _settings;
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly VM_NpcSelectionBar _npcSelectionBar; // To access AllNpcs and navigate
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly Lazy<VM_MainWindow> _lazyMainWindowVm; // *** NEW: To switch tabs ***
        private readonly Auxilliary _aux;
        private readonly PluginProvider _pluginProvider;
        private readonly BsaHandler _bsaHandler;
        private readonly ConcurrentDictionary<(string pluginSourcePath, ModKey modKey), bool> _overridesCache = new();

        private readonly CompositeDisposable _disposables = new();

        public const string BaseGameModSettingName = "Base Game";
        public const string CreationClubModsettingName = "Creation Club";

        // Subject and Observable for scroll requests
        private readonly BehaviorSubject<VM_ModSetting?> _requestScrollToModSubject =
            new BehaviorSubject<VM_ModSetting?>(null);

        public IObservable<VM_ModSetting?> RequestScrollToModObservable => _requestScrollToModSubject.AsObservable();

        // --- Filtering Properties (Left Panel) ---
        [Reactive] public string NameFilterText { get; set; } = string.Empty;
        [Reactive] public string PluginFilterText { get; set; } = string.Empty;
        [Reactive] public ModNpcSearchType SelectedNpcSearchType { get; set; } = ModNpcSearchType.Name;
        [Reactive] public string NpcSearchText { get; set; } = string.Empty;
        public Array AvailableNpcSearchTypes => Enum.GetValues(typeof(ModNpcSearchType));
        [Reactive] public bool IsLoadingNpcData { get; private set; }

        // --- Data Lists (Left Panel) ---
        private List<VM_ModSetting> _allModSettingsInternal = new();
        public IReadOnlyList<VM_ModSetting> AllModSettings => _allModSettingsInternal; // Public access
        public ObservableCollection<VM_ModSetting> ModSettingsList { get; } = new();

        // --- Right Panel Properties ---
        [Reactive] public VM_ModSetting? SelectedModForMugshots { get; private set; }
        public ObservableCollection<VM_ModsMenuMugshot> CurrentModNpcMugshots { get; } = new();

        [Reactive] public bool IsLoadingMugshots { get; private set; }

        // This property will be set to true by the View (ModsView.xaml.cs) when the user
        // directly interacts with zoom (Ctrl+Scroll, +/- buttons).
        [Reactive] public bool ModsViewHasUserManuallyZoomed { get; set; } = false;

        // Subject for triggering right panel image refresh
        private readonly Subject<Unit> _refreshMugshotSizesSubject = new Subject<Unit>();
        public IObservable<Unit> RefreshMugshotSizesObservable => _refreshMugshotSizesSubject.AsObservable();

        // --- NEW: Zoom Control Properties & Commands for ModsView ---
        [Reactive] public double ModsViewZoomLevel { get; set; }
        [Reactive] public bool ModsViewIsZoomLocked { get; set; }
        public ReactiveCommand<Unit, Unit> ZoomInModsCommand { get; }
        public ReactiveCommand<Unit, Unit> ZoomOutModsCommand { get; }
        public ReactiveCommand<Unit, Unit> ResetZoomModsCommand { get; }
        private const double _minZoomPercentage = 1.0;
        private const double _maxZoomPercentage = 1000.0;
        private const double _zoomStepPercentage = 2.5; // For +/- buttons and scroll wheel
        
        // --- New: Other Display Controls
        public bool NormalizeImageDimensions => _settings.NormalizeImageDimensions;

        // --- NEW: Source Plugin Disambiguation (Right Panel - Above Mugshots, below Mod Name) ---
        [Reactive] public ModKey? SelectedSourcePluginForDisambiguation { get; set; }
        [ObservableAsProperty] public bool ShowSourcePluginControls { get; }
        [ObservableAsProperty] public ObservableCollection<ModKey> AvailableSourcePluginsForSelectedMod { get; }
        public ReactiveCommand<Unit, Unit> SetGlobalSourcePluginCommand { get; }

        // --- Placeholder Image Configuration --- 
        private const string PlaceholderResourceRelativePath = @"Resources\No Mugshot.png";

        private static readonly string FullPlaceholderPath =
            Path.Combine(AppContext.BaseDirectory, PlaceholderResourceRelativePath);

        private static readonly bool PlaceholderExists = File.Exists(FullPlaceholderPath);

        // --- Commands ---
        public ReactiveCommand<VM_ModSetting, Unit> ShowMugshotsCommand { get; }

        // Expose for binding in VM_ModSetting commands
        public string ModsFolderSetting => _settings.ModsFolder;
        public string MugshotsFolderSetting => _settings.MugshotsFolder; // Needed for BrowseMugshotFolder
        public SkyrimRelease SkyrimRelease => _settings.SkyrimRelease;
        public EnvironmentStateProvider EnvironmentStateProvider => _environmentStateProvider;

        // Concurrency management
        private bool _isPopulatingModSettings = false;

        // Factory fields
        private readonly VM_ModSetting.FromModelFactory _modSettingFromModelFactory;
        private readonly VM_ModSetting.FromMugshotPathFactory _modSettingFromMugshotPathFactory;
        private readonly VM_ModSetting.FromModFolderFactory _modSettingFromModFolderFactory;

        // *** Updated Constructor Signature ***
        public VM_Mods(Settings settings, EnvironmentStateProvider environmentStateProvider,
            VM_NpcSelectionBar npcSelectionBar, NpcConsistencyProvider consistencyProvider,
            Lazy<VM_MainWindow> lazyMainWindowVm, Auxilliary aux, PluginProvider pluginProvider,
            BsaHandler bsaHandler,
            VM_ModSetting.FromModelFactory modSettingFromModelFactory,
            VM_ModSetting.FromMugshotPathFactory modSettingFromMugshotPathFactory,
            VM_ModSetting.FromModFolderFactory modSettingFromModFolderFactory)
        {
            _settings = settings;
            _environmentStateProvider = environmentStateProvider;
            _npcSelectionBar = npcSelectionBar;
            _consistencyProvider = consistencyProvider;
            _lazyMainWindowVm = lazyMainWindowVm;
            _aux = aux;
            _pluginProvider = pluginProvider;
            _bsaHandler = bsaHandler;
            _modSettingFromModelFactory = modSettingFromModelFactory;
            _modSettingFromMugshotPathFactory = modSettingFromMugshotPathFactory;
            _modSettingFromModFolderFactory = modSettingFromModFolderFactory;

            ShowMugshotsCommand = ReactiveCommand.CreateFromTask<VM_ModSetting>(ShowMugshotsAsync);
            ShowMugshotsCommand.ThrownExceptions.Subscribe(ex =>
            {
                ScrollableMessageBox.ShowError($"Error loading mugshots: {ex.Message}");
                IsLoadingMugshots = false;
            }).DisposeWith(_disposables);


            // --- NEW: Initialize Zoom Settings from _settings ---
            ModsViewZoomLevel =
                Math.Max(_minZoomPercentage,
                    Math.Min(_maxZoomPercentage, _settings.ModsViewZoomLevel)); // Clamp initial load
            ModsViewIsZoomLocked = _settings.ModsViewIsZoomLocked;
            Debug.WriteLine(
                $"VM_Mods.Constructor: Initial ZoomLevel: {ModsViewZoomLevel:F2}, IsZoomLocked: {ModsViewIsZoomLocked}");

            // --- NEW: Zoom Commands ---
            ZoomInModsCommand = ReactiveCommand.Create(() =>
            {
                Debug.WriteLine("VM_Mods: ZoomInModsCommand executed.");
                ModsViewHasUserManuallyZoomed = true;
                ModsViewZoomLevel = Math.Min(_maxZoomPercentage, ModsViewZoomLevel + _zoomStepPercentage);
            });
            ZoomOutModsCommand = ReactiveCommand.Create(() =>
            {
                Debug.WriteLine("VM_Mods: ZoomOutModsCommand executed.");
                ModsViewHasUserManuallyZoomed = true;
                ModsViewZoomLevel = Math.Max(_minZoomPercentage, ModsViewZoomLevel - _zoomStepPercentage);
            });
            ResetZoomModsCommand = ReactiveCommand.Create(() =>
            {
                Debug.WriteLine("VM_Mods: ResetZoomModsCommand executed.");
                ModsViewIsZoomLocked = false;
                ModsViewHasUserManuallyZoomed = false; // This allows packer to take over
                // The key is that the VIEW needs to be told to re-evaluate its layout
                // BEFORE the packer uses the ScrollViewer's dimensions.
                // So, just signaling the subject might not be enough if the view's layout
                // isn't guaranteed to be updated first.
                // This subject will trigger RefreshMugshotImageSizes in the view.
                _refreshMugshotSizesSubject.OnNext(Unit.Default);
            });
            // ... (exception handlers for commands) ...
            ZoomInModsCommand.ThrownExceptions
                .Subscribe(ex => Debug.WriteLine($"Error ZoomInModsCommand: {ex.Message}")).DisposeWith(_disposables);
            ZoomOutModsCommand.ThrownExceptions
                .Subscribe(ex => Debug.WriteLine($"Error ZoomOutModsCommand: {ex.Message}")).DisposeWith(_disposables);
            ResetZoomModsCommand.ThrownExceptions
                .Subscribe(ex => Debug.WriteLine($"Error ResetZoomModsCommand: {ex.Message}"))
                .DisposeWith(_disposables);

            // --- Source Plugin Disambiguation Logic ---
            this.WhenAnyValue(x => x.SelectedModForMugshots)
                .Select(mod => mod != null && mod.CorrespondingModKeys.Count > 1 && mod.AmbiguousNpcFormKeys.Any())
                .ToPropertyEx(this, x => x.ShowSourcePluginControls)
                .DisposeWith(_disposables);

            this.WhenAnyValue(x => x.SelectedModForMugshots)
                .Select(mod => mod != null && mod.CorrespondingModKeys.Count > 1
                    ? new ObservableCollection<ModKey>(mod.CorrespondingModKeys.OrderBy(mk => mk.FileName.String))
                    : new ObservableCollection<ModKey>())
                .ToPropertyEx(this, x => x.AvailableSourcePluginsForSelectedMod)
                .DisposeWith(_disposables);

            // When SelectedModForMugshots changes, reset the SelectedSourcePluginForDisambiguation
            this.WhenAnyValue(x => x.SelectedModForMugshots)
                .Subscribe(_ => SelectedSourcePluginForDisambiguation = null) // Reset dropdown selection
                .DisposeWith(_disposables);

            // Command for setting the global source plugin
            var canSetGlobalSource = this.WhenAnyValue(
                x => x.SelectedModForMugshots,
                x => x.SelectedSourcePluginForDisambiguation,
                (mod, selectedPlugin) => mod != null && selectedPlugin.HasValue && !selectedPlugin.Value.IsNull &&
                                         mod.CorrespondingModKeys.Count > 1);

            SetGlobalSourcePluginCommand = ReactiveCommand.Create(() =>
            {
                if (SelectedModForMugshots != null && SelectedSourcePluginForDisambiguation.HasValue &&
                    !SelectedSourcePluginForDisambiguation.Value.IsNull)
                {
                    // SetSourcePluginForAllApplicableNpcs now returns a list of changed FormKeys
                    List<FormKey> changedKeys =
                        SelectedModForMugshots.SetSourcePluginForAllApplicableNpcs(SelectedSourcePluginForDisambiguation
                            .Value);

                    if (changedKeys.Any())
                    {
                        // Manually update the CurrentSourcePlugin for displayed mugshots
                        // This ensures their context menu checkmarks are correct without a full panel reload.
                        foreach (var mugshotVM in CurrentModNpcMugshots)
                        {
                            if (changedKeys.Contains(mugshotVM.NpcFormKey))
                            {
                                mugshotVM.CurrentSourcePlugin = SelectedSourcePluginForDisambiguation;
                            }
                        }

                        Debug.WriteLine(
                            $"VM_Mods: Updated CurrentSourcePlugin for {changedKeys.Count} displayed mugshots after global source set.");
                    }
                }
            }, canSetGlobalSource); // canSetGlobalSource is the WhenAnyValue observable

            SetGlobalSourcePluginCommand.ThrownExceptions.Subscribe(ex =>
            {
                ScrollableMessageBox.ShowError($"Error setting global source plugin: {ex.Message}");
            }).DisposeWith(_disposables);
            // --- END: Source Plugin Disambiguation Logic ---

            // --- Setup Filter Reaction ---
            this.WhenAnyValue(x => x.NameFilterText, x => x.PluginFilterText, x => x.NpcSearchText,
                    x => x.SelectedNpcSearchType) // Added NpcSearchType
                .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ApplyFilters())
                .DisposeWith(_disposables);

            this.WhenAnyValue(x => x.NameFilterText, x => x.PluginFilterText, x => x.NpcSearchText,
                    x => x.SelectedNpcSearchType)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    if (SelectedModForMugshots != null && !ModSettingsList.Contains(SelectedModForMugshots))
                    {
                        SelectedModForMugshots = null;
                        CurrentModNpcMugshots.Clear();
                    }
                })
                .DisposeWith(_disposables);

            // --- NEW: Persist Zoom Settings and Trigger Refresh ---
            this.WhenAnyValue(x => x.ModsViewZoomLevel)
                .Skip(1)
                .Throttle(TimeSpan.FromMilliseconds(100))
                .Subscribe(zoom =>
                {
                    bool isFromPackerUpdate = !ModsViewIsZoomLocked && !ModsViewHasUserManuallyZoomed;
                    Debug.WriteLine(
                        $"VM_Mods: ModsViewZoomLevel RAW input {zoom:F2}. IsFromPacker: {isFromPackerUpdate}, IsLocked: {ModsViewIsZoomLocked}, ManualZoom: {ModsViewHasUserManuallyZoomed}");

                    double previousVmZoomLevel = _settings.ModsViewZoomLevel;
                    double newClampedZoom = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, zoom));

                    if (Math.Abs(_settings.ModsViewZoomLevel - newClampedZoom) > 0.001)
                    {
                        _settings.ModsViewZoomLevel = newClampedZoom;
                        Debug.WriteLine($"VM_Mods: Settings.ModsViewZoomLevel updated to {newClampedZoom:F2}.");
                    }

                    if (Math.Abs(newClampedZoom - zoom) > 0.001)
                    {
                        Debug.WriteLine(
                            $"VM_Mods: ZoomLevel IS being clamped from {zoom:F2} to {newClampedZoom:F2}. Updating property.");
                        ModsViewZoomLevel = newClampedZoom;
                        return;
                    }

                    if (ModsViewIsZoomLocked || ModsViewHasUserManuallyZoomed)
                    {
                        Debug.WriteLine(
                            $"VM_Mods: ZoomLevel processed. IsLocked or ManualZoom. Triggering refresh. Value: {newClampedZoom:F2}");
                        _refreshMugshotSizesSubject.OnNext(Unit.Default);
                    }
                    else
                    {
                        Debug.WriteLine(
                            $"VM_Mods: ZoomLevel processed. Unlocked & not manual. No VM-initiated refresh. Value: {newClampedZoom:F2}");
                    }
                })
                .DisposeWith(_disposables);

            this.WhenAnyValue(x => x.ModsViewIsZoomLocked)
                .Skip(1)
                .Subscribe(isLocked =>
                {
                    Debug.WriteLine($"VM_Mods: ModsViewIsZoomLocked changed to {isLocked}.");
                    _settings.ModsViewIsZoomLocked = isLocked;
                    ModsViewHasUserManuallyZoomed = false;
                    Debug.WriteLine("VM_Mods: ModsViewIsZoomLocked changed - Triggering _refreshMugshotSizesSubject.");
                    _refreshMugshotSizesSubject.OnNext(Unit.Default);
                })
                .DisposeWith(_disposables);

            // MODIFIED: When SelectedModForMugshots changes, reset manual zoom state if not locked.
            this.WhenAnyValue(x => x.SelectedModForMugshots)
                .Skip(1)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(selectedMod =>
                {
                    Debug.WriteLine(
                        $"VM_Mods: SelectedModForMugshots changed to {selectedMod?.DisplayName ?? "null"}.");
                    if (!ModsViewIsZoomLocked)
                    {
                        Debug.WriteLine(
                            "VM_Mods: SelectedModForMugshots changed - Zoom not locked, setting ModsViewHasUserManuallyZoomed = false");
                        ModsViewHasUserManuallyZoomed = false;
                    }
                    // ShowMugshotsAsync (called when this property changes, typically via command) will trigger _refreshMugshotSizesSubject
                })
                .DisposeWith(_disposables);

            // When SelectedModForMugshots changes (which happens when ShowMugshotsCommand is executed),
            // signal a scroll request for this newly selected mod.
            this.WhenAnyValue(x => x.SelectedModForMugshots)
                .Skip(1) // Skip initial null or first value
                .ObserveOn(RxApp
                    .MainThreadScheduler) // Ensure subject is updated on UI thread if needed, though OnNext is thread-safe
                .Subscribe(modToScrollTo =>
                {
                    if (modToScrollTo != null)
                    {
                        Debug.WriteLine(
                            $"VM_Mods: SelectedModForMugshots changed to {modToScrollTo.DisplayName}. Signaling scroll.");
                        _requestScrollToModSubject.OnNext(modToScrollTo);
                    }
                    else
                    {
                        _requestScrollToModSubject.OnNext(null); // Clear scroll request if selection is cleared
                    }

                    // Reset manual zoom flag if not locked (existing logic)
                    if (!ModsViewIsZoomLocked)
                    {
                        ModsViewHasUserManuallyZoomed = false;
                    }
                })
                .DisposeWith(_disposables);

            ApplyFilters(); // Apply initial filter
        }

        /// <summary>
        /// Adds a new VM_ModSetting (typically created by Unlink operation) to the internal list
        /// and refreshes dependent UI.
        /// </summary>
        public void AddAndRefreshModSetting(VM_ModSetting newVm)
        {
            if (newVm == null || _allModSettingsInternal.Any(vm =>
                    vm.DisplayName.Equals(newVm.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                Debug.WriteLine(
                    $"VM_Mods: Not adding VM '{newVm?.DisplayName}' either because it's null or a VM with that DisplayName already exists.");
                // Optionally, if it exists, consider merging properties, but for unlink, it should be a new entry.
                return;
            }

            _allModSettingsInternal.Add(newVm);
            // Re-sort the internal list by DisplayName
            SortVMsInPlace();

            // Recalculate its mugshot validity (it might be a new mugshot-only entry)
            RecalculateMugshotValidity(newVm);

            // Refresh the filtered list in the UI
            ApplyFilters();

            // Asynchronously refresh its NPC lists if it might have mod data (though unlink usually makes it mugshot-only)
            // For a new mugshot-only entry, RefreshNpcLists won't find much, but it's harmless.
            
            (var allFaceGenLooseFiles, var allFaceGenBsaFiles) =
                CacheFaceGenPathsOnLoad(); 
            
            var plugins = _pluginProvider.LoadPlugins(newVm.CorrespondingModKeys, newVm.CorrespondingFolderPaths.ToHashSet());
            Task.Run(() => newVm.RefreshNpcLists(allFaceGenLooseFiles, allFaceGenBsaFiles, plugins));
            _pluginProvider.UnloadPlugins(newVm.CorrespondingModKeys);
        }

        /// <summary>
        /// Sorts an input mod settings list alphabetically (except for base game and CC content)
        /// </summary>
        public List<VM_ModSetting> SortVMs(IEnumerable<VM_ModSetting> inputs)
        {
            var sorted = inputs
                .OrderBy(vm => vm.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            var baseGameSetting = inputs.FirstOrDefault(x => x.DisplayName == BaseGameModSettingName);
            var creationClubSetting =
                inputs.FirstOrDefault(x => x.DisplayName == CreationClubModsettingName);

            if (creationClubSetting != null)
            {
                sorted.Remove(creationClubSetting);
                sorted.Insert(0, creationClubSetting);
            }

            if (baseGameSetting != null)
            {
                sorted.Remove(baseGameSetting);
                sorted.Insert(0, baseGameSetting);
            }

            return sorted;
        }

        /// <summary>
        /// Sorts the main mod settings list alphabetically (except for base game and CC content) in-place
        /// </summary>
        public void SortVMsInPlace()
        {
            var sorted = SortVMs(_allModSettingsInternal);
            _allModSettingsInternal.Clear();
            _allModSettingsInternal.AddRange(sorted);
        }

        /// <summary>
        /// Requests the VM_NpcSelectionBar to refresh its current NPC's appearance sources.
        /// </summary>
        public void RequestNpcSelectionBarRefresh()
        {
            // This relies on VM_NpcSelectionBar being accessible, e.g., if injected or via a message bus.
            // Assuming _npcSelectionBar is the injected instance.
            _npcSelectionBar?.RefreshAppearanceSources();
        }

        // View Models/VM_Mods.cs

        private async Task ShowMugshotsAsync(VM_ModSetting selectedModSetting)
        {
            if (selectedModSetting == null)
            {
                SelectedModForMugshots = null;
                CurrentModNpcMugshots.ForEach(vm => vm.Dispose());
                CurrentModNpcMugshots.Clear();
                return;
            }

            IsLoadingMugshots = true;
            SelectedModForMugshots = selectedModSetting;
            CurrentModNpcMugshots.ForEach(vm => vm.Dispose());
            CurrentModNpcMugshots.Clear();

            if (!ModsViewIsZoomLocked)
            {
                ModsViewHasUserManuallyZoomed = false;
            }

            // Create a temporary list to hold data for VM creation. This is thread-safe.
            var mugshotCreationData =
                new List<(string ImagePath, FormKey NpcFormKey, string NpcDisplayName, bool IsAmbiguous, List<ModKey>
                    AvailablePlugins, ModKey? CurrentSource)>();

            try
            {
                // Gather data on a background thread without creating view models
                if (selectedModSetting.HasValidMugshots &&
                    !string.IsNullOrWhiteSpace(selectedModSetting.MugShotFolderPath) &&
                    Directory.Exists(selectedModSetting.MugShotFolderPath))
                {
                    await Task.Run(() =>
                    {
                        var imageFiles = Directory.EnumerateFiles(selectedModSetting.MugShotFolderPath, "*.*",
                                SearchOption.AllDirectories)
                            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^[0-9A-F]{8}\.(png|jpg|jpeg|bmp)$",
                                RegexOptions.IgnoreCase))
                            .ToList();

                        foreach (var imagePath in imageFiles)
                        {
                            string fileName = Path.GetFileName(imagePath);
                            string hexPart = Path.GetFileNameWithoutExtension(fileName);
                            DirectoryInfo? pluginDir = new FileInfo(imagePath).Directory;

                            if (pluginDir != null && Regex.IsMatch(pluginDir.Name, @"^.+\.(esm|esp|esl)$",
                                    RegexOptions.IgnoreCase))
                            {
                                string pluginName = pluginDir.Name;
                                string formIdHex = hexPart.Substring(Math.Max(0, hexPart.Length - 6));
                                string formKeyString = $"{formIdHex}:{pluginName}";

                                try
                                {
                                    FormKey npcFormKey = FormKey.Factory(formKeyString);
                                    string npcDisplayName;

                                    if (selectedModSetting.NpcFormKeysToDisplayName.TryGetValue(npcFormKey,
                                            out var knownNpcName))
                                    {
                                        npcDisplayName = knownNpcName;
                                    }
                                    else if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey,
                                                 out var npcGetter))
                                    {
                                        npcDisplayName = npcGetter.Name?.String ??
                                                         npcGetter.EditorID ?? npcFormKey.ToString();
                                    }
                                    else
                                    {
                                        npcDisplayName = "Unknown NPC (" + formKeyString + ")";
                                    }

                                    bool isAmbiguous = selectedModSetting.AmbiguousNpcFormKeys.Contains(npcFormKey);

                                    List<ModKey> availableModKeys = new();
                                    ModKey? currentSource = null;

                                    if (selectedModSetting.AvailablePluginsForNpcs.TryGetValue(npcFormKey,
                                            out List<ModKey>? availableModKeysForNpc))
                                    {
                                        availableModKeys = availableModKeysForNpc;
                                        if (selectedModSetting.NpcPluginDisambiguation.ContainsKey(npcFormKey))
                                        {
                                            currentSource = selectedModSetting.NpcPluginDisambiguation[npcFormKey];
                                        }
                                        else
                                        {
                                            currentSource = availableModKeys.FirstOrDefault();
                                        }
                                    }

                                    // Add the data to the temporary list instead of creating the VM here
                                    mugshotCreationData.Add((imagePath, npcFormKey, npcDisplayName, isAmbiguous,
                                        availableModKeys, currentSource));
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine(
                                        $"Error creating FormKey/VM for real mugshot {imagePath}: {ex.Message}");
                                }
                            }
                        }
                    });
                }

                if (!mugshotCreationData.Any() && PlaceholderExists)
                {
                    if (selectedModSetting.NpcFormKeysToDisplayName.Any())
                    {
                        Debug.WriteLine(
                            $"ShowMugshotsAsync: Loading placeholders for {selectedModSetting.DisplayName} ({selectedModSetting.NpcFormKeysToDisplayName.Count} known NPCs).");
                        await Task.Run(() =>
                        {
                            foreach (var npcEntry in selectedModSetting.NpcFormKeysToDisplayName)
                            {
                                FormKey npcFormKey = npcEntry.Key;
                                string npcDisplayName = npcEntry.Value;

                                bool isAmbiguous = selectedModSetting.AmbiguousNpcFormKeys.Contains(npcFormKey);

                                List<ModKey> availableModKeys = new();
                                ModKey? currentSource = null;

                                if (selectedModSetting.AvailablePluginsForNpcs.TryGetValue(npcFormKey,
                                        out List<ModKey>? availableModKeysForNpc))
                                {
                                    availableModKeys = availableModKeysForNpc;
                                    if (selectedModSetting.NpcPluginDisambiguation.ContainsKey(npcFormKey))
                                    {
                                        currentSource = selectedModSetting.NpcPluginDisambiguation[npcFormKey];
                                    }
                                    else
                                    {
                                        currentSource = availableModKeys.FirstOrDefault();
                                    }
                                }

                                // Add placeholder data to the temporary list
                                mugshotCreationData.Add((FullPlaceholderPath, npcFormKey, npcDisplayName, isAmbiguous,
                                    availableModKeys, currentSource));
                            }
                        });
                    }
                    else
                    {
                        Debug.WriteLine(
                            $"ShowMugshotsAsync: No real mugshots for {selectedModSetting.DisplayName}, and no NPCs known to this mod setting to show placeholders for.");
                    }
                }

                // Now, back on the UI thread, create the VMs from the collected data.
                var mugshotVMs = mugshotCreationData
                    .Select(data => new VM_ModsMenuMugshot(
                        data.ImagePath,
                        data.NpcFormKey,
                        data.NpcDisplayName,
                        this,
                        data.IsAmbiguous,
                        data.AvailablePlugins,
                        data.CurrentSource,
                        selectedModSetting,
                        _consistencyProvider))
                    .OrderBy(vm => vm.NpcDisplayName)
                    .ToList();

                CurrentModNpcMugshots.ForEach(vm => vm.Dispose());
                CurrentModNpcMugshots.Clear();
                foreach (var vm in mugshotVMs)
                    CurrentModNpcMugshots.Add(vm);
            }
            catch (Exception ex)
            {
                ScrollableMessageBox.ShowWarning(
                    $"Failed to load mugshot data for {selectedModSetting.DisplayName}:\n{ex.Message}",
                    "Mugshot Load Error");
                CurrentModNpcMugshots.ForEach(vm => vm.Dispose());
                CurrentModNpcMugshots.Clear();
            }
            finally
            {
                IsLoadingMugshots = false;
                if (CurrentModNpcMugshots.Any())
                    _refreshMugshotSizesSubject.OnNext(Unit.Default);
            }
        }

        // --- NEW or MODIFIED IF NEEDED: Dispose method for cleaning up subscriptions ---
        public void Dispose() // If VM_Mods needs to be disposable
        {
            _disposables.Dispose();
            CurrentModNpcMugshots.ForEach(vm => vm.Dispose());
            CurrentModNpcMugshots.Clear();
            _requestScrollToModSubject.Dispose(); // Dispose the subject
        }

        // *** NEW: Method to handle navigation triggered by VM_ModsMenuMugshot ***
        // In VM_Mods.cs
        public void NavigateToNpc(FormKey npcFormKey)
        {
            _lazyMainWindowVm.Value.IsNpcsTabSelected = true;

            // Use a slightly longer initial delay to ensure tab switch UI operations can start
            RxApp.MainThreadScheduler.Schedule(TimeSpan.FromMilliseconds(100), () =>
            {
                var npcToSelect = _npcSelectionBar.AllNpcs.FirstOrDefault(npc => npc.NpcFormKey == npcFormKey);
                if (npcToSelect != null)
                {
                    Debug.WriteLine(
                        $"VM_Mods.NavigateToNpc: Found NPC {npcToSelect.DisplayName}. Initiating navigation sequence.");
                    _npcSelectionBar.IsProgrammaticNavigationInProgress = true; // Set flag BEFORE clearing filters

                    // Clear filters. This will reactively trigger _npcSelectionBar.ApplyFilter.
                    // ApplyFilter will see IsProgrammaticNavigationInProgress = true and will NOT auto-select.
                    _npcSelectionBar.SearchText1 = "";
                    _npcSelectionBar.SearchText2 = "";
                    _npcSelectionBar.SearchText3 = "";

                    // Schedule the explicit selection and scroll signal to occur *after*
                    // the filter clearing has triggered ApplyFilter and ApplyFilter has updated FilteredNpcs.
                    // The WhenAnyValue for filters in VM_NpcSelectionBar is throttled by 300ms.
                    // So, we schedule this for slightly after that throttle period.
                    RxApp.MainThreadScheduler.Schedule(TimeSpan.FromMilliseconds(350), () =>
                    {
                        Debug.WriteLine(
                            $"VM_Mods.NavigateToNpc: Attempting to explicitly select {npcToSelect.DisplayName}.");

                        // It's possible FilteredNpcs doesn't contain npcToSelect if ApplyFilter somehow
                        // didn't include it (e.g., if ApplyFilter was triggered by something else very quickly).
                        // A safeguard: if target not in list, ApplyFilter again (though this should be rare with blank filters).
                        if (!_npcSelectionBar.FilteredNpcs.Contains(npcToSelect))
                        {
                            Debug.WriteLine(
                                $"VM_Mods.NavigateToNpc: Target {npcToSelect.DisplayName} not in FilteredNpcs. Re-applying filter.");
                            // This ApplyFilter will also see IsProgrammaticNavigationInProgress = true.
                            _npcSelectionBar.ApplyFilter(false);
                            // Give this ApplyFilter a moment if it was needed.
                            RxApp.MainThreadScheduler.Schedule(TimeSpan.FromMilliseconds(50), SelectAndSignal);
                        }
                        else
                        {
                            SelectAndSignal();
                        }

                        void SelectAndSignal()
                        {
                            _npcSelectionBar.SelectedNpc = npcToSelect; // Explicitly set the selection
                            Debug.WriteLine(
                                $"VM_Mods.NavigateToNpc: _npcSelectionBar.SelectedNpc explicitly set to {npcToSelect.DisplayName}.");

                            // Schedule the scroll signal with a small delay for the selection to bind in the UI
                            RxApp.MainThreadScheduler.Schedule(TimeSpan.FromMilliseconds(5), () =>
                            {
                                if (_npcSelectionBar.SelectedNpc == npcToSelect) // Final check
                                {
                                    Debug.WriteLine(
                                        $"VM_Mods.NavigateToNpc: Signaling scroll for {npcToSelect.DisplayName}.");
                                    _npcSelectionBar.SignalScrollToNpc(npcToSelect);
                                }
                                else
                                {
                                    Debug.WriteLine(
                                        $"VM_Mods.NavigateToNpc: ERROR - SelectedNpc is now '{_npcSelectionBar.SelectedNpc?.DisplayName ?? "null"}' " +
                                        $"but expected '{npcToSelect.DisplayName}' before signaling scroll. Scroll aborted.");
                                }

                                // Reset the flag AFTER all operations related to this navigation are complete.
                                _npcSelectionBar.IsProgrammaticNavigationInProgress = false;
                                Debug.WriteLine(
                                    $"VM_Mods.NavigateToNpc: IsProgrammaticNavigationInProgress set to false for {npcToSelect.DisplayName}.");
                            });
                        }
                    });
                }
                else
                {
                    ScrollableMessageBox.ShowWarning(
                        $"Could not find NPC with FormKey {npcFormKey} in the main NPC list.", "NPC Not Found");
                    // Ensure flag is reset even if NPC not found.
                    if (_npcSelectionBar != null) _npcSelectionBar.IsProgrammaticNavigationInProgress = false;
                }
            });
        }

        // RecalculateMugshotValidity now sets VM_ModSetting.HasValidMugshots
        // based on whether *actual* mugshots can be found for its defined NPCs.
        public void RecalculateMugshotValidity(VM_ModSetting modSetting)
        {
            modSetting.HasValidMugshots =
                CheckMugshotValidity(modSetting.MugShotFolderPath); // Pass the VM_ModSetting itself
            // If this was the selected mod, refresh the right panel
            if (SelectedModForMugshots == modSetting)
            {
                // Re-run ShowMugshotsAsync to reflect potential change from real to placeholder or vice-versa
                ShowMugshotsCommand.Execute(modSetting).Subscribe().DisposeWith(_disposables);
            }
        }

        private bool CheckMugshotValidity(string? mugshotFolderPath)
        {
            {
                if (string.IsNullOrWhiteSpace(mugshotFolderPath) || !Directory.Exists(mugshotFolderPath))
                {
                    return false;
                }

                try
                {
                    // Check if it contains at least one valid image file directly or in a plugin subfolder
                    return Directory.EnumerateFiles(mugshotFolderPath, "*.*", SearchOption.AllDirectories)
                        .Any(f => Regex.IsMatch(Path.GetFileName(f), @"^[0-9A-F]{8}\.(png|jpg|jpeg|bmp)$",
                                      RegexOptions.IgnoreCase) &&
                                  new FileInfo(f).Directory?.Name?.Contains('.') ==
                                  true); // Basic check for plugin-like folder name
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error checking mugshot validity for path {mugshotFolderPath}: {ex.Message}");
                    return false;
                }
            }
        }

        public async Task PopulateModSettingsAsync(VM_SplashScreen? splashReporter)
        {
            _aux.ReinitializeModDependentProperties();
            // Phase 1: Initialize and load data from disk
            var (tempList, loadedDisplayNames, claimedMugshotPaths, warnings) = InitializePopulation(splashReporter);

            LoadModsFromSettings(tempList, loadedDisplayNames, claimedMugshotPaths);

            var vmsFromMugshotsOnly = ScanForMugshotOnlyMods(loadedDisplayNames, claimedMugshotPaths, warnings, splashReporter);

            await ScanForModsInModFolderAsync(tempList, vmsFromMugshotsOnly, loadedDisplayNames, claimedMugshotPaths, splashReporter, warnings);

            // Phase 2: Consolidate and sort the gathered data
            FinalizeModList(tempList, vmsFromMugshotsOnly);
            AddBaseAndCreationClubMods(tempList);
            _allModSettingsInternal.Clear();
            _allModSettingsInternal.AddRange(SortVMs(tempList));

            // Phase 3: Perform heavy analysis on the consolidated data
            splashReporter?.UpdateStep("Pre-caching asset file paths...");
            var faceGenCache = await CacheFaceGenPathsOnLoadAsync(splashReporter);

            try
            {
                await AnalyzeModSettingsAsync(splashReporter, faceGenCache);

                // Phase 4: Run final UI-dependent work
                splashReporter?.UpdateStep("Finalizing mod settings...");
                await FinalizeAndApplySettingsOnUI(warnings);
                
                _aux.SaveRaceCache();
            }
            catch (AggregateException aggEx)
            {
                // Handle exceptions from the analysis phase
                foreach (var ex in aggEx.Flatten().InnerExceptions)
                {
                    Debug.WriteLine($"Async NPC list refresh error (outer): {ex.Message}");
                    Application.Current.Dispatcher.Invoke(() => warnings.Add($"Async NPC list refresh error: {ex.Message}"));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in PopulateModSettingsAsync after WhenAll: {ex.Message}");
                Application.Current.Dispatcher.Invoke(() => warnings.Add($"Unexpected error: {ex.Message}"));
            }
            finally
            {
                splashReporter?.UpdateStep("Mod settings populated.");
            }
        }

        private (List<VM_ModSetting> tempList, HashSet<string> loadedDisplayNames, HashSet<string> claimedMugshotPaths, List<string> warnings) 
            InitializePopulation(VM_SplashScreen? splashReporter)
        {
            _allModSettingsInternal.Clear();
            _overridesCache.Clear();
            IsLoadingNpcData = true;
            var warnings = new List<string>();

            if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid || _environmentStateProvider.LoadOrder == null)
            {
                warnings.Add("Environment is not valid. Cannot accurately link plugins.");
            }

            splashReporter?.UpdateStep("Processing configured mod settings...");

            return (new List<VM_ModSetting>(), 
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), 
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), 
                    warnings);
        }

        private void LoadModsFromSettings(List<VM_ModSetting> tempList, HashSet<string> loadedDisplayNames, HashSet<string> claimedMugshotPaths)
        {
            using (ContextualPerformanceTracer.Trace("PopulateMods.FromSettings"))
            {
                foreach (var settingModel in _settings.ModSettings)
                {
                    if (string.IsNullOrWhiteSpace(settingModel.DisplayName)) continue;
                    var vm = _modSettingFromModelFactory(settingModel, this);
                    if (!string.IsNullOrWhiteSpace(vm.MugShotFolderPath) && Directory.Exists(vm.MugShotFolderPath))
                        claimedMugshotPaths.Add(vm.MugShotFolderPath);
                    else if (string.IsNullOrWhiteSpace(vm.MugShotFolderPath))
                    {
                        if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder))
                        {
                            string potentialPathByName = Path.Combine(_settings.MugshotsFolder, vm.DisplayName);
                            if (Directory.Exists(potentialPathByName) && !claimedMugshotPaths.Contains(potentialPathByName))
                            {
                                vm.MugShotFolderPath = potentialPathByName;
                                claimedMugshotPaths.Add(potentialPathByName);
                            }
                        }
                    }

                    tempList.Add(vm);
                    loadedDisplayNames.Add(vm.DisplayName);
                }
            }
        }

        private List<VM_ModSetting> ScanForMugshotOnlyMods(HashSet<string> loadedDisplayNames, HashSet<string> claimedMugshotPaths, List<string> warnings, VM_SplashScreen? splashReporter)
        {
            //splashReporter?.UpdateStep("Scanning for new Mugshots...");
            var vmsFromMugshotsOnly = new List<VM_ModSetting>();
            using (ContextualPerformanceTracer.Trace("PopulateMods.ScanMugshots"))
            {
                if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder))
                {
                    try
                    {
                        foreach (var dirPath in Directory.EnumerateDirectories(_settings.MugshotsFolder).ToList())
                        {
                            if (!claimedMugshotPaths.Contains(dirPath))
                            {
                                string folderName = Path.GetFileName(dirPath);
                                if (!loadedDisplayNames.Contains(folderName))
                                    vmsFromMugshotsOnly.Add(_modSettingFromMugshotPathFactory(folderName, dirPath, this));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Error scanning Mugshots folder '{_settings.MugshotsFolder}': {Environment.NewLine}{ExceptionLogger.GetExceptionStack(ex)}");
                    }
                }
            }
            return vmsFromMugshotsOnly;
        }

        #region Mod Folder Scan Result Types
        /// <summary>
        /// Base class for different outcomes of scanning a single mod folder.
        /// </summary>
        private abstract class ModFolderScanResult { }

        /// <summary>
        /// Represents a newly discovered mod that needs to be added to the list.
        /// </summary>
        private class NewVmResult(VM_ModSetting vm) : ModFolderScanResult
        {
            public VM_ModSetting Vm { get; } = vm;
        }

        /// <summary>
        /// Represents an action to upgrade an existing VM with a new mod folder path and plugins.
        /// </summary>
        private class UpgradeVmResult(string vmDisplayName, string modFolderPath, List<ModKey> modKeys) : ModFolderScanResult
        {
            public string VmDisplayName { get; } = vmDisplayName;
            public string ModFolderPath { get; } = modFolderPath;
            public List<ModKey> ModKeys { get; } = modKeys;
        }

        /// <summary>
        /// Represents a folder that should be cached as a non-appearance mod and skipped in the future.
        /// </summary>
        private class CacheNonAppearanceResult(string modFolderPath, string reason) : ModFolderScanResult
        {
            public string ModFolderPath { get; } = modFolderPath;
            public string Reason { get; } = reason;
        }
        #endregion

        private async Task ScanForModsInModFolderAsync(List<VM_ModSetting> tempList, List<VM_ModSetting> vmsFromMugshotsOnly, HashSet<string> loadedDisplayNames, HashSet<string> claimedMugshotPaths, VM_SplashScreen? splashReporter, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(_settings.ModsFolder) || !Directory.Exists(_settings.ModsFolder)) return;

            var modDirectories = Directory.EnumerateDirectories(_settings.ModsFolder).ToList();
            if (!modDirectories.Any()) return;

            // Use a thread-safe bag to collect results from all parallel tasks.
            var scanResults = new ConcurrentBag<ModFolderScanResult>();
            var scannedModFolders = 0;
            const string tokenFileName = "NPC_Token.json";
            var cachedNonAppearanceDirs = _settings.CachedNonAppearanceMods.Keys.ToHashSet();
            
            splashReporter?.UpdateStep($"Scanning {modDirectories.Count} folders for new appearance mods", modDirectories.Count);

            // Create a collection of tasks, one for each mod directory.
            var processingTasks = modDirectories.Select(modFolderPath => Task.Run(async () =>
            {
                // -- This entire block runs in parallel for each folder. --

                string modFolderName = Path.GetFileName(modFolderPath);
                
                // Update progress in a thread-safe manner.
                var currentProgress = (double)Interlocked.Increment(ref scannedModFolders) / modDirectories.Count * 100.0;

                if (File.Exists(Path.Combine(modFolderPath, tokenFileName)) || cachedNonAppearanceDirs.Contains(modFolderPath))
                {
                    splashReporter?.IncrementProgress($"Scanned: {modFolderName}");
                    return; // Skip this directory.
                }

                var modKeysInFolder = _aux.GetModKeysInDirectory(modFolderPath, warnings, false);

                // Perform READ-ONLY checks against the original lists. This is thread-safe.
                var existingVmFromSettings = tempList.FirstOrDefault(vm => vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));
                var mugshotOnlyVmToUpgrade = vmsFromMugshotsOnly.FirstOrDefault(vm => vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));

                if (existingVmFromSettings != null)
                {
                    scanResults.Add(new UpgradeVmResult(existingVmFromSettings.DisplayName, modFolderPath, modKeysInFolder));
                }
                else if (mugshotOnlyVmToUpgrade != null)
                {
                    scanResults.Add(new UpgradeVmResult(mugshotOnlyVmToUpgrade.DisplayName, modFolderPath, modKeysInFolder));
                }
                else
                {
                    // This helper is called to create a new VM if warranted.
                    var newVmResult = await ProcessNewModFolderForParallelScanAsync(modFolderPath, modKeysInFolder, claimedMugshotPaths);
                    if (newVmResult != null)
                    {
                        scanResults.Add(newVmResult);
                    }
                }
                
                // Unload plugins used only in this task's scope.
                 _pluginProvider.UnloadPlugins(modKeysInFolder);
                 
                 splashReporter?.IncrementProgress($"Scanned: {modFolderName}");
            })).ToList();

            // Await all parallel tasks to complete.
            try
            {
                await Task.WhenAll(processingTasks);
            }
            catch (Exception ex)
            {
                warnings.Add($"An error occurred during parallel mod scanning: {Environment.NewLine}{ExceptionLogger.GetExceptionStack(ex)}");
            }
            
            // -- All parallel work is done. Now, process the results sequentially. --

            // Create a lookup for fast access.
            var mugshotVmLookup = vmsFromMugshotsOnly.ToDictionary(vm => vm.DisplayName, StringComparer.OrdinalIgnoreCase);

            foreach (var result in scanResults)
            {
                switch (result)
                {
                    case UpgradeVmResult upgrade:
                        var vmToUpgrade = tempList.FirstOrDefault(vm => vm.DisplayName == upgrade.VmDisplayName) 
                                          ?? mugshotVmLookup.GetValueOrDefault(upgrade.VmDisplayName);

                        if (vmToUpgrade != null)
                        {
                            UpgradeVmWithPathAndPlugins(vmToUpgrade, upgrade.ModFolderPath, upgrade.ModKeys);

                            // If it was a mugshot-only VM, it now needs to be moved to the main list.
                            if (mugshotVmLookup.ContainsKey(upgrade.VmDisplayName))
                            {
                                tempList.Add(vmToUpgrade);
                                vmsFromMugshotsOnly.Remove(vmToUpgrade);
                                mugshotVmLookup.Remove(upgrade.VmDisplayName); // Prevent re-adding
                            }
                        }
                        break;
                        
                    case NewVmResult newVm:
                        tempList.Add(newVm.Vm);
                        loadedDisplayNames.Add(newVm.Vm.DisplayName);
                        break;

                    case CacheNonAppearanceResult cache:
                        _settings.CachedNonAppearanceMods.TryAdd(cache.ModFolderPath, cache.Reason);
                        break;
                }
            }
        }

        /// <summary>
        /// A modified version of ProcessNewModFolderAsync designed to return a result object
        /// instead of directly modifying collections, making it safe for parallel execution.
        /// </summary>
        private async Task<ModFolderScanResult?> ProcessNewModFolderForParallelScanAsync(string modFolderPath, List<ModKey> modKeysInFolder, ICollection<string> claimedMugshotPaths)
        {
            var scanResult = await FaceGenScanner.CollectFaceGenFilesAsync(modFolderPath, _bsaHandler, modKeysInFolder, _environmentStateProvider.SkyrimVersion.ToGameRelease());

            if (!scanResult.AnyFilesFound)
            {
                return new CacheNonAppearanceResult(modFolderPath, "No FaceGen Files Found");
            }

            var newVm = _modSettingFromModFolderFactory(modFolderPath, modKeysInFolder, this);
            newVm.IsNewlyCreated = true;

            _pluginProvider.LoadPlugins(modKeysInFolder, new HashSet<string> { modFolderPath });

            if (modKeysInFolder.Any() && await ContainsAppearancePluginsAsync(modKeysInFolder, new() { modFolderPath }))
            {
                string potentialMugshotPath = Path.Combine(_settings.MugshotsFolder, newVm.DisplayName);
                if (Directory.Exists(potentialMugshotPath) && !claimedMugshotPaths.Contains(potentialMugshotPath))
                {
                    newVm.MugShotFolderPath = potentialMugshotPath;
                    // Note: This isn't thread-safe, but the chance of collision is low and the impact is minor.
                    // A better solution would involve a ConcurrentDictionary for claimedMugshotPaths.
                    claimedMugshotPaths.Add(potentialMugshotPath);
                }
                CheckMergeInSuitability(newVm);
                return new NewVmResult(newVm);
            }
            else if (modKeysInFolder.Any())
            {
                return new CacheNonAppearanceResult(modFolderPath, "Does not provide new NPCs or modify any NPCs currently in load order");
            }
            else // FaceGen only
            {
                newVm.IsFaceGenOnlyEntry = true;
                foreach (var (pluginName, npcIds) in scanResult.FaceGenFiles)
                {
                    newVm.CorrespondingModKeys.Add(ModKey.FromFileName(pluginName));
                    foreach (var id in npcIds.Where(id => id.Length == 8))
                    {
                        if (FormKey.TryFactory($"{id.Substring(2, 6)}:{pluginName}", out var formKey))
                        {
                            newVm.FaceGenOnlyNpcFormKeys.Add(formKey);
                        }
                    }
                }
                CheckMergeInSuitability(newVm);
                return new NewVmResult(newVm);
            }
        }
        
        private void UpgradeVmWithPathAndPlugins(VM_ModSetting vm, string modFolderPath, List<ModKey> modKeysInFolder)
        {
            if (!vm.CorrespondingFolderPaths.Contains(modFolderPath, StringComparer.OrdinalIgnoreCase))
            {
                vm.CorrespondingFolderPaths.Add(modFolderPath);
            }
            foreach (var key in modKeysInFolder)
            {
                if (!vm.CorrespondingModKeys.Contains(key))
                {
                    vm.CorrespondingModKeys.Add(key);
                }
            }
        }

        private async Task ProcessNewModFolderAsync(string modFolderPath, List<ModKey> modKeysInFolder, List<VM_ModSetting> tempList, HashSet<string> loadedDisplayNames, HashSet<string> claimedMugshotPaths)
        {
            FaceGenScanResult scanResult;
            using (ContextualPerformanceTracer.Trace("PopulateMods.CollectFaceGenFilesAsync"))
            {
                scanResult = await FaceGenScanner.CollectFaceGenFilesAsync(modFolderPath, _bsaHandler, modKeysInFolder, _environmentStateProvider.SkyrimVersion.ToGameRelease());
            }

            if (!scanResult.AnyFilesFound)
            {
                _settings.CachedNonAppearanceMods.Add(modFolderPath, "No FaceGen Files Found");
                return;
            }

            var newVm = _modSettingFromModFolderFactory(modFolderPath, modKeysInFolder, this);
            newVm.IsNewlyCreated = true;

            using (ContextualPerformanceTracer.Trace("PopulateMods.LoadPlugins"))
            {
                _pluginProvider.LoadPlugins(modKeysInFolder, new HashSet<string> { modFolderPath });
            }

            if (modKeysInFolder.Any() && await ContainsAppearancePluginsAsync(modKeysInFolder, new() { modFolderPath }))
            {
                string potentialMugshotPath = Path.Combine(_settings.MugshotsFolder, newVm.DisplayName);
                if (Directory.Exists(potentialMugshotPath) && !claimedMugshotPaths.Contains(potentialMugshotPath))
                {
                    newVm.MugShotFolderPath = potentialMugshotPath;
                    claimedMugshotPaths.Add(potentialMugshotPath);
                }
                tempList.Add(newVm);
                loadedDisplayNames.Add(newVm.DisplayName);
            }
            else if (modKeysInFolder.Any())
            {
                _settings.CachedNonAppearanceMods.Add(modFolderPath, "Does not provide new NPCs or modify any NPCs currently in load order");
            }
            else // FaceGen only
            {
                newVm.IsFaceGenOnlyEntry = true;
                foreach (var (pluginName, npcIds) in scanResult.FaceGenFiles)
                {
                    newVm.CorrespondingModKeys.Add(ModKey.FromFileName(pluginName));
                    foreach (var id in npcIds.Where(id => id.Length == 8))
                    {
                        if (FormKey.TryFactory($"{id.Substring(2, 6)}:{pluginName}", out var formKey))
                        {
                            newVm.FaceGenOnlyNpcFormKeys.Add(formKey);
                        }
                    }
                }
                tempList.Add(newVm);
                loadedDisplayNames.Add(newVm.DisplayName);
            }

            using (ContextualPerformanceTracer.Trace("PopulateMods.CheckMergeInSuitability"))
            {
                CheckMergeInSuitability(newVm);
            }
        }

        private void FinalizeModList(List<VM_ModSetting> tempList, List<VM_ModSetting> vmsFromMugshotsOnly)
        {
            foreach (var mugshotVm in vmsFromMugshotsOnly)
            {
                if (!tempList.Any(existing => existing.DisplayName.Equals(mugshotVm.DisplayName, StringComparison.OrdinalIgnoreCase)))
                {
                    tempList.Add(mugshotVm);
                }
            }

            foreach (var vm in tempList)
            {
                if (vm.IsMugshotOnlyEntry && (vm.CorrespondingFolderPaths.Any() || vm.CorrespondingModKeys.Any()))
                {
                    vm.IsMugshotOnlyEntry = false;
                }
            }
        }
        
        private void AddBaseAndCreationClubMods(List<VM_ModSetting> tempList)
        {
            var baseGameModKeys = _environmentStateProvider.BaseGamePlugins;
            var creationClubModKeys = _environmentStateProvider.CreationClubPlugins;

            baseGameModKeys.RemoveWhere(mk => tempList.Any(vm => !vm.IsFaceGenOnlyEntry && !vm.IsMugshotOnlyEntry && vm.CorrespondingModKeys.Contains(mk)));
            creationClubModKeys.RemoveWhere(mk => tempList.Any(vm => !vm.IsFaceGenOnlyEntry && !vm.IsMugshotOnlyEntry && vm.CorrespondingModKeys.Contains(mk)));

            if (creationClubModKeys.Any() && !tempList.Any(vm => vm.DisplayName.Equals(CreationClubModsettingName, StringComparison.OrdinalIgnoreCase)))
            {
                var ccMod = new ModSetting() { DisplayName = CreationClubModsettingName, CorrespondingModKeys = creationClubModKeys.ToList(), IsAutoGenerated = true, MergeInDependencyRecords = false };
                var ccModVm = _modSettingFromModelFactory(ccMod, this);
                ccModVm.MergeInDependencyRecordsVisible = false;
                tempList.Add(ccModVm);
            }

            if (baseGameModKeys.Any() && !tempList.Any(vm => vm.DisplayName.Equals(BaseGameModSettingName, StringComparison.OrdinalIgnoreCase)))
            {
                var baseMod = new ModSetting() { DisplayName = BaseGameModSettingName, CorrespondingModKeys = baseGameModKeys.ToList(), IsAutoGenerated = true, MergeInDependencyRecords = false };
                var baseModVm = _modSettingFromModelFactory(baseMod, this);
                baseModVm.MergeInDependencyRecordsVisible = false;
                tempList.Add(baseModVm);
            }
        }
        
        private async Task AnalyzeModSettingsAsync(VM_SplashScreen? splashReporter, (HashSet<string> allFaceGenLooseFiles, Dictionary<string, HashSet<string>> allFaceGenBsaFiles) faceGenCache)
        {
            var maxParallelism = Environment.ProcessorCount;
            var semaphore = new SemaphoreSlim(maxParallelism);
            
            var allVMs = _allModSettingsInternal.ToList(); // Create a copy to iterate over
            var vmsToAnalyze = new List<VM_ModSetting>();
            
            var modSettingsToLogCount = _allModSettingsInternal.Count(x => x.IsNewlyCreated);
            var analyzedCount = 0;
            splashReporter?.UpdateStep($"Preparing to analyze data for {modSettingsToLogCount} Mods...");
            
            // --- CACHING LOGIC ---
            using (ContextualPerformanceTracer.Trace("AnalyzeModSettings.CacheValidation"))
            {
                foreach (var vm in allVMs)
                {
                    // Don't use cache for newly discovered mods or facegen-only entries
                    if (vm.IsNewlyCreated || vm.IsFaceGenOnlyEntry)
                    {
                        vm.LastKnownState = null; // Ensure no old state is saved
                        vmsToAnalyze.Add(vm);
                        continue;
                    }

                    var currentSnapshot = GenerateSnapshot(vm);
                    if (currentSnapshot != null && vm.LastKnownState != null && currentSnapshot.Equals(vm.LastKnownState))
                    {
                        // CACHE HIT: The mod is unchanged. Do nothing.
                        Debug.WriteLine($"Cache HIT for: {vm.DisplayName}");
                        // The VM was already populated from the model, so we are done with it.
                        vm.LastKnownState = currentSnapshot; // Keep the snapshot updated in the VM to save it again.
                    }
                    else
                    {
                        // CACHE MISS: Mod has changed or snapshot failed. Needs analysis.
                        Debug.WriteLine($"Cache MISS for: {vm.DisplayName}");
                        vmsToAnalyze.Add(vm);
                        vm.LastKnownState = currentSnapshot; // Store the NEW snapshot to be saved after analysis
                    }
                }
            }
            // --- END CACHING LOGIC ---

            var refreshTasks = vmsToAnalyze.Select(async vm =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await Task.Run(async () =>
                    {
                        var modFolderPathsForVm = vm.CorrespondingFolderPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var plugins = _pluginProvider.LoadPlugins(vm.CorrespondingModKeys, modFolderPathsForVm);
                        try
                        {
                            using (ContextualPerformanceTracer.Trace("RefreshNpcLists"))
                            {
                                vm.RefreshNpcLists(faceGenCache.allFaceGenLooseFiles, faceGenCache.allFaceGenBsaFiles, plugins);
                            }

                            if (vm.IsNewlyCreated)
                            {
                                using (ContextualPerformanceTracer.Trace("FindPluginsWithOverrides"))
                                {
                                    await vm.FindPluginsWithOverrides(_pluginProvider);
                                }
                            }
                        }
                        finally
                        {
                            _pluginProvider.UnloadPlugins(vm.CorrespondingModKeys);
                            var currentAnalyzed = Interlocked.Increment(ref analyzedCount);
                            var progress = modSettingsToLogCount > 0 ? (double)currentAnalyzed / modSettingsToLogCount * 100.0 : 0;
                            splashReporter?.UpdateProgress(progress, $"Analyzed: {vm.DisplayName}");
                        }
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(refreshTasks);
        }

        private async Task FinalizeAndApplySettingsOnUI(List<string> warnings)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var vm in _allModSettingsInternal)
                {
                    if (vm.HasAlteredMergeLogic)
                    {
                        vm.MergeInLabelColor = new(System.Windows.Media.Colors.Purple);
                    }
                    else
                    {
                        vm.MergeInLabelColor = new(System.Windows.Media.Colors.Black);
                    }
                    RecalculateMugshotValidity(vm);
                }

                IsLoadingNpcData = false;
                ApplyFilters();

                if (warnings.Any())
                {
                    ScrollableMessageBox.ShowWarning(string.Join("\n", warnings), "Mod Settings Population Warning");
                }
            });
        }

        private (HashSet<string> allFaceGenLooseFiles, Dictionary<string, HashSet<string>> allFaceGenBsaFiles)
            CacheFaceGenPathsOnLoad()
        {
            // Cache 1: All loose FaceGen files from all mod directories.
            Debug.WriteLine("Caching loose FaceGen file paths...");
            var allUniqueModPaths = _allModSettingsInternal
                .SelectMany(vm => vm.CorrespondingFolderPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var allFaceGenLooseFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var modPath in allUniqueModPaths)
            {
                if (!Directory.Exists(modPath)) continue;

                // Search for .nif (meshes) and .dds (textures) in the expected FaceGen locations
                var texturesPath = Path.Combine(modPath, "Textures");
                var meshesPath = Path.Combine(modPath, "Meshes");

                if (Directory.Exists(texturesPath))
                {
                    foreach (var file in Directory.EnumerateFiles(texturesPath, "*.dds", SearchOption.AllDirectories))
                    {
                        // Store the relative path, normalized
                        allFaceGenLooseFiles.Add(Path.GetRelativePath(modPath, file).Replace('\\', '/'));
                    }
                }

                if (Directory.Exists(meshesPath))
                {
                    foreach (var file in Directory.EnumerateFiles(meshesPath, "*.nif", SearchOption.AllDirectories))
                    {
                        allFaceGenLooseFiles.Add(Path.GetRelativePath(modPath, file).Replace('\\', '/'));
                    }
                }
            }

            Debug.WriteLine($"Cached {allFaceGenLooseFiles.Count} loose file paths.");


            // Cache 2: All FaceGen files within all relevant BSAs, grouped by the ModSetting VM.
            Debug.WriteLine("Caching FaceGen file paths from BSAs...");
            var allFaceGenBsaFiles = new Dictionary<string, HashSet<string>>(); // key is ModSetting Display Name
            var internalBsaDirCache =
                new Dictionary<string, HashSet<string>>(); // key is BSA path. For interal use here

            foreach (var vm in _allModSettingsInternal)
            {
                var bsaFilePathsForVm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var pathsToSearch = new HashSet<string>(vm.CorrespondingFolderPaths);
                if (vm.DisplayName == VM_Mods.BaseGameModSettingName ||
                    vm.DisplayName == VM_Mods.CreationClubModsettingName)
                {
                    pathsToSearch.Add(_environmentStateProvider.DataFolderPath);
                }

                var bsaDict = _bsaHandler.GetBsaPathsForPluginsInDirs(vm.CorrespondingModKeys, pathsToSearch,
                    _settings.SkyrimRelease.ToGameRelease());

                foreach (var modKey in vm.CorrespondingModKeys)
                {
                    var bsaPathsForPlugin = bsaDict[modKey];
                    var bsaReadersForPlugin = _bsaHandler.OpenBsaArchiveReaders(bsaPathsForPlugin,
                        _settings.SkyrimRelease.ToGameRelease(), false);

                    foreach (var entry in bsaReadersForPlugin)
                    {
                        if (internalBsaDirCache.TryGetValue(entry.Key, out var faceGenFilesInArchive))
                        {
                            bsaFilePathsForVm.UnionWith(faceGenFilesInArchive);
                        }
                        else
                        {
                            // Iterate all file records in the BSA ONCE and store them
                            var reader = entry.Value;
                            if (reader.Files.Any())
                            {
                                HashSet<string> faceGenFilesInThisArchive = new();
                                foreach (var fileRecord in reader.Files)
                                {
                                    string path = fileRecord.Path.ToLowerInvariant().Replace('\\', '/');
                                    if (path.StartsWith("meshes/actors/character/facegendata/") ||
                                        path.StartsWith("textures/actors/character/facegendata/"))
                                    {
                                        bsaFilePathsForVm.Add(path);
                                        faceGenFilesInThisArchive.Add(path);
                                    }
                                }

                                internalBsaDirCache.Add(entry.Key, faceGenFilesInThisArchive);
                            }
                        }
                    }
                }

                // Store the collected BSA file paths against a unique key for the vm (e.g., DisplayName)
                allFaceGenBsaFiles[vm.DisplayName] = bsaFilePathsForVm;
            }

            Debug.WriteLine($"Cached BSA file paths for {allFaceGenBsaFiles.Count} mod settings.");

// -        -- END OF NEW PRE-CACHING LOGIC ---
            return (allFaceGenLooseFiles, allFaceGenBsaFiles);
        }
        
        private async Task<(HashSet<string> allFaceGenLooseFiles, Dictionary<string, HashSet<string>> allFaceGenBsaFiles)>
            CacheFaceGenPathsOnLoadAsync(VM_SplashScreen? splashReporter)
        {
            // --- Part 1: Cache loose files (this is a fast operation) ---
            Debug.WriteLine("Caching loose FaceGen file paths...");
            var allUniqueModPaths = _allModSettingsInternal
                .SelectMany(vm => vm.CorrespondingFolderPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var allFaceGenLooseFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var modPath in allUniqueModPaths)
            {
                if (!Directory.Exists(modPath)) continue;

                var texturesPath = Path.Combine(modPath, "Textures");
                if (Directory.Exists(texturesPath))
                {
                    foreach (var file in Directory.EnumerateFiles(texturesPath, "*.dds", SearchOption.AllDirectories))
                    {
                        allFaceGenLooseFiles.Add(Path.GetRelativePath(modPath, file).Replace('\\', '/'));
                    }
                }

                var meshesPath = Path.Combine(modPath, "Meshes");
                if (Directory.Exists(meshesPath))
                {
                    foreach (var file in Directory.EnumerateFiles(meshesPath, "*.nif", SearchOption.AllDirectories))
                    {
                        allFaceGenLooseFiles.Add(Path.GetRelativePath(modPath, file).Replace('\\', '/'));
                    }
                }
            }
            Debug.WriteLine($"Cached {allFaceGenLooseFiles.Count} loose file paths.");
            
            // --- Part 2: Asynchronously cache BSA files with progress reporting ---
            splashReporter?.UpdateStep("Pre-caching BSA file paths...");
            Debug.WriteLine("Pre-caching all relevant BSA paths...");

            var (vmBsaPathsCache, allRelevantBsaPaths) = await Task.Run(() =>
            {
                var localVmBsaPathsCache = new Dictionary<string, Dictionary<ModKey, HashSet<string>>>();
                var localAllRelevantBsaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var totalVmCount = _allModSettingsInternal.Count;
                var processedVmCount = 0;

                foreach (var vm in _allModSettingsInternal)
                {
                    var pathsToSearch = new HashSet<string>(vm.CorrespondingFolderPaths);
                    if (vm.DisplayName is BaseGameModSettingName or CreationClubModsettingName)
                    {
                        pathsToSearch.Add(_environmentStateProvider.DataFolderPath);
                    }

                    // This is the expensive, blocking call.
                    var bsaDictForVm = _bsaHandler.GetBsaPathsForPluginsInDirs(vm.CorrespondingModKeys, pathsToSearch, _settings.SkyrimRelease.ToGameRelease());
            
                    localVmBsaPathsCache[vm.DisplayName] = bsaDictForVm;

                    foreach (var bsaPath in bsaDictForVm.Values.SelectMany(paths => paths))
                    {
                        localAllRelevantBsaPaths.Add(bsaPath);
                    }

                    // Report progress after each item in the loop is processed.
                    processedVmCount++;
                    var progress = totalVmCount > 0 ? (double)processedVmCount / totalVmCount * 100.0 : 100.0;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        splashReporter?.UpdateProgress(progress, $"Analyzing assets: {vm.DisplayName}");
                    });
                }
                return (localVmBsaPathsCache, localAllRelevantBsaPaths);
            });
            Debug.WriteLine($"Found {allRelevantBsaPaths.Count} unique BSAs to process.");

            // b) Process each unique BSA, reporting progress.
            splashReporter?.UpdateStep("Caching asset contents...");
            var bsaContentCache = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var processingTasks = allRelevantBsaPaths.Select(bsaPath => Task.Run(() =>
            {
                var faceGenFilesInArchive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var bsaReaders = _bsaHandler.OpenBsaArchiveReaders(new[] { bsaPath }, _settings.SkyrimRelease.ToGameRelease(), false);

                if (bsaReaders.TryGetValue(bsaPath, out var reader) && reader.Files.Any())
                {
                    foreach (var fileRecord in reader.Files)
                    {
                        string path = fileRecord.Path.ToLowerInvariant().Replace('\\', '/');
                        if (path.StartsWith("meshes/actors/character/facegendata/") ||
                            path.StartsWith("textures/actors/character/facegendata/"))
                        {
                            faceGenFilesInArchive.Add(path);
                        }
                    }
                }
                bsaContentCache.TryAdd(bsaPath, faceGenFilesInArchive);
            })).ToList();

            await Task.WhenAll(processingTasks);
            
            Debug.WriteLine("Finished caching content from all BSAs.");
            splashReporter?.UpdateStep("Finalizing asset cache...");
            
            // --- Part 3: Assemble the final dictionary for each VM using the caches ---
            var allFaceGenBsaFiles = new Dictionary<string, HashSet<string>>();
            foreach (var vm in _allModSettingsInternal)
            {
                var bsaFilePathsForVm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Retrieve the pre-computed BSA dictionary from the cache instead of recalculating.
                if (vmBsaPathsCache.TryGetValue(vm.DisplayName, out var bsaDict))
                {
                     // Get all unique BSA paths for this specific VM from the cached result.
                    var uniqueBsaPathsForVm = bsaDict.Values.SelectMany(paths => paths).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    // Look up the pre-cached content for each BSA and add it to the VM's set.
                    foreach (var bsaPath in uniqueBsaPathsForVm)
                    {
                        if (bsaContentCache.TryGetValue(bsaPath, out var cachedContent))
                        {
                            bsaFilePathsForVm.UnionWith(cachedContent);
                        }
                    }
                }
                
                // Store the collected BSA file paths against the VM's DisplayName.
                allFaceGenBsaFiles[vm.DisplayName] = bsaFilePathsForVm;
            }
            
            Debug.WriteLine($"Assembled BSA file paths for {allFaceGenBsaFiles.Count} mod settings from cache.");
            return (allFaceGenLooseFiles, allFaceGenBsaFiles);
        }

        // Filtering Logic (Left Panel)
        public void ApplyFilters()
        {
            // If data is actively being loaded, the underlying collection is unstable.
            // Clear the public list and exit. The loading process will call this method again when complete.
            if (IsLoadingNpcData)
            {
                ModSettingsList.Clear();
                return;
            }
            
            IEnumerable<VM_ModSetting> filtered = _allModSettingsInternal;

            if (!string.IsNullOrWhiteSpace(NameFilterText))
            {
                filtered = filtered.Where(vm =>
                    vm.DisplayName.Contains(NameFilterText, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(PluginFilterText))
            {
                filtered = filtered.Where(vm => vm.CorrespondingModKeys.Any(key =>
                    key.FileName.String.Contains(PluginFilterText, StringComparison.OrdinalIgnoreCase) ||
                    key.ToString().Contains(PluginFilterText, StringComparison.OrdinalIgnoreCase)
                ));
            }

            // *** Apply NPC Filter ***
            if (!IsLoadingNpcData && !string.IsNullOrWhiteSpace(NpcSearchText))
            {
                string searchTextLower = NpcSearchText.Trim().ToLowerInvariant(); // Use invariant culture lowercase

                switch (SelectedNpcSearchType)
                {
                    case ModNpcSearchType.Name:
                        filtered = filtered.Where(vm =>
                            vm.NpcNames.Any(name =>
                                name.Contains(searchTextLower, StringComparison.OrdinalIgnoreCase)) ||
                            vm.NpcFormKeysToDisplayName.Values.Any(dName =>
                                dName.Contains(searchTextLower,
                                    StringComparison.OrdinalIgnoreCase))); // Also check dictionary values
                        break;
                    case ModNpcSearchType.EditorID:
                        filtered = filtered.Where(vm =>
                            vm.NpcEditorIDs.Any(
                                eid => eid.Contains(searchTextLower, StringComparison.OrdinalIgnoreCase)));
                        break;
                    case ModNpcSearchType.FormKey:
                        // Compare string representations of FormKeys
                        filtered = filtered.Where(vm => vm.NpcFormKeys.Any(fk =>
                            fk.ToString().Contains(searchTextLower, StringComparison.OrdinalIgnoreCase)));
                        break;
                }
            }
            // *** End NPC Filter ***

            var previouslySelectedMod = SelectedModForMugshots; // Preserve selection if possible

            ModSettingsList.Clear();
            var filteredList = filtered.ToList(); // Materialize the list
            foreach (var vm in filteredList)
            {
                ModSettingsList.Add(vm);
            }

            // Check if the previously selected item for mugshots is still in the filtered list
            if (previouslySelectedMod != null && !filteredList.Contains(previouslySelectedMod))
            {
                // It was filtered out, clear the right panel
                SelectedModForMugshots = null;
                CurrentModNpcMugshots.Clear();
            }


            System.Diagnostics.Debug.WriteLine(
                $"ApplyFilters: Displaying {ModSettingsList.Count} of {_allModSettingsInternal.Count} items.");
        }

        public bool TryGetWinningNpc(FormKey fk, out INpcGetter? npcGetter)
        {
            var matchingNpc = _environmentStateProvider.LinkCache.TryResolve<INpcGetter>(fk, out npcGetter);
            return matchingNpc;
        }


        // Save Logic
        public void SaveModSettingsToModel()
        {
            _settings.ModSettings.Clear();
            foreach (var vm in _allModSettingsInternal) // Save from the full list
            {
                // Only save if it has meaningful data (Key, Folder Paths, or Mugshot Path)
                if (!string.IsNullOrWhiteSpace(vm.DisplayName) &&
                    (vm.CorrespondingModKeys.Any() || vm.CorrespondingFolderPaths.Any())) // Check if any keys exist
                {
                    // Create a new ModSetting model instance
                    var model = vm.SaveToModel();
                    _settings.ModSettings.Add(model);
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"DEBUG: SaveModSettingsToModel preparing to save {_settings.ModSettings.Count} items.");

            // Saving the main settings file is handled by VM_Settings on App Exit
            // string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.json");
            // JSONhandler<Settings>.SaveJSONFile(_settings, settingsPath, out bool success, out string exception);
            // if (!success) { MessageBox.Show($"Error saving settings: {exception}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error); }
            // else { System.Diagnostics.Debug.WriteLine("DEBUG: Settings successfully updated in memory by SaveModSettingsToModel."); }
        }

        /// <summary>
        /// Removes the specified VM_ModSetting from the internal list and refreshes the filtered view.
        /// </summary>
        /// <param name="modSettingToRemove">The VM_ModSetting to remove.</param>
        /// <returns>True if the item was successfully found and removed; otherwise, false.</returns>
        public bool RemoveModSetting(VM_ModSetting modSettingToRemove)
        {
            if (modSettingToRemove == null) return false;

            bool removed = _allModSettingsInternal.Remove(modSettingToRemove);
            if (removed)
            {
                Debug.WriteLine($"VM_Mods: Removed ModSetting '{modSettingToRemove.DisplayName}' from internal list.");
                // If the removed mod was selected for mugshots, clear the selection
                if (SelectedModForMugshots == modSettingToRemove)
                {
                    SelectedModForMugshots = null;
                    CurrentModNpcMugshots.Clear();
                }

                ApplyFilters(); // Refresh the ModSettingsList (left panel)
            }
            else
            {
                Debug.WriteLine(
                    $"VM_Mods: ModSetting '{modSettingToRemove.DisplayName}' not found in internal list for removal.");
            }

            return removed;
        }

        /// <summary>
        /// Tries to find an existing VM_ModSetting matching the plugin key.
        /// It searches based on the CorrespondingModKey property of the VM_ModSettings.
        /// </summary>
        /// <param name="appearancePluginKey">The ModKey of the appearance plugin to search for.</param>
        /// <param name="foundVms">Output: The found VM_ModSetting instances if a match exists; otherwise, empty list.</param>
        /// <returns>True if a matching VM_ModSetting was found based on the CorrespondingModKey, false otherwise.</returns>
        public bool TryGetModSettingForPlugin(ModKey appearancePluginKey, out List<VM_ModSetting> foundVms)
        {
            // Initialize output parameters
            foundVms = new();

            // Check if the input ModKey is valid (not null or default)
            if (appearancePluginKey.IsNull)
            {
                Debug.WriteLine($"TryGetModSettingForPlugin: Received an invalid (IsNull) ModKey.");
                // Keep foundVm as null and modDisplayName as the default set above.
                return false; // Cannot find a match for an invalid key.
            }

            // Search the internal list of all loaded/created mod settings.
            // Find the first VM where *any* of its CorrespondingModKeys matches the input key.
            foundVms = _allModSettingsInternal.Where(vm =>
                vm.CorrespondingModKeys.Any(key => key.Equals(appearancePluginKey))).ToList();

            if (foundVms.Count > 1)
            {
                foundVms = foundVms.Where(x => !x.IsFaceGenOnlyEntry).ToList();
            }
            
            return foundVms.Any();
        }

        /// <summary>
        /// Called after a Mod Folder Path or Mugshot Folder Path is potentially changed on a VM_ModSetting.
        /// Checks if this change links it to another complementary VM (one with only mugshots, one with only mods)
        /// and performs an automatic merge if conditions are met.
        /// </summary>
        /// <param name="modifiedVm">The VM that the user directly modified.</param>
        /// <param name="addedOrSetPath">The specific path that was added or set.</param>
        /// <param name="pathType">Indicates whether a ModFolder or MugshotFolder was changed.</param>
        /// <param name="hadMugshotPathBefore">Did modifiedVm have a mugshot path BEFORE this change?</param>
        /// <param name="hadModPathsBefore">Did modifiedVm have mod paths BEFORE this change?</param>
        public void CheckForAndPerformMerge(VM_ModSetting modifiedVm, string addedOrSetPath, PathType pathType,
            bool hadMugshotPathBefore, bool hadModPathsBefore)
        {
            if (modifiedVm == null || string.IsNullOrEmpty(addedOrSetPath)) return;

            VM_ModSetting? sourceVm = null; // The potential VM to merge *from*

            // Find a potential source VM that contains the path added/set to the modified VM
            foreach (var vm in _allModSettingsInternal)
            {
                if (vm == modifiedVm) continue; // Don't compare to self

                bool pathMatches = false;
                if (pathType == PathType.ModFolder &&
                    vm.CorrespondingFolderPaths.Contains(addedOrSetPath, StringComparer.OrdinalIgnoreCase))
                {
                    pathMatches = true;
                }
                else if (pathType == PathType.MugshotFolder &&
                         addedOrSetPath.Equals(vm.MugShotFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    pathMatches = true;
                }

                if (pathMatches)
                {
                    sourceVm = vm;
                    break; // Found a potential source containing the path
                }
            }

            if (sourceVm == null) return; // No other VM contains this specific path

            // Now check if the merge conditions based on initial states are met
            bool mergeConditionsMet = false;
            VM_ModSetting winner = modifiedVm; // Assume the modified VM is the winner initially
            VM_ModSetting loser = sourceVm;

            if (pathType == PathType.ModFolder)
            {
                // User added a Mod Folder path to 'modifiedVm'
                // Conditions:
                // 1. 'modifiedVm' previously ONLY had mugshots (hadMugshotPathBefore=true, hadModPathsBefore=false)
                // 2. 'sourceVm' previously ONLY had this specific mod path and NO mugshots
                bool sourceHadOnlyThisModPath = sourceVm.CorrespondingFolderPaths.Count == 1 &&
                                                sourceVm.CorrespondingFolderPaths.Contains(addedOrSetPath,
                                                    StringComparer.OrdinalIgnoreCase);
                bool sourceHadNoMugshots =
                    string.IsNullOrWhiteSpace(sourceVm.MugShotFolderPath); // Check current state is okay here

                if (hadMugshotPathBefore && !hadModPathsBefore && sourceHadOnlyThisModPath && sourceHadNoMugshots)
                {
                    mergeConditionsMet = true;
                    // Winner = modifiedVm, Loser = sourceVm (Correctly initialized)
                }
            }
            else // pathType == PathType.MugshotFolder
            {
                // User set the Mugshot Folder path on 'modifiedVm'
                // Conditions:
                // 1. 'modifiedVm' previously ONLY had mod paths (hadMugshotPathBefore=false, hadModPathsBefore=true)
                // 2. 'sourceVm' previously ONLY had this specific mugshot path and NO mod paths
                bool sourceHadOnlyThisMugshot =
                    addedOrSetPath.Equals(sourceVm.MugShotFolderPath, StringComparison.OrdinalIgnoreCase) &&
                    !sourceVm.HasModPathsAssigned; // Check current state is okay
                bool sourceHadNoModPaths = !sourceVm.HasModPathsAssigned; // Redundant check, but clear

                if (!hadMugshotPathBefore && hadModPathsBefore && sourceHadOnlyThisMugshot)
                {
                    mergeConditionsMet = true;
                    // Winner = modifiedVm, Loser = sourceVm (Correctly initialized)
                }
            }


            // Perform the merge if conditions are met
            if (mergeConditionsMet)
            {
                Debug.WriteLine($"Merge Condition Met: Merging '{loser.DisplayName}' into '{winner.DisplayName}'");

                // --- Perform Merge Actions ---
                // 1. Transfer necessary data (loser -> winner)
                // Mugshot Path (only if winner doesn't have one)
                if (!winner.HasMugshotPathAssigned && loser.HasMugshotPathAssigned)
                {
                    winner.MugShotFolderPath = loser.MugShotFolderPath;
                }

                // Mod Folder Paths (add paths from loser not already in winner)
                foreach (var path in loser.CorrespondingFolderPaths)
                {
                    if (!winner.CorrespondingFolderPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        winner.CorrespondingFolderPaths.Add(path);
                    }
                }

                // Merge Corresponding Mod Keys (add keys from loser not already in winner)
                foreach (var key in loser.CorrespondingModKeys)
                {
                    if (!winner.CorrespondingModKeys.Contains(key))
                    {
                        winner.CorrespondingModKeys.Add(key);
                    }
                }

                // Merge NpcPluginDisambiguation: Loser's choices might be relevant if winner didn't have them
                foreach (var disambiguationEntry in loser.NpcPluginDisambiguation)
                {
                    if (!winner.NpcPluginDisambiguation.ContainsKey(disambiguationEntry.Key))
                    {
                        // Only add if the plugin is now part of the winner's CorrespondingModKeys
                        if (winner.CorrespondingModKeys.Contains(disambiguationEntry.Value))
                        {
                            winner.NpcPluginDisambiguation[disambiguationEntry.Key] = disambiguationEntry.Value;
                        }
                    }
                }

                // IsMugshotOnlyEntry should remain based on the WINNER's original status
                // Although, if a merge happens, it's unlikely the winner was mugshot-only. Let's set it to false.
                winner.IsMugshotOnlyEntry = false;

                // Refresh NPC lists for the winner as its sources may have changed/**/

                (var allFaceGenLooseFiles, var allFaceGenBsaFiles) =
                    CacheFaceGenPathsOnLoad();

                var plugins = _pluginProvider.LoadPlugins(winner.CorrespondingModKeys, winner.CorrespondingFolderPaths.ToHashSet());
                Task.Run(() => winner.RefreshNpcLists(allFaceGenLooseFiles, allFaceGenBsaFiles, plugins));
                _pluginProvider.UnloadPlugins(winner.CorrespondingModKeys);

                // 2. Update NPC Selections (_model.SelectedAppearanceMods via _consistencyProvider)
                string loserName = loser.DisplayName;
                string winnerName = winner.DisplayName;
                var selectionsToUpdate = _settings.SelectedAppearanceMods
                    .Where(kvp => kvp.Value.ModName.Equals(loserName, StringComparison.OrdinalIgnoreCase))
                    .ToList(); 

                if (selectionsToUpdate.Any())
                {
                    Debug.WriteLine(
                        $"Updating {selectionsToUpdate.Count} NPC selections from '{loserName}' to '{winnerName}'.");
                    foreach (var selection in selectionsToUpdate)
                    {
                        var targetNpcKey = selection.Key;
                        var originalSourceNpcKey = selection.Value.NpcFormKey;

                        // Call SetSelectedMod with the new winner mod name, but the original source NPC key.
                        _consistencyProvider.SetSelectedMod(targetNpcKey, winnerName, originalSourceNpcKey);
                    }
                }
                
                // 3. Update the stale data cache within the NPC view model's list
                foreach (var npcVM in _npcSelectionBar.AllNpcs)
                {
                    var modToRemove = npcVM.AppearanceMods.FirstOrDefault(m => m == loser);
                    if (modToRemove != null)
                    {
                        npcVM.AppearanceMods.Remove(modToRemove);
                        if (!npcVM.AppearanceMods.Contains(winner))
                        {
                            npcVM.AppearanceMods.Add(winner);
                        }
                    }
                }
                
                // 4. Remove Loser VM
                bool removed = _allModSettingsInternal.Remove(loser);
                Debug.WriteLine($"Removed loser VM '{loserName}': {removed}");

                // 5. Refresh UI
                ApplyFilters(); // Refreshes the Mods view
                _npcSelectionBar.RefreshAppearanceSources();
            }
        }

        public void SignalScrollToMod(VM_ModSetting? modSetting)
        {
            if (modSetting != null)
            {
                Debug.WriteLine($"VM_Mods: Explicit signal to scroll to ModSetting {modSetting.DisplayName}");
                _requestScrollToModSubject.OnNext(modSetting);
            }
            else
            {
                _requestScrollToModSubject.OnNext(null);
            }
        }

        /// <summary>
        /// Called by VM_ModSetting when a single NPC's source plugin has changed.
        /// This might trigger a refresh of the mugshots if the display depends on the chosen source.
        /// </summary>
        public void NotifyNpcSourceChanged(VM_ModSetting modSetting, FormKey npcKey)
        {
            Debug.WriteLine(
                $"VM_Mods: Notified that source for NPC {npcKey} changed in ModSetting '{modSetting.DisplayName}'.");
            // If the affected modSetting is the one currently displayed for mugshots,
            // and the mugshot display logic considers the *chosen* source, then refresh.
            if (SelectedModForMugshots == modSetting)
            {
                // Reload mugshots for the selected mod
                ShowMugshotsCommand.Execute(modSetting).Subscribe().DisposeWith(_disposables);
            }
        }

        /// <summary>
        /// Called by VM_ModSetting when multiple NPC source plugins might have changed (e.g., by global set).
        /// </summary>
        public void NotifyMultipleNpcSourcesChanged(VM_ModSetting modSetting)
        {
            Debug.WriteLine(
                $"VM_Mods: Notified that multiple NPC sources may have changed in ModSetting '{modSetting.DisplayName}'.");
            if (SelectedModForMugshots == modSetting)
            {
                ShowMugshotsCommand.Execute(modSetting).Subscribe().DisposeWith(_disposables);
            }
        }

        // For passing plugin provider to sub-view-models (seems faster than doing it via AutoFac)
        public PluginProvider GetPluginProvider()
        {
            return _pluginProvider;
        }

        /// <summary>
        /// Asynchronously checks if any of the given mods in a folder modify NPC appearance.
        /// </summary>
        private async Task<bool> ContainsAppearancePluginsAsync(IEnumerable<ModKey> modKeysInMod,
            HashSet<string> modFolderPaths)
        {
            foreach (var modKey in modKeysInMod)
            {
                if (_environmentStateProvider.BaseGamePlugins.Contains(modKey))
                {
                    return true;
                }
                // TryGetPlugin is likely a fast, synchronous operation.
                if (_pluginProvider.TryGetPlugin(modKey, modFolderPaths, out var plugin) && plugin != null)
                {
                    bool pluginProvidesNewNpcs = false;
                    using (ContextualPerformanceTracer.Trace("PopulateMods.PluginProvidesNewNpcs"))
                    {
                        if (await PluginProvidesNewNpcs(plugin))
                        {
                            return true;
                        }
                    }

                    using (ContextualPerformanceTracer.Trace("PopulateMods.PluginModifiesAppearanceAsync"))
                    {
                        if ( await PluginModifiesAppearanceAsync(plugin, modKeysInMod))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private Task<bool> PluginProvidesNewNpcs(ISkyrimModGetter mod)
        {
            // Use Task.Run to execute the synchronous, CPU-bound logic on a thread pool thread.
            return Task.Run(() =>
            {
                foreach (var npc in mod.Npcs)
                {
                    if (npc.FormKey.ModKey.Equals(mod.ModKey))
                    {
                        return true; // not an overridden NPC
                    }
                }

                return false;
            });
        }

        /// <summary>
        /// Offloads the CPU-intensive work of checking for NPC appearance modifications to a background thread.
        /// </summary>
        private Task<bool> PluginModifiesAppearanceAsync(ISkyrimModGetter mod, IEnumerable<ModKey> allKeysInCurrentMod)
        {
            // Use Task.Run to execute the synchronous, CPU-bound logic on a thread pool thread.
            return Task.Run(() =>
            {
                var candidateMasters = mod.ModHeader.MasterReferences.Select(x => x.Master)
                    .Where(x => !allKeysInCurrentMod.Contains(x))
                    .ToHashSet();

                var enabledMasters = _environmentStateProvider.LoadOrder.PriorityOrder
                    .Where(x => candidateMasters.Contains(x.ModKey))
                    .ToList();

                var tempLinkCache = mod.ToImmutableLinkCache();
                foreach (var npc in mod.Npcs)
                {
                    if (npc.FormKey.ModKey.Equals(mod.ModKey))
                    {
                        continue; // not an overridden NPC
                    }

                    if (!tempLinkCache.TryResolve<INpcGetter>(npc.FormKey, out var npcGetter))
                    {
                        continue;
                    }

                    foreach (var listing in enabledMasters)
                    {
                        var baseNpcGetter = listing.Mod?.Npcs.FirstOrDefault(x => x.FormKey.Equals(npc.FormKey));
                        if (baseNpcGetter != null)
                        {
                            // A series of comparisons to check for appearance changes.
                            if ((npcGetter.FaceMorph != null && baseNpcGetter.FaceMorph == null) ||
                                (npcGetter.FaceMorph != null && !npcGetter.FaceMorph.Equals(baseNpcGetter.FaceMorph)) ||
                                (npcGetter.FaceParts != null && baseNpcGetter.FaceParts == null) ||
                                (npcGetter.FaceParts != null && !npcGetter.FaceParts.Equals(baseNpcGetter.FaceParts)) ||
                                !npcGetter.Height.Equals(baseNpcGetter.Height) ||
                                !npcGetter.Weight.Equals(baseNpcGetter.Weight) ||
                                !npcGetter.TextureLighting.Equals(baseNpcGetter.TextureLighting) ||
                                !npcGetter.HeadTexture.Equals(baseNpcGetter.HeadTexture) ||
                                !npcGetter.WornArmor.Equals(baseNpcGetter.WornArmor) ||
                                !npcGetter.HeadParts.Count.Equals(baseNpcGetter.HeadParts.Count) ||
                                !npcGetter.TintLayers.Count.Equals(baseNpcGetter.TintLayers.Count) ||
                                !npcGetter.HairColor.Equals(baseNpcGetter.HairColor)
                               )
                            {
                                return true;
                            }

                            foreach (var headPart in npcGetter.HeadParts)
                            {
                                if (!baseNpcGetter.HeadParts.Contains(headPart))
                                {
                                    return true;
                                }
                            }

                            foreach (var tintLayer in npcGetter.TintLayers)
                            {
                                if (!baseNpcGetter.TintLayers.Contains(tintLayer))
                                {
                                    return true;
                                }
                            }

                            break; // Analyzed highest priority mod containing this NPC; no need to look further
                        }
                    }
                }

                return false;
            });
        }
        
        /// <summary>
        /// Checks the number of potential appearance records vs. total records
        /// If most of the records are not related to NPC appearance, flag that this mod probably shouldn't be merged in
        /// </summary>
        private void CheckMergeInSuitability(VM_ModSetting modSettingVM)
        {
            int appearanceRecordCount = 0;
            int nonAppearanceRecordCount = 0;
            bool isBaseGame = false;

            // Define the set of appearance-related record types to skip in the main enumeration
            var appearanceTypesToSkip = new HashSet<Type>()
            {
                typeof(INpcGetter),
                typeof(IArmorGetter),
                typeof(IArmorAddonGetter),
                typeof(ITextureSetGetter),
                typeof(IHeadPartGetter),
                typeof(IHairGetter),
                typeof(IColorRecordGetter),
                typeof(IEyesGetter)
            };

            foreach (var modKey in modSettingVM.CorrespondingModKeys)
            {
                if (_environmentStateProvider.BaseGamePlugins.Contains(modKey) ||
                    _environmentStateProvider.CreationClubPlugins.Contains(modKey))
                {
                    isBaseGame = true; 
                    break;
                }
                
                if (!_pluginProvider.TryGetPlugin(modKey, modSettingVM.CorrespondingFolderPaths.ToHashSet(), out var plugin) || plugin == null)
                {
                    continue;
                }

                // Get counts of appearance records instantly (O(1) operation)
                appearanceRecordCount += plugin.Npcs.Count;
                appearanceRecordCount += plugin.Armors.Count;
                appearanceRecordCount += plugin.ArmorAddons.Count;
                appearanceRecordCount += plugin.TextureSets.Count;
                appearanceRecordCount += plugin.HeadParts.Count;
                appearanceRecordCount += plugin.Hairs.Count;
                appearanceRecordCount += plugin.Colors.Count;
                appearanceRecordCount += plugin.Eyes.Count;

                // Lazily enumerate and count ONLY the non-appearance records
                int nonAppearanceRecordCountForPlugin = Auxilliary.LazyEnumerateMajorRecords(plugin, appearanceTypesToSkip).Count();
                
                nonAppearanceRecordCount += nonAppearanceRecordCountForPlugin;
            }
            

            if (isBaseGame || nonAppearanceRecordCount > appearanceRecordCount)
            {
                modSettingVM.HasAlteredMergeLogic = true;
                modSettingVM.MergeInDependencyRecords = false;
                modSettingVM.MergeInToolTip =
                    $"N.P.C. has determined that the plugin(s) in {modSettingVM.DisplayName} have more non-appearance records than appearance records, " +
                    Environment.NewLine +
                    "suggesting that it's not just an appearance replacer mod. Merge-in has been disabled by default. You can re-enable it, but be warned that " +
                    Environment.NewLine +
                    "merging in large plugins with a lot of non-appearance records can freeze the patcher and is completely unnecessary if the plugin is staying in your load order" +
                    Environment.NewLine +
                    "and you're just making sure its NPC appearances are winning conflicts." + Environment.NewLine +
                    Environment.NewLine + ModSetting.DefaultMergeInTooltip;
            }
        }

        public ConcurrentDictionary<(string pluginSourcePath, ModKey modKey), bool> GetOverrideCache()
        {
            return _overridesCache;
        }

        public void UpdateTemplates(FormKey npcFormKey, VM_ModSetting modSettingVM)
        {
            int maxCycleCount = 50; // this should be way overkill
            List<(FormKey formKey, string displayName)> templateChain = new();
            List<string> errorMessages = new();

            Dictionary<ModKey, ISkyrimModGetter> plugins = new();
            foreach (var modKey in modSettingVM.CorrespondingModKeys)
            {
                if (_pluginProvider.TryGetPlugin(modKey, modSettingVM.CorrespondingFolderPaths.ToHashSet(),
                        out var plugin) && plugin != null)
                {
                    plugins.Add(modKey, plugin);
                }
            }

            int cycleCount = 0;
            ISkyrimModGetter sourcePlugin;
            while (cycleCount < maxCycleCount)
            {
                var availablePlugins = modSettingVM.AvailablePluginsForNpcs.TryGetValue(npcFormKey);
                if (availablePlugins is not null && availablePlugins.Any())
                {
                    if (availablePlugins.Count == 1)
                    {
                        if (plugins.TryGetValue(availablePlugins.First(), out var plugin))
                        {
                            sourcePlugin = plugin;
                        }
                        else
                        {
                            errorMessages.Add($"Could not find plugin {availablePlugins.First()} for {npcFormKey} within {modSettingVM.DisplayName}.");
                            break;
                        }
                    }
                    else if (modSettingVM.NpcPluginDisambiguation.TryGetValue(npcFormKey, out var disambiguation))
                    {
                        if (plugins.TryGetValue(disambiguation, out var plugin))
                        {
                            sourcePlugin = plugin;
                        }
                        else
                        {
                            errorMessages.Add($"Could not find plugin {disambiguation} for {npcFormKey} within {modSettingVM.DisplayName}.");
                            break;
                        }
                    }
                    else
                    {
                        errorMessages.Add($"Could not determine source plugin for {npcFormKey} within plugin {modSettingVM.DisplayName}: [{string.Join(", ", availablePlugins)}])");
                        break;
                    }
                }
                else
                {
                    errorMessages.Add($"Could not find any available plugins for {npcFormKey} within {modSettingVM.DisplayName}");
                    break;
                }

                if (sourcePlugin != null)
                {
                    var npc = sourcePlugin.Npcs.Where(x => x.FormKey.Equals(npcFormKey)).FirstOrDefault();
                    if (npc is null)
                    {
                        errorMessages.Add($"Could not find {npcFormKey} in {sourcePlugin.ModKey.FileName} even though analysis indicates it should be there");
                        break;
                    }

                    var newEntry = (npc.FormKey, Auxilliary.GetNpcLogString(npc, true));
                    templateChain.Add(newEntry);
                    
                    if (npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits))
                    {
                        if (npc.Template is null || npc.Template.IsNull)
                        {
                            errorMessages.Add($"The appearance template for {Auxilliary.GetNpcLogString(npc)} in {sourcePlugin.ModKey.FileName} is blank despite it having a Traits template flag");
                            break;
                        }
                        else
                        {
                            npcFormKey = npc.Template.FormKey; // repeat for the next template
                        }
                    }
                    else
                    {
                        break; // template chain stops here
                    }
                }
            }

            if (templateChain.Any())
            {
                StringBuilder message = new();
                message.AppendLine(
                    "This NPC inherits appearance from a template, which means that it needs to come from the same mod as the template.");
                message.AppendLine($"Template Chain: {string.Join(" -> ", templateChain.Select(x => x.displayName))}");
                message.AppendLine();
                if (errorMessages.Any())
                {
                    message.AppendLine("Note: the following error(s) occured when analyzing the template chain:");
                    message.AppendLine(string.Join(Environment.NewLine, errorMessages));
                }
                message.AppendLine();
                message.AppendLine("Would you like to apply this mod selection for all NPCs in the chain?");

                if (ScrollableMessageBox.Confirm(message.ToString(), "Update template chain?"))
                {
                    int index = 0;
                    foreach (var entry in templateChain)
                    {
                        index++;
                        if(index == 1) { continue; } // the current mugshot has already been set by the caller
                        _consistencyProvider.SetSelectedMod(entry.formKey, modSettingVM.DisplayName, entry.formKey);
                    }
                }
            }
        }

        private ModStateSnapshot? GenerateSnapshot(VM_ModSetting vm)
        {
            try
            {
                var snapshot = new ModStateSnapshot();
                var allPaths = new HashSet<string>(vm.CorrespondingFolderPaths, StringComparer.OrdinalIgnoreCase);
                if (vm.IsAutoGenerated)
                {
                    allPaths.Add(_environmentStateProvider.DataFolderPath);
                }

                // 1. Snapshot Plugins
                foreach (var modKey in vm.CorrespondingModKeys)
                {
                    if (_pluginProvider.TryGetPlugin(modKey, allPaths, out _, out var path) && path != null)
                    {
                        var info = new FileInfo(path);
                        snapshot.PluginSnapshots.Add(new FileSnapshot
                            { FileName = info.Name, FileSize = info.Length, LastWriteTimeUtc = info.LastWriteTimeUtc });
                    }
                }

                // 2. Snapshot BSAs
                var bsaPaths = _bsaHandler
                    .GetBsaPathsForPluginsInDirs(vm.CorrespondingModKeys, allPaths,
                        _settings.SkyrimRelease.ToGameRelease()).Values.SelectMany(p => p).Distinct();
                foreach (var bsaPath in bsaPaths)
                {
                    var info = new FileInfo(bsaPath);
                    if (info.Exists)
                    {
                        snapshot.BsaSnapshots.Add(new FileSnapshot
                            { FileName = info.Name, FileSize = info.Length, LastWriteTimeUtc = info.LastWriteTimeUtc });
                    }
                }

                // 3. Snapshot Directories (for loose FaceGen)
                foreach (var modPath in vm.CorrespondingFolderPaths)
                {
                    string faceGeomPath = Path.Combine(modPath, "meshes", "actors", "character", "facegendata",
                        "facegeom");
                    string faceTintPath = Path.Combine(modPath, "textures", "actors", "character", "facegendata",
                        "facetint");

                    foreach (var dirPath in new[] { faceGeomPath, faceTintPath })
                    {
                        var dirInfo = new DirectoryInfo(dirPath);
                        if (dirInfo.Exists)
                        {
                            snapshot.DirectorySnapshots.Add(new DirectorySnapshot
                            {
                                Path = dirInfo.FullName,
                                FileCount = Directory.EnumerateFiles(dirInfo.FullName, "*", SearchOption.AllDirectories)
                                    .Count(),
                                LastWriteTimeUtc = dirInfo.LastWriteTimeUtc
                            });
                        }
                    }
                }

                return snapshot;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to generate snapshot for {vm.DisplayName}: {ex.Message}");
                return null; // Return null on failure to ensure re-analysis
            }
        }
    }
}