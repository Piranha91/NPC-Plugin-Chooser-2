using System.IO;
using System.Reflection;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Analyzes why specific master plugins are required by a target ESP/ESM file.
/// Adapted from WhereforeArtThouMastered synthesis patcher.
/// </summary>
public class MasterAnalyzer
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly NpcConsistencyProvider _npcConsistencyProvider;
    private readonly Settings _settings;

    public MasterAnalyzer(EnvironmentStateProvider environmentStateProvider, NpcConsistencyProvider npcConsistencyProvider, Settings settings)
    {
        _environmentStateProvider = environmentStateProvider;
        _npcConsistencyProvider = npcConsistencyProvider;
        _settings = settings;
    }

    /// <summary>
    /// Reads the master list from a plugin file's header.
    /// </summary>
    /// <param name="pluginPath">Full path to the plugin file.</param>
    /// <returns>List of ModKey masters, or empty list if unable to read.</returns>
    public List<ModKey> GetMastersFromPlugin(string pluginPath)
    {
        var masters = new List<ModKey>();
        
        try
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, _environmentStateProvider.SkyrimVersion);
            foreach (var masterRef in mod.ModHeader.MasterReferences)
            {
                masters.Add(masterRef.Master);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error reading masters from {pluginPath}: {ex.Message}");
        }

        return masters;
    }

    /// <summary>
    /// Analyzes a target plugin to find all references to records from the specified master plugins.
    /// </summary>
    /// <param name="targetPluginPath">Full path to the target plugin to analyze.</param>
    /// <param name="mastersToAnalyze">List of master ModKeys to search for references to.</param>
    /// <param name="verboseMode">If true, logs records that don't contain references.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analysis results containing references found for each master.</returns>
    public MasterAnalysisResult AnalyzeMasterReferences(
        string targetPluginPath,
        IEnumerable<ModKey> mastersToAnalyze,
        bool verboseMode = false,
        IProgress<(double percent, string message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MasterAnalysisResult();
        var masterSet = mastersToAnalyze.ToHashSet();

        if (!masterSet.Any())
        {
            result.ErrorMessage = "No masters selected for analysis.";
            return result;
        }

        ISkyrimModGetter targetPlugin;
        try
        {
            targetPlugin = SkyrimMod.CreateFromBinaryOverlay(targetPluginPath, _environmentStateProvider.SkyrimVersion);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Failed to load target plugin: {ex.Message}";
            return result;
        }

        var targetModKey = ModKey.FromFileName(Path.GetFileName(targetPluginPath));
        result.TargetPlugin = targetModKey;
        result.AnalyzedMasters = masterSet.ToList();

        // Build a combined set of FormKeys from all candidate masters for fast lookup
        var masterFormKeys = new Dictionary<ModKey, HashSet<FormKey>>();
        foreach (var masterKey in masterSet)
        {
            masterFormKeys[masterKey] = new HashSet<FormKey>();
            
            // Try to load the master from the load order
            var masterListing = _environmentStateProvider.LoadOrder.TryGetValue(masterKey);
            if (masterListing?.Mod != null)
            {
                foreach (var record in masterListing.Mod.EnumerateMajorRecords())
                {
                    if (record.FormKey.ModKey.Equals(masterKey))
                    {
                        masterFormKeys[masterKey].Add(record.FormKey);
                    }
                }
            }
        }

        // Initialize result containers for each master
        foreach (var masterKey in masterSet)
        {
            result.ReferencesByMaster[masterKey] = new List<MasterReference>();
        }

        // Enumerate all records in the target plugin
        var recordsToSearch = targetPlugin.EnumerateMajorRecords().ToList();
        int totalRecords = recordsToSearch.Count;
        int processedRecords = 0;

        progress?.Report((0, $"Analyzing {totalRecords} records..."));

        foreach (var record in recordsToSearch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var recordLogString = GetLogString(record, true);
            var visitedObjects = new HashSet<object>();
            
            // Check if this is an NPC and get appearance mod info
            string? appearanceModInfo = null;
            if (record is INpcGetter npcGetter)
            {
                appearanceModInfo = GetAppearanceModInfo(npcGetter.FormKey);
            }

            // Search for references to each master
            foreach (var masterKey in masterSet)
            {
                var references = new List<string>();
                FindMasterReferences(
                    record,
                    new List<string>(),
                    masterFormKeys[masterKey],
                    masterKey,
                    recordLogString,
                    visitedObjects,
                    references);

                foreach (var reference in references)
                {
                    result.ReferencesByMaster[masterKey].Add(new MasterReference
                    {
                        SourceRecord = recordLogString,
                        ReferencePath = reference,
                        AppearanceModInfo = appearanceModInfo
                    });
                }
            }

            processedRecords++;
            if (processedRecords % 100 == 0 || processedRecords == totalRecords)
            {
                double percent = (double)processedRecords / totalRecords * 100;
                progress?.Report((percent, $"Processed {processedRecords}/{totalRecords} records..."));
            }
        }

        progress?.Report((100, "Analysis complete."));
        return result;
    }

    /// <summary>
    /// Gets a readable string identifier for a major record.
    /// </summary>
    private string GetLogString(IMajorRecordGetter majorRecordGetter, bool fullString = false)
    {
        var sb = new StringBuilder();

        if (majorRecordGetter is ITranslatedNamedGetter namedGetter && namedGetter.Name != null)
        {
            var language = _settings.LocalizationLanguage;
            if (language != null && namedGetter.Name.TryLookup(language.Value, out var localizedName))
            {
                sb.Append(localizedName);
            }
            else if (namedGetter.Name.String != null)
            {
                sb.Append(namedGetter.Name.String);
            }

            if (fullString && sb.Length > 0)
            {
                sb.Append(" | ");
            }
        }

        if (sb.Length == 0 || fullString)
        {
            if (majorRecordGetter.EditorID != null)
            {
                sb.Append(majorRecordGetter.EditorID);
                if (fullString) sb.Append(" | ");
            }

            if (sb.Length == 0 || fullString)
            {
                sb.Append(majorRecordGetter.FormKey.ToString());
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Recursively searches through an object's properties to find FormLinks that point to a master plugin.
    /// </summary>
    private void FindMasterReferences(
        object? currentObject,
        List<string> currentPath,
        HashSet<FormKey> masterFormKeys,
        ModKey masterKey,
        string rootRecordLogString,
        HashSet<object> visitedObjects,
        List<string> foundReferences)
    {
        if (currentObject == null) return;

        // Cycle prevention for reference types
        if (currentObject is not ValueType && !visitedObjects.Add(currentObject)) return;

        var type = currentObject.GetType();

        // Skip primitives, enums, and strings
        if (type.IsPrimitive || type.IsEnum || type == typeof(string)) return;

        // Base Case: Found a FormLink
        if (currentObject is IFormLinkGetter formLink)
        {
            if (!formLink.IsNull && masterFormKeys.Contains(formLink.FormKey))
            {
                var pathString = string.Join(" -> ", currentPath);
                var targetRecordString = GetLogStringForLink(formLink);
                foundReferences.Add($"{pathString} -> {targetRecordString}");
            }
            return;
        }

        // Recursive Step 1: Collections
        if (currentObject is System.Collections.IEnumerable collection && currentObject is not string)
        {
            int i = 0;
            foreach (var item in collection)
            {
                var newPath = new List<string>(currentPath);
                if (newPath.Any())
                {
                    newPath[^1] = $"{newPath.Last()}[{i++}]";
                }
                else
                {
                    newPath.Add($"[{i++}]");
                }
                FindMasterReferences(item, newPath, masterFormKeys, masterKey, rootRecordLogString, visitedObjects, foundReferences);
            }
            return;
        }

        // Recursive Step 2: Complex Objects (focus on Mutagen types)
        if (type.Namespace == null || !type.Namespace.StartsWith("Mutagen")) return;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Skip indexers and non-useful properties
            if (property.GetIndexParameters().Length > 0) continue;
            if (property.Name is "FormKey" or "EditorID" or "Master" or "VersionControl" or "Version2") continue;

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(currentObject);
            }
            catch
            {
                continue;
            }

            var newPath = new List<string>(currentPath) { property.Name };
            FindMasterReferences(propertyValue, newPath, masterFormKeys, masterKey, rootRecordLogString, visitedObjects, foundReferences);
        }
    }

    /// <summary>
    /// Gets a readable string for a FormLink, resolving it if possible.
    /// </summary>
    private string GetLogStringForLink(IFormLinkGetter link)
    {
        if (_environmentStateProvider.LinkCache.TryResolve(link, out var resolved) && resolved != null)
        {
            return GetLogString(resolved, true);
        }
        return link.FormKey.ToString();
    }

    /// <summary>
    /// Gets the appearance mod info string for an NPC, including shared source if applicable.
    /// </summary>
    private string? GetAppearanceModInfo(FormKey npcFormKey)
    {
        var (modName, sourceNpcFormKey) = _npcConsistencyProvider.GetSelectedMod(npcFormKey);
        
        if (string.IsNullOrEmpty(modName))
        {
            return null;
        }

        // Check if this is a shared appearance (source NPC is different from this NPC)
        if (!sourceNpcFormKey.IsNull && !sourceNpcFormKey.Equals(npcFormKey))
        {
            // Try to resolve the source NPC to get its name
            string sourceNpcString = sourceNpcFormKey.ToString();
            if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(sourceNpcFormKey, out var sourceNpc) && sourceNpc != null)
            {
                sourceNpcString = GetLogString(sourceNpc, true);
            }
            
            return $"{modName} (Shared from {sourceNpcString})";
        }

        return modName;
    }

    /// <summary>
    /// Formats the analysis result into a human-readable report.
    /// </summary>
    public string FormatAnalysisReport(MasterAnalysisResult result)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            sb.AppendLine($"ERROR: {result.ErrorMessage}");
            return sb.ToString();
        }

        sb.AppendLine($"Master Analysis Report for: {result.TargetPlugin.FileName}");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine();

        foreach (var masterKey in result.AnalyzedMasters)
        {
            var references = result.ReferencesByMaster.GetValueOrDefault(masterKey, new List<MasterReference>());
            
            sb.AppendLine($"Master: {masterKey.FileName}");
            sb.AppendLine(new string('-', 40));

            if (references.Count == 0)
            {
                sb.AppendLine("  No references found to this master.");
                sb.AppendLine("  This master may be unnecessary, or references may be in record types not analyzed.");
            }
            else
            {
                sb.AppendLine($"  Found {references.Count} reference(s):");
                sb.AppendLine();

                // Group references by source record for readability
                var groupedRefs = references.GroupBy(r => new { r.SourceRecord, r.AppearanceModInfo });
                foreach (var group in groupedRefs)
                {
                    // Build the source record header with appearance mod info if available
                    var headerBuilder = new StringBuilder($"  [{group.Key.SourceRecord}");
                    if (!string.IsNullOrEmpty(group.Key.AppearanceModInfo))
                    {
                        headerBuilder.Append($" | Appearance Mod: {group.Key.AppearanceModInfo}");
                    }
                    headerBuilder.Append("]");
                    sb.AppendLine(headerBuilder.ToString());
                    
                    foreach (var reference in group)
                    {
                        sb.AppendLine($"    -> {reference.ReferencePath}");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"Analysis completed at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        return sb.ToString();
    }
}

/// <summary>
/// Contains the results of a master analysis operation.
/// </summary>
public class MasterAnalysisResult
{
    public ModKey TargetPlugin { get; set; }
    public List<ModKey> AnalyzedMasters { get; set; } = new();
    public Dictionary<ModKey, List<MasterReference>> ReferencesByMaster { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Represents a single reference from a record to a master.
/// </summary>
public class MasterReference
{
    public string SourceRecord { get; set; } = string.Empty;
    public string ReferencePath { get; set; } = string.Empty;
    public string? AppearanceModInfo { get; set; }
}