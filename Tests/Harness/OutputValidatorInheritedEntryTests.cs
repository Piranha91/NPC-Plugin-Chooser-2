using System.IO;
using System.Text;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.Integration;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// "Validate Output" and the entries the patcher writes for NPCs that inherit their face from a
/// patched NPC (bench finding N11; see <c>Patcher.MirrorAppearanceOntoInheritingNpcs</c>). Such an
/// entry names the TEMPLATE's surrogate, so grading it against the inheriting NPC's own selection
/// would report a mismatch that is the intended result; and the "Not patched" note for a selection
/// that screening rejected must no longer say the NPC shows whatever the load order supplies.
///
/// <para>Reached like <see cref="OutputValidatorRecastTests"/>: an uninitialized validator with only
/// <c>_settings</c> and <c>_environmentStateProvider</c> set, in-memory records, a temp Data folder.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class OutputValidatorInheritedEntryTests
{
    private const string ModA = "Mod A";
    private const string ModB = "Mod B";
    private const string OldNote = "whatever your load order already supplies";
    private const string SkipReason =
        "Skipped before patching: Templated NPC — the back end can't apply an appearance through a template chain";

    private static readonly FormKey Surrogate = FormKey.Factory("000800:NPCTest.esp");
    private static readonly FormKey SurrogateSkin = FormKey.Factory("000801:NPCTest.esp");

    /// <summary>
    /// Validates the twin of a two-NPC load order (a template that owns its face, and a twin that
    /// takes its traits from it) against a deployed output in which the template has an entry and,
    /// when <paramref name="twinHasEntry"/>, the twin has the entry the patcher writes for it.
    /// </summary>
    private static List<ValidationIssue> ValidateTwin(OutputBackend backend, string templateSelection,
        bool withToken, bool twinHasEntry = true)
    {
        using var tmp = new TempDir("n11val");

        var baseMod = MutagenFixtures.NewMod("N11Base.esp");
        var template = MutagenFixtures.NewNpc(baseMod, "N11_Template", name: "N11_Template");
        var twin = MutagenFixtures.NewNpc(baseMod, "N11_Twin", name: "N11_Twin", traitsTemplate: true, template: template);
        var cache = baseMod.ToImmutableLinkCache();

        var settings = new Settings
        {
            OutputBackend = backend,
            RecastMode = RecastMode.RecastOnly,
            SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
            {
                [template.FormKey] = (templateSelection, template.FormKey),
                [twin.FormKey] = (ModA, twin.FormKey),
            },
        };

        var env = NpcChooserTestEnvironment.Invalid();
        Reflect.SetField(env, "<OutputPluginName>k__BackingField", "NPCTest");
        var validator = Reflect.Uninitialized<OutputValidator>();
        Reflect.SetField(validator, "_settings", settings);
        Reflect.SetField(validator, "_environmentStateProvider", env);

        object? iniMap = null;
        if (backend == OutputBackend.SkyPatcher)
        {
            string Line(FormKey target, string extra) =>
                $"filterByNPCs={SkyPatcherInterface.FormatFormKeyForSkyPatcher(target)}:" +
                $"copyVisualStyle={SkyPatcherInterface.FormatFormKeyForSkyPatcher(Surrogate)}:height=1:{extra}" +
                $"skin={SkyPatcherInterface.FormatFormKeyForSkyPatcher(SurrogateSkin)}:weight=50";

            var ini = Line(template.FormKey, string.Empty) + Environment.NewLine;
            if (twinHasEntry) ini += Line(twin.FormKey, "removeTemplateFlags=traits:") + Environment.NewLine;
            var iniPath = tmp.WriteText(
                Path.Combine("SKSE", "Plugins", "SkyPatcher", "npc", "NPC Plugin Chooser", "NPCTest.ini"), ini);
            iniMap = Reflect.InvokeStatic<OutputValidator, object>("ParseNpc2SkyPatcherIni", iniPath);
        }
        else
        {
            string Block(FormKey target) =>
                $"\n[[npcs]]\ntarget = \"{RecastInterface.FormatSelector(target)}\"\nface = \"{RecastInterface.FormatSelector(Surrogate)}\"\n";

            var toml = "[manifest]\nname = \"NPC Plugin Chooser 2\"\npriority = 1000\napi_version = 1\n" + Block(template.FormKey);
            if (twinHasEntry) toml += Block(twin.FormKey);
            var tomlPath = Path.Combine(RecastInterface.GetOutputDir(tmp.Path), "NPCTest.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(tomlPath)!);
            File.WriteAllText(tomlPath, toml);
        }

        var log = new StringBuilder();
        var recastIndex = Reflect.Invoke<object>(validator, "BuildRecastIndex", tmp.Path, log)!;

        NpcToken? lastRun = null;
        if (withToken)
        {
            lastRun = new NpcToken { CreationDate = "2026-11-26T12:00:00.0000000-08:00" };
            lastRun.ProcessedNpcs[template.FormKey] = new NpcAppearanceData
            {
                ModName = templateSelection,
                AppearancePlugin = ModKey.FromFileName("ModA.esp"),
                OutputPlugin = ModKey.FromFileName("NPCTest.esp"),
            };
            lastRun.SkippedNpcs[twin.FormKey] = SkipReason;
        }

        var modSettingsByName = new Dictionary<string, ModSetting>(StringComparer.OrdinalIgnoreCase)
        {
            [ModA] = new ModSetting { DisplayName = ModA },
            [ModB] = new ModSetting { DisplayName = ModB },
        };

        var result = new ValidationRunResult();
        IReadOnlySet<FormKey> scoped = new HashSet<FormKey> { template.FormKey, twin.FormKey };
        Reflect.InvokeVoid(validator, "ValidateNpc",
            twin.FormKey, cache, new List<IModListingGetter<ISkyrimModGetter>>(), modSettingsByName,
            null, recastIndex, iniMap, tmp.Path, scoped, lastRun, null, result, log);
        return result.Issues;
    }

    [Theory]
    [InlineData(OutputBackend.SkyPatcher)]
    [InlineData(OutputBackend.Recast)]
    public void RejectedSelection_WithAnInheritedEntry_SaysWhoseAppearanceTheNpcShows(OutputBackend backend)
    {
        var issues = ValidateTwin(backend, templateSelection: ModB, withToken: true);

        var row = issues.Should().ContainSingle("the rejected selection still gets exactly one row").Subject;
        row.Severity.Should().Be(ValidationSeverity.Info, "the user was shown the rejection before patching");
        row.Issue.Should().NotContain(OldNote, "the last run did give this NPC an appearance: its template's");
        row.Issue.Should().Contain("N11_Template").And.Contain(ModB);
        row.Details.Should().Contain(SkipReason, "the recorded reason is still quoted");
    }

    [Theory]
    [InlineData(OutputBackend.SkyPatcher)]
    [InlineData(OutputBackend.Recast)]
    public void RejectedSelection_WithAnInheritedEntryOfTheSameMod_IsOneInfoRowAndNoFinding(OutputBackend backend)
    {
        var issues = ValidateTwin(backend, templateSelection: ModA, withToken: true);

        var row = issues.Should().ContainSingle().Subject;
        row.Severity.Should().Be(ValidationSeverity.Info);
        row.Issue.Should().NotContain(OldNote);
        row.Issue.Should().Contain("N11_Template");
    }

    [Theory]
    [InlineData(OutputBackend.SkyPatcher)]
    [InlineData(OutputBackend.Recast)]
    public void InheritedEntry_WithoutARunLedger_IsNotGradedAgainstTheNpcsOwnSelection(OutputBackend backend)
    {
        // No NPC_Token.json: every NPC is graded as if it had been patched. The twin's entry names the
        // template's surrogate on purpose, so the surrogate checks must not run against the twin's own
        // selection; with the template set to the same mod there is nothing to say at all.
        ValidateTwin(backend, templateSelection: ModA, withToken: false)
            .Should().BeEmpty("the twin shows the mod the user picked for it, through its template");

        var row = ValidateTwin(backend, templateSelection: ModB, withToken: false)
            .Should().ContainSingle().Subject;
        row.Severity.Should().Be(ValidationSeverity.Warning, "the face shown is another mod's, and nothing told the user");
        row.Check.Should().Be(ValidationCheckKind.Template);
        row.Issue.Should().Contain("N11_Template").And.Contain(ModB);
    }

    [Theory]
    [InlineData(OutputBackend.SkyPatcher)]
    [InlineData(OutputBackend.Recast)]
    public void RejectedSelection_WithoutAnEntry_KeepsTheNotPatchedRow(OutputBackend backend)
    {
        // The control: an output written before the patcher gave inheriting NPCs an entry.
        var row = ValidateTwin(backend, templateSelection: ModA, withToken: true, twinHasEntry: false)
            .Should().ContainSingle().Subject;
        row.Severity.Should().Be(ValidationSeverity.Info);
        row.Issue.Should().Contain(OldNote);
    }
}
