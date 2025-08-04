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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks; // Added for Task
using System.Windows;
using System.Windows.Forms; // Added for MessageBox
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
    private readonly VM_ModSetting.FromModelFactory _modSettingFromModelFactory;

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
    [Reactive] public bool IsProgrammaticNavigationInProgress { get; set; } = false;

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

    // --- NPC Group Properties ---
    [Reactive] public string SelectedGroupName { get; set; } = string.Empty;
    public ObservableCollection<string> AvailableNpcGroups { get; } = new();
    public ReactiveCommand<Unit, Unit> AddCurrentNpcToGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCurrentNpcFromGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> AddAllVisibleNpcsToGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveAllVisibleNpcsFromGroupCommand { get; }
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
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                ToggleModVisibility();
                _refreshImageSizesSubject.OnNext(Unit.Default);
            })
            .DisposeWith(_disposables);

        _consistencyProvider.NpcSelectionChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(args => UpdateSelectionState(args.NpcFormKey, args.SelectedMod))
            .DisposeWith(_disposables);

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

        this.WhenAnyValue(x => x.SearchType1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(type =>
            {
                if (type == NpcSearchType.Group || type == NpcSearchType.SelectionState) SearchText1 = string.Empty;
                if (type != NpcSearchType.Group) SelectedGroupFilter1 = null;
            })
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType2)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(type =>
            {
                if (type == NpcSearchType.Group || type == NpcSearchType.SelectionState) SearchText2 = string.Empty;
                if (type != NpcSearchType.Group) SelectedGroupFilter2 = null;
            })
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.SearchType3)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(type =>
            {
                if (type == NpcSearchType.Group || type == NpcSearchType.SelectionState) SearchText3 = string.Empty;
                if (type != NpcSearchType.Group) SelectedGroupFilter3 = null;
            })
            .DisposeWith(_disposables);

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


        if (CurrentNpcAppearanceMods != null && CurrentNpcAppearanceMods.Any())
        {
            _refreshImageSizesSubject.OnNext(Unit.Default);
        }
    }

    // --- Methods ---

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

    // --- End NEW Command Execution Methods ---
