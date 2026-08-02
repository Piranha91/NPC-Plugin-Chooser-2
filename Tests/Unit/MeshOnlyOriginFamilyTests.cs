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
/// <c>Patcher.ResolveOriginFamilyPlugins</c> — the candidate set for re-pairing a mesh-only
/// selection whose default record pairing fails the head-part probe.
///
/// <para><b>Why.</b> A mod that ships FaceGen but no plugin record has its mesh paired with the
/// record from the NPC's ORIGIN plugin, on the assumption the author built against it. That is
/// wrong whenever a later plugin of the same family supersedes it: Dawnguard swaps the vampires'
/// eye head part (FemaleEyesHumanDemon -> FemaleEyesHumanVampire) and mods bake their meshes
/// against Dawnguard's version, so the origin pairing dark-faces. The walk retries the family's
/// other records, winner-first.</para>
///
/// <para>This pins WHICH plugins are eligible, which is the part that can quietly go wrong: too
/// wide and the mesh gets paired with an unrelated mod's record — the exact failure the
/// origin-pairing rule exists to prevent.</para>
/// </summary>
public class MeshOnlyOriginFamilyTests
{
    private static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm");
    private static readonly ModKey Dawnguard = ModKey.FromFileName("Dawnguard.esm");
    private static readonly ModKey Dragonborn = ModKey.FromFileName("Dragonborn.esm");
    private static readonly ModKey CreationClub = ModKey.FromFileName("ccBGSSSE001-Fish.esm");
    private static readonly ModKey ThreeDnpc = ModKey.FromFileName("3DNPC.esp");
    private static readonly ModKey ThreeDnpcPatch = ModKey.FromFileName("3DNPC Patch.esp");

    private static readonly FormKey VanillaVampire = FormKey.Factory("033870:Skyrim.esm");
    private static readonly FormKey ModNpc = FormKey.Factory("0012F7:3DNPC.esp");

    /// <summary>A Patcher with only the two collaborators this seam reads.</summary>
    private static Patcher Make(IEnumerable<ModSetting> modSettings, params ModKey[] baseGame)
    {
        var env = Reflect.Uninitialized<EnvironmentStateProvider>();
        Reflect.SetField(env, "SkyrimVersion", Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);

        var patcher = Reflect.Uninitialized<Patcher>();
        Reflect.SetField(patcher, "_environmentStateProvider", env);
        Reflect.SetField(patcher, "_npcProvidingOwnersByPlugin",
            MergeEligibility.BuildNpcProvidingOwnerIndex(modSettings));
        return patcher;
    }

    private static HashSet<ModKey>? Resolve(Patcher p, FormKey npc) =>
        Reflect.Invoke<HashSet<ModKey>>(p, "ResolveOriginFamilyPlugins", npc);

    private static ModSetting Mod(string name, ModKey[] plugins, FormKey[] npcs) => new()
    {
        DisplayName = name,
        CorrespondingModKeys = plugins.ToList(),
        NpcFormKeys = npcs.ToHashSet(),
    };

    [Fact]
    public void VanillaNpc_FamilyIsTheBaseGameIncludingDlc()
    {
        // BaseGamePlugins is derived from the release's Implicits, so this asserts the real set
        // rather than a fixture: the DLC ship with every SE/AE/VR install, which is why an author's
        // Creation Kit shows Dawnguard's record and their mesh is baked against it.
        var patcher = Make(new[] { Mod("Base Game", new[] { Skyrim, Dawnguard }, new[] { VanillaVampire }) });

        var family = Resolve(patcher, VanillaVampire);

        family.Should().NotBeNull();
        family!.Should().Contain(Skyrim).And.Contain(Dawnguard).And.Contain(Dragonborn);
    }

    [Fact]
    public void VanillaFamily_ExcludesCreationClub()
    {
        // Explicitly not "the base game": CC is optional content with its own mod entry, so its
        // records are no more authoritative for a vanilla NPC than any other mod's.
        var patcher = Make(new[] { Mod("Base Game", new[] { Skyrim, Dawnguard }, new[] { VanillaVampire }) });

        Resolve(patcher, VanillaVampire)!.Should().NotContain(CreationClub);
    }

    [Fact]
    public void ModAddedNpc_FamilyIsItsOwnModEntrysPlugins()
    {
        var patcher = Make(new[]
        {
            Mod("Interesting NPCs SE", new[] { ThreeDnpc, ThreeDnpcPatch }, new[] { ModNpc })
        });

        var family = Resolve(patcher, ModNpc);

        family.Should().BeEquivalentTo(new[] { ThreeDnpc, ThreeDnpcPatch });
    }

    [Fact]
    public void OwnerThatDoesNotActuallyListTheNpc_IsRejected()
    {
        // The owner index is FIRST-WINS across every mod entry, so a mod that merely names the
        // plugin can be handed back as its owner. Pairing a mesh against that entry's records
        // would be exactly the cross-author mismatch this whole mechanism exists to avoid.
        var patcher = Make(new[]
        {
            Mod("Some Unrelated Mod", new[] { ThreeDnpc, ThreeDnpcPatch }, new[] { FormKey.Factory("999999:3DNPC.esp") })
        });

        Resolve(patcher, ModNpc).Should().BeNull();
    }

    [Fact]
    public void UnknownOriginPlugin_HasNoFamily()
    {
        var patcher = Make(System.Array.Empty<ModSetting>());

        Resolve(patcher, ModNpc).Should().BeNull();
    }

    [Fact]
    public void SinglePluginFamily_IsStillReturned_AndTheCallerSkipsIt()
    {
        // The <2 guard lives in the walk, not here, so this stays a faithful "what is the family"
        // answer; the walk declines to probe when there is only one candidate.
        var patcher = Make(new[] { Mod("Solo Mod", new[] { ThreeDnpc }, new[] { ModNpc }) });

        Resolve(patcher, ModNpc).Should().BeEquivalentTo(new[] { ThreeDnpc });
    }
}
