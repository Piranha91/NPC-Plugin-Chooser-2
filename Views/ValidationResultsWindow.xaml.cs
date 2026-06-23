using System.Windows;

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class ValidationResultsWindow : Window
    {
        public ValidationResultsWindow()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