// --- NEW: Import/Export Methods ---
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

        try
        {
            var selectionsToExport = _settings.SelectedAppearanceMods
                .ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);

            JSONhandler<Dictionary<string, string>>.SaveJSONFile(selectionsToExport, dialog.FileName,
                out bool success, out var exceptionString);
            if (!success)
            {
                ScrollableMessageBox.ShowError(exceptionString, "Error while exporting NPC Choices");
            }
            else
            {
                ScrollableMessageBox.Show(
                    $"Successfully exported {selectionsToExport.Count} choices to {Path.GetFileName(dialog.FileName)}.",
                    "Export Complete");
            }
        }
        catch (Exception ex)
        {
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
                $"Are you sure you want to overwrite your current NPC choices? This action cannot be undone.",
                "Confirm Import Choices", MessageBoxImage.Warning))
        {
            return; // User cancelled
        }

        List<string> missingNpcs = new();
        List<string> unMatchedNpcs = new();

        foreach (var npc in AllNpcs)
        {
            if (!_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npc.NpcFormKey, out var npcGetter) ||
                npcGetter == null)
            {
                var logStr = npc.DisplayName;
                if (npc.NpcEditorId.Any())
                {
                    logStr += " (" + npc.NpcEditorId + ")";
                }

                logStr += " (" + npc.NpcFormKeyString + ")";
                missingNpcs.Add(logStr);
                continue;
            }

            var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npc.NpcFormKey);

            bool foundWinningMod = false;
            foreach (var context in contexts)
            {
                if (_settings.ImportFromLoadOrderExclusions.Contains(context.ModKey))
                {
                    continue;
                }

                // get all appearance mods with the current modkey
                var correspondingMods = _lazyModsVm.Value.AllModSettings
                    .Where(x => x.CorrespondingModKeys.Contains(context.ModKey)).ToList();
                if (correspondingMods.Count() == 1)
                {
                    var winningMod = correspondingMods.First();
                    _consistencyProvider.SetSelectedMod(npc.NpcFormKey, winningMod.DisplayName);
                    foundWinningMod = true;
                    break;
                }

                if (correspondingMods.Count() > 1)
                {
                    var (meshSubPath, texSubPath) = Auxilliary.GetFaceGenSubPathStrings(npc.NpcFormKey);

                    var meshToMatchPath = Path.Combine(_environmentStateProvider.DataFolderPath, "meshes",
                        meshSubPath);
                    var texToMatchPath = Path.Combine(_environmentStateProvider.DataFolderPath, "textures",
                        texSubPath);

                    bool mustMatchMesh = false;
                    int meshRefSize = 0;
                    string meshRefHash = String.Empty;

                    bool mustMatchTex = false;
                    int texRefSize = 0;
                    string texRefHash = String.Empty;

                    if (File.Exists(meshToMatchPath))
                    {
                        mustMatchMesh = true;
                        (meshRefSize, meshRefHash) = Auxilliary.GetCheapFileEqualityIdentifiers(meshToMatchPath);
                    }

                    if (File.Exists(texToMatchPath))
                    {
                        mustMatchMesh = true;
                        (texRefSize, texRefHash) = Auxilliary.GetCheapFileEqualityIdentifiers(texToMatchPath);
                    }

                    foreach (var candidateMod in correspondingMods)
                    {
                        foreach (var modFolder in candidateMod.CorrespondingFolderPaths)
                        {
                            bool matchedMesh = !mustMatchMesh;
                            bool matchedTex = !mustMatchMesh;

                            // Will need to fix this section to account for BSAs
                            if (mustMatchMesh)
                            {
                                var candidateMeshPath = Path.Combine(modFolder, "meshes",
                                    meshSubPath);
                                if (File.Exists(candidateMeshPath) &&
                                    Auxilliary.FastFilesAreIdentical(candidateMeshPath, meshRefSize, meshRefHash))
                                {
                                    matchedMesh = true;
                                }
                            }

                            if (mustMatchTex)
                            {
                                var candidateTexPath = Path.Combine(modFolder, "textures",
                                    texSubPath);
                                if (File.Exists(candidateTexPath) &&
                                    Auxilliary.FastFilesAreIdentical(candidateTexPath, texRefSize, texRefHash))
                                {
                                    matchedTex = true;
                                }
                            }

                            if (matchedMesh && matchedTex)
                            {
                                _consistencyProvider.SetSelectedMod(npc.NpcFormKey, candidateMod.DisplayName);
                                foundWinningMod = true;
                                break;
                            }
                        } // end mod folder loop

                        if (foundWinningMod)
                        {
                            break;
                        }
                    } // end mod loop

                    if (foundWinningMod)
                    {
                        break;
                    }
                }

                if (foundWinningMod)
                {
                    break;
                }
            } // end context loop

            if (!foundWinningMod)
            {
                unMatchedNpcs.Add(Auxilliary.GetNpcLogString(npcGetter, true));
            }
        } // end NPC loop

        if (missingNpcs.Any())
        {
            string missingMessage =
                "The following NPCs could not be found in your load order. Their appearance selection was not modified:" +
                Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, missingNpcs);
            ScrollableMessageBox.ShowWarning(missingMessage, "Missing NPCs");
        }

        if (unMatchedNpcs.Any())
        {
            string missingMessage =
                "A winning mod could not be identified for the following NPCs:" + Environment.NewLine +
                Environment.NewLine + string.Join(Environment.NewLine, unMatchedNpcs);
            ScrollableMessageBox.ShowWarning(missingMessage, "Unassigned NPCs");
        }
    }

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
            var importedSelectionsStr =
                JSONhandler<Dictionary<string, string>>.LoadJSONFile(dialog.FileName, out bool readSuccess,
                    out var exceptionStr);
            if (!readSuccess)
            {
                ScrollableMessageBox.ShowError(exceptionStr, "Failed to import choices");
                return;
            }

            if (importedSelectionsStr == null || !importedSelectionsStr.Any())
            {
                ScrollableMessageBox.ShowWarning("The selected file is empty or contains no valid data.",
                    "Import Warning");
                return;
            }

            // Convert string keys back to FormKey, skipping malformed ones
            var importedSelections = new Dictionary<FormKey, string>();
            var malformedKeys = new List<string>();
            foreach (var kvp in importedSelectionsStr)
            {
                try
                {
                    importedSelections.Add(FormKey.Factory(kvp.Key), kvp.Value);
                }
                catch
                {
                    malformedKeys.Add(kvp.Key);
                }
            }

            // --- Validation ---
            var report = new StringBuilder();
            var validSelections = new Dictionary<FormKey, string>();
            var availableModNames = new HashSet<string>(_lazyModsVm.Value.AllModSettings.Select(m => m.DisplayName),
                StringComparer.OrdinalIgnoreCase);

            var missingNpcs = new List<string>();
            var missingMods = new List<string>();

            foreach (var kvp in importedSelections)
            {
                var formKey = kvp.Key;
                var modName = kvp.Value;
                bool npcExists =
                    _environmentStateProvider.LinkCache.TryResolve<INpcGetter>(formKey, out var npcGetter);
                bool modExists = availableModNames.Contains(modName);

                if (npcExists && modExists)
                {
                    validSelections.Add(formKey, modName);
                }
                else
                {
                    if (!npcExists)
                    {
                        missingNpcs.Add(
                            $"- NPC with FormKey {formKey} (assigned to '{modName}') was not found in the current load order.");
                    }

                    if (!modExists)
                    {
                        // Try to find the NPC's name for a better message
                        string npcIdentifier = npcGetter?.Name?.String ??
                                               npcGetter?.EditorID ?? $"NPC with FormKey {formKey}";
                        missingMods.Add(
                            $"- {npcIdentifier} was assigned to mod '{modName}', which is not installed or recognized.");
                    }
                }
            }

            // --- Reporting and Confirmation ---
            if (missingNpcs.Any() || missingMods.Any() || malformedKeys.Any())
            {
                if (missingNpcs.Any())
                {
                    report.AppendLine("NPCs Not Found in Load Order:");
                    missingNpcs.ForEach(line => report.AppendLine(line));
                    report.AppendLine();
                }

                if (missingMods.Any())
                {
                    report.AppendLine("Assigned Mods Not Found:");
                    missingMods.ForEach(line => report.AppendLine(line));
                    report.AppendLine();
                }

                if (malformedKeys.Any())
                {
                    report.AppendLine("Malformed FormKeys Skipped:");
                    malformedKeys.ForEach(key => report.AppendLine($"- {key}"));
                    report.AppendLine();
                }

                var preamble = $"The import file contains entries that will be skipped.\n\n" +
                               $"Do you want to proceed with importing the {validSelections.Count} valid choices? This will overwrite all your current selections.";

                report.Insert(0, preamble + "\n\n--- Details ---\n");

                if (!ScrollableMessageBox.Confirm(report.ToString(), "Import Confirmation",
                        MessageBoxImage.Warning))
                {
                    ScrollableMessageBox.Show("Import cancelled by user.", "Import Cancelled");
                    return;
                }
            }
            else
            {
                if (!ScrollableMessageBox.Confirm(
                        $"This will overwrite your current {_settings.SelectedAppearanceMods.Count} selection(s) with {validSelections.Count} choice(s) from the file. Proceed?",
                        "Confirm Import"))
                {
                    ScrollableMessageBox.Show("Import cancelled by user.", "Import Cancelled");
                    return;
                }
            }

            // --- Perform Import ---
            _consistencyProvider.ClearAllSelections();

            foreach (var kvp in validSelections)
            {
                _consistencyProvider.SetSelectedMod(kvp.Key, kvp.Value);
            }

            ScrollableMessageBox.Show($"Import complete. {validSelections.Count} choices have been applied.",
                "Import Successful");
        }
        catch (JsonException jsonEx)
        {
            ScrollableMessageBox.ShowError($"The file is not a valid JSON file. Error: {jsonEx.Message}",
                "Import Error");
        }
        catch (Exception ex)
        {
            ScrollableMessageBox.ShowError($"An unexpected error occurred during import: {ex.Message}",
                "Import Error");
        }
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

    private void UpdateSelectionState(FormKey npcFormKey, string selectedMod)
    {
        var npcVM = FilteredNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(npcFormKey))
                    ?? AllNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(npcFormKey));

        if (npcVM != null)
        {
            npcVM.SelectedAppearanceModName = selectedMod;
            if (SelectedNpc == npcVM && CurrentNpcAppearanceMods != null)
            {
                foreach (var modVM in CurrentNpcAppearanceMods)
                {
                    modVM.IsSelected = modVM.ModName.Equals(selectedMod, StringComparison.OrdinalIgnoreCase);
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
        // 1. Clear all UI-bound collections on the UI thread first.
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SelectedNpc = null;
            AllNpcs.Clear();
            FilteredNpcs.Clear();
            CurrentNpcDescription = null;
        });

        if (!_environmentStateProvider.EnvironmentIsValid)
        {
            splashReporter?.UpdateStep("Environment not valid for NPC list.");
            ScrollableMessageBox.ShowWarning(
                $"Environment is not valid. Check settings.\nError: {_environmentStateProvider.EnvironmentBuilderError}",
                "Environment Error");
            _mugshotData.Clear();
            return;
        }

        splashReporter?.UpdateStep("Scanning mugshot directory...");
        using (ContextualPerformanceTracer.Trace("InitializeNpcs.ScanMugshots"))
        {
            _mugshotData = await Task.Run(() => ScanMugshotDirectory(splashReporter));
        }

        await Application.Current.Dispatcher.InvokeAsync(UpdateAvailableNpcGroups);
        splashReporter?.UpdateStep("Updating NPC groups...");

        splashReporter?.UpdateStep("Querying NPC records from mods...");

        // 2. Perform ALL heavy data processing in the background.
        var processedNpcData = new List<NpcInitializationData>();
        using (ContextualPerformanceTracer.Trace("InitializeNpcs.ProcessNpcData"))
        {
            processedNpcData = await Task.Run(() =>
            {
                var npcDataMap = new Dictionary<FormKey, (NpcDisplayData? NpcData, List<VM_ModSetting> Mods)>();

                if (_lazyModsVm.Value?.AllModSettings == null)
                {
                    Debug.WriteLine(
                        "Warning: InitializeAsync called before AllModSettings was populated. Returning empty NPC list.");
                    return new List<NpcInitializationData>();
                }

                // Aggregate all NPCs from all mod settings first
                foreach (var modSetting in _lazyModsVm.Value.AllModSettings)
                {
                    foreach (var formKeyToName in modSetting.NpcFormKeysToDisplayName)
                    {
                        var npcFormKey = formKeyToName.Key;
                        if (!npcDataMap.TryGetValue(npcFormKey, out var data))
                        {
                            data = (null, new List<VM_ModSetting>());
                            npcDataMap[npcFormKey] = data;
                        }

                        data.Mods.Add(modSetting);
                    }
                }

                // Now, resolve the NpcDisplayData for each unique NPC
                var finalDataList = new List<NpcInitializationData>();
                foreach (var kvp in npcDataMap)
                {
                    var npcFormKey = kvp.Key;
                    var appearanceMods = kvp.Value.Mods;

                    if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey, out var npcGetter))
                    {
                        // Create lightweight object and discard the getter
                        var displayData = NpcDisplayData.FromGetter(npcGetter);
                        finalDataList.Add(new NpcInitializationData
                            { NpcData = displayData, AppearanceMods = appearanceMods });
                    }
                }

                return finalDataList;
            });
        }

        // 3. Now back on the UI thread, create the ViewModel objects.
        splashReporter?.UpdateStep("Creating NPC view models...");
        using (ContextualPerformanceTracer.Trace("InitializeNpcs.CreateViewModels"))
        {
            int totalNpcsToProcess = processedNpcData.Count;
            int processedNpcCount = 0;
            foreach (var initData in processedNpcData)
            {
                processedNpcCount++;
                if (totalNpcsToProcess > 0)
                {
                    var progress = (double)processedNpcCount / totalNpcsToProcess * 100.0;
                    // Update progress with the name of the NPC being processed
                    var npcName = initData.NpcData?.DisplayName ?? "Unknown";
                    splashReporter?.UpdateProgress(progress, $"Processing: {npcName}");
                }
                
                var selector = new VM_NpcsMenuSelection(initData.NpcData.FormKey, _environmentStateProvider, this);
                selector.UpdateWithData(initData.NpcData);

                foreach (var mod in initData.AppearanceMods)
                {
                    selector.AppearanceMods.Add(mod);
                }

                AllNpcs.Add(selector);
            }
        }

        // 4. Handle NPCs that only exist in mugshot data.
        var allNpcKeys = new HashSet<FormKey>(AllNpcs.Select(n => n.NpcFormKey));
        var mugshotOnlyKeys = _mugshotData.Keys.Where(key => !allNpcKeys.Contains(key)).ToList();
        using (ContextualPerformanceTracer.Trace("InitializeNpcs.CreateMugshotOnlyViewModels"))
        {
            foreach (var mugshotFormKey in mugshotOnlyKeys)
            {
                try
                {
                    var npcSelector = new VM_NpcsMenuSelection(mugshotFormKey, _environmentStateProvider, this);
                    if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(mugshotFormKey, out var getter))
                    {
                        npcSelector.UpdateWithData(NpcDisplayData.FromGetter(getter));
                    }

                    AllNpcs.Add(npcSelector);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error creating VM for mugshot-only NPC {mugshotFormKey}: {ex.Message}");
                }
            }
        }

        // 5. Final cleanup and filtering on the UI thread.
        splashReporter?.UpdateStep("Finalizing NPC List...");
        using (ContextualPerformanceTracer.Trace("InitializeNpcs.FinalCleanup"))
        {
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

        // 6. Restore previous selection or select the first item.
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
    public void ApplyFilter(bool initializing)
    {
        List<VM_NpcsMenuSelection> results = AllNpcs;
        var predicates = new List<Func<VM_NpcsMenuSelection, bool>>();

        // --- Your existing predicate building logic ---
        if (SearchType1 == NpcSearchType.SelectionState)
        {
            predicates.Add(npc => CheckSelectionState(npc, SelectedStateFilter1));
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

        results.SortByFormId(_auxilliary);

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
        var previouslySelectedNpcKey = SelectedNpc?.NpcFormKey;
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

        if (SelectedNpc != newSelection) // Only update if it's actually different
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

    private bool CheckSelectionState(VM_NpcsMenuSelection npcsMenu, SelectionStateFilterType filterState)
    {
        bool isSelected = !string.IsNullOrEmpty(_consistencyProvider.GetSelectedMod(npcsMenu.NpcFormKey));
        return filterState == SelectionStateFilterType.Made ? isSelected : !isSelected;
    }

    private Func<VM_NpcsMenuSelection, bool>? BuildGroupPredicate(string? selectedGroup)
    {
        if (string.IsNullOrWhiteSpace(selectedGroup)) return null;
        return npc => _settings.NpcGroupAssignments.TryGetValue(npc.NpcFormKey, out var groups) &&
                      groups != null &&
                      groups.Contains(selectedGroup);
    }

    private Func<VM_NpcsMenuSelection, bool>? BuildTextPredicate(NpcSearchType type, string searchText)
    {
        if (type == NpcSearchType.SelectionState || type == NpcSearchType.Group ||
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
            case NpcSearchType.FromMod:
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
        var finalModVMs = new Dictionary<string, VM_NpcsMenuMugshot>(StringComparer.OrdinalIgnoreCase);
        if (selectionVm == null) return new ObservableCollection<VM_NpcsMenuMugshot>();

        var relevantModSettings = new HashSet<VM_ModSetting>();

        if (mugshotData.TryGetValue(selectionVm.NpcFormKey, out var npcMugshotListForThisNpc))
        {
            foreach (var mugshotInfo in npcMugshotListForThisNpc)
            {
                var modSettingViaMugshotName = _lazyModsVm.Value?.AllModSettings.FirstOrDefault(ms =>
                    ms.DisplayName.Equals(mugshotInfo.ModName, StringComparison.OrdinalIgnoreCase));

                if (modSettingViaMugshotName != null)
                {
                    if (!string.IsNullOrWhiteSpace(modSettingViaMugshotName.MugShotFolderPath))
                    {
                        string expectedMugshotParentDir = Path.GetFileName(
                            modSettingViaMugshotName.MugShotFolderPath.TrimEnd(Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar));
                        if (mugshotInfo.ModName.Equals(expectedMugshotParentDir,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            relevantModSettings.Add(modSettingViaMugshotName);
                        }
                    }
                    else
                    {
                        relevantModSettings.Add(modSettingViaMugshotName);
                    }
                }
            }
        }

        foreach (var modSetting in selectionVm.AppearanceMods)
        {
            if (!relevantModSettings.Contains(modSetting))
            {
                relevantModSettings.Add(modSetting);
            }
        }

        ModKey baseModKey = selectionVm.NpcFormKey.ModKey;

        if (!baseModKey.IsNull && _lazyModsVm.IsValueCreated && _lazyModsVm.Value != null)
        {
            var modSettingsForBasePlugin = _lazyModsVm.Value?.AllModSettings
                .Where(ms => ms.CorrespondingModKeys.Contains(baseModKey))
                .ToList();
            if (modSettingsForBasePlugin != null && modSettingsForBasePlugin.Any())
            {
                foreach (var ms in modSettingsForBasePlugin) relevantModSettings.Add(ms);
            }
            else
            {
                var dummyModSetting = new ModSetting()
                {
                    DisplayName = baseModKey.ToString(),
                    CorrespondingModKeys = new() { baseModKey }
                };

                var dummyVM = _modSettingFromModelFactory(dummyModSetting, _lazyModsVm.Value);
                relevantModSettings.Add(dummyVM);
            }
        }

        bool baseKeyHandledByAModSettingVM = false;
        foreach (var modSettingVM in relevantModSettings)
        {
            string displayName = modSettingVM.DisplayName;
            ModKey? specificPluginKey = null;
            if (modSettingVM.NpcPluginDisambiguation.TryGetValue(selectionVm.NpcFormKey, out var mappedSourceKey))
            {
                specificPluginKey = mappedSourceKey;
            }

            if ((specificPluginKey == null || specificPluginKey.Value.IsNull) && 
                modSettingVM.AvailablePluginsForNpcs.TryGetValue(selectionVm.NpcFormKey, out var candiatePlugins) && 
                candiatePlugins.Any())
            {
                specificPluginKey = modSettingVM.AvailablePluginsForNpcs[selectionVm.NpcFormKey].First();
            }

            if ((specificPluginKey == null || specificPluginKey.Value.IsNull) && !baseModKey.IsNull &&
                modSettingVM.CorrespondingModKeys.Contains(baseModKey))
            {
                specificPluginKey = baseModKey;
            }

            if (specificPluginKey == null || specificPluginKey.Value.IsNull)
            {
                specificPluginKey = modSettingVM.CorrespondingModKeys.FirstOrDefault();
            }

            string? imagePath = null;
            if (!string.IsNullOrWhiteSpace(modSettingVM.MugShotFolderPath) &&
                Directory.Exists(modSettingVM.MugShotFolderPath) &&
                mugshotData.TryGetValue(selectionVm.NpcFormKey, out var availableMugshotsForNpcViaCache))
            {
                string mugshotDirNameForThisSetting = Path.GetFileName(
                    modSettingVM.MugShotFolderPath.TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar));
                var specificMugshotInfo = availableMugshotsForNpcViaCache.FirstOrDefault(m =>
                    m.ModName.Equals(mugshotDirNameForThisSetting, StringComparison.OrdinalIgnoreCase));
                if (specificMugshotInfo != default && !string.IsNullOrWhiteSpace(specificMugshotInfo.ImagePath) &&
                    File.Exists(specificMugshotInfo.ImagePath))
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
                                var inferredKey = modSettingVM.CorrespondingModKeys.FirstOrDefault(mk =>
                                    mk.FileName.String.Equals(pluginNameFromPath,
                                        StringComparison.OrdinalIgnoreCase));
                                if (inferredKey != null && !inferredKey.IsNull)
                                {
                                    specificPluginKey = inferredKey;
                                }
                                else
                                {
                                    try
                                    {
                                        specificPluginKey = ModKey.FromFileName(pluginNameFromPath);
                                    }
                                    catch
                                    {
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(
                                $"Error inferring plugin key from image path '{imagePath}' for modSetting '{displayName}': {ex.Message}");
                        }
                    }
                }
            }

            var appearanceVM =
                _appearanceModFactory(displayName, selectionVm.DisplayName, selectionVm.NpcFormKey,
                    specificPluginKey, imagePath);

            finalModVMs[displayName] = appearanceVM;

            if (modSettingVM.NpcFormKeysToNotifications.TryGetValue(selectionVm.NpcFormKey, out var notif))
            {
                appearanceVM.HasIssueNotification = true;
                appearanceVM.IssueType = notif.IssueType;
                appearanceVM.IssueNotificationText = notif.IssueMessage;
            }

            if (!baseModKey.IsNull && specificPluginKey != null && specificPluginKey.Value.Equals(baseModKey))
            {
                baseKeyHandledByAModSettingVM = true;
            }
        }

        if (!baseModKey.IsNull && !baseKeyHandledByAModSettingVM)
        {
            if (!finalModVMs.ContainsKey(baseModKey.FileName))
            {
                Debug.WriteLine(
                    $"Creating placeholder VM_NpcsMenuMugshot for unhandled base plugin: {baseModKey.FileName} for NPC {selectionVm.NpcFormKey}");
                var placeholderBaseVM =
                    _appearanceModFactory(baseModKey.FileName, selectionVm.DisplayName, selectionVm.NpcFormKey,
                        baseModKey, null);
                finalModVMs[baseModKey.FileName] = placeholderBaseVM;
            }
        }

        var sortedVMs = finalModVMs.Values.OrderBy(vm => vm.ModName).ToList();
        // move auto-gen plugins first
        var ccMugShot = sortedVMs.FirstOrDefault(vm => vm.ModName.Equals(VM_Mods.CreationClubModsettingName));
        if (ccMugShot != null)
        {
            sortedVMs.Remove(ccMugShot);
            sortedVMs.Insert(0, ccMugShot);
        }

        var baseMugShot = sortedVMs.FirstOrDefault(vm => vm.ModName.Equals(VM_Mods.BaseGameModSettingName));
        if (baseMugShot != null)
        {
            sortedVMs.Remove(baseMugShot);
            sortedVMs.Insert(0, baseMugShot);
        }
        // end move

        foreach (var m in sortedVMs)
        {
            bool isGloballyHidden = _hiddenModNames.Contains(m.ModName);
            bool isPerNpcHidden = _hiddenModsPerNpc.TryGetValue(selectionVm.NpcFormKey, out var hiddenSet) &&
                                  hiddenSet.Contains(m.ModName);
            m.IsSetHidden = isGloballyHidden || isPerNpcHidden;
            m.IsCheckedForCompare = false;
        }

        var selectedModName = _consistencyProvider.GetSelectedMod(selectionVm.NpcFormKey);
        if (!string.IsNullOrEmpty(selectedModName))
        {
            var selectedVmInstance = sortedVMs.FirstOrDefault(x =>
                x.ModName.Equals(selectedModName, StringComparison.OrdinalIgnoreCase));
            if (selectedVmInstance != null)
            {
                selectedVmInstance.IsSelected = true;
            }
        }

        return new ObservableCollection<VM_NpcsMenuMugshot>(sortedVMs);
    }

    public void RefreshAppearanceSources()
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

    public void SelectAllFromMod(VM_NpcsMenuMugshot referenceMod)
    {
        if (referenceMod == null || string.IsNullOrWhiteSpace(referenceMod.ModName))
        {
            Debug.WriteLine("SelectAllFromMod: referenceMod or its ModName is null/empty.");
            return;
        }

        string targetModName = referenceMod.ModName;
        int updatedCount = 0;
        Debug.WriteLine($"SelectAllFromMod: Attempting to select '{targetModName}' for all applicable NPCs.");
        foreach (var npcVM in AllNpcs)
        {
            if (npcVM == null) continue;
            if (IsModAnAppearanceSourceForNpc(npcVM, referenceMod))
            {
                _consistencyProvider.SetSelectedMod(npcVM.NpcFormKey, targetModName);
                updatedCount++;
            }
        }

        Debug.WriteLine(
            $"SelectAllFromMod: Finished processing. Attempted to set '{targetModName}' for {updatedCount} NPCs where it was an available source.");
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
    private void AddCurrentNpcToGroup()
    {
        if (SelectedNpc == null || string.IsNullOrWhiteSpace(SelectedGroupName)) return;
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
            ApplyFilter(false);
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
        var groupName = SelectedGroupName.Trim();
        if (_settings.NpcGroupAssignments.TryGetValue(npcKey, out var groups))
        {
            if (groups.Remove(groupName))
            {
                Debug.WriteLine($"Removed NPC {npcKey} from group '{groupName}'");
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
            }
        }
        else
        {
            Debug.WriteLine($"NPC {npcKey} has no group assignments.");
        }
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

    private void AddAllVisibleNpcsToGroup()
    {
        if (FilteredNpcs.Count == 0 || string.IsNullOrWhiteSpace(SelectedGroupName)) return;
        var groupName = SelectedGroupName.Trim();
        int count = FilteredNpcs.Count;
        int totalNpcCount = AllNpcs.Count;
        if (!AreAnyFiltersActive())
        {
            if (ScrollableMessageBox.Confirm(
                    $"No filters are currently applied. Are you sure you want to add ALL {totalNpcCount} NPCs in your game to the group '{groupName}'?",
                    "Confirm Add All NPCs"))
            {
                Debug.WriteLine("Add All Visible NPCs to Group cancelled by user (no filters active).");
                return;
            }
        }
        else
        {
            if (ScrollableMessageBox.Confirm($"Add all {count} currently visible NPCs to the group '{groupName}'?",
                    "Confirm Add Visible NPCs"))
            {
                Debug.WriteLine("Add All Visible NPCs to Group cancelled by user.");
                return;
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
            }
        }

        if (groupListChanged)
        {
            UpdateAvailableNpcGroups();
        }

        ApplyFilter(false);
        Debug.WriteLine($"Added {addedCount} visible NPCs to group '{groupName}'.");
        ScrollableMessageBox.Show($"Added {addedCount} visible NPCs to group '{groupName}'.", "Operation Complete");
    }

    private void RemoveAllVisibleNpcsFromGroup()
    {
        if (FilteredNpcs.Count == 0 || string.IsNullOrWhiteSpace(SelectedGroupName)) return;
        var groupName = SelectedGroupName.Trim();
        int count = FilteredNpcs.Count;
        int totalNpcCount = AllNpcs.Count;
        if (!AreAnyFiltersActive())
        {
            if (ScrollableMessageBox.Confirm(
                    $"No filters are currently applied. Are you sure you want to attempt removing ALL {totalNpcCount} NPCs in your game from the group '{groupName}'?",
                    "Confirm Remove All NPCs", MessageBoxImage.Warning))
            {
                Debug.WriteLine("Remove All Visible NPCs from Group cancelled by user (no filters active).");
                return;
            }
        }
        else
        {
            if (ScrollableMessageBox.Confirm(
                    $"Remove all {count} currently visible NPCs from the group '{groupName}'?",
                    "Confirm Remove Visible NPCs"))
            {
                Debug.WriteLine("Remove All Visible NPCs from Group cancelled by user.");
                return;
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
        ScrollableMessageBox.Show($"Removed {removedCount} visible NPCs from group '{groupName}'.",
            "Operation Complete");
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

        MessageBus.Current.SendMessage(new NpcGroupsChangedMessage());
        Debug.WriteLine($"Updated AvailableNpcGroups. Count: {AvailableNpcGroups.Count}");
    }
    // --- End NPC Group Methods ---

    public void MassUpdateNpcSelections(string fromModName, string toModName)
    {
        if (string.Equals(fromModName, toModName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Find all NPCs whose current selection is fromModName
        var npcsToUpdate = AllNpcs
            .Where(npc => string.Equals(_consistencyProvider.GetSelectedMod(npc.NpcFormKey), fromModName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!npcsToUpdate.Any())
        {
            ScrollableMessageBox.Show($"No NPCs are currently assigned to '{fromModName}'. No changes were made.",
                "Information");
            return;
        }

        // Confirmation dialog
        var confirmationMessage =
            $"This will change the selected appearance for {npcsToUpdate.Count} NPC(s) from '{fromModName}' to '{toModName}'.\n\nAre you sure you want to proceed?";
        string imagePath = @"Resources\Replace Selected Mod.png";
        if (ScrollableMessageBox.Confirm(confirmationMessage, "Out with the old, in with the new?",
                displayImagePath: imagePath))
        {
            foreach (var npc in npcsToUpdate)
            {
                // Set the new mod. The consistency provider will handle the update and notification.
                _consistencyProvider.SetSelectedMod(npc.NpcFormKey, toModName);
            }
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