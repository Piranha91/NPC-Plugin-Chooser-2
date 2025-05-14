// NpcsView.xaml.cs (Revised RefreshImageSizes)
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
                
                this.Bind(ViewModel, vm => vm.NpcsViewZoomLevel, v => v.ZoomPercentageTextBox.Text,
                    vmToViewConverter: val => val.ToString("F0"), 
                    viewToVmConverter: text => {
                        if (ViewModel != null) ViewModel.NpcsViewHasUserManuallyZoomed = true;
                        return double.TryParse(text, out double result) ? Math.Max(10, Math.Min(500, result)) : 100.0;
                    }
                ).DisposeWith(d);
                
                this.BindCommand(ViewModel, vm => vm.ZoomInNpcsCommand, v => v.ZoomInButton).DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.ZoomOutNpcsCommand, v => v.ZoomOutButton).DisposeWith(d);
                this.Bind(ViewModel, vm => vm.NpcsViewIsZoomLocked, v => v.LockZoomCheckBox.IsChecked).DisposeWith(d);


                ViewModel.RefreshImageSizesObservable // This subject is signaled by VM when things like SelectedNpc, ShowHidden, ZoomLevel, or LockState change
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => RefreshImageSizes())
                    .DisposeWith(d);
                
                ViewModel.ScrollToNpcInteraction.RegisterHandler(interaction =>
                {
                    var npcToScrollTo = interaction.Input;
                    if (npcToScrollTo != null)
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                if (NpcListBox.Items.Contains(npcToScrollTo))
                                {
                                    NpcListBox.ScrollIntoView(npcToScrollTo);
                                }
                                interaction.SetOutput(Unit.Default); 
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"NpcsView Interaction Handler: Error during ScrollIntoView: {ex.Message}");
                                interaction.SetOutput(Unit.Default);
                            }
                        });
                    }
                    else { interaction.SetOutput(Unit.Default); }
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
                    double change = (e.Delta > 0 ? 1 : -1) * 10.0; 
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
                            imagesForPacker, // This collection will have its ImageWidth/Height updated by the packer
                            availableHeight,
                            availableWidth,
                            5, 5 
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
            double change = (e.Delta > 0 ? 1 : -1) * 10.0; 
            
            ViewModel.NpcsViewHasUserManuallyZoomed = true; 
            ViewModel.NpcsViewZoomLevel = Math.Max(10, Math.Min(500, currentValue + change));
            
            textBox.CaretIndex = textBox.Text.Length;
            textBox.SelectAll();
            e.Handled = true;
        }
    }
}