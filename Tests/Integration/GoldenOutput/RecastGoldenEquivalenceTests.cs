using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Recast output checked against the SkyPatcher output of the same golden combo, run side by side
/// in the golden environment. The two backends build the same surrogates and copy the same FaceGen
/// (Recast reads FaceGen from the surrogate's path, exactly as SkyPatcher's copyVisualStyle does), so
/// they must agree on everything except how the face is delivered. Comparing them pins that without
/// a separate reference set; references for Recast combos can still be added once the user
/// generates them.
///
/// <para>Per SkyPatcher combo (07-12):</para>
/// <list type="bullet">
/// <item>The Recast TOML targets the NPCs the ini's copyVisualStyle lines target, each through a
///   surrogate with the same appearance. RecastOnly may skip cross-race selections it can't render
///   (screening must say so); the baseline is then re-run without them, since dropping an NPC changes
///   which mod supplies shared textures and shifts the surrogate FormIDs baked into FaceGen.</item>
/// <item>The plugin's masters match; the TOML names plugins by file name and adds none.</item>
/// <item>Hybrid's ini carries the SkyPatcher run's race/outfit/keyword directives and no appearance
///   directive; RecastOnly's carries neither.</item>
/// <item>FaceGen and other assets match by content.</item>
/// </list>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class RecastGoldenEquivalenceTests : IClassFixture<GoldenEnvFixture>
{
    private readonly GoldenEnvFixture _fixture;
    private readonly ITestOutputHelper _output;

    private static readonly string[] AppearanceDirectives =
        { "copyVisualStyle", "skin", "height", "weight", "setFlags", "removeFlags", "setTemplateFlags", "removeTemplateFlags" };

    private static readonly string[] HybridDirectives = { "race", "outfitDefault", "outfitSleep", "keywordsToAdd" };

    public RecastGoldenEquivalenceTests(GoldenEnvFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public static IEnumerable<object[]> Cases =>
        GoldenCombos.All.Where(c => c.UseSkyPatcher)
            .SelectMany(c => new[] { new object[] { c.Index, RecastMode.Hybrid }, new object[] { c.Index, RecastMode.RecastOnly } });

    private async Task<(string Dir, GoldenPatchResult Run)> RunAsync(GoldenCombo combo, OutputBackend backend, RecastMode mode,
        IReadOnlySet<FormKey>? withoutTargets = null)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "NpcRecastGolden_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var settings = GoldenComboSettingsBuilder.Build(_fixture.Config!, combo, outDir);
        settings.OutputBackend = backend;
        settings.RecastMode = mode;
        foreach (var target in withoutTargets ?? new HashSet<FormKey>()) settings.SelectedAppearanceMods.Remove(target);
        var run = await GoldenPatchRunner.RunAsync(_fixture.Env!.Provider, settings);
        _output.WriteLine($"[{combo.FolderName} / {backend} {mode}] patched {run.PatchedTargets.Count}, invalid {run.InvalidSelections.Count}.");
        return (outDir, run);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RecastOutput_MatchesTheSkyPatcherRunOfTheSameCombo(int comboIndex, RecastMode mode)
    {
        if (!_fixture.Available) { _output.WriteLine("SKIPPED: " + _fixture.SkipReason); return; }
        var combo = GoldenCombos.All.First(c => c.Index == comboIndex);

        var (skyDir, _) = await RunAsync(combo, OutputBackend.SkyPatcher, RecastMode.Hybrid);
        var (recastDir, recastRun) = await RunAsync(combo, OutputBackend.Recast, mode);
        try
        {
            var tomlPath = Path.Combine(RecastInterface.GetOutputDir(recastDir), "NPC.toml");
            File.Exists(tomlPath).Should().BeTrue("Recast output must emit its patch");
            var toml = RecastPatchReader.ReadFile(tomlPath);
            toml.IsLoaded.Should().BeTrue(string.Join("; ", toml.Errors));
            toml.ManifestName.Should().Be(RecastInterface.ManifestName);
            var recastFaces = toml.Faces.ToDictionary(f => f.Target.FormKey!.Value, f => f.Source.FormKey!.Value);

            var skyIni = Path.Combine(skyDir, SkyPatcherIniComparer.DefaultIniRelativePath);
            var skipped = SkyPatcherIniComparer.SurrogateByTarget(skyIni).Keys.Except(recastFaces.Keys).ToHashSet();
            if (skipped.Count > 0)
            {
                mode.Should().Be(RecastMode.RecastOnly, "Hybrid delivers every face SkyPatcher does");
                foreach (var target in skipped)
                {
                    recastRun.InvalidSelections.Should().Contain(
                        s => s.Contains(target.ToString(), StringComparison.OrdinalIgnoreCase) && s.Contains("Race change needed"),
                        $"{target} is missing from the TOML, so screening must have rejected it for its race");
                }

                try { Directory.Delete(skyDir, true); } catch { /* best effort */ }
                (skyDir, _) = await RunAsync(combo, OutputBackend.SkyPatcher, RecastMode.Hybrid, skipped);
                _output.WriteLine($"Baseline re-run without the {skipped.Count} skipped NPC(s).");
            }

            using var skyHandle = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyDir, "NPC.esp"), SkyrimRelease.SkyrimSE);
            using var recastHandle = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(recastDir, "NPC.esp"), SkyrimRelease.SkyrimSE);
            ISkyrimModGetter sky = skyHandle;
            ISkyrimModGetter recast = recastHandle;
            var failures = new List<string>();

            // --- Faces: same targets, same surrogate appearance ---
            skyIni = Path.Combine(skyDir, SkyPatcherIniComparer.DefaultIniRelativePath);
            var skyFaces = SkyPatcherIniComparer.SurrogateByTarget(skyIni);
            skyFaces.Should().NotBeEmpty("the SkyPatcher run is the baseline");
            recastFaces.Keys.Should().BeEquivalentTo(skyFaces.Keys, "both runs deliver a face for the same NPCs");

            var skyEids = NpcAppearanceComparer.BuildEditorIdMap(sky);
            var recastEids = NpcAppearanceComparer.BuildEditorIdMap(recast);
            foreach (var (target, recastSurrogate) in recastFaces)
            {
                var s = sky.Npcs.FirstOrDefault(n => n.FormKey == skyFaces[target]);
                var r = recast.Npcs.FirstOrDefault(n => n.FormKey == recastSurrogate);
                if (s == null || r == null)
                {
                    failures.Add($"{target}: surrogate sky={(s == null ? "MISSING" : "ok")} recast={(r == null ? "MISSING" : "ok")}");
                    continue;
                }
                var diffs = NpcAppearanceComparer.Compare(s, skyEids, r, recastEids);
                if (diffs.Count > 0) failures.Add($"{target} surrogate:\n  " + string.Join("\n  ", diffs));
            }

            // --- Masters: the TOML adds none ---
            var skyMasters = sky.ModHeader.MasterReferences.Select(m => m.Master).ToHashSet();
            var recastMasters = recast.ModHeader.MasterReferences.Select(m => m.Master).ToHashSet();
            if (!recastMasters.SetEquals(skyMasters))
            {
                failures.Add($"MASTERS: sky=[{string.Join(", ", skyMasters)}] recast=[{string.Join(", ", recastMasters)}]");
            }

            // --- The ini: Hybrid's non-appearance directives only, none for RecastOnly ---
            var recastIni = Path.Combine(recastDir, SkyPatcherIniComparer.DefaultIniRelativePath);
            var recastDirectives = File.Exists(recastIni)
                ? SkyPatcherIniComparer.DirectivesByTarget(recastIni)
                : new Dictionary<FormKey, IReadOnlyDictionary<string, string>>();
            foreach (var (target, dirs) in recastDirectives)
            {
                foreach (var key in dirs.Keys.Intersect(AppearanceDirectives, StringComparer.OrdinalIgnoreCase))
                    failures.Add($"INI: {target} carries appearance directive '{key}' beside a Recast face");
            }

            var skyDirectives = SkyPatcherIniComparer.DirectivesByTarget(skyIni);
            foreach (var (target, dirs) in skyDirectives)
            {
                if (!recastFaces.ContainsKey(target)) continue;
                foreach (var key in HybridDirectives)
                {
                    if (!dirs.TryGetValue(key, out var skyValue)) continue;
                    recastDirectives.TryGetValue(target, out var recastDirs);
                    string? recastValue = recastDirs != null && recastDirs.TryGetValue(key, out var v) ? v : null;
                    if (mode == RecastMode.Hybrid && !string.Equals(skyValue, recastValue, StringComparison.OrdinalIgnoreCase))
                        failures.Add($"INI: {target} '{key}' sky={skyValue} hybrid={recastValue ?? "(none)"}");
                    if (mode == RecastMode.RecastOnly && recastValue != null && key != "outfitDefault")
                        failures.Add($"INI: {target} '{key}' delivered by SkyPatcher under RecastOnly");
                }
            }

            // --- Assets: same files, FaceGen by content ---
            var assets = AssetComparer.Compare(recastDir, skyDir, skyPatcherMode: true);
            if (!assets.IsMatch) failures.Add("ASSETS: " + assets.Describe());

            failures.Should().BeEmpty($"{combo.FolderName} under Recast {mode} should match its SkyPatcher run");
        }
        finally
        {
            foreach (var dir in new[] { skyDir, recastDir })
            {
                try { Directory.Delete(dir, true); } catch { /* best effort */ }
            }
        }
    }
}
