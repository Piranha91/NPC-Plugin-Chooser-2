using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.Benchmark;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Lab;

/// <summary>
/// Counts, on a real load order, the NPCs exposed to the "inherited face under a helmet" defect
/// (bench finding N11): an NPC that takes its face from another NPC through its Traits template,
/// where that other NPC has a wig the selected appearance mod's conversion turns into a hair head
/// part, while the inheriting NPC itself wears headgear on a hair slot. In the game the inheritor
/// then shows the template's hair through its headgear (seen on Legate Rikke's twin,
/// <c>0D0573:Skyrim.esm</c>, under the SkyPatcher and Recast back ends).
///
/// <para>Read-only and record-level: it resolves the load order of a bench arm, reads the staged
/// settings' persisted wig analysis, and writes one text file. It builds no output and starts
/// nothing. The three conditions it counts are written out in the result file itself.</para>
///
/// <para>Opt-in, because it needs a mod manager instance: does nothing unless
/// <c>NPC2_N11_REACH_SPEC</c> names a bench arm spec
/// (<c>{ "instance", "profile", "settingsPath" }</c>, the format of
/// <see cref="BenchOutputBuilder"/>). <c>NPC2_N11_REACH_OUT</c> is the text file to write (test
/// output only when unset). <c>NPC2_N11_REACH_MOD</c> picks the appearance mod entries by a part
/// of their display name; the default is "High Poly".</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class InheritedFaceHelmetReachDiagnostic
{
    private sealed class Spec
    {
        public string Instance { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
        public string SettingsPath { get; set; } = string.Empty;
    }

    /// <summary>One Armor on a hair slot that an outfit can put on its wearer.</summary>
    private sealed record Headgear(FormKey Key, string? EditorId, string? Name, IReadOnlyList<int> Slots, bool Direct)
    {
        public string Label =>
            !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(EditorId) ? $"{Name} ({EditorId})"
            : !string.IsNullOrWhiteSpace(Name) ? Name!
            : !string.IsNullOrWhiteSpace(EditorId) ? EditorId!
            : Key.ToString();
    }

    private enum InventoryState
    {
        /// <summary>No Inventory template flag (or the flag with no template): the record's own outfit.</summary>
        Own,
        /// <summary>The outfit of the NPC the Inventory template chain ends at.</summary>
        FromTemplate,
        /// <summary>The Inventory template chain reaches a leveled NPC list: no one outfit to read.</summary>
        LeveledList,
        /// <summary>The Inventory template chain has a link that does not resolve, or a cycle.</summary>
        Unfollowable,
    }

    /// <summary>One NPC whose Traits template chain resolved to an NPC (condition 1).</summary>
    private sealed class Inheritor
    {
        public FormKey Key { get; init; }
        public string? EditorId { get; init; }
        public bool Unique { get; init; }
        public FormKey Terminus { get; init; }
        public INpcGetter Record { get; init; } = null!;

        // Filled on demand (condition 3).
        public bool OutfitEvaluated { get; set; }
        public InventoryState Inventory { get; set; }
        public FormKey OutfitOwner { get; set; }
        public FormKey? Outfit { get; set; }
        public bool OutfitResolved { get; set; }
        public IReadOnlyList<Headgear> Headgear { get; set; } = Array.Empty<Headgear>();
    }

    private static readonly FormKey RikkeTwin = FormKey.Factory("0D0573:Skyrim.esm");

    private readonly ITestOutputHelper _output;
    public InheritedFaceHelmetReachDiagnostic(ITestOutputHelper output) => _output = output;

    [Fact]
    public void CountNpcsInheritingAConvertedWigUnderHeadgear()
    {
        var specPath = Environment.GetEnvironmentVariable("NPC2_N11_REACH_SPEC");
        if (string.IsNullOrWhiteSpace(specPath))
        {
            _output.WriteLine("SKIPPED: set NPC2_N11_REACH_SPEC=<arm spec json> to count the NPCs exposed to N11.");
            return;
        }

        var outPath = Environment.GetEnvironmentVariable("NPC2_N11_REACH_OUT");
        var wanted = Environment.GetEnvironmentVariable("NPC2_N11_REACH_MOD");
        if (string.IsNullOrWhiteSpace(wanted)) wanted = "High Poly";

        var total = Stopwatch.StartNew();

        var spec = JsonSerializer.Deserialize<Spec>(File.ReadAllText(specPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var settings = JSONhandler<Settings>.LoadJSONFile(spec.SettingsPath, out var loaded, out var loadError);
        loaded.Should().BeTrue(loadError);

        var environmentTimer = Stopwatch.StartNew();
        Mo2ProfileEnvironment.TryBuild(spec.Instance, spec.Profile, settings!.OutputPluginName, out var env, out var failure)
            .Should().BeTrue(failure);
        environmentTimer.Stop();

        var linkCache = env!.Provider.LinkCache!;
        var loadOrder = env.Provider.LoadOrder!;

        var mods = settings.ModSettings
            .Where(m => m.DisplayName.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
        mods.Should().NotBeEmpty($"the staged settings must hold a mod whose display name contains '{wanted}'");
        var primary = mods.FirstOrDefault(m => m.ModWigHandlingMode == WigHandlingMode.ConvertToHeadParts) ?? mods[0];

        INpcGetter? ResolveNpc(FormKey key) => linkCache.TryResolve<INpcGetter>(key, out var n) ? n : null;
        bool IsLeveledNpc(FormKey key) => linkCache.TryResolve<ILeveledNpcGetter>(key, out _);

        // ---- Condition 1: the Traits template chain, over the load order's winning records ----
        var countTimer = Stopwatch.StartNew();
        int allNpcs = 0, templatedWithTraits = 0, leveledStop = 0, unfollowable = 0;
        var inheritors = new List<Inheritor>();
        var leveledStopExamples = new List<string>();
        foreach (var npc in loadOrder.PriorityOrder.Npc().WinningOverrides())
        {
            allNpcs++;
            if (!Auxilliary.IsValidTemplatedNpc(npc)) continue;
            templatedWithTraits++;

            var status = Auxilliary.TryResolveAppearanceTerminus(npc, ResolveNpc, out var terminus, IsLeveledNpc);
            switch (status)
            {
                case FaceGenChainStatus.Resolved:
                    inheritors.Add(new Inheritor
                    {
                        Key = npc.FormKey,
                        EditorId = npc.EditorID,
                        Unique = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique),
                        Terminus = terminus,
                        Record = npc,
                    });
                    break;
                case FaceGenChainStatus.LeveledTerminus:
                    leveledStop++;
                    if (leveledStopExamples.Count < 5) leveledStopExamples.Add($"{npc.EditorID} [{npc.FormKey}]");
                    break;
                default:
                    unfollowable++;
                    break;
            }
        }

        // ---- Condition 3: hair-slot headgear in the outfit the NPC wears ----
        var headgearByOutfit = new Dictionary<FormKey, (bool Resolved, IReadOnlyList<Headgear> Items)>();

        (bool Resolved, IReadOnlyList<Headgear> Items) ScanOutfit(FormKey outfitKey)
        {
            if (headgearByOutfit.TryGetValue(outfitKey, out var cached)) return cached;

            var found = new List<Headgear>();
            bool resolved = linkCache.TryResolve<IOutfitGetter>(outfitKey, out var outfit) && outfit != null;
            if (resolved && outfit!.Items != null)
            {
                var seenArmors = new HashSet<FormKey>();
                var enteredLists = new HashSet<FormKey>();

                void Visit(FormKey itemKey, int depth)
                {
                    if (itemKey.IsNull) return;
                    if (linkCache.TryResolve<IArmorGetter>(itemKey, out var armor))
                    {
                        if (!seenArmors.Add(armor.FormKey)) return;
                        var slots = armor.BodyTemplate?.FirstPersonFlags ?? 0;
                        if ((slots & (BipedObjectFlag.Hair | BipedObjectFlag.LongHair)) == 0) return;
                        found.Add(new Headgear(armor.FormKey, armor.EditorID, armor.Name?.String,
                            WigHeadgearConflictDetector.SlotNumbers(slots), Direct: depth == 0));
                        return;
                    }

                    if (!linkCache.TryResolve<ILeveledItemGetter>(itemKey, out var list) || list.Entries == null) return;
                    // A list entered once is not entered again: the cycle guard.
                    if (!enteredLists.Add(list.FormKey) || depth >= 32) return;
                    foreach (var entry in list.Entries)
                    {
                        var reference = entry?.Data?.Reference;
                        if (reference == null || reference.IsNull) continue;
                        Visit(reference.FormKey, depth + 1);
                    }
                }

                foreach (var item in outfit.Items)
                {
                    if (item == null || item.IsNull) continue;
                    Visit(item.FormKey, 0);
                }
            }

            var result = (resolved, (IReadOnlyList<Headgear>)found);
            headgearByOutfit[outfitKey] = result;
            return result;
        }

        void EvaluateOutfit(Inheritor row)
        {
            if (row.OutfitEvaluated) return;
            row.OutfitEvaluated = true;

            // The record whose DefaultOutfit the engine reads: follow Template while the Inventory
            // template flag is set (independent of the Traits chain, though both use the one link).
            var current = row.Record;
            var visited = new HashSet<FormKey> { current.FormKey };
            row.Inventory = InventoryState.Own;
            for (int guard = 0; guard < 50; guard++)
            {
                if (!current.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Inventory)) break;
                if (current.Template is not { IsNull: false } templateLink) break;
                if (!visited.Add(templateLink.FormKey)) { row.Inventory = InventoryState.Unfollowable; return; }
                if (IsLeveledNpc(templateLink.FormKey)) { row.Inventory = InventoryState.LeveledList; return; }
                var next = ResolveNpc(templateLink.FormKey);
                if (next == null) { row.Inventory = InventoryState.Unfollowable; return; }
                row.Inventory = InventoryState.FromTemplate;
                current = next;
            }

            row.OutfitOwner = current.FormKey;
            if (current.DefaultOutfit.IsNull) return;
            row.Outfit = current.DefaultOutfit.FormKey;
            var scan = ScanOutfit(current.DefaultOutfit.FormKey);
            row.OutfitResolved = scan.Resolved;
            row.Headgear = scan.Items;
        }

        // ---- Condition 2, per matching mod entry (the bench stages one entry per wig mode) ----
        bool TerminusInList(ModSetting mod, Inheritor row) => mod.NpcFormKeys.Contains(row.Terminus);
        bool TerminusHasWig(ModSetting mod, Inheritor row) =>
            TerminusInList(mod, row) && settings.GetEffectiveNpcWigSources(mod, row.Terminus).Count > 0;

        var perMod = new List<(ModSetting Mod, int InList, int WithWig, int Exposed)>();
        foreach (var mod in mods)
        {
            int inList = 0, withWig = 0, exposedCount = 0;
            foreach (var row in inheritors)
            {
                if (!TerminusInList(mod, row)) continue;
                inList++;
                if (!TerminusHasWig(mod, row)) continue;
                withWig++;
                EvaluateOutfit(row);
                if (row.Headgear.Count > 0) exposedCount++;
            }
            perMod.Add((mod, inList, withWig, exposedCount));
        }

        var c1InList = inheritors.Where(r => TerminusInList(primary, r)).ToList();
        var c12 = c1InList.Where(r => TerminusHasWig(primary, r)).ToList();
        foreach (var row in c12) EvaluateOutfit(row);
        var exposed = c12.Where(r => r.Headgear.Count > 0).ToList();
        countTimer.Stop();

        string EditorIdOf(FormKey key) => ResolveNpc(key)?.EditorID ?? "(no EditorID)";

        string WigSourcesOf(FormKey terminus)
        {
            var effective = settings.GetEffectiveNpcWigSources(primary, terminus);
            int skin = effective.Count(s => s.Kind == NpcWigSourceKind.WornArmor);
            int outfitWigs = effective.Count(s => s.Kind == NpcWigSourceKind.Outfit);
            return $"wig sources: {skin} on the skin, {outfitWigs} in the outfit";
        }

        var hpNames = mods.Select(m => m.DisplayName).ToHashSet(StringComparer.Ordinal);
        string SelectionOf(FormKey key) =>
            settings.SelectedAppearanceMods.TryGetValue(key, out var sel) ? sel.ModName : "(none)";

        // ---- The report ----
        var report = new StringBuilder();
        void Line(string text = "") => report.AppendLine(text);

        Line("N11 reach: NPCs that inherit their face from an NPC with a convertible wig, and wear hair-slot headgear");
        Line($"written {DateTime.Now:yyyy-MM-dd HH:mm:ss} by InheritedFaceHelmetReachDiagnostic (read-only, record level)");
        Line();
        Line("INPUT");
        Line($"  spec: {specPath}");
        Line($"  instance: {spec.Instance}; profile: {spec.Profile}");
        Line($"  settings: {spec.SettingsPath}");
        Line($"  staged run settings: backend {settings.OutputBackend}" +
             $"{(settings.OutputBackend == OutputBackend.Recast ? "/" + settings.RecastMode : string.Empty)}, " +
             $"patching mode {settings.PatchingMode}, template handling {settings.TemplateHandlingMode}, " +
             $"{settings.SelectedAppearanceMods.Count} selections");
        Line($"  load order: {env.ResolvedPlugins.Count} plugins resolved from the profile, " +
             $"{env.UnresolvedPlugins.Count} active plugins not found" +
             (env.UnresolvedPlugins.Count > 0 ? " (" + string.Join(", ", env.UnresolvedPlugins) + ")" : string.Empty) +
             $"; {env.Provider.LoadOrderModKeys.Count()} in the environment after the output plugin '{settings.OutputPluginName}' and its dependents were removed");
        Line($"  mod entries whose name contains '{wanted}': {mods.Count}");
        foreach (var mod in mods)
        {
            Line($"    '{mod.DisplayName}': {mod.NpcFormKeys.Count} NPCs in its list, {mod.NpcWigSources.Count} NPCs with wig-source entries, " +
                 $"{mod.DetectedWigArmatures.Count} detected wig armatures, {mod.DetectedWigArmors.Count} detected wig armors, " +
                 $"wig mode {mod.ModWigHandlingMode?.ToString() ?? "(default)"} (effective {settings.GetEffectiveWigMode(mod)})");
        }
        Line($"  counted against: '{primary.DisplayName}'");
        Line();
        Line("WHAT WAS COUNTED");
        Line("  (1) The NPC's winning record in the load order has a Template and the Traits template flag, and the chain");
        Line("      (each step: winning record, Template set, Traits flag set) ends at an NPC record T that is not itself");
        Line("      such a record. Walked by Auxilliary.TryResolveAppearanceTerminus with the link cache. A step that is a");
        Line("      leveled NPC list stops the walk; those NPCs are counted apart and are in none of the numbers below.");
        Line("  (2) T is in the mod entry's NPC list (ModSetting.NpcFormKeys) AND Settings.GetEffectiveNpcWigSources(mod, T)");
        Line("      is not empty: the mod's persisted per-NPC wig map (NpcWigSources), each entry kept only when it is an");
        Line("      effective wig (skin entries: DetectedWigArmatures and the manual lists; outfit entries: DetectedWigArmors).");
        Line("      This is the mod's own analysis as staged. It does not run the converter, so the converter's per-NPC");
        Line("      declines (see NOT COUNTED) are not applied.");
        Line("  (3) The outfit the NPC wears holds an Armor whose BodyTemplate.FirstPersonFlags includes Hair (slot 31) or");
        Line("      LongHair (slot 41), directly or through leveled item lists followed recursively (each list entered once).");
        Line("      The outfit is the DefaultOutfit of the NPC's winning record, or, while the Inventory template flag is set,");
        Line("      of the record its Template chain leads to (winning records). EVERY entry of a leveled list is followed:");
        Line("      an Armor that the list only sometimes gives still counts.");
        Line();
        Line("COUNTS (whole load order, winning records)");
        Line($"  NPC records: {allNpcs}");
        Line($"  with a Template and the Traits template flag: {templatedWithTraits}");
        Line($"    chain ends at an NPC T                                   (1): {inheritors.Count}");
        Line($"    chain goes through a leveled list (counted apart)           : {leveledStop}" +
             (leveledStopExamples.Count > 0 ? "   e.g. " + string.Join(", ", leveledStopExamples) : string.Empty));
        Line($"    chain not followable (dangling link, cycle)                 : {unfollowable}");
        Line($"  (1) and T is in the mod's NPC list (wig or not)               : {c1InList.Count}");
        Line($"  (1)+(2): T is in the list and has an effective wig            : {c12.Count}");
        Line($"  (1)+(2)+(3) EXPOSED                                           : {exposed.Count}");
        Line();
        Line("  of the (1)+(2) NPCs, condition (3) in detail:");
        Line($"    hair-slot headgear found (EXPOSED)                          : {exposed.Count}");
        Line($"      at least one such Armor directly in the outfit            : {exposed.Count(r => r.Headgear.Any(h => h.Direct))}");
        Line($"      only through leveled item lists                           : {exposed.Count(r => r.Headgear.All(h => !h.Direct))}");
        Line($"      outfit is the NPC's own record's                          : {exposed.Count(r => r.Inventory == InventoryState.Own)}");
        Line($"      outfit comes through the Inventory template flag          : {exposed.Count(r => r.Inventory == InventoryState.FromTemplate)}");
        Line($"        of those, the outfit is T's own (dressed like T)        : {exposed.Count(r => r.Inventory == InventoryState.FromTemplate && r.OutfitOwner == r.Terminus)}");
        Line($"    no hair-slot headgear in a resolved outfit                  : {c12.Count(r => r.Outfit != null && r.OutfitResolved && r.Headgear.Count == 0)}");
        Line($"    no outfit named                                             : {c12.Count(r => r.Inventory is InventoryState.Own or InventoryState.FromTemplate && r.Outfit == null)}");
        Line($"    outfit named but not in the load order                      : {c12.Count(r => r.Outfit != null && !r.OutfitResolved)}");
        Line($"    not evaluable: inventory comes from a leveled NPC list      : {c12.Count(r => r.Inventory == InventoryState.LeveledList)}");
        Line($"    not evaluable: Inventory template chain not followable      : {c12.Count(r => r.Inventory == InventoryState.Unfollowable)}");
        Line();
        Line("  of the EXPOSED NPCs:");
        Line($"    themselves in the mod's NPC list (selection would be 'rejected, inherits') : {exposed.Count(r => primary.NpcFormKeys.Contains(r.Key))}");
        Line($"    not in the mod's NPC list (never selectable from it, still inheriting)     : {exposed.Count(r => !primary.NpcFormKeys.Contains(r.Key))}");
        Line($"    Unique flag set / not set                                                  : {exposed.Count(r => r.Unique)} / {exposed.Count(r => !r.Unique)}");
        Line($"    distinct templates T                                                       : {exposed.Select(r => r.Terminus).Distinct().Count()}");
        Line($"    T has exactly one effective wig source / more than one                     : " +
             $"{exposed.Count(r => settings.GetEffectiveNpcWigSources(primary, r.Terminus).Count == 1)} / " +
             $"{exposed.Count(r => settings.GetEffectiveNpcWigSources(primary, r.Terminus).Count > 1)}");
        Line($"    T is selected on THIS arm with one of the matching mod entries             : {exposed.Count(r => hpNames.Contains(SelectionOf(r.Terminus)))}");
        Line($"    the NPC itself is selected on THIS arm (any mod)                           : {exposed.Count(r => settings.SelectedAppearanceMods.ContainsKey(r.Key))}");
        Line();

        Line("THE SAME THREE COUNTS PER MATCHING MOD ENTRY");
        foreach (var (mod, inList, withWig, exposedCount) in perMod)
        {
            Line($"  '{mod.DisplayName}': (1) with T in list {inList}; (1)+(2) {withWig}; EXPOSED {exposedCount}");
        }
        Line();

        var byTemplate = exposed.GroupBy(r => r.Terminus)
            .Select(g => (Terminus: g.Key, EditorId: EditorIdOf(g.Key), Rows: g.OrderBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(g => g.Rows.Count).ThenBy(g => g.EditorId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Line($"EXPOSED BY TEMPLATE T: the 15 largest of {byTemplate.Count}");
        foreach (var group in byTemplate.Take(15))
        {
            Line($"  {group.Rows.Count,5}  {group.EditorId} [{group.Terminus}]  ({WigSourcesOf(group.Terminus)}; selected on this arm: {SelectionOf(group.Terminus)})");
        }
        Line();

        // Examples: one per template in size order, then a second round, until 20.
        var examples = new List<Inheritor>();
        for (int round = 0; examples.Count < 20 && round < 20; round++)
        {
            bool any = false;
            foreach (var group in byTemplate)
            {
                if (group.Rows.Count <= round) continue;
                any = true;
                examples.Add(group.Rows[round]);
                if (examples.Count >= 20) break;
            }
            if (!any) break;
        }

        string Describe(Inheritor row)
        {
            var first = row.Headgear.FirstOrDefault(h => h.Direct) ?? row.Headgear[0];
            return $"{row.EditorId ?? "(no EditorID)"} [{row.Key}]  headgear: {first.Label} [{first.Key}] " +
                   $"{WigHeadgearConflictDetector.FormatSlots(first.Slots)}{(first.Direct ? string.Empty : " (through a leveled list)")}" +
                   $"{(row.Headgear.Count > 1 ? $" (+{row.Headgear.Count - 1} more)" : string.Empty)}" +
                   $"  inherits from {EditorIdOf(row.Terminus)} [{row.Terminus}]" +
                   $"{(row.Inventory == InventoryState.FromTemplate ? $"  outfit of {EditorIdOf(row.OutfitOwner)}" : string.Empty)}";
        }

        Line($"EXAMPLES ({examples.Count} of {exposed.Count})");
        foreach (var row in examples) Line("  " + Describe(row));
        Line();

        Line("CHECK ON THE NPC THE DEFECT WAS SEEN ON");
        var twin = inheritors.FirstOrDefault(r => r.Key == RikkeTwin);
        if (twin == null)
        {
            var twinRecord = ResolveNpc(RikkeTwin);
            Line($"  {RikkeTwin}: not in the (1) set (record {(twinRecord == null ? "not in the load order" : "'" + twinRecord.EditorID + "' present")}, " +
                 $"templated with Traits: {Auxilliary.IsValidTemplatedNpc(twinRecord)})");
        }
        else
        {
            EvaluateOutfit(twin);
            Line($"  {twin.EditorId} [{twin.Key}]: (1) yes, T = {EditorIdOf(twin.Terminus)} [{twin.Terminus}]; " +
                 $"T in the mod's list: {TerminusInList(primary, twin)}; (2): {TerminusHasWig(primary, twin)} ({WigSourcesOf(twin.Terminus)}); " +
                 $"(3): {twin.Headgear.Count > 0}" +
                 (twin.Headgear.Count > 0 ? " (" + string.Join("; ", twin.Headgear.Select(h => h.Label)) + ")" : string.Empty) +
                 $"; itself in the mod's list: {primary.NpcFormKeys.Contains(twin.Key)}; selected on this arm: {SelectionOf(twin.Key)}; " +
                 $"T selected on this arm: {SelectionOf(twin.Terminus)}");
        }
        Line();

        Line("NOT COUNTED, OR COUNTED TOO WIDELY");
        Line("  - (2) is the mod's analysis, not a conversion: the converter declines a skin-carried wig when the skin carries");
        Line("    more than one wig ArmorAddon that applies to the NPC's race, when none applies, when the wig mesh or the");
        Line("    FaceGen mesh is missing, or when the race builds no FaceGen head. Those T are still counted here.");
        Line("  - (2) asks whether the mod HAS a convertible wig for T, not whether T is selected with that mod or whether");
        Line("    the mod's wig mode converts: this is exposure if the user picks the mod for T with 'Convert to head parts'");
        Line("    (or 'Forward to Outfit' where the outfit field is inert, which converts too).");
        Line("  - (3) follows every leveled-list entry, so headgear the list gives only at some levels or by chance counts.");
        Line("  - (3) reads DefaultOutfit only: no sleep outfit, no items in the NPC's inventory, no outfit a script, quest");
        Line("    alias, SkyPatcher or SPID gives at run time, and no outfit an appearance selection of the NPC itself adds.");
        Line("  - Chains and outfits are read from the load order's winning records. The appearance mod's own plugin is not");
        Line("    in the load order, so a template link or outfit that only it changes is not seen.");
        Line("  - NPCs whose chain goes through a leveled list are left out; the game picks their face at run time.");
        Line("  - The NPCs T themselves (a converted wig under T's OWN headgear) are a different question and not counted.");
        Line();

        Line($"ALL TEMPLATES T OF THE EXPOSED SET ({byTemplate.Count})");
        foreach (var group in byTemplate)
        {
            Line($"  {group.Rows.Count,5}  {group.EditorId} [{group.Terminus}]  ({WigSourcesOf(group.Terminus)})");
        }
        Line();

        Line($"ALL EXPOSED NPCS ({exposed.Count})");
        foreach (var group in byTemplate)
        {
            foreach (var row in group.Rows) Line("  " + Describe(row));
        }
        Line();

        total.Stop();
        Line($"TIMING: environment {environmentTimer.Elapsed.TotalSeconds:F1} s, count {countTimer.Elapsed.TotalSeconds:F1} s, total {total.Elapsed.TotalSeconds:F1} s");

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            File.WriteAllText(outPath, report.ToString());
            _output.WriteLine($"result written to {outPath}");
        }

        _output.WriteLine($"NPCs {allNpcs}; (1) {inheritors.Count}; leveled {leveledStop}; unfollowable {unfollowable}; " +
                          $"(1)+in list {c1InList.Count}; (1)+(2) {c12.Count}; EXPOSED {exposed.Count}; " +
                          $"total {total.Elapsed.TotalSeconds:F1} s");
        if (string.IsNullOrWhiteSpace(outPath)) _output.WriteLine(report.ToString());
    }
}
