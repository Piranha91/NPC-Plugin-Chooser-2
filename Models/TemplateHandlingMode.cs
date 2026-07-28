namespace NPC_Plugin_Chooser_2.Models;

/// <summary>
/// How the patcher treats NPCs whose appearance is inherited from another NPC via the
/// Traits template flag. Such an NPC has no face of its own: the engine renders the face
/// of the record at the end of its template chain (the terminus) and loads FaceGen from
/// the TERMINUS's path, so everything written under the NPC's own FormID is inert and
/// every NPC sharing one terminus resolves to the same single face.
///
/// Applies identically to record output and SkyPatcher output. In both cases the FaceGen
/// SOURCE is unchanged — it is always measured at the terminus's paths (see
/// <c>BackEnd.FaceGenLadder</c>); this mode only decides the DESTINATION and what the
/// output record says about inheritance.
/// </summary>
public enum TemplateHandlingMode
{
    /// <summary>
    /// Keep the inheritance: the output preserves the Traits flag, FaceGen lands at the
    /// terminus's shared path, and the NPC keeps showing the template's face in game —
    /// whatever appearance was selected for it. This is the vanilla-faithful shape and
    /// the historical behaviour of record mode.
    /// </summary>
    InheritFromTemplate,

    /// <summary>
    /// Flatten the chain: copy the terminus's appearance fields onto the NPC's own
    /// record, clear the Traits flag, and write FaceGen under the NPC's own FormID.
    /// The selection then applies per-NPC, so two NPCs sharing a template can look
    /// different. Costs one FaceGen copy per templated NPC instead of one shared copy.
    /// Only chains that resolve to a concrete NPC are flattened — a chain ending in a
    /// levelled list (the game picks an actor at runtime) or an unfollowable chain
    /// keeps inheriting regardless of this setting.
    /// </summary>
    GiveEachNpcOwnCopy
}
