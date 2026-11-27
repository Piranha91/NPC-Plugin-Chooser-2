using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd.OutfitDistribution;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.BackEnd;

// Wig-and-headgear conflicts (see WigHeadgearChoice).
//
// "Forward to Outfit" adds the mod's wig to the outfit the NPC wears. When that outfit already
// holds headgear on the wig's biped slot the game shows only one of the two (in-game bench,
// 2026-11-25: Legate Rikke's twin wore the wig and no Imperial officer helmet). The user is told
// and chooses per NPC; the selection stays valid.
//
// This file holds the routing decision Apply and the pre-run scan share (which wig pieces would be
// added, and to which outfit), the glue to the pure record test in WigHeadgearConflictDetector,
// and the scan itself. Nothing here creates or changes a record.
public partial class WigForwarder
{
    /// <summary>
    /// What <see cref="Apply"/> will do with one NPC's wig and antler pieces, decided from records
    /// alone. Built by <see cref="DetermineRouting"/>.
    /// </summary>
    private sealed class Routing
    {
        public WigHandlingMode WigMode { get; init; }
        public AntlerHandlingMode AntlerMode { get; init; }

        /// <summary>The record whose Traits-governed appearance the OUTPUT will carry: the flatten
        /// terminus when there is one, else the donor.</summary>
        public INpcGetter AppearanceNpc { get; init; } = null!;

        /// <summary>The donor's own DefaultOutfit, as the mod supplies it.</summary>
        public IOutfitGetter? DonorOutfit { get; init; }

        /// <summary>Detected wig Armors among the donor outfit's direct items.</summary>
        public List<(FormKey Key, bool IsAntler)> WigItems { get; } = new();

        /// <summary>Detected antler Armors among the donor outfit's direct items.</summary>
        public List<(FormKey Key, bool IsAntler)> AntlerItems { get; } = new();

        public FormKey WnamKey { get; init; }

        /// <summary>The resolved WornArmor of <see cref="AppearanceNpc"/>, or null when it has none
        /// or the link does not resolve.</summary>
        public IArmorGetter? Wnam { get; init; }

        public bool HasWnam => Wnam != null;
        public bool WigToSkin { get; init; }
        public bool WigToOutfit { get; init; }
        public bool AntlerToSkin { get; init; }
        public bool AntlerToOutfit { get; init; }
        public bool AntlerRemove { get; init; }

        /// <summary>Skin-carried (WNAM) wig ArmorAddons that ForwardToOutfit relocates into the
        /// worn outfit, each through a minted wearable wig Armor. Empty in every other wig mode
        /// and when there is no resolvable WNAM.</summary>
        public List<IArmorAddonGetter> SkinWigArmasForOutfit { get; init; } = new();

        /// <summary>Whether any wig-class piece is headed for the worn outfit.</summary>
        public bool HasWigForOutfit => WigToOutfit || SkinWigArmasForOutfit.Count > 0;
    }

