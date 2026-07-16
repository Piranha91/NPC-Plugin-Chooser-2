using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Newtonsoft.Json.Linq;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pins the stale-physics-config notice contract: notices stamped on a mugshot
/// PNG (an attire mesh links an SMP/HDT physics XML that doesn't exist — a
/// broken link in the mod itself) are informational ONLY. They must round-trip
/// through their own metadata key, must never surface as missing assets, and
/// must never perturb the settings hash — i.e. nothing about them may ever
/// re-stale a cached mugshot.
/// </summary>
public class PhysicsConfigNoticeMetadataTests
{
    private static readonly FormKey Npc = FormKey.Factory("01326F:Skyrim.esm");

    private static string BuildJson(IReadOnlyList<string>? notices) =>
        InternalMugshotMetadata.Build(
            Npc, new InternalMugshotSettings(),
            effectiveIncludeDefaultOutfit: true, effectiveIncludeHeadgear: false,
            effectiveOutfitIdentity: "none",
            physicsConfigNotices: notices);

    [Fact]
    public void Notices_RoundTrip_ThroughOwnKey()
    {
        var notices = new List<string>
        {
            "Outfit:00080A:Nocturnal Noir Outfit.esp:4194304: the mesh links physics config " +
            "'Meshes\\Obicnii\\NocturnalNoir\\SkirtYXXY.xml' which does not exist",
        };
        var json = BuildJson(notices);

        InternalMugshotMetadata.TryReadPhysicsConfigNotices(json)
            .Should().BeEquivalentTo(notices);
    }

    [Fact]
    public void Notices_DoNotCountAsMissingAssets()
    {
        var json = BuildJson(new[] { "some stale physics link notice" });

        InternalMugshotMetadata.TryReadMissingAssets(json, out var meshes, out var textures);
        meshes.Should().BeEmpty();
        textures.Should().BeEmpty();
    }

    [Fact]
    public void Notices_DoNotPerturbSettingsHash()
    {
        // The staleness checker compares the stamped settings_hash against a
        // recomputed one — if the notices leaked into the hash, every stamped
        // notice would re-stale its mugshot each session (the exact bug this
        // key exists to avoid).
        var withNotices = JObject.Parse(BuildJson(new[] { "notice" }));
        var without = JObject.Parse(BuildJson(null));

        withNotices["settings_hash"]!.Value<string>()
            .Should().Be(without["settings_hash"]!.Value<string>());
    }

    [Fact]
    public void Notices_EmptyList_OmitsKeyEntirely()
    {
        // Matches the missing-asset arrays: the common success case stays small
        // and older readers see no unfamiliar key.
        JObject.Parse(BuildJson(new List<string>()))
            .Should().NotContainKey("physics_config_notices");
        JObject.Parse(BuildJson(null))
            .Should().NotContainKey("physics_config_notices");
    }

    [Fact]
    public void Read_AbsentKeyOrMalformedJson_YieldsEmpty()
    {
        // Pre-existing PNGs stamped before this key existed.
        InternalMugshotMetadata.TryReadPhysicsConfigNotices(BuildJson(null))
            .Should().BeEmpty();
        InternalMugshotMetadata.TryReadPhysicsConfigNotices("not json {{{")
            .Should().BeEmpty();
        InternalMugshotMetadata.TryReadPhysicsConfigNotices("")
            .Should().BeEmpty();
    }
}
