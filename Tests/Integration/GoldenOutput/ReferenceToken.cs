using System.IO;
using System.Linq;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;

/// <summary>
/// Reads a reference combo's <c>NPC_Token.json</c> to learn which target NPCs that run actually processed.
/// This is the authoritative per-combo expected set: in plain Create mode the validator drops cross-NPC
/// appearance swaps, so those combos legitimately contain fewer NPCs than the full selection list.
/// </summary>
internal static class ReferenceToken
{
    /// <summary>The target NPC FormKeys recorded in the reference token's ProcessedNpcs map.</summary>
    public static IReadOnlySet<FormKey> ProcessedTargets(string comboDir)
    {
        var path = Path.Combine(comboDir, "NPC_Token.json");
        var set = new HashSet<FormKey>();
        if (!File.Exists(path)) return set;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("ProcessedNpcs", out var processed)
            && processed.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in processed.EnumerateObject())
            {
                if (TryParseFormKey(prop.Name, out var fk)) set.Add(fk);
            }
        }
        return set;
    }

    private static bool TryParseFormKey(string raw, out FormKey formKey)
    {
        formKey = default;
        try { formKey = FormKey.Factory(raw); return true; }
        catch { return false; }
    }
}