    /// <summary>
    /// The per-NPC routing decision at the top of <see cref="Apply"/>: which of the donor outfit's
    /// items are wig and antler pieces, which skin the output will wear, and where each class of
    /// piece goes under the given modes (including the no-WNAM fallback from ForwardToSkin to
    /// ForwardToOutfit). Reads records only.
    ///
    /// <para>Factored out so the pre-run headgear scan
    /// (<see cref="FindUnansweredHeadgearConflicts"/>) asks the same question the run answers,
    /// instead of holding a second copy of the rules.</para>
    /// </summary>
    private Routing DetermineRouting(INpcGetter donorNpc, ModSetting appearanceModSetting,
        HashSet<string> modFolderPaths, WigHandlingMode wigMode, AntlerHandlingMode antlerMode,
        INpcGetter? flattenTerminusNpc)
    {
        var modKeys = appearanceModSetting.CorrespondingModKeys;

        // The record whose Traits-governed appearance the OUTPUT will carry (see Apply's doc).
        var appearanceNpc = flattenTerminusNpc ?? donorNpc;

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

        // Resolve the WNAM up front: ForwardToSkin needs a resolvable WNAM
        // to transfer into. hasWnam = present AND resolvable so the no-WNAM
        // fallback (→ ForwardToOutfit) also covers an unresolvable link.
        // Traits-governed, so this is the terminus's skin under a flatten — the same one
        // CopyInheritedAppearance writes, which the duplicate then replaces.
        var wnamKey = appearanceNpc.WornArmor.IsNull ? FormKey.Null : appearanceNpc.WornArmor.FormKey;
        var wnamGetter = appearanceNpc.WornArmor.IsNull
            ? null
            : ResolveFromModsOrWinner<IArmorGetter>(appearanceNpc.WornArmor, modKeys, modFolderPaths);
        bool hasWnam = wnamGetter != null;

        // Skin-carried (WNAM) wigs under ForwardToOutfit: each effective wig ARMA
        // (IsWigArmature — scan minus vetoes plus manual designations) is relocated
        // into the worn outfit.
        var skinWigArmasForOutfit =
            wigMode == WigHandlingMode.ForwardToOutfit && wnamGetter?.Armature != null
                ? CollectWnamWigArmas(wnamGetter, donorNpc, appearanceModSetting, modFolderPaths, hairSlotOnly: false)
                : new List<IArmorAddonGetter>();

        var routing = new Routing
        {
            WigMode = wigMode,
            AntlerMode = antlerMode,
            AppearanceNpc = appearanceNpc,
            DonorOutfit = donorOutfitGetter,
            WnamKey = wnamKey,
            Wnam = wnamGetter,

            // Per-class effective routing (the no-WNAM fallback flips ForwardToSkin →
            // ForwardToOutfit for both classes, since they share the one donor WNAM).
            WigToSkin = wigMode == WigHandlingMode.ForwardToSkin && hasWnam && wigItems.Count > 0,
            WigToOutfit = wigItems.Count > 0 &&
                          (wigMode == WigHandlingMode.ForwardToOutfit ||
                           (wigMode == WigHandlingMode.ForwardToSkin && !hasWnam)),
            AntlerToSkin = antlerMode == AntlerHandlingMode.ForwardToSkin && hasWnam && antlerItems.Count > 0,
            AntlerToOutfit = antlerItems.Count > 0 &&
                             (antlerMode == AntlerHandlingMode.ForwardToOutfit ||
                              (antlerMode == AntlerHandlingMode.ForwardToSkin && !hasWnam)),
            AntlerRemove = antlerMode == AntlerHandlingMode.Remove,
            SkinWigArmasForOutfit = skinWigArmasForOutfit,
        };
        routing.WigItems.AddRange(wigItems);
        routing.AntlerItems.AddRange(antlerItems);
        return routing;
    }

    /// <summary>The outfit forwarded pieces are added to: what the display resolver says the NPC
    /// wears, and that outfit's record (null when the NPC wears none, or it does not resolve).</summary>
    private sealed record ForwardTarget(OutfitDisplayResult Display, IOutfitGetter? EffectiveOutfit);

    /// <summary>
    /// The outfit the NPC will actually wear in game absent forwarding: plugin-level effective
    /// outfit (patch-mode aware) + SkyPatcher/SPID runtime layers — the same simulation the
    /// renderer depicts. This is the outfit <see cref="TryForwardToOutfit"/> duplicates and adds
    /// the forwarded pieces to; the pre-run headgear scan resolves it through this same method.
    /// </summary>
    private ForwardTarget ResolveForwardTarget(FormKey targetNpcFormKey, INpcGetter donorNpc,
        ModSetting appearanceModSetting, HashSet<string> modFolderPaths)
    {
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

        return new ForwardTarget(display, effectiveOutfit);
    }

