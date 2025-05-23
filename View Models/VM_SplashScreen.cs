// ViewModels/VM_SplashScreen.cs
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Windows.Threading; // Required for Dispatcher

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_SplashScreen : ReactiveObject
    {
        [Reactive] public string ProgramVersion { get; private set; }
        [Reactive] public double ProgressValue { get; private set; }
        [Reactive] public string OperationText { get; private set; }
        public string ImagePath => "pack://application:,,,/Resources/SplashScreenImage.png"; // Assumes image is in Resources folder, Build Action: Resource

        private Dispatcher _dispatcher;

        public VM_SplashScreen(string programVersion)
        {
            ProgramVersion = programVersion;
            OperationText = "Initializing...";
            ProgressValue = 0;
            // Capture the dispatcher of the thread that creates this VM (should be UI thread)
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        public void UpdateProgress(double percentage, string message)
        {
            // Ensure updates run on the UI thread
            _dispatcher.InvokeAsync(() =>
            {
                ProgressValue = percentage;
                OperationText = message;
            });
        }
    }
}