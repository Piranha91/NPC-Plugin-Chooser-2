namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// How <see cref="OutputBackend.Recast"/> delivers what Recast itself cannot. Recast swaps the
/// face, head parts, skin, height, weight, hair colour and sex, but never race, outfits or
/// keywords. Meaningless under any other backend.
/// </summary>
public enum RecastMode
{
    /// <summary>
    /// Recast for the face; a SkyPatcher INI for race, outfits and keywords. The INI never carries
    /// a visual style for a Recast target: a Recast face and a SkyPatcher <c>copyVisualStyle</c>
    /// on one NPC trade places between a new game and a loaded save (measured, houseCARL
    /// 2026-10-28).
    /// </summary>
    Hybrid,

    /// <summary>
    /// Recast alone. Race changes, outfits and keywords cannot be delivered; NPCs whose selection
    /// needs a race change are screened by the race-compatibility checks before the run.
    /// </summary>
    RecastOnly,
}
