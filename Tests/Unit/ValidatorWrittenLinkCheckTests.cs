using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="Validator"/>'s written-link check — the pre-run screen for links the output plugin
/// cannot legally reference.
///
/// <para><b>The hole it closes.</b> The older master check vets the masters a plugin DECLARES. A
/// plugin's references to its OWN records declare no master at all, so nothing screened them —
/// yet copying such a record into the output turns that self-reference into a reference to the
/// source plugin, which then has to be a master of the output. With the plugin absent from the
/// load order and merge-in off, Mutagen refuses the write at the very end of the run.</para>
///
/// <para>Motivating specimen: Bandit War.esp's overrides of vanilla bandits are templated for
/// stats/inventory/AI with the Traits flag OFF (they own their faces), and their TPLT points at a
/// record inside Bandit War.esp itself. The mod is disabled in the mod manager and its merge-in is
/// off, so all 98 selections produced a dangling TPLT — but only under SkyPatcher, whose surrogate
/// is a blanket DeepCopyIn of the donor. Record mode never copies a TPLT the donor does not
/// inherit its FACE through, which is why the same configuration patched cleanly there.</para>
///
/// <para>Both seams under test are pure statics, exercised directly the way
/// <see cref="ValidatorMasterCheckTests"/> exercises <c>IsMasterSatisfied</c>.</para>
/// </summary>
public class ValidatorWrittenLinkCheckTests
{
    private static readonly ModKey AbsentKey = MutagenFixtures.Mk("Bandit War.esp");
    private static readonly ModKey Resource = MutagenFixtures.Mk("ProjectJaKhaJay.esp");
    private static readonly ModKey Vanilla = MutagenFixtures.Mk("Skyrim.esm");

    private static ModSetting Mod(bool mergeIn, ModKey[] plugins, ModKey[]? resourceOnly = null,
        Dictionary<ModKey, bool>? overrides = null, bool includeOutfits = false) => new()
    {
        DisplayName = "Lawless - A Bandit Overhaul",
        MergeInDependencyRecords = mergeIn,
        CorrespondingModKeys = plugins.ToList(),
        ResourceOnlyModKeys = new HashSet<ModKey>(resourceOnly ?? new ModKey[0]),
        NpcFormKeys = new HashSet<FormKey> { FormKey.Factory("000801:Bandit War.esp") },
        PluginMergeInOverrides = overrides ?? new Dictionary<ModKey, bool>(),
        IncludeOutfits = includeOutfits,
    };

    private static HashSet<ModKey> Absent(ModSetting mod, IEnumerable<ModKey>? loadOrder = null,
        IEnumerable<ModKey>? implicits = null)
        => Reflect.InvokeStatic<HashSet<ModKey>>(typeof(Validator), "ComputeAbsentPlugins",
            mod,
            (loadOrder ?? Enumerable.Empty<ModKey>()).ToList(),
            new HashSet<ModKey>(implicits ?? Enumerable.Empty<ModKey>()))!;

    private static IReadOnlyList<(string Field, FormKey Key, Type? Type)> WrittenLinks(
        INpcGetter appearanceRecord, INpcGetter donor, bool includeOutfit = false,
        bool useSkyPatcherMode = false)
        => Reflect.InvokeStatic<IEnumerable<(string Field, FormKey Key, Type? Type)>>(typeof(Validator),
            "EnumerateWrittenLinks", appearanceRecord, donor, includeOutfit, useSkyPatcherMode)!.ToList();

    /// <summary>Field/key pairs only — the resolve-check's Type is asserted separately.</summary>
    private static IReadOnlyList<(string Field, FormKey Key)> WrittenPairs(INpcGetter appearanceRecord,
        INpcGetter donor, bool includeOutfit = false, bool useSkyPatcherMode = false)
        => WrittenLinks(appearanceRecord, donor, includeOutfit, useSkyPatcherMode)
            .Select(l => (l.Field, l.Key)).ToList();

    private static bool IncludeOutfit(ModSetting mod, FormKey npc,
        Dictionary<FormKey, OutfitOverride>? overrides = null)
        => Reflect.InvokeStatic<bool>(typeof(Validator), "ResolveIncludeOutfit",
            mod, npc, overrides ?? new Dictionary<FormKey, OutfitOverride>());

