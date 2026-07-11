using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// SkyPatcher npc-ini parsing + matching, mirroring behaviors verified against
/// the SkyPatcher source (github.com/Zzyxz/SkyPatcher npc.cpp): ';' comments,
/// key=value regex extraction stopping at ':', comma-split values with "none"
/// dropped, the direct-filterByNpcs pass that bypasses vetoes, the sweep gate
/// quirk, findObject's OR-across-categories model, and the BFS config walk.
/// </summary>
public class SkyPatcherOutfitConfigParserTests
{
    private readonly SkyPatcherOutfitConfigParser _parser = new();

    private static readonly FormKey NpcKey = FormKey.Factory("001234:MyNpcs.esp");
    private static readonly FormKey OtherNpcKey = FormKey.Factory("00AAAA:MyNpcs.esp");
    private static readonly FormKey FactionKey = FormKey.Factory("01BCC0:Skyrim.esm");
    private static readonly FormKey RaceKey = FormKey.Factory("013746:Skyrim.esm");

    private static NpcRuntimeFacts Facts(
        bool isFemale = false,
        string? editorId = "TestNpc",
        FormKey? race = null,
        IEnumerable<FormKey>? factions = null,
        IEnumerable<string>? keywordEdids = null)
    {
        return new NpcRuntimeFacts
        {
            NpcFormKey = NpcKey,
            EditorId = editorId,
            Name = "Test Npc",
            IsFemale = isFemale,
            RaceFormKey = race ?? RaceKey,
            FactionFormKeys = factions?.ToHashSet() ?? new HashSet<FormKey>(),
            KeywordEditorIds = (keywordEdids ?? Array.Empty<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            OriginPluginNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MyNpcs.esp" },
            SelfAndTemplateFormKeys = new HashSet<FormKey> { NpcKey },
        };
    }

    /// <summary>Identifier resolution stub: "Plugin|hex" → FormKey; a few
    /// well-known EditorIDs; everything else unresolved.</summary>
    private static FormKey? Resolve(string identifier)
    {
        if (identifier.Contains('|'))
        {
            var parsed = SkyPatcherOutfitConfigParser.ParseIdentifier(identifier);
            if (parsed.Kind == RuntimeFormIdentifierKind.ModAndLocalId &&
                ModKey.TryFromNameAndExtension(parsed.ModName!, out var mk))
            {
                return new FormKey(mk, parsed.LocalOrRuntimeId);
            }
            return null;
        }
        return identifier switch
        {
            "TestNpc" => NpcKey,
            "GuardFaction" => FactionKey,
            "NordRace" => RaceKey,
            _ => null,
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Line parsing
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseFile_ExtractsOutfitLines_SkipsCommentsAndNonOutfit()
    {
        var lines = new[]
        {
            ";filterByNpcs=MyNpcs.esp|1234:outfitDefault=Some.esp|100",
            "",
            "filterByNpcs=MyNpcs.esp|1234:skin=Some.esp|200", // no outfitDefault
            "filterByNpcs=MyNpcs.esp|1234:outfitDefault=Some.esp|300",
        };
        var result = _parser.ParseFile(lines, "test.ini");
        result.Should().HaveCount(1);
        result[0].OutfitIdentifier.Should().Be("Some.esp|300");
        result[0].LineNumber.Should().Be(4);
        result[0].FilterByNpcs.Should().ContainSingle().Which.Should().Be("MyNpcs.esp|1234");
    }

    [Fact]
    public void ParseFile_ValueStopsAtColon_AndKeysAreCaseInsensitive()
    {
        var lines = new[]
        {
            "FILTERBYNPCS=MyNpcs.esp|1234,none,MyNpcs.esp|AAAA:OUTFITDEFAULT=Some.esp|100:filterByGender=female",
        };
        var result = _parser.ParseFile(lines, "test.ini");
        result.Should().HaveCount(1);
        result[0].FilterByNpcs.Should().Equal("MyNpcs.esp|1234", "MyNpcs.esp|AAAA"); // "none" dropped
        result[0].OutfitIdentifier.Should().Be("Some.esp|100");
        result[0].FilterByGender.Should().Be("female");
    }

    [Fact]
    public void ParseFile_RecordsUnevaluatedFilterKeys()
    {
        var lines = new[]
        {
            "filterByFactions=GuardFaction:restrictToSkill=onehanded(10/50):outfitDefault=Some.esp|100",
        };
        var result = _parser.ParseFile(lines, "test.ini");
        result.Should().HaveCount(1);
        result[0].UnevaluatedFilterKeys.Should().Contain("restrictToSkill");
    }

    [Fact]
    public void ParseIdentifier_PluginAndLocalId_MasksTo24Bits()
    {
        var id = SkyPatcherOutfitConfigParser.ParseIdentifier("Skyrim.esm|FF01B1D3");
        id.Kind.Should().Be(RuntimeFormIdentifierKind.ModAndLocalId);
        id.ModName.Should().Be("Skyrim.esm");
        id.LocalOrRuntimeId.Should().Be(0x01B1D3u);
    }

    [Fact]
    public void ParseIdentifier_BareValue_IsEditorId()
    {
        var id = SkyPatcherOutfitConfigParser.ParseIdentifier("GuardOutfit01");
        id.Kind.Should().Be(RuntimeFormIdentifierKind.EditorId);
        id.EditorId.Should().Be("GuardOutfit01");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Matching
    // ─────────────────────────────────────────────────────────────────────

    private SkyPatcherOutfitInstruction ParseSingle(string line)
        => _parser.ParseFile(new[] { line }, "test.ini").Single();

    [Fact]
    public void Match_DirectNpcList_Applies()
    {
        var instr = ParseSingle("filterByNpcs=MyNpcs.esp|1234:outfitDefault=Some.esp|100");
        var match = _parser.MatchesNpc(instr, Facts(), Resolve);
        match.Applies.Should().BeTrue();
        match.ViaDirectNpcList.Should().BeTrue();
    }

    [Fact]
    public void Match_DirectNpcList_ByEditorId_Applies()
    {
        var instr = ParseSingle("filterByNpcs=TestNpc:outfitDefault=Some.esp|100");
        _parser.MatchesNpc(instr, Facts(), Resolve).Applies.Should().BeTrue();
    }

    [Fact]
    public void Match_DirectNpcList_BypassesGenderVeto()
    {
        // Faithful quirk: NPCs listed in filterByNpcs are patched directly —
        // the gender filter never runs for them.
        var instr = ParseSingle("filterByNpcs=MyNpcs.esp|1234:filterByGender=female:outfitDefault=Some.esp|100");
        _parser.MatchesNpc(instr, Facts(isFemale: false), Resolve).Applies.Should().BeTrue();
    }

    [Fact]
    public void Match_DirectNpcList_OtherNpc_NoSweepWithoutBroadFilters()
    {
        var instr = ParseSingle("filterByNpcs=MyNpcs.esp|AAAA:outfitDefault=Some.esp|100");
        var facts = Facts(); // our NPC is 1234, list names AAAA
        _parser.MatchesNpc(instr, facts, Resolve).Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_SweepGateQuirk_NpcsPlusRacesOnly_RaceFilterIsDead()
    {
        // filterByRaces is NOT in the sweep gate list: a line with filterByNpcs
        // + filterByRaces and nothing else never sweeps, so the race filter
        // can't match other NPCs (mirrors process_patch_instructions:387).
        var instr = ParseSingle("filterByNpcs=MyNpcs.esp|AAAA:filterByRaces=NordRace:outfitDefault=Some.esp|100");
        var facts = Facts(race: RaceKey);
        _parser.MatchesNpc(instr, facts, Resolve).Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_RacesOnly_SweepsAndMatches()
    {
        var instr = ParseSingle("filterByRaces=NordRace:outfitDefault=Some.esp|100");
        _parser.MatchesNpc(instr, Facts(race: RaceKey), Resolve).Applies.Should().BeTrue();
        _parser.MatchesNpc(instr, Facts(race: FormKey.Factory("013747:Skyrim.esm")), Resolve)
            .Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_Factions_AndSemantics()
    {
        var otherFaction = FormKey.Factory("012345:Skyrim.esm");
        var instr = ParseSingle("filterByFactions=GuardFaction,Skyrim.esm|12345:outfitDefault=Some.esp|100");

        _parser.MatchesNpc(instr, Facts(factions: new[] { FactionKey, otherFaction }), Resolve)
            .Applies.Should().BeTrue();
        // Only one of the two AND'd factions present → no match.
        _parser.MatchesNpc(instr, Facts(factions: new[] { FactionKey }), Resolve)
            .Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_FactionsOr_AnySemantics()
    {
        var instr = ParseSingle("filterByFactionsOr=GuardFaction,Skyrim.esm|12345:outfitDefault=Some.esp|100");
        _parser.MatchesNpc(instr, Facts(factions: new[] { FactionKey }), Resolve)
            .Applies.Should().BeTrue();
    }

    [Fact]
    public void Match_GenderVeto_AppliesToSweep()
    {
        var instr = ParseSingle("filterByFactions=GuardFaction:filterByGender=female:outfitDefault=Some.esp|100");
        _parser.MatchesNpc(instr, Facts(isFemale: true, factions: new[] { FactionKey }), Resolve)
            .Applies.Should().BeTrue();
        _parser.MatchesNpc(instr, Facts(isFemale: false, factions: new[] { FactionKey }), Resolve)
            .Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_EditorIdContains_CaseInsensitive()
    {
        var instr = ParseSingle("filterByEditorIdContains=estnp:outfitDefault=Some.esp|100");
        _parser.MatchesNpc(instr, Facts(editorId: "TESTNPC"), Resolve).Applies.Should().BeTrue();
        _parser.MatchesNpc(instr, Facts(editorId: "Nobody"), Resolve).Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_ExcludedNpc_Vetoed()
    {
        var instr = ParseSingle(
            "filterByFactions=GuardFaction:filterByNpcsExcluded=MyNpcs.esp|1234:outfitDefault=Some.esp|100");
        _parser.MatchesNpc(instr, Facts(factions: new[] { FactionKey }), Resolve)
            .Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_NoSelectionFilters_PatchesEverything()
    {
        var instr = ParseSingle("outfitDefault=Some.esp|100");
        _parser.MatchesNpc(instr, Facts(), Resolve).Applies.Should().BeTrue();
    }

    [Fact]
    public void Match_ModNames_FiltersByOriginPlugin()
    {
        var instr = ParseSingle("filterByModNames=MyNpcs.esp:outfitDefault=Some.esp|100");
        _parser.MatchesNpc(instr, Facts(), Resolve).Applies.Should().BeTrue();

        var other = ParseSingle("filterByModNames=SomethingElse.esp:outfitDefault=Some.esp|100");
        _parser.MatchesNpc(other, Facts(), Resolve).Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_KeywordsByEditorId()
    {
        var instr = ParseSingle("filterByKeywords=ActorTypeNPC:outfitDefault=Some.esp|100");
        _parser.MatchesNpc(instr, Facts(keywordEdids: new[] { "ActorTypeNPC" }), Resolve)
            .Applies.Should().BeTrue();
        _parser.MatchesNpc(instr, Facts(keywordEdids: new[] { "ActorTypeCreature" }), Resolve)
            .Applies.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Discovery (BFS walk, plugin gating, exclusion)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Discover_BfsOrder_RootFilesBeforeSubfolders_AlphaWithin()
    {
        var root = Directory.CreateTempSubdirectory("skypatcher-test-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Beta"));
            Directory.CreateDirectory(Path.Combine(root, "Alpha"));
            File.WriteAllText(Path.Combine(root, "zzz.ini"), "");
            File.WriteAllText(Path.Combine(root, "aaa.ini"), "");
            File.WriteAllText(Path.Combine(root, "Beta", "b.ini"), "");
            File.WriteAllText(Path.Combine(root, "Alpha", "a.ini"), "");
            File.WriteAllText(Path.Combine(root, "notes.txt"), ""); // no ".ini" — skipped

            var found = _parser.DiscoverConfigFiles(root, _ => true);
            found.Select(f => f.RelativePath).Should().Equal(
                "aaa.ini", "zzz.ini",
                Path.Combine("Alpha", "a.ini"),
                Path.Combine("Beta", "b.ini"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_PluginGatedNames_SkippedWhenPluginAbsent()
    {
        var root = Directory.CreateTempSubdirectory("skypatcher-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Present.esp.ini"), "");
            File.WriteAllText(Path.Combine(root, "Absent.esp.ini"), "");
            File.WriteAllText(Path.Combine(root, "ungated.ini"), "");

            var found = _parser.DiscoverConfigFiles(root,
                plugin => plugin.Equals("Present.esp", StringComparison.OrdinalIgnoreCase));
            found.Select(f => f.RelativePath).Should().Equal("Present.esp.ini", "ungated.ini");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_ExclusionPredicate_DropsNpc2SelfFolder()
    {
        var root = Directory.CreateTempSubdirectory("skypatcher-test-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "NPC Plugin Chooser"));
            File.WriteAllText(Path.Combine(root, "NPC Plugin Chooser", "MyOutput.ini"), "");
            File.WriteAllText(Path.Combine(root, "external.ini"), "");

            var found = _parser.DiscoverConfigFiles(root, _ => true,
                rel => rel.StartsWith("NPC Plugin Chooser" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            found.Select(f => f.RelativePath).Should().Equal("external.ini");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
