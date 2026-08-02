using System.IO;
using System.Linq;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Exercises the <c>DrawnInGame</c> flag of <see cref="NifHandler.GetTexturesByShape"/>, which gates
/// the textureless-shape warning (<see cref="NpcWarningKind.TexturelessShapes"/>).
///
/// <para>Specimen: High Poly NPC Overhaul, which dresses every NPC in a bald head part plus a wig.
/// Four of its 3025 FaceGen heads still carry the hair geometry from an earlier export, hidden at
/// material alpha 0 and textured from a hair the mod no longer ships; the wig actually worn is a
/// different, properly shipped hair. Every shape below therefore names an unresolvable texture, but
/// in game only the wig rendered untextured. That split is what this flag has to reproduce:
/// suppress the hidden leftovers, keep the wig reportable.</para>
///
/// <para>Machine-local; skips green when the specimen mod isn't installed, following the suite's
/// Skyrim-integration convention.</para>
/// </summary>
public class NifHandlerShapeVisibilityTests
{
    private const string ModsRoot = @"S:\Skyrim NPC Selection\mods";

    /// <summary>Adrianne Avenicci's FaceGen head — hair baked in at material alpha 0.</summary>
    private const string FaceGenSpecimen = ModsRoot +
        @"\High Poly NPC Overhaul - Skyrim Special Edition 2.0 (All Vanilla NPCs)\meshes\actors\character\FaceGenData\FaceGeom\Skyrim.esm\00013BB9.NIF";

    /// <summary>The wig Uglarz actually wears — same missing-texture family, but drawn.</summary>
    private const string WigSpecimen = ModsRoot +
        @"\High Poly NPC Overhaul - Resources\meshes\Wigs\Female\Jackdaw.nif";

    [Fact]
    public void FaceGenHairAtZeroMaterialAlpha_IsNotDrawnInGame()
    {
        if (!File.Exists(FaceGenSpecimen)) return; // specimen not installed on this machine

        var byShape = NifHandler.GetTexturesByShape(FaceGenSpecimen);
        byShape.Should().NotBeEmpty();

        // The two hair shapes that named the unresolvable KS Hairdos textures.
        foreach (var hairShape in new[] { "0Victorian", "0VictorianHL" })
        {
            var shape = byShape.SingleOrDefault(s => s.ShapeName == hairShape);
            shape.ShapeName.Should().Be(hairShape, "the specimen must still contain '{0}'", hairShape);
            shape.TexturePaths.Should().Contain(p => p.Contains("victorian", System.StringComparison.OrdinalIgnoreCase));
            shape.DrawnInGame.Should().BeFalse(
                "'{0}' sits at material alpha 0 under an NiAlphaProperty — the wig supplies the visible hair", hairShape);
        }

        // The face itself is opaque and must stay reportable, or the exemption is too broad.
        byShape.Single(s => s.ShapeName == "00KLH_FemaleHeadImperial").DrawnInGame.Should().BeTrue();
    }

    [Fact]
    public void WornWigShapes_AreDrawnInGame()
    {
        if (!File.Exists(WigSpecimen)) return; // specimen not installed on this machine

        var byShape = NifHandler.GetTexturesByShape(WigSpecimen);
        byShape.Should().NotBeEmpty();

        // Both shapes are alpha-blended/alpha-tested like the FaceGen hair, but at material alpha 1:
        // transparency machinery alone must never suppress a shape.
        foreach (var wigShape in new[] { "Hair", "HairLine" })
        {
            var shape = byShape.SingleOrDefault(s => s.ShapeName == wigShape);
            shape.ShapeName.Should().Be(wigShape, "the specimen must still contain '{0}'", wigShape);
            shape.DrawnInGame.Should().BeTrue(
                "'{0}' is worn geometry at full material alpha — a missing texture here really does show", wigShape);
        }
    }
}
