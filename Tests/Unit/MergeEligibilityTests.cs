using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="MergeEligibility"/> — the per-plugin merge-in decision that replaced the old
/// all-or-nothing per-mod switch.
///
/// <para>The bug it fixes: one mod entry can bundle plugins with opposite needs. "Lawless - A
/// Bandit Overhaul" supplies NPCs from Bandit War*.esp (which stay in the load order, so their
/// records must be REFERENCED) while those NPC records point at a race and head parts in a
/// bundled resource-only plugin that is NOT in the load order (so its records must be COPIED).
/// With one switch, turning merge off to protect the former left the latter dangling and the
/// output plugin could not be written at all.</para>
///
/// <para>The back-compat property matters as much as the new behaviour: a mod with no
/// resource-only plugins must still resolve to all-eligible or none-eligible, reproducing the
/// pre-2.2.3 result exactly.</para>
/// </summary>
public class MergeEligibilityTests
{
    private static ModKey Mk(string name) => MutagenFixtures.Mk(name);

    private static readonly ModKey NpcPlugin = Mk("BanditWar.esp");
    private static readonly ModKey PatchPlugin = Mk("BanditWar - ProjectJaKhaJay.esp");
    private static readonly ModKey ResourcePlugin = Mk("ProjectJaKhaJay.esp");
    private static readonly FormKey SomeNpc = FormKey.Factory("000801:BanditWar.esp");

    private static ModSetting Mod(string name, bool mergeIn, IEnumerable<ModKey> plugins,
        IEnumerable<ModKey>? resourceOnly = null, IEnumerable<FormKey>? npcs = null,
        Dictionary<ModKey, bool>? overrides = null) => new()
    {
        DisplayName = name,
        MergeInDependencyRecords = mergeIn,
        CorrespondingModKeys = plugins.ToList(),
        ResourceOnlyModKeys = new HashSet<ModKey>(resourceOnly ?? Enumerable.Empty<ModKey>()),
        NpcFormKeys = new HashSet<FormKey>(npcs ?? Enumerable.Empty<FormKey>()),
        PluginMergeInOverrides = overrides ?? new Dictionary<ModKey, bool>(),
    };

    // ── Back-compat: no resource-only plugins means the old behaviour, unchanged ──────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ModWithNoResourcePlugins_ResolvesAllOrNothing(bool mergeIn)
    {
        var mod = Mod("Plain", mergeIn, new[] { NpcPlugin, PatchPlugin }, npcs: new[] { SomeNpc });

        var eligible = MergeEligibility.GetMergeEligiblePlugins(mod, null);

        if (mergeIn) eligible.Should().BeEquivalentTo(new[] { NpcPlugin, PatchPlugin });
        else eligible.Should().BeEmpty();
    }

    // ── Rule 2: plugins that provide NPCs mirror their own mod's toggle ───────────────────

    [Fact]
    public void NonResourcePlugin_MirrorsItsOwnModsToggle_EvenWhenAResourceSiblingMerges()
    {
        var mod = Mod("Lawless", mergeIn: false,
            new[] { NpcPlugin, PatchPlugin, ResourcePlugin },
            resourceOnly: new[] { ResourcePlugin },
            npcs: new[] { SomeNpc });

        // The whole point: the NPC plugins stay referenced (they remain in the load order)
        // while the resource plugin is merged, from the SAME mod entry.
        MergeEligibility.IsPluginMergeEligible(mod, NpcPlugin, null).Should().BeFalse();
        MergeEligibility.IsPluginMergeEligible(mod, PatchPlugin, null).Should().BeFalse();
        MergeEligibility.IsPluginMergeEligible(mod, ResourcePlugin, null).Should().BeTrue();
    }

    // ── Rule 3: a resource plugin owned elsewhere inherits that owner's toggle ────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResourcePlugin_InheritsFromTheModEntryThatProvidesIt(bool ownerMergesIn)
    {
        var consumer = Mod("Lawless", mergeIn: false,
            new[] { NpcPlugin, ResourcePlugin },
            resourceOnly: new[] { ResourcePlugin },
            npcs: new[] { SomeNpc });

        var owner = Mod("Project ja-Kha'jay", ownerMergesIn, new[] { ResourcePlugin },
            npcs: new[] { FormKey.Factory("0008C4:ProjectJaKhaJay.esp") });

        var index = MergeEligibility.BuildNpcProvidingOwnerIndex(new[] { consumer, owner });

        MergeEligibility.IsPluginMergeEligible(consumer, ResourcePlugin, index).Should().Be(ownerMergesIn);
    }

    [Fact]
    public void OwnerIndex_IgnoresEntriesThatProvideNoNpcs()
    {
        var consumer = Mod("Lawless", mergeIn: false, new[] { ResourcePlugin },
            resourceOnly: new[] { ResourcePlugin }, npcs: new[] { SomeNpc });

        // An owner with merge OFF but no NPCs has no authority; rule 4 applies instead.
        var npcLessOwner = Mod("Textures Only", mergeIn: false, new[] { ResourcePlugin });

        var index = MergeEligibility.BuildNpcProvidingOwnerIndex(new[] { consumer, npcLessOwner });

        index.Should().NotContainKey(ResourcePlugin);
        MergeEligibility.IsPluginMergeEligible(consumer, ResourcePlugin, index).Should().BeTrue();
    }

