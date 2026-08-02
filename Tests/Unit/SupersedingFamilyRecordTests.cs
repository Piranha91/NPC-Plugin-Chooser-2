using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <c>Patcher.HasSupersedingFamilyRecord</c> — the cost gate on the mesh-only re-pairing walk.
///
/// <para>Re-pairing exists because a mod that ships FaceGen but no plugin record has its mesh
/// paired with the NPC's ORIGIN record, which is wrong when a DLC supersedes it (Dawnguard swaps
/// the vampires' eye head part and mods bake against Dawnguard's version). But probing costs a NIF
/// parse, often after a BSA extraction, and 1136 of the measuring run's 8338 selections are
/// record-less — while the ladder row those mods land on ("mod ships both halves") is deliberately
/// never NIF-probed. So this in-memory check runs first: with nothing in the family superseding
/// the origin record there is nothing to re-pair with, and no mesh is opened at all.</para>
///
/// <para>In-memory Mutagen mods and a real link cache; no game install.</para>
/// </summary>
public class SupersedingFamilyRecordTests
{
    private static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm");
    private static readonly ModKey Dawnguard = ModKey.FromFileName("Dawnguard.esm");
    private static readonly ModKey Unrelated = ModKey.FromFileName("SomeOverhaul.esp");

    private static readonly FormKey Vampire = FormKey.Factory("033870:Skyrim.esm");

    /// <summary>A link cache over the given plugin order, where each plugin named in
    /// <paramref name="carriers"/> holds a record for the NPC.</summary>
    private static ILinkCache<ISkyrimMod, ISkyrimModGetter> Cache(ModKey[] order, params ModKey[] carriers)
    {
        var lo = new LoadOrder<IModListingGetter<ISkyrimModGetter>>();
        foreach (var key in order)
        {
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            if (carriers.Contains(key))
            {
                var npc = new Npc(Vampire, MutagenFixtures.Release)
                {
                    EditorID = "EncVampire03BretonF_" + key.Name
                };
                mod.Npcs.Add(npc);
            }
            lo.Add(new ModListing<ISkyrimModGetter>(mod, enabled: true));
        }

        return lo.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
    }

    private static bool HasSuperseding(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> cache, HashSet<ModKey> family) =>
        Patcher.HasSupersedingFamilyRecord(cache, Vampire, family);

    [Fact]
    public void FamilyMemberOverridingTheOrigin_Triggers()
    {
        // The measured case: Dawnguard overrides a Skyrim.esm vampire.
        var p = Cache(new[] { Skyrim, Dawnguard }, Skyrim, Dawnguard);

        HasSuperseding(p, new HashSet<ModKey> { Skyrim, Dawnguard }).Should().BeTrue();
    }

    [Fact]
    public void OnlyTheOriginCarriesIt_DoesNotTrigger()
    {
        // The overwhelmingly common shape — and the whole point of the gate: no mesh is parsed.
        var p = Cache(new[] { Skyrim, Dawnguard }, Skyrim);

        HasSuperseding(p, new HashSet<ModKey> { Skyrim, Dawnguard }).Should().BeFalse();
    }

    [Fact]
    public void OverrideOutsideTheFamily_DoesNotTrigger()
    {
        // An unrelated overhaul overriding the NPC is not a candidate to pair the mesh with —
        // that is the cross-author mismatch the origin rule exists to avoid.
        var p = Cache(new[] { Skyrim, Unrelated }, Skyrim, Unrelated);

        HasSuperseding(p, new HashSet<ModKey> { Skyrim, Dawnguard }).Should().BeFalse();
    }

    [Fact]
    public void EmptyFamily_DoesNotTrigger()
    {
        var p = Cache(new[] { Skyrim, Dawnguard }, Skyrim, Dawnguard);

        HasSuperseding(p, new HashSet<ModKey>()).Should().BeFalse();
    }
}
