using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// The surrogate NPC records a runtime backend (SkyPatcher or Recast) applies an appearance from,
/// and the target↔surrogate map that ties each one back to the NPC it is for. Backend-neutral:
/// the SkyPatcher ini (<c>copyVisualStyle=</c>) and the Recast TOML (<c>face =</c>) both name a
/// surrogate built here, and the asset pass writes FaceGen to the surrogate's own path in either.
/// </summary>
public class SurrogateNpcRegistry
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly Dictionary<FormKey, FormKey> _keyOriginalValSurrogate = new(); // key: original NPC, value: surrogate
    private readonly Dictionary<FormKey, FormKey> _keySurrogateValOriginal = new();

    public SurrogateNpcRegistry(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
    }

    /// <summary>Forgets every surrogate. Called once per patching iteration, alongside the
    /// backends' own <c>Reinitialize</c>.</summary>
    public void Reinitialize()
    {
        _keyOriginalValSurrogate.Clear();
        _keySurrogateValOriginal.Clear();
    }

    /// <summary>
    /// Creates the surrogate "_Template" NPC. Its CONTENT is a deep copy of
    /// <paramref name="appearanceDonor"/> (the chosen appearance), but it is registered under
    /// <paramref name="targetNpcFormKey"/> (the NPC being patched in the user's load order). The two
    /// are deliberately decoupled so that:
    ///   - the output masters only to the donor's appearance plugins, never the target's data mods, and
    ///   - the runtime config names the TARGET NPC even when the donor is a DIFFERENT NPC (a cross-NPC
    ///     appearance swap) — something only a runtime backend can do.
    /// AssetHandler likewise looks the surrogate up by the target FormKey via TryGetSurrogateFormKey.
    ///
    /// <para><b>Template chains can be flattened</b> (opt-in via
    /// <see cref="Models.TemplateHandlingMode.GiveEachNpcOwnCopy"/>; the caller passes
    /// <paramref name="appearanceTerminus"/> only when that mode is active). When the donor
    /// inherits its appearance (Traits + TPLT), a plain DeepCopyIn hands the surrogate that
    /// inheritance too — and the engine then
    /// resolves the recipient's face to the chain TERMINUS and loads FaceGen from the terminus's own
    /// path (proven in game). That has two consequences we do not want: FaceGen written under the
    /// surrogate is inert, and the face the user actually gets is whichever mod wins the terminus's
    /// path in their load order. Worse, it is not per-NPC — every NPC inheriting from that terminus
    /// resolves to the one shared path, so two NPCs given different appearance mods would be forced
    /// to look identical.</para>
    ///
    /// <para>Copying the terminus's appearance in and clearing the Traits bit makes the surrogate
    /// own its face outright, so the destination is always its own path and each NPC's choice is
    /// honoured independently. Costs one duplicated FaceGen per surrogate, which is the price of
    /// that independence.</para>
    ///
    /// <para><b>Failure contract:</b> if the donor cannot be read (malformed plugin), the copy's
    /// exception is rethrown after the half-built surrogate is removed again — no record and no
    /// map state is left behind, so the caller can log and skip just that NPC.</para>
    /// </summary>
    /// <param name="appearanceTerminus">
    /// The end of the donor's Traits chain, or null when the donor does not inherit — or when the
    /// user's Template Handling Mode says to keep inheriting. Supplying it is what enables the
    /// flattening described above.
    /// </param>
    /// <param name="appearanceOnly">
    /// Create-and-Patch only: strip the donor's non-appearance data from the surrogate — see
    /// <see cref="StripNonAppearanceData"/>. Left false for plain Create, where forwarding the
    /// donor's whole record IS the mode's contract and record mode does exactly the same.
    /// </param>
    /// <param name="includeOutfit">
    /// Whether an outfit is actually being applied to this NPC. When false the surrogate is given no
    /// outfit at all; only meaningful alongside <paramref name="appearanceOnly"/>.
    /// </param>
    public Npc CreateSurrogate(FormKey targetNpcFormKey, INpcGetter appearanceDonor,
        INpcGetter? appearanceTerminus = null, bool appearanceOnly = false, bool includeOutfit = false)
    {
        if (_keyOriginalValSurrogate.ContainsKey(targetNpcFormKey))
        {
            throw new ArgumentException($"A surrogate already exists for {targetNpcFormKey}.",
                nameof(targetNpcFormKey));
        }

        var npcCopy = _environmentStateProvider.OutputMod.Npcs.AddNew();
        try
        {
            npcCopy.DeepCopyIn(appearanceDonor, out _);

            if (appearanceTerminus != null && !appearanceTerminus.FormKey.Equals(appearanceDonor.FormKey))
            {
                // Overlay only the appearance the engine would have inherited; everything else stays
                // the donor's, since the surrogate exists to carry a visual style and nothing more.
                Auxilliary.CopyInheritedAppearance(npcCopy, appearanceTerminus);
                npcCopy.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Traits;
            }

            if (appearanceOnly)
            {
                StripNonAppearanceData(npcCopy, includeOutfit);
            }

            var edid = appearanceDonor.EditorID ?? "NoEditorID";
            npcCopy.EditorID = edid + "_Template";
        }
        catch
        {
            // The deep copy is the first full read of the donor, so a malformed donor plugin
            // (e.g. a truncated NAM5 subrecord) throws from the lazy overlay right here. Unwind
            // the AddNew before rethrowing so the caller can skip just this NPC: the orphan
            // sweep only REPORTS leftover NPC records, it never removes them, so the empty
            // surrogate would otherwise ship in the output plugin.
            _environmentStateProvider.OutputMod.Npcs.Remove(npcCopy.FormKey);
            throw;
        }

        _keyOriginalValSurrogate.Add(targetNpcFormKey, npcCopy.FormKey);
        _keySurrogateValOriginal.Add(npcCopy.FormKey, targetNpcFormKey);
        return npcCopy;
    }

    /// <summary>
    /// Drops the donor's non-appearance links from a Create-and-Patch surrogate.
    ///
    /// <para><b>Why this is needed.</b> The surrogate is a <c>DeepCopyIn</c> of the donor, so it
    /// carries the donor's factions, packages, inventory, perks, spells, voice, class, combat style
    /// and outfit as well as its face. The Patcher's final merge-in walker then follows EVERY link
    /// on it, so any of those that live in the appearance plugin get duplicated into the output —
    /// records the user never asked for, plus the assets the asset collector then copies for them.
    /// Record mode merges none of it: there the patched record is an override of the WINNING record,
    /// whose non-appearance links are the recipient's own and already in the load order.</para>
    ///
    /// <para>Outfits are cleared outright when no outfit is being applied: "Include Outfit off"
    /// means the surrogate should have no outfit opinion at all, and it never gets one —
    /// <c>ApplySkyPatcherDirectives</c> only emits <c>outfitDefault=</c> when an outfit IS being
    /// applied. A wig-forwarded outfit is assigned later (<c>WigForwarder.ApplyLinksTo</c>, after
    /// <c>CopyAppearanceData</c>), so clearing here cannot lose it.</para>
    ///
    /// <para><b>ALL of it goes, not just the part that would be merged.</b> An earlier rule kept
    /// any link the user's game could already resolve, on the grounds that it merges nothing and
    /// dangles nothing. That turned out to be false in one specific way: a donor shipped by a mod
    /// whose plugin is NOT enabled can reference records from a DIFFERENT version of a master it
    /// does not ship. Those links resolve for the mod's author and not for this user, and forwarding
    /// them puts a broken reference into a plugin that IS loaded — the donor's own plugin never was.
    /// Legacy of the Dragonborn's appearance mods are built against LOTD v5; run against v6 their
    /// inventory and AI-package links point at records that no longer exist, and the surrogates
    /// carrying them were the output's only null references.</para>
    ///
    /// <para>Keeping them bought nothing to weigh against that. Both runtime backends read the
    /// appearance off the surrogate and nothing else, and the surrogate is never instantiated as an
    /// actor, so its inventory, packages, factions, perks and spells are inert cargo either way.</para>
    ///
    /// <para><c>Class</c> is the sole exception and is left EXACTLY as the donor had it: CNAM is a
    /// required subrecord, and a null one makes xEdit report "Found a NULL reference, expected:
    /// CLAS" and stalls the game on launch. A Class the output cannot reference is screened out per
    /// NPC instead (<c>Validator.WrittenLinksAreSatisfiable</c>).</para>
    /// </summary>
    private static void StripNonAppearanceData(Npc npc, bool includeOutfit)
    {
        // Outfits: an opinion the surrogate is only entitled to when one is actually applied.
        if (!includeOutfit) npc.DefaultOutfit.SetToNull();
        npc.SleepingOutfit.SetToNull();

        // NOT npc.Class — see the CNAM note above.
        npc.Voice.SetToNull();
        npc.CombatStyle.SetToNull();
        npc.CrimeFaction.SetToNull();
        npc.DeathItem.SetToNull();
        npc.GiftFilter.SetToNull();
        npc.AttackRace.SetToNull();
        npc.FarAwayModel.SetToNull();
        npc.DefaultPackageList.SetToNull();
        npc.CombatOverridePackageList.SetToNull();
        npc.SpectatorOverridePackageList.SetToNull();
        npc.ObserveDeadBodyOverridePackageList.SetToNull();
        npc.GuardWarnOverridePackageList.SetToNull();

        npc.Factions?.Clear();
        npc.Packages?.Clear();
        npc.Keywords?.Clear();
        npc.ActorEffect?.Clear();
        npc.Perks?.Clear();
        npc.Items?.Clear();
        npc.Attacks?.Clear();

        // Nested structures: no partial edit makes sense, and neither is read from a face template.
        npc.VirtualMachineAdapter = null;
        npc.Destructible = null;
    }

    public bool TryGetSurrogateFormKey(FormKey originalNpcFormKey, out FormKey surrogateNpcFormKey)
    {
        return _keyOriginalValSurrogate.TryGetValue(originalNpcFormKey, out surrogateNpcFormKey);
    }

    public bool TryGetOriginalFormKey(FormKey surrogateNpcFormKey, out FormKey originalNpcFormKey)
    {
        return _keySurrogateValOriginal.TryGetValue(surrogateNpcFormKey, out originalNpcFormKey);
    }
}
