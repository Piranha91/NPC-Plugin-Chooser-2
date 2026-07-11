using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;

/// <summary>
/// Parses SkyPatcher npc configs (<c>Data\SKSE\Plugins\SkyPatcher\npc\**</c>)
/// and evaluates which <c>outfitDefault=</c> line, if any, applies to an NPC.
/// Mirrors the behavior of SkyPatcher's own loader (verified against
/// github.com/Zzyxz/SkyPatcher npc.cpp @ main):
///
/// <list type="bullet">
/// <item>Discovery (readConfig): breadth-first directory walk — every file in
/// a folder is processed before any of its subfolders; subfolders are visited
/// in the order the parent enumeration returned them. NTFS returns directory
/// entries in case-insensitive name order, which this parser reproduces with
/// an explicit OrdinalIgnoreCase sort per directory.</item>
/// <item>A file participates when its name CONTAINS ".ini" (case-sensitive
/// std::string::find — "x.ini.bak" is processed too, faithfully).</item>
/// <item>Plugin-gated configs: if the name before the first ".ini" contains
/// ".esp"/".esl"/".esm" (case-sensitive strstr), the whole prefix is treated
/// as a plugin filename and the file is skipped unless that plugin is in the
/// load order.</item>
/// <item>Lines starting with ';' and empty lines are skipped. Each remaining
/// line is one instruction; keys are matched case-insensitively anywhere in
/// the line via <c>key\s*=([^:]+)</c> (first occurrence only), values are
/// comma-split, trimmed, and "none" items dropped.</item>
/// <item>Application order: instructions run in file order as each file is
/// read; a later matching <c>outfitDefault</c> assignment overwrites an
/// earlier one, so the LAST matching line across the whole walk wins. An
/// unresolvable outfit value is skipped, leaving the previous assignment.</item>
/// </list>
/// </summary>
public class SkyPatcherOutfitConfigParser
{
    // ─────────────────────────────────────────────────────────────────────
    //  Discovery
    // ─────────────────────────────────────────────────────────────────────

    public sealed record DiscoveredConfig(string AbsolutePath, string RelativePath);

    /// <summary>
    /// Enumerates config files under <paramref name="npcRootFolder"/> in
    /// SkyPatcher's load order (BFS; per-directory case-insensitive name
    /// sort). <paramref name="isPluginInstalled"/> answers whether a plugin
    /// filename is present in the load order (for plugin-gated config names).
    /// <paramref name="excludeRelativePath"/> lets the caller drop NPC2's own
    /// output ini (it is simulated from live state instead).
    /// </summary>
    public List<DiscoveredConfig> DiscoverConfigFiles(
        string npcRootFolder,
        Func<string, bool> isPluginInstalled,
        Func<string, bool>? excludeRelativePath = null)
    {
        var results = new List<DiscoveredConfig>();
        if (string.IsNullOrWhiteSpace(npcRootFolder) || !Directory.Exists(npcRootFolder)) return results;

        var pending = new Queue<string>();
        pending.Enqueue(npcRootFolder);

        while (pending.Count > 0)
        {
            var currentFolder = pending.Dequeue();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(currentFolder); }
            catch { continue; }

            // NTFS hands readdir entries back in case-insensitive lexical
            // order (B-tree storage); explicit sort keeps the simulation
            // deterministic on any filesystem.
            Array.Sort(entries, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                bool isDir;
                try { isDir = Directory.Exists(entry); }
                catch { continue; }

                if (isDir)
                {
                    pending.Enqueue(entry); // BFS: after ALL files of this folder
                    continue;
                }

                var fileName = Path.GetFileName(entry);
                int iniPos = fileName.IndexOf(".ini", StringComparison.Ordinal);
                if (iniPos < 0) continue;

                // Plugin-gated config name: "<Plugin.esp>.ini" only loads when
                // Plugin.esp is present. The gate substring check is
                // case-sensitive (strstr), the plugin lookup itself is not.
                var modName = fileName.Substring(0, iniPos);
                if (modName.Contains(".esp", StringComparison.Ordinal) ||
                    modName.Contains(".esl", StringComparison.Ordinal) ||
                    modName.Contains(".esm", StringComparison.Ordinal))
                {
                    if (!isPluginInstalled(modName)) continue;
                }

                var relative = Path.GetRelativePath(npcRootFolder, entry);
                if (excludeRelativePath != null && excludeRelativePath(relative)) continue;

                results.Add(new DiscoveredConfig(entry, relative));
            }
        }

