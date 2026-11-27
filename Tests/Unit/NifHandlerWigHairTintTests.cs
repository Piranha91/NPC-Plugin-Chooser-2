using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The hair-tint leg of <see cref="NifHandler.BakeWigIntoFaceGen"/>
/// (<see cref="WigHairTintMode"/>). A worn hair-slot wig is recolored in game by
/// RaceMenu (<c>bEnableTintHairSlot</c>), never by the engine, so wig
/// meshes routinely ship a dark PLACEHOLDER <c>hairTintColor</c>. Baking such a
/// wig into the FaceGen takes it out of RaceMenu's reach, so whatever tint the
/// bake leaves is permanent.
///
/// <para>Whether the bake writes the NPC's hair color in is decided before it
/// runs (<see cref="WigHairTintDecision.ShouldTint"/>) and handed over as
/// <c>ApplyHairTint</c>. Each test here goes through that decision with a mode, a
/// RaceMenu state and the specimen's real carrier slots, then reads the tints the
/// bake left. Auto reproduces what the game shows without this app: tinted when
/// RaceMenu would have tinted the worn wig, untouched otherwise. It does NOT look
/// at the tint the wig already carries (RaceMenu does not either).</para>
///
/// <para>Two real specimens, one per authoring style:</para>
/// <list type="bullet">
/// <item>High Poly NPC Overhaul's KS Hairdos wigs — HairTint shader, placeholder
/// tint (0.1333, 0.1333, 0.1333), greyscale-ish texture, carried on the skin.
/// Black hair in game without RaceMenu, the NPC's hair color with it.</item>
/// <item>FoxGlove Auri's wig — HairTint shader, NEUTRAL tint (1, 1, 1) over a
/// pre-colored red texture, carried in the outfit. Measured in game: RaceMenu
/// tints it all the same.</item>
/// </list>
/// Machine-local: gracefully skips when a specimen isn't installed (suite
/// convention). Works on temp copies; source files are never modified.
/// </summary>
public class NifHandlerWigHairTintTests
{
    /// <summary>RaceMenu as its shipped ini configures it: tint on, mask 2050 (slots 31 + 41).</summary>
    private static readonly RaceMenuHairTintState RaceMenuShipped =
        new(true, true, 2050, @"SKSE\Plugins\skee64.dll", @"X:\Data\SKSE\Plugins\skee64.dll", Array.Empty<string>());

    private static readonly RaceMenuHairTintState NoRaceMenu =
        RaceMenuHairTintState.NotInstalled(@"SKSE\Plugins\skee64.dll");

    /// <summary>The slots the wig was worn through, read from the specimens' plugins:
    /// High Poly's skin ARMO (HighPoly_BodyArmor_Hair*, slots 30-33 + 37) with its wig addon
    /// (HighPoly_WigAA_*, slot 31); FoxGlove's wig ARMO (FoxGlove_Wig, slots 31 + 38) with its
    /// addon (FoxGlove_Wig_AA, slots 31 + 41).</summary>
    private static readonly (uint Armor, uint Addon) HpnoCarrier = (143, 2);
    private static readonly (uint Armor, uint Addon) FoxGloveCarrier = (258, 2050);

    // --- FoxGlove Auri: neutral-tint wig over a pre-colored texture ---
    // The merged "- Test" copy first; where only the mod as installed exists, its FaceGen is in
    // the option folder and its wig in the Core folder. Without the fallback every test of this
    // specimen returned early on a machine that has the mod but not the merged copy.
    private const string FoxGloveMods = @"S:\Skyrim NPC Selection\mods\FoxGlove - Auri Visual Overhaul - ";
    private const string FoxGloveFaceGenRel =
        @"\meshes\actors\character\FaceGenData\FaceGeom\018auri.esp\00000D63.NIF";
    private const string FoxGloveWigRel = @"\meshes\actors\FoxGlove Auri\Wig\22a_1.nif";
    private static readonly string FoxGloveFaceGen = FirstExisting(
        FoxGloveMods + "The FoxGlove - Classic Red - No Warpaint - Test" + FoxGloveFaceGenRel,
        FoxGloveMods + "The FoxGlove - Classic Red - No Warpaint (Default)" + FoxGloveFaceGenRel);
    private static readonly string FoxGloveWig = FirstExisting(
        FoxGloveMods + "The FoxGlove - Classic Red - No Warpaint - Test" + FoxGloveWigRel,
        FoxGloveMods + "Core" + FoxGloveWigRel);

