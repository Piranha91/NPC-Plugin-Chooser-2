using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Patch-time wig/antler forwarding (see <see cref="WigHandlingMode"/> and
/// <see cref="AntlerHandlingMode"/>). Runs per NPC BEFORE the appearance merge,
/// evaluating the two classes independently. It authors the +Wig/+Antler
/// duplicate records into the output mod and seeds the RecordHandler's
/// duplicate-in mapping so the subsequent merge walkers redirect to the WornArmor
/// duplicate instead of also merging the original (skin forwarding), and/or hands
/// back an outfit duplicate for the Patcher to assign after
/// <c>CopyAppearanceData</c> (outfit forwarding — no mapping seed, because other
/// NPCs legitimately share the original outfit). Antler Remove additionally strips
/// baked-in antler ArmorAddons from the WornArmor duplicate (source 2) and removes
/// keyword-detected antler head parts from the NPC record + FaceGen (source 3).
/// Active in Create-and-Patch record mode and in SkyPatcher mode (either
/// PatchingMode); the caller gates on
/// <see cref="Settings.WigOrAntlerHandlingActive"/>.
/// </summary>
public class WigForwarder
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    private readonly RecordHandler _recordHandler;
    private readonly Settings _settings;
    private readonly OutfitDisplayResolver _outfitDisplayResolver;

    // Per appearance-mod-batch reuse cache (reset alongside RecordHandler.ResetMapping):
    // NPCs sharing the same donor WNAM/outfit + same wig set get the same duplicate.
    private readonly Dictionary<(FormKey Source, string WigSet), FormKey> _skinDuplicates = new();
    private readonly Dictionary<(FormKey Source, string WigSet), FormKey> _outfitDuplicates = new();
    private readonly object _lock = new();

    public WigForwarder(EnvironmentStateProvider environmentStateProvider, RecordHandler recordHandler,
        Settings settings, OutfitDisplayResolver outfitDisplayResolver)
    {
        _environmentStateProvider = environmentStateProvider;
        _recordHandler = recordHandler;
        _settings = settings;
        _outfitDisplayResolver = outfitDisplayResolver;
    }

    public sealed class Result
    {
        public FormKey? SkinDuplicateKey { get; set; }
        public FormKey? OutfitDuplicateKey { get; set; }
        public List<MajorRecord> MergedRecords { get; } = new();
        public bool OutfitForwarded => OutfitDuplicateKey != null;

        /// <summary>Donor-side FormKeys of the Hair-type head parts to remove
        /// from the patched NPC record and REPLACE with the modeless bald hair
        /// (ForwardToSkin with a hair-slot wig only — the skin-carried wig does
        /// not suppress the head part hair in game the way an equipped one does,
        /// so record + FaceGen NIF both clash without removal). Empty when
        /// nothing needs removing (e.g. antler-only forwarding keeps the real
        /// hair).</summary>
        public HashSet<FormKey> DonorHairHeadPartKeys { get; } = new();

        /// <summary>Donor-side FormKeys of the antler head parts (source 3) to
        /// remove from the patched NPC record with NO replacement (antler
        /// Remove — an antler isn't a required head-part type, so removing it
        /// doesn't back-fill anything the way removing hair does).</summary>
        public HashSet<FormKey> DonorAntlerHeadPartKeys { get; } = new();

        /// <summary>FaceGen NIF shape names to strip alongside the record
        /// removals: the removed hair / antler head parts' EditorIDs plus their
        /// ExtraParts' EditorIDs — baked FaceGen shapes are named after the head
        /// part EditorIDs.</summary>
        public HashSet<string> FaceGenShapeNamesToStrip { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Points the patched NPC at the +Wig duplicates. Called AFTER
        /// CopyAppearanceData (whose non-merge path resets WNAM to the donor
        /// original, and whose outfit branch may null/replace DefaultOutfit).</summary>
        public void ApplyLinksTo(Npc patchNpc)
        {
            if (SkinDuplicateKey is { } skin) patchNpc.WornArmor.SetTo(skin);
            if (OutfitDuplicateKey is { } outfit) patchNpc.DefaultOutfit.SetTo(outfit);
        }
    }

    /// <summary>EditorID of the generated modeless bald hair head part (one
    /// per output plugin). Mirrors High Poly NPC Overhaul's
    /// <c>HighPoly_HairBald</c>: an NPC whose hair comes from a skin-carried
    /// wig must still HAVE a Hair-type head part — with none assigned, the
    /// engine back-fills a random hair from the race's chargen head data
    /// (user-verified in game), which both clashes with the wig and
    /// dark-faces the NPC. A modeless hair satisfies the engine and renders
    /// nothing.</summary>
    public const string BaldHairEditorId = "NPC2_HairBald";

    // HeadPartsAllRacesMinusBeast [FLST:0A803F] — the ValidRaces list High
    // Poly NPC Overhaul's HighPoly_HairBald points at (read from its record
    // bytes: RNAM=3F800A00, DATA=06 = Male|Female non-playable, no MODL).
    private static readonly FormKey HeadPartsAllRacesMinusBeastKey = FormKey.Factory("0A803F:Skyrim.esm");

    private FormKey GetOrCreateBaldHairHeadPart()
    {
        lock (_lock)
        {
            var outputMod = _environmentStateProvider.OutputMod;
            var existing = outputMod.HeadParts.FirstOrDefault(h =>
                string.Equals(h.EditorID, BaldHairEditorId, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing.FormKey;

            var bald = outputMod.HeadParts.AddNew();
            bald.EditorID = BaldHairEditorId;
            bald.Type = HeadPart.TypeEnum.Hair;
            bald.Flags = HeadPart.Flag.Male | HeadPart.Flag.Female;
            bald.ValidRaces.SetTo(HeadPartsAllRacesMinusBeastKey);
            RecordProvenanceDiag.RecordGenerated(bald.FormKey, bald.EditorID, "HeadPart");
            return bald.FormKey;
        }
    }

    /// <summary>
    /// Applies a forwarding result to the patched NPC record: points WNAM /
    /// DefaultOutfit at the +Wig duplicates and REPLACES the superseded hair
    /// head parts (donor keys AND their merged-in output duplicates — the
    /// merge may have remapped them by the time this runs) with the shared
    /// modeless bald hair record. Call AFTER CopyAppearanceData in the record
    /// path, or right after surrogate creation (before the merge walker) in
    /// the SkyPatcher Create path. The matching FaceGen NIF shape removal
    /// happens later, after the asset copy completes (see Patcher's pending
    /// wig NIF edits).
    /// </summary>
    public void FinalizeNpcRecord(Result result, Npc patchNpc, string npcIdentifier,
        Action<string, bool, bool> appendLog)
    {
        result.ApplyLinksTo(patchNpc);

        // Antler head parts (source 3, antler Remove): remove with NO replacement
        // — an antler isn't a required head-part type, so nothing back-fills.
        if (result.DonorAntlerHeadPartKeys.Count > 0)
        {
            var antlerRemoveKeys = ExpandWithDuplicateMappings(result.DonorAntlerHeadPartKeys);
            int removedAntler = patchNpc.HeadParts.RemoveAll(l => l != null && antlerRemoveKeys.Contains(l.FormKey));
            if (removedAntler > 0)
            {
                appendLog($"      Antler handling (Remove): removed {removedAntler} antler head part(s) from " +
                          $"{npcIdentifier}; the baked FaceGen shape(s) are stripped after asset copy.",
                    false, false);
            }
        }

        // Hair head parts (wig ForwardToSkin): remove AND replace with the shared
        // modeless bald hair — an NPC with no Hair head part back-fills a random
        // race-chargen hair (and dark-faces), so removal alone is not enough.
        if (result.DonorHairHeadPartKeys.Count > 0)
        {
            var hairRemoveKeys = ExpandWithDuplicateMappings(result.DonorHairHeadPartKeys);
            int removed = patchNpc.HeadParts.RemoveAll(l => l != null && hairRemoveKeys.Contains(l.FormKey));
            if (removed > 0)
            {
                var baldKey = GetOrCreateBaldHairHeadPart();
                if (patchNpc.HeadParts.All(l => l.FormKey != baldKey))
                {
                    patchNpc.HeadParts.Add(baldKey.ToLink<IHeadPartGetter>());
                }

                appendLog($"      Wig handling: replaced {removed} hair head part(s) on {npcIdentifier} " +
                          $"with the modeless '{BaldHairEditorId}' record (the forwarded wig supplies the hair; " +
                          "the baked FaceGen shape(s) are stripped after asset copy).",
                    false, false);
            }
        }
    }

    /// <summary>Donor head-part keys plus any output duplicates the merge remapped
    /// them to (the merge may have run before FinalizeNpcRecord, so the patched NPC
    /// may reference the mapped key rather than the donor key).</summary>
    private HashSet<FormKey> ExpandWithDuplicateMappings(HashSet<FormKey> donorKeys)
    {
        var set = new HashSet<FormKey>(donorKeys);
        foreach (var donorKey in donorKeys)
        {
            if (_recordHandler.TryGetDuplicateMapping(donorKey, out var mapped)) set.Add(mapped);
        }
        return set;
    }

    /// <summary>Clears the per-batch duplicate reuse caches. Call alongside
    /// <see cref="RecordHandler.ResetMapping"/> — the seeded mappings these
    /// duplicates rely on die with the batch.</summary>
    public void ResetCache()
    {
        lock (_lock)
        {
            _skinDuplicates.Clear();
            _outfitDuplicates.Clear();
        }
    }

    /// <summary>
    /// Applies the effective wig AND antler handling modes for one NPC,
    /// evaluating the two classes independently. Returns null when nothing was
    /// forwarded, stripped, or removed. Must run BEFORE CopyAppearanceData /
    /// dependency merge-in so the seeded WNAM mapping takes effect; the caller
    /// then invokes <see cref="Result.ApplyLinksTo"/> (via
    /// <see cref="FinalizeNpcRecord"/>) on the patched NPC record afterwards.
    ///
    /// <para>Routing per class (wig / antler): ForwardToSkin transfers the
    /// pieces' ArmorAddons into one shared WNAM duplicate; ForwardToOutfit adds
    /// them to the worn outfit; antler Remove keeps them off entirely — stripped
    /// from a forwarded outfit (source 1), from the WornArmor duplicate (source 2:
    /// baked-in ARMAs), and from the NPC record + FaceGen (source 3: baked head
    /// parts). A single NPC may need both a skin duplicate and an outfit
    /// duplicate (e.g. wig→skin + antler→outfit).</para>
    /// </summary>
    public Result? Apply(
        FormKey targetNpcFormKey,
        INpcGetter donorNpc,
        ModSetting appearanceModSetting,
        ModKey donorContextModKey,
        HashSet<string> modFolderPaths,
        bool mergeInDependencyRecords,
        bool includeOutfit,
        string npcIdentifier,
        Action<string, bool, bool> appendLog)
    {
        var wigMode = _settings.GetEffectiveWigMode(appearanceModSetting);
        var antlerMode = _settings.GetEffectiveAntlerMode(appearanceModSetting);
        if (wigMode == WigHandlingMode.None && antlerMode == AntlerHandlingMode.None) return null;

        var modKeys = appearanceModSetting.CorrespondingModKeys;

        // Applicable outfit pieces = the donor outfit's direct items the scan
        // classified, partitioned by class (wigs nested in leveled lists are not
        // forwarded — the detection is outfit-item based by design).
        var donorOutfitGetter = ResolveFromModsOrWinner<IOutfitGetter>(donorNpc.DefaultOutfit, modKeys, modFolderPaths);
        var wigItems = new List<(FormKey Key, bool IsAntler)>();
        var antlerItems = new List<(FormKey Key, bool IsAntler)>();
        if (donorOutfitGetter?.Items != null)
        {
            foreach (var item in donorOutfitGetter.Items)
            {
                if (item == null || item.IsNull) continue;
                if (appearanceModSetting.DetectedAntlerArmors.Contains(item.FormKey))
                    antlerItems.Add((item.FormKey, true));
                else if (appearanceModSetting.DetectedWigArmors.Contains(item.FormKey))
                    wigItems.Add((item.FormKey, false));
            }
        }

        // Resolve the donor WNAM up front: ForwardToSkin needs a resolvable WNAM
        // to transfer into. hasWnam = present AND resolvable so the no-WNAM
        // fallback (→ ForwardToOutfit) also covers an unresolvable link.
        var wnamKey = donorNpc.WornArmor.IsNull ? FormKey.Null : donorNpc.WornArmor.FormKey;
        var wnamGetter = donorNpc.WornArmor.IsNull
            ? null
            : ResolveFromModsOrWinner<IArmorGetter>(donorNpc.WornArmor, modKeys, modFolderPaths);
        bool hasWnam = wnamGetter != null;

        // Per-class effective routing (the no-WNAM fallback flips ForwardToSkin →
        // ForwardToOutfit for both classes, since they share the one donor WNAM).
        bool wigToSkin = wigMode == WigHandlingMode.ForwardToSkin && hasWnam && wigItems.Count > 0;
        bool wigToOutfit = wigItems.Count > 0 &&
            (wigMode == WigHandlingMode.ForwardToOutfit ||
             (wigMode == WigHandlingMode.ForwardToSkin && !hasWnam));
        bool antlerToSkin = antlerMode == AntlerHandlingMode.ForwardToSkin && hasWnam && antlerItems.Count > 0;
        bool antlerToOutfit = antlerItems.Count > 0 &&
            (antlerMode == AntlerHandlingMode.ForwardToOutfit ||
             (antlerMode == AntlerHandlingMode.ForwardToSkin && !hasWnam));
        bool antlerRemove = antlerMode == AntlerHandlingMode.Remove;

        if (wigMode == WigHandlingMode.ForwardToSkin && !hasWnam && wigItems.Count > 0)
            appendLog($"      Wig handling: {npcIdentifier} has no WNAM in the appearance plugin — the wig falls back to ForwardToOutfit.",
                false, false);
        if (antlerMode == AntlerHandlingMode.ForwardToSkin && !hasWnam && antlerItems.Count > 0)
            appendLog($"      Antler handling: {npcIdentifier} has no WNAM in the appearance plugin — the antler falls back to ForwardToOutfit.",
                false, false);

        // Pieces routed to the skin (union), added to the worn outfit, and pieces
        // that must NOT remain in a forwarded worn outfit (moved to skin, or
        // antler-removed).
        var skinPieces = new List<(FormKey Key, bool IsAntler)>();
        if (wigToSkin) skinPieces.AddRange(wigItems);
        if (antlerToSkin) skinPieces.AddRange(antlerItems);

        var outfitAddPieces = new List<(FormKey Key, bool IsAntler)>();
        if (wigToOutfit) outfitAddPieces.AddRange(wigItems);
        if (antlerToOutfit) outfitAddPieces.AddRange(antlerItems);

        var outfitStripPieces = new List<(FormKey Key, bool IsAntler)>();
        if (wigToSkin) outfitStripPieces.AddRange(wigItems);
        if (antlerToSkin || antlerRemove) outfitStripPieces.AddRange(antlerItems);

        // Source 2: antler ArmorAddons baked directly into the WornArmor — antler
        // Remove strips them from the WNAM duplicate.
        var wnamAntlerRemovals = new HashSet<FormKey>();
        if (antlerRemove && wnamGetter?.Armature != null)
        {
            foreach (var armaLink in wnamGetter.Armature)
            {
                if (armaLink != null && !armaLink.IsNull &&
                    appearanceModSetting.DetectedAntlerArmatures.Contains(armaLink.FormKey))
                {
                    wnamAntlerRemovals.Add(armaLink.FormKey);
                }
            }
        }

        var result = new Result();

        // 1) Skin duplicate — built when there are pieces to ADD (skinPieces) or
        //    antler ARMAs to REMOVE from the WornArmor (source 2 Remove).
        if (skinPieces.Count > 0 || wnamAntlerRemovals.Count > 0)
        {
            BuildSkinDuplicate(donorNpc, wnamKey, wnamGetter!, appearanceModSetting, donorContextModKey,
                modFolderPaths, mergeInDependencyRecords, wigMode, antlerMode, skinPieces,
                wnamAntlerRemovals, result, npcIdentifier, appendLog);
        }

        // Source 3: antler head parts baked into the FaceGen — Remove collects
        // them for record removal (no bald replacement) + FaceGen shape strip.
        if (antlerRemove)
        {
            CollectAntlerHeadPartRemoval(donorNpc, appearanceModSetting, modFolderPaths, result);
        }

        // 2) Outfit forward — add pieces routed to the worn outfit (and strip
        //    skin/Remove pieces from that same duplicate).
        if (outfitAddPieces.Count > 0)
        {
            TryForwardToOutfit(targetNpcFormKey, donorNpc, appearanceModSetting, donorContextModKey,
                modFolderPaths, mergeInDependencyRecords, wigMode, antlerMode, outfitAddPieces,
                outfitStripPieces, result, npcIdentifier, appendLog);
        }

        // 3) Include-Outfit strip of the forwarded DONOR outfit (only when step 2
        //    did not already produce an outfit duplicate). Removes the pieces that
        //    moved to skin / must be removed, keeping the rest of the outfit.
        if (result.OutfitDuplicateKey == null && includeOutfit && outfitStripPieces.Count > 0 &&
            donorOutfitGetter != null)
        {
            StripWigsFromForwardedOutfit(result, donorOutfitGetter, outfitStripPieces, wigMode, antlerMode,
                appearanceModSetting, donorContextModKey, modFolderPaths, mergeInDependencyRecords,
                npcIdentifier, appendLog);
        }

        // 4) Antler Remove requested but no forwarded outfit to strip: the load
        //    order owns the worn outfit and Remove cannot reach it. (The antler is
        //    still not forwarded, so it only shows if the worn outfit already
        //    carries it.)
        if (antlerRemove && antlerItems.Count > 0 && result.OutfitDuplicateKey == null && !includeOutfit)
        {
            appendLog($"      Antler handling (Remove): {npcIdentifier}'s worn outfit is not being forwarded " +
                      "(Include Outfit off), so the outfit antler cannot be stripped here — the load order owns " +
                      "the worn outfit. The antler is not forwarded, so it will only appear if that outfit already carries it.",
                false, false);
        }

        if (result.SkinDuplicateKey == null && result.OutfitDuplicateKey == null &&
            result.DonorHairHeadPartKeys.Count == 0 && result.DonorAntlerHeadPartKeys.Count == 0)
        {
            return null;
        }
        return result;
    }

    /// <summary>
    /// Builds (or reuses) the one shared WornArmor duplicate that carries the
    /// skin-forwarded pieces of BOTH classes and, for antler Remove, has the
    /// baked-in antler ArmorAddons (source 2) stripped. Populates
    /// <paramref name="result"/> with the skin key, merged records, and hair
    /// removal. The donor WNAM is already resolved (<paramref name="wnamGetter"/>).
    /// </summary>
    private void BuildSkinDuplicate(
        INpcGetter donorNpc,
        FormKey wnamKey,
        IArmorGetter wnamGetter,
        ModSetting appearanceModSetting,
        ModKey donorContextModKey,
        HashSet<string> modFolderPaths,
        bool mergeInDependencyRecords,
        WigHandlingMode wigMode,
        AntlerHandlingMode antlerMode,
        List<(FormKey Key, bool IsAntler)> skinPieces,
        HashSet<FormKey> wnamAntlerRemovals,
        Result result,
        string npcIdentifier,
        Action<string, bool, bool> appendLog)
    {
        // Whether any WIG-class skin piece actually transfers a hair-slot ARMA —
        // keyed on the wig class so a slot-42 antler never triggers hair removal,
        // and computed from the set (not what got appended) so the reuse path
        // reaches the same decision.
        bool transfersHairSlot = skinPieces.Any(w => !w.IsAntler &&
            ResolveFromModsOrWinner<IArmorGetter>(w.Key.ToLink<IArmorGetter>(),
                appearanceModSetting.CorrespondingModKeys, modFolderPaths) is { } armo &&
            WigDetector.GetForwardableArmatures(armo, false,
                    fk => ResolveFromModsOrWinner<IArmorAddonGetter>(fk.ToLink<IArmorAddonGetter>(),
                        appearanceModSetting.CorrespondingModKeys, modFolderPaths))
                .Any(link => ResolveFromModsOrWinner<IArmorAddonGetter>(
                                 link.FormKey.ToLink<IArmorAddonGetter>(),
                                 appearanceModSetting.CorrespondingModKeys, modFolderPaths)
                             ?.BodyTemplate?.FirstPersonFlags is { } flags &&
                             (flags & BipedObjectFlag.Hair) != 0));

        // Reuse key folds both modes, the added set, and the removed set — two
        // NPCs sharing the same WNAM + config + sets share the duplicate.
        string skinKey = ModePairTag(wigMode, antlerMode) + "|add:" + BuildWigSetId(skinPieces) +
                         "|rm:" + BuildKeySetId(wnamAntlerRemovals);
        lock (_lock)
        {
            if (_skinDuplicates.TryGetValue((wnamKey, skinKey), out var existing))
            {
                // Re-seed (a different set may have overwritten the mapping) and
                // re-collect hair removal (per-NPC — each NPC's own head parts).
                _recordHandler.SeedDuplicateMapping(wnamKey, existing);
                result.SkinDuplicateKey = existing;
                if (transfersHairSlot)
                {
                    CollectHairRemoval(donorNpc, appearanceModSetting, modFolderPaths, result);
                }
                return;
            }
        }

        if (!Auxilliary.TryDuplicateGenericRecordAsNew(wnamGetter, _environmentStateProvider.OutputMod,
                out dynamic? dupDyn, out string exceptionString) || dupDyn is not Armor dup)
        {
            appendLog($"      Wig handling ERROR for {npcIdentifier}: could not duplicate WNAM {wnamKey}: {exceptionString}",
                true, true);
            return;
        }

        // Keep the donor's EditorID (the duplicate IS the merged representation of
        // that WNAM — OutputValidator compares skins by resolved EditorID).
        dup.EditorID = wnamGetter.EditorID;

        result.SkinDuplicateKey = dup.FormKey;
        result.MergedRecords.Add(dup);

        int appended = AppendForwardableArmatures(dup.Armature, skinPieces, appearanceModSetting,
            modFolderPaths, out var appendedSlots);

        // The engine only dresses the biped slots the ARMO's own BOD2 declares —
        // ArmorAddons on slots outside the parent armor's mask are ignored at
        // runtime (user-verified in game: the wig/antlers never appear without
        // this). Widen the duplicate's slot mask to cover the transferred ARMAs.
        if (appendedSlots != 0)
        {
            dup.BodyTemplate ??= new BodyTemplate();
            dup.BodyTemplate.FirstPersonFlags |= appendedSlots;
        }

        // Source 2 (antler Remove): drop the baked-in antler ArmorAddons from the
        // duplicate's Armature. The BOD2 mask is left as-is — removing addons just
        // gives the engine fewer to dress; narrowing the mask is unnecessary.
        int removedArmas = 0;
        if (wnamAntlerRemovals.Count > 0)
        {
            removedArmas = dup.Armature.RemoveAll(a => a != null && wnamAntlerRemovals.Contains(a.FormKey));
        }

        // A skin-carried hair-slot wig does NOT suppress the NPC's head part hair
        // the way an equipped one does (user-verified in game: both meshes render
        // and clash), so when a hair-slot piece is transferred, collect the donor's
        // Hair-type head parts for removal — from the record (FinalizeNpcRecord)
        // and from the baked FaceGen NIF (stripped post asset-copy). Antler-only
        // forwarding transfers no hair slot and keeps the real hair.
        if (transfersHairSlot)
        {
            CollectHairRemoval(donorNpc, appearanceModSetting, modFolderPaths, result);
        }

        _recordHandler.RecordMergedRecordOrigin(wnamKey, dup.FormKey, wnamGetter.EditorID);
        RecordProvenanceDiag.RecordMergedAsNew(wnamKey, wnamGetter.EditorID, "Armor", dup.FormKey, null);

        // Seed BEFORE the merge walkers run: the original WNAM is never duplicated,
        // and every reference to it redirects to this duplicate.
        _recordHandler.SeedDuplicateMapping(wnamKey, dup.FormKey);
        lock (_lock) { _skinDuplicates[(wnamKey, skinKey)] = dup.FormKey; }

        if (mergeInDependencyRecords)
        {
            List<string> exceptions = new();
            var merged = _recordHandler.DuplicateFromOnlyReferencedGetters(
                _environmentStateProvider.OutputMod, dup, appearanceModSetting.CorrespondingModKeys,
                donorContextModKey, onlySubRecords: true, appearanceModSetting.HandleInjectedRecords,
                modFolderPaths, ref exceptions);
            result.MergedRecords.AddRange(merged);
            if (exceptions.Any())
            {
                appendLog("Exceptions during wig skin-forwarding merge for " + npcIdentifier + ":" +
                          Environment.NewLine + string.Join(Environment.NewLine, exceptions), true, false);
            }
        }

        string addPart = appended > 0 ? $"transferred {appended} wig/antler ArmorAddon(s)" : "no ArmorAddons transferred";
        string rmPart = removedArmas > 0 ? $", stripped {removedArmas} baked antler ArmorAddon(s)" : "";
        appendLog($"      Wig/antler handling (skin): duplicated WNAM {wnamKey} as {dup.FormKey} — {addPart}{rmPart}.",
            false, false);
    }

    /// <summary>
    /// Adds <paramref name="addPieces"/> (ForwardToOutfit pieces of either class)
    /// to a duplicate of the outfit the NPC actually wears in game, and removes
    /// <paramref name="stripPieces"/> (pieces moved to skin, or antler-removed)
    /// from that same duplicate. Populates <paramref name="result"/>.
    /// </summary>
    private void TryForwardToOutfit(
        FormKey targetNpcFormKey,
        INpcGetter donorNpc,
        ModSetting appearanceModSetting,
        ModKey donorContextModKey,
        HashSet<string> modFolderPaths,
        bool mergeInDependencyRecords,
        WigHandlingMode wigMode,
        AntlerHandlingMode antlerMode,
        List<(FormKey Key, bool IsAntler)> addPieces,
        List<(FormKey Key, bool IsAntler)> stripPieces,
        Result result,
        string npcIdentifier,
        Action<string, bool, bool> appendLog)
    {
        // The outfit the NPC will actually wear in game absent forwarding:
        // plugin-level effective outfit (patch-mode aware) + SkyPatcher/SPID
        // runtime layers — the same simulation the renderer depicts.
        var display = _outfitDisplayResolver.ResolveForDisplay(
            targetNpcFormKey, donorNpc.FormKey, appearanceModSetting, includeDefaultOutfitRenderFlag: true);

        IOutfitGetter? effectiveOutfit = null;
        if (display.OutfitFormKey is { } effectiveKey)
        {
            var link = effectiveKey.ToLink<IOutfitGetter>();
            effectiveOutfit = display.UseModScopedResolution
                ? ResolveFromModsOrWinner<IOutfitGetter>(link, appearanceModSetting.CorrespondingModKeys, modFolderPaths)
                : ResolveWinner<IOutfitGetter>(effectiveKey);
        }

        var stripKeys = stripPieces.Select(s => s.Key).ToHashSet();

        // Already correct (contains every add piece AND none of the strip pieces):
        // nothing to do.
        if (effectiveOutfit?.Items != null)
        {
            var itemKeys = effectiveOutfit.Items.Where(i => i != null && !i.IsNull)
                .Select(i => i.FormKey).ToHashSet();
            if (addPieces.All(w => itemKeys.Contains(w.Key)) && !itemKeys.Overlaps(stripKeys))
            {
                return;
            }
        }

        string setId = ModePairTag(wigMode, antlerMode) + "|add:" + BuildWigSetId(addPieces) +
                       "|strip:" + BuildKeySetId(stripKeys);
        FormKey sourceKey = display.OutfitFormKey ?? FormKey.Null;
        lock (_lock)
        {
            if (_outfitDuplicates.TryGetValue((sourceKey, setId), out var existing))
            {
                result.OutfitDuplicateKey = existing;
                return;
            }
        }

        Outfit dupOutfit;
        if (effectiveOutfit != null)
        {
            if (!Auxilliary.TryDuplicateGenericRecordAsNew(effectiveOutfit, _environmentStateProvider.OutputMod,
                    out dynamic? dupDyn, out string exceptionString) || dupDyn is not Outfit dup)
            {
                appendLog($"      Wig handling ERROR for {npcIdentifier}: could not duplicate outfit {display.OutfitFormKey}: {exceptionString}",
                    true, true);
                return;
            }

            dupOutfit = dup;
            dupOutfit.EditorID = effectiveOutfit.EditorID;
            _recordHandler.RecordMergedRecordOrigin(effectiveOutfit.FormKey, dupOutfit.FormKey, effectiveOutfit.EditorID);
            RecordProvenanceDiag.RecordMergedAsNew(effectiveOutfit.FormKey, effectiveOutfit.EditorID, "Outfit",
                dupOutfit.FormKey, null);
        }
        else
        {
            // No outfit anywhere in the stack: author a minimal wig/antler-only
            // outfit so the forwarded piece still shows in game.
            dupOutfit = _environmentStateProvider.OutputMod.Outfits.AddNew();
            dupOutfit.EditorID = "NPC2_WigOutfit_" + (donorNpc.EditorID ?? donorNpc.FormKey.ToString().Replace(":", "_"));
            RecordProvenanceDiag.RecordGenerated(dupOutfit.FormKey, dupOutfit.EditorID, "Outfit");
        }

        var items = dupOutfit.Items ??= new ExtendedList<IFormLinkGetter<IOutfitTargetGetter>>();
        var existingItems = items.Where(i => i != null && !i.IsNull).Select(i => i.FormKey).ToHashSet();
        foreach (var (addKey, _) in addPieces)
        {
            if (existingItems.Add(addKey))
            {
                items.Add(addKey.ToLink<IOutfitTargetGetter>());
            }
        }

        int stripped = stripKeys.Count > 0 ? items.RemoveAll(l => l != null && stripKeys.Contains(l.FormKey)) : 0;

        result.OutfitDuplicateKey = dupOutfit.FormKey;
        result.MergedRecords.Add(dupOutfit);
        lock (_lock) { _outfitDuplicates[(sourceKey, setId)] = dupOutfit.FormKey; }

        // Merge the donor-side chain (the forwarded ARMO + its armatures/textures —
        // and, for a mod-scoped source outfit, its other donor items). Items owned
        // by other load-order plugins are left as-is and become masters, matching
        // how the winning outfit's contents behave everywhere else. Deliberately NO
        // SeedDuplicateMapping for the source outfit: other NPCs sharing it must not
        // be redirected to this duplicate.
        if (mergeInDependencyRecords)
        {
            List<string> exceptions = new();
            var merged = _recordHandler.DuplicateFromOnlyReferencedGetters(
                _environmentStateProvider.OutputMod, dupOutfit, appearanceModSetting.CorrespondingModKeys,
                donorContextModKey, onlySubRecords: true, appearanceModSetting.HandleInjectedRecords,
                modFolderPaths, ref exceptions);
            result.MergedRecords.AddRange(merged);
            if (exceptions.Any())
            {
                appendLog("Exceptions during wig outfit-forwarding merge for " + npcIdentifier + ":" +
                          Environment.NewLine + string.Join(Environment.NewLine, exceptions), true, false);
            }
        }

        string stripNote = stripped > 0 ? $", stripped {stripped} skin/removed item(s)" : "";
        appendLog($"      Wig/antler handling (ForwardToOutfit): duplicated outfit " +
                  $"{(display.OutfitFormKey?.ToString() ?? "(none — authored new)")} as {dupOutfit.FormKey} " +
                  $"and added {addPieces.Count} wig/antler armor(s){stripNote} " +
                  $"(effective outfit source: {display.Source}{(display.SourceDetail != null ? ", " + display.SourceDetail : "")}).",
            false, false);

        if (display.Source is OutfitDisplaySource.SkyPatcher or OutfitDisplaySource.Spid)
        {
            appendLog($"      WARNING: {npcIdentifier}'s outfit is distributed at runtime by " +
                      $"{display.Source} ({display.SourceDetail ?? "unknown config"}); the forwarded wig/antler outfit " +
                      "may be overridden in game depending on distributor load order.", false, true);
        }
    }

    /// <summary>
    /// ForwardToSkin companion for Include Outfit: duplicates the donor's
    /// outfit (its mod-scoped WINNING content — including the case where the
    /// mod only OVERRIDES a foundation outfit record) with the forwarded
    /// wig/antler items removed, and hands the duplicate back on
    /// <paramref name="result"/> so <see cref="Result.ApplyLinksTo"/> assigns
    /// it. Duplicate-and-strip rather than an in-place override edit: other
    /// NPCs sharing the original outfit keep their wig. Also has the welcome
    /// side effect of carrying the mod's outfit content into the output, so
    /// the assignment no longer depends on the (soon-disabled) appearance
    /// plugin winning the outfit record in the load order.
    /// </summary>
    private void StripWigsFromForwardedOutfit(
        Result result,
        IOutfitGetter donorOutfit,
        List<(FormKey Key, bool IsAntler)> stripPieces,
        WigHandlingMode wigMode,
        AntlerHandlingMode antlerMode,
        ModSetting appearanceModSetting,
        ModKey donorContextModKey,
        HashSet<string> modFolderPaths,
        bool mergeInDependencyRecords,
        string npcIdentifier,
        Action<string, bool, bool> appendLog)
    {
        string wigSetId = "strip:" + ModePairTag(wigMode, antlerMode) + ":" + BuildWigSetId(stripPieces);
        lock (_lock)
        {
            if (_outfitDuplicates.TryGetValue((donorOutfit.FormKey, wigSetId), out var existing))
            {
                result.OutfitDuplicateKey = existing;
                return;
            }
        }

        if (!Auxilliary.TryDuplicateGenericRecordAsNew(donorOutfit, _environmentStateProvider.OutputMod,
                out dynamic? dupDyn, out string exceptionString) || dupDyn is not Outfit dup)
        {
            appendLog($"      Wig handling ERROR for {npcIdentifier}: could not duplicate forwarded outfit " +
                      $"{donorOutfit.FormKey} for wig removal: {exceptionString}", true, true);
            return;
        }

        dup.EditorID = donorOutfit.EditorID;
        var removeKeys = stripPieces.Select(w => w.Key).ToHashSet();
        int removed = dup.Items?.RemoveAll(l => l != null && removeKeys.Contains(l.FormKey)) ?? 0;

        _recordHandler.RecordMergedRecordOrigin(donorOutfit.FormKey, dup.FormKey, donorOutfit.EditorID);
        RecordProvenanceDiag.RecordMergedAsNew(donorOutfit.FormKey, donorOutfit.EditorID, "Outfit",
            dup.FormKey, null);

        result.OutfitDuplicateKey = dup.FormKey;
        result.MergedRecords.Add(dup);
        lock (_lock) { _outfitDuplicates[(donorOutfit.FormKey, wigSetId)] = dup.FormKey; }

        if (mergeInDependencyRecords)
        {
            List<string> exceptions = new();
            var merged = _recordHandler.DuplicateFromOnlyReferencedGetters(
                _environmentStateProvider.OutputMod, dup, appearanceModSetting.CorrespondingModKeys,
                donorContextModKey, onlySubRecords: true, appearanceModSetting.HandleInjectedRecords,
                modFolderPaths, ref exceptions);
            result.MergedRecords.AddRange(merged);
            if (exceptions.Any())
            {
                appendLog("Exceptions during wig outfit-strip merge for " + npcIdentifier + ":" +
                          Environment.NewLine + string.Join(Environment.NewLine, exceptions), true, false);
            }
        }

        appendLog($"      Wig/antler handling: duplicated forwarded outfit {donorOutfit.FormKey} " +
                  $"as {dup.FormKey} with {removed} wig/antler item(s) removed (moved to skin, or antler Remove).",
            false, false);
    }

    /// <summary>Collects the donor NPC's Hair-type head parts into
    /// <paramref name="result"/>: record keys for the NPC-record removal, and
    /// shape names (the head parts' EditorIDs plus their ExtraParts' EditorIDs,
    /// e.g. hairlines — baked FaceGen shapes are named after them) for the
    /// FaceGen NIF strip.</summary>
    private void CollectHairRemoval(INpcGetter donorNpc, ModSetting appearanceModSetting,
        HashSet<string> modFolderPaths, Result result)
    {
        foreach (var hpLink in donorNpc.HeadParts)
        {
            if (hpLink == null || hpLink.IsNull) continue;
            var hpRec = ResolveFromModsOrWinner<IHeadPartGetter>(hpLink,
                appearanceModSetting.CorrespondingModKeys, modFolderPaths);
            if (hpRec?.Type != HeadPart.TypeEnum.Hair) continue;

            result.DonorHairHeadPartKeys.Add(hpLink.FormKey);
            AddShapeNames(hpRec, appearanceModSetting, modFolderPaths, result);
        }
    }

    /// <summary>Collects the donor NPC's keyword-detected antler head parts
    /// (source 3) into <paramref name="result"/> for removal — record keys (no
    /// bald replacement) and FaceGen shape names to strip. Only head parts the
    /// scan flagged (<see cref="ModSetting.DetectedAntlerHeadParts"/>) qualify;
    /// non-intelligible names need manual designation, a separate feature.</summary>
    private void CollectAntlerHeadPartRemoval(INpcGetter donorNpc, ModSetting appearanceModSetting,
        HashSet<string> modFolderPaths, Result result)
    {
        if (appearanceModSetting.DetectedAntlerHeadParts.Count == 0) return;
        foreach (var hpLink in donorNpc.HeadParts)
        {
            if (hpLink == null || hpLink.IsNull) continue;
            if (!appearanceModSetting.DetectedAntlerHeadParts.Contains(hpLink.FormKey)) continue;
            result.DonorAntlerHeadPartKeys.Add(hpLink.FormKey);
            var hpRec = ResolveFromModsOrWinner<IHeadPartGetter>(hpLink,
                appearanceModSetting.CorrespondingModKeys, modFolderPaths);
            if (hpRec != null) AddShapeNames(hpRec, appearanceModSetting, modFolderPaths, result);
        }
    }

    /// <summary>Adds a head part's EditorID and its ExtraParts' EditorIDs to the
    /// FaceGen shape-strip set (baked shapes are named after the head part
    /// EditorIDs).</summary>
    private void AddShapeNames(IHeadPartGetter hpRec, ModSetting appearanceModSetting,
        HashSet<string> modFolderPaths, Result result)
    {
        if (!string.IsNullOrEmpty(hpRec.EditorID)) result.FaceGenShapeNamesToStrip.Add(hpRec.EditorID);
        if (hpRec.ExtraParts != null)
        {
            foreach (var extraLink in hpRec.ExtraParts)
            {
                if (extraLink == null || extraLink.IsNull) continue;
                var extraRec = ResolveFromModsOrWinner<IHeadPartGetter>(extraLink,
                    appearanceModSetting.CorrespondingModKeys, modFolderPaths);
                if (!string.IsNullOrEmpty(extraRec?.EditorID)) result.FaceGenShapeNamesToStrip.Add(extraRec.EditorID);
            }
        }
    }

    /// <summary>Appends the forwardable ArmorAddons of each applicable wig
    /// (hair-slot ARMAs; ALL ARMAs for antlers) to <paramref name="armature"/>,
    /// skipping ones already present. Returns the number appended;
    /// <paramref name="appendedSlots"/> is the union of the appended ARMAs'
    /// biped slots, which the caller must fold into the parent armor's BOD2
    /// mask (the engine ignores addons on slots the armor doesn't declare).</summary>
    private int AppendForwardableArmatures(
        ExtendedList<IFormLinkGetter<IArmorAddonGetter>> armature,
        List<(FormKey Key, bool IsAntler)> applicableWigs,
        ModSetting appearanceModSetting,
        HashSet<string> modFolderPaths,
        out BipedObjectFlag appendedSlots)
    {
        var present = armature.Where(a => a != null && !a.IsNull).Select(a => a.FormKey).ToHashSet();
        int appended = 0;
        appendedSlots = 0;
        foreach (var (wigKey, isAntler) in applicableWigs)
        {
            var armo = ResolveFromModsOrWinner<IArmorGetter>(wigKey.ToLink<IArmorGetter>(),
                appearanceModSetting.CorrespondingModKeys, modFolderPaths);
            if (armo == null) continue;

            IArmorAddonGetter? ResolveArma(FormKey fk) =>
                ResolveFromModsOrWinner<IArmorAddonGetter>(fk.ToLink<IArmorAddonGetter>(),
                    appearanceModSetting.CorrespondingModKeys, modFolderPaths);

            foreach (var armaLink in WigDetector.GetForwardableArmatures(armo, isAntler, ResolveArma))
            {
                if (present.Add(armaLink.FormKey))
                {
                    armature.Add(armaLink.FormKey.ToLink<IArmorAddonGetter>());
                    appended++;
                    var arma = ResolveArma(armaLink.FormKey);
                    if (arma?.BodyTemplate != null)
                    {
                        appendedSlots |= arma.BodyTemplate.FirstPersonFlags;
                    }
                }
            }
        }

        return appended;
    }

    private static string BuildWigSetId(List<(FormKey Key, bool IsAntler)> applicableWigs)
        => string.Join("|", applicableWigs.Select(w => w.Key.ToString()).OrderBy(s => s, StringComparer.Ordinal));

    private static string BuildKeySetId(IEnumerable<FormKey> keys)
        => string.Join("|", keys.Select(k => k.ToString()).OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>Per-batch cache disambiguator: folds both class modes into the
    /// reuse key so duplicates never collide across different mode configs (the
    /// modes are per-mod and constant within a batch, but this keeps the keys
    /// self-describing and collision-proof).</summary>
    private static string ModePairTag(WigHandlingMode wig, AntlerHandlingMode antler)
        => (int)wig + "." + (int)antler;

    /// <summary>Resolves a record from the appearance mod's own plugins first
    /// (mod-scoped, matching where donor data actually comes from), falling
    /// back to the load-order winner for records the mod inherits from masters
    /// (e.g. a vanilla WNAM).</summary>
    private TGetter? ResolveFromModsOrWinner<TGetter>(IFormLinkGetter link, IEnumerable<ModKey> modKeys,
        HashSet<string> modFolderPaths)
        where TGetter : class, IMajorRecordGetter
    {
        if (link.IsNull) return null;
        if (_recordHandler.TryGetRecordFromMods(link, modKeys, modFolderPaths,
                RecordHandler.RecordLookupFallBack.None, out var record) && record is TGetter typed)
        {
            return typed;
        }

        return ResolveWinner<TGetter>(link.FormKey);
    }

    private TGetter? ResolveWinner<TGetter>(FormKey formKey)
        where TGetter : class, IMajorRecordGetter
    {
        var linkCache = _environmentStateProvider.LinkCache;
        if (linkCache == null || formKey.IsNull) return null;
        return linkCache.TryResolve<TGetter>(formKey, out var winner) ? winner : null;
    }
}
