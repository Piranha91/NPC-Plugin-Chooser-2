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
/// Counts, on a real load order, the NPCs for which <c>Patcher.MirrorAppearanceOntoInheritingNpcs</c>
/// would write its "[its own record's race, sex or voice differs from the template's record]"
/// marker if every NPC of an appearance mod's list were patched (bench finding N11, the
/// "select everything" case). The mirror gives an NPC that takes its appearance from a patched NPC
/// through its Traits template that NPC's appearance entry, and the entry stops the NPC inheriting
/// its traits; from then on its OWN record's race, sex and voice are in force. Where those differ
/// from the template's, the result may be wrong. This says how many NPCs that is, and what the
/// differing values on their own records look like.
///
/// <para>Read-only and record-level: it resolves the load order of a bench arm, reads the staged
/// settings for the mod entry's NPC list, and writes one text file. It builds no output, runs no
/// patcher and starts nothing. The comparison is a copy of the three predicates of the mirror
/// method (<see cref="Compare"/>); the method itself is private and is not called.</para>
///
/// <para>Opt-in, because it needs a mod manager instance: does nothing unless
/// <c>NPC2_N11_TRAITS_SPEC</c> names a bench arm spec
/// (<c>{ "instance", "profile", "settingsPath" }</c>, the format of
/// <see cref="BenchOutputBuilder"/>). <c>NPC2_N11_TRAITS_OUT</c> is the text file to write (test
/// output only when unset). <c>NPC2_N11_TRAITS_MOD</c> picks the appearance mod entry by a part of
/// its display name; the default is "High Poly".</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class InheritedTraitMismatchDiagnostic
{
    private sealed class Spec
    {
        public string Instance { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
        public string SettingsPath { get; set; } = string.Empty;
    }

    /// <summary>One NPC whose Traits template chain ends at an NPC of the mod's list.</summary>
    private sealed class Inheritor
    {
        public INpcGetter Record { get; init; } = null!;
        public INpcGetter Terminus { get; init; } = null!;
        /// <summary>The first chain member that is in the mod's list: the mirror's source when every
        /// listed NPC has an appearance entry of its own.</summary>
        public INpcGetter Nearest { get; init; } = null!;
        public bool Unique { get; init; }
        public bool InList { get; init; }
        public bool Race { get; init; }
        public bool Sex { get; init; }
        public bool Voice { get; init; }
        public bool Any => Race || Sex || Voice;
        public bool NearestAny { get; init; }
    }

    private const int ExampleLimit = 20;

    private readonly ITestOutputHelper _output;
    public InheritedTraitMismatchDiagnostic(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The three predicates of <c>Patcher.MirrorAppearanceOntoInheritingNpcs</c>'s
    /// <c>ownTraitsDiffer</c>, copied verbatim and kept apart so each can be counted: race by
    /// FormKey, sex by <see cref="Auxilliary.GetGender"/> (the Female flag), voice by FormKey (a null
    /// link compares as the null FormKey).
    /// </summary>
    private static (bool Race, bool Sex, bool Voice) Compare(INpcGetter npc, INpcGetter templateRecord) => (
        !npc.Race.FormKey.Equals(templateRecord.Race.FormKey),
        Auxilliary.GetGender(npc) != Auxilliary.GetGender(templateRecord),
        !npc.Voice.FormKey.Equals(templateRecord.Voice.FormKey));

    [Fact]
    public void CountInheritorsWhoseOwnRaceSexOrVoiceDiffersFromTheTemplate()
    {
        var specPath = Environment.GetEnvironmentVariable("NPC2_N11_TRAITS_SPEC");
        if (string.IsNullOrWhiteSpace(specPath))
        {
            _output.WriteLine("SKIPPED: set NPC2_N11_TRAITS_SPEC=<arm spec json> to count the inheriting NPCs whose own traits differ.");
            return;
        }

        var outPath = Environment.GetEnvironmentVariable("NPC2_N11_TRAITS_OUT");
        var wanted = Environment.GetEnvironmentVariable("NPC2_N11_TRAITS_MOD");
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
        var primary = mods[0];
        var patchedList = primary.NpcFormKeys.ToHashSet();
        bool allListsEqual = mods.All(m => m.NpcFormKeys.Count == patchedList.Count && m.NpcFormKeys.All(patchedList.Contains));

        INpcGetter? ResolveNpc(FormKey key) => linkCache.TryResolve<INpcGetter>(key, out var n) ? n : null;
        bool IsLeveledNpc(FormKey key) => linkCache.TryResolve<ILeveledNpcGetter>(key, out _);

        // ---- The inheritors: the Traits chain, over the load order's winning records ----
        var countTimer = Stopwatch.StartNew();
        int allNpcs = 0, templatedWithTraits = 0, resolvedChains = 0, leveledStop = 0, unfollowable = 0;
        var inheritors = new List<Inheritor>();
        foreach (var npc in loadOrder.PriorityOrder.Npc().WinningOverrides())
        {
            allNpcs++;
            if (!Auxilliary.IsValidTemplatedNpc(npc)) continue;
            templatedWithTraits++;

            var status = Auxilliary.TryResolveAppearanceTerminus(npc, ResolveNpc, out var terminusKey, IsLeveledNpc);
            if (status == FaceGenChainStatus.LeveledTerminus) { leveledStop++; continue; }
            if (status != FaceGenChainStatus.Resolved) { unfollowable++; continue; }
            resolvedChains++;

            if (!patchedList.Contains(terminusKey)) continue;
            var terminus = ResolveNpc(terminusKey);
            if (terminus == null) continue;

            // The mirror's hop loop, with "patched" read as "in the mod's list".
            INpcGetter nearest = terminus;
            INpcGetter? current = npc;
            while (current != null && Auxilliary.IsValidTemplatedNpc(current))
            {
                var next = current.Template.FormKey;
                if (patchedList.Contains(next))
                {
                    nearest = ResolveNpc(next) ?? terminus;
                    break;
                }

                current = ResolveNpc(next);
            }

            var (race, sex, voice) = Compare(npc, terminus);
            var againstNearest = Compare(npc, nearest);
            inheritors.Add(new Inheritor
            {
                Record = npc,
                Terminus = terminus,
                Nearest = nearest,
                Unique = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique),
                InList = patchedList.Contains(npc.FormKey),
                Race = race,
                Sex = sex,
                Voice = voice,
                NearestAny = againstNearest.Race || againstNearest.Sex || againstNearest.Voice,
            });
        }
        countTimer.Stop();

        // ---- Naming the values ----
        string RaceLabel(INpcGetter npc)
        {
            if (npc.Race.IsNull) return "(no race)";
            return linkCache.TryResolve<IRaceGetter>(npc.Race.FormKey, out var race) && race != null
                ? $"{race.EditorID ?? "(no EditorID)"} [{npc.Race.FormKey}]"
                : $"(not in the load order) [{npc.Race.FormKey}]";
        }

        string RaceName(INpcGetter npc)
        {
            if (npc.Race.IsNull) return "(no race)";
            return linkCache.TryResolve<IRaceGetter>(npc.Race.FormKey, out var race) && race != null
                ? race.EditorID ?? npc.Race.FormKey.ToString()
                : $"(not in the load order) {npc.Race.FormKey}";
        }

        bool RaceResolves(INpcGetter npc) =>
            !npc.Race.IsNull && linkCache.TryResolve<IRaceGetter>(npc.Race.FormKey, out _);

        bool IsDefaultRace(INpcGetter npc) =>
            !npc.Race.IsNull && linkCache.TryResolve<IRaceGetter>(npc.Race.FormKey, out var race) && race != null &&
            string.Equals(race.EditorID, "DefaultRace", StringComparison.OrdinalIgnoreCase);

        string VoiceLabel(INpcGetter npc)
        {
            if (npc.Voice.IsNull) return "(no voice)";
            return linkCache.TryResolve<IVoiceTypeGetter>(npc.Voice.FormKey, out var voice) && voice != null
                ? $"{voice.EditorID ?? "(no EditorID)"} [{npc.Voice.FormKey}]"
                : $"(not in the load order) [{npc.Voice.FormKey}]";
        }

        string VoiceName(INpcGetter npc)
        {
            if (npc.Voice.IsNull) return "(no voice)";
            return linkCache.TryResolve<IVoiceTypeGetter>(npc.Voice.FormKey, out var voice) && voice != null
                ? voice.EditorID ?? npc.Voice.FormKey.ToString()
                : $"(not in the load order) {npc.Voice.FormKey}";
        }

        bool VoiceResolves(INpcGetter npc) =>
            !npc.Voice.IsNull && linkCache.TryResolve<IVoiceTypeGetter>(npc.Voice.FormKey, out _);

        string SexLabel(INpcGetter npc) => Auxilliary.IsFemale(npc) ? "female" : "male";
        string Who(INpcGetter npc) => $"{npc.EditorID ?? "(no EditorID)"} [{npc.FormKey}]";

        var raceRows = inheritors.Where(r => r.Race).ToList();
        var sexRows = inheritors.Where(r => r.Sex).ToList();
        var voiceRows = inheritors.Where(r => r.Voice).ToList();
        var anyRows = inheritors.Where(r => r.Any).ToList();

        IEnumerable<Inheritor> Ordered(IEnumerable<Inheritor> rows) => rows
            .OrderByDescending(r => r.Unique)
            .ThenBy(r => r.Record.EditorID, StringComparer.OrdinalIgnoreCase);

        // ---- The report ----
        var report = new StringBuilder();
        void Line(string text = "") => report.AppendLine(text);

        Line("N11 trait mismatch: inheriting NPCs whose own record's race, sex or voice differs from their template's");
        Line($"written {DateTime.Now:yyyy-MM-dd HH:mm:ss} by InheritedTraitMismatchDiagnostic (read-only, record level)");
        Line();
        Line("INPUT");
        Line($"  spec: {specPath}");
        Line($"  instance: {spec.Instance}; profile: {spec.Profile}");
        Line($"  settings: {spec.SettingsPath}");
        Line($"  staged run settings: backend {settings.OutputBackend}" +
             $"{(settings.OutputBackend == OutputBackend.Recast ? "/" + settings.RecastMode : string.Empty)}, " +
             $"patching mode {settings.PatchingMode}, template handling {settings.TemplateHandlingMode}, " +
             $"{settings.SelectedAppearanceMods.Count} selections (not used: every NPC of the list counts as patched)");
        Line($"  load order: {env.ResolvedPlugins.Count} plugins resolved from the profile, " +
             $"{env.UnresolvedPlugins.Count} active plugins not found" +
             (env.UnresolvedPlugins.Count > 0 ? " (" + string.Join(", ", env.UnresolvedPlugins) + ")" : string.Empty));
        Line($"  mod entries whose name contains '{wanted}': {mods.Count}; counted against '{primary.DisplayName}' " +
             $"({patchedList.Count} NPCs in its list; the other entries' lists are {(allListsEqual ? "the same" : "NOT the same")})");
        Line();
        Line("DEFINITION");
        Line("  Inheritor: an NPC whose winning record in the load order has a Template and the Traits template flag, whose");
        Line("    chain (each step: winning record, Template set, Traits flag set) is followable to an NPC record T that is");
        Line("    not itself such a record (Auxilliary.TryResolveAppearanceTerminus == Resolved; a chain through a leveled");
        Line("    NPC list is left out, as the mirror leaves it out), and T is in the mod entry's NPC list");
        Line("    (ModSetting.NpcFormKeys). 'Select everything': every NPC of that list is taken to have an appearance entry.");
        Line("  Comparison: the three predicates of Patcher.MirrorAppearanceOntoInheritingNpcs's 'ownTraitsDiffer', COPIED");
        Line("    (the method is private; it was not changed or called), the inheritor's winning record against T's:");
        Line("      race : !npc.Race.FormKey.Equals(template.Race.FormKey)");
        Line("      sex  : Auxilliary.GetGender(npc) != Auxilliary.GetGender(template)   (the Female flag)");
        Line("      voice: !npc.Voice.FormKey.Equals(template.Voice.FormKey)             (a null link is the null FormKey)");
        Line("  The mirror compares against the NEAREST patched chain member, not always T. With every listed NPC patched");
        Line("    that is the first chain member in the list; the difference is reported under NEAREST below.");
        Line("  An inheritor that is itself in the list is still counted: under template handling 'InheritFromTemplate' its");
        Line("    own selection is rejected because it inherits, and the mirror covers it. Under 'GiveEachNpcOwnCopy' it");
        Line("    would get an entry of its own and the mirror would skip it; the in-list / not-in-list split is given.");
        Line();
        Line("COUNTS (whole load order, winning records)");
        Line($"  NPC records: {allNpcs}");
        Line($"  with a Template and the Traits template flag: {templatedWithTraits}");
        Line($"    chain ends at an NPC: {resolvedChains}; through a leveled list (left out): {leveledStop}; not followable: {unfollowable}");
        Line($"  INHERITORS (chain ends at an NPC of the mod's list)      : {inheritors.Count}");
        Line($"    themselves in the mod's list / not in it               : {inheritors.Count(r => r.InList)} / {inheritors.Count(r => !r.InList)}");
        Line($"    Unique flag set                                        : {inheritors.Count(r => r.Unique)}");
        Line();
        Line("  own record differs from T's record in");
        Line($"    race                                                   : {raceRows.Count}   (Unique: {raceRows.Count(r => r.Unique)})");
        Line($"    sex (Female flag)                                      : {sexRows.Count}   (Unique: {sexRows.Count(r => r.Unique)})");
        Line($"    voice                                                  : {voiceRows.Count}   (Unique: {voiceRows.Count(r => r.Unique)})");
        Line($"    ANY of the three (the mirror's marker)                 : {anyRows.Count}   (Unique: {anyRows.Count(r => r.Unique)})");
        Line($"      themselves in the mod's list / not in it             : {anyRows.Count(r => r.InList)} / {anyRows.Count(r => !r.InList)}");
        Line($"      race only / sex only / voice only                    : {anyRows.Count(r => r.Race && !r.Sex && !r.Voice)} / {anyRows.Count(r => !r.Race && r.Sex && !r.Voice)} / {anyRows.Count(r => !r.Race && !r.Sex && r.Voice)}");
        Line($"      race+sex / race+voice / sex+voice / all three        : {anyRows.Count(r => r.Race && r.Sex && !r.Voice)} / {anyRows.Count(r => r.Race && !r.Sex && r.Voice)} / {anyRows.Count(r => !r.Race && r.Sex && r.Voice)} / {anyRows.Count(r => r.Race && r.Sex && r.Voice)}");
        Line($"    none of the three                                      : {inheritors.Count - anyRows.Count}");
        Line();
        Line("  NEAREST (the mirror's own comparison partner when every listed NPC is patched)");
        Line($"    inheritors whose nearest listed chain member is not T  : {inheritors.Count(r => r.Nearest.FormKey != r.Terminus.FormKey)}");
        Line($"    marker against the nearest listed member               : {inheritors.Count(r => r.NearestAny)}");
        Line($"    marker against T but not against the nearest, or the reverse: {inheritors.Count(r => r.Any && !r.NearestAny)} / {inheritors.Count(r => !r.Any && r.NearestAny)}");
        Line();

        Line("WHAT THE DIFFERING VALUE ON THE INHERITOR'S OWN RECORD LOOKS LIKE");
        Line("  race (of the race-differing inheritors), own record:");
        Line($"    no race link                                           : {raceRows.Count(r => r.Record.Race.IsNull)}");
        Line($"    DefaultRace                                            : {raceRows.Count(r => IsDefaultRace(r.Record))}");
        Line($"    a race link that is not in the load order              : {raceRows.Count(r => !r.Record.Race.IsNull && !RaceResolves(r.Record))}");
        Line($"    another race that resolves (a real value)              : {raceRows.Count(r => RaceResolves(r.Record) && !IsDefaultRace(r.Record))}");
        Line($"    for reference, T's race is missing or DefaultRace      : {raceRows.Count(r => r.Terminus.Race.IsNull || IsDefaultRace(r.Terminus))}");
        Line("    pairs, own race -> T's race:");
        foreach (var pair in raceRows.GroupBy(r => $"{RaceName(r.Record)} -> {RaceName(r.Terminus)}")
                     .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(40))
        {
            Line($"      {pair.Count(),5}  {pair.Key}");
        }
        Line("  sex (of the sex-differing inheritors): the Female flag is one bit and has no unset state; 'male' is also what");
        Line("  a record shows when nobody set it.");
        Line($"    own record male, T female                              : {sexRows.Count(r => !Auxilliary.IsFemale(r.Record))}");
        Line($"    own record female, T male (a set bit: a real value)    : {sexRows.Count(r => Auxilliary.IsFemale(r.Record))}");
        Line("  voice (of the voice-differing inheritors), own record:");
        Line($"    no voice link (unset)                                  : {voiceRows.Count(r => r.Record.Voice.IsNull)}");
        Line($"    a voice link that is not in the load order             : {voiceRows.Count(r => !r.Record.Voice.IsNull && !VoiceResolves(r.Record))}");
        Line($"    another voice type that resolves (a real value)        : {voiceRows.Count(r => VoiceResolves(r.Record))}");
        Line($"    for reference, T has no voice link while the own record has one: {voiceRows.Count(r => r.Terminus.Voice.IsNull && !r.Record.Voice.IsNull)}");
        Line("    pairs, own voice -> T's voice:");
        foreach (var pair in voiceRows.GroupBy(r => $"{VoiceName(r.Record)} -> {VoiceName(r.Terminus)}")
                     .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(40))
        {
            Line($"      {pair.Count(),5}  {pair.Key}");
        }
        Line();

        string Flags(Inheritor row) =>
            $"{(row.Unique ? "Unique" : "not unique")}, {(row.InList ? "in the list" : "not in the list")}";

        Line($"EXAMPLES: RACE ({Math.Min(ExampleLimit, raceRows.Count)} of {raceRows.Count}; Unique first)");
        foreach (var row in Ordered(raceRows).Take(ExampleLimit))
        {
            Line($"  {Who(row.Record)}  own: {RaceLabel(row.Record)}  template {Who(row.Terminus)}: {RaceLabel(row.Terminus)}  ({Flags(row)})");
        }
        Line();
        Line($"EXAMPLES: SEX ({Math.Min(ExampleLimit, sexRows.Count)} of {sexRows.Count}; Unique first)");
        foreach (var row in Ordered(sexRows).Take(ExampleLimit))
        {
            Line($"  {Who(row.Record)}  own: {SexLabel(row.Record)}  template {Who(row.Terminus)}: {SexLabel(row.Terminus)}  ({Flags(row)})");
        }
        Line();
        Line($"EXAMPLES: VOICE ({Math.Min(ExampleLimit, voiceRows.Count)} of {voiceRows.Count}; Unique first)");
        foreach (var row in Ordered(voiceRows).Take(ExampleLimit))
        {
            Line($"  {Who(row.Record)}  own: {VoiceLabel(row.Record)}  template {Who(row.Terminus)}: {VoiceLabel(row.Terminus)}  ({Flags(row)})");
        }
        Line();
        Line($"ALL UNIQUE INHERITORS THAT DIFFER ({anyRows.Count(r => r.Unique)})");
        foreach (var row in Ordered(anyRows.Where(r => r.Unique)))
        {
            Line($"  {Who(row.Record)} '{row.Record.Name?.String}'  template {Who(row.Terminus)}" +
                 (row.Race ? $"  race {RaceName(row.Record)} -> {RaceName(row.Terminus)}" : string.Empty) +
                 (row.Sex ? $"  sex {SexLabel(row.Record)} -> {SexLabel(row.Terminus)}" : string.Empty) +
                 (row.Voice ? $"  voice {VoiceName(row.Record)} -> {VoiceName(row.Terminus)}" : string.Empty));
        }
        Line();

        Line("NOT COUNTED, OR COUNTED TOO WIDELY");
        Line("  - Only the three fields of the mirror's marker. The Traits flag covers more of the record; the appearance");
        Line("    entry delivers the face, skin, height and weight, so those are not compared here.");
        Line("  - Winning records of the load order. The appearance mod's own plugin is not in it, so a race, sex or voice");
        Line("    that only the mod's plugin changes on T is not seen; the entry would carry a race or sex directive for T");
        Line("    in that case and the mirror repeats it for the inheritor.");
        Line("  - 'Patched' is 'in the mod's list', not 'passed screening': a listed NPC the run would refuse still counts.");
        Line("  - NPCs whose chain goes through a leveled list are left out; the mirror leaves them alone too.");
        Line();

        total.Stop();
        Line($"TIMING: environment {environmentTimer.Elapsed.TotalSeconds:F1} s, count {countTimer.Elapsed.TotalSeconds:F1} s, total {total.Elapsed.TotalSeconds:F1} s");

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            File.WriteAllText(outPath, report.ToString());
            _output.WriteLine($"result written to {outPath}");
        }

        _output.WriteLine($"NPCs {allNpcs}; inheritors {inheritors.Count}; race {raceRows.Count}; sex {sexRows.Count}; " +
                          $"voice {voiceRows.Count}; any {anyRows.Count} (Unique {anyRows.Count(r => r.Unique)}); " +
                          $"total {total.Elapsed.TotalSeconds:F1} s");
        if (string.IsNullOrWhiteSpace(outPath)) _output.WriteLine(report.ToString());
    }
}
