using System.Windows;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class ProgressWindow : Window
    {
        public ProgressWindow()
        {
            InitializeComponent();
        }

        public VM_ProgressWindow? ViewModel
        {
            get => DataContext as VM_ProgressWindow;
            set => DataContext = value;
        }
    }
}