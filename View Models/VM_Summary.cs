// View Models/VM_Summary.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows.Forms;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

    public class VM_Summary : ReactiveObject, IDisposable, IActivatableViewModel
    {
        // Dependencies
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly PortraitCreator _portraitCreator;
        private readonly FaceFinderClient _faceFinderClient;
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly VM_NpcSelectionBar _npcsViewModel;
        private readonly VM_Mods _modsViewModel;
        private readonly Lazy<VM_MainWindow> _lazyMainWindowVm;
        private readonly SummaryMugshotFactory _summaryMugshotFactory;
        private readonly BatchMugshotGenerator _batchMugshotGenerator;
        private readonly CompositeDisposable _disposables = new();
        private CancellationTokenSource? _summaryImageLoadCts;
        
        // Holds only what can be computed from in-memory state. The mugshot path is
        // resolved per page in UpdateDisplay: it needs disk probes (and a PNG metadata
        // read for autogen staleness), which is too slow to do for every selection.
        private record SummaryNpcData(FormKey TargetNpcFormKey, FormKey SourceNpcFormKey, string NpcDisplayName, string ModDisplayName, string SourceNpcDisplayName, bool IsGuest, VM_ModSetting? ModSetting, bool IsAmbiguous, bool HasIssue, string IssueText, bool HasNoData, string NoDataText);
        private List<SummaryNpcData> _allNpcData = new();
        private List<VM_SummaryListItem> _allListItems = new();
        private readonly ISubject<Unit> _refreshImageSizesSubject = new Subject<Unit>();
        
        public ViewModelActivator Activator { get; } = new ViewModelActivator();
        
        public int MaxMugshotsToFit => _settings.MaxMugshotsToFit;

        // --- Top Row Properties ---
        public enum SummaryViewMode
        {
            Gallery,
            List
        }
        [Reactive] public SummaryViewMode ViewMode { get; set; } = SummaryViewMode.Gallery;

        public ObservableCollection<string> AvailableNpcGroups { get; } = new();
        [Reactive] public string SelectedNpcGroup { get; set; }
        [Reactive] public int MaxNpcsPerPage { get; set; }

        // --- Main Display Properties ---
        // Separate collections so each view mode's control only observes its own items.
        // The gallery's WrapPanel doesn't virtualize, so sharing one collection made it
        // build a container for every list row while hidden.
        public ObservableCollection<VM_SummaryMugshot> GalleryItems { get; } = new();
        // Replaced wholesale (one rebind) rather than filled item-by-item, so the
        // DataGrid doesn't process a CollectionChanged event per row.
        [Reactive] public List<VM_SummaryListItem> ListItems { get; private set; } = new();

        // --- Bottom Row / Pagination Properties ---
        [Reactive] public int CurrentPage { get; set; } = 1;
        [Reactive] public int TotalPages { get; set; } = 1;
        public string PageDisplay => $"Page {CurrentPage} of {TotalPages}";

        // --- Zoom Control Properties ---
        [Reactive] public double SummaryViewZoomLevel { get; set; }
        [Reactive] public bool SummaryViewIsZoomLocked { get; set; }
        public IObservable<Unit> RefreshImageSizesObservable => _refreshImageSizesSubject.AsObservable();
        [Reactive] public bool SummaryViewHasUserManuallyZoomed { get; set; } = false;
        private const double _minZoomPercentage = 1.0;
        private const double _maxZoomPercentage = 1000.0;
        private const double _zoomStepPercentage = 2.5;

        // --- Commands ---
        public ReactiveCommand<Unit, Unit> NextPageCommand { get; }
        public ReactiveCommand<Unit, Unit> PreviousPageCommand { get; }
        public ReactiveCommand<Unit, Unit> ZoomInSummaryCommand { get; }
        public ReactiveCommand<Unit, Unit> ZoomOutSummaryCommand { get; }
        public ReactiveCommand<Unit, Unit> ResetZoomSummaryCommand { get; }
        
        public delegate VM_SummaryMugshot SummaryMugshotFactory(
            string imagePath,
            FormKey targetNpcFormKey,
            FormKey sourceNpcFormKey,
            string npcDisplayName,
            string modDisplayName,
            string sourceNpcDisplayName,
            bool isGuest, bool isAmbiguous, bool hasIssue, string issueText, bool hasNoData, string noDataText, bool hasMugshot,
            VM_ModSetting? associatedModSetting
        );

        public VM_Summary(
            Settings settings,
            NpcConsistencyProvider consistencyProvider,
            EnvironmentStateProvider environmentStateProvider,
            FaceFinderClient faceFinderClient,
            PortraitCreator portraitCreator,
            VM_NpcSelectionBar npcsViewModel,
            VM_Mods modsViewModel,
            Lazy<VM_MainWindow> lazyMainWindowVm,
            SummaryMugshotFactory summaryMugshotFactory,
            BatchMugshotGenerator batchMugshotGenerator)
        {
            _settings = settings;
            _consistencyProvider = consistencyProvider;
            _environmentStateProvider = environmentStateProvider;
            _faceFinderClient = faceFinderClient;
            _portraitCreator = portraitCreator;
            _npcsViewModel = npcsViewModel;
            _modsViewModel = modsViewModel;
            _lazyMainWindowVm = lazyMainWindowVm;
            _summaryMugshotFactory = summaryMugshotFactory;
            _batchMugshotGenerator = batchMugshotGenerator;

            MaxNpcsPerPage = _settings.MaxNpcsPerPageSummaryView;
            this.WhenAnyValue(x => x.MaxNpcsPerPage)
                .Throttle(TimeSpan.FromMilliseconds(500))
                .Subscribe(val => _settings.MaxNpcsPerPageSummaryView = val)
                .DisposeWith(_disposables);

            // Populate NPC groups from the Npcs View Model
            AvailableNpcGroups = _npcsViewModel.AvailableNpcGroups;
            SelectedNpcGroup = AvailableNpcGroups.FirstOrDefault() ?? "All NPCs";

            // --- Pagination Commands ---
            var canGoNext = this.WhenAnyValue(x => x.CurrentPage, x => x.TotalPages, (c, t) => c < t);
            NextPageCommand = ReactiveCommand.Create(() => { CurrentPage++; }, canGoNext).DisposeWith(_disposables);;
            var canGoPrev = this.WhenAnyValue(x => x.CurrentPage, c => c > 1);
            PreviousPageCommand = ReactiveCommand.Create(() => { CurrentPage--; }, canGoPrev).DisposeWith(_disposables);;

            // --- Zoom Commands & Properties ---
            SummaryViewZoomLevel = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, _settings.SummaryViewZoomLevel));
            SummaryViewIsZoomLocked = _settings.SummaryViewIsZoomLocked;

            ZoomInSummaryCommand = ReactiveCommand.Create(() =>
            {
                SummaryViewHasUserManuallyZoomed = true;
                SummaryViewZoomLevel = Math.Min(_maxZoomPercentage, SummaryViewZoomLevel + _zoomStepPercentage);
            }).DisposeWith(_disposables);;
            ZoomOutSummaryCommand = ReactiveCommand.Create(() =>
            {
                SummaryViewHasUserManuallyZoomed = true;
                SummaryViewZoomLevel = Math.Max(_minZoomPercentage, SummaryViewZoomLevel - _zoomStepPercentage);
            }).DisposeWith(_disposables);;
            ResetZoomSummaryCommand = ReactiveCommand.Create(() =>
            {
                SummaryViewIsZoomLocked = false;
                SummaryViewHasUserManuallyZoomed = false;
                _refreshImageSizesSubject.OnNext(Unit.Default);
            }).DisposeWith(_disposables);;

            this.WhenAnyValue(x => x.SummaryViewZoomLevel)
                .Skip(1) // Don't fire on initial load
                .Throttle(TimeSpan.FromMilliseconds(100))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(zoom =>
                {
                    // Clamp the value to prevent invalid zoom levels
                    double clampedZoom = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, zoom));
                    _settings.SummaryViewZoomLevel = clampedZoom;

                    // If the value was clamped, update the ViewModel property to reflect it
                    if (Math.Abs(SummaryViewZoomLevel - clampedZoom) > 0.001)
                    {
                        SummaryViewZoomLevel = clampedZoom;
                        return; // The property change will re-trigger this subscription, so we exit
                    }

                    // If the user is manually zooming or the zoom is locked,
                    // explicitly signal the view to refresh the image sizes.
                    if (SummaryViewIsZoomLocked || SummaryViewHasUserManuallyZoomed)
                    {
                        _refreshImageSizesSubject.OnNext(Unit.Default);
                    }
                })
                .DisposeWith(_disposables);

            this.WhenAnyValue(x => x.SummaryViewIsZoomLocked)
                .Skip(1)
                .Subscribe(isLocked =>
                {
                    _settings.SummaryViewIsZoomLocked = isLocked;
                    // When locking/unlocking, always refresh the view to apply the correct scaling
                    _refreshImageSizesSubject.OnNext(Unit.Default);
                })
                .DisposeWith(_disposables);

            // Trigger updates
            this.WhenAnyValue(x => x.CurrentPage, x => x.ViewMode, x => x.SelectedNpcGroup, x => x.MaxNpcsPerPage)
                .Throttle(TimeSpan.FromMilliseconds(50))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => UpdateDisplay())
                .DisposeWith(_disposables);

            // Update page display text
            this.WhenAnyValue(x => x.CurrentPage, x => x.TotalPages)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(PageDisplay)))
                .DisposeWith(_disposables);

            // Load data only when the user switches to the corresponding menu
            this.WhenActivated((CompositeDisposable disposables) =>
            {
                // InitializeData is now fast, as it does no image I/O
                InitializeData();

                this.RaisePropertyChanged(nameof(ViewMode));

                // UpdateDisplay will create the VMs for the first page
                UpdateDisplay();
            });
        }

        /// <summary>
        /// Forces a rebuild of the summary data + display VMs. Mirrors the
        /// WhenActivated body so callers (e.g. VM_MainWindow on tab switch
        /// after a MugshotSourcePriority change) can refresh without waiting
        /// for the next activation cycle.
        /// </summary>
        public void RefreshDisplay()
        {
            InitializeData();
            UpdateDisplay();
        }

        private void InitializeData()
        {
            // Create a lookup map from the AllNpcs list for efficient name retrieval.
            var npcViewModelMap = _npcsViewModel.AllNpcs.ToDictionary(npc => npc.NpcFormKey);
            
            var allSelections = _settings.SelectedAppearanceMods;
            
            // First match wins, matching the FirstOrDefault lookup this replaces.
            var modSettingsByName = new Dictionary<string, VM_ModSetting>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in _modsViewModel.AllModSettings)
            {
                modSettingsByName.TryAdd(mod.DisplayName, mod);
            }

            _allNpcData = new List<SummaryNpcData>();
            _allListItems = new List<VM_SummaryListItem>();

            foreach (var selection in allSelections)
            {
                var targetNpcKey = selection.Key;
                var (modName, sourceNpcKey) = selection.Value;
                
                string targetNpcName = npcViewModelMap.TryGetValue(targetNpcKey, out var tNpc) ? tNpc.DisplayName : targetNpcKey.ToString();
                string sourceNpcName = npcViewModelMap.TryGetValue(sourceNpcKey, out var sNpc) ? sNpc.DisplayName : sourceNpcKey.ToString();

                bool isGuest = !targetNpcKey.Equals(sourceNpcKey);

                // For mugshot view, we need more details
                modSettingsByName.TryGetValue(modName, out var modSetting);

                // Decorator info
                bool isAmbiguous = false, hasIssue = false, hasNoData = false;
                string issueText = "", noDataText = "";

                if (modSetting != null)
                {
                    isAmbiguous = modSetting.AmbiguousNpcFormKeys.Contains(sourceNpcKey);
                    hasNoData = (modSetting == null || (!modSetting.CorrespondingFolderPaths.Any() && !modSetting.IsAutoGenerated));
                    if(hasNoData) noDataText = $"{modName} is a Mugshot-only entry; it doesn't contain mod data. This selection will be skipped during patching.";

                    if (modSetting.NpcFormKeysToNotifications.TryGetValue(sourceNpcKey, out var notif))
                    {
                        hasIssue = true;
                        issueText = notif.IssueMessage;
                    }
                }
                
                // Create and store the lightweight data object
                var data = new SummaryNpcData(targetNpcKey, sourceNpcKey, targetNpcName, modName, sourceNpcName, isGuest, modSetting, isAmbiguous, hasIssue, issueText, hasNoData, noDataText);
                _allNpcData.Add(data);
                _allListItems.Add(new VM_SummaryListItem(targetNpcKey, targetNpcName, modName, sourceNpcName, isGuest));
            }
            
            _allNpcData = _allNpcData.OrderBy(d => (npcViewModelMap.TryGetValue(d.TargetNpcFormKey, out var npcVM) && npcVM.IsInLoadOrder) ? 0 : 1).ThenBy(d => d.NpcDisplayName).ToList();
            _allListItems = _allListItems.OrderBy(i => (npcViewModelMap.TryGetValue(i.TargetNpcFormKey, out var npcVM) && npcVM.IsInLoadOrder) ? 0 : 1).ThenBy(i => i.NpcDisplayName).ToList();
        }

        private void UpdateDisplay()
        {
            // Cancel any image loading tasks from the previous page
            _summaryImageLoadCts?.Cancel();
            
            GalleryItems.Clear();

            if (ViewMode == SummaryViewMode.List)
            {
                // List view shows all filtered items without pagination.
                // Copy so the DataGrid never holds a reference to _allListItems itself.
                ListItems = new List<VM_SummaryListItem>(GetFilteredListItems());
                TotalPages = 1;
            }
            else // Gallery View
            {
                if (ListItems.Count > 0) ListItems = new List<VM_SummaryListItem>();

                // Create a new cancellation source for the current page
                _summaryImageLoadCts = new CancellationTokenSource();
                var token = _summaryImageLoadCts.Token;
                var filteredData = GetFilteredData();
                int totalItems = filteredData.Count;
                TotalPages = Math.Max(1, (int)Math.Ceiling((double)totalItems / MaxNpcsPerPage));
                CurrentPage = Math.Min(CurrentPage, TotalPages);

                // Get just the data for the current page
                var pagedData = filteredData
                    .Skip((CurrentPage - 1) * MaxNpcsPerPage)
                    .Take(MaxNpcsPerPage)
                    .ToList();
            
                // Create the heavy ViewModels ONLY for the visible page
                string placeholderPath = Path.Combine(AppContext.BaseDirectory, @"Resources\No Mugshot.png");
                var viewModelsForPage = pagedData.Select(data =>
                {
                    string imagePath = ResolveImagePath(data) ?? placeholderPath;
                    bool hasMugshot = !imagePath.Equals(placeholderPath, StringComparison.OrdinalIgnoreCase);

                    // Call the factory with only the runtime parameters.
                    // Autofac will automatically inject the singleton dependencies.
                    return _summaryMugshotFactory(
                        imagePath, data.TargetNpcFormKey, data.SourceNpcFormKey, data.NpcDisplayName, data.ModDisplayName, data.SourceNpcDisplayName,
                        data.IsGuest, data.IsAmbiguous, data.HasIssue, data.IssueText, data.HasNoData, data.NoDataText,
                        hasMugshot, data.ModSetting);

                }).ToList();


                // Add them to the display collection
                foreach(var vm in viewModelsForPage)
                {
                    GalleryItems.Add(vm);
                }
            
                Task.Run(async () =>
                {
                    foreach(var vm in viewModelsForPage)
                    {
                        if (token.IsCancellationRequested) break;
                        await vm.LoadRealImageAsync(token);
                    }
                }, token);
            }

            if (!SummaryViewIsZoomLocked) SummaryViewHasUserManuallyZoomed = false;
            _refreshImageSizesSubject.OnNext(Unit.Default);
        }

        private string? ResolveImagePath(SummaryNpcData data)
        {
            // Curated mugshots resolve via the mod's MugShotFolderPaths. Auto-generated
            // PNGs live in a separate AutoGen folder that (post-2.1.7) is NOT registered
            // in MugShotFolderPaths, so GetMugshotPathForNpc can't see them. Probe the
            // AutoGen folder directly when curated resolution comes up empty, mirroring
            // the NPCs/Mods menu fast-path (VM_NpcsMenuMugshot.LoadInitialImageAsync ->
            // TryGetExistingFreshAutoGenPath). Without this the Summary tile starts as a
            // placeholder and only the racy/cancellable LoadRealImageAsync could recover
            // it, which is why auto-generated faces were missing here but shown elsewhere.
            string? resolvedPath = _npcsViewModel.GetMugshotPathForNpc(data.ModDisplayName, data.SourceNpcFormKey, data.TargetNpcFormKey);
            if (resolvedPath != null || data.ModSetting == null) return resolvedPath;

            if (_batchMugshotGenerator.TryGetExistingFreshAutoGenPath(data.SourceNpcFormKey, data.ModSetting, out var autoGenPath))
            {
                return autoGenPath;
            }
            // Freshness probe said stale (or its identity inputs weren't
            // resolvable) but a cached render still exists — show it rather
            // than the placeholder, mirroring the NPCs-menu fallback.
            if (_batchMugshotGenerator.TryGetExistingAutoGenPath(data.SourceNpcFormKey, data.ModSetting, out var anyAutoGenPath))
            {
                return anyAutoGenPath;
            }
            return null;
        }

        private List<SummaryNpcData> GetFilteredData()
        {
            if (SelectedNpcGroup == "All NPCs" || string.IsNullOrEmpty(SelectedNpcGroup))
            {
                return _allNpcData;
            }
            return _allNpcData.Where(d => _settings.NpcGroupAssignments.ContainsKey(d.TargetNpcFormKey) &&
                                          _settings.NpcGroupAssignments[d.TargetNpcFormKey].Contains(SelectedNpcGroup))
                .ToList();
        }

        private List<VM_SummaryListItem> GetFilteredListItems()
        {
            if (SelectedNpcGroup == "All NPCs" || string.IsNullOrEmpty(SelectedNpcGroup))
            {
                return _allListItems;
            }
    
            var targetKeysInGroup = _settings.NpcGroupAssignments
                .Where(kvp => kvp.Value.Contains(SelectedNpcGroup))
                .Select(kvp => kvp.Key)
                .ToHashSet();

            // The corrected, efficient line:
            return _allListItems.Where(i => targetKeysInGroup.Contains(i.TargetNpcFormKey)).ToList();
        }

        public void Dispose()
        {
            _summaryImageLoadCts?.Cancel(); // Cancel any lingering tasks on dispose
            _disposables.Dispose();
        }
    }
