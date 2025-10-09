using System;
using System.Reactive;
using System.Reactive.Disposables;
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

    public void Dispose()
    {
        _disposables.Dispose();
    }
}