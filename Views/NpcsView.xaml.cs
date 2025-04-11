// NpcsView.xaml.cs (Updated with Disposal Logic)
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using System.Windows;
using System.Reactive.Disposables; // Required for CompositeDisposable and DisposeWith
using System.Windows.Input;        // Required for MouseWheelEventArgs
using Splat;
using System.Reactive.Linq;
using System.Windows.Controls; // Required for .Subscribe()

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class NpcsView : ReactiveUserControl<VM_NpcSelectionBar>
    {
        // 1. Field to hold disposables tied to the View's activation lifecycle
        private readonly CompositeDisposable _viewBindings = new CompositeDisposable();
        private const double ImageSizeStepFactor = 15.0; // Pixel change per standard wheel tick

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
            if (ViewModel != null & ViewModel?.CurrentNpcAppearanceMods != null)
            {
                try
                {
                    if (Keyboard.Modifiers == ModifierKeys.Control)
                    {
                        // Ctrl + Scroll → Resize images
                        if (ViewModel?.CurrentNpcAppearanceMods != null)
                        {
                            double change = (e.Delta / 120.0) * ImageSizeStepFactor;

                            foreach (var vm in ViewModel.CurrentNpcAppearanceMods)
                            {
                                vm.ImageHeight += change;
                                vm.ImageWidth += change;
                            }

                            // Prevent normal scroll behavior
                            e.Handled = true;
                        }
                    }
                    // Else: allow normal scroll behavior
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
            if (ViewModel != null && ViewModel.CurrentNpcAppearanceMods != null)
            {
                //ImagePacker.MaximizeStartingImageSizes(ViewModel.CurrentNpcAppearanceMods, e.NewSize.Height, e.NewSize.Width, 5, 5);
            }
        }


        // Optional: Explicitly dispose if the view might be reused without full destruction/recreation
        // Usually not needed if WhenActivated handles disposal correctly on deactivation.
        // protected override void OnClosed(EventArgs e) // Or similar lifecycle method if applicable
        // {
        //     _viewBindings.Dispose();
        //     base.OnClosed(e);
        // }
        
        
        private void NpcListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewModel != null && ViewModel.CurrentNpcAppearanceMods != null)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var availableHeight = ImageDisplayScrollViewer.ViewportHeight;
                    var availableWidth = AppearanceModsItemsControl.ActualWidth;

                    // Only scale if layout has been finalized
                    if (availableHeight > 0 && availableWidth > 0)
                    {
                        ImagePacker.MaximizeStartingImageSizes(
                            ViewModel.CurrentNpcAppearanceMods,
                            availableHeight,
                            availableWidth,
                            5, 5);
                    }
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void Image_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (sender is Image image && image.DataContext is VM_AppearanceMod vm)
                {
                    vm.ToggleFullScreenCommand.Execute().Subscribe(); // ReactiveUI-friendly execution
                    e.Handled = true;
                }
            }
            // else: allow normal context menu behavior
        }
    }
}