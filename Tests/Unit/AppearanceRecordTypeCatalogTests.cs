using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="AppearanceRecordTypeCatalog"/>: the definition of "appearance record type" behind the
/// per-mod "Appearance records only" option.
///
/// <para><b>Why the completeness test is the important one.</b> The set is one hop of reflection
/// from the ticked Override Roots, extended along a DECLARED table of links between record types.
/// A declared table is only safe if it is exhaustive about what it leaves out: a link nobody
/// classified is a link that is silently not followed, and the records behind it silently stop
/// being forwarded for every mod with the box ticked. So each FormLink-bearing member of every
/// type in the table must be listed as followed or as not followed, and that is asserted against
/// Mutagen's own record classes by reflection: upgrade Mutagen into a new link, or add a type to
/// the table, and this fails until the member is classified
/// (the <see cref="NpcRootFieldCatalogTests"/> pattern).</para>
///
/// <para>The member enumeration here is written separately from the catalog's own reflection walk
/// on purpose, and the two are compared, so one walk's blind spot cannot hide a member from both.</para>
/// </summary>
public class AppearanceRecordTypeCatalogTests
{
    private readonly ITestOutputHelper _output;
    public AppearanceRecordTypeCatalogTests(ITestOutputHelper output) => _output = output;

    private static readonly Assembly Skyrim = typeof(Npc).Assembly;
    private static readonly Type[] SkyrimTypes = Skyrim.GetTypes();

    // ── This file's own "does this member bear a link" walk ──────────────────────────────────

    private static bool BearsLinks(Type type, HashSet<Type> seen)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        // The NON-generic base interface, where the catalog looks for IFormLinkGetter<T>.
        if (typeof(IFormLinkGetter).IsAssignableFrom(type)) return true;
        if (type.IsArray) return BearsLinks(type.GetElementType()!, seen);
        if (type.IsGenericType && type.GetGenericArguments().Any(a => BearsLinks(a, seen))) return true;
        if (type.Assembly != Skyrim || type.IsEnum || type.IsPrimitive) return false;
        if (typeof(IMajorRecordGetter).IsAssignableFrom(type)) return false; // held by value: not a link
        if (!seen.Add(type)) return false;

        // The type itself and everything that can stand in for it, whether or not it is abstract:
        // ScriptProperty is concrete, and the link is on its subclass ScriptObjectProperty.
        var shapes = SkyrimTypes
            .Where(c => c.IsClass && type.IsAssignableFrom(c) && !typeof(IMajorRecordGetter).IsAssignableFrom(c))
            .Append(type)
            .Distinct();

