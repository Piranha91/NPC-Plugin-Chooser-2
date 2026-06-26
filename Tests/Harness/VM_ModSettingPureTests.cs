using System.Collections.ObjectModel;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// Pure / link-cache-free surface of <see cref="VM_ModSetting"/>:
///   * <c>TryParseSkyPatcherNpc</c> — parses a SkyPatcher <c>filterByNpcs=</c> token of either
///     <c>Plugin.esp|HEXID</c> or bare <c>EditorID</c> form into a <see cref="FormKey"/> set.
///     This is a private instance method but reads NO instance state, so it runs on an
///     <see cref="Reflect.Uninitialized{T}"/> receiver.
///   * <see cref="VM_ModSetting.FaceGenExists"/> — public; tests whether a given NPC's FaceGen
///     mesh/tint path is present in the per-mod loose-file or BSA caches. Reads only the
///     <c>CorrespondingFolderPaths</c> [Reactive] collection, which is seeded directly into the
///     Fody backing field. A <see cref="TempDir"/>-keyed cache is used for the loose-file branch
///     so this file lives in the harness namespace.
///   * the private <c>SafeFolderNames</c> / <c>HasAnyAssignedPath</c> path helpers.
///
/// All targets are deterministic functions of their arguments (plus, for FaceGenExists, the one
/// seeded collection), so the VM is allocated with <see cref="Reflect.Uninitialized{T}"/> — its
/// heavy ReactiveUI constructor (schedulers, commands, parent VM) never runs. No STA, no game
/// install, no scheduler.
/// </summary>
public class VM_ModSettingPureTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static bool TryParse(
        string token,
        IReadOnlyDictionary<string, HashSet<FormKey>> editorIdMap,
        out HashSet<FormKey> result)
    {
        var vm = Reflect.Uninitialized<VM_ModSetting>();
        var args = new object?[] { token, editorIdMap, null };
        var ok = Reflect.Invoke<bool>(vm, "TryParseSkyPatcherNpc", args);
        result = (HashSet<FormKey>)args[2]!;
        return ok;
    }

    private static IReadOnlyDictionary<string, HashSet<FormKey>> EmptyMap() =>
        new Dictionary<string, HashSet<FormKey>>();

    /// <summary>Builds a VM_ModSetting without its heavy ctor, seeding only the
    /// CorrespondingFolderPaths collection that FaceGenExists reads.</summary>
    private static VM_ModSetting FaceGenVm(params string[] folderPaths)
    {
        var vm = Reflect.Uninitialized<VM_ModSetting>();
        // Fody's [Reactive] weaver retains the compiler-generated backing field; setting it
        // directly bypasses RaiseAndSetIfChanged (which an uninitialized object can't run).
        Reflect.SetField(vm, "<CorrespondingFolderPaths>k__BackingField",
            new ObservableCollection<string>(folderPaths));
        return vm;
    }

    /// <summary>Reproduces exactly the normalized relative paths FaceGenExists builds for a
    /// FormKey (forward-slash, lowercase, with the Meshes/Textures type-folder prefix).</summary>
    private static (string mesh, string tex) FaceGenRelPaths(FormKey fk)
    {
        var (meshSub, texSub) = Auxilliary.GetFaceGenSubPathStrings(fk);
        var mesh = System.IO.Path.Combine("Meshes", meshSub).Replace('\\', '/').ToLowerInvariant();
        var tex = System.IO.Path.Combine("Textures", texSub).Replace('\\', '/').ToLowerInvariant();
        return (mesh, tex);
    }

    private static HashSet<string> SafeFolderNames(VM_ModSetting vm, IEnumerable<string>? paths) =>
        // Wrap the single argument in an explicit object[] so a null `paths` is passed as a
        // one-element args array (a null element), not as a null args array.
        Reflect.Invoke<HashSet<string>>(vm, "SafeFolderNames", new object?[] { paths })!;

    private static bool HasAnyAssignedPath(IReadOnlyCollection<string>? paths) =>
        // Explicit args array so a null `paths` is the single argument, not a null args array.
        Reflect.InvokeStatic<VM_ModSetting, bool>("HasAnyAssignedPath", new object?[] { paths });

    // ------------------------------------------------------------------
    // TryParseSkyPatcherNpc — Plugin.esp|HEXID form
    // ------------------------------------------------------------------

    [Fact]
    public void TryParse_ModPipeHex_ParsesToFormKey()
    {
        var ok = TryParse("Skyrim.esm|132AB", EmptyMap(), out var keys);

        ok.Should().BeTrue();
        keys.Should().ContainSingle()
            .Which.Should().Be(new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x132AB));
    }

    [Fact]
    public void TryParse_HexIsCaseInsensitive()
    {
        TryParse("Dawnguard.esm|abcd", EmptyMap(), out var keys).Should().BeTrue();
        keys.Single().Should().Be(new FormKey(ModKey.FromFileName("Dawnguard.esm"), 0xABCD));
    }

    [Fact]
    public void TryParse_ZeroId_IsValid()
    {
        TryParse("Update.esm|0", EmptyMap(), out var keys).Should().BeTrue();
        keys.Single().ID.Should().Be(0u);
    }

    [Fact]
    public void TryParse_BadHex_ReturnsFalseAndEmptySet()
    {
        // "ZZZZ" is not a hex number -> uint.TryParse fails -> overall false.
        TryParse("Skyrim.esm|ZZZZ", EmptyMap(), out var keys).Should().BeFalse();
        keys.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_DecimalLookingButValidHex_IsParsedAsHex()
    {
        // "10" is parsed with NumberStyles.HexNumber -> 0x10 == 16, NOT decimal 10.
        TryParse("Skyrim.esm|10", EmptyMap(), out var keys).Should().BeTrue();
        keys.Single().ID.Should().Be(0x10u);
    }

    [Fact]
    public void TryParse_HexWithLeadingHashOrPrefix_ReturnsFalse()
    {
        // "0x132AB" includes the '0x' prefix which HexNumber style rejects.
        TryParse("Skyrim.esm|0x132AB", EmptyMap(), out var keys).Should().BeFalse();
        keys.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_ModNameWithoutPluginExtension_ReturnsFalse()
    {
        // "NotAPlugin" has no .esp/.esm/.esl extension -> ModKey.TryFromFileName fails.
        TryParse("NotAPlugin|132AB", EmptyMap(), out var keys).Should().BeFalse();
        keys.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_OverflowingHexId_ReturnsFalse()
    {
        // 9 hex digits overflow a uint -> uint.TryParse fails.
        TryParse("Skyrim.esm|1FFFFFFFF", EmptyMap(), out var keys).Should().BeFalse();
        keys.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // TryParseSkyPatcherNpc — bare EditorID form (single part)
    // ------------------------------------------------------------------

    [Fact]
    public void TryParse_EditorId_HitInMap_ReturnsMappedSet()
    {
        var fk1 = MutagenFixtures.Fk("000800:Source.esp");
        var fk2 = MutagenFixtures.Fk("000801:Other.esp");
        var map = new Dictionary<string, HashSet<FormKey>>
        {
            ["MyNpcEditorID"] = new() { fk1, fk2 }
        };

        TryParse("MyNpcEditorID", map, out var keys).Should().BeTrue();
        keys.Should().BeEquivalentTo(new[] { fk1, fk2 });
    }

    [Fact]
    public void TryParse_EditorId_MissInMap_ReturnsFalse()
    {
        var map = new Dictionary<string, HashSet<FormKey>>
        {
            ["KnownEditorID"] = new() { MutagenFixtures.Fk("000800:Source.esp") }
        };

        // The TryGetValue miss sets the out param to null; the method returns false.
        TryParse("UnknownEditorID", map, out var keys).Should().BeFalse();
        keys.Should().BeNull("a map miss leaves the out HashSet as the TryGetValue null result");
    }

    [Fact]
    public void TryParse_EditorId_IsCaseSensitiveAgainstMap()
    {
        var fk = MutagenFixtures.Fk("000800:Source.esp");
        var map = new Dictionary<string, HashSet<FormKey>> { ["CaseMatters"] = new() { fk } };

        // Default dictionary equality is ordinal/case-sensitive -> wrong case is a miss.
        TryParse("casematters", map, out _).Should().BeFalse();
        TryParse("CaseMatters", map, out var keys).Should().BeTrue();
        keys.Single().Should().Be(fk);
    }

    // ------------------------------------------------------------------
    // TryParseSkyPatcherNpc — degenerate input
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_BlankInput_ReturnsFalseWithEmptySet(string? token)
    {
        var ok = TryParse(token!, EmptyMap(), out var keys);
        ok.Should().BeFalse();
        keys.Should().BeEmpty("the method seeds an empty set before the blank short-circuit");
    }

    [Fact]
    public void TryParse_MoreThanTwoParts_ReturnsFalse()
    {
        // Three '|'-delimited parts match neither the 2-part nor the 1-part branch.
        TryParse("Skyrim.esm|132AB|extra", EmptyMap(), out var keys).Should().BeFalse();
        keys.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_TrailingPipe_IsTwoPartsWithBlankHex_ReturnsFalse()
    {
        // "Skyrim.esm|" splits to ["Skyrim.esm", ""] -> 2 parts, but "" is not valid hex.
        TryParse("Skyrim.esm|", EmptyMap(), out var keys).Should().BeFalse();
        keys.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_LeadingPipe_BlankModName_ReturnsFalse()
    {
        // "|132AB" splits to ["", "132AB"] -> blank mod name fails ModKey.TryFromFileName.
        TryParse("|132AB", EmptyMap(), out var keys).Should().BeFalse();
        keys.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // FaceGenExists — loose-file cache branch
    // ------------------------------------------------------------------

    [Fact]
    public void FaceGenExists_LooseMeshHit_ReturnsTrue()
    {
        using var dir = new TempDir(nameof(FaceGenExists_LooseMeshHit_ReturnsTrue));
        var fk = MutagenFixtures.Fk("000800:Source.esp");
        var (mesh, _) = FaceGenRelPaths(fk);

        var vm = FaceGenVm(dir.Path);
        var loose = new Dictionary<string, HashSet<string>>
        {
            [dir.Path] = new() { mesh }
        };

        vm.FaceGenExists(fk, loose, new HashSet<string>()).Should().BeTrue();
    }

    [Fact]
    public void FaceGenExists_LooseTintHit_ReturnsTrue()
    {
        using var dir = new TempDir(nameof(FaceGenExists_LooseTintHit_ReturnsTrue));
        var fk = MutagenFixtures.Fk("000800:Source.esp");
        var (_, tex) = FaceGenRelPaths(fk);

        var vm = FaceGenVm(dir.Path);
        var loose = new Dictionary<string, HashSet<string>>
        {
            [dir.Path] = new() { tex }
        };

        // Only the tint texture is present (no mesh) -> still a hit.
        vm.FaceGenExists(fk, loose, new HashSet<string>()).Should().BeTrue();
    }

    [Fact]
    public void FaceGenExists_LooseLookupIsCaseInsensitive()
    {
        using var dir = new TempDir(nameof(FaceGenExists_LooseLookupIsCaseInsensitive));
        var fk = MutagenFixtures.Fk("000800:Source.esp");
        var (mesh, _) = FaceGenRelPaths(fk);

        var vm = FaceGenVm(dir.Path);
        var loose = new Dictionary<string, HashSet<string>>
        {
            [dir.Path] = new() { mesh.ToUpperInvariant() }
        };

        // The comparison uses OrdinalIgnoreCase, so an upper-cased cache entry still matches.
        vm.FaceGenExists(fk, loose, new HashSet<string>()).Should().BeTrue();
    }

    [Fact]
    public void FaceGenExists_LooseCacheKeyedToDifferentFolder_IsMiss()
    {
        using var dir = new TempDir(nameof(FaceGenExists_LooseCacheKeyedToDifferentFolder_IsMiss));
        var fk = MutagenFixtures.Fk("000800:Source.esp");
        var (mesh, tex) = FaceGenRelPaths(fk);

        // The VM's folder is dir.Path, but the cache is keyed under a DIFFERENT folder.
        var vm = FaceGenVm(dir.Path);
        var loose = new Dictionary<string, HashSet<string>>
        {
            ["C:/some/other/folder"] = new() { mesh, tex }
        };

        vm.FaceGenExists(fk, loose, new HashSet<string>()).Should().BeFalse();
    }

    [Fact]
    public void FaceGenExists_NoCorrespondingFolders_SkipsLooseBranch()
    {
        var fk = MutagenFixtures.Fk("000800:Source.esp");
        var (mesh, tex) = FaceGenRelPaths(fk);

        // VM has no folders, so the loose cache is never consulted even though it has the file.
        var vm = FaceGenVm();
        var loose = new Dictionary<string, HashSet<string>>
        {
            ["irrelevant"] = new() { mesh, tex }
        };

        vm.FaceGenExists(fk, loose, new HashSet<string>()).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // FaceGenExists — BSA cache branch
    // ------------------------------------------------------------------

    [Fact]
    public void FaceGenExists_BsaMeshHit_ReturnsTrue()
    {
        var fk = MutagenFixtures.Fk("000800:Source.esp");
        var (mesh, _) = FaceGenRelPaths(fk);

        // Empty loose cache; BSA cache holds the mesh path -> hit (BSA branch is folder-agnostic).
        var vm = FaceGenVm();
        vm.FaceGenExists(fk, new Dictionary<string, HashSet<string>>(),
            new HashSet<string> { mesh }).Should().BeTrue();
    }

    [Fact]
    public void FaceGenExists_BsaTintHit_ReturnsTrue()
    {
        var fk = MutagenFixtures.Fk("000800:Source.esp");
        var (_, tex) = FaceGenRelPaths(fk);

        var vm = FaceGenVm();
        vm.FaceGenExists(fk, new Dictionary<string, HashSet<string>>(),
            new HashSet<string> { tex }).Should().BeTrue();
    }

    [Fact]
    public void FaceGenExists_BsaCacheIsCaseSensitive_HashSetDefaultComparer()
    {
        var fk = MutagenFixtures.Fk("000800:Source.esp");
        var (mesh, _) = FaceGenRelPaths(fk);

        // The BSA branch uses HashSet.Contains with the set's own (here default/ordinal)
        // comparer; an upper-cased entry will NOT match the lowercase query path.
        var vm = FaceGenVm();
        vm.FaceGenExists(fk, new Dictionary<string, HashSet<string>>(),
            new HashSet<string> { mesh.ToUpperInvariant() }).Should().BeFalse();
    }

    [Fact]
    public void FaceGenExists_NeitherCacheHasIt_ReturnsFalse()
    {
        var fk = MutagenFixtures.Fk("000800:Source.esp");
        var vm = FaceGenVm("anyFolder");

        vm.FaceGenExists(fk,
            new Dictionary<string, HashSet<string>> { ["anyFolder"] = new() { "unrelated/path.nif" } },
            new HashSet<string> { "another/unrelated.dds" }).Should().BeFalse();
    }

    [Fact]
    public void FaceGenExists_PathFormat_LocksLowercaseForwardSlashWithTypePrefix()
    {
        // Lock the exact normalized key shape so a future change to FaceGenExists' path
        // construction is caught here, independent of GetFaceGenSubPathStrings.
        var fk = FormKey.Factory("01A696:Skyrim.esm");
        var (mesh, tex) = FaceGenRelPaths(fk);

        mesh.Should().Be("meshes/actors/character/facegendata/facegeom/skyrim.esm/0001a696.nif");
        tex.Should().Be("textures/actors/character/facegendata/facetint/skyrim.esm/0001a696.dds");

        var vm = FaceGenVm();
        vm.FaceGenExists(fk, new Dictionary<string, HashSet<string>>(),
            new HashSet<string> { mesh }).Should().BeTrue();
    }

    [Fact]
    public void FaceGenExists_DistinctFormKeysDoNotCollide()
    {
        var present = MutagenFixtures.Fk("000800:Source.esp");
        var absent = MutagenFixtures.Fk("000801:Source.esp");
        var (presentMesh, _) = FaceGenRelPaths(present);

        var vm = FaceGenVm();
        var bsa = new HashSet<string> { presentMesh };

        vm.FaceGenExists(present, new Dictionary<string, HashSet<string>>(), bsa).Should().BeTrue();
        vm.FaceGenExists(absent, new Dictionary<string, HashSet<string>>(), bsa)
            .Should().BeFalse("a different local form ID maps to a different facegen filename");
    }

    // ------------------------------------------------------------------
    // SafeFolderNames — private instance helper (reads nothing instance-specific)
    // ------------------------------------------------------------------

    [Fact]
    public void SafeFolderNames_ReturnsLeafFolderNames()
    {
        var vm = Reflect.Uninitialized<VM_ModSetting>();
        var names = SafeFolderNames(vm, new[]
        {
            @"C:\Mods\Apachii Hair",
            @"D:\Games\Skyrim\SG Brows"
        });

        names.Should().BeEquivalentTo(new[] { "Apachii Hair", "SG Brows" });
    }

    [Fact]
    public void SafeFolderNames_TrimsTrailingSeparators()
    {
        var vm = Reflect.Uninitialized<VM_ModSetting>();
        var names = SafeFolderNames(vm, new[] { @"C:\Mods\KS Hair\" });
        names.Should().ContainSingle().Which.Should().Be("KS Hair");
    }

    [Fact]
    public void SafeFolderNames_IgnoresBlankEntriesAndIsCaseInsensitiveSet()
    {
        var vm = Reflect.Uninitialized<VM_ModSetting>();
        var names = SafeFolderNames(vm, new[]
        {
            @"C:\Mods\Hair",
            "   ",
            "",
            @"D:\Other\hair" // same leaf name, different case -> deduped by OrdinalIgnoreCase
        });

        names.Should().ContainSingle().Which.Should().BeEquivalentTo("Hair");
    }

    [Fact]
    public void SafeFolderNames_NullInput_ReturnsEmptySet()
    {
        var vm = Reflect.Uninitialized<VM_ModSetting>();
        SafeFolderNames(vm, null).Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // HasAnyAssignedPath — private static helper
    // ------------------------------------------------------------------

    [Fact]
    public void HasAnyAssignedPath_NullCollection_IsFalse()
    {
        HasAnyAssignedPath(null).Should().BeFalse();
    }

    [Fact]
    public void HasAnyAssignedPath_EmptyCollection_IsFalse()
    {
        HasAnyAssignedPath(Array.Empty<string>()).Should().BeFalse();
    }

    [Fact]
    public void HasAnyAssignedPath_OnlyBlankAndSeparatorOnlyPaths_IsFalse()
    {
        // Each entry trims to empty after stripping directory separators / whitespace.
        HasAnyAssignedPath(new[] { "", "   ", "\\", "/" }).Should().BeFalse();
    }

    [Fact]
    public void HasAnyAssignedPath_AnyRealPath_IsTrue()
    {
        HasAnyAssignedPath(new[] { "", @"C:\Mods\Real" }).Should().BeTrue();
    }

    // NOTE: SKIPPED env/DI-bound members — RefreshNpcLists, GetSkyPatcherImportsAsync,
    // GetSkyPatcherTargetModKeysAsync, UpdateCorrespondingModKeys, RecomputeResourceOnlyPlugins,
    // AutoSetResourcePlugins, CheckMergeInSuitability, SaveToModel-roundtrip-against-parent, and
    // every ReactiveCommand body all read injected services (_aux / _pluginProvider /
    // _environmentStateProvider / _parentVm / _lazySettingsVm) or a live link cache, so they
    // require the full DI graph / a Skyrim install and are out of scope for this pure file.
}
