using System.IO;
using System.Text;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// The Recast half of <see cref="OutputValidator"/>: <c>BuildRecastIndex</c> over a temp Data folder
/// and <c>CheckRecast</c> per backend. Reached like <see cref="OutputValidatorIndexTests"/>: an
/// uninitialized validator with only <c>_settings</c> and <c>_environmentStateProvider</c> set.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class OutputValidatorRecastTests
{
    private static readonly FormKey Lydia = FormKey.Factory("0A2C8E:Skyrim.esm");

    private static OutputValidator Validator(Settings settings)
    {
        var env = NpcChooserTestEnvironment.Invalid();
        Reflect.SetField(env, "<OutputPluginName>k__BackingField", "NPCTest");
        var v = Reflect.Uninitialized<OutputValidator>();
        Reflect.SetField(v, "_settings", settings);
        Reflect.SetField(v, "_environmentStateProvider", env);
        return v;
    }

    private static void WritePatch(string dataFolder, string relPath, int priority, string body)
    {
        var path = Path.Combine(RecastPatchReader.GetPatchesDir(dataFolder), relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"[manifest]\nname = \"x\"\npriority = {priority}\napi_version = 1\n" + body);
    }

    private const string LydiaFace = "[[npcs]]\ntarget = \"0x000A2C8E~Skyrim.esm\"\nface = \"0x801~Pack.esp\"\n";

    private static List<ValidationIssue> Check(Settings settings, string dataFolder, string? editorId = null)
    {
        var v = Validator(settings);
        var log = new StringBuilder();
        var index = Reflect.Invoke<object>(v, "BuildRecastIndex", dataFolder, log)!;
        var result = new ValidationRunResult();
        Reflect.InvokeVoid(v, "CheckRecast", Lydia, "Lydia", "Some Mod", editorId == null ? null : MakeNpc(editorId), index, result);
        return result.Issues;
    }

    private static Mutagen.Bethesda.Skyrim.INpcGetter MakeNpc(string editorId) =>
        MutagenFixtures.NewNpc(MutagenFixtures.NewMod("Rec.esp"), editorId: editorId);

    [Theory]
    [InlineData(OutputBackend.ESP, "overriding the appearance this app wrote")]
    [InlineData(OutputBackend.SkyPatcher, "between a new game and a loaded save")]
    public void ThirdPartyFace_IsAnErrorOutsideTheRecastBackend(OutputBackend backend, string wording)
    {
        using var tmp = new TempDir("recastval");
        WritePatch(tmp.Path, "Pack\\pack.toml", 100, LydiaFace);

        var issues = Check(new Settings { OutputBackend = backend }, tmp.Path);

        issues.Should().ContainSingle();
        issues[0].Severity.Should().Be(ValidationSeverity.Error);
        issues[0].Check.Should().Be(ValidationCheckKind.Recast);
        issues[0].Issue.Should().Contain(wording);
        issues[0].WinningSource.Should().Be("Pack\\pack.toml (priority 100)");
    }

    [Theory]
    [InlineData(999, ValidationSeverity.Info)]    // below NPC2's 1000
    [InlineData(1000, ValidationSeverity.Info)]   // tie: "NPCTest.toml" > "Apack.toml" by name
    [InlineData(1001, ValidationSeverity.Error)]
    public void RecastBackend_RanksLikeRecast(int thirdPartyPriority, ValidationSeverity expected)
    {
        using var tmp = new TempDir("recastval");
        WritePatch(tmp.Path, "NPC Plugin Chooser\\NPCTest.toml", 1000, LydiaFace.Replace("0x801~Pack", "0x801~NPCTest"));
        WritePatch(tmp.Path, "Pack\\Apack.toml", thirdPartyPriority, LydiaFace);

        var issues = Check(new Settings { OutputBackend = OutputBackend.Recast }, tmp.Path);

        issues.Should().ContainSingle("this app's own patch is never flagged")
            .Which.Severity.Should().Be(expected);
    }

    [Fact]
    public void RecastBackend_WithoutOwnPatch_IsAnError()
    {
        using var tmp = new TempDir("recastval");
        WritePatch(tmp.Path, "Pack\\pack.toml", 1, LydiaFace);

        Check(new Settings { OutputBackend = OutputBackend.Recast }, tmp.Path)
            .Should().ContainSingle().Which.Issue.Should().Contain("own Recast patch was not found");
    }

    [Fact]
    public void EditorIdTarget_MatchesTheRecipientsEditorId_AndSaysItNeedsTweaks()
    {
        using var tmp = new TempDir("recastval");
        WritePatch(tmp.Path, "pack.toml", 100, "[replacements]\nhousecarlwhiterun = \"Pack_Lydia\"\n");

        Check(new Settings { OutputBackend = OutputBackend.ESP }, tmp.Path, editorId: "HousecarlWhiterun")
            .Should().ContainSingle().Which.Issue.Should().Contain("powerofthree's Tweaks");
        Check(new Settings { OutputBackend = OutputBackend.ESP }, tmp.Path, editorId: "SomeoneElse")
            .Should().BeEmpty();
    }

    [Fact]
    public void SkippedFile_IsNotIndexedButIsCounted()
    {
        using var tmp = new TempDir("recastval");
        var path = Path.Combine(RecastPatchReader.GetPatchesDir(tmp.Path), "broken.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, LydiaFace); // no [manifest]

        var v = Validator(new Settings());
        var index = Reflect.Invoke<object>(v, "BuildRecastIndex", tmp.Path, new StringBuilder())!;
        var skipped = (System.Collections.IList)index.GetType().GetProperty("SkippedFiles")!.GetValue(index)!;
        skipped.Count.Should().Be(1);

        Check(new Settings { OutputBackend = OutputBackend.ESP }, tmp.Path).Should().BeEmpty();
    }
}
