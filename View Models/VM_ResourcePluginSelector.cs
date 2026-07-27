using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Views;
using ReactiveUI;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Backs the "Set Resource-Only Plugins" dialog. Each row carries two decisions:
/// whether the plugin is resource-only (provides no NPCs of its own), and whether its
/// records may be merged into the output patch.
///
/// <para>Merge-in is only editable for resource-only rows. A plugin that provides NPCs is
/// expected to stay in the user's load order, so its records should be referenced rather
/// than copied; those rows mirror the mod's own Merge Dependencies toggle, disabled. The
/// merge value shown for an untouched row is the live default from
/// <see cref="MergeEligibility"/>, and only rows the user actually changes are written back
/// as overrides — so the defaults keep tracking the owning mod's setting.</para>
/// </summary>
public class VM_ResourcePluginSelector : ReactiveObject, IDisposable
{
    // --- Event for closing ---
    public event Action RequestClose = delegate { };

    // --- Properties ---
    public ObservableCollection<VM_SelectableMod> SelectablePlugins { get; }
    public bool HasChanged { get; private set; } = false;

    // --- Commands ---
    public ReactiveCommand<Unit, Unit> OKCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    // --- Private Fields ---
    private readonly HashSet<ModKey> _initialSelection;
    private readonly Dictionary<ModKey, bool> _initialOverrides;
    private readonly ModSetting _owner;
    private readonly IReadOnlyDictionary<ModKey, ModSetting>? _npcProvidingOwnersByPlugin;
    private readonly CompositeDisposable _disposables = new();

    // --- Constructor ---
    public VM_ResourcePluginSelector(IEnumerable<ModKey> allModKeys,
        HashSet<ModKey> currentlySelectedResourceModKeys,
        ModSetting owner,
        IReadOnlyDictionary<ModKey, ModSetting>? npcProvidingOwnersByPlugin = null)
    {
        _initialSelection = new HashSet<ModKey>(currentlySelectedResourceModKeys);
        _owner = owner;
        _npcProvidingOwnersByPlugin = npcProvidingOwnersByPlugin;
        _initialOverrides = new Dictionary<ModKey, bool>(owner.PluginMergeInOverrides ?? new Dictionary<ModKey, bool>());

        SelectablePlugins = new ObservableCollection<VM_SelectableMod>(
            allModKeys.Select(modKey => new VM_SelectableMod(modKey, _initialSelection.Contains(modKey)))
                .OrderBy(vm => vm.DisplayText)
        );

        foreach (var row in SelectablePlugins)
        {
            ApplyMergeStateFor(row);

            // Ticking/unticking resource-only flips the row between "mirrors the mod" and
            // "independently settable", and re-derives the default it starts from.
            row.WhenAnyValue(x => x.IsSelected)
                .Skip(1)
                .Subscribe(_ => ApplyMergeStateFor(row))
                .DisposeWith(_disposables);
        }

        OKCommand = ReactiveCommand.Create(ExecuteOk).DisposeWith(_disposables);
        CancelCommand = ReactiveCommand.Create(ExecuteCancel).DisposeWith(_disposables);
    }

    public HashSet<ModKey> GetSelectedModKeys()
    {
        return new HashSet<ModKey>(
            SelectablePlugins.Where(vm => vm.IsSelected)
                .Select(vm => vm.ModKey)
        );
    }

    /// <summary>
    /// The per-plugin merge overrides to persist: only rows that are resource-only AND whose
    /// merge value differs from the live default. Storing nothing for an agreeing row is what
    /// keeps the default following the owning mod's Merge Dependencies toggle over time.
    /// </summary>
    public Dictionary<ModKey, bool> GetMergeInOverrides()
    {
        var overrides = new Dictionary<ModKey, bool>();
        foreach (var row in SelectablePlugins)
        {
            if (!row.IsSelected) continue; // non-resource rows always mirror the mod; never stored
            if (row.IsMergedIn == DefaultMergeFor(row.ModKey, isResourceOnly: true)) continue;
            overrides[row.ModKey] = row.IsMergedIn;
        }
        return overrides;
    }

    /// <summary>
    /// The merge value a row would take with no explicit override — rule 2/3/4 of
    /// <see cref="MergeEligibility"/>, evaluated against the row's CURRENT resource-only state
    /// rather than the saved one, so the dialog previews the effect of ticking the box.
    /// </summary>
    private bool DefaultMergeFor(ModKey plugin, bool isResourceOnly)
    {
        // Delegate to the same resolver the patcher uses, with the row's live resource-only
        // state substituted in, so the dialog can never preview a value the patch run won't
        // apply. Explicit overrides are stripped first — this method IS the default.
        var probe = new ModSetting
        {
            DisplayName = _owner.DisplayName,
            MergeInDependencyRecords = _owner.MergeInDependencyRecords,
            CorrespondingModKeys = new List<ModKey> { plugin },
            ResourceOnlyModKeys = isResourceOnly ? new HashSet<ModKey> { plugin } : new HashSet<ModKey>(),
            PluginMergeInOverrides = new Dictionary<ModKey, bool>(),
        };

        return MergeEligibility.IsPluginMergeEligible(probe, plugin, _npcProvidingOwnersByPlugin);
    }

    private string BuildMergeToolTip(ModKey plugin, bool isResourceOnly)
    {
        if (!isResourceOnly)
        {
            return $"'{plugin.FileName}' provides NPCs, so it is expected to stay in your load order and its " +
                   "records are referenced rather than copied. This follows the mod's own 'Merge Dependencies' " +
                   "setting. Mark it resource-only to control merging separately.";
        }

        if (_npcProvidingOwnersByPlugin != null &&
            _npcProvidingOwnersByPlugin.TryGetValue(plugin, out var npcProvidingOwner) &&
            npcProvidingOwner != null &&
            !MergeEligibility.IsSameModEntry(npcProvidingOwner, _owner))
        {
            return $"Whether records from '{plugin.FileName}' are copied into the output patch. " +
                   $"Defaults to the 'Merge Dependencies' setting of '{npcProvidingOwner.DisplayName}', the mod " +
                   "entry that provides this plugin.";
        }

        return $"Whether records from '{plugin.FileName}' are copied into the output patch. Defaults to on: no " +
               "other mod entry vouches for this plugin being present, so records referencing it would otherwise " +
               "point at a plugin that isn't in your load order.";
    }

    private void ApplyMergeStateFor(VM_SelectableMod row)
    {
        bool isResourceOnly = row.IsSelected;
        row.IsMergeEditable = isResourceOnly;
        row.MergeInToolTip = BuildMergeToolTip(row.ModKey, isResourceOnly);

        // A saved override only applies while the row is still resource-only; a plugin that is
        // no longer a resource has no independent merge state to restore.
        row.IsMergedIn = isResourceOnly && _initialOverrides.TryGetValue(row.ModKey, out var saved)
            ? saved
            : DefaultMergeFor(row.ModKey, isResourceOnly);
    }

    // --- Command Implementations ---

    private void ExecuteOk()
    {
        var finalSelection = GetSelectedModKeys();
        var finalOverrides = GetMergeInOverrides();

        if (!_initialSelection.SetEquals(finalSelection) ||
            finalOverrides.Count != _initialOverrides.Count ||
            finalOverrides.Any(kvp => !_initialOverrides.TryGetValue(kvp.Key, out var prior) || prior != kvp.Value))
        {
            HasChanged = true;
        }

        RequestClose?.Invoke();
    }

    private void ExecuteCancel()
    {
        HasChanged = false; // Ensure no changes are processed
        RequestClose?.Invoke();
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
