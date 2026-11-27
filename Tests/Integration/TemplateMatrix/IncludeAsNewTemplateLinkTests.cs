using System.IO;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// A Traits-templated NPC whose appearance mod ALSO overrides its template, under the override
/// modes. Found in game on 2026-11-27 (N12): with Include As New and "use the template's
/// appearance", Captain Hargar (01E38B) came out with a black face. High Poly NPC Overhaul overrides
/// both him and his template, the Blackblood Marauder (0DC8DA). The override walk follows the
/// Template link (a default Override Root), so it copied the mod's Marauder as a NEW NPC and repointed
/// Hargar's Template at the copy. Nothing ships a face for a copied NPC, and a Traits NPC renders its
/// template's face.
///
/// <para>The rule pinned here: the walk goes THROUGH an NPC it reaches (so the mod's overrides under
/// it, such as the head parts a flattened template hands over, are still copied), but never copies
/// the NPC itself. The Template link stays on the original.</para>
///
/// <para>The Include (not As New) test only records what the output holds today. What Include should
/// do with an NPC it meets is not decided.</para>
///
/// <para>Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class IncludeAsNewTemplateLinkTests
{
    private readonly ITestOutputHelper _output;

    public IncludeAsNewTemplateLinkTests(ITestOutputHelper output) => _output = output;

    private static readonly ModKey[] BaseMasters =
        Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();

    private static readonly ModKey BaseKey = ModKey.FromFileName("NPC2ItlBase.esp");
    private static readonly ModKey ModAKey = ModKey.FromFileName("NPC2ItlModA.esp");
    private static readonly ModKey ModBKey = ModKey.FromFileName("NPC2ItlModB.esp");

    // Batches run in display-name order, so A (the inheritor's mod) runs before B (the template's),
    // as High Poly NPC Overhaul ran before Nordic Faces in the game run.
    private const string ModAName = "N12 Fixture Mod A";
    private const string ModBName = "N12 Fixture Mod B";

    private const string EidTemplated = "NPC2Itl_Templated";
    private const string EidTerminus = "NPC2Itl_Terminus";
    private const string HpBase = "NPC2Itl_HP_Base";
    private const string HpModATemplated = "NPC2Itl_HP_ModATemplated";
    private const string HpModATerminus = "NPC2Itl_HP_ModATerminus";
    private const string HpModBTerminus = "NPC2Itl_HP_ModBTerminus";

    private const float BaseHeight = 1.00f;
    private const float ModATemplatedHeight = 1.10f;
    private const float ModATerminusHeight = 1.20f;
    private const float ModBTerminusHeight = 1.30f;

    private const string FaceGenModA = "n12-facegen|ModA";
    private const string FaceGenModB = "n12-facegen|ModB";

    private static bool IsVanillaOrCc(ModKey key)
    {
        if (BaseMasters.Contains(key)) return true;
        var fn = key.FileName.String;
        return fn.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
               || fn.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Run : IDisposable
    {
        public required string Root { get; init; }
        public required string OutDir { get; init; }
        public required ISkyrimModGetter Output { get; init; }
        public required IDisposable Handle { get; init; }
        public required FormKey TemplatedKey { get; init; }
        public required FormKey TerminusKey { get; init; }
        public required FormKey HpModATerminusKey { get; init; }

        public void Dispose()
        {
            Handle.Dispose();
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { /* overlay may hold a map */ }
        }
    }

    /// <summary>
    /// Base plugin: a plain terminus and a Traits-templated NPC pointing at it. Mod A (selected for the
    /// templated NPC) overrides BOTH NPCs and the head part its terminus uses, and ships a face for the
    /// terminus only, as High Poly NPC Overhaul does for the Marauder. Mod B overrides the terminus and
    /// ships its face. Neither appearance plugin is in the load order (how these mods are configured).
    /// Null when there is no Skyrim SE environment.
    /// </summary>
    private async Task<Run?> BuildAndRunAsync(PatchingMode patchingMode, TemplateHandlingMode templateMode,
        RecordOverrideHandlingMode overrideMode, bool selectTerminusWithModB)
    {
        var root = Path.Combine(Path.GetTempPath(), "NpcItl_" + Guid.NewGuid().ToString("N"));
        var folderA = Path.Combine(root, "ModA");
        var folderB = Path.Combine(root, "ModB");
        var outDir = Path.Combine(root, "Output");
        var envOutDir = Path.Combine(root, "EnvOutput");
        var writeOrder = BaseMasters.Concat(new[] { BaseKey, ModAKey, ModBKey }).ToArray();

        var baseMod = new SkyrimMod(BaseKey, SkyrimRelease.SkyrimSE);

        HeadPart NewHeadPart(string editorId)
        {
            var hp = baseMod.HeadParts.AddNew();
            hp.EditorID = editorId;
            hp.Type = HeadPart.TypeEnum.Face;
            return hp;
        }

        var hpBase = NewHeadPart(HpBase);
        var hpATemplated = NewHeadPart(HpModATemplated);
        var hpATerminus = NewHeadPart(HpModATerminus);
        var hpBTerminus = NewHeadPart(HpModBTerminus);

        Npc Plain(string editorId)
        {
            var n = baseMod.Npcs.AddNew();
            n.EditorID = editorId;
            n.Name = editorId;
            n.Race.SetTo(Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Race.NordRace);
            n.Height = BaseHeight;
            n.HeadParts.Add(hpBase.FormKey);
            return n;
        }

        var terminus = Plain(EidTerminus);
        var templated = Plain(EidTemplated);
        templated.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
        templated.Template.SetTo(terminus.FormKey);

        var basePath = Path.Combine(root, "Base", BaseKey.FileName);
        WriteMod(baseMod, basePath, writeOrder);

        var modA = new SkyrimMod(ModAKey, SkyrimRelease.SkyrimSE);
        var aTemplated = modA.Npcs.GetOrAddAsOverride(templated);
        aTemplated.Height = ModATemplatedHeight;
        aTemplated.HeadParts.Clear();
        aTemplated.HeadParts.Add(hpATemplated.FormKey);
        var aTerminus = modA.Npcs.GetOrAddAsOverride(terminus);
        aTerminus.Height = ModATerminusHeight;
        aTerminus.HeadParts.Clear();
        aTerminus.HeadParts.Add(hpATerminus.FormKey);
        // Something the mod overrides UNDER the terminus, so walking through it has a job to do.
        modA.HeadParts.GetOrAddAsOverride(hpATerminus).EditorID = HpModATerminus + "_EditedByModA";
        WriteMod(modA, Path.Combine(folderA, ModAKey.FileName), writeOrder);

        var modB = new SkyrimMod(ModBKey, SkyrimRelease.SkyrimSE);
        var bTerminus = modB.Npcs.GetOrAddAsOverride(terminus);
        bTerminus.Height = ModBTerminusHeight;
        bTerminus.HeadParts.Clear();
        bTerminus.HeadParts.Add(hpBTerminus.FormKey);
        WriteMod(modB, Path.Combine(folderB, ModBKey.FileName), writeOrder);

        WriteFaceGen(folderA, terminus.FormKey, FaceGenModA);
        WriteFaceGen(folderB, terminus.FormKey, FaceGenModB);

        IEnumerable<IModListingGetter<ISkyrimModGetter>> Transform(
            IEnumerable<IModListingGetter<ISkyrimModGetter>> input)
        {
            var listings = input.OnlyEnabledAndExisting().Where(l => IsVanillaOrCc(l.ModKey)).ToList();
            listings.Add(new ModListing<ISkyrimModGetter>(
                BaseKey, SkyrimMod.CreateFromBinaryOverlay(basePath, SkyrimRelease.SkyrimSE), enabled: true));
            return listings;
        }

        Directory.CreateDirectory(envOutDir);
        var provider = new EnvironmentStateProvider(null);
        provider.SetEnvironmentTarget(SkyrimRelease.SkyrimSE, string.Empty, "NPC", envOutDir);
        provider.UpdateEnvironmentForTest(Transform);
        if (provider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
        {
            _output.WriteLine("SKIPPED: no valid Skyrim SE environment. " + provider.EnvironmentBuilderError);
            try { Directory.Delete(root, true); } catch { /* best effort */ }
            return null;
        }

        var settings = new Settings
        {
            SkyrimRelease = SkyrimRelease.SkyrimSE,
            OutputPluginName = string.Empty,
            OutputDirectory = outDir,
            AppendTimestampToOutputDirectory = false,
            ModsFolder = string.Empty,
            SplitOutput = false,
            AutoEslIfy = false,
            AutoSplitOutput = false,
            PatchingMode = patchingMode,
            OutputBackend = OutputBackend.ESP,
            DefaultWigHandlingMode = WigHandlingMode.None,
            DefaultAntlerHandlingMode = AntlerHandlingMode.None,
            TemplateHandlingMode = templateMode,
            DefaultRecordOverrideHandlingMode = overrideMode,
            DefaultMaxNestedIntervalDepth = 2,
            DefaultIncludeAllOverrides = false,
            PublishForwardedOutfitsToDistributors = false,
            LocalizationLanguage = null,
            BatFilePreCommands = string.Empty,
            BatFilePostCommands = string.Empty,
        };
        ModSetting Mod(string name, ModKey key, string folder) => new()
        {
            DisplayName = name,
            CorrespondingModKeys = new List<ModKey> { key },
            CorrespondingFolderPaths = new List<string> { folder },
            MergeInDependencyRecords = true,
            IncludeOutfits = false,
            CopyAssets = false,
            ModRecordOverrideHandlingMode = overrideMode,
            IncludeAllOverrides = false,
        };
        settings.ModSettings = new List<ModSetting> { Mod(ModAName, ModAKey, folderA), Mod(ModBName, ModBKey, folderB) };
        settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
        {
            [templated.FormKey] = (ModAName, templated.FormKey),
        };
        if (selectTerminusWithModB)
        {
            settings.SelectedAppearanceMods[terminus.FormKey] = (ModBName, terminus.FormKey);
        }

        Directory.CreateDirectory(outDir);
        var run = await GoldenPatchRunner.RunAsync(provider, settings);
        foreach (var inv in run.InvalidSelections) _output.WriteLine("  INVALID: " + inv);
        _output.WriteLine("--- RUN LOG ---\n" + run.Log + "\n--- END RUN LOG ---");

        var outPlugin = Path.Combine(outDir, "NPC.esp");
        File.Exists(outPlugin).Should().BeTrue("the patcher must write an output plugin");
        var handle = SkyrimMod.CreateFromBinaryOverlay(outPlugin, SkyrimRelease.SkyrimSE);
        ISkyrimModGetter outMod = handle;
        _output.WriteLine("output records: " + string.Join(", ",
            outMod.EnumerateMajorRecords().Select(r => $"{r.Registration.Name} {r.FormKey} '{r.EditorID}'")));

        return new Run
        {
            Root = root,
            OutDir = outDir,
            Output = outMod,
            Handle = handle,
            TemplatedKey = templated.FormKey,
            TerminusKey = terminus.FormKey,
            HpModATerminusKey = hpATerminus.FormKey,
        };
    }

    private static INpcGetter Templated(Run run)
    {
        var npc = run.Output.Npcs.SingleOrDefault(n => n.EditorID == EidTemplated);
        npc.Should().NotBeNull($"'{EidTemplated}' must reach the output plugin; output NPCs: " +
            string.Join(", ", run.Output.Npcs.Select(n => $"{n.FormKey} '{n.EditorID}'")));
        return npc!;
    }

    /// <summary>
    /// The N12 defect: no NPC is minted, and the templated NPC's Template stays on the original. Under
    /// "use the template's appearance" the record it renders must also be one that has a face.
    /// </summary>
    [Theory]
    [InlineData(PatchingMode.CreateAndPatch, TemplateHandlingMode.InheritFromTemplate)]
    [InlineData(PatchingMode.Create, TemplateHandlingMode.InheritFromTemplate)]
    [InlineData(PatchingMode.CreateAndPatch, TemplateHandlingMode.GiveEachNpcOwnCopy)]
    [InlineData(PatchingMode.Create, TemplateHandlingMode.GiveEachNpcOwnCopy)]
    public async Task IncludeAsNew_KeepsTheTemplateLinkOnTheOriginal_AndMintsNoNpc(
        PatchingMode patchingMode, TemplateHandlingMode templateMode)
    {
        using var run = await BuildAndRunAsync(patchingMode, templateMode,
            RecordOverrideHandlingMode.IncludeAsNew, selectTerminusWithModB: true);
        if (run == null) return;

        var minted = run.Output.Npcs.Where(n => n.FormKey.ModKey == run.Output.ModKey).ToList();
        minted.Select(n => $"{n.FormKey} '{n.EditorID}'").Should().BeEmpty(
            "Include As New must not copy an NPC it reaches through a Template link: the copy has no face");

        var templatedOut = Templated(run);
        templatedOut.Template.FormKey.Should().Be(run.TerminusKey,
            "the Template link must stay on the original template record");

        if (templateMode == TemplateHandlingMode.InheritFromTemplate)
        {
            templatedOut.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)
                .Should().BeTrue("under 'use the template's appearance' the NPC keeps inheriting");
            var target = templatedOut.Template.FormKey;
            var (meshRel, _) = Auxilliary.GetFaceGenSubPathStrings(target, regularized: true);
            (target.ModKey != run.Output.ModKey || File.Exists(Path.Combine(run.OutDir, meshRel)))
                .Should().BeTrue($"the NPC renders {target}'s face, so that record must not be a copy with no " +
                                 $"face file ({meshRel})");
        }

        OutputLinkSweep.AssertNoLinksOutsideLoadOrder(run.Output, BaseMasters.Append(BaseKey), _output,
            "Include As New output for a templated NPC");
    }

    /// <summary>
    /// What the Template walk is for: under own-copy the flatten writes the TEMPLATE's head part
    /// (Mod A's version) onto the NPC, and the mod's override of that head part must still arrive as
    /// a private copy the NPC points at. Green before and after the N12 fix; it guards the walk
    /// THROUGH the template.
    /// </summary>
    [Theory]
    [InlineData(PatchingMode.CreateAndPatch)]
    [InlineData(PatchingMode.Create)]
    public async Task IncludeAsNew_OwnCopy_StillDeliversTheModsOverrideUnderTheTemplate(PatchingMode patchingMode)
    {
        using var run = await BuildAndRunAsync(patchingMode, TemplateHandlingMode.GiveEachNpcOwnCopy,
            RecordOverrideHandlingMode.IncludeAsNew, selectTerminusWithModB: true);
        if (run == null) return;

        var templatedOut = Templated(run);
        templatedOut.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)
            .Should().BeFalse("own-copy clears the Traits flag");

        var copies = run.Output.HeadParts
            .Where(h => h.FormKey.ModKey == run.Output.ModKey && h.EditorID != null &&
                        h.EditorID.StartsWith(HpModATerminus + "_EditedByModA", StringComparison.Ordinal))
            .Select(h => h.FormKey)
            .ToList();
        copies.Should().ContainSingle("Mod A's override of the template's head part is a private copy");

        templatedOut.HeadParts.Select(h => h.FormKey).Should().Equal(new[] { copies[0] },
            "the flattened head part is the template's (Mod A's) head part, repointed at its private copy");
    }

    /// <summary>
    /// RECORD OF CURRENT BEHAVIOUR, not a decision. Plain Include (not As New), Create-and-Patch,
    /// "use the template's appearance": what the output holds for the TEMPLATE NPC, when the
    /// template is (a) selected with Mod B, (b) not selected. Include's collector follows the Template
    /// link of the inheritor and writes Mod A's override of the template into the output as an
    /// override. If a fix later changes this, change this test with it.
    /// The N12-OBS line in the output is what the trace compares across repeated runs.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Include_CurrentBehaviour_TemplateNpcRecordAndFace(bool terminusSelectedWithModB)
    {
        using var run = await BuildAndRunAsync(PatchingMode.CreateAndPatch, TemplateHandlingMode.InheritFromTemplate,
            RecordOverrideHandlingMode.Include, selectTerminusWithModB: terminusSelectedWithModB);
        if (run == null) return;

        var terminusOut = run.Output.Npcs.SingleOrDefault(n => n.FormKey == run.TerminusKey);
        var hpNames = terminusOut == null
            ? "-"
            : string.Join("+", terminusOut.HeadParts.Select(h =>
                run.Output.HeadParts.FirstOrDefault(x => x.FormKey == h.FormKey)?.EditorID ?? h.FormKey.ToString()));
        string? HeadPartEid(FormKey key) => key == run.HpModATerminusKey ? HpModATerminus : null;
        var hpKeys = terminusOut == null
            ? "-"
            : string.Join("+", terminusOut.HeadParts.Select(h => HeadPartEid(h.FormKey) ?? h.FormKey.ToString()));
        var (meshRel, _) = Auxilliary.GetFaceGenSubPathStrings(run.TerminusKey, regularized: true);
        var meshPath = Path.Combine(run.OutDir, meshRel);
        var face = File.Exists(meshPath) ? File.ReadAllText(meshPath) : "none";
        var minted = run.Output.Npcs.Count(n => n.FormKey.ModKey == run.Output.ModKey);
        var templatedOut = Templated(run);

        var obs = $"N12-OBS selectedWithB={terminusSelectedWithModB} terminusInOutput={terminusOut != null} " +
                  $"height={terminusOut?.Height.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "-"} " +
                  $"headParts={hpKeys} headPartNames={hpNames} face={face} mintedNpcs={minted} " +
                  $"templatedTemplate={templatedOut.Template.FormKey}";
        _output.WriteLine(obs);

        // Today's behaviour, as predicted from the code (Patcher.cs Include branch, RecordHandler.cs
        // CollectOverriddenDependencyRecordsCore): the Template link is untouched and no NPC is
        // minted. With B selected, B's batch runs after A's and its appearance copy overwrites Mod A's
        // delta on the template; unselected, Mod A's delta stands and nothing ships a face for it.
        templatedOut.Template.FormKey.Should().Be(run.TerminusKey, obs);
        minted.Should().Be(0, obs);
        terminusOut.Should().NotBeNull(obs);
        if (terminusSelectedWithModB)
        {
            terminusOut!.Height.Should().BeApproximately(ModBTerminusHeight, 0.001f, obs);
            face.Should().StartWith(FaceGenModB, obs);
        }
        else
        {
            terminusOut!.Height.Should().BeApproximately(ModATerminusHeight, 0.001f, obs);
            face.Should().Be("none", obs);
        }
    }

    private static void WriteMod(SkyrimMod mod, string path, ModKey[] writeOrder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        mod.BeginWrite.ToPath(path).WithLoadOrder(writeOrder).WithNoDataFolder().Write();
    }

    private static void WriteFaceGen(string modFolder, FormKey npcKey, string content)
    {
        var (meshRel, texRel) = Auxilliary.GetFaceGenSubPathStrings(npcKey, regularized: true);
        foreach (var (rel, half) in new[] { (meshRel, "nif"), (texRel, "dds") })
        {
            var path = Path.Combine(modFolder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content + "|" + half);
        }
    }
}
