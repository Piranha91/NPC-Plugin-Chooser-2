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
using Microsoft.Win32;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Backs the validation results window: a flat, Excel-friendly table of findings with
/// a text filter and TSV-clipboard / CSV-file export.
/// </summary>
public sealed class VM_ValidationResultsWindow : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly List<ValidationIssue> _allIssues;

    public ObservableCollection<ValidationIssue> FilteredIssues { get; } = new();

    [Reactive] public string FilterText { get; set; } = string.Empty;

    public string SummaryText { get; }
    public string NotesText { get; }
    public bool HasNotes { get; }

    public ReactiveCommand<Unit, Unit> CopyTsvCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCsvCommand { get; }

    private static readonly string[] Headers =
        { "Severity", "Check", "NPC", "FormKey", "Selected Mod", "Issue", "Winning Source", "Details" };

    public VM_ValidationResultsWindow(ValidationRunResult result)
    {
        _allIssues = result.Issues
            .OrderBy(i => SeverityRank(i.Severity))
            .ThenBy(i => i.NpcDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int errors = _allIssues.Count(i => i.Severity == ValidationSeverity.Error);
        int warnings = _allIssues.Count(i => i.Severity == ValidationSeverity.Warning);
        int infos = _allIssues.Count(i => i.Severity == ValidationSeverity.Info);
        SummaryText =
            $"Validated {result.NpcsChecked} NPC(s): {errors} error(s), {warnings} warning(s), {infos} info. " +
            (_allIssues.Count == 0 ? "No issues found." : $"{_allIssues.Count} finding(s).");

        NotesText = result.Notes.Count > 0 ? string.Join(Environment.NewLine, result.Notes) : string.Empty;
        HasNotes = result.Notes.Count > 0;

        ApplyFilter();

        this.WhenAnyValue(x => x.FilterText)
            .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter())
            .DisposeWith(_disposables);

        CopyTsvCommand = ReactiveCommand.Create(CopyTsv).DisposeWith(_disposables);
        SaveCsvCommand = ReactiveCommand.Create(SaveCsv).DisposeWith(_disposables);
    }

    private static int SeverityRank(ValidationSeverity s) => s switch
    {
        ValidationSeverity.Error => 0,
        ValidationSeverity.Warning => 1,
        _ => 2
    };

    private void ApplyFilter()
    {
        FilteredIssues.Clear();
        IEnumerable<ValidationIssue> query = _allIssues;
        var s = FilterText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(i =>
                i.NpcDisplayName.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                i.NpcFormKey.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                i.SelectedMod.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                i.Issue.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                i.WinningSource.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                i.CheckText.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                i.SeverityText.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var i in query) FilteredIssues.Add(i);
    }

    private void CopyTsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("\t", Headers));
        foreach (var i in FilteredIssues)
        {
            sb.AppendLine(string.Join("\t", Row(i).Select(CleanTsv)));
        }
        try { Clipboard.SetText(sb.ToString()); }
        catch { /* clipboard can be transiently locked by another process; ignore */ }
    }

    private void SaveCsv()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Validation Report",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = "ValidationReport.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Headers.Select(CsvField)));
        foreach (var i in FilteredIssues)
        {
            sb.AppendLine(string.Join(",", Row(i).Select(CsvField)));
        }
        // UTF-8 BOM so Excel detects encoding correctly.
        System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
    }

    private static string[] Row(ValidationIssue i) =>
        new[] { i.SeverityText, i.CheckText, i.NpcDisplayName, i.NpcFormKey, i.SelectedMod, i.Issue, i.WinningSource, i.Details };

    private static string CleanTsv(string? s) =>
        (s ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static string CsvField(string? s)
    {
        s ??= string.Empty;
        if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        return s;
    }

    public void Dispose() => _disposables.Dispose();
}
