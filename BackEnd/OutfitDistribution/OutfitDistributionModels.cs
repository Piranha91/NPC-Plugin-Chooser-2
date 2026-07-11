using System;
using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;

// ─────────────────────────────────────────────────────────────────────────────
//  Shared models for the runtime-outfit-distribution simulators (SkyPatcher /
//  SPID). Parsing and matching are kept as plain data + pure logic so the unit
//  tests can exercise them without a Mutagen environment; only
//  OutfitDisplayResolver touches Mutagen types.
//
//  Format facts in this folder were verified against the actual tool sources
//  (not docs, which lag behind):
//   - SkyPatcher: github.com/Zzyxz/SkyPatcher @ main (npc.cpp readConfig /
//     create_patch_instruction / process_patch_instructions / findObject,
//     utility.cpp GetFormFromIdentifier / extractData).
//   - SPID: github.com/powerof3/Spell-Perk-Item-Distributor @ main
//     (LookupConfigs.* / Parser.h / LookupNPC.cpp / Distribute.h /
//     Outfits/OutfitManager.cpp) + powerof3/CLibUtil distribution.hpp
//     (get_configs / get_record).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>How a raw config-file form reference identifies its target.</summary>
public enum RuntimeFormIdentifierKind
{
    /// <summary>"Plugin.esp|1B1D3" (SkyPatcher) or "0x1B1D3~Plugin.esp" (SPID):
    /// a local FormID within an explicit plugin.</summary>
    ModAndLocalId,

    /// <summary>SPID bare hex ("0x14012345"): a RUNTIME FormID whose top byte
    /// (or 0xFExxx prefix) is a load-order slot. SPID's config sanitizer turns
    /// fully-written vanilla IDs ("0001B1D3") into this shape, so in practice
    /// these usually point at slot 00 = Skyrim.esm.</summary>
    RuntimeFormId,

    /// <summary>A bare EditorID (both tools fall back to this when the value
    /// is neither hex nor contains a plugin separator).</summary>
    EditorId,

    /// <summary>SPID only: a value containing ".es" with no '~' is a plugin
    /// name filter ("Mod.esp"), matching any NPC originating from that file.
    /// (SPID's check is case-sensitive on ".es" — mirrored faithfully.)</summary>
    ModName,
}

/// <summary>A parsed config-file form reference. Resolution to a concrete
/// FormKey happens later against the load order (see OutfitDisplayResolver);
/// parsers only classify.</summary>
public sealed record RuntimeFormIdentifier
{
    public RuntimeFormIdentifierKind Kind { get; init; }
    public string? ModName { get; init; }       // ModAndLocalId / ModName
    public uint LocalOrRuntimeId { get; init; } // ModAndLocalId (local) / RuntimeFormId (full runtime ID)
    public string? EditorId { get; init; }      // EditorId kind
    public string Raw { get; init; } = string.Empty;

    public override string ToString() => Raw;
}

// ─────────────────────────────────────────────────────────────────────────────
//  SkyPatcher npc instruction
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One SkyPatcher npc-ini line that carries an <c>outfitDefault=</c> directive,
/// with the filter keys the display simulator evaluates. Filter values are the
/// raw comma-split, trimmed items ("none" entries already dropped), exactly as
/// SkyPatcher's extractData produces them.
/// </summary>
public sealed class SkyPatcherOutfitInstruction
{
    /// <summary>Config file path relative to the SkyPatcher npc root — used for
    /// user-facing provenance messages and for ordering diagnostics.</summary>
    public string SourceFile { get; init; } = string.Empty;

    public int LineNumber { get; init; }

    /// <summary>Raw value of <c>outfitDefault=</c> ("Plugin|ID" or EditorID).</summary>
    public string OutfitIdentifier { get; init; } = string.Empty;

    // Selection filters (any listed item matches, per findObject's OR-across-
    // categories model). Empty list = key absent.
    public List<string> FilterByNpcs { get; init; } = new();
    public List<string> FilterByKeywords { get; init; } = new();          // AND within
    public List<string> FilterByKeywordsOr { get; init; } = new();        // OR within
    public List<string> FilterByRaces { get; init; } = new();
    public List<string> FilterByDefaultOutfits { get; init; } = new();
    public List<string> FilterByClass { get; init; } = new();
    public List<string> FilterByCombatStyle { get; init; } = new();
    public List<string> FilterByFactions { get; init; } = new();          // AND within
    public List<string> FilterByFactionsOr { get; init; } = new();        // OR within
    public List<string> FilterByEditorIdContains { get; init; } = new();  // AND within
    public List<string> FilterByEditorIdContainsOr { get; init; } = new();// OR within
    public List<string> FilterByModNames { get; init; } = new();

    // Vetoes.
    public string? FilterByGender { get; init; }          // "female"/"male"
    public string? RestrictToGender { get; init; }
    public List<string> FilterByNpcsExcluded { get; init; } = new();
    public List<string> FilterByKeywordsExcluded { get; init; } = new();
    public List<string> FilterByFactionsExcluded { get; init; } = new();
    public List<string> FilterByEditorIdContainsExcluded { get; init; } = new();
    public List<string> FilterByClassExclude { get; init; } = new();
    public List<string> RestrictToRaces { get; init; } = new();
    public List<string> RestrictToVoiceType { get; init; } = new();

    /// <summary>Filter keys present on the line that the simulator does not
    /// evaluate (e.g. restrictToSkill, restrictToFlags). Treated as passing;
    /// surfaced in provenance so the user can see the approximation.</summary>
    public List<string> UnevaluatedFilterKeys { get; init; } = new();
}

