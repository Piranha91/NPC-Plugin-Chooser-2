using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Locks <see cref="OutfitDisplayResolver.ComputeWigPersistence"/> — the shared
/// answer to "will this NPC's wig reach the patch output?", which drives BOTH the
/// mugshot tile's crossed-out has-wig badge and the 3D preview's warning banner.
///
/// <para>The two surfaces diverge on purpose and this decides for both: the
/// mugshot always DRAWS a Default-Outfit wig (it is the NPC's hair) and marks the
/// gap on the badge, while the output-faithful preview leaves the wig to the
/// ordinary Include-Headgear-gated outfit walk and warns in a banner. If this
/// helper is wrong, both lie in the same direction.</para>
///
/// <para>Pure settings + persisted scan data: no record resolution and no
/// environment, so the resolver's other two dependencies are never touched.</para>
/// </summary>
public class WigPersistenceTests
{
    private const string ModName = "FoxGlove - Auri Visual Overhaul";
    private static readonly FormKey Npc = MutagenFixtures.Fk("000D63:018Auri.esp");
    private static readonly FormKey WigArmo = MutagenFixtures.Fk("000808:FoxGloveAuri.esp");
    private static readonly FormKey HairArma = MutagenFixtures.Fk("000807:FoxGloveAuri.esp");

    /// <summary>ComputeWigPersistence reads only Settings and the ModSetting, so the
    /// environment/record-handler dependencies are deliberately left null — a test
    /// that starts needing them has caught a real regression in that guarantee.</summary>
    private static OutfitDisplayResolver Resolver(Settings settings) =>
        new(settings, null!, null!);

    private static ModSetting Mod(bool outfitWig = true, bool wnamWig = false, bool includeOutfits = false)
    {
        var mod = new ModSetting { DisplayName = ModName, IncludeOutfits = includeOutfits };
        var entries = new System.Collections.Generic.List<NpcWigSource>();
        if (outfitWig)
        {
            mod.DetectedWigArmors.Add(WigArmo);
            entries.Add(new NpcWigSource
                { Kind = NpcWigSourceKind.Outfit, RecordFormKey = WigArmo, EditorId = "FoxGlove_Wig" });
        }
        if (wnamWig)
        {
            mod.DetectedWigArmatures.Add(HairArma);
            entries.Add(new NpcWigSource
                { Kind = NpcWigSourceKind.WornArmor, RecordFormKey = HairArma, EditorId = "FoxGlove_HairArma" });
        }
        mod.NpcWigSources[Npc] = entries;
        return mod;
    }

    /// <summary>Wig handling only runs in Create-and-Patch or SkyPatcher output;
    /// the default Settings ctor must not silently be one of those.</summary>
    private static Settings ActiveOutputMode() =>
        new() { PatchingMode = PatchingMode.CreateAndPatch, UseSkyPatcherMode = false };

    [Fact]
    public void NoModOrNoWig_IsPersisted()
    {
        var settings = ActiveOutputMode();

        Resolver(settings).ComputeWigPersistence(Npc, Npc, null)
            .AnyDropped.Should().BeFalse("no mod means nothing to lose");
        Resolver(settings).ComputeWigPersistence(Npc, Npc, new ModSetting { DisplayName = ModName })
            .AnyDropped.Should().BeFalse("this mod supplies no wig for this NPC");
    }

    [Fact]
    public void ModeLeaveAsIs_WithOutfitExcluded_DropsTheWig()
    {
        var settings = ActiveOutputMode();
        settings.DefaultWigHandlingMode = WigHandlingMode.None;

        var result = Resolver(settings).ComputeWigPersistence(Npc, Npc, Mod());

        result.AnyDropped.Should().BeTrue();
        result.Reason.Should().Be(WigDropReason.ModeLeaveAsIs);
        result.DroppedSources.Should().ContainSingle()
            .Which.RecordFormKey.Should().Be(WigArmo);
        result.FixAdvice.Should().Contain("Leave As Is");
    }

    [Theory]
    [InlineData(WigHandlingMode.ForwardToSkin)]
    [InlineData(WigHandlingMode.ForwardToOutfit)]
    [InlineData(WigHandlingMode.ConvertToHeadParts)]
    public void AnyActiveMode_PersistsTheWig(WigHandlingMode mode)
    {
        var settings = ActiveOutputMode();
        settings.DefaultWigHandlingMode = mode;

        Resolver(settings).ComputeWigPersistence(Npc, Npc, Mod())
            .AnyDropped.Should().BeFalse("every non-None mode forwards or bakes the wig");
    }

    [Fact]
    public void PerModOverride_BeatsTheGlobalDefault()
    {
        var settings = ActiveOutputMode();
        settings.DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin;
        var mod = Mod();
        mod.ModWigHandlingMode = WigHandlingMode.None;

        Resolver(settings).ComputeWigPersistence(Npc, Npc, mod)
            .AnyDropped.Should().BeTrue("the per-mod Leave As Is wins over the active global default");
    }

    [Fact]
    public void IncludeOutfits_PersistsTheWigEvenUnderLeaveAsIs()
    {
        var settings = ActiveOutputMode();
        settings.DefaultWigHandlingMode = WigHandlingMode.None;

        Resolver(settings).ComputeWigPersistence(Npc, Npc, Mod(includeOutfits: true))
            .AnyDropped.Should().BeFalse("forwarding the outfit carries the wig inside it");
    }

