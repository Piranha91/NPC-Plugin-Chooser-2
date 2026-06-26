using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="JSONhandler{T}"/> — the static Newtonsoft wrapper used to persist NPC2 models.
/// This file covers the parts of the wrapper that the model-shape round-trip suite
/// (<c>SerializableModelsRoundTripTests</c>) deliberately leaves untested: the serializer
/// settings produced by <c>GetMyMutagenJSONSettings</c> (converter registration, indented
/// formatting, replace-on-create, freshness per call), the success/exception out-params on
/// <c>Deserialize</c> for null/empty/malformed input, the <c>[JsonIgnore]</c> static tooltips
/// on <see cref="ModSetting"/>, <c>CloneViaJSON</c> independence guarantees, and the file
/// round-trip (<c>SaveJSONFile</c>/<c>LoadJSONFile</c>) including parent-dir creation and the
/// missing/invalid-path error messages. Pure logic + temp files — no clock, no network, no game.
/// </summary>
public class JsonHandlerTests
{
    private static readonly FormKey Npc1 = FormKey.Factory("000801:Skyrim.esm");
    private static readonly FormKey Npc2 = FormKey.Factory("00ABCD:Skyrim.esm");
    private static readonly FormKey Npc3 = FormKey.Factory("0F1234:Dawnguard.esm");
    private static readonly ModKey ModA = ModKey.FromFileName("ModA.esp");
    private static readonly ModKey ModB = ModKey.FromFileName("ModB.esm");
    private static readonly ModKey ModC = ModKey.FromFileName("ModC.esl");

    // ── GetMyMutagenJSONSettings ────────────────────────────────────────────────

    [Fact]
    public void GetMyMutagenJSONSettings_HasIndentedFormattingAndReplaceCreation()
    {
        var settings = JSONhandler<ModSetting>.GetMyMutagenJSONSettings();

        settings.Formatting.Should().Be(Formatting.Indented);
        settings.ObjectCreationHandling.Should().Be(ObjectCreationHandling.Replace);
    }

    [Fact]
    public void GetMyMutagenJSONSettings_RegistersStringEnumAndModSettingConverters()
    {
        var settings = JSONhandler<ModSetting>.GetMyMutagenJSONSettings();

        // The two converters the wrapper adds explicitly, on top of AddMutagenConverters().
        settings.Converters.Should().ContainSingle(c => c is StringEnumConverter);
        settings.Converters.Should().ContainSingle(c => c is ModSettingConverter);
    }

    [Fact]
    public void GetMyMutagenJSONSettings_RegistersMutagenConverters_BeyondTheTwoExplicitOnes()
    {
        var settings = JSONhandler<ModSetting>.GetMyMutagenJSONSettings();

        // AddMutagenConverters() contributes the FormKey/ModKey (and friends) converters,
        // so the total must exceed the two converters the wrapper itself appends.
        settings.Converters.Count.Should().BeGreaterThan(2,
            "AddMutagenConverters registers the FormKey/ModKey converters in addition to the two explicit ones");
    }

    [Fact]
    public void GetMyMutagenJSONSettings_ReturnsFreshInstanceEachCall()
    {
        var a = JSONhandler<ModSetting>.GetMyMutagenJSONSettings();
        var b = JSONhandler<ModSetting>.GetMyMutagenJSONSettings();

        // A fresh settings object (with its own converter collection) per call: callers may
        // hand the result to Newtonsoft, which mutates internal state, so sharing would be a bug.
        a.Should().NotBeSameAs(b);
        a.Converters.Should().NotBeSameAs(b.Converters);
    }

    [Fact]
    public void GetMyMutagenJSONSettings_IsGenericInstantiationIndependent()
    {
        // The settings are produced identically regardless of the closing type argument
        // (the method is static on an open generic, but does not depend on T).
        var fromModSetting = JSONhandler<ModSetting>.GetMyMutagenJSONSettings();
        var fromNpcToken = JSONhandler<NpcToken>.GetMyMutagenJSONSettings();

        fromModSetting.Converters.Count.Should().Be(fromNpcToken.Converters.Count);
        fromModSetting.Formatting.Should().Be(fromNpcToken.Formatting);
        fromModSetting.ObjectCreationHandling.Should().Be(fromNpcToken.ObjectCreationHandling);
    }

