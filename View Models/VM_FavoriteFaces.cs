using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Windows;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat;

namespace NPC_Plugin_Chooser_2.View_Models;

public class VM_FavoriteFaces : ReactiveObject, IActivatableViewModel, IDisposable
{
    public delegate VM_FavoriteFaces Factory(FavoriteFacesMode mode, VM_NpcsMenuSelection? targetNpcForApply);
    
    public enum FavoriteFacesMode
    {
        Share, // Launched from global "Favs" button
        Apply // Launched from NPC context menu to apply a favorite
    }

    // Dependencies
    private readonly Settings _settings;
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly NpcConsistencyProvider _consistencyProvider;
    private readonly VM_NpcSelectionBar _npcsViewModel;
    private readonly VM_Mods _modsViewModel;
    private readonly Lazy<VM_MainWindow> _lazyMainWindowVm;
    private readonly VM_NpcsMenuSelection? _targetNpcForApply;
    private readonly FavoriteMugshotFactory _favoriteMugshotFactory;
    private readonly CompositeDisposable _disposables = new();
    private CancellationTokenSource? _imageLoadCts;

    public ViewModelActivator Activator { get; } = new();
    public FavoriteFacesMode Mode { get; }
    public bool IsShareMode => Mode == FavoriteFacesMode.Share;
    public bool IsApplyMode => Mode == FavoriteFacesMode.Apply;

    // UI Properties
    public ObservableCollection<VM_SummaryMugshot> FavoriteMugshots { get; } = new();
    public ObservableCollection<VM_SummaryMugshot> FilteredFavoriteMugshots { get; } = new();
    [Reactive] public VM_SummaryMugshot? SelectedMugshot { get; set; }
    [Reactive] public VM_NpcsMenuSelection? CurrentTargetNpc { get; private set; }

    // --- Filter Properties (mirrors the NPCs menu's multi-field filter) ---
    private const string AllFavoritesGroup = "All Favorites";

    [Reactive] public FavoriteFaceSearchType SearchType1 { get; set; } = FavoriteFaceSearchType.Name;
    [Reactive] public FavoriteFaceSearchType SearchType2 { get; set; } = FavoriteFaceSearchType.Mod;
    [Reactive] public FavoriteFaceSearchType SearchType3 { get; set; } = FavoriteFaceSearchType.Group;

    [Reactive] public string SearchText1 { get; set; } = string.Empty;
    [Reactive] public string SearchText2 { get; set; } = string.Empty;
    [Reactive] public string SearchText3 { get; set; } = string.Empty;

    [Reactive] public GenderFilterType SelectedGenderFilter1 { get; set; } = GenderFilterType.Any;
    [Reactive] public GenderFilterType SelectedGenderFilter2 { get; set; } = GenderFilterType.Any;
    [Reactive] public GenderFilterType SelectedGenderFilter3 { get; set; } = GenderFilterType.Any;

    [Reactive] public UniquenessFilterType SelectedUniquenessFilter1 { get; set; } = UniquenessFilterType.Any;
    [Reactive] public UniquenessFilterType SelectedUniquenessFilter2 { get; set; } = UniquenessFilterType.Any;
    [Reactive] public UniquenessFilterType SelectedUniquenessFilter3 { get; set; } = UniquenessFilterType.Any;

    [Reactive] public string? SelectedGroupFilter1 { get; set; }
    [Reactive] public string? SelectedGroupFilter2 { get; set; }
    [Reactive] public string? SelectedGroupFilter3 { get; set; }

    [Reactive] public SearchLogic CurrentSearchLogic { get; set; } = SearchLogic.AND;

    // Per-row input-control visibility (mirrors NpcsView: the row's text box
    // collapses and the matching combo shows when the selected type needs a picker).
    [ObservableAsProperty] public bool IsGenderSearch1 { get; }
    [ObservableAsProperty] public bool IsGenderSearch2 { get; }
    [ObservableAsProperty] public bool IsGenderSearch3 { get; }
    [ObservableAsProperty] public bool IsUniquenessSearch1 { get; }
    [ObservableAsProperty] public bool IsUniquenessSearch2 { get; }
    [ObservableAsProperty] public bool IsUniquenessSearch3 { get; }
    [ObservableAsProperty] public bool IsGroupSearch1 { get; }
    [ObservableAsProperty] public bool IsGroupSearch2 { get; }
    [ObservableAsProperty] public bool IsGroupSearch3 { get; }
    [ObservableAsProperty] public bool IsRaceSearch1 { get; }
    [ObservableAsProperty] public bool IsRaceSearch2 { get; }
    [ObservableAsProperty] public bool IsRaceSearch3 { get; }

