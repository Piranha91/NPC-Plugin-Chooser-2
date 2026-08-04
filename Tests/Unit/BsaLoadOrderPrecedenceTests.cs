using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost.Adapters;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Broadcast archive lookup: enumerating every archive that holds a path
/// (<see cref="BsaHandler.LocateAllInBsas"/>) and picking between them by load
/// order (<see cref="NpcChooserBsaProviderAdapter.SelectByLoadOrder"/>).
///
/// <para>What these pin: the broadcast used to stop at the first hit, which made
/// the winner a function of the order mods were INDEXED (i.e. Settings.ModSettings
/// order) rather than of the load order. That is invisible while paths are
/// mod-unique and silently wrong the moment two archives ship the same path.</para>
///
/// <para>The tier-2 rule matters as much as tier 1: NPC2 indexes archives for mods
/// the user has NOT enabled, and load order cannot rank those. They must still
/// resolve — a mugshot of a disabled mod is a normal thing to render — so an
/// unranked candidate is a fallback, never a reason to report a miss.</para>
/// </summary>
public class BsaLoadOrderPrecedenceTests
{
    private static readonly ModKey Skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
    private static readonly ModKey EarlyMod = ModKey.FromNameAndExtension("EarlyMod.esp");
    private static readonly ModKey LateMod = ModKey.FromNameAndExtension("LateMod.esp");
    private static readonly ModKey NotInLoadOrder = ModKey.FromNameAndExtension("Disabled.esp");

    private static readonly IReadOnlyList<ModKey> ListedOrder =
        new List<ModKey> { Skyrim, EarlyMod, LateMod };

    // ─── SelectByLoadOrder ───────────────────────────────────────────────

    [Fact]
    public void LatestLoadingPluginWins_RegardlessOfCandidateOrder()
    {
        var candidates = new List<(ModKey, string)>
        {
            (LateMod, @"S:\mods\Late\Late.bsa"),
            (Skyrim, @"C:\Game\Data\Skyrim - Meshes0.bsa"),
            (EarlyMod, @"S:\mods\Early\Early.bsa"),
        };

        var winner = NpcChooserBsaProviderAdapter.SelectByLoadOrder(candidates, ListedOrder);

        winner.Should().NotBeNull();
        winner!.Value.ModKey.Should().Be(LateMod);
        winner.Value.BsaPath.Should().Be(@"S:\mods\Late\Late.bsa");

        // Same set, reversed: the verdict must come from the load order, not
        // from where the candidate happened to sit in the list.
        var reversed = NpcChooserBsaProviderAdapter.SelectByLoadOrder(
            candidates.AsEnumerable().Reverse().ToList(), ListedOrder);
        reversed!.Value.ModKey.Should().Be(LateMod);
    }

    [Fact]
    public void VanillaLosesToAnyLoadedMod()
    {
        // The case the old first-hit broadcast got wrong most often: vanilla is
        // always indexed early, so it won by default.
        var candidates = new List<(ModKey, string)>
        {
            (Skyrim, @"C:\Game\Data\Skyrim - Meshes0.bsa"),
            (EarlyMod, @"S:\mods\Early\Early.bsa"),
        };

        var winner = NpcChooserBsaProviderAdapter.SelectByLoadOrder(candidates, ListedOrder);

        winner!.Value.ModKey.Should().Be(EarlyMod);
    }

    [Fact]
    public void CandidatesOutsideTheLoadOrderAreNotRanked()
    {
        var candidates = new List<(ModKey, string)>
        {
            (NotInLoadOrder, @"S:\mods\Disabled\Disabled.bsa"),
            (EarlyMod, @"S:\mods\Early\Early.bsa"),
        };

        var winner = NpcChooserBsaProviderAdapter.SelectByLoadOrder(candidates, ListedOrder);

        // The disabled mod contributes nothing at runtime, so it cannot outrank
        // a plugin that is actually loaded — even though nothing about its
        // position says otherwise.
        winner!.Value.ModKey.Should().Be(EarlyMod);
    }

    [Fact]
    public void ReturnsNull_WhenNoCandidateIsInTheLoadOrder()
    {
        var candidates = new List<(ModKey, string)>
        {
            (NotInLoadOrder, @"S:\mods\Disabled\Disabled.bsa"),
        };

        // Null is the caller's signal to fall back to the first indexed match,
        // NOT to report the asset missing.
        NpcChooserBsaProviderAdapter.SelectByLoadOrder(candidates, ListedOrder)
            .Should().BeNull();
    }

