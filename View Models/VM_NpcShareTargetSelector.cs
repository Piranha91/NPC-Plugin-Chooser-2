using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Windows;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

public enum ShareReturn
{
    ShareAndSelect,
    Share,
    Cancel
}

public class VM_NpcShareTargetSelector : ReactiveObject, IDisposable
{
    private readonly List<VM_NpcsMenuSelection> _allNpcs;
    private readonly VM_NpcsMenuSelection? _excludedNpc;
    private readonly CompositeDisposable _disposables = new();

    [Reactive] public string SearchText { get; set; } = string.Empty;
    [Reactive] public NpcSearchType SearchType { get; set; } = NpcSearchType.Name;
    public ObservableCollection<VM_NpcsMenuSelection> FilteredNpcs { get; } = new();
    [Reactive] public VM_NpcsMenuSelection? SelectedNpc { get; set; }

    public Array AvailableSearchTypes => Enum.GetValues(typeof(NpcSearchType))
        .Cast<NpcSearchType>()
        .Where(e => e == NpcSearchType.Name || e == NpcSearchType.EditorID || e == NpcSearchType.FormKey)
        .ToArray();

    public ReactiveCommand<Window, Unit> ShareAndSelectCommand { get; }
    public ReactiveCommand<Window, Unit> ShareCommand { get; }
    public ReactiveCommand<Window, Unit> CancelCommand { get; }

    public ShareReturn ReturnStatus { get; set; } = ShareReturn.Cancel;

    /// <summary>Explains the excluded (source) NPC's absence, but only once the search has
    /// narrowed to it alone — i.e. the user is provably looking for that NPC and would otherwise
    /// see an unexplained empty list. Empty at all other times, including when nothing was
    /// excluded.</summary>
    [Reactive] public string ExclusionNote { get; private set; } = string.Empty;

    [Reactive] public bool HasExclusionNote { get; private set; }

    /// <param name="sourceNpcKeyToExclude">The NPC the shared appearance comes from, if known.
    /// It is removed from the pickable list: an NPC cannot be shared with itself. Such an entry
    /// is indistinguishable from the NPC's own native appearance, so its tile renders with
    /// <c>IsGuestAppearance == false</c>, the "Unshare from this NPC" menu item never appears,
    /// and the share can never be undone.</param>
    public VM_NpcShareTargetSelector(List<VM_NpcsMenuSelection> allNpcs, FormKey? sourceNpcKeyToExclude = null)
    {
        if (sourceNpcKeyToExclude is { } sourceKey && !sourceKey.Equals(FormKey.Null))
        {
            _excludedNpc = allNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(sourceKey));
            _allNpcs = _excludedNpc != null
                ? allNpcs.Where(n => n != _excludedNpc).ToList()
                : allNpcs;
        }
        else
        {
            _allNpcs = allNpcs;
        }

        this.WhenAnyValue(x => x.SearchText, x => x.SearchType)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter())
            .DisposeWith(_disposables);

        var canOk = this.WhenAnyValue(x => x.SelectedNpc)
            .Select(npc => npc != null);

        ShareAndSelectCommand = ReactiveCommand.Create<Window>(window =>
        {
            ReturnStatus = ShareReturn.ShareAndSelect;
            window.Close();
        }, canOk).DisposeWith(_disposables);

        ShareCommand = ReactiveCommand.Create<Window>(window =>
        {
            ReturnStatus = ShareReturn.Share;
            window.Close();
        }, canOk).DisposeWith(_disposables);

        CancelCommand = ReactiveCommand.Create<Window>(window =>
        {
            ReturnStatus = ShareReturn.Cancel;
            window.Close();
        }).DisposeWith(_disposables);

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredNpcs.Clear();
        foreach (var item in Filter(_allNpcs).OrderBy(n => n.DisplayName))
        {
            FilteredNpcs.Add(item);
        }

        // The exclusion normally needs no explanation, but a search that matches ONLY the
        // excluded NPC leaves an empty list that looks like a bug. Say why in exactly that case.
        bool searchedForExcludedNpcAlone = _excludedNpc != null
                                           && FilteredNpcs.Count == 0
                                           && Filter(new[] { _excludedNpc }).Any();
        ExclusionNote = searchedForExcludedNpcAlone
            ? $"{_excludedNpc!.DisplayName} is the NPC this appearance comes from, " +
              "so it cannot be shared with itself."
            : string.Empty;
        HasExclusionNote = searchedForExcludedNpcAlone;
    }

    /// <summary>Applies the current search text/type to <paramref name="source"/>. Shared by the
    /// list build and the "did the user search for the excluded NPC?" check so both judge a match
    /// identically.</summary>
    private IEnumerable<VM_NpcsMenuSelection> Filter(IEnumerable<VM_NpcsMenuSelection> source)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return source;

        return SearchType switch
        {
            NpcSearchType.Name => source.Where(n =>
                n.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)),
            NpcSearchType.EditorID => source.Where(n =>
                n.NpcEditorId.Contains(SearchText, StringComparison.OrdinalIgnoreCase)),
            NpcSearchType.FormKey => source.Where(n =>
                n.NpcFormKeyString.Contains(SearchText, StringComparison.OrdinalIgnoreCase)),
            _ => source
        };
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}