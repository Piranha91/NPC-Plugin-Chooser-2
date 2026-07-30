using System;
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
/// <see cref="WigDetector.EffectiveWnamWigArmatures"/> — the "which ArmorAddons in this skin are
/// wigs right now" walk.
///
/// It had five near-identical copies (patcher forwarder, head-part converter, output validator, 3D
/// preview hair-hiding, mugshot staleness stamp) resolving records three different ways, with three
/// of the five applying a race filter the other two did not. Nothing was broken, but a rule written
/// out five times is four chances to miss one. These tests pin the extracted walk so the callers can
/// keep differing only where they mean to.
///
/// Pure: in-memory records, injected resolver and predicates, no environment.
/// </summary>
public class WigDetectorWnamWalkTests
{
    private static ArmorAddon NewArma(SkyrimMod mod, string editorId)
    {
        var arma = mod.ArmorAddons.AddNew();
        arma.EditorID = editorId;
        arma.BodyTemplate = new BodyTemplate { FirstPersonFlags = WigDetector.HairSlots };
        return arma;
    }

    private static Armor NewSkin(SkyrimMod mod, params IFormLinkGetter<IArmorAddonGetter>[] armatures)
    {
        var skin = mod.Armors.AddNew();
        skin.EditorID = "TheSkin";
        foreach (var link in armatures) skin.Armature.Add(link);
        return skin;
    }

    /// <summary>Resolves against the mod the records live in; unknown keys resolve to null.</summary>
    private static Func<IFormLinkGetter<IArmorAddonGetter>, IArmorAddonGetter?> ResolverFor(SkyrimMod mod) =>
        link => mod.ArmorAddons.FirstOrDefault(a => a.FormKey == link.FormKey);

    private static List<string> Walk(
        IArmorGetter? skin,
        SkyrimMod mod,
        Func<IArmorAddonGetter, bool> isWig,
        Func<IArmorAddonGetter, bool>? extraFilter = null) =>
        WigDetector.EffectiveWnamWigArmatures(skin, ResolverFor(mod), isWig, extraFilter)
            .Select(a => a.EditorID ?? "(null)")
            .ToList();

    [Fact]
    public void YieldsOnlyTheArmaturesThePredicateAccepts()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var wig = NewArma(mod, "TheWig");
        var body = NewArma(mod, "TheBody");
        var skin = NewSkin(mod, wig.ToLink(), body.ToLink());

        Walk(skin, mod, a => a.EditorID == "TheWig").Should().Equal("TheWig");
    }

    [Fact]
    public void PreservesArmatureOrder()
    {
        // Callers that take [0] of the result are choosing "the first armature in the skin", so the
        // order has to be the record's, not the resolver's or a hash set's.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var second = NewArma(mod, "Second");
        var first = NewArma(mod, "First");
        var skin = NewSkin(mod, first.ToLink(), second.ToLink());

        Walk(skin, mod, _ => true).Should().Equal("First", "Second");
    }

    [Fact]
    public void SkipsArmaturesThatResolveNowhere()
    {
        // A dangling armature link cannot be forwarded, converted or rendered, so no caller should
        // count it. The output validator used to, and disagreed with the converter it mirrors.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var elsewhere = MutagenFixtures.NewMod("Elsewhere.esp");
        var reachable = NewArma(mod, "Reachable");
        var unreachable = NewArma(elsewhere, "Unreachable");
        var skin = NewSkin(mod, reachable.ToLink(), unreachable.ToLink());

        // Predicate says yes to everything; only the resolvable one comes back.
        Walk(skin, mod, _ => true).Should().Equal("Reachable");
    }

    [Fact]
    public void ExtraFilterRunsBeforeTheWigTest()
    {
        // The callers' extra filters are race and biped-slot narrowings. If the wig test ran first a
        // manual designation could resurrect an armature the NPC's race is not served by.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var wrongRace = NewArma(mod, "WrongRace");
        var skin = NewSkin(mod, wrongRace.ToLink());

        var wigTestSaw = new List<string>();
        var result = WigDetector.EffectiveWnamWigArmatures(
            skin,
            ResolverFor(mod),
            a => { wigTestSaw.Add(a.EditorID!); return true; },
            _ => false).ToList();

        result.Should().BeEmpty();
        wigTestSaw.Should().BeEmpty("an armature the extra filter rejected must never reach the wig test");
    }

    [Fact]
    public void DoesNotDeduplicate()
    {
        // Load-bearing. ComputeWigHideHeadShapeNames and ApplyWnamConversion both act only when
        // there is EXACTLY ONE effective wig ARMA; silently collapsing a doubled armature entry
        // would turn a declined conversion into an applied one.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var wig = NewArma(mod, "TheWig");
        var skin = NewSkin(mod, wig.ToLink(), wig.ToLink());

        Walk(skin, mod, _ => true).Should().Equal("TheWig", "TheWig");
    }

    [Fact]
    public void NullSkinOrEmptyArmatureYieldsNothing()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var empty = NewSkin(mod);

        Walk(null, mod, _ => true).Should().BeEmpty();
        Walk(empty, mod, _ => true).Should().BeEmpty();
    }

    [Fact]
    public void SkipsNullArmatureLinks()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var wig = NewArma(mod, "TheWig");
        var skin = mod.Armors.AddNew();
        skin.Armature.Add(FormKey.Null.ToLink<IArmorAddonGetter>());
        skin.Armature.Add(wig.ToLink());

        Walk(skin, mod, _ => true).Should().Equal("TheWig");
    }

    [Fact]
    public void IsLazyEnoughToStopAtTheFirstHit()
    {
        // The output validator only asks "is there one?" (.Any()). Deferred execution keeps that
        // from resolving every armature in a full body skin just to answer yes.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var first = NewArma(mod, "First");
        var second = NewArma(mod, "Second");
        var skin = NewSkin(mod, first.ToLink(), second.ToLink());

        var resolved = new List<string>();
        var any = WigDetector.EffectiveWnamWigArmatures(
            skin,
            link =>
            {
                var arma = mod.ArmorAddons.FirstOrDefault(a => a.FormKey == link.FormKey);
                if (arma?.EditorID != null) resolved.Add(arma.EditorID);
                return arma;
            },
            _ => true).Any();

        any.Should().BeTrue();
        resolved.Should().Equal("First");
    }
}
