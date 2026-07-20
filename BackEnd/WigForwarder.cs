using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

/// <summary>
/// Patch-time wig/antler forwarding (see <see cref="WigHandlingMode"/>).
/// Runs per NPC BEFORE the appearance merge: it authors the +Wig duplicate
/// records into the output mod and seeds the RecordHandler's duplicate-in
/// mapping so the subsequent merge walkers redirect to the duplicate instead
/// of also merging the original (ForwardToSkin), or hands back an outfit
/// duplicate for the Patcher to assign after <c>CopyAppearanceData</c>
/// (ForwardToOutfit — no mapping seed, because other NPCs legitimately share
/// the original outfit without the wig). Active in Create-and-Patch record
/// mode and in SkyPatcher mode (either PatchingMode); the caller gates on
/// <see cref="Settings.GetEffectiveWigMode"/>.
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
        public FormKey? SkinDuplicateKey { get; init; }
        public FormKey? OutfitDuplicateKey { get; init; }
        public List<MajorRecord> MergedRecords { get; } = new();
        public bool OutfitForwarded => OutfitDuplicateKey != null;

        /// <summary>Donor-side FormKeys of the Hair-type head parts to remove
        /// from the patched NPC record (ForwardToSkin with a hair-slot wig
        /// only — the skin-carried wig does not suppress the head part hair
        /// in game the way an equipped one does, so record + FaceGen NIF both
        /// clash without removal). Empty when nothing needs removing (e.g.
        /// antler-only forwarding keeps the real hair).</summary>
        public HashSet<FormKey> DonorHairHeadPartKeys { get; } = new();

        /// <summary>FaceGen NIF shape names to strip alongside the record
        /// removal: the removed hair head parts' EditorIDs plus their
        /// ExtraParts' EditorIDs (hairlines) — baked FaceGen shapes are named
        /// after the head part EditorIDs.</summary>
        public HashSet<string> HairShapeNames { get; } = new(StringComparer.OrdinalIgnoreCase);

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

        if (result.DonorHairHeadPartKeys.Count == 0) return;

        var removeKeys = new HashSet<FormKey>(result.DonorHairHeadPartKeys);
        foreach (var donorKey in result.DonorHairHeadPartKeys)
        {
            if (_recordHandler.TryGetDuplicateMapping(donorKey, out var mapped))
            {
                removeKeys.Add(mapped);
            }
        }

        int removed = patchNpc.HeadParts.RemoveAll(l => l != null && removeKeys.Contains(l.FormKey));
        if (removed > 0)
        {
            var baldKey = GetOrCreateBaldHairHeadPart();
            if (patchNpc.HeadParts.All(l => l.FormKey != baldKey))
            {
                patchNpc.HeadParts.Add(baldKey.ToLink<IHeadPartGetter>());
            }

            appendLog($"      Wig handling: replaced {removed} hair head part(s) on {npcIdentifier} " +
                      $"with the modeless '{BaldHairEditorId}' record (the forwarded wig supplies the hair; " +
                      $"the baked FaceGen shape(s) [{string.Join(", ", result.HairShapeNames)}] are stripped " +
                      "after asset copy).",
                false, false);
        }
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
    /// Applies the effective wig handling mode for one NPC. Returns null when
    /// there is nothing to forward (mode None, no detected wig in the donor's
    /// outfit, or the effective in-game outfit already contains the wig).
    /// Must run BEFORE CopyAppearanceData / dependency merge-in so the seeded
    /// WNAM mapping takes effect; the caller then invokes
    /// <see cref="Result.ApplyLinksTo"/> on the patched NPC record afterwards.
    /// </summary>
    public Result? Apply(
        FormKey targetNpcFormKey,
        INpcGetter donorNpc,
        ModSetting appearanceModSetting,
        ModKey donorContextModKey,
        HashSet<string> modFolderPaths,
        bool mergeInDependencyRecords,
        string npcIdentifier,
        Action<string, bool, bool> appendLog)
    {
        var mode = _settings.GetEffectiveWigMode(appearanceModSetting);
        if (mode == WigHandlingMode.None) return null;

        var modKeys = appearanceModSetting.CorrespondingModKeys;

        // Applicable wigs = the donor outfit's direct items that the analysis
        // scan classified as wigs/antlers. (Wigs nested in leveled lists are
        // not forwarded — none observed in the wild; the detection is
        // outfit-item based by design.)
        var donorOutfitGetter = ResolveFromModsOrWinner<IOutfitGetter>(donorNpc.DefaultOutfit, modKeys, modFolderPaths);
        var applicableWigs = new List<(FormKey Key, bool IsAntler)>();
        if (donorOutfitGetter?.Items != null)
        {
            foreach (var item in donorOutfitGetter.Items)
            {
                if (item == null || item.IsNull) continue;
                if (appearanceModSetting.DetectedAntlerArmors.Contains(item.FormKey))
                    applicableWigs.Add((item.FormKey, true));
                else if (appearanceModSetting.DetectedWigArmors.Contains(item.FormKey))
                    applicableWigs.Add((item.FormKey, false));
            }
        }

        if (applicableWigs.Count == 0) return null;

        if (mode == WigHandlingMode.ForwardToSkin)
        {
            var skinResult = TryForwardToSkin(donorNpc, appearanceModSetting, donorContextModKey, modFolderPaths,
                mergeInDependencyRecords, applicableWigs, npcIdentifier, appendLog);
            if (skinResult != null) return skinResult;
            appendLog($"      Wig handling: {npcIdentifier} has no WNAM in the appearance plugin — falling back to ForwardToOutfit.",
                false, false);
        }

        return TryForwardToOutfit(targetNpcFormKey, donorNpc, appearanceModSetting, donorContextModKey,
            modFolderPaths, mergeInDependencyRecords, applicableWigs, npcIdentifier, appendLog);
    }

    private Result? TryForwardToSkin(
        INpcGetter donorNpc,
        ModSetting appearanceModSetting,
        ModKey donorContextModKey,
        HashSet<string> modFolderPaths,
        bool mergeInDependencyRecords,
        List<(FormKey Key, bool IsAntler)> applicableWigs,
        string npcIdentifier,
        Action<string, bool, bool> appendLog)
    {
        if (donorNpc.WornArmor.IsNull) return null;
        var wnamKey = donorNpc.WornArmor.FormKey;
        var wnamGetter = ResolveFromModsOrWinner<IArmorGetter>(donorNpc.WornArmor,
            appearanceModSetting.CorrespondingModKeys, modFolderPaths);
        if (wnamGetter == null) return null;

        // Whether any applicable piece actually transfers a hair-slot ARMA —
        // computed from the wig set (not from what got appended) so the
        // duplicate-reuse path below reaches the same hair-removal decision.
        bool transfersHairSlot = applicableWigs.Any(w =>
            ResolveFromModsOrWinner<IArmorGetter>(w.Key.ToLink<IArmorGetter>(),
                appearanceModSetting.CorrespondingModKeys, modFolderPaths) is { } armo &&
            WigDetector.GetForwardableArmatures(armo, w.IsAntler,
                    fk => ResolveFromModsOrWinner<IArmorAddonGetter>(fk.ToLink<IArmorAddonGetter>(),
                        appearanceModSetting.CorrespondingModKeys, modFolderPaths))
                .Any(link => ResolveFromModsOrWinner<IArmorAddonGetter>(
                                 link.FormKey.ToLink<IArmorAddonGetter>(),
                                 appearanceModSetting.CorrespondingModKeys, modFolderPaths)
                             ?.BodyTemplate?.FirstPersonFlags is { } flags &&
                             (flags & BipedObjectFlag.Hair) != 0));

        string wigSetId = BuildWigSetId(applicableWigs);
        lock (_lock)
        {
            if (_skinDuplicates.TryGetValue((wnamKey, wigSetId), out var existing))
            {
                // Same WNAM + same wig set already forwarded for an earlier NPC of
                // this batch: reuse the duplicate. Re-seed so the mapping points at
                // it in case a different wig set overwrote the entry in between.
                // Hair removal is PER-NPC (each NPC has its own hair head parts),
                // so it is re-collected even on a duplicate-reuse hit.
                _recordHandler.SeedDuplicateMapping(wnamKey, existing);
                var reused = new Result { SkinDuplicateKey = existing };
                if (transfersHairSlot)
                {
                    CollectHairRemoval(donorNpc, appearanceModSetting, modFolderPaths, reused);
                }

                return reused;
            }
        }

        if (!Auxilliary.TryDuplicateGenericRecordAsNew(wnamGetter, _environmentStateProvider.OutputMod,
                out dynamic? dupDyn, out string exceptionString) || dupDyn is not Armor dup)
        {
            appendLog($"      Wig handling ERROR for {npcIdentifier}: could not duplicate WNAM {wnamKey}: {exceptionString}",
                true, true);
            return null;
        }

        // Keep the donor's EditorID (the duplicate IS the merged representation of
        // that WNAM — OutputValidator compares skins by resolved EditorID).
        dup.EditorID = wnamGetter.EditorID;

        var result = new Result { SkinDuplicateKey = dup.FormKey };
        result.MergedRecords.Add(dup);

        int appended = AppendForwardableArmatures(dup.Armature, applicableWigs, appearanceModSetting,
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

        // A skin-carried hair-slot wig does NOT suppress the NPC's head part
        // hair the way an equipped one does (user-verified in game: both
        // meshes render and clash), so when an actual hair-slot piece is
        // transferred, collect the donor's Hair-type head parts for removal —
        // from the record (FinalizeNpcRecord) and from the baked FaceGen NIF
        // (stripped post asset-copy). Antler-only forwarding transfers no
        // hair slot and keeps the real hair.
        if (transfersHairSlot)
        {
            CollectHairRemoval(donorNpc, appearanceModSetting, modFolderPaths, result);
        }

        _recordHandler.RecordMergedRecordOrigin(wnamKey, dup.FormKey, wnamGetter.EditorID);
        RecordProvenanceDiag.RecordMergedAsNew(wnamKey, wnamGetter.EditorID, "Armor", dup.FormKey, null);

        // Seed BEFORE the merge walkers run: the original WNAM is never
        // duplicated, and every reference to it redirects to the +Wig duplicate.
        _recordHandler.SeedDuplicateMapping(wnamKey, dup.FormKey);
        lock (_lock) { _skinDuplicates[(wnamKey, wigSetId)] = dup.FormKey; }

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

        appendLog($"      Wig handling (ForwardToSkin): duplicated WNAM {wnamKey} as {dup.FormKey} " +
                  $"and transferred {appended} wig/antler ArmorAddon(s) from {applicableWigs.Count} armor(s).",
            false, false);
        return result;
    }

    private Result? TryForwardToOutfit(
        FormKey targetNpcFormKey,
        INpcGetter donorNpc,
        ModSetting appearanceModSetting,
        ModKey donorContextModKey,
        HashSet<string> modFolderPaths,
        bool mergeInDependencyRecords,
        List<(FormKey Key, bool IsAntler)> applicableWigs,
        string npcIdentifier,
        Action<string, bool, bool> appendLog)
    {
        // The outfit the NPC will actually wear in game absent wig forwarding:
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

        // Already contains every applicable wig (e.g. the donor outfit IS the
        // effective outfit): nothing to do.
        if (effectiveOutfit?.Items != null)
        {
            var itemKeys = effectiveOutfit.Items.Where(i => i != null && !i.IsNull)
                .Select(i => i.FormKey).ToHashSet();
            if (applicableWigs.All(w => itemKeys.Contains(w.Key)))
            {
                return null;
            }
        }

        string wigSetId = BuildWigSetId(applicableWigs);
        FormKey sourceKey = display.OutfitFormKey ?? FormKey.Null;
        lock (_lock)
        {
            if (_outfitDuplicates.TryGetValue((sourceKey, wigSetId), out var existing))
            {
                return new Result { OutfitDuplicateKey = existing };
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
                return null;
            }

            dupOutfit = dup;
            dupOutfit.EditorID = effectiveOutfit.EditorID;
            _recordHandler.RecordMergedRecordOrigin(effectiveOutfit.FormKey, dupOutfit.FormKey, effectiveOutfit.EditorID);
            RecordProvenanceDiag.RecordMergedAsNew(effectiveOutfit.FormKey, effectiveOutfit.EditorID, "Outfit",
                dupOutfit.FormKey, null);
        }
        else
        {
            // No outfit anywhere in the stack: author a minimal wig-only outfit
            // so the wig still shows in game.
            dupOutfit = _environmentStateProvider.OutputMod.Outfits.AddNew();
            dupOutfit.EditorID = "NPC2_WigOutfit_" + (donorNpc.EditorID ?? donorNpc.FormKey.ToString().Replace(":", "_"));
            RecordProvenanceDiag.RecordGenerated(dupOutfit.FormKey, dupOutfit.EditorID, "Outfit");
        }

        var items = dupOutfit.Items ??= new ExtendedList<IFormLinkGetter<IOutfitTargetGetter>>();
        var existingItems = items.Where(i => i != null && !i.IsNull).Select(i => i.FormKey).ToHashSet();
        foreach (var (wigKey, _) in applicableWigs)
        {
            if (existingItems.Add(wigKey))
            {
                items.Add(wigKey.ToLink<IOutfitTargetGetter>());
            }
        }

        var result = new Result { OutfitDuplicateKey = dupOutfit.FormKey };
        result.MergedRecords.Add(dupOutfit);
        lock (_lock) { _outfitDuplicates[(sourceKey, wigSetId)] = dupOutfit.FormKey; }

        // Merge the donor-side chain (the wig ARMO + its armatures/textures —
        // and, for a mod-scoped source outfit, its other donor items). Items
        // owned by other load-order plugins are left as-is and become masters,
        // matching how the winning outfit's contents behave everywhere else.
        // Deliberately NO SeedDuplicateMapping for the source outfit: other
        // NPCs sharing it must not be redirected to the +Wig duplicate.
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

        appendLog($"      Wig handling (ForwardToOutfit): duplicated outfit " +
                  $"{(display.OutfitFormKey?.ToString() ?? "(none — authored new)")} as {dupOutfit.FormKey} " +
                  $"and added {applicableWigs.Count} wig/antler armor(s) " +
                  $"(effective outfit source: {display.Source}{(display.SourceDetail != null ? ", " + display.SourceDetail : "")}).",
            false, false);

        if (display.Source is OutfitDisplaySource.SkyPatcher or OutfitDisplaySource.Spid)
        {
            appendLog($"      WARNING: {npcIdentifier}'s outfit is distributed at runtime by " +
                      $"{display.Source} ({display.SourceDetail ?? "unknown config"}); the forwarded wig outfit " +
                      "may be overridden in game depending on distributor load order.", false, true);
        }

        return result;
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
            if (!string.IsNullOrEmpty(hpRec.EditorID)) result.HairShapeNames.Add(hpRec.EditorID);
            if (hpRec.ExtraParts != null)
            {
                foreach (var extraLink in hpRec.ExtraParts)
                {
                    if (extraLink == null || extraLink.IsNull) continue;
                    var extraRec = ResolveFromModsOrWinner<IHeadPartGetter>(extraLink,
                        appearanceModSetting.CorrespondingModKeys, modFolderPaths);
                    if (!string.IsNullOrEmpty(extraRec?.EditorID)) result.HairShapeNames.Add(extraRec.EditorID);
                }
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
