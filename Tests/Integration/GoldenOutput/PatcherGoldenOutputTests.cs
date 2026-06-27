using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Builds the golden-test environment (vanilla + USSEP + AI Overhaul from mod folders) once and reuses it
/// across the 12 combo cases. Holds a skip reason instead of throwing when the local map / Skyrim install
/// is unavailable, so the whole class skips gracefully.
/// </summary>
public sealed class GoldenEnvFixture : IDisposable
{
    internal GoldenOutputConfig? Config { get; }
    internal GoldenEnvironment? Env { get; }
    public string SkipReason { get; } = string.Empty;
    private readonly string _envOutputDir;

    public GoldenEnvFixture()
    {
        _envOutputDir = Path.Combine(Path.GetTempPath(), "NpcGoldenEnv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_envOutputDir);

        if (!GoldenOutputConfig.TryLoad(out var config, out var skip)) { SkipReason = skip; return; }
        Config = config;
        if (!GoldenEnvironmentBuilder.TryBuild(config!, _envOutputDir, "NPC", out var env, out var envSkip))
        {
            SkipReason = envSkip;
            return;
        }
        Env = env;
    }

    public bool Available => Env != null && Config != null;

    public void Dispose()
    {
        try { if (Directory.Exists(_envOutputDir)) Directory.Delete(_envOutputDir, true); } catch { }
    }
}

/// <summary>
/// Golden-output comparison: runs the real patcher headlessly across all 12 setting combinations and
/// compares the output (plugin records + assets + SkyPatcher .ini) against the committed reference set.
/// Appearance is compared by resolved EditorID; assets by content hash. The two SkyPatcher+Include combos
/// (08, 11) have references captured before the ChildClothes01 fix, so they assert the fix and tolerate
/// exactly that deviation. Skips gracefully without the local map / a Skyrim SE install.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class PatcherGoldenOutputTests : IClassFixture<GoldenEnvFixture>
{
    private readonly GoldenEnvFixture _fixture;
    private readonly ITestOutputHelper _output;

    private static readonly FormKey ChildClothes01 = FormKey.Factory("06D92C:Skyrim.esm");

    public PatcherGoldenOutputTests(GoldenEnvFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public static IEnumerable<object[]> ComboIndices => GoldenCombos.All.Select(c => new object[] { c.Index });

    /// <summary>
    /// The reference plugin/token/ini/sel for a combo: prefer the committed, version-controlled copy under
    /// Tests/TestData/GoldenReference; fall back to the (gitignored) external reference root. Assets are
    /// never committed (licensing) and always come from the external root.
    /// </summary>
    private static string ReferenceArtifactDir(GoldenOutputConfig config, string folder)
    {
        var committed = GoldenPaths.CommittedReferenceDir;
        if (committed != null)
        {
            var dir = Path.Combine(committed, folder);
            if (File.Exists(Path.Combine(dir, "NPC.esp"))) return dir;
        }
        return config.ReferenceComboDir(folder);
    }

    [Theory]
    [MemberData(nameof(ComboIndices))]
    public async Task Combo_MatchesReference(int comboIndex)
    {
        if (!_fixture.Available) { _output.WriteLine("SKIPPED: " + _fixture.SkipReason); return; }
        var config = _fixture.Config!;
        var combo = GoldenCombos.All.First(c => c.Index == comboIndex);

        var refDir = ReferenceArtifactDir(config, combo.FolderName);     // plugin / token / ini / sel
        var assetRefDir = config.ReferenceComboDir(combo.FolderName);    // assets (external only)
        var refPlugin = Path.Combine(refDir, "NPC.esp");
        if (!File.Exists(refPlugin)) { _output.WriteLine($"SKIPPED: reference plugin missing for '{combo.FolderName}'."); return; }

        var outDir = Path.Combine(Path.GetTempPath(), "NpcGolden_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var settings = GoldenComboSettingsBuilder.Build(config, combo, outDir);
            var run = await GoldenPatchRunner.RunAsync(_fixture.Env!.Provider, settings);
            _output.WriteLine($"[{combo.FolderName}] patched {run.PatchedTargets.Count}, invalid {run.InvalidSelections.Count}.");

            using var freshHandle = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(outDir, "NPC.esp"), SkyrimRelease.SkyrimSE);
            using var refHandle = SkyrimMod.CreateFromBinaryOverlay(refPlugin, SkyrimRelease.SkyrimSE);
            ISkyrimModGetter fresh = freshHandle;
            ISkyrimModGetter reference = refHandle;

            var expectedTargets = ReferenceToken.ProcessedTargets(refDir);
            expectedTargets.Should().NotBeEmpty("the reference token lists the NPCs that combo processed");

            bool stale = GoldenCombos.IsStaleForChildClothesFix(combo);
            var failures = new List<string>();

            // --- Assets: content-hash comparison (SkyPatcher facegen matched by content). Assets are not
            // committed, so they are only compared when the external reference root holds them. ---
            if (Directory.Exists(Path.Combine(assetRefDir, "meshes")) || Directory.Exists(Path.Combine(assetRefDir, "textures")))
            {
                var assets = AssetComparer.Compare(outDir, assetRefDir, combo.UseSkyPatcher);
                if (assets.MissingFromFresh.Count > 0 || assets.HashMismatches.Count > 0
                    || (!stale && assets.ExtraInFresh.Count > 0))
                {
                    // For the stale combos, the fix legitimately adds the ChildClothes01 override's assets, so
                    // EXTRA-in-fresh is tolerated there; missing/hash-mismatch is always a failure.
                    failures.Add("ASSETS:\n" + assets.Describe());
                }
            }
            else
            {
                _output.WriteLine("NOTE: external reference assets unavailable; skipping asset comparison.");
            }

            // --- Appearance / SkyPatcher directives (unaffected by the ChildClothes01 fix). ---
            var refEids = NpcAppearanceComparer.BuildEditorIdMap(reference);
            var freshEids = NpcAppearanceComparer.BuildEditorIdMap(fresh);

            if (!combo.UseSkyPatcher)
            {
                foreach (var target in expectedTargets)
                {
                    var r = reference.Npcs.FirstOrDefault(n => n.FormKey == target);
                    var f = fresh.Npcs.FirstOrDefault(n => n.FormKey == target);
                    if (r == null || f == null)
                    {
                        failures.Add($"NPC {target}: reference={(r == null ? "MISSING" : "ok")} fresh={(f == null ? "MISSING" : "ok")}");
                        continue;
                    }
                    var diffs = NpcAppearanceComparer.Compare(r, refEids, f, freshEids);
                    if (diffs.Count > 0) failures.Add($"NPC {target} '{r.EditorID}':\n  " + string.Join("\n  ", diffs));
                }
            }
            else
            {
                var refIni = Path.Combine(refDir, SkyPatcherIniComparer.DefaultIniRelativePath);
                var freshIni = Path.Combine(outDir, SkyPatcherIniComparer.DefaultIniRelativePath);
                File.Exists(freshIni).Should().BeTrue("SkyPatcher mode must emit the .ini");

                var ini = SkyPatcherIniComparer.Compare(refIni, freshIni, "NPC.esp");
                if (!ini.IsMatch) failures.Add("INI:\n" + ini.Describe());

                var refSurr = SkyPatcherIniComparer.SurrogateByTarget(refIni);
                var freshSurr = SkyPatcherIniComparer.SurrogateByTarget(freshIni);
                foreach (var target in expectedTargets)
                {
                    if (!refSurr.TryGetValue(target, out var rSur) || !freshSurr.TryGetValue(target, out var fSur)) continue;
                    var r = reference.Npcs.FirstOrDefault(n => n.FormKey == rSur);
                    var f = fresh.Npcs.FirstOrDefault(n => n.FormKey == fSur);
                    if (r == null || f == null)
                    {
                        failures.Add($"Surrogate for {target}: reference={(r == null ? "MISSING" : "ok")} fresh={(f == null ? "MISSING" : "ok")}");
                        continue;
                    }
                    var diffs = NpcAppearanceComparer.Compare(r, refEids, f, freshEids);
                    if (diffs.Count > 0) failures.Add($"Surrogate {target} '{r.EditorID}':\n  " + string.Join("\n  ", diffs));
                }
            }

            // --- Specifically-edited form: ChildClothes01 (0006D92C). ---
            bool ccFresh = fresh.EnumerateMajorRecords().Any(x => x.FormKey == ChildClothes01);
            bool ccRef = reference.EnumerateMajorRecords().Any(x => x.FormKey == ChildClothes01);
            if (combo.OverrideMode == Models.RecordOverrideHandlingMode.Include)
            {
                if (!ccFresh)
                    failures.Add("ChildClothes01 (0006D92C) override MISSING from fresh Include output (the bug).");
                if (stale)
                    _output.WriteLine($"NOTE: '{combo.FolderName}' reference is pre-fix/stale (ccRef={ccRef}); " +
                                      "asserting the fix writes ChildClothes01 and tolerating extra-asset deviations.");
                else if (!ccRef)
                    failures.Add("ChildClothes01 missing from the (non-stale) reference - unexpected.");
            }

            if (failures.Count > 0)
                _output.WriteLine($"[{combo.FolderName}] FAILURES:\n" + string.Join("\n", failures));
            failures.Should().BeEmpty($"combo '{combo.FolderName}' output should match the reference");
        }
        finally
        {
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        }
    }

    [Fact]
    public void SpawnBatch_SelGroup_MatchesReferencePlaceAtMeLines()
    {
        if (!_fixture.Available) { _output.WriteLine("SKIPPED: " + _fixture.SkipReason); return; }
        var config = _fixture.Config!;

        // A reference sel.txt (combo 02 has one). Compare the deterministic player.placeatme block.
        var refSel = Path.Combine(ReferenceArtifactDir(config, "NPC 02 - CreateAndPatch - Include"), "sel.txt");
        if (!File.Exists(refSel)) { _output.WriteLine("SKIPPED: reference sel.txt missing."); return; }

        var outDir = Path.Combine(Path.GetTempPath(), "NpcGoldenSel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var settings = GoldenComboSettingsBuilder.Build(config, GoldenCombos.All.First(c => c.Index == 2), outDir);
            using var harness = new NpcChooserHarness(_fixture.Env!.Provider, settings);
            var aux = Autofac.ResolutionExtensions.Resolve<Auxilliary>(harness.Container);

            // The "sel" group is all 8 targets (NpcGroupAssignments), mirroring the spawn-batch flow.
            var selNpcs = GoldenComboSettingsBuilder.Selections.Select(s => FormKey.Factory(s.Target)).ToList();
            var content = aux.BuildSpawnBatchContent(selNpcs, settings.BatFilePreCommands, settings.BatFilePostCommands,
                out var _, out var unresolved);
            unresolved.Should().BeEmpty("all 8 vanilla NPCs resolve to FormIDs");

            var freshLines = PlaceAtMeLines(content);
            var refLines = PlaceAtMeLines(File.ReadAllText(refSel));
            _output.WriteLine($"placeatme lines: fresh={freshLines.Count} ref={refLines.Count}");
            freshLines.Should().BeEquivalentTo(refLines, "the spawn-batch placeatme block must match the reference group");
        }
        finally
        {
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        }
    }

    private static HashSet<string> PlaceAtMeLines(string content) =>
        content.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("player.placeatme", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.ToLowerInvariant())
            .ToHashSet();
}
