using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Text;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>One NPC (or unreadable file line) in an EasyNPC import, export or profile update.
/// <paramref name="Current"/> / <paramref name="New"/> are the before/after appearance source; the
/// window names those columns per operation. Empty cells mean "not applicable".</summary>
public sealed record EasyNpcTransferRow(string Status, string Npc, string FormKey, string Current, string New,
    string FacePlugin, string FaceGenSource, string DefaultPlugin, string Note)
{
    /// <summary>Applying this row changes nothing; hidden unless "Show unchanged" is ticked.</summary>
    public bool IsUnchanged { get; init; }

    /// <summary>Several mod entries matched and the first was used; dropped from the operation
    /// when "Use first match" is unticked.</summary>
    public bool IsAmbiguous { get; init; }

    /// <summary>The row is part of the operation (as opposed to skipped or unreadable).</summary>
    public bool IsApplicable { get; init; }
}

/// <summary>Per-operation wording for <see cref="VM_EasyNpcTransferWindow"/>.
/// A null <paramref name="CurrentHeader"/> hides the before column (plain export has none).
/// <paramref name="ChangeLabel"/> completes the summary's "N ..." for rows that will change.</summary>
public sealed record EasyNpcTransferWindowOptions(string Title, string ConfirmVerb, string ChangeLabel,
    string? CurrentHeader, string NewHeader, IReadOnlyList<string> Notes);

/// <summary>
/// Backs the EasyNPC transfer report: one sortable/filterable row per NPC the import, export or
/// profile update touches or skips, with the go/no-go answer owned by the window. Replaced the
/// flat issue list + confirmation message boxes (2026-10-29), which at profile scale listed
/// thousands of raw FormKeys. Unchanged rows are hidden by default so the table opens on what
/// actually happens.
/// </summary>
public sealed class VM_EasyNpcTransferWindow : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly IReadOnlyList<EasyNpcTransferRow> _rows;
    private readonly string _confirmVerb;
    private readonly string _changeLabel;

    public ObservableCollection<EasyNpcTransferRow> FilteredRows { get; } = new();

    [Reactive] public string FilterText { get; set; } = string.Empty;
    [Reactive] public bool ShowUnchanged { get; set; }
    [Reactive] public bool UseFirstMatch { get; set; } = true;
    [Reactive] public string ConfirmText { get; private set; } = string.Empty;
    [Reactive] public bool CanConfirm { get; private set; }

    [Reactive] public string SummaryText { get; private set; } = string.Empty;
    public string NotesText { get; }
    public bool HasNotes { get; }
    public bool HasUnchangedRows { get; }
    public bool HasAmbiguousRows { get; }
    public string? CurrentHeader { get; }
    public string NewHeader { get; }

    public ReactiveCommand<Unit, Unit> CopySheetCommand { get; }

    public VM_EasyNpcTransferWindow(EasyNpcTransferWindowOptions options, IReadOnlyList<EasyNpcTransferRow> rows)
    {
        _rows = rows ?? Array.Empty<EasyNpcTransferRow>();
        _confirmVerb = options.ConfirmVerb;
        _changeLabel = options.ChangeLabel;
        CurrentHeader = options.CurrentHeader;
        NewHeader = options.NewHeader;
        NotesText = string.Join(Environment.NewLine + Environment.NewLine, options.Notes);
        HasNotes = NotesText.Length > 0;
        HasUnchangedRows = _rows.Any(r => r.IsUnchanged);
        HasAmbiguousRows = _rows.Any(r => r.IsAmbiguous);

        ApplyFilter();
        UpdateCounts();

        this.WhenAnyValue(x => x.FilterText)
            .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter())
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.ShowUnchanged)
            .Skip(1)
            .Subscribe(_ => { ApplyFilter(); UpdateCounts(); })
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.UseFirstMatch)
            .Skip(1)
            .Subscribe(_ => UpdateCounts())
            .DisposeWith(_disposables);

        CopySheetCommand = ReactiveCommand.Create(CopySheet).DisposeWith(_disposables);
    }

    private void UpdateCounts()
    {
        int applied = _rows.Count(r => r.IsApplicable && (UseFirstMatch || !r.IsAmbiguous));
        int changing = _rows.Count(r => r.IsApplicable && !r.IsUnchanged && (UseFirstMatch || !r.IsAmbiguous));
        int unchanged = _rows.Count(r => r.IsApplicable && r.IsUnchanged);
        int skipped = _rows.Count - applied;

        var parts = new List<string> { $"{changing} {_changeLabel}" };
        if (unchanged > 0) parts.Add($"{unchanged} unchanged{(ShowUnchanged ? "" : " (hidden)")}");
        parts.Add($"{skipped} skipped");
        SummaryText = string.Join(", ", parts) + "." +
                      (HasAmbiguousRows && UseFirstMatch ? " Rows marked \"First match used\" are included." : "");

        // Nothing to do when every applicable row is unchanged.
        CanConfirm = changing > 0;
        ConfirmText = CanConfirm ? $"{_confirmVerb} ({changing})" : _confirmVerb;
    }

    private void ApplyFilter()
    {
        FilteredRows.Clear();
        IEnumerable<EasyNpcTransferRow> query = _rows;
        if (!ShowUnchanged) query = query.Where(r => !r.IsUnchanged);
        var s = FilterText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(r => Cells(r).Any(c => c.Contains(s, StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var r in query) FilteredRows.Add(r);
    }

    private static string[] Cells(EasyNpcTransferRow r) =>
        [r.Status, r.Npc, r.FormKey, r.Current, r.New, r.FacePlugin, r.FaceGenSource, r.DefaultPlugin, r.Note];

    /// <summary>One flat tab-separated row per table row, headers included; always the FULL list
    /// (unchanged rows too), not the filtered view.</summary>
    private void CopySheet()
    {
        var headers = new List<string> { "Status", "NPC", "FormKey" };
        if (CurrentHeader != null) headers.Add(CurrentHeader);
        headers.AddRange([NewHeader, "Face Plugin", "FaceGen Source", "Default Plugin", "Note"]);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join("\t", headers));
        foreach (var r in _rows)
        {
            var cells = Cells(r).ToList();
            if (CurrentHeader == null) cells.RemoveAt(3);
            sb.AppendLine(string.Join("\t", cells.Select(CleanTsv)));
        }
        try { Clipboard.SetText(sb.ToString()); }
        catch { /* clipboard can be transiently locked by another process; ignore */ }
    }

    private static string CleanTsv(string? s) =>
        (s ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    public void Dispose() => _disposables.Dispose();
}
