namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// What the patcher writes to deliver each NPC's selected appearance. Orthogonal to
/// <see cref="PatchingMode"/>: every backend combines with Create and with Create-and-Patch.
///
/// <para>Persisted by name (StringEnumConverter), so members may be reordered but never renamed.
/// Replaces the old <c>UseSkyPatcherMode</c> bool; <see cref="Settings"/> still reads that key
/// from older settings files.</para>
/// </summary>
public enum OutputBackend
{
    /// <summary>NPC records in the output plugin: overrides of the selected NPCs.</summary>
    ESP,

    /// <summary>
    /// A surrogate NPC per target in the output plugin, applied at runtime by a SkyPatcher INI
    /// (<c>filterByNPCs=target:copyVisualStyle=surrogate,...</c>).
    /// </summary>
    SkyPatcher,

    /// <summary>
    /// A surrogate NPC per target in the output plugin, applied at runtime by a Recast TOML
    /// (<c>[[npcs]] target / face</c>). See <see cref="RecastMode"/> for what carries the
    /// fields Recast cannot (race, outfits, keywords).
    /// </summary>
    Recast,
}
