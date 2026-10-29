using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.Benchmark;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.Lab;

/// <summary>
/// Builds NPC2's Recast output for the in-game check (Recast plan Phase 8) into the houseCARL Conflict
/// Lab's mod folders, through the real screening and patcher, then checks the files before any launch.
/// Opt-in: does nothing unless <c>NPC2_LAB_BUILD=1</c>, because it writes outside the repository.
///
/// <para>The measurement contract is <c>S:\Dev\houseCARL\_claude\runs\2026-10-29-npc2-recast-ingame\PLAN.md</c>;
/// the cases are in <c>S:\Dev\houseCARL\_claude\harness\npc2recast\cases.json</c>. Per arm it writes
/// <c>build\&lt;arm&gt;\manifest.json</c> (resolved donors, screening rejections, file checks) and the
/// patcher log beside the plan.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class LabOutputBuilder
{
    private const string Lab = @"X:\Skyrim Conflict Lab";
    private const string RunDir = @"S:\Dev\houseCARL\_claude\runs\2026-10-29-npc2-recast-ingame";
    private const string OutputName = "NPC2Lab";
    private static readonly ModKey LabPlugin = ModKey.FromFileName("FaceSwapLab.esp");
    private static readonly ModKey ReplacerPlugin = ModKey.FromFileName("LabReplacer.esp");
    private static string Mods(string name) => Path.Combine(Lab, "mods", name);
    private static FormKey LabNpc(string hex) => new(LabPlugin, Convert.ToUInt32(hex, 16));

    // FaceSwapLab.esp forms (harness/npc2recast/cases.json)
    private static readonly FormKey TP01 = LabNpc("800"), TP02 = LabNpc("801"), TP03 = LabNpc("802"),
        TP04 = LabNpc("803"), TP05 = LabNpc("804"), TP06 = LabNpc("805"), TP07 = LabNpc("806"),
        TP08 = LabNpc("807"), TP09 = LabNpc("808"), TP10 = LabNpc("809"), TT01 = LabNpc("80B"),
        TS01 = LabNpc("80D"), SrcTemplated = LabNpc("810");

    private readonly ITestOutputHelper _output;
    public LabOutputBuilder(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task BuildRecastLabOutputs()
    {
        if (Environment.GetEnvironmentVariable("NPC2_LAB_BUILD") != "1")
        {
            _output.WriteLine("SKIPPED: set NPC2_LAB_BUILD=1 to build the Conflict Lab outputs.");
            return;
        }

        var replacerToml = BuildLabReplacer();

        var hybrid = await BuildArm("Hybrid", "NPC2Recast-Hybrid", "NPC2 Output - Hybrid", RecastMode.Hybrid, replacerToml);
        var only = await BuildArm("Only", "NPC2Recast-Only", "NPC2 Output - RecastOnly", RecastMode.RecastOnly, replacerToml);
        var measure = BuildMeasure();

        // The replacer's own patch must not reach the game: NPC2 delivers its donor now.
        File.Move(replacerToml, replacerToml + ".disabled", overwrite: true);

        foreach (var (arm, manifest) in new[] { ("Hybrid", hybrid), ("Only", only), ("Measure", measure) })
        {
            var dir = Path.Combine(RunDir, "build", arm);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    // ------------------------------------------------------------------ the replacer pack (case C8)

    /// <summary>
    /// "Lab Replacer": a Recast replacer pack the way third parties ship one — its own plugin carrying the
    /// donor (a copy of FaceSwapLab's TS01, a scarred male Nord), the donor's FaceGen, and a patch that
    /// points TP07 at it. Returns the patch path (still enabled, for the import).
    /// </summary>
    private string BuildLabReplacer()
    {
        var folder = Mods("Lab Replacer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "meta.ini"), "[General]\ngameName=skyrimse\nmodid=0\nversion=1.0\n");

        using var lab = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(Mods("houseCARL - FaceSwapLab"), "FaceSwapLab.esp"),
            SkyrimRelease.SkyrimSE);
        var source = lab.Npcs.First(n => n.FormKey == TS01);

        var mod = new SkyrimMod(ReplacerPlugin, SkyrimRelease.SkyrimSE);
        var donor = mod.Npcs.AddNew();
        donor.DeepCopyIn(source, out _);
        donor.EditorID = "LabReplacer_Donor";
        donor.EnumerateFormLinks().Select(l => l.FormKey.ModKey).Distinct()
            .Should().OnlyContain(k => k == Implicits.Get(GameRelease.SkyrimSE).BaseMasters.First() || k == ReplacerPlugin || k.IsNull,
                "the donor copy must reference only Skyrim.esm, so the pack needs no lab master");
        mod.BeginWrite.ToPath(Path.Combine(folder, ReplacerPlugin.FileName)).WithLoadOrderFromHeaderMasters()
            .WithNoDataFolder().Write();

        foreach (var (kind, ext) in new[] { ("meshes", "FaceGeom", "nif"), ("textures", "FaceTint", "dds") }
                     .Select(t => ((t.Item1, t.Item2), t.Item3)))
        {
            var from = Path.Combine(Mods("houseCARL - FaceSwapLab"), kind.Item1, "actors", "character", "FaceGenData",
                kind.Item2, LabPlugin.FileName, $"{TS01.ID:X8}.{ext}");
            var to = Path.Combine(folder, kind.Item1, "actors", "character", "FaceGenData", kind.Item2,
                ReplacerPlugin.FileName, $"{donor.FormKey.ID:X8}.{ext}");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
        }

        var toml = Path.Combine(folder, "SKSE", "Plugins", "Recast", "Patches", "Lab Replacer", "replacer.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(toml)!);
        if (File.Exists(toml + ".disabled")) File.Delete(toml + ".disabled");
        File.WriteAllText(toml,
            "[manifest]\nname = \"Lab Replacer\"\npriority = 50\napi_version = 1\n\n" +
            $"[[npcs]]\ntarget = \"{RecastInterface.FormatSelector(TP07)}\"\nface = \"{RecastInterface.FormatSelector(donor.FormKey)}\"\n",
            new UTF8Encoding(false));
        return toml;
    }

    // ------------------------------------------------------------------ one arm

    private async Task<Dictionary<string, object?>> BuildArm(string arm, string profile, string outMod, RecastMode mode,
        string replacerToml)
    {
        Mo2ProfileEnvironment.TryBuild(Lab, profile, OutputName, out var env, out var failure)
            .Should().BeTrue(failure);
        var cache = env!.Provider.LinkCache!;

        var baseGame = new Dictionary<string, INpcGetter>(StringComparer.OrdinalIgnoreCase);
        foreach (var edid in new[] { "Torvar", "Muiri", "RevynSadri", "Kharjo", "Hadvar" })
        {
            baseGame[edid] = cache.PriorityOrder.WinningOverrides<INpcGetter>()
                .Single(n => string.Equals(n.EditorID, edid, StringComparison.OrdinalIgnoreCase) &&
                             n.FormKey.ModKey.FileName.String.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase));
        }

        // C8: the pair comes from NPC2's own replacer import, not from this file.
        var vm = Reflect.Uninitialized<VM_ModSetting>();
        Reflect.SetField(vm, "<CorrespondingFolderPaths>k__BackingField",
            new System.Collections.ObjectModel.ObservableCollection<string>(new[] { Mods("Lab Replacer") }));
        Reflect.SetField(vm, "<DisplayName>k__BackingField", "Lab Replacer");
        Reflect.SetField(vm, "<NpcFormKeysToDisplayName>k__BackingField", new Dictionary<FormKey, string>());
        var imports = await vm.GetReplacerImportsAsync(new Dictionary<string, HashSet<FormKey>>(),
            new Dictionary<string, HashSet<FormKey>>());
        File.Exists(replacerToml).Should().BeTrue();
        imports.Should().ContainSingle("the pack names one face");
        var (importTarget, importDonor, _, _) = imports[0];
        importTarget.Should().Be(TP07);

        var selections = new Dictionary<FormKey, (string ModName, FormKey NpcFormKey)>
        {
            [TP02] = ("Base Game", baseGame["Torvar"].FormKey),
            [TP03] = ("Base Game", baseGame["Muiri"].FormKey),
            [TP04] = ("Base Game", baseGame["RevynSadri"].FormKey),
            [TP05] = ("Base Game", baseGame["Kharjo"].FormKey),
            [TT01] = ("Base Game", baseGame["Muiri"].FormKey),
            [TP01] = ("FaceSwapLab", SrcTemplated),
            [TP06] = ("Base Game", baseGame["Hadvar"].FormKey),
            [importTarget] = ("Lab Replacer", importDonor),
        };

        var outDir = Mods(outMod);
        var runs = new List<object>();
        if (mode == RecastMode.Hybrid)
        {
            // C9: a SkyPatcher run into the same folder first, then Recast without cleaning it.
            var sky = await Run(env, Settings(outDir, OutputBackend.SkyPatcher, mode, selections));
            File.ReadAllText(Path.Combine(outDir, SkyPatcherIniComparer.DefaultIniRelativePath.Replace("NPC.ini", OutputName + ".ini")))
                .Should().Contain("copyVisualStyle", "the switch case needs a SkyPatcher appearance ini to sweep");
            runs.Add(new { backend = "SkyPatcher (switch baseline)", sky.PatchedTargets.Count, sky.InvalidSelections });
            File.WriteAllText(Path.Combine(RunDir, "build", $"{arm}-skypatcher-log.txt"), sky.Log);
        }

        var run = await Run(env, Settings(outDir, OutputBackend.Recast, mode, selections));
        Directory.CreateDirectory(Path.Combine(RunDir, "build"));
        File.WriteAllText(Path.Combine(RunDir, "build", $"{arm}-log.txt"), run.Log);
        File.WriteAllText(Path.Combine(outDir, "meta.ini"), "[General]\ngameName=skyrimse\nmodid=0\nversion=1.0\n");
        runs.Add(new { backend = $"Recast {mode}", run.PatchedTargets.Count, run.InvalidSelections });

        // ---- file-level checks (PLAN §3): refuse the launch on any failure ----
        var toml = RecastPatchReader.ReadFile(Path.Combine(RecastInterface.GetOutputDir(outDir), OutputName + ".toml"));
        toml.IsLoaded.Should().BeTrue(string.Join("; ", toml.Errors));
        var faces = toml.Faces.ToDictionary(f => f.Target.FormKey!.Value, f => f.Source.FormKey!.Value);
        var rejected = selections.Keys.Where(k => run.InvalidSelections.Any(s => s.Contains(k.ToString(), StringComparison.OrdinalIgnoreCase))).ToHashSet();
        faces.Keys.Should().BeEquivalentTo(selections.Keys.Except(rejected), "every screened selection gets exactly one face");

        var ini = Path.Combine(outDir, "SKSE", "Plugins", "SkyPatcher", "npc", "NPC Plugin Chooser", OutputName + ".ini");
        var iniText = File.Exists(ini) ? File.ReadAllText(ini) : null;
        if (mode == RecastMode.Hybrid)
        {
            rejected.Should().BeEmpty("Hybrid can deliver race, so it rejects nothing here");
            if (iniText != null)
            {
                foreach (var key in new[] { "copyVisualStyle", "skin=", "height=", "weight=", "Flags=female", "TemplateFlags" })
                    iniText.Should().NotContain(key, "Hybrid's ini carries no appearance directive (and the switch swept the old one)");
            }
        }
        else
        {
            rejected.Should().Contain(TP05, "a Khajiit donor cannot be drawn on a Nord body");
            iniText.Should().BeNull("RecastOnly writes no SkyPatcher ini");
        }

        var cases = new Dictionary<string, object?>();
        foreach (var (target, (modName, donorFk)) in selections)
        {
            var donor = modName == "Lab Replacer"
                ? SkyrimMod.CreateFromBinaryOverlay(Path.Combine(Mods("Lab Replacer"), ReplacerPlugin.FileName), SkyrimRelease.SkyrimSE)
                    .Npcs.First(n => n.FormKey == donorFk)
                : cache.Resolve<INpcGetter>(donorFk);
            cases[target.ToString()] = new
            {
                mod = modName, donor = donorFk.ToString(), donorEditorId = donor.EditorID,
                donorRace = cache.TryResolve<IRaceGetter>(donor.Race.FormKey, out var r) ? r.EditorID : donor.Race.FormKey.ToString(),
                donorFemale = Auxilliary.IsFemale(donor),
                face = faces.TryGetValue(target, out var surrogate) ? surrogate.ToString() : null,
                rejected = run.InvalidSelections.FirstOrDefault(s => s.Contains(target.ToString(), StringComparison.OrdinalIgnoreCase)),
                iniDirectives = iniText?.Split('\n').FirstOrDefault(l => l.Contains(SkyPatcherInterface.FormatFormKeyForSkyPatcher(target) + ":")),
            };
        }

        _output.WriteLine($"[{arm}] faces {faces.Count}, rejected {rejected.Count}");
        return new Dictionary<string, object?>
        {
            ["arm"] = arm, ["profile"] = profile, ["outputMod"] = outMod, ["runs"] = runs, ["cases"] = cases,
            ["tomlFile"] = toml.Path, ["iniPresent"] = iniText != null,
        };
    }

    private static async Task<GoldenPatchResult> Run(Mo2ProfileEnvironment env, Settings settings)
    {
        env.Reapply();
        return await GoldenPatchRunner.RunAsync(env.Provider, settings);
    }

    private static Settings Settings(string outDir, OutputBackend backend, RecastMode mode,
        Dictionary<FormKey, (string ModName, FormKey NpcFormKey)> selections) => new()
    {
        SkyrimRelease = SkyrimRelease.SkyrimSE,
        OutputPluginName = OutputName,
        OutputDirectory = outDir,
        AppendTimestampToOutputDirectory = false,
        ModsFolder = string.Empty,
        SplitOutput = false,
        AutoEslIfy = true,
        AutoSplitOutput = false,
        PatchingMode = PatchingMode.CreateAndPatch,
        OutputBackend = backend,
        RecastMode = mode,
        DefaultRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
        DefaultMaxNestedIntervalDepth = 2,
        DefaultIncludeAllOverrides = false,
        BatFilePreCommands = string.Empty,
        BatFilePostCommands = string.Empty,
        SelectedAppearanceMods = new(selections),
        NpcOutfitOverrides = new() { [TP06] = OutfitOverride.Yes },   // C7
        ModSettings = new List<ModSetting>
        {
            new()
            {
                DisplayName = "Base Game", IsAutoGenerated = true, OverwriteBaseGameAssets = false,
                CorrespondingModKeys = Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToList(),
                CorrespondingFolderPaths = new List<string>(), MergeInDependencyRecords = false, CopyAssets = true,
                ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
            },
            new()
            {
                DisplayName = "FaceSwapLab", CorrespondingModKeys = new List<ModKey> { LabPlugin },
                CorrespondingFolderPaths = new List<string> { Mods("houseCARL - FaceSwapLab") },
                MergeInDependencyRecords = true, CopyAssets = true,
                ModTemplateHandlingMode = TemplateHandlingMode.GiveEachNpcOwnCopy,   // C6
            },
            new()
            {
                DisplayName = "Lab Replacer", CorrespondingModKeys = new List<ModKey> { ReplacerPlugin },
                CorrespondingFolderPaths = new List<string> { Mods("Lab Replacer") },
                MergeInDependencyRecords = true, CopyAssets = true,
            },
        },
    };

    // ------------------------------------------------------------------ the head-part measurement (question 3)

    /// <summary>
    /// Hand-written Recast patch (not NPC2 output): vanilla donors of other races onto Nord targets, no
    /// race change, so the engine has to draw head parts whose ValidRaces may not list NordRace.
    /// </summary>
    private Dictionary<string, object?> BuildMeasure()
    {
        Mo2ProfileEnvironment.TryBuild(Lab, "NPC2Recast-Measure", OutputName, out var env, out var failure)
            .Should().BeTrue(failure);
        var cache = env!.Provider.LinkCache!;
        var skyrimNpcs = cache.PriorityOrder.WinningOverrides<INpcGetter>()
            .Where(n => n.FormKey.ModKey.FileName.String.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)).ToList();

        INpcGetter ByEditorId(params string[] edids) =>
            edids.Select(e => skyrimNpcs.FirstOrDefault(n => string.Equals(n.EditorID, e, StringComparison.OrdinalIgnoreCase)))
                .First(n => n != null)!;

        var rows = new[]
        {
            ("M1", TP08, ByEditorId("Ghorbash", "Yamarz", "Larak", "Burguk")),
            ("M2", TP09, ByEditorId("Kharjo")),
            ("M3", TP10, ByEditorId("Belethor")),
        };

        var sb = new StringBuilder("# Phase 8 head-part measurement: hand-written, not NPC2 output.\n" +
                                   "[manifest]\nname = \"NPC2 Measure\"\npriority = 1000\napi_version = 1\n");
        var cases = new Dictionary<string, object?>();
        foreach (var (id, target, donor) in rows)
        {
            sb.Append($"\n# {id} {donor.EditorID}\n[[npcs]]\ntarget = \"{RecastInterface.FormatSelector(target)}\"\n" +
                      $"face = \"{RecastInterface.FormatSelector(donor.FormKey)}\"\n");
            var parts = donor.HeadParts.Select(hp =>
            {
                var part = cache.Resolve<IHeadPartGetter>(hp.FormKey);
                var races = part.ValidRaces.IsNull || !cache.TryResolve<IFormListGetter>(part.ValidRaces.FormKey, out var list)
                    ? new List<string?>()
                    : list.Items.Select(i => cache.TryResolve<IRaceGetter>(i.FormKey, out var rr) ? rr.EditorID : i.FormKey.ToString()).ToList();
                return new { formKey = hp.FormKey.ToString(), part.EditorID, type = part.Type?.ToString(),
                             validRacesList = part.ValidRaces.FormKey.ToString(), nordValid = races.Contains("NordRace"), validRaces = races };
            }).ToList();
            cases[id] = new
            {
                target = target.ToString(), donor = donor.FormKey.ToString(), donorEditorId = donor.EditorID,
                donorRace = cache.TryResolve<IRaceGetter>(donor.Race.FormKey, out var r) ? r.EditorID : null,
                donorFemale = Auxilliary.IsFemale(donor), headParts = parts,
            };
        }

        var toml = Path.Combine(Mods("NPC2 Measure - ValidRaces"), "SKSE", "Plugins", "Recast", "Patches", "NPC2 Measure", "measure.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(toml)!);
        File.WriteAllText(toml, sb.ToString(), new UTF8Encoding(false));
        RecastPatchReader.ReadFile(toml).Faces.Should().HaveCount(3);
        return new Dictionary<string, object?> { ["arm"] = "Measure", ["tomlFile"] = toml, ["cases"] = cases };
    }
}
