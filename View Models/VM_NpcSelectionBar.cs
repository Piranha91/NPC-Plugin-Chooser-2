// [VM_NpcSelectionBar.cs] - Updated for List<MugshotInfo>
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions; // For regex matching
using System.Windows; // For MessageBox
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models; // Needed for Settings
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat; // For Locator

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_NpcSelectionBar : ReactiveObject, IDisposable
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider;
        private readonly Auxilliary _auxilliary;
        private readonly CompositeDisposable _disposables = new();

        // *** UPDATED TYPE ***
        // Stores mugshot data keyed by NPC FormKey string (case-insensitive)
        // Value is a list of potential mugshots from different [ModName] folders
        private Dictionary<string, List<(string ModName, string ImagePath)>> _mugshotData = new();

        public ObservableCollection<VM_NpcSelection> Npcs { get; } = new();

        [Reactive] public VM_NpcSelection? SelectedNpc { get; set; }

        [ObservableAsProperty] public ObservableCollection<VM_AppearanceMod>? CurrentNpcAppearanceMods { get; }

        public VM_NpcSelectionBar(EnvironmentStateProvider environmentStateProvider, Settings settings, Auxilliary auxilliary, NpcConsistencyProvider consistencyProvider)
        {
            _environmentStateProvider = environmentStateProvider;
            _settings = settings;
            _auxilliary = auxilliary;
            _consistencyProvider = consistencyProvider;

            this.WhenAnyValue(x => x.SelectedNpc)
                 // *** Pass updated _mugshotData type ***
                .Select(selectedNpc => selectedNpc != null
                    ? CreateAppearanceModViewModels(selectedNpc, _mugshotData)
                    : new ObservableCollection<VM_AppearanceMod>())
                .ObserveOn(RxApp.MainThreadScheduler)
                .ToPropertyEx(this, x => x.CurrentNpcAppearanceMods);

            // Subscribe to selection changes from the consistency provider
             _consistencyProvider.NpcSelectionChanged
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(args =>
                {
                    var npcVM = Npcs.FirstOrDefault(n => n.NpcFormKey.ToString().Equals(args.NpcFormKey.ToString(), StringComparison.OrdinalIgnoreCase));
                    if (npcVM != null)
                    {
                        npcVM.SelectedAppearanceMod = args.SelectedMod;
                        if (SelectedNpc == npcVM && CurrentNpcAppearanceMods != null)
                        {
                            foreach (var modVM in CurrentNpcAppearanceMods)
                            {
                                modVM.IsSelected = modVM.ModName.Equals(args.SelectedMod, StringComparison.OrdinalIgnoreCase);
                            }
                        }
                    }
                })
                .DisposeWith(_disposables);

            Initialize();
        }

        private static readonly Regex PluginRegex = new(@"^.+\.(esm|esp|esl)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HexFileRegex = new(@"^[0-9A-F]{8}\.(png|jpg|jpeg|bmp)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);


        /// <summary>
        /// Scans the Mugshots directory for images matching the expected structure
        /// MugShotFolder\[ModName]\[PluginName.ext]\[8DigitHex].(png|jpg|jpeg|bmp)
        /// </summary>
        /// <returns>Dictionary mapping NPC FormKey string (case-insensitive) to a List of (ModName from folder, ImagePath)</returns>
        // *** UPDATED RETURN TYPE ***
        private Dictionary<string, List<(string ModName, string ImagePath)>> ScanMugshotDirectory()
        {
            // *** UPDATED DICTIONARY TYPE ***
            var results = new Dictionary<string, List<(string ModName, string ImagePath)>>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(_settings.MugshotsFolder) || !Directory.Exists(_settings.MugshotsFolder))
            {
                System.Diagnostics.Debug.WriteLine("Mugshot directory not set or not found.");
                return results;
            }

            System.Diagnostics.Debug.WriteLine($"Scanning mugshot directory: {_settings.MugshotsFolder}");
            string expectedParentPath = _settings.MugshotsFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            try
            {
                var potentialFiles = Directory.EnumerateFiles(_settings.MugshotsFolder, "*.*", SearchOption.AllDirectories)
                                              .Where(f => HexFileRegex.IsMatch(Path.GetFileName(f)));

                foreach (var filePath in potentialFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        string hexFileName = fileInfo.Name;

                        DirectoryInfo? pluginDir = fileInfo.Directory;
                        if (pluginDir == null || !PluginRegex.IsMatch(pluginDir.Name)) continue;
                        string pluginName = pluginDir.Name;

                        DirectoryInfo? modDir = pluginDir.Parent;
                        if (modDir == null || string.IsNullOrWhiteSpace(modDir.Name)) continue;
                        string modName = modDir.Name;

                        if (modDir.Parent == null || !modDir.Parent.FullName.Equals(expectedParentPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string hexPart = Path.GetFileNameWithoutExtension(hexFileName);
                        if (hexPart.Length != 8) continue;

                        string formKeyString = $"{hexPart.Substring(hexPart.Length - 6)}:{pluginName}";

                        try { FormKey.Factory(formKeyString); }
                        catch { continue; } // Skip invalid format

                        // *** UPDATED LOGIC TO ADD TO LIST ***
                        var mugshotInfo = (ModName: modName, ImagePath: filePath);

                        if (results.TryGetValue(formKeyString, out var existingList))
                        {
                            // Add to existing list if ModName/Path combo isn't already there
                            if (!existingList.Any(item => item.ModName.Equals(mugshotInfo.ModName, StringComparison.OrdinalIgnoreCase) && item.ImagePath.Equals(mugshotInfo.ImagePath, StringComparison.OrdinalIgnoreCase)))
                            {
                                existingList.Add(mugshotInfo);
                            }
                        }
                        else
                        {
                            // Create new list and add it to the dictionary
                            results[formKeyString] = new List<(string ModName, string ImagePath)> { mugshotInfo };
                        }
                        //System.Diagnostics.Debug.WriteLine($"Found mugshot: Key='{formKeyString}', Mod='{modName}', Path='{filePath}'");

                    }
                    catch (IOException ioEx) { System.Diagnostics.Debug.WriteLine($"IO Error processing '{filePath}': {ioEx.Message}"); }
                    catch (UnauthorizedAccessException uaEx) { System.Diagnostics.Debug.WriteLine($"Access Denied processing '{filePath}': {uaEx.Message}"); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"General Error processing '{filePath}': {ex.Message}"); }
                }
            }
            catch (UnauthorizedAccessException uaEx) { MessageBox.Show($"Access Denied scanning '{_settings.MugshotsFolder}': {uaEx.Message}", "Mugshot Scan Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
            catch (Exception ex) { MessageBox.Show($"Error scanning '{_settings.MugshotsFolder}': {ex.Message}", "Mugshot Scan Error", MessageBoxButton.OK, MessageBoxImage.Warning); }

            System.Diagnostics.Debug.WriteLine($"Mugshot scan complete. Found entries for {results.Count} unique FormKeys.");
            return results;
        }


        public void Initialize()
        {
            if (!_environmentStateProvider.EnvironmentIsValid)
            {
                MessageBox.Show($"Environment is not valid. Check settings.\nError: {_environmentStateProvider.EnvironmentBuilderError}", "Environment Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                Npcs.Clear();
                _mugshotData.Clear();
                SelectedNpc = null;
                return;
            }

            _mugshotData = ScanMugshotDirectory();
            var processedMugshotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var previouslySelectedNpcKey = SelectedNpc?.NpcFormKey;
            Npcs.Clear();
            SelectedNpc = null;


            var npcRecords = _environmentStateProvider.LoadOrder.PriorityOrder
                                 .WinningOverrides<INpcGetter>()
                                 .Where(npc => npc.EditorID?.Contains("Preset", StringComparison.OrdinalIgnoreCase) == false &&
                                               !npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.IsCharGenFacePreset))
                                 .OrderBy(x => _auxilliary.FormKeyStringToFormIDString(x.FormKey.ToString()))
                                 .ToArray();

            foreach (var npc in npcRecords)
            {
                try
                {
                    var npcSelector = new VM_NpcSelection(npc, _environmentStateProvider, _consistencyProvider);
                    Npcs.Add(npcSelector);
                    string npcFormKeyString = npc.FormKey.ToString();
                    if (_mugshotData.ContainsKey(npcFormKeyString))
                    {
                        processedMugshotKeys.Add(npcFormKeyString);
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error initializing VM for NPC {npc.EditorID ?? npc.FormKey.ToString()}: {ex.Message}"); }
            }

            // Process NPCs found ONLY in Mugshots
            foreach (var kvp in _mugshotData)
            {
                 string mugshotFormKeyString = kvp.Key;
                 // *** Use the list from the dictionary value ***
                 List<(string ModName, string ImagePath)> mugshots = kvp.Value;

                if (!processedMugshotKeys.Contains(mugshotFormKeyString) && mugshots.Any()) // Ensure list isn't empty
                {
                    try
                    {
                        FormKey mugshotFormKey = FormKey.Factory(mugshotFormKeyString);
                        var npcSelector = new VM_NpcSelection(mugshotFormKey, _environmentStateProvider, _consistencyProvider);

                        if (npcSelector.DisplayName == mugshotFormKeyString)
                        {
                            // Use the ModName from the *first* mugshot found for a better default name
                            string firstModName = mugshots[0].ModName;
                            string pluginBaseName = Path.GetFileNameWithoutExtension(mugshotFormKey.ModKey.FileName);
                            npcSelector.DisplayName = $"{firstModName} - {pluginBaseName} [{mugshotFormKeyString}]";
                        }

                        Npcs.Add(npcSelector);
                        System.Diagnostics.Debug.WriteLine($"Added NPC {mugshotFormKeyString} from mugshot data.");
                    }
                    catch(Exception ex) { System.Diagnostics.Debug.WriteLine($"Error creating VM for mugshot-only NPC {mugshotFormKeyString}: {ex.Message}"); }
                }
            }

             // Optional re-sort if needed
             // ...

             // Restore Selection
             if (previouslySelectedNpcKey != null)
             {
                 SelectedNpc = Npcs.FirstOrDefault(n => n.NpcFormKey.Equals(previouslySelectedNpcKey));
             }
             if (SelectedNpc == null && Npcs.Any())
             {
                 SelectedNpc = Npcs[0];
             }
        }


        /// <summary>
        /// Creates the list of Appearance Mod ViewModels for the selected NPC,
        /// incorporating both plugin overrides and distinct mugshot sources.
        /// </summary>
        // *** UPDATED method signature and logic ***
        private ObservableCollection<VM_AppearanceMod> CreateAppearanceModViewModels(
            VM_NpcSelection npcVM,
            Dictionary<string, List<(string ModName, string ImagePath)>> mugshotData)
        {
            var modVMs = new ObservableCollection<VM_AppearanceMod>();
            if (npcVM == null) return modVMs;

            string npcFormKeyString = npcVM.NpcFormKey.ToString();
            // Get the list of mugshots for this NPC, default to empty list if not found
            var npcMugshotList = mugshotData.GetValueOrDefault(npcFormKeyString, new List<(string ModName, string ImagePath)>());

            // Track added sources (Plugin FileName or Mugshot ModName folder) to avoid duplicates
            var addedModSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Process Plugin-based Appearance Mods (only for NPCs from load order)
            if (npcVM.NpcGetter != null)
            {
                foreach (var modKey in npcVM.AppearanceMods.Distinct())
                {
                    string pluginModName = modKey.FileName; // Use the actual plugin filename as the identifier
                    string? imagePathForThisPlugin = null;

                    // Find if there's a mugshot entry with a matching ModName (folder name == plugin filename)
                    var matchingMugshot = npcMugshotList.FirstOrDefault(m => m.ModName.Equals(pluginModName, StringComparison.OrdinalIgnoreCase));
                    if (matchingMugshot != default) // default is (null, null) for struct tuple
                    {
                        imagePathForThisPlugin = matchingMugshot.ImagePath;
                         //System.Diagnostics.Debug.WriteLine($"Matched mugshot {imagePathForThisPlugin} to plugin {pluginModName}");
                    }

                    // Create VM for the plugin source, potentially with an image path found above
                    modVMs.Add(new VM_AppearanceMod(pluginModName, modKey, npcVM.NpcFormKey, imagePathForThisPlugin, _settings, _consistencyProvider));
                    addedModSources.Add(pluginModName); // Mark this plugin filename as processed
                }
            }

            // 2. Process Mugshot-based Appearance Sources from the list
            foreach (var mugshotInfo in npcMugshotList)
            {
                string mugshotModName = mugshotInfo.ModName; // This is the [ModName] folder name
                string mugshotImagePath = mugshotInfo.ImagePath;

                // Check if a VM corresponding to this mugshot's ModName (folder name)
                // was already added in step 1 (meaning folder name matched a plugin filename)
                if (!addedModSources.Contains(mugshotModName))
                {
                    // This mugshot represents an appearance source not covered by the loaded plugins OR
                    // the folder [ModName] is different from any relevant plugin filename.
                    // Create a new VM specifically for this mugshot source.
                    // Use the FormKey's ModKey as a fallback identifier, as there's no direct plugin ModKey.
                    System.Diagnostics.Debug.WriteLine($"Adding separate VM for mugshot source: {mugshotModName} (Path: {mugshotImagePath})");
                    modVMs.Add(new VM_AppearanceMod(mugshotModName, npcVM.NpcFormKey.ModKey, npcVM.NpcFormKey, mugshotImagePath, _settings, _consistencyProvider));
                    addedModSources.Add(mugshotModName); // Mark this mugshot folder name as processed
                }
            }

            // 3. Handle Mugshot-only NPCs (NpcGetter is null)
            // They won't have gone through step 1. Step 2 should cover adding VMs for all their mugshots.
            // If npcVM.AppearanceMods still holds the base FormKey.ModKey, ensure it doesn't cause duplicates if
            // a mugshot folder name happened to match the FormKey.ModKey.FileName. `addedModSources` handles this.
            if (npcVM.NpcGetter == null && !modVMs.Any() && npcVM.AppearanceMods.Any())
            {
                // Fallback: If a mugshot-only NPC somehow had no mugshots processed in step 2,
                // but still has its base modkey in AppearanceMods, add a basic entry.
                var baseModKey = npcVM.AppearanceMods.First(); // Should be the FormKey.ModKey
                 if (!addedModSources.Contains(baseModKey.FileName))
                 {
                      System.Diagnostics.Debug.WriteLine($"Adding fallback VM for mugshot-only NPC {npcFormKeyString} using ModKey {baseModKey.FileName}");
                      modVMs.Add(new VM_AppearanceMod(baseModKey.FileName, baseModKey, npcVM.NpcFormKey, null, _settings, _consistencyProvider));
                 }
            }


            // Final Sort
            var sortedVMs = modVMs.OrderBy(vm => vm.ModName).ToList();
            modVMs.Clear();
            foreach (var vm in sortedVMs)
            {
                modVMs.Add(vm);
            }

            return modVMs;
        }


        // Dispose managed resources
        public void Dispose()
        {
            _disposables.Dispose();
            ClearAppearanceModViewModels();
        }

         private void ClearAppearanceModViewModels()
        {
            if (this.CurrentNpcAppearanceMods != null)
            {
                foreach (var vm in this.CurrentNpcAppearanceMods)
                {
                    vm.Dispose();
                }
            }
             // Let WhenAnyValue handle replacing the collection property itself.
        }
    }
}