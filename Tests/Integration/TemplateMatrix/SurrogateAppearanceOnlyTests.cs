using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// The SkyPatcher surrogate must carry the donor's APPEARANCE and nothing else.
///
/// <para>It is a <c>DeepCopyIn</c> of the donor, so before
/// <c>SkyPatcherInterface.StripNonAppearanceData</c> it also carried the donor's factions,
/// packages, inventory, perks, spells, voice, class, combat style and outfit — and the Patcher's
/// final merge-in walker follows every link on it, duplicating all of those into the output when
/// they live in the appearance plugin. Record mode merges none of them, because there the patched
/// record is an override of the WINNING record, whose non-appearance links are the recipient's own
/// and already in the load order.</para>
///
/// <para>The donor here is given one record of every link-bearing non-appearance kind an
/// <c>Npc</c> has, all in a resource plugin outside the load order — so anything that survives into
/// the output had to have been merged, and is named in the failure. The list was taken by
/// reflecting over <c>Npc</c>'s link-bearing members, so a Mutagen upgrade that adds a field will
/// show up here as an unexplained extra record rather than as silent bloat in users' output.</para>
///
/// <para>Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class SurrogateAppearanceOnlyTests
{
    private readonly ITestOutputHelper _output;

    public SurrogateAppearanceOnlyTests(ITestOutputHelper output) => _output = output;

    /// <summary>Everything the donor supplies that is NOT appearance. None of it may reach the
    /// output in either mode.</summary>
    private static readonly string[] NonAppearanceIdentities =
    {
        "Outfit 'BLEED_Outfit'",
        "Outfit 'BLEED_SleepingOutfit'",
        "Armor 'BLEED_Dress'",
        "Faction 'BLEED_Faction'",
        "Faction 'BLEED_CrimeFaction'",
        "Package 'BLEED_Package'",
        "VoiceType 'BLEED_Voice'",
        "Class 'BLEED_Class'",
        "CombatStyle 'BLEED_CombatStyle'",
        "Spell 'BLEED_Spell'",
        "Perk 'BLEED_Perk'",
        "MiscItem 'BLEED_InvItem'",
        "Keyword 'BLEED_Keyword'",
        "FormList 'BLEED_PackageList'",
        "LeveledItem 'BLEED_DeathItem'",
        "Armor 'BLEED_FarAwayModel'",
    };

    [Fact]
    public async Task Surrogate_CarriesAppearanceOnly_AndMergesWhatRecordModeMerges()
    {
        using var fx = new WigRouteFixture("appearance-only");
        var npc = fx.AddBaseNpc("NPC2Route_Bleed");

        // --- appearance: must be merged, in both modes -------------------------------------------
        var bodyArma = fx.AddResArmorAddon("BLEED_BodyAA", BipedObjectFlag.Body);
        var skin = fx.AddResArmor("BLEED_Skin", bodyArma);

        var headTxst = fx.ResMod.TextureSets.AddNew();
        headTxst.EditorID = "BLEED_HeadTXST";
        headTxst.Diffuse = "textures/bleed/head.dds";

        // --- non-appearance: must NOT be merged, in either mode -----------------------------------
        var dress = fx.AddResArmor("BLEED_Dress", bodyArma);
        var outfit = fx.AddResOutfit("BLEED_Outfit", dress);
        var sleepingOutfit = fx.AddResOutfit("BLEED_SleepingOutfit", dress);
        var farAway = fx.AddResArmor("BLEED_FarAwayModel", bodyArma);

        var faction = fx.ResMod.Factions.AddNew();
        faction.EditorID = "BLEED_Faction";
        var crimeFaction = fx.ResMod.Factions.AddNew();
        crimeFaction.EditorID = "BLEED_CrimeFaction";
        var package = fx.ResMod.Packages.AddNew();
        package.EditorID = "BLEED_Package";
        var packageList = fx.ResMod.FormLists.AddNew();
        packageList.EditorID = "BLEED_PackageList";
        var voice = fx.ResMod.VoiceTypes.AddNew();
        voice.EditorID = "BLEED_Voice";
        var cls = fx.ResMod.Classes.AddNew();
        cls.EditorID = "BLEED_Class";
        var combat = fx.ResMod.CombatStyles.AddNew();
        combat.EditorID = "BLEED_CombatStyle";
        var spell = fx.ResMod.Spells.AddNew();
        spell.EditorID = "BLEED_Spell";
        var perk = fx.ResMod.Perks.AddNew();
        perk.EditorID = "BLEED_Perk";
        var invItem = fx.ResMod.MiscItems.AddNew();
        invItem.EditorID = "BLEED_InvItem";
        var keyword = fx.ResMod.Keywords.AddNew();
        keyword.EditorID = "BLEED_Keyword";
        var deathItem = fx.ResMod.LeveledItems.AddNew();
        deathItem.EditorID = "BLEED_DeathItem";

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.HeadTexture.SetTo(headTxst);
        modNpc.DefaultOutfit.SetTo(outfit);
        modNpc.SleepingOutfit.SetTo(sleepingOutfit);
        modNpc.FarAwayModel.SetTo(farAway);
        modNpc.Voice.SetTo(voice);
        modNpc.Class.SetTo(cls);
        modNpc.CombatStyle.SetTo(combat);
        modNpc.CrimeFaction.SetTo(crimeFaction);
        modNpc.DeathItem.SetTo(deathItem);
        modNpc.DefaultPackageList.SetTo(packageList);
        modNpc.Factions.Add(new RankPlacement { Faction = faction.ToLink(), Rank = 0 });
        modNpc.Packages.Add(package.FormKey.ToLink<IPackageGetter>());
        modNpc.Keywords ??= new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>();
        modNpc.Keywords.Add(keyword.FormKey.ToLink<IKeywordGetter>());
        modNpc.ActorEffect ??= new Noggog.ExtendedList<IFormLinkGetter<ISpellRecordGetter>>();
        modNpc.ActorEffect.Add(spell.FormKey.ToLink<ISpellRecordGetter>());
        modNpc.Perks ??= new Noggog.ExtendedList<PerkPlacement>();
        modNpc.Perks.Add(new PerkPlacement { Perk = perk.ToLink(), Rank = 1 });
        modNpc.Items ??= new Noggog.ExtendedList<ContainerEntry>();
        modNpc.Items.Add(new ContainerEntry
        {
            Item = new ContainerItem { Item = invItem.FormKey.ToLink<IItemGetter>(), Count = 1 },
        });

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var merged = new Dictionary<bool, HashSet<string>>();
        foreach (var skyPatcherMode in new[] { false, true })
        {
            var label = skyPatcherMode ? "skypatcher" : "record";
            var settings = fx.NewSettings(skyPatcherMode, label);
            var modSetting = fx.NewModSetting();
            modSetting.IncludeOutfits = false;
            settings.ModSettings = new List<ModSetting> { modSetting };
            settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
            {
                [npc.FormKey] = (WigRouteFixture.ModName, npc.FormKey),
            };

            using var run = await fx.RunAsync(settings, _output, label);
            if (run == null) return;

            run.Log.Should().NotContain("FATAL SAVE ERROR", $"[{label}] the plugin must be writable");
            run.PluginExists.Should().BeTrue($"[{label}] the patcher must write an output plugin");
            OutputLinkSweep.DumpRecords(run.Output, _output, label);

            // Dropping links must not leave a dangling one behind.
            OutputLinkSweep.AssertNoLinksOutsideLoadOrder(run.Output, run.LoadOrderKeys, _output,
                $"[{label}] stripping the surrogate must not strand a reference");

            var identities = OutputLinkSweep.MergedRecordIdentities(run.Output);

            // The appearance still arrives — a surrogate stripped down to nothing would otherwise
            // satisfy every assertion below.
            identities.Should().Contain("Armor 'BLEED_Skin'", $"[{label}] the donor's skin is appearance");
            identities.Should().Contain("ArmorAddon 'BLEED_BodyAA'", $"[{label}] merged recursively");
            identities.Should().Contain("TextureSet 'BLEED_HeadTXST'", $"[{label}] head texture is appearance");

            var outNpc = run.Output.Npcs.Single();
            outNpc.WornArmor.FormKey.ModKey.Should().Be(run.Output.ModKey, $"[{label}] skin is applied");
            outNpc.HeadTexture.FormKey.ModKey.Should().Be(run.Output.ModKey, $"[{label}] head texture is applied");

            foreach (var bleed in NonAppearanceIdentities)
            {
                identities.Should().NotContain(bleed,
                    $"[{label}] the donor's non-appearance data must not be forwarded into the output");
            }

            merged[skyPatcherMode] = identities.Where(i => !i.StartsWith("Npc ", StringComparison.Ordinal))
                .ToHashSet();
        }

        // The strongest statement of the fix: the two modes now merge the same records.
        merged[true].Except(merged[false]).OrderBy(s => s).ToList().Should().BeEmpty(
            "SkyPatcher mode must not merge records record mode leaves alone");
        merged[false].Except(merged[true]).OrderBy(s => s).ToList().Should().BeEmpty(
            "nor the other way round");
    }

    /// <summary>
    /// The surrogate keeps non-appearance links the user's game can already resolve. Dropping those
    /// too would be churn — they merge nothing and dangle nothing — and would strip Class/Voice off
    /// the record for no gain. Only links that would otherwise have to be MERGED are removed.
    /// </summary>
    [Fact]
    public async Task Surrogate_KeepsNonAppearanceLinksThatResolveInTheLoadOrder()
    {
        using var fx = new WigRouteFixture("appearance-only-vanilla");
        var npc = fx.AddBaseNpc("NPC2Route_VanillaLinks");

        var bodyArma = fx.AddResArmorAddon("VAN_BodyAA", BipedObjectFlag.Body);
        var skin = fx.AddResArmor("VAN_Skin", bodyArma);

        // Vanilla class/voice — in the load order, so nothing has to be merged for them.
        var vanillaVoice = Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.VoiceType.MaleNord.FormKey;
        var vanillaClass = Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Class.CWSoldierClass.FormKey;

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.WornArmor.SetTo(skin);
        modNpc.Voice.SetTo(vanillaVoice);
        modNpc.Class.SetTo(vanillaClass);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var settings = fx.NewSettings(skyPatcherMode: true, "skypatcher");
        var modSetting = fx.NewModSetting();
        settings.ModSettings = new List<ModSetting> { modSetting };
        settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
        {
            [npc.FormKey] = (WigRouteFixture.ModName, npc.FormKey),
        };

        using var run = await fx.RunAsync(settings, _output, "skypatcher");
        if (run == null) return;

        run.Log.Should().NotContain("FATAL SAVE ERROR");
        OutputLinkSweep.AssertNoLinksOutsideLoadOrder(run.Output, run.LoadOrderKeys, _output,
            "keeping load-order links must stay writable");

        var outNpc = run.Output.Npcs.Single();
        outNpc.Voice.FormKey.Should().Be(vanillaVoice,
            "a vanilla voice type costs nothing to keep — it merges nothing and dangles nothing — " +
            "so the strip must leave it alone");
        outNpc.Class.FormKey.Should().Be(vanillaClass,
            "same for a vanilla class, which also keeps the record structurally complete");
    }
}
