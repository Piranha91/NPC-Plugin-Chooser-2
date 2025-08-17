// Views/ModsView.xaml.cs (Revised RefreshMugshotImageSizes)
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat; 
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;      
using System.Reactive; 
using System.Reactive.Linq; 
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using System.Windows.Controls; 
using System.Windows.Input;  
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading; 

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class ModsView : ReactiveUserControl<VM_Mods>
    {
        private readonly CompositeDisposable _viewBindings = new CompositeDisposable(); 
        private readonly Subject<SizeChangedEventArgs> _sizeChangedSubject = new Subject<SizeChangedEventArgs>();
        private const double _minZoomPercentage = 1.0;
        private const double _maxZoomPercentage = 1000.0;
        private const double _zoomStepPercentage = 2.5; // For +/- buttons and scroll wheel
        private bool _isInitialLayout = true; // Flag for one-time initial sizing
        private bool _userHasAdjustedSplitter = false; // Flag to track user interaction

        public ModsView()
        {
            InitializeComponent();

            if (this.DataContext == null)
            {
                try {
                    ViewModel = Locator.Current.GetService<VM_Mods>();
                    DataContext = this.ViewModel;
                } catch (Exception ex) {
                    Debug.WriteLine($"Error resolving VM_Mods: {ex.Message}");
                }
            }

            this.WhenActivated(d =>
            {
                _viewBindings.Clear(); // Clear previous bindings if any (good practice for WhenActivated)
                d.DisposeWith(_viewBindings);
                if (ViewModel == null) return;

                // --- TextBox Zoom Level Binding with Throttle ---
                // One-way from VM to View (for display, formatted)
                this.WhenAnyValue(x => x.ViewModel.ModsViewZoomLevel)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Select(val => val.ToString("F2", CultureInfo.InvariantCulture))
                    .BindTo(this, v => v.ZoomPercentageTextBoxMods.Text)
                    .DisposeWith(d);

                // From View (TextBox) to VM, with throttle
                Observable.FromEventPattern<TextChangedEventArgs>(ZoomPercentageTextBoxMods, nameof(ZoomPercentageTextBoxMods.TextChanged))
                    .Select(ep => ((TextBox)ep.Sender).Text)
                    .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler) 
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(text =>
                    {
                        Debug.WriteLine($"ModsView: ZoomPercentageTextBoxMods TextChanged to '{text}' (throttled)");
                        if (ViewModel != null)
                        {
                            if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
                            {
                                ViewModel.ModsViewHasUserManuallyZoomed = true;
                                double clampedResult = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, result)); // Use field
                                if (Math.Abs(ViewModel.ModsViewZoomLevel - clampedResult) > 0.001) 
                                {
                                    Debug.WriteLine($"ModsView: Textbox updating VM.ModsViewZoomLevel to {clampedResult}");
                                    ViewModel.ModsViewZoomLevel = clampedResult;
                                }
                            }
                            else if (!string.IsNullOrWhiteSpace(text))
                            {
                                Debug.WriteLine($"ModsView: Textbox parse failed for '{text}', resetting to VM value.");
                                ZoomPercentageTextBoxMods.Text = ViewModel.ModsViewZoomLevel.ToString("F2", CultureInfo.InvariantCulture);
                            }
                        }
                    })
                    .DisposeWith(d);
                // --- End TextBox Zoom Level Binding ---
                
                _sizeChangedSubject
                    .Throttle(TimeSpan.FromMilliseconds(250), RxApp.MainThreadScheduler)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(args => {
                        Debug.WriteLine($"ModsView: _sizeChangedSubject (Throttled ScrollViewer.SizeChanged) triggered. New Size: {args.NewSize.Width}x{args.NewSize.Height}");
                        if (ViewModel != null && !ViewModel.ModsViewIsZoomLocked && !_userHasAdjustedSplitter)
                        {
                            ViewModel.ModsViewHasUserManuallyZoomed = false;
                        }
                        // Here, the ScrollViewer's size *has* changed, so we can directly refresh.
                        // No need for the extra invalidation of MainContentGridForSplitter,
                        // as this path is a *result* of layout changes, not a trigger for them in the same way.
                        RefreshMugshotImageSizes();
                    })
                    .DisposeWith(d);
                
                // Subscription to GridSplitter DragCompleted (NEW explicit refresh trigger)
                Observable.FromEventPattern<DragCompletedEventArgs>(ColumnSplitter, nameof(GridSplitter.DragCompleted))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(ep => {
                        _userHasAdjustedSplitter = true;
                        Debug.WriteLine("ModsView: ColumnSplitter_DragCompleted. User has adjusted.");
                        if(ViewModel != null) ViewModel.ModsViewHasUserManuallyZoomed = true; // Keep user's intent

                        // Sequence: 1. Invalidate Grid, 2. Update Grid Layout, 3. Refresh Images
                        Dispatcher.BeginInvoke(new Action(() => {
                            Debug.WriteLine("ModsView: DragCompleted - Phase 1: Invalidating MainContentGridForSplitter.");
                            if (MainContentGridForSplitter.IsLoaded)
                            {
                                MainContentGridForSplitter.InvalidateMeasure();
                                MainContentGridForSplitter.InvalidateArrange(); // Invalidate both
                                MainContentGridForSplitter.UpdateLayout();      // Force re-layout of the grid and its columns
                            }
                            // Now that the grid owning the columns has hopefully updated,
                            // queue the image refresh to run after this.
                            Dispatcher.BeginInvoke(new Action(() => {
                                Debug.WriteLine("ModsView: DragCompleted - Phase 2: Calling RefreshMugshotImageSizes.");
                                RefreshMugshotImageSizes();
                            }), DispatcherPriority.Background); // Or Loaded. Background is safer.
                        }), DispatcherPriority.ContextIdle); // Or even Send if you want it more immediate after drag.
                    })
                    .DisposeWith(d);
                
                this.BindCommand(ViewModel, vm => vm.ZoomInModsCommand, v => v.ZoomInButtonMods).DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.ZoomOutModsCommand, v => v.ZoomOutButtonMods).DisposeWith(d);
                this.Bind(ViewModel, vm => vm.ModsViewIsZoomLocked, v => v.LockZoomCheckBoxMods.IsChecked).DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.ResetZoomModsCommand, v => v.ResetZoomModsButton).DisposeWith(d); // NEW BINDING


                ViewModel.RefreshMugshotSizesObservable
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => {
                        Debug.WriteLine("ModsView: RefreshMugshotSizesObservable (from VM) triggered.");
                        // This is often called after VM property changes (like ResetZoom).
                        // We need to ensure the Grid is also up-to-date here.
                        Dispatcher.BeginInvoke(new Action(() => {
                            Debug.WriteLine("ModsView: VM Refresh - Phase 1: Invalidating MainContentGridForSplitter.");
                            if (MainContentGridForSplitter.IsLoaded)
                            {
                                MainContentGridForSplitter.InvalidateMeasure();
                                MainContentGridForSplitter.InvalidateArrange();
                                MainContentGridForSplitter.UpdateLayout();
                            }
                            Dispatcher.BeginInvoke(new Action(() => {
                                Debug.WriteLine("ModsView: VM Refresh - Phase 2: Calling RefreshMugshotImageSizes.");
                                RefreshMugshotImageSizes();
                            }), DispatcherPriority.Background);
                        }), DispatcherPriority.ContextIdle);
                    })
                    .DisposeWith(d);
                
                if (ViewModel.CurrentModNpcMugshots != null && ViewModel.CurrentModNpcMugshots.Any())
                {
                    RefreshMugshotImageSizes();
                }
                
                // NEW: Subscribe to the ViewModel's scroll request observable for Mods
                ViewModel.RequestScrollToModObservable
            .Where(modToScrollTo => modToScrollTo != null) 
            .ObserveOn(RxApp.MainThreadScheduler) 
            .Subscribe(async modSettingToScrollTo => 
            {
                Debug.WriteLine($"ModsView.WhenActivated: Received scroll request for ModSetting {modSettingToScrollTo.DisplayName}");
                try
                {
                    await Task.Delay(150); // Increased delay slightly for UI to settle
                    ModSettingsItemsControl.UpdateLayout(); 

                    if (ModSettingsItemsControl.Items.Contains(modSettingToScrollTo))
                    {
                        var container = ModSettingsItemsControl.ItemContainerGenerator.ContainerFromItem(modSettingToScrollTo) as FrameworkElement;
                        
                        if (container != null)
                        {
                            container.BringIntoView();
                            Debug.WriteLine($"ModsView: Scrolled to {modSettingToScrollTo.DisplayName} using BringIntoView on container.");
                        }
                        else
                        {
                            Debug.WriteLine($"ModsView: Container for {modSettingToScrollTo.DisplayName} not found after initial UpdateLayout. Item might be virtualized or not yet rendered.");
                            // For a plain ItemsControl that isn't virtualizing heavily, if the container is null,
                            // it often means the item is simply not visible or not yet part of the visual tree.
                            // If ModSettingsItemsControl *is* virtualizing (e.g. its ItemsPanel is VirtualizingStackPanel),
                            // then this is a harder problem without ListBox.ScrollIntoView.
                            // One advanced technique would be to calculate the approximate offset based on item index and average item height,
                            // then use ScrollViewer.ScrollToVerticalOffset. This is complex.

                            // As a simpler fallback, if your ItemsControl is NOT virtualizing,
                            // this else block might mean the item isn't present or an issue with ItemContainerGenerator.
                            // If it IS virtualizing, and BringIntoView on the container is the goal,
                            // and the container is null, we are a bit stuck with simple methods.
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"ModsView: ModSetting {modSettingToScrollTo.DisplayName} not in ModSettingsItemsControl items when trying to scroll.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ModsView: Error during scroll attempt for {modSettingToScrollTo.DisplayName}: {ex.Message}");
                }
            })
            .DisposeWith(d); 
            });
        }

        private void ModsView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isInitialLayout && !_userHasAdjustedSplitter)
            {
                AdjustLeftColumnWidth();
                _isInitialLayout = false; // Ensure this runs only once per load unless reset
            }
        }
        
        private void AdjustLeftColumnWidth()
        {
            // Ensure the grid has had a chance to perform its initial layout pass
            // to get a valid ActualWidth.
            if (MainContentGridForSplitter.ActualWidth > 0 && LeftColumnForModList != null)
            {
                double availableWidth = MainContentGridForSplitter.ActualWidth;
                double targetWidth = availableWidth * 0.25;

                // Respect MinWidth if defined on the ColumnDefinition
                if (targetWidth < LeftColumnForModList.MinWidth)
                {
                    targetWidth = LeftColumnForModList.MinWidth;
                }
                // Respect MaxWidth if defined (though you weren't using it for this column)
                if (targetWidth > LeftColumnForModList.MaxWidth)
                {
                    targetWidth = LeftColumnForModList.MaxWidth;
                }

                // Set the Width. GridLength can take a double for pixel value.
                // It's important to set it as a pixel value here, not a star,
                // because we want a specific size based on the current parent width.
                // The GridSplitter will then operate on this pixel-defined width.
                LeftColumnForModList.Width = new GridLength(targetWidth, GridUnitType.Pixel);

                Debug.WriteLine($"Initial AdjustLeftColumnWidth: Available={availableWidth:F2}, Target 25%={targetWidth:F2}. LeftColumn set to {LeftColumnForModList.Width}");
            }
            else
            {
                Debug.WriteLine("Initial AdjustLeftColumnWidth: MainContentGridForSplitter.ActualWidth is 0 or LeftColumnForModList is null. Deferring.");
                // If ActualWidth is 0, the layout hasn't completed yet.
                // We can try to dispatch this to run after the current layout pass.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isInitialLayout && !_userHasAdjustedSplitter && MainContentGridForSplitter.ActualWidth > 0 && LeftColumnForModList != null)
                    {
                        // Re-attempt after dispatch
                        double availableWidth = MainContentGridForSplitter.ActualWidth;
                        double targetWidth = availableWidth * 0.25;
                        if (targetWidth < LeftColumnForModList.MinWidth) targetWidth = LeftColumnForModList.MinWidth;
                        if (targetWidth > LeftColumnForModList.MaxWidth) targetWidth = LeftColumnForModList.MaxWidth;
                        LeftColumnForModList.Width = new GridLength(targetWidth, GridUnitType.Pixel);
                        Debug.WriteLine($"Initial AdjustLeftColumnWidth (deferred): Available={availableWidth:F2}, Target 25%={targetWidth:F2}. LeftColumn set to {LeftColumnForModList.Width}");
                        _isInitialLayout = false; // Still mark as done
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void MugshotScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Debug.WriteLine($"ModsView: MugshotScrollViewer_SizeChanged RAW event. New size: {e.NewSize.Width}x{e.NewSize.Height}. Pushing to _sizeChangedSubject.");
            _sizeChangedSubject.OnNext(e);
        }

        private void MugshotScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel?.CurrentModNpcMugshots == null || !ViewModel.CurrentModNpcMugshots.Any()) return;
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                double change = (e.Delta > 0 ? 1 : -1) * _zoomStepPercentage; // Use field
                ViewModel.ModsViewHasUserManuallyZoomed = true; 
                ViewModel.ModsViewZoomLevel = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, ViewModel.ModsViewZoomLevel + change)); // Use fields
                e.Handled = true; 
            }
        }

         private void MugshotItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
         {
             if (Keyboard.Modifiers == ModifierKeys.Control)
             {
                 if (sender is FrameworkElement element && element.DataContext is VM_ModsMenuMugshot vm)
                 {
                      if (vm.ToggleFullScreenCommand.CanExecute.FirstAsync().Wait())
                      {
                          vm.ToggleFullScreenCommand.Execute(Unit.Default).Subscribe().DisposeWith(_viewBindings);
                      }
                      e.Handled = true; 
                 }
             }
         }
         
        private void RefreshMugshotImageSizes()
        {
            if (ViewModel == null) { Debug.WriteLine("ModsView.RefreshMugshotImageSizes: ViewModel is null. Skipping."); return; }
            if (ViewModel.CurrentModNpcMugshots == null) { Debug.WriteLine("ModsView.RefreshMugshotImageSizes: CurrentModNpcMugshots is null. Skipping."); return; }

            var imagesToProcess = ViewModel.CurrentModNpcMugshots;
            // No need to check imagesToProcess.Any() here, ImagePacker handles empty list.

            Debug.WriteLine($"ModsView.RefreshMugshotImageSizes: ENTER. VM.IsZoomLocked: {ViewModel.ModsViewIsZoomLocked}, VM.HasUserManuallyZoomed: {ViewModel.ModsViewHasUserManuallyZoomed}, VM.ZoomLevel: {ViewModel.ModsViewZoomLevel:F2}");

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewModel == null || ViewModel.CurrentModNpcMugshots == null || !MugshotScrollViewer.IsLoaded)
                {
                    Debug.WriteLine("ModsView.RefreshMugshotImageSizes (Dispatcher): VM/Collection null or ScrollViewer not loaded. Skipping.");
                    return;
                }

                // Force layout update to ensure ActualWidth/ViewportWidth are current
                MugshotScrollViewer.UpdateLayout();
                MugshotsItemsControl.UpdateLayout(); // Also update the ItemsControl which contains the WrapPanel

                // Use ActualWidth when HorizontalScrollBarVisibility is Disabled, as ViewportWidth might not be what we expect.
                // ActualWidth reflects the space allocated to the ScrollViewer by its parent.
                double availableWidth = MugshotScrollViewer.ActualWidth;
                double availableHeight = MugshotScrollViewer.ActualHeight; // Using ActualHeight too for consistency

                // If vertical scrollbar is visible, subtract its width from availableWidth for packing.
                // And use ViewportHeight for availableHeight.
                if (MugshotScrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                {
                    availableHeight = MugshotScrollViewer.ViewportHeight;
                    // Assuming SystemParameters.VerticalScrollBarWidth is a close enough approximation
                    availableWidth -= SystemParameters.VerticalScrollBarWidth;
                }
                 // Ensure availableWidth is not negative if scrollbar is wider than the viewer (edge case)
                availableWidth = Math.Max(0, availableWidth);


                Debug.WriteLine($"ModsView.RefreshMugshotImageSizes (Dispatcher BEGIN):");
                Debug.WriteLine($"  ScrollViewer.IsLoaded: {MugshotScrollViewer.IsLoaded}");
                Debug.WriteLine($"  ScrollViewer.ActualWidth: {MugshotScrollViewer.ActualWidth}, ScrollViewer.ViewportWidth: {MugshotScrollViewer.ViewportWidth}");
                Debug.WriteLine($"  ScrollViewer.ExtentWidth: {MugshotScrollViewer.ExtentWidth}, ScrollViewer.ScrollableWidth: {MugshotScrollViewer.ScrollableWidth}");
                Debug.WriteLine($"  ScrollViewer.ActualHeight: {MugshotScrollViewer.ActualHeight}, ScrollViewer.ViewportHeight: {MugshotScrollViewer.ViewportHeight}");
                Debug.WriteLine($"  ScrollViewer.ComputedVerticalScrollBarVisibility: {MugshotScrollViewer.ComputedVerticalScrollBarVisibility}");
                Debug.WriteLine($"  ItemsControl.ActualWidth: {MugshotsItemsControl.ActualWidth}");
                Debug.WriteLine($"  Calculated availableWidth for Packer: {availableWidth}, availableHeight for Packer: {availableHeight}");


                // The ImagePacker itself filters for IsVisible and valid dimensions.
                // We pass the whole collection from the ViewModel.
                var imagesForPacker = ViewModel.CurrentModNpcMugshots;


                if (ViewModel.ModsViewIsZoomLocked || ViewModel.ModsViewHasUserManuallyZoomed)
                {
                    Debug.WriteLine("ModsView.RefreshMugshotImageSizes (Dispatcher): Applying DIRECT scaling (Locked or Manual Zoom).");

                    var visibleImagesForDirectScale = imagesForPacker
                        .Where(img => img.IsVisible && img.OriginalDipDiagonal > 0)
                        .ToList();

                    if (!visibleImagesForDirectScale.Any())
                    {
                        Debug.WriteLine("ModsView.RefreshMugshotImageSizes (Dispatcher): No visible images for direct scaling.");
                        foreach (var img in imagesForPacker) { img.ImageWidth = 0; img.ImageHeight = 0; } // Clear all
                        return;
                    }

                    double sumOfDiagonals = visibleImagesForDirectScale.Sum(img => img.OriginalDipDiagonal);
                    double averageOriginalDipDiagonal = sumOfDiagonals / visibleImagesForDirectScale.Count;
                    // No need for fallback if averageOriginalDipDiagonal is 0 because visibleImagesForDirectScale ensures OriginalDipDiagonal > 0

                    double userZoomFactor = ViewModel.ModsViewZoomLevel / 100.0;
                    Debug.WriteLine($"ModsView.RefreshMugshotImageSizes (Dispatcher): DIRECT - AvgDiag: {averageOriginalDipDiagonal:F2}, UserZoomFactor: {userZoomFactor:F2}");

                    foreach (var img in imagesForPacker) // Iterate over the full list to update all
                    {
                        if (img.IsVisible && img.OriginalDipDiagonal > 0)
                        {
                            double individualScaleFactor = (averageOriginalDipDiagonal / img.OriginalDipDiagonal) * userZoomFactor;
                            img.ImageWidth = img.OriginalDipWidth * individualScaleFactor;
                            img.ImageHeight = img.OriginalDipHeight * individualScaleFactor;
                        }
                        else // Not visible or invalid original dimensions
                        {
                            img.ImageWidth = 0;
                            img.ImageHeight = 0;
                        }
                    }
                }
                else // Packer scaling (Unlocked and Not Manually Zoomed)
                {
                    Debug.WriteLine("ModsView.RefreshMugshotImageSizes (Dispatcher): Applying PACKER scaling.");

                    if (availableHeight > 0 && availableWidth > 0)
                    {
                        // ImagePacker.FitOriginalImagesToContainer expects ObservableCollection<IHasMugshotImage>
                        // and will modify the ImageWidth/ImageHeight of the items within it.
                        // Since imagesForPacker from ViewModel is already ObservableCollection<VM_ModsMenuMugshot>
                        // and VM_ModsMenuMugshot implements IHasMugshotImage, we can cast.
                        // However, the method signature is specific.
                        // It's better if the ImagePacker can take IEnumerable<IHasMugshotImage>
                        // or if we pass a new ObservableCollection as it expects.
                        // For now, let's assume the packer method is updated or we make a temp collection.

                        var tempCollectionForPacker = new ObservableCollection<IHasMugshotImage>(imagesForPacker.Cast<IHasMugshotImage>());

                        double packerScaleFactor = ImagePacker.FitOriginalImagesToContainer(
                            tempCollectionForPacker, // Pass the casted & potentially filtered collection
                            availableHeight,
                            availableWidth,
                            5, // xamlItemUniformMargin (from XAML Margin="5")
                            ViewModel.NormalizeImageDimensions,
                            ViewModel.MaxMugshotsToFit
                        );

                        // After packer runs, items in tempCollectionForPacker have updated ImageWidth/Height.
                        // We need to transfer these back if tempCollectionForPacker was a new collection of *new* VMs.
                        // But if it's a collection of *references* to the original VMs, they are already updated.
                        // The current ImagePacker modifies the items in the passed collection.
                        // The Cast().ToList() then new ObservableCollection(list) creates new list with original references.

                        Debug.WriteLine($"ModsView.RefreshMugshotImageSizes (Dispatcher): Packer returned scaleFactor: {packerScaleFactor:F4}. Updating VM.ModsViewZoomLevel.");
                        if (ViewModel != null) // Check ViewModel again as this is in a lambda
                        {
                             ViewModel.ModsViewZoomLevel = packerScaleFactor * 100.0;
                        }
                    }
                    else
                    {
                        Debug.WriteLine("ModsView.RefreshMugshotImageSizes (Dispatcher): Packer NOT called due to zero calculated available height/width.");
                        // If packer isn't called, images might retain old sizes or need clearing.
                        // Let's clear them to avoid stale display if container becomes too small.
                        foreach (var img in imagesForPacker) { img.ImageWidth = 0; img.ImageHeight = 0; }
                    }
                }
                Debug.WriteLine("ModsView.RefreshMugshotImageSizes (Dispatcher): EXIT.");
            }), DispatcherPriority.Background); // Using Background for more layout time
        }


        private void ZoomPercentageTextBoxMods_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null || !(sender is TextBox textBox)) return;
            double currentValue = ViewModel.ModsViewZoomLevel;
            double change = (e.Delta > 0 ? 1 : -1) * _zoomStepPercentage; // Use field
            
            ViewModel.ModsViewHasUserManuallyZoomed = true; 
            ViewModel.ModsViewZoomLevel = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, currentValue + change)); // Use fields
            
            var binding = textBox.GetBindingExpression(TextBox.TextProperty);
            binding?.UpdateSource();
            textBox.CaretIndex = textBox.Text.Length;
            textBox.SelectAll();
            e.Handled = true;
        }
    }
    
    // Helper extension method (place in a utility class or at the bottom of ModsView.xaml.cs if local)
    public static class FrameworkElementExtensions
    {
        public static T? TryFindParent<T>(this DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            T parent = parentObject as T;
            if (parent != null)
                return parent;
            else
                return TryFindParent<T>(parentObject);
        }
    }
}