// [VM_NpcsMenuSelection.cs] - Updated
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    [DebuggerDisplay("{DisplayName}")]
    public class VM_NpcsMenuSelection : ReactiveObject
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly VM_NpcSelectionBar _parentMenu;
        private readonly Auxilliary _aux;

        public NpcDisplayData? NpcData { get; set; } // Keep the original record (null if from mugshot only)
        public FormKey NpcFormKey { get; }
 
        private static string _defaultDisplayName = "Unnamed NPC";
        [Reactive] public string DisplayName { get; set; } = _defaultDisplayName;
        private static string _defaultNpcName = "No Name Assigned";
        [Reactive] public string NpcName { get; set; } = _defaultNpcName;
        private static string _defaultEditorID = "No Editor ID";
        [Reactive] public string NpcEditorId { get; set; } = _defaultEditorID;
        private static string _defaultFormKeyString = "No FormKey";
        public string NpcFormKeyString { get; } = _defaultFormKeyString;
        public string FormIdString { get; }
        private bool _pluginFound = false;

        // This property reflects the centrally stored selection
        [Reactive] public string? SelectedAppearanceModName { get; set; }
        [Reactive] public ObservableCollection<VM_ModSetting> AppearanceMods { get; set; } = new();

        // Alternative constructor for NPCs found *only* via mugshots
        public VM_NpcsMenuSelection(FormKey npcFormKey, EnvironmentStateProvider environmentStateProvider, VM_NpcSelectionBar parentMenu, Auxilliary aux)
        {
            using (ContextualPerformanceTracer.Trace("NpcMenuEntry.MainConstructor"))
            {
                _environmentStateProvider = environmentStateProvider;
                _parentMenu = parentMenu;
                _aux = aux;
                NpcFormKey = npcFormKey;
                NpcFormKeyString = npcFormKey.ToString();
                DisplayName = npcFormKey.ToString();
                NpcData = null; // Initially null, will be populated by UpdateWithData
            }

            using (ContextualPerformanceTracer.Trace("NpcMenuEntry.GetFormID"))
            {
                FormIdString = _aux.FormKeyToFormIDString(NpcFormKey);
            }
        }
        
        public void UpdateWithData(NpcDisplayData npcData)
        {
            NpcData = npcData;
            NpcName = npcData.Name ?? _defaultNpcName;
            NpcEditorId = npcData.EditorID ?? _defaultEditorID;
            DisplayName = npcData.DisplayName;
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
        public static void SortByFormId(this List<VM_NpcsMenuSelection> npcs)
        {
            if (npcs is null) throw new ArgumentNullException(nameof(npcs));

            npcs.Sort((a, b) =>
            {
                // --- Presence test ------------------------------------------------------
                var aFormId = a.FormIdString;
                var bFormId = b.FormIdString;

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