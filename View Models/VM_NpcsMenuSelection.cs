// [VM_NpcsMenuSelection.cs] - Updated
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
    public class VM_NpcsMenuSelection : ReactiveObject
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly NpcConsistencyProvider _consistencyProvider;

        public INpcGetter? NpcGetter { get; } // Keep the original record (null if from mugshot only)
        public FormKey NpcFormKey { get; }

        [Reactive] public string DisplayName { get; set; } = "Unnamed NPC";
        [Reactive] public string NpcName { get; set; } = "No Name Assigned";
        [Reactive] public string NpcEditorId { get; set; } = "No Editor ID";
        public string NpcFormKeyString { get;  } = "No FormKey";
        
        // Stores the ModKeys providing appearance data (from plugins)
        public ObservableCollection<ModKey> AppearanceMods { get; } = new();
        // Holds viewmodels for appearance sources (plugins + potentially mugshots) - managed by VM_NpcSelectionBar
        // public ObservableCollection<VM_NpcsMenuMugshot> AppearanceSources { get; } = new(); // Maybe add later if needed directly here

        // This property reflects the centrally stored selection
        [Reactive] public string? SelectedAppearanceMod { get; set; }

        // Constructor for NPCs found in load order
        public VM_NpcsMenuSelection(INpcGetter npcGetter, EnvironmentStateProvider environmentStateProvider, NpcConsistencyProvider consistencyProvider)
        {
            _environmentStateProvider = environmentStateProvider;
            _consistencyProvider = consistencyProvider;
            NpcFormKey = npcGetter.FormKey;
            NpcFormKeyString = NpcFormKey.ToString();
            NpcGetter = npcGetter; // Store the getter

            Initialize(npcGetter); // Initialize based on the record

            // Get initial selection from the consistency provider
            // SelectedAppearanceMod is now set within Initialize or Initialize(FormKey)
        }

        // Alternative constructor for NPCs found *only* via mugshots
        public VM_NpcsMenuSelection(FormKey npcFormKey, EnvironmentStateProvider environmentStateProvider, NpcConsistencyProvider consistencyProvider)
        {
            _environmentStateProvider = environmentStateProvider;
            _consistencyProvider = consistencyProvider;
            NpcFormKey = npcFormKey;
            NpcFormKeyString = npcFormKey.ToString();
            NpcGetter = null; // No getter available

            Initialize(npcFormKey); // Initialize based on the FormKey

            // Get initial selection from the consistency provider
            // SelectedAppearanceMod is now set within Initialize or Initialize(FormKey)
        }

        private void Initialize(INpcGetter npcGetter)
        {
            DisplayName = npcGetter.Name?.String ?? npcGetter.EditorID ?? npcGetter.FormKey.ToString();
            if (npcGetter.Name != null && npcGetter.Name.String != null)
            {
                NpcName = npcGetter.Name.String;
            }

            if (npcGetter.EditorID != null)
            {
                NpcEditorId = npcGetter.EditorID;
            }
            
            try
            {
                var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcGetter.FormKey).ToArray();
                if (!contexts.Any())
                {
                    // If no contexts, still add the base modkey if getter exists
                    AppearanceMods.Add(npcGetter.FormKey.ModKey);
                    SelectedAppearanceMod = _consistencyProvider.GetSelectedMod(npcGetter.FormKey);
                    return;
                }


                var baseContext = contexts.Last(); // Original Npc Record

                // Add the base record's modkey first
                if (!AppearanceMods.Contains(baseContext.ModKey))
                {
                    AppearanceMods.Add(baseContext.ModKey);
                }


                // Check every mod that provides this NPC record
                foreach (var context in contexts.Reverse().Skip(1)) // Iterate overrides before the winner
                {
                     // Only compare previous overrides against the winner
                    if (ModifiesAppearance(baseContext.Record, context.Record))
                    {
                        // Check if the mod is enabled in the load order
                        if (_environmentStateProvider.LoadOrder.ContainsKey(context.ModKey))
                        {
                            if (!AppearanceMods.Contains(context.ModKey))
                            {
                                AppearanceMods.Add(context.ModKey);
                            }
                        }
                    }
                }

                // Ensure the original defining mod is included if different from winner
                 if (!AppearanceMods.Contains(npcGetter.FormKey.ModKey) && _environmentStateProvider.LoadOrder.ContainsKey(npcGetter.FormKey.ModKey))
                 {
                      AppearanceMods.Add(npcGetter.FormKey.ModKey);
                 }


                // Retrieve the potentially user-overridden selection, using winner as default
                SelectedAppearanceMod = _consistencyProvider.GetSelectedMod(npcGetter.FormKey);

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing appearance mods for {DisplayName} ({NpcFormKey}): {ex.Message}");
                // Ensure base mod is added as fallback
                if (!AppearanceMods.Any() && _environmentStateProvider.LoadOrder.ContainsKey(npcGetter.FormKey.ModKey))
                {
                     AppearanceMods.Add(npcGetter.FormKey.ModKey);
                     SelectedAppearanceMod = _consistencyProvider.GetSelectedMod(npcGetter.FormKey);
                }
                else if (!AppearanceMods.Any())
                {
                     // Absolute fallback if even base mod fails
                     SelectedAppearanceMod = "Error";
                }

            }
        }

        // Initialize for mugshot-only NPC
        private void Initialize(FormKey npcFormKey)
        {
            // Try to resolve name from LinkCache if possible, otherwise use FormKey string
            // This might be slow if done for many mugshot-only NPCs. Consider optimizing if needed.
            string? resolvedName = null;
            try {
                if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey, out INpcGetter? npcGetter))
                {
                    resolvedName = npcGetter?.Name?.String;
                }
            } catch { /* Ignore resolution errors */ }

            DisplayName = resolvedName ?? npcFormKey.ToString(); // Use resolved name or fallback to FormKey

            // Add the ModKey derived from the FormKey as the *only* known source initially.
            // The corresponding VM_NpcsMenuMugshot will be created later by VM_NpcSelectionBar
            if (!AppearanceMods.Contains(npcFormKey.ModKey))
            {
                 AppearanceMods.Add(npcFormKey.ModKey);
            }
        }


        // Comparison logic - Ensure HeadParts comparison is robust
        public static bool ModifiesAppearance(INpcGetter baseRecord, INpcGetter currentRecord)
        {
            // If it's the same record instance, it doesn't modify itself relative to itself
            if (ReferenceEquals(baseRecord, currentRecord)) return false;

            // Quick check for simple cases
            if (baseRecord.Equals(currentRecord)) return false;


            // Use a mask for efficient comparison of relevant fields
             Npc.TranslationMask appearanceMask = new(defaultOn: false)
            {
                // Visual appearance fields
                HeadParts = true, // Note: Order matters in-game, but Mutagen's Equals might handle sequence equality. Test needed.
                FaceMorph = true,
                FaceParts = true,
                HairColor = true,
                HeadTexture = true,
                WornArmor = true, // Affects appearance
                Height = true,
                Weight = true,
                TintLayers = true, // Complex comparison, may need manual check if mask fails
                // Potentially add others if needed: DefaultOutfit, SleepingOutfit?
            };

             // Compare using the mask
            if (!baseRecord.Equals(currentRecord, appearanceMask))
            {
                return true;
            }

            // Explicit check for TintLayers if mask comparison is insufficient (SequenceEqual checks order and content)
             if (!baseRecord.TintLayers.SequenceEqual(currentRecord.TintLayers))
             {
                 return true;
             }

            // If mask comparison and TintLayers are equal, consider them non-modifying for appearance selection
            return false;
        }
    }
}