// [MultiImageDisplayView.xaml.cs] - Full Code
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using System;
using System.Collections.ObjectModel; // Though not directly used here, often useful for collections
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
//using System.Threading.Tasks; // Not strictly needed in this version of the file
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class MultiImageDisplayView : ReactiveWindow<VM_MultiImageDisplay>
    {
        // _viewBindings can be used for subscriptions not tied to WhenActivated,
        // but for this view, most things will be in WhenActivated.
        private readonly CompositeDisposable _viewBindings = new CompositeDisposable();

        // Constants for zoom, should ideally match or be driven by VM if they can change
        private const double MinZoomPercentage = 10.0; 
        private const double MaxZoomPercentage = 500.0; 
        private const double ZoomStepPercentage = 5.0;  

        public MultiImageDisplayView()
        {
            InitializeComponent();

            this.WhenActivated(d => // 'd' is the CompositeDisposable for this activation
            {
                // Dispose of any subscriptions made during this activation when the view is deactivated.
                // This is the primary way to manage subscription lifecycles in WhenActivated.
                
                // Corrected KeyDown subscription for Escape key
                Observable.FromEventPattern<KeyEventHandler, KeyEventArgs>(
                    handler => this.KeyDown += handler,
                    handler => this.KeyDown -= handler)
                    .Where(ep => ep.EventArgs.Key == Key.Escape)
                    .ObserveOn(RxApp.MainThreadScheduler) // Ensure Close is called on UI thread
                    .Subscribe(_ => this.Close())
                    .DisposeWith(d); // Dispose with the WhenActivated disposable

                if (ViewModel == null)
                {
                    Debug.WriteLine("MultiImageDisplayView.WhenActivated: ViewModel is null. Skipping bindings.");
                    return;
                }

                // Bindings for main content and zoom controls
                this.OneWayBind(ViewModel, vm => vm.ImagesToDisplay, v => v.ImageItemsControl.ItemsSource).DisposeWith(d);

                this.WhenAnyValue(x => x.ViewModel.ZoomLevel)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Select(val => val.ToString("F0", CultureInfo.InvariantCulture)) // Using F0 for integer percentage display
                    .BindTo(this, v => v.ZoomPercentageTextBox.Text)
                    .DisposeWith(d);

                Observable.FromEventPattern<TextChangedEventArgs>(ZoomPercentageTextBox, nameof(ZoomPercentageTextBox.TextChanged))
                    .Select(ep => ((TextBox)ep.Sender).Text)
                    .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(text =>
                    {
                        if (ViewModel != null) // Double check VM hasn't become null
                        {
                            if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
                            {
                                ViewModel.HasUserManuallyZoomed = true;
                                double clampedResult = Math.Max(MinZoomPercentage, Math.Min(MaxZoomPercentage, result));
                                if (Math.Abs(ViewModel.ZoomLevel - clampedResult) > 0.001) // Avoid feedback loop if already clamped
                                {
                                    ViewModel.ZoomLevel = clampedResult;
                                }
                            }
                            else if (!string.IsNullOrWhiteSpace(text)) // If parse fails but text is not empty, revert
                            {
                                ZoomPercentageTextBox.Text = ViewModel.ZoomLevel.ToString("F0", CultureInfo.InvariantCulture);
                            }
                        }
                    })
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.ZoomInCommand, v => v.ZoomInButton).DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.ZoomOutCommand, v => v.ZoomOutButton).DisposeWith(d);
                this.Bind(ViewModel, vm => vm.IsZoomLocked, v => v.LockZoomCheckBox.IsChecked).DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.ResetZoomCommand, v => v.ResetZoomButton).DisposeWith(d);

                // Subscribe to the VM's signal to refresh image sizes
                ViewModel.RefreshImageSizesObservable
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => RefreshImageSizes())
                    .DisposeWith(d);
                
                // Initial call to size images once the view is loaded and VM is set.
                // Dispatcher ensures that ActualWidth/Height are available.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ViewModel != null) // Check ViewModel again inside BeginInvoke
                    {
                        RefreshImageSizes();
                    }
                }), DispatcherPriority.Loaded);
            });
        }

        private void CloseOnClick(object sender, MouseButtonEventArgs e)
        {
            // Close only if the click is on the Grid background itself, not on its children (like the control panel)
            if (e.Source == sender) // 'sender' will be the Grid in this case
            {
                this.Close();
            }
        }
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }


        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null) return;

            // Handle Ctrl+Scroll for zooming only if the source is the main ScrollViewer
            if (sender == ImageDisplayScrollViewer && Keyboard.Modifiers == ModifierKeys.Control)
            {
                double change = (e.Delta > 0 ? 1 : -1) * ZoomStepPercentage;
                ViewModel.HasUserManuallyZoomed = true; // User interaction
                ViewModel.ZoomLevel = Math.Max(MinZoomPercentage, Math.Min(MaxZoomPercentage, ViewModel.ZoomLevel + change));
                e.Handled = true; // Prevent the ScrollViewer from scrolling
            }
            // If Ctrl is not pressed, or if the event source is something else (like the ZoomPercentageTextBox),
            // let the event bubble or be handled by other handlers.
        }

        private void ImageDisplayScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ViewModel != null && !ViewModel.IsZoomLocked)
            {
                ViewModel.HasUserManuallyZoomed = false; // Allow packer to take over if unlocked
            }
            // Refresh sizes regardless, as available space changed
            RefreshImageSizes();
        }
        
        private void ZoomPercentageTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null || !(sender is TextBox textBox)) return;
            
            double currentValue = ViewModel.ZoomLevel;
            double change = (e.Delta > 0 ? 1 : -1) * ZoomStepPercentage;
            
            ViewModel.HasUserManuallyZoomed = true; 
            ViewModel.ZoomLevel = Math.Max(MinZoomPercentage, Math.Min(MaxZoomPercentage, currentValue + change));
            
            // The reactive binding from VM.ZoomLevel to TextBox.Text should update the display.
            // If you need to force an update from the textbox to the VM (e.g. if TwoWay binding wasn't immediate):
            // var binding = textBox.GetBindingExpression(TextBox.TextProperty);
            // binding?.UpdateSource(); 

            textBox.CaretIndex = textBox.Text.Length; // Move caret to end
            textBox.SelectAll(); // Optionally select all text
            e.Handled = true; // Prevent the parent ScrollViewer from handling this wheel event
        }

        private void RefreshImageSizes()
        {
            if (ViewModel?.ImagesToDisplay == null || !ViewModel.ImagesToDisplay.Any())
            {
                // If there's nothing to display, clear any existing sizes
                if (ViewModel?.ImagesToDisplay != null)
                {
                    foreach (var img in ViewModel.ImagesToDisplay) { img.ImageWidth = 0; img.ImageHeight = 0; }
                }
                return;
            }

            // Defer until layout is complete and valid dimensions are available
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Re-check conditions as state might have changed during BeginInvoke
                if (ViewModel == null || ViewModel.ImagesToDisplay == null || !ViewModel.ImagesToDisplay.Any() || 
                    !ImageDisplayScrollViewer.IsLoaded || 
                    ImageDisplayScrollViewer.ViewportWidth <= 0 || ImageDisplayScrollViewer.ViewportHeight <= 0)
                {
                    // If any critical condition isn't met, clear sizes or do nothing and wait for next trigger
                    if (ViewModel?.ImagesToDisplay != null)
                    {
                       foreach (var img in ViewModel.ImagesToDisplay) { img.ImageWidth = 0; img.ImageHeight = 0; }
                    }
                    return;
                }

                var visibleImages = ViewModel.ImagesToDisplay
                                    .Where(img => img.IsVisible && img.OriginalDipDiagonal > 0 && !string.IsNullOrEmpty(img.ImagePath))
                                    .ToList(); 

                if (!visibleImages.Any())
                {
                    foreach (var img in ViewModel.ImagesToDisplay) { img.ImageWidth = 0; img.ImageHeight = 0; }
                    return;
                }
                
                double availableWidth = ImageDisplayScrollViewer.ViewportWidth;
                double availableHeight = ImageDisplayScrollViewer.ViewportHeight;

                if (ViewModel.IsZoomLocked || ViewModel.HasUserManuallyZoomed)
                {
                    // Direct scaling based on user's zoom level
                    double sumOfDiagonals = visibleImages.Sum(img => img.OriginalDipDiagonal);
                    if (sumOfDiagonals <= 0) { 
                        foreach (var img in ViewModel.ImagesToDisplay) { img.ImageWidth = 50; img.ImageHeight = 50; } 
                        return;
                    }
                    double averageOriginalDipDiagonal = sumOfDiagonals / visibleImages.Count;
                    if (averageOriginalDipDiagonal <= 0) averageOriginalDipDiagonal = 100.0; // Fallback

                    double userZoomFactor = ViewModel.ZoomLevel / 100.0;

                    foreach (var img in ViewModel.ImagesToDisplay) 
                    {
                        if (img.IsVisible && img.OriginalDipDiagonal > 0 && !string.IsNullOrEmpty(img.ImagePath))
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
                else // Unlocked and not manually zoomed: Let packer fit
                {
                    // Ensure there are valid dimensions to pack into
                    if (availableHeight > 0 && availableWidth > 0)
                    {
                        // ImagePacker.FitOriginalImagesToContainer expects an ObservableCollection.
                        // ViewModel.ImagesToDisplay is already of this type.
                        double packerScaleFactor = ImagePacker.FitOriginalImagesToContainer(
                            ViewModel.ImagesToDisplay, 
                            availableHeight,
                            availableWidth,
                            5 // xamlItemUniformMargin (from XAML Margin="5" on the Border in ItemTemplate)
                        );
                        
                        ViewModel.ZoomLevel = packerScaleFactor * 100.0;
                    }
                    else
                    {
                        // Fallback if available dimensions are invalid (e.g. window minimized or not yet rendered)
                        foreach (var img in ViewModel.ImagesToDisplay) { img.ImageWidth = 50; img.ImageHeight = 50; } 
                    }
                }
            }), DispatcherPriority.Render); // Using Render or Loaded to ensure sizes are available
        }
        
        protected override void OnClosed(EventArgs e)
        {
            // Ensure ViewModel is disposed to release its subscriptions
            if (this.ViewModel is IDisposable disposableVM)
            {
                disposableVM.Dispose();
            }
            // Dispose view-specific subscriptions
            _viewBindings.Dispose();
            base.OnClosed(e);
            Debug.WriteLine("MultiImageDisplayView Closed and resources potentially released.");
        }
    }
}