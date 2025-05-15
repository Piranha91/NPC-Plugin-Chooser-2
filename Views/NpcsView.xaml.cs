// NpcsView.xaml.cs (Revised RefreshImageSizes)
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq; 
using System.Reactive;
using System.Reactive.Disposables; 
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;        
using System.Windows.Threading; 
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat;


namespace NPC_Plugin_Chooser_2.Views
{
    public partial class NpcsView : ReactiveUserControl<VM_NpcSelectionBar>
    {
        private readonly CompositeDisposable _viewBindings = new CompositeDisposable();

        public NpcsView()
        {
            InitializeComponent();

            if (this.DataContext == null) {
                try {
                    var vm = Locator.Current.GetService<VM_NpcSelectionBar>();
                    if (vm != null) {
                        this.DataContext = vm; this.ViewModel = vm;
                    } 
                } catch (Exception ex) {
                    Debug.WriteLine($"DEBUG: Error manually resolving/setting DataContext in NpcsView: {ex.Message}");
                }
            }

            this.WhenActivated(d => 
            {
                d.DisposeWith(_viewBindings); 

                if (ViewModel == null) return;

                this.OneWayBind(ViewModel, vm => vm.FilteredNpcs, v => v.NpcListBox.ItemsSource).DisposeWith(d); 
                this.Bind(ViewModel, vm => vm.SelectedNpc, v => v.NpcListBox.SelectedItem).DisposeWith(d);
                this.OneWayBind(ViewModel, vm => vm.CurrentNpcAppearanceMods, v => v.AppearanceModsItemsControl.ItemsSource).DisposeWith(d);
                
                // --- TextBox Zoom Level Binding with Throttle ---
                // One-way from VM to View (for display, formatted)
                this.WhenAnyValue(x => x.ViewModel.NpcsViewZoomLevel)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Select(val => val.ToString("F2", CultureInfo.InvariantCulture))
                    .BindTo(this, v => v.ZoomPercentageTextBox.Text)
                    .DisposeWith(d);

                // From View (TextBox) to VM, with throttle
                Observable.FromEventPattern<TextChangedEventArgs>(ZoomPercentageTextBox, nameof(ZoomPercentageTextBox.TextChanged))
                    .Select(ep => ((TextBox)ep.Sender).Text)
                    .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler) // Adjust throttle time as needed
                    .ObserveOn(RxApp.MainThreadScheduler) // Ensure update happens on UI thread if VM property change has UI effects immediately
                    .Subscribe(text =>
                    {
                        if (ViewModel != null)
                        {
                            if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
                            {
                                ViewModel.NpcsViewHasUserManuallyZoomed = true;
                                double clampedResult = Math.Max(10.0, Math.Min(500.0, result));
                                // Only update if the parsed and clamped value is different from current VM value
                                // to avoid redundant updates and potential loops if formatting causes slight changes.
                                if (Math.Abs(ViewModel.NpcsViewZoomLevel - clampedResult) > 0.001) 
                                {
                                     ViewModel.NpcsViewZoomLevel = clampedResult;
                                }
                            }
                            // Optional: handle parse failure, e.g., revert textbox to VM value or show error
                            // else if the textbox is not empty, it means invalid input.
                            // Could reset textbox text to current ViewModel.NpcsViewZoomLevel.ToString("F2")
                            else if (!string.IsNullOrWhiteSpace(text))
                            {
                                // Re-set the textbox from VM if parsing fails to avoid inconsistent state
                                ZoomPercentageTextBox.Text = ViewModel.NpcsViewZoomLevel.ToString("F2", CultureInfo.InvariantCulture);
                            }
                        }
                    })
                    .DisposeWith(d);
                // --- End TextBox Zoom Level Binding ---
                
                this.BindCommand(ViewModel, vm => vm.ZoomInNpcsCommand, v => v.ZoomInButton).DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.ZoomOutNpcsCommand, v => v.ZoomOutButton).DisposeWith(d);
                this.Bind(ViewModel, vm => vm.NpcsViewIsZoomLocked, v => v.LockZoomCheckBox.IsChecked).DisposeWith(d);


                ViewModel.RefreshImageSizesObservable // This subject is signaled by VM when things like SelectedNpc, ShowHidden, ZoomLevel, or LockState change
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => RefreshImageSizes())
                    .DisposeWith(d);
                
