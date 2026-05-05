using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// Modal progress dialog hosting <see cref="MeshSurveyRunner.RunAsync"/>.
/// Shows a per-mod progress bar, a current-NPC status line, and a Cancel
/// button. On completion the "Open RenderLogs Folder" button enables and
/// the title flips to "Done" or "Aborted"; the user closes the window
/// manually so they can copy the path / open the folder.
/// </summary>
public partial class MeshSurveyDialog : Window
{
    private readonly MeshSurveyRunner _runner;
    private readonly CancellationTokenSource _cts = new();
    private string? _outputPath;
    private bool _runFinished;

    public MeshSurveyDialog(MeshSurveyRunner runner)
    {
        InitializeComponent();
        _runner = runner;
        DataContext = new ProgressVm();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var vm = (ProgressVm)DataContext;
        var progress = new Progress<MeshSurveyRunner.ProgressInfo>(info =>
        {
            // Marshaled to the UI thread by Progress<T>.
            if (info.Total > 0)
                vm.PercentComplete = (double)info.Completed / info.Total * 100.0;
            StatusText.Text = info.Total > 0
                ? $"[{info.Completed}/{info.Total}] {info.CurrentLabel}"
                : info.CurrentLabel;
        });

        try
        {
            _outputPath = await Task.Run(() => _runner.RunAsync(progress, _cts.Token));
        }
        catch (OperationCanceledException)
        {
            // RunAsync swallows the OCE internally and returns the path it
            // wrote to before cancellation, but defensive in case that path
            // changes.
        }
        catch (Exception ex)
        {
            StatusText.Text = "Survey failed: " + ex.Message;
            CancelButton.Content = "Close";
            _runFinished = true;
            return;
        }

        _runFinished = true;
        CancelButton.Content = "Close";
        OpenFolderButton.IsEnabled = _outputPath != null;
        if (_outputPath != null)
        {
            Title = _cts.IsCancellationRequested ? "Mesh Survey — Aborted" : "Mesh Survey — Done";
            OutputPathText.Text = "Output: " + _outputPath;
        }
        else
        {
            Title = "Mesh Survey — No eligible mods";
            StatusText.Text = "No mods with non-empty mod folders + at least one NPC.";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runFinished)
        {
            Close();
            return;
        }
        // Mid-run: signal cancel; OnLoaded's await will resume and finalize
        // the dialog state. Disable the button so we don't double-cancel.
        CancelButton.IsEnabled = false;
        StatusText.Text = "Aborting…";
        _cts.Cancel();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_outputPath == null) return;
        try
        {
            // Open Explorer with the CSV pre-selected.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_outputPath}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Fall back to opening the folder if /select fails.
            try
            {
                var folder = Path.GetDirectoryName(_outputPath);
                if (!string.IsNullOrEmpty(folder))
                    Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch { /* nothing else to do */ }
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // If user closes mid-run via the title-bar X, treat as cancel.
        if (!_runFinished)
        {
            _cts.Cancel();
            // Allow the close — OnLoaded's task continues in the background
            // and finishes writing whatever it had buffered.
        }
        _cts.Dispose();
    }

    /// <summary>Minimal one-property VM so the ProgressBar binding has a
    /// notification source. Code-behind owns it directly; no DI/IoC needed
    /// for a transient progress dialog.</summary>
    private sealed class ProgressVm : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private double _pct;
        public double PercentComplete
        {
            get => _pct;
            set
            {
                if (_pct == value) return;
                _pct = value;
                PropertyChanged?.Invoke(this,
                    new System.ComponentModel.PropertyChangedEventArgs(nameof(PercentComplete)));
            }
        }
    }
}
