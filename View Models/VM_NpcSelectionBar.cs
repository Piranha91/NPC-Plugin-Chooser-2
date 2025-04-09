// VM_NpcSelectionBar.cs (Updated)
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
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
    public class VM_NpcSelectionBar : ReactiveObject
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider; // Use central provider
        private readonly Auxilliary _auxilliary;

        public ObservableCollection<VM_NpcSelection> Npcs { get; } = new();

        [Reactive] public VM_NpcSelection? SelectedNpc { get; set; }

        // Holds the VM_AppearanceMod instances for the currently selected NPC
        [ObservableAsProperty] public ObservableCollection<VM_AppearanceMod>? CurrentNpcAppearanceMods { get; }

        public VM_NpcSelectionBar(EnvironmentStateProvider environmentStateProvider, Settings settings, Auxilliary auxilliary, NpcConsistencyProvider consistencyProvider)
        {
            _environmentStateProvider = environmentStateProvider;
            _settings = settings;
            _auxilliary = auxilliary;
            _consistencyProvider = consistencyProvider; // Inject consistency provider

            // When SelectedNpc changes, update the list of appearance mods
            this.WhenAnyValue(x => x.SelectedNpc)
                .Select(selectedNpc => selectedNpc != null ? CreateAppearanceModViewModels(selectedNpc) : new ObservableCollection<VM_AppearanceMod>())
                .ObserveOn(RxApp.MainThreadScheduler) // Ensure collection updates happen on UI thread
                .ToPropertyEx(this, x => x.CurrentNpcAppearanceMods);

            // If the underlying environment changes, re-initialize
            // (Needs a way to signal environment update, e.g., an Observable in EnvironmentStateProvider)
            // For now, call Initialize explicitly when needed.
            Initialize();

             // Subscribe to selection changes from the consistency provider
             _consistencyProvider.NpcSelectionChanged
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(args =>
                {
                    // Find the relevant NPC VM and update its selection state
                    var npcVM = Npcs.FirstOrDefault(n => n.NpcGetter.FormKey == args.NpcFormKey);
                    if (npcVM != null)
                    {
                        npcVM.SelectedAppearanceMod = args.SelectedMod;
                        // Also update the IsSelected state of the relevant VM_AppearanceMod if the current NPC is selected
                        if (SelectedNpc == npcVM && CurrentNpcAppearanceMods != null)
                        {
                             foreach(var modVM in CurrentNpcAppearanceMods)
                             {
                                 modVM.IsSelected = modVM.ModKey == args.SelectedMod;
                             }
                        }
                    }
                })
                .DisposeWith(NpcsViewModelDisposables); // Assuming you have a mechanism to dispose subscriptions
        }

         // Add a CompositeDisposable for managing subscriptions if needed
        private readonly System.Reactive.Disposables.CompositeDisposable NpcsViewModelDisposables = new System.Reactive.Disposables.CompositeDisposable();


        public void Initialize()
        {
            if (!_environmentStateProvider.EnvironmentIsValid)
            {
                MessageBox.Show($"Environment is not valid. Check settings.\nError: {_environmentStateProvider.EnvironmentBuilderError}", "Environment Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                Npcs.Clear();
                return;
            }

            Npcs.Clear();
            var npcRecords = _environmentStateProvider.LoadOrder.PriorityOrder
                                 .WinningOverrides<INpcGetter>()
                                 .Where(npc => !npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.IsCharGenFacePreset)) // Exclude face presets
                                 .OrderBy(x => _auxilliary.FormKeyStringToFormIDString(x.FormKey.ToString())) // Ensure consistent ordering
                                 .ToArray();

            foreach (var npc in npcRecords)
            {
                try
                {
                    // Pass consistency provider to VM_NpcSelection
                    var npcSelector = new VM_NpcSelection(npc, _environmentStateProvider, _consistencyProvider);
                    if (npcSelector.AppearanceMods.Any()) // Only add NPCs with more than one appearance option
                    {
                        Npcs.Add(npcSelector);
                    }
                }
                catch (Exception ex)
                {
                     // Log or handle initialization errors for specific NPCs
                     System.Diagnostics.Debug.WriteLine($"Error initializing VM for NPC {npc.EditorID ?? npc.FormKey.ToString()}: {ex.Message}");
                }
            }
        }

        private ObservableCollection<VM_AppearanceMod> CreateAppearanceModViewModels(VM_NpcSelection npcVM)
        {
            var modVMs = new ObservableCollection<VM_AppearanceMod>();
            if (npcVM == null) return modVMs;

            foreach (var modKey in npcVM.AppearanceMods.Distinct()) // Ensure distinct mods
            {
                 // Pass necessary info to VM_AppearanceMod
                modVMs.Add(new VM_AppearanceMod(modKey.ToString(), modKey, npcVM.NpcGetter, _settings, _consistencyProvider));
            }
            return modVMs;
        }
    }
}