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
using System.Windows.Controls; 
using System.Windows.Input;  
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading; 

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class ModsView : ReactiveUserControl<VM_Mods>
    {
        private readonly CompositeDisposable _viewBindings = new CompositeDisposable(); 

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
                    .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler) // Adjust throttle time as needed
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(text =>
                    {
                        if (ViewModel != null)
                        {
                            if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
                            {
                                ViewModel.ModsViewHasUserManuallyZoomed = true;
                                double clampedResult = Math.Max(10.0, Math.Min(500.0, result));
                                if (Math.Abs(ViewModel.ModsViewZoomLevel - clampedResult) > 0.001)
                                {
                                     ViewModel.ModsViewZoomLevel = clampedResult;
                                }
                            }
                            else if (!string.IsNullOrWhiteSpace(text))
                            {
                                ZoomPercentageTextBoxMods.Text = ViewModel.ModsViewZoomLevel.ToString("F2", CultureInfo.InvariantCulture);
                            }
                        }
                    })
                    .DisposeWith(d);
                // --- End TextBox Zoom Level Binding ---
                
                this.BindCommand(ViewModel, vm => vm.ZoomInModsCommand, v => v.ZoomInButtonMods).DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.ZoomOutModsCommand, v => v.ZoomOutButtonMods).DisposeWith(d);
                this.Bind(ViewModel, vm => vm.ModsViewIsZoomLocked, v => v.LockZoomCheckBoxMods.IsChecked).DisposeWith(d);


                ViewModel.RefreshMugshotSizesObservable
                    .ObserveOn(RxApp.MainThreadScheduler) 
                    .Subscribe(_ => RefreshMugshotImageSizes())
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


        private void MugshotScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ViewModel != null && !ViewModel.ModsViewIsZoomLocked)
            {
                ViewModel.ModsViewHasUserManuallyZoomed = false;
            }
            RefreshMugshotImageSizes();
        }

        private void MugshotScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel?.CurrentModNpcMugshots == null || !ViewModel.CurrentModNpcMugshots.Any()) return;

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                 double change = (e.Delta > 0 ? 1 : -1) * 1.0; 
                 ViewModel.ModsViewHasUserManuallyZoomed = true; 
                 ViewModel.ModsViewZoomLevel = Math.Max(10, Math.Min(500, ViewModel.ModsViewZoomLevel + change));
                 e.Handled = true; 
            }
        }

         private void MugshotItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
         {
             if (Keyboard.Modifiers == ModifierKeys.Control)
             {
                 if (sender is FrameworkElement element && element.DataContext is VM_ModNpcMugshot vm)
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
            if (ViewModel?.CurrentModNpcMugshots == null) return;
            
            var imagesToProcess = ViewModel.CurrentModNpcMugshots; // This is ObservableCollection<VM_ModNpcMugshot>
            if (!imagesToProcess.Any()) {
                return;
            }
            
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewModel == null || ViewModel.CurrentModNpcMugshots == null || !MugshotScrollViewer.IsLoaded) return;

                // For ModsView, all items in CurrentModNpcMugshots are considered "visible" for packing purposes
                // as there's no separate IsVisible flag being toggled like in NpcsView.
                var visibleImages = imagesToProcess
                                    .Where(img => img.OriginalDipDiagonal > 0) // Ensure valid original dimensions
                                    .ToList<IHasMugshotImage>(); 

                if (!visibleImages.Any())
                {
                    foreach (var img in imagesToProcess) { img.ImageWidth = 0; img.ImageHeight = 0; }
                    return;
                }

                if (ViewModel.ModsViewIsZoomLocked || ViewModel.ModsViewHasUserManuallyZoomed)
                {
                    double sumOfDiagonals = visibleImages.Sum(img => img.OriginalDipDiagonal);
                    double averageOriginalDipDiagonal = sumOfDiagonals / visibleImages.Count;
                    if (averageOriginalDipDiagonal <= 0) averageOriginalDipDiagonal = 100.0;

                    double userZoomFactor = ViewModel.ModsViewZoomLevel / 100.0;

                    foreach (var img in imagesToProcess) // Set all in the master list
                    {
                        if (img.OriginalDipDiagonal > 0) // Only scale if valid original dimensions
                        {
                            double individualScaleFactor = (averageOriginalDipDiagonal / img.OriginalDipDiagonal) * userZoomFactor;
                            img.ImageWidth = img.OriginalDipWidth * individualScaleFactor;
                            img.ImageHeight = img.OriginalDipHeight * individualScaleFactor;
                        }
                        else
                        {
                            img.ImageWidth = 0; 
                            img.ImageHeight = 0;
                        }
                    }
                }
                else
                {
                    var availableHeight = MugshotScrollViewer.ViewportHeight; 
                    var availableWidth = MugshotScrollViewer.ViewportWidth;   

                    if (availableHeight > 0 && availableWidth > 0)
                    {
                        var imagesForPacker = new ObservableCollection<IHasMugshotImage>(imagesToProcess.Cast<IHasMugshotImage>());
                        
                        double packerScaleFactor = ImagePacker.FitOriginalImagesToContainer(
                            imagesForPacker,
                            availableHeight,
                            availableWidth,
                            5 // xamlItemUniformMargin (from XAML Margin="5")
                        );

                        ViewModel.ModsViewZoomLevel = packerScaleFactor * 100.0;
                    }
                }
            }), DispatcherPriority.Loaded); 
        }

        private void ZoomPercentageTextBoxMods_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null || !(sender is TextBox textBox)) return;

            double currentValue = ViewModel.ModsViewZoomLevel;
            double change = (e.Delta > 0 ? 1 : -1) * 1.0; 
            
            ViewModel.ModsViewHasUserManuallyZoomed = true; 
            ViewModel.ModsViewZoomLevel = Math.Max(10, Math.Min(500, currentValue + change));
            
            // The WhenAnyValue binding from VM to Textbox will update the display.
            // The explicit TextChanged subscription will push it back to VM after throttle.
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