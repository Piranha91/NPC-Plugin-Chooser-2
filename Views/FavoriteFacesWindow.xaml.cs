using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class FavoriteFacesWindow : ReactiveWindow<VM_FavoriteFaces>
    {
        private const double _minZoomPercentage = 1.0;
        private const double _maxZoomPercentage = 1000.0;
        private const double _zoomStepPercentage = 2.5;

        public FavoriteFacesWindow()
        {
            InitializeComponent();
            this.WhenActivated(d =>
            {
                if (ViewModel == null) return;
                
                // Close window when requested by ViewModel
                ViewModel.RequestClose += this.Close;
                Disposable.Create(() => ViewModel.RequestClose -= this.Close).DisposeWith(d);

                ViewModel.RefreshImageSizesObservable
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => RefreshMugshotImageSizes())
                    .DisposeWith(d);
            });
        }
        
        private void Mugshot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel != null && sender is FrameworkElement element && element.DataContext is VM_SummaryMugshot selectedMugshot)
            {
                ViewModel.SelectedMugshot = selectedMugshot;

                // Update the IsSelected property on all view models.
                // The UI will react to this change automatically.
                foreach (var item in ViewModel.FavoriteMugshots)
                {
                    item.IsSelected = (item == selectedMugshot);
                }
            }
        }
        
        private void MugshotScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ViewModel != null && !ViewModel.IsZoomLocked) ViewModel.HasUserManuallyZoomed = false;
            RefreshMugshotImageSizes();
        }

        private void MugshotScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null || Keyboard.Modifiers != ModifierKeys.Control) return;
            
            double change = (e.Delta > 0 ? 1 : -1) * _zoomStepPercentage;
            ViewModel.HasUserManuallyZoomed = true;
            ViewModel.ZoomLevel = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, ViewModel.ZoomLevel + change));
            e.Handled = true;
        }

        private void ZoomPercentageTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null || !(sender is TextBox textBox)) return;

            double change = (e.Delta > 0 ? 1 : -1) * _zoomStepPercentage;
            ViewModel.HasUserManuallyZoomed = true;
            ViewModel.ZoomLevel = Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, ViewModel.ZoomLevel + change));

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            textBox.CaretIndex = textBox.Text.Length;
            textBox.SelectAll();
            e.Handled = true;
        }

        private void RefreshMugshotImageSizes()
        {
            // Use FilteredFavoriteMugshots instead of FavoriteMugshots for sizing calculations
            if (ViewModel?.FilteredFavoriteMugshots == null || !ViewModel.FilteredFavoriteMugshots.Any()) return;

            var imagesToProcess = ViewModel.FilteredFavoriteMugshots.OfType<IHasMugshotImage>().ToList();
            if (!imagesToProcess.Any()) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewModel == null || !MugshotScrollViewer.IsLoaded || MugshotScrollViewer.ViewportWidth <= 0) return;

                if (ViewModel.IsZoomLocked || ViewModel.HasUserManuallyZoomed)
                {
                    double averageDiagonal = imagesToProcess.Where(i => i.OriginalDipDiagonal > 0).Average(i => i.OriginalDipDiagonal);
                    if (averageDiagonal <= 0) averageDiagonal = 100;
                    double userZoomFactor = ViewModel.ZoomLevel / 100.0;
                    
                    foreach (var img in imagesToProcess)
                    {
                        double scale = (averageDiagonal / img.OriginalDipDiagonal) * userZoomFactor;
                        img.ImageWidth = img.OriginalDipWidth * scale;
                        img.ImageHeight = img.OriginalDipHeight * scale;
                    }
                }
                else
                {
                    var packer = Locator.Current.GetService<ImagePacker>();
                    if (packer != null)
                    {
                        var items = new ObservableCollection<IHasMugshotImage>(imagesToProcess);
                        double scaleFactor = packer.FitOriginalImagesToContainer(items, MugshotScrollViewer.ViewportHeight, MugshotScrollViewer.ViewportWidth, 5, true, ViewModel.MaxMugshotsToFit);
                        ViewModel.ZoomLevel = scaleFactor * 100.0;
                    }
                }
            }), DispatcherPriority.Background);
        }
    }
}
