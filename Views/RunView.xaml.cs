// RunView.xaml.cs
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Splat;
using System; // Added for Exception

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Interaction logic for RunView.xaml
    /// </summary>
    public partial class RunView : ReactiveUserControl<VM_Run>
    {
        /// <summary>
        /// Whether the log should stick to the bottom as lines arrive. Starts true and is
        /// switched off as soon as the user scrolls up, so scrolling back to read (or select)
        /// something mid-run is not fought by the next batch of lines.
        /// </summary>
        private bool _autoScrollLog = true;

        // --- Drag-select state (see LogListBox_PreviewMouseMove) ---
        private int _dragAnchorIndex = -1;
        private int _dragTargetIndex = -1;
        private int _selectedRangeFrom = -1;
        private int _selectedRangeTo = -1;
        private bool _isDragSelecting;
        private int _dragEdgeStep;
        private DispatcherTimer? _dragEdgeScrollTimer;

        public RunView()
        {
            InitializeComponent();

            // Attempt to resolve the ViewModel if DataContext is not already set by ViewLocator
            if (this.DataContext == null)
            {
                try
                {
                    ViewModel = Locator.Current.GetService<VM_Run>();
                    // Setting DataContext explicitly might interfere with ReactiveUI's View resolution
                    // Only do this if ViewLocator isn't working as expected.
                    DataContext = this.ViewModel;
                }
                catch (Exception ex)
                {
                    // Log or handle the error where the VM couldn't be resolved
                    System.Diagnostics.Debug.WriteLine($"Error resolving VM_Run: {ex.Message}");
                    // The view might not function correctly without its ViewModel
                }
            }
            
            this.WhenActivated(d =>
            {
                this.BindCommand(ViewModel, vm => vm.RunCommand, v => v.RunButton).DisposeWith(d);

                // Bind ComboBox for groups
                this.OneWayBind(ViewModel, vm => vm.AvailableNpcGroups, v => v.GroupComboBox.ItemsSource).DisposeWith(d);
                this.Bind(ViewModel, vm => vm.SelectedNpcGroup, v => v.GroupComboBox.SelectedItem).DisposeWith(d);
                
                // Bind Verbose Mode CheckBox
                this.Bind(ViewModel, vm => vm.IsVerboseModeEnabled, v => v.VerboseModeCheckBox.IsChecked).DisposeWith(d);

                // Log auto-scroll. ItemsSource itself is bound in XAML; here we only keep the
                // view pinned to the newest line while the user has not scrolled away.
                var onLogScrolled = new ScrollChangedEventHandler(LogScrollChanged);
                LogListBox.AddHandler(ScrollViewer.ScrollChangedEvent, onLogScrolled);
                Disposable.Create(() =>
                        LogListBox.RemoveHandler(ScrollViewer.ScrollChangedEvent, onLogScrolled))
                    .DisposeWith(d);

                if (ViewModel?.LogLines is INotifyCollectionChanged logLines)
                {
                    NotifyCollectionChangedEventHandler onLogChanged = (_, _) => ScrollLogToEndIfPinned();
                    logLines.CollectionChanged += onLogChanged;
                    Disposable.Create(() => logLines.CollectionChanged -= onLogChanged).DisposeWith(d);
                }

                // Ctrl+C over the log copies the selected lines (ListBox has no Copy binding of
                // its own); the context menu routes to the same handlers.
                var copyBinding = new CommandBinding(ApplicationCommands.Copy,
                    (_, e) => { CopySelectedLogLines(); e.Handled = true; },
                    (_, e) => { e.CanExecute = true; e.Handled = true; });
                LogListBox.CommandBindings.Add(copyBinding);
                Disposable.Create(() => LogListBox.CommandBindings.Remove(copyBinding)).DisposeWith(d);

                // Bind Progress Bar (OneWay since VM updates it)
                this.OneWayBind(ViewModel, vm => vm.ProgressValue, v => v.ProgressBar.Value).DisposeWith(d); // Assumes ProgressBar has x:Name="ProgressBar"
                this.OneWayBind(ViewModel, vm => vm.ProgressText, v => v.ProgressTextBlock.Text).DisposeWith(d); // Assumes TextBlock has x:Name="ProgressTextBlock"
                
                // Bind the Analyze Masters command
                this.BindCommand(ViewModel, vm => vm.AnalyzeMastersCommand, v => v.AnalyzeMastersButton).DisposeWith(d);
            });
        }

        /// <summary>
        /// Re-arms or disarms auto-scroll from the user's own scrolling. Scroll changes caused by
        /// the log growing (ExtentHeightChange != 0) are ignored, otherwise appending a line
        /// would look like the user scrolling away from the bottom.
        /// </summary>
        private void LogScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange != 0) return;
            _autoScrollLog = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 0.5;
        }

        private void ScrollLogToEndIfPinned()
        {
            if (!_autoScrollLog) return;

            var lines = ViewModel?.LogLines;
            if (lines == null || lines.Count == 0) return;

            LogListBox.ScrollIntoView(lines[lines.Count - 1]);
        }

        // ==================================================================
        // Drag-select
        //
        // WPF's ListBox has no drag-selection of its own — it only handles click, Ctrl+click,
        // Shift+click and Ctrl+A — so press-and-sweep is implemented here. Selection is
        // line-granular (a ListBox selects items, not characters); the mouse is captured on the
        // first move so a sweep can continue outside the control, and a timer keeps extending
        // the range while the pointer is held past the top or bottom edge.
        // ==================================================================

        private void LogListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ResetDragSelection();

            // Leave the scrollbar alone, and leave Ctrl/Shift to the ListBox's own additive and
            // range selection rather than fighting it.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                return;
            }

            var hit = LogListBox.InputHitTest(e.GetPosition(LogListBox)) as DependencyObject;
            if (hit == null || hit.TryFindParent<ScrollBar>() != null) return;

            int index = IndexOfContainingItem(hit);
            if (index < 0) return;

            // The anchor is only armed here; capture waits for an actual move so a plain click
            // still behaves exactly like a plain click.
            _dragAnchorIndex = index;
            _dragTargetIndex = index;
        }

        private void LogListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragAnchorIndex < 0) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                ResetDragSelection();
                return;
            }

            if (!_isDragSelecting)
            {
                _isDragSelecting = true;
                LogListBox.CaptureMouse();
            }

            UpdateDragSelection(e.GetPosition(LogListBox));
            e.Handled = true;
        }

        private void LogListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragSelecting) return;

            // Swallow the release: ListBoxItem's own mouse-up handling would otherwise collapse
            // the swept range back to the single line under the pointer.
            e.Handled = true;
            LogListBox.ReleaseMouseCapture();
            ResetDragSelection();
        }

        private void LogListBox_LostMouseCapture(object sender, MouseEventArgs e) => ResetDragSelection();

        /// <summary>
        /// Extends the selection to the line under <paramref name="position"/>, and arms or
        /// disarms edge scrolling when the pointer is held outside the viewport.
        /// </summary>
        private void UpdateDragSelection(Point position)
        {
            if (position.Y < 0)
            {
                _dragEdgeStep = -EdgeStepFor(-position.Y);
            }
            else if (position.Y > LogListBox.ActualHeight)
            {
                _dragEdgeStep = EdgeStepFor(position.Y - LogListBox.ActualHeight);
            }
            else
            {
                _dragEdgeStep = 0;

                // Inside the viewport: track the line under the pointer. A miss (the ListBox's
                // own padding band) just keeps the previous target.
                var hit = LogListBox.InputHitTest(position) as DependencyObject;
                if (hit != null)
                {
                    int index = IndexOfContainingItem(hit);
                    if (index >= 0) _dragTargetIndex = index;
                }
            }

            if (_dragEdgeStep == 0) StopEdgeScroll(); else StartEdgeScroll();

            ApplyDragSelection();
        }

        /// <summary>Pointer distance outside the viewport → lines per tick, so a far sweep moves faster.</summary>
        private static int EdgeStepFor(double distanceOutside) =>
            Math.Clamp(1 + (int)(distanceOutside / 20), 1, 10);

        private void StartEdgeScroll()
        {
            if (_dragEdgeScrollTimer == null)
            {
                _dragEdgeScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
                {
                    Interval = TimeSpan.FromMilliseconds(30)
                };
                _dragEdgeScrollTimer.Tick += DragEdgeScrollTick;
            }

            _dragEdgeScrollTimer.Start();
        }

        private void StopEdgeScroll() => _dragEdgeScrollTimer?.Stop();

        private void DragEdgeScrollTick(object? sender, EventArgs e)
        {
            var lines = ViewModel?.LogLines;
            if (!_isDragSelecting || _dragEdgeStep == 0 || lines == null || lines.Count == 0)
            {
                StopEdgeScroll();
                return;
            }

            int next = Math.Clamp(_dragTargetIndex + _dragEdgeStep, 0, lines.Count - 1);
            if (next == _dragTargetIndex)
            {
                StopEdgeScroll(); // already at the end of the log
                return;
            }

            _dragTargetIndex = next;
            ApplyDragSelection();
            LogListBox.ScrollIntoView(lines[next]);
        }

        private void ApplyDragSelection()
        {
            var lines = ViewModel?.LogLines;
            if (lines == null || _dragAnchorIndex < 0 || _dragTargetIndex < 0) return;

            int from = Math.Min(_dragAnchorIndex, _dragTargetIndex);
            int to = Math.Max(_dragAnchorIndex, _dragTargetIndex);
            if (to >= lines.Count) return;
            if (from == _selectedRangeFrom && to == _selectedRangeTo) return; // nothing moved

            _selectedRangeFrom = from;
            _selectedRangeTo = to;

            LogListBox.SelectedItems.Clear();
            for (int i = from; i <= to; i++)
            {
                LogListBox.SelectedItems.Add(lines[i]);
            }
        }

        private void ResetDragSelection()
        {
            StopEdgeScroll();
            _isDragSelecting = false;
            _dragAnchorIndex = -1;
            _dragTargetIndex = -1;
            _selectedRangeFrom = -1;
            _selectedRangeTo = -1;
            _dragEdgeStep = 0;
        }

        /// <summary>Index of the ListBoxItem containing <paramref name="hit"/>, or -1.</summary>
        private int IndexOfContainingItem(DependencyObject hit)
        {
            var container = hit as ListBoxItem ?? hit.TryFindParent<ListBoxItem>();
            return container == null ? -1 : LogListBox.ItemContainerGenerator.IndexFromContainer(container);
        }

        private void CopySelectedLogLines_Click(object sender, RoutedEventArgs e) => CopySelectedLogLines();

        private void CopyEntireLog_Click(object sender, RoutedEventArgs e) =>
            CopyLogLines(ViewModel?.LogLines);

        /// <summary>
        /// Copies the selected lines, or the whole log when nothing is selected (which is what a
        /// user pressing Ctrl+C on a log they have not clicked into expects).
        /// </summary>
        private void CopySelectedLogLines()
        {
            var lines = ViewModel?.LogLines;
            if (LogListBox.SelectedItems.Count == 0)
            {
                CopyLogLines(lines);
                return;
            }

            // Walk the log rather than SelectedItems so the copy comes out in display order
            // regardless of the order the user picked the lines in.
            var selected = new HashSet<RunLogEntry>(LogListBox.SelectedItems.Cast<RunLogEntry>());
            CopyLogLines(lines?.Where(selected.Contains) ?? selected);
        }

        private static void CopyLogLines(IEnumerable<RunLogEntry>? lines)
        {
            if (lines == null) return;

            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                sb.AppendLine(line.Text);
            }

            if (sb.Length == 0) return;

            try
            {
                Clipboard.SetText(sb.ToString());
            }
            catch (Exception ex)
            {
                // The clipboard can be locked by another process; a failed copy must not take
                // the window down.
                System.Diagnostics.Debug.WriteLine($"Failed to copy log to clipboard: {ex.Message}");
            }
        }
    }
}