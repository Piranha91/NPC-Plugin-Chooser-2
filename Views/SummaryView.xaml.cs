// Views/SummaryView.xaml.cs
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class SummaryView : ReactiveUserControl<VM_Summary>
    {
        private const double _minZoomPercentage = 1.0;
        private const double _maxZoomPercentage = 1000.0;
        private const double _zoomStepPercentage = 2.5;

        public SummaryView()
        {
            InitializeComponent();

            // This manual ViewModel resolution is acceptable if you're not using a navigation framework.
            if (this.DataContext == null)
            {
                ViewModel = Locator.Current.GetService<VM_Summary>();
                DataContext = ViewModel;
            }

            // WhenActivated is the central place for all bindings and subscriptions
            // that should live and die with the View's visibility.
            this.WhenActivated(d =>
            {
                if (ViewModel == null) return;

                // --- BINDINGS ---
                // These bindings connect your XAML controls to the ViewModel properties.
                // Replace "SummaryItemsControl" and "ZoomPercentageTextBox" with the
                // actual x:Name of your controls in SummaryView.xaml.

                // Binds the collection of mugshots/items to the ItemsControl.
                this.OneWayBind(ViewModel,
                        vm => vm.DisplayedItems,
                        v => v.SummaryItemsControl.ItemsSource)
                    .DisposeWith(d);

                // Binds the zoom level to the TextBox for display and editing.
                this.Bind(ViewModel,
                        vm => vm.SummaryViewZoomLevel,
                        v => v.ZoomPercentageTextBox.Text)
                    .DisposeWith(d);

                // --- SUBSCRIPTIONS ---
                // Your existing subscription to refresh image sizes now lives here.
                ViewModel.RefreshImageSizesObservable
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => RefreshMugshotImageSizes())
                    .DisposeWith(d);
            });
        }

        private void MugshotScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ViewModel != null && !ViewModel.SummaryViewIsZoomLocked)
            {
                ViewModel.SummaryViewHasUserManuallyZoomed = false;
            }

            RefreshMugshotImageSizes();
        }

        private void MugshotScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null) return;
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                double change = (e.Delta > 0 ? 1 : -1) * _zoomStepPercentage;
                ViewModel.SummaryViewHasUserManuallyZoomed = true;
                ViewModel.SummaryViewZoomLevel = Math.Max(_minZoomPercentage,
                    Math.Min(_maxZoomPercentage, ViewModel.SummaryViewZoomLevel + change));
                e.Handled = true;
            }
        }

        private void ZoomPercentageTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null || !(sender is TextBox textBox)) return;

            double change = (e.Delta > 0 ? 1 : -1) * _zoomStepPercentage;
            ViewModel.SummaryViewHasUserManuallyZoomed = true;
            ViewModel.SummaryViewZoomLevel = Math.Max(_minZoomPercentage,
                Math.Min(_maxZoomPercentage, ViewModel.SummaryViewZoomLevel + change));

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            textBox.CaretIndex = textBox.Text.Length;
            textBox.SelectAll();
            e.Handled = true;
        }

        private void RefreshMugshotImageSizes()
        {
            if (ViewModel?.DisplayedItems == null || !ViewModel.IsGalleryView) return;

            var imagesToProcess = ViewModel.DisplayedItems.OfType<IHasMugshotImage>().ToList();
            if (!imagesToProcess.Any()) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewModel == null || !MugshotScrollViewer.IsLoaded) return;

                if (ViewModel.SummaryViewIsZoomLocked || ViewModel.SummaryViewHasUserManuallyZoomed)
                {
                    // Direct scaling logic remains the same
                    double averageDiagonal = imagesToProcess.Where(i => i.OriginalDipDiagonal > 0)
                        .Average(i => i.OriginalDipDiagonal);
                    if (averageDiagonal <= 0) averageDiagonal = 100.0;
                    double userZoomFactor = ViewModel.SummaryViewZoomLevel / 100.0;

                    foreach (var img in imagesToProcess)
                    {
                        double scale = (averageDiagonal / img.OriginalDipDiagonal) * userZoomFactor;
                        img.ImageWidth = img.OriginalDipWidth * scale;
                        img.ImageHeight = img.OriginalDipHeight * scale;
                    }
                }
                else
                {
                    // Packer scaling logic is updated
                    var packer = Locator.Current.GetService<ImagePacker>();
                    if (packer != null && MugshotScrollViewer.ViewportWidth > 0)
                    {
                        var items =
                            new System.Collections.ObjectModel.ObservableCollection<IHasMugshotImage>(imagesToProcess);

                        // THE FIX: Replace 9999 with the property from the ViewModel
                        double scaleFactor = packer.FitOriginalImagesToContainer(
                            items,
                            MugshotScrollViewer.ViewportHeight,
                            MugshotScrollViewer.ViewportWidth,
                            5,
                            true,
                            ViewModel.MaxMugshotsToFit); // <-- Changed here

                        ViewModel.SummaryViewZoomLevel = scaleFactor * 100.0;
                    }
                }
            }), DispatcherPriority.Background);
        }
    }
}