using Autofac;
using Autofac.Extensions.DependencyInjection;
using CharacterViewer.Rendering;
using CharacterViewer.Rendering.Offscreen;
using Microsoft.Extensions.DependencyInjection;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Memory;

/// <summary>
/// Stands up the <em>frontend</em> half of NPC2's Autofac graph — the view-model closure rooted at
/// <see cref="VM_NpcSelectionBar"/> / <see cref="VM_Mods"/> plus every backend service and
/// CharacterViewer adapter those VMs transitively need — around a supplied
/// <see cref="EnvironmentStateProvider"/> and <see cref="Settings"/>. It mirrors the registrations in
/// <c>App.xaml.cs</c> (<c>InitializeCoreApplicationAsync</c>) verbatim, with two deliberate omissions:
/// <list type="bullet">
///   <item>the WPF <c>Views</c> + the Splat/ReactiveUI view locator wiring — headless VM driving never
///     resolves a <c>IViewFor&lt;&gt;</c>, so registering views would only pull XAML into the graph; and</item>
///   <item>the real <see cref="IOffscreenRenderer"/>, which is replaced by <see cref="StubOffscreenRenderer"/>
///     by default so no GLFW window / GPU context is created. Pass <c>useRealRenderer: true</c> to register
///     the production <c>OffscreenRendererFactory</c> instead (needed by the mugshot-autogeneration memory
///     diagnostic; that path requires the WPF UI thread and a GPU, so it is opt-in / local-only).</item>
/// </list>
///
/// <para>This is the "drive and manipulate the UI" seam the memory tests use: it exposes the real singleton
/// VMs so a test can move <see cref="VM_NpcSelectionBar.SelectedNpc"/> across NPCs and exercise the exact
/// tile build/dispose path (<c>CurrentNpcAppearanceMods</c>) that commit 2312cb6 fixed. Construct and drive
/// it on the STA thread (via <c>WpfStaFixture.RunOnStaAsync</c>) — the VMs are full WPF/ReactiveUI citizens
/// with thread affinity.</para>
/// </summary>
public sealed class FrontendVmHarness : IDisposable
{
    public IContainer Container { get; }
    public EnvironmentStateProvider Environment { get; }
    public Settings Settings { get; }

