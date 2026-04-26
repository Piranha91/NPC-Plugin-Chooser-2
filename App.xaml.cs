// App.xaml.cs
using Autofac;
using CharacterViewer.Rendering;
using CharacterViewer.Rendering.Offscreen;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;
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
using NPC_Plugin_Chooser_2.Themes;
using IContainer = Autofac.IContainer; // Added for Task

namespace NPC_Plugin_Chooser_2
{
    public partial class App : Application
    {
        private SplashScreenWindow _splashScreenWindow;
        private IContainer _container;
        public const string ProgramVersion = "2.1.5"; // Central version definition

        // App constructor should be minimal
        public App()
        {
            // InitializeComponent(); // Usually called by App.g.cs
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            // Check for file-based startup logging trigger before anything else
            StartupLogger.InitializeFromFileTrigger();
            StartupLogger.Log("Application starting");

            using (ContextualPerformanceTracer.Trace("App.OnStartup"))
            {
                base.OnStartup(e);
                this.Exit += OnApplicationExit;

                StartupLogger.Log("Showing splash screen");
                var splashVM = VM_SplashScreen.InitializeAndShow(App.ProgramVersion, keepTopMost: false);
                splashVM.UpdateProgress(0, "Initializing application...");

                try
                {
                    _container = await InitializeCoreApplicationAsync(splashVM);
                }
                catch (Exception ex)
                {
                    StartupLogger.Log($"Fatal error during startup: {ex.Message}", "ERROR");
                    splashVM?.ShowMessagesOnClose("An error occured during startup: " + Environment.NewLine + Environment.NewLine + ExceptionLogger.GetExceptionStack(ex));
                }

                splashVM.UpdateProgress(95, "Loading main window...");

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
                        splashVM.UpdateProgress(100, "Application loaded.");
                        await Task.Delay(200);
                        await splashVM.CloseSplashScreenAsync();
                        return; // Skip further logic since it's not the real MainWindow
                    }
                }
                catch (Exception ex)
                {
                    splashVM.UpdateProgress(96, $"Error resolving main window: {ex.Message.Split('\n')[0]}");
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

                StartupLogger.Log("Initializing application state");
                using (ContextualPerformanceTracer.Trace("App.OnStartup.InitializeApplicationState"))
                {
                    mainWindowViewModel?.InitializeApplicationState(isStartup: true);
                }

                splashVM.UpdateProgress(100, "Application loaded.");

                // If mods folder is blank (e.g. first launch), defer log completion so that
                // initialization after the user selects a mods folder is also captured.
                var settingsModel = _container?.Resolve<Settings>();
                if (StartupLogger.IsEnabled && string.IsNullOrEmpty(settingsModel?.ModsFolder))
                {
                    StartupLogger.DeferCompletion();
                }
                else
                {
                    StartupLogger.Complete();
                }

                await Task.Delay(250);
                await splashVM.CloseSplashScreenAsync();
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

            StartupLogger.LogPhase("Loading Settings");
            splashVM.UpdateProgress(15, "Loading settings model...");
            StartupLogger.Log("Loading settings from disk");
            var settingsModel = VM_Settings.LoadSettings(); // Use the static method from your Settings model
            // Enable startup logging from settings if not already enabled by file trigger
            StartupLogger.InitializeFromSettings(settingsModel.LogStartup);
            StartupLogger.Log("Settings loaded successfully");
            // Apply theme: prefer saved ThemeName, fall back to IsDarkMode for backward compat
            if (!string.IsNullOrEmpty(settingsModel.ThemeName))
                ThemeManager.ApplyTheme(settingsModel.ThemeName);
            else
                ThemeManager.ApplyTheme(settingsModel.IsDarkMode);
            // Run the update handler to migrate settings before they are used by the application.
            splashVM.UpdateProgress(16, "Checking for setting updates...");
            StartupLogger.Log("Running update handler");
            var updateHandler = new UpdateHandler(settingsModel);
            updateHandler.InitialCheckForUpdatesAndPatch();
            builder.RegisterInstance(settingsModel).AsSelf().SingleInstance();

            StartupLogger.LogPhase("Dependency Injection Setup");
            splashVM.UpdateProgress(20, "Registering core components...");
            StartupLogger.Log("Registering core components");
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
            builder.RegisterType<FaceFinderClient>().AsSelf().SingleInstance();
            builder.RegisterType<PortraitCreator>().AsSelf().SingleInstance();
            builder.RegisterType<MasterAnalyzer>().AsSelf().SingleInstance();

            // CharacterViewer.Rendering host adapters — bind NPC2's concrete services
            // behind the renderer's interfaces so the renderer never sees Mutagen
            // or NPC2-specific types directly.
            builder.RegisterType<NpcChooserViewerLoggerAdapter>().As<ICharacterViewerLogger>().SingleInstance();
            builder.RegisterType<NpcChooserSettingsAdapter>().As<ICharacterViewerSettings>().SingleInstance();
            builder.RegisterType<NpcChooserDataFolderAdapter>().As<IDataFolderProvider>().SingleInstance();
            builder.RegisterType<NpcChooserBsaProviderAdapter>().As<IBsaArchiveProvider>().SingleInstance();
            builder.RegisterType<NpcChooserNpcMeshDataSourceAdapter>().As<INpcMeshDataSource>().SingleInstance();
            builder.RegisterType<WpfDispatcherMarshaller>().As<IRenderThreadMarshaller>().SingleInstance();

            // CharacterViewer.Rendering leaf services.
            builder.RegisterType<NpcMeshResolver>().AsSelf().SingleInstance();
            builder.RegisterType<CharacterViewerLogGate>().AsSelf().SingleInstance();
            builder.RegisterType<GameAssetResolver>().AsSelf().SingleInstance();
            builder.RegisterType<BsdFileParser>().AsSelf().SingleInstance();
            builder.RegisterType<BodyTriFileParser>().AsSelf().SingleInstance();
            builder.RegisterType<BodySlideDeformer>().AsSelf().SingleInstance();
            builder.RegisterType<CharacterPreviewCache>().AsSelf().SingleInstance();
            builder.RegisterType<VM_CharacterViewer>().AsSelf();  // transient — one per preview window

            builder.RegisterType<InternalMugshotGenerator>().AsSelf().SingleInstance();
            builder.RegisterType<MugshotStalenessChecker>().AsSelf().SingleInstance();

            // Offscreen renderer is a managed singleton — its GameWindow + FBO
            // are amortized across many mugshot renders. The factory must be
            // called from the WPF UI thread (GLFW init constraint); see the
            // eager resolution below in InitializeCoreApplicationAsync.
            builder.Register(c => OffscreenRendererFactory.Create(
                    c.Resolve<CharacterPreviewCache>(),
                    c.Resolve<BodySlideDeformer>(),
                    c.Resolve<BsdFileParser>(),
                    c.Resolve<BodyTriFileParser>(),
                    c.Resolve<GameAssetResolver>(),
                    c.Resolve<ICharacterViewerSettings>(),
                    c.Resolve<CharacterViewerLogGate>(),
                    c.Resolve<ICharacterViewerLogger>()))
                .As<IOffscreenRenderer>()
                .SingleInstance();

            splashVM.UpdateProgress(30, "Registering ViewModels...");
            builder.RegisterType<VM_MainWindow>().AsSelf().SingleInstance();
            builder.RegisterType<VM_NpcSelectionBar>().AsSelf().SingleInstance();
            builder.RegisterType<VM_Settings>().AsSelf().SingleInstance(); 
            builder.RegisterType<VM_Run>().AsSelf().SingleInstance();
            builder.RegisterType<VM_Mods>().AsSelf().SingleInstance();  
            builder.RegisterType<VM_Summary>().AsSelf().SingleInstance();
            builder.RegisterType<VM_FavoriteFaces>().AsSelf();
            builder.RegisterType<VM_FullScreenImage>().AsSelf();
            builder.RegisterType<VM_ModsMenuMugshot>().AsSelf();
            builder.RegisterType<VM_NpcsMenuMugshot>().AsSelf();
            builder.RegisterType<VM_SummaryMugshot >().AsSelf();
            builder.RegisterType<VM_MultiImageDisplay>().AsSelf(); 
            builder.RegisterType<VM_ModSetting>().AsSelf();
            builder.RegisterType<VM_ModFaceFinderLinker>().AsSelf();
            builder.RegisterType<VM_InternalMugshotPreview>().AsSelf();
            builder.RegisterType<ImagePacker>().AsSelf().SingleInstance();
            
            builder.RegisterType<EventLogger>().AsSelf().SingleInstance();

            splashVM.UpdateProgress(40, "Registering Views with DI...");
            builder.RegisterType<MainWindow>().As<IViewFor<VM_MainWindow>>();
            builder.RegisterType<NpcsView>().As<IViewFor<VM_NpcSelectionBar>>();
            builder.RegisterType<SettingsView>().As<IViewFor<VM_Settings>>();
            builder.RegisterType<RunView>().As<IViewFor<VM_Run>>();
            builder.RegisterType<SummaryView>().As<IViewFor<VM_Summary>>();
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
            Locator.CurrentMutable.Register(() => new MainWindow(), typeof(IViewFor<VM_MainWindow>));
            Locator.CurrentMutable.Register(() => new NpcsView(), typeof(IViewFor<VM_NpcSelectionBar>));
            Locator.CurrentMutable.Register(() => new SettingsView(), typeof(IViewFor<VM_Settings>));
            Locator.CurrentMutable.Register(() => new RunView(), typeof(IViewFor<VM_Run>));
            Locator.CurrentMutable.Register(() => new SummaryView(), typeof(IViewFor<VM_Summary>));
            Locator.CurrentMutable.Register(() => new ModsView(), typeof(IViewFor<VM_Mods>));
            Locator.CurrentMutable.Register(() => new FullScreenImageView(), typeof(IViewFor<VM_FullScreenImage>));
            Locator.CurrentMutable.Register(() => new MultiImageDisplayView(), typeof(IViewFor<VM_MultiImageDisplay>));

            splashVM.UpdateProgress(60, "Building DI container...");
            StartupLogger.Log("Building DI container");
            var container = builder.Build();
            autofacResolver.SetLifetimeScope(container);
            
            StartupLogger.LogPhase("Application Initialization");
            splashVM.UpdateProgress(65, "Initializing main application services...");
            VM_Settings? settingsViewModel;
            StartupLogger.Log("Resolving VM_Settings");
            using (ContextualPerformanceTracer.Trace("InitializeCoreApplicationAsync.ResolveSettingsVM"))
            {
                settingsViewModel = container.Resolve<VM_Settings>();
            }

            StartupLogger.Log("Starting VM_Settings.InitializeAsync");
            await settingsViewModel.InitializeAsync(splashVM); // Pass splashVM implicitly if injected, or explicitly if needed
            StartupLogger.Log("VM_Settings.InitializeAsync complete");

            StartupLogger.Log("Initializing PortraitCreator");
            var portraitCreator = container.Resolve<PortraitCreator>();
            await portraitCreator.InitializeAsync();
            StartupLogger.Log("PortraitCreator initialized");

            // CharacterViewer.Rendering bundled-DLL version check. Bump the
            // required version when this build starts depending on an API
            // introduced in a newer release of CharacterViewer.Rendering.
            // Policy is documented at the top of CharacterViewerRendering.cs:
            // Major = breaking, Minor = additive, Patch = bugfix.
            //
            // 1.1.0 added OffscreenRenderRequest.AdditionalDataFolders +
            // VM_CharacterViewer.AdditionalDataFolders (consumed by both the
            // mugshot generator and the live preview), plus the per-render
            // GL context release that fixes "WGL: Failed to make context
            // current: The requested resource is in use" on parallel renders.
            // 1.2.0 added the strict two-phase scope chain
            // (OffscreenRenderRequest.AdditionalScopes + RenderScope +
            // IBsaArchiveProvider.TryLocateInScopedBsa) so the active mod's
            // BSAs win over vanilla when both ship the same relative path
            // (e.g. mod-overridden FaceGen NIFs).
            var requiredViewerVersion = new Version(1, 2, 0);
            if (CharacterViewerRendering.Version < requiredViewerVersion)
            {
                StartupLogger.Log(
                    $"Bundled CharacterViewer.Rendering is v{CharacterViewerRendering.Version}; " +
                    $"this build of NPC2 expects v{requiredViewerVersion} or newer. " +
                    "Some Internal-renderer features may be unavailable.",
                    "WARN");
            }

            // GLFW requires the OffscreenRenderer to be constructed on the WPF UI
            // thread. We're already on it here; eagerly resolve so the GameWindow
            // is built now and subsequent background-thread render calls succeed.
            // Per-render context release is handled inside the rendering library
            // (CharacterViewer.Rendering 1.1.0+) so we no longer need to detach
            // here ourselves.
            try
            {
                StartupLogger.Log("Initializing CharacterViewer offscreen renderer");
                container.Resolve<IOffscreenRenderer>();
                StartupLogger.Log("CharacterViewer offscreen renderer initialized");
            }
            catch (Exception ex)
            {
                StartupLogger.Log("OffscreenRenderer initialization failed: " + ex.Message, "WARN");
            }

            // Pre-warm the BSA reader cache on a background thread. The Internal
            // renderer's broadcast-lookup adapter (NpcChooserBsaProviderAdapter)
            // needs every mod's archives indexed; doing this lazily on the first
            // mugshot generation would block the calling thread for several
            // seconds with hundreds of mods. Fire-and-forget here so the work
            // overlaps with the rest of startup.
            try
            {
                var bsaProvider = container.Resolve<IBsaArchiveProvider>();
                _ = Task.Run(() =>
                {
                    try { bsaProvider.EnsureAllArchivesOpened(); }
                    catch (Exception ex)
                    {
                        StartupLogger.Log("Background BSA pre-warm failed: " + ex.Message, "WARN");
                    }
                });
            }
            catch (Exception ex)
            {
                StartupLogger.Log("Could not schedule BSA pre-warm: " + ex.Message, "WARN");
            }

            var modsViewModel = container.Resolve<VM_Mods>();
            var npcsViewModel = container.Resolve<VM_NpcSelectionBar>();
            var pluginProvider = container.Resolve<PluginProvider>();
            var aux = container.Resolve<Auxilliary>();
            var environmentProvider = container.Resolve<EnvironmentStateProvider>();
            StartupLogger.Log("Running final update checks");
            await updateHandler.FinalCheckForUpdatesAndPatch(npcsViewModel, modsViewModel, pluginProvider, aux, environmentProvider, splashVM);
            
            splashVM.UpdateProgress(90, "Core initialization complete."); // After heavy lifting in InitializeAsync
            return container;
        }
        
