using System;
using System.Collections.Generic;
using System.Linq;
using SkyRender;
using SkyRender.Character;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// The full-load-order fallback behind
/// <see cref="NpcMeshResolver.SelectEnabledLoadOrderPluginsToIndex"/> and
/// <see cref="NpcMeshResolver.CollectUnreachableFallbackAssetPaths"/> — the escape hatch
/// for archives the record-scoped donor widening cannot discover.
///
/// The gap it closes: BSA ownership is by plugin FILENAME, and some mods ship their
/// records in one plugin while their assets sit in an archive named after a second,
/// resource-only plugin with no master link back (a dummy-loader ESP — e.g. "TW3 Geralt
/// Prologue Gear.esp" with its meshes in TW3Resources.bsa, loaded by a byte-identical
/// TW3Resources.esp). No walked record ever names the carrier plugin, so donor+master
/// selection misses it; when a fallback-eligible attire asset is provably unreachable,
/// the resolver instead indexes every enabled load-order plugin — the engine's own rule.
///
/// Pure and static, so these run with no game install, no environment and no archive I/O.
/// </summary>
public class NpcMeshResolverFullLoadOrderFallbackTests
{
    private static ModKey Mk(string fileName) => MutagenFixtures.Mk(fileName);

    private static readonly ModKey[] Vanilla =
    {
        Mk("Skyrim.esm"), Mk("Update.esm"), Mk("Dawnguard.esm"),
        Mk("HearthFires.esm"), Mk("Dragonborn.esm"),
    };

    private static IEnumerable<string> Names(IEnumerable<ModKey> keys)
        => keys.Select(k => k.FileName.String);

    // =====================================================================================
    // SelectEnabledLoadOrderPluginsToIndex — which plugins a full sweep offers the index.
    // =====================================================================================

    [Fact]
    public void DisabledListings_AreDropped()
    {
        // The game only loads an ENABLED plugin's archives, so a disabled listing's BSA
        // is exactly as invisible in game as it would be here.
        var result = NpcMeshResolver.SelectEnabledLoadOrderPluginsToIndex(
            new[]
            {
                (Mk("Skyrim.esm"), true),
                (Mk("TW3Resources.esp"), true),
                (Mk("DisabledMod.esp"), false),
            },
            Vanilla, null);

        Names(result).Should().BeEquivalentTo("TW3Resources.esp");
    }

    [Fact]
    public void BaseGameAndCreationClub_AreFilteredOut()
    {
        // Their archives are indexed at startup via the synthetic ModSettings.
        var result = NpcMeshResolver.SelectEnabledLoadOrderPluginsToIndex(
            new[]
            {
                (Mk("Skyrim.esm"), true),
                (Mk("Dawnguard.esm"), true),
                (Mk("ccBGSSSE001-Fish.esm"), true),
                (Mk("Armor.esp"), true),
            },
            Vanilla,
            new[] { Mk("ccBGSSSE001-Fish.esm") });

        Names(result).Should().BeEquivalentTo("Armor.esp");
    }

    [Fact]
    public void NullVanillaAndCcLists_AreTolerated()
    {
        var result = NpcMeshResolver.SelectEnabledLoadOrderPluginsToIndex(
            new[] { (Mk("Armor.esp"), true) }, null, null);

        Names(result).Should().BeEquivalentTo("Armor.esp");
    }

    [Fact]
    public void NullLoadOrder_ReturnsEmpty()
    {
        NpcMeshResolver.SelectEnabledLoadOrderPluginsToIndex(null, Vanilla, null)
            .Should().BeEmpty();
    }

    [Fact]
    public void NullModKey_IsNeverReturned()
    {
        var result = NpcMeshResolver.SelectEnabledLoadOrderPluginsToIndex(
            new[] { (ModKey.Null, true), (Mk("Armor.esp"), true) },
            Vanilla, null);

        result.Should().NotContain(ModKey.Null);
        Names(result).Should().BeEquivalentTo("Armor.esp");
    }

    [Fact]
    public void DuplicateListings_AreDeduplicated()
    {
        var result = NpcMeshResolver.SelectEnabledLoadOrderPluginsToIndex(
            new[] { (Mk("Armor.esp"), true), (Mk("Armor.esp"), true) },
            Vanilla, null);

        result.Should().ContainSingle();
    }

    // =====================================================================================
    // CollectUnreachableFallbackAssetPaths — when the sweep triggers.
    // =====================================================================================

