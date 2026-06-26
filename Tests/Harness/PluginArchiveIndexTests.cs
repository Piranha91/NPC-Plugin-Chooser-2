using System.IO;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// <see cref="PluginArchiveIndex"/> — maps a plugin <see cref="ModKey"/> to the BSA files it
/// "owns" in a directory, with a process-wide per-directory cache + <c>Invalidate</c>.
///
/// Ownership is derived purely from file NAMES (the source enumerates filenames; it never
/// opens or parses BSA headers), so the fixtures create empty <c>.bsa</c> stubs plus the
/// matching plugin stub. A BSA is owned by a plugin base when the BSA base name either equals
/// the plugin base (exact) or extends it with a space at the boundary (a "Foo - Textures.bsa"
/// sub-archive). When several plugin bases match, the LONGEST one wins. A BSA whose name has no
/// corresponding plugin file in the directory is owned by nobody.
///
/// Each test isolates itself with a fresh <see cref="TempDir"/> (a brand-new GUID path, so the
/// static cache key never collides) and/or an explicit <c>Invalidate</c> to prove cache
/// behaviour, avoiding bleed across the process-wide dictionary.
/// </summary>
public class PluginArchiveIndexTests
{
    /// <summary>Creates an empty plugin stub (so it registers as a valid owner base).</summary>
    private static void Plugin(TempDir tmp, string fileName) => tmp.WriteText(fileName, "");

    /// <summary>Creates an empty BSA stub (the index keys off the name, not the bytes).</summary>
    private static void Bsa(TempDir tmp, string fileName) => tmp.WriteText(fileName, "");

    private static ModKey Mk(string fileName) => MutagenFixtures.Mk(fileName);

    // -------------------------------------------------------------------------------------
    // Null / empty / missing inputs short-circuit to an empty set.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void GetOwnedBsaFiles_NullModKey_ReturnsEmpty()
    {
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod.esp");
        Bsa(tmp, "MyMod.bsa");

        PluginArchiveIndex.GetOwnedBsaFiles(ModKey.Null, tmp.Path).Should().BeEmpty();
    }

