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
/// <see cref="RecastRaceCompatibility"/>: whether a Recast-only cross-race swap renders the donor's
/// body. Pure over in-memory records; the resolvers look them up in the one fixture plugin.
/// </summary>
public class RecastRaceCompatibilityTests
{
    private readonly SkyrimMod _mod = MutagenFixtures.NewMod("Races.esp");

    private Race NewRace(string editorId, string skeleton = "actors\\character\\character assets\\skeleton.nif",
        float height = 1f)
    {
        var race = _mod.Races.AddNew();
        race.EditorID = editorId;
        race.SkeletalModel = new GenderedItem<SimpleModel?>(
            new SimpleModel { File = skeleton }, new SimpleModel { File = skeleton });
        race.Height = new GenderedItem<float>(height, height);
        return race;
    }

    private ArmorAddon NewAddon(string model, params Race[] races)
    {
        var arma = _mod.ArmorAddons.AddNew();
        arma.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body | BipedObjectFlag.Hands };
        arma.WorldModel = new GenderedItem<Model?>(new Model { File = model }, new Model { File = model });
        arma.Race.SetTo(races[0]);
        foreach (var extra in races.Skip(1)) arma.AdditionalRaces.Add(extra.ToLink());
        return arma;
    }

    private Armor NewSkin(params ArmorAddon[] addons)
    {
        var armor = _mod.Armors.AddNew();
        foreach (var a in addons) armor.Armature.Add(a.ToLink());
        return armor;
    }

    private Npc NewDonor(Race race, Armor? wornArmor = null)
    {
        var npc = MutagenFixtures.NewNpc(_mod, "Donor");
        npc.Race.SetTo(race);
        if (wornArmor != null) npc.WornArmor.SetTo(wornArmor);
        return npc;
    }

    private RecastRaceCompatibility.Verdict Check(Npc donor, Race donorRace, Race targetRace) =>
        RecastRaceCompatibility.Check(donor, donorRace, targetRace,
            fk => _mod.Armors.TryGetValue(fk, out var a) ? a : null,
            fk => _mod.ArmorAddons.TryGetValue(fk, out var a) ? a : null);

    [Fact]
    public void SameRace_IsAlwaysFine()
    {
        var nord = NewRace("NordRace");
        Check(NewDonor(nord), nord, nord).Ok.Should().BeTrue();
    }

    [Fact]
    public void DonorSkinWithOneAddonForBothRaces_IsFine()
    {
        // The Breton-on-Nord control from the houseCARL lab: one shared body addon.
        var breton = NewRace("BretonRace");
        var nord = NewRace("NordRace");
        var skin = NewSkin(NewAddon("actors\\character\\body.nif", breton, nord));

        var verdict = Check(NewDonor(breton, skin), breton, nord);

        verdict.Ok.Should().BeTrue();
        verdict.Note.Should().BeNull("the races have the same height");
    }

    [Fact]
    public void DonorSkinWithoutAnAddonForTheTargetRace_IsAnInvisibleBody()
    {
        var custom = NewRace("CustomRace");
        var nord = NewRace("NordRace");
        var skin = NewSkin(NewAddon("custom\\body.nif", custom));

        Check(NewDonor(custom, skin), custom, nord).Failure.Should().Contain("invisible");
    }

    [Fact]
    public void NoDonorSkin_RaceSkinsWithDifferentMeshes_IsASeam()
    {
        var orc = NewRace("OrcRace");
        var nord = NewRace("NordRace");
        orc.Skin.SetTo(NewSkin(NewAddon("actors\\character\\orcbody.nif", orc)));
        nord.Skin.SetTo(NewSkin(NewAddon("actors\\character\\body.nif", nord)));

        Check(NewDonor(orc), orc, nord).Failure.Should().Contain("mesh").And.Contain("seam");
    }

    [Fact]
    public void NoDonorSkin_SameMeshButDifferentSkinTexture_IsAToneChange()
    {
        var orc = NewRace("OrcRace");
        var nord = NewRace("NordRace");
        var orcAddon = NewAddon("actors\\character\\body.nif", orc);
        var orcTexture = _mod.TextureSets.AddNew();
        orcAddon.SkinTexture = new GenderedItem<IFormLinkNullableGetter<ITextureSetGetter>>(
            orcTexture.ToNullableLink(), orcTexture.ToNullableLink());
        orc.Skin.SetTo(NewSkin(orcAddon));
        nord.Skin.SetTo(NewSkin(NewAddon("actors\\character\\body.nif", nord)));

        Check(NewDonor(orc), orc, nord).Failure.Should().Contain("skin tone");
    }

    [Fact]
    public void NoDonorSkin_RaceSkinsWithTheSameMeshAndTexture_IsFine()
    {
        var breton = NewRace("BretonRace");
        var nord = NewRace("NordRace");
        breton.Skin.SetTo(NewSkin(NewAddon("actors\\character\\body.nif", breton)));
        nord.Skin.SetTo(NewSkin(NewAddon("actors\\character\\body.nif", nord)));

        Check(NewDonor(breton), breton, nord).Ok.Should().BeTrue();
    }

    [Fact]
    public void DifferentSkeleton_IsRejected()
    {
        var khajiit = NewRace("KhajiitRace", skeleton: "actors\\character\\character assets\\skeletonbeast.nif");
        var nord = NewRace("NordRace");
        var skin = NewSkin(NewAddon("actors\\character\\body.nif", khajiit, nord));

        Check(NewDonor(khajiit, skin), khajiit, nord).Failure.Should().Contain("skeleton");
    }

    [Fact]
    public void DifferentRaceHeight_IsOnlyANote()
    {
        var altmer = NewRace("HighElfRace", height: 1.1f);
        var nord = NewRace("NordRace", height: 1.03f);
        var skin = NewSkin(NewAddon("actors\\character\\body.nif", altmer, nord));

        var verdict = Check(NewDonor(altmer, skin), altmer, nord);

        verdict.Ok.Should().BeTrue();
        verdict.Note.Should().Contain("height");
    }

    [Fact]
    public void ArmorRaceIndirection_CountsAsCoverage()
    {
        // Chaconne Vilja's shape: a custom race whose ArmorRace is NordRace wears Nord addons.
        var nord = NewRace("NordRace");
        var vilja = NewRace("AAEMNordViljaRace");
        vilja.ArmorRace.SetTo(nord);
        var skin = NewSkin(NewAddon("actors\\character\\body.nif", nord));

        Check(NewDonor(nord, skin), nord, vilja).Ok.Should().BeTrue();
    }
}
