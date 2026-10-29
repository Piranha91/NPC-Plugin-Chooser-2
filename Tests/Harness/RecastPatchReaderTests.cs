using System.IO;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// <see cref="RecastPatchReader"/> — parses Recast patch TOML by Recast 1.6.3's rules. The fixture
/// text is modelled on Recast's own <c>examples/example_recast.toml</c>.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class RecastPatchReaderTests
{
    private const string Manifest = "[manifest]\nname = \"Pack\"\npriority = 100\napi_version = 1\n";

    private static RecastPatchFile Parse(string body, string manifest = Manifest) =>
        RecastPatchReader.Parse(manifest + body, "C:\\x\\pack.toml");

    private static readonly FormKey Lydia = FormKey.Factory("0A2C8E:Skyrim.esm");

    // ── Manifest ──────────────────────────────────────────────────────────────

    [Fact]
    public void Manifest_NameAndPriorityAreRead()
    {
        var file = Parse("");
        file.IsLoaded.Should().BeTrue();
        file.ManifestName.Should().Be("Pack");
        file.Priority.Should().Be(100);
        file.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "missing or invalid [manifest]")]
    [InlineData("[manifest]\nname = \"x\"\n", "api_version is required")]
    [InlineData("[manifest]\napi_version = 2\n", "api_version 2 not supported")]
    [InlineData("[manifest\napi_version = 1\n", "TOML parse error")]
    public void BadFile_IsSkippedWhole(string manifest, string because)
    {
        var file = Parse("[[npcs]]\ntarget = \"Lydia\"\nface = \"Other\"\n", manifest);
        file.IsLoaded.Should().BeFalse();
        file.Faces.Should().BeEmpty();
        file.Errors.Should().ContainSingle().Which.Should().Contain(because);
    }

    [Fact]
    public void MissingPriority_IsZero()
    {
        Parse("", "[manifest]\napi_version = 1\n").Priority.Should().Be(0);
    }

    // ── The three syntaxes ────────────────────────────────────────────────────

    [Fact]
    public void AllThreeSyntaxes_CoexistInOneFile()
    {
        var file = Parse(
            "[[npcs]]\ntarget = \"Lydia\"\nface = \"VIS2_FDN_Lydia\"\n" +
            "[[npcs]]\ntarget = \"0x000A2C8E~Skyrim.esm\"\nsource = \"VIS2_FDN_LydiaAlt\"\n" +
            "[[npcs]]\ntarget = \"Aela\"\nbody = \"SomeSkin\"\n" +
            "[replacements]\nMjoll = \"VIS2_FDN_Mjoll\"\n\"0x1327E~Skyrim.esm\" = { form_id = \"0x801~Foundation.esp\" }\n" +
            "[[recasts]]\ntarget = { form_id = \"0x0001327E~Skyrim.esm\" }\nsource = { editor_id = \"Donor\" }\n");

        file.Errors.Should().BeEmpty();
        file.Faces.Select(f => (f.Section, f.Target.ToString(), f.Source.ToString())).Should().BeEquivalentTo(new[]
        {
            ("npcs", "Lydia", "VIS2_FDN_Lydia"),
            ("npcs", "0x000A2C8E~Skyrim.esm", "VIS2_FDN_LydiaAlt"),
            ("replacements", "Mjoll", "VIS2_FDN_Mjoll"),
            ("replacements", "0x0001327E~Skyrim.esm", "0x00000801~Foundation.esp"),
            ("recasts", "0x0001327E~Skyrim.esm", "Donor"),
        }, "a body-only [[npcs]] block carries no face");
    }

    [Fact]
    public void NpcsBlock_WithFaceAndSource_IsSkipped()
    {
        var file = Parse("[[npcs]]\ntarget = \"Lydia\"\nface = \"A\"\nsource = \"B\"\n");
        file.IsLoaded.Should().BeTrue();
        file.Faces.Should().BeEmpty();
        file.Errors.Should().ContainSingle().Which.Should().Contain("choose one");
    }

    [Fact]
    public void BadEntry_SkipsOnlyThatEntry()
    {
        var file = Parse(
            "[[npcs]]\ntarget = \"A2C8E~Skyrim.esm\"\nface = \"X\"\n" +
            "[[npcs]]\ntarget = \"Lydia\"\nface = \"Y\"\n");
        file.Faces.Should().ContainSingle().Which.Source.EditorId.Should().Be("Y");
        file.Errors.Should().ContainSingle().Which.Should().Contain("contains '~'");
    }

    // ── Selectors ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0x000A2C8E~Skyrim.esm", "0A2C8E:Skyrim.esm")]
    [InlineData("0XA2C8E~Skyrim.esm", "0A2C8E:Skyrim.esm")]
    [InlineData("0x02000801~Mod.esp", "000801:Mod.esp")]
    [InlineData("0xFE001801~Light.esp", "000801:Light.esp")]
    [InlineData("0x801~Light.esl", "000801:Light.esl")]
    public void FormIdSelector_KeepsTheLocalIdOnly(string raw, string expected)
    {
        RecastPatchReader.TryParseSelectorString(raw, "t", out var sel, out var error).Should().BeTrue(error);
        sel.FormKey.Should().Be(FormKey.Factory(expected));
    }

    [Theory]
    [InlineData("0x~Skyrim.esm")]
    [InlineData("0x000A2C8E~")]
    [InlineData("0x123456789~Skyrim.esm")]
    [InlineData("Lydia~Skyrim.esm")]
    [InlineData("")]
    public void MalformedSelector_IsRejected(string raw)
    {
        RecastPatchReader.TryParseSelectorString(raw, "t", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void SelectorIdentity_DiffersBetweenFormIdAndEditorIdForms()
    {
        RecastPatchReader.TryParseSelectorString("0xA2C8E~SKYRIM.ESM", "t", out var a, out _);
        RecastPatchReader.TryParseSelectorString("0x000A2C8E~Skyrim.esm", "t", out var b, out _);
        RecastPatchReader.TryParseSelectorString("Lydia", "t", out var c, out _);
        a.Identity.Should().Be(b.Identity);
        c.Identity.Should().NotBe(b.Identity);
    }

    [Fact]
    public void Resolve_EditorIdThroughMap_FormIdDirectly()
    {
        var map = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Lydia"] = new() { Lydia },
        };
        RecastPatchReader.Resolve(new RecastSelector(null, "lydia"), map).Should().Equal(Lydia);
        RecastPatchReader.Resolve(new RecastSelector(null, "Nobody"), map).Should().BeEmpty();
        RecastPatchReader.Resolve(new RecastSelector(Lydia, null), map).Should().Equal(Lydia);
    }

    // ── Precedence ────────────────────────────────────────────────────────────

    [Fact]
    public void Outranks_ByPriorityThenFileName()
    {
        RecastPatchFile F(string name, int priority) => new() { Path = "C:\\p\\" + name, IsLoaded = true, Priority = priority };

        RecastPatchReader.Outranks(F("a.toml", 2), F("z.toml", 1)).Should().BeTrue();
        RecastPatchReader.Outranks(F("z.toml", 1), F("a.toml", 1)).Should().BeTrue();
        RecastPatchReader.Outranks(F("a.toml", 1), F("z.toml", 1)).Should().BeFalse();
    }

    // ── Files on disk ─────────────────────────────────────────────────────────

    [Fact]
    public void EnumeratePatchFiles_IsRecursiveAndCaseInsensitive()
    {
        using var tmp = new TempDir("recastread");
        var dir = RecastPatchReader.GetPatchesDir(tmp.Path);
        Directory.CreateDirectory(Path.Combine(dir, "Sub"));
        File.WriteAllText(Path.Combine(dir, "b.toml"), "");
        File.WriteAllText(Path.Combine(dir, "Sub", "a.TOML"), "");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "");

        RecastPatchReader.EnumeratePatchFiles(tmp.Path).Select(Path.GetFileName)
            .Should().BeEquivalentTo("b.toml", "a.TOML");
        RecastPatchReader.EnumeratePatchFiles(Path.Combine(tmp.Path, "missing")).Should().BeEmpty();
    }

    [Fact]
    public void RoundTrip_ReadsWhatRecastInterfaceWrites()
    {
        using var tmp = new TempDir("recastread");
        var env = NpcChooserTestEnvironment.Invalid();
        env.OutputMod = new SkyrimMod(ModKey.FromName("NPCTest", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var writer = new RecastInterface(env, new Settings { RecastPatchPriority = 1000 });
        var surrogate = FormKey.Factory("000801:NPCTest.esp");
        writer.AddFace(Lydia, surrogate, "Lydia \"Housecarl\"");
        writer.WriteToml(tmp.Path).Should().BeTrue();

        var file = RecastPatchReader.ReadFile(RecastPatchReader.EnumeratePatchFiles(tmp.Path).Single());

        file.Errors.Should().BeEmpty();
        file.ManifestName.Should().Be(RecastInterface.ManifestName);
        file.Priority.Should().Be(1000);
        file.Faces.Should().ContainSingle().Which.Should().Be(
            new RecastFaceEntry(new RecastSelector(Lydia, null), new RecastSelector(surrogate, null), "npcs"));
    }
}
