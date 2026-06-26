using System.Collections;
using System.IO;
using System.Reflection;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// Deterministic, pure SkyPatcher parsers / file helpers on <see cref="OutputValidator"/>.
///
/// Every target here is <c>private static</c>, so it is reached through
/// <see cref="Reflect.InvokeStatic{TOwner,T}"/> (no constructed OutputValidator, no live
/// environment, no Skyrim install). The file lives in the Harness namespace because the
/// <see cref="OutputValidator.FilesEqual"/> / <c>SafeFileLength</c> / <c>ParseNpc2SkyPatcherIni</c>
/// coverage reads/writes real temp files (via <see cref="TempDir"/>).
///
/// Covered:
///  - TryParseSkyPatcherFormKey  (out FormKey, bool return) — "Plugin.esp|hexid" → FormKey.
///  - NormalizeSkyPatcherFormId  — lowercase / leading-zero trim / all-zero→"0" / null guards.
///  - FormKeyToSkyPatcherKey     — FormKey → "plugin|trimmedhex" + round-trip vs NormalizeSkyPatcherFormId.
///  - MakeSortKey                — relative-path, lowercased, backslash-normalized sort key.
///  - StripHeadPartPrefix        — "eid:" / "fk:" prefix stripping.
///  - FilesEqual                 — byte-for-byte equality (identical / 1-byte diff / length diff /
///                                 missing side / both-empty / >64KB buffer-boundary).
///  - SafeFileLength             — length, missing → -1.
///  - ParseNpc2SkyPatcherIni     — .ini → recipient-key map (private value type reflected generically).
///  - TryParseBroadFilterRule    — bool return asserted; rule.Clauses count reflected generically.
///
/// NOTE: BuildSkyPatcherIndex / MatchesBroadFilter / EvaluateClause / the per-NPC Validate* flow
/// are NOT covered here — they require a constructed OutputValidator (heavy ctor:
/// EnvironmentStateProvider / RecordHandler / BsaHandler / FaceGenConsistencyAnalyzer) and a live
/// load order, which belongs to the integration wave.
/// </summary>
public class OutputValidatorParsersTests
{
    // ------------------------------------------------------------------
    // Reflection shims for the private statics under test.
    // ------------------------------------------------------------------

    private static bool TryParseSkyPatcherFormKey(string token, out FormKey fk)
    {
        // Out-param: Reflect.InvokeStatic forwards the same args array to MethodInfo.Invoke,
        // which writes the resolved out value back into args[1] after the call.
        var args = new object?[] { token, default(FormKey) };
        var ok = Reflect.InvokeStatic<OutputValidator, bool>("TryParseSkyPatcherFormKey", args);
        fk = (FormKey)args[1]!;
        return ok;
    }

    private static string? NormalizeSkyPatcherFormId(string? token) =>
        Reflect.InvokeStatic<OutputValidator, string?>("NormalizeSkyPatcherFormId", token);

    private static string FormKeyToSkyPatcherKey(FormKey fk) =>
        Reflect.InvokeStatic<OutputValidator, string>("FormKeyToSkyPatcherKey", fk)!;

    private static string MakeSortKey(string root, string iniPath) =>
        Reflect.InvokeStatic<OutputValidator, string>("MakeSortKey", root, iniPath)!;

    private static string StripHeadPartPrefix(string token) =>
        Reflect.InvokeStatic<OutputValidator, string>("StripHeadPartPrefix", token)!;

    private static bool FilesEqual(string a, string b) =>
        Reflect.InvokeStatic<OutputValidator, bool>("FilesEqual", a, b);

    private static long SafeFileLength(string path) =>
        Reflect.InvokeStatic<OutputValidator, long>("SafeFileLength", path);

    private static IDictionary ParseNpc2SkyPatcherIni(string path) =>
        (IDictionary)Reflect.InvokeStatic<OutputValidator, object>("ParseNpc2SkyPatcherIni", path)!;

