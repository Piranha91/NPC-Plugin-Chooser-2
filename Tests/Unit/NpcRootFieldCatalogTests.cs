using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="NpcRootFieldCatalog"/> — the table behind the per-mod "Override Roots" dialog and the
/// patcher's override-discovery roots.
///
/// <para><b>Why the completeness test is the important one.</b> Restricting discovery to a curated
/// set of NPC fields is only safe if the set is exhaustive: a field nobody listed is a field whose
/// records silently stop being merged, which surfaces months later as "this mod stopped working".
/// The app's previous hardcoded appearance list had already needed three ad-hoc additions
/// (always-on outfit for discovery, sleeping outfit injected as an extra root, template) — each
/// found the hard way. So completeness is asserted against Mutagen's own record definition by
/// reflection rather than trusted to review: add a Skyrim record field, or upgrade Mutagen into one,
/// and this fails until the catalog and the dialog learn about it.</para>
/// </summary>
public class NpcRootFieldCatalogTests
{
    // ── Completeness ────────────────────────────────────────────────────────────

    /// <summary>Whether a type can produce a FormLink: either it IS one, or it is a record/struct
    /// (or a collection of them) that transitively contains one. Bounded and cycle-guarded.</summary>
    private static bool CanBearLinks(Type type, HashSet<Type> visited, int depth = 0)
    {
        if (depth > 6 || !visited.Add(type)) return false;
        if (typeof(IFormLinkGetter).IsAssignableFrom(type)) return true;

        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var element = type.IsGenericType ? type.GetGenericArguments().FirstOrDefault() : null;
            if (element != null && CanBearLinks(element, visited, depth + 1)) return true;
        }

        // Only Mutagen's own record/sub-record types are worth descending into.
        if (type.Namespace?.StartsWith("Mutagen.Bethesda", StringComparison.Ordinal) != true) return false;
        if (type.IsPrimitive || type.IsEnum) return false;

