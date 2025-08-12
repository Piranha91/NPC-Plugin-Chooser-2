// NpcsView.xaml.cs (Full Code)
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks; // Added this
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data; // For PlacementMode
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
        private const double _minZoomPercentage = 1.0;
        private const double _maxZoomPercentage = 1000.0;
        private const double _zoomStepPercentage = 2.5; // For +/- buttons and scroll wheel
        private static int _mugshotBorderThickness = 3;
        private static int _mugshotMargin = 2;
        private static int MugshotMarginTotal => _mugshotBorderThickness + _mugshotMargin;
        public Thickness MugshotBorderThickness { get; } = new Thickness(_mugshotBorderThickness); // 3 on all sides
        public Thickness MugshotMargin { get; } = new Thickness(_mugshotMargin); // uniform 2-px margin

        public NpcsView()
        {
            InitializeComponent();

            // Set the DataContext for the proxy resource
            if (this.Resources["DataContextProxy"] is FrameworkElement proxy)
            {
                // Bind the proxy's DataContext to the UserControl's DataContext.
                // This binding will ensure that if the UserControl's DataContext changes,
                // the proxy's DataContext also changes.
                var binding = new Binding("DataContext") { Source = this };
                BindingOperations.SetBinding(proxy, FrameworkElement.DataContextProperty, binding);
                // For immediate check during debugging:
                // proxy.DataContext = this.DataContext; // This would also work if DC doesn't change after this point
            }
            else
            {
                Debug.WriteLine("!!!!!!!!!!!!!!!! CRITICAL: DataContextProxy RESOURCE NOT FOUND !!!!!!!!!!!!!!!!");
            }

            this.Loaded += (s, e) =>
            {
                if (this.DataContext is VM_NpcSelectionBar vm)
                {
                    Debug.WriteLine("NpcsView DataContext is VM_NpcSelectionBar - OK");
                    if (vm.HideAllSelectedCommand == null)
                    {
                        Debug.WriteLine("!!!!!!!!!!!!!!!! VM.HideAllSelectedCommand IS NULL !!!!!!!!!!!!!!!!");
                    }
                    else
                    {
                        Debug.WriteLine("VM.HideAllSelectedCommand is NOT NULL - OK");
                    }
                }
                else
                {
                    Debug.WriteLine(
                        $"!!!!!!!!!!!!!!!! NpcsView DataContext IS UNEXPECTED: {this.DataContext?.GetType().Name ?? "null"} !!!!!!!!!!!!!!!!");
                }

                if (this.Resources["DataContextProxy"] is FrameworkElement proxy)
                {
                    if (proxy.DataContext is VM_NpcSelectionBar proxyVm)
                    {
                        Debug.WriteLine("Proxy DataContext is VM_NpcSelectionBar - OK");
                    }
                    else
                    {
                        Debug.WriteLine(
                            $"!!!!!!!!!!!!!!!! Proxy DataContext IS UNEXPECTED: {proxy.DataContext?.GetType().Name ?? "null"} !!!!!!!!!!!!!!!!");
                    }
                }
                else
                {
                    Debug.WriteLine(
                        "!!!!!!!!!!!!!!!! DataContextProxy RESOURCE NOT FOUND OR NOT FRAMEWORKELEMENT !!!!!!!!!!!!!!!!");
                }
            };

            if (this.DataContext == null)
            {
                try
                {
                    var vm = Locator.Current.GetService<VM_NpcSelectionBar>();
                    if (vm != null)
                    {
                        this.DataContext = vm;
                        this.ViewModel = vm;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DEBUG: Error manually resolving/setting DataContext in NpcsView: {ex.Message}");
                }
            }

            this.WhenActivated((CompositeDisposable d) => // Explicitly type 'disposables'
            {
                // d.DisposeWith(_viewBindings); // This was potentially causing issues if _viewBindings was used elsewhere.
                // 'd' itself is the disposable for WhenActivated subscriptions.

                if (ViewModel == null) return;

                this.OneWayBind(ViewModel, vm => vm.FilteredNpcs, v => v.NpcListBox.ItemsSource).DisposeWith(d);
                this.Bind(ViewModel, vm => vm.SelectedNpc, v => v.NpcListBox.SelectedItem).DisposeWith(d);
                this.OneWayBind(ViewModel, vm => vm.CurrentNpcAppearanceMods,
                    v => v.AppearanceModsItemsControl.ItemsSource).DisposeWith(d);

                this.WhenAnyValue(x => x.ViewModel.NpcsViewZoomLevel)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Select(val => val.ToString("F2", CultureInfo.InvariantCulture))
                    .BindTo(this, v => v.ZoomPercentageTextBox.Text)
                    .DisposeWith(d);

                Observable.FromEventPattern<TextChangedEventArgs>(ZoomPercentageTextBox,
                        nameof(ZoomPercentageTextBox.TextChanged))
                    .Select(ep => ((TextBox)ep.Sender).Text)
                    .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(text =>
                    {
                        Debug.WriteLine($"NpcsView: ZoomPercentageTextBox TextChanged to '{text}' (throttled)");
                        if (ViewModel != null)
                        {
                            if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture,
                                    out double result))
                            {
                                ViewModel.NpcsViewHasUserManuallyZoomed = true;
                                double clampedResult = Math.Max(_minZoomPercentage,
                                    Math.Min(_maxZoomPercentage, result));
                                if (Math.Abs(ViewModel.NpcsViewZoomLevel - clampedResult) > 0.001)
                                {
                                    Debug.WriteLine(
                                        $"NpcsView: Textbox updating VM.NpcsViewZoomLevel to {clampedResult}");
                                    ViewModel.NpcsViewZoomLevel = clampedResult;
                                }
                            }
                            else if (!string.IsNullOrWhiteSpace(text))
                            {
                                Debug.WriteLine($"NpcsView: Textbox parse failed for '{text}', resetting to VM value.");
                                ZoomPercentageTextBox.Text =
                                    ViewModel.NpcsViewZoomLevel.ToString("F2", CultureInfo.InvariantCulture);
                            }
                        }
                    })
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.ZoomInNpcsCommand, v => v.ZoomInButton).DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.ZoomOutNpcsCommand, v => v.ZoomOutButton).DisposeWith(d);
                this.Bind(ViewModel, vm => vm.NpcsViewIsZoomLocked, v => v.LockZoomCheckBox.IsChecked).DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.ResetZoomNpcsCommand, v => v.ResetZoomNpcsButton).DisposeWith(d);


                ViewModel.RefreshImageSizesObservable
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => RefreshImageSizes())
                    .DisposeWith(d);


                ViewModel.RequestScrollToNpcObservable
                    .Where(npcToScrollTo => npcToScrollTo != null) // Already good
                    .ObserveOn(RxApp.MainThreadScheduler) // Already good
                    .SelectMany(async npcToScrollTo => // npcToScrollTo is the value from the observable stream
                    {
                        Debug.WriteLine(
                            $"NpcsView.WhenActivated: Received scroll request for {npcToScrollTo.DisplayName}");
                        try
                        {
                            // It's crucial to allow the ViewModel's SelectedNpc to update and bind to the ListBox
                            // BEFORE we try to evaluate the ListBox's state or scroll.
                            // A small delay helps ensure UI thread operations (like binding updates) can complete.
                            await Task.Delay(5); // Delay to let VM and bindings settle. Adjust if needed.
                            NpcListBox.UpdateLayout(); // Ensure ListBox layout is current

                            // Get the *current* truth from the ViewModel
                            var currentVmSelectedNpc = ViewModel.SelectedNpc;

                            // *** THE FIX: Check if the incoming scroll request is stale ***
                            if (npcToScrollTo != currentVmSelectedNpc)
                            {
                                Debug.WriteLine(
                                    $"NpcsView: Scroll request for '{npcToScrollTo.DisplayName}' (from observable) " +
                                    $"does not match current VM selection '{currentVmSelectedNpc?.DisplayName ?? "null"}'. " +
                                    "This is likely a stale request (e.g., from BehaviorSubject replay). Ignoring.");
                                return Unit.Default; // Abort this scroll operation
                            }

                            // If we reach here, npcToScrollTo == currentVmSelectedNpc.
                            // This means the scroll request is for the NPC that the ViewModel currently has selected.

                            var currentListBoxSelection = NpcListBox.SelectedItem as VM_NpcsMenuSelection;

                            if (NpcListBox.Items.Contains(npcToScrollTo))
                            {
                                // If ListBox.SelectedItem is already the target, ScrollIntoView is often a no-op but safe.
                                // If it's not, ScrollIntoView will change it and scroll.
                                if (currentListBoxSelection == npcToScrollTo)
                                {
                                    NpcListBox.ScrollIntoView(npcToScrollTo);
                                    Debug.WriteLine(
                                        $"NpcsView: Scrolled to {npcToScrollTo.DisplayName} (SelectedItem matched VM and ListBox).");
                                }
                                else
                                {
                                    // This case means ViewModel.SelectedNpc is npcToScrollTo, but ListBox.SelectedItem might lag
                                    // or there's a brief inconsistency. We trust the VM.
                                    Debug.WriteLine(
                                        $"NpcsView: ListBox.SelectedItem ('{currentListBoxSelection?.DisplayName ?? "null"}') " +
                                        $"might differ from VM.SelectedNpc ('{npcToScrollTo.DisplayName}'). " +
                                        $"Scrolling to '{npcToScrollTo.DisplayName}' based on VM truth.");
                                    NpcListBox.ScrollIntoView(npcToScrollTo);
                                }

                                // For ListBox, ScrollIntoView usually works well.
                                // If items are virtualized and ScrollIntoView isn't enough to guarantee the container is generated,
                                // an additional step to get the container and call BringIntoView can be more robust.
                                await Task.Delay(5); // Short delay for ScrollIntoView to take effect
                                NpcListBox.UpdateLayout();
                                if (NpcListBox.ItemContainerGenerator.ContainerFromItem(npcToScrollTo) is ListBoxItem
                                    item)
                                {
                                    item.BringIntoView();
                                    Debug.WriteLine(
                                        $"NpcsView: Ensured visibility using BringIntoView on ListBoxItem for {npcToScrollTo.DisplayName}.");
                                }
                                else
                                {
                                    Debug.WriteLine(
                                        $"NpcsView: ListBoxItem container still not found for {npcToScrollTo.DisplayName} after scroll and potential BringIntoView attempt. Item might be filtered out or list is empty.");
                                }
                            }
                            else
                            {
                                Debug.WriteLine(
                                    $"NpcsView: NPC {npcToScrollTo.DisplayName} not in ListBox.Items when scroll requested (and confirmed by VM).");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(
                                $"NpcsView: Error during scroll attempt for '{npcToScrollTo?.DisplayName ?? "null"}': {ex.Message}");
                        }

                        return Unit.Default;
                    })
                    .Subscribe()
                    .DisposeWith(d);

                if (ViewModel.CurrentNpcAppearanceMods != null && ViewModel.CurrentNpcAppearanceMods.Any())
                {
                    RefreshImageSizes();
                }

                // Add other view-specific subscriptions to 'd'
                _viewBindings.Add(d); // If you want to manage 'd' within _viewBindings
                
                // Animate Add button on successful execution
                ViewModel.AddCurrentNpcToGroupCommand
                    .Where(wasSuccessful => wasSuccessful) // Only trigger if the command returned true
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => AnimateButtonConfirmation(AddCurrentNpcButton, "Add Cur"))
                    .DisposeWith(d);

                // Animate Remove button on successful execution
                ViewModel.RemoveCurrentNpcFromGroupCommand
                    .Where(wasSuccessful => wasSuccessful) // Only trigger if the command returned true
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => AnimateButtonConfirmation(RemoveCurrentNpcButton, "Rem Cur"))
                    .DisposeWith(d);
                
                // Animate Add button on successful execution
                ViewModel.AddAllVisibleNpcsToGroupCommand
                    .Where(wasSuccessful => wasSuccessful) // Only trigger if the command returned true
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => AnimateButtonConfirmation(AddVisibleNpcsButton, "Add Vis"))
                    .DisposeWith(d);

                // Animate Remove button on successful execution
                ViewModel.RemoveAllVisibleNpcsFromGroupCommand
                    .Where(wasSuccessful => wasSuccessful) // Only trigger if the command returned true
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => AnimateButtonConfirmation(RemoveVisibleNpcsButton, "Rem Vis"))
                    .DisposeWith(d);
            });
        }
        
        private async void AnimateButtonConfirmation(Button button, string originalContent)
        {
            // Store the original background brush
            var originalBackground = button.Background;

            // Set the "Done" state
            button.Background = System.Windows.Media.Brushes.LawnGreen;
            button.Content = "Done";

            // Wait for 2 seconds
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Revert to the original state
            button.Background = originalBackground;
            button.Content = originalContent;
        }

        private void HideUnhideButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu != null)
            {
                // Crucial: This sets the PlacementTarget that the MenuItem bindings rely on
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = PlacementMode.Bottom;
                button.ContextMenu.IsOpen = true;
            }
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null) return;
            if (sender is ScrollViewer scrollViewer && scrollViewer.Name == "ImageDisplayScrollViewer" &&
                Keyboard.Modifiers == ModifierKeys.Control)
            {
                double change = (e.Delta > 0 ? 1 : -1) * _zoomStepPercentage;
                ViewModel.NpcsViewHasUserManuallyZoomed = true;
                ViewModel.NpcsViewZoomLevel = Math.Max(_minZoomPercentage,
                    Math.Min(_maxZoomPercentage, ViewModel.NpcsViewZoomLevel + change));
                e.Handled = true;
            }
        }

        private void ImageDisplayScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ViewModel != null && !ViewModel.NpcsViewIsZoomLocked)
            {
                ViewModel.NpcsViewHasUserManuallyZoomed = false;
            }

            RefreshImageSizes();
        }

        private void NpcListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Logic handled by VM
        }

        private void RefreshImageSizes()
        {
            if (ViewModel?.CurrentNpcAppearanceMods == null) return;

            var imagesToProcess = ViewModel.CurrentNpcAppearanceMods;
            if (!imagesToProcess.Any())
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewModel == null || ViewModel.CurrentNpcAppearanceMods == null ||
                    !ImageDisplayScrollViewer.IsLoaded) return;

                var visibleImages = imagesToProcess
                    .Where(img => img.IsVisible && img.OriginalDipDiagonal > 0)
                    .ToList<IHasMugshotImage>();

                if (!visibleImages.Any())
                {
                    foreach (var img in imagesToProcess)
                    {
                        img.ImageWidth = 0;
                        img.ImageHeight = 0;
                    }

                    return;
                }

                if (ViewModel.NpcsViewIsZoomLocked || ViewModel.NpcsViewHasUserManuallyZoomed)
                {
                    double sumOfDiagonals = visibleImages.Sum(img => img.OriginalDipDiagonal);
                    double averageOriginalDipDiagonal = sumOfDiagonals / visibleImages.Count;
                    if (averageOriginalDipDiagonal <= 0) averageOriginalDipDiagonal = 100.0;

                    double userZoomFactor = ViewModel.NpcsViewZoomLevel / 100.0;

                    foreach (var img in imagesToProcess)
                    {
                        if (img.IsVisible && img.OriginalDipDiagonal > 0)
                        {
                            double individualScaleFactor =
                                (averageOriginalDipDiagonal / img.OriginalDipDiagonal) * userZoomFactor;
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
                    var availableHeight = ImageDisplayScrollViewer.ViewportHeight;
                    var availableWidth = ImageDisplayScrollViewer.ViewportWidth;

                    if (availableHeight > 0 && availableWidth > 0)
                    {
                        var imagesForPacker =
                            new ObservableCollection<IHasMugshotImage>(imagesToProcess.Cast<IHasMugshotImage>());

                        double packerScaleFactor = ImagePacker.FitOriginalImagesToContainer(
                            imagesForPacker,
                            availableHeight,
                            availableWidth,
                            _mugshotMargin,
                            ViewModel.NormalizeImageDimensions
                        );
                        ViewModel.NpcsViewZoomLevel = packerScaleFactor * 100.0;
                    }
                }
            }), DispatcherPriority.Loaded);
        }

        private void Image_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (sender is FrameworkElement element && element.DataContext is VM_NpcsMenuMugshot vm)
                {
                    // Check if command can execute before invoking
                    vm.ToggleFullScreenCommand.CanExecute.Take(1).Subscribe(canExecute =>
                    {
                        if (canExecute)
                        {
                            vm.ToggleFullScreenCommand.Execute(Unit.Default).Subscribe()
                                .DisposeWith(_viewBindings); // Or manage disposal per click
                        }
                    }).DisposeWith(_viewBindings); // Ensure this outer subscription is also managed
                    e.Handled = true;
                }
            }
        }


        private void ZoomPercentageTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ViewModel == null || !(sender is TextBox textBox)) return;
            double currentValue = ViewModel.NpcsViewZoomLevel;
            double change = (e.Delta > 0 ? 1 : -1) * _zoomStepPercentage;

            ViewModel.NpcsViewHasUserManuallyZoomed = true;
            ViewModel.NpcsViewZoomLevel =
                Math.Max(_minZoomPercentage, Math.Min(_maxZoomPercentage, currentValue + change));

            var binding = textBox.GetBindingExpression(TextBox.TextProperty);
            binding?.UpdateSource();
            textBox.CaretIndex = textBox.Text.Length;
            textBox.SelectAll();
            e.Handled = true;
        }

        // Make sure to dispose _viewBindings if the UserControl is unloaded or disposed
        // For ReactiveUserControl, WhenActivated handles many cases, but if you have subscriptions
        // outside of it, you might need an explicit Dispose pattern or Unloaded event.
        // For simplicity, I'm assuming WhenActivated covers most UI-related subscriptions.
    }
}