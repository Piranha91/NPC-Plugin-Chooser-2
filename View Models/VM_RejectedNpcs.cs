using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using NPC_Plugin_Chooser_2.BackEnd;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Shared base for the two node kinds in the Rejected NPCs tree. The detail pane binds to one
/// of these regardless of which level was clicked, so both levels answer the same four
/// questions: what is this, where did it come from, why is it here, and what identifies it.
/// </summary>
public abstract class VM_RejectedNode : ReactiveObject
{
    /// <summary>Bound TwoWay to TreeViewItem.IsExpanded so filtering can auto-open matched mods.</summary>
    [Reactive] public bool IsExpanded { get; set; }

    public abstract string DisplayText { get; }
    public abstract string DetailTitle { get; }
    public abstract string DetailSubtitle { get; }
    public abstract string DetailReason { get; }
    public abstract string DetailMeta { get; }
}

/// <summary>One discarded NPC — a leaf of the tree.</summary>
public sealed class VM_RejectedNpcEntry : VM_RejectedNode
{
    private readonly string _searchBlob;

    public VM_RejectedNpcEntry(RejectedNpcRecord record, string modName)
    {
        Label = record.Label;
        NpcIdentifier = record.NpcIdentifier;
        EditorId = record.EditorId;
        FormKey = record.FormKey;
        Reason = record.Reason;
        RawText = record.RawText;
        ModName = string.IsNullOrWhiteSpace(record.ModName) ? modName : record.ModName;

        _searchBlob = string.Join(Environment.NewLine, Label, NpcIdentifier, Reason).ToLowerInvariant();
    }

    public string Label { get; }
    public string NpcIdentifier { get; }
    public string EditorId { get; }
    public string FormKey { get; }
    public string Reason { get; }
    public string RawText { get; }
    public string ModName { get; }

    public bool Matches(string lowercaseFilter) => _searchBlob.Contains(lowercaseFilter, StringComparison.Ordinal);

    public override string DisplayText => Label;
    public override string DetailTitle => Label;
    public override string DetailSubtitle => "Rejected from: " + ModName;
    public override string DetailReason => Reason;

    public override string DetailMeta
    {
        get
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(EditorId)) sb.AppendLine("EditorID: " + EditorId);
            if (!string.IsNullOrWhiteSpace(FormKey)) sb.AppendLine("FormKey: " + FormKey);
            if (!string.IsNullOrWhiteSpace(NpcIdentifier) && NpcIdentifier != Label)
            {
                sb.AppendLine("Logged as: " + NpcIdentifier);
            }

            sb.AppendLine();
            sb.Append("Log line:");
            sb.AppendLine();
            sb.Append(RawText);
            return sb.ToString();
        }
    }
}

/// <summary>One mod's log file — a root node holding its rejected NPCs.</summary>
public sealed class VM_RejectedModGroup : VM_RejectedNode
{
    private readonly string _lowercaseModName;
    private IReadOnlyList<VM_RejectedNpcEntry> _entries;

    public VM_RejectedModGroup(string modName, string logFilePath, IReadOnlyList<VM_RejectedNpcEntry> allEntries)
    {
        ModName = modName;
        LogFilePath = logFilePath;
        AllEntries = allEntries;
        _entries = allEntries;
        _lowercaseModName = modName.ToLowerInvariant();
    }

    public string ModName { get; }
    public string LogFilePath { get; }
    public IReadOnlyList<VM_RejectedNpcEntry> AllEntries { get; }

    /// <summary>
    /// The filtered subset bound to the TreeViewItem. Swapped wholesale rather than mutated —
    /// a mod can hold thousands of entries, and replacing the list is one notification instead
    /// of thousands.
    /// </summary>
    public IReadOnlyList<VM_RejectedNpcEntry> Entries
    {
        get => _entries;
        private set => this.RaiseAndSetIfChanged(ref _entries, value);
    }

    /// <summary>Applies a filter and reports whether this mod still has anything to show.</summary>
    public bool ApplyFilter(string lowercaseFilter)
    {
        if (lowercaseFilter.Length == 0 || _lowercaseModName.Contains(lowercaseFilter, StringComparison.Ordinal))
        {
            Entries = AllEntries;
        }
        else
        {
            Entries = AllEntries.Where(e => e.Matches(lowercaseFilter)).ToList();
        }

        this.RaisePropertyChanged(nameof(DisplayText));
        this.RaisePropertyChanged(nameof(DetailSubtitle));
        this.RaisePropertyChanged(nameof(DetailReason));
        return Entries.Count > 0;
    }

