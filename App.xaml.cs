// App.xaml.cs
using Autofac;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat;
using Splat.Autofac; // Ensure this using directive is present
using System.IO;
using System.Reflection;
using System.Windows;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Collections.Generic; // Added for Dictionary
using System;
using System.ComponentModel; // Added for Exception, Path, etc.
using System.Linq;
using System.Net.Http;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection; // Added for LINQ methods if needed

namespace NPC_Plugin_Chooser_2
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            // Set up Autofac
            var builder = new ContainerBuilder();
            

            // Register Services & Models (as singletons where appropriate)
            // Load/Save Settings (Consider implementing persistence here)
            TypeDescriptor.AddAttributes(typeof(FormKey), new TypeConverterAttribute(typeof(FormKeyTypeConverter))); // Required for JSON deserialization of Dictionary<FormKey, anything>

            // --- Setup HttpClientFactory using Microsoft.Extensions.DependencyInjection ---
            // 1. Create a ServiceCollection
            var services = new ServiceCollection();
            // 2. Register HttpClientFactory and related services
            services.AddHttpClient();
            // 3. Populate the Autofac builder with the registrations from ServiceCollection
            builder.Populate(services);
            // --- End HttpClientFactory Setup ---
            
            
            var settings = VM_Settings.LoadSettings(); // Replace with actual loading logic if needed
            builder.RegisterInstance(settings).AsSelf().SingleInstance();

            builder.RegisterType<EnvironmentStateProvider>().AsSelf().SingleInstance();
            builder.RegisterType<Auxilliary>().AsSelf().SingleInstance();
            builder.RegisterType<NpcConsistencyProvider>().AsSelf().SingleInstance(); // Added for central selection management
            builder.RegisterType<NpcDescriptionProvider>().AsSelf().SingleInstance(); // Added for central NPC Description management

            // Register ViewModels
            builder.RegisterType<VM_MainWindow>().AsSelf().SingleInstance(); // Main window is often a singleton
            builder.RegisterType<VM_NpcSelectionBar>().AsSelf().SingleInstance(); // Keep NPC list state
            builder.RegisterType<VM_Settings>().AsSelf().SingleInstance(); // Keep settings state
            builder.RegisterType<VM_Run>().AsSelf().SingleInstance(); // Keep run state
            builder.RegisterType<VM_Mods>().AsSelf().SingleInstance(); // *** NEW: Register Mods VM ***
            // Register VM_FullScreenImage transiently as it's created on demand
            builder.RegisterType<VM_FullScreenImage>().AsSelf();
            // Register VM_ModsMenuMugshot transiently (created on demand)
            builder.RegisterType<VM_ModsMenuMugshot>().AsSelf();
            // Register VM_AppearnceMod transiently (created on demand)
            builder.RegisterType<VM_NpcsMenuMugshot>().AsSelf();

            // Register Views using Splat's IViewFor convention
            // This allows resolving Views via service locator if needed, but primarily helps ReactiveUI internals
            builder.RegisterType<MainWindow>().As<IViewFor<VM_MainWindow>>();
            builder.RegisterType<NpcsView>().As<IViewFor<VM_NpcSelectionBar>>();
            builder.RegisterType<SettingsView>().As<IViewFor<VM_Settings>>();
            builder.RegisterType<RunView>().As<IViewFor<VM_Run>>();
            builder.RegisterType<ModsView>().As<IViewFor<VM_Mods>>(); // *** NEW: Register Mods View ***
            builder.RegisterType<FullScreenImageView>().As<IViewFor<VM_FullScreenImage>>(); // For full screen view

            // Use Autofac for Splat Dependency Resolution
            var autofacResolver = builder.UseAutofacDependencyResolver();

            // Register the resolver in Autofac so it can be later resolved
            builder.RegisterInstance(autofacResolver);

            // Configure Splat to use the Autofac resolver
            // ModeDetector.OverrideModeDetector(Mode.Run); // Obsolete - Splat should detect mode automatically
            Locator.SetLocator(autofacResolver); // Configure Splat globally

            // Initialize ReactiveUI specifics AFTER setting the locator
            Locator.CurrentMutable.InitializeSplat();
            Locator.CurrentMutable.InitializeReactiveUI();

            // Register Views with ReactiveUI's ViewLocator (needed for ViewModelViewHost, DataTemplates etc.)
            // These factory registrations tell ReactiveUI how to create a View instance when it sees a specific ViewModel type.
            Locator.CurrentMutable.Register(() => new NpcsView(), typeof(IViewFor<VM_NpcSelectionBar>));
            Locator.CurrentMutable.Register(() => new SettingsView(), typeof(IViewFor<VM_Settings>));
            Locator.CurrentMutable.Register(() => new RunView(), typeof(IViewFor<VM_Run>));
            Locator.CurrentMutable.Register(() => new ModsView(), typeof(IViewFor<VM_Mods>)); // *** NEW: Register Mods View Factory ***
            Locator.CurrentMutable.Register(() => new FullScreenImageView(), typeof(IViewFor<VM_FullScreenImage>));
            Locator.CurrentMutable.Register(() => new MultiImageDisplayView(), typeof(IViewFor<VM_MultiImageDisplay>));

            var container = builder.Build();
            autofacResolver.SetLifetimeScope(container);

            // Hook into Exit event to save settings (including ModSettings)
            // Ensure the SaveModSettingsToModel is called before VM_Settings saves the main settings file
            this.Exit += (s, e) =>
            {
                 var modsVm = Locator.Current.GetService<VM_Mods>();
                 var settingsVm = Locator.Current.GetService<VM_Settings>(); // To trigger its save maybe? No, VM_Settings already hooks Exit.

                 if (modsVm != null)
                 {
                      try
                      {
                          modsVm.SaveModSettingsToModel(); // Saves VM list back to Settings model instance
                          // VM_Settings saving logic should already be hooked to App.Exit,
                          // so it will save the updated Settings model afterwards.
                      }
                      catch (Exception ex)
                      {
                           System.Diagnostics.Debug.WriteLine($"Error saving Mod Settings on exit: {ex.Message}");
                           // Optionally show an error message to the user
                      }
                 }
            };

            // Resolve the main window view model to start the application
            // The MainWindow's DataContext will be set automatically by ReactiveUI's View magic
            // if MainWindow inherits ReactiveWindow<VM_MainWindow> and its VM is resolved correctly.
            // We don't strictly need to resolve the VM here; the View's constructor/WhenActivated handles it.
            // var mainWindowViewModel = container.Resolve<VM_MainWindow>();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // Resolve the MainWindow *View* using the service locator.
            // ReactiveUI's ReactiveWindow base class or ViewLocator mechanisms
            // will typically handle associating the correct ViewModel.
            var mainWindow = Locator.Current.GetService<IViewFor<VM_MainWindow>>() as Window;

            // Fallback or alternative: Just create the window directly.
            // If MainWindow resolves its VM correctly in its constructor using Splat, this works fine.
            if (mainWindow == null)
            {
                mainWindow = new MainWindow();
            }

            if (mainWindow != null)
            {
                mainWindow.Show();

                // Initialize application state after window is shown
                VM_MainWindow? mainWindowViewModel = null;
                if (mainWindow.DataContext is VM_MainWindow vm) {
                    mainWindowViewModel = vm;
                } else if (mainWindow is MainWindow mwInstance) { // Specific cast if DataContext is not yet VM_MainWindow
                    mainWindowViewModel = mwInstance.ViewModel;
                }
                
                // Fallback if still null (e.g. DataContext set later or different view type)
                mainWindowViewModel ??= Locator.Current.GetService<VM_MainWindow>();

                mainWindowViewModel?.InitializeApplicationState(isStartup: true);
            }
            else
            {
                // Handle error - main window view couldn't be resolved or created
                MessageBox.Show("Fatal Error: Could not resolve or create the MainWindow view.", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                // Consider Application.Current.Shutdown();
            }
        }
    }
}