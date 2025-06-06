﻿// [VM_NpcsMenuSelection.cs] - Updated
using System;
using System.Collections.ObjectModel;
using System.Linq;
using DynamicData;
using Microsoft.Build.Tasks.Deployment.ManifestUtilities;
using Microsoft.WindowsAPICodePack.Shell.PropertySystem;
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
        private readonly VM_NpcSelectionBar _parentMenu;

        public INpcGetter? NpcGetter { get; set; } // Keep the original record (null if from mugshot only)
        public FormKey NpcFormKey { get; }
 
        private static string _defaultDisplayName = "Unnamed NPC";
        [Reactive] public string DisplayName { get; set; } = _defaultDisplayName;
        private static string _defaultNpcName = "No Name Assigned";
        [Reactive] public string NpcName { get; set; } = _defaultNpcName;
        private static string _defaultEditorID = "No Editor ID";
        [Reactive] public string NpcEditorId { get; set; } = _defaultEditorID;
        private static string _defaultFormKeyString = "No FormKey";
        public string NpcFormKeyString { get; } = _defaultFormKeyString;
        private bool _pluginFound = false;

        // This property reflects the centrally stored selection
        [Reactive] public string? SelectedAppearanceModName { get; set; }
        [Reactive] public ObservableCollection<VM_ModSetting> AppearanceMods { get; set; } = new();

        // Alternative constructor for NPCs found *only* via mugshots
        public VM_NpcsMenuSelection(FormKey npcFormKey, EnvironmentStateProvider environmentStateProvider, VM_NpcSelectionBar parentMenu)
        {
            _environmentStateProvider = environmentStateProvider;
            _parentMenu = parentMenu;
            NpcFormKey = npcFormKey;
            NpcFormKeyString = npcFormKey.ToString();
            NpcGetter = null; // No getter available
            
            var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcFormKey);
            var sourceContext = contexts.LastOrDefault();
            if (sourceContext != null)
            {
                var sourceGetter = sourceContext.Record;
                if (sourceGetter != null)
                {
                    _pluginFound = true;
                    UpdateDisplayName(sourceGetter);
                }
            }
            // Get initial selection from the consistency provider
            // SelectedAppearanceModName is now set within Initialize or Initialize(FormKey)
        }

        public void Update(VM_ModSetting modSetting)
        {
            AppearanceMods.Add(modSetting);
            if (!_pluginFound)
            {
                foreach (var subDir in modSetting.CorrespondingFolderPaths)
                {
                    foreach (var plugin in modSetting.CorrespondingModKeys)
                    {
                        var candidatePath = System.IO.Path.Combine(subDir, plugin.ToString());
                        
                        if (!_parentMenu.NpcGetterCache.ContainsKey(candidatePath) && System.IO.File.Exists(candidatePath))
                        {
                            try
                            {
                                var mod = SkyrimMod.CreateFromBinaryOverlay(candidatePath, SkyrimRelease.SkyrimSE);
                                Dictionary<FormKey, INpcGetter> subCache = new();
                                _parentMenu.NpcGetterCache.Add(candidatePath, subCache);

                                foreach (var npc in mod.Npcs)
                                {
                                    subCache.Add(npc.FormKey, npc);
                                }
                            }
                            catch
                            {
                                // pass
                            }
                        }
                        
                        if (_parentMenu.NpcGetterCache.ContainsKey(candidatePath) && _parentMenu.NpcGetterCache[candidatePath].TryGetValue(NpcFormKey, out var npcGetter))
                        {
                            _pluginFound = true;
                            UpdateDisplayName(npcGetter);
                        }
                    }
                }
            }
        }

        private void UpdateDisplayName(INpcGetter sourceGetter)
        {
            NpcGetter = sourceGetter;
            NpcName = sourceGetter.Name?.String ?? _defaultNpcName;
            NpcEditorId = sourceGetter.EditorID ?? _defaultEditorID;

            if (NpcName != _defaultNpcName)
            {
                DisplayName = NpcName;
            }
            else if (NpcEditorId != _defaultEditorID)
            {
                DisplayName = NpcEditorId;
            }
            else
            {
                DisplayName = NpcFormKeyString;
            }
        }
    }
    
    public static class NpcListExtensions
    {
        /// <summary>
        /// Sorts the list in place:
        ///   1) NPCs whose FormKey belongs to the current load order first (by FormID)
        ///   2) Then NPCs not in the load order (by ModKey name, then by IDString)
        /// </summary>
        /// <param name="npcs">The list to reorder in place.</param>
        /// <param name="aux">
        /// Helper that converts a FormKey to its full FormID string, returning "" if the
        /// FormKey is not found in the current load order.
        /// </param>
        public static void SortByFormId(this List<VM_NpcsMenuSelection> npcs,
                                         Auxilliary aux)
        {
            if (npcs is null) throw new ArgumentNullException(nameof(npcs));
            if (aux  is null) throw new ArgumentNullException(nameof(aux));

            npcs.Sort((a, b) =>
            {
                // --- Presence test ------------------------------------------------------
                var aFormId = aux.FormKeyToFormIDString(a.NpcFormKey);
                var bFormId = aux.FormKeyToFormIDString(b.NpcFormKey);

                bool aInLoadOrder = aFormId.Length != 0;
                bool bInLoadOrder = bFormId.Length != 0;

                // Partition: load-order entries first
                if (aInLoadOrder && !bInLoadOrder) return -1;
                if (!aInLoadOrder && bInLoadOrder) return  1;

                // --- Both in load order → sort by FormID ------------------------------
                if (aInLoadOrder)                       // (true for both, since the first two returns are gone)
                    return string.Compare(aFormId, bFormId, StringComparison.Ordinal);

                // --- Both NOT in load order -------------------------------------------
                int modCmp = string.Compare(
                    a.NpcFormKey.ModKey.FileName,
                    b.NpcFormKey.ModKey.FileName,
                    StringComparison.OrdinalIgnoreCase);

                if (modCmp != 0) return modCmp;

                return string.Compare(
                    a.NpcFormKey.IDString(),
                    b.NpcFormKey.IDString(),
                    StringComparison.OrdinalIgnoreCase);
            });
        }
    }
}