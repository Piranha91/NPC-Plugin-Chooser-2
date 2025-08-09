// ViewModels/VM_SplashScreen.cs

using System.Reactive;
using System.Reactive.Threading.Tasks;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Windows.Threading;
using NPC_Plugin_Chooser_2.Views; // Required for Dispatcher
using System.Windows; // Required for Application
using System.Diagnostics; // Required for Stopwatch
using System; // Required for TimeSpan

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_SplashScreen : ReactiveObject
    {
        [Reactive] public string ProgramVersion { get; private set; }
        [Reactive] public double ProgressValue { get; private set; }
        [Reactive] public string OperationText { get; private set; }
        [Reactive] public string? FooterMessage { get; private set; }
        [Reactive] public string? StepText { get; private set; }
        [Reactive] public string ElapsedTimeString { get; private set; } // New property for the timer

        public string ImagePath => "pack://application:,,,/Resources/SplashScreenImage.png";

        private readonly Dispatcher _dispatcher;
        private Window? _window; // Reference to the window
        private readonly DispatcherTimer _timer; // New timer
        private readonly Stopwatch _stopwatch; // New stopwatch

        public Interaction<Unit, Unit> RequestOpen { get; } = new();
        public Interaction<Unit, Unit> RequestClose { get; } = new();

        public VM_SplashScreen(string programVersion)
        {
            ProgramVersion = programVersion;
            OperationText = "Initializing...";
            ProgressValue = 0;
            ElapsedTimeString = "Elapsed: 00:00:00"; // Initial value
            _dispatcher = Dispatcher.CurrentDispatcher;

            // --- Start of new code ---
            _stopwatch = Stopwatch.StartNew();
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (sender, args) =>
            {
                ElapsedTimeString = $"Elapsed: {_stopwatch.Elapsed:hh\\:mm\\:ss}";
            };
            _timer.Start();
            // --- End of new code ---
        }

        public void UpdateProgress(double percent, string message)
        {
            if (_dispatcher.CheckAccess())
            {
                ProgressValue = percent;
                OperationText = message;
            }
            else
            {
                _dispatcher.Invoke(() =>
                {
                    ProgressValue = percent;
                    OperationText = message;
                }, DispatcherPriority.Send);
            }
        }
        
        /// <summary>
        /// Creates a fresh splash‐screen VM + window, shows it, and returns the VM.
        /// Can be shown as a modal window that disables the main window.
        /// </summary>
        public static VM_SplashScreen InitializeAndShow(string programVersion, string? footerMessage = null, bool isModal = false, bool keepTopMost = false)
        {
            var vm = new VM_SplashScreen(programVersion)
            {
                FooterMessage = footerMessage,
            };
            var window = new SplashScreenWindow { DataContext = vm };
            
            vm._window = window; 

            // Set owner and handle modal behavior
            Window? owner = Application.Current?.MainWindow;
            if (owner != null && owner.IsVisible)
            {
                window.Owner = owner;
                if (isModal)
                {
                    // Disable owner to block input, but don't block the UI thread
                    owner.IsEnabled = false;
                }
            }

            if (!isModal)
            {
                window.Topmost = true;
                window.Activated += (sender, args) =>
                {
                    if (sender is SplashScreenWindow activatedWindow && !keepTopMost)
                    {
                        activatedWindow.Topmost = false;
                    }
                };
            }
    
            // Always use Show() so the UI thread is not blocked
            window.Show();

            return vm;
        }
        
        public async Task OpenSplashScreenAsync()
        {
            await RequestOpen.Handle(Unit.Default).ToTask();
            await Task.Yield();
        }
        
        public void UpdateStep(string stepMessage)
        {
            if (_dispatcher.CheckAccess())
            {
                StepText = stepMessage;
                ProgressValue = 0; // Reset progress for the new step
                OperationText = "Please wait..."; // Reset operation text
            }
            else
            {
                _dispatcher.Invoke(() =>
                {
                    StepText = stepMessage;
                    ProgressValue = 0;
                    OperationText = "Please wait...";
                }, DispatcherPriority.Send);
            }
        }

        /// <summary>
        /// Closes the splash screen window and re-enables the owner if it was disabled.
        /// </summary>
        public async Task CloseSplashScreenAsync()
        {
            Action closeAction = () =>
            {
                // --- Start of modified code ---
                _timer.Stop();
                _stopwatch.Stop();
                // --- End of modified code ---
                
                if (_window != null)
                {
                    // Re-enable the owner if it exists and was disabled
                    if (_window.Owner != null && !_window.Owner.IsEnabled)
                    {
                        _window.Owner.IsEnabled = true;
                    }
                    _window.Close();
                }
            };

            if (_dispatcher.CheckAccess())
            {
                closeAction();
            }
            else
            {
                await _dispatcher.InvokeAsync(closeAction);
            }

            await Task.Yield();
        }
    }
}