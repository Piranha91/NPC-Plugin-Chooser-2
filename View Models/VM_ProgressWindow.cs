using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

public class VM_ProgressWindow : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();

    [Reactive] public string Title { get; set; } = "Progress";
    [Reactive] public string StatusMessage { get; set; } = "Processing...";
    [Reactive] public double ProgressValue { get; set; } = 0;
    [Reactive] public double ProgressMaximum { get; set; } = 100;
    [Reactive] public bool IsIndeterminate { get; set; } = false;
    [Reactive] public bool IsCancellationRequested { get; private set; } = false;
    [Reactive] public bool CanCancel { get; set; } = true;

    /// <summary>Optional sub-line shown below the main StatusMessage; the
    /// "Generate All Mugshots" batch uses it for the current "Mod — NPC"
    /// pair. Empty string hides the row in the XAML.</summary>
    [Reactive] public string CurrentSubItem { get; set; } = string.Empty;

    /// <summary>Pre-formatted ETA string ("ETA: 4m 12s"). Empty string hides
    /// the row in the XAML; <see cref="RefreshEta"/> populates it from a
    /// running average.</summary>
    [Reactive] public string EtaText { get; set; } = string.Empty;

    /// <summary>Bound to the action button's content. Defaults to "Cancel"
    /// to match prior callers; the batch flow sets this to "Abort".</summary>
    [Reactive] public string CancelButtonText { get; set; } = "Cancel";

    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public VM_ProgressWindow()
    {
        CancelCommand = ReactiveCommand.Create(() =>
        {
            IsCancellationRequested = true;
            CanCancel = false; // Disable the button after cancellation
            StatusMessage = "Cancelling...";
        }, this.WhenAnyValue(x => x.CanCancel)).DisposeWith(_disposables);
    }

    public void UpdateProgress(double value, string? message = null)
    {
        ProgressValue = value;
        if (message != null)
        {
            StatusMessage = message;
        }
    }

    /// <summary>
    /// Convenience overload: recomputes <see cref="EtaText"/> from a flat
    /// running average over <paramref name="completed"/> uniform items. Use
    /// when every item costs roughly the same. For batches with mixed item
    /// costs (e.g. fast cache hits vs full renders), call <see cref="SetEta"/>
    /// directly with a precomputed estimate so cheap items don't bias the
    /// average.
    /// </summary>
    public void RefreshEta(TimeSpan elapsed, int completed, int remaining)
    {
        if (completed <= 0 || remaining <= 0)
        {
            SetEta(null);
            return;
        }
        double avgSeconds = elapsed.TotalSeconds / completed;
        SetEta(TimeSpan.FromSeconds(avgSeconds * remaining));
    }

    /// <summary>
    /// Sets <see cref="EtaText"/> from a precomputed ETA. Pass null or a
    /// non-positive span to clear the line.
    /// </summary>
    public void SetEta(TimeSpan? eta)
    {
        if (eta == null || eta.Value.TotalSeconds <= 0)
        {
            EtaText = string.Empty;
            return;
        }
        var v = eta.Value;
        // Format adapts to magnitude — sub-minute jobs show seconds only,
        // sub-hour jobs show m+s, longer jobs show h+m.
        string formatted = v.TotalHours >= 1
            ? $"{(int)v.TotalHours}h {v.Minutes:D2}m"
            : v.TotalMinutes >= 1
                ? $"{v.Minutes}m {v.Seconds:D2}s"
                : $"{v.Seconds}s";
        EtaText = $"ETA: {formatted}";
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}