                // Subscribe to the ViewModel's scroll request observable
        ViewModel.RequestScrollToNpcObservable
            .Where(npcToScrollTo => npcToScrollTo != null) // Only act if there's an NPC
            .ObserveOn(RxApp.MainThreadScheduler) // Ensure UI operations are on the UI thread
            .Subscribe(async npcToScrollTo => // Make lambda async
            {
                Debug.WriteLine($"NpcsView.WhenActivated: Received scroll request for {npcToScrollTo.DisplayName}");
                try
                {
                    // Give the ListBox a moment to update its items after SelectedNpc might have changed
                    await Task.Delay(100); // Adjust delay as needed, or try DispatcherPriority.Loaded/Render
                    NpcListBox.UpdateLayout(); // Force layout to ensure containers are generated

                    if (NpcListBox.Items.Contains(npcToScrollTo))
                    {
                        var listBoxItem = NpcListBox.ItemContainerGenerator.ContainerFromItem(npcToScrollTo) as ListBoxItem;
                        if (listBoxItem != null)
                        {
                            //listBoxItem.Focus(); // Optional
                            NpcListBox.ScrollIntoView(npcToScrollTo);
                            Debug.WriteLine($"NpcsView: Scrolled to {npcToScrollTo.DisplayName} (ListBoxItem found).");
                        }
                        else
                        {
                            // Fallback if container not immediately found (common with virtualization)
                            NpcListBox.ScrollIntoView(npcToScrollTo);
                            Debug.WriteLine($"NpcsView: Scrolled to {npcToScrollTo.DisplayName} (general ScrollIntoView, item container might still be virtualized).");
                            
                            // Optional: A slightly more robust retry for virtualized items
                            await Task.Delay(50); // Brief wait for virtualization
                            NpcListBox.UpdateLayout();
                            if (NpcListBox.ItemContainerGenerator.ContainerFromItem(npcToScrollTo) is ListBoxItem finalItem)
                            {
                                NpcListBox.ScrollIntoView(finalItem); // Scroll to the actual container
                                Debug.WriteLine($"NpcsView: Retry scroll for {npcToScrollTo.DisplayName} using ListBoxItem successful.");
                            }
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"NpcsView: NPC {npcToScrollTo.DisplayName} not in ListBox items when trying to scroll.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"NpcsView: Error during scroll attempt for {npcToScrollTo.DisplayName}: {ex.Message}");
                }
                }).DisposeWith(d); 

                if (ViewModel.CurrentNpcAppearanceMods != null && ViewModel.CurrentNpcAppearanceMods.Any())
                {
                    RefreshImageSizes(); // Initial call
                }
            });
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null) return;

