using System.IO;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// The per-mod <b>Appearance records only</b> option (<see cref="ModSetting.AppearanceOverridesOnly"/>),
/// end to end, in each of the four override-mode combinations: Include and Include As New, each
/// walking from the NPC and with "Include All".
///
/// <para><b>The fixture.</b> A base plugin in the load order holds an NPC whose skin (an Armor)
/// points at an armature and carries a keyword. The appearance plugin, which is not in the load
/// order, overrides three of those records: the skin and the armature, which decide how the NPC
/// looks, and the keyword, which does not and sits one link away from the skin. With the box
/// ticked only the skin and the armature reach the output; with it clear, all three do, as before
/// the option existed.</para>
///
/// <para>Two more shapes pin that only what is COLLECTED narrows, not the walk: an appearance
/// override that is reached only through a non-appearance override is still found (Include), and
/// under Include As New the record between them is bridged with the load order's version.</para>
///
/// <para>Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class AppearanceOverridesOnlyTests
{
    private readonly ITestOutputHelper _output;
    public AppearanceOverridesOnlyTests(ITestOutputHelper output) => _output = output;

    private static readonly ModKey[] BaseMasters = Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();
    private static readonly ModKey BaseKey = ModKey.FromFileName("NPC2Ao.esp");
    private static readonly ModKey LookKey = ModKey.FromFileName("NPC2AoLook.esp");
    private const string LookMod = "Appearance Only Fixture Look";

    private static bool IsVanillaOrCc(ModKey key)
    {
        if (BaseMasters.Contains(key)) return true;
        var fn = key.FileName.String;
        return fn.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
               || fn.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture
    {
        public required FormKey Npc, Race, Skin, Armature, Keyword;
        /// <summary>Only in the <see cref="Shape.RaceBetween"/> fixture: the race's own skin.</summary>
        public FormKey? RaceSkin;
        /// <summary>Only in the <see cref="Shape.EnchantmentBetween"/> fixture.</summary>
        public FormKey? Enchantment, FormList;
    }

    private enum Shape
    {
        /// <summary>The mod overrides the skin, the armature and the keyword. Default roots.</summary>
        Plain,
        /// <summary>As Plain, plus: the armature names a race whose skin is another Armor, and the
        /// mod overrides that race and that Armor. The mod's Override Roots are WNAM alone, so Race
        /// is NOT an appearance type for it, and the second Armor is reached only THROUGH the
        /// race.</summary>
        RaceBetween,
        /// <summary>The skin carries an enchantment that names a form list. The mod overrides the
        /// enchantment (not an appearance type) and the form list below it (in the set, by ruling),
        /// and NOT the skin: the form list is reached only through the enchantment. Default roots.</summary>
        EnchantmentBetween,
    }

    private sealed record RunResult(ISkyrimModGetter? Out, Fixture Fx, string Log, IDisposable? Handle)
    {
        public bool Skipped => Out == null && Log.Length == 0;
    }

    private async Task<RunResult> Run(string root, RecordOverrideHandlingMode mode, bool includeAll,
        bool appearanceOnly, Shape shape = Shape.Plain)
    {
        var lookFolder = Path.Combine(root, "LookMod");
        var outDir = Path.Combine(root, "Output");
        var envOutDir = Path.Combine(root, "EnvOutput");
        var writeOrder = BaseMasters.Concat(new[] { BaseKey, LookKey }).ToArray();

        // --- base: in the load order ---------------------------------------------------------------
        var baseMod = new SkyrimMod(BaseKey, SkyrimRelease.SkyrimSE);
        var race = baseMod.Races.AddNew();
        race.EditorID = "NPC2Ao_Race";
        race.Flags |= Race.Flag.FaceGenHead; // or screening drops every NPC on it

        var headPart = baseMod.HeadParts.AddNew();
        headPart.EditorID = "NPC2Ao_HP";
        headPart.Type = HeadPart.TypeEnum.Face;

        var armature = baseMod.ArmorAddons.AddNew();
        armature.EditorID = "NPC2Ao_AA";
        armature.Race.SetTo(race);
        armature.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.Clothing };

        var keyword = baseMod.Keywords.AddNew();
        keyword.EditorID = "NPC2Ao_KW";

        var skin = baseMod.Armors.AddNew();
        skin.EditorID = "NPC2Ao_Skin";
        skin.Race.SetTo(race);
        skin.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.Clothing };
        skin.Armature.Add(armature.FormKey.ToLink<IArmorAddonGetter>());
        skin.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>();
        skin.Keywords.Add(keyword.FormKey.ToLink<IKeywordGetter>());

        Armor? raceSkin = null;
        if (shape == Shape.RaceBetween)
        {
            raceSkin = baseMod.Armors.AddNew();
            raceSkin.EditorID = "NPC2Ao_RaceSkin";
            raceSkin.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.Clothing };
            race.Skin.SetTo(raceSkin);
        }

        ObjectEffect? enchantment = null;
        FormList? formList = null;
        if (shape == Shape.EnchantmentBetween)
        {
            formList = baseMod.FormLists.AddNew();
            formList.EditorID = "NPC2Ao_List";
            enchantment = baseMod.ObjectEffects.AddNew();
            enchantment.EditorID = "NPC2Ao_Ench";
            enchantment.Name = "base";
            enchantment.WornRestrictions.SetTo(formList);
            skin.ObjectEffect.SetTo(enchantment);
        }

        var npc = baseMod.Npcs.AddNew();
        npc.EditorID = "NPC2Ao_Npc";
        npc.Name = "NPC2Ao_Npc";
        npc.Race.SetTo(race.FormKey);
        npc.HeadParts.Add(headPart.FormKey);
        npc.WornArmor.SetTo(skin.FormKey);

        var basePath = Path.Combine(root, "Base", BaseKey.FileName);
        WriteMod(baseMod, basePath, writeOrder);

        // --- the appearance plugin: never in the load order ----------------------------------------
        var lookMod = new SkyrimMod(LookKey, SkyrimRelease.SkyrimSE);
        lookMod.Npcs.GetOrAddAsOverride(npc).Height = 1.05f;
        if (shape == Shape.EnchantmentBetween)
        {
            // Not appearance: the enchantment. Appearance (in the set by ruling): the list below it.
            lookMod.ObjectEffects.GetOrAddAsOverride(enchantment!).Name = "edited";
            lookMod.FormLists.GetOrAddAsOverride(formList!).Items.Add(keyword);
        }
        else
        {
            // Appearance: the skin and its armature.
            lookMod.Armors.GetOrAddAsOverride(skin).Name = "edited";
            lookMod.ArmorAddons.GetOrAddAsOverride(armature).BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = BipedObjectFlag.Body | BipedObjectFlag.Hands, ArmorType = ArmorType.Clothing,
            };
            // Not appearance, and within reach: the keyword on the skin.
            lookMod.Keywords.GetOrAddAsOverride(keyword).Color = System.Drawing.Color.FromArgb(0x20, 0x30, 0x40);
        }
        if (shape == Shape.RaceBetween)
        {
            lookMod.Races.GetOrAddAsOverride(race).Description = "edited";
            lookMod.Armors.GetOrAddAsOverride(raceSkin!).Name = "edited";
        }
        WriteMod(lookMod, Path.Combine(lookFolder, LookKey.FileName), writeOrder);
        WriteFaceGen(lookFolder, npc.FormKey);

        var fx = new Fixture
        {
            Npc = npc.FormKey, Race = race.FormKey, Skin = skin.FormKey, Armature = armature.FormKey,
            Keyword = keyword.FormKey, RaceSkin = raceSkin?.FormKey,
            Enchantment = enchantment?.FormKey, FormList = formList?.FormKey,
        };

        // --- environment: vanilla + the base plugin; the appearance plugin stays out ----------------
        IEnumerable<IModListingGetter<ISkyrimModGetter>> Transform(IEnumerable<IModListingGetter<ISkyrimModGetter>> input)
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
            return new RunResult(null, fx, string.Empty, null);
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
            PatchingMode = PatchingMode.CreateAndPatch,
            OutputBackend = OutputBackend.ESP,
            DefaultWigHandlingMode = WigHandlingMode.None,
            DefaultAntlerHandlingMode = AntlerHandlingMode.None,
            TemplateHandlingMode = TemplateHandlingMode.InheritFromTemplate,
            DefaultRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
            DefaultMaxNestedIntervalDepth = 2,
            DefaultIncludeAllOverrides = false,
            PublishForwardedOutfitsToDistributors = false,
            LocalizationLanguage = null,
            BatFilePreCommands = string.Empty,
            BatFilePostCommands = string.Empty,
        };
        settings.ModSettings = new List<ModSetting>
        {
            new()
            {
                DisplayName = LookMod,
                CorrespondingModKeys = new List<ModKey> { LookKey },
                CorrespondingFolderPaths = new List<string> { lookFolder },
                MergeInDependencyRecords = true,
                IncludeOutfits = false,
                CopyAssets = false,
                ModRecordOverrideHandlingMode = mode,
                MaxNestedIntervalDepth = 2,
                IncludeAllOverrides = includeAll,
                AppearanceOverridesOnly = appearanceOnly,
                OverrideTraversalRoots = shape == Shape.RaceBetween
                    ? new HashSet<NpcRootField> { NpcRootField.WornArmor }
                    : null,
            },
        };
        settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)> { [npc.FormKey] = (LookMod, npc.FormKey) };

        Directory.CreateDirectory(outDir);
        var run = await GoldenPatchRunner.RunAsync(provider, settings);
        _output.WriteLine("--- RUN LOG ---\n" + run.Log + "\n--- END RUN LOG ---");

        var outPlugin = Path.Combine(outDir, "NPC.esp");
        if (!File.Exists(outPlugin)) return new RunResult(null, fx, run.Log, null);
        var handle = SkyrimMod.CreateFromBinaryOverlay(outPlugin, SkyrimRelease.SkyrimSE);
        _output.WriteLine("output records: " + string.Join(", ",
            ((ISkyrimModGetter)handle).EnumerateMajorRecords().Select(r => $"{r.Registration.Name} {r.FormKey} '{r.EditorID}'")));
        return new RunResult(handle, fx, run.Log, handle);
    }

    // ------------------------------------------------------------------ Include (overrides in place)

    /// <summary>Under Include the mod's edits are written as overrides of the load order's records,
    /// so each record is looked for under its own FormKey.</summary>
    private async Task Include_KeepsOnlyAppearanceOverrides(bool includeAll, bool appearanceOnly)
    {
        var root = NewRoot();
        try
        {
            var r = await Run(root, RecordOverrideHandlingMode.Include, includeAll, appearanceOnly);
            if (r.Skipped) return;
            using var _ = r.Handle;

            r.Log.Should().NotContain("FATAL SAVE ERROR");
            r.Out.Should().NotBeNull("the output plugin must be written");

            var armature = r.Out!.ArmorAddons.SingleOrDefault(a => a.FormKey == r.Fx.Armature);
            armature.Should().NotBeNull("the armature decides how the NPC looks; its override is kept either way");
            armature!.BodyTemplate!.FirstPersonFlags.Should().Be(BipedObjectFlag.Body | BipedObjectFlag.Hands, "the mod's edit");
            r.Out.Armors.Select(a => a.FormKey).Should().Contain(r.Fx.Skin, "the skin is an appearance record too");

            if (appearanceOnly)
            {
                r.Out.Keywords.Should().BeEmpty(
                    "a keyword does not decide how the NPC looks, so its override is left out when the box is ticked");
            }
            else
            {
                r.Out.Keywords.Select(k => k.FormKey).Should().Contain(r.Fx.Keyword,
                    "with the box clear every override the search finds is forwarded, as before the option existed");
            }
        }
        finally { Cleanup(root); }
    }

    [Fact] public Task Include_Walking_BoxTicked_OnlyAppearanceOverrides() => Include_KeepsOnlyAppearanceOverrides(includeAll: false, appearanceOnly: true);
    [Fact] public Task Include_Walking_BoxClear_EveryOverride() => Include_KeepsOnlyAppearanceOverrides(includeAll: false, appearanceOnly: false);
    [Fact] public Task Include_IncludeAll_BoxTicked_OnlyAppearanceOverrides() => Include_KeepsOnlyAppearanceOverrides(includeAll: true, appearanceOnly: true);
    [Fact] public Task Include_IncludeAll_BoxClear_EveryOverride() => Include_KeepsOnlyAppearanceOverrides(includeAll: true, appearanceOnly: false);

    // ------------------------------------------------------------------ Include As New (private copies)

    /// <summary>Under Include As New the mod's versions become NEW records in the output, named
    /// after their originals, and the NPC is pointed at them.</summary>
    private async Task IncludeAsNew_KeepsOnlyAppearanceOverrides(bool includeAll, bool appearanceOnly)
    {
        var root = NewRoot();
        try
        {
            var r = await Run(root, RecordOverrideHandlingMode.IncludeAsNew, includeAll, appearanceOnly);
            if (r.Skipped) return;
            using var _ = r.Handle;

            r.Log.Should().NotContain("FATAL SAVE ERROR");
            r.Out.Should().NotBeNull("the output plugin must be written");
            var outMod = r.Out!;

            var armatureCopy = outMod.ArmorAddons.SingleOrDefault(a =>
                a.FormKey.ModKey == outMod.ModKey && (a.EditorID ?? "").StartsWith("NPC2Ao_AA", StringComparison.Ordinal));
            armatureCopy.Should().NotBeNull("the armature decides how the NPC looks; the mod's version is copied either way");
            armatureCopy!.BodyTemplate!.FirstPersonFlags.Should().Be(BipedObjectFlag.Body | BipedObjectFlag.Hands, "the mod's edit");

            var skinCopy = outMod.Armors.SingleOrDefault(a =>
                a.FormKey.ModKey == outMod.ModKey && (a.EditorID ?? "").StartsWith("NPC2Ao_Skin", StringComparison.Ordinal));
            skinCopy.Should().NotBeNull("the skin is an appearance record too");
            skinCopy!.Armature.Select(a => a.FormKey).Should().Equal(new[] { armatureCopy.FormKey },
                "the skin copy points at the armature copy");
            outMod.Npcs.Single(n => n.FormKey == r.Fx.Npc).WornArmor.FormKey.Should().Be(skinCopy.FormKey,
                "the NPC wears the copy");

            var keywordCopies = outMod.Keywords.Where(k => k.FormKey.ModKey == outMod.ModKey).ToList();
            var skinKeywords = skinCopy.Keywords!.Select(k => k.FormKey).ToList();
            if (appearanceOnly)
            {
                outMod.Keywords.Should().BeEmpty(
                    "a keyword does not decide how the NPC looks, so the mod's version is not copied when the box is ticked");
                skinKeywords.Should().Equal(new[] { r.Fx.Keyword },
                    "the skin copy keeps pointing at the load order's keyword");
            }
            else
            {
                keywordCopies.Should().ContainSingle(
                    "with the box clear every override the search finds is copied, as before the option existed");
                skinKeywords.Should().Equal(new[] { keywordCopies[0].FormKey });
            }
        }
        finally { Cleanup(root); }
    }

    [Fact] public Task IncludeAsNew_Walking_BoxTicked_OnlyAppearanceOverrides() => IncludeAsNew_KeepsOnlyAppearanceOverrides(includeAll: false, appearanceOnly: true);
    [Fact] public Task IncludeAsNew_Walking_BoxClear_EveryOverride() => IncludeAsNew_KeepsOnlyAppearanceOverrides(includeAll: false, appearanceOnly: false);
    [Fact] public Task IncludeAsNew_IncludeAll_BoxTicked_OnlyAppearanceOverrides() => IncludeAsNew_KeepsOnlyAppearanceOverrides(includeAll: true, appearanceOnly: true);
    [Fact] public Task IncludeAsNew_IncludeAll_BoxClear_EveryOverride() => IncludeAsNew_KeepsOnlyAppearanceOverrides(includeAll: true, appearanceOnly: false);

    // ------------------------------------------------------------------ the walk still passes through

    /// <summary>
    /// The walk is not narrowed, only what it collects. Here the mod's roots are WNAM alone, so
    /// Race is not an appearance type for it; the race's own skin is reached only through the race
    /// the armature names. Its override must still be found, while the race override between the
    /// two is left out.
    /// </summary>
    private async Task Include_Walking_PassesThroughANonAppearanceOverride(bool appearanceOnly)
    {
        var root = NewRoot();
        try
        {
            var r = await Run(root, RecordOverrideHandlingMode.Include, includeAll: false, appearanceOnly, Shape.RaceBetween);
            if (r.Skipped) return;
            using var _ = r.Handle;

            r.Log.Should().NotContain("FATAL SAVE ERROR");
            r.Out.Should().NotBeNull("the output plugin must be written");

            r.Out!.Armors.Select(a => a.FormKey).Should().Contain(r.Fx.RaceSkin!.Value,
                "the Armor below the race is still reached: the search walks through records it does not collect");

            if (appearanceOnly)
            {
                r.Out.Races.Select(x => x.FormKey).Should().NotContain(r.Fx.Race,
                    "with RNAM unticked a race is not an appearance type for this mod, so its override is left out");
                r.Out.Keywords.Should().BeEmpty();
            }
            else
            {
                r.Out.Races.Select(x => x.FormKey).Should().Contain(r.Fx.Race);
                r.Out.Keywords.Select(k => k.FormKey).Should().Contain(r.Fx.Keyword);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact] public Task Include_Walking_BoxTicked_CollectsBelowANonAppearanceOverride() => Include_Walking_PassesThroughANonAppearanceOverride(appearanceOnly: true);
    [Fact] public Task Include_Walking_BoxClear_CollectsTheOverrideBetweenToo() => Include_Walking_PassesThroughANonAppearanceOverride(appearanceOnly: false);

    /// <summary>
    /// Include As New needs a private chain from the NPC down to every copy. Here the copy (a form
    /// list) hangs below an enchantment the mod also overrides. With the box ticked the
    /// enchantment override is left out, so the chain is bridged with a copy of the LOAD ORDER's
    /// enchantment, as for any record the mod does not carry, and never with the mod's version.
    /// </summary>
    private async Task IncludeAsNew_Walking_BridgesAcrossANonAppearanceOverride(bool appearanceOnly)
    {
        var root = NewRoot();
        try
        {
            var r = await Run(root, RecordOverrideHandlingMode.IncludeAsNew, includeAll: false, appearanceOnly,
                Shape.EnchantmentBetween);
            if (r.Skipped) return;
            using var _ = r.Handle;

            r.Log.Should().NotContain("FATAL SAVE ERROR");
            r.Out.Should().NotBeNull("the output plugin must be written");
            var outMod = r.Out!;

            var listCopy = outMod.FormLists.SingleOrDefault(l => l.FormKey.ModKey == outMod.ModKey);
            listCopy.Should().NotBeNull("the form list override is found through the enchantment, ticked or not");
            listCopy!.Items.Select(i => i.FormKey).Should().Contain(r.Fx.Keyword, "the mod's version of the list is copied");

            var enchantmentCopy = outMod.ObjectEffects.SingleOrDefault(e => e.FormKey.ModKey == outMod.ModKey);
            enchantmentCopy.Should().NotBeNull("something has to lead from the skin to the list copy");
            enchantmentCopy!.WornRestrictions.FormKey.Should().Be(listCopy.FormKey);
            enchantmentCopy.Name?.String.Should().Be(appearanceOnly ? "base" : "edited",
                appearanceOnly
                    ? "the mod's enchantment edit is left out: the bridge is the load order's enchantment"
                    : "with the box clear the mod's version of every override is copied");

            var skinCopy = outMod.Armors.SingleOrDefault(a => a.FormKey.ModKey == outMod.ModKey);
            skinCopy.Should().NotBeNull("the skin, which the mod does not touch, bridges the NPC to the enchantment");
            skinCopy!.ObjectEffect.FormKey.Should().Be(enchantmentCopy.FormKey);
            outMod.Npcs.Single(n => n.FormKey == r.Fx.Npc).WornArmor.FormKey.Should().Be(skinCopy.FormKey);
        }
        finally { Cleanup(root); }
    }

    [Fact] public Task IncludeAsNew_Walking_BoxTicked_BridgeIsTheLoadOrdersVersion() => IncludeAsNew_Walking_BridgesAcrossANonAppearanceOverride(appearanceOnly: true);
    [Fact] public Task IncludeAsNew_Walking_BoxClear_TheModsVersionIsCopied() => IncludeAsNew_Walking_BridgesAcrossANonAppearanceOverride(appearanceOnly: false);

    // ------------------------------------------------------------------ helpers

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "NpcAppearanceOnly_" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* overlay may hold a map */ }
    }

    private static void WriteMod(SkyrimMod mod, string path, ModKey[] writeOrder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        mod.BeginWrite.ToPath(path).WithLoadOrder(writeOrder).WithNoDataFolder().Write();
    }

    private static void WriteFaceGen(string modFolder, FormKey npcKey)
    {
        var (meshRel, texRel) = Auxilliary.GetFaceGenSubPathStrings(npcKey, regularized: true);
        foreach (var rel in new[] { meshRel, texRel })
        {
            var path = Path.Combine(modFolder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "npc2-appearance-only-fixture");
        }
    }
}
