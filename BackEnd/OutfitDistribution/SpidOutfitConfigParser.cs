using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;

/// <summary>The form types SPID's has_form check distinguishes for NPC form
/// filters (LookupNPC.cpp). The resolver classifies each filter form so the
/// matcher can stay Mutagen-free.</summary>
public enum SpidFilterFormType
{
    Keyword,
    Faction,
    Race,
    Class,
    CombatStyle,
    VoiceType,
    Npc,
    Outfit,
    Spell,
    Perk,
    Armor,
    FormList,
    OtherItem,
    /// <summary>Resolved to a form type the record-level simulation cannot
    /// evaluate (e.g. Location) — treated as non-matching with a note.</summary>
    Unsupported,
}

/// <summary>A form filter resolved against the load order. For FormList the
/// members are pre-expanded (SPID recurses into list members).</summary>
public sealed record ResolvedFilterForm(
    FormKey Key,
    SpidFilterFormType Type,
    IReadOnlyList<ResolvedFilterForm>? ListMembers = null);

/// <summary>
/// Parses SPID <c>*_DISTR.ini</c> files and evaluates <c>Outfit =</c> /
/// <c>FinalOutfit =</c> entries against an NPC. Mirrors the verified behavior
/// of SPID (github.com/powerof3/Spell-Perk-Item-Distributor @ main) and
/// CLibUtil's config discovery:
///
/// <list type="bullet">
/// <item>Discovery: <c>Data\*.ini</c> — top level only, file name must contain
/// "_DISTR" and the extension must be exactly ".ini" (both case-sensitive);
/// the resulting path list is sorted ordinally (std::ranges::sort), which
/// defines processing order.</item>
/// <item>Only keys outside any [section] header are read (CSimpleIni section
/// ""); ';' and '#' start comments.</item>
/// <item>Entry layout: <c>Form|StringFilters|FormFilters|LevelFilters|Traits|
/// IdxOrCount|Chance</c>; trailing sections optional; "NONE" = empty. More
/// than 7 sections make the entry invalid (skipped with a log by SPID).</item>
/// <item>SPID pre-sanitizes values: bare zero-padded FormIDs like
/// <c>0001B1D3</c> become <c>0x1B1D3</c> (a runtime FormID in load-order slot
/// 00), and hex literals get leading zeros stripped.</item>
/// <item>Outfits distribute via <c>for_first_form</c>: the FIRST entry (in
/// config order) whose filters pass wins; remaining entries are skipped.</item>
/// </list>
/// </summary>
public class SpidOutfitConfigParser
{
    // ─────────────────────────────────────────────────────────────────────
    //  Discovery
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Returns the *_DISTR.ini files of <paramref name="dataFolder"/>
    /// in SPID's processing order (ordinal full-path sort).</summary>
    public List<string> DiscoverConfigFiles(string dataFolder)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(dataFolder) || !Directory.Exists(dataFolder)) return results;

        string[] files;
        try { files = Directory.GetFiles(dataFolder); }
        catch { return results; }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            // Mirrors get_configs: extension exactly ".ini" and "_DISTR"
            // anywhere in the name, both case-sensitive.
            if (!name.EndsWith(".ini", StringComparison.Ordinal)) continue;
            if (!name.Contains("_DISTR", StringComparison.Ordinal)) continue;
            results.Add(file);
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Parsing
    // ─────────────────────────────────────────────────────────────────────

    // SPID's config sanitizer (LookupConfigs.cpp detail::sanitize).
    private static readonly Regex ReBareFormId = new(@"\b00+([0-9a-fA-F]{1,6})\b", RegexOptions.Compiled);
    private static readonly Regex ReLeadingZeros = new(@"(0x00+)([0-9a-fA-F]+)", RegexOptions.Compiled);

    private static string Sanitize(string value)
    {
        var v = ReBareFormId.Replace(value, "0x$1");
        v = ReLeadingZeros.Replace(v, "0x$2");
        return v;
    }

    /// <summary>Parses one _DISTR.ini's lines, returning Outfit / FinalOutfit
    /// entries in declaration order.</summary>
    public List<SpidOutfitEntry> ParseFile(IReadOnlyList<string> lines, string fileName)
    {
        var result = new List<SpidOutfitEntry>();
        bool inNamedSection = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line[0] == ';' || line[0] == '#') continue;

            if (line[0] == '[')
            {
                // SPID reads only the section-less part of the file
                // (GetSection("")); anything under a named section is invisible
                // to it.
                inNamedSection = true;
                continue;
            }
            if (inNamedSection) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line.Substring(0, eq).Trim();
            bool isFinal = false;
            if (key.StartsWith("Final", StringComparison.Ordinal))
            {
                isFinal = true;
                key = key.Substring(5);
            }
            if (!key.Equals("Outfit", StringComparison.Ordinal)) continue;

            var value = Sanitize(line.Substring(eq + 1).Trim());
            var entry = ParseEntry(value, fileName, i + 1, isFinal);
            if (entry != null) result.Add(entry);
        }

        return result;
    }

    /// <summary>Parses the value part of an Outfit entry. Returns null for
    /// structurally invalid entries (SPID skips those with a warning).</summary>
    public SpidOutfitEntry? ParseEntry(string value, string fileName, int lineNumber, bool isFinal)
    {
        var sections = value.Split('|');
        if (sections.Length > 7) return null; // "Too many sections" — invalid
        string Section(int idx) => idx < sections.Length ? sections[idx].Trim() : string.Empty;

        static bool IsValid(string s) => s.Length > 0 && !s.Equals("NONE", StringComparison.OrdinalIgnoreCase);
        static List<string> SplitEntry(string s) =>
            IsValid(s) ? s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : new List<string>();

        var formSection = Section(0);
        if (!IsValid(formSection)) return null; // MissingDistributableFormException

        var unevaluated = new List<string>();

        // Strings: '+' combine (ALL), '-' exclusion (NOT), '*' contains (ANY).
        var stringsMatch = new List<string>();
        var stringsAny = new List<string>();
        var stringsNot = new List<string>();
        var stringsAll = new List<string>();
        foreach (var str in SplitEntry(Section(1)))
        {
            if (str.Contains('+'))
            {
                stringsAll.AddRange(str.Split('+').Select(x => x.Trim()).Where(x => x.Length > 0));
            }
            else if (str.StartsWith('-'))
            {
                stringsNot.Add(str.Substring(1));
            }
            else if (str.StartsWith('*'))
            {
                stringsAny.Add(str.Substring(1));
            }
            else
            {
                stringsMatch.Add(str);
            }
        }

        // Form filters: '+' combine (ALL), '-' exclusion (NOT).
        var formsMatch = new List<RuntimeFormIdentifier>();
        var formsNot = new List<RuntimeFormIdentifier>();
        var formsAll = new List<RuntimeFormIdentifier>();
        foreach (var ids in SplitEntry(Section(2)))
        {
            if (ids.Contains('+'))
            {
                formsAll.AddRange(ids.Split('+').Select(x => x.Trim()).Where(x => x.Length > 0)
                    .Select(ParseIdentifier));
            }
            else if (ids.StartsWith('-'))
            {
                formsNot.Add(ParseIdentifier(ids.Substring(1)));
            }
            else
            {
                formsMatch.Add(ParseIdentifier(ids));
            }
        }

        // Levels: plain chunks are actor level "min" or "min/max" (last plain
        // chunk wins, mirroring SPID's overwrite); skill chunks "sk(min/max)"
        // are runtime state the preview can't depict → unevaluated.
        ushort? minLevel = null, maxLevel = null;
        foreach (var chunk in SplitEntry(Section(3)))
        {
            if (chunk.Contains('('))
            {
                unevaluated.Add("skill-level filter");
                continue;
            }
            var parts = chunk.Split('/');
            if (parts.Length > 1)
            {
                if (ushort.TryParse(parts[0], out var mn)) minLevel = mn;
                if (ushort.TryParse(parts[1], out var mx)) maxLevel = mx;
            }
            else if (ushort.TryParse(chunk, out var exact))
            {
                minLevel = exact;
                maxLevel = exact;
            }
        }

        // Traits: '/'-separated single-letter flags with '-' negation.
        bool? female = null, unique = null, summonable = null, child = null,
            leveled = null, teammate = null, startsDead = null;
        var traitsSection = Section(4);
        if (IsValid(traitsSection))
        {
            foreach (var trait in traitsSection.Split('/').Select(t => t.Trim()).Where(t => t.Length > 0))
            {
                switch (trait)
                {
                    case "M": case "-F": female = false; break;
                    case "F": case "-M": female = true; break;
                    case "U": unique = true; break;
                    case "-U": unique = false; break;
                    case "S": summonable = true; break;
                    case "-S": summonable = false; break;
                    case "C": child = true; break;
                    case "-C": child = false; break;
                    case "L": leveled = true; break;
                    case "-L": leveled = false; break;
                    case "T": teammate = true; break;
                    case "-T": teammate = false; break;
                    case "D": startsDead = true; break;
                    case "-D": startsDead = false; break;
                }
            }
        }

        // Section 5 (IdxOrCount) is irrelevant for outfits. Section 6: chance
        // percent, optional trailing '!' (deterministic per-NPC seed — still
        // random from the preview's standpoint).
        double chance = 100.0;
        var chanceSection = Section(6);
        if (IsValid(chanceSection))
        {
            var numeric = chanceSection.EndsWith('!') ? chanceSection[..^1] : chanceSection;
            if (double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                chance = parsed;
            }
        }

        return new SpidOutfitEntry
        {
            SourceFile = fileName,
            LineNumber = lineNumber,
            IsFinal = isFinal,
            OutfitForm = ParseIdentifier(formSection),
            StringsMatch = stringsMatch,
            StringsAny = stringsAny,
            StringsNot = stringsNot,
            StringsAll = stringsAll,
            FormsMatch = formsMatch,
            FormsNot = formsNot,
            FormsAll = formsAll,
            MinLevel = minLevel,
            MaxLevel = maxLevel,
            Traits = new SpidTraits
            {
                Female = female, Unique = unique, Summonable = summonable,
                Child = child, Leveled = leveled, Teammate = teammate, StartsDead = startsDead,
            },
            ChancePercent = chance,
            UnevaluatedFilters = unevaluated,
        };
    }

    /// <summary>Parses a SPID form reference (CLibUtil get_record):
    /// "0x123~Plugin.esp" → local ID in plugin; a value containing ".es"
    /// (case-sensitive) → plugin-name filter; pure hex → runtime FormID
    /// (load-order slot in the top byte); anything else → EditorID.</summary>
    public static RuntimeFormIdentifier ParseIdentifier(string raw)
    {
        var str = raw.Trim();
        int tilde = str.IndexOf('~');
        if (tilde >= 0)
        {
            var idPart = str.Substring(0, tilde).Trim();
            var modPart = str.Substring(tilde + 1).Trim();
            if (idPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) idPart = idPart.Substring(2);
            if (uint.TryParse(idPart, NumberStyles.HexNumber, null, out var localId))
            {
                return new RuntimeFormIdentifier
                {
                    Kind = RuntimeFormIdentifierKind.ModAndLocalId,
                    ModName = modPart,
                    LocalOrRuntimeId = localId & 0xFFFFFF,
                    Raw = raw,
                };
            }
            return new RuntimeFormIdentifier { Kind = RuntimeFormIdentifierKind.EditorId, EditorId = str, Raw = raw };
        }

        if (str.Contains(".es", StringComparison.Ordinal))
        {
            return new RuntimeFormIdentifier { Kind = RuntimeFormIdentifierKind.ModName, ModName = str, Raw = raw };
        }

        var hexBody = str.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? str.Substring(2) : str;
        if (hexBody.Length > 0 && hexBody.All(Uri.IsHexDigit) &&
            uint.TryParse(hexBody, NumberStyles.HexNumber, null, out var runtimeId))
        {
            return new RuntimeFormIdentifier
            {
                Kind = RuntimeFormIdentifierKind.RuntimeFormId,
                LocalOrRuntimeId = runtimeId,
                Raw = raw,
            };
        }

        return new RuntimeFormIdentifier { Kind = RuntimeFormIdentifierKind.EditorId, EditorId = str, Raw = raw };
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Matching (mirrors LookupFilters.cpp / LookupNPC.cpp at record level)
    // ─────────────────────────────────────────────────────────────────────

    public sealed record MatchResult(bool Applies, IReadOnlyList<string> Approximations);

    /// <summary>
    /// Evaluates whether <paramref name="entry"/>'s filters pass for the NPC in
    /// <paramref name="facts"/>. <paramref name="resolveFilterForm"/> resolves a
    /// form filter identifier to its typed form (null = not present in the load
    /// order). Unresolvable MATCH/ALL filter forms make the entry unmatchable
    /// (conservative); unresolvable NOT forms are ignored. Sub-100% chance
    /// entries never match (non-deterministic at runtime).
    /// </summary>
    public MatchResult MatchesNpc(
        SpidOutfitEntry entry,
        NpcRuntimeFacts facts,
        Func<RuntimeFormIdentifier, ResolvedFilterForm?> resolveFilterForm)
    {
        var noMatch = new MatchResult(false, Array.Empty<string>());
        var approximations = new List<string>(entry.UnevaluatedFilters);

        if (entry.ChancePercent < 100.0) return noMatch;

        // String filters. MATCH/ALL use exact (case-insensitive) comparison
        // against keywords / name / self+template EditorIDs; ANY uses contains.
        bool HasStringExact(string s) =>
            facts.KeywordEditorIds.Contains(s) ||
            (facts.Name != null && facts.Name.Equals(s, StringComparison.OrdinalIgnoreCase)) ||
            facts.SelfAndTemplateEditorIds.Contains(s);
        bool HasStringContains(string s) =>
            (facts.Name != null && facts.Name.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
            facts.SelfAndTemplateEditorIds.Any(id => id.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
            facts.KeywordEditorIds.Any(k => k.Contains(s, StringComparison.OrdinalIgnoreCase));

        if (entry.StringsAll.Count > 0 && !entry.StringsAll.All(HasStringExact)) return noMatch;
        if (entry.StringsNot.Count > 0 && entry.StringsNot.Any(HasStringExact)) return noMatch;
        if (entry.StringsMatch.Count > 0 && !entry.StringsMatch.Any(HasStringExact)) return noMatch;
        if (entry.StringsAny.Count > 0 && !entry.StringsAny.Any(HasStringContains)) return noMatch;

        // Form filters.
        bool HasForm(ResolvedFilterForm form)
        {
            switch (form.Type)
            {
                case SpidFilterFormType.Keyword: return facts.KeywordFormKeys.Contains(form.Key);
                case SpidFilterFormType.Faction: return facts.FactionFormKeys.Contains(form.Key);
                case SpidFilterFormType.Race: return facts.RaceFormKey != null && facts.RaceFormKey.Value.Equals(form.Key);
                case SpidFilterFormType.Class: return facts.ClassFormKey != null && facts.ClassFormKey.Value.Equals(form.Key);
                case SpidFilterFormType.CombatStyle: return facts.CombatStyleFormKey != null && facts.CombatStyleFormKey.Value.Equals(form.Key);
                case SpidFilterFormType.VoiceType: return facts.VoiceTypeFormKey != null && facts.VoiceTypeFormKey.Value.Equals(form.Key);
                case SpidFilterFormType.Npc: return facts.SelfAndTemplateFormKeys.Contains(form.Key);
                case SpidFilterFormType.Outfit: return facts.DefaultOutfitFormKey != null && facts.DefaultOutfitFormKey.Value.Equals(form.Key);
                case SpidFilterFormType.Spell: return facts.SpellFormKeys.Contains(form.Key);
                case SpidFilterFormType.Perk: return facts.PerkFormKeys.Contains(form.Key);
                case SpidFilterFormType.Armor:
                    return (facts.SkinFormKey != null && facts.SkinFormKey.Value.Equals(form.Key)) ||
                           facts.InventoryItemFormKeys.Contains(form.Key);
                case SpidFilterFormType.OtherItem: return facts.InventoryItemFormKeys.Contains(form.Key);
                case SpidFilterFormType.FormList:
                    return form.ListMembers != null && form.ListMembers.Any(HasForm);
                case SpidFilterFormType.Unsupported:
                    approximations.Add("unsupported filter form type");
                    return false;
                default: return false;
            }
        }

        bool HasFormOrFile(RuntimeFormIdentifier id, out bool resolved)
        {
            resolved = true;
            if (id.Kind == RuntimeFormIdentifierKind.ModName)
            {
                return id.ModName != null && facts.OriginPluginNames.Contains(id.ModName);
            }
            var form = resolveFilterForm(id);
            if (form == null)
            {
                resolved = false;
                return false;
            }
            return HasForm(form);
        }

        foreach (var id in entry.FormsAll)
        {
            if (!HasFormOrFile(id, out _)) return noMatch; // unresolved ALL form ⇒ unmatchable
        }
        if (entry.FormsNot.Count > 0)
        {
            foreach (var id in entry.FormsNot)
            {
                if (HasFormOrFile(id, out _)) return noMatch; // unresolved NOT forms simply don't veto
            }
        }
        if (entry.FormsMatch.Count > 0)
        {
            bool any = false;
            foreach (var id in entry.FormsMatch)
            {
                if (HasFormOrFile(id, out _)) { any = true; break; }
            }
            if (!any) return noMatch;
        }

        // Level filter — record-level value; PC-level-mult NPCs scale with the
        // player, so the preview can't evaluate them meaningfully.
        if (entry.MinLevel != null || entry.MaxLevel != null)
        {
            if (facts.HasPcLevelMult)
            {
                approximations.Add("level filter on a PC-level-mult NPC");
            }
            else
            {
                if (entry.MinLevel != null && facts.Level < entry.MinLevel.Value) return noMatch;
                if (entry.MaxLevel != null && facts.Level > entry.MaxLevel.Value) return noMatch;
            }
        }

        // Traits.
        var t = entry.Traits;
        if (t.Female != null && t.Female.Value != facts.IsFemale) return noMatch;
        if (t.Unique != null && t.Unique.Value != facts.IsUnique) return noMatch;
        if (t.Summonable != null && t.Summonable.Value != facts.IsSummonable) return noMatch;
        if (t.Child != null && t.Child.Value != facts.IsChild) return noMatch;
        if (t.Leveled != null && t.Leveled.Value != facts.IsLeveled) return noMatch;
        if (t.StartsDead != null && t.StartsDead.Value != facts.StartsDead) return noMatch;
        if (t.Teammate != null)
        {
            // Record-level approximation: PotentialFollowerFaction membership
            // (LookupNPC.cpp also ORs the runtime IsPlayerTeammate state).
            approximations.Add("teammate trait (record-level approximation)");
            bool isTeammate = facts.FactionFormKeys.Contains(PotentialFollowerFaction);
            if (t.Teammate.Value != isTeammate) return noMatch;
        }

        return new MatchResult(true, approximations);
    }

    /// <summary>Skyrim.esm PotentialFollowerFaction (0005C84D) — SPID's
    /// record-level teammate signal.</summary>
    public static readonly FormKey PotentialFollowerFaction = FormKey.Factory("05C84D:Skyrim.esm");
}
