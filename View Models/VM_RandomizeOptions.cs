using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>Which NPCs a randomize run targets.</summary>
public enum RandomizeScope
{
    VisibleNpcs,
    AllNpcs
}

/// <summary>Where eligible appearances may be drawn from during randomize.</summary>
public enum RandomizeAppearanceSource
{
    FavoriteFaces,
    SelectedMods,
    ModsAndFavorites
}

/// <summary>Friendly label + value pair for the Appearance Source combo box.
/// ToString is overridden so the themed ComboBox's selection box (which falls back
/// to ToString rather than honoring DisplayMemberPath) shows the friendly label.</summary>
public record AppearanceSourceOption(string Display, RandomizeAppearanceSource Value)
{
    public override string ToString() => Display;
}

/// <summary>A mod entry (by display name) that can be toggled as an eligible randomize source.</summary>
public class VM_SelectableModSetting : ReactiveObject
{
    public string DisplayName { get; }
    [Reactive] public bool IsSelected { get; set; }

    public VM_SelectableModSetting(string displayName, bool isSelected = false)
    {
        DisplayName = displayName;
        IsSelected = isSelected;
    }
}

/// <summary>
/// Options-only view model for the Randomize dialog. It only collects the user's
/// choices and hands them back to <see cref="VM_NpcSelectionBar"/>, which owns the
/// actual randomization (mirroring how <see cref="VM_NpcShareTargetSelector"/> just
/// returns a target NPC). The dialog does not persist anything — it opens with the
/// spec defaults every time.
/// </summary>
public class VM_RandomizeOptions : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly List<VM_SelectableModSetting> _allMods;

    // --- Scope ---
    [Reactive] public RandomizeScope Scope { get; set; } = RandomizeScope.VisibleNpcs;

    // --- Base appearance ---
    [Reactive] public bool AllowBaseMod { get; set; } = false;

    // --- Shared appearances ---
    [Reactive] public bool AllowSharedAppearance { get; set; } = false;
    [Reactive] public bool ForceSharedAppearance { get; set; } = true;
    [Reactive] public bool ShareFromSameRace { get; set; } = true;
    [Reactive] public bool ShareFromSameGender { get; set; } = true;
    [Reactive] public bool ShareFromSameWeight { get; set; } = false;
    [Reactive] public bool AllowDuplicateShares { get; set; } = false;

    // --- Appearance source ---
    [Reactive] public RandomizeAppearanceSource AppearanceSource { get; set; } = RandomizeAppearanceSource.ModsAndFavorites;

    public IReadOnlyList<AppearanceSourceOption> AvailableAppearanceSources { get; } = new[]
    {
        new AppearanceSourceOption("Favorite Faces", RandomizeAppearanceSource.FavoriteFaces),
        new AppearanceSourceOption("Selected Mods", RandomizeAppearanceSource.SelectedMods),
        new AppearanceSourceOption("Mods and Favorites", RandomizeAppearanceSource.ModsAndFavorites),
    };

    // The mod checklist is only relevant when the source includes mods.
    [ObservableAsProperty] public bool IsModListVisible { get; }

    // --- Mod checklist ---
    [Reactive] public string ModFilterText { get; set; } = string.Empty;
    public ObservableCollection<VM_SelectableModSetting> FilteredMods { get; } = new();

    public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectVisibleCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectNoneCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearRandomizedNpcsCommand { get; }
    public ReactiveCommand<Window, Unit> OkCommand { get; }
    public ReactiveCommand<Window, Unit> CancelCommand { get; }

    /// <summary>True when the user pressed OK (Randomize); false on Cancel/close.</summary>
    public bool Confirmed { get; private set; }

    public VM_RandomizeOptions(IEnumerable<string> installedAppearanceModNames, Action clearRandomizedNpcs)
    {
        _allMods = (installedAppearanceModNames ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new VM_SelectableModSetting(n))
            .ToList();

        this.WhenAnyValue(x => x.AppearanceSource)
            .Select(s => s != RandomizeAppearanceSource.FavoriteFaces)
            .ToPropertyEx(this, x => x.IsModListVisible)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.ModFilterText)
            .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyModFilter())
            .DisposeWith(_disposables);

        SelectAllCommand = ReactiveCommand.Create(() =>
        {
            foreach (var m in _allMods) m.IsSelected = true;
        }).DisposeWith(_disposables);

        SelectVisibleCommand = ReactiveCommand.Create(() =>
        {
            foreach (var m in FilteredMods) m.IsSelected = true;
        }).DisposeWith(_disposables);

        SelectNoneCommand = ReactiveCommand.Create(() =>
        {
            foreach (var m in _allMods) m.IsSelected = false;
        }).DisposeWith(_disposables);

        // Delegates to the parent VM (which owns selection state); the dialog stays open.
        ClearRandomizedNpcsCommand = ReactiveCommand.Create(() => clearRandomizedNpcs?.Invoke())
            .DisposeWith(_disposables);

        OkCommand = ReactiveCommand.Create<Window>(window =>
        {
            Confirmed = true;
            window.Close();
        }).DisposeWith(_disposables);

        CancelCommand = ReactiveCommand.Create<Window>(window =>
        {
            Confirmed = false;
            window.Close();
        }).DisposeWith(_disposables);

        ApplyModFilter();
    }

    private void ApplyModFilter()
    {
        // FilteredMods holds references to the same VM instances as _allMods, so
        // check state survives filtering.
        IEnumerable<VM_SelectableModSetting> results = _allMods;
        if (!string.IsNullOrWhiteSpace(ModFilterText))
        {
            results = results.Where(m =>
                m.DisplayName.Contains(ModFilterText, StringComparison.OrdinalIgnoreCase));
        }

        FilteredMods.Clear();
        foreach (var m in results)
        {
            FilteredMods.Add(m);
        }
    }

    /// <summary>Display names of the mods the user checked as eligible sources.</summary>
    public HashSet<string> GetSelectedModNames() =>
        _allMods.Where(m => m.IsSelected)
            .Select(m => m.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
