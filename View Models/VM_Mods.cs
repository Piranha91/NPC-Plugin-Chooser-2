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
                CacheFaceGenPathsOnLoad(); //////////////////////////////////////////////////////////////////////////
            Task.Run(() => newVm.RefreshNpcLists(allFaceGenLooseFiles, allFaceGenBsaFiles));
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
            _allModSettingsInternal.Clear();
            _overridesCache.Clear();
            var warnings = new List<string>();
            var loadedDisplayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tempList = new List<VM_ModSetting>();
            IsLoadingNpcData = true;
            var claimedMugshotPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!_environmentStateProvider.EnvironmentIsValid || _environmentStateProvider.LoadOrder == null)
                warnings.Add("Environment is not valid. Cannot accurately link plugins.");

            splashReporter?.UpdateStep("Processing configured mod settings...");

            // first load mods from settings that already exist
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
                        if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) &&
                            Directory.Exists(_settings.MugshotsFolder))
                        {
                            string potentialPathByName = Path.Combine(_settings.MugshotsFolder, vm.DisplayName);
                            if (Directory.Exists(potentialPathByName) &&
                                !claimedMugshotPaths.Contains(potentialPathByName))
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

            // then load mods from mugshots (whether they exist in the Mods directory or not).
            splashReporter?.UpdateStep("Scanning for new mod folders...");
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
                                    vmsFromMugshotsOnly.Add(
                                        _modSettingFromMugshotPathFactory(folderName, dirPath, this));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add(
                            $"Error scanning Mugshots folder '{_settings.MugshotsFolder}': {Environment.NewLine}{ExceptionLogger.GetExceptionStack(ex)}");
                    }
                }
            }


            // Then load mods from mods folder (e.g. mods for which mugshots are not installed)
            splashReporter?.UpdateProgress(20, "Scanning for new mod folders...");
            using (ContextualPerformanceTracer.Trace("PopulateMods.ScanModFolders"))
            {
                if (!string.IsNullOrWhiteSpace(_settings.ModsFolder) && Directory.Exists(_settings.ModsFolder))
                {
                    try
                    {
                        const string tokenFileName = "NPC_Token.json"; // Define the token file name
                        var modDirectories = Directory.EnumerateDirectories(_settings.ModsFolder).ToList();
                        var totalModFolders = modDirectories.Count;
                        var scannedModFolders = 0;
                        
                        foreach (var modFolderPath in Directory.EnumerateDirectories(_settings.ModsFolder))
                        {
                            scannedModFolders++;
                            string modFolderName = Path.GetFileName(modFolderPath);
                            var progress = (double)scannedModFolders / totalModFolders * 100.0;
                            splashReporter?.UpdateProgress(progress, $"Scanning: {modFolderName}");


                            string tokenFilePath = Path.Combine(modFolderPath, tokenFileName);
                            if (File.Exists(tokenFilePath))
                            {
                                Debug.WriteLine(
                                    $"Skipping directory '{Path.GetFileName(modFolderPath)}' as it contains a token file and appears to be a previous patcher output.");
                                continue; // Skip this directory and move to the next one
                            }

                            if (_settings.CachedNonAppearanceMods.Contains(modFolderPath))
                            {
                                continue;
                            }

                            var modKeysInFolder = _aux.GetModKeysInDirectory(modFolderPath, warnings, false);

                            var existingVmFromSettings = tempList.FirstOrDefault(vm =>
                                vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));
                            var mugshotOnlyVmToUpgrade = vmsFromMugshotsOnly.FirstOrDefault(vm =>
                                vm.DisplayName.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));

                            if (existingVmFromSettings != null)
                            {
                                if (!existingVmFromSettings.CorrespondingFolderPaths.Contains(modFolderPath,
                                        StringComparer.OrdinalIgnoreCase))
                                    existingVmFromSettings.CorrespondingFolderPaths.Add(modFolderPath);
                                foreach (var key in modKeysInFolder)
                                    if (!existingVmFromSettings.CorrespondingModKeys.Contains(key))
                                        existingVmFromSettings.CorrespondingModKeys.Add(key);
                            }
                            else if (mugshotOnlyVmToUpgrade != null)
                            {
                                mugshotOnlyVmToUpgrade.CorrespondingFolderPaths.Add(modFolderPath);
                                foreach (var key in modKeysInFolder)
                                    if (!mugshotOnlyVmToUpgrade.CorrespondingModKeys.Contains(key))
                                        mugshotOnlyVmToUpgrade.CorrespondingModKeys.Add(key);
                                tempList.Add(mugshotOnlyVmToUpgrade);
                                vmsFromMugshotsOnly.Remove(mugshotOnlyVmToUpgrade);
                                loadedDisplayNames.Add(mugshotOnlyVmToUpgrade.DisplayName);
                            }
                            else
                            {
                                FaceGenScanResult scanResult;
                                using (ContextualPerformanceTracer.Trace("PopulateMods.CollectFaceGenFilesAsync"))
                                {
                                    scanResult = await FaceGenScanner.CollectFaceGenFilesAsync(modFolderPath,
                                        _bsaHandler,
                                        modKeysInFolder, _environmentStateProvider.SkyrimVersion.ToGameRelease());
                                }

                                if (scanResult.AnyFilesFound)
                                {
                                    var faceGenFiles = scanResult.FaceGenFiles;
                                    var newVm = _modSettingFromModFolderFactory(modFolderPath, modKeysInFolder, this);
                                    newVm.IsNewlyCreated = true;

                                    // load resources
                                    using (ContextualPerformanceTracer.Trace("PopulateMods.LoadPlugins"))
                                    {
                                        _pluginProvider.LoadPlugins(modKeysInFolder,
                                            new HashSet<string> { modFolderPath });
                                    }
                                    //

                                    if (modKeysInFolder.Any() &&
                                        await ContainsAppearancePluginsAsync(modKeysInFolder, new() { modFolderPath })) // profiling is internal
                                    {
                                        string potentialMugshotPath =
                                            Path.Combine(_settings.MugshotsFolder, newVm.DisplayName);
                                        if (Directory.Exists(potentialMugshotPath) &&
                                            !claimedMugshotPaths.Contains(potentialMugshotPath))
                                        {
                                            newVm.MugShotFolderPath = potentialMugshotPath;
                                            claimedMugshotPaths.Add(potentialMugshotPath);
                                        }

                                        tempList.Add(newVm);
                                        loadedDisplayNames.Add(newVm.DisplayName);
                                    }
                                    else if (modKeysInFolder.Any())
                                    {
                                        _settings.CachedNonAppearanceMods.Add(modFolderPath);
                                    }
                                    else
                                    {
                                        newVm.IsFaceGenOnlyEntry = true;

                                        foreach (var pluginName in faceGenFiles.Keys)
                                        {
                                            var modKey = ModKey.FromFileName(pluginName);
                                            newVm.CorrespondingModKeys.Add(modKey);

                                            var NpcIds = faceGenFiles[pluginName];
                                            foreach (var id in NpcIds)
                                            {
                                                if (id.Length != 8)
                                                {
                                                    continue;
                                                } // not a FormID

                                                var subId = id.Substring(2, 6);
                                                var formKeyStr = subId + ":" + pluginName;
                                                if (FormKey.TryFactory(formKeyStr, out var formKey))
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
                                else
                                {
                                    _settings.CachedNonAppearanceMods.Add(modFolderPath);
                                }
                            }

                            // Unload Resources
                            _pluginProvider.UnloadPlugins(modKeysInFolder);
                            //
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add(
                            $"Error scanning Mods folder '{_settings.ModsFolder}': {Environment.NewLine}{ExceptionLogger.GetExceptionStack(ex)}");
                    }
                }
            }

            foreach (var mugshotVm in vmsFromMugshotsOnly)
                if (!tempList.Any(existing =>
                        existing.DisplayName.Equals(mugshotVm.DisplayName, StringComparison.OrdinalIgnoreCase)))
                {
                    tempList.Add(mugshotVm);
                }

            foreach (var vm in tempList)
            {
                if (vm.IsMugshotOnlyEntry && (vm.CorrespondingFolderPaths.Any() || vm.CorrespondingModKeys.Any()))
                {
                    vm.IsMugshotOnlyEntry = false;
                }
            }
            // --- End of existing logic ---

            // Add base game plugins if not handled by mods (e.g. cleaned versions of the plugins)

            var baseGameModKeys = _environmentStateProvider.BaseGamePlugins.ToHashSet(); // copy to allow set modification
            var creationClubModKeys = _environmentStateProvider.CreationClubPlugins.ToHashSet(); // copy to allow set modification

            baseGameModKeys.RemoveWhere(mk => tempList.Any(vm => !vm.IsFaceGenOnlyEntry && !vm.IsMugshotOnlyEntry && vm.CorrespondingModKeys.Contains(mk)));
            creationClubModKeys.RemoveWhere(mk => tempList.Any(vm => !vm.IsFaceGenOnlyEntry && !vm.IsMugshotOnlyEntry && vm.CorrespondingModKeys.Contains(mk)));

            if (creationClubModKeys.Any())
            {
                // Check if a mod setting with the Creation Club name already exists.
                var existingCcMod = tempList.FirstOrDefault(vm =>
                    vm.DisplayName.Equals(CreationClubModsettingName, StringComparison.OrdinalIgnoreCase));

                if (existingCcMod == null)
                {
                    // Only create and add a new one if it doesn't already exist.
                    // Create the json model first and load data from there to select the correct constructor
                    var ccMod = new ModSetting()
                    {
                        DisplayName = CreationClubModsettingName,
                        CorrespondingModKeys = creationClubModKeys.ToList(),
                        IsAutoGenerated = true,
                        MergeInDependencyRecords = false
                    };
                    var ccModVm = _modSettingFromModelFactory(ccMod, this);
                    ccModVm.MergeInDependencyRecordsVisible = false;
                    tempList.Add(ccModVm);
                }
            }

            if (baseGameModKeys.Any())
            {
                // Check if a mod setting with the Base Game name already exists.
                var existingBaseMod = tempList.FirstOrDefault(vm =>
                    vm.DisplayName.Equals(BaseGameModSettingName, StringComparison.OrdinalIgnoreCase));

                if (existingBaseMod == null)
                {
                    // Only create and add a new one if it doesn't already exist.
                    // Create the json model first and load data from there to select the correct constructor
                    var baseMod = new ModSetting()
                    {
                        DisplayName = BaseGameModSettingName,
                        CorrespondingModKeys = baseGameModKeys.ToList(),
                        IsAutoGenerated = true,
                        MergeInDependencyRecords = false
                    };
                    var baseModVm = _modSettingFromModelFactory(baseMod, this);
                    baseModVm.MergeInDependencyRecordsVisible = false;
                    tempList.Add(baseModVm);
                }
            }

            _allModSettingsInternal.Clear();
            _allModSettingsInternal.AddRange(SortVMs(tempList));

            splashReporter?.UpdateStep("Pre-caching asset file paths...");
            (var allFaceGenLooseFiles, var allFaceGenBsaFiles) = CacheFaceGenPathsOnLoad();

            splashReporter?.UpdateStep("Analyzing mod data...");
            // Limit to the number of logical processors to balance CPU work and I/O
            var maxParallelism = Environment.ProcessorCount;
            var semaphore = new SemaphoreSlim(maxParallelism);

            var modSettingsToLogCount = _allModSettingsInternal.Where(x => x.IsNewlyCreated).Count();
            var analyzedCount = 0;
            var refreshTasks = _allModSettingsInternal.Select(async (vm, index) =>
            {
                await semaphore.WaitAsync(); // Wait for a free slot
                try
                {
                    await Task.Run(async () =>
                    {
                        // Get the paths for this specific VM
                        var modFolderPathsForVm = vm.CorrespondingFolderPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            
                        // Explicitly load only the plugins needed for this task
                        _pluginProvider.LoadPlugins(vm.CorrespondingModKeys, modFolderPathsForVm);
                        try
                        {
                            var currentAnalyzed = Interlocked.Increment(ref analyzedCount);
                            var progress = (double)currentAnalyzed / modSettingsToLogCount * 100.0;
                            splashReporter?.UpdateProgress(progress, $"Analyzing: {vm.DisplayName}");
                            
                            // Perform the analysis using the now-cached plugins
                            using (ContextualPerformanceTracer.Trace("RefreshNpcLists"))
                            {
                                // Perform the analysis using the now-cached plugins
                                vm.RefreshNpcLists(allFaceGenLooseFiles, allFaceGenBsaFiles);
                            }

                            if (vm.IsNewlyCreated)
                            {
                                using (ContextualPerformanceTracer.Trace("FindPluginsWithOverrides"))
                                {
                                    await vm.FindPluginsWithOverrides(_pluginProvider);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error during analysis for {vm.DisplayName}: {ex.Message}");
                        }
                        finally
                        {
                            // CRUCIAL: Unload the plugins after analysis is complete,
                            // even if an error occurred.
                            _pluginProvider.UnloadPlugins(vm.CorrespondingModKeys);
                        }
                    });
                }
                finally
                {
                    semaphore.Release(); // Release the slot
                }
            }).ToList();

            try
            {
                await Task.WhenAll(refreshTasks);

                splashReporter?.UpdateStep("Finalizing mod settings...");

                // Phase 4: Run UI-dependent work on the UI thread
                // Use Dispatcher.InvokeAsync for WPF
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    foreach (var vm in _allModSettingsInternal)
                    {
                        // Now, create the appropriate brush on the UI thread
                        if (vm.HasAlteredMergeLogic)
                        {
                            vm.MergeInDependencyRecords = false;
                            vm.MergeInLabelColor = new(System.Windows.Media.Colors.Purple);
                        }
                        else
                        {
                            // Assign the default brush here, on the UI thread
                            vm.MergeInLabelColor = new(System.Windows.Media.Colors.Black);
                        }
                        
                        RecalculateMugshotValidity(vm);
                    }

                    IsLoadingNpcData = false;
                    ApplyFilters();

                    if (warnings.Any())
                    {
                        ScrollableMessageBox.ShowWarning(string.Join("\n", warnings),
                            "Mod Settings Population Warning");
                    }
                    // If RecalculateMugshotValidity or ApplyFilters were async and returned Task,
                    // you could await them here. Since they are not, the async () => is for InvokeAsync.
                });
            }
            catch (AggregateException aggEx)
            {
                foreach (var ex in aggEx.Flatten().InnerExceptions)
                {
                    Debug.WriteLine($"Async NPC list refresh error (outer): {ex.Message}");
                    // Safely add to warnings on UI thread (if warnings is a UI bound collection, otherwise direct add is fine)
                    Application.Current.Dispatcher.Invoke(() =>
                        warnings.Add($"Async NPC list refresh error: {ex.Message}"));
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

                var bsaDict = BsaHandler.GetBsaPathsForPluginsInDirs(vm.CorrespondingModKeys, pathsToSearch,
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
        /// <param name="foundVm">Output: The found VM_ModSetting instance if a match exists; otherwise, null.</param>
        /// <param name="modDisplayName">Output: The display name of the found VM_ModSetting if found; otherwise, defaults to the input plugin's filename.</param>
        /// <returns>True if a matching VM_ModSetting was found based on the CorrespondingModKey, false otherwise.</returns>
        public bool TryGetModSettingForPlugin(ModKey appearancePluginKey, out VM_ModSetting? foundVm,
            out string modDisplayName)
        {
            // Initialize output parameters
            foundVm = null;
            // Default the display name to the filename from the input key.
            // This will be used if the VM is not found or if the key is invalid.
            // Use ?? to handle potential null FileName (though unlikely for valid ModKey)
            modDisplayName = appearancePluginKey.FileName;

            // Check if the input ModKey is valid (not null or default)
            if (appearancePluginKey.IsNull)
            {
                Debug.WriteLine($"TryGetModSettingForPlugin: Received an invalid (IsNull) ModKey.");
                // Keep foundVm as null and modDisplayName as the default set above.
                return false; // Cannot find a match for an invalid key.
            }

            // Search the internal list of all loaded/created mod settings.
            // Find the first VM where *any* of its CorrespondingModKeys matches the input key.
            foundVm = _allModSettingsInternal.FirstOrDefault(vm =>
                vm.CorrespondingModKeys.Any(key => key.Equals(appearancePluginKey)));
            // Check if a VM was found
            if (foundVm != null)
            {
                // A matching VM was found. Update the output display name to the actual display name of the found VM.
                modDisplayName = foundVm.DisplayName;
                // Log success for debugging if needed.
                // Debug.WriteLine($"TryGetModSettingForPlugin: Found existing VM '{modDisplayName}' for key '{appearancePluginKey}'.");
                return true; // Indicate success.
            }
            else
            {
                // No VM with a matching CorrespondingModKey was found in the list.
                // Log failure for debugging if needed.
                // Debug.WriteLine($"TryGetModSettingForPlugin: No VM found for key '{appearancePluginKey}'.");
                // Keep foundVm as null and modDisplayName as the default filename set earlier.
                return false; // Indicate failure.
            }
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
                    CacheFaceGenPathsOnLoad(); ///////////////////////////////////////////////////////////////////////
                Task.Run(() => winner.RefreshNpcLists(allFaceGenLooseFiles, allFaceGenBsaFiles));

                // 2. Update NPC Selections (_model.SelectedAppearanceMods via _consistencyProvider)
                string loserName = loser.DisplayName;
                string winnerName = winner.DisplayName;
                var npcsToUpdate = _settings.SelectedAppearanceMods
                    .Where(kvp => kvp.Value.Equals(loserName, StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key) // Get FormKeys of NPCs assigned to the loser
                    .ToList(); // Materialize the list before modifying the dictionary

                if (npcsToUpdate.Any())
                {
                    Debug.WriteLine(
                        $"Updating {npcsToUpdate.Count} NPC selections from '{loserName}' to '{winnerName}'.");
                    foreach (var npcKey in npcsToUpdate)
                    {
                        // Use consistency provider to update both cache and _settings model
                        _consistencyProvider.SetSelectedMod(npcKey, winnerName);
                    }
                }


                // 3. Remove Loser VM
                bool removed = _allModSettingsInternal.Remove(loser);
                Debug.WriteLine($"Removed loser VM '{loserName}': {removed}");


                // 4. Refresh UI
                ApplyFilters();


                // 5. Inform User
                ScrollableMessageBox.Show(
                    $"Automatically merged mod settings:\n\n'{loserName}' was merged into '{winnerName}'.\n\nNPC assignments using '{loserName}' have been updated.",
                    "Mod Settings Merged"
                );
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
            int totalRecordCount = 0;
            int appearanceRecordCount = 0;
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
                
                totalRecordCount += appearanceRecordCount + nonAppearanceRecordCountForPlugin;
            }

            int nonAppearanceRecordCount = totalRecordCount - appearanceRecordCount;

            if (isBaseGame || nonAppearanceRecordCount > totalRecordCount / 2)
            {
                modSettingVM.HasAlteredMergeLogic = true; 
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
    }
}