    [Fact]
    public void GetOwnedBsaFiles_BlankDirectory_ReturnsEmpty()
    {
        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), "").Should().BeEmpty();
        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), "   ").Should().BeEmpty();
    }

    [Fact]
    public void GetOwnedBsaFiles_MissingDirectory_ReturnsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), "NPC2.Tests", "does_not_exist_" + Guid.NewGuid().ToString("N"));
        Directory.Exists(missing).Should().BeFalse();

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), missing).Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------
    // Exact-name ownership and sub-archive ("Foo - Textures.bsa") ownership.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void GetOwnedBsaFiles_ExactMatch_IsOwned()
    {
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod.esp");
        Bsa(tmp, "MyMod.bsa");

        var owned = PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), tmp.Path);

        owned.Should().ContainSingle();
        owned.Should().Contain(p => Path.GetFileName(p).Equals("MyMod.bsa", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetOwnedBsaFiles_SubArchivesWithSpace_AreOwned()
    {
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod.esp");
        Bsa(tmp, "MyMod.bsa");
        Bsa(tmp, "MyMod - Textures.bsa");
        Bsa(tmp, "MyMod - Voices.bsa");

        var owned = PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), tmp.Path);

        owned.Select(Path.GetFileName).Should().BeEquivalentTo(
            new[] { "MyMod.bsa", "MyMod - Textures.bsa", "MyMod - Voices.bsa" });
    }

    [Fact]
    public void GetOwnedBsaFiles_NameExtendedWithoutSpace_NotOwned()
    {
        // "MyModExtra.bsa" starts with "MyMod" but the boundary char is 'E', not ' ',
        // so it is not a sub-archive of MyMod. With no MyModExtra plugin present, it is
        // owned by nobody.
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod.esp");
        Bsa(tmp, "MyMod.bsa");
        Bsa(tmp, "MyModExtra.bsa");

        var owned = PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), tmp.Path);

        owned.Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "MyMod.bsa" });
        owned.Should().NotContain(p => Path.GetFileName(p).Equals("MyModExtra.bsa", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetOwnedBsaFiles_UnrelatedBsa_NotOwned()
    {
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod.esp");
        Bsa(tmp, "MyMod.bsa");
        Bsa(tmp, "OtherMod.bsa"); // no plugin prefix matches -> orphan

        var owned = PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), tmp.Path);

        owned.Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "MyMod.bsa" });
    }

    [Fact]
    public void GetOwnedBsaFiles_BsaWithoutMatchingPlugin_IsOrphan()
    {
        // A BSA whose base has no corresponding plugin file is owned by nobody, even when
        // queried by a ModKey that does have a plugin.
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod.esp");
        Bsa(tmp, "Standalone.bsa"); // no Standalone.esp/.esm/.esl

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("Standalone.esp"), tmp.Path).Should().BeEmpty();
        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), tmp.Path).Should().BeEmpty();
    }

    [Fact]
    public void GetOwnedBsaFiles_QueriedPluginHasNoBsas_ReturnsEmpty()
    {
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod.esp");
        Plugin(tmp, "Empty.esp");
        Bsa(tmp, "MyMod.bsa");

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("Empty.esp"), tmp.Path).Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------
    // Longest-base-wins between overlapping plugin names ("Mod" vs "ModPlus").
    // -------------------------------------------------------------------------------------

    [Fact]
    public void GetOwnedBsaFiles_LongestBaseWins_ExactSubName()
    {
        // "ModPlus.bsa" starts with both "Mod" and "ModPlus". The longer base ("ModPlus")
        // is matched first and claims it as an exact match, so "Mod" does not own it.
        using var tmp = new TempDir();
        Plugin(tmp, "Mod.esp");
        Plugin(tmp, "ModPlus.esp");
        Bsa(tmp, "Mod.bsa");
        Bsa(tmp, "ModPlus.bsa");

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("ModPlus.esp"), tmp.Path)
            .Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "ModPlus.bsa" });

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("Mod.esp"), tmp.Path)
            .Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "Mod.bsa" });
    }

    [Fact]
    public void GetOwnedBsaFiles_LongestBaseWins_SubArchive()
    {
        // "ModPlus - Textures.bsa" matches "ModPlus" (space boundary) before "Mod" (which
        // would see 'P' at the boundary and reject anyway). It belongs to ModPlus.
        using var tmp = new TempDir();
        Plugin(tmp, "Mod.esp");
        Plugin(tmp, "ModPlus.esp");
        Bsa(tmp, "Mod.bsa");
        Bsa(tmp, "Mod - Textures.bsa");
        Bsa(tmp, "ModPlus.bsa");
        Bsa(tmp, "ModPlus - Textures.bsa");

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("Mod.esp"), tmp.Path)
            .Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "Mod.bsa", "Mod - Textures.bsa" });

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("ModPlus.esp"), tmp.Path)
            .Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "ModPlus.bsa", "ModPlus - Textures.bsa" });
    }

    // -------------------------------------------------------------------------------------
    // Case-insensitivity of plugin extension, BSA name, and ModKey filename.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void GetOwnedBsaFiles_BsaNameCaseInsensitive()
    {
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod.esp");
        Bsa(tmp, "mymod.bsa");                 // lower-case base
        Bsa(tmp, "MYMOD - TEXTURES.bsa");      // upper-case sub-archive

        var owned = PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), tmp.Path);

        owned.Select(Path.GetFileName).Should().BeEquivalentTo(
            new[] { "mymod.bsa", "MYMOD - TEXTURES.bsa" });
    }

    [Fact]
    public void GetOwnedBsaFiles_QueryModKeyCaseInsensitive()
    {
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod.esp");
        Bsa(tmp, "MyMod.bsa");

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MYMOD.ESP"), tmp.Path)
            .Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "MyMod.bsa" });
    }

    [Theory]
    [InlineData(".esp")]
    [InlineData(".esm")]
    [InlineData(".esl")]
    public void GetOwnedBsaFiles_AllValidPluginExtensionsRegisterOwners(string ext)
    {
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod" + ext);
        Bsa(tmp, "MyMod.bsa");
        Bsa(tmp, "MyMod - Textures.bsa");

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod" + ext), tmp.Path)
            .Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "MyMod.bsa", "MyMod - Textures.bsa" });
    }

    // -------------------------------------------------------------------------------------
    // Returned set semantics: it is an OrdinalIgnoreCase HashSet of absolute paths.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void GetOwnedBsaFiles_ReturnsOrdinalIgnoreCaseHashSetOfFullPaths()
    {
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod.esp");
        var bsaPath = tmp.WriteText("MyMod.bsa", "");

        var owned = PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), tmp.Path);

        owned.Should().Contain(bsaPath, "the index stores the full enumerated path");
        owned.Should().Contain(bsaPath.ToUpperInvariant(), "the set compares OrdinalIgnoreCase");
        owned.Should().AllSatisfy(p => Path.IsPathRooted(p).Should().BeTrue());
    }

    [Fact]
    public void GetOwnedBsaFiles_EmptyDirectory_ReturnsEmpty()
    {
        using var tmp = new TempDir();
        // No plugins, no BSAs at all.
        PluginArchiveIndex.GetOwnedBsaFiles(Mk("Anything.esp"), tmp.Path).Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------
    // Process-wide cache + Invalidate behaviour.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void GetOwnedBsaFiles_CachesDirectory_StaleUntilInvalidate()
    {
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod.esp");
        Bsa(tmp, "MyMod.bsa");

        // Prime the cache.
        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), tmp.Path)
            .Should().ContainSingle();

        // Add a new sub-archive on disk AFTER the directory was indexed.
        Bsa(tmp, "MyMod - Textures.bsa");

        // Cached result is stale: the new file is not seen yet.
        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), tmp.Path)
            .Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "MyMod.bsa" });

        // After invalidation the directory is re-scanned and the new file appears.
        PluginArchiveIndex.Invalidate(tmp.Path);

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), tmp.Path)
            .Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "MyMod.bsa", "MyMod - Textures.bsa" });
    }

    [Fact]
    public void Invalidate_UnknownDirectory_DoesNotThrow()
    {
        var act = () => PluginArchiveIndex.Invalidate(
            Path.Combine(Path.GetTempPath(), "NPC2.Tests", "never_indexed_" + Guid.NewGuid().ToString("N")));
        act.Should().NotThrow();
    }

    [Fact]
    public void Invalidate_IsCaseInsensitiveOnDirectoryKey()
    {
        using var tmp = new TempDir();
        Plugin(tmp, "MyMod.esp");
        Bsa(tmp, "MyMod.bsa");

        // Prime with the canonical path.
        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), tmp.Path).Should().ContainSingle();

        Bsa(tmp, "MyMod - Textures.bsa");

        // Invalidate using an upper-cased directory key; the cache compares OrdinalIgnoreCase
        // so this still evicts the original entry.
        PluginArchiveIndex.Invalidate(tmp.Path.ToUpperInvariant());

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), tmp.Path)
            .Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "MyMod.bsa", "MyMod - Textures.bsa" });
    }

    [Fact]
    public void GetOwnedBsaFiles_DistinctDirectories_AreIndependent()
    {
        using var a = new TempDir();
        using var b = new TempDir();

        Plugin(a, "MyMod.esp");
        Bsa(a, "MyMod.bsa");

        Plugin(b, "MyMod.esp");
        Bsa(b, "MyMod.bsa");
        Bsa(b, "MyMod - Textures.bsa");

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), a.Path)
            .Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "MyMod.bsa" });

        PluginArchiveIndex.GetOwnedBsaFiles(Mk("MyMod.esp"), b.Path)
            .Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "MyMod.bsa", "MyMod - Textures.bsa" });
    }
}