    /// <summary>
    /// The wig pieces <paramref name="routing"/> would ADD to the outfit in
    /// <paramref name="target"/>, tested against what that outfit already holds. Empty unless the
    /// wig mode is ForwardToOutfit. A wig Armor the outfit already carries is not added by the
    /// forwarder, so it is not tested; a skin-carried wig ArmorAddon is always added (through a
    /// wearable Armor minted with the ArmorAddon's own slots), so it is described by the
    /// ArmorAddon, which exists before the run does.
    /// </summary>
    private IReadOnlyList<WigHeadgearConflict> FindHeadgearConflicts(Routing routing, ForwardTarget target,
        ModSetting appearanceModSetting, HashSet<string> modFolderPaths)
    {
        if (routing.WigMode != WigHandlingMode.ForwardToOutfit) return Array.Empty<WigHeadgearConflict>();

        var outfit = target.EffectiveOutfit;
        if (outfit?.Items == null) return Array.Empty<WigHeadgearConflict>();

        var modKeys = appearanceModSetting.CorrespondingModKeys;
        var alreadyWorn = outfit.Items.Where(i => i != null && !i.IsNull).Select(i => i.FormKey).ToHashSet();

        var pieces = new List<WigHeadgearConflictDetector.WigPiece>();
        if (routing.WigToOutfit)
        {
            foreach (var (wigKey, _) in routing.WigItems)
            {
                if (alreadyWorn.Contains(wigKey)) continue;
                var wigArmor = ResolveFromModsOrWinner<IArmorGetter>(wigKey.ToLink<IArmorGetter>(), modKeys, modFolderPaths);
                if (wigArmor != null) pieces.Add(WigHeadgearConflictDetector.WigPiece.From(wigArmor));
            }
        }

        foreach (var arma in routing.SkinWigArmasForOutfit)
        {
            // Same mask GetOrMintWigArmorForArma gives the wearable armor it wraps the addon in.
            pieces.Add(new WigHeadgearConflictDetector.WigPiece(arma.FormKey, arma.EditorID, null,
                arma.BodyTemplate?.FirstPersonFlags ?? WigDetector.HairSlots));
        }

        if (pieces.Count == 0) return Array.Empty<WigHeadgearConflict>();

        // The outfit's items are resolved the way the outfit itself was: through the mod's own
        // plugins first when the outfit is the mod's, else as the load order has them. Typed
        // lookups (Armor, then LeveledItem), never an untyped one.
        bool modScoped = target.Display.UseModScopedResolution;
        IMajorRecordGetter? ResolveItem(FormKey itemKey)
        {
            if (modScoped)
            {
                return (IMajorRecordGetter?)ResolveFromModsOrWinner<IArmorGetter>(
                           itemKey.ToLink<IArmorGetter>(), modKeys, modFolderPaths)
                       ?? ResolveFromModsOrWinner<ILeveledItemGetter>(
                           itemKey.ToLink<ILeveledItemGetter>(), modKeys, modFolderPaths);
            }

            return (IMajorRecordGetter?)ResolveWinner<IArmorGetter>(itemKey)
                   ?? ResolveWinner<ILeveledItemGetter>(itemKey);
        }

        return WigHeadgearConflictDetector.Find(pieces, outfit, ResolveItem);
    }