    public FrontendVmHarness(EnvironmentStateProvider environment, Settings settings, bool useRealRenderer = false)
    {
        Environment = environment;
        Settings = settings;

        var builder = new ContainerBuilder();

        // IHttpClientFactory (NpcDescriptionProvider / wiki lookups). App.xaml.cs wires this via a
        // ServiceCollection.AddHttpClient() populated into Autofac; mirror that here.
        var services = new ServiceCollection();
        services.AddHttpClient();
        builder.Populate(services);

        builder.RegisterInstance(settings).AsSelf().SingleInstance();
        builder.RegisterInstance(environment).AsSelf().SingleInstance();

        // --- Backend services (mirror of App.xaml.cs) ---
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
        builder.RegisterType<OutputValidator>().AsSelf().SingleInstance();
        builder.RegisterType<EasyNpcTranslator>().AsSelf().SingleInstance();
        builder.RegisterType<FaceFinderClient>().AsSelf().SingleInstance();
        builder.RegisterType<PortraitCreator>().AsSelf().SingleInstance();
        builder.RegisterType<MasterAnalyzer>().AsSelf().SingleInstance();

        // --- CharacterViewer host adapters (bind NPC2 services behind the renderer's interfaces) ---
        builder.RegisterType<NpcChooserViewerLoggerAdapter>().As<ICharacterViewerLogger>().SingleInstance();
        builder.RegisterType<NpcChooserSettingsAdapter>().As<ICharacterViewerSettings>().SingleInstance();
        builder.RegisterType<NpcChooserDataFolderAdapter>().As<IDataFolderProvider>().SingleInstance();
        builder.RegisterType<NpcChooserBsaProviderAdapter>().As<IBsaArchiveProvider>().SingleInstance();
        builder.RegisterType<NpcChooserNpcMeshDataSourceAdapter>().As<INpcMeshDataSource>().SingleInstance();
        builder.RegisterType<WpfDispatcherMarshaller>().As<IRenderThreadMarshaller>().SingleInstance();

        // --- CharacterViewer support services + mugshot generators the VMs depend on ---
        builder.RegisterType<NpcMeshResolver>().AsSelf().SingleInstance();
        builder.RegisterType<CharacterViewerLogGate>().AsSelf().SingleInstance();
        builder.RegisterType<GameAssetResolver>().AsSelf().SingleInstance();
        builder.RegisterType<BsdFileParser>().AsSelf().SingleInstance();
        builder.RegisterType<BodyTriFileParser>().AsSelf().SingleInstance();
        builder.RegisterType<BodySlideDeformer>().AsSelf().SingleInstance();
        builder.RegisterType<CharacterPreviewCache>().AsSelf().SingleInstance();
        builder.RegisterType<VM_CharacterViewer>().AsSelf();

        builder.RegisterType<InternalMugshotGenerator>().AsSelf().SingleInstance();
        builder.RegisterType<BatchMugshotGenerator>().AsSelf().SingleInstance();
        builder.RegisterType<MugshotStalenessChecker>().AsSelf().SingleInstance();
        builder.RegisterType<GeneratedMugshotTracker>().AsSelf().SingleInstance();
        builder.RegisterType<FaceFinderCacheTracker>().AsSelf().SingleInstance();
        builder.RegisterType<MeshSurveyRunner>().AsSelf().SingleInstance();
        builder.RegisterType<FaceGenAnalysisCache>().AsSelf().SingleInstance();
        builder.RegisterType<FaceGenConsistencyAnalyzer>().AsSelf().SingleInstance();

        if (useRealRenderer)
        {
            // Production renderer: builds a GLFW window + FBO. Must be resolved on the WPF UI thread.
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
        }
        else
        {
            builder.RegisterInstance(new StubOffscreenRenderer()).As<IOffscreenRenderer>().SingleInstance();
        }

        // --- View Models (mirror of App.xaml.cs; Views intentionally omitted) ---
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
        builder.RegisterType<VM_SummaryMugshot>().AsSelf();
        builder.RegisterType<VM_MultiImageDisplay>().AsSelf();
        builder.RegisterType<VM_ModSetting>().AsSelf();
        builder.RegisterType<VM_ModFaceFinderLinker>().AsSelf();
        builder.RegisterType<VM_InternalMugshotPreview>().AsSelf();
        builder.RegisterType<VM_FullScreen3DPreview>().AsSelf();
        builder.RegisterType<ImagePacker>().AsSelf().SingleInstance();
        builder.RegisterType<EventLogger>().AsSelf().SingleInstance();

        Container = builder.Build();
    }

    /// <summary>
    /// Points ReactiveUI's main-thread scheduler at the current STA dispatcher — exactly what
    /// <c>App.xaml.cs</c> does at startup (<c>RxSchedulers.MainThreadScheduler = new DispatcherScheduler(...)</c>).
    /// The browse flow's tile rebuild is an async reactive pipeline that hops off-thread and marshals its
    /// result back via this scheduler; the <c>ImmediateScheduler</c> that <see cref="StaticStateGuard"/>
    /// installs by default cannot marshal it back, so the rebuild never completes. Call inside
    /// <c>RunOnStaAsync</c>, after constructing a <c>StaticStateGuard(immediateSchedulers: false)</c> (which
    /// snapshots the real scheduler and restores it on dispose).
    /// </summary>
    public static void InstallStaMainThreadScheduler() =>
        ReactiveUI.RxSchedulers.MainThreadScheduler =
            new System.Reactive.Concurrency.DispatcherScheduler(
                System.Windows.Threading.Dispatcher.CurrentDispatcher);

