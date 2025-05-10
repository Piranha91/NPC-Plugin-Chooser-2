// MainWindow.xaml.cs
using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat;

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class MainWindow : ReactiveWindow<VM_MainWindow>
    {
        public MainWindow()
        {
            InitializeComponent();

            // Resolve ViewModel via Splat/Autofac
            ViewModel = Locator.Current.GetService<VM_MainWindow>();
            if (ViewModel == null)
            {
                MessageBox.Show(
                    "Critical Error: Could not resolve the Main Window ViewModel. The application will close.",
                    "Initialization Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            // All bindings & subscriptions live here
            this.WhenActivated(disposables =>
            {
                // 1) Host your inner view
                this.OneWayBind(ViewModel,
                                vm => vm.CurrentViewModel,
                                v  => v.ViewModelViewHost.ViewModel)
                    .DisposeWith(disposables);

                // 2) Tab-selection bindings
                this.Bind(ViewModel,
                          vm => vm.IsNpcsTabSelected,
                          v  => v.NpcsRadioButton.IsChecked)
                    .DisposeWith(disposables);
                this.Bind(ViewModel,
                          vm => vm.IsModsTabSelected,
                          v  => v.ModsRadioButton.IsChecked)
                    .DisposeWith(disposables);
                this.Bind(ViewModel,
                          vm => vm.IsSettingsTabSelected,
                          v  => v.SettingsRadioButton.IsChecked)
                    .DisposeWith(disposables);
                this.Bind(ViewModel,
                          vm => vm.IsRunTabSelected,
                          v  => v.RunRadioButton.IsChecked)
                    .DisposeWith(disposables);

                // 3) Enable/disable the “other” tabs
                this.WhenAnyValue(x => x.ViewModel.AreOtherTabsEnabled)
                    .Do(enabled =>
                    {
                        NpcsRadioButton.IsEnabled     = enabled;
                        ModsRadioButton.IsEnabled      = enabled;
                        RunRadioButton.IsEnabled       = enabled;
                        // Settings remains always enabled
                    })
                    .Subscribe()
                    .DisposeWith(disposables);

                // 4) Grey-out the text when they go disabled
                this.WhenAnyValue(x => x.ViewModel.AreOtherTabsEnabled)
                    .Subscribe(enabled =>
                    {
                        if (enabled)
                        {
                            // Clear to let your style/template restore normal look
                            NpcsRadioButton.ClearValue(Control.ForegroundProperty);
                            ModsRadioButton.ClearValue(Control.ForegroundProperty);
                            RunRadioButton.ClearValue(Control.ForegroundProperty);
                        }
                        else
                        {
                            // Use the system’s gray‐text brush
                            NpcsRadioButton.SetResourceReference(
                                Control.ForegroundProperty,
                                SystemColors.GrayTextBrushKey);
                            ModsRadioButton.SetResourceReference(
                                Control.ForegroundProperty,
                                SystemColors.GrayTextBrushKey);
                            RunRadioButton.SetResourceReference(
                                Control.ForegroundProperty,
                                SystemColors.GrayTextBrushKey);
                        }
                    })
                    .DisposeWith(disposables);
            });
        }
    }
}