    // Distinct race Names + EditorIDs for the Race filter's editable combo, read from
    // Settings.CachedFilterRaces (the NPCs menu populates that cache during init).
    public ObservableCollection<string> AvailableRaces { get; } = new();

    public ReactiveCommand<Unit, Unit> ClearFiltersCommand { get; }

    // --- Favorite Groups (a separate namespace from NPC groups; members are
    // favorite faces, keyed by their (source NpcFormKey, ModName) pair) ---
    [Reactive] public string SelectedFavoriteGroupName { get; set; } = string.Empty;
    public ObservableCollection<string> AvailableFavoriteGroups { get; } = new();
    public ReactiveCommand<Unit, Unit> AddCurrentFavoriteToGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCurrentFavoriteFromGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> AddAllVisibleFavoritesToGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveAllVisibleFavoritesFromGroupCommand { get; }

    // Lazily-built source-NPC lookup used to resolve Gender/Uniqueness for a
    // favorite from the main menu's NPC view models (same semantics as the NPCs
    // menu). AllNpcs is stable for the window's lifetime, so building it once is safe.
    private Dictionary<FormKey, VM_NpcsMenuSelection>? _sourceNpcMap;

    // Zoom Control Properties
    [Reactive] public double ZoomLevel { get; set; }
    [Reactive] public bool IsZoomLocked { get; set; }
    private readonly ISubject<Unit> _refreshImageSizesSubject = new Subject<Unit>();
    public IObservable<Unit> RefreshImageSizesObservable => _refreshImageSizesSubject.AsObservable();
    [Reactive] public bool HasUserManuallyZoomed { get; set; }
    private const double _minZoomPercentage = 1.0;
    private const double _maxZoomPercentage = 1000.0;
    private const double _zoomStepPercentage = 2.5;
    public int MaxMugshotsToFit => _settings.MaxMugshotsToFit;

