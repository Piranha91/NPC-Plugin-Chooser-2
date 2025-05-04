// RunView.xaml.cs
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Windows.Controls;
using Splat; // Required for TextBox ScrollToEnd

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Interaction logic for RunView.xaml
    /// </summary>
    public partial class RunView : ReactiveUserControl<VM_Run>
    {
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

                // Bind Log Output and add auto-scroll
                this.OneWayBind(ViewModel, vm => vm.LogOutput, v => v.LogTextBox.Text).DisposeWith(d);
                this.WhenAnyValue(v => v.LogTextBox.Text) // Use WhenAnyValue to react to text changes
                    .Subscribe(_ => LogTextBox.ScrollToEnd()) // Simple auto-scroll
                    .DisposeWith(d);

                // Bind Progress Bar (OneWay since VM updates it)
                this.OneWayBind(ViewModel, vm => vm.ProgressValue, v => v.ProgressBar.Value).DisposeWith(d); // Assumes ProgressBar has x:Name="ProgressBar"
                this.OneWayBind(ViewModel, vm => vm.ProgressText, v => v.ProgressTextBlock.Text).DisposeWith(d); // Assumes TextBlock has x:Name="ProgressTextBlock"
            });
        }
    }
}