using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autofac;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.Benchmark;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Lab;

/// <summary>
/// Measures what the NPC list's wig-and-headgear indicator costs on a real load order: the
/// per-row question (<see cref="WigForwarder.FindHeadgearConflictsForSelection"/>) put for every
/// NPC of one appearance mod, as the list would put it if every one of them were selected. Twice:
/// the first pass pays for loading the mod's plugins and filling the resolver caches, the second
/// is what a repeat costs. Read-only: it creates no output and writes nothing but test output.
///
/// <para>Opt-in, because it needs a mod manager instance: does nothing unless
/// <c>NPC2_WIG_QUERY_SPEC</c> names a bench arm spec
/// (<c>{ "instance", "profile", "settingsPath" }</c>, the format of
/// <see cref="BenchOutputBuilder"/>). <c>NPC2_WIG_QUERY_MOD</c> picks the mod by a part of its
/// display name; the default is the first mod whose effective wig mode is 'Forward to Outfit'.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class WigHeadgearIndicatorCostDiagnostic
{
    private sealed class Spec
    {
        public string Instance { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
        public string SettingsPath { get; set; } = string.Empty;
    }

    private readonly ITestOutputHelper _output;
    public WigHeadgearIndicatorCostDiagnostic(ITestOutputHelper output) => _output = output;

    [Fact]
    public void MeasurePerRowQuery_OverEveryNpcOfOneMod()
    {
        var specPath = Environment.GetEnvironmentVariable("NPC2_WIG_QUERY_SPEC");
        if (string.IsNullOrWhiteSpace(specPath))
        {
            _output.WriteLine("SKIPPED: set NPC2_WIG_QUERY_SPEC=<arm spec json> to measure the indicator's record queries.");
            return;
        }

        var spec = JsonSerializer.Deserialize<Spec>(File.ReadAllText(specPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var settings = JSONhandler<Settings>.LoadJSONFile(spec.SettingsPath, out var loaded, out var loadError);
        loaded.Should().BeTrue(loadError);

        // Nothing is written by the question, and nothing may land in the instance's own output mod.
        var scratch = Path.Combine(Path.GetTempPath(), "npc2-wig-query-diagnostic");
        Directory.CreateDirectory(scratch);
        settings!.OutputDirectory = scratch;

        Mo2ProfileEnvironment.TryBuild(spec.Instance, spec.Profile, settings.OutputPluginName, out var env, out var failure)
            .Should().BeTrue(failure);
        env!.Reapply();

        using var harness = new NpcChooserHarness(env.Provider, settings);
        harness.PluginProvider.VirtualDataPluginPaths = env.VirtualDataPlugins;
        var forwarder = harness.Container.Resolve<WigForwarder>();

        var wanted = Environment.GetEnvironmentVariable("NPC2_WIG_QUERY_MOD");
        var mod = settings.ModSettings.FirstOrDefault(m =>
            settings.GetEffectiveWigMode(m) == WigHandlingMode.ForwardToOutfit &&
            (string.IsNullOrWhiteSpace(wanted) || m.DisplayName.Contains(wanted, StringComparison.OrdinalIgnoreCase)));
        mod.Should().NotBeNull("the staged settings must hold a mod whose effective wig mode is 'Forward to Outfit'");

        var linkCache = env.Provider.LinkCache!;
        var npcs = mod!.NpcFormKeys
            .Where(k => linkCache.TryResolve<Mutagen.Bethesda.Skyrim.INpcGetter>(k, out _))
            .ToList();

        _output.WriteLine($"profile {spec.Profile}: {env.ResolvedPlugins.Count} plugins; backend {settings.OutputBackend}" +
                          $"{(settings.OutputBackend == OutputBackend.Recast ? "/" + settings.RecastMode : string.Empty)}, " +
                          $"patching mode {settings.PatchingMode}");
        _output.WriteLine($"mod '{mod.DisplayName}': {mod.NpcFormKeys.Count} NPCs, {npcs.Count} of them in the load order");

        var cases = new List<WigHeadgearConflictCase>();
        for (int pass = 1; pass <= 2; pass++)
        {
            var perCall = new List<double>(npcs.Count);
            int withConflict = 0, conflicts = 0;
            cases.Clear();
            var total = Stopwatch.StartNew();
            foreach (var npc in npcs)
            {
                long started = Stopwatch.GetTimestamp();
                var found = forwarder.FindHeadgearConflictsForSelection(npc, npc, mod);
                perCall.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (found.Count > 0)
                {
                    withConflict++;
                    conflicts += found.Count;
                    cases.Add(new WigHeadgearConflictCase(npc, npc.ToString(), mod.DisplayName, found));
                }
            }
            total.Stop();

            perCall.Sort();
            double Percentile(double p) => perCall.Count == 0 ? 0 : perCall[(int)Math.Min(perCall.Count - 1, p * perCall.Count)];
            _output.WriteLine(
                $"pass {pass}: {npcs.Count} queries in {total.Elapsed.TotalSeconds:F2} s; per query mean " +
                $"{(perCall.Count == 0 ? 0 : perCall.Average()):F3} ms, median {Percentile(0.5):F3} ms, " +
                $"95th {Percentile(0.95):F3} ms, max {(perCall.Count == 0 ? 0 : perCall[^1]):F1} ms; " +
                $"{withConflict} NPCs with a conflict ({conflicts} conflicts)");
        }

        // What the pre-run window would have to show for these NPCs.
        var grouping = NPC_Plugin_Chooser_2.View_Models.WigHeadgearConflictGrouping.From(cases);
        int distinctConflictSets = cases
            .Select(c => string.Join("|", c.Conflicts
                .Select(x => $"{x.OutfitFormKey}/{x.Headgear.FormKey}/{x.Wig.FormKey}").OrderBy(s => s, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal).Count();
        var perNpc = cases.Select(c => c.Conflicts.Count).OrderBy(n => n).ToList();
        _output.WriteLine(
            $"window: {grouping.TotalsText}; {grouping.Rows.Count} rows in all; conflicts per NPC median " +
            $"{(perNpc.Count == 0 ? 0 : perNpc[perNpc.Count / 2])}, max {(perNpc.Count == 0 ? 0 : perNpc[^1])}; " +
            $"{distinctConflictSets} distinct sets of conflicts; " +
            $"{cases.SelectMany(c => c.Conflicts).Select(x => x.OutfitFormKey).Distinct().Count()} outfits, " +
            $"{cases.SelectMany(c => c.Conflicts).Select(x => x.Headgear.FormKey).Distinct().Count()} headgear items, " +
            $"{cases.SelectMany(c => c.Conflicts).Select(x => x.Wig.FormKey).Distinct().Count()} wigs");
        foreach (var group in grouping.Groups.Take(5))
        {
            _output.WriteLine($"  {group.CountText}: {group.Description}");
        }
        foreach (var group in grouping.Groups.Skip(grouping.Groups.Count / 2).Take(3))
        {
            _output.WriteLine($"  (mid) {group.CountText}: {group.Description}");
        }
    }
}
