using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// Archive FaceGen discovery (<see cref="FaceGenDiscovery.GetArchiveFaceGenPaths"/> and its folder-metadata
/// probe). The contract: the result always equals what enumerating every file of the archive through Mutagen
/// yields (the original implementation, <see cref="FullEnumeration"/>); the detailed reader is consulted only
/// for archives the probe proves contain FaceGen folders, and every archive whose metadata cannot be
/// validated goes through the full reader. Archives are real BSAs written by <see cref="BsaFixtureWriter"/>.
/// </summary>
public class FaceGenDiscoveryArchiveTests
{
    private const string Geom = @"meshes\actors\character\facegendata\facegeom";
    private const string Tint = @"textures\actors\character\facegendata\facetint";

    private static readonly string[] FaceGenEntries =
    {
        $@"{Geom}\Skyrim.esm\00013BA1.nif",
        $@"{Geom}\Skyrim.esm\0001A696.nif",
        $@"{Geom}\MyMod.esp\00000D62.nif",
        $@"{Tint}\Skyrim.esm\00013BA1.dds",
    };

    private static IEnumerable<string> UnrelatedEntries(int count = 60) =>
        Enumerable.Range(0, count).SelectMany(i => new[]
        {
            $@"meshes\armor\set{i}\cuirass.nif",
            $@"textures\armor\set{i}\cuirass.dds",
        }).Append(@"meshes\actors\character\character assets\hair.nif");

    private static IArchiveReader Open(string path) => Archive.CreateReader(GameRelease.SkyrimSE, path);

