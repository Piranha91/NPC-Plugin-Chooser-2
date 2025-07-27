// ViewModels/VM_SplashScreen.cs

using System.Reactive;
using System.Reactive.Threading.Tasks;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Windows.Threading;
using NPC_Plugin_Chooser_2.Views; // Required for Dispatcher
using System.Windows; // Required for Application

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_SplashScreen : ReactiveObject
    {
        [Reactive] public string ProgramVersion { get; private set; }
        [Reactive] public double ProgressValue { get; private set; }
        [Reactive] public string OperationText { get; private set; }
        public string ImagePath => "pack://application:,,,/Resources/SplashScreenImage.png";

        private Dispatcher _dispatcher;
        private Window? _window; // Reference to the window
        
        public Interaction<Unit, Unit> RequestOpen  { get; } = new();
        public Interaction<Unit, Unit> RequestClose { get; } = new();

        public VM_SplashScreen(string programVersion)
        {
            ProgramVersion = programVersion;
            OperationText = "Initializing...";
            ProgressValue = 0;
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        public void UpdateProgress(double percent, string message)
        {
            if (_dispatcher.CheckAccess())
            {
                ProgressValue  = percent;
                OperationText  = message;
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
        public static VM_SplashScreen InitializeAndShow(string programVersion, bool isModal = false, bool keepTopMost = false)
        {
            var vm = new VM_SplashScreen(programVersion);
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

        /// <summary>
        /// Closes the splash screen window and re-enables the owner if it was disabled.
        /// </summary>
        public async Task CloseSplashScreenAsync()
        {
            Action closeAction = () =>
            {
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