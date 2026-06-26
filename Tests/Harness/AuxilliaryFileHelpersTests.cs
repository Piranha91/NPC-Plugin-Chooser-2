using System.IO;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// File-touching static helpers on <see cref="Auxilliary"/>, exercised against real temp files
/// via <see cref="TempDir"/>. All of these are <c>public static</c>, so they are called directly
/// (no reflection): cheap file-equality identifiers + fast comparison (xxHash128),
/// <c>ParseMetaIni</c>, <c>FindExistingCachedImage</c>, and <c>CreateDirectoryIfNeeded</c>.
///
/// Determinism: every byte read here comes from a freshly-written temp file under the OS temp
/// dir — no game install, clock, or network is involved.
/// </summary>
public class AuxilliaryFileHelpersTests
{
    // ── GetCheapFileEqualityIdentifiers ───────────────────────────────────────

    [Fact]
    public void GetCheapFileEqualityIdentifiers_Null_ThrowsArgumentNull()
    {
        Action act = () => Auxilliary.GetCheapFileEqualityIdentifiers(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetCheapFileEqualityIdentifiers_Missing_ThrowsFileNotFound()
    {
        using var tmp = new TempDir();
        var missing = Path.Combine(tmp.Path, "nope", "absent.bin");
        Action act = () => Auxilliary.GetCheapFileEqualityIdentifiers(missing);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void GetCheapFileEqualityIdentifiers_ReturnsLengthAndUppercaseHexHash()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteText("a.txt", "hello world");

        var (length, hash) = Auxilliary.GetCheapFileEqualityIdentifiers(path);

        length.Should().Be((int)new FileInfo(path).Length);
        length.Should().Be(11); // "hello world" is 11 ASCII bytes
        // Convert.ToHexString emits 32 uppercase hex chars for a 128-bit (16-byte) digest.
        hash.Should().HaveLength(32);
        hash.Should().MatchRegex("^[0-9A-F]+$");
    }

    [Fact]
    public void GetCheapFileEqualityIdentifiers_IdenticalContent_SameHash()
    {
        using var tmp = new TempDir();
        var p1 = tmp.WriteText("one.txt", "the quick brown fox");
        var p2 = tmp.WriteText("two.txt", "the quick brown fox");

        var first = Auxilliary.GetCheapFileEqualityIdentifiers(p1);
        var second = Auxilliary.GetCheapFileEqualityIdentifiers(p2);

        second.Length.Should().Be(first.Length);
        second.CheapHash.Should().Be(first.CheapHash);
    }

    [Fact]
    public void GetCheapFileEqualityIdentifiers_DifferentContent_DifferentHash()
    {
        using var tmp = new TempDir();
        var p1 = tmp.WriteText("one.txt", "alpha");
        var p2 = tmp.WriteText("two.txt", "bravo"); // same length, different bytes

        var first = Auxilliary.GetCheapFileEqualityIdentifiers(p1);
        var second = Auxilliary.GetCheapFileEqualityIdentifiers(p2);

        first.Length.Should().Be(second.Length); // both 5 bytes
        second.CheapHash.Should().NotBe(first.CheapHash);
    }

    [Fact]
    public void GetCheapFileEqualityIdentifiers_EmptyFile_HasZeroLength()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteText("empty.bin", "");

        var (length, hash) = Auxilliary.GetCheapFileEqualityIdentifiers(path);

        length.Should().Be(0);
        hash.Should().HaveLength(32); // xxHash128 of empty input is still a 16-byte digest
    }

    // ── FastFilesAreIdentical ─────────────────────────────────────────────────

    [Fact]
    public void FastFilesAreIdentical_NullCandidatePath_ThrowsArgumentNull()
    {
        Action act = () => Auxilliary.FastFilesAreIdentical(null!, 0, "00");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FastFilesAreIdentical_NullTargetHash_ThrowsArgumentNull()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteText("x.txt", "abc");
        Action act = () => Auxilliary.FastFilesAreIdentical(path, 3, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FastFilesAreIdentical_MissingCandidate_ReturnsFalseNotThrow()
    {
        using var tmp = new TempDir();
        var missing = Path.Combine(tmp.Path, "ghost.bin");
        // Missing candidate is a benign "not identical", not an exception.
        Auxilliary.FastFilesAreIdentical(missing, 5, "DEADBEEF").Should().BeFalse();
    }

    [Fact]
    public void FastFilesAreIdentical_IdenticalFile_ReturnsTrue()
    {
        using var tmp = new TempDir();
        var target = tmp.WriteText("target.txt", "identical payload");
        var candidate = tmp.WriteText("candidate.txt", "identical payload");

        var (len, hash) = Auxilliary.GetCheapFileEqualityIdentifiers(target);
        Auxilliary.FastFilesAreIdentical(candidate, len, hash).Should().BeTrue();
    }

    [Fact]
    public void FastFilesAreIdentical_LengthMismatch_ReturnsFalseWithoutHashing()
    {
        using var tmp = new TempDir();
        var candidate = tmp.WriteText("candidate.txt", "short");

        // Declare a different target length; the early size check must reject it.
        // The supplied hash is irrelevant because the length gate fires first.
        Auxilliary.FastFilesAreIdentical(candidate, targetFileLength: 999, targetFileCheapHash: "IRRELEVANT")
            .Should().BeFalse();
    }

    [Fact]
    public void FastFilesAreIdentical_SameLengthDifferentContent_ReturnsFalse()
    {
        using var tmp = new TempDir();
        var target = tmp.WriteText("target.txt", "alpha");
        var candidate = tmp.WriteText("candidate.txt", "bravo"); // same 5-byte length

        var (len, hash) = Auxilliary.GetCheapFileEqualityIdentifiers(target);
        Auxilliary.FastFilesAreIdentical(candidate, len, hash).Should().BeFalse();
    }

    [Fact]
    public void FastFilesAreIdentical_HashComparisonIsCaseInsensitive()
    {
        using var tmp = new TempDir();
        var target = tmp.WriteText("target.txt", "case insensitive hash");
        var candidate = tmp.WriteText("candidate.txt", "case insensitive hash");

        var (len, hash) = Auxilliary.GetCheapFileEqualityIdentifiers(target);
        // The production hash is uppercase hex; pass a lowercased copy to prove the
        // comparison uses OrdinalIgnoreCase.
        var lowerHash = hash.ToLowerInvariant();
        lowerHash.Should().NotBe(hash, "the digest must contain at least one hex letter for this to be meaningful");

        Auxilliary.FastFilesAreIdentical(candidate, len, lowerHash).Should().BeTrue();
    }

    // ── ParseMetaIni ──────────────────────────────────────────────────────────

    [Fact]
    public void ParseMetaIni_MapsSkyrimSeGameNameToNexusDomain()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteText("meta.ini",
            "[General]\r\ngameName=SkyrimSE\r\nmodid=12345\r\n");

        var (gameName, modId) = Auxilliary.ParseMetaIni(path);

        gameName.Should().Be("skyrimspecialedition");
        modId.Should().Be("12345");
    }

    [Theory]
    [InlineData("SkyrimSE")]
    [InlineData("skyrimse")]
    [InlineData("SKYRIMSE")]
    public void ParseMetaIni_SkyrimSeMappingIsCaseInsensitiveOnValue(string value)
    {
        using var tmp = new TempDir();
        var path = tmp.WriteText("meta.ini", $"gameName={value}\n");

        Auxilliary.ParseMetaIni(path).gameName.Should().Be("skyrimspecialedition");
    }

    [Fact]
    public void ParseMetaIni_NonSkyrimSeGameNamePassesThroughTrimmed()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteText("meta.ini", "gameName=  Fallout4  \nmodid=  77  \n");

        var (gameName, modId) = Auxilliary.ParseMetaIni(path);

        gameName.Should().Be("Fallout4"); // trimmed, not remapped
        modId.Should().Be("77");          // trimmed
    }

    [Fact]
    public void ParseMetaIni_KeysAreCaseInsensitive()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteText("meta.ini", "GameName=Oblivion\r\nModID=42\r\n");

        var (gameName, modId) = Auxilliary.ParseMetaIni(path);

        gameName.Should().Be("Oblivion");
        modId.Should().Be("42");
    }

