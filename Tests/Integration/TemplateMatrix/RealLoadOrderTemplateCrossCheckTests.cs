using System.IO;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// The cross-check a purely synthetic suite cannot perform on itself: the same specimen shape,
/// occurring naturally in a real load order.
///
/// <para>Two orc "Adventurer" NPCs (<c>083279</c>, <c>0C176B</c>, both Skyrim.esm) are Traits-templated
/// to <c>03DE70</c> (<c>EncBandit01Melee2HBerserkOrcM</c>), which is itself selectable — specimens #3,
/// #4 and #5 of the synthetic fixture, occurring by nature rather than by construction. If the
/// synthetic fixtures pass but this behaves differently, the fixture is wrong.</para>
///
/// <para>Reuses the golden suite's environment (vanilla + USSEP + AI Overhaul from the local
/// environment map).</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class RealLoadOrderTemplateCrossCheckTests : IClassFixture<GoldenEnvFixture>
{
    private readonly GoldenEnvFixture _env;
    private readonly ITestOutputHelper _output;

    /// <summary>Adventurer A.</summary>
    private static readonly FormKey AdventurerA = FormKey.Factory("083279:Skyrim.esm");
    /// <summary>Adventurer B — templated to the same terminus as A.</summary>
    private static readonly FormKey AdventurerB = FormKey.Factory("0C176B:Skyrim.esm");

    /// <summary>
    /// The Adventurers' IMMEDIATE template target (<c>EncBandit01Melee2HBerserkOrcM</c>). Both
    /// templated-NPC handoffs call this "the terminus", but measured against this machine's load order
    /// it is not one: it is itself Traits-templated and the chain continues past it. The terminus is
    /// therefore resolved at run time rather than hard-coded — the shape that matters (two NPCs, one
    /// shared terminus) does not depend on how many hops it takes to get there.
    /// </summary>
    private static readonly FormKey ImmediateTemplate = FormKey.Factory("03DE70:Skyrim.esm");

    public RealLoadOrderTemplateCrossCheckTests(GoldenEnvFixture env, ITestOutputHelper output)
    {
        _env = env;
        _output = output;
    }

    private bool Skip()
    {
        if (_env.Available) return false;
        _output.WriteLine("SKIPPED: " + _env.SkipReason);
        return true;
    }

    /// <summary>
    /// Validates the synthetic fixture's central shape against reality, with no appearance mods
    /// involved: two NPCs Traits-templated to one concrete terminus that is not itself templated.
    /// </summary>
    [Fact]
    public void OrcAdventurers_ShareOneResolvableTerminus()
    {
        if (Skip()) return;
        var terminus = ResolveSharedTerminus(out var describe);
        _output.WriteLine(describe);
        terminus.Should().NotBe(FormKey.Null);
    }

    /// <summary>
    /// Walks both Adventurers' chains and returns the terminus they share, asserting the shape the
    /// synthetic fixture models: both are Traits-templated, both resolve, both land on the SAME
    /// concrete NPC, and that NPC is not itself templated. Deliberately compares the two walks against
    /// each other rather than against a hard-coded FormKey — the chain's length is load-order specific,
    /// the shared-terminus property is not.
    /// </summary>
    private FormKey ResolveSharedTerminus(out string description)
    {
        var linkCache = _env.Env!.Provider.LinkCache!;
        INpcGetter? Resolve(FormKey fk) => linkCache.TryResolve<INpcGetter>(fk, out var n) ? n : null;
        bool IsLeveled(FormKey fk) => linkCache.TryResolve<ILeveledNpcGetter>(fk, out _);

        var termini = new List<FormKey>();
        var lines = new List<string>();

        foreach (var adventurer in new[] { AdventurerA, AdventurerB })
        {
            var npc = Resolve(adventurer);
            npc.Should().NotBeNull($"{adventurer} must exist in the load order");

            Auxilliary.IsValidTemplatedNpc(npc).Should().BeTrue(
                $"{adventurer} ('{npc!.EditorID}') is expected to carry the Traits template flag");

            var hops = new List<string>();
            var status = Auxilliary.TryResolveAppearanceTerminus(
                npc, Resolve, out var terminus, IsLeveled, trace: hops.Add);

            status.Should().Be(FaceGenChainStatus.Resolved,
                $"{adventurer} ('{npc.EditorID}') must reach a concrete NPC");
            termini.Add(terminus);
            lines.Add($"{adventurer} '{npc.EditorID}': {string.Join(" ", hops)}");
        }

        termini[0].Should().Be(termini[1],
            "the whole point of this specimen group is that both Adventurers share one terminus");

        var terminusRecord = Resolve(termini[0]);
        terminusRecord.Should().NotBeNull();
        Auxilliary.IsValidTemplatedNpc(terminusRecord).Should().BeFalse(
            "the terminus must render its own face, or it is not a terminus");

        // Documented for the record: the immediate target both handoffs name as "the terminus".
        var immediate = Resolve(ImmediateTemplate);
        lines.Add($"immediate template {ImmediateTemplate} '{immediate?.EditorID}' is itself templated: " +
                  Auxilliary.IsValidTemplatedNpc(immediate));
        lines.Add($"shared terminus = {termini[0]} '{terminusRecord!.EditorID}'");

        description = string.Join(Environment.NewLine + "  ", lines);
        return termini[0];
    }

    /// <summary>
    /// The decisive §3b assertion on real data. Self-skipping: it needs at least two configured
    /// appearance mods that actually ship FaceGen for the TERMINUS (that is where the ladder looks for
    /// a templated NPC's face), and a third to give the terminus a selection of its own. Add such mods
    /// — e.g. High Poly NPC Overhaul and Lawless - A Bandit Overhaul — to
    /// <c>Tests/TestData/EnvironmentMap.local.json</c> and this activates with no code change.
    /// </summary>
    [Theory]
    [InlineData(TemplateHandlingMode.InheritFromTemplate)]
    [InlineData(TemplateHandlingMode.GiveEachNpcOwnCopy)]
    public async Task OrcAdventurers_FollowTheTemplateSetting(TemplateHandlingMode templateMode)
    {
        if (Skip()) return;
        using var _ = new StaticStateGuard();

        var config = _env.Config!;
        var terminus = ResolveSharedTerminus(out var describe);
        _output.WriteLine(describe);

        var suppliers = ModsSupplyingFaceGen(config, terminus).ToList();
        _output.WriteLine("Mods shipping loose FaceGen for the terminus: " +
                          (suppliers.Count == 0 ? "(none)" : string.Join(", ", suppliers)));

        if (suppliers.Count < 2)
        {
            var (meshRel, _) = Auxilliary.GetFaceGenSubPathStrings(terminus, regularized: true);
            _output.WriteLine("SKIPPED: this check needs at least two configured appearance mods shipping " +
                              $"'{meshRel}'. Add e.g. High Poly NPC Overhaul and Lawless - A Bandit Overhaul " +
                              "to Tests/TestData/EnvironmentMap.local.json to enable it.");
            return;
        }

        // A third supplier lets the terminus hold a selection distinct from both followers', which is
        // what makes the "neither follower writes while inheriting" half meaningful.
        bool terminusHasOwnMod = suppliers.Count >= 3;
        var settings = BuildSettings(config, templateMode, suppliers, terminus, terminusHasOwnMod,
            Path.Combine(Path.GetTempPath(), "NpcOrcCrossCheck_" + Guid.NewGuid().ToString("N")));

        FaceGenLadderDiag.Reset();
        FaceGenLadderDiag.SetEnabled(true);
        GoldenPatchResult run;
        try { run = await GoldenPatchRunner.RunAsync(_env.Env!.Provider, settings); }
        finally { FaceGenLadderDiag.SetEnabled(false); }

        try
        {
            var processed = ReferenceToken.ProcessedTargets(settings.OutputDirectory);
            _output.WriteLine($"processed={processed.Count} invalid={run.InvalidSelections.Count}");
            foreach (var d in FaceGenLadderDiag.Decisions) _output.WriteLine("  ladder: " + d.LogLine);

            // Presence gate first — measured from the output, not from the screening cache.
            processed.Should().Contain(AdventurerA).And.Contain(AdventurerB);

            var hashA = FaceGenHash(settings.OutputDirectory, AdventurerA);
            var hashB = FaceGenHash(settings.OutputDirectory, AdventurerB);
            var hashTerminus = FaceGenHash(settings.OutputDirectory, terminus);
            _output.WriteLine($"A={hashA ?? "none"} B={hashB ?? "none"} terminus={hashTerminus ?? "none"}");

            if (templateMode == TemplateHandlingMode.GiveEachNpcOwnCopy)
            {
                hashA.Should().NotBeNull("under own-copy each Adventurer gets FaceGen at its own FormID path");
                hashB.Should().NotBeNull();
                hashA.Should().NotBe(hashB,
                    "the two Adventurers were given different mods, so their faces must be different files");
            }
            else if (terminusHasOwnMod)
            {
                hashA.Should().BeNull("while inheriting, anything under the Adventurer's own FormID is inert");
                hashB.Should().BeNull();
                hashTerminus.Should().NotBeNull("the terminus's own selection still writes to its own path");
            }
            else
            {
                _output.WriteLine("NOTE: only two suppliers configured, so the terminus shares a mod with a " +
                                  "follower and the inherit-half assertion is not meaningful; skipped.");
            }
        }
        finally
        {
            try { Directory.Delete(settings.OutputDirectory, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Configured appearance mods with a loose FaceGen mesh for the given NPC, in config order.</summary>
    private static IEnumerable<string> ModsSupplyingFaceGen(GoldenOutputConfig config, FormKey subject)
    {
        var (meshRel, _) = Auxilliary.GetFaceGenSubPathStrings(subject, regularized: true);
        foreach (var (name, entry) in config.AppearanceMods)
        {
            if (entry.Folders.Any(f => File.Exists(Path.Combine(f, meshRel)))) yield return name;
        }
    }

    private static Settings BuildSettings(GoldenOutputConfig config, TemplateHandlingMode templateMode,
        IReadOnlyList<string> suppliers, FormKey terminus, bool terminusHasOwnMod, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var settings = new Settings
        {
            SkyrimRelease = SkyrimRelease.SkyrimSE,
            OutputPluginName = string.Empty,
            OutputDirectory = outputDirectory,
            AppendTimestampToOutputDirectory = false,
            ModsFolder = string.Empty,
            SplitOutput = false,
            AutoEslIfy = false,
            AutoSplitOutput = false,
            // The record path, where "flattened" is visible as a file at the NPC's own FormID path.
            PatchingMode = PatchingMode.CreateAndPatch,
            UseSkyPatcherMode = false,
            TemplateHandlingMode = templateMode,
            DefaultRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
            DefaultMaxNestedIntervalDepth = 2,
            DefaultIncludeAllOverrides = false,
            LocalizationLanguage = null,
            BatFilePreCommands = string.Empty,
            BatFilePostCommands = string.Empty,
        };

        settings.ModSettings = config.AppearanceMods.Select(kv => new ModSetting
        {
            DisplayName = kv.Key,
            CorrespondingModKeys = kv.Value.Plugins.Select(p => ModKey.FromFileName(p)).ToList(),
            CorrespondingFolderPaths = kv.Value.Folders.ToList(),
            IsFaceGenOnlyEntry = kv.Value.IsFaceGenOnly,
            MergeInDependencyRecords = true,
            IncludeOutfits = false,
            CopyAssets = false,      // FaceGen is copied regardless; this keeps the run small
            ModRecordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore,
        }).ToList();

        settings.SelectedAppearanceMods = new Dictionary<FormKey, (string ModName, FormKey NpcFormKey)>
        {
            [AdventurerA] = (suppliers[0], AdventurerA),
            [AdventurerB] = (suppliers[1], AdventurerB),
            [terminus] = (suppliers[terminusHasOwnMod ? 2 : 0], terminus),
        };

        return settings;
    }

    private static string? FaceGenHash(string outputDirectory, FormKey formKey)
    {
        var (meshRel, _) = Auxilliary.GetFaceGenSubPathStrings(formKey, regularized: true);
        var path = Path.Combine(outputDirectory, meshRel);
        return File.Exists(path) ? TemplateMatrixRunner.ShortHash(path) : null;
    }
}
