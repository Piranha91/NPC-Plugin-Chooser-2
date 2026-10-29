using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

public class EasyNpcTransferTests
{
    private static readonly FormKey Npc = FormKey.Factory("01347A:Skyrim.esm");
    private static readonly ModKey Default = ModKey.FromFileName("AI Overhaul.esp");
    private static readonly ModKey Faces = ModKey.FromFileName("Faces.esp");
    private static readonly ModKey OtherFaces = ModKey.FromFileName("OtherFaces.esp");

    private static EasyNpcProfileEntry Entry(string faceMod = "") => new(Npc, Default, Faces, faceMod);

    private static ModSetting Mod(bool faceGenOnly = false, string name = "Renamed in NPC2",
        string folder = "Face Pack") => new()
    {
        DisplayName = name,
        CorrespondingFolderPaths = [$@"C:\MO2\mods\{folder}"],
        CorrespondingModKeys = faceGenOnly ? [] : [Faces, OtherFaces],
        IsFaceGenOnlyEntry = faceGenOnly,
        NpcFormKeys = [Npc],
        AvailablePluginsForNpcs = faceGenOnly ? new() : new() { [Npc] = [Faces, OtherFaces] },
        NpcPluginDisambiguation = new() { [Npc] = OtherFaces }
    };

    [Theory]
    [InlineData("")]
    [InlineData("Face Pack")]
    [InlineData("::12345//Face Pack")]
    public void Profile_RoundTripsEveryField(string faceMod)
    {
        var expected = Entry(faceMod);
        EasyNpcProfile.TryParseLine(expected.Serialize(), out var parsed, out var error).Should().BeTrue(error);
        parsed.Should().Be(expected);
    }

