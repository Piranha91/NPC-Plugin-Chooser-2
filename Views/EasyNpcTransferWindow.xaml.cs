using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// The EasyNPC import / export / profile-update report: what each NPC's selection becomes and
/// what was skipped and why, as a sortable/filterable table, with the go/no-go answer.
/// </summary>
public partial class EasyNpcTransferWindow : Window
{
    public EasyNpcTransferWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the report modally. Returns true to proceed; closing with Esc or the title bar counts
    /// as cancel. <paramref name="useFirstMatch"/> is the final state of the ambiguity toggle.
    /// </summary>
    public static bool Confirm(EasyNpcTransferWindowOptions options, IReadOnlyList<EasyNpcTransferRow> rows,
        out bool useFirstMatch)
    {
        var vm = new VM_EasyNpcTransferWindow(options, rows);
        var window = new EasyNpcTransferWindow { DataContext = vm, Title = options.Title };
        // DataGrid columns are not in the visual tree, so their headers cannot bind to the VM.
        window.NewColumn.Header = vm.NewHeader;
        if (vm.CurrentHeader == null) window.CurrentColumn.Visibility = Visibility.Collapsed;
        else window.CurrentColumn.Header = vm.CurrentHeader;
        TrySetOwner(window);

        bool? result = window.ShowDialog();
        useFirstMatch = vm.UseFirstMatch;
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

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
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
