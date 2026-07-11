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
/// SPID *_DISTR.ini parsing + matching, mirroring behaviors verified against
/// the SPID source (github.com/powerof3/Spell-Perk-Item-Distributor): section
/// layout Form|Strings|Forms|Levels|Traits|IdxOrCount|Chance, the config
/// sanitizer's bare-FormID rewrite, string/form filter modifiers (+/-/*),
/// trait letters, and record-level filter evaluation (LookupNPC.cpp).
/// </summary>
public class SpidOutfitConfigParserTests
{
    private readonly SpidOutfitConfigParser _parser = new();

    private static readonly FormKey NpcKey = FormKey.Factory("001234:MyNpcs.esp");
    private static readonly FormKey FactionKey = FormKey.Factory("01BCC0:Skyrim.esm");
    private static readonly FormKey OutfitKey = FormKey.Factory("0ABCDE:Outfits.esp");
    private static readonly FormKey KeywordKey = FormKey.Factory("013794:Skyrim.esm");

    private static NpcRuntimeFacts Facts(
        bool isFemale = false,
        bool isUnique = false,
        bool isChild = false,
        ushort level = 10,
        bool pcLevelMult = false,
        IEnumerable<FormKey>? factions = null,
        IEnumerable<string>? keywordEdids = null)
    {
        return new NpcRuntimeFacts
        {
            NpcFormKey = NpcKey,
            EditorId = "TestNpc",
            Name = "Test Npc",
            IsFemale = isFemale,
            IsUnique = isUnique,
            IsChild = isChild,
            Level = level,
            HasPcLevelMult = pcLevelMult,
            FactionFormKeys = factions?.ToHashSet() ?? new HashSet<FormKey>(),
            KeywordEditorIds = (keywordEdids ?? Array.Empty<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            KeywordFormKeys = new HashSet<FormKey> { KeywordKey },
            OriginPluginNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MyNpcs.esp" },
            SelfAndTemplateFormKeys = new HashSet<FormKey> { NpcKey },
            SelfAndTemplateEditorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TestNpc" },
        };
    }

    /// <summary>Typed-form resolution stub for form filters.</summary>
    private static ResolvedFilterForm? ResolveForm(RuntimeFormIdentifier id)
    {
        if (id.Kind == RuntimeFormIdentifierKind.EditorId)
        {
            return id.EditorId switch
            {
                "GuardFaction" => new ResolvedFilterForm(FactionKey, SpidFilterFormType.Faction),
                "SomeKeyword" => new ResolvedFilterForm(KeywordKey, SpidFilterFormType.Keyword),
                "TestNpc" => new ResolvedFilterForm(NpcKey, SpidFilterFormType.Npc),
                "AList" => new ResolvedFilterForm(FormKey.Factory("000FFF:Lists.esp"),
                    SpidFilterFormType.FormList,
                    new[] { new ResolvedFilterForm(FactionKey, SpidFilterFormType.Faction) }),
                _ => null,
            };
        }
        if (id.Kind == RuntimeFormIdentifierKind.ModAndLocalId &&
            ModKey.TryFromNameAndExtension(id.ModName!, out var mk))
        {
            var fk = new FormKey(mk, id.LocalOrRuntimeId);
            if (fk.Equals(FactionKey)) return new ResolvedFilterForm(fk, SpidFilterFormType.Faction);
            return null;
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Parsing
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseFile_OnlyOutfitKeys_OutsideSections_CommentsSkipped()
    {
        var lines = new[]
        {
            "; a comment",
            "# another comment",
            "Spell = 0x123~Magic.esp",              // different record type
            "Outfit = 0xABCDE~Outfits.esp|NONE|GuardFaction",
            "FinalOutfit = 0xABCDE~Outfits.esp",
            "[SomeSection]",
            "Outfit = 0x111~Hidden.esp",            // under a named section — invisible to SPID
        };
        var result = _parser.ParseFile(lines, "test_DISTR.ini");
        result.Should().HaveCount(2);
        result[0].IsFinal.Should().BeFalse();
        result[1].IsFinal.Should().BeTrue();
    }

    [Fact]
    public void ParseFile_SanitizesBareZeroPaddedFormIds()
    {
        // SPID's sanitizer turns "0001B1D3" into "0x1B1D3" (a runtime FormID).
        var result = _parser.ParseFile(new[] { "Outfit = 0001B1D3" }, "test_DISTR.ini");
        result.Should().HaveCount(1);
        result[0].OutfitForm.Kind.Should().Be(RuntimeFormIdentifierKind.RuntimeFormId);
        result[0].OutfitForm.LocalOrRuntimeId.Should().Be(0x1B1D3u);
    }

    [Fact]
    public void ParseEntry_AllSections()
    {
        var entry = _parser.ParseEntry(
            "0xABCDE~Outfits.esp|Bandit,-Chief,*guard,Nord+Warrior|GuardFaction,-TestNpc|5/20,onehanded(10/50)|F/U/-C|NONE|100",
            "test_DISTR.ini", 1, isFinal: false);
        entry.Should().NotBeNull();
        entry!.OutfitForm.Kind.Should().Be(RuntimeFormIdentifierKind.ModAndLocalId);
        entry.OutfitForm.ModName.Should().Be("Outfits.esp");
        entry.OutfitForm.LocalOrRuntimeId.Should().Be(0xABCDEu);

        entry.StringsMatch.Should().Equal("Bandit");
        entry.StringsNot.Should().Equal("Chief");
        entry.StringsAny.Should().Equal("guard");
        entry.StringsAll.Should().Equal("Nord", "Warrior");

        entry.FormsMatch.Should().ContainSingle().Which.EditorId.Should().Be("GuardFaction");
        entry.FormsNot.Should().ContainSingle().Which.EditorId.Should().Be("TestNpc");

        entry.MinLevel.Should().Be(5);
        entry.MaxLevel.Should().Be(20);
        entry.UnevaluatedFilters.Should().Contain("skill-level filter");

        entry.Traits.Female.Should().BeTrue();
        entry.Traits.Unique.Should().BeTrue();
        entry.Traits.Child.Should().BeFalse();

        entry.ChancePercent.Should().Be(100.0);
    }

    [Fact]
    public void ParseEntry_TooManySections_IsInvalid()
    {
        _parser.ParseEntry("A|B|C|D|E|F|G|H", "f.ini", 1, false).Should().BeNull();
    }

    [Fact]
    public void ParseEntry_DeterministicChanceSuffix_Parsed()
    {
        var entry = _parser.ParseEntry("SomeOutfit|NONE|NONE|NONE|NONE|NONE|50!", "f.ini", 1, false);
        entry!.ChancePercent.Should().Be(50.0);
    }

    [Fact]
    public void ParseIdentifier_Shapes()
    {
        SpidOutfitConfigParser.ParseIdentifier("0x123~Mod.esp").Kind
            .Should().Be(RuntimeFormIdentifierKind.ModAndLocalId);
        SpidOutfitConfigParser.ParseIdentifier("Mod.esp").Kind
            .Should().Be(RuntimeFormIdentifierKind.ModName);
        SpidOutfitConfigParser.ParseIdentifier("0x14012345").Kind
            .Should().Be(RuntimeFormIdentifierKind.RuntimeFormId);
        SpidOutfitConfigParser.ParseIdentifier("GuardOutfit").Kind
            .Should().Be(RuntimeFormIdentifierKind.EditorId);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Matching
    // ─────────────────────────────────────────────────────────────────────

    private SpidOutfitEntry Entry(string value)
        => _parser.ParseEntry(value, "test_DISTR.ini", 1, false)!;

    [Fact]
    public void Match_SubHundredChance_NeverApplies()
    {
        var entry = Entry("SomeOutfit|NONE|GuardFaction|NONE|NONE|NONE|99");
        _parser.MatchesNpc(entry, Facts(factions: new[] { FactionKey }), ResolveForm)
            .Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_StringFilter_MatchesKeywordNameOrEditorId()
    {
        _parser.MatchesNpc(Entry("O|TestNpc"), Facts(), ResolveForm).Applies.Should().BeTrue();
        _parser.MatchesNpc(Entry("O|Test Npc"), Facts(), ResolveForm).Applies.Should().BeTrue();
        _parser.MatchesNpc(Entry("O|ActorTypeNPC"), Facts(keywordEdids: new[] { "ActorTypeNPC" }), ResolveForm)
            .Applies.Should().BeTrue();
        _parser.MatchesNpc(Entry("O|SomebodyElse"), Facts(), ResolveForm).Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_StringFilter_WildcardContains()
    {
        _parser.MatchesNpc(Entry("O|*estnp"), Facts(), ResolveForm).Applies.Should().BeTrue();
        _parser.MatchesNpc(Entry("O|*zzz"), Facts(), ResolveForm).Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_StringFilter_ExclusionVetoes()
    {
        _parser.MatchesNpc(Entry("O|-TestNpc"), Facts(), ResolveForm).Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_FormFilter_FactionAndPluginName()
    {
        _parser.MatchesNpc(Entry("O|NONE|GuardFaction"), Facts(factions: new[] { FactionKey }), ResolveForm)
            .Applies.Should().BeTrue();
        _parser.MatchesNpc(Entry("O|NONE|GuardFaction"), Facts(), ResolveForm)
            .Applies.Should().BeFalse();
        // Plugin-name filter matches the NPC's origin plugin.
        _parser.MatchesNpc(Entry("O|NONE|MyNpcs.esp"), Facts(), ResolveForm).Applies.Should().BeTrue();
        _parser.MatchesNpc(Entry("O|NONE|Other.esp"), Facts(), ResolveForm).Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_FormFilter_FormListRecurses()
    {
        _parser.MatchesNpc(Entry("O|NONE|AList"), Facts(factions: new[] { FactionKey }), ResolveForm)
            .Applies.Should().BeTrue();
        _parser.MatchesNpc(Entry("O|NONE|AList"), Facts(), ResolveForm).Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_FormFilter_UnresolvedMatchForm_MakesEntryUnmatchable()
    {
        _parser.MatchesNpc(Entry("O|NONE|NotInLoadOrder"), Facts(), ResolveForm)
            .Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_FormFilter_UnresolvedNotForm_DoesNotVeto()
    {
        _parser.MatchesNpc(Entry("O|NONE|GuardFaction,-NotInLoadOrder"),
                Facts(factions: new[] { FactionKey }), ResolveForm)
            .Applies.Should().BeTrue();
    }

    [Fact]
    public void Match_Traits()
    {
        _parser.MatchesNpc(Entry("O|NONE|NONE|NONE|F"), Facts(isFemale: true), ResolveForm)
            .Applies.Should().BeTrue();
        _parser.MatchesNpc(Entry("O|NONE|NONE|NONE|F"), Facts(isFemale: false), ResolveForm)
            .Applies.Should().BeFalse();
        _parser.MatchesNpc(Entry("O|NONE|NONE|NONE|-U"), Facts(isUnique: false), ResolveForm)
            .Applies.Should().BeTrue();
        _parser.MatchesNpc(Entry("O|NONE|NONE|NONE|C"), Facts(isChild: false), ResolveForm)
            .Applies.Should().BeFalse();
    }

    [Fact]
    public void Match_LevelRange_RecordLevel()
    {
        _parser.MatchesNpc(Entry("O|NONE|NONE|5/20"), Facts(level: 10), ResolveForm)
            .Applies.Should().BeTrue();
        _parser.MatchesNpc(Entry("O|NONE|NONE|15/20"), Facts(level: 10), ResolveForm)
            .Applies.Should().BeFalse();
        // PC-level-mult NPCs can't be evaluated → treated as matching + noted.
        var match = _parser.MatchesNpc(Entry("O|NONE|NONE|15/20"), Facts(level: 10, pcLevelMult: true), ResolveForm);
        match.Applies.Should().BeTrue();
        match.Approximations.Should().Contain(a => a.Contains("PC-level-mult"));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Discovery
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Discover_TopLevelDistrInisOnly_OrdinalSort()
    {
        var root = Directory.CreateTempSubdirectory("spid-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Zebra_DISTR.ini"), "");
            File.WriteAllText(Path.Combine(root, "Alpha_DISTR.ini"), "");
            File.WriteAllText(Path.Combine(root, "NotADistr.ini"), "");
            File.WriteAllText(Path.Combine(root, "Wrong_DISTR.txt"), "");
            Directory.CreateDirectory(Path.Combine(root, "Sub"));
            File.WriteAllText(Path.Combine(root, "Sub", "Nested_DISTR.ini"), "");

            var found = _parser.DiscoverConfigFiles(root);
            found.Select(Path.GetFileName).Should().Equal("Alpha_DISTR.ini", "Zebra_DISTR.ini");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
