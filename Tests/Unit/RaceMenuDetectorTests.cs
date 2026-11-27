using System;
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.Benchmark;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="RaceMenuDetector"/> and <see cref="WigHairTintDecision"/>: what
/// <see cref="WigHairTintMode.Auto"/> turns on. Auto reproduces what the game shows without this
/// app, so a converted wig is tinted exactly when RaceMenu would have tinted the worn one. That
/// needs RaceMenu's own rules, taken from its source (github.com/expired6978/SKSE64Plugins,
/// skee64/main.cpp and skee64/TintMaskInterface.cpp):
/// <list type="bullet">
/// <item><c>bool g_enableTintHairSlot = true;</c> and <c>UInt32 g_tintHairSlot = 1;</c> are the
/// code defaults; the shipped ini sets the mask to 2050.</item>
/// <item><c>SKEE64GetConfigOption</c> reads <c>skee64.ini</c>, then <c>skee64_custom.ini</c>,
/// taking the custom value only when it is non-empty.</item>
/// <item><c>SKEE64GetConfigValue</c> parses with <c>sscanf("%u")</c> and leaves the default when
/// that fails; the bool is <c>tmp &gt; 0</c>.</item>
/// <item><c>TintMaskInterface::OnAttach</c> tints when
/// <c>((armorMask &amp; addonMask) &amp; g_tintHairSlot) != 0</c>.</item>
/// </list>
/// Pure file-system tests against temp folders: no game, no RaceMenu install.
/// </summary>
public class RaceMenuDetectorTests : IDisposable
{
    private const uint Hair = 2;        // biped slot 31
    private const uint LongHair = 2048; // biped slot 41
    private const uint Shipped = 2050;  // the shipped skee64.ini's uTintHairSlot

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NPC2_RaceMenuDetectorTests", Guid.NewGuid().ToString("N"));

