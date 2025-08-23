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

            // **THE FIX:** Manually resolve and set the DataContext in the constructor
            // if it hasn't been set by the framework yet. This ensures bindings
            // have a source when the view is first loaded.
            if (this.DataContext == null)
            {
                ViewModel = Locator.Current.GetService<VM_Summary>();
                DataContext = ViewModel;
            }
    
            this.WhenActivated(d =>
            {
                // By the time WhenActivated runs, the ViewModel should already be set.
                // We just double-check before subscribing to its observables.
                if (ViewModel == null) return;

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
                ViewModel.SummaryViewZoomLevel = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, ViewModel.SummaryViewZoomLevel + change));
                e.Handled = true;
            }
        }
        
        private void ZoomPercentageTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null || !(sender is TextBox textBox)) return;
            
            double change = (e.Delta > 0 ? 1 : -1) * _zoomStepPercentage;
            ViewModel.SummaryViewHasUserManuallyZoomed = true;
            ViewModel.SummaryViewZoomLevel = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, ViewModel.SummaryViewZoomLevel + change));
            
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
                    // Direct scaling logic
                    double averageDiagonal = imagesToProcess.Where(i => i.OriginalDipDiagonal > 0).Average(i => i.OriginalDipDiagonal);
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
                    // Packer scaling logic
                    var packer = Locator.Current.GetService<ImagePacker>();
                    if (packer != null && MugshotScrollViewer.ViewportWidth > 0)
                    {
                        var items = new ObservableCollection<IHasMugshotImage>(imagesToProcess);
                        double scaleFactor = packer.FitOriginalImagesToContainer(items, MugshotScrollViewer.ViewportHeight, MugshotScrollViewer.ViewportWidth, 5, true, 9999);
                        ViewModel.SummaryViewZoomLevel = scaleFactor * 100.0;
                    }
                }
            }), DispatcherPriority.Background);
        }
    }
}