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
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks; // Added for Task
using System.Windows;
using System.Windows.Forms; // Added for MessageBox
using System.Runtime.InteropServices;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views; 
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat;
using Application = System.Windows.Application;


namespace NPC_Plugin_Chooser_2.View_Models;

public class VM_NpcSelectionBar : ReactiveObject, IDisposable
{
    // --- Define the Factory Delegate ---
    public delegate VM_NpcsMenuMugshot AppearanceModFactory(
        string modName,
        string npcDisplayName,
        FormKey targetNpcFormKey,
        FormKey sourceNpcFormKey,
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
    private readonly VM_ModSetting.FromModelFactory _modSettingFromModelFactory;
    
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    // --- Internal State ---
    private HashSet<string> _hiddenModNames = new();
    private Dictionary<FormKey, HashSet<string>> _hiddenModsPerNpc = new();
    private Dictionary<FormKey, List<(string ModName, string ImagePath)>> _mugshotData = new();
    private readonly ISubject<Unit> _refreshImageSizesSubject = new Subject<Unit>();

    private readonly BehaviorSubject<VM_NpcsMenuSelection?> _requestScrollToNpcSubject =
        new BehaviorSubject<VM_NpcsMenuSelection?>(null);

    public IObservable<VM_NpcsMenuSelection?> RequestScrollToNpcObservable =>
        _requestScrollToNpcSubject.AsObservable();

    // --- Search Properties ---
    [Reactive] public string SearchText1 { get; set; } = string.Empty;
    [Reactive] public NpcSearchType SearchType1 { get; set; } = NpcSearchType.Name;
    [Reactive] public string SearchText2 { get; set; } = string.Empty;
    [Reactive] public NpcSearchType SearchType2 { get; set; } = NpcSearchType.InAppearanceMod;
    [Reactive] public string SearchText3 { get; set; } = string.Empty;
    [Reactive] public NpcSearchType SearchType3 { get; set; } = NpcSearchType.Group;
    
    private const string AllNpcsGroup = "All NPCs";

    // Visibility & Selection State Filters
    [ObservableAsProperty] public bool IsSelectionStateSearch1 { get; }

    [Reactive] public SelectionStateFilterType SelectedStateFilter1 { get; set; } = SelectionStateFilterType.NotMade;

    [ObservableAsProperty] public bool IsSelectionStateSearch2 { get; }

    [Reactive] public SelectionStateFilterType SelectedStateFilter2 { get; set; } = SelectionStateFilterType.NotMade;

    [ObservableAsProperty] public bool IsSelectionStateSearch3 { get; }

    [Reactive] public SelectionStateFilterType SelectedStateFilter3 { get; set; } = SelectionStateFilterType.NotMade;

    public Array AvailableSelectionStateFilters => Enum.GetValues(typeof(SelectionStateFilterType));
    [Reactive] public bool IsProgrammaticNavigationInProgress { get; set; } = false;

    // Group Filter Visibility & Selection
    [ObservableAsProperty] public bool IsGroupSearch1 { get; }
    [Reactive] public string? SelectedGroupFilter1 { get; set; }
    [ObservableAsProperty] public bool IsGroupSearch2 { get; }
    [Reactive] public string? SelectedGroupFilter2 { get; set; }
    [ObservableAsProperty] public bool IsGroupSearch3 { get; }
    [Reactive] public string? SelectedGroupFilter3 { get; set; }
    
    // Guest Status Visibility & Selection
    
    [ObservableAsProperty] public bool IsShareStatusSearch1 { get; }
    [Reactive] public ShareStatusFilterType SelectedShareStatusFilter1 { get; set; } = ShareStatusFilterType.Any;
    [ObservableAsProperty] public bool IsShareStatusSearch2 { get; }
    [Reactive] public ShareStatusFilterType SelectedShareStatusFilter2 { get; set; } = ShareStatusFilterType.Any;
    [ObservableAsProperty] public bool IsShareStatusSearch3 { get; }
    [Reactive] public ShareStatusFilterType SelectedShareStatusFilter3 { get; set; } = ShareStatusFilterType.Any;
    

    [Reactive] public bool IsSearchAndLogic { get; set; } = true;
    public Array AvailableSearchTypes => Enum.GetValues(typeof(NpcSearchType));
    // --- End Search Properties ---

    // --- UI / Display Properties ---
    [Reactive] public bool ShowHiddenMods { get; set; } = false;
    [Reactive] public bool ShowSingleOptionNpcs { get; set; } = true;
    [Reactive] public bool ShowUnloadedNpcs { get; set; } = true;
    [Reactive] public bool ShowNpcDescriptions { get; set; }
    public List<VM_NpcsMenuSelection> AllNpcs { get; } = new();
    public ObservableCollection<VM_NpcsMenuSelection> FilteredNpcs { get; } = new();
    [Reactive] public VM_NpcsMenuSelection? SelectedNpc { get; set; }
    [ObservableAsProperty] public ObservableCollection<VM_NpcsMenuMugshot>? CurrentNpcAppearanceMods { get; }
    [Reactive] public string? CurrentNpcDescription { get; private set; }
    public ReactiveCommand<Unit, string?> LoadDescriptionCommand { get; }
    [ObservableAsProperty] public bool IsLoadingDescription { get; }
    public IObservable<Unit> RefreshImageSizesObservable => _refreshImageSizesSubject.AsObservable();

    // --- NEW: Zoom Control Properties & Commands for NpcsView ---
    [Reactive] public double NpcsViewZoomLevel { get; set; }
    [Reactive] public bool NpcsViewIsZoomLocked { get; set; }
    public ReactiveCommand<Unit, Unit> ZoomInNpcsCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutNpcsCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetZoomNpcsCommand { get; }
    private const double _minZoomPercentage = 1.0;
    private const double _maxZoomPercentage = 1000.0;
    private const double _zoomStepPercentage = 2.5; // For +/- buttons and scroll wheel
    [Reactive] public bool NpcsViewHasUserManuallyZoomed { get; set; } = false;
    
    // --- New: Other Display Controls
    public bool NormalizeImageDimensions => _settings.NormalizeImageDimensions;
    public int MaxMugshotsToFit => _settings.MaxMugshotsToFit;

    // --- NPC Group Properties ---
    [Reactive] public string SelectedGroupName { get; set; } = string.Empty;
    public ObservableCollection<string> AvailableNpcGroups { get; } = new();
    public ReactiveCommand<Unit, bool> AddCurrentNpcToGroupCommand { get; }
    public ReactiveCommand<Unit, bool> RemoveCurrentNpcFromGroupCommand { get; }
    public ReactiveCommand<Unit, bool> AddAllVisibleNpcsToGroupCommand { get; }
    public ReactiveCommand<Unit, bool> RemoveAllVisibleNpcsFromGroupCommand { get; }
    // --- End NPC Group Properties ---

    // --- NEW: Compare/Hide/Deselect Functionality ---
    [ObservableAsProperty] public int CheckedMugshotCount { get; }
    [ObservableAsProperty] public bool CanOpenHideUnhideMenu { get; }
    public ReactiveCommand<Unit, Unit> CompareSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> HideAllSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> HideAllButSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> UnhideAllSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> UnhideAllButSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> DeselectAllCommand { get; }
    // --- End NEW Compare/Hide/Deselect ---

    // --- NEW: Import/Export Commands ---
    public ReactiveCommand<Unit, Unit> ImportChoicesFromLoadOrderCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportChoicesCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportChoicesCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearChoicesCommand { get; }
    // --- End Import/Export Commands ---
    
    public ReactiveCommand<object, Unit> SetNpcOutfitOverrideCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowFavoritesCommand { get; }
    public ReactiveCommand<VM_NpcsMenuSelection, Unit> AddFavoriteFaceToNpcCommand { get; }

    // --- Constructor ---
    public VM_NpcSelectionBar(EnvironmentStateProvider environmentStateProvider,
        Settings settings,
        Auxilliary auxilliary,
        NpcConsistencyProvider consistencyProvider,
        NpcDescriptionProvider descriptionProvider,
        Lazy<VM_Mods> lazyModsVm,
        Lazy<VM_MainWindow> lazyMainWindowVm,
        AppearanceModFactory appearanceModFactory,
        VM_ModSetting.FromModelFactory modSettingFromModelFactory)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
        _auxilliary = auxilliary;
        _consistencyProvider = consistencyProvider;
        _descriptionProvider = descriptionProvider;
        _lazyModsVm = lazyModsVm;
        _lazyMainWindowVm = lazyMainWindowVm;
        _appearanceModFactory = appearanceModFactory;
        _modSettingFromModelFactory = modSettingFromModelFactory;

        _hiddenModNames = _settings.HiddenModNames ?? new(StringComparer.OrdinalIgnoreCase);
        _hiddenModsPerNpc = _settings.HiddenModsPerNpc ?? new();
        _settings.NpcGroupAssignments ??= new();

        NpcsViewZoomLevel =
            Math.Max(_minZoomPercentage,
                Math.Min(_maxZoomPercentage, _settings.NpcsViewZoomLevel)); // Clamp initial load
        NpcsViewIsZoomLocked = _settings.NpcsViewIsZoomLocked;
        Debug.WriteLine(
            $"VM_NpcSelectionBar.Constructor: Initial ZoomLevel: {NpcsViewZoomLevel:F2}, IsZoomLocked: {NpcsViewIsZoomLocked}");

        ZoomInNpcsCommand = ReactiveCommand.Create(() =>
        {
            Debug.WriteLine("VM_NpcSelectionBar: ZoomInNpcsCommand executed.");
            NpcsViewHasUserManuallyZoomed = true;
            NpcsViewZoomLevel = Math.Min(_maxZoomPercentage, NpcsViewZoomLevel + _zoomStepPercentage);
        });
        ZoomOutNpcsCommand = ReactiveCommand.Create(() =>
        {
            Debug.WriteLine("VM_NpcSelectionBar: ZoomOutNpcsCommand executed.");
            NpcsViewHasUserManuallyZoomed = true;
            NpcsViewZoomLevel = Math.Max(_minZoomPercentage, NpcsViewZoomLevel - _zoomStepPercentage);
        });
        ResetZoomNpcsCommand = ReactiveCommand.Create(() =>
        {
            Debug.WriteLine("VM_NpcSelectionBar: ResetZoomNpcsCommand executed.");
            NpcsViewIsZoomLocked = false;
            NpcsViewHasUserManuallyZoomed = false;
            _refreshImageSizesSubject.OnNext(Unit.Default);
        });
        
        SetNpcOutfitOverrideCommand = ReactiveCommand.Create<object>(param =>
        {
            // This is the corrected, compatible code:
            if (param is object[] arr && arr.Length == 2 && 
                arr[0] is OutfitOverride newOverride && 
                arr[1] is VM_NpcsMenuSelection npcVM)
            {
                SetNpcOutfitOverride(npcVM.NpcFormKey, newOverride);
            }
        });

        ZoomInNpcsCommand.ThrownExceptions
            .Subscribe(ex => Debug.WriteLine($"Error ZoomInNpcsCommand: {ex.Message}")).DisposeWith(_disposables);
        ZoomOutNpcsCommand.ThrownExceptions
            .Subscribe(ex => Debug.WriteLine($"Error ZoomOutNpcsCommand: {ex.Message}")).DisposeWith(_disposables);
        ResetZoomNpcsCommand.ThrownExceptions
            .Subscribe(ex => Debug.WriteLine($"Error ResetZoomNpcsCommand: {ex.Message}"))
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.SelectedNpc)
            .Subscribe(npc =>
            {
                if (npc != null)
                {
                    _settings.LastSelectedNpcFormKey = npc.NpcFormKey;
                }

                if (!NpcsViewIsZoomLocked)
                {
                    NpcsViewHasUserManuallyZoomed = false;
                }
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.SelectedNpc)
            .Select(selectedNpc => selectedNpc != null
                ? CreateMugShotViewModels(selectedNpc, _mugshotData)
                : new ObservableCollection<VM_NpcsMenuMugshot>())
            .ObserveOn(RxApp.MainThreadScheduler)
            .ToPropertyEx(this, x => x.CurrentNpcAppearanceMods);

        this.WhenAnyValue(x => x.CurrentNpcAppearanceMods)
            // Add a 50ms throttle. This gives the UI thread a moment to complete the
            // layout pass for the newly loaded mugshots before the resize signal is sent.
            .Throttle(TimeSpan.FromMilliseconds(50), RxApp.MainThreadScheduler) 
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                ToggleModVisibility();
                _refreshImageSizesSubject.OnNext(Unit.Default);
            })
            .DisposeWith(_disposables);

        _consistencyProvider.NpcSelectionChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(args => UpdateSelectionState(args.NpcFormKey, args.SelectedModName, args.SourceNpcFormKey))
            .DisposeWith(_disposables);
        
        // Listen for the request to share an appearance
        MessageBus.Current.Listen<ShareAppearanceRequest>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(request => HandleShareAppearanceRequest(request.MugshotToShare))
            .DisposeWith(_disposables);
        
        MessageBus.Current.Listen<UnshareAppearanceRequest>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(request => HandleUnshareAppearanceRequest(request.MugshotToUnshare))
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.SearchType1)
            .Select(type => type == NpcSearchType.SelectionState)
            .ToPropertyEx(this, x => x.IsSelectionStateSearch1);
        this.WhenAnyValue(x => x.SearchType1)
            .Select(type => type == NpcSearchType.Group)
            .ToPropertyEx(this, x => x.IsGroupSearch1);
        this.WhenAnyValue(x => x.SearchType1)
            .Select(type => type == NpcSearchType.ShareStatus)
            .ToPropertyEx(this, x => x.IsShareStatusSearch1);

        this.WhenAnyValue(x => x.SearchType2)
            .Select(type => type == NpcSearchType.SelectionState)
            .ToPropertyEx(this, x => x.IsSelectionStateSearch2);
        this.WhenAnyValue(x => x.SearchType2)
            .Select(type => type == NpcSearchType.Group)
            .ToPropertyEx(this, x => x.IsGroupSearch2);
        this.WhenAnyValue(x => x.SearchType2)
            .Select(type => type == NpcSearchType.ShareStatus)
            .ToPropertyEx(this, x => x.IsShareStatusSearch2);

        this.WhenAnyValue(x => x.SearchType3)
            .Select(type => type == NpcSearchType.SelectionState)
            .ToPropertyEx(this, x => x.IsSelectionStateSearch3);
        this.WhenAnyValue(x => x.SearchType3)
            .Select(type => type == NpcSearchType.Group)
            .ToPropertyEx(this, x => x.IsGroupSearch3);
        this.WhenAnyValue(x => x.SearchType3)
            .Select(type => type == NpcSearchType.ShareStatus)
            .ToPropertyEx(this, x => x.IsShareStatusSearch3);

        this.WhenAnyValue(x => x.SearchType1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(type =>
            {
                if (type == NpcSearchType.Group || type == NpcSearchType.SelectionState || type == NpcSearchType.ShareStatus) SearchText1 = string.Empty;
                if (type != NpcSearchType.Group) SelectedGroupFilter1 = null;
            })
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType2)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(type =>
            {
                if (type == NpcSearchType.Group || type == NpcSearchType.SelectionState || type == NpcSearchType.ShareStatus) SearchText2 = string.Empty;
                if (type != NpcSearchType.Group) SelectedGroupFilter2 = null;
            })
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType3)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(type =>
            {
                if (type == NpcSearchType.Group || type == NpcSearchType.SelectionState || type == NpcSearchType.ShareStatus) SearchText3 = string.Empty;
                if (type != NpcSearchType.Group) SelectedGroupFilter3 = null;
            })
            .DisposeWith(_disposables);
        
        ShowSingleOptionNpcs = _settings.ShowSingleOptionNpcs;
        this.WhenAnyValue(x => x.ShowSingleOptionNpcs)
            .Skip(1) // Skip the initial value on load
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(show => 
            {
                _settings.ShowSingleOptionNpcs = show;
                ApplyFilter(false);
            })
            .DisposeWith(_disposables);

        ShowUnloadedNpcs = _settings.ShowUnloadedNpcs;
        this.WhenAnyValue(x => x.ShowUnloadedNpcs)
            .Skip(1) // Skip the initial value on load
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(show => 
            {
                _settings.ShowUnloadedNpcs = show;
                ApplyFilter(false);
            })
            .DisposeWith(_disposables);

        var filter1Changes = this.WhenAnyValue(
            x => x.SearchText1, x => x.SearchType1, x => x.SelectedStateFilter1, x => x.SelectedGroupFilter1, x => x.SelectedShareStatusFilter1
        ).Select(_ => Unit.Default);
        var filter2Changes = this.WhenAnyValue(
            x => x.SearchText2, x => x.SearchType2, x => x.SelectedStateFilter2, x => x.SelectedGroupFilter2, x => x.SelectedShareStatusFilter2
        ).Select(_ => Unit.Default);
        var filter3Changes = this.WhenAnyValue(
            x => x.SearchText3, x => x.SearchType3, x => x.SelectedStateFilter3, x => x.SelectedGroupFilter3, x => x.SelectedShareStatusFilter3
        ).Select(_ => Unit.Default);
        var logicChanges = this.WhenAnyValue(
            x => x.IsSearchAndLogic
        ).Select(_ => Unit.Default);

        Observable.Merge(filter1Changes, filter2Changes, filter3Changes, logicChanges)
            .Throttle(TimeSpan.FromMilliseconds(100), RxApp.MainThreadScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter(false))
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.ShowHiddenMods)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (!NpcsViewIsZoomLocked)
                {
                    NpcsViewHasUserManuallyZoomed = false;
                }

                ToggleModVisibility();
            })
            .DisposeWith(_disposables);

        ShowNpcDescriptions = _settings.ShowNpcDescriptions;
        this.WhenAnyValue(x => x.ShowNpcDescriptions)
            .Subscribe(b => _settings.ShowNpcDescriptions = b)
            .DisposeWith(_disposables);

        LoadDescriptionCommand = ReactiveCommand.CreateFromTask<Unit, string?>(
            async (_, ct) =>
            {
                var npc = SelectedNpc;
                if (npc != null && ShowNpcDescriptions)
                {
                    try
                    {
                        return await _descriptionProvider.GetDescriptionAsync(npc.NpcFormKey, npc.DisplayName,
                            npc.NpcData?.EditorID);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error executing LoadDescriptionCommand: {ex}");
                        return null;
                    }
                }

                return null;
            },
            this.WhenAnyValue(x => x.SelectedNpc, x => x.ShowNpcDescriptions, (npc, show) => npc != null && show)
        );
        LoadDescriptionCommand.ObserveOn(RxApp.MainThreadScheduler).BindTo(this, x => x.CurrentNpcDescription)
            .DisposeWith(_disposables);
        LoadDescriptionCommand.IsExecuting.ToPropertyEx(this, x => x.IsLoadingDescription)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SelectedNpc, x => x.ShowNpcDescriptions)
            .Throttle(TimeSpan.FromMilliseconds(200)).Select(_ => Unit.Default)
            .InvokeCommand(LoadDescriptionCommand).DisposeWith(_disposables);

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
        AddAllVisibleNpcsToGroupCommand =
            ReactiveCommand.Create(AddAllVisibleNpcsToGroup, canExecuteAllGroupAction);
        RemoveAllVisibleNpcsFromGroupCommand =
            ReactiveCommand.Create(RemoveAllVisibleNpcsFromGroup, canExecuteAllGroupAction);

        AddCurrentNpcToGroupCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error adding NPC to group: {ex.Message}"))
            .DisposeWith(_disposables);
        RemoveCurrentNpcFromGroupCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error removing NPC from group: {ex.Message}"))
            .DisposeWith(_disposables);
        AddAllVisibleNpcsToGroupCommand.ThrownExceptions.Subscribe(ex =>
                ScrollableMessageBox.ShowError($"Error adding all visible NPCs to group: {ex.Message}"))
            .DisposeWith(_disposables);
        RemoveAllVisibleNpcsFromGroupCommand.ThrownExceptions.Subscribe(ex =>
                ScrollableMessageBox.ShowError($"Error removing all visible NPCs from group: {ex.Message}"))
            .DisposeWith(_disposables);

        UpdateAvailableNpcGroups();

        this.WhenAnyValue(x => x.NpcsViewZoomLevel)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(100))
            .Subscribe(zoom =>
            {
                bool isFromPackerUpdate = !NpcsViewIsZoomLocked && !NpcsViewHasUserManuallyZoomed;
                Debug.WriteLine(
                    $"VM_NpcSelectionBar: NpcsViewZoomLevel RAW input {zoom:F2}. IsFromPacker: {isFromPackerUpdate}, IsLocked: {NpcsViewIsZoomLocked}, ManualZoom: {NpcsViewHasUserManuallyZoomed}");

                double previousVmZoomLevel = _settings.NpcsViewZoomLevel;
                double newClampedZoom = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, zoom));

                if (Math.Abs(_settings.NpcsViewZoomLevel - newClampedZoom) > 0.001)
                {
                    _settings.NpcsViewZoomLevel = newClampedZoom;
                    Debug.WriteLine(
                        $"VM_NpcSelectionBar: Settings.NpcsViewZoomLevel updated to {newClampedZoom:F2}.");
                }

                if (Math.Abs(newClampedZoom - zoom) > 0.001)
                {
                    Debug.WriteLine(
                        $"VM_NpcSelectionBar: ZoomLevel IS being clamped from {zoom:F2} to {newClampedZoom:F2}. Updating property.");
                    NpcsViewZoomLevel = newClampedZoom;
                    return;
                }

                if (NpcsViewIsZoomLocked || NpcsViewHasUserManuallyZoomed)
                {
                    Debug.WriteLine(
                        $"VM_NpcSelectionBar: ZoomLevel processed. IsLocked or ManualZoom. Triggering refresh. Value: {newClampedZoom:F2}");
                    _refreshImageSizesSubject.OnNext(Unit.Default);
                }
                else
                {
                    Debug.WriteLine(
                        $"VM_NpcSelectionBar: ZoomLevel processed. Unlocked & not manual. No VM-initiated refresh. Value: {newClampedZoom:F2}");
                }
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.NpcsViewIsZoomLocked)
            .Skip(1)
            .Subscribe(isLocked =>
            {
                _settings.NpcsViewIsZoomLocked = isLocked;
                NpcsViewHasUserManuallyZoomed = false;
                _refreshImageSizesSubject.OnNext(Unit.Default);
            })
            .DisposeWith(_disposables);


        // --- NEW: Setup for Compare/Hide/Deselect ---
        var checkedMugshotCountObservable = this.WhenAnyValue(x => x.CurrentNpcAppearanceMods)
            .Select(mods =>
            {
                if (mods == null || !mods.Any())
                    return Observable.Return(0);

                var itemCheckedObservables = mods.Select(m =>
                    m.WhenAnyValue(x => x.IsCheckedForCompare)
                        .Select(_ => m.IsCheckedForCompare)
                ).ToList();

                if (!itemCheckedObservables.Any())
                    return Observable.Return(0);

                return Observable.CombineLatest(itemCheckedObservables)
                    .Select(statuses => statuses.Count(isChecked => isChecked));
            })
            .Switch()
            .StartWith(0);

        checkedMugshotCountObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .ToPropertyEx(this, x => x.CheckedMugshotCount)
            .DisposeWith(_disposables);

        var canCompareSelected = this.WhenAnyValue(x => x.CheckedMugshotCount)
            .Select(count => count >= 2);
        CompareSelectedCommand = ReactiveCommand.Create(ExecuteCompareSelected, canCompareSelected);
        CompareSelectedCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error comparing selected: {ex.Message}"))
            .DisposeWith(_disposables);

        // Define the observable for enabling the Hide/Unhide menu button
        var atLeastOneSelected = this.WhenAnyValue(x => x.CheckedMugshotCount)
            .Select(count => count >= 1)
            .StartWith(false); // Start disabled until count is known

        // Convert it to a property
        atLeastOneSelected
            .ToPropertyEx(this, x => x.CanOpenHideUnhideMenu)
            .DisposeWith(_disposables);

        var canExecuteHideUnhideActions = atLeastOneSelected; // Reuse the observable

        HideAllButSelectedCommand = ReactiveCommand.Create(ExecuteHideAllButSelected, canExecuteHideUnhideActions);
        HideAllButSelectedCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error hiding unselected: {ex.Message}"))
            .DisposeWith(_disposables);
        HideAllSelectedCommand = ReactiveCommand.Create(ExecuteHideAllSelected, canExecuteHideUnhideActions);
        HideAllSelectedCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error hiding selected: {ex.Message}"))
            .DisposeWith(_disposables);
        UnhideAllSelectedCommand = ReactiveCommand.Create(ExecuteUnhideAllSelected, canExecuteHideUnhideActions);
        UnhideAllSelectedCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error unhiding selected: {ex.Message}"))
            .DisposeWith(_disposables);
        UnhideAllButSelectedCommand =
            ReactiveCommand.Create(ExecuteUnhideAllButSelected, canExecuteHideUnhideActions);
        UnhideAllButSelectedCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error unhiding unselected: {ex.Message}"))
            .DisposeWith(_disposables);

        var canDeselectAll = this.WhenAnyValue(x => x.CheckedMugshotCount)
            .Select(count => count >= 1);
        DeselectAllCommand = ReactiveCommand.Create(ExecuteDeselectAll, canDeselectAll);
        DeselectAllCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error deselecting all: {ex.Message}"))
            .DisposeWith(_disposables);
        // --- End NEW Setup ---

        // --- NEW: Import/Export Command Setup ---
        ImportChoicesFromLoadOrderCommand = ReactiveCommand.CreateFromTask(ImportChoicesFromLoadOrderAsync);
        ExportChoicesCommand = ReactiveCommand.CreateFromTask(ExportChoicesAsync);
        ImportChoicesCommand = ReactiveCommand.CreateFromTask(ImportChoicesAsync);
        ClearChoicesCommand = ReactiveCommand.Create(ClearChoices);

        ImportChoicesFromLoadOrderCommand.ThrownExceptions
            .Subscribe(ex =>
                ScrollableMessageBox.ShowError($"Error importing choices from load order: {ex.Message}",
                    "Import Error"))
            .DisposeWith(_disposables);
        ExportChoicesCommand.ThrownExceptions
            .Subscribe(ex =>
                ScrollableMessageBox.ShowError($"Error exporting choices: {ex.Message}", "Export Error"))
            .DisposeWith(_disposables);
        ImportChoicesCommand.ThrownExceptions
            .Subscribe(ex =>
                ScrollableMessageBox.ShowError($"Error importing choices: {ex.Message}", "Import Error"))
            .DisposeWith(_disposables);
        ClearChoicesCommand.ThrownExceptions
            .Subscribe(ex => ScrollableMessageBox.ShowError($"Error clearing choices: {ex.Message}", "Clear Error"))
            .DisposeWith(_disposables);
        // --- End Import/Export Setup ---
        
        ShowFavoritesCommand = ReactiveCommand.Create(ShowFavoritesWindowForSharing);
        ShowFavoritesCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error opening favorites: {ex.Message}"));

        AddFavoriteFaceToNpcCommand = ReactiveCommand.Create<VM_NpcsMenuSelection>(ShowFavoritesWindowForApplying);
        AddFavoriteFaceToNpcCommand.ThrownExceptions.Subscribe(ex => ScrollableMessageBox.ShowError($"Error opening favorites: {ex.Message}"));


        if (CurrentNpcAppearanceMods != null && CurrentNpcAppearanceMods.Any())
        {
            _refreshImageSizesSubject.OnNext(Unit.Default);
        }
    }

    // --- Methods ---
    
    private void ShowFavoritesWindowForSharing()
    {
        var vm = new VM_FavoriteFaces(_settings, _consistencyProvider, this, _lazyModsVm.Value, _lazyMainWindowVm, VM_FavoriteFaces.FavoriteFacesMode.Share, null);
        var window = new FavoriteFacesWindow { DataContext = vm, ViewModel = vm };
    
        // Find the currently active window to set as the owner.
        window.Owner = Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive);
    
        window.ShowDialog();
    }

    private void ShowFavoritesWindowForApplying(VM_NpcsMenuSelection targetNpc)
    {
        if (targetNpc == null) return;
        var vm = new VM_FavoriteFaces(_settings, _consistencyProvider, this, _lazyModsVm.Value, _lazyMainWindowVm, VM_FavoriteFaces.FavoriteFacesMode.Apply, targetNpc);
        var window = new FavoriteFacesWindow { DataContext = vm, ViewModel = vm };
    
        // Find the currently active window to set as the owner.
        window.Owner = Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive);
    
        window.ShowDialog();
    }

    // --- NEW: Command Execution Methods ---
    private void ExecuteCompareSelected()
    {
        if (CurrentNpcAppearanceMods == null) return;

        var selectedMugshotVMs = CurrentNpcAppearanceMods
            .Where(m => m.IsCheckedForCompare && m.HasMugshot && !string.IsNullOrEmpty(m.ImagePath) &&
                        File.Exists(m.ImagePath))
            .ToList();

        if (selectedMugshotVMs.Count < 2)
        {
            ScrollableMessageBox.ShowWarning("Please select at least two valid mugshots to compare.",
                "Compare Selected");
            return;
        }

        Debug.WriteLine($"CompareSelected: {selectedMugshotVMs.Count} mugshots selected for comparison.");

        try
        {
            var multiImageVM =
                new VM_MultiImageDisplay(selectedMugshotVMs.Cast<IHasMugshotImage>() /*, _settings */);
            // It's good practice to ensure the new window has an owner if it's a dialog
            var currentWindow = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive);

            var multiImageView = new MultiImageDisplayView
            {
                DataContext = multiImageVM,
                ViewModel = multiImageVM,
                Owner = currentWindow // Set owner for proper dialog behavior
            };

            multiImageView.ShowDialog();

            // After the dialog closes, trigger a refresh in NpcsView to reset sizes based on its context
            _refreshImageSizesSubject.OnNext(Unit.Default);
            Debug.WriteLine("VM_NpcSelectionBar: Triggered NpcsView refresh after compare dialog closed.");
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"Could not open comparison window: {ex.Message}", "Error Comparing");
            Debug.WriteLine($"Error in ExecuteCompareSelected: {ex}");
        }
    }

    private void ExecuteHideAllSelected()
    {
        if (CurrentNpcAppearanceMods == null) return;
        bool refreshNeeded = false;

        foreach (var mugshotVM in CurrentNpcAppearanceMods)
        {
            if (mugshotVM.IsCheckedForCompare)
            {
                if (!mugshotVM.IsSetHidden) // Only hide if *not* already hidden
                {
                    HideSelectedMod(mugshotVM);
                    refreshNeeded = true;
                }
            }
        }

        if (refreshNeeded)
        {
            ToggleModVisibility();
        }

        Debug.WriteLine("HideAllSelected: Marked checked mugshots as hidden.");
    }

    private void ExecuteHideAllButSelected()
    {
        if (CurrentNpcAppearanceMods == null) return;
        bool refreshNeeded = false;

        foreach (var mugshotVM in CurrentNpcAppearanceMods)
        {
            if (!mugshotVM.IsCheckedForCompare)
            {
                // Call the standard hiding function on this view model.
                if (!mugshotVM.IsSetHidden) // Prevent duplicate hiding
                {
                    HideSelectedMod(mugshotVM);
                    refreshNeeded = true;
                }
            }
        }

        if (refreshNeeded)
        {
            ToggleModVisibility();
        }

        Debug.WriteLine("HideAllButSelected: Non-checked mugshots marked as hidden.");
    }

    private void ExecuteUnhideAllSelected()
    {
        if (CurrentNpcAppearanceMods == null) return;
        bool refreshNeeded = false;

        foreach (var mugshotVM in CurrentNpcAppearanceMods)
        {
            if (mugshotVM.IsCheckedForCompare)
            {
                if (mugshotVM.IsSetHidden) // Only unhide if *currently* hidden
                {
                    UnhideSelectedMod(mugshotVM);
                    refreshNeeded = true;
                }
            }
        }

        if (refreshNeeded)
        {
            ToggleModVisibility();
        }

        Debug.WriteLine("UnhideAllSelected: Unhid checked mugshots");
    }

    private void ExecuteUnhideAllButSelected()
    {
        if (CurrentNpcAppearanceMods == null) return;
        bool refreshNeeded = false;

        foreach (var mugshotVM in CurrentNpcAppearanceMods)
        {
            if (!mugshotVM.IsCheckedForCompare)
            {
                if (mugshotVM.IsSetHidden) // Only unhide if *currently* hidden
                {
                    UnhideSelectedMod(mugshotVM);
                    refreshNeeded = true;
                }
            }
        }

        if (refreshNeeded)
        {
            ToggleModVisibility();
        }

        Debug.WriteLine("UnhideAllSelected: Unhid checked mugshots");
    }

    // Added new version of Deselect
    private void ExecuteDeselectAll()
    {
        if (CurrentNpcAppearanceMods == null) return;
        foreach (var mugshotVM in CurrentNpcAppearanceMods)
        {
            mugshotVM.IsCheckedForCompare = false; // Clears the compare selection
        }

        Debug.WriteLine("DeselectAll: All mugshot compare checkboxes cleared.");
    }


    // Define a small, serializable record to structure the JSON output.
    private record NpcChoiceDto(string ModName, string SourceNpcFormKey);

    private async Task ExportChoicesAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            Title = "Export NPC Choices",
            FileName = "MyNpcChoices.json",
            DefaultExt = "json",
            AddExtension = true
        };

        if (dialog.ShowDialog() != DialogResult.OK) return;

        // 1. Transform the settings data into the serializable DTO format.
        // This correctly handles the new (string, FormKey) tuple structure.
        var selectionsToExport = _settings.SelectedAppearanceMods
            .ToDictionary(
                kvp => kvp.Key.ToString(), // Key: The target NPC's FormKey as a string.
                kvp => new NpcChoiceDto(kvp.Value.ModName,
                    kvp.Value.NpcFormKey.ToString()) // Value: The structured choice.
            );

        try
        {
            // 2. Run the synchronous file I/O on a background thread.
            // This keeps the UI responsive and makes the method truly async.
            bool success = await Task.Run(() =>
            {
                JSONhandler<Dictionary<string, NpcChoiceDto>>.SaveJSONFile(
                    selectionsToExport,
                    dialog.FileName,
                    out bool wasSuccessful,
                    out var exceptionString);

                if (!wasSuccessful)
                {
                    // Show the error message on the UI thread.
                    Application.Current.Dispatcher.Invoke(() =>
                        ScrollableMessageBox.ShowError(exceptionString, "Error while exporting NPC Choices"));
                }

                return wasSuccessful;
            });

            if (success)
            {
                ScrollableMessageBox.Show(
                    $"Successfully exported {selectionsToExport.Count} choices to {Path.GetFileName(dialog.FileName)}.",
                    "Export Complete");
            }
        }
        catch (Exception ex)
        {
            // Catch any other unexpected exceptions from Task.Run or message boxes.
            ScrollableMessageBox.ShowError($"Failed to export choices: {ex.Message}", "Export Error");
        }
    }

    private void ClearChoices()
    {
        int currentCount = _settings.SelectedAppearanceMods.Count;
        if (currentCount == 0)
        {
            ScrollableMessageBox.Show("There are no choices to clear.", "No Choices");
            return;
        }

        if (!ScrollableMessageBox.Confirm(
                $"Are you sure you want to clear all {currentCount} of your current NPC choices? This action cannot be undone.",
                "Confirm Clear Choices", MessageBoxImage.Warning))
        {
            return; // User cancelled
        }

        _consistencyProvider.ClearAllSelections();
    }

    private async Task ImportChoicesFromLoadOrderAsync()
    {
        if (!ScrollableMessageBox.Confirm(
                "This will overwrite your current choices based on your load order. This action cannot be undone. Are you sure you want to continue?",
                "Confirm Import Choices", MessageBoxImage.Warning))
        {
            return; // User cancelled
        }

        // Run the entire heavy operation on a background thread.
        var (missingNpcs, unMatchedNpcs) = await Task.Run(() =>
        {
            var missing = new List<string>();
            var unmatched = new List<string>();

            foreach (var npc in AllNpcs)
            {
                if (!_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npc.NpcFormKey, out var npcGetter) || npcGetter == null)
                {
                    missing.Add($"{npc.DisplayName} ({npc.NpcFormKeyString})");
                    continue;
                }

                var winningMod = FindWinningModForNpc(npcGetter);

                if (winningMod != null)
                {
                    // Correctly call the updated SetSelectedMod with the NPC's own FormKey as the source.
                    _consistencyProvider.SetSelectedMod(npc.NpcFormKey, winningMod.DisplayName, npc.NpcFormKey);
                }
                else
                {
                    unmatched.Add(Auxilliary.GetLogString(npcGetter, _settings.LocalizationLanguage, true));
                }
            }
            return (missing, unmatched);
        });

        // Display results on the UI thread after the work is done.
        if (missingNpcs.Any())
        {
            string message = "The following NPCs could not be found in your load order and were skipped:" +
                             Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, missingNpcs);
            ScrollableMessageBox.ShowWarning(message, "Missing NPCs");
        }

        if (unMatchedNpcs.Any())
        {
            string message = "A winning mod could not be identified for the following NPCs:" + Environment.NewLine +
                             Environment.NewLine + string.Join(Environment.NewLine, unMatchedNpcs);
            ScrollableMessageBox.ShowWarning(message, "Unassigned NPCs");
        }
        
        ScrollableMessageBox.Show("Import from load order complete.", "Import Complete");
    }

    /// <summary>
    /// Finds the best-matching appearance mod for a given NPC based on load order and file conflicts.
    /// </summary>
    private VM_ModSetting? FindWinningModForNpc(INpcGetter npcGetter)
    {
        // ResolveAllContexts returns plugins in load order, so the last one is the winner.
        var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcGetter.FormKey);

        foreach (var context in contexts.Reverse()) // Iterate backwards from the winning plugin.
        {
            if (_settings.ImportFromLoadOrderExclusions.Contains(context.ModKey))
            {
                continue;
            }

            var correspondingMods = _lazyModsVm.Value.AllModSettings
                .Where(x => x.CorrespondingModKeys.Contains(context.ModKey)).ToList();

            if (correspondingMods.Count == 1)
            {
                return correspondingMods.First(); // Simple case: one plugin maps to one mod setting.
            }

            if (correspondingMods.Count > 1)
            {
                // Complex case: one plugin maps to multiple mod settings (e.g., FOMOD).
                // We need to check for FaceGen files to find the real winner.
                var winningMod = DisambiguateModsByFaceGen(correspondingMods, npcGetter.FormKey);
                if (winningMod != null)
                {
                    return winningMod;
                }
            }
        }

        return null; // No matching mod found.
    }

    /// <summary>
    /// For a list of candidate mods from the same plugin, determines the winner by matching FaceGen files.
    /// </summary>
    private VM_ModSetting? DisambiguateModsByFaceGen(List<VM_ModSetting> candidateMods, FormKey npcFormKey)
    {
        var (meshSubPath, texSubPath) = Auxilliary.GetFaceGenSubPathStrings(npcFormKey);
        var meshToMatchPath = Path.Combine(_environmentStateProvider.DataFolderPath, "meshes", meshSubPath);
        var texToMatchPath = Path.Combine(_environmentStateProvider.DataFolderPath, "textures", texSubPath);

        bool mustMatchMesh = File.Exists(meshToMatchPath);
        (int meshRefSize, string meshRefHash) = mustMatchMesh ? Auxilliary.GetCheapFileEqualityIdentifiers(meshToMatchPath) : (0, string.Empty);

        bool mustMatchTex = File.Exists(texToMatchPath);
        (int texRefSize, string texRefHash) = mustMatchTex ? Auxilliary.GetCheapFileEqualityIdentifiers(texToMatchPath) : (0, string.Empty);
        
        if (!mustMatchMesh && !mustMatchTex) return null; // No loose files to match against.

        foreach (var candidate in candidateMods)
        {
            foreach (var modFolder in candidate.CorrespondingFolderPaths)
            {
                bool matchedMesh = !mustMatchMesh;
                bool matchedTex = !mustMatchTex;

                if (mustMatchMesh)
                {
                    var candidateMeshPath = Path.Combine(modFolder, "meshes", meshSubPath);
                    if (File.Exists(candidateMeshPath) && Auxilliary.FastFilesAreIdentical(candidateMeshPath, meshRefSize, meshRefHash))
                    {
                        matchedMesh = true;
                    }
                }

                if (mustMatchTex)
                {
                    var candidateTexPath = Path.Combine(modFolder, "textures", texSubPath);
                    if (File.Exists(candidateTexPath) && Auxilliary.FastFilesAreIdentical(candidateTexPath, texRefSize, texRefHash))
                    {
                        matchedTex = true;
                    }
                }

                if (matchedMesh && matchedTex)
                {
                    return candidate; // Found the mod that provides the winning loose files.
                }
            }
        }

        return null; // No candidate provided matching files.
    }

    /// <summary>
    /// Holds the results of the import file validation process.
    /// </summary>
    private record ImportValidationReport(
        Dictionary<FormKey, (string ModName, FormKey NpcFormKey)> ValidSelections,
        List<string> MalformedEntries,
        List<string> UnresolvedNpcs,
        List<string> UnrecognizedMods
    );
    
    private async Task ImportChoicesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            Title = "Import NPC Choices",
            CheckFileExists = true,
            CheckPathExists = true
        };

        if (dialog.ShowDialog() != DialogResult.OK) return;

        try
        {
            // Run the entire import and validation process on a background thread.
            await Task.Run(() =>
            {
                // 1. Deserialize the JSON into our new DTO format.
                var importedData = JSONhandler<Dictionary<string, NpcChoiceDto>>.LoadJSONFile(
                    dialog.FileName, 
                    out bool readSuccess, 
                    out var exceptionStr);

                if (!readSuccess)
                {
                    Application.Current.Dispatcher.Invoke(() => 
                        ScrollableMessageBox.ShowError(exceptionStr, "Failed to Read Import File"));
                    return;
                }

                if (importedData == null || !importedData.Any())
                {
                    Application.Current.Dispatcher.Invoke(() => 
                        ScrollableMessageBox.ShowWarning("The selected file is empty or contains no valid data.", "Import Warning"));
                    return;
                }

                // 2. Validate the data against the current load order and settings.
                var report = ValidateImportData(importedData);
                var issues = report.MalformedEntries.Concat(report.UnresolvedNpcs).Concat(report.UnrecognizedMods).ToList();

                // 3. Show a confirmation dialog on the UI thread.
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var reportMessage = new StringBuilder();
                    if (issues.Any())
                    {
                        reportMessage.AppendLine($"The import file contains {issues.Count} issue(s) that will be skipped.\n");
                        if(report.MalformedEntries.Any()) reportMessage.AppendLine("--- Malformed Entries ---\n" + string.Join('\n', report.MalformedEntries) + "\n");
                        if(report.UnresolvedNpcs.Any()) reportMessage.AppendLine("--- Unresolved NPCs ---\n" + string.Join('\n', report.UnresolvedNpcs) + "\n");
                        if(report.UnrecognizedMods.Any()) reportMessage.AppendLine("--- Unrecognized Mods/Choices ---\n" + string.Join('\n', report.UnrecognizedMods) + "\n");
                        reportMessage.AppendLine($"Do you want to proceed with importing the {report.ValidSelections.Count} valid choices?");
                    }
                    else
                    {
                        reportMessage.Append($"This will overwrite your current choices with {report.ValidSelections.Count} choice(s) from the file. Proceed?");
                    }

                    if (ScrollableMessageBox.Confirm(reportMessage.ToString(), "Confirm Import", issues.Any() ? MessageBoxImage.Warning : MessageBoxImage.Question))
                    {
                        // 4. If confirmed, apply the valid selections.
                        _consistencyProvider.ClearAllSelections();
                        foreach (var kvp in report.ValidSelections)
                        {
                            _consistencyProvider.SetSelectedMod(kvp.Key, kvp.Value.ModName, kvp.Value.NpcFormKey);
                        }
                        ScrollableMessageBox.Show($"Import complete. {report.ValidSelections.Count} choices have been applied.", "Import Successful");
                    }
                    else
                    {
                        ScrollableMessageBox.Show("Import cancelled by user.", "Import Cancelled");
                    }
                });
            });
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"An unexpected error occurred during import: {ex.Message}", "Import Error");
        }
    }

    /// <summary>
    /// Validates deserialized import data against the current application state.
    /// </summary>
    private ImportValidationReport ValidateImportData(Dictionary<string, NpcChoiceDto> importedData)
    {
        var validSelections = new Dictionary<FormKey, (string ModName, FormKey NpcFormKey)>();
        var malformed = new List<string>();
        var unresolved = new List<string>();
        var unrecognized = new List<string>();

        var availableModNames = new HashSet<string>(_lazyModsVm.Value.AllModSettings.Select(m => m.DisplayName), StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in importedData)
        {
            // Validate and parse FormKeys
            if (!FormKey.TryFactory(kvp.Key, out var targetNpcKey))
            {
                malformed.Add($"- Invalid target NPC FormKey string: {kvp.Key}");
                continue;
            }
            if (!FormKey.TryFactory(kvp.Value.SourceNpcFormKey, out var sourceNpcKey))
            {
                malformed.Add($"- Invalid source NPC FormKey string for {targetNpcKey}: {kvp.Value.SourceNpcFormKey}");
                continue;
            }

            // Validate existence of NPCs and Mod
            bool targetNpcExists = _environmentStateProvider.LinkCache.TryResolve<INpcGetter>(targetNpcKey, out _);
            bool sourceNpcExists = _environmentStateProvider.LinkCache.TryResolve<INpcGetter>(sourceNpcKey, out _);
            bool modExists = availableModNames.Contains(kvp.Value.ModName);

            if (!targetNpcExists) unresolved.Add($"- Target NPC {targetNpcKey} not found in load order.");
            if (!sourceNpcExists) unresolved.Add($"- Source NPC {sourceNpcKey} (for {targetNpcKey}) not found in load order.");
            if (!modExists) unrecognized.Add($"- Appearance Mod '{kvp.Value.ModName}' (for {targetNpcKey}) not found or installed.");
            
            if (targetNpcExists && sourceNpcExists && modExists)
            {
                validSelections.Add(targetNpcKey, (kvp.Value.ModName, sourceNpcKey));
            }
        }

        return new ImportValidationReport(validSelections, malformed, unresolved, unrecognized);
    }

    public bool CanJumpToMod(string appearanceModName)
    {
        var modsVm = _lazyModsVm.Value;
        if (modsVm == null)
        {
            return false;
        }

        var targetModSetting = modsVm.AllModSettings.FirstOrDefault(ms =>
            ms.DisplayName.Equals(appearanceModName, StringComparison.OrdinalIgnoreCase));
        return targetModSetting != null;
    }

    public void JumpToMod(VM_NpcsMenuMugshot npcsMenuMugshot)
    {
        if (npcsMenuMugshot == null || string.IsNullOrWhiteSpace(npcsMenuMugshot.ModName)) return;

        string targetModName = npcsMenuMugshot.ModName;
        Debug.WriteLine($"VM_NpcSelectionBar.JumpToMod: Requested for {targetModName}");

        var modsVm = _lazyModsVm.Value;
        if (modsVm == null)
        {
            ScrollableMessageBox.ShowError("Mods view model is not available.");
            return;
        }

        var targetModSetting = modsVm.AllModSettings.FirstOrDefault(ms =>
            ms.DisplayName.Equals(targetModName, StringComparison.OrdinalIgnoreCase));

        if (targetModSetting != null)
        {
            Debug.WriteLine(
                $"VM_NpcSelectionBar.JumpToMod: Found target VM_ModSetting: {targetModSetting.DisplayName}");
            var mainWindowVm = _lazyMainWindowVm.Value;
            if (mainWindowVm == null)
            {
                ScrollableMessageBox.ShowError("Main window view model is not available.");
                return;
            }

            mainWindowVm.IsModsTabSelected = true;

            RxApp.MainThreadScheduler.Schedule(TimeSpan.FromMilliseconds(100), () =>
            {
                if (!modsVm.ModSettingsList.Contains(targetModSetting))
                {
                    Debug.WriteLine(
                        $"VM_NpcSelectionBar.JumpToMod: Target mod {targetModSetting.DisplayName} not in filtered list. Clearing filters.");
                    modsVm.NameFilterText = string.Empty;
                    modsVm.PluginFilterText = string.Empty;
                    modsVm.NpcSearchText = string.Empty;
                }

                modsVm.ShowMugshotsCommand.Execute(targetModSetting)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(
                        _ =>
                        {
                            Debug.WriteLine(
                                $"VM_NpcSelectionBar.JumpToMod: Successfully triggered ShowMugshots for {targetModSetting.DisplayName}. VM_Mods will signal scroll.");
                        },
                        ex =>
                        {
                            Debug.WriteLine(
                                $"VM_NpcSelectionBar.JumpToMod: Error executing ShowMugshotsCommand: {ex.Message}");
                        }
                    ).DisposeWith(_disposables);
            });
        }
        else
        {
            Debug.WriteLine(
                $"VM_NpcSelectionBar.JumpToMod: Could not find VM_ModSetting with DisplayName: {targetModName}");
            ScrollableMessageBox.ShowWarning($"Could not find the mod '{targetModName}' in the Mods list.",
                "Mod Not Found");
        }
    }

    private void UpdateSelectionState(FormKey npcFormKey, string? selectedModName, FormKey sourceNpcFormKey)
    {
        var npcVM = AllNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(npcFormKey));

        if (npcVM != null)
        {
            npcVM.SelectedAppearanceModName = selectedModName;
            if (SelectedNpc == npcVM && CurrentNpcAppearanceMods != null)
            {
                foreach (var modVM in CurrentNpcAppearanceMods)
                {
                    modVM.IsSelected = modVM.ModName.Equals(selectedModName, StringComparison.OrdinalIgnoreCase) &&
                                       modVM.SourceNpcFormKey.Equals(sourceNpcFormKey);
                }
            }
        }
    }

    private static readonly Regex PluginRegex =
        new(@"^.+\.(esm|esp|esl)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HexFileRegex = new(@"^[0-9A-F]{8}\.(png|jpg|jpeg|bmp)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private Dictionary<FormKey, List<(string ModName, string ImagePath)>> ScanMugshotDirectory(
        VM_SplashScreen? splashReporter)
    {
        var results = new Dictionary<FormKey, List<(string ModName, string ImagePath)>>();
        if (string.IsNullOrWhiteSpace(_settings.MugshotsFolder) || !Directory.Exists(_settings.MugshotsFolder))
            return results;

        System.Diagnostics.Debug.WriteLine($"Scanning mugshot directory: {_settings.MugshotsFolder}");
        string expectedParentPath =
            _settings.MugshotsFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        try
        {
            var potentialFiles = Directory
                .EnumerateFiles(_settings.MugshotsFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => HexFileRegex.IsMatch(Path.GetFileName(f)));

            int fileCount = potentialFiles.Count();
            int scannedFileCount = 0;
            using (ContextualPerformanceTracer.Trace("ScanMugshotDirectory.FileLoop"))
            {
                foreach (var filePath in potentialFiles)
                {
                    scannedFileCount++;
                    if (scannedFileCount % 200 == 0)
                    {
                        // *** MODIFY progress calculation to be a self-contained 0-100% run ***
                        var progress = (double)scannedFileCount / fileCount * 100.0;
                        splashReporter?.UpdateProgress(progress, $"Scanning mugshot files: {scannedFileCount} / {fileCount}");
                    }

                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        string hexFileName = fileInfo.Name;
                        DirectoryInfo? pluginDir = fileInfo.Directory;
                        if (pluginDir == null || !PluginRegex.IsMatch(pluginDir.Name)) continue;
                        string pluginName = pluginDir.Name;
                        DirectoryInfo? modDir = pluginDir.Parent;
                        if (modDir == null || string.IsNullOrWhiteSpace(modDir.Name)) continue;
                        string modName = modDir.Name;
                        if (modDir.Parent == null ||
                            !modDir.Parent.FullName.Equals(expectedParentPath, StringComparison.OrdinalIgnoreCase))
                            continue;
                        string hexPart = Path.GetFileNameWithoutExtension(hexFileName);
                        if (hexPart.Length != 8) continue;
                        string formKeyString = $"{hexPart.Substring(hexPart.Length - 6)}:{pluginName}";
                        try
                        {
                            var formKey = FormKey.Factory(formKeyString);
                            var mugshotInfo = (ModName: modName, ImagePath: filePath);
                            if (results.TryGetValue(formKey, out var list))
                            {
                                if (!list.Any(i => i.ModName.Equals(modName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    list.Add(mugshotInfo);
                                }
                            }
                            else
                            {
                                results[formKey] = new List<(string ModName, string ImagePath)> { mugshotInfo };
                            }
                        }
                        catch
                        {
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error processing mugshot file '{filePath}': {ex.Message}");
                    }
                }
            }

            splashReporter?.UpdateProgress(100, $"Finished scanning {fileCount.ToString()} Mugshots.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error scanning mugshot directory '{_settings.MugshotsFolder}': {ex.Message}");
        }

        System.Diagnostics.Debug.WriteLine(
            $"Mugshot scan complete. Found entries for {results.Count} unique FormKeys.");
        return results;
    }

    // This used to safely transfer processed data from the background thread to the UI thread.
    private record NpcInitializationData
    {
        public NpcDisplayData NpcData { get; init; }
        public List<VM_ModSetting> AppearanceMods { get; init; } = new();
    }
    
    public async Task InitializeAsync(VM_SplashScreen? splashReporter)
    {
        // 1. UI-thread cleanup (unchanged)
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SelectedNpc = null;
            AllNpcs.Clear();
            FilteredNpcs.Clear();
            CurrentNpcDescription = null;
        });

        if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
        {
            splashReporter?.UpdateStep("Environment not valid for NPC list.");
            splashReporter?.ShowMessagesOnClose(
                $"NPC Bar: InitializeAsync: Environment is not valid. You should only see this message if you launch this program and you don't have Skyrim SE/AE installed in your SteamApps directory. Go to your settings and point them at your correct Data folder and Game version.");

            _mugshotData.Clear();
            return;
        }

        // --- Scan Mugshots (largely unchanged) ---
        splashReporter?.UpdateStep("Scanning mugshot directory...");
        using (ContextualPerformanceTracer.Trace("InitializeNpcs.ScanMugshots"))
        {
            _mugshotData = await Task.Run(() => ScanMugshotDirectory(splashReporter));
        }

        await Application.Current.Dispatcher.InvokeAsync(UpdateAvailableNpcGroups);
        splashReporter?.UpdateStep("Analyzing NPC data...");

        // --- OPTIMIZATION: New batched approach ---
        Dictionary<FormKey, NpcDisplayData> npcDisplayDataCache = new();
        Dictionary<FormKey, VM_NpcsMenuSelection> npcViewModelMap = new();

        await Task.Run(() =>
        {
            // 2. AGGREGATE all unique FormKeys from all sources first.
            var allRequiredNpcKeys = new HashSet<FormKey>();
            if (_lazyModsVm.Value?.AllModSettings != null)
            {
                foreach (var modSetting in _lazyModsVm.Value.AllModSettings)
                {
                    foreach (var formKey in modSetting.NpcFormKeysToDisplayName.Keys)
                    {
                        allRequiredNpcKeys.Add(formKey);
                    }
                }
            }

            foreach (var key in _mugshotData.Keys)
            {
                allRequiredNpcKeys.Add(key);
            }

            // 3. BATCH PROCESS: Resolve all NPCs in a single pass.
            splashReporter?.UpdateStep("Resolving NPC records", allRequiredNpcKeys.Count);
            foreach (var npcFormKey in allRequiredNpcKeys)
            {
                if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey, out var npcGetter))
                {
                    // Store lightweight data, discard the heavy getter.
                    var npcData = NpcDisplayData.FromGetter(npcGetter);
                    npcDisplayDataCache[npcFormKey] = npcData;
                }
                else
                {
                    var npcData = NpcDisplayData.FromFormKey(npcFormKey);
                    if (!npcDisplayDataCache.ContainsKey(npcFormKey))
                    {
                        npcDisplayDataCache.Add(npcFormKey, npcData);
                    }
                    splashReporter?.IncrementProgress(npcFormKey.ToString());
                }
            }

            // 4. POPULATE: Create all ViewModel objects from the cached data.
            splashReporter?.UpdateStep("Creating NPC list", npcDisplayDataCache.Count);

            // Create VMs for NPCs that were successfully resolved
            foreach (var kvp in npcDisplayDataCache)
            {
                var npcVM = new VM_NpcsMenuSelection(kvp.Key, _environmentStateProvider, this, _auxilliary);
                npcVM.UpdateWithData(kvp.Value, _settings.LocalizationLanguage);
                npcViewModelMap[kvp.Key] = npcVM;
                splashReporter?.IncrementProgress(npcVM.DisplayName);
            }

            // Create placeholder VMs for mugshot-only NPCs that couldn't be resolved in the load order
            splashReporter?.UpdateStep("Adding Loose Mugshots", _mugshotData.Count);
            foreach (var mugshotKey in _mugshotData.Keys)
            {
                if (!npcViewModelMap.ContainsKey(mugshotKey))
                {
                    var npcVM = new VM_NpcsMenuSelection(mugshotKey, _environmentStateProvider, this, _auxilliary);
                    npcVM.IsInLoadOrder = false;
                    npcViewModelMap[mugshotKey] = npcVM;
                    splashReporter?.IncrementProgress(npcVM.DisplayName);
                }
            }

            // Assign appearance mods to the newly created ViewModels
            if (_lazyModsVm.Value?.AllModSettings != null)
            {
                foreach (var modSetting in _lazyModsVm.Value.AllModSettings)
                {
                    foreach (var npcFormKey in modSetting.NpcFormKeysToDisplayName.Keys)
                    {
                        if (npcViewModelMap.TryGetValue(npcFormKey, out var npcVM))
                        {
                            npcVM.AppearanceMods.Add(modSetting);
                        }
                    }
                }
            }
        });

        // 5. Finalize on UI thread
        splashReporter?.UpdateStep("Finalizing NPC List...");
        using (ContextualPerformanceTracer.Trace("InitializeNpcs.FinalCleanup"))
        {
            // Add all created VMs to the final list
            AllNpcs.AddRange(npcViewModelMap.Values);
            
            // Update group display string for each NPC on initial load
            foreach (var npcVM in AllNpcs)
            {
                _settings.NpcGroupAssignments.TryGetValue(npcVM.NpcFormKey, out var groups);
                npcVM.UpdateGroupDisplay(groups);
            }

            // Remove any NPCs that ultimately have no appearance sources
            for (int i = AllNpcs.Count - 1; i >= 0; i--)
            {
                var currentNpc = AllNpcs[i];
                if (!currentNpc.AppearanceMods.Any() && !_mugshotData.ContainsKey(currentNpc.NpcFormKey))
                {
                    AllNpcs.RemoveAt(i);
                }
            }
        }

        await Application.Current.Dispatcher.InvokeAsync(() => ApplyFilter(initializing: true));

        // 6. Restore selection (unchanged)
        VM_NpcsMenuSelection? npcToSelectOnLoad = null;
        if (!_settings.LastSelectedNpcFormKey.IsNull)
        {
            npcToSelectOnLoad = FilteredNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(_settings.LastSelectedNpcFormKey))
                                ?? AllNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(_settings.LastSelectedNpcFormKey));
        }

        SelectedNpc = npcToSelectOnLoad ?? FilteredNpcs.FirstOrDefault();

        if (SelectedNpc != null)
        {
            _requestScrollToNpcSubject.OnNext(SelectedNpc);
        }

        splashReporter?.UpdateStep("NPC list initialized.");
    }


    public void SignalScrollToNpc(VM_NpcsMenuSelection? npc)
    {
        if (npc != null)
        {
            Debug.WriteLine($"VM_NpcSelectionBar: Explicit signal to scroll to {npc.DisplayName}");
            _requestScrollToNpcSubject.OnNext(npc);
        }
        else
        {
            _requestScrollToNpcSubject.OnNext(null);
        }
    }

    // In VM_NpcSelectionBar.cs
    public void ApplyFilter(bool initializing, bool preserveSelection = true)
    {
        List<VM_NpcsMenuSelection> results = AllNpcs;

        if (!ShowSingleOptionNpcs)
        {
            results = results.Where(n => n.AppearanceMods.Count > 1).ToList();
        }

        if (!ShowUnloadedNpcs)
        {
            results = results.Where(n => n.IsInLoadOrder).ToList();
        }
        
        var predicates = new List<Func<VM_NpcsMenuSelection, bool>>();
        
        // Preserve the currently selected NPC
        var npcToPreserve = SelectedNpc;
        
        // cache share status if necessary

        HashSet<FormKey> allShareSources = new();
        HashSet<FormKey> allSelectedShareSources = new();

        if (SearchType1 == NpcSearchType.ShareStatus || SearchType2 == NpcSearchType.ShareStatus ||
            SearchType3 == NpcSearchType.ShareStatus)
        {
            allShareSources.UnionWith(
                _settings.GuestAppearances.Values.SelectMany(guestSet => guestSet.Select(g => g.Item2))
            );

            foreach (var (targetNpc, guestSet) in _settings.GuestAppearances)
            {
                foreach (var (modName, sourceNpc, _) in guestSet)
                {
                    if (_consistencyProvider.IsModSelected(targetNpc, modName, sourceNpc))
                    {
                        allSelectedShareSources.Add(sourceNpc);
                    }
                }
            }
        }

        // --- Your existing predicate building logic ---
        if (SearchType1 == NpcSearchType.SelectionState)
        {
            predicates.Add(npc => CheckSelectionState(npc, SelectedStateFilter1));
        }
        else if (SearchType1 == NpcSearchType.ShareStatus) // NEW
        {
            predicates.Add(npc => CheckShareStatus(npc, SelectedShareStatusFilter1, allShareSources, allSelectedShareSources));
        }
        else if (SearchType1 == NpcSearchType.Group)
        {
            var p = BuildGroupPredicate(SelectedGroupFilter1);
            if (p != null) predicates.Add(p);
        }
        else if (!string.IsNullOrWhiteSpace(SearchText1))
        {
            var p = BuildTextPredicate(SearchType1, SearchText1);
            if (p != null) predicates.Add(p);
        }

        if (SearchType2 == NpcSearchType.SelectionState)
        {
            predicates.Add(npc => CheckSelectionState(npc, SelectedStateFilter2));
        }
        else if (SearchType2 == NpcSearchType.ShareStatus) // NEW
        {
            predicates.Add(npc => CheckShareStatus(npc, SelectedShareStatusFilter2, allShareSources, allSelectedShareSources));
        }
        else if (SearchType2 == NpcSearchType.Group)
        {
            var p = BuildGroupPredicate(SelectedGroupFilter2);
            if (p != null) predicates.Add(p);
        }
        else if (!string.IsNullOrWhiteSpace(SearchText2))
        {
            var p = BuildTextPredicate(SearchType2, SearchText2);
            if (p != null) predicates.Add(p);
        }

        if (SearchType3 == NpcSearchType.SelectionState)
        {
            predicates.Add(npc => CheckSelectionState(npc, SelectedStateFilter3));
        }
        else if (SearchType3 == NpcSearchType.ShareStatus) // NEW
        {
            predicates.Add(npc => CheckShareStatus(npc, SelectedShareStatusFilter3, allShareSources, allSelectedShareSources));
        }
        else if (SearchType3 == NpcSearchType.Group)
        {
            var p = BuildGroupPredicate(SelectedGroupFilter3);
            if (p != null) predicates.Add(p);
        }
        else if (!string.IsNullOrWhiteSpace(SearchText3))
        {
            var p = BuildTextPredicate(SearchType3, SearchText3);
            if (p != null) predicates.Add(p);
        }
        // --- End predicate building ---

        if (predicates.Any())
        {
            if (IsSearchAndLogic)
            {
                results = results.Where(npc => predicates.All(p => p(npc))).ToList();
            }
            else
            {
                results = results.Where(npc => predicates.Any(p => p(npc))).ToList();
            }
        }
        
        //results.Sort((a, b) => StrCmpLogicalW(a.FormIdString, b.FormIdString));
        results.SortByFormId();

        FilteredNpcs.Clear();
        foreach (var npc in results)
        {
            FilteredNpcs.Add(npc);
        }

        // If a programmatic navigation is in progress, VM_Mods will handle setting SelectedNpc.
        // ApplyFilter should only update the FilteredNpcs list and not interfere with the selection.
        if (IsProgrammaticNavigationInProgress)
        {
            Debug.WriteLine(
                $"ApplyFilter: Programmatic navigation in progress (IsProgrammaticNavigationInProgress=true). FilteredNpcs updated. Deferring selection to VM_Mods.");
            // We don't change SelectedNpc here. VM_Mods will set it explicitly.
            // We must ensure that the target NPC (which VM_Mods *will* select) is actually in FilteredNpcs.
            // If SelectedNpc is already set to the navigation target, and it's NOT in FilteredNpcs,
            // then SelectedNpc might become null due to ListBox behavior.
            // However, VM_Mods will re-set it.
            return; // Exit early, let VM_Mods control selection.
        }

        // Standard selection logic if not navigating programmatically
        var previouslySelectedNpcKey = npcToPreserve?.NpcFormKey;
        VM_NpcsMenuSelection? newSelection = null;

        if (previouslySelectedNpcKey != null)
        {
            newSelection = FilteredNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(previouslySelectedNpcKey));
        }

        if (newSelection == null && FilteredNpcs.Any() && !initializing)
        {
            Debug.WriteLine(
                $"ApplyFilter: Auto-selecting first NPC ('{FilteredNpcs[0]?.DisplayName ?? "null"}') from filtered list because previous selection was lost or null, and not initializing.");
            newSelection = FilteredNpcs[0];
        }

        if (SelectedNpc != newSelection && preserveSelection) // Only update if it's actually different
        {
            Debug.WriteLine(
                $"ApplyFilter: Setting SelectedNpc to '{newSelection?.DisplayName ?? "null"}'. Previous was '{SelectedNpc?.DisplayName ?? "null"}'.");
            SelectedNpc = newSelection;
        }
        else
        {
            Debug.WriteLine(
                $"ApplyFilter: SelectedNpc ('{SelectedNpc?.DisplayName ?? "null"}') remains unchanged.");
        }
    }

    private bool CheckSelectionState(VM_NpcsMenuSelection npcMenu, SelectionStateFilterType filterState)
    {
        // 1. Get the selection tuple. A selection is considered "made" if a ModName exists.
        var selection = _consistencyProvider.GetSelectedMod(npcMenu.NpcFormKey);
        bool isSelected = !string.IsNullOrEmpty(selection.ModName);

        // 2. Determine the desired state from the filter.
        bool filterWantsSelectionMade = (filterState == SelectionStateFilterType.Made);

        // 3. Return true only if the NPC's state matches the filter's desired state.
        return isSelected == filterWantsSelectionMade;
    }

    private Func<VM_NpcsMenuSelection, bool>? BuildGroupPredicate(string? selectedGroup)
    {
        if (string.IsNullOrWhiteSpace(selectedGroup) || selectedGroup == AllNpcsGroup)
        {
            return null;
        }

        return npc => _settings.NpcGroupAssignments.TryGetValue(npc.NpcFormKey, out var groups) &&
                      groups != null &&
                      groups.Contains(selectedGroup);
    }
    
    private bool CheckShareStatus(
        VM_NpcsMenuSelection npcMenu, 
        ShareStatusFilterType filterType,
        HashSet<FormKey> allShareSources,
        HashSet<FormKey> allSelectedShareSources)
    {
        // Check if the NPC is a guest at all (i.e., has shared appearances available).
        bool isGuest = _settings.GuestAppearances.ContainsKey(npcMenu.NpcFormKey);

        // Check if the NPC's currently selected appearance is a guest appearance.
        var selection = _consistencyProvider.GetSelectedMod(npcMenu.NpcFormKey);
        bool isGuestSelected = isGuest && selection.ModName != null && !selection.SourceNpcFormKey.Equals(npcMenu.NpcFormKey);

        switch (filterType)
        {
            case ShareStatusFilterType.Any:
                // An NPC is involved in sharing if it's a guest OR a source.
                return isGuest || allShareSources.Contains(npcMenu.NpcFormKey);

            case ShareStatusFilterType.GuestAvailable:
                // The NPC has guest appearances available but does NOT have one selected.
                return isGuest && !isGuestSelected;

            case ShareStatusFilterType.GuestSelected:
                // The NPC has a guest appearance currently selected.
                return isGuestSelected;

            case ShareStatusFilterType.Shared:
                // The NPC provides an appearance to at least one other NPC.
                return allShareSources.Contains(npcMenu.NpcFormKey);

            case ShareStatusFilterType.SharedAndSelected:
                // The NPC is a share source AND at least one guest has it selected.
                return allSelectedShareSources.Contains(npcMenu.NpcFormKey);

            default:
                return true;
        }
    }

    private Func<VM_NpcsMenuSelection, bool>? BuildTextPredicate(NpcSearchType type, string searchText)
    {
        if (type == NpcSearchType.SelectionState || type == NpcSearchType.Group || type == NpcSearchType.ShareStatus ||
            string.IsNullOrWhiteSpace(searchText))
        {
            return null;
        }

        string searchTextLower = searchText.Trim().ToLowerInvariant();
        switch (type)
        {
            case NpcSearchType.Name:
                return npc => npc.DisplayName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false;
            case NpcSearchType.EditorID:
                // Use the lightweight NpcData object
                return npc =>
                    npc.NpcData?.EditorID?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false;
            case NpcSearchType.InAppearanceMod:
                return npc =>
                    npc.AppearanceMods.Any(m =>
                        m.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase)) ||
                    (_mugshotData.TryGetValue(npc.NpcFormKey, out var mugshots) &&
                     mugshots.Any(m => m.ModName.Contains(searchText, StringComparison.OrdinalIgnoreCase)));
            case NpcSearchType.ChosenInMod:
                return npc =>
                    _consistencyProvider.GetSelectedMod(npc.NpcFormKey).ModName?
                        .Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false;
            case NpcSearchType.FromPlugin:
                return npc =>
                    npc.NpcFormKey.ModKey.FileName.String.Contains(searchText, StringComparison.OrdinalIgnoreCase);
            case NpcSearchType.FormKey:
                return npc => npc.NpcFormKey.ToString().Contains(searchText, StringComparison.OrdinalIgnoreCase);
            default:
                return null;
        }
    }
    
    private ObservableCollection<VM_NpcsMenuMugshot> CreateMugShotViewModels(VM_NpcsMenuSelection selectionVm,
        Dictionary<FormKey, List<(string ModName, string ImagePath)>> mugshotData)
    {
        if (selectionVm == null) return new ObservableCollection<VM_NpcsMenuMugshot>();

        var finalModVMs = new Dictionary<(string ModName, FormKey SourceKey), VM_NpcsMenuMugshot>();
        var targetNpcFormKey = selectionVm.NpcFormKey;

        // Helper function to centralize VM creation and prevent duplicates.
        void CreateVmIfNotExists(string modName, FormKey sourceNpcKey, string? overrideSourceNpc = null)
        {
            var vmKey = (modName, sourceNpcKey);
            if (finalModVMs.ContainsKey(vmKey)) return;

            // Find an associated mod setting if it exists. This is optional.
            var modSettingVM = _lazyModsVm.Value.AllModSettings.FirstOrDefault(m => m.DisplayName.Equals(modName, StringComparison.OrdinalIgnoreCase));
            
            string? imagePath = GetImagePathForNpc(modSettingVM, sourceNpcKey, mugshotData);
            var specificPluginKey = GetPluginKeyForNpc(modSettingVM, sourceNpcKey);

            var appearanceVM = _appearanceModFactory(
                modName,
                selectionVm.DisplayName,
                targetNpcFormKey,
                sourceNpcKey,
                specificPluginKey,
                imagePath
            );
            
            // Add issue notifications if the mod setting exists and has them.
            if (modSettingVM != null && modSettingVM.NpcFormKeysToNotifications.TryGetValue(sourceNpcKey, out var notif))
            {
                appearanceVM.HasIssueNotification = true;
                appearanceVM.IssueType = notif.IssueType;
                appearanceVM.IssueNotificationText = notif.IssueMessage;

                if (notif.IssueType == NpcIssueType.Template)
                {
                    if (notif.ReferencedFormKey != null)
                    {
                        var assignment = _consistencyProvider.GetSelectedMod(notif.ReferencedFormKey.Value);
                        if (assignment.ModName != null)
                        {
                            appearanceVM.IssueNotificationText += "\n" + $"The template NPC is currently set to: {assignment.ModName}";
                            if (!assignment.SourceNpcFormKey.Equals(notif.ReferencedFormKey))
                            {
                                appearanceVM.IssueNotificationText +=
                                    $" (using appearance from {assignment.SourceNpcFormKey.ToString()})";
                            }
                        }
                        else
                        {
                            appearanceVM.IssueNotificationText += "\n" + $"The template NPC does not yet have an appearance mod assigned";
                        }
                    }
                }
            }
            
            // Add the name of the original NPC, if different from source
            if (overrideSourceNpc is not null)
            {
                appearanceVM.OriginalTargetName = overrideSourceNpc;
            }

            finalModVMs.Add(vmKey, appearanceVM);
        }

        // --- Source 1: Standard appearances from the NPC's game data ---
        foreach (var modSetting in selectionVm.AppearanceMods)
        {
            CreateVmIfNotExists(modSetting.DisplayName, targetNpcFormKey);
        }

        // --- Source 2: Guest appearances from settings ---
        if (_settings.GuestAppearances.TryGetValue(targetNpcFormKey, out var guestList))
        {
            foreach (var guest in guestList)
            {
                CreateVmIfNotExists(guest.ModName, guest.NpcFormKey, guest.NpcDisplayName);
            }
        }

        // --- Source 3: All other mugshots from the cache for this NPC ---
        // This corrected section ensures mugshot-only mods are always included.
        if (mugshotData.TryGetValue(targetNpcFormKey, out var allMugshotsForNpc))
        {
            foreach (var mugshotInfo in allMugshotsForNpc)
            {
                // The source NPC for a standard mugshot is the target NPC itself.
                if (_lazyModsVm.Value.AllModSettings.Any(m => m.DisplayName.Equals(mugshotInfo.ModName, StringComparison.OrdinalIgnoreCase)))
                {
                    CreateVmIfNotExists(mugshotInfo.ModName, targetNpcFormKey);
                }
            }
        }

        // --- Finalize: Sort, configure, and set the current selection ---
        var npcSourcePlugin = targetNpcFormKey.ModKey;
        var sortedVMs = finalModVMs.Values
                        // Primary sort: Use OrderByDescending on a boolean to put the "native" mod first.
                        .OrderByDescending(vm => vm.AssociatedModSetting?.CorrespondingModKeys.Contains(npcSourcePlugin) ?? false)
                        // Secondary sort: Alphabetical by the appearance mod's name.
                        .ThenBy(vm => vm.ModName)
                        // Tertiary sort: For guest appearances from the same mod, sort by source NPC.
                        .ThenBy(vm => vm.SourceNpcFormKey.ToString())
                        .ToList();
        
        // Configure IsSetHidden and IsCheckedForCompare properties
        foreach (var m in sortedVMs)
        {
            bool isGloballyHidden = _hiddenModNames.Contains(m.ModName);
            bool isPerNpcHidden = _hiddenModsPerNpc.TryGetValue(targetNpcFormKey, out var hiddenSet) && hiddenSet.Contains(m.ModName);
            m.IsSetHidden = isGloballyHidden || isPerNpcHidden;
            m.IsCheckedForCompare = false;
        }

        // Set the currently selected item's border
        var (selectedModName, selectedSourceKey) = _consistencyProvider.GetSelectedMod(targetNpcFormKey);
        if (!string.IsNullOrEmpty(selectedModName))
        {
            var selectedVmInstance = sortedVMs.FirstOrDefault(x =>
                x.ModName.Equals(selectedModName, StringComparison.OrdinalIgnoreCase) && x.SourceNpcFormKey.Equals(selectedSourceKey));
            if (selectedVmInstance != null)
            {
                selectedVmInstance.IsSelected = true;
            }
        }

        return new ObservableCollection<VM_NpcsMenuMugshot>(sortedVMs);
    }

    // You will also need this helper method if you don't have it already.
    private ModKey? GetPluginKeyForNpc(VM_ModSetting? modSetting, FormKey npcFormKey)
    {
        if (modSetting == null) return null;

        if (modSetting.NpcPluginDisambiguation.TryGetValue(npcFormKey, out var mappedSourceKey))
        {
            return mappedSourceKey;
        }
        
        if (modSetting.AvailablePluginsForNpcs.TryGetValue(npcFormKey, out var candidatePlugins) && candidatePlugins.Any())
        {
            return candidatePlugins.First();
        }
        
        return modSetting.CorrespondingModKeys.FirstOrDefault();
    }
    
    // Helper method to look up image paths for any NPC
    private string? GetImagePathForNpc(VM_ModSetting modSetting, FormKey npcFormKey, Dictionary<FormKey, List<(string ModName, string ImagePath)>> mugshotData)
    {
        if (modSetting == null || !modSetting.MugShotFolderPaths.Any()) return null;

        if (mugshotData.TryGetValue(npcFormKey, out var availableMugshotsForNpc))
        {
            // Iterate through all assigned mugshot paths for the mod setting.
            foreach (var path in modSetting.MugShotFolderPaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;

                // The existing logic matches based on the directory's name.
                string mugshotDirName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var mugshotInfo = availableMugshotsForNpc.FirstOrDefault(m => m.ModName.Equals(mugshotDirName, StringComparison.OrdinalIgnoreCase));

                if (mugshotInfo != default && !string.IsNullOrWhiteSpace(mugshotInfo.ImagePath) && File.Exists(mugshotInfo.ImagePath))
                {
                    return mugshotInfo.ImagePath; // Return the first valid path found.
                }
            }
        }
        return null; // No matching mugshot found in any of the specified folders.
    }
    
    public string? GetMugshotPathForNpc(string modName, FormKey npcFormKey)
    {
        // Find the mod setting associated with the given mod name.
        var modSetting = _lazyModsVm.Value.AllModSettings.FirstOrDefault(m => m.DisplayName.Equals(modName, StringComparison.OrdinalIgnoreCase));
        if (modSetting == null)
        {
            // If no mod setting exists (e.g., a mugshot-only entry not yet linked),
            // we can still try to find a direct match in the raw mugshot data.
            if (_mugshotData.TryGetValue(npcFormKey, out var mugshots))
            {
                var match = mugshots.FirstOrDefault(m => m.ModName.Equals(modName, StringComparison.OrdinalIgnoreCase));
                if (match != default) return match.ImagePath;
            }
            return null;
        }
    
        // Use the existing private helper method to get the specific image path,
        // ensuring consistency with the rest of the application.
        return GetImagePathForNpc(modSetting, npcFormKey, _mugshotData);
    }

    private void HandleShareAppearanceRequest(VM_NpcsMenuMugshot mugshotToShare)
    {
        var selectorVm = new VM_NpcShareTargetSelector(this.AllNpcs);
        var owner = Application.Current.Windows.OfType<Window>().SingleOrDefault(x => x.IsActive);
        
        var selectorView = new NpcShareTargetSelectorView 
        { 
            DataContext = selectorVm,
            Owner = owner
        };
        
        selectorView.ShowDialog();

        var result = selectorVm.ReturnStatus;

        if ((result == ShareReturn.ShareAndSelect || result == ShareReturn.Share) && selectorVm.SelectedNpc != null)
        {
            var targetNpcKey = selectorVm.SelectedNpc.NpcFormKey;
            AddGuestAppearance(targetNpcKey, mugshotToShare.ModName, mugshotToShare.SourceNpcFormKey, mugshotToShare.TargetDisplayName);

            if (result == ShareReturn.ShareAndSelect)
            {
                _consistencyProvider.SetSelectedMod(targetNpcKey, mugshotToShare.ModName, mugshotToShare.SourceNpcFormKey);
            }
        }
    }

    public void AddGuestAppearance(FormKey targetNpcKey, string guestModName, FormKey guestNpcKey, string guestDisplayStr)
    {
        if (!_settings.GuestAppearances.TryGetValue(targetNpcKey, out var guestSet))
        {
            guestSet = new HashSet<(string, FormKey, string)>();
            _settings.GuestAppearances[targetNpcKey] = guestSet;
        }

        if (guestSet.Add((guestModName, guestNpcKey, guestDisplayStr)))
        {
            if (SelectedNpc != null && SelectedNpc.NpcFormKey.Equals(targetNpcKey))
            {
                RefreshCurrentNpcAppearanceSources();
            }
        }
    }
    
    private void HandleUnshareAppearanceRequest(VM_NpcsMenuMugshot mugshotToUnshare)
    {
        // The mugshot carries all the necessary information.
        // The target is the currently selected NPC.
        var targetNpcKey = this.SelectedNpc.NpcFormKey;
        var guestModName = mugshotToUnshare.ModName;
        var guestNpcKey = mugshotToUnshare.SourceNpcFormKey;
        var guestNpcDisplayName = mugshotToUnshare.OriginalTargetName;

        RemoveGuestAppearance(targetNpcKey, guestModName, guestNpcKey, guestNpcDisplayName);
    }

    public void RemoveGuestAppearance(FormKey targetNpcKey, string guestModName, FormKey guestNpcKey, string guestDisplayStr)
    {
        if (_settings.GuestAppearances.TryGetValue(targetNpcKey, out var guestSet))
        {
            var guestToRemove = (guestModName, guestNpcKey, guestDisplayStr);
            if (guestSet.Remove(guestToRemove))
            {
                // Check if the removed guest was the active selection for the target NPC.
                var currentSelection = _consistencyProvider.GetSelectedMod(targetNpcKey);
                if (currentSelection.ModName == guestModName && currentSelection.SourceNpcFormKey.Equals(guestNpcKey))
                {
                    // If it was, clear the selection to prevent a dangling reference.
                    _consistencyProvider.ClearSelectedMod(targetNpcKey);
                    Debug.WriteLine($"Cleared active selection for NPC {targetNpcKey} because its guest appearance was removed.");
                }
                Debug.WriteLine($"Removed guest appearance {guestToRemove} from NPC {targetNpcKey}");
                
                // If this was the last guest for this NPC, remove the entry entirely.
                if (!guestSet.Any())
                {
                    _settings.GuestAppearances.Remove(targetNpcKey);
                }

                // If the NPC whose appearances were just modified is currently selected, refresh the view.
                // This will cause the unshared mugshot to disappear.
                if (SelectedNpc != null && SelectedNpc.NpcFormKey.Equals(targetNpcKey))
                {
                    RefreshCurrentNpcAppearanceSources();
                }
            }
        }
    }

    public void RefreshCurrentNpcAppearanceSources()
    {
        Debug.WriteLine("VM_NpcSelectionBar: Refreshing appearance sources after drop...");
        var currentNpc = this.SelectedNpc;
        if (currentNpc != null)
        {
            this.SelectedNpc = null;
            this.SelectedNpc = currentNpc;
        }
    }

    public void HideSelectedMod(VM_NpcsMenuMugshot referenceMod)
    {
        if (referenceMod == null) return;
        referenceMod.IsSetHidden = true;

        if (SelectedNpc != null)
        {
            if (!_hiddenModsPerNpc.ContainsKey(SelectedNpc.NpcFormKey))
            {
                _hiddenModsPerNpc[SelectedNpc.NpcFormKey] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            _hiddenModsPerNpc[SelectedNpc.NpcFormKey].Add(referenceMod.ModName);
        }

        ToggleModVisibility();
    }

    public void UnhideSelectedMod(VM_NpcsMenuMugshot referenceMod)
    {
        if (referenceMod == null) return;
        referenceMod.IsSetHidden = false;
        if (SelectedNpc != null && _hiddenModsPerNpc.TryGetValue(SelectedNpc.NpcFormKey, out var hiddenSet))
        {
            if (hiddenSet.Remove(referenceMod.ModName))
            {
                if (!hiddenSet.Any())
                {
                    _hiddenModsPerNpc.Remove(SelectedNpc.NpcFormKey);
                }
            }
        }

        ToggleModVisibility();
    }

    public void SelectAllFromMod(VM_NpcsMenuMugshot referenceMod, bool onlyAvailable)
    {
        if (referenceMod == null || string.IsNullOrWhiteSpace(referenceMod.ModName))
        {
            Debug.WriteLine("SelectAllFromMod: referenceMod or its ModName is null/empty.");
            return;
        }

        string targetModName = referenceMod.ModName;

        // First, find all NPCs for whom this mod is a valid "native" appearance source.
        var applicableNpcs = AllNpcs
            .Where(npc => npc != null && IsModAnAppearanceSourceForNpc(npc, referenceMod) &&
                          (!onlyAvailable || !_consistencyProvider.DoesNpcHaveSelection(npc.NpcFormKey)))
            .ToList();

        if (!applicableNpcs.Any())
        {
            ScrollableMessageBox.Show($"The mod '{targetModName}' is not a direct appearance source for any known NPCs.", "No Applicable NPCs");
            return;
        }

        // Add a confirmation dialog for this potentially large-scale change.
        var confirmationMessage = $"This will set the appearance for {applicableNpcs.Count} NPC(s) to '{targetModName}'.\n\nAre you sure you want to proceed?";
        if (!ScrollableMessageBox.Confirm(confirmationMessage, "Confirm Bulk Selection"))
        {
            return;
        }

        // If confirmed, perform the update for each applicable NPC.
        foreach (var npcVM in applicableNpcs)
        {
            // Correctly call SetSelectedMod with the NPC's own FormKey as the source of the appearance.
            _consistencyProvider.SetSelectedMod(npcVM.NpcFormKey, targetModName, npcVM.NpcFormKey);
        }

        // Provide clear feedback to the user upon completion.
        Debug.WriteLine(
            $"Finished processing. Set '{targetModName}' as the selected appearance for {applicableNpcs.Count} NPC(s).");
    }

    private bool IsModAnAppearanceSourceForNpc(VM_NpcsMenuSelection npcSelectionVm, VM_NpcsMenuMugshot referenceMod)
    {
        if (npcSelectionVm == null || referenceMod == null || string.IsNullOrEmpty(referenceMod.ModName))
            return false;

        if (referenceMod.AssociatedModSetting != null &&
            npcSelectionVm.AppearanceMods.Contains(referenceMod.AssociatedModSetting))
        {
            return true;
        }

        if (_mugshotData.TryGetValue(npcSelectionVm.NpcFormKey, out var mugshots))
        {
            if (mugshots.Any(m => m.ModName.Equals(referenceMod.ModName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    public void HideAllFromMod(VM_NpcsMenuMugshot referenceMod)
    {
        if (referenceMod == null || string.IsNullOrWhiteSpace(referenceMod.ModName)) return;
        if (_hiddenModNames.Add(referenceMod.ModName))
        {
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

        ToggleModVisibility();
    }

    public void UnhideAllFromMod(VM_NpcsMenuMugshot referenceMod)
    {
        if (referenceMod == null || string.IsNullOrWhiteSpace(referenceMod.ModName)) return;
        if (_hiddenModNames.Remove(referenceMod.ModName))
        {
            if (CurrentNpcAppearanceMods != null)
            {
                foreach (var modVM in CurrentNpcAppearanceMods)
                {
                    if (modVM.ModName.Equals(referenceMod.ModName, StringComparison.OrdinalIgnoreCase))
                    {
                        bool isHiddenPerNpc = SelectedNpc != null &&
                                              _hiddenModsPerNpc.TryGetValue(SelectedNpc.NpcFormKey,
                                                  out var hiddenSet) &&
                                              hiddenSet.Contains(modVM.ModName);
                        modVM.IsSetHidden = isHiddenPerNpc;
                    }
                }
            }
        }

        ToggleModVisibility();
    }

    public void ToggleModVisibility()
    {
        if (CurrentNpcAppearanceMods == null || !CurrentNpcAppearanceMods.Any()) return;

        bool needsRefresh = false;
        var npcSpecificHidden =
            SelectedNpc != null ? _hiddenModsPerNpc.GetValueOrDefault(SelectedNpc.NpcFormKey) : null;

        foreach (var mod in CurrentNpcAppearanceMods)
        {
            bool isGloballyHidden = _hiddenModNames.Contains(mod.ModName);
            bool isSpecificallyHidden = npcSpecificHidden?.Contains(mod.ModName) ?? false;
            bool shouldBeHidden = isGloballyHidden || isSpecificallyHidden;
            mod.IsSetHidden = shouldBeHidden;
            bool shouldBeVisible = ShowHiddenMods || !mod.IsSetHidden;
            if (mod.IsVisible != shouldBeVisible)
            {
                mod.IsVisible = shouldBeVisible;
                needsRefresh = true;
            }
        }

        if (needsRefresh)
        {
            _refreshImageSizesSubject.OnNext(Unit.Default);
        }
    }

    // --- NPC Group Methods ---
    private bool AddCurrentNpcToGroup()
    {
        if (SelectedNpc == null || string.IsNullOrWhiteSpace(SelectedGroupName)) return false;
        
        var npcKey = SelectedNpc.NpcFormKey;
        var groupName = SelectedGroupName.Trim();
        if (!_settings.NpcGroupAssignments.TryGetValue(npcKey, out var groups))
        {
            groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _settings.NpcGroupAssignments[npcKey] = groups;
        }

        if (groups.Add(groupName))
        {
            Debug.WriteLine($"Added NPC {npcKey} to group '{groupName}'");
            UpdateAvailableNpcGroups();
            SelectedNpc.UpdateGroupDisplay(groups);
            ApplyFilter(false);
        }
        else
        {
            Debug.WriteLine($"NPC {npcKey} already in group '{groupName}'");
            return false;
        }

        return true;
    }

    private bool RemoveCurrentNpcFromGroup()
    {
        if (SelectedNpc == null || string.IsNullOrWhiteSpace(SelectedGroupName)) return false;
        var npcKey = SelectedNpc.NpcFormKey;
        var groupName = SelectedGroupName.Trim();
        if (_settings.NpcGroupAssignments.TryGetValue(npcKey, out var groups))
        {
            if (groups.Remove(groupName))
            {
                Debug.WriteLine($"Removed NPC {npcKey} from group '{groupName}'");
                SelectedNpc.UpdateGroupDisplay(groups);
                if (!groups.Any())
                {
                    _settings.NpcGroupAssignments.Remove(npcKey);
                    Debug.WriteLine($"Removed group entry for NPC {npcKey} as it's now empty.");
                }

                UpdateAvailableNpcGroups();
                ApplyFilter(false);
            }
            else
            {
                Debug.WriteLine($"NPC {npcKey} was not in group '{groupName}'");
                return false;
            }
        }
        else
        {
            Debug.WriteLine($"NPC {npcKey} has no group assignments.");
            return false;
        }

        return true;
    }

    private bool AreAnyFiltersActive()
    {
        if (SearchType1 != NpcSearchType.SelectionState && SearchType1 != NpcSearchType.Group &&
            !string.IsNullOrWhiteSpace(SearchText1)) return true;
        if (SearchType2 != NpcSearchType.SelectionState && SearchType2 != NpcSearchType.Group &&
            !string.IsNullOrWhiteSpace(SearchText2)) return true;
        if (SearchType3 != NpcSearchType.SelectionState && SearchType3 != NpcSearchType.Group &&
            !string.IsNullOrWhiteSpace(SearchText3)) return true;
        if (SearchType1 == NpcSearchType.SelectionState) return true;
        if (SearchType2 == NpcSearchType.SelectionState) return true;
        if (SearchType3 == NpcSearchType.SelectionState) return true;
        if (SearchType1 == NpcSearchType.Group && !string.IsNullOrWhiteSpace(SelectedGroupFilter1)) return true;
        if (SearchType2 == NpcSearchType.Group && !string.IsNullOrWhiteSpace(SelectedGroupFilter2)) return true;
        if (SearchType3 == NpcSearchType.Group && !string.IsNullOrWhiteSpace(SelectedGroupFilter3)) return true;
        return false;
    }

    private bool AddAllVisibleNpcsToGroup()
    {
        if (FilteredNpcs.Count == 0 || string.IsNullOrWhiteSpace(SelectedGroupName)) return false;
        var groupName = SelectedGroupName.Trim();
        int count = FilteredNpcs.Count;
        int totalNpcCount = AllNpcs.Count;
        if (!AreAnyFiltersActive())
        {
            if (!ScrollableMessageBox.Confirm(
                    $"No filters are currently applied. Are you sure you want to add ALL {totalNpcCount} NPCs in your game to the group '{groupName}'?",
                    "Confirm Add All NPCs"))
            {
                Debug.WriteLine("Add All Visible NPCs to Group cancelled by user (no filters active).");
                return false;
            }
        }
        else
        {
            if (!ScrollableMessageBox.Confirm($"Add all {count} currently visible NPCs to the group '{groupName}'?",
                    "Confirm Add Visible NPCs"))
            {
                Debug.WriteLine("Add All Visible NPCs to Group cancelled by user.");
                return false;
            }
        }

        int addedCount = 0;
        bool groupListChanged = false;
        foreach (var npc in FilteredNpcs)
        {
            if (!_settings.NpcGroupAssignments.TryGetValue(npc.NpcFormKey, out var groups))
            {
                groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _settings.NpcGroupAssignments[npc.NpcFormKey] = groups;
            }

            if (groups.Add(groupName))
            {
                addedCount++;
                groupListChanged = true;
                npc.UpdateGroupDisplay(groups);
            }
        }

        if (groupListChanged)
        {
            UpdateAvailableNpcGroups();
        }

        ApplyFilter(false);
        Debug.WriteLine($"Added {addedCount} visible NPCs to group '{groupName}'.");
        return true;
    }

    private bool RemoveAllVisibleNpcsFromGroup()
    {
        if (FilteredNpcs.Count == 0 || string.IsNullOrWhiteSpace(SelectedGroupName)) return false;
        var groupName = SelectedGroupName.Trim();
        int count = FilteredNpcs.Count;
        int totalNpcCount = AllNpcs.Count;
        if (!AreAnyFiltersActive())
        {
            if (!ScrollableMessageBox.Confirm(
                    $"No filters are currently applied. Are you sure you want to attempt removing ALL {totalNpcCount} NPCs in your game from the group '{groupName}'?",
                    "Confirm Remove All NPCs", MessageBoxImage.Warning))
            {
                Debug.WriteLine("Remove All Visible NPCs from Group cancelled by user (no filters active).");
                return false;
            }
        }
        else
        {
            if (!ScrollableMessageBox.Confirm(
                    $"Remove all {count} currently visible NPCs from the group '{groupName}'?",
                    "Confirm Remove Visible NPCs"))
            {
                Debug.WriteLine("Remove All Visible NPCs from Group cancelled by user.");
                return false;
            }
        }

        int removedCount = 0;
        bool groupListMayNeedUpdate = false;
        foreach (var npc in FilteredNpcs)
        {
            if (_settings.NpcGroupAssignments.TryGetValue(npc.NpcFormKey, out var groups))
            {
                if (groups.Remove(groupName))
                {
                    removedCount++;
                    npc.UpdateGroupDisplay(groups);
                    groupListMayNeedUpdate = true;
                    if (!groups.Any())
                    {
                        _settings.NpcGroupAssignments.Remove(npc.NpcFormKey);
                    }
                }
            }
        }

        if (groupListMayNeedUpdate)
        {
            UpdateAvailableNpcGroups();
        }

        ApplyFilter(false);
        Debug.WriteLine($"Removed {removedCount} visible NPCs from group '{groupName}'.");
        return true;
    }

    private void UpdateAvailableNpcGroups()
    {
        var distinctGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_settings.NpcGroupAssignments != null)
        {
            foreach (var groupSet in _settings.NpcGroupAssignments.Values)
            {
                if (groupSet != null)
                {
                    foreach (var groupName in groupSet)
                    {
                        if (!string.IsNullOrWhiteSpace(groupName))
                        {
                            distinctGroups.Add(groupName.Trim());
                        }
                    }
                }
            }
        }

        var sortedGroups = distinctGroups.OrderBy(g => g).ToList();
        string? currentSelection = SelectedGroupName;
        bool selectionStillExists = false;
        AvailableNpcGroups.Clear();
        AvailableNpcGroups.Add(AllNpcsGroup);
        foreach (var group in sortedGroups)
        {
            AvailableNpcGroups.Add(group);
            if (group.Equals(currentSelection, StringComparison.OrdinalIgnoreCase))
            {
                selectionStillExists = true;
            }
        }

        if (!selectionStillExists)
        {
            SelectedGroupName = string.Empty;
        }
        else
        {
            SelectedGroupName = currentSelection;
        }

        MessageBus.Current.SendMessage(new NpcGroupsChangedMessage());
        Debug.WriteLine($"Updated AvailableNpcGroups. Count: {AvailableNpcGroups.Count}");
    }
    // --- End NPC Group Methods ---

    public void MassUpdateNpcSelections(string fromModName, FormKey fromNpcKey, string toModName, FormKey toNpcKey)
    {
        if (string.Equals(fromModName, toModName, StringComparison.OrdinalIgnoreCase) && fromNpcKey.Equals(toNpcKey))
        {
            return;
        }

        var targetMod = _lazyModsVm.Value.AllModSettings.FirstOrDefault(x => x.DisplayName == toModName);
        if (targetMod == null)
        {
            return;
        }

        var npcsToUpdate = AllNpcs
            .Where(npc => {
                var selection = _consistencyProvider.GetSelectedMod(npc.NpcFormKey);
                return string.Equals(selection.ModName, fromModName, StringComparison.OrdinalIgnoreCase) && targetMod.AvailablePluginsForNpcs.ContainsKey(npc.NpcFormKey);
            })
            .ToList();

        if (!npcsToUpdate.Any()) return;

        var confirmationMessage = $"This will change the selected appearance for {npcsToUpdate.Count} NPC(s) from '{fromModName} ({fromNpcKey})' to '{toModName} ({toNpcKey})'. Proceed?";
        string imagePath = @"Resources\Replace Selected Mod.png";
        if (ScrollableMessageBox.Confirm(confirmationMessage, "Confirm Mass Update", displayImagePath: imagePath))
        {
            foreach (var npc in npcsToUpdate)
            {
                _consistencyProvider.SetSelectedMod(npc.NpcFormKey, toModName, toNpcKey);
            }
        }
    }
    
    public OutfitOverride GetNpcOutfitOverride(FormKey npcFormKey)
    {
        if (_settings.NpcOutfitOverrides.TryGetValue(npcFormKey, out var storedOverride))
        {
            return storedOverride;
        }
        return OutfitOverride.UseModSetting;
    }

    private void SetNpcOutfitOverride(FormKey npcFormKey, OutfitOverride newOverride)
    {
        if (newOverride == OutfitOverride.UseModSetting)
        {
            // If setting back to default, remove the key to keep the dictionary clean.
            if (_settings.NpcOutfitOverrides.Remove(npcFormKey))
            {
                Debug.WriteLine($"Removed outfit override for NPC {npcFormKey}.");
            }
        }
        else
        {
            _settings.NpcOutfitOverrides[npcFormKey] = newOverride;
            Debug.WriteLine($"Set outfit override for NPC {npcFormKey} to {newOverride}.");
        }
    }

    // --- Disposal ---
    public void Dispose()
    {
        _disposables.Dispose();
        ClearAppearanceModViewModels();
    }

    private void ClearAppearanceModViewModels()
    {
        if (CurrentNpcAppearanceMods != null)
        {
            var vmsToDispose = CurrentNpcAppearanceMods.ToList();
            CurrentNpcAppearanceMods.Clear();
            foreach (var vm in vmsToDispose)
            {
                vm.Dispose();
            }
        }
    }
}

public enum SelectionStateFilterType
{
    [Description("Selection Not Made")]
    NotMade,
    [Description("Selection Made")]
    Made
}

public enum ShareStatusFilterType
{
    [Description("Any")]
    Any,
    [Description("Guest Available")]
    GuestAvailable,
    [Description("Guest Selected")]
    GuestSelected,
    [Description("Shared")]
    Shared,
    [Description("Shared & Selected")]
    SharedAndSelected
}

public class ShareAppearanceRequest
{
    public VM_NpcsMenuMugshot MugshotToShare { get; }
    public ShareAppearanceRequest(VM_NpcsMenuMugshot mugshotToShare)
    {
        MugshotToShare = mugshotToShare;
    }
}

public class UnshareAppearanceRequest
{
    public VM_NpcsMenuMugshot MugshotToUnshare { get; }
    public UnshareAppearanceRequest(VM_NpcsMenuMugshot mugshotToUnshare)
    {
        MugshotToUnshare = mugshotToUnshare;
    }
}