    // ── Serialize formatting & encoding ─────────────────────────────────────────

    [Fact]
    public void Serialize_ProducesIndentedJson()
    {
        var json = JSONhandler<NpcToken>.Serialize(new NpcToken { CreationDate = "x" }, out var ok, out _);
        ok.Should().BeTrue();
        // Indented output contains newlines; a compact serializer would not.
        json.Should().Contain("\n");
    }

    [Fact]
    public void Serialize_FormKey_UsesColonString_ModKey_UsesBareFilename()
    {
        var token = new NpcToken
        {
            CreatedPlugins = new List<ModKey> { ModA },
            ProcessedNpcs = new Dictionary<FormKey, NpcAppearanceData>
            {
                [Npc1] = new() { ModName = "A", AppearancePlugin = ModA },
            },
        };
        var json = JSONhandler<NpcToken>.Serialize(token, out var ok, out _);
        ok.Should().BeTrue();

        json.Should().Contain("000801:Skyrim.esm", "FormKey serializes to its hex:plugin colon form");
        json.Should().Contain("ModA.esp", "ModKey serializes to its bare plugin filename");
    }

    [Fact]
    public void Serialize_EnumWrittenAsName_NotInteger()
    {
        var setting = new ModSetting { ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.IncludeAsNew };
        var json = JSONhandler<ModSetting>.Serialize(setting, out var ok, out _);

        ok.Should().BeTrue();
        json.Should().Contain("IncludeAsNew");
        json.Should().NotContain("\"ModRecordOverrideHandlingMode\": 2");
    }

    [Fact]
    public void Serialize_StaticJsonIgnoredTooltips_AreNotEmitted()
    {
        // DefaultRecordInjectionToolTip / DefaultMergeInTooltip are [JsonIgnore] static members,
        // so their *static* property names must never appear as JSON keys. (The non-static
        // instance tooltips that hold their default text are still emitted.)
        var json = JSONhandler<ModSetting>.Serialize(new ModSetting(), out var ok, out _);

        ok.Should().BeTrue();
        json.Should().NotContain("\"DefaultRecordInjectionToolTip\"");
        json.Should().NotContain("\"DefaultMergeInTooltip\"");
    }

    // ── Deserialize success / exception out-params ──────────────────────────────

    [Fact]
    public void Deserialize_LiteralNull_DefaultsToFailureWithMessage()
    {
        // canBeNull defaults to false: a JSON "null" payload deserializes to a null object,
        // which the wrapper reports as a failure with an explanatory message. (Uses NpcToken,
        // which has no value-converter; ModSetting's ModSettingConverter instead throws on a
        // null token and is covered as the malformed-input path below.)
        var result = JSONhandler<NpcToken>.Deserialize("null", out var ok, out var ex);

        result.Should().BeNull();
        ok.Should().BeFalse();
        ex.Should().Be("JSON object was null");
    }

