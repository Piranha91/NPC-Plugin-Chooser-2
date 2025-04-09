// MainWindow.xaml.cs
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat; // Required for Locator and GetService extension methods (though we use the base method here)
using System.Reactive.Disposables;
using System.Windows; // Required for Window if not implicitly covered by ReactiveWindow

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : ReactiveWindow<VM_MainWindow>
    {
        public MainWindow()
        {
            InitializeComponent();

            // Use the non-generic GetService and cast the result
            // Ensure that VM_MainWindow is registered correctly in App.xaml.cs
            ViewModel = (VM_MainWindow?)Locator.Current.GetService(typeof(VM_MainWindow));

            // It's good practice to handle the case where the ViewModel might not be resolved
            if (ViewModel == null)
            {
                 // Log or display a critical error, as the application cannot function
                 MessageBox.Show("Critical Error: Could not resolve the Main Window ViewModel. The application will close.", "Initialization Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                 // Consider Application.Current.Shutdown(); or throw new InvalidOperationException(...)
                 // For now, we'll just prevent the WhenActivated block from running if VM is null
                 return;
            }


            this.WhenActivated(d =>
            {
                // Ensure ViewModel is not null before proceeding with bindings
                if (ViewModel == null) return;

                // Bindings can be defined here in code-behind if preferred,
                // but XAML bindings are generally cleaner for simple cases.
                // Example: this.Bind(ViewModel, vm => vm.IsNpcsTabSelected, v => v.NpcRadioButton.IsChecked).DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.CurrentViewModel, v => v.ViewModelViewHost.ViewModel).DisposeWith(d);

                // Wire up tab selection logic (can also be done with commands)
                // These require x:Name attributes on the RadioButtons in MainWindow.xaml
                 this.Bind(ViewModel, vm => vm.IsNpcsTabSelected, v => v.NpcsRadioButton.IsChecked).DisposeWith(d);
                 this.Bind(ViewModel, vm => vm.IsSettingsTabSelected, v => v.SettingsRadioButton.IsChecked).DisposeWith(d);
                 this.Bind(ViewModel, vm => vm.IsRunTabSelected, v => v.RunRadioButton.IsChecked).DisposeWith(d);

            });
        }
    }
}