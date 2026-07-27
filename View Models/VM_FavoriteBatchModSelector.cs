using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Mutagen.Bethesda.Plugins;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>Which side of the Favorites window's "All From Mod" batch action is being run.</summary>
public enum FavoriteBatchAction
{
    Add,
    Remove
}

/// <summary>
/// Backs the small picker the Favorites window's Batch Actions box opens: one dropdown of mods
/// plus a confirm/cancel pair. It only chooses a mod — the caller owns the actual favorite
/// add/remove so the settings mutation stays in one place (<see cref="VM_FavoriteFaces"/>).
/// </summary>
public class VM_FavoriteBatchModSelector : ReactiveObject, IDisposable
{
    public event Action? RequestClose;

    /// <summary>One pickable mod, carrying the counts that tell the user what the action will do.</summary>
    public sealed class ModOption
    {
        public ModOption(string modName, int npcCount, int favoriteCount, string displayText)
        {
            ModName = modName;
            NpcCount = npcCount;
            FavoriteCount = favoriteCount;
            DisplayText = displayText;
        }

        /// <summary>The mod's display name — the same string a favorite stores as its ModName.</summary>
        public string ModName { get; }

        /// <summary>Appearances the mod provides (0 for a mod known only from existing favorites).</summary>
        public int NpcCount { get; }

        /// <summary>How many of this mod's appearances are already favorited.</summary>
        public int FavoriteCount { get; }

        public string DisplayText { get; }
    }

    private readonly CompositeDisposable _disposables = new();

    public FavoriteBatchAction Action { get; }
    public string WindowTitle { get; }
    public string Prompt { get; }
    public string ConfirmButtonText { get; }

    public ObservableCollection<ModOption> AvailableMods { get; } = new();
    [Reactive] public ModOption? SelectedMod { get; set; }

    /// <summary>True when the window was closed with the confirm button rather than Cancel.</summary>
    public bool Confirmed { get; private set; }

    public ReactiveCommand<Unit, Unit> ConfirmCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public VM_FavoriteBatchModSelector(
        FavoriteBatchAction action,
        IEnumerable<VM_ModSetting> allModSettings,
        IEnumerable<(FormKey NpcFormKey, string ModName)> favorites)
    {
        Action = action;
        WindowTitle = action == FavoriteBatchAction.Add
            ? "Add All Favorites From Mod"
            : "Remove All Favorites From Mod";
        Prompt = action == FavoriteBatchAction.Add
            ? "Select a mod. Every appearance it provides will be added to your favorites."
            : "Select a mod. Every one of its appearances will be removed from your favorites.";
        ConfirmButtonText = action == FavoriteBatchAction.Add ? "Add" : "Remove";

        foreach (var option in BuildOptions(action, allModSettings, favorites))
        {
            AvailableMods.Add(option);
        }

        var canConfirm = this.WhenAnyValue(x => x.SelectedMod).Select(mod => mod != null);

        ConfirmCommand = ReactiveCommand.Create(() =>
        {
            Confirmed = true;
            RequestClose?.Invoke();
        }, canConfirm).DisposeWith(_disposables);

        CancelCommand = ReactiveCommand.Create(() =>
        {
            Confirmed = false;
            RequestClose?.Invoke();
        }).DisposeWith(_disposables);
    }

    /// <summary>
    /// Builds the pickable list for one action. Add offers every mod that actually provides
    /// appearances (a mod with none has nothing to favorite); Remove offers only mods the user
    /// currently has favorites from — including mods that are no longer in the Mods menu, so
    /// favorites orphaned by a removed mod entry can still be cleared out.
    /// </summary>
    internal static List<ModOption> BuildOptions(
        FavoriteBatchAction action,
        IEnumerable<VM_ModSetting> allModSettings,
        IEnumerable<(FormKey NpcFormKey, string ModName)> favorites)
    {
        // Case-insensitive, matching how the Favorites window resolves a favorite's ModName
        // back to its mod entry.
        var favoriteCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, favoriteModName) in favorites)
        {
            if (string.IsNullOrWhiteSpace(favoriteModName)) continue;
            favoriteCounts[favoriteModName] = favoriteCounts.GetValueOrDefault(favoriteModName) + 1;
        }

        var options = new List<ModOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in allModSettings)
        {
            if (string.IsNullOrWhiteSpace(mod.DisplayName)) continue;

            int npcCount = mod.NpcFormKeysToDisplayName.Count;
            int favoriteCount = favoriteCounts.GetValueOrDefault(mod.DisplayName);
            bool relevant = action == FavoriteBatchAction.Add ? npcCount > 0 : favoriteCount > 0;
            if (!relevant || !seen.Add(mod.DisplayName)) continue;

            options.Add(new ModOption(mod.DisplayName, npcCount, favoriteCount,
                Describe(action, mod.DisplayName, npcCount, favoriteCount)));
        }

        if (action == FavoriteBatchAction.Remove)
        {
            foreach (var (favoriteModName, favoriteCount) in favoriteCounts)
            {
                if (!seen.Add(favoriteModName)) continue;
                options.Add(new ModOption(favoriteModName, 0, favoriteCount,
                    Describe(action, favoriteModName, 0, favoriteCount)));
            }
        }

        return options.OrderBy(o => o.ModName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Describe(FavoriteBatchAction action, string modName, int npcCount, int favoriteCount)
    {
        if (action == FavoriteBatchAction.Remove)
        {
            return $"{modName}  ({favoriteCount} favorited)";
        }

        return favoriteCount > 0
            ? $"{modName}  ({npcCount} appearances, {favoriteCount} already favorited)"
            : $"{modName}  ({npcCount} appearances)";
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
