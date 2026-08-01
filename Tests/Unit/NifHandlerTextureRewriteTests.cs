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
        @"S:\Skyrim NPC Selection\mods\FoxGlove - Auri Visual Overhaul - The FoxGlove - Classic Red - No Warpaint - Test\meshes\actors\character\FaceGenData\FaceGeom\018auri.esp\00000D63.NIF";

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
}
