using System.Collections.Concurrent;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>The result of running the headless patcher for one golden combo.</summary>
internal sealed class GoldenPatchResult
{
    public required string OutputDirectory { get; init; }
    /// <summary>Target NPC FormKeys that passed screening and were patched (post validator drops).</summary>
    public required IReadOnlyList<FormKey> PatchedTargets { get; init; }
    /// <summary>Selections the validator rejected (e.g. Create-mode appearance swaps), human-readable.</summary>
    public required IReadOnlyList<string> InvalidSelections { get; init; }
    public required string Log { get; init; }
}

/// <summary>
/// Drives the real <see cref="Patcher"/> headlessly through the same sequence as
/// <c>VM_Run.ExecutePatchingAsync</c> (BuildModSettingsMap -&gt; PreInit -&gt; ScreenSelections -&gt;
/// fresh OutputMod -&gt; RunPatchingLogic -&gt; WriteUnifiedTokenFile), against a supplied environment and
/// settings, writing the output plugin + assets (+ SkyPatcher .ini) into the settings' output directory.
/// </summary>
internal static class GoldenPatchRunner
{
    /// <param name="writeUnifiedToken">
    /// When false, the final <see cref="Patcher.WriteUnifiedTokenFile"/> enrichment is skipped, leaving
    /// only the bootstrap NPC_Token.json marker that <see cref="Patcher.RunPatchingLogic"/> writes up
    /// front. This reproduces the on-disk state a crash (or swallowed save failure) between the plugin
    /// write and the unified-token write would leave behind, so tests can assert the marker still exists.
    /// </param>
    public static async Task<GoldenPatchResult> RunAsync(
        EnvironmentStateProvider provider, Settings settings, bool writeUnifiedToken = true)
    {
        // The TextFileFormKeyAllocator persists EditorID->FormKey assignments to disk and is committed at
        // the end of a run; delete it so each combo allocates from a clean slate (deterministic per run).
        try
        {
            var allocatorPath = provider.GetAllocatorPath();
            if (System.IO.File.Exists(allocatorPath)) System.IO.File.Delete(allocatorPath);
        }
        catch { /* best effort */ }

        using var harness = new NpcChooserHarness(provider, settings);
        var patcher = harness.Patcher;
        var validator = harness.Validator;

        var log = new ConcurrentQueue<string>();
        void Append(string msg, bool isError, bool force) => log.Enqueue(msg);
        patcher.ConnectToUILogger(Append, (_, _, _) => { }, () => { }, () => { });
        validator.ConnectToUILogger(Append, (_, _, _) => { }, () => { }, () => { });

        var modSettingsMap = patcher.BuildModSettingsMap();
        await patcher.PreInitializationLogicAsync();

        var report = await validator.ScreenSelectionsAsync(modSettingsMap, "<All NPCs>", CancellationToken.None);

        // Mirror VM_Run: recreate a fresh output mod immediately before the patch run.
        provider.OutputMod = new SkyrimMod(
            ModKey.FromName(provider.OutputPluginName, ModType.Plugin), provider.SkyrimVersion);

        var validSelections = validator.GetScreeningCache()
            .Where(kv => kv.Value.SelectionIsValid)
            .ToList();

        await patcher.RunPatchingLogic(validSelections, showFinalMessage: false, isFirstIteration: true,
            CancellationToken.None);
        if (writeUnifiedToken)
        {
            patcher.WriteUnifiedTokenFile();
        }

        return new GoldenPatchResult
        {
            OutputDirectory = settings.OutputDirectory,
            PatchedTargets = validSelections.Select(kv => kv.Key).ToList(),
            InvalidSelections = report.InvalidSelections,
            Log = string.Join(Environment.NewLine, log),
        };
    }
}