    [Theory]
    [InlineData("Skyrim.esm#01347A=|Faces.esp|")]
    [InlineData("Skyrim.esm#01347A=Skyrim.esm||")]
    [InlineData("Skyrim.esm#ZZZZZZ=Skyrim.esm|Faces.esp|")]
    [InlineData("Skyrim.esm#01347A=Skyrim.esm|Faces.esp|::broken")]
    [InlineData("Skyrim.esm#01347A=Skyrim.esm|Faces.esp|Pack|Extra")]
    [InlineData("Skyrim.esm#01347A=Skyrim.esm|Faces.txt|")]
    [InlineData("Skyrim.esm#01347A=Skyrim.esm|Faces|")]
    [InlineData("Skyrim.esm#1201347A=Skyrim.esm|Faces.esp|")]
    [InlineData("not a profile line")]
    public void Profile_RejectsMalformedEntries(string line)
    {
        EasyNpcProfile.TryParseLine(line, out _, out var error).Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void Profile_NormalizesIdsAndReportsDuplicatesWithoutGuessing()
    {
        var errors = new List<EasyNpcIssue>();
        var entries = EasyNpcProfile.Parse([
            "  # comment", " // comment", "", Entry().Serialize(),
            "Skyrim.esm#0001347A=Skyrim.esm|OtherFaces.esp|"
        ], false, errors);
        entries.Should().ContainSingle().Which.Should().Be(Entry());
        errors.Should().ContainSingle().Which.Message.Should().Contain("Duplicate NPC");
    }

    private static string Event(string field, string? value, string time = "2026-01-01T00:00:00") =>
        Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            master = "Skyrim.esm", id = "01347A", field, newValue = value, time
        });

    [Fact]
    public void Autosave_ReplaysLatestFieldInFileOrderAndClearsFaceGenOverride()
    {
        var errors = new List<EasyNpcIssue>();
        var entries = EasyNpcProfile.Parse([
            Event("DefaultPlugin", "AI Overhaul.esp"), Event("FacePlugin", "OtherFaces.esp"),
            Event("FaceMod", "::12//Face Pack"), Event("FacePlugin", "Faces.esp", "2000-01-01T00:00:00"),
            Event("FaceMod", null)
        ], true, errors);
        errors.Should().BeEmpty();
        entries.Should().ContainSingle().Which.Should().Be(Entry());
    }

    [Fact]
    public void Autosave_SkipsTruncatedAndInvalidEventsWithoutLosingOtherFields()
    {
        var errors = new List<EasyNpcIssue>();
        var entries = EasyNpcProfile.Parse([
            Event("DefaultPlugin", "AI Overhaul.esp"), Event("FacePlugin", "Faces.esp"),
            "{\"master\":", "{\"master\":\"Skyrim.esm\",\"id\":\"01347A\",\"field\":\"FacePlugin\",\"newValue\":27}"
        ], true, errors);
        entries.Should().ContainSingle().Which.Should().Be(Entry());
        errors.Should().HaveCount(2);
    }

    [Fact]
    public void Autosave_DoesNotInventMissingPluginsOrRevertToEarlierValidChoice()
    {
        var errors = new List<EasyNpcIssue>();
        EasyNpcProfile.Parse([Event("FacePlugin", "Faces.esp")], true, errors).Should().BeEmpty();
        errors.Should().ContainSingle();
        errors.Clear();
        EasyNpcProfile.Parse([
            Event("DefaultPlugin", "AI Overhaul.esp"), Event("FacePlugin", "Faces.esp"), Event("FacePlugin", null)
        ], true, errors).Should().BeEmpty();
        errors.Should().ContainSingle();
    }

    [Fact]
    public void Autosave_PreservesFaceModLocator()
    {
        var errors = new List<EasyNpcIssue>();
        var entries = EasyNpcProfile.Parse([
            Event("DefaultPlugin", "AI Overhaul.esp"), Event("FacePlugin", "Faces.esp"),
            Event("FaceMod", "::123//Face Pack")
        ], true, errors);
        entries.Should().ContainSingle().Which.Should().Be(Entry("::123//Face Pack"));
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Import_RetainsExactPluginEvenWhenModChoiceDoesNotChange()
    {
        var mod = Mod();
        var match = EasyNpcTransfer.FindImportCandidates(Entry(), [mod]);
        match.Candidates.Should().Equal(mod);
        match.Error.Should().BeEmpty();
        EasyNpcTransfer.IsUnchanged(Entry(), mod, (mod.DisplayName, Npc), Default).Should().BeFalse("source plugin differs");
        EasyNpcTransfer.ApplyPluginChoice(Entry(), mod);
        mod.NpcPluginDisambiguation[Npc].Should().Be(Faces);
        EasyNpcTransfer.IsUnchanged(Entry(), mod, (mod.DisplayName, Npc), Default).Should().BeTrue();
        EasyNpcTransfer.TryCreateEntry(Npc, (mod.DisplayName, Npc), mod, Default, out var exported, out var error)
            .Should().BeTrue(error);
        exported.Should().Be(Entry());
    }

    [Fact]
    public void Import_DoesNotAcceptPluginJustBecauseAnotherPluginInSameModHasNpc()
    {
        var mod = Mod();
        mod.AvailablePluginsForNpcs[Npc] = [OtherFaces];
        var match = EasyNpcTransfer.FindImportCandidates(Entry(), [mod]);
        match.Candidates.Should().BeEmpty();
        match.Error.Should().NotBeEmpty();
    }

    [Fact]
    public void Import_AmbiguousModSourcesKeepModListOrderSoFirstMatchIsDeterministic()
    {
        var first = Mod();
        var second = Mod(name: "Second");
        var match = EasyNpcTransfer.FindImportCandidates(Entry(), [first, second]);
        match.IsAmbiguous.Should().BeTrue();
        match.Candidates.Should().Equal(first, second);
    }

    [Theory]
    [InlineData("Face Pack")]
    [InlineData("::12345//face pack")]
    [InlineData("Renamed in NPC2")]
    public void Import_MatchesFaceGenOverrideByFolderOrDisplayName(string name)
    {
        var mod = Mod(true);
        var entry = Entry(name) with { FacePlugin = Npc.ModKey };
        var match = EasyNpcTransfer.FindImportCandidates(entry, [mod]);
        match.Candidates.Should().Equal(mod);
        match.Entry.Should().Be(entry);
        match.Warning.Should().BeEmpty();
    }

    [Fact]
    public void Import_RedundantFaceModNamingThePluginsOwnModIsNotAnOverride()
    {
        // Pre-0.9.2 EasyNPC wrote the face plugin's own mod into FaceMod for every NPC.
        var match = EasyNpcTransfer.FindImportCandidates(Entry("Face Pack"), [Mod()]);
        match.Candidates.Should().ContainSingle();
        match.Entry.Should().Be(Entry());
        match.Warning.Should().BeEmpty();
    }

    [Fact]
    public void Import_MissingFaceGenSourceFallsBackToAppearancePluginWithWarning()
    {
        var mod = Mod();
        var match = EasyNpcTransfer.FindImportCandidates(Entry("Missing Pack"), [mod]);
        match.Candidates.Should().Equal(mod);
        match.Entry.Should().Be(Entry());
        match.Warning.Should().Contain("was not found").And.Contain("without the FaceGen override");
    }

    [Fact]
    public void Import_UnrepresentableOverrideFallsBackToAppearancePluginWithWarning()
    {
        var plugin = Mod(name: "Plugin Mod", folder: "Plugin Mod");
        var match = EasyNpcTransfer.FindImportCandidates(Entry("Face Pack"), [Mod(true), plugin]);
        match.Candidates.Should().Equal(plugin);
        match.Warning.Should().Contain("original record");
    }

    [Fact]
    public void Import_SkipsWhenNeitherOverrideNorPluginCanBeUsed()
    {
        var match = EasyNpcTransfer.FindImportCandidates(Entry("Face Pack"), [Mod(true)]);
        match.Candidates.Should().BeEmpty();
        match.Error.Should().Contain("original record").And.Contain("No mod provides");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Export_RepresentsFaceGenOnlyNpcWithOriginRecordAndRealFolderName(bool mixedMod)
    {
        var mod = Mod(!mixedMod);
        if (mixedMod)
        {
            mod.FaceGenOnlyNpcFormKeys.Add(Npc);
            mod.AvailablePluginsForNpcs.Remove(Npc);
        }
        EasyNpcTransfer.TryCreateEntry(Npc, (mod.DisplayName, Npc), mod, Default, out var entry, out var error)
            .Should().BeTrue(error);
        entry.FacePlugin.Should().Be(Npc.ModKey);
        entry.FaceMod.Should().Be("Face Pack");
        EasyNpcTransfer.FindImportCandidates(entry, [mod]).Candidates.Should().Equal(mod);
    }

    [Fact]
    public void Export_RejectsSharedFaceInsteadOfSubstitutingTargetFace()
    {
        var mod = Mod();
        EasyNpcTransfer.TryCreateEntry(Npc, (mod.DisplayName, FormKey.Factory("000800:Donors.esp")),
            mod, Default, out _, out var error).Should().BeFalse();
        error.Should().Contain("Shared face");
    }

    [Fact]
    public void Export_RejectsStalePluginDisambiguation()
    {
        var mod = Mod();
        mod.NpcPluginDisambiguation[Npc] = Default;
        EasyNpcTransfer.TryCreateEntry(Npc, (mod.DisplayName, Npc), mod, Default, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("no longer available");
    }

    [Fact]
    public void Export_DoesNotGuessWhichOfMultipleFaceGenFoldersSuppliesAssets()
    {
        var mod = Mod(true);
        mod.CorrespondingFolderPaths.Add(@"C:\MO2\mods\Other Pack");
        EasyNpcTransfer.TryCreateEntry(Npc, (mod.DisplayName, Npc), mod, Default, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("exactly one source mod folder");
    }

    [Fact]
    public void Update_PreservesDefaultAndUnchangedLinesWhileUpdatingBothAppearanceFields()
    {
        var old = Entry("::12//Old Pack");
        var replacement = Entry("Face Pack") with { DefaultPlugin = Npc.ModKey, FacePlugin = Npc.ModKey };
        var unrelated = "Other.esm#000800=Other.esm|OtherFaces.esp|Other Pack";
        var errors = new List<EasyNpcIssue>();
        var lines = EasyNpcProfile.UpdateLines(["# comment", old.Serialize(), unrelated, ""],
            new Dictionary<FormKey, EasyNpcProfileEntry> { [Npc] = replacement }, true, errors,
            out var updated, out var added);
        lines.Should().Equal("# comment", (replacement with { DefaultPlugin = Default }).Serialize(), unrelated, "");
        updated.Should().Be(1);
        added.Should().Be(0);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Update_KeepsEasyNpcLocatorForSameFaceGenModAndReportsUnchanged()
    {
        var original = (Entry("::12//Face Pack") with { FacePlugin = Npc.ModKey }).Serialize();
        var errors = new List<EasyNpcIssue>();
        var changes = new List<EasyNpcLineChange>();
        EasyNpcProfile.UpdateLines([original],
            new Dictionary<FormKey, EasyNpcProfileEntry> { [Npc] = Entry("face pack") with { FacePlugin = Npc.ModKey } },
            false, errors, out var updated, out _, changes).Should().Equal(original);
        updated.Should().Be(0);
        changes.Should().ContainSingle().Which.Kind.Should().Be(EasyNpcLineChangeKind.Unchanged);
    }

    [Fact]
    public void Update_NormalAppearanceClearsOldFaceGenOverride()
    {
        var errors = new List<EasyNpcIssue>();
        var lines = EasyNpcProfile.UpdateLines([Entry("Face Pack").Serialize()],
            new Dictionary<FormKey, EasyNpcProfileEntry> { [Npc] = Entry() }, false, errors, out _, out _);
        lines.Should().Equal(Entry().Serialize());
    }

    [Fact]
    public void Update_UnsupportedOrAbsentReplacementDoesNotEraseExistingFaceGenOverride()
    {
        var original = Entry("Face Pack").Serialize();
        var errors = new List<EasyNpcIssue>();
        EasyNpcProfile.UpdateLines([original], new Dictionary<FormKey, EasyNpcProfileEntry>(), true,
            errors, out var updated, out _).Should().Equal(original);
        updated.Should().Be(0);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void Update_AddMissingHonorsFlag(bool addMissing, int expectedAdded)
    {
        var errors = new List<EasyNpcIssue>();
        var lines = EasyNpcProfile.UpdateLines([], new Dictionary<FormKey, EasyNpcProfileEntry> { [Npc] = Entry() },
            addMissing, errors, out var updated, out var added);
        updated.Should().Be(0);
        added.Should().Be(expectedAdded);
        lines.Should().HaveCount(expectedAdded);
        errors.Should().HaveCount(addMissing ? 0 : 1);
    }

    [Fact]
    public void Update_DuplicatesAndMalformedRowsRemainUnchangedAndAreReported()
    {
        var originals = new[] { Entry().Serialize(), Entry("Face Pack").Serialize(), "bad line" };
        var errors = new List<EasyNpcIssue>();
        EasyNpcProfile.UpdateLines(originals,
            new Dictionary<FormKey, EasyNpcProfileEntry> { [Npc] = Entry() with { FacePlugin = OtherFaces } },
            true, errors, out var updated, out var added).Should().Equal(originals);
        updated.Should().Be(0);
        added.Should().Be(0);
        errors.Should().HaveCount(3);
    }
}