        return shapes.Any(shape => InstanceProperties(shape).Any(p => BearsLinks(p.PropertyType, seen)));
    }

    private static IEnumerable<PropertyInfo> InstanceProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0);

    private static List<string> LinkBearingMembers(Type recordClass) =>
        InstanceProperties(recordClass)
            .Where(p => BearsLinks(p.PropertyType, new HashSet<Type>()))
            .Select(p => p.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static string Names(IEnumerable<Type> getterTypes)
    {
        var names = getterTypes.Select(Short).OrderBy(n => n, StringComparer.Ordinal).ToList();
        return names.Count == AppearanceRecordTypeCatalog.AllRecordTypes.Count
            ? $"any of the {names.Count} record types"
            : string.Join(", ", names);
    }

    /// <summary>IArmorAddonGetter to "ArmorAddon".</summary>
    private static string Short(Type getterType) =>
        getterType.Name.StartsWith("I", StringComparison.Ordinal) && getterType.Name.EndsWith("Getter", StringComparison.Ordinal)
            ? getterType.Name.Substring(1, getterType.Name.Length - "IGetter".Length)
            : getterType.Name;

    private static IEnumerable<AppearanceRecordTypeCatalog.TypeEntry> DeclaredRows =>
        AppearanceRecordTypeCatalog.All.Where(e => !e.MembersComeFromNpcRootFieldCatalog);

    // ── Completeness ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryLinkBearingMember_OfEveryTypeInTheTable_IsFollowedOrDeliberatelyNot()
    {
        var unclassified = new List<string>();
        foreach (var row in DeclaredRows)
        {
            var declared = row.Members.Select(m => m.Name).ToHashSet();
            foreach (var member in LinkBearingMembers(row.ClassType).Where(m => !declared.Contains(m)))
            {
                unclassified.Add($"{row.ClassType.Name}.{member} -> " +
                                 Names(AppearanceRecordTypeCatalog.LinkTargetsOfMember(row.ClassType, member)));
            }
        }

        unclassified.Should().BeEmpty(
            "every FormLink-bearing member of a type in the table must be listed as an appearance " +
            "link (followed) or as deliberately not followed; an unlisted one is silently not " +
            "followed, and the records behind it silently stop being forwarded");
    }

    [Fact]
    public void EveryDeclaredMember_IsARealLinkBearingMember()
    {
        var stale = new List<string>();
        foreach (var row in DeclaredRows)
        {
            var real = LinkBearingMembers(row.ClassType).ToHashSet();
            stale.AddRange(row.Members.Where(m => !real.Contains(m.Name))
                .Select(m => $"{row.ClassType.Name}.{m.Name}"));
            row.Members.Select(m => m.Name).Should().OnlyHaveUniqueItems(
                "{0} must classify each member once", row.ClassType.Name);
        }

        stale.Should().BeEmpty("a row naming a member that does not exist, or bears no link, classifies nothing");
    }

    [Fact]
    public void TheTwoReflectionWalks_AgreeOnWhichMembersBearLinks()
    {
        // The catalog's walk feeds step 1 (roots to record types) and the checks below; this
        // file's walk decides what must be classified. A member only one of them sees is a bug
        // in one of them.
        var disagreements = new List<string>();
        foreach (var row in AppearanceRecordTypeCatalog.All)
        {
            var mine = LinkBearingMembers(row.ClassType).ToHashSet();
            foreach (var p in InstanceProperties(row.ClassType).Select(p => p.Name).Distinct())
            {
                bool catalogSees = AppearanceRecordTypeCatalog.LinkTargetsOfMember(row.ClassType, p).Count > 0;
                if (catalogSees != mine.Contains(p)) disagreements.Add($"{row.ClassType.Name}.{p}");
            }
        }

        disagreements.Should().BeEmpty();
    }

    [Fact]
    public void EveryFollowedTarget_IsReachableThroughItsMember()
    {
        var wrong = new List<string>();
        foreach (var row in DeclaredRows)
        {
            foreach (var member in row.Members.Where(m => m.IsFollowed))
            {
                var reachable = AppearanceRecordTypeCatalog.LinkTargetsOfMember(row.ClassType, member.Name);
                wrong.AddRange(member.Follows.Where(t => !reachable.Contains(t))
                    .Select(t => $"{row.ClassType.Name}.{member.Name} cannot point at {Short(t)}"));
            }
        }

        wrong.Should().BeEmpty("a declared link must be one the record format really has");
    }

    [Fact]
    public void EveryMemberNotFollowed_SaysWhy()
    {
        DeclaredRows.SelectMany(r => r.Members.Where(m => !m.IsFollowed && string.IsNullOrWhiteSpace(m.Reason))
                .Select(m => $"{r.ClassType.Name}.{m.Name}"))
            .Should().BeEmpty();
    }

    [Fact]
    public void EveryTypeTheTableLeadsTo_HasARow_SoItsMembersAreClassifiedToo()
    {
        var rows = AppearanceRecordTypeCatalog.All.Select(e => e.GetterType).ToHashSet();

        DeclaredRows.SelectMany(r => r.Members.SelectMany(m => m.Follows)).Distinct()
            .Where(t => !rows.Contains(t)).Select(Short)
            .Should().BeEmpty("a followed type with no row would be terminal by accident, not by decision");

        // ...and so does everything the default roots point at directly.
        NpcRootFieldCatalog.Defaults.SelectMany(f => AppearanceRecordTypeCatalog.RootFieldTargets(f)).Distinct()
            .Where(t => !rows.Contains(t)).Select(Short)
            .Should().BeEmpty("every type the default roots reach in one hop needs its links classified");
    }

    [Fact]
    public void NpcLinkMembers_AreTheOverrideRootsFields()
    {
        // Npc has no members of its own in the table: its links are the Override Roots fields,
        // and the ones followed are the ones the user ticked. That delegation only holds if every
        // link-bearing Npc member really is a root field.
        var rootFields = Enum.GetNames<NpcRootField>().ToHashSet();

        LinkBearingMembers(typeof(Npc)).Where(m => !rootFields.Contains(m))
            .Should().BeEmpty("an Npc link that is not an Override Roots field would be classified by nobody");
    }

    [Fact]
    public void EveryRow_PairsARecordClassWithTheGetterItsRecordsReport()
    {
        // The filter asks a record for record.Registration.GetterType; the table and the
        // reflection name types by the same interface. Checked on a real instance of each type.
        var known = AppearanceRecordTypeCatalog.AllRecordTypes.ToDictionary(x => x.ClassType, x => x.GetterType);
        var key = new FormKey(ModKey.FromFileName("Test.esp"), 0x800);

        foreach (var row in AppearanceRecordTypeCatalog.All)
        {
            known.Should().ContainKey(row.ClassType);
            known[row.ClassType].Should().Be(row.GetterType);
        }

        var mismatches = new List<string>();
        int checkedCount = 0;
        foreach (var (classType, getterType) in AppearanceRecordTypeCatalog.AllRecordTypes)
        {
            var ctor = classType.GetConstructor(new[] { typeof(FormKey), typeof(SkyrimRelease) });
            if (ctor == null) continue;
            var record = (IMajorRecordGetter)ctor.Invoke(new object[] { key, SkyrimRelease.SkyrimSE });
            checkedCount++;
            if (record.Registration.GetterType != getterType)
            {
                mismatches.Add($"{classType.Name}: reports {record.Registration.GetterType.Name}, mapped to {getterType.Name}");
            }
        }

        mismatches.Should().BeEmpty();
        checkedCount.Should().BeGreaterThan(100, "nearly every record class has the (FormKey, release) constructor");
        foreach (var row in AppearanceRecordTypeCatalog.All)
        {
            row.ClassType.GetConstructor(new[] { typeof(FormKey), typeof(SkyrimRelease) })
                .Should().NotBeNull("{0} must be among the types checked on an instance", row.ClassType.Name);
        }
    }

    // ── Step 1: the ticked roots, one hop ───────────────────────────────────────────────────

    [Theory]
    [InlineData(NpcRootField.Template, "LeveledNpc, Npc")]
    [InlineData(NpcRootField.Race, "Race")]
    [InlineData(NpcRootField.WornArmor, "Armor")]
    [InlineData(NpcRootField.HeadTexture, "TextureSet")]
    [InlineData(NpcRootField.HairColor, "ColorRecord")]
    [InlineData(NpcRootField.HeadParts, "HeadPart")]
    [InlineData(NpcRootField.DefaultOutfit, "Outfit")]
    [InlineData(NpcRootField.SleepingOutfit, "Outfit")]
    [InlineData(NpcRootField.Packages, "Package")]
    public void RootFieldTargets_AreTheRecordTypesTheFieldPointsAt(NpcRootField field, string expected) =>
        Names(AppearanceRecordTypeCatalog.RootFieldTargets(field)).Should().Be(expected);

    [Fact]
    public void EveryRootField_PointsAtSomeRecordType()
    {
        Enum.GetValues<NpcRootField>()
            .Where(f => AppearanceRecordTypeCatalog.RootFieldTargets(f).Count == 0)
            .Should().BeEmpty("a root field the reflection cannot read would contribute no type when ticked");
    }

    // ── The resolved set ────────────────────────────────────────────────────────────────────

    private static readonly Type[] DefaultSet =
    {
        typeof(INpcGetter), typeof(ILeveledNpcGetter), typeof(IRaceGetter), typeof(IArmorGetter),
        typeof(ITextureSetGetter), typeof(IColorRecordGetter), typeof(IHeadPartGetter), typeof(IOutfitGetter),
        // through the declared table:
        typeof(IArmorAddonGetter), typeof(ILeveledItemGetter), typeof(IFormListGetter),
    };

    [Fact]
    public void Resolve_DefaultRoots_IsTheAppearanceSet()
    {
        var resolved = AppearanceRecordTypeCatalog.Resolve(NpcRootFieldCatalog.Defaults);
        _output.WriteLine("default roots: " + Names(resolved));

        resolved.Should().BeEquivalentTo(DefaultSet);
    }

    [Fact]
    public void Resolve_WithoutTheOutfitRoots_DropsOutfitAndWhatOnlyOutfitsReach()
    {
        var roots = NpcRootFieldCatalog.Defaults
            .Where(f => f is not (NpcRootField.DefaultOutfit or NpcRootField.SleepingOutfit))
            .ToHashSet();

        var resolved = AppearanceRecordTypeCatalog.Resolve(roots);
        _output.WriteLine("without DOFT and SOFT: " + Names(resolved));

        resolved.Should().BeEquivalentTo(
            DefaultSet.Except(new[] { typeof(IOutfitGetter), typeof(ILeveledItemGetter) }),
            "the set follows the roots that are ticked; a levelled item list is only reached through an outfit");
        resolved.Should().Contain(typeof(IArmorGetter), "armor is still reached through the skin");
    }

    [Fact]
    public void Resolve_NoRoots_IsEmpty() =>
        AppearanceRecordTypeCatalog.Resolve(new HashSet<NpcRootField>()).Should().BeEmpty();

    [Fact]
    public void Resolve_ANonAppearanceRoot_AddsWhatItPointsAt_AsATerminalType()
    {
        var resolved = AppearanceRecordTypeCatalog.Resolve(new HashSet<NpcRootField> { NpcRootField.Packages });

        resolved.Should().BeEquivalentTo(new[] { typeof(IPackageGetter) },
            "the set follows what is ticked, and a type with no row is not followed any further");
    }

    [Fact]
    public void Resolve_IsCachedPerSetOfRoots()
    {
        var a = AppearanceRecordTypeCatalog.Resolve(
            new HashSet<NpcRootField> { NpcRootField.Race, NpcRootField.WornArmor });
        var b = AppearanceRecordTypeCatalog.Resolve(
            new HashSet<NpcRootField> { NpcRootField.WornArmor, NpcRootField.Race });
        var c = AppearanceRecordTypeCatalog.Resolve(new HashSet<NpcRootField> { NpcRootField.Race });

        b.Should().BeSameAs(a, "the same roots in another order are the same set, computed once");
        c.Should().NotBeSameAs(a);
    }

    [Fact]
    public void Resolve_ForAMod_UsesTheRootsInForceForIt()
    {
        var settings = new Settings
        {
            DefaultOverrideTraversalRoots = new HashSet<NpcRootField> { NpcRootField.HairColor },
        };
        var mod = new ModSetting { OverrideTraversalRoots = new HashSet<NpcRootField> { NpcRootField.HeadTexture } };

        AppearanceRecordTypeCatalog.Resolve(new ModSetting(), settings)
            .Should().BeEquivalentTo(new[] { typeof(IColorRecordGetter) });
        AppearanceRecordTypeCatalog.Resolve(mod, settings)
            .Should().BeEquivalentTo(new[] { typeof(ITextureSetGetter) });
        AppearanceRecordTypeCatalog.Resolve(new ModSetting(), new Settings())
            .Should().BeEquivalentTo(DefaultSet);
    }

    [Fact]
    public void Contains_GoesByTheRecordsType()
    {
        var mod = new SkyrimMod(ModKey.FromFileName("Test.esp"), SkyrimRelease.SkyrimSE);
        var set = AppearanceRecordTypeCatalog.Resolve(NpcRootFieldCatalog.Defaults);

        AppearanceRecordTypeCatalog.Contains(set, mod.ArmorAddons.AddNew()).Should().BeTrue();
        AppearanceRecordTypeCatalog.Contains(set, mod.TextureSets.AddNew()).Should().BeTrue();
        AppearanceRecordTypeCatalog.Contains(set, mod.Spells.AddNew()).Should().BeFalse();
        AppearanceRecordTypeCatalog.Contains(set, mod.Keywords.AddNew()).Should().BeFalse();
        AppearanceRecordTypeCatalog.Contains(set, mod.Factions.AddNew()).Should().BeFalse();
    }

    // ── The table, printed (for review; asserts nothing) ────────────────────────────────────

    [Fact]
    public void PrintTheTable()
    {
        var sb = new StringBuilder();
        foreach (var row in AppearanceRecordTypeCatalog.All)
        {
            sb.AppendLine(row.ClassType.Name +
                          (row.MembersComeFromNpcRootFieldCatalog ? "  (members: the Override Roots fields)" : ""));
            foreach (var m in row.Members.Where(m => m.IsFollowed))
            {
                sb.AppendLine($"  FOLLOWED      {m.Name} -> {Names(m.Follows)}" +
                              (m.Reason == null ? "" : $"   [{m.Reason}]"));
            }
            foreach (var m in row.Members.Where(m => !m.IsFollowed))
            {
                sb.AppendLine($"  not followed  {m.Name} ({Names(AppearanceRecordTypeCatalog.LinkTargetsOfMember(row.ClassType, m.Name))})" +
                              $"   [{m.Reason}]");
            }
            if (!row.MembersComeFromNpcRootFieldCatalog)
            {
                var declared = row.Members.Select(m => m.Name).ToHashSet();
                foreach (var member in LinkBearingMembers(row.ClassType).Where(m => !declared.Contains(m)))
                {
                    sb.AppendLine($"  UNCLASSIFIED  {member} ({Names(AppearanceRecordTypeCatalog.LinkTargetsOfMember(row.ClassType, member))})");
                }
            }
        }
        sb.AppendLine("default roots resolve to: " +
                      Names(AppearanceRecordTypeCatalog.Resolve(NpcRootFieldCatalog.Defaults)));
        _output.WriteLine(sb.ToString());
    }
}
