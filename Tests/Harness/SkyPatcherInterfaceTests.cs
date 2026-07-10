using System.IO;
using System.Text;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.Integration;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// <see cref="SkyPatcherInterface"/> — emits a SkyPatcher .ini from in-memory NPC directives.
///
/// Two surfaces are covered:
///   1. <see cref="SkyPatcherInterface.FormatFormKeyForSkyPatcher"/> — a pure static formatter
///      ("000800:Skyrim.esm" -> "Skyrim.esm|800"). Tested directly, no instance needed.
///   2. The instance directive surface, driven through a provider whose <c>Status == Invalid</c>
///      (<see cref="NpcChooserTestEnvironment.Invalid"/>) plus a fresh in-memory <c>OutputMod</c>.
///      No Skyrim install is touched: directives are seeded against in-memory Mutagen records and
///      the resulting .ini is written to (and read back from) a <see cref="TempDir"/>.
///
/// Joins the integration collection so the culture-mutating <see cref="StaticStateGuard"/> test
/// runs serially (the guard also resets the process-global ReactiveUI schedulers / logger).
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class SkyPatcherInterfaceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// An Invalid provider (no game) with a fresh in-memory output plugin named "NPCTest.esp".
    /// That is all the directive surface needs: CreateSkyPatcherNpc adds to OutputMod.Npcs and
    /// WriteIni reads OutputMod.ModKey.Name for the .ini filename.
    /// </summary>
    private static EnvironmentStateProvider InvalidEnvWithOutput()
    {
        var env = NpcChooserTestEnvironment.Invalid();
        env.OutputMod = new SkyrimMod(ModKey.FromName("NPCTest", ModType.Plugin), SkyrimRelease.SkyrimSE);
        return env;
    }

    private static SkyPatcherInterface NewInterface(out EnvironmentStateProvider env)
    {
        env = InvalidEnvWithOutput();
        return new SkyPatcherInterface(env);
    }

    /// <summary>
    /// Builds a standalone appearance-donor NPC in its own in-memory plugin so DeepCopyIn has a
    /// real INpcGetter to copy. (The donor lives in a different mod than the output, mirroring how
    /// CreateSkyPatcherNpc decouples the surrogate's content from the target FormKey.)
    /// </summary>
    private static INpcGetter MakeDonor(string editorId = "DonorNpc", string plugin = "Donor.esp")
    {
        var mod = MutagenFixtures.NewMod(plugin);
        return MutagenFixtures.NewNpc(mod, editorId: editorId, name: "Donor");
    }

    /// <summary>
    /// The canonical output path WriteIni/Reinitialize use:
    /// {root}/SKSE/Plugins/SkyPatcher/npc/NPC Plugin Chooser/{OutputModName}.ini
    /// </summary>
    private static string IniPath(string root, string outputModName = "NPCTest") =>
        Path.Combine(root, "SKSE", "Plugins", "SkyPatcher", "npc", "NPC Plugin Chooser", outputModName + ".ini");

    // ── FormatFormKeyForSkyPatcher (public static, pure) ──────────────────────

    [Fact]
    public void Format_LeadingZerosTrimmed_ExtensionPreserved()
    {
        SkyPatcherInterface.FormatFormKeyForSkyPatcher(FormKey.Factory("000800:Skyrim.esm"))
            .Should().Be("Skyrim.esm|800");
    }

    [Fact]
    public void Format_NoLeadingZeros_PassesThrough()
    {
        // 6 significant hex digits -> nothing to trim.
        SkyPatcherInterface.FormatFormKeyForSkyPatcher(FormKey.Factory("123456:Some.esp"))
            .Should().Be("Some.esp|123456");
    }

    [Fact]
    public void Format_UppercaseHexPreserved()
    {
        // IDString() is uppercase hex; the formatter must not lowercase it, and the single
        // leading zero is trimmed off "0ABCDE" -> "ABCDE".
        SkyPatcherInterface.FormatFormKeyForSkyPatcher(FormKey.Factory("0ABCDE:Mod.esp"))
            .Should().Be("Mod.esp|ABCDE");
    }

    [Fact]
    public void Format_IdZero_TrimsToEmptyAfterPipe()
    {
        // "000000".TrimStart('0') == "" -> trailing pipe with no id.
        SkyPatcherInterface.FormatFormKeyForSkyPatcher(FormKey.Factory("000000:Skyrim.esm"))
            .Should().Be("Skyrim.esm|");
    }

    [Fact]
    public void Format_NullFormKey_UsesNullModKeyName()
    {
        // FormKey.Null -> ModKey "Null.esp" (Mutagen's null sentinel) and id 0 -> trailing pipe.
        var result = SkyPatcherInterface.FormatFormKeyForSkyPatcher(FormKey.Null);
        result.Should().EndWith("|");
        result.Should().Be(FormKey.Null.ModKey.ToString() + "|");
    }

    [Theory]
    [InlineData("000001:A.esp", "A.esp|1")]
    [InlineData("00000F:A.esp", "A.esp|F")]
    [InlineData("0000F0:A.esp", "A.esp|F0")]
    [InlineData("00FF00:B.esm", "B.esm|FF00")]
    [InlineData("FFFFFF:C.esl", "C.esl|FFFFFF")]
    public void Format_Table(string fk, string expected)
    {
        SkyPatcherInterface.FormatFormKeyForSkyPatcher(FormKey.Factory(fk)).Should().Be(expected);
    }

    // ── CreateSkyPatcherNpc seeds the surrogate map ───────────────────────────

    [Fact]
    public void CreateSkyPatcherNpc_SeedsSurrogateMapsBothDirections_AndOutputsEntry()
    {
        var spi = NewInterface(out var env);
        var target = FormKey.Factory("000801:Target.esp");
        var donor = MakeDonor("Lydia");

        var surrogate = spi.CreateSkyPatcherNpc(target, donor);

        // The surrogate is a brand-new NPC in the output plugin, EditorID = donor + "_Template".
        surrogate.FormKey.ModKey.Should().Be(env.OutputMod.ModKey);
        surrogate.EditorID.Should().Be("Lydia_Template");
        env.OutputMod.Npcs.Should().ContainSingle();

        // target -> surrogate and surrogate -> target are both registered.
        spi.TryGetSurrogateFormKey(target, out var surFk).Should().BeTrue();
        surFk.Should().Be(surrogate.FormKey);
        spi.TryGetOriginalFormKey(surrogate.FormKey, out var origFk).Should().BeTrue();
        origFk.Should().Be(target);

        // _outputs now has an entry keyed by the TARGET (drives HasSkinEntries / WriteIni).
        spi.HasSkinEntries().Should().BeTrue();
    }

    [Fact]
    public void CreateSkyPatcherNpc_DonorWithoutEditorId_UsesNoEditorIdPlaceholder()
    {
        var spi = NewInterface(out _);
        var mod = MutagenFixtures.NewMod("Donor.esp");
        var donor = mod.Npcs.AddNew(); // no EditorID set

        var surrogate = spi.CreateSkyPatcherNpc(FormKey.Factory("000900:T.esp"), donor);

        surrogate.EditorID.Should().Be("NoEditorID_Template");
    }

    [Fact]
    public void TryGetSurrogateFormKey_Unknown_ReturnsFalse()
    {
        var spi = NewInterface(out _);
        spi.TryGetSurrogateFormKey(FormKey.Factory("00BEEF:Other.esp"), out var fk).Should().BeFalse();
        fk.Should().Be(FormKey.Null);
    }

    [Fact]
    public void TryGetOriginalFormKey_Unknown_ReturnsFalse()
    {
        var spi = NewInterface(out _);
        spi.TryGetOriginalFormKey(FormKey.Factory("00BEEF:Other.esp"), out var fk).Should().BeFalse();
        fk.Should().Be(FormKey.Null);
    }

    [Fact]
    public void CreateSkyPatcherNpc_DuplicateTarget_Throws()
    {
        // _outputs/_keyOriginalValSurrogate are Dictionary.Add — a second seed of the same
        // target FormKey throws (the patcher must create each surrogate exactly once).
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:Target.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor("A"));

        Action again = () => spi.CreateSkyPatcherNpc(target, MakeDonor("B"));
        again.Should().Throw<ArgumentException>();
    }

    // ── Directive guards (early-return when target not seeded / null) ─────────

    [Fact]
    public void Directives_OnUnseededTarget_AreNoOps()
    {
        using var tmp = new TempDir();
        var spi = NewInterface(out _);
        var unseeded = FormKey.Factory("00ABCD:Nope.esp");

        // No CreateSkyPatcherNpc for this key -> every directive must early-return harmlessly.
        spi.ApplyFace(unseeded, FormKey.Factory("000111:F.esp"));
        spi.ApplySkin(unseeded, FormKey.Factory("000222:S.esp"));
        spi.ApplyRace(unseeded, FormKey.Factory("000333:R.esp"));
        spi.ApplyHeight(unseeded, 1.0f);
        spi.ApplyWeight(unseeded, 50f);
        spi.ToggleGender(unseeded, Gender.Female);

        spi.HasSkinEntries().Should().BeFalse("no surrogate was ever created");
        spi.WriteIni(tmp.Path).Should().BeTrue();
        File.ReadAllText(IniPath(tmp.Path)).Should().BeEmpty();
    }

    [Fact]
    public void ApplyFace_NullFaceTemplate_IsNoOp()
    {
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        spi.ApplyFace(target, FormKey.Null); // null face -> guard returns before adding

        GetActionStrings(spi, target).Should().BeEmpty();
    }

    [Fact]
    public void ApplySkin_NullSkin_IsNoOp()
    {
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        spi.ApplySkin(target, FormKey.Null);

        GetActionStrings(spi, target).Should().BeEmpty();
    }

    [Fact]
    public void ApplyRace_NullRace_IsNoOp()
    {
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        spi.ApplyRace(target, FormKey.Null);

        GetActionStrings(spi, target).Should().BeEmpty();
    }

    // ── Directive content (assert through WriteIni output) ────────────────────

    [Fact]
    public void ApplyFace_EmitsCopyVisualStyleDirective()
    {
        using var tmp = new TempDir();
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        spi.ApplyFace(target, FormKey.Factory("000800:Skyrim.esm"));
        spi.WriteIni(tmp.Path);

        File.ReadAllText(IniPath(tmp.Path)).TrimEnd('\r', '\n')
            .Should().Be("filterByNPCs=T.esp|801:copyVisualStyle=Skyrim.esm|800");
    }

    [Fact]
    public void ApplySkin_EmitsSkinDirective()
    {
        using var tmp = new TempDir();
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        spi.ApplySkin(target, FormKey.Factory("00AB12:Skins.esp"));
        GetActionStrings(spi, target).Should().ContainSingle()
            .Which.Should().Be("skin=Skins.esp|AB12");
    }

    [Fact]
    public void ApplyRace_EmitsRaceDirective()
    {
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        spi.ApplyRace(target, FormKey.Factory("000FF0:Skyrim.esm"));
        GetActionStrings(spi, target).Should().ContainSingle()
            .Which.Should().Be("race=Skyrim.esm|FF0");
    }

    [Fact]
    public void ToggleGender_Female_SetsFemaleFlag()
    {
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        spi.ToggleGender(target, Gender.Female);
        GetActionStrings(spi, target).Should().ContainSingle().Which.Should().Be("setFlags=female");
    }

    [Fact]
    public void ToggleGender_Male_RemovesFemaleFlag()
    {
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        spi.ToggleGender(target, Gender.Male);
        GetActionStrings(spi, target).Should().ContainSingle().Which.Should().Be("removeFlags=female");
    }

    // ── ApplyHeight / ApplyWeight: culture-invariant decimal separator ────────

    [Fact]
    public void ApplyHeight_UsesInvariantDot_UnderGermanCulture()
    {
        // de-DE uses ',' as the decimal separator. The directive must still use '.' because
        // SkyPatcher parses the .ini culture-invariantly.
        using var g = new StaticStateGuard().WithCulture("de-DE");
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        spi.ApplyHeight(target, 0.975f);

        GetActionStrings(spi, target).Should().ContainSingle().Which.Should().Be("height=0.975");
    }

    [Fact]
    public void ApplyWeight_UsesInvariantDot_UnderGermanCulture()
    {
        using var g = new StaticStateGuard().WithCulture("de-DE");
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        spi.ApplyWeight(target, 12.5f);

        GetActionStrings(spi, target).Should().ContainSingle().Which.Should().Be("weight=12.5");
    }

    [Fact]
    public void ApplyHeight_IntegralValue_FormatsWithoutDecimal()
    {
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        spi.ApplyHeight(target, 1f);
        GetActionStrings(spi, target).Should().ContainSingle().Which.Should().Be("height=1");
    }

    // ── WriteIni: sorting, encoding, formatting ───────────────────────────────

    [Fact]
    public void WriteIni_SortsActionStringsOrdinally_HeightThenSkinThenWeight()
    {
        using var tmp = new TempDir();
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        // Add out of order; WriteIni applies .Order() (ordinal) -> height < skin < weight.
        spi.ApplyWeight(target, 50f);
        spi.ApplySkin(target, FormKey.Factory("000222:Skins.esp"));
        spi.ApplyHeight(target, 1f);

        spi.WriteIni(tmp.Path).Should().BeTrue();

        var line = File.ReadAllText(IniPath(tmp.Path)).TrimEnd('\r', '\n');
        line.Should().Be("filterByNPCs=T.esp|801:height=1,skin=Skins.esp|222,weight=50");
    }

    // ── WriteIni: post-auto-split FormKey remap ───────────────────────────────

    [Fact]
    public void WriteIni_WithRemap_RewritesOutputPluginRefs_LeavesDonorRefsUntouched()
    {
        // When the output plugin is auto-split, a surrogate that used to live in "NPCTest.esp"
        // moves to "NPCTest_2.esp" (same local id). The remap must rewrite the copyVisualStyle
        // target (an output-plugin ref) while leaving the donor skin ref alone.
        using var tmp = new TempDir();
        var spi = NewInterface(out var env);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());

        var outputFace = FormKey.Factory("000800:NPCTest.esp"); // lives in the output plugin
        spi.ApplyFace(target, outputFace);
        spi.ApplySkin(target, FormKey.Factory("00AB12:Skins.esp")); // donor ref - must NOT move

        var remap = new Dictionary<FormKey, FormKey>
        {
            [outputFace] = FormKey.Factory("000800:NPCTest_2.esp"),
        };

        spi.WriteIni(tmp.Path, remap).Should().BeTrue();

        var line = File.ReadAllText(IniPath(tmp.Path)).TrimEnd('\r', '\n');
        line.Should().Contain("copyVisualStyle=NPCTest_2.esp|800");
        line.Should().NotContain("NPCTest.esp|800");
        line.Should().Contain("skin=Skins.esp|AB12");
    }

    [Fact]
    public void WriteIni_NullRemap_LeavesFormKeysUnchanged()
    {
        // The common (non-split) path passes a null remap; every FormKey is emitted verbatim.
        using var tmp = new TempDir();
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());
        spi.ApplyFace(target, FormKey.Factory("000800:NPCTest.esp"));

        spi.WriteIni(tmp.Path, null).Should().BeTrue();

        File.ReadAllText(IniPath(tmp.Path)).TrimEnd('\r', '\n')
            .Should().Be("filterByNPCs=T.esp|801:copyVisualStyle=NPCTest.esp|800");
    }

    [Fact]
    public void WriteIni_RemapWithoutMatchingEntry_IsNoOp()
    {
        // A remap that doesn't contain the referenced FormKey leaves it untouched (defensive: an
        // unrelated split entry must never alter a directive it doesn't apply to).
        using var tmp = new TempDir();
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());
        spi.ApplyFace(target, FormKey.Factory("000800:NPCTest.esp"));

        var remap = new Dictionary<FormKey, FormKey>
        {
            [FormKey.Factory("000999:NPCTest.esp")] = FormKey.Factory("000999:NPCTest_2.esp"),
        };

        spi.WriteIni(tmp.Path, remap).Should().BeTrue();

        File.ReadAllText(IniPath(tmp.Path)).TrimEnd('\r', '\n')
            .Should().Be("filterByNPCs=T.esp|801:copyVisualStyle=NPCTest.esp|800");
    }

    [Fact]
    public void WriteIni_NoEntries_WritesEmptyFile()
    {
        using var tmp = new TempDir();
        var spi = NewInterface(out _);

        spi.HasSkinEntries().Should().BeFalse();
        spi.WriteIni(tmp.Path).Should().BeTrue();

        var path = IniPath(tmp.Path);
        File.Exists(path).Should().BeTrue();
        new FileInfo(path).Length.Should().Be(0);
    }

    [Fact]
    public void WriteIni_WritesUtf8WithoutBom()
    {
        using var tmp = new TempDir();
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());
        spi.ApplyHeight(target, 1f);

        spi.WriteIni(tmp.Path).Should().BeTrue();

        var bytes = File.ReadAllBytes(IniPath(tmp.Path));
        // No UTF-8 BOM (EF BB BF) prefix.
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        Encoding.UTF8.GetString(bytes).Should().StartWith("filterByNPCs=");
    }

    [Fact]
    public void WriteIni_OneLinePerEntry_TerminatedWithEnvironmentNewLine()
    {
        using var tmp = new TempDir();
        var spi = NewInterface(out _);

        var t1 = FormKey.Factory("000801:T.esp");
        var t2 = FormKey.Factory("000802:T.esp");
        spi.CreateSkyPatcherNpc(t1, MakeDonor("A", "DonorA.esp"));
        spi.CreateSkyPatcherNpc(t2, MakeDonor("B", "DonorB.esp"));
        spi.ApplyHeight(t1, 1f);
        spi.ApplyHeight(t2, 2f);

        spi.WriteIni(tmp.Path).Should().BeTrue();

        var text = File.ReadAllText(IniPath(tmp.Path));
        // Two entries -> two NewLine terminators (one per entry, trailing included).
        var lines = text.Split(System.Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines.Should().Contain("filterByNPCs=T.esp|801:height=1");
        lines.Should().Contain("filterByNPCs=T.esp|802:height=2");
        text.Should().EndWith(System.Environment.NewLine);
    }

    [Fact]
    public void WriteIni_CreatesNestedOutputDirectory()
    {
        using var tmp = new TempDir();
        var spi = NewInterface(out _);
        // Output directory does not exist yet; WriteIni must create the full SKSE/.../ path.
        var expectedDir = Path.GetDirectoryName(IniPath(tmp.Path))!;
        Directory.Exists(expectedDir).Should().BeFalse();

        spi.WriteIni(tmp.Path).Should().BeTrue();

        Directory.Exists(expectedDir).Should().BeTrue();
    }

    // ── HasSkinEntries ────────────────────────────────────────────────────────

    [Fact]
    public void HasSkinEntries_FalseBeforeSeed_TrueAfter()
    {
        var spi = NewInterface(out _);
        spi.HasSkinEntries().Should().BeFalse();
        spi.CreateSkyPatcherNpc(FormKey.Factory("000801:T.esp"), MakeDonor());
        spi.HasSkinEntries().Should().BeTrue();
    }

    // ── Reinitialize ──────────────────────────────────────────────────────────

    [Fact]
    public void Reinitialize_ClearsAllMapsAndDeletesExistingIni()
    {
        using var tmp = new TempDir();
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor());
        spi.ApplyHeight(target, 1f);
        spi.WriteIni(tmp.Path).Should().BeTrue();

        var iniPath = IniPath(tmp.Path);
        File.Exists(iniPath).Should().BeTrue();

        spi.Reinitialize(tmp.Path);

        // All bookkeeping cleared...
        spi.HasSkinEntries().Should().BeFalse();
        spi.TryGetSurrogateFormKey(target, out _).Should().BeFalse();
        // ...and the previously-written .ini is deleted.
        File.Exists(iniPath).Should().BeFalse();
    }

    [Fact]
    public void Reinitialize_NoExistingIni_DoesNotThrow()
    {
        using var tmp = new TempDir();
        var spi = NewInterface(out _);

        Action act = () => spi.Reinitialize(tmp.Path);
        act.Should().NotThrow();
        spi.HasSkinEntries().Should().BeFalse();
    }

    [Fact]
    public void Reinitialize_AllowsReseedingSameTargetAfterClear()
    {
        using var tmp = new TempDir();
        var spi = NewInterface(out _);
        var target = FormKey.Factory("000801:T.esp");
        spi.CreateSkyPatcherNpc(target, MakeDonor("A"));

        spi.Reinitialize(tmp.Path);

        // Maps were cleared, so the same target key can be seeded again without the duplicate-Add throw.
        Action reseed = () => spi.CreateSkyPatcherNpc(target, MakeDonor("B"));
        reseed.Should().NotThrow();
    }

    // ── Reflection helper: read a seeded NPC's ActionStrings without WriteIni ──

    /// <summary>
    /// Reads the private Actions list for a seeded target via Reflect and renders each directive to
    /// its final ".ini" string (no remap applied), so the directive-content assertions can stay
    /// unchanged after Actions moved from pre-formatted strings to structural (Text, FormKeyRef)
    /// entries. The dictionary (_outputs), the NpcContainer, and the SkyPatcherAction nested types
    /// are all private; we reach them reflectively without naming the types.
    /// </summary>
    private static List<string> GetActionStrings(SkyPatcherInterface spi, FormKey target)
    {
        var outputs = Reflect.GetField<System.Collections.IDictionary>(spi, "_outputs");
        var container = outputs[target];
        container.Should().NotBeNull("the target must have been seeded via CreateSkyPatcherNpc");
        var actions = (System.Collections.IEnumerable)container!.GetType().GetProperty("Actions")!.GetValue(container)!;

        var rendered = new List<string>();
        foreach (var action in actions)
        {
            var text = (string)action.GetType().GetProperty("Text")!.GetValue(action)!;
            var formKeyRef = action.GetType().GetProperty("FormKeyRef")!.GetValue(action);
            rendered.Add(formKeyRef is FormKey fk
                ? text + "=" + SkyPatcherInterface.FormatFormKeyForSkyPatcher(fk)
                : text);
        }
        return rendered;
    }
}
