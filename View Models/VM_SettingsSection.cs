using System.Collections.Generic;
using ReactiveUI;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Expander state for one section of the Settings view.
///
/// Bound straight to <c>Expander.IsExpanded</c> (TwoWay), so unlike
/// <see cref="VM_CollapsibleGroup"/> — whose NPCs-view header is a click target driving a
/// command — there is no toggle command to hang persistence off. The write-back therefore
/// lives in the setter, which also means the initial value assigned in the constructor
/// never touches the store: only a real user toggle is recorded, so sections the user has
/// never touched keep following <paramref name="defaultExpanded"/> even if that default
/// changes in a later release.
/// </summary>
public sealed class VM_SettingsSection : ReactiveObject
{
    private readonly IDictionary<string, bool> _store;
    private bool _isExpanded;

    /// <param name="key">Section caption; doubles as the persistence key.</param>
    /// <param name="defaultExpanded">State used when the key is absent from the store.</param>
    /// <param name="store">Backing map, normally Settings.SettingsViewExpandedSections.</param>
    public VM_SettingsSection(string key, bool defaultExpanded, IDictionary<string, bool> store = null)
    {
        Key = key;
        _store = store;
        _isExpanded = store != null && store.TryGetValue(key, out var saved) ? saved : defaultExpanded;
    }

    public string Key { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (value == _isExpanded) return;
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            if (_store != null) _store[Key] = value;
        }
    }
}
