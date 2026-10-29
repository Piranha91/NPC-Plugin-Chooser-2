using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="InstallLocationCheck"/>: N.P.C.2 installed as an MO2 mod runs from the virtual Data
/// folder, so its Settings.json collided with Pandora's Data\Settings.json.
/// </summary>
public class InstallLocationCheckTests
{
    private const string Data = @"C:\Games\Skyrim Special Edition\Data";
    private const string Mods = @"C:\MO2\mods";

    [Fact]
    public void ExeInVirtualDataFolder_Warns() =>
        InstallLocationCheck.GetWarning(Data + @"\", Data, Mods).Should().Contain("Data folder");

    [Fact]
    public void ExeInModFolder_Warns() =>
        InstallLocationCheck.GetWarning(@"C:\MO2\mods\NPC Plugin Chooser 2\", Data, Mods).Should().Contain("Mods folder");

    [Fact]
    public void ExeOutside_DoesNotWarn() =>
        InstallLocationCheck.GetWarning(@"C:\Modding\NPC2\", Data, Mods).Should().BeNull();

    [Fact]
    public void SiblingWithSharedPrefix_IsNotInside() =>
        InstallLocationCheck.IsInside(@"C:\MO2\mods2\NPC2", Mods).Should().BeFalse();

    [Fact]
    public void BlankFolders_AreIgnored() =>
        InstallLocationCheck.GetWarning(@"C:\Modding\NPC2\", "", null).Should().BeNull();
}
