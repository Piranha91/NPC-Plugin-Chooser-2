using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Exercises <see cref="NifHandler.RemoveShapesByName"/> against the real
/// FoxGlove Auri FaceGen specimen (the wig-forwarding baked-hair strip).
/// Machine-local: gracefully skips when the specimen mod isn't installed,
/// following the suite's Skyrim-integration convention. Works on a temp copy;
/// the source NIF is never modified.
/// </summary>
public class NifHandlerWigShapeTests
{
    private const string FoxGloveFaceGenNif =
        @"S:\Skyrim NPC Selection\mods\FoxGlove - Auri Visual Overhaul - The FoxGlove - Classic Red - No Warpaint - Test\meshes\actors\character\FaceGenData\FaceGeom\018auri.esp\00000D63.NIF";

    [Fact]
    public void RemoveShapesByName_StripsHairShapes_AndPreservesTheRest()
    {
        if (!File.Exists(FoxGloveFaceGenNif)) return; // specimen not installed on this machine

        string temp = Path.Combine(Path.GetTempPath(), "npc2-wigtest-" + Guid.NewGuid().ToString("N") + ".nif");
        File.Copy(FoxGloveFaceGenNif, temp);
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "FoxGloveHairMesh", "FoxGloveHairlineMesh",
            };

            int removed = NifHandler.RemoveShapesByName(temp, names);
            removed.Should().Be(2);

            using var nif = new nifly.NifFile();
            nif.Load(temp).Should().Be(0, "the stripped NIF must remain loadable");
            var remaining = new List<string>();
            using (var shapes = nif.GetShapes())
            {
                foreach (var shape in shapes)
                {
                    string? name = shape.name?.get();
                    if (!string.IsNullOrEmpty(name)) remaining.Add(name);
                }
            }

            remaining.Should().NotContain("FoxGloveHairMesh");
            remaining.Should().NotContain("FoxGloveHairlineMesh");
            // Head, eyes, brows, mouth, and the scalp (deliberately kept — bald
            // scalp under the wig is exactly the bald-FaceGen + wig pattern).
            remaining.Should().Contain("FoxGloveHead");
            remaining.Should().Contain("FoxGloveEyeMesh");
            remaining.Should().Contain("FoxGloveHairScalp");

            // Idempotent second pass: nothing left to remove, file untouched.
            NifHandler.RemoveShapesByName(temp, names).Should().Be(0);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
