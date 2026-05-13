using System.Text;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Structured warning emitted during initialization. Pooled by (concrete type, GroupKey)
/// in VM_SplashScreen so repeats of the same root cause collapse into one message.
/// </summary>
public abstract record InitializationWarning
{
    public abstract string GroupKey { get; }
    public abstract string Render(IReadOnlyList<InitializationWarning> group);
}

public sealed record MultiSourceMasterWarning(
    string MasterFileName,
    IReadOnlyList<string> CandidateSources,
    string ChosenSource,
    string RequestingMod) : InitializationWarning
{
    public override string GroupKey =>
        $"MultiSourceMaster|{MasterFileName.ToLowerInvariant()}|{ChosenSource.ToLowerInvariant()}";

    public override string Render(IReadOnlyList<InitializationWarning> group)
    {
        var items = group.OfType<MultiSourceMasterWarning>().ToList();
        var unionSources = items
            .SelectMany(w => w.CandidateSources)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sourcesStr = string.Join(", ", unionSources);

        if (items.Count == 1)
        {
            var only = items[0];
            var originalSources = string.Join(", ", only.CandidateSources);
            return $"Found multiple sources for master '{only.MasterFileName}' needed by '{only.RequestingMod}': [{originalSources}]. Choosing the newest version from '{only.ChosenSource}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found multiple sources for master '{MasterFileName}'");
        sb.AppendLine($"({items.Count} mods need it). Choosing the newest version from '{ChosenSource}'.");
        sb.AppendLine($"  Available sources: [{sourcesStr}]");
        sb.AppendLine("  Needed by:");
        foreach (var w in items
                     .OrderBy(w => w.RequestingMod, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"    - {w.RequestingMod}");
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed record UnscannedInjectedRecordsWarning(
    string PluginFileName,
    string RequestingMod,
    IReadOnlyList<string> MissingMasters) : InitializationWarning
{
    public override string GroupKey
    {
        get
        {
            var key = string.Join(",",
                MissingMasters
                    .Select(m => m.ToLowerInvariant())
                    .OrderBy(m => m, StringComparer.Ordinal));
            return $"UnscannedInjected|{key}";
        }
    }

    public override string Render(IReadOnlyList<InitializationWarning> group)
    {
        var items = group.OfType<UnscannedInjectedRecordsWarning>().ToList();

        if (items.Count == 1)
        {
            var only = items[0];
            var mastersJoined = string.Join(" and ", only.MissingMasters);
            return $"Warning: {only.PluginFileName} in {only.RequestingMod} could not be fully scanned for injected records because its master(s) {mastersJoined} are not in your load order. You can complete the scan by adding the master and clicking the Refresh button for {only.RequestingMod} in the Mods Menu.";
        }

        var mastersDisplay = string.Join(" and ", MissingMasters);
        var sb = new StringBuilder();
        sb.AppendLine($"Warning: {items.Count} mods could not be fully scanned for injected records because their master(s) {mastersDisplay} are not in your load order.");
        sb.AppendLine("Add the missing master(s) to your load order and click Refresh for each mod in the Mods Menu to complete the scan.");
        sb.AppendLine("Affected mods:");
        foreach (var w in items
                     .OrderBy(w => w.RequestingMod, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(w => w.PluginFileName, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"    - {w.PluginFileName} in {w.RequestingMod}");
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed record SkippedMissingMasterWarning(
    string RequestingMod,
    IReadOnlyList<string> UnresolvedMasters) : InitializationWarning
{
    public override string GroupKey
    {
        get
        {
            var key = string.Join(",",
                UnresolvedMasters
                    .Select(m => m.ToLowerInvariant())
                    .OrderBy(m => m, StringComparer.Ordinal));
            return $"SkippedMissingMaster|{key}";
        }
    }

    public override string Render(IReadOnlyList<InitializationWarning> group)
    {
        var items = group.OfType<SkippedMissingMasterWarning>().ToList();

        if (items.Count == 1)
        {
            var only = items[0];
            return $"'{only.RequestingMod}' was skipped because the following masters are not installed in any mod folder: {string.Join(", ", only.UnresolvedMasters)}.";
        }

        var mastersDisplay = string.Join(", ", UnresolvedMasters);
        var sb = new StringBuilder();
        sb.AppendLine($"{items.Count} mods were skipped because the following masters are not installed in any mod folder: {mastersDisplay}.");
        sb.AppendLine("Skipped mods:");
        foreach (var w in items
                     .OrderBy(w => w.RequestingMod, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"    - {w.RequestingMod}");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Fallback wrapper for ad-hoc string messages. Each instance gets a unique
/// GroupKey so they never pool together.
/// </summary>
public sealed record GenericWarning(string Message) : InitializationWarning
{
    private readonly string _groupKey = Guid.NewGuid().ToString();
    public override string GroupKey => _groupKey;
    public override string Render(IReadOnlyList<InitializationWarning> group) => Message;
}