    [Fact]
    public void Deserialize_LiteralNull_WithCanBeNull_IsSuccess()
    {
        // With canBeNull:true the null payload is accepted (no error message).
        var result = JSONhandler<NpcToken>.Deserialize("null", out var ok, out var ex, canBeNull: true);

        result.Should().BeNull();
        ok.Should().BeTrue();
        ex.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_EmptyString_IsNullAndFailsWhenNotNullable()
    {
        // Newtonsoft treats an empty string as a null result rather than throwing.
        var result = JSONhandler<ModSetting>.Deserialize("", out var ok, out var ex);

        result.Should().BeNull();
        ok.Should().BeFalse();
        ex.Should().Be("JSON object was null");
    }

    [Fact]
    public void Deserialize_EmptyString_WithCanBeNull_IsSuccess()
    {
        var result = JSONhandler<ModSetting>.Deserialize("", out var ok, out var ex, canBeNull: true);

        result.Should().BeNull();
        ok.Should().BeTrue();
        ex.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_MalformedJson_ReportsFailureWithNonEmptyExceptionStack()
    {
        // A syntactically broken payload throws inside Newtonsoft; the wrapper catches it,
        // sets success=false and surfaces the exception stack string (never throws to caller).
        var result = JSONhandler<ModSetting>.Deserialize("{ this is not json", out var ok, out var ex);

        result.Should().BeNull();
        ok.Should().BeFalse();
        ex.Should().NotBeNullOrWhiteSpace();
        ex.Should().NotBe("JSON object was null", "a parse error must surface the exception, not the null sentinel");
    }

    [Fact]
    public void Deserialize_WrongShapeForFormKey_ReportsFailure()
    {
        // A value that the FormKey converter cannot parse must be caught and reported,
        // not propagated as an exception.
        const string bad = """
        {
          "ProcessedNpcs": { "not-a-valid-formkey": { "ModName": "x" } }
        }
        """;
        var result = JSONhandler<NpcToken>.Deserialize(bad, out var ok, out var ex);

        result.Should().BeNull();
        ok.Should().BeFalse();
        ex.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Deserialize_ValidPayload_SetsSuccessAndEmptyException()
    {
        var json = JSONhandler<NpcToken>.Serialize(
            new NpcToken { CreationDate = "2026-06-26" }, out _, out _);

        var clone = JSONhandler<NpcToken>.Deserialize(json, out var ok, out var ex);

        ok.Should().BeTrue();
        ex.Should().BeEmpty();
        clone.Should().NotBeNull();
        clone!.CreationDate.Should().Be("2026-06-26");
    }

    // ── CloneViaJSON independence ───────────────────────────────────────────────

    [Fact]
    public void CloneViaJSON_EmptyModSetting_KeepsCollectionsNonNullAndEmpty()
    {
        // ObjectCreationHandling.Replace replaces the default-constructed collections with
        // the (empty) deserialized ones; they must remain non-null empties, never null.
        var clone = JSONhandler<ModSetting>.CloneViaJSON(new ModSetting());

        clone.Should().NotBeNull();
        clone!.CorrespondingModKeys.Should().NotBeNull().And.BeEmpty();
        clone.ResourceOnlyModKeys.Should().NotBeNull().And.BeEmpty();
        clone.NpcFormKeys.Should().NotBeNull().And.BeEmpty();
        clone.AvailablePluginsForNpcs.Should().NotBeNull().And.BeEmpty();
        clone.NpcFormKeysToNotifications.Should().NotBeNull().And.BeEmpty();
        clone.Keywords.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void CloneViaJSON_PreservesFormKeyKeyedDictionary()
    {
        var token = new NpcToken
        {
            ProcessedNpcs = new Dictionary<FormKey, NpcAppearanceData>
            {
                [Npc1] = new() { ModName = "A", AppearancePlugin = ModA },
                [Npc2] = new() { ModName = "B", AppearancePlugin = ModB },
                [Npc3] = new() { ModName = "C", AppearancePlugin = ModC },
            },
        };
        var clone = JSONhandler<NpcToken>.CloneViaJSON(token);

        clone.Should().NotBeNull();
        // FormKey dictionary keys round-trip as real FormKeys (deep value equality, not string).
        clone!.ProcessedNpcs.Keys.Should().BeEquivalentTo(new[] { Npc1, Npc2, Npc3 });
        clone.ProcessedNpcs[Npc2].ModName.Should().Be("B");
        clone.ProcessedNpcs[Npc3].AppearancePlugin.Should().Be(ModC);
    }

    [Fact]
    public void CloneViaJSON_IsReferenceDistinctWithIndependentCollections()
    {
        var original = new ModSetting
        {
            DisplayName = "Indep",
            CorrespondingModKeys = new List<ModKey> { ModA, ModB },
            NpcFormKeys = new HashSet<FormKey> { Npc1 },
            NpcFormKeysToNotifications =
                new Dictionary<FormKey, (NpcIssueType, string, FormKey?)>
                {
                    [Npc1] = (NpcIssueType.Template, "msg", Npc2),
                },
        };
        var clone = JSONhandler<ModSetting>.CloneViaJSON(original);

        clone.Should().NotBeNull();
        clone.Should().NotBeSameAs(original);
        clone!.CorrespondingModKeys.Should().NotBeSameAs(original.CorrespondingModKeys);
        clone.NpcFormKeys.Should().NotBeSameAs(original.NpcFormKeys);
        clone.NpcFormKeysToNotifications.Should().NotBeSameAs(original.NpcFormKeysToNotifications);

        // Mutating the clone leaves the original intact.
        clone.CorrespondingModKeys.Add(ModC);
        clone.NpcFormKeys.Add(Npc3);
        original.CorrespondingModKeys.Should().Equal(ModA, ModB);
        original.NpcFormKeys.Should().BeEquivalentTo(new[] { Npc1 });

        // And the nullable FormKey tuple element survives the clone.
        clone.NpcFormKeysToNotifications[Npc1].ReferencedFormKey.Should().Be(Npc2);
    }

    [Fact]
    public void CloneViaJSON_NullInput_WithoutCanBeNull_ReturnsNull()
    {
        // Serialize(null) -> "null"; Deserialize("null") with canBeNull=false yields default(T).
        var clone = JSONhandler<NpcToken>.CloneViaJSON(null!);
        clone.Should().BeNull();
    }

    [Fact]
    public void CloneViaJSON_NullInput_WithCanBeNull_ReturnsNull()
    {
        var clone = JSONhandler<NpcToken>.CloneViaJSON(null!, canBeNull: true);
        clone.Should().BeNull();
    }

    // ── SaveJSONFile / LoadJSONFile round-trip ──────────────────────────────────

    [Fact]
    public void SaveAndLoad_RoundTripsEqualGraph()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("token.json");

        var token = new NpcToken
        {
            CreationDate = "2026-06-26",
            CreatedPlugins = new List<ModKey> { ModA, ModB },
            ProcessedNpcs = new Dictionary<FormKey, NpcAppearanceData>
            {
                [Npc1] = new() { ModName = "A", AppearancePlugin = ModA, OutputPlugin = ModB },
            },
        };

        JSONhandler<NpcToken>.SaveJSONFile(token, path, out var saveOk, out var saveErr);
        saveOk.Should().BeTrue("save should succeed: {0}", saveErr);
        System.IO.File.Exists(path).Should().BeTrue();

        var loaded = JSONhandler<NpcToken>.LoadJSONFile(path, out var loadOk, out var loadErr);
        loadOk.Should().BeTrue("load should succeed: {0}", loadErr);
        loaded.Should().NotBeNull();
        loaded!.CreationDate.Should().Be("2026-06-26");
        loaded.CreatedPlugins.Should().Equal(ModA, ModB);
        loaded.ProcessedNpcs[Npc1].AppearancePlugin.Should().Be(ModA);
        loaded.ProcessedNpcs[Npc1].OutputPlugin.Should().Be(ModB);
    }

    [Fact]
    public void SaveJSONFile_CreatesMissingParentDirectories()
    {
        using var tmp = new TempDir();
        // Nested path whose parent directories do not yet exist.
        var path = System.IO.Path.Combine(tmp.Path, "a", "b", "c", "settings.json");
        System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(path)!).Should().BeFalse();

        JSONhandler<ModSetting>.SaveJSONFile(
            new ModSetting { DisplayName = "Deep" }, path, out var ok, out var err);

        ok.Should().BeTrue("save should create parent dirs: {0}", err);
        System.IO.File.Exists(path).Should().BeTrue();

        var loaded = JSONhandler<ModSetting>.LoadJSONFile(path, out var loadOk, out _);
        loadOk.Should().BeTrue();
        loaded!.DisplayName.Should().Be("Deep");
    }

    [Fact]
    public void SaveJSONFile_WritesIndentedContent()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("indented.json");

        JSONhandler<NpcToken>.SaveJSONFile(
            new NpcToken { CreationDate = "x" }, path, out var ok, out _);
        ok.Should().BeTrue();

        var text = System.IO.File.ReadAllText(path);
        text.Should().Contain("\n", "the file is persisted with indented formatting");
    }

    [Fact]
    public void LoadJSONFile_MissingFile_FailsWithDescriptiveMessage()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("does-not-exist.json"); // Combine creates parents only, not the file.
        System.IO.File.Exists(path).Should().BeFalse();

        var loaded = JSONhandler<NpcToken>.LoadJSONFile(path, out var ok, out var ex);

        loaded.Should().BeNull();
        ok.Should().BeFalse();
        ex.Should().Contain("does not exist");
        ex.Should().Contain(path);
    }

