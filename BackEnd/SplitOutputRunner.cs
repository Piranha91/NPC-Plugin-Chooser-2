using System.IO;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Runs the patcher over a set of screened selections, honouring the user-chosen output split
/// (<see cref="Settings.SplitOutput"/> by gender, race and/or NPC count): the selections are
/// grouped, and the patcher runs once per group into a plugin of its own
/// (<c>&lt;output name&gt;_&lt;suffix&gt;.esp</c>). With the split off it runs once into the one
/// output plugin.
///
/// <para>This used to live in <c>VM_Run</c>, so only the UI split; headless callers drove
/// <see cref="Patcher.RunPatchingLogic"/> directly and always wrote a single plugin. It is the
/// same work, relocated, so that both go through one implementation.</para>
///
/// <para>Unrelated to <see cref="Settings.AutoSplitOutput"/>, which is Mutagen's master-limit
/// split inside a single save and stays in the <see cref="Patcher"/>.</para>
/// </summary>
public class SplitOutputRunner : OptionalUIModule
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Settings _settings;
    private readonly Patcher _patcher;

    public SplitOutputRunner(EnvironmentStateProvider environmentStateProvider, Settings settings, Patcher patcher)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
        _patcher = patcher;
    }

    /// <summary>One output plugin's worth of selections. An empty <paramref name="Suffix"/> means
    /// the plugin keeps the plain output name.</summary>
    public record PatchingBatch(string Suffix, List<KeyValuePair<FormKey, ScreeningResult>> Selections);

    /// <summary>
    /// Starts from a fresh output mod, runs the patcher once (split off, or nothing to patch) or
    /// once per batch (split on), then writes the unified NPC token file.
    /// </summary>
    /// <param name="writeUnifiedTokenFile">
    /// False skips the final <see cref="Patcher.WriteUnifiedTokenFile"/>, leaving only the bootstrap
    /// marker the first patcher run writes: the on-disk state of a run that died before finishing.
    /// The app always passes true.
    /// </param>
    public async Task RunAsync(List<KeyValuePair<FormKey, ScreeningResult>> validSelections,
        CancellationToken token, bool writeUnifiedTokenFile = true)
    {
        // Reiniitialize the output mod in case the user is re-running the patcher in the current session
        _environmentStateProvider.OutputMod = new SkyrimMod(
            ModKey.FromName(_environmentStateProvider.OutputPluginName, ModType.Plugin), _environmentStateProvider.SkyrimVersion);

        if (_settings.SplitOutput && validSelections.Any())
        {
            AppendLog($"\nSplitting output based on user settings...", forceLog: true);
            var batches = CreatePatchingBatches(validSelections);
            AppendLog($"Created {batches.Count} patching batches.", forceLog: true);

            // Each batch writes its own plugin and its own SkyPatcher/Recast config. The patcher needs
            // the whole run's selections so that a batch does not write an entry for an NPC that
            // another batch patches itself (Patcher.MirrorAppearanceOntoInheritingNpcs).
            var selectionsOfWholeRun = validSelections.Select(kv => kv.Key).ToHashSet();

            for (int i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                token.ThrowIfCancellationRequested();

                string originalPluginName =
                    Path.GetFileNameWithoutExtension(_environmentStateProvider.OutputPluginName);
                string newPluginName = string.IsNullOrWhiteSpace(batch.Suffix)
                    ? originalPluginName
                    : $"{originalPluginName}_{batch.Suffix}";

                AppendLog(
                    $"\n--- Processing Batch {i + 1}/{batches.Count}: {newPluginName} ({batch.Selections.Count} NPCs) ---",
                    forceLog: true);

                // Update environment with the new output mod name for this batch run
                _environmentStateProvider.OutputMod = new SkyrimMod(
                    ModKey.FromName(newPluginName, ModType.Plugin), _environmentStateProvider.SkyrimVersion);

                // Call patcher with just the NPCs for this specific batch
                await _patcher.RunPatchingLogic(batch.Selections, false, i == 0, token, selectionsOfWholeRun);
            }
        }
        else
        {
            // If not splitting, run the patcher once with all valid selections
            await _patcher.RunPatchingLogic(validSelections, false, true, token);
        }

        // Write unified NPC token file after all batches are complete
        if (writeUnifiedTokenFile)
        {
            _patcher.WriteUnifiedTokenFile();
        }
    }

    /// <summary>
    /// Creates virtual groups ("batches") of NPCs to be processed into separate plugin files.
    /// </summary>
    public List<PatchingBatch> CreatePatchingBatches(List<KeyValuePair<FormKey, ScreeningResult>> validSelections)
    {
        var batches = new List<PatchingBatch>();
        if (!validSelections.Any()) return batches;

        // Group selections by the chosen criteria (gender and/or race).
        var groupedByCriteria = validSelections.GroupBy(kvp =>
        {
            var npc = kvp.Value.WinningNpcOverride;

            string genderKey = _settings.SplitOutputByGender ? Auxilliary.GetGender(npc).ToString() : string.Empty;
            string raceKey = string.Empty;
            if (_settings.SplitOutputByRace)
            {
                raceKey = npc.Race.TryResolve(_environmentStateProvider.LinkCache, out var raceRecord)
                    ? (raceRecord.EditorID ?? "UnknownRace")
                    : "UnknownRace";
            }

            // Sanitize raceKey to be filename-friendly
            raceKey = Regex.Replace(raceKey, @"[^a-zA-Z0-9]", "");

            return (Gender: genderKey, Race: raceKey);
        });

        int maxNpcsPerPlugin = _settings.SplitOutputMaxNpcs ?? int.MaxValue;

        // Process each criteria group (e.g., all Male Nords).
        foreach (var group in groupedByCriteria)
        {
            var npcsInGroup = group.ToList();
            int totalInGroup = npcsInGroup.Count;
            int currentOffset = 0;
            int subBatchCounter = 1;

            // Sub-divide the criteria group by the max number of NPCs.
            while (currentOffset < totalInGroup)
            {
                var chunk = npcsInGroup.Skip(currentOffset).Take(maxNpcsPerPlugin).ToList();

                var nameParts = new List<string>();
                if (_settings.SplitOutputByGender && !string.IsNullOrEmpty(group.Key.Gender))
                    nameParts.Add(group.Key.Gender);
                if (_settings.SplitOutputByRace && !string.IsNullOrEmpty(group.Key.Race))
                    nameParts.Add(group.Key.Race);

                // Only add a numeric suffix if the group was large enough to be split.
                if (totalInGroup > maxNpcsPerPlugin)
                {
                    nameParts.Add(subBatchCounter.ToString());
                }

                string batchSuffix = string.Join("_", nameParts.Where(s => !string.IsNullOrEmpty(s)));

                batches.Add(new PatchingBatch(batchSuffix, chunk));

                currentOffset += maxNpcsPerPlugin;
                subBatchCounter++;
            }
        }

        return batches;
    }
}
