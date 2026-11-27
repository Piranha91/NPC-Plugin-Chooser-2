using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// The "Clear remembered wig and headgear choices (N)" button of Settings > Head Editing. N is
/// the number of NPCs with a remembered answer; the button exists only while N is above zero and
/// follows every store and clear through <see cref="WigHeadgearChoiceCoordinator.Changed"/>.
/// Disabled, not hidden, while a patch run is in progress: the patcher is reading the answers.
/// </summary>
public sealed class VM_WigHeadgearChoiceReset : ReactiveObject, IDisposable
{
    private readonly WigHeadgearChoiceCoordinator _choices;
    private readonly CompositeDisposable _disposables = new();

    public VM_WigHeadgearChoiceReset(WigHeadgearChoiceCoordinator choices)
    {
        _choices = choices;
        Refresh();

        _choices.Changed
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => Refresh())
            .DisposeWith(_disposables);

        var canClear = this.WhenAnyValue(x => x.HasRememberedChoices, x => x.IsRunActive,
            (has, running) => has && !running);
        ClearCommand = ReactiveCommand.Create(() => { _choices.ClearRemembered(); }, canClear)
            .DisposeWith(_disposables);
    }

    /// <summary>Number of NPCs with a remembered answer.</summary>
    [Reactive] public int RememberedCount { get; private set; }

    /// <summary>The button's visibility: true only while there is something to clear.</summary>
    [Reactive] public bool HasRememberedChoices { get; private set; }

    /// <summary>"Clear remembered wig and headgear choices (N)".</summary>
    [Reactive] public string ButtonText { get; private set; } = FormatButtonText(0);

    [Reactive] public bool IsRunActive { get; private set; }

    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    public const string ToolTipText =
        "When wig handling 'Forward to Outfit' would add a wig to an outfit that already holds headgear on " +
        "the same body slot, the run asks what to do. Answers given with 'Remember this choice for these " +
        "NPCs' are kept and those NPCs are not asked about again. This forgets all of them, so the next run " +
        "asks again. Not available while a patch run is in progress.";

    public string ToolTip => ToolTipText;

    public static string FormatButtonText(int count) =>
        $"Clear remembered wig and headgear choices ({count})";

    private void Refresh()
    {
        int count = _choices.RememberedCount;
        RememberedCount = count;
        HasRememberedChoices = count > 0;
        ButtonText = FormatButtonText(count);
        IsRunActive = _choices.IsRunActive;
    }

    public void Dispose() => _disposables.Dispose();
}