    /// <summary>
    /// Would forwarding this NPC's wig to its outfit put the wig on a biped slot the outfit's
    /// headgear already uses? Uses the very routing decision and outfit resolution
    /// <see cref="Apply"/> uses, and creates nothing. Empty when the mod's effective wig mode is
    /// not ForwardToOutfit, when the NPC has no wig to forward, or when nothing overlaps.
    ///
    /// <para>Whether the user already answered for this NPC is NOT consulted here; that is the
    /// caller's question (<see cref="FindUnansweredHeadgearConflicts"/> skips answered NPCs, the
    /// Patcher asks only about NPCs with a stored "convert" answer).</para>
    ///
    /// <para>Never throws for a record it cannot read: the test only informs, so a failure counts
    /// as "no conflict found" (both callers then do what they would have done without it).</para>
    /// </summary>
    /// <param name="targetNpcFormKey">The NPC being patched.</param>
    /// <param name="donorNpc">The appearance record the selected mod supplies for it.</param>
    /// <param name="flattenTerminusNpc">The Traits-chain terminus when the appearance is being
    /// flattened onto the NPC (its WornArmor is then the skin the output wears), else null.</param>
    public IReadOnlyList<WigHeadgearConflict> FindHeadgearConflicts(FormKey targetNpcFormKey, INpcGetter donorNpc,
        ModSetting appearanceModSetting, HashSet<string> modFolderPaths, INpcGetter? flattenTerminusNpc = null)
    {
        var wigMode = _settings.GetEffectiveWigMode(appearanceModSetting);
        if (wigMode != WigHandlingMode.ForwardToOutfit) return Array.Empty<WigHeadgearConflict>();

        try
        {
            var routing = DetermineRouting(donorNpc, appearanceModSetting, modFolderPaths, wigMode,
                _settings.GetEffectiveAntlerMode(appearanceModSetting), flattenTerminusNpc);
            if (!routing.HasWigForOutfit) return Array.Empty<WigHeadgearConflict>();

            var target = ResolveForwardTarget(targetNpcFormKey, donorNpc, appearanceModSetting, modFolderPaths);
            return FindHeadgearConflicts(routing, target, appearanceModSetting, modFolderPaths);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"WigForwarder.FindHeadgearConflicts: {targetNpcFormKey} could not be checked: {ex.Message}");
            return Array.Empty<WigHeadgearConflict>();
        }
    }

    /// <summary>Records the batched end-of-run warning for one NPC whose wig was forwarded into an
    /// outfit that holds headgear on the same slot.</summary>
    private static void ReportWigForwardedOntoHeadgear(IReadOnlyList<WigHeadgearConflict> conflicts,
        ForwardTarget? target, ModSetting appearanceModSetting, string npcIdentifier)
    {
        // "label: k=v, k=v" rows sharing keys render as one table in the detailed log
        // (NpcWarningReporter.ClassifyTechnicalLines); slot lists are space-separated because a
        // comma would end the field.
        static string Row(string label, FormKey formKey, string? editorId, string slotsKey, IReadOnlyList<int> slots) =>
            $"{label}: record={formKey}, editorId={(string.IsNullOrWhiteSpace(editorId) ? "(none)" : editorId)}, " +
            $"{slotsKey}={string.Join(" ", slots)}";

        var technical = new List<string>();
        foreach (var conflict in conflicts)
        {
            technical.Add(Row("wig", conflict.Wig.FormKey, conflict.Wig.EditorID, "slots", conflict.Wig.Slots));
            technical.Add(Row("headgear", conflict.Headgear.FormKey, conflict.Headgear.EditorID, "slots", conflict.Headgear.Slots));
            technical.Add(Row("outfit", conflict.OutfitFormKey, conflict.OutfitEditorID, "sharedSlots", conflict.SharedSlots));
        }

        if (target != null)
        {
            technical.Add($"outfit source: {target.Display.Source}" +
                          (target.Display.SourceDetail != null ? " " + target.Display.SourceDetail : string.Empty));
        }

        NpcWarningReporter.Record(NpcWarningKind.WigForwardedOntoHeadgear, npcIdentifier,
            detail: string.Join("; ", conflicts.Select(c => c.Describe())),
            technicalDetail: string.Join("\n", technical),
            modName: appearanceModSetting.DisplayName);
    }

    /// <summary>
    /// The pre-run question: among the screened, valid selections, which NPCs would have a wig
    /// forwarded into an outfit that holds headgear on the same biped slot, and have no stored
    /// answer yet (<see cref="Settings.WigHeadgearConflictChoices"/>)? One case per NPC, in the
    /// order given; grouping for display is the caller's.
    ///
    /// <para>Side-effect free: it reads records and creates or changes none, in the output mod or
    /// anywhere else. Returns at once, having resolved nothing, when no selected mod's effective
    /// wig mode is ForwardToOutfit, so a run that does not forward wigs to outfits pays nothing.</para>
    ///
    /// <para>It asks <see cref="FindHeadgearConflicts(FormKey, INpcGetter, ModSetting, HashSet{string}, INpcGetter)"/>,
    /// the same determination <see cref="Apply"/> makes during the run. What it has to work out for
    /// itself is what the Patcher hands Apply: the donor record (the mod's own record for the NPC;
    /// an NPC the mod only ships FaceGen for gets no wig handling and is skipped), and the
    /// Traits-chain terminus when the mod's template handling flattens. An NPC whose outfit field
    /// is inert is skipped too: the run converts its wig to head parts and forwards nothing.</para>
    ///
    /// <para>Differences from the run that can make the scan report a case the run does not act
    /// on, or the reverse: the scan resolves records through the mod's own folders only (the run
    /// also adds the folders of merge-eligible plugins installed as separate mods), and it does
    /// not know which NPCs the run will later skip for lack of a usable face. A headless run shows
    /// no window, so its unanswered cases are forwarded and listed in the end-of-run warnings.</para>
    /// </summary>
    /// <param name="validSelections">Target NPC to screening result, as
    /// <c>Validator.GetScreeningCache()</c> holds them. Invalid entries are ignored.</param>
    public IReadOnlyList<WigHeadgearConflictCase> FindUnansweredHeadgearConflicts(
        IEnumerable<KeyValuePair<FormKey, ScreeningResult>> validSelections,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<KeyValuePair<FormKey, ScreeningResult>>();
        foreach (var selection in validSelections)
        {
            var screening = selection.Value;
            if (screening == null || !screening.SelectionIsValid || screening.AppearanceModSetting == null) continue;
            if (_settings.GetEffectiveWigMode(screening.AppearanceModSetting) != WigHandlingMode.ForwardToOutfit) continue;
            candidates.Add(selection);
        }

        if (candidates.Count == 0) return Array.Empty<WigHeadgearConflictCase>();

        var linkCache = _environmentStateProvider.LinkCache;
        if (linkCache == null) return Array.Empty<WigHeadgearConflictCase>();

        var cases = new List<WigHeadgearConflictCase>();
        var folderPathsByMod = new Dictionary<ModSetting, HashSet<string>>();

        foreach (var (targetNpcFormKey, screening) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Answered already, whatever the answer.
            if (_settings.GetWigHeadgearChoice(targetNpcFormKey) != null) continue;

            var modSetting = screening.AppearanceModSetting;
            if (!folderPathsByMod.TryGetValue(modSetting, out var folderPaths))
            {
                folderPaths = modSetting.CorrespondingFolderPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                folderPathsByMod[modSetting] = folderPaths;
            }

            IReadOnlyList<WigHeadgearConflict> conflicts;
            try
            {
                conflicts = FindHeadgearConflictsForSelectionCore(targetNpcFormKey,
                    screening.AppearanceNpcFormKey, modSetting, screening.WinningNpcOverride, folderPaths, linkCache);
            }
            catch (Exception ex)
            {
                // A selection the scan cannot read is not a reason to stop the run from starting;
                // the run reports its own problems with that NPC.
                System.Diagnostics.Debug.WriteLine(
                    $"WigForwarder.FindUnansweredHeadgearConflicts: {targetNpcFormKey} could not be checked: {ex.Message}");
                continue;
            }

            if (conflicts.Count == 0) continue;

            var displayRecord = screening.WinningNpcOverride;
            string display = displayRecord != null
                ? Auxilliary.GetLogString(displayRecord, _settings.LocalizationLanguage).TrimEnd(' ', '|')
                : targetNpcFormKey.ToString();

            cases.Add(new WigHeadgearConflictCase(targetNpcFormKey, display, modSetting.DisplayName, conflicts));
        }

        return cases;
    }

    /// <summary>
    /// One selection's part of the pre-run scan: the conflicts the run would meet for this NPC,
    /// worked out the way <see cref="FindUnansweredHeadgearConflicts"/> documents (donor from the
    /// mod's own plugins, inert outfit field, flatten terminus). Empty when there is nothing to
    /// ask about. Does not look at stored answers and does not catch: the callers decide what a
    /// record that cannot be read means for them.
    /// </summary>
    /// <param name="winningNpcOverride">The NPC as the load order has it.</param>
    private IReadOnlyList<WigHeadgearConflict> FindHeadgearConflictsForSelectionCore(
        FormKey targetNpcFormKey, FormKey appearanceNpcFormKey, ModSetting modSetting,
        INpcGetter? winningNpcOverride, HashSet<string> folderPaths,
        Mutagen.Bethesda.Plugins.Cache.ILinkCache linkCache)
    {
        // The donor as the patch loop resolves it. No record in the mod's own plugins means
        // a FaceGen-only selection, for which the run does no wig handling at all.
        var donorNpc = _recordHandler.ResolveNpcFromModPlugins(appearanceNpcFormKey, modSetting, folderPaths);
        if (donorNpc == null) return Array.Empty<WigHeadgearConflict>();

        // The run converts instead of forwarding when the outfit field it writes is inert
        // (Patcher.RecordOutfitIsInert picks the same record), so there is no forward to
        // ask about.
        var writtenRecord = _settings.PatchingMode == PatchingMode.CreateAndPatch
            ? winningNpcOverride
            : donorNpc;
        if (_settings.OutfitFieldIsInert(writtenRecord)) return Array.Empty<WigHeadgearConflict>();

        return FindHeadgearConflicts(targetNpcFormKey, donorNpc, modSetting, folderPaths,
            ResolveFlattenTerminusForScan(donorNpc, modSetting, folderPaths, linkCache));
    }

    /// <summary>
    /// The same question as <see cref="FindUnansweredHeadgearConflicts"/>, for ONE selection and
    /// without a screening pass: would forwarding the wig of <paramref name="targetNpcFormKey"/>,
    /// as <paramref name="appearanceModSetting"/> supplies it, put the wig on a slot the NPC's
    /// outfit already uses for headgear? For the NPC list, which marks such a row.
    ///
    /// <para>Read-only, and it differs from the scan in two ways only: it takes the NPC's winning
    /// record from the load order itself instead of from a <see cref="ScreeningResult"/>, and it
    /// does NOT skip an NPC with a stored answer (the list still marks a stored "forward").
    /// Whether the selection would pass screening is not judged here; the caller asks only about
    /// rows it shows as valid. Resolves nothing and returns empty when the mod's effective wig
    /// mode is not ForwardToOutfit. Never throws for a record it cannot read (empty instead).</para>
    ///
    /// <para>Resolves records and reads plugin files: call it off the UI thread.</para>
    /// </summary>
    /// <param name="targetNpcFormKey">The NPC the selection is for.</param>
    /// <param name="appearanceNpcFormKey">The NPC whose appearance was selected for it: the same
    /// key, except for a guest appearance.</param>
    public IReadOnlyList<WigHeadgearConflict> FindHeadgearConflictsForSelection(
        FormKey targetNpcFormKey, FormKey appearanceNpcFormKey, ModSetting? appearanceModSetting)
    {
        if (appearanceModSetting == null) return Array.Empty<WigHeadgearConflict>();
        if (_settings.GetEffectiveWigMode(appearanceModSetting) != WigHandlingMode.ForwardToOutfit)
        {
            return Array.Empty<WigHeadgearConflict>();
        }

        var linkCache = _environmentStateProvider.LinkCache;
        if (linkCache == null) return Array.Empty<WigHeadgearConflict>();

        try
        {
            if (!linkCache.TryResolve<INpcGetter>(targetNpcFormKey, out var winningNpcOverride) ||
                winningNpcOverride == null)
            {
                return Array.Empty<WigHeadgearConflict>();
            }

            var folderPaths = appearanceModSetting.CorrespondingFolderPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return FindHeadgearConflictsForSelectionCore(targetNpcFormKey, appearanceNpcFormKey,
                appearanceModSetting, winningNpcOverride, folderPaths, linkCache);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"WigForwarder.FindHeadgearConflictsForSelection: {targetNpcFormKey} could not be checked: {ex.Message}");
            return Array.Empty<WigHeadgearConflict>();
        }
    }

    /// <summary>
    /// The record whose appearance the run would flatten onto the NPC, as
    /// <c>Patcher.ResolveAppearanceTerminusRecord</c> decides it: only when the mod's effective
    /// template handling gives each NPC its own copy AND the donor's Traits chain resolves to a
    /// concrete NPC; null otherwise. The run reads the chain status off its FaceGen decision; the
    /// walk behind that status is this one, with the same resolvers.
    /// </summary>
    private INpcGetter? ResolveFlattenTerminusForScan(INpcGetter donorNpc, ModSetting modSetting,
        HashSet<string> folderPaths, Mutagen.Bethesda.Plugins.Cache.ILinkCache linkCache)
    {
        if (_settings.GetEffectiveTemplateHandlingMode(modSetting) != TemplateHandlingMode.GiveEachNpcOwnCopy)
        {
            return null;
        }

        var status = Auxilliary.TryResolveAppearanceTerminus(
            donorNpc,
            fk => _recordHandler.ResolveNpcPreferringMod(fk, modSetting, folderPaths, isFaceGenOnly: false),
            out var terminusKey,
            fk => linkCache.TryResolve<ILeveledNpcGetter>(fk, out _));
        if (status != FaceGenChainStatus.Resolved) return null;

        return _recordHandler.ResolveNpcPreferringMod(terminusKey, modSetting, folderPaths, isFaceGenOnly: false);
    }
}