    private static readonly Func<string, bool> Nowhere = _ => false;

    private static MeshOverride Fallback(string meshPath) => new()
    {
        Key = "Outfit:test",
        MeshPath = meshPath,
        AllowLoadOrderFallback = true,
    };

    [Fact]
    public void NonFallbackOverrides_AreNotProbed()
    {
        // Scope-chain overrides (wigs, worn armor, donor outfits) resolve from mod
        // folders the probes deliberately don't cover — probing them would false-trigger
        // the sweep on every render.
        var scoped = new MeshOverride
        {
            Key = "WigForward",
            MeshPath = @"meshes\hair\wig.nif",
            AllowLoadOrderFallback = false,
        };

        NpcMeshResolver.CollectUnreachableFallbackAssetPaths(
                new[] { scoped }, Nowhere, Nowhere)
            .Should().BeEmpty();
    }

    [Fact]
    public void RootedPath_IsReachableByConstruction()
    {
        // A rooted path was already rebased onto an on-disk mod folder.
        NpcMeshResolver.CollectUnreachableFallbackAssetPaths(
                new[] { Fallback(@"C:\Mods\Gear\meshes\armor.nif") }, Nowhere, Nowhere)
            .Should().BeEmpty();
    }

    [Fact]
    public void LooseDataFolderHit_IsReachable()
    {
        NpcMeshResolver.CollectUnreachableFallbackAssetPaths(
                new[] { Fallback(@"meshes\Geralt Prologue\armor_1.nif") },
                p => p == @"meshes\Geralt Prologue\armor_1.nif",
                Nowhere)
            .Should().BeEmpty();
    }

    [Fact]
    public void IndexedArchiveHit_IsReachable()
    {
        NpcMeshResolver.CollectUnreachableFallbackAssetPaths(
                new[] { Fallback(@"meshes\Geralt Prologue\armor_1.nif") },
                Nowhere,
                p => p == @"meshes\Geralt Prologue\armor_1.nif")
            .Should().BeEmpty();
    }

    [Fact]
    public void UnreachableMesh_IsReported()
    {
        NpcMeshResolver.CollectUnreachableFallbackAssetPaths(
                new[] { Fallback(@"meshes\Geralt Prologue\glove_1.nif") }, Nowhere, Nowhere)
            .Should().BeEquivalentTo(@"meshes\Geralt Prologue\glove_1.nif");
    }

    [Fact]
    public void TexturePaths_AreProbed_OnBothChannels()
    {
        // The mesh resolves but its TXST overrides (flat + AlternateTextures) do not —
        // a texture-only resource archive must trigger the sweep too.
        var over = new MeshOverride
        {
            Key = "Outfit:test",
            MeshPath = @"meshes\armor\cuirass.nif",
            AllowLoadOrderFallback = true,
            Textures = new Dictionary<int, string> { [0] = @"textures\armor\cuirass.dds" },
            AlternateTextures = new[]
            {
                new AlternateTextureSpec
                {
                    ShapeName = "Cuirass",
                    ShapeIndex = 0,
                    Textures = new Dictionary<int, string> { [1] = @"textures\armor\cuirass_n.dds" },
                },
            },
        };

        NpcMeshResolver.CollectUnreachableFallbackAssetPaths(
                new[] { over },
                p => p == @"meshes\armor\cuirass.nif",
                Nowhere)
            .Should().BeEquivalentTo(
                @"textures\armor\cuirass.dds",
                @"textures\armor\cuirass_n.dds");
    }

    [Fact]
    public void DuplicatePathsAcrossOverrides_ReportOnce()
    {
        // Case-insensitive: one shared missing texture reports (and logs) once.
        var a = Fallback(@"meshes\Geralt Prologue\boots_1.nif");
        var b = Fallback(@"MESHES\Geralt Prologue\BOOTS_1.NIF");

        NpcMeshResolver.CollectUnreachableFallbackAssetPaths(
                new[] { a, b }, Nowhere, Nowhere)
            .Should().ContainSingle();
    }

    [Fact]
    public void EmptyMeshPath_IsSkipped()
    {
        NpcMeshResolver.CollectUnreachableFallbackAssetPaths(
                new[] { new MeshOverride { Key = "Empty", AllowLoadOrderFallback = true } },
                Nowhere, Nowhere)
            .Should().BeEmpty();
    }
}