    private static string FirstExisting(params string[] candidates) =>
        candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    private static readonly string[] FoxGloveDonorHair =
        { "FoxGloveHairMesh", "FoxGloveHairlineMesh", "FoxGloveHairScalp" };

    /// <summary>Auri's CK-baked hair color, on her brows and hairline — exactly
    /// 2x her HCLR record (66, 53, 45), the same convention Alvor confirms.</summary>
    private static readonly (float R, float G, float B) FoxGloveFaceGenHairTint = (0.5176f, 0.4157f, 0.3529f);

    // --- High Poly NPC Overhaul: placeholder-tint wig on a BALD FaceGen ---
    private const string HpnoFaceGen =
        @"S:\Skyrim NPC Selection\mods\High Poly NPC Overhaul - Skyrim Special Edition 2.0 (All Vanilla NPCs)" +
        @"\meshes\actors\character\FaceGenData\FaceGeom\Skyrim.esm\00013261.NIF";
    private const string HpnoWig =
        @"S:\Skyrim NPC Selection\mods\High Poly NPC Overhaul - Resources\meshes\Wigs\Female\Anchor.nif";

    /// <summary>The placeholder every KS Hairdos wig in HPNO-Resources bakes.</summary>
    private static readonly (float R, float G, float B) HpnoPlaceholderTint = (0.1333f, 0.1333f, 0.1333f);

    /// <summary>The hair color the CK baked into 00013261's brows and beard —
    /// what the FaceGen-harvest fallback must find.</summary>
    private static readonly (float R, float G, float B) HpnoFaceGenHairTint = (0.5176f, 0.4157f, 0.3529f);

    /// <summary>Stand-in for a resolved HCLR record (sRGB 0..1), deliberately
    /// unlike either specimen's baked value so an overwrite is unambiguous.</summary>
    private static readonly (float R, float G, float B) RecordHairColor = (0.75f, 0.25f, 0.10f);

    /// <summary>FoxGlove Auri's baked wig tint — a no-op multiply.</summary>
    private static readonly (float R, float G, float B) NeutralTint = (1f, 1f, 1f);

    private static bool FoxGloveMissing => !File.Exists(FoxGloveFaceGen) || !File.Exists(FoxGloveWig);
    private static bool HpnoMissing => !File.Exists(HpnoFaceGen) || !File.Exists(HpnoWig);

