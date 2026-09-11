using Mutagen.Bethesda.Plugins;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NPC_Plugin_Chooser_2.BackEnd;

// Shared with Export so its existing JSON shape stays the import contract.
internal record NpcChoiceDto(string? ModName, string? SourceNpcFormKey)
{
    // Import-only metadata; internal so Export's JSON shape is unchanged.
    internal bool SourceNpcWasInferred { get; init; }
}

internal record NpcChoiceImportPlan(
    Dictionary<FormKey, (string ModName, FormKey NpcFormKey)> Selections,
    HashSet<FormKey> MissingNpcs,
    HashSet<FormKey> ProtectedSharedNpcs,
    int InferredSourceCount);

internal static class NpcChoiceImport
{
    /// <summary>
    /// Normalizes a choices export or patch token without consulting filenames or showing UI.
    /// Leaves malformed entries for the usual per-NPC validation report.
    /// </summary>
    internal static bool TryParse(string json, out Dictionary<string, NpcChoiceDto?> choices,
        out string error)
    {
        choices = new();
        error = string.Empty;
        try
        {
            var root = JObject.Parse(json, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
            var processedNpcs = root.GetValue("ProcessedNpcs", StringComparison.OrdinalIgnoreCase);
            bool isToken = processedNpcs != null;
            var entries = isToken ? processedNpcs as JObject : root;

            if (entries == null || (!isToken && entries.Count > 0 &&
                !entries.Properties().Any(p => p.Value is JObject entry &&
                    (entry.GetValue(nameof(NpcChoiceDto.ModName), StringComparison.OrdinalIgnoreCase) != null ||
                     entry.GetValue(nameof(NpcChoiceDto.SourceNpcFormKey), StringComparison.OrdinalIgnoreCase) != null))))
            {
                error = "The selected JSON is not an NPC choices export or an NPC_Token.json patch token.";
                return false;
            }

            foreach (var property in entries.Properties())
            {
                if (property.Value is not JObject entry)
                {
                    choices[property.Name] = null;
                    continue;
                }

                var source = entry.GetValue(nameof(NpcChoiceDto.SourceNpcFormKey), StringComparison.OrdinalIgnoreCase);
                // Older tokens recorded only the target NPC. Never infer a donor from
                // AppearancePlugin or OutputPlugin: neither identifies a shared face.
                bool inferSource = isToken && (source == null || source.Type == JTokenType.Null);
                string? sourceKey = inferSource
                    ? property.Name
                    : ReadString(source);
                choices[property.Name] = new NpcChoiceDto(
                    ReadString(entry.GetValue(nameof(NpcChoiceDto.ModName), StringComparison.OrdinalIgnoreCase)),
                    sourceKey) { SourceNpcWasInferred = inferSource };
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"Could not read the file as an NPC choices export or an NPC_Token.json patch token: {ex.Message}";
            return false;
        }
    }

    internal static NpcChoiceImportPlan CreatePlan(
        IReadOnlyDictionary<string, NpcChoiceDto?> importedData,
        IReadOnlyDictionary<FormKey, (string ModName, FormKey NpcFormKey)> validSelections,
        IReadOnlyDictionary<FormKey, (string ModName, FormKey NpcFormKey)> currentSelections)
    {
        var selections = validSelections.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var fileNpcs = new HashSet<FormKey>();
        var protectedSharedNpcs = new HashSet<FormKey>();
        int inferredSourceCount = 0;
        foreach (var (key, choice) in importedData)
        {
            if (!FormKey.TryFactory(key, out var target)) continue;
            // Membership comes from the file, including rejected entries. Choosing to
            // clear ABSENT NPCs must not also erase a present but invalid choice.
            fileNpcs.Add(target);
            if (choice?.SourceNpcWasInferred != true) continue;

            inferredSourceCount++;
            if (currentSelections.TryGetValue(target, out var current) && current.NpcFormKey != target)
            {
                selections.Remove(target);
                protectedSharedNpcs.Add(target);
            }
        }

        return new NpcChoiceImportPlan(selections,
            currentSelections.Keys.Where(key => !fileNpcs.Contains(key)).ToHashSet(),
            protectedSharedNpcs, inferredSourceCount);
    }

    /// <summary>Null means Cancel. All mutations go through the consistency provider so
    /// persisted choices, the selection cache and NPC menu notifications stay in sync.</summary>
    internal static bool Apply(NpcChoiceImportPlan plan, NpcConsistencyProvider provider, bool? keepMissing)
    {
        if (keepMissing == null || plan.Selections.Count == 0) return false;

        if (!keepMissing.Value)
        {
            foreach (var target in plan.MissingNpcs)
                provider.ClearSelectedMod(target);
        }

        foreach (var (target, choice) in plan.Selections)
            provider.SetSelectedMod(target, choice.ModName, choice.NpcFormKey);

        return true;
    }

    private static string? ReadString(JToken? value) =>
        value?.Type == JTokenType.String ? value.Value<string>() : null;
}