        return type.GetProperties().Any(p => CanBearLinks(p.PropertyType, visited, depth + 1));
    }

    private static IEnumerable<PropertyInfo> LinkBearingNpcProperties() =>
        typeof(INpcGetter).GetProperties()
            .Concat(typeof(INpcGetter).GetInterfaces().SelectMany(i => i.GetProperties()))
            .DistinctBy(p => p.Name)
            // Identity/bookkeeping members every record carries; not fields of the NPC form.
            .Where(p => p.Name is not ("FormKey" or "FormVersion" or "Version2" or "VersionControl"))
            .Where(p => CanBearLinks(p.PropertyType, new HashSet<Type>()));

    [Fact]
    public void EveryLinkBearingNpcField_HasACatalogEntry()
    {
        var covered = NpcRootFieldCatalog.All.Select(e => e.Field.ToString()).ToHashSet();
        var missing = LinkBearingNpcProperties().Select(p => p.Name)
            .Where(name => !covered.Contains(name))
            .OrderBy(n => n)
            .ToList();

        missing.Should().BeEmpty(
            "every FormLink-bearing NPC field must be offerable as a discovery root — an unlisted " +
            "one can never be traversed and its records silently stop merging");
    }

    [Fact]
    public void EveryCatalogEntry_NamesARealNpcField()
    {
        // The enum member name IS the Mutagen property name; that identity is what lets the test
        // above compare the two lists at all.
        var npcProperties = typeof(INpcGetter).GetProperties()
            .Concat(typeof(INpcGetter).GetInterfaces().SelectMany(i => i.GetProperties()))
            .Select(p => p.Name).ToHashSet();

        NpcRootFieldCatalog.All.Select(e => e.Field.ToString())
            .Where(name => !npcProperties.Contains(name))
            .Should().BeEmpty("a catalog entry naming no real field would never contribute a root");
    }

    [Fact]
    public void EveryEnumMember_AppearsExactlyOnce()
    {
        NpcRootFieldCatalog.All.Select(e => e.Field).Should()
            .BeEquivalentTo(Enum.GetValues<NpcRootField>())
            .And.OnlyHaveUniqueItems();
    }

    // ── The defaults ────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultsAreTheAppearanceSet()
    {
        NpcRootFieldCatalog.Defaults.Should().BeEquivalentTo(new[]
        {
            NpcRootField.Race, NpcRootField.WornArmor, NpcRootField.HeadTexture,
            NpcRootField.HairColor, NpcRootField.HeadParts, NpcRootField.DefaultOutfit,
            NpcRootField.SleepingOutfit, NpcRootField.Template,
        });
    }

    [Fact]
    public void PackagesAreNotADefaultRoot()
    {
        // The measured regression: rooting at PKID walks out through AI packages into placed
        // references, cells and quests, and anything genuinely overridden down there drags its
        // whole ancestry in as private copies — six NPCs had their package links repointed at
        // duplicates of vanilla packages referencing duplicates of DB01 and SolitudeOpening.
        NpcRootFieldCatalog.Defaults.Should().NotContain(NpcRootField.Packages);
    }

    // ── Display ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(NpcRootField.WornArmor, "WNAM - Worn Armor")]
    [InlineData(NpcRootField.HairColor, "HCLF - Hair Color")]
    [InlineData(NpcRootField.HeadTexture, "FTST - Head texture")]
    [InlineData(NpcRootField.Packages, "PKID - Package")]
    public void DisplayNameUsesXEditNaming(NpcRootField field, string expected) =>
        NpcRootFieldCatalog.Get(field).DisplayName.Should().Be(expected);

    [Fact]
    public void SignaturelessEntry_FallsBackToItsLabel() =>
        NpcRootFieldCatalog.Get(NpcRootField.Sound).DisplayName
            .Should().Be("Sound Types / Inherits Sounds From (CSDT, CSCR)");

    // ── Precedence ──────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_UnsetAtBothLevels_FallsBackToTheAppearanceSet()
    {
        // The no-migration path: a settings file written before this option existed has null at
        // both levels, and must land on the narrowed default rather than on "everything".
        NpcRootFieldCatalog.Resolve(new ModSetting(), new Settings())
            .Should().BeEquivalentTo(NpcRootFieldCatalog.Defaults);
    }

    [Fact]
    public void Resolve_GlobalDefaultWins_OverTheCatalog()
    {
        var settings = new Settings
        {
            DefaultOverrideTraversalRoots = new HashSet<NpcRootField> { NpcRootField.Race },
        };

        NpcRootFieldCatalog.Resolve(new ModSetting(), settings)
            .Should().BeEquivalentTo(new[] { NpcRootField.Race });
    }

    [Fact]
    public void Resolve_PerModWins_OverTheGlobalDefault()
    {
        var settings = new Settings
        {
            DefaultOverrideTraversalRoots = new HashSet<NpcRootField> { NpcRootField.Race },
        };
        var mod = new ModSetting
        {
            OverrideTraversalRoots = new HashSet<NpcRootField> { NpcRootField.Packages },
        };

        NpcRootFieldCatalog.Resolve(mod, settings)
            .Should().BeEquivalentTo(new[] { NpcRootField.Packages });
    }

    [Fact]
    public void Resolve_EmptyIsARealAnswer_NotUnset()
    {
        // Distinguishing empty from null is what lets a user switch discovery off for one mod;
        // treating empty as "unset" would silently re-enable the default set instead.
        var mod = new ModSetting { OverrideTraversalRoots = new HashSet<NpcRootField>() };

        NpcRootFieldCatalog.Resolve(mod, new Settings()).Should().BeEmpty();
    }

    // ── Root extraction ─────────────────────────────────────────────────────────

    [Fact]
    public void GetRootLinks_ReturnsOnlyCheckedFields()
    {
        var mod = new SkyrimMod(ModKey.FromFileName("Test.esp"), SkyrimRelease.SkyrimSE);
        var npc = mod.Npcs.AddNew();
        var race = mod.Races.AddNew();
        var outfit = mod.Outfits.AddNew();
        var package = mod.Packages.AddNew();
        npc.Race.SetTo(race);
        npc.DefaultOutfit.SetTo(outfit);
        npc.Packages.Add(package);

        var appearanceOnly = NpcRootFieldCatalog.GetRootLinks(npc, NpcRootFieldCatalog.Defaults)
            .Select(l => l.FormKey).ToList();

        appearanceOnly.Should().Contain(race.FormKey).And.Contain(outfit.FormKey);
        appearanceOnly.Should().NotContain(package.FormKey, "PKID is not a default root");

        NpcRootFieldCatalog.GetRootLinks(npc, NpcRootFieldCatalog.AllFields)
            .Select(l => l.FormKey).Should().Contain(package.FormKey, "ticking it opts back in");
    }

    [Fact]
    public void GetRootLinks_SkipsUnsetFields()
    {
        var mod = new SkyrimMod(ModKey.FromFileName("Test.esp"), SkyrimRelease.SkyrimSE);
        var npc = mod.Npcs.AddNew();

        NpcRootFieldCatalog.GetRootLinks(npc, NpcRootFieldCatalog.AllFields)
            .Should().BeEmpty("a blank NPC has no links to root at");
    }
}
