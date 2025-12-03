using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat;
using System.Reactive.Disposables;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
        
        // Allows only integer values
        private void IntegerTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsTextAllowed(e.Text, @"^[0-9]+$"); // Regex for one or more digits
        }

        // Allows floating point values (digits and a single decimal point)
        private void FloatTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            // Allow digits, and allow a single '.' if it doesn't already exist in the text.
            bool isDecimalAllowed = e.Text == "." && textBox != null && !textBox.Text.Contains('.');
            e.Handled = !IsTextAllowed(e.Text, @"^[0-9]+$") && !isDecimalAllowed;
        }

        // Pasting validation (blocks paste if content is invalid for the respective type)
        private void IntegerTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                if (!IsTextAllowed(text, @"^[0-9]+$"))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }
    
        private void FloatTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                // Simple float validation: allows one optional decimal point
                if (!IsTextAllowed(text, @"^[0-9]*\.?[0-9]*$"))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private static bool IsTextAllowed(string text, string allowedRegex)
        {
            return Regex.IsMatch(text, allowedRegex);
        }
    }
}