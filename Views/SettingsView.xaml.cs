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

        // Allows floating point values: digits, a single decimal point, and a
        // single leading '-' sign. Some fields routed through this handler need
        // negatives (e.g. Pitch looking up, Frame Bottom < 0 for ultra-tight
        // crop), so the keystroke filter must accept them.
        //
        // Validates the FULL resulting string (current text with the keystroke
        // applied at the caret / selection) instead of just e.Text in isolation.
        // The per-character approach broke compound input like "0.1" - the "0"
        // keystroke was checked against ^[0-9]+$ which matches "0" alone, so it
        // was allowed; but on the next "." keystroke, the isDecimalAllowed
        // check tripped on a stale selection / text snapshot in some inputs and
        // rejected the dot, leaving the user with "0" or "01"-shaped input.
        // Validating the resulting string is order-of-keystroke-independent and
        // matches FloatTextBox_Pasting's regex exactly.
        private void FloatTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is not TextBox textBox) return;

            string current = textBox.Text ?? string.Empty;
            int selStart = textBox.SelectionStart;
            int selLength = textBox.SelectionLength;
            string resulting = current.Substring(0, selStart)
                + (e.Text ?? string.Empty)
                + current.Substring(selStart + selLength);

            // Accept any prefix or full form of a signed decimal: optional '-'
            // at the start, optional digits, optional '.', optional digits.
            // Empty string is allowed (mid-edit deletes leave it empty).
            e.Handled = !Regex.IsMatch(resulting, @"^-?[0-9]*\.?[0-9]*$");
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
                // Float validation: optional leading '-', digits, optional '.'.
                // Matches FloatTextBox_PreviewTextInput's accepted shape.
                if (!IsTextAllowed(text, @"^-?[0-9]*\.?[0-9]*$"))
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