    /// <summary>A donor templated for stats/inventory/AI with Traits deliberately OFF.</summary>
    private static Npc NonTraitsTemplatedDonor(SkyrimMod mod, INpcGetter template)
    {
        var donor = MutagenFixtures.NewNpc(mod, "OREO_EncBandit01ChampionNordF");
        donor.Template.SetTo(template.FormKey);
        donor.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Stats |
                                            NpcConfiguration.TemplateFlag.Inventory |
                                            NpcConfiguration.TemplateFlag.AIData;
        return donor;
    }

    // ── ComputeAbsentPlugins (the gate that keeps this check free) ──────────────────────

    [Fact]
    public void AbsentPlugins_IsEmptyForAModWhosePluginsAreAllInTheLoadOrder()
    {
        // The overwhelmingly common case — the whole check short-circuits here, resolving nothing.
        // Such a mod's donors are already live in the user's game, so its links are exactly as
        // valid as the game itself and the output cannot add breakage that was not there.
        var mod = Mod(mergeIn: false, new[] { AbsentKey, Resource });

        Absent(mod, loadOrder: new[] { Vanilla, AbsentKey, Resource }).Should().BeEmpty();
    }

    [Fact]
    public void AbsentPlugins_CountsMergedInPluginsToo()
    {
        // Merging fixes the "output cannot reference it" half, but not the version-drift half: the
        // donor still comes out of a file the user is not running. So the gate must still open.
        var mod = Mod(mergeIn: true, new[] { AbsentKey, Resource });

        Absent(mod, loadOrder: new[] { Vanilla }).Should().BeEquivalentTo(new[] { AbsentKey, Resource });
    }

    [Fact]
    public void AbsentPlugins_NamesEveryPluginTheUserIsNotRunning()
    {
        var mod = Mod(mergeIn: false, new[] { AbsentKey, Resource }, resourceOnly: new[] { Resource });

        Absent(mod, loadOrder: new[] { Vanilla }).Should().BeEquivalentTo(new[] { AbsentKey, Resource });
    }

    [Fact]
    public void AbsentPlugins_TreatsImplicitVanillaMastersAsPresent()
    {
        var mod = Mod(mergeIn: false, new[] { Vanilla });

        Absent(mod, implicits: new[] { Vanilla }).Should().BeEmpty();
    }

    // ── EnumerateWrittenLinks ───────────────────────────────────────────────────────────

    [Fact]
    public void WrittenLinks_RecordMode_ScreensATemplateOnlyWhenTheFaceIsInherited()
    {
        var mod = MutagenFixtures.NewMod("Bandit War.esp");
        var template = MutagenFixtures.NewNpc(mod, "OREO_EncBandit01TemplateChampion");

        // Traits OFF: record mode's SyncTemplateInheritance never mirrors this TPLT, so screening
        // it would reject a selection that patches cleanly.
        var statsOnly = NonTraitsTemplatedDonor(mod, template);
        WrittenPairs(statsOnly, statsOnly).Select(l => l.Field).Should().NotContain("Template");

        // Traits ON: the TPLT IS written onto the recipient, and can dangle.
        var inheritor = MutagenFixtures.NewNpc(mod, "Inheritor", traitsTemplate: true, template: template);
        WrittenPairs(inheritor, inheritor).Should().Contain(("Template", template.FormKey));
    }

    [Fact]
    public void WrittenLinks_SkyPatcherMode_ScreensTheTemplateEvenWithoutTraits()
    {
        // The surrogate is a DeepCopyIn, so it carries the donor's TPLT whether or not the donor
        // inherits its face through it — which is exactly how Bandit War.esp's bandits mastered the
        // output to a plugin outside the load order.
        var mod = MutagenFixtures.NewMod("Bandit War.esp");
        var template = MutagenFixtures.NewNpc(mod, "OREO_EncBandit01TemplateChampion");
        var statsOnly = NonTraitsTemplatedDonor(mod, template);

        WrittenPairs(statsOnly, statsOnly, useSkyPatcherMode: true)
            .Should().Contain(("Template", template.FormKey));
    }

