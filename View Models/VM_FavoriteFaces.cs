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
    public enum FavoriteFacesMode
    {
        Share, // Launched from global "Favs" button
        Apply // Launched from NPC context menu to apply a favorite
    }

    // Dependencies
    private readonly Settings _settings;
    private readonly NpcConsistencyProvider _consistencyProvider;
    private readonly VM_NpcSelectionBar _npcsViewModel;
    private readonly VM_Mods _modsViewModel;
    private readonly Lazy<VM_MainWindow> _lazyMainWindowVm;
    private readonly VM_NpcsMenuSelection? _targetNpcForApply;
    private readonly CompositeDisposable _disposables = new();

    public ViewModelActivator Activator { get; } = new();
    public FavoriteFacesMode Mode { get; }
    public bool IsShareMode => Mode == FavoriteFacesMode.Share;
    public bool IsApplyMode => Mode == FavoriteFacesMode.Apply;

    // UI Properties
    public ObservableCollection<VM_SummaryMugshot> FavoriteMugshots { get; } = new();
    [Reactive] public VM_SummaryMugshot? SelectedMugshot { get; set; }

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

    public delegate void CloseWindowAction();

    public event CloseWindowAction? RequestClose;

    public VM_FavoriteFaces(
        Settings settings,
        NpcConsistencyProvider consistencyProvider,
        VM_NpcSelectionBar npcsViewModel,
        VM_Mods modsViewModel,
        Lazy<VM_MainWindow> lazyMainWindowVm,
        FavoriteFacesMode mode,
        VM_NpcsMenuSelection? targetNpcForApply)
    {
        _settings = settings;
        _consistencyProvider = consistencyProvider;
        _npcsViewModel = npcsViewModel;
        _modsViewModel = modsViewModel;
        _lazyMainWindowVm = lazyMainWindowVm;
        Mode = mode;
        _targetNpcForApply = targetNpcForApply;

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

        this.WhenActivated((CompositeDisposable d) => { LoadFavoritesAsync().ConfigureAwait(false); });
    }

    private async Task LoadFavoritesAsync()
    {
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

                var favVM = new VM_SummaryMugshot(
                    imagePath, sourceNpcKey, sourceNpcName, modName, sourceNpcName,
                    false, false, false, "", hasNoData, "", hasMugshot,
                    _lazyMainWindowVm, _modsViewModel);

                favorites.Add(favVM);
            }

            // Switch to UI thread to update collection
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                FavoriteMugshots.Clear();
                foreach (var fav in favorites) FavoriteMugshots.Add(fav);
                _refreshImageSizesSubject.OnNext(Unit.Default);

                // Asynchronously load the actual image sources
                Task.Run(async () =>
                {
                    foreach (var vm in FavoriteMugshots) await vm.LoadImageAsync();
                });
            });
        });
    }

    private void ApplyFavorite()
    {
        if (SelectedMugshot == null || _targetNpcForApply == null) return;
        _npcsViewModel.AddGuestAppearance(_targetNpcForApply.NpcFormKey, SelectedMugshot.ModDisplayName,
            SelectedMugshot.TargetNpcFormKey, SelectedMugshot.SourceNpcDisplayName);
        _consistencyProvider.SetSelectedMod(_targetNpcForApply.NpcFormKey, SelectedMugshot.ModDisplayName,
            SelectedMugshot.TargetNpcFormKey);
        RequestClose?.Invoke();
    }

    private void MakeFavoriteAvailable()
    {
        if (SelectedMugshot == null || _targetNpcForApply == null) return;
        _npcsViewModel.AddGuestAppearance(_targetNpcForApply.NpcFormKey, SelectedMugshot.ModDisplayName,
            SelectedMugshot.TargetNpcFormKey, SelectedMugshot.SourceNpcDisplayName);
        RequestClose?.Invoke();
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
        _disposables.Dispose();
    }
}