using System.Windows;
using NPC_Plugin_Chooser_2.View_Models.SetupWizard;

namespace NPC_Plugin_Chooser_2.Views;

public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow(VM_SetupWizard viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Finish → DialogResult true; Cancel and the title-bar X both leave it false.
        viewModel.CloseRequested += finished =>
        {
            DialogResult = finished;
            Close();
        };
    }
}