        private void OnApplicationExit(object sender, ExitEventArgs e)
        {
            // Resolve the VM_Settings instance from the container
            var settingsViewModel = _container.Resolve<VM_Settings>();
            settingsViewModel.SaveSettings(); // Call the save method
            
            // Save the Portrait Creator output log
            var portraitCreator = _container.Resolve<PortraitCreator>();
            portraitCreator.SaveOutputLog();
            
            // NEW: Clean up the temporary extraction directory
            try
            {
                string tempPath = portraitCreator.TempExtractionPath;
                if (Directory.Exists(tempPath))
                {
                    Directory.Delete(tempPath, recursive: true);
                }
            }
            catch (Exception ex)
            {
                // Log the error, but don't prevent the app from closing.
                System.Diagnostics.Debug.WriteLine($"Failed to clean up temporary directory: {ex.Message}");
            }

            // Dispose the offscreen renderer's GL context before the container goes away.
            try
            {
                if (_container.TryResolve<IOffscreenRenderer>(out var offscreenRenderer))
                {
                    offscreenRenderer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to dispose OffscreenRenderer: {ex.Message}");
            }

            // Your existing disposal logic
            var pluginProvider = _container.Resolve<PluginProvider>();
            pluginProvider.Dispose();

            _container.Dispose();
        }
    }
}