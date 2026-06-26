using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Newtonsoft.Json;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The Newtonsoft / TypeConverter JSON adapters in <see cref="JsonConverters"/>:
/// <list type="bullet">
/// <item><see cref="FormKeyTypeConverter"/> — string ↔ <see cref="FormKey"/> with bracket
/// normalization, case folding and format validation.</item>
/// <item><see cref="ModSettingConverter"/> — read-only migration of legacy
/// <c>MugShotFolderPath</c>/<c>MergeInLabelColor</c>/<c>HandleInjectedRecordsLabelColor</c>
/// fields onto the current <see cref="ModSetting"/> shape.</item>
/// <item><see cref="ColorJsonConverter"/> — WPF <see cref="Color"/> ↔ "#AARRGGBB" string.</item>
/// <item><see cref="PortraitCameraModeConverter"/> — "Relative" → Portrait migration over
/// the base <c>StringEnumConverter</c>.</item>
/// </list>
/// All pure / deterministic — no Skyrim install, clock or network needed.
/// </summary>
public class JsonConvertersTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // FormKeyTypeConverter
    // ──────────────────────────────────────────────────────────────────────────

    private static FormKey ConvertFormKey(object value) =>
        (FormKey)new FormKeyTypeConverter().ConvertFrom(null!, CultureInfo.InvariantCulture, value)!;

    [Fact]
    public void FormKey_CanConvertFrom_StringTrue_IntFalse()
    {
        var c = new FormKeyTypeConverter();
        c.CanConvertFrom(null!, typeof(string)).Should().BeTrue();
        c.CanConvertFrom(null!, typeof(int)).Should().BeFalse();
    }

    [Fact]
    public void FormKey_ConvertFrom_BareString_Parses()
    {
        var fk = ConvertFormKey("000C00:Skyrim.esm");
        fk.ID.Should().Be(0xC00u);
        fk.ModKey.Should().Be(ModKey.FromFileName("Skyrim.esm"));
    }

    [Fact]
    public void FormKey_ConvertFrom_BracketedString_Parses()
    {
        // Already wrapped — must NOT be double-wrapped before matching.
        var fk = ConvertFormKey("[000C00:Skyrim.esm]");
        fk.ID.Should().Be(0xC00u);
        fk.ModKey.Should().Be(ModKey.FromFileName("Skyrim.esm"));
    }

    [Fact]
    public void FormKey_ConvertFrom_LowercaseHex_IsUppercasedButEquivalent()
    {
        // The hex part is upper-cased internally; FormKeys parsed from either case are equal.
        var lower = ConvertFormKey("00abcd:Skyrim.esm");
        var upper = ConvertFormKey("00ABCD:Skyrim.esm");
        lower.Should().Be(upper);
        lower.ID.Should().Be(0xABCDu);
    }

    [Fact]
    public void FormKey_ConvertFrom_DottedFilename_KeepsFullName()
    {
        // The ".+" filename group is greedy, so the final ".esp" is the extension and
        // "My.Mod" is the base name — i.e. the plugin is "My.Mod.esp".
        var fk = ConvertFormKey("001234:My.Mod.esp");
        fk.ID.Should().Be(0x1234u);
        fk.ModKey.Should().Be(ModKey.FromFileName("My.Mod.esp"));
        fk.ModKey.FileName.String.Should().Be("My.Mod.esp");
    }

    [Theory]
    [InlineData("000C00:Skyrim.esp", "esp")]
    [InlineData("000C00:Skyrim.esm", "esm")]
    [InlineData("000C00:Skyrim.esl", "esl")]
    [InlineData("000C00:Skyrim.ESM", "esm")]
    public void FormKey_ConvertFrom_AllExtensions_Accepted(string input, string expectedExt)
    {
        var fk = ConvertFormKey(input);
        // Extension is lower-cased when the standardized key is rebuilt.
        fk.ModKey.FileName.String.Should().EndWith("." + expectedExt);
    }

    [Fact]
    public void FormKey_ConvertFrom_FiveHexDigits_Throws()
    {
        // Exactly six hex digits are required before the colon.
        Action act = () => ConvertFormKey("00C00:Skyrim.esm");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void FormKey_ConvertFrom_SevenHexDigits_Throws()
    {
        Action act = () => ConvertFormKey("0000C00:Skyrim.esm");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void FormKey_ConvertFrom_BadExtension_Throws()
    {
        Action act = () => ConvertFormKey("000C00:Skyrim.txt");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void FormKey_ConvertFrom_MissingColon_Throws()
    {
        Action act = () => ConvertFormKey("000C00 Skyrim.esm");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void FormKey_ConvertFrom_NonString_Throws()
    {
        // Falls through to TypeConverter.ConvertFrom which has no int conversion registered.
        Action act = () => ConvertFormKey(1234);
        act.Should().Throw<NotSupportedException>();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ModSettingConverter
    // ──────────────────────────────────────────────────────────────────────────

    private static ModSetting DeserializeModSetting(string json)
    {
        var settings = new JsonSerializerSettings { Converters = { new ModSettingConverter() } };
        return JsonConvert.DeserializeObject<ModSetting>(json, settings)!;
    }

    [Fact]
    public void ModSetting_CanConvert_OnlyModSetting()
    {
        var c = new ModSettingConverter();
        c.CanConvert(typeof(ModSetting)).Should().BeTrue();
        c.CanConvert(typeof(Settings)).Should().BeFalse();
        c.CanConvert(typeof(string)).Should().BeFalse();
    }

    [Fact]
    public void ModSetting_IsReadOnly()
    {
        new ModSettingConverter().CanWrite.Should().BeFalse();
    }

    [Fact]
    public void ModSetting_WriteJson_Throws()
    {
        var c = new ModSettingConverter();
        Action act = () => c.WriteJson(null!, new ModSetting(), JsonSerializer.CreateDefault());
        act.Should().Throw<NotImplementedException>();
    }

    [Fact]
    public void ModSetting_ReadJson_PopulatesStandardProperties()
    {
        var ms = DeserializeModSetting("""{ "DisplayName": "MyMod", "MergeInDependencyRecords": false }""");
        ms.DisplayName.Should().Be("MyMod");
        ms.MergeInDependencyRecords.Should().BeFalse();
    }

    [Fact]
    public void ModSetting_ReadJson_MigratesLegacyMugShotFolderPath_ToList()
    {
        // Legacy single-string property with no new list present -> seeded into the list.
        var ms = DeserializeModSetting("""{ "DisplayName": "X", "MugShotFolderPath": "C:\\Mugs\\X" }""");
        ms.MugShotFolderPaths.Should().ContainSingle().Which.Should().Be(@"C:\Mugs\X");
    }

    [Fact]
    public void ModSetting_ReadJson_LegacyMugShotPath_DoesNotOverwriteExistingList()
    {
        // The new list already has entries -> the legacy scalar is ignored.
        var ms = DeserializeModSetting(
            """{ "MugShotFolderPath": "C:\\Old", "MugShotFolderPaths": ["C:\\New1", "C:\\New2"] }""");
        ms.MugShotFolderPaths.Should().BeEquivalentTo(new[] { @"C:\New1", @"C:\New2" });
    }

    [Fact]
    public void ModSetting_ReadJson_LegacyMugShotPath_Empty_LeavesListEmpty()
    {
        var ms = DeserializeModSetting("""{ "MugShotFolderPath": "" }""");
        ms.MugShotFolderPaths.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void ModSetting_ReadJson_NoLegacyFields_DefaultsUnchanged()
    {
        var ms = DeserializeModSetting("""{ "DisplayName": "Plain" }""");
        ms.MugShotFolderPaths.Should().BeEmpty();
        ms.HasAlteredMergeLogic.Should().BeFalse();
        ms.HasAlteredHandleInjectedRecordsLogic.Should().BeFalse();
    }

    [Fact]
    public void ModSetting_ReadJson_MergeInLabelColorPresent_SetsHasAlteredMergeLogic()
    {
        // A non-null legacy color implies the user altered merge logic.
        var ms = DeserializeModSetting("""{ "DisplayName": "X", "MergeInLabelColor": "#FF00FF00" }""");
        ms.HasAlteredMergeLogic.Should().BeTrue();
    }

    [Fact]
    public void ModSetting_ReadJson_MergeInLabelColorNull_DoesNotSetFlag()
    {
        // Explicit JSON null is type Null -> the migration is skipped.
        var ms = DeserializeModSetting("""{ "DisplayName": "X", "MergeInLabelColor": null }""");
        ms.HasAlteredMergeLogic.Should().BeFalse();
    }

    [Fact]
    public void ModSetting_ReadJson_InjectedRecordsLabelColorPresent_SetsFlag()
    {
        var ms = DeserializeModSetting(
            """{ "DisplayName": "X", "HandleInjectedRecordsLabelColor": "#FFAABBCC" }""");
        ms.HasAlteredHandleInjectedRecordsLogic.Should().BeTrue();
    }

    [Fact]
    public void ModSetting_ReadJson_InjectedRecordsLabelColorNull_DoesNotSetFlag()
    {
        var ms = DeserializeModSetting(
            """{ "DisplayName": "X", "HandleInjectedRecordsLabelColor": null }""");
        ms.HasAlteredHandleInjectedRecordsLogic.Should().BeFalse();
    }

    [Fact]
    public void ModSetting_ReadJson_BothLegacyColorsPresent_SetBothFlags()
    {
        var ms = DeserializeModSetting(
            """
            {
                "DisplayName": "X",
                "MergeInLabelColor": "#FFFF0000",
                "HandleInjectedRecordsLabelColor": "#FF0000FF"
            }
            """);
        ms.HasAlteredMergeLogic.Should().BeTrue();
        ms.HasAlteredHandleInjectedRecordsLogic.Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ColorJsonConverter
    // ──────────────────────────────────────────────────────────────────────────

    private static string SerializeColor(Color c) =>
        JsonConvert.SerializeObject(c, new ColorJsonConverter());

    private static Color DeserializeColor(string json) =>
        JsonConvert.DeserializeObject<Color>(json, new ColorJsonConverter());

    [Fact]
    public void Color_Write_OpaqueRed_IsHashFFFF0000()
    {
        // WPF Color.ToString() renders #AARRGGBB; an opaque red is fully-opaque alpha first.
        SerializeColor(Colors.Red).Should().Be("\"#FFFF0000\"");
    }

    [Fact]
    public void Color_Write_PreservesAlpha()
    {
        var c = Color.FromArgb(0x80, 0x12, 0x34, 0x56);
        SerializeColor(c).Should().Be("\"#80123456\"");
    }

    [Fact]
    public void Color_Read_HexString_RoundTrips()
    {
        DeserializeColor("\"#FFFF0000\"").Should().Be(Colors.Red);
    }

    [Fact]
    public void Color_Read_NamedColor_Resolves()
    {
        DeserializeColor("\"Red\"").Should().Be(Colors.Red);
        DeserializeColor("\"White\"").Should().Be(Colors.White);
        DeserializeColor("\"Blue\"").Should().Be(Colors.Blue);
    }

    [Theory]
    [InlineData(58, 61, 64)]    // the default MugshotBackgroundColor RGB
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(105, 105, 105)]
    public void Color_RoundTrip_ThroughStringIsLossless(byte r, byte g, byte b)
    {
        var original = Color.FromRgb(r, g, b);
        var restored = DeserializeColor(SerializeColor(original));
        restored.Should().Be(original);
    }

    [Fact]
    public void Color_RoundTrip_WithAlphaIsLossless()
    {
        var original = Color.FromArgb(0x40, 0x10, 0x20, 0x30);
        DeserializeColor(SerializeColor(original)).Should().Be(original);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // PortraitCameraModeConverter
    // ──────────────────────────────────────────────────────────────────────────

    private static PortraitCameraMode ReadCameraMode(string jsonValue)
    {
        // Wrap the bare value so Newtonsoft has a token stream to read.
        var settings = new JsonSerializerSettings { Converters = { new PortraitCameraModeConverter() } };
        return JsonConvert.DeserializeObject<PortraitCameraMode>(jsonValue, settings);
    }

    [Fact]
    public void Camera_Read_LegacyRelative_MapsToPortrait()
    {
        ReadCameraMode("\"Relative\"").Should().Be(PortraitCameraMode.Portrait);
    }

    [Fact]
    public void Camera_Read_Portrait_StaysPortrait()
    {
        ReadCameraMode("\"Portrait\"").Should().Be(PortraitCameraMode.Portrait);
    }

    [Fact]
    public void Camera_Read_Fixed_StaysFixed()
    {
        ReadCameraMode("\"Fixed\"").Should().Be(PortraitCameraMode.Fixed);
    }

    [Fact]
    public void Camera_Read_NumericOrdinal_UsesBaseBehavior()
    {
        // Base StringEnumConverter still tolerates the underlying integer ordinal.
        ReadCameraMode("0").Should().Be(PortraitCameraMode.Portrait);
        ReadCameraMode("1").Should().Be(PortraitCameraMode.Fixed);
    }

    [Fact]
    public void Camera_Write_UsesEnumName_NotOrdinal()
    {
        // Inherits StringEnumConverter, so it serializes the enum name.
        var settings = new JsonSerializerSettings { Converters = { new PortraitCameraModeConverter() } };
        JsonConvert.SerializeObject(PortraitCameraMode.Fixed, settings).Should().Be("\"Fixed\"");
        JsonConvert.SerializeObject(PortraitCameraMode.Portrait, settings).Should().Be("\"Portrait\"");
    }

    [Fact]
    public void Camera_Read_OnSettings_AppliesConverterViaAttribute()
    {
        // Settings.SelectedCameraMode carries [JsonConverter(typeof(PortraitCameraModeConverter))],
        // so the "Relative" -> Portrait migration fires without registering the converter.
        var s = JsonConvert.DeserializeObject<Settings>("""{ "SelectedCameraMode": "Relative" }""")!;
        s.SelectedCameraMode.Should().Be(PortraitCameraMode.Portrait);
    }
}
