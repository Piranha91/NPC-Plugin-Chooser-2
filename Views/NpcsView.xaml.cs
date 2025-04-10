// NpcsView.xaml.cs (Updated)
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using System; // Required for Exception, Console
using System.Reactive.Disposables;
// using System.Windows.Controls; // Not strictly required if using XAML bindings mostly
// using System.Windows.Media; // Not required for these bindings
using Splat; // Required for Locator

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Interaction logic for NpcsView.xaml
    /// </summary>
    public partial class NpcsView : ReactiveUserControl<VM_NpcSelectionBar>
    {
        public NpcsView()
        {
            InitializeComponent();

            // Attempt to manually resolve/set DataContext if not done by ViewLocator
            // NOTE: This is often not necessary if ReactiveUI's View Location is set up correctly.
            if (this.DataContext == null)
            {
                try
                {
                    // Use GetService<T> which returns default(T) (null for classes) if not found
                    var vm = Locator.Current.GetService<VM_NpcSelectionBar>();
                    if (vm != null)
                    {
                        System.Diagnostics.Debug.WriteLine("DEBUG: Manually setting DataContext/ViewModel in NpcsView constructor!");
                        this.DataContext = vm;
                        // Setting ViewModel is redundant if DataContext is set correctly and
                        // the view inherits ReactiveUserControl<TViewModel>, but doesn't hurt.
                        this.ViewModel = vm;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("DEBUG: Failed to resolve VM_NpcSelectionBar manually in constructor (GetService returned null).");
                        // Consider throwing an exception or logging more severely if the VM is essential here.
                    }
                }
                catch (Exception ex)
                {
                    // Catch potential exceptions from Splat/DI container
                    System.Diagnostics.Debug.WriteLine($"DEBUG: Error manually resolving/setting DataContext in NpcsView: {ex.Message}");
                    // Handle error appropriately, maybe show a message to the user or log to a file.
                }
            }


            this.WhenActivated(d =>
            {
                // --- FIX: Bind to FilteredNpcs instead of Npcs ---
                // Binds the ViewModel's FilteredNpcs collection to the NpcListBox's ItemsSource.
                this.OneWayBind(ViewModel,
                                vm => vm.FilteredNpcs, // Source property on ViewModel
                                v => v.NpcListBox.ItemsSource) // Target property on View
                    .DisposeWith(d); // Dispose the binding when the view is deactivated

                // Binds the SelectedNpc property both ways between ViewModel and ListBox selection.
                this.Bind(ViewModel,
                          vm => vm.SelectedNpc, // Source property on ViewModel
                          v => v.NpcListBox.SelectedItem) // Target property on View
                    .DisposeWith(d);

                // Binds the ViewModel's collection of appearance mods for the currently selected NPC
                // to the ItemsControl displaying them.
                this.OneWayBind(ViewModel,
                                vm => vm.CurrentNpcAppearanceMods, // Source property on ViewModel
                                v => v.AppearanceModsItemsControl.ItemsSource) // Target property on View
                    .DisposeWith(d);

                // Visibility bindings are handled in XAML using DataTriggers/MultiDataTriggers,
                // which is generally preferred for UI state logic.
                // Example of how it *could* be done in code-behind (but not needed here):
                // this.OneWayBind(ViewModel, vm => vm.SelectedNpc, v => v.YourPlaceholderTextBlock.Visibility,
                //    npc => npc == null ? Visibility.Visible : Visibility.Collapsed).DisposeWith(d);

            });
        }
    }
}