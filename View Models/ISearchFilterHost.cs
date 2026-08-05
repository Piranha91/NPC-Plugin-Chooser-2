namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// Implemented by view models that own a search/filter menu, so the global
/// Ctrl+Shift+C hotkey can reset whichever one is currently on screen without
/// knowing anything about its particular filter shape.
/// </summary>
public interface ISearchFilterHost
{
    /// <summary>
    /// Returns every search row to an inactive state, so the unfiltered list is shown again.
    ///
    /// Implementations deliberately touch ONLY the search criteria. Display toggles
    /// (Show Hidden Mods, Show Single Option NPCs, Show Mugshot-Only Mods, ...), sort
    /// order, zoom and the per-row Is / Is Not inversion are user preferences rather
    /// than filters, and are left as the user set them.
    ///
    /// A row's search-type dropdown is likewise preserved wherever the type has some
    /// neutral value that switches the row off. Only the handful of types with no such
    /// value (see <see cref="VM_NpcSelectionBar.ClearSearchFilters"/>) fall back to
    /// resetting that one row's type.
    /// </summary>
    void ClearSearchFilters();
}
