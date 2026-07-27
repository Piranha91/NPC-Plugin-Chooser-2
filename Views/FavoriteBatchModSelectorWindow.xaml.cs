using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Interaction logic for FavoriteBatchModSelectorWindow.xaml
    /// </summary>
    public partial class FavoriteBatchModSelectorWindow : ReactiveWindow<VM_FavoriteBatchModSelector>
    {
        public FavoriteBatchModSelectorWindow()
        {
            InitializeComponent();

            this.WhenActivated((CompositeDisposable d) =>
            {
                if (this.ViewModel == null) return;

                ViewModel.RequestClose += this.Close;
                Disposable.Create(() => this.ViewModel.RequestClose -= this.Close).DisposeWith(d);
            });
        }
    }
}
