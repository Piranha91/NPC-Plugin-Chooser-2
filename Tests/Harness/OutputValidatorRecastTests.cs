using System.IO;
using System.Text;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
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

    // ── Recast backend: this app's own patch ─────────────────────────────────

    private static List<ValidationIssue> ValidateRecast(string dataFolder)
    {
        var v = Validator(new Settings { OutputBackend = OutputBackend.Recast });
        var log = new StringBuilder();
        var recastIndex = Reflect.Invoke<object>(v, "BuildRecastIndex", dataFolder, log)!;
        string skyRoot = Path.Combine(dataFolder, "SKSE", "Plugins", "SkyPatcher", "npc");
        var skyIndex = Reflect.Invoke<object>(v, "BuildSkyPatcherIndex",
            skyRoot, Path.Combine(skyRoot, "NPC Plugin Chooser", "NPCTest.ini"), log)!;
        var result = new ValidationRunResult();
        Reflect.InvokeVoid(v, "ValidateNpcRecast", Lydia, Lydia, false, "Some Mod", "Lydia", null,
            new ModSetting(), recastIndex, null, new List<IModListingGetter<ISkyrimModGetter>>(), skyIndex,
            dataFolder, null, result, log);
        return result.Issues;
    }

    [Fact]
    public void RecastBackend_OwnPatchWithoutAnEntryForTheNpc_IsAnError()
    {
        using var tmp = new TempDir("recastval");
        WritePatch(tmp.Path, "NPC Plugin Chooser\\NPCTest.toml", 1000,
            "[[npcs]]\ntarget = \"0x00013BB9~Skyrim.esm\"\nface = \"0x801~NPCTest.esp\"\n");

        ValidateRecast(tmp.Path).Should().ContainSingle()
            .Which.Issue.Should().Contain("has no face entry for this NPC");
    }

    [Fact]
    public void RecastBackend_SkyPatcherVisualStyleOnTheSameNpc_IsTheFlipError()
    {
        using var tmp = new TempDir("recastval");
        var ini = Path.Combine(tmp.Path, "SKSE", "Plugins", "SkyPatcher", "npc", "Other", "other.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(ini, "filterByNPCs=Skyrim.esm|A2C8E:copyVisualStyle=Other.esp|801\n");

        var issues = ValidateRecast(tmp.Path); // no own patch either: one Recast row, one flip row
        issues.Should().Contain(i => i.Check == ValidationCheckKind.SkyPatcher
                                     && i.Severity == ValidationSeverity.Error
                                     && i.Issue.Contains("between a new game and a loaded save"));
        issues.Should().Contain(i => i.Check == ValidationCheckKind.Recast);
    }

    [Theory]
    [InlineData(OutputBackend.Recast, false, true)]
    [InlineData(OutputBackend.Recast, true, false)]
    [InlineData(OutputBackend.ESP, false, false)]
    public void DeployGate_RequiresTheRecastPatchOnlyUnderTheRecastBackend(OutputBackend backend, bool tomlExists, bool blocked)
    {
        using var tmp = new TempDir("recastval");
        string toml = Path.Combine(RecastInterface.GetOutputDir(tmp.Path), "NPCTest.toml");
        if (tomlExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(toml)!);
            File.WriteAllText(toml, "");
        }
        var listings = new List<IModListingGetter<ISkyrimModGetter>>
        {
            new ModListing<ISkyrimModGetter>(ModKey.FromFileName("NPCTest.esp"), MutagenFixtures.NewMod("NPCTest.esp"), enabled: true)
        };

        var block = Reflect.Invoke<string?>(Validator(new Settings { OutputBackend = backend }), "EvaluateDeployGate",
            listings, Path.Combine(tmp.Path, "none.ini"), toml, null);

        (block != null).Should().Be(blocked, block);
    }
}
