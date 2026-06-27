using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Verifies the <see cref="GoldenEnvironmentBuilder"/> reproduces the reference-generation environment:
/// the active extras (USSEP, AI Overhaul) are loaded from their mod folders and participate in conflict
/// resolution, and the prior <c>NPC.esp</c> output is trimmed out. Skips gracefully when the local map
/// or a Skyrim SE install is unavailable.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class GoldenEnvironmentTests
{
    private readonly ITestOutputHelper _output;
    public GoldenEnvironmentTests(ITestOutputHelper output) => _output = output;

    // Riverwood NPC: Dorthe (the RS Children Overhaul target). Stable vanilla FormKey.
    private static readonly FormKey Dorthe = FormKey.Factory("013477:Skyrim.esm");
    private static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm");

    private bool TryBuild(out GoldenEnvironment? env, out TempDirGuard? temp, out string skip)
    {
        env = null;
        temp = null;
        skip = string.Empty;
        if (!GoldenOutputConfig.TryLoad(out var config, out skip)) return false;
        temp = new TempDirGuard();
        if (!GoldenEnvironmentBuilder.TryBuild(config!, temp.Path, "NPC", out env, out skip))
        {
            temp.Dispose();
            temp = null;
            return false;
        }
        return true;
    }

    [Fact]
    public void Build_InjectsActiveExtras_AndResolvesValidEnvironment()
    {
        if (!TryBuild(out var env, out var temp, out var skip))
        {
            _output.WriteLine("SKIPPED: " + skip);
            return;
        }
        using (temp)
        {
            var p = env!.Provider;
            p.Status.Should().Be(EnvironmentStateProvider.EnvironmentStatus.Valid);
            p.LoadOrder!.ContainsKey(Skyrim).Should().BeTrue();

            var loaded = p.LoadOrder.ListedOrder.Select(l => l.ModKey).ToHashSet();
            loaded.Should().Contain(ModKey.FromFileName("unofficial skyrim special edition patch.esp"));
            loaded.Should().Contain(ModKey.FromFileName("AI Overhaul.esp"));
            loaded.Should().Contain(ModKey.FromFileName("AI Overhaul - Scripted Addon.esp"));
            loaded.Should().Contain(ModKey.FromFileName("AI Overhaul - USSEP Patch.esp"));

            env.InjectedExtraKeys.Should().HaveCount(4);
            if (env.MissingKeys.Count > 0)
                _output.WriteLine("Missing (non-fatal) vanilla/CC keys: " +
                    string.Join(", ", env.MissingKeys.Select(k => k.FileName.String)));

            _output.WriteLine($"Resolved load order has {p.LoadOrder.ListedOrder.Count()} plugins.");
        }
    }

    [Fact]
    public void PriorNpcEsp_IsTrimmed_FromTheEnvironment()
    {
        if (!TryBuild(out var env, out var temp, out var skip))
        {
            _output.WriteLine("SKIPPED: " + skip);
            return;
        }
        using (temp)
        {
            var p = env!.Provider;
            // The output mod is added by WithOutputMod; it must be the empty in-memory one, proving the
            // active on-disk NPC.esp (a prior output) was trimmed rather than mapped into the link cache.
            p.OutputMod.ModKey.Should().Be(env.OutputKey);
            p.OutputMod.Npcs.Count.Should().Be(0, "the prior NPC.esp output must be trimmed, not loaded");

            // The writable output is held in OutputMod (asserted above), not as a listing. Crucially the
            // prior on-disk NPC.esp contributes NO listing to the resolved order, so its stale records can
            // never win conflict resolution during patching.
            var staleNpcEspListings = p.LoadOrder!.ListedOrder.Count(l => l.ModKey == env.OutputKey);
            staleNpcEspListings.Should().Be(0, "the prior NPC.esp must be trimmed out of the resolved load order");
        }
    }

    [Fact]
    public void Dorthe_WinningOverride_ComesFromTheLoadedOrder_NotAStaleOutput()
    {
        if (!TryBuild(out var env, out var temp, out var skip))
        {
            _output.WriteLine("SKIPPED: " + skip);
            return;
        }
        using (temp)
        {
            var p = env!.Provider;
            p.LinkCache!.TryResolveContext<INpc, INpcGetter>(Dorthe, out var ctx).Should().BeTrue(
                "Dorthe is a vanilla NPC present in the resolved load order");

            var winner = ctx!.ModKey;
            _output.WriteLine($"Dorthe winning override source: {winner.FileName}");
            winner.Should().NotBe(env.OutputKey, "the winner must not be a stale NPC.esp output");

            var loaded = new HashSet<ModKey>(System.Linq.Enumerable.Select(p.LoadOrder!.ListedOrder, l => l.ModKey));
            loaded.Should().Contain(winner);
        }
    }

    /// <summary>Disposable temp directory used as the env's output-mod folder.</summary>
    private sealed class TempDirGuard : IDisposable
    {
        public string Path { get; }
        public TempDirGuard()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NpcGolden_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