    [Fact]
    public void SamePluginTwoArchives_KeepsTheFirstEnumerated()
    {
        // Load order cannot separate two archives owned by one plugin, so the
        // selection must be stable rather than arbitrary between runs.
        var candidates = new List<(ModKey, string)>
        {
            (LateMod, @"S:\mods\Late\Late - Textures.bsa"),
            (LateMod, @"S:\mods\Late\Late.bsa"),
        };

        var winner = NpcChooserBsaProviderAdapter.SelectByLoadOrder(candidates, ListedOrder);

        winner!.Value.BsaPath.Should().Be(@"S:\mods\Late\Late - Textures.bsa");
    }

    [Fact]
    public void EmptyInputsAreNull()
    {
        NpcChooserBsaProviderAdapter.SelectByLoadOrder(
            Array.Empty<(ModKey, string)>(), ListedOrder).Should().BeNull();

        // An unresolved environment yields an empty load order; that must read
        // as "cannot rank", not as an exception.
        NpcChooserBsaProviderAdapter.SelectByLoadOrder(
            new List<(ModKey, string)> { (EarlyMod, "x.bsa") },
            Array.Empty<ModKey>()).Should().BeNull();
    }

    // ─── LocateAllInBsas ─────────────────────────────────────────────────

    private static BsaHandler HandlerWith(
        params (ModKey Key, string BsaPath, string[] Files)[] archives)
    {
        var contents = new Dictionary<ModKey, Dictionary<string, HashSet<string>>>();
        foreach (var (key, bsaPath, files) in archives)
        {
            if (!contents.TryGetValue(key, out var byArchive))
            {
                byArchive = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                contents[key] = byArchive;
            }
            // Mirrors the real index: OrdinalIgnoreCase, backslash separators.
            byArchive[bsaPath] = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        }

        var handler = Reflect.Uninitialized<BsaHandler>();
        Reflect.SetField(handler, "_bsaContents", contents);
        Reflect.SetField(handler, "_bsaContentsLock", new object());
        return handler;
    }

    [Fact]
    public void LocateAll_ReturnsEveryArchiveHoldingThePath()
    {
        var handler = HandlerWith(
            (Skyrim, @"C:\Game\Data\Skyrim - Meshes0.bsa", new[] { @"meshes\armor\boots.nif" }),
            (EarlyMod, @"S:\mods\Early\Early.bsa", new[] { @"meshes\armor\boots.nif", @"meshes\other.nif" }),
            (LateMod, @"S:\mods\Late\Late.bsa", new[] { @"meshes\unrelated.nif" }));

        var matches = handler.LocateAllInBsas(@"meshes\armor\boots.nif");

        // The whole point of the method: the first-hit overloads would have
        // returned one of these and hidden the conflict entirely.
        matches.Select(m => m.ModKey).Should().BeEquivalentTo(new[] { Skyrim, EarlyMod });
    }

    [Fact]
    public void LocateAll_MatchesCaseInsensitivelyAndNormalizesSlashes()
    {
        var handler = HandlerWith(
            (EarlyMod, @"S:\mods\Early\Early.bsa", new[] { @"MESHES\Armor\BOOTS.NIF" }));

        // Callers pass renderer-style forward slashes and arbitrary casing; the
        // index stores backslashes. Both have to be reconciled here or the
        // fallback misses files that are demonstrably present.
        handler.LocateAllInBsas("meshes/armor/boots.nif").Should().HaveCount(1);
        handler.LocateAllInBsas(@"meshes\armor\boots.nif").Should().HaveCount(1);
    }

    [Fact]
    public void LocateAll_SurfacesBothArchivesOfOneMod()
    {
        // Divine Elegance's shape: meshes in one archive, textures in another,
        // both owned by the same plugin.
        var handler = HandlerWith(
            (EarlyMod, @"S:\mods\Early\Early.bsa", new[] { @"meshes\robe.nif" }),
            (EarlyMod, @"S:\mods\Early\Early - Textures.bsa", new[] { @"textures\robe.dds" }));

        handler.LocateAllInBsas(@"meshes\robe.nif").Single().BsaPath
            .Should().Be(@"S:\mods\Early\Early.bsa");
        handler.LocateAllInBsas(@"textures\robe.dds").Single().BsaPath
            .Should().Be(@"S:\mods\Early\Early - Textures.bsa");
    }

    [Fact]
    public void LocateAll_ReturnsEmptyForAnUnknownPath()
    {
        var handler = HandlerWith(
            (EarlyMod, @"S:\mods\Early\Early.bsa", new[] { @"meshes\robe.nif" }));

        handler.LocateAllInBsas(@"meshes\nothing.nif").Should().BeEmpty();
    }
}
