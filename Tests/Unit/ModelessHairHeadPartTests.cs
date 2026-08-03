using System;
using System.Collections.Generic;
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
/// The modeless-head-part gate shared by the wig/antler pipeline and the output validator:
/// <c>FaceGenConsistencyAnalyzer.BearsBakedGeometry</c> and
/// <c>OutputValidator.DonorHasModeledHair</c>.
///
/// <para><b>The bug.</b> A mod that ships its wigs on the SKIN pairs each of them with a modeless
/// bald hair head part — High Poly NPC Overhaul's <c>HighPoly_HairBald</c>, whose record bytes are
/// EDID + DATA(0x06) + PNAM(3=Hair) + RNAM only: no MODL, no NAM0/NAM1. It renders nothing.
/// Under Forward to Skin the patcher nonetheless treated it as real hair: it removed it, minted its
/// own functionally identical <c>NPC2_HairBald</c> in its place, and queued a FaceGen strip for a
/// shape the Creation Kit never baked. On a 3,550-NPC run that produced 3,431 "no shape named
/// [HighPoly_HairBald] found" warnings claiming baked hair might still show (nothing was baked) and
/// 3,446 validation Errors reporting <c>missing [HighPoly_HairBald]; extra [NPC2_HairBald]</c> — a
/// record swapped for a copy of itself, reported as an appearance mismatch.</para>
///
/// <para><b>The rule.</b> A head part participates in removal, stripping and the validator's
/// wig-forwarding excuse only if it contributes baked geometry. Geometry counts on the part itself
/// OR on an ExtraPart, because a modeless parent owning a modeled hairline still renders.</para>
///
/// <para>Pure and deterministic: in-memory Mutagen records, no game install.</para>
/// </summary>
public class ModelessHairHeadPartTests
{
    /// <summary>Resolver over one mod, for the static <c>DonorHasModeledHair</c> walk.</summary>
    private static Func<IFormLinkGetter<IHeadPartGetter>, IHeadPartGetter?> Resolver(SkyrimMod mod)
    {
        var byKey = new Dictionary<FormKey, IHeadPartGetter>();
        foreach (var hp in mod.HeadParts) byKey[hp.FormKey] = hp;
        return link => byKey.TryGetValue(link.FormKey, out var rec) ? rec : null;
    }

    // ── BearsBakedGeometry ──────────────────────────────────────────────────────

    [Fact]
    public void HpnoBaldPlaceholder_BearsNoGeometry()
    {
        var mod = MutagenFixtures.NewMod("HPNO.esp");
        // The real record, field for field.
        var bald = mod.HeadParts.AddNew();
        bald.EditorID = "HighPoly_HairBald";
        bald.Type = HeadPart.TypeEnum.Hair;
        bald.Flags = HeadPart.Flag.Male | HeadPart.Flag.Female;
        bald.ValidRaces.SetTo(FormKey.Factory("0A803F:Skyrim.esm"));

        FaceGenConsistencyAnalyzer.BearsBakedGeometry(bald).Should().BeFalse(
            "no MODL and no NAM0/NAM1 means the Creation Kit bakes no shape named after it");
    }

    [Fact]
    public void ModeledHair_BearsGeometry()
    {
        var mod = MutagenFixtures.NewMod("Mod.esp");
        var hair = MutagenFixtures.NewHeadPart(mod, "RealHair", HeadPart.TypeEnum.Hair);

        FaceGenConsistencyAnalyzer.BearsBakedGeometry(hair).Should().BeTrue();
    }

    [Fact]
    public void ChargenPartsWithoutModel_StillBearGeometry()
    {
        var mod = MutagenFixtures.NewMod("Mod.esp");
        var hp = MutagenFixtures.NewHeadPart(mod, "MorphOnly", HeadPart.TypeEnum.Face, modeless: true);
        hp.Parts.Add(new Part { FileName = @"actors\character\facegendata\morph.tri" });

        FaceGenConsistencyAnalyzer.BearsBakedGeometry(hp).Should().BeTrue(
            "NAM0/NAM1 chargen parts are baked too — the gate is 'renders nothing', not 'has no MODL'");
    }

    [Fact]
    public void NullHeadPart_BearsNoGeometry()
    {
        FaceGenConsistencyAnalyzer.BearsBakedGeometry(null).Should().BeFalse();
    }

    // ── DonorHasModeledHair (the validator's precondition) ───────────────────────

