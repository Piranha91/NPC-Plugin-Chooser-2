using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.Benchmark;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Lab;

/// <summary>
/// Builds one arm of the in-game pre-release check: NPC2 output for a staged <c>Settings.json</c>,
/// through the real screening and patcher, into a mod folder of an MO2 instance. The matrix itself
/// (which mods, which modes, which NPCs) lives in the houseCARL harness
/// (<c>_claude/harness/npc2bench/bench_def.py</c>), which stages the settings from NPC2's own
/// persisted mod analysis — so wig and antler detections are the app's, not a transcription.
///
/// <para>Opt-in: does nothing unless <c>NPC2_BENCH_SPEC</c> names an arm spec file, because it
/// writes outside the repository. The spec is
/// <c>{ "instance", "profile", "settingsPath", "manifestPath" }</c>; the output folder and plugin
/// name come from the staged settings. Beside the output it writes a manifest: screening verdicts,
/// the files produced, and — read from the SOURCE mod, never from the output — each donor's head
/// parts, skin armatures and outfit pieces, with the ones the mod's analysis calls wig or antler
/// marked. The in-game scorer predicts from those.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class BenchOutputBuilder
{
    private const string OutputFolderPrefix = "NPC2Bench Output";

    private sealed class Spec
    {
        public string Instance { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
        public string SettingsPath { get; set; } = string.Empty;
        public string ManifestPath { get; set; } = string.Empty;
    }

    private readonly ITestOutputHelper _output;
    public BenchOutputBuilder(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task BuildBenchArm()
    {
        var specPath = Environment.GetEnvironmentVariable("NPC2_BENCH_SPEC");
        if (string.IsNullOrWhiteSpace(specPath))
        {
            _output.WriteLine("SKIPPED: set NPC2_BENCH_SPEC=<arm spec json> to build an in-game bench arm.");
            return;
        }

        var spec = JsonSerializer.Deserialize<Spec>(File.ReadAllText(specPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var settings = JSONhandler<Settings>.LoadJSONFile(spec.SettingsPath, out var loaded, out var loadError);
        loaded.Should().BeTrue(loadError);

        // This test deletes and rewrites its output folder: only ever one of the bench's own.
        var outDir = Path.GetFullPath(settings!.OutputDirectory);
        var modsRoot = Path.GetFullPath(Path.Combine(spec.Instance, "mods")) + Path.DirectorySeparatorChar;
        outDir.StartsWith(modsRoot, StringComparison.OrdinalIgnoreCase).Should().BeTrue("the output must be a mod folder of the instance");
        Path.GetFileName(outDir).StartsWith(OutputFolderPrefix, StringComparison.Ordinal).Should()
            .BeTrue($"the output folder name must start with '{OutputFolderPrefix}'");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        Mo2ProfileEnvironment.TryBuild(spec.Instance, spec.Profile, settings.OutputPluginName, out var env, out var failure)
            .Should().BeTrue(failure);
        env!.UnresolvedPlugins.Should().BeEmpty("every active plugin of the bench profile must exist");
        env.Reapply();

        var sourceFacts = ReadSourceFacts(settings);

        // The app applies "Log Record Provenance" in App.xaml.cs; headless, nothing does. Honour the
        // staged setting here and keep the CSV beside the manifest (it is written next to the test
        // assembly, where the next build would overwrite it).
        var provenanceCsv = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RecordProvenance.csv");
        if (settings.LogRecordProvenance && File.Exists(provenanceCsv)) File.Delete(provenanceCsv);
        RecordProvenanceDiag.SetEnabled(settings.LogRecordProvenance);
        GoldenPatchResult run;
        try
        {
            // Through MO2 the Data folder is virtual and holds every enabled mod's plugins; headless
            // it is the real one. Without this, an override whose original lives in another mod's
            // folder (Auri's outfit, from 018Auri.esp) is never delta-patched: the build would test
            // the whole-copy fallback instead of the patching. NPC2_BENCH_REAL_DATA_ONLY=1 turns it
            // off, to reproduce the builds made before 2026-11-25.
            //
            // The same goes for RaceMenu, which a converted wig's hair color follows under
            // WigHairTintMode.Auto: its DLL and ini live in a mod folder, so the detector is told
            // where the profile's files really are. A profile without RaceMenu enabled reads as
            // not installed, as it would in game.
            var realDataOnly = Environment.GetEnvironmentVariable("NPC2_BENCH_REAL_DATA_ONLY") == "1";
            run = await GoldenPatchRunner.RunAsync(env.Provider, settings, configure: realDataOnly
                ? null
                : h =>
                {
                    h.PluginProvider.VirtualDataPluginPaths = env.VirtualDataPlugins;
                    h.RaceMenuDetector.DataRelativeFileResolver = env.ResolveVirtualDataFile;
                });
        }
        finally
        {
            RecordProvenanceDiag.SetEnabled(false);
        }
        if (settings.LogRecordProvenance && File.Exists(provenanceCsv))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(spec.ManifestPath)!);
            File.Copy(provenanceCsv, Path.ChangeExtension(spec.ManifestPath, ".provenance.csv"), overwrite: true);
        }
        File.WriteAllText(Path.Combine(outDir, "meta.ini"), "[General]\ngameName=skyrimse\nmodid=0\nversion=1.0\n");

        var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(outDir, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var manifest = new Dictionary<string, object?>
        {
            ["profile"] = spec.Profile,
            ["outputDirectory"] = outDir,
            ["outputPluginName"] = settings.OutputPluginName,
            ["backend"] = settings.OutputBackend.ToString(),
            ["recastMode"] = settings.RecastMode.ToString(),
            ["patchingMode"] = settings.PatchingMode.ToString(),
            ["loadOrder"] = env.ResolvedPlugins.Select(p => p.Key.FileName.String).ToList(),
            ["selected"] = settings.SelectedAppearanceMods.Count,
            ["patchedTargets"] = run.PatchedTargets.Select(k => k.ToString()).ToList(),
            ["invalidSelections"] = run.InvalidSelections,
            ["plugins"] = files.Where(f => !f.Contains('/') && (f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                                                                f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                                                                f.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))).ToList(),
            ["fileCount"] = files.Count,
            ["files"] = files,
            ["sourceFacts"] = sourceFacts,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(spec.ManifestPath)!);
        File.WriteAllText(spec.ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.ChangeExtension(spec.ManifestPath, ".log.txt"), run.Log);

        _output.WriteLine($"selected {settings.SelectedAppearanceMods.Count}, patched {run.PatchedTargets.Count}, " +
                          $"rejected {run.InvalidSelections.Count}, files {files.Count}");
        run.PatchedTargets.Count.Should().BeGreaterThan(0, "an arm that patches nothing has nothing to launch");
    }

    /// <summary>
    /// What each selected donor carries in its SOURCE plugin: head parts, the armatures of its skin,
    /// and the pieces of its default outfit, by EditorID, each marked wig or antler where the mod's
    /// persisted analysis (<see cref="ModSetting"/>'s Detected* sets) says so. Read straight from the
    /// mod's plugins in its own folders; records the mod does not carry are resolved from the game's
    /// Data folder masters where possible and otherwise reported by FormKey only.
    /// </summary>
    private Dictionary<string, object?> ReadSourceFacts(Settings settings)
    {
        var facts = new Dictionary<string, object?>();
        var pluginCache = new Dictionary<string, ISkyrimModGetter?>(StringComparer.OrdinalIgnoreCase);

        ISkyrimModGetter? Load(ModSetting mod, ModKey key)
        {
            foreach (var folder in Enumerable.Reverse(mod.CorrespondingFolderPaths))
            {
                var path = Path.Combine(folder, key.FileName);
                if (pluginCache.TryGetValue(path, out var cached)) return cached;
                if (!File.Exists(path)) continue;
                return pluginCache[path] = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            }
            return null;
        }

        foreach (var (target, (modName, donor)) in settings.SelectedAppearanceMods)
        {
            var mod = settings.ModSettings.FirstOrDefault(m => m.DisplayName == modName);
            if (mod == null || mod.IsFaceGenOnlyEntry && mod.CorrespondingModKeys.Count == 0)
            {
                facts[target.ToString()] = new { mod = modName, donor = donor.ToString(), plugin = (string?)null };
                continue;
            }

            var plugins = mod.CorrespondingModKeys.Select(k => (Key: k, Mod: Load(mod, k))).Where(p => p.Mod != null).ToList();
            // The last plugin of the mod that carries the NPC is the mod's own winning record.
            INpcGetter? npc = null;
            string? from = null;
            foreach (var (key, plugin) in plugins)
            {
                var hit = plugin!.Npcs.FirstOrDefault(n => n.FormKey == donor);
                if (hit != null) { npc = hit; from = key.FileName; }
            }
            if (npc == null)
            {
                facts[target.ToString()] = new { mod = modName, donor = donor.ToString(), plugin = (string?)null };
                continue;
            }

            string? EditorIdOf<T>(FormKey key, Func<ISkyrimModGetter, IEnumerable<T>> group) where T : class, IMajorRecordGetter
            {
                foreach (var (_, plugin) in Enumerable.Reverse(plugins))
                {
                    var hit = group(plugin!).FirstOrDefault(r => r.FormKey == key);
                    if (hit != null) return hit.EditorID;
                }
                return null;
            }
            T? RecordOf<T>(FormKey key, Func<ISkyrimModGetter, IEnumerable<T>> group) where T : class, IMajorRecordGetter
            {
                foreach (var (_, plugin) in Enumerable.Reverse(plugins))
                {
                    var hit = group(plugin!).FirstOrDefault(r => r.FormKey == key);
                    if (hit != null) return hit;
                }
                return null;
            }

            var headParts = npc.HeadParts.Select(hp => new
            {
                formKey = hp.FormKey.ToString(),
                editorId = EditorIdOf<IHeadPartGetter>(hp.FormKey, m => m.HeadParts),
                antler = mod.DetectedAntlerHeadParts.Contains(hp.FormKey),
            }).ToList();

            var skin = npc.WornArmor.IsNull ? null : RecordOf<IArmorGetter>(npc.WornArmor.FormKey, m => m.Armors);
            var skinAddons = skin?.Armature.Select(a => new
            {
                formKey = a.FormKey.ToString(),
                editorId = EditorIdOf<IArmorAddonGetter>(a.FormKey, m => m.ArmorAddons),
                wig = mod.DetectedWigArmatures.Contains(a.FormKey),
                antler = mod.DetectedAntlerArmatures.Contains(a.FormKey),
            }).ToList();

            var outfit = npc.DefaultOutfit.IsNull ? null : RecordOf<IOutfitGetter>(npc.DefaultOutfit.FormKey, m => m.Outfits);
            var outfitItems = outfit?.Items?.Select(i => new
            {
                formKey = i.FormKey.ToString(),
                editorId = EditorIdOf<IArmorGetter>(i.FormKey, m => m.Armors),
                wig = mod.DetectedWigArmors.Contains(i.FormKey),
                antler = mod.DetectedAntlerArmors.Contains(i.FormKey),
            }).ToList();

            facts[target.ToString()] = new
            {
                mod = modName, donor = donor.ToString(), plugin = from, donorEditorId = npc.EditorID,
                race = npc.Race.FormKey.ToString(),
                female = Auxilliary.IsFemale(npc),
                templated = !npc.Template.IsNull,
                wornArmor = npc.WornArmor.IsNull ? null : npc.WornArmor.FormKey.ToString(),
                wornArmorInMod = skin != null,
                defaultOutfit = npc.DefaultOutfit.IsNull ? null : npc.DefaultOutfit.FormKey.ToString(),
                defaultOutfitInMod = outfit != null,
                headParts, skinAddons, outfitItems,
                hasWigSource = mod.NpcWigSources.ContainsKey(donor),
            };
        }
        return facts;
    }
}
