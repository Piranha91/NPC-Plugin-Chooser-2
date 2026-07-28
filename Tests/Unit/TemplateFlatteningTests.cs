using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Template flattening (Settings.TemplateHandlingMode = GiveEachNpcOwnCopy): the shared field
/// copier <see cref="Auxilliary.CopyInheritedAppearance"/> — lifted from SkyPatcherInterface so
/// record mode and SkyPatcher mode flatten identically — and the Patcher's gate that decides
/// whether a terminus record is resolved for flattening at all
/// (<c>Patcher.ResolveAppearanceTerminusRecord</c>).
///
/// The ladder-side consequences of flattening (destination = own path, WinnerInPlace → Winner)
/// are covered in <see cref="FaceGenLadderTests"/>; the SkyPatcher surrogate overlay is covered
/// in <see cref="PatcherTemplateInheritanceTests"/>.
/// </summary>
public class TemplateFlatteningTests
{
    // ---- Auxilliary.CopyInheritedAppearance --------------------------------------------------

    /// <summary>A terminus with every Traits-governed field populated distinctly.</summary>
    private static Npc FullTerminus(SkyrimMod mod)
    {
        var terminus = MutagenFixtures.NewNpc(mod, "Terminus", female: true,
            race: MutagenFixtures.NewRace(mod, "TerminusRace"));
        terminus.HeadTexture.SetTo(mod.TextureSets.AddNew().FormKey);
        terminus.HairColor.SetTo(mod.Colors.AddNew().FormKey);
        terminus.WornArmor.SetTo(mod.Armors.AddNew().FormKey);
        terminus.Height = 1.05f;
        terminus.Weight = 42f;
        terminus.TextureLighting = System.Drawing.Color.FromArgb(10, 20, 30);
        terminus.HeadParts.Add(mod.HeadParts.AddNew().FormKey.ToLink<IHeadPartGetter>());
        terminus.HeadParts.Add(mod.HeadParts.AddNew().FormKey.ToLink<IHeadPartGetter>());
        terminus.FaceMorph = new NpcFaceMorph { BrowsForwardVsBack = 0.5f };
        terminus.FaceParts = new NpcFaceParts();
        terminus.TintLayers.Add(new TintLayer { Index = 3 });
        return terminus;
    }

    [Fact]
    public void CopyInheritedAppearance_CopiesEveryTraitsGovernedField()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var terminus = FullTerminus(mod);
        var target = MutagenFixtures.NewNpc(mod, "Target");

        Auxilliary.CopyInheritedAppearance(target, terminus);

