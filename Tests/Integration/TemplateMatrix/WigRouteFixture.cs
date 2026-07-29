using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// Scaffolding shared by the wig/antler two-mode route tests. Builds the plugin shape those routes
/// need — a base plugin that IS in the load order holding the NPC, plus an appearance plugin and a
/// resource plugin that are NOT — then runs the real patcher against it in whichever output mode the
/// test asks for.
///
/// <para>Everything the donor contributes therefore lives outside the load order, so any link the
/// patcher leaves pointing at donor content is a dangling master. That is what makes
/// <see cref="OutputLinkSweep"/> a meaningful assertion here: in this fixture "merged" and "not
/// dangling" are the same statement.</para>
///
/// <para>Authoring order: mutate <see cref="BaseMod"/>/<see cref="ResMod"/>/<see cref="AppearanceMod"/>,
/// call <see cref="WritePlugins"/>, then <see cref="RunAsync"/> once per mode. Skips gracefully
/// (returns null from <see cref="RunAsync"/>) without a Skyrim SE install.</para>
/// </summary>
internal sealed class WigRouteFixture : IDisposable
{
    public const string ModName = "Wig Route Fixture Mod";

    private static readonly ModKey[] BaseMasters =
        Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();

    private static bool IsVanillaOrCc(ModKey key)
    {
        if (BaseMasters.Contains(key)) return true;
        var fn = key.FileName.String;
        return fn.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
               || fn.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ArmorLeatherSimpleOutfit — a real vanilla outfit, so routes that duplicate "the
    /// outfit the NPC actually wears" have something real to duplicate.</summary>
    public static readonly FormKey VanillaOutfit = FormKey.Factory("057A34:Skyrim.esm");

    public static readonly FormKey NordRace = Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Race.NordRace.FormKey;
    public static readonly FormKey DefaultRace = Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Race.DefaultRace.FormKey;

    public string Root { get; }
    public string ModFolder { get; }

    public ModKey BaseKey { get; }
    public ModKey ResKey { get; }
    public ModKey AppearanceKey { get; }

    /// <summary>In the load order. Holds the NPC the user is patching.</summary>
    public SkyrimMod BaseMod { get; }

    /// <summary>NOT in the load order, and flagged resource-only: the "High Poly NPC Overhaul -
    /// Resources.esp" role — armatures, armors, head parts the appearance plugin points at.</summary>
    public SkyrimMod ResMod { get; }

    /// <summary>NOT in the load order: the appearance mod, overriding the base NPC.</summary>
    public SkyrimMod AppearanceMod { get; }

    private readonly string _basePath;
    private readonly ModKey[] _writeOrder;
    private int _runIndex;

    public WigRouteFixture(string label)
    {
        Root = Path.Combine(Path.GetTempPath(), $"NpcWigRoute_{label}_{Guid.NewGuid():N}");
        ModFolder = Path.Combine(Root, "Mod");

        BaseKey = ModKey.FromFileName("NPC2RouteBase.esp");
        ResKey = ModKey.FromFileName("NPC2RouteRes.esp");
        AppearanceKey = ModKey.FromFileName("NPC2RouteMod.esp");
        _writeOrder = BaseMasters.Concat(new[] { BaseKey, ResKey, AppearanceKey }).ToArray();
        _basePath = Path.Combine(Root, "Base", BaseKey.FileName);

        BaseMod = new SkyrimMod(BaseKey, SkyrimRelease.SkyrimSE);
        ResMod = new SkyrimMod(ResKey, SkyrimRelease.SkyrimSE);
        AppearanceMod = new SkyrimMod(AppearanceKey, SkyrimRelease.SkyrimSE);
    }

    /// <summary>Adds a plain NPC to <see cref="BaseMod"/> wearing <see cref="VanillaOutfit"/>, with a
    /// Hair head part (several routes require the donor to have real hair to remove).</summary>
    public Npc AddBaseNpc(string editorId)
    {
        var hair = BaseMod.HeadParts.AddNew();
        hair.EditorID = editorId + "_Hair";
        hair.Type = HeadPart.TypeEnum.Hair;

        var npc = BaseMod.Npcs.AddNew();
        npc.EditorID = editorId;
        npc.Name = editorId;
        npc.Race.SetTo(NordRace);
        npc.HeadParts.Add(hair.FormKey);
        npc.DefaultOutfit.SetTo(VanillaOutfit);
        return npc;
    }

    /// <summary>
    /// Adds an NPC to <see cref="BaseMod"/> that inherits <paramref name="flag"/> from
    /// <paramref name="template"/>. Mirrors <c>TemplateFixtureBuilder</c>'s templated specimens.
    /// The two flags behave completely differently and the wig tests need both:
    /// <b>Traits</b> makes the appearance (head parts, WornArmor, race, sex, weight) come from the
    /// template — which <c>GiveEachNpcOwnCopy</c> then flattens onto the NPC's own record — while
    /// <b>Inventory</b> makes the worn outfit come from the template, which nothing flattens.
    /// </summary>
    public Npc AddTemplatedNpc(string editorId, INpcGetter template, NpcConfiguration.TemplateFlag flag)
    {
        var npc = BaseMod.Npcs.AddNew();
        npc.EditorID = editorId;
        npc.Name = editorId;
        npc.Race.SetTo(NordRace);
        npc.Configuration.TemplateFlags |= flag;
        npc.Template.SetTo(template.FormKey);
        return npc;
    }

    /// <summary>An ArmorAddon in the resource plugin, on the hair slot by default.</summary>
    public ArmorAddon AddResArmorAddon(string editorId, BipedObjectFlag slots = BipedObjectFlag.Hair)
    {
        var arma = ResMod.ArmorAddons.AddNew();
        arma.EditorID = editorId;
        arma.Race.SetTo(DefaultRace);
        arma.BodyTemplate = new BodyTemplate
        {
            FirstPersonFlags = slots,
            ArmorType = ArmorType.Clothing,
        };
        return arma;
    }

    /// <summary>An Armor in the resource plugin wrapping <paramref name="armatures"/>.</summary>
    public Armor AddResArmor(string editorId, params IArmorAddonGetter[] armatures)
    {
        var armo = ResMod.Armors.AddNew();
        armo.EditorID = editorId;
        armo.Name = editorId;
        armo.Race.SetTo(DefaultRace);
        armo.BodyTemplate = new BodyTemplate
        {
            ArmorType = ArmorType.Clothing,
            FirstPersonFlags = armatures.Aggregate(
                (BipedObjectFlag)0, (acc, a) => acc | (a.BodyTemplate?.FirstPersonFlags ?? 0)),
        };
        foreach (var a in armatures) armo.Armature.Add(a.FormKey.ToLink<IArmorAddonGetter>());
        return armo;
    }

    /// <summary>An Outfit in the resource plugin.</summary>
    public Outfit AddResOutfit(string editorId, params IArmorGetter[] items)
    {
        var outfit = ResMod.Outfits.AddNew();
        outfit.EditorID = editorId;
        outfit.Items = new Noggog.ExtendedList<IFormLinkGetter<IOutfitTargetGetter>>();
        foreach (var i in items) outfit.Items.Add(i.FormKey.ToLink<IOutfitTargetGetter>());
        return outfit;
    }

    /// <summary>A dummy FaceGen mesh + texture under the mod folder, so the NPC is not skipped for
    /// missing FaceGen.</summary>
    public void WriteFaceGen(FormKey npcKey)
    {
        var (meshRel, texRel) = Auxilliary.GetFaceGenSubPathStrings(npcKey, regularized: true);
        WriteText(Path.Combine(ModFolder, meshRel), "npc2-wigroute-nif");
        WriteText(Path.Combine(ModFolder, texRel), "npc2-wigroute-dds");
    }

    /// <summary>A loose file under the mod folder, at a Data-relative path.</summary>
    public void WriteLooseFile(string dataRelPath, string content) =>
        WriteText(Path.Combine(ModFolder, dataRelPath), content);

    public void WritePlugins()
    {
        WriteMod(BaseMod, _basePath);
        WriteMod(ResMod, Path.Combine(ModFolder, ResKey.FileName));
        WriteMod(AppearanceMod, Path.Combine(ModFolder, AppearanceKey.FileName));
    }

    /// <summary>The mod entry the patcher patches from. Detection sets start empty — each route test
    /// fills in the ones its route keys off.</summary>
    public ModSetting NewModSetting() => new()
    {
        DisplayName = ModName,
        CorrespondingModKeys = new List<ModKey> { ResKey, AppearanceKey },
        ResourceOnlyModKeys = new HashSet<ModKey> { ResKey },
        CorrespondingFolderPaths = new List<string> { ModFolder },
        MergeInDependencyRecords = true,
        IncludeOutfits = false,
        CopyAssets = false,
        ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
    };

    /// <summary>Baseline settings for a route run. Wig/antler modes default to off; each route test
    /// sets the ones it exercises. <paramref name="templateMode"/> defaults to Inherit — the routes
    /// that exercise the flatten seam (where the wig pass must read the TERMINUS, not the donor)
    /// pass GiveEachNpcOwnCopy explicitly.</summary>
    public Settings NewSettings(bool skyPatcherMode, string tag,
        TemplateHandlingMode templateMode = TemplateHandlingMode.InheritFromTemplate) => new()
    {
        SkyrimRelease = SkyrimRelease.SkyrimSE,
        OutputPluginName = string.Empty,
        OutputDirectory = Path.Combine(Root, "Output_" + tag),
        AppendTimestampToOutputDirectory = false,
        ModsFolder = string.Empty,
        SplitOutput = false,
        AutoEslIfy = false,
        AutoSplitOutput = false,
        PatchingMode = PatchingMode.CreateAndPatch,
        UseSkyPatcherMode = skyPatcherMode,
        DefaultWigHandlingMode = WigHandlingMode.None,
        DefaultAntlerHandlingMode = AntlerHandlingMode.None,
        TemplateHandlingMode = templateMode,
        DefaultRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
        DefaultMaxNestedIntervalDepth = 2,
        DefaultIncludeAllOverrides = false,
        PublishForwardedOutfitsToDistributors = false,
        LocalizationLanguage = null,
        BatFilePreCommands = string.Empty,
        BatFilePostCommands = string.Empty,
    };

    /// <summary>
    /// Runs the patcher once and hands back the written plugin. Returns null (with a note on
    /// <paramref name="output"/>) when there is no valid Skyrim SE environment to run against.
    /// </summary>
    public async Task<RouteRun?> RunAsync(Settings settings, ITestOutputHelper output, string label,
        Action<NpcChooserHarness>? configure = null)
    {
        var provider = TryBuildProvider(output);
        if (provider == null) return null;

        Directory.CreateDirectory(settings.OutputDirectory);
        var run = await GoldenPatchRunner.RunAsync(provider, settings, configure: configure);
        output.WriteLine($"--- RUN LOG ({label}) ---{Environment.NewLine}{run.Log}{Environment.NewLine}--- END ---");

        var outPluginPath = Path.Combine(settings.OutputDirectory, "NPC.esp");
        return new RouteRun(run, outPluginPath, provider.LoadOrderModKeys.ToList(), label);
    }

    /// <summary>
    /// Builds the load order (vanilla + the fixture's base plugin) and returns a valid provider, or
    /// null with a note on <paramref name="output"/> when there is no resolvable Skyrim SE install.
    /// Split out of <see cref="RunAsync"/> so tests that exercise a single service against the
    /// fixture's link cache — rather than a whole patch run — can share the same environment.
    /// </summary>
    public EnvironmentStateProvider? TryBuildProvider(ITestOutputHelper output)
    {
        var envOutDir = Path.Combine(Root, "Env_" + (++_runIndex));
        Directory.CreateDirectory(envOutDir);

        IEnumerable<IModListingGetter<ISkyrimModGetter>> Transform(
            IEnumerable<IModListingGetter<ISkyrimModGetter>> input)
        {
            var listings = input.OnlyEnabledAndExisting()
                .Where(l => IsVanillaOrCc(l.ModKey))
                .ToList();
            listings.Add(new ModListing<ISkyrimModGetter>(
                BaseKey, SkyrimMod.CreateFromBinaryOverlay(_basePath, SkyrimRelease.SkyrimSE), enabled: true));
            return listings;
        }

        var provider = new EnvironmentStateProvider(null);
        provider.SetEnvironmentTarget(SkyrimRelease.SkyrimSE, string.Empty, "NPC", envOutDir);
        provider.UpdateEnvironmentForTest(Transform);

        if (provider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
        {
            output.WriteLine("SKIPPED: no valid Skyrim SE environment. " + provider.EnvironmentBuilderError);
            return null;
        }

        return provider;
    }

    private void WriteMod(SkyrimMod mod, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        mod.BeginWrite.ToPath(path).WithLoadOrder(_writeOrder).WithNoDataFolder().Write();
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        catch { /* a binary overlay may still hold a memory map */ }
    }
}

/// <summary>One patcher run: the log, the written plugin (opened lazily), and the load order it was
/// written against.</summary>
internal sealed class RouteRun : IDisposable
{
    private ISkyrimModDisposableGetter? _handle;

    public RouteRun(GoldenPatchResult run, string outputPluginPath, IReadOnlyList<ModKey> loadOrderKeys,
        string label)
    {
        Result = run;
        OutputPluginPath = outputPluginPath;
        LoadOrderKeys = loadOrderKeys;
        Label = label;
    }

    public GoldenPatchResult Result { get; }
    public string OutputPluginPath { get; }
    public IReadOnlyList<ModKey> LoadOrderKeys { get; }
    public string Label { get; }
    public string Log => Result.Log;
    public bool PluginExists => File.Exists(OutputPluginPath);

    public ISkyrimModGetter Output => _handle ??=
        SkyrimMod.CreateFromBinaryOverlay(OutputPluginPath, SkyrimRelease.SkyrimSE);

    public void Dispose() => _handle?.Dispose();
}
