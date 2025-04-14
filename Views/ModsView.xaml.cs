// Views/ModsView.xaml.cs
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat; // For Locator
using System;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Windows.Controls; // For ScrollViewer, ItemsControl etc.
using System.Windows.Input;  // For MouseEventArgs, Keyboard
using System.Windows;      // For SizeChangedEventArgs
using System.Reactive.Linq; // For Observable throttling/scheduling

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Interaction logic for ModsView.xaml
    /// </summary>
    public partial class ModsView : ReactiveUserControl<VM_Mods>
    {
        private const double MugshotSizeStepFactor = 15.0; // Pixel change per standard wheel tick

        public ModsView()
        {
            InitializeComponent();

            // Attempt to resolve the ViewModel if DataContext is not already set by ViewLocator
            if (this.DataContext == null)
            {
                try
                {
                    ViewModel = Locator.Current.GetService<VM_Mods>();
                    // Setting DataContext explicitly might interfere with ReactiveUI's View resolution
                    // Only do this if ViewLocator isn't working as expected.
                    DataContext = this.ViewModel;
                }
                catch (Exception ex)
                {
                    // Log or handle the error where the VM couldn't be resolved
                    System.Diagnostics.Debug.WriteLine($"Error resolving VM_Mods: {ex.Message}");
                    // The view might not function correctly without its ViewModel
                }
            }


            this.WhenActivated(disposables =>
            {
                // ViewModel should be resolved by now if registered and View implements IViewFor
                if (ViewModel == null) return;

                // Subscribe to the mugshot refresh observable from the VM
                ViewModel.RefreshMugshotSizesObservable
                    .ObserveOn(RxApp.MainThreadScheduler) // Ensure UI updates happen on the correct thread
                    .Subscribe(_ => {
                        if (!ViewModel.HasUsedMugshotZoom) // Only auto-size if user hasn't zoomed
                        {
                             RefreshMugshotImageSizes();
                        }
                     })
                    .DisposeWith(disposables);

                // Handle clearing zoom state when the selected mod changes
                this.WhenAnyValue(x => x.ViewModel.SelectedModForMugshots)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(selectedMod => {
                        if (ViewModel != null) ViewModel.HasUsedMugshotZoom = false; // Reset zoom state
                        // Optionally trigger RefreshMugshotImageSizes here if needed immediately after selection change AND items are present
                        // However, the ShowMugshotsCommand likely already triggers the refresh subject.
                     })
                    .DisposeWith(disposables);
            });
        }

        // --- Right Panel Event Handlers ---

        private void MugshotScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Refresh sizes when the container size changes, but only if not manually zoomed
             if (ViewModel != null && !ViewModel.HasUsedMugshotZoom)
             {
                  RefreshMugshotImageSizes();
             }
        }

        private void MugshotScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel?.CurrentModNpcMugshots != null && Keyboard.Modifiers == ModifierKeys.Control)
            {
                 double change = (e.Delta / 120.0) * MugshotSizeStepFactor;

                 // Apply size change to all mugshot VMs
                 foreach (var vm in ViewModel.CurrentModNpcMugshots)
                 {
                     // Basic additive scaling; consider multiplicative or clamping if needed
                     vm.ImageHeight = Math.Max(20, vm.ImageHeight + change); // Min height 20
                     vm.ImageWidth = Math.Max(20, vm.ImageWidth + change);   // Min width 20
                 }

                 ViewModel.HasUsedMugshotZoom = true; // Mark that manual zoom has occurred
                 e.Handled = true; // Prevent default scroll behavior
            }
             // else: Allow normal scrolling if Ctrl is not pressed
        }

         private void MugshotItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
         {
             // Handle Ctrl+Right Click for Fullscreen
             if (Keyboard.Modifiers == ModifierKeys.Control)
             {
                 // The sender should be the Border containing the item
                 if (sender is FrameworkElement element && element.DataContext is VM_ModNpcMugshot vm)
                 {
                      // Check if the command can execute and then execute it
                      if (vm.ToggleFullScreenCommand.CanExecute.FirstAsync().Wait()) // Use Wait() for simplicity here, or async/await if preferred
                      {
                          vm.ToggleFullScreenCommand.Execute().Subscribe().Dispose(); // Execute and subscribe (and dispose subscription)
                      }
                      e.Handled = true; // Prevent the context menu from opening
                 }
             }
              // else: Allow normal right-click behavior (opening the context menu)
         }

        // Helper to refresh mugshot sizes using ImagePacker
        private void RefreshMugshotImageSizes()
        {
            // Ensure VM and collection are valid, and the ScrollViewer is loaded
             if (ViewModel?.CurrentModNpcMugshots != null && MugshotScrollViewer.IsLoaded && ViewModel.CurrentModNpcMugshots.Any())
             {
                  // Use Dispatcher to ensure layout pass is complete before getting ActualWidth/Height
                  Dispatcher.BeginInvoke(new Action(() =>
                  {
                      var availableHeight = MugshotScrollViewer.ViewportHeight; // Use ViewportHeight for visible area
                      var availableWidth = MugshotScrollViewer.ViewportWidth;   // Use ViewportWidth for visible area

                       // Avoid running if dimensions are invalid (e.g., during initial load)
                      if (availableHeight > 0 && availableWidth > 0)
                      {
                          var images = new ObservableCollection<IHasMugshotImage>(ViewModel.CurrentModNpcMugshots);
                          ImagePacker.MaximizeStartingImageSizes(
                              images, // Pass the correct collection
                              availableHeight,
                              availableWidth,
                              5, // verticalMargin (match XAML)
                              5  // horizontalMargin (match XAML)
                          );
                      }
                  }), System.Windows.Threading.DispatcherPriority.Loaded); // Lower priority to wait for layout
             }
        }
    }
}