    public override string DisplayText => $"{ModName}  ({Entries.Count})";
    public override string DetailTitle => ModName;

    public override string DetailSubtitle => Entries.Count == AllEntries.Count
        ? $"{AllEntries.Count:N0} rejected NPC(s)"
        : $"{Entries.Count:N0} of {AllEntries.Count:N0} rejected NPC(s) match the filter";

    public override string DetailReason
    {
        get
        {
            if (Entries.Count == 0) return "No entries match the current filter.";

            var sb = new StringBuilder("Reasons:");
            foreach (var group in Entries.GroupBy(e => e.Reason).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine();
                sb.Append($"  {group.Count():N0} ×  {group.Key}");
            }

            return sb.ToString();
        }
    }

    public override string DetailMeta => "Log file:" + Environment.NewLine + LogFilePath;
}

/// <summary>
/// Backs the Settings &gt; Mod Import Settings &gt; Rejected NPCs panel: a Mod -&gt; NPC tree of
/// everything the mod analysis discarded, with the reason for the selected node beside it.
///
/// The source of truth is the "Rejected NPCs" folder next to the .exe, rewritten per mod during
/// <c>VM_ModSetting.RefreshNpcLists</c>. Nothing is read until the panel is first expanded (the
/// folder can hold tens of thousands of lines), and Refresh re-reads it after a re-scan.
/// </summary>
public sealed class VM_RejectedNpcs : ReactiveObject
{
    private static readonly IReadOnlyList<VM_RejectedModGroup> NoGroups = Array.Empty<VM_RejectedModGroup>();

    // Above this many matches, auto-expanding every matched mod would realize more rows than the
    // tree can usefully show, so matched mods are left collapsed and only their counts update.
    private const int AutoExpandEntryLimit = 500;

    private readonly string _logDirectory;
    private IReadOnlyList<VM_RejectedModGroup> _allGroups = NoGroups;
    private IReadOnlyList<VM_RejectedModGroup> _groups = NoGroups;

    public VM_RejectedNpcs(string logDirectory = null)
    {
        _logDirectory = logDirectory ?? RejectedNpcLogParser.DefaultLogDirectory;

        RefreshCommand = ReactiveCommand.CreateFromTask(() => LoadAsync(force: true));
        RefreshCommand.ThrownExceptions.Subscribe(ex =>
            Debug.WriteLine($"Rejected NPCs refresh failed: {ExceptionLogger.GetExceptionStack(ex)}"));

        OpenFolderCommand = ReactiveCommand.Create(() => Auxilliary.OpenFolder(_logDirectory));

        this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => node != null)
            .ToPropertyEx(this, x => x.HasSelection);

