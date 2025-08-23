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
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

    public class VM_Summary : ReactiveObject, IDisposable
    {
        // Dependencies
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly VM_NpcSelectionBar _npcsViewModel;
        private readonly VM_Mods _modsViewModel;
        private readonly Lazy<VM_MainWindow> _lazyMainWindowVm;
        private readonly CompositeDisposable _disposables = new();
        
        private List<VM_SummaryMugshot> _allMugshots = new();
        private List<VM_SummaryListItem> _allListItems = new();
        private readonly ISubject<Unit> _refreshImageSizesSubject = new Subject<Unit>();

        // --- Top Row Properties ---
        [Reactive] public bool IsGalleryView { get; set; } = true;
        [ObservableAsProperty] public bool IsListView { get; }
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

        public VM_Summary(
            Settings settings,
            NpcConsistencyProvider consistencyProvider,
            EnvironmentStateProvider environmentStateProvider,
            VM_NpcSelectionBar npcsViewModel,
            VM_Mods modsViewModel,
            Lazy<VM_MainWindow> lazyMainWindowVm)
        {
            _settings = settings;
            _consistencyProvider = consistencyProvider;
            _environmentStateProvider = environmentStateProvider;
            _npcsViewModel = npcsViewModel;
            _modsViewModel = modsViewModel;
            _lazyMainWindowVm = lazyMainWindowVm;

            MaxNpcsPerPage = _settings.MaxNpcsPerPageSummaryView;
            this.WhenAnyValue(x => x.MaxNpcsPerPage)
                .Throttle(TimeSpan.FromMilliseconds(500))
                .Subscribe(val => _settings.MaxNpcsPerPageSummaryView = val);

            // Populate NPC groups from the Npcs View Model
            AvailableNpcGroups = _npcsViewModel.AvailableNpcGroups;
            SelectedNpcGroup = AvailableNpcGroups.FirstOrDefault() ?? "All NPCs";

            this.WhenAnyValue(x => x.IsGalleryView)
                .Select(isGallery => !isGallery)
                .ToPropertyEx(this, x => x.IsListView);

            // --- Pagination Commands ---
            var canGoNext = this.WhenAnyValue(x => x.CurrentPage, x => x.TotalPages, (c, t) => c < t);
            NextPageCommand = ReactiveCommand.Create(() => { CurrentPage++; }, canGoNext);
            var canGoPrev = this.WhenAnyValue(x => x.CurrentPage, c => c > 1);
            PreviousPageCommand = ReactiveCommand.Create(() => { CurrentPage--; }, canGoPrev);

            // --- Zoom Commands & Properties ---
            SummaryViewZoomLevel = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, _settings.SummaryViewZoomLevel));
            SummaryViewIsZoomLocked = _settings.SummaryViewIsZoomLocked;

            ZoomInSummaryCommand = ReactiveCommand.Create(() =>
            {
                SummaryViewHasUserManuallyZoomed = true;
                SummaryViewZoomLevel = Math.Min(_maxZoomPercentage, SummaryViewZoomLevel + _zoomStepPercentage);
            });
            ZoomOutSummaryCommand = ReactiveCommand.Create(() =>
            {
                SummaryViewHasUserManuallyZoomed = true;
                SummaryViewZoomLevel = Math.Max(_minZoomPercentage, SummaryViewZoomLevel - _zoomStepPercentage);
            });
            ResetZoomSummaryCommand = ReactiveCommand.Create(() =>
            {
                SummaryViewIsZoomLocked = false;
                SummaryViewHasUserManuallyZoomed = false;
                _refreshImageSizesSubject.OnNext(Unit.Default);
            });

            this.WhenAnyValue(x => x.SummaryViewZoomLevel)
                .Skip(1) // Don't fire on initial load
                .Throttle(TimeSpan.FromMilliseconds(100))
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
            this.WhenAnyValue(x => x.CurrentPage, x => x.IsGalleryView, x => x.SelectedNpcGroup, x => x.MaxNpcsPerPage)
                .Throttle(TimeSpan.FromMilliseconds(50))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => UpdateDisplay());

            // Update page display text
            this.WhenAnyValue(x => x.CurrentPage, x => x.TotalPages)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(PageDisplay)));
            
            // Re-fetch data when selections change
            _consistencyProvider.NpcSelectionChanged
                .Throttle(TimeSpan.FromSeconds(1)) // Debounce rapid changes
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => InitializeData());

            InitializeData();
        }

        private void InitializeData()
        {
            // Create a lookup map from the AllNpcs list for efficient name retrieval.
            var npcViewModelMap = _npcsViewModel.AllNpcs.ToDictionary(npc => npc.NpcFormKey);
            
            var allSelections = _settings.SelectedAppearanceMods;
            
            _allMugshots = new List<VM_SummaryMugshot>();
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
                    hasNoData = modSetting.IsFaceGenOnlyEntry && !modSetting.FaceGenOnlyNpcFormKeys.Contains(sourceNpcKey);
                    if(hasNoData) noDataText = $"{modName} is a FaceGen-only entry and doesn't contain a plugin record for this NPC.";

                    if (modSetting.NpcFormKeysToNotifications.TryGetValue(sourceNpcKey, out var notif))
                    {
                        hasIssue = true;
                        issueText = notif.IssueMessage;
                    }
                }

                _allMugshots.Add(new VM_SummaryMugshot(imagePath, targetNpcKey, targetNpcName, modName, sourceNpcName, isGuest, isAmbiguous, hasIssue, issueText, hasNoData, noDataText, hasMugshot, _lazyMainWindowVm, _npcsViewModel, _modsViewModel));
                _allListItems.Add(new VM_SummaryListItem(targetNpcKey, targetNpcName, modName, sourceNpcName, isGuest));
            }
            
            // Sort the lists once: NPCs with resolved names first, then alphabetically.
            // This now checks IsInLoadOrder, which is a reliable flag for a resolved name.
            _allMugshots = _allMugshots
                .OrderBy(m => (npcViewModelMap.TryGetValue(m.TargetNpcFormKey, out var npcVM) && npcVM.IsInLoadOrder) ? 0 : 1)
                .ThenBy(m => m.NpcDisplayName)
                .ToList();

            _allListItems = _allListItems
                .OrderBy(i => (npcViewModelMap.TryGetValue(i.TargetNpcFormKey, out var npcVM) && npcVM.IsInLoadOrder) ? 0 : 1)
                .ThenBy(i => i.NpcDisplayName)
                .ToList();

            UpdateDisplay();
        }

        private void UpdateDisplay()
        {
            DisplayedItems.Clear();

            var filteredMugshots = GetFilteredMugshots();
            
            if (IsListView)
            {
                // List view shows all filtered items without pagination
                var filteredListItems = GetFilteredListItems();
                foreach(var item in filteredListItems) DisplayedItems.Add(item);
                TotalPages = 1;
            }
            else // Gallery View
            {
                int totalItems = filteredMugshots.Count;
                TotalPages = Math.Max(1, (int)Math.Ceiling((double)totalItems / MaxNpcsPerPage));
                CurrentPage = Math.Min(CurrentPage, TotalPages);

                var pagedItems = filteredMugshots
                    .Skip((CurrentPage - 1) * MaxNpcsPerPage)
                    .Take(MaxNpcsPerPage);

                foreach(var item in pagedItems) DisplayedItems.Add(item);
            }

            if (!SummaryViewIsZoomLocked) SummaryViewHasUserManuallyZoomed = false;
            _refreshImageSizesSubject.OnNext(Unit.Default);
        }

        private List<VM_SummaryMugshot> GetFilteredMugshots()
        {
            if (SelectedNpcGroup == "All NPCs" || string.IsNullOrEmpty(SelectedNpcGroup))
            {
                return _allMugshots;
            }
            return _allMugshots.Where(m => _settings.NpcGroupAssignments.ContainsKey(m.TargetNpcFormKey) &&
                                           _settings.NpcGroupAssignments[m.TargetNpcFormKey].Contains(SelectedNpcGroup))
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

            return _allListItems.Where(i => targetKeysInGroup.Contains(
                _allMugshots.First(m => m.NpcDisplayName == i.NpcDisplayName).TargetNpcFormKey // This link is a bit weak; needs a key on the list item
            )).ToList();
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