    [Fact]
    public void ResourcePlugin_DoesNotInheritFromItsOwnEntry()
    {
        // The consumer provides NPCs and lists the plugin, but classifies it as a resource.
        // Deferring to itself would just re-apply rule 2 and re-create the original bug.
        var consumer = Mod("Lawless", mergeIn: false,
            new[] { NpcPlugin, ResourcePlugin },
            resourceOnly: new[] { ResourcePlugin },
            npcs: new[] { SomeNpc });

        var index = MergeEligibility.BuildNpcProvidingOwnerIndex(new[] { consumer });

        index.Should().NotContainKey(ResourcePlugin, "a plugin its own entry calls a resource doesn't own itself");
        MergeEligibility.IsPluginMergeEligible(consumer, ResourcePlugin, index).Should().BeTrue();
    }

    [Fact]
    public void OwnerIdentityIsByDisplayName_SoSnapshotsBehaveLikePersistedInstances()
    {
        var consumer = Mod("Lawless", mergeIn: false,
            new[] { NpcPlugin, ResourcePlugin },
            resourceOnly: new[] { ResourcePlugin },
            npcs: new[] { SomeNpc });

        // A distinct instance with the same DisplayName — what the dialog builds from live VMs.
        var consumerSnapshot = Mod("Lawless", mergeIn: false,
            new[] { NpcPlugin, ResourcePlugin },
            resourceOnly: new[] { ResourcePlugin },
            npcs: new[] { SomeNpc });

        var index = new Dictionary<ModKey, ModSetting> { [ResourcePlugin] = consumerSnapshot };

        // Must be recognised as the same entry (rule 3 skipped -> rule 4), not treated as a
        // separate owner whose merge=false would be inherited.
        MergeEligibility.IsPluginMergeEligible(consumer, ResourcePlugin, index).Should().BeTrue();
    }

    // ── Rule 4: unclaimed resource plugins default to merging ────────────────────────────

    [Fact]
    public void UnclaimedResourcePlugin_DefaultsToMerging()
    {
        var mod = Mod("Standalone", mergeIn: false, new[] { NpcPlugin, ResourcePlugin },
            resourceOnly: new[] { ResourcePlugin }, npcs: new[] { SomeNpc });

        MergeEligibility.IsPluginMergeEligible(mod, ResourcePlugin, null).Should().BeTrue();
    }

    // ── Rule 1: explicit overrides win over everything ───────────────────────────────────

    [Fact]
    public void ExplicitOverride_BeatsEveryDefault()
    {
        var owner = Mod("Project ja-Kha'jay", mergeIn: true, new[] { ResourcePlugin },
            npcs: new[] { FormKey.Factory("0008C4:ProjectJaKhaJay.esp") });

        var consumer = Mod("Lawless", mergeIn: true,
            new[] { NpcPlugin, ResourcePlugin },
            resourceOnly: new[] { ResourcePlugin },
            npcs: new[] { SomeNpc },
            overrides: new Dictionary<ModKey, bool>
            {
                [ResourcePlugin] = false, // against the owner's ON default
                [NpcPlugin] = false,      // against this mod's own ON toggle
            });

        var index = MergeEligibility.BuildNpcProvidingOwnerIndex(new[] { consumer, owner });

        MergeEligibility.IsPluginMergeEligible(consumer, ResourcePlugin, index).Should().BeFalse();
        MergeEligibility.IsPluginMergeEligible(consumer, NpcPlugin, index).Should().BeFalse();
        MergeEligibility.GetMergeEligiblePlugins(consumer, index).Should().BeEmpty();
    }

    // ── The motivating end-to-end shape ──────────────────────────────────────────────────

    [Fact]
    public void MotivatingCase_ReferencesLoadOrderPluginsButMergesTheMissingResource()
    {
        var jaKhajay = Mod("Project ja-Kha'jay- Khajiit Diversity Overhaul", mergeIn: true,
            new[] { ResourcePlugin }, npcs: new[] { FormKey.Factory("0008C4:ProjectJaKhaJay.esp") });

        var lawless = Mod("Lawless - A Bandit Overhaul", mergeIn: false,
            new[] { NpcPlugin, PatchPlugin, ResourcePlugin },
            resourceOnly: new[] { ResourcePlugin },
            npcs: new[] { SomeNpc });

        var index = MergeEligibility.BuildNpcProvidingOwnerIndex(new[] { lawless, jaKhajay });
        var eligible = MergeEligibility.GetMergeEligiblePlugins(lawless, index);

        eligible.Should().BeEquivalentTo(new[] { ResourcePlugin },
            "the resource plugin isn't in the load order so its records must be copied, while the " +
            "Bandit War plugins are and must stay referenced");
    }

    // ── Defensive shapes ────────────────────────────────────────────────────────────────

    [Fact]
    public void NullOwnerOrEmptyPluginList_ProduceNoEligiblePlugins()
    {
        MergeEligibility.GetMergeEligiblePlugins(null!, null).Should().BeEmpty();
        MergeEligibility.IsPluginMergeEligible(null!, NpcPlugin, null).Should().BeFalse();
        MergeEligibility.GetMergeEligiblePlugins(Mod("Empty", true, new ModKey[0]), null).Should().BeEmpty();
        MergeEligibility.BuildNpcProvidingOwnerIndex(null!).Should().BeEmpty();
    }

    [Fact]
    public void DuplicatePluginEntries_AreCollapsed()
    {
        var mod = Mod("Dupes", mergeIn: true, new[] { NpcPlugin, NpcPlugin, PatchPlugin },
            npcs: new[] { SomeNpc });

        MergeEligibility.GetMergeEligiblePlugins(mod, null)
            .Should().BeEquivalentTo(new[] { NpcPlugin, PatchPlugin });
    }
}