    [Fact]
    public void LoadJSONFile_FileWithMalformedJson_FailsWithException()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteText("broken.json", "{ not valid json ]");

        var loaded = JSONhandler<ModSetting>.LoadJSONFile(path, out var ok, out var ex);

        loaded.Should().BeNull();
        ok.Should().BeFalse();
        ex.Should().NotBeNullOrWhiteSpace();
        ex.Should().NotContain("does not exist", "the file exists; the failure is a parse error");
    }

    [Fact]
    public void LoadJSONFile_FileContainingLiteralNull_FailsWhenNotNullable()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteText("null.json", "null");

        var loaded = JSONhandler<NpcToken>.LoadJSONFile(path, out var ok, out var ex);

        loaded.Should().BeNull();
        ok.Should().BeFalse();
        ex.Should().Be("JSON object was null");
    }

    [Fact]
    public void LoadJSONFile_FileContainingLiteralNull_SucceedsWhenNullable()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteText("null.json", "null");

        var loaded = JSONhandler<NpcToken>.LoadJSONFile(path, out var ok, out var ex, canBeNull: true);

        loaded.Should().BeNull();
        ok.Should().BeTrue();
        ex.Should().BeEmpty();
    }

    [Fact]
    public void SaveJSONFile_InvalidPath_FailsGracefullyWithoutThrowing()
    {
        // An invalid path (illegal characters) makes the underlying File write throw;
        // the wrapper catches it, reports failure, and never propagates the exception.
        const string invalidPath = "Z:\\\0invalid\\settings.json";

        Action act = () => JSONhandler<ModSetting>.SaveJSONFile(
            new ModSetting(), invalidPath, out _, out _);

        act.Should().NotThrow();

        JSONhandler<ModSetting>.SaveJSONFile(new ModSetting(), invalidPath, out var ok, out var ex);
        ok.Should().BeFalse();
        ex.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SaveThenLoad_ModSetting_PreservesEnumAndKeyedCollections()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("modsetting.json");

        var setting = new ModSetting
        {
            DisplayName = "Full",
            CorrespondingModKeys = new List<ModKey> { ModA, ModB },
            NpcFormKeys = new HashSet<FormKey> { Npc1, Npc2 },
            ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.IncludeAsNew,
            AvailablePluginsForNpcs = new Dictionary<FormKey, List<ModKey>>
            {
                [Npc1] = new() { ModA },
                [Npc2] = new(), // empty value list must survive the file round-trip
            },
        };

        JSONhandler<ModSetting>.SaveJSONFile(setting, path, out var saveOk, out _);
        saveOk.Should().BeTrue();

        var loaded = JSONhandler<ModSetting>.LoadJSONFile(path, out var loadOk, out _);
        loadOk.Should().BeTrue();
        loaded!.DisplayName.Should().Be("Full");
        loaded.CorrespondingModKeys.Should().Equal(ModA, ModB);
        loaded.NpcFormKeys.Should().BeEquivalentTo(new[] { Npc1, Npc2 });
        loaded.ModRecordOverrideHandlingMode.Should().Be(RecordOverrideHandlingMode.IncludeAsNew);
        loaded.AvailablePluginsForNpcs[Npc1].Should().Equal(ModA);
        loaded.AvailablePluginsForNpcs[Npc2].Should().NotBeNull().And.BeEmpty();
    }
}
