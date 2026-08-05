using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Exercises <see cref="NifHandler.RewriteTexturePaths"/> — the Include-As-New asset isolation's
/// in-NIF rewrite (an isolated mesh copy must reference the isolated copies of the textures its
/// mod ships, or it keeps pulling whatever wins the shared path). Machine-local: reuses the
/// FoxGlove specimen the wig-shape tests use and gracefully skips when it isn't installed,
/// following the suite's Skyrim-integration convention. Works on a temp copy; the source NIF is
/// never modified.
/// </summary>
public class NifHandlerTextureRewriteTests
{
    private const string SpecimenNif =
        @"S:\Skyrim NPC Selection\mods\FoxGlove - Auri Visual Overhaul - The FoxGlove - Classic Red - No Warpaint (Default)\meshes\actors\character\FaceGenData\FaceGeom\018auri.esp\00000D63.NIF";

    [Fact]
    public void RewriteTexturePaths_RewritesMatchedSlots_AndLeavesTheRest()
    {
        if (!File.Exists(SpecimenNif)) return; // specimen not installed on this machine

        string temp = Path.Combine(Path.GetTempPath(), "npc2-texrewrite-" + Guid.NewGuid().ToString("N") + ".nif");
        File.Copy(SpecimenNif, temp);
        try
        {
            var before = NifHandler.GetTexturesByShape(temp)
                .SelectMany(s => s.TexturePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            before.Should().NotBeEmpty("the specimen must reference textures for this test to mean anything");

            // Re-point exactly one texture the way isolation would; every other slot must survive.
            string victim = before[0];
            string isolated = AssetHandler.InsertIsolationPrefix(victim, "IsolationTest");
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [victim] = isolated,
            };

            int rewritten = NifHandler.RewriteTexturePaths(temp, map);
            rewritten.Should().BeGreaterThan(0, "the victim path exists in at least one slot");

            var after = NifHandler.GetTexturesByShape(temp)
                .SelectMany(s => s.TexturePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            after.Should().Contain(p => string.Equals(p, isolated, StringComparison.OrdinalIgnoreCase));
            after.Should().NotContain(p => string.Equals(p, victim, StringComparison.OrdinalIgnoreCase));
            foreach (var untouched in before.Skip(1))
            {
                after.Should().Contain(p => string.Equals(p, untouched, StringComparison.OrdinalIgnoreCase),
                    "slots not named in the map must survive the rewrite verbatim");
            }

            // No matches -> no rewrite, and the report says so.
            NifHandler.RewriteTexturePaths(temp, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["textures\\does\\not\\exist.dds"] = "textures\\nor\\this.dds",
            }).Should().Be(0);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>
    /// The appearance-share half of the tint story: a FaceGen NIF delivered under a different
    /// NPC's FormKey must have its baked face-tint slot re-pointed at the tint file delivered
    /// beside it, because the engine reads the tint's path from the NIF, not from the FormID
    /// convention. Matching must survive slash-spelling differences (mods bake either).
    /// </summary>
    [Fact]
    public void RewriteCopiedFaceTintPath_RepointsDonorTintToTargetNpc()
    {
        if (!File.Exists(SpecimenNif)) return; // specimen not installed on this machine

        string temp = Path.Combine(Path.GetTempPath(), "npc2-facetintrewrite-" + Guid.NewGuid().ToString("N") + ".nif");
        File.Copy(SpecimenNif, temp);
        try
        {
            var before = NifHandler.GetTexturesByShape(temp)
                .SelectMany(s => s.TexturePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string? donorTint = before.FirstOrDefault(p =>
                p.Replace('/', '\\').Contains(@"\facegendata\facetint\", StringComparison.OrdinalIgnoreCase));
            if (donorTint == null) return; // specimen has no baked tint slot; nothing to exercise

            // Bake a forward-slash spelling into the NIF so the match below has to go through
            // regularization rather than exact string equality.
            string storedDonorTint = donorTint.Replace('\\', '/');
            if (!string.Equals(storedDonorTint, donorTint, StringComparison.Ordinal))
            {
                NifHandler.RewriteTexturePaths(temp,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [donorTint] = storedDonorTint,
                    }).Should().BeGreaterThan(0);
            }

            string targetTint = Path.Combine(
                Path.GetDirectoryName(donorTint.Replace('/', '\\'))!, "00013b99.dds");

            AssetHandler.RewriteCopiedFaceTintPath(temp, donorTint, targetTint)
                .Should().BeGreaterThan(0, "equivalent slash spellings must still identify the donor tint");

            var after = NifHandler.GetTexturesByShape(temp)
                .SelectMany(s => s.TexturePaths)
                .ToList();
            after.Should().Contain(p => string.Equals(p, targetTint, StringComparison.OrdinalIgnoreCase));
            after.Should().NotContain(p => string.Equals(p, storedDonorTint, StringComparison.OrdinalIgnoreCase));
            foreach (var untouched in before.Where(p => !string.Equals(p, donorTint, StringComparison.OrdinalIgnoreCase)))
            {
                after.Should().Contain(p => string.Equals(p, untouched, StringComparison.OrdinalIgnoreCase),
                    "only the tint slot may change; every other texture must survive verbatim");
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void RewriteCopiedFaceTintPath_NoOpCases_TouchNoFile()
    {
        const string tint = @"textures\actors\character\facegendata\facetint\skyrim.esm\000a2c8e.dds";

        // Same source and destination: the NPC keeps its own FaceGen path — no NIF load or save.
        AssetHandler.RewriteCopiedFaceTintPath("missing.nif", tint, tint)
            .Should().Be(0);

        // No destination: the standard non-shared copy path.
        AssetHandler.RewriteCopiedFaceTintPath("missing.nif", tint, null)
            .Should().Be(0);

        // Non-NIF destination: the DDS half of a FaceGen pair goes through the same scheduling.
        AssetHandler.RewriteCopiedFaceTintPath("missing.dds", tint,
                @"textures\actors\character\facegendata\facetint\skyrim.esm\00013b99.dds")
            .Should().Be(0);
    }
}