            if (sender is ScrollViewer scrollViewer)
            {
                if (scrollViewer.Name == "ImageDisplayScrollViewer" && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    double change = (e.Delta > 0 ? 1 : -1) * 1.0; 
                    ViewModel.NpcsViewHasUserManuallyZoomed = true; 
                    ViewModel.NpcsViewZoomLevel = Math.Max(10, Math.Min(500, ViewModel.NpcsViewZoomLevel + change));
                    e.Handled = true;
                }
            }
        }
        
        private void ImageDisplayScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ViewModel != null && !ViewModel.NpcsViewIsZoomLocked)
            {
                ViewModel.NpcsViewHasUserManuallyZoomed = false; // Size change should allow packer to take over if unlocked
            }
            RefreshImageSizes();
        }
        
        private void NpcListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // VM_NpcSelectionBar's subscription to SelectedNpc already handles resetting NpcsViewHasUserManuallyZoomed
            // RefreshImageSizes is called via the CurrentNpcAppearanceMods changing or _refreshImageSizesSubject
        }
        
        private void RefreshImageSizes()
        {
            if (ViewModel?.CurrentNpcAppearanceMods == null) return;

            var imagesToProcess = ViewModel.CurrentNpcAppearanceMods; // This is ObservableCollection<VM_AppearanceMod>
            if (!imagesToProcess.Any()) {
                 // If no images, nothing to do for sizing.
                 // The displayed zoom level in textbox remains what it was.
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewModel == null || ViewModel.CurrentNpcAppearanceMods == null || !ImageDisplayScrollViewer.IsLoaded) return;

                var visibleImages = imagesToProcess
                                    .Where(img => img.IsVisible && img.OriginalDipDiagonal > 0)
                                    .ToList<IHasMugshotImage>(); // Cast to list of interface

                if (!visibleImages.Any())
                {
                    // Set all to 0 if no visible images with valid diagonals
                    foreach (var img in imagesToProcess) { img.ImageWidth = 0; img.ImageHeight = 0; }
                    return;
                }

                // This is the core logic change:
                if (ViewModel.NpcsViewIsZoomLocked || ViewModel.NpcsViewHasUserManuallyZoomed)
                {
                    // Apply direct scaling based on user's (locked or manual) zoom level
                    double sumOfDiagonals = visibleImages.Sum(img => img.OriginalDipDiagonal);
                    double averageOriginalDipDiagonal = sumOfDiagonals / visibleImages.Count;
                    if (averageOriginalDipDiagonal <= 0) averageOriginalDipDiagonal = 100.0; // Fallback

                    double userZoomFactor = ViewModel.NpcsViewZoomLevel / 100.0;

                    foreach (var img in imagesToProcess) // Iterate over the master list to set all, visible or not
                    {
                        if (img.IsVisible && img.OriginalDipDiagonal > 0)
                        {
                            double individualScaleFactor = (averageOriginalDipDiagonal / img.OriginalDipDiagonal) * userZoomFactor;
                            img.ImageWidth = img.OriginalDipWidth * individualScaleFactor;
                            img.ImageHeight = img.OriginalDipHeight * individualScaleFactor;
                        }
                        else
                        {
                            img.ImageWidth = 0; // Not visible or invalid
                            img.ImageHeight = 0;
                        }
                    }
                }
                else
                {
                    // Unlocked and not manually zoomed: Let packer fit original DIPs
                    var availableHeight = ImageDisplayScrollViewer.ViewportHeight;
                    var availableWidth = ImageDisplayScrollViewer.ViewportWidth;

                    if (availableHeight > 0 && availableWidth > 0)
                    {
                        // ImagePacker needs ObservableCollection<IHasMugshotImage>
                        var imagesForPacker = new ObservableCollection<IHasMugshotImage>(imagesToProcess.Cast<IHasMugshotImage>());
                        
                        double packerScaleFactor = ImagePacker.FitOriginalImagesToContainer(
                            imagesForPacker, 
                            availableHeight,
                            availableWidth,
                            5 // xamlItemUniformMargin (from XAML Margin="5")
                        );
                        
                        // Update the ViewModel's zoom level to reflect what the packer did
                        // No need to set NpcsViewHasUserManuallyZoomed false here, it should already be false to reach this branch
                        ViewModel.NpcsViewZoomLevel = packerScaleFactor * 100.0;
                    }
                }
            }), DispatcherPriority.Loaded);
        }

        private void Image_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (sender is FrameworkElement element && element.DataContext is VM_AppearanceMod vm)
                {
                    if (vm.ToggleFullScreenCommand.CanExecute.FirstAsync().Wait())
                    {
                        vm.ToggleFullScreenCommand.Execute(Unit.Default).Subscribe().DisposeWith(_viewBindings);
                    }
                    e.Handled = true;
                }
            }
        }

        private void ZoomPercentageTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null || !(sender is TextBox textBox)) return;

            double currentValue = ViewModel.NpcsViewZoomLevel;
            double change = (e.Delta > 0 ? 1 : -1) * 1.0; 
            
            ViewModel.NpcsViewHasUserManuallyZoomed = true; 
            ViewModel.NpcsViewZoomLevel = Math.Max(10, Math.Min(500, currentValue + change));
            
            // The WhenAnyValue binding from VM to Textbox will update the display.
            // The explicit TextChanged subscription will push it back to VM after throttle.
            e.Handled = true;
        }
    }
}