        this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => node == null)
            .ToPropertyEx(this, x => x.HasNoSelection);

        // Delay=250 on the TextBox binding already coalesces typing, so filtering straight off the
        // property keeps the collection swap on the UI thread where the tree expects it.
        this.WhenAnyValue(x => x.FilterText)
            .Skip(1)
            .Subscribe(_ => ApplyFilter());
    }

    [Reactive] public string FilterText { get; set; } = string.Empty;
    [Reactive] public VM_RejectedNode SelectedNode { get; set; }
    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public string StatusText { get; private set; } = "Not loaded yet.";

    [ObservableAsProperty] public bool HasSelection { get; }
    [ObservableAsProperty] public bool HasNoSelection { get; }

    public IReadOnlyList<VM_RejectedModGroup> Groups
    {
        get => _groups;
        private set => this.RaiseAndSetIfChanged(ref _groups, value);
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenFolderCommand { get; }

    public bool HasLoaded { get; private set; }

    /// <summary>Loads on first use; subsequent calls are no-ops until <see cref="RefreshCommand"/>.</summary>
    public Task EnsureLoadedAsync() => HasLoaded ? Task.CompletedTask : LoadAsync(force: false);

    /// <summary>
    /// Discards the parsed snapshot after the log folder has been rewritten by a re-scan.
    ///
    /// <para>A panel that has already been opened re-reads immediately, because its rows are on
    /// screen and would otherwise keep naming mods that the re-scan just removed. One that has
    /// never been opened is only marked stale, so a re-scan does not pay for a read nobody has
    /// asked for — the same laziness <see cref="EnsureLoadedAsync"/> exists to preserve.</para>
    ///
    /// <para>Fire-and-forget by design: the caller is a refresh pipeline that should not block on
    /// a cosmetic reload, and <see cref="LoadAsync"/> already routes its own failures to the
    /// status line.</para>
    /// </summary>
    public void Invalidate()
    {
        if (!HasLoaded)
        {
            return;
        }

        HasLoaded = false;
        LoadAsync(force: true).ContinueWith(
            t => Debug.WriteLine($"Rejected NPCs invalidation reload failed: {ExceptionLogger.GetExceptionStack(t.Exception!)}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public async Task LoadAsync(bool force)
    {
        if (IsLoading || (HasLoaded && !force))
        {
            return;
        }

        IsLoading = true;
        StatusText = "Reading logs…";
        SelectedNode = null;
        var directory = _logDirectory;

        try
        {
            var groups = await Task.Run(() => ReadGroups(directory));

            _allGroups = groups;
            HasLoaded = true;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _allGroups = NoGroups;
            Groups = NoGroups;
            StatusText = "Could not read the Rejected NPCs folder: " + ex.Message;
            Debug.WriteLine($"Rejected NPCs load failed: {ExceptionLogger.GetExceptionStack(ex)}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static IReadOnlyList<VM_RejectedModGroup> ReadGroups(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return NoGroups;
        }

        var groups = new List<VM_RejectedModGroup>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.txt"))
        {
            List<RejectedNpcRecord> records;
            try
            {
                records = RejectedNpcLogParser.ParseFile(file);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not parse rejection log '{file}': {ExceptionLogger.GetExceptionStack(ex)}");
                continue;
            }

            if (records.Count == 0)
            {
                continue;
            }

            // The file name is the path-safe form of the mod's display name, so prefer the name the
            // log lines themselves recorded and fall back to the file name.
            var modName = records.Select(r => r.ModName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                          ?? Path.GetFileNameWithoutExtension(file);

            var entries = records
                .Select(r => new VM_RejectedNpcEntry(r, modName))
                .OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            groups.Add(new VM_RejectedModGroup(modName, file, entries));
        }

        return groups.OrderBy(g => g.ModName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ApplyFilter()
    {
        if (!HasLoaded)
        {
            return;
        }

        var filter = (FilterText ?? string.Empty).Trim().ToLowerInvariant();
        var matched = new List<VM_RejectedModGroup>();
        long shownEntries = 0;

        foreach (var group in _allGroups)
        {
            if (group.ApplyFilter(filter))
            {
                matched.Add(group);
                shownEntries += group.Entries.Count;
            }
        }

        var autoExpand = filter.Length > 0 && shownEntries <= AutoExpandEntryLimit;
        foreach (var group in matched)
        {
            group.IsExpanded = autoExpand;
        }

        Groups = matched;

        // A selection that filtered out would leave the detail pane describing a hidden node.
        if (SelectedNode is VM_RejectedModGroup selectedGroup && !matched.Contains(selectedGroup))
        {
            SelectedNode = null;
        }
        else if (SelectedNode is VM_RejectedNpcEntry selectedEntry &&
                 !matched.Any(g => g.Entries.Contains(selectedEntry)))
        {
            SelectedNode = null;
        }

        var totalEntries = _allGroups.Sum(g => (long)g.AllEntries.Count);
        if (_allGroups.Count == 0)
        {
            StatusText = Directory.Exists(_logDirectory)
                ? "No rejection logs found — no NPCs were discarded during mod analysis."
                : "No 'Rejected NPCs' folder yet. It is written next to the application when your mods are analyzed.";
        }
        else if (filter.Length == 0)
        {
            StatusText = $"{totalEntries:N0} rejected NPC(s) across {_allGroups.Count:N0} mod(s).";
        }
        else
        {
            StatusText = $"Showing {shownEntries:N0} of {totalEntries:N0} NPC(s) in " +
                         $"{matched.Count:N0} of {_allGroups.Count:N0} mod(s).";
        }
    }
}
