using System.IO;
using System.Reflection;
using NPC_Plugin_Chooser_2.View_Models;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class RaceHandler : OptionalUIModule
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Settings _settings;
    private readonly RecordHandler _recordHandler;
    private readonly AssetHandler _assetHandler;
    private readonly Auxilliary _aux;
    private readonly RecordDeltaPatcher _recordDeltaPatcher;
    private readonly string _serializationDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Serializations");

    // --- Data for Race Handling ---
    private List<RaceSerializationInfo>
        _racesToSerializeForYaml = new(); // For ForwardWinningOverrides (Default) YAML generation
    private Dictionary<INpcGetter, RaceSerializationInfo> _raceSerializationNpcAssignments = new();
    
    private Dictionary<FormKey, Dictionary<ModKey, List<string>>> _alteredPropertiesMap = new();
    private Dictionary<ModKey, Dictionary<FormKey, RaceEditInfo>> _racesToModify = new();

    public RaceHandler(EnvironmentStateProvider environmentStateProvider, Settings settings, RecordHandler recordHandler, AssetHandler assetHandler, Auxilliary aux, RecordDeltaPatcher recordDeltaPatcher)
    {
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
        _recordHandler = recordHandler;
        _assetHandler = assetHandler;
        _aux = aux;
        _recordDeltaPatcher = recordDeltaPatcher;
    }

    internal class RaceEditInfo
    {
        public IModContext<ISkyrimMod, ISkyrimModGetter, IRace, IRaceGetter> AppearanceModRaceContext;
        public IEnumerable<IModContext<ISkyrimMod, ISkyrimModGetter, IRace, IRaceGetter>> RaceContexts;
        public ModSetting AppearanceModSetting;

        public RaceEditInfo(IModContext<ISkyrimMod, ISkyrimModGetter, IRace, IRaceGetter> appearanceModRaceContext,
            IEnumerable<IModContext<ISkyrimMod, ISkyrimModGetter, IRace, IRaceGetter>> raceContexts,
            ModSetting appearanceModSetting)
        {
            AppearanceModRaceContext = appearanceModRaceContext;
            RaceContexts = raceContexts;
            AppearanceModSetting = appearanceModSetting;
        }
    }

    public void Reinitialize()
    {
        _alteredPropertiesMap.Clear();
        _racesToModify.Clear();
        _racesToSerializeForYaml.Clear();
    }

    public async Task ApplyRaceChanges(string outputBaseDir)
    {
        if (_racesToModify.Any())
        {
            string modifyingRaceMods = string.Join(", ", _racesToModify.Keys.Select(x => x.FileName));
            AppendLog(Environment.NewLine + $"Patching Race Overrides from {modifyingRaceMods}. This may take some time.",
                false, true);
        }
        foreach (var entry in _racesToModify)
        {
            var raceModKey = entry.Key;
            foreach (var raceToPatch in entry.Value)
            {
                var appearanceRaceContext = raceToPatch.Value.AppearanceModRaceContext;
                var allRaceContexts = raceToPatch.Value.RaceContexts;
                var modSetting = raceToPatch.Value.AppearanceModSetting;
                
                var currentRaceHandlingMode = modSetting.ModRaceHandlingMode ?? _settings.DefaultRaceHandlingMode;
                switch (currentRaceHandlingMode)
                {
                    case RaceHandlingMode.IgnoreRace:
                        break;
                    case RaceHandlingMode.DuplicateAndRemapRace:
                        throw new NotImplementedException("Duplicate Race Handling Mode");
                    case RaceHandlingMode.ForwardWinningOverrides:
                        await HandleForwardWinningOverridesRace(appearanceRaceContext, allRaceContexts, modSetting,
                            raceModKey, outputBaseDir);
                        break;
                }
            }
        }
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
            AppendLog(
                $"      NPC {patchNpc.EditorID} has no plugin from {appearanceModSetting.DisplayName}. Skipping race handling.");
            return;
        }

        // Check if the selected plugin's race record exits
        if (sourceNpcIfUsed.Race.IsNull)
        {
            AppendLog(
                $"      NPC {patchNpc.EditorID} has a null Race record from {appearanceModSetting.DisplayName}. Skipping race handling.");
            return;
        }

        // Check if the appearance mod provides an override for the NPC's current race
        var raceContexts =
            _environmentStateProvider.LinkCache
                .ResolveAllContexts<IRace, IRaceGetter>(sourceNpcIfUsed.Race.FormKey).ToArray();
        var raceContextFromAppearanceMod = raceContexts?.FirstOrDefault(x => x.ModKey.Equals(appearanceModKey));
        if (raceContextFromAppearanceMod == null)
        {
            AppendLog(
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
            AppendLog(
                $"      NPC {patchNpc.EditorID} uses new custom race {raceGetterFromAppearanceMod.EditorID} ({raceGetterFromAppearanceMod.FormKey}) defined in its appearance mod {appearanceModKey.FileName}.");
            AppendLog($"        Merging race and its dependencies from {appearanceModKey.FileName}.");

            // Ensure patchNpc points to this race (it should if sourceNpcIfUsed was its origin and had this race)
            patchNpc.Race.SetTo(raceGetterFromAppearanceMod);

            // Add the race record (as it exists in the patch, which is an override of currentNpcRaceRecord)
            // to the map for dependency duplication.
            _recordHandler.DuplicateInFormLink(patchNpc.Race, raceGetterFromAppearanceMod.ToLink(),
                _environmentStateProvider.OutputMod,
                appearanceModSetting.CorrespondingModKeys, appearanceModKey, RecordHandler.RecordLookupFallBack.Origin);
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
            AppendLog(
                $"      NPC {patchNpc.EditorID} uses overriden race {raceGetterFromAppearanceMod.EditorID} ({raceGetterFromAppearanceMod.FormKey}).");
            AppendLog($"        Appearance mod {appearanceModKey.FileName} modifies this race.");
            
            patchNpc.Race.SetTo(raceGetterFromAppearanceMod);

            switch (_settings.DefaultRaceHandlingMode)
            {
                case RaceHandlingMode.ForwardWinningOverrides:
                    if (!_racesToModify.ContainsKey(raceContextFromAppearanceMod.ModKey))
                    {
                        _racesToModify.Add(raceContextFromAppearanceMod.ModKey, new Dictionary<FormKey, RaceEditInfo>());
                    }

                    if (!_racesToModify[raceContextFromAppearanceMod.ModKey]
                            .ContainsKey(raceContextFromAppearanceMod.Record.FormKey))
                    {
                        _racesToModify[raceContextFromAppearanceMod.ModKey]
                            [raceContextFromAppearanceMod.Record.FormKey] = new RaceEditInfo(raceContextFromAppearanceMod, raceContexts, appearanceModSetting);
                    }
                    break;

                /*
                case RaceHandlingMode.DuplicateAndRemapRace:
                    // currently not exposed.
                    throw new NotImplementedException();
                    break;*/

                case RaceHandlingMode.IgnoreRace:
                    AppendLog(
                        $"        Race Handling Mode: IgnoreRace. No changes made to race record {raceGetterFromAppearanceMod.EditorID} itself by this logic.");
                    break;
            }
        }
    }

    private async Task HandleForwardWinningOverridesRace(
        IModContext<ISkyrimMod, ISkyrimModGetter, IRace, IRaceGetter> appearanceModRaceContext,
        IEnumerable<IModContext<ISkyrimMod, ISkyrimModGetter, IRace, IRaceGetter>> raceContexts,
        ModSetting appearanceModSetting,
        ModKey appearanceModKey,
        string outputBaseDir)
    {
        var originalRaceGetter = raceContexts.Last().Record;
        if (originalRaceGetter == null)
        {
            AppendLog("Error: Could not determine original race context for race handling.");
            return;
        }

        HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>> dependencyRecords = new();
        List<IAssetLinkGetter> assetLinks = new();

        var recordOverrideHandlingMode = appearanceModSetting.ModRecordOverrideHandlingMode ??
                                         _settings.DefaultRecordOverrideHandlingMode;
        
        bool searchDependencyRecords = recordOverrideHandlingMode == RecordOverrideHandlingMode.Include ||
                                       recordOverrideHandlingMode == RecordOverrideHandlingMode.IncludeAsNew;

        if (_settings.PatchingMode == PatchingMode.EasyNPC_Like)
        {
            AppendLog(
                $"        Race Handling: ForwardWinningOverrides (EasyNPC-Like) for race {originalRaceGetter.EditorID}.");
            // In EasyNPC-Like mode, patchNpc is an override of winningNpcOverride.
            // So patchNpc.Race should already point to originalRaceContext.FormKey.
            // We need to override originalRaceContext in the patch and forward differing properties.
            // Note the additional possibility that the winning appearance override set the NPC race to a DIFFERENT vanilla race, so we stil lneed to set the race first.

            var racePropertyDiffs =
                _recordDeltaPatcher.GetPropertyDiffs(appearanceModRaceContext.Record, originalRaceGetter);

            if (racePropertyDiffs.Any())
            {
                var winningRaceGetter = raceContexts.First().Record;
                if (winningRaceGetter == null)
                {
                    AppendLog("Error: Could not determine winning race context for race handling.");
                    return;
                }

                var winningRaceRecord = _environmentStateProvider.OutputMod.Races.GetOrAddAsOverride(winningRaceGetter);
                assetLinks.AddRange(_aux.ShallowGetAssetLinks(winningRaceRecord));
                
                _recordDeltaPatcher.ApplyPropertyDiffs(winningRaceRecord, racePropertyDiffs);
                
                // Get required asset file paths BEFORE remapping (to faciliate search by plugin)
                if (searchDependencyRecords)
                {
                    dependencyRecords = _recordHandler.DeepGetOverriddenDependencyRecords(winningRaceGetter,
                        appearanceModSetting.CorrespondingModKeys);

                    foreach (var ctx in dependencyRecords)
                    {
                        ctx.GetOrAddAsOverride(_environmentStateProvider.OutputMod);
                        var assets = _aux.ShallowGetAssetLinks(ctx.Record).Where(x => !assetLinks.Contains(x));
                        assetLinks.AddRange(assets);
                    }
                }
                
                // remap dependencies if needed
                if (appearanceModSetting.MergeInDependencyRecords)
                {
                    _recordHandler.DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod,
                        winningRaceRecord, appearanceModSetting.CorrespondingModKeys, appearanceModKey, true, RecordHandler.RecordLookupFallBack.Winner);

                    foreach (var ctx in dependencyRecords)
                    {
                        _recordHandler.DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod,
                            ctx.Record,
                            appearanceModSetting.CorrespondingModKeys, appearanceModKey, true, RecordHandler.RecordLookupFallBack.Winner);
                    }
                }
            }
            else
            {
                AppendLog(
                    $"          Race properties are identical to original context. No race forwarding needed.");
            }
        }
        else // Default Patching Mode
        {

            /* Skip YAML serialization for now; may come back to it
            AppendLog(
                $"          Generating YAML record of race version from {appearanceModKey.FileName} for {patchNpc.EditorID}.");

            var existingSerialization = _racesToSerializeForYaml.FirstOrDefault(
                x => x.AppearanceModKey.Equals(appearanceModKey) &&
                     x.OriginalRaceFormKey.Equals(originalRaceGetter.FormKey));

            if (existingSerialization != null)
            {
                AppendLog(
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
            AppendLog(
                $"          Scheduled YAML generation for race {appearanceModRaceContext.Record.EditorID}.");
            */

            // Add race to patch exactly how it appears in the source mod
            var appearanceRaceRecord = _environmentStateProvider.OutputMod.Races.GetOrAddAsOverride(appearanceModRaceContext.Record);
            assetLinks.AddRange(_aux.ShallowGetAssetLinks(appearanceRaceRecord));
            
            if (searchDependencyRecords)
            {
                dependencyRecords = _recordHandler.DeepGetOverriddenDependencyRecords(appearanceRaceRecord,
                    appearanceModSetting.CorrespondingModKeys);

                foreach (var ctx in dependencyRecords)
                {
                    var assets = _aux.ShallowGetAssetLinks(ctx.Record).Where(x => !assetLinks.Contains(x));
                    assetLinks.AddRange(assets);
                    ctx.GetOrAddAsOverride(_environmentStateProvider.OutputMod);
                }
            }
            
            // remap dependencies if needed
            if (appearanceModSetting.MergeInDependencyRecords)
            {
                _recordHandler.DuplicateFromOnlyReferencedGetters(_environmentStateProvider.OutputMod,
                    appearanceRaceRecord, appearanceModSetting.CorrespondingModKeys, appearanceModKey, true, RecordHandler.RecordLookupFallBack.Origin);

                assetLinks = _aux.DeepGetAssetLinks(appearanceRaceRecord, appearanceModSetting.CorrespondingModKeys);
            }
        }

        await _assetHandler.CopyAssetLinkFiles(assetLinks, appearanceModSetting, outputBaseDir);
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
        AppendLog("Serialized saved races to " + ouputPath);
    }

    // --- Forward Race Edits From YAML ---
    private async Task ForwardRaceEditsFromYamlAsync()
    {
        AppendLog("\n--- Initiating Forward Race Edits from YAML ---");
        if (!_environmentStateProvider.EnvironmentIsValid)
        {
            AppendLog("Environment not valid. Cannot proceed.");
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
            AppendLog("No YAML file selected. Operation cancelled.");
            return;
        }

        string yamlFilePath = openFileDialog.FileName;
        AppendLog($"Processing YAML file: {yamlFilePath}");

        try
        {

        }
        catch (Exception ex)
        {
            AppendLog(
                $"An unexpected error occurred while forwarding race edits: {ExceptionLogger.GetExceptionStack(ex)}");
        }
        finally
        {
            AppendLog("--- Finished Forward Race Edits from YAML ---");
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