    [Fact]
    public void WrittenLinks_CoverEveryCopiedAppearanceField_AndIndexHeadParts()
    {
        var mod = MutagenFixtures.NewMod("Appearance.esp");
        var race = MutagenFixtures.NewRace(mod, "TestRace");
        var skin = mod.Armors.AddNew();
        var headPart0 = mod.HeadParts.AddNew();
        var headPart1 = mod.HeadParts.AddNew();
        var hairColor = mod.Colors.AddNew();
        var headTexture = mod.TextureSets.AddNew();
        var outfit = mod.Outfits.AddNew();

        var donor = MutagenFixtures.NewNpc(mod, "Donor", race: race);
        donor.WornArmor.SetTo(skin);
        donor.HeadParts.Add(headPart0.ToLink());
        donor.HeadParts.Add(headPart1.ToLink());
        donor.HairColor.SetTo(hairColor);
        donor.HeadTexture.SetTo(headTexture);
        donor.DefaultOutfit.SetTo(outfit);

        var withOutfit = WrittenPairs(donor, donor, includeOutfit: true);

        withOutfit.Should().Contain(("Race", race.FormKey));
        withOutfit.Should().Contain(("WornArmor(skin)", skin.FormKey));
        withOutfit.Should().Contain(("HeadTexture", headTexture.FormKey));
        withOutfit.Should().Contain(("HairColor", hairColor.FormKey));
        withOutfit.Should().Contain(("HeadParts[0]", headPart0.FormKey));
        withOutfit.Should().Contain(("HeadParts[1]", headPart1.FormKey));
        withOutfit.Should().Contain(("DefaultOutfit", outfit.FormKey));

        // An outfit that is not copied cannot dangle.
        WrittenPairs(donor, donor, includeOutfit: false)
            .Select(l => l.Key).Should().NotContain(outfit.FormKey);
    }

    [Fact]
    public void WrittenLinks_SkipUnsetFields()
    {
        var mod = MutagenFixtures.NewMod("Sparse.esp");
        var race = MutagenFixtures.NewRace(mod, "TestRace");
        var donor = MutagenFixtures.NewNpc(mod, "Sparse", race: race);

        var links = WrittenPairs(donor, donor, includeOutfit: true);

        links.Should().ContainSingle().Which.Should().Be(("Race", race.FormKey));
        links.Select(l => l.Key).Should().NotContain(FormKey.Null);
    }

    [Fact]
    public void WrittenLinks_UnderAFlatten_ScreenTheTerminusAppearanceButTheDonorsInheritance()
    {
        // The flatten overlays the TERMINUS's appearance (Auxilliary.CopyInheritedAppearance), so
        // the donor's own head parts are overwritten and must not be screened — while the TPLT
        // that is mirrored still comes from the donor.
        var mod = MutagenFixtures.NewMod("Flatten.esp");
        var donorHeadPart = mod.HeadParts.AddNew();
        var terminusHeadPart = mod.HeadParts.AddNew();

        var terminus = MutagenFixtures.NewNpc(mod, "Terminus");
        terminus.HeadParts.Add(terminusHeadPart.ToLink());

        var donor = MutagenFixtures.NewNpc(mod, "Donor", traitsTemplate: true, template: terminus);
        donor.HeadParts.Add(donorHeadPart.ToLink());

        var links = WrittenPairs(appearanceRecord: terminus, donor: donor);

        links.Select(l => l.Key).Should().Contain(terminusHeadPart.FormKey);
        links.Select(l => l.Key).Should().NotContain(donorHeadPart.FormKey);
        links.Should().Contain(("Template", terminus.FormKey));
    }

    // ── ResolveIncludeOutfit ────────────────────────────────────────────────────────────

    [Fact]
    public void IncludeOutfit_FallsBackToTheModSettingWithNoOverride()
    {
        var npc = FormKey.Factory("000801:Skyrim.esm");

        IncludeOutfit(Mod(false, new[] { AbsentKey }, includeOutfits: true), npc).Should().BeTrue();
        IncludeOutfit(Mod(false, new[] { AbsentKey }, includeOutfits: false), npc).Should().BeFalse();
    }