    private static bool TryParseBroadFilterRule(string line, out object? rule)
    {
        var args = new object?[] { line, "rel.ini", "rel.ini", false, null };
        var ok = Reflect.InvokeStatic<OutputValidator, bool>("TryParseBroadFilterRule", args);
        rule = args[4];
        return ok;
    }

    private static T FieldOf<T>(object target, string name)
    {
        var f = target.GetType().GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        f.Should().NotBeNull($"the private nested type should expose field '{name}'");
        return (T)f!.GetValue(target)!;
    }

    // ------------------------------------------------------------------
    // TryParseSkyPatcherFormKey
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseFormKey_WellFormed_PadsHexAndBuildsFormKey()
    {
        TryParseSkyPatcherFormKey("Mod.esp|1234", out var fk).Should().BeTrue();
        fk.Should().Be(FormKey.Factory("001234:Mod.esp"));
    }

    [Fact]
    public void TryParseFormKey_FullSixDigitHex_Preserved()
    {
        TryParseSkyPatcherFormKey("Mod.esp|0A0F12", out var fk).Should().BeTrue();
        fk.Should().Be(FormKey.Factory("0A0F12:Mod.esp"));
    }

    [Fact]
    public void TryParseFormKey_OverlongHex_KeepsLastSixChars()
    {
        // A load-order-prefixed FormID (e.g. "FE001234...") is trimmed to its last 6 hex chars.
        TryParseSkyPatcherFormKey("Mod.esp|01000ABC", out var fk).Should().BeTrue();
        fk.Should().Be(FormKey.Factory("000ABC:Mod.esp"));
    }

    [Fact]
    public void TryParseFormKey_TrimsWhitespaceAroundParts()
    {
        TryParseSkyPatcherFormKey("  Mod.esp  |  001234  ", out var fk).Should().BeTrue();
        fk.Should().Be(FormKey.Factory("001234:Mod.esp"));
    }

    [Theory]
    [InlineData("")]                 // empty
    [InlineData("   ")]              // whitespace only
    [InlineData("Mod.esp")]          // no pipe
    [InlineData("Mod.esp|12|34")]    // too many pipes
    [InlineData("|001234")]          // empty plugin
    [InlineData("Mod.esp|")]         // empty id
    [InlineData("Mod.esp|ZZZZ")]     // non-hex id → FormKey.Factory throws → false
    public void TryParseFormKey_Malformed_ReturnsFalse(string token)
    {
        TryParseSkyPatcherFormKey(token, out var fk).Should().BeFalse();
        fk.Should().Be(default(FormKey));
    }

    // ------------------------------------------------------------------
    // NormalizeSkyPatcherFormId
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Mod.esp|0A0001", "mod.esp|a0001")] // lowercases plugin + id, trims leading zeros
    [InlineData("MOD.ESP|001234", "mod.esp|1234")]
    [InlineData("Mod.esp|00ABCD", "mod.esp|abcd")]
    [InlineData("  Mod.esp  |  001234  ", "mod.esp|1234")] // trims surrounding whitespace
    public void NormalizeFormId_LowercasesAndTrimsLeadingZeros(string token, string expected)
    {
        NormalizeSkyPatcherFormId(token).Should().Be(expected);
    }

    [Fact]
    public void NormalizeFormId_AllZeroId_BecomesZero()
    {
        NormalizeSkyPatcherFormId("Mod.esp|000000").Should().Be("mod.esp|0");
        NormalizeSkyPatcherFormId("Mod.esp|0").Should().Be("mod.esp|0");
    }

    [Theory]
    [InlineData("Mod.esp")]        // no pipe
    [InlineData("Mod.esp|12|34")]  // too many pipes
    [InlineData("|001234")]        // empty plugin → null
    public void NormalizeFormId_Malformed_ReturnsNull(string token)
    {
        NormalizeSkyPatcherFormId(token).Should().BeNull();
    }

