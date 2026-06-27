using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Strongly-typed SOURCE-ORACLE comparison for the RS Children dependency records the patcher writes for
/// Dorthe - independent of the golden reference, so it catches both patcher bugs AND mistakes in the golden
/// output. For every in-place override record the patcher emits it serializes four versions to their full
/// element tree (Mutagen YAML) and checks, element by element:
/// <list type="bullet">
/// <item>Create (wholesale forward): output == RS Children's source record.</item>
/// <item>Create-and-Patch (delta patch): for each element, output == RS Children's value where RS Children
/// differs from the Skyrim.esm base, else == the conflict-winning value (e.g. USSEP). This is exactly the
/// delta the patcher should produce, derived here independently from the source/base/winning records.</item>
/// </list>
/// FormKey leaves are normalized to EditorID so merged-in records (remapped FormKeys) compare correctly.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class SourceOracleTests : IClassFixture<GoldenEnvFixture>
{
    private readonly GoldenEnvFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SourceOracleTests(GoldenEnvFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData(5, false)] // Create / Include          -> wholesale forward
    [InlineData(2, true)]  // CreateAndPatch / Include   -> delta patch
    public async Task InPlaceOverrides_MatchSource(int comboIndex, bool deltaMode)
    {
        if (!_fixture.Available) { _output.WriteLine("SKIPPED: " + _fixture.SkipReason); return; }
        var config = _fixture.Config!;
        var combo = GoldenCombos.All.First(c => c.Index == comboIndex);

        var outDir = Path.Combine(Path.GetTempPath(), "NpcOracle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var settings = GoldenComboSettingsBuilder.Build(config, combo, outDir);
            await GoldenPatchRunner.RunAsync(_fixture.Env!.Provider, settings);

            // RS Children link cache (masters before plugins for correct conflict resolution).
            var rsc = config.AppearanceMods["RS Children Overhaul"];
            var rscMods = rsc.Plugins
                .Select(p => SkyrimMod.CreateFromBinaryOverlay(Path.Combine(rsc.Folders[0], p), SkyrimRelease.SkyrimSE))
                .OrderBy(m => m.ModKey.Type == ModType.Plugin ? 1 : 0)
                .ToList();
            var rscCache = rscMods.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();

            using var fresh = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(outDir, "NPC.esp"), SkyrimRelease.SkyrimSE);
            var freshCache = fresh.ToImmutableLinkCache();
            var env = _fixture.Env!.Provider.LinkCache!;

            string? Resolve(FormKey fk)
            {
                if (env.TryResolve(fk, out var a)) return a.EditorID;
                if (rscCache.TryResolve(fk, out var b)) return b.EditorID;
                if (freshCache.TryResolve(fk, out var c)) return c.EditorID;
                return null;
            }

            var outputKey = ModKey.FromFileName("NPC.esp");
            // In-place override records the patcher wrote (vanilla FormKey, non-NPC). For combo 2/5 these
            // are all RS Children's overrides reachable from Dorthe.
            var inPlace = fresh.EnumerateMajorRecords()
                .Where(r => r is not INpcGetter && r.FormKey.ModKey != outputKey)
                .ToList();
            inPlace.Should().NotBeEmpty("the patcher writes RS Children in-place overrides for Dorthe");

            var failures = new List<string>();
            int comparedRecords = 0, comparedElements = 0;

            foreach (var rec in inPlace)
            {
                var type = rec.Registration.GetterType;
                if (!rscCache.TryResolveContext(rec.FormKey, type, out var srcCtx)) continue; // not an RS Children override
                if (!freshCache.TryResolveContext(rec.FormKey, type, out var outCtx)) continue;
                env.TryResolveContext(rec.FormKey, type, out var baseCtx, ResolveTarget.Origin);
                env.TryResolveContext(rec.FormKey, type, out var winCtx, ResolveTarget.Winner);

                var outFlat = await RecordYaml.ToFlatAsync(outCtx!, Resolve);
                var srcFlat = await RecordYaml.ToFlatAsync(srcCtx!, Resolve);
                var baseFlat = baseCtx != null ? await RecordYaml.ToFlatAsync(baseCtx, Resolve) : new();
                var winFlat = winCtx != null ? await RecordYaml.ToFlatAsync(winCtx, Resolve) : srcFlat;

                comparedRecords++;
                string label = $"[{rec.Registration.Name}] {rec.EditorID} ({rec.FormKey})";

                foreach (var key in outFlat.Keys.Union(srcFlat.Keys))
                {
                    comparedElements++;
                    string outV = outFlat.GetValueOrDefault(key, "<absent>");
                    string srcV = srcFlat.GetValueOrDefault(key, "<absent>");

                    string expected;
                    if (!deltaMode)
                    {
                        expected = srcV; // forward: output element == source element
                    }
                    else
                    {
                        string baseV = baseFlat.GetValueOrDefault(key, "<absent>");
                        string winV = winFlat.GetValueOrDefault(key, "<absent>");
                        // delta: RS Children changed this element vs base => take source; else keep winning.
                        expected = srcV != baseV ? srcV : winV;
                    }

                    if (outV != expected)
                        failures.Add($"{label} .{key}: output='{outV}' expected='{expected}'" +
                            (deltaMode ? $" (src='{srcV}' base='{baseFlat.GetValueOrDefault(key, "<absent>")}' win='{winFlat.GetValueOrDefault(key, "<absent>")}')" : ""));
                }
            }

            _output.WriteLine($"[{combo.FolderName}] oracle compared {comparedRecords} records, {comparedElements} elements; {failures.Count} mismatches.");
            if (failures.Count > 0)
                _output.WriteLine(string.Join("\n", failures.Take(40)));
            failures.Should().BeEmpty($"every in-place RS Children override should match the source-derived expectation ({combo.FolderName})");
        }
        finally
        {
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        }
    }

    [Fact]
    public async Task MergedInNewRecords_AreFaithfulCopiesOfRsChildrenSource()
    {
        if (!_fixture.Available) { _output.WriteLine("SKIPPED: " + _fixture.SkipReason); return; }
        var config = _fixture.Config!;
        var combo = GoldenCombos.All.First(c => c.Index == 2); // CreateAndPatch / Include, MergeInDependencyRecords=true

        var outDir = Path.Combine(Path.GetTempPath(), "NpcOracleMerge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var settings = GoldenComboSettingsBuilder.Build(config, combo, outDir);
            await GoldenPatchRunner.RunAsync(_fixture.Env!.Provider, settings);

            var rsc = config.AppearanceMods["RS Children Overhaul"];
            var rscMods = rsc.Plugins
                .Select(p => SkyrimMod.CreateFromBinaryOverlay(Path.Combine(rsc.Folders[0], p), SkyrimRelease.SkyrimSE))
                .OrderBy(m => m.ModKey.Type == ModType.Plugin ? 1 : 0)
                .ToList();
            var rscCache = rscMods.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
            // RS Children's NEW records keyed by EditorID (later/plugin wins).
            var rscByEid = new Dictionary<string, IMajorRecordGetter>(StringComparer.Ordinal);
            foreach (var r in rscMods.SelectMany(m => m.EnumerateMajorRecords()))
                if (!string.IsNullOrEmpty(r.EditorID)) rscByEid[r.EditorID!] = r;

            using var fresh = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(outDir, "NPC.esp"), SkyrimRelease.SkyrimSE);
            var freshCache = fresh.ToImmutableLinkCache();
            var env = _fixture.Env!.Provider.LinkCache!;
            string? Resolve(FormKey fk)
            {
                if (env.TryResolve(fk, out var a)) return a.EditorID;
                if (rscCache.TryResolve(fk, out var b)) return b.EditorID;
                if (freshCache.TryResolve(fk, out var c)) return c.EditorID;
                return null;
            }

            var outputKey = ModKey.FromFileName("NPC.esp");
            var failures = new List<string>();
            int comparedRecords = 0;

            // Merged-in-as-new records the patcher duplicated from RS Children (own FormKey in NPC.esp, EID 0RCO*).
            foreach (var rec in fresh.EnumerateMajorRecords()
                         .Where(r => r is not INpcGetter && r.FormKey.ModKey == outputKey && r.EditorID != null))
            {
                if (!rscByEid.TryGetValue(rec.EditorID!, out var src)) continue; // not from RS Children
                var type = rec.Registration.GetterType;
                if (src.Registration.GetterType != type) continue;
                if (!freshCache.TryResolveContext(rec.FormKey, type, out var outCtx)) continue;
                if (!rscCache.TryResolveContext(src.FormKey, type, out var srcCtx)) continue;

                var outFlat = await RecordYaml.ToFlatAsync(outCtx!, Resolve);
                var srcFlat = await RecordYaml.ToFlatAsync(srcCtx!, Resolve);
                comparedRecords++;

                foreach (var key in outFlat.Keys.Union(srcFlat.Keys))
                {
                    var outV = outFlat.GetValueOrDefault(key, "<absent>");
                    var srcV = srcFlat.GetValueOrDefault(key, "<absent>");
                    if (outV != srcV)
                        failures.Add($"[{rec.Registration.Name}] {rec.EditorID} .{key}: output='{outV}' source='{srcV}'");
                }
            }

            _output.WriteLine($"[{combo.FolderName}] merged-in oracle compared {comparedRecords} RS Children records; {failures.Count} mismatches.");
            if (failures.Count > 0) _output.WriteLine(string.Join("\n", failures.Take(40)));
            comparedRecords.Should().BeGreaterThan(0, "RS Children new records are merged in under MergeInDependencyRecords");
            failures.Should().BeEmpty("every merged-in RS Children record should be a faithful element-by-element copy of its source");
        }
        finally
        {
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        }
    }
}
