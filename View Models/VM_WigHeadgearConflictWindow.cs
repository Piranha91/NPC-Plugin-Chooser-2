using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// What the user answered in the wig-and-headgear window.
/// </summary>
/// <param name="Choice">What to do for every NPC listed, or null to cancel the run.</param>
/// <param name="Remember">Whether the choice is to be kept for these NPCs beyond this run.
/// Meaningless when the run is cancelled.</param>
public sealed record WigHeadgearConflictDecision(WigHeadgearChoice? Choice, bool Remember)
{
    public static readonly WigHeadgearConflictDecision CancelRun = new(null, false);

    /// <summary>False only for "Cancel run".</summary>
    public bool ContinuesRun => Choice != null;
}

/// <summary>
/// Backs the second pre-run window: the valid selections whose wig 'Forward to Outfit' would add
/// to an outfit that already holds headgear on the same body slot. The selections stay valid and
/// nothing is removed here; the user picks one of three ways to continue, for every NPC listed,
/// or cancels the run. The window owns closing; this type owns what was chosen.
/// </summary>
public sealed class VM_WigHeadgearConflictWindow : ReactiveObject, IDisposable
{
    public const string WindowTitle = "Wig and headgear on the same slot";

    public const string ExplanationText =
        "Wig handling 'Forward to Outfit' would add a wig to the outfit of the NPCs below, and that outfit " +
        "already holds headgear on the same body slot. Skyrim shows only one of two items on the same slot, " +
        "so either the wig or the headgear would be missing in game. Choose what NPC2 should do for these NPCs.";

    public const string ForwardButtonText = "Continue (forward the wig)";
    public const string OmitButtonText = "Continue (omit the wig)";
    public const string ConvertButtonText = "Convert to head parts";
    public const string CancelButtonText = "Cancel run";
    public const string RememberText = "Remember this choice for these NPCs";

    public const string ForwardToolTipText =
        "The wig is added to the outfit as asked. The headgear or the wig will not show.";
    public const string OmitToolTipText =
        "The wig is left out for these NPCs. They keep their headgear and have no wig.";
    public const string ConvertToolTipText =
        "The wig becomes part of the head for these NPCs, so it stays under the headgear like normal hair.";
    public const string CancelToolTipText = "Abort without patching anything.";
    public const string RememberToolTipText =
        "Ticked: the choice is stored for the NPCs listed and the next runs do not ask about them again. " +
        "It can be cleared in Settings, under Head Editing. Not ticked: the choice applies to this run only. " +
        "Applies to the three choices that continue.";

    private readonly CompositeDisposable _disposables = new();
    private readonly WigHeadgearConflictGrouping _grouping;

    public VM_WigHeadgearConflictWindow(IReadOnlyList<WigHeadgearConflictCase> cases)
    {
        _grouping = WigHeadgearConflictGrouping.From(cases);

        ForwardCommand = ReactiveCommand.Create(() => Decide(WigHeadgearChoice.Forward)).DisposeWith(_disposables);
        OmitCommand = ReactiveCommand.Create(() => Decide(WigHeadgearChoice.Omit)).DisposeWith(_disposables);
        ConvertCommand = ReactiveCommand.Create(() => Decide(WigHeadgearChoice.ConvertToHeadParts))
            .DisposeWith(_disposables);
        CancelCommand = ReactiveCommand.Create(() => Decide(null)).DisposeWith(_disposables);
    }

    public string Title => WindowTitle;
    public string Explanation => ExplanationText;
    public string ForwardToolTip => ForwardToolTipText;
    public string OmitToolTip => OmitToolTipText;
    public string ConvertToolTip => ConvertToolTipText;
    public string CancelToolTip => CancelToolTipText;
    public string RememberToolTip => RememberToolTipText;

    /// <summary>"N NPCs in M groups".</summary>
    public string TotalsText => _grouping.TotalsText;

    public IReadOnlyList<WigHeadgearConflictGroupRow> Groups => _grouping.Groups;

    /// <summary>Group rows and NPC rows in display order, as one flat list.</summary>
    public IReadOnlyList<object> Rows => _grouping.Rows;

    /// <summary>The NPCs the choice applies to: every NPC listed.</summary>
    public IReadOnlyList<FormKey> NpcFormKeys => _grouping.NpcFormKeys;

    [Reactive] public bool RememberChoice { get; set; }

    public ReactiveCommand<Unit, Unit> ForwardCommand { get; }
    public ReactiveCommand<Unit, Unit> OmitCommand { get; }
    public ReactiveCommand<Unit, Unit> ConvertCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>The answer. Stays "cancel the run" until a button is pressed, so a window closed
    /// by Esc or by its title bar cancels, like the invalid-selections window.</summary>
    public WigHeadgearConflictDecision Decision { get; private set; } = WigHeadgearConflictDecision.CancelRun;

    /// <summary>Raised once a button has set <see cref="Decision"/>; the window closes on it.</summary>
    public event Action? CloseRequested;

    private void Decide(WigHeadgearChoice? choice)
    {
        Decision = choice == null
            ? WigHeadgearConflictDecision.CancelRun
            : new WigHeadgearConflictDecision(choice, RememberChoice);
        CloseRequested?.Invoke();
    }

    public void Dispose() => _disposables.Dispose();
}