        return results;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Line parsing
    // ─────────────────────────────────────────────────────────────────────

    private static readonly RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    private static Regex Key(string key) => new($@"{key}\s*=([^:]+)", Opts);

    private static readonly Regex ReOutfitDefault = Key("outfitDefault");
    private static readonly Regex ReNpcs = Key("filterByNpcs");
    private static readonly Regex ReNpcsExcluded = Key("filterByNpcsExcluded");
    private static readonly Regex ReKeywords = Key("filterByKeywords");
    private static readonly Regex ReKeywordsOr = Key("filterByKeywordsOr");
    private static readonly Regex ReKeywordsExcluded = Key("filterByKeywordsExcluded");
    private static readonly Regex ReRaces = Key("filterByRaces");
    private static readonly Regex ReDefaultOutfits = Key("filterByDefaultOutfits");
    private static readonly Regex ReClass = Key("filterByClass");
    private static readonly Regex ReClassExclude = Key("filterByClassExclude");
    private static readonly Regex ReCombatStyle = Key("filterByCombatStyle");
    private static readonly Regex ReFactions = Key("filterByFactions");
    private static readonly Regex ReFactionsOr = Key("filterByFactionsOr");
    private static readonly Regex ReFactionsExcluded = Key("filterByFactionsExcluded");
    private static readonly Regex ReEdidContains = Key("filterByEditorIdContains");
    private static readonly Regex ReEdidContainsOr = Key("filterByEditorIdContainsOr");
    private static readonly Regex ReEdidContainsExcluded = Key("filterByEditorIdContainsExcluded");
    private static readonly Regex ReModNames = Key("filterByModNames");
    private static readonly Regex ReGender = Key("filterByGender");
    private static readonly Regex ReRestrictGender = Key("restrictToGender");
    private static readonly Regex ReRestrictRaces = Key("restrictToRaces");
    private static readonly Regex ReRestrictVoice = Key("restrictToVoiceType");

    /// <summary>Filter keys this simulator treats as "pass" (too runtime- or
    /// niche-specific to evaluate from the record). Their presence is recorded
    /// on the instruction so provenance can flag the approximation.</summary>
    private static readonly (string Name, Regex Re)[] UnevaluatedKeys =
    {
        ("restrictToSkill", Key("restrictToSkill")),
        ("restrictToFlags", Key("restrictToFlags")),
        ("restrictToTemplateFlags", Key("restrictToTemplateFlags")),
        ("restrictToKeywords", Key("restrictToKeywords")),
        ("restrictToMaleModelContains", Key("restrictToMaleModelContains")),
        ("restrictToCombatStyle", Key("restrictToCombatStyle")),
        ("filterByPCLevelMult", Key("filterByPCLevelMult")),
        ("filterByAutoCalc", Key("filterByAutoCalc")),
        ("filterByEssential", Key("filterByEssential")),
        ("filterByProtected", Key("filterByProtected")),
    };

    /// <summary>Parses one config file's lines, returning only instructions
    /// that carry an <c>outfitDefault=</c> directive.</summary>
    public List<SkyPatcherOutfitInstruction> ParseFile(IReadOnlyList<string> lines, string relativePath)
    {
        var result = new List<SkyPatcherOutfitInstruction>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) continue;
            if (line[0] == ';') continue;

            var outfit = ExtractSingle(ReOutfitDefault, line);
            if (string.IsNullOrEmpty(outfit)) continue;

            var unevaluated = new List<string>();
            foreach (var (name, re) in UnevaluatedKeys)
            {
                if (re.IsMatch(line)) unevaluated.Add(name);
            }

