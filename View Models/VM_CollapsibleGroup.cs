using System;
using System.Reactive;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Header state for one collapsible GroupBox in the NPCs view's settings bar.
///
/// This is bound straight to <c>GroupBox.Header</c> and rendered by the shared
/// <c>CollapsibleGroupHeader</c> HeaderTemplate, which both GroupBox template families in
/// use here honour: the stock WPF template (Dark/Light) and the custom one in GitHub Light
/// / Tokyo Night both render the header through a ContentPresenter with
/// ContentSource="Header". Because the template's DataContext is this object, the chevron
/// and the click binding reach <see cref="ToggleCommand"/> with no DataContext plumbing.
///
/// The template deliberately sets no font properties on its TextBlocks, so each theme's
/// own caption styling still applies (12/Normal in Dark and Light, 10/SemiBold in the two
/// modern themes) and the header keeps looking exactly as it did before it became clickable.
/// </summary>
public sealed class VM_CollapsibleGroup : ReactiveObject
{
    /// <param name="title">Caption text; doubles as the persistence key.</param>
    /// <param name="isExpanded">Initial state, restored from Settings.</param>
    /// <param name="onChanged">Invoked with (title, isExpanded) after a user toggle so the
    /// owner can write the new state back to Settings.</param>
    public VM_CollapsibleGroup(string title, bool isExpanded, Action<string, bool> onChanged = null)
    {
        Title = title;
        IsExpanded = isExpanded;

        // Toggling through the command is the only way IsExpanded changes, so persistence
        // hangs off it directly rather than off a WhenAnyValue subscription that would need
        // disposing and a Skip(1) to ignore the initial value.
        ToggleCommand = ReactiveCommand.Create(() =>
        {
            IsExpanded = !IsExpanded;
            onChanged?.Invoke(Title, IsExpanded);
        });
    }

    public string Title { get; }

    [Reactive] public bool IsExpanded { get; set; }

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }
}
