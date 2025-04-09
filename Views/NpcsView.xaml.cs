// NpcsView.xaml.cs
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Windows.Controls; // Required for ListBox
using System.Windows.Media; // Required for Brush
using Splat;

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
            
            if (this.DataContext == null) // Only if not already set
            {
                try
                {
                    var vm = Locator.Current.GetService<VM_NpcSelectionBar>();
                    if (vm != null)
                    {
                        Console.WriteLine("DEBUG: Manually setting DataContext in SettingsView constructor!");
                        this.DataContext = vm;
                        this.ViewModel = vm; // Also set the ViewModel property
                    }
                    else
                    {
                        Console.WriteLine("DEBUG: Failed to resolve VM_Settings manually in constructor.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DEBUG: Error manually resolving/setting DataContext: {ex.Message}");
                }
            }

            this.WhenActivated(d =>
            {
                this.OneWayBind(ViewModel, vm => vm.Npcs, v => v.NpcListBox.ItemsSource).DisposeWith(d);
                this.Bind(ViewModel, vm => vm.SelectedNpc, v => v.NpcListBox.SelectedItem).DisposeWith(d);
                this.OneWayBind(ViewModel, vm => vm.CurrentNpcAppearanceMods, v => v.AppearanceModsItemsControl.ItemsSource).DisposeWith(d);

                // Bind visibility triggers (done in XAML for simplicity here)
                // this.OneWayBind(ViewModel, vm => vm.SelectedNpc, v => v.SelectNpcTextBlock.Visibility,
                //    npc => npc == null ? Visibility.Visible : Visibility.Collapsed).DisposeWith(d);
                // this.OneWayBind(ViewModel, vm => vm.SelectedNpc, v => v.AppearanceModsScrollViewer.Visibility,
                //    npc => npc != null ? Visibility.Visible : Visibility.Collapsed).DisposeWith(d);

            });
        }
    }
}
// Add x:Name to controls in XAML to use code-behind bindings:
// <ListBox x:Name="NpcListBox" ... />
// <ItemsControl x:Name="AppearanceModsItemsControl" ... />
// <TextBlock x:Name="SelectNpcTextBlock" ... />
// <ScrollViewer x:Name="AppearanceModsScrollViewer" ... />