            result.Add(new SkyPatcherOutfitInstruction
            {
                SourceFile = relativePath,
                LineNumber = i + 1,
                OutfitIdentifier = outfit,
                FilterByNpcs = ExtractList(ReNpcs, line),
                FilterByNpcsExcluded = ExtractList(ReNpcsExcluded, line),
                FilterByKeywords = ExtractList(ReKeywords, line),
                FilterByKeywordsOr = ExtractList(ReKeywordsOr, line),
                FilterByKeywordsExcluded = ExtractList(ReKeywordsExcluded, line),
                FilterByRaces = ExtractList(ReRaces, line),
                FilterByDefaultOutfits = ExtractList(ReDefaultOutfits, line),
                FilterByClass = ExtractList(ReClass, line),
                FilterByClassExclude = ExtractList(ReClassExclude, line),
                FilterByCombatStyle = ExtractList(ReCombatStyle, line),
                FilterByFactions = ExtractList(ReFactions, line),
                FilterByFactionsOr = ExtractList(ReFactionsOr, line),
                FilterByFactionsExcluded = ExtractList(ReFactionsExcluded, line),
                FilterByEditorIdContains = ExtractList(ReEdidContains, line),
                FilterByEditorIdContainsOr = ExtractList(ReEdidContainsOr, line),
                FilterByEditorIdContainsExcluded = ExtractList(ReEdidContainsExcluded, line),
                FilterByModNames = ExtractList(ReModNames, line),
                FilterByGender = ExtractSingle(ReGender, line),
                RestrictToGender = ExtractSingle(ReRestrictGender, line),
                RestrictToRaces = ExtractList(ReRestrictRaces, line),
                RestrictToVoiceType = ExtractList(ReRestrictVoice, line),
                UnevaluatedFilterKeys = unevaluated,
            });
        }
        return result;
    }

    private static string? ExtractSingle(Regex re, string line)
    {
        var m = re.Match(line);
        if (!m.Success) return null;
        var v = m.Groups[1].Value.Trim();
        if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        return v;
    }

    private static List<string> ExtractList(Regex re, string line)
    {
        var result = new List<string>();
        var m = re.Match(line);
        if (!m.Success) return result;
        foreach (var raw in m.Groups[1].Value.Split(','))
        {
            var v = raw.Trim();
            if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(v);
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Identifier parsing ("Plugin.esp|1B1D3" or EditorID)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Parses a SkyPatcher form identifier. "Plugin|hexID" (local ID;
    /// SkyPatcher masks with 0xFFFFFF, or 0xFFF for light plugins — the
    /// resolver tries both) or a bare EditorID.</summary>
    public static RuntimeFormIdentifier ParseIdentifier(string raw)
    {
        int sep = raw.IndexOf('|');
        if (sep >= 0)
        {
            var modName = raw.Substring(0, sep).Trim();
            var idPart = raw.Substring(sep + 1).Trim();
            idPart = idPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? idPart.Substring(2) : idPart;
            if (uint.TryParse(idPart, System.Globalization.NumberStyles.HexNumber, null, out uint id))
            {
                return new RuntimeFormIdentifier
                {
                    Kind = RuntimeFormIdentifierKind.ModAndLocalId,
                    ModName = modName,
                    LocalOrRuntimeId = id & 0xFFFFFF,
                    Raw = raw,
                };
            }
            // Malformed hex — unresolvable; keep raw for diagnostics.
            return new RuntimeFormIdentifier { Kind = RuntimeFormIdentifierKind.EditorId, EditorId = raw, Raw = raw };
        }
        return new RuntimeFormIdentifier { Kind = RuntimeFormIdentifierKind.EditorId, EditorId = raw.Trim(), Raw = raw };
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Matching (mirrors process_patch_instructions + findObject)
    // ─────────────────────────────────────────────────────────────────────

    public sealed record MatchResult(bool Applies, bool ViaDirectNpcList, IReadOnlyList<string> Approximations);

    private static readonly MatchResult NoMatch = new(false, false, Array.Empty<string>());

    /// <summary>
    /// Decides whether <paramref name="instruction"/> applies its outfit to the
    /// NPC described by <paramref name="facts"/>.
    /// <paramref name="resolveIdentifier"/> resolves a raw identifier string to
    /// the FormKey it denotes in the current load order (null when absent).
    /// Faithful quirks preserved from the SkyPatcher source:
    /// <list type="bullet">
    /// <item>NPCs listed in <c>filterByNpcs</c> are patched DIRECTLY — gender,
    /// exclusion, and restrictTo vetoes do NOT apply to them.</item>
    /// <item>The broad-filter sweep only runs when at least one of keywords /
    /// class / combat style / factions / default-outfit / editorIdContains
    /// filters is present OR <c>filterByNpcs</c> is empty; a line with only
    /// <c>filterByNpcs</c> + <c>filterByRaces</c> never sweeps, so its race
    /// filter is dead — mirrored here.</item>
    /// <item>A line with no selection filters at all patches every NPC.</item>
    /// </list>
    /// </summary>
    public MatchResult MatchesNpc(
        SkyPatcherOutfitInstruction instruction,
        NpcRuntimeFacts facts,
        Func<string, FormKey?> resolveIdentifier)
    {
        // Pass 1 — direct filterByNpcs list: unconditional patch, no vetoes.
        foreach (var npcIdentifier in instruction.FilterByNpcs)
        {
            var fk = resolveIdentifier(npcIdentifier);
            if (fk != null && fk.Value.Equals(facts.NpcFormKey))
            {
                return new MatchResult(true, true, instruction.UnevaluatedFilterKeys);
            }
        }

        // Sweep gate (process_patch_instructions:387): with a filterByNpcs
        // list present, the whole-NPC-array sweep only runs when one of these
        // broad filters is also present. filterByRaces / filterByModNames are
        // NOT in the gate list (faithful quirk).
        bool hasBroadGateFilter =
            instruction.FilterByKeywords.Count > 0 || instruction.FilterByKeywordsOr.Count > 0 ||
            instruction.FilterByClass.Count > 0 || instruction.FilterByCombatStyle.Count > 0 ||
            instruction.FilterByFactions.Count > 0 || instruction.FilterByFactionsOr.Count > 0 ||
            instruction.FilterByDefaultOutfits.Count > 0 ||
            instruction.FilterByEditorIdContains.Count > 0 || instruction.FilterByEditorIdContainsOr.Count > 0;
        if (instruction.FilterByNpcs.Count > 0 && !hasBroadGateFilter) return NoMatch;

        // Sweep-loop mod-name pre-filter: matches the record's origin plugin.
        if (instruction.FilterByModNames.Count > 0 &&
            !instruction.FilterByModNames.Any(m => facts.OriginPluginNames.Contains(m)))
        {
            return NoMatch;
        }

        bool found = FindObject(instruction, facts, resolveIdentifier);
        return found
            ? new MatchResult(true, false, instruction.UnevaluatedFilterKeys)
            : NoMatch;
    }

    /// <summary>Mirrors npc.cpp findObject: categories are OR'd in source
    /// order (each only consulted while not yet found), then gender and the
    /// exclusion / restrictTo filters veto.</summary>
    private bool FindObject(
        SkyPatcherOutfitInstruction line,
        NpcRuntimeFacts facts,
        Func<string, FormKey?> resolve)
    {
        bool found = false;

        // keywords (AND) + keywordsOr (OR) combine: both sides must hold, and
        // at least one of the two keys must be present.
        bool keywordAnd = line.FilterByKeywords.Count == 0 ||
                          line.FilterByKeywords.All(k => HasKeyword(facts, k, resolve));
        bool keywordOr = line.FilterByKeywordsOr.Count == 0 ||
                         line.FilterByKeywordsOr.Any(k => HasKeyword(facts, k, resolve));
        if ((line.FilterByKeywords.Count > 0 || line.FilterByKeywordsOr.Count > 0) && keywordAnd && keywordOr)
        {
            found = true;
        }

        if (!found && line.FilterByRaces.Count > 0 && facts.RaceFormKey != null)
        {
            found = line.FilterByRaces.Any(r => resolve(r) is { } fk && fk.Equals(facts.RaceFormKey.Value));
        }

        if (!found && line.FilterByDefaultOutfits.Count > 0 && facts.DefaultOutfitFormKey != null)
        {
            found = line.FilterByDefaultOutfits.Any(o => resolve(o) is { } fk && fk.Equals(facts.DefaultOutfitFormKey.Value));
        }

        if (!found && line.FilterByClass.Count > 0 && facts.ClassFormKey != null)
        {
            found = line.FilterByClass.Any(c => resolve(c) is { } fk && fk.Equals(facts.ClassFormKey.Value));
        }

        if (!found && line.FilterByCombatStyle.Count > 0 && facts.CombatStyleFormKey != null)
        {
            found = line.FilterByCombatStyle.Any(c => resolve(c) is { } fk && fk.Equals(facts.CombatStyleFormKey.Value));
        }

        // factions (AND) + factionsOr (OR) combine like keywords.
        if (!found)
        {
            bool factionAnd = line.FilterByFactions.Count == 0 ||
                              line.FilterByFactions.All(f => resolve(f) is { } fk && facts.FactionFormKeys.Contains(fk));
            bool factionOr = line.FilterByFactionsOr.Count == 0 ||
                             line.FilterByFactionsOr.Any(f => resolve(f) is { } fk && facts.FactionFormKeys.Contains(fk));
            if ((line.FilterByFactions.Count > 0 || line.FilterByFactionsOr.Count > 0) && factionAnd && factionOr)
            {
                found = true;
            }
        }

        // editorIdContains (AND) + editorIdContainsOr (OR) combine likewise.
        if (!found)
        {
            string edid = facts.EditorId ?? string.Empty;
            bool contains = line.FilterByEditorIdContains.Count == 0 ||
                            line.FilterByEditorIdContains.All(s => edid.Contains(s, StringComparison.OrdinalIgnoreCase));
            bool containsOr = line.FilterByEditorIdContainsOr.Count == 0 ||
                              line.FilterByEditorIdContainsOr.Any(s => edid.Contains(s, StringComparison.OrdinalIgnoreCase));
            if ((line.FilterByEditorIdContains.Count > 0 || line.FilterByEditorIdContainsOr.Count > 0) && contains && containsOr)
            {
                found = true;
            }
        }

        // "Patch everything" fallback: no selection filters at all.
        if (!found &&
            line.FilterByNpcs.Count == 0 && line.FilterByRaces.Count == 0 &&
            line.FilterByKeywords.Count == 0 && line.FilterByKeywordsOr.Count == 0 &&
            line.FilterByClass.Count == 0 && line.FilterByCombatStyle.Count == 0 &&
            line.FilterByFactions.Count == 0 && line.FilterByFactionsOr.Count == 0 &&
            line.FilterByDefaultOutfits.Count == 0 &&
            line.FilterByEditorIdContains.Count == 0 && line.FilterByEditorIdContainsOr.Count == 0)
        {
            found = true;
        }

        // Gender veto (filterByGender), then exclusions, then restrictTo*.
        if (found && line.FilterByGender != null)
        {
            found = MatchesGender(line.FilterByGender, facts.IsFemale);
        }

        if (found && line.FilterByKeywordsExcluded.Count > 0 &&
            line.FilterByKeywordsExcluded.Any(k => HasKeyword(facts, k, resolve)))
        {
            found = false;
        }

        if (found && line.FilterByFactionsExcluded.Count > 0 &&
            line.FilterByFactionsExcluded.Any(f => resolve(f) is { } fk && facts.FactionFormKeys.Contains(fk)))
        {
            found = false;
        }

        if (found && line.FilterByEditorIdContainsExcluded.Count > 0)
        {
            string edid = facts.EditorId ?? string.Empty;
            if (line.FilterByEditorIdContainsExcluded.Any(s => edid.Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                found = false;
            }
        }

        if (found && line.FilterByClassExclude.Count > 0 && facts.ClassFormKey != null &&
            line.FilterByClassExclude.Any(c => resolve(c) is { } fk && fk.Equals(facts.ClassFormKey.Value)))
        {
            found = false;
        }

        if (found && line.FilterByNpcsExcluded.Count > 0 &&
            line.FilterByNpcsExcluded.Any(n => resolve(n) is { } fk && fk.Equals(facts.NpcFormKey)))
        {
            found = false;
        }

        if (found && line.RestrictToGender != null)
        {
            found = MatchesGender(line.RestrictToGender, facts.IsFemale);
        }

        if (found && line.RestrictToRaces.Count > 0 && facts.RaceFormKey != null &&
            !line.RestrictToRaces.Any(r => resolve(r) is { } fk && fk.Equals(facts.RaceFormKey.Value)))
        {
            found = false;
        }

        if (found && line.RestrictToVoiceType.Count > 0 && facts.VoiceTypeFormKey != null &&
            !line.RestrictToVoiceType.Any(v => resolve(v) is { } fk && fk.Equals(facts.VoiceTypeFormKey.Value)))
        {
            found = false;
        }

        return found;
    }

    private static bool MatchesGender(string genderValue, bool npcIsFemale)
    {
        if (genderValue.Equals("female", StringComparison.OrdinalIgnoreCase)) return npcIsFemale;
        if (genderValue.Equals("male", StringComparison.OrdinalIgnoreCase)) return !npcIsFemale;
        return true; // unknown value — SkyPatcher leaves found untouched
    }

    private static bool HasKeyword(NpcRuntimeFacts facts, string identifier, Func<string, FormKey?> resolve)
    {
        // Keyword filters accept Plugin|ID or EditorID. EditorID compare is
        // done against the keyword EDID set directly (cheap); form references
        // resolve to a FormKey and compare against the keyword key set.
        if (!identifier.Contains('|'))
        {
            return facts.KeywordEditorIds.Contains(identifier);
        }
        return resolve(identifier) is { } fk && facts.KeywordFormKeys.Contains(fk);
    }
}