    [Fact]
    public void DonorWithOnlyTheBaldPlaceholder_HasNoModeledHair()
    {
        var mod = MutagenFixtures.NewMod("HPNO.esp");
        var bald = MutagenFixtures.NewHeadPart(mod, "HighPoly_HairBald", HeadPart.TypeEnum.Hair, modeless: true);
        var npc = MutagenFixtures.NewNpc(mod, "Abelone");
        npc.HeadParts.Add(bald.ToLink());

        OutputValidator.DonorHasModeledHair(npc, Resolver(mod)).Should().BeFalse(
            "nothing is removed for this NPC, so the comparison must stay ON — claiming a removal " +
            "would blind it to real head-part damage on every NPC of the mod");
    }

    [Fact]
    public void DonorWithRealHair_HasModeledHair()
    {
        var mod = MutagenFixtures.NewMod("Mod.esp");
        var hair = MutagenFixtures.NewHeadPart(mod, "HairMaleNord10", HeadPart.TypeEnum.Hair);
        var npc = MutagenFixtures.NewNpc(mod, "Npc");
        npc.HeadParts.Add(hair.ToLink());

        OutputValidator.DonorHasModeledHair(npc, Resolver(mod)).Should().BeTrue();
    }

    [Fact]
    public void DonorWithBothRealHairAndThePlaceholder_HasModeledHair()
    {
        // The 13-NPC minority in the measuring run (MaleDremoraHair01, HairMaleNord10, ...
        // alongside HighPoly_HairBald). The real hair IS removed, so the excuse must apply.
        var mod = MutagenFixtures.NewMod("HPNO.esp");
        var real = MutagenFixtures.NewHeadPart(mod, "MaleDremoraHair01", HeadPart.TypeEnum.Hair);
        var bald = MutagenFixtures.NewHeadPart(mod, "HighPoly_HairBald", HeadPart.TypeEnum.Hair, modeless: true);
        var npc = MutagenFixtures.NewNpc(mod, "Npc");
        npc.HeadParts.Add(real.ToLink());
        npc.HeadParts.Add(bald.ToLink());

        OutputValidator.DonorHasModeledHair(npc, Resolver(mod)).Should().BeTrue();
    }

    [Fact]
    public void ModelessParentWithModeledHairline_HasModeledHair()
    {
        // A modeless parent is not automatically a placeholder: its hairline ExtraPart is baked
        // and would clash with the wig, so this NPC's hair does get removed.
        var mod = MutagenFixtures.NewMod("Mod.esp");
        var hairline = MutagenFixtures.NewHeadPart(mod, "HairLine01", HeadPart.TypeEnum.Misc);
        var parent = MutagenFixtures.NewHeadPart(mod, "HairParent", HeadPart.TypeEnum.Hair, modeless: true);
        parent.ExtraParts.Add(hairline.ToLink());
        var npc = MutagenFixtures.NewNpc(mod, "Npc");
        npc.HeadParts.Add(parent.ToLink());

        OutputValidator.DonorHasModeledHair(npc, Resolver(mod)).Should().BeTrue();
    }

    [Fact]
    public void ModeledPartsOfOtherTypes_DoNotCount()
    {
        var mod = MutagenFixtures.NewMod("Mod.esp");
        var eyes = MutagenFixtures.NewHeadPart(mod, "Eyes01", HeadPart.TypeEnum.Eyes);
        var bald = MutagenFixtures.NewHeadPart(mod, "HighPoly_HairBald", HeadPart.TypeEnum.Hair, modeless: true);
        var npc = MutagenFixtures.NewNpc(mod, "Npc");
        npc.HeadParts.Add(eyes.ToLink());
        npc.HeadParts.Add(bald.ToLink());

        OutputValidator.DonorHasModeledHair(npc, Resolver(mod)).Should().BeFalse(
            "only Hair-type parts are superseded by a wig");
    }

    [Fact]
    public void UnresolvableHeadPart_DoesNotCount()
    {
        var mod = MutagenFixtures.NewMod("Mod.esp");
        var npc = MutagenFixtures.NewNpc(mod, "Npc");
        npc.HeadParts.Add(FormKey.Factory("001234:Missing.esp").ToLink<IHeadPartGetter>());

        OutputValidator.DonorHasModeledHair(npc, Resolver(mod)).Should().BeFalse(
            "the patcher's collectors skip unresolvable links too — matching them keeps the " +
            "validator from disagreeing with the patcher on exactly the broken mods where it matters");
    }
}
