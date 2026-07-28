using System.IO;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// WHICH plugin's copy of the terminus a flatten reads (Template Handling Mode = give each NPC its
/// own copy). The main matrix cannot answer this: no fixture mod there overrides both a templated
/// specimen AND its terminus, so every terminus lookup falls through to the load order either way.
///
/// <para>The shape this pins is the common real one — a big NPC overhaul overrides a templated NPC
/// and the generic actor it templates from, while some unrelated later plugin also touches that
/// generic actor and wins the load order. The flatten writes the terminus's head parts onto the
/// NPC's own record, and the ladder copies the terminus's FaceGen mesh from the SELECTED MOD to
/// the NPC's own FormID. Read the record from the load-order winner instead and the output pairs
/// one plugin's mesh with another plugin's head parts — the dark-face bug, by the app's own
/// <see cref="FaceGenConsistencyAnalyzer"/> rule.</para>
///
/// <para>Field values separate all three possible outcomes: the mod's terminus (correct), the
/// winner's terminus (the regression), and the donor's own inert data (no flatten at all).</para>
///
/// <para>Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class TemplateTerminusSourceTests
{
    private readonly ITestOutputHelper _output;

    public TemplateTerminusSourceTests(ITestOutputHelper output) => _output = output;

    private static readonly ModKey[] BaseMasters =
        Implicits.Get(GameRelease.SkyrimSE).BaseMasters.ToArray();

    private static bool IsVanillaOrCc(ModKey key)
    {
        if (BaseMasters.Contains(key)) return true;
        var fn = key.FileName.String;
        return fn.StartsWith("cc", StringComparison.OrdinalIgnoreCase)
               || fn.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
    }

    private const string ModName = "Terminus Source Fixture Mod";

    // One height per (plugin, record) so a single field names the source of the flattened appearance.
    private const float BaseHeight = 1.00f;
    private const float ModTemplatedHeight = 1.10f;   // the donor's own (inert) data
    private const float ModTerminusHeight = 1.20f;    // CORRECT: the selected mod's terminus
    private const float WinnerTerminusHeight = 1.30f; // REGRESSION: the load-order winner's terminus

    private const string EidTemplated = "NPC2Term_Templated";
    private const string EidTerminus = "NPC2Term_Terminus";
    private const string HpBase = "NPC2Term_HP_Base";
    private const string HpModTemplated = "NPC2Term_HP_ModTemplated";
    private const string HpModTerminus = "NPC2Term_HP_ModTerminus";
    private const string HpWinnerTerminus = "NPC2Term_HP_WinnerTerminus";

    [Theory]
    [InlineData(PatchingMode.CreateAndPatch)]
    [InlineData(PatchingMode.Create)]
    public async Task Flatten_ReadsTheTerminusFromTheSelectedMod_NotTheLoadOrderWinner(
        PatchingMode patchingMode)
    {
        var root = Path.Combine(Path.GetTempPath(), "NpcTermSrc_" + Guid.NewGuid().ToString("N"));
        var modFolder = Path.Combine(root, "Mod");
        var outDir = Path.Combine(root, "Output");
        var envOutDir = Path.Combine(root, "EnvOutput");

        try
        {
            // --- 1. Author base / mod / winner plugins ---------------------------------------
            var baseKey = ModKey.FromFileName("NPC2TermBase.esp");
            var modKey = ModKey.FromFileName("NPC2TermMod.esp");
            var winnerKey = ModKey.FromFileName("NPC2TermWinner.esp");
            var writeOrder = BaseMasters.Concat(new[] { baseKey, modKey, winnerKey }).ToArray();

            var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);

            // Every head part lives in the base plugin, which stays in the load order as a master of
            // the output, so the written links are the fixture's own FormKeys and need no remapping.
            HeadPart NewHeadPart(string editorId)
            {
                var hp = baseMod.HeadParts.AddNew();
                hp.EditorID = editorId;
                hp.Type = HeadPart.TypeEnum.Face;
                return hp;
            }

            var hpBase = NewHeadPart(HpBase);
            var hpModTemplated = NewHeadPart(HpModTemplated);
            var hpModTerminus = NewHeadPart(HpModTerminus);
            var hpWinnerTerminus = NewHeadPart(HpWinnerTerminus);

            Npc Plain(string editorId)
            {
                var n = baseMod.Npcs.AddNew();
                n.EditorID = editorId;
                n.Name = editorId;
                n.Race.SetTo(Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Race.NordRace);
                n.Height = BaseHeight;
                n.HeadParts.Add(hpBase.FormKey);
                return n;
            }

            var terminus = Plain(EidTerminus);
            var templated = Plain(EidTemplated);
            templated.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
            templated.Template.SetTo(terminus.FormKey);

            var templatedKey = templated.FormKey;
            var terminusKey = terminus.FormKey;

            var basePath = Path.Combine(root, "Base", baseKey.FileName);
            WriteMod(baseMod, basePath, writeOrder);

            // The selected appearance mod overrides BOTH records — the shape the main matrix lacks.
            var appearanceMod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            var modTemplated = appearanceMod.Npcs.GetOrAddAsOverride(templated);
            modTemplated.Height = ModTemplatedHeight;
            modTemplated.HeadParts.Clear();
            modTemplated.HeadParts.Add(hpModTemplated.FormKey);
            var modTerminus = appearanceMod.Npcs.GetOrAddAsOverride(terminus);
            modTerminus.Height = ModTerminusHeight;
            modTerminus.HeadParts.Clear();
            modTerminus.HeadParts.Add(hpModTerminus.FormKey);

            var modPath = Path.Combine(modFolder, modKey.FileName);
            WriteMod(appearanceMod, modPath, writeOrder);

            // An unrelated later plugin that also edits the terminus and wins the load order.
            var winnerMod = new SkyrimMod(winnerKey, SkyrimRelease.SkyrimSE);
            var winnerTerminus = winnerMod.Npcs.GetOrAddAsOverride(terminus);
            winnerTerminus.Height = WinnerTerminusHeight;
            winnerTerminus.HeadParts.Clear();
            winnerTerminus.HeadParts.Add(hpWinnerTerminus.FormKey);

            var winnerPath = Path.Combine(root, "Winner", winnerKey.FileName);
            WriteMod(winnerMod, winnerPath, writeOrder);

            // --- 2. FaceGen, at the SUBJECT's path (the terminus) ------------------------------
            // Only the selected mod ships it; that is the mesh the flattened record must match.
            var (meshRel, texRel) = Auxilliary.GetFaceGenSubPathStrings(terminusKey, regularized: true);
            WriteText(Path.Combine(modFolder, meshRel), "npc2-termsrc-nif");
            WriteText(Path.Combine(modFolder, texRel), "npc2-termsrc-dds");

            // --- 3. Inject into the environment ------------------------------------------------
            var fixturePlugins = new[]
            {
                (Key: baseKey, Path: basePath),
                (Key: modKey, Path: modPath),
                (Key: winnerKey, Path: winnerPath),
            };

            IEnumerable<IModListingGetter<ISkyrimModGetter>> Transform(
                IEnumerable<IModListingGetter<ISkyrimModGetter>> input)
            {
                var listings = input.OnlyEnabledAndExisting()
                    .Where(l => IsVanillaOrCc(l.ModKey))
                    .ToList();
                foreach (var (key, path) in fixturePlugins)
                {
                    listings.Add(new ModListing<ISkyrimModGetter>(
                        key, SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE), enabled: true));
                }
                return listings;
            }

            Directory.CreateDirectory(envOutDir);
            var provider = new EnvironmentStateProvider(null);
            provider.SetEnvironmentTarget(SkyrimRelease.SkyrimSE, string.Empty, "NPC", envOutDir);
            provider.UpdateEnvironmentForTest(Transform);

            if (provider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
            {
                _output.WriteLine("SKIPPED: no valid Skyrim SE environment. " + provider.EnvironmentBuilderError);
                return;
            }

            // The premise: the load order's winner for the terminus is NOT the selected mod's copy.
            provider.LinkCache!.TryResolve<INpcGetter>(terminusKey, out var winningTerminus).Should().BeTrue();
            winningTerminus!.Height.Should().Be(WinnerTerminusHeight,
                "the fixture is only meaningful while a later plugin wins the terminus");

            // --- 4. Select the mod for the TEMPLATED NPC only -----------------------------------
            var settings = new Settings
            {
                SkyrimRelease = SkyrimRelease.SkyrimSE,
                OutputPluginName = string.Empty,
                OutputDirectory = outDir,
                AppendTimestampToOutputDirectory = false,
                ModsFolder = string.Empty,
                SplitOutput = false,
                AutoEslIfy = false,
                AutoSplitOutput = false,
                PatchingMode = patchingMode,
                UseSkyPatcherMode = false,
                TemplateHandlingMode = TemplateHandlingMode.GiveEachNpcOwnCopy,
                DefaultRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
                DefaultMaxNestedIntervalDepth = 2,
                DefaultIncludeAllOverrides = false,
                LocalizationLanguage = null,
                BatFilePreCommands = string.Empty,
                BatFilePostCommands = string.Empty,
            };
            settings.ModSettings = new List<ModSetting>
            {
                new()
                {
                    DisplayName = ModName,
                    CorrespondingModKeys = new List<ModKey> { modKey },
                    CorrespondingFolderPaths = new List<string> { modFolder },
                    MergeInDependencyRecords = false,
                    IncludeOutfits = false,
                    CopyAssets = false,
                    ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
                },
            };
            settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
            {
                [templatedKey] = (ModName, templatedKey),
            };

            // --- 5. Run the real patcher --------------------------------------------------------
            Directory.CreateDirectory(outDir);
            var run = await GoldenPatchRunner.RunAsync(provider, settings);
            foreach (var inv in run.InvalidSelections) _output.WriteLine("  INVALID: " + inv);
            _output.WriteLine("--- RUN LOG ---\n" + run.Log + "\n--- END RUN LOG ---");

            // --- 6. Read the flattened record back ----------------------------------------------
            var outPlugin = Path.Combine(outDir, "NPC.esp");
            File.Exists(outPlugin).Should().BeTrue("the patcher must write an output plugin");
            using var outHandle = SkyrimMod.CreateFromBinaryOverlay(outPlugin, SkyrimRelease.SkyrimSE);
            ISkyrimModGetter outMod = outHandle;

            // FormKeys are remapped in Create mode, so identify by EditorID.
            var patched = outMod.Npcs.SingleOrDefault(n => n.EditorID == EidTemplated);
            patched.Should().NotBeNull($"'{EidTemplated}' must reach the output plugin; output NPCs: " +
                string.Join(", ", outMod.Npcs.Select(n => $"{n.FormKey} '{n.EditorID}'")));

            _output.WriteLine($"patched height={patched!.Height} headParts=" +
                string.Join(", ", patched.HeadParts.Select(h => h.FormKey.ToString())));

            patched.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)
                .Should().BeFalse("give-each-NPC-its-own-copy clears the Traits flag");

            patched.Height.Should().Be(ModTerminusHeight,
                $"the flatten must read the terminus from '{ModName}' — {WinnerTerminusHeight} would mean " +
                "it read the load-order winner (the dark-face regression) and " +
                $"{ModTemplatedHeight} would mean it did not flatten at all");

            patched.HeadParts.Select(h => h.FormKey).Should().Equal(new[] { hpModTerminus.FormKey },
                "the head parts must come from the same plugin as the FaceGen mesh the ladder copied");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* overlay may hold a map */ }
        }
    }

    /// <summary>
    /// The flatten overlay writes the terminus's links verbatim and the caller's merge-in walker
    /// remaps them afterwards, so anything that judges those links has to run after the walker.
    /// The dangling-link check used to run inside the appearance copy, between the two: with a
    /// mod whose terminus references a RESOURCE plugin outside the load order (the ordinary shape
    /// — an overhaul's assets .esp listed under the mod but never enabled), it reported "THE
    /// OUTPUT PLUGIN CANNOT BE SAVED" for links the walker then merged, and the run saved fine.
    ///
    /// <para>Neither the appearance plugin nor its resource plugin is injected into the load
    /// order here — that is how these mods are actually configured, and it is what makes the
    /// resource link dangling until the walker merges it.</para>
    /// </summary>
    [Fact]
    public async Task Flatten_IntoAResourcePluginOutsideTheLoadOrder_MergesWithoutCryingUnsaveable()
    {
        var root = Path.Combine(Path.GetTempPath(), "NpcTermRes_" + Guid.NewGuid().ToString("N"));
        var modFolder = Path.Combine(root, "Mod");
        var outDir = Path.Combine(root, "Output");
        var envOutDir = Path.Combine(root, "EnvOutput");

        try
        {
            var baseKey = ModKey.FromFileName("NPC2ResBase.esp");
            var resKey = ModKey.FromFileName("NPC2ResAssets.esp");
            var modKey = ModKey.FromFileName("NPC2ResMod.esp");
            var writeOrder = BaseMasters.Concat(new[] { baseKey, resKey, modKey }).ToArray();

            // Base: the templated NPC and its terminus, both plain.
            var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
            var hpBase = baseMod.HeadParts.AddNew();
            hpBase.EditorID = HpBase;
            hpBase.Type = HeadPart.TypeEnum.Face;

            Npc Plain(string editorId)
            {
                var n = baseMod.Npcs.AddNew();
                n.EditorID = editorId;
                n.Name = editorId;
                n.Race.SetTo(Mutagen.Bethesda.FormKeys.SkyrimSE.Skyrim.Race.NordRace);
                n.Height = BaseHeight;
                n.HeadParts.Add(hpBase.FormKey);
                return n;
            }

            var terminus = Plain(EidTerminus);
            var templated = Plain(EidTemplated);
            templated.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
            templated.Template.SetTo(terminus.FormKey);
            var templatedKey = templated.FormKey;

            var basePath = Path.Combine(root, "Base", baseKey.FileName);
            WriteMod(baseMod, basePath, writeOrder);

            // Resource plugin: owns the head part the mod's records point at. Never enabled.
            var resMod = new SkyrimMod(resKey, SkyrimRelease.SkyrimSE);
            var hpRes = resMod.HeadParts.AddNew();
            hpRes.EditorID = HpModTerminus;
            hpRes.Type = HeadPart.TypeEnum.Face;
            var resPath = Path.Combine(modFolder, resKey.FileName);
            WriteMod(resMod, resPath, writeOrder);

            // Appearance mod: overrides both records, both pointing into the resource plugin.
            var appearanceMod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            foreach (var src in new[] { templated, terminus })
            {
                var ovr = appearanceMod.Npcs.GetOrAddAsOverride(src);
                ovr.Height = src == terminus ? ModTerminusHeight : ModTemplatedHeight;
                ovr.HeadParts.Clear();
                ovr.HeadParts.Add(hpRes.FormKey);
            }

            var modPath = Path.Combine(modFolder, modKey.FileName);
            WriteMod(appearanceMod, modPath, writeOrder);

            var (meshRel, texRel) = Auxilliary.GetFaceGenSubPathStrings(terminus.FormKey, regularized: true);
            WriteText(Path.Combine(modFolder, meshRel), "npc2-termres-nif");
            WriteText(Path.Combine(modFolder, texRel), "npc2-termres-dds");

            // Only the base plugin joins the load order — the mod is read from its folder.
            IEnumerable<IModListingGetter<ISkyrimModGetter>> Transform(
                IEnumerable<IModListingGetter<ISkyrimModGetter>> input)
            {
                var listings = input.OnlyEnabledAndExisting()
                    .Where(l => IsVanillaOrCc(l.ModKey))
                    .ToList();
                listings.Add(new ModListing<ISkyrimModGetter>(
                    baseKey, SkyrimMod.CreateFromBinaryOverlay(basePath, SkyrimRelease.SkyrimSE), enabled: true));
                return listings;
            }

            Directory.CreateDirectory(envOutDir);
            var provider = new EnvironmentStateProvider(null);
            provider.SetEnvironmentTarget(SkyrimRelease.SkyrimSE, string.Empty, "NPC", envOutDir);
            provider.UpdateEnvironmentForTest(Transform);

            if (provider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid)
            {
                _output.WriteLine("SKIPPED: no valid Skyrim SE environment. " + provider.EnvironmentBuilderError);
                return;
            }

            provider.LoadOrder!.ListedOrder.Select(l => l.ModKey).Should().NotContain(resKey,
                "the resource plugin must stay out of the load order for the link to need merging");

            var settings = new Settings
            {
                SkyrimRelease = SkyrimRelease.SkyrimSE,
                OutputPluginName = string.Empty,
                OutputDirectory = outDir,
                AppendTimestampToOutputDirectory = false,
                ModsFolder = string.Empty,
                SplitOutput = false,
                AutoEslIfy = false,
                AutoSplitOutput = false,
                PatchingMode = PatchingMode.CreateAndPatch,
                UseSkyPatcherMode = false,
                TemplateHandlingMode = TemplateHandlingMode.GiveEachNpcOwnCopy,
                DefaultRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
                DefaultMaxNestedIntervalDepth = 2,
                DefaultIncludeAllOverrides = false,
                LocalizationLanguage = null,
                BatFilePreCommands = string.Empty,
                BatFilePostCommands = string.Empty,
            };
            settings.ModSettings = new List<ModSetting>
            {
                new()
                {
                    DisplayName = ModName,
                    // Same shape as a real overhaul: assets plugin first, listed resource-only.
                    CorrespondingModKeys = new List<ModKey> { resKey, modKey },
                    ResourceOnlyModKeys = new HashSet<ModKey> { resKey },
                    CorrespondingFolderPaths = new List<string> { modFolder },
                    MergeInDependencyRecords = true,
                    IncludeOutfits = false,
                    CopyAssets = false,
                    ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
                },
            };
            settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
            {
                [templatedKey] = (ModName, templatedKey),
            };

            Directory.CreateDirectory(outDir);
            var run = await GoldenPatchRunner.RunAsync(provider, settings);
            _output.WriteLine("--- RUN LOG ---\n" + run.Log + "\n--- END RUN LOG ---");

            var outPlugin = Path.Combine(outDir, "NPC.esp");
            File.Exists(outPlugin).Should().BeTrue("the patcher must write an output plugin");
            using var outHandle = SkyrimMod.CreateFromBinaryOverlay(outPlugin, SkyrimRelease.SkyrimSE);
            ISkyrimModGetter outMod = outHandle;

            var patched = outMod.Npcs.SingleOrDefault(n => n.EditorID == EidTemplated);
            patched.Should().NotBeNull();

            patched!.HeadParts.Should().ContainSingle()
                .Which.FormKey.ModKey.Should().Be(outMod.ModKey,
                    "the merge-in walker must pull the resource plugin's head part into the output");

            outMod.ModHeader.MasterReferences.Select(m => m.Master).Should().NotContain(resKey,
                "a surviving reference to the unloaded resource plugin is what makes a plugin unsaveable");

            run.Log.Should().NotContain("CRITICAL WARNING",
                "the dangling-link check must judge the links AFTER the merge-in walker has remapped " +
                "them, not the intermediate state the flatten overlay leaves behind");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* overlay may hold a map */ }
        }
    }

    private static void WriteMod(SkyrimMod mod, string path, ModKey[] writeOrder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        mod.BeginWrite.ToPath(path).WithLoadOrder(writeOrder).WithNoDataFolder().Write();
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
