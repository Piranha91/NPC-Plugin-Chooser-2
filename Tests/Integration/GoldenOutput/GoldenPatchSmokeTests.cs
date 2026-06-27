using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// End-to-end smoke test: runs the headless patcher for a single combo and verifies it produces an output
/// plugin, a token file and assets. Proves the full pipeline (env -&gt; screening -&gt; RunPatchingLogic -&gt;
/// asset copy -&gt; save) works headlessly before the full comparison suite relies on it.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class GoldenPatchSmokeTests
{
    private readonly ITestOutputHelper _output;
    public GoldenPatchSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task CreateAndPatch_Include_ProducesPluginTokenAndAssets()
    {
        if (!GoldenOutputConfig.TryLoad(out var config, out var skip))
        {
            _output.WriteLine("SKIPPED: " + skip);
            return;
        }

        var combo = GoldenCombos.All.First(c => c.Index == 2); // CreateAndPatch / Include / non-SkyPatcher
        var outDir = Path.Combine(Path.GetTempPath(), "NpcGoldenSmoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);

        try
        {
            if (!GoldenEnvironmentBuilder.TryBuild(config!, outDir, "NPC", out var env, out var envSkip))
            {
                _output.WriteLine("SKIPPED: " + envSkip);
                return;
            }

            var settings = GoldenComboSettingsBuilder.Build(config!, combo, outDir);
            var result = await GoldenPatchRunner.RunAsync(env!.Provider, settings);

            _output.WriteLine($"Patched {result.PatchedTargets.Count} targets; {result.InvalidSelections.Count} invalid.");
            foreach (var inv in result.InvalidSelections) _output.WriteLine("  INVALID: " + inv);

            var pluginPath = Path.Combine(outDir, "NPC.esp");
            var tokenPath = Path.Combine(outDir, "NPC_Token.json");

            File.Exists(pluginPath).Should().BeTrue("the output plugin must be written. Log tail:\n" + Tail(result.Log));
            File.Exists(tokenPath).Should().BeTrue("the token file must be written");

            // CreateAndPatch keeps all 8 selections (no validator drop).
            result.PatchedTargets.Should().HaveCount(8);

            var meshes = Directory.Exists(Path.Combine(outDir, "meshes"))
                ? Directory.GetFiles(Path.Combine(outDir, "meshes"), "*", SearchOption.AllDirectories).Length : 0;
            var textures = Directory.Exists(Path.Combine(outDir, "textures"))
                ? Directory.GetFiles(Path.Combine(outDir, "textures"), "*", SearchOption.AllDirectories).Length : 0;
            _output.WriteLine($"Assets: {meshes} meshes, {textures} textures.");
            (meshes + textures).Should().BeGreaterThan(0, "appearance assets should have been copied");

            result.Log.Should().NotContain("FATAL SAVE ERROR");
        }
        finally
        {
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { /* best effort */ }
        }
    }

    private static string Tail(string log)
    {
        var lines = log.Split('\n');
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 25)));
    }
}
