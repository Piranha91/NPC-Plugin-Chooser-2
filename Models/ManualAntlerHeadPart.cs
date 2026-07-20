using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// A head part the user manually designated as an antler in the 3D preview's
/// "Set Antler Head Parts" selector, keyed by EditorID (stable across FormKey
/// remapping — the same identity NPC2 compares appearances by). Records the
/// provenance (<see cref="Sources"/>: which mod + NPC each designation came
/// from) so <see cref="AntlerBlockScope"/> can restrict blocking to the same
/// mod or the specific NPC(s). Persisted on <see cref="Settings"/> (not
/// ModSetting) so it survives the VM→model rebuild.
/// </summary>
public class ManualAntlerHeadPart
{
    /// <summary>The head part's EditorID — the block key, matched
    /// case-insensitively against each NPC's resolved head parts.</summary>
    public string EditorId { get; set; } = "";

    /// <summary>Where this EditorID was designated (one entry per mod+NPC the
    /// user checked it on). Drives SameMod / SpecificNpc scoping and correct
    /// per-source un-designation.</summary>
    public List<AntlerHeadPartSource> Sources { get; set; } = new();
}

/// <summary>One (mod, NPC) provenance entry for a
/// <see cref="ManualAntlerHeadPart"/> designation.</summary>
public class AntlerHeadPartSource
{
    /// <summary>DisplayName of the appearance mod the designation was made in.</summary>
    public string ModName { get; set; } = "";

    /// <summary>The (source/appearance) NPC FormKey the designation was made on.</summary>
    public FormKey NpcFormKey { get; set; }
}