        target.Race.FormKey.Should().Be(terminus.Race.FormKey);
        target.HeadTexture.FormKey.Should().Be(terminus.HeadTexture.FormKey);
        target.HairColor.FormKey.Should().Be(terminus.HairColor.FormKey);
        target.WornArmor.FormKey.Should().Be(terminus.WornArmor.FormKey);
        target.Height.Should().Be(terminus.Height);
        target.Weight.Should().Be(terminus.Weight);
        target.TextureLighting.Should().Be(terminus.TextureLighting);
        target.HeadParts.Select(h => h.FormKey).Should()
            .Equal(terminus.HeadParts.Select(h => h.FormKey));
        target.FaceMorph!.BrowsForwardVsBack.Should().Be(0.5f);
        target.FaceParts.Should().NotBeNull();
        target.TintLayers.Should().ContainSingle().Which.Index.Should().Be((ushort)3);
    }

    [Fact]
    public void CopyInheritedAppearance_ReplacesListsInsteadOfAppending()
    {
        // The target's own head parts / tint layers are the inert donor leftovers; keeping them
        // alongside the terminus's would hand the engine a head no FaceGen matches.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var terminus = FullTerminus(mod);
        var target = MutagenFixtures.NewNpc(mod, "Target");
        target.HeadParts.Add(mod.HeadParts.AddNew().FormKey.ToLink<IHeadPartGetter>());
        target.TintLayers.Add(new TintLayer { Index = 99 });

        Auxilliary.CopyInheritedAppearance(target, terminus);

        target.HeadParts.Select(h => h.FormKey).Should()
            .Equal(terminus.HeadParts.Select(h => h.FormKey));
        target.TintLayers.Should().ContainSingle().Which.Index.Should().Be((ushort)3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyInheritedAppearance_SexFollowsTheFace_BothDirections(bool terminusFemale)
    {
        // Sex drives which head parts and FaceGen the engine builds, so it must follow the
        // terminus in BOTH directions — clearing matters as much as setting.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var terminus = MutagenFixtures.NewNpc(mod, "Terminus", female: terminusFemale);
        var target = MutagenFixtures.NewNpc(mod, "Target", female: !terminusFemale);

        Auxilliary.CopyInheritedAppearance(target, terminus);

        target.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female)
            .Should().Be(terminusFemale);
    }

    [Fact]
    public void CopyInheritedAppearance_DoesNotTouchTheTraitsFlag()
    {
        // Clearing Traits is the CALLER's half of the flatten, kept at the decision site. The
        // copier changing it too would hide the decision and break callers that only want fields.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var terminus = MutagenFixtures.NewNpc(mod, "Terminus");
        var target = MutagenFixtures.NewNpc(mod, "Target", traitsTemplate: true, template: terminus);

        Auxilliary.CopyInheritedAppearance(target, terminus);

        target.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)
            .Should().BeTrue();
        target.Template.FormKey.Should().Be(terminus.FormKey,
            "the TPLT link also drives non-appearance inheritance and must survive");
    }

    // ---- Patcher.ResolveAppearanceTerminusRecord gating --------------------------------------
    //
    // The method needs the environment only on its POSITIVE path (resolving the terminus through
    // the link cache), so an uninitialised Patcher proves the gates: any gated-off input must
    // return null before the environment is ever touched — an NRE here means a gate was removed.

    private static INpcGetter? Resolve(FaceGenLadderDecision? decision) =>
        Reflect.Invoke<INpcGetter>(Reflect.Uninitialized<Patcher>(),
            "ResolveAppearanceTerminusRecord", decision);

    private static FaceGenLadderDecision Decision(FaceGenChainStatus chain, bool flatten) =>
        FaceGenLadder.Classify(new FaceGenLadderInputs(
            NpcIdentifier: "Test NPC",
            TargetFormKey: MutagenFixtures.Fk("083279:Skyrim.esm"),
            DonorFormKey: MutagenFixtures.Fk("083279:Skyrim.esm"),
            SubjectFormKey: MutagenFixtures.Fk("03DE70:Skyrim.esm"),
            ChainStatus: chain,
            ModName: "Some Mod",
            Mode: FaceGenDestinationMode.Record,
            SourceNif: FaceGenAssetPresence.LooseFile,
            SourceDds: FaceGenAssetPresence.LooseFile,
            SourceHasPluginRecord: true,
            OriginRecordExists: true,
            OriginNif: FaceGenAssetPresence.LooseFile,
            OriginDds: FaceGenAssetPresence.LooseFile,
            WinnerNifExists: true,
            WinnerNifOwner: null,
            WinnerDdsExists: true,
            OriginNifCompatible: null,
            WinnerNifCompatible: null,
            LegacyDonorNif: FaceGenAssetPresence.NotFound,
            LegacyDonorDds: FaceGenAssetPresence.NotFound,
            FlattenTemplateChain: flatten));

    [Fact]
    public void NoDecision_ResolvesNothing()
    {
        Resolve(null).Should().BeNull();
    }

    [Fact]
    public void InheritMode_NeverFlattens_EvenWithAResolvedChain()
    {
        // The default setting: a resolved chain stays inherited, in SkyPatcher mode too — this
        // is the behaviour change from the previously unconditional SkyPatcher flatten.
        Resolve(Decision(FaceGenChainStatus.Resolved, flatten: false)).Should().BeNull();
    }

    [Theory]
    [InlineData(FaceGenChainStatus.NotTemplated)]
    [InlineData(FaceGenChainStatus.LeveledTerminus)]
    [InlineData(FaceGenChainStatus.Unfollowable)]
    public void OwnCopyMode_OnlyAResolvedChainQualifies(FaceGenChainStatus chain)
    {
        // A levelled terminus has no fixed face to copy (the game picks an actor at runtime) and
        // an unfollowable chain has no terminus at all — both keep inheriting with the toggle on.
        Resolve(Decision(chain, flatten: true)).Should().BeNull();
    }

    // ---- Per-mod override resolution (Settings.GetEffectiveTemplateHandlingMode) -------------
    //
    // Mirrors the wig/antler per-mod pattern: null = fall back to the global setting. Unlike
    // those resolvers there is deliberately no detection or output-mode gate — see the
    // resolver's doc for why a stale override on a template-less mod is inert.

    [Theory]
    [InlineData(null, TemplateHandlingMode.InheritFromTemplate, TemplateHandlingMode.InheritFromTemplate)]
    [InlineData(null, TemplateHandlingMode.GiveEachNpcOwnCopy, TemplateHandlingMode.GiveEachNpcOwnCopy)]
    [InlineData(TemplateHandlingMode.GiveEachNpcOwnCopy, TemplateHandlingMode.InheritFromTemplate, TemplateHandlingMode.GiveEachNpcOwnCopy)]
    [InlineData(TemplateHandlingMode.InheritFromTemplate, TemplateHandlingMode.GiveEachNpcOwnCopy, TemplateHandlingMode.InheritFromTemplate)]
    public void EffectiveMode_PerModOverrideWins_NullFallsBackToGlobal(
        TemplateHandlingMode? perMod, TemplateHandlingMode global, TemplateHandlingMode expected)
    {
        var settings = new Settings { TemplateHandlingMode = global };
        var mod = new ModSetting { DisplayName = "Some Mod", ModTemplateHandlingMode = perMod };

        settings.GetEffectiveTemplateHandlingMode(mod).Should().Be(expected);
    }

    [Fact]
    public void EffectiveMode_NullModSetting_FallsBackToGlobal()
    {
        var settings = new Settings { TemplateHandlingMode = TemplateHandlingMode.GiveEachNpcOwnCopy };

        settings.GetEffectiveTemplateHandlingMode(null)
            .Should().Be(TemplateHandlingMode.GiveEachNpcOwnCopy);
    }

    [Fact]
    public void HasTemplatedNpcs_TracksTheScannedTemplateNotifications()
    {
        // The dropdown's visibility gate: derived from the persisted per-NPC notification map
        // (same lifecycle as the wig detection sets), specifically the Template entries — a mod
        // with only FaceGen-only notifications is not a templated-NPC mod.
        var npc = MutagenFixtures.Fk("083279:Skyrim.esm");
        var mod = new ModSetting { DisplayName = "Some Mod" };

        mod.HasTemplatedNpcs.Should().BeFalse("no notifications at all");

        mod.NpcFormKeysToNotifications[npc] = (NpcIssueType.FaceGenOnly, "facegen only", null);
        mod.HasTemplatedNpcs.Should().BeFalse("FaceGen-only notifications are not template ones");

        mod.NpcFormKeysToNotifications[npc] = (NpcIssueType.Template, "templated", null);
        mod.HasTemplatedNpcs.Should().BeTrue();
    }
}
