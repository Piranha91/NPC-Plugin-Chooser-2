using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The two override walks of <see cref="RecordHandler"/> under the per-mod "Appearance records
/// only" option: the walk visits what it always visited and only what it COLLECTS narrows.
///
/// <para>Under Include As New a collected record needs every record between it and the NPC copied
/// too (a bridge), or nothing points at the copy. When one of those records is an override the
/// option left out, the bridge is a copy of the LOAD ORDER's version of it, as for any record the
/// mod does not carry, and never the mod's version: the mod's edit to that record is exactly what
/// the option leaves out.</para>
///
/// <para>The type set is passed in directly, so these tests choose a small one (Armor alone) and
/// do not depend on what the catalog resolves to. In-memory Mutagen mods and link caches seeded
/// via Reflect, as in <see cref="RecordHandlerBridgeParentTests"/>; no game install, no disk.</para>
/// </summary>
public class RecordHandlerAppearanceOnlyWalkTests
{
    private static readonly ModKey BaseKey = ModKey.FromFileName("Base.esm");
    private static readonly ModKey ModKeyEsp = ModKey.FromFileName("Appearance.esp");
    private static readonly ModKey OutputKey = ModKey.FromFileName("NPC.esp");

    /// <summary>Armor is the only "appearance" type in these tests; Outfit and FormList are not.</summary>
    private static readonly IReadOnlySet<Type> ArmorOnly = new HashSet<Type> { typeof(IArmorGetter) };

    private sealed class Fixture
    {
        public required RecordHandler Handler { get; init; }
        public required SkyrimMod BaseMod { get; init; }
        public required SkyrimMod AppearanceMod { get; init; }
        public required SkyrimMod OutputMod { get; init; }
    }

    private static Fixture Build()
    {
        var baseMod = new SkyrimMod(BaseKey, SkyrimRelease.SkyrimSE);
        var appearanceMod = new SkyrimMod(ModKeyEsp, SkyrimRelease.SkyrimSE);
        var outputMod = new SkyrimMod(OutputKey, SkyrimRelease.SkyrimSE);

        var env = Reflect.Uninitialized<EnvironmentStateProvider>();
        Reflect.SetField(env, "OutputMod", outputMod);

        var handler = new RecordHandler(env, null!, new Settings());

        // Seeding both caches keeps the parent-record lookup off the LoadOrder (and therefore off
        // the uninitialised env): it only consults LoadOrder when the ModKey is absent here.
        var caches = Reflect.GetField<
            System.Collections.Concurrent.ConcurrentDictionary<
                ModKey, ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>>>(handler, "_modLinkCaches");
        caches[BaseKey] = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(
            baseMod, new LinkCachePreferences());
        caches[ModKeyEsp] = new ImmutableModLinkCache<ISkyrimMod, ISkyrimModGetter>(
            appearanceMod, new LinkCachePreferences());

        return new Fixture
        {
            Handler = handler, BaseMod = baseMod,
            AppearanceMod = appearanceMod, OutputMod = outputMod,
        };
    }

