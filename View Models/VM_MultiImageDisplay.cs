using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using NPC_Plugin_Chooser_2.Views; // For ImagePacker
using NPC_Plugin_Chooser_2.Models; // For Settings, if we decide to persist zoom for this view

namespace NPC_Plugin_Chooser_2.View_Models;

public class VM_MultiImageDisplay : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly ISubject<Unit> _refreshImageSizesSubject = new Subject<Unit>();

    public ObservableCollection<IHasMugshotImage> ImagesToDisplay { get; }

    [Reactive] public double ZoomLevel { get; set; } = 100.0;
    [Reactive] public bool IsZoomLocked { get; set; } = false;
    [Reactive] public bool HasUserManuallyZoomed { get; set; } = false;

    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetZoomCommand { get; }

    public IObservable<Unit> RefreshImageSizesObservable => _refreshImageSizesSubject.AsObservable();

    private const double MinZoomPercentage = 10.0; // Might want different min/max for compare view
    private const double MaxZoomPercentage = 500.0;
    private const double ZoomStepPercentage = 5.0;

    // Optional: Inject settings if you want to save/load zoom for this specific view
    // private readonly Settings _settings;

    public VM_MultiImageDisplay(IEnumerable<IHasMugshotImage> images) //, Settings settings)
    {
        ImagesToDisplay =
            new ObservableCollection<IHasMugshotImage>(images.Where(img =>
                img.HasMugshot && (img.MugshotSource != null || !string.IsNullOrEmpty(img.ImagePath))));

        // Initialize zoom from settings if desired, e.g.:
        // ZoomLevel = Math.Max(MinZoomPercentage, Math.Min(MaxZoomPercentage, _settings.CompareViewZoomLevel));
        // IsZoomLocked = _settings.CompareViewIsZoomLocked;

        ZoomInCommand = ReactiveCommand.Create(() =>
        {
            HasUserManuallyZoomed = true;
            ZoomLevel = Math.Min(MaxZoomPercentage, ZoomLevel + ZoomStepPercentage);
        }).DisposeWith(_disposables);

        ZoomOutCommand = ReactiveCommand.Create(() =>
        {
            HasUserManuallyZoomed = true;
            ZoomLevel = Math.Max(MinZoomPercentage, ZoomLevel - ZoomStepPercentage);
        }).DisposeWith(_disposables);

        ResetZoomCommand = ReactiveCommand.Create(() =>
        {
            IsZoomLocked = false;
            HasUserManuallyZoomed = false;
            // The View's RefreshImageSizes will recalculate the optimal zoom and set it
            _refreshImageSizesSubject.OnNext(Unit.Default);
        }).DisposeWith(_disposables);

        this.WhenAnyValue(x => x.ZoomLevel)
            .Skip(1) // Skip initial value
            .Throttle(TimeSpan.FromMilliseconds(50)) //Slightly shorter throttle for responsiveness
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(zoom =>
            {
                double newClampedZoom = Math.Max(MinZoomPercentage, Math.Min(MaxZoomPercentage, zoom));
                if (Math.Abs(ZoomLevel - newClampedZoom) > 0.001 && ZoomLevel == zoom) // Check if it was clamped
                {
                    ZoomLevel = newClampedZoom; // Update property if clamped
                    return; // Avoid re-triggering
                }

                // if (_settings != null) _settings.CompareViewZoomLevel = newClampedZoom; // Save if using settings

                if (IsZoomLocked || HasUserManuallyZoomed)
                {
                    _refreshImageSizesSubject.OnNext(Unit.Default);
                }
            })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.IsZoomLocked)
            .Skip(1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isLocked =>
            {
                // if (_settings != null) _settings.CompareViewIsZoomLocked = isLocked; // Save if using settings
                if (!isLocked) // If unlocking
                {
                    HasUserManuallyZoomed = false;
                }

                _refreshImageSizesSubject.OnNext(Unit.Default);
            })
            .DisposeWith(_disposables);
    }

    public void SignalRefreshNeeded()
    {
        _refreshImageSizesSubject.OnNext(Unit.Default);
    }

    public void Dispose()
    {
        Debug.WriteLine("VM_MultiImageDisplay Disposed");
        _disposables.Dispose();
    }
}