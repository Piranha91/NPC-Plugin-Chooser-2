using System.IO;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pure static string/path helpers on <see cref="Auxilliary"/>: mojibake repair,
/// file-name sanitisation, FaceGen sub-path formatting, path regularisation,
/// type-folder prefixing, folder normalisation/root containment, and the
/// plugin-extension set. All of these are deterministic, env-free, and disk-free
/// (the few that touch <see cref="Path"/> only do string math, not the filesystem),
/// so they run offline with no game installed.
///
/// NOTE: env/disk-dependent members of Auxilliary (FormKeyToFormIDString,
/// TryFormKeyToFormIDString, GetModKeysInDirectory, IsValidAppearanceRace,
/// TemplateChainTerminatesInLeveledNpc, the asset-link collectors, the
/// race-cache load/save, OpenFolder/OpenUrl, ParseMetaIni, FindExistingCachedImage,
/// and the file-hash helpers) are not covered here: they require a live link cache /
/// environment, a process shell, or real files outside the deterministic string surface.
/// </summary>
public class AuxilliaryStringPathTests
{
    // ---------------------------------------------------------------------
    // FixMojibake
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FixMojibake_NullOrEmpty_ReturnsInputUnchanged(string? input)
    {
        // Null/empty short-circuits before any conversion is attempted.
        Auxilliary.FixMojibake(input!).Should().Be(input);
    }

    [Fact]
    public void FixMojibake_PlainAscii_IsUnchanged()
    {
        // ASCII round-trips identically through Windows-1252 -> UTF-8, so the
        // "candidate != input" guard keeps the original.
        Auxilliary.FixMojibake("Lydia").Should().Be("Lydia");
    }

    [Fact]
    public void FixMojibake_RepairsClassicLatin1Mojibake()
    {
        // "é" encoded as UTF-8 (0xC3 0xA9) then misread as Windows-1252 yields "Ã©".
        // Re-encoding "Ã©" as Windows-1252 gives back [0xC3,0xA9], decoded as UTF-8 -> "é".
        Auxilliary.FixMojibake("Ã©").Should().Be("é");
    }

    [Fact]
    public void FixMojibake_AlreadyDecodedNonLatinScript_IsLeftAlone()
    {
        // Contains CJK (already decoded correctly) -> early return, no round-trip.
        const string cjk = "ドラゴン";
        Auxilliary.FixMojibake(cjk).Should().Be(cjk);
    }

    [Fact]
    public void FixMojibake_CyrillicText_IsLeftAlone()
    {
        const string cyr = "Драугр";
        Auxilliary.FixMojibake(cyr).Should().Be(cyr);
    }

    // ---------------------------------------------------------------------
    // MakeStringPathSafe
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MakeStringPathSafe_NullOrWhitespace_ReturnsEmpty(string? input)
    {
        Auxilliary.MakeStringPathSafe(input!).Should().BeEmpty();
    }

    [Fact]
    public void MakeStringPathSafe_CleanName_IsUnchanged()
    {
        Auxilliary.MakeStringPathSafe("Bijin Warmaidens").Should().Be("Bijin Warmaidens");
    }

    [Fact]
    public void MakeStringPathSafe_ReplacesInvalidFileNameCharsWithUnderscore()
    {
        // ':' and '"' and '|' are invalid file-name chars on Windows. Each one
        // becomes a single underscore; valid chars are preserved verbatim.
        var result = Auxilliary.MakeStringPathSafe("a:b\"c|d");
        result.Should().Be("a_b_c_d");
    }

    [Fact]
    public void MakeStringPathSafe_PreservesLengthOneToOnePerChar()
    {
        // Replacement is char-for-char, so output length matches input length.
        const string input = "x?y*z";
        Auxilliary.MakeStringPathSafe(input).Length.Should().Be(input.Length);
    }

    // ---------------------------------------------------------------------
    // GetFaceGenSubPathStrings
    // ---------------------------------------------------------------------

