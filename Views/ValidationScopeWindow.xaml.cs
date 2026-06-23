using System.Windows;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class ValidationScopeWindow : Window
    {
        public ValidationScopeWindow()
        {
            InitializeComponent();
        }

        // Checking an NPC implies the user wants a specific subset, so flip the scope radio.
        private void NpcCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (DataContext is VM_ValidationScopeWindow vm)
            {
                vm.ScopeSubset = true;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
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
}
