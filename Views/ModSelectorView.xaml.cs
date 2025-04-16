using NPC_Plugin_Chooser_2.View_Models; // Adjust namespace if needed
using ReactiveUI;
using System.Reactive.Disposables; // For WhenActivated

namespace NPC_Plugin_Chooser_2.Views // Adjust namespace if needed
{
    /// <summary>
    /// Interaction logic for ModSelectorView.xaml
    /// </summary>
    public partial class ModSelectorView : ReactiveUserControl<VM_ModSelector>
    {
        public ModSelectorView()
        {
            InitializeComponent();

            this.WhenActivated(disposables =>
            {
                // No specific bindings needed here typically, as ItemsSource and ItemTemplate bindings
                // are handled in XAML. Add view-specific logic if required.
            });
        }
    }
}