    public VM_Mods ModsVm => Container.Resolve<VM_Mods>();
    public VM_NpcSelectionBar NpcSelectionBar => Container.Resolve<VM_NpcSelectionBar>();
    public NpcConsistencyProvider ConsistencyProvider => Container.Resolve<NpcConsistencyProvider>();

    /// <summary>
    /// Eagerly resolves the <see cref="IOffscreenRenderer"/> (with <c>useRealRenderer</c> this builds the
    /// GLFW window + FBO — must be called on the WPF UI thread) so its one-time allocation isn't attributed
    /// to a later browse iteration's memory delta. Returns the singleton.
    /// </summary>
    public IOffscreenRenderer EnsureRendererResolved() => Container.Resolve<IOffscreenRenderer>();

    /// <summary>
    /// Reproduces the startup population sequence the app runs inside
    /// <c>VM_Settings.InitializeAsync</c> (VM_Settings.cs steps 1–2): populate the mod list, sync the
    /// discovered mods into <c>Settings.ModSettings</c>, then initialize the NPC selection bar so
    /// <see cref="VM_NpcSelectionBar.AllNpcs"/> is filled. Call inside <c>RunOnStaAsync</c>.
    /// </summary>
    public async Task DriveStartupPopulationAsync()
    {
        await ModsVm.PopulateModSettingsAsync(null);
        ModsVm.SaveModSettingsToModel();
        await NpcSelectionBar.InitializeAsync(null);
    }

    /// <summary>
    /// Selects <paramref name="npc"/> on the bar and pumps the STA dispatcher until the async rebuild of
    /// <see cref="VM_NpcSelectionBar.CurrentNpcAppearanceMods"/> has swapped in a fresh collection (a new
    /// reference distinct from the one shown for the previous NPC), or a timeout elapses. Returns that
    /// freshly-built tile collection. Call on the STA thread — awaiting <see cref="Task.Delay(int)"/> lets
    /// the dispatcher drain the reactive pipeline's continuations. This is the "click an NPC" primitive the
    /// memory tests drive the browse flow with.
    /// </summary>
    public async Task<System.Collections.ObjectModel.ObservableCollection<VM_NpcsMenuMugshot>?> SelectAndWaitAsync(
        VM_NpcsMenuSelection npc, int timeoutMs = 8000)
    {
        var bar = NpcSelectionBar;
        var previous = bar.CurrentNpcAppearanceMods;
        bar.SelectedNpc = npc;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(15);
            var current = bar.CurrentNpcAppearanceMods;
            if (current != null && !ReferenceEquals(current, previous))
                return current;
        }
        return bar.CurrentNpcAppearanceMods;
    }

    public void Dispose() => Container.Dispose();

    /// <summary>
    /// A no-GPU <see cref="IOffscreenRenderer"/> for the leak/browse tests: satisfies the DI graph without
    /// creating a GLFW window. It returns a 1×1 transparent PNG / a 4-byte BGRA buffer so any incidental
    /// generation call the browse flow triggers is a cheap no-op rather than a GL crash. The memory tests
    /// that exercise the browse path never render (they load curated mugshots from disk); the diagnostic
    /// that does render uses the real renderer via <c>useRealRenderer: true</c>.
    /// </summary>
    public sealed class StubOffscreenRenderer : IOffscreenRenderer
    {
        // Minimal valid 1×1 transparent PNG.
        private static readonly byte[] OnePixelPng =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82,
        };

        public Task<byte[]> RenderToPngAsync(OffscreenRenderRequest request) => Task.FromResult(OnePixelPng);
        public Task<byte[]> RenderToBgra32Async(OffscreenRenderRequest request) => Task.FromResult(new byte[4]);
        public void InvalidateCaches() { }
        public Task PrewarmAsync(OffscreenRenderRequest request) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
