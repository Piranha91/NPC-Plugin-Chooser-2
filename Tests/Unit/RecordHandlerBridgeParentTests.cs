using System.Collections.Generic;
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
/// The BRIDGE-parent branch of <c>RecordHandler.TraverseAndDuplicateInOverrideRecords</c>: when
/// Include As New duplicates a record the mod overrides, every ancestor between the NPC's own field
/// and that record must be privately copied too, or nothing points at the copy and the mod's edit
/// silently stops applying (RS Children's ChildOutfit02 -> ChildClothes01 -> ArmorAddon chain).
///
/// <para><b>The bug this pins.</b> Some record types have no top-level group to duplicate into —
/// Cells live in CellBlock/CellSubBlock, placed references live inside Cells — so the bridge copy
/// fails. The failure used to be swallowed: the method still returned "merge my parent", so every
/// ancestor above the un-copyable node got bridged anyway, minting copies whose chain was already
/// severed. Measured on a real run: an NPC's AI package reached a Cell, the Cell's bridge failed,
/// and 24 Packages, 5 Quests, a DialogTopic and a Faction were minted above it as pure waste, each
/// failure also emitting a CRITICAL line. An undeliverable subtree must report itself as such.</para>
///
/// <para>In-memory Mutagen mods and link caches seeded via Reflect; no game install, no disk. The
/// private traversal is invoked directly so the test does not need a PluginProvider.</para>
/// </summary>
public class RecordHandlerBridgeParentTests
{
    private static readonly ModKey BaseKey = ModKey.FromFileName("Base.esm");
    private static readonly ModKey ModKeyEsp = ModKey.FromFileName("Appearance.esp");
    private static readonly ModKey OutputKey = ModKey.FromFileName("NPC.esp");

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

    /// <summary>Walks one root link the way DuplicateInOverrideRecordsFromLinks does.</summary>
    private static bool Traverse(Fixture fx, IFormLinkGetter root)
    {
        var exceptions = new List<string>();
        object?[] args =
        {
            root, new List<ModKey> { ModKeyEsp }, fx.OutputMod,
            new Dictionary<FormKey, FormKey>(), new HashSet<IMajorRecord>(),
            10, 0, exceptions, new HashSet<FormKey>(), null, CancellationToken.None,
        };
        var method = typeof(RecordHandler).GetMethod("TraverseAndDuplicateInOverrideRecords",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (bool)method.Invoke(fx.Handler, args)!;
    }

    /// <summary>
    /// Adds an interior Cell to <paramref name="mod"/>. Cells are the specimen precisely because
    /// they are not a top-level group, which is what makes the bridge copy fail.
    /// </summary>
    private static Cell AddCell(SkyrimMod mod)
    {
        var cell = new Cell(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        var subBlock = new CellSubBlock();
        subBlock.Cells.Add(cell);
        var block = new CellBlock();
        block.SubBlocks.Add(subBlock);
        mod.Cells.Records.Add(block);
        return cell;
    }

    [Fact]
    public void BridgeFailure_DoesNotDragTheAncestorIn()
    {
        var fx = Build();

        // Base.esm: FormList -> Cell -> Faction. Appearance.esp overrides only the Faction, so the
        // Cell and the FormList are both pure bridges on the way to it.
        var faction = fx.BaseMod.Factions.AddNew();
        var cell = AddCell(fx.BaseMod);
        cell.Owner.SetTo(faction);
        var formList = fx.BaseMod.FormLists.AddNew();
        formList.Items.Add(cell);

        fx.AppearanceMod.Factions.GetOrAddAsOverride(faction);

        var mergeParent = Traverse(fx, formList.ToLink());

        // The genuine override is delivered...
        fx.OutputMod.Factions.Should().HaveCount(1, "the mod really does override that Faction");
        // ...but nothing above the un-copyable Cell is minted, because nothing above it could ever
        // reach the copy.
        fx.OutputMod.FormLists.Should().BeEmpty("the Cell below it could not be bridged");
        mergeParent.Should().BeFalse("an undeliverable subtree must report itself upward");
    }

    [Fact]
    public void BridgeSucceeds_WhenTheWholeChainIsCopyable()
    {
        var fx = Build();

        // The case bridging exists for: FormList -> Outfit -> Armor, mod overrides only the Armor.
        // Every link is a top-level type, so the private chain is buildable and must be built.
        var armor = fx.BaseMod.Armors.AddNew();
        var outfit = fx.BaseMod.Outfits.AddNew();
        outfit.Items ??= new(); // null by default on a fresh Outfit
        outfit.Items.Add(armor);
        var formList = fx.BaseMod.FormLists.AddNew();
        formList.Items.Add(outfit);

        fx.AppearanceMod.Armors.GetOrAddAsOverride(armor);

        var mergeParent = Traverse(fx, formList.ToLink());

        fx.OutputMod.Armors.Should().HaveCount(1);
        fx.OutputMod.Outfits.Should().HaveCount(1, "the Outfit bridges the NPC's field to the copy");
        fx.OutputMod.FormLists.Should().HaveCount(1, "and so does everything above it");
        mergeParent.Should().BeTrue();
    }

    [Fact]
    public void NothingOverridden_MintsNothing()
    {
        var fx = Build();

        var armor = fx.BaseMod.Armors.AddNew();
        var outfit = fx.BaseMod.Outfits.AddNew();
        outfit.Items ??= new(); // null by default on a fresh Outfit
        outfit.Items.Add(armor);

        var mergeParent = Traverse(fx, outfit.ToLink());

        fx.OutputMod.Armors.Should().BeEmpty();
        fx.OutputMod.Outfits.Should().BeEmpty();
        mergeParent.Should().BeFalse();
    }
}
