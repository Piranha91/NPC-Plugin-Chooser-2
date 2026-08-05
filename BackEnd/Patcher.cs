using System.Diagnostics;
using System.IO;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Allocators;
using Mutagen.Bethesda.Plugins.Analysis;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Utility;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class Patcher : OptionalUIModule
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Settings _settings;
    private readonly Validator _validator;
    private readonly AssetHandler _assetHandler;
    private readonly RecordHandler _recordHandler;
    private readonly Auxilliary _aux;
    private readonly RecordDeltaPatcher _recordDeltaPatcher;
    private readonly PluginProvider _pluginProvider;
    private readonly BsaHandler _bsaHandler;
    private readonly SkyPatcherInterface _skyPatcherInterface;
    private readonly WigForwarder _wigForwarder;
    private readonly HeadPartWigConverter _headPartWigConverter;
    private readonly ForwardedOutfitDistributor _forwardedOutfitDistributor;
    private readonly OutfitDisplayResolver _outfitDisplayResolver;

    // FaceGen NIFs that need their baked hair shape(s) stripped after the
    // asset copy completes (ForwardToSkin wig handling; see WigForwarder).
    // Populated per NPC during the (parallel) patch loop, applied once after
    // MonitorAndWaitForAllTasks.
    private readonly System.Collections.Concurrent.ConcurrentBag<(string NifPath, HashSet<string> ShapeNames, string NpcIdentifier)>
        _pendingWigNifEdits = new();

    // FaceGen NIFs that need the wig scene BAKED in after the asset copy
    // completes (ConvertToHeadParts wig handling; see HeadPartWigConverter).
    // The bake itself strips the donor hair shapes (the strip list rides in the
    // Result), so these NPCs are deliberately NOT also queued through
    // _pendingWigNifEdits for hair. Drained destructively (TryTake) because
    // RunPatchingLogic runs once per output plugin and re-baking an
    // already-baked FaceGen would duplicate the wig shapes.
    private readonly System.Collections.Concurrent.ConcurrentBag<(string NifPath, HeadPartWigConverter.Result Convert, string NpcIdentifier)>
        _pendingWigBakes = new();

    // FaceGen NIFs whose shapes must be renamed to follow head-part records this run
    // duplicated under a new EditorID (Include As New appends "_<sourcePlugin>"). The engine
    // pairs head parts to baked geometry BY NAME, so a renamed record whose shape kept the old
    // name dark-faces. Applied after the two wig phases, which is what keeps it out of their
    // way: the bake has already stripped/renamed the shapes it owns by then, so the old names
    // it removed are simply absent here.
    private readonly System.Collections.Concurrent.ConcurrentBag<(string NifPath, Dictionary<string, string> Renames, string NpcIdentifier)>
        _pendingHeadPartRenames = new();

    // Output-relative paths of FaceGen meshes the phases above actually rewrote. Recorded into
    // NPC_Token.json: an edited file is deliberately no longer byte-identical to the appearance
    // mod's copy, which is how "Validate Output" otherwise proves nothing overwrote our output,
    // so without this an intentional edit reads as a lost conflict.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte>
        _editedFaceGenPaths = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, ModSetting> _modSettingsMap;
    // Lazy: the analyzer hangs off CharacterPreviewCache and its asset-resolver chain, and is only
    // needed on the rare ladder rows that borrow a mesh from outside the selected mod.
    private readonly Lazy<FaceGenConsistencyAnalyzer> _faceGenConsistency;

    /// <summary>NPCs the FaceGen ladder refused to patch this run. Collected so the end-of-run
    /// summary names them: a skip that only exists as one line in a log of thousands reads as the
    /// NPC having been patched, which is exactly the wrong impression.</summary>
    private readonly List<(string Npc, string Mod, string Reason)> _faceGenSkippedNpcs = new();

    /// <summary>NPCs whose Include Outfit write landed in a dead record field this run — the
    /// Inventory template flag makes the engine take the whole inventory, outfit included, from
    /// the template (see <see cref="RecordOutfitIsInert"/>). The face still applies; only the
    /// outfit does not, so this is reported rather than treated as a skip.</summary>
    private readonly List<(string Npc, string Mod, string Template)> _inertOutfitNpcs = new();

    /// <summary>NPCs whose chosen appearance could not reach them this run because their output
    /// record still inherits its face (see <see cref="FaceGenLadder.KeepsInheritedFace"/>). They are
    /// patched normally and simply keep showing their template's face, so — like
    /// <see cref="_inertOutfitNpcs"/> — this is a report and not a skip. <c>TemplateSelection</c> is
    /// the mod chosen for the template itself, when it has one: those NPCs do change appearance, to
    /// the template's choice, which is a materially different outcome worth saying.</summary>
    private readonly List<(string Npc, string Mod, string Template, string? TemplateSelection)>
        _inheritedFaceNpcs = new();

    /// <summary>NPCs whose Traits chain WAS flattened this run, but for whom the chosen mod supplied
    /// neither half of the face, so the flattened record carries the origin's or the load-order
    /// winner's face instead (see <see cref="FaceGenLadderDecision.FlattenedFaceCameFromElsewhere"/>).
    /// The selection is as undeliverable as in <see cref="_inheritedFaceNpcs"/> and gets the same
    /// forced naming — the two cases used to be reported very differently for no reason the user
    /// could see.</summary>
    private readonly List<(string Npc, string Mod, string Template, string Source)>
        _flattenedFallbackNpcs = new();

    private string _currentRunOutputAssetPath = string.Empty;

    // Plugin -> the mod entry that provides NPCs from it. Lets a resource-only plugin bundled
    // into one mod entry inherit merge-in from the entry that actually owns it (rule 3 in
    // MergeEligibility). Rebuilt per run alongside _modSettingsMap.
    private Dictionary<ModKey, ModSetting> _npcProvidingOwnersByPlugin = new();

    // Folder paths of the mod entries that own each plugin, so a resource-only plugin whose
    // files live in a DIFFERENT mod's folder can still be loaded and read during merge-in.
    // Without this the merge silently aborts ("could not resolve") and the reference dangles
    // exactly as it did before the per-plugin merge existed.
    private Dictionary<ModKey, List<string>> _ownerFolderPathsByPlugin = new();

    private Dictionary<string, IKeywordGetter> _generatedKeywords = new();

    private bool _clearOutputDirectoryOnRun = true;

    // Accumulated token data across all patching cycles for unified JSON output
    private Dictionary<FormKey, NpcAppearanceData> _accumulatedTokenData = new();
    private List<ModKey> _generatedOutputPlugins = new();

    // NPCs that had a selection this run did NOT patch, mapped to the reason: rejected by
    // pre-run screening (handed over by VM_Run) or aborted by the FaceGen ladder. Written to
    // NPC_Token.json alongside the processed set so "Validate Output" can tell "we never
    // touched this NPC" apart from "we patched it and it came out wrong".
    private readonly Dictionary<FormKey, string> _skippedTokenData = new();

    // Tracks which NPCs reference each record added to the OutputMod during patching, so
    // a failed NPC can roll back any records that no other NPC depends on.
    private readonly Dictionary<FormKey, HashSet<FormKey>> _patchedRecordOwners = new();
    private readonly Dictionary<FormKey, Type> _patchedRecordTypes = new();

    // Every ModKey the output plugin is allowed to reference: the active load order plus
    // this app's own output plugin(s). A FormLink pointing anywhere else cannot be written
    // — Mutagen throws "A referenced mod was not present on the load order being sorted
    // against" at save time. Rebuilt at the start of each RunPatchingLogic (the output
    // ModKey differs per iteration when the run splits into several plugins). Consumed by
    // the copy-time check in CopyAppearanceData and by BuildDanglingMasterDiagnostics.
    private HashSet<ModKey> _allowedMasterKeys = new();

    // Per-NPC appearance provenance, recorded as each NPC is patched so a save failure at
    // the very end of the run can still name the mod and plugin a bad reference came from.
    // Written from the (sequentially awaited) patch loop; read only after it completes.
    private readonly Dictionary<FormKey, (string ModName, ModKey SourcePlugin, bool MergeIn)> _npcAppearanceSources = new();

    public const string ALL_NPCS_GROUP = VM_Run.ALL_NPCS_GROUP;
    public const string PluginDescriptionSignature = "Generated By NPC Plugin Chooser 2";

    public Patcher(EnvironmentStateProvider environmentStateProvider, Settings settings, Validator validator,
        AssetHandler assetHandler, RecordHandler recordHandler, Auxilliary aux, RecordDeltaPatcher recordDeltaPatcher,
        PluginProvider pluginProvider, BsaHandler bsaHandler, SkyPatcherInterface skyPatcherInterface,
        WigForwarder wigForwarder, HeadPartWigConverter headPartWigConverter,
        ForwardedOutfitDistributor forwardedOutfitDistributor,
        OutfitDisplayResolver outfitDisplayResolver,
        Lazy<FaceGenConsistencyAnalyzer> faceGenConsistency)
    {
        _faceGenConsistency = faceGenConsistency;
        _environmentStateProvider = environmentStateProvider;
        _settings = settings;
        _validator = validator;
        _assetHandler = assetHandler;
        _recordHandler = recordHandler;
        _aux = aux;
        _recordDeltaPatcher = recordDeltaPatcher;
        _pluginProvider = pluginProvider;
        _bsaHandler = bsaHandler;
        _skyPatcherInterface = skyPatcherInterface;
        _wigForwarder = wigForwarder;
        _headPartWigConverter = headPartWigConverter;
        _forwardedOutfitDistributor = forwardedOutfitDistributor;
        _outfitDisplayResolver = outfitDisplayResolver;
    }

    public async Task PreInitializationLogicAsync()
    {
        AppendLog("Pre-Indexing loose file paths...", false, true);
        await _assetHandler.PopulateExistingFilePathsAsync(_settings.ModSettings);
        AppendLog("Finished Pre-Indexing loose file paths.", false, true);

        AppendLog("Pre-Indexing BSA file paths...", false, true);
        await _bsaHandler.PopulateBsaContentPathsAsync(_settings.ModSettings,
            _environmentStateProvider.SkyrimVersion.ToGameRelease());
        AppendLog("Finished Pre-Indexing BSA file paths.", false, true);
        
        _generatedKeywords.Clear();
        
        // Clear accumulated token data at the start of a new patching session
        _accumulatedTokenData.Clear();
        _generatedOutputPlugins.Clear();
        // Runs before screening, so the rejections VM_Run hands over below survive this reset.
        _skippedTokenData.Clear();
    }

    /// <summary>
    /// Records the selections pre-run screening rejected, so they land in NPC_Token.json's
    /// skipped map. Called between screening and patching — <see cref="PreInitializationLogicAsync"/>
    /// has already cleared the map by then, and the FaceGen ladder adds its own aborts during the
    /// run.
    /// </summary>
    public void RecordScreenedOutNpcs(IReadOnlyDictionary<FormKey, string> rejections)
    {
        foreach (var (npcFormKey, reason) in rejections)
        {
            _skippedTokenData[npcFormKey] = "Skipped before patching: " + reason;
        }
    }

    private void RegisterRecordOwnership(FormKey npcFormKey, IMajorRecordGetter record,
        HashSet<FormKey> npcContributions)
    {
        var formKey = record.FormKey;
        npcContributions.Add(formKey);
        if (!_patchedRecordOwners.TryGetValue(formKey, out var owners))
        {
            owners = new HashSet<FormKey>();
            _patchedRecordOwners[formKey] = owners;
            _patchedRecordTypes[formKey] = record.Registration.GetterType;
        }
        owners.Add(npcFormKey);
    }

    private void RegisterRecordOwnerships(FormKey npcFormKey, IEnumerable<IMajorRecordGetter> records,
        HashSet<FormKey> npcContributions)
    {
        foreach (var rec in records)
        {
            RegisterRecordOwnership(npcFormKey, rec, npcContributions);
        }
    }

    private int RollbackNpcContributions(FormKey npcFormKey, HashSet<FormKey> contributedRecordKeys)
    {
        int removedCount = 0;
        foreach (var recordFormKey in contributedRecordKeys)
        {
            if (!_patchedRecordOwners.TryGetValue(recordFormKey, out var owners)) continue;
            owners.Remove(npcFormKey);
            if (owners.Count == 0)
            {
                if (_patchedRecordTypes.TryGetValue(recordFormKey, out var recordType))
                {
                    try
                    {
                        _environmentStateProvider.OutputMod.Remove(recordFormKey, recordType);
                        removedCount++;
                        RecordProvenanceDiag.RemoveOutputRecord(recordFormKey);
                    }
                    catch (Exception ex)
                    {
                        AppendLog(
                            $"        WARNING: Failed to remove orphaned record {recordFormKey} during rollback: {ex.Message}",
                            true, true);
                    }
                }
                _patchedRecordOwners.Remove(recordFormKey);
                _patchedRecordTypes.Remove(recordFormKey);
            }
        }
        return removedCount;
    }

    /// <summary>
    /// Scans the finished output plugin for any FormLink that points at a plugin not
    /// present in the active load order — the condition that makes Mutagen throw at
    /// master-sort time ("A referenced mod was not present on the load order...").
    /// For each offending record it reports, where known, the source record it was
    /// merged in from and the NPC(s) whose patching pulled it in, so the user can see
    /// the root cause instead of a bare output FormKey. Returns an empty string if no
    /// dangling reference is found (the failure was something else).
    /// </summary>
    private string BuildDanglingMasterDiagnostics()
    {
        var outputMod = _environmentStateProvider.OutputMod;
        if (outputMod == null) return string.Empty;

        // Masters Mutagen will accept: everything in the sorted load order, plus the
        // output mod's own key (self-references are fine).
        var allowedMasters = new HashSet<ModKey>(_environmentStateProvider.LoadOrderModKeys) { outputMod.ModKey };

        // Output record FormKey -> every dangling FormKey it references.
        var offenders = new Dictionary<FormKey, (IMajorRecordGetter Record, HashSet<FormKey> DanglingTargets)>();
        var allMissingMasters = new HashSet<ModKey>();

        foreach (var record in outputMod.EnumerateMajorRecords())
        {
            foreach (var link in record.EnumerateFormLinks())
            {
                if (link.FormKey.IsNull) continue;
                var refMod = link.FormKey.ModKey;
                if (allowedMasters.Contains(refMod)) continue;

                if (!offenders.TryGetValue(record.FormKey, out var entry))
                {
                    entry = (record, new HashSet<FormKey>());
                    offenders[record.FormKey] = entry;
                }
                entry.DanglingTargets.Add(link.FormKey);
                allMissingMasters.Add(refMod);
            }
        }

        if (offenders.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("======= Diagnosing missing-master save failure =======");
        sb.AppendLine(
            $"The output plugin references {allMissingMasters.Count} plugin(s) that are NOT in your active load order:");
        foreach (var missing in allMissingMasters.OrderBy(m => m.FileName.String))
        {
            sb.AppendLine($"    {missing.FileName}{DescribeMissingPluginInstallState(missing)}");
        }

        sb.AppendLine();
        sb.AppendLine(
            $"{offenders.Count} record(s) in the output plugin still point at the missing plugin(s). " +
            "These were merged in as appearance dependencies but a reference was left dangling:");
        sb.AppendLine();

        foreach (var kvp in offenders)
        {
            var record = kvp.Value.Record;
            sb.AppendLine(
                $"  • [{record.GetType().Name}] {record.FormKey}" +
                (string.IsNullOrEmpty(record.EditorID) ? string.Empty : $"  (EditorID: {record.EditorID})"));

            if (_recordHandler.TryGetMergedRecordOrigin(record.FormKey, out var origin))
            {
                sb.AppendLine(
                    $"      Merged in from source record: {origin.SourceFormKey}" +
                    (string.IsNullOrEmpty(origin.SourceEditorId) ? string.Empty : $" (EditorID: {origin.SourceEditorId})"));
            }
            else if (_npcAppearanceSources.TryGetValue(record.FormKey, out var appearanceSource))
            {
                // Not merged in: this IS a patched NPC record, so the dangling link was
                // carried over by the appearance copy itself rather than by a dependency walk.
                sb.AppendLine(
                    $"      This is a patched NPC record. Its appearance was taken from mod " +
                    $"'{appearanceSource.ModName}', plugin '{appearanceSource.SourcePlugin.FileName}' " +
                    $"(Merge In Dependency Records = {(appearanceSource.MergeIn ? "ON" : "OFF")}).");
            }
            else
            {
                sb.AppendLine("      (No merge-in provenance recorded — may be an originally-authored output record.)");
            }

            if (_patchedRecordOwners.TryGetValue(record.FormKey, out var owners) && owners.Count > 0)
            {
                sb.AppendLine("      Pulled in while patching NPC(s):");
                foreach (var ownerKey in owners)
                {
                    sb.AppendLine($"         - {DescribeNpc(ownerKey)}");
                }
            }

            // The actionable part: WHICH field on this record points at the missing plugin,
            // and at what. Without it the user only learns that some plugin is missing, not
            // what in their setup depends on it.
            sb.AppendLine("      Dangling reference(s):");
            var fieldNames = RecordFieldPathMapper.MapFieldNames(record, kvp.Value.DanglingTargets);
            foreach (var target in kvp.Value.DanglingTargets.OrderBy(t => t.ToString()))
            {
                string field = fieldNames.TryGetValue(target, out var names) && names.Count > 0
                    ? string.Join(" / ", names.OrderBy(n => n))
                    : "(field unknown)";
                sb.AppendLine($"         - {field} = {target}");
            }

            sb.AppendLine();
        }

        sb.AppendLine(
            "Likely cause: a record copied from one of your appearance mods depends on the plugin(s) above, " +
            "which are not enabled in your load order. Enable the missing plugin(s), or change the appearance " +
            "selection for the NPC(s) listed so they no longer pull in records that need a master you don't have.");

        return sb.ToString();
    }

    /// <summary>
    /// Says whether a plugin that is missing from the load order is nonetheless installed —
    /// i.e. present in the folders of one of the configured appearance mods, or sitting in the
    /// game's Data folder. "Installed but not enabled" and "not present at all" call for very
    /// different fixes, and the raw Mutagen error distinguishes neither.
    /// </summary>
    private string DescribeMissingPluginInstallState(ModKey missing)
    {
        try
        {
            var owningMods = _settings.ModSettings
                .Where(ms => ms.CorrespondingModKeys.Contains(missing))
                .Select(ms => ms.DisplayName)
                .Distinct()
                .ToList();

            if (owningMods.Any())
            {
                return $"  — installed as part of appearance mod(s) [{string.Join(", ", owningMods)}] " +
                       "but NOT enabled in your load order";
            }

            var dataPath = _environmentStateProvider.DataFolderPath;
            if (!string.IsNullOrWhiteSpace(dataPath.Path) &&
                File.Exists(Path.Combine(dataPath.Path, missing.FileName.String)))
            {
                return "  — present in your game's Data folder but NOT enabled in your load order";
            }

            return "  — not found in any configured mod folder or in the game's Data folder";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Best-effort human-readable label for an NPC FormKey, for diagnostics.</summary>
    private string DescribeNpc(FormKey npcFormKey)
    {
        try
        {
            if (_environmentStateProvider.LinkCache != null &&
                _environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey, out var npc) && npc != null)
            {
                string label = npc.Name?.String ?? npc.EditorID ?? string.Empty;
                return string.IsNullOrEmpty(label) ? npcFormKey.ToString() : $"{label} ({npcFormKey})";
            }
        }
        catch
        {
            // fall through to the bare FormKey
        }
        return npcFormKey.ToString();
    }

    public Dictionary<string, ModSetting> BuildModSettingsMap()
    {
        // --- Build Mod Settings Map ---
        _modSettingsMap = _settings.ModSettings
            .Where(ms => !string.IsNullOrWhiteSpace(ms.DisplayName))
            .GroupBy(ms => ms.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        AppendLog($"Built lookup map for {_modSettingsMap.Count} unique Mod Settings."); // Verbose only

        BuildCrossModPluginIndexes();
        return _modSettingsMap;
    }

    /// <summary>
    /// Builds the two cross-mod plugin indexes the per-plugin merge decision needs: who owns
    /// each plugin as an NPC provider, and where each plugin's files live. Both are keyed by
    /// plugin and span every configured mod entry, because a resource-only plugin bundled into
    /// one entry is frequently a full mod entry of its own elsewhere in the mods list.
    /// </summary>
    private void BuildCrossModPluginIndexes()
    {
        _npcProvidingOwnersByPlugin = MergeEligibility.BuildNpcProvidingOwnerIndex(_settings.ModSettings);

        _ownerFolderPathsByPlugin = new Dictionary<ModKey, List<string>>();
        foreach (var mod in _settings.ModSettings)
        {
            if (mod?.CorrespondingModKeys == null || mod.CorrespondingFolderPaths == null) continue;
            if (mod.CorrespondingFolderPaths.Count == 0) continue;

            foreach (var key in mod.CorrespondingModKeys)
            {
                if (!_ownerFolderPathsByPlugin.TryGetValue(key, out var folders))
                {
                    folders = new List<string>();
                    _ownerFolderPathsByPlugin[key] = folders;
                }

                foreach (var folder in mod.CorrespondingFolderPaths)
                {
                    if (!string.IsNullOrWhiteSpace(folder) && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    {
                        folders.Add(folder);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The folders to search for a mod's plugins during merge-in: its own, plus the folders of
    /// whichever mod entries own its merge-eligible plugins. A resource-only plugin is often
    /// installed as its own mod (with its own folder) and merely LISTED under the appearance
    /// mod, so without the owner's folders the plugin can't be loaded and the merge aborts.
    /// </summary>
    private HashSet<string> BuildMergeSourceFolderPaths(HashSet<string> ownFolderPaths,
        IEnumerable<ModKey> mergeEligiblePlugins)
    {
        var folders = new HashSet<string>(ownFolderPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in mergeEligiblePlugins)
        {
            if (_ownerFolderPathsByPlugin.TryGetValue(plugin, out var ownerFolders))
            {
                folders.UnionWith(ownerFolders);
            }
        }
        return folders;
    }

    /// <summary>
    /// Per-NPC log of the merge decision for each of the mod's plugins, with the reason. This is
    /// the trace that explains a dangling reference: a link into a plugin listed as "no" here was
    /// left as a bare reference, which only works if that plugin is in the load order.
    /// </summary>
    private void LogMergeEligibility(ModSetting appearanceModSetting, HashSet<ModKey> mergeEligiblePlugins)
    {
        NpcDiagnosticLogger.Log(
            $"  Merge-in eligibility for '{appearanceModSetting.DisplayName}' " +
            $"(mod-level Merge Dependencies = {(appearanceModSetting.MergeInDependencyRecords ? "ON" : "OFF")}):");

        foreach (var key in appearanceModSetting.CorrespondingModKeys.Distinct())
        {
            bool eligible = mergeEligiblePlugins.Contains(key);
            string reason;
            if (appearanceModSetting.PluginMergeInOverrides != null &&
                appearanceModSetting.PluginMergeInOverrides.ContainsKey(key))
            {
                reason = "explicit per-plugin override";
            }
            else if (appearanceModSetting.ResourceOnlyModKeys == null ||
                     !appearanceModSetting.ResourceOnlyModKeys.Contains(key))
            {
                reason = "not resource-only; follows the mod's own Merge Dependencies";
            }
            else if (_npcProvidingOwnersByPlugin.TryGetValue(key, out var owner) &&
                     !MergeEligibility.IsSameModEntry(owner, appearanceModSetting))
            {
                reason = $"resource-only; inherited from owning mod '{owner.DisplayName}'";
            }
            else
            {
                reason = "resource-only with no NPC-providing owner; defaults to merging";
            }

            NpcDiagnosticLogger.Log($"    - {key.FileName}: merge={(eligible ? "YES" : "no")} ({reason})");
        }
    }

    public async Task RunPatchingLogic(List<KeyValuePair<FormKey, ScreeningResult>> selectionsToProcess, bool showFinalMessage, bool isFirstIteration, CancellationToken ct)
    {
        ResetLog();
        UpdateProgress(0, 1, "Initializing...");
        AppendLog("Starting patch generation...");

        // Local dictionary for this batch's processed NPCs
        var processedNpcsTokenData = new Dictionary<FormKey, NpcAppearanceData>();

        if (_environmentStateProvider.Status != EnvironmentStateProvider.EnvironmentStatus.Valid || _environmentStateProvider.LoadOrder == null)
        {
            AppendLog("ERROR: Environment is not valid. Aborting.", true);
            ResetProgress();
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.OutputDirectory))
        {
            AppendLog("ERROR: Output Directory is not set. Aborting.", true);
            ResetProgress();
            return;
        }
        
        _environmentStateProvider.CurrentAllocator = new TextFileFormKeyAllocator(_environmentStateProvider.OutputMod, _environmentStateProvider.GetAllocatorPath());
        _environmentStateProvider.OutputMod.SetAllocator(_environmentStateProvider.CurrentAllocator);

        if (!_modSettingsMap.Any())
        {
            BuildModSettingsMap();
        }

        // BSA readers this run opens (refcount +1 per entry, one list occurrence
        // per bump). Released 1:1 in the finally below — NOT via
        // UnloadAllBsaReaders, whose hard wipe also disposed the readers the
        // CharacterViewer BSA adapter opened at startup; the adapter never
        // re-opens (EnsureAllArchivesOpened latches), so post-run mugshot
        // extractions failed BSA-CACHE-MISS and the renderer cached the misses
        // as NotFound for the session (headless renders).
        var openedBsaPaths = new List<string>();

        try
        {
            if (isFirstIteration)
            {
                _assetHandler.Initialize(); // asset handler should only be reinitialized once regardless of how many output plugins there are.
                GenerateKeywords();
                _patchedRecordOwners.Clear();
                _patchedRecordTypes.Clear();
                _npcAppearanceSources.Clear();
                _raceDriftUsage.Clear();
                _raceDriftFindings.Clear();
                _recordHandler.ResetMergedRecordTracking();
                _pendingWigNifEdits.Clear();
                _pendingWigBakes.Clear();
                _pendingHeadPartRenames.Clear();
                _editedFaceGenPaths.Clear();
                _headPartWigConverter.ResetSession(); // collision guards + temp BSA extractions from the last run
                RecordProvenanceDiag.Reset(); // opt-in per-run record provenance report (no-op unless enabled)
            }

            _recordDeltaPatcher.Reinitialize(true);

            string baseOutputDirectory;
            bool isSpecifiedDirectory = false;
            // Check if the provided path is a fully qualified path (e.g., "C:\My Output").
            // Path.IsPathRooted correctly distinguishes "NPC Output" from "C:\NPC Output".
            if (Path.IsPathRooted(_settings.OutputDirectory))
            {
                // If it's a full path, use it directly, whether it exists or not.
                baseOutputDirectory = _settings.OutputDirectory;
                isSpecifiedDirectory = true;
            }
            else
            {
                // If it's a simple name (relative path), treat it as a subdirectory of the mods folder.
                baseOutputDirectory = Path.Combine(_settings.ModsFolder, _settings.OutputDirectory);
                // isSpecifiedDirectory remains false, which is correct for this case.
            }

            // The baseOutputDirectory is already determined (e.g., "modsDir\NPC Output" or "C:\Mods\NPC Output")
            _currentRunOutputAssetPath = baseOutputDirectory;

            // Now, append a timestamp if the setting is enabled, regardless of whether the path was specified or not.
            if (_settings.AppendTimestampToOutputDirectory)
            {
                // Use the user-requested timestamp format.
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

                // Append the timestamp directly to the path string with a space.
                // This changes "C:\Mods\NPC Output" to "C:\Mods\NPC Output 2025-05-29_13-42-12".
                _currentRunOutputAssetPath = $"{baseOutputDirectory} {timestamp}";
            }

            AppendLog($"Using output asset directory: {_currentRunOutputAssetPath}", false, true);
            try
            {
                Directory.CreateDirectory(_currentRunOutputAssetPath);
                AppendLog("Ensured output asset directory exists.");
            }
            catch (Exception ex)
            {
                AppendLog(
                    $"ERROR: Could not create output asset directory... Aborting. Error: {ExceptionLogger.GetExceptionStack(ex)}",
                    true);
                ResetProgress();
                return;
            }

            // Reinitialize whether in SkyPatcher mode or not, to avoid stale output. On the
            // first iteration both sweep EVERY ini they have ever generated rather than just
            // this plugin's: the directory clear below spares non-asset folders (so SKSE\ is
            // never touched), and a run that splits into fewer plugins than the last one
            // would otherwise leave orphaned configs still applying in game.
            _skyPatcherInterface.Reinitialize(_currentRunOutputAssetPath, isFirstIteration);
            _forwardedOutfitDistributor.Reinitialize(_currentRunOutputAssetPath,
                _environmentStateProvider.OutputMod.ModKey, isFirstIteration);

            // IMPORTANT: The OutputMod is now created and set by VM_Run before this method is called.
            // We no longer create it here, we just use the one that's already set.
            AppendLog($"Initialized output mod: {_environmentStateProvider.OutputMod.ModKey.FileName}");
            _generatedOutputPlugins.Add(_environmentStateProvider.OutputMod.ModKey);

            // Snapshot what the output plugin may legally reference for this iteration
            // (see _allowedMasterKeys). Includes plugins generated by earlier iterations
            // of the same run, which are legitimate masters of this one.
            _allowedMasterKeys = new HashSet<ModKey>(_environmentStateProvider.LoadOrderModKeys);
            _allowedMasterKeys.UnionWith(_generatedOutputPlugins);

            if (_clearOutputDirectoryOnRun && isFirstIteration)
            {
                AppendLog("Clearing output asset directory...");
                try
                {
                    ClearDirectory(_currentRunOutputAssetPath);
                    AppendLog("Output asset directory cleared.");
                }
                catch (Exception ex)
                {
                    AppendLog(
                        $"ERROR: Failed to clear output asset directory: {ExceptionLogger.GetExceptionStack(ex)}. Aborting.",
                        true);
                    ResetProgress();
                    return;
                }
            }

            // Write the NPC_Token.json marker up front, before any plugin or asset is saved. The
            // self-output guard in VM_Mods keys purely on this file's existence to skip this app's
            // own output folder during appearance-mod scanning. It used to be written last, so a
            // crash (or a swallowed save failure) mid-patch could leave partial output on disk with
            // no marker, and the next launch would consume its own output as an appearance mod.
            // Writing an empty-payload marker here closes that window; WriteUnifiedTokenFile
            // overwrites it with the full ProcessedNpcs payload once all batches finish.
            if (isFirstIteration)
            {
                if (WriteTokenFileToDisk(out var bootstrapEx))
                {
                    AppendLog("Wrote bootstrap NPC_Token.json marker.");
                }
                else
                {
                    AppendLog($"WARNING: Could not write bootstrap NPC_Token.json marker: {bootstrapEx}", true, true);
                }
            }

            AppendLog("\nProcessing Valid NPC Appearance Selections...");

            if (!selectionsToProcess.Any())
            {
                AppendLog("No valid NPC selections found or remaining after screening.");
            }
            else
            {
                var groupedSelections = selectionsToProcess
                    .GroupBy(kv => kv.Value.AppearanceModSetting?.DisplayName ?? "[FaceGen/No ModSetting]")
                    .OrderBy(g => g.Key);

                int totalToProcess = selectionsToProcess.Count;
                int overallProgressCounter = 0;
                int processedCount = 0;

                foreach (var npcGroup in groupedSelections)
                {
                    ct.ThrowIfCancellationRequested();

                    using var _ = ContextualPerformanceTracer.BeginContext(npcGroup.Key);

                    AppendLog($"\n--- Loading resources for batch: {npcGroup.Key} ---", false, true);

                    List<ModKey> modKeysForBatch = new();
                    HashSet<string> currentModFolderPaths = new();
                    HashSet<string> loadedPluginPaths = new();

                    await Task.Run(() =>
                    {
                        ModSetting? currentModSetting = null;

                        if (_modSettingsMap.TryGetValue(npcGroup.Key, out currentModSetting) &&
                            currentModSetting != null)
                        {
                            modKeysForBatch.AddRange(currentModSetting.CorrespondingModKeys);

                            // Merge-eligible plugins are frequently installed as their own mod
                            // (own folder) and merely LISTED under this appearance mod, so their
                            // files aren't under this mod's folders. Add the owning entries'
                            // folders before loading, or the merge aborts at lookup time and the
                            // reference it was supposed to fix is left dangling.
                            var batchEligiblePlugins = MergeEligibility.GetMergeEligiblePlugins(
                                currentModSetting, _npcProvidingOwnersByPlugin);
                            currentModFolderPaths = BuildMergeSourceFolderPaths(
                                currentModSetting.CorrespondingFolderPaths.ToHashSet(), batchEligiblePlugins);

                            _pluginProvider.LoadPlugins(modKeysForBatch, currentModFolderPaths, out loadedPluginPaths);
                            _recordHandler.PrimeLinkCachesFor(modKeysForBatch, currentModFolderPaths);
                            _recordHandler.ResetMapping();
                            _wigForwarder.ResetCache();
                            _headPartWigConverter.ResetCache();
                            openedBsaPaths.AddRange(
                                _bsaHandler.OpenBsaReadersFor(currentModSetting, _settings.SkyrimRelease.ToGameRelease()));
                        }
                        else
                        {
                            AppendLog(
                                $"Note: Batch '{npcGroup.Key}' has no associated mod setting. Processing with standard resources.",
                                false, true);
                        }
                    });

                    _recordDeltaPatcher.Reinitialize(false);
                    HashSet<FormKey> searchedOverrideFormKeysForGroup = new HashSet<FormKey>();

                    var npcsInGroup = npcGroup.ToList();
                    for (int i = 0; i < npcsInGroup.Count; i++)
                    {
                        overallProgressCounter++;
                        var kvp = npcsInGroup[i];
                        var npcFormKey = kvp.Key;
                        var result = kvp.Value;
                        var winningNpcOverride = result.WinningNpcOverride;
                        var appearanceModSetting = result.AppearanceModSetting;
                        var appearanceNpcFormKey = kvp.Value.AppearanceNpcFormKey;

                        string selectedModDisplayName = appearanceModSetting?.DisplayName ?? "N/A";
                        string npcIdentifier =
                            $"{winningNpcOverride.Name?.String ?? winningNpcOverride.EditorID ?? npcFormKey.ToString()} ({npcFormKey})";

                        bool shouldUpdateUI = (overallProgressCounter % 10 == 0) ||
                                              (overallProgressCounter == totalToProcess) ||
                                              (overallProgressCounter == 1);

                        if (shouldUpdateUI)
                        {
                            UpdateProgress(overallProgressCounter, totalToProcess,
                                $"Processing: {winningNpcOverride.EditorID ?? npcIdentifier}");
                            await Task.Yield();
                        }

                        await Task.Run(async () =>
                        {
                            using var _ = ContextualPerformanceTracer.Trace("Patcher.MainLoopIteration");

                            // Route this NPC's full patch trace (this AppendLog + every merge-in
                            // call below) to its per-NPC diagnostic file. AsyncLocal context set
                            // here flows into the RecordHandler calls made within this task; it is
                            // isolated to this task, so it does not leak to the next NPC. No-op
                            // unless the user added this NPC to the logging list.
                            NpcDiagnosticLogger.BeginNpc(npcFormKey);
                            NpcDiagnosticLogger.LogSection("PATCHING");

                            // Root-NPC context for the opt-in record-provenance report: every
                            // record merged in until the next call is chained back to this NPC.
                            // Safe as an ambient static because NPCs are processed sequentially.
                            RecordProvenanceDiag.SetCurrentNpc(npcFormKey, winningNpcOverride.EditorID);

                            AppendLog($"- Processing: {npcIdentifier} -> Selected Mod: '{selectedModDisplayName}'");

                            // Tracks records added to OutputMod on behalf of this NPC, used for rollback if patching fails partway.
                            var npcContributions = new HashSet<FormKey>();

                            INpcGetter? appearanceNpcRecord = null;
                            ModKey? appearanceModKey = null;
                            bool correspondingRecordFound = false;

                            if (appearanceModSetting.NpcPluginDisambiguation.TryGetValue(appearanceNpcFormKey,
                                    out var disambiguationKey) &&
                                _recordHandler.TryGetRecordGetterFromMod(appearanceNpcFormKey.ToLink<INpcGetter>(),
                                    disambiguationKey,
                                    currentModFolderPaths,
                                    RecordHandler.RecordLookupFallBack.None, out var disambiguatedRecord) &&
                                disambiguatedRecord != null)
                            {
                                appearanceNpcRecord = disambiguatedRecord as INpcGetter;
                                appearanceModKey = disambiguationKey;
                                correspondingRecordFound = true;
                                AppendLog(
                                    $"    Source: Found specific plugin record override in {disambiguationKey.FileName} (disambiguated).");
                            }
                            else
                            {
                                if (appearanceModSetting.CorrespondingModKeys.Any())
                                {
                                    for (int i = appearanceModSetting.CorrespondingModKeys.Count - 1; i >= 0; i--) // iterate backwards; lowest in list is winner.
                                    {
                                        var candidateKey = appearanceModSetting.CorrespondingModKeys[i];
                                        if (appearanceModSetting.ResourceOnlyModKeys.Contains(candidateKey))
                                        {
                                            continue;
                                        }
                                        
                                        if (_recordHandler.TryGetRecordGetterFromMod(appearanceNpcFormKey.ToLink<INpcGetter>(),
                                                candidateKey, currentModFolderPaths,
                                                RecordHandler.RecordLookupFallBack.None,
                                                out var record) &&
                                            record != null)
                                        {
                                            appearanceNpcRecord = record as INpcGetter;
                                            appearanceModKey = candidateKey;
                                            correspondingRecordFound = true;
                                            AppendLog(
                                                $"    Source: Found plugin record override in {candidateKey.FileName}.");
                                            break;
                                        }
                                    }
                                }
                            }

                            bool isFaceGenOnly = false;
                            if (!correspondingRecordFound)
                            {
                                // Try to find the original source record (e.g., from Skyrim.esm).
                                // Resolve the appearance DONOR (appearanceNpcFormKey), not the target NPC.
                                // For a shared/guest appearance the two differ, and the output record must
                                // carry the donor's base appearance data to match the donor's FaceGen mesh.
                                // Pairing the target's own appearance record with the donor's FaceGen nif
                                // (e.g. a Nord recipient + a Khajiit donor's head mesh) CTDs on spawn.
                                // For a normal replacer donor == target, so this is unchanged.
                                if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(appearanceNpcFormKey,
                                        out var baseNpcGetter, ResolveTarget.Origin))
                                {
                                    AppendLog(
                                        $"    Source: No specific plugin record override found in '{selectedModDisplayName}'. Using the source plugin as the base for assets.");
                                    appearanceNpcRecord = baseNpcGetter;
                                    appearanceModKey = baseNpcGetter.FormKey.ModKey;
                                    isFaceGenOnly = true;
                                }
                                else
                                {
                                    AppendLog(
                                        $"      ERROR: Could not resolve the original source record for {npcIdentifier}. This NPC may be from a missing master file. SKIPPING.",
                                        isError: true,
                                        forceLog: true);

                                    return;
                                }
                            }

                            Npc? patchNpc = null;
                            WigForwarder.Result? wigForward = null;
                            HeadPartWigConverter.Result? wigConvert = null;
                            // Assigned alongside patchNpc below; both are null only on the paths
                            // that never reach the asset stage.
                            FaceGenLadderDecision? faceGenDecision = null;
                            // Merge-in is decided per PLUGIN, not per mod: one mod entry can bundle
                            // plugins that stay in the load order (reference their records) with
                            // resource-only plugins that don't (copy their records, or the output
                            // references an unwritable master). See MergeEligibility. An all-defaults
                            // mod resolves to all-or-nothing, exactly as before this was per-plugin.
                            var mergeEligiblePlugins = appearanceModSetting != null
                                ? MergeEligibility.GetMergeEligiblePlugins(appearanceModSetting,
                                    _npcProvidingOwnersByPlugin)
                                : new HashSet<ModKey>();
                            var mergeInDependencyRecords = mergeEligiblePlugins.Count > 0;
                            var recordOverrideHandlingMode = appearanceModSetting?.ModRecordOverrideHandlingMode ??
                                                             _settings.DefaultRecordOverrideHandlingMode;
                            
                            // When mod setting uses "Default" mode (null), use the global defaults
                            // Otherwise use the mod-specific settings
                            var useDefaultOverrideSettings = appearanceModSetting?.ModRecordOverrideHandlingMode == null;
                            var maxNestedIntervalDepth = useDefaultOverrideSettings 
                                ? _settings.DefaultMaxNestedIntervalDepth 
                                : appearanceModSetting.MaxNestedIntervalDepth;
                            var includeAllOverrides = useDefaultOverrideSettings
                                ? _settings.DefaultIncludeAllOverrides
                                : appearanceModSetting.IncludeAllOverrides;

                            // Declared here (not in the record-handling block below) because the
                            // post-switch directive emission at the bottom of this lambda needs it;
                            // assigned once the appearance record is known to be valid.
                            bool includeOutfit = false;

                            if (isFaceGenOnly)
                            {
                                mergeInDependencyRecords = false;
                                mergeEligiblePlugins.Clear();
                                recordOverrideHandlingMode = RecordOverrideHandlingMode.Ignore;
                            }

                            if (NpcDiagnosticLogger.IsActive && appearanceModSetting != null)
                            {
                                LogMergeEligibility(appearanceModSetting, mergeEligiblePlugins);
                            }

                            // Remember where this NPC's appearance actually came from. A save
                            // failure surfaces thousands of NPCs later with nothing but an output
                            // FormKey; BuildDanglingMasterDiagnostics uses this to name the mod and
                            // the specific plugin that supplied the offending record.
                            if (appearanceModKey.HasValue)
                            {
                                _npcAppearanceSources[npcFormKey] =
                                    (selectedModDisplayName, appearanceModKey.Value, mergeInDependencyRecords);
                            }

                            List<IAssetLinkGetter> assetLinks = new();

                            if (appearanceNpcRecord != null)
                            {

                                // Decide where this NPC's face will come from BEFORE anything is
                                // written. An unassemblable face aborts here, while the output mod
                                // is still untouched for this NPC — no record to remove, no
                                // dependency records to roll back. This supersedes the old
                                // missing-mesh warnings: the ladder covers the same cases and says
                                // what it did about them rather than only that they exist.
                                var faceGenPlan = await ComputeFaceGenDecisionAsync(
                                    npcFormKey, appearanceNpcRecord, appearanceModSetting,
                                    currentModFolderPaths, selectedModDisplayName, npcIdentifier,
                                    isFaceGenOnly);
                                faceGenDecision = faceGenPlan.Decision;

                                // A mesh-only selection may have been re-paired with a different
                                // record from the NPC's own mod of origin — see
                                // TryRepairMeshOnlyRecordPairingAsync. Everything downstream (the
                                // record splice, the asset stage) must use the record the mesh was
                                // actually graded against, not the one we started with.
                                appearanceNpcRecord = faceGenPlan.DonorRecord;

                                if (faceGenDecision.Abort)
                                {
                                    // Verbose-only on purpose: ReportFaceGenSkippedNpcs repeats this
                                    // same sentence per NPC at the end of the run, where it is
                                    // actually read. Forcing it here too printed every skip twice.
                                    AppendLog($"      {faceGenDecision.LogLine}", false, false);
                                    _faceGenSkippedNpcs.Add((npcIdentifier, selectedModDisplayName,
                                        faceGenDecision.AbortReason ?? string.Empty));
                                    // Same wording as ReportFaceGenSkippedNpcs' header, so the token
                                    // reason reads identically to the run log entry it came from.
                                    _skippedTokenData[npcFormKey] = "Face could not be assembled safely: " +
                                                                    (faceGenDecision.AbortReason ?? "no usable face mesh was found.");
                                    return;
                                }

                                // Patched, but its face comes from its template and nothing this run
                                // writes can change that. Collected here rather than in the asset
                                // stage so it is recorded even for an NPC that never reaches it.
                                if (faceGenDecision.InheritedFaceLeftToTemplate)
                                {
                                    _inheritedFaceNpcs.Add((npcIdentifier, selectedModDisplayName,
                                        DescribeFormKey(faceGenDecision.Inputs.SubjectFormKey),
                                        SelectionForNpc(faceGenDecision.Inputs.SubjectFormKey)));
                                }

                                // Flattened, but with nothing of the mod's to flatten: same
                                // undeliverable selection, so the same forced report.
                                if (faceGenDecision.FlattenedFaceCameFromElsewhere)
                                {
                                    _flattenedFallbackNpcs.Add((npcIdentifier, selectedModDisplayName,
                                        DescribeFormKey(faceGenDecision.Inputs.SubjectFormKey),
                                        faceGenDecision.NifChoice == FaceGenSourceChoice.Origin
                                            ? "the mod that originally added it"
                                            : "another mod already installed"));
                                }

                                if (isFaceGenOnly)
                                {
                                    AppendLog("    Source: Original Plugin (FaceGen-only Mod)");
                                }
                                else
                                {
                                    AppendLog("    Source: Plugin Record Override");
                                }

                                // Outfit inclusion is independent of the patching mode, so resolve it once
                                // here for both the Create and Create-and-Patch branches.
                                if (_settings.NpcOutfitOverrides.TryGetValue(npcFormKey,
                                        out var outfitOverrideChoice))
                                {
                                    includeOutfit = outfitOverrideChoice switch
                                    {
                                        OutfitOverride.No => false,
                                        OutfitOverride.Yes => true,
                                        OutfitOverride.UseModSetting => appearanceModSetting.IncludeOutfits,
                                        _ => appearanceModSetting.IncludeOutfits,
                                    };
                                }
                                else
                                {
                                    includeOutfit = appearanceModSetting.IncludeOutfits;
                                }

                                // Include Outfit writes DefaultOutfit, which the engine ignores whenever
                                // the Inventory template flag is set — the NPC takes its whole inventory,
                                // outfit included, from its template. Report it here, independent of the
                                // wig branch below (whose own use of this predicate only fires when wig
                                // handling is active). The write itself is left in place: it is harmless,
                                // and correct again if the flag is cleared by other means.
                                if (includeOutfit && RecordOutfitIsInert(winningNpcOverride, appearanceNpcRecord))
                                {
                                    var inertTemplate = (_settings.PatchingMode == PatchingMode.CreateAndPatch
                                        ? winningNpcOverride
                                        : appearanceNpcRecord).Template.FormKey.ToString();
                                    _inertOutfitNpcs.Add((npcIdentifier, selectedModDisplayName, inertTemplate));
                                    AppendLog($"      Include Outfit: {npcIdentifier} takes its inventory from " +
                                              $"template {inertTemplate}, so the outfit written to its record is " +
                                              "never worn in game.", false, true);
                                }

                                // Where override discovery may START. Computed from the ORIGINAL donor record,
                                // before CopyAppearanceData redirects its links at merged-in output records.
                                //
                                // Every mode uses the same per-mod field selection now (Override Roots dialog,
                                // NpcRootFieldCatalog). It used to be SkyPatcher-only: the record modes rooted at
                                // the NPC's entire EnumerateFormLinks(), so AI packages were roots, and from a
                                // package the walk reached placed references, cells, quests and other NPCs —
                                // anything genuinely overridden down there dragged its whole ancestry in as
                                // private duplicates. A measured run repointed six NPCs' package links at copies
                                // of vanilla packages referencing copies of DB01 and SolitudeOpening.
                                //
                                // The default set is appearance-only, but it is the USER'S list, not a fixed one:
                                // no allowlist can be proven complete (appearance hides in oblique places, and
                                // the previous hardcoded one had already needed three ad-hoc additions), so a mod
                                // that genuinely needs another field can have it ticked back on.
                                //
                                // DefaultOutfit is a default root and is NOT gated on the user's includeOutfit
                                // choice: an appearance mod that edits an outfit-reachable record in place — RS
                                // Children's ChildClothes01 (0006D92C), part of Dorthe's outfit — must still have
                                // that override carried in. includeOutfit governs whether the outfit is DELIVERED
                                // (the SkyPatcher SetOutfit directive / the written record), never discovery.
                                var discoveryRootFields = NpcRootFieldCatalog.Resolve(appearanceModSetting, _settings);
                                List<IFormLinkGetter> discoveryRootLinks =
                                    NpcRootFieldCatalog.GetRootLinks(appearanceNpcRecord, discoveryRootFields);

                                // Null unless the user opted into own-copy template handling AND the
                                // donor's chain resolved — see ResolveAppearanceTerminusRecord.
                                //
                                // Resolved HERE, before the wig pass, because the converter and the
                                // forwarder both read Traits-governed appearance (race, sex, weight,
                                // hair colour, WornArmor, head parts) and under a flatten every one of
                                // those comes from the terminus — the same record CopyInheritedAppearance
                                // overlays further down. Reading the donor instead left the converter's
                                // hair removal pointed at head parts the flatten had already replaced
                                // (it matched nothing, so the terminus's hair survived alongside the
                                // minted wig) and pointed the forwarder's skin duplicate at the donor's
                                // WornArmor instead of the terminus's. Both switch branches below reuse
                                // this one local so the record flatten and the wig pass cannot disagree.
                                var flattenTerminus = ResolveAppearanceTerminusRecord(faceGenDecision,
                                    appearanceModSetting, currentModFolderPaths, isFaceGenOnly);

                                // Wig/antler forwarding (see WigHandlingMode). Runs BEFORE the
                                // appearance copy / dependency merge-in: ForwardToSkin seeds the
                                // donor WNAM → +Wig duplicate mapping so the merge redirects to
                                // the duplicate and never pulls in the original. The NPC record
                                // links are pointed at the duplicates AFTER CopyAppearanceData
                                // (whose non-merge path would otherwise reset them). Inert unless
                                // the mod has detected wigs and the output mode activates it
                                // (GetEffectiveWigMode).
                                //
                                // ConvertToHeadParts routes the wig class through the converter
                                // instead (minted HDPT records + post-copy FaceGen bake); the
                                // forwarder still runs after it for antler handling and to STRIP
                                // the converted wig from any forwarded outfit. A converter
                                // decline (bald donor, unresolvable wig NIF, …) downgrades that
                                // NPC to the proven ForwardToSkin flow via wigModeOverride.
                                if (!isFaceGenOnly && appearanceModSetting != null &&
                                    _settings.WigOrAntlerHandlingActive(appearanceModSetting))
                                {
                                    WigHandlingMode? wigModeOverride = null;
                                    var effectiveWigMode = _settings.GetEffectiveWigMode(appearanceModSetting);

                                    // ForwardToOutfit writes the wig into the NPC's DefaultOutfit, which
                                    // the engine ignores whenever the Inventory template flag is set —
                                    // it takes the whole inventory, outfit included, from the template.
                                    // Head parts have no such flag (they ride the Traits data this app
                                    // already owns), so convert instead of forwarding into a dead field.
                                    bool outfitFieldInert = effectiveWigMode == WigHandlingMode.ForwardToOutfit &&
                                                            RecordOutfitIsInert(winningNpcOverride, appearanceNpcRecord);

                                    if (effectiveWigMode == WigHandlingMode.ConvertToHeadParts || outfitFieldInert)
                                    {
                                        if (outfitFieldInert)
                                        {
                                            AppendLog($"      Wig handling: {npcIdentifier} inherits its inventory from " +
                                                      "a template, so a forwarded outfit could never reach it — " +
                                                      "converting the wig to head parts instead.", false, false);
                                        }

                                        wigConvert = _headPartWigConverter.Apply(appearanceNpcRecord,
                                            appearanceModSetting, currentModFolderPaths, npcIdentifier,
                                            AppendLog, out bool fallBackToForwardToSkin,
                                            faceGenSubjectFormKey: FlattenedFaceGenSubject(faceGenDecision),
                                            flattenTerminusNpc: flattenTerminus);
                                        if (wigConvert != null)
                                        {
                                            RegisterRecordOwnerships(npcFormKey, wigConvert.MintedRecords,
                                                npcContributions);

                                            // The forwarder must now STRIP the wig from any forwarded
                                            // outfit rather than add it — the head parts carry it.
                                            if (outfitFieldInert) wigModeOverride = WigHandlingMode.ConvertToHeadParts;
                                        }
                                        // Any decline on the inert-outfit path goes to the skin, which IS
                                        // live: WornArmor is Traits data, not inventory. Declines on the
                                        // ordinary path keep their own contract (only the risky ones
                                        // downgrade; "nothing to convert" leaves the mode alone).
                                        else if (fallBackToForwardToSkin || outfitFieldInert)
                                        {
                                            wigModeOverride = WigHandlingMode.ForwardToSkin;
                                        }
                                    }

                                    // The converter's superseded skin-carried wig ARMAs are
                                    // stripped from the WNAM duplicate by the forwarder (sole
                                    // duplicate owner). A ForwardToSkin downgrade passes null —
                                    // nothing was converted.
                                    wigForward = _wigForwarder.Apply(npcFormKey, appearanceNpcRecord,
                                        appearanceModSetting, appearanceModKey.Value, currentModFolderPaths,
                                        mergeInDependencyRecords, includeOutfit, npcIdentifier, AppendLog,
                                        wigModeOverride,
                                        wnamConvertedWigStrips: wigConvert?.WnamArmatureKeysToStrip,
                                        flattenTerminusNpc: flattenTerminus);
                                    if (wigForward != null)
                                    {
                                        RegisterRecordOwnerships(npcFormKey, wigForward.MergedRecords,
                                            npcContributions);
                                        _aux.CollectShallowAssetLinks(wigForward.MergedRecords, assetLinks);

                                        // A record-level DefaultOutfit loses to SkyPatcher/SPID at
                                        // runtime, so a forwarded outfit whose slot is contested has
                                        // to be republished through those same distributors.
                                        if (wigForward.OutfitDuplicateKey is { } forwardedOutfitKey)
                                        {
                                            _forwardedOutfitDistributor.Publish(npcFormKey, forwardedOutfitKey,
                                                wigForward.OutfitContest, npcIdentifier);
                                        }
                                    }
                                }

                                switch (_settings.PatchingMode)
                                {
                                    case PatchingMode.CreateAndPatch:
                                        AppendLog(
                                            $"      Mode: Create and Patch. Patching winning override ({winningNpcOverride.FormKey.ModKey.FileName}) with appearance from {appearanceModKey?.FileName ?? "N/A"}.");

                                        if (_settings.UseSkyPatcherMode)
                                        {
                                            // SkyPatcher applies the appearance at runtime; nothing overrides the
                                            // recipient NPC record in-game, so the surrogate template must be the
                                            // DONOR appearance record (not the recipient). Building it from
                                            // winningNpcOverride would drag the recipient's packages/items/factions
                                            // into the output and master it to every non-appearance data plugin.
                                            // Terminus supplied so an inherited appearance is flattened
                                            // into the surrogate — see CreateSkyPatcherNpc. CopyAppearanceData
                                            // below re-copies donor fields, so it re-applies the same overlay.
                                            //
                                            // appearanceOnly: the DeepCopyIn also hands the surrogate the
                                            // donor's factions/packages/items/outfit, and the merge-in walker
                                            // below follows every link on it — so without the strip, an
                                            // appearance plugin's non-appearance records get duplicated into
                                            // the output. Record mode merges none of them: its target is an
                                            // override of the WINNING record, whose non-appearance links are
                                            // the recipient's own.
                                            patchNpc = _skyPatcherInterface.CreateSkyPatcherNpc(npcFormKey,
                                                appearanceNpcRecord, flattenTerminus,
                                                appearanceOnly: true, includeOutfit: includeOutfit);
                                        }
                                        else
                                        {
                                            try
                                            {
                                                patchNpc =
                                                    _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(
                                                        winningNpcOverride);
                                            }
                                            catch (Exception ex)
                                            {
                                                AppendLog(
                                                    $"      ERROR: Failed to add {npcIdentifier} as override to the output mod. Skipping this NPC. Details: {ex.Message}",
                                                    isError: true,
                                                    forceLog: true);
                                                return;
                                            }
                                        }

                                        var mergedInAppearanceRecords = CopyAppearanceData(appearanceNpcRecord,
                                            patchNpc,
                                            appearanceModSetting, appearanceModKey.Value,
                                            currentModFolderPaths, npcIdentifier,
                                            mergeInDependencyRecords, includeOutfit, mergeEligiblePlugins,
                                            flattenTerminus);
                                        RegisterRecordOwnerships(npcFormKey, mergedInAppearanceRecords, npcContributions);
                                        _aux.CollectShallowAssetLinks(mergedInAppearanceRecords, assetLinks);

                                        // After CopyAppearanceData: its non-merge path resets WNAM
                                        // to the donor original and its outfit branch may replace
                                        // DefaultOutfit; this re-points them at the +Wig duplicates
                                        // and removes hair head parts superseded by a forwarded wig.
                                        if (wigForward != null)
                                        {
                                            _wigForwarder.FinalizeNpcRecord(wigForward, patchNpc,
                                                npcIdentifier, AppendLog);
                                        }

                                        // ConvertToHeadParts: replace the copied hair head-part
                                        // links with the minted wig parent (no bald back-fill —
                                        // the parent IS the Hair part).
                                        if (wigConvert != null)
                                        {
                                            _headPartWigConverter.FinalizeNpcRecord(wigConvert, patchNpc,
                                                npcIdentifier, AppendLog);
                                        }

                                        if (mergeInDependencyRecords)
                                        {
                                            List<string> mergeInExceptions = new();
                                            var mergedInRecords = _recordHandler.DuplicateFromOnlyReferencedGetters(
                                                _environmentStateProvider.OutputMod, patchNpc,
                                                mergeEligiblePlugins, appearanceModKey.Value, true,
                                                appearanceModSetting.HandleInjectedRecords,
                                                currentModFolderPaths,
                                                ref mergeInExceptions);
                                            RegisterRecordOwnerships(npcFormKey, mergedInRecords, npcContributions);
                                            if (mergeInExceptions.Any())
                                            {
                                                AppendLog("Exceptions occurred during dependency merge-in of " +
                                                          Auxilliary.GetLogString(patchNpc,
                                                              _settings.LocalizationLanguage) + Environment.NewLine +
                                                          string.Join(Environment.NewLine, mergeInExceptions));
                                            }

                                            _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                        }

                                        // Links are final now: the appearance copy, the wig
                                        // finalizers and the merge-in walker have all run.
                                        WarnOnDanglingAppearanceLinks(patchNpc, appearanceNpcRecord,
                                            appearanceModSetting, npcIdentifier, includeOutfit,
                                            mergeInDependencyRecords);

                                        switch (recordOverrideHandlingMode)
                                        {
                                            case RecordOverrideHandlingMode.Ignore:
                                                break;

                                            case RecordOverrideHandlingMode.Include:
                                            {
                                                AppendLog($"Searching for Overrides for {npcIdentifier}", false, true);

                                                HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord,
                                                    IMajorRecordGetter>> dependencyContexts;

                                                if (includeAllOverrides)
                                                {
                                                    AppendLog(
                                                        $"  Using 'Include All' mode - collecting all overrides from plugins",
                                                        false, true);
                                                    dependencyContexts = await Task.Run(() =>
                                                        _recordHandler.GetAllOverriddenDependencyRecords(
                                                            appearanceModSetting.CorrespondingModKeys,
                                                            searchedOverrideFormKeysForGroup,
                                                            currentModFolderPaths,
                                                            ct));
                                                }
                                                else
                                                {
                                                    // Roots come from the mod's Override Roots selection in every
                                                    // mode now; the record paths used to walk the NPC's whole link
                                                    // set, which is how AI packages became discovery roots.
                                                    dependencyContexts = await Task.Run(() =>
                                                        _recordHandler.DeepGetOverriddenDependencyRecords(
                                                            discoveryRootLinks,
                                                            appearanceModSetting.CorrespondingModKeys,
                                                            searchedOverrideFormKeysForGroup,
                                                            currentModFolderPaths,
                                                            maxNestedIntervalDepth,
                                                            ct));
                                                }

                                                List<MajorRecord> deltaPatchedRecords = new();
                                                foreach (var ctx in dependencyContexts)
                                                {
                                                    bool wasDeltaPatched = false;
                                                    if (_recordHandler.TryGetRecordFromMod(ctx.Record.FormKey,
                                                            ctx.Record.Type,
                                                            ctx.Record.FormKey.ModKey,
                                                            currentModFolderPaths,
                                                            RecordHandler.RecordLookupFallBack.None,
                                                            out var baseRecord) && baseRecord != null)
                                                    {
                                                        if (!_recordHandler.TryGetRecordFromMod(ctx.Record.FormKey,
                                                                ctx.Record.Type, ctx.ModKey,
                                                                currentModFolderPaths,
                                                                RecordHandler.RecordLookupFallBack.None,
                                                                out var overrideRecord) && baseRecord != null)
                                                        {
                                                            continue;
                                                        }

                                                        List<RecordDeltaPatcher.PropertyDiff> recordDifs =
                                                            _recordDeltaPatcher.GetPropertyDiffs(overrideRecord,
                                                                baseRecord, overrideRecord, ctx.ModKey);

                                                        if (recordDifs is not null && recordDifs.Any())
                                                        {
                                                            IMajorRecordGetter? winningGetter = null;
                                                            var loquiType = Auxilliary.GetRecordGetterType(ctx.Record);

                                                            if (
                                                                (loquiType != null && _environmentStateProvider
                                                                     .LinkCache.TryResolve(
                                                                         ctx.Record.FormKey,
                                                                         ctx.Record.Type, out winningGetter)

                                                                 ||

                                                                 _environmentStateProvider.LinkCache
                                                                     .TryResolve( // fallback because the typed lookup fails for IRaceGetter
                                                                         ctx.Record.FormKey, out winningGetter)
                                                                ) &&
                                                                winningGetter != null)
                                                            {
                                                                if (Auxilliary.TryGetOrAddGenericRecordAsOverride(
                                                                        winningGetter,
                                                                        _environmentStateProvider.OutputMod,
                                                                        out var winningRecord,
                                                                        out string exceptionString) &&
                                                                    winningRecord != null)
                                                                {
                                                                    _recordDeltaPatcher.ApplyPropertyDiffs(
                                                                        winningRecord,
                                                                        recordDifs, winningRecord, ctx.ModKey);
                                                                    deltaPatchedRecords.Add(winningRecord);
                                                                    RegisterRecordOwnership(npcFormKey, winningRecord, npcContributions);
                                                                    RecordProvenanceDiag.RecordOverrideWritten(
                                                                        ctx.Record.FormKey, ctx.Record.EditorID,
                                                                        ctx.Record.Registration.Name,
                                                                        deltaPatched: true,
                                                                        includeAllOverrides
                                                                            ? "discovered by all-overrides plugin scan"
                                                                            : null);
                                                                }
                                                                else
                                                                {
                                                                    AppendLog(
                                                                        Auxilliary.GetLogString(patchNpc,
                                                                            _settings.LocalizationLanguage) +
                                                                        ": Could not merge in winning override for " +
                                                                        Auxilliary.GetLogString(winningGetter,
                                                                            _settings.LocalizationLanguage) + ": " +
                                                                        exceptionString, true, true);
                                                                }
                                                            }
                                                        }
                                                    }

                                                    if (!wasDeltaPatched)
                                                    {
                                                        try
                                                        {
                                                            ctx.GetOrAddAsOverride(_environmentStateProvider.OutputMod);
                                                            RegisterRecordOwnership(npcFormKey, ctx.Record, npcContributions);
                                                            RecordProvenanceDiag.RecordOverrideWritten(
                                                                ctx.Record.FormKey, ctx.Record.EditorID,
                                                                ctx.Record.Registration.Name,
                                                                deltaPatched: false,
                                                                includeAllOverrides
                                                                    ? "discovered by all-overrides plugin scan"
                                                                    : null);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            AppendLog(
                                                                $"      ERROR: Failed to apply dependency override {ctx.Record.FormKey} for {npcIdentifier}. Removing this NPC from the output mod and skipping. Details: {ex.Message}",
                                                                isError: true,
                                                                forceLog: true);
                                                            if (!_settings.UseSkyPatcherMode)
                                                            {
                                                                _environmentStateProvider.OutputMod.Npcs.Remove(patchNpc.FormKey);
                                                            }
                                                            int removed = RollbackNpcContributions(npcFormKey, npcContributions);
                                                            if (removed > 0)
                                                            {
                                                                AppendLog(
                                                                    $"        Removed {removed} orphaned dependency record(s) that were only referenced by this NPC.",
                                                                    false, true);
                                                            }
                                                            return;
                                                        }
                                                    }
                                                }

                                                if (mergeInDependencyRecords)
                                                {
                                                    List<string> mergeInExceptions = new();
                                                    var importSourceModKeys = mergeEligiblePlugins
                                                        .Where(k => k != patchNpc.FormKey.ModKey)
                                                        .ToHashSet();
                                                    var additionalMergedRecords =
                                                        _recordHandler.DuplicateFromOnlyReferencedGetters(
                                                            _environmentStateProvider.OutputMod, deltaPatchedRecords,
                                                            importSourceModKeys, appearanceModKey.Value, true,
                                                            appearanceModSetting.HandleInjectedRecords,
                                                            currentModFolderPaths,
                                                            ref mergeInExceptions);
                                                    if (mergeInExceptions.Any())
                                                    {
                                                        AppendLog("Exceptions occurred during dependency merge-in of " +
                                                                  Auxilliary.GetLogString(patchNpc,
                                                                      _settings.LocalizationLanguage) +
                                                                  Environment.NewLine +
                                                                  string.Join(Environment.NewLine, mergeInExceptions));
                                                    }

                                                    _aux.CollectShallowAssetLinks(additionalMergedRecords, assetLinks);
                                                }

                                                _aux.CollectShallowAssetLinks(dependencyContexts, assetLinks);
                                                break;
                                            }

                                            case RecordOverrideHandlingMode.IncludeAsNew:
                                            {
                                                List<string> overrideExceptionStrings = new();
                                                HashSet<IMajorRecord> mergedInRecords;

                                                // Roots beyond the donor's own links: the chain hanging from
                                                // the outfit the actor will ACTUALLY wear. Discovery walks
                                                // the donor, but with Include Outfits off the worn outfit is
                                                // the recipient's — when the two differ, the recipient's
                                                // chain would never be duplicated and the mod's outfit-side
                                                // edits (e.g. RS Children's ArmorAddon fixes) silently miss
                                                // the NPC. Sleep outfits are never taken from the donor at
                                                // all, so the recipient's is a root whenever this mode runs.
                                                // Roots are keyed exactly as the remap/delivery looks them up
                                                // (patched record's field in record mode, resolver-effective
                                                // outfit for the SkyPatcher directive), so mint and delivery
                                                // cannot disagree. Create record mode forwards the donor
                                                // record wholesale — recipient roots would only mint
                                                // unreferenced duplicates there.
                                                List<IFormLinkGetter>? additionalRootLinks = null;
                                                HashSet<FormKey>? excludedDonorRootKeys = null;
                                                if (_settings.UseSkyPatcherMode ||
                                                    _settings.PatchingMode == PatchingMode.CreateAndPatch)
                                                {
                                                    additionalRootLinks = new();

                                                    // The inverse of the extra roots below: donor links
                                                    // the written record will NOT carry must not root
                                                    // discovery either — a chain reachable only through
                                                    // them is minted and then referenced by nothing (RS
                                                    // Children's donor-only 0RCOClothesO* outfits were
                                                    // the measured case). Sleep outfits are never taken
                                                    // from the donor; the donor's default outfit ships
                                                    // only when Include Outfits is on. Create record
                                                    // mode is exempt (the donor record ships wholesale,
                                                    // so its outfit links ARE delivered).
                                                    excludedDonorRootKeys = new();
                                                    if (!appearanceNpcRecord.SleepingOutfit.IsNull)
                                                    {
                                                        excludedDonorRootKeys.Add(
                                                            appearanceNpcRecord.SleepingOutfit.FormKey);
                                                    }
                                                    if (!includeOutfit && !appearanceNpcRecord.DefaultOutfit.IsNull)
                                                    {
                                                        excludedDonorRootKeys.Add(
                                                            appearanceNpcRecord.DefaultOutfit.FormKey);
                                                    }

                                                    // Each substitution follows its own Override Roots checkbox: it
                                                    // exists to redirect a root the donor would have supplied, so
                                                    // switching that root off has to switch the substitute off too.
                                                    if (discoveryRootFields.Contains(NpcRootField.SleepingOutfit) &&
                                                        !winningNpcOverride.SleepingOutfit.IsNull)
                                                    {
                                                        additionalRootLinks.Add(winningNpcOverride.SleepingOutfit);
                                                    }

                                                    if (!includeOutfit &&
                                                        discoveryRootFields.Contains(NpcRootField.DefaultOutfit))
                                                    {
                                                        if (!winningNpcOverride.DefaultOutfit.IsNull)
                                                        {
                                                            additionalRootLinks.Add(winningNpcOverride.DefaultOutfit);
                                                        }

                                                        if (_settings.UseSkyPatcherMode &&
                                                            ResolveRecipientEffectiveOutfit(npcFormKey,
                                                                appearanceNpcRecord.FormKey,
                                                                appearanceModSetting) is { } effectiveOutfit &&
                                                            effectiveOutfit != winningNpcOverride.DefaultOutfit.FormKey)
                                                        {
                                                            additionalRootLinks.Add(
                                                                new FormLink<IOutfitGetter>(effectiveOutfit));
                                                        }
                                                    }
                                                }

                                                if (includeAllOverrides)
                                                {
                                                    AppendLog(
                                                        $"  Using 'Include All' mode - duplicating all overrides from plugins as new records",
                                                        false, true);
                                                    mergedInRecords = _recordHandler.DuplicateAllOverrideRecordsAsNew(
                                                        patchNpc,
                                                        appearanceModSetting.CorrespondingModKeys,
                                                        appearanceModKey.Value, patchNpc.FormKey.ModKey,
                                                        appearanceModSetting.HandleInjectedRecords,
                                                        currentModFolderPaths,
                                                        ref overrideExceptionStrings,
                                                        searchedOverrideFormKeysForGroup,
                                                        ct);

                                                    // Bulk import copies overrides but does not bridge
                                                    // unoverridden parents (e.g. a vanilla Outfit above an
                                                    // overridden Armor), so the outfit roots still need the
                                                    // traversal to build a deliverable chain.
                                                    if (additionalRootLinks is { Count: > 0 })
                                                    {
                                                        mergedInRecords.UnionWith(
                                                            _recordHandler.DuplicateInOverrideRecordsFromLinks(
                                                                additionalRootLinks, patchNpc,
                                                                appearanceModSetting.CorrespondingModKeys,
                                                                appearanceModKey.Value, patchNpc.FormKey.ModKey,
                                                                appearanceModSetting.HandleInjectedRecords,
                                                                maxNestedIntervalDepth,
                                                                currentModFolderPaths,
                                                                ref overrideExceptionStrings,
                                                                searchedOverrideFormKeysForGroup,
                                                                ct));
                                                    }
                                                }
                                                else
                                                {
                                                    // Explicit roots: the mod's Override Roots selection, minus the
                                                    // donor links the written record will not carry, plus the
                                                    // recipient substitutes. Was DuplicateInOverrideRecords, which
                                                    // derived its roots from the donor's whole EnumerateFormLinks().
                                                    var overrideRoots = discoveryRootLinks
                                                        .Where(l => excludedDonorRootKeys?.Contains(l.FormKey) != true)
                                                        .Concat(additionalRootLinks ?? Enumerable.Empty<IFormLinkGetter>())
                                                        .ToList();

                                                    mergedInRecords = _recordHandler.DuplicateInOverrideRecordsFromLinks(
                                                        overrideRoots, patchNpc,
                                                        appearanceModSetting.CorrespondingModKeys,
                                                        appearanceModKey.Value, patchNpc.FormKey.ModKey,
                                                        appearanceModSetting.HandleInjectedRecords,
                                                        maxNestedIntervalDepth,
                                                        currentModFolderPaths,
                                                        ref overrideExceptionStrings,
                                                        searchedOverrideFormKeysForGroup,
                                                        ct);
                                                }

                                                if (overrideExceptionStrings.Any())
                                                {
                                                    AppendLog(
                                                        string.Join(Environment.NewLine, overrideExceptionStrings),
                                                        true,
                                                        true);
                                                }

                                                // Asset-side isolation: re-points every mod-shipped
                                                // asset on the duplicates at a private destination
                                                // and schedules those copies. Must precede the
                                                // harvest below so it sees the rewritten paths.
                                                _assetHandler.ScheduleIncludeAsNewAssetIsolation(
                                                    mergedInRecords, appearanceModSetting,
                                                    _currentRunOutputAssetPath, npcFormKey,
                                                    appearanceNpcRecord, npcIdentifier);

                                                _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                                break;
                                            }
                                        }

                                        break;

                                    default:
                                        AppendLog(
                                            $"      Mode: Create. Forwarding record from source plugin ({appearanceModKey?.FileName ?? "N/A"}).");

                                        if (_settings.UseSkyPatcherMode)
                                        {
                                            // Terminus supplied so an inherited appearance is flattened
                                            // into the surrogate — see CreateSkyPatcherNpc. No
                                            // CopyAppearanceData runs in this branch, so the surrogate's
                                            // overlay is not disturbed afterwards.
                                            patchNpc = _skyPatcherInterface.CreateSkyPatcherNpc(npcFormKey,
                                                appearanceNpcRecord, flattenTerminus);
                                        }
                                        else
                                        {
                                            try
                                            {
                                                patchNpc =
                                                    _environmentStateProvider.OutputMod.Npcs
                                                        .GetOrAddAsOverride(appearanceNpcRecord);
                                            }
                                            catch (Exception ex)
                                            {
                                                AppendLog(
                                                    $"      ERROR: Failed to add {npcIdentifier} as override to the output mod. Skipping this NPC. Details: {ex.Message}",
                                                    isError: true,
                                                    forceLog: true);
                                                return;
                                            }

                                            // Same flatten as CopyAppearanceData performs in the
                                            // Create-and-Patch branch (which never runs here): the
                                            // forwarded record carries the donor's inheritance, so
                                            // overlay the terminus's appearance and clear Traits.
                                            // The TPLT link stays — it also drives non-appearance
                                            // inheritance this app does not touch. Runs before the
                                            // merge-in walker below, which remaps any overlaid link
                                            // that points into a merge-eligible plugin.
                                            if (flattenTerminus != null)
                                            {
                                                Auxilliary.CopyInheritedAppearance(patchNpc, flattenTerminus);
                                                patchNpc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Traits;
                                                AppendLog($"      {npcIdentifier} inherits its appearance from {flattenTerminus.FormKey}; " +
                                                          $"copied that appearance onto its own record so its selection applies to it individually.");
                                            }
                                        }

                                        // Wig forwarding in this branch only activates in SkyPatcher
                                        // mode (GetEffectiveWigMode is None for plain Create). Point
                                        // the surrogate at the +Wig duplicates and drop superseded
                                        // hair head parts BEFORE the merge-in walker, so neither the
                                        // original WNAM nor the removed hair is ever traversed.
                                        if (wigForward != null)
                                        {
                                            _wigForwarder.FinalizeNpcRecord(wigForward, patchNpc,
                                                npcIdentifier, AppendLog);
                                        }

                                        // ConvertToHeadParts on the SkyPatcher surrogate: swap the
                                        // donor hair links for the minted wig parent BEFORE the
                                        // merge walker, same as the forwarder's hair removal above.
                                        if (wigConvert != null)
                                        {
                                            _headPartWigConverter.FinalizeNpcRecord(wigConvert, patchNpc,
                                                npcIdentifier, AppendLog);
                                        }

                                        if (mergeInDependencyRecords)
                                        {
                                            List<string> mergeInExceptions = new();
                                            var mergedInRecords = _recordHandler.DuplicateFromOnlyReferencedGetters(
                                                _environmentStateProvider.OutputMod, patchNpc,
                                                mergeEligiblePlugins, appearanceModKey.Value, true,
                                                appearanceModSetting.HandleInjectedRecords,
                                                currentModFolderPaths,
                                                ref mergeInExceptions);
                                            RegisterRecordOwnerships(npcFormKey, mergedInRecords, npcContributions);
                                            if (mergeInExceptions.Any())
                                            {
                                                AppendLog("Exceptions occurred during dependency merge-in of " +
                                                          Auxilliary.GetLogString(patchNpc,
                                                              _settings.LocalizationLanguage) + Environment.NewLine +
                                                          string.Join(Environment.NewLine, mergeInExceptions));
                                            }

                                            _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                        }

                                        // Links are final now: the appearance copy, the wig
                                        // finalizers and the merge-in walker have all run.
                                        WarnOnDanglingAppearanceLinks(patchNpc, appearanceNpcRecord,
                                            appearanceModSetting, npcIdentifier, includeOutfit,
                                            mergeInDependencyRecords);

                                        switch (recordOverrideHandlingMode)
                                        {
                                            case RecordOverrideHandlingMode.Ignore:
                                                break;

                                            case RecordOverrideHandlingMode.Include:
                                            {
                                                AppendLog($"Searching for Overrides for {npcIdentifier}", false, true);

                                                HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord,
                                                    IMajorRecordGetter>> dependencyContexts;

                                                if (includeAllOverrides)
                                                {
                                                    AppendLog(
                                                        $"  Using 'Include All' mode - collecting all overrides from plugins",
                                                        false, true);
                                                    dependencyContexts = await Task.Run(() =>
                                                        _recordHandler.GetAllOverriddenDependencyRecords(
                                                            appearanceModSetting.CorrespondingModKeys,
                                                            searchedOverrideFormKeysForGroup,
                                                            currentModFolderPaths,
                                                            ct));
                                                }
                                                else
                                                {
                                                    // Roots come from the mod's Override Roots selection in every
                                                    // mode now; the record paths used to walk the NPC's whole link
                                                    // set, which is how AI packages became discovery roots.
                                                    dependencyContexts = await Task.Run(() =>
                                                        _recordHandler.DeepGetOverriddenDependencyRecords(
                                                            discoveryRootLinks,
                                                            appearanceModSetting.CorrespondingModKeys,
                                                            searchedOverrideFormKeysForGroup,
                                                            currentModFolderPaths,
                                                            maxNestedIntervalDepth,
                                                            ct));
                                                }

                                                foreach (var ctx in dependencyContexts)
                                                {
                                                    try
                                                    {
                                                        ctx.GetOrAddAsOverride(_environmentStateProvider.OutputMod);
                                                        RegisterRecordOwnership(npcFormKey, ctx.Record, npcContributions);
                                                        RecordProvenanceDiag.RecordOverrideWritten(
                                                            ctx.Record.FormKey, ctx.Record.EditorID,
                                                            ctx.Record.Registration.Name,
                                                            deltaPatched: false,
                                                            includeAllOverrides
                                                                ? "discovered by all-overrides plugin scan"
                                                                : null);
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        AppendLog(
                                                            $"      ERROR: Failed to apply dependency override {ctx.Record.FormKey} for {npcIdentifier}. Removing this NPC from the output mod and skipping. Details: {ex.Message}",
                                                            isError: true,
                                                            forceLog: true);
                                                        if (!_settings.UseSkyPatcherMode)
                                                        {
                                                            _environmentStateProvider.OutputMod.Npcs.Remove(patchNpc.FormKey);
                                                        }
                                                        int removed = RollbackNpcContributions(npcFormKey, npcContributions);
                                                        if (removed > 0)
                                                        {
                                                            AppendLog(
                                                                $"        Removed {removed} orphaned dependency record(s) that were only referenced by this NPC.",
                                                                false, true);
                                                        }
                                                        return;
                                                    }
                                                }

                                                _aux.CollectShallowAssetLinks(dependencyContexts, assetLinks);

                                                if (mergeInDependencyRecords)
                                                {
                                                    List<string> mergeInExceptions = new();
                                                    var importSourceModKeys = mergeEligiblePlugins
                                                        .Where(k => k != patchNpc.FormKey.ModKey)
                                                        .ToHashSet();
                                                    var additionalMergedRecords =
                                                        _recordHandler.DuplicateFromOnlyReferencedGetters(
                                                            _environmentStateProvider.OutputMod,
                                                            dependencyContexts.Select(x => x.Record).ToHashSet(),
                                                            importSourceModKeys, appearanceModKey.Value, true,
                                                            appearanceModSetting.HandleInjectedRecords,
                                                            currentModFolderPaths,
                                                            ref mergeInExceptions);
                                                    if (mergeInExceptions.Any())
                                                    {
                                                        AppendLog("Exceptions occurred during dependency merge-in of " +
                                                                  Auxilliary.GetLogString(patchNpc,
                                                                      _settings.LocalizationLanguage) +
                                                                  Environment.NewLine +
                                                                  string.Join(Environment.NewLine, mergeInExceptions));
                                                    }

                                                    _aux.CollectShallowAssetLinks(additionalMergedRecords, assetLinks);
                                                }

                                                break;
                                            }

                                            case RecordOverrideHandlingMode.IncludeAsNew:
                                            {
                                                List<string> overrideExceptionStrings = new();
                                                HashSet<IMajorRecord> mergedInRecords;

                                                // Roots beyond the donor's own links: the chain hanging from
                                                // the outfit the actor will ACTUALLY wear. Discovery walks
                                                // the donor, but with Include Outfits off the worn outfit is
                                                // the recipient's — when the two differ, the recipient's
                                                // chain would never be duplicated and the mod's outfit-side
                                                // edits (e.g. RS Children's ArmorAddon fixes) silently miss
                                                // the NPC. Sleep outfits are never taken from the donor at
                                                // all, so the recipient's is a root whenever this mode runs.
                                                // Roots are keyed exactly as the remap/delivery looks them up
                                                // (patched record's field in record mode, resolver-effective
                                                // outfit for the SkyPatcher directive), so mint and delivery
                                                // cannot disagree. Create record mode forwards the donor
                                                // record wholesale — recipient roots would only mint
                                                // unreferenced duplicates there.
                                                List<IFormLinkGetter>? additionalRootLinks = null;
                                                HashSet<FormKey>? excludedDonorRootKeys = null;
                                                if (_settings.UseSkyPatcherMode ||
                                                    _settings.PatchingMode == PatchingMode.CreateAndPatch)
                                                {
                                                    additionalRootLinks = new();

                                                    // The inverse of the extra roots below: donor links
                                                    // the written record will NOT carry must not root
                                                    // discovery either — a chain reachable only through
                                                    // them is minted and then referenced by nothing (RS
                                                    // Children's donor-only 0RCOClothesO* outfits were
                                                    // the measured case). Sleep outfits are never taken
                                                    // from the donor; the donor's default outfit ships
                                                    // only when Include Outfits is on. Create record
                                                    // mode is exempt (the donor record ships wholesale,
                                                    // so its outfit links ARE delivered).
                                                    excludedDonorRootKeys = new();
                                                    if (!appearanceNpcRecord.SleepingOutfit.IsNull)
                                                    {
                                                        excludedDonorRootKeys.Add(
                                                            appearanceNpcRecord.SleepingOutfit.FormKey);
                                                    }
                                                    if (!includeOutfit && !appearanceNpcRecord.DefaultOutfit.IsNull)
                                                    {
                                                        excludedDonorRootKeys.Add(
                                                            appearanceNpcRecord.DefaultOutfit.FormKey);
                                                    }

                                                    // Each substitution follows its own Override Roots checkbox: it
                                                    // exists to redirect a root the donor would have supplied, so
                                                    // switching that root off has to switch the substitute off too.
                                                    if (discoveryRootFields.Contains(NpcRootField.SleepingOutfit) &&
                                                        !winningNpcOverride.SleepingOutfit.IsNull)
                                                    {
                                                        additionalRootLinks.Add(winningNpcOverride.SleepingOutfit);
                                                    }

                                                    if (!includeOutfit &&
                                                        discoveryRootFields.Contains(NpcRootField.DefaultOutfit))
                                                    {
                                                        if (!winningNpcOverride.DefaultOutfit.IsNull)
                                                        {
                                                            additionalRootLinks.Add(winningNpcOverride.DefaultOutfit);
                                                        }

                                                        if (_settings.UseSkyPatcherMode &&
                                                            ResolveRecipientEffectiveOutfit(npcFormKey,
                                                                appearanceNpcRecord.FormKey,
                                                                appearanceModSetting) is { } effectiveOutfit &&
                                                            effectiveOutfit != winningNpcOverride.DefaultOutfit.FormKey)
                                                        {
                                                            additionalRootLinks.Add(
                                                                new FormLink<IOutfitGetter>(effectiveOutfit));
                                                        }
                                                    }
                                                }

                                                if (includeAllOverrides)
                                                {
                                                    AppendLog(
                                                        $"  Using 'Include All' mode - duplicating all overrides from plugins as new records",
                                                        false, true);
                                                    mergedInRecords = _recordHandler.DuplicateAllOverrideRecordsAsNew(
                                                        patchNpc,
                                                        appearanceModSetting.CorrespondingModKeys,
                                                        appearanceModKey.Value, patchNpc.FormKey.ModKey,
                                                        appearanceModSetting.HandleInjectedRecords,
                                                        currentModFolderPaths,
                                                        ref overrideExceptionStrings,
                                                        searchedOverrideFormKeysForGroup,
                                                        ct);

                                                    // Bulk import copies overrides but does not bridge
                                                    // unoverridden parents (e.g. a vanilla Outfit above an
                                                    // overridden Armor), so the outfit roots still need the
                                                    // traversal to build a deliverable chain.
                                                    if (additionalRootLinks is { Count: > 0 })
                                                    {
                                                        mergedInRecords.UnionWith(
                                                            _recordHandler.DuplicateInOverrideRecordsFromLinks(
                                                                additionalRootLinks, patchNpc,
                                                                appearanceModSetting.CorrespondingModKeys,
                                                                appearanceModKey.Value, patchNpc.FormKey.ModKey,
                                                                appearanceModSetting.HandleInjectedRecords,
                                                                maxNestedIntervalDepth,
                                                                currentModFolderPaths,
                                                                ref overrideExceptionStrings,
                                                                searchedOverrideFormKeysForGroup,
                                                                ct));
                                                    }
                                                }
                                                else
                                                {
                                                    // Explicit roots: the mod's Override Roots selection, minus the
                                                    // donor links the written record will not carry, plus the
                                                    // recipient substitutes. Was DuplicateInOverrideRecords, which
                                                    // derived its roots from the donor's whole EnumerateFormLinks().
                                                    var overrideRoots = discoveryRootLinks
                                                        .Where(l => excludedDonorRootKeys?.Contains(l.FormKey) != true)
                                                        .Concat(additionalRootLinks ?? Enumerable.Empty<IFormLinkGetter>())
                                                        .ToList();

                                                    mergedInRecords = _recordHandler.DuplicateInOverrideRecordsFromLinks(
                                                        overrideRoots, patchNpc,
                                                        appearanceModSetting.CorrespondingModKeys,
                                                        appearanceModKey.Value, patchNpc.FormKey.ModKey,
                                                        appearanceModSetting.HandleInjectedRecords,
                                                        maxNestedIntervalDepth,
                                                        currentModFolderPaths,
                                                        ref overrideExceptionStrings,
                                                        searchedOverrideFormKeysForGroup,
                                                        ct);
                                                }

                                                if (overrideExceptionStrings.Any())
                                                {
                                                    AppendLog(
                                                        string.Join(Environment.NewLine, overrideExceptionStrings),
                                                        true,
                                                        true);
                                                }

                                                // Asset-side isolation: re-points every mod-shipped
                                                // asset on the duplicates at a private destination
                                                // and schedules those copies. Must precede the
                                                // harvest below so it sees the rewritten paths.
                                                _assetHandler.ScheduleIncludeAsNewAssetIsolation(
                                                    mergedInRecords, appearanceModSetting,
                                                    _currentRunOutputAssetPath, npcFormKey,
                                                    appearanceNpcRecord, npcIdentifier);

                                                _aux.CollectShallowAssetLinks(mergedInRecords, assetLinks);
                                                break;
                                            }
                                        }

                                        break;
                                }

                                ApplyKeywords(patchNpc, appearanceModSetting.Keywords);

                            }
                            else
                            {
                                AppendLog(
                                    $"ERROR: UNEXPECTED: Selection for {npcIdentifier} was marked valid but has no plugin record. Skipping.",
                                    true);
                            }

                            if (patchNpc != null && appearanceModSetting != null && faceGenDecision != null)
                            {
                                await _assetHandler.ScheduleCopyNpcAssets(npcFormKey, appearanceNpcRecord,
                                    appearanceModSetting, // appearanceNpcRecord here rather than patchNpc is intentional
                                    _currentRunOutputAssetPath, npcIdentifier, faceGenDecision);

                                // Queue the baked hair/antler shape strip for this NPC's copied
                                // FaceGen NIF (wig ForwardToSkin removes hair; antler Remove removes
                                // baked antler head-part shapes). The copy destination is keyed by
                                // the OUTPUT record's FormKey in every mode (surrogate in SkyPatcher
                                // mode, the patch target otherwise); applied after
                                // MonitorAndWaitForAllTasks below, once the file exists.
                                if (wigForward is { FaceGenShapeNamesToStrip.Count: > 0 })
                                {
                                    var (wigFaceGenNifRelPath, _) =
                                        Auxilliary.GetFaceGenSubPathStrings(patchNpc.FormKey, true);
                                    _pendingWigNifEdits.Add((
                                        Path.Combine(_currentRunOutputAssetPath, wigFaceGenNifRelPath),
                                        wigForward.FaceGenShapeNamesToStrip, npcIdentifier));
                                }

                                var (_, faceTintPath) = Auxilliary.GetFaceGenSubPathStrings(appearanceNpcRecord.FormKey, true);

                                // ConvertToHeadParts: schedule the wig NIF (HDPT Model target,
                                // whose copy also pulls its textures + original physics XML) and
                                // the rewritten physics XML, then queue the post-copy bake into
                                // this NPC's copied FaceGen. The bake strips the donor hair
                                // shapes itself (the strip list rides in wigConvert), so nothing
                                // is queued through _pendingWigNifEdits for the hair.
                                if (wigConvert != null)
                                {
                                    _assetHandler.ScheduleWigConversionAssets(wigConvert, appearanceModSetting,
                                        _currentRunOutputAssetPath, faceTintPath, npcFormKey,
                                        appearanceNpcRecord, npcIdentifier);

                                    var (bakeFaceGenRelPath, _) =
                                        Auxilliary.GetFaceGenSubPathStrings(patchNpc.FormKey, true);
                                    _pendingWigBakes.Add((
                                        Path.Combine(_currentRunOutputAssetPath, bakeFaceGenRelPath),
                                        wigConvert, npcIdentifier));
                                }

                                // Head parts this run duplicated under a new EditorID need their
                                // baked FaceGen shapes renamed to match, or the engine's by-name
                                // pairing breaks and the NPC dark-faces.
                                var headPartRenames = CollectHeadPartShapeRenames(patchNpc);
                                if (headPartRenames.Count > 0)
                                {
                                    var (renameFaceGenRelPath, _) =
                                        Auxilliary.GetFaceGenSubPathStrings(patchNpc.FormKey, true);
                                    _pendingHeadPartRenames.Add((
                                        Path.Combine(_currentRunOutputAssetPath, renameFaceGenRelPath),
                                        headPartRenames, npcIdentifier));
                                }

                                await _assetHandler.ScheduleCopyAssetLinkFiles(assetLinks, appearanceModSetting,
                                    _currentRunOutputAssetPath, faceTintPath,
                                    appearanceNpcRecord, npcFormKey, npcIdentifier);


                                if (_settings.UseSkyPatcherMode)
                                {
                                    // Directives are emitted HERE — after the override switch — so
                                    // link-valued directives (race=, outfitDefault=) carry post-remap
                                    // FormKeys. Emitting them before the switch orphaned every
                                    // Include-As-New duplicate the remap was supposed to deliver, and
                                    // dropped race= for the first NPC of each batch (Kayd bug; see
                                    // docs/SkyPatcher-IncludeAsNew-Outfit-Records.md §4.3/§4.5).
                                    // The wig OR: a wig forwarded to the outfit must emit
                                    // outfitDefault= even when the user's Include Outfit choice is
                                    // off — the duplicate outfit is how the wig reaches the NPC.
                                    ApplySkyPatcherDirectives(npcFormKey, winningNpcOverride, patchNpc,
                                        includeOutfit || (wigForward?.OutfitForwarded ?? false));

                                    if (recordOverrideHandlingMode == RecordOverrideHandlingMode.IncludeAsNew)
                                    {
                                        DeliverIncludeAsNewOutfitDirectives(npcFormKey, winningNpcOverride,
                                            appearanceNpcRecord.FormKey, appearanceModSetting,
                                            includeOutfit || (wigForward?.OutfitForwarded ?? false));
                                    }

                                    _skyPatcherInterface.ApplyCoreAppearance(npcFormKey, patchNpc);
                                }
                            }
                            else
                            {
                                AppendLog(
                                    $"ERROR: Could not proceed with asset copying due to missing patch record or mod setting for {npcIdentifier}.",
                                    true);
                            }

                            if (appearanceModKey.HasValue)
                            {
                                processedNpcsTokenData[npcFormKey] = new NpcAppearanceData
                                {
                                    ModName = selectedModDisplayName,
                                    AppearancePlugin = appearanceModKey.Value,
                                    OutputPlugin = _environmentStateProvider.OutputMod.ModKey
                                };
                            }
                        });

                        processedCount++;
                    }

                    if (_settings.LogPerformance)
                    {
                        var perfReport = ContextualPerformanceTracer.GenerateReportForGroup(npcGroup.Key, false);
                        AppendLog(perfReport, false, true);
                    }

                    if (loadedPluginPaths.Any())
                    {
                        AppendLog($"--- Unloading resources for batch: {npcGroup.Key} ---", false, true);
                        _pluginProvider.UnloadPlugins(loadedPluginPaths);
                        _recordHandler.ClearLinkCachesFor(modKeysForBatch);
                    }
                }

                UpdateProgress(totalToProcess, totalToProcess, "Copying Files...");

                if (processedCount > 0)
                {
                    AppendLog($"\nProcessed {processedCount} NPC(s).", false, true);

                    AppendLog("Waiting for all background asset copying and extraction to finish...", false, true);

                    await _assetHandler.MonitorAndWaitForAllTasks(logMessage =>
                        AppendLog("  " + logMessage, false, false));

                    // Verify any cached file access errors to see if they were actual failures.
                    _assetHandler.LogTrueCopyFailures();

                    // FaceGen copies whose baked tint path the asset handler re-pointed (appearance
                    // share, SkyPatcher surrogates, flattened Traits templates) are no longer
                    // byte-identical to the selected mod's file; record them like the wig/rename
                    // edits below so the output validator treats the delivery as ours (see
                    // NoteFaceGenEdited).
                    foreach (var rel in _assetHandler.RewrittenFaceGenNifPaths)
                    {
                        _editedFaceGenPaths[rel.ToLowerInvariant()] = 0;
                    }

                    // CPU-bound NIF edits over potentially thousands of files (one per
                    // wig-wearing NPC); without Task.Run they resume on the UI thread's
                    // sync context after the await above and freeze the window.
                    await Task.Run(() =>
                    {
                        ApplyPendingWigNifEdits();

                        ApplyPendingWigBakes();

                        // Last: the two phases above own specific shape names (stripped donor
                        // hair, baked wig shapes), so renaming runs against the final shape set.
                        ApplyPendingHeadPartRenames();
                    }, ct);

                    // Any record minted under a new FormKey that nothing references is dead cargo:
                    // copies are removed, anything this run authored is reported (neutral note, not
                    // a warning) — see PruneAndLogOrphanedDuplicates. Must run after every NPC (and
                    // every rollback) has finished touching the output; BEFORE the record-provenance
                    // flush below, so the CSV describes the plugin that actually gets written; and
                    // before the save, whose ESL compaction should not have to find FormIDs for
                    // records nothing uses.
                    PruneAndLogOrphanedDuplicates();

                    // Opt-in asset-provenance report (AssetProvenance.csv): why each file was copied
                    // and which NPCs/mods/records pulled it in. No-op unless enabled (Settings
                    // checkbox or the LogAssetProvenance.txt dev trigger).
                    AssetProvenanceDiag.Flush();

                    // Opt-in record-provenance report (RecordProvenance.csv): every non-NPC record
                    // in the output plugin with the reference chain that pulled it in. No-op unless
                    // enabled (Settings checkbox or the LogRecordProvenance.txt dev trigger).
                    RecordProvenanceDiag.Flush();

                    // Opt-in FaceGen-ladder report (FaceGenLadder.csv): which ladder row each NPC
                    // hit and where each half of its face came from. No-op unless enabled (the
                    // LogFaceGenLadder.txt dev trigger; PatchVerify harness runs flush a second
                    // copy into their own report folder).
                    FaceGenLadderDiag.Flush();

                    ReportFaceGenSkippedNpcs();
                    ReportInheritedFaceNpcs();
                    ReportFlattenedFallbackNpcs();
                    ReportInertOutfitNpcs();

                    // Race-drift findings were held back during processing because their remedy
                    // (which Record Override Handling Mode to recommend) depends on the whole
                    // run's selections; convert them to warnings now, before the reporter flushes.
                    FlushRaceDriftFindings();

                    // Per-NPC warnings (suspect origin meshes, missing tints, textureless
                    // shapes), grouped by type with one explanation per group — see
                    // NpcWarningReporter. Textureless entries accumulate from background NIF
                    // post-processing, so the flush must stay after the task drain above.
                    NpcWarningReporter.Flush((msg, isError, force) => AppendLog(msg, isError, force));

                    AppendLog("All file operations finished.", false, true);

                    string outputPluginPath = Path.Combine(_currentRunOutputAssetPath,
                        _environmentStateProvider.OutputMod.ModKey.FileName);
                    UpdateProgress(totalToProcess, totalToProcess, "Saving output plugin...");
                    AppendLog($"Attempting to save output mod to: {outputPluginPath}", false);
                    try
                    {
                        if (_settings.AutoEslIfy)
                        {
                            ModCompaction.CompactToWithFallback(_environmentStateProvider.OutputMod, MasterStyle.Small);
                        }

                        _environmentStateProvider.OutputMod.ModHeader.Description = PluginDescriptionSignature;

                        // WithAutoSplit() first attempts a normal single-file write and only splits
                        // into <name>.esp/<name>_2.esp/... if the output would exceed Skyrim's
                        // 255-master limit, so the common case is unchanged. Skipped when the user
                        // disables the setting (in which case an overflow throws as before).
                        if (_settings.AutoSplitOutput)
                        {
                            await _environmentStateProvider.OutputMod.BeginWrite
                                .ToPath(outputPluginPath)
                                .WithLoadOrder(_environmentStateProvider.LoadOrder)
                                .WithAutoSplit()
                                .WriteAsync();
                        }
                        else
                        {
                            await _environmentStateProvider.OutputMod.BeginWrite
                                .ToPath(outputPluginPath)
                                .WithLoadOrder(_environmentStateProvider.LoadOrder)
                                .WriteAsync();
                        }

                        _environmentStateProvider.CurrentAllocator?.Commit();
                        _environmentStateProvider.CurrentAllocator?.Dispose();
                        
                        AppendLog($"Saved plugin: {outputPluginPath}.", false, true);

                        // Accumulate token data for this batch into the class-level dictionary
                        // instead of writing the JSON file here
                        foreach (var kvp in processedNpcsTokenData)
                        {
                            _accumulatedTokenData[kvp.Key] = kvp.Value;
                        }
                        AppendLog($"Accumulated {processedNpcsTokenData.Count} NPC token entries for unified output.", false, true);
                    }
                    catch (Exception ex)
                    {
                        AppendLog(
                            $"FATAL SAVE ERROR: Could not write output plugin: {ExceptionLogger.GetExceptionStack(ex)}",
                            true);

                        // The most common save failure is a dangling master: a record was
                        // deep-copied ("merged in") as an NPC's dependency but still points at
                        // a plugin that isn't in the active load order. The raw exception only
                        // names the output FormKey, which tells the user nothing actionable.
                        // Enrich it with the source record it came from and the NPC(s) that
                        // pulled it in, using the provenance maps built during patching.
                        try
                        {
                            string diag = BuildDanglingMasterDiagnostics();
                            if (!string.IsNullOrEmpty(diag))
                            {
                                AppendLog(diag, true, true);
                            }
                        }
                        catch (Exception diagEx)
                        {
                            AppendLog($"(Could not build extended save-error diagnostics: {diagEx.Message})", false, true);
                        }

                        ResetProgress();
                        return;
                    }

                    // Runtime-distributor configs. In SkyPatcher mode the ini carries the
                    // appearance directives; in record mode it is written only when the
                    // wig/antler pass had to republish a forwarded outfit past a contesting
                    // SkyPatcher config (see ForwardedOutfitDistributor), and the generated
                    // SPID ini likewise only when a SPID entry contested one.
                    bool writeSkyPatcherIni = _settings.UseSkyPatcherMode || _skyPatcherInterface.HasEntries;
                    if (writeSkyPatcherIni || _forwardedOutfitDistributor.HasSpidEntries)
                    {
                        // If auto-split relocated output records into "<name>_2.esp"/etc., the
                        // configs' in-memory FormKeys (all "<name>.esp|ID") are stale — surrogate
                        // templates for the SkyPatcher ini, outfit duplicates for both. Build a map
                        // to their true post-split files so the writers can rewrite them.
                        var outputFormKeyRemap = _settings.AutoSplitOutput
                            ? BuildSplitFormKeyRemap(outputPluginPath)
                            : null;
                        if (writeSkyPatcherIni)
                        {
                            _skyPatcherInterface.WriteIni(_currentRunOutputAssetPath, outputFormKeyRemap);
                        }

                        _forwardedOutfitDistributor.WriteSpidConfig(_currentRunOutputAssetPath,
                            _environmentStateProvider.OutputMod.ModKey, outputFormKeyRemap);
                    }
                }
                else
                {
                    AppendLog("\nNo NPC appearances processed or dependencies duplicated.", false, true);
                    AppendLog("Output mod not saved as no changes were made.", false, true);
                }
            }
        }
        finally
        {
            // Release only the reader refs THIS run took (see openedBsaPaths
            // declaration). Readers other consumers still hold stay alive.
            _bsaHandler.ReleaseReaders(openedBsaPaths);
            _recordDeltaPatcher.FinalizeLog();
            UpdateProgress(selectionsToProcess.Count, selectionsToProcess.Count, "Finished.");
        }

        if (showFinalMessage)
        {
            AppendLog("\nPatch generation process completed.", false, true);
        }

        UpdateProgress(selectionsToProcess.Count, selectionsToProcess.Count, "Finished.");
    }

    /// <summary>
    /// After an auto-split write, the surrogate template records the SkyPatcher .ini points at may
    /// have moved from "&lt;name&gt;.esp" into "&lt;name&gt;_2.esp"/etc. (their local FormID is preserved,
    /// only the plugin changes). Reads the written split files back and returns a map from each
    /// original output-plugin FormKey to its true post-split FormKey, or <c>null</c> when the output
    /// was not split (the common case, where the .ini needs no remapping).
    /// </summary>
    private IReadOnlyDictionary<FormKey, FormKey>? BuildSplitFormKeyRemap(string outputPluginPath)
    {
        var outputModKey = _environmentStateProvider.OutputMod.ModKey;

        List<FilePath> splitFiles;
        try
        {
            splitFiles = MultiModFileAnalysis.GetSplitModFiles(new ModPath(outputModKey, outputPluginPath));
        }
        catch (Exception ex)
        {
            // GetSplitModFiles throws on an inconsistent on-disk state; fall back to no remap.
            AppendLog($"Could not enumerate split output files for SkyPatcher remap: {ex.Message}", false, true);
            return null;
        }

        if (splitFiles.Count <= 1)
        {
            return null; // Not split - the in-memory FormKeys are already correct.
        }

        var remap = new Dictionary<FormKey, FormKey>();
        foreach (var fp in splitFiles)
        {
            string filePath = fp;
            var fileModKey = ModKey.FromFileName(Path.GetFileName(filePath));

            // The base file keeps the original ModKey, so its new records still resolve as
            // "<name>.esp|ID" - no remap needed for those.
            if (fileModKey.Equals(outputModKey)) continue;

            try
            {
                using var mod = SkyrimMod.CreateFromBinaryOverlay(filePath, _environmentStateProvider.SkyrimVersion);
                foreach (var rec in mod.EnumerateMajorRecords())
                {
                    // Only records mastered to this split file were created in the output plugin
                    // (surrogates + any injected records). Overrides keep their donor ModKey and
                    // must be left alone.
                    if (!rec.FormKey.ModKey.Equals(fileModKey)) continue;
                    remap[new FormKey(outputModKey, rec.FormKey.ID)] = rec.FormKey;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Could not read split output file '{filePath}' for SkyPatcher remap: {ex.Message}", false, true);
            }
        }

        if (remap.Count > 0)
        {
            AppendLog($"Auto-split relocated {remap.Count} output record(s); remapped SkyPatcher .ini references across {splitFiles.Count} files.", false, true);
            return remap;
        }
        return null;
    }

    /// <summary>
    /// Writes the unified NPC token JSON file containing all processed NPCs across all patching cycles.
    /// This should be called after all patching batches are complete.
    /// </summary>
    public void WriteUnifiedTokenFile()
    {
        if (string.IsNullOrWhiteSpace(_currentRunOutputAssetPath))
        {
            AppendLog("ERROR: Output path not set. Cannot write unified token file.", true);
            return;
        }

        // Always finalize the marker, even when no NPCs were processed: a bootstrap marker with an
        // empty payload was already written up front, and overwriting it keeps the on-disk token
        // consistent with the completed run. The self-output guard keys on existence, not contents.
        AppendLog($"Writing unified NPC token file with {_accumulatedTokenData.Count} entries...", false, true);

        var tokenFilePath = Path.Combine(_currentRunOutputAssetPath, "NPC_Token.json");
        if (WriteTokenFileToDisk(out var exceptionStr))
        {
            AppendLog($"Successfully wrote unified NPC_Token.json to {tokenFilePath}", false, true);
        }
        else
        {
            AppendLog($"NPC_Token.json not saved:" + Environment.NewLine + exceptionStr, true, true);
        }
    }

    /// <summary>
    /// Serializes NPC_Token.json into the current output directory from whatever token data has
    /// accumulated so far. Shared by the early bootstrap-marker write (before any plugin/asset is
    /// saved) and the final <see cref="WriteUnifiedTokenFile"/> enrichment. Returns false (with the
    /// serializer's message) if the output path is unset or the write fails, so callers can log.
    /// </summary>
    private bool WriteTokenFileToDisk(out string exceptionStr)
    {
        exceptionStr = string.Empty;
        if (string.IsNullOrWhiteSpace(_currentRunOutputAssetPath))
        {
            exceptionStr = "Output path not set.";
            return false;
        }

        var tokenFilePath = Path.Combine(_currentRunOutputAssetPath, "NPC_Token.json");
        var tokenData = new NpcToken
        {
            CreationDate = DateTime.Now.ToString("o"),
            CreatedPlugins = _generatedOutputPlugins,
            PatchingMode = _settings.PatchingMode.ToString(),
            UseSkyPatcherMode = _settings.UseSkyPatcherMode,
            ProcessedNpcs = _accumulatedTokenData,
            SkippedNpcs = _skippedTokenData,
            EditedFaceGen = new HashSet<string>(_editedFaceGenPaths.Keys, StringComparer.OrdinalIgnoreCase)
        };

        JSONhandler<NpcToken>.SaveJSONFile(tokenData, tokenFilePath, out bool tokenSaved, out exceptionStr);
        return tokenSaved;
    }

    private void ClearDirectory(string path)
    {
        DirectoryInfo di = new DirectoryInfo(path);
        if (!di.Exists) return;

        foreach (FileInfo file in di.EnumerateFiles())
        {
            bool preserveFile = false;
            // Check if the file is a .txt file first, which is a fast operation.
            if (file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Efficiently check if any line in the file contains the command,
                    // without loading the entire file into memory.
                    if (File.ReadLines(file.FullName).Any(line =>
                            line.Contains("player.placeatme", StringComparison.OrdinalIgnoreCase)))
                    {
                        preserveFile = true;
                    }
                }
                catch (Exception ex)
                {
                    // If the file can't be read (e.g., permissions), err on the side of caution and preserve it.
                    AppendLog(
                        $"  Could not read file '{file.Name}' to check for preservation: {ex.Message}. Skipping deletion.",
                        isError: true);
                    preserveFile = true;
                }
            }

            if (preserveFile)
            {
                AppendLog($"  Preserving spawn command file: {file.Name}");
                continue; // Skip to the next file without deleting.
            }
            // --- End of new logic ---

            file.Delete();
        }

        foreach (DirectoryInfo dir in di.EnumerateDirectories())
        {
            string dirNameLower = dir.Name.ToLowerInvariant();
            if (dirNameLower == "meshes" || dirNameLower == "textures" || dirNameLower == "facegendata" ||
                dirNameLower == "actors")
            {
                dir.Delete(true);
            }
            else
            {
                AppendLog($"  Skipping deletion of non-asset directory: {dir.Name}");
            }
        }
    }

    /// <summary>
    /// Strips the baked hair/antler shape(s) from the copied FaceGen NIFs queued
    /// by the wig/antler-forwarding pass (see <see cref="WigForwarder"/>: the hair
    /// head part was removed for a forwarded wig, and/or an antler head part was
    /// removed by antler Remove; the baked FaceGen shape would otherwise still
    /// render in game). Runs once per patch run, after all asset copy/extraction
    /// tasks have finished so the destination files exist. Files are processed
    /// in parallel (each entry owns its NPC's FaceGen copy). Per-file failures
    /// are logged and skipped — a surviving baked shape clashes visually but
    /// breaks nothing.
    /// </summary>
    private void ApplyPendingWigNifEdits()
    {
        if (_pendingWigNifEdits.IsEmpty) return;

        AppendLog($"Stripping baked hair/antler shapes from {_pendingWigNifEdits.Count} FaceGen NIF(s) (wig/antler forwarding)...",
            false, false);
        var pendingEdits = _pendingWigNifEdits.ToList();
        int stripTotal = pendingEdits.Count;
        int stripDone = 0;

        void StripOne((string NifPath, HashSet<string> ShapeNames, string NpcIdentifier) item)
        {
            var (nifPath, shapeNames, npcIdentifier) = item;
            UpdateProgress(Interlocked.Increment(ref stripDone), stripTotal, "Stripping baked hair/antler shapes...");
            try
            {
                if (!File.Exists(nifPath))
                {
                    AppendLog($"  WARNING: {npcIdentifier}: FaceGen NIF not found for baked-hair strip: {nifPath}",
                        false, true);
                    return;
                }

                int removed = NifHandler.RemoveShapesByName(nifPath, shapeNames,
                    msg => AppendLog("    " + msg, false, false));
                if (removed > 0)
                {
                    NoteFaceGenEdited(nifPath);
                    AppendLog($"  {npcIdentifier}: removed {removed} baked hair/antler shape(s) " +
                              $"[{string.Join(", ", shapeNames)}] from {Path.GetFileName(nifPath)}.", false, false);
                }
                else
                {
                    AppendLog($"  WARNING: {npcIdentifier}: no shape named [{string.Join(", ", shapeNames)}] " +
                              $"found in {Path.GetFileName(nifPath)} — the baked hair/antler may still show in game.",
                        false, true);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"  ERROR stripping baked hair/antler for {npcIdentifier} ({nifPath}): {ex.Message}",
                    true, true);
            }
        }

        // Each entry edits a different NPC's FaceGen copy and nifly is safe for
        // concurrent loads on separate NifFile instances. The first file runs
        // alone to prime nifly's native singletons before fanning out.
        StripOne(pendingEdits[0]);
        Parallel.ForEach(pendingEdits.Skip(1),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, StripOne);
    }

    /// <summary>
    /// Notes that a copied FaceGen mesh was rewritten in place, keyed exactly as
    /// <see cref="Auxilliary.GetFaceGenSubPathStrings"/> renders it (regularized, lowercase) so
    /// the validator can look it up against the path it builds from the same helper.
    /// </summary>
    private void NoteFaceGenEdited(string absoluteNifPath)
    {
        var root = _currentRunOutputAssetPath;
        if (string.IsNullOrEmpty(root)) return;
        if (!absoluteNifPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;

        var relative = absoluteNifPath.Substring(root.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
        if (relative.Length > 0) _editedFaceGenPaths[relative] = 0;
    }

    /// <summary>
    /// Old shape name -> new shape name for every head part <paramref name="patchNpc"/> effectively
    /// wears — its own, their Extra Parts, and its race's defaults — that this run duplicated into
    /// the output under a CHANGED EditorID. Empty for the overwhelming majority of NPCs (nothing
    /// was duplicated, or the EditorID survived).
    ///
    /// <para>The engine pairs an NPC's head parts to its baked FaceGen geometry by name —
    /// <c>GetObjectByName(headPart-&gt;formEditorID)</c> — so a duplicate minted under
    /// <c>&lt;original&gt;_&lt;sourcePlugin&gt;</c> (what Include As New does, to keep one mod's
    /// copy of a shared record off every other mod's NPCs) points at a name no shape in the mesh
    /// carries. Confirmed in game on RS Children's Assur and Svari: dark faces.</para>
    ///
    /// <para>Derived from the merged-record provenance map rather than from the override-handling
    /// mode, so it covers every mint site that renames and cannot drift from them. Records the
    /// wig pipeline mints are absent from that map — it creates them itself and bakes shapes
    /// already carrying the new names — so they are naturally excluded.</para>
    /// </summary>
    private Dictionary<string, string> CollectHeadPartShapeRenames(INpcGetter patchNpc)
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var outputMod = _environmentStateProvider.OutputMod;
        var visited = new HashSet<FormKey>();

        // ExtraParts are walked because a hairline is an EXTRA PART of its hair head part, not a
        // top-level entry on the NPC — and it gets its own baked shape and its own duplicate. The
        // first cut of this only read the NPC's own list, which renamed Assur's hair and left his
        // hairline behind: still dark-faced, just for one part instead of two. Mirrors the walk
        // in FaceGenConsistencyAnalyzer, which is what grades the result.
        void Walk(IFormLinkGetter<IHeadPartGetter> link)
        {
            if (link.IsNull || !visited.Add(link.FormKey)) return;
            if (!link.FormKey.ModKey.Equals(outputMod.ModKey)) return; // not one of ours
            if (!outputMod.HeadParts.TryGetValue(link.FormKey, out var minted)) return;

            if (_recordHandler.TryGetMergedRecordOrigin(link.FormKey, out var origin))
            {
                var oldName = origin.SourceEditorId;
                var newName = minted.EditorID;
                if (!string.IsNullOrEmpty(oldName) && !string.IsNullOrEmpty(newName) &&
                    !string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    renames[oldName] = newName;
                }
            }

            // Descend regardless: a part we did not rename can still own ones we did.
            if (minted.ExtraParts == null) return;
            foreach (var extra in minted.ExtraParts) Walk(extra);
        }

        foreach (var hp in patchNpc.HeadParts) Walk(hp);

        // The race's defaults are walked too, because a slot the NPC leaves unset is filled from
        // its RACE and baked into the FaceGen exactly like one of its own parts. RS Children's
        // children are the specimen: Kayd, Alesan and Francois Beaufort carry only brows/eyes and
        // take their hair from the race, so when Include As New minted the race its defaults named
        // 'HairMaleRedguardChild01_RSkyrimChildren.esm' while their meshes still carried
        // 'HairMaleRedguardChild01'. All three dark-faced in game; the NPCs of the same mod that
        // own their hair outright were fine, which is exactly the shape of this gap.
        //
        // Only a race in the OUTPUT plugin can name renamed duplicates — RemapLinks rewrites only
        // records this run wrote, so an unminted race still points at the originals the mesh
        // already matches, and there is nothing to do.
        //
        // Deliberately NOT mirroring FaceGenConsistencyAnalyzer's skip of slots the NPC overrides:
        // that test needs every part's Type, and the NPC's own parts routinely live in appearance
        // plugins outside the load order, so it could only be answered for some of them. The
        // asymmetry settles it — a missed rename is a dark face, while a surplus one names a shape
        // the mesh does not carry and does nothing (and RenameShapesByName refuses to rename onto
        // a name already present, which is the one case that could do harm).
        if (!patchNpc.Race.IsNull &&
            outputMod.Races.TryGetValue(patchNpc.Race.FormKey, out var mintedRace) &&
            mintedRace is IRaceGetter race)
        {
            var headData = Auxilliary.IsFemale(patchNpc) ? race.HeadData?.Female : race.HeadData?.Male;
            if (headData?.HeadParts != null)
            {
                foreach (var hpRef in headData.HeadParts) Walk(hpRef.Head);
            }
        }

        return renames;
    }

    /// <summary>
    /// Renames the baked shapes in each queued NPC's copied FaceGen NIF to the EditorIDs of the
    /// head-part duplicates this run minted for it (see
    /// <see cref="CollectHeadPartShapeRenames"/>). Runs last of the three post-copy NIF phases —
    /// after the antler/hair strip and the wig bake — so it sees the final shape set and never
    /// competes with the names those phases own. Drains destructively for the same reason the
    /// bake does: RunPatchingLogic runs once per output plugin.
    /// </summary>
    private void ApplyPendingHeadPartRenames()
    {
        if (_pendingHeadPartRenames.IsEmpty) return;

        AppendLog($"Renaming FaceGen shapes to match duplicated head parts in " +
                  $"{_pendingHeadPartRenames.Count} NIF(s)...", false, false);

        var pending = new List<(string NifPath, Dictionary<string, string> Renames, string NpcIdentifier)>();
        while (_pendingHeadPartRenames.TryTake(out var item)) pending.Add(item);

        int total = pending.Count;
        int done = 0;

        void RenameOne((string NifPath, Dictionary<string, string> Renames, string NpcIdentifier) item)
        {
            UpdateProgress(Interlocked.Increment(ref done), total, "Renaming FaceGen shapes...");
            var (nifPath, renames, npcIdentifier) = item;
            try
            {
                if (!File.Exists(nifPath))
                {
                    // No FaceGen of our own for this NPC. The renamed head parts then apply to
                    // whatever mesh the load order supplies, which this run cannot edit.
                    AppendLog($"  WARNING: {npcIdentifier}: FaceGen NIF not found ({nifPath}), so the " +
                              $"duplicated head part(s) [{string.Join(", ", renames.Values)}] keep names no " +
                              "shape in its mesh carries. This NPC may show the dark face bug — set the mod's " +
                              "Record Override Handling Mode to Include instead of IncludeAsNew if it does.",
                        true, true);
                    return;
                }

                int renamed = NifHandler.RenameShapesByName(nifPath, renames,
                    msg => AppendLog("    " + msg, false, false));

                if (renamed > 0)
                {
                    NoteFaceGenEdited(nifPath);
                    AppendLog($"  {npcIdentifier}: renamed {renamed} FaceGen shape(s) in " +
                              $"{Path.GetFileName(nifPath)} to match duplicated head parts.", false, false);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"  ERROR renaming FaceGen shapes for {npcIdentifier} ({nifPath}): " +
                          ExceptionLogger.GetExceptionStack(ex), true, true);
            }
        }

        if (pending.Count == 0) return;
        RenameOne(pending[0]);
        Parallel.ForEach(pending.Skip(1),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, RenameOne);
    }

    /// <summary>
    /// Bakes the wig scene into each queued NPC's copied FaceGen NIF
    /// (ConvertToHeadParts wig handling; see <see cref="HeadPartWigConverter"/> /
    /// <see cref="NifHandler.BakeWigIntoFaceGen"/>). Runs after all asset
    /// copy/extraction tasks have finished so the destination FaceGen files
    /// exist, and after <see cref="ApplyPendingWigNifEdits"/> (an NPC can carry
    /// both: an antler strip there and the wig bake here — disjoint shape sets,
    /// but one load/save each keeps them independent; the two phases stay
    /// sequential relative to each other for that reason, while the files
    /// WITHIN each phase are processed in parallel). Drains destructively:
    /// RunPatchingLogic is invoked once per output plugin, and re-baking an
    /// already-baked file would duplicate the wig shapes. A failed bake is a
    /// dark-face risk (the minted records expect the baked shapes), so it is
    /// logged as an ERROR with the recovery options.
    /// </summary>
    private void ApplyPendingWigBakes()
    {
        if (_pendingWigBakes.IsEmpty) return;

        AppendLog($"Baking wig scenes into {_pendingWigBakes.Count} FaceGen NIF(s) (wig ConvertToHeadParts)...",
            false, false);

        // Drain destructively up front (see doc comment), then fan out below.
        var pendingBakes = new List<(string NifPath, HeadPartWigConverter.Result Convert, string NpcIdentifier)>();
        while (_pendingWigBakes.TryTake(out var pending)) pendingBakes.Add(pending);

        int bakeTotal = pendingBakes.Count;
        int bakeDone = 0;

        void BakeOne((string NifPath, HeadPartWigConverter.Result Convert, string NpcIdentifier) item)
        {
            UpdateProgress(Interlocked.Increment(ref bakeDone), bakeTotal, "Baking wig meshes into FaceGen...");
            var (nifPath, convert, npcIdentifier) = item;
            try
            {
                if (!File.Exists(nifPath))
                {
                    AppendLog($"  ERROR: {npcIdentifier}: FaceGen NIF not found for wig bake: {nifPath}. " +
                              $"The minted head parts ('{convert.ParentEditorId}' + extras) expect baked shapes — " +
                              "this NPC will dark-face in game. Ensure the appearance mod provides FaceGen for it, " +
                              "or switch the mod's Wig Handling Mode to ForwardToSkin.", true, true);
                    return;
                }

                int baked = NifHandler.BakeWigIntoFaceGen(new NifHandler.WigBakeInstruction(
                        nifPath,
                        convert.WigNifSourcePath,
                        convert.ShapeRenames,
                        convert.FaceGenShapeNamesToStrip,
                        convert.PhysicsXmlNewDataRelPath,
                        SynthesizeHairPartitionIfNoDonor: convert.SynthesizeHairPartitionTemplate,
                        HairTintMode: convert.HairTintMode,
                        HairTintRgb: convert.HairTintRgb),
                    msg => AppendLog("    " + msg, false, false));

                if (baked > 0)
                {
                    NoteFaceGenEdited(nifPath);
                    AppendLog($"  {npcIdentifier}: baked {baked} wig shape(s) from " +
                              $"{Path.GetFileName(convert.WigNifSourcePath)} into {Path.GetFileName(nifPath)} " +
                              (convert.FaceGenShapeNamesToStrip.Count > 0
                                  ? $"(donor hair [{string.Join(", ", convert.FaceGenShapeNamesToStrip)}] stripped)."
                                  : "(bald donor — synthesized hair partition)."),
                        false, false);
                }
                else
                {
                    AppendLog($"  ERROR: {npcIdentifier}: wig bake produced no shapes for {Path.GetFileName(nifPath)} " +
                              "(file left untouched). The minted head parts expect baked shapes — this NPC will " +
                              "dark-face in game. Re-run with the mod's Wig Handling Mode set to ForwardToSkin, " +
                              "or report this wig so the converter can be taught to handle it.", true, true);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"  ERROR baking wig for {npcIdentifier} ({nifPath}): {ExceptionLogger.GetExceptionStack(ex)}",
                    true, true);
            }
        }

        // Each bake reads the shared donor wig NIF and writes only its own NPC's
        // FaceGen copy, on NifFile instances local to the call — safe to fan out.
        // The first bake runs alone to prime nifly's native singletons.
        BakeOne(pendingBakes[0]);
        Parallel.ForEach(pendingBakes.Skip(1),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, BakeOne);
    }

    /// <param name="flattenTerminus">
    /// Non-null when the donor's Traits chain is being flattened (Template Handling Mode =
    /// give each NPC its own copy, chain resolved): the terminus's appearance is overlaid onto
    /// the output at the end of this method and the Traits flag is cleared, instead of the
    /// donor's inheritance being mirrored. In SkyPatcher mode the surrogate arrives here already
    /// flattened by <see cref="SkyPatcherInterface.CreateSkyPatcherNpc"/>, and the overlay must
    /// STILL run — the donor-field copying above it re-copies the donor's inert head data, and
    /// without the re-overlay <see cref="SyncTemplateInheritance"/> would also re-mirror the
    /// donor's Traits flag onto the surrogate, silently undoing the flatten.
    /// </param>
    private List<MajorRecord> CopyAppearanceData(INpcGetter sourceNpc, Npc targetNpc, ModSetting appearanceModSetting,
        ModKey sourceNpcContextModKey, HashSet<string> currentModFolderPaths, string npcIdentifier,
        bool mergeInDependencyRecords, bool includeOutfit, HashSet<ModKey> mergeEligiblePlugins,
        INpcGetter? flattenTerminus)
    {
        using var _ = ContextualPerformanceTracer.Trace("Patcher.CopyAppearanceData");

        // Protect the NPC being patched from being merged in as a new record. Its
        // winning override frequently lives in an appearance plugin that is in the
        // duplicate-from set, so without this an internal self-reference (or the
        // input NPC's own override) gets deep-copied and assigned a new FormKey.
        // Seeding an identity remap makes any link to it resolve to this output
        // override instead. (The NPC must never be duplicated.)
        _recordHandler.ProtectRecordFromDuplication(targetNpc.FormKey);

        targetNpc.FaceMorph = sourceNpc.FaceMorph?.DeepCopy();
        targetNpc.FaceParts = sourceNpc.FaceParts?.DeepCopy();
        targetNpc.Height = sourceNpc.Height;
        targetNpc.Weight = sourceNpc.Weight;
        targetNpc.TextureLighting = sourceNpc.TextureLighting;
        targetNpc.TintLayers.Clear();
        targetNpc.TintLayers.AddRange(sourceNpc.TintLayers?.Select(t => t.DeepCopy()) ??
                                      Enumerable.Empty<TintLayer>());

        // SkyPatcher .ini directives (gender/race/traits/outfit) are NOT emitted here. They are
        // emitted once, after dependency merge-in, by ApplySkyPatcherDirectives, which compares the
        // recipient (winningNpcOverride) against the donor surrogate. Emitting them here would be
        // wrong in SkyPatcher mode because targetNpc IS the donor surrogate, so the comparisons
        // below are donor-vs-donor and would never detect a delta.
        if (ShouldChangeGender(targetNpc, sourceNpc, out var genderToSet) && genderToSet != null)
        {
            SetGender(targetNpc, genderToSet.Value);
        }

        if (ShouldChangeRace(targetNpc, sourceNpc, out var raceToSet) && raceToSet != null)
        {
            targetNpc.Race.SetTo(raceToSet);
        }

        if (flattenTerminus == null)
        {
            SyncTemplateInheritance(targetNpc, sourceNpc);
        }
        // else: the terminus overlay at the end of this method replaces the donor's inheritance
        // outright — mirroring the donor's Traits state here would only be overwritten there.

        List<MajorRecord> mergedInRecords = new();

        // Only the merge-eligible plugins are copied from; a link into any other plugin falls
        // through RecordHandler's eligibility gate and is written as a plain reference, which is
        // correct precisely when that plugin stays in the load order. The source NPC's own
        // defining plugin is excluded as before so vanilla records are never duplicated.
        var importSourceModKeys = mergeEligiblePlugins
            .Where(k => k != sourceNpc.FormKey.ModKey)
            .ToHashSet();

        if (mergeInDependencyRecords)
        {
            try
            {
                List<string> skinExceptions = new();
                var skinRecords = _recordHandler.DuplicateInOrAddFormLink(targetNpc.WornArmor, sourceNpc.WornArmor,
                    _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, appearanceModSetting.HandleInjectedRecords,
                    currentModFolderPaths, ref skinExceptions);
                if (skinExceptions.Any())
                {
                    AppendLog(
                        "Exceptions during skin assignment: " + Environment.NewLine +
                        string.Join(Environment.NewLine, skinExceptions), true, true);
                }

                mergedInRecords.AddRange(skinRecords);

                List<string> headExceptions = new();
                var headRecords = _recordHandler.DuplicateInOrAddFormLink(targetNpc.HeadTexture, sourceNpc.HeadTexture,
                    _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, appearanceModSetting.HandleInjectedRecords,
                    currentModFolderPaths, ref headExceptions);
                if (headExceptions.Any())
                {
                    AppendLog(
                        "Exceptions during head texture assignment: " + Environment.NewLine +
                        string.Join(Environment.NewLine, headExceptions), true, true);
                }

                mergedInRecords.AddRange(headRecords);

                if (!targetNpc.Race.Equals(sourceNpc.Race))
                {
                    List<string> raceExceptions = new();
                    var raceRecords = _recordHandler.DuplicateInOrAddFormLink(targetNpc.Race, sourceNpc.Race,
                        _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, appearanceModSetting.HandleInjectedRecords,
                        currentModFolderPaths, ref raceExceptions);
                    if (raceExceptions.Any())
                    {
                        AppendLog(
                            "Exceptions during race assignment: " + Environment.NewLine +
                            string.Join(Environment.NewLine, raceExceptions), true, true);
                    }

                    mergedInRecords.AddRange(raceRecords);
                }

                List<string> colorExceptions = new();
                var hairColorRecords = _recordHandler.DuplicateInOrAddFormLink(targetNpc.HairColor, sourceNpc.HairColor,
                    _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, appearanceModSetting.HandleInjectedRecords,
                    currentModFolderPaths, ref colorExceptions);
                if (colorExceptions.Any())
                {
                    AppendLog(
                        "Exceptions during hair color assignment: " + Environment.NewLine +
                        string.Join(Environment.NewLine, skinExceptions), true, true);
                }

                mergedInRecords.AddRange(hairColorRecords);

                targetNpc.HeadParts.Clear();
                List<string> headPartExceptions = new();
                foreach (var hp in sourceNpc.HeadParts.Where(x => !x.IsNull))
                {
                    var targetHp = new FormLink<IHeadPartGetter>();
                    var headPartRecords = _recordHandler.DuplicateInOrAddFormLink(targetHp, hp,
                        _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, appearanceModSetting.HandleInjectedRecords,
                        currentModFolderPaths, ref headPartExceptions);
                    targetNpc.HeadParts.Add(targetHp);
                    mergedInRecords.AddRange(headPartRecords);
                }

                if (headPartExceptions.Any())
                {
                    AppendLog(
                        "Exceptions during head part assignment: " + Environment.NewLine +
                        string.Join(Environment.NewLine, headPartExceptions), true, true);
                }

                if (includeOutfit)
                {
                    List<string> outfitExceptions = new();
                    var outfitRecords = _recordHandler.DuplicateInOrAddFormLink(targetNpc.DefaultOutfit, sourceNpc.DefaultOutfit,
                        _environmentStateProvider.OutputMod, importSourceModKeys, sourceNpcContextModKey, appearanceModSetting.HandleInjectedRecords,
                        currentModFolderPaths, ref outfitExceptions);
                    if (outfitExceptions.Any())
                    {
                        AppendLog(
                            "Exceptions during outfit assignment: " + Environment.NewLine +
                            string.Join(Environment.NewLine, outfitExceptions), true, true);
                    }
                    mergedInRecords.AddRange(outfitRecords);
                }

                AppendLog($"    Completed dependency processing for {npcIdentifier}.");
            }
            catch (Exception ex)
            {
                AppendLog(
                    $"  ERROR duplicating dependencies for {npcIdentifier}: {ExceptionLogger.GetExceptionStack(ex)}",
                    true);
            }
        }
        else // set the formlinks to the original values
        {
            targetNpc.WornArmor.SetTo(sourceNpc.WornArmor);
            targetNpc.HeadTexture.SetTo(sourceNpc.HeadTexture);
            targetNpc.HairColor.SetTo(sourceNpc.HairColor);
            targetNpc.HeadParts.Clear();
            foreach (var hp in sourceNpc.HeadParts)
            {
                targetNpc.HeadParts.Add(hp);
            }

            if (includeOutfit)
            {
                targetNpc.DefaultOutfit.SetTo(sourceNpc.DefaultOutfit);
            }
        }

        AppendLog(
            $"      Copied appearance fields from {sourceNpc.FormKey.ModKey.FileName} to {targetNpc.FormKey} in patch.");

        if (Auxilliary.IsValidTemplatedNpc(targetNpc) && !Auxilliary.IsValidTemplatedNpc(sourceNpc))
        {
            AppendLog($"      Removing template flag from {targetNpc.FormKey} in patch.");
            targetNpc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Traits;
        }

        // Flatten: the donor's Traits-governed fields copied above are inert (the engine would
        // have rendered the terminus's face, not the donor's own head data), so overlay the
        // terminus's appearance and clear the flag. The TPLT link is deliberately kept — it also
        // drives non-appearance inheritance (inventory, AI packages, factions...) that this app
        // does not touch. When dependency merge-in is on, the caller's merge walker runs after
        // this method and remaps any overlaid link that points into a merge-eligible plugin.
        if (flattenTerminus != null)
        {
            Auxilliary.CopyInheritedAppearance(targetNpc, flattenTerminus);
            targetNpc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Traits;
            AppendLog($"      {npcIdentifier} inherits its appearance from {flattenTerminus.FormKey}; " +
                      $"copied that appearance onto its own record so its selection applies to it individually.");
        }

        // NOTE: the dangling-link check deliberately does NOT run here. The flatten overlay above
        // writes the terminus's links verbatim, and the caller's merge-in walker — which runs after
        // this method — is what remaps them into the output. Checking here saw that intermediate
        // state and reported a fatal condition for links that were about to be fixed. See
        // WarnOnDanglingAppearanceLinks, which the caller invokes once the links are final.

        // Concrete record of every appearance field applied to the NPC, for the
        // per-NPC diagnostic file (only built when this NPC is being logged).
        if (NpcDiagnosticLogger.IsActive)
        {
            // Flag every dumped FormKey whose plugin is absent from the load order. Without
            // this the dump reads as ordinary output even when it is the direct cause of the
            // end-of-run save failure. A flatten's overlaid links are still pre-merge at this
            // point, so say so rather than letting the marks read as defects.
            string Mark(FormKey fk) => fk.IsNull || _allowedMasterKeys.Contains(fk.ModKey)
                ? fk.ToString()
                : $"{fk} **PLUGIN NOT IN LOAD ORDER**";

            NpcDiagnosticLogger.Log($"  NPC record fields applied (source {sourceNpc.FormKey}, mergeInDependencies={mergeInDependencyRecords}" +
                                    (flattenTerminus != null ? "; flattened links not yet merged" : "") + "):");
            NpcDiagnosticLogger.Log($"    FaceMorph={(sourceNpc.FaceMorph != null ? "copied" : "null")}, FaceParts={(sourceNpc.FaceParts != null ? "copied" : "null")}, Height={sourceNpc.Height}, Weight={sourceNpc.Weight}, TintLayers={targetNpc.TintLayers.Count}");
            NpcDiagnosticLogger.Log($"    Race={Mark(targetNpc.Race.FormKey)}, WornArmor(skin)={Mark(targetNpc.WornArmor.FormKey)}, HeadTexture={Mark(targetNpc.HeadTexture.FormKey)}, HairColor={Mark(targetNpc.HairColor.FormKey)}");
            NpcDiagnosticLogger.Log($"    HeadParts=[{string.Join(", ", targetNpc.HeadParts.Select(h => Mark(h.FormKey)))}]");
            if (includeOutfit) NpcDiagnosticLogger.Log($"    DefaultOutfit={Mark(targetNpc.DefaultOutfit.FormKey)}");
        }

        return mergedInRecords;
    }

    /// <summary>
    /// A written link that points outside the load order is fatal: the run completes, then
    /// Mutagen refuses to write the plugin with a bare "referenced mod was not present" naming only
    /// an output FormKey — thousands of NPCs after the one that caused it. Reported here, where the
    /// NPC, the offending field, the source plugin and the mod are all still known, so the failure
    /// is attributable from the main log and the per-NPC log. In Create-and-Patch only the named
    /// appearance set is ours to dangle; both Create flavors ship the donor's whole record, so
    /// every link on it is checked there (extras labelled "record data").
    ///
    /// <para>MUST be called only once every step that can rewrite this NPC's appearance links has
    /// run — the appearance copy, the wig/antler finalizers, and above all the dependency merge-in
    /// walker, which is what turns a link into a plugin outside the load order into a merged output
    /// record. Called from inside the appearance copy it fired on links the walker was about to
    /// fix, reporting an unsaveable plugin that in fact saved correctly.</para>
    /// </summary>
    private void WarnOnDanglingAppearanceLinks(Npc targetNpc, INpcGetter sourceNpc,
        ModSetting? appearanceModSetting, string npcIdentifier, bool includeOutfit,
        bool mergeInDependencyRecords)
    {
        // Create-and-Patch writes only the named appearance set onto the winning override, so those
        // are the only links of ours that can dangle. Both Create flavors forward the donor's WHOLE
        // record (record mode wholesale, SkyPatcher surrogate un-stripped) — ANY link on it can fail
        // the save, and the outfit ships whether or not Include Outfits is on. Named entries first so
        // the familiar fields keep their labels; the remainder reports as "record data".
        bool wholeRecordShips = _settings.PatchingMode != PatchingMode.CreateAndPatch;
        var written = EnumerateNamedAppearanceLinks(targetNpc, includeOutfit || wholeRecordShips)
            .ToList();
        if (wholeRecordShips)
        {
            var named = written.Select(l => l.Key).ToHashSet();
            written.AddRange(targetNpc.EnumerateFormLinks()
                .Where(l => !l.FormKey.IsNull && !named.Contains(l.FormKey))
                .Select(l => ("record data", l.FormKey)));
        }

        var danglingAppearanceLinks = written
            .Where(l => !_allowedMasterKeys.Contains(l.Key.ModKey))
            .ToList();
        if (!danglingAppearanceLinks.Any()) return;

        var missingPlugins = danglingAppearanceLinks
            .Select(l => l.Key.ModKey)
            .Distinct()
            .Select(m => m.FileName.ToString())
            .ToList();

        string remedy = mergeInDependencyRecords
            ? "Dependency merge-in is ON for this mod, so the record could not be copied into the output " +
              "(it lives in a plugin this app cannot load) — see the merge-in trace above."
            : "Dependency merge-in is OFF for this mod, so the link was copied verbatim. Enabling " +
              "'Merge In Dependency Records' for this mod, enabling the missing plugin, or choosing a " +
              "different appearance for this NPC all resolve it.";

        AppendLog(
            $"      CRITICAL WARNING: {npcIdentifier}'s patched record from '{appearanceModSetting?.DisplayName ?? "N/A"}' " +
            $"(source record {sourceNpc.FormKey}) references plugin(s) that are NOT in your load order: " +
            $"{string.Join(", ", missingPlugins)}. THE OUTPUT PLUGIN CANNOT BE SAVED while this reference exists. " +
            $"Offending field(s): {string.Join("; ", danglingAppearanceLinks.Select(l => $"{l.Field}={l.Key}"))}. " +
            remedy,
            true, true);
    }

    // The written appearance set, carrying the record field each link came from so a bad reference
    // can be reported as "Race=..." / "HeadParts[2]=..." rather than as a bare FormKey.
    // Diagnostics only.
    //
    // Deliberately NOT the same question as NpcRootFieldCatalog, which says where dependent-
    // OVERRIDE discovery may start (user-configurable per mod). This one asks "what is
    // written to the record, and can the output legally reference it". TPLT is written — by
    // SyncTemplateInheritance in record mode and by the surrogate's DeepCopyIn in SkyPatcher mode —
    // and a TPLT into a plugin outside the load order fails the save exactly like a head part does.
    private static IEnumerable<(string Field, FormKey Key)> EnumerateNamedAppearanceLinks(INpcGetter npc,
        bool includeOutfit)
    {
        if (!npc.Race.IsNull) yield return ("Race", npc.Race.FormKey);
        if (!npc.WornArmor.IsNull) yield return ("WornArmor(skin)", npc.WornArmor.FormKey);
        if (!npc.HeadTexture.IsNull) yield return ("HeadTexture", npc.HeadTexture.FormKey);
        if (!npc.HairColor.IsNull) yield return ("HairColor", npc.HairColor.FormKey);

        int hpIndex = 0;
        foreach (var hp in npc.HeadParts)
        {
            if (!hp.IsNull) yield return ($"HeadParts[{hpIndex}]", hp.FormKey);
            hpIndex++;
        }

        if (includeOutfit && !npc.DefaultOutfit.IsNull) yield return ("DefaultOutfit", npc.DefaultOutfit.FormKey);
        if (npc.Template is { IsNull: false }) yield return ("Template", npc.Template.FormKey);
    }

    // Emits the SkyPatcher .ini directives for the appearance delta between the recipient
    // (winningNpcOverride) and the donor surrogate (patchNpc). Must be called AFTER dependency
    // merge-in so the referenced FormKeys are the merged-in ones, not the stale originals.
    // Shared by both the Create and Create-and-Patch branches.
    private void ApplySkyPatcherDirectives(FormKey npcFormKey, INpcGetter winningNpcOverride, Npc patchNpc,
        bool includeOutfit)
    {
        if (ShouldChangeGender(winningNpcOverride, patchNpc, out var genderToSet) && genderToSet != null)
        {
            _skyPatcherInterface.ToggleGender(npcFormKey, genderToSet.Value);
        }

        if (ShouldChangeRace(winningNpcOverride, patchNpc, out var raceToSet) && raceToSet != null)
        {
            _skyPatcherInterface.ApplyRace(npcFormKey, raceToSet.Value);
        }

        // Traits is handled ASYMMETRICALLY here, unlike record mode, because the directive lands on
        // the RECIPIENT (filterByNPCs=recipient) while the appearance arrives via the surrogate.
        //
        // Per SkyPatcher's source (github.com/Zzyxz/SkyPatcher npc_patcher.cpp @ main),
        // copyVisualStyle assigns `curobj->faceNPC = bo` — the recipient's face NPC becomes the
        // surrogate — plus height/weight/tintLayers/bodyTintColor/headRelatedData and the head
        // parts. It never walks a template chain and never writes a TPLT. The surrogate is a
        // DeepCopyIn of the donor (SkyPatcherInterface.CreateSkyPatcherNpc), so it carries the
        // donor's Traits flag AND its TPLT, and an inherited face resolves from there — the
        // SkyPatcher-mode equivalent of what SyncTemplateInheritance writes in record mode.
        //
        // CLEARING the bit is therefore real work: a recipient that inherits its own face would
        // otherwise keep showing the template's, not the appearance the user picked.
        //
        // SETTING it is not, and can do harm. SkyPatcher cannot re-point the recipient's TPLT, so
        // the flag could only ever make the recipient inherit from ITS OWN template — never the
        // donor's. That is inert when the recipient has no TPLT, and wrong when it has one set for
        // inventory/AI inheritance with the Traits bit deliberately off. Actions are written
        // alphabetically ordered, so it would also apply after copyVisualStyle rather than before.
        if (ShouldChangeTraitsStatus(winningNpcOverride, patchNpc, out bool hasTraitsStatus) && !hasTraitsStatus)
        {
            _skyPatcherInterface.ToggleTemplateTraitsStatus(npcFormKey, false);
        }

        if (includeOutfit)
        {
            _skyPatcherInterface.SetOutfit(npcFormKey, patchNpc.DefaultOutfit.FormKey);
        }
    }

    /// <summary>
    /// The outfit the actor is actually expected to wear when Include Outfits is OFF for its
    /// appearance mod — the recipient's effective outfit per <see cref="OutfitDisplayResolver"/>
    /// (chain-resolved, patch-mode-aware, runtime distributor layers included). Used both to
    /// root the Include-As-New duplication traversal and to look the minted duplicate up again
    /// at delivery time, so mint and delivery can never disagree on the key.
    /// </summary>
    private FormKey? ResolveRecipientEffectiveOutfit(FormKey npcFormKey, FormKey donorFormKey,
        ModSetting appearanceModSetting)
    {
        var display = _outfitDisplayResolver.ResolveForDisplay(npcFormKey, donorFormKey,
            appearanceModSetting, includeDefaultOutfitRenderFlag: true);
        return display.OutfitFormKey is { IsNull: false } fk ? fk : null;
    }

    /// <summary>
    /// SkyPatcher-mode delivery of Include-As-New outfit-side duplicates. Repointing the actor's
    /// outfit at the mod's private copy is plumbing ("which copy of the outfit"), not an outfit
    /// opinion ("whose outfit") — the Include Outfits flag answers only the latter, so this runs
    /// even when it is off (docs/SkyPatcher-IncludeAsNew-Outfit-Records.md §2). copyVisualStyle
    /// never carries an outfit, so outfitDefault=/outfitSleep= are the only channels to the game;
    /// each is emitted ONLY when this batch actually minted a private copy of the outfit the
    /// actor will wear — otherwise the runtime outfit contest is left alone. When Include Outfits
    /// is ON (or a wig forwarded an outfit), the default outfit is already emitted from the
    /// surrogate's post-remap field by <see cref="ApplySkyPatcherDirectives"/>, so only the sleep
    /// outfit is considered here.
    /// </summary>
    private void DeliverIncludeAsNewOutfitDirectives(FormKey npcFormKey, INpcGetter winningNpcOverride,
        FormKey donorFormKey, ModSetting appearanceModSetting, bool includeOutfit)
    {
        if (!includeOutfit &&
            ResolveRecipientEffectiveOutfit(npcFormKey, donorFormKey, appearanceModSetting) is { } effectiveOutfit &&
            _recordHandler.TryGetDuplicateMapping(effectiveOutfit, out var outfitDup) &&
            outfitDup != effectiveOutfit)
        {
            _skyPatcherInterface.SetOutfit(npcFormKey, outfitDup);
        }

        var sleepLink = winningNpcOverride.SleepingOutfit;
        if (!sleepLink.IsNull &&
            _recordHandler.TryGetDuplicateMapping(sleepLink.FormKey, out var sleepDup) &&
            sleepDup != sleepLink.FormKey)
        {
            _skyPatcherInterface.SetSleepOutfit(npcFormKey, sleepDup);
        }
    }

    /// <summary>
    /// Post-run sweep for 'Include As New' and dependency merge-in: a record minted into the
    /// output under a NEW FormKey that nothing references — no FormLink on any output record, no
    /// FormKey-valued SkyPatcher directive, no generated SPID assignment — is dead cargo. The game
    /// cannot resolve it, and the edits it carries silently miss the NPCs it was duplicated for.
    ///
    /// <para><b>Copies are pruned; anything else is reported.</b> The measured population was 135
    /// records in one run: the wig pipeline strips a converted ArmorAddon out of the WornArmor
    /// duplicate, but the appearance merge still walks the ORIGINAL WornArmor — the seeded
    /// duplicate mapping stops the armor itself from being copied, not the traversal of its links
    /// — so every superseded child was copied and then pointed at by nothing (129
    /// <c>HighPoly_WigAA_*</c> ArmorAddons, 5 skin Armors, a replaced hair HeadPart). Suppressing
    /// the walk instead was tried and is NOT safe: the same record can still be reachable from
    /// another copy the run makes, and skipping it leaves that copy pointing into a donor plugin
    /// that may not be in the load order — Mutagen then refuses to write the plugin at all. Judging
    /// the finished output cannot make that mistake: a record nothing references can always go.
    /// Pruning also runs before ESL compaction, so the freed FormIDs are freed where it counts.</para>
    ///
    /// <para>Removal repeats to a fixpoint — dropping a copy orphans the sub-records only it
    /// referenced (a wig ArmorAddon's TextureSet) — and is limited to records duplicated from a
    /// source (<see cref="RecordHandler.TryGetMergedRecordOrigin"/>). Records this run AUTHORED
    /// (minted head parts, generated outfits) and NPC records are left alone and merely reported:
    /// they are the product rather than transport, and a future feature could deliver one by
    /// FormID through a channel this scan does not know about.</para>
    ///
    /// <para>Deliberately NOT a <see cref="NpcWarningReporter"/> warning (user standard,
    /// 2026-08-02): colored WARNINGs are reserved for issues the user notices in game, and an
    /// unreferenced record is inert there — dead weight in the plugin, nothing more. What was
    /// PRUNED is verbose-only in full (summary included): it is housekeeping the user does not need
    /// told about every run. What was LEFT behind still gets a forced neutral one-line note, which
    /// is the maintainer-facing tripwire, with the per-record list at verbose.</para>
    /// </summary>
    private void PruneAndLogOrphanedDuplicates()
    {
        var outputMod = _environmentStateProvider.OutputMod;
        var outputKey = outputMod.ModKey;

        // Referenced from OUTSIDE the plugin: the emitted SkyPatcher ini and the generated SPID
        // ini name output records by FormID, so no FormLink in the plugin points at them.
        var externallyReferenced = new HashSet<FormKey>(_skyPatcherInterface.EnumerateDirectiveFormKeys());
        externallyReferenced.UnionWith(_forwardedOutfitDistributor.EnumerateSpidReferencedFormKeys());

        HashSet<FormKey> CollectReferenced()
        {
            var referenced = new HashSet<FormKey>(externallyReferenced);
            foreach (var record in outputMod.EnumerateMajorRecords())
            {
                foreach (var link in record.EnumerateFormLinks())
                {
                    if (link.FormKey.ModKey == outputKey)
                    {
                        referenced.Add(link.FormKey);
                    }
                }
            }

            return referenced;
        }

        string DescribeOrphan(IMajorRecordGetter record)
        {
            string sourceNote = _recordHandler.TryGetMergedRecordOrigin(record.FormKey, out var origin)
                ? $"copied from {origin.SourceEditorId ?? "(no EditorID)"} ({origin.SourceFormKey})"
                : "authored by this run";

            string ownerNote = string.Empty;
            if (_patchedRecordOwners.TryGetValue(record.FormKey, out var owners) && owners.Count > 0)
            {
                var mods = owners
                    .Select(o => _npcAppearanceSources.TryGetValue(o, out var src) ? src.ModName : null)
                    .Where(m => !string.IsNullOrEmpty(m))
                    .Distinct()
                    .ToList();
                ownerNote = $"; minted for {owners.Count} NPC selection(s)" +
                            (mods.Count > 0 ? $" from {string.Join(", ", mods)}" : string.Empty);
            }

            return $"  - {record.EditorID ?? "(no EditorID)"} [{record.Registration.Name}] " +
                   $"{record.FormKey}: {sourceNote}{ownerNote}";
        }

        var prunedLines = new List<string>();
        // Records that refused to be removed, so a failure is reported once instead of being
        // retried (and re-listed) on every pass — it stays unreferenced, so it stays a candidate.
        var removalFailures = new HashSet<FormKey>();

        // The fixpoint terminates on its own (each pass removes at least one record or stops); the
        // cap is a pure safety net so a future bug cannot spin here at the end of a long run.
        for (int pass = 0; pass < 32; pass++)
        {
            var referenced = CollectReferenced();
            var doomed = outputMod.EnumerateMajorRecords()
                .Where(r => r.FormKey.ModKey == outputKey
                            && r is not INpcGetter
                            && !referenced.Contains(r.FormKey)
                            && !removalFailures.Contains(r.FormKey)
                            && _recordHandler.TryGetMergedRecordOrigin(r.FormKey, out _))
                .ToList();
            if (doomed.Count == 0) break;

            foreach (var record in doomed)
            {
                try
                {
                    outputMod.Remove(record.FormKey, record.Registration.GetterType);
                    RecordProvenanceDiag.RemoveOutputRecord(record.FormKey);
                    prunedLines.Add(DescribeOrphan(record));
                }
                catch (Exception ex)
                {
                    // Not every record type can be removed by FormKey (Cells and placed references
                    // live inside groups). Leaving it in place is harmless — it was inert anyway.
                    removalFailures.Add(record.FormKey);
                    prunedLines.Add($"  - (could not be removed) {record.FormKey}: {ex.Message}");
                }
            }
        }

        if (prunedLines.Count > 0)
        {
            int removedCount = prunedLines.Count - removalFailures.Count;

            // Verbose-only, summary included: removing dead weight is housekeeping, not something
            // the user needs told about on every run. Neutral phrasing on purpose besides — no
            // WARNING:/ERROR: marker, so RunLogClassifier leaves it uncolored.
            AppendLog($"Note: removed {removedCount} private record cop" +
                      $"{(removedCount == 1 ? "y" : "ies")} that nothing in the output referenced " +
                      "(created by 'Include As New' / dependency merge-in, then superseded — most often " +
                      "by wig handling rewriting the record that used to point at them). They were inert " +
                      "in game; removing them keeps the plugin and its FormID space clean. If an NPC that " +
                      "should have received a mod's non-NPC edits looks wrong, the list below is the " +
                      "place to start.", false);
            foreach (var line in prunedLines)
            {
                AppendLog(line, false);
            }
        }

        // Whatever is STILL unreferenced was authored by this run (or could not be removed), so it
        // is deliberately left in place — but it is exactly the delivery-gap tripwire this check
        // exists for (RS Children's outfit chain shipped orphaned for months;
        // docs/SkyPatcher-IncludeAsNew-Outfit-Records.md).
        var remaining = CollectReferenced();
        var orphanLines = outputMod.EnumerateMajorRecords()
            .Where(r => r.FormKey.ModKey == outputKey && !remaining.Contains(r.FormKey))
            .Where(r => r is not INpcGetter)
            .Select(DescribeOrphan)
            .ToList();
        if (orphanLines.Count == 0) return;

        AppendLog($"Note: {orphanLines.Count} record{(orphanLines.Count == 1 ? "" : "s")} ended up " +
                  "unreferenced by the output and were left in place — records this run authored may be " +
                  "delivered by FormID rather than by a link. These are inert in game (unused records; no " +
                  "visible effect). If an NPC that should have received a mod's non-NPC edits looks wrong, " +
                  "this list (in the verbose log) is the place to start.", false, true);
        foreach (var line in orphanLines)
        {
            AppendLog(line, false);
        }
    }

    private bool ShouldChangeRace(INpcGetter targetNpc, INpcGetter appearanceNpc, out FormKey? changeTo)
    {
        changeTo = null;
        if (!targetNpc.Race.Equals(appearanceNpc.Race))
        {
            changeTo = appearanceNpc.Race.FormKey;
            return true;
        }
        return false;
    }

    private bool ShouldChangeGender(INpcGetter targetNpc, INpcGetter appearanceNpc, out Gender? changeTo)
    {
        changeTo = null;
        
        var targetGender = Auxilliary.GetGender(targetNpc);
        var appearanceGender = Auxilliary.GetGender(appearanceNpc);
        if (appearanceGender == Gender.Male && targetGender == Gender.Female)
        {
            changeTo = Gender.Male;
            return true;
        }

        if (appearanceGender == Gender.Female && targetGender == Gender.Male)
        {
            changeTo = Gender.Female;
            return true;
        }

        return false;
    }

    private void SetGender(Npc targetNpc, Gender gender)
    {
        if (gender == Gender.Female)
        {
            // Set Female bit
            targetNpc.Configuration.Flags |= NpcConfiguration.Flag.Female;
        }
        else
        {
            // Clear Female bit
            targetNpc.Configuration.Flags &= ~NpcConfiguration.Flag.Female;
        }
    }

    /// <summary>
    /// Does this appearance record change head data that the game will need a FaceGen for? Used to
    /// decide whether a missing FaceGen mesh is a black-face risk or a non-event.
    ///
    /// <para>An inherited appearance (Traits flag + template) is a non-event: the game renders the
    /// TEMPLATE's face and never loads a FaceGen under this NPC's FormID, so its own head-part /
    /// FaceMorph / FaceParts values are inert no matter how far they drift from the base record.
    /// Warning about those buried the real cases — one run produced ~200 of them, almost all on
    /// generic template users (Enc*Template, TreasCorpse*, "Redguard Woman"...).</para>
    ///
    /// <para>The diff is measured against the DONOR's own origin record, not the target's, because
    /// the output carries the donor's head data.</para>
    /// </summary>
    /// <summary>
    /// Gathers the ladder's inputs for one NPC, classifies, and records the verdict to
    /// <see cref="FaceGenLadderDiag"/>. The result drives both the abort decision here and the
    /// asset sourcing in <see cref="AssetHandler.ScheduleCopyNpcAssets"/>.
    ///
    /// <para>Everything is measured at the SUBJECT's FaceGen paths — the end of the donor's Traits
    /// chain — because that is the record the engine builds a face from. The donor's own paths are
    /// gathered too, but only to describe what the pre-ladder code did, which keyed off the donor
    /// and therefore found nothing at all whenever the donor inherited.</para>
    ///
    /// <para>Two deliberate cost controls, because this runs for every NPC in a run of thousands:
    /// the origin/winner probes touch the disk and are skipped entirely for the ~96% of NPCs whose
    /// mod ships both halves, and head-part compatibility (which parses a NIF, often after a BSA
    /// extraction) is evaluated only for the branches that actually gate on it.</para>
    /// </summary>
    /// <summary>The ladder's verdict plus the donor record it was reached with. The two travel
    /// together because a mesh-only selection can be re-paired with a different record from the
    /// NPC's own mod of origin (see <see cref="TryRepairMeshOnlyRecordPairingAsync"/>), and the
    /// caller must splice the record the mesh was actually graded against.</summary>
    private sealed record FaceGenPlan(FaceGenLadderDecision Decision, INpcGetter DonorRecord);

    private async Task<FaceGenPlan> ComputeFaceGenDecisionAsync(
        FormKey targetNpcFormKey, INpcGetter appearanceNpcRecord, ModSetting appearanceModSetting,
        HashSet<string> currentModFolderPaths, string modDisplayName, string npcIdentifier,
        bool isFaceGenOnly)
    {
        var linkCache = _environmentStateProvider.LinkCache;
        var chainHops = new List<string>();
        // Each hop resolves from the SELECTED MOD's plugins first (see ResolveNpcPreferringMod),
        // exactly as the donor record itself was resolved. The walk already starts from the donor
        // record rather than its FormKey so it carries the donor's inheritance; resolving the hops
        // through the load order instead would abandon that halfway and follow the winner's chain.
        var chainStatus = Auxilliary.TryResolveAppearanceTerminus(
            appearanceNpcRecord,
            fk => ResolveNpcPreferringMod(fk, appearanceModSetting, currentModFolderPaths, isFaceGenOnly),
            out var subjectFormKey,
            fk => linkCache != null && linkCache.TryResolve<ILeveledNpcGetter>(fk, out _),
            chainHops.Add);

        var (subjectNifRel, subjectDdsRel) =
            Auxilliary.GetFaceGenSubPathStrings(subjectFormKey, regularized: true);
        var (donorNifRel, donorDdsRel) =
            Auxilliary.GetFaceGenSubPathStrings(appearanceNpcRecord.FormKey, regularized: true);

        var sourceNif = _assetHandler.GetAssetPresence(subjectNifRel, appearanceModSetting);
        var sourceDds = _assetHandler.GetAssetPresence(subjectDdsRel, appearanceModSetting);

        var mode = _settings.UseSkyPatcherMode
            ? FaceGenDestinationMode.SkyPatcher
            : targetNpcFormKey.Equals(appearanceNpcRecord.FormKey)
                ? FaceGenDestinationMode.Record
                : FaceGenDestinationMode.FaceSwap;

        // Row 1 consults no fallback, and a short-circuiting chain status consults nothing at all,
        // so neither pays for the origin lookup or the two Data-folder probes.
        bool bothHalvesPresent = sourceNif != FaceGenAssetPresence.NotFound
                                 && sourceDds != FaceGenAssetPresence.NotFound;
        bool chainShortCircuits = chainStatus is FaceGenChainStatus.LeveledTerminus
                                              or FaceGenChainStatus.Unfollowable;
        bool needsFallbacks = !bothHalvesPresent && !chainShortCircuits;

        ModSetting? originModSetting = null;
        var originNif = FaceGenAssetPresence.NotFound;
        var originDds = FaceGenAssetPresence.NotFound;
        bool originRecordExists = false;
        bool winnerNif = false, winnerDds = false;
        string? winnerOwner = null;

        if (needsFallbacks)
        {
            originModSetting = _assetHandler.FindOriginModSetting(subjectFormKey.ModKey, subjectNifRel);
            if (originModSetting != null)
            {
                originNif = _assetHandler.GetAssetPresence(subjectNifRel, originModSetting);
                originDds = _assetHandler.GetAssetPresence(subjectDdsRel, originModSetting);
            }

            originRecordExists = linkCache != null &&
                                 linkCache.TryResolve<INpcGetter>(subjectFormKey, out _, ResolveTarget.Origin);
            winnerNif = _assetHandler.WinningAssetExists(subjectNifRel, out winnerOwner);
            winnerDds = _assetHandler.WinningAssetExists(subjectDdsRel, out _);
        }

        var inputs = new FaceGenLadderInputs(
            NpcIdentifier: npcIdentifier,
            TargetFormKey: targetNpcFormKey,
            DonorFormKey: appearanceNpcRecord.FormKey,
            SubjectFormKey: subjectFormKey,
            ChainStatus: chainStatus,
            ModName: modDisplayName,
            Mode: mode,
            SourceNif: sourceNif,
            SourceDds: sourceDds,
            SourceHasPluginRecord: !isFaceGenOnly,
            OriginRecordExists: originRecordExists,
            OriginNif: originNif,
            OriginDds: originDds,
            WinnerNifExists: winnerNif,
            WinnerNifOwner: winnerOwner,
            WinnerDdsExists: winnerDds,
            OriginNifCompatible: null,
            WinnerNifCompatible: null,
            LegacyDonorNif: _assetHandler.GetAssetPresence(donorNifRel, appearanceModSetting),
            LegacyDonorDds: _assetHandler.GetAssetPresence(donorDdsRel, appearanceModSetting),
            ChainTrace: string.Join(" ", chainHops),
            // Per-mod override when set, else the global setting. This is the single point
            // where the mode enters the pipeline — the record flatten and the asset stage
            // both read it back off the decision, so they cannot disagree per NPC.
            FlattenTemplateChain: _settings.GetEffectiveTemplateHandlingMode(appearanceModSetting)
                                  == TemplateHandlingMode.GiveEachNpcOwnCopy);

        var decision = FaceGenLadder.Classify(inputs);

        // Row 3 gates a borrowed mesh on head-part compatibility; rows 4/5 probe (without gating)
        // both the origin mesh and the winner fallback; row 2 with a FaceGen-only selection probes
        // the MOD's mesh against the origin's record (same cross-author pairing, roles reversed).
        // Classifying first tells us whether any parse is worth doing at all.
        if (NeedsCompatibilityCheck(decision) || ProbesModMesh(decision))
        {
            // Match against the record the ENGINE will reconcile the mesh with. For a flattened
            // Traits chain that is the TERMINUS record — CopyInheritedAppearance overlays its head
            // parts onto whatever ships — resolved here exactly the way the flatten itself
            // resolves it. Matching the donor there graded the mesh against head parts the
            // overlay was about to replace (seam fixed 2026-07-30). Otherwise: the origin record
            // when row 4 forwards it, else the donor's own.
            INpcGetter? originRecordForMatch = null;
            if (decision.ForwardOriginRecord && linkCache != null &&
                linkCache.TryResolve<INpcGetter>(subjectFormKey, out var originRec, ResolveTarget.Origin))
            {
                originRecordForMatch = originRec;
            }

            var recordToMatch = ChooseCompatibilityRecord(
                ResolveAppearanceTerminusRecord(decision, appearanceModSetting, currentModFolderPaths, isFaceGenOnly),
                originRecordForMatch,
                appearanceNpcRecord);

            // Each side is parsed only where its answer can matter: the mod's own mesh only on
            // the row-2 FaceGen-only shape, the origin/winner fallbacks only on the rows that
            // consult them — a row-2 NPC never pays for fallback parses it will not use. Failed
            // probes keep their evidence, composed into CompatProbeNotes for the detailed
            // warning log (which record was graded, which plugins rewrite its head data, and
            // each probe's record-needs vs mesh-bakes lists).
            bool probeFallbacks = NeedsCompatibilityCheck(decision);
            var failedProbes = new List<(string Label, string? Mismatch)>();

            bool? sourceCompat = null, originCompat = null, winnerCompat = null;
            if (ProbesModMesh(decision) && appearanceModSetting != null)
            {
                (sourceCompat, var mm) =
                    await EvaluateNifCompatibilityAsync(recordToMatch, subjectNifRel, appearanceModSetting);
                if (sourceCompat == false) failedProbes.Add(("selected mod's mesh failed the probe", mm));
            }

            if (probeFallbacks && originModSetting != null && originNif != FaceGenAssetPresence.NotFound)
            {
                (originCompat, var mm) =
                    await EvaluateNifCompatibilityAsync(recordToMatch, subjectNifRel, originModSetting);
                if (originCompat == false) failedProbes.Add(("origin mesh failed the probe", mm));
            }

            if (probeFallbacks && winnerNif)
            {
                (winnerCompat, var mm) =
                    await EvaluateWinningNifCompatibilityAsync(recordToMatch, subjectNifRel);
                if (winnerCompat == false) failedProbes.Add(("winner mesh failed the probe", mm));
            }

            inputs = inputs with
            {
                SourceNifCompatible = sourceCompat,
                OriginNifCompatible = originCompat,
                WinnerNifCompatible = winnerCompat,
                CompatProbeNotes = failedProbes.Count > 0
                    ? ComposeCompatProbeContext(recordToMatch, subjectFormKey, failedProbes)
                    : null,
            };

            decision = FaceGenLadder.Classify(inputs);
        }

        // The one pairing none of the legs above probe: the MOD's own mesh shipping with a record
        // it was authored with — self-consistent by authorship (see ProbesModMesh). That
        // consistency can still break from OUTSIDE, through the RACE record: for head slots the
        // record leaves unset, the engine falls back to the race's chargen defaults resolved from
        // the LIVE load order, while the mesh was baked against the race as the mod's author saw
        // it. RS Children is the measured case — its NPC records merge into the output but its
        // race edit is not carried over, so the defaults roll back to vanilla under its meshes —
        // and an unrelated mod winning the race record does the same (docs/KnownLimitations.md #5).
        // A record-level trigger (do the two contexts disagree about the defaults?) picks the NPCs
        // that pay for a mesh parse, so the vast majority, whose race resolves identically, skip
        // it. Warn-don't-gate, like every compat probe. Findings are HELD rather than recorded:
        // the remedy is the per-mod Record Override Handling Mode, and which of its modes fits
        // depends on the whole run's selections (see FlushRaceDriftFindings) — and a mod already
        // set to Include/IncludeAsNew ships its race edit, so its drift is being handled and
        // must not warn. Not routed through a ladder input because SourceNifCompatible belongs
        // to the row-2 pairing — setting it would trip ModMeshFailedCompatCheck's unrelated
        // warning.
        if (!decision.Abort && appearanceModSetting != null)
        {
            var gradedRecord = ChooseCompatibilityRecord(
                ResolveAppearanceTerminusRecord(decision, appearanceModSetting, currentModFolderPaths, isFaceGenOnly),
                null, appearanceNpcRecord);

            var drift = TryEvaluateRaceDrift(gradedRecord, appearanceModSetting, currentModFolderPaths);
            if (drift != null)
            {
                // The advice census needs every selected mod that puts NPCs on this race, with
                // the race version each was authored against — drifted or not: a baseline-
                // authored mod on the same race is exactly what rules plain Include out.
                RegisterRaceUsage(drift.RaceFormKey, modDisplayName, drift.AuthorDefaults);

                var overrideMode = isFaceGenOnly
                    ? RecordOverrideHandlingMode.Ignore // mirrors the caller: FaceGen-only forces Ignore
                    : appearanceModSetting.ModRecordOverrideHandlingMode ?? _settings.DefaultRecordOverrideHandlingMode;

                if (drift.Drifted
                    && overrideMode == RecordOverrideHandlingMode.Ignore
                    && decision.NifChoice == FaceGenSourceChoice.AppearanceMod
                    && decision.Inputs.SourceNifCompatible is null)
                {
                    var (driftCompat, driftMismatch) =
                        await EvaluateDriftNifCompatibilityAsync(gradedRecord, subjectNifRel, appearanceModSetting,
                            currentModFolderPaths);
                    if (driftCompat == false)
                    {
                        _raceDriftFindings.Add(new RaceDriftFinding(
                            npcIdentifier, modDisplayName, drift.RaceFormKey, drift.Detail,
                            decision.TechnicalSummary + "\n" + drift.TechnicalDetail + "\n" +
                            ComposeCompatProbeContext(gradedRecord, subjectFormKey,
                                new[] { ("selected mod's mesh failed the probe", driftMismatch) })));
                    }
                }
            }
        }

        // ---- Mesh-only record re-pairing -------------------------------------------------
        // A selection with no plugin RECORD has its mesh paired with the record from the NPC's
        // ORIGIN plugin, on the assumption the author built against it. That fails whenever a
        // later plugin of the NPC's own family supersedes it: Dawnguard swaps the vanilla
        // vampires' eye head part (FemaleEyesHumanDemon -> FemaleEyesHumanVampire) and mods bake
        // their FaceGen against Dawnguard's record, because that is what their Creation Kit
        // showed. The origin pairing then names a head part the mesh does not bake — dark face.
        //
        // Note this is keyed on SourceHasPluginRecord, NOT on the ladder ROW. The rows describe
        // which ASSETS the mod ships (row 2 = mesh but no tint); a mod can ship both halves and
        // still have no record, which is exactly the Nordic Faces / Cathedral shape and why an
        // earlier attempt gated on row 2 and never fired.
        if (!decision.Abort && isFaceGenOnly && appearanceModSetting != null &&
            decision.NifChoice == FaceGenSourceChoice.AppearanceMod &&
            // Only where the mesh and the record belong to the same NPC. A Traits redirect makes
            // the chain a property of the record being replaced, so re-pairing could invalidate
            // the very subject this was probed against.
            subjectFormKey.Equals(appearanceNpcRecord.FormKey))
        {
            // Cheap trigger first: with nothing in the family superseding the origin record there
            // is nothing to re-pair with, so skip before touching a NIF. This matters — row 1
            // ("mod ships both halves") is deliberately never NIF-probed, and 1136 of the
            // measuring run's 8338 selections are record-less. An in-memory context walk keeps
            // the parse for the handful that a DLC actually overrides.
            var family = ResolveOriginFamilyPlugins(subjectFormKey);
            if (family != null && HasSupersedingFamilyRecord(linkCache, subjectFormKey, family))
            {
                var pairingRecord = ChooseCompatibilityRecord(
                    ResolveAppearanceTerminusRecord(decision, appearanceModSetting, currentModFolderPaths, isFaceGenOnly),
                    null, appearanceNpcRecord);

                // Row 2 already probed this exact pairing; reuse its verdict rather than parsing
                // the same mesh twice.
                bool? compatible = decision.Inputs.SourceNifCompatible;
                if (compatible == null)
                {
                    (compatible, _) = await EvaluateNifCompatibilityAsync(
                        pairingRecord, subjectNifRel, appearanceModSetting);
                }

                if (compatible == false)
                {
                    var repaired = await TryRepairMeshOnlyRecordPairingAsync(
                        subjectFormKey, subjectNifRel, appearanceModSetting, npcIdentifier);

                    if (repaired != null)
                    {
                        appearanceNpcRecord = repaired;
                    }
                    else
                    {
                        // No record in the family fits, so there is no safe pairing left: patching
                        // would ship a record naming head parts the mesh does not bake. Abort, as
                        // the ladder does when no usable mesh exists at all — the NPC keeps the
                        // face the load order already gives it and is named in the run report.
                        //
                        // Decided here rather than in FaceGenLadder.Classify because "no
                        // compatible record in the origin family" is not one of the ladder's
                        // inputs, and SourceNifCompatible alone means only that the default
                        // pairing failed, which it deliberately warns about rather than treats
                        // as fatal.
                        string reason =
                            $"'{modDisplayName}' supplies a face mesh for this NPC but no plugin record, and the " +
                            "mesh does not match any record for it in the mod that originally added the NPC. " +
                            "Patching it would produce the dark-face bug, so it is being left unchanged. Pick a " +
                            "different mod for this NPC, or install the overhaul this mod was built to sit on top of.";

                        decision = decision with
                        {
                            Abort = true,
                            AbortReason = reason,
                            LogLine = $"ABORT: {reason}",
                        };
                    }
                }
            }
        }

        FaceGenLadderDiag.Record(decision);
        return new FaceGenPlan(decision, appearanceNpcRecord);
    }

    /// <summary>
    /// A mesh-only selection ships no plugin record, so its mesh is paired with the record from
    /// the NPC's ORIGIN plugin. When that pairing fails the head-part probe, try the other records
    /// the NPC's own mod of origin offers — its plugins walked winner-first, back toward the
    /// origin — and return the first whose head parts the mesh actually bakes. Null when none fit.
    ///
    /// <para>Candidates are confined to the mod entry that owns the NPC's origin plugin (for a
    /// vanilla NPC: the "Base Game" entry, i.e. Skyrim.esm plus the DLC). Ranging wider would pair
    /// the mesh with some unrelated appearance mod's record, which is the very thing the
    /// origin-pairing rule exists to avoid.</para>
    ///
    /// <para>Only called after the default pairing has already failed, so the walk costs nothing
    /// for the NPCs that are fine, and no correctly-rendering NPC can be re-pointed by it.</para>
    /// </summary>
    /// <summary>
    /// The plugins that count as "the same mod of origin" as <paramref name="subjectFormKey"/>'s
    /// defining plugin, for the mesh-only re-pairing walk. Null when the NPC has no such family.
    ///
    /// <para>A vanilla NPC's family is the base game INCLUDING the DLC — those ship with every
    /// SE/AE/VR install, so a mod author's Creation Kit shows the DLC's version of the record and
    /// that is what their mesh was baked against. Creation Club is deliberately NOT included: it
    /// is optional content and its own mod entry, so its records are no more "the base game" than
    /// any other mod's.</para>
    ///
    /// <para>For a mod-added NPC the family is the mod entry that provides it, and only when that
    /// entry really lists this NPC — the owner index is first-wins across all mod entries, so
    /// without that guard an unrelated entry that happens to name the plugin could hand back a
    /// candidate set the mesh was never authored against.</para>
    /// </summary>
    private HashSet<ModKey>? ResolveOriginFamilyPlugins(FormKey subjectFormKey)
    {
        var baseGame = _environmentStateProvider.BaseGamePlugins;
        if (baseGame.Contains(subjectFormKey.ModKey)) return baseGame;

        if (_npcProvidingOwnersByPlugin.TryGetValue(subjectFormKey.ModKey, out var originMod) &&
            originMod?.CorrespondingModKeys != null &&
            originMod.NpcFormKeys != null &&
            originMod.NpcFormKeys.Contains(subjectFormKey))
        {
            return originMod.CorrespondingModKeys.ToHashSet();
        }

        return null;
    }

    /// <summary>Does any plugin of <paramref name="family"/> other than the NPC's own defining
    /// plugin carry a record for it? False means the origin record IS the family's winner, so
    /// there is nothing to re-pair with and no reason to parse a mesh. In-memory only.</summary>
    internal static bool HasSupersedingFamilyRecord(
        ILinkCache<ISkyrimMod, ISkyrimModGetter>? linkCache, FormKey subjectFormKey, HashSet<ModKey> family)
    {
        if (linkCache == null || family.Count == 0) return false;

        foreach (var ctx in linkCache.ResolveAllContexts<INpc, INpcGetter>(subjectFormKey))
        {
            if (!ctx.ModKey.Equals(subjectFormKey.ModKey) && family.Contains(ctx.ModKey)) return true;
        }

        return false;
    }

    private async Task<INpcGetter?> TryRepairMeshOnlyRecordPairingAsync(
        FormKey subjectFormKey, string subjectNifRel, ModSetting meshMod, string npcIdentifier)
    {
        var linkCache = _environmentStateProvider.LinkCache;
        if (linkCache == null) return null;

        var candidatePlugins = ResolveOriginFamilyPlugins(subjectFormKey);
        if (candidatePlugins == null || candidatePlugins.Count < 2) return null;

        // Winner-first, which is the order the mod's author saw in the Creation Kit; the walk
        // continues back toward the origin so a mesh authored against an older record still finds
        // its match. Every candidate is probed, including the origin plugin's own record — it is
        // the one that just failed, but only as resolved for the CURRENT pairing, and re-testing
        // it costs one parse on a path that is already rare.
        foreach (var ctx in linkCache.ResolveAllContexts<INpc, INpcGetter>(subjectFormKey))
        {
            if (!candidatePlugins.Contains(ctx.ModKey)) continue;

            var (compatible, _) = await EvaluateNifCompatibilityAsync(ctx.Record, subjectNifRel, meshMod);
            if (compatible != true) continue;

            AppendLog(
                $"      Re-paired mesh-only selection with '{ctx.ModKey.FileName}' — its record's head parts " +
                $"match the mesh, the origin plugin's do not.", false, false);
            NpcDiagnosticLogger.Log(
                $"  Mesh-only pairing repaired for {npcIdentifier}: origin record failed the head-part probe; " +
                $"using '{ctx.ModKey.FileName}' instead.");
            return ctx.Record;
        }

        return null;
    }

    /// <summary>
    /// Names every NPC the ladder refused to patch, at the end of the run where it will actually be
    /// read. These NPCs keep whatever appearance the load order already gave them, which is worth
    /// saying plainly — the user picked a mod for them and did not get it. This is the ONLY place
    /// the skips are forced into a non-verbose log; the per-NPC line at the abort site is
    /// verbose-only so the same sentence does not print twice.
    ///
    /// <para>The header carries the "WARNING: " marker <see cref="View_Models.RunLogClassifier"/>
    /// colours on, matching the <see cref="NpcWarningReporter"/> groups it prints beside: unlike
    /// the sibling reports below, whose NPCs ARE patched, these ones did not get the mod the user
    /// chose. The per-NPC lines stay unmarked there and here — the heading is what signals the
    /// group, and a wall of coloured entries would drown it.</para>
    /// </summary>
    private void ReportFaceGenSkippedNpcs()
    {
        if (_faceGenSkippedNpcs.Count == 0) return;

        AppendLog($"\nWARNING: {_faceGenSkippedNpcs.Count} NPC(s) were left unpatched because their face " +
                  $"could not be assembled safely. They will look the way they did before this run:", false, true);

        foreach (var (npc, mod, reason) in _faceGenSkippedNpcs)
        {
            AppendLog($"  - {npc} (you picked '{mod}'): {reason}", false, true);
        }

        _faceGenSkippedNpcs.Clear();
    }

    /// <summary>
    /// Names every NPC whose Include Outfit write could not reach the game, at the end of the run.
    /// Unlike <see cref="ReportFaceGenSkippedNpcs"/> these NPCs ARE patched — their face applies
    /// normally — so this is deliberately a report and not a skip: promoting it to the blocking
    /// pre-run dialog (whose wording is "invalid selections that will be skipped") would be wrong
    /// on both counts and would train users to dismiss it.
    /// </summary>
    private void ReportInertOutfitNpcs()
    {
        if (_inertOutfitNpcs.Count == 0) return;

        AppendLog($"\n{_inertOutfitNpcs.Count} NPC(s) had 'Include Outfit' enabled but take their whole " +
                  "inventory — the default outfit with it — from a template, so the outfit written to their " +
                  "record is never worn in game. Their appearance was patched normally:", false, true);

        foreach (var (npc, mod, template) in _inertOutfitNpcs)
        {
            AppendLog($"  - {npc} (from '{mod}'): inventory template {template}", false, true);
        }

        _inertOutfitNpcs.Clear();
    }

    /// <summary>
    /// Names every NPC whose chosen appearance could not reach it because its face is inherited,
    /// and says what to change. Like <see cref="ReportInertOutfitNpcs"/> these NPCs ARE patched —
    /// this is the documented meaning of the inherit mode, not a failure — so it is a report rather
    /// than a screening rejection; the pre-run dialog's "invalid selections that will be skipped"
    /// would be wrong on both counts, and hundreds of entries there would train users past it.
    ///
    /// <para>The two outcomes are separated because they are not equally disappointing: an NPC
    /// whose template was itself given a mod does change appearance — to the template's choice —
    /// while one whose template was left alone does not change at all.</para>
    ///
    /// <para>VERBOSE-ONLY, headline included (user direction, 2026-07-31), and the only one of these
    /// four reports that is. Inheriting is not an anomaly here — it is precisely what "Use the
    /// template's appearance" means, so EVERY templated NPC in the load order lands in this list
    /// (755 on the reporting run) and every one of them renders correctly. A forced report of that
    /// size is the whole log, and it reads as several hundred things having gone wrong when nothing
    /// did. The siblings stay forced because they report the opposite: a pick that genuinely could
    /// not be delivered, in numbers a user can act on.</para>
    /// </summary>
    private void ReportInheritedFaceNpcs()
    {
        if (_inheritedFaceNpcs.Count == 0) return;

        AppendLog($"\n{_inheritedFaceNpcs.Count} NPC(s) have no face of their own — they take it from " +
                  "another NPC (the Traits template flag) — and 'Templated NPCs' is set to \"Use the " +
                  "template's appearance\", so the mod picked for them could not be applied to them. Their " +
                  "records were patched normally; only their faces still come from their templates. To give " +
                  "them their own face instead, set 'Templated NPCs' to \"Give each NPC its own copy\" " +
                  "(globally in Settings, or per mod in Mods):");

        foreach (var (npc, mod, template, templateSelection) in _inheritedFaceNpcs)
        {
            AppendLog(
                $"  - {npc} (you picked '{mod}'): takes its face from {template}" +
                (templateSelection == null
                    ? ", which has no appearance selected, so this NPC will look unchanged."
                    : $", which is set to '{templateSelection}' — so that is the face this NPC will show."));
        }

        _inheritedFaceNpcs.Clear();
    }

    /// <summary>
    /// Names the NPCs whose template chain was flattened but whose chosen mod had no face to put on
    /// the flattened record, so it carries the origin's or the winner's instead.
    ///
    /// <para>Deliberately worded as "could not be applied" rather than as a failure: the patch is
    /// correct and the NPC renders, it simply renders a face the user did not choose. That is the
    /// same disappointment <see cref="ReportInheritedFaceNpcs"/> reports, and it used to be
    /// verbose-only here while being forced there — a difference the user had no way to predict.</para>
    /// </summary>
    private void ReportFlattenedFallbackNpcs()
    {
        if (_flattenedFallbackNpcs.Count == 0) return;

        AppendLog($"\n{_flattenedFallbackNpcs.Count} NPC(s) have no face of their own — they take it " +
                  "from another NPC (the Traits template flag) — and although 'Templated NPCs' is set to " +
                  "\"Give each NPC its own copy\", the mod picked for them supplies no face files for the " +
                  "NPC they copy from, so there was nothing of that mod's to give them. They were patched " +
                  "and will render, but with the face they would have had anyway. To change that, pick a " +
                  "mod that covers the NPC named below, or select that NPC directly:", false, true);

        foreach (var (npc, mod, template, source) in _flattenedFallbackNpcs)
        {
            AppendLog($"  - {npc} (you picked '{mod}'): copies its face from {template}, which " +
                      $"'{mod}' does not cover, so the face came from {source}.", false, true);
        }

        _flattenedFallbackNpcs.Clear();
    }

    /// <summary>The mod chosen for an NPC, or null when it has no selection. Read straight off the
    /// user's selections rather than off the run's progress, because the terminus may be patched
    /// after the NPC that inherits from it.</summary>
    private string? SelectionForNpc(FormKey npcFormKey) =>
        _settings.SelectedAppearanceMods != null
        && _settings.SelectedAppearanceMods.TryGetValue(npcFormKey, out var selection)
        && !string.IsNullOrEmpty(selection.ModName)
            ? selection.ModName
            : null;

    /// <summary>A FormKey as something a user can find in xEdit: the record's own log string when it
    /// resolves, the raw key when it does not. <see cref="Auxilliary.GetLogString"/> appends " | "
    /// after the EditorID of a record with no Name, so it is trimmed — otherwise the line ends on a
    /// dangling separator.</summary>
    private string DescribeFormKey(FormKey formKey)
    {
        var linkCache = _environmentStateProvider.LinkCache;
        return linkCache != null && linkCache.TryResolve<INpcGetter>(formKey, out var npc)
            ? Auxilliary.GetLogString(npc, _settings.LocalizationLanguage).TrimEnd(' ', '|')
            : formKey.ToString();
    }

    /// <summary>The record whose appearance gets flattened onto the output, or null whenever no
    /// flattening should happen: the user kept the default Template Handling Mode (inherit), the
    /// donor does not inherit, or the chain did not resolve to a concrete NPC (a levelled terminus
    /// or an unfollowable chain must keep inheriting regardless of the mode). Non-null answers are
    /// the terminus record at the end of the donor's Traits chain, and both output modes flatten
    /// from it: the SkyPatcher surrogate and the record-mode override alike.
    ///
    /// <para>The terminus is resolved from the SELECTED MOD's plugins (see
    /// <see cref="ResolveNpcPreferringMod"/>), because the FaceGen this flatten forwards to the
    /// NPC's own FormID is the mod's copy of the TERMINUS's mesh (measured at the subject's paths
    /// in <see cref="ComputeFaceGenDecisionAsync"/>). Taking the record from the load order's
    /// winning override instead paired that mesh with another plugin's head parts — the dark-face
    /// bug, from the app's own <see cref="FaceGenConsistencyAnalyzer"/> rule.</para></summary>
    private INpcGetter? ResolveAppearanceTerminusRecord(FaceGenLadderDecision? decision,
        ModSetting? appearanceModSetting, HashSet<string> currentModFolderPaths, bool isFaceGenOnly)
    {
        if (decision?.Inputs.ChainStatus != FaceGenChainStatus.Resolved) return null;
        if (!decision.Inputs.FlattenTemplateChain) return null;

        return ResolveNpcPreferringMod(decision.Inputs.SubjectFormKey, appearanceModSetting,
            currentModFolderPaths, isFaceGenOnly);
    }

    /// <summary>
    /// The FormKey whose FaceGen mesh will be copied to THIS NPC's own path, when a Traits chain is
    /// being flattened — the chain terminus. Null otherwise, meaning "the donor's own", which covers
    /// both the untemplated case (donor == subject) and the inheriting case (the NPC's own path
    /// receives nothing, so there is no bake target and the wig converter must decline as before).
    ///
    /// <para>Same gate as <see cref="ResolveAppearanceTerminusRecord"/>, kept in step with it: the
    /// mesh, the flattened record and the wig bake all have to agree on which record's face this
    /// NPC ends up wearing.</para>
    /// </summary>
    private static FormKey? FlattenedFaceGenSubject(FaceGenLadderDecision? decision) =>
        decision is { Inputs.ChainStatus: FaceGenChainStatus.Resolved, Inputs.FlattenTemplateChain: true }
            ? decision.Inputs.SubjectFormKey
            : null;

    /// <summary>
    /// Is this NPC's record-level <c>DefaultOutfit</c> a dead field? The Inventory template flag
    /// makes the engine take the NPC's whole inventory — the default outfit with it — from its
    /// template, so anything written to the record's own outfit is never worn.
    ///
    /// <para>SkyPatcher mode is exempt: there the outfit is applied at runtime by a
    /// <c>SetOutfit</c> directive (see <see cref="ApplySkyPatcherDirectives"/>), which acts on the
    /// actor and bypasses record-level template resolution entirely.</para>
    ///
    /// <para>The record examined is the one that will actually be written — the recipient's winning
    /// override in Create-and-Patch, the donor in Create — because that is where the flag lands.
    /// Flattening does not affect the answer: it clears Traits, never Inventory.</para>
    ///
    /// <para>Only the record SELECTION lives here; the test itself is
    /// <see cref="Settings.OutfitFieldIsInert"/>, shared with the output validator so the
    /// ForwardToOutfit → ConvertToHeadParts downgrade this drives cannot be known to one and not
    /// the other.</para>
    /// </summary>
    private bool RecordOutfitIsInert(INpcGetter winningNpcOverride, INpcGetter appearanceNpcRecord) =>
        _settings.OutfitFieldIsInert(_settings.PatchingMode == PatchingMode.CreateAndPatch
            ? winningNpcOverride
            : appearanceNpcRecord);

    /// <summary>Shorthand for <see cref="RecordHandler.ResolveNpcPreferringMod"/>, which the
    /// Validator also uses so screening walks the same Traits chain the patcher will.</summary>
    private INpcGetter? ResolveNpcPreferringMod(FormKey npcFormKey, ModSetting? appearanceModSetting,
        HashSet<string> currentModFolderPaths, bool isFaceGenOnly) =>
        _recordHandler.ResolveNpcPreferringMod(npcFormKey, appearanceModSetting, currentModFolderPaths,
            isFaceGenOnly);

    /// <summary>Whether this verdict rests on a BORROWED mesh (origin or winner) whose head-part
    /// compatibility has not been established from its own source.</summary>
    private static bool NeedsCompatibilityCheck(FaceGenLadderDecision d) =>
        !d.Abort
        && d.Row is FaceGenLadderRow.DdsOnlyWithRecord
                 or FaceGenLadderRow.DdsOnlyNoRecord
                 or FaceGenLadderRow.Neither
        && d.NifChoice is FaceGenSourceChoice.Origin
                       or FaceGenSourceChoice.Winner
                       or FaceGenSourceChoice.WinnerInPlace;

    /// <summary>Whether the MOD's own mesh should be probed: row 2 with a FaceGen-only selection
    /// ships the mod's mesh against the ORIGIN's record — the same cross-author pairing rows 4/5
    /// probe, with the roles reversed. Probed and warned (ModMeshFailedCompatCheck), never gated,
    /// matching the rows-4/5 stance. A row-2 mod that ships its own record authored the mesh and
    /// the record together, so it is self-consistent and never probed — the same reason row 1 is
    /// not probed at all.</summary>
    private static bool ProbesModMesh(FaceGenLadderDecision d) =>
        !d.Abort
        && d.Row == FaceGenLadderRow.NifOnly
        && !d.Inputs.SourceHasPluginRecord
        && d.NifChoice == FaceGenSourceChoice.AppearanceMod;

    /// <summary>The record the engine will reconcile a candidate mesh against, which is what
    /// compatibility must be graded on: the flatten TERMINUS when a Traits chain is being
    /// flattened (CopyInheritedAppearance overlays its head parts onto whatever ships), else the
    /// origin record when row 4 forwards it, else the donor's own record.</summary>
    private static INpcGetter ChooseCompatibilityRecord(
        INpcGetter? flattenTerminus, INpcGetter? forwardedOrigin, INpcGetter donorRecord) =>
        flattenTerminus ?? forwardedOrigin ?? donorRecord;

    /// <summary>What the race-drift trigger measured for one NPC. <see cref="Drifted"/> false
    /// still carries the census half (<see cref="RaceFormKey"/> + <see cref="AuthorDefaults"/>) —
    /// a non-drifting mod on the same race is exactly what the end-of-run advice needs to know
    /// about. <see cref="Detail"/>/<see cref="TechnicalDetail"/> are composed only when drifted.</summary>
    private sealed record RaceDriftInfo(
        FormKey RaceFormKey, bool Drifted, HashSet<FormKey> AuthorDefaults,
        string Detail, string TechnicalDetail);

    /// <summary>One race-drift probe failure, held until the end of the run so the recommended
    /// Record Override Handling Mode can be computed from the whole run's selections — see
    /// <see cref="FlushRaceDriftFindings"/>.</summary>
    private sealed record RaceDriftFinding(
        string NpcIdentifier, string ModName, FormKey RaceFormKey, string Detail, string TechnicalDetail);

    // race -> (selected mod -> the race-default set that mod was authored against). Fed by every
    // NPC whose drift trigger could evaluate, drifted or not; read by FlushRaceDriftFindings.
    // Kept across split-output batches (better census as the run progresses); cleared with the
    // other per-run state on the first iteration.
    private readonly Dictionary<FormKey, Dictionary<string, HashSet<FormKey>>> _raceDriftUsage = new();
    private readonly List<RaceDriftFinding> _raceDriftFindings = new();

    private void RegisterRaceUsage(FormKey raceFormKey, string modName, HashSet<FormKey> authorDefaults)
    {
        if (!_raceDriftUsage.TryGetValue(raceFormKey, out var byMod))
        {
            byMod = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);
            _raceDriftUsage[raceFormKey] = byMod;
        }
        byMod[modName] = authorDefaults; // a mod resolves the same author context every time; last write is fine
    }

    /// <summary>
    /// The cheap trigger in front of the race-drift mesh probe: do the selected mod's own plugins
    /// (falling back to the implicit-master baseline — see
    /// <see cref="RecordHandler.ResolveRacePreferringMod"/>) and the live load order disagree
    /// about the RACE's chargen default head parts for this record's sex? Record lookups only, no
    /// file I/O, so it can run for every NPC; the mesh parse is paid only on drift. The trigger
    /// deliberately over-fires a little — drift in a slot the NPC's own record occupies is
    /// harmless — because the probe behind it grades the truth and stays silent when the mesh
    /// fits. Null when the race cannot be evaluated (no race, unresolvable).
    /// </summary>
    private RaceDriftInfo? TryEvaluateRaceDrift(INpcGetter gradedRecord, ModSetting appearanceModSetting,
        HashSet<string> currentModFolderPaths)
    {
        if (gradedRecord.Race.IsNull) return null;
        var raceFk = gradedRecord.Race.FormKey;

        var linkCache = _environmentStateProvider.LinkCache;
        if (linkCache == null || !linkCache.TryResolve<IRaceGetter>(raceFk, out var liveRace) || liveRace == null)
        {
            return null; // an unresolvable race is the record checks' business, not this probe's
        }

        var authorRace = _recordHandler.ResolveRacePreferringMod(raceFk, appearanceModSetting, currentModFolderPaths);
        if (authorRace == null) return null;

        bool female = Auxilliary.IsFemale(gradedRecord);
        var authorDefaults = RaceDefaultHeadParts(authorRace, female);
        var liveDefaults = RaceDefaultHeadParts(liveRace, female);
        if (authorDefaults.SetEquals(liveDefaults))
        {
            return new RaceDriftInfo(raceFk, Drifted: false, authorDefaults, string.Empty, string.Empty);
        }

        string raceName = liveRace.EditorID ?? authorRace.EditorID ?? raceFk.ToString();

        // Name the plugin winning the race when an override (not the defining plugin) supplies
        // it — the LOTD-style third-party case; for a merged-away race edit the winner IS the
        // defining master and the header's explanation carries the story instead.
        string winnerNote = string.Empty;
        var raceWinner = linkCache.ResolveAllContexts<IRace, IRaceGetter>(raceFk).FirstOrDefault()?.ModKey;
        if (raceWinner != null && raceWinner.Value != raceFk.ModKey)
        {
            winnerNote = $" (race record winner: {raceWinner.Value.FileName})";
        }

        string detail = $"race '{raceName}' — its default head parts in your load order differ from the ones " +
                        $"the selected mod's face was built against{winnerNote}.";
        string technicalDetail =
            $"race defaults drift: race='{raceName}' ({raceFk}), sex={(female ? "female" : "male")}\n" +
            $"mod-context defaults: {FormatRaceDefaultSet(authorDefaults)}\n" +
            $"load-order defaults: {FormatRaceDefaultSet(liveDefaults)}";
        return new RaceDriftInfo(raceFk, Drifted: true, authorDefaults, detail, technicalDetail);
    }

    /// <summary>
    /// Converts the run's held race-drift probe failures into <see cref="NpcWarningReporter"/>
    /// warnings, each carrying the remedy that fits the RUN, not just the NPC: the per-mod
    /// Record Override Handling Mode exists precisely for race-editing appearance mods, and
    /// which of its modes to recommend depends on every selection sharing the race. If all
    /// selected mods that put NPCs on the race agree about its default head parts, Include
    /// carries the race edit into the output and fixes them together; once two selected mods
    /// disagree (children split between an RS-style overhaul and a mod authored against the
    /// unedited race, on the measuring run), a shared override always breaks one side, so the
    /// drifting mod is steered to IncludeAsNew — its NPCs get their own copy of the race.
    /// Class-gated by construction: nothing here keys on a specific mod's identity. Clears the
    /// findings (per batch); the usage census persists for later batches of a split run.
    /// </summary>
    private void FlushRaceDriftFindings()
    {
        if (_raceDriftFindings.Count == 0) return;

        foreach (var finding in _raceDriftFindings)
        {
            string advice;
            string census = string.Empty;
            if (_raceDriftUsage.TryGetValue(finding.RaceFormKey, out var byMod) &&
                byMod.TryGetValue(finding.ModName, out var ownVersion))
            {
                var disagreeing = ModsWithDifferentRaceVersion(byMod, finding.ModName);
                advice = disagreeing.Count == 0
                    ? $"Fix: in the Mods menu, set the Record Override Handling Mode of '{finding.ModName}' to " +
                      "Include — no other selected appearance mod uses a different version of this race, so " +
                      "carrying the race edit into the output is safe."
                    : $"Fix: in the Mods menu, set the Record Override Handling Mode of '{finding.ModName}' to " +
                      $"IncludeAsNew. Plain Include would break the faces of NPCs from {Summarize(disagreeing)}, " +
                      "which use a different version of this race; IncludeAsNew gives this mod's NPCs their own " +
                      "copy of the race instead.";

                census = "race referenced by selected mods: " + string.Join(", ", byMod
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => $"'{kv.Key}' ({(kv.Value.SetEquals(ownVersion) ? "same" : "different")} race version)"));
            }
            else
            {
                // Defensive: no census entry (should not happen — the finding registered one).
                advice = $"Fix: in the Mods menu, set the Record Override Handling Mode of '{finding.ModName}' to " +
                         "Include or IncludeAsNew so its race edit reaches the game.";
            }

            NpcWarningReporter.Record(NpcWarningKind.RaceDefaultsDrift, finding.NpcIdentifier,
                detail: finding.Detail + " " + advice,
                technicalDetail: finding.TechnicalDetail +
                                 (census.Length > 0 ? "\n" + census : string.Empty));
        }

        _raceDriftFindings.Clear();
    }

    /// <summary>The selected mods whose author-context version of a race disagrees with
    /// <paramref name="modName"/>'s — the mods a plain Include override would break. Empty means
    /// every selection sharing the race agrees, and Include is safe. Keyed on version CONTENT,
    /// not mod count: two RS-family mods that agree about the race still get the simpler Include
    /// advice. Pure and internal for tests.</summary>
    internal static List<string> ModsWithDifferentRaceVersion(
        IReadOnlyDictionary<string, HashSet<FormKey>> raceUsageByMod, string modName)
    {
        if (!raceUsageByMod.TryGetValue(modName, out var ownVersion)) return new List<string>();

        return raceUsageByMod
            .Where(kv => !kv.Key.Equals(modName, StringComparison.OrdinalIgnoreCase) &&
                         !kv.Value.SetEquals(ownVersion))
            .Select(kv => kv.Key)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"'A'", "'A' and 'B'", or "'A', 'B' and 2 more" — keeps the advice sentence flat
    /// when many mods share the race.</summary>
    private static string Summarize(IReadOnlyList<string> mods)
    {
        const int max = 2;
        var shown = mods.Take(max).Select(m => $"'{m}'").ToList();
        if (mods.Count <= max)
        {
            return string.Join(" and ", shown);
        }
        return string.Join(", ", shown) + $" and {mods.Count - max} more";
    }

    /// <summary>The RACE's chargen default head part FormKeys for one sex — the parts the engine
    /// falls back to for head slots an NPC record leaves unset, and therefore the set whose drift
    /// between resolution contexts invalidates a baked FaceGen mesh. Null-tolerant at every
    /// level; an absent HeadData reads as an empty set.</summary>
    internal static HashSet<FormKey> RaceDefaultHeadParts(IRaceGetter race, bool female)
    {
        var result = new HashSet<FormKey>();
        var headData = female ? race.HeadData?.Female : race.HeadData?.Male;
        if (headData?.HeadParts == null) return result;

        foreach (var hpRef in headData.HeadParts)
        {
            if (!hpRef.Head.IsNull) result.Add(hpRef.Head.FormKey);
        }
        return result;
    }

    /// <summary>One line of a race's default head parts for the drift warning's technical block,
    /// deterministic order, EditorIDs added where the live load order can name them (a merged-away
    /// mod's parts resolve nowhere live and print as bare FormKeys — the probe evidence below the
    /// block names the missing parts with EditorIDs anyway).</summary>
    private string FormatRaceDefaultSet(IReadOnlyCollection<FormKey> parts)
    {
        if (parts.Count == 0) return "(none)";
        var linkCache = _environmentStateProvider.LinkCache;
        return string.Join(", ", parts
            .OrderBy(p => p.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(p => linkCache != null && linkCache.TryResolve<IHeadPartGetter>(p, out var hp) &&
                         !string.IsNullOrEmpty(hp?.EditorID)
                ? $"'{hp!.EditorID}' ({p})"
                : p.ToString()));
    }

    /// <summary>Winner-side twin of <see cref="EvaluateNifCompatibilityAsync"/>: materializes the
    /// load-order-winning copy of the mesh — extracting when the winner is BSA-resident — and
    /// grades it against the record. The loose-only path it replaces returned NotEvaluated for
    /// BSA-packed winners, which <c>?? true</c> then accepted unprobed (seam fixed 2026-07-30).</summary>
    private async Task<(bool? Compatible, string? Mismatch)> EvaluateWinningNifCompatibilityAsync(
        INpcGetter recordToMatch, string nifRelPath)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "NPC2_FaceGenCompat");
        string? materialized = null;
        try
        {
            materialized = await _assetHandler.MaterializeWinningAssetAsync(nifRelPath, tempDir);
            return EvaluateNifCompatibility(recordToMatch, materialized);
        }
        finally
        {
            // Only delete what we extracted; a loose winner path is the deployed file itself.
            if (materialized != null && materialized.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(materialized); } catch { /* temp cleanup is best-effort */ }
            }
        }
    }

    private async Task<(bool? Compatible, string? Mismatch)> EvaluateNifCompatibilityAsync(
        INpcGetter recordToMatch, string nifRelPath, ModSetting sourceMod)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "NPC2_FaceGenCompat");
        string? materialized = null;
        try
        {
            materialized = await _assetHandler.MaterializeAssetAsync(nifRelPath, sourceMod, tempDir);
            return EvaluateNifCompatibility(recordToMatch, materialized);
        }
        finally
        {
            // Only delete what we extracted; a loose source path is the mod's own file.
            if (materialized != null && materialized.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(materialized); } catch { /* temp cleanup is best-effort */ }
            }
        }
    }

    /// <summary>Race-drift twin of <see cref="EvaluateNifCompatibilityAsync"/>: same mesh
    /// materialization, but the record's head parts resolve through the selected mod's plugins
    /// first (live load order as fallback) so the donor's own custom parts read as the shipped
    /// output will — see the <c>headPartResolver</c> remarks on
    /// <see cref="EvaluateNifCompatibility"/>. The race still resolves live.</summary>
    private async Task<(bool? Compatible, string? Mismatch)> EvaluateDriftNifCompatibilityAsync(
        INpcGetter recordToMatch, string nifRelPath, ModSetting sourceMod, HashSet<string> currentModFolderPaths)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "NPC2_FaceGenCompat");
        string? materialized = null;
        try
        {
            materialized = await _assetHandler.MaterializeAssetAsync(nifRelPath, sourceMod, tempDir);
            return EvaluateNifCompatibility(recordToMatch, materialized,
                fk => ResolveHeadPartPreferringMod(fk, sourceMod, currentModFolderPaths));
        }
        finally
        {
            // Only delete what we extracted; a loose source path is the mod's own file.
            if (materialized != null && materialized.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(materialized); } catch { /* temp cleanup is best-effort */ }
            }
        }
    }

    /// <summary>A head part as the selected mod's author saw it: the mod's own plugins first
    /// (lowest in the list wins, like the donor), the live load order as fallback. Unlike the
    /// NPC/race resolvers, resource-only plugins are INCLUDED — head parts legitimately live in
    /// resource masters (High Poly Head.esm, RSkyrimChildren.esm), which is the very reason those
    /// plugins get marked resource-only.</summary>
    private IHeadPartGetter? ResolveHeadPartPreferringMod(FormKey headPartFormKey, ModSetting sourceMod,
        HashSet<string> currentModFolderPaths)
    {
        var link = headPartFormKey.ToLink<IHeadPartGetter>();
        for (int i = sourceMod.CorrespondingModKeys.Count - 1; i >= 0; i--)
        {
            if (_recordHandler.TryGetRecordGetterFromMod(link, sourceMod.CorrespondingModKeys[i],
                    currentModFolderPaths, RecordHandler.RecordLookupFallBack.None, out var record) &&
                record is IHeadPartGetter modHeadPart)
            {
                return modHeadPart;
            }
        }

        var linkCache = _environmentStateProvider.LinkCache;
        return linkCache != null && linkCache.TryResolve<IHeadPartGetter>(headPartFormKey, out var live)
            ? live
            : null;
    }

    /// <summary>
    /// Does this mesh have a baked shape for every geometry-bearing head part the record resolves
    /// to? That reconciliation is what the engine performs when it applies the face tint, and its
    /// failure is the dark-face bug — so a borrowed mesh that fails it must not be used.
    /// Null means "could not tell", which the ladder treats optimistically. On a mismatch,
    /// <c>Mismatch</c> carries the evidence (record-needs vs mesh-bakes) for the detailed warning
    /// log; null otherwise.
    /// </summary>
    /// <param name="headPartResolver">Overrides how the record's head parts resolve; null means
    /// the live load order. The race-drift probe passes a mod-first resolver here — the graded
    /// record is the donor's pre-merge version, whose own custom parts only resolve inside the
    /// mod (the output remaps them, and the mesh bakes their shapes), so live-only resolution
    /// misread every such part as a broken link and failed the probe on plumbing rather than on
    /// the race drift under test. The RACE always resolves live: for the drift probe that is
    /// precisely the question being asked.</param>
    private (bool? Compatible, string? Mismatch) EvaluateNifCompatibility(INpcGetter recordToMatch, string? nifPath,
        Func<FormKey, IHeadPartGetter?>? headPartResolver = null)
    {
        if (string.IsNullOrEmpty(nifPath) || !File.Exists(nifPath)) return (null, null);

        var linkCache = _environmentStateProvider.LinkCache;
        if (linkCache == null) return (null, null);

        try
        {
            var analyzer = _faceGenConsistency.Value;
            if (analyzer == null) return (null, null);

            var analysis = analyzer.Analyze(
                recordToMatch,
                headPartResolver ?? (fk => linkCache.TryResolve<IHeadPartGetter>(fk, out var hp) ? hp : null),
                fk => linkCache.TryResolve<IRaceGetter>(fk, out var r) ? r : null,
                nifPath);
            return analysis.HasMismatch
                ? (false, DescribeMismatch(analysis, WinningPluginTag))
                : (true, null);
        }
        catch
        {
            return (null, null); // a malformed NIF must not abort a patch run
        }

        // Names the plugin whose override currently WINS a head part, when that is not the
        // plugin that defined it — the "which plugin is overwriting" half of the evidence.
        string? WinningPluginTag(FormKey fk)
        {
            try
            {
                var winner = linkCache.ResolveAllContexts<IHeadPart, IHeadPartGetter>(fk)
                    .FirstOrDefault()?.ModKey;
                return winner != null && winner.Value != fk.ModKey
                    ? winner.Value.FileName.ToString()
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>The evidence half of a failed probe, for the detailed warning log: which head
    /// parts the record resolves that the mesh has no baked shape for (with the winning plugin
    /// named when an override, not the defining plugin, currently supplies the part), which baked
    /// shapes the mesh carries that match no resolved part, and any record links that resolve
    /// nowhere. Pure over the analysis; <paramref name="winningPluginName"/> supplies the
    /// override-aware tag (null tags nothing).</summary>
    private static string DescribeMismatch(
        FaceGenConsistencyAnalyzer.Result analysis, Func<FormKey, string?> winningPluginName)
    {
        var parts = new List<string>();

        if (analysis.MissingBakedShapes.Count > 0)
        {
            parts.Add("record needs (no baked shape in mesh): " + string.Join(", ",
                analysis.MissingBakedShapes.Select(m =>
                {
                    string? winner = winningPluginName(m.FormKey);
                    return $"'{m.EditorId}' ({m.FormKey}{(winner != null ? $", winning override: {winner}" : string.Empty)})";
                })));
        }

        if (analysis.OrphanBakedShapes.Count > 0)
        {
            parts.Add("mesh bakes (no matching head part): " + string.Join(", ", analysis.OrphanBakedShapes));
        }

        if (analysis.UnresolvedHeadParts.Count > 0)
        {
            parts.Add("record links that resolve nowhere: " + string.Join(", ", analysis.UnresolvedHeadParts));
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// The shared context block above the per-probe evidence in the detailed warning log: which
    /// record was graded, and the load-order override chains of the NPC record and its race —
    /// naming exactly which plugins rewrite the head data out from under an origin-authored
    /// pairing (RS Children overriding a child race is the measured case). Winner first.
    /// </summary>
    private string ComposeCompatProbeContext(
        INpcGetter recordToMatch, FormKey subjectFormKey,
        IReadOnlyList<(string Label, string? Mismatch)> failedProbes)
    {
        var sb = new StringBuilder();
        sb.Append($"graded record: '{recordToMatch.EditorID}' ({recordToMatch.FormKey})");

        var npcChain = DescribeOverrideChain<INpc, INpcGetter>(subjectFormKey);
        if (npcChain != null) sb.Append($"\nNPC record supplied by: {npcChain}");

        if (!recordToMatch.Race.IsNull)
        {
            var linkCache = _environmentStateProvider.LinkCache;
            string raceName = linkCache != null &&
                              linkCache.TryResolve<IRaceGetter>(recordToMatch.Race.FormKey, out var race)
                ? race.EditorID ?? "(no EditorID)"
                : "(unresolved)";
            sb.Append($"\nrace: '{raceName}' ({recordToMatch.Race.FormKey})");
            var raceChain = DescribeOverrideChain<IRace, IRaceGetter>(recordToMatch.Race.FormKey);
            if (raceChain != null) sb.Append($"\nrace record supplied by: {raceChain}");
        }

        foreach (var (label, mismatch) in failedProbes)
        {
            if (string.IsNullOrWhiteSpace(mismatch)) continue;
            sb.Append($"\n{label}:");
            foreach (var line in mismatch.Split('\n'))
            {
                sb.Append($"\n  {line.TrimEnd('\r')}");
            }
        }

        return sb.ToString();
    }

    /// <summary>Every plugin carrying a version of the record, winner first — "who is overwriting
    /// whom" in one line. Null when the record resolves nowhere or the cache is unavailable.</summary>
    private string? DescribeOverrideChain<TMajor, TMajorGetter>(FormKey fk)
        where TMajor : class, IMajorRecordQueryable, TMajorGetter
        where TMajorGetter : class, IMajorRecordQueryableGetter
    {
        try
        {
            var linkCache = _environmentStateProvider.LinkCache;
            if (linkCache == null) return null;

            var keys = linkCache.ResolveAllContexts<TMajor, TMajorGetter>(fk)
                .Select(c => c.ModKey.FileName.ToString())
                .ToList();
            if (keys.Count == 0) return null;

            if (keys.Count > 1) keys[0] += " (winner)";
            keys[^1] += " (origin)";
            return string.Join(", ", keys);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Mirrors the donor's appearance inheritance onto the patched record. The Traits flag and the
    /// TPLT link are ONE unit: an NPC with Traits set and a null Template has no face to inherit,
    /// and its own head parts/FaceGen are then inconsistent with everything. Setting the flag
    /// without the link produced exactly that (e.g. Redguard Woman 0B85AB, whose donor inherits
    /// from TreasCorpseCommonerRedguardFemale 048117) — so whenever the donor inherits, the output
    /// must point at the same template, and it must be re-pointed even when the flag itself does
    /// not change (donor and recipient can both inherit, from DIFFERENT templates).
    ///
    /// <para>The link is deliberately NOT cleared when the flag is cleared: TPLT also drives
    /// non-appearance inheritance (inventory, AI packages, factions...) whose flags this app does
    /// not touch, so dropping it would break unrelated behaviour.</para>
    /// </summary>
    private void SyncTemplateInheritance(Npc targetNpc, INpcGetter sourceNpc)
    {
        if (ShouldChangeTraitsStatus(targetNpc, sourceNpc, out bool hasTraitsStatus))
        {
            SetTraitsFlag(targetNpc, hasTraitsStatus);
        }

        if (!Auxilliary.HasTraitsFlag(sourceNpc)) return;

        if (sourceNpc.Template.IsNull)
        {
            // Donor is itself malformed (Traits with no template). Nothing to copy; say so rather
            // than silently leaving whatever link the recipient had.
            AppendLog($"      WARNING: appearance source {sourceNpc.FormKey} has the Traits flag but no template record, so no template could be applied to {targetNpc.FormKey}.");
            return;
        }

        if (targetNpc.Template.FormKey != sourceNpc.Template.FormKey)
        {
            targetNpc.Template.SetTo(sourceNpc.Template.FormKey);
            AppendLog($"      Set template of {targetNpc.FormKey} to {sourceNpc.Template.FormKey} (its appearance is inherited from that NPC).");
        }
    }

    private void SetTraitsFlag(Npc targetNpc, bool hasTraits)
    {
        if (hasTraits)
        {
            // Set Traits bit
            targetNpc.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
        }
        else
        {
            // Clear Traits bit
            targetNpc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Traits;
        }
    }

    private bool ShouldChangeTraitsStatus(INpcGetter targetNpc, INpcGetter appearanceNpc, out bool hasTraits)
    {
        hasTraits = false;

        bool targetHasTraits = Auxilliary.HasTraitsFlag(targetNpc);
        bool apparanceHasTraits = Auxilliary.HasTraitsFlag(appearanceNpc);

        if (apparanceHasTraits && !targetHasTraits)
        {
            hasTraits = true;
            return true;
        }

        if (!apparanceHasTraits && targetHasTraits)
        {
            hasTraits = false;
            return true;
        }
        
        return false;
    }

    private IKeywordGetter GetoOrCreateKeyword(string keyword)
    {
        if (_generatedKeywords.TryGetValue(keyword, out var keywordGetter) && keywordGetter != null)
        {
            return keywordGetter;
        }
        
        //var newKeyword = new Keyword(_environmentStateProvider.OutputMod, keyword);
        var newKeyword = _environmentStateProvider.OutputMod.Keywords.AddNew(keyword);
        newKeyword.EditorID = keyword;
        RecordProvenanceDiag.RecordGenerated(newKeyword.FormKey, keyword, "Keyword");
        _generatedKeywords.Add(keyword, newKeyword);
        return newKeyword;
    }
    
    // Generates all used keywords in the first pass to ensure that in a split mod, the first generated plugin gets
    // all of the keywords.
    private void GenerateKeywords()
    {
        var keywordStrings = _settings
            .ModSettings
            .SelectMany(x => x.Keywords)
            .Distinct()
            .ToHashSet();

        foreach (var k in keywordStrings)
        {
            GetoOrCreateKeyword(k);
        }
    }
    
    private void ApplyKeywords(Npc patchNpc, IEnumerable<string> keywords)
    {
        if (_settings.UseSkyPatcherMode)
        {
            _skyPatcherInterface.ApplyKeywords(patchNpc.FormKey, keywords);
        }
        else
        {
            foreach (var kw in keywords)
            {
                var keyword = GetoOrCreateKeyword(kw);
                if (patchNpc.Keywords == null)
                {
                    patchNpc.Keywords = new();
                }

                if (!patchNpc.Keywords.Contains(keyword))
                {
                    patchNpc.Keywords.Add(keyword);
                }
            }
        }
    }
}