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
/// RaceMenu's skee64 (<c>bEnableTintHairSlot</c>), never by the engine, so wig
/// meshes routinely ship a dark PLACEHOLDER <c>hairTintColor</c>. Baking such a
/// wig into the FaceGen takes it out of skee64's reach and would freeze the
/// placeholder — the bake writes the NPC's hair color in instead.
///
/// <para>Two real specimens anchor the modes, one per authoring style:</para>
/// <list type="bullet">
/// <item>High Poly NPC Overhaul's KS Hairdos wigs — HairTint shader, placeholder
/// tint (0.1333, 0.1333, 0.1333), greyscale-ish texture. Black hair in game
/// without RaceMenu; the case this feature exists for.</item>
/// <item>FoxGlove Auri's wig — HairTint shader, NEUTRAL tint (1, 1, 1) over a
/// pre-colored red texture. Auto must leave it alone.</item>
/// </list>
/// Machine-local: gracefully skips when a specimen isn't installed (suite
/// convention). Works on temp copies; source files are never modified.
/// </summary>
public class NifHandlerWigHairTintTests
{
    // --- FoxGlove Auri: neutral-tint wig over a pre-colored texture ---
    private const string FoxGloveModRoot =
        @"S:\Skyrim NPC Selection\mods\FoxGlove - Auri Visual Overhaul - The FoxGlove - Classic Red - No Warpaint - Test";
    private const string FoxGloveFaceGen =
        FoxGloveModRoot + @"\meshes\actors\character\FaceGenData\FaceGeom\018auri.esp\00000D63.NIF";
    private const string FoxGloveWig =
        FoxGloveModRoot + @"\meshes\actors\FoxGlove Auri\Wig\22a_1.nif";
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
    public void Auto_OverwritesPlaceholderTint_WithTheFaceGensOwnHairColor()
    {
        if (HpnoMissing) return;

        // The FaceGen's own hair color wins over the supplied record value, so
        // the wig ends up the same color as this NPC's brows and beard.
        var tints = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            WigHairTintMode.Auto, RecordHairColor, synthesizePartition: true);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, HpnoFaceGenHairTint),
            "a placeholder tint is exactly what RaceMenu would have replaced in game");
    }

    [Fact]
    public void Always_OverwritesPlaceholderTint_WithTheFaceGensOwnHairColor()
    {
        if (HpnoMissing) return;

        var tints = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            WigHairTintMode.Always, RecordHairColor, synthesizePartition: true);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, HpnoFaceGenHairTint));
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

        var tints = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            WigHairTintMode.Never, RecordHairColor, synthesizePartition: true);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, HpnoPlaceholderTint),
            "Never is the pre-feature behavior — the wig keeps whatever its author shipped");
    }

    [Fact]
    public void NoHairColorRecord_StillUsesTheFaceGenHairTint()
    {
        if (HpnoMissing) return;

        var tints = BakeAndReadWigTints(HpnoFaceGen, HpnoWig, Array.Empty<string>(),
            WigHairTintMode.Auto, hairTintRgb: null, synthesizePartition: true);

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
            WigHairTintMode.Always, RecordHairColor, synthesizePartition: true);

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

    [Fact]
    public void Auto_LeavesNeutralWhiteTintUntouched()
    {
        if (FoxGloveMissing) return;

        var tints = BakeAndReadWigTints(FoxGloveFaceGen, FoxGloveWig, FoxGloveDonorHair,
            WigHairTintMode.Auto, RecordHairColor);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, NeutralTint),
            "a neutral tint is a no-op multiply — the author pre-colored the texture");
    }

    [Fact]
    public void Always_OverwritesEvenANeutralWhiteTint()
    {
        if (FoxGloveMissing) return;

        var tints = BakeAndReadWigTints(FoxGloveFaceGen, FoxGloveWig, FoxGloveDonorHair,
            WigHairTintMode.Always, RecordHairColor);

        tints.Should().NotBeEmpty();
        tints.Should().OnlyContain(t => Approximately(t, FoxGloveFaceGenHairTint),
            "Always mirrors skee64, which tints every hair-slot HairTint item");
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
                HairTintMode: WigHairTintMode.Always,
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
    /// every BAKED WIG shape (matched by its minted name).</summary>
    private static List<(float R, float G, float B)> BakeAndReadWigTints(
        string faceGenNif, string wigNif, IReadOnlyCollection<string> donorHairShapes,
        WigHairTintMode mode, (float R, float G, float B)? hairTintRgb,
        bool synthesizePartition = false)
    {
        string tempFaceGen = CopyToTemp(faceGenNif, out string tempDir);
        try
        {
            var renames = BuildRenames(NifHandler.GetRenderShapeNames(wigNif));
            int baked = NifHandler.BakeWigIntoFaceGen(new NifHandler.WigBakeInstruction(
                tempFaceGen, wigNif, renames, donorHairShapes, null,
                SynthesizeHairPartitionIfNoDonor: synthesizePartition,
                HairTintMode: mode,
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
        using var nif = new nifly.NifFile();
        nif.Load(nifPath).Should().Be(0);
        var header = nif.GetHeader();
        using var shapes = nif.GetShapes();
        foreach (var shape in shapes)
        {
            string? name = shape.name?.get();
            if (string.IsNullOrEmpty(name) || !nameFilter(name)) continue;
            var shaderRef = shape.ShaderPropertyRef();
            if (shaderRef == null || shaderRef.IsEmpty()) continue;
            if (header.GetBlockById(shaderRef.index) is not nifly.BSLightingShaderProperty bslsp) continue;
            if (bslsp.bslspShaderType != 6) continue; // BSLSP_HAIRTINT
            var tint = bslsp.hairTintColor;
            if (tint != null) tints.Add((tint.x, tint.y, tint.z));
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
