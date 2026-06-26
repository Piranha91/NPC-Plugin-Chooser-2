using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Stands up NPC2's real <see cref="EnvironmentStateProvider"/> headlessly against the
/// machine's Skyrim SE install, or reports a skip reason when no usable environment can
/// be resolved (so integration tests pass as no-ops on a machine without Skyrim SE — the
/// same graceful-skip contract as SynthEBD.Tests' TestEnvironmentStateProvider).
///
/// <para>NPC2's provider is a concrete <c>ReactiveObject</c>, not an interface, so unlike
/// SynthEBD we construct the real one and drive it through its public pipeline
/// (<c>SetEnvironmentTarget</c> + <c>UpdateEnvironment</c>). For pure guard/early-return
/// assertions that must NOT touch the game, use <see cref="Invalid"/> instead — it builds
/// a provider with no environment (Status == Invalid, LinkCache null) and needs no install.</para>
/// </summary>
public sealed class NpcChooserTestEnvironment : IDisposable
{
    public EnvironmentStateProvider Provider { get; }

    private NpcChooserTestEnvironment(EnvironmentStateProvider provider) => Provider = provider;

    /// <summary>
    /// A provider that has NOT resolved a game environment: <c>Status == Invalid</c>,
    /// <c>LinkCache == null</c>, <c>LoadOrder</c> empty. Needs no Skyrim install — the
    /// workhorse for guard/early-return tests. <paramref name="setTarget"/> seeds
    /// <c>SkyrimVersion</c> (some code paths read it) without building an environment.
    /// </summary>
    public static EnvironmentStateProvider Invalid(bool setTarget = true, string outputPluginName = "NPCTest")
    {
        var p = new EnvironmentStateProvider(null);
        if (setTarget) p.SetEnvironmentTarget(SkyrimRelease.SkyrimSE, string.Empty, outputPluginName);
        return p;
    }

    /// <summary>
    /// Tries to build a valid, real Skyrim SE environment. Returns false with a human-readable
    /// <paramref name="skipReason"/> when none is available. On success the provider's
    /// <c>OutputMod</c> is a fresh in-memory plugin named <paramref name="outputPluginName"/>.
    /// </summary>
    public static bool TryBuild(out NpcChooserTestEnvironment? env, out string skipReason,
        string outputPluginName = "NPCTest")
    {
        env = null;
        skipReason = string.Empty;
        try
        {
            var p = new EnvironmentStateProvider(null);
            // Empty data-folder path => provider auto-detects the install (registry/Steam).
            p.SetEnvironmentTarget(SkyrimRelease.SkyrimSE, string.Empty, outputPluginName);
            p.UpdateEnvironment();

            if (p.Status != EnvironmentStateProvider.EnvironmentStatus.Valid
                || p.LinkCache == null
                || p.LoadOrder == null
                || p.LoadOrder.ListedOrder.Count() <= 1)
            {
                skipReason = "No valid Skyrim SE environment resolved" +
                             (string.IsNullOrEmpty(p.EnvironmentBuilderError) ? "." : ": " + p.EnvironmentBuilderError);
                return false;
            }

            // UpdateEnvironment already created an OutputMod; ensure it is named as requested.
            p.OutputMod ??= new SkyrimMod(ModKey.FromName(outputPluginName, ModType.Plugin), SkyrimRelease.SkyrimSE);
            env = new NpcChooserTestEnvironment(p);
            return true;
        }
        catch (Exception ex)
        {
            skipReason = "Could not build a Skyrim SE environment: " + ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        // EnvironmentStateProvider exposes no disposal handle; the GameEnvironment it holds
        // is released when the provider is collected. Nothing to release here.
    }
}
