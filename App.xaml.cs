// App.xaml.cs
using Autofac;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Splat;
using Splat.Autofac; 
using System.IO;
using System.Reflection;
using System.Windows;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using IContainer = Autofac.IContainer; // Added for Task

namespace NPC_Plugin_Chooser_2
{
    public partial class App : Application
    {
        private SplashScreenWindow _splashScreenWindow;
        private VM_SplashScreen _splashVM;
        private IContainer _container;
        public const string ProgramVersion = "2.0.0"; // Central version definition

        // App constructor should be minimal
        public App()
        {
            // InitializeComponent(); // Usually called by App.g.cs
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            this.Exit += OnApplicationExit;

            // 1. Create and show splash screen
            _splashVM = VM_SplashScreen.InitializeAndShow(App.ProgramVersion, keepTopMost: false);

            _splashVM.UpdateProgress(0, "Initializing application...");

            // 2. Perform main application initialization asynchronously
            _container = await InitializeCoreApplicationAsync(_splashVM);

            // 3. Setup and show MainWindow
            _splashVM.UpdateProgress(95, "Loading main window...");

            MainWindow mainWindow = null;
            try
            {
                // Resolve the MainWindow *View* using the service locator.
                var mainWindowView = Locator.Current.GetService<IViewFor<VM_MainWindow>>();
                if (mainWindowView is MainWindow mw)
                {
                    mainWindow = mw;
                }
                else if (mainWindowView is Window w) // Fallback if it's a Window but not MainWindow type
                {
                     // This case might indicate a registration issue or if you swapped MainWindow for another Window type.
                     // For now, we assume it's a MainWindow or create one.
                     w.Show(); // Show it, but we might not have the specific MainWindow instance.
                     // Attempt to get VM if possible
                     var mainVM = Locator.Current.GetService<VM_MainWindow>();
                     mainVM?.InitializeApplicationState(isStartup: true);
                     _splashVM.UpdateProgress(100, "Application loaded.");
                     await Task.Delay(200); // Keep splash visible briefly
                     _splashScreenWindow.Close();
                     return; // Exit if we showed a generic window and can't proceed with MainWindow specific logic
                }
            }
            catch (Exception ex)
            {
                _splashVM.UpdateProgress(96, $"Error resolving main window: {ex.Message.Split('\n')[0]}");
                // Log detailed error
                System.Diagnostics.Debug.WriteLine($"Error resolving MainWindow: {ex}");
            }

            if (mainWindow == null)
            {
                mainWindow = new MainWindow(); // Fallback: Create directly
            }
            
            mainWindow.Show();

            // Initialize application state after window is shown
            VM_MainWindow mainWindowViewModel = null;
            if (mainWindow.DataContext is VM_MainWindow vmFromDC) {
                mainWindowViewModel = vmFromDC;
            } else if (mainWindow is MainWindow mwInstance) {
                mainWindowViewModel = mwInstance.ViewModel;
            }
            mainWindowViewModel ??= Locator.Current.GetService<VM_MainWindow>();

            mainWindowViewModel?.InitializeApplicationState(isStartup: true);

            _splashVM.UpdateProgress(100, "Application loaded.");
            await Task.Delay(250); // Keep splash visible briefly
            await _splashVM.CloseSplashScreenAsync();
        }

