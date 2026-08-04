using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System;

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Interaction logic for OverrideRootSelectorWindow.xaml
    /// </summary>
    public partial class OverrideRootSelectorWindow : ReactiveWindow<VM_OverrideRootSelector>
    {
        public OverrideRootSelectorWindow()
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