    [Fact]
    public void ParseMetaIni_MissingKeys_ReturnNulls()
    {
        using var tmp = new TempDir();
        // No gameName / modid lines at all.
        var path = tmp.WriteText("meta.ini", "[General]\r\nversion=1.0\r\ninstalled=true\r\n");

        var (gameName, modId) = Auxilliary.ParseMetaIni(path);

        gameName.Should().BeNull();
        modId.Should().BeNull();
    }

    [Fact]
    public void ParseMetaIni_MissingFile_ReturnsNullsNotThrow()
    {
        using var tmp = new TempDir();
        var missing = Path.Combine(tmp.Path, "does-not-exist.ini");

        var (gameName, modId) = Auxilliary.ParseMetaIni(missing);

        gameName.Should().BeNull();
        modId.Should().BeNull();
    }

    [Fact]
    public void ParseMetaIni_StopsAfterBothFound_IgnoringLaterDuplicates()
    {
        using var tmp = new TempDir();
        // The loop breaks once both values are set, so the later overriding lines are ignored.
        var path = tmp.WriteText("meta.ini",
            "gameName=SkyrimSE\nmodid=100\ngameName=Fallout4\nmodid=999\n");

        var (gameName, modId) = Auxilliary.ParseMetaIni(path);

        gameName.Should().Be("skyrimspecialedition");
        modId.Should().Be("100");
    }

    // ── FindExistingCachedImage ───────────────────────────────────────────────

    [Fact]
    public void FindExistingCachedImage_NoneExist_ReturnsNull()
    {
        using var tmp = new TempDir();
        var basePath = Path.Combine(tmp.Path, "portrait");
        Auxilliary.FindExistingCachedImage(basePath).Should().BeNull();
    }