    [Fact]
    public void IncludeOutfit_HonoursThePerNpcOverride()
    {
        var npc = FormKey.Factory("000801:Skyrim.esm");
        var modOn = Mod(false, new[] { AbsentKey }, includeOutfits: true);
        var modOff = Mod(false, new[] { AbsentKey }, includeOutfits: false);

        IncludeOutfit(modOn, npc, new() { [npc] = OutfitOverride.No }).Should().BeFalse();
        IncludeOutfit(modOff, npc, new() { [npc] = OutfitOverride.Yes }).Should().BeTrue();

        // UseModSetting defers, and an override for a DIFFERENT NPC is ignored.
        IncludeOutfit(modOn, npc, new() { [npc] = OutfitOverride.UseModSetting }).Should().BeTrue();
        IncludeOutfit(modOn, npc, new() { [FormKey.Factory("000802:Skyrim.esm")] = OutfitOverride.No })
            .Should().BeTrue();
    }

    // ── What the screen covers vs. what the strip removes ───────────────────────────────

    /// <summary>The plain-Create sweep: that mode hands the surrogate the donor's whole record, so
    /// every link on it lands in the output and every link is screened.</summary>
    private static IReadOnlyList<FormKey> UnsatisfiableRecordLinks(INpcGetter donor,
        params ModKey[] satisfiable)
        => donor.EnumerateFormLinks()
            .Where(l => !l.FormKey.IsNull && !satisfiable.Contains(l.FormKey.ModKey))
            .Select(l => l.FormKey)
            .ToList();

    [Fact]
    public void CreateAndPatch_ScreensOnlyWhatSurvivesTheStrip()
    {
        // The surrogate keeps appearance data plus Class and drops everything else
        // (SkyPatcherInterface.StripNonAppearanceData), so Voice and CombatStyle cannot reach the
        // output and screening them would reject selections that patch cleanly.
        var plugin = MutagenFixtures.NewMod("Bandit War.esp");
        var donor = MutagenFixtures.NewNpc(plugin, "OREO_EncBandit01ChampionNordF",
            race: MutagenFixtures.NewRace(plugin, "BanditRace"));
        donor.Class.SetTo(plugin.Classes.AddNew());
        donor.Voice.SetTo(plugin.VoiceTypes.AddNew());
        donor.CombatStyle.SetTo(plugin.CombatStyles.AddNew());

        WrittenPairs(donor, donor, useSkyPatcherMode: true).Select(l => l.Field)
            .Should().BeEquivalentTo(new[] { "Race", "Class" });
    }

    [Fact]
    public void PlainCreate_SweepSeesEveryLinkOnTheDonor()
    {
        // Plain Create forwards the donor's whole record by contract — appearanceOnly is passed
        // only in the Create-and-Patch branch — so nothing is stripped and everything is screened.
        var plugin = MutagenFixtures.NewMod("Bandit War.esp");
        var donor = MutagenFixtures.NewNpc(plugin, "OREO_EncBandit01ChampionNordF",
            race: MutagenFixtures.NewRace(plugin, "BanditRace"));
        donor.Class.SetTo(plugin.Classes.AddNew());
        donor.Voice.SetTo(plugin.VoiceTypes.AddNew());
        donor.CombatStyle.SetTo(plugin.CombatStyles.AddNew());

        UnsatisfiableRecordLinks(donor, MutagenFixtures.Mk("Skyrim.esm"))
            .Should().HaveCount(4)
            .And.Contain(donor.Class.FormKey)
            .And.Contain(donor.Voice.FormKey)
            .And.Contain(donor.CombatStyle.FormKey);
    }

    [Fact]
    public void RecordSweep_IsCleanWhenTheDonorOnlyReferencesSatisfiablePlugins()
    {
        // The ordinary appearance replacer: its donor points at vanilla, so nothing is swept up
        // and the selection is not rejected.
        var vanilla = MutagenFixtures.NewMod("Skyrim.esm");
        var donor = MutagenFixtures.NewNpc(vanilla, "VanillaDonor",
            race: MutagenFixtures.NewRace(vanilla, "NordRace"));
        donor.Class.SetTo(vanilla.Classes.AddNew());

        UnsatisfiableRecordLinks(donor, vanilla.ModKey).Should().BeEmpty();
    }

