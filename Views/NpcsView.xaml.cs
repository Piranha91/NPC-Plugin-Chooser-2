// NpcsView.xaml.cs (Updated with Disposal Logic)
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using System.Windows;
using System.Reactive.Disposables; // Required for CompositeDisposable and DisposeWith
using System.Windows.Input;        // Required for MouseWheelEventArgs
using Splat;
using System.Reactive.Linq;        // Required for .Subscribe()

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class NpcsView : ReactiveUserControl<VM_NpcSelectionBar>
    {
        // 1. Field to hold disposables tied to the View's activation lifecycle
        private readonly CompositeDisposable _viewBindings = new CompositeDisposable();

        public NpcsView()
        {
            InitializeComponent();

            // Manual DataContext logic (keep if needed)
            if (this.DataContext == null) {
                try {
                    var vm = Locator.Current.GetService<VM_NpcSelectionBar>();
                    if (vm != null) {
                        System.Diagnostics.Debug.WriteLine("DEBUG: Manually setting DataContext/ViewModel in NpcsView constructor!");
                        this.DataContext = vm; this.ViewModel = vm;
                    } else {
                        System.Diagnostics.Debug.WriteLine("DEBUG: Failed to resolve VM_NpcSelectionBar manually in constructor (GetService returned null).");
                    }
                } catch (Exception ex) {
                    System.Diagnostics.Debug.WriteLine($"DEBUG: Error manually resolving/setting DataContext in NpcsView: {ex.Message}");
                }
            }

            this.WhenActivated(d => // 'd' is the CompositeDisposable for this activation
            {
                // 2. Ensure the view's disposable field is disposed when the view deactivates.
                d.DisposeWith(_viewBindings); // Add the WhenActivated disposable itself to our field

                // Bindings (from previous steps)
                this.OneWayBind(ViewModel, vm => vm.FilteredNpcs, v => v.NpcListBox.ItemsSource)
                    .DisposeWith(d); // Dispose these specific bindings with the activation disposable
                this.Bind(ViewModel, vm => vm.SelectedNpc, v => v.NpcListBox.SelectedItem)
                    .DisposeWith(d);
                this.OneWayBind(ViewModel, vm => vm.CurrentNpcAppearanceMods, v => v.AppearanceModsItemsControl.ItemsSource)
                    .DisposeWith(d);

                // Any other view-specific subscriptions or bindings set up here
                // should also use .DisposeWith(d)
            });
        }

        // Event Handler for Scroll Wheel (Using View's Disposables)
        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Check if ViewModel and command exist
            if (ViewModel?.ChangeImageSizeCommand != null)
            {
                try
                {
                    ViewModel.ChangeImageSizeCommand.Execute(e.Delta)
                        .Subscribe( // Subscribe to handle potential errors from the command execution
                            _ => { /* Optional: Action on successful completion (usually not needed for Execute) */ },
                            ex => { // Action on error during command execution
                                System.Diagnostics.Debug.WriteLine($"Error executing ChangeImageSizeCommand: {ex.Message}");
                            })
                        // 3. Add this subscription to the view's disposable collection.
                        // It will be disposed automatically when the view is deactivated.
                        .DisposeWith(_viewBindings);

                    // Mark the event as handled to prevent default ScrollViewer scrolling.
                    e.Handled = true;
                }
                catch (Exception ex)
                {
                    // Catch synchronous errors during Execute dispatch (less common)
                    System.Diagnostics.Debug.WriteLine($"Synchronous error calling ChangeImageSizeCommand.Execute: {ex.Message}");
                }
            }
        }
        
        private void ImageDisplayScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ViewModel != null)
            {
                double availableWidth = e.NewSize.Width;
                double availableHeight = e.NewSize.Height;
                int imageCount = ViewModel.CurrentNpcAppearanceMods?.Count ?? 0;

                if (imageCount > 0)
                {
                    double spacing = 10; // Left+Right margin in your Border
                    double paddingPerImage = spacing;

                    // Try a square grid
                    int columns = (int)Math.Ceiling(Math.Sqrt(imageCount * availableWidth / availableHeight));
                    int rows = (int)Math.Ceiling((double)imageCount / columns);

                    double maxWidthPerImage = (availableWidth - (columns * paddingPerImage)) / columns;
                    double maxHeightPerImage = (availableHeight - (rows * paddingPerImage)) / rows;
                    double finalSize = Math.Min(maxWidthPerImage, maxHeightPerImage);

                    // Clamp to min/max defined in your ViewModel
                    const double MinImageSize = 40.0;
                    const double MaxImageSize = 600.0;
                    finalSize = Math.Clamp(finalSize, MinImageSize, MaxImageSize);

                    ViewModel.ImageDisplaySize = finalSize;
                }
            }
        }


        // Optional: Explicitly dispose if the view might be reused without full destruction/recreation
        // Usually not needed if WhenActivated handles disposal correctly on deactivation.
        // protected override void OnClosed(EventArgs e) // Or similar lifecycle method if applicable
        // {
        //     _viewBindings.Dispose();
        //     base.OnClosed(e);
        // }
    }
}