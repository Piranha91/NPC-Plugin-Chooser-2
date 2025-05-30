using System.IO;
using System.Reflection;
using NPC_Plugin_Chooser_2.View_Models;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using NPC_Plugin_Chooser_2.Models;
using Mutagen.Bethesda.Serialization.Yaml;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class RaceHandler
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Lazy<VM_Run> _runVM;
    private readonly Settings _settings;
    private readonly DuplicateInManager _duplicateInManager;
    private readonly string _serializationDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Serializations");

    // --- Data for Race Handling ---
    private List<RaceSerializationInfo>
        _racesToSerializeForYaml = new(); // For ForwardWinningOverrides (Default) YAML generation
    private Dictionary<INpcGetter, RaceSerializationInfo> _raceSerializationNpcAssignments = new();
    
    private Dictionary<FormKey, Dictionary<ModKey, List<string>>> _alteredPropertiesMap = new();

    public RaceHandler(EnvironmentStateProvider environmentStateProvider, Lazy<VM_Run> runVM, Settings settings, DuplicateInManager duplicateInManager)
    {
        _environmentStateProvider = environmentStateProvider;
        _runVM = runVM;
        _settings = settings;
        _duplicateInManager = duplicateInManager;
    }

    public void Reinitialize()
    {
        _alteredPropertiesMap.Clear();
        _racesToSerializeForYaml.Clear();
    }

    public void ProcessNpcRace(
        Npc patchNpc, // The NPC record in our output patch
        INpcGetter? sourceNpcIfUsed, // The NPC record from the appearance mod (if used for NPC data)
        INpcGetter winningNpcOverride, // The original conflict-winning NPC
        ModKey appearanceModKey, // The .esp key of the appearance mod providing the NPC override / assets
        ModSetting appearanceModSetting) // Full mod setting)
    {
        // Check if the selected mod provided a plugin. If not, the race could not have been modified.
        if (sourceNpcIfUsed == null)
        {
            _runVM.Value.AppendLog(
                $"      NPC {patchNpc.EditorID} has no plugin from {appearanceModSetting.DisplayName}. Skipping race handling.");
            return;
        }

        // Check if the selected plugin's race record exits
        if (sourceNpcIfUsed.Race.IsNull)
        {
            _runVM.Value.AppendLog(
                $"      NPC {patchNpc.EditorID} has a null Race record from {appearanceModSetting.DisplayName}. Skipping race handling.");
            return;
        }

        // Check if the appearance mod provides an override for the NPC's current race
        var raceContexts =
            _environmentStateProvider.LinkCache
                .ResolveAllContexts<IRace, IRaceGetter>(sourceNpcIfUsed.Race.FormKey);
        var raceContextFromAppearanceMod = raceContexts?.FirstOrDefault(x => x.ModKey.Equals(appearanceModKey));
        if (raceContextFromAppearanceMod == null)
        {
            _runVM.Value.AppendLog(
                $"      {appearanceModSetting.DisplayName} does not edit the race of NPC {patchNpc.EditorID}. Skipping race handling.");
            return;
        }
        var raceGetterFromAppearanceMod = raceContextFromAppearanceMod.Record ?? null;

        // SCENARIO 1: NPC uses a NEW race that's defined IN ITS OWN appearance mod.
        // Criteria:
        // 1. The race record (`raceGetterFromAppearanceMod`) is defined by `appearanceModKey`.
        // This scenario implies that `sourceNpcIfUsed` (if available and used for `patchNpc` creation) had this custom race.
        bool isNewCustomRaceFromAppearanceMod = raceGetterFromAppearanceMod != null &&
                                                raceGetterFromAppearanceMod.FormKey.ModKey.Equals(appearanceModKey);

        if (isNewCustomRaceFromAppearanceMod)
        {
            _runVM.Value.AppendLog(
                $"      NPC {patchNpc.EditorID} uses new custom race {raceGetterFromAppearanceMod.EditorID} ({raceGetterFromAppearanceMod.FormKey}) defined in its appearance mod {appearanceModKey.FileName}.");
            _runVM.Value.AppendLog($"        Merging race and its dependencies from {appearanceModKey.FileName}.");

            // Ensure patchNpc points to this race (it should if sourceNpcIfUsed was its origin and had this race)
            patchNpc.Race.SetTo(raceGetterFromAppearanceMod);

            // Add the race record (as it exists in the patch, which is an override of currentNpcRaceRecord)
            // to the map for dependency duplication.
            _duplicateInManager.DuplicateInFormLink(patchNpc.Race, raceGetterFromAppearanceMod.ToLink(),
                _environmentStateProvider.OutputMod,
                appearanceModSetting.CorrespondingModKeys, appearanceModKey);
            return; // Handled
        }

        // SCENARIO 2: NPC uses an existing race, and that race is present as a record in the appearance mod
        // (i.e., appearance mod MODIFIES a race the NPC uses).
        // Criteria:
        // 1. `raceGetterFromAppearanceMod` is not null (the appearance-providing mod contains this race as a new record or override)
        // 2. `raceGetterFromAppearanceMod' has a root ModKey that's not appearanceModKey (implying that it's an overrride)
        bool isOverriddenRace = raceGetterFromAppearanceMod != null &&
                                !raceGetterFromAppearanceMod.FormKey.ModKey.Equals(appearanceModKey);

        if (isOverriddenRace)
        {
            _runVM.Value.AppendLog(
                $"      NPC {patchNpc.EditorID} uses overriden race {raceGetterFromAppearanceMod.EditorID} ({raceGetterFromAppearanceMod.FormKey}).");
            _runVM.Value.AppendLog($"        Appearance mod {appearanceModKey.FileName} modifies this race.");

            switch (_settings.RaceHandlingMode)
            {
                case RaceHandlingMode.ForwardWinningOverrides:
                    HandleForwardWinningOverridesRace(patchNpc, raceContextFromAppearanceMod,
                        raceContexts, appearanceModSetting.CorrespondingModKeys, appearanceModKey);
                    break;

                /*
                case RaceHandlingMode.DuplicateAndRemapRace:
                    // currently not exposed.
                    throw new NotImplementedException();
                    break;*/

                case RaceHandlingMode.IgnoreRace:
                    _runVM.Value.AppendLog(
                        $"        Race Handling Mode: IgnoreRace. No changes made to race record {raceGetterFromAppearanceMod.EditorID} itself by this logic.");
                    break;
            }
        }
    }

    private void HandleForwardWinningOverridesRace(
        Npc patchNpc,
        IModContext<ISkyrimMod, ISkyrimModGetter, IRace, IRaceGetter> appearanceModRaceContext,
        IEnumerable<IModContext<ISkyrimMod, ISkyrimModGetter, IRace, IRaceGetter>>? raceContexts,
        List<ModKey> deepCopyEligibleModKeys,
        ModKey appearanceModKey)
    {

        var originalRaceGetter = raceContexts.Last().Record;
        if (originalRaceGetter == null)
        {
            _runVM.Value.AppendLog("Error: Could not determine original race context for race handling.");
            return;
        }

        if (_settings.PatchingMode == PatchingMode.EasyNPC_Like)
        {
            _runVM.Value.AppendLog(
                $"        Race Handling: ForwardWinningOverrides (EasyNPC-Like) for race {originalRaceGetter.EditorID}.");
            // In EasyNPC-Like mode, patchNpc is an override of winningNpcOverride.
            // So patchNpc.Race should already point to originalRaceContext.FormKey.
            // We need to override originalRaceContext in the patch and forward differing properties.

            var racePropertyDeltas =
                RaceHelpers.GetDifferingPropertyNames(appearanceModRaceContext.Record, originalRaceGetter);
            if (racePropertyDeltas.Any())
            {
                var winningRaceGetter = raceContexts.First().Record;
                if (winningRaceGetter == null)
                {
                    _runVM.Value.AppendLog("Error: Could not determine winning race context for race handling.");
                    return;
                }

                var winningRaceRecord = _environmentStateProvider.OutputMod.Races.GetOrAddAsOverride(winningRaceGetter);
                
                // set the deltas
                RaceHelpers.CopyPropertiesToNewRace(appearanceModRaceContext.Record, winningRaceRecord, racePropertyDeltas);
                if (!_alteredPropertiesMap.ContainsKey(winningRaceRecord.FormKey))
                {
                    _alteredPropertiesMap[winningRaceRecord.FormKey] = new Dictionary<ModKey, List<string>>();
                }

                if (!_alteredPropertiesMap[winningRaceRecord.FormKey].ContainsKey(appearanceModKey))
                {
                    _alteredPropertiesMap[winningRaceRecord.FormKey][appearanceModKey] = new List<string>();
                }
                
                _alteredPropertiesMap[winningRaceRecord.FormKey][appearanceModKey].AddRange(racePropertyDeltas);
                
                // remap dependencies if needed
                _duplicateInManager.DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod,
                    winningRaceRecord, deepCopyEligibleModKeys, appearanceModKey, true);
            }
            else
            {
                _runVM.Value.AppendLog(
                    $"          Race properties are identical to original context. No race forwarding needed.");
            }
        }
        else // Default Patching Mode
        {

            /* Skip YAML serialization for now; may come back to it
            _runVM.Value.AppendLog(
                $"          Generating YAML record of race version from {appearanceModKey.FileName} for {patchNpc.EditorID}.");

            var existingSerialization = _racesToSerializeForYaml.FirstOrDefault(
                x => x.AppearanceModKey.Equals(appearanceModKey) &&
                     x.OriginalRaceFormKey.Equals(originalRaceGetter.FormKey));

            if (existingSerialization != null)
            {
                _runVM.Value.AppendLog(
                    $"          Applying previously generated race {existingSerialization.RaceToSerialize.Record.EditorID}.");
                _raceSerializationNpcAssignments.Add(patchNpc, existingSerialization);
                return;
            }

            // Schedule new YAML generation for the diff.
            var toSerialize = new RaceSerializationInfo
            {
                RaceToSerialize = appearanceModRaceContext, // This is the one from the appearance mod
                OriginalRaceFormKey = originalRaceGetter.FormKey, // This is the vanilla FormKey
                AppearanceModKey = appearanceModKey,
                DiffProperties = RaceHelpers.GetDifferingPropertyNames(appearanceModRaceContext.Record, originalRaceGetter)
                    .ToList()
            };

            _racesToSerializeForYaml.Add(toSerialize);
            _raceSerializationNpcAssignments.Add(patchNpc, toSerialize);
            _runVM.Value.AppendLog(
                $"          Scheduled YAML generation for race {appearanceModRaceContext.Record.EditorID}.");
            */

            // Add race to patch exactly how it appears in the source mod
            var appearanceRaceRecord = _environmentStateProvider.OutputMod.Races.GetOrAddAsOverride(appearanceModRaceContext.Record);
            
            // remap dependencies if needed
            _duplicateInManager.DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod,
                appearanceRaceRecord, deepCopyEligibleModKeys, appearanceModKey, true);
        }
    }
    
    public bool GetClashingModifiedRaceProperties()
    {
        string errorStr = "";
        foreach (var kvp in _alteredPropertiesMap)
        {
            foreach (var entry in kvp.Value)
            {
                var duplicates = entry.Value
                    .GroupBy(s => s)             // group by the string’s own value
                    .Where(g => g.Count() > 1)   // only groups that have more than one member
                    .Select(g => new 
                    {
                        Value = g.Key,           // the duplicated string
                        Count = g.Count()        // how many times it appears
                    })
                    .ToList();

                if (duplicates.Any())
                {
                    string raceIdStr = kvp.Key.ToString();
                    if (_environmentStateProvider.LinkCache.TryResolve<IRaceGetter>(kvp.Key, out var raceGetter))
                    {
                        raceIdStr = raceGetter.Name?.String ?? raceGetter.EditorID ?? kvp.Key.ToString();
                    }

                    errorStr += raceIdStr +
                                ": Detected multiple mods modifying the same properties. Make sure there's no conflict. Properties: " +
                                Environment.NewLine + string.Join(", ", duplicates) + Environment.NewLine;
                }
            }
        }

        return errorStr.Any();
    }

    private string ResolveNpcName(FormKey npcKey) =>
        _environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcKey, out var npc)
            ? (npc.Name?.String ?? npc.EditorID ?? npcKey.ToString())
            : npcKey.ToString();

    private string ResolveRaceName(FormKey raceKey) =>
        _environmentStateProvider.LinkCache.TryResolve<IRaceGetter>(raceKey, out var race)
            ? (race.Name?.String ?? race.EditorID ?? raceKey.ToString())
            : raceKey.ToString();


    public void GenerateRaceYamlFiles()
    {
        SkyrimMod tempMod = new("tempMod.esp", SkyrimRelease.SkyrimSE);
        foreach (var entry in _racesToSerializeForYaml)
        {
            entry.RaceToSerialize.GetOrAddAsOverride(tempMod);
        }

        var ouputPath = Path.Combine(_serializationDir, DateTime.Now.ToString("yyyy_MM_dd_HH-mm-ss"));

        //MutagenYamlConverter.Instance.Serialize(tempMod, ouputPath).Wait();
        _runVM.Value.AppendLog("Serialized saved races to " + ouputPath);
    }

    // --- Forward Race Edits From YAML ---
    private async Task ForwardRaceEditsFromYamlAsync()
    {
        _runVM.Value.AppendLog("\n--- Initiating Forward Race Edits from YAML ---");
        if (!_environmentStateProvider.EnvironmentIsValid)
        {
            _runVM.Value.AppendLog("Environment not valid. Cannot proceed.");
            return;
        }

        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "YAML files (*.yaml)|*.yaml|All files (*.*)|*.*",
            Title = "Select Race Diff YAML file",
            InitialDirectory = Path.Combine(_serializationDir), // Default to RaceDiffs folder
            Multiselect = false // For now, one file at a time
        };

        if (openFileDialog.ShowDialog() != true)
        {
            _runVM.Value.AppendLog("No YAML file selected. Operation cancelled.");
            return;
        }

        string yamlFilePath = openFileDialog.FileName;
        _runVM.Value.AppendLog($"Processing YAML file: {yamlFilePath}");

        try
        {

        }
        catch (Exception ex)
        {
            _runVM.Value.AppendLog(
                $"An unexpected error occurred while forwarding race edits: {ExceptionLogger.GetExceptionStack(ex)}");
        }
        finally
        {
            _runVM.Value.AppendLog("--- Finished Forward Race Edits from YAML ---");
        }
    }

    internal class RaceSerializationInfo
    {
        public IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter> RaceToSerialize { get; set; }
        public FormKey OriginalRaceFormKey { get; set; } // FormKey of the race this is a diff against
        public ModKey AppearanceModKey { get; set; } // Source of the modified race
        public List<string> DiffProperties { get; set; } // Properties to forward when diff patching
    }

    // Helper class for deserializing our specific YAML structure for the "Forward Race Edits" button
    // This is what we expect to find in the YAML files generated by "ForwardWinningOverrides" (Default)
    public class RaceYamlWrapper
    {
        public FormLink<IRaceGetter> OriginalRace { get; set; }
        public Race ModifiedRace { get; set; } // The actual race data from the appearance mod
        public ModKey AppearanceSourceMod { get; set; }
    }
}