    [Fact]
    public void GetFaceGenSubPathStrings_FormatsEightHexLowercasePaths()
    {
        var fk = FormKey.Factory("01A696:Skyrim.esm");
        var (mesh, tex) = Auxilliary.GetFaceGenSubPathStrings(fk);

        mesh.Should().Be(@"actors\character\facegendata\facegeom\skyrim.esm\0001a696.nif");
        tex.Should().Be(@"actors\character\facegendata\facetint\skyrim.esm\0001a696.dds");
    }

    [Fact]
    public void GetFaceGenSubPathStrings_ZeroPadsAndLowercasesId()
    {
        // Small ID must be zero-padded to 8 hex digits and the whole path lowercased.
        var fk = FormKey.Factory("000007:Dawnguard.esm");
        var (mesh, tex) = Auxilliary.GetFaceGenSubPathStrings(fk);

        mesh.Should().Be(@"actors\character\facegendata\facegeom\dawnguard.esm\00000007.nif");
        tex.Should().Be(@"actors\character\facegendata\facetint\dawnguard.esm\00000007.dds");
    }

    [Fact]
    public void GetFaceGenSubPathStrings_Regularized_StripsKnownTypeFoldersStillLowercase()
    {
        // With regularized=true the paths are passed through TryRegularizePath.
        // The input already starts with "actors\..." (no "data" prefix, .nif/.dds
        // extension) so the type folder is inferred and prepended (meshes\ / textures\),
        // and the final result is lowercased.
        var fk = FormKey.Factory("01A696:Skyrim.esm");
        var (mesh, tex) = Auxilliary.GetFaceGenSubPathStrings(fk, regularized: true);

        mesh.Should().Be(@"meshes\actors\character\facegendata\facegeom\skyrim.esm\0001a696.nif");
        tex.Should().Be(@"textures\actors\character\facegendata\facetint\skyrim.esm\0001a696.dds");
    }

    // ---------------------------------------------------------------------
    // TryRegularizePath
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryRegularizePath_NullOrWhitespace_ReturnsFalseAndEmpty(string? input)
    {
        var ok = Auxilliary.TryRegularizePath(input, out var result);
        ok.Should().BeFalse();
        result.Should().BeEmpty();
    }

    [Fact]
    public void TryRegularizePath_StripsDataPrefix()
    {
        var ok = Auxilliary.TryRegularizePath(@"C:\Games\Skyrim\Data\textures\foo\bar.dds", out var result);
        ok.Should().BeTrue();
        result.Should().Be(@"textures\foo\bar.dds");
    }

    [Fact]
    public void TryRegularizePath_DataPrefixIsCaseInsensitive()
    {
        var ok = Auxilliary.TryRegularizePath(@"X:\modlist\DATA\meshes\actors\x.nif", out var result);
        ok.Should().BeTrue();
        result.Should().Be(@"meshes\actors\x.nif");
    }

    [Fact]
    public void TryRegularizePath_DataAsLastSegment_IsNotTreatedAsPrefix()
    {
        // dataIdx + 1 must be < segments.Count; a trailing "data" segment falls
        // through to extension inference instead (here .dds -> textures prepended).
        var ok = Auxilliary.TryRegularizePath(@"foo\data.dds", out var result);
        ok.Should().BeTrue();
        result.Should().Be(@"textures\foo\data.dds");
    }

    [Fact]
    public void TryRegularizePath_BareDds_PrependsTextures()
    {
        var ok = Auxilliary.TryRegularizePath(@"foo\bar.dds", out var result);
        ok.Should().BeTrue();
        result.Should().Be(@"textures\foo\bar.dds");
    }

