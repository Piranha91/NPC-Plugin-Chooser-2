// [VM_NpcsMenuSelection.cs] - Updated
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
        private readonly NpcConsistencyProvider _consistencyProvider;

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
        public VM_NpcsMenuSelection(FormKey npcFormKey, EnvironmentStateProvider environmentStateProvider, NpcConsistencyProvider consistencyProvider)
        {
            _environmentStateProvider = environmentStateProvider;
            _consistencyProvider = consistencyProvider;
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
                        if (System.IO.File.Exists(candidatePath))
                        {
                            try
                            {
                                var mod = SkyrimMod.CreateFromBinaryOverlay(candidatePath, SkyrimRelease.SkyrimSE);
                                var npcGetter = mod.Npcs.FirstOrDefault(x => x.FormKey.Equals(NpcFormKey));
                                if (npcGetter != null)
                                {
                                    _pluginFound = true;
                                    UpdateDisplayName(npcGetter);
                                }
                            }
                            catch
                            {
                                // pass
                            }
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
}