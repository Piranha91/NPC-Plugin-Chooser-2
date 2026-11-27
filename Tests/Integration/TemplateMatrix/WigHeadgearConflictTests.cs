using System.IO;
using Autofac;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// Wig mode "Forward to Outfit" against an outfit that already holds headgear on the wig's biped
/// slot (in-game bench, 2026-11-25: Legate Rikke's twin wore the wig and no Imperial officer
/// helmet, both on slot 31). The game shows one of two items on a slot, so this is a conflict the
/// user is told about and chooses on, per NPC (<see cref="WigHeadgearChoice"/>); the selection
/// stays valid.
///
/// <para>Covered here against synthetic plugins: the pre-run scan
/// (<see cref="WigForwarder.FindUnansweredHeadgearConflicts"/>), and the three patch-time
/// behaviours: forward with a warning (no stored choice, or Forward), omit, and convert to head
/// parts. Both wig sources are exercised: a wig the mod carries on the skin (WornArmor), which
/// ForwardToOutfit relocates through a generated wearable Armor, and a wig Armor the mod carries
/// in its own outfit.</para>
///
/// <para>All of these skip gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class WigHeadgearConflictTests
{
    private readonly ITestOutputHelper _output;

    public WigHeadgearConflictTests(ITestOutputHelper output) => _output = output;

    private const string WarningLead = "WARNING: Wig handling 'Forward to Outfit' added a wig to the outfit";
    private const string MintedWigPrefix = "NPC2WigArmor_";

    private static string Label(bool skyPatcherMode, bool skinCarried) =>
        (skyPatcherMode ? "skypatcher" : "record") + (skinCarried ? "-skin" : "-outfit");

    /// <summary>What the fixture built, for the assertions.</summary>
    private sealed class Specimen
    {
        public FormKey Npc;
        public FormKey? BareheadedNpc;
        public FormKey OfficerOutfit;
        public FormKey PlainOutfit;
        public FormKey Helmet;
        public FormKey WigArma;
        public FormKey? WigArmo;
        public ModSetting ModSetting = null!;

        /// <summary>The wig as the conflict report names it.</summary>
        public string WigLabel = string.Empty;
    }

    /// <summary>
    /// Base plugin (in the load order): an officer outfit holding a cuirass, a helmet on slots 31
    /// and 42, and a leveled list of boots; an NPC wearing it; optionally a second NPC wearing an
    /// outfit with no headgear. Appearance mod (not in the load order): a slot-31 wig for each NPC,
    /// either on the skin or as an Armor in the mod's own outfit.
    /// </summary>
    private static Specimen Build(WigRouteFixture fx, bool skinCarried, bool convertible = false,
        bool withBareheadedNpc = false)
    {
        var spec = new Specimen();

        Armor BaseArmor(string editorId, string name, BipedObjectFlag slots)
        {
            var armor = fx.BaseMod.Armors.AddNew();
            armor.EditorID = editorId;
            armor.Name = name;
            armor.Race.SetTo(WigRouteFixture.DefaultRace);
            armor.BodyTemplate = new BodyTemplate { ArmorType = ArmorType.Clothing, FirstPersonFlags = slots };
            return armor;
        }

        var helmet = BaseArmor("NPC2Conf_Helmet", "Officer Helmet", BipedObjectFlag.Hair | BipedObjectFlag.Circlet);
        var cuirass = BaseArmor("NPC2Conf_Cuirass", "Officer Armor", BipedObjectFlag.Body);
        var boots = BaseArmor("NPC2Conf_Boots", "Officer Boots", BipedObjectFlag.Feet);
        spec.Helmet = helmet.FormKey;

        var bootList = fx.BaseMod.LeveledItems.AddNew();
        bootList.EditorID = "NPC2Conf_LItemBoots";
        bootList.Entries = new Noggog.ExtendedList<LeveledItemEntry>
        {
            new LeveledItemEntry
            {
                Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = boots.FormKey.ToLink<IItemGetter>() },
            },
        };

        var officerOutfit = fx.BaseMod.Outfits.AddNew();
        officerOutfit.EditorID = "NPC2Conf_OfficerOutfit";
        officerOutfit.Items = new Noggog.ExtendedList<IFormLinkGetter<IOutfitTargetGetter>>
        {
            cuirass.FormKey.ToLink<IOutfitTargetGetter>(),
            helmet.FormKey.ToLink<IOutfitTargetGetter>(),
            bootList.FormKey.ToLink<IOutfitTargetGetter>(),
        };
        spec.OfficerOutfit = officerOutfit.FormKey;

        var plainOutfit = fx.BaseMod.Outfits.AddNew();
        plainOutfit.EditorID = "NPC2Conf_PlainOutfit";
        plainOutfit.Items = new Noggog.ExtendedList<IFormLinkGetter<IOutfitTargetGetter>>
        {
            cuirass.FormKey.ToLink<IOutfitTargetGetter>(),
            bootList.FormKey.ToLink<IOutfitTargetGetter>(),
        };
        spec.PlainOutfit = plainOutfit.FormKey;

        var npc = fx.AddBaseNpc("NPC2Conf_Rikke");
        npc.DefaultOutfit.SetTo(officerOutfit.FormKey);
        spec.Npc = npc.FormKey;

        Npc? bareheaded = null;
        if (withBareheadedNpc)
        {
            bareheaded = fx.AddBaseNpc("NPC2Conf_Bareheaded");
            bareheaded.DefaultOutfit.SetTo(plainOutfit.FormKey);
            spec.BareheadedNpc = bareheaded.FormKey;
        }

        // --- the appearance mod ---------------------------------------------------------------
        var modSetting = fx.NewModSetting();
        spec.ModSetting = modSetting;

        var bodyArma = fx.AddResArmorAddon("NPC2Conf_BodyAA", BipedObjectFlag.Body);
        var wigArma = fx.AddResArmorAddon("NPC2Conf_WigAA");
        spec.WigArma = wigArma.FormKey;

        Armor skin;
        Outfit? donorOutfit = null;
        HeadPart? baldHair = null;
        if (skinCarried)
        {
            if (convertible)
            {
                // The shape Route3b converts: a wig NIF the (stubbed) converter can read, an
                // armature named for the NPC's own race, and a modeless bald placeholder as hair.
                const string wigNifRecordPath = @"actors\NPC2Conf\wig_1.nif";
                wigArma.WorldModel = new GenderedItem<Model?>(
                    new Model { File = wigNifRecordPath }, new Model { File = wigNifRecordPath });
                wigArma.Race.SetTo(WigRouteFixture.NordRace);
                baldHair = MutagenFixtures.NewHeadPart(
                    fx.ResMod, "NPC2Conf_BaldHair", HeadPart.TypeEnum.Hair, modeless: true);
                fx.WriteLooseFile(@"meshes\actors\NPC2Conf\wig_1.nif", "dummy");
                fx.WriteLooseFile(@"meshes\actors\NPC2Conf\wig_0.nif", "dummy");
            }

            skin = fx.AddResArmor("NPC2Conf_Skin", bodyArma, wigArma);
            modSetting.DetectedWigArmatures.Add(wigArma.FormKey);
            spec.WigLabel = "NPC2Conf_WigAA";
        }
        else
        {
            skin = fx.AddResArmor("NPC2Conf_Skin", bodyArma);
            var wigArmo = fx.AddResArmor("NPC2Conf_Wig", wigArma);
            wigArmo.Name = "Rikke Wig";
            donorOutfit = fx.AddResOutfit("NPC2Conf_DonorOutfit", wigArmo);
            modSetting.DetectedWigArmors.Add(wigArmo.FormKey);
            spec.WigArmo = wigArmo.FormKey;
            spec.WigLabel = "Rikke Wig (NPC2Conf_Wig)";
        }

        foreach (var baseNpc in new[] { npc, bareheaded })
        {
            if (baseNpc == null) continue;
            var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(baseNpc);
            modNpc.WornArmor.SetTo(skin);
            if (donorOutfit != null) modNpc.DefaultOutfit.SetTo(donorOutfit);
            if (baldHair != null)
            {
                modNpc.HeadParts.Clear();
                modNpc.HeadParts.Add(baldHair.FormKey);
            }
            fx.WriteFaceGen(baseNpc.FormKey);
        }

        fx.WritePlugins();
        return spec;
    }

    private static Settings NewSettings(WigRouteFixture fx, Specimen spec, bool skyPatcherMode, string tag)
    {
        var settings = fx.NewSettings(skyPatcherMode, tag);
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToOutfit;
        settings.ModSettings = new List<ModSetting> { spec.ModSetting };
        settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
        {
            [spec.Npc] = (WigRouteFixture.ModName, spec.Npc),
        };
        if (spec.BareheadedNpc is { } bare)
        {
            settings.SelectedAppearanceMods[bare] = (WigRouteFixture.ModName, bare);
        }
        return settings;
    }

    private void AssertCleanWrite(RouteRun run)
    {
        run.Log.Should().NotContain("FATAL SAVE ERROR", $"[{run.Label}] the output must be writable");
        run.PluginExists.Should().BeTrue($"[{run.Label}] the patcher must write an output plugin");
        OutputLinkSweep.DumpRecords(run.Output, _output, run.Label);
        OutputLinkSweep.AssertNoLinksOutsideLoadOrder(run.Output, run.LoadOrderKeys, _output,
            $"[{run.Label}] nothing the run wrote may point at the appearance mod's plugins");
    }

    /// <summary>The single patched NPC: the override in record mode, the surrogate in SkyPatcher mode.</summary>
    private static INpcGetter PatchedNpc(RouteRun run) =>
        run.Output.Npcs.Should().ContainSingle($"[{run.Label}] exactly one NPC is patched").Subject;

    /// <summary>FormKeys that mean "the wig" as an outfit item in this run's output: the donor's
    /// wig Armor, any copy of it the run merged in, and any wearable wig Armor the run generated.</summary>
    private static HashSet<FormKey> WigItemKeys(RouteRun run, Specimen spec)
    {
        var keys = run.Output.Armors
            .Where(a => a.EditorID != null &&
                        (a.EditorID == "NPC2Conf_Wig" || a.EditorID.StartsWith(MintedWigPrefix, StringComparison.Ordinal)))
            .Select(a => a.FormKey)
            .ToHashSet();
        if (spec.WigArmo is { } donorWig) keys.Add(donorWig);
        return keys;
    }

    private static IEnumerable<FormKey> ItemKeys(IOutfitGetter outfit) =>
        (outfit.Items ?? Enumerable.Empty<IFormLinkGetter<IOutfitTargetGetter>>()).Select(l => l.FormKey);

    /// <summary>EditorIDs of the ArmorAddons on the patched NPC's skin, or null when the NPC's skin
    /// is not a record the run wrote.</summary>
    private static List<string>? SkinArmatureEditorIds(RouteRun run, INpcGetter npc)
    {
        var skin = run.Output.Armors.FirstOrDefault(a => a.FormKey == npc.WornArmor.FormKey);
        return skin?.Armature
            .Select(l => run.Output.ArmorAddons.FirstOrDefault(a => a.FormKey == l.FormKey)?.EditorID
                         ?? $"(not in output: {l.FormKey})")
            .ToList();
    }

    // =============================================================================================
    // (a) + (e): the pre-run scan.
    // =============================================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreRunScan_ReportsExactlyTheNpcWhoseOutfitHoldsHeadgear_UntilAChoiceIsStored(bool skinCarried)
    {
        using var fx = new WigRouteFixture("conf-scan");
        var spec = Build(fx, skinCarried, withBareheadedNpc: true);
        var settings = NewSettings(fx, spec, skyPatcherMode: false, "scan" + (skinCarried ? "-skin" : "-outfit"));

        var provider = fx.TryBuildProvider(_output);
        if (provider == null) return;

        Directory.CreateDirectory(settings.OutputDirectory);
        using var harness = new NpcChooserHarness(provider, settings);
        var patcher = harness.Patcher;
        var validator = harness.Validator;
        void Append(string message, bool isError, bool force) => _output.WriteLine(message);
        patcher.ConnectToUILogger(Append, (_, _, _) => { }, () => { }, () => { });
        validator.ConnectToUILogger(Append, (_, _, _) => { }, () => { }, () => { });

        var modSettingsMap = patcher.BuildModSettingsMap();
        await patcher.PreInitializationLogicAsync();
        await validator.ScreenSelectionsAsync(modSettingsMap, "<All NPCs>", CancellationToken.None);
        var validSelections = validator.GetScreeningCache().Where(kv => kv.Value.SelectionIsValid).ToList();
        validSelections.Select(kv => kv.Key).Should().BeEquivalentTo(new[] { spec.Npc, spec.BareheadedNpc!.Value },
            "both selections must pass screening, or the scan is not being asked about them");

        var forwarder = harness.Container.Resolve<WigForwarder>();
        int outputRecordsBefore = provider.OutputMod.EnumerateMajorRecords().Count();

        // (a) exactly the NPC whose outfit holds the helmet; the bareheaded one is not a case.
        var cases = forwarder.FindUnansweredHeadgearConflicts(validSelections);

        var found = cases.Should().ContainSingle("only one of the two NPCs wears headgear").Subject;
        found.TargetNpcFormKey.Should().Be(spec.Npc);
        found.TargetNpcDisplay.Should().Contain("NPC2Conf_Rikke");
        found.AppearanceModName.Should().Be(WigRouteFixture.ModName);
        var conflict = found.Conflicts.Should().ContainSingle().Subject;
        conflict.Wig.FormKey.Should().Be(skinCarried ? spec.WigArma : spec.WigArmo!.Value);
        conflict.Wig.Label.Should().Be(spec.WigLabel);
        conflict.Wig.Slots.Should().Equal(31);
        conflict.Headgear.FormKey.Should().Be(spec.Helmet);
        conflict.Headgear.EditorID.Should().Be("NPC2Conf_Helmet");
        conflict.Headgear.Name.Should().Be("Officer Helmet");
        conflict.Headgear.Slots.Should().Equal(31, 42);
        conflict.OutfitFormKey.Should().Be(spec.OfficerOutfit);
        conflict.OutfitEditorID.Should().Be("NPC2Conf_OfficerOutfit");
        conflict.SharedSlots.Should().Equal(31);

        provider.OutputMod.EnumerateMajorRecords().Count().Should().Be(outputRecordsBefore,
            "the scan must not create or change any record in the output mod");

        // (e) a stored choice, whichever it is, answers the question.
        foreach (var choice in Enum.GetValues<WigHeadgearChoice>())
        {
            settings.SetWigHeadgearChoice(new[] { spec.Npc }, choice);
            forwarder.FindUnansweredHeadgearConflicts(validSelections).Should().BeEmpty(
                $"the user already answered '{choice}' for this NPC");
        }

        settings.ClearWigHeadgearChoices();
        forwarder.FindUnansweredHeadgearConflicts(validSelections).Should().ContainSingle(
            "forgetting the answers brings the question back");

        // Nothing to ask when no selected mod forwards wigs to outfits.
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin;
        forwarder.FindUnansweredHeadgearConflicts(validSelections).Should().BeEmpty();

        provider.OutputMod.EnumerateMajorRecords().Count().Should().Be(outputRecordsBefore);
    }

    // =============================================================================================
    // The NPC list's question about ONE row (the hatched selection indicator), which has no
    // screening pass to lean on and must still say what the scan says.
    // =============================================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PerSelectionQuery_SaysWhatTheScanSays_WithoutScreening_AndWhateverIsStored(bool skinCarried)
    {
        using var fx = new WigRouteFixture("conf-row");
        var spec = Build(fx, skinCarried, withBareheadedNpc: true);
        var settings = NewSettings(fx, spec, skyPatcherMode: false, "row" + (skinCarried ? "-skin" : "-outfit"));

        var provider = fx.TryBuildProvider(_output);
        if (provider == null) return;

        Directory.CreateDirectory(settings.OutputDirectory);
        using var harness = new NpcChooserHarness(provider, settings);
        var forwarder = harness.Container.Resolve<WigForwarder>();
        int outputRecordsBefore = provider.OutputMod.EnumerateMajorRecords().Count();

        // Asked cold, as the NPC list asks: no pre-initialisation, no screening.
        var forRikke = forwarder.FindHeadgearConflictsForSelection(spec.Npc, spec.Npc, spec.ModSetting);
        var forBareheaded = forwarder.FindHeadgearConflictsForSelection(
            spec.BareheadedNpc!.Value, spec.BareheadedNpc.Value, spec.ModSetting);

        var conflict = forRikke.Should().ContainSingle("the helmeted NPC has the conflict").Subject;
        conflict.Wig.Label.Should().Be(spec.WigLabel);
        conflict.Headgear.FormKey.Should().Be(spec.Helmet);
        conflict.OutfitFormKey.Should().Be(spec.OfficerOutfit);
        conflict.SharedSlots.Should().Equal(31);
        forBareheaded.Should().BeEmpty("an outfit without headgear is no conflict");

        // The same answer the pre-run scan gives for the same selections.
        var patcher = harness.Patcher;
        var validator = harness.Validator;
        void Append(string message, bool isError, bool force) => _output.WriteLine(message);
        patcher.ConnectToUILogger(Append, (_, _, _) => { }, () => { }, () => { });
        validator.ConnectToUILogger(Append, (_, _, _) => { }, () => { }, () => { });
        await patcher.PreInitializationLogicAsync();
        await validator.ScreenSelectionsAsync(patcher.BuildModSettingsMap(), "<All NPCs>", CancellationToken.None);
        var validSelections = validator.GetScreeningCache().Where(kv => kv.Value.SelectionIsValid).ToList();
        var scanned = forwarder.FindUnansweredHeadgearConflicts(validSelections).Should().ContainSingle().Subject;
        scanned.TargetNpcFormKey.Should().Be(spec.Npc);
        scanned.Conflicts.Select(c => c.Describe()).Should().Equal(forRikke.Select(c => c.Describe()));

        // Unlike the scan it does not skip an answered NPC: the list still marks a stored "forward",
        // and decides for itself what a stored answer means for the row.
        foreach (var choice in Enum.GetValues<WigHeadgearChoice>())
        {
            settings.SetWigHeadgearChoice(new[] { spec.Npc }, choice);
            forwarder.FindHeadgearConflictsForSelection(spec.Npc, spec.Npc, spec.ModSetting)
                .Should().ContainSingle($"the records still show the conflict with '{choice}' stored");
        }
        settings.ClearWigHeadgearChoices();

        // What the list pays per row on these synthetic plugins (the real cost is the load order's).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int repeats = 200;
        for (int i = 0; i < repeats; i++)
        {
            forwarder.FindHeadgearConflictsForSelection(spec.Npc, spec.Npc, spec.ModSetting);
        }
        sw.Stop();
        _output.WriteLine($"FindHeadgearConflictsForSelection, synthetic fixture ({(skinCarried ? "skin" : "outfit")} wig): " +
                          $"{sw.Elapsed.TotalMilliseconds / repeats:F3} ms per call over {repeats} calls");

        // Nothing is resolved at all under another wig mode.
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin;
        forwarder.FindHeadgearConflictsForSelection(spec.Npc, spec.Npc, spec.ModSetting).Should().BeEmpty();
        forwarder.FindHeadgearConflictsForSelection(spec.Npc, spec.Npc, null).Should().BeEmpty();

        provider.OutputMod.EnumerateMajorRecords().Count().Should().Be(outputRecordsBefore,
            "the question must not create or change any record in the output mod");
    }

    // =============================================================================================
    // (b): no stored choice, or Forward: the wig is forwarded as before, and the user is told.
    // =============================================================================================

    [Theory]
    [InlineData(false, true, null)]
    [InlineData(false, false, null)]
    [InlineData(true, true, null)]
    [InlineData(true, false, null)]
    [InlineData(false, true, WigHeadgearChoice.Forward)]
    public async Task NoChoiceOrForward_ForwardsTheWig_AndWarnsWithTheNamesAndSlotNumbers(
        bool skyPatcherMode, bool skinCarried, WigHeadgearChoice? storedChoice)
    {
        using var fx = new WigRouteFixture("conf-fwd");
        var spec = Build(fx, skinCarried);
        var settings = NewSettings(fx, spec, skyPatcherMode, Label(skyPatcherMode, skinCarried));
        if (storedChoice is { } choice) settings.SetWigHeadgearChoice(new[] { spec.Npc }, choice);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode, skinCarried));
        if (run == null) return;

        AssertCleanWrite(run);

        // The wig is in the outfit the NPC is given, beside the helmet: today's behaviour.
        var outNpc = PatchedNpc(run);
        var outfit = run.Output.Outfits.Should().ContainSingle(o => o.FormKey == outNpc.DefaultOutfit.FormKey,
            $"[{run.Label}] the NPC must be given the outfit duplicate the forwarder made").Subject;
        ItemKeys(outfit).Should().Contain(spec.Helmet, "the helmet stays in the duplicated outfit");
        ItemKeys(outfit).Should().Contain(k => WigItemKeys(run, spec).Contains(k),
            "with no choice to the contrary the wig is forwarded into the outfit");
        if (skyPatcherMode)
        {
            var directives = run.DirectivesFor(spec.Npc);
            directives.Should().NotBeNull().And.ContainKey("outfitDefault",
                "in SkyPatcher mode the forwarded outfit reaches the actor through this directive");
        }

        // ...and the run log says so, with every name and number read from the records.
        run.Log.Should().Contain(WarningLead);
        run.Log.Should().Contain(
            $"wig '{spec.WigLabel}' (slot 31) was added to outfit 'NPC2Conf_OfficerOutfit', which holds " +
            "'Officer Helmet (NPC2Conf_Helmet)' (slots 31 and 42); both use slot 31");
        run.Log.Split('\n').Should().Contain(l => l.StartsWith("  - ") && l.Contains("NPC2Conf_Rikke") &&
                                                  l.Contains("both use slot 31"),
            "the warning lists the NPC under the header");
    }

    // =============================================================================================
    // (c): Omit: the headgear wins, the wig is dropped.
    // =============================================================================================

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Omit_LeavesTheWigOutOfTheOutfit_AndOffTheSkin_AndKeepsTheHelmet(
        bool skyPatcherMode, bool skinCarried)
    {
        using var fx = new WigRouteFixture("conf-omit");
        var spec = Build(fx, skinCarried);
        var settings = NewSettings(fx, spec, skyPatcherMode, Label(skyPatcherMode, skinCarried));
        settings.SetWigHeadgearChoice(new[] { spec.Npc }, WigHeadgearChoice.Omit);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode, skinCarried));
        if (run == null) return;

        AssertCleanWrite(run);

        var outNpc = PatchedNpc(run);
        var wigKeys = WigItemKeys(run, spec);

        // No wig in any outfit the run wrote, and no outfit was duplicated on the wig's account.
        run.Output.Outfits.Should().NotContain(o => ItemKeys(o).Any(k => wigKeys.Contains(k)),
            $"[{run.Label}] the user chose to omit the wig");
        run.Output.Outfits.Should().BeEmpty(
            $"[{run.Label}] with nothing added and nothing stripped there is no reason to copy the outfit");
        run.Output.Armors.Should().NotContain(a => a.EditorID != null &&
                                                   a.EditorID.StartsWith(MintedWigPrefix, StringComparison.Ordinal),
            $"[{run.Label}] no wearable wig armor is generated for a wig that is not worn");

        // The helmet is still what the NPC wears: the outfit that holds it is untouched.
        if (skyPatcherMode)
        {
            var directives = run.DirectivesFor(spec.Npc);
            directives.Should().NotBeNull($"[{run.Label}] the ini must carry a line for the NPC");
            directives!.Should().NotContainKey("outfitDefault",
                $"[{run.Label}] no outfit is delivered, so the NPC keeps the officer outfit and its helmet");
        }
        else
        {
            outNpc.DefaultOutfit.FormKey.Should().Be(spec.OfficerOutfit,
                $"[{run.Label}] the NPC keeps the officer outfit, which holds the helmet");
        }

        // None left on the skin: ForwardToOutfit takes a skin-carried wig off the skin, and so does Omit.
        if (skinCarried)
        {
            var armature = SkinArmatureEditorIds(run, outNpc);
            armature.Should().NotBeNull($"[{run.Label}] the NPC must wear the skin duplicate the forwarder made");
            armature.Should().BeEquivalentTo(new[] { "NPC2Conf_BodyAA" },
                $"[{run.Label}] the wig armature is stripped from the skin exactly as when it is forwarded");
        }

        run.Log.Should().NotContain(WarningLead, "nothing was forwarded onto the headgear");
        run.Log.Should().Contain("is not added to outfit 'NPC2Conf_OfficerOutfit'",
            "one line per NPC says the stored choice was applied");
    }

    // =============================================================================================
    // (d): ConvertToHeadParts: this NPC's wig goes through the head-part converter.
    // =============================================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertChoice_DeliversTheWigAsHeadParts_AndKeepsTheHelmet(bool skyPatcherMode)
    {
        using var fx = new WigRouteFixture("conf-conv");
        var spec = Build(fx, skinCarried: true, convertible: true);
        var settings = NewSettings(fx, spec, skyPatcherMode, Label(skyPatcherMode, true));
        settings.SetWigHeadgearChoice(new[] { spec.Npc }, WigHeadgearChoice.ConvertToHeadParts);

        using var run = await fx.RunAsync(settings, _output, Label(skyPatcherMode, true), configure: h =>
        {
            // A synthetic fixture cannot supply parseable meshes; stub the converter's NIF seams as
            // WigRouteTwoModeTests does, so the record-minting path is the real code.
            var converter = h.HeadPartWigConverter;
            converter.RenderShapeNamesProvider = _ => new[] { "wigMain", "wigExtra" };
            converter.PartitionProbe = (_, _) => true;
            converter.PhysicsXmlProvider = _ => Array.Empty<string>();
        });
        if (run == null) return;

        AssertCleanWrite(run);

        var outNpc = PatchedNpc(run);
        var headPartEids = outNpc.HeadParts.Select(l =>
            run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID
            ?? $"(not in output: {l.FormKey})").ToList();
        headPartEids.Should().Contain(e => e != null && e.StartsWith("NPC2Wig_", StringComparison.Ordinal),
            $"[{run.Label}] the converter's wig head part must be on the NPC");

        var wigKeys = WigItemKeys(run, spec);
        run.Output.Outfits.Should().NotContain(o => ItemKeys(o).Any(k => wigKeys.Contains(k)),
            $"[{run.Label}] a converted wig is not also worn as an outfit item");
        var armature = SkinArmatureEditorIds(run, outNpc);
        armature.Should().NotBeNull();
        armature.Should().BeEquivalentTo(new[] { "NPC2Conf_BodyAA" },
            $"[{run.Label}] the converted wig armature is stripped from the skin");

        if (skyPatcherMode)
        {
            run.DirectivesFor(spec.Npc)!.Should().NotContainKey("outfitDefault",
                $"[{run.Label}] the NPC keeps the officer outfit and its helmet");
        }
        else
        {
            outNpc.DefaultOutfit.FormKey.Should().Be(spec.OfficerOutfit,
                $"[{run.Label}] the NPC keeps the officer outfit, which holds the helmet");
        }

        run.Log.Should().NotContain(WarningLead, "the wig is no longer competing for the slot");

        // The resolver the output validator consults reaches the same per-NPC mode the patcher used.
        settings.GetEffectiveWigModeForNpc(spec.ModSetting, outNpc, spec.Npc)
            .Should().Be(WigHandlingMode.ConvertToHeadParts);
    }

    // =============================================================================================
    // No conflict: a stored choice changes nothing.
    // =============================================================================================

    [Theory]
    [InlineData(null)]
    [InlineData(WigHeadgearChoice.Omit)]
    [InlineData(WigHeadgearChoice.ConvertToHeadParts)]
    public async Task NpcWithoutHeadgear_IsForwardedAsBefore_WhateverChoiceIsStored(WigHeadgearChoice? staleChoice)
    {
        using var fx = new WigRouteFixture("conf-none");
        var spec = Build(fx, skinCarried: true, convertible: true, withBareheadedNpc: true);
        var bare = spec.BareheadedNpc!.Value;
        var settings = NewSettings(fx, spec, skyPatcherMode: false, "record-nohat-" + (staleChoice?.ToString() ?? "none"));
        settings.SelectedAppearanceMods.Remove(spec.Npc); // patch only the NPC with no headgear
        if (staleChoice is { } choice) settings.SetWigHeadgearChoice(new[] { bare }, choice);

        using var run = await fx.RunAsync(settings, _output, "record-nohat", configure: h =>
        {
            var converter = h.HeadPartWigConverter;
            converter.RenderShapeNamesProvider = _ => new[] { "wigMain", "wigExtra" };
            converter.PartitionProbe = (_, _) => true;
            converter.PhysicsXmlProvider = _ => Array.Empty<string>();
        });
        if (run == null) return;

        AssertCleanWrite(run);

        var outNpc = PatchedNpc(run);
        var outfit = run.Output.Outfits.Should().ContainSingle(o => o.FormKey == outNpc.DefaultOutfit.FormKey).Subject;
        ItemKeys(outfit).Should().Contain(k => WigItemKeys(run, spec).Contains(k),
            "with no headgear in the outfit the wig is forwarded exactly as before");
        outNpc.HeadParts.Select(l => run.Output.HeadParts.FirstOrDefault(h => h.FormKey == l.FormKey)?.EditorID)
            .Should().NotContain(e => e != null && e.StartsWith("NPC2Wig_", StringComparison.Ordinal),
                "a stored choice applies to a conflict that exists, not to an NPC without one");
        run.Log.Should().NotContain(WarningLead);
    }
}
