using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd.SetupValidation;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="SetupValidators"/>: the setup wizard's per-page checks. Each case is a setup mistake
/// reported on the Nexus page (docs/SetupWizardPlan-2026-10.md), reproduced on a temp folder.
/// </summary>
public sealed class SetupValidatorsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "npc2-setup-" + Guid.NewGuid().ToString("N"));

    public SetupValidatorsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Dir(params string[] parts)
    {
        string path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private string File(params string[] parts)
    {
        string path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, string.Empty);
        return path;
    }

    private static IEnumerable<SetupFinding> Errors(IEnumerable<SetupFinding> f) => f.Where(x => x.Severity == SetupSeverity.Error);
    private static IEnumerable<SetupFinding> Warnings(IEnumerable<SetupFinding> f) => f.Where(x => x.Severity == SetupSeverity.Warning);

    // ---- game data path

    [Fact]
    public void DataPath_Blank_IsAutoDetect() =>
        Errors(SetupValidators.DataFolderPath("")).Should().BeEmpty();

    [Fact]
    public void DataPath_GameFolderInsteadOfData_PointsAtDataSubfolder()
    {
        File("Stock Game", "Data", "Skyrim.esm");
        var findings = SetupValidators.DataFolderPath(Path.Combine(_root, "Stock Game"));
        Errors(findings).Should().ContainSingle().Which.Message.Should().Contain(Path.Combine(_root, "Stock Game", "Data"));
    }

    [Fact]
    public void DataPath_WithoutSkyrimEsm_IsError() =>
        Errors(SetupValidators.DataFolderPath(Dir("NotAGame"))).Should().NotBeEmpty();

    [Fact]
    public void DataPath_RealDataFolder_HasNoErrors()
    {
        File("Game", "Data", "Skyrim.esm");
        Errors(SetupValidators.DataFolderPath(Path.Combine(_root, "Game", "Data"))).Should().BeEmpty();
    }

    [Fact]
    public void PortableGameCopy_NextToToolsFolder_IsFound()
    {
        // Typical modlist layout: <instance>\Stock Game\Data and <instance>\tools\NPC2\exe.
        File("Instance", "Stock Game", "Data", "Skyrim.esm");
        string exeDir = Dir("Instance", "tools", "NPC2");
        SetupValidators.FindPortableGameDataFolders(exeDir)
            .Should().Contain(Path.Combine(_root, "Instance", "Stock Game", "Data"));
    }

    // ---- game version

    [Fact]
    public void GameVersion_VrFolderWithSeSelected_IsError()
    {
        File("VR", "SkyrimVR.exe");
        Errors(SetupValidators.GameVersion(SkyrimRelease.SkyrimSE, Path.Combine(_root, "VR"))).Should().NotBeEmpty();
    }

    [Fact]
    public void GameVersion_MatchingExe_IsFine()
    {
        File("SE", "SkyrimSE.exe");
        var findings = SetupValidators.GameVersion(SkyrimRelease.SkyrimSE, Path.Combine(_root, "SE"));
        Errors(findings).Should().BeEmpty();
        Warnings(findings).Should().BeEmpty();
    }

    // ---- mods folder

    [Fact]
    public void ModsFolder_DataFolderPicked_IsError()
    {
        string data = Dir("Game", "Data");
        Errors(SetupValidators.ModsFolder(data, data, SetupModManager.MO2)).Should().NotBeEmpty();
    }

    [Fact]
    public void ModsFolder_SingleModPicked_PointsAtParent()
    {
        File("MO2", "mods", "Some Mod", "SomeMod.esp");
        var findings = SetupValidators.ModsFolder(Path.Combine(_root, "MO2", "mods", "Some Mod"), null, SetupModManager.MO2);
        Errors(findings).Should().ContainSingle().Which.Message.Should().Contain(Path.Combine(_root, "MO2", "mods"));
    }

    [Fact]
    public void ModsFolder_Mo2Instance_IsRecognised()
    {
        File("MO2", "ModOrganizer.ini");
        File("MO2", "mods", "Some Mod", "SomeMod.esp");
        var findings = SetupValidators.ModsFolder(Path.Combine(_root, "MO2", "mods"), null, SetupModManager.MO2);
        Errors(findings).Should().BeEmpty();
        findings.Should().Contain(f => f.Message.Contains("Mod Organizer 2"));
    }

    [Fact]
    public void ModsFolder_VortexStaging_IsRecognised()
    {
        File("Staging", "__vortex_staging_folder");
        File("Staging", "Some Mod", "meshes", "x.nif");
        SetupValidators.ModsFolder(Path.Combine(_root, "Staging"), null, SetupModManager.Vortex)
            .Should().Contain(f => f.Message.Contains("Vortex staging"));
    }

    // ---- modlist filter

    [Fact]
    public void ModlistFilter_FromAnotherInstance_IsError()
    {
        Dir("mods", "Local Mod");
        string modlist = Path.Combine(_root, "modlist.txt");
        System.IO.File.WriteAllLines(modlist, new[] { "+Other Mod", "-Disabled" });
        Errors(SetupValidators.ModlistFilter(modlist, Path.Combine(_root, "mods"))).Should().NotBeEmpty();
    }

    [Fact]
    public void ModlistFilter_CountsEnabledMatches()
    {
        Dir("mods", "A");
        Dir("mods", "B");
        string modlist = Path.Combine(_root, "modlist.txt");
        System.IO.File.WriteAllLines(modlist, new[] { "+a", "-B" });
        SetupValidators.ModlistFilter(modlist, Path.Combine(_root, "mods"))
            .Should().ContainSingle(f => f.Message.StartsWith("1 of the 2"));
    }

    [Fact]
    public void Mo2Profiles_AreFound()
    {
        File("MO2", "profiles", "Default", "modlist.txt");
        Dir("MO2", "mods");
        SetupValidators.FindMo2ProfileModlists(Path.Combine(_root, "MO2", "mods"))
            .Should().ContainSingle().Which.Should().EndWith(Path.Combine("Default", "modlist.txt"));
    }

    // ---- mugshots

    [Fact]
    public void Mugshots_UnextractedArchive_Warns()
    {
        File("Mugshots", "Pack.7z");
        File("Mugshots", "Mod A", "ModA.esp", "00000D62.png");
        Warnings(SetupValidators.MugshotsFolder(Path.Combine(_root, "Mugshots"), null))
            .Should().Contain(f => f.Message.Contains("not been extracted"));
    }

    [Fact]
    public void Mugshots_ExtraNestingLevel_Warns()
    {
        File("Mugshots", "Pack Download", "Mod A", "ModA.esp", "00000D62.png");
        Warnings(SetupValidators.MugshotsFolder(Path.Combine(_root, "Mugshots"), null))
            .Should().Contain(f => f.Message.Contains("extra folder level"));
    }

    [Fact]
    public void Mugshots_SingleModFolderPicked_PointsAtParent()
    {
        File("Mugshots", "Mod A", "ModA.esp", "00000D62.png");
        Errors(SetupValidators.MugshotsFolder(Path.Combine(_root, "Mugshots", "Mod A"), null))
            .Should().ContainSingle().Which.Message.Should().Contain(Path.Combine(_root, "Mugshots"));
    }

    [Fact]
    public void Mugshots_VanillaFolder_SuggestsBaseGame()
    {
        File("Mugshots", "Vanilla", "Skyrim.esm", "00013BBF.png");
        SetupValidators.MugshotsFolder(Path.Combine(_root, "Mugshots"), null)
            .Should().Contain(f => f.Message.Contains("Base Game"));
    }

    [Fact]
    public void Mugshots_MatchRateAgainstModsFolder()
    {
        File("Mugshots", "Mod A", "ModA.esp", "1.png");
        File("Mugshots", "Mod B", "ModB.esp", "1.png");
        Dir("mods", "mod a");
        var findings = SetupValidators.MugshotsFolder(Path.Combine(_root, "Mugshots"), Path.Combine(_root, "mods"));
        Errors(findings).Should().BeEmpty();
        findings.Should().Contain(f => f.Message.StartsWith("1 of 2"));
    }

    // ---- output

    [Fact]
    public void Output_BareName_ResolvesIntoModsFolder() =>
        SetupValidators.ResolveOutputDirectory("NPC Output", @"C:\MO2\mods").Should().Be(@"C:\MO2\mods\NPC Output");

    [Fact]
    public void Output_CurlyBraces_IsError() =>
        Errors(SetupValidators.OutputDirectory("NPC {Output}", Dir("mods"), null, SetupModManager.MO2)).Should().NotBeEmpty();

    [Fact]
    public void Output_IntoDataFolder_IsError()
    {
        string data = Dir("Game", "Data");
        Errors(SetupValidators.OutputDirectory(Path.Combine(data, "NPC"), Dir("mods"), data, SetupModManager.MO2))
            .Should().NotBeEmpty();
    }

    [Fact]
    public void Output_ModsFolderItself_IsError()
    {
        string mods = Dir("mods");
        Errors(SetupValidators.OutputDirectory(mods, mods, null, SetupModManager.MO2)).Should().NotBeEmpty();
    }

    [Fact]
    public void Output_BareNameWithoutModsFolder_IsError() =>
        Errors(SetupValidators.OutputDirectory("NPC Output", "", null, SetupModManager.MO2)).Should().NotBeEmpty();

    [Fact]
    public void Output_VortexOutsideStaging_Warns()
    {
        string mods = Dir("staging");
        string elsewhere = Dir("elsewhere", "NPC Output");
        Warnings(SetupValidators.OutputDirectory(elsewhere, mods, null, SetupModManager.Vortex)).Should().NotBeEmpty();
    }

    [Fact]
    public void Output_NewFolderInsideModsFolder_IsFine()
    {
        string mods = Dir("mods");
        var findings = SetupValidators.OutputDirectory("NPC Output", mods, null, SetupModManager.MO2);
        Errors(findings).Should().BeEmpty();
        Warnings(findings).Should().BeEmpty();
    }

    [Fact]
    public void PluginName_SameStemAsAnEsm_DoesNotWarn() =>
        Warnings(SetupValidators.OutputPluginName("Skyrim", new[] { "Skyrim.esm", "npc.esp" }))
            .Should().BeEmpty("Skyrim.esp is not Skyrim.esm");

    [Fact]
    public void PluginName_CollidesWithLoadOrderPlugin_Warns() =>
        Warnings(SetupValidators.OutputPluginName("NPC.esp", new[] { "Skyrim.esm", "npc.esp" })).Should().NotBeEmpty();

    [Fact]
    public void PluginName_InvalidCharacters_IsError() =>
        Errors(SetupValidators.OutputPluginName("NPC:Out", null)).Should().NotBeEmpty();
}
