using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pins the depicted-outfit identity-stamp contract: the mugshot cache keys on
/// the resolved outfit FormKey ONLY — never the provenance/mode that produced
/// it — so switching Create ↔ CreateAndPatch (or any other input change) that
/// lands on the same outfit must not re-stale a tile, and "no outfit" is a
/// first-class stamped value.
/// </summary>
public class OutfitIdentityStampTests
{
    private static readonly FormKey Outfit = FormKey.Factory("0ABCDE:Outfits.esp");

    [Fact]
    public void IdentityStamp_IsFormKeyOnly_IndependentOfSource()
    {
        var viaMod = new OutfitDisplayResult
        {
            OutfitFormKey = Outfit,
            Source = OutfitDisplaySource.AppearanceMod,
            UseModScopedResolution = true,
        };
        var viaWinner = new OutfitDisplayResult
        {
            OutfitFormKey = Outfit,
            Source = OutfitDisplaySource.WinningOverride,
            SourceDetail = "something entirely different",
        };
        var viaSkyPatcher = new OutfitDisplayResult
        {
            OutfitFormKey = Outfit,
            Source = OutfitDisplaySource.SkyPatcher,
            WarningText = "overridden",
        };

        viaMod.IdentityStamp.Should().Be(viaWinner.IdentityStamp)
            .And.Be(viaSkyPatcher.IdentityStamp)
            .And.Be(Outfit.ToString());
    }

    [Fact]
    public void IdentityStamp_NoOutfit_IsNoneSentinel()
    {
        OutfitDisplayResult.NoOutfit.IdentityStamp.Should().Be(InternalMugshotMetadata.NoOutfitIdentity);
        new OutfitDisplayResult { OutfitFormKey = null, Source = OutfitDisplaySource.None }
            .IdentityStamp.Should().Be("none");
    }

    [Fact]
    public void SettingsHash_V12_SameOutfitIdentity_SameHash()
    {
        var cfg = new InternalMugshotSettings();
        var a = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 12, true, false, Outfit.ToString());
        var b = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 12, true, false, Outfit.ToString());
        a.Should().Be(b);
    }

    [Fact]
    public void SettingsHash_V12_DifferentOutfitIdentity_DifferentHash()
    {
        var cfg = new InternalMugshotSettings();
        var a = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 12, true, false, Outfit.ToString());
        var b = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 12, true, false, "none");
        a.Should().NotBe(b);
    }

    [Fact]
    public void SettingsHash_V12_NullIdentity_FallsBackToNone()
    {
        var cfg = new InternalMugshotSettings();
        var explicitNone = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 12, false, false, "none");
        var implicitNone = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 12, false, false, null);
        explicitNone.Should().Be(implicitNone);
    }

    [Fact]
    public void SettingsHash_PreV12_IgnoresOutfitIdentity()
    {
        // Pre-v12 PNGs are compared at their stamped schema — the outfit
        // identity must not perturb those hashes, or the feature's rollout
        // would mass-invalidate existing mugshots.
        var cfg = new InternalMugshotSettings();
        var a = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 11, true, false, Outfit.ToString());
        var b = InternalMugshotMetadata.ComputeSettingsHashAtSchema(cfg, 11, true, false, "none");
        a.Should().Be(b);
    }

    [Fact]
    public void Metadata_StampsAndReadsBackEffectiveOutfit()
    {
        var json = InternalMugshotMetadata.Build(
            FormKey.Factory("001234:Skyrim.esm"), new InternalMugshotSettings(),
            effectiveIncludeDefaultOutfit: true, effectiveIncludeHeadgear: false,
            effectiveOutfitIdentity: Outfit.ToString());
        InternalMugshotMetadata.TryReadEffectiveOutfit(json).Should().Be(Outfit.ToString());
    }

    [Fact]
    public void Metadata_PreV12Json_ReadsNull()
    {
        InternalMugshotMetadata.TryReadEffectiveOutfit("{\"renderer\":\"Internal\"}").Should().BeNull();
        InternalMugshotMetadata.TryReadEffectiveOutfit("not json").Should().BeNull();
    }
}