    [Fact]
    public void PerNpcOutfitOverride_DecidesAgainstTheModFlag()
    {
        var settings = ActiveOutputMode();
        settings.DefaultWigHandlingMode = WigHandlingMode.None;

        // Include Outfits off for the mod, but forced on for this NPC.
        settings.NpcOutfitOverrides[Npc] = OutfitOverride.Yes;
        Resolver(settings).ComputeWigPersistence(Npc, Npc, Mod())
            .AnyDropped.Should().BeFalse("the per-NPC Yes forwards the outfit, wig included");

        // ...and the reverse: on for the mod, forced off for this NPC.
        settings.NpcOutfitOverrides[Npc] = OutfitOverride.No;
        Resolver(settings).ComputeWigPersistence(Npc, Npc, Mod(includeOutfits: true))
            .AnyDropped.Should().BeTrue("the per-NPC No excludes the outfit that carried the wig");
    }

    /// <summary>The override is keyed by the TARGET NPC (the one being patched),
    /// not the appearance donor — they differ for guest appearances.</summary>
    [Fact]
    public void OutfitOverride_IsKeyedByTheTargetNpc()
    {
        var target = MutagenFixtures.Fk("001234:Skyrim.esm");
        var settings = ActiveOutputMode();
        settings.DefaultWigHandlingMode = WigHandlingMode.None;
        settings.NpcOutfitOverrides[target] = OutfitOverride.Yes;

        Resolver(settings).ComputeWigPersistence(Npc, target, Mod())
            .AnyDropped.Should().BeFalse("the target's override is the one the patcher applies");
        Resolver(settings).ComputeWigPersistence(Npc, Npc, Mod())
            .AnyDropped.Should().BeTrue("keyed by the donor, the override must not be found");
    }

    [Fact]
    public void SkinCarriedWig_IsNeverReportedAsDropped()
    {
        var settings = ActiveOutputMode();
        settings.DefaultWigHandlingMode = WigHandlingMode.None;

        Resolver(settings).ComputeWigPersistence(Npc, Npc, Mod(outfitWig: false, wnamWig: true))
            .AnyDropped.Should().BeFalse("a WornArmor wig rides the appearance forward whatever the mode");
    }

    /// <summary>Mixed sources: the skin wig survives, the outfit wig doesn't. The
    /// badge is crossed out (something IS lost) and only the outfit source is named.</summary>
    [Fact]
    public void MixedSources_ReportsOnlyTheOutfitWig()
    {
        var settings = ActiveOutputMode();
        settings.DefaultWigHandlingMode = WigHandlingMode.None;

        var result = Resolver(settings).ComputeWigPersistence(Npc, Npc, Mod(outfitWig: true, wnamWig: true));

        result.AnyDropped.Should().BeTrue();
        result.DroppedSources.Should().ContainSingle()
            .Which.Kind.Should().Be(NpcWigSourceKind.Outfit);
    }

    /// <summary>Plain Create record mode makes wig handling inert whatever the
    /// dropdown says, so the advice has to point at the OUTPUT mode instead.</summary>
    [Fact]
    public void PlainCreateMode_DropsTheWigAndSaysWhy()
    {
        var settings = new Settings
        {
            PatchingMode = PatchingMode.Create,
            UseSkyPatcherMode = false,
            DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin
        };

        var result = Resolver(settings).ComputeWigPersistence(Npc, Npc, Mod());

        result.AnyDropped.Should().BeTrue("wig handling cannot run in plain Create mode");
        result.Reason.Should().Be(WigDropReason.InertInCreateMode);
        result.FixAdvice.Should().Contain("Create and Patch");
    }

    [Fact]
    public void SkyPatcherMode_ActivatesWigHandlingUnderEitherPatchingMode()
    {
        var settings = new Settings
        {
            PatchingMode = PatchingMode.Create,
            UseSkyPatcherMode = true,
            DefaultWigHandlingMode = WigHandlingMode.ForwardToSkin
        };

        Resolver(settings).ComputeWigPersistence(Npc, Npc, Mod())
            .AnyDropped.Should().BeFalse("SkyPatcher output activates wig handling regardless of PatchingMode");
    }

    [Fact]
    public void ManualVeto_UnWigsTheOutfitEntryAndClearsTheWarning()
    {
        var settings = ActiveOutputMode();
        settings.DefaultWigHandlingMode = WigHandlingMode.None;
        var mod = Mod();

        Resolver(settings).ComputeWigPersistence(Npc, Npc, mod)
            .AnyDropped.Should().BeTrue("baseline: detected and dropped");

        // Un-detecting the ARMO makes the entry ineffective, so there is no longer
        // a wig to lose — the badge itself disappears along with the warning.
        mod.DetectedWigArmors.Clear();
        Resolver(settings).ComputeWigPersistence(Npc, Npc, mod)
            .AnyDropped.Should().BeFalse("an entry that isn't an effective wig can't be a dropped wig");
    }

    [Fact]
    public void PersistedResult_CarriesNoTextAndNoSources()
    {
        WigPersistenceResult.Persisted.AnyDropped.Should().BeFalse();
        WigPersistenceResult.Persisted.DroppedSources.Should().BeEmpty();
        WigPersistenceResult.Persisted.FixAdvice.Should().BeEmpty();
    }

    [Fact]
    public void Headline_IsPluralizedForMultipleWigs()
    {
        var single = new WigPersistenceResult(WigDropReason.ModeLeaveAsIs,
            new[] { new NpcWigSource { Kind = NpcWigSourceKind.Outfit } });
        var many = new WigPersistenceResult(WigDropReason.ModeLeaveAsIs,
            new[] { new NpcWigSource { Kind = NpcWigSourceKind.Outfit },
                    new NpcWigSource { Kind = NpcWigSourceKind.Outfit } });

        single.Headline.Should().Contain("wig this mod gives");
        many.Headline.Should().Contain("wigs this mod gives");
    }
}