    [Fact]
    public void FindExistingCachedImage_SingleMatch_ReturnsFullPathWithExtension()
    {
        using var tmp = new TempDir();
        var basePath = Path.Combine(tmp.Path, "portrait");
        var png = tmp.WriteText("portrait.png", "fake-png");

        Auxilliary.FindExistingCachedImage(basePath).Should().Be(png);
    }

    [Fact]
    public void FindExistingCachedImage_WebpWinsOverPng()
    {
        using var tmp = new TempDir();
        var basePath = Path.Combine(tmp.Path, "portrait");
        var webp = tmp.WriteText("portrait.webp", "fake-webp");
        tmp.WriteText("portrait.png", "fake-png");

        // .webp has the highest priority in the extension list.
        Auxilliary.FindExistingCachedImage(basePath).Should().Be(webp);
    }

    [Fact]
    public void FindExistingCachedImage_PngWinsOverJpg()
    {
        using var tmp = new TempDir();
        var basePath = Path.Combine(tmp.Path, "portrait");
        var png = tmp.WriteText("portrait.png", "fake-png");
        tmp.WriteText("portrait.jpg", "fake-jpg");

        Auxilliary.FindExistingCachedImage(basePath).Should().Be(png);
    }

    [Fact]
    public void FindExistingCachedImage_JpgWinsOverJpeg()
    {
        using var tmp = new TempDir();
        var basePath = Path.Combine(tmp.Path, "portrait");
        var jpg = tmp.WriteText("portrait.jpg", "fake-jpg");
        tmp.WriteText("portrait.jpeg", "fake-jpeg");

        Auxilliary.FindExistingCachedImage(basePath).Should().Be(jpg);
    }

    [Fact]
    public void FindExistingCachedImage_JpegOnly_IsFoundLast()
    {
        using var tmp = new TempDir();
        var basePath = Path.Combine(tmp.Path, "portrait");
        var jpeg = tmp.WriteText("portrait.jpeg", "fake-jpeg");

        Auxilliary.FindExistingCachedImage(basePath).Should().Be(jpeg);
    }

    [Fact]
    public void FindExistingCachedImage_FullPriorityOrderWebpFirst()
    {
        using var tmp = new TempDir();
        var basePath = Path.Combine(tmp.Path, "portrait");
        var webp = tmp.WriteText("portrait.webp", "w");
        tmp.WriteText("portrait.png", "p");
        tmp.WriteText("portrait.jpg", "j");
        tmp.WriteText("portrait.jpeg", "e");

        // All four present -> .webp wins.
        Auxilliary.FindExistingCachedImage(basePath).Should().Be(webp);
    }

    // ── CreateDirectoryIfNeeded ───────────────────────────────────────────────

    [Fact]
    public void CreateDirectoryIfNeeded_DirectoryType_CreatesDirAndReturnsDirectoryInfo()
    {
        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "nested", "deeper");

        Directory.Exists(target).Should().BeFalse();

        var result = Auxilliary.CreateDirectoryIfNeeded(target, Auxilliary.PathType.Directory);

        Directory.Exists(target).Should().BeTrue();
        ((object)result).Should().BeOfType<DirectoryInfo>();
        ((DirectoryInfo)result).FullName.Should().Be(new DirectoryInfo(target).FullName);
    }

    [Fact]
    public void CreateDirectoryIfNeeded_FileType_CreatesParentDirOnlyAndReturnsFileInfo()
    {
        using var tmp = new TempDir();
        var filePath = Path.Combine(tmp.Path, "made", "up", "image.dds");
        var parentDir = Path.GetDirectoryName(filePath)!;

        Directory.Exists(parentDir).Should().BeFalse();

        var result = Auxilliary.CreateDirectoryIfNeeded(filePath, Auxilliary.PathType.File);

        // The parent directory is created; the file itself is NOT.
        Directory.Exists(parentDir).Should().BeTrue();
        File.Exists(filePath).Should().BeFalse();
        ((object)result).Should().BeOfType<FileInfo>();
        ((FileInfo)result).FullName.Should().Be(new FileInfo(filePath).FullName);
    }

    [Fact]
    public void CreateDirectoryIfNeeded_Directory_IsIdempotent()
    {
        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "repeat");

        Auxilliary.CreateDirectoryIfNeeded(target, Auxilliary.PathType.Directory);
        // A second call on an existing directory is a no-op and must not throw.
        Action again = () => Auxilliary.CreateDirectoryIfNeeded(target, Auxilliary.PathType.Directory);

        again.Should().NotThrow();
        Directory.Exists(target).Should().BeTrue();
    }

    [Fact]
    public void CreateDirectoryIfNeeded_File_ExistingParent_IsIdempotent()
    {
        using var tmp = new TempDir();
        var parent = tmp.Dir("already-there");
        var filePath = Path.Combine(parent, "file.nif");

        Action act = () => Auxilliary.CreateDirectoryIfNeeded(filePath, Auxilliary.PathType.File);

        act.Should().NotThrow();
        Directory.Exists(parent).Should().BeTrue();
        File.Exists(filePath).Should().BeFalse();
    }
}