public static class RaceHelpers
{
    /// <summary>
    /// Compares two <see cref="IRaceGetter"/>s and returns the names of every public
    /// property whose values are not equal.
    /// </summary>
    public static IReadOnlyList<string> GetDifferingPropertyNames(
        IRaceGetter first,
        IRaceGetter second)
    {
        if (first  is null) throw new ArgumentNullException(nameof(first));
        if (second is null) throw new ArgumentNullException(nameof(second));

        var differences = new List<string>();

        // All public instance properties declared on the interface
        foreach (PropertyInfo pi in typeof(IRaceGetter)
                                     .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? a = pi.GetValue(first);
            object? b = pi.GetValue(second);

            // NB: object.Equals handles null‐checks for us
            if (!Equals(a, b))
                differences.Add(pi.Name);
        }

        return differences;
    }

    /// <summary>
    /// Copies the properties named in <paramref name="propertiesToCopy"/> from
    /// <paramref name="source"/> onto <paramref name="destinationRace"/>.
    /// </summary>
    public static void CopyPropertiesToNewRace(
        IRaceGetter sourceGetter,
        Race destinationRace,
        IEnumerable<string> propertiesToCopy)
    {
        if (sourceGetter is null) throw new ArgumentNullException(nameof(sourceGetter));
        if (destinationRace is null) throw new ArgumentNullException(nameof(destinationRace));
        if (propertiesToCopy is null) throw new ArgumentNullException(nameof(propertiesToCopy));
        
        SkyrimMod tempMod = new("tempMod.esp", SkyrimRelease.SkyrimSE);
        var source = tempMod.Races.GetOrAddAsOverride(sourceGetter);
        
        // Cache reflection once for speed
        var getterProps = typeof(IRaceGetter)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name);

        var setterProps = typeof(IRace)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name);

        foreach (string propName in propertiesToCopy)
        {
            if (!getterProps.TryGetValue(propName, out PropertyInfo? srcPi)) continue; // unknown
            if (!setterProps.TryGetValue(propName, out PropertyInfo? dstPi)) continue; // not writable

            object? value = srcPi.GetValue(source);
            dstPi.SetValue(destinationRace, value);
        }
    }
}