    // ── Types, which drive the resolve-check ────────────────────────────────────────────

    [Fact]
    public void WrittenLinks_CarryTheGetterTypeForEachField()
    {
        // The resolve-check asks the link cache "does this record exist", and a typed lookup is
        // both cheaper and more accurate than an untyped one — so every field must name its type.
        var mod = MutagenFixtures.NewMod("Typed.esp");
        var race = MutagenFixtures.NewRace(mod, "R");
        var template = MutagenFixtures.NewNpc(mod, "T");
        var donor = MutagenFixtures.NewNpc(mod, "Donor", race: race, template: template);
        donor.WornArmor.SetTo(mod.Armors.AddNew());
        donor.HeadTexture.SetTo(mod.TextureSets.AddNew());
        donor.HairColor.SetTo(mod.Colors.AddNew());
        donor.HeadParts.Add(mod.HeadParts.AddNew().ToLink());
        donor.DefaultOutfit.SetTo(mod.Outfits.AddNew());
        donor.Class.SetTo(mod.Classes.AddNew());

        var byField = WrittenLinks(donor, donor, includeOutfit: true, useSkyPatcherMode: true)
            .ToDictionary(l => l.Field, l => l.Type);

        byField["Race"].Should().Be(typeof(IRaceGetter));
        byField["WornArmor(skin)"].Should().Be(typeof(IArmorGetter));
        byField["HeadTexture"].Should().Be(typeof(ITextureSetGetter));
        byField["HairColor"].Should().Be(typeof(IColorRecordGetter));
        byField["HeadParts[0]"].Should().Be(typeof(IHeadPartGetter));
        byField["DefaultOutfit"].Should().Be(typeof(IOutfitGetter));
        byField["Template"].Should().Be(typeof(INpcSpawnGetter));
        byField["Class"].Should().Be(typeof(IClassGetter));
        byField.Values.Should().NotContainNulls();
    }

    [Fact]
    public void WrittenLinks_ScreenClassInSkyPatcherModeOnly()
    {
        // Class is the one non-appearance link the surrogate keeps — CNAM is required, so it cannot
        // be nulled like the rest, which makes screening the only way to catch a bad one. Record
        // mode never copies it at all.
        var mod = MutagenFixtures.NewMod("Bandit War.esp");
        var donor = MutagenFixtures.NewNpc(mod, "Donor", race: MutagenFixtures.NewRace(mod, "R"));
        var cls = mod.Classes.AddNew();
        donor.Class.SetTo(cls);

        WrittenPairs(donor, donor, useSkyPatcherMode: true).Should().Contain(("Class", cls.FormKey));
        WrittenPairs(donor, donor).Select(l => l.Field).Should().NotContain("Class");
    }

    // ── The reported configuration, end to end across the two seams ─────────────────────

    [Fact]
    public void MotivatingCase_TheDanglingTemplateIsNowScreenable()
    {
        // Bandit War.esp: disabled in the mod manager, merge-in off, and the donor's TPLT points
        // at one of its own records. Nothing about the plugin's DECLARED masters is wrong — every
        // one of them is vanilla — so only a content check can see this.
        var mod = Mod(mergeIn: false, new[] { AbsentKey, Resource }, resourceOnly: new[] { Resource });
        var loadOrder = new[] { Vanilla };
        var implicits = new[] { Vanilla };

        Absent(mod, loadOrder, implicits).Should().Contain(AbsentKey);

        var plugin = MutagenFixtures.NewMod("Bandit War.esp");
        var template = MutagenFixtures.NewNpc(plugin, "OREO_EncBandit01TemplateChampion");
        var donor = NonTraitsTemplatedDonor(plugin, template);
        donor.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;

        var offending = WrittenPairs(donor, donor).Where(l =>
            !Reflect.InvokeStatic<bool>(typeof(Validator), "IsMasterSatisfied",
                l.Key.ModKey, mod, loadOrder.ToList(), new HashSet<ModKey>(implicits),
                new Dictionary<ModKey, ModSetting>(), null)).ToList();

        offending.Should().ContainSingle()
            .Which.Should().Be(("Template", template.FormKey));
    }
}
