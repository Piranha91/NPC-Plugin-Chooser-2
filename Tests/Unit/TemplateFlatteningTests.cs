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
            "ResolveAppearanceTerminusRecord", decision, null, new HashSet<string>(), false);

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

    // ---- Patcher.FlattenedFaceGenSubject ------------------------------------------------------
    //
    // The wig→HeadPart converter bakes into the FaceGen this NPC ends up wearing. Under a flatten
    // that mesh is the TERMINUS's, copied to the NPC's own path; otherwise it is the donor's own
    // (null = "use the donor"). Same gate as ResolveAppearanceTerminusRecord, so mesh, record and
    // bake cannot disagree about whose face this is.

    private static FormKey? Subject(FaceGenLadderDecision? decision) =>
        Reflect.InvokeStatic<Patcher, FormKey?>("FlattenedFaceGenSubject", decision);

    [Fact]
    public void FlattenedFaceGenSubject_IsTheTerminus_WhenFlattening()
    {
        Subject(Decision(FaceGenChainStatus.Resolved, flatten: true))
            .Should().Be(MutagenFixtures.Fk("03DE70:Skyrim.esm"));
    }

    [Fact]
    public void FlattenedFaceGenSubject_IsTheDonorsOwn_InInheritMode()
    {
        // An inheriting NPC receives no FaceGen at its own path, so there is no bake target and
        // the converter must go on declining exactly as before.
        Subject(Decision(FaceGenChainStatus.Resolved, flatten: false)).Should().BeNull();
    }

    [Theory]
    [InlineData(FaceGenChainStatus.NotTemplated)]
    [InlineData(FaceGenChainStatus.LeveledTerminus)]
    [InlineData(FaceGenChainStatus.Unfollowable)]
    public void FlattenedFaceGenSubject_IsTheDonorsOwn_WhenNoChainIsFlattened(FaceGenChainStatus chain)
    {
        Subject(Decision(chain, flatten: true)).Should().BeNull();
    }

    // ---- Patcher.RecordOutfitIsInert ----------------------------------------------------------
    //
    // The Inventory template flag makes the engine read the NPC's inventory — default outfit
    // included — from its template, so a wig forwarded into DefaultOutfit is never worn. Measured
    // 2026-07-28 on Captain Hargar: identical head parts, skin and DefaultOutfit link to his
    // terminus, differing only in this flag, and only he came out bald.

    private static bool OutfitInert(Settings settings, INpcGetter winner, INpcGetter donor)
    {
        var patcher = Reflect.Uninitialized<Patcher>();
        Reflect.SetField(patcher, "_settings", settings);
        return Reflect.Invoke<bool>(patcher, "RecordOutfitIsInert", winner, donor);
    }

    private static Npc InventoryTemplated(SkyrimMod mod, string editorId, INpcGetter template)
    {
        var npc = MutagenFixtures.NewNpc(mod, editorId);
        npc.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Inventory;
        npc.Template.SetTo(template);
        return npc;
    }

    [Theory]
    [InlineData(PatchingMode.CreateAndPatch)]
    [InlineData(PatchingMode.Create)]
    public void OutfitIsInert_WhenTheWrittenRecordInheritsItsInventory(PatchingMode mode)
    {
        // Each mode writes a different record, so each has to read the flag off its own.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "Template");
        var plain = MutagenFixtures.NewNpc(mod, "Plain");
        var templated = InventoryTemplated(mod, "Templated", template);

        var settings = new Settings { PatchingMode = mode, UseSkyPatcherMode = false };
        var (winner, donor) = mode == PatchingMode.CreateAndPatch
            ? ((INpcGetter)templated, (INpcGetter)plain)
            : (plain, templated);

        OutfitInert(settings, winner, donor).Should().BeTrue();
        OutfitInert(settings, plain, plain).Should().BeFalse("neither record inherits its inventory");
    }

    [Fact]
    public void OutfitIsNotInert_WithoutATemplateToInheritFrom()
    {
        // The flag alone decides nothing: with no template link there is nowhere for the engine
        // to read an inventory from, so the record's own outfit stands.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, "Dangling");
        npc.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Inventory;

        OutfitInert(new Settings { PatchingMode = PatchingMode.CreateAndPatch }, npc, npc)
            .Should().BeFalse();
    }

    [Fact]
    public void OutfitIsNotInert_InSkyPatcherMode()
    {
        // SkyPatcher applies the outfit at runtime with SetOutfit, which acts on the actor and
        // never consults the record's template flags — so nothing needs rerouting there.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "Template");
        var templated = InventoryTemplated(mod, "Templated", template);

        var settings = new Settings
        {
            PatchingMode = PatchingMode.CreateAndPatch,
            UseSkyPatcherMode = true,
        };

        OutfitInert(settings, templated, templated).Should().BeFalse();
    }

    [Fact]
    public void OutfitIsNotInert_ForTraitsTemplatingAlone()
    {
        // Traits governs the face, not the inventory. A flatten clears Traits and never touches
        // Inventory, so the two must not be conflated.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "Template");
        var traitsOnly = MutagenFixtures.NewNpc(mod, "TraitsOnly", traitsTemplate: true, template: template);

        OutfitInert(new Settings { PatchingMode = PatchingMode.CreateAndPatch }, traitsOnly, traitsOnly)
            .Should().BeFalse();
    }

    // ---- Mugshot template badge (VM_NpcSelectionBar.ShouldTreatTemplateAsPerNpc) --------------
    //
    // Decides whether the issue "!" stays red ("whichever mod you pick, this NPC shows the
    // template's face") or drops to the warning colour ("this mod's copy of that face lands on
    // this NPC's own record"). Must agree with the patcher's own flatten gate — see
    // Patcher.ResolveAppearanceTerminusRecord, which requires mode AND a resolved chain.

    private static bool PerNpc(TemplateHandlingMode mode, bool hasTemplate = true, bool levelled = false) =>
        VM_NpcSelectionBar.ShouldTreatTemplateAsPerNpc(mode, hasTemplate, levelled);

    [Fact]
    public void TemplateBadge_IsPerNpc_OnlyInOwnCopyMode()
    {
        PerNpc(TemplateHandlingMode.GiveEachNpcOwnCopy).Should().BeTrue();
        PerNpc(TemplateHandlingMode.InheritFromTemplate).Should().BeFalse(
            "the default mode really does ignore the mod picked here");
    }

    [Fact]
    public void TemplateBadge_StaysInherited_ForALevelledChain_EvenInOwnCopyMode()
    {
        // The game picks the actor at runtime, so the chain is never flattened whatever the mode
        // says. Whole classes of generic vanilla actors land here, so claiming per-NPC control
        // for them would be wrong on a large population.
        PerNpc(TemplateHandlingMode.GiveEachNpcOwnCopy, levelled: true).Should().BeFalse();
    }

    [Fact]
    public void TemplateBadge_StaysInherited_WithNoTemplateToFollow()
    {
        PerNpc(TemplateHandlingMode.GiveEachNpcOwnCopy, hasTemplate: false).Should().BeFalse();
    }

    [Fact]
    public void PerNpcTemplateTooltip_ReplacesTheInheritWording_AndStaysReadable()
    {
        // It REPLACES the scan-time message (which states the opposite rule) rather than being
        // appended to it, so it has to stand alone: name the mod, name the setting that caused
        // this, and carry none of the jargon or FormKeys the stored message uses.
        var text = VM_NpcSelectionBar.BuildPerNpcTemplateTooltip("Some Appearance Mod");

        text.Should().Contain("Some Appearance Mod", "the user needs to know whose face they get");
        text.Should().Contain(HandlingModeDisplay.ToDisplayString(TemplateHandlingMode.GiveEachNpcOwnCopy),
            "the setting has to be findable from the tooltip");
        text.Should().NotContain("Traits", "'the Traits flag' means nothing to most users");
        text.Should().NotContain(":", "a FormKey would leak in as 'xxxxxx:Plugin.esp'");
    }

    // ---- Randomize's inherited-template note (VM_NpcSelectionBar.BuildInheritedTemplateRandomizeNote)
    //
    // Randomize used to list every templated NPC it could not assign under "had no candidate that
    // passed validation", which reads as breakage: those NPCs have no face of their own, so an
    // unassigned one still shows its template's face in game exactly as intended. They are counted
    // and summarised by this note instead. The same gate as the badge decides which side an NPC
    // lands on, so under GiveEachNpcOwnCopy a real miss keeps its individual entry.

    [Fact]
    public void InheritedTemplateNote_ReadsAsAnOutcome_NotAFailure()
    {
        var text = VM_NpcSelectionBar.BuildInheritedTemplateRandomizeNote(570);

        text.Should().Contain("570");
        text.Should().NotContainAny("failed", "invalid", "could not", "error");
        text.Should().NotContain("Traits", "'the Traits flag' means nothing to most users");
    }

    [Fact]
    public void InheritedTemplateNote_DoesNotSendTheUserToTheTemplateSetting()
    {
        // Deliberate: the outcome is already correct, so naming a mode switch the user never asked
        // about is noise on a message most of them will only skim.
        VM_NpcSelectionBar.BuildInheritedTemplateRandomizeNote(5)
            .Should().NotContain(HandlingModeDisplay.ToDisplayString(TemplateHandlingMode.GiveEachNpcOwnCopy));
    }

    // ---- Randomize's cleared-selection note (VM_NpcSelectionBar.BuildClearedSelectionsRandomizeNote)
    //
    // An NPC randomize could not place ends the run unselected instead of keeping the pick it
    // arrived with. That is the one destructive thing a run does that the confirmation dialog does
    // not enumerate up front (it counts selections to be replaced, not removed), so the summary has
    // to own up to it plainly.

    [Fact]
    public void ClearedSelectionsNote_SaysSelectionsWereRemoved()
    {
        var text = VM_NpcSelectionBar.BuildClearedSelectionsRandomizeNote(91);

        text.Should().Contain("91");
        text.Should().ContainAny("removed", "unselected");
        text.Should().NotContain("Traits", "'the Traits flag' means nothing to most users");
    }

    [Fact]
    public void InheritedTemplateNote_DoesNotClaimTheNpcsWereLeftUntouched()
    {
        // These NPCs are cleared like any other unplaced NPC; only the reason they could not be
        // placed differs. Saying "left unchanged" would describe the older behaviour, under which
        // a stale selection survived the run and later showed up in the output validator paired
        // against a template that had been randomized to a different mod.
        VM_NpcSelectionBar.BuildInheritedTemplateRandomizeNote(570)
            .Should().NotContainAny("unchanged", "untouched", "kept");
    }

    [Theory]
    [InlineData(null, TemplateHandlingMode.GiveEachNpcOwnCopy, TemplateHandlingMode.GiveEachNpcOwnCopy)]
    [InlineData(TemplateHandlingMode.InheritFromTemplate, TemplateHandlingMode.GiveEachNpcOwnCopy, TemplateHandlingMode.InheritFromTemplate)]
    [InlineData(TemplateHandlingMode.GiveEachNpcOwnCopy, TemplateHandlingMode.InheritFromTemplate, TemplateHandlingMode.GiveEachNpcOwnCopy)]
    public void ResolveTemplateHandlingMode_MatchesTheModelSideResolver(
        TemplateHandlingMode? perMod, TemplateHandlingMode global, TemplateHandlingMode expected)
    {
        // The badge reads the override off the VM, the patcher reads it off the model; both must
        // land on the same answer or the tooltip would promise something the run does not do.
        var settings = new Settings { TemplateHandlingMode = global };

        settings.ResolveTemplateHandlingMode(perMod).Should().Be(expected);
        settings.GetEffectiveTemplateHandlingMode(new ModSetting
        {
            DisplayName = "Some Mod",
            ModTemplateHandlingMode = perMod,
        }).Should().Be(expected);
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
