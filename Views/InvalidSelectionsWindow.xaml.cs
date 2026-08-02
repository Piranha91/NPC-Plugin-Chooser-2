using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// The pre-run screening result: everything the validator rejected, as an issue -> mod -> NPC
/// tree, with the same go/no-go answer the message box it replaced returned.
/// </summary>
public partial class InvalidSelectionsWindow : Window
{
    public InvalidSelectionsWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the dialog modally. Returns true to skip the rejected selections and patch the rest,
    /// false to abort the run — closing the window with Esc or the title bar counts as abort,
    /// matching the No answer of the message box this replaced.
    /// </summary>
    public static bool Confirm(IReadOnlyList<Validator.InvalidSelection> entries)
    {
        var vm = new VM_InvalidSelectionsWindow(entries);
        var window = new InvalidSelectionsWindow { DataContext = vm };
        TrySetOwner(window);

        bool? result = window.ShowDialog();
        vm.Dispose();
        return result == true;
    }

    private static void TrySetOwner(Window window)
    {
        try
        {
            var owner = Application.Current?.MainWindow
                        ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            if (owner != null && owner != window && owner.IsLoaded)
            {
                window.Owner = owner;
            }
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"Could not set window owner: {ex.Message}");
        }
    }

    private bool _suppressingBringIntoView;

    /// <summary>
    /// Re-issues a row's bring-into-view request with zero width, so keyboard navigation still
    /// scrolls vertically to the selected row but nothing ever scrolls the tree sideways. Rows
    /// here end in a FormKey and are commonly wider than the window, and the default behaviour
    /// scrolls the chevron column off the left edge the moment a row is clicked.
    /// </summary>
    private void TreeViewItem_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (_suppressingBringIntoView || sender is not FrameworkElement item) return;

        e.Handled = true;
        try
        {
            // The re-issued request raises this same event; the flag keeps it from recursing.
            _suppressingBringIntoView = true;
            item.BringIntoView(new Rect(0, 0, 0, item.ActualHeight));
        }
        finally
        {
            _suppressingBringIntoView = false;
        }
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