    // ═══════════════════════════════════════════════════════════════════════
    //  Placeholder-tint wig (the High Poly NPC Overhaul case)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Auto_WithRaceMenu_OverwritesPlaceholderTint_WithTheFaceGensOwnHairColor()
    {
        if (HpnoMissing) return;

        // The FaceGen's own hair color wins over the supplied record value, so
        // the wig ends up the same color as this NPC's brows and beard.
        var tints = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            WigHairTintMode.Auto, RaceMenuShipped, HpnoCarrier, RecordHairColor, synthesizePartition: true);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, HpnoFaceGenHairTint),
            "RaceMenu replaces the placeholder on the worn wig in game, so Auto bakes the same result");
    }

    [Fact]
    public void Auto_WithoutRaceMenu_LeavesPlaceholderTintUntouched()
    {
        if (HpnoMissing) return;

        var tints = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            WigHairTintMode.Auto, NoRaceMenu, HpnoCarrier, RecordHairColor, synthesizePartition: true);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, HpnoPlaceholderTint),
            "without RaceMenu the game shows the placeholder, and Auto reproduces what the game shows");
    }

    [Fact]
    public void Auto_WithRaceMenuTintSettingOff_LeavesPlaceholderTintUntouched()
    {
        if (HpnoMissing) return;

        var tintOff = RaceMenuShipped with { TintEnabled = false };
        var tints = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            WigHairTintMode.Auto, tintOff, HpnoCarrier, RecordHairColor, synthesizePartition: true);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, HpnoPlaceholderTint));
    }

    [Fact]
    public void Always_OverwritesPlaceholderTint_WithTheFaceGensOwnHairColor()
    {
        if (HpnoMissing) return;

        // On needs no RaceMenu.
        var tints = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            WigHairTintMode.Always, NoRaceMenu, HpnoCarrier, RecordHairColor, synthesizePartition: true);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, HpnoFaceGenHairTint));
    }

    /// <summary>
    /// The per-NPC override, end to end: the mod says Off, this one NPC says On, and the mode
    /// <see cref="Settings.GetEffectiveWigHairTintMode(ModSetting, Mutagen.Bethesda.Plugins.FormKey)"/>
    /// resolves for each is what the bake then does.
    /// </summary>
    [Fact]
    public void PerNpcOverride_BeatsTheModsMode_InWhatIsBaked()
    {
        if (HpnoMissing) return;

        var npc = Mutagen.Bethesda.Plugins.FormKey.Factory("013261:Skyrim.esm");
        var otherNpc = Mutagen.Bethesda.Plugins.FormKey.Factory("013262:Skyrim.esm");
        var mod = new ModSetting
        {
            DisplayName = "HPNO",
            DetectedWigArmatures = { Mutagen.Bethesda.Plugins.FormKey.Factory("0008A9:HighPolyResources.esp") },
            ModWigHairTintMode = WigHairTintMode.Never,
        };
        var settings = new Settings();
        settings.SetNpcWigHairTintMode(new[] { npc }, WigHairTintMode.Always);

        var overridden = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            settings.GetEffectiveWigHairTintMode(mod, npc), RaceMenuShipped, HpnoCarrier, RecordHairColor,
            synthesizePartition: true);
        var followsMod = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            settings.GetEffectiveWigHairTintMode(mod, otherNpc), RaceMenuShipped, HpnoCarrier, RecordHairColor,
            synthesizePartition: true);

        overridden.Should().NotBeEmpty();
        overridden.Should().OnlyContain(t => Approximately(t, HpnoFaceGenHairTint), "this NPC's override is On");
        followsMod.Should().NotBeEmpty();
        followsMod.Should().OnlyContain(t => Approximately(t, HpnoPlaceholderTint), "the mod's mode is Off");
    }

    /// <summary>
    /// The CK bakes <c>hairTintColor = 2 x HCLR</c>. Measured over the 344 HPNO
    /// FaceGens with a resolvable hair color: 336 are exactly 2x on all three
    /// channels. Alvor is the clean case — <c>HairColor05DarkBlond</c> is
    /// (56, 59, 44) and his baked brow/beard tint is (112, 118, 88).
    /// </summary>
    [Fact]
    public void HclrToFaceGenTint_DoublesAndClamps()
    {
        var alvor = NifHandler.HclrToFaceGenTint((56 / 255f, 59 / 255f, 44 / 255f));
        Approximately(alvor, (112 / 255f, 118 / 255f, 88 / 255f)).Should().BeTrue();

        NifHandler.HclrToFaceGenTint((0.9f, 0.5f, 0.1f))
            .Should().Be((1f, 1f, 0.2f), "channels saturate rather than exceed 1");
    }

    [Fact]
    public void Never_LeavesPlaceholderTintUntouched()
    {
        if (HpnoMissing) return;

        // Off wins even where RaceMenu would tint the worn wig.
        var tints = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            WigHairTintMode.Never, RaceMenuShipped, HpnoCarrier, RecordHairColor, synthesizePartition: true);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, HpnoPlaceholderTint),
            "Never is the pre-feature behavior — the wig keeps whatever its author shipped");
    }

    [Fact]
    public void NoHairColorRecord_StillUsesTheFaceGenHairTint()
    {
        if (HpnoMissing) return;

        var tints = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            WigHairTintMode.Auto, RaceMenuShipped, HpnoCarrier, hairTintRgb: null, synthesizePartition: true);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, HpnoFaceGenHairTint));
    }

    /// <summary>
    /// Regression: the record and the FaceGen genuinely disagree when a mod later
    /// in the load order overrides the color record after the appearance mod
    /// exported its FaceGen. Alvor's FaceGen was baked from HairColor05DarkBlond
    /// (56, 59, 44) while the winning override resolves to (54, 41, 37) — taking
    /// the record gave him red hair over a dark-blond beard.
    /// </summary>
    [Fact]
    public void FaceGenHairColor_BeatsTheRecord_WhenTheyDisagree()
    {
        if (HpnoMissing) return;

        var tints = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            WigHairTintMode.Always, NoRaceMenu, HpnoCarrier, RecordHairColor, synthesizePartition: true);

        tints.Should().NotBeEmpty();
        tints.Should().NotContain(t => Approximately(t, RecordHairColor));
        tints.Should().OnlyContain(t => Approximately(t, HpnoFaceGenHairTint),
            "the wig must match the brows and beard, which carry the FaceGen's own hair color");
    }

    [Fact]
    public void GetFaceGenHairTint_ReadsTheCkBakedHairColor()
    {
        if (HpnoMissing) return;

        var tint = NifHandler.GetFaceGenHairTint(HpnoFaceGen);

        tint.Should().NotBeNull();
        Approximately(tint!.Value, HpnoFaceGenHairTint).Should().BeTrue();
    }

    [Fact]
    public void GetFaceGenHairTint_ReturnsNullForAMissingFile()
    {
        NifHandler.GetFaceGenHairTint(Path.Combine(Path.GetTempPath(), "npc2-no-such-facegen.nif"))
            .Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Neutral-tint wig (the FoxGlove Auri case — pre-colored texture)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Was <c>Auto_LeavesNeutralWhiteTintUntouched</c>: Auto used to mean "tint unless the baked
    /// tint is neutral white", on the theory that a white tint marks a pre-colored texture the
    /// game leaves alone. Measured in game, RaceMenu tints Auri's white-tint wig like any other
    /// (its test is the slot mask, never the existing color), so a converted wig left white would
    /// NOT look the way the worn one did.
    /// </summary>
    [Fact]
    public void Auto_WithRaceMenu_OverwritesANeutralWhiteTint_AsRaceMenuDoesInGame()
    {
        if (FoxGloveMissing) return;

        var tints = BakeAndReadWigTints(FoxGloveFaceGen, FoxGloveWig, FoxGloveDonorHair,
            WigHairTintMode.Auto, RaceMenuShipped, FoxGloveCarrier, RecordHairColor);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, FoxGloveFaceGenHairTint),
            "the white tint is not a reason to skip: RaceMenu does not test the existing color");
    }

    [Fact]
    public void Auto_WithoutRaceMenu_LeavesNeutralWhiteTintUntouched()
    {
        if (FoxGloveMissing) return;

        var tints = BakeAndReadWigTints(FoxGloveFaceGen, FoxGloveWig, FoxGloveDonorHair,
            WigHairTintMode.Auto, NoRaceMenu, FoxGloveCarrier, RecordHairColor);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, NeutralTint),
            "nothing tints the worn wig without RaceMenu, so the converted one keeps its authored tint");
    }

    [Fact]
    public void Always_OverwritesEvenANeutralWhiteTint()
    {
        if (FoxGloveMissing) return;

        var tints = BakeAndReadWigTints(FoxGloveFaceGen, FoxGloveWig, FoxGloveDonorHair,
            WigHairTintMode.Always, NoRaceMenu, FoxGloveCarrier, RecordHairColor);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, FoxGloveFaceGenHairTint),
            "On writes the NPC's hair color into every HairTint shape of the wig");
    }

    [Fact]
    public void Never_LeavesNeutralWhiteTintUntouched()
    {
        if (FoxGloveMissing) return;

        var tints = BakeAndReadWigTints(FoxGloveFaceGen, FoxGloveWig, FoxGloveDonorHair,
            WigHairTintMode.Never, RaceMenuShipped, FoxGloveCarrier, RecordHairColor);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, NeutralTint));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Which wigs the mode can affect at all
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void HasHairTintShape_IsTrueForAHairTintWig()
    {
        if (HpnoMissing) return;

        NifHandler.HasHairTintShape(HpnoWig).Should().BeTrue();
    }

    [Fact]
    public void HasHairTintShape_IsFalseForAMissingFile()
    {
        NifHandler.HasHairTintShape(Path.Combine(Path.GetTempPath(), "npc2-no-such-wig.nif")).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Non-wig geometry must never be re-tinted
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Always_DoesNotRetintTheDonorFaceGensOwnHairTintShapes()
    {
        if (HpnoMissing) return;

        // 00013261's brows and beard are HairTint shapes too. They are the
        // NPC's existing FaceGen geometry, not the baked wig, and the bake must
        // leave them exactly as the CK wrote them.
        string tempFaceGen = CopyToTemp(HpnoFaceGen, out string tempDir);
        try
        {
            var renames = BuildRenames(NifHandler.GetRenderShapeNames(HpnoWig));
            NifHandler.BakeWigIntoFaceGen(new NifHandler.WigBakeInstruction(
                tempFaceGen, HpnoWig, renames, Array.Empty<string>(), null,
                SynthesizeHairPartitionIfNoDonor: true,
                ApplyHairTint: true,
                HairTintRgb: RecordHairColor));

            var donorTints = ReadHairTints(tempFaceGen, name => !renames.ContainsValue(name));

            donorTints.Should().NotBeEmpty("the FaceGen carries HairTint brows/beard");
            donorTints.Should().OnlyContain(t => Approximately(t, HpnoFaceGenHairTint));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Bakes onto a temp copy and returns the resulting hairTintColor of
    /// every BAKED WIG shape (matched by its minted name). Whether the bake tints is
    /// resolved the way the converter resolves it: the mode, the RaceMenu state and
    /// the slots of the armor and armor addon the wig was worn through.</summary>
    private static List<(float R, float G, float B)> BakeAndReadWigTints(
        string faceGenNif, string wigNif, IReadOnlyCollection<string> donorHairShapes,
        WigHairTintMode mode, RaceMenuHairTintState raceMenu, (uint Armor, uint Addon) carrier,
        (float R, float G, float B)? hairTintRgb,
        bool synthesizePartition = false)
    {
        string tempFaceGen = CopyToTemp(faceGenNif, out string tempDir);
        try
        {
            var renames = BuildRenames(NifHandler.GetRenderShapeNames(wigNif));
            int baked = NifHandler.BakeWigIntoFaceGen(new NifHandler.WigBakeInstruction(
                tempFaceGen, wigNif, renames, donorHairShapes, null,
                SynthesizeHairPartitionIfNoDonor: synthesizePartition,
                ApplyHairTint: WigHairTintDecision.ShouldTint(mode, raceMenu, carrier.Armor, carrier.Addon),
                HairTintRgb: hairTintRgb));
            baked.Should().BeGreaterThan(0, "the bake must succeed for the tint assertions to mean anything");

            return ReadHairTints(tempFaceGen, renames.ContainsValue);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    /// <summary>hairTintColor of every HairTint shape whose name passes
    /// <paramref name="nameFilter"/>.</summary>
    private static List<(float R, float G, float B)> ReadHairTints(
        string nifPath, Func<string, bool> nameFilter)
    {
        var tints = new List<(float R, float G, float B)>();
        var nif = new NiflySharp.NifFile();
        nif.Load(nifPath).Should().Be(0);
        foreach (var shape in nif.GetShapes())
        {
            string? name = shape.Name?.String;
            if (string.IsNullOrEmpty(name) || !nameFilter(name)) continue;
            if (nif.GetBlock<NiflySharp.Blocks.BSLightingShaderProperty>(shape.ShaderPropertyRef) is not { } bslsp)
                continue;
            if ((uint)bslsp.ShaderType_SK_FO4 != 6) continue; // BSLSP_HAIRTINT
            var tint = bslsp.HairTintColor;
            tints.Add((tint.R, tint.G, tint.B));
        }
        return tints;
    }

    private static string CopyToTemp(string sourceNif, out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "npc2-wigtint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dest = Path.Combine(tempDir, Path.GetFileName(sourceNif));
        File.Copy(sourceNif, dest);
        return dest;
    }

    private static Dictionary<string, string> BuildRenames(IEnumerable<string> renderShapeNames) =>
        renderShapeNames.ToDictionary(
            n => n,
            n => "NPC2Wig_Test_" + new string(n.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray()),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>hairTintColor is stored as float32 but authored from 8-bit
    /// channels, so compare with a tolerance below one 1/255 step.</summary>
    private static bool Approximately((float R, float G, float B) a, (float R, float G, float B) b) =>
        Math.Abs(a.R - b.R) < 0.002f && Math.Abs(a.G - b.G) < 0.002f && Math.Abs(a.B - b.B) < 0.002f;
}