    /// <summary>The original per-archive cache: every file path, lower-cased, '/', under facegendata.</summary>
    private static HashSet<string> FullEnumeration(string path)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Open(path).Files)
        {
            var p = file.Path.ToLowerInvariant().Replace('\\', '/');
            if (p.StartsWith("meshes/actors/character/facegendata/") ||
                p.StartsWith("textures/actors/character/facegendata/"))
                result.Add(p);
        }
        return result;
    }

    /// <summary>Wraps a real reader and records how discovery used it.</summary>
    private sealed class SpyReader : IArchiveReader
    {
        private readonly IArchiveReader _inner;
        public Func<string, bool>? RefuseFolder { get; init; }
        public int FilesEnumerations { get; private set; }
        public List<string> FoldersRequested { get; } = new();

        public SpyReader(IArchiveReader inner) => _inner = inner;

        public bool TryGetFolder(string path, [MaybeNullWhen(false)] out IArchiveFolder folder)
        {
            FoldersRequested.Add(path);
            if (RefuseFolder?.Invoke(path) == true)
            {
                folder = null;
                return false;
            }
            return _inner.TryGetFolder(path, out folder);
        }

        public IEnumerable<IArchiveFile> Files
        {
            get
            {
                FilesEnumerations++;
                return _inner.Files;
            }
        }
    }

    private static (HashSet<string> Paths, List<SpyReader> Opened) Discover(string path, Func<IArchiveReader, SpyReader>? wrap = null)
    {
        var opened = new List<SpyReader>();
        var paths = FaceGenDiscovery.GetArchiveFaceGenPaths(path, p =>
        {
            var spy = wrap?.Invoke(Open(p)) ?? new SpyReader(Open(p));
            opened.Add(spy);
            return spy;
        });
        return (paths, opened);
    }

    [Fact]
    public void FixtureWriter_ProducesArchivesMutagenReads()
    {
        using var dir = new TempDir(nameof(FixtureWriter_ProducesArchivesMutagenReads));
        var bsa = dir.Combine("Fixture.bsa");
        BsaFixtureWriter.WriteFiles(bsa, FaceGenEntries.Concat(UnrelatedEntries(3)));

        var files = Open(bsa).Files.ToList();
        files.Select(f => f.Path).Should().BeEquivalentTo(FaceGenEntries.Concat(UnrelatedEntries(3)));
        Encoding.ASCII.GetString(files.First(f => f.Path.EndsWith("0001A696.nif")).GetBytes())
            .Should().Be($@"data:{Geom}\Skyrim.esm\0001A696.nif");
    }

    [Fact]
    public void ArchiveWithFaceGen_ReadsOnlyItsFaceGenFolders_AndMatchesFullEnumeration()
    {
        using var dir = new TempDir(nameof(ArchiveWithFaceGen_ReadsOnlyItsFaceGenFolders_AndMatchesFullEnumeration));
        var bsa = dir.Combine("Mixed.bsa");
        BsaFixtureWriter.WriteFiles(bsa, UnrelatedEntries().Concat(FaceGenEntries));

        var probe = FaceGenDiscovery.ProbeArchiveFolders(bsa);
        probe.Outcome.Should().Be(ArchiveProbeOutcome.Present);
        probe.FaceGenFolders.Should().BeEquivalentTo(new[] { $@"{Geom}\Skyrim.esm", $@"{Geom}\MyMod.esp", $@"{Tint}\Skyrim.esm" });

        var (paths, opened) = Discover(bsa);
        paths.Should().BeEquivalentTo(FullEnumeration(bsa));
        paths.Should().HaveCount(4);
        opened.Should().ContainSingle();
        opened[0].FilesEnumerations.Should().Be(0, "only the FaceGen folders' file lists are read");
        opened[0].FoldersRequested.Should().BeEquivalentTo(probe.FaceGenFolders);
    }

    [Fact]
    public void ArchiveWithOnlyUnrelatedAssets_IsAbsent_WithoutOpeningTheDetailedReader()
    {
        using var dir = new TempDir(nameof(ArchiveWithOnlyUnrelatedAssets_IsAbsent_WithoutOpeningTheDetailedReader));
        var bsa = dir.Combine("Armor.bsa");
        BsaFixtureWriter.WriteFiles(bsa, UnrelatedEntries());

        FaceGenDiscovery.ProbeArchiveFolders(bsa).Outcome.Should().Be(ArchiveProbeOutcome.Absent);
        var (paths, opened) = Discover(bsa);
        paths.Should().BeEmpty();
        FullEnumeration(bsa).Should().BeEmpty();
        opened.Should().BeEmpty("a proven-absent archive is never opened for detail");
    }

    [Fact]
    public void EveryFolderUnderFaceGenData_IsKept_LikeTheOriginalPrefixRule()
    {
        // The original archive cache kept everything under facegendata, not just facegeom/facetint (consumers
        // filter further). Files directly in facegendata and in sibling folders must survive unchanged.
        using var dir = new TempDir(nameof(EveryFolderUnderFaceGenData_IsKept_LikeTheOriginalPrefixRule));
        var bsa = dir.Combine("Odd.bsa");
        BsaFixtureWriter.WriteFiles(bsa, new[]
        {
            @"meshes\actors\character\facegendata\readme.nif",
            @"meshes\actors\character\facegendata\facegeom_old\Skyrim.esm\00013BA1.nif",
            @"textures\actors\character\facegendata\facetint\Skyrim.esm\00013BA1.tga",
            @"meshes\actors\character\facegendatax\Skyrim.esm\00013BA1.nif",
            @"meshes\actors\character\character assets\hair.nif",
        });

        var (paths, _) = Discover(bsa);
        paths.Should().BeEquivalentTo(FullEnumeration(bsa));
        paths.Should().HaveCount(3).And.NotContain(p => p.Contains("facegendatax"));
    }

    [Fact]
    public void SkyrimLeArchive_IsProbedConclusively()
    {
        using var dir = new TempDir(nameof(SkyrimLeArchive_IsProbedConclusively));
        var bsa = dir.Combine("Le.bsa");
        BsaFixtureWriter.WriteFiles(bsa, UnrelatedEntries(5).Concat(FaceGenEntries), new() { Version = 104 });

        FaceGenDiscovery.ProbeArchiveFolders(bsa).Outcome.Should().Be(ArchiveProbeOutcome.Present);
        var (paths, opened) = Discover(bsa);
        paths.Should().BeEquivalentTo(FullEnumeration(bsa));
        opened.Single().FilesEnumerations.Should().Be(0);
    }

    [Fact]
    public void NonAsciiFolderNames_DecodeAsMutagenDoes()
    {
        using var dir = new TempDir(nameof(NonAsciiFolderNames_DecodeAsMutagenDoes));
        var bsa = dir.Combine("Accents.bsa");
        BsaFixtureWriter.WriteFiles(bsa, new[] { $@"{Geom}\Café Société.esp\00000801.nif", @"meshes\x\y.nif" });

        var (paths, opened) = Discover(bsa);
        paths.Should().BeEquivalentTo(FullEnumeration(bsa)).And.ContainSingle(p => p.Contains("café société.esp"));
        opened.Single().FilesEnumerations.Should().Be(0, "Mutagen found the folder by the probe's decoded name");
    }

    public static IEnumerable<object[]> UnusualMetadata() => new[]
    {
        new object[] { "unsupported version", new BsaFixtureWriter.Options { Version = 103 } },
        new object[] { "no folder names", new BsaFixtureWriter.Options { FolderNames = false } },
        new object[] { "no file names", new BsaFixtureWriter.Options { FileNames = false } },
        new object[] { "folder-name total disagrees", new BsaFixtureWriter.Options { FolderNameLengthSkew = 3 } },
    };

    [Theory]
    [MemberData(nameof(UnusualMetadata))]
    public void UnusualMetadata_IsInconclusive_AndFallsBackToTheFullReader(string label, BsaFixtureWriter.Options options)
    {
        using var dir = new TempDir("unusual_" + label.Replace(' ', '_'));
        var bsa = dir.Combine("Unusual.bsa");
        BsaFixtureWriter.WriteFiles(bsa, UnrelatedEntries(5).Concat(FaceGenEntries), options);

        FaceGenDiscovery.ProbeArchiveFolders(bsa).Outcome.Should().Be(ArchiveProbeOutcome.Inconclusive, label);

        // Whatever the full reader makes of the archive is the answer — including failing the same way.
        HashSet<string>? expected = null;
        Exception? expectedError = null;
        try { expected = FullEnumeration(bsa); } catch (Exception ex) { expectedError = ex; }

        if (expectedError != null)
        {
            var act = () => Discover(bsa);
            act.Should().Throw<Exception>().Which.GetType().Should().Be(expectedError.GetType());
            return;
        }
        var (paths, opened) = Discover(bsa);
        paths.Should().BeEquivalentTo(expected!);
        opened.Single().FilesEnumerations.Should().Be(1, "the inconclusive probe hands over to the full enumeration");
    }

    [Fact]
    public void DuplicateFolderNames_AreInconclusive_AndResolvedByTheFullReader()
    {
        using var dir = new TempDir(nameof(DuplicateFolderNames_AreInconclusive_AndResolvedByTheFullReader));
        var bsa = dir.Combine("Dupes.bsa");
        byte[] D(string s) => Encoding.ASCII.GetBytes(s);
        BsaFixtureWriter.Write(bsa, new List<(string, IReadOnlyList<(string, byte[])>)>
        {
            ($@"{Geom}\Skyrim.esm", new List<(string, byte[])> { ("00013BA1.nif", D("a")) }),
            ($@"{Geom}\SKYRIM.ESM", new List<(string, byte[])> { ("0001A696.nif", D("b")) }),
        });

        FaceGenDiscovery.ProbeArchiveFolders(bsa).Outcome.Should().Be(ArchiveProbeOutcome.Inconclusive);
        var (paths, opened) = Discover(bsa);
        paths.Should().BeEquivalentTo(FullEnumeration(bsa)).And.HaveCount(2);
        opened.Single().FilesEnumerations.Should().Be(1);
    }

    [Theory]
    [InlineData("not a bsa")]
    [InlineData("truncated")]
    [InlineData("empty")]
    public void UnreadableArchives_AreInconclusive_AndKeepTheFullReadersOutcome(string kind)
    {
        using var dir = new TempDir("unreadable_" + kind.Replace(' ', '_'));
        var bsa = dir.Combine("Broken.bsa");
        BsaFixtureWriter.WriteFiles(bsa, UnrelatedEntries(10).Concat(FaceGenEntries));
        var bytes = File.ReadAllBytes(bsa);
        File.WriteAllBytes(bsa, kind switch
        {
            "not a bsa" => Encoding.ASCII.GetBytes("PK\u0003\u0004 definitely a zip"),
            "truncated" => bytes[..60],
            _ => Array.Empty<byte>(),
        });

        FaceGenDiscovery.ProbeArchiveFolders(bsa).Outcome.Should().Be(ArchiveProbeOutcome.Inconclusive);
        Exception? expectedError = null;
        HashSet<string>? expected = null;
        try { expected = FullEnumeration(bsa); } catch (Exception ex) { expectedError = ex; }

        var act = () => Discover(bsa).Paths;
        if (expectedError != null)
            act.Should().Throw<Exception>("an archive the full reader cannot read still surfaces as an error");
        else
            act().Should().BeEquivalentTo(expected!);
    }

    [Fact]
    public void ProbedFolderMutagenCannotAddress_FallsBackToTheFullReader()
    {
        using var dir = new TempDir(nameof(ProbedFolderMutagenCannotAddress_FallsBackToTheFullReader));
        var bsa = dir.Combine("Refusing.bsa");
        BsaFixtureWriter.WriteFiles(bsa, UnrelatedEntries(5).Concat(FaceGenEntries));

        var (paths, opened) = Discover(bsa, r => new SpyReader(r) { RefuseFolder = f => f.EndsWith("MyMod.esp") });
        paths.Should().BeEquivalentTo(FullEnumeration(bsa));
        opened.Single().FilesEnumerations.Should().Be(1);
    }

    [Fact]
    public void ChangedArchive_IsReadAgain_UnchangedArchive_IsServedFromTheListingCache()
    {
        using var dir = new TempDir(nameof(ChangedArchive_IsReadAgain_UnchangedArchive_IsServedFromTheListingCache));
        var bsa = dir.Combine("Changing.bsa");
        BsaFixtureWriter.WriteFiles(bsa, UnrelatedEntries(5));
        Discover(bsa).Paths.Should().BeEmpty();

        // Unchanged (same size and timestamp): no probe, no reader.
        var again = Discover(bsa);
        again.Paths.Should().BeEmpty();

        // Replaced by an archive that now ships FaceGen.
        BsaFixtureWriter.WriteFiles(bsa, UnrelatedEntries(5).Concat(FaceGenEntries));
        File.SetLastWriteTimeUtc(bsa, DateTime.UtcNow.AddMinutes(1));
        var changed = Discover(bsa);
        changed.Paths.Should().BeEquivalentTo(FullEnumeration(bsa)).And.HaveCount(4);
        changed.Opened.Should().ContainSingle();

        // Same size, different content, new timestamp: still re-read (size alone does not hide a change).
        var sameSize = FaceGenEntries.Select(p => p.Replace("00013BA1", "00013BA2")).ToArray();
        BsaFixtureWriter.WriteFiles(bsa, UnrelatedEntries(5).Concat(sameSize));
        File.SetLastWriteTimeUtc(bsa, DateTime.UtcNow.AddMinutes(2));
        Discover(bsa).Paths.Should().Contain(p => p.Contains("00013ba2"));
    }

    [Fact]
    public void UnchangedArchive_SecondDiscovery_DoesNotOpenAReader()
    {
        using var dir = new TempDir(nameof(UnchangedArchive_SecondDiscovery_DoesNotOpenAReader));
        var bsa = dir.Combine("Stable.bsa");
        BsaFixtureWriter.WriteFiles(bsa, UnrelatedEntries(5).Concat(FaceGenEntries));
        var first = Discover(bsa);
        first.Opened.Should().ContainSingle();

        var second = Discover(bsa);
        second.Paths.Should().BeEquivalentTo(first.Paths);
        second.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task NonFaceGenAssets_StayAvailableToRenderingAndPatching_AfterDiscovery()
    {
        // Discovery answers FaceGen questions only; BsaHandler's full asset index (used to resolve every
        // other mesh/texture for rendering and patching) and its shared, reference-counted readers must be
        // untouched by it.
        using var dir = new TempDir(nameof(NonFaceGenAssets_StayAvailableToRenderingAndPatching_AfterDiscovery));
        dir.WriteText("ResourceOnly.esp", "");
        var bsa = dir.Combine("ResourceOnly.bsa");
        BsaFixtureWriter.WriteFiles(bsa, UnrelatedEntries(5).Concat(FaceGenEntries));
        var handler = new BsaHandler(NpcChooserTestEnvironment.Invalid());

        // A long-lived consumer (the renderer adapter) holds a cached, ref-counted reader.
        var held = handler.OpenBsaArchiveReaders(new[] { bsa }, GameRelease.SkyrimSE, cacheReaders: true)[bsa];

        // Discovery as production runs it: uncached open, which borrows the shared reader without a ref.
        var paths = FaceGenDiscovery.GetArchiveFaceGenPaths(bsa, p =>
            handler.OpenBsaArchiveReaders(new[] { p }, GameRelease.SkyrimSE, false).TryGetValue(p, out var r) ? r : null);
        paths.Should().HaveCount(4);

        handler.TryGetFileFromSingleReader(@"textures\armor\set3\cuirass.dds", held, out var file).Should().BeTrue();
        Encoding.ASCII.GetString(file!.GetBytes()).Should().Be(@"data:textures\armor\set3\cuirass.dds");

        var modKey = ModKey.FromFileName("ResourceOnly.esp");
        await handler.PopulateBsaContentPathsAsync(new[]
        {
            new ModSetting { DisplayName = "Resource Mod", CorrespondingModKeys = new() { modKey },
                CorrespondingFolderPaths = new() { dir.Path } },
        }, GameRelease.SkyrimSE);
        handler.FileExists(@"meshes\armor\set1\cuirass.nif", modKey, out var foundIn).Should().BeTrue();
        foundIn.Should().Be(bsa);

        handler.ReleaseReaders(new[] { bsa });
        handler.GetStatusReport().Should().NotContain(bsa, "the one ref discovery never took is the only one to release");
    }
}