    [Fact]
    public void NormalizeFormId_EmptyIdAfterTrim_BecomesZeroNotNull()
    {
        // "|0".TrimStart('0') == "" → coerced to "0"; plugin present so the result is non-null.
        NormalizeSkyPatcherFormId("Mod.esp|0").Should().Be("mod.esp|0");
    }

    // ------------------------------------------------------------------
    // FormKeyToSkyPatcherKey (+ round-trip with NormalizeSkyPatcherFormId)
    // ------------------------------------------------------------------

    [Fact]
    public void FormKeyToKey_LowercasesPluginAndTrimsHex()
    {
        FormKeyToSkyPatcherKey(FormKey.Factory("0A0001:Mod.esp")).Should().Be("mod.esp|a0001");
        FormKeyToSkyPatcherKey(FormKey.Factory("001234:Mod.esp")).Should().Be("mod.esp|1234");
    }

    [Fact]
    public void FormKeyToKey_NullFormId_BecomesZero()
    {
        FormKeyToSkyPatcherKey(FormKey.Factory("000000:Mod.esp")).Should().Be("mod.esp|0");
    }

    [Theory]
    [InlineData("0A0001:Mod.esp")]
    [InlineData("001234:Mod.esp")]
    [InlineData("000000:Mod.esp")]
    [InlineData("0ABCDE:Mod.esp")]
    public void FormKeyToKey_RoundTripsThroughNormalizeFormId(string fkText)
    {
        var fk = FormKey.Factory(fkText);
        // The canonical key produced from a FormKey must equal the normalization of the
        // SkyPatcher form token for that same FormKey ("Plugin.esp|HEXID").
        var token = $"{fk.ModKey.FileName}|{fk.ID:X6}";
        FormKeyToSkyPatcherKey(fk).Should().Be(NormalizeSkyPatcherFormId(token));
    }

    // ------------------------------------------------------------------
    // MakeSortKey
    // ------------------------------------------------------------------

    [Fact]
    public void MakeSortKey_RelativeToRoot_LowercasedBackslashNormalized()
    {
        var root = Path.Combine("C:", "Data", "SkyPatcher", "npc");
        var ini = Path.Combine(root, "ZZZ Author", "MyConfig.INI");

        var key = MakeSortKey(root, ini);

        key.Should().Be(@"zzz author\myconfig.ini");
    }

    [Fact]
    public void MakeSortKey_FileDirectlyInRoot_IsJustTheFileName()
    {
        var root = Path.Combine("C:", "Data", "SkyPatcher", "npc");
        var ini = Path.Combine(root, "Foo.ini");

        MakeSortKey(root, ini).Should().Be("foo.ini");
    }

    [Fact]
    public void MakeSortKey_ApproximatesAlphanumericLoadOrder()
    {
        var root = Path.Combine("C:", "npc");
        var a = MakeSortKey(root, Path.Combine(root, "AAA", "x.ini"));
        var z = MakeSortKey(root, Path.Combine(root, "ZZZ", "x.ini"));

        string.Compare(a, z, StringComparison.OrdinalIgnoreCase).Should().BeNegative(
            "earlier-loading configs sort before later-loading ones");
    }