    // Commands
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> MakeAvailableCommand { get; }
    public ReactiveCommand<Unit, Unit> ShareWithNpcCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveFromFavoritesCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; } // For Apply mode
    public ReactiveCommand<Unit, Unit> CloseCommand { get; } // For Share mode
    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetZoomCommand { get; }
    
    public delegate VM_SummaryMugshot FavoriteMugshotFactory(
        string imagePath,
        FormKey targetNpcFormKey,
        FormKey sourceNpcFormKey,
        string npcDisplayName,
        string modDisplayName,
        string sourceNpcDisplayName,
        bool isGuest, bool isAmbiguous, bool hasIssue, string issueText, bool hasNoData, string noDataText, bool hasMugshot,
        VM_ModSetting? associatedModSetting
    );

    public delegate void CloseWindowAction();

    public event CloseWindowAction? RequestClose;

    public VM_FavoriteFaces(
        Settings settings,
        EnvironmentStateProvider environmentStateProvider,
        NpcConsistencyProvider consistencyProvider,
        VM_NpcSelectionBar npcsViewModel,
        VM_Mods modsViewModel,
        Lazy<VM_MainWindow> lazyMainWindowVm,
        FavoriteFacesMode mode,
        VM_NpcsMenuSelection? targetNpcForApply,
        FavoriteMugshotFactory favoriteMugshotFactory)
    {
        _settings = settings;
        _environmentStateProvider = environmentStateProvider;
        _consistencyProvider = consistencyProvider;
        _npcsViewModel = npcsViewModel;
        _modsViewModel = modsViewModel;
        _lazyMainWindowVm = lazyMainWindowVm;
        _favoriteMugshotFactory = favoriteMugshotFactory;
        Mode = mode;
        _targetNpcForApply = targetNpcForApply;
        
        if (Mode == FavoriteFacesMode.Apply)
        {
            CurrentTargetNpc = _targetNpcForApply;
        }
        else // Share Mode
        {
            _npcsViewModel.WhenAnyValue(x => x.SelectedNpc)
                .BindTo(this, x => x.CurrentTargetNpc)
                .DisposeWith(_disposables);
        }

        var canExecuteWithSelection = this.WhenAnyValue(x => x.SelectedMugshot)
            .Select(mugshot => mugshot != null);

        ApplyCommand = ReactiveCommand.Create(ApplyFavorite, canExecuteWithSelection).DisposeWith(_disposables);
        MakeAvailableCommand = ReactiveCommand.Create(MakeFavoriteAvailable, canExecuteWithSelection)
            .DisposeWith(_disposables);
        ShareWithNpcCommand = ReactiveCommand.Create(ShareFavoriteWithNpc, canExecuteWithSelection)
            .DisposeWith(_disposables);
        RemoveFromFavoritesCommand = ReactiveCommand.Create(RemoveSelectedFavorite, canExecuteWithSelection)
            .DisposeWith(_disposables);
        CancelCommand = ReactiveCommand.Create(() => RequestClose?.Invoke()).DisposeWith(_disposables);
        CloseCommand = ReactiveCommand.Create(() => RequestClose?.Invoke()).DisposeWith(_disposables);

        // --- Favorite Group commands ---
        var canActOnCurrentFavorite = this.WhenAnyValue(
            x => x.SelectedMugshot,
            x => x.SelectedFavoriteGroupName,
            (mugshot, groupName) => mugshot != null && !string.IsNullOrWhiteSpace(groupName));
        var canActOnVisibleFavorites = this.WhenAnyValue(
            x => x.FilteredFavoriteMugshots.Count,
            x => x.SelectedFavoriteGroupName,
            (count, groupName) => count > 0 && !string.IsNullOrWhiteSpace(groupName));

        AddCurrentFavoriteToGroupCommand =
            ReactiveCommand.Create(AddCurrentFavoriteToGroup, canActOnCurrentFavorite).DisposeWith(_disposables);
        RemoveCurrentFavoriteFromGroupCommand =
            ReactiveCommand.Create(RemoveCurrentFavoriteFromGroup, canActOnCurrentFavorite).DisposeWith(_disposables);
        AddAllVisibleFavoritesToGroupCommand =
            ReactiveCommand.Create(AddAllVisibleFavoritesToGroup, canActOnVisibleFavorites).DisposeWith(_disposables);
        RemoveAllVisibleFavoritesFromGroupCommand =
            ReactiveCommand.Create(RemoveAllVisibleFavoritesFromGroup, canActOnVisibleFavorites).DisposeWith(_disposables);

        // Clear Filters Command — resets the filter *values*, leaving the chosen
        // search-field types in place.
        ClearFiltersCommand = ReactiveCommand.Create(() =>
        {
            SearchText1 = string.Empty;
            SearchText2 = string.Empty;
            SearchText3 = string.Empty;
            SelectedGroupFilter1 = null;
            SelectedGroupFilter2 = null;
            SelectedGroupFilter3 = null;
            SelectedGenderFilter1 = GenderFilterType.Any;
            SelectedGenderFilter2 = GenderFilterType.Any;
            SelectedGenderFilter3 = GenderFilterType.Any;
            SelectedUniquenessFilter1 = UniquenessFilterType.Any;
            SelectedUniquenessFilter2 = UniquenessFilterType.Any;
            SelectedUniquenessFilter3 = UniquenessFilterType.Any;
        }).DisposeWith(_disposables);

        // Zoom setup
        ZoomLevel = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, _settings.NpcsViewZoomLevel));
        IsZoomLocked = _settings.NpcsViewIsZoomLocked;

        ZoomInCommand = ReactiveCommand.Create(() =>
        {
            HasUserManuallyZoomed = true;
            ZoomLevel = Math.Min(_maxZoomPercentage, ZoomLevel + _zoomStepPercentage);
        }).DisposeWith(_disposables);
        ZoomOutCommand = ReactiveCommand.Create(() =>
        {
            HasUserManuallyZoomed = true;
            ZoomLevel = Math.Max(_minZoomPercentage, ZoomLevel - _zoomStepPercentage);
        }).DisposeWith(_disposables);
        ResetZoomCommand = ReactiveCommand.Create(() =>
        {
            IsZoomLocked = false;
            HasUserManuallyZoomed = false;
            _refreshImageSizesSubject.OnNext(Unit.Default);
        }).DisposeWith(_disposables);

        this.WhenAnyValue(x => x.ZoomLevel).Skip(1).Throttle(TimeSpan.FromMilliseconds(100))
            .Subscribe(zoom =>
            {
                if (IsZoomLocked || HasUserManuallyZoomed) _refreshImageSizesSubject.OnNext(Unit.Default);
            }).DisposeWith(_disposables);

        // Per-row input-control visibility (which picker/textbox to show).
        this.WhenAnyValue(x => x.SearchType1).Select(t => t == FavoriteFaceSearchType.Gender)
            .ToPropertyEx(this, x => x.IsGenderSearch1).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType2).Select(t => t == FavoriteFaceSearchType.Gender)
            .ToPropertyEx(this, x => x.IsGenderSearch2).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType3).Select(t => t == FavoriteFaceSearchType.Gender)
            .ToPropertyEx(this, x => x.IsGenderSearch3).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType1).Select(t => t == FavoriteFaceSearchType.Uniqueness)
            .ToPropertyEx(this, x => x.IsUniquenessSearch1).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType2).Select(t => t == FavoriteFaceSearchType.Uniqueness)
            .ToPropertyEx(this, x => x.IsUniquenessSearch2).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType3).Select(t => t == FavoriteFaceSearchType.Uniqueness)
            .ToPropertyEx(this, x => x.IsUniquenessSearch3).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType1).Select(t => t == FavoriteFaceSearchType.Group)
            .ToPropertyEx(this, x => x.IsGroupSearch1).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType2).Select(t => t == FavoriteFaceSearchType.Group)
            .ToPropertyEx(this, x => x.IsGroupSearch2).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType3).Select(t => t == FavoriteFaceSearchType.Group)
            .ToPropertyEx(this, x => x.IsGroupSearch3).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType1).Select(t => t == FavoriteFaceSearchType.Race)
            .ToPropertyEx(this, x => x.IsRaceSearch1).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType2).Select(t => t == FavoriteFaceSearchType.Race)
            .ToPropertyEx(this, x => x.IsRaceSearch2).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType3).Select(t => t == FavoriteFaceSearchType.Race)
            .ToPropertyEx(this, x => x.IsRaceSearch3).DisposeWith(_disposables);

        // Seed the Race combo from the shared cache the NPCs menu builds during init.
        foreach (var race in _settings.CachedFilterRaces)
            AvailableRaces.Add(race);

        // When a row's type changes, clear inputs that no longer apply so a hidden
        // filter can't silently keep narrowing results (mirrors NpcsView).
        this.WhenAnyValue(x => x.SearchType1).ObserveOn(RxApp.MainThreadScheduler).Subscribe(type =>
        {
            if (type is FavoriteFaceSearchType.Group or FavoriteFaceSearchType.Gender or FavoriteFaceSearchType.Uniqueness)
                SearchText1 = string.Empty;
            if (type != FavoriteFaceSearchType.Group) SelectedGroupFilter1 = null;
        }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType2).ObserveOn(RxApp.MainThreadScheduler).Subscribe(type =>
        {
            if (type is FavoriteFaceSearchType.Group or FavoriteFaceSearchType.Gender or FavoriteFaceSearchType.Uniqueness)
                SearchText2 = string.Empty;
            if (type != FavoriteFaceSearchType.Group) SelectedGroupFilter2 = null;
        }).DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType3).ObserveOn(RxApp.MainThreadScheduler).Subscribe(type =>
        {
            if (type is FavoriteFaceSearchType.Group or FavoriteFaceSearchType.Gender or FavoriteFaceSearchType.Uniqueness)
                SearchText3 = string.Empty;
            if (type != FavoriteFaceSearchType.Group) SelectedGroupFilter3 = null;
        }).DisposeWith(_disposables);

        // Normalize deserialized group sets to case-insensitive membership (JSON
        // round-trips a HashSet with the default comparer), then seed the combos.
        foreach (var assignment in _settings.FavoriteFacesGroupAssignments)
        {
            if (!Equals(assignment.Groups.Comparer, StringComparer.OrdinalIgnoreCase))
                assignment.Groups = new HashSet<string>(assignment.Groups, StringComparer.OrdinalIgnoreCase);
        }
        UpdateAvailableFavoriteGroups();

        // Filter subscription — recompute the filtered set when any row (or the
        // AND/OR logic) changes. Each row bundles its 5 inputs into one Unit stream.
        var filter1Changes = this.WhenAnyValue(
            x => x.SearchText1, x => x.SearchType1, x => x.SelectedGenderFilter1, x => x.SelectedUniquenessFilter1, x => x.SelectedGroupFilter1,
            (_, _, _, _, _) => Unit.Default);
        var filter2Changes = this.WhenAnyValue(
            x => x.SearchText2, x => x.SearchType2, x => x.SelectedGenderFilter2, x => x.SelectedUniquenessFilter2, x => x.SelectedGroupFilter2,
            (_, _, _, _, _) => Unit.Default);
        var filter3Changes = this.WhenAnyValue(
            x => x.SearchText3, x => x.SearchType3, x => x.SelectedGenderFilter3, x => x.SelectedUniquenessFilter3, x => x.SelectedGroupFilter3,
            (_, _, _, _, _) => Unit.Default);
        var logicChanges = this.WhenAnyValue(x => x.CurrentSearchLogic).Select(_ => Unit.Default);

        Observable.Merge(filter1Changes, filter2Changes, filter3Changes, logicChanges)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilters())
            .DisposeWith(_disposables);

        // Also refresh filters when the source collection changes
        FavoriteMugshots.CollectionChanged += (s, e) => ApplyFilters();

        this.WhenActivated((CompositeDisposable d) => { LoadFavoritesAsync().ConfigureAwait(false); });
    }

    private void ApplyFilters()
    {
        var predicates = new List<Func<VM_SummaryMugshot, bool>>();
        AddRowPredicate(SearchType1, SearchText1, SelectedGenderFilter1, SelectedUniquenessFilter1, SelectedGroupFilter1, predicates);
        AddRowPredicate(SearchType2, SearchText2, SelectedGenderFilter2, SelectedUniquenessFilter2, SelectedGroupFilter2, predicates);
        AddRowPredicate(SearchType3, SearchText3, SelectedGenderFilter3, SelectedUniquenessFilter3, SelectedGroupFilter3, predicates);

        IEnumerable<VM_SummaryMugshot> results = FavoriteMugshots;
        if (predicates.Count > 0)
        {
            results = CurrentSearchLogic == SearchLogic.AND
                ? FavoriteMugshots.Where(m => predicates.All(p => p(m)))
                : FavoriteMugshots.Where(m => predicates.Any(p => p(m)));
        }

        var filtered = results.ToList();

        FilteredFavoriteMugshots.Clear();
        foreach (var item in filtered)
        {
            FilteredFavoriteMugshots.Add(item);
        }

        // Trigger image size refresh after filtering
        if (!IsZoomLocked && !HasUserManuallyZoomed)
        {
            _refreshImageSizesSubject.OnNext(Unit.Default);
        }
    }

    private void AddRowPredicate(
        FavoriteFaceSearchType type, string? searchText,
        GenderFilterType genderFilter, UniquenessFilterType uniquenessFilter, string? groupFilter,
        List<Func<VM_SummaryMugshot, bool>> predicates)
    {
        switch (type)
        {
            case FavoriteFaceSearchType.Gender:
                predicates.Add(m => CheckGender(m, genderFilter));
                break;
            case FavoriteFaceSearchType.Uniqueness:
                predicates.Add(m => CheckUniqueness(m, uniquenessFilter));
                break;
            case FavoriteFaceSearchType.Group:
                var groupPredicate = BuildGroupPredicate(groupFilter);
                if (groupPredicate != null) predicates.Add(groupPredicate);
                break;
            case FavoriteFaceSearchType.Race:
                var racePredicate = BuildRacePredicate(searchText);
                if (racePredicate != null) predicates.Add(racePredicate);
                break;
            default:
                var textPredicate = BuildTextPredicate(type, searchText);
                if (textPredicate != null) predicates.Add(textPredicate);
                break;
        }
    }

    private Func<VM_SummaryMugshot, bool>? BuildTextPredicate(FavoriteFaceSearchType type, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return null;
        var text = searchText.Trim();
        switch (type)
        {
            case FavoriteFaceSearchType.Name:
                return m =>
                    (m.NpcDisplayName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.SourceNpcDisplayName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
            case FavoriteFaceSearchType.EditorID:
                return m => EditorIdMatches(m, text);
            case FavoriteFaceSearchType.FormKey:
                return m =>
                    m.TargetNpcFormKey.ToString().Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    m.SourceNpcFormKey.ToString().Contains(text, StringComparison.OrdinalIgnoreCase);
            case FavoriteFaceSearchType.Mod:
                return m => m.ModDisplayName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false;
            default:
                return null;
        }
    }

    private bool EditorIdMatches(VM_SummaryMugshot mugshot, string searchText)
    {
        // Target == Source for a favorite, but resolve both for robustness.
        if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(mugshot.TargetNpcFormKey, out var targetGetter)
            && targetGetter.EditorID != null
            && targetGetter.EditorID.Contains(searchText, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(mugshot.SourceNpcFormKey, out var sourceGetter)
            && sourceGetter.EditorID != null
            && sourceGetter.EditorID.Contains(searchText, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    private bool CheckGender(VM_SummaryMugshot mugshot, GenderFilterType filterType)
    {
        if (filterType == GenderFilterType.Any) return true;
        // Source NPC not resolvable (e.g. mugshot-only, not in load order) => unknown
        // gender, so it drops out of a concrete filter (matches the NPCs menu).
        var sourceNpc = GetSourceNpc(mugshot.SourceNpcFormKey);
        return filterType switch
        {
            GenderFilterType.Male => sourceNpc?.NpcData?.Gender == Gender.Male,
            GenderFilterType.Female => sourceNpc?.NpcData?.Gender == Gender.Female,
            _ => true
        };
    }

    private bool CheckUniqueness(VM_SummaryMugshot mugshot, UniquenessFilterType filterType)
    {
        if (filterType == UniquenessFilterType.Any) return true;
        var sourceNpc = GetSourceNpc(mugshot.SourceNpcFormKey);
        return filterType switch
        {
            UniquenessFilterType.Unique => sourceNpc?.IsUnique ?? false,
            UniquenessFilterType.Generic => sourceNpc != null && !sourceNpc.IsUnique,
            _ => true
        };
    }

    private Func<VM_SummaryMugshot, bool>? BuildGroupPredicate(string? selectedGroup)
    {
        if (string.IsNullOrWhiteSpace(selectedGroup) || selectedGroup == AllFavoritesGroup)
        {
            return null;
        }
        return mugshot =>
        {
            var groups = GetFavoriteGroups(mugshot.TargetNpcFormKey, mugshot.ModDisplayName);
            return groups != null && groups.Contains(selectedGroup);
        };
    }

    private VM_NpcsMenuSelection? GetSourceNpc(FormKey sourceNpcFormKey)
    {
        _sourceNpcMap ??= _npcsViewModel.AllNpcs
            .GroupBy(n => n.NpcFormKey)
            .ToDictionary(g => g.Key, g => g.First());
        return _sourceNpcMap.TryGetValue(sourceNpcFormKey, out var vm) ? vm : null;
    }

    // Matches a favorite by its source NPC's race Name or EditorID. A trailing '~'
    // means exact match (e.g. "NordRace~" excludes "NordRaceVampire"). Mirrors the
    // NPCs menu's Race filter (Auxilliary.ParseRaceSearchTerm / RaceMatches).
    private Func<VM_SummaryMugshot, bool>? BuildRacePredicate(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return null;
        var (term, exact) = Auxilliary.ParseRaceSearchTerm(searchText);
        if (string.IsNullOrEmpty(term)) return null;
        var raceInfoCache = new Dictionary<FormKey, (string? Name, string? EditorId)>();
        return mugshot =>
        {
            var raceKey = GetSourceNpc(mugshot.SourceNpcFormKey)?.NpcData?.RaceFormKey;
            if (raceKey == null || raceKey.Value.IsNull) return false;
            var info = GetRaceInfo(raceKey.Value, raceInfoCache);
            return Auxilliary.RaceMatches(info.Name, info.EditorId, term, exact);
        };
    }

    private (string? Name, string? EditorId) GetRaceInfo(
        FormKey raceKey, Dictionary<FormKey, (string? Name, string? EditorId)> cache)
    {
        if (cache.TryGetValue(raceKey, out var cached)) return cached;
        (string? Name, string? EditorId) result = (null, null);
        if (_environmentStateProvider.LinkCache.TryResolve<IRaceGetter>(raceKey, out var race))
            result = (race.Name?.String, race.EditorID);
        cache[raceKey] = result;
        return result;
    }

    // --- Favorite group storage helpers (keyed per favorite: source NPC + mod) ---
    private FavoriteFaceGroupAssignment? FindFavoriteGroupRecord(FormKey npcFormKey, string modName) =>
        _settings.FavoriteFacesGroupAssignments.FirstOrDefault(a =>
            a.NpcFormKey.Equals(npcFormKey) && string.Equals(a.ModName, modName, StringComparison.Ordinal));

    private HashSet<string>? GetFavoriteGroups(FormKey npcFormKey, string modName) =>
        FindFavoriteGroupRecord(npcFormKey, modName)?.Groups;

    private HashSet<string> GetOrCreateFavoriteGroups(FormKey npcFormKey, string modName)
    {
        var record = FindFavoriteGroupRecord(npcFormKey, modName);
        if (record == null)
        {
            record = new FavoriteFaceGroupAssignment
            {
                NpcFormKey = npcFormKey,
                ModName = modName,
                Groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
            _settings.FavoriteFacesGroupAssignments.Add(record);
        }
        return record.Groups;
    }

    // --- Favorite group commands ---
    private void AddCurrentFavoriteToGroup()
    {
        if (SelectedMugshot == null || string.IsNullOrWhiteSpace(SelectedFavoriteGroupName)) return;
        var groupName = SelectedFavoriteGroupName.Trim();
        var groups = GetOrCreateFavoriteGroups(SelectedMugshot.TargetNpcFormKey, SelectedMugshot.ModDisplayName);
        if (groups.Add(groupName))
        {
            UpdateAvailableFavoriteGroups();
            ApplyFilters();
        }
    }

    private void RemoveCurrentFavoriteFromGroup()
    {
        if (SelectedMugshot == null || string.IsNullOrWhiteSpace(SelectedFavoriteGroupName)) return;
        var groupName = SelectedFavoriteGroupName.Trim();
        var record = FindFavoriteGroupRecord(SelectedMugshot.TargetNpcFormKey, SelectedMugshot.ModDisplayName);
        if (record != null && record.Groups.Remove(groupName))
        {
            if (record.Groups.Count == 0) _settings.FavoriteFacesGroupAssignments.Remove(record);
            UpdateAvailableFavoriteGroups();
            ApplyFilters();
        }
    }

    private void AddAllVisibleFavoritesToGroup()
    {
        if (FilteredFavoriteMugshots.Count == 0 || string.IsNullOrWhiteSpace(SelectedFavoriteGroupName)) return;
        var groupName = SelectedFavoriteGroupName.Trim();
        if (!ScrollableMessageBox.Confirm(
                $"Add all {FilteredFavoriteMugshots.Count} currently visible favorite(s) to the group '{groupName}'?",
                "Confirm Add Visible Favorites"))
        {
            return;
        }

        bool changed = false;
        foreach (var mugshot in FilteredFavoriteMugshots)
        {
            var groups = GetOrCreateFavoriteGroups(mugshot.TargetNpcFormKey, mugshot.ModDisplayName);
            if (groups.Add(groupName)) changed = true;
        }
        if (changed) UpdateAvailableFavoriteGroups();
        ApplyFilters();
    }

    private void RemoveAllVisibleFavoritesFromGroup()
    {
        if (FilteredFavoriteMugshots.Count == 0 || string.IsNullOrWhiteSpace(SelectedFavoriteGroupName)) return;
        var groupName = SelectedFavoriteGroupName.Trim();
        if (!ScrollableMessageBox.Confirm(
                $"Remove all {FilteredFavoriteMugshots.Count} currently visible favorite(s) from the group '{groupName}'?",
                "Confirm Remove Visible Favorites"))
        {
            return;
        }

        bool changed = false;
        // Snapshot first: removing now-empty records mutates the settings list.
        foreach (var mugshot in FilteredFavoriteMugshots.ToList())
        {
            var record = FindFavoriteGroupRecord(mugshot.TargetNpcFormKey, mugshot.ModDisplayName);
            if (record != null && record.Groups.Remove(groupName))
            {
                changed = true;
                if (record.Groups.Count == 0) _settings.FavoriteFacesGroupAssignments.Remove(record);
            }
        }
        if (changed) UpdateAvailableFavoriteGroups();
        ApplyFilters();
    }

    private void UpdateAvailableFavoriteGroups()
    {
        var distinctGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in _settings.FavoriteFacesGroupAssignments)
        {
            foreach (var groupName in assignment.Groups)
            {
                if (!string.IsNullOrWhiteSpace(groupName)) distinctGroups.Add(groupName.Trim());
            }
        }

        var sortedGroups = distinctGroups.OrderBy(g => g).ToList();
        string? currentSelection = SelectedFavoriteGroupName;
        bool selectionStillExists = false;
        AvailableFavoriteGroups.Clear();
        AvailableFavoriteGroups.Add(AllFavoritesGroup);
        foreach (var group in sortedGroups)
        {
            AvailableFavoriteGroups.Add(group);
            if (group.Equals(currentSelection, StringComparison.OrdinalIgnoreCase)) selectionStillExists = true;
        }

        SelectedFavoriteGroupName = selectionStillExists ? currentSelection! : string.Empty;
    }

    private async Task LoadFavoritesAsync()
    {
        // Cancel any previous load and create a new token source for this load operation
        _imageLoadCts?.Cancel();
        _imageLoadCts = new CancellationTokenSource();
        var token = _imageLoadCts.Token;
        
        await Task.Run(() =>
        {
            var npcViewModelMap = _npcsViewModel.AllNpcs.ToDictionary(npc => npc.NpcFormKey);
            string placeholderPath = Path.Combine(AppContext.BaseDirectory, @"Resources\No Mugshot.png");
            var favorites = new List<VM_SummaryMugshot>();

            foreach (var (sourceNpcKey, modName) in _settings.FavoriteFaces.OrderBy(f => f.ModName)
                         .ThenBy(f => f.NpcFormKey.ToString()))
            {
                string sourceNpcName = npcViewModelMap.TryGetValue(sourceNpcKey, out var sNpc)
                    ? sNpc.DisplayName
                    : sourceNpcKey.ToString();
                string imagePath = _npcsViewModel.GetMugshotPathForNpc(modName, sourceNpcKey) ?? placeholderPath;
                bool hasMugshot = !imagePath.Equals(placeholderPath, StringComparison.OrdinalIgnoreCase);

                var modSetting = _modsViewModel.AllModSettings.FirstOrDefault(m =>
                    m.DisplayName.Equals(modName, StringComparison.OrdinalIgnoreCase));
                bool hasNoData = (modSetting == null ||
                                  (!modSetting.CorrespondingFolderPaths.Any() && !modSetting.IsAutoGenerated));
                
                var favVM = _favoriteMugshotFactory(
                    imagePath, sourceNpcKey, sourceNpcKey, sourceNpcName, modName, sourceNpcName,
                    false, false, false, "", hasNoData, "", 
                    hasMugshot, modSetting);

                favorites.Add(favVM);
            }
            if (token.IsCancellationRequested) return;

            // Switch to UI thread to update collection
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                if (token.IsCancellationRequested) return;
                FavoriteMugshots.Clear();
                foreach (var fav in favorites) FavoriteMugshots.Add(fav);
                
                // ApplyFilters will be triggered by CollectionChanged, but call it explicitly to ensure it runs
                ApplyFilters();
                _refreshImageSizesSubject.OnNext(Unit.Default);

                // Asynchronously load the actual image sources
                Task.Run(async () =>
                {
                    foreach (var vm in FavoriteMugshots)
                    {
                        if (token.IsCancellationRequested) break;
                        await vm.LoadRealImageAsync(token);
                    }
                }, token);
            });
        });
    }

    private void ApplyFavorite()
    {
        if (SelectedMugshot == null) return;
        if (CurrentTargetNpc == null)
        {
            ScrollableMessageBox.ShowWarning("Please select an NPC in the main window to apply this face to.", "No NPC Selected");
            return;
        }
        _npcsViewModel.AddGuestAppearance(CurrentTargetNpc.NpcFormKey, SelectedMugshot.ModDisplayName,
            SelectedMugshot.TargetNpcFormKey, SelectedMugshot.SourceNpcDisplayName);
        _consistencyProvider.SetSelectedMod(CurrentTargetNpc.NpcFormKey, SelectedMugshot.ModDisplayName,
            SelectedMugshot.TargetNpcFormKey);
    
        if (IsApplyMode)
        {
            RequestClose?.Invoke();
        }
    }

    private void MakeFavoriteAvailable()
    {
        if (SelectedMugshot == null) return;
        if (CurrentTargetNpc == null)
        {
            ScrollableMessageBox.ShowWarning("Please select an NPC in the main window to make this face available to.", "No NPC Selected");
            return;
        }
    
        _npcsViewModel.AddGuestAppearance(CurrentTargetNpc.NpcFormKey, SelectedMugshot.ModDisplayName,
            SelectedMugshot.TargetNpcFormKey, SelectedMugshot.SourceNpcDisplayName);
    
        if (IsApplyMode)
        {
            RequestClose?.Invoke();
        }
    }

    private void ShareFavoriteWithNpc()
    {
        if (SelectedMugshot == null) return;

        var selectorVm = new VM_NpcShareTargetSelector(_npcsViewModel.AllNpcs);
        var selectorView = new NpcShareTargetSelectorView
            { DataContext = selectorVm, Owner = Application.Current.MainWindow };
        selectorView.ShowDialog();

        var result = selectorVm.ReturnStatus;

        if ((result == ShareReturn.ShareAndSelect || result == ShareReturn.Share) && selectorVm.SelectedNpc != null)
        {
            var targetNpcKey = selectorVm.SelectedNpc.NpcFormKey;
            _npcsViewModel.AddGuestAppearance(targetNpcKey, SelectedMugshot.ModDisplayName,
                SelectedMugshot.TargetNpcFormKey, SelectedMugshot.SourceNpcDisplayName);
            if (result == ShareReturn.ShareAndSelect)
            {
                _consistencyProvider.SetSelectedMod(targetNpcKey, SelectedMugshot.ModDisplayName,
                    SelectedMugshot.TargetNpcFormKey);
            }
        }
    }

    private void RemoveSelectedFavorite()
    {
        if (SelectedMugshot == null) return;

        // Create the tuple that represents the favorite in the settings
        var favoriteTuple = (SelectedMugshot.TargetNpcFormKey, SelectedMugshot.ModDisplayName);

        // Remove the favorite from the persistent settings
        _settings.FavoriteFaces.Remove(favoriteTuple);

        // Drop any group memberships for this favorite so no orphaned assignment
        // lingers in settings, and refresh the group combos if the removal emptied
        // a group entirely.
        var groupRecord = FindFavoriteGroupRecord(favoriteTuple.Item1, favoriteTuple.Item2);
        if (groupRecord != null)
        {
            _settings.FavoriteFacesGroupAssignments.Remove(groupRecord);
            UpdateAvailableFavoriteGroups();
        }

        // Remove the favorite from the collection currently displayed in the UI
        FavoriteMugshots.Remove(SelectedMugshot);

        // Clear the selection
        SelectedMugshot = null;

        // If zoom isn't locked, trigger a refresh to repack the remaining images.
        if (!IsZoomLocked)
        {
            HasUserManuallyZoomed = false;
            _refreshImageSizesSubject.OnNext(Unit.Default);
        }
    }

    public void Dispose()
    {
        _imageLoadCts?.Cancel(); // Cancel any running image loads when the window closes
        _disposables.Dispose();
    }
}