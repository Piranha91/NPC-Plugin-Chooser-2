using SkyRender;
using SkyRender.Character;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The DDS upload policy is the setting's only route to the cache and to the v20 settings hash.
/// Everything here goes through the pure helpers and the hash's explicit stamp parameter, so no
/// test mutates the process environment or <see cref="TextureUploadPolicy.MaxFinalMipDimension"/>,
/// which parallel hash tests read.
/// </summary>
public class TextureUploadPolicyTests
{
    [Theory]
    // An already-set variable (or the DdsMaxFinalMip.txt trigger, which sets it) wins over the setting.
    [InlineData("decoded", true, "decoded")]
    [InlineData("16", false, "16")]
    // Unset: the setting decides; on leaves the library default in force.
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData(null, false, "decoded")]
    [InlineData("  ", false, "decoded")]
    public void EffectiveEnvironmentValue_FollowsPrecedence(string? current, bool useCompressed, string? expected)
        => TextureUploadPolicy.EffectiveEnvironmentValue(current, useCompressed).Should().Be(expected);

    [Fact]
    public void ParseAsCacheDoes_UnsetIsLibraryDefault_InvalidFallsBackToDecoded()
    {
        TextureUploadPolicy.ParseAsCacheDoes(null).Should().Be(DdsMipPolicy.DefaultMaximumFinalMipDimension);
        TextureUploadPolicy.ParseAsCacheDoes("decoded").Should().BeNull();
        TextureUploadPolicy.ParseAsCacheDoes("nonsense").Should().BeNull();
    }

    [Theory]
    [InlineData(null, "decoded")]
    [InlineData(4, "4")]
    [InlineData(int.MaxValue, "unlimited")]
    public void StampFor_NamesTheRoute(int? max, string expected)
        => TextureUploadPolicy.StampFor(max).Should().Be(expected);

    [Fact]
    public void Schema20Hash_DriftsWithPolicy()
    {
        var cfg = new InternalMugshotSettings();
        string compressed = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 20, textureUploadPolicyStamp: "4");
        string decoded = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 20, textureUploadPolicyStamp: "decoded");
        compressed.Should().NotBe(decoded);
    }

    /// <summary>The default flip must not re-stale the existing library: a v19 tile is compared at
    /// schema 19, which excludes the policy.</summary>
    [Fact]
    public void Schema19Hash_IgnoresPolicy()
    {
        var cfg = new InternalMugshotSettings();
        string compressed = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 19, textureUploadPolicyStamp: "4");
        string decoded = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 19, textureUploadPolicyStamp: "decoded");
        compressed.Should().Be(decoded);
    }

    [Fact]
    public void Setting_DefaultsOn()
        => new InternalMugshotSettings().UseCompressedTextures.Should().BeTrue();
}