    // ------------------------------------------------------------------
    // StripHeadPartPrefix
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("eid:HairFemale01", "HairFemale01")]
    [InlineData("fk:001234:Mod.esp", "001234:Mod.esp")]
    [InlineData("HairFemale01", "HairFemale01")]   // no recognised prefix → unchanged
    [InlineData("", "")]                            // empty → unchanged
    [InlineData("EID:Upper", "EID:Upper")]          // case-sensitive prefix → not stripped
    [InlineData("eid:", "")]                         // prefix only → empty remainder
    public void StripHeadPartPrefix_RemovesKnownPrefixesOnly(string token, string expected)
    {
        StripHeadPartPrefix(token).Should().Be(expected);
    }

    [Fact]
    public void StripHeadPartPrefix_OnlyStripsLeadingPrefix_NotMidString()
    {
        StripHeadPartPrefix("eid:foo:eid:bar").Should().Be("foo:eid:bar");
    }

    // ------------------------------------------------------------------
    // FilesEqual
    // ------------------------------------------------------------------

    [Fact]
    public void FilesEqual_IdenticalContent_True()
    {
        using var tmp = new TempDir();
        var a = tmp.WriteText("a.bin", "hello world");
        var b = tmp.WriteText("b.bin", "hello world");
        FilesEqual(a, b).Should().BeTrue();
    }

    [Fact]
    public void FilesEqual_SingleByteDifference_SameLength_False()
    {
        using var tmp = new TempDir();
        var a = tmp.Combine("a.bin");
        var b = tmp.Combine("b.bin");
        File.WriteAllBytes(a, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(b, new byte[] { 1, 2, 9, 4 });
        FilesEqual(a, b).Should().BeFalse();
    }

    [Fact]
    public void FilesEqual_DifferentLength_False()
    {
        using var tmp = new TempDir();
        var a = tmp.Combine("a.bin");
        var b = tmp.Combine("b.bin");
        File.WriteAllBytes(a, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(b, new byte[] { 1, 2, 3 });
        FilesEqual(a, b).Should().BeFalse();
    }

    [Fact]
    public void FilesEqual_FirstExists_SecondMissing_False()
    {
        using var tmp = new TempDir();
        var a = tmp.WriteText("a.bin", "x");
        var missing = tmp.Combine("nope.bin"); // parents created, but file not written
        File.Exists(missing).Should().BeFalse();
        FilesEqual(a, missing).Should().BeFalse();
    }

    [Fact]
    public void FilesEqual_BothMissing_False()
    {
        using var tmp = new TempDir();
        FilesEqual(tmp.Combine("p.bin"), tmp.Combine("q.bin")).Should().BeFalse();
    }

    [Fact]
    public void FilesEqual_BothEmpty_True()
    {
        using var tmp = new TempDir();
        var a = tmp.Combine("a.bin");
        var b = tmp.Combine("b.bin");
        File.WriteAllBytes(a, Array.Empty<byte>());
        File.WriteAllBytes(b, Array.Empty<byte>());
        // Equal length (0) and the read loop never executes → trivially equal.
        FilesEqual(a, b).Should().BeTrue();
    }

    [Fact]
    public void FilesEqual_LargerThanBuffer_IdenticalAcrossChunks_True()
    {
        using var tmp = new TempDir();
        var data = new byte[64 * 1024 + 777]; // spans more than one 64KB read
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);
        var a = tmp.Combine("a.bin");
        var b = tmp.Combine("b.bin");
        File.WriteAllBytes(a, data);
        File.WriteAllBytes(b, data);
        FilesEqual(a, b).Should().BeTrue();
    }

    [Fact]
    public void FilesEqual_LargerThanBuffer_DiffInSecondChunk_False()
    {
        using var tmp = new TempDir();
        var data = new byte[64 * 1024 + 777];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);
        var other = (byte[])data.Clone();
        other[64 * 1024 + 100] ^= 0xFF; // flip a byte past the first buffer boundary
        var a = tmp.Combine("a.bin");
        var b = tmp.Combine("b.bin");
        File.WriteAllBytes(a, data);
        File.WriteAllBytes(b, other);
        FilesEqual(a, b).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // SafeFileLength
    // ------------------------------------------------------------------

    [Fact]
    public void SafeFileLength_ExistingFile_ReturnsByteLength()
    {
        using var tmp = new TempDir();
        var p = tmp.Combine("a.bin");
        File.WriteAllBytes(p, new byte[] { 1, 2, 3, 4, 5 });
        SafeFileLength(p).Should().Be(5);
    }

    [Fact]
    public void SafeFileLength_EmptyFile_ReturnsZero()
    {
        using var tmp = new TempDir();
        var p = tmp.Combine("a.bin");
        File.WriteAllBytes(p, Array.Empty<byte>());
        SafeFileLength(p).Should().Be(0);
    }

    [Fact]
    public void SafeFileLength_MissingFile_ReturnsNegativeOne()
    {
        using var tmp = new TempDir();
        SafeFileLength(tmp.Combine("nope.bin")).Should().Be(-1);
    }

    // ------------------------------------------------------------------
    // ParseNpc2SkyPatcherIni
    // ------------------------------------------------------------------

    [Fact]
    public void ParseIni_MissingFile_ReturnsEmptyMap()
    {
        using var tmp = new TempDir();
        ParseNpc2SkyPatcherIni(tmp.Combine("does-not-exist.ini")).Count.Should().Be(0);
    }

    [Fact]
    public void ParseIni_VisualTransferLine_MapsRecipientToSurrogate()
    {
        using var tmp = new TempDir();
        var ini = tmp.WriteText("npc2.ini",
            "filterByNpcs=Skyrim.esm|013BB9:copyVisualStyle=MyOutput.esp|000801,height=1.0");

        var map = ParseNpc2SkyPatcherIni(ini);

        map.Count.Should().Be(1);
        // The recipient key is the normalized form of the filter token.
        map.Contains("skyrim.esm|13bb9").Should().BeTrue();
        var entry = map["skyrim.esm|13bb9"]!;
        FieldOf<bool>(entry, "HasSurrogate").Should().BeTrue();
        FieldOf<FormKey>(entry, "Surrogate").Should().Be(FormKey.Factory("000801:MyOutput.esp"));
        FieldOf<string>(entry, "RawLine").Should().Contain("copyVisualStyle");
    }

    [Fact]
    public void ParseIni_LineWithoutCopyVisualStyle_HasNoSurrogate()
    {
        using var tmp = new TempDir();
        var ini = tmp.WriteText("npc2.ini",
            "filterByNpcs=Skyrim.esm|013BB9:skin=MyOutput.esp|000801,height=1.0");

        var map = ParseNpc2SkyPatcherIni(ini);

        var entry = map["skyrim.esm|13bb9"]!;
        FieldOf<bool>(entry, "HasSurrogate").Should().BeFalse();
        FieldOf<FormKey>(entry, "Surrogate").Should().Be(default(FormKey));
    }

    [Fact]
    public void ParseIni_MultipleRecipients_AllMapToSameEntry()
    {
        using var tmp = new TempDir();
        var ini = tmp.WriteText("npc2.ini",
            "filterByNpcs=Skyrim.esm|013BB9,Skyrim.esm|000ABC:copyVisualStyle=Out.esp|000801");

        var map = ParseNpc2SkyPatcherIni(ini);

        map.Count.Should().Be(2);
        map.Contains("skyrim.esm|13bb9").Should().BeTrue();
        map.Contains("skyrim.esm|abc").Should().BeTrue();
        // Both recipient keys point at the one parsed line (same surrogate).
        var a = map["skyrim.esm|13bb9"]!;
        var b = map["skyrim.esm|abc"]!;
        FieldOf<FormKey>(a, "Surrogate").Should().Be(FieldOf<FormKey>(b, "Surrogate"));
    }

    [Fact]
    public void ParseIni_FilterByNpcsFormIdAlias_IsAccepted()
    {
        using var tmp = new TempDir();
        var ini = tmp.WriteText("npc2.ini",
            "filterByNpcsFormID=Skyrim.esm|013BB9:copyVisualStyle=Out.esp|000801");

        var map = ParseNpc2SkyPatcherIni(ini);

        map.Contains("skyrim.esm|13bb9").Should().BeTrue();
    }

    [Fact]
    public void ParseIni_CommentsBlankLinesAndNonNpcFilters_AreIgnored()
    {
        using var tmp = new TempDir();
        var ini = tmp.WriteText("npc2.ini", string.Join("\n", new[]
        {
            "; a comment line",
            "",
            "   ",
            "filterByFactions=Skyrim.esm|0AAAAA:copyVisualStyle=Out.esp|000801", // broad filter, not filterByNpcs
            "noColonHereSoIgnored",
            "filterByNpcs=Skyrim.esm|013BB9:copyVisualStyle=Out.esp|000801",      // the only mapped line
        }));

        var map = ParseNpc2SkyPatcherIni(ini);

        map.Count.Should().Be(1);
        map.Contains("skyrim.esm|13bb9").Should().BeTrue();
    }

    [Fact]
    public void ParseIni_CaseInsensitiveKeyLookup()
    {
        using var tmp = new TempDir();
        var ini = tmp.WriteText("npc2.ini",
            "filterByNpcs=Skyrim.esm|013BB9:copyVisualStyle=Out.esp|000801");

        var map = ParseNpc2SkyPatcherIni(ini);

        // The map is built with an OrdinalIgnoreCase comparer.
        map.Contains("SKYRIM.ESM|13BB9").Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // TryParseBroadFilterRule
    // ------------------------------------------------------------------

    [Fact]
    public void BroadFilterRule_GenderFemaleVisualLine_ParsesOneClause()
    {
        TryParseBroadFilterRule("filterByGender=Female:skin=Out.esp|000801", out var rule)
            .Should().BeTrue();
        rule.Should().NotBeNull();
        var clauses = FieldOf<IList>(rule!, "Clauses");
        clauses.Count.Should().Be(1);
    }

    [Fact]
    public void BroadFilterRule_RaceAndFaction_ParsesTwoClauses()
    {
        TryParseBroadFilterRule(
                "filterByRaces=Skyrim.esm|013746:filterByFactions=Skyrim.esm|0AAAAA:race=Out.esp|000801",
                out var rule)
            .Should().BeTrue();
        var clauses = FieldOf<IList>(rule!, "Clauses");
        clauses.Count.Should().Be(2);
    }

    [Fact]
    public void BroadFilterRule_ExcludedDimension_StillParses()
    {
        TryParseBroadFilterRule(
                "filterByRacesExcluded=Skyrim.esm|013746:headparts=Out.esp|000801",
                out var rule)
            .Should().BeTrue();
        FieldOf<IList>(rule!, "Clauses").Count.Should().Be(1);
    }

    [Fact]
    public void BroadFilterRule_UnknownDimension_ReturnsFalse()
    {
        // An unrecognised filterBy* dimension makes the whole line un-evaluable.
        TryParseBroadFilterRule("filterByActorValue=Health|50:skin=Out.esp|000801", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void BroadFilterRule_MalformedFormToken_ReturnsFalse()
    {
        // A FormID-based dimension with an unparseable token cannot be honestly evaluated.
        TryParseBroadFilterRule("filterByRaces=NotAFormToken:skin=Out.esp|000801", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void BroadFilterRule_BadGenderValue_ReturnsFalse()
    {
        TryParseBroadFilterRule("filterByGender=Other:skin=Out.esp|000801", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void BroadFilterRule_NoFilterClausesOnlyActions_ReturnsFalse()
    {
        // A line with only action directives and no filterBy* clause is not a broad-filter rule.
        TryParseBroadFilterRule("skin=Out.esp|000801,height=1.0", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void BroadFilterRule_ModNameClause_ParsesWithoutFormToken()
    {
        // filterByModNames takes bare plugin names (not Plugin|hexid), so it must parse cleanly.
        TryParseBroadFilterRule("filterByModNames=3DNPC.esp:skin=Out.esp|000801", out var rule)
            .Should().BeTrue();
        FieldOf<IList>(rule!, "Clauses").Count.Should().Be(1);
    }
}
