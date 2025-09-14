using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat;
using System.Reactive.Disposables;
using System.Windows;
using System.Windows.Controls;

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Interaction logic for SettingsView.xaml
    /// </summary>
    public partial class SettingsView : ReactiveUserControl<VM_Settings>
    {
        public SettingsView()
        {
            InitializeComponent();
            
            if (this.DataContext == null) // Only if not already set
            {
                try
                {
                    var vm = Locator.Current.GetService<VM_Settings>();
                    if (vm != null)
                    {
                        Console.WriteLine("DEBUG: Manually setting DataContext in SettingsView constructor!");
                        this.DataContext = vm;
                        this.ViewModel = vm; // Also set the ViewModel property
                    }
                    else
                    {
                        Console.WriteLine("DEBUG: Failed to resolve VM_Settings manually in constructor.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DEBUG: Error manually resolving/setting DataContext: {ex.Message}");
                }
            }
            
            this.WhenActivated(d =>
            {
                // Bindings are mostly handled in XAML for settings
                // Example if needed:
                // this.Bind(ViewModel, vm => vm.SkyrimGamePath, v => v.GamePathTextBox.Text).DisposeWith(d);
                // this.BindCommand(ViewModel, vm => vm.SelectGameFolderCommand, v => v.BrowseGamePathButton).DisposeWith(d);
            });
        }
        
        // This event handler is the secure way to get the password from the UI
        // and pass it to the ViewModel without ever storing it in a bindable property.
        private void ApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (this.ViewModel is VM_Settings vm)
            {
                vm.UpdateApiKey(((PasswordBox)sender).Password);
            }
        }
    }
}