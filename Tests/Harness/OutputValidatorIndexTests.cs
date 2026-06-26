using System.Collections;
using System.IO;
using System.Reflection;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// The SkyPatcher <c>SkyPatcherIndex</c> build/lookup seam and the loose-FaceGen finders on
/// <see cref="OutputValidator"/>. Complements <see cref="OutputValidatorParsersTests"/> (which
/// covers the pure static token parsers); this file drives the stateful pieces that build/query
/// the private nested index over real temp <c>SkyPatcher</c> .ini files, and the file-system
/// finders that locate loose FaceGen across mod folders.
///
/// Reach strategy (no DI graph, no Skyrim install):
///  - <c>BuildSkyPatcherIndex</c> / <c>FindLooseFaceGenProviders</c> are INSTANCE methods but read
///    no — or only the <c>_settings</c> — field, so they run on a <see cref="Reflect.Uninitialized{T}"/>
///    <see cref="OutputValidator"/> (with <c>_settings</c> set where needed via <see cref="Reflect.SetField"/>).
///  - <c>FindLooseInModFolders</c> is <c>private static</c> → <see cref="Reflect.InvokeStatic{TOwner,T}"/>.
///  - The returned <c>SkyPatcherIndex</c> / <c>SkyPatcherHit</c> are PRIVATE nested types; their public
///    members are reflected generically (properties / <c>AddByFormKey</c> / <c>AddByEditorId</c> / <c>Lookup</c>).
///
/// Covered: BuildSkyPatcherIndex (formkey hit, editorId target, broad-filter counted, un-evaluable
/// counted, non-visual ignored, npc2 ini flagged, missing dir → empty, recursive scan, Npc2SortKey),
/// SkyPatcherIndex.Lookup/AddByFormKey/AddByEditorId (formkey hit, editorId case-insensitive, dedup,
/// empty-editorId no-op), FindLooseInModFolders (reverse last-wins, none → null, null list → null),
/// FindLooseFaceGenProviders (identical-in-another-folder found, self-mod excluded, byte-different
/// excluded, cap at 5, blank ModsFolder → empty, missing ModsFolder → empty).
/// </summary>
public class OutputValidatorIndexTests
{
    // A relative FaceGen-ish path; the finders treat it as an opaque relative path (no parsing).
    private const string RelMesh = @"meshes\actors\character\facegendata\facegeom\Skyrim.esm\00013BB9.nif";

    // ------------------------------------------------------------------
    // Reflection shims
    // ------------------------------------------------------------------

    /// <summary>An OutputValidator whose ctor never ran. Only fields a tested method reads are set.</summary>
    private static OutputValidator BareValidator(Settings? settings = null)
    {
        var v = Reflect.Uninitialized<OutputValidator>();
        if (settings != null) Reflect.SetField(v, "_settings", settings);
        return v;
    }

    /// <summary>Builds the private SkyPatcherIndex over a real folder (returned boxed as object).</summary>
    private static object BuildIndex(string skyPatcherNpcRoot, string npc2IniPath)
    {
        var v = BareValidator();
        var log = NewLog();
        return Reflect.Invoke<object>(v, "BuildSkyPatcherIndex", skyPatcherNpcRoot, npc2IniPath, log)!;
    }

    /// <summary>A StringBuilder for the <c>log</c> parameter (the methods append to it but never read it).</summary>
    private static object NewLog() => new System.Text.StringBuilder();

    private static string? FindLooseInModFolders(ModSetting mod, string rel) =>
        Reflect.InvokeStatic<OutputValidator, string?>("FindLooseInModFolders", mod, rel);

    private static List<string> FindLooseFaceGenProviders(OutputValidator v, string rel, string subjectPath, ModSetting selected) =>
        Reflect.Invoke<List<string>>(v, "FindLooseFaceGenProviders", rel, subjectPath, selected)!;

    // --- generic accessors for the private nested index/hit types ---

