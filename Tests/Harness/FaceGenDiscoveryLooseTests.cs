using System.Collections.ObjectModel;
using System.IO;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// Tests read <see cref="DiscoveryMetrics"/>' process-wide counters, so this collection must not run
/// alongside any other test that could exercise discovery code at the same time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiscoveryMetricsCollection
{
    public const string Name = "DiscoveryMetrics";
}

/// <summary>
/// Targeted loose FaceGen discovery (<see cref="FaceGenDiscovery.DiscoverLooseFaceGen"/>). The contract is
/// stated against the consumers: for any folder layout, <see cref="FaceGenScanner"/> and
/// <see cref="VM_ModSetting.FaceGenExists"/> must see the same FaceGen as they did through the original
/// cache of every mesh and texture (<see cref="OriginalLooseCache"/>), while unrelated assets are no longer
/// enumerated or retained.
/// </summary>
[Collection(DiscoveryMetricsCollection.Name)]
public class FaceGenDiscoveryLooseTests
{
    private const string Geom = "Meshes/actors/character/FaceGenData/FaceGeom";
    private const string Tint = "Textures/actors/character/FaceGenData/FaceTint";

    /// <summary>The cache as it was built before targeted discovery: every *.dds under Textures and every
    /// *.nif under Meshes, as forward-slash paths relative to the folder. Kept here as the oracle.</summary>
    private static Dictionary<string, HashSet<string>> OriginalLooseCache(params string[] folders)
    {
        var all = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var modPath in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(modPath)) continue;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var textures = Path.Combine(modPath, "Textures");
            if (Directory.Exists(textures))
                foreach (var f in Directory.EnumerateFiles(textures, "*.dds", SearchOption.AllDirectories))
                    files.Add(Path.GetRelativePath(modPath, f).Replace('\\', '/'));
            var meshes = Path.Combine(modPath, "Meshes");
            if (Directory.Exists(meshes))
                foreach (var f in Directory.EnumerateFiles(meshes, "*.nif", SearchOption.AllDirectories))
                    files.Add(Path.GetRelativePath(modPath, f).Replace('\\', '/'));
            if (files.Count > 0) all[modPath] = files;
        }
        return all;
    }

    private static void Touch(TempDir dir, string relative) => dir.WriteText(relative, "x");

    /// <summary>A mod with FaceGen buried among a realistic amount of unrelated meshes and textures.</summary>
    private static void SeedMixedMod(TempDir dir)
    {
        for (int i = 0; i < 40; i++)
        {
            Touch(dir, $"Meshes/armor/set{i}/cuirass_{i}.nif");
            Touch(dir, $"Textures/armor/set{i}/cuirass_{i}.dds");
            Touch(dir, $"Textures/armor/set{i}/cuirass_{i}_n.dds");
        }
        Touch(dir, "Meshes/actors/character/character assets/hair.nif");
        Touch(dir, "Textures/actors/character/female/body.dds");
        Touch(dir, $"{Geom}/Skyrim.esm/00013BA1.nif");
        Touch(dir, $"{Tint}/Skyrim.esm/00013BA1.dds");
        Touch(dir, $"{Geom}/MyMod.esp/00000D62.nif");
        Touch(dir, $"{Tint}/MyMod.esp/00000D63.dds");
    }

    private static void AssertConsumersSeeTheSameFaceGen(string folder, Dictionary<string, HashSet<string>> targeted)
    {
        var original = OriginalLooseCache(folder);
        var none = new Dictionary<string, HashSet<string>>();

        var before = FaceGenScanner.CreateFaceGenScanResultFromCache(new[] { folder }, "Mod", original, none);
        var after = FaceGenScanner.CreateFaceGenScanResultFromCache(new[] { folder }, "Mod", targeted, none);
        after.MeshesExist.Should().Be(before.MeshesExist);
        after.TexturesExist.Should().Be(before.TexturesExist);
        after.FaceGenFiles.Keys.Should().Equal(before.FaceGenFiles.Keys, "plugin spellings and their order must match");
        foreach (var (plugin, ids) in before.FaceGenFiles)
        {
            after.FaceGenFiles[plugin].Should().BeEquivalentTo(ids);
        }

        var vm = Reflect.Uninitialized<VM_ModSetting>();
        Reflect.SetField(vm, "<CorrespondingFolderPaths>k__BackingField", new ObservableCollection<string>(new[] { folder }));
        foreach (var id in before.FaceGenFiles.SelectMany(kv => kv.Value.Select(v => (kv.Key, v))).Where(x => x.v.Length == 8))
        {
            // Plugin folders like "backup" are not plugin names; FormKey parsing rejects them in both caches.
            if (!FormKey.TryFactory($"{id.v.Substring(2)}:{id.Key}", out var fk)) continue;
            vm.FaceGenExists(fk, targeted, new HashSet<string>())
                .Should().Be(vm.FaceGenExists(fk, original, new HashSet<string>()), $"FaceGenExists({fk})");
        }
    }

    [Fact]
    public void MixedMod_KeepsExactlyTheFaceGenPaths_InTheOriginalFormat()
    {
        using var dir = new TempDir(nameof(MixedMod_KeepsExactlyTheFaceGenPaths_InTheOriginalFormat));
        SeedMixedMod(dir);

        var targeted = FaceGenDiscovery.DiscoverLooseFaceGen(new[] { dir.Path });

        var original = OriginalLooseCache(dir.Path)[dir.Path];
        var expected = original.Where(p =>
            p.StartsWith(Geom + "/", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(Tint + "/", StringComparison.OrdinalIgnoreCase)).ToList();
        expected.Should().HaveCount(4);
        targeted[dir.Path].Should().BeEquivalentTo(expected, "same strings, same casing as the original cache");
        targeted[dir.Path].Should().NotContain(p => p.Contains("armor"));
        AssertConsumersSeeTheSameFaceGen(dir.Path, targeted);
    }

    [Fact]
    public void UnrelatedAssets_AreNotEnumerated()
    {
        using var dir = new TempDir(nameof(UnrelatedAssets_AreNotEnumerated));
        SeedMixedMod(dir);
        DiscoveryMetrics.Reset();
        DiscoveryMetrics.Enabled = true;
        try
        {
            FaceGenDiscovery.DiscoverLooseFaceGen(new[] { dir.Path });
            DiscoveryMetrics.SnapshotCounters()["LooseCandidatePaths"].Should().Be(4,
                "only the facegeom/facetint trees are walked, not the 122 unrelated files");
        }
        finally
        {
            DiscoveryMetrics.Enabled = false;
        }
    }

    [Theory]
    [InlineData("mesh-only")]
    [InlineData("tint-only")]
    [InlineData("none")]
    public void MeshOnly_TintOnly_AndNoFaceGen_MatchTheOriginalConsumers(string layout)
    {
        using var dir = new TempDir("layout_" + layout);
        Touch(dir, "Meshes/weapons/sword.nif");
        Touch(dir, "Textures/weapons/sword.dds");
        if (layout == "mesh-only") Touch(dir, $"{Geom}/Skyrim.esm/0001A696.nif");
        if (layout == "tint-only") Touch(dir, $"{Tint}/Skyrim.esm/0001A696.dds");

        var targeted = FaceGenDiscovery.DiscoverLooseFaceGen(new[] { dir.Path });

        if (layout == "none")
        {
            targeted.Should().NotContainKey(dir.Path, "a folder with no FaceGen gets no entry");
        }
        AssertConsumersSeeTheSameFaceGen(dir.Path, targeted);
        var scan = FaceGenScanner.CreateFaceGenScanResultFromCache(new[] { dir.Path }, "Mod", targeted, new());
        scan.MeshesExist.Should().Be(layout == "mesh-only");
        scan.TexturesExist.Should().Be(layout == "tint-only");
    }

    [Fact]
    public void PluginSubfolders_CasingVariants_AndNestedLayouts_AreKeptLikeTheOriginal()
    {
        using var dir = new TempDir(nameof(PluginSubfolders_CasingVariants_AndNestedLayouts_AreKeptLikeTheOriginal));
        // Upper-case and lower-case tree spellings, several plugins, upper-case extension.
        Touch(dir, "MESHES/ACTORS/Character/facegendata/facegeom/Skyrim.esm/00013BA1.NIF");
        Touch(dir, "meshes/actors/character/FACEGENDATA/FACEGEOM/Dawnguard.esm/00002B6C.nif");
        Touch(dir, "textures/actors/character/facegendata/facetint/HearthFires.esm/0000090D.DDS");
        // Layouts the game ignores but the original cache held: a backup folder inside a plugin folder,
        // a file directly in facegeom, a non-hex file name. Consumers decide what they mean.
        Touch(dir, "Meshes/actors/character/FaceGenData/FaceGeom/Skyrim.esm/backup/00013BA1.nif");
        Touch(dir, "Meshes/actors/character/FaceGenData/FaceGeom/stray.nif");
        Touch(dir, "Textures/actors/character/FaceGenData/FaceTint/Skyrim.esm/00013BA1-.dds");

        var targeted = FaceGenDiscovery.DiscoverLooseFaceGen(new[] { dir.Path });

        targeted[dir.Path].Should().HaveCount(6);
        targeted[dir.Path].Should().BeEquivalentTo(OriginalLooseCache(dir.Path)[dir.Path]);
        AssertConsumersSeeTheSameFaceGen(dir.Path, targeted);
    }

    [Fact]
    public void MixedCasePluginFolders_KeepTheOriginalFirstSpelling()
    {
        // The facetint and facegeom folders of one plugin differ only in case. FaceGenScanner keeps the
        // spelling it meets first; that spelling becomes the persisted FaceGen-only FormKey, so the targeted
        // cache must present textures before meshes, as the original enumeration did.
        using var dir = new TempDir(nameof(MixedCasePluginFolders_KeepTheOriginalFirstSpelling));
        Touch(dir, $"{Geom}/glenmoril.esm/00007B80.nif");
        Touch(dir, $"{Tint}/Glenmoril.esm/00007B80.dds");

        var targeted = FaceGenDiscovery.DiscoverLooseFaceGen(new[] { dir.Path });
        var scan = FaceGenScanner.CreateFaceGenScanResultFromCache(new[] { dir.Path }, "Mod", targeted, new());

        scan.FaceGenFiles.Keys.Should().Equal("Glenmoril.esm");
        AssertConsumersSeeTheSameFaceGen(dir.Path, targeted);
    }

    [Fact]
    public void LookalikeAndRelocatedTrees_AreNotFaceGen()
    {
        // Deliberate narrowing: a sibling like "facegeom_old" or a whole data tree nested one level down is
        // not loaded by the game. The original cache let FaceGenScanner's substring test accept such files;
        // targeted discovery only reads the two real roots. (No mod in the reference instance uses either.)
        using var dir = new TempDir(nameof(LookalikeAndRelocatedTrees_AreNotFaceGen));
        Touch(dir, "Meshes/actors/character/FaceGenData/FaceGeom_old/Skyrim.esm/00013BA1.nif");
        Touch(dir, "Meshes/extra/meshes/actors/character/facegendata/facegeom/Skyrim.esm/00013BA1.nif");

        FaceGenDiscovery.DiscoverLooseFaceGen(new[] { dir.Path }).Should().BeEmpty();
    }

    [Fact]
    public void DuplicateFolderReferences_AreEnumeratedOnce_AndServeEverySpelling()
    {
        using var dir = new TempDir(nameof(DuplicateFolderReferences_AreEnumeratedOnce_AndServeEverySpelling));
        SeedMixedMod(dir);
        var trailing = dir.Path + Path.DirectorySeparatorChar;
        var upper = dir.Path.ToUpperInvariant();

        DiscoveryMetrics.Reset();
        DiscoveryMetrics.Enabled = true;
        Dictionary<string, HashSet<string>> targeted;
        try
        {
            targeted = FaceGenDiscovery.DiscoverLooseFaceGen(new[] { dir.Path, trailing, dir.Path, upper });
            var counters = DiscoveryMetrics.SnapshotCounters();
            counters["LooseFoldersScanned"].Should().Be(1);
            counters["LooseDuplicateFolderReferences"].Should().Be(1, "the trailing-separator spelling is the same folder");
        }
        finally
        {
            DiscoveryMetrics.Enabled = false;
        }

        // Lookups are by the consumer's own string, so every spelling resolves to the same contents.
        targeted[dir.Path].Should().HaveCount(4);
        targeted[trailing].Should().BeEquivalentTo(targeted[dir.Path]);
        targeted[upper].Should().BeEquivalentTo(targeted[dir.Path]);
        targeted[trailing].Should().NotBeSameAs(targeted[dir.Path], "entries never alias one mutable set");
    }

    [Fact]
    public void SecondDiscovery_SeesFilesAddedAndRemoved_NothingIsCachedAcrossCalls()
    {
        using var dir = new TempDir(nameof(SecondDiscovery_SeesFilesAddedAndRemoved_NothingIsCachedAcrossCalls));
        Touch(dir, $"{Geom}/Skyrim.esm/00013BA1.nif");
        FaceGenDiscovery.DiscoverLooseFaceGen(new[] { dir.Path })[dir.Path].Should().HaveCount(1);

        Touch(dir, $"{Tint}/Skyrim.esm/00013BA1.dds");
        File.Delete(Path.Combine(dir.Path, $"{Geom}/Skyrim.esm/00013BA1.nif"));
        var refreshed = FaceGenDiscovery.DiscoverLooseFaceGen(new[] { dir.Path })[dir.Path];

        refreshed.Should().BeEquivalentTo(new[] { $"{Tint}/Skyrim.esm/00013BA1.dds" });
    }

    [Fact]
    public void MultipleFolders_EachKeepTheirOwnEntries_AndMissingFoldersAreSkipped()
    {
        using var a = new TempDir("multi_a");
        using var b = new TempDir("multi_b");
        Touch(a, $"{Geom}/Skyrim.esm/00013BA1.nif");
        Touch(b, $"{Tint}/Other.esp/00000800.dds");
        var missing = Path.Combine(a.Path, "does-not-exist");

        var targeted = FaceGenDiscovery.DiscoverLooseFaceGen(new[] { a.Path, missing, b.Path });

        targeted.Keys.Should().BeEquivalentTo(new[] { a.Path, b.Path });
        var scan = FaceGenScanner.CreateFaceGenScanResultFromCache(new[] { a.Path, b.Path }, "Mod", targeted, new());
        scan.FaceGenFiles.Keys.Should().BeEquivalentTo(new[] { "Skyrim.esm", "Other.esp" });
    }

    [Fact]
    public void AnyLooseMeshOrTexture_KeepsTheRefreshPathsBroaderQuestion()
    {
        using var withAssets = new TempDir("assets_only");
        Touch(withAssets, "Textures/weapons/sword.dds");
        using var empty = new TempDir("no_assets");
        Touch(empty, "readme.txt");
        Touch(empty, "Meshes/notes.txt");

        FaceGenDiscovery.AnyLooseMeshOrTexture(new[] { withAssets.Path }).Should().BeTrue(
            "a plugin-less mod with only unrelated textures was classified FaceGen-only by refresh before");
        FaceGenDiscovery.AnyLooseMeshOrTexture(new[] { empty.Path }).Should().BeFalse();
        FaceGenDiscovery.AnyLooseMeshOrTexture(new[] { empty.Path, withAssets.Path }).Should().BeTrue();
        // It answers exactly what the original cache's emptiness answered.
        OriginalLooseCache(withAssets.Path).Any().Should().BeTrue();
        OriginalLooseCache(empty.Path).Any().Should().BeFalse();
    }

    [Fact]
    public void PlainDataScannerOverload_MatchesTheViewModelOverload()
    {
        using var dir = new TempDir(nameof(PlainDataScannerOverload_MatchesTheViewModelOverload));
        SeedMixedMod(dir);
        var loose = FaceGenDiscovery.DiscoverLooseFaceGen(new[] { dir.Path });
        var bsa = new Dictionary<string, HashSet<string>>
        {
            ["My Mod"] = new() { "meshes/actors/character/facegendata/facegeom/archived.esp/00000801.nif" },
        };
        var vm = Reflect.Uninitialized<VM_ModSetting>();
        Reflect.SetField(vm, "<CorrespondingFolderPaths>k__BackingField", new ObservableCollection<string>(new[] { dir.Path }));
        Reflect.SetField(vm, "<DisplayName>k__BackingField", "My Mod");

        var viaVm = FaceGenScanner.CreateFaceGenScanResultFromCache(vm, loose, bsa);
        var viaData = FaceGenScanner.CreateFaceGenScanResultFromCache(new[] { dir.Path }, "My Mod", loose, bsa);

        viaData.FaceGenFiles.Should().BeEquivalentTo(viaVm.FaceGenFiles);
        viaData.FaceGenFiles.Should().ContainKey("archived.esp");
        (viaData.MeshesExist, viaData.TexturesExist).Should().Be((viaVm.MeshesExist, viaVm.TexturesExist));
    }
}
