using Autofac;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Builds the backend half of NPC2's Autofac graph (the closure rooted at <see cref="Patcher"/>
/// and <see cref="Validator"/>) around a supplied <see cref="EnvironmentStateProvider"/> and
/// <see cref="Settings"/>. Mirrors the backend registrations in <c>App.xaml.cs</c> but omits the
/// renderer/adapters/ViewModels — <c>AssetHandler</c>'s <c>Lazy&lt;VM_Run&gt;</c> dependency is
/// stubbed (it is only dereferenced on UI-feedback paths the headless tests don't take).
///
/// <para>Use <see cref="Invalid"/> for guard/early-return tests (no game needed) or pass a real
/// provider from <see cref="NpcChooserTestEnvironment.TryBuild"/> for live runs.</para>
/// </summary>
public sealed class NpcChooserHarness : IDisposable
{
    public IContainer Container { get; }
    public EnvironmentStateProvider Environment { get; }
    public Settings Settings { get; }

    public NpcChooserHarness(EnvironmentStateProvider environment, Settings settings)
    {
        Environment = environment;
        Settings = settings;

        var builder = new ContainerBuilder();
        builder.RegisterInstance(environment).AsSelf().SingleInstance();
        builder.RegisterInstance(settings).AsSelf().SingleInstance();
        builder.RegisterType<Auxilliary>().AsSelf().SingleInstance();
        builder.RegisterType<PluginProvider>().AsSelf().SingleInstance();
        builder.RegisterType<BsaHandler>().AsSelf().SingleInstance();
        builder.RegisterType<RecordHandler>().AsSelf().SingleInstance();
        builder.RegisterType<RecordDeltaPatcher>().AsSelf().SingleInstance();
        builder.RegisterType<SkyPatcherInterface>().AsSelf().SingleInstance();
        builder.RegisterType<AssetHandler>().AsSelf().SingleInstance();
        builder.RegisterType<BackEnd.OutfitDistribution.OutfitDisplayResolver>().AsSelf().SingleInstance();
        builder.RegisterType<BackEnd.OutfitDistribution.ForwardedOutfitDistributor>().AsSelf().SingleInstance();
        builder.RegisterType<WigForwarder>().AsSelf().SingleInstance();
        builder.RegisterType<HeadPartWigConverter>().AsSelf().SingleInstance();
        builder.RegisterType<Validator>().AsSelf().SingleInstance();
        builder.RegisterType<Patcher>().AsSelf().SingleInstance();
        // AssetHandler depends on Lazy<VM_Run>; VM_Run is a heavy UI VM not registered here.
        // The lazy is only realized on progress/feedback paths the headless patcher tests avoid.
        builder.Register(_ => new Lazy<VM_Run>(() => null!)).As<Lazy<VM_Run>>().SingleInstance();
        // Same treatment for the FaceGen ladder's compatibility analyzer: it hangs off
        // CharacterPreviewCache and its whole asset-resolver chain, and is only realized on the
        // rare ladder rows that borrow a mesh from outside the selected mod.
        builder.Register(_ => new Lazy<FaceGenConsistencyAnalyzer>(() => null!))
            .As<Lazy<FaceGenConsistencyAnalyzer>>().SingleInstance();

        Container = builder.Build();
    }

    /// <summary>Harness with an Invalid (no-game) environment — for guard/early-return assertions.</summary>
    public static NpcChooserHarness Invalid(Settings? settings = null) =>
        new(NpcChooserTestEnvironment.Invalid(), settings ?? new Settings());

    public Patcher Patcher => Container.Resolve<Patcher>();
    public Validator Validator => Container.Resolve<Validator>();

    /// <summary>Exposed so an integration test can stub the converter's NIF-reading seams before a
    /// run — a synthetic fixture cannot supply parseable meshes.</summary>
    public HeadPartWigConverter HeadPartWigConverter => Container.Resolve<HeadPartWigConverter>();

    public void Dispose() => Container.Dispose();
}