    private static T Prop<T>(object target, string name)
    {
        var p = target.GetType().GetProperty(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        p.Should().NotBeNull($"the private nested type should expose property '{name}'");
        return (T)p!.GetValue(target)!;
    }

    private static IList Lookup(object index, FormKey fk, string? editorId)
    {
        var m = index.GetType().GetMethod("Lookup",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        m.Should().NotBeNull();
        return (IList)m!.Invoke(index, new object?[] { fk, editorId })!;
    }

    private static void AddByEditorId(object index, string editorId, object hit)
    {
        var m = index.GetType().GetMethod("AddByEditorId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        m!.Invoke(index, new[] { editorId, hit });
    }

    private static void AddByFormKey(object index, string key, object hit)
    {
        var m = index.GetType().GetMethod("AddByFormKey",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        m!.Invoke(index, new[] { key, hit });
    }

    /// <summary>Pulls one SkyPatcherHit out of a Lookup result (or null) and returns it boxed.</summary>
    private static object? FirstHit(IList hits) => hits.Count > 0 ? hits[0] : null;

    // ==================================================================
    // BuildSkyPatcherIndex
    // ==================================================================

    [Fact]
    public void BuildIndex_MissingDir_ReturnsEmptyIndex_NoThrow()
    {
        using var tmp = new TempDir();
        var root = tmp.Combine("SkyPatcher", "npc"); // path string only; dir intentionally NOT created
        Directory.Exists(root).Should().BeFalse();

        var index = BuildIndex(root, Path.Combine(root, "NPC Plugin Chooser", "Out.ini"));

        Prop<IList>(index, "BroadFilterRules").Count.Should().Be(0);
        Prop<int>(index, "UnevaluableBroadFilterLineCount").Should().Be(0);
        // No NPC was indexed; a formkey/editorId lookup finds nothing.
        Lookup(index, FormKey.Factory("013BB9:Skyrim.esm"), "AnyEid").Count.Should().Be(0);
    }

    [Fact]
    public void BuildIndex_FilterByNpcsFormKey_VisualLine_IsLookableByFormKey()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        File.WriteAllText(Path.Combine(root, "author.ini"),
            "filterByNpcs=Skyrim.esm|013BB9:copyVisualStyle=Other.esp|000801");

        var index = BuildIndex(root, Path.Combine(root, "NPC Plugin Chooser", "Out.ini"));

        var hits = Lookup(index, FormKey.Factory("013BB9:Skyrim.esm"), editorId: null);
        hits.Count.Should().Be(1);
        var hit = FirstHit(hits)!;
        Prop<string>(hit, "IniRelPath").Should().Be("author.ini");
        Prop<bool>(hit, "IsNpc2").Should().BeFalse();
        Prop<string>(hit, "RawLine").Should().Contain("copyVisualStyle");
        Prop<string?>(hit, "MatchNote").Should().BeNull("a filterByNpcs hit is exact, not a broad-filter capture");
    }

    [Fact]
    public void BuildIndex_FilterByNpcs_EditorIdTarget_IsLookableByEditorId()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        // A bare token (no '|') is treated as an EditorID target, not a FormKey.
        File.WriteAllText(Path.Combine(root, "ed.ini"),
            "filterByNpcs=Lydia:skin=Other.esp|000801");

        var index = BuildIndex(root, Path.Combine(root, "Out.ini"));

        // No formkey index entry...
        Lookup(index, FormKey.Factory("00A2C8:Skyrim.esm"), editorId: null).Count.Should().Be(0);
        // ...but the EditorID lookup hits (case-insensitively — the index lowercases on store).
        var hits = Lookup(index, FormKey.Factory("00A2C8:Skyrim.esm"), editorId: "LYDIA");
        hits.Count.Should().Be(1);
        Prop<string>(FirstHit(hits)!, "IniRelPath").Should().Be("ed.ini");
    }

    [Fact]
    public void BuildIndex_BroadFilterVisualLine_IsCountedAsEvaluableRule()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        File.WriteAllText(Path.Combine(root, "broad.ini"),
            "filterByFactions=Skyrim.esm|0AAAAA:headparts=Other.esp|000801");

        var index = BuildIndex(root, Path.Combine(root, "Out.ini"));

        Prop<IList>(index, "BroadFilterRules").Count.Should().Be(1);
        Prop<int>(index, "UnevaluableBroadFilterLineCount").Should().Be(0);
    }

    [Fact]
    public void BuildIndex_UnevaluableBroadFilter_IsCountedNotRuleified()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        // filterByActorValue is a broad filter this tool cannot evaluate per-NPC.
        File.WriteAllText(Path.Combine(root, "unk.ini"),
            "filterByActorValue=Health|50:skin=Other.esp|000801");

        var index = BuildIndex(root, Path.Combine(root, "Out.ini"));

        Prop<IList>(index, "BroadFilterRules").Count.Should().Be(0);
        Prop<int>(index, "UnevaluableBroadFilterLineCount").Should().Be(1);
    }

