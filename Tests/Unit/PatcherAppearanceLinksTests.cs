using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Locks the contract of <c>Patcher.GetAppearanceFormLinks</c>, the building block of the SkyPatcher +
/// Include outfit-override fix. The patcher feeds these links into override discovery in SkyPatcher mode;
/// the fix calls it with <c>includeOutfit: true</c> for that discovery (independent of the user's outfit
/// choice) so an appearance mod's in-place override of an outfit-reachable record - e.g. RS Children
/// Overhaul's edit of ChildClothes01 (0006D92C) on Dorthe's default outfit - is still carried into the
/// output, matching the non-SkyPatcher Include and IncludeAsNew paths. The full end-to-end regression for
/// the fix lives in <c>PatcherGoldenOutputTests</c> (combos 08 and 11).
/// </summary>
public class PatcherAppearanceLinksTests
{
    private static IReadOnlyList<FormKey> AppearanceLinks(Mutagen.Bethesda.Skyrim.INpcGetter npc, bool includeOutfit)
        => Reflect.InvokeStatic<IEnumerable<IFormLinkGetter>>(typeof(Patcher), "GetAppearanceFormLinks", npc, includeOutfit)!
            .Select(l => l.FormKey).ToList();

    [Fact]
    public void DiscoveryLinks_AlwaysIncludeAppearanceFields_AndGateOutfitOnTheFlag()
    {
        var mod = MutagenFixtures.NewMod("OutfitLinks.esp");
        var race = MutagenFixtures.NewRace(mod, "TestRace");
        var armor = mod.Armors.AddNew();
        var headPart = mod.HeadParts.AddNew();
        var hairColor = mod.Colors.AddNew();
        var headTexture = mod.TextureSets.AddNew();
        var outfit = mod.Outfits.AddNew();

        var npc = MutagenFixtures.NewNpc(mod, "TestNpc", race: race);
        npc.WornArmor.SetTo(armor);
        npc.HeadParts.Add(headPart.ToLink());
        npc.HairColor.SetTo(hairColor);
        npc.HeadTexture.SetTo(headTexture);
        npc.DefaultOutfit.SetTo(outfit);

        var withoutOutfit = AppearanceLinks(npc, includeOutfit: false);
        var withOutfit = AppearanceLinks(npc, includeOutfit: true);

        // Appearance fields are always present regardless of the outfit flag.
        foreach (var links in new[] { withoutOutfit, withOutfit })
        {
            links.Should().Contain(armor.FormKey);
            links.Should().Contain(headPart.FormKey);
            links.Should().Contain(hairColor.FormKey);
            links.Should().Contain(headTexture.FormKey);
            links.Should().Contain(race.FormKey);
        }

        // The outfit link is gated on the flag: excluded when false, included when true.
        withoutOutfit.Should().NotContain(outfit.FormKey, "the outfit is gated out when includeOutfit is false");
        withOutfit.Should().Contain(outfit.FormKey,
            "override discovery must always pull the outfit in (this is what the SkyPatcher+Include fix relies on)");
    }
}
