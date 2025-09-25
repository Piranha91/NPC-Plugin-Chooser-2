// View Models/VM_Summary.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows.Forms;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
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
        private readonly CompositeDisposable _disposables = new();
        
        private record SummaryNpcData(FormKey TargetNpcFormKey, FormKey SourceNpcFormKey, string NpcDisplayName, string ModDisplayName, string SourceNpcDisplayName, bool IsGuest, string ImagePath, bool HasMugshot, bool IsAmbiguous, bool HasIssue, string IssueText, bool HasNoData, string NoDataText);
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
        public ObservableCollection<object> DisplayedItems { get; } = new();

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
            SummaryMugshotFactory summaryMugshotFactory)
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

        private void InitializeData()
        {
            // Create a lookup map from the AllNpcs list for efficient name retrieval.
            var npcViewModelMap = _npcsViewModel.AllNpcs.ToDictionary(npc => npc.NpcFormKey);
            
            var allSelections = _settings.SelectedAppearanceMods;
            
            _allNpcData = new List<SummaryNpcData>();
            _allListItems = new List<VM_SummaryListItem>();

            string placeholderPath = Path.Combine(AppContext.BaseDirectory, @"Resources\No Mugshot.png");

            foreach (var selection in allSelections)
            {
                var targetNpcKey = selection.Key;
                var (modName, sourceNpcKey) = selection.Value;
                
                string targetNpcName = npcViewModelMap.TryGetValue(targetNpcKey, out var tNpc) ? tNpc.DisplayName : targetNpcKey.ToString();
                string sourceNpcName = npcViewModelMap.TryGetValue(sourceNpcKey, out var sNpc) ? sNpc.DisplayName : sourceNpcKey.ToString();

                bool isGuest = !targetNpcKey.Equals(sourceNpcKey);

                // For mugshot view, we need more details
                var modSetting = _modsViewModel.AllModSettings.FirstOrDefault(m => m.DisplayName.Equals(modName, StringComparison.OrdinalIgnoreCase));
                
                string imagePath = _npcsViewModel.GetMugshotPathForNpc(modName, sourceNpcKey) ?? placeholderPath;
                bool hasMugshot = !imagePath.Equals(placeholderPath, StringComparison.OrdinalIgnoreCase);

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
                var data = new SummaryNpcData(targetNpcKey, sourceNpcKey, targetNpcName, modName, sourceNpcName, isGuest, imagePath, hasMugshot, isAmbiguous, hasIssue, issueText, hasNoData, noDataText);
                _allNpcData.Add(data);
                _allListItems.Add(new VM_SummaryListItem(targetNpcKey, targetNpcName, modName, sourceNpcName, isGuest));
            }
            
            _allNpcData = _allNpcData.OrderBy(d => (npcViewModelMap.TryGetValue(d.TargetNpcFormKey, out var npcVM) && npcVM.IsInLoadOrder) ? 0 : 1).ThenBy(d => d.NpcDisplayName).ToList();
            _allListItems = _allListItems.OrderBy(i => (npcViewModelMap.TryGetValue(i.TargetNpcFormKey, out var npcVM) && npcVM.IsInLoadOrder) ? 0 : 1).ThenBy(i => i.NpcDisplayName).ToList();
        }

        private void UpdateDisplay()
        {
            DisplayedItems.Clear();
            
            if (ViewMode == SummaryViewMode.List)
            {
                // List view shows all filtered items without pagination
                var filteredListItems = GetFilteredListItems();
                foreach(var item in filteredListItems) DisplayedItems.Add(item);
                TotalPages = 1;
            }
            else // Gallery View
            {
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
                var viewModelsForPage = pagedData.Select(data =>
                {
                    var modSetting = _modsViewModel.AllModSettings.FirstOrDefault(m => m.DisplayName.Equals(data.ModDisplayName, StringComparison.OrdinalIgnoreCase));
            
                    // Call the factory with only the runtime parameters.
                    // Autofac will automatically inject the singleton dependencies.
                    return _summaryMugshotFactory(
                        data.ImagePath, data.TargetNpcFormKey, data.SourceNpcFormKey, data.NpcDisplayName, data.ModDisplayName, data.SourceNpcDisplayName,
                        data.IsGuest, data.IsAmbiguous, data.HasIssue, data.IssueText, data.HasNoData, data.NoDataText, 
                        data.HasMugshot, modSetting);

                }).ToList();


                // Add them to the display collection
                foreach(var vm in viewModelsForPage)
                {
                    DisplayedItems.Add(vm);
                }
            
                Task.Run(async () =>
                {
                    foreach(var vm in viewModelsForPage)
                    {
                        await vm.LoadAndGenerateImageAsync();
                    }
                });
            }

            if (!SummaryViewIsZoomLocked) SummaryViewHasUserManuallyZoomed = false;
            _refreshImageSizesSubject.OnNext(Unit.Default);
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
            _disposables.Dispose();
        }
    }