    [Fact]
    public void BuildIndex_NonVisualLine_IsIgnored()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        // No VisualActionKeys directive (only a non-visual 'setflags') → the line is skipped entirely.
        File.WriteAllText(Path.Combine(root, "nonvisual.ini"),
            "filterByNpcs=Skyrim.esm|013BB9:setflags=Essential");

        var index = BuildIndex(root, Path.Combine(root, "Out.ini"));

        Lookup(index, FormKey.Factory("013BB9:Skyrim.esm"), editorId: null).Count.Should().Be(0);
        Prop<IList>(index, "BroadFilterRules").Count.Should().Be(0);
        Prop<int>(index, "UnevaluableBroadFilterLineCount").Should().Be(0);
    }

    [Fact]
    public void BuildIndex_CommentsAndBlankLines_AreSkipped()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        File.WriteAllText(Path.Combine(root, "mix.ini"), string.Join("\n", new[]
        {
            "; comment",
            "",
            "   ",
            "filterByNpcs=Skyrim.esm|013BB9:race=Other.esp|000801",
        }));

        var index = BuildIndex(root, Path.Combine(root, "Out.ini"));

        Lookup(index, FormKey.Factory("013BB9:Skyrim.esm"), editorId: null).Count.Should().Be(1);
    }

    [Fact]
    public void BuildIndex_Npc2Ini_HitIsFlaggedIsNpc2()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        var npc2Dir = Path.Combine(root, "NPC Plugin Chooser");
        Directory.CreateDirectory(npc2Dir);
        var npc2Ini = Path.Combine(npc2Dir, "Out.ini");
        File.WriteAllText(npc2Ini,
            "filterByNpcs=Skyrim.esm|013BB9:copyVisualStyle=Out.esp|000801");

        var index = BuildIndex(root, npc2Ini);

        var hits = Lookup(index, FormKey.Factory("013BB9:Skyrim.esm"), editorId: null);
        hits.Count.Should().Be(1);
        Prop<bool>(FirstHit(hits)!, "IsNpc2").Should().BeTrue("the line came from this app's own .ini path");
    }

    [Fact]
    public void BuildIndex_Npc2SortKey_IsRelativePathOfOwnIni()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        var npc2Ini = Path.Combine(root, "NPC Plugin Chooser", "Out.ini");

        var index = BuildIndex(root, npc2Ini);

        // MakeSortKey: path relative to the npc root, lowercased, backslash-normalized.
        Prop<string>(index, "Npc2SortKey").Should().Be(@"npc plugin chooser\out.ini");
    }

    [Fact]
    public void BuildIndex_RecursivelyScansSubdirectories()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        var sub = Path.Combine(root, "ZZZ Author");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "deep.ini"),
            "filterByNpcs=Skyrim.esm|013BB9:hair=Other.esp|000801");

        var index = BuildIndex(root, Path.Combine(root, "Out.ini"));

        var hits = Lookup(index, FormKey.Factory("013BB9:Skyrim.esm"), editorId: null);
        hits.Count.Should().Be(1);
        // Relative path retains the subfolder.
        Prop<string>(FirstHit(hits)!, "IniRelPath").Should().Be(Path.Combine("ZZZ Author", "deep.ini"));
    }

    // ==================================================================
    // SkyPatcherIndex.Lookup / AddByFormKey / AddByEditorId
    // ==================================================================

    [Fact]
    public void Lookup_FormKeyAndEditorId_BothMatchSameLine_IsDeduplicated()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        // ONE line targets the NPC by BOTH a formkey token and an editorId token; the formkey and
        // editorId indexes therefore both reference the same hit. Lookup must return it only once.
        File.WriteAllText(Path.Combine(root, "both.ini"),
            "filterByNpcs=Skyrim.esm|013BB9,Lydia:weight=Other.esp|000801");

        var index = BuildIndex(root, Path.Combine(root, "Out.ini"));

        var hits = Lookup(index, FormKey.Factory("013BB9:Skyrim.esm"), editorId: "Lydia");
        hits.Count.Should().Be(1, "the same (IniRelPath, RawLine) hit is deduplicated across the two indexes");
    }

    [Fact]
    public void Lookup_EditorId_IsCaseInsensitive()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        File.WriteAllText(Path.Combine(root, "ed.ini"),
            "filterByNpcs=Lydia:headtexture=Other.esp|000801");

        var index = BuildIndex(root, Path.Combine(root, "Out.ini"));

        Lookup(index, FormKey.Factory("00A2C8:Skyrim.esm"), "lydia").Count.Should().Be(1);
        Lookup(index, FormKey.Factory("00A2C8:Skyrim.esm"), "LYDIA").Count.Should().Be(1);
        Lookup(index, FormKey.Factory("00A2C8:Skyrim.esm"), "LyDiA").Count.Should().Be(1);
    }

    [Fact]
    public void Lookup_NullOrEmptyEditorId_DoesNotMatchEditorIdIndex()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        File.WriteAllText(Path.Combine(root, "ed.ini"),
            "filterByNpcs=Lydia:skin=Other.esp|000801");

        var index = BuildIndex(root, Path.Combine(root, "Out.ini"));

        Lookup(index, FormKey.Factory("00A2C8:Skyrim.esm"), editorId: null).Count.Should().Be(0);
        Lookup(index, FormKey.Factory("00A2C8:Skyrim.esm"), editorId: "").Count.Should().Be(0);
    }

    [Fact]
    public void Lookup_NoMatch_ReturnsEmpty()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        File.WriteAllText(Path.Combine(root, "x.ini"),
            "filterByNpcs=Skyrim.esm|013BB9:skin=Other.esp|000801");

        var index = BuildIndex(root, Path.Combine(root, "Out.ini"));

        Lookup(index, FormKey.Factory("0AAAAA:Other.esp"), "Nobody").Count.Should().Be(0);
    }

    [Fact]
    public void AddByEditorId_EmptyEditorId_IsNoOp()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        // Build a real (empty) index, then directly drive the index's add/lookup methods.
        var index = BuildIndex(root, Path.Combine(root, "Out.ini"));

        // Fabricate a hit by lifting one from a populated index (avoids naming the private hit type).
        var hit = MakeHit(root, "Skyrim.esm|013BB9");

        AddByEditorId(index, "", hit);   // empty editorId must be ignored
        Lookup(index, FormKey.Factory("000000:Skyrim.esm"), editorId: "").Count.Should().Be(0);

        // Sanity: a non-empty editorId DOES register and look up (case-insensitively).
        AddByEditorId(index, "somenpc", hit);
        Lookup(index, FormKey.Factory("000000:Skyrim.esm"), editorId: "SOMENPC").Count.Should().Be(1);
    }

    [Fact]
    public void AddByFormKey_ThenLookup_FindsHitByCanonicalKey()
    {
        using var tmp = new TempDir();
        var root = tmp.Dir("npc");
        var index = BuildIndex(root, Path.Combine(root, "Out.ini"));
        var hit = MakeHit(root, "Skyrim.esm|013BB9");

        // The index keys formkeys by FormKeyToSkyPatcherKey: "<plugin lowercase>|<trimmed hex>".
        AddByFormKey(index, "skyrim.esm|13bb9", hit);

        Lookup(index, FormKey.Factory("013BB9:Skyrim.esm"), editorId: null).Count.Should().Be(1);
        // A different FormKey (different canonical key) does not collide.
        Lookup(index, FormKey.Factory("013BBA:Skyrim.esm"), editorId: null).Count.Should().Be(0);
    }

    /// <summary>
    /// Returns a real (private) SkyPatcherHit instance by building a one-line index and reading its
    /// formkey hit back out via Lookup — so tests can re-add it without referencing the private type.
    /// </summary>
    private static object MakeHit(string scratchRoot, string formIdToken)
    {
        var donor = Path.Combine(scratchRoot, "_hitsource");
        Directory.CreateDirectory(donor);
        var ini = Path.Combine(donor, "src.ini");
        File.WriteAllText(ini, $"filterByNpcs={formIdToken}:skin=Other.esp|000801");

        var idx = BuildIndex(donor, Path.Combine(donor, "Out.ini"));
        var parts = formIdToken.Split('|');
        var fk = FormKey.Factory(parts[1].PadLeft(6, '0') + ":" + parts[0]);
        var hits = Lookup(idx, fk, editorId: null);
        hits.Count.Should().BeGreaterThan(0, "the helper line must produce a formkey hit to lift");
        return hits[0]!;
    }

    // ==================================================================
    // FindLooseInModFolders (private static)
    // ==================================================================

    [Fact]
    public void FindLooseInModFolders_NullFolderList_ReturnsNull()
    {
        var mod = new ModSetting { DisplayName = "M" };
        mod.CorrespondingFolderPaths = null!; // method guards against a null list
        FindLooseInModFolders(mod, RelMesh).Should().BeNull();
    }

    [Fact]
    public void FindLooseInModFolders_NoFolderContainsFile_ReturnsNull()
    {
        using var tmp = new TempDir();
        var f1 = tmp.Dir("folderA");
        var f2 = tmp.Dir("folderB");
        var mod = new ModSetting { DisplayName = "M", CorrespondingFolderPaths = new() { f1, f2 } };

        FindLooseInModFolders(mod, RelMesh).Should().BeNull("no folder holds the relative file");
    }

    [Fact]
    public void FindLooseInModFolders_SingleFolderHit_ReturnsThatPath()
    {
        using var tmp = new TempDir();
        var f1 = tmp.Dir("folderA");
        var hit = Path.Combine(f1, RelMesh);
        Directory.CreateDirectory(Path.GetDirectoryName(hit)!);
        File.WriteAllText(hit, "data");

        var mod = new ModSetting { DisplayName = "M", CorrespondingFolderPaths = new() { f1 } };

        FindLooseInModFolders(mod, RelMesh).Should().Be(hit);
    }

    [Fact]
    public void FindLooseInModFolders_PresentInBothFolders_LastFolderWins()
    {
        using var tmp = new TempDir();
        var baseFolder = tmp.Dir("base");
        var overrideFolder = tmp.Dir("override");
        foreach (var f in new[] { baseFolder, overrideFolder })
        {
            var p = Path.Combine(f, RelMesh);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "data");
        }

        // Folders are scanned in REVERSE so the last (override) wins, matching AssetHandler.
        var mod = new ModSetting
        {
            DisplayName = "M",
            CorrespondingFolderPaths = new() { baseFolder, overrideFolder }
        };

        FindLooseInModFolders(mod, RelMesh).Should().Be(Path.Combine(overrideFolder, RelMesh));
    }

    [Fact]
    public void FindLooseInModFolders_OnlyEarlierFolderHasFile_StillFoundAfterReverseScan()
    {
        using var tmp = new TempDir();
        var baseFolder = tmp.Dir("base");
        var overrideFolder = tmp.Dir("override"); // empty
        var p = Path.Combine(baseFolder, RelMesh);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "data");

        var mod = new ModSetting
        {
            DisplayName = "M",
            CorrespondingFolderPaths = new() { baseFolder, overrideFolder }
        };

        FindLooseInModFolders(mod, RelMesh).Should().Be(p);
    }

    // ==================================================================
    // FindLooseFaceGenProviders (instance; reads _settings.ModsFolder)
    // ==================================================================

    /// <summary>Writes <paramref name="bytes"/> at <c>modDir/RelMesh</c> and returns the file path.</summary>
    private static string WriteFaceGen(string modDir, byte[] bytes)
    {
        var p = Path.Combine(modDir, RelMesh);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    [Fact]
    public void FindLooseProviders_BlankModsFolder_ReturnsEmpty()
    {
        var v = BareValidator(new Settings { ModsFolder = "   " });
        var selected = new ModSetting { DisplayName = "Sel" };

        FindLooseFaceGenProviders(v, RelMesh, subjectPath: "anything", selected)
            .Should().BeEmpty();
    }

    [Fact]
    public void FindLooseProviders_MissingModsFolder_ReturnsEmpty()
    {
        using var tmp = new TempDir();
        var modsFolder = Path.Combine(tmp.Path, "does-not-exist");
        var v = BareValidator(new Settings { ModsFolder = modsFolder });
        var selected = new ModSetting { DisplayName = "Sel" };

        FindLooseFaceGenProviders(v, RelMesh, subjectPath: "anything", selected)
            .Should().BeEmpty();
    }

    [Fact]
    public void FindLooseProviders_IdenticalCopyInAnotherMod_IsReportedByFolderName()
    {
        using var tmp = new TempDir();
        var modsFolder = tmp.Dir("Mods");
        var deployedBytes = new byte[] { 1, 2, 3, 4, 5 };

        // The deployed file (subject) lives outside the mods folder.
        var subjectPath = WriteFaceGen(tmp.Dir("DeployedData"), deployedBytes);

        // A culprit mod under Mods/ has a byte-identical copy.
        var culprit = Path.Combine(modsFolder, "CulpritMod");
        Directory.CreateDirectory(culprit);
        WriteFaceGen(culprit, deployedBytes);

        var v = BareValidator(new Settings { ModsFolder = modsFolder });
        var selected = new ModSetting { DisplayName = "Sel" }; // unrelated to the culprit folder

        var providers = FindLooseFaceGenProviders(v, RelMesh, subjectPath, selected);

        providers.Should().ContainSingle().Which.Should().Be("CulpritMod",
            "the provider is named by its top-level mod folder name");
    }

    [Fact]
    public void FindLooseProviders_SelectedModsOwnFolder_IsExcluded()
    {
        using var tmp = new TempDir();
        var modsFolder = tmp.Dir("Mods");
        var bytes = new byte[] { 9, 9, 9 };
        var subjectPath = WriteFaceGen(tmp.Dir("DeployedData"), bytes);

        // The only matching copy is inside the SELECTED mod's own folder → must be skipped
        // (it was already compared upstream; this finder names OTHER providers).
        var selFolder = Path.Combine(modsFolder, "SelectedMod");
        Directory.CreateDirectory(selFolder);
        WriteFaceGen(selFolder, bytes);

        var v = BareValidator(new Settings { ModsFolder = modsFolder });
        var selected = new ModSetting
        {
            DisplayName = "Sel",
            CorrespondingFolderPaths = new() { selFolder }
        };

        FindLooseFaceGenProviders(v, RelMesh, subjectPath, selected).Should().BeEmpty();
    }

    [Fact]
    public void FindLooseProviders_ByteDifferentCopy_IsNotAMatch()
    {
        using var tmp = new TempDir();
        var modsFolder = tmp.Dir("Mods");
        var subjectPath = WriteFaceGen(tmp.Dir("DeployedData"), new byte[] { 1, 2, 3, 4 });

        // Same relative path, same length, but one differing byte → FilesEqual is false.
        var other = Path.Combine(modsFolder, "OtherMod");
        Directory.CreateDirectory(other);
        WriteFaceGen(other, new byte[] { 1, 2, 9, 4 });

        var v = BareValidator(new Settings { ModsFolder = modsFolder });
        var selected = new ModSetting { DisplayName = "Sel" };

        FindLooseFaceGenProviders(v, RelMesh, subjectPath, selected).Should().BeEmpty();
    }

    [Fact]
    public void FindLooseProviders_FolderWithoutTheFile_IsIgnored()
    {
        using var tmp = new TempDir();
        var modsFolder = tmp.Dir("Mods");
        var bytes = new byte[] { 7, 7 };
        var subjectPath = WriteFaceGen(tmp.Dir("DeployedData"), bytes);

        // One mod folder simply doesn't contain the relative path at all.
        Directory.CreateDirectory(Path.Combine(modsFolder, "EmptyMod"));
        // Another contains a matching copy.
        var match = Path.Combine(modsFolder, "MatchMod");
        Directory.CreateDirectory(match);
        WriteFaceGen(match, bytes);

        var v = BareValidator(new Settings { ModsFolder = modsFolder });
        var selected = new ModSetting { DisplayName = "Sel" };

        FindLooseFaceGenProviders(v, RelMesh, subjectPath, selected)
            .Should().ContainSingle().Which.Should().Be("MatchMod");
    }

    [Fact]
    public void FindLooseProviders_CapsAtFiveMatches()
    {
        using var tmp = new TempDir();
        var modsFolder = tmp.Dir("Mods");
        var bytes = new byte[] { 4, 2 };
        var subjectPath = WriteFaceGen(tmp.Dir("DeployedData"), bytes);

        // Seven mods all carry a byte-identical copy; the finder stops after 5.
        for (int i = 0; i < 7; i++)
        {
            var d = Path.Combine(modsFolder, "Mod" + i);
            Directory.CreateDirectory(d);
            WriteFaceGen(d, bytes);
        }

        var v = BareValidator(new Settings { ModsFolder = modsFolder });
        var selected = new ModSetting { DisplayName = "Sel" };

        FindLooseFaceGenProviders(v, RelMesh, subjectPath, selected).Count.Should().Be(5);
    }

    [Fact]
    public void FindLooseProviders_MultipleDistinctCulprits_AllUnderCap_AreAllReported()
    {
        using var tmp = new TempDir();
        var modsFolder = tmp.Dir("Mods");
        var bytes = new byte[] { 5, 5, 5 };
        var subjectPath = WriteFaceGen(tmp.Dir("DeployedData"), bytes);

        var names = new[] { "ModA", "ModB", "ModC" };
        foreach (var n in names)
        {
            var d = Path.Combine(modsFolder, n);
            Directory.CreateDirectory(d);
            WriteFaceGen(d, bytes);
        }

        var v = BareValidator(new Settings { ModsFolder = modsFolder });
        var selected = new ModSetting { DisplayName = "Sel" };

        FindLooseFaceGenProviders(v, RelMesh, subjectPath, selected)
            .Should().BeEquivalentTo(names);
    }
}
