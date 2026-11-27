using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// The second pre-run window: valid selections whose wig 'Forward to Outfit' would add to an
/// outfit that already holds headgear on the same body slot. It removes no selection; it asks
/// what to do with the wig for the NPCs listed, or cancels the run.
/// </summary>
public partial class WigHeadgearConflictWindow : Window
{
    public WigHeadgearConflictWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the window modally and returns the user's answer. Closing it with Esc or the title
    /// bar cancels the run, as declining the invalid-selections window does. Call on the UI thread.
    /// </summary>
    public static WigHeadgearConflictDecision Ask(IReadOnlyList<WigHeadgearConflictCase> cases)
    {
        var vm = new VM_WigHeadgearConflictWindow(cases);
        var window = new WigHeadgearConflictWindow { DataContext = vm };
        TrySetOwner(window);

        void Close() => window.Close();
        vm.CloseRequested += Close;
        try
        {
            window.ShowDialog();
            return vm.Decision;
        }
        finally
        {
            vm.CloseRequested -= Close;
            vm.Dispose();
        }
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
}
