using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using System.Reactive.Disposables; // Required for CompositeDisposable and DisposeWith
using System;

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Interaction logic for ResourcePluginSelectorWindow.xaml
    /// </summary>
    public partial class ResourcePluginSelectorWindow : ReactiveWindow<VM_ResourcePluginSelector>
    {
        public ResourcePluginSelectorWindow()
        {
            InitializeComponent();

            this.WhenActivated((CompositeDisposable d) =>
            {
                if (this.ViewModel == null) return;

                // Subscribe to the ViewModel's request to close the window.
                ViewModel.RequestClose += this.Close;
                
                // Ensure the subscription is cleaned up when the view is deactivated.
                Disposable.Create(() => this.ViewModel.RequestClose -= this.Close).DisposeWith(d);
            });
        }
    }
}