    /// <summary>Walks one root link the way DuplicateInOverrideRecordsFromLinks does (Include As New).</summary>
    private static bool Duplicate(Fixture fx, IFormLinkGetter root, IReadOnlySet<Type>? appearanceTypes, int maxDepth = 10)
    {
        object?[] args =
        {
            root, new List<ModKey> { ModKeyEsp }, fx.OutputMod,
            new Dictionary<FormKey, FormKey>(), new HashSet<IMajorRecord>(),
            maxDepth, 0, new List<string>(), new HashSet<FormKey>(), null, CancellationToken.None,
            appearanceTypes,
        };
        var method = typeof(RecordHandler).GetMethod("TraverseAndDuplicateInOverrideRecordsCore",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (bool)method.Invoke(fx.Handler, args)!;
    }

    /// <summary>Walks one root link the way DeepGetOverriddenDependencyRecords does (Include).</summary>
    private static List<IMajorRecordGetter> Collect(Fixture fx, IFormLinkGetter root, IReadOnlySet<Type>? appearanceTypes, int maxDepth = 10)
    {
        var collected = new HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>>();
        object?[] args =
        {
            root, new List<ModKey> { ModKeyEsp }, collected,
            maxDepth, 0, new HashSet<FormKey>(), null, CancellationToken.None,
            appearanceTypes,
        };
        typeof(RecordHandler).GetMethod("CollectOverriddenDependencyRecordsCore",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(fx.Handler, args);
        return collected.Select(c => c.Record).ToList();
    }

    /// <summary>Base.esm: FormList -> Outfit -> Armor. The mod overrides the Outfit (renaming it,
    /// so its version can be told from the load order's) and the Armor.</summary>
    private static (FormList List, Outfit Outfit, Armor Armor) OutfitAboveArmor(Fixture fx)
    {
        var armor = fx.BaseMod.Armors.AddNew();
        armor.EditorID = "BaseArmor";
        var outfit = fx.BaseMod.Outfits.AddNew();
        outfit.EditorID = "BaseOutfit";
        outfit.Items ??= new(); // null by default on a fresh Outfit
        outfit.Items.Add(armor);
        var formList = fx.BaseMod.FormLists.AddNew();
        formList.EditorID = "BaseList";
        formList.Items.Add(outfit);

        fx.AppearanceMod.Armors.GetOrAddAsOverride(armor).EditorID = "ModArmor";
        fx.AppearanceMod.Outfits.GetOrAddAsOverride(outfit).EditorID = "ModOutfit";
        return (formList, outfit, armor);
    }

    // ── Include As New ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void LeftOutOverride_IsWalkedThrough_AndBridgedWithTheLoadOrdersVersion()
    {
        var fx = Build();
        var (formList, _, _) = OutfitAboveArmor(fx);

        var mergeParent = Duplicate(fx, formList.ToLink(), ArmorOnly);

        fx.OutputMod.Armors.Should().ContainSingle("the Armor override is below the Outfit and must still be found")
            .Which.EditorID.Should().StartWith("ModArmor", "an appearance override is copied in the mod's version");
        fx.OutputMod.Outfits.Should().ContainSingle("the Outfit bridges the way to the Armor copy")
            .Which.EditorID.Should().StartWith("BaseOutfit",
                "the option left the mod's Outfit out, so the bridge is the load order's Outfit, not the mod's");
        fx.OutputMod.FormLists.Should().HaveCount(1, "and so does everything above it");
        mergeParent.Should().BeTrue();
    }

    [Fact]
    public void OptionOff_TheSameWalk_CopiesTheModsVersionOfEveryOverride()
    {
        var fx = Build();
        var (formList, _, _) = OutfitAboveArmor(fx);

        var mergeParent = Duplicate(fx, formList.ToLink(), appearanceTypes: null);

        fx.OutputMod.Armors.Should().ContainSingle().Which.EditorID.Should().StartWith("ModArmor");
        fx.OutputMod.Outfits.Should().ContainSingle().Which.EditorID.Should().StartWith("ModOutfit",
            "with the option off every override is copied in the mod's version, as before the option existed");
        fx.OutputMod.FormLists.Should().HaveCount(1);
        mergeParent.Should().BeTrue();
    }

    [Fact]
    public void LeftOutOverride_WithNothingCollectedBelowIt_MintsNothing()
    {
        var fx = Build();
        var armor = fx.BaseMod.Armors.AddNew();
        var outfit = fx.BaseMod.Outfits.AddNew();
        outfit.Items ??= new();
        outfit.Items.Add(armor);
        fx.AppearanceMod.Outfits.GetOrAddAsOverride(outfit).EditorID = "ModOutfit";

        var mergeParent = Duplicate(fx, outfit.ToLink(), ArmorOnly);

        fx.OutputMod.Outfits.Should().BeEmpty("an Outfit is not in the set, and nothing below it was copied");
        fx.OutputMod.Armors.Should().BeEmpty();
        mergeParent.Should().BeFalse();
    }

    [Fact]
    public void LeftOutOverride_WhoseLoadOrderVersionDoesNotPointAtTheCopy_IsNotBridged()
    {
        var fx = Build();

        // The load order's Outfit holds only armorA. The MOD's Outfit also holds armorB, and the
        // mod overrides armorB: the copy of armorB is reached only through the mod's edit to the
        // Outfit, which the option leaves out. A copy of the load order's Outfit would point at
        // nothing that was copied, so none is made, and nothing above it is bridged either.
        var armorA = fx.BaseMod.Armors.AddNew();
        var armorB = fx.BaseMod.Armors.AddNew();
        var outfit = fx.BaseMod.Outfits.AddNew();
        outfit.Items ??= new();
        outfit.Items.Add(armorA);
        var formList = fx.BaseMod.FormLists.AddNew();
        formList.Items.Add(outfit);

        fx.AppearanceMod.Armors.GetOrAddAsOverride(armorB);
        var modOutfit = fx.AppearanceMod.Outfits.GetOrAddAsOverride(outfit);
        modOutfit.Items!.Add(armorB);

        var mergeParent = Duplicate(fx, formList.ToLink(), ArmorOnly);

        fx.OutputMod.Armors.Should().HaveCount(1, "the walk went through the mod's Outfit and found the Armor override");
        fx.OutputMod.Outfits.Should().BeEmpty("the load order's Outfit does not point at the copied Armor");
        fx.OutputMod.FormLists.Should().BeEmpty("with no bridge below it, nothing above is bridged");
        mergeParent.Should().BeFalse();
    }

    [Fact]
    public void LeftOutOverride_StillResetsTheDepthCounter()
    {
        var fx = Build();
        var (_, outfit, _) = OutfitAboveArmor(fx);

        // Depth limit 1: the Outfit is the first hop. Finding the mod's version of it resets the
        // counter, with or without the option, so the Armor (one more hop) is still in reach.
        Duplicate(fx, outfit.ToLink(), ArmorOnly, maxDepth: 1);

        fx.OutputMod.Armors.Should().HaveCount(1,
            "a record of the mod resets the search depth whether or not its type is collected");
    }

    // ── Include ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Collect_OptionOn_ReturnsOnlyOverridesOfTheGivenTypes_ButWalksThroughTheOthers()
    {
        var fx = Build();
        var (formList, outfit, armor) = OutfitAboveArmor(fx);

        var collected = Collect(fx, formList.ToLink(), ArmorOnly);

        collected.Select(r => r.FormKey).Should().Equal(new[] { armor.FormKey },
            "the Outfit override is walked through and not collected; the Armor below it is collected");
    }

    [Fact]
    public void Collect_OptionOff_ReturnsEveryOverride()
    {
        var fx = Build();
        var (formList, outfit, armor) = OutfitAboveArmor(fx);

        var collected = Collect(fx, formList.ToLink(), appearanceTypes: null);

        collected.Select(r => r.FormKey).Should().BeEquivalentTo(new[] { outfit.FormKey, armor.FormKey });
    }

    [Fact]
    public void Collect_LeftOutOverride_StillResetsTheDepthCounter()
    {
        var fx = Build();
        var (_, outfit, armor) = OutfitAboveArmor(fx);

        var collected = Collect(fx, outfit.ToLink(), ArmorOnly, maxDepth: 1);

        collected.Select(r => r.FormKey).Should().Equal(new[] { armor.FormKey });
    }
}
