using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// An ArmorAddon the user manually designated as a skin-carried (WNAM) wig —
/// or explicitly NOT a wig — in the 3D preview's "Set Wig Meshes" selector,
/// keyed by EditorID (stable across FormKey remapping — the same identity NPC2
/// compares appearances by). Which direction an entry means is determined by
/// which list it lives in (<see cref="Settings.ManualWigArmatures"/> = IS a
/// wig, <see cref="Settings.ManualNonWigArmatures"/> = is NOT a wig; the
/// negative list vetoes scan detections, the positive list promotes slot-only
/// candidates the scan missed). Records the provenance (<see cref="Sources"/>:
/// which mod + NPC each designation came from) so the wig block scope can
/// restrict it to the same mod or the specific NPC(s). Persisted on
/// <see cref="Settings"/> (not ModSetting) so it survives the VM→model rebuild.
/// </summary>
public class ManualWigArmature
{
    /// <summary>The ArmorAddon's EditorID — the designation key, matched
    /// case-insensitively.</summary>
    public string EditorId { get; set; } = "";

    /// <summary>Where this EditorID was designated (one entry per mod+NPC the
    /// user toggled it on). Drives SameMod / SpecificNpc scoping and correct
    /// per-source un-designation.</summary>
    public List<WigArmatureSource> Sources { get; set; } = new();
}

/// <summary>One (mod, NPC) provenance entry for a
/// <see cref="ManualWigArmature"/> designation.</summary>
public class WigArmatureSource
{
    /// <summary>DisplayName of the appearance mod the designation was made in.</summary>
    public string ModName { get; set; } = "";

    /// <summary>The (source/appearance) NPC FormKey the designation was made on.</summary>
    public FormKey NpcFormKey { get; set; }
}
