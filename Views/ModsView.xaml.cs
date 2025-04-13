using System.Windows.Controls;

// Views/ModsView.xaml.cs
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat; // For Locator
using System;
using System.Reactive.Disposables;

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Interaction logic for ModsView.xaml
    /// </summary>
    public partial class ModsView : ReactiveUserControl<VM_Mods>
    {
        public ModsView()
        {
            InitializeComponent();

            // Attempt to resolve the ViewModel if DataContext is not already set by ViewLocator
            if (this.DataContext == null)
            {
                try
                {
                    this.ViewModel = Locator.Current.GetService<VM_Mods>();
                    this.DataContext = this.ViewModel; // Set DataContext explicitly if needed
                }
                catch (Exception ex)
                {
                    // Log or handle the error where the VM couldn't be resolved
                    System.Diagnostics.Debug.WriteLine($"Error resolving VM_Mods: {ex.Message}");
                    // The view might not function correctly without its ViewModel
                }
            }


            this.WhenActivated(disposables =>
            {
                // Setup bindings or interactions here if needed in code-behind
                // For example:
                // this.OneWayBind(ViewModel, vm => vm.ModSettingsList, v => v.ModsItemsControl.ItemsSource).DisposeWith(disposables);

                // Most bindings are handled in XAML for this view.
            });
        }
    }
}