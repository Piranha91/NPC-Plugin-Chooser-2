using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Newtonsoft.Json.Linq;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pins the missing-outfit-assets metadata contract: attire (outfit/headgear)
/// meshes that didn't resolve/render and attire textures that couldn't decode
/// are stamped under their OWN key so they route to the outfit-asset icon rather
/// than the base NPC missing-asset icon. Unlike the stale-physics-config notices
/// (see <see cref="PhysicsConfigNoticeMetadataTests"/>) these ARE real missing
/// assets — MugshotStalenessChecker treats them as re-render-eligible under
/// AutoUpdateMugshotsWithMissingOutfitAssets. But like every render OUTPUT, they must
/// never perturb the settings hash (the re-render signal is the key's presence,
/// not a hash change) and must stay out of the base missing-mesh/texture arrays.
/// </summary>
public class MissingOutfitAssetsMetadataTests
{
    private static readonly FormKey Npc = FormKey.Factory("01326F:Skyrim.esm");

    private static string BuildJson(
        IReadOnlyList<string>? outfitAssets,
        IReadOnlyList<string>? physicsNotices = null,
        IReadOnlyList<string>? baseMeshes = null,
        IReadOnlyList<string>? baseTextures = null) =>
        InternalMugshotMetadata.Build(
            Npc, new InternalMugshotSettings(),
            effectiveIncludeDefaultOutfit: true, effectiveIncludeHeadgear: false,
            effectiveOutfitIdentity: "none",
            missingMeshes: baseMeshes,
            missingTextures: baseTextures,
            physicsConfigNotices: physicsNotices,
            missingOutfitAssets: outfitAssets);

    [Fact]
    public void OutfitAssets_RoundTrip_ThroughOwnKey()
    {
        var assets = new List<string>
        {
            "Outfit:10C5FF:Skyrim.esm:24: mesh not found (meshes\\USKP\\Armor\\Steel\\F\\gauntletskha_1.nif)",
            "Outfit texture not found: textures\\armor\\steel\\gauntlets_d.dds",
        };
        var json = BuildJson(assets);

        InternalMugshotMetadata.TryReadMissingOutfitAssets(json)
            .Should().BeEquivalentTo(assets);
    }

    [Fact]
    public void OutfitAssets_DoNotLeakIntoBaseMissingAssetArrays()
    {
        // The whole point of the split: an outfit mesh/texture problem must NOT
        // surface on the base NPC missing-asset icon.
        var json = BuildJson(new[] { "Outfit:10C5FF:Skyrim.esm:24: mesh not found (x.nif)" });

        InternalMugshotMetadata.TryReadMissingAssets(json, out var meshes, out var textures);
        meshes.Should().BeEmpty();
        textures.Should().BeEmpty();
    }

    [Fact]
    public void OutfitAssets_AreSeparateFromPhysicsNotices()
    {
        // Both live on the same icon but under distinct keys with distinct
        // staleness semantics — they must not bleed into each other.
        var json = BuildJson(
            outfitAssets: new[] { "Outfit texture not found: t.dds" },
            physicsNotices: new[] { "some stale physics link notice" });

        InternalMugshotMetadata.TryReadMissingOutfitAssets(json)
            .Should().ContainSingle().Which.Should().Be("Outfit texture not found: t.dds");
        InternalMugshotMetadata.TryReadPhysicsConfigNotices(json)
            .Should().ContainSingle().Which.Should().Be("some stale physics link notice");
    }

    [Fact]
    public void BaseAndOutfit_Coexist_WithoutCrossContamination()
    {
        var json = BuildJson(
            outfitAssets: new[] { "Outfit texture not found: outfit.dds" },
            baseMeshes: new[] { "meshes\\actors\\character\\head.nif" },
            baseTextures: new[] { "textures\\face_d.dds" });

        InternalMugshotMetadata.TryReadMissingAssets(json, out var meshes, out var textures);
        meshes.Should().ContainSingle().Which.Should().Be("meshes\\actors\\character\\head.nif");
        textures.Should().ContainSingle().Which.Should().Be("textures\\face_d.dds");
        InternalMugshotMetadata.TryReadMissingOutfitAssets(json)
            .Should().ContainSingle().Which.Should().Be("Outfit texture not found: outfit.dds");
    }

    [Fact]
    public void OutfitAssets_DoNotPerturbSettingsHash()
    {
        // They are a render output, not an input — so, exactly like the base
        // missing-asset arrays, they must never change the settings hash.
        // (Their re-render trigger is the checker reading the key, not the hash.)
        var withAssets = JObject.Parse(BuildJson(new[] { "Outfit texture not found: t.dds" }));
        var without = JObject.Parse(BuildJson(null));

        withAssets["settings_hash"]!.Value<string>()
            .Should().Be(without["settings_hash"]!.Value<string>());
    }

    [Fact]
    public void OutfitAssets_EmptyList_OmitsKeyEntirely()
    {
        JObject.Parse(BuildJson(new List<string>()))
            .Should().NotContainKey("missing_outfit_assets");
        JObject.Parse(BuildJson(null))
            .Should().NotContainKey("missing_outfit_assets");
    }

    [Fact]
    public void Read_AbsentKeyOrMalformedJson_YieldsEmpty()
    {
        InternalMugshotMetadata.TryReadMissingOutfitAssets(BuildJson(null))
            .Should().BeEmpty();
        InternalMugshotMetadata.TryReadMissingOutfitAssets("not json {{{")
            .Should().BeEmpty();
        InternalMugshotMetadata.TryReadMissingOutfitAssets("")
            .Should().BeEmpty();
    }
}