        private async Task<IContainer> InitializeCoreApplicationAsync(VM_SplashScreen splashVM)
        {
            splashVM.UpdateProgress(5, "Configuring type descriptors...");
            TypeDescriptor.AddAttributes(typeof(FormKey), new TypeConverterAttribute(typeof(FormKeyTypeConverter)));

            splashVM.UpdateProgress(10, "Setting up HTTP services...");
            var services = new ServiceCollection();
            services.AddHttpClient();
            
            var builder = new ContainerBuilder();
            builder.Populate(services);

            splashVM.UpdateProgress(15, "Loading settings model...");
            var settingsModel = VM_Settings.LoadSettings(); // Use the static method from your Settings model
            builder.RegisterInstance(settingsModel).AsSelf().SingleInstance();

            // Register VM_SplashScreen so it can be injected
            builder.RegisterInstance(splashVM).As<VM_SplashScreen>().SingleInstance();

            splashVM.UpdateProgress(20, "Registering core components...");
            builder.RegisterType<EnvironmentStateProvider>().AsSelf().SingleInstance();
            builder.RegisterType<Auxilliary>().AsSelf().SingleInstance();
            builder.RegisterType<Patcher>().AsSelf().SingleInstance();
            builder.RegisterType<Validator>().AsSelf().SingleInstance();
            builder.RegisterType<AssetHandler>().AsSelf().SingleInstance();
            builder.RegisterType<BsaHandler>().AsSelf().SingleInstance();
            builder.RegisterType<RecordHandler>().AsSelf().SingleInstance();
            builder.RegisterType<RecordDeltaPatcher>().AsSelf().SingleInstance();
            builder.RegisterType<NpcConsistencyProvider>().AsSelf().SingleInstance();
            builder.RegisterType<NpcDescriptionProvider>().AsSelf().SingleInstance();
            builder.RegisterType<PluginProvider>().AsSelf().SingleInstance();
            builder.RegisterType<SkyPatcherInterface>().AsSelf().SingleInstance();
            builder.RegisterType<EasyNpcTranslator>().AsSelf().SingleInstance();

            splashVM.UpdateProgress(30, "Registering ViewModels...");
            builder.RegisterType<VM_MainWindow>().AsSelf().SingleInstance();
            builder.RegisterType<VM_NpcSelectionBar>().AsSelf().SingleInstance();
            builder.RegisterType<VM_Settings>().AsSelf().SingleInstance(); 
            builder.RegisterType<VM_Run>().AsSelf().SingleInstance();
            builder.RegisterType<VM_Mods>().AsSelf().SingleInstance();   
            builder.RegisterType<VM_FullScreenImage>().AsSelf();
            builder.RegisterType<VM_ModsMenuMugshot>().AsSelf();
            builder.RegisterType<VM_NpcsMenuMugshot>().AsSelf();
            builder.RegisterType<VM_MultiImageDisplay>().AsSelf(); 
            builder.RegisterType<VM_ModSetting>().AsSelf();

            splashVM.UpdateProgress(40, "Registering Views with DI...");
            builder.RegisterType<MainWindow>().As<IViewFor<VM_MainWindow>>();
            builder.RegisterType<NpcsView>().As<IViewFor<VM_NpcSelectionBar>>();
            builder.RegisterType<SettingsView>().As<IViewFor<VM_Settings>>();
            builder.RegisterType<RunView>().As<IViewFor<VM_Run>>();
            builder.RegisterType<ModsView>().As<IViewFor<VM_Mods>>();
            builder.RegisterType<FullScreenImageView>().As<IViewFor<VM_FullScreenImage>>();
            builder.RegisterType<MultiImageDisplayView>().As<IViewFor<VM_MultiImageDisplay>>();


            splashVM.UpdateProgress(50, "Initializing ReactiveUI and Splat...");
            var autofacResolver = builder.UseAutofacDependencyResolver();
            builder.RegisterInstance(autofacResolver);
            Locator.SetLocator(autofacResolver);
            Locator.CurrentMutable.InitializeSplat();
            Locator.CurrentMutable.InitializeReactiveUI();

            splashVM.UpdateProgress(55, "Registering View Factories with Splat...");
            Locator.CurrentMutable.Register(() => new NpcsView(), typeof(IViewFor<VM_NpcSelectionBar>));
            Locator.CurrentMutable.Register(() => new SettingsView(), typeof(IViewFor<VM_Settings>));
            Locator.CurrentMutable.Register(() => new RunView(), typeof(IViewFor<VM_Run>));
            Locator.CurrentMutable.Register(() => new ModsView(), typeof(IViewFor<VM_Mods>));
            Locator.CurrentMutable.Register(() => new FullScreenImageView(), typeof(IViewFor<VM_FullScreenImage>));
            Locator.CurrentMutable.Register(() => new MultiImageDisplayView(), typeof(IViewFor<VM_MultiImageDisplay>));

            splashVM.UpdateProgress(60, "Building DI container...");
            var container = builder.Build();
            autofacResolver.SetLifetimeScope(container);
            
            splashVM.UpdateProgress(61, "Initializing main application services...");
            // Resolve VM_Settings. This will trigger its constructor, which should be lightweight.
            // Then call its InitializeAsync method.
            var settingsViewModel = container.Resolve<VM_Settings>();
            await settingsViewModel.InitializeAsync(); // Pass splashVM implicitly if injected, or explicitly if needed
            
            splashVM.UpdateProgress(90, "Core initialization complete."); // After heavy lifting in InitializeAsync
            return container;
        }
        
        private void OnApplicationExit(object sender, ExitEventArgs e)
        {
            // ## ADD THESE TWO LINES ##
            // Resolve the VM_Settings instance from the container
            var settingsViewModel = _container.Resolve<VM_Settings>();
            settingsViewModel.SaveSettings(); // Call the save method

            // Your existing disposal logic
            var pluginProvider = _container.Resolve<PluginProvider>();
            pluginProvider.Dispose();

            _container.Dispose();
        }
    }
}