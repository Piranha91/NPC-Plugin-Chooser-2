// VM_Run.cs (Revised based on clarifications)
using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.IO;
using Mutagen.Bethesda;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Reactive.Disposables;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins.Cache;
using Noggog; // Needed for LinkCache Interface

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_Run : ReactiveObject
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly Auxilliary _auxilliary;
        private readonly VM_Settings _vmSettings;
        private readonly CompositeDisposable _disposables = new();

        // --- Constants ---
        private const string ALL_NPCS_GROUP = "<All NPCs>";
        // Define Base Game Plugins (matching V1) - used for DuplicateFromOnlyReferenced exclusion
        private static readonly HashSet<ModKey> BaseGamePlugins = new()
        {
            ModKey.FromNameAndExtension("Skyrim.esm"),
            ModKey.FromNameAndExtension("Update.esm"),
            ModKey.FromNameAndExtension("Dawnguard.esm"),
            ModKey.FromNameAndExtension("HearthFires.esm"),
            ModKey.FromNameAndExtension("Dragonborn.esm")
            // Add USSEP potentially? V1 didn't, so let's stick to that for now.
        };


        // --- Logging & State ---
        [Reactive] public string LogOutput { get; private set; } = string.Empty;
        [Reactive] public bool IsRunning { get; private set; }
        [Reactive] public double ProgressValue { get; private set; } = 0;
        [Reactive] public string ProgressText { get; private set; } = string.Empty;


        // --- Group Filtering ---
        public ObservableCollection<string> AvailableNpcGroups { get; } = new();
        [Reactive] public string SelectedNpcGroup { get; set; } = ALL_NPCS_GROUP;


        // --- Configuration (Mirrored from V1 for backend use) ---
        // These could be exposed in Settings View later if desired, or kept internal
        private bool ClearOutputDirectoryOnRun => true; // Example: default to true
        private bool AbortIfMissingFaceGen => true; // Example: default to true
        private bool AbortIfMissingExtraAssets => false; // Example: default to false
        private bool GetMissingExtraAssetsFromAvailableWinners => false; // Example: default to false
        private bool SuppressAllMissingFileWarnings => false; // Example: default to false
        private bool SuppressKnownMissingFileWarnings => true; // Example: default to true
        private bool HandleBSAFiles_Patching => true; // Example: default to true
        private bool CopyExtraAssets => true; // Example: default to true (common request)
        private bool FindExtraTexturesInNifs => true; // Example: default to true


        // --- Internal Data for Patching Run ---
        private HashSet<string> _pathsToIgnore = new();
        private Dictionary<string, HashSet<string>> _warningsToSuppress = new(); // Key: Plugin Name (lowercase), Value: Set of paths
        private HashSet<string> _warningsToSuppress_Global = new();
        private Dictionary<string, ModSetting> _modSettingsMap = new(); // Key: DisplayName, Value: ModSetting
        private string _currentRunOutputAssetPath = string.Empty;
        private HashSet<ModKey> _pluginsUsedForAppearance = new(); // Track plugins needing dependency duplication


        public ReactiveCommand<Unit, Unit> RunCommand { get; }

        public VM_Run(
            EnvironmentStateProvider environmentStateProvider,
            Settings settings,
            NpcConsistencyProvider consistencyProvider,
            Auxilliary auxilliary,
            VM_Settings vmSettings)
        {
            _environmentStateProvider = environmentStateProvider;
            _settings = settings;
            _consistencyProvider = consistencyProvider;
            _auxilliary = auxilliary;
            _vmSettings = vmSettings;

            // Command can only execute if environment is valid and not already running
            var canExecute = this.WhenAnyValue(x => x.IsRunning, x => x._environmentStateProvider.EnvironmentIsValid,
                                               (running, valid) => !running && valid);

            RunCommand = ReactiveCommand.CreateFromTask(RunPatchingLogic, canExecute);

            // Update IsRunning status when command executes/completes
            RunCommand.IsExecuting.BindTo(this, x => x.IsRunning);

            // Log exceptions from the command
            RunCommand.ThrownExceptions.Subscribe(ex =>
            {
                AppendLog($"ERROR: {ex.GetType().Name} - {ex.Message}");
                AppendLog(ExceptionLogger.GetExceptionStack(ex)); // Use ExceptionLogger
                AppendLog("Patching failed.");
                ResetProgress();
            });

            // Update Available Groups when NpcGroupAssignments changes in settings
            UpdateAvailableGroups();

            // Subscribe to VM_Settings EnvironmentIsValid changes to refresh groups when env becomes valid
             _vmSettings.WhenAnyValue(x => x.EnvironmentIsValid)
                 .Where(isValid => isValid)
                 .ObserveOn(RxApp.MainThreadScheduler)
                 .Subscribe(_ => UpdateAvailableGroups());
             
             // Subscribe to group change messages
             MessageBus.Current.Listen<NpcGroupsChangedMessage>()
                 .ObserveOn(RxApp.MainThreadScheduler) // Ensure update happens on UI thread
                 .Subscribe(_ =>
                 {
                     AppendLog("NPC Groups potentially changed. Refreshing dropdown...");
                     UpdateAvailableGroups();
                 })
                 .DisposeWith(_disposables); // Add subscription to disposables
        }


        private void UpdateAvailableGroups()
        {
             // (Implementation remains the same as before)
             RxApp.MainThreadScheduler.Schedule(() =>
             {
                 string currentSelection = SelectedNpcGroup;
                 AvailableNpcGroups.Clear();
                 AvailableNpcGroups.Add(ALL_NPCS_GROUP);

                 if (_settings.NpcGroupAssignments != null)
                 {
                     var distinctGroups = _settings.NpcGroupAssignments.Values
                                             .SelectMany(set => set ?? Enumerable.Empty<string>())
                                             .Where(g => !string.IsNullOrWhiteSpace(g))
                                             .Select(g => g.Trim())
                                             .Distinct(StringComparer.OrdinalIgnoreCase)
                                             .OrderBy(g => g);

                     foreach (var group in distinctGroups)
                     {
                         AvailableNpcGroups.Add(group);
                     }
                 }

                 if (AvailableNpcGroups.Contains(currentSelection))
                 {
                     SelectedNpcGroup = currentSelection;
                 }
                 else
                 {
                     SelectedNpcGroup = ALL_NPCS_GROUP;
                 }
             });
        }

        private void ResetProgress() {
            ProgressValue = 0;
            ProgressText = string.Empty;
        }

        private void UpdateProgress(int current, int total, string message)
        {
             RxApp.MainThreadScheduler.Schedule(() =>
             {
                  if (total > 0) {
                      ProgressValue = (double)current / total * 100.0;
                      ProgressText = $"[{current}/{total}] {message}";
                  } else {
                      ProgressValue = 0;
                      ProgressText = message;
                  }
             });
        }

        private bool LoadAuxiliaryFiles()
        {
            AppendLog("Loading auxiliary configuration files...");
            bool success = true;
            // **Correction 3:** Use Resources path
            string resourcesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");

            // Load Ignored Paths
            _pathsToIgnore.Clear();
            string ignorePathFile = Path.Combine(resourcesPath, "Paths To Ignore.json"); // Corrected path
            if (File.Exists(ignorePathFile))
            {
                try
                {
                    var tempList = JSONhandler<List<string>>.LoadJSONFile(ignorePathFile, out bool loadSuccess, out string loadEx);
                    if (loadSuccess && tempList != null)
                    {
                         _pathsToIgnore = new HashSet<string>(tempList.Select(p => p.Replace(@"\\", @"\")), StringComparer.OrdinalIgnoreCase);
                         AppendLog($"Loaded {_pathsToIgnore.Count} paths to ignore.");
                    }
                    else { throw new Exception(loadEx); }
                }
                catch (Exception ex)
                {
                     // **Correction 4:** Use ExceptionLogger
                     AppendLog($"WARNING: Could not load or parse '{ignorePathFile}'. No paths will be ignored. Error: {ExceptionLogger.GetExceptionStack(ex)}");
                }
            } else {
                AppendLog($"INFO: Ignore paths file not found at '{ignorePathFile}'. No paths will be ignored.");
            }


            // Load Suppressed Warnings
            _warningsToSuppress.Clear();
            _warningsToSuppress_Global.Clear();
            string suppressWarningsFile = Path.Combine(resourcesPath, "Warnings To Suppress.json"); // Corrected path
            if (File.Exists(suppressWarningsFile))
            {
                 try
                 {
                     var tempList = JSONhandler<List<suppressedWarnings>>.LoadJSONFile(suppressWarningsFile, out bool loadSuccess, out string loadEx);
                     if (loadSuccess && tempList != null)
                     {
                         foreach(var sw in tempList)
                         {
                             var cleanedPaths = new HashSet<string>(sw.Paths.Select(p => p.Replace(@"\\", @"\")), StringComparer.OrdinalIgnoreCase);
                             string pluginKeyLower = sw.Plugin.ToLowerInvariant();

                             if(pluginKeyLower == "global")
                             {
                                 _warningsToSuppress_Global = cleanedPaths;
                             }
                             else
                             {
                                 _warningsToSuppress[pluginKeyLower] = cleanedPaths;
                             }
                         }
                         AppendLog($"Loaded suppressed warnings for {_warningsToSuppress.Count} specific plugins and global scope.");
                     }
                     else { throw new Exception(loadEx); }
                 }
                 catch (Exception ex)
                 {
                      // **Correction 4:** Use ExceptionLogger
                      AppendLog($"ERROR: Could not load or parse '{suppressWarningsFile}'. Suppressed warnings will not be used. Error: {ExceptionLogger.GetExceptionStack(ex)}");
                      success = false;
                 }
            } else {
                 AppendLog($"ERROR: Suppressed warnings file not found at '{suppressWarningsFile}'. Cannot proceed.");
                 success = false;
            }

            return success;
        }

        private bool ShouldSkipNpc(INpcGetter npc, string selectedGroup)
        {
             // (Implementation remains the same as before)
            if (selectedGroup == ALL_NPCS_GROUP) return false;

            if (_settings.NpcGroupAssignments != null &&
                _settings.NpcGroupAssignments.TryGetValue(npc.FormKey, out var assignedGroups) &&
                assignedGroups != null &&
                assignedGroups.Contains(selectedGroup, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }


        private async Task RunPatchingLogic()
        {
            LogOutput = string.Empty;
            ResetProgress();
            _pluginsUsedForAppearance.Clear(); // Clear for new run
            UpdateProgress(0, 1, "Initializing...");
            AppendLog("Starting patch generation...");

            // --- Pre-Run Checks ---
             if (!_environmentStateProvider.EnvironmentIsValid || _environmentStateProvider.LoadOrder == null)
             { AppendLog("Environment is not valid..."); ResetProgress(); return; }
             if (_settings.ModSettings == null || !_settings.ModSettings.Any())
             { AppendLog("No Mod Settings configured..."); ResetProgress(); return; }
             if (string.IsNullOrWhiteSpace(_settings.OutputDirectory))
             { AppendLog("Output Directory is not set..."); ResetProgress(); return; }

            // --- Load Auxiliary Config Files ---
            if (!LoadAuxiliaryFiles())
            { AppendLog("Failed to load auxiliary files..."); ResetProgress(); return; }

            // --- Prepare Output Paths ---
            // check if _settings.OutputDirectory is a valid directory path. If yes, assume the user wants output in that specific directory
            // If not, assume it's a folder name and place it in the mods folder
            string baseOutputDirectory;
            bool isSpecifiedDirectory = false;
            var testSplit = _settings.OutputDirectory.Split(Path.DirectorySeparatorChar);
            if (testSplit.Length > 1 && Directory.Exists(_settings.OutputDirectory))
            {
                baseOutputDirectory = _settings.OutputDirectory;
                isSpecifiedDirectory = true;
            }
            else if (testSplit.Length == 1)
            {
                baseOutputDirectory = Path.Combine(_settings.ModsFolder, _settings.OutputDirectory);
            }
            else
            {
                AppendLog("Error: Could not locate directory " + _settings.OutputDirectory); ResetProgress(); return;
            }
            
            _currentRunOutputAssetPath = baseOutputDirectory;
            if (_settings.AppendTimestampToOutputDirectory)
            {
                if (isSpecifiedDirectory)
                {
                    AppendLog(
                        "Warning: Timestamp will not be appended to output directory because the path is fully specified. " +
                        "If you want to add a timestamp, only provide the name of your desired output folder.");
                }
                else
                {
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    _currentRunOutputAssetPath = Path.Combine(baseOutputDirectory, timestamp);
                    AppendLog($"Using timestamped output asset directory: {_currentRunOutputAssetPath}");
                }
            } 
            else { AppendLog($"Using output asset directory: {_currentRunOutputAssetPath}"); }
            
            try { Directory.CreateDirectory(_currentRunOutputAssetPath); AppendLog("Ensured output asset directory exists."); }
            catch (Exception ex)
            {
                 AppendLog($"ERROR: Could not create output asset directory... Aborting. Error: {ExceptionLogger.GetExceptionStack(ex)}"); ResetProgress(); return;
            }

            // --- Initialize Output Mod ---
            _environmentStateProvider.OutputMod = new SkyrimMod(ModKey.FromName(_environmentStateProvider.OutputPluginName, ModType.Plugin), _environmentStateProvider.SkyrimVersion);
            AppendLog($"Initialized output mod: {_environmentStateProvider.OutputPluginName}");

            // --- Clear Output Asset Directory ---
            if (ClearOutputDirectoryOnRun)
            {
                 AppendLog("Clearing output asset directory...");
                 try { ClearDirectory(_currentRunOutputAssetPath); AppendLog("Output asset directory cleared."); }
                 catch (Exception ex)
                 { AppendLog($"ERROR: Failed to clear output asset directory: {ExceptionLogger.GetExceptionStack(ex)}. Aborting."); ResetProgress(); return; }
            }

            // --- Build Mod Settings Map ---
            _modSettingsMap = _settings.ModSettings
                .Where(ms => !string.IsNullOrWhiteSpace(ms.DisplayName))
                .GroupBy(ms => ms.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            AppendLog($"Built lookup map for {_modSettingsMap.Count} unique Mod Settings.");

            // --- Main Processing Loop ---
            AppendLog("\nProcessing NPC Appearance Selections...");
            int processedCount = 0;
            int skippedCount = 0;

            var selections = _settings.SelectedAppearanceMods;
            if (selections == null || !selections.Any())
            { AppendLog("No NPC appearance selections have been made..."); }
            else
            {
                int totalSelectedNpcs = selections.Count;
                var npcsToProcess = selections.Keys.ToList();

                for(int i = 0; i < npcsToProcess.Count; i++)
                {
                    var npcFormKey = npcsToProcess[i];
                    var selectedModDisplayName = selections[npcFormKey];
                    string npcIdentifier = npcFormKey.ToString();

                    // Resolve NPC and Filter
                    if (!_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey, out var winningNpcOverride))
                    { AppendLog($"  WARNING: Could not resolve NPC {npcFormKey}... Skipping."); skippedCount++; UpdateProgress(i + 1, totalSelectedNpcs, $"Skipped {npcIdentifier}"); await Task.Delay(1); continue; }
                    npcIdentifier = $"{winningNpcOverride.Name?.String ?? winningNpcOverride.EditorID ?? npcFormKey.ToString()} ({npcFormKey})";
                    if (ShouldSkipNpc(winningNpcOverride, SelectedNpcGroup))
                    { AppendLog($"  Skipping {npcIdentifier} (Group Filter)..."); skippedCount++; UpdateProgress(i + 1, totalSelectedNpcs, $"Skipped {npcIdentifier} (Group Filter)"); await Task.Delay(1); continue; }

                    UpdateProgress(i + 1, totalSelectedNpcs, $"Processing {winningNpcOverride.EditorID ?? npcFormKey.ToString()}");
                    AppendLog($"- Processing: {npcIdentifier} -> Selected Mod: '{selectedModDisplayName}'");

                    // Lookup ModSetting
                    if (!_modSettingsMap.TryGetValue(selectedModDisplayName, out var appearanceModSetting))
                    { AppendLog($"  ERROR: Could not find Mod Setting '{selectedModDisplayName}'... Skipping."); skippedCount++; UpdateProgress(i + 1, totalSelectedNpcs, $"ERROR: ModSetting {selectedModDisplayName} not found"); await Task.Delay(1); continue; }

                    // Get Appearance Plugin Key
                    ModKey? appearancePluginKeyNullable = appearanceModSetting.ModKey; // Renamed for clarity
                    if (!appearancePluginKeyNullable.HasValue || appearancePluginKeyNullable.Value.IsNull)
                    { AppendLog($"  ERROR: Mod Setting '{selectedModDisplayName}' has no Plugin assigned... Skipping."); skippedCount++; UpdateProgress(i + 1, totalSelectedNpcs, $"ERROR: ModSetting {selectedModDisplayName} no plugin"); await Task.Delay(1); continue; }
                    ModKey appearancePluginKey = appearancePluginKeyNullable.Value; // Use non-nullable from here

                    // Resolve Source NPC
                    /* I think this is the correct algorithm
                    var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcFormKey);
                    var selectedModContext = contexts.FirstOrDefault(x => x.ModKey.Equals(appearancePluginKey));
                    if (selectedModContext == null)
                    {
                        AppendLog($"  WARNING: Could not find NPC record in '{appearancePluginKey.FileName}'... Skipping record patching."); 
                        skippedCount++; UpdateProgress(i + 1, totalSelectedNpcs, $"ERROR: NPC not in {appearancePluginKey.FileName}"); 
                        await Task.Delay(1); continue;
                    }
 
                    var sourceNpc = selectedModContext.Record;
                    */
                    
                    // Resolve Source NPC: Temporarily going with Gemini app
                    // Construct the specific FormKey for the override within the appearance plugin
                    FormKey targetOverrideKey = new FormKey(appearancePluginKey, npcFormKey.ID);

                    // Try to resolve the context (and thus the record) using this specific override key
                    if (!_environmentStateProvider.LinkCache.TryResolveContext<INpc, INpcGetter>(
                            targetOverrideKey, out var sourceNpcContext) || sourceNpcContext?.Record == null) // Added null check on context too
                    {
                        // Log that we couldn't find the specific override record in the expected plugin
                        AppendLog($"  WARNING: Could not find NPC record override ({targetOverrideKey}) in the selected appearance plugin '{appearancePluginKey.FileName}'. This plugin might not actually override this NPC's appearance record. Skipping record patching.");
                        skippedCount++;
                        UpdateProgress(i + 1, totalSelectedNpcs, $"ERROR: NPC not in {appearancePluginKey.FileName}");
                        await Task.Delay(1);
                        continue;
                    }

                    // If successful, sourceNpcContext.Record now holds the INpcGetter from the appearance plugin
                    var sourceNpc = sourceNpcContext.Record;
                    
                    // END Gemini approach

                    // --- Apply Patching Mode Logic (Clarification 1 Applied) ---
                    Npc patchNpc;
                    bool isTemplated = NPCisTemplated(sourceNpc);

                    switch (_settings.PatchingMode)
                    {
                        case PatchingMode.EasyNPC_Like: // V1: ForwardConflictWinnerData = true
                            AppendLog($"    Mode: EasyNPC-Like. Patching winning override ({winningNpcOverride.FormKey.ModKey.FileName}).");
                            // Start with the winning override
                            patchNpc = _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(winningNpcOverride);
                            // Copy appearance FROM sourceNpc ONTO patchNpc
                            CopyAppearanceData(sourceNpc, patchNpc);
                            break;

                        case PatchingMode.Default: // V1: ForwardConflictWinnerData = false
                        default:
                            AppendLog($"    Mode: Default. Forwarding record directly from source plugin ({sourceNpc.FormKey.ModKey.FileName}).");
                            // Add the source record directly, overwriting anything else for this FormKey in the patch.
                            // Non-appearance data comes *only* from the source plugin record.
                            patchNpc = _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(sourceNpc);
                            // No merging of non-appearance data from winning override in this mode.
                            break;
                    }

                    // Track the appearance plugin used for dependency duplication later
                    _pluginsUsedForAppearance.Add(appearancePluginKey);

                    // --- Copy Assets (Clarification 2 Applied within helper) ---
                    CopyNpcAssets(patchNpc, sourceNpc, appearancePluginKey, appearanceModSetting, isTemplated);

                    processedCount++;
                    await Task.Delay(5);
                } // End For Loop

                UpdateProgress(totalSelectedNpcs, totalSelectedNpcs, "Finalizing...");

                // --- Post-Processing: Duplicate Dependencies (Clarification 5 Applied) ---
                if (_pluginsUsedForAppearance.Any())
                {
                     AppendLog($"\nDuplicating referenced records from used appearance plugins...");
                     int duplicatedCount = 0;
                     foreach(var pluginKey in _pluginsUsedForAppearance)
                     {
                         if (BaseGamePlugins.Contains(pluginKey))
                         {
                              AppendLog($"  Skipping dependency duplication for base game plugin: {pluginKey.FileName}");
                              continue;
                         }

                         try
                         {
                             AppendLog($"  Processing dependencies for: {pluginKey.FileName}");
                             _environmentStateProvider.OutputMod.DuplicateFromOnlyReferenced(_environmentStateProvider.LinkCache, pluginKey, out var recordsDuplicated);
                              AppendLog($"    Duplicated {recordsDuplicated.Count} records.");
                              duplicatedCount += recordsDuplicated.Count;
                         }
                         catch (Exception ex)
                         {
                              AppendLog($"  ERROR duplicating dependencies for {pluginKey.FileName}: {ExceptionLogger.GetExceptionStack(ex)}");
                              // Continue processing other plugins
                         }
                     }
                     AppendLog($"Finished dependency duplication. Total records duplicated: {duplicatedCount}.");
                }


                // --- Final Steps (Save Output Mod) ---
                if (processedCount > 0 || _pluginsUsedForAppearance.Any()) // Save even if only dependencies were duplicated
                {
                    AppendLog($"\nProcessed {processedCount} NPC(s).");
                    if (skippedCount > 0) AppendLog($"{skippedCount} NPC(s) were skipped.");

                    string outputPluginPath = Path.Combine(_currentRunOutputAssetPath, _environmentStateProvider.OutputPluginName);
                    AppendLog($"Attempting to save output mod to: {outputPluginPath}");
                    try
                    {
                         _environmentStateProvider.OutputMod.WriteToBinary(outputPluginPath);
                         AppendLog($"Output mod saved successfully.");
                    }
                     catch (Exception ex)
                     {
                         AppendLog($"FATAL SAVE ERROR: Could not write output plugin: {ExceptionLogger.GetExceptionStack(ex)}");
                         AppendLog($"Output mod ({_environmentStateProvider.OutputPluginName}) was NOT saved.");
                         ResetProgress();
                         return;
                     }
                }
                else
                {
                    AppendLog("\nNo NPC appearances processed or dependencies duplicated.");
                    if (skippedCount > 0) AppendLog($"{skippedCount} NPC(s) were skipped.");
                    AppendLog("Output mod not saved as no changes were made.");
                }
            }

            AppendLog("\nPatch generation process completed.");
            UpdateProgress(processedCount + skippedCount, processedCount + skippedCount, "Finished.");
        }

        // --- Helper Methods (Partially Revised) ---

        private void ClearDirectory(string path)
        {
            // (Implementation remains the same as before)
            DirectoryInfo di = new DirectoryInfo(path);
            if (!di.Exists) return;

            foreach (FileInfo file in di.EnumerateFiles()) file.Delete();
            foreach (DirectoryInfo dir in di.EnumerateDirectories())
            {
                string dirNameLower = dir.Name.ToLowerInvariant();
                if (dirNameLower == "meshes" || dirNameLower == "textures" || dirNameLower == "facegendata" || dirNameLower == "actors")
                { dir.Delete(true); }
                else { AppendLog($"  Skipping deletion of non-asset directory: {dir.Name}"); }
            }
        }


        /// <summary>
        /// Copies appearance-related fields from sourceNpc to targetNpc.
        /// Used only in EasyNPC-Like mode.
        /// </summary>
        private void CopyAppearanceData(INpcGetter sourceNpc, Npc targetNpc)
        {
            // (Implementation remains the same as before - this copies *appearance* fields)
            targetNpc.FaceMorph = sourceNpc.FaceMorph?.DeepCopy();
            targetNpc.FaceParts = sourceNpc.FaceParts?.DeepCopy();
            if (sourceNpc.HairColor != null && !sourceNpc.HairColor.IsNull) targetNpc.HairColor.SetTo(sourceNpc.HairColor); else targetNpc.HairColor.SetToNull();
            targetNpc.HeadParts.Clear(); targetNpc.HeadParts.AddRange(sourceNpc.HeadParts);
            if (sourceNpc.HeadTexture != null && !sourceNpc.HeadTexture.IsNull) targetNpc.HeadTexture.SetTo(sourceNpc.HeadTexture); else targetNpc.HeadTexture.SetToNull();
            targetNpc.Height = sourceNpc.Height;
            targetNpc.Weight = sourceNpc.Weight;
            targetNpc.TextureLighting = sourceNpc.TextureLighting;
            targetNpc.TintLayers.Clear(); targetNpc.TintLayers.AddRange(sourceNpc.TintLayers?.Select(t => t.DeepCopy()) ?? Enumerable.Empty<TintLayer>());
            if (sourceNpc.WornArmor != null && !sourceNpc.WornArmor.IsNull) targetNpc.WornArmor.SetTo(sourceNpc.WornArmor); else targetNpc.WornArmor.SetToNull();

            AppendLog($"      Copied appearance fields from {sourceNpc.FormKey.ModKey.FileName} to {targetNpc.FormKey} in patch.");
        }

        private bool NPCisTemplated(INpcGetter? npc)
        {
            // (Implementation remains the same as before)
            if (npc == null) return false;
            return !npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits);
        }

        /// <summary>
        /// Main asset copying orchestrator. Calls helpers for identification and copying.
        /// </summary>
        private void CopyNpcAssets(Npc npcInPatch, INpcGetter sourceNpc, ModKey appearancePluginKey, ModSetting appearanceModSetting, bool isTemplated)
        {
            AppendLog($"    Copying assets for {npcInPatch.EditorID ?? npcInPatch.FormKey.ToString()} from sources related to '{appearanceModSetting.DisplayName}'...");

            // Source directories are now just the list from ModSetting
            var assetSourceDirs = appearanceModSetting.CorrespondingFolderPaths ?? new List<string>();
            if (!assetSourceDirs.Any())
            {
                AppendLog($"      WARNING: Mod Setting '{appearanceModSetting.DisplayName}' has no Corresponding Folder Paths. Cannot copy assets.");
                return;
            }
            AppendLog($"      Asset source directories: {string.Join(", ", assetSourceDirs)}");


            // --- Identify Required Assets ---
            HashSet<string> meshesToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> texturesToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. FaceGen Assets
            if (!isTemplated)
            {
                 var (faceMeshPath, faceTexPath) = GetFaceGenSubPathStrings(sourceNpc.FormKey);
                 meshesToCopy.Add(faceMeshPath);
                 texturesToCopy.Add(faceTexPath);
                 AppendLog($"      Identified FaceGen: {faceMeshPath}, {faceTexPath}");

                 // Check FaceGen existence (**Clarification 2 Applied**)
                 if (!FaceGenExists(sourceNpc.FormKey, appearancePluginKey, assetSourceDirs))
                 {
                     string errorMsg = $"Missing expected FaceGen for NPC {npcInPatch.EditorID ?? npcInPatch.FormKey.ToString()} in source directories for plugin {appearancePluginKey.FileName}.";
                     AppendLog($"      WARNING: {errorMsg}");
                     if (AbortIfMissingFaceGen)
                     { throw new FileNotFoundException(errorMsg); }
                 }
            } else { AppendLog($"      Skipping FaceGen identification (Templated)."); }

            // 2. Extra Assets
            if (CopyExtraAssets)
            {
                 AppendLog($"      Identifying extra assets...");
                 GetAssetsReferencedByPlugin(sourceNpc, meshesToCopy, texturesToCopy);
            }

            // --- Handle BSAs (**Clarification 2 Applied**) ---
            HashSet<string> extractedMeshFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> extractedTextureFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (HandleBSAFiles_Patching)
            {
                AppendLog($"      Checking BSAs associated with {appearancePluginKey.FileName} in source directories...");
                // UnpackAssetsFromBSA now handles iterating through source directories
                UnpackAssetsFromBSA(meshesToCopy, texturesToCopy, extractedMeshFiles, extractedTextureFiles, appearancePluginKey, assetSourceDirs, _currentRunOutputAssetPath);
                AppendLog($"      Extracted {extractedMeshFiles.Count} meshes and {extractedTextureFiles.Count} textures from BSAs.");
            }

             // --- Handle NIF Scanning (**Uses Clarification 2 via UnpackAssetsFromBSA**) ---
             if (CopyExtraAssets && FindExtraTexturesInNifs)
             {
                  AppendLog($"      Scanning NIF files for additional textures...");
                  HashSet<string> texturesFromNifs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                  HashSet<string> alreadyHandledTextures = new HashSet<string>(texturesToCopy, StringComparer.OrdinalIgnoreCase);
                  alreadyHandledTextures.UnionWith(extractedTextureFiles);

                  // Scan loose NIFs (check all source dirs)
                  GetExtraTexturesFromNifSet(meshesToCopy, assetSourceDirs, texturesFromNifs, alreadyHandledTextures);
                  // Scan extracted NIFs (check output dir)
                  GetExtraTexturesFromNifSet(extractedMeshFiles, new List<string> { _currentRunOutputAssetPath }, texturesFromNifs, alreadyHandledTextures);

                  AppendLog($"        Found {texturesFromNifs.Count} additional textures in NIFs.");

                  // Try extracting newly found textures from BSAs
                  if (HandleBSAFiles_Patching && texturesFromNifs.Any())
                  {
                      HashSet<string> newlyExtractedNifTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                      UnpackAssetsFromBSA(new HashSet<string>(), texturesFromNifs, new HashSet<string>(), newlyExtractedNifTextures, appearancePluginKey, assetSourceDirs, _currentRunOutputAssetPath);
                      AppendLog($"        Extracted {newlyExtractedNifTextures.Count} of these additional textures from BSAs.");
                      texturesFromNifs.ExceptWith(newlyExtractedNifTextures);
                  }
                  texturesToCopy.UnionWith(texturesFromNifs); // Add remaining loose NIF textures
             }


            // --- Copy Loose Files (**Clarification 2 Applied**) ---
            AppendLog($"      Copying {meshesToCopy.Count} loose mesh files...");
            CopyAssetFiles(assetSourceDirs, meshesToCopy, "Meshes", appearancePluginKey.FileName.String);

            AppendLog($"      Copying {texturesToCopy.Count} loose texture files...");
            CopyAssetFiles(assetSourceDirs, texturesToCopy, "Textures", appearancePluginKey.FileName.String);

            AppendLog($"    Finished asset copying for {npcInPatch.EditorID ?? npcInPatch.FormKey.ToString()}.");
        }


        // --- Asset Identification Helpers (No changes needed here) ---
        private void GetAssetsReferencedByPlugin(INpcGetter npc, HashSet<string> meshPaths, HashSet<string> texturePaths)
        { /* Implementation remains the same */
             if (npc.HeadParts != null) foreach (var hpLink in npc.HeadParts) GetHeadPartAssetPaths(hpLink, texturePaths, meshPaths);
             if (!npc.WornArmor.IsNull && npc.WornArmor.TryResolve(_environmentStateProvider.LinkCache, out var wornArmorGetter) && wornArmorGetter.Armature != null)
                foreach (var aaLink in wornArmorGetter.Armature) GetARMAAssetPaths(aaLink, texturePaths, meshPaths);
        }
        private void GetHeadPartAssetPaths(IFormLinkGetter<IHeadPartGetter> hpLink, HashSet<string> texturePaths, HashSet<string> meshPaths)
        { /* Implementation remains the same */
            if (hpLink.IsNull || !hpLink.TryResolve(_environmentStateProvider.LinkCache, out var hpGetter)) return;
            if (hpGetter.Model?.File != null) meshPaths.Add(hpGetter.Model.File);
            if (hpGetter.Parts != null) foreach (var part in hpGetter.Parts) if (part?.FileName != null) meshPaths.Add(part.FileName);
            if (!hpGetter.TextureSet.IsNull) GetTextureSetPaths(hpGetter.TextureSet, texturePaths);
            if (hpGetter.ExtraParts != null) foreach (var extraPartLink in hpGetter.ExtraParts) GetHeadPartAssetPaths(extraPartLink, texturePaths, meshPaths);
        }
        private void GetARMAAssetPaths(IFormLinkGetter<IArmorAddonGetter> aaLink, HashSet<string> texturePaths, HashSet<string> meshPaths)
        { /* Implementation remains the same */
             if (aaLink.IsNull || !aaLink.TryResolve(_environmentStateProvider.LinkCache, out var aaGetter)) return;
             if (aaGetter.WorldModel?.Male?.File != null) meshPaths.Add(aaGetter.WorldModel.Male.File);
             if (aaGetter.WorldModel?.Female?.File != null) meshPaths.Add(aaGetter.WorldModel.Female.File);
             if (!aaGetter.SkinTexture?.Male.IsNull ?? false) GetTextureSetPaths(aaGetter.SkinTexture.Male, texturePaths);
             if (!aaGetter.SkinTexture?.Female.IsNull ?? false) GetTextureSetPaths(aaGetter.SkinTexture.Female, texturePaths);
        }
        private void GetTextureSetPaths(IFormLinkGetter<ITextureSetGetter> txstLink, HashSet<string> texturePaths)
        { /* Implementation remains the same */
             if (txstLink.IsNull || !txstLink.TryResolve(_environmentStateProvider.LinkCache, out var txstGetter)) return;
             if (!string.IsNullOrEmpty(txstGetter.Diffuse?.GivenPath)) texturePaths.Add(txstGetter.Diffuse.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.NormalOrGloss?.GivenPath)) texturePaths.Add(txstGetter.NormalOrGloss.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.EnvironmentMaskOrSubsurfaceTint?.GivenPath)) texturePaths.Add(txstGetter.EnvironmentMaskOrSubsurfaceTint.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.GlowOrDetailMap?.GivenPath)) texturePaths.Add(txstGetter.GlowOrDetailMap.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.Height?.GivenPath)) texturePaths.Add(txstGetter.Height.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.Environment?.GivenPath)) texturePaths.Add(txstGetter.Environment.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.Multilayer?.GivenPath)) texturePaths.Add(txstGetter.Multilayer.GivenPath);
             if (!string.IsNullOrEmpty(txstGetter.BacklightMaskOrSpecular?.GivenPath)) texturePaths.Add(txstGetter.BacklightMaskOrSpecular.GivenPath);
        }
        private (string, string) GetFaceGenSubPathStrings(FormKey npcFormKey)
        { /* Implementation remains the same */
            string meshPath = $"actors\\character\\facegendata\\facegeom\\{npcFormKey.ModKey.FileName}\\{npcFormKey.IDString()}.nif";
            string texPath = $"actors\\character\\facegendata\\facetint\\{npcFormKey.ModKey.FileName}\\{npcFormKey.IDString()}.dds";
            return (meshPath.ToLowerInvariant(), texPath.ToLowerInvariant());
        }

        /// <summary>
        /// Checks if FaceGen files exist loose or in BSAs within the provided source directories.
        /// **Revised for Clarification 2.**
        /// </summary>
               private bool FaceGenExists(FormKey npcFormKey, ModKey appearancePluginKey, List<string> assetSourceDirs)
        {
            var (faceMeshPath, faceTexPath) = GetFaceGenSubPathStrings(npcFormKey);
            bool nifFound = false;
            bool ddsFound = false;

            // Check Loose Files (Iterate all source dirs)
            foreach (var sourceDir in assetSourceDirs)
            {
                if (!nifFound && File.Exists(Path.Combine(sourceDir, "meshes", faceMeshPath))) nifFound = true;
                if (!ddsFound && File.Exists(Path.Combine(sourceDir, "textures", faceTexPath))) ddsFound = true;
                if (nifFound && ddsFound) break;
            }

            // Check BSAs (if not found loose and BSA handling is enabled)
            if ((!nifFound || !ddsFound) && HandleBSAFiles_Patching)
            {
                // Iterate source directories *backwards*
                for (int i = assetSourceDirs.Count - 1; i >= 0; i--)
                {
                    var sourceDir = assetSourceDirs[i];
                    HashSet<IArchiveReader> readers = BsaHandler.OpenBsaArchiveReaders(sourceDir, appearancePluginKey);
                    if (readers.Any())
                    {
                        string bsaMeshPath = Path.Combine("meshes", faceMeshPath).Replace('/', '\\');
                        string bsaTexPath = Path.Combine("textures", faceTexPath).Replace('/', '\\');

                        // Check *this specific set* of readers using HaveFile
                        // *** Use HaveFile here (out var _ discards the file) ***
                        if (!nifFound && BsaHandler.HaveFile(bsaMeshPath, readers, out _)) nifFound = true;
                        if (!ddsFound && BsaHandler.HaveFile(bsaTexPath, readers, out _)) ddsFound = true;

                        if (nifFound && ddsFound) break; // Found both
                    }
                }
            }
            return nifFound && ddsFound;
        }


        // --- Asset Copying/Extraction Helpers (Revised for Clarification 2) ---

        /// <summary>
        /// Extracts assets from BSAs found in any of the assetSourceDirs, prioritizing later directories.
        /// **Revised for Clarification 2.**
        /// </summary>
                private void UnpackAssetsFromBSA(
            HashSet<string> MeshesToExtract, HashSet<string> TexturesToExtract,
            HashSet<string> extractedMeshes, HashSet<string> extractedTextures,
            ModKey currentPluginKey, List<string> assetSourceDirs, string targetAssetPath)
        {
            if (!assetSourceDirs.Any()) return;

            var foundMeshSources = new Dictionary<string, (IArchiveFile file, string dest)>(StringComparer.OrdinalIgnoreCase);
            var foundTextureSources = new Dictionary<string, (IArchiveFile file, string dest)>(StringComparer.OrdinalIgnoreCase);

            // Iterate source directories *backwards*
            for (int i = assetSourceDirs.Count - 1; i >= 0; i--)
            {
                string sourceDir = assetSourceDirs[i];
                var readers = BsaHandler.OpenBsaArchiveReaders(sourceDir, currentPluginKey); // Use V2's OpenBsaArchiveReaders
                if (!readers.Any()) continue;

                // Check remaining meshes
                foreach (string subPath in MeshesToExtract.ToList())
                {
                    if (foundMeshSources.ContainsKey(subPath)) continue;

                    string bsaMeshPath = Path.Combine("meshes", subPath).Replace('/', '\\');
                    // *** Use HaveFile here ***
                    if (BsaHandler.HaveFile(bsaMeshPath, readers, out var file) && file != null)
                    {
                        foundMeshSources[subPath] = (file, Path.Combine(targetAssetPath, "meshes", subPath));
                    }
                }

                // Check remaining textures
                foreach (string subPath in TexturesToExtract.ToList())
                {
                    if (foundTextureSources.ContainsKey(subPath)) continue;

                    string bsaTexPath = Path.Combine("textures", subPath).Replace('/', '\\');
                     // *** Use HaveFile here ***
                    if (BsaHandler.HaveFile(bsaTexPath, readers, out var file) && file != null)
                    {
                        foundTextureSources[subPath] = (file, Path.Combine(targetAssetPath, "textures", subPath));
                    }
                }
            }

            // Extract winning sources
            foreach (var kvp in foundMeshSources)
            {
                string subPath = kvp.Key;
                if (BsaHandler.ExtractFileFromBsa(kvp.Value.file, kvp.Value.dest)) // Assumes ExtractFileFromBSA returns bool
                {
                    extractedMeshes.Add(subPath);
                    MeshesToExtract.Remove(subPath);
                } else { AppendLog($"          ERROR extracting winning BSA mesh: {subPath}"); }
            }
            foreach (var kvp in foundTextureSources)
            {
                string subPath = kvp.Key;
                if (BsaHandler.ExtractFileFromBsa(kvp.Value.file, kvp.Value.dest)) // Assumes ExtractFileFromBSA returns bool
                {
                    extractedTextures.Add(subPath);
                    TexturesToExtract.Remove(subPath);
                } else { AppendLog($"          ERROR extracting winning BSA texture: {subPath}"); }
            }
        }


        /// <summary>
        /// Scans NIFs found in the source directories for textures.
        /// **Revised for Clarification 2.**
        /// </summary>
        private void GetExtraTexturesFromNifSet(HashSet<string> nifSubPaths, List<string> sourceBaseDirs, HashSet<string> outputTextures, HashSet<string> ignoredTextures)
        {
             int foundCount = 0;
             foreach (var nifPathRelative in nifSubPaths)
             {
                 if (!nifPathRelative.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) continue;

                 string? foundNifFullPath = null;
                 // Iterate source dirs to find the NIF file
                 foreach(var baseDir in sourceBaseDirs)
                 {
                     string potentialPath = Path.Combine(baseDir, "meshes", nifPathRelative);
                     if(File.Exists(potentialPath))
                     {
                         foundNifFullPath = potentialPath;
                         break; // Found it
                     }
                 }

                 if (foundNifFullPath != null)
                 {
                     try
                     {
                         var nifTextures = NifHandler.GetExtraTexturesFromNif(foundNifFullPath);
                         foreach (var texPathRelative in nifTextures)
                         {
                             if (!ignoredTextures.Contains(texPathRelative) && !IsIgnored(texPathRelative, _pathsToIgnore))
                             {
                                 if (outputTextures.Add(texPathRelative)) foundCount++;
                             }
                         }
                     }
                     catch (Exception ex)
                     { AppendLog($"        WARNING: Failed to scan NIF '{foundNifFullPath}': {ExceptionLogger.GetExceptionStack(ex)}"); } // Use logger
                 }
             }
        }

        /// <summary>
        /// Copies loose asset files, checking all source directories.
        /// **Revised for Clarification 2.**
        /// </summary>
        private void CopyAssetFiles(List<string> sourceDataPaths, HashSet<string> assetRelativePathList, string assetType /*"Meshes" or "Textures"*/, string sourcePluginName)
        {
            string outputBase = Path.Combine(_currentRunOutputAssetPath, assetType);
            Directory.CreateDirectory(outputBase);

            var warningsToSuppressSet = _warningsToSuppress_Global;
            string pluginKeyLower = sourcePluginName.ToLowerInvariant();
            if (_warningsToSuppress.TryGetValue(pluginKeyLower, out var specificWarnings)) { warningsToSuppressSet = specificWarnings; }

            foreach (string relativePath in assetRelativePathList)
            {
                if (IsIgnored(relativePath, _pathsToIgnore)) continue;

                string? foundSourcePath = null;
                // Check Source Directories
                foreach (var sourcePathBase in sourceDataPaths)
                {
                     string potentialPath = Path.Combine(sourcePathBase, assetType, relativePath);
                     if(File.Exists(potentialPath))
                     {
                         foundSourcePath = potentialPath;
                         break; // Found it
                     }
                }

                // Check Game Data Folder (if configured and not FaceGen)
                bool isFaceGen = relativePath.Contains("facegendata", StringComparison.OrdinalIgnoreCase);
                if (foundSourcePath == null && GetMissingExtraAssetsFromAvailableWinners && !isFaceGen)
                {
                     string gameDataPath = Path.Combine(_environmentStateProvider.DataFolderPath.ToString(), assetType, relativePath);
                     if (File.Exists(gameDataPath))
                     {
                         foundSourcePath = gameDataPath;
                         AppendLog($"        Found missing asset '{relativePath}' in game data folder.");
                     }
                }

                if (foundSourcePath == null)
                {
                    // Handle Missing File Warning/Error
                    bool suppressWarning = SuppressAllMissingFileWarnings ||
                                          (SuppressKnownMissingFileWarnings && warningsToSuppressSet.Contains(relativePath)) ||
                                          GetExtensionOfMissingFile(relativePath) == ".tri";
                     if (!suppressWarning)
                     {
                          string errorMsg = $"Asset '{relativePath}' not found in any source directories";
                          if(GetMissingExtraAssetsFromAvailableWinners && !isFaceGen) errorMsg += " or game data folder";
                          errorMsg += $" (needed by {sourcePluginName}).";
                          AppendLog($"      WARNING: {errorMsg}");
                          if (AbortIfMissingExtraAssets && !isFaceGen)
                          { throw new FileNotFoundException(errorMsg, relativePath); }
                     }
                }
                else
                {
                     // Copy the found file
                     string destPath = Path.Combine(outputBase, relativePath);
                     try
                     {
                          FileInfo fileInfo = Auxilliary.CreateDirectoryIfNeeded(destPath, Auxilliary.PathType.File);
                          File.Copy(foundSourcePath, destPath, true);
                     }
                     catch (Exception ex)
                     { AppendLog($"      ERROR copying '{foundSourcePath}' to '{destPath}': {ExceptionLogger.GetExceptionStack(ex)}"); } // Use logger
                }
            }
        }


        private bool IsIgnored(string relativePath, HashSet<string> toIgnore)
        {
            // (Implementation remains the same as before)
             return toIgnore.Contains(relativePath);
        }

        private string GetExtensionOfMissingFile(string input)
        {
            // (Implementation remains the same as before)
             if (string.IsNullOrEmpty(input)) { return ""; }
             return Path.GetExtension(input).ToLowerInvariant();
        }

        private void AppendLog(string message)
        {
             // (Implementation remains the same as before)
             RxApp.MainThreadScheduler.Schedule(() =>
             {
                 LogOutput += message + Environment.NewLine;
             });
        }
    }


    // Temporary class matching V1's structure for loading JSON
    public class suppressedWarnings
    {
        public string Plugin { get; set; } = "";
        public HashSet<string> Paths { get; set; } = new HashSet<string>();
    }

    // *** Need an Adapter or Modification for BSAHandler ***
    // Add a method like this to your BSAHandler class:
    /*
    public static class BSAHandler // Assuming static class
    {
        // ... other methods ...

        /// <summary>
        /// Tries to find a file within any BSA reader in the provided set.
        /// </summary>
        /// <returns>True if found, false otherwise. Outputs the file if found.</returns>
        public static bool GetFileFromReaders(string subpath, HashSet<IArchiveReader> bsaReaders, out IArchiveFile? file)
        {
            file = null;
            foreach (var reader in bsaReaders)
            {
                // Assuming TryGetFile exists and checks reader.Files case-insensitively
                if (TryGetFile(subpath, reader, out file))
                {
                    return true; // Found it
                }
            }
            return false; // Not found in any reader in this set
        }

        // Make sure ExtractFileFromBSA returns bool indicating success/failure
        public static bool ExtractFileFromBSA(IArchiveFile file, string destPath)
        {
            string? dirPath = Path.GetDirectoryName(destPath);
            if (dirPath == null) return false; // Invalid destination path

            try
            {
                Directory.CreateDirectory(dirPath); // Ensure directory exists
                using (var fileStream = File.Create(destPath)) // Use 'using' for proper disposal
                {
                    file.CopyDataTo(fileStream);
                }
                return true; // Success
            }
            catch (Exception ex) // Catch specific exceptions if needed (IOException, UnauthorizedAccessException)
            {
                Console.WriteLine($"==========================================================================================================");
                Console.WriteLine($"Could not extract file from BSA: {file.Path} to {destPath}. Error: {ExceptionLogger.GetExceptionStack(ex)}");
                Console.WriteLine($"==========================================================================================================");
                // Optionally re-throw or log differently
                return false; // Failure
            }
        }
    }
    */
}