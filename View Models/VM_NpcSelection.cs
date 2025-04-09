// VM_NpcSelection.cs (Updated)
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models; // For Settings if needed indirectly
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat; // For Locator if needed

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_NpcSelection : ReactiveObject
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly NpcConsistencyProvider _consistencyProvider;

        public INpcGetter? NpcGetter { get; } // Keep the original record
        public FormKey NpcFormKey { get; }

        [Reactive] public string DisplayName { get; set; } = "Unnamed NPC";
        public ObservableCollection<ModKey> AppearanceMods { get; } = new();

        // This property reflects the centrally stored selection
        [Reactive] public string SelectedAppearanceMod { get; set; }

        // Constructor now takes NpcConsistencyProvider
        public VM_NpcSelection(INpcGetter npcGetter, EnvironmentStateProvider environmentStateProvider, NpcConsistencyProvider consistencyProvider)
        {
            _environmentStateProvider = environmentStateProvider;
            _consistencyProvider = consistencyProvider;
            NpcFormKey = npcGetter.FormKey;
            NpcGetter = npcGetter;

            Initialize(npcGetter);

             // Get initial selection from the consistency provider
            SelectedAppearanceMod = _consistencyProvider.GetSelectedMod(npcGetter.FormKey);

             // Listen for external changes to this NPC's selection
             // (This is handled in VM_NpcSelectionBar now for simplicity)
             // It could also be done here if VM_NpcSelection instances were longer-lived or managed differently.
        }
        
        // Alternative constructor for NPCs not currently in the load order (i.e. no Getter is available, but a FormKey can be generated).
        public VM_NpcSelection(FormKey npcFormKey, EnvironmentStateProvider environmentStateProvider, NpcConsistencyProvider consistencyProvider)
        {
            _environmentStateProvider = environmentStateProvider;
            _consistencyProvider = consistencyProvider;
            NpcFormKey = npcFormKey;
            NpcGetter = null;

            Initialize(npcFormKey);

            // Get initial selection from the consistency provider
            SelectedAppearanceMod = _consistencyProvider.GetSelectedMod(npcFormKey);

            // Listen for external changes to this NPC's selection
            // (This is handled in VM_NpcSelectionBar now for simplicity)
            // It could also be done here if VM_NpcSelection instances were longer-lived or managed differently.
        }

        private void Initialize(INpcGetter npcGetter)
        {
            DisplayName = npcGetter.Name?.String ?? npcGetter.EditorID ?? npcGetter.FormKey.ToString();

            try
            {
                var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcGetter.FormKey).ToArray();
                if (!contexts.Any()) return;

                var baseContext = contexts.Last(); 

                // Add the original mod defining the NPC if it's not already implicitly included
                // (Though ResolveAllContexts should include it)
                 AppearanceMods.Add(npcGetter.FormKey.ModKey);


                // Check every mod that provides this NPC record
                foreach (var context in contexts)
                {
                    if (ModifiesAppearance(baseContext.Record, context.Record)) // Compare against the winner
                    {
                         // Check if the mod is enabled in the load order
                        if(_environmentStateProvider.LoadOrder.ContainsKey(context.ModKey))
                        {
                            if (!AppearanceMods.Contains(context.ModKey))
                            {
                                AppearanceMods.Add(context.ModKey);
                            }
                        }
                    }
                }

                // Set initial selected mod based on winning override, before checking consistency provider
                SelectedAppearanceMod = baseContext.ModKey.ToString();

                 // Now retrieve the potentially user-overridden selection
                SelectedAppearanceMod = _consistencyProvider.GetSelectedMod(npcGetter.FormKey, baseContext.ModKey); // Pass winner as default

            }
            catch (Exception ex)
            {
                // Log error during initialization for this specific NPC
                 System.Diagnostics.Debug.WriteLine($"Error initializing appearance mods for {DisplayName}: {ex.Message}");
                 // Potentially clear AppearanceMods or set an error state?
            }
        }
        
        private void Initialize(FormKey npcFormKey)
        {
            DisplayName = npcFormKey.ToString();
            AppearanceMods.Add(npcFormKey.ModKey);
            // Set initial selected mod based on winning override, before checking consistency provider
            SelectedAppearanceMod = npcFormKey.ModKey.ToString();
            SelectedAppearanceMod = _consistencyProvider.GetSelectedMod(npcFormKey, npcFormKey.ModKey); // Pass winner as default
        }


        // Comparison logic - Ensure HeadParts comparison is robust
        private bool ModifiesAppearance(INpcGetter baseRecord, INpcGetter currentRecord)
        { 
             // If it's the same record instance, it doesn't modify itself relative to itself
            if (ReferenceEquals(baseRecord, currentRecord)) return false;
             // If it's the exact same mod key, it's the same override (unless comparing base game vs override)
            // Let's focus on content comparison.

            Npc.TranslationMask appearanceMask = new Npc.TranslationMask(defaultOn: false)
            {
                FaceMorph = true,
                FaceParts = true,
                HairColor = true,
                // HeadParts equality testing might be tricky. Let's compare manually.
                HeadTexture = true,
                TextureLighting = true, // Sometimes contains facegen refs
                TintLayers = true,
                WornArmor = true, // Worn Armor can affect appearance significantly
                Height = true, // Include height/weight
                Weight = true
            };

             // Explicit HeadParts comparison (order matters in game, but for difference detection, content might be enough)
            bool headPartsEqual = baseRecord.HeadParts.Count == currentRecord.HeadParts.Count &&
                                  baseRecord.HeadParts.All(hp => currentRecord.HeadParts.Contains(hp)); // Simple contains check


            // Use the mask for other properties. Return true if they are NOT equal OR headparts/tints are NOT equal.
            return !baseRecord.Equals(currentRecord, appearanceMask) || !headPartsEqual;
        }
    }
}