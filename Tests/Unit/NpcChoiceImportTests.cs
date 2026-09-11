using System.IO;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

public class NpcChoiceImportTests
{
    private const string Target = "01347A:Skyrim.esm";
    private const string Donor = "000801:SharedFaces.esp";

    private static Dictionary<string, NpcChoiceDto?> Parse(string json)
    {
        NpcChoiceImport.TryParse(json, out var choices, out var error).Should().BeTrue(error);
        error.Should().BeEmpty();
        return choices;
    }

    [Fact]
    public void ExportFormat_PreservesOwnAndSharedFaces()
    {
        var exported = new Dictionary<string, NpcChoiceDto>
        {
            [Target] = new("Base Game", Target),
            ["013475:Skyrim.esm"] = new("Shared Faces", Donor)
        };
        var json = JSONhandler<Dictionary<string, NpcChoiceDto>>.Serialize(exported, out var saved, out var error);
        saved.Should().BeTrue(error);
        json.Should().NotContain("SourceNpcWasInferred");

        var choices = Parse(json);

        choices.Should().HaveCount(2);
        choices[Target].Should().Be(new NpcChoiceDto("Base Game", Target));
        choices["013475:Skyrim.esm"].Should().Be(new NpcChoiceDto("Shared Faces", Donor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentToken_PreservesDonorAndIgnoresOutputMetadata(bool skyPatcher)
    {
        var token = new NpcToken
        {
            UseSkyPatcherMode = skyPatcher,
            CreatedPlugins = [ModKey.FromNameAndExtension("NPC.esp")],
            ProcessedNpcs = new()
            {
                [FormKey.Factory(Target)] = new()
                {
                    ModName = "Shared Faces",
                    SourceNpcFormKey = FormKey.Factory(Donor),
                    AppearancePlugin = ModKey.FromNameAndExtension("SharedFaces.esp"),
                    OutputPlugin = ModKey.FromNameAndExtension("NPC.esp")
                }
            },
            SkippedNpcs = new() { [FormKey.Factory("013475:Skyrim.esm")] = "Skipped by screening" },
            AssetDependencies = new() { LooseFiles = ["textures/example.dds"] }
        };
        var json = JSONhandler<NpcToken>.Serialize(token, out var saved, out var error);
        saved.Should().BeTrue(error);

        var choices = Parse(json);

        choices.Should().ContainSingle();
        choices[Target].Should().Be(new NpcChoiceDto("Shared Faces", Donor));
    }

    [Theory]
    [InlineData("")]
    [InlineData(", \"SourceNpcFormKey\": null")]
    public void LegacyToken_UsesTargetAsSource(string sourceProperty)
    {
        var choices = Parse($$"""
            { "ProcessedNpcs": { "{{Target}}": { "ModName": "Base Game" {{sourceProperty}} } } }
            """);

        choices[Target].Should().Be(new NpcChoiceDto("Base Game", Target) { SourceNpcWasInferred = true });
    }

    [Fact]
    public void CommittedLegacyTokens_AreImportableWithoutGameOrOutputPlugins()
    {
        var paths = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData", "GoldenReference"),
            "NPC_Token.json", SearchOption.AllDirectories);
        paths.Should().HaveCount(12);

        foreach (var path in paths)
        {
            var choices = Parse(File.ReadAllText(path));
            choices.Should().NotBeEmpty(path);
            foreach (var (target, choice) in choices)
            {
                FormKey.TryFactory(target, out _).Should().BeTrue();
                choice!.ModName.Should().NotBeNullOrWhiteSpace();
                choice.SourceNpcFormKey.Should().Be(target);
            }
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"CreationDate\":\"bootstrap\",\"ProcessedNpcs\":{}}")]
    public void EmptyExportOrBootstrapToken_ContainsNoChoices(string json)
    {
        Parse(json).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"SelectedAppearanceMods\":{}}")]
    [InlineData("{\"CreationDate\":\"no payload\",\"CreatedPlugins\":[]}")]
    [InlineData("{\"ProcessedNpcs\":null}")]
    [InlineData("{\"ProcessedNpcs\":[]}")]
    [InlineData("{\"ProcessedNpcs\":{},\"ProcessedNpcs\":{}}")]
    public void UnsupportedOrBrokenJson_IsRejectedWithoutThrowing(string json)
    {
        NpcChoiceImport.TryParse(json, out var choices, out var error).Should().BeFalse();
        choices.Should().BeEmpty();
        error.Should().Contain("NPC_Token.json");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedEntries_AreRetainedForValidationAlongsideValidChoices(bool token)
    {
        var entries = $$"""
            {
                "{{Target}}": { "ModName": "Shared Faces", "SourceNpcFormKey": "{{Donor}}" },
                "bad-key": { "ModName": "A mod", "SourceNpcFormKey": "bad-source" },
                "013475:Skyrim.esm": null,
                "013476:Skyrim.esm": 123,
                "013477:Skyrim.esm": { "ModName": 123, "SourceNpcFormKey": [] },
                "013478:Skyrim.esm": { "ModName": "A mod", "SourceNpcFormKey": "" }
            }
            """;

        var choices = Parse(token ? "{\"ProcessedNpcs\":" + entries + "}" : entries);

        choices.Should().HaveCount(6);
        choices[Target].Should().Be(new NpcChoiceDto("Shared Faces", Donor));
        choices["bad-key"].Should().Be(new NpcChoiceDto("A mod", "bad-source"));
        choices["013475:Skyrim.esm"].Should().BeNull();
        choices["013476:Skyrim.esm"].Should().BeNull();
        choices["013477:Skyrim.esm"].Should().Be(new NpcChoiceDto(null, null));
        choices["013478:Skyrim.esm"]!.SourceNpcFormKey.Should().BeEmpty();
    }

    [Fact]
    public void ExportWithoutSource_DoesNotUseLegacyTokenFallback()
    {
        Parse($$"""{ "{{Target}}": { "ModName": "Base Game" } }""")[Target]!
            .SourceNpcFormKey.Should().BeNull();
    }

    [Fact]
    public void PropertyCasingAndAdditionalFields_MatchExistingJsonCompatibility()
    {
        var choices = Parse($$"""
            {
                "processednpcs": {
                    "{{Target}}": { "modname": "Shared Faces", "sourcenpcformkey": "{{Donor}}", "FutureField": {} }
                },
                "FutureMetadata": []
            }
            """);

        choices[Target].Should().Be(new NpcChoiceDto("Shared Faces", Donor));
    }
}