// ─────────────────────────────────────────────────────────────────────────────
//  SPID outfit entry
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>SPID trait filter (Traits section, '/'-separated M/F/U/S/C/L/T/D
/// with '-' negation). Null = unconstrained.</summary>
public sealed record SpidTraits
{
    public bool? Female { get; init; }
    public bool? Unique { get; init; }
    public bool? Summonable { get; init; }
    public bool? Child { get; init; }
    public bool? Leveled { get; init; }
    public bool? Teammate { get; init; }
    public bool? StartsDead { get; init; }
}

/// <summary>
/// One parsed <c>Outfit =</c> / <c>FinalOutfit =</c> line from a *_DISTR.ini.
/// Section layout (split on '|', trailing sections optional, "NONE" = empty):
/// Form|StringFilters|FormFilters|LevelFilters|Traits|IdxOrCount|Chance.
/// </summary>
public sealed class SpidOutfitEntry
{
    public string SourceFile { get; init; } = string.Empty; // file name for provenance
    public int LineNumber { get; init; }
    public bool IsFinal { get; init; }

    public RuntimeFormIdentifier OutfitForm { get; init; } = new();

    // String filters (comma-split): MATCH = plain, ANY = '*' prefix (contains),
    // NOT = '-' prefix, ALL = '+'-joined groups. Matching is case-insensitive
    // against: NPC name, NPC EditorID (incl. template-base EditorIDs), and
    // NPC+race keyword EditorIDs (LookupNPC.cpp HasStringFilter).
    public List<string> StringsMatch { get; init; } = new();
    public List<string> StringsAny { get; init; } = new();
    public List<string> StringsNot { get; init; } = new();
    public List<string> StringsAll { get; init; } = new();

    // Form filters: MATCH (any-of), NOT, ALL. Each resolves to a form whose
    // type decides the check (faction/race/class/combat style/keyword-by-form/
    // NPC/voice/outfit/spell/armor/formlist) or a plugin-name filter.
    public List<RuntimeFormIdentifier> FormsMatch { get; init; } = new();
    public List<RuntimeFormIdentifier> FormsNot { get; init; } = new();
    public List<RuntimeFormIdentifier> FormsAll { get; init; } = new();

    // Actor level range (levels section, "min" or "min/max"). Skill-level
    // chunks ("skill(min/max)") are recorded as unevaluated.
    public ushort? MinLevel { get; init; }
    public ushort? MaxLevel { get; init; }

    public SpidTraits Traits { get; init; } = new();

    /// <summary>Distribution chance in percent (0-100, default 100). The
    /// preview only treats 100 as applying — sub-100 chances are per-actor
    /// random at runtime and cannot be depicted deterministically.</summary>
    public double ChancePercent { get; init; } = 100.0;

    /// <summary>Filter aspects present but not evaluated by the simulator
    /// (skill-level filters, teammate trait, etc.). Treated as passing;
    /// surfaced in provenance.</summary>
    public List<string> UnevaluatedFilters { get; init; } = new();
}

// ─────────────────────────────────────────────────────────────────────────────
//  NPC facts snapshot
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Record-level facts about the TARGET NPC (the conflict-winning override in
/// the user's load order — the record the runtime distributors will see),
/// pre-extracted so filter matching is pure and unit-testable. Built by
/// OutfitDisplayResolver from Mutagen; tests construct it directly.
/// </summary>
public sealed class NpcRuntimeFacts
{
    public FormKey NpcFormKey { get; init; }
    public string? EditorId { get; init; }
    public string? Name { get; init; }
    public bool IsFemale { get; init; }
    public bool IsUnique { get; init; }
    public bool IsSummonable { get; init; }
    public bool IsChild { get; init; }
    /// <summary>True when the record inherits from a leveled-character
    /// template (SPID's 'L' trait approximation at record level).</summary>
    public bool IsLeveled { get; init; }
    public bool StartsDead { get; init; }
    public bool IsEssential { get; init; }
    public bool IsProtected { get; init; }
    public bool HasPcLevelMult { get; init; }
    public bool HasAutoCalcStats { get; init; }
    public ushort Level { get; init; }

    public FormKey? RaceFormKey { get; init; }
    public string? RaceEditorId { get; init; }
    public FormKey? ClassFormKey { get; init; }
    public FormKey? CombatStyleFormKey { get; init; }
    public FormKey? VoiceTypeFormKey { get; init; }
    public FormKey? SkinFormKey { get; init; }
    public FormKey? DefaultOutfitFormKey { get; init; }

    /// <summary>EditorIDs of the NPC's + its race's keywords (case-insensitive
    /// matching). Both tools match keywords through the race as well.</summary>
    public HashSet<string> KeywordEditorIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<FormKey> KeywordFormKeys { get; init; } = new();
    public HashSet<FormKey> FactionFormKeys { get; init; } = new();
    public HashSet<FormKey> SpellFormKeys { get; init; } = new();
    public HashSet<FormKey> PerkFormKeys { get; init; } = new();
    public HashSet<FormKey> InventoryItemFormKeys { get; init; } = new();

    /// <summary>The NPC's own FormKey plus template-base FormKeys (SPID's IDs
    /// list matches NPC form filters against the template chain too).</summary>
    public HashSet<FormKey> SelfAndTemplateFormKeys { get; init; } = new();
    /// <summary>EditorIDs of self + template bases (SPID string filters).</summary>
    public HashSet<string> SelfAndTemplateEditorIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Origin plugins of self + template bases (plugin-name filters;
    /// SkyPatcher's filterByModNames checks the record's ORIGIN file, SPID's
    /// mod filter checks whether the file contains the record).</summary>
    public HashSet<string> OriginPluginNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
