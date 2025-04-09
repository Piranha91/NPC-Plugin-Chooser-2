using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using System.Reactive.Disposables;

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Interaction logic for RunView.xaml
    /// </summary>
    public partial class RunView : ReactiveUserControl<VM_Run>
    {
        public RunView()
        {
            InitializeComponent();
            this.WhenActivated(d =>
            {
                this.BindCommand(ViewModel, vm => vm.RunCommand, v => v.RunButton).DisposeWith(d);
                this.OneWayBind(ViewModel, vm => vm.LogOutput, v => v.LogTextBox.Text).DisposeWith(d);
            });
        }
    }
}
// Add x:Name="RunButton" and x:Name="LogTextBox" if using code-behind bindings