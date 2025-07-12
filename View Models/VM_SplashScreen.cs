// ViewModels/VM_SplashScreen.cs

using System.Reactive;
using System.Reactive.Threading.Tasks;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Windows.Threading;
using NPC_Plugin_Chooser_2.Views; // Required for Dispatcher

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_SplashScreen : ReactiveObject
    {
        [Reactive] public string ProgramVersion { get; private set; }
        [Reactive] public double ProgressValue { get; private set; }
        [Reactive] public string OperationText { get; private set; }
        public string ImagePath => "pack://application:,,,/Resources/SplashScreenImage.png"; // Assumes image is in Resources folder, Build Action: Resource

        private Dispatcher _dispatcher;
        
        /// <summary>Interaction the View wires up to actually show the window.</summary>
        public Interaction<Unit, Unit> RequestOpen  { get; } = new();

        /// <summary>Interaction the View wires up to actually hide the window.</summary>
        public Interaction<Unit, Unit> RequestClose { get; } = new();

        public VM_SplashScreen(string programVersion)
        {
            ProgramVersion = programVersion;
            OperationText = "Initializing...";
            ProgressValue = 0;
            // Capture the dispatcher of the thread that creates this VM (should be UI thread)
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        public void UpdateProgress(double percent, string message)
        {
            if (_dispatcher.CheckAccess())           // already on UI thread
            {
                ProgressValue  = percent;
                OperationText  = message;
            }
            else
            {
                _dispatcher.Invoke(() =>             // ← blocks calling thread
                {
                    ProgressValue = percent;
                    OperationText = message;
                }, DispatcherPriority.Send);
            }
        }
        
        /// <summary>
        /// Creates a fresh splash‐screen VM + window, shows it, and returns the VM
        /// so you can call UpdateProgress(...) and CloseSplashScreenAsync() on it.
        /// </summary>
        public static VM_SplashScreen InitializeAndShow(string programVersion)
        {
            // 1) Create a brand‐new VM
            var vm = new VM_SplashScreen(programVersion);

            // 2) Wire up a new window
            var window = new SplashScreenWindow
            {
                DataContext = vm
            };

            // 3) Set the window to be topmost initially
            window.Topmost = true;

            // 4) After the window is first activated, turn off Topmost so other windows can cover it.
            //    We use an event handler that unhooks itself after it runs once.
            window.Activated += (sender, args) =>
            {
                if (sender is SplashScreenWindow activatedWindow)
                {
                    activatedWindow.Topmost = false;
                }
            };
    
            // 5) Show it immediately
            window.Show();

            return vm;
        }
        
        /// <summary>
        /// Requests the splash screen window to show (via the RequestOpen interaction),
        /// then yields so WPF has a chance to process the Show() before any further work.
        /// </summary>
        public async Task OpenSplashScreenAsync()
        {
            // Ask the View to show
            await RequestOpen.Handle(Unit.Default)
                .ToTask();

            // Let the UI thread catch up and actually render
            await Task.Yield();
        }

        /// <summary>
        /// Requests the splash screen window to hide (via the RequestClose interaction),
        /// then yields so WPF has a chance to process the Hide() before any further work.
        /// </summary>
        public async Task CloseSplashScreenAsync()
        {
            // Ask the View to hide
            await RequestClose.Handle(Unit.Default)
                .ToTask();

            // Let the UI thread catch up
            await Task.Yield();
        }
    }
}