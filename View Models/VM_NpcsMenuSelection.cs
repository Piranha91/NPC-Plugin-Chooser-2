using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using DynamicData;
using Microsoft.Build.Tasks.Deployment.ManifestUtilities;
using Microsoft.WindowsAPICodePack.Shell.PropertySystem;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models; // For Settings if needed indirectly
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Splat; // For Locator if needed

namespace NPC_Plugin_Chooser_2.View_Models;

[DebuggerDisplay("{DisplayName}")]
public class VM_NpcsMenuSelection : ReactiveObject
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly VM_NpcSelectionBar _parentMenu;
    private readonly Settings _settings;
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
    [Reactive] public string FormIdString { get; set; }
    private bool _pluginFound = false;
    public bool IsInLoadOrder { get; set; }
    [Reactive] public bool IsUnique { get; set; } 
    
    // Template cache — populated once during initialization
    public bool BaseRecordHasTemplate { get; set; }
    public bool WinningOverrideHasTemplate { get; set; }

    // --- Template source indicators ---

    // Grey T — winning override template source (static, set once during init)
    [Reactive] public bool IsWinningOverrideTemplateSource { get; set; }
    public string WinningOverrideTemplateUsersTooltip { get; set; } = string.Empty;

    // Raw reference data — set once during init, used by recalculation
    public List<(string ModName, FormKey NpcFormKey, string NpcDisplayName)> AppModTemplateReferences { get; set; } = new();

    // Purple/Green T — app-mod template source (reactive, updated on selection change)
    [Reactive] public bool ShowAppModTemplateT { get; set; }
    [Reactive] public bool IsAppModTemplateGreen { get; set; }  // true = green, false = purple
    [Reactive] public string AppModTemplateTooltip { get; set; } = string.Empty;

    // Red ! — template conflict (reactive, updated on selection change)
    [Reactive] public bool HasTemplateConflict { get; set; }
    [Reactive] public string TemplateConflictTooltip { get; set; } = string.Empty;

    // Context menu "Jump to Template Reference" — populated during init
    public ObservableCollection<TemplateReferenceEntry> TemplateReferenceEntries { get; set; } = new();
    [Reactive] public bool HasTemplateReferences { get; set; }

    [Reactive] public string NpcGroupsDisplay { get; set; } = "Groups: None";

    // This property reflects the centrally stored selection
    [Reactive] public string? SelectedAppearanceModName { get; set; }
    [Reactive] public ObservableCollection<VM_ModSetting> AppearanceMods { get; set; } = new();

    // Alternative constructor for NPCs found *only* via mugshots
    public VM_NpcsMenuSelection(FormKey npcFormKey, EnvironmentStateProvider environmentStateProvider,
        VM_NpcSelectionBar parentMenu, Auxilliary aux, Settings settings)
    {
        using (ContextualPerformanceTracer.Trace("NpcMenuEntry.MainConstructor"))
        {
            _environmentStateProvider = environmentStateProvider;
            _parentMenu = parentMenu;
            _aux = aux;
            _settings = settings;
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
        NpcEditorId = npcData.EditorID ?? _defaultEditorID;
        IsInLoadOrder = npcData.IsInLoadOrder;
        IsUnique = npcData.IsUnique;

        UpdateDisplayName();
    }

    public void UpdateGroupDisplay(HashSet<string>? groups)
    {
        if (groups != null && groups.Any())
        {
            NpcGroupsDisplay = $"Groups: {string.Join(", ", groups.OrderBy(g => g))}";
        }
        else
        {
            NpcGroupsDisplay = "Groups: None";
        }
    }

    public void UpdateDisplayName()
    {
        var language = _settings.LocalizationLanguage;
        // Determine base name from localization settings
        bool hasName = false;
        if (language.HasValue && NpcData?.Name != null &&
            NpcData.Name.TryLookup(language.Value, out var localizedName))
        {
            NpcName = localizedName;
            if (_settings.FixGarbledText)
            {
                NpcName = Auxilliary.FixMojibake(NpcName);
            }
            hasName = true;
        }
        else if (NpcData?.Name?.String != null)
        {
            NpcName = NpcData.Name.String;
            if (_settings.FixGarbledText)
            {
                NpcName = Auxilliary.FixMojibake(NpcName);
            }
            hasName = true;
        }
        else
        {
            NpcName = _defaultNpcName;
        }

        // Build the display string from parts based on settings
        var parts = new List<string>();
        var separator = $" {_settings.NpcListSeparator} ";

        if (_settings.ShowNpcNameInList)
        {
            parts.Add(hasName ? NpcName : (NpcEditorId != _defaultEditorID ? NpcEditorId : NpcFormKeyString));
        }

        if (_settings.ShowNpcEditorIdInList && NpcEditorId != _defaultEditorID)
        {
            parts.Add(NpcEditorId);
        }

        if (_settings.ShowNpcFormKeyInList)
        {
            parts.Add(NpcFormKeyString);
        }

        if (_settings.ShowNpcFormIdInList && !string.IsNullOrWhiteSpace(FormIdString))
        {
            parts.Add(FormIdString);
        }

        if (parts.Any())
        {
            // Use Distinct() to avoid showing the same identifier twice (e.g., if Name and EditorID are the same)
            DisplayName = string.Join(separator, parts.Distinct());
        }
        else // Fallback in case all are unchecked
        {
            DisplayName = hasName ? NpcName : (NpcEditorId != _defaultEditorID ? NpcEditorId : NpcFormKeyString);
        }
    }
}

/// <summary>
/// Lightweight entry for the "Jump to Template Reference" context menu.
/// </summary>
public record TemplateReferenceEntry(string DisplayText, FormKey NpcFormKey);

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
            if (!aInLoadOrder && bInLoadOrder) return 1;

            // --- Both in load order → sort by FormID ------------------------------
            if (aInLoadOrder) // (true for both, since the first two returns are gone)
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