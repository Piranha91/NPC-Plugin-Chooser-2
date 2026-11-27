using System.IO;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// Bench finding N11: under a runtime back end (SkyPatcher, Recast) an entry changes only the NPC it
/// names. An NPC that takes its face from a patched NPC through its Traits template then shows the
/// template's face MESH (the same file) but not its record-level appearance (skin, head parts): Legate
/// Rikke's double <c>0D0573:Skyrim.esm</c> showed her converted wig through its helmet.
///
/// <para>The patcher therefore writes, after the selected NPCs, an entry for every NPC in the load
/// order that inherits its face from an NPC patched in this run and has no appearance entry of its
/// own. The entry repeats the template's and points at the template's surrogate.</para>
///
/// <para>Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class InheritedAppearanceMirrorTests
{
    private readonly ITestOutputHelper _output;

    public InheritedAppearanceMirrorTests(ITestOutputHelper output) => _output = output;

    private static readonly FormKey BretonRace = Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Race.BretonRace.FormKey;

    /// <summary>The start of the batch line the run log carries for the mirrored entries.</summary>
    private const string BatchLine = "NPC(s) that take their appearance from a patched NPC";

    /// <summary>A base NPC that owns its face, and the appearance mod's version of it: a skin of the
    /// mod's own, a height and a weight that differ from the base record's.</summary>
    private static Npc AddPatchedNpc(WigRouteFixture fx, string editorId, Action<Npc>? editModRecord = null)
    {
        var npc = fx.AddBaseNpc(editorId);
        var skin = fx.AddResArmor(editorId + "_Skin", fx.AddResArmorAddon(editorId + "_BodyAA", BipedObjectFlag.Body));
        var modRecord = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modRecord.WornArmor.SetTo(skin);
        modRecord.Height = 1.05f;
        modRecord.Weight = 80f;
        editModRecord?.Invoke(modRecord);
        fx.WriteFaceGen(npc.FormKey);
        return npc;
    }

    private static Dictionary<FormKey, (string, FormKey)> Select(params Npc[] npcs) =>
        npcs.ToDictionary(n => n.FormKey, n => (WigRouteFixture.ModName, n.FormKey));

    private static void AssertRepeatsTheAppearanceOf(IReadOnlyDictionary<string, string>? inheritor,
        IReadOnlyDictionary<string, string> template, string who)
    {
        inheritor.Should().NotBeNull($"{who} inherits its face from a patched NPC, so it needs an entry of its own");
        inheritor!["copyVisualStyle"].Should().Be(template["copyVisualStyle"], $"{who} is pointed at the template's surrogate");
        inheritor["skin"].Should().Be(template["skin"], $"{who} gets the template's skin");
        inheritor["height"].Should().Be(template["height"]);
        inheritor["weight"].Should().Be(template["weight"]);
        inheritor["removeTemplateFlags"].Should().Be("traits",
            "the line only holds while the NPC no longer takes its traits from the template record");
    }

    private static string TomlPath(Settings settings) =>
        Path.Combine(RecastInterface.GetOutputDir(settings.OutputDirectory), "NPC.toml");

    private static Dictionary<FormKey, FormKey> RecastFaces(Settings settings)
    {
        var toml = RecastPatchReader.ReadFile(TomlPath(settings));
        toml.IsLoaded.Should().BeTrue(string.Join("; ", toml.Errors));
        return toml.Faces.ToDictionary(f => f.Target.FormKey!.Value, f => f.Source.FormKey!.Value);
    }

    // ------------------------------------------------------------------ SkyPatcher

    [Fact]
    public async Task SkyPatcher_UnselectedInheritor_GetsTheTemplatesAppearance_ButNotItsOutfitOrKeywords()
    {
        using var fx = new WigRouteFixture("n11-sky");
        var template = AddPatchedNpc(fx, "N11_Template");
        var twin = fx.AddTemplatedNpc("N11_Twin", template, NpcConfiguration.TemplateFlag.Traits);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: true, "sky");
        var modSetting = fx.NewModSetting();
        modSetting.IncludeOutfits = true;
        modSetting.Keywords = new HashSet<string> { "N11Keyword" };
        settings.ModSettings = new List<ModSetting> { modSetting };
        settings.SelectedAppearanceMods = Select(template);

        using var run = await fx.RunAsync(settings, _output, "sky");
        if (run == null) return;

        var templateLine = run.DirectivesFor(template.FormKey);
        templateLine.Should().NotBeNull();
        templateLine!.Should().ContainKey("outfitDefault", "the control: the template's own line carries its outfit");
        templateLine.Should().ContainKey("keywordsToAdd", "the control: the template's own line carries the mod's keyword");

        var twinLine = run.DirectivesFor(twin.FormKey);
        AssertRepeatsTheAppearanceOf(twinLine, templateLine!, "the twin");
        twinLine!.Should().NotContainKey("outfitDefault", "the inheriting NPC keeps its own inventory");
        twinLine.Should().NotContainKey("outfitSleep");
        twinLine.Should().NotContainKey("keywordsToAdd", "keywords are not a trait");

        _output.WriteLine("MIRRORED LINE: " + File.ReadAllLines(run.IniPath).First(l =>
            l.StartsWith("filterByNPCs=" + SkyPatcherInterface.FormatFormKeyForSkyPatcher(twin.FormKey) + ":")));
    }

    [Fact]
    public async Task SkyPatcher_InheritorSelectedButRejectedBecauseItInherits_StillGetsTheTemplatesAppearance()
    {
        using var fx = new WigRouteFixture("n11-rejected");
        var template = AddPatchedNpc(fx, "N11_Template");
        var twin = fx.AddTemplatedNpc("N11_Twin", template, NpcConfiguration.TemplateFlag.Traits);
        fx.AppearanceMod.Npcs.GetOrAddAsOverride(twin); // the mod covers the twin too, still templated
        fx.WriteFaceGen(twin.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: true, "rejected"); // "Use the template's appearance"
        settings.ModSettings = new List<ModSetting> { fx.NewModSetting() };
        settings.SelectedAppearanceMods = Select(template, twin);

        using var run = await fx.RunAsync(settings, _output, "rejected");
        if (run == null) return;

        run.Result.InvalidSelections.Should().ContainSingle(s => s.Contains("Templated NPC"),
            "the twin's own selection is still rejected: its own face cannot be delivered in this mode");
        run.Result.PatchedTargets.Should().NotContain(twin.FormKey);

        AssertRepeatsTheAppearanceOf(run.DirectivesFor(twin.FormKey), run.DirectivesFor(template.FormKey)!, "the twin");
    }

    [Fact]
    public async Task SkyPatcher_InheritorPatchedItself_KeepsItsOwnLineAndGetsNoSecond()
    {
        using var fx = new WigRouteFixture("n11-owncopy");
        var template = AddPatchedNpc(fx, "N11_Template");
        var twin = fx.AddTemplatedNpc("N11_Twin", template, NpcConfiguration.TemplateFlag.Traits);
        fx.AppearanceMod.Npcs.GetOrAddAsOverride(twin);
        fx.WriteFaceGen(twin.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: true, "owncopy", TemplateHandlingMode.GiveEachNpcOwnCopy);
        settings.ModSettings = new List<ModSetting> { fx.NewModSetting() };
        settings.SelectedAppearanceMods = Select(template, twin);

        using var run = await fx.RunAsync(settings, _output, "owncopy");
        if (run == null) return;

        run.Result.PatchedTargets.Should().Contain(new[] { template.FormKey, twin.FormKey });

        var twinKey = "filterByNPCs=" + SkyPatcherInterface.FormatFormKeyForSkyPatcher(twin.FormKey) + ":";
        File.ReadAllLines(run.IniPath).Count(l => l.StartsWith(twinKey)).Should().Be(1, "the twin has exactly one line");

        var twinLine = run.DirectivesFor(twin.FormKey);
        twinLine.Should().NotBeNull();
        twinLine!["copyVisualStyle"].Should().NotBe(run.DirectivesFor(template.FormKey)!["copyVisualStyle"],
            "the twin was patched itself, so its line names its own surrogate");
    }

    [Fact]
    public async Task SkyPatcher_ChainOfTwo_BothMembersGetTheTemplatesAppearance()
    {
        using var fx = new WigRouteFixture("n11-chain");
        var template = AddPatchedNpc(fx, "N11_Template");
        var middle = fx.AddTemplatedNpc("N11_Middle", template, NpcConfiguration.TemplateFlag.Traits);
        var outer = fx.AddTemplatedNpc("N11_Outer", middle, NpcConfiguration.TemplateFlag.Traits);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: true, "chain");
        settings.ModSettings = new List<ModSetting> { fx.NewModSetting() };
        settings.SelectedAppearanceMods = Select(template);

        using var run = await fx.RunAsync(settings, _output, "chain");
        if (run == null) return;

        var templateLine = run.DirectivesFor(template.FormKey)!;
        AssertRepeatsTheAppearanceOf(run.DirectivesFor(middle.FormKey), templateLine, "the middle of the chain");
        AssertRepeatsTheAppearanceOf(run.DirectivesFor(outer.FormKey), templateLine, "the outer end of the chain");

        run.Log.Should().Contain("2 " + BatchLine, "the run log says how many NPCs were covered");
        run.Log.Should().Contain("through SkyPatcher");
        run.Log.Should().Contain("N11_Outer", "the per-NPC list (verbose) names each of them");
    }

    [Fact]
    public async Task SkyPatcher_TemplateWithoutTraitsFlag_AndChainThroughALeveledList_GetNoEntry()
    {
        using var fx = new WigRouteFixture("n11-negative");
        var template = AddPatchedNpc(fx, "N11_Template");

        // Template link, but for the inventory only: the face is the NPC's own.
        var inventoryOnly = fx.AddTemplatedNpc("N11_InventoryOnly", template, NpcConfiguration.TemplateFlag.Inventory);

        // Traits template is a leveled NPC list: the game picks the face at run time.
        var leveled = fx.BaseMod.LeveledNpcs.AddNew();
        leveled.EditorID = "N11_LeveledList";
        var throughList = fx.BaseMod.Npcs.AddNew();
        throughList.EditorID = "N11_ThroughList";
        throughList.Race.SetTo(WigRouteFixture.NordRace);
        throughList.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
        throughList.Template.SetTo(leveled.FormKey);

        // The control, so an empty result is not the reason the two above have no line.
        var twin = fx.AddTemplatedNpc("N11_Twin", template, NpcConfiguration.TemplateFlag.Traits);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: true, "negative");
        settings.ModSettings = new List<ModSetting> { fx.NewModSetting() };
        settings.SelectedAppearanceMods = Select(template);

        using var run = await fx.RunAsync(settings, _output, "negative");
        if (run == null) return;

        run.DirectivesFor(twin.FormKey).Should().NotBeNull("the control inherits its face and gets an entry");
        run.DirectivesFor(inventoryOnly.FormKey).Should().BeNull("without the Traits flag the NPC does not inherit its face");
        run.DirectivesFor(throughList.FormKey).Should().BeNull("a chain through a leveled list is left out");
        run.Log.Should().Contain("1 " + BatchLine);
    }

    [Fact]
    public async Task SkyPatcher_RaceAndFemaleFlagOfTheTemplatesLine_AreRepeated()
    {
        using var fx = new WigRouteFixture("n11-race");
        var template = AddPatchedNpc(fx, "N11_Template", modRecord =>
        {
            modRecord.Race.SetTo(BretonRace);
            modRecord.Configuration.Flags |= NpcConfiguration.Flag.Female;
        });
        var twin = fx.AddTemplatedNpc("N11_Twin", template, NpcConfiguration.TemplateFlag.Traits);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: true, "race");
        settings.ModSettings = new List<ModSetting> { fx.NewModSetting() };
        settings.SelectedAppearanceMods = Select(template);

        using var run = await fx.RunAsync(settings, _output, "race");
        if (run == null) return;

        var templateLine = run.DirectivesFor(template.FormKey)!;
        templateLine.Should().ContainKey("race", "the control: the template's line changes its race");
        templateLine.Should().ContainKey("setFlags", "the control: the template's line sets the female flag");

        var twinLine = run.DirectivesFor(twin.FormKey);
        AssertRepeatsTheAppearanceOf(twinLine, templateLine, "the twin");
        twinLine!["race"].Should().Be(templateLine["race"], "race is a trait");
        twinLine["setFlags"].Should().Be("female", "sex is a trait");
    }

    // ------------------------------------------------------------------ Recast

    [Fact]
    public async Task RecastOnly_UnselectedInheritor_GetsAnEntryWithTheTemplatesFace()
    {
        using var fx = new WigRouteFixture("n11-recast");
        var template = AddPatchedNpc(fx, "N11_Template");
        var twin = fx.AddTemplatedNpc("N11_Twin", template, NpcConfiguration.TemplateFlag.Traits);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: false, "recast");
        settings.OutputBackend = OutputBackend.Recast;
        settings.RecastMode = RecastMode.RecastOnly;
        settings.ModSettings = new List<ModSetting> { fx.NewModSetting() };
        settings.SelectedAppearanceMods = Select(template);

        using var run = await fx.RunAsync(settings, _output, "recast");
        if (run == null) return;

        var faces = RecastFaces(settings);
        faces.Should().ContainKey(template.FormKey);
        faces.Should().ContainKey(twin.FormKey, "the twin inherits its face from a patched NPC");
        faces[twin.FormKey].Should().Be(faces[template.FormKey], "the entry names the template's surrogate");
        File.Exists(run.IniPath).Should().BeFalse("Recast Only writes no SkyPatcher file");
        run.Log.Should().Contain("1 " + BatchLine).And.Contain("through Recast");

        _output.WriteLine("RECAST PATCH:" + Environment.NewLine + File.ReadAllText(TomlPath(settings)));
    }

    [Fact]
    public async Task RecastHybrid_InheritorGetsTheFace_AndTheTemplatesRaceAction()
    {
        using var fx = new WigRouteFixture("n11-hybrid");
        var template = AddPatchedNpc(fx, "N11_Template", modRecord => modRecord.Race.SetTo(BretonRace));
        var twin = fx.AddTemplatedNpc("N11_Twin", template, NpcConfiguration.TemplateFlag.Traits);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: false, "hybrid");
        settings.OutputBackend = OutputBackend.Recast;
        settings.RecastMode = RecastMode.Hybrid;
        var modSetting = fx.NewModSetting();
        modSetting.IncludeOutfits = true;
        settings.ModSettings = new List<ModSetting> { modSetting };
        settings.SelectedAppearanceMods = Select(template);

        using var run = await fx.RunAsync(settings, _output, "hybrid");
        if (run == null) return;

        var faces = RecastFaces(settings);
        faces.Should().ContainKey(twin.FormKey);
        faces[twin.FormKey].Should().Be(faces[template.FormKey]);

        var templateLine = run.DirectivesFor(template.FormKey);
        templateLine.Should().NotBeNull("Hybrid delivers the template's race through SkyPatcher");
        templateLine!.Should().ContainKey("race");
        templateLine.Should().ContainKey("outfitDefault", "the control: the template's line carries its outfit");

        var twinLine = run.DirectivesFor(twin.FormKey);
        twinLine.Should().NotBeNull("the race the template is given is a trait the twin inherits");
        twinLine!.Keys.Should().Equal(new[] { "race" }, "only the race is repeated: no outfit, and no SkyPatcher appearance beside a Recast face");
        twinLine["race"].Should().Be(templateLine!["race"]);
    }

    // ------------------------------------------------------------------ plugin records

    [Fact]
    public async Task PluginRecords_WriteNothingForTheInheritor()
    {
        using var fx = new WigRouteFixture("n11-records");
        var template = AddPatchedNpc(fx, "N11_Template");
        var twin = fx.AddTemplatedNpc("N11_Twin", template, NpcConfiguration.TemplateFlag.Traits);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: false, "records");
        settings.ModSettings = new List<ModSetting> { fx.NewModSetting() };
        settings.SelectedAppearanceMods = Select(template);

        using var run = await fx.RunAsync(settings, _output, "records");
        if (run == null) return;

        run.PluginExists.Should().BeTrue();
        run.Output.Npcs.Select(n => n.FormKey).Should().Equal(new[] { template.FormKey },
            "the game's own inheritance carries the template's appearance to the twin");
        File.Exists(run.IniPath).Should().BeFalse("plugin-record output has no SkyPatcher file here");
        File.Exists(TomlPath(settings)).Should().BeFalse("plugin-record output has no Recast patch");
        run.Log.Should().NotContain(BatchLine);
    }

    // ------------------------------------------------------------------ split output

    /// <summary>
    /// One output plugin per selection. The inheritor's entry must be written beside its template's,
    /// in the same file and naming that file's surrogate; and an inheritor that is patched itself in
    /// ANOTHER output must not get a second entry in its template's output.
    /// </summary>
    [Fact]
    public async Task SplitOutput_InheritorEntryLandsInItsTemplatesOutput_AndNeverBesideAnOwnEntryElsewhere()
    {
        using var fx = new WigRouteFixture("n11-split");
        var templateA = AddPatchedNpc(fx, "N11_TemplateA");
        var twinA = fx.AddTemplatedNpc("N11_TwinA", templateA, NpcConfiguration.TemplateFlag.Traits);

        var templateB = AddPatchedNpc(fx, "N11_TemplateB");
        var twinB = fx.AddTemplatedNpc("N11_TwinB", templateB, NpcConfiguration.TemplateFlag.Traits);
        fx.AppearanceMod.Npcs.GetOrAddAsOverride(twinB); // selected and patched itself
        fx.WriteFaceGen(twinB.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: true, "split", TemplateHandlingMode.GiveEachNpcOwnCopy);
        settings.SplitOutput = true;
        settings.SplitOutputMaxNpcs = 1;
        settings.ModSettings = new List<ModSetting> { fx.NewModSetting() };
        settings.SelectedAppearanceMods = Select(templateA, templateB, twinB);

        using var run = await fx.RunAsync(settings, _output, "split");
        if (run == null) return;

        var iniDir = Path.GetDirectoryName(run.IniPath)!;
        var inis = Directory.GetFiles(iniDir, "*.ini")
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p), p => SkyPatcherIniComparer.DirectivesByTarget(p));
        _output.WriteLine("ini files: " + string.Join("; ", inis.Select(kv => $"{kv.Key} [{string.Join(", ", kv.Value.Keys)}]")));
        inis.Should().HaveCount(3, "three selections, one output each");

        var withTemplateA = inis.Single(kv => kv.Value.ContainsKey(templateA.FormKey));
        withTemplateA.Value.Should().ContainKey(twinA.FormKey, "the entry is written beside its template's");
        AssertRepeatsTheAppearanceOf(withTemplateA.Value[twinA.FormKey], withTemplateA.Value[templateA.FormKey], "twin A");
        withTemplateA.Value[twinA.FormKey]["copyVisualStyle"].Should().StartWith(withTemplateA.Key + ".esp|",
            "the surrogate is the one in this output plugin");
        inis.Count(kv => kv.Value.ContainsKey(twinA.FormKey)).Should().Be(1);

        inis.Count(kv => kv.Value.ContainsKey(twinB.FormKey)).Should().Be(1,
            "twin B is patched itself in an output of its own, so its template's output must not name it too");
        var withTwinB = inis.Single(kv => kv.Value.ContainsKey(twinB.FormKey));
        withTwinB.Value.Should().NotContainKey(templateB.FormKey, "twin B's own line is in its own output");
        withTwinB.Value[twinB.FormKey]["copyVisualStyle"].Should().StartWith(withTwinB.Key + ".esp|");
    }
}
