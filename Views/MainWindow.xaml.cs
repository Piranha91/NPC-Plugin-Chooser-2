// MainWindow.xaml.cs
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Themes;
using ReactiveUI;
using Splat;

namespace NPC_Plugin_Chooser_2.Views
{
    public partial class MainWindow : ReactiveWindow<VM_MainWindow>
    {
        private Settings _appSettings; // Store a reference to the settings
        public MainWindow()
        {
            InitializeComponent();

            // Resolve ViewModel via Splat/Autofac
            ViewModel = Locator.Current.GetService<VM_MainWindow>();
            if (ViewModel == null)
            {
                MessageBox.Show(
                    "Critical Error: Could not resolve the Main Window ViewModel.",
                    "Initialization Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
            
            _appSettings = Locator.Current.GetService<Settings>(); // Resolve settings instance
            
            // --- Apply Stored Window Settings on Load ---
            ApplyWindowSettings();

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
                        vm => vm.IsSummaryTabSelected,
                        v  => v.SummaryRadioButton.IsChecked)
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
                        NpcsRadioButton.IsEnabled      = enabled;
                        ModsRadioButton.IsEnabled      = enabled;
                        SummaryRadioButton.IsEnabled   = enabled;
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
                            SummaryRadioButton.ClearValue(Control.ForegroundProperty);
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
                            SummaryRadioButton.SetResourceReference(
                                Control.ForegroundProperty,
                                SystemColors.GrayTextBrushKey);
                            RunRadioButton.SetResourceReference(
                                Control.ForegroundProperty,
                                SystemColors.GrayTextBrushKey);
                        }
                    })
                    .DisposeWith(disposables);
                
                // --- Save Window Settings on Close ---
                // Handle the Window.Closing event
                Observable.FromEventPattern<CancelEventArgs>(this, nameof(Closing))
                    .Subscribe(args => SaveWindowSettings())
                    .DisposeWith(disposables);

                // Alternative for saving, if already handling App.Exit for other settings:
                // The App.Exit event in App.xaml.cs is generally better for saving *all* settings,
                // but window-specific state like position/size is often saved directly from the window's Closing event.
                // If VM_Settings.SaveAllSettings() is robust and called on App.Exit,
                // you just need to ensure _appSettings instance is updated before that.
            });
        }
        
        private void ApplyWindowSettings()
        {
            if (_appSettings == null) return;

            // Restore window state first, then size/position
            // Important: Set state AFTER size/position if restoring from maximized/minimized
            // to avoid issues where size doesn't apply correctly.
            // Or, set size/pos, then state. Let's try size/pos then state.

            this.Width = _appSettings.MainWindowWidth;
            this.Height = _appSettings.MainWindowHeight;
            this.Top = _appSettings.MainWindowTop;
            this.Left = _appSettings.MainWindowLeft;

            // Ensure the window is on a visible screen
            Rect windowRect = new Rect(this.Left, this.Top, this.Width, this.Height);
            bool onScreen = false;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens) // Requires reference to System.Windows.Forms
            {
                System.Drawing.Rectangle screenRect = screen.WorkingArea;
                if (screenRect.IntersectsWith(new System.Drawing.Rectangle((int)windowRect.X, (int)windowRect.Y, (int)windowRect.Width, (int)windowRect.Height)))
                {
                    onScreen = true;
                    break;
                }
            }

            if (!onScreen)
            {
                // Window is off-screen, reset to default or center
                this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                // Or reset to your defaults from Settings model if preferred
                // this.Width = _appSettings.MainWindowWidth; // (assuming defaults are sensible)
                // this.Height = _appSettings.MainWindowHeight;
            }


            // Restore state (Normal, Maximized). Avoid Minimized on startup.
            if (_appSettings.MainWindowState == WindowState.Minimized)
            {
                this.WindowState = WindowState.Normal; // Or restore previous non-minimized state if you store that too
            }
            else
            {
                this.WindowState = _appSettings.MainWindowState;
            }
            Debug.WriteLine($"Applied Window Settings: L:{Left} T:{Top} W:{Width} H:{Height} S:{WindowState}");
        }
        
        private void SaveWindowSettings()
        {
            if (_appSettings == null) return;

            // Save current state, size, and position
            // Important: If window is maximized or minimized, RestoreBounds gives the 'normal' dimensions.
            if (this.WindowState == WindowState.Normal)
            {
                _appSettings.MainWindowLeft = this.Left;
                _appSettings.MainWindowTop = this.Top;
                _appSettings.MainWindowWidth = this.Width;
                _appSettings.MainWindowHeight = this.Height;
            }
            else // Maximized or Minimized
            {
                // RestoreBounds contains the size and position of the window before it was maximized or minimized.
                _appSettings.MainWindowLeft = this.RestoreBounds.Left;
                _appSettings.MainWindowTop = this.RestoreBounds.Top;
                _appSettings.MainWindowWidth = this.RestoreBounds.Width;
                _appSettings.MainWindowHeight = this.RestoreBounds.Height;
            }
            _appSettings.MainWindowState = this.WindowState;
            Debug.WriteLine($"Saved Window Settings: L:{_appSettings.MainWindowLeft} T:{_appSettings.MainWindowTop} W:{_appSettings.MainWindowWidth} H:{_appSettings.MainWindowHeight} S:{_appSettings.MainWindowState}");

            // The actual saving to disk will be handled by VM_Settings.SaveAllSettings() on App.Exit
            // or if you have a dedicated save call here.
            // For now, we're just updating the in-memory _appSettings instance.
        }
    }
}