    public RaceMenuDetectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* best effort */ }
    }

    // ---- fake installations ------------------------------------------------------------------

    private string DataFolder(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Put(string dataFolder, string fileName, string content = "")
    {
        var path = Path.Combine(dataFolder, "SKSE", "Plugins", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>An ini shaped like the one RaceMenu ships: other keys around the two that
    /// matter, and a comment after every value.</summary>
    private static string Ini(string? enable, string? mask) =>
        "; RaceMenu settings\r\n" +
        "[Debug]\r\n" +
        "iLogLevel=4 ; Default[4]\r\n" +
        "\r\n" +
        "[Features]\r\n" +
        "bEnableOverlays=1 ; Default[1]\r\n" +
        (enable == null ? "" : $"bEnableTintHairSlot={enable}\r\n") +
        "bEnableTintSync=1 ; Default[1]\r\n" +
        "\r\n" +
        "[General]\r\n" +
        (mask == null ? "" : $"uTintHairSlot={mask}\r\n") +
        "iTransformLoader=1\r\n";

    private static Func<string, string?> Resolver(string dataFolder) => rel =>
    {
        var path = Path.Combine(dataFolder, rel);
        return File.Exists(path) ? path : null;
    };

    private static RaceMenuHairTintState Detect(string dataFolder, SkyrimRelease release = SkyrimRelease.SkyrimSE) =>
        RaceMenuDetector.Detect(release, Resolver(dataFolder));

    // ---- detection: SE -----------------------------------------------------------------------

    [Fact]
    public void SE_InstalledAndEnabled_ReadsTheShippedIni_TrailingCommentsAndAll()
    {
        var data = DataFolder("se");
        var dll = Put(data, "skee64.dll");
        var ini = Put(data, "skee64.ini", Ini("1 ; Default[1]", "2050 ; Default[2050] Hair + LongHair"));

        var state = Detect(data);

        state.Installed.Should().BeTrue("the DLL exists");
        state.DllPath.Should().Be(dll);
        state.TintEnabled.Should().BeTrue();
        state.SlotMask.Should().Be(Shipped, "the number is read up to the comment that follows it");
        state.IniPathsRead.Should().Equal(ini);
    }

    [Fact]
    public void SE_IniKeyOff_ReportsTheTintDisabled()
    {
        var data = DataFolder("off");
        Put(data, "skee64.dll");
        Put(data, "skee64.ini", Ini("0 ; Default[1]", "2050"));

        var state = Detect(data);

        state.Installed.Should().BeTrue();
        state.TintEnabled.Should().BeFalse();
        state.SlotMask.Should().Be(Shipped);
        state.TintsWornItem(Hair, Hair).Should().BeFalse("the feature is off, whatever the slots");
    }

    [Fact]
    public void SE_IniMissing_UsesTheCodeDefaults()
    {
        var data = DataFolder("noini");
        Put(data, "skee64.dll");

        var state = Detect(data);

        state.Installed.Should().BeTrue();
        state.TintEnabled.Should().BeTrue("g_enableTintHairSlot = true");
        state.SlotMask.Should().Be(1u, "g_tintHairSlot = 1: the code default is slot 30, not the hair slots");
        state.IniPathsRead.Should().BeEmpty();
        state.TintsWornItem(Hair, Hair).Should().BeFalse(
            "with the code-default mask no hair-slot wig is tinted in game");
    }

    [Fact]
    public void SE_KeysMissingFromTheIni_UseTheCodeDefaults()
    {
        var data = DataFolder("nokeys");
        Put(data, "skee64.dll");
        var ini = Put(data, "skee64.ini", Ini(enable: null, mask: null));

        var state = Detect(data);

        state.TintEnabled.Should().BeTrue();
        state.SlotMask.Should().Be(1u);
        state.IniPathsRead.Should().Equal(new[] { ini }, "the file was read even though it supplied neither key");
    }

    [Fact]
    public void SE_ValueThatIsNotANumber_LeavesTheCodeDefault()
    {
        // sscanf("%u") fails, SKEE64GetConfigValue returns false and the global keeps its value.
        var data = DataFolder("garbage");
        Put(data, "skee64.dll");
        Put(data, "skee64.ini", Ini("true", "hair"));

        var state = Detect(data);

        state.TintEnabled.Should().BeTrue();
        state.SlotMask.Should().Be(1u);
    }

    [Fact]
    public void SE_CustomIni_OverridesKeyByKey()
    {
        var data = DataFolder("custom");
        Put(data, "skee64.dll");
        var ini = Put(data, "skee64.ini", Ini("1 ; Default[1]", "2050 ; Default[2050]"));
        var custom = Put(data, "skee64_custom.ini", "[Features]\r\nbEnableTintHairSlot=0\r\n");

        var state = Detect(data);

        state.TintEnabled.Should().BeFalse("the custom ini has the key, so it wins");
        state.SlotMask.Should().Be(Shipped, "the custom ini has no uTintHairSlot, so the base value stands");
        state.IniPathsRead.Should().Equal(ini, custom);
    }

    [Fact]
    public void SE_CustomIni_CanSupplyAKeyTheBaseIniLacks()
    {
        var data = DataFolder("customonly");
        Put(data, "skee64.dll");
        Put(data, "skee64.ini", Ini(enable: "1", mask: null));
        Put(data, "skee64_custom.ini", "[General]\r\nuTintHairSlot=2 ; hair only\r\n");

        Detect(data).SlotMask.Should().Be(Hair);
    }

    [Fact]
    public void SE_CustomIni_WithAnEmptyValue_DoesNotOverride()
    {
        // "if (resultLen > 0) // Only take custom if we have it"
        var data = DataFolder("customempty");
        Put(data, "skee64.dll");
        Put(data, "skee64.ini", Ini("0", "2050"));
        Put(data, "skee64_custom.ini", "[Features]\r\nbEnableTintHairSlot=\r\n[General]\r\nuTintHairSlot=\r\n");

        var state = Detect(data);

        state.TintEnabled.Should().BeFalse();
        state.SlotMask.Should().Be(Shipped);
    }

    [Fact]
    public void SE_DllMissing_IsNotInstalled_EvenWithAnIni()
    {
        var data = DataFolder("nodll");
        Put(data, "skee64.ini", Ini("1", "2050"));

        var state = Detect(data);

        state.Installed.Should().BeFalse("installed means the DLL exists; a leftover ini is not RaceMenu");
        state.DllPath.Should().BeNull();
        state.IniPathsRead.Should().BeEmpty();
        state.TintsWornItem(Hair, Hair).Should().BeFalse();
        state.Describe().Should().Contain("not installed").And.Contain("skee64.dll");
    }

    [Fact]
    public void SectionAndKey_MatchWithoutRegardToCase_AndOnlyInTheirOwnSection()
    {
        var data = DataFolder("case");
        Put(data, "skee64.dll");
        Put(data, "skee64.ini",
            "[features]\r\n" +
            "uTintHairSlot=4 ; the right key in the wrong section is not read\r\n" +
            "BENABLETINTHAIRSLOT = 0\r\n" +
            "[GENERAL]\r\n" +
            " utinthairslot = 2050 \r\n");

        var state = Detect(data);

        state.TintEnabled.Should().BeFalse();
        state.SlotMask.Should().Be(Shipped);
    }

    // ---- detection: VR -----------------------------------------------------------------------

    [Fact]
    public void VR_ReadsSkeevr_EvenWhenSkee64IsAlsoPresent()
    {
        // VR mod lists ship RaceMenu VR and commonly leave the regular RaceMenu enabled too.
        var data = DataFolder("vr");
        Put(data, "skee64.dll");
        Put(data, "skee64.ini", Ini("1", "2050"));
        var vrDll = Put(data, "skeevr.dll");
        var vrIni = Put(data, "skeevr.ini", Ini("0 ; Default[1]", "2 ; hair only"));

        var state = Detect(data, SkyrimRelease.SkyrimVR);

        state.Installed.Should().BeTrue();
        state.DllPath.Should().Be(vrDll);
        state.TintEnabled.Should().BeFalse("skeevr.ini says so; skee64.ini is the other game's file");
        state.SlotMask.Should().Be(Hair);
        state.IniPathsRead.Should().Equal(vrIni);
    }

    [Fact]
    public void VR_WithOnlySkee64_IsNotInstalled()
    {
        var data = DataFolder("vr-se-only");
        Put(data, "skee64.dll");
        Put(data, "skee64.ini", Ini("1", "2050"));

        var state = Detect(data, SkyrimRelease.SkyrimVR);

        state.Installed.Should().BeFalse("skee64.dll does not load in VR");
        state.Describe().Should().Contain("skeevr.dll");
    }

    [Fact]
    public void VR_DoesNotApplyACustomIni()
    {
        // The VR port's source was not available, so no custom-ini rule is assumed for it.
        var data = DataFolder("vr-custom");
        Put(data, "skeevr.dll");
        var vrIni = Put(data, "skeevr.ini", Ini("1", "2050"));
        Put(data, "skeevr_custom.ini", "[Features]\r\nbEnableTintHairSlot=0\r\n");
        Put(data, "skee64_custom.ini", "[Features]\r\nbEnableTintHairSlot=0\r\n");

        var state = Detect(data, SkyrimRelease.SkyrimVR);

        state.TintEnabled.Should().BeTrue();
        state.IniPathsRead.Should().Equal(vrIni);
    }

    [Fact]
    public void NonVR_WithOnlySkeevr_IsNotInstalled()
    {
        var data = DataFolder("se-vr-only");
        Put(data, "skeevr.dll");
        Put(data, "skeevr.ini", Ini("1", "2050"));

        Detect(data, SkyrimRelease.SkyrimSE).Installed.Should().BeFalse("skeevr.dll does not load in SE");
    }

    // ---- value parsing -----------------------------------------------------------------------

    [Theory]
    [InlineData("1 ; Default[1]", true, 1u)]
    [InlineData("2050 ; Default[2050]", true, 2050u)]
    [InlineData("  2050\t; tab before the comment", true, 2050u)]
    [InlineData("0", true, 0u)]
    [InlineData("+7", true, 7u)]
    [InlineData("12abc", true, 12u)]
    [InlineData("", false, 0u)]
    [InlineData("; only a comment", false, 0u)]
    [InlineData("true", false, 0u)]
    public void LeadingUnsigned_ParsesLikeSscanfPercentU(string value, bool expectedOk, uint expected)
    {
        RaceMenuDetector.TryParseLeadingUnsigned(value, out var parsed).Should().Be(expectedOk);
        if (expectedOk) parsed.Should().Be(expected);
    }

    // ---- the detector service ----------------------------------------------------------------

    private static EnvironmentStateProvider BareEnvironment() =>
        (EnvironmentStateProvider)RuntimeHelpers.GetUninitializedObject(typeof(EnvironmentStateProvider));

    [Fact]
    public void Detector_WithNoDataFolder_ReportsNotInstalled()
    {
        new RaceMenuDetector(BareEnvironment()).State.Installed.Should().BeFalse();
    }

    [Fact]
    public void Detector_UsesTheInjectedResolver_KeepsItsAnswer_AndRefreshReadsAgain()
    {
        var data = DataFolder("service");
        var dll = Put(data, "skee64.dll");
        Put(data, "skee64.ini", Ini("1", "2050"));
        var detector = new RaceMenuDetector(BareEnvironment()) { DataRelativeFileResolver = Resolver(data) };

        detector.State.Installed.Should().BeTrue();
        detector.State.SlotMask.Should().Be(Shipped);

        File.Delete(dll);
        detector.State.Installed.Should().BeTrue("one run gets one answer for all its NPCs");
        detector.Refresh().Installed.Should().BeFalse("the next run reads the files again");
        detector.State.Installed.Should().BeFalse();
    }

    // ---- the headless bench's resolver (Mo2ProfileEnvironment) --------------------------------

    [Fact]
    public void ProfileSearchOrder_FindsRaceMenuInAnEnabledModFolder_HighestPriorityFirst()
    {
        var overwrite = DataFolder("overwrite");
        var tweaks = DataFolder("mods-RaceMenu ini tweaks"); // higher priority than RaceMenu itself
        var raceMenu = DataFolder("mods-RaceMenu");
        var gameData = DataFolder("game-Data");
        Put(raceMenu, "skee64.dll");
        Put(raceMenu, "skee64.ini", Ini("1", "2050"));
        Put(tweaks, "skee64.ini", Ini("0", "2050"));
        var searchOrder = new[] { overwrite, tweaks, raceMenu, gameData };

        var state = RaceMenuDetector.Detect(SkyrimRelease.SkyrimSE,
            rel => Mo2ProfileEnvironment.ResolveInSearchOrder(searchOrder, rel));

        state.Installed.Should().BeTrue();
        state.TintEnabled.Should().BeFalse("the higher-priority folder's ini is the one the game would read");
        state.IniPathsRead.Should().ContainSingle().Which.Should().StartWith(tweaks);
    }

    [Fact]
    public void ProfileSearchOrder_WithoutRaceMenuEnabled_SaysNotInstalled()
    {
        // RaceMenu exists in the instance's mods folder, but the profile does not enable it, so
        // its folder is not in the search order.
        var disabledRaceMenu = DataFolder("mods-RaceMenu (disabled)");
        Put(disabledRaceMenu, "skee64.dll");
        Put(disabledRaceMenu, "skee64.ini", Ini("1", "2050"));
        var searchOrder = new[] { DataFolder("overwrite2"), DataFolder("mods-Other"), DataFolder("game-Data2") };

        var state = RaceMenuDetector.Detect(SkyrimRelease.SkyrimSE,
            rel => Mo2ProfileEnvironment.ResolveInSearchOrder(searchOrder, rel));

        state.Installed.Should().BeFalse();
    }

    // ---- the run-log line --------------------------------------------------------------------

    [Fact]
    public void Describe_StatesTheSettingTheMaskAndTheFilesRead()
    {
        var data = DataFolder("describe");
        var dll = Put(data, "skee64.dll");
        var ini = Put(data, "skee64.ini", Ini("0", "2050"));

        var line = Detect(data).Describe();

        line.Should().Contain("is installed").And.Contain(dll);
        line.Should().Contain("OFF");
        line.Should().Contain("2050").And.Contain("slots 31, 41");
        line.Should().Contain(ini);
    }

    // ---- the decision ------------------------------------------------------------------------

    private static RaceMenuHairTintState RaceMenu(bool installed, bool enabled, uint mask) =>
        new(installed, enabled, mask, @"SKSE\Plugins\skee64.dll", installed ? @"X:\skee64.dll" : null,
            Array.Empty<string>());

    [Fact]
    public void MutagenHairSlots_AreTheBitsOfRaceMenusShippedMask()
    {
        // The decision feeds Mutagen's BipedObjectFlag straight into RaceMenu's mask test, which
        // is only right while bit 0 is biped slot 30 in both.
        ((uint)(BipedObjectFlag.Hair | BipedObjectFlag.LongHair)).Should().Be(Shipped);
        ((uint)BipedObjectFlag.Hair).Should().Be(Hair);
        ((uint)BipedObjectFlag.LongHair).Should().Be(LongHair);
    }

    [Theory]
    // On (Always): tints whatever RaceMenu does and whatever the slots are.
    [InlineData(WigHairTintMode.Always, true, true, Hair, Hair, true)]
    [InlineData(WigHairTintMode.Always, false, true, Hair, Hair, true)]
    [InlineData(WigHairTintMode.Always, true, false, Hair, Hair, true)]
    [InlineData(WigHairTintMode.Always, true, true, 4u, Hair, true)]
    [InlineData(WigHairTintMode.Always, false, false, 0u, 0u, true)]
    // Off (Never): never tints.
    [InlineData(WigHairTintMode.Never, true, true, Hair, Hair, false)]
    [InlineData(WigHairTintMode.Never, false, true, Hair, Hair, false)]
    [InlineData(WigHairTintMode.Never, true, false, Hair, Hair, false)]
    [InlineData(WigHairTintMode.Never, true, true, 4u, Hair, false)]
    // Auto: only when RaceMenu is installed, enabled, and the slot test passes.
    [InlineData(WigHairTintMode.Auto, true, true, Hair, Hair, true)]
    [InlineData(WigHairTintMode.Auto, false, true, Hair, Hair, false)]
    [InlineData(WigHairTintMode.Auto, true, false, Hair, Hair, false)]
    [InlineData(WigHairTintMode.Auto, false, false, Hair, Hair, false)]
    [InlineData(WigHairTintMode.Auto, true, true, 4u, Hair, false)]  // armor (body only) lacks the slot
    [InlineData(WigHairTintMode.Auto, true, true, Hair, 4u, false)]  // addon lacks the slot
    [InlineData(WigHairTintMode.Auto, true, true, 0u, Hair, false)]  // armor has no body template
    [InlineData(WigHairTintMode.Auto, true, true, LongHair, Hair, false)] // both in the mask, no slot in common
    [InlineData(WigHairTintMode.Auto, true, true, LongHair, Hair | LongHair, true)]
    // Measured in game: both of these are tinted by RaceMenu with the shipped mask.
    [InlineData(WigHairTintMode.Auto, true, true, 143u, Hair, true)]            // High Poly: skin 30-33+37, wig addon 31
    [InlineData(WigHairTintMode.Auto, true, true, 258u, Hair | LongHair, true)] // FoxGlove Auri: wig 31+38, addon 31+41
    public void ShouldTint_ModeByRaceMenuBySlotTest(WigHairTintMode mode, bool installed, bool enabled,
        uint armorSlots, uint addonSlots, bool expected)
    {
        WigHairTintDecision.ShouldTint(mode, RaceMenu(installed, enabled, Shipped), armorSlots, addonSlots)
            .Should().Be(expected);
    }

    [Fact]
    public void ShouldTint_Auto_UsesTheDetectedMask_NotAFixedOne()
    {
        var hairOnly = RaceMenu(installed: true, enabled: true, mask: Hair);
        var codeDefault = RaceMenu(installed: true, enabled: true, mask: RaceMenuHairTintState.DefaultSlotMask);

        WigHairTintDecision.ShouldTint(WigHairTintMode.Auto, hairOnly, LongHair, LongHair).Should().BeFalse();
        WigHairTintDecision.ShouldTint(WigHairTintMode.Auto, hairOnly, Hair, Hair).Should().BeTrue();
        WigHairTintDecision.ShouldTint(WigHairTintMode.Auto, codeDefault, Hair, Hair).Should().BeFalse(
            "without the shipped ini RaceMenu's mask is slot 30, so it tints no hair-slot wig");
    }
}
