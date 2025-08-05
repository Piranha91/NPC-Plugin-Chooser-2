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
            using (ContextualPerformanceTracer.Trace("App.OnStartup"))
            {
                base.OnStartup(e);
                this.Exit += OnApplicationExit;

                _splashVM = VM_SplashScreen.InitializeAndShow(App.ProgramVersion, keepTopMost: false);
                _splashVM.UpdateProgress(0, "Initializing application...");

                _container = await InitializeCoreApplicationAsync(_splashVM);

                _splashVM.UpdateProgress(95, "Loading main window...");

                Window mainWindow = null;

                try
                {
                    var mainWindowView = Locator.Current.GetService<IViewFor<VM_MainWindow>>();
                    if (mainWindowView is MainWindow typedMainWindow)
                    {
                        mainWindow = typedMainWindow;
                    }
                    else if (mainWindowView is Window genericWindow)
                    {
                        genericWindow.Show();
                        _splashVM.UpdateProgress(100, "Application loaded.");
                        await Task.Delay(200);
                        await _splashVM.CloseSplashScreenAsync();
                        return; // Skip further logic since it's not the real MainWindow
                    }
                }
                catch (Exception ex)
                {
                    _splashVM.UpdateProgress(96, $"Error resolving main window: {ex.Message.Split('\n')[0]}");
                    System.Diagnostics.Debug.WriteLine($"Error resolving MainWindow: {ex}");
                }

                if (mainWindow == null)
                {
                    mainWindow = new MainWindow(); // Fallback
                }

                mainWindow.Show();

                // Attempt to get ViewModel from DataContext or container
                var mainWindowViewModel =
                    mainWindow.DataContext as VM_MainWindow ??
                    (mainWindow as MainWindow)?.ViewModel ??
                    Locator.Current.GetService<VM_MainWindow>();

                using (ContextualPerformanceTracer.Trace("App.OnStartup.InitializeApplicationState"))
                {
                    mainWindowViewModel?.InitializeApplicationState(isStartup: true);
                }

                _splashVM.UpdateProgress(100, "Application loaded.");
                await Task.Delay(250);
                await _splashVM.CloseSplashScreenAsync();
            }
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
            VM_Settings? settingsViewModel;
            using (ContextualPerformanceTracer.Trace("InitializeCoreApplicationAsync.ResolveSettingsVM"))
            {
                settingsViewModel = container.Resolve<VM_Settings>();
            }

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