    [Theory]
    [InlineData("foo/bar.nif")]
    [InlineData("foo/bar.tri")]
    public void TryRegularizePath_BareMeshExtensions_PrependMeshes(string input)
    {
        // Forward slashes are normalised to backslashes; .nif and .tri both map to meshes.
        var ok = Auxilliary.TryRegularizePath(input, out var result);
        ok.Should().BeTrue();
        result.Should().StartWith(@"meshes\");
        result.Should().EndWith(Path.GetExtension(input));
    }

    [Fact]
    public void TryRegularizePath_AlreadyTypePrefixed_LeavesPrefixSingle()
    {
        // segments[0] already equals the expected type folder -> no extra prefix added.
        var ok = Auxilliary.TryRegularizePath(@"textures\foo\bar.dds", out var result);
        ok.Should().BeTrue();
        result.Should().Be(@"textures\foo\bar.dds");
    }

    [Fact]
    public void TryRegularizePath_AlreadyTypePrefixed_IsCaseInsensitive()
    {
        var ok = Auxilliary.TryRegularizePath(@"Meshes\foo\bar.nif", out var result);
        ok.Should().BeTrue();
        // The original casing of the first segment is preserved (only re-joined).
        result.Should().Be(@"Meshes\foo\bar.nif");
    }

    [Fact]
    public void TryRegularizePath_UnknownExtensionWithoutData_ReturnsFalseNormalized()
    {
        // No "data" prefix and an unsupported extension -> cannot guarantee
        // regularization. The normalised (slash-converted, trimmed) path is echoed.
        var ok = Auxilliary.TryRegularizePath(@"foo/bar.txt", out var result);
        ok.Should().BeFalse();
        result.Should().Be(@"foo\bar.txt");
    }

    [Fact]
    public void TryRegularizePath_DataPrefixWins_OverExtensionInference()
    {
        // When both a "data" segment and a known extension are present, the data
        // strip takes priority and the type folder is NOT prepended.
        var ok = Auxilliary.TryRegularizePath(@"Data\sub\bar.dds", out var result);
        ok.Should().BeTrue();
        result.Should().Be(@"sub\bar.dds");
    }

    // ---------------------------------------------------------------------
    // AddTopFolderByExtension
    // ---------------------------------------------------------------------

    [Fact]
    public void AddTopFolderByExtension_DdsWithoutTextures_PrependsTextures()
    {
        Auxilliary.AddTopFolderByExtension(@"foo\bar.dds")
            .Should().Be(Path.Combine("Textures", @"foo\bar.dds"));
    }

    [Theory]
    [InlineData(@"foo\bar.nif")]
    [InlineData(@"foo\bar.tri")]
    public void AddTopFolderByExtension_MeshExtensionsWithoutMeshes_PrependMeshes(string input)
    {
        Auxilliary.AddTopFolderByExtension(input)
            .Should().Be(Path.Combine("Meshes", input));
    }

    [Theory]
    [InlineData(@"textures\foo\bar.dds")]
    [InlineData(@"Textures\foo\bar.dds")]
    public void AddTopFolderByExtension_DdsAlreadyUnderTextures_IsUnchanged(string input)
    {
        // Prefix check is case-insensitive, so neither casing gets a second prefix.
        Auxilliary.AddTopFolderByExtension(input).Should().Be(input);
    }

    [Fact]
    public void AddTopFolderByExtension_NifAlreadyUnderMeshes_IsUnchanged()
    {
        Auxilliary.AddTopFolderByExtension(@"meshes\foo\bar.nif").Should().Be(@"meshes\foo\bar.nif");
    }

    [Fact]
    public void AddTopFolderByExtension_UnknownExtension_IsUnchanged()
    {
        Auxilliary.AddTopFolderByExtension(@"foo\bar.txt").Should().Be(@"foo\bar.txt");
    }

    // ---------------------------------------------------------------------
    // NormalizeFolderForCompare
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeFolderForCompare_NullOrWhitespace_ReturnsEmpty(string? input)
    {
        Auxilliary.NormalizeFolderForCompare(input).Should().BeEmpty();
    }

    [Fact]
    public void NormalizeFolderForCompare_TrimsTrailingSeparators()
    {
        // A rooted absolute path is preserved by GetFullPath; trailing separators are stripped.
        var result = Auxilliary.NormalizeFolderForCompare(@"C:\Games\Skyrim\Data\");
        result.Should().Be(@"C:\Games\Skyrim\Data");
    }

    [Fact]
    public void NormalizeFolderForCompare_TrimsAltSeparator()
    {
        var result = Auxilliary.NormalizeFolderForCompare(@"C:\Games\Skyrim\Data/");
        result.Should().Be(@"C:\Games\Skyrim\Data");
    }

    [Fact]
    public void NormalizeFolderForCompare_ResolvesToFullPath()
    {
        // A bare relative segment is resolved against the current directory, so the
        // result is rooted and ends with the segment.
        var result = Auxilliary.NormalizeFolderForCompare("relativeFolder");
        result.Should().Be(Path.Combine(Directory.GetCurrentDirectory(), "relativeFolder"));
    }

    // ---------------------------------------------------------------------
    // IsUnderRoot
    // ---------------------------------------------------------------------

    [Fact]
    public void IsUnderRoot_EmptyRoot_ReturnsFalse()
    {
        Auxilliary.IsUnderRoot(@"C:\anything", "").Should().BeFalse();
    }

    [Fact]
    public void IsUnderRoot_ExactMatch_ReturnsTrue()
    {
        Auxilliary.IsUnderRoot(@"C:\Games\Foo", @"C:\Games\Foo").Should().BeTrue();
    }

    [Fact]
    public void IsUnderRoot_ExactMatch_IsCaseInsensitive()
    {
        Auxilliary.IsUnderRoot(@"C:\GAMES\foo", @"C:\games\FOO").Should().BeTrue();
    }

    [Fact]
    public void IsUnderRoot_DescendantPath_ReturnsTrue()
    {
        Auxilliary.IsUnderRoot(@"C:\Games\Foo\textures\x.dds", @"C:\Games\Foo").Should().BeTrue();
    }

    [Fact]
    public void IsUnderRoot_SiblingWithSharedPrefix_IsNotUnderRoot()
    {
        // "FooBar" must NOT be considered under "Foo": the separator-aware check
        // requires a directory boundary after the root, not a bare string prefix.
        Auxilliary.IsUnderRoot(@"C:\Games\FooBar", @"C:\Games\Foo").Should().BeFalse();
    }

    [Fact]
    public void IsUnderRoot_CandidateIsParentOfRoot_ReturnsFalse()
    {
        Auxilliary.IsUnderRoot(@"C:\Games", @"C:\Games\Foo").Should().BeFalse();
    }

    [Fact]
    public void IsUnderRoot_RoundTripWithNormalizeForCompare()
    {
        // Exercise the intended pairing: both args produced by NormalizeFolderForCompare.
        var root = Auxilliary.NormalizeFolderForCompare(@"C:\Games\Foo\");
        var candidate = Auxilliary.NormalizeFolderForCompare(@"C:\Games\Foo\sub\");
        Auxilliary.IsUnderRoot(candidate, root).Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // ValidPluginExtensions
    // ---------------------------------------------------------------------

    [Fact]
    public void ValidPluginExtensions_ContainsThePluginExtensions()
    {
        Auxilliary.ValidPluginExtensions.Should().BeEquivalentTo(new[] { ".esp", ".esm", ".esl" });
    }

    [Theory]
    [InlineData(".ESP")]
    [InlineData(".Esm")]
    [InlineData(".eSl")]
    public void ValidPluginExtensions_IsCaseInsensitive(string ext)
    {
        // Built with StringComparer.OrdinalIgnoreCase.
        Auxilliary.ValidPluginExtensions.Contains(ext).Should().BeTrue();
    }

    [Theory]
    [InlineData("esp")]   // missing leading dot
    [InlineData(".bsa")]
    [InlineData(".nif")]
    public void ValidPluginExtensions_RejectsNonPluginExtensions(string ext)
    {
        Auxilliary.ValidPluginExtensions.Contains(ext).Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // Gender enum (declared alongside Auxilliary)
    // ---------------------------------------------------------------------

    [Fact]
    public void Gender_HasExactlyFemaleThenMale()
    {
        ((int)Gender.Female).Should().Be(0);
        ((int)Gender.Male).Should().Be(1);
        Enum.GetValues<Gender>().Should().HaveCount(2);
    }
}
